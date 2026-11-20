/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Numerics;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// The rest of the core library's C API - IO, Struct, Range, Regexp, Mutex, Thread, Set,
    /// Proc, conversions - mostly by calling the Ruby methods CRuby's functions implement.
    /// </summary>
    internal static unsafe partial class CApi {

        private static object Constant(string/*!*/ path) {
            return ResolveGlobal(_context, path);
        }

        private static object Call(object target, string/*!*/ name, params object[]/*!*/ args) {
            return Send(target, name, null, args);
        }

        private static VALUE CallV(VALUE target, string/*!*/ name, params object[]/*!*/ args) {
            return ToValue(Send(FromValue(target), name, null, args));
        }

        private static RubySymbol/*!*/ Sym(string/*!*/ name) {
            return _context.CreateAsciiSymbol(name);
        }

        // as CRuby's rb_check_funcall: #respond_to? when the object has one (a BasicObject has not),
        // otherwise whether the method is there
        private static bool RespondTo(object obj, string/*!*/ name, bool includePrivate = true) {
            if (!_context.ResolveMethod(obj, "respond_to?", true).Found) {
                return _context.ResolveMethod(obj, name, includePrivate).Found;
            }
            return Protocols.IsTrue(Call(obj, "respond_to?", Sym(name), includePrivate));
        }

        #region Conversions

        internal static byte* rb_string_value_ptr(VALUE* ptr) {
            return GetStringMirror(ToMutableString(rb_string_value(ptr))).Ptr;
        }

        private static readonly Dictionary<int, string> _convertClassNames = new Dictionary<int, string> {
            { T_STRING, "String" }, { T_ARRAY, "Array" }, { T_HASH, "Hash" }, { T_FLOAT, "Float" },
            { T_FIXNUM, "Integer" }, { T_BIGNUM, "Integer" }, { T_SYMBOL, "Symbol" }, { T_REGEXP, "Regexp" },
        };

        private static bool HasType(object obj, int type) {
            int actual = BuiltinType(obj);
            return actual == type || (type == T_BIGNUM && actual == T_FIXNUM) || (type == T_FIXNUM && actual == T_BIGNUM);
        }

        /// <summary>
        /// CRuby's convert_type_with_id: obj.mid if obj responds to it; a TypeError (raise) or nil.
        /// </summary>
        private static object ConvertType(object obj, int type, string/*!*/ typeName, string/*!*/ method, bool raise) {
            if (HasType(obj, type)) {
                return obj;
            }
            if (!RespondTo(obj, method)) {
                if (!raise) {
                    return null;
                }
                string what = obj == null ? "nil" : obj is bool b ? (b ? "true" : "false") : ClassName(obj);
                throw RubyExceptions.CreateTypeError(String.Format("no implicit conversion of {0} into {1}", what, typeName));
            }
            object result = Call(obj, method);
            if (result == null && !raise) {
                return null;
            }
            if (!HasType(result, type)) {
                throw RubyExceptions.CreateTypeError(String.Format("can't convert {0} to {1} ({0}#{2} gives {3})",
                    ClassName(obj), typeName, method, ClassName(result)));
            }
            return result;
        }

        internal static VALUE rb_convert_type(VALUE val, int type, byte* name, byte* mid) {
            return ToValue(ConvertType(FromValue(val), type, FromCString(name), FromCString(mid), true));
        }

        internal static VALUE rb_check_convert_type(VALUE val, int type, byte* name, byte* mid) {
            return ToValue(ConvertType(FromValue(val), type, FromCString(name), FromCString(mid), false));
        }

        internal static VALUE rb_check_array_type(VALUE obj) {
            return ToValue(ConvertType(FromValue(obj), T_ARRAY, "Array", "to_ary", false));
        }

        internal static VALUE rb_check_to_integer(VALUE val, byte* mid) {
            object o = FromValue(val);
            if (AsInteger(o).HasValue) {
                return val;
            }
            string method = FromCString(mid);
            if (!RespondTo(o, method)) {
                return Qnil;
            }
            object r = Call(o, method);
            return AsInteger(r).HasValue ? ToValue(r) : Qnil;
        }

        internal static VALUE rb_get_path(VALUE obj) {
            return ToValue(Call(Constant("File"), "path", FromValue(obj)));
        }

        internal static VALUE rb_to_symbol(VALUE name) {
            object o = FromValue(name);
            if (o is RubySymbol) {
                return name;
            }
            return ToValue(_context.CreateSymbol(ConvertToString(o)));
        }

        internal static VALUE rb_sym2str(VALUE symbol) {
            return ToValue(Call(FromValue(symbol), "name"));
        }

        internal static VALUE rb_sym_to_s(VALUE sym) {
            return ToValue(Call(FromValue(sym), "to_s"));
        }

        internal static VALUE rb_intern3(byte* name, long len, void* enc) {
            return SymbolToId(_context.CreateSymbol(Bytes(name, len), FromNativeEncoding(enc) ?? RubyEncoding.Binary));
        }

        internal static VALUE rb_str_format(int argc, VALUE* argv, VALUE fmt) {
            var args = new List<object> { FromValue(fmt) };
            args.AddRange(FromValues(argc, argv));
            return ToValue(Call(_context.KernelModule, "format", args.ToArray()));
        }

        internal static VALUE rb_f_sprintf(int argc, VALUE* argv) {
            return ToValue(Call(_context.KernelModule, "format", FromValues(argc, argv)));
        }

        #endregion

        #region Integers

        internal static ulong rb_fix2uint(VALUE num) {
            return rb_num2uint(num);
        }

        internal static VALUE rb_uint2big(nuint i) { return ToValue(Normalize((ulong)i)); }
        internal static VALUE rb_uint2inum(nuint i) { return ToValue(Normalize((ulong)i)); }
        internal static VALUE rb_int2inum(nint i) { return ToValue(Normalize((long)i)); }

        private static BigInteger ToBig(VALUE val) {
            return ToBigInteger(FromValue(val), "integer");
        }

        internal static nuint rb_absint_size(VALUE val, int* nlz_bits_ret) {
            BigInteger abs = BigInteger.Abs(ToBig(val));
            long bits = abs.IsZero ? 0 : (long)abs.GetBitLength();
            long bytes = (bits + 7) / 8;
            if (nlz_bits_ret != null) {
                *nlz_bits_ret = (int)(bytes * 8 - bits);
            }
            return (nuint)bytes;
        }

        internal static int rb_absint_singlebit_p(VALUE val) {
            BigInteger abs = BigInteger.Abs(ToBig(val));
            return !abs.IsZero && abs.IsPowerOfTwo ? 1 : 0;
        }

        internal static int rb_big_sign(VALUE num) {
            return ToBig(num).Sign >= 0 ? 1 : 0;
        }

        internal static VALUE rb_big_cmp(VALUE lhs, VALUE rhs) {
            return CallV(lhs, "<=>", FromValue(rhs));
        }

        internal static void rb_big_pack(VALUE val, System.Runtime.InteropServices.CULong* buf, long num_longs) {
            // rb_integer_pack(val, buf, num_longs, sizeof(long), 0, INTEGER_PACK_LSWORD_FIRST|INTEGER_PACK_NATIVE|INTEGER_PACK_2COMP)
            rb_integer_pack(val, buf, (nuint)num_longs, (nuint)sizeof(System.Runtime.InteropServices.CULong), 0,
                PACK_LSWORD_FIRST | PACK_NATIVE_BYTE_ORDER | PACK_2COMP);
        }

        internal static void rb_num_zerodiv() {
            RaiseNamed("ZeroDivisionError", "divided by 0");
        }

        private static object Coerce(VALUE lhs, VALUE rhs, out object y) {
            object x = FromValue(lhs);
            var pair = Call(FromValue(rhs), "coerce", x) as RubyArray;
            if (pair == null || pair.Count != 2) {
                throw RubyExceptions.CreateTypeError("coerce must return [x, y]");
            }
            y = pair[1];
            return pair[0];
        }

        internal static VALUE rb_num_coerce_bin(VALUE lhs, VALUE rhs, VALUE op) {
            object y;
            object x = Coerce(lhs, rhs, out y);
            return ToValue(Call(x, IdToString(op), y));
        }

        // numeric.c's do_coerce(..., FALSE): nil when #coerce answers no pair, what it raises goes through
        internal static VALUE rb_num_coerce_cmp(VALUE lhs, VALUE rhs, VALUE op) {
            var pair = Call(FromValue(rhs), "coerce", FromValue(lhs)) as RubyArray;
            if (pair == null || pair.Count != 2) {
                return Qnil;
            }
            return ToValue(Call(pair[0], IdToString(op), pair[1]));
        }

        internal static VALUE rb_num_coerce_relop(VALUE lhs, VALUE rhs, VALUE op) {
            object y, x;
            try {
                x = Coerce(lhs, rhs, out y);
            } catch (Exception) {
                rb_cmperr(lhs, rhs);
                return Qnil;
            }
            object r = Call(x, IdToString(op), y);
            if (r == null) {
                rb_cmperr(lhs, rhs);
            }
            return ToValue(r);
        }

        internal static VALUE rb_rational_new(VALUE num, VALUE den) {
            return ToValue(Call(_context.KernelModule, "Rational", FromValue(num), FromValue(den)));
        }

        internal static VALUE rb_rational_num(VALUE rat) { return CallV(rat, "numerator"); }
        internal static VALUE rb_rational_den(VALUE rat) { return CallV(rat, "denominator"); }

        internal static VALUE rb_complex_new(VALUE real, VALUE imag) {
            return ToValue(Call(_context.KernelModule, "Complex", FromValue(real), FromValue(imag)));
        }

        #endregion

        #region IDs

        private static int Scope(VALUE id) {
            return id < 128 ? -1 : (int)(id & 0x0e);
        }

        internal static int rb_is_local_id(VALUE id) { return Scope(id) == 0x00 ? 1 : 0; }
        internal static int rb_is_instance_id(VALUE id) { return Scope(id) == 0x02 ? 1 : 0; }
        internal static int rb_is_global_id(VALUE id) { return Scope(id) == 0x06 ? 1 : 0; }
        internal static int rb_is_attrset_id(VALUE id) { return Scope(id) == 0x08 ? 1 : 0; }
        internal static int rb_is_const_id(VALUE id) { return Scope(id) == 0x0a ? 1 : 0; }
        internal static int rb_is_class_id(VALUE id) { return Scope(id) == 0x0c ? 1 : 0; }

        #endregion

        #region Classes, modules, methods

        internal static VALUE rb_define_class_id_under(VALUE outer, VALUE id, VALUE super) {
            return ToValue(DefineClassUnder(ToModule(outer), IdToString(id), super));
        }

        internal static VALUE rb_define_module_id_under(VALUE outer, VALUE id) {
            return ToValue(DefineModuleUnder(ToModule(outer), IdToString(id)));
        }

        internal static int rb_const_defined_at(VALUE space, VALUE name) {
            return Protocols.IsTrue(Call(FromValue(space), "const_defined?", IdToSymbol(name), false)) ? 1 : 0;
        }

        internal static byte* rb_class2name(VALUE klass) {
            // rb_class_name's: an anonymous class is its inspect, "#<Class:0x...>"
            var real = FromValue(rb_class_real(klass)) as RubyModule;
            return real != null ? PermanentCString(FromValue(rb_class_path(ToValue(real))).ToString()) : null;
        }

        internal static VALUE rb_class_real(VALUE klass) {
            object o = FromValue(klass);
            var cls = o as RubyClass;
            if (cls == null) {
                // a module is its own real class, as in CRuby
                return o is RubyModule ? klass : Qfalse;
            }
            return ToValue(cls.GetNonSingletonClass());
        }

        internal static VALUE rb_path_to_class(VALUE path) {
            var s = ConvertToString(FromValue(path));
            fixed (byte* p = CString(s.ToString())) {
                return rb_path2class(p);
            }
        }

        internal static VALUE rb_class_new(VALUE super) {
            // unlike Class.new, without the superclass's #inherited
            RubyClass superClass = RubyClass.CheckInheritable(_context, FromValue(super));
            return ToValue(_context.DefineClass(_context.ObjectClass, null, superClass, null, false));
        }

        internal static void rb_prepend_module(VALUE klass, VALUE module) {
            Call(FromValue(klass), "prepend", FromValue(module));
        }

        internal static void rb_undef(VALUE mod, VALUE mid) {
            Call(FromValue(mod), "undef_method", IdToSymbol(mid));
        }

        internal static void rb_alias(VALUE klass, VALUE dst, VALUE src) {
            Call(FromValue(klass), "alias_method", IdToSymbol(dst), IdToSymbol(src));
        }

        internal static VALUE rb_class_public_instance_methods(int argc, VALUE* argv, VALUE mod) { return CallV(mod, "public_instance_methods", FromValues(argc, argv)); }
        internal static VALUE rb_class_instance_methods(int argc, VALUE* argv, VALUE mod) { return CallV(mod, "instance_methods", FromValues(argc, argv)); }
        internal static VALUE rb_class_private_instance_methods(int argc, VALUE* argv, VALUE mod) { return CallV(mod, "private_instance_methods", FromValues(argc, argv)); }
        internal static VALUE rb_class_protected_instance_methods(int argc, VALUE* argv, VALUE mod) { return CallV(mod, "protected_instance_methods", FromValues(argc, argv)); }
        internal static VALUE rb_obj_singleton_methods(int argc, VALUE* argv, VALUE obj) { return CallV(obj, "singleton_methods", FromValues(argc, argv)); }
        internal static VALUE rb_mod_constants(int argc, VALUE* argv, VALUE recv) { return CallV(recv, "constants", FromValues(argc, argv)); }

        internal static VALUE rb_mod_module_eval(int argc, VALUE* argv, VALUE mod) {
            return ToValue(Send(FromValue(mod), "module_eval", _frame?.Block, FromValues(argc, argv)));
        }

        internal static VALUE rb_obj_instance_exec(int argc, VALUE* argv, VALUE recv) {
            return ToValue(Send(FromValue(recv), "instance_exec", _frame?.Block, FromValues(argc, argv)));
        }

        internal static VALUE rb_obj_method(VALUE recv, VALUE mid) {
            return CallV(recv, "method", FromValue(mid));
        }

        internal static int rb_obj_method_arity(VALUE obj, VALUE mid) {
            return (int)Call(Call(FromValue(obj), "method", IdToSymbol(mid)), "arity");
        }

        // ex: 0 any visibility, 1 public only (CRuby's BOUND_RESPONDS)
        internal static int rb_method_boundp(VALUE klass, VALUE id, int ex) {
            object m = FromValue(klass);
            var sym = IdToSymbol(id);
            if (Protocols.IsTrue(Call(m, "public_method_defined?", sym))) {
                return 1;
            }
            if (ex != 0) {
                return 0;
            }
            return Protocols.IsTrue(Call(m, "private_method_defined?", sym)) || Protocols.IsTrue(Call(m, "protected_method_defined?", sym)) ? 1 : 0;
        }

        internal static int rb_obj_respond_to(VALUE obj, VALUE mid, int private_p) {
            return Protocols.IsTrue(Call(FromValue(obj), "respond_to?", IdToSymbol(mid), private_p != 0)) ? 1 : 0;
        }

        internal static VALUE rb_check_funcall(VALUE recv, VALUE mid, int argc, VALUE* argv) {
            object o = FromValue(recv);
            string name = IdToString(mid);
            if (!RespondTo(o, name)) {
                return Qundef;
            }
            return ToValue(Send(o, name, null, FromValues(argc, argv)));
        }

        internal static VALUE rb_class_new_instance_kw(int argc, VALUE* argv, VALUE klass, int kw_splat) {
            return ToValue(Send(FromValue(klass), "new", null, KeywordArgs(argc, argv, kw_splat)));
        }

        internal static VALUE rb_funcall_with_block_kw(VALUE recv, VALUE mid, int argc, VALUE* argv, VALUE procval, int kw_splat) {
            return ToValue(SendWith(true, FromValue(recv), IdToString(mid), FromValue(procval) as Proc, KeywordArgs(argc, argv, kw_splat)));
        }

        internal static int rb_frame_method_id_and_class(VALUE* idp, VALUE* klassp) {
            var frame = _frame;
            if (frame == null) {
                return 0;
            }
            if (idp != null) *idp = Intern(frame.Method.Name);
            if (klassp != null) *klassp = ToValue(frame.Method.Owner);
            return 1;
        }

        internal static IntPtr rb_get_alloc_func(VALUE klass) {
            var cls = FromValue(klass) as RubyClass;
            return cls != null ? GetAllocFunc(cls) : IntPtr.Zero;
        }

        internal static VALUE rb_make_backtrace() {
            return ToValue(Call(_context.KernelModule, "caller", 0));
        }

        #endregion

        #region Instance variables

        internal static VALUE rb_obj_instance_variables(VALUE obj) { return CallV(obj, "instance_variables"); }

        internal static ulong rb_ivar_count(VALUE obj) {
            return (ulong)_context.GetInstanceVariableNames(FromValue(obj)).Length;
        }

        internal static void rb_ivar_foreach(VALUE obj, IntPtr func, ulong arg) {
            object o = FromValue(obj);
            // a class's or module's own class variables come first, as CRuby keeps them in the same table
            if (o is RubyModule module) {
                foreach (object cvar in (RubyArray)Call(module, "class_variables", false)) {
                    if (CallForeach(func, Intern(cvar.ToString()), ToValue(Call(module, "class_variable_get", cvar)), arg) == ST_STOP) {
                        return;
                    }
                }
            }
            foreach (var name in _context.GetInstanceVariableNames(o)) {
                object value;
                _context.TryGetInstanceVariable(o, name, out value);
                int r = CallForeach(func, Intern(name), ToValue(value), arg);
                if (r == ST_STOP) {
                    break;
                }
            }
        }

        internal static void rb_free_generic_ivar(VALUE obj) {
            object o = FromValue(obj);
            foreach (var name in _context.GetInstanceVariableNames(o)) {
                object unused;
                _context.TryRemoveInstanceVariable(o, name, out unused);
            }
        }

        internal static void rb_copy_generic_ivar(VALUE clone, VALUE obj) {
            object from = FromValue(obj), to = FromValue(clone);
            foreach (var name in _context.GetInstanceVariableNames(from)) {
                object value;
                _context.TryGetInstanceVariable(from, name, out value);
                _context.SetInstanceVariable(to, name, value);
            }
        }

        #endregion

        #region Exceptions, catch/throw

        internal static VALUE rb_make_exception(int argc, VALUE* argv) {
            if (argc == 0) {
                return Qnil;
            }
            object first = FromValue(argv[0]);
            if (argc == 1 && first is MutableString) {
                return ToValue(Call(Constant("RuntimeError"), "new", first));
            }
            if (argc > 3) {
                rb_error_arity(argc, 0, 3);
            }
            if (!RespondTo(first, "exception")) {
                throw RubyExceptions.CreateTypeError("exception class/object expected");
            }
            object exc = argc == 1 ? Call(first, "exception") : Call(first, "exception", FromValue(argv[1]));
            if (!(exc is Exception)) {
                throw RubyExceptions.CreateTypeError("exception object expected");
            }
            if (argc == 3) {
                Call(exc, "set_backtrace", FromValue(argv[2]));
            }
            return ToValue(exc);
        }

        internal static VALUE rb_syserr_new(int err, byte* msg) {
            return ToValue(Call(Constant("SystemCallError"), "new", msg != null ? (object)MutableString.Create(FromCString(msg), RubyEncoding.UTF8) : null, err));
        }

        internal static VALUE rb_syserr_new_str(int n, VALUE arg) {
            return ToValue(Call(Constant("SystemCallError"), "new", FromValue(arg), n));
        }

        internal static void rb_syserr_fail_str(int err, VALUE msg) {
            rb_exc_raise(rb_syserr_new_str(err, msg));
        }

        internal static VALUE rb_catch_obj(VALUE tag, IntPtr func, VALUE data) {
            return ToValue(Send(_context.KernelModule, "catch", MakeBlock(func, data), FromValue(tag)));
        }

        internal static void rb_throw_obj(VALUE tag, VALUE val) {
            Call(_context.KernelModule, "throw", FromValue(tag), FromValue(val));
        }

        internal static void rb_throw(byte* tag, VALUE val) {
            Call(_context.KernelModule, "throw", _context.EncodeIdentifier(FromCString(tag)), FromValue(val));
        }

        #endregion

        #region Procs and blocks

        internal static VALUE rb_proc_new(IntPtr func, VALUE callback_arg) {
            return ToValue(MakeBlock(func, callback_arg));
        }

        internal static VALUE rb_block_lambda() {
            var block = _frame?.Block;
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }
            if (block.Kind == ProcKind.Lambda || _frame.Bfc != null && !_frame.Bfc.IsLiteralBlock) {
                // a Proc passed with &: CRuby hands it back as it is, lambda or not
                return ToValue(block);
            }
            return ToValue(block.ToLambda(null));
        }

        internal static VALUE rb_yield_splat(VALUE ary) {
            var array = FromValue(ary) as RubyArray;
            if (array == null) {
                throw RubyExceptions.CreateArgumentError("not an array");
            }
            return Yield(array.ToArray());
        }

        internal static VALUE rb_obj_is_proc(VALUE recv) { return Bool(FromValue(recv) is Proc); }
        internal static VALUE rb_obj_is_method(VALUE recv) { return Bool(FromValue(recv) is RubyMethod); }

        internal static VALUE rb_proc_call(VALUE recv, VALUE args) {
            return ToValue(Send(FromValue(recv), "call", null, ((RubyArray)FromValue(args)).ToArray()));
        }

        internal static VALUE rb_proc_call_with_block(VALUE recv, int argc, VALUE* argv, VALUE proc) {
            return ToValue(Send(FromValue(recv), "call", FromValue(proc) as Proc, FromValues(argc, argv)));
        }

        internal static int rb_proc_arity(VALUE recv) { return (int)Call(FromValue(recv), "arity"); }
        internal static VALUE rb_proc_lambda_p(VALUE recv) { return CallV(recv, "lambda?"); }

        internal static VALUE rb_method_call(int argc, VALUE* argv, VALUE recv) {
            return ToValue(Send(FromValue(recv), "call", null, FromValues(argc, argv)));
        }

        internal static VALUE rb_enumeratorize(VALUE recv, VALUE meth, int argc, VALUE* argv) {
            var args = new List<object> { FromValue(meth) };
            args.AddRange(FromValues(argc, argv));
            return ToValue(Call(FromValue(recv), "enum_for", args.ToArray()));
        }

        /// <summary>
        /// enumerator.c's: the enumerator's size comes from func(recv, args, enumerator), called
        /// when Enumerator#size asks - a size block given to enum_for.
        /// </summary>
        internal static VALUE rb_enumeratorize_with_size(VALUE recv, VALUE meth, int argc, VALUE* argv, void* func) {
            if (func == null) {
                return rb_enumeratorize(recv, meth, argc, argv);
            }
            var args = new List<object> { FromValue(meth) };
            args.AddRange(FromValues(argc, argv));
            var sizeArgs = new RubyArray(FromValues(argc, argv));
            object enumerator = null;
            var size = Proc.Create(_context, 0, new Func<BlockParam, object, object[], RubyArray, object>((b, self, unused, rest) => {
                VALUE* sizeArgv = stackalloc VALUE[2];
                sizeArgv[0] = ToValue(sizeArgs);
                sizeArgv[1] = ToValue(enumerator);
                return FromValue(CallMethod((IntPtr)func, 2, recv, 2, sizeArgv));
            }));
            enumerator = Send(FromValue(recv), "enum_for", size, args.ToArray());
            return ToValue(enumerator);
        }

        internal static void rb_set_end_proc(IntPtr func, VALUE arg) {
            Send(_context.KernelModule, "at_exit", Proc.Create(_context, 0, new Func<BlockParam, object, object[], RubyArray, object>(
                (b, self, unused, args) => { CallVoid(func, (void*)arg); return null; })));
        }

        #endregion

        #region IO

        // The descriptor the OS knows for one of IronRuby's (IO#fileno numbers a stream in IronRuby's
        // own table), or fd itself when IronRuby has no stream under it: what an extension got
        // from rb_cloexec_open or open(2) is native already. -1 for a stream with no descriptor.
        internal static int rb_ir_native_fd(int fd) {
            if (fd <= 2) {
                return fd;
            }
            var stream = _context.GetStream(fd);
            return stream != null ? RubyIO.DescriptorOf(stream) : fd;
        }

        internal static int rb_io_descriptor(VALUE io) {
            return (int)Call(FromValue(io), "fileno");
        }

        internal static VALUE rb_io_write(VALUE io, VALUE str) { return CallV(io, "write", FromValue(str)); }
        internal static VALUE rb_io_puts(int argc, VALUE* argv, VALUE io) { return CallV(io, "puts", FromValues(argc, argv)); }
        internal static VALUE rb_io_print(int argc, VALUE* argv, VALUE io) { return CallV(io, "print", FromValues(argc, argv)); }
        internal static VALUE rb_io_addstr(VALUE io, VALUE str) { CallV(io, "<<", FromValue(str)); return io; }
        internal static VALUE rb_io_closed_p(VALUE io) { return CallV(io, "closed?"); }
        internal static VALUE rb_io_binmode(VALUE io) { return CallV(io, "binmode"); }
        internal static VALUE rb_io_close(VALUE io) { return CallV(io, "close"); }
        internal static VALUE rb_io_path(VALUE io) { return CallV(io, "path"); }

        internal static VALUE rb_io_get_io(VALUE io) {
            return ToValue(ConvertType(FromValue(io), T_FILE, "IO", "to_io", true));
        }

        internal static VALUE rb_io_check_io(VALUE io) {
            return ToValue(ConvertType(FromValue(io), T_FILE, "IO", "to_io", false));
        }

        internal static VALUE rb_file_open_str(VALUE fname, byte* fmode) {
            return ToValue(Call(Constant("File"), "open", FromValue(fname), MutableString.Create(FromCString(fmode), RubyEncoding.UTF8)));
        }

        internal static VALUE rb_file_open(byte* fname, byte* fmode) {
            return ToValue(Call(Constant("File"), "open", MutableString.CreateBinary(CBytes(fname), RubyEncoding.UTF8),
                MutableString.Create(FromCString(fmode), RubyEncoding.UTF8)));
        }

        internal static VALUE rb_marshal_dump(VALUE obj, VALUE port) {
            object p = FromValue(port);
            return p == null ? ToValue(Call(Constant("Marshal"), "dump", FromValue(obj))) : ToValue(Call(Constant("Marshal"), "dump", FromValue(obj), p));
        }

        internal static VALUE rb_marshal_load(VALUE port) {
            return ToValue(Call(Constant("Marshal"), "load", FromValue(port)));
        }

        #endregion

        #region Struct

        internal static VALUE rb_struct_aref(VALUE st, VALUE k) { return CallV(st, "[]", FromValue(k)); }
        internal static VALUE rb_struct_aset(VALUE st, VALUE k, VALUE v) { return CallV(st, "[]=", FromValue(k), FromValue(v)); }
        internal static VALUE rb_struct_getmember(VALUE self, VALUE key) { return CallV(self, "[]", IdToSymbol(key)); }
        internal static VALUE rb_struct_members(VALUE self) { return CallV(self, "members"); }
        internal static VALUE rb_struct_s_members(VALUE klass) { return CallV(klass, "members"); }
        internal static VALUE rb_struct_size(VALUE st) { return CallV(st, "size"); }

        internal static VALUE rb_gc_latest_gc_info(VALUE key) {
            object k = FromValue(key);
            if (!(k is RubySymbol) && !(k is IDictionary<object, object>)) {
                throw RubyExceptions.CreateTypeError("non-hash or symbol given");
            }
            if (k is RubySymbol sym && !((IDictionary<object, object>)Call(Constant("GC"), "latest_gc_info")).ContainsKey(sym)) {
                throw RubyExceptions.CreateArgumentError("unknown key: " + sym);
            }
            return ToValue(Call(Constant("GC"), "latest_gc_info", k));
        }

        internal static VALUE rb_struct_initialize(VALUE self, VALUE values) {
            object obj = FromValue(self);
            var args = ((RubyArray)FromValue(values)).ToArray();
            var data = Constant("Data") as RubyModule;
            if (data != null && _context.IsKindOf(obj, data)) {
                // a Data object takes its members positionally here, the missing ones nil, and is frozen after
                if (_context.IsObjectFrozen(obj)) {
                    throw RubyExceptions.CreateObjectFrozenError(_context, obj);
                }
                var members = (RubyArray)Call(_context.GetClassOf(obj), "members");
                if (args.Length > members.Count) {
                    throw RubyExceptions.CreateArgumentError("struct size differs");
                }
                var keywords = new Hash(_context.EqualityComparer, members.Count);
                for (int i = 0; i < members.Count; i++) {
                    keywords[members[i]] = i < args.Length ? args[i] : null;
                }
                keywords.IsKeywordArguments = true;
                Send(obj, "initialize", null, keywords);
                _context.FreezeObject(obj);
                return Qnil;
            }
            Send(obj, "initialize", null, args);
            return Qnil;
        }

        #endregion

        #region Range, Regexp

        internal static VALUE rb_range_new(VALUE beg, VALUE end, int excl) {
            return ToValue(Call(Constant("Range"), "new", FromValue(beg), FromValue(end), excl != 0));
        }

        internal static int rb_range_values(VALUE range, VALUE* begp, VALUE* endp, int* exclp) {
            object r = FromValue(range);
            if (r is IronRuby.Builtins.Range || (r != null && RespondTo(r, "begin") && RespondTo(r, "end") && RespondTo(r, "exclude_end?"))) {
                *begp = ToValue(Call(r, "begin"));
                *endp = ToValue(Call(r, "end"));
                *exclp = Protocols.IsTrue(Call(r, "exclude_end?")) ? 1 : 0;
                return 1;
            }
            return 0;
        }

        // CRuby's rb_range_beg_len: Qfalse when not a range, Qnil when out of range (err 0), else Qtrue
        internal static VALUE rb_range_beg_len(VALUE range, System.Runtime.InteropServices.CLong* begp, System.Runtime.InteropServices.CLong* lenp, long len, int err) {
            long b = 0, l = 0;
            VALUE r = RangeBegLen(range, &b, &l, len, err);
            if (r == Qtrue) {
                *begp = new System.Runtime.InteropServices.CLong((nint)b);
                *lenp = new System.Runtime.InteropServices.CLong((nint)l);
            }
            return r;
        }

        private static VALUE RangeBegLen(VALUE range, long* begp, long* lenp, long len, int err) {
            VALUE b, e;
            int excl;
            if (rb_range_values(range, &b, &e, &excl) == 0) {
                return Qfalse;
            }
            VALUE r = RangeComponentBegLen(b, e, excl, begp, lenp, len, err);
            if (r == Qnil && err != 0) {
                throw RubyExceptions.CreateRangeError(String.Format("{0} out of range", ToMutableString(rb_inspect(range))));
            }
            return r;
        }

        // CRuby's rb_range_component_beg_len (range.c): Qtrue, or Qnil when out of range.
        private static VALUE RangeComponentBegLen(VALUE b, VALUE e, int excl, long* begp, long* lenp, long len, int err) {
            long beg = b == Qnil ? 0 : rb_num2long(b);
            long end = e == Qnil ? -1 : rb_num2long(e);
            if (e == Qnil) {
                excl = 0;
            }
            if (beg < 0) {
                beg += len;
                if (beg < 0) {
                    return Qnil;
                }
            }
            if (end < 0) {
                end += len;
            }
            // Include the end point; LONG_MAX + 1 is not representable, so saturate.
            if (excl == 0 && end < Int64.MaxValue) {
                end++;
            }
            if (err == 0 || err == 2) {
                if (beg > len) {
                    return Qnil;
                }
                if (end > len) {
                    end = len;
                }
            }
            *begp = beg;
            *lenp = end > beg ? end - beg : 0;
            return Qtrue;
        }

        // rb_arithmetic_sequence_components_t (ruby/internal/intern/enumerator.h)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ArithmeticSequenceComponents {
            public VALUE Begin;
            public VALUE End;
            public VALUE Step;
            public int ExcludeEnd;
        }

        // CRuby's (enumerator.c): an Enumerator::ArithmeticSequence, or a Range with step 1.
        internal static int rb_arithmetic_sequence_extract(VALUE obj, void* buf) {
            var component = (ArithmeticSequenceComponents*)buf;
            object o = FromValue(obj);
            var arithSeq = Constant("Enumerator::ArithmeticSequence") as RubyModule;
            if (arithSeq != null && _context.IsKindOf(o, arithSeq)) {
                component->Begin = ToValue(Call(o, "begin"));
                component->End = ToValue(Call(o, "end"));
                component->Step = ToValue(Call(o, "step"));
                component->ExcludeEnd = Protocols.IsTrue(Call(o, "exclude_end?")) ? 1 : 0;
                return 1;
            }
            VALUE b, e;
            int excl;
            if (rb_range_values(obj, &b, &e, &excl) != 0) {
                component->Begin = b;
                component->End = e;
                component->Step = ToValue((object)1);
                component->ExcludeEnd = excl;
                return 1;
            }
            return 0;
        }

        // CRuby's (enumerator.c)
        internal static VALUE rb_arithmetic_sequence_beg_len_step(VALUE obj, System.Runtime.InteropServices.CLong* begp,
            System.Runtime.InteropServices.CLong* lenp, System.Runtime.InteropServices.CLong* stepp, long len, int err) {
            if (begp == null || lenp == null || stepp == null) {
                throw RubyExceptions.CreateArgumentError("NULL pointer given");
            }
            ArithmeticSequenceComponents aseq;
            if (rb_arithmetic_sequence_extract(obj, &aseq) == 0) {
                return Qfalse;
            }

            long step = aseq.Step == Qnil ? 1 : rb_num2long(aseq.Step);
            *stepp = new System.Runtime.InteropServices.CLong((nint)step);

            if (step < 0) {
                if (aseq.ExcludeEnd != 0 && aseq.End != Qnil) {
                    // the exclusion is handled before the range is reversed
                    aseq.End = ToValue(Protocols.Normalize(rb_num2long(aseq.End) + 1));
                    // and does not exclude the previous beginning
                    aseq.ExcludeEnd = 0;
                }
                VALUE tmp = aseq.Begin;
                aseq.Begin = aseq.End;
                aseq.End = tmp;
            }

            long beg = 0, length = 0;
            VALUE result;
            if (err == 0 && (step < -1 || step > 1)) {
                result = RangeComponentBegLen(aseq.Begin, aseq.End, aseq.ExcludeEnd, &beg, &length, len, 1);
                if (result == Qtrue && beg <= len && length <= len) {
                    *begp = new System.Runtime.InteropServices.CLong((nint)beg);
                    *lenp = new System.Runtime.InteropServices.CLong((nint)length);
                    return Qtrue;
                }
                if (result == Qtrue) {
                    *begp = new System.Runtime.InteropServices.CLong((nint)beg);
                    *lenp = new System.Runtime.InteropServices.CLong((nint)length);
                }
            } else {
                result = RangeComponentBegLen(aseq.Begin, aseq.End, aseq.ExcludeEnd, &beg, &length, len, err);
                if (result == Qtrue) {
                    *begp = new System.Runtime.InteropServices.CLong((nint)beg);
                    *lenp = new System.Runtime.InteropServices.CLong((nint)length);
                }
                return result;
            }
            throw RubyExceptions.CreateRangeError(String.Format("{0} out of range", ToMutableString(rb_inspect(obj))));
        }

        internal static VALUE rb_reg_new(byte* src, long len, int opts) {
            return ToValue(Call(Constant("Regexp"), "new", MutableString.CreateBinary(Bytes(src, len), RubyEncoding.Binary), opts));
        }

        internal static VALUE rb_reg_new_str(VALUE src, int opts) {
            return ToValue(Call(Constant("Regexp"), "new", FromValue(src), opts));
        }

        internal static VALUE rb_reg_regcomp(VALUE str) {
            return ToValue(Call(Constant("Regexp"), "new", FromValue(str)));
        }

        // Regexp#=~, whose $~ is the frame's that called the C method (rb_backref_get reads it there)
        internal static VALUE rb_reg_match(VALUE re, VALUE str) {
            RubyClosureScope scope = CallerClosureScope();
            if (scope == null) {
                return CallV(re, "=~", FromValue(str));
            }
            object match = Call(FromValue(re), "match", FromValue(str));
            scope.CurrentMatch = match as MatchData;
            return match == null ? Qnil : ToValue(Call(match, "begin", 0));
        }
        internal static int rb_reg_options(VALUE re) { return (int)Call(FromValue(re), "options"); }

        internal static VALUE rb_reg_nth_match(int n, VALUE md) {
            object m = FromValue(md);
            return m == null ? Qnil : ToValue(Call(m, "[]", n));
        }

        // $~ and $_ are the frame's that called the C method, as in CRuby. Outside one (an Init
        // function, say) there is no Ruby frame of the extension's own: Kernel#eval's is used.
        private static RubyClosureScope CallerClosureScope() {
            RubyScope scope = _frame != null ? _frame.Scope : null;
            return scope != null ? scope.GetInnerMostClosureScope() : null;
        }

        internal static VALUE rb_define_finalizer(VALUE obj, VALUE block) {
            return ToValue(Call(Constant("ObjectSpace"), "define_finalizer", FromValue(obj), FromValue(block)));
        }

        internal static VALUE rb_undefine_finalizer(VALUE obj) {
            return ToValue(Call(Constant("ObjectSpace"), "undefine_finalizer", FromValue(obj)));
        }

        internal static VALUE rb_backref_get() {
            RubyClosureScope scope = CallerClosureScope();
            if (scope != null) {
                return ToValue(scope.CurrentMatch);
            }
            return ToValue(Call(_context.KernelModule, "eval", MutableString.CreateAscii("$~")));
        }

        internal static void rb_backref_set(VALUE md) {
            // CRuby stores whatever it is given (vm_svar_set); $~ = holds a MatchData or nil
            RubyClosureScope scope = CallerClosureScope();
            if (scope != null) {
                scope.CurrentMatch = FromValue(md) as MatchData;
            }
        }

        internal static VALUE rb_lastline_get() {
            RubyClosureScope scope = CallerClosureScope();
            if (scope != null) {
                return ToValue(scope.LastInputLine);
            }
            return ToValue(Call(_context.KernelModule, "eval", MutableString.CreateAscii("$_")));
        }

        internal static void rb_lastline_set(VALUE str) {
            RubyClosureScope scope = CallerClosureScope();
            if (scope != null) {
                scope.LastInputLine = FromValue(str);
            }
        }

        internal static int rb_memcicmp(void* s1, void* s2, long n) {
            byte* a = (byte*)s1, b = (byte*)s2;
            for (long i = 0; i < n; i++) {
                int x = a[i] >= 'A' && a[i] <= 'Z' ? a[i] + 32 : a[i];
                int y = b[i] >= 'A' && b[i] <= 'Z' ? b[i] + 32 : b[i];
                if (x != y) {
                    return x < y ? -1 : 1;
                }
            }
            return 0;
        }

        #endregion

        #region Mutex, Thread, GC

        internal static VALUE rb_mutex_lock(VALUE mutex) { return CallV(mutex, "lock"); }
        internal static VALUE rb_mutex_unlock(VALUE mutex) { return CallV(mutex, "unlock"); }
        internal static VALUE rb_mutex_trylock(VALUE mutex) { return CallV(mutex, "try_lock"); }
        internal static VALUE rb_mutex_locked_p(VALUE mutex) { return CallV(mutex, "locked?"); }

        internal static VALUE rb_mutex_sleep(VALUE self, VALUE timeout) {
            object t = FromValue(timeout);
            return t == null ? CallV(self, "sleep") : CallV(self, "sleep", t);
        }

        internal static VALUE rb_mutex_synchronize(VALUE mutex, IntPtr func, VALUE arg) {
            rb_mutex_lock(mutex);
            try {
                return CallFunc1(func, arg);
            } finally {
                rb_mutex_unlock(mutex);
            }
        }

        internal static VALUE rb_thread_create(IntPtr f, void* g) {
            var body = Proc.Create(_context, 0, new Func<BlockParam, object, object[], RubyArray, object>(
                (b, self, unused, args) => FromValue(CallFunc1(f, (VALUE)g))));
            return ToValue(Send(Constant("Thread"), "new", body));
        }

        internal static VALUE rb_thread_wakeup(VALUE thread) { return CallV(thread, "wakeup"); }
        internal static VALUE rb_thread_local_aref(VALUE thread, VALUE key) { return CallV(thread, "[]", IdToSymbol(key)); }
        internal static VALUE rb_thread_local_aset(VALUE thread, VALUE key, VALUE val) { return CallV(thread, "[]=", IdToSymbol(key), FromValue(val)); }

        internal static int rb_thread_alone() {
            return ((RubyArray)Call(Constant("Thread"), "list")).Count == 1 ? 1 : 0;
        }

        internal static void rb_gc() { Call(Constant("GC"), "start"); }
        internal static VALUE rb_gc_enable() { return ToValue(Call(Constant("GC"), "enable")); }
        internal static VALUE rb_gc_disable() { return ToValue(Call(Constant("GC"), "disable")); }

        #endregion

        #region Set

        internal static VALUE rb_set_new() { return ToValue(Call(Constant("Set"), "new")); }
        internal static VALUE rb_set_new_capa(nuint capa) { return rb_set_new(); }
        internal static byte rb_set_add(VALUE set, VALUE element) { return Protocols.IsTrue(Call(FromValue(set), "add?", FromValue(element))) ? (byte)1 : (byte)0; }
        internal static byte rb_set_delete(VALUE set, VALUE element) { return Protocols.IsTrue(Call(FromValue(set), "delete?", FromValue(element))) ? (byte)1 : (byte)0; }
        internal static byte rb_set_lookup(VALUE set, VALUE element) { return Protocols.IsTrue(Call(FromValue(set), "include?", FromValue(element))) ? (byte)1 : (byte)0; }
        internal static nuint rb_set_size(VALUE set) { return (nuint)(int)Call(FromValue(set), "size"); }
        internal static VALUE rb_set_clear(VALUE set) { return CallV(set, "clear"); }

        internal static void rb_set_foreach(VALUE set, IntPtr func, VALUE arg) {
            var items = (RubyArray)Call(FromValue(set), "to_a");
            foreach (object item in items) {
                // int (*)(VALUE element, VALUE arg): the foreach trampoline's third argument is unused
                if (CallForeach(func, ToValue(item), arg, Qnil) == ST_STOP) {
                    break;
                }
            }
        }

        #endregion

        #region Arrays, hashes, strings

        internal static VALUE rb_ary_resize(VALUE ary, long len) {
            var array = ToRubyArray(ary);
            array.RequireNotFrozen();
            if (len < 0) {
                len += array.Count;
                if (len < 0) {
                    throw RubyExceptions.CreateIndexError(String.Format("index {0} too small for array; minimum: -{1}", len - array.Count, array.Count));
                }
            }
            while (array.Count > len) {
                array.RemoveAt(array.Count - 1);
            }
            while (array.Count < len) {
                array.Add(null);
            }
            return ary;
        }

        internal static VALUE rb_ary_replace(VALUE copy, VALUE orig) { return CallV(copy, "replace", FromValue(orig)); }
        internal static VALUE rb_hash_fetch(VALUE hash, VALUE key) { return CallV(hash, "fetch", FromValue(key)); }

        internal static VALUE rb_hash_set_ifnone(VALUE hash, VALUE ifnone) {
            Call(FromValue(hash), "default=", FromValue(ifnone));
            return hash;
        }

        internal static ulong rb_hash_start(ulong i) {
            return i * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
        }

        internal static void rb_hash_bulk_insert(long argc, VALUE* argv, VALUE hash) {
            object h = FromValue(hash);
            for (long i = 0; i + 1 < argc; i += 2) {
                Call(h, "[]=", FromValue(argv[i]), FromValue(argv[i + 1]));
            }
        }

        internal static VALUE rb_str_concat(VALUE dst, VALUE src) { return CallV(dst, "concat", FromValue(src)); }
        internal static VALUE rb_str_succ(VALUE orig) { return CallV(orig, "succ"); }
        internal static VALUE rb_str_replace(VALUE dst, VALUE src) { return CallV(dst, "replace", FromValue(src)); }

        internal static VALUE rb_str_ellipsize(VALUE str, long len) {
            var s = ToMutableString(str);
            if (s.GetCharCount() <= len) {
                return str;
            }
            if (len < 3) {
                return CallV(str, "[]", 0, (int)len);
            }
            var head = (MutableString)Call(s, "[]", 0, (int)len - 3);
            return ToValue(Call(head, "+", MutableString.CreateAscii("...")));
        }

        internal static VALUE rb_str_scrub(VALUE str, VALUE repl) {
            object r = FromValue(repl);
            var s = ToMutableString(str);
            if (Protocols.IsTrue(Call(s, "valid_encoding?"))) {
                return Qnil;
            }
            return r == null ? CallV(str, "scrub") : CallV(str, "scrub", r);
        }

        internal static VALUE rb_str_buf_new_cstr(byte* ptr) {
            return rb_str_new_cstr(ptr);
        }

        #endregion

        #region Encodings

        // CRuby's ENC_CODERANGE_7BIT / VALID / BROKEN
        internal static int rb_enc_str_coderange(VALUE str) {
            var s = ToMutableString(str);
            if (s.IsAscii() && s.Encoding.IsAsciiIdentity) {
                return (int)CR_7BIT;
            }
            return Protocols.IsTrue(Call(s, "valid_encoding?")) ? (int)CR_VALID : (int)(CR_7BIT | CR_VALID);
        }

        internal static int rb_enc_str_asciionly_p(VALUE str) {
            return rb_enc_str_coderange(str) == (int)CR_7BIT ? 1 : 0;
        }


        internal static int rb_to_encoding_index(VALUE obj) {
            object o = FromValue(obj);
            if (o is RubyEncoding e) {
                return EncodingIndex(e);
            }
            VALUE name = rb_check_string_type(obj);
            if (name == Qnil) {
                return -1;
            }
            var found = FindEncoding(FromValue(name).ToString());
            return found != null ? EncodingIndex(found) : -1;
        }

        internal static int rb_enc_to_index(void* enc) {
            var e = FromNativeEncoding(enc);
            return e != null ? EncodingIndex(e) : 0;
        }

        internal static VALUE rb_enc_str_new_static(byte* ptr, long len, void* enc) {
            return NewStaticString(ptr, len, FromNativeEncoding(enc) ?? RubyEncoding.Binary);
        }

        internal static int rb_enc_dummy_p(void* enc) {
            var e = FromNativeEncoding(enc);
            return e != null && e.IsDummy ? 1 : 0;
        }

        internal static void* rb_enc_compatible(VALUE str1, VALUE str2) {
            object e = Call(Constant("Encoding"), "compatible?", FromValue(str1), FromValue(str2));
            return e is RubyEncoding re ? NativeEncoding(re) : null;
        }

        internal static void* rb_enc_check(VALUE str1, VALUE str2) {
            void* e = rb_enc_compatible(str1, str2);
            if (e == null) {
                var a = GetEncoding(FromValue(str1));
                var b = GetEncoding(FromValue(str2));
                RaiseNamed("Encoding::CompatibilityError", String.Format("incompatible character encodings: {0} and {1}", a?.Name, b?.Name));
            }
            return e;
        }

        internal static void* rb_default_internal_encoding() { return NativeEncoding(_context.DefaultInternalEncoding); }
        internal static void* rb_default_external_encoding() { return NativeEncoding(_context.DefaultExternalEncoding); }
        internal static void* rb_locale_encoding() { return NativeEncoding(LocaleEncoding()); }
        internal static int rb_locale_encindex() { return EncodingIndex(LocaleEncoding()); }
        internal static void* rb_filesystem_encoding() { return NativeEncoding(_context.GetPathEncoding()); }
        internal static int rb_filesystem_encindex() { return EncodingIndex(_context.GetPathEncoding()); }

        internal static int rb_enc_alias(byte* alias, byte* orig) {
            string name = FromCString(alias);
            var encoding = RubyEncoding.DefineAlias(name, FromCString(orig));
            if (encoding == null) {
                return -1;
            }
            DefineEncodingConstants(name, encoding);
            return EncodingIndex(encoding);
        }

        /// <summary>
        /// The C half of rb_define_dummy_encoding (native/capi.c raises EncodingError on -1):
        /// the new encoding's index, the last of Encoding.list.
        /// </summary>
        internal static int rb_ir_define_dummy_encoding(byte* name) {
            string n = FromCString(name);
            var encoding = RubyEncoding.DefineDummy(n);
            if (encoding == null) {
                return -1;
            }
            DefineEncodingConstants(n, encoding);
            return EncodingIndex(encoding);
        }

        /// <summary>
        /// encoding.c's set_encoding_const: the name as a constant of Encoding when it can be one
        /// (characters that cannot be in a constant name become '_'), and its upper case form.
        /// </summary>
        private static void DefineEncodingConstants(string/*!*/ name, RubyEncoding/*!*/ encoding) {
            if (name.Length == 0 || !Char.IsAsciiLetterUpper(name[0])) {
                return;
            }
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (!Char.IsAsciiLetterOrDigit(chars[i])) {
                    chars[i] = '_';
                }
            }
            var constant = new string(chars);
            var module = (RubyModule)Constant("Encoding");
            foreach (var c in new[] { constant, constant.ToUpperInvariant() }) {
                if (!module.TryGetConstant(null, c, out object _)) {
                    module.SetConstant(c, encoding);
                }
            }
        }

        #endregion

        #region Globals

        internal static void rb_define_global_const(byte* name, VALUE val) {
            rb_define_const(ToValue(_context.ObjectClass), name, val);
        }

        internal static VALUE rb_f_global_variables() {
            return ToValue(Call(_context.KernelModule, "global_variables"));
        }

        internal static VALUE rb_time_num_new(VALUE timev, VALUE off) {
            object t = Call(Constant("Time"), "at", FromValue(timev));
            object o = FromValue(off);
            return ToValue(o == null ? t : Call(t, "localtime", o));
        }

        #endregion

        #region rb_get_kwargs

        /// <summary>
        /// CRuby's rb_get_kwargs: takes the required and optional keywords out of the hash into
        /// values (Qundef for an optional one not given) - with values NULL it only looks; optional
        /// &lt; 0 means other keys are allowed and stay in the hash.
        /// </summary>
        internal static int rb_get_kwargs(VALUE keyword_hash, VALUE* table, int required, int optional, VALUE* values) {
            bool rest = optional < 0;
            if (rest) {
                optional = -1 - optional;
            }
            var hash = FromValue(keyword_hash) as IDictionary<object, object>;
            var missing = new List<object>();
            int found = 0;
            for (int i = 0; i < required + optional; i++) {
                var key = IdToSymbol(table[i]);
                object value;
                if (hash != null && hash.TryGetValue(key, out value)) {
                    if (values != null) {
                        // taken out, as CRuby's extract_kwarg does: what is left over is the rest
                        hash.Remove(key);
                        values[i] = ToValue(value);
                    }
                    found++;
                } else {
                    if (i < required) {
                        missing.Add(key);
                    }
                    if (values != null) {
                        values[i] = Qundef;
                    }
                }
            }
            if (missing.Count > 0) {
                throw RubyExceptions.CreateArgumentError(String.Format("missing keyword{0}: {1}", missing.Count > 1 ? "s" : "",
                    String.Join(", ", missing.ConvertAll(k => ":" + k))));
            }
            if (!rest && hash != null && hash.Count > (values != null ? 0 : found)) {
                var known = new HashSet<object>();
                for (int i = 0; i < required + optional; i++) {
                    known.Add(IdToSymbol(table[i]));
                }
                var unknown = new List<string>();
                foreach (var key in hash.Keys) {
                    if (!known.Contains(key)) {
                        unknown.Add(Inspect(key));
                    }
                }
                if (unknown.Count > 0) {
                    throw RubyExceptions.CreateArgumentError(String.Format("unknown keyword{0}: {1}", unknown.Count > 1 ? "s" : "", String.Join(", ", unknown)));
                }
            }
            return found;
        }

        #endregion
    }
}
