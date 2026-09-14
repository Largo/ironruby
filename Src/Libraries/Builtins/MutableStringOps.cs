/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {
    using BinaryOpStorageWithScope = CallSiteStorage<Func<CallSite, RubyScope, object, object, object>>;

    [RubyClass("String", Extends = typeof(MutableString), Inherits = typeof(Object))]
    [Includes(typeof(Comparable))]
    public class MutableStringOps {

        [RubyConstructor]
        public static MutableString/*!*/ Create(RubyClass/*!*/ self) {
            return MutableString.CreateEmpty();
        }
        
        [RubyConstructor]
        public static MutableString/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ value) {
            return MutableString.Create(value);
        }

        // Ruby 2.x: String.new(str, encoding: enc, capacity: n). The capacity is only a hint
        // about how much room to reserve, so it is accepted and ignored.
        [RubyConstructor]
        public static MutableString/*!*/ Create(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, RubyClass/*!*/ self,
            [DefaultProtocol, Optional, NotNull]MutableString value, [NotNull]IDictionary<object, object>/*!*/ options) {

            MutableString result = (value != null) ? MutableString.Create(value) : MutableString.CreateEmpty();
            foreach (var entry in options) {
                var name = (entry.Key as RubySymbol)?.ToString();
                if (name == "encoding") {
                    if (entry.Value != null) {
                        result.ForceEncoding(Protocols.ConvertToEncoding(toStr, entry.Value));
                    }
                } else if (name != "capacity") {
                    throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", context.Inspect(entry.Key).ToString());
                }
            }
            return result;
        }

        [RubyConstructor]
        public static MutableString/*!*/ Create(RubyClass/*!*/ self, [NotNull]byte[]/*!*/ value) {
            return MutableString.CreateBinary(value);
        }

        #region Helpers

        internal static bool InExclusiveRangeNormalized(int length, ref int index) {
            if (index < 0) {
                index = index + length;
            }
            return index >= 0 && index < length;
        }

        private static bool InInclusiveRangeNormalized(MutableString/*!*/ str, ref int index) {
            if (index < 0) {
                index = index + str.Length;
            }
            return index >= 0 && index <= str.Length;
        }

        internal static bool NormalizeSubstringRange(ConversionStorage<int>/*!*/ fixnumCast, Range/*!*/ range, int length, out int begin, out int count) {
            // Ruby 2.6/2.7 beginless and endless ranges: a missing bound is the
            // start resp. the end of the string, and an open end is never exclusive.
            begin = (range.Begin == null) ? 0 : Protocols.CastToFixnum(fixnumCast, range.Begin);

            begin = IListOps.NormalizeIndex(length, begin);
            if (begin < 0 || begin > length) {
                count = 0;
                return false;
            }

            if (range.End == null) {
                count = length - begin;
                return true;
            }

            int end = IListOps.NormalizeIndex(length, Protocols.CastToFixnum(fixnumCast, range.End));

            count = range.ExcludeEnd ? end - begin : end - begin + 1;
            return true;
        }

        internal static bool NormalizeSubstringRange(int length, ref int start, ref int count) {
            if (start < 0) {
                start += length;
            }

            if (start < 0 || start >= length || count < 0) {
                return false;
            }

            if (start + count > length) {
                count = length - start;
            }

            return true;
        }

        internal static int NormalizeInsertIndex(int index, int length) {
            int result = index < 0 ? index + length + 1 : index;
            if (result > length || result < 0) {
                throw RubyExceptions.CreateIndexError("index {0} out of string", index);
            }
            return result;
        }
        
        // Parses interval strings that are of this form:
        //
        // abc         # abc
        // abc-efg-h   # abcdefgh
        // ^abc        # all characters in range 0-255 except abc
        // \x00-0xDD   # all characters in hex range
        public class IntervalParser {
            private readonly MutableString/*!*/ _range;
            private int _pos;
            private bool _rangeStarted;
            private int _startRange;

            public IntervalParser(MutableString/*!*/ range) {
                _range = range;
                _pos = 0;
                _rangeStarted = false;
            }

            public int PeekChar() {
                return _pos >= _range.Length ? -1 : _range.GetChar(_pos);
            }

            public int GetChar() {
                return _pos >= _range.Length ? -1 : _range.GetChar(_pos++);
            }

            public int NextToken() {
                int current = GetChar();

                if (current == '\\') {
                    int next = PeekChar();
                    switch (next) {
                        case 'x':
                            _pos++;
                            int digit1 = Tokenizer.ToDigit(GetChar());
                            int digit2 = Tokenizer.ToDigit(GetChar());

                            if (digit1 >= 16) {
                                throw RubyExceptions.CreateArgumentError("Invalid escape character syntax");
                            }

                            if (digit2 >= 16) {
                                current = digit1;
                            } else {
                                current = ((digit1 << 4) + digit2);
                            }
                            break;

                        case 't':
                            current = '\t';
                            break;

                        case 'n':
                            current = '\n';
                            break;

                        case 'r':
                            current = '\r';
                            break;

                        case 'v':
                            current = '\v';
                            break;

                        case '\\':
                            current = '\\';
                            break;

                        default:
                            break;
                    }
                }

                return current;
            }

            // TODO: refactor this and Parse()
            public MutableString/*!*/ ParseSequence() {
                _pos = 0;

                MutableString result = MutableString.CreateBinary();
                if (_range.Length == 0) {
                    return result;
                }

                bool negate = false;
                if (_range.StartsWith('^')) {
                    // Special case of ^
                    if (_range.GetLength() == 1) {
                        result.Append('^');
                        return result;
                    }

                    negate = true;
                    _pos = 1;
                }

                BitArray array = new BitArray(256);
                array.Not();

                int c;
                while ((c = NextToken()) != -1) {
                    if (_rangeStarted) {
                        // _startRange - c. ignore ranges which are the reverse sequence
                        if (_startRange <= c) {
                            for (int i = _startRange; i <= c; ++i) {
                                if (negate) {
                                    array.Set(i, false);
                                } else {
                                    result.Append((byte)i);
                                }
                            }
                        }
                        _rangeStarted = false;
                    } else {
                        int p = PeekChar();
                        if (p == '-') {
                            // z- is treated as a literal 'z', '-'
                            if (_pos == _range.Length - 1) {
                                if (negate) {
                                    array.Set(c, false);
                                    array.Set('-', false);
                                } else {
                                    result.Append((byte)c);
                                    result.Append('-');
                                }
                                break;
                            }

                            _startRange = c;
                            if (_rangeStarted) {
                                if (negate) {
                                    array.Set('-', false);
                                } else {
                                    result.Append('-');
                                }
                                _rangeStarted = false;
                            } else {
                                _rangeStarted = true;
                            }
                            _pos++; // consume -
                        } else {
                            if (negate) {
                                array.Set(c, false);
                            } else {
                                result.Append((byte)c);
                            }
                        }
                    }
                }

                if (negate) {
                    for (int i = 0; i < 256; i++) {
                        if (array.Get(i)) {
                            result.Append((byte)i);
                        }
                    }
                }
                return result;
            }

            public BitArray/*!*/ Parse() {
                _pos = 0;

                BitArray result = new BitArray(256);
                if (_range.Length == 0) {
                    return result;
                }

                bool negate = false;
                if (_range.StartsWith('^')) {
                    // Special case of ^
                    if (_range.GetLength() == 1) {
                        result.Set('^', true);
                        return result;
                    }

                    negate = true;
                    _pos = 1;
                    result.Not();
                }

                int c;
                while ((c = NextToken()) != -1) {
                    if (_rangeStarted) {
                        // _startRange - c. ignore ranges which are the reverse sequence
                        if (_startRange <= c) {
                            for (int i = _startRange; i <= c; ++i)
                                result.Set(i, !negate);
                        }
                        _rangeStarted = false;
                    } else {
                        int p = PeekChar();
                        if (p == '-') {
                            // z- is treated as a literal 'z', '-'
                            if (_pos == _range.Length - 1) {
                                result.Set(c, !negate);
                                result.Set('-', !negate);
                                break;
                            }

                            _startRange = c;
                            if (_rangeStarted) {
                                result.Set('-', !negate);
                                _rangeStarted = false;
                            } else {
                                _rangeStarted = true;
                            }
                            _pos++; // consume -
                        } else {
                            result.Set(c, !negate);
                        }
                    }
                }

                return result;
            }
        }

        public class RangeParser {

            private readonly MutableString[]/*!*/ _ranges;

            public RangeParser(params MutableString[]/*!*/ ranges) {
                ContractUtils.RequiresNotNull(ranges, "ranges");
                _ranges = ranges;
            }

            public BitArray Parse() {
                BitArray result = new IntervalParser(_ranges[0]).Parse();
                for (int i = 1; i < _ranges.Length; i++) {
                    result.And(new IntervalParser(_ranges[i]).Parse());
                }
                return result;
            }
        }

        /// <summary>
        /// The set of characters a tr-style selector ("a-z", "^abc", "a\\-b") describes.
        ///
        /// This replaces IntervalParser.Parse's 256-entry BitArray for #count, #delete and
        /// #squeeze: a selector may name any character, not just a Latin-1 one, and an
        /// unrepresentable one used to index past the end of the bit array and surface as a
        /// CLR IndexOutOfRangeException. The parse follows MRI's trnext: a backslash escapes
        /// the next character unless it is the last one, a trailing '-' is a literal, and a
        /// descending range is an error rather than an empty set.
        /// </summary>
        public sealed class CharacterSelector {
            private readonly bool _negated;
            private readonly HashSet<int>/*!*/ _singles = new HashSet<int>();
            private readonly List<int>/*!*/ _rangeBounds = new List<int>();

            private CharacterSelector(bool negated) {
                _negated = negated;
            }

            public bool Contains(int c) {
                bool hit = _singles.Contains(c);
                if (!hit) {
                    for (int i = 0; i < _rangeBounds.Count; i += 2) {
                        if (c >= _rangeBounds[i] && c <= _rangeBounds[i + 1]) {
                            hit = true;
                            break;
                        }
                    }
                }
                return _negated ? !hit : hit;
            }

            private static int CodepointAt(MutableString/*!*/ str, ref int position) {
                char c = str.GetChar(position++);
                if (Char.IsHighSurrogate(c) && position < str.Length) {
                    char low = str.GetChar(position);
                    if (Char.IsLowSurrogate(low)) {
                        position++;
                        return Char.ConvertToUtf32(c, low);
                    }
                }
                return c;
            }

            public static CharacterSelector/*!*/ Parse(MutableString/*!*/ selector) {
                selector.PrepareForCharacterRead();
                int length = selector.Length;
                int position = 0;

                // A lone "^" selects the circumflex itself.
                bool negated = length > 1 && selector.GetChar(0) == '^';
                if (negated) {
                    position = 1;
                }

                var result = new CharacterSelector(negated);
                while (position < length) {
                    if (selector.GetChar(position) == '\\' && position < length - 1) {
                        position++;
                    }
                    int first = CodepointAt(selector, ref position);

                    // "a-z" is a range only when something follows the dash.
                    if (position < length - 1 && selector.GetChar(position) == '-') {
                        position++;
                        int last = CodepointAt(selector, ref position);
                        if (first > last) {
                            if (first < 0x80 && last < 0x80) {
                                throw RubyExceptions.CreateArgumentError(
                                    String.Format("invalid range \"{0}-{1}\" in string transliteration", (char)first, (char)last)
                                );
                            }
                            throw RubyExceptions.CreateArgumentError("invalid range in string transliteration");
                        }
                        result._rangeBounds.Add(first);
                        result._rangeBounds.Add(last);
                    } else {
                        result._singles.Add(first);
                    }
                }

                return result;
            }
        }

        /// <summary>A character is selected when every one of the selectors picks it.</summary>
        public sealed class CharacterSelectorSet {
            private readonly CharacterSelector[]/*!*/ _selectors;

            private CharacterSelectorSet(CharacterSelector[]/*!*/ selectors) {
                _selectors = selectors;
            }

            public bool Contains(int c) {
                for (int i = 0; i < _selectors.Length; i++) {
                    if (!_selectors[i].Contains(c)) {
                        return false;
                    }
                }
                return true;
            }

            public static CharacterSelectorSet/*!*/ Parse(MutableString/*!*/ self, MutableString[]/*!*/ selectors) {
                var parsed = new CharacterSelector[selectors.Length];
                for (int i = 0; i < selectors.Length; i++) {
                    self.RequireCompatibleEncoding(selectors[i]);
                    RequireValidEncoding(selectors[i]);
                    parsed[i] = CharacterSelector.Parse(selectors[i]);
                }
                return new CharacterSelectorSet(parsed);
            }
        }

        #endregion


        #region initialize, initialize_copy

        // Reinitialization. Not called when a factory/non-default ctor is called.
        // Nothing is changed, so MRI does not object to a frozen receiver here.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ Reinitialize(MutableString/*!*/ self) {
            return self;
        }

        // "initialize" not called when a factory/non-default ctor is called.
        // "initialize_copy" called from "dup" and "clone"
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ Reinitialize(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString other) {
            self.RequireNotFrozen();
            if (ReferenceEquals(self, other)) {
                return self;
            }

            self.Clear();
            self.Append(other);
            // The encoding comes across too: "".send(:initialize, utf16) is a UTF-16 string.
            self.ForceEncoding(other.Encoding);
            return self.TaintBy(other);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ Reinitialize(MutableString/*!*/ self, [NotNull]byte[] other) {
            self.Clear();
            self.Append(other);
            return self;
        }

        #endregion


        #region %, *, +

        [RubyMethod("%")]
        public static MutableString/*!*/ Format(StringFormatterSiteStorage/*!*/ storage, MutableString/*!*/ self, [NotNull]IList/*!*/ args) {
            StringFormatter formatter = new StringFormatter(storage, self.ConvertToString(), self.Encoding, args);
            return formatter.Format().TaintBy(self);
        }

        [RubyMethod("%")]
        public static MutableString/*!*/ Format(StringFormatterSiteStorage/*!*/ storage, ConversionStorage<IList>/*!*/ arrayTryCast,
            MutableString/*!*/ self, object args) {
            return Format(storage, self, Protocols.TryCastToArray(arrayTryCast, args) ?? new[] { args });
        }

        // encoding aware
        [RubyMethod("*")]
        public static MutableString/*!*/ Repeat(MutableString/*!*/ self, [DefaultProtocol]int times) {
            if (times < 0) {
                throw RubyExceptions.CreateArgumentError("negative argument");
            }

            // Ruby 3.0 dropped subclass preservation here: "MyString.new("x") * 2".class is String.
            var result = MutableString.CreateMutable(self.Encoding).TaintBy(self);
            if (self.IsEmpty) {
                // AppendMultiple would spin `times` times appending nothing: "" * 2_000_000_000 hung.
                return result;
            }
            if ((long)self.GetByteCount() * times > Int32.MaxValue) {
                throw RubyExceptions.CreateArgumentError("argument too big");
            }
            return result.AppendMultiple(self, times);
        }

        /// <summary>
        /// A count that does not fit in a Fixnum. MRI's limit is a C long, not an int, so
        /// "" * (2 ** 63 - 1) is "" rather than a RangeError, and only a genuinely out-of-range
        /// count (past a C long) is a RangeError.
        /// </summary>
        [RubyMethod("*")]
        public static MutableString/*!*/ Repeat(MutableString/*!*/ self, [NotNull]BigInteger/*!*/ times) {
            if (times.Sign < 0) {
                throw RubyExceptions.CreateArgumentError("negative argument");
            }
            if (times > Int64.MaxValue) {
                throw RubyExceptions.CreateRangeError("bignum too big to convert into `long'");
            }

            // Ruby 3.0 dropped subclass preservation here: "MyString.new("x") * 2".class is String.
            var result = MutableString.CreateMutable(self.Encoding).TaintBy(self);
            if (self.IsEmpty) {
                return result;
            }
            throw RubyExceptions.CreateArgumentError("argument too big");
        }

        // encoding aware
        [RubyMethod("+")]
        public static MutableString/*!*/ Concatenate(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ other) {
            // doesn't create a subclass:
            return self.Concat(other).TaintBy(self).TaintBy(other);
        }

        // encoding aware
        [RubyMethod("+")]
        public static MutableString/*!*/ Concatenate(MutableString/*!*/ self, [NotNull]RubySymbol/*!*/ other) {
            // doesn't create a subclass:
            return self.Concat(other.String).TaintBy(self).TaintBy(other);
        }

        #endregion

        #region <<, concat

        // encoding aware
        [RubyMethod("<<")]
        [RubyMethod("concat")]
        public static MutableString/*!*/ Append(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ other) {
            return self.Append(other).TaintBy(other);
        }

        [RubyMethod("<<")]
        [RubyMethod("concat")]
        public static MutableString/*!*/ Append(MutableString/*!*/ self, int c) {
            if (c < 0) {
                throw RubyExceptions.CreateRangeError("{0} out of char range", c);
            }

            // #5855: appending a 0x80-0xff code point to a US-ASCII string widens the receiver
            // to BINARY instead of failing. Integer#chr is stricter - 0x80.chr("US-ASCII") is a
            // RangeError - so this case cannot go through ToChr.
            if (c >= 0x80 && c <= 0xff && self.Encoding == RubyEncoding.Ascii) {
                self.ForceEncoding(RubyEncoding.Binary);
                return self.Append((byte)c);
            }
            return self.Append(Integer.ToChr(self.Encoding, self.Encoding, c));
        }

        [RubyMethod("<<")]
        [RubyMethod("concat")]
        public static MutableString/*!*/ Append(MutableString/*!*/ self, [NotNull]BigInteger/*!*/ c) {
            throw RubyExceptions.CreateRangeError("bignum out of char range");
        }

        // Ruby 1.9 gave #concat - but not #<< - any number of arguments. Each one is an
        // Integer codepoint or something String-convertible, and they are appended in order,
        // so "s.concat(s, s)" triples s rather than looping.
        [RubyMethod("concat")]
        public static MutableString/*!*/ Append(ConversionStorage<MutableString>/*!*/ stringCast, MutableString/*!*/ self,
            [NotNull]params object/*!*/[]/*!*/ others) {
            self.RequireNotFrozen();

            var pieces = new object[others.Length];
            for (int i = 0; i < others.Length; i++) {
                if (others[i] is int || others[i] is BigInteger) {
                    pieces[i] = others[i];
                } else {
                    // A copy, so that "s.concat(s, s)" sees the original both times.
                    pieces[i] = Protocols.CastToString(stringCast, others[i]).Clone();
                }
            }

            for (int i = 0; i < pieces.Length; i++) {
                if (pieces[i] is int) {
                    Append(self, (int)pieces[i]);
                } else if (pieces[i] is BigInteger) {
                    Append(self, (BigInteger)pieces[i]);
                } else {
                    Append(self, (MutableString)pieces[i]);
                }
            }
            return self;
        }

        #endregion

        #region <=>, ==, ===

        [RubyMethod("<=>")]
        public static int Compare(MutableString/*!*/ self, [NotNull]MutableString/*!*/ other) {
            return Math.Sign(self.CompareTo(other));
        }

        [RubyMethod("<=>")]
        public static int Compare(MutableString/*!*/ self, [NotNull]string/*!*/ other) {
            return Math.Sign(self.CompareTo(other));
        }

        /// <summary>Pairs whose inverse comparison is in progress; see Compare below.</summary>
        [ThreadStatic]
        private static List<object> _inverseComparisons;

        [RubyMethod("<=>")]
        public static object Compare(ConversionStorage<MutableString>/*!*/ stringTryCast, BinaryOpStorage/*!*/ comparisonStorage,
            RespondToStorage/*!*/ respondToStorage, MutableString/*!*/ self, object other) {
            // MRI converts with #to_str and compares the result; only when that is not
            // available does it ask the argument to compare itself and negate the answer.
            MutableString converted = (other is RubySymbol) ? null : Protocols.TryCastToString(stringTryCast, other);
            if (converted != null) {
                return ScriptingRuntimeHelpers.Int32ToObject(Compare(self, converted));
            }
            return Compare(comparisonStorage, respondToStorage, (object)self, other);
        }

        /// <summary>
        /// The "ask the other object and negate" half, which is all the CLR String and ClrName
        /// wrappers need.
        /// </summary>
        public static object Compare(BinaryOpStorage/*!*/ comparisonStorage, RespondToStorage/*!*/ respondToStorage,
            object/*!*/ self, object other) {
            if (!Protocols.RespondTo(respondToStorage, other, "<=>")) {
                return null;
            }

            // "other <=> self" may well be defined as "self <=> other", so the pair being
            // compared is remembered and a second, identical question answers nil - which is
            // what MRI's rb_invcmp recursion guard does.
            var pending = _inverseComparisons ?? (_inverseComparisons = new List<object>());
            for (int i = 0; i + 1 < pending.Count; i += 2) {
                if (ReferenceEquals(pending[i], self) && ReferenceEquals(pending[i + 1], other)) {
                    return null;
                }
            }

            pending.Add(self);
            pending.Add(other);
            try {
                var site = comparisonStorage.GetCallSite("<=>");
                object answer = site.Target(site, other, self);
                return (answer == null) ? null : Integer.TryUnaryMinus(answer);
            } finally {
                pending.RemoveRange(pending.Count - 2, 2);
            }
        }

        [RubyMethod("eql?")]
        public static bool Eql(MutableString/*!*/ lhs, [NotNull]MutableString/*!*/ rhs) {
            return lhs.Equals(rhs);
        }

        [RubyMethod("eql?")]
        public static bool Eql(MutableString/*!*/ lhs, [NotNull]string/*!*/ rhs) {
            return lhs.Equals(rhs);
        }

        [RubyMethod("eql?")]
        public static bool Eql(MutableString/*!*/ lhs, object rhs) {
            return false;
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool StringEquals(MutableString/*!*/ lhs, [NotNull]MutableString/*!*/ rhs) {
            return lhs.Equals(rhs);
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool StringEquals(MutableString/*!*/ lhs, [NotNull]string/*!*/ rhs) {
            return lhs.Equals(rhs);
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equals(RespondToStorage/*!*/ respondToStorage, BinaryOpStorage/*!*/ equalsStorage,
            object/*!*/ self, object other) {
            // Self is object so that we can reuse this method.

            if (!Protocols.RespondTo(respondToStorage, other, "to_str")) {
                return false;
            }

            var equals = equalsStorage.GetCallSite("==");
            return Protocols.IsTrue(equals.Target(equals, other, self));
        }

        #endregion


        #region slice!

        [RubyMethod("slice!")]
        public static object RemoveCharInPlace(RubyContext/*!*/ context, MutableString/*!*/ self, [DefaultProtocol]int index) {
            // The frozen check happens before the index is bounds checked: MRI raises
            // FrozenError even when the call would be a no-op.
            self.RequireNotFrozen();

            // Ruby 1.9 returns the character at the index, not its first byte.
            if (!InExclusiveRangeNormalized(self.GetCharCount(), ref index)) {
                return null;
            }

            MutableString result = self.GetSlice(index, 1);
            self.Remove(index, 1);
            return result;
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(MutableString/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol]int length) {
            self.RequireNotFrozen();
            if (length < 0) {
                return null;
            }

            if (!InInclusiveRangeNormalized(self, ref start)) {
                return null;
            }

            if (start + length > self.Length) {
                length = self.Length - start;
            }

            MutableString result = self.CreateDerived().Append(self, start, length).TaintBy(self);
            self.Remove(start, length);
            return result;
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(ConversionStorage<int>/*!*/ fixnumCast, 
            MutableString/*!*/ self, [NotNull]Range/*!*/ range) {
            self.RequireNotFrozen();
            int begin = Protocols.CastToFixnum(fixnumCast, range.Begin);
            int end = Protocols.CastToFixnum(fixnumCast, range.End);

            if (!InInclusiveRangeNormalized(self, ref begin)) {
                return null;
            }

            end = IListOps.NormalizeIndex(self.Length, end);

            int count = range.ExcludeEnd ? end - begin : end - begin + 1;
            return count < 0 ? self.CreateDerived() : RemoveSubstringInPlace(self, begin, count);
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(RubyScope/*!*/ scope, MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            self.RequireNotFrozen();
            if (regex.IsEmpty) {
                return self.CloneDerived().TaintBy(regex, scope);
            }

            MatchData match = RegexpOps.Match(scope, regex, self);
            if (match == null) {
                return null;
            }

            return RemoveSubstringInPlace(self, match.Index, match.Length).TaintBy(regex, scope);
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol]int occurrance) {

            self.RequireNotFrozen();
            if (regex.IsEmpty) {
                return self.CloneDerived().TaintBy(regex, scope);
            }

            MatchData match = RegexpOps.Match(scope, regex, self);
            if (match == null || !RegexpOps.NormalizeGroupIndex(ref occurrance, match.GroupCount)) {
                return null;
            }

            return match.GroupSuccess(occurrance) ?
                RemoveSubstringInPlace(self, match.GetGroupStart(occurrance), match.GetGroupLength(occurrance)).TaintBy(regex, scope) : null;
        }

        [RubyMethod("slice!")]
        public static MutableString RemoveSubstringInPlace(MutableString/*!*/ self, [NotNull]MutableString/*!*/ searchStr) {
            self.RequireNotFrozen();
            if (searchStr.IsEmpty) {
                return searchStr.CloneDerived();
            }

            int index = self.IndexOf(searchStr);
            if (index < 0) {
                return null;
            }

            RemoveSubstringInPlace(self, index, searchStr.Length);
            return searchStr.CloneDerived();
        }

        #endregion

        #region [], slice, getbyte, setbyte, ord

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetChar(MutableString/*!*/ self, [DefaultProtocol]int index) {
            return InExclusiveRangeNormalized(self.GetCharCount(), ref index) ? self.GetSlice(index, 1) : null;
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(MutableString/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol]int count) {
            int charCount = self.GetCharCount();
            if (!NormalizeSubstringRange(charCount, ref start, ref count)) {
                return (start == charCount) ? self.CreateDerived().TaintBy(self) : null;
            }

            return self.CreateDerived().Append(self, start, count).TaintBy(self);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(ConversionStorage<int>/*!*/ fixnumCast, MutableString/*!*/ self, [NotNull]Range/*!*/ range) {
            int begin, count;
            if (!NormalizeSubstringRange(fixnumCast, range, self.GetCharCount(), out begin, out count)) {
                return null;
            }
            return (count < 0) ? self.CreateDerived().TaintBy(self) : GetSubstring(self, begin, count);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(MutableString/*!*/ self, [NotNull]MutableString/*!*/ searchStr) {
            return (self.IndexOf(searchStr) != -1) ? searchStr.CloneDerived() : null;
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubyScope/*!*/ scope, MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            if (regex.IsEmpty) {
                return self.CreateDerived().TaintBy(self).TaintBy(regex, scope);
            }

            MatchData match = RegexpOps.Match(scope, regex, self);
            if (match == null) {
                return null;
            }

            return self.CreateDerived().TaintBy(self).Append(self, match.Index, match.Length).TaintBy(regex, scope);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol]int occurrance) {
            if (regex.IsEmpty) {
                return self.CreateDerived().TaintBy(self).TaintBy(regex, scope);
            }

            MatchData match = RegexpOps.Match(scope, regex, self);
            if (match == null || !RegexpOps.NormalizeGroupIndex(ref occurrance, match.GroupCount)) {
                return null;
            }

            MutableString result = match.AppendGroupValue(occurrance, self.CreateDerived());
            return result != null ? result.TaintBy(regex, scope) : null;
        }

        // MRI also addresses a capture by its name, as a String or a Symbol.
        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubyScope/*!*/ scope, MutableString/*!*/ self,
            [NotNull]RubyRegex/*!*/ regex, [NotNull]MutableString/*!*/ groupName) {
            return GetNamedGroupSubstring(scope, self, regex, groupName.ConvertToString());
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubyScope/*!*/ scope, MutableString/*!*/ self,
            [NotNull]RubyRegex/*!*/ regex, [NotNull]RubySymbol/*!*/ groupName) {
            return GetNamedGroupSubstring(scope, self, regex, groupName.ToString());
        }

        private static MutableString GetNamedGroupSubstring(RubyScope/*!*/ scope, MutableString/*!*/ self,
            RubyRegex/*!*/ regex, string/*!*/ groupName) {
            MatchData match = RegexpOps.Match(scope, regex, self);
            if (match == null) {
                // MRI only reports an unknown name once something matched.
                return null;
            }

            if (!match.HasNamedGroup(groupName)) {
                throw RubyExceptions.CreateIndexError("undefined group name reference: {0}", groupName);
            }

            return match.GetNamedGroupValue(groupName);
        }

        [RubyMethod("getbyte")]
        public static object GetByte(MutableString/*!*/ self, [DefaultProtocol]int index) {
            return InExclusiveRangeNormalized(self.GetByteCount(), ref index) ? ScriptingRuntimeHelpers.Int32ToObject(self.GetByte(index)) : null;
        }

        /// <summary>
        /// Returns the first codepoint.
        /// </summary>
        [RubyMethod("ord")]
        public static int Ord(MutableString/*!*/ str) {
            if (str.IsEmpty) {
                throw RubyExceptions.CreateArgumentError("empty string");
            }
            char c1 = str.GetChar(0);
            if (!Char.IsSurrogate(c1)) {
                return (int)c1;
            }

            char c2;
            if (Tokenizer.IsHighSurrogate(c1) && str.GetCharCount() > 1 && Tokenizer.IsLowSurrogate(c2 = str.GetChar(1))) {
                return Tokenizer.ToCodePoint(c1, c2);
            }
            throw RubyExceptions.CreateArgumentError("invalid byte sequence in {0}", str.Encoding);
        }

        #endregion

        #region []=

        [RubyMethod("setbyte")]
        public static object SetByte(MutableString/*!*/ self, [DefaultProtocol]int index, [DefaultProtocol]int value) {
            self.RequireNotFrozen();
            int count = self.GetByteCount();
            int at = index < 0 ? index + count : index;
            if (at < 0 || at >= count) {
                throw RubyExceptions.CreateIndexError("index {0} out of string", index);
            }
            self.SetByte(at, unchecked((byte)value));
            // MRI answers the value that was written, not the receiver.
            return ScriptingRuntimeHelpers.Int32ToObject(value);
        }

        [RubyMethod("[]=")]
        public static MutableString/*!*/ ReplaceCharacter(MutableString/*!*/ self,
            [DefaultProtocol]int index, [DefaultProtocol, NotNull]MutableString/*!*/ value) {

            index = index < 0 ? index + self.Length : index;
            // Appending at the very end is allowed, which is the only way "" can be assigned to.
            if (index < 0 || index > self.Length) {
                throw RubyExceptions.CreateIndexError("index {0} out of string", index);
            }

            if (index == self.Length) {
                self.Append(value).TaintBy(value);
                return value;
            }

            if (value.IsEmpty) {
                self.Remove(index, 1).TaintBy(value);
                return MutableString.CreateEmpty();
            }

            self.Replace(index, 1, value).TaintBy(value);
            return value;
        }

        [RubyMethod("[]=")]
        public static MutableString/*!*/ ReplaceSubstring(MutableString/*!*/ self, 
            [DefaultProtocol]int start, [DefaultProtocol]int charsToOverwrite, [DefaultProtocol, NotNull]MutableString/*!*/ value) {
            
            if (charsToOverwrite < 0) {
                throw RubyExceptions.CreateIndexError("negative length {0}", charsToOverwrite);
            }

            if (System.Math.Abs(start) > self.Length) {
                throw RubyExceptions.CreateIndexError("index {0} out of string", start);
            }

            start = start < 0 ? start + self.Length : start;

            if (charsToOverwrite <= value.Length) {
                int insertIndex = start + charsToOverwrite;
                int limit = charsToOverwrite;
                if (insertIndex > self.Length) {
                    limit -= insertIndex - self.Length;
                    insertIndex = self.Length;
                }

                self.Replace(start, limit, value);
            } else {
                self.Replace(start, value.Length, value);

                int pos = start + value.Length;
                int charsToRemove = charsToOverwrite - value.Length;
                int charsLeftInString = self.Length - pos;

                self.Remove(pos, System.Math.Min(charsToRemove, charsLeftInString));
            }

            self.TaintBy(value);
            return value;
        }

        [RubyMethod("[]=")]
        public static MutableString/*!*/ ReplaceSubstring(ConversionStorage<int>/*!*/ fixnumCast, MutableString/*!*/ self, 
            [NotNull]Range/*!*/ range, [DefaultProtocol, NotNull]MutableString/*!*/ value) {

            int begin = (range.Begin == null) ? 0 : Protocols.CastToFixnum(fixnumCast, range.Begin);
            int end = (range.End == null) ? self.Length : Protocols.CastToFixnum(fixnumCast, range.End);

            int normalizedBegin = begin < 0 ? begin + self.Length : begin;

            if (normalizedBegin < 0 || normalizedBegin > self.Length) {
                // MRI quotes the range as it was written, not as it was normalized.
                throw RubyExceptions.CreateRangeError("{0}..{1} out of range", begin, end);
            }

            end = end < 0 ? end + self.Length : end;

            int count = range.ExcludeEnd ? end - normalizedBegin : end - normalizedBegin + 1;
            // An end before the beginning is an insertion, not a negative-length error.
            return ReplaceSubstring(self, normalizedBegin, count < 0 ? 0 : count, value);
        }

        [RubyMethod("[]=")]
        public static MutableString ReplaceSubstring(MutableString/*!*/ self,
            [NotNull]MutableString/*!*/ substring, [DefaultProtocol, NotNull]MutableString/*!*/ value) {

            int index = self.IndexOf(substring);
            if (index == -1) {
                throw RubyExceptions.CreateIndexError("string not matched");
            }

            return ReplaceSubstring(self, index, substring.Length, value);
        }

        [RubyMethod("[]=")]
        public static MutableString ReplaceSubstring(ConversionStorage<MutableString>/*!*/ stringCast, RubyContext/*!*/ context,
            MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex, [NotNull]object/*!*/ value) {
            return ReplaceSubstring(stringCast, context, self, regex, 0, value);
        }

        // A String replacement gets its own three-argument overload so that the binder still
        // has a better match than "[]=(Fixnum, Fixnum, String)" for "str[/re/, n] = s".
        [RubyMethod("[]=")]
        public static MutableString ReplaceSubstring(ConversionStorage<MutableString>/*!*/ stringCast, RubyContext/*!*/ context,
            MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol]int groupIndex,
            [NotNull]MutableString/*!*/ value) {
            return ReplaceSubstring(stringCast, context, self, regex, groupIndex, (object)value);
        }

        [RubyMethod("[]=")]
        public static MutableString ReplaceSubstring(ConversionStorage<MutableString>/*!*/ stringCast, RubyContext/*!*/ context,
            MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol]int groupIndex, [NotNull]object/*!*/ value) {

            // MRI checks the match, and the capture index, before it asks the replacement for
            // #to_str - so the conversion is done by hand rather than by the binder.
            MatchData match = regex.Match(self);
            if (match == null) {
                throw RubyExceptions.CreateIndexError("regexp not matched");
            }

            if (groupIndex <= -match.GroupCount || groupIndex >= match.GroupCount) {
                throw RubyExceptions.CreateIndexError("index {0} out of regexp", groupIndex);
            }

            if (groupIndex < 0) {
                groupIndex += match.GroupCount;
            }

            if (!match.GroupSuccess(groupIndex)) {
                throw RubyExceptions.CreateIndexError("regexp group {0} not matched", groupIndex);
            }

            var replacement = value as MutableString ?? Protocols.CastToString(stringCast, value);

            return ReplaceSubstring(self, match.GetGroupStart(groupIndex), match.GetGroupLength(groupIndex), replacement);
        }

        #endregion

        #region casecmp, capitalize, capitalize!, downcase, downcase!, swapcase, swapcase!, upcase, upcase!

        /// <summary>
        /// MRI's check_case_options (string.c). The distinctions are MRI's: an unrecognised
        /// option is "invalid option", an option that cannot follow the first is "invalid second
        /// option", and a second option after one that takes no partner is "too many options".
        /// Verified against CRuby 4.0.6:
        ///   "a".upcase(:foo)             -> invalid option
        ///   "a".upcase(:turkic, :ascii)  -> invalid second option
        ///   "a".upcase(:ascii, :ascii)   -> too many options
        ///   "a".upcase(:fold)            -> option :fold only allowed for downcasing
        /// </summary>
        private static CaseMappingOptions ParseCaseOptions(object[]/*!*/ options, bool foldingAllowed) {
            if (options.Length == 0) {
                return CaseMappingOptions.None;
            }
            if (options.Length > 2) {
                throw RubyExceptions.CreateArgumentError("too many options");
            }

            CaseMappingOptions first, partner;
            switch (OptionName(options[0])) {
                case "ascii": first = CaseMappingOptions.Ascii; partner = CaseMappingOptions.None; break;
                case "turkic": first = CaseMappingOptions.Turkic; partner = CaseMappingOptions.Lithuanian; break;
                case "lithuanian": first = CaseMappingOptions.Lithuanian; partner = CaseMappingOptions.Turkic; break;
                case "fold":
                    if (!foldingAllowed) {
                        throw RubyExceptions.CreateArgumentError("option :fold only allowed for downcasing");
                    }
                    first = CaseMappingOptions.Fold;
                    partner = CaseMappingOptions.None;
                    break;
                default:
                    throw RubyExceptions.CreateArgumentError("invalid option");
            }

            if (options.Length == 1) {
                return first;
            }
            if (partner == CaseMappingOptions.None) {
                throw RubyExceptions.CreateArgumentError("too many options");
            }
            if (OptionName(options[1]) != (partner == CaseMappingOptions.Turkic ? "turkic" : "lithuanian")) {
                throw RubyExceptions.CreateArgumentError("invalid second option");
            }
            return first | partner;
        }

        // Anything that is not a Symbol is simply not one of the four names, and MRI reports it
        // as an invalid option rather than a type error: "a".upcase(1) is an ArgumentError.
        private static string OptionName(object option) {
            return (option as RubySymbol)?.ToString();
        }

        /// <summary>
        /// The text a case mapping should run over, or null when the string has to be treated as
        /// opaque bytes. ASCII-8BIT is the case that matters: MRI maps only a-z/A-Z in it, so
        /// "\xE4".b.upcase is "\xE4" and not "\xC4" the way the same bytes in ISO-8859-1 would be.
        /// Bytes that are invalid in the string's own encoding are not a problem here - they
        /// decode to lone surrogates (see EscapingEncoding), which no mapping touches, so they
        /// survive the round trip untouched.
        /// </summary>
        private static string TryDecodeForCaseMapping(MutableString/*!*/ self) {
            RubyEncoding encoding = self.Encoding;
            if (ReferenceEquals(encoding, RubyEncoding.Binary) || encoding.IsDummy) {
                return null;
            }
            return self.ToString();
        }

        /// <summary>
        /// ASCII-only mapping of opaque bytes, each byte standing for itself. Answers null when
        /// nothing moved. Length never changes, since no a-z/A-Z has a multi-character mapping.
        /// </summary>
        private static byte[] MapAsciiBytes(byte[]/*!*/ bytes, CaseMappingKind kind) {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) {
                chars[i] = (char)bytes[i];
            }

            string source = new String(chars);
            string mapped = UnicodeCaseMapping.Apply(source, kind, CaseMappingOptions.Ascii);
            if (ReferenceEquals(mapped, source)) {
                return null;
            }

            var result = new byte[mapped.Length];
            for (int i = 0; i < mapped.Length; i++) {
                result[i] = (byte)mapped[i];
            }
            return result;
        }

        private static MutableString/*!*/ CaseMap(MutableString/*!*/ self, CaseMappingKind kind,
            object[]/*!*/ options, bool foldingAllowed) {

            CaseMappingOptions flags = ParseCaseOptions(options, foldingAllowed);

            string source = TryDecodeForCaseMapping(self);
            if (source != null) {
                return MutableString.Create(UnicodeCaseMapping.Apply(source, kind, flags), self.Encoding).TaintBy(self);
            }

            byte[] bytes = self.ToByteArray();
            return MutableString.CreateBinary(MapAsciiBytes(bytes, kind) ?? bytes, self.Encoding).TaintBy(self);
        }

        private static MutableString CaseMapInPlace(MutableString/*!*/ self, CaseMappingKind kind,
            object[]/*!*/ options, bool foldingAllowed) {

            // The options are checked before the receiver is: "abc".freeze.upcase!(:bogus) is an
            // ArgumentError in MRI, not a FrozenError.
            CaseMappingOptions flags = ParseCaseOptions(options, foldingAllowed);
            self.RequireNotFrozen();

            string source = TryDecodeForCaseMapping(self);
            if (source != null) {
                string mapped = UnicodeCaseMapping.Apply(source, kind, flags);
                if (ReferenceEquals(mapped, source)) {
                    return null;
                }
                self.Clear();
                self.Append(mapped);
                return self;
            }

            byte[] mappedBytes = MapAsciiBytes(self.ToByteArray(), kind);
            if (mappedBytes == null) {
                return null;
            }
            self.Clear();
            self.Append(mappedBytes);
            return self;
        }

        // Ruby 3.4: a chilled literal counts as frozen here, so +"str" answers a copy that
        // can be mutated without the warning.
        [RubyMethod("+@")]
        public static MutableString/*!*/ Unchill(MutableString/*!*/ self) {
            return (self.IsFrozen || self.IsChilled) ? self.Clone() : self;
        }

        /// <summary>
        /// #casecmp folds a-z/A-Z and nothing else - it is not the Unicode comparison #casecmp?
        /// is. Confirmed against CRuby 4.0.6: "ä".casecmp("Ä") is 1, and "ss".casecmp("ß") is -1.
        /// </summary>
        public static int Casecmp(MutableString/*!*/ self, MutableString/*!*/ other) {
            return Compare(DownCaseAscii(self), DownCaseAscii(other));
        }

        private static MutableString/*!*/ DownCaseAscii(MutableString/*!*/ str) {
            byte[] bytes = str.ToByteArray();
            return MutableString.CreateBinary(MapAsciiBytes(bytes, CaseMappingKind.DownCase) ?? bytes, str.Encoding);
        }

        /// <summary>
        /// The key #casecmp? compares by: full case folding, which is why "ß".casecmp?("ss") is
        /// true where #casecmp says they differ.
        /// </summary>
        private static string/*!*/ FoldForComparison(MutableString/*!*/ str) {
            string source = TryDecodeForCaseMapping(str);
            if (source != null) {
                return UnicodeCaseMapping.Fold(source);
            }
            byte[] bytes = str.ToByteArray();
            byte[] mapped = MapAsciiBytes(bytes, CaseMappingKind.DownCase) ?? bytes;
            var chars = new char[mapped.Length];
            for (int i = 0; i < mapped.Length; i++) {
                chars[i] = (char)mapped[i];
            }
            return new String(chars);
        }

        // MRI answers nil rather than raising when the argument is not a String, and also when
        // the two encodings are not compatible.
        [RubyMethod("casecmp")]
        public static object Casecmp(ConversionStorage<MutableString>/*!*/ stringTryCast, MutableString/*!*/ self, object other) {
            MutableString str = (other is RubySymbol) ? null : Protocols.TryCastToString(stringTryCast, other);
            if (str == null) {
                return null;
            }
            if (!self.Encoding.Equals(str.Encoding) && !(self.IsAscii() || str.IsAscii())) {
                return null;
            }
            return ScriptingRuntimeHelpers.Int32ToObject(Casecmp(self, str));
        }

        [RubyMethod("casecmp?")]
        public static object CasecmpQ(ConversionStorage<MutableString>/*!*/ stringTryCast, MutableString/*!*/ self, object other) {
            MutableString str = (other is RubySymbol) ? null : Protocols.TryCastToString(stringTryCast, other);
            if (str == null) {
                return null;
            }
            if (!self.Encoding.Equals(str.Encoding) && !(self.IsAscii() || str.IsAscii())) {
                return null;
            }
            return ScriptingRuntimeHelpers.BooleanToObject(FoldForComparison(self) == FoldForComparison(str));
        }

        [RubyMethod("capitalize")]
        public static MutableString/*!*/ Capitalize(MutableString/*!*/ self, params object[]/*!*/ options) {
            return CaseMap(self, CaseMappingKind.Capitalize, options, false);
        }

        [RubyMethod("capitalize!")]
        public static MutableString CapitalizeInPlace(MutableString/*!*/ self, params object[]/*!*/ options) {
            return CaseMapInPlace(self, CaseMappingKind.Capitalize, options, false);
        }

        [RubyMethod("downcase")]
        public static MutableString/*!*/ DownCase(MutableString/*!*/ self, params object[]/*!*/ options) {
            return CaseMap(self, CaseMappingKind.DownCase, options, true);
        }

        [RubyMethod("downcase!")]
        public static MutableString DownCaseInPlace(MutableString/*!*/ self, params object[]/*!*/ options) {
            return CaseMapInPlace(self, CaseMappingKind.DownCase, options, true);
        }

        [RubyMethod("swapcase")]
        public static MutableString/*!*/ SwapCase(MutableString/*!*/ self, params object[]/*!*/ options) {
            return CaseMap(self, CaseMappingKind.SwapCase, options, false);
        }

        [RubyMethod("swapcase!")]
        public static MutableString SwapCaseInPlace(MutableString/*!*/ self, params object[]/*!*/ options) {
            return CaseMapInPlace(self, CaseMappingKind.SwapCase, options, false);
        }

        [RubyMethod("upcase")]
        public static MutableString/*!*/ UpCase(MutableString/*!*/ self, params object[]/*!*/ options) {
            return CaseMap(self, CaseMappingKind.UpCase, options, false);
        }

        [RubyMethod("upcase!")]
        public static MutableString UpCaseInPlace(MutableString/*!*/ self, params object[]/*!*/ options) {
            return CaseMapInPlace(self, CaseMappingKind.UpCase, options, false);
        }

        #endregion


        #region center

        private static readonly MutableString _DefaultPadding = MutableString.CreateAscii(" ").Freeze();

        [RubyMethod("center")]
        public static MutableString/*!*/ Center(MutableString/*!*/ self, 
            [DefaultProtocol]int length,
            [Optional, DefaultProtocol]MutableString padding) {

            if (padding != null && padding.IsEmpty) {
                throw RubyExceptions.CreateArgumentError("zero width padding");
            }

            if (padding == null) {
                padding = _DefaultPadding;
            } else {
                self.RequireCompatibleEncoding(padding);
            }

            int selfLength = self.GetCharCount();
            if (selfLength >= length) {
                return self;
            }

            int paddingLength = padding.GetCharCount();

            char[] charArray = new char[length];
            int n = (length - selfLength) / 2;

            for (int i = 0; i < n; i++) {
                charArray[i] = padding.GetChar(i % paddingLength);
            }

            for (int i = 0; i < selfLength; i++) {
                charArray[n + i] = self.GetChar(i);
            }

            int m = length - selfLength - n;
            for (int i = 0; i < m; i++) {
                charArray[n + selfLength + i] = padding.GetChar(i % paddingLength);
            }

            return self.CreateDerived().Append(charArray).TaintBy(self).TaintBy(padding); 
        }

        #endregion


        #region chomp, chomp!, chop, chop!

        private static MutableString/*!*/ ChompTrailingCarriageReturns(MutableString/*!*/ str, bool removeCarriageReturnsToo) {
            int end = str.GetCharCount();
            while (true) {
                if (end > 1) {
                    if (str.GetChar(end - 1) == '\n') {
                        end -= str.GetChar(end - 2) == '\r' ? 2 : 1;
                    } else if (removeCarriageReturnsToo && str.GetChar(end - 1) == '\r') {
                        end -= 1;
                    }
                    else {
                        break;
                    }
                } else if (end > 0) {
                    if (str.GetChar(end - 1) == '\n' || str.GetChar(end - 1) == '\r') {
                        end -= 1;
                    }
                    break;
                } else {
                    break;
                }
            }
            return str.GetSlice(0, end);
        }

        private static MutableString InternalChomp(MutableString/*!*/ self, MutableString separator) {
            if (separator == null) {
                return self.CloneDerived();
            }

            // Remove multiple trailing CR/LFs
            if (separator.IsEmpty) {
                return ChompTrailingCarriageReturns(self, false).TaintBy(self);
            }

            // Remove single trailing CR/LFs
            MutableString result = self.CloneDerived();
            int length = result.GetCharCount();
            if (separator.StartsWith('\n') && separator.GetLength() == 1) {
                if (length > 1 && result.GetChar(length - 2) == '\r' && result.GetChar(length - 1) == '\n') {
                    result.Remove(length - 2, 2);
                } else if (length > 0 && (self.GetChar(length - 1) == '\n' || result.GetChar(length - 1) == '\r')) {
                    result.Remove(length - 1, 1);
                }
            } else if (result.EndsWith(separator)) {
                int separatorLength = separator.GetCharCount();
                result.Remove(length - separatorLength, separatorLength);
            }

            return result;
        }

        [RubyMethod("chomp")]
        public static MutableString/*!*/ Chomp(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return InternalChomp(self, context.InputSeparator);
        }

        [RubyMethod("chomp")]
        public static MutableString/*!*/ Chomp(MutableString/*!*/ self, [DefaultProtocol]MutableString separator) {
            return InternalChomp(self, separator);
        }

        [RubyMethod("chomp!")]
        public static MutableString/*!*/ ChompInPlace(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return ChompInPlace(self, context.InputSeparator);
        }

        [RubyMethod("chomp!")]
        public static MutableString ChompInPlace(MutableString/*!*/ self, [DefaultProtocol]MutableString separator) {
            MutableString result = InternalChomp(self, separator);

            if (result.Equals(self) || result == null) {
                self.RequireNotFrozen();
                return null;
            }

            self.Clear();
            self.Append(result);
            return self;
        }

        private static MutableString/*!*/ ChopInteral(MutableString/*!*/ self) {
            int length = self.GetCharCount();
            if (length == 1 || self.GetChar(length - 2) != '\r' || self.GetChar(length - 1) != '\n') {
                self.Remove(length - 1, 1);
            } else {
                self.Remove(length - 2, 2);
            }
            return self;
        }

        [RubyMethod("chop!")]
        public static MutableString ChopInPlace(MutableString/*!*/ self) {
            self.RequireNotFrozen();
            return self.IsEmpty ? null : ChopInteral(self);
        }

        [RubyMethod("chop")]
        public static MutableString/*!*/ Chop(MutableString/*!*/ self) {
            return self.IsEmpty ? self.CreateDerived().TaintBy(self) : ChopInteral(self.CloneDerived());
        }

        #endregion


        #region dump, inspect

        public static string/*!*/ GetQuotedStringRepresentation(MutableString/*!*/ self, bool isDump, char quote) {
            // #dump always escapes non-ASCII. #inspect only does so when the characters
            // cannot be represented in the output encoding; for a UTF-8 string it prints
            // them as they are, which is what Ruby has done since 1.9 became 2.0.
            // AppendRepresentation still forces escaping for binary strings.
            // Only a UTF-8 string can be printed as-is: anything else has to stay escaped,
            // or rendering it would try to encode characters the target code page cannot
            // represent and throw.
            var escape = MutableString.Escape.Special;
            if (isDump || self.Encoding != RubyEncoding.UTF8) {
                escape |= MutableString.Escape.NonAscii;
            }

            return self.AppendRepresentation(
                new StringBuilder().Append(quote), null, escape, quote
            ).Append(quote).ToString();
        }

        // encoding aware
        [RubyMethod("dump")]
        public static MutableString/*!*/ Dump(MutableString/*!*/ self) {
            // Note that "self" could be a subclass of MutableString, and the return value should be
            // of the same type
            if (!self.Encoding.IsAsciiIdentity) {
                // A string whose encoding is not ASCII-compatible cannot be dumped as text:
                // MRI escapes its *bytes* and appends the call that puts the encoding back.
                var bytes = MutableString.CreateBinary(self.ToByteArray());
                string quoted = GetQuotedStringRepresentation(bytes, true, '"')
                    + ".force_encoding(\"" + self.Encoding.Name + "\")";
                return MutableString.Create(quoted, RubyEncoding.Ascii).TaintBy(self);
            }

            return self.CreateDerived().Append(GetQuotedStringRepresentation(self, true, '"')).TaintBy(self);
        }

        // encoding aware
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, MutableString/*!*/ self) {
            // TODO: RubyEncoding encoding = context.DefaultInternalEncoding ?? context.DefaultExternalEncoding;
            
            // Note that "self" could be a subclass of MutableString, but the return value should 
            // always be just a MutableString
            return MutableString.Create(GetQuotedStringRepresentation(self, false, '"'), self.Encoding).TaintBy(self);
        }

        #endregion

        #region each_byte/bytes, chars, chr, each_codepoint/codepoints, each_line/lines

        [RubyMethod("bytes")]
        [RubyMethod("each_byte")]
        public static Enumerator/*!*/ EachByte(MutableString/*!*/ self) {
            return new Enumerator(self, "each_byte");
        }

        [RubyMethod("bytes")]
        [RubyMethod("each_byte")]
        public static object EachByte([NotNull]BlockParam/*!*/ block, MutableString/*!*/ self) {
            foreach (byte b in self.GetBytes()) {
                object result;
                if (block.Yield(ScriptingRuntimeHelpers.Int32ToObject((int)b), out result)) {
                    return result;
                }
            }

            return self;
        }

        [RubyMethod("chars")]
        [RubyMethod("each_char")]
        public static Enumerator/*!*/ EachChar(MutableString/*!*/ self) {
            return new Enumerator(self, "each_char");
        }

        [RubyMethod("chars")]
        [RubyMethod("each_char")]
        public static object EachChar([NotNull]BlockParam/*!*/ block, MutableString/*!*/ self) {
            var enumerator = self.GetCharacters();
            while (enumerator.MoveNext()) {
                object result;
                if (block.Yield(enumerator.Current.ToMutableString(self.Encoding), out result)) {
                    return result;
                }
            }

            return self;
        }

        [RubyMethod("chr")]
        public static MutableString/*!*/ FirstChar(MutableString/*!*/ self) {
            if (self.IsEmpty) {
                return self.CloneDerived();
            }

            // TODO: optimize
            var enumerator = self.GetCharacters();
            enumerator.MoveNext();
            return enumerator.Current.ToMutableString(self.Encoding);
        }

        [RubyMethod("each_codepoint")]
        public static Enumerator/*!*/ EachCodePoint(MutableString/*!*/ self) {
            return new Enumerator(self, "each_codepoint");
        }

        // Ruby 1.9: #codepoints is the array, #each_codepoint the enumerator. The array form
        // walks the string straight away, so a broken byte sequence is reported there and not
        // at the first #next.
        [RubyMethod("codepoints")]
        public static RubyArray/*!*/ GetCodePoints(MutableString/*!*/ self) {
            var result = new RubyArray();
            var enumerator = self.GetCharacters();
            while (enumerator.MoveNext()) {
                if (!enumerator.Current.IsValid) {
                    throw RubyExceptions.CreateArgumentError("invalid byte sequence in {0}", self.Encoding.Name);
                }
                result.Add(ScriptingRuntimeHelpers.Int32ToObject((int)enumerator.Current.Codepoint));
            }
            return result;
        }

        [RubyMethod("codepoints")]
        [RubyMethod("each_codepoint")]
        public static object EachCodePoint([NotNull]BlockParam/*!*/ block, MutableString/*!*/ self) {
            var enumerator = self.GetCharacters();
            while (enumerator.MoveNext()) {
                if (!enumerator.Current.IsValid) {
                    throw RubyExceptions.CreateArgumentError("invalid byte sequence in {0}", self.Encoding.Name);
                }

                object result;
                if (block.Yield(ScriptingRuntimeHelpers.Int32ToObject((int)enumerator.Current.Codepoint), out result)) {
                    return result;
                }
            }

            return self;
        }

        internal static readonly MutableString DefaultLineSeparator = MutableString.CreateAscii("\n").Freeze();
        internal static readonly MutableString DefaultParagraphSeparator = MutableString.CreateAscii("\n\n").Freeze();

        [RubyMethod("lines")]
        [RubyMethod("each_line")]
        public static Enumerator/*!*/ EachLine(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return new Enumerator((_, block) => EachLine(block, self, context.InputSeparator));
        }

        [RubyMethod("lines")]
        [RubyMethod("each_line")]
        public static object EachLine(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self) {
            return EachLine(block, self, context.InputSeparator);
        }

        [RubyMethod("lines")]
        [RubyMethod("each_line")]
        public static Enumerator/*!*/ EachLine(MutableString/*!*/ self, [DefaultProtocol]MutableString separator) {
            return new Enumerator((_, block) => EachLine(block, self, separator, 0));
        }

        [RubyMethod("lines")]
        [RubyMethod("each_line")]
        public static object EachLine([NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, [DefaultProtocol]MutableString separator) {
            return EachLine(block, self, separator, 0);
        }

        public static object EachLine(BlockParam/*!*/ block, MutableString/*!*/ self, [DefaultProtocol]MutableString separator, int start) {
            self.TrackChanges();

            MutableString paragraphSeparator;
            if (separator == null || separator.IsEmpty) {
                separator = DefaultLineSeparator;
                paragraphSeparator = DefaultParagraphSeparator;
            } else {
                paragraphSeparator = null;
            }

            // TODO: this is slow, refactor when we redo MutableString
            MutableString str = self;

            // In "normal" mode just split the string at the end of each seperator occurrance.
            // In "paragraph" mode, split the string at the end of each occurrance of two or more
            // successive seperators.
            while (start < self.Length) {
                int end;
                if (paragraphSeparator == null) {
                    end = str.IndexOf(separator, start);
                    if (end >= 0) {
                        end += separator.Length;
                    } else {
                        end = str.Length;
                    }
                } else {
                    end = str.IndexOf(paragraphSeparator, start);
                    if (end >= 0) {
                        end += (2 * separator.Length);
                        while (str.IndexOf(separator, end) == end) {
                            end += separator.Length;
                        }
                    } else {
                        end = str.Length;
                    }
                }

                object result;
                MutableString line = self.CreateDerived().TaintBy(self).Append(str, start, end - start);
                if (block.Yield(line, out result)) {
                    return result;
                }

                start = end;
            }

            // MRI 1.8: this is checked after each line
            // MRI 1.9: not checked at all
            // Ensure that the underlying string has not been mutated during the iteration
            RequireNoVersionChange(self);
            return self;
        }

        #endregion


        #region empty?, size, bytesize, length, ascii_only?, encoding, force_encoding, encode, encode!

        // encoding aware
        [RubyMethod("empty?")]
        public static bool IsEmpty(MutableString/*!*/ self) {
            return self.IsEmpty;
        }

        // encoding aware
        [RubyMethod("size")]
        [RubyMethod("length")]
        public static int GetCharacterCount(MutableString/*!*/ self) {
            return self.GetCharacterCount();
        }

        // encoding aware
        [RubyMethod("bytesize")]
        public static int GetByteCount(MutableString/*!*/ self) {
            return self.GetByteCount();
        }

        // encoding aware
        [RubyMethod("ascii_only?")]
        public static bool IsAscii(MutableString/*!*/ self) {
            return self.IsAscii();
        }

        // encoding aware
        [RubyMethod("encoding")]
        public static RubyEncoding/*!*/ GetEncoding(MutableString/*!*/ self) {
            return self.Encoding;
        }

        // encoding aware
        [RubyMethod("valid_encoding?")]
        public static bool ValidEncoding(MutableString/*!*/ self) {
            return !self.ContainsInvalidCharacters();
        }

        // encoding aware
        [RubyMethod("force_encoding")]
        public static MutableString/*!*/ ForceEncoding(MutableString/*!*/ self, [NotNull]RubyEncoding/*!*/ encoding) {
            // Retagging is a mutation as far as frozen-ness is concerned.
            self.RequireNotFrozen();
            self.ForceEncoding(encoding);
            return self;
        }

        // encoding aware
        [RubyMethod("force_encoding")]
        public static MutableString/*!*/ ForceEncoding(RubyContext/*!*/ context, MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ encodingName) {
            return ForceEncoding(self, ResolveEncodingName(context, encodingName));
        }

        /// <summary>
        /// "internal", "external", "locale" and "filesystem" name the current defaults rather
        /// than an encoding of their own; an unset default_internal means BINARY here.
        /// </summary>
        private static RubyEncoding/*!*/ ResolveEncodingName(RubyContext/*!*/ context, MutableString/*!*/ name) {
            switch (name.ConvertToString().ToLowerInvariant()) {
                case "internal": return context.DefaultInternalEncoding ?? RubyEncoding.Binary;
                case "external": return context.DefaultExternalEncoding;
                case "locale":
                case "filesystem": return context.DefaultExternalEncoding;
                default: return context.GetRubyEncoding(name);
            }
        }

        [RubyMethod("encode")]
        public static MutableString/*!*/ Encode(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ fallbackStorage,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr,
            MutableString/*!*/ self,
            [Optional]object toEncoding,
            [Optional]object fromEncoding,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            // TODO: optimize
            return EncodeInPlace(fallbackStorage, toHash, toStr, self.Clone(), toEncoding, fromEncoding, options);
        }

        [RubyMethod("encode!")]
        public static MutableString/*!*/ EncodeInPlace(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ fallbackStorage,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr,
            MutableString/*!*/ self,
            [Optional]object toEncoding,
            [Optional]object fromEncoding,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            Protocols.TryConvertToOptions(toHash, ref options, ref toEncoding, ref fromEncoding);

            // encodings:
            RubyEncoding to, from;
            MutableString toEncodingName = null, fromEncodingName = null;
            if (toEncoding == Missing.Value) {
                // Without a target and without a default_internal the encoding does not change,
                // but the newline decorators still have to run.
                to = toStr.Context.DefaultInternalEncoding ?? self.Encoding;
            } else {
                to = toEncoding as RubyEncoding;
                if (to == null) {
                    toEncodingName = Protocols.CastToString(toStr, toEncoding);
                }
            }

            if (fromEncoding == Missing.Value) {
                from = self.Encoding;
            } else {
                from = fromEncoding as RubyEncoding;
                if (from == null) {
                    fromEncodingName = Protocols.CastToString(toStr, fromEncoding);
                }
            }

            try {
                if (fromEncodingName != null) {
                    from = toStr.Context.GetRubyEncoding(fromEncodingName);
                }
                if (toEncodingName != null) {
                    to = toStr.Context.GetRubyEncoding(toEncodingName);
                }
            } catch (ArgumentException) {
                throw new ConverterNotFoundError(RubyExceptions.FormatMessage("code converter not found ({0} to {1})",
                    (fromEncodingName != null) ? fromEncodingName.ToAsciiString() : from.Name, 
                    (toEncodingName != null) ? toEncodingName.ToAsciiString() : to.Name
                ));
            }

            self.RequireNotFrozen();

            var settings = TranscodeSettings.Parse(toStr.Context, toHash, options, to);

            if (from == to && !settings.ReplaceInvalid && settings.XmlMode == 0 && settings.Newline == 0) {
                // Nothing to do, and in particular nothing to validate: MRI does not check the
                // bytes when the source and target encodings are the same.
                self.ForceEncoding(to);
                return self;
            }

            var transcoded = Transcode(fallbackStorage, toStr, self, from, to, settings);

            if (settings.XmlMode == 'a') {
                // xml: :attr produces a quoted attribute value, quotes included.
                var quoted = MutableString.CreateMutable(to);
                quoted.Append('"').Append(transcoded).Append('"');
                transcoded = quoted;
            }

            // The content is replaced wholesale rather than through Replace: the receiver still
            // carries the source encoding at this point, and Replace would refuse to splice text
            // in the target encoding into it.
            self.Clear();
            self.ForceEncoding(to);
            self.Append(transcoded);
            return self;
        }

        #region Transcoding with options

        /// <summary>
        /// The options #encode accepts, resolved once so the conversion loop does not have to
        /// re-read the hash per character.
        /// </summary>
        private struct TranscodeSettings {
            internal bool ReplaceInvalid;
            internal bool ReplaceUndefined;
            internal MutableString Replacement;
            internal object Fallback;
            internal int XmlMode;           // 0 none, 't' text, 'a' attr
            internal int Newline;           // 0 none, 'u' universal, 'r' cr, 'c' crlf

            internal bool IsPlain {
                get {
                    return !ReplaceInvalid && !ReplaceUndefined && Replacement == null
                        && Fallback == null && XmlMode == 0 && Newline == 0;
                }
            }

            /// <summary>
            /// MRI's default replacement is U+FFFD when the target can hold it and "?" otherwise.
            /// </summary>
            internal MutableString/*!*/ GetReplacement(RubyEncoding/*!*/ to) {
                if (Replacement != null) {
                    return Replacement;
                }
                return to.IsUnicodeEncoding
                    ? MutableString.Create("�", to)
                    : MutableString.CreateAscii("?");
            }

            internal static TranscodeSettings Parse(RubyContext/*!*/ context,
                ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
                IDictionary<object, object> options, RubyEncoding/*!*/ to) {

                var result = new TranscodeSettings();
                if (options == null) {
                    return result;
                }

                foreach (var entry in options) {
                    var key = entry.Key as RubySymbol;
                    if (key == null) {
                        continue;
                    }

                    switch (key.ToString()) {
                        case "invalid":
                            result.ReplaceInvalid = CheckReplaceOption(entry.Value, "invalid");
                            break;

                        case "undef":
                            result.ReplaceUndefined = CheckReplaceOption(entry.Value, "undefined");
                            break;

                        case "replace":
                            result.Replacement = entry.Value as MutableString;
                            break;

                        case "fallback":
                            result.Fallback = entry.Value;
                            break;

                        case "xml":
                            var xml = entry.Value as RubySymbol;
                            if (xml != null && xml.ToString() == "text") {
                                result.XmlMode = 't';
                            } else if (xml != null && xml.ToString() == "attr") {
                                result.XmlMode = 'a';
                            } else {
                                throw RubyExceptions.CreateArgumentError("unexpected value for xml option: {0}",
                                    xml != null ? (object)xml.ToString() : context.Inspect(entry.Value));
                            }
                            // :xml implies escaping anything the target cannot hold.
                            result.ReplaceUndefined = true;
                            break;

                        case "newline":
                            var nl = entry.Value as RubySymbol;
                            string nlName = nl != null ? nl.ToString() : null;
                            if (nlName == "universal") {
                                result.Newline = 'u';
                            } else if (nlName == "cr") {
                                result.Newline = 'r';
                            } else if (nlName == "crlf") {
                                result.Newline = 'c';
                            } else if (nlName == "lf") {
                                result.Newline = 'u';
                            } else {
                                throw RubyExceptions.CreateArgumentError("unexpected value for newline option: {0}",
                                    (object)nlName ?? context.Inspect(entry.Value));
                            }
                            break;

                        case "universal_newline":
                            if (RubyOps.IsTrue(entry.Value)) { result.Newline = 'u'; }
                            break;

                        case "cr_newline":
                            if (RubyOps.IsTrue(entry.Value)) { result.Newline = 'r'; }
                            break;

                        case "crlf_newline":
                            if (RubyOps.IsTrue(entry.Value)) { result.Newline = 'c'; }
                            break;
                    }
                }

                return result;
            }

            /// <summary>
            /// :invalid and :undef take :replace or nil and nothing else.
            /// </summary>
            private static bool CheckReplaceOption(object value, string/*!*/ name) {
                if (value == null) {
                    return false;
                }
                var symbol = value as RubySymbol;
                if (symbol != null && symbol.ToString() == "replace") {
                    return true;
                }
                throw RubyExceptions.CreateArgumentError("unknown value for {0} character option", name);
            }
        }

        /// <summary>
        /// The body of #encode once the encodings and options are known.
        ///
        /// This is deliberately a character at a time rather than a single Encoding.Convert: the
        /// options are all about what to do with the characters that cannot be converted, and .NET's
        /// fallbacks cannot express "ask this Ruby object what to substitute".
        /// </summary>
        private static MutableString/*!*/ Transcode(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ fallbackStorage,
            ConversionStorage<MutableString>/*!*/ toStr,
            MutableString/*!*/ self, RubyEncoding/*!*/ from, RubyEncoding/*!*/ to, TranscodeSettings settings) {

            // MRI has no converter between a byte string and a character encoding in either
            // direction, so a byte above 0x7F coming out of ASCII-8BIT, and a character above
            // 0x7F going into it, are undefined conversions however well the code point would
            // otherwise fit.  Between ASCII-8BIT and itself nothing is converted, so nothing is
            // undefined either.
            bool binarySource = from == RubyEncoding.Binary && to != RubyEncoding.Binary;
            bool binaryTarget = to == RubyEncoding.Binary && from != RubyEncoding.Binary;

            string text = DecodeForTranscoding(self, from, to, settings);

            if (settings.Newline != 0) {
                text = NormalizeNewlines(text, settings.Newline);
            }

            var result = MutableString.CreateMutable(to);
            // ASCII-8BIT as a *target* takes only ASCII: MRI has no converter from a character
            // encoding to a byte string, so "\u00e9".encode("BINARY") is an undefined conversion
            // even though the code point would fit in one byte.
            var encoder = to.StrictEncoding;
            var buffer = new char[2];

            for (int i = 0; i < text.Length; i++) {
                int charCount = 1;
                if (Char.IsHighSurrogate(text[i]) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1])) {
                    charCount = 2;
                }
                buffer[0] = text[i];
                if (charCount == 2) {
                    buffer[1] = text[i + 1];
                }
                string piece = new string(buffer, 0, charCount);
                i += charCount - 1;

                string escaped = EscapeForXml(piece, settings.XmlMode);
                if (escaped != null) {
                    result.Append(escaped);
                    continue;
                }

                bool oversized = (binarySource || binaryTarget) && buffer[0] > 0x7f;
                if (!oversized && CanEncode(encoder, buffer, charCount)) {
                    result.Append(piece);
                    continue;
                }

                // A character the target has no room for. :fallback gets first refusal, then
                // :undef => :replace, and otherwise it is an error.
                // An explicit :replace with undef: :replace wins over :fallback.
                MutableString substitute = (settings.ReplaceUndefined && settings.Replacement != null)
                    ? null
                    : CallFallback(fallbackStorage, toStr, settings.Fallback, piece, from);
                if (substitute != null) {
                    string replacementText = substitute.ConvertToString();
                    // The substitute has to fit in the target encoding too.
                    if (!CanEncode(encoder, replacementText.ToCharArray(), replacementText.Length)) {
                        throw RubyExceptions.CreateArgumentError("too big fallback string");
                    }
                    result.Append(replacementText);
                    continue;
                }

                if (settings.XmlMode != 0) {
                    // xml: escapes anything left over as a numeric character reference.
                    result.Append(CharacterReference(piece));
                    continue;
                }

                if (!settings.ReplaceUndefined) {
                    throw CreateUndefinedConversionError(piece, from, to, binarySource);
                }

                result.Append(settings.GetReplacement(to).ConvertToString());
            }

            return result;
        }

        /// <summary>
        /// The error MRI raises for a character no leg of the conversion can represent.
        ///
        /// A byte leaving ASCII-8BIT fails on the first leg, the one into UTF-8, so it is named by
        /// its byte rather than by a code point and the error reports UTF-8 as its destination
        /// however the conversion was asked for; everything else fails on the last leg, the one
        /// into the target encoding.
        /// </summary>
        private static Exception/*!*/ CreateUndefinedConversionError(string/*!*/ piece,
            RubyEncoding/*!*/ from, RubyEncoding/*!*/ to, bool binarySource) {

            var path = RubyExceptions.ConversionPath(from, to);

            if (binarySource) {
                var raw = new byte[] { (byte)piece[0] };
                return RubyExceptions.CreateUndefinedConversionError(
                    RubyExceptions.InspectTranscodingBytes(raw), raw, path, 0);
            }

            byte[] charBytes = null;
            try {
                charBytes = from.StrictEncoding.GetBytes(piece);
            } catch (EncoderFallbackException) {
                // The character came out of the receiver's own bytes, so this should not happen;
                // if the source encoding cannot spell it again, #error_char has nothing to show.
            }

            int codepoint = Char.ConvertToUtf32(piece, 0);
            return RubyExceptions.CreateUndefinedConversionError(
                "U+" + codepoint.ToString(codepoint > 0xffff ? "X" : "X4", CultureInfo.InvariantCulture),
                charBytes, path, path.Length - 2);
        }

        /// <summary>
        /// Reads the receiver's bytes as characters of the source encoding, honouring
        /// :invalid => :replace.
        ///
        /// The bytes go through a stateful decoder one at a time so that the error units come out
        /// the shape MRI reports them: an error covers the longest prefix that could still have
        /// grown into a character, the byte that proved it could not is read again rather than
        /// swallowed with it, and a prefix left over at the end of the input is one incomplete
        /// character rather than a run of stray bytes.
        /// </summary>
        private static string/*!*/ DecodeForTranscoding(MutableString/*!*/ self, RubyEncoding/*!*/ from,
            RubyEncoding/*!*/ to, TranscodeSettings settings) {

            byte[] bytes = self.ToByteArray();
            var strict = from.StrictEncoding;

            // ASCII-8BIT has no invalid bytes at all - every byte is a character of it - so the
            // decoder dance below would be both pointless and wrong for it.
            if (from == RubyEncoding.Binary) {
                var raw = new StringBuilder(bytes.Length);
                foreach (byte b in bytes) {
                    raw.Append((char)b);
                }
                return raw.ToString();
            }

            string replacement = settings.ReplaceInvalid ? settings.GetReplacement(to).ConvertToString() : null;
            var result = new StringBuilder(bytes.Length);
            var decoder = strict.GetDecoder();
            var chars = new char[8];
            int pending = 0;    // bytes seen so far that have not produced a character yet
            int i = 0;

            while (i < bytes.Length) {
                int produced;
                try {
                    produced = decoder.GetChars(bytes, i, 1, chars, 0, false);
                } catch (DecoderFallbackException) {
                    // bytes[i] cannot continue what the decoder is holding.
                    byte[] errorBytes, readAgain;
                    if (pending != 0) {
                        errorBytes = new byte[pending];
                        Array.Copy(bytes, i - pending, errorBytes, 0, pending);
                        // bytes[i] is left for the next round: it may start a character of its own.
                        readAgain = new byte[] { bytes[i] };
                    } else {
                        errorBytes = new byte[] { bytes[i] };
                        readAgain = null;
                        i++;
                    }
                    pending = 0;
                    decoder = strict.GetDecoder();

                    if (replacement == null) {
                        throw RubyExceptions.CreateInvalidByteSequenceError(from, to, errorBytes, readAgain, false);
                    }
                    result.Append(replacement);
                    continue;
                }

                i++;
                if (produced == 0) {
                    pending++;
                    continue;
                }
                pending = 0;
                result.Append(chars, 0, produced);
            }

            if (pending != 0) {
                var errorBytes = new byte[pending];
                Array.Copy(bytes, bytes.Length - pending, errorBytes, 0, pending);
                if (replacement == null) {
                    throw RubyExceptions.CreateInvalidByteSequenceError(from, to, errorBytes, null, true);
                }
                result.Append(replacement);
            }

            return result.ToString();
        }

        private static bool CanEncode(Encoding/*!*/ encoder, char[]/*!*/ buffer, int charCount) {
            try {
                encoder.GetByteCount(buffer, 0, charCount);
                return true;
            } catch (EncoderFallbackException) {
                return false;
            }
        }

        /// <summary>xml: :text escapes &amp;, &lt; and &gt;; xml: :attr also escapes the quote.</summary>
        private static string EscapeForXml(string/*!*/ piece, int mode) {
            if (mode == 0 || piece.Length != 1) {
                return null;
            }
            switch (piece[0]) {
                case '&': return "&amp;";
                case '<': return "&lt;";
                case '>': return "&gt;";
                case '"': return mode == 'a' ? "&quot;" : null;
                case '\'': return mode == 'a' ? "&apos;" : null;
                default: return null;
            }
        }

        private static string/*!*/ CharacterReference(string/*!*/ piece) {
            return "&#x" + Char.ConvertToUtf32(piece, 0).ToString("X") + ";";
        }

        /// <summary>
        /// The three decorators are not variations on one normalisation: :universal_newline folds
        /// CRLF and CR down to LF, while :cr_newline and :crlf_newline only expand an LF and leave
        /// a CR that was already there alone. Chaining them turns "\r\n" into "\r\r".
        /// </summary>
        private static string/*!*/ NormalizeNewlines(string/*!*/ text, int mode) {
            switch (mode) {
                case 'r': return text.Replace("\n", "\r");
                case 'c': return text.Replace("\n", "\r\n");
                default: return text.Replace("\r\n", "\n").Replace("\r", "\n");
            }
        }

        /// <summary>
        /// :fallback may be a Hash, a Proc, a Method or anything else answering to #[]. A nil
        /// answer means "no substitute", which falls through to :undef.
        /// </summary>
        private static MutableString CallFallback(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ fallbackStorage,
            ConversionStorage<MutableString>/*!*/ toStr, object fallback, string/*!*/ piece, RubyEncoding/*!*/ from) {

            if (fallback == null) {
                return null;
            }

            // MRI ignores a :fallback that cannot be indexed rather than calling it.
            if (!fallbackStorage.Context.RespondTo(fallback, "[]")) {
                return null;
            }

            var key = MutableString.Create(piece, from);
            var site = fallbackStorage.GetCallSite("[]", 1);
            object answer = site.Target(site, fallback, key);
            if (answer == null) {
                return null;
            }
            return Protocols.CastToString(toStr, answer);
        }

        #endregion


        #endregion


        #region sub, gsub

        // returns true if block jumped
        // "result" will be null if there is no successful match
        private static bool BlockReplaceFirst(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, MutableString/*!*/ input, BlockParam/*!*/ block,
            RubyRegex/*!*/ pattern, out object blockResult, out MutableString result) {

            var matchScope = scope.GetInnerMostClosureScope();
            MatchData match = RegexpOps.Match(scope, pattern, input);
            if (match == null) {
                result = null;
                blockResult = null;
                matchScope.CurrentMatch = null;
                return false;
            }

            // copy upfront so that no modifications to the input string are included in the result:
            result = input.CloneDerived();
            matchScope.CurrentMatch = match;

            if (block.Yield(match.GetValue(), out blockResult)) {
                return true;
            }

            // resets the $~ scope variable to the last match (skipped if block jumped):
            matchScope.CurrentMatch = match;

            MutableString replacement = Protocols.ConvertToString(tosConversion, blockResult);
            result.TaintBy(replacement);

            // Note - we don't interpolate special sequences like \1 in block return value
            result.Replace(match.Index, match.Length, replacement);

            blockResult = null;
            return false;
        }
        
        // returns true if block jumped
        // "result" will be null if there is no successful match
        private static bool BlockReplaceAll(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, MutableString/*!*/ input, BlockParam/*!*/ block,
            RubyRegex/*!*/ regex, out object blockResult, out MutableString result) {

            var matchScope = scope.GetInnerMostClosureScope();

            var matches = regex.Matches(input);
            if (matches.Count == 0) {
                result = null;
                blockResult = null;
                matchScope.CurrentMatch = null;
                return false;
            }

            // create an empty result:
            result = input.CreateDerived().TaintBy(input);
            
            int offset = 0;
            foreach (MatchData match in matches) {
                matchScope.CurrentMatch = match;

                input.TrackChanges();
                if (block.Yield(match.GetValue(), out blockResult)) {
                    return true;
                }
                if (input.HasChanged) {
                    return false;
                }

                // resets the $~ scope variable to the last match (skipd if block jumped):
                matchScope.CurrentMatch = match;

                MutableString replacement = Protocols.ConvertToString(tosConversion, blockResult);
                result.TaintBy(replacement);

                // prematch:
                result.Append(input, offset, match.Index - offset);

                // replacement (unlike ReplaceAll, don't interpolate special sequences like \1 in block return value):
                result.Append(replacement);

                offset = match.Index + match.Length;
            }

            // post-last-match:
            result.Append(input, offset, input.Length - offset);

            blockResult = null;
            return false;
        }

        private static void AppendBackslashes(int backslashCount, MutableString/*!*/ result, int minBackslashes) {
            for (int j = 0; j < ((backslashCount - 1) >> 1) + minBackslashes; j++) {
                result.Append('\\');
            }
        }

        private static void AppendReplacementExpression(ConversionStorage<MutableString> toS, BinaryOpStorage hashDefault,
            MutableString/*!*/ input, MatchData/*!*/ match, MutableString/*!*/ result, Union<IDictionary<object, object>, MutableString>/*!*/ replacement) {

            if (replacement.Second != null) {
                AppendReplacementExpression(input, match, result, replacement.Second);
            } else {
                Debug.Assert(toS != null && hashDefault != null);

                object replacementObj = HashOps.GetElement(hashDefault, replacement.First, match.GetValue());
                if (replacementObj != null) {
                    var replacementStr = Protocols.ConvertToString(toS, replacementObj);
                    result.Append(replacementStr).TaintBy(replacementStr);
                }
            }
        }

        private static void AppendReplacementExpression(MutableString/*!*/ input, MatchData/*!*/ match, MutableString/*!*/ result, 
            MutableString/*!*/ replacement) {

            int backslashCount = 0;
            for (int i = 0; i < replacement.Length; i++) {
                char c = replacement.GetChar(i);
                if (c == '\\') {
                    backslashCount++;
                } else if (backslashCount == 0) {
                    result.Append(c);
                } else {
                    AppendBackslashes(backslashCount, result, 0);
                    // Odd number of \'s + digit means insert replacement expression
                    if ((backslashCount & 1) == 1) {
                        if (Char.IsDigit(c)) {
                            AppendGroupByIndex(match, c - '0', result);
                        } else if (c == '&') {
                            AppendGroupByIndex(match, match.GroupCount - 1, result);
                        } else if (c == '`') {
                            // Replace with everything in the input string BEFORE the match
                            result.Append(input, 0, match.Index);
                        } else if (c == '\'') {
                            // Replace with everything in the input string AFTER the match
                            int start = match.Index + match.Length;
                            // TODO:
                            result.Append(input, start, input.GetLength() - start);
                        } else if (c == '+') {
                            // Replace last character in last successful match group
                            AppendLastCharOfLastMatchGroup(match, result);
                        } else {
                            // unknown escaped replacement char, go ahead and replace untouched
                            result.Append('\\');
                            result.Append(c);
                        }
                    } else {
                        // Any other # of \'s or a non-digit character means insert literal \'s and character
                        AppendBackslashes(backslashCount, result, 1);
                        result.Append(c);
                    }
                    backslashCount = 0;
                }
            }
            AppendBackslashes(backslashCount, result, 1);
            result.TaintBy(replacement);
        }

        private static void AppendLastCharOfLastMatchGroup(MatchData/*!*/ match, MutableString/*!*/ result) {
            int i = match.GroupCount - 1;
            // move to last successful match group
            while (i > 0 && !match.GroupSuccess(i)) {
                i--;
            }

            if (i > 0 && match.GroupSuccess(i)) {
                int length = match.GetGroupLength(i);
                if (length > 0) {
                   result.Append(match.OriginalString, match.GetGroupStart(i) + length - 1, 1);
                }
            }
        }

        private static void AppendGroupByIndex(MatchData/*!*/ match, int index, MutableString/*!*/ result) {
            var value = match.GetGroupValue(index);
            if (value != null) {
                result.Append(value);
            }
        }

        private static MutableString ReplaceFirst(ConversionStorage<MutableString> toS, BinaryOpStorage hashDefault,
            RubyScope/*!*/ scope, MutableString/*!*/ input, Union<IDictionary<object, object>, MutableString>/*!*/ replacement, RubyRegex/*!*/ pattern) {

            MatchData match = RegexpOps.Match(scope, pattern, input);
            if (match == null) {
                return null;
            }

            MutableString result = input.CreateDerived().TaintBy(input);
            
            // prematch:
            result.Append(input, 0, match.Index);

            AppendReplacementExpression(toS, hashDefault, input, match, result, replacement);

            // postmatch:
            int offset = match.Index + match.Length;
            result.Append(input, offset, input.Length - offset);

            return result;
        }

        private static MutableString ReplaceAll(ConversionStorage<MutableString> toS, BinaryOpStorage hashDefault, 
            RubyScope/*!*/ scope, MutableString/*!*/ input, Union<IDictionary<object, object>, MutableString>/*!*/ replacement, RubyRegex/*!*/ regex) {
            var matchScope = scope.GetInnerMostClosureScope();
            
            IList<MatchData> matches = regex.Matches(input);
            if (matches.Count == 0) {
                matchScope.CurrentMatch = null;
                return null;
            }

            MutableString result = input.CreateDerived().TaintBy(input);

            int offset = 0;
            foreach (MatchData match in matches) {
                result.Append(input, offset, match.Index - offset);
                AppendReplacementExpression(toS, hashDefault, input, match, result, replacement);
                offset = match.Index + match.Length;
            }

            result.Append(input, offset, input.Length - offset);

            matchScope.CurrentMatch = matches[matches.Count - 1];
            return result;
        }

        [RubyMethod("sub")]
        public static object BlockReplaceFirst(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ pattern) {

            object blockResult;
            MutableString result;
            return BlockReplaceFirst(tosConversion, scope, self, block, pattern, out blockResult, out result) ? blockResult : (result ?? self.CloneDerived());
        }
        
        [RubyMethod("gsub")]
        public static object BlockReplaceAll(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, 
            [NotNull]RubyRegex pattern) {

            object blockResult;
            MutableString result;
            self.TrackChanges();
            object r = BlockReplaceAll(tosConversion, scope, self, block, pattern, out blockResult, out result) ? blockResult : (result ?? self.CloneDerived());

            RequireNoVersionChange(self);
            return r;
        }

        [RubyMethod("sub")]
        public static object BlockReplaceFirst(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, 
            [NotNull]MutableString matchString) {

            object blockResult;
            MutableString result;
            // TODO:
            var regex = new RubyRegex(MutableString.CreateMutable(Regex.Escape(matchString.ToString()), matchString.Encoding), RubyRegexOptions.NONE);

            return BlockReplaceFirst(tosConversion, scope, self, block, regex, out blockResult, out result) ? blockResult : (result ?? self.CloneDerived());
        }

        [RubyMethod("gsub")]
        public static object BlockReplaceAll(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, 
            [NotNull]MutableString matchString) {

            object blockResult;
            MutableString result;
            // TODO:
            var regex = new RubyRegex(MutableString.CreateMutable(Regex.Escape(matchString.ToString()), matchString.Encoding), RubyRegexOptions.NONE);

            self.TrackChanges();
            object r = BlockReplaceAll(tosConversion, scope, self, block, regex, out blockResult, out result) ? blockResult : (result ?? self.CloneDerived());
            RequireNoVersionChange(self);
            return r;
        }

        [RubyMethod("sub")]
        public static MutableString/*!*/ ReplaceFirst(ConversionStorage<MutableString>/*!*/ toS, BinaryOpStorage/*!*/ hashDefault,
            ConversionStorage<MutableString>/*!*/ toStr, RubyScope/*!*/ scope, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern, [NotNull]object/*!*/ replacement) {

            return ReplaceFirst(toS, hashDefault, scope, self, ToReplacement(toStr, replacement), pattern) ?? self.CloneDerived();
        }

        [RubyMethod("gsub")]
        public static MutableString/*!*/ ReplaceAll(ConversionStorage<MutableString>/*!*/ toS, BinaryOpStorage/*!*/ hashDefault,
            ConversionStorage<MutableString>/*!*/ toStr, RubyScope/*!*/ scope, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern, [NotNull]object/*!*/ replacement) {

            return ReplaceAll(toS, hashDefault, scope, self, ToReplacement(toStr, replacement), pattern) ?? self.CloneDerived();
        }

        #endregion


        #region sub!, gsub!

        private static object BlockReplaceInPlace(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, BlockParam/*!*/ block, MutableString/*!*/ self, 
            RubyRegex/*!*/ pattern, bool replaceAll) {

            object blockResult;

            self.RequireNotFrozen();
            self.TrackChanges();

            // prepare replacement in a builder:
            MutableString builder;
            if (replaceAll ?
                BlockReplaceAll(tosConversion, scope, self, block, pattern, out blockResult, out builder) :
                BlockReplaceFirst(tosConversion, scope, self, block, pattern, out blockResult, out builder)) {

                // block jumped:
                return blockResult;
            }

            // unsuccessful match:
            if (builder == null) {
                return null;
            }

            RequireNoVersionChange(self);

            // replace content of self with content of the builder:
            self.Replace(0, self.Length, builder);
            return self.TaintBy(builder);
        }

        private static MutableString ReplaceInPlace(ConversionStorage<MutableString> toS, BinaryOpStorage hashDefault, 
            RubyScope/*!*/ scope, MutableString/*!*/ self, RubyRegex/*!*/ pattern,
            Union<IDictionary<object, object>, MutableString>/*!*/ replacement, bool replaceAll) {
            
            self.RequireNotFrozen();
            
            MutableString builder = replaceAll ?
                ReplaceAll(toS, hashDefault, scope, self, replacement, pattern) :
                ReplaceFirst(toS, hashDefault, scope, self, replacement, pattern);
            
            // unsuccessful match:
            if (builder == null) {
                return null;
            }

            self.Replace(0, self.Length, builder);
            return self.TaintBy(builder);
        }

        [RubyMethod("sub!")]
        public static object BlockReplaceFirstInPlace(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern) {

            return BlockReplaceInPlace(tosConversion, scope, block, self, pattern, false);
        }

        [RubyMethod("gsub!")]
        public static object BlockReplaceAllInPlace(ConversionStorage<MutableString>/*!*/ tosConversion, 
            RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern) {

            return BlockReplaceInPlace(tosConversion, scope, block, self, pattern, true);
        }

        [RubyMethod("sub!")]
        public static MutableString ReplaceFirstInPlace(ConversionStorage<MutableString>/*!*/ toS, BinaryOpStorage/*!*/ hashDefault,
            ConversionStorage<MutableString>/*!*/ toStr, RubyScope/*!*/ scope, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern, [NotNull]object/*!*/ replacement) {

            return ReplaceInPlace(toS, hashDefault, scope, self, pattern, ToReplacement(toStr, replacement), false);
        }

        [RubyMethod("gsub!")]
        public static MutableString ReplaceAllInPlace(ConversionStorage<MutableString>/*!*/ toS, BinaryOpStorage/*!*/ hashDefault,
            ConversionStorage<MutableString>/*!*/ toStr, RubyScope/*!*/ scope, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern, [NotNull]object/*!*/ replacement) {

            return ReplaceInPlace(toS, hashDefault, scope, self, pattern, ToReplacement(toStr, replacement), true);
        }

        /// <summary>
        /// The replacement argument of #sub/#gsub is a Hash or a String, and the binder cannot
        /// be left to choose between the two: a Union parameter never reaches #to_str, and two
        /// separate overloads make a Hash fail the String conversion before the Hash one is
        /// tried. So it arrives as an object and is sorted out here.
        /// </summary>
        private static Union<IDictionary<object, object>, MutableString> ToReplacement(
            ConversionStorage<MutableString>/*!*/ toStr, object/*!*/ replacement) {
            var hash = replacement as IDictionary<object, object>;
            if (hash != null) {
                return new Union<IDictionary<object, object>, MutableString>(hash, null);
            }
            return new Union<IDictionary<object, object>, MutableString>(null, Protocols.CastToString(toStr, replacement));
        }

        // Ruby 1.9: with neither a replacement nor a block, #sub/#gsub and their in-place
        // forms answer an Enumerator that yields the matched substrings.
        [RubyMethod("gsub")]
        public static object ReplaceAll(ConversionStorage<MutableString>/*!*/ tosConversion, RubyScope/*!*/ scope,
            MutableString/*!*/ self, [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern) {
            return new Enumerator((_, block) => BlockReplaceAll(tosConversion, scope, block, self, pattern));
        }

        [RubyMethod("gsub!")]
        public static object ReplaceAllInPlace(ConversionStorage<MutableString>/*!*/ tosConversion, RubyScope/*!*/ scope,
            MutableString/*!*/ self, [DefaultProtocol, NotNull]RubyRegex/*!*/ pattern) {
            return new Enumerator((_, block) => BlockReplaceInPlace(tosConversion, scope, block, self, pattern, true));
        }

        #endregion


        #region index, rindex

        // encoding aware
        [RubyMethod("index")]
        public static object Index(MutableString/*!*/ self, 
            [DefaultProtocol, NotNull]MutableString/*!*/ substring, [DefaultProtocol, Optional]int start) {

            self.PrepareForCharacterRead();
            if (!NormalizeStart(self.GetCharCount(), ref start)) {
                return null;
            }

            self.RequireCompatibleEncoding(substring);
            substring.PrepareForCharacterRead();

            int result = self.IndexOf(substring, start);
            return (result != -1) ? ScriptingRuntimeHelpers.Int32ToObject(result) : null;
        }

        // encoding aware
        [RubyMethod("index")]
        public static object Index(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol, Optional]int start) {

            // A Regexp has to interpret characters, so unlike #index with a String this one does
            // refuse a receiver whose bytes are not valid in its encoding, the way MRI does.
            RequireValidEncoding(self);

            MatchData match = regex.Match(self, start, true);
            scope.GetInnerMostClosureScope().CurrentMatch = match;
            return (match != null) ? ScriptingRuntimeHelpers.Int32ToObject(match.Index) : null;
        }
        
        // encoding aware
        [RubyMethod("rindex")]
        public static object LastIndexOf(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ substring) {
            if (substring.IsEmpty) {
                self.PrepareForCharacterRead();
                return ScriptingRuntimeHelpers.Int32ToObject(self.GetCharCount());
            }
            return LastIndexOf(self, substring, -1);
        }

        // encoding aware
        [RubyMethod("rindex")]
        public static object LastIndexOf(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ substring, [DefaultProtocol]int start) {

            self.PrepareForCharacterRead();
            int charCount = self.GetCharCount();

            start = IListOps.NormalizeIndex(charCount, start);
            if (start < 0) {
                return null;
            }

            if (substring.IsEmpty) {
                return ScriptingRuntimeHelpers.Int32ToObject((start >= charCount) ? charCount : start);
            }

            self.RequireCompatibleEncoding(substring);
            substring.PrepareForCharacterRead();
            int subCharCount = substring.GetCharCount();

            // Nothing can match a substring longer than the receiver. Without this the clamp below
            // turns "".rindex("l", 0) into LastIndexOf(.., -1), which is a CLR ArgumentException.
            if (subCharCount > charCount) {
                return null;
            }

            // LastIndexOf has CLR semantics: no characters of the substring are matched beyond start position.
            // Hence we need to increase start by the length of the substring - 1.
            if (start > charCount - subCharCount) {
                start = charCount - 1;
            } else {
                start += subCharCount - 1;
            }

            int result = self.LastIndexOf(substring, start);
            return (result != -1) ? ScriptingRuntimeHelpers.Int32ToObject(result) : null;
        }

        // encoding aware
        [RubyMethod("rindex")]
        public static object LastIndexOf(RubyScope/*!*/ scope, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol, DefaultParameterValue(Int32.MaxValue)]int start) {

            MatchData match = regex.LastMatch(self, start);
            scope.GetInnerMostClosureScope().CurrentMatch = match;
            return (match != null) ? ScriptingRuntimeHelpers.Int32ToObject(match.Index) : null;
        }

        // Start in range ==> search range from the first character towards the end.
        //
        // [-length, 0)     ==> [0, length + start]
        // start < -length  ==> false
        // [0, length)      ==> [start, length)
        // start > length   ==> false
        //
        private static bool NormalizeStart(int length, ref int start) {
            start = IListOps.NormalizeIndex(length, start);
            if (start < 0 || start > length) {
                return false;
            }
            return true;
        }

        // Ruby 1.9 takes any number of prefixes and answers true as soon as one matches,
        // which is why the arguments are converted one at a time: MRI never asks the ones
        // it did not need for #to_str. A Regexp prefix (Ruby 2.5) has to match at position 0.
        [RubyMethod("start_with?")]
        public static bool StartsWith(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope,
            MutableString/*!*/ self, [NotNull]params object/*!*/[]/*!*/ prefixes) {

            for (int i = 0; i < prefixes.Length; i++) {
                RubyRegex regex = prefixes[i] as RubyRegex;
                if (regex != null) {
                    MatchData match = RegexpOps.Match(scope, regex, self);
                    if (match != null && match.Index == 0) {
                        return true;
                    }
                    continue;
                }

                if (StartsWith(self, Protocols.CastToString(stringCast, prefixes[i]))) {
                    return true;
                }
            }
            return false;
        }

        private static bool StartsWith(MutableString/*!*/ self, MutableString/*!*/ subString) {
            int prefix = subString.GetByteCount();
            if (self.GetByteCount() < prefix) {
                return false;
            }
            for (int i = 0; i < prefix; i++) {
                if (self.GetByte(i) != subString.GetByte(i)) {
                    return false;
                }
            }

            // The bytes match, but MRI also insists that they end on a character boundary:
            // "\xC3\xA9" does not start with "\xC3" even though its first byte is one.
            return EndsOnCharacterBoundary(self, prefix);
        }

        /// <summary>
        /// True when <paramref name="byteOffset"/> is the start of a character of
        /// <paramref name="self"/> (or its very end).
        /// </summary>
        private static bool EndsOnCharacterBoundary(MutableString/*!*/ self, int byteOffset) {
            if (byteOffset == 0) {
                return true;
            }
            int at = 0;
            var characters = self.GetCharacters();
            while (characters.MoveNext()) {
                at += characters.Current.ToMutableString(self.Encoding).GetByteCount();
                if (at >= byteOffset) {
                    return at == byteOffset;
                }
            }
            return at == byteOffset;
        }

        [RubyMethod("end_with?")]
        public static bool EndsWith(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope,
            MutableString/*!*/ self, [NotNull]params object/*!*/[]/*!*/ suffixes) {

            for (int i = 0; i < suffixes.Length; i++) {
                MutableString suffix = Protocols.CastToString(stringCast, suffixes[i]);
                self.RequireCompatibleEncoding(suffix);
                if (EndsWith(self, suffix)) {
                    return true;
                }
            }
            return false;
        }

        private static bool EndsWith(MutableString/*!*/ self, MutableString/*!*/ subString) {
            int suffix = subString.GetByteCount();
            if (self.GetByteCount() < suffix) {
                return false;
            }

            // Comparing the strings rather than converting the argument to a CLR string keeps
            // this working for a string holding bytes that are invalid in its encoding, which
            // MRI answers from the trailing bytes.
            if (!self.EndsWith(subString)) {
                return false;
            }

            // ... and the suffix has to begin on a character boundary: "\xC3\xA9" does not end
            // with "\xA9".
            return EndsOnCharacterBoundary(self, self.GetByteCount() - suffix);
        }

        #endregion


        #region delete, delete!, clear

        private static MutableString/*!*/ InternalDelete(MutableString/*!*/ self, MutableString[]/*!*/ ranges) {
            var map = CharacterSelectorSet.Parse(self, ranges);
            self.PrepareForCharacterRead();
            MutableString result = self.CreateDerived().TaintBy(self);
            for (int i = 0; i < self.Length; i++) {
                if (!map.Contains(self.GetChar(i))) {
                    result.Append(self.GetChar(i));
                }
            }
            return result;
        }

        private static MutableString/*!*/ InternalDeleteInPlace(MutableString/*!*/ self, MutableString[]/*!*/ ranges) {
            MutableString result = InternalDelete(self, ranges);
            if (self.Equals(result)) {
                return null;
            }

            self.Clear();
            self.Append(result);
            return self;
        }

        /// <summary>
        /// Most operations get along fine with bytes that are not valid in the string's encoding -
        /// see EscapingEncoding - but the ones that have to interpret characters do not, and MRI
        /// refuses those up front rather than producing nonsense.
        /// </summary>
        private static void RequireValidEncoding(MutableString/*!*/ self) {
            if (self.ContainsInvalidCharacters()) {
                throw RubyExceptions.CreateArgumentError("invalid byte sequence in {0}", self.Encoding.Name);
            }
        }

        [RubyMethod("delete")]
        public static MutableString/*!*/ Delete(MutableString/*!*/ self, 
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ strs) {
            if (strs.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments");
            }
            RequireValidEncoding(self);
            return InternalDelete(self, strs);
        }

        [RubyMethod("delete!")]
        public static MutableString/*!*/ DeleteInPlace(MutableString/*!*/ self,
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ strs) {
            self.RequireNotFrozen();

            if (strs.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments");
            }
            return InternalDeleteInPlace(self, strs);
        }

        [RubyMethod("clear")]
        public static MutableString/*!*/ Clear(MutableString/*!*/ self) {
            return self.Clear();
        }

        #endregion

        #region count
        
        private static object InternalCount(MutableString/*!*/ self, MutableString[]/*!*/ ranges) {
            var map = CharacterSelectorSet.Parse(self, ranges);
            self.PrepareForCharacterRead();
            int count = 0;
            for (int i = 0; i < self.Length; i++) {
                if (map.Contains(self.GetChar(i))) {
                    count++;
                }
            }
            return ScriptingRuntimeHelpers.Int32ToObject(count);
        }

        [RubyMethod("count")]
        public static object Count(RubyContext/*!*/ context, MutableString/*!*/ self, 
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ strs) {
            if (strs.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments");
            }
            return InternalCount(self, strs);
        }

        #endregion

        #region include?

        [RubyMethod("include?")]
        public static bool Include(MutableString/*!*/ str, [DefaultProtocol, NotNull]MutableString/*!*/ subString) {
            str.RequireCompatibleEncoding(subString);

            // MRI looks for the byte sequence, so it answers this even for a string holding
            // bytes that are invalid in its encoding. Reading such a string as characters -
            // which is what the path below does - raises instead, and that is not a theoretical
            // concern: mspec asks `description.include?(pattern)` about every example it runs,
            // so one spec description with a stray byte in it used to take down the whole run
            // before any tally was printed.
            if (subString.ContainsInvalidCharacters()) {
                // rb_str_index gives up on a broken needle rather than searching for it.
                return false;
            }
            if (str.ContainsInvalidCharacters()) {
                return str.IndexOf(subString.ToByteArray()) != -1;
            }

            str.PrepareForCharacterRead();
            subString.PrepareForCharacterRead();
            return str.IndexOf(subString) != -1;
        }

        [RubyMethod("include?")]
        public static bool Include(MutableString/*!*/ str, int c) {
            return str.IndexOf((byte)(c % 256)) != -1;
        }

        #endregion


        #region insert

        [RubyMethod("insert")]
        public static MutableString Insert(MutableString/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol, NotNull]MutableString/*!*/ value) {
            return self.Insert(NormalizeInsertIndex(start, self.GetLength()), value).TaintBy(value);
        }

        #endregion


        #region =~, match

        [RubyMethod("=~")]
        public static object Match(RubyScope/*!*/ scope, MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            return RegexpOps.MatchIndex(scope, regex, self);
        }

        [RubyMethod("=~")]
        public static object Match(MutableString/*!*/ self, [NotNull]MutableString/*!*/ str) {
            throw RubyExceptions.CreateTypeError("type mismatch: String given");
        }

        [RubyMethod("=~")]
        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, MutableString/*!*/ self, object obj) {
            var site = storage.GetCallSite("=~", new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf));
            return site.Target(site, scope, obj, self);
        }

        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            var site = storage.GetCallSite("match", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasScope));
            return site.Target(site, scope, regex, self);
        }

        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ pattern) {
            var site = storage.GetCallSite("match", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasScope));
            return site.Target(site, scope, new RubyRegex(pattern, RubyRegexOptions.NONE), self);
        }

        // Ruby 1.9 gave #match a block that receives the MatchData, and an optional start
        // offset. Without an offset the call still goes through Regexp#match dynamically, so
        // a Regexp subclass that overrides it is honoured; with one it goes straight to the
        // implementation, which already knows about offsets. The pattern must be a Regexp or
        // a String - unlike most String arguments a Symbol is not accepted.
        [RubyMethod("match")]
        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, [Optional]BlockParam block,
            MutableString/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            return YieldMatch(block, Match(storage, scope, self, regex));
        }

        [RubyMethod("match")]
        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, [Optional]BlockParam block,
            MutableString/*!*/ self, [NotNull]MutableString/*!*/ pattern) {
            return YieldMatch(block, Match(storage, scope, self, pattern));
        }

        [RubyMethod("match")]
        public static object Match(RubyScope/*!*/ scope, [Optional]BlockParam block, MutableString/*!*/ self,
            [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol]int start) {
            return RegexpOps.Match(scope, block, regex, self, start);
        }

        [RubyMethod("match")]
        public static object Match(RubyScope/*!*/ scope, [Optional]BlockParam block, MutableString/*!*/ self,
            [NotNull]MutableString/*!*/ pattern, [DefaultProtocol]int start) {
            return RegexpOps.Match(scope, block, new RubyRegex(pattern, RubyRegexOptions.NONE), self, start);
        }

        [RubyMethod("match")]
        public static object Match(ConversionStorage<MutableString>/*!*/ stringTryCast, RubyScope/*!*/ scope,
            [Optional]BlockParam block, MutableString/*!*/ self, object pattern, [DefaultProtocol, Optional]int start) {
            // A Symbol converts to a String elsewhere in IronRuby, but MRI's #match takes only
            // a Regexp or a String.
            MutableString converted = (pattern is RubySymbol) ? null : Protocols.TryCastToString(stringTryCast, pattern);
            if (converted == null) {
                throw RubyExceptions.CreateUnexpectedTypeError(scope.RubyContext, pattern, "Regexp");
            }
            return RegexpOps.Match(scope, block, new RubyRegex(converted, RubyRegexOptions.NONE), self, start);
        }

        private static object YieldMatch(BlockParam block, object match) {
            if (block == null || match == null) {
                return match;
            }
            object blockResult;
            block.Yield(match, out blockResult);
            return blockResult;
        }
       
        #endregion


        #region scan

        [RubyMethod("scan")]
        public static RubyArray/*!*/ Scan(RubyScope/*!*/ scope, MutableString/*!*/ self, [DefaultProtocol, NotNull]RubyRegex/*!*/ regex) {
            IList<MatchData> matches = regex.Matches(self, false);

            var matchScope = scope.GetInnerMostClosureScope();
            
            RubyArray result = new RubyArray(matches.Count);
            if (matches.Count == 0) {
                matchScope.CurrentMatch = null;
                return result;
            } 

            foreach (MatchData match in matches) {
                result.Add(MatchToScanResult(scope, self, regex, match));
            }

            matchScope.CurrentMatch = matches[matches.Count - 1];
            return result;
        }

        [RubyMethod("scan")]
        public static object/*!*/ Scan(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, [DefaultProtocol, NotNull]RubyRegex regex) {
            var matchScope = scope.GetInnerMostClosureScope();

            IList<MatchData> matches = regex.Matches(self);
            if (matches.Count == 0) {
                matchScope.CurrentMatch = null;
                return self;
            } 

            foreach (MatchData match in matches) {
                matchScope.CurrentMatch = match;

                object blockResult;
                if (block.Yield(MatchToScanResult(scope, self, regex, match), out blockResult)) {
                    return blockResult;
                }

                // resets the $~ scope variable to the last match (skipped if block jumped):
                matchScope.CurrentMatch = match;
            }
            return self;
        }

        private static object MatchToScanResult(RubyScope/*!*/ scope, MutableString/*!*/ self, RubyRegex/*!*/ regex, MatchData/*!*/ match) {
            if (match.GroupCount == 1) {
                return match.GetValue().TaintBy(regex, scope);
            } else {
                var result = new RubyArray(match.GroupCount - 1);
                for (int i = 1; i < match.GroupCount; i++) {
                    MutableString value = match.GetGroupValue(i);
                    result.Add(value != null ? value.TaintBy(regex, scope) : value);
                }
                return result;
            }
        }

        #endregion


        #region succ, succ!

        public static int GetIndexOfRightmostAlphaNumericCharacter(MutableString/*!*/ str, int index) {
            for (int i = index; i >= 0; --i)
                if (Char.IsLetterOrDigit(str.GetChar(i)))
                    return i;

            return -1;
        }

        // TODO: remove recursion
        public static void IncrementAlphaNumericChar(MutableString/*!*/ str, int index) {
            char c = str.GetChar(index);
            if (c == 'z' || c == 'Z' || c == '9') {
                int nextIndex = GetIndexOfRightmostAlphaNumericCharacter(str, index - 1);
                if (c == 'z') {
                    str.SetChar(index, 'a');
                    if (nextIndex == -1) {
                        str.Insert(index, 'a');
                    } else {
                        IncrementAlphaNumericChar(str, nextIndex);
                    }
                } else if (c == 'Z') {
                    str.SetChar(index, 'A');
                    if (nextIndex == -1) {
                        str.Insert(index, 'A');
                    } else {
                        IncrementAlphaNumericChar(str, nextIndex);
                    }
                } else {
                    str.SetChar(index, '0');
                    if (nextIndex == -1) {
                        str.Insert(index, '1');
                    } else {
                        IncrementAlphaNumericChar(str, nextIndex);
                    }
                }
            } else {
                IncrementCharacterOrByte(str, index);
            }
        }

        /// <summary>
        /// MRI's rb_str_succ is encoding-aware: in a character encoding the code point of
        /// the character goes up by one, and only a binary string steps its bytes. Always
        /// stepping bytes turned "\u0999".succ into a broken character instead of
        /// "\u099a", and made a range of non-ASCII characters walk off the end of the
        /// string's byte array.
        /// </summary>
        private static void IncrementCharacterOrByte(MutableString/*!*/ str, int index) {
            if (str.Encoding != RubyEncoding.Binary) {
                char c = str.GetChar(index);
                if (c < Char.MaxValue) {
                    str.SetChar(index, (char)(c + 1));
                    return;
                }
            }
            IncrementChar(str, index);
        }

        public static void IncrementChar(MutableString/*!*/ str, int index) {
            byte c = str.GetByte(index);
            if (c == 255) {
                str.SetByte(index, 0);
                if (index > 0) {
                    IncrementChar(str, index - 1);
                } else {
                    str.Insert(0, 1);
                }
            } else {
                str.SetByte(index, unchecked((byte)(c + 1)));
            }
        }

        [RubyMethod("succ!")]
        [RubyMethod("next!")]
        public static MutableString/*!*/ SuccInPlace(MutableString/*!*/ self) {
            self.RequireNotFrozen();

            if (self.IsEmpty) {
                return self;
            }

            // Length counts bytes once the string has been read as binary - which anything
            // that asks for its byte count makes it do - while the increment below indexes
            // characters, so a multi-byte string walked off the end of itself.
            int last = self.GetCharCount() - 1;
            int index = GetIndexOfRightmostAlphaNumericCharacter(self, last);
            if (index == -1) {
                IncrementCharacterOrByte(self, last);
            } else {
                IncrementAlphaNumericChar(self, index);
            }

            return self;
        }

        [RubyMethod("succ")]
        [RubyMethod("next")]
        public static MutableString/*!*/ Succ(MutableString/*!*/ self) {
            return SuccInPlace(self.CloneDerived());
        }

        #endregion


        #region split

        private static RubyArray/*!*/ MakeRubyArray(MutableString/*!*/ self, MutableString[]/*!*/ elements) {
            return MakeRubyArray(self, elements, 0, elements.Length);
        }

        private static RubyArray/*!*/ MakeRubyArray(MutableString/*!*/ self, MutableString[]/*!*/ elements, int start, int count) {
            RubyArray result = new RubyArray(elements.Length);
            for (int i = 0; i < count; i++) {
                result.Add(self.CreateDerived().Append(elements[start + i]).TaintBy(self));
            }
            return result;
        }
        
        private static char[] _WhiteSpaceSeparators = new char[] { ' ', '\n', '\r', '\t', '\v', '\f' };

        private static bool IsAwkWhiteSpace(char c) {
            return c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\v' || c == '\f';
        }

        /// <summary>
        /// "awk" split: the separator is a single space or absent. This follows MRI's
        /// rb_str_split_m loop, which is not simply "split on runs of whitespace and drop
        /// the empties" once a positive limit is involved: the limit counts fields, the
        /// remainder of the string becomes the last field verbatim, and an empty trailing
        /// field survives unless no limit was given at all.
        /// </summary>
        private static RubyArray/*!*/ WhitespaceSplit(MutableString/*!*/ str, int limit) {
            str.PrepareForCharacterRead();

            RubyArray result = new RubyArray();
            int length = str.GetCharCount();
            int begin = 0, end = 0, position = 0, fields = 1;
            bool skipping = true;

            while (position < length) {
                char c = str.GetChar(position);
                position++;

                if (skipping) {
                    if (IsAwkWhiteSpace(c)) {
                        begin = position;
                    } else {
                        end = position;
                        skipping = false;
                        if (limit > 0 && limit <= fields) {
                            break;
                        }
                    }
                } else if (IsAwkWhiteSpace(c)) {
                    result.Add(str.CreateDerived().Append(str, begin, end - begin).TaintBy(str));
                    skipping = true;
                    begin = position;
                    if (limit > 0) {
                        fields++;
                    }
                } else {
                    end = position;
                }
            }

            if (length > 0 && (limit != 0 || length > begin)) {
                result.Add(str.CreateDerived().Append(str, begin, length - begin).TaintBy(str));
            }

            if (limit == 0) {
                RemoveTrailingEmptyItems(result);
            }

            return result;
        }

        private static RubyArray/*!*/ InternalSplit(MutableString/*!*/ str, MutableString separator, int limit) {
            RubyArray result;
            if (limit == 1) {
                // one field: the whole string, but as a fresh String
                result = new RubyArray(1);
                result.Add(str.CreateDerived().Append(str).TaintBy(str));
                return result;
            }

            if (separator == null || separator.StartsWith(' ') && separator.GetLength() == 1) {
                return WhitespaceSplit(str, limit);
            }

            if (separator.IsEmpty) {
                return CharacterSplit(str, limit);
            }

            if (limit <= 0) {
                result = new RubyArray();
            } else {
                result = new RubyArray(limit + 1);
            }

            // TODO: invalid characters, k-coding?
            str.PrepareForCharacterRead();
            separator.PrepareForCharacterRead();
            str.RequireCompatibleEncoding(separator);

            int separatorLength = separator.GetCharCount();
            int i = 0;
            int next;
            while ((limit <= 0 || result.Count < limit - 1) && (next = str.IndexOf(separator, i)) != -1) {
                result.Add(str.CreateDerived().Append(str, i, next - i).TaintBy(str));
                i = next + separatorLength;
            }

            result.Add(str.CreateDerived().Append(str, i).TaintBy(str));

            if (limit == 0) {
                RemoveTrailingEmptyItems(result);
            }

            return result;
        }

        private static void RemoveTrailingEmptyItems(RubyArray/*!*/ array) {
            while (array.Count != 0 && ((MutableString)array[array.Count - 1]).IsEmpty) {
                array.RemoveAt(array.Count - 1);
            }
        }

        private static RubyArray/*!*/ CharacterSplit(MutableString/*!*/ str, int limit) {
            RubyArray result = new RubyArray();
            
            var charEnum = str.GetCharacters();
            int i = 0;
            while (limit <= 0 || result.Count < limit - 1) {
                if (!charEnum.MoveNext()) {
                    break;
                }

                result.Add(str.CreateDerived().Append(charEnum.Current).TaintBy(str));
                i++;
            }

            if (charEnum.HasMore || limit != 0) {
                result.Add(str.CreateDerived().AppendRemaining(charEnum).TaintBy(str));
            }
            
            return result;
        }

        public static RubyArray/*!*/ Split(ConversionStorage<MutableString>/*!*/ stringCast, MutableString/*!*/ self) {
            return Split(stringCast, self, (MutableString)null, 0);
        }

        public static RubyArray/*!*/ Split(ConversionStorage<MutableString>/*!*/ stringCast, MutableString/*!*/ self, 
            [DefaultProtocol]MutableString separator, [DefaultProtocol, Optional]int limit) {

            RequireValidEncoding(self);
            if (separator != null) {
                RequireValidEncoding(separator);
            }

            if (separator == null) {
                object defaultSeparator = stringCast.Context.StringSeparator;
                RubyRegex regexSeparator = defaultSeparator as RubyRegex;
                if (regexSeparator != null) {
                    return Split(stringCast, self, regexSeparator, limit);
                }
                
                if (defaultSeparator != null) {
                    separator = Protocols.CastToString(stringCast, defaultSeparator);
                }
            }

            if (self.IsEmpty) {
                // If self is "", the result is always []. This is special cased because the code will
                // return [""].
                return new RubyArray();
            }

            return InternalSplit(self, separator, limit);            
        }

        public static RubyArray/*!*/ Split(ConversionStorage<MutableString>/*!*/ stringCast, MutableString/*!*/ self, 
            [NotNull]RubyRegex/*!*/ regexp, [DefaultProtocol, Optional]int limit) {

            RequireValidEncoding(self);

            if (regexp.IsEmpty) {
                return InternalSplit(self, MutableString.FrozenEmpty, limit);
            }

            if (self.IsEmpty) {
                // If self is "", the result is always []. This is special cased because the code will
                // return [""].
                return new RubyArray();
            }

            if (limit == 0) {
                // suppress trailing empty fields
                RubyArray array = MakeRubyArray(self, regexp.Split(self));
                while (array.Count != 0 && ((MutableString)array[array.Count - 1]).Length == 0) {
                    array.RemoveAt(array.Count - 1);
                }
                return array;
            } else if (limit == 1) {
                // one field: the whole string, but as a fresh String
                RubyArray result = new RubyArray(1);
                result.Add(self.CreateDerived().Append(self).TaintBy(self));
                return result;
            } else if (limit < 0) {
                // does not suppress trailing fields when negative 
                return MakeRubyArray(self, regexp.Split(self));
            } else {
                // limit > 1 limits to N fields
                return MakeRubyArray(self, regexp.Split(self, limit));
            }
        }

        // Ruby 2.6: #split yields each field to a block and answers self instead of
        // building the array. The three splitting overloads above stay the plain
        // array-producing versions and are what the CLR String extension calls.
        [RubyMethod("split")]
        public static object Split(ConversionStorage<MutableString>/*!*/ stringCast, BlockParam block, MutableString/*!*/ self) {
            return YieldSplit(block, self, Split(stringCast, self));
        }

        [RubyMethod("split")]
        public static object Split(ConversionStorage<MutableString>/*!*/ stringCast, BlockParam block, MutableString/*!*/ self,
            [DefaultProtocol]MutableString separator, [DefaultProtocol, Optional]int limit) {
            return YieldSplit(block, self, Split(stringCast, self, separator, limit));
        }

        [RubyMethod("split")]
        public static object Split(ConversionStorage<MutableString>/*!*/ stringCast, BlockParam block, MutableString/*!*/ self,
            [NotNull]RubyRegex/*!*/ regexp, [DefaultProtocol, Optional]int limit) {
            return YieldSplit(block, self, Split(stringCast, self, regexp, limit));
        }

        private static object YieldSplit(BlockParam block, MutableString/*!*/ self, RubyArray/*!*/ fields) {
            if (block == null) {
                return fields;
            }

            foreach (object field in fields) {
                object blockResult;
                if (block.Yield(field, out blockResult)) {
                    return blockResult;
                }
            }

            return self;
        }

        #endregion


        #region strip, strip!, lstrip, lstrip!, rstrip, rstrip!

        [RubyMethod("strip")]
        public static MutableString/*!*/ Strip(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return Strip(self, true, true);
        }

        [RubyMethod("lstrip")]
        public static MutableString/*!*/ StripLeft(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return Strip(self, true, false);
        }

        [RubyMethod("rstrip")]
        public static MutableString/*!*/ StripRight(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return Strip(self, false, true);
        }

        [RubyMethod("strip!")]
        public static MutableString StripInPlace(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return StripInPlace(self, true, true);
        }

        [RubyMethod("lstrip!")]
        public static MutableString StripLeftInPlace(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return StripInPlace(self, true, false);
        }

        [RubyMethod("rstrip!")]
        public static MutableString StripRightInPlace(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return StripInPlace(self, false, true);
        }
        
        private static MutableString/*!*/ Strip(MutableString/*!*/ str, bool trimLeft, bool trimRight) {
            RequireStrippable(str, trimLeft);
            int left, right;
            GetTrimRange(str, trimLeft, trimRight, out left, out right);
            return str.GetSlice(left, right - left).TaintBy(str);
        }

        public static MutableString StripInPlace(MutableString/*!*/ self, bool trimLeft, bool trimRight) {
            // MRI checks frozen-ness before it works out whether there is anything to trim.
            self.RequireNotFrozen();
            RequireStrippable(self, trimLeft);

            int left, right;
            GetTrimRange(self, trimLeft, trimRight, out left, out right);
            int remaining = right - left;

            // nothing to trim:
            if (remaining == self.Length) {
                return null;
            }

            if (remaining == 0) {
                // all whitespace
                self.Clear();
            } else {
                self.Trim(left, remaining);
            }
            return self;
        }

        /// <summary>
        /// Stripping has to read characters, so a broken byte sequence stops it. MRI reports
        /// that as ArgumentError from the left-hand scan and Encoding::CompatibilityError from
        /// the right-hand one - #lstrip and #rstrip really do raise different classes.
        /// </summary>
        private static void RequireStrippable(MutableString/*!*/ str, bool trimLeft) {
            if (!str.ContainsInvalidCharacters()) {
                return;
            }
            if (trimLeft) {
                throw RubyExceptions.CreateArgumentError("invalid byte sequence in {0}", str.Encoding.Name);
            }
            throw new EncodingCompatibilityError(
                String.Format("invalid byte sequence in {0}", str.Encoding.Name)
            );
        }

        // MRI strips ASCII whitespace and NUL, on both sides; it does not strip the
        // non-ASCII characters that Char.IsWhiteSpace also reports (U+00A0 and friends).
        private static bool IsStrippedCharacter(char c) {
            return c == ' ' || (c >= '\t' && c <= '\r') || c == '\0';
        }

        private static void GetTrimRange(MutableString/*!*/ str, bool left, bool right, out int leftIndex, out int rightIndex) {
            GetTrimRange(
                str.Length,
                !left ? (Func<int, bool>)null : (i) => IsStrippedCharacter(str.GetChar(i)),
                !right ? (Func<int, bool>)null : (i) => IsStrippedCharacter(str.GetChar(i)),
                out leftIndex, 
                out rightIndex
            );
        }

        // Returns indices of the first non-whitespace character (from left and from right).
        // ensures (leftIndex == rightIndex) ==> all characters are whitespace
        // leftIndex == 0, rightIndex == length if there is no whitespace to be trimmed.
        internal static void GetTrimRange(int length, Func<int, bool> trimLeft, Func<int, bool> trimRight, out int leftIndex, out int rightIndex) {
            int i;
            if (trimLeft != null) {
                i = 0;
                while (i < length) {
                    if (!trimLeft(i)) {
                        break;
                    }
                    i++;
                }
            } else {
                i = 0;
            }

            int j;
            if (trimRight != null) {
                j = length - 1;
                // we need to compare i-th character again as it could be treated as right whitespace but not as left whitespace:
                while (j >= i) {
                    if (!trimRight(j)) {
                        break;
                    }
                    j--;
                }

                // should point right after the non-whitespace character:
                j++;
            } else {
                j = length;
            }

            leftIndex = i;
            rightIndex = j;

            Debug.Assert(leftIndex >= 0 && leftIndex <= length);
            Debug.Assert(rightIndex >= 0 && rightIndex <= length);
        }

        #endregion


        #region squeeze, squeeze!

        [RubyMethod("squeeze")]
        public static MutableString/*!*/ Squeeze(RubyContext/*!*/ context, MutableString/*!*/ self, 
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ args) {
            MutableString result = self.CloneDerived();
            SqueezeMutableString(result, args);
            return result;
        }

        [RubyMethod("squeeze!")]
        public static MutableString/*!*/ SqueezeInPlace(RubyContext/*!*/ context, MutableString/*!*/ self,
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ args) {
            return SqueezeMutableString(self, args);
        }

        private static MutableString SqueezeMutableString(MutableString/*!*/ str, MutableString[]/*!*/ ranges) {
            // if squeezeAll is true then there should be no ranges, and vice versa
            Assert.NotNull(str, ranges);

            // convert the args into a map of characters to be squeezed (same algorithm as count)
            CharacterSelectorSet map = null;
            if (ranges.Length > 0) {
                map = CharacterSelectorSet.Parse(str, ranges);
            }
            str.PrepareForCharacterRead();

            // Do the squeeze in place
            int j = 1, k = 1;
            while (j < str.Length) {
                if (str.GetChar(j) == str.GetChar(j-1) && (ranges.Length == 0 || map.Contains(str.GetChar(j)))) {
                    j++;
                } else {
                    str.SetChar(k, str.GetChar(j));
                    j++; k++;
                }
            }
            if (j > k) {
                str.Remove(k, j - k);
            }

            // if not modified return null
            return j == k ? null : str;
        }

        #endregion


        #region to_i, hex, oct

        [RubyMethod("to_i")]
        public static object/*!*/ ToInteger(MutableString/*!*/ self, [DefaultProtocol, DefaultParameterValue(10)]int @base) {
            return ClrString.ToInteger(self.ConvertToString(), @base);
        }

        [RubyMethod("hex")]
        public static object/*!*/ ToIntegerHex(MutableString/*!*/ self) {
            return ClrString.ToIntegerHex(self.ConvertToString());
        }

        [RubyMethod("oct")]
        public static object/*!*/ ToIntegerOctal(MutableString/*!*/ self) {
            return ClrString.ToIntegerOctal(self.ConvertToString());
        }

        #endregion

        #region to_f, to_s, to_str, to_clr_string, to_sym, intern

        [RubyMethod("to_f")]
        public static double ToDouble(MutableString/*!*/ self) {
            if (!self.Encoding.IsAsciiIdentity) {
                throw new EncodingCompatibilityError(
                    String.Format("ASCII incompatible encoding: {0}", self.Encoding.Name)
                );
            }
            return ClrString.ToDouble(self.ConvertToString());
        }

        private static readonly System.Text.RegularExpressions.Regex/*!*/ _rationalPattern =
            new System.Text.RegularExpressions.Regex(@"\A\s*(?<sign>[+-]?)(?<int>[\d_]*)(\.(?<frac>[\d_]+))?(\s*/\s*(?<den>[\d_]+))?",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// String#to_r: parses a leading rational literal ("3", "0.3", "1/3", "-1.5/2") and
        /// returns (0/1) when nothing parses. Trailing garbage is ignored.
        /// </summary>
        [RubyMethod("to_r")]
        public static object ToRational(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, MutableString/*!*/ self) {

            var match = _rationalPattern.Match(self.ConvertToString());

            BigInteger numerator = BigInteger.Zero;
            BigInteger denominator = BigInteger.One;

            if (match.Success) {
                string digits = match.Groups["int"].Value.Replace("_", "");
                string fraction = match.Groups["frac"].Success ? match.Groups["frac"].Value.Replace("_", "") : "";

                if (digits.Length != 0 || fraction.Length != 0) {
                    numerator = BigInteger.Parse((digits.Length != 0 ? digits : "0") + fraction, System.Globalization.CultureInfo.InvariantCulture);
                    denominator = BigInteger.Pow(new BigInteger(10), fraction.Length);

                    if (match.Groups["den"].Success) {
                        string den = match.Groups["den"].Value.Replace("_", "");
                        BigInteger d = BigInteger.Parse(den, System.Globalization.CultureInfo.InvariantCulture);
                        if (d.IsZero) {
                            throw new DivideByZeroException("divided by 0");
                        }
                        denominator *= d;
                    }

                    if (match.Groups["sign"].Value == "-") {
                        numerator = -numerator;
                    }
                }
            }

            return RubyTimeOps.MakeRational(toRational, scope, ExactNum.Make(numerator, denominator));
        }

        [RubyMethod("to_s")]
        [RubyMethod("to_str")]
        public static MutableString/*!*/ ToS(MutableString/*!*/ self) {
            return self.GetType() == typeof(MutableString) ? self : MutableString.Create(self).TaintBy(self);
        }

        [RubyMethod("to_clr_string")]
        public static string/*!*/ ToClrString(MutableString/*!*/ str) {
            return str.ConvertToString();
        }

        
        [RubyMethod("to_sym")]
        [RubyMethod("intern")]
        public static RubySymbol/*!*/ ToSymbol(RubyContext/*!*/ context, MutableString/*!*/ self) {
            return context.CreateSymbol(self);
        }

        #endregion


        #region upto

        [RubyMethod("upto")]
        public static Enumerator/*!*/ UpTo(RangeOps.EachStorage/*!*/ storage, MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ endString) {
            return new Enumerator((_, block) => UpTo(storage, block, self, endString));
        }

        [RubyMethod("upto")]
        public static object UpTo(RangeOps.EachStorage/*!*/ storage, [NotNull]BlockParam/*!*/ block, MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ endString) {
            var range = new Range(self, endString, false);
            object result = RangeOps.Each(storage, block, range);
            // Each answers the range it walked once the block has run to the end; anything
            // else is what a break or a return inside the block produced, and discarding it
            // - as this used to - swallowed the jump entirely.
            return ReferenceEquals(result, range) ? (object)self : result;
        }

        #endregion


        #region replace, reverse, reverse!

        [RubyMethod("replace")]
        public static MutableString/*!*/ Replace(MutableString/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ other) {
            // Handle case where objects are the same identity
            if (ReferenceEquals(self, other)) {
                self.RequireNotFrozen();
                return self;
            }

            self.Clear();
            self.Append(other);
            self.ForceEncoding(other.Encoding);
            return self.TaintBy(other);
        }

        [RubyMethod("reverse")]
        public static MutableString/*!*/ GetReversed(MutableString/*!*/ self) {
            return self.CloneDerived().Reverse();
        }

        [RubyMethod("reverse!")]
        public static MutableString/*!*/ Reverse(MutableString/*!*/ self) {
            self.RequireNotFrozen();
            
            if (self.IsEmpty) {
                return self;
            }
            
            // TODO: MRI 1.9: allows invalid characters
            return self.Reverse();
        }

        #endregion


        #region tr, tr_s

        internal static MutableString/*!*/ Translate(MutableString/*!*/ src, MutableString/*!*/ from, MutableString/*!*/ to, 
            bool inplace, bool squeeze, out bool anyCharacterMaps) {
            Assert.NotNull(src, from, to);

            if (from.IsEmpty) {
                anyCharacterMaps = false;
                return inplace ? src : src.CloneDerived();
            }

            MutableString dst;
            if (inplace) {
                dst = src;
            } else {
                dst = src.CreateDerived().TaintBy(src);
            }

            // TODO: KCODE
            src.RequireCompatibleEncoding(from);
            dst.RequireCompatibleEncoding(to);
            from.PrepareForCharacterRead();
            to.PrepareForCharacterRead();

            CharacterMap map = CharacterMap.Create(from, to);

            if (to.IsEmpty) {
                anyCharacterMaps = MutableString.TranslateRemove(src, dst, map);
            } else if (squeeze) {
                anyCharacterMaps = MutableString.TranslateSqueeze(src, dst, map);
            } else {
                anyCharacterMaps = MutableString.Translate(src, dst, map);
            }

            return dst;
        }

        // encoding aware, TODO: KCODE
        [RubyMethod("tr")]
        public static MutableString/*!*/ GetTranslated(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ from, [DefaultProtocol, NotNull]MutableString/*!*/ to) {
            bool _;
            return Translate(self, from, to, false, false, out _);
        }

        // encoding aware, TODO: KCODE
        [RubyMethod("tr!")]
        public static MutableString Translate(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ from, [DefaultProtocol, NotNull]MutableString/*!*/ to) {

            bool anyCharacterMaps;
            self.RequireNotFrozen();
            Translate(self, from, to, true, false, out anyCharacterMaps);
            return anyCharacterMaps ? self : null;
        }

        // encoding aware, TODO: KCODE
        [RubyMethod("tr_s")]
        public static MutableString/*!*/ TrSqueeze(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ from, [DefaultProtocol, NotNull]MutableString/*!*/ to) {
            bool _;
            return Translate(self, from, to, false, true, out _);
        }

        // encoding aware, TODO: KCODE
        [RubyMethod("tr_s!")]
        public static MutableString TrSqueezeInPlace(MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ from, [DefaultProtocol, NotNull]MutableString/*!*/ to) {

            bool anyCharacterMaps;
            self.RequireNotFrozen();
            Translate(self, from, to, true, true, out anyCharacterMaps);
            return anyCharacterMaps ? self : null;
        }

        #endregion

        
        #region ljust

        [RubyMethod("ljust")]
        public static MutableString/*!*/ LeftJustify(MutableString/*!*/ self, [DefaultProtocol]int width) {
            // TODO: is this correct? Is it just a space or is this some configurable whitespace thing?
            return LeftJustify(self, width, _DefaultPadding);
        }

        [RubyMethod("ljust")]
        public static MutableString/*!*/ LeftJustify(MutableString/*!*/ self, 
            [DefaultProtocol]int width, [DefaultProtocol, NotNull]MutableString/*!*/ padding) {

            if (padding.Length == 0) {
                throw RubyExceptions.CreateArgumentError("zero width padding");
            }

            int count = width - self.Length;
            if (count <= 0) {
                return self;
            }

            int iterations = count / padding.Length;
            int remainder = count % padding.Length;
            MutableString result = self.CloneDerived().TaintBy(padding);

            for (int i = 0; i < iterations; i++) {
                result.Append(padding);
            }

            result.Append(padding, 0, remainder);

            return result;
        }

        #endregion

        #region rjust

        [RubyMethod("rjust")]
        public static MutableString/*!*/ RightJustify(MutableString/*!*/ self, [DefaultProtocol]int width) {
            // TODO: is this correct? Is it just a space or is this some configurable whitespace thing?
            return RightJustify(self, width, _DefaultPadding);
        }

        [RubyMethod("rjust")]
        public static MutableString/*!*/ RightJustify(MutableString/*!*/ self, 
            [DefaultProtocol]int width, [DefaultProtocol, NotNull]MutableString/*!*/ padding) {

            if (padding.Length == 0) {
                throw RubyExceptions.CreateArgumentError("zero width padding");
            }

            int count = width - self.Length;
            if (count <= 0) {
                return self;
            }

            int iterations = count / padding.Length;
            int remainder = count % padding.Length;
            MutableString result = self.CreateDerived().TaintBy(self).TaintBy(padding);

            for (int i = 0; i < iterations; i++) {
                result.Append(padding);
            }

            result.Append(padding.GetSlice(0, remainder));
            result.Append(self);

            return result;
        }

        #endregion

        #region unpack

        [RubyMethod("unpack")]
        public static RubyArray/*!*/ Unpack(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ format,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            return RubyEncoder.Unpack(self, format, GetUnpackOffset(fixnumCast, context, self, options));
        }

        [RubyMethod("unpack1")]
        public static object Unpack1(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, MutableString/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ format,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            var result = RubyEncoder.Unpack(self, format, GetUnpackOffset(fixnumCast, context, self, options));
            return result.Count > 0 ? result[0] : null;
        }

        /// <summary>
        /// unpack(format, offset: n) - where in the receiver's *bytes* to start reading. MRI
        /// rejects a negative offset and one past the end, but allows exactly the end, where every
        /// directive yields nil.
        /// </summary>
        private static int GetUnpackOffset(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context,
            MutableString/*!*/ self, IDictionary<object, object> options) {

            if (options == null || options.Count == 0) {
                return 0;
            }

            int offset = 0;
            foreach (var entry in options) {
                var key = entry.Key as RubySymbol;
                if (key == null || key.ToString() != "offset") {
                    throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", context.Inspect(entry.Key));
                }
                offset = Protocols.CastToFixnum(fixnumCast, entry.Value);
            }

            if (offset < 0) {
                throw RubyExceptions.CreateArgumentError("offset can't be negative");
            }
            if (offset > self.GetByteCount()) {
                throw RubyExceptions.CreateArgumentError("offset outside of string");
            }
            return offset;
        }


        #endregion

        #region sum

        [RubyMethod("sum")]
        public static object GetChecksum(MutableString/*!*/ self, [DefaultProtocol, DefaultParameterValue(16)]int bitCount) {
            int length = self.GetByteCount();
            uint mask = (bitCount > 31) ? 0xffffffff : (1U << bitCount) - 1;
            uint sum = 0;
            for (int i = 0; i < length; i++) {
                byte b = self.GetByte(i);
                try {
                    checked { sum = (sum + b) & mask; }
                } catch (OverflowException) {
                    return GetBigChecksum(self, i, sum, bitCount);
                }
            }

            return (sum > Int32.MaxValue) ? (BigInteger)sum : (object)(int)sum;
        }

        private static BigInteger GetBigChecksum(MutableString/*!*/ self, int start, BigInteger/*!*/ sum, int bitCount) {
            BigInteger mask = (((BigInteger)1) << bitCount) - 1;

            int length = self.GetByteCount();
            for (int i = start; i < length; i++) {
                sum = (sum + self.GetByte(i)) & mask;
            }
            return sum;
        }

        #endregion

        private static void RequireNoVersionChange(MutableString/*!*/ self) {
            if (self.HasChanged) {
                throw RubyExceptions.CreateRuntimeError("string modified");
            }
        }
    }
}
