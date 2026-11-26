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
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.StringScanner {

    /// <summary>
    /// Every position this class deals with - #pos, #peek, #rest_size, #matched_size, the
    /// offsets behind #pre_match and #post_match - is a byte offset, as MRI's is.  #charpos is
    /// the one exception, and #getch is the one operation that moves by a character.
    /// </summary>
    [RubyClass("StringScanner")]
    public sealed class StringScanner : RubyObject {

        [RubyException("Error")]
        public class Error : SystemException {
            public Error() : this(null, null) { }
            public Error(string message) : this(message, null) { }
            public Error(string message, Exception inner) : base(message ?? "Error", inner) { }
        }

        private MutableString/*!*/ _scanString;

        /// <summary>
        /// When false - the default - ^ and \A anchor to the scan pointer rather than to the
        /// start of the string, which is implemented by matching against the rest of the
        /// string as a subject in its own right.
        /// </summary>
        private bool _fixedAnchor;

        private int _position;
        private int _previousPosition;

        private bool _matched;
        private int _matchStart;
        private int _matchEnd;

        /// <summary>
        /// The regexp match behind the last scan, or null after #getch, #get_byte, #scan_byte
        /// and #scan_integer - those match without a pattern, so they leave a match with text
        /// but no groups at all.
        /// </summary>
        private MatchData _match;

        /// <summary>
        /// A regexp match whose MatchData is only made when its groups are asked for (see
        /// CurrentMatch): the operations that answer a length or a position (#skip, #match?,
        /// #exist?, #skip_until) rarely have their groups read.
        /// </summary>
        private RubyRegex.PendingMatch _pendingMatch;

        /// <summary>The byte offset _match's own offsets are relative to.</summary>
        private int _matchBase;

        #region Construction

        public StringScanner(RubyClass/*!*/ rubyClass)
            : base(rubyClass) {
            _scanString = MutableString.FrozenEmpty;
        }

        public StringScanner(SerializationInfo/*!*/ info, StreamingContext context)
            : base(info, context) {
            // TODO: deserialize
        }

        public override void GetObjectData(SerializationInfo/*!*/ info, StreamingContext context) {
            base.GetObjectData(info, context);
            // TODO: serialize
        }

        protected override RubyObject/*!*/ CreateInstance() {
            return new StringScanner(ImmediateClass.NominalClass);
        }

        private void InitializeFrom(StringScanner/*!*/ other) {
            _scanString = other._scanString;
            _fixedAnchor = other._fixedAnchor;
            _position = other._position;
            _previousPosition = other._previousPosition;
            _matched = other._matched;
            _matchStart = other._matchStart;
            _matchEnd = other._matchEnd;
            _match = other._match;
            _pendingMatch = other._pendingMatch;
            _matchBase = other._matchBase;
        }

        private static bool ReadFixedAnchor(RubyContext/*!*/ context, object options) {
            var hash = options as IDictionary<object, object>;
            if (hash == null) {
                return false;
            }
            object value;
            return hash.TryGetValue(context.CreateAsciiSymbol("fixed_anchor"), out value) && Protocols.IsTrue(value);
        }

        [RubyConstructor]
        public static StringScanner/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ scan,
            [Optional]object options) {

            var result = new StringScanner(self);
            Reinitialize(result, scan, options);
            return result;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static void Reinitialize(StringScanner/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ scan,
            [Optional]object options) {

            self._fixedAnchor = ReadFixedAnchor(self.ImmediateClass.Context, options);
            self._scanString = scan;
            self.ResetScanner();
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static void InitializeFrom(StringScanner/*!*/ self, [DefaultProtocol, NotNull]StringScanner/*!*/ other) {
            self.InitializeFrom(other);
        }

        #endregion

        #region Singleton Methods

        /// <summary>
        /// This method is defined for backwards compatibility
        /// </summary>
        [RubyMethod("must_C_version", RubyMethodAttributes.PublicSingleton)]
        public static object MustCVersion(object self) {
            return self;
        }

        #endregion

        #region byte / character bookkeeping

        /// <summary>
        /// The scan pointer is a byte offset and the regex engine matches CLR characters. For a
        /// subject that is valid UTF-8 and not all ASCII, converting between the two used to copy
        /// out and count everything before the offset, in both directions, and to read the subject
        /// as bytes (GetByteCount, GetBinarySlice, GetByte), which switched it from characters to
        /// bytes - and the next match switched it back, decoding the whole of it: a scan of 100 KB
        /// of UTF-8 text word by word took 8.4 s (CRuby: 2 ms). The map converts offsets by walking
        /// the subject's characters from a cursor, the last offset converted; a scanner moves
        /// forward, so a step costs about the length of the token. It is made for one snapshot of
        /// the subject's characters (ConvertToString answers the same string until the subject
        /// changes, and a new one after) and dropped when that changes.
        /// </summary>
        private sealed class Utf8Map {
            internal readonly string/*!*/ Text;
            internal readonly int ByteLength;
            internal readonly bool HasPairs;

            // the byte offset in the high half, the CLR index in the low half: one field, so that a
            // scanner shared between threads can only see a cursor that was right at some point
            private long _cursor;

            internal Utf8Map(string/*!*/ text) {
                Text = text;
                ByteLength = Encoding.UTF8.GetByteCount(text);
                HasPairs = text.Length != CountRunes(text);
            }

            private static int CountRunes(string/*!*/ text) {
                int count = 0;
                for (int i = 0; i < text.Length; i++) {
                    if (!Char.IsLowSurrogate(text[i])) {
                        count++;
                    }
                }
                return count;
            }

            /// <summary>The CLR index of byte offset <paramref name="byteOffset"/>, or -1 when it is inside a character.</summary>
            internal int ClrOf(int byteOffset) {
                if (byteOffset < 0 || byteOffset > ByteLength) {
                    return -1;
                }
                int b, c;
                return Seek(true, byteOffset, out b, out c) ? c : -1;
            }

            /// <summary>The byte offset of CLR index <paramref name="clrIndex"/>, or -1 when it is between the halves of a pair.</summary>
            internal int ByteOf(int clrIndex) {
                if (clrIndex < 0 || clrIndex > Text.Length) {
                    return -1;
                }
                int b, c;
                return Seek(false, clrIndex, out b, out c) ? b : -1;
            }

            private bool Seek(bool byBytes, int target, out int b, out int c) {
                long cursor = Volatile.Read(ref _cursor);
                b = (int)(cursor >> 32);
                c = (int)cursor;
                int at = byBytes ? b : c;
                if (target < at && target < at / 2) {
                    b = c = 0;
                    at = 0;
                }
                string text = Text;
                if (target >= at) {
                    while ((byBytes ? b : c) < target) {
                        char ch = text[c];
                        if (ch < 0x80) {
                            b += 1; c += 1;
                        } else if (ch < 0x800) {
                            b += 2; c += 1;
                        } else if (Char.IsHighSurrogate(ch)) {
                            b += 4; c += 2;
                        } else {
                            b += 3; c += 1;
                        }
                    }
                } else {
                    while ((byBytes ? b : c) > target) {
                        char ch = text[c - 1];
                        if (ch < 0x80) {
                            b -= 1; c -= 1;
                        } else if (ch < 0x800) {
                            b -= 2; c -= 1;
                        } else if (Char.IsLowSurrogate(ch)) {
                            b -= 4; c -= 2;
                        } else {
                            b -= 3; c -= 1;
                        }
                    }
                }
                Volatile.Write(ref _cursor, ((long)b << 32) | (uint)c);
                return (byBytes ? b : c) == target;
            }
        }

        private Utf8Map _map;
        private string _unmappedText;

        // the map _match's CLR offsets are offsets into; null when the match was not made through one
        private Utf8Map _matchMap;

        /// <summary>
        /// The map for the subject as it is now, or null when the subject is not one it is for:
        /// not UTF-8, all ASCII (bytes and CLR characters are one for one, which the paths below
        /// already know), or not valid. IR_FAST_SEARCH=0 makes none.
        /// </summary>
        private Utf8Map Map {
            get {
                if (!MutableStringOps.FastSearchEnabled) {
                    return null;
                }
                MutableString subject = _scanString;
                if (subject.Encoding != RubyEncoding.UTF8 || subject.IsAscii()) {
                    return null;
                }
                // a string held as bytes is decoded once, as a match would
                subject.PrepareForCharacterRead();
                string text = subject.ConvertToString();
                Utf8Map map = _map;
                if (map != null && ReferenceEquals(map.Text, text)) {
                    return map;
                }
                if (ReferenceEquals(text, _unmappedText) || subject.ContainsInvalidCharacters()) {
                    _unmappedText = text;
                    return null;
                }
                return _map = new Utf8Map(text);
            }
        }

        private int ByteLength {
            get {
                Utf8Map map = Map;
                return (map != null) ? map.ByteLength : _scanString.GetByteCount();
            }
        }

        private MutableString/*!*/ ByteSlice(int start, int count) {
            if (count <= 0) {
                return MutableString.CreateEmpty(_scanString.Encoding);
            }
            Utf8Map map = Map;
            if (map != null) {
                int clrStart = map.ClrOf(start);
                int clrEnd = (clrStart >= 0) ? map.ClrOf(start + count) : -1;
                if (clrEnd >= 0) {
                    return MutableString.Create(map.Text.Substring(clrStart, clrEnd - clrStart), _scanString.Encoding);
                }
            }
            return MutableString.CreateBinary(_scanString.GetBinarySlice(start, count), _scanString.Encoding);
        }

        private MutableString/*!*/ RestString() {
            return ByteSlice(_position, ByteLength - _position);
        }

        /// <summary>
        /// How many bytes the character at the scan pointer occupies. A MutableString built
        /// from bytes slices by byte, so the width has to come from the encoding itself: offer
        /// it one more byte at a time until the strict encoding accepts them as a character.
        ///
        /// The strict encoding is the one that matters here. RubyEncoding.Encoding is lenient
        /// by design - it has to be, so that a string whose bytes are invalid in its own
        /// encoding still survives - and answers a replacement character for the first byte of
        /// a multi-byte sequence, which would make every character one byte wide.
        /// </summary>
        private int CharWidthAtPosition() {
            int available = ByteLength - _position;
            if (available <= 0) {
                return 0;
            }
            Utf8Map map = Map;
            if (map != null) {
                int clr = map.ClrOf(_position);
                if (clr >= 0) {
                    char ch = map.Text[clr];
                    return (ch < 0x80) ? 1 : (ch < 0x800) ? 2 : Char.IsHighSurrogate(ch) ? 4 : 3;
                }
            }

            // No encoding Ruby knows needs more than this for one character.
            byte[] bytes = _scanString.GetBinarySlice(_position, Math.Min(available, 8));
            if (bytes == null || bytes.Length == 0) {
                return 1;
            }
            var encoding = _scanString.Encoding.StrictEncoding;
            for (int count = 1; count <= bytes.Length; count++) {
                try {
                    if (encoding.GetCharCount(bytes, 0, count) > 0) {
                        return count;
                    }
                } catch (DecoderFallbackException) {
                    // Not a character yet - or not one at all, in which case the loop runs out
                    // and the byte stands for itself, which is what MRI does with a broken
                    // string too.
                }
            }
            return 1;
        }

        /// <summary>
        /// How many characters the first <paramref name="byteIndex"/> bytes spell, as Ruby counts
        /// them (#charpos): a character above U+FFFF is one, where CharIndexOf counts it as the two
        /// CLR chars the regex engine sees.
        /// </summary>
        private int CharacterCountOf(int byteIndex) {
            if (byteIndex <= 0) {
                return 0;
            }
            if (_scanString.DetectSingleByteCharacters()) {
                return byteIndex;
            }
            Utf8Map map = Map;
            if (map != null) {
                int clr = map.ClrOf(byteIndex);
                if (clr >= 0) {
                    if (!map.HasPairs) {
                        return clr;
                    }
                    int count = 0;
                    for (int i = 0; i < clr; i++) {
                        if (!Char.IsLowSurrogate(map.Text[i])) {
                            count++;
                        }
                    }
                    return count;
                }
            }
            return MutableString.CreateBinary(_scanString.GetBinarySlice(0, byteIndex), _scanString.Encoding).GetCharacterCount();
        }

        /// <summary>
        /// The CLR index - into the subject's character representation, which the regex engine
        /// matches - of byte offset <paramref name="byteIndex"/>.
        /// </summary>
        private int CharIndexOf(int byteIndex) {
            if (byteIndex <= 0) {
                return 0;
            }
            if (_scanString.DetectSingleByteCharacters()) {
                // One byte per character, so the two indices are the same number. The general
                // path below copies the prefix out to count its characters, which would make
                // every scan cost a pass over everything already scanned.
                return byteIndex;
            }
            Utf8Map map = Map;
            if (map != null) {
                int clr = map.ClrOf(byteIndex);
                if (clr >= 0) {
                    return clr;
                }
            }
            return MutableString.CreateBinary(_scanString.GetBinarySlice(0, byteIndex), _scanString.Encoding).GetCharCount();
        }

        private void ResetScanner() {
            _position = 0;
            _previousPosition = 0;
            ClearMatch();
        }

        private void ClearMatch() {
            _matched = false;
            _match = null;
            _pendingMatch = default(RubyRegex.PendingMatch);
            _matchBase = 0;
            _matchStart = 0;
            _matchEnd = 0;
            _matchMap = null;
        }

        #endregion

        #region matching

        /// <summary>
        /// The pattern of a scanning method is either a Regexp or a String that stands for
        /// itself; anything else is converted with #to_str and fails as a String would.
        /// </summary>
        private static object/*!*/ ToPattern(ConversionStorage<MutableString>/*!*/ stringCast, object pattern) {
            var regex = pattern as RubyRegex;
            if (regex != null) {
                return regex;
            }
            var str = pattern as MutableString;
            if (str != null) {
                return str;
            }
            // Symbol has no #to_str in Ruby, but this runtime's implicit String conversion
            // still accepts one - a 1.8-ism that survives in ConvertToStrAction - so the
            // pattern has to be rejected here rather than by the conversion.
            if (pattern is RubySymbol) {
                throw RubyExceptions.CreateTypeError("no implicit conversion of Symbol into String");
            }
            return Protocols.CastToString(stringCast, pattern);
        }

        private bool Match(object/*!*/ pattern, bool anchored, bool advance) {
            return Match(pattern, anchored, advance, false);
        }

        /// <param name="lazyGroups">The caller answers a length or a position, not text: the
        /// MatchData can wait until something asks for a group.</param>
        private bool Match(object/*!*/ pattern, bool anchored, bool advance, bool lazyGroups) {
            var regex = pattern as RubyRegex;
            return (regex != null)
                ? MatchRegex(regex, anchored, advance, lazyGroups)
                : MatchLiteral((MutableString)pattern, anchored, advance);
        }

        /// <summary>
        /// The MatchData of the last regexp match, made now if the match left it pending; null
        /// when the last match had no pattern.
        /// </summary>
        private MatchData CurrentMatch {
            get {
                if (_match == null && !_pendingMatch.IsEmpty) {
                    _match = _pendingMatch.Complete();
                    _pendingMatch = default(RubyRegex.PendingMatch);
                }
                return _match;
            }
        }

        private bool MatchRegex(RubyRegex/*!*/ pattern, bool anchored, bool advance, bool lazyGroups) {
            ClearMatch();

            Utf8Map map = Map;
            int clrPosition = (map != null) ? map.ClrOf(_position) : -1;
            if (clrPosition >= 0) {
                // The MatchData is made here rather than left pending: the window match makes it anyway
                // for a subject that is not one byte per character (see MatchWindowBounds).
                MatchData data = _fixedAnchor
                    ? pattern.Match(_scanString, clrPosition, false)
                    : pattern.MatchWindow(_scanString, clrPosition, anchored, false);
                int clrStart, clrEnd;
                if (data == null || !data.TryGetGroupClrOffsets(0, out clrStart, out clrEnd)) {
                    return false;
                }
                int byteStart = map.ByteOf(clrStart);
                int byteEnd = (byteStart >= 0) ? map.ByteOf(clrEnd) : -1;
                if (byteEnd >= 0) {
                    if (anchored && byteStart != _position) {
                        return false;
                    }
                    _match = data;
                    _matchMap = map;
                    _matchBase = 0;
                    return Matched(byteStart, byteEnd, advance);
                }
                if (!data.TryGetGroupByteOffsets(0, out byteStart, out byteEnd) || (anchored && byteStart != _position)) {
                    return false;
                }
                _match = data;
                _matchBase = 0;
                return Matched(byteStart, byteEnd, advance);
            }

            if (lazyGroups && !_fixedAnchor) {
                int boundsStart, boundsEnd;
                RubyRegex.PendingMatch pending;
                if (!pattern.MatchWindowBounds(_scanString, CharIndexOf(_position), anchored, out boundsStart, out boundsEnd, out pending)) {
                    return false;
                }
                if (anchored && boundsStart != _position) {
                    return false;
                }
                _pendingMatch = pending;
                _matchBase = 0;
                return Matched(boundsStart, boundsEnd, advance);
            }

            MatchData match;
            int matchBase = 0;
            if (_fixedAnchor) {
                match = pattern.Match(_scanString, CharIndexOf(_position), false);
            } else {
                // Without a fixed anchor, \A, ^ and \z have to see the rest of the string as if it
                // were the whole subject. Cutting the rest out into a string of its own says that,
                // but it also copies - and re-encodes - everything left to scan on every single
                // call, which makes a scan of a long subject quadratic in its length. Matching a
                // window of the subject says exactly the same thing to the CLR regex engine and
                // costs nothing, and the offsets it reports are already absolute.
                match = pattern.MatchWindow(_scanString, CharIndexOf(_position), anchored, false);
            }

            if (match == null) {
                return false;
            }

            int matchStart, matchEnd;
            if (!match.TryGetGroupByteOffsets(0, out matchStart, out matchEnd)) {
                return false;
            }

            int start = matchBase + matchStart;
            if (anchored && start != _position) {
                return false;
            }

            _match = match;
            _matchBase = matchBase;
            return Matched(start, matchBase + matchEnd, advance);
        }

        private bool MatchLiteral(MutableString/*!*/ pattern, bool anchored, bool advance) {
            ClearMatch();

            if (anchored && pattern.IsAscii()) {
                // The usual #skip("{"): an ASCII pattern's characters are its bytes, so it can be
                // compared where it stands instead of being copied out to a byte array first.
                Utf8Map map = Map;
                int clr = (map != null) ? map.ClrOf(_position) : -1;
                if (clr >= 0) {
                    string literal = pattern.ToString();
                    if (!map.Text.AsSpan(clr).StartsWith(literal.AsSpan())) {
                        return false;
                    }
                    return Matched(_position, _position + literal.Length, advance);
                }
                int length = pattern.GetByteCount();
                if (_position + length > ByteLength) {
                    return false;
                }
                for (int i = 0; i < length; i++) {
                    if (_scanString.GetByte(_position + i) != pattern.GetByte(i)) {
                        return false;
                    }
                }
                return Matched(_position, _position + length, advance);
            }

            byte[] needle = pattern.ToByteArray();
            int start = -1;
            if (anchored) {
                if (StartsAt(_position, needle)) {
                    start = _position;
                }
            } else {
                for (int i = _position; i + needle.Length <= ByteLength; i++) {
                    if (StartsAt(i, needle)) {
                        start = i;
                        break;
                    }
                }
            }

            if (start < 0) {
                return false;
            }
            return Matched(start, start + needle.Length, advance);
        }

        private bool StartsAt(int offset, byte[]/*!*/ needle) {
            if (offset + needle.Length > ByteLength) {
                return false;
            }
            for (int i = 0; i < needle.Length; i++) {
                if (_scanString.GetByte(offset + i) != needle[i]) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Records a successful match and, if asked, moves the scan pointer past it.</summary>
        private bool Matched(int start, int end, bool advance) {
            _matched = true;
            _matchStart = start;
            _matchEnd = end;
            _previousPosition = _position;
            if (advance) {
                _position = end;
            }
            return true;
        }

        #endregion

        #region Public Instance Methods

        [RubyMethod("<<")]
        [RubyMethod("concat")]
        public static StringScanner Concat(StringScanner/*!*/ self, MutableString str) {
            self._scanString.Append(str);
            return self;
        }

        [RubyMethod("[]")]
        public static MutableString GetMatchSubgroup(ConversionStorage<int>/*!*/ fixnumCast, StringScanner/*!*/ self, object index) {
            string name = GroupName(index);
            if (name != null) {
                if (!self._matched) {
                    return null;
                }
                // A match with no pattern behind it has no named groups at all, so every name
                // is undefined rather than merely unmatched.
                if (self.CurrentMatch == null || !self.CurrentMatch.HasNamedGroup(name)) {
                    throw RubyExceptions.CreateIndexError("undefined group name reference: {0}", name);
                }
                int[] named = self.CurrentMatch.GetGroupOffsets(name, true);
                return (named == null) ? null : self.ByteSlice(self._matchBase + named[0], named[1] - named[0]);
            }

            // An Integer index is the usual case; it converts to itself.
            int subgroup = (index is int) ? (int)index : Protocols.CastToFixnum(fixnumCast, index);
            if (!self._matched) {
                return null;
            }
            if (self.CurrentMatch == null) {
                return (subgroup == 0) ? self.ByteSlice(self._matchStart, self._matchEnd - self._matchStart) : null;
            }

            int count = self.CurrentMatch.GroupCount;
            if (subgroup < 0) {
                subgroup += count;
            }
            if (subgroup < 0 || subgroup >= count) {
                return null;
            }
            int start, end;
            Utf8Map map = self.Map;
            if (map != null && map == self._matchMap) {
                if (!self.CurrentMatch.TryGetGroupClrOffsets(subgroup, out start, out end)) {
                    return null;
                }
                int byteStart = map.ByteOf(start), byteEnd = map.ByteOf(end);
                if (byteStart >= 0 && byteEnd >= 0) {
                    return self.ByteSlice(byteStart, byteEnd - byteStart);
                }
            }
            return self.CurrentMatch.TryGetGroupByteOffsets(subgroup, out start, out end)
                ? self.ByteSlice(self._matchBase + start, end - start)
                : null;
        }

        private static string GroupName(object index) {
            var symbol = index as RubySymbol;
            if (symbol != null) {
                return symbol.ToString();
            }
            var str = index as MutableString;
            return (str != null) ? str.ConvertToString() : null;
        }

        [RubyMethod("captures")]
        public static RubyArray Captures(StringScanner/*!*/ self) {
            if (!self._matched) {
                return null;
            }
            var result = new RubyArray();
            if (self.CurrentMatch == null) {
                return result;
            }
            for (int i = 1; i < self.CurrentMatch.GroupCount; i++) {
                int[] offsets = self.CurrentMatch.GetGroupOffsets(i, true);
                result.Add((offsets == null) ? null : self.ByteSlice(self._matchBase + offsets[0], offsets[1] - offsets[0]));
            }
            return result;
        }

        [RubyMethod("values_at")]
        public static RubyArray ValuesAt(ConversionStorage<int>/*!*/ fixnumCast, StringScanner/*!*/ self, [NotNull]params object[]/*!*/ indices) {
            if (!self._matched) {
                return null;
            }
            var result = new RubyArray(indices.Length);
            foreach (var index in indices) {
                result.Add(GetMatchSubgroup(fixnumCast, self, index));
            }
            return result;
        }

        [RubyMethod("named_captures")]
        public static Hash/*!*/ NamedCaptures(RubyContext/*!*/ context, StringScanner/*!*/ self) {
            var result = new Hash(context);
            if (!self._matched || self.CurrentMatch == null) {
                return result;
            }
            foreach (var name in self.CurrentMatch.GetGroupNames()) {
                int[] offsets = self.CurrentMatch.GetGroupOffsets(name, true);
                result[MutableString.CreateAscii(name).Freeze()] =
                    (offsets == null) ? null : self.ByteSlice(self._matchBase + offsets[0], offsets[1] - offsets[0]);
            }
            return result;
        }

        [RubyMethod("size")]
        public static int? Size(StringScanner/*!*/ self) {
            if (!self._matched) {
                return null;
            }
            return (self.CurrentMatch != null) ? self.CurrentMatch.GroupCount : 1;
        }

        [RubyMethod("fixed_anchor?")]
        public static bool IsFixedAnchor(StringScanner/*!*/ self) {
            return self._fixedAnchor;
        }

        [RubyMethod("beginning_of_line?")]
        [RubyMethod("bol?")]
        public static bool BeginningOfLine(StringScanner/*!*/ self) {
            if (self._position == 0) {
                return true;
            }
            Utf8Map map = self.Map;
            int clr = (map != null) ? map.ClrOf(self._position) : -1;
            if (clr > 0) {
                return map.Text[clr - 1] == '\n';
            }
            return self._scanString.GetByte(self._position - 1) == (byte)'\n';
        }

        [RubyMethod("check")]
        public static MutableString Check(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern) {
            return ScanFull(stringCast, self, pattern, false, true) as MutableString;
        }

        [RubyMethod("check_until")]
        public static MutableString CheckUntil(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern) {
            return SearchFull(stringCast, self, pattern, false, true) as MutableString;
        }

        [RubyMethod("eos?")]
        public static bool EndOfLine(StringScanner/*!*/ self) {
            return self._position >= self.ByteLength;
        }

        [RubyMethod("exist?")]
        public static int? Exist(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern) {
            if (!self.Match(ToPattern(stringCast, pattern), false, false, true)) {
                return null;
            }
            return self._matchEnd - self._position;
        }

        [RubyMethod("get_byte")]
        public static MutableString GetByte(StringScanner/*!*/ self) {
            if (self._position >= self.ByteLength) {
                self.ClearMatch();
                return null;
            }
            var result = self.ByteSlice(self._position, 1);
            self.ClearMatch();
            self.Matched(self._position, self._position + 1, true);
            return result;
        }

        [RubyMethod("peek_byte")]
        public static int? PeekByte(StringScanner/*!*/ self) {
            if (self._position >= self.ByteLength) {
                return null;
            }
            return self._scanString.GetByte(self._position);
        }

        [RubyMethod("scan_byte")]
        public static int? ScanByte(StringScanner/*!*/ self) {
            if (self._position >= self.ByteLength) {
                self.ClearMatch();
                return null;
            }
            int result = self._scanString.GetByte(self._position);
            self.ClearMatch();
            self.Matched(self._position, self._position + 1, true);
            return result;
        }

        [RubyMethod("getch")]
        public static MutableString GetChar(StringScanner/*!*/ self) {
            if (self._position >= self.ByteLength) {
                self.ClearMatch();
                return null;
            }
            // The one place a scanner moves by a character rather than by a byte.
            int width = self.CharWidthAtPosition();
            var result = self.ByteSlice(self._position, width);
            self.ClearMatch();
            self.Matched(self._position, self._position + width, true);
            return result;
        }

        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString ToString(StringScanner/*!*/ self) {
            return MutableString.Create(self.ToString(), self._scanString.Encoding);
        }

        [RubyMethod("match?")]
        public static int? Match(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern) {
            if (!self.Match(ToPattern(stringCast, pattern), true, false, true)) {
                return null;
            }
            return self._matchEnd - self._matchStart;
        }

        [RubyMethod("matched")]
        public static MutableString Matched(StringScanner/*!*/ self) {
            if (!self._matched) {
                return null;
            }
            return self.ByteSlice(self._matchStart, self._matchEnd - self._matchStart);
        }

        [RubyMethod("matched?")]
        public static bool WasMatched(StringScanner/*!*/ self) {
            return self._matched;
        }

        [RubyMethod("matched_size")]
        public static int? MatchedSize(StringScanner/*!*/ self) {
            if (!self._matched) {
                return null;
            }
            return self._matchEnd - self._matchStart;
        }

        [RubyMethod("peek")]
        public static MutableString/*!*/ Peek(StringScanner/*!*/ self, [DefaultProtocol]int len) {
            if (len < 0) {
                throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
            }
            int available = self.ByteLength - self._position;
            return self.ByteSlice(self._position, Math.Min(len, available));
        }

        [RubyMethod("pos")]
        [RubyMethod("pointer")]
        public static int GetCurrentPosition(StringScanner/*!*/ self) {
            return self._position;
        }

        [RubyMethod("charpos")]
        public static int GetCharPosition(StringScanner/*!*/ self) {
            return self.CharacterCountOf(self._position);
        }

        [RubyMethod("pos=")]
        [RubyMethod("pointer=")]
        public static int SetCurrentPosition(StringScanner/*!*/ self, [DefaultProtocol]int newPosition) {
            int position = newPosition;
            if (position < 0) {
                position += self.ByteLength;
            }
            if (position < 0 || position > self.ByteLength) {
                throw RubyExceptions.CreateRangeError("index out of range");
            }
            self._position = position;
            return newPosition;
        }

        [RubyMethod("post_match")]
        public static MutableString PostMatch(StringScanner/*!*/ self) {
            if (!self._matched) {
                return null;
            }
            return self.ByteSlice(self._matchEnd, self.ByteLength - self._matchEnd);
        }

        [RubyMethod("pre_match")]
        public static MutableString PreMatch(StringScanner/*!*/ self) {
            if (!self._matched) {
                return null;
            }
            return self.ByteSlice(0, self._matchStart);
        }

        [RubyMethod("reset")]
        public static StringScanner Reset(StringScanner/*!*/ self) {
            self.ResetScanner();
            return self;
        }

        [RubyMethod("rest")]
        public static MutableString/*!*/ Rest(StringScanner/*!*/ self) {
            return self.RestString();
        }

        [RubyMethod("rest?")]
        public static bool IsRestLeft(StringScanner/*!*/ self) {
            return self._position < self.ByteLength;
        }

        [RubyMethod("rest_size")]
        public static int RestSize(StringScanner/*!*/ self) {
            return Math.Max(0, self.ByteLength - self._position);
        }

        [RubyMethod("scan")]
        public static object Scan(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern) {
            return ScanFull(stringCast, self, pattern, true, true);
        }

        [RubyMethod("scan_full")]
        public static object ScanFull(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern,
            bool advancePointer, bool returnString) {

            if (!self.Match(ToPattern(stringCast, pattern), true, advancePointer)) {
                return null;
            }
            int length = self._matchEnd - self._matchStart;
            return returnString
                ? (object)self.ByteSlice(self._matchStart, length)
                : ScriptingRuntimeHelpers.Int32ToObject(length);
        }

        [RubyMethod("scan_until")]
        public static object ScanUntil(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern) {
            return SearchFull(stringCast, self, pattern, true, true);
        }

        [RubyMethod("search_full")]
        public static object SearchFull(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern,
            bool advancePointer, bool returnString) {

            if (!self.Match(ToPattern(stringCast, pattern), false, advancePointer)) {
                return null;
            }
            int length = self._matchEnd - self._previousPosition;
            return returnString
                ? (object)self.ByteSlice(self._previousPosition, length)
                : ScriptingRuntimeHelpers.Int32ToObject(length);
        }

        [RubyMethod("skip")]
        public static int? Skip(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern) {
            if (!self.Match(ToPattern(stringCast, pattern), true, true, true)) {
                return null;
            }
            return self._position - self._previousPosition;
        }

        [RubyMethod("skip_until")]
        public static int? SkipUntil(ConversionStorage<MutableString>/*!*/ stringCast, StringScanner/*!*/ self, object pattern) {
            if (!self.Match(ToPattern(stringCast, pattern), false, true, true)) {
                return null;
            }
            return self._position - self._previousPosition;
        }

        /// <summary>
        /// Scans a decimal or hexadecimal integer literal by hand rather than through a
        /// pattern, which is what lets it answer in the scanner's own encoding-free terms.
        /// </summary>
        [RubyMethod("scan_integer")]
        public static object ScanInteger(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, StringScanner/*!*/ self,
            [Keywords, Optional]object options) {

            int numericBase = 10;
            var hash = options as IDictionary<object, object>;
            if (hash != null) {
                object value;
                if (hash.TryGetValue(context.CreateAsciiSymbol("base"), out value) && value != null) {
                    numericBase = Protocols.CastToFixnum(fixnumCast, value);
                }
            }
            if (numericBase != 10 && numericBase != 16) {
                throw RubyExceptions.CreateArgumentError("Unsupported integer base: {0}, expected 10 or 16", numericBase);
            }
            if (!self._scanString.Encoding.IsAsciiIdentity) {
                throw new EncodingCompatibilityError(
                    String.Format("ASCII incompatible encoding: {0}", self._scanString.Encoding.Name)
                );
            }

            int end = self.ByteLength;
            int i = self._position;
            bool negative = false;
            if (i < end && (self._scanString.GetByte(i) == (byte)'+' || self._scanString.GetByte(i) == (byte)'-')) {
                negative = self._scanString.GetByte(i) == (byte)'-';
                i++;
            }

            int digitsStart = i;
            if (numericBase == 16 && i + 1 < end && self._scanString.GetByte(i) == (byte)'0' &&
                (self._scanString.GetByte(i + 1) == (byte)'x' || self._scanString.GetByte(i + 1) == (byte)'X')) {

                // "0x" with nothing usable behind it is the literal 0, and the x is left unscanned.
                if (i + 2 < end && IsDigit(self._scanString.GetByte(i + 2), 16)) {
                    i += 2;
                    digitsStart = i;
                }
            }

            var digits = new StringBuilder();
            while (i < end && IsDigit(self._scanString.GetByte(i), numericBase)) {
                digits.Append((char)self._scanString.GetByte(i));
                i++;
            }

            if (digits.Length == 0) {
                self.ClearMatch();
                return null;
            }

            BigInteger result = BigInteger.Zero;
            foreach (char c in digits.ToString()) {
                result = result * numericBase + DigitValue(c);
            }
            if (negative) {
                result = -result;
            }

            self.ClearMatch();
            self.Matched(self._position, i, true);
            return Protocols.Normalize(result);
        }

        private static bool IsDigit(byte b, int numericBase) {
            if (b >= '0' && b <= '9') {
                return true;
            }
            return numericBase == 16 && ((b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F'));
        }

        private static int DigitValue(char c) {
            if (c >= '0' && c <= '9') {
                return c - '0';
            }
            return (c >= 'a') ? (c - 'a' + 10) : (c - 'A' + 10);
        }

        [RubyMethod("string")]
        public static MutableString GetString(StringScanner/*!*/ self) {
            return self._scanString;
        }

        [RubyMethod("string=")]
        public static MutableString SetString(StringScanner/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            self._scanString = str;
            self.ResetScanner();
            return str;
        }

        [RubyMethod("terminate")]
        public static StringScanner Clear(StringScanner/*!*/ self) {
            self.ClearMatch();
            self._previousPosition = self._position;
            self._position = self.ByteLength;
            return self;
        }

        [RubyMethod("unscan")]
        public static StringScanner Unscan(StringScanner/*!*/ self) {
            if (!self._matched) {
                throw new Error("unscan failed: previous match record not exist");
            }
            self._position = self._previousPosition;
            self.ClearMatch();
            return self;
        }

        #endregion

        public override string ToString() {
            // #<StringScanner 4/14 "This" @ " is a...">
            if (_position >= ByteLength) {
                return "#<StringScanner fin>";
            }

            byte[] scanned = _scanString.ToByteArray();
            var sb = new StringBuilder("#<StringScanner ");
            sb.AppendFormat("{0}/{1}", _position, scanned.Length);

            if (_position > 0) {
                sb.Append(" \"");
                int len = Math.Min(_position, 5);
                if (_position > 5) {
                    sb.Append("...");
                }
                for (int i = _position - len; i < _position; i++) {
                    MutableString.AppendCharRepresentation(sb, scanned[i], -1,
                        MutableString.Escape.NonAscii | MutableString.Escape.Special, '"', -1
                    );
                }
                sb.Append('"');
            }

            sb.Append(" @ \"");
            int restLen = Math.Min(scanned.Length - _position, 5);
            for (int i = _position; i < _position + restLen; i++) {
                MutableString.AppendCharRepresentation(sb, scanned[i], -1,
                    MutableString.Escape.NonAscii | MutableString.Escape.Special, '"', -1
                );
            }
            if (scanned.Length - _position > 5) {
                sb.Append("...");
            }
            sb.Append("\">");
            return sb.ToString();
        }
    }
}
