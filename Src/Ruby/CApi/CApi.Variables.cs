/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// Time (whose functions pass struct timeval and timespec by value), class variables, global
    /// variables backed by C, fibers, and a few hash and proc functions.
    /// </summary>
    internal static unsafe partial class CApi {

        #region Time

        [StructLayout(LayoutKind.Sequential)]
        internal struct TimeVal {
            // both C longs: 32-bit on Windows
            public CLong sec, usec;
            public long tv_sec { get { return (long)sec.Value; } set { sec = new CLong(checked((nint)value)); } }
            public long tv_usec { get { return (long)usec.Value; } set { usec = new CLong((nint)value); } }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TimeSpec {
            public long tv_sec;    // time_t: 64-bit on both
            public CLong nsec;
            public long tv_nsec { get { return (long)nsec.Value; } set { nsec = new CLong((nint)value); } }
        }

        internal static VALUE rb_time_new(long sec, long usec) {
            return ToValue(Call(Constant("Time"), "at", Normalize(sec), Normalize(usec), Sym("usec")));
        }

        internal static VALUE rb_time_nano_new(long sec, long nsec) {
            return ToValue(Call(Constant("Time"), "at", Normalize(sec), Normalize(nsec), Sym("nsec")));
        }

        // offset INT_MAX: local time; INT_MAX - 1: UTC; otherwise seconds east of UTC
        internal static VALUE rb_time_timespec_new(TimeSpec* ts, int offset) {
            object t = Call(Constant("Time"), "at", Normalize(ts->tv_sec), Normalize(ts->tv_nsec), Sym("nsec"));
            if (offset == Int32.MaxValue) {
                return ToValue(t);
            }
            if (offset == Int32.MaxValue - 1) {
                return ToValue(Call(t, "utc"));
            }
            if (offset <= -86400 || offset >= 86400) {
                throw RubyExceptions.CreateArgumentError("utc_offset out of range");
            }
            return ToValue(Call(t, "localtime", offset));
        }

        private static void TimeParts(object obj, bool interval, out long sec, out long nsec) {
            if (_context.GetClassOf(obj).GetNonSingletonClass() == Constant("Time")) {
                if (interval) {
                    throw RubyExceptions.CreateTypeError("can't convert Time into time interval");
                }
                sec = Convert.ToInt64(Call(obj, "to_i"));
                nsec = Convert.ToInt64(Call(obj, "nsec"));
                return;
            }
            var i = AsInteger(obj);
            if (i.HasValue) {
                if (interval && i.Value < 0) {
                    throw RubyExceptions.CreateArgumentError("time interval must not be negative");
                }
                sec = (long)i.Value;
                nsec = 0;
                return;
            }
            if (obj is double d) {
                if (interval && d < 0) {
                    throw RubyExceptions.CreateArgumentError("time interval must not be negative");
                }
                double f = Math.Floor(d);
                sec = (long)f;
                nsec = (long)Math.Round((d - f) * 1e9);
                return;
            }
            if (obj == null || obj is bool || obj is MutableString || !RespondTo(obj, "divmod")) {
                throw RubyExceptions.CreateTypeError(String.Format("can't convert {0} into time{1}", ClassName(obj), interval ? " interval" : ""));
            }
            if (interval && Protocols.IsTrue(Call(obj, "<", 0))) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }
            var parts = (RubyArray)Call(obj, "divmod", 1);
            sec = Convert.ToInt64(parts[0]);
            nsec = Convert.ToInt64(Call(Call(parts[1], "*", 1000000000), "to_i"));
        }

        internal static TimeVal rb_time_interval(VALUE num) {
            long sec, nsec;
            TimeParts(FromValue(num), true, out sec, out nsec);
            return new TimeVal { tv_sec = sec, tv_usec = nsec / 1000 };
        }

        internal static TimeVal rb_time_timeval(VALUE time) {
            long sec, nsec;
            TimeParts(FromValue(time), false, out sec, out nsec);
            return new TimeVal { tv_sec = sec, tv_usec = nsec / 1000 };
        }

        internal static TimeSpec rb_time_timespec(VALUE time) {
            long sec, nsec;
            TimeParts(FromValue(time), false, out sec, out nsec);
            return new TimeSpec { tv_sec = sec, tv_nsec = nsec };
        }

        internal static void rb_timespec_now(TimeSpec* ts) {
            long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
            ts->tv_sec = ticks / TimeSpan.TicksPerSecond;
            ts->tv_nsec = (ticks % TimeSpan.TicksPerSecond) * 100;
        }

        #endregion

        #region Class variables

        private static RubySymbol/*!*/ ClassVariableName(string/*!*/ name) {
            return _context.EncodeIdentifier(name.StartsWith("@@", StringComparison.Ordinal) ? name : "@@" + name);
        }

        internal static VALUE rb_cvar_get(VALUE klass, VALUE name) { return CallV(klass, "class_variable_get", IdToSymbol(name)); }
        internal static void rb_cvar_set(VALUE klass, VALUE name, VALUE val) { Call(FromValue(klass), "class_variable_set", IdToSymbol(name), FromValue(val)); }
        internal static VALUE rb_cvar_defined(VALUE klass, VALUE name) { return CallV(klass, "class_variable_defined?", IdToSymbol(name)); }
        internal static VALUE rb_cv_get(VALUE klass, byte* name) { return CallV(klass, "class_variable_get", ClassVariableName(FromCString(name))); }
        internal static void rb_cv_set(VALUE klass, byte* name, VALUE val) { Call(FromValue(klass), "class_variable_set", ClassVariableName(FromCString(name)), FromValue(val)); }

        internal static void rb_define_class_variable(VALUE klass, byte* name, VALUE val) {
            string n = FromCString(name);
            if (!n.StartsWith("@@", StringComparison.Ordinal)) {
                throw RubyExceptions.CreateNameError(String.Format("wrong class variable name {0}", n));
            }
            Call(FromValue(klass), "class_variable_set", ClassVariableName(n), FromValue(val));
        }

        #endregion

        #region Global variables

        private static delegate* unmanaged[Cdecl]<void*, VALUE, VALUE*, int*, VALUE> _callGvarGet;
        private static delegate* unmanaged[Cdecl]<void*, VALUE, VALUE, VALUE*, int*, void> _callGvarSet;

        /// <summary>
        /// A global variable an extension defined: its value in a C variable, or computed by C
        /// functions (getter(id, data) and setter(value, id, data), CRuby's rb_gvar_getter_t...).
        /// </summary>
        private sealed class CGlobalVariable : Runtime.GlobalVariable {
            public string Name;
            public VALUE* Var;
            public IntPtr Getter, Setter;
            public bool ReadOnly;

            public override object GetValue(RubyContext/*!*/ context, RubyScope scope) {
                if (Getter == IntPtr.Zero) {
                    return Var != null ? FromValue(*Var) : null;
                }
                int raised;
                var depth = EnterNative();
                VALUE result;
                try {
                    result = _callGvarGet((void*)Getter, Intern("$" + Name), Var, &raised);
                } finally {
                    LeaveNative(depth);
                }
                if (raised != 0) {
                    Rethrow();
                }
                return FromValue(result);
            }

            public override void SetValue(RubyContext/*!*/ context, RubyScope scope, string/*!*/ name, object value) {
                if (ReadOnly) {
                    throw RubyExceptions.CreateNameError(String.Format("${0} is a read-only variable", Name));
                }
                if (Setter == IntPtr.Zero) {
                    if (Var != null) {
                        *Var = ToValue(value);
                    }
                    return;
                }
                int raised;
                var depth = EnterNative();
                try {
                    _callGvarSet((void*)Setter, ToValue(value), Intern("$" + Name), Var, &raised);
                } finally {
                    LeaveNative(depth);
                }
                if (raised != 0) {
                    Rethrow();
                }
            }
        }

        private static void DefineGlobal(byte* name, VALUE* var, IntPtr getter, IntPtr setter, bool readOnly) {
            if (_callGvarGet == null) {
                _callGvarGet = (delegate* unmanaged[Cdecl]<void*, VALUE, VALUE*, int*, VALUE>)ShimExport("rb_ir_call_gvar_get");
                _callGvarSet = (delegate* unmanaged[Cdecl]<void*, VALUE, VALUE, VALUE*, int*, void>)ShimExport("rb_ir_call_gvar_set");
            }
            string n = FromCString(name);
            if (n.StartsWith("$", StringComparison.Ordinal)) {
                n = n.Substring(1);
            }
            _context.DefineGlobalVariable(n, new CGlobalVariable { Name = n, Var = var, Getter = getter, Setter = setter, ReadOnly = readOnly });
        }

        internal static void rb_define_variable(byte* name, VALUE* var) {
            DefineGlobal(name, var, IntPtr.Zero, IntPtr.Zero, false);
        }

        internal static void rb_define_readonly_variable(byte* name, VALUE* var) {
            DefineGlobal(name, var, IntPtr.Zero, IntPtr.Zero, true);
        }

        internal static void rb_define_hooked_variable(byte* name, VALUE* var, void* getter, void* setter) {
            DefineGlobal(name, var, (IntPtr)getter, (IntPtr)setter, false);
        }

        // no setter: read-only
        internal static void rb_define_virtual_variable(byte* name, void* getter, void* setter) {
            DefineGlobal(name, null, (IntPtr)getter, (IntPtr)setter, setter == null);
        }

        #endregion

        #region Fibers

        internal static VALUE rb_fiber_new(IntPtr func, VALUE callback_obj) {
            return ToValue(Send(Constant("Fiber"), "new", MakeBlock(func, callback_obj)));
        }

        internal static VALUE rb_fiber_resume(VALUE fiber, int argc, VALUE* argv) { return CallV(fiber, "resume", FromValues(argc, argv)); }
        internal static VALUE rb_fiber_yield(int argc, VALUE* argv) { return ToValue(Call(Constant("Fiber"), "yield", FromValues(argc, argv))); }
        internal static VALUE rb_fiber_current() { return ToValue(Call(Constant("Fiber"), "current")); }
        internal static VALUE rb_fiber_alive_p(VALUE fiber) { return CallV(fiber, "alive?"); }
        internal static VALUE rb_fiber_raise(VALUE fiber, int argc, VALUE* argv) { return CallV(fiber, "raise", FromValues(argc, argv)); }

        #endregion

        #region Hashes, procs, catch

        internal static VALUE rb_ident_hash_new() {
            return ToValue(Call(Send(HashClass(), "new", null), "compare_by_identity"));
        }

        internal static VALUE rb_hash_new_capa(long capa) { return rb_hash_new(); }
        internal static VALUE rb_hash(VALUE obj) { return CallV(obj, "hash"); }
        internal static VALUE rb_hash_freeze(VALUE obj) { return rb_obj_freeze(obj); }

        internal static VALUE rb_hash_delete_if(VALUE hash) {
            return ToValue(Send(FromValue(hash), "delete_if", _frame?.Block));
        }

        internal static VALUE rb_proc_call_kw(VALUE recv, VALUE args, int kw_splat) {
            return rb_proc_call(recv, args);
        }

        internal static VALUE rb_proc_call_with_block_kw(VALUE recv, int argc, VALUE* argv, VALUE proc, int kw_splat) {
            return rb_proc_call_with_block(recv, argc, argv, proc);
        }

        internal static VALUE rb_catch(byte* tag, IntPtr func, VALUE data) {
            return ToValue(Send(_context.KernelModule, "catch", MakeBlock(func, data), _context.EncodeIdentifier(FromCString(tag))));
        }

        #endregion
    }
}
