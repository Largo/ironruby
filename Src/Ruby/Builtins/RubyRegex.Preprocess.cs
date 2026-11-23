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
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    public partial class RubyRegex {
        /// <summary>
        /// MRI's unescape_nonascii, which rb_reg_preprocess runs over a source before Onigmo sees
        /// it, for what it decides rather than for what it writes: the first error it stops at
        /// (null if none), and the encoding the source fixes (<paramref name="fixedEncoding"/>,
        /// MRI's *encp; null if none).
        ///
        /// A character outside ASCII fixes <paramref name="encoding"/>, and so does an escape of
        /// bytes outside ASCII (\xHH, \NNN, \M-...) outside US-ASCII; a \u escape above 0x7F fixes
        /// UTF-8. Two of them that disagree are an error: "non ASCII character in UTF-8 regexp",
        /// "escaped non ASCII character in UTF-8 regexp", "UTF-8 character in non UTF-8 regexp".
        ///
        /// Not every non-ASCII byte fixes it. A character right after a backslash only does when
        /// it is longer than one byte (the backslash goes with it); a single non-ASCII byte there
        /// - a BINARY, ISO-8859-1 or other single byte source can hold one, and Shift_JIS's
        /// half-width katakana are one - is copied "as is", so Regexp.new("\\\xFF".b) is
        /// US-ASCII and not fixed. A comment - (?#...), or # to the end of the line in extended
        /// mode outside a character class - fixes it only in UTF-8 and is never an error that way,
        /// and the bytes of a # comment are not checked. (?x) and (?-x) turn extended mode on or
        /// off for the rest of the group around them, (?x:...) and (?-x:...) inside their own group.
        ///
        /// The escapes of bytes are read as MRI's read_escaped_byte and unescape_escaped_nonascii
        /// read them: outside US-ASCII, escaped bytes outside ASCII have to make whole characters
        /// of <paramref name="encoding"/>. <paramref name="pinned"/> tells whether a character (not
        /// an escape) fixed the encoding, <paramref name="nonAsciiEscape"/> whether the first escape
        /// that stands for something outside ASCII is one of bytes (1) or a \u (2), 0 if none is.
        /// <paramref name="encoding"/> is ASCII compatible.
        /// </summary>
        internal static string Preprocess(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding, bool extended, out bool pinned) {
            int nonAsciiEscape;
            RubyEncoding fixedEncoding;
            return Preprocess(bytes, encoding, extended, out pinned, out nonAsciiEscape, out fixedEncoding);
        }

        internal static string Preprocess(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding, bool extended, out bool pinned,
            out int nonAsciiEscape) {
            RubyEncoding fixedEncoding;
            return Preprocess(bytes, encoding, extended, out pinned, out nonAsciiEscape, out fixedEncoding);
        }

        internal static string Preprocess(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding, bool extended, out bool pinned,
            out int nonAsciiEscape, out RubyEncoding fixedEncoding) {
            pinned = false;
            nonAsciiEscape = 0;
            fixedEncoding = null;
            bool utf8 = encoding == RubyEncoding.UTF8;
            bool usAscii = encoding == RubyEncoding.Ascii;
            // in US-ASCII an escape of a byte outside ASCII is copied as it is, which a \u that
            // fixes UTF-8 makes an invalid code point for Onigmo
            bool usAsciiHighByte = false;
            int classDepth = 0;
            // what each open group restores extended mode to at its ')': -1 nothing, 0 off, 1 on
            var groups = new Stack<int>();
            int n = bytes.Length;
            int i = 0;
            string error;
            while (i < n) {
                int length = CharacterLength(bytes, i, encoding);
                if (length < 0) {
                    return "invalid multibyte character";
                }
                if (length > 1 || bytes[i] >= 0x80) {
                    pinned = true;
                    if ((error = Fix(ref fixedEncoding, encoding, "non ASCII character in UTF-8 regexp")) != null) {
                        return error;
                    }
                    i += length;
                    continue;
                }

                byte c = bytes[i++];
                switch (c) {
                    case (byte)'\\':
                        if (i == n) {
                            return "too short escape sequence";
                        }
                        length = CharacterLength(bytes, i, encoding);
                        if (length < 0) {
                            return "invalid multibyte character";
                        }
                        if (length > 1) {
                            // the backslash goes with the character
                            pinned = true;
                            if ((error = Fix(ref fixedEncoding, encoding, "non ASCII character in UTF-8 regexp")) != null) {
                                return error;
                            }
                            i += length;
                            break;
                        }
                        byte escaped = bytes[i++];
                        if (escaped >= '1' && escaped <= '7') {
                            // a back reference, or an octal escape of a byte in ASCII, is left as it is
                            int octal = 0;
                            for (int k = i - 1; k < n && k < i + 2 && bytes[k] >= '0' && bytes[k] <= '7'; k++) {
                                octal = octal * 8 + (bytes[k] - '0');
                            }
                            if (octal <= 0x7f) {
                                break;
                            }
                            escaped = (byte)'0';
                        }
                        switch (escaped) {
                            case (byte)'0':
                            case (byte)'x':
                            case (byte)'c':
                            case (byte)'C':
                            case (byte)'M':
                                i -= 2;
                                int code;
                                if (usAscii) {
                                    if ((error = ReadEscapedByte(bytes, ref i, out code)) != null) {
                                        return error;
                                    }
                                    usAsciiHighByte |= code > 0x7f;
                                } else {
                                    bool isNonAscii;
                                    if ((error = ReadEscapedCharacter(bytes, ref i, encoding, out isNonAscii)) != null) {
                                        return error;
                                    }
                                    if (isNonAscii) {
                                        if ((error = Fix(ref fixedEncoding, encoding, "escaped non ASCII character in UTF-8 regexp")) != null) {
                                            return error;
                                        }
                                        if (nonAsciiEscape == 0) {
                                            nonAsciiEscape = 1;
                                        }
                                    }
                                }
                                break;

                            case (byte)'u':
                                bool isUnicode;
                                if ((error = ReadUnicodeEscape(bytes, ref i, out isUnicode)) != null) {
                                    return error;
                                }
                                if (isUnicode) {
                                    if ((error = Fix(ref fixedEncoding, RubyEncoding.UTF8, "UTF-8 character in non UTF-8 regexp")) != null) {
                                        return error;
                                    }
                                    if (nonAsciiEscape == 0) {
                                        nonAsciiEscape = 2;
                                    }
                                }
                                break;
                        }
                        break;

                    case (byte)'#':
                        if (extended && classDepth == 0) {
                            while (i < n && bytes[i++] != '\n') {
                                if (bytes[i - 1] >= 0x80 && utf8 && fixedEncoding == null) {
                                    fixedEncoding = encoding;
                                    pinned = true;
                                }
                            }
                        }
                        break;

                    case (byte)'[':
                        classDepth++;
                        break;

                    case (byte)']':
                        if (classDepth > 0) {
                            classDepth--;
                        }
                        break;

                    case (byte)')':
                        if (classDepth == 0 && groups.Count > 0) {
                            int restore = groups.Pop();
                            if (restore >= 0) {
                                extended = restore == 1;
                            }
                        }
                        break;

                    case (byte)'(':
                        if (classDepth > 0) {
                            break;
                        }
                        if (i + 1 < n && bytes[i] == '?' && bytes[i + 1] == '#') {
                            bool commentFixes;
                            if (SkipGroupComment(bytes, ref i, encoding, out commentFixes, out error)) {
                                if (commentFixes && fixedEncoding == null) {
                                    fixedEncoding = encoding;
                                    pinned = true;
                                }
                                break;
                            }
                            if (error != null) {
                                return error;
                            }
                            // unterminated: the '(' is an ordinary one, and Onigmo complains
                            groups.Push(-1);
                            break;
                        }
                        groups.Push(i + 1 < n && bytes[i] == '?' ? ExtendedOptionChange(bytes, i + 1, ref extended) : -1);
                        break;
                }
            }
            if (usAsciiHighByte && fixedEncoding == RubyEncoding.UTF8) {
                return "invalid code point value";
            }
            return null;
        }

        // MRI's "if (*encp == 0) *encp = enc; else if (*encp != enc) error"
        private static string Fix(ref RubyEncoding fixedEncoding, RubyEncoding/*!*/ encoding, string/*!*/ error) {
            if (fixedEncoding == null) {
                fixedEncoding = encoding;
            } else if (fixedEncoding != encoding) {
                return error;
            }
            return null;
        }

        /// <summary>
        /// MRI's unescape_escaped_nonascii: the escaped bytes starting at the backslash at
        /// <paramref name="p"/>, as many as make one character of <paramref name="encoding"/>.
        /// <paramref name="isNonAscii"/> tells whether that is outside ASCII.
        /// </summary>
        private static string ReadEscapedCharacter(byte[]/*!*/ bytes, ref int p, RubyEncoding/*!*/ encoding, out bool isNonAscii) {
            isNonAscii = false;
            const int MaxLength = 6;
            var area = new byte[MaxLength];
            int count = 0;
            int length;
            while (true) {
                int code;
                string error = ReadEscapedByte(bytes, ref p, out code);
                if (error != null) {
                    return error;
                }
                area[count++] = (byte)code;
                length = PreciseLength(area, 0, count, encoding);
                if (length != 0 || count == MaxLength) {
                    break;
                }
            }
            if (length < 0) {
                return "invalid multibyte escape";
            }
            isNonAscii = count > 1 || area[0] >= 0x80;
            return null;
        }

        /// <summary>
        /// The options of "(?opts)" or "(?opts:" starting at <paramref name="start"/>: when they
        /// turn extended mode on or off, it changes here, and the result is the mode the group's
        /// ')' restores (-1 for none: "(?x)" changes the mode for the rest of the enclosing group).
        /// </summary>
        private static int ExtendedOptionChange(byte[]/*!*/ bytes, int start, ref bool extended) {
            bool invert = false;
            int localExtend = 0;
            for (int s = start; s < bytes.Length; s++) {
                switch (bytes[s]) {
                    case (byte)'x':
                        localExtend = invert ? -1 : 1;
                        break;
                    case (byte)'-':
                        invert = true;
                        break;
                    case (byte)'i':
                    case (byte)'m':
                    case (byte)'a':
                    case (byte)'d':
                    case (byte)'u':
                        break;
                    case (byte)':':
                    case (byte)')':
                        if (localExtend == 0 || (localExtend == 1) == extended) {
                            return -1;
                        }
                        int restore = extended ? 1 : 0;
                        extended = localExtend == 1;
                        return bytes[s] == ':' ? restore : -1;
                    default:
                        return -1;
                }
            }
            return -1;
        }

        /// <summary>
        /// Skips the comment "(?#...)" whose "?#" starts at <paramref name="i"/>: false, with
        /// <paramref name="i"/> unchanged, if it has no ')'. A backslash takes the character after
        /// it along, and a character outside ASCII fixes the encoding only in UTF-8
        /// (<paramref name="fixes"/>).
        /// </summary>
        private static bool SkipGroupComment(byte[]/*!*/ bytes, ref int i, RubyEncoding/*!*/ encoding, out bool fixes,
            out string error) {
            error = null;
            fixes = false;
            int p = i + 2;
            while (p < bytes.Length) {
                byte c = bytes[p++];
                if (c == ')') {
                    i = p;
                    return true;
                }
                if (c == '\\' || c >= 0x80) {
                    if (c >= 0x80) {
                        fixes |= encoding == RubyEncoding.UTF8;
                        p--;
                    }
                    int length = p < bytes.Length ? CharacterLength(bytes, p, encoding) : -1;
                    if (length < 0) {
                        error = "invalid multibyte character";
                        return false;
                    }
                    p += length;
                }
            }
            return false;
        }

        /// <summary>
        /// MRI's read_escaped_byte: \xHH, \OOO, \cX, \C-X, \M-X and their combinations, starting at
        /// the backslash; <paramref name="code"/> is the byte.
        /// </summary>
        private static string ReadEscapedByte(byte[]/*!*/ bytes, ref int p, out int code) {
            int n = bytes.Length;
            bool meta = false, control = false;
            code = 0;
            if (p == n || bytes[p++] != '\\') {
                return "too short escaped multibyte character";
            }

            while (true) {
                if (p == n) {
                    return "too short escape sequence";
                }
                byte c = bytes[p++];
                switch (c) {
                    case (byte)'\\': code = '\\'; break;
                    case (byte)'n': code = '\n'; break;
                    case (byte)'t': code = '\t'; break;
                    case (byte)'r': code = '\r'; break;
                    case (byte)'f': code = '\f'; break;
                    case (byte)'v': code = 0x0b; break;
                    case (byte)'a': code = 0x07; break;
                    case (byte)'e': code = 0x1b; break;

                    case (byte)'0': case (byte)'1': case (byte)'2': case (byte)'3':
                    case (byte)'4': case (byte)'5': case (byte)'6': case (byte)'7':
                        p--;
                        for (int k = 0; k < 3 && p < n && bytes[p] >= '0' && bytes[p] <= '7'; k++) {
                            code = code * 8 + (bytes[p++] - '0');
                        }
                        break;

                    case (byte)'x':
                        int digits = 0;
                        while (digits < 2 && p < n && IsHexDigit(bytes[p])) {
                            code = code * 16 + HexDigitValue(bytes[p++]);
                            digits++;
                        }
                        if (digits == 0) {
                            return "invalid hex escape";
                        }
                        break;

                    case (byte)'M':
                        if (meta) {
                            return "duplicate meta escape";
                        }
                        meta = true;
                        if (p + 1 < n && bytes[p++] == '-' && bytes[p] < 0x80) {
                            if (bytes[p] == '\\') {
                                p++;
                                continue;
                            }
                            code = bytes[p++];
                            break;
                        }
                        return "too short meta escape";

                    case (byte)'C':
                    case (byte)'c':
                        if (c == 'C' && (p == n || bytes[p++] != '-')) {
                            return "too short control escape";
                        }
                        if (control) {
                            return "duplicate control escape";
                        }
                        control = true;
                        if (p < n && bytes[p] < 0x80) {
                            if (bytes[p] == '\\') {
                                p++;
                                continue;
                            }
                            code = bytes[p++];
                            break;
                        }
                        return "too short control escape";

                    default:
                        return "unexpected escape sequence";
                }

                if (code > 0xff) {
                    return "invalid escape code";
                }
                if (control) {
                    code &= 0x1f;
                }
                if (meta) {
                    code |= 0x80;
                }
                return null;
            }
        }

        private static int HexDigitValue(byte b) {
            return b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;
        }

        /// <summary>
        /// MRI's unescape_unicode_list and unescape_unicode_bmp: \u{H HH ...} or \uHHHH, starting
        /// after the u, each code point checked as check_unicode_range checks it (no surrogate,
        /// none above U+10FFFF). <paramref name="isUnicode"/> tells whether one is outside ASCII
        /// (MRI's append_utf8, which fixes UTF-8).
        /// </summary>
        private static string ReadUnicodeEscape(byte[]/*!*/ bytes, ref int p, out bool isUnicode) {
            int n = bytes.Length;
            isUnicode = false;
            if (p == n) {
                return "too short escape sequence";
            }
            if (bytes[p] == '{') {
                p++;
                bool any = false;
                while (p < n && IsSpace(bytes[p])) {
                    p++;
                }
                while (true) {
                    int digits = 0, code = 0;
                    while (p < n && IsHexDigit(bytes[p])) {
                        if (digits < 7) {
                            code = code * 16 + HexDigitValue(bytes[p]);
                        }
                        p++;
                        digits++;
                    }
                    if (digits == 0) {
                        break;
                    }
                    if (digits > 6 || !IsUnicodeScalar(code)) {
                        return "invalid Unicode range";
                    }
                    any = true;
                    isUnicode |= code > 0x7f;
                    while (p < n && IsSpace(bytes[p])) {
                        p++;
                    }
                }
                if (!any || p == n || bytes[p++] != '}') {
                    return "invalid Unicode list";
                }
                return null;
            }
            int value = 0;
            for (int k = 0; k < 4; k++) {
                if (p + k >= n || !IsHexDigit(bytes[p + k])) {
                    return "invalid Unicode escape";
                }
                value = value * 16 + HexDigitValue(bytes[p + k]);
            }
            if (!IsUnicodeScalar(value)) {
                return "invalid Unicode range";
            }
            isUnicode = value > 0x7f;
            p += 4;
            return null;
        }

        private static bool IsUnicodeScalar(int code) {
            return code <= 0x10ffff && (code < 0xd800 || code > 0xdfff);
        }

        private static bool IsHexDigit(byte b) {
            return b >= '0' && b <= '9' || b >= 'a' && b <= 'f' || b >= 'A' && b <= 'F';
        }

        private static bool IsSpace(byte b) {
            return b == ' ' || b >= '\t' && b <= '\r';
        }

        /// <summary>
        /// The length of the character the bytes have at <paramref name="start"/> in the encoding
        /// (MRI's rb_enc_precise_mbclen), or -1 if they begin none.
        /// </summary>
        internal static int CharacterLength(byte[]/*!*/ bytes, int start, RubyEncoding/*!*/ encoding) {
            int length = PreciseLength(bytes, start, bytes.Length - start, encoding);
            return length > 0 ? length : -1;
        }

        /// <summary>
        /// MRI's rb_enc_precise_mbclen for the <paramref name="count"/> bytes at
        /// <paramref name="start"/>: the length of the character they begin with, 0 if they are
        /// the beginning of one that is cut short, negative if they begin none.
        /// </summary>
        private static int PreciseLength(byte[]/*!*/ bytes, int start, int count, RubyEncoding/*!*/ encoding) {
            // every byte is a character of a single byte encoding, those it has no table entry
            // for (Windows-1252's 0x81) included; US-ASCII has none above 0x7F
            if (bytes[start] < 0x80 && encoding.IsAsciiIdentity || encoding == RubyEncoding.Binary ||
                encoding != RubyEncoding.Ascii && (encoding.IsSingleByteCharacterSet || encoding.Encoding.IsSingleByte)) {
                return 1;
            }

            var head = new byte[Math.Min(count, 8)];
            Buffer.BlockCopy(bytes, start, head, 0, head.Length);
            int maxLength;
            int length = RubyOnlyEncodings.PreciseLength(encoding.Encoding, head, head.Length, out maxLength);
            if (maxLength > 0) {
                return length;
            }

            // A decoder that is not flushed keeps a character it has only begun to itself: no
            // characters yet, and no error either.
            Decoder decoder = encoding.StrictEncoding.GetDecoder();
            for (int k = 1; k <= Math.Min(count, 4); k++) {
                try {
                    decoder.Reset();
                    if (decoder.GetCharCount(bytes, start, k, false) > 0) {
                        return k;
                    }
                } catch (DecoderFallbackException) {
                    return -1;
                }
            }
            return count < 4 ? 0 : -1;
        }
    }
}
