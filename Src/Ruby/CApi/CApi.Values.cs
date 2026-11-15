/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using IronRuby.Builtins;
using Microsoft.Scripting.Runtime;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// VALUEs. Immediates are encoded exactly as CRuby 4.0 on 64-bit encodes them (ruby/internal/
    /// special_consts.h), so the header macros that test a VALUE's bits work unchanged: Fixnums
    /// (n &lt;&lt; 1 | 1), Flonums (the rotated double | 0b10), static Symbols (ID &lt;&lt; 8 | 0x0c), and
    /// Qfalse 0x00, Qnil 0x04, Qtrue 0x14, Qundef 0x24. Every other object is a handle: the
    /// address of a 48-byte slot of native memory laid out as CRuby's struct RData/RTypedData -
    /// RBasic (flags, klass), two words, the data pointer - followed by the object's index in
    /// IronRuby's table. For a T_DATA object (DataObject) the slot is where its fields live:
    /// DATA_PTR and RTYPEDDATA_TYPE are plain reads and writes of it, as in CRuby. The headers never read the slot - their
    /// macros call into IronRuby - but code that dereferences RBASIC(obj) itself, as some
    /// extensions and ruby/spec's rbasic specs do, reads a plausible type, frozen bit and class
    /// (as of when the handle was made) instead of crashing the process.
    ///
    /// An object keeps its handle - the same VALUE every time, which C code compares with == -
    /// and the table keeps it alive for the life of the process. That is the first version's
    /// simplification: CRuby frees what C no longer marks, and doing the same here needs a
    /// handle scope per call plus mark functions, which is the next step (README.md).
    /// </summary>
    internal static unsafe partial class CApi {
        internal const VALUE Qfalse = 0x00, Qnil = 0x04, Qtrue = 0x14, Qundef = 0x24;
        // CRuby's FIXNUM_MAX is LONG_MAX / 2: 62 bits on Unix, 30 on Windows, where C's long is 32-bit
        internal static readonly bool IsWindows = OperatingSystem.IsWindows();
        private static readonly long FixnumMax = IsWindows ? Int32.MaxValue >> 1 : long.MaxValue >> 1;
        private static readonly long FixnumMin = IsWindows ? Int32.MinValue >> 1 : long.MinValue >> 1;

        private static readonly object _handleLock = new object();
        private static object[] _handles = new object[1024];
        private static int _handleCount = 0;

        // Handle slots come from slabs that never move or go away; a slab holds slots
        // [Start, Start + Count) of the table.
        private const int SlotSize = 48;
        private const int SlotIndexWord = 5;
        private struct Slab { public VALUE Base; public int Start, Count; }
        private static Slab[] _slabs = new Slab[0];
        private static readonly Dictionary<object, VALUE> _handleOf = new Dictionary<object, VALUE>(ReferenceEqualityComparer.Instance);

        // IDs: a symbol's ID is its index here, or, for a one-character operator, the character
        // itself - C code passes '+' as an ID.
        private static readonly Dictionary<RubySymbol, VALUE> _idOf = new Dictionary<RubySymbol, VALUE>();
        private static readonly List<RubySymbol> _symbols = new List<RubySymbol>();
        private static readonly RubySymbol[] _charSymbols = new RubySymbol[128];
        private const int IdSerialStart = 0x40;

        #region VALUE <-> object

        internal static VALUE ToValue(object obj) {
            if (obj == null) {
                return Qnil;
            }
            if (obj is bool b) {
                return b ? Qtrue : Qfalse;
            }
            if (obj is int i && i >= FixnumMin && i <= FixnumMax) {
                return ((VALUE)(long)i << 1) | 1;
            }
            if (obj is long l && l >= FixnumMin && l <= FixnumMax) {
                return ((VALUE)l << 1) | 1;
            }
            if (obj is BigInteger big && big >= FixnumMin && big <= FixnumMax) {
                return ((VALUE)(long)big << 1) | 1;
            }
            if (obj is double d) {
                VALUE flonum = TryFlonum(d);
                if (flonum != 0) {
                    return flonum;
                }
            }
            if (obj is RubySymbol sym) {
                return (SymbolToId(sym) << 8) | 0x0c;
            }
            return ToHandle(obj);
        }

        private static VALUE ToHandle(object/*!*/ obj) {
            lock (_handleLock) {
                VALUE value;
                if (_handleOf.TryGetValue(obj, out value)) {
                    return value;
                }
                if (_handleCount == _handles.Length) {
                    var bigger = new object[_handles.Length * 2];
                    Array.Copy(_handles, bigger, _handles.Length);
                    Volatile.Write(ref _handles, bigger);
                }
                int index = _handleCount++;
                _handles[index] = obj;
                value = SlotAddress(index);
                _handleOf.Add(obj, value);
                // RBasic: the class may need a handle of its own, made after this one so that a
                // class whose class is itself (Class) terminates
                ((VALUE*)value)[0] = (VALUE)BuiltinType(obj);
                ((VALUE*)value)[SlotIndexWord] = (VALUE)index;
                ((VALUE*)value)[1] = ToValue(_context.GetImmediateClassOf(obj));
                return value;
            }
        }

        private static VALUE SlotAddress(int index) {
            var slabs = _slabs;
            if (slabs.Length == 0 || index >= slabs[slabs.Length - 1].Start + slabs[slabs.Length - 1].Count) {
                int count = slabs.Length == 0 ? 4096 : slabs[slabs.Length - 1].Count * 2;
                var slab = new Slab {
                    Base = (VALUE)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)(count * SlotSize), 16),
                    Start = index,
                    Count = count,
                };
                System.Runtime.InteropServices.NativeMemory.Clear((void*)slab.Base, (nuint)(count * SlotSize));
                var bigger = new Slab[slabs.Length + 1];
                Array.Copy(slabs, bigger, slabs.Length);
                bigger[slabs.Length] = slab;
                Volatile.Write(ref _slabs, slabs = bigger);
            }
            var last = slabs[slabs.Length - 1];
            return last.Base + (VALUE)((index - last.Start) * SlotSize);
        }

        /// <summary>
        /// The table index of a handle, or -1 for a value that is not one.
        /// </summary>
        private static int HandleIndex(VALUE value) {
            foreach (var slab in Volatile.Read(ref _slabs)) {
                if (value >= slab.Base && value < slab.Base + (VALUE)(slab.Count * SlotSize)) {
                    VALUE offset = value - slab.Base;
                    return (offset % SlotSize) == 0 ? slab.Start + (int)(offset / SlotSize) : -1;
                }
            }
            return -1;
        }

        internal static object FromValue(VALUE value) {
            if ((value & 1) != 0) {
                long n = (long)value >> 1;
                return (n >= Int32.MinValue && n <= Int32.MaxValue) ? ScriptingRuntimeHelpers.Int32ToObject((int)n) : (object)n;
            }
            switch (value) {
                case Qfalse: return ScriptingRuntimeHelpers.False;
                case Qnil: return null;
                case Qtrue: return ScriptingRuntimeHelpers.True;
                case Qundef: throw RubyExceptions.CreateArgumentError("Qundef passed where a Ruby object is expected");
            }
            if ((value & 3) == 2) {
                return FlonumToDouble(value);
            }
            if ((value & 0xff) == 0x0c) {
                return IdToSymbol(value >> 8);
            }
            int index = HandleIndex(value);
            if (index >= 0) {
                var handles = Volatile.Read(ref _handles);
                if (index < handles.Length) {
                    object obj = handles[index];
                    if (obj != null) {
                        return obj;
                    }
                }
            }
            throw RubyExceptions.CreateArgumentError(String.Format("0x{0:x16} is not a VALUE IronRuby handed out", value));
        }

        internal static object[]/*!*/ FromValues(int argc, VALUE* argv) {
            if (argc < 0) {
                throw RubyExceptions.CreateArgumentError("negative argument count");
            }
            var result = new object[argc];
            for (int i = 0; i < argc; i++) {
                result[i] = FromValue(argv[i]);
            }
            return result;
        }

        internal static bool IsTruthy(VALUE value) {
            return (value & ~Qnil) != 0;
        }

        internal static VALUE Bool(bool value) {
            return value ? Qtrue : Qfalse;
        }

        #endregion

        #region Flonums (CRuby's rb_float_new_inline and rb_float_flonum_value)

        private static VALUE TryFlonum(double d) {
            VALUE bits = (VALUE)BitConverter.DoubleToInt64Bits(d);
            int b = (int)((bits >> 60) & 7);
            if (bits != 0x3000000000000000UL && ((b - 3) & ~1) == 0) {
                return (BitOperations.RotateLeft(bits, 3) & ~(VALUE)1) | 2;
            }
            if (bits == 0) {
                return 0x8000000000000002UL;
            }
            return 0;
        }

        private static double FlonumToDouble(VALUE v) {
            if (v == 0x8000000000000002UL) {
                return 0.0;
            }
            VALUE b63 = v >> 63;
            VALUE t = BitOperations.RotateRight((2 - b63) | (v & ~(VALUE)3), 3);
            return BitConverter.Int64BitsToDouble((long)t);
        }

        #endregion

        #region IDs and Symbols

        internal static VALUE SymbolToId(RubySymbol/*!*/ symbol) {
            lock (_symbols) {
                VALUE id;
                if (_idOf.TryGetValue(symbol, out id)) {
                    return id;
                }
                string name = symbol.ToString();
                if (name.Length == 1 && name[0] < 128 && !Char.IsLetterOrDigit(name[0]) && name[0] != '_') {
                    id = name[0];
                    _charSymbols[name[0]] = symbol;
                } else {
                    id = ((VALUE)(IdSerialStart + _symbols.Count) << 4) | IdScope(name);
                    _symbols.Add(symbol);
                }
                _idOf.Add(symbol, id);
                return id;
            }
        }

        internal static RubySymbol/*!*/ IdToSymbol(VALUE id) {
            lock (_symbols) {
                if (id < 128) {
                    var c = _charSymbols[id];
                    if (c == null) {
                        c = _context.CreateAsciiSymbol(((char)id).ToString());
                        SymbolToId(c);
                    }
                    return c;
                }
                long serial = (long)(id >> 4) - IdSerialStart;
                if (serial >= 0 && serial < _symbols.Count) {
                    return _symbols[(int)serial];
                }
            }
            throw RubyExceptions.CreateArgumentError(String.Format("{0} is not an ID IronRuby handed out", id));
        }

        internal static string/*!*/ IdToString(VALUE id) {
            return IdToSymbol(id).ToString();
        }

        internal static VALUE Intern(string/*!*/ name) {
            return SymbolToId(_context.EncodeIdentifier(name));
        }

        // CRuby's ID_LOCAL, ID_INSTANCE... (symbol.h): the low bits of an ID say what it names.
        private static VALUE IdScope(string/*!*/ name) {
            if (name.StartsWith("@@", StringComparison.Ordinal)) return 0x0c;
            if (name.StartsWith("@", StringComparison.Ordinal)) return 0x02;
            if (name.StartsWith("$", StringComparison.Ordinal)) return 0x06;
            if (name.Length > 1 && name.EndsWith("=", StringComparison.Ordinal) && Char.IsLetter(name[0])) return 0x08;
            if (name.Length > 0 && Char.IsUpper(name[0])) return 0x0a;
            return 0x00;
        }

        #endregion

        #region Types and flags

        // ruby_value_type
        internal const int T_NONE = 0x00, T_OBJECT = 0x01, T_CLASS = 0x02, T_MODULE = 0x03, T_FLOAT = 0x04,
            T_STRING = 0x05, T_REGEXP = 0x06, T_ARRAY = 0x07, T_HASH = 0x08, T_STRUCT = 0x09, T_BIGNUM = 0x0a,
            T_FILE = 0x0b, T_DATA = 0x0c, T_MATCH = 0x0d, T_COMPLEX = 0x0e, T_RATIONAL = 0x0f, T_NIL = 0x11,
            T_TRUE = 0x12, T_FALSE = 0x13, T_SYMBOL = 0x14, T_FIXNUM = 0x15, T_UNDEF = 0x16;

        internal static int BuiltinType(object obj) {
            switch (obj) {
                case null: return T_NIL;
                case bool b: return b ? T_TRUE : T_FALSE;
                case int i: return (i >= FixnumMin && i <= FixnumMax) ? T_FIXNUM : T_BIGNUM;
                case long l: return (l >= FixnumMin && l <= FixnumMax) ? T_FIXNUM : T_BIGNUM;
                case BigInteger _: return T_BIGNUM;
                case double _: return T_FLOAT;
                case RubySymbol _: return T_SYMBOL;
                case MutableString _: return T_STRING;
                case RubyArray _: return T_ARRAY;
                case IDictionary<object, object> _: return T_HASH;
                case RubyClass _: return T_CLASS;
                case RubyModule _: return T_MODULE;
                case RubyRegex _: return T_REGEXP;
                case MatchData _: return T_MATCH;
                case RubyStruct _: return T_STRUCT;
                case RubyIO _: return T_FILE;
                case Exception _: return T_OBJECT;
                case DataObject _: return T_DATA;
                case RubyObject _: return RationalOrComplex(obj) ?? T_OBJECT;
            }
            return T_DATA;
        }

        private static int? RationalOrComplex(object/*!*/ obj) {
            var cls = _context.GetClassOf(obj).GetNonSingletonClass();
            string name = cls.Name;
            if (name == "Rational") return T_RATIONAL;
            if (name == "Complex") return T_COMPLEX;
            return null;
        }

        private static int BuiltinType(VALUE value) {
            if ((value & 1) != 0) return T_FIXNUM;
            switch (value) {
                case Qfalse: return T_FALSE;
                case Qnil: return T_NIL;
                case Qtrue: return T_TRUE;
                case Qundef: return T_UNDEF;
            }
            if ((value & 3) == 2) return T_FLOAT;
            if ((value & 0xff) == 0x0c) return T_SYMBOL;
            return BuiltinType(FromValue(value));
        }

        // RUBY_FL_*: the bits CRuby keeps in RBasic.flags. Those IronRuby knows from the object
        // itself are computed; the FL_USER bits an extension sets on its own objects are kept here.
        private const VALUE FL_FREEZE = 1 << 11, FL_SINGLETON = 1 << 13 /* FL_USER1 */,
            RUBY_TYPED_FL_IS_TYPED_DATA = 1 << 6, CR_7BIT = 1UL << 20, CR_VALID = 1UL << 21;
        private static readonly ConditionalWeakTable<object, StrongBox<VALUE>> _userFlags =
            new ConditionalWeakTable<object, StrongBox<VALUE>>();

        internal static VALUE GetFlags(object obj) {
            VALUE flags = (VALUE)BuiltinType(obj);
            if (_context.IsObjectFrozen(obj)) {
                flags |= FL_FREEZE;
            }
            if (obj is RubyClass cls && cls.IsSingletonClass) {
                flags |= FL_SINGLETON;
            }
            if (obj is MutableString str) {
                flags |= str.IsAscii() ? CR_7BIT : CR_VALID;
            }
            if (obj is DataObject data && data.Typed) {
                flags |= RUBY_TYPED_FL_IS_TYPED_DATA;
            }
            StrongBox<VALUE> user;
            if (obj != null && _userFlags.TryGetValue(obj, out user)) {
                flags |= user.Value;
            }
            return flags;
        }

        private static void SetUserFlags(object obj, VALUE set, VALUE clear) {
            if ((set & FL_FREEZE) != 0) {
                _context.FreezeObject(obj);
            }
            // the type, the freeze bit, the singleton bit and the code range are not the extension's to set
            const VALUE computed = 0x1f | FL_FREEZE | FL_SINGLETON | CR_7BIT | CR_VALID | RUBY_TYPED_FL_IS_TYPED_DATA;
            set &= ~computed;
            clear &= ~computed;
            if (set == 0 && clear == 0) {
                return;
            }
            var box = _userFlags.GetValue(obj, _ => new StrongBox<VALUE>());
            lock (box) {
                box.Value = (box.Value | set) & ~clear;
            }
        }

        #endregion
    }
}
