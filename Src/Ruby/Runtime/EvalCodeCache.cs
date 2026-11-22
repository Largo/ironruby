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
using System.Collections.Generic;
using IronRuby.Builtins;

namespace IronRuby.Runtime {
    /// <summary>
    /// The compiled code of the strings a runtime has evaluated (RubyUtils.Evaluate), so that a text
    /// evaluated again - an ERB template's source, a library's generated method, a loop's eval - is not
    /// parsed, bridged, transformed and compiled again; that took some 200 microseconds of an eval of
    /// `1 + 1' that CRuby does in 4.  MRI compiles each time, but nothing it does differs on the second
    /// compile of the same text in the same place, which is what the key captures: the text and its
    /// encoding, the file and line, whether it is a module eval, the frame it runs in (label, block
    /// depth, method name and parameters) and the locals it can see - a local the previous eval defined
    /// makes a different key, as it makes different code - and $VERBOSE, which decides the parse
    /// warnings. Code whose compile gave a warning is not kept, so that the warning is given each time.
    /// The key holds strings only: no scope, binding or object of the program.
    /// Per runtime, bounded (least recently used goes first), and locked: there is no GVL.
    /// </summary>
    internal sealed class EvalCodeCache {
        internal const int Capacity = 256;

        // Bigger texts are compiled each time: keeping them would cost more memory than their compile.
        internal const int MaxCodeLength = 64 * 1024;

        private readonly Dictionary<Key, LinkedListNode<KeyValuePair<Key, Delegate>>> _map =
            new Dictionary<Key, LinkedListNode<KeyValuePair<Key, Delegate>>>();
        private readonly LinkedList<KeyValuePair<Key, Delegate>> _order = new LinkedList<KeyValuePair<Key, Delegate>>();
        private readonly object _lock = new object();

        internal Delegate TryGet(Key/*!*/ key) {
            lock (_lock) {
                LinkedListNode<KeyValuePair<Key, Delegate>> node;
                if (!_map.TryGetValue(key, out node)) {
                    return null;
                }
                if (node != _order.First) {
                    _order.Remove(node);
                    _order.AddFirst(node);
                }
                return node.Value.Value;
            }
        }

        internal void Add(Key/*!*/ key, Delegate/*!*/ code) {
            lock (_lock) {
                if (_map.ContainsKey(key)) {
                    return;
                }
                key.MakeWeak();
                var node = _order.AddFirst(new KeyValuePair<Key, Delegate>(key, code));
                _map.Add(key, node);
                if (_map.Count > Capacity) {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }

        internal sealed class Key : IEquatable<Key> {
            private readonly byte[]/*!*/ _code;
            private readonly RubyEncoding/*!*/ _encoding;
            private readonly string/*!*/ _path;
            private readonly int _line;
            private readonly bool _isModuleEval;
            private readonly int _verbosity;
            private readonly string _baseLabel;
            private readonly int _blockLevels;
            private readonly string _methodName;
            private readonly string[] _parameterNames;
            private readonly bool _hasUnsplat;
            private readonly List<string>/*!*/ _localNames;

            // The modules constants are looked up in, innermost first (RubyScope.TryResolveConstantNoLock):
            // a constant's site caches what it found, as if it were always in the same lexical scope, and
            // a string evaluated in two classes is not. A key looked up holds them; a key kept holds them
            // weakly (WeakReference), so that the cache keeps no class alive.
            private object[]/*!*/ _modules;
            private readonly int _hash;

            internal Key(byte[]/*!*/ code, RubyEncoding/*!*/ encoding, string/*!*/ path, int line, bool isModuleEval, int verbosity,
                string baseLabel, int blockLevels, string methodName, string[] parameterNames, bool hasUnsplat, List<string>/*!*/ localNames,
                object[]/*!*/ modules) {
                _code = code;
                _encoding = encoding;
                _path = path;
                _line = line;
                _isModuleEval = isModuleEval;
                _verbosity = verbosity;
                _baseLabel = baseLabel;
                _blockLevels = blockLevels;
                _methodName = methodName;
                _parameterNames = parameterNames;
                _hasUnsplat = hasUnsplat;
                _localNames = localNames;
                _modules = modules;

                var hash = new HashCode();
                hash.AddBytes(code);
                hash.Add(encoding);
                hash.Add(path);
                hash.Add(line);
                hash.Add(isModuleEval);
                hash.Add(verbosity);
                hash.Add(baseLabel);
                hash.Add(blockLevels);
                hash.Add(methodName);
                hash.Add(localNames.Count);
                foreach (var name in localNames) {
                    hash.Add(name);
                }
                foreach (var module in modules) {
                    hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(module));
                }
                _hash = hash.ToHashCode();
            }

            public override int GetHashCode() {
                return _hash;
            }

            public override bool Equals(object obj) {
                return Equals(obj as Key);
            }

            public bool Equals(Key other) {
                return other != null && _hash == other._hash && _line == other._line && _isModuleEval == other._isModuleEval &&
                    _verbosity == other._verbosity && _blockLevels == other._blockLevels && _hasUnsplat == other._hasUnsplat &&
                    _encoding == other._encoding && _path == other._path && _baseLabel == other._baseLabel &&
                    _methodName == other._methodName && SameNames(_parameterNames, other._parameterNames) &&
                    SameNames(_localNames, other._localNames) && SameModules(_modules, other._modules) &&
                    _code.AsSpan().SequenceEqual(other._code);
            }

            /// <summary>Makes the key's hold on its modules weak, before it is kept.</summary>
            internal void MakeWeak() {
                var weak = new object[_modules.Length];
                for (int i = 0; i < weak.Length; i++) {
                    weak[i] = new WeakReference(_modules[i]);
                }
                _modules = weak;
            }

            private static bool SameModules(object[]/*!*/ a, object[]/*!*/ b) {
                if (a.Length != b.Length) {
                    return false;
                }
                for (int i = 0; i < a.Length; i++) {
                    object x = (a[i] is WeakReference wx) ? wx.Target : a[i];
                    object y = (b[i] is WeakReference wy) ? wy.Target : b[i];
                    if (x == null || x != y) {
                        return false;
                    }
                }
                return true;
            }

            private static bool SameNames(IList<string> a, IList<string> b) {
                if (a == null || b == null) {
                    return a == b;
                }
                if (a.Count != b.Count) {
                    return false;
                }
                for (int i = 0; i < a.Count; i++) {
                    if (a[i] != b[i]) {
                        return false;
                    }
                }
                return true;
            }
        }
    }
}
