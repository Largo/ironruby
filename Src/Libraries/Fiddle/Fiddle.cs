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
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Fiddle {

    /// <summary>
    /// The unmanaged half of Fiddle.  Fiddle::Function, Fiddle::Pointer and the rest of
    /// the user-visible API are written in Ruby, in Src/StdLib/ruby/4.0/fiddle.rb; this
    /// module is what they call to reach native code.
    ///
    /// A foreign call is made by emitting a DynamicMethod whose body is a single `calli`
    /// with the requested signature - the same technique the (32 bit only) Win32API
    /// library uses - and caching it per signature.  The stub takes the target address
    /// and an object[] of already boxed arguments, so one delegate type covers every
    /// signature and no delegate marshalling is involved.
    /// </summary>
    [RubyModule("Fiddle")]
    public static class Fiddle {

        [RubyModule("Native")]
        public static class Native {

            #region Type codes

            // Must agree with the TYPE_* constants in fiddle.rb.
            private const int TYPE_VOID = 0;
            private const int TYPE_VOIDP = 1;
            private const int TYPE_CHAR = 2;
            private const int TYPE_SHORT = 3;
            private const int TYPE_INT = 4;
            private const int TYPE_LONG = 5;
            private const int TYPE_LONG_LONG = 6;
            private const int TYPE_FLOAT = 7;
            private const int TYPE_DOUBLE = 8;

            private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            private static Type/*!*/ NativeType(int code) {
                switch (code) {
                    case TYPE_VOID: return typeof(void);
                    case TYPE_VOIDP: return typeof(IntPtr);
                    case TYPE_CHAR: return typeof(sbyte);
                    case -TYPE_CHAR: return typeof(byte);
                    case TYPE_SHORT: return typeof(short);
                    case -TYPE_SHORT: return typeof(ushort);
                    case TYPE_INT: return typeof(int);
                    case -TYPE_INT: return typeof(uint);
                    // C's long is 64 bit everywhere but on Windows (LLP64).
                    case TYPE_LONG: return (IntPtr.Size == 8 && !IsWindows) ? typeof(long) : typeof(int);
                    case -TYPE_LONG: return (IntPtr.Size == 8 && !IsWindows) ? typeof(ulong) : typeof(uint);
                    case TYPE_LONG_LONG: return typeof(long);
                    case -TYPE_LONG_LONG: return typeof(ulong);
                    case TYPE_FLOAT: return typeof(float);
                    case TYPE_DOUBLE: return typeof(double);
                }
                throw RubyExceptions.CreateTypeError("unknown Fiddle type {0}", code);
            }

            #endregion

            #region Calli stubs

            private delegate object Stub(IntPtr function, object[]/*!*/ args);

            private static readonly Dictionary<string, Stub>/*!*/ _stubs = new Dictionary<string, Stub>();
            private static readonly object/*!*/ _stubLock = new object();
            private static ModuleBuilder _dynamicModule;

            private static ModuleBuilder/*!*/ DynamicModule {
                get {
                    if (_dynamicModule == null) {
                        var name = new AssemblyName("IronRuby.StandardLibrary.Fiddle.DynamicAssembly");
                        var assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
                        _dynamicModule = assembly.DefineDynamicModule(name.Name);
                    }
                    return _dynamicModule;
                }
            }

            private static Stub/*!*/ GetStub(string/*!*/ key, Type/*!*/ returnType, Type[]/*!*/ parameterTypes) {
                lock (_stubLock) {
                    Stub stub;
                    if (_stubs.TryGetValue(key, out stub)) {
                        return stub;
                    }

                    var dm = new DynamicMethod(
                        "fiddle_calli", typeof(object), new[] { typeof(IntPtr), typeof(object[]) }, DynamicModule, true
                    );

                    var il = dm.GetILGenerator();
                    for (int i = 0; i < parameterTypes.Length; i++) {
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldc_I4, i);
                        il.Emit(OpCodes.Ldelem_Ref);
                        il.Emit(OpCodes.Unbox_Any, parameterTypes[i]);
                    }
                    il.Emit(OpCodes.Ldarg_0);
                    il.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, returnType, parameterTypes);
                    if (returnType == typeof(void)) {
                        il.Emit(OpCodes.Ldnull);
                    } else {
                        il.Emit(OpCodes.Box, returnType);
                    }
                    il.Emit(OpCodes.Ret);

                    stub = (Stub)dm.CreateDelegate(typeof(Stub));
                    _stubs.Add(key, stub);
                    return stub;
                }
            }

            #endregion

            #region Conversions

            private static long ToInt64(object value) {
                if (value is int) {
                    return (int)value;
                }
                if (value is long) {
                    return (long)value;
                }
                if (value is BigInteger) {
                    return (long)(BigInteger)value;
                }
                if (value == null) {
                    return 0;
                }
                if (value is bool) {
                    return (bool)value ? 1 : 0;
                }
                if (value is double) {
                    return (long)(double)value;
                }
                throw RubyExceptions.CreateTypeError("cannot convert {0} to a native integer",
                    value.GetType().Name);
            }

            private static double ToDouble(object value) {
                if (value is double) {
                    return (double)value;
                }
                if (value is int) {
                    return (int)value;
                }
                if (value is long) {
                    return (long)value;
                }
                if (value is BigInteger) {
                    return (double)(BigInteger)value;
                }
                if (value == null) {
                    return 0.0;
                }
                throw RubyExceptions.CreateTypeError("cannot convert {0} to a native float",
                    value.GetType().Name);
            }

            /// <summary>
            /// A String passed where a pointer is expected.  The callee gets a pinned
            /// copy with one NUL byte appended - C code reading a char* needs the
            /// terminator, and MutableString does not keep one - and whatever the callee
            /// wrote is copied back afterwards, so an output buffer (getrusage, read,
            /// ...) still updates the Ruby string in place.
            /// </summary>
            private struct StringBuffer {
                internal MutableString/*!*/ String;
                internal byte[]/*!*/ Bytes;
                internal int Count;
            }

            /// <summary>
            /// Boxes one Ruby argument as the CLR value the calli stub will unbox.
            /// </summary>
            private static object/*!*/ MarshalArgument(Type/*!*/ type, object value,
                List<GCHandle>/*!*/ pins, List<StringBuffer>/*!*/ buffers) {

                if (type == typeof(IntPtr)) {
                    var str = value as MutableString;
                    if (str != null) {
                        int count = str.GetByteCount();
                        var bytes = new byte[count + 1];
#pragma warning disable 618 // RubyOps.GetMutableStringBytes is "internal only"
                        Array.Copy(RubyOps.GetMutableStringBytes(str), bytes, count);
#pragma warning restore 618
                        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                        pins.Add(handle);
                        if (!str.IsFrozen) {
                            buffers.Add(new StringBuffer { String = str, Bytes = bytes, Count = count });
                        }
                        return handle.AddrOfPinnedObject();
                    }
                    return new IntPtr(ToInt64(value));
                }
                if (type == typeof(double)) {
                    return ToDouble(value);
                }
                if (type == typeof(float)) {
                    return (float)ToDouble(value);
                }

                long n = ToInt64(value);
                if (type == typeof(sbyte)) return unchecked((sbyte)n);
                if (type == typeof(byte)) return unchecked((byte)n);
                if (type == typeof(short)) return unchecked((short)n);
                if (type == typeof(ushort)) return unchecked((ushort)n);
                if (type == typeof(int)) return unchecked((int)n);
                if (type == typeof(uint)) return unchecked((uint)n);
                if (type == typeof(ulong)) return unchecked((ulong)n);
                return n;
            }

            private static object MarshalResult(Type/*!*/ type, object value) {
                if (type == typeof(void)) return null;
                if (type == typeof(IntPtr)) return Protocols.Normalize(((IntPtr)value).ToInt64());
                if (type == typeof(double)) return value;
                if (type == typeof(float)) return (double)(float)value;
                if (type == typeof(sbyte)) return (int)(sbyte)value;
                if (type == typeof(byte)) return (int)(byte)value;
                if (type == typeof(short)) return (int)(short)value;
                if (type == typeof(ushort)) return (int)(ushort)value;
                if (type == typeof(int)) return value;
                if (type == typeof(uint)) return Protocols.Normalize((uint)value);
                if (type == typeof(ulong)) return Protocols.Normalize((ulong)value);
                return Protocols.Normalize((long)value);
            }

            #endregion

            #region Ruby API

            /// <summary>
            /// Calls the function at +address+ with the given Fiddle type codes.
            /// Arguments must already be Integer, Float, String or nil - Fiddle::Function
            /// does the Ruby-level coercion (Pointer#to_i, Integer(), Float()) itself.
            /// </summary>
            [RubyMethod("invoke", RubyMethodAttributes.PublicSingleton)]
            public static object Invoke(RubyModule/*!*/ self, object address, [NotNull]IList/*!*/ argTypes,
                object returnTypeCode, [NotNull]IList/*!*/ args) {

                if (argTypes.Count != args.Count) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected {1})",
                        args.Count, argTypes.Count);
                }

                IntPtr function = new IntPtr(ToInt64(address));
                if (function == IntPtr.Zero) {
                    throw RubyExceptions.CreateRuntimeError("NULL function pointer");
                }

                int returnCode = (int)ToInt64(returnTypeCode);
                Type returnType = NativeType(returnCode);
                var parameterTypes = new Type[argTypes.Count];
                var key = new System.Text.StringBuilder();
                key.Append(returnCode);
                for (int i = 0; i < parameterTypes.Length; i++) {
                    int code = (int)ToInt64(argTypes[i]);
                    parameterTypes[i] = NativeType(code);
                    key.Append(',').Append(code);
                }

                var stub = GetStub(key.ToString(), returnType, parameterTypes);

                var pins = new List<GCHandle>();
                var buffers = new List<StringBuffer>();
                try {
                    var boxed = new object[parameterTypes.Length];
                    for (int i = 0; i < boxed.Length; i++) {
                        boxed[i] = MarshalArgument(parameterTypes[i], args[i], pins, buffers);
                    }
                    object result = MarshalResult(returnType, stub(function, boxed));
                    for (int i = 0; i < buffers.Count; i++) {
                        var buffer = buffers[i];
                        buffer.String.Write(0, buffer.Bytes, 0, buffer.Count);
                    }
                    return result;
                } finally {
                    for (int i = 0; i < pins.Count; i++) {
                        pins[i].Free();
                    }
                }
            }

            /// <summary>
            /// malloc(3).  Fiddle::Pointer owns the result.
            /// </summary>
            [RubyMethod("malloc", RubyMethodAttributes.PublicSingleton)]
            public static object Malloc(RubyModule/*!*/ self, [DefaultProtocol]int size) {
                if (size < 0) {
                    throw RubyExceptions.CreateArgumentError("negative allocation size");
                }
                IntPtr p = Marshal.AllocHGlobal(size == 0 ? 1 : size);
                for (int i = 0; i < size; i++) {
                    Marshal.WriteByte(p, i, 0);
                }
                return Protocols.Normalize(p.ToInt64());
            }

            [RubyMethod("realloc", RubyMethodAttributes.PublicSingleton)]
            public static object Realloc(RubyModule/*!*/ self, object address, [DefaultProtocol]int size) {
                IntPtr p = new IntPtr(ToInt64(address));
                return Protocols.Normalize(Marshal.ReAllocHGlobal(p, new IntPtr(size)).ToInt64());
            }

            [RubyMethod("free", RubyMethodAttributes.PublicSingleton)]
            public static object Free(RubyModule/*!*/ self, object address) {
                IntPtr p = new IntPtr(ToInt64(address));
                if (p != IntPtr.Zero) {
                    Marshal.FreeHGlobal(p);
                }
                return null;
            }

            /// <summary>
            /// Reads +length+ bytes from +address+ into a new binary String.
            /// </summary>
            [RubyMethod("read", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Read(RubyModule/*!*/ self, object address, [DefaultProtocol]int length) {
                if (length < 0) {
                    throw RubyExceptions.CreateArgumentError("negative length");
                }
                var bytes = new byte[length];
                if (length > 0) {
                    Marshal.Copy(new IntPtr(ToInt64(address)), bytes, 0, length);
                }
                return MutableString.CreateBinary(bytes);
            }

            [RubyMethod("write", RubyMethodAttributes.PublicSingleton)]
            public static object Write(RubyModule/*!*/ self, object address, [NotNull]MutableString/*!*/ value) {
                byte[] bytes = value.ToByteArray();
                if (bytes.Length > 0) {
                    Marshal.Copy(bytes, 0, new IntPtr(ToInt64(address)), bytes.Length);
                }
                return null;
            }

            /// <summary>
            /// strlen(3) on a native pointer, used by Fiddle::Pointer#to_s and
            /// Fiddle::Function's TYPE_CONST_STRING return values.
            /// </summary>
            [RubyMethod("strlen", RubyMethodAttributes.PublicSingleton)]
            public static object Strlen(RubyModule/*!*/ self, object address) {
                IntPtr p = new IntPtr(ToInt64(address));
                if (p == IntPtr.Zero) {
                    return 0;
                }
                int n = 0;
                while (Marshal.ReadByte(p, n) != 0) {
                    n++;
                }
                return Protocols.Normalize((long)n);
            }

            #endregion
        }
    }
}
