/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// Objects, classes and modules, constants, variables, exceptions and the GC's API.
    /// </summary>
    internal static unsafe partial class CApi {

        #region Helpers

        internal static string/*!*/ Inspect(object obj) {
            return _context.Inspect(obj).ToString();
        }

        internal static string/*!*/ ClassName(object obj) {
            switch (obj) {
                case null: return "nil";
                case bool b: return b ? "true" : "false";
            }
            return _context.GetClassOf(obj).GetNonSingletonClass().Name;
        }

        // C strings IronRuby hands out (rb_id2name, rb_obj_classname...) live as long as the process
        private static readonly ConcurrentDictionary<string, IntPtr> _cStrings = new ConcurrentDictionary<string, IntPtr>();

        internal static byte* PermanentCString(string/*!*/ str) {
            return (byte*)_cStrings.GetOrAdd(str, s => {
                byte[] bytes = CString(s);
                IntPtr p = (IntPtr)NativeMemory.Alloc((nuint)bytes.Length);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                return p;
            });
        }

        #endregion

        #region Classes and modules

        private static object GetOwnConstant(RubyModule/*!*/ owner, string/*!*/ name, out bool found) {
            object value;
            found = owner.TryGetConstant(null, name, out value);
            return value;
        }

        private static RubyClass/*!*/ DefineClassUnder(RubyModule/*!*/ owner, string/*!*/ name, VALUE super) {
            object superObject = FromValue(super);
            RubyClass superClass = RubyClass.CheckInheritable(_context, superObject);
            bool found;
            object existing = GetOwnConstant(owner, name, out found);
            if (found) {
                var cls = existing as RubyClass;
                if (cls == null || !cls.IsClass) {
                    throw RubyExceptions.CreateTypeError(String.Format("{0} is not a class ({1})", name, ClassName(existing)));
                }
                if (!ReferenceEquals(cls.SuperClass, superClass)) {
                    throw RubyExceptions.CreateTypeError(String.Format("superclass mismatch for class {0}", name));
                }
                return cls;
            }
            return _context.DefineClass(owner, name, superClass, null);
        }

        private static RubyModule/*!*/ DefineModuleUnder(RubyModule/*!*/ owner, string/*!*/ name) {
            bool found;
            object existing = GetOwnConstant(owner, name, out found);
            if (found) {
                var module = existing as RubyModule;
                if (module == null || module.IsClass) {
                    throw RubyExceptions.CreateTypeError(String.Format("{0} is not a module ({1})", name, ClassName(existing)));
                }
                return module;
            }
            return _context.DefineModule(owner, name);
        }

        internal static VALUE rb_define_class(byte* name, VALUE super) {
            return ToValue(DefineClassUnder(_context.ObjectClass, FromCString(name), super));
        }

        internal static VALUE rb_define_class_under(VALUE outer, byte* name, VALUE super) {
            return ToValue(DefineClassUnder(ToModule(outer), FromCString(name), super));
        }

        internal static VALUE rb_define_module(byte* name) {
            return ToValue(DefineModuleUnder(_context.ObjectClass, FromCString(name)));
        }

        internal static VALUE rb_define_module_under(VALUE outer, byte* name) {
            return ToValue(DefineModuleUnder(ToModule(outer), FromCString(name)));
        }

        internal static VALUE rb_obj_class(VALUE obj) {
            return ToValue(_context.GetClassOf(FromValue(obj)).GetNonSingletonClass());
        }

        internal static VALUE rb_singleton_class(VALUE obj) {
            return ToValue(_context.GetOrCreateSingletonClass(FromValue(obj)));
        }

        internal static VALUE rb_class_superclass(VALUE klass) {
            return ToValue(Send(FromValue(klass), "superclass", null));
        }

        internal static VALUE rb_class_get_superclass(VALUE klass) {
            var cls = FromValue(klass) as RubyClass;
            return cls != null ? ToValue(cls.SuperClass) : Qnil;
        }

        internal static VALUE rb_class_inherited_p(VALUE scion, VALUE ascendant) {
            return ToValue(Send(FromValue(scion), "<=", null, FromValue(ascendant)));
        }

        internal static VALUE rb_mod_ancestors(VALUE mod) {
            return ToValue(Send(FromValue(mod), "ancestors", null));
        }

        internal static VALUE rb_mod_name(VALUE mod) {
            return ToValue(Send(FromValue(mod), "name", null));
        }

        internal static VALUE rb_class_path(VALUE mod) {
            object name = Send(FromValue(mod), "name", null);
            return name != null ? ToValue(name) : ToValue(Send(FromValue(mod), "inspect", null));
        }

        internal static VALUE rb_class_name(VALUE obj) {
            return rb_class_path(obj);
        }

        internal static byte* rb_obj_classname(VALUE obj) {
            return PermanentCString(ClassName(FromValue(obj)));
        }

        internal static VALUE rb_path2class(byte* path) {
            string p = FromCString(path);
            object current = _context.ObjectClass;
            foreach (var name in p.Split(new[] { "::" }, StringSplitOptions.None)) {
                var module = current as RubyModule;
                if (module == null || !module.TryGetConstant(null, name, out current)) {
                    throw RubyExceptions.CreateArgumentError(String.Format("undefined class/module {0}", p));
                }
            }
            if (!(current is RubyModule)) {
                throw RubyExceptions.CreateTypeError(String.Format("{0} does not refer to class/module", p));
            }
            return ToValue(current);
        }

        internal static void rb_include_module(VALUE klass, VALUE module) {
            Send(FromValue(klass), "include", null, FromValue(module));
        }

        internal static void rb_extend_object(VALUE obj, VALUE mod) {
            Send(FromValue(mod), "extend_object", null, FromValue(obj));
        }

        internal static VALUE rb_obj_is_kind_of(VALUE obj, VALUE klass) {
            return Bool(_context.IsKindOf(FromValue(obj), ToModule(klass)));
        }

        internal static VALUE rb_obj_is_instance_of(VALUE obj, VALUE klass) {
            return Bool(ReferenceEquals(_context.GetClassOf(FromValue(obj)).GetNonSingletonClass(), FromValue(klass)));
        }

        #endregion

        #region Constants and variables

        internal static VALUE rb_const_get(VALUE space, VALUE name) {
            return ToValue(Send(FromValue(space), "const_get", null, IdToSymbol(name)));
        }

        internal static VALUE rb_const_get_at(VALUE space, VALUE name) {
            return ToValue(Send(FromValue(space), "const_get", null, IdToSymbol(name), false));
        }

        internal static int rb_const_defined(VALUE space, VALUE name) {
            return Protocols.IsTrue(Send(FromValue(space), "const_defined?", null, IdToSymbol(name))) ? 1 : 0;
        }

        internal static void rb_const_set(VALUE space, VALUE name, VALUE val) {
            ToModule(space).SetConstant(IdToString(name), FromValue(val));
        }

        internal static void rb_define_const(VALUE klass, byte* name, VALUE val) {
            string n = FromCString(name);
            if (n.Length == 0 || !Char.IsUpper(n[0])) {
                _context.ReportWarning(String.Format("rb_define_const: invalid name '{0}' for constant", n), false);
            }
            ToModule(klass).SetConstant(n, FromValue(val));
        }

        internal static VALUE rb_ivar_get(VALUE obj, VALUE name) {
            object value;
            return _context.TryGetInstanceVariable(FromValue(obj), IdToString(name), out value) ? ToValue(value) : Qnil;
        }

        internal static VALUE rb_ivar_set(VALUE obj, VALUE name, VALUE val) {
            _context.SetInstanceVariable(FromValue(obj), IdToString(name), FromValue(val));
            return val;
        }

        internal static VALUE rb_ivar_defined(VALUE obj, VALUE name) {
            object value;
            return Bool(_context.TryGetInstanceVariable(FromValue(obj), IdToString(name), out value));
        }

        internal static VALUE rb_iv_get(VALUE obj, byte* name) {
            return rb_ivar_get(obj, Intern(FromCString(name)));
        }

        internal static VALUE rb_iv_set(VALUE obj, byte* name, VALUE val) {
            return rb_ivar_set(obj, Intern(FromCString(name)), val);
        }

        internal static VALUE rb_attr_get(VALUE obj, VALUE name) {
            return rb_ivar_get(obj, Intern("@" + IdToString(name)));
        }

        private static string/*!*/ GlobalName(byte* name) {
            string n = FromCString(name);
            return n.StartsWith("$", StringComparison.Ordinal) ? n.Substring(1) : n;
        }

        internal static VALUE rb_gv_get(byte* name) {
            object value;
            return _context.TryGetGlobalVariable(null, GlobalName(name), out value) ? ToValue(value) : Qnil;
        }

        internal static VALUE rb_gv_set(byte* name, VALUE val) {
            _context.SetGlobalVariable(null, GlobalName(name), FromValue(val));
            return val;
        }

        #endregion

        #region Objects

        internal static VALUE rb_inspect(VALUE obj) {
            object result = Send(FromValue(obj), "inspect", null);
            return ToValue(result as MutableString ?? _context.Inspect(result));
        }

        internal static VALUE rb_obj_as_string(VALUE obj) {
            object o = FromValue(obj);
            if (o is MutableString) {
                return obj;
            }
            object s = Send(o, "to_s", null);
            return s is MutableString ? ToValue(s) : rb_any_to_s(obj);
        }

        internal static VALUE rb_any_to_s(VALUE obj) {
            object o = FromValue(obj);
            return ToValue(RubyUtils.ObjectToMutableString(_context, o));
        }

        internal static int rb_respond_to(VALUE obj, VALUE mid) {
            return Protocols.IsTrue(Send(FromValue(obj), "respond_to?", null, IdToSymbol(mid))) ? 1 : 0;
        }

        internal static VALUE rb_equal(VALUE lhs, VALUE rhs) {
            if (lhs == rhs) {
                return Qtrue;
            }
            return Bool(Protocols.IsTrue(Send(FromValue(lhs), "==", null, FromValue(rhs))));
        }

        internal static int rb_eql(VALUE lhs, VALUE rhs) {
            if (lhs == rhs) {
                return 1;
            }
            return Protocols.IsTrue(Send(FromValue(lhs), "eql?", null, FromValue(rhs))) ? 1 : 0;
        }

        internal static VALUE rb_obj_freeze(VALUE obj) {
            _context.FreezeObject(FromValue(obj));
            return obj;
        }

        internal static VALUE rb_obj_frozen_p(VALUE obj) {
            return Bool(_context.IsObjectFrozen(FromValue(obj)));
        }

        internal static VALUE rb_obj_id(VALUE obj) {
            return ToValue(Send(FromValue(obj), "object_id", null));
        }

        internal static VALUE rb_obj_dup(VALUE obj) {
            return ToValue(Send(FromValue(obj), "dup", null));
        }

        internal static VALUE rb_obj_clone(VALUE obj) {
            return ToValue(Send(FromValue(obj), "clone", null));
        }

        // CRuby hides internal objects from ObjectSpace by clearing their class; IronRuby's are not
        // in ObjectSpace's way to begin with.
        internal static VALUE rb_obj_hide(VALUE obj) {
            object o = FromValue(obj);
            if (o != null && !(o is bool) && !(o is int)) {
                _hidden.AddOrUpdate(o, null);
            }
            return obj;
        }

        internal static VALUE rb_obj_reveal(VALUE obj, VALUE klass) {
            object o = FromValue(obj);
            if (o != null) {
                _hidden.Remove(o);
            }
            return obj;
        }

        internal static VALUE rb_mutex_new() {
            object mutex;
            _context.ObjectClass.TryGetConstant(null, "Mutex", out mutex);
            return ToValue(Send(mutex, "new", null));
        }

        internal static VALUE rb_thread_current() {
            return ToValue(Send(_context.GetClass(typeof(System.Threading.Thread)), "current", null));
        }

        internal static void rb_check_type(VALUE obj, int t) {
            int actual = BuiltinType(obj);
            if (actual != t && !(t == T_DATA && actual == T_DATA)) {
                rb_unexpected_type(obj, t);
            }
        }

        private static readonly Dictionary<int, string> _typeNames = new Dictionary<int, string> {
            { T_OBJECT, "Object" }, { T_CLASS, "Class" }, { T_MODULE, "Module" }, { T_FLOAT, "Float" },
            { T_STRING, "String" }, { T_REGEXP, "Regexp" }, { T_ARRAY, "Array" }, { T_HASH, "Hash" },
            { T_STRUCT, "Struct" }, { T_BIGNUM, "Integer" }, { T_FILE, "File" }, { T_DATA, "Data" },
            { T_MATCH, "MatchData" }, { T_COMPLEX, "Complex" }, { T_RATIONAL, "Rational" }, { T_NIL, "nil" },
            { T_TRUE, "true" }, { T_FALSE, "false" }, { T_SYMBOL, "Symbol" }, { T_FIXNUM, "Integer" },
        };

        internal static void rb_unexpected_type(VALUE self, int t) {
            string expected;
            if (!_typeNames.TryGetValue(t, out expected)) {
                throw RubyExceptions.CreateRuntimeError(String.Format("unknown type 0x{0:x} ({1} given)", t, ClassName(FromValue(self))));
            }
            throw RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (expected {1})", ClassName(FromValue(self)), expected));
        }

        #endregion

        #region IDs

        internal static VALUE rb_intern(byte* name) {
            return SymbolToId(_context.CreateSymbol(CBytes(name), RubyEncoding.UTF8));
        }

        internal static VALUE rb_intern2(byte* name, long len) {
            return SymbolToId(_context.CreateSymbol(Bytes(name, len), RubyEncoding.UTF8));
        }

        internal static VALUE rb_intern_str(VALUE str) {
            return SymbolToId(_context.CreateSymbol(ToMutableString(str)));
        }

        internal static VALUE rb_to_id(VALUE name) {
            object o = FromValue(name);
            if (o is RubySymbol sym) {
                return SymbolToId(sym);
            }
            if (o is MutableString str) {
                return SymbolToId(_context.CreateSymbol(str));
            }
            throw RubyExceptions.CreateTypeError(String.Format("{0} is not a symbol nor a string", Inspect(o)));
        }

        internal static VALUE rb_id2sym(VALUE id) {
            return (id << 8) | 0x0c;
        }

        internal static VALUE rb_sym2id(VALUE obj) {
            if ((obj & 0xff) == 0x0c) {
                return obj >> 8;
            }
            var sym = FromValue(obj) as RubySymbol;
            if (sym == null) {
                throw RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (expected Symbol)", ClassName(FromValue(obj))));
            }
            return SymbolToId(sym);
        }

        internal static byte* rb_id2name(VALUE id) {
            return PermanentCString(IdToString(id));
        }

        internal static VALUE rb_id2str(VALUE id) {
            return ToValue(IdToSymbol(id).String.Clone().Freeze());
        }

        internal static VALUE rb_frame_this_func() {
            var frame = _frame;
            return frame != null ? Intern(frame.Method.Name) : 0;
        }

        internal static byte* rb_sourcefile() {
            return null;
        }

        internal static int rb_sourceline() {
            return 0;
        }

        #endregion

        #region Exceptions

        // what rb_protect & co caught on this thread (CRuby's ec->errinfo)
        [ThreadStatic] private static Exception _errinfo;

        // CRuby's TAG_RAISE, TAG_THROW...
        private const int TAG_RETURN = 1, TAG_BREAK = 2, TAG_RAISE = 6, TAG_THROW = 7, TAG_FATAL = 8;

        internal static int rb_ir_caught() {
            Exception e = _pending;
            _pending = null;
            _errinfo = e;
            if (e is StackUnwinder) {
                // throw, break and the like: the tag only has to be non-zero to the extension
                return TAG_THROW;
            }
            return TAG_RAISE;
        }

        internal static void rb_jump_tag(int state) {
            Exception e = _errinfo;
            _errinfo = null;
            if (e == null) {
                throw RubyExceptions.CreateRuntimeError(String.Format("rb_jump_tag({0}) with nothing to rethrow", state));
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
        }

        internal static int rb_ir_rescue_match(int count, VALUE* classes) {
            Exception e = _errinfo;
            if (e == null || e is StackUnwinder) {
                return 0;
            }
            for (int i = 0; i < count; i++) {
                var module = FromValue(classes[i]) as RubyModule;
                if (module != null && _context.IsKindOf(e, module)) {
                    return 1;
                }
            }
            return 0;
        }

        internal static VALUE rb_errinfo() {
            Exception e = _errinfo;
            if (e != null && !(e is StackUnwinder)) {
                return ToValue(e);
            }
            return ToValue(_context.CurrentException);
        }

        internal static void rb_set_errinfo(VALUE err) {
            object e = FromValue(err);
            if (e != null && !(e is Exception)) {
                throw RubyExceptions.CreateTypeError("assigning non-exception to $!");
            }
            _errinfo = (Exception)e;
        }

        internal static void rb_exc_raise(VALUE exc) {
            Send(_context.KernelModule, "raise", null, FromValue(exc));
            throw new InvalidOperationException("Kernel.raise returned");
        }

        internal static void rb_exc_fatal(VALUE exc) {
            rb_exc_raise(exc);
        }

        internal static VALUE rb_exc_new(VALUE etype, byte* ptr, long len) {
            return ToValue(Send(FromValue(etype), "new", null, MutableString.CreateBinary(Bytes(ptr, len), RubyEncoding.Binary)));
        }

        internal static VALUE rb_exc_new_cstr(VALUE etype, byte* str) {
            return ToValue(Send(FromValue(etype), "new", null, MutableString.CreateBinary(CBytes(str), RubyEncoding.Binary)));
        }

        internal static VALUE rb_exc_new_str(VALUE etype, VALUE str) {
            return ToValue(Send(FromValue(etype), "new", null, FromValue(str)));
        }

        private static void RaiseSystemCallError(int err, string msg) {
            object cls = _context.ObjectClass.TryGetConstant(null, "SystemCallError", out cls) ? cls : null;
            object exc = Send(cls, "new", null, msg != null ? MutableString.Create(msg, RubyEncoding.UTF8) : null, err);
            Send(_context.KernelModule, "raise", null, exc);
        }

        internal static void rb_syserr_fail(int err, byte* msg) {
            RaiseSystemCallError(err, msg != null ? FromCString(msg) : null);
        }

        /// <summary>
        /// Raises an exception of a class IronRuby defines in a library (FloatDomainError...).
        /// </summary>
        internal static void RaiseNamed(string/*!*/ className, string/*!*/ message) {
            Send(_context.KernelModule, "raise", null, ResolveGlobal(_context, className), MutableString.Create(message, RubyEncoding.UTF8));
            throw new InvalidOperationException("Kernel.raise returned");
        }

        internal static void rb_memerror() {
            throw new OutOfMemoryException("failed to allocate memory");
        }

        internal static void rb_error_arity(int argc, int min, int max) {
            string expected = min == max ? min.ToString() : max == -1 || max == Int32.MaxValue ? min + "+" : min + ".." + max;
            throw RubyExceptions.CreateArgumentError(String.Format("wrong number of arguments (given {0}, expected {1})", argc, expected));
        }

        internal static void rb_error_frozen_object(VALUE what) {
            object o = FromValue(what);
            throw RubyExceptions.CreateObjectFrozenError(_context, o);
        }

        internal static void rb_scan_args_bad_format(byte* fmt) {
            throw RubyExceptions.CreateRuntimeError(String.Format("bad scan arg format: {0}", FromCString(fmt)));
        }

        internal static void rb_scan_args_length_mismatch(byte* fmt, int argc) {
            throw RubyExceptions.CreateRuntimeError(String.Format("variable argument length mismatch: {0} {1}", argc, FromCString(fmt)));
        }

        internal static void rb_cmperr(VALUE a, VALUE b) {
            object y = FromValue(b);
            string what = (y == null || y is bool || y is int || y is double) ? Inspect(y) : ClassName(y);
            throw RubyExceptions.CreateArgumentError(String.Format("comparison of {0} with {1} failed", ClassName(FromValue(a)), what));
        }

        internal static int rb_cmpint(VALUE val, VALUE a, VALUE b) {
            if (val == Qnil) {
                rb_cmperr(a, b);
            }
            if ((val & 1) != 0) {
                long n = (long)val >> 1;
                return n > 0 ? 1 : n < 0 ? -1 : 0;
            }
            object v = FromValue(val);
            if (v is System.Numerics.BigInteger big) {
                return big.Sign;
            }
            if (Protocols.IsTrue(Send(v, ">", null, 0))) return 1;
            if (Protocols.IsTrue(Send(v, "<", null, 0))) return -1;
            return 0;
        }

        internal static void rb_ir_warn(VALUE message, int verbose_only) {
            _context.ReportWarning(ToMutableString(message).ToString(), verbose_only != 0);
        }

        internal static void rb_ir_raise_notimp(byte* name) {
            throw RubyExceptions.CreateNotImplementedError(String.Format("{0}() is not implemented by IronRuby's C API yet", FromCString(name)));
        }

        internal static void rb_ir_bug(byte* message) {
            string m = "[BUG] " + FromCString(message);
            Console.Error.WriteLine(m);
            Environment.FailFast(m);
        }

        #endregion

        #region GC

        // Every handle is kept for the life of the process (CApi.Values.cs), so there is nothing to
        // register, mark or barrier yet.
        internal static void rb_gc_register_address(VALUE* valptr) { }
        internal static void rb_gc_unregister_address(VALUE* valptr) { }
        internal static void rb_gc_register_mark_object(VALUE obj) { }
        internal static void rb_global_variable(VALUE* valptr) { }
        internal static void rb_gc_mark(VALUE obj) { }
        internal static void rb_gc_mark_movable(VALUE obj) { }
        internal static void rb_gc_mark_maybe(VALUE obj) { }
        internal static void rb_gc_writebarrier(VALUE old, VALUE young) { }
        internal static void rb_gc_writebarrier_unprotect(VALUE obj) { }
        internal static VALUE rb_gc_location(VALUE obj) { return obj; }
        internal static int rb_during_gc() { return 0; }

        internal static void rb_mem_clear(VALUE* buf, long len) {
            for (long i = 0; i < len; i++) {
                buf[i] = Qnil;
            }
        }

        #endregion
    }
}
