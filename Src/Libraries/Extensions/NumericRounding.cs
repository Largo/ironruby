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
using System.Numerics;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    /// <summary>
    /// Shared implementation of the <c>ndigits</c>/<c>half:</c> forms of
    /// Integer#round/floor/ceil/truncate and Float#round/floor/ceil/truncate.
    ///
    /// This is a plain helper class: it carries no [RubyClass]/[RubyModule]/[RubyMethod]
    /// attributes, so it is not visible to the class-init generator.
    /// </summary>
    internal static class NumericRounding {
        internal enum Half {
            Up,
            Down,
            Even
        }

        #region argument parsing

        /// <summary>
        /// Reads the <c>half:</c> keyword out of a trailing options hash. MRI defaults to :up.
        /// </summary>
        internal static Half GetHalfOption(RubyContext/*!*/ context, IDictionary<object, object> options) {
            if (options == null) {
                return Half.Up;
            }

            foreach (var entry in options) {
                var key = entry.Key as RubySymbol;
                if (key == null || key.ToString() != "half") {
                    continue;
                }

                object value = entry.Value;
                if (value == null) {
                    return Half.Up;
                }

                string mode = null;
                var symbol = value as RubySymbol;
                if (symbol != null) {
                    mode = symbol.ToString();
                } else {
                    var str = value as MutableString;
                    if (str != null) {
                        mode = str.ToString();
                    }
                }

                switch (mode) {
                    case "up": return Half.Up;
                    case "down": return Half.Down;
                    case "even": return Half.Even;
                }

                throw RubyExceptions.CreateArgumentError("invalid rounding mode: {0}",
                    mode ?? context.Inspect(value).ToString());
            }

            return Half.Up;
        }

        /// <summary>
        /// MRI converts ndigits with NUM2INT: Floats truncate, anything that doesn't fit in an
        /// int is a RangeError, anything without #to_int is a TypeError.
        /// </summary>
        internal static int GetNDigits(ConversionStorage<IntegerValue>/*!*/ integerCast, object ndigits) {
            if (ndigits == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            }

            if (ndigits is double) {
                double d = (double)ndigits;
                if (Double.IsNaN(d)) {
                    throw new FloatDomainError("NaN");
                }
                if (d >= 2147483648.0 || d <= -2147483649.0) {
                    throw RubyExceptions.CreateRangeError("float {0} out of range of integer", d);
                }
                return (int)d;
            }

            IntegerValue value = Protocols.CastToInteger(integerCast, ndigits);
            if (!value.IsFixnum) {
                throw RubyExceptions.CreateRangeError("integer {0} too big to convert to `int'", value.Bignum);
            }
            return value.Fixnum;
        }

        internal static object/*!*/ Normalize(BigInteger value) {
            int result;
            if (value.AsInt32(out result)) {
                return ScriptingRuntimeHelpers.Int32ToObject(result);
            }
            return value;
        }

        #endregion

        #region double rounding primitives (ports of MRI's round_half_{up,down,even})

        internal static double RoundHalf(double x, double s, Half mode) {
            switch (mode) {
                case Half.Down: return RoundHalfDown(x, s);
                case Half.Even: return RoundHalfEven(x, s);
                default: return RoundHalfUp(x, s);
            }
        }

        // MRI: round(x) - half away from zero.
        private static double RoundAwayFromZero(double x) {
            return Math.Round(x, MidpointRounding.AwayFromZero);
        }

        private static double RoundHalfUp(double x, double s) {
            double xs = x * s;
            double f = RoundAwayFromZero(xs);

            if (s == 1.0) {
                return f;
            }
            if (x > 0) {
                if ((f + 0.5) / s <= x) {
                    f += 1;
                }
            } else {
                if ((f - 0.5) / s >= x) {
                    f -= 1;
                }
            }
            return f;
        }

        private static double RoundHalfDown(double x, double s) {
            double xs = x * s;
            double f = RoundAwayFromZero(xs);

            if (x > 0) {
                if ((f - 0.5) / s >= x) {
                    f -= 1;
                }
            } else {
                if ((f + 0.5) / s <= x) {
                    f += 1;
                }
            }
            return f;
        }

        private static double RoundHalfEven(double x, double s) {
            double u = Math.Truncate(x);
            double v = x - u;
            double us = u * s;
            double vs = v * s;
            double f, d, uf;

            if (x > 0.0) {
                f = Math.Floor(vs);
                uf = us + f;
                d = vs - f;
                if (d > 0.5) {
                    d = 1.0;
                } else if (d == 0.5 || (uf + 0.5) / s <= x) {
                    d = (uf % 2.0 == 0.0) ? 0.0 : 1.0;
                } else {
                    d = 0.0;
                }
                x = f + d;
            } else if (x < 0.0) {
                f = Math.Ceiling(vs);
                uf = us + f;
                d = f - vs;
                if (d > 0.5) {
                    d = 1.0;
                } else if (d == 0.5 || (uf - 0.5) / s >= x) {
                    d = (uf % 2.0 == 0.0) ? 0.0 : 1.0;
                } else {
                    d = 0.0;
                }
                x = f - d;
            } else {
                return us;
            }
            return us + x;
        }

        #endregion

        #region MRI's float_round_overflow / float_round_underflow

        private const int FloatDig = 17; // DBL_DIG + 2
        private const int DblMantDig = 53;

        // frexp's exponent: number = mantissa * 2**binexp with 0.5 <= |mantissa| < 1
        private static int BinaryExponent(double x) {
            return Math.ILogB(x) + 1;
        }

        internal static bool RoundOverflows(double number, int ndigits) {
            int binexp = BinaryExponent(number);
            return ndigits >= FloatDig - (binexp > 0 ? binexp / 4 : binexp / 3 - 1);
        }

        internal static bool RoundUnderflows(double number, int ndigits) {
            return BinaryExponent(number) < -DblMantDig;
        }

        #endregion

        #region integer rounding

        /// <summary>
        /// Rounds an integer to a multiple of 10**(-ndigits); ndigits must be negative.
        /// </summary>
        internal static object/*!*/ RoundInteger(BigInteger value, int ndigits, Half mode) {
            if (RoundsAwayEntirely(value, ndigits)) {
                return ScriptingRuntimeHelpers.Int32ToObject(0);
            }

            BigInteger f = Pow10(-ndigits);
            int sign = value.Sign;
            BigInteger x = sign < 0 ? -value : value;

            BigInteger remainder;
            BigInteger q = BigInteger.DivRem(x, f, out remainder);

            int cmp = (remainder * 2).CompareTo(f);
            bool up;
            if (cmp > 0) {
                up = true;
            } else if (cmp < 0) {
                up = false;
            } else {
                switch (mode) {
                    case Half.Down: up = false; break;
                    case Half.Even: up = !(q % 2).IsZero; break;
                    default: up = true; break;
                }
            }

            if (up) {
                q += BigInteger.One;
            }

            BigInteger result = q * f;
            return Normalize(sign < 0 ? -result : result);
        }

        /// <summary>Floors an integer to a multiple of 10**(-ndigits); ndigits must be negative.</summary>
        internal static object/*!*/ FloorInteger(BigInteger value, int ndigits) {
            if (RoundsAwayEntirely(value, ndigits) && value.Sign >= 0) {
                return ScriptingRuntimeHelpers.Int32ToObject(0);
            }
            BigInteger f = Pow10(-ndigits);
            BigInteger remainder = BigInteger.Remainder(value, f);
            if (remainder.Sign < 0) {
                remainder += f;
            }
            return Normalize(value - remainder);
        }

        /// <summary>Ceils an integer to a multiple of 10**(-ndigits); ndigits must be negative.</summary>
        internal static object/*!*/ CeilInteger(BigInteger value, int ndigits) {
            if (RoundsAwayEntirely(value, ndigits) && value.Sign <= 0) {
                return ScriptingRuntimeHelpers.Int32ToObject(0);
            }
            BigInteger f = Pow10(-ndigits);
            BigInteger remainder = BigInteger.Remainder(value, f);
            if (remainder.IsZero) {
                return Normalize(value);
            }
            if (remainder.Sign < 0) {
                remainder += f;
            }
            return Normalize(value - remainder + f);
        }

        /// <summary>Truncates an integer towards zero to a multiple of 10**(-ndigits); ndigits must be negative.</summary>
        internal static object/*!*/ TruncateInteger(BigInteger value, int ndigits) {
            if (RoundsAwayEntirely(value, ndigits)) {
                return ScriptingRuntimeHelpers.Int32ToObject(0);
            }
            BigInteger f = Pow10(-ndigits);
            return Normalize(value - BigInteger.Remainder(value, f));
        }

        /// <summary>
        /// True when 10**(-ndigits) is so much larger than the value that every rounding mode
        /// that moves towards zero produces 0. Avoids materialising an absurdly large 10**n.
        /// </summary>
        private static bool RoundsAwayEntirely(BigInteger value, int ndigits) {
            if (value.IsZero) {
                return true;
            }
            // number of decimal digits, erring on the high side
            int digits = (int)Math.Floor(BigInteger.Log10(BigInteger.Abs(value))) + 2;
            return -(long)ndigits > digits;
        }

        private static BigInteger Pow10(int exponent) {
            return BigInteger.Pow(10, exponent);
        }

        #endregion
    }
}
