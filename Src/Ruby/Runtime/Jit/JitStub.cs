/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * The profiling tier and the entry stub.
 *
 * When -X:JIT is on, RubyMethodBody.Compile wraps the generic body delegate in a
 * trampoline of the same signature, so nothing else in the runtime - the binder, the
 * call site rules, the dispatchers - has to change. The trampoline counts calls and
 * records the CLR type of each argument; once the method is hot and its argument types
 * have been stable, it asks JitCompiler for a typed body and, if it gets one, compiles
 * an *entry* lambda that checks the guards and either enters the typed body or falls
 * through to the generic one.
 *
 * Guards, and why they are sound:
 *   - RubyModule.GlobalMethodVersion is bumped by every method table change anywhere
 *     (def, undef, alias, include/extend, singleton method, attr_*), so one snapshot
 *     comparison covers method redefinition and overriding in a subclass.
 *   - JitRuntime.Disabled is set when refinements appear.
 *   - The receiver's immediate class is pinned whenever the specialized body contains a
 *     direct self-call, so that call cannot be a call to an override.
 *   - Argument types are re-checked on entry; a miss falls through to the generic body.
 *   - Integer overflow out of Int64, a narrowing back to Int32 that does not fit, and
 *     division by zero raise JitDeoptException, caught at the entry,
 *     which re-runs the generic body from the top. JitCompiler only produces code that
 *     can deopt for a body with no observable effect, so the restart is invisible.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    internal abstract class JitStub {
        internal const int Threshold = 40;
        internal const int MaxArity = 3;

        protected readonly MethodDeclaration/*!*/ _ast;
        protected readonly RubyContext/*!*/ _context;
        protected readonly RubyModule/*!*/ _declaringModule;
        protected readonly Delegate/*!*/ _generic;
        protected readonly int _arity;
        private string _frameName;

        // profiling feedback
        private int _calls;
        private readonly int[]/*!*/ _argKind;       // 0 = unseen, 1 = int, 2 = double, 3 = other/mixed, 4 = long
        private RubyClass _receiverClass;
        private bool _polymorphicReceiver;
        private volatile bool _givenUp;

        protected JitStub(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, RubyModule/*!*/ declaringModule, Delegate/*!*/ generic, int arity) {
            _ast = ast;
            _context = context;
            _declaringModule = declaringModule;
            _generic = generic;
            _arity = arity;
            _argKind = new int[arity];
        }

        protected abstract void Install(Delegate/*!*/ entry);
        internal abstract Delegate/*!*/ Trampoline { get; }

        /// <summary>Called by the trampoline on every call until the method is specialized.</summary>
        protected void Profile(object self, object[] args) {
            if (_givenUp) { return; }

            for (int i = 0; i < _arity; i++) {
                int k = (args[i] is int) ? 1 : (args[i] is double) ? 2 : (args[i] is long) ? 4 : 3;
                if (_argKind[i] == 0) {
                    _argKind[i] = k;
                } else if (_argKind[i] != k) {
                    // int and long are one Ruby class seen in two representations, so a
                    // parameter that has been both is still an Integer: carry it as a long.
                    bool bothIntegral = (k == 1 || k == 4) && (_argKind[i] == 1 || _argKind[i] == 4);
                    _argKind[i] = bothIntegral ? 4 : 3;
                }
            }

            RubyClass cls = JitRuntime.ClassOf(_context, self);
            if (_receiverClass == null) { _receiverClass = cls; } else if (_receiverClass != cls) { _polymorphicReceiver = true; }

            if (++_calls < Threshold) { return; }
            _calls = 0;

            try {
                if (!TrySpecialize()) {
                    GiveUp();
                }
            } catch (Exception) {
                GiveUp();
            }
        }

        /// <summary>
        /// Stop profiling this method for good, and put the generic body back in the fast slot.
        /// Leaving the slot empty would be much worse than doing nothing: the trampoline would
        /// go on allocating an argument array and calling Profile - which returns immediately -
        /// on every call for the rest of the process.  A method the JIT declined is the common
        /// case, so that path has to cost one delegate hop and nothing else.
        /// </summary>
        private void GiveUp() {
            _givenUp = true;
            // Every method that gets here passed JitScreen, so each one is either a genuinely
            // type-dependent refusal or the screen and JitCompiler.Emit having drifted apart.
            // Name it, so a count that creeps up can be traced to the construct responsible.
            JitRuntime.Rejected++;
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit] declined {0}/{1} arity {2} (passed the pre-screen)",
                    _declaringModule.Name ?? "main", _ast.Name, _arity);
            }
            Install(_generic);
        }

        /// <summary>Deoptimize: drop the specialized entry and let profiling start over.</summary>
        internal void Invalidate() {
            JitRuntime.Invalidations++;
            // A method that was already declined must not fall back into profiling here.
            Install(_givenUp ? _generic : null);
            _calls = 0;
        }

        private bool TrySpecialize() {
            if (JitRuntime.Disabled || _context.HasRefinements) { return false; }

            var types = new JT[_arity];
            for (int i = 0; i < _arity; i++) {
                switch (_argKind[i]) {
                    case 1: types[i] = JT.Int; break;
                    case 2: types[i] = JT.Dbl; break;
                    case 4: types[i] = JT.Lng; break;
                    default: types[i] = JT.Obj; break;
                }
            }

            long start = Stopwatch.GetTimestamp();
            var code = JitCompiler.TryCompile(_ast, _context, types, _frameName);
            if (code == null) { return false; }

            bool needsClassGuard = code.EmittedSelfCall;
            if (needsClassGuard) {
                if (_receiverClass == null || _polymorphicReceiver) { return false; }
                if (!SelfCallResolvesHere(_receiverClass)) { return false; }
            }

            int version = RubyModule.GlobalMethodVersion;

            // The typed body only has to exist as a delegate of its own when it calls itself: the
            // direct self-call goes through SelfCell. Otherwise the entry invokes the lambda
            // itself, which the expression compiler inlines, so a specialized call is the
            // trampoline hop and the entry and nothing more.
            MSA.Expression callee;
            if (code.EmittedSelfCall) {
                Delegate typed = code.Lambda.Compile();
                code.SelfCell.Value = typed;
                callee = Ast.Constant(typed, typed.GetType());
            } else {
                callee = code.Lambda;
            }

            var entry = BuildEntry(code, callee, version, needsClassGuard ? _receiverClass : null);
            Install(entry.Compile());

            JitRuntime.CompileTicks += Stopwatch.GetTimestamp() - start;
            JitRuntime.Specialized++;
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit] specialized {0}/{1} arity {2} -> {3}{4}",
                    _declaringModule.Name ?? "main", _ast.Name, _arity, code.ReturnType, needsClassGuard ? " (self-call)" : "");
            }
            return true;
        }

        /// <summary>
        /// The direct self-call is only correct if the name still resolves, from the profiled
        /// receiver class, to this very body.
        /// </summary>
        private bool SelfCallResolvesHere(RubyClass/*!*/ cls) {
            var resolved = cls.ResolveMethod(_ast.Name, VisibilityContext.AllVisible);
            if (!resolved.Found) { return false; }
            var info = resolved.Info as RubyMethodInfo;
            return info != null && ReferenceEquals(info.GetSyntaxTree(), _ast);
        }

        // ---- entry lambda ------------------------------------------------------------------

        private MSA.LambdaExpression/*!*/ BuildEntry(JitCode/*!*/ code, MSA.Expression/*!*/ callee, int version, RubyClass pinnedClass) {
            var self = Ast.Parameter(typeof(object), "self");
            var blk = Ast.Parameter(typeof(Proc), "block");
            var args = new MSA.ParameterExpression[_arity];
            for (int i = 0; i < _arity; i++) {
                args[i] = Ast.Parameter(typeof(object), "a" + i);
            }

            MSA.Expression test = Ast.Call(JitRuntime.M("VersionOk"), Ast.Constant(version));
            if (pinnedClass != null) {
                test = Ast.AndAlso(test, Ast.ReferenceEqual(
                    Ast.Call(JitRuntime.M("ClassOf"), Ast.Constant(_context), self),
                    Ast.Constant(pinnedClass, typeof(RubyClass))));
            }
            for (int i = 0; i < _arity; i++) {
                switch (code.ParameterTypes[i]) {
                    case JT.Int: test = Ast.AndAlso(test, Ast.TypeIs(args[i], typeof(int))); break;
                    case JT.Dbl: test = Ast.AndAlso(test, Ast.TypeIs(args[i], typeof(double))); break;
                    // An Integer parameter carried as a long accepts either representation: a
                    // value in Int32 range arrives as an Int32 and only a larger one as an Int64.
                    case JT.Lng: test = Ast.AndAlso(test, Ast.Call(JitRuntime.M("IsIntegral"), args[i])); break;
                }
            }

            var typedArgs = new MSA.Expression[_arity + 2];
            typedArgs[0] = self;
            typedArgs[1] = Ast.Constant(0);     // recursion depth: see JitRuntime.EnterSelfCall
            for (int i = 0; i < _arity; i++) {
                switch (code.ParameterTypes[i]) {
                    case JT.Int: typedArgs[i + 2] = Ast.Unbox(args[i], typeof(int)); break;
                    case JT.Dbl: typedArgs[i + 2] = Ast.Unbox(args[i], typeof(double)); break;
                    case JT.Lng: typedArgs[i + 2] = Ast.Call(JitRuntime.M("ToLong"), args[i]); break;
                    default: typedArgs[i + 2] = args[i]; break;
                }
            }

            MSA.Expression fast = Ast.Invoke(callee, typedArgs);
            if (code.ReturnType == JT.Lng) {
                // The one place a specialized method's value becomes a Ruby object: it leaves
                // through the representation funnel, so a result that fits in an Int32 is one.
                fast = Ast.Call(JitRuntime.M("NarrowLong"), fast);
            } else if (fast.Type != typeof(object)) {
                fast = Ast.Convert(fast, typeof(object));
            }

            if (code.CanDeopt) {
                // The generic body runs after the catch, not inside it: a catch handler runs on
                // top of the frames the exception is leaving, so a recursive method that
                // deopts at the bottom of its recursion - and re-runs generically, recursing
                // into the typed body again, which deopts again - nested one handler per level
                // and overflowed the stack a few hundred levels down.
                var result = Ast.Variable(typeof(object), "#result");
                var deopt = Ast.Variable(typeof(bool), "#deopt");
                fast = Ast.Block(new[] { result, deopt },
                    Ast.Assign(deopt, Ast.Constant(false)),
                    Ast.TryCatch(
                        Ast.Assign(result, fast),
                        Ast.Catch(typeof(JitDeoptException), Ast.Block(Ast.Assign(deopt, Ast.Constant(true)), Ast.Default(typeof(object))))),
                    Ast.Condition(deopt, GenericCall(self, blk, args), result));
            }

            var body = Ast.Condition(test, fast, GenericCall(self, blk, args), typeof(object));

            var all = new MSA.ParameterExpression[_arity + 2];
            all[0] = self;
            all[1] = blk;
            Array.Copy(args, 0, all, 2, _arity);
            return Ast.Lambda(_generic.GetType(), body, "jitentry$" + _ast.Name, all);
        }

        private MSA.Expression/*!*/ GenericCall(MSA.ParameterExpression/*!*/ self, MSA.ParameterExpression/*!*/ blk, MSA.ParameterExpression[]/*!*/ args) {
            var callArgs = new MSA.Expression[_arity + 2];
            callArgs[0] = self;
            callArgs[1] = blk;
            Array.Copy(args, 0, callArgs, 2, _arity);
            return Ast.Invoke(Ast.Constant(_generic, _generic.GetType()), callArgs);
        }

        // ---- factory -----------------------------------------------------------------------

        /// <summary>
        /// Decides, statically and cheaply, whether a method is worth putting a stub on, and
        /// returns the trampoline that replaces the generic delegate. Returns the generic
        /// delegate unchanged when it is not.
        /// </summary>
        /// <param name="frameName">The generic body's lambda name, which encodes the method's name,
        /// line and file for backtraces (RubyStackTraceBuilder.EncodeMethodName); the typed body is
        /// given it too, so that its frames read like the generic body's.</param>
        internal static Delegate/*!*/ Wrap(Delegate/*!*/ generic, MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, RubyModule/*!*/ declaringModule,
            string frameName) {
            if (JitRuntime.Disabled) { return generic; }
            JitRuntime.HookStats();

            var ps = ast.Parameters;
            if (ps.Optional.Length != 0 || ps.Unsplat != null || ps.Block != null || ast.UsesBlock) { return generic; }
            int arity = ps.Mandatory.Length;
            if (arity > MaxArity) { return generic; }
            for (int i = 0; i < arity; i++) {
                if (!(ps.Mandatory[i] is LocalVariable)) { return generic; }
            }
            // Tracing and coverage need the real frame.
            if (context.RubyOptions.EnableTracing || context.RubyOptions.Profile) { return generic; }

            // A body the compiler can never accept, whatever types it is called with, is not
            // wrapped at all. Once wrapped, the trampoline is what the runtime caches and bakes
            // into call site rules, so a method declined later still pays one extra indirect
            // call on every call for the life of the process.
            string rejectedBy;
            if (!JitScreen.CanEverCompile(ast, out rejectedBy)) {
                JitRuntime.RecordScreenRejection(rejectedBy);
                return generic;
            }

            JitStub stub;
            switch (arity) {
                case 0: stub = new JitStub0(ast, context, declaringModule, generic); break;
                case 1: stub = new JitStub1(ast, context, declaringModule, generic); break;
                case 2: stub = new JitStub2(ast, context, declaringModule, generic); break;
                default: stub = new JitStub3(ast, context, declaringModule, generic); break;
            }
            stub._frameName = frameName;
            return stub.Trampoline;
        }

        private static readonly object[] NoArgs = new object[0];
        protected static object[] Args0 { get { return NoArgs; } }
    }

    // ---- arity-specialized stubs -----------------------------------------------------------

    internal sealed class JitStub0 : JitStub {
        private volatile Func<object, Proc, object> _fast;
        private readonly Func<object, Proc, object>/*!*/ _gen;
        private readonly Func<object, Proc, object>/*!*/ _tramp;

        internal JitStub0(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic)
            : base(ast, context, m, generic, 0) {
            _gen = (Func<object, Proc, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object>)entry; }

        private object Call(object self, Proc block) {
            var f = _fast;
            if (f != null) { return f(self, block); }
            Profile(self, Args0);
            f = _fast;
            return (f != null) ? f(self, block) : _gen(self, block);
        }
    }

    internal sealed class JitStub1 : JitStub {
        private volatile Func<object, Proc, object, object> _fast;
        private readonly Func<object, Proc, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object>/*!*/ _tramp;

        internal JitStub1(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic)
            : base(ast, context, m, generic, 1) {
            _gen = (Func<object, Proc, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object>)entry; }

        private object Call(object self, Proc block, object a0) {
            var f = _fast;
            if (f != null) { return f(self, block, a0); }
            Profile(self, new[] { a0 });
            f = _fast;
            return (f != null) ? f(self, block, a0) : _gen(self, block, a0);
        }
    }

    internal sealed class JitStub2 : JitStub {
        private volatile Func<object, Proc, object, object, object> _fast;
        private readonly Func<object, Proc, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object>/*!*/ _tramp;

        internal JitStub2(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic)
            : base(ast, context, m, generic, 2) {
            _gen = (Func<object, Proc, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object>)entry; }

        private object Call(object self, Proc block, object a0, object a1) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1); }
            Profile(self, new[] { a0, a1 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1) : _gen(self, block, a0, a1);
        }
    }

    internal sealed class JitStub3 : JitStub {
        private volatile Func<object, Proc, object, object, object, object> _fast;
        private readonly Func<object, Proc, object, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object, object>/*!*/ _tramp;

        internal JitStub3(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic)
            : base(ast, context, m, generic, 3) {
            _gen = (Func<object, Proc, object, object, object, object>)generic;
            _tramp = Call;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void Install(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object>)entry; }

        private object Call(object self, Proc block, object a0, object a1, object a2) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2); }
            Profile(self, new[] { a0, a1, a2 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2) : _gen(self, block, a0, a1, a2);
        }
    }

}
