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
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using IronRuby.Runtime.Osr;
using IronRuby.Runtime.Precompiled;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstBlock = Microsoft.Scripting.Ast.BlockBuilder;

    // pre-test:
    //   while <expression> do <statements> end
    //   until <expression> do <statements> end
    // post-test:
    //   <statement> while <expression>             
    //   <statement> until <expression>
    //   <block-expression> while <expression>   
    //   <block-expression> until <expression>
    public partial class WhileLoopExpression : Expression {
        private readonly Expression _condition;
        private readonly Statements _statements;		// optional
        private readonly bool _isWhileLoop; // while or until
        private readonly bool _isPostTest;  // do-while or while-do 

        // A loop BlockLoopSite made of a block and its iterator (`n.times { ... }', `a.each { ... }'), not one
        // the source wrote: the method JIT's loop tier refuses an instance variable write in it
        // (JitCompiler.EmitLoopIVarWrite).
        internal bool IsBlockLoop;

        public Expression Condition {
            get { return _condition; }
        }

        public Statements Statements {
            get { return _statements; }
        }

        public bool IsWhileLoop {
            get { return _isWhileLoop; }
        }

        public bool IsPostTest {
            get { return _isPostTest; }
        }

        public WhileLoopExpression(Expression/*!*/ condition, bool isWhileLoop, bool isPostTest, Statements/*!*/ statements, SourceSpan location)
            : base(location) {

            ContractUtils.RequiresNotNull(condition, "condition");
            ContractUtils.RequiresNotNull(statements, "statements");

            _condition = condition;
            _isWhileLoop = isWhileLoop;
            _isPostTest = isPostTest;
            _statements = statements;
        }

        /// <summary>
        /// IR_OSR_YIELD=0: a loop with a yield in its body is never given to a compiled copy (the copy gives up at the yield).
        /// </summary>
        internal static readonly bool YieldLoopsEnabled = Environment.GetEnvironmentVariable("IR_OSR_YIELD") != "0";

        /// <summary>
        /// Stage 3a of Docs/jit-resume-deopt.md: the index of the one `yield' of the loop, if the loop is of the shape a compiled
        /// copy may run a yield in - a `while'/`until' tested first, one yield in statement position directly in its body with
        /// plain arguments, no other yield anywhere in it, and statements after it (its tail) that the generic loop can run
        /// again on its own (OsrYieldOutcome): no block, lambda or definition, no loop, no jump and no exception handling.
        /// -1 otherwise.
        /// </summary>
        internal int GetYieldIndex() {
            if (_isPostTest || !YieldLoopsEnabled) { return -1; }
            int index = -1;
            for (int i = 0; i < _statements.Count; i++) {
                var yieldCall = _statements[i] as YieldCall;
                if (yieldCall != null) {
                    if (index >= 0 || !IsPlainYield(yieldCall)) { return -1; }
                    index = i;
                }
            }
            if (index < 0) { return -1; }
            var finder = new YieldTailFinder();
            _condition.Walk(finder);
            for (int i = 0; i < _statements.Count && !finder.Refused; i++) {
                if (i == index) {
                    var args = ((YieldCall)_statements[i]).Arguments;
                    if (args != null) {
                        foreach (var arg in args.Expressions) { arg.Walk(finder); }
                    }
                } else {
                    finder.InTail = i > index;
                    _statements[i].Walk(finder);
                }
            }
            return finder.Refused ? -1 : index;
        }

        private static bool IsPlainYield(YieldCall/*!*/ node) {
            var args = node.Arguments;
            if (args == null) { return true; }
            foreach (var arg in args.Expressions) {
                var hash = arg as HashConstructor;
                if (arg is SplattedArgument || hash != null && hash.IsKeywordArguments) { return false; }
            }
            return true;
        }

        // Another yield anywhere in the loop, and in its tail anything whose transformation is not repeatable or that leaves it.
        private sealed class YieldTailFinder : Walker {
            internal bool Refused, InTail;

            public override bool Enter(YieldCall/*!*/ node) { Refused = true; return false; }
            public override bool Enter(BlockDefinition/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(LambdaDefinition/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(MethodDefinition/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(WhileLoopExpression/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(ForLoopExpression/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(Body/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(RescueExpression/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(BreakStatement/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(NextStatement/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(RedoStatement/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(RetryStatement/*!*/ node) { Refused |= InTail; return !Refused; }
            public override bool Enter(ReturnStatement/*!*/ node) { Refused |= InTail; return !Refused; }
        }

        private static readonly System.Reflection.FieldInfo/*!*/ _OutcomeBfc = typeof(OsrYieldOutcome).GetField(nameof(OsrYieldOutcome.Bfc));
        private static readonly System.Reflection.FieldInfo/*!*/ _OutcomeResult = typeof(OsrYieldOutcome).GetField(nameof(OsrYieldOutcome.Result));

        /// <summary>
        /// The copy handed an iteration back after its yield (OsrYieldOutcome): the block's jump gets the generic yield's
        /// post-yield test, which returns from the method on a `break' (MethodYield; a light frame's LightMethodYield, which
        /// the frame's rewriter puts in its place), then the statements after the yield run here and the loop goes on at its
        /// head, the next back edge asking the copy again.
        /// </summary>
        private static MSA.Expression/*!*/ MakeYieldHandBack(AstGenerator/*!*/ gen, MSA.Expression/*!*/ osrResult, MSA.Expression/*!*/ tail,
            MSA.Expression/*!*/ osrCountdown, MSA.LabelTarget/*!*/ continueLabel) {
            var outcome = Ast.Variable(typeof(OsrYieldOutcome), "#yield-outcome");
            var bfc = Ast.Field(outcome, _OutcomeBfc);
            var result = Ast.Field(outcome, _OutcomeResult);
            return Ast.Block(new[] { outcome },
                Ast.Assign(outcome, Ast.TypeAs(osrResult, typeof(OsrYieldOutcome))),
                Ast.IfThen(Ast.NotEqual(outcome, AstUtils.Constant(null, typeof(OsrYieldOutcome))),
                    Ast.Block(
                        Ast.IfThen(
                            Ast.AndAlso(
                                Ast.NotEqual(bfc, AstUtils.Constant(null, typeof(BlockParam))),
                                Methods.MethodYield.OpCall(gen.CurrentScopeVariable, bfc, result)
                            ),
                            gen.Return(result)
                        ),
                        tail,
                        Ast.Assign(osrCountdown, AstUtils.Constant(1L)),
                        Ast.Continue(continueLabel)
                    )
                )
            );
        }

        // break, next and redo in the loop's body are jumps to its labels (Break/Next/RedoStatement)
        private static readonly System.Reflection.MethodInfo/*!*/ _LeaveLoopLine = typeof(PrecompiledFrames).GetMethod(nameof(PrecompiledFrames.LeaveLoopLine));

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            MSA.Expression resultVariable = gen.CurrentScope.DefineHiddenVariable("#loop-result", typeof(object));
            MSA.Expression redoVariable = gen.CurrentScope.DefineHiddenVariable("#skip-condition", typeof(bool));
            MSA.ParameterExpression unwinder;

            bool isInnerLoop = gen.CurrentLoop != null;

            // -X:OSR: the loop counts its own back edges, and when it has taken enough of them
            // it asks its site for a type-specialized copy of itself and finishes there. The
            // locals it would carry over are in the enclosing scope's tuple already, so there
            // is nothing to materialize: the copy is handed the same tuples and picks up at the
            // top of the next iteration.
            bool osr = gen.CanReplaceLoops;
            MSA.Expression osrCountdown = null, osrResult = null;
            OsrLoopSite osrSite = null;
            if (osr) {
                osrCountdown = gen.CurrentScope.DefineHiddenVariable("#osr-n", typeof(long));
                osrResult = gen.CurrentScope.DefineHiddenVariable("#osr-result", typeof(object));
                osrSite = new OsrLoopSite(gen.SourcePath + ":" + Location.Start.Line);
                osrSite.LoopAst = this;
                osrSite.Context = gen.Context;
                osrSite.ScopeDepth = gen.CurrentScope.LexicalDepth;
            }

            MSA.LabelTarget breakLabel = Ast.Label();
            MSA.LabelTarget continueLabel = Ast.Label();

            // the frame's line record while the interpreter runs the loop compiled (SourceLineMarker); an inner loop of the
            // same frame shares the outer one's
            var outerLoop = gen.CurrentLoop;
            bool ownsLoopLine = outerLoop == null || outerLoop.LoopLineScope != gen.CurrentScope;
            MSA.Expression loopLineVariable = ownsLoopLine ?
                gen.CurrentScope.DefineHiddenVariable("#loop-line", typeof(PrecompiledFrames.LoopLine)) : outerLoop.LoopLineVariable;

            // A loop with a yield its compiled copy may run (GetYieldIndex): in a method's own body, not a block's or an eval's,
            // whose yields answer the block's or the eval's post-yield test.
            int yieldIndex = (osr && gen.CurrentBlock == null && !gen.CompilerOptions.IsEval && gen.CurrentMethod != null) ? GetYieldIndex() : -1;

            gen.EnterLoop(redoVariable, resultVariable, breakLabel, continueLabel);
            gen.CurrentLoop.LoopLineVariable = loopLineVariable;
            gen.CurrentLoop.LoopLineScope = gen.CurrentScope;
            MSA.Expression transformedBody = gen.TransformStatements(_statements, ResultOperation.Ignore);
            // The statements after the yield once more, which the generic loop runs when the copy hands an iteration back after
            // its yield (OsrYieldOutcome): the same code, with the same line markers and :line hooks.
            MSA.Expression transformedTail = null;
            if (yieldIndex >= 0) {
                var tail = new Statements();
                for (int i = yieldIndex + 1; i < _statements.Count; i++) {
                    tail.Add(_statements[i], _statements.GetStartLine(i));
                }
                transformedTail = (tail.Count > 0) ? gen.TransformStatements(tail, ResultOperation.Ignore) : AstUtils.Empty();
            }
            MSA.Expression transformedCondition = _condition.TransformCondition(gen, true);
            gen.LeaveLoop();

            MSA.Expression conditionPositiveStmt, conditionNegativeStmt;
            if (_isWhileLoop) {
                conditionPositiveStmt = AstUtils.Empty();
                conditionNegativeStmt = Ast.Break(breakLabel);
            } else {
                conditionPositiveStmt = Ast.Break(breakLabel);
                conditionNegativeStmt = AstUtils.Empty();
            }

            // The tuples the specialized copy addresses the loop's locals through: this scope's
            // own, and one per outer scope the body reached into. Read after the body has been
            // transformed, which is what creates the closure variables.
            MSA.Expression osrArguments = null;
            if (osr) {
                var tupleArgIndex = new Dictionary<int, int>();
                var tupleArguments = new List<MSA.Expression>();
                foreach (var entry in gen.CurrentScope.TupleVariablesByDepth) {
                    if (!tupleArgIndex.ContainsKey(entry.Key)) {
                        tupleArgIndex.Add(entry.Key, tupleArguments.Count);
                        tupleArguments.Add(AstUtils.Box(entry.Value));
                    }
                }
                osrSite.TupleArgIndex = tupleArgIndex;
                // and self, which the instance variables the loop reads belong to
                osrSite.SelfArgIndex = tupleArguments.Count;
                tupleArguments.Add(AstUtils.Box(gen.CurrentSelfVariable));
                // and the scope, which the constants the loop reads resolve in, and whose refinements
                // the copy's operators are resolved under
                osrSite.ScopeArgIndex = tupleArguments.Count;
                tupleArguments.Add(AstUtils.Convert(gen.CurrentScopeVariable, typeof(object)));
                // and the method's block, which a yield the copy makes yields to
                if (yieldIndex >= 0) {
                    osrSite.BlockArgIndex = tupleArguments.Count;
                    tupleArguments.Add(AstUtils.Convert(gen.MakeMethodBlockParameterRead(), typeof(object)));
                }
                osrArguments = Ast.NewArrayInit(typeof(object), tupleArguments);
            }

            // The back-edge test, at the top of every iteration: two compares and a decrement of
            // a local. Once the site has given up, its countdown is Int64.MaxValue and the test
            // costs exactly that and nothing more, for the whole life of the program.
            //
            // A pending redo must not trip it: the specialized copy starts at the condition, and
            // a redo is precisely the state in which the condition has to be skipped.
            MSA.Expression backEdge = osr ? (MSA.Expression)AstUtils.IfThen(
                Ast.AndAlso(
                    Ast.AndAlso(
                        Ast.GreaterThan(osrCountdown, AstUtils.Constant(0L)),
                        Ast.Not(redoVariable)
                    ),
                    Ast.Equal(Ast.Assign(osrCountdown, Ast.Add(osrCountdown, AstUtils.Constant(-1L))), AstUtils.Constant(0L))
                ),
                Ast.Block(
                    Ast.Assign(osrResult, Ast.Call(Ast.Constant(osrSite, typeof(OsrLoopSite)), OsrLoopSite.RunMethod, osrArguments)),
                    (transformedTail == null) ? (MSA.Expression)AstUtils.Empty() : MakeYieldHandBack(gen, osrResult, transformedTail, osrCountdown, continueLabel),
                    AstUtils.If(
                        Ast.Not(Ast.ReferenceEqual(osrResult, Ast.Constant(OsrLoopSite.Retry, typeof(object)))),
                        Ast.Assign(resultVariable, osrResult),
                        Ast.Break(breakLabel),
                        AstUtils.Empty()
                    ).Else(
                        // The copy handed the loop back. Take the site's budget again rather
                        // than run out the rest of this entry generically: without this the
                        // back edge fires exactly once per entry, so a loop that deopts in its
                        // first thousandth - an accumulator crossing 2^31, say - is never looked
                        // at again, and the specialization is worth nothing on exactly the shape
                        // it was meant for.
                        Ast.Assign(osrCountdown, Ast.Field(Ast.Constant(osrSite, typeof(OsrLoopSite)), OsrLoopSite.CountdownField)),
                        AstUtils.Empty()
                    ),
                    AstUtils.Empty()
                )
            ) : AstUtils.Empty();

            // make the loop first:
            MSA.Expression loop = new AstBlock {
                gen.ClearDebugInfo(),
                Ast.Assign(redoVariable, AstUtils.Constant(_isPostTest)),

                AstFactory.Infinite(breakLabel, continueLabel,
                    // The interrupt check on the back edge, so that Thread#raise, Thread#kill and
                    // Timeout reach a thread spinning in this loop (RubyUtils.SafePoint: one
                    // volatile load and a branch while nothing is pending). It carries the loop's
                    // own line, which is where MRI reports an exception raised at a back edge;
                    // without it the frame would show the cleared sequence point (0xFEEFEE).
                    gen.AddDebugInfo(Ast.Call(RubyUtils.SafePointMethod), Location, loopLineVariable),
                    backEdge,
                    AstUtils.Try(

                        AstUtils.If(redoVariable, 
                            Ast.Assign(redoVariable, AstUtils.Constant(false))
                        ).ElseIf(transformedCondition,
                            conditionPositiveStmt
                        ).Else(
                            conditionNegativeStmt
                        ),

                        transformedBody,
                        AstUtils.Empty()

                    ).Catch(unwinder = Ast.Parameter(typeof(BlockUnwinder), "#u"), 
                        // redo = u.IsRedo
                        Ast.Assign(redoVariable, Ast.Field(unwinder, BlockUnwinder.IsRedoField)),
                        AstUtils.Empty()

                    ).Filter(unwinder = Ast.Parameter(typeof(EvalUnwinder), "#u"), 
                        Ast.Equal(Ast.Field(unwinder, EvalUnwinder.ReasonField), AstFactory.BlockReturnReasonBreak),

                        // result = unwinder.ReturnValue
                        Ast.Assign(resultVariable, Ast.Field(unwinder, EvalUnwinder.ReturnValueField)),
                        Ast.Break(breakLabel)
                    )
                ),
                gen.ClearDebugInfo(),
                AstUtils.Empty(),
            };

            // The frame's line record lets go of the frame once the loop is left - it would keep the frame's locals alive after
            // it returned - and is looked up again the next time (PrecompiledFrames.LeaveLoopLine). Not when an exception
            // leaves: the record keeps its line, which the exception's backtrace reads by depth.
            if (ownsLoopLine) {
                loop = AstUtils.Try(
                    loop
                ).Finally(
                    AstUtils.IfThen(Ast.NotEqual(loopLineVariable, AstUtils.Constant(null, typeof(PrecompiledFrames.LoopLine))),
                        Ast.Call(_LeaveLoopLine, loopLineVariable),
                        Ast.Assign(loopLineVariable, AstUtils.Constant(null, typeof(PrecompiledFrames.LoopLine)))
                    )
                );
            }

            // wrap it to try finally that updates RFC state:
            if (!isInnerLoop) {
                loop = AstUtils.Try(
                    Methods.EnterLoop.OpCall(gen.CurrentScopeVariable),
                    loop
                ).Finally(
                    Methods.LeaveLoop.OpCall(gen.CurrentScopeVariable)
                );
            }

            if (!osr) {
                return Ast.Block(loop, resultVariable);
            }

            // The budget this entry gets, and, on the way out, what it did not spend - so that a
            // loop entered many times for a few iterations each still adds up to a compilation.
            var site = Ast.Constant(osrSite, typeof(OsrLoopSite));
            return Ast.Block(
                Ast.Assign(osrCountdown, Ast.Field(site, OsrLoopSite.CountdownField)),
                loop,
                AstUtils.IfThen(
                    Ast.AndAlso(
                        Ast.Field(site, OsrLoopSite.CountingField),
                        Ast.LessThan(osrCountdown, Ast.Field(site, OsrLoopSite.CountdownField))
                    ),
                    Ast.Assign(Ast.Field(site, OsrLoopSite.CountdownField), osrCountdown)
                ),
                resultVariable
            );
        }

        internal override MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen) {
            // do not mark a sequence point wrapping the entire node:
            return TransformRead(gen);
        }        
    }
}
