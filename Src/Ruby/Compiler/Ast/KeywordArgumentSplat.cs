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

using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    /// <summary>
    /// A call-site keyword-argument hash that was written with at least one `**` splat, e.g.
    /// `f(1, **opts)` or `f(**a, **b)`.
    ///
    /// IronRuby models keyword arguments as a trailing positional Hash, which is Ruby 2
    /// behaviour and is right for `f(x: 1)`.  It is wrong for `**`: Ruby 3 drops the argument
    /// altogether when the splatted hash turns out to be empty, so `f(1, **{})` calls `f(1)`.
    /// Emptiness is only known at run time, so this produces a one-or-zero element array in
    /// place of the usual `to_a` splat conversion and lets the ordinary splatting machinery
    /// in <see cref="Arguments"/> flatten it away.
    ///
    /// A plain `f(1, {})` is *not* built this way and keeps passing the hash.
    ///
    /// Deriving from <see cref="SplattedArgument"/> (rather than wrapping one) is what makes
    /// Arguments.IndexOfSplatted see it, so no new NodeTypes entry is needed.
    /// </summary>
    public partial class KeywordArgumentSplat : SplattedArgument {
        public KeywordArgumentSplat(Expression/*!*/ hash)
            : base(hash) {
        }

        internal override MSA.Expression/*!*/ TransformRead(AstGenerator/*!*/ gen) {
            return Methods.SplatKeywordHash.OpCall(AstUtils.Box(Argument.TransformRead(gen)));
        }
    }
}
