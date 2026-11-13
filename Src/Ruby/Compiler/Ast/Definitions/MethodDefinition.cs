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
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Compiler.Ast {
    using Ast = MSA.Expression;
    using AstUtils = Microsoft.Scripting.Ast.Utils;
    using AstExpressions = ReadOnlyCollectionBuilder<MSA.Expression>;
    using AstParameters = ReadOnlyCollectionBuilder<MSA.ParameterExpression>;
    
    public partial class MethodDefinition : DefinitionExpression {
        // self, block
        internal const int HiddenParameterCount = 2;
            
        /// <summary>
        /// Non-null for singleton/class methods.
        /// </summary>
        private readonly Expression _target;

        private readonly string/*!*/ _name;
        private readonly Parameters/*!*/ _parameters;

        public Expression Target {
            get { return _target; }
        }

        public string/*!*/ Name { 
            get { return _name; }
        }

        public Parameters/*!*/ Parameters {
            get { return _parameters; }
        }

        /// <summary>
        /// MRI's use_block: the method declares a block parameter, yields, or calls super passing
        /// its block on. A block given to a method without it may be ignored, which MRI warns about.
        /// </summary>
        // The prism bridge works this out; a method built any other way is assumed to use its
        // block, so it is never warned about or given the block-less fast path.
        public bool UsesBlock { get; set; } = true;

        public MethodDefinition(LexicalScope/*!*/ definedScope, Expression target, string/*!*/ name, Parameters parameters, Body/*!*/ body, 
            SourceSpan location)
            : base(definedScope, body, location) {
            Assert.NotNull(name);

            // only for-loop block might use other than local variable for unsplat:
            Debug.Assert(parameters.Unsplat == null || parameters.Unsplat is LocalVariable);
                
            _target = target;
            _name = name;
            _parameters = parameters ?? Parameters.Empty;
        }

        /// <summary>
        /// A method whose body the front end builds when it is first asked for - normally when the method
        /// is first called. Building it adds the body's locals to <paramref name="definedScope"/>.
        /// </summary>
        public MethodDefinition(LexicalScope/*!*/ definedScope, Expression target, string/*!*/ name, Parameters parameters,
            Func<Body>/*!*/ bodyFactory, SourceSpan location)
            : base(definedScope, bodyFactory, location) {
            Assert.NotNull(name);
            Debug.Assert(parameters == null || parameters.Unsplat == null || parameters.Unsplat is LocalVariable);

            _target = target;
            _name = name;
            _parameters = parameters ?? Parameters.Empty;
        }

        private ScopeBuilder/*!*/ DefineLocals(out AstParameters/*!*/ parameters) {
            // Method signature:
            // <self>, &<block>, <leading-mandatory>, <trailing-mandatory>, <optional>, *<unsplat>
            
            parameters = new AstParameters(
                HiddenParameterCount +
                _parameters.Mandatory.Length +
                _parameters.Optional.Length +
                (_parameters.Unsplat != null ? 1 : 0)
            );

            int closureIndex = 0;
            int firstClosureParam = 1;
            parameters.Add(Ast.Parameter(typeof(object), "#self"));

            if (_parameters.Block != null) {
                parameters.Add(Ast.Parameter(typeof(Proc), _parameters.Block.Name));
                _parameters.Block.SetClosureIndex(closureIndex++);
            } else {
                parameters.Add(Ast.Parameter(typeof(Proc), "#block"));
                firstClosureParam++;
            }

            foreach (LeftValue lvalue in _parameters.Mandatory) {
                var param = lvalue as LocalVariable;
                if (param != null) {
                    parameters.Add(Ast.Parameter(typeof(object), param.Name));
                    param.SetClosureIndex(closureIndex++);
                } else {
                    // TODO:
                    throw new NotSupportedException("TODO: compound parameters");
                }
            }

            foreach (var lvalue in _parameters.Optional) {
                var param = (LocalVariable)lvalue.Left;
                parameters.Add(Ast.Parameter(typeof(object), param.Name));
                param.SetClosureIndex(closureIndex++);
            }

            if (_parameters.Unsplat != null) {
                var unsplatLocal = (LocalVariable)_parameters.Unsplat;
                parameters.Add(Ast.Parameter(typeof(object), unsplatLocal.Name));
                unsplatLocal.SetClosureIndex(closureIndex++);
            }

            // allocate closure slots for locals:
            int localCount = DefinedScope.AllocateClosureSlotsForLocals(closureIndex);

            return new ScopeBuilder(parameters, firstClosureParam, localCount, null, DefinedScope);
        }

        /// <summary>
        /// The label MRI 3.4 and later puts on a method frame: "M::C#foo" for an instance method,
        /// "M::C.foo" for a method on a module's singleton, and the bare name where there is no
        /// module to name - a singleton method of an ordinary object (including main), or a method
        /// defined in an anonymous module.
        ///
        /// The owner is only known once the method is defined, which is also when the body is
        /// compiled, so the label can be baked into the frame name along with everything else.
        /// </summary>
        internal static string/*!*/ QualifyFrameLabel(string/*!*/ name, RubyModule declaringModule) {
            if (declaringModule == null) {
                return name;
            }

            RubyClass cls = declaringModule as RubyClass;
            if (cls != null && cls.IsSingletonClass) {
                // A top-level `def' of hosted code leaves a copy in main's singleton class, but that copy runs as Object's
                // method (RubyMethodInfo.BodyModule) and gets here with Object. What does get here with main's singleton
                // class is a singleton method of main - `def self.x', a `def' in main.instance_eval -, labelled "x".
                RubyModule attached = cls.SingletonClassOf as RubyModule;
                return (attached != null && attached.Name != null) ? attached.Name + "." + name : name;
            }

            return (declaringModule.Name != null) ? declaringModule.Name + "#" + name : name;
        }

        internal MSA.LambdaExpression/*!*/ TransformBody(AstGenerator/*!*/ gen, RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule,
            StrongBox<RefinementActivation> definitionRefinements) {
            return TransformBody(gen, ExactConstant(declaringScope, typeof(RubyScope)), ExactConstant(declaringModule, typeof(RubyModule)),
                QualifyFrameLabel(_name, declaringModule), definitionRefinements, true);
        }

        /// <summary>
        /// A compiled lambda keeps its object constants in an object[] and casts each one back on
        /// every load. Typed as their base class the declaring scope (a top-level, module or method
        /// scope) and module (a class, mostly) miss the inline exact-type check and go to the
        /// runtime's class-cast helper, twice per call of the method; typed as what they are, the
        /// cast is one compare, and the up-cast to the parameter type is free.
        /// </summary>
        private static MSA.Expression/*!*/ ExactConstant(object/*!*/ value, Type/*!*/ type) {
            return AstUtils.Convert(Ast.Constant(value, value.GetType()), type);
        }

        /// <summary>
        /// The body with the declaring scope and module given as expressions rather than baked-in
        /// objects. Ahead-of-time compilation (Util/aot) uses it: there, both are only known when
        /// the `def' runs, so they are parameters of a factory that builds the body's delegate.
        /// </summary>
        internal MSA.LambdaExpression/*!*/ TransformBody(AstGenerator/*!*/ gen, MSA.Expression/*!*/ declaringScope, MSA.Expression/*!*/ declaringModule,
            string/*!*/ frameLabel, StrongBox<RefinementActivation> definitionRefinements) {
            return TransformBody(gen, declaringScope, declaringModule, frameLabel, definitionRefinements, false);
        }

        /// <summary>
        /// <paramref name="allowLightFrame"/>: whether the body may be a light frame (see TransformLightFrame),
        /// whose scope description is an object constant - so not where the body is compiled ahead of time.
        /// </summary>
        private MSA.LambdaExpression/*!*/ TransformBody(AstGenerator/*!*/ gen, MSA.Expression/*!*/ declaringScope, MSA.Expression/*!*/ declaringModule,
            string/*!*/ frameLabel, StrongBox<RefinementActivation> definitionRefinements, bool allowLightFrame) {
            string encodedName = RubyStackTraceBuilder.EncodeMethodName(frameLabel, gen.SourcePath, Location, gen.DebugMode);

            // a body built on demand adds its locals to the scope: build it before they are allocated
            Body methodBody = Body;

            AstParameters parameters;
            ScopeBuilder scope = DefineLocals(out parameters);

            var scopeVariable = scope.DefineHiddenVariable("#scope", typeof(RubyMethodScope));
            var selfParameter = parameters[0];
            var blockParameter = parameters[1];

            // exclude block parameter even if it is explicitly specified:
            int visiblePrameterCountAndSignatureFlags = (parameters.Count - 2) << 2;
            if (_parameters.Block != null) {
                visiblePrameterCountAndSignatureFlags |= RubyMethodScope.HasBlockFlag;
            }
            if (_parameters.Unsplat != null) {
                visiblePrameterCountAndSignatureFlags |= RubyMethodScope.HasUnsplatFlag;
            }

            gen.EnterMethodDefinition(
                scope,
                selfParameter,
                scopeVariable,
                blockParameter,
                _name,
                _parameters
            );

            // Blocks written inside this method are labelled "block in <this frame's label>",
            // so they need the qualified form rather than the bare method name.
            gen.CurrentMethod.FrameLabel = frameLabel;
            gen.CurrentMethod.HasOwnCompiledFrame = true;

            // profiling:
            MSA.Expression profileStart = AstUtils.Empty();
            MSA.Expression profileEnd = AstUtils.Empty();

            if (gen.Profiler != null) {
                int profileTickIndex = gen.Profiler.GetTickIndex(encodedName);
                var stampVariable = scope.DefineHiddenVariable("#stamp", typeof(long));
                profileStart = Ast.Assign(stampVariable, Methods.Stopwatch_GetTimestamp.OpCall());
                profileEnd = Methods.UpdateProfileTicks.OpCall(AstUtils.Constant(profileTickIndex), stampVariable);
            }

            MSA.ParameterExpression unwinder;
            
            // see RubyScope.GetActiveRefinements:
            MSA.Expression recordRefinements = (definitionRefinements != null)
                ? (MSA.Expression)Ast.Assign(
                    Ast.Property(scopeVariable, typeof(RubyMethodScope).GetProperty("DefinitionRefinements")),
                    Ast.Constant(definitionRefinements, typeof(StrongBox<RefinementActivation>)))
                : AstUtils.Empty();

            // :methods coverage: one call, counted where the frame is entered
            MSA.Expression countCall;
            var methodCoverage = gen.MethodCoverage;
            if (methodCoverage != null) {
                countCall = Ast.Call(AstUtils.Constant(methodCoverage), typeof(MethodCoverage).GetMethod("Count"));
            } else {
                countCall = AstUtils.Empty();
            }

            gen.BindingParameters = true;
            MSA.Expression optionalsInitialization = _parameters.TransformOptionalsInitialization(gen);
            gen.BindingParameters = false;
            gen.CountParameterAssignments(_parameters);
            MSA.Expression statements = methodBody.TransformResult(gen, ResultOperation.Return);

            // A body that never needs its scope makes none (see TransformLightFrame). The optional parameters' defaults are
            // rewritten with it (AreLightOptionals); they run first either way.
            LightFrame light = null;
            if (_parameters.Optional.Length > 0) {
                statements = Ast.Block(optionalsInitialization, statements);
                optionalsInitialization = AstUtils.Empty();
            }
            if (allowLightFrame && LightFramesEnabled && definitionRefinements == null && gen.Profiler == null && !gen.TraceEnabled &&
                !gen.CompilerOptions.IsEval && !scope.HasClosures && IsLightFrameCandidate()) {
                light = TransformLightFrame(gen, scope, scopeVariable, selfParameter, blockParameter, declaringScope, declaringModule,
                    visiblePrameterCountAndSignatureFlags, ref statements);
            }
            if (LightFramesWhy && light == null) {
                Console.Error.WriteLine("[light] why not: {0} ({1}:{2}): {3}", _name, gen.SourcePath, Location.Start.Line,
                    !(allowLightFrame && LightFramesEnabled) ? "not allowed" :
                    definitionRefinements != null ? "refinements" : gen.Profiler != null ? "profiler" : gen.TraceEnabled ? "trace" :
                    gen.CompilerOptions.IsEval ? "eval" : scope.HasClosures ? "defines a closure (block literal)" :
                    WhyNotLightFrameCandidate() ?? "body needs its scope after the rewrite");
            }

            // The handler only takes the MethodUnwinder of a `return' from a block of this method. It
            // used to take every exception, for IsMethodUnwinderTargetFrame to capture the backtrace in,
            // but a filter here is a catch and a rethrow (TryStatementBuilder: DynamicMethods have no
            // filters), and on .NET 8 each is a full CLR exception dispatch of its own: a raise that left
            // n compiled methods was thrown n + 1 times. Any other exception now goes through with its
            // CLR trace growing as it passes, and the frame that finally handles it - a rescue, a block,
            // an interpreted frame, the top level - captures the same backtrace from there.
            MSA.Expression body = AstUtils.Try(
                countCall,
                recordRefinements,
                profileStart,
                optionalsInitialization,
                statements
            ).Filter(unwinder = Ast.Parameter(typeof(MethodUnwinder), "#u"),
                // a light frame makes a scope for the filter only when a MethodUnwinder passes:
                Methods.IsMethodUnwinderTargetFrame.OpCall((light != null) ? Ast.Coalesce(scopeVariable, light.MakeScope()) : (MSA.Expression)scopeVariable, unwinder),
                Ast.Return(gen.ReturnLabel, Methods.GetMethodUnwinderReturnValue.OpCall(unwinder))
            ).Finally(  
                // leave frame:
                (light != null) ? Methods.LeaveLightMethodFrame.OpCall(light.Rfc, scopeVariable) : Methods.LeaveMethodFrame.OpCall(scopeVariable),
                Ast.Empty(),
                profileEnd
            );

            // TracePoint :return: falling off the end reports the line of `end'; an explicit
            // `return' reports its own line and jumps past this hook (see ReturnStatement).
            if (gen.Traceable) {
                body = new TraceReturnExpression((light != null) ? light.LazyScope : scopeVariable, gen.AddReturnTarget(body), TraceEvents.Return, gen.SourcePath, Location.End.Line);
                if (gen.CurrentMethod.ExplicitReturnLabel != null) {
                    body = Ast.Label(gen.CurrentMethod.ExplicitReturnLabel, body);
                }
            } else {
                body = gen.AddReturnTarget(body);
            }

            // A body with keyword parameters is never a light frame (IsLightFrameCandidate).
            MSA.Expression variableNames = null;
            if (light == null) {
                variableNames = scope.GetVariableNamesExpression();
                if (variableNames is MSA.ConstantExpression names) {
                    SuperCall.RecordKeywordSignature((string[])names.Value, _parameters.Signature);
                }
            }

            // A light frame keeps its locals in CLR variables if it can: `def one(x); yield x; end' would otherwise allocate their
            // tuple on every call.
            MSA.Expression enter = null;
            if (light != null) {
                var registerBody = scope.TryUseRegisterLocals(body);
                if (registerBody != null) {
                    body = registerBody;
                    enter = light.EnterWithRegisterLocals(scope);
                } else {
                    enter = light.Enter(scope);
                }
            }

            body = scope.CreateScope(
                scopeVariable,
                enter ?? Methods.CreateMethodScope.OpCall(new AstExpressions {
                    scope.MakeLocalsStorage(),
                    variableNames,
                    Ast.Constant(visiblePrameterCountAndSignatureFlags),
                    declaringScope,
                    declaringModule,
                    Ast.Constant(_name),
                    selfParameter, blockParameter,
                    EnterInterpretedFrameExpression.Instance
                }),
                body
            );

            gen.LeaveMethodDefinition();

            return CreateLambda(encodedName, parameters, new CompiledFrameExpression(body, encodedName, Location.Start.Line));
        }

        #region Light frames

        /// <summary>
        /// IR_LIGHT_FRAMES=0 turns light frames off, to compare against or to rule them out; IR_LIGHT_FRAMES=verbose
        /// names on stderr each method compiled as one, and each candidate whose transformed body needed its scope;
        /// IR_LIGHT_FRAMES=why also says, for each method that is not one, why not (WhyNotLightFrameCandidate).
        /// </summary>
        internal static readonly bool LightFramesEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_FRAMES") != "0";


        /// <summary>
        /// IR_LIGHT_CALLS=0 keeps every method that makes a call out of light frames (see LightFrameRewriter.LightScopeCall).
        /// </summary>
        internal static readonly bool LightCallsEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_CALLS") != "0";
        /// <summary>
        /// IR_LIGHT_OPTIONALS=0 keeps every method with an optional parameter out of light frames (see AreLightOptionals).
        /// </summary>
        internal static readonly bool LightOptionalsEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_OPTIONALS") != "0";

        /// <summary>
        /// IR_LIGHT_BLOCKS=0 keeps every method with a literal block out of light frames (see TransformLightFrame).
        /// </summary>
        internal static readonly bool LightBlocksEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_BLOCKS") != "0";
        private static readonly bool LightFramesWhy = Environment.GetEnvironmentVariable("IR_LIGHT_FRAMES") == "why";
        private static readonly bool LightFramesVerbose = LightFramesWhy || Environment.GetEnvironmentVariable("IR_LIGHT_FRAMES") == "verbose";

        /// <summary>
        /// The pieces of a method body that runs without a RubyMethodScope (see TransformLightFrame).
        /// </summary>
        private sealed class LightFrame {
            internal MSA.ParameterExpression/*!*/ Rfc;
            internal MSA.ParameterExpression/*!*/ Callee;
            internal MSA.Expression/*!*/ LazyScope;
            internal Func<MSA.Expression>/*!*/ MakeScope;
            internal Func<MSA.Expression, MSA.Expression>/*!*/ MakeScopeIfTraced;
            internal MSA.Expression/*!*/ Name;

            /// <summary>
            /// Replaces CreateMethodScope: the locals, the safe point and the call's flow-control frame, the alias
            /// the method was called by, and a scope only if a TracePoint listens for calls.
            /// </summary>
            internal MSA.Expression/*!*/ Enter(ScopeBuilder/*!*/ scope) {
                return Ast.Block(
                    StackCheck,
                    scope.MakeLocalsStorage(),
                    Ast.Assign(Rfc, EnterMethod),
                    Ast.Assign(Callee, Methods.TakePendingCallee.OpCall(Name)),
                    MakeScopeIfTraced(scope.LocalsTupleVariable)
                );
            }

            /// <summary>
            /// <see cref="Enter"/> of a body that keeps its locals in CLR variables (ScopeBuilder.TryUseRegisterLocals): their
            /// tuple is made only for the scope a TracePoint listening for calls has made at the entry.
            /// </summary>
            internal MSA.Expression/*!*/ EnterWithRegisterLocals(ScopeBuilder/*!*/ scope) {
                return Ast.Block(
                    StackCheck,
                    scope.MakeRegisterLocalsStorage(),
                    Ast.Assign(Rfc, EnterMethod),
                    Ast.Assign(Callee, Methods.TakePendingCallee.OpCall(Name)),
                    Ast.Condition(Methods.IsMethodCallTraced.OpCall(),
                        MakeScopeIfTraced(scope.MaterializeLocals()),
                        AstUtils.Constant(null, typeof(RubyMethodScope))
                    )
                );
            }

            internal MSA.Expression _blockParameter;

            /// <summary>
            /// Set if the body makes calls (LightFrameRewriter.LightScopeCall): it may then recurse, and needs the stack check
            /// CreateMethodScope makes, so that too deep a recursion is a SystemStackError and not the end of the process.
            /// A body that makes no call cannot recurse, and skips it.
            /// </summary>
            internal bool MakesCalls;

            private static readonly MethodInfo/*!*/ _RequireSufficientStack = typeof(RubyOps).GetMethod("RequireSufficientStack");
            private static readonly MethodInfo/*!*/ _EnterLightMethodNamingBlock = typeof(RubyOps).GetMethod("EnterLightMethodNamingBlock");

            /// <summary>
            /// Set if the method has a block parameter: its block may leave the method through it, so it is not one a
            /// light frame "can only yield to" (RubyOps.EnterLightMethodNamingBlock).
            /// </summary>
            internal bool NamesBlock;

            private MSA.Expression/*!*/ EnterMethod {
                get { return NamesBlock ? Ast.Call(_EnterLightMethodNamingBlock, _blockParameter) : Methods.EnterLightMethod.OpCall(_blockParameter); }
            }

            private MSA.Expression/*!*/ StackCheck {
                get { return MakesCalls ? (MSA.Expression)Ast.Call(_RequireSufficientStack) : AstUtils.Empty(); }
            }
        }

        /// <summary>
        /// A method whose body can never need its scope at runtime - yield and return over self, its parameters,
        /// its locals, instance variables, constants and literals: `def tap1; yield self; self; end',
        /// `def initialize(a, b); @a = a; @b = b; end', `def limit = MAX'. Instance variables and constants take a scope,
        /// but only for its runtime and its lexical chain, which the declaring scope has too (LightFrameRewriter.ScopeOperandIndex).
        /// A call without a block takes the scope too, but makes it only when the method it binds to would read it
        /// (LightFrameRewriter.LightScopeCall), so `def getter; a; end', `def cmp(o); @x < o; end' and conditions over
        /// such calls are light as well, and so are the attribute and element assignments, string interpolation, `case/when'
        /// and `while' loops made of them (a loop's OSR site gets the scope made on demand). Global variables need the scope.
        /// The transformed body is still checked (LightFrameRewriter); this is the cheap filter.
        /// </summary>
        private bool IsLightFrameCandidate() {
            if (!AreLightOptionals() || _parameters.Unsplat != null || _parameters.SuperSplat != null ||
                _parameters.LeadingMandatoryCount != _parameters.Mandatory.Length) {
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
            var body = Body;
            if (body == null || body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                return false;
            }
            if (body.Statements != null) {
                bool first = true;
                foreach (var statement in body.Statements) {
                    if (!IsScopeFree(statement, LightBlocksEnabled) && !(first && IsBlockGuard(statement))) {
                        return false;
                    }
                    first = false;
                }
            }
            return true;
        }

        /// <summary>
        /// IR_LIGHT_FRAMES=why: what IsLightFrameCandidate refused - the parameters, a rescue/ensure, or the first statement
        /// that is not scope-free (its node type and line). Null if it is a candidate.
        /// </summary>
        private string WhyNotLightFrameCandidate() {
            if (!AreLightOptionals() || _parameters.Unsplat != null || _parameters.SuperSplat != null ||
                _parameters.LeadingMandatoryCount != _parameters.Mandatory.Length) {
                return "optional, splat or post parameters";
            }
            var signature = _parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords)) {
                return "keyword parameters";
            }
            var body = Body;
            if (body == null || body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                return "rescue, else or ensure";
            }
            if (body.Statements != null) {
                bool first = true;
                foreach (var statement in body.Statements) {
                    if (!IsScopeFree(statement, LightBlocksEnabled) && !(first && IsBlockGuard(statement))) {
                        return String.Format("{0} at line {1}", statement.GetType().Name, statement.Location.Start.Line);
                    }
                    first = false;
                }
            }
            return IsLightFrameCandidate() ? null : "parameters";
        }

        /// <summary>
        /// `return ... unless defined?(yield)' - CRuby's `then' (<internal:kernel>), and IronRuby's in ruby4.rb - as the first
        /// statement of a light frame: only a call without a block runs it, and there it may make the scope on demand (see
        /// LightFrameRewriter), as a TracePoint hook does, for whatever it calls. A call with a block never makes one.
        /// </summary>
        private static bool IsBlockGuard(Expression/*!*/ node) {
            var unless = node as UnlessExpression;
            if (unless == null || unless.ElseClause != null || unless.Statements.Count != 1 || !(unless.Statements.First is ReturnStatement)) {
                return false;
            }
            var defined = unless.Condition as IsDefinedExpression;
            var yieldCall = (defined != null) ? defined.Expression as YieldCall : null;
            return yieldCall != null && yieldCall.Arguments == null && yieldCall.Block == null;
        }

        // Some statement of a light-frame candidate is scope-free only with its literal blocks (see LightFrameRewriter.BlockLiterals).
        private bool HasLightBlockLiteral() {
            var statements = Body.Statements;
            if (statements != null) {
                bool first = true;
                foreach (var statement in statements) {
                    if (!IsScopeFree(statement, false) && !(first && IsBlockGuard(statement))) {
                        return true;
                    }
                    first = false;
                }
            }
            return false;
        }

        /// <summary>
        /// Optional parameters a light frame takes: locals whose defaults are scope-free (`def initialize(size = 0)'), not followed
        /// by keywords. Their initialization is rewritten with the body (see TransformLightFrame), and runs in the same order.
        /// </summary>
        private bool AreLightOptionals() {
            var optional = _parameters.Optional;
            if (optional.Length == 0) {
                return true;
            }
            if (!LightOptionalsEnabled || _parameters.KeywordsAfterOptionals) {
                return false;
            }
            foreach (var parameter in optional) {
                if (!(parameter.Left is LocalVariable) || !IsScopeFree(parameter.Right, false)) {
                    return false;
                }
            }
            return true;
        }

        private bool HasBlockGuard {
            get {
                var statements = Body.Statements;
                return statements != null && statements.Count > 0 && IsBlockGuard(statements.First);
            }
        }

        internal static bool IsScopeFree(Expression/*!*/ node) {
            return IsScopeFree(node, false);
        }

        // blockLiterals: a call with a literal block is scope-free too (see TransformLightFrame); the block's own body is compiled
        // with a scope of its own, made with this frame's scope as its parent, which is then made on demand for it
        private static bool IsScopeFree(Expression/*!*/ node, bool blockLiterals) {
            // `def f = [[[...]]]' a thousand deep: such a body is not worth a light frame, nor a stack overflow to find out
            if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()) {
                return false;
            }
            if (node is LocalVariable || node is Literal || node is SelfReference || node is InstanceVariable) {
                return true;
            }
            var constant = node as ConstantVariable;
            if (constant != null) {
                return IsCachedConstant(constant);
            }
            var assignment = node as SimpleAssignmentExpression;
            if (assignment != null) {
                // `@x += @v' is a call of + between a read and a write
                if (!IsScopeFree(assignment.Right, blockLiterals)) {
                    return false;
                }
                if (assignment.Left is LocalVariable || assignment.Left is InstanceVariable) {
                    return assignment.Operation == null || LightCallsEnabled;
                }
                // `o.x = v', `a[i] = v' (and their op-assigns): calls of x= and []=
                var attribute = assignment.Left as AttributeAccess;
                if (attribute != null) {
                    return LightCallsEnabled && !attribute.IsSafeNavigation && IsScopeFree(attribute.Qualifier, blockLiterals);
                }
                var item = assignment.Left as ArrayItemAccess;
                return item != null && LightCallsEnabled && item.Block == null && IsScopeFree(item.Array, blockLiterals) && AreScopeFree(item.Arguments, blockLiterals);
            }
            // a String literal is a constant, or a copy of one; a Symbol literal is a constant
            if (node is StringLiteral) {
                return true;
            }
            if (LightCallsEnabled) {
                var array = node as ArrayConstructor;
                if (array != null) {
                    return AreScopeFree(array.Arguments, blockLiterals);
                }
                // "#{x}" converts each part through a to_s site
                var interpolation = node as StringConstructor;
                if (interpolation != null && interpolation.Kind == StringKind.Mutable) {
                    foreach (var part in interpolation.Parts) {
                        if (!IsScopeFree(part, blockLiterals)) {
                            return false;
                        }
                    }
                    return true;
                }
                var loop = node as WhileLoopExpression;
                if (loop != null) {
                    return IsScopeFree(loop.Condition, blockLiterals) && AreScopeFree(loop.Statements, blockLiterals);
                }
                var caseExpression = node as CaseExpression;
                if (caseExpression != null) {
                    if (caseExpression.Value != null && !IsScopeFree(caseExpression.Value, blockLiterals) || !AreScopeFree(caseExpression.ElseStatements, blockLiterals)) {
                        return false;
                    }
                    foreach (var clause in caseExpression.WhenClauses) {
                        foreach (var comparison in clause.Comparisons) {
                            if (!IsScopeFree(comparison, blockLiterals)) {
                                return false;
                            }
                        }
                        if (!AreScopeFree(clause.Statements, blockLiterals)) {
                            return false;
                        }
                    }
                    return true;
                }
                var call = node as MethodCall;
                if (call != null) {
                    return (call.Block == null || blockLiterals && call.Block is BlockDefinition) && (call.Target == null || IsScopeFree(call.Target, blockLiterals)) && AreScopeFree(call.Arguments, blockLiterals);
                }
                var conditional = node as ConditionalExpression;
                if (conditional != null) {
                    return IsScopeFree(conditional.Condition, blockLiterals) && IsScopeFree(conditional.TrueExpression, blockLiterals) &&
                        IsScopeFree(conditional.FalseExpression, blockLiterals);
                }
                var ifExpression = node as IfExpression;
                if (ifExpression != null) {
                    if (!IsScopeFree(ifExpression.Condition, blockLiterals) || !AreScopeFree(ifExpression.Body, blockLiterals)) {
                        return false;
                    }
                    if (ifExpression.ElseIfClauses != null) {
                        foreach (var clause in ifExpression.ElseIfClauses) {
                            if (clause.Condition != null && !IsScopeFree(clause.Condition, blockLiterals) || !AreScopeFree(clause.Statements, blockLiterals)) {
                                return false;
                            }
                        }
                    }
                    return true;
                }
                var unlessExpression = node as UnlessExpression;
                if (unlessExpression != null) {
                    return IsScopeFree(unlessExpression.Condition, blockLiterals) && AreScopeFree(unlessExpression.Statements, blockLiterals) &&
                        (unlessExpression.ElseClause == null || AreScopeFree(unlessExpression.ElseClause.Statements, blockLiterals));
                }
                var and = node as AndExpression;
                if (and != null) {
                    return IsScopeFree(and.Left, blockLiterals) && IsScopeFree(and.Right, blockLiterals);
                }
                var or = node as OrExpression;
                if (or != null) {
                    return IsScopeFree(or.Left, blockLiterals) && IsScopeFree(or.Right, blockLiterals);
                }
                var not = node as NotExpression;
                if (not != null) {
                    return IsScopeFree(not.Expression, blockLiterals);
                }
            }
            var yieldCall = node as YieldCall;
            if (yieldCall != null) {
                return yieldCall.Block == null && AreScopeFree(yieldCall.Arguments, blockLiterals);
            }
            // `(yield self; self)', the body of an endless def written with several statements
            var block = node as BlockExpression;
            if (block != null) {
                foreach (var statement in block.Statements) {
                    if (!IsScopeFree(statement, blockLiterals)) {
                        return false;
                    }
                }
                return true;
            }
            var jump = node as ReturnStatement;
            if (jump != null) {
                return AreScopeFree(jump.Arguments, blockLiterals);
            }
            return false;
        }

        // A, ::A, A::B::C - the reads ConstantVariable.MakeCachedRead makes - not {expr}::A
        private static bool IsCachedConstant(ConstantVariable/*!*/ constant) {
            var qualifier = constant.Qualifier;
            return qualifier == null || (qualifier is ConstantVariable && IsCachedConstant((ConstantVariable)qualifier));
        }

        private static bool AreScopeFree(Statements statements, bool blockLiterals) {
            if (statements != null) {
                foreach (var statement in statements) {
                    if (!IsScopeFree(statement, blockLiterals)) {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool AreScopeFree(Arguments arguments, bool blockLiterals) {
            if (arguments != null) {
                foreach (var argument in arguments.Expressions) {
                    if (!IsScopeFree(argument, blockLiterals)) {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Makes the body run without a RubyMethodScope, if the transformed statements use theirs only where a light
        /// frame can stand in for it: the post-yield test (LightMethodYield, against the call's flow-control frame) and
        /// the TracePoint hooks, which only evaluate their scope while a TracePoint listens and get it made on demand.
        /// The call's flow-control frame is what a library method with a block has (RubyOps.CreateRfcForMethod): the
        /// literal block itself, so a break from it still ends this call. Returns null, leaving the statements as they
        /// were, for a body that needs its scope after all.
        /// </summary>
        private LightFrame TransformLightFrame(AstGenerator/*!*/ gen, ScopeBuilder/*!*/ scope, MSA.ParameterExpression/*!*/ scopeVariable,
            MSA.Expression/*!*/ selfParameter, MSA.Expression/*!*/ blockParameter, MSA.Expression/*!*/ declaringScope,
            MSA.Expression/*!*/ declaringModule, int visibleParameterCountAndSignatureFlags, ref MSA.Expression/*!*/ statements) {

            var names = scope.GetVariableNamesExpression() as MSA.ConstantExpression;
            if (names == null) {
                return null;
            }

            var light = new LightFrame();
            light.Rfc = scope.DefineHiddenVariable("#rfc", typeof(RuntimeFlowControl));
            light.Callee = scope.DefineHiddenVariable("#callee", typeof(string));
            light.Name = AstUtils.Constant(_name);
            light._blockParameter = blockParameter;
            light.NamesBlock = _parameters.Block != null;

            var shape = Ast.Constant(new MethodFrameShape((string[])names.Value, visibleParameterCountAndSignatureFlags, _name));
            Func<MethodInfo, MSA.Expression, MSA.Expression> make = (method, locals) => method.OpCall(new AstExpressions {
                shape, declaringScope, declaringModule, locals, selfParameter, blockParameter,
                EnterInterpretedFrameExpression.Instance, light.Rfc, light.Callee
            });
            light.MakeScope = () => make(Methods.CreateMethodScopeLazy, scope.LocalsTupleVariable);
            light.MakeScopeIfTraced = locals => make(Methods.CreateMethodScopeIfTraced, locals);
            var inLoop = scope.DefineHiddenVariable("#in-loop", typeof(bool));
            light.LazyScope = Ast.Coalesce(scopeVariable, Ast.Assign(scopeVariable, Ast.Call(_LightScopeInLoop, light.MakeScope(), inLoop)));

            var rewriter = new LightFrameRewriter(scopeVariable, light.LazyScope, light.Rfc, declaringScope, inLoop, HasBlockGuard) {
                BlockLiterals = LightBlocksEnabled && HasLightBlockLiteral()
            };
            var rewritten = rewriter.Visit(statements);
            if (LightFramesVerbose) {
                Console.Error.WriteLine("[light] {0} {1} ({2}:{3})", rewriter.NeedsScope ? "needs scope:" : "light:", _name, gen.SourcePath, Location.Start.Line);
            }
            if (rewriter.NeedsScope) {
                return null;
            }
            statements = rewritten;
            light.MakesCalls = rewriter.MakesCalls;
            return light;
        }

        /// <summary>
        /// Points a scope-free method body at its light frame: MethodYield to LightMethodYield, the TracePoint hooks
        /// at the scope made on demand. Any other use of the scope, or a node it cannot look into, means the body needs
        /// its scope after all.
        /// </summary>
        private static readonly MethodInfo/*!*/ _LightScopeInLoop = typeof(RubyOps).GetMethod("LightScopeInLoop");
        private static readonly MethodInfo/*!*/ _EnterLightLoop = typeof(RubyOps).GetMethod("EnterLightLoop");
        private static readonly MethodInfo/*!*/ _LeaveLightLoop = typeof(RubyOps).GetMethod("LeaveLightLoop");

        internal sealed class LightFrameRewriter : MSA.ExpressionVisitor {
            private readonly MSA.ParameterExpression/*!*/ _scope;
            private readonly MSA.Expression/*!*/ _inLoop;
            private readonly MSA.Expression/*!*/ _lazyScope;
            private readonly MSA.Expression/*!*/ _rfc;
            private readonly MSA.Expression/*!*/ _declaringScope;
            private int _inTraceHook;

            // The body starts with a block guard (IsBlockGuard), the only statement IsLightFrameCandidate let through that may use
            // the scope: every use of it is a use of the scope made on demand, and the calls it makes are looked into.
            private readonly bool _lazyUses;

            internal bool NeedsScope;

            // The body may make calls with a literal block (IsScopeFree's blockLiterals): the Proc of one needs a scope to be its
            // parent, so the scope is made on demand right there - the call with the block, and the block's instantiation, use
            // the scope made on demand as any other raw use of the scope then does - and the path that never reaches the block
            // makes none. The scope made on demand has the frame's locals (its tuple, made from the register locals if those are
            // used), self, block and flow-control frame, which is all the block and the call read from it.
            internal bool BlockLiterals;

            // see LightFrame.MakesCalls
            internal bool MakesCalls;

            internal LightFrameRewriter(MSA.ParameterExpression/*!*/ scope, MSA.Expression/*!*/ lazyScope, MSA.Expression/*!*/ rfc,
                MSA.Expression/*!*/ declaringScope, MSA.Expression/*!*/ inLoop, bool lazyUses) {
                _inLoop = inLoop;
                _lazyUses = lazyUses;
                _scope = scope;
                _lazyScope = lazyScope;
                _rfc = rfc;
                _declaringScope = declaringScope;
            }

            /// <summary>
            /// The operand of a helper that takes the scope only for what a method scope shares with the scope the method
            /// was defined in, so the declaring scope can be passed instead: the runtime (instance variables), and the
            /// lexical chain a constant is looked up in - a method scope has no module of its own, so the lookup of one
            /// starts at its parent, which is the declaring scope. -1 for any other helper.
            /// </summary>
            private static int ScopeOperandIndex(MethodInfo/*!*/ method) {
                if (method == Methods.GetInstanceVariable || method == Methods.IsDefinedInstanceVariable ||
                    method == Methods.GetUnqualifiedConstant || method == Methods.GetQualifiedConstant ||
                    method == Methods.GetMissingConstant || method == Methods.GetGlobalMissingConstant ||
                    method == Methods.IsDefinedUnqualifiedConstant || method == Methods.IsDefinedGlobalConstant ||
                    method == Methods.IsDefinedQualifiedConstant) {
                    return 0;
                }
                if (method == Methods.SetInstanceVariable) {
                    return 2;
                }
                return -1;
            }

            public override MSA.Expression Visit(MSA.Expression node) {
                // a deeply nested body (see IsScopeFree) keeps its scope rather than overflow the stack here
                if (NeedsScope || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()) {
                    NeedsScope = true;
                    return node;
                }
                return base.Visit(node);
            }

            protected override MSA.Expression/*!*/ VisitParameter(MSA.ParameterExpression/*!*/ node) {
                if (node == _scope) {
                    if (_inTraceHook > 0 || _lazyUses || BlockLiterals) {
                        return _lazyScope;
                    }
                    NeedsScope = true;
                }
                return node;
            }

            protected override MSA.Expression/*!*/ VisitMethodCall(MSA.MethodCallExpression/*!*/ node) {
                // (a block's body, BlockDefinition.TransformLightCalls, has no flow-control frame of its own: _rfc is null)
                if (_rfc != null && node.Method == Methods.MethodYield && node.Arguments.Count == 3 && node.Arguments[0] == _scope) {
                    // the raw scope variable, which is null unless a hook made the scope
                    return Methods.LightMethodYield.OpCall(_rfc, _scope, Visit(node.Arguments[1]), Visit(node.Arguments[2]));
                }
                // the outermost `while' marks the scope as in a loop (EvalBreak), and the frame, for a scope made in it later
                if ((node.Method == Methods.EnterLoop || node.Method == Methods.LeaveLoop) && node.Arguments.Count == 1 && node.Arguments[0] == _scope) {
                    bool enter = node.Method == Methods.EnterLoop;
                    return Ast.Block(
                        Ast.Assign(_inLoop, AstUtils.Constant(enter)),
                        Ast.Call(enter ? _EnterLightLoop : _LeaveLightLoop, _scope)
                    );
                }
                if (node.Method == IronRuby.Runtime.Osr.OsrLoopSite.RunMethod) {
                    // -X:OSR: a loop that has taken enough back edges hands its specialized copy the scope (for its constants
                    // and the sites it binds), made on demand like a TracePoint hook's - once per loop entry that gets that far
                    _inTraceHook++;
                    try {
                        return base.VisitMethodCall(node);
                    } finally {
                        _inTraceHook--;
                    }
                }
                int index = ScopeOperandIndex(node.Method);
                if (index < 0 && _rfc == null && (node.Method == Methods.GetMethodBlockParameter ||
                    node.Method == Methods.GetMethodBlockParameterSelf || node.Method == Methods.BlockYield)) {
                    // A yield in a block: the method's block is found from the scope the block was defined in as it is from
                    // the block's own (the innermost method scope above either), and BlockYield takes the scope for nothing.
                    index = 0;
                }
                if (index >= 0 && index < node.Arguments.Count && node.Arguments[index] == _scope &&
                    node.Method.GetParameters()[index].ParameterType == typeof(RubyScope) && node.Object == null) {
                    var arguments = new MSA.Expression[node.Arguments.Count];
                    for (int i = 0; i < arguments.Length; i++) {
                        arguments[i] = (i == index) ? _declaringScope : Visit(node.Arguments[i]);
                    }
                    return Ast.Call(node.Method, arguments);
                }
                return base.VisitMethodCall(node);
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
                var traceReturn = node as TraceReturnExpression;
                if (traceReturn != null) {
                    // only the scope operand is the hook's; the value is the method's own code
                    _inTraceHook++;
                    MSA.Expression hookScope;
                    try {
                        hookScope = Visit(traceReturn.Scope);
                    } finally {
                        _inTraceHook--;
                    }
                    return traceReturn.Update(hookScope, Visit(traceReturn.Value));
                }
                if (node is EnterInterpretedFrameExpression || node is SourceLineMarker) {
                    return node;
                }
                var dynamic = node as Microsoft.Scripting.Ast.LightDynamicExpression;
                if (dynamic != null) {
                    // Any site may run Ruby code that calls this method again - a call with a literal block, which
                    // LightScopeCall leaves alone, as much as one without (`def f; f { }; end') - so the frame checks the
                    // stack at entry (LightFrame.MakesCalls): too deep a recursion is a SystemStackError, not the end of
                    // the process.
                    MakesCalls = true;
                }
                if (dynamic != null && !_lazyUses && _inTraceHook == 0) {
                    var call = LightScopeCall(dynamic);
                    if (call != null) {
                        return call;
                    }
                }
                if (node is CompiledFrameExpression || ((_lazyUses || BlockLiterals) && node is Microsoft.Scripting.Ast.LightDynamicExpression)) {
                    return base.VisitExtension(node);
                }
                NeedsScope = true;
                return node;
            }

            /// <summary>
            /// A method call made with the scope, as a call that does not need it: the site passes the declaring scope and
            /// is bound with a light scope (RubyCallFlags.HasLightScope). A rule that would read the scope - a library method
            /// that takes it (binding, block_given?, local_variables, eval...), a protected or missing method called on a
            /// receiver, a refined method - instead returns RubyOps.LightScopeRequired, and the call is then made again,
            /// with the same evaluated arguments, through the original site and the scope made on demand. So the scope is
            /// made when, and only when, something the call reaches asks for it, and that sees it as it would have at the
            /// method's entry: the locals are its tuple's (or materialized from their variables), self and the block the
            /// method's. Returns null for any other dynamic expression.
            /// </summary>
            private MSA.Expression LightScopeCall(Microsoft.Scripting.Ast.LightDynamicExpression/*!*/ node) {
                var action = node.Binder as RubyCallAction;
                if (!LightCallsEnabled || action == null || action.Context == null) {
                    return null;
                }
                var signature = action.Signature;
                if (!signature.HasScope || signature.HasBlock || signature.ResolveOnly || signature.IsSuperCall || signature.IsVirtualCall ||
                    signature.IsInteropCall || signature.HasLightScope) {
                    return null;
                }
                var reduced = node.Reduce() as MSA.DynamicExpression;
                if (reduced == null || reduced.Type != typeof(object) || reduced.Arguments.Count < 2 || !IsScope(reduced.Arguments[0])) {
                    return null;
                }

                var lightAction = RubyCallAction.Make(action.Context, action.MethodName,
                    new RubyCallSignature(signature.ArgumentCount, signature.Flags | RubyCallFlags.HasLightScope));

                int count = reduced.Arguments.Count;
                var temps = new MSA.ParameterExpression[count];
                var body = new AstExpressions();
                var lightArgs = new AstExpressions(count);
                var fullArgs = new AstExpressions(count);
                lightArgs.Add(AstUtils.Convert(_declaringScope, typeof(RubyScope)));
                fullArgs.Add(AstUtils.Convert(_lazyScope, typeof(RubyScope)));
                for (int i = 1; i < count; i++) {
                    var argument = Visit(reduced.Arguments[i]);
                    temps[i] = Ast.Variable(argument.Type, "#a" + i);
                    body.Add(Ast.Assign(temps[i], argument));
                    lightArgs.Add(temps[i]);
                    fullArgs.Add(temps[i]);
                }
                var result = Ast.Variable(typeof(object), "#r");
                temps[0] = result;
                MakesCalls = true;
                body.Add(Ast.Assign(result, AstUtils.LightDynamic(lightAction, typeof(object), lightArgs)));
                body.Add(Ast.Condition(
                    Ast.ReferenceEqual(result, AstUtils.Constant(RubyOps.LightScopeRequired)),
                    AstUtils.LightDynamic(action, typeof(object), fullArgs),
                    result
                ));
                return Ast.Block(temps, body);
            }

            // the scope variable, as a call site's first operand converts it
            private bool IsScope(MSA.Expression/*!*/ node) {
                while (node.NodeType == MSA.ExpressionType.Convert) {
                    node = ((MSA.UnaryExpression)node).Operand;
                }
                return node == _scope;
            }
        }

        #endregion

        private static MSA.LambdaExpression/*!*/ CreateLambda(string/*!*/ name, AstParameters/*!*/ parameters, MSA.Expression/*!*/ body) {
            var result = MethodDispatcher.CreateRubyMethodLambda(body, name, parameters);
            if (result != null) {
                return result;
            } 

            // to many parameters for Func<> delegate -> use object[]:
            MSA.ParameterExpression array = Ast.Parameter(typeof(object[]), "#params");
            var actualParameters = new AstParameters() { parameters[0], parameters[1], array };
            // Drop the two implicit parameters - self and the block - so that what is left is
            // exactly the declared parameters, in order, which is how they are read back out of
            // the object[].  Both live at the front, so both removals are at index 0: removing
            // index 1 second would take the *first declared* parameter instead of the block, and
            // leave the block's Proc-typed variable to be assigned an object out of the array.
            parameters.RemoveAt(0);
            parameters.RemoveAt(0);

            // ReadOnlyCollectionBuilder's int constructor takes a capacity, not a length, so the
            // collection starts empty - assigning through the indexer would be an index out of
            // range on a list of zero items.
            var bodyWithParamInit = new AstExpressions(parameters.Count + 1);
            for (int i = 0; i < parameters.Count; i++) {
                bodyWithParamInit.Add(Ast.Assign(parameters[i], Ast.ArrayIndex(array, AstUtils.Constant(i))));
            }
            bodyWithParamInit.Add(body);

            return Ast.Lambda<Func<object, Proc, object[], object>>(
                Ast.Block(
                    parameters, 
                    bodyWithParamInit
                ),
                name,
                actualParameters
            );
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            return Methods.DefineMethod.OpCall(
                (_target != null) ? AstUtils.Box(_target.TransformRead(gen)) : AstUtils.Constant(null),
                gen.CurrentScopeVariable,
                Ast.Constant(new RubyMethodBody(this, gen.Document, gen.Encoding, gen.Coverage))
            );
        }
    }
}
