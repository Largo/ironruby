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
    /// Encodings Ruby has and .NET does not.
    ///
    /// .NET only knows Windows code pages, so several encodings that every Ruby program can name
    /// have no System.Text counterpart at all, and a couple of others exist under a name that means
    /// something different in Ruby.  Without these, Encoding.find("UTF-16") silently answered with
    /// .NET's "utf-16", which is Ruby's UTF-16LE - a different encoding with a different byte order
    /// and, unlike Ruby's UTF-16, not a dummy.
    ///
    /// Each class below carries a code page from RubyEncoding's private block so that it can key
    /// the same tables as a real .NET encoding without ever colliding with one.
    /// </summary>
    internal static class RubyOnlyEncodings {
        internal static Encoding/*!*/ Create(int codepage, bool throwOnError) {
            switch (codepage) {
                case RubyEncoding.CodePageUTF16: return new BomEncoding(codepage, 2, throwOnError);
                case RubyEncoding.CodePageUTF32: return new BomEncoding(codepage, 4, throwOnError);
                case RubyEncoding.CodePageCESU8: return new CesuEncoding(throwOnError);
                case RubyEncoding.CodePageTIS620: return new Tis620Encoding(throwOnError);
                default: throw new ArgumentOutOfRangeException("codepage");
            }
        }

        internal static bool IsRubyOnly(int codepage) {
            switch (codepage) {
                case RubyEncoding.CodePageUTF16:
                case RubyEncoding.CodePageUTF32:
                case RubyEncoding.CodePageCESU8:
                case RubyEncoding.CodePageTIS620:
                    return true;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Ruby's "UTF-16" and "UTF-32": the byte order is carried by a BOM instead of by the encoding
    /// name.  Both are dummy encodings in Ruby - a string tagged with one of them is a byte string,
    /// because nothing can be said about a character until the BOM has been read.
    ///
    /// Ruby writes big endian with a BOM and reads whichever order the BOM asks for, defaulting to
    /// big endian when there is none, which is what this implements.
    /// </summary>
    internal sealed class BomEncoding : Encoding {
        private readonly int _unit;         // bytes per code unit: 2 for UTF-16, 4 for UTF-32
        private readonly Encoding/*!*/ _bigEndian;
        private readonly Encoding/*!*/ _littleEndian;
        private readonly string/*!*/ _name;

        internal BomEncoding(int codepage, int unit, bool throwOnError)
            : base(codepage) {
            _unit = unit;
            _name = unit == 2 ? "UTF-16" : "UTF-32";
            if (unit == 2) {
                _bigEndian = new UnicodeEncoding(true, false, throwOnError);
                _littleEndian = new UnicodeEncoding(false, false, throwOnError);
            } else {
                _bigEndian = new UTF32Encoding(true, false, throwOnError);
                _littleEndian = new UTF32Encoding(false, false, throwOnError);
            }
        }

        /// <summary>Big endian BOM: FE FF, or 00 00 FE FF.</summary>
        private byte[]/*!*/ Bom {
            get { return _unit == 2 ? new byte[] { 0xfe, 0xff } : new byte[] { 0x00, 0x00, 0xfe, 0xff }; }
        }

        /// <summary>
        /// Reports which endianness the input asks for and how many bytes of BOM to skip.
        /// </summary>
        private Encoding/*!*/ Sniff(byte[]/*!*/ bytes, int index, int count, out int skip) {
            if (count >= _unit) {
                if (_unit == 2) {
                    if (bytes[index] == 0xfe && bytes[index + 1] == 0xff) {
                        skip = 2;
                        return _bigEndian;
                    }
                    if (bytes[index] == 0xff && bytes[index + 1] == 0xfe) {
                        skip = 2;
                        return _littleEndian;
                    }
                } else {
                    if (bytes[index] == 0 && bytes[index + 1] == 0 && bytes[index + 2] == 0xfe && bytes[index + 3] == 0xff) {
                        skip = 4;
                        return _bigEndian;
                    }
                    if (bytes[index] == 0xff && bytes[index + 1] == 0xfe && bytes[index + 2] == 0 && bytes[index + 3] == 0) {
                        skip = 4;
                        return _littleEndian;
                    }
                }
            }

            skip = 0;
            return _bigEndian;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            return _unit + _bigEndian.GetByteCount(chars, index, count);
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            var bom = Bom;
            Array.Copy(bom, 0, bytes, byteIndex, bom.Length);
            return bom.Length + _bigEndian.GetBytes(chars, charIndex, charCount, bytes, byteIndex + bom.Length);
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            int skip;
            var inner = Sniff(bytes, index, count, out skip);
            return inner.GetCharCount(bytes, index + skip, count - skip);
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            int skip;
            var inner = Sniff(bytes, byteIndex, byteCount, out skip);
            return inner.GetChars(bytes, byteIndex + skip, byteCount - skip, chars, charIndex);
        }

        public override int GetMaxByteCount(int charCount) {
            return _unit + _bigEndian.GetMaxByteCount(charCount);
        }

        public override int GetMaxCharCount(int byteCount) {
            return _bigEndian.GetMaxCharCount(byteCount);
        }

        public override byte[]/*!*/ GetPreamble() {
            return Bom;
        }

        public override string/*!*/ EncodingName {
            get { return _name; }
        }

        public override string/*!*/ WebName {
            get { return _name; }
        }
    }

    /// <summary>
    /// CESU-8: UTF-8 applied to UTF-16 code units, so a non-BMP character is two three byte
    /// sequences (one per surrogate) rather than one four byte sequence.  Ruby ships it; .NET does
    /// not, and .NET's UTF8Encoding rejects the lone surrogates it is made of.
    /// </summary>
    internal sealed class CesuEncoding : Encoding {
        private readonly bool _throwOnError;

        internal CesuEncoding(bool throwOnError)
            : base(RubyEncoding.CodePageCESU8) {
            _throwOnError = throwOnError;
        }

        private static int ByteCount(char c) {
            if (c < 0x80) {
                return 1;
            }
            return c < 0x800 ? 2 : 3;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            int result = 0;
            for (int i = 0; i < count; i++) {
                result += ByteCount(chars[index + i]);
            }
            return result;
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            int j = byteIndex;
            for (int i = 0; i < charCount; i++) {
                int c = chars[charIndex + i];
                if (c < 0x80) {
                    bytes[j++] = (byte)c;
                } else if (c < 0x800) {
                    bytes[j++] = (byte)(0xc0 | (c >> 6));
                    bytes[j++] = (byte)(0x80 | (c & 0x3f));
                } else {
                    bytes[j++] = (byte)(0xe0 | (c >> 12));
                    bytes[j++] = (byte)(0x80 | ((c >> 6) & 0x3f));
                    bytes[j++] = (byte)(0x80 | (c & 0x3f));
                }
            }
            return j - byteIndex;
        }

        /// <summary>
        /// Length in bytes of the sequence starting at <paramref name="index"/>, or 0 if the bytes
        /// there are not a well formed CESU-8 sequence.
        /// </summary>
        private static int SequenceLength(byte[]/*!*/ bytes, int index, int limit) {
            byte b = bytes[index];
            int length;
            if (b < 0x80) {
                return 1;
            } else if (b >= 0xc2 && b <= 0xdf) {
                length = 2;
            } else if (b >= 0xe0 && b <= 0xef) {
                length = 3;
            } else {
                return 0;
            }

            if (index + length > limit) {
                return 0;
            }
            for (int i = 1; i < length; i++) {
                if ((bytes[index + i] & 0xc0) != 0x80) {
                    return 0;
                }
            }
            return length;
        }

        /// <summary>
        /// DecoderFallbackException has to be built with the bytes it rejected: the parameterless
        /// constructor leaves BytesUnknown null, and RubyExceptions.CreateInvalidByteSequenceError
        /// formats that property, so a null there surfaces as a CLR ArgumentNullException instead
        /// of Ruby's Encoding::InvalidByteSequenceError.
        /// </summary>
        private static DecoderFallbackException/*!*/ Undecodable(byte[]/*!*/ bytes, int index) {
            return new DecoderFallbackException(null, new[] { bytes[index] }, index);
        }

        private char Decode(byte[]/*!*/ bytes, int index, int length) {
            switch (length) {
                case 1: return (char)bytes[index];
                case 2: return (char)(((bytes[index] & 0x1f) << 6) | (bytes[index + 1] & 0x3f));
                default: return (char)(((bytes[index] & 0x0f) << 12) | ((bytes[index + 1] & 0x3f) << 6) | (bytes[index + 2] & 0x3f));
            }
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            int limit = index + count;
            int result = 0;
            int i = index;
            while (i < limit) {
                int length = SequenceLength(bytes, i, limit);
                if (length == 0) {
                    if (_throwOnError) {
                        throw Undecodable(bytes, i);
                    }
                    // BinaryDecoderFallback's convention: an undecodable byte becomes one char.
                    length = 1;
                }
                i += length;
                result++;
            }
            return result;
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            int limit = byteIndex + byteCount;
            int i = byteIndex, j = charIndex;
            while (i < limit) {
                int length = SequenceLength(bytes, i, limit);
                if (length == 0) {
                    if (_throwOnError) {
                        throw Undecodable(bytes, i);
                    }
                    chars[j++] = (char)bytes[i++];
                    continue;
                }
                chars[j++] = Decode(bytes, i, length);
                i += length;
            }
            return j - charIndex;
        }

        public override int GetMaxByteCount(int charCount) {
            return charCount * 3;
        }

        public override int GetMaxCharCount(int byteCount) {
            return byteCount;
        }

        public override bool IsSingleByte {
            get { return false; }
        }

        public override string/*!*/ EncodingName {
            get { return "CESU-8"; }
        }

        public override string/*!*/ WebName {
            get { return "CESU-8"; }
        }
    }

    /// <summary>
    /// TIS-620, the Thai national standard.  .NET only has Windows-874, which is TIS-620 plus the
    /// Windows C1 repertoire (the Euro sign at 0x80, the smart quotes, and NBSP at 0xa0); Ruby
    /// keeps the two apart, and resolving "TIS-620" to Windows-874 made a byte like 0x80 decode
    /// where Ruby says it is undefined.
    /// </summary>
    internal sealed class Tis620Encoding : Encoding {
        private readonly bool _throwOnError;

        internal Tis620Encoding(bool throwOnError)
            : base(RubyEncoding.CodePageTIS620) {
            _throwOnError = throwOnError;
        }

        // 0x00-0x7f is ASCII; 0xa1-0xda and 0xdf-0xfb map onto the Thai block in order; everything
        // else is undefined.
        private static bool IsDefinedByte(byte b) {
            return b < 0x80 || b >= 0xa1 && b <= 0xda || b >= 0xdf && b <= 0xfb;
        }

        private static char ToChar(byte b) {
            return b < 0x80 ? (char)b : (char)(0x0e00 + (b - 0xa0));
        }

        /// <summary>See CesuEncoding.Undecodable - BytesUnknown must not be left null.</summary>
        private static DecoderFallbackException/*!*/ Undecodable(byte[]/*!*/ bytes, int index) {
            return new DecoderFallbackException(null, new[] { bytes[index] }, index);
        }

        private static int ToByte(char c) {
            if (c < 0x80) {
                return c;
            }
            int b = c - 0x0e00 + 0xa0;
            if (c >= 0x0e01 && b <= 0xff && IsDefinedByte((byte)b)) {
                return b;
            }
            return -1;
        }

        public override int GetByteCount(char[]/*!*/ chars, int index, int count) {
            if (_throwOnError) {
                for (int i = 0; i < count; i++) {
                    if (ToByte(chars[index + i]) < 0) {
                        throw new EncoderFallbackException();
                    }
                }
            }
            return count;
        }

        public override int GetBytes(char[]/*!*/ chars, int charIndex, int charCount, byte[]/*!*/ bytes, int byteIndex) {
            for (int i = 0; i < charCount; i++) {
                int b = ToByte(chars[charIndex + i]);
                if (b < 0) {
                    if (_throwOnError) {
                        throw new EncoderFallbackException();
                    }
                    b = '?';
                }
                bytes[byteIndex + i] = (byte)b;
            }
            return charCount;
        }

        public override int GetCharCount(byte[]/*!*/ bytes, int index, int count) {
            if (_throwOnError) {
                for (int i = 0; i < count; i++) {
                    if (!IsDefinedByte(bytes[index + i])) {
                        throw Undecodable(bytes, index + i);
                    }
                }
            }
            return count;
        }

        public override int GetChars(byte[]/*!*/ bytes, int byteIndex, int byteCount, char[]/*!*/ chars, int charIndex) {
            for (int i = 0; i < byteCount; i++) {
                byte b = bytes[byteIndex + i];
                if (!IsDefinedByte(b)) {
                    if (_throwOnError) {
                        throw Undecodable(bytes, byteIndex + i);
                    }
                    // BinaryDecoderFallback's convention: the byte passes through as U+00mn.
                    chars[charIndex + i] = (char)b;
                } else {
                    chars[charIndex + i] = ToChar(b);
                }
            }
            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) {
            return charCount;
        }

        public override int GetMaxCharCount(int byteCount) {
            return byteCount;
        }

        public override bool IsSingleByte {
            get { return true; }
        }

        public override string/*!*/ EncodingName {
            get { return "TIS-620"; }
        }

        public override string/*!*/ WebName {
            get { return "TIS-620"; }
        }
    }
}
#endif
