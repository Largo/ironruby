/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Numerics;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// Integer and Float conversions (CRuby's bignum.c, numeric.c and object.c parts of the API).
    /// </summary>
    internal static unsafe partial class CApi {

        internal static object Normalize(BigInteger value) {
            if (value >= Int32.MinValue && value <= Int32.MaxValue) {
                return (int)value;
            }
            if (value >= Int64.MinValue && value <= Int64.MaxValue) {
                return (long)value;
            }
            return value;
        }

        private static BigInteger? AsInteger(object obj) {
            switch (obj) {
                case int i: return i;
                case long l: return l;
                case BigInteger b: return b;
            }
            return null;
        }

        /// <summary>
        /// An Integer for a C integer conversion, as CRuby's rb_num2long: Integers as they are,
        /// Floats truncated, nil and true/false refused, anything else through #to_int.
        /// </summary>
        private static BigInteger ToBigInteger(object obj, string/*!*/ ctype) {
            var i = AsInteger(obj);
            if (i.HasValue) {
                return i.Value;
            }
            if (obj is double d) {
                if (Double.IsNaN(d) || Double.IsInfinity(d) || Math.Truncate(d) > 9.3e18 || Math.Truncate(d) < -9.3e18) {
                    throw RubyExceptions.CreateRangeError(String.Format("float {0} out of range of integer", FormatFloat(d)));
                }
                return new BigInteger(Math.Truncate(d));
            }
            if (obj == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            }
            if (obj is bool b) {
                throw RubyExceptions.CreateTypeError(String.Format("no implicit conversion of {0} into Integer", b ? "true" : "false"));
            }
            // a String is no Integer to C (rb_to_int), whatever Kernel#Integer would make of it
            if (!(obj is MutableString)) {
                var r = AsInteger(Send(_context.KernelModule, "Integer", null, obj));
                if (r.HasValue) {
                    return r.Value;
                }
            }
            throw RubyExceptions.CreateTypeError(String.Format("no implicit conversion of {0} into Integer", ClassName(obj)));
        }

        // rb_num2long's "%-.10g"
        private static string/*!*/ FormatFloat(double d) {
            return RubyUtils.FormatFloatG10(d);
        }

        // C's long: 64-bit on Unix, 32-bit on Windows
        private static readonly long CLongMax = IsWindows ? Int32.MaxValue : Int64.MaxValue;
        private static readonly long CLongMin = IsWindows ? Int32.MinValue : Int64.MinValue;
        private static readonly ulong CULongMax = IsWindows ? UInt32.MaxValue : UInt64.MaxValue;

        private static long ToLong(VALUE value, string ctype, long min = Int64.MinValue, long max = Int64.MaxValue) {
            if ((value & 1) != 0) {
                return (long)value >> 1;
            }
            object obj = FromValue(value);
            BigInteger b = ToBigInteger(obj, ctype);
            if (b < min || b > max) {
                throw RubyExceptions.CreateRangeError(String.Format("bignum too big to convert into '{0}'", ctype));
            }
            return (long)b;
        }

        private static ulong ToULong(VALUE value, string ctype, ulong max = UInt64.MaxValue) {
            if ((value & 1) != 0) {
                return (ulong)((long)value >> 1);
            }
            BigInteger b = ToBigInteger(FromValue(value), ctype);
            // CRuby accepts the negative values of the signed type and wraps them
            if (b < -(BigInteger)max / 2 - 1 || b > max) {
                throw RubyExceptions.CreateRangeError(String.Format("bignum out of range of {0}", ctype));
            }
            return b < 0 ? (ulong)(long)b & max : (ulong)b;
        }

        internal static long rb_num2long(VALUE num) {
            return ToLong(num, "long", CLongMin, CLongMax);
        }

        internal static ulong rb_num2ulong(VALUE num) {
            return ToULong(num, "unsigned long", CULongMax);
        }

        internal static long rb_num2ll(VALUE num) {
            return ToLong(num, "long long");
        }

        internal static ulong rb_num2ull(VALUE num) {
            return ToULong(num, "unsigned long long");
        }

        private static void CheckInt(long n, string ctype, long min, long max) {
            if (n < min || n > max) {
                throw RubyExceptions.CreateRangeError(String.Format("integer {0} too {1} to convert to '{2}'", n, n < min ? "small" : "big", ctype));
            }
        }

        internal static long rb_num2int(VALUE num) {
            long n = ToLong(num, "int");
            CheckInt(n, "int", Int32.MinValue, Int32.MaxValue);
            return n;
        }

        internal static long rb_fix2int(VALUE num) {
            return rb_num2int(num);
        }

        internal static void rb_out_of_int(long num) {
            CheckInt(num, "int", Int32.MinValue, Int32.MaxValue);
        }

        internal static ulong rb_num2uint(VALUE num) {
            long n = ToLong(num, "unsigned int");
            // CRuby accepts INT_MIN..UINT_MAX and wraps the negatives (check_uint)
            if (n < Int32.MinValue || n > UInt32.MaxValue) {
                throw RubyExceptions.CreateRangeError(String.Format("integer {0} too {1} to convert to 'unsigned int'", n, n < 0 ? "small" : "big"));
            }
            return (ulong)n;
        }

        internal static short rb_num2short(VALUE num) {
            long n = ToLong(num, "short");
            CheckInt(n, "short", Int16.MinValue, Int16.MaxValue);
            return (short)n;
        }

        internal static short rb_fix2short(VALUE num) {
            return rb_num2short(num);
        }

        internal static ushort rb_num2ushort(VALUE num) {
            long n = ToLong(num, "unsigned short");
            if (n < -UInt16.MaxValue || n > UInt16.MaxValue) {
                throw RubyExceptions.CreateRangeError(String.Format("integer {0} too {1} to convert to 'unsigned short'", n, n < 0 ? "small" : "big"));
            }
            return (ushort)n;
        }

        internal static VALUE rb_num2fix(VALUE val) {
            if ((val & 1) != 0) {
                return val;
            }
            long n = rb_num2long(val);
            if (n < FixnumMin || n > FixnumMax) {
                throw RubyExceptions.CreateRangeError(String.Format("integer {0} out of range of fixnum", n));
            }
            return ((VALUE)n << 1) | 1;
        }

        internal static VALUE rb_int2big(nint i) {
            return ToValue(Normalize(i));
        }

        internal static VALUE rb_ll2inum(long num) {
            return ToValue(Normalize(num));
        }

        internal static VALUE rb_ull2inum(ulong num) {
            return ToValue(Normalize(num));
        }

        internal static double rb_num2dbl(VALUE num) {
            object obj = FromValue(num);
            switch (obj) {
                case double d: return d;
                case int i: return i;
                case long l: return l;
                case BigInteger b: return (double)b;
                case null: throw RubyExceptions.CreateTypeError("no implicit conversion to float from nil");
                case bool b: throw RubyExceptions.CreateTypeError(String.Format("no implicit conversion to float from {0}", b ? "true" : "false"));
                case MutableString _: throw RubyExceptions.CreateTypeError("no implicit conversion to float from string");
            }
            object f = Send(obj, "to_f", null);
            if (f is double r) {
                return r;
            }
            throw RubyExceptions.CreateTypeError(String.Format("can't convert {0} into Float", ClassName(obj)));
        }

        internal static VALUE rb_float_new(double d) {
            return ToValue(d);
        }

        internal static VALUE rb_float_new_in_heap(double d) {
            return ToValue(d);
        }

        internal static double rb_float_value(VALUE num) {
            object obj = FromValue(num);
            if (obj is double d) {
                return d;
            }
            throw RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (expected Float)", ClassName(obj)));
        }

        internal static VALUE rb_dbl2big(double d) {
            if (Double.IsNaN(d)) {
                RaiseNamed("FloatDomainError", "NaN");
            }
            if (Double.IsInfinity(d)) {
                RaiseNamed("FloatDomainError", d < 0 ? "-Infinity" : "Infinity");
            }
            return ToValue(Normalize(new BigInteger(Math.Truncate(d))));
        }

        internal static double rb_big2dbl(VALUE x) {
            var b = AsInteger(FromValue(x));
            if (!b.HasValue) {
                throw RubyExceptions.CreateTypeError("wrong argument type (expected Integer)");
            }
            return (double)b.Value;
        }

        internal static long rb_big2long(VALUE x) {
            return ToLong(x, "long", CLongMin, CLongMax);
        }

        internal static ulong rb_big2ulong(VALUE x) {
            return ToULong(x, "unsigned long", CULongMax);
        }

        internal static long rb_big2ll(VALUE x) {
            return ToLong(x, "long long");
        }

        internal static ulong rb_big2ull(VALUE x) {
            return ToULong(x, "unsigned long long");
        }

        internal static VALUE rb_big2str(VALUE x, int base_) {
            return ToValue(Send(FromValue(x), "to_s", null, base_));
        }

        internal static VALUE rb_fix2str(VALUE val, int base_) {
            return ToValue(Send(FromValue(val), "to_s", null, base_));
        }

        internal static VALUE rb_Integer(VALUE val) {
            return ToValue(Send(_context.KernelModule, "Integer", null, FromValue(val)));
        }

        internal static VALUE rb_Float(VALUE val) {
            return ToValue(Send(_context.KernelModule, "Float", null, FromValue(val)));
        }

        internal static VALUE rb_Rational(VALUE num, VALUE den) {
            return ToValue(Send(_context.KernelModule, "Rational", null, FromValue(num), FromValue(den)));
        }

        internal static VALUE rb_Complex(VALUE real, VALUE imag) {
            return ToValue(Send(_context.KernelModule, "Complex", null, FromValue(real), FromValue(imag)));
        }

        internal static VALUE rb_to_int(VALUE val) {
            object obj = FromValue(val);
            if (AsInteger(obj).HasValue) {
                return val;
            }
            object r = null;
            if (Protocols.IsTrue(Send(obj, "respond_to?", null, _context.CreateAsciiSymbol("to_int"), true))) {
                r = Send(obj, "to_int", null);
                if (AsInteger(r).HasValue) {
                    return ToValue(r);
                }
                throw RubyExceptions.CreateTypeError(String.Format("can't convert {0} to Integer ({0}#to_int gives {1})", ClassName(obj), ClassName(r)));
            }
            throw RubyExceptions.CreateTypeError(String.Format("no implicit conversion of {0} into Integer", obj == null ? "nil" : ClassName(obj)));
        }

        internal static VALUE rb_cstr_to_inum(byte* str, int base_, int badcheck) {
            var s = MutableString.CreateBinary(CBytes(str), RubyEncoding.Binary);
            return StringToInteger(s, base_, badcheck != 0);
        }

        internal static VALUE rb_cstr2inum(byte* str, int base_) {
            return rb_cstr_to_inum(str, base_, base_ == 0 ? 1 : 0);
        }

        internal static VALUE rb_str2inum(VALUE str, int base_) {
            return StringToInteger(ToMutableString(str), base_, base_ == 0);
        }

        private static VALUE StringToInteger(MutableString/*!*/ s, int base_, bool badcheck) {
            if (badcheck) {
                return ToValue(Send(_context.KernelModule, "Integer", null, s, base_ == 0 ? 10 : base_));
            }
            // String#to_i takes the base 0 as "from the prefix" too only through Integer(); CRuby's
            // rb_cstr_to_inum without badcheck does, so a prefix decides here as well.
            return ToValue(Send(s, "to_i", null, base_ == 0 ? 10 : base_));
        }

        internal static double rb_cstr_to_dbl(byte* str, int mode) {
            var s = MutableString.CreateBinary(CBytes(str), RubyEncoding.Binary);
            return (double)(mode != 0 ? Send(_context.KernelModule, "Float", null, s) : Send(s, "to_f", null));
        }

        internal static double rb_str_to_dbl(VALUE str, int mode) {
            var s = ToMutableString(str);
            return (double)(mode != 0 ? Send(_context.KernelModule, "Float", null, s) : Send(s, "to_f", null));
        }

        internal static VALUE rb_int_positive_pow(long x, ulong y) {
            return ToValue(Normalize(BigInteger.Pow(x, checked((int)y))));
        }

        // INTEGER_PACK_* (ruby/internal/intern/bignum.h)
        private const int PACK_MSWORD_FIRST = 0x01, PACK_LSWORD_FIRST = 0x02, PACK_MSBYTE_FIRST = 0x10,
            PACK_LSBYTE_FIRST = 0x20, PACK_NATIVE_BYTE_ORDER = 0x40, PACK_2COMP = 0x80;

        /// <summary>
        /// CRuby's rb_integer_pack: the absolute value (or with INTEGER_PACK_2COMP, the two's
        /// complement) into numwords words of wordsize bytes. Returns the sign, or +-2 when the
        /// value did not fit.
        /// </summary>
        internal static int rb_integer_pack(VALUE val, void* words, nuint numwords, nuint wordsize, nuint nails, int flags) {
            if (nails != 0) {
                throw RubyExceptions.CreateNotImplementedError("rb_integer_pack with nails is not implemented by IronRuby's C API yet");
            }
            BigInteger value = ToBigInteger(FromValue(val), "integer");
            int sign = value.Sign;
            bool twoComp = (flags & PACK_2COMP) != 0;
            int total = checked((int)(numwords * wordsize));

            BigInteger magnitude = BigInteger.Abs(value);
            BigInteger modulus = BigInteger.One << (total * 8);
            BigInteger stored = twoComp ? ((value % modulus) + modulus) % modulus : magnitude % modulus;
            byte[] le = stored.ToByteArray(isUnsigned: true, isBigEndian: false);
            bool overflow = twoComp ? (value >= modulus || value < -modulus) : magnitude >= modulus;

            byte* dst = (byte*)words;
            bool msWordFirst = (flags & PACK_MSWORD_FIRST) != 0;
            bool msByteFirst = (flags & PACK_MSBYTE_FIRST) != 0 ||
                ((flags & PACK_NATIVE_BYTE_ORDER) != 0 && !BitConverter.IsLittleEndian);
            for (int w = 0; w < (int)numwords; w++) {
                int wordIndex = msWordFirst ? (int)numwords - 1 - w : w;   // w counts from the least significant word
                for (int b = 0; b < (int)wordsize; b++) {
                    int byteIndex = msByteFirst ? (int)wordsize - 1 - b : b;
                    int k = w * (int)wordsize + b;
                    dst[wordIndex * (int)wordsize + byteIndex] = k < le.Length ? le[k] : (byte)0;
                }
            }
            if (overflow) {
                return sign < 0 ? -2 : 2;
            }
            return sign;
        }
    }
}
