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

using MSA = System.Linq.Expressions;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    
    public partial class CaseExpression : Expression {
        
        //	case value 
        //		when args; statements
        //      ...
        //      when args; statements
        //  else
        //      statements
        //	end

        // equivalent to
        // value == null:
        //   if <expr> then <stmts> elseif <expr> then <stmts> else <stmts> end
        // value != null:
        //   if <expr> === <value> then <stmts> elseif <expr> === <value> then <stmts> else <stmts> end

        // the only tricky part is that the when clause can contain a splatted array:
        // case ...
        //     when arg0, ..., *argn; statements
        //     ...
        // end

        private readonly Expression _value;
        private readonly WhenClause/*!*/[]/*!*/ _whenClauses;
        private readonly Statements _elseStatements;

        public Expression Value {
            get { return _value; } 
        }

        public WhenClause/*!*/[]/*!*/ WhenClauses {
            get { return _whenClauses; }
        }

        public Statements ElseStatements {
            get { return _elseStatements; }
        }

        internal CaseExpression(Expression value, WhenClause/*!*/[] whenClauses, ElseIfClause elseClause, SourceSpan location)
            : this(value, whenClauses, (elseClause != null) ? elseClause.Statements : null, location) {
        }

        public CaseExpression(Expression value, WhenClause/*!*/[] whenClauses, Statements elseStatements, SourceSpan location)
            : base(location) {
            _value = value;
            _whenClauses = whenClauses ?? Array.Empty<WhenClause>();
            _elseStatements = elseStatements;
        }

        // when <expr>
        //   generates into:
        //   RubyOps.IsTrue(<expr>) if the case has no value, otherise:
        //   RubyOps.IsTrue(Call("===", <expr>, <value>))
        private static MSA.Expression/*!*/ MakeTest(AstGenerator/*!*/ gen, Expression/*!*/ expr, MSA.Expression value) {
            MSA.Expression transformedExpr = expr.TransformRead(gen);
            if (expr is SplattedArgument) {
                if (value != null) {
                    return Methods.ExistsUnsplatCompare.OpCall(
                        Ast.Constant(CallSite<Func<CallSite, object, object, object>>.Create(
                            RubyCallAction.Make(gen.Context, "===", RubyCallSignature.WithImplicitSelf(2))
                        )),
                        AstUtils.LightDynamic(ExplicitTrySplatAction.Make(gen.Context), transformedExpr),
                        AstUtils.Box(value)
                    );
                } else {
                    return Methods.ExistsUnsplat.OpCall(
                        AstUtils.LightDynamic(ExplicitTrySplatAction.Make(gen.Context), transformedExpr)
                    );
                }
            } else {
                if (value != null) {
                    return AstFactory.IsTrue(
                        CallSiteBuilder.InvokeMethod(gen.Context, "===", new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf),
                            gen.CurrentScopeVariable,
                            transformedExpr,
                            value
                        )
                    );
                } else {
                    return AstFactory.IsTrue(transformedExpr);
                }
            }
        }

        // when <expr0>, *<expr1>, ..., <exprN>
        // generates:
        // <MakeTest>(<expr0>) || <MakeArrayTest>(<expr1>) || ... [ || <MakeTest>(<exprN>) ]
        internal static MSA.Expression/*!*/ TransformWhenCondition(AstGenerator/*!*/ gen, Expression/*!*/[]/*!*/ comparisons, MSA.Expression value) {
            MSA.Expression result;
            if (comparisons.Length > 0) {
                result = MakeTest(gen, comparisons[comparisons.Length - 1], value);
                for (int i = comparisons.Length - 2; i >= 0; i--) {
                    result = Ast.OrElse(MakeTest(gen, comparisons[i], value), result);
                }
            } else {
                result = Ast.Constant(false);
            }

            return result;
        }

        /// <summary>
        /// IR_CASE_DISPATCH=0 makes every `when' of a `case' over String literals a `===' call, as before (see
        /// CaseStringSite).
        /// </summary>
        internal static readonly bool CaseDispatchEnabled = Environment.GetEnvironmentVariable("IR_CASE_DISPATCH") != "0";

        // A `case value' whose `when's are all String literals, each ASCII in an ASCII-compatible encoding (MRI's
        // opt_case_dispatch takes the same `case's, with other literals too): the site that looks the value up
        // among them, or null.
        private CaseStringSite TryMakeStringDispatch() {
            if (!CaseDispatchEnabled || _value == null || _whenClauses.Length == 0) {
                return null;
            }
            var literals = new List<string>();
            var clauses = new List<int>();
            for (int i = 0; i < _whenClauses.Length; i++) {
                if (_whenClauses[i].Comparisons.Length == 0) {
                    return null;
                }
                foreach (var comparison in _whenClauses[i].Comparisons) {
                    // a SymbolLiteral is a StringLiteral too, and `:all === "all"' is false: Strings only
                    var literal = comparison as StringLiteral;
                    if (literal == null || literal is SymbolLiteral) {
                        return null;
                    }
                    var str = literal.GetMutableString();
                    if (!str.Encoding.IsAsciiIdentity || !str.IsAscii()) {
                        return null;
                    }
                    literals.Add(str.ToString());
                    clauses.Add(i);
                }
            }
            return new CaseStringSite(literals.ToArray(), clauses.ToArray());
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            MSA.Expression result;
            if (_elseStatements != null) {
                // ... else body end
                result = gen.TransformStatementsToExpression(_elseStatements);
            } else {
                // no else clause => the result of the if-expression is nil:
                result = AstUtils.Constant(null);
            }

            MSA.Expression value;
            if (_value != null) {
                value = gen.CurrentScope.DefineHiddenVariable("#case-compare-value", typeof(object));
            } else {
                value = null;
            }

            // `when' i is taken if the site says so, or, if the site cannot tell, if its `===' calls say so:
            //   clause = CaseStringDispatch(scope, value, site)
            //   if (clause == NotHandled ? <condition 0> : clause == 0) ... elseif ... else ... end
            CaseStringSite site = TryMakeStringDispatch();
            MSA.ParameterExpression clause = (site != null) ? gen.CurrentScope.DefineHiddenVariable("#case-clause", typeof(int)) : null;

            for (int i = _whenClauses.Length - 1; i >= 0; i--) {
                MSA.Expression condition = TransformWhenCondition(gen, _whenClauses[i].Comparisons, value);
                if (site != null) {
                    condition = Ast.Condition(
                        Ast.Equal(clause, AstUtils.Constant(CaseStringSite.NotHandled)),
                        condition,
                        Ast.Equal(clause, AstUtils.Constant(i))
                    );
                }
                // emit: else (if (condition) body else result)
                result = AstFactory.Condition(
                    condition,
                    gen.TransformStatementsToExpression(_whenClauses[i].Statements),
                    result
                );
            }

            if (_value != null) {
                result = (site == null)
                    ? Ast.Block(
                        Ast.Assign(value, Ast.Convert(_value.TransformRead(gen), typeof(object))),
                        result
                    )
                    : Ast.Block(
                        Ast.Assign(value, Ast.Convert(_value.TransformRead(gen), typeof(object))),
                        Ast.Assign(clause, Methods.CaseStringDispatch.OpCall(gen.CurrentScopeVariable, value, AstUtils.Constant(site))),
                        result
                    );
            }

            return result;
        }
    }
}
