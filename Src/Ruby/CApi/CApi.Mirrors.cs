/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// RSTRING_PTR and RARRAY_PTR. A MutableString's bytes and a RubyArray's elements live in
    /// the CLR heap, where they move and are not laid out as C expects, so C gets a native copy -
    /// a mirror - that IronRuby keeps in step with the object, as JRuby's C extension support did:
    ///
    /// - a mirror is made, or refreshed from the object, when C asks for the pointer;
    /// - while C runs, the mirrors it has asked for are active: whenever C calls back into Ruby
    ///   they are written back to their objects if C changed them (a byte compare against the
    ///   last sync), and refreshed when that Ruby code changed the object;
    /// - when the outermost call into C returns, they are written back a last time and go
    ///   inactive, but stay allocated for the object's lifetime, so the pointer an extension was
    ///   given stays valid memory (holding what it held then) - CRuby makes no promise either
    ///   about a pointer kept past a change to the string.
    ///
    /// The cost is a compare of every active mirror's bytes per transition between C and Ruby,
    /// which is fine for the short strings C extensions usually touch and is the first thing to
    /// replace (with native storage inside MutableString) when a real workload says otherwise.
    /// </summary>
    internal static unsafe partial class CApi {
        private abstract class Mirror {
            public bool Active;
            public abstract void WriteBack();
            public abstract void Refresh();
        }

        private sealed class StringMirror : Mirror {
            public readonly MutableString Str;
            public byte* Ptr;
            public bool Owned = true;   // false: a static buffer the extension handed over (rb_str_new_static)
            public int Capacity;   // bytes, not counting the terminator (up to TermSlack NULs)
            public int Length;     // what both sides agreed on at the last sync
            public int ChangeCount;

            public StringMirror(MutableString/*!*/ str) {
                Str = str;
            }

            // room for the widest terminator CRuby writes (UTF-32's four NULs)
            private const int TermSlack = 4;

            ~StringMirror() {
                if (Ptr != null && Owned) {
                    NativeMemory.Free(Ptr);
                }
            }

            public void EnsureCapacity(int capacity) {
                if (Ptr == null || capacity > Capacity || !Owned) {
                    int newCapacity = Math.Max(capacity, Ptr == null ? 15 : Capacity);
                    // C may write up to the old capacity; keep what is there
                    byte* p = (byte*)NativeMemory.AllocZeroed((nuint)(newCapacity + TermSlack));
                    if (Ptr != null) {
                        Buffer.MemoryCopy(Ptr, p, newCapacity + TermSlack, Owned ? Capacity + TermSlack : Length);
                        if (Owned) {
                            NativeMemory.Free(Ptr);
                        }
                    }
                    Ptr = p;
                    Owned = true;
                    Capacity = newCapacity;
                }
            }

            private void Terminate(int length) {
                int termLength = MinCharLength(Str.Encoding);
                for (int i = 0; i < termLength && length + i < Capacity + TermSlack; i++) {
                    Ptr[length + i] = 0;
                }
            }

            /// <summary>
            /// Object to native, when the object has changed since the last sync.
            /// </summary>
            public override void Refresh() {
                if (Ptr != null && Str.ChangeCount == ChangeCount) {
                    return;
                }
                byte[] bytes = Str.ToByteArray();
                if (!Owned || Ptr == null || bytes.Length > Capacity) {
                    EnsureCapacity(bytes.Length);
                }
                bytes.AsSpan().CopyTo(new Span<byte>(Ptr, bytes.Length));
                Terminate(bytes.Length);
                Length = bytes.Length;
                ChangeCount = Str.ChangeCount;
            }

            /// <summary>
            /// Native to object, when C has written to the buffer.
            /// </summary>
            public override void WriteBack() {
                if (Ptr == null || Str.IsFrozen || !Owned) {
                    return;
                }
                if (Str.ChangeCount != ChangeCount) {
                    // Ruby changed the object since the last sync: it wins, Refresh follows
                    return;
                }
                var native = new ReadOnlySpan<byte>(Ptr, Length);
                if (native.SequenceEqual(Str.ToByteArray())) {
                    return;
                }
                Str.Remove(0);
                Str.Append(native.ToArray());
                ChangeCount = Str.ChangeCount;
            }

            /// <summary>
            /// rb_str_set_len and friends: C wrote Length bytes and says so.
            /// </summary>
            public void SetLength(int length) {
                if (length > Capacity || !Owned) {
                    EnsureCapacity(length);
                }
                Length = length;
                Terminate(length);
                Str.Remove(0);
                Str.Append(new ReadOnlySpan<byte>(Ptr, length).ToArray());
                ChangeCount = Str.ChangeCount;
            }
        }

        private sealed class ArrayMirror : Mirror {
            public readonly RubyArray Array;
            public VALUE* Ptr;
            public int Capacity;
            public int Length;

            public ArrayMirror(RubyArray/*!*/ array) {
                Array = array;
            }

            ~ArrayMirror() {
                if (Ptr != null) {
                    NativeMemory.Free(Ptr);
                }
            }

            public override void Refresh() {
                int count = Array.Count;
                if (Ptr == null || count > Capacity) {
                    int capacity = Math.Max(count, 8);
                    VALUE* p = (VALUE*)NativeMemory.AllocZeroed((nuint)capacity, (nuint)sizeof(VALUE));
                    if (Ptr != null) {
                        NativeMemory.Free(Ptr);
                    }
                    Ptr = p;
                    Capacity = capacity;
                }
                for (int i = 0; i < count; i++) {
                    Ptr[i] = ToValue(Array[i]);
                }
                Length = count;
            }

            public override void WriteBack() {
                if (Ptr == null || Array.IsFrozen) {
                    return;
                }
                int count = Math.Min(Length, Array.Count);
                for (int i = 0; i < count; i++) {
                    if (Ptr[i] != ToValue(Array[i])) {
                        Array[i] = FromValue(Ptr[i]);
                    }
                }
            }
        }

        private static readonly ConditionalWeakTable<MutableString, StringMirror> _stringMirrors = new ConditionalWeakTable<MutableString, StringMirror>();
        private static readonly ConditionalWeakTable<RubyArray, ArrayMirror> _arrayMirrors = new ConditionalWeakTable<RubyArray, ArrayMirror>();

        [ThreadStatic] private static List<Mirror> _activeMirrors;
        [ThreadStatic] private static int _nativeDepth;

        private static void Activate(Mirror/*!*/ mirror) {
            if (!mirror.Active) {
                mirror.Active = true;
                (_activeMirrors ?? (_activeMirrors = new List<Mirror>())).Add(mirror);
            }
        }

        private static StringMirror/*!*/ GetStringMirror(MutableString/*!*/ str) {
            var mirror = _stringMirrors.GetValue(str, s => new StringMirror(s));
            if (!mirror.Active) {
                mirror.Refresh();
                Activate(mirror);
            }
            return mirror;
        }

        /// <summary>
        /// rb_str_new_static and friends: RSTRING_PTR is the extension's own buffer until the
        /// string changes.
        /// </summary>
        private static void AdoptStaticBuffer(MutableString/*!*/ str, byte* ptr, int length) {
            var mirror = _stringMirrors.GetValue(str, s => new StringMirror(s));
            mirror.Ptr = ptr;
            mirror.Owned = false;
            mirror.Capacity = length;
            mirror.Length = length;
            mirror.ChangeCount = str.ChangeCount;
        }

        private static ArrayMirror/*!*/ GetArrayMirror(RubyArray/*!*/ array) {
            var mirror = _arrayMirrors.GetValue(array, a => new ArrayMirror(a));
            if (!mirror.Active) {
                mirror.Refresh();
                Activate(mirror);
            }
            return mirror;
        }

        private static void WriteBackAll() {
            var mirrors = _activeMirrors;
            if (mirrors != null) {
                for (int i = 0; i < mirrors.Count; i++) {
                    mirrors[i].WriteBack();
                }
            }
        }

        private static void RefreshAll() {
            var mirrors = _activeMirrors;
            if (mirrors != null) {
                for (int i = 0; i < mirrors.Count; i++) {
                    mirrors[i].Refresh();
                }
            }
        }

        /// <summary>
        /// Before IronRuby calls C: Ruby code may have changed what C has pointers into.
        /// </summary>
        internal static NativeScope EnterNative(CCode code = null) {
            var scope = new NativeScope();
            EnterCode(code, ref scope);
            if (_nativeDepth > 0) {
                RefreshAll();
            }
            scope.Depth = _nativeDepth++;
            return scope;
        }

        /// <summary>
        /// After C returns (or unwinds) to IronRuby.
        /// </summary>
        internal static void LeaveNative(NativeScope scope) {
            try {
                LeaveMirrors(scope.Depth);
            } finally {
                LeaveCode(ref scope);
            }
        }

        private static void LeaveMirrors(int depth) {
            _nativeDepth = depth;
            var mirrors = _activeMirrors;
            if (mirrors != null && mirrors.Count > 0) {
                WriteBackAll();
                if (depth == 0) {
                    foreach (var m in mirrors) {
                        m.Active = false;
                    }
                    mirrors.Clear();
                }
            }
        }

        /// <summary>
        /// On entry to a managed function called from C: C may have written to its mirrors.
        /// </summary>
        private static int EnterManaged() {
            var mirrors = _activeMirrors;
            if (mirrors != null && mirrors.Count > 0) {
                WriteBackAll();
            }
            return 0;
        }

        /// <summary>
        /// On return to C: what the managed function changed must be visible through the pointers.
        /// </summary>
        private static void LeaveManaged(int unused) {
            var mirrors = _activeMirrors;
            if (mirrors != null && mirrors.Count > 0) {
                RefreshAll();
            }
        }

        #region rb_ir_* (ruby/internal/ironruby.h)

        internal static MutableString/*!*/ ToMutableString(VALUE value) {
            var obj = FromValue(value);
            var str = obj as MutableString;
            if (str == null) {
                throw RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (expected String)", ClassName(obj)));
            }
            return str;
        }

        internal static RubyArray/*!*/ ToRubyArray(VALUE value) {
            var obj = FromValue(value);
            var array = obj as RubyArray;
            if (array == null) {
                throw RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (expected Array)", ClassName(obj)));
            }
            return array;
        }

        internal static long rb_ir_rstring_len(VALUE str) {
            // any mirror was written back on the way in (EnterManaged)
            return ToMutableString(str).GetByteCount();
        }

        internal static byte* rb_ir_rstring_ptr(VALUE str) {
            return GetStringMirror(ToMutableString(str)).Ptr;
        }

        internal static long rb_ir_rarray_len(VALUE ary) {
            return ToRubyArray(ary).Count;
        }

        internal static VALUE* rb_ir_rarray_ptr(VALUE ary) {
            return GetArrayMirror(ToRubyArray(ary)).Ptr;
        }

        internal static VALUE rb_ir_rarray_aref(VALUE ary, long i) {
            var array = ToRubyArray(ary);
            return (i >= 0 && i < array.Count) ? ToValue(array[(int)i]) : Qnil;
        }

        internal static void rb_ir_rarray_aset(VALUE ary, long i, VALUE v) {
            rb_ary_store(ary, i, v);
        }

        internal static VALUE rb_ir_builtin_type(VALUE obj) {
            return (VALUE)BuiltinType(obj);
        }

        // rb_obj_hide: CRuby clears the class pointer, which is all an extension can see of it
        private static readonly ConditionalWeakTable<object, object> _hidden = new ConditionalWeakTable<object, object>();

        internal static VALUE rb_ir_rbasic_class(VALUE obj) {
            object o = FromValue(obj);
            object unused;
            if (o != null && _hidden.TryGetValue(o, out unused)) {
                return 0;
            }
            return ToValue(_context.GetImmediateClassOf(o));
        }

        internal static VALUE rb_ir_fl_test(VALUE obj, VALUE flags) {
            return GetFlags(FromValue(obj)) & flags;
        }

        internal static void rb_ir_fl_set(VALUE obj, VALUE flags) {
            SetUserFlags(FromValue(obj), flags, 0);
        }

        internal static void rb_ir_fl_unset(VALUE obj, VALUE flags) {
            SetUserFlags(FromValue(obj), 0, flags);
        }

        internal static void rb_ir_fl_reverse(VALUE obj, VALUE flags) {
            object o = FromValue(obj);
            VALUE current = GetFlags(o);
            SetUserFlags(o, ~current & flags, current & flags);
        }

        #endregion
    }
}
