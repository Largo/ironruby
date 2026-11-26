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
using Microsoft.Scripting;

namespace IronRuby.Compiler.Ast {
    using Microsoft.Scripting.Utils;
using MSA = System.Linq.Expressions;

    
    // x = expression rescue jump_statement
    // x = expression rescue expression
    public partial class RescueExpression : Expression {
        private readonly SourceSpan _rescueSpan;
        private readonly Expression/*!*/ _guardedExpression;
        private readonly Expression/*!*/ _rescueClauseStatement;

        public Expression/*!*/ GuardedExpression {
            get { return _guardedExpression; }
        }

        public Expression/*!*/ RescueClauseStatement {
            get { return _rescueClauseStatement; }
        }
        
        public RescueExpression(Expression/*!*/ guardedExpression, Expression/*!*/ rescueClauseStatement, SourceSpan rescueSpan, SourceSpan location)
            : base(location) {
            ContractUtils.RequiresNotNull(guardedExpression, "guardedExpression");
            ContractUtils.RequiresNotNull(rescueClauseStatement, "rescueClauseStatement");

            _guardedExpression = guardedExpression;
            _rescueClauseStatement = rescueClauseStatement;
            _rescueSpan = rescueSpan;
        }

        /// <summary>
        /// The guarded expression is not a line of its own to TracePoint :line: set on the expression of an endless def
        /// (`def m = x rescue y'), which CRuby reports no line event for.
        /// </summary>
        public bool GuardedWithoutLine { get; set; }

        private Body/*!*/ ToBody(AstGenerator/*!*/ gen) {
            var guarded = new Statements();
            guarded.Add(_guardedExpression, GuardedWithoutLine ? Statements.NoLine : _guardedExpression.Location.Start.Line);
            return new Body(
                guarded,
                CollectionUtils.MakeList(new RescueClause(Array.Empty<Expression>(), null, new Statements(_rescueClauseStatement), _rescueSpan)),
            null, null, Location);
        }

        internal override MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen) {
            return ToBody(gen).TransformResult(gen, ResultOperation.Ignore);
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator gen) {
            return ToBody(gen).TransformRead(gen);
        }
    }
}
