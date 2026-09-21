/* ****************************************************************************
 *
 * -X:OSR, second tier: a type-specialized copy of a hot loop.
 *
 * Outlining a loop and handing it to the DLR's compiler is, on its own, worth almost
 * nothing: IronRuby's compiled tier runs an arithmetic loop at the same speed as its
 * interpreter, because what costs is the dynamic call site on every `+' and every `<', not
 * the interpretation. What makes the outlining pay is that it gives the loop a compilation
 * point of its own - and at that point the loop's locals are sitting in the enclosing
 * scope's MutableTuple with their values visible, which is a type profile no counter had to
 * collect.
 *
 * So when a loop turns hot, this asks JitCompiler for a typed body, with the scope's locals
 * bound to CLR int/double/bool locals loaded from the tuple on entry and stored back on the
 * way out. Inside, a Fixnum add is an add.
 *
 * Deoptimization is what keeps that honest. The body signals one (JitDeoptException) when a
 * Fixnum overflows, when a division would raise, or when anything else it assumed stops
 * holding. The loop keeps a copy of its locals as they were at the top of the current
 * iteration; a deopt restores those and returns the Retry sentinel, so the loop is handed
 * back at an iteration boundary and the generic copy carries on from there. JitCompiler only
 * produces a body whose iterations are unobservable part way through, which is what makes
 * re-running the interrupted one invisible.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IronRuby.Compiler.Ast;
using IronRuby.Builtins;
using IronRuby.Runtime.Osr;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;

    /// <summary>One scope local the loop touches, bound to its slot in the scope's tuple.</summary>
    internal sealed class JitLoopSlot {
        internal JT Type;
        internal MSA.ParameterExpression/*!*/ Variable;
        internal MSA.ParameterExpression/*!*/ Snapshot;
        /// <summary>The tuple property chain the entry reads and the exits write.</summary>
        internal MSA.Expression/*!*/ Accessor;
        /// <summary>
        /// Non-null when the slot did not hold a value of this type when the loop turned hot -
        /// a temporary the loop creates itself. The entry then loads nothing and this flag says
        /// whether the run has written the slot yet; the exits store it only once it has.
        /// </summary>
        internal MSA.ParameterExpression Assigned;
        internal MSA.ParameterExpression AssignedSnapshot;
    }

    internal static class JitLoopCompiler {
        /// <summary>
        /// Builds a typed entry for a loop, or returns null when the loop is not the shape
        /// this can specialize. <paramref name="args"/> are the locals tuples the site passes,
        /// one per lexical depth; they carry the live values the types are read from, which is
        /// a type profile a loop that is already running gets for free.
        /// </summary>
        internal static Func<object[], object> TryCompile(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context,
            int scopeDepth, Dictionary<int, int>/*!*/ tupleArgIndex, object[]/*!*/ args) {

            if (JitRuntime.Disabled || context.HasRefinements) { return null; }

            var argsParameter = Ast.Parameter(typeof(object[]), "#args");
            var exit = Ast.Label(typeof(object), "#osr-jit-exit");
            var tuples = new Dictionary<int, MSA.ParameterExpression>();
            var tupleInit = new List<MSA.Expression>();
            var tupleGuards = new List<MSA.Expression>();

            Func<LocalVariable, JT, JitLoopSlot> bind = (local, hint) => {
                int depth = local.DefinitionLexicalDepth;
                int argIndex;
                if (!tupleArgIndex.TryGetValue(depth, out argIndex) || argIndex >= args.Length) {
                    if (OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   no tuple for {0} at depth {1}", local.Name, depth); }
                    return null;
                }
                object tupleValue = args[argIndex];
                if (tupleValue == null) { return null; }

                MSA.ParameterExpression tuple;
                if (!tuples.TryGetValue(depth, out tuple)) {
                    Type tupleType = tupleValue.GetType();
                    tuple = Ast.Parameter(tupleType, "#tuple" + depth);
                    tuples.Add(depth, tuple);
                    tupleGuards.Add(Ast.Not(Ast.TypeIs(Ast.ArrayIndex(argsParameter, Ast.Constant(argIndex)), tupleType)));
                    tupleInit.Add(Ast.Assign(tuple,
                        Ast.Convert(Ast.ArrayIndex(argsParameter, Ast.Constant(argIndex)), tupleType)));
                }

                object current;
                MSA.Expression accessor;
                try {
                    accessor = ScopeBuilder.GetVariableAccessor(tuple, local.ClosureIndex);
                    // Read the live value down the very path the emitted code will use.
                    current = tupleValue;
                    foreach (var property in MutableTuple.GetAccessPath(tupleValue.GetType(), local.ClosureIndex)) {
                        current = property.GetValue(current, null);
                    }
                } catch (Exception) {
                    return null;
                }

                JT type;
                bool writeFirst = (hint == JT.Int || hint == JT.Dbl);
                if (writeFirst) {
                    // The loop met this local as the target of an assignment, so the type it
                    // will hold is the one being stored into it, whatever it holds right now.
                    // What it holds at entry is then not something to insist on either - the
                    // scope may be brand new and the slot nil - so the slot carries a flag
                    // rather than a guard: reads before the first write deopt, and the exits
                    // leave the slot alone until the flag is up.
                    type = hint;
                } else if (current is int) {
                    type = JT.Int;
                } else if (current is double) {
                    type = JT.Dbl;
                } else {
                    if (OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   {0} holds {1}", local.Name, current == null ? "nil" : current.GetType().Name); }
                    return null;
                }

                Type clr = (type == JT.Int) ? typeof(int) : typeof(double);
                return new JitLoopSlot {
                    Type = type,
                    Variable = Ast.Parameter(clr, local.Name),
                    Snapshot = Ast.Parameter(clr, local.Name + "#snap"),
                    Accessor = accessor,
                    Assigned = writeFirst ? Ast.Parameter(typeof(bool), local.Name + "#set") : null,
                    AssignedSnapshot = writeFirst ? Ast.Parameter(typeof(bool), local.Name + "#set-snap") : null,
                };
            };

            List<JitLoopSlot> slots;
            bool canDeopt;
            MSA.Expression body = JitCompiler.TryCompileLoop(loop, context, scopeDepth, bind, out slots, out canDeopt);
            if (body == null || slots.Count == 0) {
                if (OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   emitter gave up (body={0} slots={1})", body != null, slots == null ? -1 : slots.Count); }
                return null;
            }

            var locals = new List<MSA.ParameterExpression>(tuples.Values);
            var probe = Ast.Parameter(typeof(object), "#probe");
            locals.Add(probe);

            // The scope this loop belongs to always makes tuples of the same shape, but nothing
            // in the type system says so: check before the cast rather than let an unexpected
            // one become an InvalidCastException in the middle of Ruby code.
            var prologue = new List<MSA.Expression>();
            foreach (var guard in tupleGuards) {
                prologue.Add(Ast.IfThen(guard, Ast.Return(exit, Ast.Constant(OsrLoopSite.Retry, typeof(object)))));
            }
            prologue.AddRange(tupleInit);

            // Nothing may have redefined Integer#+ (or anything else) since the types were
            // read: one global version covers every def, undef, alias and include.
            prologue.Add(Ast.IfThen(
                Ast.Not(Ast.Call(JitRuntime.M("VersionOk"), Ast.Constant(RubyModule.GlobalMethodVersion))),
                Ast.Return(exit, Ast.Call(JitRuntime.M("OsrGuardFailed"), Ast.Constant("method version")))
            ));

            var storeCurrent = new List<MSA.Expression>();
            var storeSnapshot = new List<MSA.Expression>();
            foreach (var slot in slots) {
                locals.Add(slot.Variable);
                locals.Add(slot.Snapshot);
                Type clr = slot.Variable.Type;
                MSA.Expression store = Ast.Assign(slot.Accessor, Ast.Convert(slot.Variable, typeof(object)));
                MSA.Expression storeSnap = Ast.Assign(slot.Accessor, Ast.Convert(slot.Snapshot, typeof(object)));
                if (slot.Assigned != null) {
                    locals.Add(slot.Assigned);
                    locals.Add(slot.AssignedSnapshot);
                    // Take the entry value when it is the type the loop wants - usually it is,
                    // because a previous run left it there - and otherwise start the slot out
                    // as unwritten rather than refuse the whole loop.
                    prologue.Add(Ast.Assign(probe, slot.Accessor));
                    prologue.Add(Ast.Assign(slot.Assigned, Ast.TypeIs(probe, clr)));
                    prologue.Add(Ast.IfThen(slot.Assigned, Ast.Assign(slot.Variable, Ast.Unbox(probe, clr))));
                    prologue.Add(Ast.Assign(slot.Snapshot, slot.Variable));
                    prologue.Add(Ast.Assign(slot.AssignedSnapshot, slot.Assigned));
                    store = Ast.IfThen(slot.Assigned, store);
                    storeSnap = Ast.IfThen(slot.AssignedSnapshot, storeSnap);
                } else {
                    prologue.Add(Ast.Assign(probe, slot.Accessor));
                    prologue.Add(Ast.IfThen(
                        Ast.Not(Ast.TypeIs(probe, clr)),
                        Ast.Return(exit, Ast.Call(JitRuntime.M("OsrGuardFailed"), Ast.Constant(slot.Variable.Name)))
                    ));
                    prologue.Add(Ast.Assign(slot.Variable, Ast.Unbox(probe, clr)));
                    prologue.Add(Ast.Assign(slot.Snapshot, slot.Variable));
                }
                storeCurrent.Add(store);
                storeSnapshot.Add(storeSnap);
            }

            // The loop runs, the locals go home, and the value is nil: a `while' has no other
            // value here, since a `break' with an argument makes the emitter give up.
            MSA.Expression run = Ast.Block(typeof(object),
                body, Ast.Block(storeCurrent.ToArray()), Ast.Constant(null, typeof(object))
            );

            if (canDeopt) {
                storeSnapshot.Add(Ast.Constant(OsrLoopSite.Retry, typeof(object)));
                run = Ast.TryCatch(run,
                    Ast.Catch(typeof(JitDeoptException), Ast.Block(typeof(object), storeSnapshot.ToArray())));
            }

            prologue.Add(run);

            var lambda = Ast.Lambda<Func<object[], object>>(
                Ast.Label(exit, Ast.Block(typeof(object), locals, prologue)),
                "osrjit$" + loop.Location.Start.Line,
                new[] { argsParameter }
            );

            try {
                return lambda.Compile();
            } catch (Exception) {
                return null;
            }
        }
    }
}
