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
 *   - The calls the typed body binds itself - operators, the self-call - are resolved under the
 *     refinements active where the method was defined (RubyMethodScope.DefinitionRefinements),
 *     which is what its call sites would see; none, for a method defined where nothing was
 *     `using'd. A method added to an active refinement bumps the global method version too.
 *   - The receiver's immediate class is pinned whenever the specialized body contains a
 *     direct self-call, so that call cannot be a call to an override.
 *   - Direct calls (on by default, -X:NoJITCalls turns them off): a call of another method on self is bound to
 *     that method's typed body (BindCallee), resolved from the same pinned class under the same
 *     method version; the guards the callee's own entry would make move to the caller's entry
 *     and the callee's deopts end there.
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
        internal const int MaxArity = 6;
        internal const string EntryNamePrefix = "jitentry$";
        internal const string DirectEntryNamePrefix = "jitdirect$";

        // What a typed body's call evaluates to in the entry when it deopted.
        private static readonly object Deopted = new object();

        protected readonly MethodDeclaration/*!*/ _ast;
        protected readonly RubyContext/*!*/ _context;
        protected readonly RubyModule/*!*/ _declaringModule;
        protected readonly Delegate/*!*/ _generic;
        protected readonly int _arity;
        // A method with keyword parameters (see JitKeywords): its last delegate parameter is the
        // keyword hash, and the typed body takes the keyword values instead, after the positionals.
        protected readonly JitKeywords _keywords;
        private readonly int _positional;     // delegate parameters that are positional parameters
        private readonly int _typedCount;     // parameters of the typed body

        // A specialized keyword method's entry for JitKeywordCall: (self, p0, p1, k0, k1, k2) with the
        // keyword values in declaration order and unused slots null. Answers JitKeywordCall.NoDirect
        // when a guard fails. Null when the method is not specialized.
        private volatile Func<object, object, object, object, object, object, object> _direct;

        internal Func<object, object, object, object, object, object, object> Direct { get { return _direct; } }
        internal JitKeywords Keywords { get { return _keywords; } }
        // Declined for good: the generic body is all this method will ever run, so there is no Direct to wait for.
        internal bool GivenUp { get { return _state == Settled; } }
        // Retired for a while (OnDeopt, Probe): the entry, and Direct with it, comes back after the cool-down.
        internal bool IsCooling { get { return _state == Cooling; } }
        internal int Positional { get { return _positional; } }

        // Optional parameters (after the mandatory ones, as in the generic delegate) have a literal
        // default; a call that omits one passes RubyOps.DefaultArgument in its place, which the
        // entry and the profiler replace with the value. _hasDefault is null with no optionals.
        private bool[] _hasDefault;
        private object[] _defaults;

        // A method with an optional parameter whose default is not such a literal gets one typed body
        // per number of optional arguments a call passes (JitCompiler's `supplied'), each evaluating the
        // defaults of the rest itself. _countsSeen has bit k set once a call passed k of them.
        private int _perCountMandatory = -1;
        private int _countsSeen;

        // Parameters the body only ever stores into an instance variable or an element: kept as
        // the boxed objects they arrived as, whatever their type (JitScreen's otherUses).
        private bool[] _storedOnly;

        // The scope the method was defined in, which its constants resolve in (JitCompiler.EmitConstant).
        // The compiled body is shared only by `def's of the same lexical module chain, which is all a
        // constant lookup depends on (RubyMethodBody.GetDelegate). Null: the body folds no constants.
        private RubyScope _declaringScope;

        // The record of the refinements active where the method was defined, as the generic body has it
        // (RubyMethodBody.Compile); null for a body compiled with none active.
        private StrongBox<RefinementActivation> _definitionRefinements;

        // profiling feedback
        private int _calls;
        private readonly int[]/*!*/ _argKind;       // 0 = unseen, 1 = int, 2 = double, 3 = other/mixed, 4 = long
        // The CLR type of each argument, and of self, while profiling: one type, JitCompiler.MixedTypes
        // once two have differed, typeof(void) for nil. A hint the compiler keeps an Array or Hash
        // intrinsic off a receiver with (JitCompiler.ObservedType); it is never relied on for a result.
        private readonly Type[]/*!*/ _argClrType;
        private Type _selfClrType;
        // The Ruby classes each argument has had while profiling, the first two (an object of a class of its own
        // or one that is not a RubyObject counts as a third): which receivers an inlined call on a parameter
        // dispatches between (JitCompiler.TryInlineCall). A hint, like the rest; the inlined call checks the class.
        private readonly RubyClass[]/*!*/ _argClass1, _argClass2;
        private readonly bool[]/*!*/ _argManyClasses;

        // The receiver of the call that reached the threshold, while TrySpecialize runs (under _compiling).
        private object _selfSample;
        private object[] _argSample;
        private RubyClass _receiverClass;
        private bool _polymorphicReceiver;

        // ---- state -------------------------------------------------------------------------
        // Profiling:   the fast slot is empty; the trampoline profiles each call (Profile).
        // Specialized: the fast slot holds _current's entry.
        // Cooling:     _current was retired for deopting too often (OnDeopt); the fast slot holds
        //              the counted generic body (CoolingBody) until _current is tried again (CoolDownCheck).
        // Settled:     the fast slot holds the generic body for good (GiveUp).
        // Every change of state is made under _sync. A callback from an entry (EntryDeopted,
        // EntryStale) names its specialization and changes nothing unless that one is current, so
        // an older entry that another thread is still running cannot undo what replaced it.
        private const int Profiling = 0, Specialized = 1, Cooling = 2, Settled = 3;
        private volatile int _state;
        private Specialization _current;
        private readonly object/*!*/ _sync = new object();
        // 1 while a thread is in TrySpecialize. The threads that reach the threshold meanwhile -
        // they keep profiling, the fast slot being empty - run the generic body instead of
        // compiling the same method again (a race the base already had: one method specialized
        // 8 times over by 8 threads, and the last to finish installed its entry over whatever
        // had happened to the first one since).
        private int _compiling;

        // -X:JITCalls: this method's typed bodies call the typed bodies of other methods of the
        // receiver (TrySpecialize, BindCallee).
        private readonly bool _directCalls;
        // Set while compiling: a call the body would bind is of a method that may still get a typed
        // body (it is profiling or cooling down, or its entry is stale). The method then goes on
        // profiling, as for a callee it would inline (ReceiverHints.CalleeNotReady, MaxInlineWaits).
        private bool _calleeNotReady;

        protected JitStub(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, RubyModule/*!*/ declaringModule, Delegate/*!*/ generic, int arity,
            JitKeywords keywords) {
            _ast = ast;
            _context = context;
            _declaringModule = declaringModule;
            _generic = generic;
            _arity = arity;
            _keywords = keywords;
            _positional = (keywords != null) ? arity - 1 : arity;
            _typedCount = (keywords != null) ? _positional + keywords.Count : arity;
            _argKind = new int[_typedCount];
            _argClrType = new Type[_typedCount];
            _directCalls = context.RubyOptions.JitCalls;
            _argClass1 = new RubyClass[_typedCount];
            _argClass2 = new RubyClass[_typedCount];
            _argManyClasses = new bool[_typedCount];
        }

        protected abstract void SetFast(Delegate entry);
        protected abstract Delegate Fast { get; }
        internal abstract Delegate/*!*/ Trampoline { get; }

        // ---- what the callers hold -----------------------------------------------------------
        // The method's dispatchers (RubyMethodBody.RegisterDispatcher) call whatever the fast slot
        // holds, not the trampoline, whenever it holds something: the specialized entry, which checks
        // every guard itself, the cooling stand-in, or the generic body once settled. A call through
        // one then makes a single delegate invoke instead of two, the trampoline's being only a
        // volatile load and a null test in front of the same invoke. With the slot empty (profiling)
        // they call the trampoline. Install is the one place the slot changes, always under _sync,
        // so the dispatchers follow each change in order. A dispatcher that still calls an entry
        // replaced a moment ago runs code that is still correct - its guards fail into the generic
        // body, and its callbacks change nothing for a specialization that is not current - as a
        // thread that read the slot before the change does. Rules that baked the trampoline into
        // their expression (RubyMethodInfo.BuildCallNoFlow) keep going through it.
        // Bounded: a program that makes call sites without end keeps the rest on the trampoline.
        private const int MaxDispatchers = 256;
        private List<MethodDispatcher> _dispatchers;
        // The method body that holds the trampoline as its delegate, told when the stub settles.
        private RubyMethodBody _owner;
        // The generic body's compiled form, once the light compiler has made it (Wrap).
        private Delegate _compiledGeneric;

        // Under _sync.
        private void Install(Delegate entry) {
            SetFast(entry);
            if (_dispatchers != null) {
                Delegate target = entry ?? Trampoline;
                foreach (var dispatcher in _dispatchers) {
                    dispatcher.SetMethod(target);
                }
            }
        }

        /// <summary>
        /// Called for each dispatcher made over the trampoline: points it at what the fast slot holds.
        /// </summary>
        internal void AddDispatcher(MethodDispatcher/*!*/ dispatcher) {
            lock (_sync) {
                if (_dispatchers == null) { _dispatchers = new List<MethodDispatcher>(); }
                if (_dispatchers.Count >= MaxDispatchers) { return; }
                _dispatchers.Add(dispatcher);
                Delegate fast = Fast;
                if (fast != null) { dispatcher.SetMethod(fast); }
            }
        }

        /// <summary>
        /// The expression a rule calls <paramref name="method"/> with, if it is a trampoline: a read of what the stub would
        /// call (Target) and one invoke of it, rather than an invoke of the trampoline that reads the fast slot and invokes that.
        /// Null for any other delegate.
        /// </summary>
        internal static MSA.Expression CallCurrent(Delegate/*!*/ method, MSA.Expression[]/*!*/ arguments) {
            var stub = method.Target as JitStub;
            if (stub == null || method != stub.Trampoline) { return null; }
            var target = Ast.Field(Ast.Constant(stub), stub.GetType().GetField("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
            return Ast.Invoke(target, arguments);
        }

        internal static void SetOwner(Delegate/*!*/ method, RubyMethodBody/*!*/ owner) {
            var stub = method.Target as JitStub;
            if (stub != null && method == stub.Trampoline) {
                stub._owner = owner;
            }
        }

        // The generic body as cheap as it is to call: compiled if it is.
        private Delegate/*!*/ BestGeneric {
            get { return Volatile.Read(ref _compiledGeneric) ?? _generic; }
        }

        private void GenericCompiled(Delegate/*!*/ compiled) {
            if (compiled.GetType() == _generic.GetType()) {
                Volatile.Write(ref _compiledGeneric, compiled);
            }
            Volatile.Write(ref _compiledAt, Stopwatch.GetTimestamp());
            _genericCompiled = true;
            lock (_sync) {
                if (_genericCostInterpreted) {
                    // The generic cost was the interpreter's - a probe waits only MaxProbeDelays windows for the compile,
                    // which go by in microseconds when every other call deopts - and many times the compiled body's: it
                    // made an entry that deopts on every other call look worth keeping (4 us against 0.16, perf/jitprobe).
                    // Forget it, so that the next window times the compiled body (Probe).
                    _genericCost = 0;
                    _genericCostInterpreted = false;
                    _probeAfter = 0;
                }
            }
            if (_compiledGeneric == null) { return; }
            bool settled;
            lock (_sync) {
                settled = _state == Settled;
                // a retired entry's rest holds the generic body too (StartResting)
                if (settled || _state == Cooling && _resting) { Install(compiled); }
            }
            if (settled) { NotifyOwner(); }
        }

        // Outside _sync: the method body's delegate becomes the generic body, so that the rules and
        // dispatchers made from now on do not go through the trampoline at all. Settled is for good,
        // and the generic body is what the trampoline would run.
        private void NotifyOwner() {
            var owner = _owner;
            if (owner != null) {
                owner.ReplaceTrampoline(Trampoline, _generic, BestGeneric);
            }
        }
        // The stand-in for a retired entry: the generic body behind a countdown of _coolCalls.
        protected abstract Delegate/*!*/ CoolingBody { get; }

        /// <summary>
        /// One specialization of the method: its entries, and the deopt counts OnDeopt keeps for
        /// it. The entry hands it to EntryDeopted and EntryStale, so that a callback says which
        /// specialization it came from. Sealed, because the entry counts its calls through it,
        /// reaching it through the closure's constant array: a cast to a sealed class is a
        /// type-handle compare where one to JitStub is a helper call.
        /// </summary>
        internal sealed class Specialization {
            // While a deopt window is open (OnDeopt): the entries into the typed body still left
            // in it, counted down by an entry that can deopt (BuildEntry). Zero otherwise, and then
            // the entry only reads it, so threads calling a method that does not deopt write
            // nothing they share. Not interlocked: a lost decrement only blurs a rate.
            public int Countdown;
            internal static readonly System.Reflection.FieldInfo/*!*/ CountdownField = typeof(Specialization).GetField("Countdown");

            internal readonly JitStub/*!*/ Stub;
            internal Delegate Entry;
            internal Func<object, object, object, object, object, object, object> Direct;
            internal int WindowDeopts;
            // The least a deopt of the open window cost, throw to catch, in Stopwatch ticks; 0 if
            // none was timed (OnDeopt).
            internal long DeoptCost;
            // The constant version the typed bodies were compiled under (JitCode.UsesConstants), or the one under
            // which the constants they folded were last found to be what they were (RevalidateConstants). The
            // entry reads it (JitRuntime.ConstantsCurrent), and so does a caller binding the body (BindCallee).
            internal int ConstantVersion;
            // What the typed bodies folded (JitCode.FoldedConstants), and the bodies whose loops guard on the
            // constant version too (JitLoopGuard); both null if they folded none. Never changed once installed.
            internal JitFoldedConstant[] FoldedConstants;
            internal JitCode[] FoldingCodes;
            // The one typed body (not per-count ones), the method version its entry checks and the class it
            // pins self to, if any: what a caller that inlines it (JitStub.InlinableBody) has to check too.
            internal JitCode Code;
            internal int Version;
            internal RubyClass PinnedClass;
            // -X:JITCalls: the body as a delegate, for a caller to call (BindCallee). Made when the method
            // calls itself, or when the first caller asks.
            internal Delegate Typed;

            internal Specialization(JitStub/*!*/ stub) {
                Stub = stub;
            }
        }

        /// <summary>Called by the trampoline on every call until the method is specialized.</summary>
        protected void Profile(object self, object[] args) {
            if (_state != Profiling) { return; }

            if (_keywords != null && _keywords.OptionalPositionals > 0) {
                JitKeywords.ShiftKeywords(args, _positional - _keywords.OptionalPositionals, _positional);
            }
            if (_keywords != null) {
                // what the prologue binds the keywords to, if it binds them at all
                object kw = args[_positional];
                if (_keywords.Matches(kw)) {
                    for (int j = 0; j < _keywords.Count; j++) {
                        // an omitted keyword whose default the typed body evaluates has no type to see
                        object v = _keywords.Value(kw, j);
                        if (v != RubyOps.DefaultArgument) { Observe(_positional + j, v); }
                    }
                }
            }
            int observed = _positional;
            if (_perCountMandatory >= 0) {
                // the omitted optional arguments are a suffix; only the passed ones have a type
                observed = _perCountMandatory;
                while (observed < _positional && args[observed] != RubyOps.DefaultArgument) { observed++; }
                _countsSeen |= 1 << (observed - _perCountMandatory);
            }
            for (int i = 0; i < observed; i++) {
                if (_hasDefault != null && _hasDefault[i] && args[i] == RubyOps.DefaultArgument) {
                    args[i] = _defaults[i];
                }
                Observe(i, args[i]);
            }

            RubyClass cls = JitRuntime.ClassOf(_context, self);
            if (_receiverClass == null) { _receiverClass = cls; } else if (_receiverClass != cls) { _polymorphicReceiver = true; }
            ObserveType(ref _selfClrType, self);

            if (++_calls < Threshold) { return; }
            _calls = 0;

            if (Interlocked.CompareExchange(ref _compiling, 1, 0) != 0) { return; }
            try {
                // A thread that read the empty fast slot before another one installed an entry
                // can get here after that one has let go of _compiling.
                if (_state != Profiling) { return; }
                // the receiver of this call is the instance variable profile (Hints); it is only
                // held while compiling, so a stub keeps no object of the program alive
                _selfSample = self;
                _argSample = args;
                if (!TrySpecialize()) {
                    GiveUp();
                }
            } catch (Exception) {
                GiveUp();
            } finally {
                _selfSample = null;
                _argSample = null;
                Volatile.Write(ref _compiling, 0);
            }
        }

        private static void ObserveType(ref Type seen, object value) {
            Type t = (value == null) ? typeof(void) : value.GetType();
            if (seen == null) { seen = t; } else if (seen != t) { seen = JitCompiler.MixedTypes; }
        }

        private void Observe(int i, object arg) {
            ObserveType(ref _argClrType[i], arg);
            if (!_argManyClasses[i]) {
                var obj = arg as RubyObject;
                RubyClass cls = (obj != null) ? obj.ImmediateClass : null;
                if (cls == null || cls.IsSingletonClass) {
                    _argManyClasses[i] = true;
                } else if (_argClass1[i] == null) {
                    _argClass1[i] = cls;
                } else if (_argClass1[i] != cls) {
                    if (_argClass2[i] == null) { _argClass2[i] = cls; } else if (_argClass2[i] != cls) { _argManyClasses[i] = true; }
                }
            }
            int k = (arg is int) ? 1 : (arg is double) ? 2 : (arg is long) ? 4 : 3;
            if (_argKind[i] == 0) {
                _argKind[i] = k;
            } else if (_argKind[i] != k) {
                // int and long are one Ruby class seen in two representations, so a
                // parameter that has been both is still an Integer: carry it as a long.
                bool bothIntegral = (k == 1 || k == 4) && (_argKind[i] == 1 || _argKind[i] == 4);
                _argKind[i] = bothIntegral ? 4 : 3;
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
            lock (_sync) {
                Settle();
            }
            NotifyOwner();
            // Every method that gets here passed JitScreen, so each one is either a genuinely
            // type-dependent refusal or the screen and JitCompiler.Emit having drifted apart.
            // Name it, so a count that creeps up can be traced to the construct responsible.
            JitRuntime.Rejected++;
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit] declined {0}/{1} arity {2} (passed the pre-screen): {3}",
                    _declaringModule.Name ?? "main", _ast.Name, _arity, _declineReason ?? "no reason recorded");
            }
        }

        // IR_JIT_VERBOSE=1: why TrySpecialize gave up, for the `declined' line (GiveUp).
        private string _declineReason;

        // Under _sync: the generic body, for good.
        private void Settle() {
            _resting = false;
            _restTimer?.Dispose();
            _restTimer = null;
            _state = Settled;
            _current = null;
            _direct = null;
            Install(BestGeneric);
        }

        // How many times a method is specialized again after a method table change made its entry
        // stale. A program that keeps defining methods (a `def obj.x' per object, define_method in
        // a loop) would otherwise pay a compile every Threshold calls for as long as it runs.
        private const int MaxRespecializations = 8;
        // How many times deopts of an inlined call on a parameter sent the method back to profiling (OnDeopt).
        private const int MaxReprofiles = 2;
        private int _reprofiles;
        private int _respecializations;

        /// <summary>
        /// Called by the entry when the global method version no longer matches its snapshot, or a
        /// constant its typed bodies folded has changed (RevalidateConstants). That guard can never
        /// pass again, so leaving the entry installed would send every later call through the failed
        /// guard into the generic body: profile again and specialize under the new version instead,
        /// or settle on the generic body once that has happened too often.
        /// </summary>
        internal void VersionChanged(Specialization/*!*/ spec) {
            bool settled = false;
            lock (_sync) {
                settled = Respecialize(spec);
            }
            if (settled) { NotifyOwner(); }
        }

        /// <summary>
        /// Called by the entry when the constant version no longer matches the specialization's snapshot and the
        /// method version still does: what moved the constant version may be nothing the typed bodies folded (a
        /// constant set anywhere, an object extended, a module mixed in, an autoload started), as the generic
        /// constant sites find out by looking their constant up again. So does this: each constant the bodies
        /// folded is resolved again, the way the compiler resolved it (JitFoldedConstant.Resolve), and if every
        /// one is the same object found in the same module, the version read before the lookups becomes the
        /// snapshot and the typed body runs. False if one is not (or cannot be folded now: an autoload in
        /// flight, a deprecated constant), and the entry then goes stale as for a method change (EntryStale).
        ///
        /// No lock but the class hierarchy's, which each lookup takes as the compiler's did. The version is read
        /// before the first lookup and stored after the last: a constant changed in between has moved the
        /// version past the one stored, and the next entry looks again. Two threads may do this at once - the
        /// list never changes once installed - and one may store an older version than the other did, which only
        /// costs another look. A version is never stored that was read after a lookup.
        /// </summary>
        internal bool RevalidateConstants(Specialization/*!*/ spec) {
            var folded = spec.FoldedConstants;
            if (folded == null) { return false; }
            int version = System.Threading.Volatile.Read(ref _context.ConstantAccessVersion);
            foreach (var constant in folded) {
                if (!constant.StillHolds(_context)) { return false; }
            }
            // While an autoload runs, what a constant resolves to depends on the thread asking
            // (RubyContext._autoloadsInProgress): no version is recorded then. (One that started after the
            // version was read has moved it past what is stored here.)
            if (_context.IsAutoloadInProgress) { return false; }
            System.Threading.Volatile.Write(ref spec.ConstantVersion, version);
            // The bodies' loops check the constant version at their safe points too (JitLoopGuard.Stale): the
            // constants they folded are current at this version as well.
            foreach (var code in spec.FoldingCodes) {
                if (code.LoopGuard.ConstantsOf != null) {
                    System.Threading.Volatile.Write(ref code.LoopGuard.ConstantVersion, version);
                }
            }
            return true;
        }

        /// <summary>What the per-count bodies folded, all of them: they share one entry and one snapshot.</summary>
        private static void FoldedBy(Specialization/*!*/ spec, JitCode[]/*!*/ codes) {
            List<JitFoldedConstant> folded = null;
            List<JitCode> folding = null;
            foreach (var code in codes) {
                if (code == null || !code.UsesConstants) { continue; }
                (folding ?? (folding = new List<JitCode>())).Add(code);
                if (code.FoldedConstants == null) {
                    // (a body that folded constants it did not record cannot be looked up again)
                    return;
                }
                (folded ?? (folded = new List<JitFoldedConstant>())).AddRange(code.FoldedConstants);
            }
            if (folding == null) { return; }
            spec.FoldedConstants = folded.ToArray();
            spec.FoldingCodes = folding.ToArray();
        }

        // Under _sync: true if the method settled on its generic body.
        private bool Respecialize(Specialization/*!*/ spec) {
            if (_state != Specialized || spec != _current) { return false; }
            if (++_respecializations > MaxRespecializations) {
                Settle();
                return true;
            }
            // deoptimize: drop the specialized entry and let profiling start over
            JitRuntime.Invalidations++;
            _current = null;
            _direct = null;
            _calls = 0;
            _state = Profiling;
            Install(null);
            return false;
        }

        // ---- deopt rate ----------------------------------------------------------------------
        // A deopt costs a CLR exception, about 10 us, on top of the generic call it falls back to,
        // so an entry that deopts on every call (arithmetic that always outgrows Int64, say) made
        // its method several times slower than not specializing it at all, for as long as the
        // program ran. A deopt now and then, though, is paid for by what the typed body saves on
        // the other calls, and how rare a deopt has to be for that depends on the body: deopting
        // on 1 call in K, a chain of Integer statements broke even with its generic body at K of
        // about 34 for 3 statements, 14 for 20, 8 for 40, 4 for 80 and 3 for 160. No one rate is
        // right for all of them; retiring at 1 call in 32, the first policy here, made a
        // 40-statement body 2x slower than the base, which never retires an entry.
        //
        // So the stub measures. The first deopt in a while opens a window of DeoptWindow entries
        // into the typed body, which the entry counts down (Countdown), and each deopt in it is
        // timed from the throw to the entry's catch. At the window's MaxDeopts'th deopt - n
        // deopts, the first one opening the window, over m entries - the entry is retired if
        //     (n - 1) * deopt > 1.25 * (m - (n - 1)) * generic
        // with deopt the least a deopt has taken in any window and generic the least median of the
        // generic calls Cool timed: while the entry was retired or, before it ever was, in a short
        // Probe once the generic body is compiled (the light compiler interprets its first calls
        // and compiles it in the background), repeated a few times as the runtime warms up. That
        // is the break-even of staying specialized as if the typed body cost nothing, with a
        // margin. A real one costs something, so the rule retires an entry only when its deopts
        // cost more than the whole generic body would on the calls that do not deopt: it keeps
        // some that lose a little (20 statements deopting on 1 call in 10-14), as the base keeps
        // them all, and does not retire one that pays its way. The least readings are the ones
        // to compare, because the two are timed at different moments, and either comes out up
        // to twice as high now and then - deopts took 10 us in most windows of a run and 16-23 us
        // in bursts of others, which retired entries that paid for themselves. A window that
        // runs out first just closes, and the entry counts nothing until the next deopt: a method
        // that does not deopt writes nothing on its calls that the threads calling it share.
        //
        // A retired entry is tried again later, because the deopts may come from one phase of the
        // input - large values while a program sets up, small ones for the millions of calls after
        // that - and retiring it for good then made a 10-statement method 3x slower for the rest of
        // the process. The method keeps its compiled entry and runs the generic body until both
        // CoolCheckCalls generic calls and a cool-down have passed; the cool-down starts at
        // CoolDownMs and doubles at each retirement. A retry that fails again costs one more window
        // of deopts, about 0.2 ms, against a cool-down of at least 8 ms; after MaxRetirements the
        // method keeps its generic body for good.
        //
        // The cool-down costs nothing per call (StartResting): the fast slot holds the generic body
        // itself, as a settled method's does, and a timer ends it with a probe (ProbeCalls generic
        // calls, each timed), whose CoolDownCheck puts the entry back. A stand-in that counted every
        // call and timed one in 8 for the whole cool-down cost a method that keeps deopting 10-16%
        // over the generic body for the two seconds before it settled (perf/jitsettle).
        internal const int MaxDeopts = 16;
        private const int DeoptWindow = MaxDeopts * 64;
        private const int MaxRetirements = 8;
        private const int CoolDownMs = 8;
        private const int CoolCheckCalls = 1024;
        private const int CoolSampleMask = 7;    // Cool times one generic call in 8, at random
        private const int ProbeCalls = 2 * MaxDeopts;  // and every one in a probe
        private const int MaxProbes = 6;
        private const int ProbeIntervalMs = 100;
        private const int MaxProbeDelays = 8;

        private int _retirements;
        private long _coolUntil;          // Stopwatch timestamp: the end of the cool-down
        protected int _coolCalls;         // generic calls until CoolDownCheck looks at the time
        // The latest generic calls Cool timed (a ring of MaxDeopts), which ones it times, and the
        // least median of them taken when the entry was put back; in Stopwatch ticks.
        private long[] _coolSamples;
        private int _coolSampleCount;
        // The same of the timed generic calls that raised. Their cost stands for the generic body's only when no timed call
        // returned and none ever has (CoolDownCheck): a method whose every call raises (a `raise' is a deopt, JitCompiler.EmitRaise)
        // would otherwise never have a generic cost, and an entry is never retired without one.
        private long[] _coolRaised;
        private int _coolRaisedCount;
        private int _coolMask;
        private uint _coolRandom = 0x9E3779B9;
        private long _genericCost;
        private long _deoptCost;          // the least a deopt took in any window, in Stopwatch ticks
        private int _probes;
        private long _probeAfter;         // Stopwatch timestamp: no probe before it
        // Set once the light compiler has compiled the generic body (Wrap), or from the start if it was
        // never interpreted. A probe waits for it, for at most MaxProbeDelays windows.
        private volatile bool _genericCompiled;
        private int _probeDelays;
        // _genericCost was timed before the generic body was compiled (GenericCompiled forgets it).
        private bool _genericCostInterpreted;
        // When GenericCompiled ran (Stopwatch ticks), and the probes taken before the compiled body warmed up (Probe).
        private long _compiledAt;
        private int _warmProbes;
        private const int WarmMs = 100;
        private const int WarmProbeIntervalMs = 10;
        private const int MaxWarmProbes = 16;

        private static MSA.Expression/*!*/ CountEntry(Specialization/*!*/ spec) {
            var countdown = Ast.Field(Ast.Constant(spec), Specialization.CountdownField);
            return Ast.IfThen(Ast.GreaterThan(countdown, Ast.Constant(0)), Ast.PreDecrementAssign(countdown));
        }

        /// <summary>
        /// Called by an entry (and the direct entry) when the typed body deopted and the call is
        /// being re-run generically, with what the exception cost (-1 if unknown): retires the
        /// entry once its deopts cost more than its typed body saves (see MaxDeopts).
        /// </summary>
        internal void OnDeopt(Specialization/*!*/ spec, long deoptCost) {
            if (_state != Specialized || spec != _current) { return; }
            if (spec.Countdown <= 0) {
                // the first deopt in a while: open a window
                spec.WindowDeopts = 1;
                spec.DeoptCost = (deoptCost > 0) ? deoptCost : 0;
                spec.Countdown = DeoptWindow;
                return;
            }
            // Not interlocked, like the rest of a window: a deopt racing with another on a second
            // thread can lose its count or its cost, which only blurs a rate.
            if (deoptCost > 0 && (spec.DeoptCost == 0 || deoptCost < spec.DeoptCost)) { spec.DeoptCost = deoptCost; }
            if (++spec.WindowDeopts < MaxDeopts) { return; }

            int entries = DeoptWindow - spec.Countdown;
            int deopts = spec.WindowDeopts - 1;
            if (spec.DeoptCost > 0 && (_deoptCost == 0 || spec.DeoptCost < _deoptCost)) { _deoptCost = spec.DeoptCost; }
            long deopt = _deoptCost;
            long generic = _genericCost;
            // close the window; the next deopt opens another
            spec.Countdown = 0;
            if (deopt <= 0) { return; }
            if (generic <= 0 || 4 * deopts * deopt <= 5 * (entries - deopts) * generic) {
                if (generic > 0 && JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] kept {0}/{1} arity {2}: {3} deopts in {4} calls ({5})",
                        _declaringModule.Name ?? "main", _ast.Name, _arity, spec.WindowDeopts, entries, Costs(deopt, generic));
                }
                // Time the generic body: it has not been yet, or it may have got faster since - the
                // runtime methods it calls start out in the CLR's quick first-tier code, which is
                // replaced some 100 ms on.
                if (_probes < MaxProbes && Stopwatch.GetTimestamp() >= _probeAfter && (_genericCompiled || ++_probeDelays > MaxProbeDelays)) {
                    Probe(spec, entries, deopt);
                }
                return;
            }

            if (spec.Code != null && spec.Code.InlinedOnArgumentClass && _reprofiles < MaxReprofiles) {
                // An inlined call on a parameter checks the one class the profile saw it hold (TryInlineCall), and
                // the calls since have mostly had another: profile again, adding the classes seen now, and compile
                // again, rather than retire a body that a second class would have fitted.
                bool settled;
                lock (_sync) {
                    if (_state != Specialized || spec != _current) { return; }
                    _reprofiles++;
                    if (JitRuntime.Verbose) {
                        Console.Error.WriteLine("[jit] profiling {0}/{1} arity {2} again: {3} deopts in {4} calls",
                            _declaringModule.Name ?? "main", _ast.Name, _arity, spec.WindowDeopts, entries);
                    }
                    settled = Respecialize(spec);
                }
                if (settled) { NotifyOwner(); }
                return;
            }

            lock (_sync) {
                if (_state != Specialized || spec != _current) { return; }
                JitRuntime.DeoptRetired++;
                bool final = ++_retirements > MaxRetirements;
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] retired {0}/{1} arity {2}: {3} deopts in {4} calls ({5}){6}",
                        _declaringModule.Name ?? "main", _ast.Name, _arity, spec.WindowDeopts, entries, Costs(deopt, generic),
                        final ? ", for good" : String.Format(", retry in {0} ms", CoolDownMs << (_retirements - 1)));
                }
                if (!final) {
                    if (RestEnabled) {
                        StartResting(CoolDownMs << (_retirements - 1));
                    } else {
                        StartCooling(Stopwatch.GetTimestamp() + (Stopwatch.Frequency * CoolDownMs / 1000) * (1L << (_retirements - 1)),
                            CoolCheckCalls, CoolSampleMask);
                    }
                    return;
                }
                Settle();
            }
            NotifyOwner();
        }

        /// <summary>
        /// Time the generic body: run it for ProbeCalls calls, timing each, then put the entry back.
        /// A probe costs what the typed body would have saved on those calls; there are at most
        /// MaxProbes, ProbeIntervalMs apart and then twice as far each time.
        /// </summary>
        private void Probe(Specialization/*!*/ spec, int entries, long deopt) {
            lock (_sync) {
                if (_state != Specialized || spec != _current || _probes >= MaxProbes) { return; }
                // While the generic body is not compiled, or compiled WarmMs ago or less - its first calls run the CLR's
                // quick first-tier code - a probe reads it high, which keeps an entry that deopts on every other call
                // (1.75 us a generic call 2 ms after the compile, 0.12 at 100 ms; perf/jitprobe). Such a probe takes
                // none of the MaxProbes, and the next may come WarmProbeIntervalMs on: the cost is the least any probe
                // read, so it comes down to the warm one's soon. At most MaxWarmProbes of them.
                long now = Stopwatch.GetTimestamp();
                bool warm = _warmProbes < MaxWarmProbes &&
                    (!_genericCompiled || now - Volatile.Read(ref _compiledAt) < Stopwatch.Frequency * WarmMs / 1000);
                if (warm) {
                    _warmProbes++;
                    _probeAfter = now + Stopwatch.Frequency * WarmProbeIntervalMs / 1000;
                } else {
                    _probeAfter = now + (Stopwatch.Frequency * ProbeIntervalMs / 1000) * (1L << _probes);
                    _probes++;
                }
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] timing the generic body of {0}/{1} arity {2}: {3} deopts in {4} calls (deopt {5:F1} us){6}",
                        _declaringModule.Name ?? "main", _ast.Name, _arity, spec.WindowDeopts, entries, deopt * 1e6 / Stopwatch.Frequency,
                        _genericCompiled ? "" : ", not known to be compiled");
                }
                StartCooling(0, ProbeCalls, 0);
            }
        }

        // A retired entry's cool-down (OnDeopt): the generic body itself in the fast slot, nothing
        // counted or timed, until a timer starts the probe that ends it (EndRest). _restGeneration
        // names the rest a timer was set for, so that one left over from an earlier rest - the state
        // moved on, and back to Cooling - does nothing.
        // IR_JIT_REST=0: the counted, sampled cool-down of before (StartCooling for the whole of it).
        internal static readonly bool RestEnabled = Environment.GetEnvironmentVariable("IR_JIT_REST") != "0";
        private bool _resting;
        private int _restGeneration;
        private Timer _restTimer;

        // Under _sync.
        private void StartResting(int ms) {
            _direct = null;
            _state = Cooling;
            _resting = true;
            int generation = ++_restGeneration;
            Install(BestGeneric);
            _restTimer?.Dispose();
            _restTimer = new Timer(state => ((JitStub)state).EndRest(generation), this, ms, Timeout.Infinite);
        }

        private void EndRest(int generation) {
            lock (_sync) {
                if (_state != Cooling || !_resting || generation != _restGeneration) { return; }
                // time the generic body on the next calls, then put the entry back (CoolDownCheck)
                StartCooling(0, ProbeCalls, 0);
            }
        }

        // Under _sync: the generic body until `until' and `calls' generic calls have passed, timing
        // the calls Cool picks with `mask'.
        private void StartCooling(long until, int calls, int mask) {
            _resting = false;
            _restTimer?.Dispose();
            _restTimer = null;
            _direct = null;
            _coolUntil = until;
            _coolCalls = calls;
            _coolMask = mask;
            _coolSamples ??= new long[MaxDeopts];
            _coolSampleCount = 0;
            _coolRaisedCount = 0;
            _state = Cooling;
            Install(CoolingBody);
        }

        private static long Median(long[]/*!*/ costs, int count) {
            var sorted = new long[count];
            Array.Copy(costs, sorted, count);
            Array.Sort(sorted);
            return sorted[count / 2];
        }

        private static string/*!*/ Costs(long deopt, long generic) {
            return String.Format("deopt {0:F1} us, generic {1:F2} us",
                deopt * 1e6 / Stopwatch.Frequency, generic * 1e6 / Stopwatch.Frequency);
        }

        /// <summary>
        /// Whether the cooling stand-in is to time this call: one in CoolSampleMask + 1, picked at
        /// random so that the calls timed are not in step with a pattern of the program's own.
        /// </summary>
        protected bool CoolPick() {
            uint r = _coolRandom;
            r ^= r << 13; r ^= r >> 17; r ^= r << 5;
            _coolRandom = r;
            return (r & _coolMask) == 0;
        }

        /// <summary>Called by the cooling stand-in with the start of a generic call it timed.</summary>
        protected void CoolSample(long start) {
            long cost = Stopwatch.GetTimestamp() - start;
            var samples = _coolSamples;
            int i = _coolSampleCount;
            if (samples != null) {
                samples[i & (MaxDeopts - 1)] = cost;
                _coolSampleCount = i + 1;
            }
        }

        /// <summary>Called, as an exception filter, by the cooling stand-in when a generic call it timed raised; never catches.</summary>
        protected bool CoolRaised(long start) {
            long cost = Stopwatch.GetTimestamp() - start;
            var samples = _coolRaised ??= new long[MaxDeopts];
            int i = _coolRaisedCount;
            samples[i & (MaxDeopts - 1)] = cost;
            _coolRaisedCount = i + 1;
            return false;
        }

        /// <summary>
        /// Called by the cooling stand-in every CoolCheckCalls calls (ProbeCalls in a probe): puts
        /// the entry back once its cool-down is over.
        /// </summary>
        protected void CoolDownCheck() {
            lock (_sync) {
                if (_state != Cooling) { return; }
                _coolCalls = CoolCheckCalls;
                if (Stopwatch.GetTimestamp() < _coolUntil) { return; }

                if (_coolSampleCount > 0) {
                    long median = Median(_coolSamples, Math.Min(_coolSampleCount, MaxDeopts));
                    if (_genericCost == 0 || median < _genericCost) { _genericCost = median; _genericCostInterpreted = !_genericCompiled; }
                } else if (_coolRaisedCount > 0 && _genericCost == 0) {
                    _genericCost = Median(_coolRaised, Math.Min(_coolRaisedCount, MaxDeopts));
                    _genericCostInterpreted = !_genericCompiled;
                }
                var spec = _current;
                spec.Countdown = 0;
                spec.WindowDeopts = 0;
                JitRuntime.DeoptRetried++;
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] retrying {0}/{1} arity {2} (generic {3:F2} us)", _declaringModule.Name ?? "main", _ast.Name, _arity,
                        _genericCost * 1e6 / Stopwatch.Frequency);
                }
                _state = Specialized;
                _direct = spec.Direct;
                Install(spec.Entry);
            }
        }

        private bool TrySpecialize() {
            // The refinements the body's calls see. A record that follows the declaring scope rather than
            // naming the refinements (an imported refinement method's) is not something to compile against.
            RefinementActivation refinements = RefinementActivation.Empty;
            if (_definitionRefinements != null) {
                refinements = _definitionRefinements.Value;
                if (refinements == null) { return false; }
            }
            if (_perCountMandatory >= 0) { return TrySpecializePerCount(refinements); }

            var types = ProfiledTypes(_typedCount);

            // The method version the entry guards on is read before compiling, so that it is never
            // newer than what the compiler resolved against - each built-in operator it checks, and
            // the self-call check below. Read after, a method redefined (on another thread) while a
            // large body compiled would be inside the snapshot, and the entry would go on running
            // the operator it replaced. The code epoch the body's loops start from is read before both
            // (JitLoopGuard.Set).
            long epoch = RubyUtils.CodeEpoch;
            int version = RubyModule.GlobalMethodVersion;
            int constantVersion = _context.ConstantAccessVersion;

            long start = Stopwatch.GetTimestamp();
            var hints = Hints();
            var code = Named(JitCompiler.TryCompile(_ast, _context, types, _keywords, refinements, -1, _declaringScope, hints,
                Callees(refinements, version, constantVersion), epoch), epoch, version, constantVersion);
            if (code == null) {
                // A call the body could inline - or, with -X:JITCalls, bind - is of a method that has not been
                // specialized yet: a caller called once per call of its callee turns hot first. Profile on, a
                // few times, rather than settling on the generic body for good.
                if ((hints.CalleeNotReady || _calleeNotReady) && ++_inlineWaits <= MaxInlineWaits) {
                    if (JitRuntime.Verbose) {
                        Console.Error.WriteLine("[jit] waiting for a callee of {0}/{1} to be specialized", _declaringModule.Name ?? "main", _ast.Name);
                    }
                    return true;
                }
                // (a bailout has printed its `rejected' line already)
                _declineReason = JitCompiler.LastDecline ?? "the compiler bailed out (see the `rejected' line)";
                return false;
            }

            // (a body with direct calls was only compiled for a receiver class the profile pinned: Callees)
            bool needsClassGuard = code.EmittedSelfCall || code.InlinedSelfClass != null || code.DirectCalls;
            if (needsClassGuard) {
                if (_receiverClass == null || _polymorphicReceiver) { _declineReason = "a class guard on a receiver of several classes"; return false; }
                if (code.EmittedSelfCall && !SelfCallResolvesHere(_receiverClass, refinements)) { _declineReason = "the self-call resolves elsewhere"; return false; }
                // the inlined self-calls were resolved on the class of the sampled receiver
                if (code.InlinedSelfClass != null && code.InlinedSelfClass != _receiverClass) { _declineReason = "an inlined self-call of another class"; return false; }
            }

            // The typed body only has to exist as a delegate of its own when it calls itself: the
            // direct self-call goes through SelfCell. Otherwise the entry invokes the lambda
            // itself, which the expression compiler inlines, so a specialized call is the
            // trampoline hop and the entry and nothing more.
            // -X:JITMethods: a body that calls other typed bodies is a method next to theirs too, so
            // that those calls are made from code the runtime compiles as it compiles them.
            MSA.Expression callee;
            Delegate typed = null;
            if (code.EmittedSelfCall || (code.DirectCalls && JitMethodEmitter.For(_context) != null)) {
                typed = CompileTyped(code);
                callee = Ast.Constant(typed, typed.GetType());
            } else {
                callee = code.Lambda;
            }

            var spec = new Specialization(this);
            spec.ConstantVersion = constantVersion;
            if (code.UsesConstants) {
                spec.FoldedConstants = code.FoldedConstants;
                spec.FoldingCodes = new[] { code };
            }
            spec.Code = code;
            spec.Version = version;
            spec.PinnedClass = needsClassGuard ? _receiverClass : null;
            spec.Typed = typed;
            if (code.Resume != null) { code.Resume.Specialization = spec; }
            spec.Entry = CompileEntry(BuildEntry(code, callee, version, needsClassGuard ? _receiverClass : null, spec));
            if (_keywords != null && !needsClassGuard && _positional <= JitKeywordCall.MaxPositional &&
                _keywords.Count <= JitKeywordCall.MaxKeywords) {
                spec.Direct = BuildDirectEntry(code, callee, version, spec).Compile();
            }

            lock (_sync) {
                // Nothing but this thread, which holds _compiling, takes the stub out of Profiling;
                // the test is only there to keep it that way.
                if (_state != Profiling) { return true; }
                _current = spec;
                _state = Specialized;
                Install(spec.Entry);
                _direct = spec.Direct;
            }

            JitRuntime.CompileTicks += Stopwatch.GetTimestamp() - start;
            JitRuntime.Specialized++;
            if (code.DirectCalls) { JitRuntime.DirectCallBodies++; }
            if (code.Resume != null) { JitRuntime.ResumeBodies++; }
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit] specialized {0}/{1} arity {2} -> {3}{4}{5}{6}",
                    _declaringModule.Name ?? "main", _ast.Name, _arity, code.ReturnType, code.EmittedSelfCall ? " (self-call)" : "",
                    code.DirectCalls ? " (direct calls)" : "", (code.Resume != null) ? " (fallbacks: " + code.FallbackSites + " sites)" : "");
            }
            return true;
        }

        // ---- -X:JITCalls ---------------------------------------------------------------------

        /// <summary>
        /// What the compiler asks for the typed body of a method this one calls on self: the name is
        /// resolved from the receiver class the profile saw - which the entry then pins - as a call
        /// without a receiver resolves it, under the refinements the body's calls see. Null when the
        /// class is not pinned down; the body then binds no call, as without the switch.
        /// </summary>
        private Func<string, int, JitCallee> Callees(RefinementActivation/*!*/ refinements, int version, int constantVersion) {
            _calleeNotReady = false;
            if (!_directCalls || _receiverClass == null || _polymorphicReceiver) { return null; }
            var cls = _receiverClass;
            // the compiler asks again in each of its rounds
            var found = new Dictionary<string, JitCallee>();
            return (name, argc) => {
                string key = name + "/" + argc;
                JitCallee callee;
                if (!found.TryGetValue(key, out callee)) {
                    found[key] = callee = FindCallee(cls, refinements, name, argc, version, constantVersion);
                }
                return callee;
            };
        }

        private JitCallee FindCallee(RubyClass/*!*/ cls, RefinementActivation/*!*/ refinements, string/*!*/ name, int argc, int version,
            int constantVersion) {
            var resolved = cls.ResolveMethod(name, VisibilityContext.AllVisible, refinements);
            if (!resolved.Found) { return null; }
            var info = resolved.Info as RubyMethodInfo;
            if (info == null) { return null; }
            // a method the JIT took has its trampoline for a body
            var stub = info.GetDelegate().Target as JitStub;
            if (stub == null || stub == this || stub._context != _context) { return null; }
            bool retry;
            var callee = stub.BindCallee(cls, argc, version, constantVersion, out retry);
            if (callee == null && retry) { _calleeNotReady = true; }
            if (JitRuntime.Verbose) {
                Console.Error.WriteLine("[jit]   {0} -> {1}/{2}: {3}", _ast.Name, name, argc,
                    (callee == null) ? (retry ? "no typed body yet" : "no typed body") : "bound");
            }
            return callee;
        }

        /// <summary>
        /// This method's typed body for a caller whose receiver is of <paramref name="receiverClass"/>
        /// and whose entry guards on <paramref name="version"/> and, if the body folded constants,
        /// <paramref name="constantVersion"/> - the versions this body must have been compiled under,
        /// since the caller's entry is the only guard a direct call passes. Null if there is no such
        /// body; <paramref name="retry"/> says there may be one later: the method is still profiling
        /// or cooling down, or its entry has gone stale, which its next call finds out. As
        /// InlinableBody, but of a body that could not be made part of its caller.
        /// </summary>
        internal JitCallee BindCallee(RubyClass/*!*/ receiverClass, int argc, int version, int constantVersion, out bool retry) {
            retry = false;
            // the body takes exactly the arguments the call passes, in order
            if (_keywords != null || _perCountMandatory >= 0 || argc != _arity) { return null; }
            Specialization spec;
            int state;
            lock (_sync) {
                state = _state;
                spec = (state == Specialized) ? _current : null;
            }
            if (spec == null) {
                retry = state != Settled;
                return null;
            }
            var code = spec.Code;
            if (code == null) { return null; }
            // The caller's entry guards on constantVersion, and looks up again what this body folded (the caller
            // takes it over: JitCompiler.EmitDirectCall), so the body must be current at that version: if its own
            // snapshot is older, the constants it folded are looked up now, which records the version they were
            // found current at. A newer one (constants changed since the caller read constantVersion), or a
            // constant that changed, and the caller does not bind it.
            if (spec.Version != version ||
                (code.UsesConstants && System.Threading.Volatile.Read(ref spec.ConstantVersion) != constantVersion &&
                 !(RevalidateConstants(spec) && System.Threading.Volatile.Read(ref spec.ConstantVersion) == constantVersion))) {
                retry = true;
                return null;
            }
            // Element writes are undone by the entry's undo log, or are the last thing the body does; a
            // construction runs an initialize, which may redefine what the caller has bound. A body with
            // fallbacks (-X:JITResume) takes the block and makes its frame, which its entry is for.
            if (code.WritesElements || code.Construction || code.Resume != null) { return null; }
            // The body's own inlined and direct calls were resolved from the class its entry pins.
            if (spec.PinnedClass != null && spec.PinnedClass != receiverClass) { return null; }

            Delegate typed;
            lock (spec) {
                // (a body that does not call itself was made part of its entry, and not compiled on its own)
                typed = spec.Typed ?? (spec.Typed = CompileTyped(code));
            }
            return new JitCallee { Code = code, Target = Ast.Constant(typed, typed.GetType()) };
        }

        /// <summary>
        /// A typed body that calls itself, as a delegate. -X:JITMethods: a static method of a runtime
        /// assembly whose self-calls are calls (JitMethodEmitter), and the cell is then not read at all.
        /// </summary>
        private Delegate/*!*/ CompileTyped(JitCode/*!*/ code) {
            var emitter = JitMethodEmitter.For(_context);
            Delegate typed = (emitter != null) ? emitter.EmitTyped(code) : null;
            if (typed == null) { typed = code.Lambda.Compile(); }
            code.SelfCell.Value = typed;
            return typed;
        }

        private Delegate/*!*/ CompileEntry(MSA.LambdaExpression/*!*/ entry) {
            var emitter = JitMethodEmitter.For(_context);
            Delegate result = (emitter != null && emitter.Entries) ? emitter.EmitEntry(entry) : null;
            return result ?? entry.Compile();
        }

        // How many times TrySpecialize went on profiling for a callee to be specialized (ReceiverHints.CalleeNotReady).
        private const int MaxInlineWaits = 3;
        private int _inlineWaits;

        /// <summary>The method is still being profiled: it may yet be specialized.</summary>
        internal bool IsProfiling {
            get { return _state == Profiling; }
        }

        private JitCompiler.ReceiverHints/*!*/ Hints() {
            return new JitCompiler.ReceiverHints { Parameters = (Type[])_argClrType.Clone(), Self = _selfClrType, SelfSample = _selfSample, ArgSamples = _argSample,
                ArgClasses = ArgClasses(), Resume = ResumeInfo() };
        }

        /// <summary>
        /// -X:JITResume: what the typed body needs to fall back to an operation's call after its first effect
        /// (JitResume.cs), for a body stage 1 takes - no parameters, no locals (JitCompiler.IsResumeCandidate) - of a
        /// method whose generic frame it can stand for: one compiled from its source, with a frame name (Named), a
        /// declaring scope and no refinements recorded. Null otherwise, and the body is compiled as without the switch.
        /// </summary>
        private JitResumeInfo ResumeInfo() {
            if (!_context.RubyOptions.JitResume || _frameName == null || _declaringScope == null || _definitionRefinements != null ||
                _keywords != null || _arity != 0 || _hasDefault != null || !JitCompiler.IsResumeCandidate(_ast)) {
                return null;
            }
            string path = JitResumeInfo.PathOf(_frameName);
            if (path == null) { return null; }
            return new JitResumeInfo(_frameName, path, _ast.Location.End.Line, _ast.Name, _declaringScope, _declaringModule);
        }

        private RubyClass[][]/*!*/ ArgClasses() {
            var result = new RubyClass[_typedCount][];
            for (int i = 0; i < _typedCount; i++) {
                if (_argManyClasses[i] || _argClass1[i] == null) { continue; }
                result[i] = (_argClass2[i] == null) ? new[] { _argClass1[i] } : new[] { _argClass1[i], _argClass2[i] };
            }
            return result;
        }

        private JT[]/*!*/ ProfiledTypes(int count) {
            var types = new JT[count];
            for (int i = 0; i < count; i++) {
                if (_storedOnly != null && _storedOnly[i]) { types[i] = JT.Obj; continue; }
                switch (_argKind[i]) {
                    case 1: types[i] = JT.Int; break;
                    case 2: types[i] = JT.Dbl; break;
                    case 4: types[i] = JT.Lng; break;
                    // never passed: the typed body types the keyword by its default
                    case 0: types[i] = PassedBoxed(i) ? JT.None : JT.Obj; break;
                    default: types[i] = JT.Obj; break;
                }
            }
            return types;
        }

        /// <summary>
        /// A keyword whose default the typed body evaluates (JitKeywords.DefaultExprs): the body takes the
        /// boxed value, or RubyOps.DefaultArgument, which the entry's type guard lets through.
        /// </summary>
        private bool PassedBoxed(int i) {
            return _keywords != null && i >= _positional && _keywords.HasDefaultExpr(i - _positional);
        }

        private MSA.Expression/*!*/ AllowDefault(int i, MSA.Expression/*!*/ value, MSA.Expression/*!*/ guard) {
            return PassedBoxed(i)
                ? Ast.OrElse(Ast.ReferenceEqual(value, Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"))), guard)
                : guard;
        }

        /// <summary>
        /// One typed body for each number of optional arguments the calls so far have passed; a call
        /// passing a number no body was compiled for runs the generic body.
        /// </summary>
        private bool TrySpecializePerCount(RefinementActivation/*!*/ refinements) {
            // read before compiling, as in TrySpecialize
            long epoch = RubyUtils.CodeEpoch;
            int version = RubyModule.GlobalMethodVersion;
            int constantVersion = _context.ConstantAccessVersion;

            long start = Stopwatch.GetTimestamp();
            int optionalCount = _positional - _perCountMandatory;
            var codes = new JitCode[optionalCount + 1];
            var callees = new MSA.Expression[optionalCount + 1];
            bool any = false, pinned = false;
            var bind = Callees(refinements, version, constantVersion);
            for (int k = 0; k <= optionalCount; k++) {
                if ((_countsSeen & (1 << k)) == 0) { continue; }
                var code = Named(JitCompiler.TryCompile(_ast, _context, ProfiledTypes(_perCountMandatory + k), null, refinements, k, _declaringScope, Hints(),
                    bind, epoch), epoch, version, constantVersion);
                if (code == null) { continue; }
                if (code.EmittedSelfCall) {
                    if (_receiverClass == null || _polymorphicReceiver || !SelfCallResolvesHere(_receiverClass, refinements)) { continue; }
                    pinned = true;
                }
                if (code.DirectCalls) { pinned = true; }
                codes[k] = code;
                any = true;
            }
            if (!any) { return false; }

            for (int k = 0; k <= optionalCount; k++) {
                var code = codes[k];
                if (code == null) { continue; }
                // as in TrySpecialize: a body of its own only when it calls itself
                if (code.EmittedSelfCall || (code.DirectCalls && JitMethodEmitter.For(_context) != null)) {
                    Delegate typed = CompileTyped(code);
                    callees[k] = Ast.Constant(typed, typed.GetType());
                } else {
                    callees[k] = code.Lambda;
                }
            }

            // one Specialization for all the bodies: they retire, cool down and go stale together
            var spec = new Specialization(this);
            spec.ConstantVersion = constantVersion;
            FoldedBy(spec, codes);
            spec.Entry = CompileEntry(BuildPerCountEntry(codes, callees, version, pinned ? _receiverClass : null, spec));

            lock (_sync) {
                // as in TrySpecialize
                if (_state != Profiling) { return true; }
                _current = spec;
                _state = Specialized;
                Install(spec.Entry);
            }

            JitRuntime.CompileTicks += Stopwatch.GetTimestamp() - start;
            JitRuntime.Specialized++;
            // `pinned' is also a self-call's
            foreach (var code in codes) {
                if (code != null && code.DirectCalls) { JitRuntime.DirectCallBodies++; break; }
            }
            if (JitRuntime.Verbose) {
                for (int k = 0; k <= optionalCount; k++) {
                    if (codes[k] == null) { continue; }
                    Console.Error.WriteLine("[jit] specialized {0}/{1} arity {2} passing {3} optional -> {4}{5}{6}",
                        _declaringModule.Name ?? "main", _ast.Name, _arity, k, codes[k].ReturnType, codes[k].EmittedSelfCall ? " (self-call)" : "",
                        codes[k].DirectCalls ? " (direct calls)" : "");
                }
            }
            return true;
        }

        /// <summary>
        /// The direct self-call is only correct if the name still resolves, from the profiled
        /// receiver class and under the body's refinements, to this very body.
        /// </summary>
        /// <summary>
        /// The typed body a caller compiled now may inline in place of a call of this method on an instance of
        /// <paramref name="receiverClass"/> (JitCompiler.TryInlineCall), or null. It is the body the call would
        /// enter now: the method specialized, its entry's method version the current one (the caller's entry
        /// checks one no newer, so the two go stale together), and any class it pins self to this one.
        /// </summary>
        internal JitCode InlinableBody(RubyContext/*!*/ context, RubyClass/*!*/ receiverClass) {
            if (context != _context || _perCountMandatory >= 0 || _keywords != null) { return null; }
            Specialization spec;
            lock (_sync) {
                if (_state != Specialized) { return null; }
                spec = _current;
            }
            if (spec == null || spec.Code == null || !spec.Code.Inlinable) { return null; }
            if (spec.Version != RubyModule.GlobalMethodVersion) { return null; }
            if (spec.PinnedClass != null && spec.PinnedClass != receiverClass) { return null; }
            return spec.Code;
        }

        private bool SelfCallResolvesHere(RubyClass/*!*/ cls, RefinementActivation/*!*/ refinements) {
            var resolved = cls.ResolveMethod(_ast.Name, VisibilityContext.AllVisible, refinements);
            if (!resolved.Found) { return false; }
            var info = resolved.Info as RubyMethodInfo;
            if (info == null) {
                // a method made by define_method calling itself: the def its block was taken as (JitBlockMethod)
                var lambda = resolved.Info as RubyLambdaMethodInfo;
                return lambda != null && ReferenceEquals(lambda.JitAst, _ast);
            }
            return ReferenceEquals(info.GetSyntaxTree(), _ast);
        }

        // ---- entry lambda ------------------------------------------------------------------

        private MSA.LambdaExpression/*!*/ BuildEntry(JitCode/*!*/ code, MSA.Expression/*!*/ callee, int version, RubyClass pinnedClass,
            Specialization/*!*/ spec) {
            var self = Ast.Parameter(typeof(object), "self");
            var blk = Ast.Parameter(typeof(Proc), "block");
            var args = new MSA.ParameterExpression[_arity];
            for (int i = 0; i < _arity; i++) {
                args[i] = Ast.Parameter(typeof(object), "a" + i);
            }

            // The values the typed parameters are made of: the positional arguments - an omitted
            // optional one being its default - then, for a method with keywords, what the prologue
            // would bind each keyword to. The generic body is still handed the arguments as they came.
            var values = new MSA.Expression[_typedCount];
            var defaulted = new List<MSA.ParameterExpression>();
            var fillDefaults = new List<MSA.Expression>();
            if (_keywords != null && _keywords.OptionalPositionals > 0) {
                // a keyword hash that landed in an optional positional is moved to where it belongs,
                // as the generic body would move it (JitKeywords.ShiftKeywords) - which it then finds
                // already done, should the guards below send the call there
                var defaultArgument = Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"));
                MSA.Expression chain = Ast.Empty();
                for (int i = _positional - _keywords.OptionalPositionals; i < _positional; i++) {
                    chain = Ast.IfThenElse(
                        Ast.NotEqual(args[i], defaultArgument),
                        Ast.IfThen(
                            Ast.Call(typeof(RubyOps).GetMethod("IsKeywordArgumentsHash"), args[i]),
                            Ast.Block(Ast.Assign(args[_positional], args[i]), Ast.Assign(args[i], defaultArgument))),
                        chain);
                }
                fillDefaults.Add(Ast.IfThen(Ast.Equal(args[_positional], defaultArgument), chain));
            }
            for (int i = 0; i < _positional; i++) {
                if (_hasDefault != null && _hasDefault[i]) {
                    var v = Ast.Variable(typeof(object), "v" + i);
                    values[i] = v;
                    defaulted.Add(v);
                    fillDefaults.Add(Ast.Assign(v, Ast.Condition(
                        Ast.ReferenceEqual(args[i], Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"))),
                        Ast.Constant(_defaults[i], typeof(object)),
                        args[i]
                    )));
                } else {
                    values[i] = args[i];
                }
            }

            MSA.Expression test = NotTracing();
            if (pinnedClass != null) {
                test = And(test, Ast.ReferenceEqual(
                    Ast.Call(JitRuntime.M("ClassOf"), Ast.Constant(_context), self),
                    Ast.Constant(pinnedClass, typeof(RubyClass))));
            }

            MSA.ParameterExpression[] keywordValues = null;
            if (_keywords != null) {
                MSA.Expression kw = args[_positional];
                // the separation check on the last mandatory positional: `m("a" => 1)' gave it the
                // keywords (an optional one cannot hold them any more, they were moved above)
                int lastMandatory = _positional - _keywords.OptionalPositionals - 1;
                if (lastMandatory >= 0 && code.ParameterTypes[lastMandatory] == JT.Obj) {
                    test = And(test, Ast.Not(Ast.Call(typeof(RubyOps).GetMethod("IsKeywordArgumentsHash"), args[lastMandatory])));
                }
                keywordValues = new MSA.ParameterExpression[_keywords.Count];
                for (int j = 0; j < keywordValues.Length; j++) {
                    values[_positional + j] = keywordValues[j] = Ast.Variable(typeof(object), "kw" + j);
                }
                var info = Ast.Constant(_keywords, typeof(object));
                if (keywordValues.Length <= 3) {
                    // match and bind in one call, the values coming back through out parameters
                    var readArgs = new MSA.Expression[keywordValues.Length + 2];
                    readArgs[0] = info;
                    readArgs[1] = kw;
                    Array.Copy(keywordValues, 0, readArgs, 2, keywordValues.Length);
                    test = And(test, Ast.Call(JitRuntime.M("KeywordsRead" + keywordValues.Length), readArgs));
                } else {
                    test = And(test, Ast.Call(JitRuntime.M("KeywordsMatch"), info, kw));
                    for (int j = 0; j < keywordValues.Length; j++) {
                        test = And(test, Ast.Block(
                            Ast.Assign(keywordValues[j], Ast.Call(JitRuntime.M("KeywordValue"), info, kw, Ast.Constant(j))),
                            Ast.Constant(true)));
                    }
                }
            }

            for (int i = 0; i < _typedCount; i++) {
                switch (code.ParameterTypes[i]) {
                    case JT.Int: test = And(test, AllowDefault(i, values[i], Ast.TypeIs(values[i], typeof(int)))); break;
                    case JT.Dbl: test = And(test, AllowDefault(i, values[i], Ast.TypeIs(values[i], typeof(double)))); break;
                    // An Integer parameter carried as a long accepts either representation: a
                    // value in Int32 range arrives as an Int32 and only a larger one as an Int64.
                    case JT.Lng: test = And(test, AllowDefault(i, values[i], Ast.Call(JitRuntime.M("IsIntegral"), values[i]))); break;
                }
            }

            // -X:JITResume: a body with fallbacks takes the block last, for the frame it makes (JitFrame)
            var typedArgs = new MSA.Expression[_typedCount + 1 + ((code.Resume != null) ? 1 : 0)];
            typedArgs[0] = self;
            if (code.Resume != null) { typedArgs[_typedCount + 1] = blk; }
            for (int i = 0; i < _typedCount; i++) {
                if (PassedBoxed(i)) { typedArgs[i + 1] = values[i]; continue; }
                switch (code.ParameterTypes[i]) {
                    case JT.Int: typedArgs[i + 1] = Ast.Unbox(values[i], typeof(int)); break;
                    case JT.Dbl: typedArgs[i + 1] = Ast.Unbox(values[i], typeof(double)); break;
                    case JT.Lng: typedArgs[i + 1] = Ast.Call(JitRuntime.M("ToLong"), values[i]); break;
                    default: typedArgs[i + 1] = values[i]; break;
                }
            }

            MSA.Expression fast = Ast.Invoke(callee, typedArgs);
            if (code.ReturnType == JT.Lng) {
                // The one place a specialized method's value becomes a Ruby object: it leaves
                // through the representation funnel, so a result that fits in an Int32 is one.
                fast = Ast.Call(JitRuntime.M("NarrowLong"), fast);
            } else if (fast.Type != typeof(object)) {
                fast = JitRuntime.BoxResult(fast);
            }

            if (code.CanDeopt) {
                // The generic body runs after the handler, not inside it: an exception it raises
                // from within a catch block would leave the frames outside this one out of its
                // backtrace. While a deopt window is open the entry counts its calls of the typed
                // body (see OnDeopt).
                var result = Ast.Variable(typeof(object), "#result");
                var deopted = Ast.Constant(Deopted, typeof(object));
                fast = Ast.Block(typeof(object), new[] { result },
                    CountEntry(spec),
                    WithUndoLog(code, handler => Ast.TryCatch(
                        Ast.Assign(result, fast),
                        Ast.Catch(typeof(JitDeoptException), Ast.Block(
                            handler,
                            Ast.Call(JitRuntime.M("EntryDeopted"), Ast.Constant(spec, typeof(object))),
                            Ast.Assign(result, deopted)
                        ))
                    )),
                    Ast.Condition(Ast.ReferenceEqual(result, deopted), GenericCall(self, blk, args), result, typeof(object))
                );
            }

            MSA.Expression body = (test != null) ? Ast.Condition(test, fast, GenericCall(self, blk, args), typeof(object)) : fast;
            if (keywordValues != null) {
                body = Ast.Block(typeof(object), keywordValues, body);
            }
            if (fillDefaults.Count > 0) {
                fillDefaults.Add(body);
                body = Ast.Block(typeof(object), defaulted, fillDefaults);
            }

            // A stale version fails this guard on every later call; the stub takes the entry down
            // (JitStub.VersionChanged) and this one call runs the generic body.
            body = Ast.Condition(
                VersionTest(version, spec, code.UsesConstants),
                body,
                Ast.Block(
                    Ast.Call(JitRuntime.M("EntryStale"), Ast.Constant(spec, typeof(object))),
                    GenericCall(self, blk, args)
                ),
                typeof(object)
            );

            var all = new MSA.ParameterExpression[_arity + 2];
            all[0] = self;
            all[1] = blk;
            Array.Copy(args, 0, all, 2, _arity);
            return Ast.Lambda(_generic.GetType(), body, EntryNamePrefix + _ast.Name, all);
        }

        /// <summary>
        /// The entry JitKeywordCall calls with the keyword values already bound (and the defaults of the
        /// ones the call left out filled in): the same guards as BuildEntry's, but a failed one - or a
        /// deopt - answers NoDirect, and the caller then makes the call the ordinary way.
        /// </summary>
        private MSA.Expression<Func<object, object, object, object, object, object, object>>/*!*/ BuildDirectEntry(JitCode/*!*/ code,
            MSA.Expression/*!*/ callee, int version, Specialization/*!*/ spec) {

            var self = Ast.Parameter(typeof(object), "self");
            var slots = new MSA.ParameterExpression[JitKeywordCall.MaxPositional + JitKeywordCall.MaxKeywords];
            for (int i = 0; i < slots.Length; i++) {
                slots[i] = Ast.Parameter(typeof(object), "s" + i);
            }
            var values = new MSA.Expression[_typedCount];
            for (int i = 0; i < _positional; i++) {
                values[i] = slots[i];
            }
            for (int j = 0; j < _keywords.Count; j++) {
                values[_positional + j] = slots[JitKeywordCall.MaxPositional + j];
            }

            MSA.Expression test = Ast.AndAlso(VersionTest(version, spec, code.UsesConstants), NotTracing());
            // the separation check on the last positional, as in BuildEntry
            if (_positional > 0 && code.ParameterTypes[_positional - 1] == JT.Obj) {
                test = And(test, Ast.Not(Ast.Call(typeof(RubyOps).GetMethod("IsKeywordArgumentsHash"), values[_positional - 1])));
            }
            var typedArgs = new MSA.Expression[_typedCount + 1];
            typedArgs[0] = self;
            for (int i = 0; i < _typedCount; i++) {
                switch (code.ParameterTypes[i]) {
                    case JT.Int:
                        test = And(test, AllowDefault(i, values[i], Ast.TypeIs(values[i], typeof(int))));
                        typedArgs[i + 1] = Ast.Unbox(values[i], typeof(int));
                        break;
                    case JT.Dbl:
                        test = And(test, AllowDefault(i, values[i], Ast.TypeIs(values[i], typeof(double))));
                        typedArgs[i + 1] = Ast.Unbox(values[i], typeof(double));
                        break;
                    case JT.Lng:
                        test = And(test, AllowDefault(i, values[i], Ast.Call(JitRuntime.M("IsIntegral"), values[i])));
                        typedArgs[i + 1] = Ast.Call(JitRuntime.M("ToLong"), values[i]);
                        break;
                    default:
                        typedArgs[i + 1] = values[i];
                        break;
                }
                if (PassedBoxed(i)) { typedArgs[i + 1] = values[i]; }
            }

            MSA.Expression fast = Ast.Invoke(callee, typedArgs);
            if (code.ReturnType == JT.Lng) {
                fast = Ast.Call(JitRuntime.M("NarrowLong"), fast);
            } else if (fast.Type != typeof(object)) {
                fast = JitRuntime.BoxResult(fast);
            }
            var noDirect = Ast.Field(null, typeof(JitKeywordCall).GetField("NoDirect"));
            if (code.CanDeopt) {
                fast = Ast.Block(
                    CountEntry(spec),
                    fast
                );
                var direct = fast;
                fast = WithUndoLog(code, handler => Ast.TryCatch(direct, Ast.Catch(typeof(JitDeoptException), Ast.Block(
                    handler,
                    Ast.Call(JitRuntime.M("EntryDeopted"), Ast.Constant(spec, typeof(object))),
                    noDirect
                ))));
            }
            var body = Ast.Condition(test, fast, noDirect, typeof(object));

            var all = new MSA.ParameterExpression[slots.Length + 1];
            all[0] = self;
            Array.Copy(slots, 0, all, 1, slots.Length);
            return Ast.Lambda<Func<object, object, object, object, object, object, object>>(body, DirectEntryNamePrefix + _ast.Name, all);
        }

        /// <summary>
        /// BuildEntry for per-count bodies: which arguments are RubyOps.DefaultArgument picks the body
        /// (a call leaves out a suffix of the optional parameters), then the same guards as BuildEntry's
        /// over the arguments that body takes.
        /// </summary>
        private MSA.LambdaExpression/*!*/ BuildPerCountEntry(JitCode[]/*!*/ codes, MSA.Expression[]/*!*/ callees, int version, RubyClass pinnedClass,
            Specialization/*!*/ spec) {
            var self = Ast.Parameter(typeof(object), "self");
            var blk = Ast.Parameter(typeof(Proc), "block");
            var args = new MSA.ParameterExpression[_arity];
            for (int i = 0; i < _arity; i++) {
                args[i] = Ast.Parameter(typeof(object), "a" + i);
            }

            MSA.Expression body = null;
            for (int k = codes.Length - 1; k >= 0; k--) {
                MSA.Expression arm = (codes[k] != null)
                    ? BuildPerCountArm(codes[k], callees[k], self, blk, args, _perCountMandatory + k, pinnedClass, spec)
                    : GenericCall(self, blk, args);
                body = (body == null) ? arm : Ast.Condition(
                    Ast.ReferenceEqual(args[_perCountMandatory + k], Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"))),
                    arm, body, typeof(object));
            }

            bool usesConstants = false;
            foreach (var code in codes) {
                if (code != null && code.UsesConstants) { usesConstants = true; }
            }
            body = Ast.Condition(
                VersionTest(version, spec, usesConstants),
                body,
                Ast.Block(
                    Ast.Call(JitRuntime.M("EntryStale"), Ast.Constant(spec, typeof(object))),
                    GenericCall(self, blk, args)
                ),
                typeof(object)
            );

            var all = new MSA.ParameterExpression[_arity + 2];
            all[0] = self;
            all[1] = blk;
            Array.Copy(args, 0, all, 2, _arity);
            return Ast.Lambda(_generic.GetType(), body, EntryNamePrefix + _ast.Name, all);
        }

        private MSA.Expression/*!*/ BuildPerCountArm(JitCode/*!*/ code, MSA.Expression/*!*/ callee, MSA.ParameterExpression/*!*/ self,
            MSA.ParameterExpression/*!*/ blk, MSA.ParameterExpression[]/*!*/ args, int count, RubyClass pinnedClass,
            Specialization/*!*/ spec) {

            MSA.Expression test = NotTracing();
            // as BuildEntry's: a body that calls itself or binds another method's typed body is only
            // right for the class those calls were resolved on. A subclass that overrides the callee
            // changes no method version, so nothing else sends its receivers to the generic body.
            if (pinnedClass != null && (code.EmittedSelfCall || code.DirectCalls)) {
                test = And(test, Ast.ReferenceEqual(
                    Ast.Call(JitRuntime.M("ClassOf"), Ast.Constant(_context), self),
                    Ast.Constant(pinnedClass, typeof(RubyClass))));
            }
            var typedArgs = new MSA.Expression[count + 1];
            typedArgs[0] = self;
            for (int i = 0; i < count; i++) {
                switch (code.ParameterTypes[i]) {
                    case JT.Int:
                        test = And(test, Ast.TypeIs(args[i], typeof(int)));
                        typedArgs[i + 1] = Ast.Unbox(args[i], typeof(int));
                        break;
                    case JT.Dbl:
                        test = And(test, Ast.TypeIs(args[i], typeof(double)));
                        typedArgs[i + 1] = Ast.Unbox(args[i], typeof(double));
                        break;
                    case JT.Lng:
                        test = And(test, Ast.Call(JitRuntime.M("IsIntegral"), args[i]));
                        typedArgs[i + 1] = Ast.Call(JitRuntime.M("ToLong"), args[i]);
                        break;
                    default:
                        typedArgs[i + 1] = args[i];
                        break;
                }
            }

            MSA.Expression fast = Ast.Invoke(callee, typedArgs);
            if (code.ReturnType == JT.Lng) {
                fast = Ast.Call(JitRuntime.M("NarrowLong"), fast);
            } else if (fast.Type != typeof(object)) {
                fast = JitRuntime.BoxResult(fast);
            }

            if (code.CanDeopt) {
                // as in BuildEntry: the generic body runs after the handler, not inside it, and the
                // deopts count towards retiring the entry (OnDeopt)
                var result = Ast.Variable(typeof(object), "#result");
                var deopted = Ast.Constant(Deopted, typeof(object));
                fast = Ast.Block(typeof(object), new[] { result },
                    CountEntry(spec),
                    WithUndoLog(code, handler => Ast.TryCatch(
                        Ast.Assign(result, fast),
                        Ast.Catch(typeof(JitDeoptException), Ast.Block(
                            handler,
                            Ast.Call(JitRuntime.M("EntryDeopted"), Ast.Constant(spec, typeof(object))),
                            Ast.Assign(result, deopted)
                        ))
                    )),
                    Ast.Condition(Ast.ReferenceEqual(result, deopted), GenericCall(self, blk, args), result, typeof(object))
                );
            }
            return (test != null) ? Ast.Condition(test, fast, GenericCall(self, blk, args), typeof(object)) : fast;
        }

        /// <summary>
        /// The events a typed body (or an outlined loop) would not deliver: it has no :line, :call or
        /// :return hooks, and the library methods it inlines no :c_call or :c_return ones (the block
        /// events are in for safety; a typed body has no blocks). It raises, rescues, defines classes
        /// and switches fibers only through the generic code, so a TracePoint on :raise, :rescue,
        /// :class, :end, :thread_*, :fiber_switch or :script_compiled leaves it running.
        /// </summary>
        internal const int TypedBodyEvents = (int)(TraceEvents.Line | TraceEvents.Call | TraceEvents.Return |
            TraceEvents.CCall | TraceEvents.CReturn | TraceEvents.BCall | TraceEvents.BReturn);

        /// <summary>
        /// No TracePoint is enabled for an event the typed body would not deliver (TypedBodyEvents), so
        /// while one is the generic body runs instead. A TracePoint is enabled and disabled at any time,
        /// which the method version does not follow, so this is tested on every entry - one static read.
        /// </summary>
        private static MSA.Expression/*!*/ NotTracing() {
            return Ast.Equal(
                Ast.And(Ast.Field(null, typeof(TracePoint).GetField("ActiveEvents")), Ast.Constant(TypedBodyEvents)),
                Ast.Constant(0));
        }

        /// <summary>
        /// The entry guard: the method version, and if the body folded constants, the constant version or, when
        /// only that has moved, the constants looked up again (RevalidateConstants).
        /// </summary>
        private MSA.Expression/*!*/ VersionTest(int version, Specialization/*!*/ spec, bool usesConstants) {
            MSA.Expression test = Ast.Call(JitRuntime.M("VersionOk"), Ast.Constant(version));
            if (usesConstants) {
                // the snapshot is read from the specialization: looking the constants up again moves it
                test = Ast.AndAlso(test, Ast.Call(JitRuntime.M("ConstantsCurrent"), Ast.Constant(_context), Ast.Constant(spec, typeof(object))));
            }
            return test;
        }

        /// <summary>
        /// The typed body's call, made by <paramref name="call"/> around the statement its deopt handler
        /// is to start with: for a body that writes Hash or Array elements (JitCode.UsesUndoLog), the
        /// handler takes those writes back (JitUndoLog) before the generic body runs the call again, and
        /// however the call ends, the writes it made and kept stand. Otherwise the handler does nothing.
        /// </summary>
        private static MSA.Expression/*!*/ WithUndoLog(JitCode/*!*/ code, Func<MSA.Expression, MSA.Expression>/*!*/ call) {
            if (!code.UsesUndoLog) {
                return call(Ast.Empty());
            }
            var mark = Ast.Variable(typeof(int), "#undo-mark");
            var guarded = call(Ast.Call(JitRuntime.M("UndoRollback"), mark));
            return Ast.Block(guarded.Type, new[] { mark },
                Ast.Assign(mark, Ast.Call(JitRuntime.M("UndoMark"))),
                Ast.TryFinally(guarded, Ast.Call(JitRuntime.M("UndoRelease"), mark))
            );
        }

        private static MSA.Expression/*!*/ And(MSA.Expression left, MSA.Expression/*!*/ right) {
            return (left != null) ? Ast.AndAlso(left, right) : right;
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
        /// Whether <see cref="Wrap"/> puts a stub on a method with this AST, whatever the run-time options: what the
        /// precompiled startup library (Util/aot) asks of each `def' it compiles, which then keeps the def's source for
        /// the JIT (see RubyMethodBody, PrecompiledCode.ParseJitSource).
        /// </summary>
        internal static bool IsCandidate(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context) {
            JitKeywords keywords;
            bool[] hasDefault, storedOnly;
            object[] defaults;
            bool perCount;
            int arity;
            string rejectedBy;
            return Screen(ast, context, null, out keywords, out hasDefault, out defaults, out perCount, out storedOnly, out arity, out rejectedBy);
        }

        /// <summary>
        /// <see cref="Wrap"/>'s static decision for the census (JitCensus), which asks it of a method that was parsed but
        /// never defined: there is no declaring module to resolve the body's calls on, so the census says which of them
        /// are of a method the typed body may inline (<paramref name="isLeafCallee"/>, as JitScreen takes it).
        /// <paramref name="typedCount"/>: the typed body's parameters; <paramref name="perCountMandatory"/>: the mandatory
        /// ones of a method with per-count bodies (TrySpecializePerCount), -1 for any other.
        /// </summary>
        internal static bool CensusScreen(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, Func<string, bool, bool>/*!*/ isLeafCallee,
            out JitKeywords keywords, out int typedCount, out int perCountMandatory, out string rejectedBy) {
            bool[] hasDefault, storedOnly;
            object[] defaults;
            bool perCount;
            int arity;
            typedCount = 0;
            perCountMandatory = -1;
            if (!Screen(ast, context, null, out keywords, out hasDefault, out defaults, out perCount, out storedOnly, out arity, out rejectedBy, isLeafCallee)) {
                return false;
            }
            // as the stub counts them (JitStub's constructor)
            typedCount = (keywords != null) ? ast.Parameters.Mandatory.Length + keywords.OptionalPositionals + keywords.Count : arity;
            if (perCount) { perCountMandatory = ast.Parameters.Mandatory.Length; }
            return true;
        }

        /// <summary>
        /// What about the parameter list keeps a method from ever being wrapped, whatever its body is; null if nothing does.
        /// </summary>
        internal static string ParameterShape(MethodDeclaration/*!*/ ast) {
            var ps = ast.Parameters;
            if (ps.Unsplat != null) { return "rest parameter"; }
            if (ps.Block != null) { return "block parameter"; }
            if (ast.UsesBlock) { return "uses its block"; }
            if (ps.Mandatory.Length + ps.Optional.Length > MaxArity) { return "more than " + MaxArity + " parameters"; }
            for (int i = 0; i < ps.Mandatory.Length; i++) {
                if (!(ps.Mandatory[i] is LocalVariable)) { return "destructuring parameter"; }
            }
            return null;
        }

        // The static part of Wrap's decision; rejectedBy is set when the screen (not the parameter list) refused the body.
        // isLeafCallee: the census's answer in place of the declaring module's (CensusScreen).
        // constantTruth: what a constant guarding a statement is now (JitScreen.CanEverCompile); null where it is not known.
        private static bool Screen(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, RubyModule declaringModule, out JitKeywords keywords, out bool[] hasDefault,
            out object[] defaults, out bool perCount, out bool[] storedOnly, out int arity, out string rejectedBy,
            Func<string, bool, bool> isLeafCallee = null, Func<ConstantVariable, bool?> constantTruth = null) {
            keywords = null;
            hasDefault = null;
            defaults = null;
            perCount = false;
            storedOnly = null;
            rejectedBy = null;
            arity = 0;

            var ps = ast.Parameters;
            int mandatoryCount = ps.Mandatory.Length;
            arity = mandatoryCount + ps.Optional.Length;
            if (ParameterShape(ast) != null) { return false; }

            // An optional parameter is taken when its default is a literal the typed body could
            // have produced itself - nil, true, false, a Fixnum or a Float: evaluating it has no
            // effect and cannot see the other parameters, so the entry may fill it in up front.
            // Keyword parameters arrive in one trailing optional parameter (see JitKeywords); a method
            // with keywords has no other optional one (PrismAstBridge lowers that mix to a splat).
            // Any other default makes it a method with per-count bodies (TrySpecializePerCount), which
            // evaluate the defaults the call left out themselves; JitScreen checks that they can.
            if (ps.Optional.Length > 0 && (keywords = JitKeywords.TryRecognize(ast, context)) == null) {
                hasDefault = new bool[arity];
                defaults = new object[arity];
                for (int i = 0; i < ps.Optional.Length; i++) {
                    var opt = ps.Optional[i];
                    if (!(opt.Left is LocalVariable)) { return false; }
                    var literal = opt.Right as Literal;
                    object v = (literal != null) ? literal.Value : null;
                    if (literal == null || !(v == null || v is bool || v is int || v is double)) {
                        perCount = true;
                        continue;
                    }
                    hasDefault[mandatoryCount + i] = true;
                    defaults[mandatoryCount + i] = v;
                }
                if (perCount) {
                    hasDefault = null;
                    defaults = null;
                }
            } else if (keywords != null && keywords.OptionalPositionals > 0) {
                // optional positionals before the keywords: the entry fills in their literal defaults
                hasDefault = new bool[arity];
                defaults = new object[arity];
                for (int i = 0; i < keywords.OptionalPositionals; i++) {
                    hasDefault[mandatoryCount + i] = true;
                    defaults[mandatoryCount + i] = keywords.OptionalDefaults[i];
                }
            }
            // A method whose keywords follow optional positionals is only taken as a keyword method:
            // the entry has to move a misplaced keyword hash first, which only that kind does.
            if (ps.KeywordsAfterOptionals && keywords == null) { return false; }
            // A body the compiler can never accept, whatever types it is called with, is not
            // wrapped at all. Once wrapped, the trampoline is what the runtime caches and bakes
            // into call site rules, so a method declined later still pays one extra indirect
            // call on every call for the life of the process.
            HashSet<string> otherUses;
            if (!JitScreen.CanEverCompile(ast, keywords,
                // (no module: IsCandidate, which only asks whether the body may ever be wrapped)
                (isLeafCallee != null) ? isLeafCallee : (declaringModule != null)
                    ? (name, onSelf) => IsLeafMethod(declaringModule, name, onSelf) ||
                        (onSelf && context.RubyOptions.JitCalls && IsCallableMethod(declaringModule, name, 0))
                    : (Func<string, bool, bool>)((name, onSelf) => true), out rejectedBy, out otherUses, constantTruth)) {
                return false;
            }

            // over the typed body's parameters: the positionals, then a keyword method's keyword locals
            int positionalCount = mandatoryCount + ((keywords != null) ? keywords.OptionalPositionals : 0);
            int typedCount = positionalCount + ((keywords != null) ? keywords.Count : ps.Optional.Length);
            for (int i = 0; i < typedCount; i++) {
                string name = (i < mandatoryCount) ? ((LocalVariable)ps.Mandatory[i]).Name
                    : (i < positionalCount) ? ((LocalVariable)ps.Optional[i - mandatoryCount].Left).Name
                    : (keywords != null) ? keywords.LocalNames[i - positionalCount]
                    : ((LocalVariable)ps.Optional[i - mandatoryCount].Left).Name;
                if (!otherUses.Contains(name)) {
                    if (storedOnly == null) { storedOnly = new bool[typedCount]; }
                    storedOnly[i] = true;
                }
            }
            return true;
        }

        /// <summary>
        /// A call of <paramref name="name"/> is one the typed body may inline (JitCompiler.TryInlineCall): the method it
        /// resolves to on the declaring module now is a Ruby method whose body calls nothing (JitScreen's leaf screen) -
        /// for a call on another object, one of the declaring class's own kind (`other.compare_to(o)'), the only kind
        /// this can see. A call on self of a method not defined yet may be of one defined further down. Only a hint, for the screen: the compiler resolves the call again on the receiver's class. Without it
        /// every method calling another one would be wrapped, and a wrapped method declined later stays slower.
        /// </summary>
        private static bool IsLeafMethod(RubyModule/*!*/ declaringModule, string/*!*/ name, bool onSelf) {
            try {
                var resolved = declaringModule.ResolveMethod(name, VisibilityContext.AllVisible);
                if (!resolved.Found) {
                    // a method defined further down the class body is not there yet when this one is defined
                    if (onSelf) { return true; }
                    // `key.compare_to(current.key)' in RedBlackTree: a method of another class, which only the profile
                    // will name. Taken if some top-level class defines a method of that name that may be inlined.
                    return IsLeafMethodOfSomeClass(declaringModule.Context, name);
                }
                // Nested only for a call on another object (`key.compare_to(k)'): taken for a call on self as well, it
                // wrapped Json's HashIndexTable#add and #get over `hash_slot_for', whose own callee is never compiled.
                return IsLeafMethod(resolved.Info as RubyMethodInfo, !onSelf);
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// -X:JITCalls: a call of <paramref name="name"/> on self is one the typed body may bind (BindCallee) - the
        /// method it resolves to on the declaring module now is a Ruby method the screen would take, its own calls
        /// on self looked at the same way, three levels down. As IsLeafMethod, only a hint, and one defined further
        /// down is taken on trust.
        /// </summary>
        internal static bool IsCallableMethod(RubyModule/*!*/ declaringModule, string/*!*/ name, int depth) {
            try {
                var resolved = declaringModule.ResolveMethod(name, VisibilityContext.AllVisible);
                if (!resolved.Found) { return true; }
                var info = resolved.Info as RubyMethodInfo;
                if (info == null) { return false; }
                var ast = info.GetSyntaxTree();
                if (!IsCallableParameterList(ast)) { return false; }
                if (depth >= 3) { return true; }
                RubyModule owner = info.DeclaringModule;
                string rejectedBy;
                HashSet<string> otherUses;
                return JitScreen.CanEverCompile(ast, null,
                    (n, onSelf) => IsLeafMethod(owner, n, onSelf) || (onSelf && IsCallableMethod(owner, n, depth + 1)),
                    out rejectedBy, out otherUses);
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>The parameters of a method a typed body may bind a call to (IsCallableMethod); the census asks it of a syntax tree.</summary>
        internal static bool IsCallableParameterList(MethodDeclaration/*!*/ ast) {
            var ps = ast.Parameters;
            return ps.Unsplat == null && ps.Block == null && !ast.UsesBlock && ps.Mandatory.Length + ps.Optional.Length <= MaxArity;
        }

        /// <summary>
        /// <paramref name="nest"/>: the method may itself call, on self, methods of the leaf shape, which its typed body
        /// inlines (JitInlineShape.IsInlinableShape) - one level, so `compare_to' calling `compare_numbers' is taken.
        /// </summary>
        internal static bool IsLeafMethod(RubyMethodInfo info, bool nest) {
            if (info == null) { return false; }
            RubyModule owner = info.DeclaringModule;
            Func<string, bool> nested = null;
            if (nest) {
                // the nested callee as the owner resolves it now; one defined further down is not taken
                nested = n => {
                    var r = owner.ResolveMethod(n, VisibilityContext.AllVisible);
                    return r.Found && IsLeafMethod(r.Info as RubyMethodInfo, false);
                };
            }
            return IsLeafShape(info.GetSyntaxTree(), nested);
        }

        /// <summary>
        /// The leaf test on the syntax tree alone, which is all the census (JitCensus) has of a method.
        /// <paramref name="nested"/>: whether a call on self of a name is of a method that is a leaf itself; null if the
        /// method may call none.
        /// </summary>
        internal static bool IsLeafShape(MethodDeclaration/*!*/ ast, Func<string, bool> nested) {
            var ps = ast.Parameters;
            if (ps.Optional.Length > 0 || ps.Unsplat != null || ps.Block != null || ps.Mandatory.Length > MaxArity) { return false; }
            Func<string, bool, bool> nestedCallee = null;
            if (nested != null) {
                nestedCallee = (n, onSelf) => onSelf && nested(n);
            }
            if (!JitInlineShape.IsInlinableShape(ast, nested)) { return false; }
            string rejectedBy;
            HashSet<string> otherUses;
            return JitScreen.CanEverCompile(ast, null, nestedCallee, out rejectedBy, out otherUses);
        }

        /// <summary>A class that is a constant of Object defines a Ruby method of this name that may be inlined.</summary>
        internal static bool IsLeafMethodOfSomeClass(RubyContext/*!*/ context, string/*!*/ name) {
            var candidates = new List<RubyMethodInfo>();
            using (context.ClassHierarchyLocker()) {
                context.ObjectClass.EnumerateConstants((module, constantName, value) => {
                    var cls = value as RubyClass;
                    RubyMemberInfo method;
                    if (cls != null && cls.Context == context && !cls.IsSingletonClass && cls.TryGetDefinedMethod(name, out method)) {
                        var info = method as RubyMethodInfo;
                        if (info != null) { candidates.Add(info); }
                    }
                    return false;
                });
            }
            foreach (var info in candidates) {
                if (IsLeafMethod(info, true)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// Decides, statically and cheaply, whether a method is worth putting a stub on, and
        /// returns the trampoline that replaces the generic delegate. Returns the generic
        /// delegate unchanged when it is not.
        /// </summary>
        // The name the generic body's frames carry (RubyStackTraceBuilder.EncodeMethodName: label, line, file), for the
        // compiled bodies too: a frame of one is then "file:line:in 'Object#f'" in a backtrace, not ":0:in 'jit$f'".
        // An exception rarely leaves one - a raise deopts first - but the stack check at its entry (#128) does.
        private string _frameName;

        /// <summary>
        /// The compiled body, under the method's frame name, with the versions it was compiled under given to
        /// its loops' safe points (JitCompiler.EmitLoopSafePoint).
        /// </summary>
        private JitCode Named(JitCode code, long epoch, int version, int constantVersion) {
            if (code != null) {
                code.LoopGuard.Folded = code.FoldedConstants;
                code.LoopGuard.Set(epoch, version, code.UsesConstants ? _context : null, constantVersion);
            }
            if (code != null && _frameName != null) {
                code.Lambda = Ast.Lambda(code.Lambda.Type, code.Lambda.Body, _frameName, code.Lambda.Parameters);
            }
            return code;
        }

        internal static Delegate/*!*/ Wrap(Delegate/*!*/ generic, MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, RubyModule/*!*/ declaringModule) {
            return Wrap(generic, ast, context, declaringModule, null, null);
        }

        internal static Delegate/*!*/ Wrap(Delegate/*!*/ generic, MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, RubyModule/*!*/ declaringModule,
            RubyScope declaringScope, StrongBox<RefinementActivation> definitionRefinements, string frameName = null) {
            JitRuntime.HookStats();

            // Tracing and coverage need the real frame. (A TracePoint enabled at run time is the
            // entry's NotTracing guard; a body that counts its lines is not handed here at all.)
            if (context.RubyOptions.EnableTracing || context.RubyOptions.Profile) { return generic; }

            JitKeywords keywords;
            bool[] hasDefault, storedOnly;
            object[] defaults;
            bool perCount;
            int arity;
            string rejectedBy;
            // A constant guarding a statement is looked up where the compiler will fold it (JitCompiler.TryEmitConstantGuarded):
            // a guard that leaves the statement dead now does not keep the method out. One that cannot be folded is
            // walked as any condition; without the scope none can be.
            Func<ConstantVariable, bool?> constantTruth = guard => {
                object value;
                RubyModule owner;
                if (declaringScope == null || JitFoldedConstant.Resolve(context, declaringScope, guard, out value, out owner) != null ||
                    !RactorIsolation.IsTriviallyShareable(value)) { return null; }
                return RubyOps.IsTrue(value);
            };
            if (!Screen(ast, context, declaringModule, out keywords, out hasDefault, out defaults, out perCount, out storedOnly, out arity, out rejectedBy,
                null, constantTruth)) {
                if (rejectedBy != null) {
                    JitRuntime.RecordScreenRejection(rejectedBy);
                    if (JitRuntime.Verbose) {
                        Console.Error.WriteLine("[jit] screen rejected {0}/{1}: {2}", declaringModule.Name ?? "main", ast.Name, JitScreen.LastRejectionDetail);
                    }
                }
                return generic;
            }
            int mandatoryCount = ast.Parameters.Mandatory.Length;

            JitStub stub;
            switch (arity) {
                case 0: stub = new JitStub0(ast, context, declaringModule, generic, keywords); break;
                case 1: stub = new JitStub1(ast, context, declaringModule, generic, keywords); break;
                case 2: stub = new JitStub2(ast, context, declaringModule, generic, keywords); break;
                case 3: stub = new JitStub3(ast, context, declaringModule, generic, keywords); break;
                case 4: stub = new JitStub4(ast, context, declaringModule, generic, keywords); break;
                case 5: stub = new JitStub5(ast, context, declaringModule, generic, keywords); break;
                default: stub = new JitStub6(ast, context, declaringModule, generic, keywords); break;
            }
            stub._hasDefault = hasDefault;
            stub._defaults = defaults;
            if (perCount) { stub._perCountMandatory = mandatoryCount; }
            stub._storedOnly = storedOnly;
            stub._declaringScope = declaringScope;
            var light = generic.Target as Microsoft.Scripting.Interpreter.LightLambda;
            if (light != null) {
                light.Compile += (sender, e) => stub.GenericCompiled(e.Compiled);
            } else {
                stub._genericCompiled = true;
            }
            stub._definitionRefinements = definitionRefinements;
            stub._frameName = frameName;
            return stub.Trampoline;
        }

        private static readonly object[] NoArgs = new object[0];
        protected static object[] Args0 { get { return NoArgs; } }
    }

    // ---- arity-specialized stubs -----------------------------------------------------------

    internal sealed class JitStub0 : JitStub {
        private volatile Func<object, Proc, object> _fast;
        // What a rule calls (JitStub.CallCurrent): the fast slot when it holds something, the trampoline otherwise.
        internal Func<object, Proc, object> Target;
        private readonly Func<object, Proc, object>/*!*/ _gen;
        private readonly Func<object, Proc, object>/*!*/ _tramp;
        private Func<object, Proc, object> _cool;

        internal JitStub0(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 0, keywords) {
            _gen = (Func<object, Proc, object>)generic;
            _tramp = Call;
            Target = _tramp;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void SetFast(Delegate entry) { _fast = (Func<object, Proc, object>)entry; Target = _fast ?? _tramp; }
        protected override Delegate Fast { get { return _fast; } }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block); }
            long start = Stopwatch.GetTimestamp();
            object result;
            // a generic call that raises is timed too (CoolRaised, a filter: nothing is caught)
            try { result = _gen(self, block); } catch (Exception) when (CoolRaised(start)) { throw; }
            CoolSample(start);
            return result;
        }

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
        // What a rule calls (JitStub.CallCurrent): the fast slot when it holds something, the trampoline otherwise.
        internal Func<object, Proc, object, object> Target;
        private readonly Func<object, Proc, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object> _cool;

        internal JitStub1(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 1, keywords) {
            _gen = (Func<object, Proc, object, object>)generic;
            _tramp = Call;
            Target = _tramp;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void SetFast(Delegate entry) { _fast = (Func<object, Proc, object, object>)entry; Target = _fast ?? _tramp; }
        protected override Delegate Fast { get { return _fast; } }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0); }
            long start = Stopwatch.GetTimestamp();
            object result;
            // a generic call that raises is timed too (CoolRaised, a filter: nothing is caught)
            try { result = _gen(self, block, a0); } catch (Exception) when (CoolRaised(start)) { throw; }
            CoolSample(start);
            return result;
        }

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
        // What a rule calls (JitStub.CallCurrent): the fast slot when it holds something, the trampoline otherwise.
        internal Func<object, Proc, object, object, object> Target;
        private readonly Func<object, Proc, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object> _cool;

        internal JitStub2(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 2, keywords) {
            _gen = (Func<object, Proc, object, object, object>)generic;
            _tramp = Call;
            Target = _tramp;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void SetFast(Delegate entry) { _fast = (Func<object, Proc, object, object, object>)entry; Target = _fast ?? _tramp; }
        protected override Delegate Fast { get { return _fast; } }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1); }
            long start = Stopwatch.GetTimestamp();
            object result;
            // a generic call that raises is timed too (CoolRaised, a filter: nothing is caught)
            try { result = _gen(self, block, a0, a1); } catch (Exception) when (CoolRaised(start)) { throw; }
            CoolSample(start);
            return result;
        }

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
        // What a rule calls (JitStub.CallCurrent): the fast slot when it holds something, the trampoline otherwise.
        internal Func<object, Proc, object, object, object, object> Target;
        private readonly Func<object, Proc, object, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object, object> _cool;

        internal JitStub3(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 3, keywords) {
            _gen = (Func<object, Proc, object, object, object, object>)generic;
            _tramp = Call;
            Target = _tramp;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void SetFast(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object>)entry; Target = _fast ?? _tramp; }
        protected override Delegate Fast { get { return _fast; } }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1, object a2) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1, a2); }
            long start = Stopwatch.GetTimestamp();
            object result;
            // a generic call that raises is timed too (CoolRaised, a filter: nothing is caught)
            try { result = _gen(self, block, a0, a1, a2); } catch (Exception) when (CoolRaised(start)) { throw; }
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0, object a1, object a2) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2); }
            Profile(self, new[] { a0, a1, a2 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2) : _gen(self, block, a0, a1, a2);
        }
    }

    internal sealed class JitStub4 : JitStub {
        private volatile Func<object, Proc, object, object, object, object, object> _fast;
        // What a rule calls (JitStub.CallCurrent): the fast slot when it holds something, the trampoline otherwise.
        internal Func<object, Proc, object, object, object, object, object> Target;
        private readonly Func<object, Proc, object, object, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object, object, object> _cool;

        internal JitStub4(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 4, keywords) {
            _gen = (Func<object, Proc, object, object, object, object, object>)generic;
            _tramp = Call;
            Target = _tramp;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void SetFast(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object, object>)entry; Target = _fast ?? _tramp; }
        protected override Delegate Fast { get { return _fast; } }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1, object a2, object a3) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1, a2, a3); }
            long start = Stopwatch.GetTimestamp();
            object result;
            // a generic call that raises is timed too (CoolRaised, a filter: nothing is caught)
            try { result = _gen(self, block, a0, a1, a2, a3); } catch (Exception) when (CoolRaised(start)) { throw; }
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0, object a1, object a2, object a3) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2, a3); }
            Profile(self, new[] { a0, a1, a2, a3 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2, a3) : _gen(self, block, a0, a1, a2, a3);
        }
    }

    internal sealed class JitStub5 : JitStub {
        private volatile Func<object, Proc, object, object, object, object, object, object> _fast;
        // What a rule calls (JitStub.CallCurrent): the fast slot when it holds something, the trampoline otherwise.
        internal Func<object, Proc, object, object, object, object, object, object> Target;
        private readonly Func<object, Proc, object, object, object, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object, object, object, object> _cool;

        internal JitStub5(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 5, keywords) {
            _gen = (Func<object, Proc, object, object, object, object, object, object>)generic;
            _tramp = Call;
            Target = _tramp;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void SetFast(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object, object, object>)entry; Target = _fast ?? _tramp; }
        protected override Delegate Fast { get { return _fast; } }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1, object a2, object a3, object a4) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1, a2, a3, a4); }
            long start = Stopwatch.GetTimestamp();
            object result;
            // a generic call that raises is timed too (CoolRaised, a filter: nothing is caught)
            try { result = _gen(self, block, a0, a1, a2, a3, a4); } catch (Exception) when (CoolRaised(start)) { throw; }
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0, object a1, object a2, object a3, object a4) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2, a3, a4); }
            Profile(self, new[] { a0, a1, a2, a3, a4 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2, a3, a4) : _gen(self, block, a0, a1, a2, a3, a4);
        }
    }

    internal sealed class JitStub6 : JitStub {
        private volatile Func<object, Proc, object, object, object, object, object, object, object> _fast;
        // What a rule calls (JitStub.CallCurrent): the fast slot when it holds something, the trampoline otherwise.
        internal Func<object, Proc, object, object, object, object, object, object, object> Target;
        private readonly Func<object, Proc, object, object, object, object, object, object, object>/*!*/ _gen;
        private readonly Func<object, Proc, object, object, object, object, object, object, object>/*!*/ _tramp;
        private Func<object, Proc, object, object, object, object, object, object, object> _cool;

        internal JitStub6(MethodDeclaration ast, RubyContext context, RubyModule m, Delegate generic, JitKeywords keywords)
            : base(ast, context, m, generic, 6, keywords) {
            _gen = (Func<object, Proc, object, object, object, object, object, object, object>)generic;
            _tramp = Call;
            Target = _tramp;
        }

        internal override Delegate/*!*/ Trampoline { get { return _tramp; } }
        protected override void SetFast(Delegate entry) { _fast = (Func<object, Proc, object, object, object, object, object, object, object>)entry; Target = _fast ?? _tramp; }
        protected override Delegate Fast { get { return _fast; } }
        protected override Delegate/*!*/ CoolingBody { get { return _cool ?? (_cool = Cool); } }

        private object Cool(object self, Proc block, object a0, object a1, object a2, object a3, object a4, object a5) {
            if (--_coolCalls <= 0) { CoolDownCheck(); }
            if (!CoolPick()) { return _gen(self, block, a0, a1, a2, a3, a4, a5); }
            long start = Stopwatch.GetTimestamp();
            object result;
            // a generic call that raises is timed too (CoolRaised, a filter: nothing is caught)
            try { result = _gen(self, block, a0, a1, a2, a3, a4, a5); } catch (Exception) when (CoolRaised(start)) { throw; }
            CoolSample(start);
            return result;
        }

        private object Call(object self, Proc block, object a0, object a1, object a2, object a3, object a4, object a5) {
            var f = _fast;
            if (f != null) { return f(self, block, a0, a1, a2, a3, a4, a5); }
            Profile(self, new[] { a0, a1, a2, a3, a4, a5 });
            f = _fast;
            return (f != null) ? f(self, block, a0, a1, a2, a3, a4, a5) : _gen(self, block, a0, a1, a2, a3, a4, a5);
        }
    }

}
