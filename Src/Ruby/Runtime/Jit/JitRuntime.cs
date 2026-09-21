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
using IronRuby.Builtins;

namespace IronRuby.Runtime.Jit {
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
    /// Runtime helpers called from JIT-compiled code, plus the global kill switch.
    /// </summary>
    public static class JitRuntime {
        /// <summary>
        /// Set when something the JIT cannot reason about shows up (refinements). Specialized
        /// code checks it on entry, so turning it on deoptimizes every specialization at once.
        /// </summary>
        internal static bool Disabled;

        /// <summary>IR_JIT_VERBOSE=1 traces what gets specialized and what is rejected.</summary>
        internal static readonly bool Verbose = Environment.GetEnvironmentVariable("IR_JIT_VERBOSE") == "1";

        /// <summary>
        /// Statistics, printed by -X:JITStats.
        /// </summary>
        internal static int Specialized, Rejected, Deopts, Invalidations;
        internal static long CompileTicks;

        internal static void Disable() {
            Disabled = true;
        }

        private static int _statsHooked;

        /// <summary>With IR_JIT_VERBOSE=1, print what the JIT did on the way out.</summary>
        internal static void HookStats() {
            if (!Verbose || System.Threading.Interlocked.Exchange(ref _statsHooked, 1) != 0) { return; }
            AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                Console.Error.WriteLine("[jit] specialized={0} rejected={1} deopts={2} invalidations={3} compile={4:F1}ms",
                    Specialized, Rejected, Deopts, Invalidations,
                    CompileTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            };
        }

        // ---- integer arithmetic with overflow deopt -------------------------------------
        // Fixnum is Int32 in IronRuby; overflowing into Bignum is a type change the specialized
        // code has no representation for, so it deopts. Computing in long makes the check one
        // compare rather than a call into the Bignum path.

        public static int AddInt(int a, int b) {
            long r = (long)a + b;
            int i = (int)r;
            if (i != r) { throw Deopt(); }
            return i;
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
            return JitDeoptException.Instance;
        }

        // ---- guards ----------------------------------------------------------------------

        /// <summary>
        /// The entry guard: the global method version has not moved since specialization and
        /// nothing has globally disabled the JIT. Any def/undef/alias/include/singleton-method
        /// anywhere bumps RubyModule's global method version, so this one check covers method
        /// redefinition, overriding in a subclass, and singleton methods.
        /// </summary>
        public static bool VersionOk(int snapshot) {
            return !Disabled && RubyModule.GlobalMethodVersion == snapshot;
        }

        public static RubyClass/*!*/ ClassOf(RubyContext/*!*/ context, object self) {
            IRubyObject obj = self as IRubyObject;
            return obj != null ? obj.ImmediateClass : context.GetImmediateClassOf(self);
        }

        // ---- instance variables without a scope ------------------------------------------

        public static object GetIVar(RubyContext/*!*/ context, object self, string/*!*/ name) {
            var data = context.TryGetInstanceData(self);
            return (data != null) ? data.GetInstanceVariable(name) : null;
        }

        public static object SetIVar(RubyContext/*!*/ context, object self, object value, string/*!*/ name) {
            context.SetInstanceVariable(self, name, value);
            return value;
        }

        // ---- reflection handles the compiler emits ---------------------------------------

        internal static MethodInfo/*!*/ M(string/*!*/ name) {
            return typeof(JitRuntime).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        }
    }
}
