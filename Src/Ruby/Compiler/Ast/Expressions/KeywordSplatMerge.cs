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

#if FEATURE_CORE_DLR
using MSA = System.Linq.Expressions;
#else
using MSA = Microsoft.Scripting.Ast;
#endif

using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;

    /// <summary>
    /// One `**h` of keyword arguments that also has `k: v` pairs or other splats, e.g. `f(k: v, **h)`:
    /// h's entries go into the keyword hash built so far (see RubyOps.MergeKeywordSplat) when h is
    /// a Hash or nil. Anything else takes <see cref="Converted"/> - `hash.merge(h || {})`, which
    /// applies #to_hash - as every `**h` used to, at the price of a Ruby-level Hash#merge.
    /// </summary>
    public partial class KeywordSplatMerge : Expression {
        // `temp = <hash so far>` and `temp2 = <h>`, the temporaries Converted reads
        private readonly Expression/*!*/ _hash;
        private readonly Expression/*!*/ _splatted;
        private readonly Expression/*!*/ _converted;

        public Expression/*!*/ Hash {
            get { return _hash; }
        }

        public Expression/*!*/ Splatted {
            get { return _splatted; }
        }

        public Expression/*!*/ Converted {
            get { return _converted; }
        }

        public KeywordSplatMerge(Expression/*!*/ hash, Expression/*!*/ splatted, Expression/*!*/ converted, SourceSpan location)
            : base(location) {
            Assert.NotNull(hash, splatted, converted);
            _hash = hash;
            _splatted = splatted;
            _converted = converted;
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            return Ast.Coalesce(
                Methods.MergeKeywordSplat.OpCall(gen.CurrentScopeVariable,
                    AstUtils.Box(_hash.TransformRead(gen)), AstUtils.Box(_splatted.TransformRead(gen))),
                AstUtils.Box(_converted.TransformRead(gen))
            );
        }
    }
}
