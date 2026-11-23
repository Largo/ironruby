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
using Microsoft.Scripting.Utils;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using IronRuby.Compiler;
using IronRuby.Runtime;
using System.Collections.Generic;

namespace IronRuby.Builtins {
    public partial class RubyRegex : IEquatable<RubyRegex>, IDuplicable, IRubyObjectState {
        // The source, frozen; in the encoding an encoding option (/e /s /u /n) names, if there is one.
        private MutableString/*!*/ _pattern;
        // HasNonAsciiEscape(_pattern) - the pattern is frozen, so it is worked out once: -1 not yet,
        // 0 none, 1 a byte escape, 2 a \u escape. Encoding and IsFixedEncoding ask on every match.
        private int _nonAsciiEscape = -1;
        // HasPropertyEscape(_pattern, extended), worked out once in the same way: -1 not yet, 0, 1.
        private int _hasProperty = -1;
        // HasLiteralNonAscii(_pattern), worked out once in the same way: -1 not yet, 0, 1.
        private int _literalNonAscii = -1;
        private RubyRegexOptions _options;
        private bool _hasGAnchor;

        // Regexp.allocate produces a regexp with no pattern at all, which MRI reports as BINARY
        // rather than as the US-ASCII an empty pattern would give.
        private bool _initialized;

        private Regex _cachedRegex;

        // The same pattern translated for a subject that holds surrogate pairs, where '.' and \B
        // must treat a pair as the one character it is (see RegexpTransformer). A subject without
        // pairs - nearly all of them - never pays for that and matches with _cachedRegex.
        private Regex _cachedAstralRegex;
        private TimeSpan _cachedAstralTimeout;

        // The same pattern translated for a match Onigmo makes with its ASCII-8BIT or US-ASCII
        // ctype table (UsesAsciiCtype) - above all a BINARY subject with bytes above 0x7F, which
        // has no letters, punctuation or case pairs there, where .NET sees U+0080-U+00FF (see
        // RegexpTransformer.Transform(..., asciiCtype)). The regex and the timeout it was made
        // with are published together, so that a thread never pairs one rebuild's regex with
        // another's timeout.
        private Tuple<TimeSpan, Regex> _cachedBinary;
        // Set once that translation has turned out to be the plain one - the pattern has no
        // property, POSIX class, \b, \R or /i Latin-1 letter - so that such a subject keeps
        // matching with _cachedRegex, which is compiled once it has been used enough.
        private bool _binaryIsPlain;

        // The same CLR regex forced to start where the search starts, for the scanning operations
        // that only ever look at the current position (StringScanner#scan, #skip, #check, #match?).
        // Cached against the regex it was derived from, so it follows _cachedRegex's own lifetime.
        private Regex _cachedAnchoredRegex;
        private Regex _cachedAnchoredSource;

        // How many matches _cachedRegex and _cachedAnchoredRegex have run while still interpreted.
        // A regex that keeps being used is rebuilt with RegexOptions.Compiled (see TierUp).
        private int _cachedRegexUses;
        private int _cachedAnchoredUses;

        /// <summary>
        /// How long a match with this pattern may run before it is abandoned, as
        /// `Regexp.new(src, timeout: seconds)' asked for. Null means the global setting applies.
        /// </summary>
        private double? _timeout;

        /// <summary>
        /// The timeout every pattern that does not carry one of its own matches under, as
        /// `Regexp.timeout=' set it. It is a property of the process in MRI too.
        /// </summary>
        private static double? _globalTimeout;

        public static double? GlobalTimeout {
            get { return _globalTimeout; }
            set { _globalTimeout = value; }
        }

        public double? Timeout {
            get { return _timeout; }
            set { _timeout = value; }
        }

        /// <summary>
        /// .NET fixes a Regex's timeout when it is constructed, so the cached one is only good
        /// while the effective timeout is the one it was built with.
        /// </summary>
        private TimeSpan _cachedTimeout;

        private TimeSpan EffectiveTimeout {
            get {
                double? seconds = _timeout ?? _globalTimeout;
                if (seconds == null || seconds.Value <= 0 || Double.IsNaN(seconds.Value)) {
                    return Regex.InfiniteMatchTimeout;
                }

                // A timeout longer than a TimeSpan can hold is no timeout at all; asking for one
                // would only throw where the matcher is built.
                if (seconds.Value >= TimeSpan.MaxValue.TotalSeconds) {
                    return Regex.InfiniteMatchTimeout;
                }
                return TimeSpan.FromSeconds(seconds.Value);
            }
        }

        private const int FrozenFlag = 1;
        // made by FromLiteralPattern
        private const int LiteralPatternFlag = 2;

        // A regexp literal is frozen by MRI, so the state has to live on the object itself rather
        // than in the context's instance data, which the literal-construction path has no access to.
        private int _flags;

        #region IRubyObjectState Members

        public bool IsFrozen {
            get { return (_flags & FrozenFlag) != 0; }
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
            // Regexp.new(re), #dup and #clone: MRI's rb_reg_init_copy checks the original
            if (!regex.IsInitialized) {
                throw RubyExceptions.CreateTypeError("uninitialized Regexp");
            }
            Set(regex.Pattern, regex.Options);
        }

        public void Set(MutableString/*!*/ pattern, RubyRegexOptions options) {
            ContractUtils.RequiresNotNull(pattern, "pattern");
            // before any state changes: an allocated regexp refused here can still be initialized
            RequireBinaryCompatibleSource(pattern, options);
            _initialized = true;

            // RubyRegexOptions.Once is only used to determine how the Regexp object should be created and cached. 
            // It is not a property of the final object. /foo/ should compare equal with /foo/o.
            _options = options & ~RubyRegexOptions.Once;
            _cachedRegex = null;
            _cachedAstralRegex = null;
            _cachedBinary = null;
            _binaryIsPlain = false;
            _nonAsciiEscape = -1;
            _hasProperty = -1;
            _literalNonAscii = -1;
            _recompiled = null;

            RubyEncoding encoding = RubyEncoding.GetRegexEncoding(options);
            if (encoding != null) {
                _pattern = MutableString.CreateBinary(pattern.ToByteArray(), encoding).Freeze();
            } else {
                _pattern = pattern.PrepareForCharacterRead().Clone().Freeze();
            }

            TransformPattern(false);
            RequireKnownProperties();
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

        /// <summary>
        /// MRI's rb_reg_prepare_enc. A broken string can never be matched, and a regexp that has
        /// pinned its encoding can only be matched against a string of that encoding - or, when the
        /// pinned encoding is ASCII compatible, against an ASCII-only string of any encoding.
        /// This is a regexp-versus-string rule, distinct from the string-versus-string
        /// compatibility MutableString.RequireCompatibleEncoding applies.
        /// </summary>
        private void RequireMatchableEncoding(MutableString/*!*/ input) {
            if ((_flags & LiteralPatternFlag) != 0) {
                // a String pattern (#sub, #gsub, #scan): MRI's rb_enc_check, receiver first
                input.RequireCompatibleEncoding(_pattern);
                return;
            }

            if (input.ContainsInvalidCharacters()) {
                throw RubyExceptions.CreateArgumentError("invalid byte sequence in {0}", input.Encoding.Name);
            }

            var patternEncoding = Encoding;
            if (input.Encoding == patternEncoding) {
                return;
            }
            // A string in an ASCII incompatible encoding (UTF-16, UTF-32) matches no regexp of
            // another encoding, ASCII only as both may be.
            if (!input.Encoding.IsAsciiIdentity ||
                IsFixedEncoding && (!patternEncoding.IsAsciiIdentity || !input.IsAscii())) {
                throw new EncodingCompatibilityError(
                    "incompatible encoding regexp match (" + patternEncoding.InspectName + " regexp with " + input.Encoding.InspectName + " string)"
                );
            }

            // MRI's rb_reg_prepare_re compiles a regexp that is not fixed again in the encoding of
            // a non-ASCII string it is matched against. A US-ASCII one can still hold bytes that are
            // not ASCII (HasLiteralNonAscii), and that fails when they are not characters there.
            // (once it has, the regexp it made is kept: RecompiledFor)
            if (IsRecompiledFor(input) && !IsRecompiledCached(input.Encoding)) {
                bool pinned;
                string error = Preprocess(_pattern.ToByteArray(), input.Encoding, (_options & RubyRegexOptions.Extended) != 0, out pinned);
                if (error != null) {
                    throw RubyExceptions.CreateArgumentError("regexp preprocess failed: " + error);
                }
            }
        }

        /// <summary>
        /// Whether the regexp is a US-ASCII one holding bytes outside ASCII (HasLiteralNonAscii)
        /// that MRI compiles again in the encoding of the non-ASCII <paramref name="input"/>
        /// (rb_reg_prepare_re), where the bytes may be other characters than in the encoding
        /// of the source: \\\x80 is U+0080 in ISO-8859-1 but U+20AC in Windows-1252.
        /// </summary>
        private bool IsRecompiledFor(MutableString/*!*/ input) {
            return input.Encoding != _pattern.Encoding && _initialized &&
                !_pattern.IsAscii() && Encoding == RubyEncoding.Ascii && !input.IsAscii();
        }

        // the regexp IsRecompiledFor made for the last encoding it was asked for; Set drops it
        private Tuple<RubyEncoding, RubyRegex> _recompiled;

        private bool IsRecompiledCached(RubyEncoding/*!*/ encoding) {
            var recompiled = _recompiled;
            return recompiled != null && recompiled.Item1 == encoding;
        }

        private RubyRegex/*!*/ RecompiledFor(MutableString/*!*/ input) {
            if (!IsRecompiledFor(input)) {
                return this;
            }
            var recompiled = _recompiled;
            if (recompiled == null || recompiled.Item1 != input.Encoding) {
                // (as Onigmo options: the encoding is the string's, /n or not)
                var regex = new RubyRegex(MutableString.CreateBinary(_pattern.ToByteArray(), input.Encoding),
                    _options & ~(RubyRegexOptions.EncodingMask | RubyRegexOptions.FixedEncoding));
                _recompiled = recompiled = Tuple.Create(input.Encoding, regex);
            }
            // the timeout is this regexp's, as it is now; the other rebuilds its CLR regex only
            // when its effective timeout changes
            recompiled.Item2.Timeout = Timeout;
            return recompiled.Item2;
        }

        /// <summary>
        /// The CLR regex to run and the text to run it on: the input's own characters. Once
        /// RequireMatchableEncoding has let the input through it is either in the regexp's
        /// encoding or ASCII only, so those are the characters the regexp means and the CLR
        /// offsets are Ruby's character offsets (but for surrogate pairs, see below), /e /s /u
        /// and /n included.
        ///
        /// <paramref name="astral"/> tells whether the text holds a surrogate pair: CLR offsets
        /// then are not character offsets, and an empty match has to step over a whole pair.
        /// <paramref name="countUse"/> is false when the caller is not going to match with the
        /// regex it gets back (MatchWindow, which derives an anchored one from it), so that only
        /// the regex that actually runs works its way up to being compiled.
        /// </summary>
        private Regex/*!*/ Transform(MutableString/*!*/ input, out string strInput, out bool astral, bool countUse) {
            ContractUtils.RequiresNotNull(input, "input");

            RequireMatchableEncoding(input);
            input.PrepareForCharacterRead();
            strInput = input.ConvertToString();
            // O(1): matching has just validated the input, which works the flags out
            astral = input.HasSurrogatePairs();

            RubyRegex target = RecompiledFor(input);
            if (astral) {
                return target.TransformAstralPattern();
            }
            if (target.UsesAsciiCtype(input)) {
                return target.TransformBinaryPattern(countUse);
            }
            return target.TransformPattern(countUse);
        }

        /// <summary>
        /// Whether MRI would match <paramref name="input"/> with Onigmo's ASCII-8BIT or US-ASCII
        /// ctype table. rb_reg_prepare_enc compiles a regexp that is not fixed-encoding for the
        /// subject's encoding, and a fixed one for its own (the subject is then in that encoding
        /// or ASCII only). Two cases differ from the plain translation: a BINARY subject with
        /// bytes above 0x7F (a US-ASCII subject that got here is ASCII), and a BINARY or US-ASCII
        /// regexp with a property, whose \p{Punct} is [[:punct:]] even on ASCII text. Any other
        /// fixed BINARY or US-ASCII regexp matches ASCII text as the plain translation does.
        /// </summary>
        private bool UsesAsciiCtype(MutableString/*!*/ input) {
            if (input.Encoding == RubyEncoding.Binary && !input.IsAscii()) {
                return true;
            }
            if (PatternHasProperty) {
                RubyEncoding encoding = Encoding;
                return encoding == RubyEncoding.Binary || encoding == RubyEncoding.Ascii;
            }
            return false;
        }

        private Regex/*!*/ TransformBinaryPattern(bool countUse) {
            TimeSpan timeout = EffectiveTimeout;
            if (_binaryIsPlain) {
                return TransformPattern(countUse);
            }
            var cached = _cachedBinary;
            if (cached != null && timeout == cached.Item1) {
                return cached.Item2;
            }

            // the plain translation first: it validates the pattern and sets _hasGAnchor
            Regex plain = TransformPattern(false);
            Regex result;
            try {
                bool hasGAnchor, applied;
                string translated = RegexpTransformer.Transform(GetClrPatternSource(), _options, out hasGAnchor, false, true, out applied);
                if (!applied) {
                    _binaryIsPlain = true;
                    return plain;
                }
                result = new Regex(translated, plain.Options, timeout);
            } catch (Exception) {
                result = plain;
            }

            _cachedBinary = Tuple.Create(timeout, result);
            return result;
        }

        private Regex/*!*/ TransformAstralPattern() {
            TimeSpan timeout = EffectiveTimeout;
            if (_cachedAstralRegex != null && timeout == _cachedAstralTimeout) {
                return _cachedAstralRegex;
            }

            // the plain translation first: it validates the pattern and sets _hasGAnchor
            Regex plain = TransformPattern(false);
            Regex result;
            try {
                bool hasGAnchor;
                result = new Regex(RegexpTransformer.Transform(GetClrPatternSource(), _options, out hasGAnchor, true), plain.Options, timeout);
            } catch (Exception) {
                result = plain;
            }

            _cachedAstralTimeout = timeout;
            _cachedAstralRegex = result;
            return result;
        }

        /// <summary>
        /// The pattern as the characters the translation reads. In an encoding other than BINARY
        /// and US-ASCII (where a byte escape means that byte) a run of \xHH or octal escapes spells
        /// characters of the pattern's encoding: /\xa4\xa2/e is "あ", one character that a
        /// quantifier after it repeats as a whole. So the run is decoded here, and refused as
        /// Onigmo refuses it when it does not decode.
        /// </summary>
        private string/*!*/ GetClrPatternSource() {
            string source = _pattern.ConvertToString();
            RubyEncoding encoding = _pattern.Encoding;
            if (encoding == RubyEncoding.Binary || encoding == RubyEncoding.Ascii || !encoding.IsAsciiIdentity || source.IndexOf('\\') < 0) {
                return source;
            }
            return DecodeByteEscapes(source, encoding);
        }

        /// <remarks>
        /// Comments are passed over undecoded, as Onigmo never reads an escape in them: a (?#...)
        /// group, and in extended mode (/x, or (?x) for the rest of a group) a # up to the end of
        /// the line outside a character class.
        /// </remarks>
        private string/*!*/ DecodeByteEscapes(string/*!*/ source, RubyEncoding/*!*/ encoding) {
            StringBuilder result = null;
            var run = new List<byte>();
            int copied = 0;
            int i = 0;
            bool extended = (_options & RubyRegexOptions.Extended) != 0;
            var groups = new Stack<bool>();
            int classDepth = 0;
            while (i < source.Length) {
                if (source[i] != '\\') {
                    i = SkipNonEscape(source, i, ref extended, groups, ref classDepth);
                    continue;
                }

                int runStart = i;
                run.Clear();
                int value, next;
                while ((value = ReadByteEscape(source, i, out next)) >= 0) {
                    run.Add((byte)value);
                    i = next;
                }

                if (run.Count == 0) {
                    // some other escape, \\ included: skip it whole
                    i += 2;
                    continue;
                }
                if (!run.Exists(b => b >= 0x80)) {
                    continue;
                }

                byte[] bytes = run.ToArray();
                string decoded;
                try {
                    decoded = encoding.StrictEncoding.GetString(bytes);
                } catch (DecoderFallbackException) {
                    throw new RegexpError((IsTruncatedCharacter(bytes, encoding)
                        ? "too short escaped multibyte character: " : "invalid multibyte escape: ") + Inspect().ToString());
                }

                result ??= new StringBuilder(source.Length);
                result.Append(source, copied, runStart - copied);
                foreach (char c in decoded) {
                    if (c < 0x80) {
                        // an ASCII byte of the run means itself, never a metacharacter
                        result.Append("\\x").Append(((int)c).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                    } else {
                        result.Append(c);
                    }
                }
                copied = i;
            }

            if (result == null) {
                return source;
            }
            return result.Append(source, copied, source.Length - copied).ToString();
        }

        /// <summary>
        /// Steps over the character at <paramref name="i"/>, which is not a backslash, or over
        /// the whole comment that starts there, keeping track of character classes, groups and
        /// whether extended mode is on. Returns where to carry on.
        /// </summary>
        private static int SkipNonEscape(string/*!*/ source, int i, ref bool extended, Stack<bool>/*!*/ groups, ref int classDepth) {
            char c = source[i];
            if (classDepth > 0) {
                if (c == '[') {
                    classDepth++;
                } else if (c == ']') {
                    classDepth--;
                }
                return i + 1;
            }

            switch (c) {
                case '[':
                    classDepth = 1;
                    return i + 1;

                case '#':
                    if (!extended) {
                        return i + 1;
                    }
                    int eol = source.IndexOf('\n', i);
                    return eol < 0 ? source.Length : eol + 1;

                case ')':
                    if (groups.Count > 0) {
                        extended = groups.Pop();
                    }
                    return i + 1;

                case '(':
                    if (i + 2 < source.Length && source[i + 1] == '?' && source[i + 2] == '#') {
                        // (?#...) ends at the first ) that is not escaped
                        int j = i + 3;
                        while (j < source.Length && source[j] != ')') {
                            j += source[j] == '\\' ? 2 : 1;
                        }
                        return Math.Min(j + 1, source.Length);
                    }

                    // (?imx-imx) sets the options for the rest of the group, (?imx-imx:...) inside it
                    if (i + 1 < source.Length && source[i + 1] == '?') {
                        int j = i + 2;
                        bool on = true, flagExtended = extended;
                        while (j < source.Length && "imxadu-".IndexOf(source[j]) >= 0) {
                            if (source[j] == '-') {
                                on = false;
                            } else if (source[j] == 'x') {
                                flagExtended = on;
                            }
                            j++;
                        }
                        if (j > i + 2 && j < source.Length && source[j] == ')') {
                            extended = flagExtended;
                            return j + 1;
                        }
                        if (j > i + 2 && j < source.Length && source[j] == ':') {
                            groups.Push(extended);
                            extended = flagExtended;
                            return j + 1;
                        }
                    }
                    groups.Push(extended);
                    return i + 1;

                default:
                    return i + 1;
            }
        }

        /// <summary>
        /// The byte a \xH, \xHH, \0, \0N, \0NN or \NNN escape at <paramref name="i"/> stands for,
        /// or -1 when there is no such escape there (\N and \NN are back references).
        /// </summary>
        private static int ReadByteEscape(string/*!*/ source, int i, out int next) {
            next = i;
            if (i + 1 >= source.Length || source[i] != '\\') {
                return -1;
            }
            char c = source[i + 1];
            int value = 0, digits = 0;
            if (c == 'x') {
                for (int j = i + 2; j < source.Length && digits < 2; j++, digits++) {
                    int d = Tokenizer.ToDigit(source[j]);
                    if (d >= 16) {
                        break;
                    }
                    value = value * 16 + d;
                }
                next = i + 2 + digits;
                return digits > 0 ? value : -1;
            }
            for (int j = i + 1; j < source.Length && digits < 3; j++, digits++) {
                int d = Tokenizer.ToDigit(source[j]);
                if (d >= 8) {
                    break;
                }
                value = value * 8 + d;
            }
            if (digits > 0 && (c == '0' || digits == 3) && value <= 0xff) {
                next = i + 1 + digits;
                return value;
            }
            return -1;
        }

        /// <summary>
        /// Whether bytes that do not decode only fail for ending in the middle of a character.
        /// </summary>
        private static bool IsTruncatedCharacter(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            try {
                encoding.StrictEncoding.GetDecoder().GetCharCount(bytes, 0, bytes.Length, false);
                return true;
            } catch (DecoderFallbackException) {
                return false;
            }
        }

        private Regex/*!*/ TransformPattern(bool countUse) {
            // The cached CLR regex is good for as long as the timeout it was made with:
            TimeSpan timeout = EffectiveTimeout;
            Regex cached = _cachedRegex;
            if (cached != null && timeout == _cachedTimeout) {
                if (countUse && (cached.Options & RegexOptions.Compiled) == 0 && ++_cachedRegexUses >= CompileThreshold) {
                    Regex interpreted = cached;
                    _cachedRegex = cached = TierUp(cached);
                    if (cached != interpreted) {
                        SharedRegex.Promote(GetClrPatternSource(), _options, interpreted, cached);
                    }
                }
                return cached;
            }

            string pattern = GetClrPatternSource();

            Regex result;
            SharedRegex shared = SharedRegex.Find(pattern, _options, timeout);
            if (shared != null) {
                result = shared.Regex;
                _hasGAnchor = shared.HasGAnchor;
            } else {
                try {
                    result = new Regex(RegexpTransformer.Transform(pattern, _options, out _hasGAnchor), ToClrOptions(_options), timeout);
                } catch (Exception e) {
                    throw TranslationError(e.Message);
                }
                SharedRegex.Add(pattern, _options, timeout, result, _hasGAnchor);
            }

            _cachedTimeout = timeout;
            _cachedRegexUses = 0;
            _cachedRegex = result;
            return result;
        }

        /// <summary>
        /// The translation quotes the pattern as its CLR characters ("error: /pattern/opts"); MRI
        /// quotes its bytes, as CreatePatternError does, which tells apart a byte that is not
        /// ASCII from \xHH and writes "/" as "\/".
        /// </summary>
        private RegexpError/*!*/ TranslationError(string/*!*/ message) {
            // the translation quotes its own input, which GetClrPatternSource may have decoded
            string tail = ": /" + GetClrPatternSource() + "/" + OptionLetters(_options);
            if (message.EndsWith(tail, StringComparison.Ordinal) && _pattern.Encoding.IsAsciiIdentity) {
                return CreatePatternError(message.Substring(0, message.Length - tail.Length), _pattern, _options);
            }
            return new RegexpError(message);
        }

        /// <summary>
        /// The CLR regexes of the patterns made last, by pattern, shared by every Regexp with the
        /// same pattern, options and timeout. Translating a pattern and having .NET parse the
        /// result is most of what Regexp.new costs, and an interpolated literal (/#{x}/) makes a
        /// new Regexp - usually of a pattern it has made before - every time it runs.
        ///
        /// A CLR Regex is immutable and safe to share between threads. Each Regexp still counts
        /// its own uses towards being compiled (TierUp); once one has been, the compiled regex
        /// replaces the interpreted one here, so the Regexps made after it start out compiled.
        /// Failures are not cached: a bad pattern is translated, and raises, every time.
        /// </summary>
        private sealed class SharedRegex {
            private readonly RubyRegexOptions _options;
            private readonly TimeSpan _timeout;
            // Another entry for the same pattern under other options or another timeout.
            private SharedRegex _next;
            internal Regex Regex;
            internal readonly bool HasGAnchor;

            // Not the static Regex cache (its key is a struct type - see Glob's matcher cache - and
            // it holds the CLR pattern, not ours).
            private static readonly Dictionary<string, SharedRegex>/*!*/ _entries = new Dictionary<string, SharedRegex>();
            private const int CacheSize = 256;

            private SharedRegex(RubyRegexOptions options, TimeSpan timeout, Regex/*!*/ regex, bool hasGAnchor) {
                _options = options;
                _timeout = timeout;
                Regex = regex;
                HasGAnchor = hasGAnchor;
            }

            internal static SharedRegex Find(string/*!*/ pattern, RubyRegexOptions options, TimeSpan timeout) {
                lock (_entries) {
                    SharedRegex entry;
                    if (_entries.TryGetValue(pattern, out entry)) {
                        for (; entry != null; entry = entry._next) {
                            if (entry._options == options && entry._timeout == timeout) {
                                return entry;
                            }
                        }
                    }
                }
                return null;
            }

            internal static void Add(string/*!*/ pattern, RubyRegexOptions options, TimeSpan timeout, Regex/*!*/ regex, bool hasGAnchor) {
                var entry = new SharedRegex(options, timeout, regex, hasGAnchor);
                lock (_entries) {
                    if (_entries.Count >= CacheSize) {
                        _entries.Clear();
                    }
                    SharedRegex existing;
                    entry._next = _entries.TryGetValue(pattern, out existing) ? existing : null;
                    _entries[pattern] = entry;
                }
            }

            /// <summary>Puts the compiled form of <paramref name="interpreted"/> in its place.</summary>
            internal static void Promote(string/*!*/ pattern, RubyRegexOptions options, Regex/*!*/ interpreted, Regex/*!*/ compiled) {
                lock (_entries) {
                    SharedRegex entry;
                    if (_entries.TryGetValue(pattern, out entry)) {
                        for (; entry != null; entry = entry._next) {
                            if (entry._options == options && entry.Regex == interpreted) {
                                entry.Regex = compiled;
                                return;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Matches a regex runs before it is compiled. The CLR interprets a Regex unless told to
        /// compile it, and compiling costs a millisecond or more - about what the compiled code
        /// then saves over ten thousand matches of a typical pattern - so only a regex that has
        /// already shown it is hot pays for it: one in a loop over a large input, a tokenizer's.
        /// </summary>
        private const int CompileThreshold = 10000;

        /// <summary>
        /// The same CLR regex compiled to IL. Compiled and interpreted regexes are the same
        /// engine and the same pattern tree and match identically; only the speed differs. If it
        /// cannot be built for any reason, the interpreted one is kept.
        /// </summary>
        private static Regex/*!*/ TierUp(Regex/*!*/ regex) {
            try {
                return new Regex(regex.ToString(), regex.Options | RegexOptions.Compiled, regex.MatchTimeout);
            } catch (Exception) {
                return regex;
            }
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

                // Regexp::FIXEDENCODING asks for the source's encoding to be kept, ASCII only
                // or not: that is the whole of what it says.
                if ((_options & RubyRegexOptions.FixedEncoding) != 0) {
                    return _pattern.Encoding;
                }

                var encodingOptions = _options & RubyRegexOptions.EncodingMask;
                if (encodingOptions == RubyRegexOptions.NONE && !PatternHasLiteralNonAscii) {
                    // é is six ASCII characters that compile to one UTF-8 character, so the
                    // regexp is UTF-8 even though its source is ASCII only. (A \xNN or octal escape
                    // above 7 bits without /n is "invalid multibyte escape" in MRI; we don't raise,
                    // and the pattern's own encoding is as good an answer as any.)
                    bool isUnicode;
                    if (PatternHasNonAsciiEscape(out isUnicode)) {
                        return isUnicode ? RubyEncoding.UTF8 : _pattern.Encoding;
                    }

                    // A character property pins the encoding the source was written in (MRI's
                    // rb_reg_preprocess, has_property): /\p{Alpha}/ is UTF-8 in a UTF-8 file.
                    if (PatternHasProperty) {
                        return _pattern.Encoding;
                    }

                    return RubyEncoding.Ascii;
                }

                if (encodingOptions == RubyRegexOptions.FIXED && !PatternHasLiteralNonAscii && !PatternHasNonAsciiEscape(out _) &&
                    !PatternHasProperty) {
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
        private bool PatternHasNonAsciiEscape(out bool isUnicode) {
            int cached = _nonAsciiEscape;
            if (cached < 0) {
                cached = HasNonAsciiEscape(_pattern, (_options & RubyRegexOptions.Extended) != 0, out isUnicode) ? (isUnicode ? 2 : 1) : 0;
                _nonAsciiEscape = cached;
            }
            isUnicode = cached == 2;
            return cached != 0;
        }

        private bool PatternHasLiteralNonAscii {
            get {
                int cached = _literalNonAscii;
                if (cached < 0) {
                    cached = HasLiteralNonAscii(_pattern, (_options & RubyRegexOptions.Extended) != 0) ? 1 : 0;
                    _literalNonAscii = cached;
                }
                return cached != 0;
            }
        }

        /// <summary>
        /// Whether the pattern holds a non-ASCII character that pins the regexp to the encoding of
        /// its source, as MRI's unescape_nonascii decides it (Preprocess): not a single byte right
        /// after a backslash, so Regexp.new("\\\xFF".b) is US-ASCII and not fixed, and not one in
        /// a comment outside UTF-8. An ASCII incompatible source pins its encoding whatever it
        /// holds (rb_reg_preprocess).
        /// </summary>
        internal static bool HasLiteralNonAscii(MutableString/*!*/ pattern, bool extended) {
            if (pattern.IsAscii()) {
                return false;
            }
            if (!pattern.Encoding.IsAsciiIdentity) {
                return true;
            }
            bool pinned;
            Preprocess(pattern.ToByteArray(), pattern.Encoding, extended, out pinned);
            return pinned;
        }

        private bool PatternHasProperty {
            get {
                int cached = _hasProperty;
                if (cached < 0) {
                    cached = HasPropertyEscape(_pattern, (_options & RubyRegexOptions.Extended) != 0) ? 1 : 0;
                    _hasProperty = cached;
                }
                return cached != 0;
            }
        }

        /// <summary>
        /// Whether the pattern names a character property (\p{...}, \P{...}) outside a comment,
        /// which MRI's unescape_nonascii notes as has_property: such a regexp keeps the encoding
        /// of its source even when that source is ASCII only. A (?#...) comment, and a # comment
        /// in extended mode outside a character class, do not count.
        /// </summary>
        public static bool HasPropertyEscape(MutableString/*!*/ pattern, bool extended) {
            return PropertyNames(pattern, extended).GetEnumerator().MoveNext();
        }

        /// <summary>
        /// The names of the character properties the pattern uses (HasPropertyEscape), as written
        /// between the braces, a leading ^ left out; "" for a \p with no braces.
        /// </summary>
        private static IEnumerable<string>/*!*/ PropertyNames(MutableString/*!*/ pattern, bool extended) {
            int length = pattern.GetCharCount();
            int inClass = 0;
            var groups = new Stack<int>();
            for (int i = 0; i < length; i++) {
                char c = pattern.GetChar(i);
                if (c == '\\') {
                    if (i + 1 < length) {
                        char next = pattern.GetChar(i + 1);
                        if (next == 'p' || next == 'P') {
                            var name = new StringBuilder();
                            int j = i + 2;
                            if (j < length && pattern.GetChar(j) == '{') {
                                j++;
                                if (j < length && pattern.GetChar(j) == '^') {
                                    j++;
                                }
                                while (j < length && pattern.GetChar(j) != '}') {
                                    name.Append(pattern.GetChar(j++));
                                }
                            }
                            yield return name.ToString();
                        }
                    }
                    // the escaped character is not looked at again
                    i++;
                } else if (c == '[') {
                    inClass++;
                } else if (c == ']') {
                    if (inClass > 0) {
                        inClass--;
                    }
                } else if (c == '#' && extended && inClass == 0) {
                    while (i < length && pattern.GetChar(i) != '\n') {
                        i++;
                    }
                } else if (c == '(' && inClass == 0) {
                    if (i + 2 < length && pattern.GetChar(i + 1) == '?' && pattern.GetChar(i + 2) == '#') {
                        // a comment runs to the first ')' that is not escaped
                        for (i += 3; i < length && pattern.GetChar(i) != ')'; i++) {
                            if (pattern.GetChar(i) == '\\') {
                                i++;
                            }
                        }
                        continue;
                    }
                    // As MRI's unescape_nonascii: (?x) and (?-x) (the options a, d, u, i and m may
                    // stand with x) turn extended mode on or off for the rest of the pattern, or of
                    // the (?x:...) group around them; (?x:...) and (?-x:...) only inside their own
                    // group, when they change it. Any other group leaves the mode alone, its end
                    // included: -1 on the stack.
                    int restore = -1;
                    if (i + 1 < length && pattern.GetChar(i + 1) == '?') {
                        bool on = true, inner = extended;
                        int j = i + 2;
                        for (; j < length; j++) {
                            char option = pattern.GetChar(j);
                            if (option == '-') {
                                on = false;
                            } else if (option == 'x') {
                                inner = on;
                            } else if ("imadu".IndexOf(option) < 0) {
                                break;
                            }
                        }
                        if (j < length && pattern.GetChar(j) == ')') {
                            // (?x): no group to come back from
                            extended = inner;
                            i = j;
                            continue;
                        }
                        if (j < length && pattern.GetChar(j) == ':' && inner != extended) {
                            restore = extended ? 1 : 0;
                            extended = inner;
                            i = j;
                        }
                    }
                    groups.Push(restore);
                } else if (c == ')' && inClass == 0) {
                    if (groups.Count > 0) {
                        int restore = groups.Pop();
                        if (restore >= 0) {
                            extended = restore == 1;
                        }
                    }
                }
            }
        }

        // The property names Onigmo knows in an encoding without Unicode's tables
        // (onigenc_minimum_property_name_to_ctype): the POSIX classes, Word and ASCII.
        private static readonly HashSet<string>/*!*/ _minimumPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Alnum", "Alpha", "Blank", "Cntrl", "Digit", "Graph", "Lower", "Print", "Punct", "Space", "Upper", "XDigit",
            "Word", "ASCII",
        };

        // The Japanese encodings also know these scripts (enc/euc_jp.c and enc/shift_jis.c's
        // property_name_to_ctype), as do their replicas.
        private static readonly HashSet<string>/*!*/ _japanesePropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Hiragana", "Katakana", "Han", "Latin", "Greek", "Cyrillic",
        };

        private static readonly HashSet<string>/*!*/ _japaneseEncodings = new HashSet<string>(StringComparer.Ordinal) {
            "EUC-JP", "eucJP-ms", "CP51932", "EUC-JIS-2004", "Shift_JIS", "Windows-31J", "MacJapanese",
            "SJIS-DoCoMo", "SJIS-KDDI", "SJIS-SoftBank",
        };

        /// <summary>
        /// A regexp in an encoding without Unicode's tables knows only a few property names, and
        /// Onigmo refuses the rest when it compiles: Regexp.new('\p{L}'.encode('US-ASCII')) is
        /// "invalid character property name {L}". The transformer, which does not know the
        /// encoding, accepts every name Onigmo's Unicode tables have.
        /// </summary>
        private void RequireKnownProperties() {
            if (!PatternHasProperty) {
                return;
            }
            var encoding = Encoding;
            if (encoding.HasUnicodeCodePoints) {
                return;
            }
            bool japanese = _japaneseEncodings.Contains(encoding.Name);
            foreach (string name in PropertyNames(_pattern, (_options & RubyRegexOptions.Extended) != 0)) {
                if (!_minimumPropertyNames.Contains(name) && !(japanese && _japanesePropertyNames.Contains(name))) {
                    throw new RegexpError("invalid character property name {" + name + "}: " + Inspect().ToString());
                }
            }
        }

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
            return HasNonAsciiEscape(pattern, false, out isUnicode);
        }

        /// <summary>
        /// As above, in or out of extended mode: as MRI's unescape_nonascii (Preprocess) reads no
        /// escape in a comment, (?#...) or, in extended mode, # to the end of the line.
        /// </summary>
        public static bool HasNonAsciiEscape(MutableString/*!*/ pattern, bool extended, out bool isUnicode) {
            if (pattern.Encoding.IsAsciiIdentity) {
                bool pinned;
                int escape;
                Preprocess(pattern.ToByteArray(), pattern.Encoding, extended, out pinned, out escape);
                isUnicode = escape == 2;
                return escape != 0;
            }
            return HasNonAsciiEscapeInCharacters(pattern, out isUnicode);
        }

        private static bool HasNonAsciiEscapeInCharacters(MutableString/*!*/ pattern, out bool isUnicode) {
            isUnicode = false;
            int length = pattern.GetCharCount();
            int classDepth = 0;
            for (int i = 0; i < length - 1; i++) {
                char current = pattern.GetChar(i);
                if (current != '\\') {
                    // MRI's unescape_nonascii reads no escape in a (?#...) comment
                    if (current == '[') {
                        classDepth++;
                    } else if (current == ']' && classDepth > 0) {
                        classDepth--;
                    } else if (current == '(' && classDepth == 0 && i + 2 < length && pattern.GetChar(i + 1) == '?' &&
                        pattern.GetChar(i + 2) == '#') {
                        int end = i + 3;
                        while (end < length && pattern.GetChar(end) != ')') {
                            end += pattern.GetChar(end) == '\\' ? 2 : 1;
                        }
                        if (end < length) {
                            i = end;
                        }
                    }
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
                    bool isUnicode;
                    // (Marshal keeps MRI's FIXEDENCODING bit for a /n regexp it was set on.)
                    return PatternHasLiteralNonAscii || PatternHasNonAsciiEscape(out isUnicode) || PatternHasProperty ||
                        (_options & RubyRegexOptions.FixedEncoding) != 0;
                }

                return (_options & (RubyRegexOptions.EUC | RubyRegexOptions.SJIS | RubyRegexOptions.UTF8 | RubyRegexOptions.FixedEncoding)) != 0
                    || Encoding != RubyEncoding.Ascii || PatternHasProperty;
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
            // MRI's rb_reg_equal compares the sources' bytes, the regexps' encodings being the same.
            // The patterns can be tagged differently all the same: /n keeps the pattern as binary
            // where the same literal without it keeps the source encoding, and a US-ASCII regexp
            // holding bytes outside ASCII (HasLiteralNonAscii) keeps the encoding of whatever
            // source it was made from.
            if (x.Equals(y)) {
                return true;
            }
            if (x.IsAscii() && y.IsAscii()) {
                return x.ToString() == y.ToString();
            }
            byte[] xBytes = x.ToByteArray(), yBytes = y.ToByteArray();
            if (xBytes.Length != yBytes.Length) {
                return false;
            }
            for (int i = 0; i < xBytes.Length; i++) {
                if (xBytes[i] != yBytes[i]) {
                    return false;
                }
            }
            return true;
        }

        public override bool Equals(object other) {
            return Equals(other as RubyRegex);
        }

        public override int GetHashCode() {
            int pattern;
            if (_pattern.IsAscii()) {
                pattern = _pattern.ToString().GetHashCode();
            } else {
                // as PatternEquals: the bytes, whatever the pattern is tagged with
                pattern = 0;
                foreach (byte b in _pattern.ToByteArray()) {
                    pattern = pattern * 31 + b;
                }
            }
            return pattern ^ (int)(_options & BehaviouralOptions);
        }

        public static RegexOptions ToClrOptions(RubyRegexOptions options) {
            RegexOptions result = RegexOptions.Multiline | RegexOptions.CultureInvariant;

#if DEBUG
            if (RubyOptions.CompileRegexps) {
                result |= RegexOptions.Compiled;
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

        /// <summary>
        /// Ruby's pos argument - Regexp#match(str, pos), String#index(re, pos), #match? - is a
        /// character index, negative from the end, while Match and LastMatch take the index
        /// space the regex runs in: the CLR chars of the string. Answers false when the
        /// position is outside the string.
        /// </summary>
        private bool ToSearchStart(MutableString/*!*/ input, ref int start, bool clamp) {
            if (start == 0 || input.KnowsCharIndexIsClrIndex) {
                return true;
            }
            input.PrepareForCharacterRead();
            if (!input.HasSurrogatePairs()) {
                return true;
            }

            int length = input.GetCharacterCount();
            if (start < 0) {
                start += length;
                if (start < 0) {
                    return false;
                }
            }
            if (start > length) {
                if (!clamp) {
                    return false;
                }
                start = length;
            }

            start = input.ToClrIndex(start);
            return true;
        }

        /// <summary>
        /// Match starting at Ruby character index <paramref name="start"/> (negative counts from
        /// the end). Null if there is no match or the start is outside the string.
        /// </summary>
        public MatchData MatchFromCharacter(MutableString/*!*/ input, int start, bool freezeInput) {
            return MatchFromCharacter(input, start, freezeInput, false);
        }

        /// <summary>
        /// With <paramref name="clampToEnd"/> a start past the end searches from the end, which is
        /// what Regexp#match and String#match do (MRI's rb_str_offset stops at the end); #index
        /// and #match? answer nil resp. false instead.
        /// </summary>
        public MatchData MatchFromCharacter(MutableString/*!*/ input, int start, bool freezeInput, bool clampToEnd) {
            if (clampToEnd && start > 0) {
                int length = input.GetCharacterCount();
                if (start > length) {
                    start = length;
                }
            }
            if (!ToSearchStart(input, ref start, false)) {
                return null;
            }
            return Match(input, start, freezeInput);
        }

        /// <summary>
        /// The last match starting at or before Ruby character index <paramref name="start"/>.
        /// </summary>
        public MatchData LastMatchFromCharacter(MutableString/*!*/ input, int start) {
            if (start != Int32.MaxValue && !ToSearchStart(input, ref start, true)) {
                return null;
            }
            return LastMatch(input, start);
        }


        public MatchData Match(MutableString/*!*/ input) {
            string str;
            bool astral;
            Regex regex = Transform(input, out str, out astral, true);
            return MatchData.Create(astral ? MatchOutsidePairs(regex, str, 0) : regex.Match(str), input, true, str, this, astral, regex);
        }

        /// <summary>
        /// A search in a subject with surrogate pairs: .NET tries every UTF-16 position in turn,
        /// the one between the two halves of a pair included, and a match can start there - a loop
        /// over a class that takes in both halves ([^"]*, .*), \B, a lookaround. None of those
        /// are positions Ruby has, so such a match is dropped and the search goes on from the
        /// next character. (Only a match nothing before it could make gets that far.)
        /// </summary>
        private static Match/*!*/ MatchOutsidePairs(Regex/*!*/ regex, string/*!*/ str, int start) {
            while (true) {
                Match match = regex.Match(str, start);
                if (!match.Success || !IsInsidePair(str, match.Index)) {
                    return match;
                }
                start = match.Index + 1;
            }
        }

        /// <summary>
        /// The same over a window of the subject. A window's start is where \A matches, so the
        /// search cannot be restarted past the dropped match with a narrower window; it goes on
        /// with NextMatch instead, inside the same window.
        /// </summary>
        private static Match/*!*/ MatchOutsidePairs(Regex/*!*/ regex, string/*!*/ str, int start, int length) {
            Match match = regex.Match(str, start, length);
            while (match.Success && IsInsidePair(str, match.Index)) {
                match = match.NextMatch();
            }
            return match;
        }

        internal static bool IsInsidePair(string/*!*/ str, int index) {
            return index > 0 && index < str.Length && Char.IsLowSurrogate(str[index]) && Char.IsHighSurrogate(str[index - 1]);
        }

        /// <summary>
        /// Start is a CLR index into the input's characters.
        /// </summary>
        public MatchData Match(MutableString/*!*/ input, int start, bool freezeInput) {
            string str;
            bool astral;
            Regex regex = Transform(input, out str, out astral, true);

            if (start < 0) {
                start += str.Length;
            }
            if (start < 0 || start > str.Length) {
                return null;
            }
            Match match = astral ? MatchOutsidePairs(regex, str, start) : regex.Match(str, start);
            return MatchData.Create(match, input, freezeInput, str, this, astral, regex);
        }

        /// <summary>
        /// Matches the window that starts at <paramref name="start"/> and runs to the end of the
        /// input, as if the rest of the string were the whole subject: \A, ^ and \z anchor to the
        /// window, not to the string it was cut from. That is what StringScanner needs, and taking
        /// a window costs nothing, where copying the rest of the subject out into a string of its
        /// own - which is how the scanner used to get those semantics - costs a pass over it on
        /// every single scan.
        ///
        /// With <paramref name="anchored"/> the match has to begin at the window's first character
        /// rather than anywhere in it, which is what Onigmo's onig_match does for MRI's scanning
        /// operations. Searching instead and throwing the result away when it started too late is
        /// the same answer, but it reads the whole rest of the subject to produce it.
        ///
        /// Offsets in the returned MatchData are relative to the whole input, as with #Match.
        /// Start is a CLR index into the input's characters.
        /// </summary>
        public MatchData MatchWindow(MutableString/*!*/ input, int start, bool anchored, bool freezeInput) {
            // Convert the whole input, not the part from "start" on: the conversion is the
            // expensive half of a match on a long subject, and converting only the tail means
            // paying for the tail again at every position the scanner stops at.
            string str;
            bool astral;
            Regex regex = Transform(input, out str, out astral, !anchored);

            if (start < 0) {
                start += str.Length;
            }
            if (start < 0 || start > str.Length) {
                return null;
            }

            if (anchored) {
                regex = AnchorAtSearchStart(regex);
            }

            Match match = astral ? MatchOutsidePairs(regex, str, start, str.Length - start) : regex.Match(str, start, str.Length - start);
            return MatchData.Create(match, input, freezeInput, str, this, astral, regex);
        }

        /// <summary>
        /// A match MatchWindowBounds found, whose MatchData has not been made yet. Most scanning
        /// operations only want to know where the match is, and building the CLR Match with its
        /// groups and the MatchData around it costs more than finding the match does.
        /// <see cref="Complete"/> makes the MatchData MatchWindow would have made, by running the
        /// same CLR regex over the same immutable text again - so a later change to the subject
        /// does not change the groups, as it does not in MRI either.
        /// </summary>
        public struct PendingMatch {
            internal RubyRegex _owner;
            internal Regex _regex;
            internal MutableString _input;
            internal string _text;
            internal int _start;
            internal MatchData _data;

            public bool IsEmpty {
                get { return _owner == null; }
            }

            public MatchData/*!*/ Complete() {
                if (_data != null) {
                    return _data;
                }
                Match match = _regex.Match(_text, _start, _text.Length - _start);
                Debug.Assert(match.Success);
                return MatchData.Create(match, _input, false, _text, _owner, false, _regex);
            }
        }

        /// <summary>
        /// MatchWindow without a frozen copy of the input, answering the byte offsets of the whole
        /// match and a <see cref="PendingMatch"/> for its groups. Where one CLR character stands
        /// for one byte of the input - a subject whose characters are all single bytes - the
        /// match is found without building any match objects at all; anything else goes
        /// through MatchWindow.
        /// </summary>
        public bool MatchWindowBounds(MutableString/*!*/ input, int start, bool anchored,
            out int matchStart, out int matchEnd, out PendingMatch pending) {

            matchStart = matchEnd = 0;
            pending = default(PendingMatch);

            string str;
            bool astral;
            Regex regex = Transform(input, out str, out astral, !anchored);

            if (astral || !input.DetectSingleByteCharacters()) {
                MatchData data = MatchWindow(input, start, anchored, false);
                if (data == null || !data.TryGetGroupByteOffsets(0, out matchStart, out matchEnd)) {
                    return false;
                }
                pending._owner = this;
                pending._data = data;
                return true;
            }

            if (start < 0) {
                start += str.Length;
            }
            if (start < 0 || start > str.Length) {
                return false;
            }

            if (anchored) {
                regex = AnchorAtSearchStart(regex);
            }

            // A span cut at "start" is the window Match(str, start, length) searches: \A, ^, \G
            // and lookbehind all stop at its first character.
            foreach (ValueMatch match in regex.EnumerateMatches(str.AsSpan(start))) {
                int clrStart = start + match.Index;
                // MatchData's offsets: the CLR offset is the byte offset, capped at the subject
                int byteCount = Math.Min(str.Length, input.GetByteCount());
                matchStart = Math.Min(clrStart, byteCount);
                matchEnd = Math.Min(clrStart + match.Length, byteCount);
                pending._owner = this;
                pending._regex = regex;
                pending._input = input;
                pending._text = str;
                pending._start = start;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The same pattern with a \G in front of it, which in a forward CLR match stands for the
        /// position the search was told to start at. The body goes in a non-capturing group so that
        /// a top-level alternation still binds inside the anchor, and group numbers do not move.
        ///
        /// Under IgnorePatternWhitespace a trailing "#" comment would otherwise swallow the closing
        /// parenthesis, so the group is closed on a line of its own - a newline that only exists in
        /// the mode that ignores it.
        ///
        /// If the wrapped pattern will not compile for some reason the original is returned, and
        /// the caller falls back to searching: slower, never wrong.
        /// </summary>
        private Regex/*!*/ AnchorAtSearchStart(Regex/*!*/ regex) {
            Regex cached = _cachedAnchoredRegex;
            if (_cachedAnchoredSource == regex && cached != null) {
                if ((cached.Options & RegexOptions.Compiled) == 0 && ++_cachedAnchoredUses >= CompileThreshold) {
                    _cachedAnchoredRegex = cached = TierUp(cached);
                }
                return cached;
            }

            Regex result;
            try {
                string body = regex.ToString();
                string pattern = ((regex.Options & RegexOptions.IgnorePatternWhitespace) != 0)
                    ? "\\G(?:" + body + "\n)"
                    : "\\G(?:" + body + ")";
                result = new Regex(pattern, regex.Options, regex.MatchTimeout);
            } catch (Exception) {
                result = regex;
            }

            _cachedAnchoredSource = regex;
            _cachedAnchoredUses = 0;
            _cachedAnchoredRegex = result;
            return result;
        }

        public MatchData LastMatch(MutableString/*!*/ input) {
            return LastMatch(input, Int32.MaxValue);
        }

        /// <summary>
        /// Finds the last match whose index is less than or equal to "start".
        /// Captures are ordered in the same way as with forward match. This is different from .NET reverse matching.
        /// Start is a CLR index into the input's characters.
        /// </summary>
        public MatchData LastMatch(MutableString/*!*/ input, int start) {
            string str;
            bool astral;
            Regex regex = Transform(input, out str, out astral, true);

            if (start < 0) {
                start += str.Length;
            }
            if (start > str.Length) {
                start = str.Length;
            }

            if (astral && IsInsidePair(str, start)) {
                // no match can start between the halves of a pair
                start--;
            }

            Match match;
            // the regexp that ran: one compiled again for the input's encoding can read \G where
            // this one does not (RecompiledFor)
            if (RecompiledFor(input)._hasGAnchor) {
                match = LastMatchWithGAnchor(regex, str, start, astral);
                if (match == null) {
                    return null;
                }
            } else {
                match = LastMatch(regex, str, start, astral);
                if (match == null) {
                    return null;
                }
            }
            return MatchData.Create(match, input, true, str, this, astral, regex);
        }

        /// <summary>
        /// A backward search in MRI tries each position from "start" down to 0, and \G stands for "start"
        /// itself rather than for the position being tried. CLR's \G is always the position the match
        /// starts at, so \G becomes a lookbehind for exactly "start" characters and a candidate position
        /// only counts when the leftmost match from it starts right there.
        /// </summary>
        private static Match LastMatchWithGAnchor(Regex/*!*/ regex, string/*!*/ input, int start, bool astral) {
            string pattern = regex.ToString();
            var sb = new StringBuilder();
            for (int i = 0; i < pattern.Length; i++) {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length) {
                    if (pattern[i + 1] == 'G') {
                        sb.Append("(?<=\\A[\\s\\S]{").Append(start).Append("})");
                    } else {
                        sb.Append(c).Append(pattern[i + 1]);
                    }
                    i++;
                } else {
                    sb.Append(c);
                }
            }

            var rewritten = new Regex(sb.ToString(), regex.Options, regex.MatchTimeout);
            for (int p = start; p >= 0; p--) {
                if (astral && IsInsidePair(input, p)) {
                    continue;
                }
                Match match = rewritten.Match(input, p);
                if (match.Success && match.Index == p) {
                    return match;
                }
            }
            return null;
        }

        /// <summary>
        /// Binary searches "str" for the last match whose index is within the range [0, start].
        /// </summary>
        private static Match LastMatch(Regex/*!*/ regex, string/*!*/ input, int start, bool astral) {
            Match result = null;
            int s = 0;
            int e = start;

            while (s <= e) {
                int m = (s + e) / 2;
                Match match = astral ? MatchOutsidePairs(regex, input, m) : regex.Match(input, m);
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
            bool astral;
            Regex regex = Transform(input, out str, out astral, true);
            IList<Match> matches;
            if (astral) {
                matches = MatchesOverPairs(regex, str);
            } else {
                // Match and NextMatch are what Regex.Matches is defined as; asking for the first
                // one directly answers "no match" - the usual outcome of a #gsub over a short
                // string - without the collection.
                Match match = regex.Match(str);
                if (!match.Success) {
                    return Array.Empty<MatchData>();
                }
                var list = new List<Match>();
                do {
                    list.Add(match);
                    match = match.NextMatch();
                } while (match.Success);
                matches = list;
            }

            var result = new MatchData[matches.Count];
            if (result.Length > 0 && inputMayMutate) {
                // clone and freeze the string once so that it can be shared by all the MatchData objects
                input = input.CreateFrozenSnapshot();
            }

            for (int i = 0; i < result.Length; i++) {
                result[i] = MatchData.Create(matches[i], input, false, str, this, astral, regex);
            }

            return result;
        }

        /// <summary>
        /// Regex.Matches, except that the search after an empty match resumes past the whole
        /// character it stopped at: .NET moves on by one UTF-16 unit, which inside a surrogate
        /// pair would find an empty match between its halves ("\u{1F600}".scan(/x*/) would be
        /// three matches, not two).
        /// </summary>
        private static List<Match>/*!*/ MatchesOverPairs(Regex/*!*/ regex, string/*!*/ str) {
            var result = new List<Match>();
            Match match = MatchOutsidePairs(regex, str, 0);
            while (match.Success) {
                result.Add(match);
                int next = match.Index + match.Length;
                if (match.Length == 0) {
                    if (next >= str.Length) {
                        break;
                    }
                    next = MutableString.NextCharacterClrIndex(str, next);
                }
                match = MatchOutsidePairs(regex, str, next);
            }
            return result;
        }

        public IList<MatchData>/*!*/ Matches(MutableString/*!*/ input) {
            return Matches(input, true);
        }

        public static MatchData SetCurrentMatchData(RubyScope/*!*/ scope, RubyRegex/*!*/ regex, MutableString str) {
            if (str != null) {
                // MRI's rb_reg_search checks the regexp (rb_reg_check), a nil subject does not
                if (!regex.IsInitialized) {
                    throw RubyExceptions.CreateTypeError("uninitialized Regexp");
                }
                regex.WarnHistoricalBinaryMatch(scope.RubyContext, str);
            }
            return scope.GetInnerMostClosureScope().CurrentMatch = (str != null) ? regex.Match(str) : null;
        }

        /// <summary>
        /// MRI's rb_reg_prepare_enc: a /n regexp that has not pinned an encoding still matches a
        /// non-ASCII string of another encoding, byte by byte, and warns that it does - once it has
        /// refused a broken string, which it does first.
        /// </summary>
        public void WarnHistoricalBinaryMatch(RubyContext/*!*/ context, MutableString/*!*/ str) {
            if ((_options & RubyRegexOptions.EncodingMask) == RubyRegexOptions.FIXED && !IsFixedEncoding &&
                str.Encoding != RubyEncoding.Binary && str.Encoding.IsAsciiIdentity && !str.IsAscii() && !str.ContainsInvalidCharacters()) {
                context.ReportWarning(String.Format("historical binary regexp match /.../n against {0} string", str.Encoding.Name));
            }
        }

        #endregion               

        #region ToString, Inspect

        public override string/*!*/ ToString() {
            return ToMutableString().ToString();
        }

        public MutableString/*!*/ ToMutableString() {
            return InRegexEncoding(AppendTo(MutableString.CreateMutable(RubyEncoding.Binary)));
        }

        /// <summary>
        /// The pattern as #inspect, #to_s and #source of a union or an interpolation write it. It
        /// can hold bytes that are no characters of the regexp's encoding: a US-ASCII regexp
        /// bytes outside ASCII (HasLiteralNonAscii), and one in extended mode anything in a
        /// comment. MRI's rb_reg_expr_str keeps such a byte right after a backslash, with the
        /// backslash, and writes any other as \xHH, so that /(?#\xFF)a/ made from BINARY is
        /// "(?-mix:(?#\\xFF)a)" and ASCII only.
        /// </summary>
        public MutableString/*!*/ TextPattern {
            get {
                if (_pattern.IsAscii() || !_initialized) {
                    return _pattern;
                }
                RubyEncoding encoding = Encoding;
                if (!encoding.IsAsciiIdentity || encoding != RubyEncoding.Ascii &&
                    (encoding == RubyEncoding.Binary || encoding != _pattern.Encoding || !_pattern.ContainsInvalidCharacters())) {
                    return _pattern;
                }

                byte[] bytes = _pattern.ToByteArray();
                var result = new List<byte>(bytes.Length + 8);
                int i = 0;
                while (i < bytes.Length) {
                    byte b = bytes[i];
                    int length;
                    if (b == '\\' && i + 1 < bytes.Length) {
                        length = 1 + Math.Max(CharacterLength(bytes, i + 1, encoding), 1);
                    } else if ((length = CharacterLength(bytes, i, encoding)) < 0) {
                        AppendAscii(result, String.Format(CultureInfo.InvariantCulture, "\\x{0:X2}", b));
                        i++;
                        continue;
                    }
                    for (int j = 0; j < length && i < bytes.Length; j++) {
                        result.Add(bytes[i++]);
                    }
                }
                return MutableString.CreateBinary(result.ToArray(), _pattern.Encoding);
            }
        }

        // the pattern's bytes tagged with the regexp's encoding, which they need not be (TextPattern)
        private MutableString/*!*/ PatternInOwnEncoding {
            get {
                RubyEncoding encoding = Encoding;
                return encoding.IsAsciiIdentity && encoding != _pattern.Encoding && !_pattern.IsAscii() ?
                    MutableString.CreateBinary(_pattern.ToByteArray(), encoding) : _pattern;
            }
        }

        public MutableString/*!*/ Inspect() {
            return Inspect(null);
        }

        /// <param name="resultEncoding">
        /// The encoding the text is meant for, Encoding.default_internal or else default_external
        /// (rb_reg_desc's resenc), or null to write the pattern's characters as they are.
        /// </param>
        public MutableString/*!*/ Inspect(RubyEncoding resultEncoding) {
            MutableString result = MutableString.CreateMutable(RubyEncoding.Binary);
            result.Append('/');
            if (resultEncoding != null) {
                // read in the regexp's own encoding: a US-ASCII regexp can hold bytes outside ASCII
                // (HasLiteralNonAscii) in a pattern tagged with the encoding of its source
                AppendForResultEncoding(result, PatternInOwnEncoding, Encoding, resultEncoding);
            } else {
                AppendEscapeForwardSlash(result, TextPattern);
            }
            result.Append('/');
            AppendOptionString(result, true);
            if ((_options & RubyRegexOptions.FIXED) != 0) {
                result.Append('n');
            }
            return InRegexEncoding(result);
        }

        // MRI's rb_reg_to_s and rb_reg_inspect copy the regexp's encoding to the text: /x/.to_s
        // is US-ASCII and /\p{L}/.to_s UTF-8, which is what keeps /#{/\p{L}/}/ UTF-8. Text with
        // non-ASCII characters already carries the pattern's encoding.
        private MutableString/*!*/ InRegexEncoding(MutableString/*!*/ text) {
            var encoding = Encoding;
            // A US-ASCII regexp can still hold bytes that are not (HasLiteralNonAscii), and MRI
            // tags the text US-ASCII all the same.
            if (encoding.IsAsciiIdentity && text.Encoding != encoding && (text.IsAscii() || encoding == RubyEncoding.Ascii)) {
                text.ForceEncoding(encoding);
            }
            return text;
        }

        public MutableString/*!*/ AppendTo(MutableString/*!*/ result) {
            return AppendTo(result, true);
        }

        /// <param name="escapeSlash">
        /// False for Regexp.union, which MRI builds with rb_reg_str_with_term(re, -1): no terminator,
        /// so a "/" in the pattern is not escaped.
        /// </param>
        public MutableString/*!*/ AppendTo(MutableString/*!*/ result, bool escapeSlash) {
            Assert.NotNull(result);

            // MRI hoists the options of a group that spans the whole pattern into the (?...) it
            // writes around it, and keeps doing so: /(?i:.)/ is "(?i-mx:.)", not "(?-mix:(?i:.))".
            MutableString pattern = TextPattern;
            RubyRegexOptions options = Options;
            while (TryHoistWholePatternGroup(ref pattern, ref options)) {
            }

            result.Append("(?");
            if (AppendOptionString(result, options, true) < 3) {
                result.Append('-');
            }
            AppendOptionString(result, options, false);
            result.Append(':');
            if (escapeSlash) {
                AppendEscapeForwardSlash(result, pattern);
            } else {
                AppendPatternPiece(result, pattern, 0, pattern.GetCharCount());
            }
            result.Append(')');
            return result;
        }

        /// <summary>
        /// A pattern that is exactly one "(?opts:...)" group is the same regexp as its body under
        /// those options, and MRI writes it that way. The group has to span the whole pattern -
        /// "(?ix:foo)bar" and "(?ix:foo)(?m:bar)" both end in ')' without being one group - and it
        /// has to be an option group rather than a lookahead or a named one.
        /// </summary>
        private static bool TryHoistWholePatternGroup(ref MutableString/*!*/ pattern, ref RubyRegexOptions options) {
            int length = pattern.GetCharCount();
            if (length < 3 || pattern.GetChar(0) != '(' || pattern.GetChar(1) != '?') {
                return false;
            }

            RubyRegexOptions hoisted = options;
            int i = 2;
            while (i < length) {
                char c = pattern.GetChar(i);
                if (c == 'm') {
                    hoisted |= RubyRegexOptions.Multiline;
                } else if (c == 'i') {
                    hoisted |= RubyRegexOptions.IgnoreCase;
                } else if (c == 'x') {
                    hoisted |= RubyRegexOptions.Extended;
                } else {
                    break;
                }
                i++;
            }

            if (i < length && pattern.GetChar(i) == '-') {
                i++;
                while (i < length) {
                    char c = pattern.GetChar(i);
                    if (c == 'm') {
                        hoisted &= ~RubyRegexOptions.Multiline;
                    } else if (c == 'i') {
                        hoisted &= ~RubyRegexOptions.IgnoreCase;
                    } else if (c == 'x') {
                        hoisted &= ~RubyRegexOptions.Extended;
                    } else {
                        break;
                    }
                    i++;
                }
            }

            // A leading "(?opts)" sets the options for the rest of the pattern, and MRI's
            // rb_reg_str_with_term folds it in the same way: /(?i)foo/ is "(?i-mx:foo)".
            if (i > 2 && i < length && pattern.GetChar(i) == ')') {
                options = hoisted;
                pattern = pattern.GetSlice(i + 1, length - i - 1);
                return true;
            }

            if (i >= length - 1 || pattern.GetChar(i) != ':' || FindGroupEnd(pattern, length) != length - 1) {
                return false;
            }

            options = hoisted;
            pattern = pattern.GetSlice(i + 1, length - i - 2);
            return true;
        }

        /// <summary>
        /// The index of the ')' closing the group the pattern opens with, or -1. A '\' escapes
        /// whatever follows it and parentheses inside a character class are literal. Classes nest,
        /// and a ']' straight after '[' or '[^' is a member of its class when another ']' follows
        /// (Onigmo's fetch_token_in_cc): "(?i:[])]a)" is one group.
        /// </summary>
        private static int FindGroupEnd(MutableString/*!*/ pattern, int length) {
            int depth = 0;
            int classDepth = 0;

            for (int i = 0; i < length; i++) {
                char c = pattern.GetChar(i);
                if (c == '\\') {
                    i++;
                } else if (c == '[') {
                    classDepth++;
                    if (i + 1 < length && pattern.GetChar(i + 1) == '^') {
                        i++;
                    }
                    if (i + 1 < length && pattern.GetChar(i + 1) == ']' && pattern.IndexOf(']', i + 2) >= 0) {
                        i++;
                    }
                } else if (classDepth > 0) {
                    if (c == ']') {
                        classDepth--;
                    }
                } else if (c == '(') {
                    depth++;
                } else if (c == ')') {
                    if (--depth == 0) {
                        return i;
                    }
                }
            }

            return -1;
        }

        private int AppendOptionString(MutableString/*!*/ result, bool enabled) {
            return AppendOptionString(result, Options, enabled);
        }

        private static int AppendOptionString(MutableString/*!*/ result, RubyRegexOptions options, bool enabled) {
            int count = 0;

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

                AppendPatternPiece(result, pattern, first, i - first);
                result.Append('\\');
                first = i; // include forward slash in the next append
                i = SkipToUnescapedForwardSlash(pattern, patternLength, i + 1);
            }

            AppendPatternPiece(result, pattern, first, patternLength - first);
            return result;
        }

        // rb_reg_expr_str writes a character that is neither printable nor a space as \xHH, so a
        // NUL or an ESC in the pattern shows as "\x00" or "\x1B" in #to_s and #inspect. A backslash
        // and the character after it are copied as they are: "\\\e" (a backslash and an ESC) stays
        // an escaped ESC, which compiles to the same pattern, where "\\\x1B" would not.
        private static void AppendPatternPiece(MutableString/*!*/ result, MutableString/*!*/ pattern, int start, int count) {
            int end = start + count;
            int runStart = start;
            for (int i = start; i < end; i++) {
                char c = pattern.GetChar(i);
                if (c == '\\' && i + 1 < end) {
                    i++;
                } else if (c < 0x20 && (c < '\t' || c > '\r') || c == 0x7f) {
                    result.Append(pattern, runStart, i - runStart);
                    result.Append("\\x").Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                    runStart = i + 1;
                }
            }
            result.Append(pattern, runStart, end - runStart);
        }

        /// <summary>
        /// MRI's rb_reg_expr_str as rb_reg_desc (Regexp#inspect) calls it, with the encoding the
        /// result is meant for. The pattern is copied as it is only when it is valid, its ASCII
        /// characters are all printable and none is a '/', and every other character is in that
        /// encoding already. Otherwise every character is looked at, and a non-ASCII one is escaped
        /// even when it is in the result's encoding: /あ\// inspects as "/\u3042\//" under UTF-8 too.
        /// It is \uXXXX (\u{XXXXX}) in a Unicode regexp, and in any other \xNN or \x{NNNN}, the
        /// code MRI reads off its bytes. An invalid byte is \xNN, a '/' "\/", an ASCII character
        /// that is neither printable nor a space \xNN, and a backslash goes out together with the
        /// character after it, whatever that is.
        /// </summary>
        private static void AppendForResultEncoding(MutableString/*!*/ result, MutableString/*!*/ pattern,
            RubyEncoding/*!*/ encoding, RubyEncoding/*!*/ resultEncoding) {

            if (!NeedsEscapingFor(pattern, encoding, resultEncoding)) {
                result.Append(pattern);
                return;
            }

            bool unicode = encoding.HasUnicodeCodePoints;
            bool afterBackslash = false;
            var characters = pattern.GetCharacters();
            while (characters.MoveNext()) {
                var current = characters.Current;
                if (afterBackslash) {
                    result.Append(current.ToMutableString(pattern.Encoding));
                    afterBackslash = false;
                } else if (!current.IsValid) {
                    foreach (byte b in current.Invalid) {
                        AppendHexByte(result, b);
                    }
                } else if (current.Codepoint < 0x80) {
                    int c = current.Codepoint;
                    if (c == '\\') {
                        result.Append('\\');
                        afterBackslash = true;
                    } else if (c == '/') {
                        result.Append("\\/");
                    } else if (c >= 0x20 && c < 0x7f || c >= '\t' && c <= '\r') {
                        result.Append((char)c);
                    } else {
                        AppendHexByte(result, c);
                    }
                } else if (unicode) {
                    // rb_str_buf_cat_escaped_char
                    int c = current.Codepoint;
                    result.Append(c < 0x10000 ? "\\u" + c.ToString("X4", System.Globalization.CultureInfo.InvariantCulture) :
                        "\\u{" + c.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "}");
                } else {
                    // The code of a character of EUC-JP, Shift_JIS, GB18030... is its bytes read as
                    // one big-endian number (mbc_to_code), and of a single byte encoding's the byte.
                    long code = 0;
                    foreach (byte b in current.ToMutableString(pattern.Encoding).ToByteArray()) {
                        code = (code << 8) | b;
                    }
                    if (code < 0x100) {
                        AppendHexByte(result, (int)code);
                    } else {
                        result.Append("\\x{").Append(code.ToString("X", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
                    }
                }
            }
        }

        /// <summary>
        /// rb_reg_expr_str's first pass: whether the pattern can be copied as it is.
        /// </summary>
        private static bool NeedsEscapingFor(MutableString/*!*/ pattern, RubyEncoding/*!*/ encoding, RubyEncoding/*!*/ resultEncoding) {
            if (!encoding.IsAsciiIdentity || pattern.ContainsInvalidCharacters()) {
                return true;
            }
            if (encoding != resultEncoding && !pattern.IsAscii()) {
                return true;
            }
            return pattern.IndexOf('/') >= 0 || HasUnprintableAscii(pattern);
        }

        private static bool HasUnprintableAscii(MutableString/*!*/ pattern) {
            int length = pattern.GetCharCount();
            for (int i = 0; i < length; i++) {
                char c = pattern.GetChar(i);
                if (c < 0x20 || c == 0x7f) {
                    return true;
                }
            }
            return false;
        }

        private static void AppendHexByte(MutableString/*!*/ result, int b) {
            result.Append("\\x").Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
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

        /// <summary>
        /// MRI refuses a pattern whose bytes are not valid in its own encoding before compiling it:
        /// Regexp.new, and #sub, #gsub, their bang forms and #scan given a String (string.c's
        /// get_pat_quoted), all raise "RegexpError: invalid multibyte character: /\xFF/".
        /// </summary>
        public static void RequireValidPattern(MutableString/*!*/ pattern) {
            if (pattern.Encoding != RubyEncoding.Binary && pattern.ContainsInvalidCharacters()) {
                throw new RegexpError("invalid multibyte character: /" + EscapeNonAsciiBytes(pattern) + "/");
            }
        }

        /// <summary>
        /// RequireValidPattern for a pattern Regexp.new or Marshal is about to make a regexp of with
        /// these options, in the order MRI's rb_reg_initialize_str and rb_reg_preprocess find what
        /// is wrong: /n first, then the source from its start (Preprocess), so that "\\x\xFF" in
        /// UTF-8 is an "invalid hex escape", "\\c\xFF" a "too short control escape" in BINARY too,
        /// and the bytes of a # comment in extended mode are not looked at. The message ends in the
        /// pattern's options (rb_reg_error_desc).
        /// </summary>
        public static void RequireValidPattern(MutableString/*!*/ pattern, RubyRegexOptions options) {
            RequireBinaryCompatibleSource(pattern, options);
            RubyEncoding encoding = pattern.Encoding;
            string error = null;
            if (encoding.IsAsciiIdentity) {
                // /n reads the source as BINARY
                bool pinned;
                error = Preprocess(pattern.ToByteArray(), (options & RubyRegexOptions.EncodingMask) == RubyRegexOptions.FIXED ?
                    RubyEncoding.Binary : encoding, (options & RubyRegexOptions.Extended) != 0, out pinned);
            } else if (pattern.ContainsInvalidCharacters()) {
                error = "invalid multibyte character";
            }
            if (error != null) {
                throw CreatePatternError(error, pattern, options);
            }
        }

        // MRI's rb_reg_initialize_str: /n reads the source as BINARY, which a source in another
        // encoding can only be when it is ASCII only.
        private static void RequireBinaryCompatibleSource(MutableString/*!*/ pattern, RubyRegexOptions options) {
            if ((options & RubyRegexOptions.EncodingMask) == RubyRegexOptions.FIXED && pattern.Encoding != RubyEncoding.Binary &&
                !pattern.IsAscii()) {
                throw CreatePatternError("/.../n has a non escaped non ASCII character in non ASCII-8BIT script", pattern, options);
            }
        }

        // Exception.Data key: what DescribeIn needs to write the message of a CreatePatternError again.
        private const string PatternErrorKey = "IronRuby.RegexpPatternError";

        /// <summary>
        /// MRI's rb_enc_reg_error_desc: the error, then the pattern between slashes as
        /// rb_reg_expr_str writes it, then the options m, i and x it has on (but no n), all in the
        /// pattern's encoding. The text has to be bytes, not CLR characters: rb_reg_expr_str
        /// copies a backslash and the character after it as they are, a byte that is not a
        /// character included.
        ///
        /// How the pattern is written depends on the default internal or external encoding, which
        /// only a context knows. Here it is taken to be UTF-8; DescribeIn writes the message again
        /// for a context's.
        /// </summary>
        private static RegexpError/*!*/ CreatePatternError(string/*!*/ error, MutableString/*!*/ pattern, RubyRegexOptions options) {
            var result = new RegexpError(error + ": /" + EscapeNonAsciiBytes(pattern) + "/" + OptionLetters(options));
            result.Data[PatternErrorKey] = new object[] { error, pattern.Clone(), options };
            RubyExceptionData.GetInstance(result).Message = DescribePatternError(error, pattern, options, RubyEncoding.UTF8);
            return result;
        }

        /// <summary>
        /// Writes the message of a RegexpError about a pattern (CreatePatternError) for the default
        /// internal or external encoding of <paramref name="context"/>. Any other exception is left
        /// as it is.
        /// </summary>
        public static void DescribeIn(Exception/*!*/ exception, RubyContext/*!*/ context) {
            var data = exception.Data[PatternErrorKey] as object[];
            if (data != null) {
                exception.Data.Remove(PatternErrorKey);
                RubyExceptionData.GetInstance(exception).Message = DescribePatternError((string)data[0], (MutableString)data[1],
                    (RubyRegexOptions)data[2], context.DefaultInternalEncoding ?? context.DefaultExternalEncoding);
            }
        }

        private static MutableString/*!*/ DescribePatternError(string/*!*/ error, MutableString/*!*/ pattern, RubyRegexOptions options,
            RubyEncoding/*!*/ textEncoding) {
            // The error can quote the pattern too: "invalid character property name {Alphä}". MRI
            // writes it and the rest as bytes in the pattern's encoding, ASCII ones as they are
            // even in an ASCII incompatible encoding.
            var bytes = new List<byte>(pattern.Encoding.IsAsciiIdentity ? pattern.Encoding.Encoding.GetBytes(error) :
                System.Text.Encoding.ASCII.GetBytes(error));
            AppendAscii(bytes, ": /");
            AppendPatternDescription(bytes, pattern, textEncoding);
            AppendAscii(bytes, "/" + OptionLetters(options));
            return MutableString.CreateBinary(bytes.ToArray(), pattern.Encoding);
        }

        private static string/*!*/ OptionLetters(RubyRegexOptions options) {
            var result = MutableString.CreateMutable(RubyEncoding.Binary);
            AppendOptionString(result, options, true);
            return result.ToString();
        }

        private static void AppendAscii(List<byte>/*!*/ bytes, string/*!*/ text) {
            foreach (char c in text) {
                bytes.Add((byte)c);
            }
        }

        /// <summary>
        /// MRI's rb_reg_expr_str with '/' as the terminator. The pattern is written as it is when
        /// its characters are all valid, none of them is "/" or an ASCII control character, and
        /// those outside ASCII are in <paramref name="textEncoding"/>. Otherwise it is escaped: a
        /// backslash goes out with the character after it as they are; a byte that begins no
        /// character as \xHH; any other character outside ASCII as the escape for its code point
        /// (\uHHHH or \u{HHHHH} in a Unicode encoding, \xHH or \x{HHHH} in another); "/" as "\/",
        /// and an ASCII control character other than a space as \xHH.
        /// </summary>
        private static void AppendPatternDescription(List<byte>/*!*/ result, MutableString/*!*/ pattern, RubyEncoding/*!*/ textEncoding) {
            RubyEncoding encoding = pattern.Encoding;
            byte[] bytes = pattern.ToByteArray();

            if (!encoding.IsAsciiIdentity) {
                AppendWidePatternDescription(result, bytes, encoding);
                return;
            }

            bool needEscape = encoding != RubyEncoding.Binary && pattern.ContainsInvalidCharacters();
            for (int j = 0; j < bytes.Length && !needEscape; j++) {
                needEscape = bytes[j] == '/' || (bytes[j] < 0x80 ? !IsPrintableAscii(bytes[j]) : encoding != textEncoding);
            }
            if (!needEscape) {
                result.AddRange(bytes);
                return;
            }

            int i = 0;
            while (i < bytes.Length) {
                byte b = bytes[i];
                if (b == '\\' && i + 1 < bytes.Length) {
                    int length = Math.Max(CharacterLength(bytes, i + 1, encoding), 1);
                    for (int j = 0; j <= length; j++) {
                        result.Add(bytes[i + j]);
                    }
                    i += 1 + length;
                } else if (b >= 0x80) {
                    int length = CharacterLength(bytes, i, encoding);
                    if (length < 0) {
                        AppendAscii(result, String.Format(CultureInfo.InvariantCulture, "\\x{0:X2}", b));
                        i++;
                        continue;
                    }
                    int codepoint;
                    if (encoding.IsUnicodeEncoding) {
                        string decoded = encoding.StrictEncoding.GetString(bytes, i, length);
                        codepoint = Char.ConvertToUtf32(decoded, 0);
                        AppendAscii(result, String.Format(CultureInfo.InvariantCulture, codepoint < 0x10000 ? "\\u{0:X4}" : "\\u{{{0:X}}}", codepoint));
                    } else {
                        codepoint = 0;
                        for (int j = 0; j < length; j++) {
                            codepoint = (codepoint << 8) | bytes[i + j];
                        }
                        AppendAscii(result, String.Format(CultureInfo.InvariantCulture, codepoint < 0x100 ? "\\x{0:X2}" : "\\x{{{0:X}}}", codepoint));
                    }
                    i += length;
                } else if (b == '/') {
                    AppendAscii(result, "\\/");
                    i++;
                } else if (IsPrintableAscii(b) || b == '\t' || b == '\n' || b == '\v' || b == '\f' || b == '\r') {
                    result.Add(b);
                    i++;
                } else {
                    AppendAscii(result, String.Format(CultureInfo.InvariantCulture, "\\x{0:X2}", b));
                    i++;
                }
            }
        }

        /// <summary>
        /// AppendPatternDescription for an ASCII incompatible encoding (UTF-16, UTF-32), which
        /// rb_reg_expr_str always escapes, a character at a time: the ASCII text it adds is single
        /// bytes all the same.
        /// </summary>
        private static void AppendWidePatternDescription(List<byte>/*!*/ result, byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            int i = 0;
            while (i < bytes.Length) {
                int length = CharacterLength(bytes, i, encoding);
                if (length < 0) {
                    AppendAscii(result, String.Format(CultureInfo.InvariantCulture, "\\x{0:X2}", bytes[i]));
                    i++;
                    continue;
                }
                int codepoint = Char.ConvertToUtf32(encoding.StrictEncoding.GetString(bytes, i, length), 0);
                if (codepoint == '\\' && i + length < bytes.Length) {
                    length += Math.Max(CharacterLength(bytes, i + length, encoding), 1);
                } else if (codepoint == '/') {
                    result.Add((byte)'\\');
                } else if (codepoint >= 0x80) {
                    AppendAscii(result, String.Format(CultureInfo.InvariantCulture, codepoint < 0x10000 ? "\\u{0:X4}" : "\\u{{{0:X}}}", codepoint));
                    i += length;
                    continue;
                } else if (!IsPrintableAscii((byte)codepoint) && (codepoint < '\t' || codepoint > '\r')) {
                    AppendAscii(result, String.Format(CultureInfo.InvariantCulture, "\\x{0:X2}", codepoint));
                    i += length;
                    continue;
                }
                for (int j = 0; j < length && i < bytes.Length; j++) {
                    result.Add(bytes[i++]);
                }
            }
        }

        private static bool IsPrintableAscii(byte b) {
            return b >= 0x20 && b < 0x7f;
        }

        // For an error message about bytes that do not decode: ASCII as is, the rest as \xHH.
        private static string/*!*/ EscapeNonAsciiBytes(MutableString/*!*/ str) {
            var result = new StringBuilder();
            foreach (byte b in str.ToByteArray()) {
                if (b < 0x80) {
                    result.Append((char)b);
                } else {
                    result.Append("\\x").Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return result.ToString();
        }

        /// <summary>
        /// A String used as a pattern by #sub, #gsub, #sub!, #gsub! and #scan: matched literally, and
        /// refused first if it is not valid in its encoding, as MRI does. MRI searches for such a
        /// pattern byte by byte, so unlike a Regexp it finds it in a string that holds bytes
        /// invalid in its encoding: "\xFFabc".sub("a", "b") is "\xFFbbc".
        /// </summary>
        public static RubyRegex/*!*/ FromLiteralPattern(MutableString/*!*/ pattern) {
            RequireValidPattern(pattern);
            var result = new RubyRegex(Escape(pattern), RubyRegexOptions.NONE);
            result._flags |= LiteralPatternFlag;
            return result;
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

                    case '\v':
                        escaped = 'v';
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
