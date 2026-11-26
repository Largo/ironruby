/* ****************************************************************************
 *
 * -X:JIT for the body of a method made by define_method.
 *
 * The method JIT works on a def's syntax tree (MethodDefinition): JitStub wraps the generic body, profiles
 * it, and asks JitCompiler for a typed body. A method made by define_method runs a block instead
 * (RubyLambdaMethodInfo), which it never took: such a call cost twice a def's (gap 23 of Util/perf/LEDGER.md).
 *
 * A block can be taken as a def when nothing in it tells the two apart:
 *   - it assigns none of the enclosing scopes' locals (BlockDispatcher.AssignedOuterVariableNames is null, so
 *     BlockDefinition kept its tree: BlockDispatcher.JitAst), and was not compiled for eval or tracing. Those it
 *     reads, the typed body reads where the block does - from the locals of the scope that many levels up from
 *     the Proc's (ReadCaptured), at each read, so a change made after the method was defined is seen;
 *   - its parameters are a def's (plain mandatory, then optional; BlockDefinition.IsJitMethodShape);
 *   - its body has nothing whose meaning is the defining scope's rather than the method's: no yield and no
 *     block_given? (the defining method's block), no super (implicit super raises; explicit super names the
 *     method's owner), no class variable (the block's lexical class), no __method__, binding and the like, no
 *     block, lambda or block-pass, no regular expression or global variable (the frame's $~ and $_).
 * The rest is the same: self is the receiver, `return' returns from the method, constants resolve in the
 * scope the block was written in (the Proc's LocalScope, as a def's in its declaring scope), and the calls
 * see the refinements active there. The JIT's own screen (JitStub.Screen) then decides as for a def.
 *
 * The generic body the stub falls back to - while profiling, on a guard that fails, on a deopt - is the
 * call the rule makes without the JIT (Proc.BuildCall), written in C#. The rule calls the stub for a call
 * with plain arguments, no block, splat or assignment value (RubyLambdaMethodInfo.BuildCallNoFlow).
 * IR_JIT_DMETH=0 turns it off.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime.Jit {
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    internal sealed class JitBlockMethod {
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("IR_JIT_DMETH") != "0";

        private readonly Proc/*!*/ _lambda;

        private JitBlockMethod(Proc/*!*/ lambda) {
            _lambda = lambda;
        }

        /// <summary>
        /// The JIT's trampoline for the method, or null when its block is not one the JIT may take (see above).
        /// <paramref name="ast"/> is the def the block is taken as, which the stub specializes.
        /// </summary>
        internal static Delegate TryWrap(RubyLambdaMethodInfo/*!*/ method, out MethodDeclaration ast) {
            ast = null;
            var lambda = method.Lambda;
            var block = lambda.Dispatcher.JitAst;
            var context = method.DeclaringModule.Context;
            if (block == null || !context.RubyOptions.Jit) { return null; }

            List<LocalVariable> captured;
            string rejectedBy = BlockScreen.Rejects(block, out captured);
            var scope = lambda.LocalScope;
            Dictionary<LocalVariable, CapturedLocal> slots = null;
            if (rejectedBy == null && captured != null) {
                slots = ResolveCaptured(block, scope, captured, out rejectedBy);
            }
            if (rejectedBy != null) {
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] define_method {0}/{1} not taken: {2}", method.DeclaringModule.Name ?? "main", method.DefinitionName, rejectedBy);
                }
                return null;
            }

            var location = block.Location;
            var def = new BlockMethodDefinition(block.DefinedScope, method.DefinitionName, block.Parameters,
                new Body(block.Body, null, null, null, location), location, slots) {
                // the body neither yields nor passes a block on (BlockScreen): the call's block is nothing to it
                UsesBlock = false
            };

            // The refinements the block's calls see are those active where it was written.
            StrongBox<RefinementActivation> refinements = null;
            if (context.HasRefinements) {
                var active = scope.GetActiveRefinements();
                if (!active.IsEmpty) {
                    refinements = new StrongBox<RefinementActivation>(active);
                }
            }

            var generic = new JitBlockMethod(lambda).MakeGeneric(def.Parameters.Mandatory.Length + def.Parameters.Optional.Length);
            if (generic == null) { return null; }
            var result = JitStub.Wrap(generic, def, context, method.DeclaringModule, scope, refinements);
            if (result == generic) { return null; }
            ast = def;
            return result;
        }

        /// <summary>
        /// Where each local of an enclosing scope the block reads lives: the scope as many levels up from the Proc's as the
        /// block is nested in the variable's, and its slot there - what the block's closure reads (ScopeBuilder.CreateScope).
        /// The runtime chain is checked against the lexical one: a scope between that declares the name, or a level that has
        /// no scope, and the block is not taken.
        /// </summary>
        private static Dictionary<LocalVariable, CapturedLocal> ResolveCaptured(BlockDefinition/*!*/ block, RubyScope/*!*/ scope,
            List<LocalVariable>/*!*/ captured, out string rejectedBy) {
            rejectedBy = null;
            var result = new Dictionary<LocalVariable, CapturedLocal>();
            var byKey = new Dictionary<string, CapturedLocal>();
            int depth = block.DefinedScope.Depth;
            foreach (var local in captured) {
                string key = local.Name + "@" + local.DefinitionLexicalDepth;
                CapturedLocal slot;
                if (!byKey.TryGetValue(key, out slot)) {
                    int hops = depth - 1 - local.DefinitionLexicalDepth;
                    RubyScope owner = scope;
                    int index;
                    for (int i = 0; i < hops && owner != null; i++) {
                        if (owner.TryGetLocalSlot(local.Name, out index)) { owner = null; break; }
                        owner = owner.Parent;
                    }
                    if (owner == null || !owner.TryGetLocalSlot(local.Name, out index)) {
                        rejectedBy = "captured local " + local.Name + " not found where the block was written";
                        return null;
                    }
                    slot = new CapturedLocal(owner, index);
                    byKey.Add(key, slot);
                }
                result[local] = slot;
            }
            return result;
        }

        /// <summary>A local of an enclosing scope a define_method body reads: the scope and the slot (ResolveCaptured).</summary>
        internal sealed class CapturedLocal {
            internal readonly RubyScope/*!*/ Scope;
            internal readonly int Index;

            internal CapturedLocal(RubyScope/*!*/ scope, int index) {
                Scope = scope;
                Index = index;
            }
        }

        /// <summary>A read of a captured local in a typed body (JitCompiler.EmitLocalRead): its value now.</summary>
        public static object ReadCaptured(CapturedLocal/*!*/ local) {
            return local.Scope.GetLocalSlotValue(local.Index);
        }

        internal static readonly System.Reflection.MethodInfo ReadCapturedMethod = typeof(JitBlockMethod).GetMethod("ReadCaptured");

        /// <summary>
        /// The def a define_method body is taken as, which knows where the locals of enclosing scopes it reads live: the
        /// screen (JitScreen) and the compiler (JitCompiler.EmitLocalRead) take a read of one, which they take of no def.
        /// </summary>
        internal sealed class BlockMethodDefinition : MethodDeclaration {
            private readonly Dictionary<LocalVariable, CapturedLocal> _captured;

            internal BlockMethodDefinition(IronRuby.Compiler.LexicalScope/*!*/ definedScope, string/*!*/ name, Parameters/*!*/ parameters, Body/*!*/ body,
                Microsoft.Scripting.SourceSpan location, Dictionary<LocalVariable, CapturedLocal> captured)
                : base(definedScope, null, name, parameters, body, location) {
                _captured = captured;
            }

            internal CapturedLocal GetCaptured(LocalVariable/*!*/ local) {
                CapturedLocal slot;
                return (_captured != null && _captured.TryGetValue(local, out slot)) ? slot : null;
            }
        }

        private Delegate MakeGeneric(int arity) {
            switch (arity) {
                case 0: return new Func<object, Proc, object>(Call0);
                case 1: return new Func<object, Proc, object, object>(Call1);
                case 2: return new Func<object, Proc, object, object, object>(Call2);
                case 3: return new Func<object, Proc, object, object, object, object>(Call3);
                case 4: return new Func<object, Proc, object, object, object, object, object>(Call4);
                case 5: return new Func<object, Proc, object, object, object, object, object, object>(Call5);
                case 6: return new Func<object, Proc, object, object, object, object, object, object, object>(Call6);
                default: return null;
            }
        }

        // What the rule of Proc.BuildCall does: a BlockParam for the call, the yield, and a return from a block
        // nested in the body caught at this call. An omitted optional argument arrives as RubyOps.DefaultArgument,
        // which is what the dispatcher would hand the body for it.

        private object Call0(object self, Proc block) {
            var bfc = RubyOps.CreateBfcForProcCall(_lambda);
            try {
                return RubyOps.MethodProcCall(bfc, RubyOps.Yield0(block, self, bfc));
            } catch (Exception e) when (RubyOps.IsLambdaUnwinderTarget(bfc, e)) {
                return RubyOps.GetLambdaUnwinderReturnValue(e);
            } finally {
                RubyOps.LeaveProcCall(bfc);
            }
        }

        private object Call1(object self, Proc block, object a0) {
            var bfc = RubyOps.CreateBfcForProcCall(_lambda);
            try {
                return RubyOps.MethodProcCall(bfc, RubyOps.Yield1(a0, block, self, bfc));
            } catch (Exception e) when (RubyOps.IsLambdaUnwinderTarget(bfc, e)) {
                return RubyOps.GetLambdaUnwinderReturnValue(e);
            } finally {
                RubyOps.LeaveProcCall(bfc);
            }
        }

        private object Call2(object self, Proc block, object a0, object a1) {
            var bfc = RubyOps.CreateBfcForProcCall(_lambda);
            try {
                return RubyOps.MethodProcCall(bfc, RubyOps.Yield2(a0, a1, block, self, bfc));
            } catch (Exception e) when (RubyOps.IsLambdaUnwinderTarget(bfc, e)) {
                return RubyOps.GetLambdaUnwinderReturnValue(e);
            } finally {
                RubyOps.LeaveProcCall(bfc);
            }
        }

        private object Call3(object self, Proc block, object a0, object a1, object a2) {
            var bfc = RubyOps.CreateBfcForProcCall(_lambda);
            try {
                return RubyOps.MethodProcCall(bfc, RubyOps.Yield3(a0, a1, a2, block, self, bfc));
            } catch (Exception e) when (RubyOps.IsLambdaUnwinderTarget(bfc, e)) {
                return RubyOps.GetLambdaUnwinderReturnValue(e);
            } finally {
                RubyOps.LeaveProcCall(bfc);
            }
        }

        private object Call4(object self, Proc block, object a0, object a1, object a2, object a3) {
            var bfc = RubyOps.CreateBfcForProcCall(_lambda);
            try {
                return RubyOps.MethodProcCall(bfc, RubyOps.Yield4(a0, a1, a2, a3, block, self, bfc));
            } catch (Exception e) when (RubyOps.IsLambdaUnwinderTarget(bfc, e)) {
                return RubyOps.GetLambdaUnwinderReturnValue(e);
            } finally {
                RubyOps.LeaveProcCall(bfc);
            }
        }

        private object Call5(object self, Proc block, object a0, object a1, object a2, object a3, object a4) {
            return CallN(self, block, new[] { a0, a1, a2, a3, a4 });
        }

        private object Call6(object self, Proc block, object a0, object a1, object a2, object a3, object a4, object a5) {
            return CallN(self, block, new[] { a0, a1, a2, a3, a4, a5 });
        }

        private object CallN(object self, Proc block, object[]/*!*/ args) {
            var bfc = RubyOps.CreateBfcForProcCall(_lambda);
            try {
                return RubyOps.MethodProcCall(bfc, RubyOps.YieldN(args, block, self, bfc));
            } catch (Exception e) when (RubyOps.IsLambdaUnwinderTarget(bfc, e)) {
                return RubyOps.GetLambdaUnwinderReturnValue(e);
            } finally {
                RubyOps.LeaveProcCall(bfc);
            }
        }

        /// <summary>What in a block body means something else in a def's (see the header).</summary>
        private sealed class BlockScreen : Walker {
            // Calls on self that answer for the frame they are made in, which is the block's.
            private static readonly HashSet<string> FrameCalls = new HashSet<string> {
                "block_given?", "iterator?", "__method__", "__callee__", "__dir__", "binding", "local_variables",
                "proc", "lambda", "eval", "caller", "caller_locations", "define_method", "instance_eval", "instance_exec",
                "class_eval", "module_eval", "class_exec", "module_exec", "using", "require_relative", "autoload",
            };

            private string _rejectedBy;
            private int _depth;
            // the reads of enclosing scopes' locals (BlockDefinition kept no block that assigns one)
            private List<LocalVariable> _captured;

            internal static string Rejects(BlockDefinition/*!*/ block, out List<LocalVariable> captured) {
                var screen = new BlockScreen { _depth = block.DefinedScope.Depth };
                try {
                    foreach (var statement in block.Body) {
                        screen.Walk(statement);
                        if (screen._rejectedBy != null) { break; }
                    }
                    foreach (var optional in block.Parameters.Optional) {
                        if (screen._rejectedBy != null) { break; }
                        screen.Walk(optional.Right);
                    }
                } catch (Exception) {
                    screen._rejectedBy = "screen-error";
                }
                captured = screen._captured;
                return screen._rejectedBy;
            }

            private bool No(string/*!*/ what) {
                if (_rejectedBy == null) { _rejectedBy = what; }
                return false;
            }

            public override bool Enter(YieldCall/*!*/ node) { return No("yield"); }
            public override bool Enter(SuperCall/*!*/ node) { return No("super"); }
            public override bool Enter(ClassVariable/*!*/ node) { return No("class variable"); }
            public override bool Enter(IronRuby.Compiler.Ast.GlobalVariable/*!*/ node) { return No("global variable"); }
            public override bool Enter(RegexMatchReference/*!*/ node) { return No("match reference"); }
            public override bool Enter(RegularExpression/*!*/ node) { return No("regular expression"); }
            public override bool Enter(RegularExpressionCondition/*!*/ node) { return No("regular expression"); }
            public override bool Enter(BlockDefinition/*!*/ node) { return No("block"); }
            public override bool Enter(LambdaDefinition/*!*/ node) { return No("lambda"); }
            public override bool Enter(BlockReference/*!*/ node) { return No("block-pass"); }
            public override bool Enter(MethodDefinition/*!*/ node) { return No("def"); }
            public override bool Enter(ClassDefinition/*!*/ node) { return No("class"); }
            public override bool Enter(ModuleDefinition/*!*/ node) { return No("module"); }
            public override bool Enter(SingletonDefinition/*!*/ node) { return No("class << self"); }

            public override bool Enter(LocalVariable/*!*/ node) {
                int depth = node.DefinitionLexicalDepth;
                if (depth < _depth) {
                    // -1: a local of the scope an eval runs in, which no lexical scope holds
                    if (depth < 0) { return No("local of an eval's scope"); }
                    if (_captured == null) { _captured = new List<LocalVariable>(); }
                    _captured.Add(node);
                }
                return true;
            }

            public override bool Enter(MethodCall/*!*/ node) {
                if ((node.Target == null || node.Target is SelfReference) && FrameCalls.Contains(node.MethodName)) {
                    return No(node.MethodName);
                }
                return true;
            }
        }
    }
}
