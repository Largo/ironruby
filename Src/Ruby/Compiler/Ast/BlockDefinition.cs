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
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;

using IronRuby.Runtime.Osr;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstExpressions = ReadOnlyCollectionBuilder<MSA.Expression>;
    using AstParameters = ReadOnlyCollectionBuilder<MSA.ParameterExpression>;

    public partial class BlockDefinition : Block {
        //	{ |args| body }

        // self, block flow control:
        internal const int HiddenParameterCount = BlockDispatcher.HiddenParameterCount;

        // if the block has more parameters object[] is used:
        internal const int MaxBlockArity = BlockDispatcher.MaxBlockArity;

        /// <summary>
        /// IR_NESTED_PROCS=0: a block literal with a literal nested in it never reuses its Proc, and the nested literal does not
        /// clear the enclosing Procs' reuse (see Transform, reusesProc).
        /// </summary>
        internal static readonly bool NestedProcsEnabled = Environment.GetEnvironmentVariable("IR_NESTED_PROCS") != "0";

        private readonly Parameters/*!*/ _parameters;
        private readonly Statements/*!*/ _body;
        private readonly LexicalScope/*!*/ _definedScope;

        public Parameters/*!*/ Parameters {
            get { return _parameters; }
        }

        public Statements/*!*/ Body {
            get { return _body; }
        }

        public sealed override bool IsDefinition { get { return true; } }

        // The number of parameters excluding splat and proc:
        private int ParameterCount {
            get {
                return _parameters.Mandatory.Length + _parameters.Optional.Length;
            }
        }

        private bool HasFormalParametersInArray {
            get {
                return ParameterCount > MaxBlockArity || HasUnsplatParameter || HasProcParameter || HasPostParameters;
            }
        }

        // Mandatory parameters written after the optional/rest ones, as in |a, *b, c|.
        // A trailing Placeholder is not one of those: it is how `|a,|` spells "swallow the rest",
        // so it binds like a rest parameter rather than like a post parameter.
        private int PostParameterCount {
            get {
                int count = 0;
                for (int i = _parameters.LeadingMandatoryCount; i < _parameters.Mandatory.Length; i++) {
                    if (!(_parameters.Mandatory[i] is Placeholder)) {
                        count++;
                    }
                }
                return count;
            }
        }

        private bool HasPostParameters {
            get { return PostParameterCount > 0; }
        }

        /// <summary>
        /// Parameters a method's typed body takes as a def's (JitStub.ParameterShape): plain mandatory locals, then
        /// optional ones, no rest, post, block or keyword parameter.
        /// </summary>
        private bool IsJitMethodShape() {
            if (HasFormalParametersInArray || ParameterCount > IronRuby.Runtime.Jit.JitStub.MaxArity) {
                return false;
            }
            var signature = _parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords)) {
                return false;
            }
            foreach (var parameter in _parameters.Mandatory) {
                if (!(parameter is LocalVariable)) {
                    return false;
                }
            }
            foreach (var optional in _parameters.Optional) {
                if (!(optional.Left is LocalVariable)) {
                    return false;
                }
            }
            return true;
        }

        public LexicalScope/*!*/ DefinedScope {
            get { return _definedScope; }
        }

        private bool HasUnsplatParameter {
            get {
                return _parameters.Unsplat != null;
            }
        }

        private bool HasProcParameter {
            get {
                return _parameters.Block != null;
            }
        }

        public BlockDefinition(LexicalScope/*!*/ definedScope, Parameters parameters, Statements/*!*/ body, SourceSpan location)
            : base(location) {
            Assert.NotNull(definedScope, body);

            _definedScope = definedScope;
            _body = body;
            _parameters = parameters ?? Parameters.Empty; 
        }

        private AstParameters/*!*/ DefineParameters(out MSA.ParameterExpression/*!*/ selfVariable, out MSA.ParameterExpression/*!*/ blockParamVariable) {
            // Block signature:
            // <proc>, <self>, <leading-mandatory>, <trailing-mandatory>, <optional>, &<block>, *<unsplat>
            // or
            // <proc>, <self>, object[] { <leading-mandatory>, <trailing-mandatory>, <optional>, &<block> } *<unsplat>,
            var parameters = new AstParameters(
                HiddenParameterCount +
                (HasFormalParametersInArray ? 1 : ParameterCount) +
                (HasUnsplatParameter ? 1 : 0) +
                (HasProcParameter ? 1 : 0)
            );

            // hidden parameters:
            // #proc must be the first one - it is used as instance target for method invocation:
            parameters.Add(blockParamVariable = Ast.Parameter(typeof(BlockParam), "#bp"));
            parameters.Add(selfVariable = Ast.Parameter(typeof(object), "#self"));

            if (HasFormalParametersInArray) {
                parameters.Add(Ast.Parameter(typeof(object[]), "#parameters"));
            } else {
                for (int i = 0; i < ParameterCount; i++) {
                    parameters.Add(Ast.Parameter(typeof(object), "#" + i));
                }
            }

            if (HasUnsplatParameter) {
                parameters.Add(Ast.Parameter(typeof(RubyArray), "#array"));
            }

            if (HasProcParameter) {
                parameters.Add(Ast.Parameter(typeof(Proc), "#proc"));
            }

            return parameters;
        }

        /// <summary>
        /// IR_PARKED_BLOCK_SCOPES=0 (or IR_PARKED_SCOPES=0) makes a block that holds a block literal make a new scope and new
        /// Procs on every call, as it did before round thirty-seven (see RubyOps.InstantiateNestedParkedBlock).
        /// </summary>
        internal static readonly bool ParkedBlockScopesEnabled = MethodDefinition.ParkedScopesEnabled &&
            Environment.GetEnvironmentVariable("IR_PARKED_BLOCK_SCOPES") != "0";

        private ScopeBuilder/*!*/ DefineLocals(ScopeBuilder/*!*/ parentBuilder) {
            // a block's frame starts at its first line
            return new ScopeBuilder(_definedScope.AllocateClosureSlotsForLocals(0), parentBuilder, _definedScope) { CurrentLine = Location.Start.Line };
        }

        internal override MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen) {
            return Transform(gen, false);
        }

        internal MSA.Expression/*!*/ Transform(AstGenerator/*!*/ gen, bool isLambda) {
            ScopeBuilder scope = DefineLocals(gen.CurrentScope);

            // define hidden parameters and RHS-placeholders (#1..#n will be used as RHS of a parallel assignment):
            MSA.ParameterExpression blockParameter, selfParameter;
            var parameters = DefineParameters(out selfParameter, out blockParameter);

            MSA.ParameterExpression scopeVariable = scope.DefineHiddenVariable("#scope", typeof(RubyBlockScope));
            MSA.LabelTarget redoLabel = Ast.Label();

            gen.EnterBlockDefinition(
                scope,
                blockParameter,
                selfParameter,
                scopeVariable,
                redoLabel
            );
            gen.CurrentBlock.LexicalDepth = _definedScope.Depth;
            gen.CurrentBlock.HasOwnCompiledFrame = true;

            // -X:OSR: `n.times { }', `a.each { }' and the like run their loop in C#; a block that can be is outlined into that
            // loop once it is hot (BlockLoopSite). Screened from the AST before the body is compiled: whether the block's Proc
            // may be reused although literals are nested in it (see reusesProc below) is known to them as they are compiled.
            BlockLoopInfo loopInfo = (!isLambda && gen.CanReplaceLoops)
                ? BlockLoopInfo.TryCreate(this, gen.Context, _definedScope.Depth, gen.SourcePath + ":" + Location.Start.Line)
                : null;
            gen.CurrentBlock.ReusesProcWithNested = NestedProcsEnabled && loopInfo != null;

            // the binding of the parameters is one assignment of each, whatever it takes to make it
            gen.BindingParameters = true;
            MSA.Expression paramInit = MakeParametersInitialization(gen, parameters);
            gen.BindingParameters = false;
            gen.CountParameterAssignments(_parameters);
            MSA.ParameterExpression blockUnwinder;

            // `do ... rescue ... end`: the entry safe point goes inside the rescued statements, so that a
            // Thread#raise that came in between two calls of the block meets the rescue (see
            // RubyOps.CreateBlockScopeSafePointInBody).
            Body rescuedBody = (_body.Count == 1) ? _body.First as Body : null;
            bool safePointInBody = rescuedBody != null && rescuedBody.HasRescue;
            if (safePointInBody) {
                rescuedBody.SafePointFirst = true;
            }

            MSA.Expression statements = gen.TransformStatements(_body, ResultOperation.Return);
            bool hasNestedReturn = gen.CurrentBlock.HasNestedReturn;
            var outerVariables = gen.CurrentBlock.OuterVariables;
            var outerVariableAssignments = gen.CurrentBlock.OuterVariableAssignments;
            var assignedOuterVariables = gen.CurrentBlock.AssignedOuterVariables;

            // A body that never uses its scope - { }, { |x| x } - has no use for the RubyBlockScope
            // every invocation would allocate, except to hand to the TracePoint hooks, which only
            // evaluate their scope while a TracePoint listens: there it is made on demand.
            MSA.Expression lazyScope = null;
            if (!hasNestedReturn && !gen.TraceEnabled && !gen.CompilerOptions.IsEval && !scope.HasClosures && IsScopeFreeBody()) {
                var names = scope.GetVariableNamesExpression();
                Func<MSA.Expression> makeScope = () => Methods.CreateBlockScopeLazy.OpCall(
                    scope.LocalsTupleVariable, names, blockParameter, selfParameter, EnterInterpretedFrameExpression.Instance
                );
                var lazy = Ast.Coalesce(scopeVariable, Ast.Assign(scopeVariable, makeScope()));
                var rewriter = new LazyScopeRewriter(scopeVariable, lazy);
                var lazyParamInit = rewriter.Visit(paramInit);
                var lazyStatements = rewriter.Visit(statements);
                if (!rewriter.NeedsScope) {
                    paramInit = lazyParamInit;
                    statements = lazyStatements;
                    lazyScope = lazy;
                }
            }

            // A body that makes calls, or uses the enclosing scopes' locals or instance variables - { |e| o.cmp(e) },
            // { |ball| bounces += 1 if ball.bounce } - runs without its scope too, until a call needs it: the calls go through
            // sites with a light scope, which are handed the scope the block was defined in (see
            // MethodDefinition.LightFrameRewriter), and the enclosing scopes' locals are read from that scope too.
            MSA.ParameterExpression parentScope = null;
            if (lazyScope == null && MethodDefinition.LightCallsEnabled && !hasNestedReturn && !gen.TraceEnabled &&
                !gen.CompilerOptions.IsEval && IsLightCallBody()) {
                var names = scope.GetVariableNamesExpression();
                var parent = scope.DefineHiddenVariable("#parent", typeof(RubyScope));
                Func<MSA.Expression> makeScope = () => Methods.CreateBlockScopeLazy.OpCall(
                    scope.LocalsTupleVariable, names, blockParameter, selfParameter, EnterInterpretedFrameExpression.Instance
                );
                // a `while' in the body marks the frame, and a scope made inside it takes the mark (RubyOps.EnterLightLoop)
                var inLoop = scope.DefineHiddenVariable("#in-loop", typeof(bool));
                var lazy = Ast.Coalesce(scopeVariable, Ast.Assign(scopeVariable,
                    Ast.Call(typeof(RubyOps).GetMethod("LightBlockScopeInLoop"), makeScope(), inLoop)));
                // The calls are handed the scope made on demand once there is one: a call that made it may have changed what
                // the block's own scope answers - `Module.new { using R; o.respond_to?(:m) }', whose respond_to? reads only the
                // runtime and the refinements from the scope it is given ([DeclaringScope]).
                var callScope = Ast.Coalesce(AstUtils.Convert(scopeVariable, typeof(RubyScope)), parent);
                var rewriter = new MethodDefinition.LightFrameRewriter(scopeVariable, lazy, null, callScope, inLoop, false) {
                    Context = gen.Context
                };
                var lazyParamInit = rewriter.Visit(paramInit);
                var lazyStatements = rewriter.Visit(statements);
                if (!rewriter.NeedsScope) {
                    paramInit = lazyParamInit;
                    statements = lazyStatements;
                    lazyScope = lazy;
                    parentScope = parent;
                }
            }
            if (gen.Census != null) {
                gen.Census.CountBlock(lazyScope != null);
            }

            var bodyTry = AstUtils.Try(
                statements
            ).Catch(blockUnwinder = Ast.Parameter(typeof(BlockUnwinder), "#u"),
                // redo:
                AstUtils.IfThen(Ast.Field(blockUnwinder, BlockUnwinder.IsRedoField), Ast.Goto(redoLabel)),

                // next:
                gen.Return(Ast.Field(blockUnwinder, BlockUnwinder.ReturnValueField))
            );

            MSA.Expression enterNestedReturn, leaveNestedReturn;
            if (hasNestedReturn) {
                // a return in a nested block leaves this block if it runs as a lambda, however it was invoked:
                MSA.ParameterExpression lambdaUnwinder;
                // a LambdaUnwinder only (IsLambdaScopeUnwinderTarget), so that nothing else is caught on its way (see Proc.BuildCall)
                bodyTry = bodyTry.Filter(lambdaUnwinder = Ast.Parameter(typeof(LambdaUnwinder), "#lu"),
                    Methods.IsLambdaScopeUnwinderTarget.OpCall(scopeVariable, lambdaUnwinder),
                    gen.Return(Methods.GetLambdaUnwinderReturnValue.OpCall(lambdaUnwinder))
                );
                enterNestedReturn = Methods.EnterLambdaReturnScope.OpCall(scopeVariable);
                leaveNestedReturn = Methods.LeaveLambdaReturnScope.OpCall(scopeVariable);
            } else {
                enterNestedReturn = leaveNestedReturn = Ast.Empty();
            }

            // No handler for the exceptions that leave the block: it used to have one whose filter captured
            // their backtrace, but a filter is a catch and a rethrow, a CLR exception dispatch of its own per
            // block an exception leaves (see MethodDefinition.TransformBody, which does the same).
            MSA.Expression body = AstUtils.Try(
                enterNestedReturn,
                paramInit,
                Ast.Label(redoLabel),
                bodyTry
            ).Finally(
                leaveNestedReturn,
                Methods.ReleaseScopeFrame.OpCall(scopeVariable),
                Ast.Empty()
            );

            // A body that needs its scope but defines no closure over it can reuse the scope of the
            // previous call of the block, unless something captured that (see RubyScope.IsCaptured).
            // A nested block literal is a Proc over the scope on every call: the scope is reused only if each
            // literal in the body reuses its Proc, and is kept with the last one (RubyOps.ParkNestedProc).
            var nestedProcs = gen.CurrentBlock.ParkProcVariables;
            bool parkNested = ParkedBlockScopesEnabled && nestedProcs != null && !gen.CurrentBlock.HasUnparkableNestedBlock;
            bool reuseScope = lazyScope == null && !hasNestedReturn && (!gen.CurrentBlock.HasNestedBlock || parkNested) &&
                !gen.TraceEnabled && !gen.CompilerOptions.IsEval;

            // The same holds for the Proc of the literal (see BlockDispatcher.ReusesProc) - unless the block may be outlined
            // into its iterator's loop (ReusesProcWithNested), which runs the nested iterators in place and makes no nested Proc
            // (BlockLoopSite, NestedBlockLoop). Then the nested literal's Proc, when a generic call does make it, keeps this one
            // from being made again (RubyOps.KeepEnclosingProcs) - and so from being parked - and a call that made none leaves
            // this Proc - and the method's scope, if every literal of the method's own reuses its Proc - for the next call
            // (NBody's `advance': a scope and two Procs per call). IR_NESTED_PROCS=0 keeps the old rule; a block that is never
            // outlined keeps it too, as reusing its Proc would only cost it the bookkeeping.
            bool hasNestedBlock = gen.CurrentBlock.HasNestedBlock;
            bool reusesProc = !isLambda && !hasNestedReturn && (!hasNestedBlock || gen.CurrentBlock.ReusesProcWithNested) &&
                !gen.TraceEnabled && !gen.CompilerOptions.IsEval;

            // (a method all of whose literals reuse their Procs may park its scope with them: MethodDefinition.ParkedScopesEnabled)
            // A literal nested in a block keeps the enclosing literals' Procs from being made again (KeepEnclosingProcs below), and
            // so the method's scope from being parked, on a call that made it: only the literals the
            // method's scope makes have their Procs parked, and only they count.
            if (!(NestedProcsEnabled && gen.CurrentBlock.ParentBlock != null)) {
                gen.CurrentMethod.BlockLiteralCount++;
                if (!reusesProc) {
                    gen.CurrentMethod.HasUnreusableBlock = true;
                }
            }

            body = gen.Traceable
                ? new TraceReturnExpression(lazyScope ?? scopeVariable, gen.AddReturnTarget(body), TraceEvents.BReturn | TraceEvents.Return, gen.SourcePath, Location.End.Line)
                : gen.AddReturnTarget(body);

            if (reuseScope) {
                // the body stays in statement position: as a call argument it would have to be spilled
                var result = scope.DefineHiddenVariable("#result", typeof(object));
                bool keepLocals = scope.HasReusableLocals;
                MSA.Expression leave = Methods.LeaveReusableBlockScope.OpCall(scopeVariable, AstUtils.Constant(keepLocals));
                if (gen.CurrentBlock.HasNestedBlock) {
                    // the scope was kept on the BlockParam: with the last Proc of each literal if none may be held elsewhere,
                    // or not at all
                    MSA.Expression staged = null;
                    for (int i = 0; i < nestedProcs.Count; i++) {
                        var stage = Methods.ParkNestedProc.OpCall(scopeVariable, AstUtils.Constant(i), AstUtils.Constant(nestedProcs.Count),
                            nestedProcs[i]);
                        staged = (staged == null) ? stage : Ast.AndAlso(staged, stage);
                    }
                    var unpark = Methods.UnparkBlockScope.OpCall(scopeVariable);
                    leave = Ast.IfThen(leave, keepLocals
                        ? Ast.IfThenElse(staged, scope.MakeClearLocals(), unpark)
                        : Ast.IfThen(Ast.Not(staged), unpark));
                } else if (keepLocals) {
                    leave = Ast.IfThen(leave, scope.MakeClearLocals());
                }
                body = Ast.Block(
                    Ast.Assign(result, AstUtils.Convert(body, typeof(object))),
                    leave,
                    result
                );
            }

            // A scope-free body keeps its locals in CLR variables: { |x| x } would otherwise allocate their tuple on every call.
            MSA.Expression registerBody = (lazyScope != null) ? scope.TryUseRegisterLocals(body) : null;

            MSA.Expression enterScope;
            if (registerBody != null) {
                body = registerBody;
                enterScope = Ast.Block(
                    scope.MakeRegisterLocalsStorage(),
                    Ast.Condition(Methods.EnterScopeFreeBlock.OpCall(),
                        Methods.TraceBlockCall.OpCall(Methods.CreateBlockScopeLazy.OpCall(
                            scope.MaterializeLocals(), scope.GetVariableNamesExpression(), blockParameter, selfParameter,
                            EnterInterpretedFrameExpression.Instance
                        )),
                        AstUtils.Constant(null, typeof(RubyBlockScope))
                    )
                );
            } else if (reuseScope) {
                // the scope to reuse is taken first, its tuple from it (see RubyOps.TakeReusableBlockScope)
                var reusableScope = scope.DefineHiddenVariable("#reusable", typeof(RubyBlockScope));
                enterScope = Methods.CreateReusableBlockScope.OpCall(new AstExpressions {
                    scope.MakeReusableLocalsStorage(blockParameter, reusableScope, safePointInBody),
                    scope.GetVariableNamesExpression(),
                    blockParameter,
                    selfParameter,
                    EnterInterpretedFrameExpression.Instance,
                    reusableScope
                });
            } else {
                enterScope = (lazyScope != null ? Methods.CreateBlockScopeIfTraced :
                    safePointInBody ? Methods.CreateBlockScopeSafePointInBody : Methods.CreateBlockScope).OpCall(new AstExpressions {
                    scope.MakeLocalsStorage(),
                    scope.GetVariableNamesExpression(),
                    blockParameter, 
                    selfParameter,
                    EnterInterpretedFrameExpression.Instance
                });
            }

            body = (parentScope != null)
                ? scope.CreateScopeWithParent(scopeVariable, enterScope, parentScope,
                    Ast.Property(Ast.Property(blockParameter, typeof(BlockParam).GetProperty("Proc")), typeof(Proc).GetProperty("LocalScope")), body)
                : scope.CreateScope(scopeVariable, enterScope, body);

            gen.LeaveBlockDefinition();

            int parameterCount = ParameterCount;
            var attributes = _parameters.GetBlockSignatureAttributes();

            var blockDispatcher = BlockDispatcher.Create(parameterCount, PostParameterCount, attributes, gen.SourcePath, Location.Start.Line);
            blockDispatcher.ReusesProc = reusesProc;
            blockDispatcher.ParameterSignature = _parameters.Signature;
            if (outerVariables != null) {
                blockDispatcher.OuterVariableNames = outerVariables.ToArray();
                blockDispatcher.AssignedOuterVariableNames = assignedOuterVariables?.ToArray();
                blockDispatcher.OuterVariableAssignments = outerVariableAssignments.ToArray();
            }
            blockDispatcher.LoopInfo = loopInfo;
            if (assignedOuterVariables == null && !hasNestedReturn && !gen.TraceEnabled && !gen.CompilerOptions.IsEval &&
                IronRuby.Runtime.Jit.JitBlockMethod.Enabled && gen.Context.RubyOptions.Jit && IsJitMethodShape()) {
                // -X:JIT: a block that assigns none of the enclosing scopes' locals may become the body of a method
                // (define_method), which the method JIT can take as it takes a def's (JitBlockMethod); those it reads
                // the typed body reads from the scopes, as the block does
                blockDispatcher.JitAst = this;
            }

            var dispatcher = Ast.Constant(blockDispatcher, typeof(BlockDispatcher));
            string encodedName = RubyStackTraceBuilder.EncodeMethodName(BlockFrameLabel(gen), gen.SourcePath, Location, gen.DebugMode);

            // A literal that may reuse its Proc keeps the last one in a variable of the frame that makes it (the enclosing
            // method's or block's call): only that frame, on its own thread, ever makes the Proc again (Proc.TryReuse).
            MSA.Expression previousProc = reusesProc ? gen.CurrentScope.DefineHiddenVariable("#proc", typeof(Proc)) : null;

            // A literal written in the body of a method with a parking slot takes the Proc parked with the method's scope
            // (RubyOps.InstantiateParkedBlock); its hidden variable is what the method parks (MethodDefinition). Only a
            // literal of the method's own body: one in a `class << self' body inside the method has no block around it
            // either, but its hidden variable is the module body's, which the method's staging code cannot reach.
            var parkSlot = gen.CurrentMethod.ParkSlot;
            MSA.Expression instantiateReusable = null;
            // A literal written in the body of a block is kept with the block's scope when the block reuses that
            // (RubyOps.InstantiateNestedParkedBlock); any other literal nested in a block keeps the block from reusing its scope.
            var outerBlock = gen.CurrentBlock;
            if (reusesProc && parkSlot != null && outerBlock == null && gen.CurrentScope == gen.CurrentMethod.Builder) {
                instantiateReusable = Methods.InstantiateParkedBlock.OpCall(previousProc, gen.CurrentScopeVariable, gen.CurrentSelfVariable,
                    dispatcher, gen.CurrentMethod.ParkedVariable, AstUtils.Constant(gen.CurrentMethod.ParkProcVariables.Count));
                gen.CurrentMethod.ParkProcVariables.Add(previousProc);
                if (hasNestedBlock && blockDispatcher.LoopInfo != null) {
                    // the calls before the loop is outlined make the nested Proc and so cannot park (see BlockLoopInfo.ParkSlot)
                    blockDispatcher.LoopInfo.ParkSlot = parkSlot;
                }
            } else if (outerBlock != null && !(reusesProc && gen.CurrentScope == outerBlock.Builder)) {
                outerBlock.HasUnparkableNestedBlock = true;
                if (reusesProc) {
                    instantiateReusable = Methods.InstantiateReusableBlock.OpCall(previousProc, gen.CurrentScopeVariable, gen.CurrentSelfVariable, dispatcher);
                }
            } else if (outerBlock != null && ParkedBlockScopesEnabled) {
                var nested = outerBlock.ParkProcVariables ?? (outerBlock.ParkProcVariables = new System.Collections.Generic.List<MSA.Expression>());
                instantiateReusable = Methods.InstantiateNestedParkedBlock.OpCall(previousProc,
                    AstUtils.Convert(gen.CurrentScopeVariable, typeof(RubyBlockScope)), gen.CurrentSelfVariable, dispatcher,
                    AstUtils.Constant(nested.Count));
                nested.Add(previousProc);
            } else if (reusesProc) {
                instantiateReusable = Methods.InstantiateReusableBlock.OpCall(previousProc, gen.CurrentScopeVariable, gen.CurrentSelfVariable, dispatcher);
            }

            MSA.Expression instance = Ast.Coalesce(
                reusesProc ? instantiateReusable :
                (isLambda ? Methods.InstantiateLambda : Methods.InstantiateBlock).OpCall(gen.CurrentScopeVariable, gen.CurrentSelfVariable, dispatcher),
                (isLambda ? Methods.DefineLambda : Methods.DefineBlock).OpCall(gen.CurrentScopeVariable, gen.CurrentSelfVariable, dispatcher,
                    BlockDispatcher.CreateLambda(
                        new CompiledFrameExpression(body, encodedName, Location.Start.Line, isLambda && RubyContext.LazyLambdasEnabled),
                        encodedName,
                        parameters,
                        parameterCount,
                        PostParameterCount,
                        attributes
                    )
                )
            );
            if (EnclosingBlockReusesProc(gen)) {
                // a literal nested in a block whose Proc may be reused (see reusesProc above): its Proc reaches that one
                instance = Methods.KeepEnclosingProcs.OpCall(instance, gen.CurrentScopeVariable);
            }
            // a block of an eval'd text whose literals are kept per eval takes the eval's with it (AstGenerator.GetEvalLiterals)
            var evalLiterals = gen.GetEvalLiterals();
            if (evalLiterals != null) {
                instance = Methods.AttachEvalLiterals.OpCall(instance, evalLiterals);
            }
            return reusesProc ? Ast.Assign(previousProc, instance) : instance;
        }

        // A block this literal is nested in reuses its Proc although literals are nested in it (ReusesProcWithNested). Called
        // after LeaveBlockDefinition: the generator's block chain is the enclosing one.
        private static bool EnclosingBlockReusesProc(AstGenerator/*!*/ gen) {
            for (var block = gen.CurrentBlock; block != null; block = block.ParentBlock) {
                if (block.ReusesProcWithNested) {
                    return true;
                }
            }
            return false;
        }

        // MRI labels a block frame "block in <enclosing>", and counts the nesting once there
        // is more than one level. This runs after LeaveBlockDefinition, so the generator's
        // block chain is already the enclosing one and this block is the extra level.
        private static string/*!*/ BlockFrameLabel(AstGenerator/*!*/ gen) {
            string enclosing;
            AstGenerator.BlockScope outermost;
            int levels = 1;
            var module = gen.GetEnclosingModuleFrame();
            if (module != null) {
                enclosing = module.FrameLabel;
                outermost = module.OuterBlock;
            } else if (gen.CompilerOptions.EvalFrameBaseLabel != null && gen.CurrentMethod == gen.TopLevelScope) {
                // a block in an eval counts its levels from the frame the eval runs in
                enclosing = gen.CompilerOptions.EvalFrameBaseLabel;
                levels += gen.CompilerOptions.EvalFrameBlockLevels;
                outermost = null;
            } else {
                enclosing = gen.CurrentMethod.FrameLabel ?? gen.CurrentMethod.MethodName ?? gen.TopLevelFrameLabel;
                outermost = null;
            }

            for (var block = gen.CurrentBlock; block != null && block != outermost; block = block.ParentBlock) {
                levels++;
            }

            return AstGenerator.FormatBlockLabel(enclosing, levels);
        }

        /// <summary>
        /// Nothing in the parameters or the body needs the block's scope at runtime: plain
        /// parameters, and statements that only read and assign the block's own locals, literals and
        /// self. Every call site takes the scope, and so do constants, instance and global variables,
        /// so this is the call-free body: { }, { |x| x }, { |a, b| a }.
        /// The transformed body is still checked (LazyScopeRewriter); this is the cheap filter.
        /// </summary>
        private bool IsScopeFreeBody() {
            if (_parameters.Optional.Length > 0 || _parameters.Unsplat != null || _parameters.Block != null || HasPostParameters) {
                return false;
            }
            var signature = _parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords)) {
                return false;
            }
            foreach (var parameter in _parameters.Mandatory) {
                if (!(parameter is LocalVariable) && !(parameter is Placeholder)) {
                    return false;
                }
            }
            foreach (var statement in _body) {
                if (!IsScopeFree(statement)) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// IsScopeFreeBody for a body that may also make calls without a block and use instance variables, constants and the
        /// enclosing scopes' locals (MethodDefinition.IsScopeFree, which light-frame methods are filtered by).
        /// </summary>
        private bool IsLightCallBody() {
            if (_parameters.Optional.Length > 0 || _parameters.Unsplat != null || _parameters.Block != null || HasPostParameters) {
                return false;
            }
            var signature = _parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords)) {
                return false;
            }
            foreach (var parameter in _parameters.Mandatory) {
                if (!(parameter is LocalVariable) && !(parameter is Placeholder)) {
                    return false;
                }
            }
            foreach (var statement in _body) {
                if (!MethodDefinition.IsScopeFree(statement)) {
                    return false;
                }
            }
            return true;
        }

        private static bool IsScopeFree(Expression/*!*/ node) {
            if (node is LocalVariable || node is Literal || node is SelfReference) {
                return true;
            }
            var assignment = node as SimpleAssignmentExpression;
            return assignment != null && assignment.Operation == null && assignment.Left is LocalVariable && IsScopeFree(assignment.Right);
        }

        /// <summary>
        /// Points the TracePoint hooks of a scope-free body at the scope made on demand. Any other use
        /// of the scope, or a node it cannot look into, means the body needs its scope after all.
        /// </summary>
        private sealed class LazyScopeRewriter : MSA.ExpressionVisitor {
            private readonly MSA.ParameterExpression/*!*/ _scope;
            private readonly MSA.Expression/*!*/ _lazyScope;
            private int _inTraceHook;

            internal bool NeedsScope;

            internal LazyScopeRewriter(MSA.ParameterExpression/*!*/ scope, MSA.Expression/*!*/ lazyScope) {
                _scope = scope;
                _lazyScope = lazyScope;
            }

            protected override MSA.Expression/*!*/ VisitParameter(MSA.ParameterExpression/*!*/ node) {
                if (node == _scope) {
                    if (_inTraceHook > 0) {
                        return _lazyScope;
                    }
                    NeedsScope = true;
                }
                return node;
            }

            protected override MSA.Expression/*!*/ VisitExtension(MSA.Expression/*!*/ node) {
                if (node is TraceLineExpression) {
                    _inTraceHook++;
                    try {
                        return base.VisitExtension(node);
                    } finally {
                        _inTraceHook--;
                    }
                }
                if (node is SourceLineMarker) {
                    return node;
                }
                if (node is CompiledFrameExpression) {
                    return base.VisitExtension(node);
                }
                NeedsScope = true;
                return node;
            }
        }

        private MSA.Expression/*!*/ GetParameterAccess(AstParameters/*!*/ parameters, MSA.Expression paramsArray, int i) {
            return (paramsArray != null) ? (MSA.Expression)Ast.ArrayAccess(paramsArray, AstUtils.Constant(i)) : parameters[HiddenParameterCount + i];
        }

        private MSA.Expression/*!*/ GetPositionalParameterAccess(AstParameters/*!*/ parameters, MSA.Expression paramsArray, int i, bool clearsKeywords) {
            var access = GetParameterAccess(parameters, paramsArray, i);
            // the call only for a Hash (exactly, see RubyOps.ClearKeywordArguments): the test is all an argument of any other
            // class costs
            return clearsKeywords ? Ast.Condition(Ast.TypeEqual(access, typeof(Hash)), Methods.ClearKeywordArguments.OpCall(access), access) : access;
        }

        private MSA.Expression/*!*/ MakeParametersInitialization(AstGenerator/*!*/ gen, AstParameters/*!*/ parameters) {
            var result = new AstExpressions(
                ParameterCount + 
                (_parameters.Optional.Length > 0 ? 1 : 0) + 
                (_parameters.Unsplat != null ? 1 : 0) +
                (_parameters.Block != null ? 1 : 0) +
                1
             );

            // TODO: we can skip parameters that are locals (need to be defined as parameters, not as #n):

            var paramsArray = HasFormalParametersInArray ? parameters[HiddenParameterCount] : null;

            // A block that declares no keywords takes a Hash passed as keywords (yield(a: 1)) as a positional argument, which
            // it may keep: it stops saying it is keyword arguments, as for a method (RubyOps.ClearKeywordArguments). The
            // dispatcher of a block with few plain parameters does that before the call (BlockDispatcher.Positional).
            var signature = _parameters.Signature;
            bool clearsKeywords = signature != null && !signature.TakesKeywords && !signature.RefusesKeywords &&
                !BlockDispatcher.UnmarksKeywordArguments(ParameterCount, PostParameterCount, _parameters.GetBlockSignatureAttributes());

            int parameterIndex = 0;
            for (int i = 0; i < _parameters.Mandatory.Length; i++) {
                result.Add(_parameters.Mandatory[i].TransformWrite(gen, GetPositionalParameterAccess(parameters, paramsArray, parameterIndex, clearsKeywords)));
                parameterIndex++;
            }

            if (_parameters.Optional.Length > 0) {
                for (int i = 0; i < _parameters.Optional.Length; i++) {
                    result.Add(_parameters.Optional[i].Left.TransformWrite(gen, GetPositionalParameterAccess(parameters, paramsArray, parameterIndex, clearsKeywords)));
                    parameterIndex++;
                }
                result.Add(_parameters.TransformOptionalsInitialization(gen));
            }

            if (_parameters.Unsplat != null) {
                // the last parameter is unsplat:
                result.Add(_parameters.Unsplat.TransformWrite(gen, parameters[parameters.Count - (_parameters.Block != null ? 2 : 1)]));
            }

            if (_parameters.Block != null) {
                result.Add(_parameters.Block.TransformWrite(gen, parameters[parameters.Count - 1]));
                parameterIndex++;
            }

            result.Add(AstUtils.Empty());
            return Ast.Block(result);
        }
    }
}
