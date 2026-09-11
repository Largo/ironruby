/* ****************************************************************************
 *
 * Support types for IronRuby's Time implementation: exact (rational) arithmetic
 * so that sub-microsecond precision survives, and a minimal TZif reader so that
 * Time#zone / %Z report real zone abbreviations on Unix.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace IronRuby.Builtins {

    /// <summary>
    /// An exact non-negative-denominator rational number. Ruby's Time keeps sub-second
    /// precision as a Rational; .NET's DateTime only has 100ns ticks, so the exact value
    /// is tracked here and the DateTime is derived from it for formatting.
    /// </summary>
    public struct ExactNum : IComparable<ExactNum>, IEquatable<ExactNum> {
        public readonly BigInteger Numerator;
        public readonly BigInteger Denominator;

        public static readonly ExactNum Zero = new ExactNum(BigInteger.Zero, BigInteger.One);
        public static readonly ExactNum One = new ExactNum(BigInteger.One, BigInteger.One);

        private ExactNum(BigInteger numerator, BigInteger denominator) {
            Numerator = numerator;
            Denominator = denominator;
        }

        public static ExactNum Make(BigInteger numerator, BigInteger denominator) {
            if (denominator.IsZero) {
                throw new DivideByZeroException();
            }
            if (denominator.Sign < 0) {
                numerator = -numerator;
                denominator = -denominator;
            }
            if (numerator.IsZero) {
                return Zero;
            }
            BigInteger gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            if (!gcd.IsOne) {
                numerator /= gcd;
                denominator /= gcd;
            }
            return new ExactNum(numerator, denominator);
        }

        public static ExactNum FromInteger(BigInteger value) {
            return new ExactNum(value, BigInteger.One);
        }

        /// <summary>
        /// Exact binary expansion of a double, matching Float#to_r.
        /// </summary>
        public static ExactNum FromDouble(double value) {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) {
                throw new OverflowException();
            }
            if (value == 0.0) {
                return Zero;
            }

            long bits = BitConverter.DoubleToInt64Bits(value);
            bool negative = bits < 0;
            int exponent = (int)((bits >> 52) & 0x7FF);
            long mantissa = bits & 0xFFFFFFFFFFFFFL;

            if (exponent == 0) {
                exponent = -1074;
            } else {
                mantissa |= 1L << 52;
                exponent = exponent - 1075;
            }

            BigInteger num = mantissa;
            BigInteger den = BigInteger.One;
            if (exponent > 0) {
                num <<= exponent;
            } else if (exponent < 0) {
                den <<= -exponent;
            }
            if (negative) {
                num = -num;
            }
            return Make(num, den);
        }

        public bool IsZero {
            get { return Numerator.IsZero; }
        }

        public bool IsInteger {
            get { return Denominator.IsOne; }
        }

        public int Sign {
            get { return Numerator.Sign; }
        }

        public static ExactNum operator +(ExactNum x, ExactNum y) {
            return Make(x.Numerator * y.Denominator + y.Numerator * x.Denominator, x.Denominator * y.Denominator);
        }

        public static ExactNum operator -(ExactNum x, ExactNum y) {
            return Make(x.Numerator * y.Denominator - y.Numerator * x.Denominator, x.Denominator * y.Denominator);
        }

        public static ExactNum operator -(ExactNum x) {
            return new ExactNum(-x.Numerator, x.Denominator);
        }

        public static ExactNum operator *(ExactNum x, ExactNum y) {
            return Make(x.Numerator * y.Numerator, x.Denominator * y.Denominator);
        }

        public static ExactNum operator /(ExactNum x, ExactNum y) {
            return Make(x.Numerator * y.Denominator, x.Denominator * y.Numerator);
        }

        /// <summary>Largest integer not greater than this value.</summary>
        public BigInteger Floor() {
            BigInteger rem;
            BigInteger q = BigInteger.DivRem(Numerator, Denominator, out rem);
            if (rem.Sign < 0) {
                q -= BigInteger.One;
            }
            return q;
        }

        public BigInteger Ceiling() {
            BigInteger rem;
            BigInteger q = BigInteger.DivRem(Numerator, Denominator, out rem);
            if (rem.Sign > 0) {
                q += BigInteger.One;
            }
            return q;
        }

        /// <summary>Round half up (away from zero for .5), matching Ruby's Time#round.</summary>
        public BigInteger Round() {
            ExactNum half = Make(BigInteger.One, 2);
            if (Sign >= 0) {
                return (this + half).Floor();
            }
            return (this - half).Ceiling();
        }

        public double ToDouble() {
            if (Denominator.IsOne) {
                return (double)Numerator;
            }
            // Scale to preserve precision for very large numerators.
            return (double)Numerator / (double)Denominator;
        }

        public int CompareTo(ExactNum other) {
            return (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
        }

        public bool Equals(ExactNum other) {
            return Numerator == other.Numerator && Denominator == other.Denominator;
        }

        public override bool Equals(object obj) {
            return obj is ExactNum && Equals((ExactNum)obj);
        }

        public override int GetHashCode() {
            return Numerator.GetHashCode() ^ Denominator.GetHashCode();
        }

        public override string ToString() {
            return Numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Minimal reader for the TZif (zoneinfo) files shipped on Unix. .NET's TimeZoneInfo
    /// exposes only long names ("Central European Standard Time"), but Ruby's Time#zone and
    /// strftime %Z report the POSIX abbreviation ("CET"/"CEST"), which only the raw file has.
    /// </summary>
    internal sealed class TzFile {
        private readonly long[]/*!*/ _transitions;      // UTC seconds since epoch
        private readonly byte[]/*!*/ _typeIndex;
        private readonly int[]/*!*/ _offsets;           // seconds
        private readonly bool[]/*!*/ _isDst;
        private readonly string[]/*!*/ _abbreviations;
        private readonly int _defaultType;

        private static readonly Dictionary<string, TzFile> _cache = new Dictionary<string, TzFile>();
        private static readonly object _cacheLock = new object();

        private TzFile(long[]/*!*/ transitions, byte[]/*!*/ typeIndex, int[]/*!*/ offsets, bool[]/*!*/ isDst, string[]/*!*/ abbreviations) {
            _transitions = transitions;
            _typeIndex = typeIndex;
            _offsets = offsets;
            _isDst = isDst;
            _abbreviations = abbreviations;

            int def = 0;
            for (int i = 0; i < isDst.Length; i++) {
                if (!isDst[i]) {
                    def = i;
                    break;
                }
            }
            _defaultType = def;
        }

        internal static TzFile TryLoadFile(string/*!*/ path) {
            try {
                return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null;
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            } catch (ArgumentException) {
            }
            return null;
        }

        /// <summary>Returns null if the zone has no zoneinfo file (e.g. on Windows, or a synthetic zone).</summary>
        internal static TzFile TryLoad(string id) {
            if (String.IsNullOrEmpty(id) || id.IndexOf("..", StringComparison.Ordinal) >= 0 || Path.IsPathRooted(id)) {
                return null;
            }

            lock (_cacheLock) {
                TzFile cached;
                if (_cache.TryGetValue(id, out cached)) {
                    return cached;
                }
            }

            TzFile result = null;
            foreach (string root in new[] { "/usr/share/zoneinfo/", "/usr/lib/zoneinfo/", "/etc/zoneinfo/" }) {
                string path = root + id;
                try {
                    if (File.Exists(path)) {
                        result = Parse(File.ReadAllBytes(path));
                        if (result != null) {
                            break;
                        }
                    }
                } catch (IOException) {
                } catch (UnauthorizedAccessException) {
                } catch (ArgumentException) {
                }
            }

            lock (_cacheLock) {
                _cache[id] = result;
            }
            return result;
        }

        private static int ReadInt32(byte[]/*!*/ data, int offset) {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        private static long ReadInt64(byte[]/*!*/ data, int offset) {
            long result = 0;
            for (int i = 0; i < 8; i++) {
                result = (result << 8) | data[offset + i];
            }
            return result;
        }

        private static TzFile Parse(byte[]/*!*/ data) {
            if (data.Length < 44 || data[0] != 'T' || data[1] != 'Z' || data[2] != 'i' || data[3] != 'f') {
                return null;
            }

            byte version = data[4];
            int pos = 20;

            int isUtcCount = ReadInt32(data, pos); pos += 4;
            int isStdCount = ReadInt32(data, pos); pos += 4;
            int leapCount = ReadInt32(data, pos); pos += 4;
            int timeCount = ReadInt32(data, pos); pos += 4;
            int typeCount = ReadInt32(data, pos); pos += 4;
            int charCount = ReadInt32(data, pos); pos += 4;

            if (version >= '2') {
                // Skip the 32-bit block entirely and use the 64-bit one, which is authoritative.
                int v1Size = timeCount * 5 + typeCount * 6 + charCount + leapCount * 8 + isStdCount + isUtcCount;
                pos += v1Size;
                if (pos + 44 > data.Length ||
                    data[pos] != 'T' || data[pos + 1] != 'Z' || data[pos + 2] != 'i' || data[pos + 3] != 'f') {
                    return null;
                }
                pos += 20;
                isUtcCount = ReadInt32(data, pos); pos += 4;
                isStdCount = ReadInt32(data, pos); pos += 4;
                leapCount = ReadInt32(data, pos); pos += 4;
                timeCount = ReadInt32(data, pos); pos += 4;
                typeCount = ReadInt32(data, pos); pos += 4;
                charCount = ReadInt32(data, pos); pos += 4;

                return ParseBlock(data, pos, timeCount, typeCount, charCount, true);
            }

            return ParseBlock(data, pos, timeCount, typeCount, charCount, false);
        }

        private static TzFile ParseBlock(byte[]/*!*/ data, int pos, int timeCount, int typeCount, int charCount, bool wide) {
            if (timeCount < 0 || typeCount <= 0 || charCount < 0) {
                return null;
            }

            int timeSize = wide ? 8 : 4;
            long needed = (long)timeCount * (timeSize + 1) + (long)typeCount * 6 + charCount;
            if (pos + needed > data.Length) {
                return null;
            }

            long[] transitions = new long[timeCount];
            for (int i = 0; i < timeCount; i++) {
                transitions[i] = wide ? ReadInt64(data, pos) : ReadInt32(data, pos);
                pos += timeSize;
            }

            byte[] typeIndex = new byte[timeCount];
            for (int i = 0; i < timeCount; i++) {
                typeIndex[i] = data[pos++];
            }

            int[] offsets = new int[typeCount];
            bool[] isDst = new bool[typeCount];
            int[] abbrIndex = new int[typeCount];
            for (int i = 0; i < typeCount; i++) {
                offsets[i] = ReadInt32(data, pos); pos += 4;
                isDst[i] = data[pos++] != 0;
                abbrIndex[i] = data[pos++];
            }

            string chars = System.Text.Encoding.ASCII.GetString(data, pos, charCount);
            string[] abbreviations = new string[typeCount];
            for (int i = 0; i < typeCount; i++) {
                int start = abbrIndex[i];
                if (start < 0 || start >= chars.Length) {
                    abbreviations[i] = "";
                    continue;
                }
                int end = chars.IndexOf('\0', start);
                abbreviations[i] = (end < 0) ? chars.Substring(start) : chars.Substring(start, end - start);
            }

            return new TzFile(transitions, typeIndex, offsets, isDst, abbreviations);
        }

        private int FindType(long unixSeconds) {
            if (_transitions.Length == 0 || unixSeconds < _transitions[0]) {
                return _defaultType;
            }

            int lo = 0, hi = _transitions.Length - 1;
            while (lo < hi) {
                int mid = lo + (hi - lo + 1) / 2;
                if (_transitions[mid] <= unixSeconds) {
                    lo = mid;
                } else {
                    hi = mid - 1;
                }
            }
            int type = _typeIndex[lo];
            return (type < _offsets.Length) ? type : _defaultType;
        }

        internal int GetOffset(long unixSeconds) {
            return _offsets[FindType(unixSeconds)];
        }

        internal string GetAbbreviation(long unixSeconds) {
            string result = _abbreviations[FindType(unixSeconds)];
            return String.IsNullOrEmpty(result) ? null : result;
        }

        internal bool IsDaylightSavingTime(long unixSeconds) {
            return _isDst[FindType(unixSeconds)];
        }
    }
}

namespace IronRuby.Builtins {

    /// <summary>
    /// A time zone as Ruby sees it. Backed by the zoneinfo file when one exists, because that
    /// carries the DST transitions and the POSIX abbreviations that Ruby reports; otherwise a
    /// plain fixed offset parsed out of a POSIX TZ string.
    /// </summary>
    public sealed class RubyTimeZone {
        private readonly TzFile _file;          // null for a fixed-offset zone
        private readonly long _fixedOffset;     // seconds east of UTC
        private readonly string/*!*/ _name;

        public static readonly RubyTimeZone/*!*/ Utc = new RubyTimeZone(null, 0, "UTC");

        private RubyTimeZone(TzFile file, long fixedOffset, string/*!*/ name) {
            _file = file;
            _fixedOffset = fixedOffset;
            _name = name;
        }

        internal static RubyTimeZone/*!*/ MakeFixed(long offsetSeconds, string/*!*/ name) {
            return new RubyTimeZone(null, offsetSeconds, name);
        }

        internal static RubyTimeZone FromId(string/*!*/ id) {
            TzFile file = TzFile.TryLoad(id);
            return (file != null) ? new RubyTimeZone(file, 0, id) : null;
        }

        /// <summary>The machine's zone, used when TZ is unset.</summary>
        internal static RubyTimeZone/*!*/ Machine() {
            TzFile file = TzFile.TryLoadFile("/etc/localtime");
            if (file != null) {
                return new RubyTimeZone(file, 0, System.TimeZoneInfo.Local.Id);
            }
            var local = System.TimeZoneInfo.Local;
            return MakeFixed((long)local.BaseUtcOffset.TotalSeconds, local.StandardName);
        }

        internal long GetOffset(long unixSeconds) {
            return (_file != null) ? _file.GetOffset(unixSeconds) : _fixedOffset;
        }

        /// <summary>
        /// The offset that applies to a wall-clock reading. Two passes, because the offset
        /// itself is what turns the reading into the instant it must be looked up by.
        /// </summary>
        internal long GetOffsetForWallClock(long wallSeconds) {
            if (_file == null) {
                return _fixedOffset;
            }
            long guess = _file.GetOffset(wallSeconds);
            return _file.GetOffset(wallSeconds - guess);
        }

        internal string/*!*/ GetAbbreviation(long unixSeconds) {
            if (_file != null) {
                string result = _file.GetAbbreviation(unixSeconds);
                if (result != null) {
                    return result;
                }
            }
            return _name;
        }

        internal bool IsDaylightSavingTime(long unixSeconds) {
            return _file != null && _file.IsDaylightSavingTime(unixSeconds);
        }

        internal string/*!*/ Name {
            get { return _name; }
        }
    }
}
