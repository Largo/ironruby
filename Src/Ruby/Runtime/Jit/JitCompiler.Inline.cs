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

            var resolved = cls.ResolveMethod(node.MethodName, VisibilityContext.AllVisible);
            if (!resolved.Found) { return null; }
            var info = resolved.Info as RubyMethodInfo;
            if (info == null) { return null; }
            if (!selfReceiver && info.Visibility != RubyMethodVisibility.Public) { return null; }
            var stub = info.GetDelegate().Target as JitStub;
            if (stub == null) { return null; }
            var code = stub.InlinableBody(_context, cls);
            if (code == null && stub.IsProfiling) { _hints.CalleeNotReady = true; }
            if (code == null || code.ParameterTypes.Length != args.Length) { return null; }

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
                } else if (pt == JT.Int && at != JT.Int || pt == JT.Dbl && at != JT.Dbl && at != JT.Int && at != JT.Lng) {
                    throw Bail("inlined call " + node.MethodName + ": argument " + at + " for " + pt);
                }
                var v = Ast.Variable(ClrType(pt), "#arg" + i);
                temps.Add(v);
                body.Add(Ast.Assign(v, Coerce(a, at, pt)));
                invokeArgs[i + 1] = v;
            }
            if (!selfReceiver) {
                _canDeopt = true;
                body.Add(Ast.Call(JitRuntime.M("GuardClass"), Ast.Constant(_context), receiver, Ast.Constant(cls, typeof(RubyClass))));
            } else {
                _inlinedSelfClass = cls;
            }
            if (code.CanDeopt) { _canDeopt = true; }
            body.Add(Ast.Invoke(code.Lambda, invokeArgs));
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit]   inlined {0}#{1} into {2}", cls.Name, node.MethodName, _ast.Name);
            }
            type = code.ReturnType;
            return Ast.Block(ClrType(type), temps, body);
        }
    }
}
