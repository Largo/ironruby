/* ****************************************************************************
 *
 * Ruby's Time#strftime. Implemented directly rather than by mapping onto .NET
 * DateTime format strings: Ruby's directive set (%C %F %G %j %k %l %L %N %P %s
 * %u %V ...), its flags (- _ 0 ^ #), explicit field widths and the %z colon
 * variants have no .NET equivalent, and .NET's own escaping rules get in the way.
 *
 * ***************************************************************************/

using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    internal static class Strftime {
        internal static readonly string[]/*!*/ DayNames = new[] {
            "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"
        };

        internal static readonly string[]/*!*/ DayNamesAbbreviated = new[] {
            "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"
        };

        internal static readonly string[]/*!*/ MonthNames = new[] {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December"
        };

        internal static readonly string[]/*!*/ MonthNamesAbbreviated = new[] {
            "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
        };

        [Flags]
        private enum Modifiers {
            None = 0,
            NoPadding = 1,
            SpacePadding = 2,
            ZeroPadding = 4,
            Upcase = 8,
            Swapcase = 16,
            Colons1 = 32,
            Colons2 = 64,
            Colons3 = 128,
        }

        private const int MaxRecursionDepth = 8;

        internal static void Format(MutableString/*!*/ result, RubyTime/*!*/ time, string/*!*/ format, int depth) {
            if (depth > MaxRecursionDepth) {
                return;
            }

            int i = 0;
            while (i < format.Length) {
                char c = format[i];
                if (c != '%') {
                    result.Append(c);
                    i++;
                    continue;
                }

                int start = i;
                i++;
                if (i >= format.Length) {
                    result.Append('%');
                    break;
                }

                Modifiers modifiers = Modifiers.None;
                bool reading = true;
                while (i < format.Length && reading) {
                    switch (format[i]) {
                        case '-': modifiers |= Modifiers.NoPadding; i++; break;
                        case '_': modifiers |= Modifiers.SpacePadding; i++; break;
                        case '0': modifiers |= Modifiers.ZeroPadding; i++; break;
                        case '^': modifiers |= Modifiers.Upcase; i++; break;
                        case '#': modifiers |= Modifiers.Swapcase; i++; break;
                        case ':':
                            if ((modifiers & Modifiers.Colons3) != 0) {
                                reading = false;
                            } else if ((modifiers & Modifiers.Colons2) != 0) {
                                modifiers = (modifiers & ~Modifiers.Colons2) | Modifiers.Colons3; i++;
                            } else if ((modifiers & Modifiers.Colons1) != 0) {
                                modifiers = (modifiers & ~Modifiers.Colons1) | Modifiers.Colons2; i++;
                            } else {
                                modifiers |= Modifiers.Colons1; i++;
                            }
                            break;
                        default:
                            reading = false;
                            break;
                    }
                }

                int width = -1;
                int widthStart = i;
                while (i < format.Length && format[i] >= '0' && format[i] <= '9') {
                    i++;
                }
                if (i > widthStart) {
                    if (!Int32.TryParse(format.Substring(widthStart, i - widthStart), NumberStyles.None, CultureInfo.InvariantCulture, out width)) {
                        width = -1;
                    }
                }

                if (i >= format.Length) {
                    result.Append(format.Substring(start));
                    return;
                }

                char directive = format[i];
                i++;

                string piece = FormatDirective(time, directive, modifiers, width, depth);
                if (piece == null) {
                    // Unknown directive: Ruby copies the whole thing through verbatim.
                    result.Append(format.Substring(start, i - start));
                } else {
                    result.Append(piece);
                }
            }
        }

        private static string FormatDirective(RubyTime/*!*/ time, char directive, Modifiers modifiers, int width, int depth) {
            RubyTime.Fields t = time.GetFields();

            switch (directive) {
                case '%': return "%";
                case 'n': return "\n";
                case 't': return "\t";

                case 'a': return Text(DayNamesAbbreviated[t.DayOfWeek], modifiers, width);
                case 'A': return Text(DayNames[t.DayOfWeek], modifiers, width);
                case 'h':
                case 'b': return Text(MonthNamesAbbreviated[t.Month - 1], modifiers, width);
                case 'B': return Text(MonthNames[t.Month - 1], modifiers, width);

                case 'p': return Text(t.Hour < 12 ? "AM" : "PM", modifiers, width);
                case 'P': return Text(t.Hour < 12 ? "am" : "pm", modifiers | Modifiers.Swapcase, width);

                case 'C': return Number(FloorDiv(t.Year, 100), modifiers, width, 2, '0');
                case 'd': return Number(t.Day, modifiers, width, 2, '0');
                case 'e': return Number(t.Day, modifiers, width, 2, ' ');
                case 'H': return Number(t.Hour, modifiers, width, 2, '0');
                case 'k': return Number(t.Hour, modifiers, width, 2, ' ');
                case 'I': return Number(Hour12(t.Hour), modifiers, width, 2, '0');
                case 'l': return Number(Hour12(t.Hour), modifiers, width, 2, ' ');
                case 'j': return Number(t.DayOfYear, modifiers, width, 3, '0');
                case 'm': return Number(t.Month, modifiers, width, 2, '0');
                case 'M': return Number(t.Minute, modifiers, width, 2, '0');
                case 'S': return Number(t.Second, modifiers, width, 2, '0');
                case 'u': return Number((t.DayOfWeek == 0) ? 7 : t.DayOfWeek, modifiers, width, 1, '0');
                case 'w': return Number(t.DayOfWeek, modifiers, width, 1, '0');
                case 'y': return Number(Mod(t.Year, 100), modifiers, width, 2, '0');
                case 'Y': return Number(t.Year, modifiers, width, 4, '0');

                case 'G': return Number(IsoYear(t), modifiers, width, 4, '0');
                case 'g': return Number(Mod(IsoYear(t), 100), modifiers, width, 2, '0');
                case 'V': return Number(IsoWeek(t), modifiers, width, 2, '0');

                case 'U': return Number((t.DayOfYear - 1 + 7 - t.DayOfWeek) / 7, modifiers, width, 2, '0');
                case 'W': return Number((t.DayOfYear - 1 + 7 - ((t.DayOfWeek + 6) % 7)) / 7, modifiers, width, 2, '0');

                case 's': return Number(time.Seconds, modifiers, width, 1, '0');

                case 'L': return Fraction(time, (width < 0) ? 3 : width);
                case 'N': return Fraction(time, (width < 0) ? 9 : width);

                case 'z': return Text(time.FormatUtcOffset(
                    (modifiers & (Modifiers.Colons1 | Modifiers.Colons2 | Modifiers.Colons3)) != 0,
                    (modifiers & (Modifiers.Colons2 | Modifiers.Colons3)) != 0), modifiers, width);

                case 'Z': {
                        string name = time.GetZoneName();
                        if (name == null) {
                            name = time.FormatUtcOffset(true, false);
                        }
                        return Text(name, modifiers, width);
                    }

                case 'c': return Compound(time, "%a %b %e %H:%M:%S %Y", modifiers, depth);
                case 'D':
                case 'x': return Compound(time, "%m/%d/%y", modifiers, depth);
                case 'F': return Compound(time, "%Y-%m-%d", modifiers, depth);
                case 'r': return Compound(time, "%I:%M:%S %p", modifiers, depth);
                case 'R': return Compound(time, "%H:%M", modifiers, depth);
                case 'T':
                case 'X': return Compound(time, "%H:%M:%S", modifiers, depth);
                case 'v': return Compound(time, "%e-%^b-%4Y", modifiers, depth);
                case '+': return Compound(time, "%a %b %e %H:%M:%S %Z %Y", modifiers, depth);

                default: return null;
            }
        }

        private static string/*!*/ Compound(RubyTime/*!*/ time, string/*!*/ format, Modifiers modifiers, int depth) {
            MutableString buffer = MutableString.CreateMutable(RubyEncoding.Binary);
            Format(buffer, time, format, depth + 1);
            return Text(buffer.ToString(), modifiers & (Modifiers.Upcase | Modifiers.Swapcase), -1);
        }

        /// <summary>ISO-8601 week-based year and week number, for %G/%g/%V.</summary>
        private static void IsoWeekDate(RubyTime.Fields t, out int year, out int week) {
            int isoWeekday = (t.DayOfWeek == 0) ? 7 : t.DayOfWeek;   // Monday = 1 .. Sunday = 7
            long days = RubyTime.DaysFromCivil(t.Year, t.Month, t.Day);
            long thursday = days + (4 - isoWeekday);                 // the Thursday of this ISO week
            long y;
            int m, d;
            RubyTime.CivilFromDays(thursday, out y, out m, out d);
            year = (int)y;
            week = (int)((thursday - RubyTime.DaysFromCivil(y, 1, 1)) / 7) + 1;
        }

        private static int IsoYear(RubyTime.Fields t) {
            int year, week;
            IsoWeekDate(t, out year, out week);
            return year;
        }

        private static int IsoWeek(RubyTime.Fields t) {
            int year, week;
            IsoWeekDate(t, out year, out week);
            return week;
        }

        private static int Hour12(int hour) {
            int result = hour % 12;
            return (result == 0) ? 12 : result;
        }

        private static int FloorDiv(int value, int divisor) {
            int q = value / divisor;
            return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? q - 1 : q;
        }

        private static int Mod(int value, int divisor) {
            int r = value % divisor;
            return (r < 0) ? r + divisor : r;
        }

        private static string/*!*/ Text(string/*!*/ value, Modifiers modifiers, int width) {
            if ((modifiers & Modifiers.Upcase) != 0) {
                value = value.ToUpperInvariant();
            } else if ((modifiers & Modifiers.Swapcase) != 0) {
                value = Swapcase(value);
            }
            if (width > value.Length) {
                value = value.PadLeft(width, ' ');
            }
            return value;
        }

        private static string/*!*/ Swapcase(string/*!*/ value) {
            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char c in value) {
                if (Char.IsUpper(c)) {
                    builder.Append(Char.ToLowerInvariant(c));
                } else if (Char.IsLower(c)) {
                    builder.Append(Char.ToUpperInvariant(c));
                } else {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }

        private static string/*!*/ Number(long value, Modifiers modifiers, int width, int defaultWidth, char defaultPad) {
            string digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
            string sign = (value < 0) ? "-" : "";

            char pad = defaultPad;
            if ((modifiers & Modifiers.ZeroPadding) != 0) {
                pad = '0';
            } else if ((modifiers & Modifiers.SpacePadding) != 0) {
                pad = ' ';
            }

            int target = (width >= 0) ? width : defaultWidth;
            if ((modifiers & Modifiers.NoPadding) != 0 && width < 0) {
                target = 0;
            }

            int fill = target - digits.Length - sign.Length;
            if (fill <= 0) {
                return sign + digits;
            }
            if (pad == '0') {
                return sign + new string('0', fill) + digits;
            }
            return new string(pad, fill) + sign + digits;
        }

        /// <summary>Sub-second digits, e.g. %N (9 digits) and %L (3 digits).</summary>
        private static string/*!*/ Fraction(RubyTime/*!*/ time, int digits) {
            if (digits <= 0) {
                return "";
            }

            ExactNum subsec = time.Subsec;
            BigInteger scale = BigInteger.Pow(new BigInteger(10), digits);
            BigInteger value = (subsec.Numerator * scale) / subsec.Denominator;
            return value.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        }
    }
}
