/* ****************************************************************************
 *
 * The numeric library calls a renderer's loops are made of, compiled to what they compute:
 *
 *   x.abs                     Integer#abs, Float#abs
 *   f.ceil, f.floor, f.round  Float -> Integer, no digits argument (an Integer's is itself)
 *   x.clamp(lo, hi)           Comparable#clamp of an Integer or a Float, with bounds of its own kind
 *   [a, b, ...].min / .max    Array#min / #max of a literal of Integers, or of Floats
 *   a >> b, a << b            Integer#>> and #<< of two Integers
 *
 * Each only while it is the library's own method, public, and the `<=>' it compares with is the
 * built-in one (RequireBuiltin; the copy's method-version guard covers a later redefinition). What
 * the library method would raise for - a NaN compared, a clamp whose min is past its max, a Float
 * with no Integer value - deopts, and the generic code raises it.
 *
 * ***************************************************************************/

using System;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;

    internal sealed partial class JitCompiler {
        internal static bool IsNumericUnary(string/*!*/ name) {
            return name == "abs" || name == "ceil" || name == "floor" || name == "round";
        }

        /// <summary>`[a, b].min', `[a, b].max': a literal of 2 to MaxArrayLiteral plain elements.</summary>
        internal static bool IsLiteralMinMax(MethodCall/*!*/ node, int argc) {
            var literal = node.Target as ArrayConstructor;
            if (argc != 0 || literal == null || (node.MethodName != "min" && node.MethodName != "max")) { return false; }
            var items = literal.Arguments.Expressions;
            if (items.Length < 2 || items.Length > MaxArrayLiteral) { return false; }
            foreach (var item in items) {
                if (item is SplattedArgument) { return false; }
            }
            return true;
        }

        /// <summary>
        /// `x.nil?': false for a number; for an object, whether it is nil, while its class answers nil? with
        /// Kernel's (JitRuntime.NilQuery, which deopts otherwise).
        /// </summary>
        private MSA.Expression/*!*/ EmitNilQuery(RExpr/*!*/ target, out JT type) {
            JT tt;
            var t = Emit(target, out tt);
            type = JT.Bool;
            if (IsNumeric(tt) || tt == JT.Bool) {
                RequireBuiltin(tt, "nil?");
                return Ast.Block(t, Ast.Constant(false));
            }
            if (tt != JT.Obj) { throw Bail("nil? of a " + tt); }
            _canDeopt = true;
            return Ast.Call(JitRuntime.M("NilQuery"), Ast.Constant(_context), t, Ast.Constant(new JitRuntime.NilQuerySite()));
        }

        internal static bool IsShift(string/*!*/ name) {
            return name == ">>" || name == "<<";
        }

        /// <summary>
        /// `a >> b', `a << b' of two Integers: a negative count shifts the other way, a right shift by 64
        /// or more leaves the sign, and a left shift out of Int64 (a Bignum) deopts.
        /// </summary>
        private MSA.Expression/*!*/ EmitShift(string/*!*/ op, MSA.Expression/*!*/ l, JT lt, MSA.Expression/*!*/ r, JT rt, out JT type) {
            RequireBuiltin(lt, op);
            _canDeopt = true;
            type = JT.Lng;
            return Ast.Call(JitRuntime.M(op == ">>" ? "ShiftRightLong" : "ShiftLeftLong"), Coerce(l, lt, JT.Lng), Coerce(r, rt, JT.Lng));
        }

        /// <summary>`x.abs', `f.ceil', `f.floor', `f.round' of a number <paramref name="t"/> of type <paramref name="tt"/>.</summary>
        private MSA.Expression/*!*/ EmitNumericUnary(string/*!*/ name, MSA.Expression/*!*/ t, JT tt, out JT type) {
            RequireBuiltin(tt, name);
            if (name == "abs") {
                switch (tt) {
                    case JT.Dbl: type = JT.Dbl; return Ast.Call(typeof(Math).GetMethod("Abs", new[] { typeof(double) }), t);
                    // |Int32.MinValue| is a long, |Int64.MinValue| a Bignum
                    case JT.Int: type = JT.Lng; return Ast.Call(typeof(Math).GetMethod("Abs", new[] { typeof(long) }), Ast.Convert(t, typeof(long)));
                    default: _canDeopt = true; type = JT.Lng; return Ast.Call(JitRuntime.M("AbsLong"), t);
                }
            }
            if (tt != JT.Dbl) {
                // Integer#ceil, #floor and #round with no digits are the Integer itself
                type = tt;
                return t;
            }
            _canDeopt = true;
            type = JT.Lng;
            string helper = (name == "ceil") ? "CeilToLong" : (name == "floor") ? "FloorToLong" : "RoundToLong";
            return Ast.Call(JitRuntime.M(helper), t);
        }

        /// <summary>
        /// One operand of clamp, min or max that is an object - an element, an attribute - next to
        /// numbers of kind <paramref name="kind"/>: unboxed through a guard, so that the generic code
        /// takes any other value (and any mix of Integers and Floats, whose result keeps its kind).
        /// </summary>
        private MSA.Expression/*!*/ SpeculateOperand(MSA.Expression/*!*/ e, ref JT t, JT kind) {
            if (t != JT.Obj) { return e; }
            _canDeopt = true;
            if (kind == JT.Dbl) {
                t = JT.Dbl;
                return Ast.Call(JitRuntime.M("GuardDouble"), e);
            }
            t = JT.Lng;
            return Ast.Call(JitRuntime.M("GuardLong"), e);
        }

        /// <summary>
        /// What the operands of clamp, min or max are compared as: Dbl if every one is a Float, Int if
        /// every one is an Int32, Lng if every one is an Integer; None for a mix of Integers and Floats
        /// (Comparable#clamp and Array#min answer one of the operands, so the kind of the answer
        /// depends on the values), or when none is known to be a number.
        /// </summary>
        private static JT CompareKind(JT[]/*!*/ types) {
            JT kind = JT.None;
            foreach (var t in types) {
                if (t == JT.Obj) { continue; }
                JT k = (t == JT.Dbl) ? JT.Dbl : (t == JT.Int || t == JT.Lng) ? JT.Lng : JT.None;
                if (k == JT.None) { return JT.None; }
                if (kind != JT.None && kind != k) { return JT.None; }
                kind = k;
            }
            if (kind != JT.Lng) { return kind; }
            foreach (var t in types) {
                if (t != JT.Int) { return JT.Lng; }
            }
            return JT.Int;
        }

        /// <summary>`x.clamp(lo, hi)', Comparable#clamp of an Integer or a Float with bounds of its own kind.</summary>
        private MSA.Expression/*!*/ EmitClamp(MethodCall/*!*/ node, out JT type) {
            var operands = new[] { node.Target, node.Arguments.Expressions[0], node.Arguments.Expressions[1] };
            var values = new MSA.Expression[3];
            var types = new JT[3];
            for (int i = 0; i < 3; i++) {
                values[i] = Emit(operands[i], out types[i]);
                values[i] = NumericIVar(operands[i], values[i], ref types[i]);
            }
            JT kind = CompareKind(types);
            if (kind == JT.None) { throw Bail("clamp of " + types[0] + " to " + types[1] + ", " + types[2]); }
            for (int i = 0; i < 3; i++) {
                values[i] = Coerce(SpeculateOperand(values[i], ref types[i], kind), types[i], kind);
            }
            // the receiver's class answers clamp, and Comparable#clamp compares with <=>
            RequireBuiltin(kind, "clamp");
            RequireBuiltin(kind, "<=>");
            _canDeopt = true;
            type = kind;
            string helper = (kind == JT.Dbl) ? "ClampDouble" : (kind == JT.Int) ? "ClampInt" : "ClampLong";
            return Ast.Call(JitRuntime.M(helper), values[0], values[1], values[2]);
        }

        /// <summary>`[a, b, ...].min' / `.max': the first of the least (greatest) elements, as Array#min would pick it.</summary>
        private MSA.Expression/*!*/ EmitLiteralMinMax(MethodCall/*!*/ node, out JT type) {
            if (!IsPublicBuiltin(ArrayClass, node.MethodName)) { throw Bail("Array#" + node.MethodName + " is not the built-in one"); }
            var items = ((ArrayConstructor)node.Target).Arguments.Expressions;
            var values = new MSA.Expression[items.Length];
            var types = new JT[items.Length];
            for (int i = 0; i < items.Length; i++) {
                values[i] = Emit(items[i], out types[i]);
                values[i] = NumericIVar(items[i], values[i], ref types[i]);
            }
            JT kind = CompareKind(types);
            if (kind == JT.None) { throw Bail("Array#" + node.MethodName + " of mixed or unknown elements"); }
            RequireBuiltin(kind, "<=>");
            bool min = (node.MethodName == "min");
            string helper = (kind == JT.Dbl) ? (min ? "MinDouble" : "MaxDouble") :
                (kind == JT.Int) ? (min ? "MinInt" : "MaxInt") : (min ? "MinLong" : "MaxLong");
            if (kind == JT.Dbl) { _canDeopt = true; }

            // left to right, as the elements are evaluated and as Array#min keeps the first of equals
            MSA.Expression result = Coerce(SpeculateOperand(values[0], ref types[0], kind), types[0], kind);
            for (int i = 1; i < items.Length; i++) {
                var next = Coerce(SpeculateOperand(values[i], ref types[i], kind), types[i], kind);
                result = Ast.Call(JitRuntime.M(helper), result, next);
            }
            type = kind;
            return result;
        }
    }

    public static partial class JitRuntime {
        // ---- the numeric library calls of JitCompiler.Numeric.cs -----------------------------

        /// <summary>The classes `nil?' was last found to be Kernel's on, under which global method version.</summary>
        public sealed class NilQuerySite {
            private sealed class Binding {
                internal readonly RubyClass/*!*/ Class;
                internal readonly int Version;
                internal readonly Binding Next;
                internal Binding(RubyClass/*!*/ cls, int version, Binding next) { Class = cls; Version = version; Next = next; }
            }
            private volatile Binding _bound;

            internal bool Holds(RubyClass/*!*/ cls) {
                int version = RubyModule.GlobalMethodVersion;
                int depth = 0;
                for (var b = _bound; b != null && depth < 4; b = b.Next, depth++) {
                    if (b.Class == cls && b.Version == version) { return true; }
                }
                var resolved = cls.ResolveMethod("nil?", IronRuby.Runtime.Calls.VisibilityContext.AllVisible);
                var info = resolved.Found ? resolved.Info as IronRuby.Runtime.Calls.RubyLibraryMethodInfo : null;
                if (info == null || !ReferenceEquals(info.DeclaringModule, cls.Context.KernelModule) || !info.IsPublic) { return false; }
                // a few classes at a time: a variable that holds nil or an Integer meets two
                var bound = _bound;
                _bound = new Binding(cls, version, (bound != null && bound.Version == version) ? bound : null);
                return true;
            }
        }

        public static bool NilQuery(RubyContext/*!*/ context, object value, NilQuerySite/*!*/ site) {
            var obj = value as RubyObject;
            RubyClass cls = (obj != null) ? obj.ImmediateClass : context.GetImmediateClassOf(value);
            if (!site.Holds(cls)) { throw Deopt(); }
            return value == null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ShiftRightLong(long value, long count) {
            if (count < 0) {
                if (count == Int64.MinValue) { throw Deopt(); }
                return ShiftLeftLong(value, -count);
            }
            return (count >= 64) ? (value < 0 ? -1 : 0) : value >> (int)count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static long ShiftLeftLong(long value, long count) {
            if (count < 0) {
                if (count == Int64.MinValue) { throw Deopt(); }
                return ShiftRightLong(value, -count);
            }
            if (value == 0) { return 0; }
            if (count >= 63) { throw Deopt(); }
            long result = value << (int)count;
            // a bit shifted out, or into the sign: the value is a Bignum
            if ((result >> (int)count) != value) { throw Deopt(); }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static long AbsLong(long value) {
            if (value == Int64.MinValue) { throw Deopt(); }
            return Math.Abs(value);
        }

        // Float#ceil, #floor, #round: an Integer, which a NaN, an infinity (FloatDomainError) or a
        // Bignum is not one we carry.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long CeilToLong(double value) {
            return FloatToLong(Math.Ceiling(value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static long FloorToLong(double value) {
            return FloatToLong(Math.Floor(value));
        }

        /// <summary>Float#round: half away from zero, as C's round(), which MRI's round_half_up is with no digits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long RoundToLong(double value) {
            return FloatToLong(Math.Round(value, MidpointRounding.AwayFromZero));
        }

        // Comparable#clamp: min past max raises, an equal value is the receiver itself.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static int ClampInt(int value, int min, int max) {
            if (min > max) { throw Deopt(); }
            return (value < min) ? min : (value > max) ? max : value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static long ClampLong(long value, long min, long max) {
            if (min > max) { throw Deopt(); }
            return (value < min) ? min : (value > max) ? max : value;
        }

        /// <summary>A NaN anywhere is a comparison that fails, and -0.0 compares equal to 0.0 (the receiver is kept).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ClampDouble(double value, double min, double max) {
            if (!(min <= max) || Double.IsNaN(value)) { throw Deopt(); }
            return (value < min) ? min : (value > max) ? max : value;
        }

        // Array#min / #max of two: the second only when it is strictly less (greater), so that of
        // equal elements the first is the one answered (0.0 and -0.0 included).

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static int MinInt(int a, int b) { return (b < a) ? b : a; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MaxInt(int a, int b) { return (b > a) ? b : a; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long MinLong(long a, long b) { return (b < a) ? b : a; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long MaxLong(long a, long b) { return (b > a) ? b : a; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static double MinDouble(double a, double b) {
            if (Double.IsNaN(a) || Double.IsNaN(b)) { throw Deopt(); }
            return (b < a) ? b : a;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public static double MaxDouble(double a, double b) {
            if (Double.IsNaN(a) || Double.IsNaN(b)) { throw Deopt(); }
            return (b > a) ? b : a;
        }
    }
}
