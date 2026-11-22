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
using System.Text;
using IronRuby.Builtins;

namespace IronRuby.Runtime {

    /// <summary>
    /// MRI's UTF8-MAC converters (enc/trans/utf8_mac.c). A UTF8-MAC string is UTF-8; only a
    /// conversion changes it: to UTF8-MAC each character is written decomposed, as HFS+ names
    /// files, and from it a character is composed with the one after it where the pair names
    /// one. The tables are Apple's (Utf8MacTables.Generated.cs, from CRuby), not Unicode's NFD
    /// and NFC: some characters stay whole, marks are not reordered, and the composition is a
    /// left fold over pairs.
    /// </summary>
    internal static partial class Utf8MacTables {
        private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
        private const int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

        private static Dictionary<int, string> _decompositionMap;
        private static Dictionary<long, int> _compositionMap;

        private static Dictionary<int, string>/*!*/ DecompositionMap {
            get {
                var map = _decompositionMap;
                if (map == null) {
                    map = new Dictionary<int, string>(_decomposed.Length);
                    for (int i = 0; i < _decomposed.Length; i++) {
                        map[_decomposed[i]] = _decompositions[i];
                    }
                    _decompositionMap = map;
                }
                return map;
            }
        }

        private static Dictionary<long, int>/*!*/ CompositionMap {
            get {
                var map = _compositionMap;
                if (map == null) {
                    map = new Dictionary<long, int>(_pairs.Length / 3);
                    for (int i = 0; i < _pairs.Length; i += 3) {
                        map[((long)_pairs[i] << 21) | (long)_pairs[i + 1]] = _pairs[i + 2];
                    }
                    _compositionMap = map;
                }
                return map;
            }
        }

        /// <summary>The text as UTF-8 to UTF8-MAC writes it: each character decomposed.</summary>
        internal static string/*!*/ Decompose(string/*!*/ text) {
            StringBuilder result = null;
            var map = DecompositionMap;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                string decomposition = null;
                int s = c - SBase;
                if (s >= 0 && s < SCount) {
                    var jamo = new StringBuilder(3);
                    jamo.Append((char)(LBase + s / NCount)).Append((char)(VBase + (s % NCount) / TCount));
                    if (s % TCount != 0) {
                        jamo.Append((char)(TBase + s % TCount));
                    }
                    decomposition = jamo.ToString();
                } else if (!Char.IsSurrogate(c)) {
                    map.TryGetValue(c, out decomposition);
                }
                if (decomposition == null) {
                    if (result != null) {
                        result.Append(c);
                    }
                    continue;
                }
                if (result == null) {
                    result = new StringBuilder(text.Length + 8);
                    result.Append(text, 0, i);
                }
                result.Append(decomposition);
            }
            return (result != null) ? result.ToString() : text;
        }

        internal static bool TryCompose(int first, int second, out int composed) {
            // Hangul: a leading and a vowel jamo, then a syllable of those two and a trailing one
            int l = first - LBase, v = second - VBase;
            if (l >= 0 && l < LCount && v >= 0 && v < VCount) {
                composed = SBase + (l * VCount + v) * TCount;
                return true;
            }
            int s = first - SBase, t = second - TBase;
            if (s >= 0 && s < SCount && s % TCount == 0 && t > 0 && t < TCount) {
                composed = first + t;
                return true;
            }
            return CompositionMap.TryGetValue(((long)first << 21) | (long)second, out composed);
        }

        /// <summary>
        /// The text as UTF8-MAC to UTF-8 writes it: MRI's from_utf8_mac holds one character and
        /// replaces it with its composition with the next where the pair has one, writing it out
        /// otherwise; a character of four UTF-8 bytes goes through on its own.
        /// </summary>
        internal static string/*!*/ Compose(string/*!*/ text) {
            var result = new StringBuilder(text.Length);
            int held = -1;
            for (int i = 0; i < text.Length; i++) {
                int c = text[i];
                if (Char.IsHighSurrogate(text[i]) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1])) {
                    if (held >= 0) {
                        AppendCodePoint(result, held);
                        held = -1;
                    }
                    result.Append(text[i]).Append(text[i + 1]);
                    i++;
                    continue;
                }
                int composed;
                if (held >= 0 && TryCompose(held, c, out composed)) {
                    held = composed;
                    continue;
                }
                if (held >= 0) {
                    AppendCodePoint(result, held);
                }
                held = c;
            }
            if (held >= 0) {
                AppendCodePoint(result, held);
            }
            return result.ToString();
        }

        private static void AppendCodePoint(StringBuilder/*!*/ result, int c) {
            result.Append((char)c);
        }
    }

    /// <summary>
    /// MRI's from_utf8_mac over a stream: Encoding::Converter hands it the characters it decodes
    /// as they come, and it holds the last one back for the one after it, which may compose with
    /// it. Flush gives up the one it holds: at the end of the input, and before anything the
    /// converter puts in the output itself (a replacement).
    /// </summary>
    public sealed class Utf8MacComposer {
        private int _held = -1;

        public string/*!*/ Push(char[]/*!*/ chars, int count) {
            var result = new StringBuilder(count + 1);
            for (int i = 0; i < count; i++) {
                if (Char.IsHighSurrogate(chars[i]) && i + 1 < count && Char.IsLowSurrogate(chars[i + 1])) {
                    // a character of four UTF-8 bytes goes through on its own
                    AppendHeld(result);
                    result.Append(chars[i]).Append(chars[i + 1]);
                    i++;
                    continue;
                }
                int composed;
                if (_held >= 0 && Utf8MacTables.TryCompose(_held, chars[i], out composed)) {
                    _held = composed;
                    continue;
                }
                AppendHeld(result);
                _held = chars[i];
            }
            return result.ToString();
        }

        public string/*!*/ Flush() {
            var result = new StringBuilder(1);
            AppendHeld(result);
            return result.ToString();
        }

        public void Reset() {
            _held = -1;
        }

        private void AppendHeld(StringBuilder/*!*/ result) {
            if (_held >= 0) {
                result.Append((char)_held);
                _held = -1;
            }
        }
    }

    /// <summary>
    /// UTF8-MAC as String#encode and Encoding::Converter read and write it
    /// (RubyEncoding.TranscodingEncoding): UTF-8, decomposed on the way in and composed on the
    /// way out (Utf8MacTables). Its decoder does not compose: the transcoder feeds it a byte at
    /// a time and needs every character back as soon as it is complete, and composes the text
    /// afterwards (RubyEncoding.FinishTranscodingDecode).
    /// </summary>
    internal sealed class Utf8MacTranscodingEncoding : Encoding {
        private static readonly Encoding _utf8 = new UTF8Encoding(false, true);

        internal Utf8MacTranscodingEncoding()
            : base(RubyEncoding.CodePageUTF8Mac, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback) {
        }

        // A code point from Utf8MacTables.FirstUndefined up has no conversion to UTF8-MAC.
        private static void RequireDefined(char[]/*!*/ chars, int index, int count) {
            for (int i = index; i < index + count - 1; i++) {
                if (Char.IsHighSurrogate(chars[i]) && Char.IsLowSurrogate(chars[i + 1]) &&
                    Char.ConvertToUtf32(chars[i], chars[i + 1]) >= Utf8MacTables.FirstUndefined) {
                    throw new EncoderFallbackException();
                }
            }
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            RequireDefined(chars, index, count);
            return _utf8.GetByteCount(Utf8MacTables.Decompose(new string(chars, index, count)));
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            RequireDefined(chars, charIndex, charCount);
            string decomposed = Utf8MacTables.Decompose(new string(chars, charIndex, charCount));
            return _utf8.GetBytes(decomposed, 0, decomposed.Length, bytes, byteIndex);
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            return Utf8MacTables.Compose(_utf8.GetString(bytes, index, count)).Length;
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            string composed = Utf8MacTables.Compose(_utf8.GetString(bytes, byteIndex, byteCount));
            composed.CopyTo(0, chars, charIndex, composed.Length);
            return composed.Length;
        }

        public override Decoder/*!*/ GetDecoder() {
            return _utf8.GetDecoder();
        }

        public override Encoder/*!*/ GetEncoder() {
            return new Utf8MacEncoder(this);
        }

        public override int GetMaxByteCount(int charCount) {
            // a character decomposes to at most four, each at most three bytes
            return (charCount + 1) * 12;
        }

        public override int GetMaxCharCount(int byteCount) {
            return byteCount + 1;
        }

        public override string/*!*/ WebName {
            get { return "UTF8-MAC"; }
        }

        public override string/*!*/ EncodingName {
            get { return "UTF8-MAC"; }
        }

        /// <summary>
        /// Holds a high surrogate at the end of one call for the low one the next brings, so a
        /// character split between two calls is decomposed (and checked) whole.
        /// </summary>
        private sealed class Utf8MacEncoder : Encoder {
            private readonly Utf8MacTranscodingEncoding/*!*/ _encoding;
            private char _pendingHigh;

            internal Utf8MacEncoder(Utf8MacTranscodingEncoding/*!*/ encoding) {
                _encoding = encoding;
            }

            public override void Reset() {
                _pendingHigh = '\0';
            }

            private char[]/*!*/ Take(char[]/*!*/ chars, int index, int count, bool flush, bool commit) {
                var text = new StringBuilder(count + 1);
                if (_pendingHigh != '\0') {
                    text.Append(_pendingHigh);
                }
                text.Append(chars, index, count);
                char pending = '\0';
                if (!flush && text.Length > 0 && Char.IsHighSurrogate(text[text.Length - 1])) {
                    pending = text[text.Length - 1];
                    text.Length--;
                }
                if (commit) {
                    _pendingHigh = pending;
                }
                return text.ToString().ToCharArray();
            }

            public override int GetByteCount(char[]/*!*/ chars, int index, int count, bool flush) {
                char[] text = Take(chars, index, count, flush, false);
                return _encoding.GetByteCount(text, 0, text.Length);
            }

            public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex, bool flush) {
                char[] text = Take(chars, charIndex, charCount, flush, true);
                return _encoding.GetBytes(text, 0, text.Length, bytes, byteIndex);
            }
        }
    }
}
