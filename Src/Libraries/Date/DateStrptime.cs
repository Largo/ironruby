/* ****************************************************************************
 *
 * Date for IronRuby - Date._strptime over ASCII-only strings.
 *
 * date/format.rb's _strptime_i (a port of 1.9's) cut the subject with a regular expression per
 * directive and kept the fields in a Bag; called from Time.strptime it was twenty times CRuby's
 * date__strptime, and the gap was spread over every step (a String per token, a case over sixty
 * String literals, a MatchData and $1 per field). This is the same walk over the format, the
 * regular expressions written out by hand, for the case where the subject and the format are
 * both ASCII-only Strings: there \d, \s, [[:alpha:]] and \b are the ASCII classes and a
 * position is a byte. Anything else (another encoding, a number too long for a long) answers
 * false and format.rb does the work as before.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Date {

    internal sealed class DateStrptime {
        // The fields in the order format.rb's Bag first set them (its Hash keeps that order).
        private readonly List<string> _names = new List<string>();
        private readonly List<object> _values = new List<object>();
        private readonly string _str;
        private int _pos;
        private bool _unsupported;

        private static readonly string[] Days = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };
        private static readonly string[] Months = { "january", "february", "march", "april", "may", "june", "july",
            "august", "september", "october", "november", "december" };

        // (?:gmt|utc?)? in the order the alternation tries them
        private static readonly string[] ZonePrefixes = { "gmt", "utc", "ut", "" };

        private DateStrptime(string str) {
            _str = str;
        }

        /// <summary>
        /// Date._strptime(str, fmt) for ASCII-only str and fmt: a Hash, null when str does not match,
        /// or false when this cannot say and format.rb must. The Hash holds :sec_fraction and
        /// :seconds of %L, %N and %Q as [numerator, denominator] Arrays, and :offset as true after
        /// each :zone, for the Ruby side to make into a Rational and zone_to_diff's answer.
        /// </summary>
        internal static object Parse(RubyContext/*!*/ context, MutableString/*!*/ str, MutableString/*!*/ fmt) {
            var p = new DateStrptime(str.ToString());
            if (!p.Run(fmt.ToString())) {
                return p._unsupported ? (object)false : null;
            }
            if (p._unsupported) {
                return false;
            }

            object cent = p.Get("_cent");
            if (cent != null) {
                long c = (long)cent;
                if (!p.AddCent("cwyear", c) || !p.AddCent("year", c)) {
                    return false;
                }
            }
            object merid = p.Get("_merid");
            if (merid != null) {
                object hour = p.Get("hour");
                if (hour != null) {
                    p.Set("hour", (long)hour % 12 + (long)merid);
                }
            }

            var hash = new Hash(context);
            for (int i = 0; i < p._names.Count; i++) {
                string name = p._names[i];
                object value = p._values[i];
                if (name[0] == '_' || (value == null && name != "offset")) {
                    continue;
                }
                if (value is long l) {
                    value = Clr.ToRubyInteger(l);
                } else if (value is long[] frac) {
                    value = new RubyArray { Clr.ToRubyInteger(frac[0]), Clr.ToRubyInteger(frac[1]) };
                } else if (value is string s) {
                    value = MutableString.Create(s, str.Encoding);
                }
                hash[context.CreateAsciiSymbol(name)] = value;
            }
            if (p._pos < p._str.Length) {
                hash[context.CreateAsciiSymbol("leftover")] = MutableString.Create(p._str.Substring(p._pos), str.Encoding);
            }
            return hash;
        }

        private bool AddCent(string name, long cent) {
            object v = Get(name);
            if (v != null) {
                try {
                    Set(name, checked((long)v + cent * 100));
                } catch (OverflowException) {
                    return false;
                }
            }
            return true;
        }

        private object Get(string name) {
            int i = _names.IndexOf(name);
            return i >= 0 ? _values[i] : null;
        }

        private void Set(string name, object value) {
            int i = _names.IndexOf(name);
            if (i >= 0) {
                _values[i] = value;
            } else {
                _names.Add(name);
                _values.Add(value);
            }
        }

        #region subject

        private static bool IsDigit(char c) => c >= '0' && c <= '9';
        private static bool IsAlpha(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        // Onigmo's \s for ASCII: space, \t, \n, \v, \f, \r.
        private static bool IsSpace(char c) => c == ' ' || (c >= '\t' && c <= '\r');
        private static bool IsWord(char c) => IsAlpha(c) || IsDigit(c) || c == '_';

        private char At(int i) => i < _str.Length ? _str[i] : '\0';

        // \b at i, where the character before i is a word character: the one at i is not.
        private bool BoundaryAfterWord(int i) => i >= _str.Length || !IsWord(_str[i]);

        private bool StartsWithIgnoreCase(int i, string lower) {
            if (i + lower.Length > _str.Length) {
                return false;
            }
            for (int k = 0; k < lower.Length; k++) {
                char c = _str[i + k];
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                if (c != lower[k]) {
                    return false;
                }
            }
            return true;
        }

        // /\A([-+]?\d{1,max})/ (signed) or /\A(\d{1,max})/; max 0 for \d{1,}. The value and the
        // match's length (the sign counted), or false when there is no match. A number too long for
        // a long marks the parse unsupported.
        private bool Number(bool signed, int max, out long value, out int length) {
            value = 0; length = 0;
            int i = _pos;
            bool negative = false;
            if (signed && i < _str.Length && (_str[i] == '+' || _str[i] == '-')) {
                negative = _str[i] == '-';
                i++;
            }
            int start = i;
            while (i < _str.Length && IsDigit(_str[i]) && (max == 0 || i - start < max)) {
                i++;
            }
            if (i == start) {
                return false;
            }
            if (i - _pos > 18) {
                _unsupported = true;
                return false;
            }
            long v = 0;
            for (int k = start; k < i; k++) {
                v = v * 10 + (_str[k] - '0');
            }
            value = negative ? -v : v;
            length = i - _pos;
            _pos = i;
            return true;
        }

        // /\A( \d|\d{1,2})/
        private bool SpaceDigitOrTwo(out long value) {
            if (At(_pos) == ' ' && IsDigit(At(_pos + 1))) {
                value = _str[_pos + 1] - '0';
                _pos += 2;
                return true;
            }
            return Number(false, 2, out value, out _);
        }

        private bool Ranged(long value, long lo, long hi) => value >= lo && value <= hi;

        // /\A(name1|name2|...)/i over full names, then over their first three letters.
        private bool Name(string[] names, out int index) {
            for (int k = 0; k < names.Length; k++) {
                if (StartsWithIgnoreCase(_pos, names[k])) {
                    _pos += names[k].Length;
                    index = k;
                    return true;
                }
            }
            for (int k = 0; k < names.Length; k++) {
                if (StartsWithIgnoreCase(_pos, names[k].Substring(0, 3))) {
                    _pos += 3;
                    index = k;
                    return true;
                }
            }
            index = -1;
            return false;
        }

        // The zone of %Z and %z:
        //   /\A((?:gmt|utc?)?[-+]\d+(?:[,.:]\d+(?::\d+)?)?
        //      |[[:alpha:].\s]+(?:standard|daylight)\s+time\b
        //      |[[:alpha:]]+(?:\s+dst)?\b
        //      )/ix
        private int ZoneEnd() {
            int end;
            foreach (string prefix in ZonePrefixes) {
                if (StartsWithIgnoreCase(_pos, prefix) && (end = OffsetEnd(_pos + prefix.Length)) >= 0) {
                    return end;
                }
            }

            int run = _pos;
            while (run < _str.Length && (IsAlpha(_str[run]) || _str[run] == '.' || IsSpace(_str[run]))) {
                run++;
            }
            for (int i = run; i > _pos; i--) {
                if (StartsWithIgnoreCase(i, "standard") || StartsWithIgnoreCase(i, "daylight")) {
                    int k = i + 8;
                    int spaces = k;
                    while (k < _str.Length && IsSpace(_str[k])) {
                        k++;
                    }
                    if (k > spaces && StartsWithIgnoreCase(k, "time") && BoundaryAfterWord(k + 4)) {
                        return k + 4;
                    }
                }
            }

            int letters = _pos;
            while (letters < _str.Length && IsAlpha(_str[letters])) {
                letters++;
            }
            if (letters > _pos) {
                int k = letters;
                while (k < _str.Length && IsSpace(_str[k])) {
                    k++;
                }
                if (k > letters && StartsWithIgnoreCase(k, "dst") && BoundaryAfterWord(k + 3)) {
                    return k + 3;
                }
                // a shorter run of letters ends between two letters, where there is no \b
                if (BoundaryAfterWord(letters)) {
                    return letters;
                }
            }
            return -1;
        }

        // [-+]\d+(?:[,.:]\d+(?::\d+)?)? from i: where it ends, or -1.
        private int OffsetEnd(int i) {
            if (i >= _str.Length || (_str[i] != '+' && _str[i] != '-')) {
                return -1;
            }
            int k = Digits(i + 1);
            if (k < 0) {
                return -1;
            }
            char c = At(k);
            int j;
            if ((c == ',' || c == '.' || c == ':') && (j = Digits(k + 1)) >= 0) {
                k = j;
                int m;
                if (At(k) == ':' && (m = Digits(k + 1)) >= 0) {
                    k = m;
                }
            }
            return k;
        }

        private int Digits(int i) {
            int k = i;
            while (k < _str.Length && IsDigit(_str[k])) {
                k++;
            }
            return k > i ? k : -1;
        }

        #endregion

        // /\A%[EO]?[CDdeFGgHIjkLlMmNQRrSsTUuVvWwXxYy\d]/ =~ s || /\A\d/ =~ s
        private static bool NumPattern(string fmt, int pos) {
            if (pos >= fmt.Length) {
                return false;
            }
            char c = fmt[pos];
            if (IsDigit(c)) {
                return true;
            }
            if (c != '%' || pos + 1 >= fmt.Length) {
                return false;
            }
            c = fmt[pos + 1];
            if ((c == 'E' || c == 'O') && pos + 2 < fmt.Length && IsNumDirective(fmt[pos + 2])) {
                return true;
            }
            return IsNumDirective(c);
        }

        private static bool IsNumDirective(char c) =>
            IsDigit(c) || "CDdeFGgHIjkLlMmNQRrSsTUuVvWwXxYy".IndexOf(c) >= 0;

        // the subject at the position starts with fmt[start, length]: a token matched as itself
        private bool Literal(string fmt, int start, int length) {
            if (_pos + length <= _str.Length && string.CompareOrdinal(_str, _pos, fmt, start, length) == 0) {
                _pos += length;
                return true;
            }
            return false;
        }

        private bool Run(string fmt) {
            int pos = 0;
            int size = fmt.Length;
            while (pos < size) {
                string s = null;
                int len;
                char ch = fmt[pos];
                if (ch == '%' && pos + 1 < size) {
                    int k = pos + 1;
                    char prefix = fmt[k];
                    if ((prefix == 'E' || prefix == 'O') && k + 1 < size) {
                        k++;
                    }
                    int m = k;
                    while (m < size && m - k < 3 && fmt[m] == ':') {
                        m++;
                    }
                    if (m > k && m < size && fmt[m] == 'z') {
                        k = m;
                    }
                    s = fmt.Substring(pos + 1, k - pos);
                    len = k + 1 - pos;
                } else {
                    len = 1;
                }
                int start = pos;
                pos += len;

                if (s == null) {
                    if (IsSpace(ch)) {
                        while (_pos < _str.Length && IsSpace(_str[_pos])) {
                            _pos++;
                        }
                    } else if (!Literal(fmt, start, len)) {
                        return false;
                    }
                    continue;
                }

                long v;
                int n;
                switch (s) {
                    case "A": case "a":
                        if (!Name(Days, out n)) return false;
                        Set("wday", (long)n);
                        break;
                    case "B": case "b": case "h":
                        if (!Name(Months, out n)) return false;
                        Set("mon", (long)n + 1);
                        break;
                    case "C": case "EC":
                        if (!Number(true, NumPattern(fmt, pos) ? 2 : 0, out v, out _)) return false;
                        Set("_cent", v);
                        break;
                    case "c": case "Ec":
                        if (!Run("%a %b %e %H:%M:%S %Y")) return false;
                        break;
                    case "D": case "x": case "Ex":
                        if (!Run("%m/%d/%y")) return false;
                        break;
                    case "d": case "e": case "Od": case "Oe":
                        if (!SpaceDigitOrTwo(out v) || !Ranged(v, 1, 31)) return false;
                        Set("mday", v);
                        break;
                    case "F":
                        if (!Run("%Y-%m-%d")) return false;
                        break;
                    case "G":
                        if (!Number(true, NumPattern(fmt, pos) ? 4 : 0, out v, out _)) return false;
                        Set("cwyear", v);
                        break;
                    case "g":
                        if (!Number(false, 2, out v, out _) || !Ranged(v, 0, 99)) return false;
                        Set("cwyear", v);
                        if (Get("_cent") == null) Set("_cent", v >= 69 ? 19L : 20L);
                        break;
                    case "H": case "k": case "OH":
                        if (!SpaceDigitOrTwo(out v) || !Ranged(v, 0, 24)) return false;
                        Set("hour", v);
                        break;
                    case "I": case "l": case "OI":
                        if (!SpaceDigitOrTwo(out v) || !Ranged(v, 1, 12)) return false;
                        Set("hour", v);
                        break;
                    case "j":
                        if (!Number(false, 3, out v, out _) || !Ranged(v, 1, 366)) return false;
                        Set("yday", v);
                        break;
                    case "L":
                    case "N":
                        if (!Number(true, NumPattern(fmt, pos) ? (s == "L" ? 3 : 9) : 0, out v, out n)) return false;
                        // the denominator counts the digits, not the sign (date__strptime)
                        Set("sec_fraction", new long[] { v, Pow10(_str[_pos - n] == '+' || _str[_pos - n] == '-' ? n - 1 : n) });
                        break;
                    case "M": case "OM":
                        if (!Number(false, 2, out v, out _) || !Ranged(v, 0, 59)) return false;
                        Set("min", v);
                        break;
                    case "m": case "Om":
                        if (!Number(false, 2, out v, out _) || !Ranged(v, 1, 12)) return false;
                        Set("mon", v);
                        break;
                    case "n": case "t":
                        if (!Run(" ")) return false;
                        break;
                    case "P": case "p": {
                        char c = At(_pos);
                        if (c != 'a' && c != 'A' && c != 'p' && c != 'P') return false;
                        // "pmx" is a pm and a leftover "x": date__strptime looks for no word boundary
                        if (At(_pos + 1) == 'm' || At(_pos + 1) == 'M') {
                            _pos += 2;
                        } else if (At(_pos + 1) == '.' && (At(_pos + 2) == 'm' || At(_pos + 2) == 'M') && At(_pos + 3) == '.') {
                            _pos += 4;
                        } else {
                            return false;
                        }
                        Set("_merid", c == 'a' || c == 'A' ? 0L : 12L);
                        break;
                    }
                    case "Q":
                        if (!NegativeNumber(out v)) return false;
                        Set("seconds", new long[] { v, 1000 });
                        break;
                    case "R":
                        if (!Run("%H:%M")) return false;
                        break;
                    case "r":
                        if (!Run("%I:%M:%S %p")) return false;
                        break;
                    case "S": case "OS":
                        if (!Number(false, 2, out v, out _) || !Ranged(v, 0, 60)) return false;
                        Set("sec", v);
                        break;
                    case "s":
                        if (!NegativeNumber(out v)) return false;
                        Set("seconds", v);
                        break;
                    case "T": case "X": case "EX":
                        if (!Run("%H:%M:%S")) return false;
                        break;
                    case "U": case "W": case "OU": case "OW":
                        if (!Number(false, 2, out v, out _) || !Ranged(v, 0, 53)) return false;
                        Set(s[s.Length - 1] == 'U' ? "wnum0" : "wnum1", v);
                        break;
                    case "u": case "Ou":
                        if (!Number(false, 1, out v, out _) || !Ranged(v, 1, 7)) return false;
                        Set("cwday", v);
                        break;
                    case "V": case "OV":
                        if (!Number(false, 2, out v, out _) || !Ranged(v, 1, 53)) return false;
                        Set("cweek", v);
                        break;
                    case "v":
                        if (!Run("%e-%b-%Y")) return false;
                        break;
                    case "w":
                        if (!Number(false, 1, out v, out _) || !Ranged(v, 0, 6)) return false;
                        Set("wday", v);
                        break;
                    case "Y": case "EY":
                        if (!Number(true, NumPattern(fmt, pos) ? 4 : 0, out v, out _)) return false;
                        Set("year", v);
                        break;
                    case "y": case "Ey": case "Oy":
                        if (!Number(false, 2, out v, out _) || !Ranged(v, 0, 99)) return false;
                        Set("year", v);
                        if (Get("_cent") == null) Set("_cent", v >= 69 ? 19L : 20L);
                        break;
                    case "Z": case "z": case ":z": case "::z": case ":::z": {
                        int end = ZoneEnd();
                        if (end < 0) return false;
                        Set("zone", _str.Substring(_pos, end - _pos));
                        Set("offset", true);
                        _pos = end;
                        break;
                    }
                    case "%":
                        if (At(_pos) != '%') return false;
                        _pos++;
                        break;
                    case "+":
                        if (!Run("%a %b %e %H:%M:%S %Z %Y")) return false;
                        break;
                    default:
                        if (!Literal(fmt, start, len)) return false;
                        break;
                }
                if (_unsupported) {
                    return false;
                }
            }
            return true;
        }

        // /\A(-?\d{1,})/
        private bool NegativeNumber(out long value) {
            if (At(_pos) == '-') {
                int start = _pos;
                _pos++;
                if (!Number(false, 0, out value, out _)) {
                    _pos = start;
                    return false;
                }
                value = -value;
                return true;
            }
            return Number(false, 0, out value, out _);
        }

        private static long Pow10(int n) {
            long r = 1;
            while (n-- > 0) r *= 10;
            return r;
        }
    }
}
