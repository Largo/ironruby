/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// Strings and encodings.
    /// </summary>
    internal static unsafe partial class CApi {

        #region Encodings

        // An encoding's index is CRuby's for the three it fixes (ASCII-8BIT 0, UTF-8 1, US-ASCII 2,
        // which C code compares against) and its place in IronRuby's list for the rest.
        private static readonly object _encodingLock = new object();
        private static List<RubyEncoding> _encodings;
        private static Dictionary<RubyEncoding, int> _encodingIndex;
        private static IntPtr[] _nativeEncodings;

        private static void InitializeEncodings() {
            if (_encodings != null) {
                return;
            }
            var list = new List<RubyEncoding> { RubyEncoding.Binary, RubyEncoding.UTF8, RubyEncoding.Ascii };
            foreach (var e in RubyEncoding.GetAll()) {
                if (!list.Contains(e)) {
                    list.Add(e);
                }
            }
            var index = new Dictionary<RubyEncoding, int>();
            for (int i = 0; i < list.Count; i++) {
                index[list[i]] = i;
            }
            _nativeEncodings = new IntPtr[list.Count];
            _encodingIndex = index;
            _encodings = list;
        }

        internal static int EncodingIndex(RubyEncoding/*!*/ encoding) {
            lock (_encodingLock) {
                InitializeEncodings();
                int i;
                if (!_encodingIndex.TryGetValue(encoding, out i)) {
                    i = _encodings.Count;
                    _encodings.Add(encoding);
                    _encodingIndex[encoding] = i;
                    Array.Resize(ref _nativeEncodings, _encodings.Count);
                }
                return i;
            }
        }

        internal static RubyEncoding/*!*/ EncodingAt(int index) {
            lock (_encodingLock) {
                InitializeEncodings();
                if (index < 0 || index >= _encodings.Count) {
                    throw RubyExceptions.CreateArgumentError(String.Format("invalid encoding index {0}", index));
                }
                return _encodings[index];
            }
        }

        private static int MinCharLength(RubyEncoding/*!*/ encoding) {
            string name = encoding.Name;
            if (name.StartsWith("UTF-32", StringComparison.OrdinalIgnoreCase)) return 4;
            if (name.StartsWith("UTF-16", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;
        }

        /// <summary>
        /// rb_ir_make_encoding's kind: which character functions the native rb_encoding gets.
        /// </summary>
        private static int NativeKind(RubyEncoding/*!*/ encoding) {
            switch (encoding.Name) {
                case "UTF-8": case "UTF8-MAC": case "UTF8-DoCoMo": case "UTF8-KDDI": case "UTF8-SoftBank": case "CESU-8":
                    return 1;
                case "UTF-16BE": return 2;
                case "UTF-16LE": return 3;
                case "UTF-32BE": return 4;
                case "UTF-32LE": return 5;
                case "ISO-8859-1": case "ISO-8859-15": case "Windows-1252":
                    return 6;
                default:
                    return 0;
            }
        }

        internal static void* NativeEncoding(RubyEncoding encoding) {
            if (encoding == null) {
                return null;
            }
            int index = EncodingIndex(encoding);
            lock (_encodingLock) {
                IntPtr native = _nativeEncodings[index];
                if (native == IntPtr.Zero) {
                    fixed (byte* name = CString(encoding.Name)) {
                        native = (IntPtr)_makeEncoding(name, MinCharLength(encoding), Math.Max(1, encoding.MaxBytesPerChar), index,
                            NativeKind(encoding));
                    }
                    _nativeEncodings[index] = native;
                }
                return (void*)native;
            }
        }

        /// <summary>
        /// The RubyEncoding of an rb_encoding* IronRuby handed out.
        /// </summary>
        internal static RubyEncoding FromNativeEncoding(void* enc) {
            if (enc == null) {
                return null;
            }
            // ruby_encoding_index is the struct's last int but one; the index is the reliable key
            lock (_encodingLock) {
                InitializeEncodings();
                for (int i = 0; i < _nativeEncodings.Length; i++) {
                    if (_nativeEncodings[i] == (IntPtr)enc) {
                        return _encodings[i];
                    }
                }
            }
            throw RubyExceptions.CreateArgumentError("not an rb_encoding IronRuby handed out");
        }

        private static RubyEncoding GetEncoding(object obj) {
            switch (obj) {
                case MutableString s: return s.Encoding;
                case RubySymbol sym: return sym.Encoding;
                case RubyRegex r: return r.Encoding;
                case RubyEncoding e: return e;
            }
            return null;
        }

        internal static int rb_enc_get_index(VALUE obj) {
            object o = FromValue(obj);
            var e = GetEncoding(o);
            return e != null ? EncodingIndex(e) : -1;
        }

        internal static void rb_enc_set_index(VALUE obj, int encindex) {
            SetEncoding(FromValue(obj), EncodingAt(encindex));
        }

        // encoding.c's rb_enc_associate_index: strings and regexps carry one, a symbol's is fixed
        private static void SetEncoding(object obj, RubyEncoding/*!*/ encoding) {
            switch (obj) {
                case MutableString s:
                    s.RequireNotFrozen();
                    s.ForceEncoding(encoding);
                    return;
                case RubyRegex r:
                    // the source's bytes in the new encoding, kept by FIXEDENCODING as CRuby's are
                    if (r.Encoding != encoding) {
                        r.Set(MutableString.CreateBinary(r.Pattern.ToByteArray(), encoding),
                            (r.Options & ~RubyRegexOptions.EncodingMask) | RubyRegexOptions.FixedEncoding);
                    }
                    return;
                case RubySymbol _:
                    throw RubyExceptions.CreateRuntimeError("cannot modify the encoding of a Symbol");
            }
            throw RubyExceptions.CreateArgumentError(String.Format("cannot set encoding on non-encoding capable object"));
        }

        internal static void* rb_enc_get(VALUE obj) {
            return NativeEncoding(GetEncoding(FromValue(obj)));
        }

        internal static VALUE rb_enc_associate_index(VALUE obj, int encindex) {
            rb_enc_set_index(obj, encindex);
            return obj;
        }

        internal static VALUE rb_enc_associate(VALUE obj, void* enc) {
            SetEncoding(FromValue(obj), FromNativeEncoding(enc) ?? RubyEncoding.Binary);
            return obj;
        }

        internal static void rb_enc_copy(VALUE dst, VALUE src) {
            SetEncoding(FromValue(dst), GetEncoding(FromValue(src)) ?? RubyEncoding.Binary);
        }

        internal static void* rb_enc_from_index(int idx) {
            lock (_encodingLock) {
                InitializeEncodings();
                if (idx < 0 || idx >= _encodings.Count) {
                    return null;
                }
            }
            return NativeEncoding(EncodingAt(idx));
        }

        private static RubyEncoding FindEncoding(string/*!*/ name) {
            try {
                return _context.GetRubyEncoding(name);
            } catch (Exception) {
                return null;
            }
        }

        internal static int rb_enc_find_index(byte* name) {
            var e = FindEncoding(FromCString(name));
            return e != null ? EncodingIndex(e) : -1;
        }

        internal static void* rb_enc_find(byte* name) {
            return NativeEncoding(FindEncoding(FromCString(name)));
        }

        // encoding.c's str_to_encoding: an Encoding, or a name through to_str
        internal static void* rb_to_encoding(VALUE obj) {
            object o = FromValue(obj);
            if (o is RubyEncoding e) {
                return NativeEncoding(e);
            }
            var name = FromValue(rb_str_to_str(obj)).ToString();
            var found = FindEncoding(name);
            if (found == null) {
                throw RubyExceptions.CreateArgumentError(String.Format("unknown encoding name - {0}", name));
            }
            return NativeEncoding(found);
        }

        internal static VALUE rb_enc_from_encoding(void* enc) {
            return enc == null ? Qnil : ToValue(FromNativeEncoding(enc));
        }

        internal static VALUE rb_obj_encoding(VALUE obj) {
            var e = GetEncoding(FromValue(obj));
            if (e == null) {
                throw RubyExceptions.CreateTypeError("unknown encoding");
            }
            return ToValue(e);
        }

        internal static void* rb_utf8_encoding() { return NativeEncoding(RubyEncoding.UTF8); }
        internal static void* rb_usascii_encoding() { return NativeEncoding(RubyEncoding.Ascii); }
        internal static void* rb_ascii8bit_encoding() { return NativeEncoding(RubyEncoding.Binary); }
        internal static int rb_utf8_encindex() { return 1; }
        internal static int rb_usascii_encindex() { return 2; }
        internal static int rb_ascii8bit_encindex() { return 0; }

        #endregion

        #region Creating strings

        private static MutableString/*!*/ NewString(byte* ptr, long len, RubyEncoding/*!*/ encoding) {
            return MutableString.CreateBinary(Bytes(ptr, len), encoding);
        }

        internal static VALUE rb_str_new(byte* ptr, long len) {
            return ToValue(NewString(ptr, len, RubyEncoding.Binary));
        }

        internal static VALUE rb_str_new_cstr(byte* ptr) {
            return ToValue(MutableString.CreateBinary(CBytes(ptr), RubyEncoding.Binary));
        }

        private static VALUE NewStaticString(byte* ptr, long len, RubyEncoding/*!*/ encoding) {
            var s = NewString(ptr, len, encoding);
            if (ptr != null) {
                AdoptStaticBuffer(s, ptr, (int)len);
            }
            return ToValue(s);
        }

        internal static VALUE rb_str_new_static(byte* ptr, long len) {
            return NewStaticString(ptr, len, RubyEncoding.Binary);
        }

        internal static VALUE rb_usascii_str_new(byte* ptr, long len) {
            return ToValue(NewString(ptr, len, RubyEncoding.Ascii));
        }

        internal static VALUE rb_usascii_str_new_cstr(byte* ptr) {
            return ToValue(MutableString.CreateBinary(CBytes(ptr), RubyEncoding.Ascii));
        }

        internal static VALUE rb_usascii_str_new_static(byte* ptr, long len) {
            return NewStaticString(ptr, len, RubyEncoding.Ascii);
        }

        internal static VALUE rb_utf8_str_new_static(byte* ptr, long len) {
            return NewStaticString(ptr, len, RubyEncoding.UTF8);
        }

        internal static VALUE rb_utf8_str_new(byte* ptr, long len) {
            return ToValue(NewString(ptr, len, RubyEncoding.UTF8));
        }

        internal static VALUE rb_utf8_str_new_cstr(byte* ptr) {
            return ToValue(MutableString.CreateBinary(CBytes(ptr), RubyEncoding.UTF8));
        }

        internal static VALUE rb_enc_str_new(byte* ptr, long len, void* enc) {
            return ToValue(NewString(ptr, len, FromNativeEncoding(enc) ?? RubyEncoding.Binary));
        }

        internal static VALUE rb_enc_str_new_cstr(byte* ptr, void* enc) {
            return ToValue(MutableString.CreateBinary(CBytes(ptr), FromNativeEncoding(enc) ?? RubyEncoding.Binary));
        }

        /// <summary>
        /// CRuby's rb_external_str_new_with_enc: bytes from outside in their encoding, BINARY when
        /// that is US-ASCII and they are not, and converted to Encoding.default_internal if one is set.
        /// </summary>
        private static VALUE ExternalString(byte[]/*!*/ bytes, RubyEncoding/*!*/ encoding) {
            var s = MutableString.CreateBinary(bytes, encoding);
            if (encoding == RubyEncoding.Ascii && !s.IsAscii()) {
                s.ForceEncoding(RubyEncoding.Binary);
                return ToValue(s);
            }
            var internalEncoding = _context.DefaultInternalEncoding;
            if (internalEncoding == null || encoding == RubyEncoding.Binary) {
                return ToValue(s);
            }
            return rb_str_conv_enc(ToValue(s), NativeEncoding(encoding), NativeEncoding(internalEncoding));
        }

        internal static VALUE rb_external_str_new(byte* ptr, long len) {
            return ExternalString(Bytes(ptr, len), _context.DefaultExternalEncoding);
        }

        internal static VALUE rb_external_str_new_cstr(byte* ptr) {
            return ExternalString(CBytes(ptr), _context.DefaultExternalEncoding);
        }

        internal static VALUE rb_external_str_new_with_enc(byte* ptr, long len, void* enc) {
            return ExternalString(Bytes(ptr, len), FromNativeEncoding(enc) ?? RubyEncoding.Binary);
        }

        // Encoding.find("locale"): not the default external one on Windows (a code page there)
        internal static RubyEncoding/*!*/ LocaleEncoding() {
            return (RubyEncoding)Send(ResolveGlobal(_context, "Encoding"), "find", null, MutableString.CreateAscii("locale"));
        }

        internal static VALUE rb_locale_str_new(byte* ptr, long len) {
            return ExternalString(Bytes(ptr, len), LocaleEncoding());
        }

        internal static VALUE rb_locale_str_new_cstr(byte* ptr) {
            return ExternalString(CBytes(ptr), LocaleEncoding());
        }

        internal static VALUE rb_ir_str_new_buffer(byte* ptr, long len, int encindex) {
            return ToValue(NewString(ptr, len, encindex >= 0 ? EncodingAt(encindex) : RubyEncoding.Binary));
        }

        internal static VALUE rb_str_new_with_class(VALUE obj, byte* ptr, long len) {
            var cls = _context.GetClassOf(FromValue(obj)).GetNonSingletonClass();
            var s = NewString(ptr, len, RubyEncoding.Binary);
            if (cls == _context.GetClass(typeof(MutableString))) {
                return ToValue(s);
            }
            object result = Send(cls, "allocate", null);
            Send(result, "replace", null, s);
            return ToValue(result);
        }

        internal static VALUE rb_str_buf_new(long capa) {
            var s = MutableString.CreateBinary((int)Math.Min(capa, Int32.MaxValue), RubyEncoding.Binary);
            GetStringMirror(s).EnsureCapacity((int)Math.Max(capa, 63));
            return ToValue(s);
        }

        internal static VALUE rb_str_tmp_new(long len) {
            return rb_obj_hide(ToValue(NewString(null, len, RubyEncoding.Binary)));
        }

        internal static VALUE rb_str_new_frozen(VALUE str) {
            var s = ToMutableString(str);
            return s.IsFrozen ? str : ToValue(s.Clone().Freeze());
        }

        internal static VALUE rb_str_new_shared(VALUE str) {
            return ToValue(ToMutableString(str).Clone());
        }

        internal static VALUE rb_str_dup(VALUE str) {
            return ToValue(Send(FromValue(str), "dup", null));
        }

        // CRuby: US-ASCII, or BINARY when a byte has the 8th bit set
        private static VALUE InternedString(byte[]/*!*/ bytes) {
            var s = MutableString.CreateBinary(bytes, Array.Exists(bytes, b => b >= 0x80) ? RubyEncoding.Binary : RubyEncoding.Ascii);
            return ToValue(Send(s, "-@", null));
        }

        internal static VALUE rb_interned_str(byte* ptr, long len) {
            return InternedString(Bytes(ptr, len));
        }

        internal static VALUE rb_interned_str_cstr(byte* ptr) {
            return InternedString(CBytes(ptr));
        }

        internal static VALUE rb_enc_interned_str(byte* ptr, long len, void* enc) {
            return ToValue(Send(NewString(ptr, len, FromNativeEncoding(enc) ?? RubyEncoding.Binary), "-@", null));
        }

        internal static VALUE rb_enc_interned_str_cstr(byte* ptr, void* enc) {
            return ToValue(Send(MutableString.CreateBinary(CBytes(ptr), FromNativeEncoding(enc) ?? RubyEncoding.Binary), "-@", null));
        }

        internal static VALUE rb_str_to_interned_str(VALUE str) {
            return ToValue(Send(FromValue(str), "-@", null));
        }

        #endregion

        #region Conversions

        internal static VALUE rb_str_to_str(VALUE obj) {
            object o = FromValue(obj);
            if (o is MutableString) {
                return obj;
            }
            return ToValue(ConvertToString(o));
        }

        private static MutableString/*!*/ ConvertToString(object o) {
            if (o is MutableString s) {
                return s;
            }
            if (o != null && !(o is bool) && Protocols.IsTrue(Send(o, "respond_to?", null, _context.CreateAsciiSymbol("to_str"), true))) {
                object r = Send(o, "to_str", null);
                if (r is MutableString rs) {
                    return rs;
                }
                throw RubyExceptions.CreateTypeError(String.Format("can't convert {0} to String ({0}#to_str gives {1})", ClassName(o), ClassName(r)));
            }
            throw RubyExceptions.CreateTypeError(String.Format("no implicit conversion of {0} into String", ClassName(o)));
        }

        internal static VALUE rb_string_value(VALUE* ptr) {
            object o = FromValue(*ptr);
            if (!(o is MutableString)) {
                *ptr = ToValue(ConvertToString(o));
            }
            return *ptr;
        }

        internal static byte* rb_string_value_cstr(VALUE* ptr) {
            var s = ToMutableString(rb_string_value(ptr));
            // a NUL character, which in UTF-16 or UTF-32 is an aligned run of zero bytes
            byte[] bytes = s.ToByteArray();
            int unit = MinCharLength(s.Encoding);
            for (int i = 0; i + unit <= bytes.Length; i += unit) {
                bool nul = true;
                for (int j = 0; j < unit; j++) {
                    nul &= bytes[i + j] == 0;
                }
                if (nul) {
                    throw RubyExceptions.CreateArgumentError("string contains null byte");
                }
            }
            return GetStringMirror(s).Ptr;
        }

        internal static VALUE rb_check_string_type(VALUE obj) {
            object o = FromValue(obj);
            if (o is MutableString) {
                return obj;
            }
            if (o != null && !(o is bool) && Protocols.IsTrue(Send(o, "respond_to?", null, _context.CreateAsciiSymbol("to_str"), true))) {
                object r = Send(o, "to_str", null);
                if (r == null) {
                    return Qnil;
                }
                if (r is MutableString) {
                    return ToValue(r);
                }
                throw RubyExceptions.CreateTypeError(String.Format("can't convert {0} to String ({0}#to_str gives {1})", ClassName(o), ClassName(r)));
            }
            return Qnil;
        }

        internal static VALUE rb_String(VALUE val) {
            return ToValue(Send(_context.KernelModule, "String", null, FromValue(val)));
        }

        #endregion

        #region Operations

        internal static long rb_str_strlen(VALUE str) {
            return ToMutableString(str).GetCharCount();
        }

        internal static VALUE rb_str_length(VALUE str) {
            return ToValue(ToMutableString(str).GetCharCount());
        }

        internal static long rb_str_sublen(VALUE str, long pos) {
            var s = ToMutableString(str);
            object prefix = Send(s, "byteslice", null, 0, (int)pos);
            return prefix is MutableString p ? p.GetCharCount() : 0;
        }

        internal static byte* rb_str_subpos(VALUE str, long beg, System.Runtime.InteropServices.CLong* lenp) {
            long l = (long)lenp->Value;
            byte* r = StrSubpos(str, beg, &l);
            *lenp = new System.Runtime.InteropServices.CLong((nint)l);
            return r;
        }

        private static byte* StrSubpos(VALUE str, long beg, long* len) {
            var s = ToMutableString(str);
            int length = s.GetCharCount();
            if (*len < 0) {
                return null;
            }
            if (beg < 0) {
                beg += length;
                if (beg < 0) {
                    return null;
                }
            }
            if (beg > length) {
                return null;
            }
            var sub = Send(s, "[]", null, (int)beg, (int)Math.Min(*len, Int32.MaxValue)) as MutableString;
            if (sub == null) {
                return null;
            }
            var prefix = (MutableString)Send(s, "[]", null, 0, (int)beg);
            *len = sub.GetByteCount();
            return GetStringMirror(s).Ptr + prefix.GetByteCount();
        }

        internal static nuint rb_str_capacity(VALUE str) {
            var s = ToMutableString(str);
            StringMirror mirror;
            if (_stringMirrors.TryGetValue(s, out mirror) && mirror.Ptr != null) {
                return (nuint)Math.Max(mirror.Capacity, s.GetByteCount());
            }
            return (nuint)s.GetByteCount();
        }

        internal static ulong rb_str_hash(VALUE str) {
            object h = Send(FromValue(str), "hash", null);
            return h is int i ? (ulong)(long)i : h is long l ? (ulong)l : (ulong)((System.Numerics.BigInteger)h & ulong.MaxValue);
        }

        internal static int rb_str_cmp(VALUE lhs, VALUE rhs) {
            object r = Send(FromValue(lhs), "<=>", null, FromValue(rhs));
            return r is int i ? i : 0;
        }

        internal static VALUE rb_str_equal(VALUE str1, VALUE str2) {
            if (str1 == str2) {
                return Qtrue;
            }
            return Bool(Protocols.IsTrue(Send(FromValue(str1), "==", null, FromValue(str2))));
        }

        internal static VALUE rb_str_freeze(VALUE str) {
            return rb_obj_freeze(str);
        }

        internal static VALUE rb_str_inspect(VALUE str) {
            return rb_inspect(str);
        }

        internal static VALUE rb_str_intern(VALUE str) {
            return ToValue(_context.CreateSymbol(ToMutableString(str)));
        }

        internal static VALUE rb_str_plus(VALUE lhs, VALUE rhs) {
            return ToValue(Send(FromValue(lhs), "+", null, FromValue(rhs)));
        }

        internal static VALUE rb_str_times(VALUE str, VALUE num) {
            return ToValue(Send(FromValue(str), "*", null, FromValue(num)));
        }

        internal static VALUE rb_str_split(VALUE str, byte* delim) {
            return ToValue(Send(FromValue(str), "split", null, MutableString.CreateBinary(CBytes(delim), RubyEncoding.UTF8)));
        }

        internal static VALUE rb_str_subseq(VALUE str, long beg, long len) {
            return ToValue(Send(FromValue(str), "byteslice", null, (int)beg, (int)len));
        }

        internal static VALUE rb_str_substr(VALUE str, long beg, long len) {
            return ToValue(Send(FromValue(str), "[]", null, (int)beg, (int)len));
        }

        internal static VALUE rb_str_append(VALUE dst, VALUE src) {
            var s = ConvertToString(FromValue(src));
            Send(ToMutableString(dst), "<<", null, s);
            return dst;
        }

        internal static VALUE rb_str_buf_append(VALUE dst, VALUE src) {
            return rb_str_append(dst, src);
        }

        internal static VALUE rb_str_cat(VALUE dst, byte* src, long srclen) {
            var s = ToMutableString(dst);
            s.RequireNotFrozen();
            if (srclen < 0) {
                throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
            }
            s.Append(Bytes(src, srclen));
            return dst;
        }

        internal static VALUE rb_str_cat_cstr(VALUE dst, byte* src) {
            var s = ToMutableString(dst);
            s.RequireNotFrozen();
            s.Append(CBytes(src));
            return dst;
        }

        internal static VALUE rb_enc_str_buf_cat(VALUE str, byte* ptr, long len, void* enc) {
            Send(ToMutableString(str), "<<", null, NewString(ptr, len, FromNativeEncoding(enc) ?? RubyEncoding.Binary));
            return str;
        }

        internal static VALUE rb_str_drop_bytes(VALUE str, long len) {
            var s = ToMutableString(str);
            s.RequireNotFrozen();
            int n = (int)Math.Min(len, s.GetByteCount());
            byte[] rest = s.GetBinarySlice(n);
            s.Remove(0);
            s.Append(rest);
            return str;
        }

        internal static void rb_str_update(VALUE dst, long beg, long len, VALUE src) {
            Send(ToMutableString(dst), "[]=", null, (int)beg, (int)len, FromValue(src));
        }

        internal static void rb_str_modify(VALUE str) {
            ToMutableString(str).RequireNotFrozen();
        }

        internal static void rb_str_modify_expand(VALUE str, long expand) {
            var s = ToMutableString(str);
            s.RequireNotFrozen();
            if (expand < 0) {
                throw RubyExceptions.CreateArgumentError("negative expanding string size");
            }
            GetStringMirror(s).EnsureCapacity(checked(s.GetByteCount() + (int)expand));
        }

        internal static void rb_str_set_len(VALUE str, long len) {
            var s = ToMutableString(str);
            s.RequireNotFrozen();
            var mirror = GetStringMirror(s);
            if (len < 0 || len > mirror.Capacity) {
                throw RubyExceptions.CreateArgumentError(String.Format("probable buffer overflow: {0} for {1}", len, mirror.Capacity));
            }
            mirror.SetLength((int)len);
        }

        internal static VALUE rb_str_resize(VALUE str, long len) {
            var s = ToMutableString(str);
            if (len < 0) {
                throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
            }
            if (len != s.GetByteCount()) {
                s.RequireNotFrozen();
                // the bytes past the length C may have written are kept, as CRuby keeps them
                var mirror = GetStringMirror(s);
                mirror.SetLength((int)len);
            }
            return str;
        }

        internal static VALUE rb_str_locktmp(VALUE str) {
            var s = ToMutableString(str);
            if (s.IsTemporarilyLocked) {
                throw RubyExceptions.CreateRuntimeError("temporal locking already locked string");
            }
            s.LockTemporarily();
            return str;
        }

        internal static VALUE rb_str_unlocktmp(VALUE str) {
            var s = ToMutableString(str);
            if (s.IsFrozen) {
                throw RubyExceptions.CreateObjectFrozenError(_context, s);
            }
            if (!s.IsTemporarilyLocked) {
                throw RubyExceptions.CreateRuntimeError("temporal unlocking already unlocked string");
            }
            s.UnlockTemporarily();
            return str;
        }

        internal static void rb_str_free(VALUE str) {
        }

        #endregion

        #region Encoding conversion

        internal static VALUE rb_str_conv_enc_opts(VALUE str, void* from, void* to, int ecflags, VALUE ecopts) {
            var s = ToMutableString(str);
            var toEncoding = FromNativeEncoding(to);
            var fromEncoding = FromNativeEncoding(from) ?? s.Encoding;
            if (toEncoding == null || toEncoding == fromEncoding) {
                return str;
            }
            if (s.Encoding == toEncoding && s.IsAscii()) {
                return str;
            }
            if (toEncoding == RubyEncoding.Binary) {
                return str;
            }
            if (s.IsAscii() && (toEncoding == RubyEncoding.UTF8 || toEncoding == RubyEncoding.Ascii) && fromEncoding.IsAsciiIdentity) {
                var copy = s.Clone();
                copy.ForceEncoding(toEncoding);
                return ToValue(copy);
            }
            try {
                return ToValue(Send(s, "encode", null, toEncoding, fromEncoding));
            } catch (Exception) {
                // CRuby: an unconvertible string comes back unchanged
                return str;
            }
        }

        internal static VALUE rb_str_conv_enc(VALUE str, void* from, void* to) {
            return rb_str_conv_enc_opts(str, from, to, 0, Qnil);
        }

        internal static VALUE rb_str_encode(VALUE str, VALUE to, int ecflags, VALUE ecopts) {
            // the ECONV_* flags as String#encode's options (ruby/internal/encoding/transcode.h)
            object opts = FromValue(ecopts);
            var hash = (IDictionary<object, object>)Send(opts != null ? Send(opts, "dup", null) : HashClass(), opts != null ? "itself" : "new", null);
            var replace = _context.CreateAsciiSymbol("replace");
            if ((ecflags & 0x0f) == 0x02) hash[_context.CreateAsciiSymbol("invalid")] = replace;
            if ((ecflags & 0xf0) == 0x20) hash[_context.CreateAsciiSymbol("undef")] = replace;
            if ((ecflags & 0xf0) == 0x30) hash[_context.CreateAsciiSymbol("fallback")] = null;
            if ((ecflags & 0x100) != 0) hash[_context.CreateAsciiSymbol("universal_newline")] = true;
            if ((ecflags & 0x1000) != 0) hash[_context.CreateAsciiSymbol("crlf_newline")] = true;
            if ((ecflags & 0x2000) != 0) hash[_context.CreateAsciiSymbol("cr_newline")] = true;
            if ((ecflags & 0x8000) != 0) hash[_context.CreateAsciiSymbol("xml")] = _context.CreateAsciiSymbol("text");
            if ((ecflags & 0x10000) != 0) hash[_context.CreateAsciiSymbol("xml")] = _context.CreateAsciiSymbol("attr");
            return hash.Count > 0 ?
                ToValue(Send(FromValue(str), "encode", null, FromValue(to), hash)) :
                ToValue(Send(FromValue(str), "encode", null, FromValue(to)));
        }

        internal static VALUE rb_str_export_to_enc(VALUE obj, void* enc) {
            return rb_str_conv_enc(rb_str_to_str(obj), null, enc);
        }

        internal static VALUE rb_str_export(VALUE obj) {
            return rb_str_conv_enc(rb_str_to_str(obj), null, NativeEncoding(_context.DefaultExternalEncoding));
        }

        internal static VALUE rb_str_export_locale(VALUE obj) {
            return rb_str_conv_enc(rb_str_to_str(obj), null, NativeEncoding(LocaleEncoding()));
        }

        #endregion
    }
}
