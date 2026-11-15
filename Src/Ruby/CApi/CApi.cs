/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// IronRuby's Ruby C API: what lets a C extension compiled against ruby.h be required.
    /// See README.md next to this file for the design; in short, the native shim
    /// libironruby-capi exports the rb_* functions, most of which call back into the static
    /// methods of this class through a table of function pointers (CApi.Generated.cs).
    ///
    /// The shim's global VALUEs (rb_cObject...) are per process, so the C API belongs to the
    /// first runtime that loads an extension; another runtime in the same process cannot load
    /// one.
    /// </summary>
    internal static unsafe partial class CApi {
        private static readonly object _loadLock = new object();
        private static RubyContext _context;
        private static IntPtr _shim;

        private static delegate* unmanaged[Cdecl]<void*, int, VALUE, int, VALUE*, int*, VALUE> _callMethod;
        private static delegate* unmanaged[Cdecl]<void*, void*, int*, void> _callVoid;
        private static delegate* unmanaged[Cdecl]<void*, VALUE, int*, VALUE> _callFunc1;
        private static delegate* unmanaged[Cdecl]<void*, VALUE, VALUE, int, VALUE*, VALUE, int*, VALUE> _callBlock;
        private static delegate* unmanaged[Cdecl]<void*, VALUE, VALUE, VALUE, int*, int> _callForeach;
        private static delegate* unmanaged[Cdecl]<int*> _pendingAddress;
        private static delegate* unmanaged[Cdecl]<byte*, int, int, int, int, void*> _makeEncoding;
        private static delegate* unmanaged[Cdecl]<byte*, int, IntPtr> _dlopen;
        private static delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> _dlsym;
        private static delegate* unmanaged[Cdecl]<byte*> _dlerror;

        // the shim's per-thread "a managed callee raised" flag, and what it raised
        [ThreadStatic] private static int* _pendingFlag;
        [ThreadStatic] private static Exception _pending;

        internal static RubyContext Context {
            get { return _context; }
        }

        #region Loading

        /// <summary>
        /// Loads a C extension: dlopen it and call Init_&lt;basename&gt;. Called by the loader for a
        /// file that is a native shared object.
        /// </summary>
        internal static void LoadExtension(RubyContext/*!*/ context, string/*!*/ path) {
            lock (_loadLock) {
                Initialize(context);

                IntPtr handle;
                fixed (byte* p = CString(path)) {
                    handle = _dlopen(p, RTLD_NOW | RTLD_GLOBAL);
                }
                if (handle == IntPtr.Zero) {
                    throw RubyExceptions.CreateLoadError(DlError() + " - " + path);
                }

                IntPtr abi;
                fixed (byte* p = CString("rb_ir_abi_version")) {
                    abi = _dlsym(handle, p);
                }
                if (abi == IntPtr.Zero || *(int*)abi != AbiVersion) {
                    throw RubyExceptions.CreateLoadError(
                        "incompatible library: not compiled against IronRuby's ruby.h (a CRuby extension?) - " + path);
                }

                string name = Path.GetFileName(path);
                int dot = name.IndexOf('.');
                if (dot >= 0) {
                    name = name.Substring(0, dot);
                }
                IntPtr init;
                fixed (byte* p = CString("Init_" + name)) {
                    init = _dlsym(handle, p);
                }
                if (init == IntPtr.Zero) {
                    throw RubyExceptions.CreateLoadError("Init_" + name + " is not defined - " + path);
                }

                CallVoid(init, null);
            }
        }

        // rb_ir_abi_version in ruby/internal/ironruby.h
        private const int AbiVersion = 1;

        private const int RTLD_LAZY = 1, RTLD_NOW = 2, RTLD_GLOBAL = 0x100;

        private static void Initialize(RubyContext/*!*/ context) {
            if (_context != null) {
                if (_context != context) {
                    throw RubyExceptions.CreateLoadError(
                        "C extensions can only be loaded by one IronRuby runtime per process, and another one has loaded them");
                }
                return;
            }

            if (!OperatingSystem.IsLinux()) {
                throw RubyExceptions.CreateLoadError("IronRuby's C API is only implemented on Linux so far");
            }

            IntPtr libc;
            if (!NativeLibrary.TryLoad("libc.so.6", out libc) && !NativeLibrary.TryLoad("libdl.so.2", out libc)) {
                throw RubyExceptions.CreateLoadError("cannot find the C library's dlopen");
            }
            _dlopen = (delegate* unmanaged[Cdecl]<byte*, int, IntPtr>)NativeLibrary.GetExport(libc, "dlopen");
            _dlsym = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)NativeLibrary.GetExport(libc, "dlsym");
            _dlerror = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(libc, "dlerror");

            string dir = Path.GetDirectoryName(typeof(CApi).Assembly.Location);
            string shimPath = Path.Combine(dir, "libironruby-capi.so");
            if (!File.Exists(shimPath)) {
                throw RubyExceptions.CreateLoadError(
                    "cannot load a C extension: " + shimPath + " is missing (build it with Util/build-capi.sh)");
            }
            fixed (byte* p = CString(shimPath)) {
                // global: the extensions' undefined rb_* symbols resolve against it
                _shim = _dlopen(p, RTLD_NOW | RTLD_GLOBAL);
            }
            if (_shim == IntPtr.Zero) {
                throw RubyExceptions.CreateLoadError(DlError() + " - " + shimPath);
            }

            _callMethod = (delegate* unmanaged[Cdecl]<void*, int, VALUE, int, VALUE*, int*, VALUE>)ShimExport("rb_ir_call_method");
            _callVoid = (delegate* unmanaged[Cdecl]<void*, void*, int*, void>)ShimExport("rb_ir_call_void");
            _callFunc1 = (delegate* unmanaged[Cdecl]<void*, VALUE, int*, VALUE>)ShimExport("rb_ir_call_func1");
            _callBlock = (delegate* unmanaged[Cdecl]<void*, VALUE, VALUE, int, VALUE*, VALUE, int*, VALUE>)ShimExport("rb_ir_call_block");
            _callForeach = (delegate* unmanaged[Cdecl]<void*, VALUE, VALUE, VALUE, int*, int>)ShimExport("rb_ir_call_foreach");
            _pendingAddress = (delegate* unmanaged[Cdecl]<int*>)ShimExport("rb_ir_pending_address");
            _makeEncoding = (delegate* unmanaged[Cdecl]<byte*, int, int, int, int, void*>)ShimExport("rb_ir_make_encoding");

            int count = *(int*)ShimExport("rb_ir_function_count");
            if (count != FunctionCount) {
                throw RubyExceptions.CreateLoadError(String.Format(
                    "libironruby-capi does not match this IronRuby: it has {0} functions, IronRuby {1} (rebuild it with Util/build-capi.sh)",
                    count, FunctionCount));
            }
            IntPtr* table = (IntPtr*)NativeMemory.Alloc((nuint)(count * sizeof(IntPtr)));
            FillFunctionTable(table);
            ((delegate* unmanaged[Cdecl]<IntPtr*, void>)ShimExport("rb_ir_set_functions"))(table);
            NativeMemory.Free(table);

            _context = context;
            InitializeGlobals(context);
            InitializeThreads();
        }

        private static IntPtr ShimExport(string/*!*/ name) {
            IntPtr result;
            fixed (byte* p = CString(name)) {
                result = _dlsym(_shim, p);
            }
            if (result == IntPtr.Zero) {
                throw RubyExceptions.CreateLoadError("libironruby-capi has no " + name + " (rebuild it with Util/build-capi.sh)");
            }
            return result;
        }

        private static string DlError() {
            byte* e = _dlerror();
            return e != null ? Marshal.PtrToStringUTF8((IntPtr)e) : "dlopen failed";
        }

        /// <summary>
        /// Fills in rb_cObject and the shim's other global VALUEs.
        /// </summary>
        private static void InitializeGlobals(RubyContext/*!*/ context) {
            int count;
            byte* table = (byte*)((delegate* unmanaged[Cdecl]<int*, void*>)ShimExport("rb_ir_global_table"))(&count);
            for (int i = 0; i < count; i++) {
                byte* path = *(byte**)(table + i * 2 * sizeof(IntPtr));
                VALUE* address = *(VALUE**)(table + (i * 2 + 1) * sizeof(IntPtr));
                *address = ToValue(ResolveGlobal(context, Marshal.PtrToStringUTF8((IntPtr)path)));
            }
        }

        private static object ResolveGlobal(RubyContext/*!*/ context, string/*!*/ path) {
            if (path.StartsWith("$", StringComparison.Ordinal)) {
                object value;
                return context.TryGetGlobalVariable(null, path.Substring(1), out value) ? value : null;
            }
            if (path == "\n") {
                return MutableString.CreateAscii("\n").Freeze();
            }
            if (path == "fatal") {
                // CRuby's fatal: an Exception no rescue clause can name, as no constant names it
                object fatal;
                if (context.ObjectClass.TryGetConstant(null, "fatal", out fatal)) {
                    return fatal;
                }
                return context.CreateRubyClass("fatal", (RubyClass)ResolveGlobal(context, "Exception"));
            }
            object current = context.ObjectClass;
            foreach (var name in path.Split(new[] { "::" }, StringSplitOptions.None)) {
                var module = current as RubyModule;
                object next;
                if (module == null || !module.TryGetConstant(null, name, out next)) {
                    return null;
                }
                current = next;
            }
            return current;
        }

        #endregion

        #region Calling C

        /// <summary>
        /// Rethrows what a C function raised: called when a trampoline reports that it unwound.
        /// </summary>
        private static void Rethrow() {
            Exception e = _pending;
            _pending = null;
            if (e == null) {
                throw new InvalidOperationException("a C function unwound without an exception");
            }
            ExceptionDispatchInfo.Capture(e).Throw();
        }

        internal static VALUE CallMethod(IntPtr fn, int arity, VALUE self, int argc, VALUE* argv) {
            int raised;
            var scope = EnterNative();
            VALUE result;
            try {
                result = _callMethod((void*)fn, arity, self, argc, argv, &raised);
            } finally {
                LeaveNative(scope);
            }
            if (raised != 0) {
                Rethrow();
            }
            return result;
        }

        internal static void CallVoid(IntPtr fn, void* arg) {
            int raised;
            var scope = EnterNative();
            try {
                _callVoid((void*)fn, arg, &raised);
            } finally {
                LeaveNative(scope);
            }
            if (raised != 0) {
                Rethrow();
            }
        }

        internal static VALUE CallFunc1(IntPtr fn, VALUE arg) {
            int raised;
            var scope = EnterNative();
            VALUE result;
            try {
                result = _callFunc1((void*)fn, arg, &raised);
            } finally {
                LeaveNative(scope);
            }
            if (raised != 0) {
                Rethrow();
            }
            return result;
        }

        internal static VALUE CallBlock(IntPtr fn, VALUE yielded, VALUE data, int argc, VALUE* argv, VALUE blockArg) {
            int raised;
            var scope = EnterNative();
            VALUE result;
            try {
                result = _callBlock((void*)fn, yielded, data, argc, argv, blockArg, &raised);
            } finally {
                LeaveNative(scope);
            }
            if (raised != 0) {
                Rethrow();
            }
            return result;
        }

        internal static int CallForeach(IntPtr fn, VALUE key, VALUE value, VALUE arg) {
            int raised;
            var scope = EnterNative();
            int result;
            try {
                result = _callForeach((void*)fn, key, value, arg, &raised);
            } finally {
                LeaveNative(scope);
            }
            if (raised != 0) {
                Rethrow();
            }
            return result;
        }

        #endregion

        #region Called from C

        /// <summary>
        /// Records what a managed function called from C threw; its forwarder unwinds to the
        /// innermost trampoline, which rethrows it in managed code.
        /// </summary>
        private static void SetPending(Exception/*!*/ e) {
            _pending = e;
            int* flag = _pendingFlag;
            if (flag == null) {
                _pendingFlag = flag = _pendingAddress();
            }
            *flag = 1;
        }

        #endregion

        #region Helpers

        internal static byte[]/*!*/ CString(string/*!*/ str) {
            int n = System.Text.Encoding.UTF8.GetByteCount(str);
            var bytes = new byte[n + 1];
            System.Text.Encoding.UTF8.GetBytes(str, 0, str.Length, bytes, 0);
            return bytes;
        }

        internal static string/*!*/ FromCString(byte* str) {
            if (str == null) {
                throw RubyExceptions.CreateArgumentError("NULL pointer given");
            }
            return Marshal.PtrToStringUTF8((IntPtr)str);
        }

        internal static byte[]/*!*/ CBytes(byte* str) {
            if (str == null) {
                throw RubyExceptions.CreateArgumentError("NULL pointer given");
            }
            int n = 0;
            while (str[n] != 0) {
                n++;
            }
            return new ReadOnlySpan<byte>(str, n).ToArray();
        }

        internal static byte[]/*!*/ Bytes(byte* ptr, long len) {
            if (len < 0) {
                throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
            }
            if (ptr == null) {
                return new byte[len];
            }
            return new ReadOnlySpan<byte>(ptr, checked((int)len)).ToArray();
        }

        #endregion
    }
}
