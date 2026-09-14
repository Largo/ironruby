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
using System.Diagnostics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Text;
using System.Text.RegularExpressions;
using IronRuby.Compiler;
using IronRuby.Runtime;
using System.Collections.Generic;

namespace IronRuby.Builtins {
    public partial class RubyRegex : IEquatable<RubyRegex>, IDuplicable, IRubyObjectState {
        // 1.9: correctly encoded, switched to characters 
        // 1.8: k-coded binary data, if _options specify encoding, or raw binary data otherwise.
        private MutableString/*!*/ _pattern;
        private RubyRegexOptions _options;
        private bool _hasGAnchor;

        // Regexp.allocate produces a regexp with no pattern at all, which MRI reports as BINARY
        // rather than as the US-ASCII an empty pattern would give.
        private bool _initialized;

        private Regex _cachedRegex;

        // Ruby 1.8: match operations use KCODE encoding so we need to remember the one for which we have cached CLR Regex.
        private RubyRegexOptions _cachedKCode;

        private const int FrozenFlag = 1;
        private const int TaintedFlag = 2;
        private const int UntrustedFlag = 4;

        // A regexp literal is frozen by MRI, so the state has to live on the object itself rather
        // than in the context's instance data, which the literal-construction path has no access to.
        private int _flags;

        #region IRubyObjectState Members

        public bool IsFrozen {
            get { return (_flags & FrozenFlag) != 0; }
        }

        public bool IsTainted {
            get { return (_flags & TaintedFlag) != 0; }
            set { _flags = (_flags & ~TaintedFlag) | (value ? TaintedFlag : 0); }
        }

        public bool IsUntrusted {
            get { return (_flags & UntrustedFlag) != 0; }
            set { _flags = (_flags & ~UntrustedFlag) | (value ? UntrustedFlag : 0); }
        }

        public void Freeze() {
            _flags |= FrozenFlag;
        }

        #endregion

        #region Construction

        public RubyRegex() {
            _pattern = MutableString.CreateEmpty();
            _options = RubyRegexOptions.NONE;
        }

        public RubyRegex(MutableString/*!*/ pattern) 
            : this(pattern, RubyRegexOptions.NONE) {
        }

        public RubyRegex(MutableString/*!*/ pattern, RubyRegexOptions options) {
            Set(pattern, options);
        }

        public RubyRegex(RubyRegex/*!*/ regex) {
            ContractUtils.RequiresNotNull(regex, "regex");
            Set(regex.Pattern, regex.Options);
        }

        public void Set(MutableString/*!*/ pattern, RubyRegexOptions options) {
            ContractUtils.RequiresNotNull(pattern, "pattern");
            _initialized = true;

            // RubyRegexOptions.Once is only used to determine how the Regexp object should be created and cached. 
            // It is not a property of the final object. /foo/ should compare equal with /foo/o.
            _options = options & ~RubyRegexOptions.Once;

            RubyEncoding encoding = RubyEncoding.GetRegexEncoding(options);
            if (encoding != null) {
                _pattern = MutableString.CreateBinary(pattern.ToByteArray(), encoding ?? RubyEncoding.Binary).Freeze();
            } else {
                _pattern = pattern.PrepareForCharacterRead().Clone().Freeze();
            }
            
            TransformPattern(encoding, options & RubyRegexOptions.EncodingMask);
        }

        /// <summary>
        /// Creates a copy of the proc that has the same target, context, self object as this instance.
        /// Doesn't copy instance data.
        /// Preserves the class of the Regexp.
        /// </summary>
        protected virtual RubyRegex/*!*/ Copy() {
            return new RubyRegex(this);
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = Copy();
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        #endregion

        #region Transformation to CLR Regex

        private Regex/*!*/ Transform(ref RubyEncoding encoding, MutableString/*!*/ input, int start, out string strInput) {
            ContractUtils.RequiresNotNull(input, "input");

            // TODO:

            // K-coding of the current operation (the current KCODE gets preference over the KCODE regex option):
            RubyRegexOptions kc = _options & RubyRegexOptions.EncodingMask;
            if (kc != 0) {
                encoding = _pattern.Encoding;
            } else {
                kc = RubyRegexOptions.NONE;
            }

            // Convert input to a string. Force k-coding if necessary.
            if (kc != 0) {
                // Handling multi-byte K-coded characters is not entirely correct here.
                // Three cases to be considered:
                // 1) Multi-byte character is explicitly contained in the pattern: /�*/
                // 2) Subsequent escapes form a complete character: /\342\202\254*/ or /\xe2\x82\xac*/
                // 3) Subsequent escapes form an incomplete character: /[\x7f-\xff]{1,3}/
                //
                // In the first two cases we want to "group" the byte triplet so that regex operators like *, +, ? and {n,m} operate on 
                // the entire character, not just the last byte. We could unescape the bytes and replace them with complete Unicode characters.
                // Then we could encode the input using the same K-coding and we would get a match. 
                // However, case 3) requires the opposite: to match the bytes we need to encode the input using binary encoding. 
                // Using this encoding makes *+? operators operate on the last byte (encoded as UTF16 character).
                // 
                // The right solution would require the regex engine to handle multi-byte escaped characters, which it doesn't.
                //
                // TODO:
                // A correct workaround would be to wrap the byte sequence that forms a character into a non-capturing group, 
                // for example transform /\342\202\254*/ to /(?:\342\202\254)*/ and use binary encoding on both input and pattern.
                // For now, we just detect if there are any non-ascii character escapes. If so we use a binary encoding accomodating case 3), 
                // but breaking cases 1 and 2. Otherwise we encode using k-coding to make case 1 match.
                if (HasEscapedNonAsciiBytes(_pattern)) {
                    encoding = RubyEncoding.Binary;
                    kc = 0;
                }
                
                strInput = ForceEncoding(input, encoding.Encoding, start);
            } else {
                _pattern.RequireCompatibleEncoding(input);
                input.PrepareForCharacterRead();
                strInput = input.ConvertToString();
            }

            return TransformPattern(encoding, kc);
        }

        private Regex/*!*/ TransformPattern(RubyEncoding encoding, RubyRegexOptions kc) {
            // We can reuse cached CLR regex if it was created for the same k-coding:
            if (_cachedRegex != null && kc == _cachedKCode) {
                return _cachedRegex;
            }

            string pattern;
            if (kc != 0 || encoding == RubyEncoding.Binary) {
                pattern = _pattern.ToString(encoding.Encoding);
            } else {
                pattern = _pattern.ConvertToString();
            }

            Regex result;
            try {
                result = new Regex(RegexpTransformer.Transform(pattern, _options, out _hasGAnchor), ToClrOptions(_options));
            } catch (Exception e) {
                throw new RegexpError(e.Message);
            }

            _cachedKCode = kc;
            _cachedRegex = result;
            return result;
        }

        /// <summary>
        /// Searches the pattern for hexadecimal and octal character escapes that represent a non-ASCII character.
        /// </summary>
        private static bool HasEscapedNonAsciiBytes(MutableString/*!*/ pattern) {
            int i = 0;
            int length = pattern.GetByteCount();
            while (i < length - 2) {
                int c = pattern.GetByte(i++);
                if (c == '\\') {
                    c = pattern.GetByte(i++);
                    if (c == 'x') {
                        // hexa escape:
                        int d1 = Tokenizer.ToDigit(PeekByte(pattern, length, i++));
                        if (d1 < 16) {
                            int d2 = Tokenizer.ToDigit(PeekByte(pattern, length, i++));
                            if (d2 < 16) {
                                return (d1 * 16 + d2 >= 0x80);
                            }
                        }
                    } else if (c >= '2' && c <= '7') {
                        // a backreference (\1..\9) or an octal escape:
                        int d = Tokenizer.ToDigit(PeekByte(pattern, length, i++));
                        if (d < 8) {
                            int value = Tokenizer.ToDigit(c) * 8 + d;
                            d = Tokenizer.ToDigit(PeekByte(pattern, length, i++));
                            if (d < 8) {
                                value = value * 8 + d;
                            }
                            return value >= 0x80;
                        }
                    }
                }
            }

            return false;
        }

        private static int PeekByte(MutableString/*!*/ str, int length, int i) {
            return (i < length) ? str.GetByte(i) : -1;
        }

        private static string/*!*/ ForceEncoding(MutableString/*!*/ input, Encoding/*!*/ encoding, int start) {
            int byteCount = input.GetByteCount();

            if (start < 0) {
                start += byteCount;
            }
            if (start < 0) {
                return null;
            }

            return (start <= byteCount) ? input.ToString(encoding, start, byteCount - start) : null;
        }

        #endregion

        public bool IsEmpty {
            get { return _pattern.IsEmpty; }
        }

        public RubyRegexOptions Options {
            get { return _options; }
        }

        /// <summary>
        /// False only for a Regexp produced by Regexp.allocate, which has no pattern yet.
        /// </summary>
        public bool IsInitialized {
            get { return _initialized; }
        }

        public RubyEncoding/*!*/ Encoding {
            get {
                // MRI's rb_reg_encoding. A regexp with no encoding flag whose source happens to be
                // ASCII only is US-ASCII, whatever the encoding of the file it was written in, so
                // that it can match a string in any ASCII compatible encoding.
                //
                // /n needs the extra escape check: MRI decides it on the bytes the pattern compiles
                // to, where an \xFF escape is one non-ASCII byte, while _pattern still holds the
                // four ASCII characters of the escape itself. So /ASCII/n is US-ASCII but
                // /\xc2\xa1/n is BINARY.
                if (!_initialized) {
                    return _pattern.Encoding;
                }

                var encodingOptions = _options & RubyRegexOptions.EncodingMask;
                if (encodingOptions == RubyRegexOptions.NONE && _pattern.IsAscii()) {
                    // é is six ASCII characters that compile to one UTF-8 character, so the
                    // regexp is UTF-8 even though its source is ASCII only. (A \xNN or octal escape
                    // above 7 bits without /n is "invalid multibyte escape" in MRI; we don't raise,
                    // and the pattern's own encoding is as good an answer as any.)
                    bool isUnicode;
                    if (HasNonAsciiEscape(_pattern, out isUnicode)) {
                        return isUnicode ? RubyEncoding.UTF8 : _pattern.Encoding;
                    }

                    return RubyEncoding.Ascii;
                }

                if (encodingOptions == RubyRegexOptions.FIXED && _pattern.IsAscii() && !HasNonAsciiEscape(_pattern)) {
                    return RubyEncoding.Ascii;
                }

                return _pattern.Encoding;
            }
        }

        public MutableString/*!*/ Pattern {
            get { return _pattern; }
        }

        #region Pattern inspection (group names, back references)

        private static readonly string[] EmptyNames = new string[0];

        /// <summary>
        /// The names of the pattern's named groups, in the order they open, with duplicates kept so
        /// that the position in the array is the group's Ruby index - 1. When a pattern uses named
        /// groups the plain parenthesised groups don't capture, so those are exactly the capture
        /// group numbers.
        /// </summary>
        public string[]/*!*/ GetGroupNames() {
            return ScanGroupNames(_pattern);
        }

        internal static string[]/*!*/ ScanGroupNames(MutableString/*!*/ pattern) {
            List<string> names = null;
            int length = pattern.GetCharCount();
            bool inClass = false;

            for (int i = 0; i < length; i++) {
                char c = pattern.GetChar(i);

                if (c == '\\') {
                    i++;
                    continue;
                }

                if (inClass) {
                    if (c == ']') {
                        inClass = false;
                    }
                    continue;
                }

                if (c == '[') {
                    inClass = true;
                    continue;
                }

                if (c != '(' || i + 2 >= length || pattern.GetChar(i + 1) != '?') {
                    continue;
                }

                char kind = pattern.GetChar(i + 2);
                char terminator;
                if (kind == '<') {
                    // (?<= and (?<! are look-behind, not a named group
                    if (i + 3 < length) {
                        char next = pattern.GetChar(i + 3);
                        if (next == '=' || next == '!') {
                            continue;
                        }
                    }
                    terminator = '>';
                } else if (kind == '\'') {
                    terminator = '\'';
                } else {
                    continue;
                }

                int start = i + 3;
                int end = start;
                while (end < length && pattern.GetChar(end) != terminator) {
                    end++;
                }

                if (end >= length) {
                    break;
                }

                var name = new StringBuilder(end - start);
                for (int j = start; j < end; j++) {
                    name.Append(pattern.GetChar(j));
                }

                (names ?? (names = new List<string>())).Add(name.ToString());
                i = end;
            }

            return names != null ? names.ToArray() : EmptyNames;
        }

        /// <summary>
        /// Whether the pattern contains a back reference (\1..\9, \k&lt;name&gt;, \k'name').
        /// Those are the constructs that force the matcher to backtrack in a way that can take
        /// more than linear time; see Regexp.linear_time?.
        /// </summary>
        public static bool HasBackReference(MutableString/*!*/ pattern) {
            int length = pattern.GetCharCount();
            for (int i = 0; i < length - 1; i++) {
                if (pattern.GetChar(i) != '\\') {
                    continue;
                }

                char c = pattern.GetChar(i + 1);
                if (c >= '1' && c <= '9' || c == 'k') {
                    return true;
                }

                // an escaped backslash isn't the start of a back reference
                i++;
            }

            return false;
        }

        /// <summary>
        /// Whether the pattern contains an escape that denotes a character outside ASCII: \xNN or
        /// \uNNNN or \NNN (octal) with a value above 0x7f.
        ///
        /// The pattern text of such a regexp is all ASCII, so String#ascii_only? says yes, but the
        /// regexp still only matches one encoding's bytes - MRI's rb_reg_preprocess sets
        /// ARG_ENCODING_FIXED for exactly this case.  Regexp.union has to know, or a /n regexp
        /// written with byte escapes would silently lose its encoding when unioned with a plain
        /// ASCII string.
        /// </summary>
        public static bool HasNonAsciiEscape(MutableString/*!*/ pattern) {
            bool isUnicode;
            return HasNonAsciiEscape(pattern, out isUnicode);
        }

        /// <summary>
        /// As above, and tells whether the escape that decided it was a \u one.  \u names a code
        /// point, so it compiles to UTF-8; \xNN and \NNN name a byte, so they compile to BINARY
        /// (and MRI only allows them at all under /n).
        /// </summary>
        public static bool HasNonAsciiEscape(MutableString/*!*/ pattern, out bool isUnicode) {
            isUnicode = false;
            int length = pattern.GetCharCount();
            for (int i = 0; i < length - 1; i++) {
                if (pattern.GetChar(i) != '\\') {
                    continue;
                }

                int value = -1;
                char c = pattern.GetChar(i + 1);
                if (c == 'x') {
                    // \xN and \xNN; a lone \x is a syntax error the matcher will report.
                    value = HexValue(pattern, i + 2, 2);
                } else if (c == 'u') {
                    // \uNNNN, or \u{...} whose first digit already decides it.
                    value = (i + 2 < length && pattern.GetChar(i + 2) == '{')
                        ? HexValue(pattern, i + 3, 6) : HexValue(pattern, i + 2, 4);
                } else if (c >= '0' && c <= '7') {
                    value = 0;
                    for (int j = i + 1; j < length && j < i + 4; j++) {
                        char digit = pattern.GetChar(j);
                        if (digit < '0' || digit > '7') {
                            break;
                        }
                        value = value * 8 + (digit - '0');
                    }
                }

                if (value > 0x7f) {
                    isUnicode = c == 'u';
                    return true;
                }

                // an escaped backslash isn't the start of an escape
                i++;
            }

            return false;
        }

        /// <summary>
        /// Reads up to <paramref name="maxDigits"/> hex digits starting at <paramref name="start"/>,
        /// or -1 when there isn't one.
        /// </summary>
        private static int HexValue(MutableString/*!*/ pattern, int start, int maxDigits) {
            int length = pattern.GetCharCount();
            int value = -1;
            for (int i = start; i < length && i < start + maxDigits; i++) {
                char c = pattern.GetChar(i);
                int digit;
                if (c >= '0' && c <= '9') {
                    digit = c - '0';
                } else if (c >= 'a' && c <= 'f') {
                    digit = c - 'a' + 10;
                } else if (c >= 'A' && c <= 'F') {
                    digit = c - 'A' + 10;
                } else {
                    break;
                }
                value = (value < 0 ? 0 : value * 16) + digit;
            }
            return value;
        }

        #endregion

        /// <summary>
        /// Whether the regexp can only match strings of one particular encoding: an encoding
        /// modifier other than /n was given, or the pattern itself isn't ASCII only.
        /// </summary>
        public bool IsFixedEncoding {
            get {
                if ((_options & RubyRegexOptions.FIXED) != 0) {
                    // /n on its own says nothing - /abc/n matches any ASCII compatible string. It
                    // pins the encoding only once the pattern really compiles to non-ASCII bytes,
                    // which it does for a byte escape as much as for a literal non-ASCII character.
                    return !_pattern.IsAscii() || HasNonAsciiEscape(_pattern);
                }

                return (_options & (RubyRegexOptions.EUC | RubyRegexOptions.SJIS | RubyRegexOptions.UTF8 | RubyRegexOptions.FixedEncoding)) != 0
                    || Encoding != RubyEncoding.Ascii;
            }
        }

        // The flags that make two regexps behave differently. /n is not one of them: it only says
        // the pattern's bytes are not to be reinterpreted, so // and //n are the same regexp, while
        // /abc/u and /abc/n differ because only the former pins an encoding (see IsFixedEncoding).
        private const RubyRegexOptions BehaviouralOptions =
            RubyRegexOptions.IgnoreCase | RubyRegexOptions.Extended | RubyRegexOptions.Multiline;

        public bool Equals(RubyRegex other) {
            return ReferenceEquals(this, other)
                || other != null
                && (_options & BehaviouralOptions) == (other._options & BehaviouralOptions)
                && IsFixedEncoding == other.IsFixedEncoding
                && Encoding == other.Encoding
                && PatternEquals(_pattern, other._pattern);
        }

        private static bool PatternEquals(MutableString/*!*/ x, MutableString/*!*/ y) {
            // /n keeps the pattern as binary where the same literal without it keeps the source
            // encoding, so the two hold the same characters in differently tagged strings.
            return x.Equals(y) || x.IsAscii() && y.IsAscii() && x.ToString() == y.ToString();
        }

        public override bool Equals(object other) {
            return Equals(other as RubyRegex);
        }

        public override int GetHashCode() {
            int pattern = _pattern.IsAscii() ? _pattern.ToString().GetHashCode() : _pattern.GetHashCode();
            return pattern ^ (int)(_options & BehaviouralOptions);
        }

        public static RegexOptions ToClrOptions(RubyRegexOptions options) {
            RegexOptions result = RegexOptions.Multiline | RegexOptions.CultureInvariant;

#if DEBUG
            if (RubyOptions.CompileRegexps) {
#if SILVERLIGHT || WIN8 // RegexOptions.Compiled
                throw new NotSupportedException("RegexOptions.Compiled is not supported on Silverlight");
#else
                result |= RegexOptions.Compiled;
#endif
            }
#endif
            if ((options & RubyRegexOptions.IgnoreCase) != 0) {
                result |= RegexOptions.IgnoreCase;
            }

            if ((options & RubyRegexOptions.Extended) != 0) {
                result |= RegexOptions.IgnorePatternWhitespace;
            }

            if ((options & RubyRegexOptions.Multiline) != 0) {
                result |= RegexOptions.Singleline;
            }

            return result;
        }

        #region Match, LastMatch, Matches, Split

        public MatchData Match(MutableString/*!*/ input) {
            string str;
            RubyEncoding kcode = null;
            return MatchData.Create(Transform(ref kcode, input, 0, out str).Match(str), input, true, str, kcode, 0);
        }

        /// <summary>
        /// Start is a number of bytes if kcode is given, otherwise it's a number of characters.
        /// </summary>
        public MatchData Match(MutableString/*!*/ input, int start, bool freezeInput) {
            string str;
            RubyEncoding kcode = null;
            Regex regex = Transform(ref kcode, input, start, out str);

            Match match;
            if (kcode != null) {
                if (str == null) {
                    return null;
                }
                match = regex.Match(str, 0);
            } else {
                if (start < 0) {
                    start += str.Length;
                }
                if (start < 0 || start > str.Length) {
                    return null;
                }
                match = regex.Match(str, start);
            }

            return MatchData.Create(match, input, freezeInput, str, kcode, (kcode != null) ? ((start < 0) ? start + input.GetByteCount() : start) : 0);
        }

        public MatchData LastMatch(MutableString/*!*/ input) {
            return LastMatch(input, Int32.MaxValue);
        }

        /// <summary>
        /// Finds the last match whose index is less than or equal to "start".
        /// Captures are ordered in the same way as with forward match. This is different from .NET reverse matching.
        /// Start is a number of bytes if kcode is given, otherwise it's a number of characters.
        /// </summary>
        public MatchData LastMatch(MutableString/*!*/ input, int start) {
            string str;
            RubyEncoding kcode = null;
            Regex regex = Transform(ref kcode, input, 0, out str);
            Debug.Assert(str != null);

            if (kcode != null) {
                int byteCount;
                byte[] bytes = input.GetByteArray(out byteCount);

                if (start < 0) {
                    start += byteCount;
                }

                // GetCharCount returns the number of whole characters:
                start = (start >= byteCount) ? str.Length : kcode.Encoding.GetCharCount(bytes, 0, start + 1) - 1;
            } else {
                if (start < 0) {
                    start += str.Length;
                }

                if (start > str.Length) {
                    start = str.Length;
                }
            }

            Match match;
            if (_hasGAnchor) {
                // This only makes some \G anchors work. It seems that CLR doesn't support \G if preceeded by some characters.
                // For example, this works in MRI but doesn't in CLR: "abcabczzz".rindex(/.+\G.+/, 3)
                match = regex.Match(str, start);
            } else {
                match = LastMatch(regex, str, start);
                if (match == null) {
                    return null;
                }
            }
            return MatchData.Create(match, input, true, str, kcode, 0);
        }

        /// <summary>
        /// Binary searches "str" for the last match whose index is within the range [0, start].
        /// </summary>
        private static Match LastMatch(Regex/*!*/ regex, string/*!*/ input, int start) {
            Match result = null;
            int s = 0;
            int e = start;

            while (s <= e) {
                int m = (s + e) / 2;
                Match match = regex.Match(input, m);
                if (match.Success && match.Index <= e) {
                    result = match;
                    s = match.Index + 1;
                } else {
                    e = m - 1;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns a collection of fresh MatchData objects.
        /// </summary>
        public IList<MatchData>/*!*/ Matches(MutableString/*!*/ input, bool inputMayMutate) {
            string str;
            RubyEncoding kcode = null;
            MatchCollection matches = Transform(ref kcode, input, 0, out str).Matches(str);

            var result = new MatchData[matches.Count];
            if (result.Length > 0 && inputMayMutate) {
                // clone and freeze the string once so that it can be shared by all the MatchData objects
                input = input.Clone().Freeze();
            }

            for (int i = 0; i < result.Length; i++) {
                result[i] = MatchData.Create(matches[i], input, false, str, kcode, 0);
            }

            return result;
        }

        public IList<MatchData>/*!*/ Matches(MutableString/*!*/ input) {
            return Matches(input, true);
        }

        public MutableString[]/*!*/ Split(MutableString/*!*/ input) {
            string str;
            RubyEncoding kcode = null;
            return MutableString.MakeArray(Transform(ref kcode, input, 0, out str).Split(str), kcode ?? input.Encoding);
        }
        
        public MutableString[]/*!*/ Split(MutableString/*!*/ input, int count) {
            string str;
            RubyEncoding kcode = null;
            return MutableString.MakeArray(Transform(ref kcode, input, 0, out str).Split(str, count), kcode ?? input.Encoding);
        }

        public static MatchData SetCurrentMatchData(RubyScope/*!*/ scope, RubyRegex/*!*/ regex, MutableString str) {
            return scope.GetInnerMostClosureScope().CurrentMatch = (str != null) ? regex.Match(str) : null;
        }

        #endregion               

        #region ToString, Inspect

        public override string/*!*/ ToString() {
            return ToMutableString().ToString();
        }

        public MutableString/*!*/ ToMutableString() {
            return AppendTo(MutableString.CreateMutable(RubyEncoding.Binary));
        }

        public MutableString/*!*/ Inspect() {
            MutableString result = MutableString.CreateMutable(RubyEncoding.Binary);
            result.Append('/');
            AppendEscapeForwardSlash(result, _pattern);
            result.Append('/');
            AppendOptionString(result, true);
            if ((_options & RubyRegexOptions.FIXED) != 0) {
                result.Append('n');
            }
            return result;
        }

        public MutableString/*!*/ AppendTo(MutableString/*!*/ result) {
            Assert.NotNull(result);

            result.Append("(?");
            if (AppendOptionString(result, true) < 3) {
                result.Append('-');
            }
            AppendOptionString(result, false);
            result.Append(':');
            AppendEscapeForwardSlash(result, _pattern);
            result.Append(')');
            return result;
        }

        private int AppendOptionString(MutableString/*!*/ result, bool enabled) {
            int count = 0;
            var options = Options;

            if (((options & RubyRegexOptions.Multiline) != 0) == enabled) {
                result.Append('m');
                count++;
            }

            if (((options & RubyRegexOptions.IgnoreCase) != 0) == enabled) {
                result.Append('i');
                count++;
            }

            if (((options & RubyRegexOptions.Extended) != 0) == enabled) {
                result.Append('x');
                count++;
            }

            return count;
        }

        private static int SkipToUnescapedForwardSlash(MutableString/*!*/ pattern, int patternLength, int i) {
            while (i < patternLength) {
                i = pattern.IndexOf('/', i);
                if (i <= 0) {
                    return i;
                }

                // An odd number of backslashes escapes the slash; an even number (\\/) does not,
                // so that pattern doesn't get a third backslash added to it.
                int backslashes = 0;
                for (int j = i - 1; j >= 0 && pattern.GetChar(j) == '\\'; j--) {
                    backslashes++;
                }

                if (backslashes % 2 == 0) {
                    return i;
                }

                i++;
            }
            return -1;
        }

        private static MutableString/*!*/ AppendEscapeForwardSlash(MutableString/*!*/ result, MutableString/*!*/ pattern) {
            int first = 0;
            int patternLength = pattern.GetCharCount();
            int i = SkipToUnescapedForwardSlash(pattern, patternLength, 0);
            while (i >= 0) {
                Debug.Assert(i < patternLength);
                Debug.Assert(pattern.GetChar(i) == '/');

                result.Append(pattern, first, i - first);
                result.Append('\\');
                first = i; // include forward slash in the next append
                i = SkipToUnescapedForwardSlash(pattern, patternLength, i + 1);
            }

            result.Append(pattern, first, patternLength - first);
            return result;
        }

        #endregion

        #region Escape

        private const int EndOfPattern = -1;

        /// <summary>
        /// Returns a new instance of MutableString that contains escaped content of the given string.
        /// </summary>
        public static MutableString/*!*/ Escape(MutableString/*!*/ str) {
            return str.EscapeRegularExpression();
        }

        private static int SkipNonSpecial(string/*!*/ pattern, int i, out char escaped) {
            while (i < pattern.Length) {
                char c = pattern[i];
                switch (c) {
                    case '$':
                    case '^':
                    case '|':
                    case '[':
                    case ']':
                    case '(':
                    case ')':
                    case '\\':
                    case '.':
                    case '#':
                    case '-':

                    case '{':
                    case '}':
                    case '*':
                    case '+':
                    case '?':
                    case ' ':
                        escaped = c;
                        return i;

                    case '\t':
                        escaped = 't';
                        return i;

                    case '\n':
                        escaped = 'n';
                        return i;

                    case '\r':
                        escaped = 'r';
                        return i;

                    case '\f':
                        escaped = 'f';
                        return i;
                }
                i++;
            }

            escaped = '\0';
            return EndOfPattern;
        }

        internal static string/*!*/ Escape(string/*!*/ pattern) {
            StringBuilder sb = EscapeToStringBuilder(pattern);
            return (sb != null) ? sb.ToString() : pattern;
        }

        internal static StringBuilder EscapeToStringBuilder(string/*!*/ pattern) {
            int first = 0;
            char escaped;
            int i = SkipNonSpecial(pattern, 0, out escaped);

            if (i == EndOfPattern) {
                return null;
            }

            StringBuilder result = new StringBuilder(pattern.Length + 1);

            do {
                Debug.Assert(i < pattern.Length);
                // pattern[i] needs escape

                result.Append(pattern, first, i - first);
                result.Append('\\');
                result.Append(escaped);
                i++;

                Debug.Assert(i <= pattern.Length);

                first = i;
                i = SkipNonSpecial(pattern, i, out escaped);
            } while (i >= 0);

            result.Append(pattern, first, pattern.Length - first);
            return result;
        }

        #endregion
    }
}
