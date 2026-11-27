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
using MSA = System.Linq.Expressions;

using System.Collections;
using System.Collections.Generic;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;
	
namespace IronRuby.Compiler.Ast {

    public class Arguments {
        internal static readonly Arguments Empty = new Arguments();

        private readonly Expression/*!*/[]/*!*/ _expressions;
        
        public bool IsEmpty {
            get { return _expressions.Length == 0; }
        }

        public Expression/*!*/[]/*!*/ Expressions { get { return _expressions; } }
        
        public Arguments() {
            _expressions = Array.Empty<Expression>();
        }

        public Arguments(Expression/*!*/ arg) {
            ContractUtils.RequiresNotNull(arg, "arg");
            _expressions = new Expression[] { arg };
        }

        public Arguments(Expression/*!*/[] expressions) {
            if (expressions != null) {
                Assert.NotNullItems(expressions);
            }

            _expressions = expressions ?? Array.Empty<Expression>();
        }

        #region Transform to Call/Yield Expression

        internal void TransformToCall(AstGenerator/*!*/ gen, CallSiteBuilder/*!*/ siteBuilder) {
            bool fresh;
            siteBuilder.SplattedArgument = TransformToCallInternal(gen, siteBuilder, out fresh);
            siteBuilder.SplattedArgumentIsFresh = fresh;
        }

        internal MSA.Expression/*!*/ TransformToYield(AstGenerator/*!*/ gen, MSA.Expression/*!*/ bfcVariable, MSA.Expression/*!*/ selfExpression) {
            var args = new List<MSA.Expression>();
            var splatted = TransformToCallInternal(gen, args, out _);
            // `yield x, **h': whether x may be auto-splatted depends on h being given at all (RubyOps.YieldKeywordSplat1)
            if (args.Count == 1 && _expressions.Length == 2 && _expressions[1] is KeywordArgumentSplat) {
                return Methods.YieldKeywordSplat1.OpCall(
                    AstUtils.Box(args[0]), AstUtils.Convert(splatted, typeof(IList)), AstFactory.NullOfProc,
                    AstUtils.Box(selfExpression), bfcVariable
                );
            }
            return AstFactory.YieldExpression(gen.Context, args, splatted, null, null, bfcVariable, selfExpression);
        }

        /// <summary>
        /// Adds arguments to the given collection (result) and returns a transformed splatted argument.
        /// </summary>
        /// <param name="fresh">The splatted argument is an array made for the call, which nothing else refers to.</param>
        private MSA.Expression TransformToCallInternal(AstGenerator/*!*/ gen, ICollection<MSA.Expression>/*!*/ result, out bool fresh) {
            fresh = false;
            int splattedCount;
            int firstSplatted = IndexOfSplatted(out splattedCount);

            for (int i = 0; i < (firstSplatted != -1 ? firstSplatted : _expressions.Length); i++) {
                result.Add(_expressions[i].TransformRead(gen));
            }

            // A call site splats one trailing array, so from the first splat on the arguments go into
            // one: foo(a,b,*c,*d,e,*f) is passed as foo(a,b,*[*c,*d,e,*f]).
            if (splattedCount == 1 && firstSplatted == _expressions.Length - 1) {
                // the method's rest parameter, passed on where nothing can see it again (ForwardedRest)
                var method = gen.CurrentMethod;
                fresh = method != null && gen.CurrentBlock == null && ReferenceEquals(_expressions[firstSplatted], method.ForwardedRestSplat);
                return _expressions[firstSplatted].TransformRead(gen);
            } else if (splattedCount >= 1) {
                fresh = true;
                return UnsplatArguments(gen, firstSplatted);
            } else {
                return null;
            }
        }

        internal int IndexOfSplatted(out int splattedCount) {
            splattedCount = 0;
            int result = -1;
            for (int i = 0; i < _expressions.Length; i++) {
                if (_expressions[i] is SplattedArgument) {
                    splattedCount++;
                    if (result == -1) {
                        result = i;
                    }
                }
            }

            return result;
        }
        
        internal MSA.Expression UnsplatArguments(AstGenerator/*!*/ gen, int start) {
            // [*array] == array, [*item] = [item]
            if (start == _expressions.Length - 1) {
                return Methods.Unsplat.OpCall(AstUtils.Box(_expressions[start].TransformRead(gen)));
            }
            
            // An argument list or array literal that starts with a splat copies it into an array with room for the items
            // that follow it, so that `f(*a, x)' and `[*a, x]' make one array of the size they end up rather than grow one
            // (RubyOps.SplatCopy). The list is copied before the items are evaluated, as it was: `[*a, a.pop]' has a's last
            // item twice.
            MSA.Expression array;
            int first = start;
            if (_expressions[start] is SplattedArgument) {
                int reserve = 0;
                for (int i = start + 1; i < _expressions.Length; i++) {
                    if (!(_expressions[i] is SplattedArgument)) {
                        reserve++;
                    }
                }
                array = Methods.SplatCopy.OpCall(AstUtils.Convert(_expressions[start].TransformRead(gen), typeof(IList)), AstUtils.Constant(reserve));
                first++;
            } else {
                array = Methods.MakeArray0.OpCall();
            }
            for (int i = first; i < _expressions.Length; i++) {
                if (_expressions[i] is SplattedArgument) {
                    array = Methods.AddRange.OpCall(array, _expressions[i].TransformRead(gen));
                } else {
                    array = Methods.AddItem.OpCall(array, AstUtils.Box(_expressions[i].TransformRead(gen)));
                }
                
            }
            return array;
        }

        #endregion

        #region Transform To Return Value or Array Initializer

        internal MSA.Expression/*!*/ TransformToArray(AstGenerator/*!*/ gen) {
            int splattedCount;
            int splatted = IndexOfSplatted(out splattedCount);
            if (splatted >= 0) {
                return UnsplatArguments(gen, 0);
            }

            // TODO: optimize big arrays
            return Methods.MakeArrayOpCall(gen.TransformExpressions(_expressions));
        }

        internal MSA.Expression/*!*/ TransformToReturnValue(AstGenerator/*!*/ gen) {
            if (_expressions.Length == 1) {
                return _expressions[0].TransformRead(gen);
            }

            return TransformToArray(gen);
        }

        #endregion
    }
}
