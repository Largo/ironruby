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

#if FEATURE_ENCODING

using System;
using System.Text;
using IronRuby.Builtins;

namespace IronRuby.Runtime {

    /// <summary>
    /// An encoding that never refuses a byte string.
    ///
    /// A Ruby string is a bag of bytes with an encoding label, and the two are allowed to
    /// disagree: "\xFF" tagged UTF-8 is a perfectly ordinary thing for a Ruby program to hold, and
    /// MRI will happily reverse it, index it, slice it and print it. IronRuby represents strings
    /// either as bytes or as characters and switches between the two on demand, and that switch
    /// used to go through the strict decoder, so every character based operation on such a string
    /// raised - in Ruby, as a bare System::Text::DecoderFallbackException.
    ///
    /// This decodes a byte that cannot begin a character to a lone low surrogate, U+DC00 plus the
    /// byte, and encodes that surrogate back to exactly the byte it came from. The mapping is
    /// therefore lossless, which is the whole point: the byte survives a round trip through the
    /// character representation, so #reverse, #index, #[] and the rest can work in characters and
    /// still produce the original bytes. It is the same trick as Python's surrogateescape.
    ///
    /// One undecodable byte becomes one character, which is also how MRI counts them - "a\xFFb" is
    /// three characters long in UTF-8 - so character offsets agree with MRI's as well.
    ///
    /// The range U+DC80..U+DCFF cannot occur in text decoded from any of the encodings this is
    /// used with: UTF-8 and friends cannot encode an unpaired surrogate at all, and a single byte
    /// encoding has no character above U+FFFF to pair one with. Validity is *not* decided here -
    /// String#valid_encoding? still asks the strict encoding, which is what makes it able to
    /// answer false for a string this class decoded happily.
    /// </summary>
    internal sealed class EscapingEncoding : Encoding {
        /// <summary>Lone low surrogates from here up stand for a single undecodable byte.</summary>
        internal const char EscapeBase = (char)0xDC00;

        private readonly Encoding/*!*/ _inner;

        internal EscapingEncoding(Encoding/*!*/ inner)
            : base(inner.CodePage) {
            _inner = inner;
        }

        internal static bool IsEscapedByte(char c) {
            return c >= (char)0xDC80 && c <= (char)0xDCFF;
        }

        /// <summary>The longest character any encoding used here produces.</summary>
        private const int MaxSequenceLength = 4;

        #region Decoding

        /// <summary>
        /// Length in bytes of the character starting at <paramref name="index"/>, or 0 if the
        /// bytes there do not begin one.
        /// </summary>
        private int SequenceLength(byte[]/*!*/ bytes, int index, int limit) {
            for (int length = 1; length <= MaxSequenceLength && index + length <= limit; length++) {
                try {
                    _inner.GetCharCount(bytes, index, length);
                    return length;
                } catch (DecoderFallbackException) {
                    // not a whole character yet - try one more byte
                }
            }
            return 0;
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            // The overwhelmingly common case is a string whose bytes are valid, and that costs one
            // call. Only a string that is not valid pays for the walk below.
            try {
                return _inner.GetCharCount(bytes, index, count);
            } catch (DecoderFallbackException) {
                // fall through
            }

            int limit = index + count;
            int result = 0;
            int i = index;
            while (i < limit) {
                int length = SequenceLength(bytes, i, limit);
                if (length == 0) {
                    result++;
                    i++;
                } else {
                    result += _inner.GetCharCount(bytes, i, length);
                    i += length;
                }
            }
            return result;
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            try {
                return _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
            } catch (DecoderFallbackException) {
                // fall through
            }

            int limit = byteIndex + byteCount;
            int i = byteIndex, j = charIndex;
            while (i < limit) {
                int length = SequenceLength(bytes, i, limit);
                if (length == 0) {
                    chars[j++] = (char)(EscapeBase + bytes[i]);
                    i++;
                } else {
                    j += _inner.GetChars(bytes, i, length, chars, j);
                    i += length;
                }
            }
            return j - charIndex;
        }

        public override string/*!*/ GetString(byte[]/*!*/ bytes, int index, int count) {
            try {
                return _inner.GetString(bytes, index, count);
            } catch (DecoderFallbackException) {
                // fall through
            }

            var chars = new char[GetCharCount(bytes, index, count)];
            int written = GetChars(bytes, index, count, chars, 0);
            return new string(chars, 0, written);
        }

        #endregion

        #region Encoding

        private static bool ContainsEscape(char[]/*!*/ chars, int index, int count) {
            for (int i = 0; i < count; i++) {
                if (IsEscapedByte(chars[index + i])) {
                    return true;
                }
            }
            return false;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            if (!ContainsEscape(chars, index, count)) {
                return _inner.GetByteCount(chars, index, count);
            }

            int result = 0;
            int runStart = index;
            int limit = index + count;
            for (int i = index; i < limit; i++) {
                if (IsEscapedByte(chars[i])) {
                    if (i > runStart) {
                        result += _inner.GetByteCount(chars, runStart, i - runStart);
                    }
                    result++;
                    runStart = i + 1;
                }
            }
            if (limit > runStart) {
                result += _inner.GetByteCount(chars, runStart, limit - runStart);
            }
            return result;
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            if (!ContainsEscape(chars, charIndex, charCount)) {
                return _inner.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
            }

            int written = 0;
            int runStart = charIndex;
            int limit = charIndex + charCount;
            for (int i = charIndex; i < limit; i++) {
                if (IsEscapedByte(chars[i])) {
                    if (i > runStart) {
                        written += _inner.GetBytes(chars, runStart, i - runStart, bytes, byteIndex + written);
                    }
                    bytes[byteIndex + written++] = (byte)(chars[i] - EscapeBase);
                    runStart = i + 1;
                }
            }
            if (limit > runStart) {
                written += _inner.GetBytes(chars, runStart, limit - runStart, bytes, byteIndex + written);
            }
            return written;
        }

        #endregion

        public override int GetMaxByteCount(int charCount) {
            return _inner.GetMaxByteCount(charCount);
        }

        public override int GetMaxCharCount(int byteCount) {
            return _inner.GetMaxCharCount(byteCount);
        }

        public override string/*!*/ EncodingName {
            get { return _inner.EncodingName; }
        }

        public override string/*!*/ WebName {
            get { return _inner.WebName; }
        }

        public override bool IsSingleByte {
            get { return _inner.IsSingleByte; }
        }
    }
}
#endif
