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
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace IronRuby.Builtins {

    /// <summary>
    /// Byte offsets into the UTF-8 form of a string of valid UTF-16 characters (one snapshot of a
    /// MutableString's characters, ConvertToString's), and back. Offsets are converted by walking the
    /// characters from a cursor - the last offset converted - so a reader that moves forward through
    /// the string (StringScanner, a loop of String#byteindex from the last answer) pays about the
    /// distance it moved, where counting from the start each time made such a loop quadratic.
    /// The caller sees to it that the text has no unpaired surrogate (the string is valid UTF-8).
    /// </summary>
    internal sealed class Utf8OffsetMap {
        internal readonly string/*!*/ Text;
        private int _byteLength = -1;
        private int _hasPairs = -1;

        // the byte offset in the high half, the CLR index in the low half: one field, so that threads
        // sharing a map can only see a cursor that was right at some point
        private long _cursor;

        // Keyed by the immutable snapshot, as MutableString's character index tables are: a string
        // that changes gets a new snapshot, and a snapshot nobody holds takes its map with it.
        private static readonly ConditionalWeakTable<string, Utf8OffsetMap> _maps = new ConditionalWeakTable<string, Utf8OffsetMap>();

        internal Utf8OffsetMap(string/*!*/ text) {
            Text = text;
        }

        /// <summary>The map shared by every reader of this snapshot (String#byteindex and #byterindex).</summary>
        internal static Utf8OffsetMap/*!*/ For(string/*!*/ text) {
            return _maps.GetValue(text, t => new Utf8OffsetMap(t));
        }

        internal int ByteLength {
            get {
                int length = _byteLength;
                if (length < 0) {
                    _byteLength = length = Encoding.UTF8.GetByteCount(Text);
                }
                return length;
            }
        }

        /// <summary>Whether a character of the text is a surrogate pair (two CLR chars for one Ruby character).</summary>
        internal bool HasPairs {
            get {
                int pairs = _hasPairs;
                if (pairs < 0) {
                    pairs = 0;
                    foreach (char c in Text) {
                        if (Char.IsLowSurrogate(c)) {
                            pairs = 1;
                            break;
                        }
                    }
                    _hasPairs = pairs;
                }
                return pairs != 0;
            }
        }

        /// <summary>
        /// The CLR index of byte offset <paramref name="byteOffset"/>: -1 when it is inside a character,
        /// -2 when it is past the end (or negative).
        /// </summary>
        internal int ClrOf(int byteOffset) {
            if (byteOffset < 0) {
                return -2;
            }
            int b, c;
            if (Seek(true, byteOffset, out b, out c)) {
                return c;
            }
            return (b < byteOffset && c >= Text.Length) ? -2 : -1;
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
                while ((byBytes ? b : c) < target && c < text.Length) {
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
}
