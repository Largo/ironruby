/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System.Runtime.CompilerServices;
using System.Threading;
using IronRuby.Builtins;

namespace IronRuby.Runtime {
    /// <summary>
    /// The objects the literals of one eval of a text make - a regexp literal's Regexp, an Integer range literal's Range -
    /// which CRuby makes anew for every eval of the same text and once for all the passes and block calls of one eval. The
    /// compiled code of such a text is shared by every eval of it (EvalCodeCache, IR_EVAL_LITERALS), so the objects cannot
    /// be constants of that code: the eval's top level makes one of these at entry (RubyOps.CreateEvalLiterals) and every
    /// Proc its code instantiates carries it (Proc.EvalLiterals), so that a block of the text, compiled apart and seeing
    /// only its scope - the binding's own, shared by every eval through it - still finds its eval's objects. A slot is
    /// filled once, by whichever thread gets there first.
    /// </summary>
    public sealed class EvalLiterals {
        private readonly object[]/*!*/ _slots;

        internal EvalLiterals(int count) {
            _slots = new object[count];
        }

        internal StrongBox<RubyRegex>/*!*/ GetRegexBox(int index) {
            var box = Volatile.Read(ref _slots[index]) as StrongBox<RubyRegex>;
            if (box != null) {
                return box;
            }
            var created = new StrongBox<RubyRegex>(null);
            return Interlocked.CompareExchange(ref _slots[index], created, null) as StrongBox<RubyRegex> ?? created;
        }

        internal Range/*!*/ GetIntegerRange(int index, int begin, int end, bool exclusive) {
            var range = Volatile.Read(ref _slots[index]) as Range;
            if (range != null) {
                return range;
            }
            var created = new Range(begin, end, exclusive);
            return Interlocked.CompareExchange(ref _slots[index], created, null) as Range ?? created;
        }
    }
}
