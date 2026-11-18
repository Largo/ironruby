/* ****************************************************************************
 *
 * -X:JITResume, stage 1 of Docs/jit-resume-deopt.md: a body declined for a deopt after an
 * effect is compiled again with the attribute operations after its first effect self-guarded
 * (JitRuntime.TryReadAttr and its kin): where the guard fails the operation is the call the
 * source makes (JitRuntime.ResumeFallback), and the body goes on. Nothing restarts, so nothing
 * has to be invisible.
 *
 * A fallback runs Ruby code in the middle of the body. So what the body does after its first
 * effect may not rely on anything the entry checked on the way in - the method version, that
 * nothing is traced - nor on self staying unfrozen: only instance variable reads, literals,
 * self and the self-guarded operations themselves are emitted there (EmitTail), each as a
 * statement of the method's own statement list. And nothing emitted before a fallback may run
 * after it, so such a body has no loop and does not call itself. Stage 1 takes only
 *
 *   - a method without parameters, locals, a rescue or a refinement record (JitStub.ResumeBody):
 *     the scope a callee may ask for is then the method's self and block and nothing else, and
 *     never has to be kept current with typed locals;
 *   - whose statements are a flat list: no `if', `while', `case', `return' or `(a; b)' anywhere
 *     in it. The :line hooks of the statements after the first fallback are then the generic
 *     body's by construction (EmitResumeStatements);
 *   - with no element write, global write, construction or bound callee.
 *
 * Everything else is declined as before. The typed body takes the method's block as a second
 * parameter: a callee that reads the scope may reach it (`binding.eval("yield")').
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;

    internal sealed partial class JitCompiler {
        // Set for the second compilation of a body that was declined for a deopt after an effect.
        private JitRuntime.ResumeBody _resume;
        // The method's block, and what the invocation has once it has fallen back (null until then).
        private MSA.ParameterExpression _block, _resumeFrame;
        // How many self-guarded operations have been emitted. Once there is one, Ruby code may have run.
        private int _fallbackSites;
        // The statement being emitted is a statement of the method that starts after the first effect:
        // its attribute operations are self-guarded, and nothing else speculative is emitted in it.
        private bool _tailStatement;
        // Its line, which the frame has while one of its operations falls back.
        private int _statementLine;

        /// <summary>
        /// The statements of a body compiled with fallbacks. Each statement of the method is flat (Emit
        /// refuses the others), so the generic body's :line hooks are one per statement that starts a new
        /// line (AstGenerator.TraceLine); the statements after the first self-guarded operation carry
        /// theirs, since that operation's callee may have enabled a TracePoint.
        /// </summary>
        private MSA.Expression/*!*/ EmitResumeStatements(Statements statements, out JT type) {
            _block = Ast.Parameter(typeof(Proc), "#block");
            _resumeFrame = Ast.Variable(typeof(JitRuntime.ResumeFrame), "#resume");
            type = JT.Obj;
            if (statements == null || statements.Count == 0) {
                return Ast.Constant(null, typeof(object));
            }
            var parts = new List<MSA.Expression>(statements.Count + 2);
            int traced = 0;
            for (int i = 0; i < statements.Count; i++) {
                int line = statements.GetStartLine(i);
                if (line > 0 && line != traced) {
                    traced = line;
                    if (_fallbackSites > 0) {
                        parts.Add(Ast.IfThen(
                            Tracing(TraceEvents.Line),
                            Ast.Call(typeof(RubyOps).GetMethod("TraceLineEvent"), ResumeScope(), Ast.Constant(_resume.SourcePath, typeof(string)), Ast.Constant(line))
                        ));
                    }
                }
                _tailStatement = !_pure;
                _statementLine = statements[i].Location.Start.Line;
                parts.Add(Emit(statements[i], out type));
                _tailStatement = false;
            }
            if (type == JT.None || type == JT.Never) { throw Bail("a body with fallbacks that has no value"); }
            return (parts.Count == 1) ? parts[0] : Ast.Block(ClrType(type), parts);
        }

        /// <summary>
        /// A node of a statement that starts after the first effect, other than a literal, self or an
        /// instance variable read: `o.x = v' and `o.x' self-guarded, `@x = v' of a plain value while no
        /// fallback has been emitted (after one, self may have been frozen, and the write deopts on that).
        /// </summary>
        private MSA.Expression/*!*/ EmitTail(RExpr/*!*/ node, out JT type) {
            var assign = node as SimpleAssignmentExpression;
            if (assign != null && assign.Operation == null) {
                var attribute = assign.Left as AttributeAccess;
                if (attribute != null) { return EmitMethodAttrWrite(attribute, null, assign.Right, out type); }
                if (assign.Left is InstanceVariable && _fallbackSites == 0 && IsTailLeaf(assign.Right)) { return EmitAssignment(assign, out type); }
            }
            var call = node as MethodCall;
            if (call != null && call.Block == null && (call.Arguments == null || call.Arguments.IsEmpty)) {
                var read = EmitMethodAttrRead(call, out type);
                if (read != null) { return read; }
            }
            throw Bail("after the first effect only attribute reads and writes are taken: " + node.GetType().Name +
                ((call != null) ? " " + call.MethodName : ""));
        }

        private static bool IsTailLeaf(RExpr/*!*/ node) {
            return node is Literal || node is SymbolLiteral || node is InstanceVariable || node is SelfReference;
        }

        private MSA.Expression/*!*/ Tracing(TraceEvents events) {
            return Ast.NotEqual(
                Ast.And(Ast.Field(null, typeof(TracePoint).GetField("ActiveEvents")), Ast.Constant((int)events)),
                Ast.Constant(0));
        }

        // The invocation's ResumeFrame, made by the first operation that needs it.
        private MSA.Expression/*!*/ ResumeFrame() {
            return Ast.Coalesce(_resumeFrame, Ast.Assign(_resumeFrame, Ast.New(typeof(JitRuntime.ResumeFrame))));
        }

        private MSA.Expression/*!*/ ResumeScope() {
            return Ast.Call(JitRuntime.M("ResumeScope"), Ast.Constant(_resume), _self, _block, ResumeFrame());
        }

        private MSA.Expression/*!*/ Fallback(JitRuntime.ResumeSite/*!*/ site, MSA.Expression/*!*/ receiver, MSA.Expression/*!*/ value) {
            return Ast.Invoke(Ast.Constant(_resume.Trampoline), Ast.Constant(site), _self, _block, ResumeFrame(), receiver, value);
        }

        /// <summary>
        /// `o.x' after the first effect: <paramref name="tryRead"/> (receiver, value) is the read under its
        /// own checks, and where it answers false the value is that of the call. The value is an object
        /// either way: a guard on what the profile saw it hold would be a deopt.
        /// </summary>
        private MSA.Expression/*!*/ EmitGuardedRead(MethodCall/*!*/ node, MSA.Expression/*!*/ receiver, bool explicitReceiver,
            Func<MSA.Expression, MSA.ParameterExpression, MSA.Expression>/*!*/ tryRead, out JT type) {
            var flags = RubyCallFlags.HasScope;
            if (!explicitReceiver) { flags |= RubyCallFlags.HasImplicitSelf; }
            if (node.IsVariableCall) { flags |= RubyCallFlags.IsVariableCall; }
            var site = new JitRuntime.ResumeSite(_resume, node.MethodName, flags, false, _statementLine);
            _fallbackSites++;

            var receiverTemp = Ast.Variable(typeof(object), "#receiver");
            var valueTemp = Ast.Variable(typeof(object), "#value");
            type = JT.Obj;
            return Ast.Block(typeof(object), new[] { receiverTemp, valueTemp },
                Ast.Assign(receiverTemp, receiver),
                Ast.Condition(tryRead(receiverTemp, valueTemp), valueTemp, Fallback(site, receiverTemp, Ast.Constant(null, typeof(object))), typeof(object))
            );
        }

        /// <summary>
        /// `o.x = v' after the first effect: <paramref name="tryWrite"/> (receiver, boxed value) is the store
        /// under its own checks, and where it answers false the call is made instead. The value is v.
        /// </summary>
        private MSA.Expression/*!*/ EmitGuardedWrite(AttributeAccess/*!*/ node, MSA.Expression/*!*/ receiver, MSA.Expression/*!*/ value, JT valueType,
            bool explicitReceiver, Func<MSA.Expression, MSA.Expression, MSA.Expression>/*!*/ tryWrite, out JT type) {
            var flags = RubyCallFlags.HasScope | RubyCallFlags.HasRhsArgument;
            if (!explicitReceiver) { flags |= RubyCallFlags.HasImplicitSelf; }
            var site = new JitRuntime.ResumeSite(_resume, node.Name, flags, true, _statementLine);
            _fallbackSites++;

            var receiverTemp = Ast.Variable(typeof(object), "#receiver");
            var valueTemp = Ast.Variable(ClrType(valueType), "#value");
            var boxedTemp = Ast.Variable(typeof(object), "#boxed");
            type = valueType;
            return Ast.Block(ClrType(valueType), new[] { receiverTemp, valueTemp, boxedTemp },
                Ast.Assign(receiverTemp, receiver),
                Ast.Assign(valueTemp, value),
                Ast.Assign(boxedTemp, BoxForStore(valueTemp, valueType)),
                Ast.IfThen(Ast.Not(tryWrite(receiverTemp, boxedTemp)), Ast.Block(typeof(void), Fallback(site, receiverTemp, boxedTemp))),
                valueTemp
            );
        }

        /// <summary>IR_JIT_VERBOSE: what else keeps a body compiled with fallbacks out; null if nothing does.</summary>
        private string ResumeObstacle() {
            if (_hasLoop) { return "a loop"; }
            if (_selfCall) { return "a self-call"; }
            if (_construction) { return "a construction"; }
            if (_hashWrite || _unloggedWrite || _unloggedStoreBefore || _undoLog != null) { return "an element write"; }
            if (_arrayLoads != null || _arrayParameters != null) { return "an Array loaded once"; }
            if (_globalsWritten != null) { return "a global variable write"; }
            if (_directCalls) { return "a call of another typed body"; }
            if (_returnLabel != null) { return "an explicit return"; }
            return null;
        }

        /// <summary>
        /// The body as the method frame the fallbacks' callees see: inside the filter that takes a `return'
        /// from a proc made in the invocation's scope, and leaving the frame as a light frame does
        /// (MethodDefinition.TransformBody). Then the generic body's :return hook, for a TracePoint a
        /// fallback's callee enabled. The value is an object: the filter's is.
        /// </summary>
        private MSA.Expression/*!*/ WrapResumeBody(MSA.Expression/*!*/ body, ref JT type) {
            body = Coerce(body, type, JT.Obj);
            type = JT.Obj;
            if (_fallbackSites == 0) { return body; }

            var result = Ast.Variable(typeof(object), "#result");
            var unwinder = Ast.Parameter(typeof(MethodUnwinder), "#u");
            var noFrame = Ast.Constant(null, typeof(JitRuntime.ResumeFrame));
            return Ast.Block(typeof(object), new[] { result, _resumeFrame },
                Ast.Assign(_resumeFrame, noFrame),
                Ast.TryCatchFinally(
                    Ast.Assign(result, body),
                    Ast.IfThen(Ast.NotEqual(_resumeFrame, noFrame), Ast.Call(JitRuntime.M("ResumeLeave"), _resumeFrame)),
                    Ast.Catch(unwinder, Ast.Condition(
                        Ast.Call(JitRuntime.M("ResumeIsTarget"), _resumeFrame, unwinder),
                        Ast.Assign(result, Ast.Call(typeof(RubyOps).GetMethod("GetMethodUnwinderReturnValue"), unwinder)),
                        Ast.Rethrow(typeof(object)),
                        typeof(object)
                    ))
                ),
                Ast.IfThen(
                    Tracing(TraceEvents.Return),
                    Ast.Call(typeof(RubyOps).GetMethod("TraceReturnEvent"), ResumeScope(), result, Ast.Constant(_resume.SourcePath, typeof(string)),
                        Ast.Constant(_resume.EndLine))
                ),
                result
            );
        }
    }
}
