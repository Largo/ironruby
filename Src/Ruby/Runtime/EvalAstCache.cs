/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System.Collections.Generic;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;

namespace IronRuby.Runtime {

    /// <summary>
    /// The trees (SourceUnitTree) of the strings a runtime evaluated lately, so that a template
    /// evaluated again and again (ERB#result) is parsed and mapped by the prism bridge once.
    /// What is kept is the AST only, which holds no runtime state: every eval still transforms
    /// and compiles it afresh, so /o regexps, END {}, literal identity and the call sites of the
    /// code a second eval runs are its own (the compiled-code cache tried before shared those;
    /// Util/eval-matrix.rb). The AST is already compiled more than once elsewhere - a method body
    /// per lexical module chain, a `def` run many times - and its transformation assigns the same
    /// closure slots each time (LexicalScope.AllocateClosureSlotsForLocals).
    ///
    /// The key is everything the bridge's result depends on for an eval: the text, the file and
    /// line, the outer locals in order, the source encoding and whether the eval is outside a method.
    /// A parse that reported anything (a warning, which may depend on $VERBOSE and must be reported
    /// again) or failed is not kept, nor are evals of an isolated proc. Per runtime, bounded (LRU),
    /// and locked: threads run in parallel.
    /// </summary>
    internal sealed class EvalAstCache {
        internal const int MaxCodeLength = 64 * 1024;
        private const int Capacity = 64;

        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, SourceUnitTree>>> _map =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, SourceUnitTree>>>();
        private readonly LinkedList<KeyValuePair<string, SourceUnitTree>> _order = new LinkedList<KeyValuePair<string, SourceUnitTree>>();

        internal static string/*!*/ MakeKey(string/*!*/ code, string path, int line, bool outsideMethod,
            RubyEncoding encoding, List<string> localNames) {
            var key = new StringBuilder(code.Length + 64);
            key.Append(path).Append('\0').Append(line).Append('\0').Append(outsideMethod ? '1' : '0').Append('\0');
            key.Append(encoding != null ? encoding.Name : "").Append('\0');
            if (localNames != null) {
                key.Append(localNames.Count);
                foreach (var name in localNames) {
                    key.Append('\0').Append(name);
                }
            }
            key.Append('\u0001').Append(code);
            return key.ToString();
        }

        internal SourceUnitTree Get(string/*!*/ key) {
            lock (_map) {
                if (_map.TryGetValue(key, out var node)) {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    return node.Value.Value;
                }
                return null;
            }
        }

        internal void Add(string/*!*/ key, SourceUnitTree/*!*/ tree) {
            lock (_map) {
                if (_map.ContainsKey(key)) {
                    return;
                }
                if (_map.Count >= Capacity) {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
                _map.Add(key, _order.AddFirst(new KeyValuePair<string, SourceUnitTree>(key, tree)));
            }
        }
    }
}
