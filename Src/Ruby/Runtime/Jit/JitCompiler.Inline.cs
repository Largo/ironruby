/* ****************************************************************************
 *
 * -X:JIT: a call of a small, pure Ruby method made part of the caller's typed body.
 *
 * `compare_numbers(@x, other.x)' inside `def compare_to', where compare_numbers is itself a
 * specialized method whose typed body has no effect, runs no Ruby code, has no loop and calls
 * nothing (JitCode.Inlinable): the caller invokes that typed body's lambda in place of the call,
 * which the expression compiler inlines. What the call would have resolved to is decided when
 * compiling and kept true by guards that are the call site's own:
 *   - self's class: the entry pins it (JitCode.InlinedSelfClass), as for a direct self-call; an
 *     explicit receiver's class is checked where the call is, and a different one deopts;
 *   - the method table: the caller's entry checks the global method version, which every def,
 *     undef, remove_method, alias, include/prepend, singleton method, visibility change and
 *     method added to a refinement bumps, so a redefined, overridden, removed or hidden callee
 *     fails it and the call runs generically;
 *   - visibility: an implicit or `self.' receiver sees private and protected methods, as the
 *     source's call does; an explicit receiver only public ones (a protected one is declined);
 *   - refinements: only where none are active, in the caller (the call's scope) and the callee.
 * The callee's typed body can only deopt, never raise, and it deopts the caller, which then runs
 * from the top generically: so an exception the callee raises (ZeroDivisionError, a type the
 * specialization did not expect) is raised by the real callee, in its own frame, with its own
 * backtrace. A body that is not pure where the call is cannot deopt there (_deoptAfterEffect),
 * and is declined, as for any other operation that deopts. The callee calls nothing, so the
 * inlined call cannot recurse; tracing sends both entries to the generic bodies (NotTracing).
 *
 * ***************************************************************************/

using System;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;

    internal sealed partial class JitCompiler {
        // The class of self the inlined self-calls were resolved on (JitCode.InlinedSelfClass).
        private RubyClass _inlinedSelfClass;
        // A call was inlined on a parameter under a check of the one class the profile saw it hold (JitCode).
        private bool _inlinedOnArgumentClass;

        /// <summary>
        /// The call inlined (see the file header), or null if it is not a call of an inlinable
        /// specialized method. Throws a bailout only for a call it has started to emit.
        /// </summary>
        private MSA.Expression TryInlineCall(MethodCall/*!*/ node, out JT type) {
            type = JT.None;
            if (_ast == null || _loop != null || _supplied >= 0 || _hints == null || !_refinements.IsEmpty || node.Block != null) { return null; }
            var args = (node.Arguments != null) ? node.Arguments.Expressions : new RExpr[0];
            foreach (var arg in args) {
                if (arg is SplattedArgument) { return null; }
            }
            bool selfReceiver = node.Target == null || node.Target is SelfReference;
            if (node.Target == null && node.IsVariableCall) { return null; }
            if (selfReceiver && node.MethodName == _ast.Name) { return null; }

            object sample;
            if (!TrySample(node.Target, out sample) || sample == null) { return null; }
            RubyClass cls = JitRuntime.ClassOf(_context, sample);
            if (cls.Context != _context) { return null; }
            if (selfReceiver && _inlinedSelfClass != null && _inlinedSelfClass != cls) { return null; }
            // an explicit receiver is one the guard below checks the class of: an object of a Ruby class
            if (!selfReceiver && !(sample is RubyObject)) { return null; }

            var code = InlinableCallee(cls, node.MethodName, selfReceiver, args.Length);
            if (code == null) { return null; }

            // A parameter the profile saw hold objects of two classes (`key' in find_node, a CallSign or a Vector2D):
            // both callees, each under a check of its own class, if their bodies take and give the same types.
            RubyClass otherCls = null;
            JitCode otherCode = null;
            var receiverLocal = node.Target as LocalVariable;
            if (receiverLocal != null && _hints.ArgClasses != null && receiverLocal.DefinitionLexicalDepth == _scopeDepth) {
                int p = Array.IndexOf(_paramNames, receiverLocal.Name);
                var seen = (p >= 0 && p < _hints.ArgClasses.Length) ? _hints.ArgClasses[p] : null;
                if (seen != null && seen.Length == 2 && (seen[0] == cls || seen[1] == cls)) {
                    RubyClass c2 = (seen[0] == cls) ? seen[1] : seen[0];
                    var code2 = (c2.Context == _context) ? InlinableCallee(c2, node.MethodName, false, args.Length) : null;
                    if (code2 != null && code2.ReturnType == code.ReturnType && SameTypes(code2.ParameterTypes, code.ParameterTypes)) {
                        otherCls = c2;
                        otherCode = code2;
                    }
                }
            }

            // the receiver, then the arguments, in order, each once; then the class check, then the body
            var temps = new System.Collections.Generic.List<MSA.ParameterExpression>();
            var body = new System.Collections.Generic.List<MSA.Expression>();
            var invokeArgs = new MSA.Expression[args.Length + 1];
            MSA.Expression receiver;
            if (selfReceiver) {
                receiver = _self;
            } else {
                JT rt;
                var r = Emit(node.Target, out rt);
                if (rt != JT.Obj) { throw Bail("inlined call " + node.MethodName + " on a " + rt); }
                var rv = Ast.Variable(typeof(object), "#recv");
                temps.Add(rv);
                body.Add(Ast.Assign(rv, r));
                receiver = rv;
            }
            invokeArgs[0] = receiver;
            for (int i = 0; i < args.Length; i++) {
                JT at;
                var a = Emit(args[i], out at);
                if (at == JT.Never || at == JT.None) { throw Bail("inlined call " + node.MethodName + " of a value that jumps away"); }
                a = NumericIVar(args[i], a, ref at);
                JT pt = code.ParameterTypes[i];
                if (at == JT.Obj && pt != JT.Obj) {
                    // the callee's body was typed for a number: anything else is the generic call's
                    _canDeopt = true;
                    a = Ast.Call(JitRuntime.M(pt == JT.Int ? "GuardInt" : pt == JT.Dbl ? "GuardDouble" : pt == JT.Lng ? "GuardLong" : "-"), a);
                    at = pt;
                } else if (pt == JT.Int && at == JT.Lng && _callees != null) {
                    // -X:JITCalls: an Integer computed here, for a body typed for one that fits an Int32, is narrowed
                    // behind a deopt (Coerce below), as an argument of a direct call is (CoerceArgument).
                } else if (pt == JT.Int && at != JT.Int || pt == JT.Dbl && at != JT.Dbl && at != JT.Int && at != JT.Lng) {
                    throw Bail("inlined call " + node.MethodName + ": argument " + at + " for " + pt);
                }
                var v = Ast.Variable(ClrType(pt), "#arg" + i);
                temps.Add(v);
                body.Add(Ast.Assign(v, Coerce(a, at, pt)));
                invokeArgs[i + 1] = v;
            }
            if (code.CanDeopt) { _canDeopt = true; }
            if (otherCode != null) {
                // the class the sample had first; the other one's check deopts on a third
                _canDeopt = true;
                if (otherCode.CanDeopt) { _canDeopt = true; }
                body.Add(Ast.Condition(
                    Ast.Call(JitRuntime.M("IsOfClass"), receiver, Ast.Constant(cls, typeof(RubyClass))),
                    Ast.Invoke(code.Lambda, invokeArgs),
                    Ast.Block(
                        Ast.Call(JitRuntime.M("GuardClass"), Ast.Constant(_context), receiver, Ast.Constant(otherCls, typeof(RubyClass))),
                        Ast.Invoke(otherCode.Lambda, invokeArgs)
                    ),
                    ClrType(code.ReturnType)
                ));
            } else {
                if (!selfReceiver) {
                    _canDeopt = true;
                    if (receiverLocal != null && Array.IndexOf(_paramNames, receiverLocal.Name) >= 0) { _inlinedOnArgumentClass = true; }
                    body.Add(Ast.Call(JitRuntime.M("GuardClass"), Ast.Constant(_context), receiver, Ast.Constant(cls, typeof(RubyClass))));
                } else {
                    _inlinedSelfClass = cls;
                }
                body.Add(Ast.Invoke(code.Lambda, invokeArgs));
            }
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit]   inlined {0}#{1} into {2}", cls.Name, node.MethodName, _ast.Name);
                if (otherCode != null) { Console.Error.WriteLine("[jit]   inlined {0}#{1} into {2}", otherCls.Name, node.MethodName, _ast.Name); }
            }
            type = code.ReturnType;
            return Ast.Block(ClrType(type), temps, body);
        }

        /// <summary>The typed body a call of <paramref name="name"/> on an object of <paramref name="cls"/> may inline, or null.</summary>
        private JitCode InlinableCallee(RubyClass/*!*/ cls, string/*!*/ name, bool selfReceiver, int argc) {
            var resolved = cls.ResolveMethod(name, VisibilityContext.AllVisible);
            if (!resolved.Found) { return null; }
            var info = resolved.Info as RubyMethodInfo;
            if (info == null) { return null; }
            if (!selfReceiver && info.Visibility != RubyMethodVisibility.Public) { return null; }
            var stub = info.GetDelegate().Target as JitStub;
            if (stub == null) { return null; }
            var code = stub.InlinableBody(_context, cls);
            if (code == null && stub.IsProfiling) { _hints.CalleeNotReady = true; }
            if (code == null || code.ParameterTypes.Length != argc) { return null; }
            return code;
        }

        private static bool SameTypes(JT[]/*!*/ a, JT[]/*!*/ b) {
            if (a.Length != b.Length) { return false; }
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) { return false; }
            }
            return true;
        }
    }
}

namespace IronRuby.Runtime.Jit {
    internal static class JitInlineShape {
        /// <summary>
        /// As far as the syntax can tell, a body whose typed body JitCode.Inlinable will accept: no loop, no assignment
        /// but to a local, no constant, no yield or super, and no call but an operator or a read of an attribute (`o.x',
        /// `x') - no call with arguments, so no construction and no self-recursion. JitStub.IsLeafMethod asks it before
        /// letting the screen wrap a caller of the method: a caller wrapped for a callee that can never be inlined is only
        /// declined later, and stays slower than one never wrapped.
        /// </summary>
        internal static bool IsInlinableShape(IronRuby.Compiler.Ast.MethodDefinition/*!*/ ast) {
            return IsInlinableShape(ast, null);
        }

        /// <summary>
        /// <paramref name="selfCallee"/>: whether a call on self with arguments (`compare_numbers(@x, other.x)') is of a
        /// method that is itself of this shape - one level of nesting: the typed body of a method whose only such calls
        /// were inlined (TryInlineCall) has no call left at run time, so it is as inlinable as a leaf. Null: none.
        /// </summary>
        internal static bool IsInlinableShape(IronRuby.Compiler.Ast.MethodDefinition/*!*/ ast, Func<string, bool> selfCallee) {
            var check = new InlinableShape();
            check.SelfCallee = selfCallee;
            check.Name = ast.Name;
            ast.Body.Walk(check);
            return check.Ok;
        }

        private sealed class InlinableShape : IronRuby.Compiler.Ast.Walker {
            internal bool Ok = true;
            internal Func<string, bool> SelfCallee;
            internal string Name;
            private bool No() { Ok = false; return false; }

            public override bool Enter(IronRuby.Compiler.Ast.WhileLoopExpression node) { return No(); }
            public override bool Enter(IronRuby.Compiler.Ast.ForLoopExpression node) { return No(); }
            public override bool Enter(IronRuby.Compiler.Ast.YieldCall node) { return No(); }
            public override bool Enter(IronRuby.Compiler.Ast.SuperCall node) { return No(); }
            public override bool Enter(IronRuby.Compiler.Ast.ConstantVariable node) { return No(); }
            public override bool Enter(IronRuby.Compiler.Ast.MemberAssignmentExpression node) { return No(); }

            public override bool Enter(IronRuby.Compiler.Ast.SimpleAssignmentExpression node) {
                return node.Left is IronRuby.Compiler.Ast.LocalVariable || No();
            }

            public override bool Enter(IronRuby.Compiler.Ast.MethodCall node) {
                if (node.Block != null) { return No(); }
                int argc = (node.Arguments == null) ? 0 : node.Arguments.Expressions.Length;
                if (argc == 0) { return true; }
                if (argc == 1 && node.Target != null && JitScreen.IsBinaryOperator(node.MethodName)) { return true; }
                if (SelfCallee != null && (node.Target == null && !node.IsVariableCall || node.Target is IronRuby.Compiler.Ast.SelfReference) &&
                    node.MethodName != Name && SelfCallee(node.MethodName)) {
                    return true;
                }
                return No();
            }
        }
    }
}
