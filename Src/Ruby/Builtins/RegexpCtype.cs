/* ****************************************************************************
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace IronRuby.Builtins {
    /// <summary>
    /// Onigmo's ctype table of a single byte encoding - BINARY (and US-ASCII), ISO-8859-*, KOI8-*,
    /// Windows-125*, the IBM and mac code pages - over the characters IronRuby decodes its bytes
    /// to. MRI compiles a regexp that is not fixed-encoding for the subject's encoding
    /// (rb_reg_prepare_enc), and each of these tables is its own where .NET knows only Unicode's:
    /// ISO-8859-1's punctuation and spaces, Windows-1251's, and none at all above 0x7F in the
    /// tables Onigmo makes replicas of ASCII-8BIT's. RegexpTransformer spells a table out when the
    /// subject is in its encoding (SharedState.Ctype). The data is CRuby's, byte by byte
    /// (RegexpCtype.Generated.cs, Util/gen-regexp-ctype.rb).
    /// </summary>
    internal sealed partial class RegexpCtype {
        // the order of the classes in Data.Posix
        private static readonly string[] PosixNames = {
            "alnum", "alpha", "ascii", "blank", "cntrl", "digit", "graph", "lower", "print", "punct", "space", "upper", "word", "xdigit",
        };

        private sealed class Data {
            public readonly string[]/*!*/ Encodings;
            public readonly ulong[]/*!*/ Posix;
            public readonly object[]/*!*/ Properties;
            public readonly ushort[]/*!*/ Folds;
            // null where a class folds as a literal does
            public readonly ushort[] ClassFolds;

            public Data(string[]/*!*/ encodings, ulong[]/*!*/ posix, object[]/*!*/ properties, ushort[]/*!*/ folds, ushort[] classFolds) {
                Encodings = encodings;
                Posix = posix;
                Properties = properties;
                Folds = folds;
                ClassFolds = classFolds;
            }
        }

        private static Dictionary<string, Data> _byName;

        // asked on every match of a subject in a single byte encoding: read without a lock
        private static readonly ConcurrentDictionary<RubyEncoding, RegexpCtype> _tables = new ConcurrentDictionary<RubyEncoding, RegexpCtype>();

        /// <summary>
        /// The table of <paramref name="encoding"/>'s regexps, or null for an encoding it has
        /// none of (a multibyte, Unicode or dummy one).
        /// </summary>
        internal static RegexpCtype For(RubyEncoding/*!*/ encoding) {
            RegexpCtype result;
            if (_tables.TryGetValue(encoding, out result)) {
                return result;
            }

            lock (_tables) {
                if (_byName == null) {
                    var byName = new Dictionary<string, Data>(StringComparer.Ordinal);
                    foreach (var data in _data) {
                        foreach (var name in data.Encodings) {
                            byName[name] = data;
                        }
                    }
                    _byName = byName;
                }

                Data table;
                result = _byName.TryGetValue(encoding.Name, out table) ? new RegexpCtype(encoding, table) : null;
                return _tables.GetOrAdd(encoding, result);
            }
        }

        private readonly Data/*!*/ _table;

        // The character each byte reads as in IronRuby (-1 for a byte that is none), and all of them.
        private readonly int[]/*!*/ _chars = new int[256];
        private readonly int[]/*!*/ _charRanges;
        private readonly int[]/*!*/ _escaped;

        private readonly Dictionary<string, int[]>/*!*/ _classes = new Dictionary<string, int[]>();

        private RegexpCtype(RubyEncoding/*!*/ encoding, Data/*!*/ data) {
            _table = data;
            bool identity = encoding == RubyEncoding.Binary || encoding == RubyEncoding.Ascii;
            var all = new List<int>();
            for (int b = 0; b < 256; b++) {
                int c = (b < 0x80 || identity) ? b : Decode(encoding, (byte)b);
                _chars[b] = c;
                if (c >= 0) {
                    all.Add(c);
                }
            }
            _charRanges = ToRanges(all);
            _escaped = CodePointRanges.Intersect(_charRanges, CodePointRanges.Single(0xdc80, 0xdcff));
        }

        // As a string reads it: a byte the encoding leaves undefined is a lone surrogate, U+DC00
        // plus the byte (EscapingEncoding).
        private static int Decode(RubyEncoding/*!*/ encoding, byte b) {
            try {
                string s = encoding.EscapingEncoding.GetString(new byte[] { b });
                return (s.Length == 1) ? s[0] : -1;
            } catch (Exception) {
                // one .NET cannot decode at all
                return -1;
            }
        }

        private static int[]/*!*/ ToRanges(List<int>/*!*/ codepoints) {
            codepoints.Sort();
            var ranges = new List<int>();
            foreach (int c in codepoints) {
                int n = ranges.Count;
                if (n != 0 && ranges[n - 1] >= c - 1) {
                    ranges[n - 1] = Math.Max(ranges[n - 1], c);
                } else {
                    ranges.Add(c);
                    ranges.Add(c);
                }
            }
            return ranges.ToArray();
        }

        private int[]/*!*/ Members(ulong[]/*!*/ masks, int offset) {
            var members = new List<int>();
            for (int b = 0; b < 256; b++) {
                if ((masks[offset + b / 64] & (1UL << (b % 64))) != 0 && _chars[b] >= 0) {
                    members.Add(_chars[b]);
                }
            }
            return ToRanges(members);
        }

        /// <summary>Every character a string in the encoding can hold, as code point ranges.</summary>
        internal int[]/*!*/ Characters {
            get { return _charRanges; }
        }

        /// <summary>
        /// The lone surrogates the bytes the encoding leaves undefined read as (Windows-1252's 0x81
        /// is U+DC81), as code point ranges. Onigmo takes them for characters that are in no class.
        /// </summary>
        internal int[]/*!*/ Escaped {
            get { return _escaped; }
        }

        /// <summary>
        /// [[:name:]] (<paramref name="name"/> lower case, one of the 14 POSIX names), or
        /// \p{name} with <paramref name="property"/>, as code point ranges.
        /// </summary>
        internal int[]/*!*/ Class(string/*!*/ name, bool property) {
            string key = property ? "p" + name : name;
            lock (_classes) {
                int[] result;
                if (_classes.TryGetValue(key, out result)) {
                    return result;
                }

                if (property) {
                    for (int i = 0; i < _table.Properties.Length; i += 2) {
                        if ((string)_table.Properties[i] == name) {
                            result = Members((ulong[])_table.Properties[i + 1], 0);
                        }
                    }
                }
                if (result == null) {
                    int index = Array.IndexOf(PosixNames, name);
                    if (index < 0) {
                        throw new ArgumentException(name);
                    }
                    result = Members(_table.Posix, index * 4);
                }
                _classes[key] = result;
                return result;
            }
        }

        #region Case folding

        /// <summary>
        /// The case folds that differ from .NET's IgnoreCase among the encoding's characters, which
        /// is where a translation under /i has to spell CRuby's out: ISO-8859-7's final sigma folds
        /// with no other sigma, the IBM code pages fold nothing above 0x7F, ISO-8859-9's dotted
        /// capital I folds with both i and I - as a literal, but not in a class -, Windows-1252's
        /// multiplication sign with its division sign. Worked out on first use, by asking .NET
        /// about every pair.
        /// </summary>
        private sealed class Folds {
            // The characters whose folds differ, as a literal or in a class, as ranges; for each, the
            // characters a literal of it matches under /i (itself included), and the characters
            // that take it in under /i when they are in a class - in CRuby, and with .NET's
            // IgnoreCase.
            public int[] Unfolded;
            public Dictionary<int, int[]> Targets;
            public Dictionary<int, int[]> Sources;
            public Dictionary<int, int[]> ClrSources;
        }

        private Folds _folds;

        private Folds/*!*/ GetFolds() {
            Folds folds = Volatile.Read(ref _folds);
            if (folds != null) {
                return folds;
            }

            var chars = new List<int>();
            for (int b = 0; b < 256; b++) {
                if (_chars[b] >= 0 && !chars.Contains(_chars[b])) {
                    chars.Add(_chars[b]);
                }
            }

            var ruby = RubyFolds(chars, _table.Folds);
            var rubyClass = (_table.ClassFolds != null) ? RubyFolds(chars, _table.ClassFolds) : ruby;

            var clr = new HashSet<long>();
            foreach (int a in chars) {
                var literal = new Regex("\\A" + Regex.Escape(((char)a).ToString()) + "\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                foreach (int b in chars) {
                    if (literal.IsMatch(((char)b).ToString())) {
                        clr.Add(Pair(a, b));
                    }
                }
            }

            var unfolded = new List<int>();
            folds = new Folds { Targets = new Dictionary<int, int[]>(), Sources = new Dictionary<int, int[]>(), ClrSources = new Dictionary<int, int[]>() };
            foreach (int c in chars) {
                bool differs = false;
                var targets = new List<int>();
                var sources = new List<int>();
                var clrSources = new List<int>();
                foreach (int x in chars) {
                    bool rubyTo = ruby.Contains(Pair(c, x)), rubyFrom = rubyClass.Contains(Pair(x, c));
                    differs |= rubyTo != clr.Contains(Pair(c, x)) || ruby.Contains(Pair(x, c)) != clr.Contains(Pair(x, c)) ||
                        rubyFrom != clr.Contains(Pair(x, c)) || rubyClass.Contains(Pair(c, x)) != clr.Contains(Pair(c, x));
                    if (rubyTo) {
                        targets.Add(x);
                    }
                    if (rubyFrom) {
                        sources.Add(x);
                    }
                    if (clr.Contains(Pair(x, c))) {
                        clrSources.Add(x);
                    }
                }
                if (differs) {
                    unfolded.Add(c);
                    folds.Targets[c] = ToRanges(targets);
                    folds.Sources[c] = sources.ToArray();
                    folds.ClrSources[c] = clrSources.ToArray();
                }
            }
            folds.Unfolded = ToRanges(unfolded);
            // two threads may both work it out, to equal results
            Volatile.Write(ref _folds, folds);
            return folds;
        }

        // CRuby's folds, from the generated pairs: a character folds onto itself - but where a
        // byte is listed with itself -, an ASCII letter onto its other case, and the rest as listed
        private HashSet<long>/*!*/ RubyFolds(List<int>/*!*/ chars, ushort[]/*!*/ pairs) {
            var notSelf = new HashSet<int>();
            foreach (ushort fold in pairs) {
                if ((fold >> 8) == (fold & 0xff) && _chars[fold & 0xff] >= 0) {
                    notSelf.Add(_chars[fold & 0xff]);
                }
            }
            var result = new HashSet<long>();
            foreach (int c in chars) {
                if (!notSelf.Contains(c)) {
                    result.Add(Pair(c, c));
                }
                if (c < 0x80 && Char.IsLetter((char)c)) {
                    result.Add(Pair(c, Char.IsUpper((char)c) ? Char.ToLowerInvariant((char)c) : Char.ToUpperInvariant((char)c)));
                }
            }
            foreach (ushort fold in pairs) {
                int a = _chars[fold >> 8], b = _chars[fold & 0xff];
                if (a >= 0 && b >= 0 && a != b) {
                    result.Add(Pair(a, b));
                }
            }
            return result;
        }

        private static long Pair(int a, int b) {
            return ((long)a << 32) | (uint)b;
        }

        /// <summary>The characters whose case folds .NET would get wrong in this encoding, as ranges.</summary>
        internal int[]/*!*/ Unfolded {
            get { return GetFolds().Unfolded; }
        }

        /// <summary>
        /// For one of the Unfolded characters, the characters a literal of it matches under /i in
        /// CRuby, itself included, as ranges; null for any other character.
        /// </summary>
        internal int[] FoldTargets(int c) {
            int[] result;
            return GetFolds().Targets.TryGetValue(c, out result) ? result : null;
        }

        /// <summary>
        /// For one of the Unfolded characters, the characters that take it in under /i when they
        /// are members of a class - in CRuby, or with .NET's IgnoreCase - itself included.
        /// </summary>
        internal int[]/*!*/ FoldSources(int c, bool clr) {
            var folds = GetFolds();
            return (clr ? folds.ClrSources : folds.Sources)[c];
        }

        #endregion
    }
}
