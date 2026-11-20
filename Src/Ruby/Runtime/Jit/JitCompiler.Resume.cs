/* ****************************************************************************
 *
 * -X:JITResume - the compiler's half of stage 1 of Docs/jit-resume-deopt.md (the runtime's is
 * JitResume.cs): an attribute read or write on another object, after the body's first effect,
 * falls back to its own call when its guard fails, and the typed body goes on.
 *
 * The rule that keeps it sound (the design's "self-guarded tail"): once a fallback has been
 * emitted, the call it makes may have changed anything - redefined a method, frozen an object,
 * enabled a TracePoint - so every node whose code is emitted after it must either speculate on
 * nothing (a literal, self, an instance variable read) or be a self-guarded attribute operation,
 * which re-checks the class, the method version, frozen-ness and tracing on every run. Any other
 * node bails (IsTailNode); a deopt after the first effect still declines the body, as without
 * the switch, so no typed operation follows the first effect either.
 *
 * Stage 1 takes only bodies with no parameters and no locals (IsResumeCandidate): the scope a
 * fallback may make never has to be kept current with typed locals.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    internal sealed partial class JitCompiler {
        // The body may fall back (JitStub.ResumeInfo); null without -X:JITResume or for a body stage 1 does not take.
        private JitResumeInfo _resume;
        // The invocation's frame (JitFrame, null until a fallback or a hook makes it) and the block the body takes.
        private MSA.ParameterExpression _frameVariable, _blockVariable;
        // How many fallbacks the body has; once there is one, every node emitted after it is checked (IsTailNode).
        private int _fallbackSites;
        // The attribute operations emitted self-guarded, which IsTailNode lets through.
        private HashSet<RExpr> _selfGuarded;
        // The line of the top-level statement being emitted (a fallback's frame line), and of the one before it.
        private int _statementLine, _previousLine;

        /// <summary>
        /// A method stage 1 may give fallbacks: no parameters of any kind and no local variable anywhere in the body, so the
        /// scope a fallback makes holds nothing the typed body keeps elsewhere.
        /// </summary>
        internal static bool IsResumeCandidate(MethodDeclaration/*!*/ ast) {
            var parameters = ast.Parameters;
            if (parameters.Mandatory.Length != 0 || parameters.Optional.Length != 0 || parameters.Unsplat != null ||
                parameters.Block != null || parameters.SuperSplat != null) {
                return false;
            }
            var signature = parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.MaxPositionalCount != 0)) {
                return false;
            }
            var walker = new LocalFinder();
            walker.Walk(ast.Body);
            return !walker.Found;
        }

        private sealed class LocalFinder : Walker {
            internal bool Found;

            public override bool Enter(LocalVariable/*!*/ node) {
                Found = true;
                return false;
            }
        }

        /// <summary>
        /// A top-level statement of a body that may fall back: after the first fallback it carries the generic body's
        /// :line hook (TraceLineExpression's: once per line, behind a test of TracePoint.ActiveEvents), on the scope made
        /// for it on demand. The first fallback's own statement needs none: nothing ran Ruby code before it.
        /// </summary>
        private MSA.Expression/*!*/ EmitTopStatement(Statements/*!*/ statements, int index, out JT type) {
            if (_resume == null) { return Emit(statements[index], out type); }
            int line = statements.GetStartLine(index);
            bool hook = _fallbackSites > 0 && _resume.Traceable && line > 0 && line != _previousLine &&
                !AstGenerator.IsTransparentStatement(statements[index]);
            _previousLine = _statementLine = line;
            var e = Emit(statements[index], out type);
            if (!hook) { return e; }
            return Ast.Block(e.Type,
                Ast.IfThen(TraceGuardInstruction.MakeTest(TraceEvents.Line), Slow(JitRuntime.SlowLine, Ast.Constant(line, typeof(object)))),
                e
            );
        }

        /// <summary>
        /// After a fallback, only what speculates on nothing - a literal, self, an instance variable read - and the
        /// self-guarded attribute operations may have code emitted (see the top of this file).
        /// </summary>
        private bool IsTailNode(RExpr/*!*/ node) {
            if (node is Literal || node is SymbolLiteral || node is SelfReference || node is InstanceVariable) { return true; }
            return _selfGuarded != null && _selfGuarded.Contains(node);
        }

        /// <summary>
        /// Whether an attribute operation on <paramref name="target"/> may fall back: a body that may, after its first
        /// effect, on an explicit receiver other than self (whose call can find only a public method).
        /// </summary>
        private bool CanFallBack(RExpr target) {
            return _resume != null && !_pure && target != null && !(target is SelfReference);
        }

        private void FallbackEmitted(RExpr/*!*/ node) {
            if (_frameVariable == null) {
                _frameVariable = Ast.Variable(typeof(JitFrame), "#frame");
                _blockVariable = Ast.Parameter(typeof(Proc), "#block");
            }
            _fallbackSites++;
            if (_selfGuarded == null) { _selfGuarded = new HashSet<RExpr>(); }
            _selfGuarded.Add(node);
        }

        /// <summary>`o.x' after the first effect: the read if TryReadAttr can make it, the call otherwise (FallbackRead).</summary>
        private MSA.Expression/*!*/ EmitFallbackRead(MethodCall/*!*/ node, MSA.Expression/*!*/ receiver, string/*!*/ variableName) {
            FallbackEmitted(node);
            var receiverTemp = Ast.Variable(typeof(object), "#receiver");
            var value = Ast.Variable(typeof(object), "#read");
            var site = new JitFallbackSite(_context, node.MethodName, false, _statementLine);
            return Ast.Block(typeof(object), new[] { receiverTemp, value },
                Ast.Assign(receiverTemp, receiver),
                Ast.Assign(value, Ast.Call(JitRuntime.M("TryReadAttr"), receiverTemp,
                    Ast.Constant(new JitRuntime.SelfAttrSite(node.MethodName, variableName, true)))),
                Ast.IfThen(Ast.ReferenceEqual(value, Ast.Field(null, typeof(JitRuntime).GetField("Missed"))),
                    Ast.Assign(value, Slow(JitRuntime.SlowRead, Ast.Constant(site, typeof(object)), receiverTemp))),
                value
            );
        }

        /// <summary>
        /// `o.x = v' after the first effect, its operands evaluated into <paramref name="receiver"/> and <paramref name="boxed"/>:
        /// the store if TryWriteAttr can make it, the call otherwise (FallbackWrite).
        /// </summary>
        private MSA.Expression/*!*/ EmitFallbackWrite(SimpleAssignmentExpression/*!*/ node, string/*!*/ name, MSA.Expression/*!*/ receiver,
            MSA.Expression/*!*/ boxed, string/*!*/ variableName) {
            FallbackEmitted(node);
            var site = new JitFallbackSite(_context, name, true, _statementLine);
            return Ast.IfThen(
                Ast.Not(Ast.Call(JitRuntime.M("TryWriteAttr"), Ast.Constant(_context), receiver, boxed,
                    Ast.Constant(new JitRuntime.SelfAttrSite(name, variableName, true, true)))),
                Slow(JitRuntime.SlowWrite, Ast.Constant(site, typeof(object)), receiver, boxed)
            );
        }

        // The invocation's frame, made if it has not been, then the call or the hook (JitRuntime.ResumeSlow).
        private MSA.Expression/*!*/ Slow(int kind, MSA.Expression/*!*/ site, MSA.Expression a = null, MSA.Expression b = null) {
            var nil = Ast.Constant(null, typeof(object));
            return Ast.Block(
                Ast.Assign(_frameVariable, Ast.Call(JitRuntime.M("ResumeFrame"), _frameVariable, Ast.Constant(_resume), _self, _blockVariable)),
                Ast.Call(JitRuntime.M("ResumeSlow"), Ast.Constant(kind), _frameVariable, site, a ?? nil, b ?? nil)
            );
        }

        /// <summary>
        /// Why a body with fallbacks cannot have them, or null: what stage 1 leaves out. Each of these is also
        /// something the frame would have to know about (a loop's safe point, the undo log, a self-call's nesting).
        /// </summary>
        private string ResumeObstacle() {
            if (_selfCall) { return "a self-call"; }
            if (_hasLoop || _calleeLoop) { return "a loop"; }
            if (_undoLog != null || _hashWrite || _unloggedWrite || _arrayLoads != null) { return "element writes or loaded Arrays"; }
            if (_construction) { return "a construction"; }
            if (_returnLabel != null) { return "an explicit return"; }
            return null;
        }

        /// <summary>
        /// The body of a method with fallbacks, as a light frame's (MethodDefinition.TransformBody): the try whose handler
        /// takes a MethodUnwinder aimed at this invocation's frame and returns its value, and whose finally leaves the
        /// frame, if one was made; then the :return hook of falling off the end. Its value is boxed.
        /// </summary>
        private MSA.Expression/*!*/ WrapResumeBody(MSA.Expression/*!*/ body, JT type) {
            var result = Ast.Variable(typeof(object), "#result");
            var unwinder = Ast.Variable(typeof(MethodUnwinder), "#u");
            MSA.Expression run = Ast.Assign(result, Coerce(body, type, JT.Obj));
            if (_resume.Traceable) {
                run = Ast.Block(
                    run,
                    Ast.IfThen(TraceGuardInstruction.MakeTest(TraceEvents.Return), Slow(JitRuntime.SlowReturn, Ast.Constant(null, typeof(object)), result))
                );
            }
            return Ast.Block(typeof(object), new[] { _frameVariable, result },
                Ast.TryCatchFinally(
                    Ast.Block(typeof(void), run),
                    Ast.Call(JitRuntime.M("ResumeLeave"), _frameVariable),
                    Ast.Catch(unwinder, Ast.IfThenElse(
                        Ast.Call(JitRuntime.M("ResumeIsTarget"), _frameVariable, unwinder),
                        Ast.Assign(result, Ast.Call(JitRuntime.M("ResumeReturnValue"), unwinder)),
                        Ast.Rethrow()
                    ))
                ),
                result
            );
        }
    }
}
