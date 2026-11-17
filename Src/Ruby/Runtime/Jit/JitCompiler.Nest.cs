/* ****************************************************************************
 *
 * -X:OSR: an iterator with a literal block run in place inside an outlined block loop.
 *
 * BlockLoopSite outlines a block given to a core iterator into the `while' the two make together.
 * A statement of that block may itself be such an iterator with a literal block
 * (Osr.NestedBlockLoop: `(a...b).each { |j| }', `n.times { |j| }', `a.upto(b) { |j| }',
 * `a.downto(b) { |j| }'), which the screen there has passed. This emits its loop where the call is:
 * the bounds evaluated once, in source order, as the call's receiver and arguments are; the inner
 * block's parameter and locals CLR locals of the copy, declared in a scope of their own; and its
 * body emitted by the same emitter, so that a Float the inner block computes and the outer block
 * reads is never boxed and there is no Proc, no frame and no yield in between. TruffleRuby gets
 * the same from inlining the block into the iterator and the iterator into the caller.
 *
 * What makes this as invisible as the outer loop is that a deopt anywhere in it restarts the outer
 * iteration: the outer slots go back to their snapshot, the attribute and element writes since then
 * are taken back (the undo log is committed only at the top of an outer iteration), and the generic
 * code runs the whole outer iteration again, the inner call included. So the copy relies on nothing
 * it does not check first: bounds that are not Integers deopt before the inner loop starts (and the
 * generic call does what it does with them, a Float bound included), and an iterator that is not the
 * library's is not run in place at all (the global method version guard keeps that true for the
 * copy's life). The inner loop's safe point deopts like any other in the copy.
 *
 * The block-local variables of the inner block are fresh on each of its iterations because the
 * screen admits only locals whose first mention is a statement assigning them.
 *
 * ***************************************************************************/

using System;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Osr;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;

    internal sealed partial class JitCompiler {
        // The deepest lexical depth whose locals are CLR locals of an iterator run in place: locals deeper than
        // the outlined block's own (_scopeDepth) and no deeper than this are declared, not bound to tuple slots.
        private int _nestDepth;

        // Whether an iterator has been run in place in this body: its safe point deopts in the middle of an outer
        // iteration, which a global store earlier in that iteration does not survive (see EmitNestedBlockLoop).
        private bool _nestedLoops;

        /// <summary>A global store in a body that runs an iterator in place bails (see EmitNestedBlockLoop).</summary>
        private void CheckGlobalStoreBesideNest(string/*!*/ what) {
            if (_nestedLoops) { throw Bail(what + " beside an iterator run in place"); }
        }

        /// <summary>Whether an unbound local is one of an inner block run in place, which a first assignment declares.</summary>
        private bool IsNestedLocal(LocalVariable/*!*/ local) {
            return _loop != null && local.DefinitionLexicalDepth > _scopeDepth && local.DefinitionLexicalDepth <= _nestDepth;
        }

        /// <summary>
        /// An iterator call with a literal block, in an outlined core-iterator loop (BlockLoopSite): its loop,
        /// emitted in place (see the top of this file). The call's value is never used: the screen admits it only
        /// as a statement whose value is dropped.
        /// </summary>
        private MSA.Expression/*!*/ EmitNestedBlockLoop(MethodCall/*!*/ node, out JT type) {
            int depth = Math.Max(_scopeDepth, _nestDepth) + 1;
            var nested = NestedBlockLoop.TryCreate(node, depth);
            if (nested == null) { throw Bail("call with a block: " + node.MethodName); }

            // the iterator the call reaches must be the library's, which runs its loop over Integers without
            // calling anything (Range#each over two Integers, Integer#times, #upto, #downto)
            RubyClass cls = (nested.Iterator == "each")
                ? _context.GetClass(typeof(IronRuby.Builtins.Range))
                : ClassOfType(JT.Int, false);
            if (!IsPublicBuiltin(cls, nested.Iterator)) { throw Bail(nested.Iterator + " is not the built-in one"); }

            // A Thread#raise (or a trap) stops the inner loop at its safe point by a deopt, which takes the outer
            // iteration back to its top and lets the generic code deliver the exception there - before it gets to
            // store a global again. A global stored earlier in the iteration would then be left ahead of the
            // locals it was stored from, which go back to the snapshot: such a body is not taken (a global stored
            // after this point bails in EmitGlobalWrite).
            _nestedLoops = true;
            if (_globalsWritten != null && _globalsWritten.Count > 0) { throw Bail("global variable stored beside an iterator run in place"); }

            // The receiver, then the argument: `(a...b)' evaluates a, then b; `a.upto(b)' a, then b.
            var from = Ast.Variable(typeof(long), "#from");
            var to = Ast.Variable(typeof(long), "#to");
            var init = new System.Collections.Generic.List<MSA.Expression>();
            // The generic code looks the iterator up each time the outer iteration reaches the call: one redefined
            // since the outer iteration's safe point (by another thread) must be seen here, before the iterator
            // runs at all - an empty range included. The loop's own safe point, once before it, then on each turn.
            var epoch = NewLoopEpoch();
            init.Add(StartLoopEpoch(epoch));
            init.Add(EmitLoopSafePoint(epoch));
            if (nested.From != null) {
                init.Add(Ast.Assign(from, EmitIntegerBound(nested.From)));
            } else {
                init.Add(Ast.Assign(from, Ast.Constant(0L)));
            }
            init.Add(Ast.Assign(to, EmitIntegerBound(nested.To)));
            if (nested.Exclusive) {
                init.Add(Ast.Assign(to, Ast.Subtract(to, Ast.Constant(1L))));
            }

            int savedNest = _nestDepth;
            var savedBreak = _breakLabel;
            var savedContinue = _continueLabel;
            _nestDepth = depth;
            // A jump in the inner block is not one of the outer loop's: the screen admits none, and one that got
            // here anyway bails rather than leave the wrong loop.
            _breakLabel = null;
            _continueLabel = null;
            PushScope();
            MSA.Expression body;
            MSA.ParameterExpression parameter = null;
            try {
                if (nested.Parameter != null) {
                    parameter = Declare(Key(nested.Parameter), JT.Int);
                }
                JT ignored;
                body = EmitStatements(nested.Block.Body, out ignored);
            } finally {
                _nestDepth = savedNest;
                _breakLabel = savedBreak;
                _continueLabel = savedContinue;
            }
            var declared = PopScope();

            // a deopt here restarts the outer iteration, which is pure until its writes are taken back
            _canDeopt = true;
            _hasLoop = true;

            var exit = Ast.Label("#nested-exit");
            var test = (nested.Step > 0) ? Ast.LessThanOrEqual(from, to) : Ast.GreaterThanOrEqual(from, to);
            var iteration = new System.Collections.Generic.List<MSA.Expression>();
            iteration.Add(Ast.IfThen(Ast.Not(test), Ast.Break(exit)));
            iteration.Add(EmitLoopSafePoint(epoch));
            if (parameter != null) {
                // from is within [from, to] of two Int32 bounds here
                iteration.Add(Ast.Assign(parameter, Ast.Convert(from, typeof(int))));
            }
            iteration.Add(body);
            iteration.Add(Ast.Assign(from, Ast.Add(from, Ast.Constant((long)nested.Step))));

            var variables = new System.Collections.Generic.List<MSA.ParameterExpression>(declared);
            variables.Add(from);
            variables.Add(to);
            if (epoch != null) { variables.Add(epoch); }
            init.Add(Ast.Loop(Ast.Block(iteration), exit));
            init.Add(Ast.Constant(null, typeof(object)));
            type = JT.Obj;
            return Ast.Block(typeof(object), variables, init);
        }

        /// <summary>
        /// A bound of an iterator run in place, as a long: an Integer that fits in an Int32, which an object
        /// is checked to be (a deopt otherwise, a Float or a Bignum bound left to the generic call).
        /// </summary>
        private MSA.Expression/*!*/ EmitIntegerBound(RExpr/*!*/ bound) {
            JT t;
            var e = Emit(bound, out t);
            switch (t) {
                case JT.Int:
                    return Ast.Convert(e, typeof(long));
                case JT.Lng:
                    // `i + 1': an Integer that is past an Int32 deopts, and the generic call takes it
                    _canDeopt = true;
                    return Ast.Convert(Ast.Call(JitRuntime.M("DemoteToInt"), e), typeof(long));
                case JT.Obj:
                    _canDeopt = true;
                    return Ast.Convert(Ast.Call(JitRuntime.M("GuardInt"), e), typeof(long));
                default:
                    throw Bail("an iterator bound of type " + t);
            }
        }
    }
}
