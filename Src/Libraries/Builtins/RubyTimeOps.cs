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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {

    // Keep the Ruby methods separate so that we can expose CLR methods on RubyTime.
    [RubyClass("Time", Extends = typeof(RubyTime), Inherits = typeof(object))]
    [Includes(typeof(Comparable))]
    public static class RubyTimeOps {

        #region Dynamic call helpers

        private static CallSite<Func<CallSite, object, object>> _numeratorSite;
        private static CallSite<Func<CallSite, object, object>> _denominatorSite;
        private static CallSite<Func<CallSite, object, object>> _toRSite;
        private static CallSite<Func<CallSite, object, object>> _toIntSite;
        private static CallSite<Func<CallSite, object, object>> _toStrSite;
        private static CallSite<Func<CallSite, object, object>> _toISite;
        private static CallSite<Func<CallSite, object, object, object>> _localToUtcSite;
        private static CallSite<Func<CallSite, object, object, object>> _utcToLocalSite;
        private static CallSite<Func<CallSite, object, object, object>> _compareSite;
        private static CallSite<Func<CallSite, object, object, object>> _greaterSite;
        private static CallSite<Func<CallSite, object, object, object>> _lessSite;

        private static object Invoke(RubyContext/*!*/ context, ref CallSite<Func<CallSite, object, object>> site, string/*!*/ name, object target) {
            var s = RubyUtils.GetCallSite(ref site, context, name, 0);
            return s.Target(s, target);
        }

        private static object Invoke(RubyContext/*!*/ context, ref CallSite<Func<CallSite, object, object, object>> site, string/*!*/ name, object target, object arg) {
            var s = RubyUtils.GetCallSite(ref site, context, name, 1);
            return s.Target(s, target, arg);
        }

        private static bool RespondTo(RubyContext/*!*/ context, object obj, string/*!*/ name) {
            return context.ResolveMethod(obj, name, VisibilityContext.AllVisible).Found;
        }

        internal static object MakeRational(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, ExactNum value) {

            return KernelOps.ToRational(storage, scope, scope.SelfObject,
                Protocols.Normalize(value.Numerator), Protocols.Normalize(value.Denominator));
        }

        #endregion

        #region Exact number conversion

        private static BigInteger ToBigInteger(object value) {
            if (value is int) {
                return new BigInteger((int)value);
            }
            if (value is BigInteger) {
                return (BigInteger)value;
            }
            if (value is long) {
                return new BigInteger((long)value);
            }
            if (value is double) {
                return new BigInteger((double)value);
            }
            throw RubyExceptions.CreateTypeError("can't convert to an exact number");
        }

        private static bool TryToExactSimple(object value, out ExactNum result) {
            if (value is int) {
                result = ExactNum.FromInteger((int)value);
                return true;
            }
            if (value is BigInteger) {
                result = ExactNum.FromInteger((BigInteger)value);
                return true;
            }
            if (value is long) {
                result = ExactNum.FromInteger((long)value);
                return true;
            }
            if (value is double) {
                double d = (double)value;
                if (Double.IsNaN(d) || Double.IsInfinity(d)) {
                    throw RubyExceptions.CreateRangeError("float {0} out of range of integer", d);
                }
                result = ExactNum.FromDouble(d);
                return true;
            }
            result = ExactNum.Zero;
            return false;
        }

        private static bool IsRational(RubyContext/*!*/ context, object value) {
            if (value == null || value is MutableString || value is RubyTime) {
                return false;
            }
            RubyClass cls = context.GetClassOf(value);
            return cls.Name == "Rational";
        }

        private static ExactNum FromRational(RubyContext/*!*/ context, object value) {
            object n = Invoke(context, ref _numeratorSite, "numerator", value);
            object d = Invoke(context, ref _denominatorSite, "denominator", value);
            return ExactNum.Make(ToBigInteger(n), ToBigInteger(d));
        }

        /// <summary>
        /// Ruby's num_exact: Integer, Rational and Float pass through; anything else must
        /// supply #to_r (plus #to_int, to reject String/Time which also define #to_r) or #to_int.
        /// </summary>
        internal static ExactNum ToExact(RubyContext/*!*/ context, object value) {
            ExactNum result;
            if (TryToExactSimple(value, out result)) {
                return result;
            }
            if (IsRational(context, value)) {
                return FromRational(context, value);
            }

            if (value != null && !(value is MutableString) && RespondTo(context, value, "to_r") && RespondTo(context, value, "to_int")) {
                object converted = Invoke(context, ref _toRSite, "to_r", value);
                if (TryToExactSimple(converted, out result)) {
                    return result;
                }
                if (IsRational(context, converted)) {
                    return FromRational(context, converted);
                }
            } else if (value != null && !(value is MutableString) && RespondTo(context, value, "to_int")) {
                object converted = Invoke(context, ref _toIntSite, "to_int", value);
                if (TryToExactSimple(converted, out result)) {
                    return result;
                }
            }

            throw RubyExceptions.CreateTypeError("can't convert {0} into an exact number", context.GetClassName(value));
        }

        /// <summary>
        /// Like ToExact, but also accepts a String, which Time.utc/local/new allow for
        /// broken-down components ("interprets all numerals as base 10").
        /// </summary>
        private static ExactNum ToExactComponent(RubyContext/*!*/ context, object value) {
            MutableString str = value as MutableString;
            if (str != null) {
                return ExactNum.FromInteger(ToBigInteger(MutableStringOps.ToInteger(str, 10)));
            }
            return ToExact(context, value);
        }

        private static int ToIntComponent(RubyContext/*!*/ context, object value) {
            if (value is int) {
                return (int)value;
            }
            if (value is MutableString) {
                return checked((int)MutableStringOps.ToInteger((MutableString)value, 10));
            }
            ExactNum exact = ToExact(context, value);
            return (int)exact.Floor();
        }

        #endregion

        #region UTC offset parsing

        private static readonly Regex/*!*/ _offsetPattern = new Regex(
            @"^(?<sign>[+-])(?<h>\d\d)(:?(?<m>\d\d)(:?(?<s>\d\d))?)?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses the utc_offset / :in argument. Returns false if the value isn't a String
        /// (the caller then treats it as a number or a timezone object).
        /// </summary>
        private static bool TryParseOffsetString(RubyContext/*!*/ context, object value, out ExactNum offset, out bool isUtc) {
            offset = ExactNum.Zero;
            isUtc = false;

            MutableString str = value as MutableString;
            if (str == null) {
                if (value == null || value is RubyTime || !RespondTo(context, value, "to_str")) {
                    return false;
                }
                str = Invoke(context, ref _toStrSite, "to_str", value) as MutableString;
                if (str == null) {
                    return false;
                }
            }

            if (!str.IsAscii() && !str.Encoding.IsAsciiIdentity) {
                throw RubyExceptions.CreateArgumentError("string contains null byte");
            }

            string s;
            try {
                s = str.ConvertToString();
            } catch (Exception) {
                throw RubyExceptions.CreateArgumentError("string contains null byte");
            }

            if (s == "UTC") {
                isUtc = true;
                return true;
            }

            if (s.Length == 1) {
                char c = s[0];
                if (c == 'Z') {
                    isUtc = true;
                    return true;
                }
                if (c >= 'A' && c <= 'I') {
                    offset = ExactNum.FromInteger((c - 'A' + 1) * 3600);
                    return true;
                }
                if (c >= 'K' && c <= 'M') {
                    offset = ExactNum.FromInteger((c - 'A') * 3600);
                    return true;
                }
                if (c >= 'N' && c <= 'Y') {
                    offset = ExactNum.FromInteger(-(c - 'N' + 1) * 3600);
                    return true;
                }
                throw InvalidUtcOffset(s);
            }

            Match m = _offsetPattern.Match(s);
            if (!m.Success) {
                throw InvalidUtcOffset(s);
            }

            int h = Int32.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            int min = m.Groups["m"].Success ? Int32.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            int sec = m.Groups["s"].Success ? Int32.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;

            if (h > 23) {
                throw RubyExceptions.CreateArgumentError("utc_offset out of range");
            }
            if (min > 59 || sec > 59) {
                throw InvalidUtcOffset(s);
            }

            long total = (long)h * 3600 + min * 60 + sec;
            if (m.Groups["sign"].Value == "-") {
                if (total == 0) {
                    isUtc = true;
                    return true;
                }
                total = -total;
            }
            offset = ExactNum.FromInteger(total);
            return true;
        }

        private static Exception InvalidUtcOffset(string value) {
            return RubyExceptions.CreateArgumentError(
                "\"+HH:MM\", \"-HH:MM\", \"UTC\" or \"A\"..\"I\",\"K\"..\"Z\" expected for utc_offset: {0}", value);
        }

        private static void CheckOffsetRange(ExactNum offset) {
            if (offset.CompareTo(ExactNum.FromInteger(86400)) >= 0 || offset.CompareTo(ExactNum.FromInteger(-86400)) <= 0) {
                throw RubyExceptions.CreateArgumentError("utc_offset out of range");
            }
        }

        /// <summary>
        /// Interprets a zone specification (nil, a numeric offset, an offset string, or a
        /// timezone object) and returns the resulting zone kind.
        /// </summary>
        private static RubyTimeZoneKind ResolveZone(RubyContext/*!*/ context, object zone, out ExactNum offset, out object zoneObject) {
            offset = ExactNum.Zero;
            zoneObject = null;

            if (zone == null || zone == Missing.Value) {
                return RubyTimeZoneKind.Local;
            }

            bool isUtc;
            if (TryParseOffsetString(context, zone, out offset, out isUtc)) {
                if (isUtc) {
                    offset = ExactNum.Zero;
                    return RubyTimeZoneKind.Utc;
                }
                CheckOffsetRange(offset);
                return RubyTimeZoneKind.FixedOffset;
            }

            if (RespondTo(context, zone, "local_to_utc")) {
                zoneObject = zone;
                return RubyTimeZoneKind.FixedOffset;
            }

            offset = ToExact(context, zone);
            CheckOffsetRange(offset);
            return RubyTimeZoneKind.FixedOffset;
        }

        #endregion

        #region Argument helpers

        private static IDictionary<object, object> ExtractOptions(ref object[]/*!*/ args) {
            if (args.Length == 0) {
                return null;
            }
            var hash = args[args.Length - 1] as IDictionary<object, object>;
            if (hash == null) {
                return null;
            }
            object[] rest = new object[args.Length - 1];
            Array.Copy(args, rest, rest.Length);
            args = rest;
            return hash;
        }

        private static object GetOption(IDictionary<object, object> options, string/*!*/ name, out bool found) {
            found = false;
            if (options == null) {
                return null;
            }
            foreach (var entry in options) {
                var key = entry.Key as RubySymbol;
                if (key != null && key.ToString() == name) {
                    found = true;
                    return entry.Value;
                }
            }
            return null;
        }

        private static object GetInOption(IDictionary<object, object> options) {
            bool found;
            return GetOption(options, "in", out found);
        }

        #endregion

        #region Construction

        private static RubyTime/*!*/ NowInternal(RubyContext/*!*/ context, object zone) {
            DateTime utcNow = DateTime.UtcNow;
            long ticks = utcNow.Ticks - RubyTime.Epoch.Ticks;
            long seconds = RubyTime.FloorDiv(ticks, TimeSpan.TicksPerSecond);
            long rest = ticks - seconds * TimeSpan.TicksPerSecond;
            ExactNum subsec = (rest == 0) ? ExactNum.Zero : ExactNum.Make(rest, RubyTime.TicksPerSecondBig);

            ExactNum offset;
            object zoneObject;
            RubyTimeZoneKind kind = ResolveZone(context, zone, out offset, out zoneObject);
            var result = new RubyTime(seconds, subsec, kind, offset);
            result.ZoneObject = zoneObject;
            return result;
        }

        [RubyConstructor]
        public static RubyTime/*!*/ Create(RubyContext/*!*/ context, RubyClass/*!*/ self) {
            return NowInternal(context, null);
        }

        [RubyConstructor]
        public static RubyTime/*!*/ Create(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]params object[]/*!*/ args) {
            return CreateFromComponents(context, args);
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyTime/*!*/ Reinitialize(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            self.CopyFrom(NowInternal(context, null));
            return self;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyTime/*!*/ Reinitialize(RubyContext/*!*/ context, RubyTime/*!*/ self, [NotNull]params object[]/*!*/ args) {
            self.CopyFrom(CreateFromComponents(context, args));
            return self;
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static RubyTime/*!*/ InitializeCopy(RubyTime/*!*/ self, [NotNull]RubyTime/*!*/ other) {
            self.CopyFrom(other);
            return self;
        }

        /// <summary>Time.new(year, month, day, hour, min, sec, utc_offset) / Time.new(in: zone)</summary>
        private static RubyTime/*!*/ CreateFromComponents(RubyContext/*!*/ context, object[]/*!*/ args) {
            var options = ExtractOptions(ref args);
            object inZone = GetInOption(options);

            if (args.Length == 0) {
                return NowInternal(context, inZone);
            }

            if (args.Length > 7) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..7)", args.Length);
            }

            if (args[0] == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            }

            object zone = (args.Length == 7) ? args[6] : inZone;

            ExactNum offset;
            object zoneObject;
            RubyTimeZoneKind kind = ResolveZone(context, zone, out offset, out zoneObject);

            int year = ToIntComponent(context, args[0]);
            int month = (args.Length > 1) ? GetMonth(context, args[1]) : 1;
            int day = (args.Length > 2 && args[2] != null) ? ToIntComponent(context, args[2]) : 1;
            int hour = (args.Length > 3 && args[3] != null) ? ToIntComponent(context, args[3]) : 0;
            int minute = (args.Length > 4 && args[4] != null) ? ToIntComponent(context, args[4]) : 0;
            ExactNum second = (args.Length > 5 && args[5] != null) ? ToExactComponent(context, args[5]) : ExactNum.Zero;

            return AssembleTime(context, year, month, day, hour, minute, second, kind, offset, zoneObject, true);
        }

        private static int GetMonth(RubyContext/*!*/ context, object value) {
            if (value == null) {
                return 1;
            }

            MutableString str = value as MutableString;
            if (str == null && !(value is int) && value != null && RespondTo(context, value, "to_str")) {
                str = Invoke(context, ref _toStrSite, "to_str", value) as MutableString;
            }

            if (str != null) {
                string s = str.ConvertToString();
                if (s.Length == 3) {
                    string lower = s.ToLowerInvariant();
                    for (int i = 0; i < _Months.Length; i++) {
                        if (_Months[i] == lower) {
                            return i + 1;
                        }
                    }
                }
                return checked((int)MutableStringOps.ToInteger(str, 10));
            }

            return ToIntComponent(context, value);
        }

        private static string[]/*!*/ _Months = new string[12] {
            "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"
        };

        /// <summary>
        /// Builds a Time from broken-down components interpreted in the given zone.
        /// </summary>
        private static RubyTime/*!*/ AssembleTime(RubyContext/*!*/ context, int year, int month, int day, int hour, int minute,
            ExactNum second, RubyTimeZoneKind kind, ExactNum offset, object zoneObject, bool allowOverflow) {

            // MRI uses a component-specific message for values just outside the valid band
            // and the generic "argument out of range" for anything further out.
            if (month < 1 || month > 12) {
                throw RubyExceptions.CreateArgumentError((month >= 13 && month <= 15) ? "mon out of range" : "argument out of range");
            }
            if (hour < 0 || hour > 24) {
                throw RubyExceptions.CreateArgumentError((hour >= 25 && hour <= 27) ? "hour out of range" : "argument out of range");
            }
            if (minute < 0 || minute > 59) {
                throw RubyExceptions.CreateArgumentError((minute >= 60 && minute <= 63) ? "min out of range" : "argument out of range");
            }
            if (second.Sign < 0) {
                throw RubyExceptions.CreateArgumentError("argument out of range");
            }
            if (second.CompareTo(ExactNum.FromInteger(61)) >= 0) {
                throw RubyExceptions.CreateArgumentError(
                    second.CompareTo(ExactNum.FromInteger(64)) < 0 ? "sec out of range" : "argument out of range");
            }
            // A day beyond the end of the month rolls into the next one (Feb 31 == Mar 3),
            // but a day outside 1..31 is an error.
            if (day < 1 || day > 31) {
                throw RubyExceptions.CreateArgumentError("argument out of range");
            }

            long wallSeconds;
            try {
                wallSeconds = checked(RubyTime.DaysFromCivil(year, month, 1) * 86400
                    + (long)(day - 1) * 86400 + (long)hour * 3600 + (long)minute * 60);
            } catch (OverflowException) {
                throw RubyExceptions.CreateArgumentError("argument out of range");
            }

            BigInteger wholeSecond = second.Floor();
            ExactNum fraction = second - ExactNum.FromInteger(wholeSecond);
            wallSeconds += (long)wholeSecond;

            long utcSeconds;
            switch (kind) {
                case RubyTimeZoneKind.Utc:
                    utcSeconds = wallSeconds;
                    break;

                case RubyTimeZoneKind.FixedOffset:
                    if (zoneObject != null) {
                        return ApplyTimezoneObject(context, wallSeconds, fraction, zoneObject);
                    }
                    utcSeconds = wallSeconds - (long)offset.Floor();
                    break;

                default: {
                        DateTime wall = RubyTime.ToDateTimeClamped(wallSeconds, DateTimeKind.Unspecified);
                        long zoneOffset = (long)RubyTime._CurrentTimeZone.GetUtcOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified)).TotalSeconds;
                        utcSeconds = wallSeconds - zoneOffset;
                        break;
                    }
            }

            var result = new RubyTime(utcSeconds, fraction, kind, offset);
            result.ZoneObject = zoneObject;
            return result;
        }

        private static RubyTime/*!*/ ApplyTimezoneObject(RubyContext/*!*/ context, long wallSeconds, ExactNum fraction, object/*!*/ zoneObject) {
            // Hand the zone object a UTC "Time-like" value and let it map it to a real instant.
            var asUtc = new RubyTime(wallSeconds, ExactNum.Zero, RubyTimeZoneKind.Utc, ExactNum.Zero);
            object mapped = Invoke(context, ref _localToUtcSite, "local_to_utc", zoneObject, asUtc);

            long utcSeconds;
            RubyTime mappedTime = mapped as RubyTime;
            if (mappedTime != null) {
                utcSeconds = mappedTime.Seconds - mappedTime.UtcOffsetSeconds;
            } else if (mapped != null && RespondTo(context, mapped, "to_i")) {
                utcSeconds = (long)ToExact(context, Invoke(context, ref _toISite, "to_i", mapped)).Floor();
            } else {
                throw RubyExceptions.CreateTypeError("can't convert {0} into an exact number", context.GetClassName(mapped));
            }

            long offsetSeconds = wallSeconds - utcSeconds;
            if (offsetSeconds <= -86400 || offsetSeconds >= 86400) {
                throw RubyExceptions.CreateArgumentError("utc_offset out of range");
            }

            var result = new RubyTime(utcSeconds, fraction, RubyTimeZoneKind.FixedOffset, ExactNum.FromInteger(offsetSeconds));
            result.ZoneObject = zoneObject;
            return result;
        }

        #region at

        [RubyMethod("at", RubyMethodAttributes.PublicSingleton)]
        public static RubyTime/*!*/ At(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]params object[]/*!*/ args) {
            var options = ExtractOptions(ref args);
            object inZone = GetInOption(options);

            if (args.Length < 1 || args.Length > 3) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..3)", args.Length);
            }

            ExactNum offset;
            object zoneObject;
            RubyTimeZoneKind kind;
            bool zoneGiven = options != null && inZone != null;
            if (zoneGiven) {
                kind = ResolveZone(context, inZone, out offset, out zoneObject);
            } else {
                kind = RubyTimeZoneKind.Local;
                offset = ExactNum.Zero;
                zoneObject = null;
            }

            RubyTime other = args[0] as RubyTime;
            if (other != null && args.Length == 1) {
                var copy = other.WithZone(zoneGiven ? kind : other.ZoneKind,
                    zoneGiven ? offset : other.UtcOffsetExact, zoneGiven ? zoneObject : other.ZoneObject);
                if (!zoneGiven) {
                    copy = new RubyTime(other.Seconds, other.Subsec, other.ZoneKind,
                        other.ZoneKind == RubyTimeZoneKind.FixedOffset ? other.UtcOffsetExact : ExactNum.Zero);
                    copy.ZoneObject = other.ZoneObject;
                }
                return copy;
            }

            ExactNum seconds = (other != null) ? other.ToExactSeconds() : ToExact(context, args[0]);

            if (args.Length >= 2) {
                ExactNum sub = ToExact(context, args[1]);
                BigInteger unitDivisor = RubyTime.MicrosecondsPerSecond;

                if (args.Length == 3) {
                    var unit = args[2] as RubySymbol;
                    string name = (unit != null) ? unit.ToString() : (args[2] as MutableString)?.ConvertToString();
                    switch (name) {
                        case "millisecond": unitDivisor = new BigInteger(1000); break;
                        case "usec":
                        case "microsecond": unitDivisor = RubyTime.MicrosecondsPerSecond; break;
                        case "nsec":
                        case "nanosecond": unitDivisor = RubyTime.NanosecondsPerSecond; break;
                        default:
                            throw RubyExceptions.CreateArgumentError("unexpected unit: {0}", context.Inspect(args[2]).ToString());
                    }
                }

                seconds = seconds + sub / ExactNum.FromInteger(unitDivisor);
            }

            var result = RubyTime.FromExactSeconds(seconds, kind, offset);
            result.ZoneObject = zoneObject;
            return result;
        }

        #endregion

        #region now

        [RubyMethod("now", RubyMethodAttributes.PublicSingleton)]
        public static RubyTime/*!*/ Now(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]params object[]/*!*/ args) {
            var options = ExtractOptions(ref args);
            if (args.Length != 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0)", args.Length);
            }
            return NowInternal(context, GetInOption(options));
        }

        #endregion

        #region local, mktime, utc, gm

        [RubyMethod("local", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("mktime", RubyMethodAttributes.PublicSingleton)]
        public static RubyTime/*!*/ CreateLocalTime(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]params object[]/*!*/ components) {
            return CreateBrokenDownTime(context, components, RubyTimeZoneKind.Local);
        }

        [RubyMethod("utc", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("gm", RubyMethodAttributes.PublicSingleton)]
        public static RubyTime/*!*/ CreateGmtTime(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]params object[]/*!*/ components) {
            return CreateBrokenDownTime(context, components, RubyTimeZoneKind.Utc);
        }

        private static RubyTime/*!*/ CreateBrokenDownTime(RubyContext/*!*/ context, object[]/*!*/ components, RubyTimeZoneKind kind) {
            if (components.Length == 10) {
                // 10 arguments in the order output by Time#to_a are permitted.
                // The last 4 are ignored. The first 6 need to be used in the reverse order.
                object[] newComponents = new object[6];
                Array.Copy(components, newComponents, 6);
                Array.Reverse(newComponents);
                components = newComponents;
            } else if (components.Length > 8 || components.Length == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..8)", components.Length);
            }

            int year = GetYear(context, components, 0);
            int month = (components.Length > 1) ? GetMonth(context, components[1]) : 1;
            int day = GetComponent(context, components, 2, 1);
            int hour = GetComponent(context, components, 3, 0);
            int minute = GetComponent(context, components, 4, 0);

            ExactNum second = (components.Length > 5 && components[5] != null) ? ToExactComponent(context, components[5]) : ExactNum.Zero;
            if (components.Length > 6 && components[6] != null) {
                second = second + ToExactComponent(context, components[6]) / ExactNum.FromInteger(RubyTime.MicrosecondsPerSecond);
            }

            return AssembleTime(context, year, month, day, hour, minute, second, kind, ExactNum.Zero, null, false);
        }

        private static int GetComponent(RubyContext/*!*/ context, object[]/*!*/ components, int index, int defValue) {
            if (index >= components.Length || components[index] == null) {
                return defValue;
            }
            return ToIntComponent(context, components[index]);
        }

        private static int GetYear(RubyContext/*!*/ context, object[]/*!*/ components, int index) {
            if (index >= components.Length || components[index] == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            }
            return ToIntComponent(context, components[index]);
        }

        #endregion

        #endregion

        #region _dump, _load

        [RubyMethod("_dump", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ Dump(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]int depth) {
            DateTime value = self.UtcDateTime;
            if (value.Year < 1900 || value.Year > 2038) {
                throw RubyExceptions.CreateTypeError("unable to marshal time");
            }

            // Little Endian
            //            32            |                32                  |
            // minute:6|second:6|usec:20|1|utc:1|year:16|month:4|day:5|hour:5|
            uint dword1 = self.IsUtc ? 0xC0000000 : 0x80000000;
            dword1 |= (unchecked((uint)(value.Year - 1900)) << 14);
            dword1 |= (unchecked((uint)(value.Month - 1)) << 10);
            dword1 |= ((uint)value.Day << 5);
            dword1 |= ((uint)value.Hour);

            uint dword2 = 0;
            dword2 |= ((uint)value.Minute << 26);
            dword2 |= ((uint)value.Second << 20);
            dword2 |= ((uint)self.Microseconds);

            MemoryStream buf = new MemoryStream(8);
            RubyEncoder.Write(buf, dword1, !BitConverter.IsLittleEndian);
            RubyEncoder.Write(buf, dword2, !BitConverter.IsLittleEndian);
            return MutableString.CreateBinary(buf.ToArray());
        }

        private static uint GetUint(byte[] data, int start) {
            Assert.NotNull(data);
            return (((((((uint)data[start + 3] << 8) + (uint)data[start + 2]) << 8) + (uint)data[start + 1]) << 8) + (uint)data[start + 0]);
        }

        [RubyMethod("_load", RubyMethodAttributes.PrivateSingleton)]
        public static RubyTime/*!*/ Load(RubyContext/*!*/ context, RubyClass/*!*/ self, [NotNull]MutableString/*!*/ time) {
            byte[] data = time.ConvertToBytes();
            if (data.Length != 8) {
                throw RubyExceptions.CreateTypeError("marshaled time format differ");
            }

            uint dword1 = GetUint(data, 0);
            uint dword2 = GetUint(data, 4);

            if ((data[3] & 0x80) == 0) {
                int secondsSinceEpoch = (int)dword1;
                uint microseconds = dword2;
                return new RubyTime(secondsSinceEpoch,
                    (microseconds == 0) ? ExactNum.Zero : ExactNum.Make(microseconds, RubyTime.MicrosecondsPerSecond),
                    RubyTimeZoneKind.Local, ExactNum.Zero);
            } else {
                bool isUtc = (data[3] & 0x40) != 0;
                int year = 1900 + (int)((dword1 >> 14) & 0xffff);
                int month = 1 + (int)((dword1 >> 10) & 0x0f);
                int day = (int)((dword1 >> 5) & 0x01f);
                int hour = (int)(dword1 & 0x01f);

                int minute = (int)((dword2 >> 26) & 0x3f);
                int second = (int)((dword2 >> 20) & 0x3f);
                int usec = (int)(dword2 & 0xfffff);

                try {
                    return AssembleTime(context, year, month, day, hour, minute,
                        ExactNum.FromInteger(second) + ExactNum.Make(usec, RubyTime.MicrosecondsPerSecond),
                        isUtc ? RubyTimeZoneKind.Utc : RubyTimeZoneKind.Local, ExactNum.Zero, null, false);
                } catch (Exception e) when (!(e is RubyTime)) {
                    throw RubyExceptions.CreateTypeError("marshaled time format differ");
                }
            }
        }

        #endregion

        #region succ, +, -, <=>, ==, eql?, hash

        [RubyMethod("succ")]
        public static RubyTime/*!*/ SuccessiveSecond(RubyTime/*!*/ self) {
            return Shift(self, ExactNum.One);
        }

        private static RubyTime/*!*/ Shift(RubyTime/*!*/ self, ExactNum delta) {
            ExactNum result = self.ToExactSeconds() + delta;
            var time = RubyTime.FromExactSeconds(result, self.ZoneKind,
                self.ZoneKind == RubyTimeZoneKind.FixedOffset ? self.UtcOffsetExact : ExactNum.Zero);
            time.ZoneObject = self.ZoneObject;
            return time;
        }

        [RubyMethod("+")]
        public static RubyTime/*!*/ AddSeconds(RubyContext/*!*/ context, RubyTime/*!*/ self, object seconds) {
            if (seconds is RubyTime) {
                throw RubyExceptions.CreateTypeError("time + time?");
            }
            try {
                return Shift(self, ToExact(context, seconds));
            } catch (OverflowException) {
                throw RubyExceptions.CreateRangeError("time + {0} out of Time range", context.Inspect(seconds).ToString());
            }
        }

        [RubyMethod("-")]
        public static object Subtract(RubyContext/*!*/ context, RubyTime/*!*/ self, object other) {
            RubyTime time = other as RubyTime;
            if (time != null) {
                return (self.ToExactSeconds() - time.ToExactSeconds()).ToDouble();
            }
            if (other is DateTime) {
                return (self.UtcDateTime - RubyTime.ToUniversalTime((DateTime)other)).TotalSeconds;
            }
            try {
                return Shift(self, -ToExact(context, other));
            } catch (OverflowException) {
                throw RubyExceptions.CreateRangeError("time - {0} out of Time range", context.Inspect(other).ToString());
            }
        }

        [RubyMethod("<=>")]
        public static object CompareTo(RubyContext/*!*/ context, RubyTime/*!*/ self, object other) {
            RubyTime time = other as RubyTime;
            if (time != null) {
                return ScriptingRuntimeHelpers.Int32ToObject(self.CompareTo(time));
            }

            // MRI asks the other object to compare itself against us and inverts the answer.
            object result = Invoke(context, ref _compareSite, "<=>", other, self);
            if (result == null) {
                return null;
            }

            object greater = Invoke(context, ref _greaterSite, ">", result, ScriptingRuntimeHelpers.Int32ToObject(0));
            if (RubyOps.IsTrue(greater)) {
                return ScriptingRuntimeHelpers.Int32ToObject(-1);
            }

            object less = Invoke(context, ref _lessSite, "<", result, ScriptingRuntimeHelpers.Int32ToObject(0));
            if (RubyOps.IsTrue(less)) {
                return ScriptingRuntimeHelpers.Int32ToObject(1);
            }

            return ScriptingRuntimeHelpers.Int32ToObject(0);
        }

        [RubyMethod("==")]
        public static bool Equal(RubyTime/*!*/ self, object other) {
            RubyTime time = other as RubyTime;
            return time != null && self.CompareTo(time) == 0;
        }

        [RubyMethod("eql?")]
        public static bool Eql(RubyTime/*!*/ self, object other) {
            RubyTime time = other as RubyTime;
            return time != null && self.Equals(time);
        }

        [RubyMethod("hash")]
        public static int GetHash(RubyTime/*!*/ self) {
            return self.GetHashCode();
        }

        #endregion

        #region utc, utc?, dst?, utc_offset, getlocal, zone

        [RubyMethod("gmtime")]
        [RubyMethod("utc")]
        public static RubyTime/*!*/ SwitchToUtc(RubyTime/*!*/ self) {
            self.CopyFrom(self.WithZone(RubyTimeZoneKind.Utc, ExactNum.Zero, null));
            return self;
        }

        [RubyMethod("localtime")]
        public static RubyTime/*!*/ SwitchToLocalTime(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object zone) {
            self.CopyFrom(GetLocal(context, self, zone));
            return self;
        }

        [RubyMethod("getlocal")]
        public static RubyTime/*!*/ GetLocal(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object zone) {
            if (zone == Missing.Value || zone == null) {
                return self.WithZone(RubyTimeZoneKind.Local, ExactNum.Zero, null);
            }

            ExactNum offset;
            object zoneObject;
            RubyTimeZoneKind kind = ResolveZone(context, zone, out offset, out zoneObject);
            return self.WithZone(kind, offset, zoneObject);
        }

        [RubyMethod("gmt?")]
        [RubyMethod("utc?")]
        public static bool IsUtc(RubyTime/*!*/ self) {
            return self.IsUtc;
        }

        [RubyMethod("dst?")]
        [RubyMethod("isdst")]
        public static bool IsDst(RubyTime/*!*/ self) {
            return self.IsDaylightSavingTime;
        }

        [RubyMethod("gmtoff")]
        [RubyMethod("utc_offset")]
        [RubyMethod("gmt_offset")]
        public static object Offset(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self) {

            ExactNum offset = self.UtcOffsetExact;
            if (offset.IsInteger) {
                return Protocols.Normalize(offset.Numerator);
            }
            return MakeRational(toRational, scope, offset);
        }

        [RubyMethod("getgm")]
        [RubyMethod("getutc")]
        public static RubyTime/*!*/ GetUTC(RubyTime/*!*/ self) {
            return self.WithZone(RubyTimeZoneKind.Utc, ExactNum.Zero, null);
        }

        [RubyMethod("zone")]
        public static object GetZone(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            if (self.ZoneObject != null) {
                return self.ZoneObject;
            }
            string name = self.GetZoneName();
            if (name == null) {
                return null;
            }
            return name.IsAscii()
                ? MutableString.CreateAscii(name)
                : MutableString.Create(name, context.GetPathEncoding());
        }

        #endregion

        #region hour, min, sec, usec, nsec, subsec, year, mon, day, yday, wday

        [RubyMethod("hour")]
        public static int Hour(RubyTime/*!*/ self) {
            return self.GetFields().Hour;
        }

        [RubyMethod("min")]
        public static int Minute(RubyTime/*!*/ self) {
            return self.GetFields().Minute;
        }

        [RubyMethod("sec")]
        public static int Second(RubyTime/*!*/ self) {
            return self.GetFields().Second;
        }

        [RubyMethod("tv_usec")]
        [RubyMethod("usec")]
        public static int GetMicroSeconds(RubyTime/*!*/ self) {
            return self.Microseconds;
        }

        [RubyMethod("tv_nsec")]
        [RubyMethod("nsec")]
        public static int GetNanoSeconds(RubyTime/*!*/ self) {
            return self.Nanoseconds;
        }

        [RubyMethod("subsec")]
        public static object GetSubsec(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self) {

            if (self.Subsec.IsZero) {
                return ScriptingRuntimeHelpers.Int32ToObject(0);
            }
            return MakeRational(toRational, scope, self.Subsec);
        }

        [RubyMethod("year")]
        public static int Year(RubyTime/*!*/ self) {
            return self.GetFields().Year;
        }

        [RubyMethod("mon")]
        [RubyMethod("month")]
        public static int Month(RubyTime/*!*/ self) {
            return self.GetFields().Month;
        }

        [RubyMethod("mday")]
        [RubyMethod("day")]
        public static int Day(RubyTime/*!*/ self) {
            return self.GetFields().Day;
        }

        [RubyMethod("yday")]
        public static int DayOfYear(RubyTime/*!*/ self) {
            return self.GetFields().DayOfYear;
        }

        [RubyMethod("wday")]
        public static int DayOfWeek(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek;
        }

        [RubyMethod("sunday?")]
        public static bool IsSunday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 0;
        }

        [RubyMethod("monday?")]
        public static bool IsMonday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 1;
        }

        [RubyMethod("tuesday?")]
        public static bool IsTuesday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 2;
        }

        [RubyMethod("wednesday?")]
        public static bool IsWednesday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 3;
        }

        [RubyMethod("thursday?")]
        public static bool IsThursday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 4;
        }

        [RubyMethod("friday?")]
        public static bool IsFriday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 5;
        }

        [RubyMethod("saturday?")]
        public static bool IsSaturday(RubyTime/*!*/ self) {
            return self.GetFields().DayOfWeek == 6;
        }

        #endregion

        #region round, floor, ceil

        [RubyMethod("round")]
        public static RubyTime/*!*/ Round(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object ndigits) {
            return RoundTo(context, self, ndigits, 0);
        }

        [RubyMethod("floor")]
        public static RubyTime/*!*/ Floor(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object ndigits) {
            return RoundTo(context, self, ndigits, -1);
        }

        [RubyMethod("ceil")]
        public static RubyTime/*!*/ Ceil(RubyContext/*!*/ context, RubyTime/*!*/ self, [Optional]object ndigits) {
            return RoundTo(context, self, ndigits, 1);
        }

        private static RubyTime/*!*/ RoundTo(RubyContext/*!*/ context, RubyTime/*!*/ self, object ndigits, int mode) {
            int digits = (ndigits == Missing.Value || ndigits == null) ? 0 : ToIntComponent(context, ndigits);
            if (digits < 0) {
                digits = 0;
            }

            ExactNum scale = ExactNum.FromInteger(BigInteger.Pow(new BigInteger(10), digits));
            ExactNum scaled = self.ToExactSeconds() * scale;

            BigInteger rounded;
            if (mode < 0) {
                rounded = scaled.Floor();
            } else if (mode > 0) {
                rounded = scaled.Ceiling();
            } else {
                rounded = scaled.Round();
            }

            ExactNum result = ExactNum.Make(rounded, scale.Numerator);
            var time = RubyTime.FromExactSeconds(result, self.ZoneKind,
                self.ZoneKind == RubyTimeZoneKind.FixedOffset ? self.UtcOffsetExact : ExactNum.Zero);
            time.ZoneObject = self.ZoneObject;
            return time;
        }

        #endregion

        #region to_f, to_i, to_r, to_a, deconstruct_keys

        [RubyMethod("to_f")]
        public static double ToFloatSeconds(RubyTime/*!*/ self) {
            return self.ToExactSeconds().ToDouble();
        }

        [RubyMethod("tv_sec")]
        [RubyMethod("to_i")]
        public static object/*!*/ ToSeconds(RubyTime/*!*/ self) {
            return Protocols.Normalize(new BigInteger(self.Seconds));
        }

        [RubyMethod("to_r")]
        public static object ToRational(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self) {

            return MakeRational(toRational, scope, self.ToExactSeconds());
        }

        [RubyMethod("asctime")]
        [RubyMethod("ctime")]
        public static MutableString/*!*/ CTime(RubyTime/*!*/ self) {
            RubyTime.Fields t = self.GetFields();
            return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                "{0} {1} {2,2} {3:D2}:{4:D2}:{5:D2} {6:D4}",
                Strftime.DayNamesAbbreviated[t.DayOfWeek], Strftime.MonthNamesAbbreviated[t.Month - 1],
                t.Day, t.Hour, t.Minute, t.Second, t.Year
            ));
        }

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToString(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            RubyTime.Fields t = self.GetFields();
            return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                "{0:D4}-{1:D2}-{2:D2} {3:D2}:{4:D2}:{5:D2} {6}",
                t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second,
                self.IsUtc ? "UTC" : self.FormatUtcOffset()
            ));
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            RubyTime.Fields t = self.GetFields();
            string subsec = FormatSubsecForInspect(self.Subsec);
            return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                "{0:D4}-{1:D2}-{2:D2} {3:D2}:{4:D2}:{5:D2}{6} {7}",
                t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, subsec,
                self.IsUtc ? "UTC" : self.FormatUtcOffset()
            ));
        }

        private static string/*!*/ FormatSubsecForInspect(ExactNum subsec) {
            if (subsec.IsZero) {
                return "";
            }

            // Exactly representable in nanoseconds? Then print the decimal digits.
            ExactNum scaled = subsec * ExactNum.FromInteger(RubyTime.NanosecondsPerSecond);
            if (scaled.IsInteger) {
                string digits = ((BigInteger)scaled.Numerator).ToString(CultureInfo.InvariantCulture).PadLeft(9, '0');
                digits = digits.TrimEnd('0');
                return "." + digits;
            }
            return " " + subsec.Numerator.ToString(CultureInfo.InvariantCulture) + "/" + subsec.Denominator.ToString(CultureInfo.InvariantCulture);
        }

        [RubyMethod("to_a")]
        public static RubyArray/*!*/ ToArray(RubyContext/*!*/ context, RubyTime/*!*/ self) {
            RubyTime.Fields t = self.GetFields();
            RubyArray result = new RubyArray();
            result.Add(t.Second);
            result.Add(t.Minute);
            result.Add(t.Hour);
            result.Add(t.Day);
            result.Add(t.Month);
            result.Add(t.Year);
            result.Add(t.DayOfWeek);
            result.Add(t.DayOfYear);
            result.Add(self.IsDaylightSavingTime);
            result.Add(GetZone(context, self));
            return result;
        }

        [RubyMethod("deconstruct_keys")]
        public static Hash/*!*/ DeconstructKeys(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self, object keys) {

            RubyContext context = scope.RubyContext;
            var result = new Hash(context);

            IList list = null;
            if (keys != null) {
                list = keys as IList;
                if (list == null) {
                    throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Array or nil)",
                        context.GetClassName(keys));
                }
            }

            if (list == null) {
                foreach (string name in _deconstructKeys) {
                    result[context.CreateAsciiSymbol(name)] = GetDeconstructValue(toRational, scope, self, name);
                }
                return result;
            }

            foreach (object key in list) {
                var symbol = key as RubySymbol;
                if (symbol == null) {
                    continue;
                }
                string name = symbol.ToString();
                if (Array.IndexOf(_deconstructKeys, name) < 0) {
                    continue;
                }
                result[symbol] = GetDeconstructValue(toRational, scope, self, name);
            }
            return result;
        }

        private static readonly string[]/*!*/ _deconstructKeys = new[] {
            "year", "month", "day", "yday", "wday", "hour", "min", "sec", "subsec", "dst", "zone"
        };

        private static object GetDeconstructValue(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational,
            RubyScope/*!*/ scope, RubyTime/*!*/ self, string/*!*/ name) {

            switch (name) {
                case "year": return ScriptingRuntimeHelpers.Int32ToObject(Year(self));
                case "month": return ScriptingRuntimeHelpers.Int32ToObject(Month(self));
                case "day": return ScriptingRuntimeHelpers.Int32ToObject(Day(self));
                case "yday": return ScriptingRuntimeHelpers.Int32ToObject(DayOfYear(self));
                case "wday": return ScriptingRuntimeHelpers.Int32ToObject(DayOfWeek(self));
                case "hour": return ScriptingRuntimeHelpers.Int32ToObject(Hour(self));
                case "min": return ScriptingRuntimeHelpers.Int32ToObject(Minute(self));
                case "sec": return ScriptingRuntimeHelpers.Int32ToObject(Second(self));
                case "subsec": return GetSubsec(toRational, scope, self);
                case "dst": return ScriptingRuntimeHelpers.BooleanToObject(self.IsDaylightSavingTime);
                default: return GetZone(scope.RubyContext, self);
            }
        }

        #endregion

        #region strftime

        [RubyMethod("strftime")]
        public static MutableString/*!*/ FormatTime(RubyContext/*!*/ context, RubyTime/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ format) {

            MutableString result = MutableString.CreateMutable(format.Encoding);
            Strftime.Format(result, self, format.ConvertToString(), 0);
            return result;
        }

        #endregion
    }
}
