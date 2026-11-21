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

                // A CRuby-built extension reads object memory directly and would crash: on Unix our
                // headers mark an extension (rb_ir_abi_version), on Windows it imports our DLL.
                if (IsWindows && !PeImportsShim(path)) {
                    throw IncompatibleExtension(path);
                }

                string error;
                IntPtr handle = OpenLibrary(path, true, out error);
                if (handle == IntPtr.Zero) {
                    throw RubyExceptions.CreateLoadError(error + " - " + path);
                }

                if (!IsWindows) {
                    IntPtr abi = Symbol(handle, "rb_ir_abi_version");
                    if (abi == IntPtr.Zero || *(int*)abi != AbiVersion) {
                        throw IncompatibleExtension(path);
                    }
                }

                string name = Path.GetFileName(path);
                int dot = name.IndexOf('.');
                if (dot >= 0) {
                    name = name.Substring(0, dot);
                }
                IntPtr init = Symbol(handle, "Init_" + name);
                if (init == IntPtr.Zero) {
                    throw RubyExceptions.CreateLoadError("Init_" + name + " is not defined - " + path);
                }

                CallInit(path, init);
            }
        }

        // rb_ir_abi_version in ruby/internal/ironruby.h
        private const int AbiVersion = 1;

        private const int RTLD_LAZY = 1, RTLD_NOW = 2, RTLD_GLOBAL = 0x100;

        private static readonly string ShimName = IsWindows ? "libironruby-capi.dll" : "libironruby-capi.so";

        private static Exception/*!*/ IncompatibleExtension(string/*!*/ path) {
            return RubyExceptions.CreateLoadError(
                "incompatible library: not compiled against IronRuby's ruby.h (a CRuby extension?) - " + path);
        }

        /// <summary>
        /// dlopen (RTLD_GLOBAL for the shim, whose symbols the extensions resolve against) or, on
        /// Windows, LoadLibrary: there an extension imports the shim by name, and the loader binds
        /// it to the copy already loaded.
        /// </summary>
        private static IntPtr OpenLibrary(string/*!*/ path, bool global, out string error) {
            error = null;
            if (IsWindows) {
                try {
                    return NativeLibrary.Load(path);
                } catch (Exception e) {
                    error = e.Message;
                    return IntPtr.Zero;
                }
            }
            IntPtr handle;
            fixed (byte* p = CString(path)) {
                handle = _dlopen(p, RTLD_NOW | (global ? RTLD_GLOBAL : 0));
            }
            if (handle == IntPtr.Zero) {
                error = DlError();
            }
            return handle;
        }

        private static IntPtr Symbol(IntPtr library, string/*!*/ name) {
            if (IsWindows) {
                IntPtr address;
                return NativeLibrary.TryGetExport(library, name, out address) ? address : IntPtr.Zero;
            }
            fixed (byte* p = CString(name)) {
                return _dlsym(library, p);
            }
        }

        /// <summary>
        /// Whether a PE DLL imports libironruby-capi.dll: what an extension linked by mkmf against
        /// IronRuby's RbConfig does, and one built for CRuby (importing x64-ucrt-ruby400.dll) does not.
        /// </summary>
        private static bool PeImportsShim(string/*!*/ path) {
            try {
                using (var stream = File.OpenRead(path))
                using (var pe = new System.Reflection.PortableExecutable.PEReader(stream)) {
                    var headers = pe.PEHeaders;
                    var dir = headers.PEHeader.ImportTableDirectory;
                    if (dir.Size == 0) {
                        return false;
                    }
                    var image = pe.GetEntireImage();
                    int RvaToOffset(int rva) {
                        foreach (var section in headers.SectionHeaders) {
                            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData)) {
                                return rva - section.VirtualAddress + section.PointerToRawData;
                            }
                        }
                        return -1;
                    }
                    var bytes = image.GetContent();
                    for (int offset = RvaToOffset(dir.RelativeVirtualAddress); offset >= 0 && offset + 20 <= bytes.Length; offset += 20) {
                        int nameRva = BitConverter.ToInt32(bytes.AsSpan(offset + 12, 4));
                        if (nameRva == 0) {
                            break;
                        }
                        int nameOffset = RvaToOffset(nameRva);
                        if (nameOffset < 0) {
                            continue;
                        }
                        int end = nameOffset;
                        while (end < bytes.Length && bytes[end] != 0) {
                            end++;
                        }
                        string dll = System.Text.Encoding.ASCII.GetString(bytes.AsSpan(nameOffset, end - nameOffset));
                        if (String.Equals(dll, ShimName, StringComparison.OrdinalIgnoreCase)) {
                            return true;
                        }
                    }
                }
            } catch (Exception) {
                // not a PE file we can read: not ours
            }
            return false;
        }

        private static void Initialize(RubyContext/*!*/ context) {
            if (_context != null) {
                if (_context != context) {
                    throw RubyExceptions.CreateLoadError(
                        "C extensions can only be loaded by one IronRuby runtime per process, and another one has loaded them");
                }
                return;
            }

            bool supported = RuntimeInformation.ProcessArchitecture == Architecture.X64 && (OperatingSystem.IsLinux() || IsWindows);
            if (!supported) {
                throw RubyExceptions.CreateLoadError("IronRuby's C API is only implemented on Linux and Windows x64 so far");
            }

            if (!IsWindows) {
                IntPtr libc;
                if (!NativeLibrary.TryLoad("libc.so.6", out libc) && !NativeLibrary.TryLoad("libdl.so.2", out libc)) {
                    throw RubyExceptions.CreateLoadError("cannot find the C library's dlopen");
                }
                _dlopen = (delegate* unmanaged[Cdecl]<byte*, int, IntPtr>)NativeLibrary.GetExport(libc, "dlopen");
                _dlsym = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)NativeLibrary.GetExport(libc, "dlsym");
                _dlerror = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(libc, "dlerror");
            }

            string dir = Path.GetDirectoryName(typeof(CApi).Assembly.Location);
            string shimPath = Path.Combine(dir, ShimName);
            if (!File.Exists(shimPath)) {
                throw RubyExceptions.CreateLoadError(
                    "cannot load a C extension: " + shimPath + " is missing (build it with Util/build-capi.sh)");
            }
            // global: on Unix the extensions' undefined rb_* symbols resolve against it
            string error;
            _shim = OpenLibrary(shimPath, true, out error);
            if (_shim == IntPtr.Zero) {
                throw RubyExceptions.CreateLoadError(error + " - " + shimPath);
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
            _defaultAllocator = ShimExport("rb_ir_default_allocator");
        }

        internal static IntPtr ShimExport(string/*!*/ name) {
            IntPtr result = Symbol(_shim, name);
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
            var variables = new List<KeyValuePair<string, IntPtr>>();
            for (int i = 0; i < count; i++) {
                byte* path = *(byte**)(table + i * 2 * sizeof(IntPtr));
                VALUE* address = *(VALUE**)(table + (i * 2 + 1) * sizeof(IntPtr));
                string name = Marshal.PtrToStringUTF8((IntPtr)path);
                *address = ToValue(ResolveGlobal(context, name));
                if (name.StartsWith("$", StringComparison.Ordinal)) {
                    variables.Add(new KeyValuePair<string, IntPtr>(name, (IntPtr)address));
                }
            }
            // rb_stdout, rb_rs and the like are copies: what Ruby code assigns to $stdout or $/
            // is copied over, as in CRuby, where the C global is the variable's own storage
            RubyContext.StandardGlobalsChanged = changed => {
                if (changed != context) {
                    return;
                }
                foreach (var variable in variables) {
                    *(VALUE*)variable.Value = ToValue(ResolveGlobal(context, variable.Key));
                }
            };
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
        internal static void Rethrow() {
            Exception e = _pending;
            _pending = null;
            if (e == null) {
                throw new InvalidOperationException("a C function unwound without an exception");
            }
            ExceptionDispatchInfo.Capture(e).Throw();
        }

        internal static VALUE CallMethod(IntPtr fn, int arity, VALUE self, int argc, VALUE* argv, CCode code = null) {
            int raised;
            var scope = EnterNative(code);
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

        internal static void CallVoid(IntPtr fn, void* arg, CCode code = null) {
            int raised;
            var scope = EnterNative(code);
            try {
                _callVoid((void*)fn, arg, &raised);
            } finally {
                LeaveNative(scope);
            }
            if (raised != 0) {
                Rethrow();
            }
        }

        internal static VALUE CallFunc1(IntPtr fn, VALUE arg, CCode code = null) {
            int raised;
            var scope = EnterNative(code);
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

        internal static VALUE CallBlock(IntPtr fn, VALUE yielded, VALUE data, int argc, VALUE* argv, VALUE blockArg, CCode code = null) {
            int raised;
            var scope = EnterNative(code);
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
