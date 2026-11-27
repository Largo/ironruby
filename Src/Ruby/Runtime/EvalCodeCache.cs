/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;

namespace IronRuby.Runtime {

    /// <summary>
    /// The compiled code of the strings a runtime evaluated lately, for the texts that keep no state
    /// of one eval: an ERB template's source evaluated at every #result is transformed and light
    /// compiled once (about 200 of the 300 us of such an eval), and its blocks reach the compiler.
    ///
    /// The first attempt (perf/evalc, round thirty-one) kept every text and was taken out: compiled
    /// code is not only code, it keeps values in boxes of its own, and a second eval must get fresh
    /// ones - a /o regexp, a regexp or range literal (one object per eval), END {}, and the same one
    /// level down in a `def' or a block compiled at its first call. So a text is kept only when its
    /// whole tree, blocks included, holds none of the nodes whose code keeps such a value
    /// (<see cref="IsShareable"/>): no regexp, range, flip-flop, END, super, yield, and no
    /// def, class or module body (whose code would also be one for two evals). A `defined?' is let in
    /// (perf/evalscreen): it keeps what its operand's code keeps. What is left - calls,
    /// locals, string, symbol and number literals, blocks, constants, conditionals, loops - keeps
    /// only call-site caches, which a method body run again shares too; a frozen string literal is
    /// interned, the same object for every eval, as MRI's is.
    ///
    /// The key is everything the compiled code depends on: the text and its encoding, the file and
    /// line, $VERBOSE, the frame (label, block depth, method name and parameters, inside a method or
    /// not), the locals it can see, and the modules its constants are looked up in (a constant's site
    /// caches what it found as if it were always in the same lexical scope). Only a plain string eval
    /// is kept (not a module eval), and not in a frame with keyword parameters or an isolated proc,
    /// nor while Coverage measures, nor a compile that warned. Per runtime, bounded (LRU), locked:
    /// threads run in parallel. The key holds its modules strongly, so a module stays alive while one
    /// of the at most <see cref="Capacity"/> entries names it.
    /// </summary>
    internal sealed class EvalCodeCache {
        internal const int MaxCodeLength = 64 * 1024;
        private const int Capacity = 64;

        private readonly Dictionary<Key, LinkedListNode<KeyValuePair<Key, Delegate>>> _map =
            new Dictionary<Key, LinkedListNode<KeyValuePair<Key, Delegate>>>();
        private readonly LinkedList<KeyValuePair<Key, Delegate>> _order = new LinkedList<KeyValuePair<Key, Delegate>>();

        internal Delegate TryGet(Key/*!*/ key) {
            lock (_map) {
                if (!_map.TryGetValue(key, out var node)) {
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
            lock (_map) {
                if (_map.ContainsKey(key)) {
                    return;
                }
                if (_map.Count >= Capacity) {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
                _map.Add(key, _order.AddFirst(new KeyValuePair<Key, Delegate>(key, code)));
            }
        }

        /// <summary>
        /// Whether the code of the tree keeps nothing of one eval, so that two evals of it may run the
        /// same compiled code (see the class comment for what is refused, and why).
        /// </summary>
        internal static bool IsShareable(SourceUnitTree/*!*/ ast) {
            var screen = new Screen();
            screen.Walk(ast);
            return screen.Shareable;
        }

        /// <summary>
        /// IR_EVAL_SHARE_DEFINED=0 refuses a text with a `defined?' again, as perf/erb36's screen did (see Screen).
        /// </summary>
        private static readonly bool ShareDefinedEnabled = Environment.GetEnvironmentVariable("IR_EVAL_SHARE_DEFINED") != "0";

        private sealed class Screen : Walker {
            internal bool Shareable = true;

            private bool Refuse() {
                Shareable = false;
                return false;
            }

            public override bool Enter(ClassDefinition/*!*/ node) => Refuse();
            public override bool Enter(ModuleDefinition/*!*/ node) => Refuse();
            public override bool Enter(SingletonDefinition/*!*/ node) => Refuse();
            public override bool Enter(MethodDefinition/*!*/ node) => Refuse();
            public override bool Enter(RangeExpression/*!*/ node) => Refuse();
            public override bool Enter(RangeCondition/*!*/ node) => Refuse();
            public override bool Enter(RegularExpression/*!*/ node) => Refuse();
            public override bool Enter(RegularExpressionCondition/*!*/ node) => Refuse();
            public override bool Enter(ShutdownHandlerStatement/*!*/ node) => Refuse();
            // `defined?' keeps what the code of its operand keeps and nothing more (IR_EVAL_SHARE_DEFINED): of a local, the
            // answer the compile found in the locals the key holds; of a call, a call site; of a constant, a constant's cache,
            // which a constant read keeps too; of an instance, global or class variable, a runtime call. The walk goes on into
            // the operand, so `defined?(yield)' and `defined?(super)' are refused as their YieldCall and SuperCall are.
            public override bool Enter(IsDefinedExpression/*!*/ node) => ShareDefinedEnabled || Refuse();
            public override bool Enter(SuperCall/*!*/ node) => Refuse();
            public override bool Enter(YieldCall/*!*/ node) => Refuse();

            // the default walk does not go into the block of a call, nor to the right of a parallel
            // assignment (as RubyCoverage's LineCollector knows)
            protected internal override void Walk(MethodCall/*!*/ node) {
                base.Walk(node);
                if (node.Block != null) {
                    node.Block.Walk(this);
                }
            }

            protected internal override void Walk(ParallelAssignmentExpression/*!*/ node) {
                base.Walk(node);
                foreach (var value in node.Right) {
                    value.Walk(this);
                }
            }
        }

        /// <summary>
        /// The modules a constant in code evaluated in the scope is looked up in, innermost first, as
        /// RubyScope.TryResolveConstantNoLock walks them.
        /// </summary>
        internal static object[]/*!*/ ConstantModules(RubyScope/*!*/ targetScope) {
            var result = new List<object>();
            for (RubyScope scope = targetScope; scope != null; scope = scope.Parent) {
                RubyModule scopeModule = scope.Module;
                if (scopeModule != null) {
                    result.Add(scopeModule);
                    result.Add(scope.LexicalConstantModule);
                }
            }
            return result.ToArray();
        }

        internal sealed class Key : IEquatable<Key> {
            private readonly byte[]/*!*/ _code;
            private readonly RubyEncoding/*!*/ _encoding;
            private readonly string/*!*/ _path;
            private readonly int _line;
            private readonly int _verbosity;
            private readonly bool _inMethod;
            private readonly string _baseLabel;
            private readonly int _blockLevels;
            private readonly string _methodName;
            private readonly string[] _parameterNames;
            private readonly bool _hasUnsplat;
            private readonly List<string> _localNames;
            private readonly object[]/*!*/ _modules;
            private readonly int _hash;

            internal Key(byte[]/*!*/ code, RubyEncoding/*!*/ encoding, string/*!*/ path, int line, int verbosity, bool inMethod,
                string baseLabel, int blockLevels, string methodName, string[] parameterNames, bool hasUnsplat,
                List<string> localNames, object[]/*!*/ modules) {
                _code = code;
                _encoding = encoding;
                _path = path;
                _line = line;
                _verbosity = verbosity;
                _inMethod = inMethod;
                _baseLabel = baseLabel;
                _blockLevels = blockLevels;
                _methodName = methodName;
                _parameterNames = parameterNames;
                _hasUnsplat = hasUnsplat;
                _localNames = localNames;
                _modules = modules;

                var hash = new HashCode();
                hash.AddBytes(code);
                hash.Add(path);
                hash.Add(line);
                hash.Add(baseLabel);
                hash.Add(blockLevels);
                hash.Add(methodName);
                if (localNames != null) {
                    hash.Add(localNames.Count);
                    foreach (var name in localNames) {
                        hash.Add(name);
                    }
                }
                foreach (var module in modules) {
                    hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(module));
                }
                _hash = hash.ToHashCode();
            }

            public override int GetHashCode() => _hash;

            public override bool Equals(object obj) => Equals(obj as Key);

            public bool Equals(Key other) {
                return other != null && _hash == other._hash && _line == other._line && _verbosity == other._verbosity &&
                    _inMethod == other._inMethod && _blockLevels == other._blockLevels && _hasUnsplat == other._hasUnsplat &&
                    _encoding == other._encoding && _path == other._path && _baseLabel == other._baseLabel &&
                    _methodName == other._methodName && SameNames(_parameterNames, other._parameterNames) &&
                    SameNames(_localNames, other._localNames) && SameModules(_modules, other._modules) &&
                    _code.AsSpan().SequenceEqual(other._code);
            }

            private static bool SameModules(object[]/*!*/ a, object[]/*!*/ b) {
                if (a.Length != b.Length) {
                    return false;
                }
                for (int i = 0; i < a.Length; i++) {
                    if (a[i] != b[i]) {
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
