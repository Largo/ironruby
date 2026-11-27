/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * Runtime support: the deoptimization signal, the guarded arithmetic helpers the
 * specialized code calls, and the reflection handles the compiler emits.
 *
 * ***************************************************************************/

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;

    /// <summary>
    /// Thrown by specialized code when an assumption it was compiled under no longer holds
    /// (Fixnum arithmetic overflowing into Bignum, a division by zero that has to raise).
    ///
    /// A specialized body is only ever produced for a *pure* method - one that cannot be
    /// observed part way through - so the entry stub answers a deopt by re-running the generic
    /// body from the top. That is why there is no mid-body state materialization here: the
    /// compiler refuses to specialize any body for which restarting would be visible.
    /// </summary>
    [Serializable]
    internal sealed class JitDeoptException : Exception {
        internal static readonly JitDeoptException Instance = new JitDeoptException();
        private JitDeoptException() : base("IronRuby JIT deoptimization") { }
    }

    /// <summary>
    /// Runtime helpers called from JIT-compiled code.
    /// </summary>
    public static partial class JitRuntime {
        /// <summary>IR_JIT_VERBOSE=1 traces what gets specialized and what is rejected.</summary>
        internal static readonly bool Verbose = Environment.GetEnvironmentVariable("IR_JIT_VERBOSE") == "1";

        /// <summary>
        /// IR_JIT_DEADCONST (on unless =0): a statement guarded by a constant whose value makes it dead when the body is
        /// compiled - `trace(x) if TRACING' with TRACING false - is left out of the typed body and of a loop's copy, under
        /// the constant's revalidation (JitCompiler.TryEmitConstantGuarded), and the screen does not refuse the body for it.
        /// </summary>
        internal static readonly bool DeadConstants = Environment.GetEnvironmentVariable("IR_JIT_DEADCONST") != "0";

        /// <summary>
        /// IR_JIT_UNSAMPLED (on unless =0): a call on a local of the method that the profile has no sample of is
        /// inlined for the classes that define an inlinable method of the name, under checks of their classes
        /// (JitCompiler.UnsampledCallees).
        /// </summary>
        internal static readonly bool InlineUnsampled = Environment.GetEnvironmentVariable("IR_JIT_UNSAMPLED") != "0";

        /// <summary>
        /// Statistics, printed by -X:JITStats.
        /// </summary>
        internal static int Specialized, Rejected, Deopts, Invalidations, ScreenRejected, DeoptRetired, DeoptRetried;
        internal static long CompileTicks;
        // -X:JITCalls: the specializations whose bodies call other methods' typed bodies (JitCode.DirectCalls)
        internal static int DirectCallBodies;

        /// <summary>
        /// Which node kinds the static pre-screen turned methods away on. Only collected under
        /// IR_JIT_VERBOSE: it is how a JitCompiler.Emit case the screen has not been told about
        /// becomes visible - the newly supported node kind sits at the top of this histogram.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, int> _screenReasons =
            Verbose ? new System.Collections.Generic.Dictionary<string, int>() : null;

        /// <summary>A method the pre-screen decided could never compile, so it was never wrapped.</summary>
        internal static void RecordScreenRejection(string nodeKind) {
            ScreenRejected++;
            if (_screenReasons == null) { return; }
            lock (_screenReasons) {
                int n;
                _screenReasons.TryGetValue(nodeKind ?? "?", out n);
                _screenReasons[nodeKind ?? "?"] = n + 1;
            }
        }

        private static int _statsHooked;

        /// <summary>With IR_JIT_VERBOSE=1, print what the JIT did on the way out.</summary>
        internal static void HookStats() {
            if (!Verbose || System.Threading.Interlocked.Exchange(ref _statsHooked, 1) != 0) { return; }
            AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                Console.Error.WriteLine("[jit] specialized={0} rejected={1} screen-rejected={2} deopts={3} invalidations={4} retired={5} retried={6} direct-calls={8} resume-bodies={9} fallbacks={10} compile={7:F1}ms",
                    Specialized, Rejected, ScreenRejected, Deopts, Invalidations, DeoptRetired, DeoptRetried,
                    CompileTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, DirectCallBodies, ResumeBodies, FallbackCount);
                PrintScreenReasons();
            };
        }

        /// <summary>
        /// The pre-screen's histogram, most frequent first. A node kind JitCompiler.Emit has
        /// since learned to compile showing up here is the screen having fallen behind it.
        /// </summary>
        private static void PrintScreenReasons() {
            if (_screenReasons == null || _screenReasons.Count == 0) { return; }
            var reasons = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(_screenReasons);
            reasons.Sort((a, b) => b.Value.CompareTo(a.Value));
            var text = new System.Text.StringBuilder("[jit] screen rejected:");
            for (int i = 0; i < reasons.Count && i < 8; i++) {
                text.AppendFormat(" {0}={1}", reasons[i].Key, reasons[i].Value);
            }
            Console.Error.WriteLine(text.ToString());
        }

        // ---- integer arithmetic with overflow deopt -------------------------------------
        // An Integer is an Int32 while it fits in one, an Int64 while it fits in that, and a
        // BigInteger beyond. The specialized code carries the first two unboxed, so all of its
        // integer arithmetic happens in long: two ints can be added, subtracted or multiplied
        // in 64 bits with no check at all, and only an operation with a long operand needs one.
        //
        // Overflow out of Int64 deopts rather than producing a BigInteger. Producing one would
        // make the static type of every arithmetic result `object', which is to say no
        // specialization at all; the deopt is one predictable branch per operation and happens
        // at most once per specialized region.

        // The int-in, int-out shapes. `a + b' on two ints is a long, but where it is stored
        // straight back into an int local the whole thing is one operation with one check, and
        // the compiler fuses it back into these rather than widen and narrow around a check.

        public static int AddInt(int a, int b) {
            long r = (long)a + b;
            int i = (int)r;
            if (i != r) { throw Deopt(); }
            return i;
        }

        /// <summary>
        /// What a JIT-compiled method does before it calls itself (JitCompiler.StackCheckPlacer): a recursion too deep
        /// is a SystemStackError (RubyOps.RequireSufficientStack). A JIT method makes no scope, so the check the
        /// interpreter makes there is not made.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void CheckStack() {
            // StackLimit.IsSufficient written out, so the main thread's test is inlined whole into the method: a
            // thread-static read there cost fib(30) more than half its time again
            byte probe;
            ulong sp = (ulong)&probe;
            if (sp - StackLimit._mainLow >= StackLimit._mainSize && !StackLimit.IsSufficientOffMain(sp)) {
                RubyOps.ThrowStackLevelTooDeep();
            }
        }

        public static int SubInt(int a, int b) {
            long r = (long)a - b;
            int i = (int)r;
            if (i != r) { throw Deopt(); }
            return i;
        }

        public static int MulInt(int a, int b) {
            long r = (long)a * b;
            int i = (int)r;
            if (i != r) { throw Deopt(); }
            return i;
        }

        /// <summary>Ruby Integer#/ is floor division and raises on a zero divisor.</summary>
        public static int DivInt(int a, int b) {
            if (b == 0 || (a == Int32.MinValue && b == -1)) { throw Deopt(); }
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) { q--; }
            return q;
        }

        /// <summary>Ruby Integer#% takes the sign of the divisor.</summary>
        public static int ModInt(int a, int b) {
            if (b == 0 || (a == Int32.MinValue && b == -1)) { throw Deopt(); }
            int m = a % b;
            if (m != 0 && ((m < 0) != (b < 0))) { m += b; }
            return m;
        }

        /// <summary>Integer#&lt;&lt; within a long; a result past a long (a Bignum) deopts.</summary>
        public static long ShlLong(long a, long s) {
            if (s < 0) { return ShrLong(a, s == Int64.MinValue ? Int64.MaxValue : -s); }
            if (a == 0) { return 0; }
            if (s >= 63) { throw Deopt(); }
            long r = a << (int)s;
            if ((r >> (int)s) != a) { throw Deopt(); }
            return r;
        }

        /// <summary>Integer#&gt;&gt;: an arithmetic shift, -1 or 0 once every bit is gone.</summary>
        public static long ShrLong(long a, long s) {
            if (s < 0) { return ShlLong(a, s == Int64.MinValue ? Int64.MaxValue : -s); }
            return (s >= 63) ? (a >> 63) : (a >> (int)s);
        }

        /// <summary>Integer#&gt;&gt; on an int; a negative width that leaves the int range deopts.</summary>
        public static int ShrInt(int a, long s) {
            if (s >= 0) { return (s >= 31) ? (a >> 31) : (a >> (int)s); }
            long r = ShlLong(a, s == Int64.MinValue ? Int64.MaxValue : -s);
            if (r != (int)r) { throw Deopt(); }
            return (int)r;
        }

        public static long AddLong(long a, long b) {
            long r = unchecked(a + b);
            // Signed overflow iff both operands differ in sign from the result.
            if (((a ^ r) & (b ^ r)) < 0) { throw Deopt(); }
            return r;
        }

        public static long SubLong(long a, long b) {
            long r = unchecked(a - b);
            // Signed overflow iff the operands differ in sign and the result differs from a.
            if (((a ^ b) & (a ^ r)) < 0) { throw Deopt(); }
            return r;
        }

        public static long MulLong(long a, long b) {
            long low;
            long high = Math.BigMul(a, b, out low);
            // The 128-bit product fits in 64 bits iff the high half is the sign extension.
            if (high != (low >> 63)) { throw Deopt(); }
            return low;
        }

        /// <summary>Ruby Integer#/ is floor division and raises on a zero divisor.</summary>
        public static long DivLong(long a, long b) {
            if (b == 0 || (a == Int64.MinValue && b == -1)) { throw Deopt(); }
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) { q--; }
            return q;
        }

        /// <summary>Ruby Integer#% takes the sign of the divisor.</summary>
        public static long ModLong(long a, long b) {
            if (b == 0 || (a == Int64.MinValue && b == -1)) { throw Deopt(); }
            long m = a % b;
            if (m != 0 && ((m < 0) != (b < 0))) { m += b; }
            return m;
        }

        /// <summary>
        /// A typed body's value as the object its entry returns: an Int32 or a Boolean comes out of
        /// the shared boxes (the DLR's cache of small Integers, the two Booleans) rather than a new
        /// box per call, the way every other place in the runtime makes one.
        /// </summary>
        internal static MSA.Expression/*!*/ BoxResult(MSA.Expression/*!*/ value) {
            if (value.Type == typeof(int)) {
                return Ast.Call(typeof(ScriptingRuntimeHelpers).GetMethod("Int32ToObject", new[] { typeof(int) }), value);
            }
            if (value.Type == typeof(bool)) {
                return Ast.Call(typeof(ScriptingRuntimeHelpers).GetMethod("BooleanToObject", new[] { typeof(bool) }), value);
            }
            return Ast.Convert(value, typeof(object));
        }

        /// <summary>
        /// The one representation rule, as the specialized code sees it: a value that fits in an
        /// Int32 IS an Int32, boxed from the small-integer cache. Every long that leaves the
        /// specialized region - a return value, a store back into a locals tuple, an ivar write -
        /// goes through here, which is what keeps #hash, #eql?, #equal? and Hash keys right for a
        /// value the JIT produced. Mirrors ClrInteger.Narrow, which lives in the other assembly.
        /// </summary>
        public static object/*!*/ NarrowLong(long v) {
            return (v >= Int32.MinValue && v <= Int32.MaxValue)
                ? ScriptingRuntimeHelpers.Int32ToObject((Int32)v) : (object)v;
        }

        /// <summary>Is this boxed value one the unboxed long representation can carry?</summary>
        public static bool IsIntegral(object value) {
            return value is int || value is long;
        }

        public static long ToLong(object value) {
            return (value is int) ? (long)(int)value : (long)value;
        }

        /// <summary>
        /// A long flowing into a slot the specialization typed as int - an int local, an int
        /// parameter of a self-call. It only fits when the value fits, and if it does not the
        /// Ruby value has genuinely grown past Int32: deopt and let the generic body carry it.
        /// </summary>
        public static int DemoteToInt(long v) {
            if (v < Int32.MinValue || v > Int32.MaxValue) { throw Deopt(); }
            return (int)v;
        }

        public static double DivDouble(double a, double b) {
            return a / b;
        }

        /// <summary>Ruby Float#% takes the sign of the divisor (IEEE remainder does not).</summary>
        public static double ModDouble(double a, double b) {
            double m = a % b;
            if (m != 0.0 && ((m < 0.0) != (b < 0.0))) { m += b; }
            return m;
        }

        public static double PowDouble(double a, double b) {
            return Math.Pow(a, b);
        }

        internal static Exception Deopt() {
            Deopts++;
            _deoptThrown = System.Diagnostics.Stopwatch.GetTimestamp();
            return JitDeoptException.Instance;
        }

        // When this thread last threw a deopt, for the method entry that catches it to know what
        // the exception cost (EntryDeopted). Zero once taken.
        [ThreadStatic]
        private static long _deoptThrown;

        // ---- safe points -----------------------------------------------------------------

        /// <summary>
        /// The safe point at the top of every iteration of a compiled loop (JitCompiler.EmitLoopSafePoint) compares
        /// the safe-point word (RubyUtils.SafePointWord) with the code epoch the code was compiled at. The word
        /// moves when safe-point work is waiting (a Thread#raise, a Thread#kill, a signal, a finalizer) or the
        /// code epoch does (a method table or a constant changed, on any thread), so the one compare that saw
        /// interrupts now sees both.
        ///
        /// In an outlined (OSR) loop the copy hands the loop back at the iteration boundary when it has moved:
        /// the copy keeps the Ruby locals in CLR locals, so it must not let a Thread#raise escape from the middle
        /// of itself - the tuples would be left holding the values from the loop's entry - and it must not go on
        /// with a method it inlined or bound (Array#size, a callee) or a constant it folded after another thread
        /// changed it: CRuby calls the new method at the next call. The deopt path stores the locals home; the
        /// generic loop's own safe point (RubyUtils.SafePoint, first thing on its back edge) delivers the
        /// exception, and the generic loop calls what is defined now. OsrLoopSite.Run counts none of it as a
        /// failed specialization: it builds a copy for the new epoch (the move may have changed nothing the copy
        /// was built on, a constant it did not fold, but the copy compares with its own epoch).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Exception/*!*/ OsrHandBack() {
            if (Osr.OsrLoopSite.Verbose) {
                Console.Error.WriteLine("[osr]   safe point: {0}, handed back",
                    RubyUtils.IsSafePointRequested ? "an interrupt is waiting" : "a method or a constant changed");
            }
            return JitDeoptException.Instance;
        }

        /// <summary>
        /// The slow path of a typed method body's loop safe point, taken when the safe-point word is neither the
        /// epoch the body was compiled at nor the one the loop last saw. What is waiting runs here and the body
        /// goes on - unless <paramref name="deopt"/> says the body writes Hash or Array elements: a trap handler
        /// run in the middle of those could change an element a later deopt takes back to what it was before the
        /// handler ran. Such a body deopts instead (it is one that may: JitCompiler takes no impure body that can
        /// deopt), and the generic body runs what is waiting at its first safe point.
        ///
        /// A method or a constant the body was compiled under that changed since - on another thread, or in the
        /// handler just run - deopts a body that may deopt (guard.MayDeopt: nothing it has done is observable, so
        /// the generic body restarts the call, and calls the new method); an impure one cannot, and goes on with
        /// what it was compiled with until it returns. Answers the epoch the loop is to compare with from now on.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long MethodLoopSafePointSlow(JitLoopGuard/*!*/ guard, StrongBox<bool>/*!*/ deopt) {
            if (RubyUtils.IsSafePointRequested) {
                if (deopt.Value) { throw SafePointDeopt(); }
                RubyUtils.SafePoint();
            }
            // read before the versions: a change after this read moves the word past it again
            long epoch = RubyUtils.CodeEpoch;
            if (guard.Stale) {
                // only constants moved, and each the body folded is what it was: the body is current (as at its entry,
                // JitStub.RevalidateConstants), and the loop goes on
                if (guard.ConstantsStillHold()) {
                    guard.Epoch = epoch;
                    return epoch;
                }
                if (guard.MayDeopt) {
                    if (Verbose) { Console.Error.WriteLine("[jit] safe point: a method or a constant the body was compiled under changed, deopt"); }
                    throw SafePointDeopt();
                }
                // an impure body: it goes on, and does not look again until something else changes
                return epoch;
            }
            // what changed is nothing the body was built on (a constant it did not fold): it is still current
            guard.Epoch = epoch;
            return epoch;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception/*!*/ SafePointDeopt() {
            return JitDeoptException.Instance;
        }

        // ---- guards ----------------------------------------------------------------------

        /// <summary>
        /// The entry guard: the global method version has not moved since specialization. Any
        /// def/undef/alias/include/singleton-method anywhere bumps RubyModule's global method
        /// version, and so does a method coming or going in a refinement (it re-versions the
        /// classes below the refined module), so this one check covers method redefinition,
        /// overriding in a subclass, singleton methods and refinements.
        /// </summary>
        public static bool VersionOk(int snapshot) {
            return RubyModule.GlobalMethodVersion == snapshot;
        }

        /// <summary>A method entry's VersionOk failed, or its ConstantsCurrent: it is stale for good (see JitStub.VersionChanged).</summary>
        public static void EntryStale(object/*!*/ specialization) {
            var spec = (JitStub.Specialization)specialization;
            spec.Stub.VersionChanged(spec);
        }

        /// <summary>A method entry's typed body deopted (see JitStub.OnDeopt).</summary>
        public static void EntryDeopted(object/*!*/ specialization) {
            long caught = System.Diagnostics.Stopwatch.GetTimestamp();
            long thrown = _deoptThrown;
            _deoptThrown = 0;
            var spec = (JitStub.Specialization)specialization;
            spec.Stub.OnDeopt(spec, (thrown != 0) ? caught - thrown : -1);
        }

        /// <summary>
        /// An outlined loop was compiled under an older method or constant version: hand it back, and the
        /// site, which finds its guard stale, builds it again (OsrLoopSite.Run) without counting a deopt.
        /// </summary>
        public static object OsrVersionStale() {
            if (Osr.OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   guard failed: method version"); }
            return Osr.OsrLoopSite.Retry;
        }

        /// <summary>An entry guard of an outlined loop did not hold; hand the loop back.</summary>
        public static object OsrGuardFailed(string/*!*/ what) {
            if (Osr.OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   guard failed: {0}", what); }
            return Osr.OsrLoopSite.Retry;
        }

        /// <summary>
        /// A local does not hold the CLR type the copy was built for. That is a different answer
        /// from a plain guard failure: the usual reason is that an accumulator grew out of Int32
        /// and is now a long, so a copy built over the types it holds *now* would run. The site
        /// rebuilds one, a bounded number of times.
        /// </summary>
        public static object OsrTypeGuardFailed(string/*!*/ what) {
            if (Osr.OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   type guard failed: {0}", what); }
            return Osr.OsrLoopSite.Retype;
        }

        /// <summary>A loop read a local it has not written in this run: it holds nil.</summary>
        public static Exception/*!*/ OsrUnsetSlot() {
            return Deopt();
        }

        // ---- keyword parameters (see JitKeywords) ---------------------------------------

        /// <summary>The entry guard of a method with keywords: the prologue would bind them without raising.</summary>
        public static bool KeywordsMatch(object/*!*/ keywords, object kw) {
            return ((JitKeywords)keywords).Matches(kw);
        }

        /// <summary>The value the prologue binds keyword number <paramref name="index"/> to.</summary>
        public static object KeywordValue(object/*!*/ keywords, object kw, int index) {
            return ((JitKeywords)keywords).Value(kw, index);
        }

        // KeywordsMatch and KeywordValue in one call, for a method with up to three keywords.

        public static bool KeywordsRead1(object/*!*/ keywords, object kw, out object v0) {
            return ((JitKeywords)keywords).Read(kw, out v0);
        }

        public static bool KeywordsRead2(object/*!*/ keywords, object kw, out object v0, out object v1) {
            return ((JitKeywords)keywords).Read(kw, out v0, out v1);
        }

        public static bool KeywordsRead3(object/*!*/ keywords, object kw, out object v0, out object v1, out object v2) {
            return ((JitKeywords)keywords).Read(kw, out v0, out v1, out v2);
        }

        public static RubyClass/*!*/ ClassOf(RubyContext/*!*/ context, object self) {
            IRubyObject obj = self as IRubyObject;
            return obj != null ? obj.ImmediateClass : context.GetImmediateClassOf(self);
        }

        // ---- global variables without a scope --------------------------------------------

        /// <summary>
        /// `$x' in a compiled body (JitCompiler.EmitGlobalRead): the site's cached variable, when it is
        /// an ordinary global, which is what RubyOps.ReadCachedGlobalVariable reads without its scope.
        /// Anything else - a special variable, one not defined (the generic code warns), a non-main
        /// Ractor's isolation - deopts, and the generic code reads it as it always does.
        /// </summary>
        public static object GetGlobal(RubyContext/*!*/ context, GlobalVariableSite/*!*/ site, object variable) {
            return GetOrdinaryGlobal(context, site, variable).Value;
        }

        /// <summary>
        /// `$x = v' in a compiled body (JitCompiler.EmitGlobalWrite): an ordinary global nobody traces is assigned in
        /// place, as RubyOps.SetCachedGlobalVariable assigns it. Anything else deopts before storing anything.
        /// </summary>
        public static object SetGlobal(RubyContext/*!*/ context, GlobalVariableSite/*!*/ site, object variable, object value) {
            var plain = GetOrdinaryGlobal(context, site, variable);
            if (context.AnyGlobalVariableTraced) {
                throw Deopt();
            }
            plain.Assign(value);
            return value;
        }

        // The variable the body was compiled for, which the name still denotes: an alias made since (or the
        // variable removed) gives the name another variable, and the body may then be reading one it writes.
        private static GlobalVariableInfo/*!*/ GetOrdinaryGlobal(RubyContext/*!*/ context, GlobalVariableSite/*!*/ site, object variable) {
            if ((context.RactorsStarted && RactorIsolation.IsNonMain(context)) || (object)site.GetVariable(context) != variable) {
                throw Deopt();
            }
            return (GlobalVariableInfo)variable;
        }

        // ---- instance variables without a scope ------------------------------------------

        // Each `@x` in a compiled body has an inline cache of its own: the same shape-checked fast
        // path as the interpreter's and the DLR compiler's (see RubyInstanceData).
        // The cached read of a plain Ruby object is inlined into the body (RubyOps.TryReadCached);
        // a plain object needs no isolation check.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object GetIVar(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            object value;
            if (self is RubyObject obj && RubyOps.TryReadCached(obj._instanceData, site, out value)) {
                return value;
            }
            return GetIVarUncached(context, self, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object GetIVarUncached(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            RequireIsolationCheckFree(context, self, site);
            return RubyOps.ReadInstanceVariable(context, self, site);
        }

        /// <summary>
        /// An instance variable of anything but a plain Ruby object, in a non-main Ractor: the
        /// generic code does what RactorIsolation says about it, raising from a frame of its own.
        /// </summary>
        private static void RequireIsolationCheckFree(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            if (context.RactorsStarted && !(self is RubyObject) && !site.IsCoreLibrary && RactorIsolation.IsNonMain(context)) {
                throw Deopt();
            }
        }

        /// <summary>
        /// `x', `self.x' or `o.x' in an outlined loop where x was, on the receiver, an attr_reader when
        /// the loop was compiled (JitCompiler.EmitCall): one per such call. Or `o.x = v' where x= was
        /// an attr_writer (JitCompiler.EmitAttributeWrite), if <see cref="Writer"/>.
        /// </summary>
        public sealed class SelfAttrSite {
            internal readonly string/*!*/ Name;
            internal readonly string/*!*/ VariableName;
            internal readonly InstanceVariableSite/*!*/ Variable;
            // `o.x' with an explicit receiver other than self can only call a public x.
            internal readonly bool PublicOnly;
            internal readonly bool Writer;

            // The class the reader was last found on, under which global method version. Replaced
            // as a whole, so a racing reader sees one consistent pair.
            private sealed class Binding {
                internal readonly RubyClass/*!*/ Class;
                internal readonly int Version;
                internal Binding(RubyClass/*!*/ cls, int version) { Class = cls; Version = version; }
            }
            private volatile Binding _bound;

            internal SelfAttrSite(string/*!*/ name, string/*!*/ variableName, bool publicOnly)
                : this(name, variableName, publicOnly, false) {
            }

            internal SelfAttrSite(string/*!*/ name, string/*!*/ variableName, bool publicOnly, bool writer) {
                Name = name;
                PublicOnly = publicOnly;
                Writer = writer;
                VariableName = variableName;
                Variable = new InstanceVariableSite(variableName);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool IsBoundTo(RubyClass/*!*/ cls) {
                var b = _bound;
                return b != null && b.Class == cls && b.Version == RubyModule.GlobalMethodVersion;
            }

            /// <summary>Whether Name is, on cls, the attr_reader (attr_writer) of VariableName.</summary>
            internal bool Bind(RubyClass/*!*/ cls) {
                int version = RubyModule.GlobalMethodVersion;
                if ((Writer ? WriterVariable(cls, Name, PublicOnly) : ReaderVariable(cls, Name, PublicOnly)) != VariableName) { return false; }
                _bound = new Binding(cls, version);
                return true;
            }

            /// <summary>
            /// What `name' calls on an object of cls, or not found when <paramref name="publicOnly"/> and a call
            /// with an explicit receiver would not reach it. The visibility is the lookup's and not the method's:
            /// `private :x' in a subclass leaves an entry there that only forwards to the inherited x
            /// (SuperForwarderInfo), the resolution follows it, and the method it finds is still public.
            /// </summary>
            internal static IronRuby.Runtime.Calls.MethodResolutionResult Resolve(RubyClass/*!*/ cls, string/*!*/ name, bool publicOnly) {
                var lookup = cls.GetMethodLookup(name);
                if (publicOnly && lookup.Visibility != RubyMethodVisibility.Public) {
                    return IronRuby.Runtime.Calls.MethodResolutionResult.NotFound;
                }
                return lookup.Result;
            }

            /// <summary>
            /// The instance variable `name' reads on cls, if it is an attr_reader there (a public one,
            /// if <paramref name="publicOnly"/>).
            /// </summary>
            internal static string ReaderVariable(RubyClass/*!*/ cls, string/*!*/ name, bool publicOnly) {
                var resolved = Resolve(cls, name, publicOnly);
                if (!resolved.Found) { return null; }
                var reader = resolved.Info as IronRuby.Runtime.Calls.RubyAttributeReaderInfo;
                return (reader != null) ? reader.InstanceVariableName : TrivialAccessorVariable(cls, resolved.Info, false);
            }

            /// <summary>
            /// The instance variable `name' writes on cls, if it is an attr_writer there (or a Ruby method
            /// that is one in all but name, see TrivialAccessorVariable).
            /// </summary>
            internal static string WriterVariable(RubyClass/*!*/ cls, string/*!*/ name, bool publicOnly) {
                var resolved = Resolve(cls, name, publicOnly);
                if (!resolved.Found) { return null; }
                var writer = resolved.Info as IronRuby.Runtime.Calls.RubyAttributeWriterInfo;
                return (writer != null) ? writer.InstanceVariableName : TrivialAccessorVariable(cls, resolved.Info, true);
            }

            /// <summary>
            /// `def x; @x; end' reads @x and `def x=(v); @x = v; end' (by whatever names) writes it, as
            /// attr_reader and attr_writer do, and nothing a call of either does is observable but that
            /// - not even in a backtrace, since a read cannot raise and a write that would deopts to the
            /// real call first. So an outlined loop reads or writes such a method's variable as it would
            /// an attribute's, as TruffleRuby inlines trivial methods. Null if the method is not one.
            /// </summary>
            private static string TrivialAccessorVariable(RubyClass/*!*/ cls, IronRuby.Runtime.Calls.RubyMemberInfo/*!*/ info, bool writer) {
                var method = info as IronRuby.Runtime.Calls.RubyMethodInfo;
                // nor under coverage: the call counts the method and its line, which a read of the variable would not
                if (method == null || cls.Context.RubyOptions.EnableTracing || cls.Context.RubyOptions.Profile ||
                    method.Body.Coverage != null) { return null; }

                var ast = method.GetSyntaxTree();
                var parameters = ast.Parameters;
                int arity = writer ? 1 : 0;
                if (parameters.Mandatory.Length != arity || parameters.Optional.Length != 0 || parameters.Unsplat != null ||
                    parameters.Block != null || parameters.SuperSplat != null) {
                    return null;
                }
                var signature = parameters.Signature;
                if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords ||
                    signature.MinPositionalCount != arity || signature.MaxPositionalCount != arity)) {
                    return null;
                }

                var body = ast.Body;
                if (body == null || body.RescueClauses != null && body.RescueClauses.Count != 0 || body.ElseStatements != null ||
                    body.EnsureStatements != null || body.Statements.Count != 1) {
                    return null;
                }

                var statement = body.Statements.First;
                if (!writer) {
                    var read = statement as IronRuby.Compiler.Ast.InstanceVariable;
                    return (read != null) ? read.Name : null;
                }

                var assignment = statement as IronRuby.Compiler.Ast.SimpleAssignmentExpression;
                var parameter = parameters.Mandatory[0] as IronRuby.Compiler.Ast.LocalVariable;
                var target = (assignment != null && assignment.Operation == null) ? assignment.Left as IronRuby.Compiler.Ast.InstanceVariable : null;
                var value = (target != null) ? assignment.Right as IronRuby.Compiler.Ast.LocalVariable : null;
                return (parameter != null && value != null && value.Name == parameter.Name &&
                    value.DefinitionLexicalDepth == parameter.DefinitionLexicalDepth) ? target.Name : null;
            }
        }

        /// <summary>
        /// The read an attr_reader does, as long as the method `site' names is still that reader on
        /// self's class: re-checked against the class and the global method version on every read,
        /// so a redefinition, a singleton method or a different self deopts to the generic loop,
        /// which then makes the call. A read is not observable, so the deopt is invisible.
        /// </summary>
        // The fast path - a plain Ruby object of the bound class, the variable at the slot the
        // site has seen - is inlined into the body (as GetIVar's is): RubyOps.TryReadCached keeps
        // RubyInstanceData's read protocol. Everything else, binding included, is out of line.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object ReadSelfAttr(RubyContext/*!*/ context, object self, SelfAttrSite/*!*/ site) {
            object value;
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass) && RubyOps.TryReadCached(obj._instanceData, site.Variable, out value)) {
                return value;
            }
            return ReadSelfAttrUncached(context, self, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object ReadSelfAttrUncached(RubyContext/*!*/ context, object self, SelfAttrSite/*!*/ site) {
            var obj = self as RubyObject;
            RubyClass cls = (obj != null) ? obj.ImmediateClass : ClassOf(context, self);
            if (!site.IsBoundTo(cls) && !site.Bind(cls)) {
                throw Deopt();
            }
            if (obj != null) {
                var data = obj._instanceData;
                if (data == null) {
                    return null;
                }
                // The site's second shape is read here, by the protocol of the inlined read (element 0 is
                // a shape, so the array is live and the index is its slot): a site that sees objects in
                // two shapes in turn reads the second without the lookup's call.
                object[] slots = data._ivars;
                var entry = site.Variable.Cache;
                object shape = RubyInstanceData.ShapeOf(slots);
                if (shape == entry.OtherShape) {
                    return RubyInstanceData.ValueOf(RubyInstanceData.GetSlot(slots, entry.OtherIndex));
                }
                // An object of a class whose instances are born raw (RubyInstanceData, "Float locations")
                // comes here on every read, since the inlined read only knows plain shapes: its hit is
                // taken here, after the one class check, and not left to GetInstanceVariable(site), which
                // costs such a class's Integer and object attributes a fifth more.
                if (shape == entry.Shape && entry.PlainShape == null && entry.Next == null) {
                    return RubyInstanceData.ValueOf(RubyInstanceData.GetSlot(slots, entry.Index));
                }
                return data.GetInstanceVariable(site.Variable);
            }
            RequireIsolationCheckFree(context, self, site.Variable);
            return RubyOps.ReadInstanceVariable(context, self, site.Variable);
        }

        /// <summary>
        /// `o.x = v' in a method (JitCompiler.EmitMethodAttrWrite): the store the attr_writer does, once the
        /// site finds it to be that writer on o's class under the global method version. Anything else, and a
        /// frozen o (the real call raises its FrozenError), deopts before writing. Another thread may freeze o
        /// or redefine x= after the check: the store is then as if made just before, as a call already past
        /// its lookup would.
        /// </summary>
        // The fast path - the bound class, a store into an existing variable at the slot the site has
        // seen - is inlined into the body; RubyOps.TryWriteCached refuses a frozen object and keeps
        // RubyInstanceData's write protocol.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteAttr(RubyContext/*!*/ context, object self, object value, SelfAttrSite/*!*/ site) {
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass) && RubyOps.TryWriteCached(obj._instanceData, value, site.Variable)) {
                return;
            }
            WriteAttrUncached(context, self, value, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteAttrUncached(RubyContext/*!*/ context, object self, object value, SelfAttrSite/*!*/ site) {
            var obj = self as RubyObject;
            if (obj == null || (!site.IsBoundTo(obj.ImmediateClass) && !site.Bind(obj.ImmediateClass))) {
                throw Deopt();
            }
            var data = obj._instanceData;
            if (data != null && data._frozen) {
                throw Deopt();
            }
            SetIVar(context, self, value, site.Variable);
        }

        /// <summary>
        /// The write an attr_writer does, under the same checks as ReadSelfAttr's, of a variable the
        /// object already has: the slot's old value goes into the undo log, so a deopt later in the
        /// iteration takes the write back (JitUndoLog.Rollback) and the restart is invisible. Anything
        /// else - a frozen object (whose FrozenError needs the real call), a variable to add (a shape
        /// change), a full log, an object that is not a RubyObject - deopts before writing.
        /// </summary>
        public static void WriteSelfAttr(object self, object value, SelfAttrSite/*!*/ site, JitUndoLog/*!*/ log) {
            var obj = self as RubyObject;
            if (obj == null || (!site.IsBoundTo(obj.ImmediateClass) && !site.Bind(obj.ImmediateClass))) {
                throw Deopt();
            }
            var data = obj._instanceData;
            if (data == null || data._frozen || log.IsFull) {
                throw Deopt();
            }
            object[] slots = data._ivars;
            var shape = RubyInstanceData.ShapeOf(slots);
            var variable = site.Variable;
            var entry = variable.Cache;
            int index;
            if (shape == entry.Shape && entry.Next == null) {
                index = entry.Index;
            } else if (shape == entry.OtherShape) {
                index = entry.OtherIndex;
            } else {
                var current = (InstanceVariableShape)shape;
                index = current.IsSentinel ? 0 : current.IndexOf(variable.Name);
                if (index == 0) {
                    throw Deopt();
                }
                variable.Update(entry.Remember(current, index));
            }
            // a raw Float (RubyInstanceData, "Float locations") is logged boxed: its location is
            // replaced by the store and dead from then on
            object old = RubyInstanceData.ValueOf((InstanceVariableShape)shape, RubyInstanceData.GetSlot(slots, index));
            RubyInstanceData.SetSlot(slots, index, value);
            if (RubyInstanceData.ShapeOf(slots) != shape) {
                // the slots moved under the write (another thread added or removed a variable):
                // make the write again where they are now; the variable exists, so it is a store
                data.SetInstanceVariable(variable.Name, value);
            }
            log.Replaced(data, variable.Name, old);
        }

        /// <summary>
        /// `o.x' / `o.x = v' in a method where o, when the method was compiled, was a Struct and x (x=)
        /// its member reader (writer) (JitCompiler.EmitMethodAttrRead/Write): the read or store the
        /// member method does, re-checked as SelfAttrSite's is against o's class and the global method
        /// version. A frozen o deopts before the store, so the FrozenError comes from the real call.
        /// </summary>
        public sealed class StructMemberSite {
            internal readonly string/*!*/ Name;
            internal readonly int Index;
            internal readonly bool PublicOnly;
            internal readonly bool Writer;

            private sealed class Binding {
                internal readonly RubyClass/*!*/ Class;
                internal readonly int Version;
                internal Binding(RubyClass/*!*/ cls, int version) { Class = cls; Version = version; }
            }
            private volatile Binding _bound;

            internal StructMemberSite(string/*!*/ name, int index, bool publicOnly, bool writer) {
                Name = name; Index = index; PublicOnly = publicOnly; Writer = writer;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool IsBoundTo(RubyClass/*!*/ cls) {
                var b = _bound;
                return b != null && b.Class == cls && b.Version == RubyModule.GlobalMethodVersion;
            }

            internal bool Bind(RubyClass/*!*/ cls) {
                int version = RubyModule.GlobalMethodVersion;
                if (Member(cls, Name, PublicOnly, Writer) != Index) { return false; }
                _bound = new Binding(cls, version);
                return true;
            }

            /// <summary>The index of the Struct member `name' reads (writes) on cls, if it is that member method; else -1.</summary>
            internal static int Member(RubyClass/*!*/ cls, string/*!*/ name, bool publicOnly, bool writer) {
                var resolved = SelfAttrSite.Resolve(cls, name, publicOnly);
                if (!resolved.Found) { return -1; }
                var custom = resolved.Info as IronRuby.Runtime.Calls.RubyCustomMethodInfo;
                if (custom == null || custom.StructMember < 0 || custom.IsStructWriter != writer) { return -1; }
                return custom.StructMember;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object ReadStructMember(object self, StructMemberSite/*!*/ site) {
            if (self is RubyStruct s && site.IsBoundTo(s.ImmediateClass)) {
                return RubyStruct.GetValue(s, site.Index);
            }
            return ReadStructMemberUncached(self, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object ReadStructMemberUncached(object self, StructMemberSite/*!*/ site) {
            var s = self as RubyStruct;
            if (s == null || (!site.IsBoundTo(s.ImmediateClass) && !site.Bind(s.ImmediateClass))) {
                throw Deopt();
            }
            return RubyStruct.GetValue(s, site.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteStructMember(object self, object value, StructMemberSite/*!*/ site) {
            if (self is RubyStruct s && site.IsBoundTo(s.ImmediateClass) && !s.IsFrozen) {
                RubyStruct.SetValue(s, site.Index, value);
                return;
            }
            WriteStructMemberUncached(self, value, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteStructMemberUncached(object self, object value, StructMemberSite/*!*/ site) {
            var s = self as RubyStruct;
            if (s == null || (!site.IsBoundTo(s.ImmediateClass) && !site.Bind(s.ImmediateClass)) || s.IsFrozen) {
                throw Deopt();
            }
            RubyStruct.SetValue(s, site.Index, value);
        }

        // The store into an existing variable of a plain Ruby object, at the slot the site has
        // seen, is inlined into the body (RubyOps.TryWriteCached keeps RubyInstanceData's protocol).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object SetIVar(RubyContext/*!*/ context, object self, object value, InstanceVariableSite/*!*/ site) {
            if (self is RubyObject obj && RubyOps.TryWriteCached(obj._instanceData, value, site)) {
                return value;
            }
            return SetIVarUncached(context, self, value, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object SetIVarUncached(RubyContext/*!*/ context, object self, object value, InstanceVariableSite/*!*/ site) {
            var obj = self as RubyObject;
            if (obj != null) {
                var data = obj._instanceData ?? RubyOps.GetInstanceData(ref obj._instanceData);
                if (!data._frozen && data.TrySetCached(site.Cache, value, obj)) {
                    return value;
                }
            } else if (RubyOps.TrySetRubyObjectInstanceVariable(self, value, site)) {
                // an instance of a Ruby subclass of Array, Hash, Exception, Enumerator...
                return value;
            }
            // A frozen receiver raises FrozenError, and a typed body has no frame to raise it from:
            // let the generic body run instead (see JitCompiler._ivarWrite).
            if (context.IsObjectFrozen(self)) {
                throw Deopt();
            }
            RequireIsolationCheckFree(context, self, site);
            context.SetInstanceVariable(self, value, site);
            return value;
        }

        /// <summary>
        /// The opening run of instance variable writes of a specialized method (JitCompiler.
        /// EmitInitialIVars): the shape a fresh object has after all of them.
        /// </summary>
        public sealed class InitialIVarsSite {
            internal readonly InstanceVariableShape/*!*/ Shape;

            private InitialIVarsSite(InstanceVariableShape/*!*/ shape) {
                Shape = shape;
            }

            /// <summary>Null if the variables would make an object too complex for shapes.</summary>
            internal static InitialIVarsSite TryCreate(string/*!*/[]/*!*/ names) {
                var shape = InstanceVariableShape.Root;
                foreach (var name in names) {
                    shape = shape.GetChild(name);
                    if (shape == null) { return null; }
                }
                return new InitialIVarsSite(shape);
            }
        }

        /// <summary>
        /// The slot array a fresh object would get the run's variables in - element 0 already the
        /// final shape, the values to be stored by the caller - or null if self is not a RubyObject
        /// without instance variables, or is frozen: then the writes are made one by one.
        /// </summary>
        public static object[] BeginInitialIVars(object self, InitialIVarsSite/*!*/ site) {
            var obj = self as RubyObject;
            if (obj == null) { return null; }
            var data = obj._instanceData;
            if (data != null && (data._frozen || data._ivars != InstanceVariableShape.EmptySlots)) { return null; }
            int count = site.Shape.Count;
            var result = new object[Math.Max(count, obj.ImmediateClass.InstanceVariableCapacity) + 1];
            // born raw if its class's instances get their Floats promoted (RubyInstanceData, "Float locations")
            result[0] = obj.ImmediateClass.FloatsRaw ? (site.Shape.RawTwin ?? site.Shape) : site.Shape;
            return result;
        }

        /// <summary>
        /// Publishes the array BeginInitialIVars made and the caller filled. False if the object
        /// got variables, or was frozen, in the meantime: the caller then makes the writes one by one.
        /// </summary>
        public static bool CommitInitialIVars(object self, object[]/*!*/ slots) {
            var obj = (RubyObject)self;
            var data = obj._instanceData;
            if (data == null) {
                var fresh = new RubyInstanceData();
                fresh._ivars = slots;
                if (System.Threading.Interlocked.CompareExchange(ref obj._instanceData, fresh, null) == null) {
                    RubyInstanceData.UpdateCapacityHint(obj.ImmediateClass, ((InstanceVariableShape)slots[0]).Count);
                    return true;
                }
                data = obj._instanceData;
            }
            if (data._frozen) { return false; }
            if (System.Threading.Interlocked.CompareExchange(ref data._ivars, slots, InstanceVariableShape.EmptySlots) == InstanceVariableShape.EmptySlots) {
                RubyInstanceData.UpdateCapacityHint(obj.ImmediateClass, ((InstanceVariableShape)slots[0]).Count);
                return true;
            }
            return false;
        }

        // ---- Float instance variables stored raw (RubyInstanceData, "Float locations") -------------
        // The typed code carries a Float as a double. Its reads of a variable it has seen hold a Float
        // (GuardDouble of GetIVar / ReadSelfAttr) and its stores of a double are these: the same checks
        // and the same fast path as their object versions, reading and writing the variable's
        // FloatLocation without a box. Anything else goes the object version's way, boxed.

        /// <summary>GuardDouble(GetIVar(...)) without boxing a raw Float (JitCompiler.GuardIVarRead).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double GetIVarDouble(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            if (self is RubyObject obj) {
                var data = obj._instanceData;
                if (data != null) {
                    object[] slots = data._ivars;
                    var entry = site.Cache;
                    if (RubyInstanceData.ShapeOf(slots) == entry.Shape) {
                        // a box first: a plain object's read is the shape compare and GuardDouble's test,
                        // as before. A location is only ever in a raw array, so its type says it all.
                        object slot = RubyInstanceData.GetSlot(slots, entry.Index);
                        if (slot is double) {
                            return (double)slot;
                        }
                        if (slot is FloatLocation location) {
                            return location.Value;
                        }
                    }
                }
            }
            return GetIVarDoubleSlow(context, self, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double GetIVarDoubleSlow(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            // the site's second shape (RubyOps.TryReadCachedFloat), then the object read
            double value;
            if (self is RubyObject obj && RubyOps.TryReadCachedFloat(obj._instanceData, site, out value)) {
                return value;
            }
            return GuardDouble(GetIVar(context, self, site));
        }

        /// <summary>GuardDouble(ReadSelfAttr(...)) without boxing a raw Float (JitCompiler.GuardIVarRead).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ReadSelfAttrDouble(RubyContext/*!*/ context, object self, SelfAttrSite/*!*/ site) {
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass)) {
                var data = obj._instanceData;
                if (data != null) {
                    object[] slots = data._ivars;
                    var entry = site.Variable.Cache;
                    if (RubyInstanceData.ShapeOf(slots) == entry.Shape) {
                        // a box first: a plain object's read is the shape compare and GuardDouble's test,
                        // as before. A location is only ever in a raw array, so its type says it all.
                        object slot = RubyInstanceData.GetSlot(slots, entry.Index);
                        if (slot is double) {
                            return (double)slot;
                        }
                        if (slot is FloatLocation location) {
                            return location.Value;
                        }
                    }
                }
            }
            return ReadSelfAttrDoubleSlow(context, self, site);
        }

        // out of line, so that the inlined part stays a shape compare and a slot read
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadSelfAttrDoubleSlow(RubyContext/*!*/ context, object self, SelfAttrSite/*!*/ site) {
            // the site's second shape (RubyOps.TryReadCachedFloat), then the object read
            double value;
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass) && RubyOps.TryReadCachedFloat(obj._instanceData, site.Variable, out value)) {
                return value;
            }
            return GuardDouble(ReadSelfAttr(context, self, site));
        }

        /// <summary>SpeculateDouble(GetIVar(...)) without boxing a raw Float (JitCompiler.GuardIVarRead).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double GetIVarSpeculateDouble(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            if (self is RubyObject obj) {
                var data = obj._instanceData;
                if (data != null) {
                    object[] slots = data._ivars;
                    var entry = site.Cache;
                    if (RubyInstanceData.ShapeOf(slots) == entry.Shape) {
                        // a box first: a plain object's read is the shape compare and GuardDouble's test,
                        // as before. A location is only ever in a raw array, so its type says it all.
                        object slot = RubyInstanceData.GetSlot(slots, entry.Index);
                        if (slot is double) {
                            return (double)slot;
                        }
                        if (slot is FloatLocation location) {
                            return location.Value;
                        }
                    }
                }
            }
            return GetIVarSpeculateDoubleSlow(context, self, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double GetIVarSpeculateDoubleSlow(RubyContext/*!*/ context, object self, InstanceVariableSite/*!*/ site) {
            // the site's second shape (RubyOps.TryReadCachedFloat), then the object read
            double value;
            if (self is RubyObject obj && RubyOps.TryReadCachedFloat(obj._instanceData, site, out value)) {
                return value;
            }
            return SpeculateDouble(GetIVar(context, self, site));
        }

        /// <summary>SpeculateDouble(ReadSelfAttr(...)) without boxing a raw Float (JitCompiler.GuardIVarRead).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ReadSelfAttrSpeculateDouble(RubyContext/*!*/ context, object self, SelfAttrSite/*!*/ site) {
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass)) {
                var data = obj._instanceData;
                if (data != null) {
                    object[] slots = data._ivars;
                    var entry = site.Variable.Cache;
                    if (RubyInstanceData.ShapeOf(slots) == entry.Shape) {
                        // a box first: a plain object's read is the shape compare and GuardDouble's test,
                        // as before. A location is only ever in a raw array, so its type says it all.
                        object slot = RubyInstanceData.GetSlot(slots, entry.Index);
                        if (slot is double) {
                            return (double)slot;
                        }
                        if (slot is FloatLocation location) {
                            return location.Value;
                        }
                    }
                }
            }
            return ReadSelfAttrSpeculateDoubleSlow(context, self, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadSelfAttrSpeculateDoubleSlow(RubyContext/*!*/ context, object self, SelfAttrSite/*!*/ site) {
            // the site's second shape (RubyOps.TryReadCachedFloat), then the object read
            double value;
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass) && RubyOps.TryReadCachedFloat(obj._instanceData, site.Variable, out value)) {
                return value;
            }
            return SpeculateDouble(ReadSelfAttr(context, self, site));
        }

        /// <summary>SetIVar of a Float the body holds raw.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetIVarDouble(RubyContext/*!*/ context, object self, double value, InstanceVariableSite/*!*/ site) {
            if (self is RubyObject obj && RubyOps.TryWriteCachedFloat(obj._instanceData, value, site, obj.ImmediateClass)) {
                return;
            }
            SetIVarUncached(context, self, value, site);
        }

        /// <summary>WriteAttr of a Float the body holds raw.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteAttrDouble(RubyContext/*!*/ context, object self, double value, SelfAttrSite/*!*/ site) {
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass) && RubyOps.TryWriteCachedFloat(obj._instanceData, value, site.Variable, obj.ImmediateClass)) {
                return;
            }
            WriteAttrUncached(context, self, value, site);
        }

        /// <summary>
        /// WriteSelfAttr of a Float the body holds raw: the same checks and the same undo log entry, the
        /// old value logged raw when it was raw, so that neither the store nor its log entry boxes.
        /// </summary>
        public static void WriteSelfAttrDouble(object self, double value, SelfAttrSite/*!*/ site, JitUndoLog/*!*/ log) {
            var obj = self as RubyObject;
            if (obj == null || (!site.IsBoundTo(obj.ImmediateClass) && !site.Bind(obj.ImmediateClass))) {
                throw Deopt();
            }
            var data = obj._instanceData;
            if (data == null || data._frozen || log.IsFull) {
                throw Deopt();
            }
            object[] slots = data._ivars;
            var shape = RubyInstanceData.ShapeOf(slots);
            var variable = site.Variable;
            var entry = variable.Cache;
            int index;
            if (shape == entry.Shape && entry.Next == null) {
                index = entry.Index;
            } else if (shape == entry.OtherShape) {
                index = entry.OtherIndex;
            } else {
                var current = (InstanceVariableShape)shape;
                index = current.IsSentinel ? 0 : current.IndexOf(variable.Name);
                if (index == 0) {
                    throw Deopt();
                }
                variable.Update(entry.Remember(current, index));
            }
            object old = RubyInstanceData.GetSlot(slots, index);
            var plain = (InstanceVariableShape)shape;
            if (plain.IsRaw) {
                var location = old as FloatLocation;
                if (location != null) {
                    // read and written by this thread alone here; another thread's store in between is
                    // a race the generic code has too (the rollback would then undo it)
                    log.ReplacedFloat(data, variable.Name, location.Value);
                } else {
                    log.Replaced(data, variable.Name, old);
                }
                if (RubyInstanceData.TryWriteFloat(slots, shape, index, value)) {
                    return;
                }
            } else {
                // a boxed store, or the object's promotion (RubyInstanceData.TryWriteFloatPlain)
                log.Replaced(data, variable.Name, old);
                var now = data.TryWriteFloatPlain(slots, plain, index, value, obj.ImmediateClass);
                if (now != null) {
                    if (now != plain) {
                        variable.Update(variable.Cache.Remember(now, index));
                    }
                    return;
                }
            }
            // the slots moved under the write: make it again where they are now
            data.SetInstanceVariable(variable.Name, value);
        }

        // An instance variable used as a numeric operand of an outlined loop (JitCompiler.NumericIVar):
        // unboxed while it holds the type it held when the loop was compiled, a deopt once it does not.

        /// <summary>`!x' of an x speculated to be true, false or nil (JitCompiler.EmitNot).</summary>
        public static bool NotBoolean(object value) {
            if (value == null) { return true; }
            if (value is bool) { return !(bool)value; }
            throw Deopt();
        }

        // The guards are inlined into the body; the deopt is thrown out of line.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GuardInt(object value) {
            if (value is int) { return (int)value; }
            return ThrowDeopt<int>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long GuardLong(object value) {
            if (value is int) { return (int)value; }
            if (value is long) { return (long)value; }
            return ThrowDeopt<long>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double GuardDouble(object value) {
            if (value is double) { return (double)value; }
            return ThrowDeopt<double>();
        }

        /// <summary>The receiver of an inlined call (JitCompiler.TryInlineCall) is of the class the call was resolved on.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GuardClass(RubyContext/*!*/ context, object value, RubyClass/*!*/ cls) {
            IRubyObject obj = value as IRubyObject;
            if (obj == null || !ReferenceEquals(obj.ImmediateClass, cls)) { ThrowDeopt<int>(); }
        }

        /// <summary>Which of two classes an inlined call's receiver is of (JitCompiler.TryInlineCall).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsOfClass(object value, RubyClass/*!*/ cls) {
            IRubyObject obj = value as IRubyObject;
            return obj != null && ReferenceEquals(obj.ImmediateClass, cls);
        }

        // `raise ...' in a typed body (JitCompiler.EmitRaise): the generic body runs again and raises.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object RaiseDeopt() {
            throw Deopt();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T ThrowDeopt<T>() {
            throw Deopt();
        }

        // ---- Integer#abs (JitCompiler.EmitCall) ------------------------------------------

        /// <summary>Integer#abs of an Int64; -2**63, whose absolute value is a Bignum, is the generic code's to produce.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long AbsLong(long value) {
            if (value == long.MinValue) { throw Deopt(); }
            return (value < 0) ? -value : value;
        }

        // ---- Float#to_i (JitCompiler.EmitCall) -------------------------------------------

        /// <summary>
        /// Float#to_i of a Float whose Integer fits in an Int64; a NaN, an infinity (FloatDomainError)
        /// or a Bignum is the generic loop's to produce.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long FloatToLong(double value) {
            // -2^63 <= value < 2^63, and false for NaN
            if (value >= -9223372036854775808.0 && value < 9223372036854775808.0) { return (long)value; }
            throw Deopt();
        }

        // ---- reflection handles the compiler emits ---------------------------------------

        // `{k => v, ...}' in a typed body (JitCompiler.EmitHashLiteral): the Hash RubyOps.MakeHash and MakeSymbolHash1..3
        // make, of the comparer of the runtime the body was compiled for. The keys are plain literals (a Symbol, String,
        // Integer, nil, true or false), which the comparer hashes without calling #hash: no Ruby code runs.
        public static object NewHash(RubyContext/*!*/ context, object[]/*!*/ items) {
            return RubyUtils.SetHashElements(context, new Hash(context.EqualityComparer, items.Length / 2), items);
        }

        public static object NewSymbolHash1(RubyContext/*!*/ context, object key1, object value1) {
            var result = Hash.CreateUnlinked(context.EqualityComparer, 1);
            result.AppendUnlinked(key1, value1);
            return result;
        }

        public static object NewSymbolHash2(RubyContext/*!*/ context, object key1, object value1, object key2, object value2) {
            var result = Hash.CreateUnlinked(context.EqualityComparer, 2);
            result.AppendUnlinked(key1, value1);
            result.AppendUnlinked(key2, value2);
            return result;
        }

        public static object NewSymbolHash3(RubyContext/*!*/ context, object key1, object value1, object key2, object value2,
            object key3, object value3) {
            var result = Hash.CreateUnlinked(context.EqualityComparer, 3);
            result.AppendUnlinked(key1, value1);
            result.AppendUnlinked(key2, value2);
            result.AppendUnlinked(key3, value3);
            return result;
        }

        internal static MethodInfo/*!*/ M(string/*!*/ name) {
            return typeof(JitRuntime).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        }
    }
    /// <summary>
    /// What one piece of compiled code with a loop (an outlined loop's copy, a typed method body) was built
    /// under, for the safe point at the top of each iteration to check again when the code epoch has moved
    /// (JitCompiler.EmitLoopSafePoint). Filled in by whoever compiled it (JitLoopCompiler, JitStub) before
    /// the code first runs.
    /// </summary>
    public sealed class JitLoopGuard {
        /// <summary>
        /// The code epoch at which Version and ConstantVersion were last found current (RubyUtils.CodeEpoch):
        /// a loop starts from it, and takes its safe point's slow path only when the word has moved since.
        /// </summary>
        public long Epoch;
        internal int Version;
        internal int ConstantVersion;
        // the runtime whose constants the code folded; null if it folded none
        internal RubyContext ConstantsOf;
        // a typed method body may deopt at its loop's safe point (see MethodLoopSafePointSlow)
        internal bool MayDeopt;
        // what the typed method body folded (JitCode.FoldedConstants), for its loops' safe points to look up again, or what
        // an outlined loop's copy folded, for its entry to (JitRuntime.LoopConstantsCurrent); null if it folded none or
        // some it did not record. Set before the code runs.
        internal JitFoldedConstant[] Folded;

        internal static readonly System.Reflection.FieldInfo/*!*/ EpochField = typeof(JitLoopGuard).GetField("Epoch");

        /// <summary>
        /// Records the versions the code was compiled under, with the epoch read before them (an epoch read
        /// after could hide a change between the two reads).
        /// </summary>
        internal void Set(long epoch, int version, RubyContext constantsOf, int constantVersion) {
            Version = version;
            ConstantsOf = constantsOf;
            ConstantVersion = constantVersion;
            System.Threading.Volatile.Write(ref Epoch, epoch);
        }

        /// <summary>
        /// The method version is the one the code was compiled under, and each constant it folded resolves as it did
        /// (JitFoldedConstant.StillHolds): what moved the constant version was nothing the code folded, and the version
        /// read before the lookups is recorded as current. As JitStub.RevalidateConstants: a change between the read
        /// and the store has moved the version past the one stored, so the next safe point looks again.
        /// </summary>
        internal bool ConstantsStillHold() {
            var context = ConstantsOf;
            var folded = Folded;
            if (context == null || folded == null || RubyModule.GlobalMethodVersion != Version) { return false; }
            int version = System.Threading.Volatile.Read(ref context.ConstantAccessVersion);
            foreach (var constant in folded) {
                if (!constant.StillHolds(context)) { return false; }
            }
            if (context.IsAutoloadInProgress) { return false; }
            System.Threading.Volatile.Write(ref ConstantVersion, version);
            return true;
        }

        /// <summary>A method table or a constant the code was built on has changed since.</summary>
        internal bool Stale {
            get {
                return RubyModule.GlobalMethodVersion != Version ||
                    (ConstantsOf != null && System.Threading.Volatile.Read(ref ConstantsOf.ConstantAccessVersion) != ConstantVersion);
            }
        }
    }
}
