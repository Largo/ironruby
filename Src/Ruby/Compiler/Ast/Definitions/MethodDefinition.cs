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
            gen.CurrentMethod.ForwardedRestSplat = ForwardedRest.Find(_parameters, Body);

            // The literals of the body take their Procs from this slot if the body turns out to park its scope (below).
            if (ParkedScopesEnabled && allowLightFrame && _parameters.Block == null && definitionRefinements == null && gen.Profiler == null &&
                !gen.TraceEnabled && !gen.CompilerOptions.IsEval) {
                gen.CurrentMethod.ParkSlot = new ParkedScopeSlot(_name);
                gen.CurrentMethod.ParkedVariable = scope.DefineHiddenVariable("#parked", typeof(ParkedProcs));
            }

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
            // A body whose block literals all reuse their Procs parks its scope with them for its next call (RubyOps.CreateMethodScopeParked).
            // Every literal of it is one of the body's own (none nested in a block), and none hands a scope of it on: no super,
            // which passes the method's block - converted by this scope - to a method that may keep it.
            var currentMethod = gen.CurrentMethod;
            ParkedScopeSlot parkSlot = currentMethod.ParkSlot;
            var procVariables = currentMethod.ParkProcVariables;
            if (parkSlot != null) {
                if (procVariables.Count > 0 && procVariables.Count == currentMethod.BlockLiteralCount && !currentMethod.HasUnreusableBlock &&
                    !currentMethod.HasSuperCall) {
                    parkSlot.ProcCount = procVariables.Count;
                } else {
                    parkSlot.Disabled = true;
                    parkSlot = null;
                }
            }

            LightFrame light = null;
            if (_parameters.Optional.Length > 0) {
                statements = Ast.Block(optionalsInitialization, statements);
                optionalsInitialization = AstUtils.Empty();
            }
            if (allowLightFrame && LightFramesEnabled && definitionRefinements == null && gen.Profiler == null && !gen.TraceEnabled &&
                !gen.CompilerOptions.IsEval && !scope.HasClosures && IsLightFrameCandidate()) {
                light = TransformLightFrame(gen, scope, scopeVariable, selfParameter, blockParameter, declaringScope, declaringModule,
                    visiblePrameterCountAndSignatureFlags, parkSlot, ref statements);
            }
            if ((LightFramesWhy || gen.Census != null) && light == null) {
                string why = !(allowLightFrame && LightFramesEnabled) ? "not allowed" :
                    definitionRefinements != null ? "refinements" : gen.Profiler != null ? "profiler" : gen.TraceEnabled ? "trace" :
                    gen.CompilerOptions.IsEval ? "eval" : scope.HasClosures ? "defines a closure (block literal)" :
                    WhyNotLightFrameCandidate() ?? "body needs its scope after the rewrite";
                if (LightFramesWhy) {
                    Console.Error.WriteLine("[light] why not: {0} ({1}:{2}): {3}", _name, gen.SourcePath, Location.Start.Line, why);
                }
                if (gen.Census != null) {
                    gen.Census.NotLightFrame = why;
                }
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
                Methods.IsMethodUnwinderTargetFrame.OpCall((light != null) ? Ast.Coalesce(scopeVariable, light.MakeScope) : (MSA.Expression)scopeVariable, unwinder),
                Ast.Return(gen.ReturnLabel, Methods.GetMethodUnwinderReturnValue.OpCall(unwinder))
            ).Finally(  
                // leave frame:
                (light != null) ? LeaveLightFrame(light, scopeVariable) : Methods.LeaveMethodFrame.OpCall(scopeVariable),
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

            // past the return target: only a call that returns normally parks its scope
            if (parkSlot != null) {
                if (ParkedScopesVerbose) {
                    Console.Error.WriteLine("[park] {0} {1} ({2}:{3})", light != null ? "light:" : "scope:", _name, gen.SourcePath, Location.Start.Line);
                }
                // #p = BeginPark(slot, scope, #parked); if (#p != null) EndPark(slot, #p, scope, StageParkedProc(#p, 0, #proc0) & ...);
                // (block variables, not hidden locals: those would take a light frame's locals out of CLR variables)
                var slotConstant = Ast.Constant(parkSlot);
                var parkResult = Ast.Variable(body.Type, "#park-result");
                var parking = Ast.Variable(typeof(ParkedProcs), "#parking");
                MSA.Expression staged = AstUtils.Constant(true);
                for (int i = 0; i < procVariables.Count; i++) {
                    staged = Ast.And(staged, Methods.StageParkedProc.OpCall(parking, AstUtils.Constant(i), procVariables[i]));
                }
                body = Ast.Block(
                    new[] { parkResult, parking },
                    Ast.Assign(parkResult, body),
                    Ast.Assign(parking, Methods.BeginPark.OpCall(slotConstant, scopeVariable, currentMethod.ParkedVariable)),
                    Ast.IfThen(Ast.NotEqual(parking, AstUtils.Constant(null, typeof(ParkedProcs))),
                        Methods.EndPark.OpCall(slotConstant, parking, scopeVariable, staged)
                    ),
                    parkResult
                );
            }

            // A light frame records its keyword signature with the names of its scope made on demand (TransformLightFrame).
            MSA.Expression variableNames = null;
            if (light == null) {
                variableNames = scope.GetVariableNamesExpression();
                if (variableNames is MSA.ConstantExpression names) {
                    SuperCall.RecordKeywordSignature((string[])names.Value, _parameters.Signature);
                }
            }

            // A light frame keeps its locals in CLR variables if it can and makes few calls (MaxRegisterLocalsCalls):
            // `def one(x); yield x; end' would otherwise allocate their tuple on every call.
            MSA.Expression enter = null;
            if (light != null) {
                var registerBody = (CountCalls() <= MaxRegisterLocalsCalls) ? scope.TryUseRegisterLocals(body) : null;
                if (registerBody != null) {
                    body = registerBody;
                    enter = light.EnterWithRegisterLocals(scope);
                } else {
                    enter = light.Enter(scope);
                }
            }

            if (enter == null && parkSlot != null) {
                // #parked = TakeParkedScope(...); #parked != null ? ReuseParkedScope(#parked, ...) : CreateMethodScope(...)
                var locals = scope.LocalsTupleVariable;
                var parked = currentMethod.ParkedVariable;
                enter = Ast.Block(
                    scope.MakeLocalsStorage(),
                    Ast.Assign(parked, Methods.TakeParkedScope.OpCall(Ast.Constant(parkSlot), declaringScope, declaringModule)),
                    Ast.Condition(Ast.NotEqual(parked, AstUtils.Constant(null, typeof(ParkedProcs))),
                        Methods.ReuseParkedScope.OpCall(parked, locals, selfParameter, blockParameter, EnterInterpretedFrameExpression.Instance),
                        Methods.CreateMethodScope.OpCall(new AstExpressions {
                            locals,
                            variableNames,
                            Ast.Constant(visiblePrameterCountAndSignatureFlags),
                            declaringScope,
                            declaringModule,
                            Ast.Constant(_name),
                            selfParameter, blockParameter,
                            EnterInterpretedFrameExpression.Instance
                        })
                    )
                );
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

        /// <summary>
        /// IR_PARKED_SCOPES=0 makes a new scope on every call of a method whose literal blocks reuse their Procs (see
        /// RubyOps.CreateMethodScopeParked); IR_PARKED_SCOPES=verbose names each method compiled with a parking slot.
        /// </summary>
        internal static readonly bool ParkedScopesEnabled = Environment.GetEnvironmentVariable("IR_PARKED_SCOPES") != "0";
        private static readonly bool ParkedScopesVerbose = Environment.GetEnvironmentVariable("IR_PARKED_SCOPES") == "verbose";

        // Core iterators whose literal block's Proc is never reusable (measured: neither [BlockNotKept] nor a light frame, for
        // any receiver of the core); `each', `map', `select', `times', `each_pair', `sum', `tap', `then', `loop' are. Only a
        // cost: a method that calls one with a literal gets no parking slot, and a method of the same name of a program's own
        // class is merely not parked.
        private static readonly System.Collections.Generic.HashSet<string> NeverParkedIterators = new System.Collections.Generic.HashSet<string> {
            "new", "upto", "downto", "step", "one?", "count", "delete_if", "keep_if", "each_cons", "each_slice",
            "each_with_index", "each_with_object", "fill", "filter_map", "group_by", "inject",
            "reduce", "max_by", "min_by", "partition", "reject", "reverse_each", "sort", "sort_by", "take_while", "drop_while",
            "uniq", "zip", "each_key", "each_value", "each_char", "each_byte", "each_line", "define_method", "lambda", "proc",
            "instance_eval", "instance_exec", "class_eval", "module_eval", "class_exec", "module_exec", "fetch", "tally_by",
        };

        // Array#find, #detect, #find_index, #index, #any?, #all? and #none? only yield ([BlockNotKept]); IR_PARKED_FIND=0 counts
        // them never parked again, as they were while an Array's find and any? were Enumerable's. (Integer#upto only yields too,
        // but parking a method around it cost more instructions than the bytes it saved: Util/perf/LEDGER.md, gap 13,
        // perf/iter2. #downto is parked since perf/permute, whose deeper slots serve a recursion's levels: below.)
        private static readonly bool ParkedFindEnabled = Environment.GetEnvironmentVariable("IR_PARKED_FIND") != "0";

        internal static bool IsNeverParkedIterator(string/*!*/ name) {
            // Integer#downto only yields ([BlockNotKept], as #times): a method that calls it with a literal (Permute's recursive
            // `permute') may park its scope. IR_PARK_DOWNTO=0 counts it among the iterators above, as before.
            if (name == "downto" && ParkDownTo) {
                return false;
            }
            return NeverParkedIterators.Contains(name) ||
                !ParkedFindEnabled && (name == "find" || name == "detect" || name == "find_index" || name == "index" ||
                    name == "any?" || name == "all?" || name == "none?");
        }

        private static readonly bool ParkDownTo = Environment.GetEnvironmentVariable("IR_PARK_DOWNTO") != "0";

        /// <summary>
        /// IR_LIGHT_BLOCK_GIVEN=0 makes `block_given?' in a light-frame method the call it was before: the frame makes its
        /// scope and Kernel#block_given? reads the block from it (see LightFrameRewriter.LightScopeCall).
        /// </summary>
        internal static readonly bool LightBlockGivenEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_BLOCK_GIVEN") != "0";

        /// <summary>
        /// IR_LIGHT_REST=0 keeps every method with a rest parameter (`def f(*args)') out of light frames (see IsLightFrameCandidate).
        /// </summary>
        internal static readonly bool LightRestEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_REST") != "0";

        /// <summary>
        /// IR_LIGHT_BLOCK_PASS=0 keeps every method that passes a block on (`inner(&block)', `map(&:to_s)') out of light frames
        /// (see IsScopeFree and LightFrameRewriter.LightScopeCall).
        /// </summary>
        internal static readonly bool LightBlockPassEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_BLOCK_PASS") != "0";

        /// <summary>
        /// IR_LIGHT_LITERAL_CALLS=0 sends every call with a block in a light frame whose body has literal blocks through the full
        /// site with the scope made on demand, as before round thirty-seven (see NarrowLiterals, LightFrameRewriter.LiteralCalls).
        /// </summary>
        internal static readonly bool LightLiteralCallsEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_LITERAL_CALLS") != "0";

        /// <summary>
        /// IR_LIGHT_SPLAT=0 keeps every method that splats a value (`inner(*args)', `[*a]', `yield(*a)') out of light frames
        /// (see IsScopeFree and LightFrameRewriter.VisitExtension).
        /// </summary>
        internal static readonly bool LightSplatEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_SPLAT") != "0";

        /// <summary>
        /// IR_LIGHT_INTERPOLATION=0 keeps every method that interpolates (`"a#{x}"') out of light frames (see
        /// LightFrameRewriter.VisitMethodCall and VisitExtension).
        /// </summary>
        internal static readonly bool LightInterpolationEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_INTERPOLATION") != "0";

        /// <summary>
        /// IR_LIGHT_HASH=0 keeps every method with a Hash literal (`{a: x}', `{}') out of light frames (see IsScopeFree and
        /// LightFrameRewriter.ScopeOperandIndex).
        /// </summary>
        internal static readonly bool LightHashEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_HASH") != "0";

        /// <summary>
        /// IR_LIGHT_KEYWORD_HASH=0 keeps every method that passes keywords (`f(a: x)') out of light frames (see IsScopeFree and
        /// LightFrameRewriter.ScopeOperandIndex).
        /// </summary>
        internal static readonly bool LightKeywordHashEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_KEYWORD_HASH") != "0";

        /// <summary>
        /// IR_LIGHT_MASGN=0 keeps every method with a multiple assignment (`a, b = b, a', `x, *rest = list') out of light frames
        /// (see IsScopeFree).
        /// </summary>
        internal static readonly bool LightMultipleAssignmentEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_MASGN") != "0";

        /// <summary>
        /// IR_LIGHT_SUPER=0 keeps every method with a `super' (`super', `super(a)', `super(&b)') out of light frames (see
        /// IsScopeFree, SuperScopeMethods and LightFrameRewriter.LightSuperCall).
        /// </summary>
        internal static readonly bool LightSuperEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_SUPER") != "0";

        /// <summary>
        /// IR_LIGHT_REGEX=0 keeps every method with a regex literal (`s.match?(/x/)', `/a#{x}/') out of light frames;
        /// IR_LIGHT_REGEX=all also lets in one whose literal goes to a method that sets $~ (see IsScopeFree).
        /// </summary>
        internal static readonly bool LightRegexEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_REGEX") != "0";
        private static readonly bool LightRegexAll = Environment.GetEnvironmentVariable("IR_LIGHT_REGEX") == "all";

        /// <summary>
        /// IR_LIGHT_GLOBALS=0 keeps every method that reads or writes a global variable out of light frames. Otherwise `$x',
        /// `$x = v' and `defined?($x)' are scope-free for every name but `$~' and `$_', which are the frame's own (see
        /// IsLightGlobal and LightFrameRewriter.VisitGlobalVariableCall).
        /// </summary>
        internal static readonly bool LightGlobalsEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_GLOBALS") != "0";

        /// <summary>
        /// IR_LIGHT_BREAK=0 keeps every method with a `break', `next' or `redo' in a `while'/`until' loop of its body out of
        /// light frames (see IsScopeFree).
        /// </summary>
        internal static readonly bool LightBreakEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_BREAK") != "0";

        /// <summary>
        /// IR_LIGHT_BEGIN=0 keeps every method with a `begin ... end' (or `begin ... end while') out of light frames, also one
        /// without rescue, else or ensure (see IsScopeFree).
        /// </summary>
        internal static readonly bool LightBeginEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_BEGIN") != "0";

        /// <summary>
        /// IR_LIGHT_OSR=0 makes a light frame's scope on demand for its loop's OSR site, as before, instead of handing the site
        /// the declaring scope (see LightFrameRewriter.VisitMethodCall and OsrLoopSite.InLightFrame).
        /// </summary>
        internal static readonly bool LightOsrEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_OSR") != "0";

        /// <summary>
        /// IR_LIGHT_KEYWORDS=0 keeps every method with keyword parameters, a `**rest' or `**nil' out of light frames (see
        /// IsLightFrameCandidate).
        /// </summary>
        internal static readonly bool LightKeywordsEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_KEYWORDS") != "0";

        /// <summary>
        /// IR_LIGHT_RESCUE=0 keeps every method with `rescue', `else' or `ensure' (in its body or in a `begin'), a rescue modifier
        /// or a `retry' out of light frames (see IsScopeFree, AreClausesScopeFree and LightFrameRewriter.VisitHandlingCall).
        /// </summary>
        internal static readonly bool LightRescueEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_RESCUE") != "0";

        /// <summary>
        /// IR_LIGHT_LEXICAL=0 keeps every method with a class variable (`@@x', `@@x = v', `defined?(@@x)'), a `defined?' of a
        /// constant (`defined?(A)', `defined?(A::B)', `defined?(x::A)') or a constant qualified by an expression
        /// (`self.class::A') out of light frames (see IsScopeFree and LightFrameRewriter.ScopeOperandIndex).
        /// </summary>
        internal static readonly bool LightLexicalEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_LEXICAL") != "0";

        /// <summary>
        /// IR_LIGHT_KEYWORD_REST=0 keeps every method with keyword parameters beside a rest or post parameter
        /// (`def warn(*msgs, uplevel: nil)', `def m(a, *r, z, k: 1)') out of light frames (see KeywordsWithSplatLowering).
        /// </summary>
        internal static readonly bool LightKeywordRestEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_KEYWORD_REST") != "0";

        /// <summary>
        /// IR_LIGHT_KEYWORD_SPLAT=0 makes a light-frame candidate that passes a hash on as keywords (`inner(*args, **opts)',
        /// a parameterless `super' with a `**rest') make its scope, as before (see LightFrameRewriter.ScopeOperandIndex).
        /// </summary>
        internal static readonly bool LightKeywordSplatEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_KEYWORD_SPLAT") != "0";

        /// <summary>
        /// IR_LIGHT_MEMBER_OPASSIGN=0 keeps a method with `recv.m op= v' (`o.x += 1', `o.x ||= v', `self.x &&= v'; a
        /// MemberAssignmentExpression) out of light frames, as it was: it is a read of m, the operator and a write of m=, three
        /// calls like those of `o.x = o.x + 1', which a light frame takes, and a hidden CLR variable for the receiver.
        /// </summary>
        internal static readonly bool LightMemberOpAssignEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_MEMBER_OPASSIGN") != "0";

        // The built-in methods that store the caller's $~ when given a Regexp, and so take the caller's scope: a light frame
        // that calls one makes its scope on the call (RubyOps.LightScopeRequired) and calls again, which costs more than the
        // scope made at entry (gap 16 of Util/perf/LEDGER.md). A name only says which method is likely, not which is called,
        // so it decides nothing but which frame is worth making light; the call's rule decides what the call needs.
        private static readonly HashSet<string>/*!*/ RegexScopeMethods = new HashSet<string> {
            "=~", "!~", "===", "match", "gsub", "gsub!", "sub", "sub!", "scan", "split", "index", "rindex", "byteindex",
            "byterindex", "[]", "[]=", "slice", "slice!", "start_with?", "partition", "rpartition", "grep", "grep_v",
        };

        /// <summary>
        /// IR_LIGHT_MATCH_SINK=0 keeps a method whose regex literal goes to a method that stores $~ out of light frames, as before
        /// (see IsMatchSink).
        /// </summary>
        internal static readonly bool LightMatchSinkEnabled = Environment.GetEnvironmentVariable("IR_LIGHT_MATCH_SINK") != "0";

        // The names of RegexScopeMethods that String, Symbol and Regexp define (RubyCallAction.IsMatchSinkMethod): a light site
        // of one in a method that is a match sink stores $~ in the frame's sink (RubyCallFlags.HasMatchSink).
        private static readonly HashSet<string>/*!*/ MatchSinkMethods = new HashSet<string> {
            "=~", "===", "match", "gsub", "gsub!", "sub", "sub!", "scan", "split", "index", "rindex", "byteindex",
            "byterindex", "[]", "[]=", "slice", "slice!", "start_with?", "partition", "rpartition",
        };

        // Set while the body of a method that is a match sink is checked (IsBodyScopeFree): a regex literal given to one of
        // MatchSinkMethods, or in a when clause, does not then keep it out of light frames.
        [ThreadStatic]
        private static bool t_matchSink;

        // The names of the methods through which a frame's $~ is read other than by `$~', `$1'... and Regexp.last_match: a
        // binding or a string eval in the frame, a call made by name (send, method), and a TracePoint the frame enables itself.
        private static readonly HashSet<string>/*!*/ MatchObserverMethods = new HashSet<string> {
            "binding", "eval", "instance_eval", "class_eval", "module_eval", "send", "__send__", "public_send", "method",
            "public_method", "singleton_method", "last_match", "enable", "set_trace_func", "trace",
        };

        // The globals a match sink may read or write: none of them is the frame's $~ or an alias of it.
        private static readonly HashSet<string>/*!*/ MatchSinkGlobals = new HashSet<string> {
            "stdout", "stderr", "stdin", "VERBOSE", "DEBUG", "0", "PROGRAM_NAME", "LOAD_PATH", "LOADED_FEATURES", ":", "\"",
            ";", ",", "/", "\\", "!", "@", "$", "*", "FILENAME", "-w", "-v", "-d", "-0", "-F", "-i", "-l", "-p", "-a", "-I",
        };

        /// <summary>
        /// Whether nothing reads the $~ of a frame of the method (CRuby: $~ is frame-local, and a block's is its method's): no
        /// `$~', `$&', `$1', Regexp.last_match or other reference to it, no block or lambda (whose binding a callee may be given),
        /// no binding, string eval or call made by name (MatchObserverMethods), no global but the runtime's own
        /// (MatchSinkGlobals: `alias $m $~' makes `$m' the frame's $~). Such a method's light frame lets the String, Symbol and
        /// Regexp methods it calls store $~ in a sink of the frame's own, a scope that holds nothing else, taken from the thread's
        /// free ones at the first such call and given back as the frame returns (RubyCallFlags.HasMatchSink, RubyOps.TakeMatchSink),
        /// where the frame would otherwise make its scope for them on every call and call them again (gap 16 of
        /// Util/perf/LEDGER.md). A scope the frame makes later gets that $~, for a TracePoint's binding; $~ after the method
        /// returns is its caller's, as in CRuby.
        /// </summary>
        private bool IsMatchSink() {
            if (!LightMatchSinkEnabled || !LightRegexEnabled) {
                return false;
            }
            var finder = new MatchObserverFinder();
            foreach (var optional in _parameters.Optional) {
                optional.Walk(finder);
            }
            Body.Walk(finder);
            return !finder.Found;
        }

        private sealed class MatchObserverFinder : Walker {
            internal bool Found;

            public override bool Enter(RegexMatchReference/*!*/ node) { Found = true; return false; }
            public override bool Enter(GlobalVariable/*!*/ node) { Found |= !MatchSinkGlobals.Contains(node.Name); return !Found; }
            public override bool Enter(BlockDefinition/*!*/ node) { Found = true; return false; }
            public override bool Enter(LambdaDefinition/*!*/ node) { Found = true; return false; }
            public override bool Enter(MethodDefinition/*!*/ node) { return false; }
            public override bool Enter(MethodCall/*!*/ node) { Found |= MatchObserverMethods.Contains(node.MethodName); return !Found; }
        }

        private static bool HasRegexLiteral(Expression target, Arguments arguments) {
            if (target is RegularExpression) {
                return true;
            }
            if (arguments != null) {
                foreach (var argument in arguments.Expressions) {
                    if (argument is RegularExpression) {
                        return true;
                    }
                }
            }
            return false;
        }

        private static readonly bool LightFramesWhy = Environment.GetEnvironmentVariable("IR_LIGHT_FRAMES") == "why";
        private static readonly bool LightFramesVerbose = LightFramesWhy || Environment.GetEnvironmentVariable("IR_LIGHT_FRAMES") == "verbose";

        /// <summary>
        /// The pieces of a method body that runs without a RubyMethodScope (see TransformLightFrame).
        /// </summary>
        private sealed class LightFrame {
            internal MSA.ParameterExpression/*!*/ Rfc;
            internal MSA.ParameterExpression/*!*/ Callee;
            internal MSA.Expression/*!*/ LazyScope;
            // The making of the scope on demand: one node, which LazyScope and the frame's filter share, so that a body that keeps
            // its locals in CLR variables rewrites it once (ScopeBuilder.TryUseRegisterLocals).
            internal MSA.Expression/*!*/ MakeScope;
            internal Func<MSA.Expression, MSA.Expression>/*!*/ MakeScopeIfTraced;
            internal MSA.Expression/*!*/ Name;
            // The frame's sink (RubyOps.TakeMatchSink) if it is a match sink, given back as it returns.
            internal MSA.ParameterExpression MatchSink;

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
                    // the test inline, as in EnterWithRegisterLocals: a call of CreateMethodScopeIfTraced, with its nine
                    // arguments, on every entry to find out that nothing listens (IR_LIGHT_ENTRY=0 makes the call)
                    RubyOps.PendingCalleesCounted
                        ? (MSA.Expression)Ast.Condition(Methods.IsMethodCallTraced.OpCall(),
                            MakeScopeIfTraced(scope.LocalsTupleVariable),
                            AstUtils.Constant(null, typeof(RubyMethodScope))
                        )
                        : MakeScopeIfTraced(scope.LocalsTupleVariable)
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
            /// A body that makes no call cannot recurse, and skips it. A Hash literal whose keys' #hash and #eql? may be Ruby methods
            /// counts as a call (AstGenerator.MethodScope.HasHashCallingKeys): its helper is no call site, but it calls them.
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
        /// and `while' loops made of them, with their `break', `next' and `redo' (a loop's OSR site gets the declaring scope).
        /// Global variables need the scope.
        /// A rest parameter (`def log(*messages)') is a local like the others: the caller's rule makes its Array, and a scope
        /// made on demand is told of it as a scope made at entry is (RubyMethodScope.HasUnsplatFlag). Parameters after it
        /// are not taken. A splatted argument (`inner(*args)') is converted by a site that takes no scope.
        /// Keyword parameters (`def f(a, k: 1, j:, **o)', `**nil') are lowered onto a hidden optional parameter that holds the
        /// call's keyword hash, and a prologue at the head of the body that checks it and binds each keyword to its local, the
        /// defaults evaluated there in order (PrismAstBridge.LowerKeywords): locals, calls and runtime helpers that take no
        /// scope, so the body is scope-free if its statements are. The hash a call that passes no keywords gets is the
        /// runtime's (LightFrameRewriter.ScopeOperandIndex). Keywords beside a rest or post parameter too (IR_LIGHT_KEYWORD_REST):
        /// the general lowering binds them out of one splat by hand (KeywordsWithSplatLowering), with helpers that take no
        /// scope but PopKeywordArguments, which reads the runtime's (ScopeOperandIndex). Not taken: such a signature whose
        /// positionals a parameterless `super' cannot read back by name, which keeps a copy of the splat (SuperSplat).
        /// A body with rescue, else or ensure clauses, in the method's body or in a `begin', or with a rescue modifier or a
        /// `retry', is one if its statements and clauses are (AreClausesScopeFree): $! is the runtime's, so the path that raises
        /// nothing saves and restores it without a scope, and the path that catches an exception makes the scope on demand where
        /// the exception is matched (LightFrameRewriter.VisitHandlingCall).
        /// The transformed body is still checked (LightFrameRewriter); this is the cheap filter.
        /// </summary>
        private bool IsLightFrameCandidate() {
            if (!AreLightOptionals() || _parameters.Unsplat != null && !LightRestEnabled || _parameters.SuperSplat != null ||
                _parameters.LeadingMandatoryCount != _parameters.Mandatory.Length) {
                return false;
            }
            if (HasKeywordParameters && (!LightKeywordsEnabled || KeywordsWithSplatLowering && !LightKeywordRestEnabled)) {
                return false;
            }
            foreach (var parameter in _parameters.Mandatory) {
                if (!(parameter is LocalVariable)) {
                    return false;
                }
            }
            var body = Body;
            if (body == null || HasHandling(body) && !LightRescueEnabled) {
                return false;
            }
            if (IsLargeWithMultipleAssignment() || IsLargeWithGlobal() || HasScopeReadingSuper()) {
                return false;
            }
            return IsBodyScopeFree(false) || LightBlocksEnabled && IsBodyScopeFree(true);
        }

        // A body with exception handling: rescue clauses, an else or an ensure clause. Wider than Body.HasExceptionHandling, which
        // leaves out the rescue clauses of a body without statements (they have nothing to rescue and are not compiled).
        private static bool HasHandling(Body/*!*/ body) {
            return body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null;
        }

        // The body's statements and its rescue, else and ensure clauses are scope-free (blockLiterals: with literal blocks).
        private bool IsBodyScopeFree(bool blockLiterals) {
            bool saved = t_matchSink;
            t_matchSink = !blockLiterals && IsMatchSink();
            try {
                return FirstNotScopeFree(blockLiterals) == null && AreClausesScopeFree(Body, blockLiterals, null);
            } finally {
                t_matchSink = saved;
            }
        }

        // The most calls a body with a multiple assignment may make and still be a light frame. A light frame's lambda grows
        // with each call (the light site, the test of its answer and the call again with the scope made on demand, which
        // copies every register local into the new tuple: ScopeBuilder.MaterializeLocals) and with each access of a register
        // local (a test of the tuple), so a large body compiles to two or three times the IL of the same body with its scope.
        // Straight-line bodies with a multiple assignment and ten locals measured, in instructions per call against
        // IR_LIGHT_MASGN=0: 8 calls 0.93, 16 calls 0.95, 24 and 32 calls 0.97, 48 calls 1.05. Past the CLR's limits
        // (String#bytesplice's body as a light frame: 49 KB of IL) the method is compiled without optimization
        // ("Tier-0 switched MinOpts"), 1.26. Bodies without a multiple assignment are not held to it (see the ledger, gap 16).
        private const int MaxMultipleAssignmentCalls = 24;

        // A body with a multiple assignment and more than MaxMultipleAssignmentCalls calls keeps its scope.
        private bool IsLargeWithMultipleAssignment() {
            if (!LightMultipleAssignmentEnabled) {
                return false;
            }
            var counter = WalkCalls();
            return counter.HasMultipleAssignment && counter.Calls > MaxMultipleAssignmentCalls;
        }

        // A body that reads or writes a global and makes more than MaxMultipleAssignmentCalls calls keeps its scope, for the
        // reason above: such bodies were all kept out of light frames until globals were let in, and the one measured lost as
        // one - the prelude's String#__ir_split_lines__ (`$/' and some forty calls), whose light lambda was 17,890 bytes of IL
        // against 7,652 with its scope, made `"a\nb\n".lines' 1.10 times slower (six alternating rounds, spread 4-6%).
        private bool IsLargeWithGlobal() {
            if (!LightGlobalsEnabled) {
                return false;
            }
            var counter = WalkCalls();
            return counter.HasGlobal && counter.Calls > MaxMultipleAssignmentCalls;
        }

        // The names of the library methods of Kernel, BasicObject, Module, Enumerable, String, Symbol and Regexp that read their
        // caller's scope (binding, block_given?, respond_to?'s visibility, $~...). A `super' in a method of one of these names
        // most likely reaches one, and a light frame would then make its scope on every call, after the light site has
        // answered RubyOps.LightScopeRequired: `def respond_to?(m, p = false) = m == :x || super' took 1,489 instructions a
        // call as one, 1,360 with its scope made at entry. Not method_missing: BasicObject's raises, and a Ruby one gains.
        private static readonly HashSet<string>/*!*/ SuperScopeMethods = new HashSet<string> {
            "respond_to?", "binding", "block_given?", "iterator?", "local_variables", "eval", "instance_eval", "class_eval",
            "module_eval", "send", "__send__", "public_send", "method", "public_method", "singleton_class",
            "define_singleton_method", "__method__", "__callee__", "require", "load", "autoload", "autoload?", "print", "gets",
            "trace_var", "include", "private", "public", "protected", "module_function", "define_method", "attr", "attr_accessor",
            "attr_reader", "attr_writer", "const_get", "const_set", "const_source_location", "instance_method",
            "public_instance_method", "nesting", "using", "used_modules", "used_refinements", "map", "collect", "inject",
            "reduce", "grep", "grep_v", "=~", "!~", "===", "~", "match", "last_match", "gsub", "gsub!", "sub", "sub!", "scan",
            "split", "index", "rindex", "byteindex", "byterindex", "slice", "slice!", "start_with?", "end_with?", "partition",
            "rpartition",
        };

        // A method named like one of SuperScopeMethods whose body calls `super' keeps its scope.
        private bool HasScopeReadingSuper() {
            return LightSuperEnabled && SuperScopeMethods.Contains(_name) && WalkCalls().HasSuperCall;
        }

        // The most calls a light frame may make and still keep its locals in CLR variables (ScopeBuilder.TryUseRegisterLocals).
        // Each call's scope-making arm copies every register local into the tuple (ScopeBuilder.MaterializeLocals) and each
        // access of one is a test of the tuple, so a large body's lambda doubles: a copy of String#bytesplice's body written with single assignments (81-100
        // calls) is 52 KB of IL and compiled MinOpts with them, 30 KB and FullOpts without, 13,290 instructions a call against 11,140 (11,440
        // with its scope). One copy of the arm per frame cannot be had: each call site is inside an expression, which control
        // cannot enter, and a nested lambda would hoist the locals into a closure. Straight-line bodies with ten locals gain
        // from them up to 32 calls (2-3%) and not at 48.
        private const int MaxRegisterLocalsCalls = 40;

        private int CountCalls() {
            return WalkCalls().Calls;
        }

        private CallCounter/*!*/ WalkCalls() {
            var counter = new CallCounter();
            // the defaults of optional parameters are part of the frame's lambda (TransformBody)
            foreach (var optional in _parameters.Optional) {
                optional.Walk(counter);
            }
            // the statements and the rescue, else and ensure clauses
            Body.Walk(counter);
            return counter;
        }

        private sealed class CallCounter : Walker {
            internal int Calls;
            internal bool HasMultipleAssignment;
            internal bool HasSuperCall;
            internal bool HasGlobal;

            public override bool Enter(GlobalVariable/*!*/ node) { HasGlobal = true; return true; }

            // the walker visits only the left side of a multiple assignment: the calls of the right side count too
            public override bool Enter(ParallelAssignmentExpression/*!*/ node) {
                HasMultipleAssignment = true;
                foreach (var value in node.Right) {
                    value.Walk(this);
                }
                return true;
            }
            // the walker visits a call's target and arguments, not its block: the calls of `&expr' count too
            public override bool Enter(MethodCall/*!*/ node) {
                Calls++;
                var blockPass = node.Block as BlockReference;
                if (blockPass != null) {
                    blockPass.Expression.Walk(this);
                }
                return true;
            }
            public override bool Enter(ArrayItemAccess/*!*/ node) { Calls++; return true; }
            public override bool Enter(AttributeAccess/*!*/ node) { Calls++; return true; }
            // `x += v' calls the operator, and `a[i] += v' and `o.x += v' read and write through two calls, of which the
            // walk of the left side counts one; `&&=' and `||=' call no operator
            public override bool Enter(SimpleAssignmentExpression/*!*/ node) {
                if (node.Operation != null) {
                    if (node.Operation != Symbols.And && node.Operation != Symbols.Or) {
                        Calls++;
                    }
                    if (node.Left is ArrayItemAccess || node.Left is AttributeAccess) {
                        Calls++;
                    }
                }
                return true;
            }
            public override bool Enter(SuperCall/*!*/ node) { Calls++; HasSuperCall = true; return true; }
            public override bool Enter(YieldCall/*!*/ node) { Calls++; return true; }
            public override bool Enter(BlockDefinition/*!*/ node) { return false; }
            public override bool Enter(MethodDefinition/*!*/ node) { return false; }
        }

        // The first statement of the body that is not scope-free (blockLiterals: with literal blocks), or null.
        private Expression FirstNotScopeFree(bool blockLiterals) {
            var statements = Body.Statements;
            if (statements != null) {
                bool first = true;
                foreach (var statement in statements) {
                    if (!IsScopeFree(statement, blockLiterals, null) && !(first && IsBlockGuard(statement))) {
                        return statement;
                    }
                    first = false;
                }
            }
            return null;
        }

        /// <summary>
        /// IR_LIGHT_FRAMES=why: what IsLightFrameCandidate refused - the parameters, a rescue/ensure, or the first statement
        /// that is not scope-free: its node type and line, then the construct inside it that IsScopeFree refused
        /// (`IfExpression at line 12: GlobalVariable'), since the statement alone does not say what to teach the frames next.
        /// Null if it is a candidate.
        /// </summary>
        private string WhyNotLightFrameCandidate() {
            if (!AreLightOptionals() || _parameters.Unsplat != null && !LightRestEnabled || _parameters.SuperSplat != null ||
                _parameters.LeadingMandatoryCount != _parameters.Mandatory.Length) {
                return "optional, splat or post parameters";
            }
            if (HasKeywordParameters && !LightKeywordsEnabled) {
                return "keyword parameters (IR_LIGHT_KEYWORDS=0)";
            }
            if (HasKeywordParameters && KeywordsWithSplatLowering && !LightKeywordRestEnabled) {
                return "keyword parameters with a rest or post parameter (IR_LIGHT_KEYWORD_REST=0)";
            }
            var body = Body;
            if (body == null || HasHandling(body) && !LightRescueEnabled) {
                return "rescue, else or ensure (IR_LIGHT_RESCUE=0)";
            }
            if (!IsBodyScopeFree(false)) {
                var statement = FirstNotScopeFree(LightBlocksEnabled);
                if (statement != null) {
                    return String.Format("{0} at line {1}: {2}", statement.GetType().Name, statement.Location.Start.Line,
                        WhyNotScopeFree(statement, LightBlocksEnabled));
                }
                var why = new StrongBox<string>();
                if (!AreClausesScopeFree(body, LightBlocksEnabled, why)) {
                    return "rescue, else or ensure clause: " + why.Value;
                }
            }
            if (IsLargeWithMultipleAssignment()) {
                return "multiple assignment in a body of more than " + MaxMultipleAssignmentCalls + " calls";
            }
            if (IsLargeWithGlobal()) {
                return "global variable in a body of more than " + MaxMultipleAssignmentCalls + " calls";
            }
            if (HasScopeReadingSuper()) {
                return "super in " + _name + " (the library method reads its caller's scope)";
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
            return !IsBodyScopeFree(false);
        }

        /// <summary>
        /// Every literal block of the body is of the narrow shape (Docs/light-frame-literal-blocks.md): it reads and writes no
        /// local of an enclosing scope, has no return, yield, super, nested literal or lambda, and calls none of the Kernel
        /// methods that reach for the frame (binding, eval, local_variables, __method__, __callee__, block_given?, iterator?).
        /// Its Proc is still made over the frame's scope, made on demand, so a callee that keeps it, a binding taken through
        /// send and a TracePoint find the method's scope as before; what the shape keeps out is a literal whose own code would
        /// reach the frame through the scope its call no longer makes.
        /// </summary>
        private bool HasOnlyNarrowLiterals() {
            var checker = new NarrowLiteralChecker();
            checker.Walk(Body);
            return checker.Literals > 0 && !checker.Wide;
        }

        private sealed class NarrowLiteralChecker : Walker {
            internal bool Wide;
            internal int Literals;
            private int _inBlock;
            private int _blockDepth;

            private static readonly System.Collections.Generic.HashSet<string> FrameMethods = new System.Collections.Generic.HashSet<string> {
                "binding", "eval", "local_variables", "__method__", "__callee__", "block_given?", "iterator?",
            };

            public override bool Enter(BlockDefinition/*!*/ node) {
                Literals++;
                if (_inBlock > 0) {
                    Wide = true;
                    return false;
                }
                _inBlock++;
                _blockDepth = node.DefinedScope.Depth;
                return true;
            }

            public override void Exit(BlockDefinition/*!*/ node) {
                _inBlock--;
            }

            public override bool Enter(LambdaDefinition/*!*/ node) { Wide = true; return false; }
            public override bool Enter(MethodDefinition/*!*/ node) { Wide = true; return false; }
            public override bool Enter(ClassDefinition/*!*/ node) { Wide = true; return false; }
            public override bool Enter(ModuleDefinition/*!*/ node) { Wide = true; return false; }
            public override bool Enter(SingletonDefinition/*!*/ node) { Wide = true; return false; }

            public override bool Enter(LocalVariable/*!*/ node) {
                if (_inBlock > 0 && node.DefinitionLexicalDepth < _blockDepth) { Wide = true; }
                return true;
            }

            public override bool Enter(ReturnStatement/*!*/ node) { if (_inBlock > 0) { Wide = true; } return true; }
            public override bool Enter(YieldCall/*!*/ node) { if (_inBlock > 0) { Wide = true; } return true; }
            // (the walker does not visit a call's block: Walker.Walk(MethodCall) leaves it to whoever asks)
            public override bool Enter(SuperCall/*!*/ node) {
                if (_inBlock > 0) { Wide = true; }
                if (node.Block != null) { node.Block.Walk(this); }
                return true;
            }

            public override bool Enter(MethodCall/*!*/ node) {
                if (_inBlock > 0 && node.Target == null && FrameMethods.Contains(node.MethodName)) { Wide = true; }
                if (node.Block != null) { node.Block.Walk(this); }
                return true;
            }
        }

        // Declares keyword parameters, a `**rest' or `**nil'. Not `...', whose signature says `**' but whose lowering is a
        // rest and a block parameter and no keyword prologue (Parameters.ForwardsArguments): what a light frame takes as
        // `def m(*a, &b)'. The keyword hash a call passes is bound into the rest by the method's RubyMethodInfo, whatever
        // frame the body is compiled for.
        private bool HasKeywordParameters {
            get {
                var signature = _parameters.Signature;
                return signature != null && !_parameters.ForwardsArguments && (signature.TakesKeywords || signature.RefusesKeywords);
            }
        }

        // Keywords beside a rest or post parameter: the general lowering takes every argument in one splat and binds the
        // positionals out of it by hand (PrismAstBridge.LowerGeneralParameters), with SplatParameterRead (see IsScopeFree).
        private bool KeywordsWithSplatLowering {
            get { return _parameters.Unsplat != null || _parameters.LeadingMandatoryCount != _parameters.Mandatory.Length; }
        }

        /// <summary>
        /// Optional parameters a light frame takes: locals whose defaults are scope-free (`def initialize(size = 0)'), and the
        /// hidden one keyword parameters are lowered onto. With keywords after optional positionals, the keyword hash of a
        /// call that left an optional out is moved into place before the defaults run (Parameters.KeywordsAfterOptionals),
        /// between locals. Their initialization is rewritten with the body (see TransformLightFrame), and runs in the same order.
        /// </summary>
        private bool AreLightOptionals() {
            var optional = _parameters.Optional;
            if (optional.Length == 0) {
                return true;
            }
            if (!LightOptionalsEnabled || _parameters.KeywordsAfterOptionals && !LightKeywordsEnabled) {
                return false;
            }
            foreach (var parameter in optional) {
                if (!(parameter.Left is LocalVariable) || !IsScopeFree(parameter.Right, false, null)) {
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

        // A statement of a block's body (BlockDefinition.IsLightCallBody): not one with exception handling, which the rewriter
        // takes only in a method's frame (LightFrameRewriter.Handling).
        internal static bool IsScopeFree(Expression/*!*/ node) {
            bool saved = t_matchSink;
            t_matchSink = false;
            try {
                return IsScopeFree(node, false, null) && !HandlingFinder.Has(node);
            } finally {
                t_matchSink = saved;
            }
        }

        // Finds a `begin' with rescue, else or ensure, a rescue modifier or a `retry' in a node, outside its blocks and definitions.
        private sealed class HandlingFinder : Walker {
            private bool _found;

            internal static bool Has(Expression/*!*/ node) {
                var finder = new HandlingFinder();
                node.Walk(finder);
                return finder._found;
            }

            public override bool Enter(Body/*!*/ node) { _found |= HasHandling(node); return !_found; }
            public override bool Enter(RescueExpression/*!*/ node) { _found = true; return false; }
            public override bool Enter(RetryStatement/*!*/ node) { _found = true; return false; }
            public override bool Enter(BlockDefinition/*!*/ node) { return false; }
            public override bool Enter(MethodDefinition/*!*/ node) { return false; }
        }

        /// <summary>
        /// The rescue, else and ensure clauses of a body, whose statements are the caller's to check: the classes of each rescue
        /// clause, read and handed to RubyOps.CompareException; its target, a local or an instance variable, written from $!; and
        /// the statements of each clause. What the exception handling itself does with the scope - $! saved and restored, the
        /// exception matched and its backtrace captured - is the rewriter's (LightFrameRewriter.VisitHandlingCall): only the path
        /// that raises makes the scope.
        /// </summary>
        private static bool AreClausesScopeFree(Body/*!*/ body, bool blockLiterals, StrongBox<string> why) {
            if (body.RescueClauses != null) {
                foreach (var clause in body.RescueClauses) {
                    foreach (var type in clause.Types) {
                        if (!IsScopeFree(type, blockLiterals, why)) {
                            return false;
                        }
                    }
                    if (clause.Target != null && !(clause.Target is LocalVariable || clause.Target is InstanceVariable)) {
                        return Refuse(why, "rescue => " + clause.Target.GetType().Name);
                    }
                    if (!AreScopeFree(clause.Statements, blockLiterals, why)) {
                        return false;
                    }
                }
            }
            return AreScopeFree(body.ElseStatements, blockLiterals, why) && AreScopeFree(body.EnsureStatements, blockLiterals, why);
        }

        // IR_LIGHT_FRAMES=why: names what IsScopeFree refused in the node. Every refusal of a sub-node is its parent's too and
        // ends the walk, so the reason it records is the innermost one: the decision IsScopeFree took, not a guess from the node.
        private static string WhyNotScopeFree(Expression/*!*/ node, bool blockLiterals) {
            var why = new StrongBox<string>();
            return IsScopeFree(node, blockLiterals, why) ? null : why.Value;
        }

        // Records the reason of a refusal for WhyNotScopeFree, if it asked (why is null otherwise), and refuses.
        private static bool Refuse(StrongBox<string> why, string/*!*/ reason) {
            if (why != null) {
                why.Value = reason;
            }
            return false;
        }

        // blockLiterals: a call with a literal block is scope-free too (see TransformLightFrame); the block's own body is compiled
        // with a scope of its own, made with this frame's scope as its parent, which is then made on demand for it
        private static bool IsScopeFree(Expression/*!*/ node, bool blockLiterals, StrongBox<string> why) {
            // `def f = [[[...]]]' a thousand deep: such a body is not worth a light frame, nor a stack overflow to find out
            if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()) {
                return Refuse(why, "nested too deep");
            }
            if (node is LocalVariable || node is Literal || node is SelfReference || node is InstanceVariable) {
                return true;
            }
            // `defined?(@x)' (SimpleDelegator#__getobj__): RubyOps.IsDefinedInstanceVariable reads only the runtime from the
            // scope, which the rewriter hands the declaring scope for (LightFrameRewriter.ScopeOperandIndex)
            var isDefined = node as IsDefinedExpression;
            if (isDefined != null && isDefined.Expression is InstanceVariable) {
                return true;
            }
            // `$stdout', `$VERBOSE', `defined?($x)': the runtime's, read through the declaring scope (IsLightGlobal);
            // RubyOps.IsDefinedGlobalVariable reads only the runtime from its scope, for any name
            var global = node as GlobalVariable;
            if (global != null) {
                return IsLightGlobal(global) || why != null && Refuse(why, LightGlobalsEnabled ?
                    "GlobalVariable " + global.FullName + " (the frame's own)" : "GlobalVariable (IR_LIGHT_GLOBALS=0)");
            }
            if (isDefined != null && isDefined.Expression is GlobalVariable && LightGlobalsEnabled) {
                return true;
            }
            // `@@x', `@@x = v', `defined?(@@x)', `defined?(A)', `defined?(A::B)', `defined?(x::A)', `x::A' (IR_LIGHT_LEXICAL):
            // their helpers (RubyOps.GetClassVariable, IsDefinedClassVariable, IsDefinedUnqualifiedConstant,
            // GetExpressionQualifiedConstant...) read the runtime, the global scope and the lexical chain from the scope. A method
            // scope has no module (Module is null) and is no instance_eval scope, so the walks of RubyScope.GetInnerMostModule
            // and TryResolveConstantNoLock pass over it to its parent, the declaring scope, which the rewriter hands them
            // (LightFrameRewriter.ScopeOperandIndex). `defined?(x::A)' evaluates x in a try that answers nil for any exception.
            if (LightLexicalEnabled) {
                if (node is ClassVariable || isDefined != null && isDefined.Expression is ClassVariable) {
                    return true;
                }
                var lexicalConstant = (isDefined != null) ? isDefined.Expression as ConstantVariable : node as ConstantVariable;
                if (lexicalConstant != null) {
                    return IsScopeFreeQualifier(lexicalConstant, blockLiterals, why);
                }
                var lexicalAssignment = node as SimpleAssignmentExpression;
                if (lexicalAssignment != null && lexicalAssignment.Left is ClassVariable) {
                    // `@@x += 1' is a call of + between a read and a write
                    return IsScopeFree(lexicalAssignment.Right, blockLiterals, why) &&
                        (lexicalAssignment.Operation == null || LightCallsEnabled || Refuse(why, "operator assignment (IR_LIGHT_CALLS=0)"));
                }
            } else if (why != null) {
                var lexicalAssignment = node as SimpleAssignmentExpression;
                var lexical = (isDefined != null) ? isDefined.Expression :
                    (lexicalAssignment != null && lexicalAssignment.Left is ClassVariable) ? lexicalAssignment.Left : node;
                if (lexical is ClassVariable || lexical is ConstantVariable && (isDefined != null || !IsCachedConstant((ConstantVariable)lexical))) {
                    return Refuse(why, (isDefined != null ? "IsDefinedExpression of " : lexical != node ? "assignment to " : "") +
                        lexical.GetType().Name + (isDefined == null && lexical is ConstantVariable ? " with an expression qualifier" : "") +
                        " (IR_LIGHT_LEXICAL=0)");
                }
            }
            var constant = node as ConstantVariable;
            if (constant != null) {
                return IsCachedConstant(constant) || Refuse(why, "ConstantVariable with an expression qualifier");
            }
            var assignment = node as SimpleAssignmentExpression;
            if (assignment != null) {
                // `@x += @v' is a call of + between a read and a write
                if (!IsScopeFree(assignment.Right, blockLiterals, why)) {
                    return false;
                }
                if (assignment.Left is LocalVariable || assignment.Left is InstanceVariable ||
                    assignment.Left is GlobalVariable && IsLightGlobal((GlobalVariable)assignment.Left)) {
                    return assignment.Operation == null || LightCallsEnabled || Refuse(why, "operator assignment (IR_LIGHT_CALLS=0)");
                }
                // `o.x = v', `a[i] = v' (and their op-assigns): calls of x= and []=
                var attribute = assignment.Left as AttributeAccess;
                if (attribute != null) {
                    if (!LightCallsEnabled || attribute.IsSafeNavigation) {
                        return Refuse(why, LightCallsEnabled ? "assignment to a safe-navigation attribute" : "assignment to an attribute (IR_LIGHT_CALLS=0)");
                    }
                    return IsScopeFree(attribute.Qualifier, blockLiterals, why);
                }
                var item = assignment.Left as ArrayItemAccess;
                if (item == null || !LightCallsEnabled || item.Block != null) {
                    // the reason is built only when it was asked for: this path runs for every method definition
                    return why != null && Refuse(why, item == null ? "assignment to " + assignment.Left.GetType().Name :
                        LightCallsEnabled ? "assignment to an element with a block" : "assignment to an element (IR_LIGHT_CALLS=0)");
                }
                // `s[/x/] = v' is String#[]=, which stores $~ (RegexScopeMethods), in no frame in a match sink (IsMatchSink)
                if (!LightRegexAll && !t_matchSink && HasRegexLiteral(null, item.Arguments)) {
                    return why != null && Refuse(why, "RegularExpression passed to []= (it sets $~)");
                }
                return IsScopeFree(item.Array, blockLiterals, why) && AreScopeFree(item.Arguments, blockLiterals, why);
            }
            // `o.x op= v' (MemberAssignmentExpression.TransformReadCore): the call of x on the receiver, kept in a hidden variable
            // of the lambda, the operator's call (or `||'/`&&') and the call of x= - sites like those of `o.x = o.x + 1', with
            // nothing that takes the frame's scope. `o&.x op= v' is a nil test around one (PrismAstBridge.MemberOpAssign).
            var memberAssignment = node as MemberAssignmentExpression;
            if (memberAssignment != null) {
                if (!LightCallsEnabled || !LightMemberOpAssignEnabled) {
                    return Refuse(why, LightCallsEnabled ? "MemberAssignmentExpression (IR_LIGHT_MEMBER_OPASSIGN=0)" :
                        "MemberAssignmentExpression (IR_LIGHT_CALLS=0)");
                }
                return IsScopeFree(memberAssignment.LeftTarget, blockLiterals, why) && IsScopeFree(memberAssignment.Right, blockLiterals, why);
            }
            // a String literal is a constant, or a copy of one; a Symbol literal is a constant
            if (node is StringLiteral) {
                return true;
            }
            // the keyword prologue's reads of the keyword hash (PrismAstBridge.BindKeywordsFrom): one runtime call each, that
            // takes no scope, and a default evaluated in place when the keyword is absent
            var keywordRead = node as KeywordParameterRead;
            if (keywordRead != null) {
                return IsScopeFree(keywordRead.Keywords, blockLiterals, why) &&
                    (keywordRead.DefaultValue == null || IsScopeFree(keywordRead.DefaultValue, blockLiterals, why));
            }
            // the general lowering's reads of the one splat (PrismAstBridge.LowerGeneralParameters, IR_LIGHT_KEYWORD_REST): one
            // runtime call each on the hidden local, which takes no scope but PopKeywordArguments, which reads the runtime's
            // equality comparer for the empty hash of a call without keywords (LightFrameRewriter.ScopeOperandIndex); an optional
            // parameter's default is evaluated in place when the splat has no argument left for it
            var splatRead = node as SplatParameterRead;
            if (splatRead != null && LightKeywordRestEnabled) {
                return IsScopeFree(splatRead.Arguments, blockLiterals, why) &&
                    (splatRead.DefaultValue == null || IsScopeFree(splatRead.DefaultValue, blockLiterals, why));
            }
            var keywordTest = node as KeywordArgumentsTest;
            if (keywordTest != null) {
                return IsScopeFree(keywordTest.Value, blockLiterals, why);
            }
            if (LightCallsEnabled) {
                var array = node as ArrayConstructor;
                if (array != null) {
                    return AreScopeFree(array.Arguments, blockLiterals, why);
                }
                // `{a: x}', `{"k" => v}', `{}': the helper a literal is made by takes the scope only for its runtime
                // (LightFrameRewriter.ScopeOperandIndex), and a String key's frozen copy and the keys' #hash and #eql? are
                // the runtime's too; a key whose #hash may be a Ruby method keeps the frame's stack check (LightFrame.MakesCalls),
                // as a call would. `**h' in a literal is a call of Hash#merge on it. The hash a call site builds for its
                // keywords (`f(a: x)') is made the same way, only marked as keywords, and is an argument of the call like
                // any other; `f(**h)' is a KeywordSplatMerge, not taken.
                var hash = node as HashConstructor;
                if (hash != null) {
                    if (hash.IsKeywordArguments ? !LightKeywordHashEnabled : !LightHashEnabled) {
                        return Refuse(why, hash.IsKeywordArguments ? "HashConstructor (keyword arguments, IR_LIGHT_KEYWORD_HASH=0)" : "HashConstructor (IR_LIGHT_HASH=0)");
                    }
                    foreach (var maplet in hash.Maplets) {
                        if (!IsScopeFree(maplet.Key, blockLiterals, why) || !IsScopeFree(maplet.Value, blockLiterals, why)) {
                            return false;
                        }
                    }
                    return true;
                }
                // `(a..b)': the Range is made from its bounds and the literal's comparison storage (RubyOps.CreateInclusiveRange);
                // Range#initialize checks the stack before the <=> it calls for bounds that are not two Integers. Not in a body
                // with literal blocks: its light frame makes the scope before the Proc all the same, and SOM's
                // `def each; (@first..(@last - 1)).each { |i| yield @s[i] }; end' was 5-7% slower per call as one than with
                // the scope made on entry (`(a..b)' kept such a body out of light frames until then).
                var range = node as RangeExpression;
                if (range != null) {
                    if (blockLiterals) {
                        return Refuse(why, "RangeExpression in a body with literal blocks");
                    }
                    return IsScopeFree(range.Begin, false, why) && IsScopeFree(range.End, false, why);
                }
                // "#{x}" converts each part through a to_s site
                var interpolation = node as StringConstructor;
                if (interpolation != null && interpolation.Kind == StringKind.Mutable) {
                    foreach (var part in interpolation.Parts) {
                        if (!IsScopeFree(part, blockLiterals, why)) {
                            return false;
                        }
                    }
                    return true;
                }
                // `a, b = b, a', `x, *rest = list', `@a, (b, c) = f': the right side becomes an Array of its values (or one value
                // through an ImplicitSplatAction site, which takes no scope), the left values' receivers go to hidden variables,
                // and each left value is written from the list by an Array helper; a write is a local, an instance variable or a
                // call of x= or []=, each what a simple assignment to it is
                var parallel = node as ParallelAssignmentExpression;
                if (parallel != null) {
                    if (!LightMultipleAssignmentEnabled) {
                        return Refuse(why, "ParallelAssignmentExpression (IR_LIGHT_MASGN=0)");
                    }
                    foreach (var value in parallel.Right) {
                        if (!IsScopeFree(value, blockLiterals, why)) {
                            return false;
                        }
                    }
                    return IsScopeFreeTarget(parallel.Left, blockLiterals, why);
                }
                // `/x/', `/a#{x}/o': the Regexp of a literal is made once and kept in a box of the site's own
                // (RubyOps.CreateRegex*), the parts of one with interpolation are converted as a String's are; it reads no
                // scope. What reads one is the method it is given to, if it stores $~ (RegexScopeMethods); `if /x/' (a
                // RegularExpressionCondition) and `/(?<a>.)/ =~ s' (a MatchExpression) read the scope themselves.
                var regex = node as RegularExpression;
                if (regex != null) {
                    if (!LightRegexEnabled) {
                        return Refuse(why, "RegularExpression (IR_LIGHT_REGEX=0)");
                    }
                    foreach (var part in regex.Pattern) {
                        if (!IsScopeFree(part, blockLiterals, why)) {
                            return false;
                        }
                    }
                    return true;
                }
                var loop = node as WhileLoopExpression;
                if (loop != null) {
                    return IsScopeFree(loop.Condition, blockLiterals, why) && AreScopeFree(loop.Statements, blockLiterals, why);
                }
                var caseExpression = node as CaseExpression;
                if (caseExpression != null) {
                    if (caseExpression.Value != null && !IsScopeFree(caseExpression.Value, blockLiterals, why) || !AreScopeFree(caseExpression.ElseStatements, blockLiterals, why)) {
                        return false;
                    }
                    foreach (var clause in caseExpression.WhenClauses) {
                        foreach (var comparison in clause.Comparisons) {
                            if (comparison is RegularExpression && !LightRegexAll && !t_matchSink) {
                                return Refuse(why, "RegularExpression in a when clause (Regexp#=== sets $~)");
                            }
                            if (!IsScopeFree(comparison, blockLiterals, why)) {
                                return false;
                            }
                        }
                        if (!AreScopeFree(clause.Statements, blockLiterals, why)) {
                            return false;
                        }
                    }
                    return true;
                }
                // `inner(*args)', `[*a]': the value goes through an ExplicitSplatAction site, which takes no scope (#to_a is
                // called through a conversion site), and the call binds the Array as its splatted argument whatever scope it
                // is given (LightFrameRewriter.LightScopeCall)
                var splat = node as SplattedArgument;
                if (splat != null) {
                    return (LightSplatEnabled || why != null && Refuse(why, node.GetType().Name + " (IR_LIGHT_SPLAT=0)")) && IsScopeFree(splat.Argument, blockLiterals, why);
                }
                // `f(k: v, **h)', `f(**a, **b)': h's entries go into the call's keyword hash by a helper that takes the scope only
                // for its runtime (RubyOps.MergeKeywordSplat, MergeKeywordHash; LightFrameRewriter.ScopeOperandIndex), and
                // anything but a Hash or nil through `hash.merge(h || {})', a call like any other; the temporaries are locals
                // (IR_LIGHT_KEYWORD_SPLAT)
                var merge = node as KeywordSplatMerge;
                if (merge != null) {
                    if (!LightKeywordSplatEnabled) {
                        return Refuse(why, "KeywordSplatMerge (IR_LIGHT_KEYWORD_SPLAT=0)");
                    }
                    return IsScopeFree(merge.Hash, blockLiterals, why) && IsScopeFree(merge.Splatted, blockLiterals, why) &&
                        (merge.Converted == null || IsScopeFree(merge.Converted, blockLiterals, why));
                }
                // `super', `super(a, *r)', `super(&b)': the site is given the module and the name the body was compiled for,
                // which is what it read from the scope, and self (LightFrameRewriter.LightScopeCall); a parameterless super
                // passes the parameters, which are locals, and the method's block, which the frame then converts as a method's
                // (LightFrame.NamesBlock). Not one with a literal block, whose Proc would need the scope as its parent.
                var superCall = node as SuperCall;
                if (superCall != null) {
                    if (!LightSuperEnabled) {
                        return Refuse(why, "SuperCall (IR_LIGHT_SUPER=0)");
                    }
                    if (superCall.Block != null) {
                        var superBlock = superCall.Block as BlockReference;
                        if (superBlock == null || !LightBlockPassEnabled) {
                            return Refuse(why, superBlock == null ? "super with a literal block" : "super passing a block (IR_LIGHT_BLOCK_PASS=0)");
                        }
                        if (!IsScopeFree(superBlock.Expression, blockLiterals, why)) {
                            return false;
                        }
                    }
                    return AreScopeFree(superCall.Arguments, blockLiterals, why);
                }
                // `defined?(_.m)', Forwardable's guard: a site that only resolves the name (MethodCall.TransformDefinedCondition)
                // on the receiver, which is evaluated, and not the arguments. Light, it looks for a public method; anything else -
                // a protected or a missing one, respond_to_missing? - is asked again with the scope made on demand
                // (LightFrameRewriter.LightIsDefined).
                var defined = node as IsDefinedExpression;
                if (defined != null) {
                    var definedCall = defined.Expression as MethodCall;
                    if (definedCall == null || definedCall.Target == null || definedCall.Block != null) {
                        return Refuse(why, "IsDefinedExpression of " + defined.Expression.GetType().Name);
                    }
                    return IsScopeFree(definedCall.Target, blockLiterals, why);
                }
                // `inner(&block)': the Proc is the caller's own, or made by #to_proc through a conversion site that takes no
                // scope, and the call passes it as an argument (LightFrameRewriter.LightScopeCall)
                var call = node as MethodCall;
                if (call != null) {
                    if (!LightRegexAll && HasRegexLiteral(call.Target, call.Arguments) && RegexScopeMethods.Contains(call.MethodName) &&
                        !(t_matchSink && MatchSinkMethods.Contains(call.MethodName))) {
                        return why != null && Refuse(why, "RegularExpression passed to " + call.MethodName + " (it sets $~)");
                    }
                    var blockReference = call.Block as BlockReference;
                    if (call.Block != null && !(blockLiterals && call.Block is BlockDefinition)) {
                        if (blockReference == null || !LightBlockPassEnabled) {
                            return Refuse(why, blockReference == null ? "call with a literal block" : "call passing a block (IR_LIGHT_BLOCK_PASS=0)");
                        }
                        if (!IsScopeFree(blockReference.Expression, blockLiterals, why)) {
                            return false;
                        }
                    }
                    return (call.Target == null || IsScopeFree(call.Target, blockLiterals, why)) && AreScopeFree(call.Arguments, blockLiterals, why);
                }
                var conditional = node as ConditionalExpression;
                if (conditional != null) {
                    return IsScopeFree(conditional.Condition, blockLiterals, why) && IsScopeFree(conditional.TrueExpression, blockLiterals, why) &&
                        IsScopeFree(conditional.FalseExpression, blockLiterals, why);
                }
                var ifExpression = node as IfExpression;
                if (ifExpression != null) {
                    if (!IsScopeFree(ifExpression.Condition, blockLiterals, why) || !AreScopeFree(ifExpression.Body, blockLiterals, why)) {
                        return false;
                    }
                    if (ifExpression.ElseIfClauses != null) {
                        foreach (var clause in ifExpression.ElseIfClauses) {
                            if (clause.Condition != null && !IsScopeFree(clause.Condition, blockLiterals, why) || !AreScopeFree(clause.Statements, blockLiterals, why)) {
                                return false;
                            }
                        }
                    }
                    return true;
                }
                var unlessExpression = node as UnlessExpression;
                if (unlessExpression != null) {
                    return IsScopeFree(unlessExpression.Condition, blockLiterals, why) && AreScopeFree(unlessExpression.Statements, blockLiterals, why) &&
                        (unlessExpression.ElseClause == null || AreScopeFree(unlessExpression.ElseClause.Statements, blockLiterals, why));
                }
                var and = node as AndExpression;
                if (and != null) {
                    return IsScopeFree(and.Left, blockLiterals, why) && IsScopeFree(and.Right, blockLiterals, why);
                }
                var or = node as OrExpression;
                if (or != null) {
                    return IsScopeFree(or.Left, blockLiterals, why) && IsScopeFree(or.Right, blockLiterals, why);
                }
                var not = node as NotExpression;
                if (not != null) {
                    return IsScopeFree(not.Expression, blockLiterals, why);
                }
            }
            var yieldCall = node as YieldCall;
            if (yieldCall != null) {
                return (yieldCall.Block == null || Refuse(why, "yield with a block")) && AreScopeFree(yieldCall.Arguments, blockLiterals, why);
            }
            // `begin ... end' without rescue, else or ensure: its statements, compiled in place (Body.TransformRead). One with
            // them: its statements and clauses (AreClausesScopeFree), and the handling around them the rewriter takes.
            var begin = node as Body;
            if (begin != null) {
                if (HasHandling(begin)) {
                    return (LightRescueEnabled || Refuse(why, "Body with rescue, else or ensure (IR_LIGHT_RESCUE=0)")) &&
                        AreScopeFree(begin.Statements, blockLiterals, why) && AreClausesScopeFree(begin, blockLiterals, why);
                }
                return (LightBeginEnabled || Refuse(why, "Body (IR_LIGHT_BEGIN=0)")) && AreScopeFree(begin.Statements, blockLiterals, why);
            }
            // `x rescue y': a Body with a bare rescue clause (RescueExpression.ToBody)
            var modifier = node as RescueExpression;
            if (modifier != null) {
                return (LightRescueEnabled || Refuse(why, "RescueExpression (IR_LIGHT_RESCUE=0)")) &&
                    IsScopeFree(modifier.GuardedExpression, blockLiterals, why) && IsScopeFree(modifier.RescueClauseStatement, blockLiterals, why);
            }
            // `retry' in a rescue clause: a jump to the label at the head of the body, after a store to a hidden variable
            // (RetryStatement.TransformRetry). The parser refuses one outside a rescue clause, and IsScopeFree walks no block's body.
            if (node is RetryStatement) {
                return LightRescueEnabled || Refuse(why, "RetryStatement (IR_LIGHT_RESCUE=0)");
            }
            // `(yield self; self)', the body of an endless def written with several statements
            var block = node as BlockExpression;
            if (block != null) {
                foreach (var statement in block.Statements) {
                    if (!IsScopeFree(statement, blockLiterals, why)) {
                        return false;
                    }
                }
                return true;
            }
            var jump = node as ReturnStatement;
            if (jump != null) {
                return AreScopeFree(jump.Arguments, blockLiterals, why);
            }
            // `break v', `next', `redo' in a loop: a jump to one of the loop's labels, the value stored in the loop's result
            // (Break/Next/RedoStatement.TransformJump). IsScopeFree walks neither a block's body nor eval'd code, and the
            // parser refuses one outside a loop or a block, so every one it meets is a loop's. The scope is read only in an
            // eval, which is never a light frame, or to leave a class body, which is not scope-free.
            if (node is BreakStatement || node is NextStatement || node is RedoStatement) {
                return (LightBreakEnabled || Refuse(why, node.GetType().Name + " (IR_LIGHT_BREAK=0)")) &&
                    AreScopeFree(((JumpStatement)node).Arguments, blockLiterals, why);
            }
            // a node of a kind not taken at all: its type says what it is, and a Hash says whether it is a call's keywords
            if (why == null) {
                return false;
            }
            var keywords = node as HashConstructor;
            return Refuse(why, node.GetType().Name + (keywords != null && keywords.IsKeywordArguments ? " (keyword arguments)" : "") +
                (LightCallsEnabled ? "" : " (IR_LIGHT_CALLS=0)"));
        }

        // A left value of a multiple assignment that a light frame writes without its scope (see IsScopeFree)
        private static bool IsScopeFreeTarget(LeftValue/*!*/ target, bool blockLiterals, StrongBox<string> why) {
            if (target is LocalVariable || target is InstanceVariable || target is Placeholder) {
                return true;
            }
            var global = target as GlobalVariable;
            if (global != null && IsLightGlobal(global)) {
                return true;
            }
            var compound = target as CompoundLeftValue;
            if (compound != null) {
                foreach (var nested in compound.LeftValues) {
                    if (!IsScopeFreeTarget(nested, blockLiterals, why)) {
                        return false;
                    }
                }
                return true;
            }
            var attribute = target as AttributeAccess;
            if (attribute != null && !attribute.IsSafeNavigation) {
                return IsScopeFree(attribute.Qualifier, blockLiterals, why);
            }
            var item = target as ArrayItemAccess;
            if (item != null && item.Block == null) {
                if (!LightRegexAll && !t_matchSink && HasRegexLiteral(null, item.Arguments)) {
                    return why != null && Refuse(why, "RegularExpression passed to []= (it sets $~)");
                }
                return IsScopeFree(item.Array, blockLiterals, why) && AreScopeFree(item.Arguments, blockLiterals, why);
            }
            return why != null && Refuse(why, "multiple assignment to " + target.GetType().Name);
        }

        /// <summary>
        /// A global a light frame reads and writes without its scope: any but `$~' (the match) and `$_' (the last line read), which
        /// live in the frame (RubyScope.GetInnerMostClosureScope), so that a method naming them keeps its scope as it would make it for
        /// them anyway. Every other global is the runtime's (SpecialGlobalVariableInfo, GlobalVariableInfo). A name can still turn out
        /// to denote one of the two - an alias, English.rb's `$LAST_MATCH_INFO' - which the frame finds out when it reads or writes it,
        /// and then makes its scope (LightFrameRewriter.VisitGlobalVariableCall).
        /// </summary>
        private static bool IsLightGlobal(GlobalVariable/*!*/ global) {
            return LightGlobalsEnabled && global.Name != "~" && global.Name != "_";
        }

        // A, ::A, A::B::C - the reads ConstantVariable.MakeCachedRead makes - not {expr}::A
        private static bool IsCachedConstant(ConstantVariable/*!*/ constant) {
            var qualifier = constant.Qualifier;
            return qualifier == null || (qualifier is ConstantVariable && IsCachedConstant((ConstantVariable)qualifier));
        }

        // IR_LIGHT_LEXICAL: A, ::A, A::B::C, or {expr}::A::B whose expr is scope-free (see IsScopeFree)
        private static bool IsScopeFreeQualifier(ConstantVariable/*!*/ constant, bool blockLiterals, StrongBox<string> why) {
            var qualifier = constant.Qualifier;
            while (qualifier is ConstantVariable) {
                qualifier = ((ConstantVariable)qualifier).Qualifier;
            }
            return qualifier == null || IsScopeFree(qualifier, blockLiterals, why);
        }

        private static bool AreScopeFree(Statements statements, bool blockLiterals, StrongBox<string> why) {
            if (statements != null) {
                foreach (var statement in statements) {
                    if (!IsScopeFree(statement, blockLiterals, why)) {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool AreScopeFree(Arguments arguments, bool blockLiterals, StrongBox<string> why) {
            if (arguments != null) {
                foreach (var argument in arguments.Expressions) {
                    if (!IsScopeFree(argument, blockLiterals, why)) {
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
            MSA.Expression/*!*/ declaringModule, int visibleParameterCountAndSignatureFlags, ParkedScopeSlot parkSlot, ref MSA.Expression/*!*/ statements) {

            var names = scope.GetVariableNamesExpression() as MSA.ConstantExpression;
            if (names == null) {
                return null;
            }

            var light = new LightFrame();
            light.Rfc = scope.DefineHiddenVariable("#rfc", typeof(RuntimeFlowControl));
            light.Callee = scope.DefineHiddenVariable("#callee", typeof(string));
            light.Name = AstUtils.Constant(_name);
            light._blockParameter = blockParameter;
            // super hands the method's block on as `&b' would (SuperCall.TransformReadCore)
            light.NamesBlock = _parameters.Block != null || gen.CurrentMethod.HasSuperCall;

            var shape = Ast.Constant(new MethodFrameShape((string[])names.Value, visibleParameterCountAndSignatureFlags, _name));
            Func<MethodInfo, MSA.Expression, MSA.Expression> make = (method, locals) => method.OpCall(new AstExpressions {
                shape, declaringScope, declaringModule, locals, selfParameter, blockParameter,
                EnterInterpretedFrameExpression.Instance, light.Rfc, light.Callee
            });
            // with a parking slot: #parked = TakeParkedScope(...); #parked != null ? ReuseParkedScopeLazy(#parked, ...) : CreateMethodScopeLazy(...)
            var parkedVariable = gen.CurrentMethod.ParkedVariable;
            light.MakeScope = (parkSlot != null)
                ? Ast.Block(
                    Ast.Assign(parkedVariable, Methods.TakeParkedScope.OpCall(Ast.Constant(parkSlot), declaringScope, declaringModule)),
                    Ast.Condition(Ast.NotEqual(parkedVariable, AstUtils.Constant(null, typeof(ParkedProcs))),
                        Methods.ReuseParkedScopeLazy.OpCall(new AstExpressions {
                            parkedVariable, scope.LocalsTupleVariable, selfParameter, blockParameter,
                            EnterInterpretedFrameExpression.Instance, light.Rfc, light.Callee
                        }),
                        make(Methods.CreateMethodScopeLazy, scope.LocalsTupleVariable)
                    )
                )
                : make(Methods.CreateMethodScopeLazy, scope.LocalsTupleVariable);
            light.MakeScopeIfTraced = locals => make(Methods.CreateMethodScopeIfTraced, locals);
            var inLoop = scope.DefineHiddenVariable("#in-loop", typeof(bool));
            bool blockLiterals = LightBlocksEnabled && HasLightBlockLiteral();
            // A match sink keeps its $~ in a sink scope of its own, taken at its first call that stores $~, and puts it into the
            // scope it makes on demand (RubyOps.TakeMatchSink, SeedLightScopeMatch); it gives the sink back as it returns.
            var matchSlot = (!blockLiterals && IsMatchSink()) ? scope.DefineHiddenVariable("#sink", typeof(RubyScope)) : null;
            light.MatchSink = matchSlot;
            MSA.Expression lateScope = Ast.Call(_LightScopeInLoop, light.MakeScope, inLoop);
            if (matchSlot != null) {
                lateScope = Ast.Call(_SeedLightScopeMatch, lateScope, matchSlot);
            }
            light.LazyScope = Ast.Coalesce(scopeVariable, Ast.Assign(scopeVariable, lateScope));

            var rewriter = new LightFrameRewriter(scopeVariable, light.LazyScope, light.Rfc, declaringScope, inLoop, HasBlockGuard) {
                BlockLiterals = blockLiterals,
                LiteralCalls = LightLiteralCallsEnabled && LightBlockPassEnabled && HasOnlyNarrowLiterals(),
                MatchSlot = matchSlot,
                BlockParameter = LightBlockGivenEnabled ? blockParameter : null,
                SuperModule = LightSuperEnabled ? ConstantValue(declaringModule) as RubyModule : null,
                SuperName = _name,
                Handling = LightRescueEnabled,
                Context = gen.Context
            };
            var rewritten = rewriter.Visit(statements);
            if (LightFramesVerbose) {
                Console.Error.WriteLine("[light] {0} {1} ({2}:{3})", rewriter.NeedsScope ? "needs scope:" : "light:", _name, gen.SourcePath, Location.Start.Line);
            }
            if (rewriter.NeedsScope) {
                return null;
            }
            foreach (var site in rewriter.OsrSites) {
                site.InLightFrame = true;
            }
            // `eval("super")' through the binding of a scope made on demand finds the keyword signature by its local names
            SuperCall.RecordKeywordSignature((string[])names.Value, _parameters.Signature);
            statements = rewritten;
            light.MakesCalls = rewriter.MakesCalls || gen.CurrentMethod.HasHashCallingKeys;
            return light;
        }

        // The value of a constant expression, under its conversions (ExactConstant), or null.
        private static object ConstantValue(MSA.Expression/*!*/ node) {
            while (node.NodeType == MSA.ExpressionType.Convert) {
                node = ((MSA.UnaryExpression)node).Operand;
            }
            var constant = node as MSA.ConstantExpression;
            return (constant != null) ? constant.Value : null;
        }

        /// <summary>
        /// Points a scope-free method body at its light frame: MethodYield to LightMethodYield, the TracePoint hooks
        /// at the scope made on demand. Any other use of the scope, or a node it cannot look into, means the body needs
        /// its scope after all.
        /// </summary>
        private static readonly MethodInfo/*!*/ _LightScopeInLoop = typeof(RubyOps).GetMethod("LightScopeInLoop");
        private static readonly MethodInfo/*!*/ _SeedLightScopeMatch = typeof(RubyOps).GetMethod("SeedLightScopeMatch");
        private static readonly MethodInfo/*!*/ _TakeMatchSink = typeof(RubyOps).GetMethod("TakeMatchSink");
        private static readonly MethodInfo/*!*/ _ReturnMatchSink = typeof(RubyOps).GetMethod("ReturnMatchSink");

        private static MSA.Expression/*!*/ LeaveLightFrame(LightFrame/*!*/ light, MSA.ParameterExpression/*!*/ scopeVariable) {
            MSA.Expression leave = Methods.LeaveLightMethodFrame.OpCall(light.Rfc, scopeVariable);
            if (light.MatchSink == null) {
                return leave;
            }
            // A TracePoint's :return hook runs after this (TraceReturnExpression) and may make the scope then: it is made now,
            // with the sink's $~, before the sink is given back.
            return Ast.IfThenElse(Ast.NotEqual(light.MatchSink, AstUtils.Constant(null, typeof(RubyScope))),
                Ast.Block(
                    Ast.IfThen(
                        Ast.NotEqual(
                            Ast.And(Ast.Field(null, typeof(TracePoint).GetField("ActiveEvents")), AstUtils.Constant((int)TraceEvents.Return)),
                            AstUtils.Constant(0)
                        ),
                        Ast.Block(light.LazyScope, AstUtils.Empty())
                    ),
                    leave,
                    Ast.Call(_ReturnMatchSink, light.MatchSink)
                ),
                leave
            );
        }
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
            // The frame's sink when nothing in it reads $~ (IsMatchSink), null otherwise: a light site of one of MatchSinkMethods has
            // RubyCallFlags.HasMatchSink and is given the sink as its scope (MakeLightCall).
            internal MSA.ParameterExpression MatchSlot;

            // Every literal of the body is of the narrow shape (MethodDefinition.HasOnlyNarrowLiterals): a call with a block takes
            // the light site as in a body without literals (LightScopeCall), the literal's Proc, made over the scope made on
            // demand, being its block argument like any other; a rule that reads the caller's scope answers LightScopeRequired and
            // the call is made again through the full site with that scope. It is the call that no longer makes the scope, not
            // the literal: a literal's instantiation still does.
            internal bool LiteralCalls;

            // see LightFrame.MakesCalls
            internal bool MakesCalls;

            // The method's block parameter, in a method's frame: what `block_given?' asks about (see LightScopeCall). Null in
            // a block's frame, which has the method's block only through its scope.
            internal MSA.Expression BlockParameter;

            // The module the method's body is compiled for and the name it was defined under, in a method's frame: what a
            // `super' of the body looks the method up by (see LightScopeCall). Null in a block's frame, whose super finds the
            // method from its scope, and where light super sites are off (IR_LIGHT_SUPER=0).
            internal RubyModule SuperModule;
            internal string SuperName;

            // A method's frame, whose body may have exception handling (IsScopeFree, IR_LIGHT_RESCUE): its helpers are given the
            // scope they need (see VisitHandlingCall). Not a block's frame, whose body has none (MethodDefinition.IsScopeFree).
            internal bool Handling;

            // The runtime the body is compiled in, if known: a global whose name already denotes a variable of the frame's when the
            // body is compiled - English.rb's `$LAST_MATCH_INFO', required before - keeps the scope (see VisitGlobalVariableCall).
            internal RubyContext Context;

            // The OSR sites handed the declaring scope (see VisitMethodCall): marked as in a light frame once the body is one.
            internal readonly List<IronRuby.Runtime.Osr.OsrLoopSite>/*!*/ OsrSites = new List<IronRuby.Runtime.Osr.OsrLoopSite>();

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
            /// starts at its parent, which is the declaring scope. And the helpers that make a Hash literal or a call's keyword
            /// hash (HashConstructor), which read only the runtime's equality comparer from it. -1 for any other helper.
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
                if (LightGlobalsEnabled && method == Methods.IsDefinedGlobalVariable) {
                    return 0;
                }
                // class variables and constants qualified by an expression: the runtime, the global scope and the lexical chain
                // (see IsScopeFree, IR_LIGHT_LEXICAL)
                if (LightLexicalEnabled) {
                    if (method == Methods.GetClassVariable || method == Methods.TryGetClassVariable ||
                        method == Methods.IsDefinedClassVariable) {
                        return 0;
                    }
                    if (method == Methods.SetClassVariable || method == Methods.GetExpressionQualifiedConstant ||
                        method == Methods.IsDefinedExpressionQualifiedConstant) {
                        return 1;
                    }
                }
                if (LightHashEnabled && (method == Methods.MakeHash || method == Methods.MakeSymbolHash1 ||
                    method == Methods.MakeSymbolHash2 || method == Methods.MakeSymbolHash3)) {
                    return 0;
                }
                if (LightKeywordHashEnabled && (method == Methods.MakeKeywordArgumentsHash || method == Methods.MakeKeywordArgumentsHash0 ||
                    method == Methods.MakeKeywordArgumentsHash1 || method == Methods.MakeKeywordArgumentsHash2 ||
                    method == Methods.MakeKeywordArgumentsHash3)) {
                    return 0;
                }
                // the default of the hidden keyword parameter, the call passed no keywords: the runtime's shared empty hash, or a
                // new one for a `**rest' to take over
                // the keyword hash taken off the end of the general lowering's splat, or an empty one (IR_LIGHT_KEYWORD_REST)
                if (LightKeywordRestEnabled && method == Methods.PopKeywordArguments) {
                    return 0;
                }
                // `f(**h)' (KeywordArgumentSplat): the copy of h the call passes, made with the runtime's equality comparer, in a
                // one-or-zero element array for a splatted call, or the call's own keyword hash passed on by a parameterless
                // `super' (IR_LIGHT_KEYWORD_SPLAT)
                if (LightKeywordSplatEnabled && (method == Methods.SplatKeywordHash || method == Methods.SplatKeywordHashIfHash ||
                    method == Methods.KeywordSplatArgument || method == Methods.PassOnKeywordRest ||
                    method == Methods.MergeKeywordSplat || method == Methods.MergeKeywordHash)) {
                    return 0;
                }
                if (LightKeywordsEnabled && (method == Methods.GetOmittedKeywordArguments || method == Methods.MakeOmittedKeywordArguments)) {
                    return 0;
                }
                // a `case' over String literals looks its value up (CaseStringSite): the runtime only
                if (method == Methods.CaseStringDispatch) {
                    return 0;
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

            // The scope a helper of a body's exception handling (Body.TransformExceptionHandling, RescueClause) needs, by its name.
            private enum HandlingScope {
                // not a helper of the exception handling
                None,
                // only the runtime: $! is the runtime's (RubyContext.CurrentException), not the scope's
                Context,
                // the frame's own: CanRescue captures the backtrace from it (its interpreted frame) and tests a LocalJumpError
                // against its flow-control frame, CompareException hands it to a TracePoint's :rescue and to a refined `==='
                Lazy,
                // the scope if one was made, or null: RescuePassUnwinder, a CLR filter, tests a LocalJumpError against it, and none
                // can be meant for a frame that has made no scope - its blocks and evals would have made it
                Raw,
                // LeaveRescue clears the flag EnterRescue set (for `retry' in an eval), on the scope if one was made
                LeaveRescue,
            }

            // A switch rather than a Dictionary<string, HandlingScope>: the ReadyToRun image cannot hold the code of a framework
            // generic over a type of its own (an enum is shared as an int, and the framework's image has no Dictionary<string, int>
            // either), so the dictionary's methods were compiled by the JIT in each process that made a light frame.
            private static HandlingScope GetHandlingScope(string/*!*/ name) {
                switch (name) {
                    case nameof(RubyOps.GetCurrentException):
                    case nameof(RubyOps.SetCurrentException):
                    case nameof(RubyOps.GetCurrentExceptionForRescueTarget):
                    case nameof(RubyOps.RescuePassMayTest):
                    case nameof(RubyOps.RescueSiteCannotMatch):
                    case nameof(RubyOps.RescueDefaultCannotMatch):
                    case nameof(RubyOps.RescuePassCommit):
                    case nameof(RubyOps.SetCauseLeavingEnsure):
                        return HandlingScope.Context;
                    case nameof(RubyOps.RescuePassUnwinder):
                        return HandlingScope.Raw;
                    case nameof(RubyOps.LeaveRescue):
                        return HandlingScope.LeaveRescue;
                    case nameof(RubyOps.EnterRescue):
                    case nameof(RubyOps.CanRescue):
                    case nameof(RubyOps.CanRescueDeferTrace):
                    case nameof(RubyOps.CanRescueAfterClauses):
                    case nameof(RubyOps.SetCauseLeavingInterpretedEnsure):
                    case nameof(RubyOps.CompareException):
                    case nameof(RubyOps.CompareSplattedExceptions):
                    case nameof(RubyOps.CompareDefaultException):
                        return HandlingScope.Lazy;
                    default:
                        return HandlingScope.None;
                }
            }

            /// <summary>
            /// A helper of the body's exception handling, given the scope it needs (HandlingScope): the path that raises nothing
            /// saves and restores $!, which is the runtime's, and so makes no scope; the path that catches an exception makes it
            /// on demand where the exception is matched - the frame then pays for the scope it would have made at its entry.
            /// The CLR filters that let an exception through uncaught (RescuePass*, SetCauseLeavingEnsure) read only the runtime.
            /// Null for any other call.
            /// </summary>
            private MSA.Expression VisitHandlingCall(MSA.MethodCallExpression/*!*/ node) {
                if (node.Object != null || node.Method.DeclaringType != typeof(RubyOps)) {
                    return null;
                }
                var use = GetHandlingScope(node.Method.Name);
                if (use == HandlingScope.None) {
                    return null;
                }
                int index = -1;
                for (int i = 0; i < node.Arguments.Count; i++) {
                    if (IsScope(node.Arguments[i])) {
                        index = i;
                        break;
                    }
                }
                if (index < 0) {
                    return null;
                }
                if (use == HandlingScope.LeaveRescue) {
                    return Ast.IfThen(Ast.NotEqual(_scope, AstUtils.Constant(null, _scope.Type)), Ast.Call(node.Method, _scope));
                }
                var scope = (use == HandlingScope.Context) ? _declaringScope : (use == HandlingScope.Raw) ? _scope : _lazyScope;
                var arguments = new MSA.Expression[node.Arguments.Count];
                for (int i = 0; i < arguments.Length; i++) {
                    arguments[i] = (i == index) ? AstUtils.Convert(scope, node.Method.GetParameters()[i].ParameterType) : Visit(node.Arguments[i]);
                }
                return Ast.Call(node.Method, arguments);
            }

            private static readonly MethodInfo/*!*/ _ReadLightGlobalVariable = typeof(RubyOps).GetMethod("ReadLightGlobalVariable");
            private static readonly MethodInfo/*!*/ _GetLightGlobalVariable = typeof(RubyOps).GetMethod("GetLightGlobalVariable");
            private static readonly MethodInfo/*!*/ _SetLightGlobalVariable = typeof(RubyOps).GetMethod("SetLightGlobalVariable");
            private static readonly FieldInfo/*!*/ _LightScopeRequired = typeof(RubyOps).GetField("LightScopeRequired");

            /// <summary>
            /// `$x', `$x ||= v', `$x = v' (RubyOps.ReadCachedGlobalVariable, GetCachedGlobalVariable, SetCachedGlobalVariable) in a light
            /// frame: read and written by RubyOps.ReadLightGlobalVariable and its siblings, through the declaring scope, which has the
            /// runtime. They do nothing and answer LightScopeRequired when the name denotes a variable of the frame's - `$~' or `$_'
            /// under another name (an alias) - or when the scope may be read for something else (a trace_var handler, a Ractor's
            /// isolation); the frame then makes its scope and does it again with the helper the body was compiled with, as a call
            /// does whose rule reads the scope. The value assigned is evaluated once, before either. The frame keeps its stack check
            /// (MakesCalls): some variables run Ruby code when they are read or assigned (ARGF.filename for $FILENAME, #respond_to?
            /// for $stdout, the trace_var handlers), which may call the method again, as a constant read may call const_missing.
            /// Null for any other call.
            /// </summary>
            private MSA.Expression VisitGlobalVariableCall(MSA.MethodCallExpression/*!*/ node) {
                MethodInfo light;
                int scopeIndex;
                if (node.Method == Methods.ReadCachedGlobalVariable) {
                    light = _ReadLightGlobalVariable;
                    scopeIndex = 0;
                } else if (node.Method == Methods.GetCachedGlobalVariable) {
                    light = _GetLightGlobalVariable;
                    scopeIndex = 0;
                } else if (node.Method == Methods.SetCachedGlobalVariable) {
                    light = _SetLightGlobalVariable;
                    scopeIndex = 1;
                } else {
                    return null;
                }
                if (node.Object != null || node.Arguments.Count != scopeIndex + 2 || !IsScope(node.Arguments[scopeIndex]) ||
                    !(node.Arguments[scopeIndex + 1] is MSA.ConstantExpression)) {
                    return null;
                }
                // A name that denotes `$~' or `$_' now, an alias made before the method's first call, would make the scope on demand
                // at every read or write: the frame makes it at its entry instead, as it did before.
                var globalSite = ((MSA.ConstantExpression)node.Arguments[scopeIndex + 1]).Value as GlobalVariableSite;
                IronRuby.Runtime.GlobalVariable current;
                if (globalSite == null || Context != null && Context.TryGetGlobalVariable(globalSite.Name, out current) && current.ReadsScope) {
                    NeedsScope = true;
                    return node;
                }
                MakesCalls = true;
                var site = node.Arguments[scopeIndex + 1];
                var declaringScope = AstUtils.Convert(_declaringScope, typeof(RubyScope));
                var lazyScope = AstUtils.Convert(_lazyScope, typeof(RubyScope));
                var result = Ast.Variable(typeof(object), "#global");
                if (scopeIndex == 0) {
                    return Ast.Block(new[] { result },
                        Ast.Assign(result, Ast.Call(light, declaringScope, site)),
                        Ast.Condition(Ast.ReferenceEqual(result, Ast.Field(null, _LightScopeRequired)), Ast.Call(node.Method, lazyScope, site), result)
                    );
                }
                var value = Ast.Variable(typeof(object), "#global-value");
                return Ast.Block(new[] { value, result },
                    Ast.Assign(value, AstUtils.Convert(Visit(node.Arguments[0]), typeof(object))),
                    Ast.Assign(result, Ast.Call(light, value, declaringScope, site)),
                    Ast.Condition(Ast.ReferenceEqual(result, Ast.Field(null, _LightScopeRequired)), Ast.Call(node.Method, value, lazyScope, site), result)
                );
            }

            protected override MSA.Expression/*!*/ VisitMethodCall(MSA.MethodCallExpression/*!*/ node) {
                if (Handling) {
                    var handling = VisitHandlingCall(node);
                    if (handling != null) {
                        return handling;
                    }
                }
                if (LightGlobalsEnabled) {
                    var global = VisitGlobalVariableCall(node);
                    if (global != null) {
                        return global;
                    }
                }
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
                    // -X:OSR: a loop that has taken enough back edges hands its specialized copy the scope, which the copy
                    // reads only for its constants and its refinements. A method's light frame hands it the declaring scope
                    // instead, which the constants resolve in as the method scope's do (it is that scope's parent, and a method
                    // scope has no module of its own), and the site is told that the refinements are none (OsrLoopSite.
                    // InLightFrame). Making the scope on demand there cost a RubyMethodScope at every entry once the loop was
                    // specialized, which a loop entered at each call of its method then made at every call.
                    var site = (node.Object as MSA.ConstantExpression)?.Value as IronRuby.Runtime.Osr.OsrLoopSite;
                    var arguments = (node.Arguments.Count == 1) ? node.Arguments[0] as MSA.NewArrayExpression : null;
                    if (LightOsrEnabled && _rfc != null && site != null && arguments != null && site.ScopeArgIndex >= 0 &&
                        site.ScopeArgIndex < arguments.Expressions.Count && IsScope(arguments.Expressions[site.ScopeArgIndex])) {
                        var items = new MSA.Expression[arguments.Expressions.Count];
                        for (int i = 0; i < items.Length; i++) {
                            items[i] = (i == site.ScopeArgIndex) ? AstUtils.Convert(_declaringScope, typeof(object)) : Visit(arguments.Expressions[i]);
                        }
                        OsrSites.Add(site);
                        return Ast.Call(node.Object, node.Method, Ast.NewArrayInit(typeof(object), items));
                    }
                    // otherwise the scope is made on demand like a TracePoint hook's - once per loop entry that gets that far
                    _inTraceHook++;
                    try {
                        return base.VisitMethodCall(node);
                    } finally {
                        _inTraceHook--;
                    }
                }
                if (LightInterpolationEnabled && _rfc != null && node.Method == Methods.TryConvertToSWithRefinements &&
                    node.Arguments.Count == 2 && IsScope(node.Arguments[0])) {
                    // "#{x}" in a light frame, as `&x' below: no refinement is active in it, and its scope would answer
                    // none, so the helper would answer null and #to_s goes through the conversion site. A block's frame
                    // (_rfc is null) keeps the helper: a block may be written where a refinement is active, or be the
                    // body of a `refine', whose Proc carries the refinements it activates.
                    return Ast.Block(Visit(node.Arguments[1]), AstUtils.Constant(null, typeof(MutableString)));
                }
                if (LightBlockPassEnabled && _rfc != null && node.Arguments.Count >= 1 && IsScope(node.Arguments[0])) {
                    if (node.Method == Methods.TryConvertBlockWithRefinements && node.Arguments.Count == 2) {
                        // `&x' in a light frame: no refinement is active in it (a method defined where one is never is a light
                        // frame), and its scope, made at entry or on demand, would answer RubyScope.GetLexicalRefinements from
                        // its recorded definition refinements, which are none - so the helper would answer null, and #to_proc
                        // goes through the conversion site as it always does then.
                        return Ast.Block(Visit(node.Arguments[1]), AstUtils.Constant(null, typeof(Proc)));
                    }
                    if (node.Method == Methods.MethodPropagateReturn || node.Method == Methods.MethodRetry) {
                        // After a call with a block that answered a BlockReturnResult (a return or a retry in the block): rare,
                        // so the scope is made on demand there, as for a literal block's call. It has the frame's flow-control
                        // frame, which is what MethodPropagateReturn compares the return's target with.
                        _inTraceHook++;
                        try {
                            return base.VisitMethodCall(node);
                        } finally {
                            _inTraceHook--;
                        }
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
                if (Handling) {
                    // the exception handling's own nodes (Body.TransformExceptionHandling): their scope operands are the helpers'
                    // (VisitHandlingCall), but for the ensure guard's, which is handed to PassEnsure, CanRescue and EnterPassedEnsure
                    // only once an exception is leaving the statements
                    if (node is CompiledFrameDepthExpression) {
                        return node;
                    }
                    if (node is RubyFilterExpression || node is Microsoft.Scripting.Ast.FinallyFlowControlExpression) {
                        return base.VisitExtension(node);
                    }
                    var guard = node as EnsureGuardExpression;
                    if (guard != null) {
                        return guard.Update(Visit(guard.Body), IsScope(guard.Scope) ? _lazyScope : Visit(guard.Scope));
                    }
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
                if (LightBlockPassEnabled && dynamic != null && dynamic.Binder is IronRuby.Runtime.Conversions.ConvertToProcAction) {
                    // `&x' of something that is not a Proc: #to_proc through a conversion site, which is given no scope
                    return base.VisitExtension(node);
                }
                if (LightSplatEnabled && dynamic != null && dynamic.Binder is IronRuby.Runtime.Conversions.ExplicitSplatAction) {
                    // `*x': #to_a through a conversion site, which is given no scope
                    return base.VisitExtension(node);
                }
                if (LightInterpolationEnabled && dynamic != null && dynamic.Binder is IronRuby.Runtime.Conversions.ConvertToSAction) {
                    // "#{x}": #to_s through a conversion site, which is given no scope
                    return base.VisitExtension(node);
                }
                if (LightMultipleAssignmentEnabled && dynamic != null && dynamic.Binder is IronRuby.Runtime.Conversions.ImplicitSplatAction) {
                    // `a, b = x': #to_ary through a conversion site, which is given no scope
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
                var superAction = node.Binder as SuperCallAction;
                if (superAction != null) {
                    return LightSuperCall(node, superAction);
                }
                var action = node.Binder as RubyCallAction;
                if (!LightCallsEnabled || action == null || action.Context == null) {
                    return null;
                }
                var signature = action.Signature;
                if (signature.ResolveOnly) {
                    return LightIsDefined(node, action);
                }
                // A call with a block is one that passes a Proc on (`inner(&block)', IsScopeFree) unless the body has literal
                // blocks, whose calls use the scope made on demand (BlockLiterals). The Proc is an argument like the others: the
                // rules bind it as a block parameter whatever scope they are given, and one that reads the caller's scope answers
                // RubyOps.LightScopeRequired as for any call.
                bool blockPass = signature.HasBlock && LightBlockPassEnabled && (!BlockLiterals || LiteralCalls) && _rfc != null;
                if (!signature.HasScope || signature.HasBlock && !blockPass || signature.ResolveOnly || signature.IsSuperCall || signature.IsVirtualCall ||
                    signature.IsInteropCall || signature.HasLightScope) {
                    return null;
                }
                var reduced = node.Reduce() as MSA.DynamicExpression;
                if (reduced == null || reduced.Type != typeof(object) || reduced.Arguments.Count < 2 || !IsScope(reduced.Arguments[0])) {
                    return null;
                }

                var lightFlags = signature.Flags | RubyCallFlags.HasLightScope;
                // (not with a block: a library method that yields stores $~ for the block, which then reads it from the frame)
                bool sink = MatchSlot != null && !signature.HasBlock && MatchSinkMethods.Contains(action.MethodName);
                if (sink) {
                    lightFlags |= RubyCallFlags.HasMatchSink;
                }
                var lightAction = RubyCallAction.Make(action.Context, action.MethodName, new RubyCallSignature(signature.ArgumentCount, lightFlags));
                return MakeLightCall(reduced, action, lightAction, DispatchIntrinsics.IsLightBlockGivenSite(action.MethodName, lightAction.Signature), sink);
            }

            /// <summary>
            /// `defined?(obj.m)' (IsScopeFree), as a site that does not need the scope: the light site answers true or false
            /// boxed, or RubyOps.LightScopeRequired where its rule would read the scope (the method is not public, or is missing
            /// and respond_to_missing? is asked: RubyCallAction.Resolve), and the question is then asked again through the
            /// original site with the scope made on demand.
            /// </summary>
            private MSA.Expression LightIsDefined(Microsoft.Scripting.Ast.LightDynamicExpression/*!*/ node, RubyCallAction/*!*/ action) {
                var signature = action.Signature;
                if (!LightCallsEnabled || action.Context == null || !signature.HasScope || signature.HasImplicitSelf || signature.HasLightScope ||
                    signature.IsInteropCall || signature.IsSuperCall || signature.IsVirtualCall) {
                    return null;
                }
                var reduced = node.Reduce() as MSA.DynamicExpression;
                if (reduced == null || reduced.Type != typeof(bool) || reduced.Arguments.Count != 2 || !IsScope(reduced.Arguments[0])) {
                    return null;
                }
                var lightAction = RubyCallAction.Make(action.Context, action.MethodName,
                    RubyCallSignature.IsDefinedWithLightScope());
                var receiver = Visit(reduced.Arguments[1]);
                var target = Ast.Variable(receiver.Type, "#a1");
                var result = Ast.Variable(typeof(object), "#r");
                MakesCalls = true;
                return Ast.Block(new[] { target, result },
                    Ast.Assign(target, receiver),
                    Ast.Assign(result, AstUtils.LightDynamic(lightAction, typeof(object), AstUtils.Convert(_declaringScope, typeof(RubyScope)), target)),
                    Ast.Condition(
                        Ast.ReferenceEqual(result, AstUtils.Constant(RubyOps.LightScopeRequired)),
                        AstUtils.LightDynamic(action, typeof(bool), AstUtils.Convert(_lazyScope, typeof(RubyScope)), target),
                        Ast.Convert(result, typeof(bool))
                    )
                );
            }

            /// <summary>
            /// `super' in the method's own body, as a call that does not need the scope (see LightScopeCall): what the site read
            /// from the scope - the module the method was defined in, the name it was defined under, its self - is the body's
            /// module and name, which its light site is made with (SuperCallAction.MakeLight), and the site's own self. The
            /// block it passes is the method's, or `&b' (IsScopeFree takes no literal block). Returns null for a site it cannot take.
            /// </summary>
            private MSA.Expression LightSuperCall(Microsoft.Scripting.Ast.LightDynamicExpression/*!*/ node, SuperCallAction/*!*/ action) {
                var signature = action.Signature;
                if (!LightCallsEnabled || SuperModule == null || _rfc == null || action.Context == null || !signature.HasScope ||
                    !signature.HasBlock || signature.ResolveOnly || signature.HasLightScope) {
                    return null;
                }
                var reduced = node.Reduce() as MSA.DynamicExpression;
                if (reduced == null || reduced.Type != typeof(object) || reduced.Arguments.Count < 2 || !IsScope(reduced.Arguments[0])) {
                    return null;
                }
                return MakeLightCall(reduced, action, action.MakeLight(SuperModule, SuperName), false);
            }

            // The call through the light site, then through the ordinary one with the scope made on demand if the light site's
            // rule answered RubyOps.LightScopeRequired, both with the same evaluated arguments (see LightScopeCall).
            private MSA.Expression/*!*/ MakeLightCall(MSA.DynamicExpression/*!*/ reduced, RubyMetaBinder/*!*/ action, RubyMetaBinder/*!*/ lightAction,
                bool blockGiven, bool matchSink = false) {
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
                // (a LightDynamicExpression takes its builder's arguments: each site gets a copy)
                AstExpressions sinkLightArgs = null;
                if (matchSink) {
                    // the frame's sink, taken at its first such call (RubyOps.TakeMatchSink), in place of the declaring scope
                    sinkLightArgs = new AstExpressions(lightArgs);
                    sinkLightArgs[0] = Ast.Coalesce(MatchSlot, Ast.Assign(MatchSlot,
                        Ast.Call(_TakeMatchSink, AstUtils.Convert(_declaringScope, typeof(RubyScope)))));
                }
                var sinkFullArgs = matchSink ? new AstExpressions(fullArgs) : null;
                var sinkLateArgs = matchSink ? new AstExpressions(fullArgs) : null;
                body.Add(Ast.Assign(result, AstUtils.LightDynamic(lightAction, typeof(object), lightArgs)));
                MSA.Expression needsScope = Ast.ReferenceEqual(result, AstUtils.Constant(RubyOps.LightScopeRequired));
                if (blockGiven && BlockParameter == null) {
                    // a block's frame has the method's block only through its scope: the call, as for any rule that reads it
                    needsScope = Ast.OrElse(needsScope, Ast.ReferenceEqual(result, AstUtils.Constant(RubyOps.LightBlockGiven)));
                }
                MSA.Expression value = Ast.Condition(needsScope, AstUtils.LightDynamic(action, typeof(object), fullArgs), result);
                if (matchSink) {
                    // The light call stores $~ in the frame's sink. Once the frame has its scope, the call stores $~ there, as an
                    // ordinary one does.
                    body.RemoveAt(body.Count - 1);
                    value = Ast.Condition(
                        Ast.NotEqual(_scope, AstUtils.Constant(null, _scope.Type)),
                        AstUtils.LightDynamic(action, typeof(object), sinkFullArgs),
                        Ast.Block(
                            Ast.Assign(result, AstUtils.LightDynamic(lightAction, typeof(object), sinkLightArgs)),
                            Ast.Condition(needsScope,
                                AstUtils.LightDynamic(action, typeof(object), sinkLateArgs),
                                result
                            )
                        )
                    );
                }
                if (blockGiven && BlockParameter != null) {
                    // `block_given?' that is Kernel's (DispatchIntrinsics.TryBuildBlockGiven): whether the frame was given a
                    // block, which is what Kernel#block_given? reads from the scope this frame would have to make for it -
                    // CreateMethodScopeLazy is handed this same parameter.
                    value = Ast.Condition(
                        Ast.ReferenceEqual(result, AstUtils.Constant(RubyOps.LightBlockGiven)),
                        Ast.Condition(
                            Ast.ReferenceNotEqual(BlockParameter, AstUtils.Constant(null, BlockParameter.Type)),
                            Ast.Field(null, typeof(Microsoft.Scripting.Runtime.ScriptingRuntimeHelpers).GetField("True")),
                            Ast.Field(null, typeof(Microsoft.Scripting.Runtime.ScriptingRuntimeHelpers).GetField("False"))
                        ),
                        value
                    );
                }
                body.Add(value);
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
