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
using System.Text;
using IronRuby.Builtins;

namespace IronRuby.Runtime.Calls {

    /// <summary>
    /// The parameter list a method or a block was *written* with, kept beside the one it is
    /// *compiled* with.
    /// 
    /// Those two are not the same thing here. IronRuby's calling convention has no slot for
    /// keyword arguments, so the front end lowers keywords onto positional parameters - in the
    /// general case onto a single rest parameter whose contents a prologue unpacks by hand. That
    /// is invisible to a caller, but it is very visible to #parameters and #arity, which are
    /// supposed to describe the source text. Recording the declared shape here lets those two
    /// answer from the declaration instead of from the lowering.
    /// </summary>
    public sealed class RubyParameterSignature {

        public struct Parameter {
            /// <summary>One of req, opt, rest, keyreq, key, keyrest, nokey, block.</summary>
            public readonly string/*!*/ Kind;

            /// <summary>The declared name, or null for a parameter that has none.</summary>
            public readonly string Name;

            public Parameter(string/*!*/ kind, string name) {
                Kind = kind;
                Name = name;
            }
        }

        private readonly Parameter[]/*!*/ _parameters;
        private readonly int _leadingCount;
        private readonly int _optionalCount;
        private readonly int _postCount;
        private readonly int _requiredKeywordCount;
        private readonly bool _hasRest;
        private readonly bool _hasKeywords;
        private readonly bool _hasKeywordRest;

        public RubyParameterSignature(Parameter[]/*!*/ parameters, int leadingCount, int optionalCount, int postCount,
            bool hasRest, int requiredKeywordCount, bool hasKeywords, bool hasKeywordRest) {

            _parameters = parameters;
            _leadingCount = leadingCount;
            _optionalCount = optionalCount;
            _postCount = postCount;
            _hasRest = hasRest;
            _requiredKeywordCount = requiredKeywordCount;
            _hasKeywords = hasKeywords;
            _hasKeywordRest = hasKeywordRest;
        }

        /// <summary>
        /// The names of the implicit parameters - `it`, or `_1` up to the highest numbered one
        /// referenced - that the block body brought into being by mentioning them. Null for a
        /// block whose parameters were written out. These are not local variables as far as MRI
        /// is concerned: #local_variables leaves them out and Binding#implicit_parameters
        /// reports them instead.
        /// </summary>
        public string[] ImplicitParameterNames { get; set; }

        public static readonly RubyParameterSignature/*!*/ Empty =
            new RubyParameterSignature(new Parameter[0], 0, 0, 0, false, 0, false, false);

        /// <summary>
        /// MRI's rb_iseq_min_max_arity: the fewest arguments a call can supply. All the required
        /// keywords together count as the single hash they arrive in.
        /// </summary>
        private int MinArgumentCount {
            get { return _leadingCount + _postCount + (_requiredKeywordCount > 0 ? 1 : 0); }
        }

        /// <summary>
        /// MRI's rb_proc_arity / rb_method_entry_arity. A lambda and a method report a negative
        /// arity as soon as the number of arguments is not fixed; a plain proc, which never
        /// complains about the count, reports a negative arity only when it can absorb an
        /// unbounded number of them, i.e. only when it has a rest parameter.
        /// </summary>
        public int GetArity(bool isLambda) {
            int min = MinArgumentCount;
            if (_hasRest) {
                return -min - 1;
            }
            if (!isLambda) {
                return min;
            }
            int max = _leadingCount + _optionalCount + _postCount + ((_hasKeywords || _hasKeywordRest) ? 1 : 0);
            return min == max ? min : -min - 1;
        }

        /// <summary>
        /// How Method#to_s spells the parameter list: "(a, b=..., *c, &blk)". MRI writes an
        /// anonymous block parameter as "..." and collapses the `*, **, &` triple that `def m(...)`
        /// produces into the same "...".
        /// </summary>
        public string/*!*/ ToParameterListString() {
            var result = new StringBuilder("(");
            bool first = true;
            foreach (var parameter in _parameters) {
                string text;
                switch (parameter.Kind) {
                    case "req": text = parameter.Name ?? "_"; break;
                    case "opt": text = parameter.Name + "=..."; break;
                    case "rest": text = "*" + (parameter.Name == "*" ? null : parameter.Name); break;
                    case "keyreq": text = parameter.Name + ":"; break;
                    case "key": text = parameter.Name + ": ..."; break;
                    case "keyrest": text = "**" + (parameter.Name == "**" ? null : parameter.Name); break;
                    case "nokey": text = "**nil"; break;
                    case "block": text = parameter.Name == "&" ? "..." : "&" + parameter.Name; break;
                    default: continue;
                }
                if (!first) {
                    result.Append(", ");
                }
                result.Append(text);
                first = false;
            }
            result.Append(')');
            return result.ToString() == "(*, **, ...)" ? "(...)" : result.ToString();
        }

        /// <summary>
        /// The #parameters array. A proc binds its positional parameters loosely, so what a lambda
        /// calls a required parameter a proc calls an optional one; nothing else differs.
        /// </summary>
        public RubyArray/*!*/ GetParameterArray(RubyContext/*!*/ context, bool isLambda) {
            var result = new RubyArray(_parameters.Length);
            foreach (var parameter in _parameters) {
                string kind = (!isLambda && parameter.Kind == "req") ? "opt" : parameter.Kind;
                var item = new RubyArray(2);
                item.Add(context.CreateAsciiSymbol(kind));
                if (parameter.Name != null) {
                    item.Add(context.EncodeIdentifier(parameter.Name));
                }
                result.Add(item);
            }
            return result;
        }
    }
}
