using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {
    /// <summary>
    /// MRI's rb_range_values: an index that is not a Range but has #begin, #end and #exclude_end?
    /// stands for the Range they describe (Array#[], String#[], #values_at...). The methods are
    /// asked for the way rb_check_funcall asks, private ones included, and an
    /// Enumerator::ArithmeticSequence is refused before they are asked.
    /// </summary>
    public sealed class RangeValuesStorage : RubyCallSiteStorage {
        private CallSite<Func<CallSite, object, object, object, object>> _respondToSite;
        private CallSite<Func<CallSite, object, object>> _beginSite;
        private CallSite<Func<CallSite, object, object>> _endSite;
        private CallSite<Func<CallSite, object, object>> _excludeEndSite;

        public RangeValuesStorage(RubyContext/*!*/ context) : base(context) { }

        /// <summary>
        /// The Range the value describes, or null when it is not range-like (its caller then
        /// converts it to an Integer, as MRI's callers of rb_range_beg_len do).
        /// </summary>
        public Range TryConvert(object value) {
            Range range = value as Range;
            if (range != null) {
                return range;
            }
            if (value == null || value is int || value is double || value is long || value is BigInteger || value is bool ||
                value is MutableString || value is RubySymbol ||
                !RespondTo(value, "begin") || !RespondTo(value, "end") || !RespondTo(value, "exclude_end?") ||
                IsArithmeticSequence(value)) {
                return null;
            }

            var beginSite = RubyUtils.GetCallSite(ref _beginSite, Context, "begin", RubyCallSignature.WithImplicitSelf(0));
            var endSite = RubyUtils.GetCallSite(ref _endSite, Context, "end", RubyCallSignature.WithImplicitSelf(0));
            var excludeEndSite = RubyUtils.GetCallSite(ref _excludeEndSite, Context, "exclude_end?", RubyCallSignature.WithImplicitSelf(0));
            object begin = beginSite.Target(beginSite, value);
            object end = endSite.Target(endSite, value);
            return Range.CreateValidated(begin, end, RubyOps.IsTrue(excludeEndSite.Target(excludeEndSite, value)));
        }

        private bool RespondTo(object value, string/*!*/ name) {
            var site = RubyUtils.GetCallSite(ref _respondToSite, Context, "respond_to?", RubyCallSignature.WithImplicitSelf(2));
            return RubyOps.IsTrue(site.Target(site, value, Context.EncodeIdentifier(name), true));
        }

        private bool IsArithmeticSequence(object value) {
            if (!(value is Enumerator)) {
                return false;
            }
            for (RubyClass cls = Context.GetClassOf(value); cls != null; cls = cls.SuperClass) {
                if (cls.Name == "Enumerator::ArithmeticSequence") {
                    return true;
                }
            }
            return false;
        }
    }
}
