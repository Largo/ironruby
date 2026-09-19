/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// ObjectSpace::WeakMap: keys compared by identity, and both keys and values held weakly - an
    /// entry goes once either side has been collected. Immediates (nil, true, Integers, Symbols,
    /// Floats) are never collected, so those are simply held.
    /// </summary>
    [RubyClass("WeakMap", DefineIn = typeof(ObjectSpace)), Includes(typeof(Enumerable))]
    public sealed class RubyWeakMap {
        private sealed class Entry {
            private readonly object _strongKey, _strongValue;
            private readonly WeakReference _weakKey, _weakValue;

            public Entry(object key, object value) {
                if (IsImmediate(key)) {
                    _strongKey = key;
                } else {
                    _weakKey = new WeakReference(key);
                }
                if (IsImmediate(value)) {
                    _strongValue = value;
                } else {
                    _weakValue = new WeakReference(value);
                }
            }

            // Both sides, or false once either has been collected.
            public bool TryGet(out object key, out object value) {
                key = (_weakKey != null) ? _weakKey.Target : _strongKey;
                value = (_weakValue != null) ? _weakValue.Target : _strongValue;
                return (_weakKey == null || key != null) && (_weakValue == null || value != null);
            }
        }

        private readonly Dictionary<long, Entry>/*!*/ _entries = new Dictionary<long, Entry>();

        private static bool IsImmediate(object obj) {
            return RubyUtils.IsRubyValueType(obj) || obj is double || obj is float;
        }

        /// <summary>The live entries, in insertion order; dead ones are dropped on the way.</summary>
        private List<KeyValuePair<object, object>>/*!*/ GetLiveEntries() {
            var result = new List<KeyValuePair<object, object>>();
            lock (_entries) {
                List<long> dead = null;
                foreach (var pair in _entries) {
                    object key, value;
                    if (pair.Value.TryGet(out key, out value)) {
                        result.Add(new KeyValuePair<object, object>(key, value));
                    } else {
                        (dead ?? (dead = new List<long>())).Add(pair.Key);
                    }
                }
                if (dead != null) {
                    foreach (var id in dead) {
                        _entries.Remove(id);
                    }
                }
            }
            return result;
        }

        private bool TryGetValue(RubyContext/*!*/ context, object key, out object value) {
            long id = RubyUtils.GetObjectId(context, key);
            lock (_entries) {
                Entry entry;
                object liveKey;
                if (_entries.TryGetValue(id, out entry)) {
                    if (entry.TryGet(out liveKey, out value)) {
                        return true;
                    }
                    _entries.Remove(id);
                }
            }
            value = null;
            return false;
        }

        [RubyMethod("[]")]
        public static object GetValue(RubyContext/*!*/ context, RubyWeakMap/*!*/ self, object key) {
            object value;
            return self.TryGetValue(context, key, out value) ? value : null;
        }

        [RubyMethod("[]=")]
        public static object SetValue(RubyContext/*!*/ context, RubyWeakMap/*!*/ self, object key, object value) {
            long id = RubyUtils.GetObjectId(context, key);
            lock (self._entries) {
                // Re-adding moves nothing: an existing key keeps its place, as in MRI's st table.
                self._entries[id] = new Entry(key, value);
            }
            return value;
        }

        [RubyMethod("key?")]
        [RubyMethod("member?")]
        [RubyMethod("include?")]
        public static bool HasKey(RubyContext/*!*/ context, RubyWeakMap/*!*/ self, object key) {
            object value;
            return self.TryGetValue(context, key, out value);
        }

        [RubyMethod("delete")]
        public static object Delete(RubyContext/*!*/ context, BlockParam block, RubyWeakMap/*!*/ self, object key) {
            object value;
            if (self.TryGetValue(context, key, out value)) {
                lock (self._entries) {
                    self._entries.Remove(RubyUtils.GetObjectId(context, key));
                }
                return value;
            }
            if (block != null) {
                object result;
                block.Yield(key, out result);
                return result;
            }
            return null;
        }

        [RubyMethod("size")]
        [RubyMethod("length")]
        public static int Size(RubyWeakMap/*!*/ self) {
            return self.GetLiveEntries().Count;
        }

        [RubyMethod("keys")]
        public static RubyArray/*!*/ Keys(RubyWeakMap/*!*/ self) {
            var result = new RubyArray();
            foreach (var pair in self.GetLiveEntries()) {
                result.Add(pair.Key);
            }
            return result;
        }

        [RubyMethod("values")]
        public static RubyArray/*!*/ Values(RubyWeakMap/*!*/ self) {
            var result = new RubyArray();
            foreach (var pair in self.GetLiveEntries()) {
                result.Add(pair.Value);
            }
            return result;
        }

        [RubyMethod("each")]
        [RubyMethod("each_pair")]
        public static object Each(BlockParam block, RubyWeakMap/*!*/ self) {
            foreach (var pair in self.GetLiveEntries()) {
                // MRI only asks for the block once there is something to give it.
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }
                object result;
                if (block.Yield(pair.Key, pair.Value, out result)) {
                    return result;
                }
            }
            return self;
        }

        [RubyMethod("each_key")]
        public static object EachKey(BlockParam block, RubyWeakMap/*!*/ self) {
            foreach (var pair in self.GetLiveEntries()) {
                // MRI only asks for the block once there is something to give it.
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }
                object result;
                if (block.Yield(pair.Key, out result)) {
                    return result;
                }
            }
            return self;
        }

        [RubyMethod("each_value")]
        public static object EachValue(BlockParam block, RubyWeakMap/*!*/ self) {
            foreach (var pair in self.GetLiveEntries()) {
                // MRI only asks for the block once there is something to give it.
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }
                object result;
                if (block.Yield(pair.Value, out result)) {
                    return result;
                }
            }
            return self;
        }

        // MRI shows an immediate by #inspect and anything else by class and address only, so
        // that inspecting the map cannot call back into (or keep alive) the objects in it.
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyWeakMap/*!*/ self) {
            MutableString result = RubyUtils.ObjectToMutableStringPrefix(context, self);
            bool first = true;
            foreach (var pair in self.GetLiveEntries()) {
                result.Append(first ? ": " : ", ");
                first = false;
                AppendEntryPart(context, result, pair.Key);
                result.Append(" => ");
                AppendEntryPart(context, result, pair.Value);
            }
            return result.Append('>');
        }

        private static void AppendEntryPart(RubyContext/*!*/ context, MutableString/*!*/ result, object obj) {
            if (IsImmediate(obj)) {
                result.Append(context.Inspect(obj));
            } else {
                result.Append(RubyUtils.ObjectToMutableString(context, obj));
            }
        }
    }
}
