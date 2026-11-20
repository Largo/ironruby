/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// Arrays and hashes.
    /// </summary>
    internal static unsafe partial class CApi {

        #region Arrays

        internal static VALUE rb_ary_new() {
            return ToValue(new RubyArray());
        }

        internal static VALUE rb_ary_new_capa(long capa) {
            if (capa < 0) {
                throw RubyExceptions.CreateArgumentError("negative array size (or size too big)");
            }
            return ToValue(new RubyArray((int)Math.Min(capa, 1 << 20)));
        }

        internal static VALUE rb_ary_hidden_new(long capa) {
            return rb_ary_new_capa(capa);
        }

        internal static VALUE rb_ary_new_from_values(long n, VALUE* elts) {
            var array = new RubyArray((int)n);
            for (long i = 0; i < n; i++) {
                array.Add(FromValue(elts[i]));
            }
            return ToValue(array);
        }

        internal static VALUE rb_assoc_new(VALUE car, VALUE cdr) {
            return ToValue(new RubyArray { FromValue(car), FromValue(cdr) });
        }

        internal static VALUE rb_ary_entry(VALUE ary, long off) {
            var array = ToRubyArray(ary);
            if (off < 0) {
                off += array.Count;
            }
            return (off >= 0 && off < array.Count) ? ToValue(array[(int)off]) : Qnil;
        }

        internal static void rb_ary_store(VALUE ary, long idx, VALUE val) {
            var array = ToRubyArray(ary);
            array.RequireNotFrozen();
            long len = array.Count;
            if (idx < 0) {
                idx += len;
                if (idx < 0) {
                    throw RubyExceptions.CreateIndexError(String.Format("index {0} too small for array; minimum: -{1}", idx - len, len));
                }
            }
            object value = FromValue(val);
            if (idx < len) {
                array[(int)idx] = value;
            } else {
                while (array.Count < idx) {
                    array.Add(null);
                }
                array.Add(value);
            }
        }

        internal static VALUE rb_ary_push(VALUE ary, VALUE elem) {
            var array = ToRubyArray(ary);
            array.RequireNotFrozen();
            array.Add(FromValue(elem));
            return ary;
        }

        internal static VALUE rb_ary_cat(VALUE ary, VALUE* train, long len) {
            var array = ToRubyArray(ary);
            array.RequireNotFrozen();
            for (long i = 0; i < len; i++) {
                array.Add(FromValue(train[i]));
            }
            return ary;
        }

        internal static VALUE rb_ary_aref(int argc, VALUE* argv, VALUE ary) {
            return ToValue(Send(ToRubyArray(ary), "[]", null, FromValues(argc, argv)));
        }

        private static VALUE AryCall(VALUE ary, string/*!*/ name, params object[] args) {
            return ToValue(Send(ToRubyArray(ary), name, null, args));
        }

        internal static VALUE rb_ary_clear(VALUE ary) { AryCall(ary, "clear"); return ary; }
        internal static VALUE rb_ary_concat(VALUE lhs, VALUE rhs) { return AryCall(lhs, "concat", FromValue(rhs)); }
        internal static VALUE rb_ary_delete(VALUE ary, VALUE elem) { return AryCall(ary, "delete", FromValue(elem)); }
        internal static VALUE rb_ary_delete_at(VALUE ary, long pos) { return AryCall(ary, "delete_at", (int)pos); }
        internal static VALUE rb_ary_freeze(VALUE obj) { return rb_obj_freeze(obj); }
        internal static VALUE rb_ary_includes(VALUE ary, VALUE elem) { return AryCall(ary, "include?", FromValue(elem)); }
        internal static VALUE rb_ary_join(VALUE ary, VALUE sep) { return AryCall(ary, "join", FromValue(sep)); }
        internal static VALUE rb_ary_plus(VALUE lhs, VALUE rhs) { return AryCall(lhs, "+", FromValue(rhs)); }
        internal static VALUE rb_ary_pop(VALUE ary) { return AryCall(ary, "pop"); }
        internal static VALUE rb_ary_shift(VALUE ary) { return AryCall(ary, "shift"); }
        internal static VALUE rb_ary_unshift(VALUE ary, VALUE elem) { return AryCall(ary, "unshift", FromValue(elem)); }
        internal static VALUE rb_ary_reverse(VALUE ary) { return AryCall(ary, "reverse!"); }
        internal static VALUE rb_ary_rotate(VALUE ary, long rot) { return AryCall(ary, "rotate!", (int)rot); }
        internal static VALUE rb_ary_sort(VALUE ary) { return AryCall(ary, "sort"); }
        internal static VALUE rb_ary_sort_bang(VALUE ary) { return AryCall(ary, "sort!"); }
        internal static VALUE rb_ary_to_s(VALUE ary) { return AryCall(ary, "to_s"); }

        internal static VALUE rb_ary_subseq(VALUE ary, long beg, long len) {
            return AryCall(ary, "[]", (int)beg, (int)Math.Min(len, Int32.MaxValue));
        }

        internal static VALUE rb_ary_dup(VALUE ary) {
            return ToValue(new RubyArray(ToRubyArray(ary)));
        }

        internal static VALUE rb_ary_to_ary(VALUE obj) {
            object o = FromValue(obj);
            if (o is RubyArray) {
                return obj;
            }
            if (o != null && !(o is bool) && Protocols.IsTrue(Send(o, "respond_to?", null, _context.CreateAsciiSymbol("to_ary"), true))) {
                return ToValue(Send(o, "to_ary", null));
            }
            return ToValue(new RubyArray { o });
        }

        internal static VALUE rb_Array(VALUE val) {
            return ToValue(Send(_context.KernelModule, "Array", null, FromValue(val)));
        }

        // object.c's: nil and [] are {}, anything else has to convert with #to_hash
        internal static VALUE rb_Hash(VALUE val) {
            object o = FromValue(val);
            if (o == null || o is RubyArray array && array.Count == 0) {
                return rb_hash_new();
            }
            return ToValue(ConvertType(o, T_HASH, "Hash", "to_hash", true));
        }

        #endregion

        #region Hashes

        private static object HashClass() {
            object cls;
            _context.ObjectClass.TryGetConstant(null, "Hash", out cls);
            return cls;
        }

        internal static VALUE rb_hash_new() {
            return ToValue(Send(HashClass(), "new", null));
        }

        internal static VALUE rb_hash_aref(VALUE hash, VALUE key) {
            return ToValue(Send(FromValue(hash), "[]", null, FromValue(key)));
        }

        internal static VALUE rb_hash_aset(VALUE hash, VALUE key, VALUE val) {
            Send(FromValue(hash), "[]=", null, FromValue(key), FromValue(val));
            return val;
        }

        internal static VALUE rb_hash_lookup2(VALUE hash, VALUE key, VALUE def) {
            var h = FromValue(hash) as IDictionary<object, object>;
            if (h == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type (expected Hash)");
            }
            object value;
            if (Protocols.IsTrue(Send(h, "key?", null, FromValue(key)))) {
                return ToValue(Send(h, "fetch", null, FromValue(key)));
            }
            return def;
        }

        internal static VALUE rb_hash_lookup(VALUE hash, VALUE key) {
            return rb_hash_lookup2(hash, key, Qnil);
        }

        internal static VALUE rb_hash_size(VALUE hash) { return ToValue(Send(FromValue(hash), "size", null)); }
        internal static VALUE rb_hash_dup(VALUE hash) { return ToValue(Send(FromValue(hash), "dup", null)); }
        internal static VALUE rb_hash_delete(VALUE hash, VALUE key) { return ToValue(Send(FromValue(hash), "delete", null, FromValue(key))); }
        internal static VALUE rb_hash_clear(VALUE hash) { Send(FromValue(hash), "clear", null); return hash; }

        // st_retval
        private const int ST_CONTINUE = 0, ST_STOP = 1, ST_DELETE = 2, ST_CHECK = 3;

        internal static void rb_hash_foreach(VALUE hash, IntPtr func, VALUE arg) {
            object h = FromValue(hash);
            var pairs = (RubyArray)Send(h, "to_a", null);
            foreach (RubyArray pair in pairs) {
                int r = CallForeach(func, ToValue(pair[0]), ToValue(pair[1]), arg);
                if (r == ST_STOP) {
                    break;
                }
                if (r == ST_DELETE) {
                    Send(h, "delete", null, pair[0]);
                }
            }
        }

        #endregion
    }
}
