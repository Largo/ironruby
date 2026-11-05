/* ****************************************************************************
 *
 * Iterators run inline: a compiled loop (or method) that calls a core iterator with a literal
 * block runs the iterator's loop itself, the block's body inside it.
 *
 *   a.each { |x| body }        #k = 0;  while #k < a.size;  x = a[#k];     #k += 1; body; end   (value a)
 *   n.times { |i| body }       #k = 0;  while #k < n;       i = #k;        #k += 1; body; end   (value n)
 *   a.upto(b) { |i| body }     #n = b - a; #k = 0; while #k <= #n; i = a + #k; #k += 1; body; end   (value a)
 *   a.downto(b) { |i| body }   #n = a - b; #k = 0; while #k <= #n; i = a - #k; #k += 1; body; end   (value a)
 *   (a..b).each { |i| body }   as upto, to b, or to b - 1 for a...b                               (value the Range)
 *
 * Only while the iterator is the library's own, public method and no refinement is active, and only
 * for an Array local loaded once (ArrayLocal) and Integers. The counter moves on before the body
 * runs, so a `next' (no value: the iterators ignore the block's value) is the loop's `continue'.
 *
 * What makes this sound is what makes BlockLoopSite's outlining sound (BlockLoopInfo.IsInlinable):
 * a block with at most one plain parameter, no break, redo, retry, return, yield or super, and
 * locals each assigned by a statement of the body before anything reads them. Those are fresh, nil,
 * at the top of every call of the block in Ruby; here they are typed CLR variables of the iteration,
 * which no iteration can read before it has assigned them, so the difference cannot be seen. They
 * are never slots of the outlined loop: they are not in its tuples and do not go home on the way
 * out, and a deopt in the block re-runs the enclosing iteration (or method) from its top, as a
 * deopt in a nested `while' does.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Osr;
using Range = IronRuby.Builtins.Range;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;

    internal sealed partial class JitCompiler {
        // the lexical depths of the blocks being run inline: their locals are CLR variables of the
        // iteration (see IsInlinedLocal), not slots and not method locals
        private HashSet<int> _inlineDepths;

        private bool IsInlinedLocal(LocalVariable/*!*/ local) {
            // every local of a callee being inlined is one of its frame's (JitCompiler.Inline.cs)
            return _inlineDepth > 0 || (_inlineDepths != null && _inlineDepths.Contains(local.DefinitionLexicalDepth));
        }

        /// <summary>`a.each { }', `n.times { }', `a.upto(b) { }', `a.downto(b) { }' with a literal block.</summary>
        internal static bool IsInlinedIterator(MethodCall/*!*/ node) {
            if (!(node.Block is BlockDefinition) || node.Target == null) { return false; }
            var args = node.Arguments;
            int argc = (args == null) ? 0 : args.Expressions.Length;
            for (int i = 0; i < argc; i++) {
                if (args.Expressions[i] is SplattedArgument) { return false; }
            }
            switch (node.MethodName) {
                case "each": case "times": return argc == 0;
                case "upto": case "downto": return argc == 1;
                default: return false;
            }
        }

        private MSA.Expression/*!*/ EmitInlinedIterator(MethodCall/*!*/ node, out JT type) {
            var block = (BlockDefinition)node.Block;
            LocalVariable parameter;
            if (!_refinements.IsEmpty) { throw Bail("iterator " + node.MethodName + " under refinements"); }
            if (!BlockLoopInfo.IsInlinable(block, out parameter)) { throw Bail("block of " + node.MethodName + " that cannot run inline"); }

            var counter = Ast.Variable(typeof(long), "#k");
            var count = Ast.Variable(typeof(long), "#n");
            var temps = new List<MSA.ParameterExpression> { counter, count };
            var setup = new List<MSA.Expression>();
            MSA.Expression test, element, value;
            JT elementType;

            var range = node.Target as RangeExpression;
            if (node.MethodName == "each" && range != null) {
                // (a..b).each, (a...b).each of Integers: the Range is made for the value only
                if (!IsPublicBuiltin(_context.GetClass(typeof(Range)), "each")) { throw Bail("Range#each is not the built-in one"); }
                JT bt, et;
                var b = Emit(range.Begin, out bt);
                var e = Emit(range.End, out et);
                b = NumericIVar(range.Begin, b, ref bt);
                e = NumericIVar(range.End, e, ref et);
                if (!IsInteger(bt) || !IsInteger(et)) { throw Bail("Range#each of " + bt + ".." + et); }
                bool ints = (bt == JT.Int && et == JT.Int);
                var first = Ast.Variable(typeof(long), "#first");
                var last = Ast.Variable(typeof(long), "#last");
                temps.Add(first);
                temps.Add(last);
                setup.Add(Ast.Assign(first, Coerce(b, bt, JT.Lng)));
                setup.Add(Ast.Assign(last, Coerce(e, et, JT.Lng)));
                // #n = last - first (- 1 when the end is excluded): the iterations less one, negative for none;
                // out of Int64 deopts
                _canDeopt = true;
                MSA.Expression span = Ast.Call(JitRuntime.M("SubLong"), last, first);
                if (range.IsExclusive) { span = Ast.Call(JitRuntime.M("SubLong"), span, Ast.Constant(1L)); }
                setup.Add(Ast.Assign(count, span));
                test = Ast.LessThanOrEqual(counter, count);
                MSA.Expression at = Ast.Add(first, counter);
                // between two Int32s every number is one
                element = ints ? (MSA.Expression)Ast.Convert(at, typeof(int)) : at;
                elementType = ints ? JT.Int : JT.Lng;
                value = Ast.Call(JitRuntime.M("MakeIntegerRange"), first, last, Ast.Constant(range.IsExclusive));
            } else if (node.MethodName == "each") {
                if (!IsPublicBuiltin(ArrayClass, "each")) { throw Bail("Array#each is not the built-in one"); }
                var loaded = ArrayLocal(node.Target);
                if (loaded == null) { throw Bail("each of anything but a load-once Array"); }
                var array = loaded.Value;
                // what the block's parameter holds, for the elements of its elements to be sampled (ElementKind)
                var source = (_peekLocal != null && node.Target is LocalVariable) ? PeekLocal((LocalVariable)node.Target) : null;
                if (parameter != null && source.HasValue && source.Value.Value is RubyArray) {
                    if (_inlineSources == null) { _inlineSources = new Dictionary<string, RubyArray>(); }
                    _inlineSources[Key(parameter)] = (RubyArray)source.Value.Value;
                }
                // Array#each looks at the size again for every element
                test = Ast.LessThan(counter, Ast.Convert(Ast.Property(array.Key, "Count"), typeof(long)));
                MSA.Expression read = Ast.Call(JitRuntime.M("ArrayGet"), array.Key, Ast.Convert(counter, typeof(int)));
                if (array.Value == JT.Int || array.Value == JT.Dbl) {
                    _canDeopt = true;
                    element = GuardElement(array, read);
                    elementType = array.Value;
                } else {
                    element = read;
                    elementType = JT.Obj;
                }
                value = Ast.Convert(array.Key, typeof(object));
            } else {
                // times, upto, downto of Integers
                JT rt;
                var receiver = Emit(node.Target, out rt);
                receiver = NumericIVar(node.Target, receiver, ref rt);
                if (!IsInteger(rt)) { throw Bail(node.MethodName + " of a " + rt); }
                RequireBuiltin(rt, node.MethodName);
                var self = Ast.Variable(ClrType(rt), "#self");
                temps.Add(self);
                setup.Add(Ast.Assign(self, receiver));
                value = Coerce(self, rt, JT.Obj);
                if (node.MethodName == "times") {
                    setup.Add(Ast.Assign(count, Coerce(self, rt, JT.Lng)));
                    test = Ast.LessThan(counter, count);
                    element = (rt == JT.Int) ? (MSA.Expression)Ast.Convert(counter, typeof(int)) : counter;
                    elementType = rt;
                } else {
                    JT lt;
                    var limitNode = node.Arguments.Expressions[0];
                    var limit = Emit(limitNode, out lt);
                    limit = NumericIVar(limitNode, limit, ref lt);
                    if (!IsInteger(lt)) { throw Bail(node.MethodName + " to a " + lt); }
                    bool up = (node.MethodName == "upto");
                    // the iterations less one; out of Int64 (a range of 2**63 numbers) deopts
                    _canDeopt = true;
                    var from = Coerce(self, rt, JT.Lng);
                    var to = Coerce(limit, lt, JT.Lng);
                    setup.Add(Ast.Assign(count, Ast.Call(JitRuntime.M("SubLong"), up ? to : from, up ? from : to)));
                    test = Ast.LessThanOrEqual(counter, count);
                    MSA.Expression at = up ? Ast.Add(from, counter) : Ast.Subtract(from, counter);
                    // between two Int32s every number is one
                    if (rt == JT.Int && lt == JT.Int) {
                        element = Ast.Convert(at, typeof(int));
                        elementType = JT.Int;
                    } else {
                        element = at;
                        elementType = JT.Lng;
                    }
                }
            }

            // the block's body, its locals declared in the iteration
            int depth = block.DefinedScopeDepth;
            if (_inlineDepths == null) { _inlineDepths = new HashSet<int>(); }
            if (!_inlineDepths.Add(depth)) { throw Bail("an inline block within itself"); }
            var exit = Ast.Label("#iterator-exit");
            var cont = Ast.Label("#iterator-continue");
            var savedBreak = _breakLabel;
            var savedContinue = _continueLabel;
            _breakLabel = exit;
            _continueLabel = cont;
            _inlinedBlocks++;
            bool wroteBefore = _hashWrite;
            _hashWrite = false;
            MSA.Expression body;
            List<MSA.ParameterExpression> declared;
            MSA.Expression bindParameter = null;
            int savedScopeIndex = _localScopeIndex;
            try {
                PushScope();
                _localScopeIndex = _vars.Count - 1;
                if (parameter != null) {
                    var p = Declare(Key(parameter), elementType);
                    bindParameter = Ast.Assign(p, element);
                }
                JT ignored;
                body = EmitStatements(block.Body, out ignored);
                declared = PopScope();
            } finally {
                _localScopeIndex = savedScopeIndex;
                _inlinedBlocks--;
                _breakLabel = savedBreak;
                _continueLabel = savedContinue;
                _inlineDepths.Remove(depth);
            }
            if (_hashWrite) { _loopWrites = true; }
            _hashWrite |= wroteBefore;

            // the interrupt check of every iteration, as a nested `while' has it (EmitWhile)
            MSA.Expression safePoint;
            if (_loop != null) {
                _canDeopt = true;
                safePoint = Ast.Call(JitRuntime.M("OsrSafePoint"));
            } else {
                safePoint = Ast.Call(JitRuntime.M("MethodSafePoint"), Ast.Constant(_safePointDeopts));
            }

            var iteration = new List<MSA.Expression>();
            iteration.Add(Ast.IfThen(Ast.Not(test), Ast.Break(exit)));
            iteration.Add(safePoint);
            if (bindParameter != null) {
                iteration.Add(bindParameter);
            } else if (node.MethodName == "each" && range == null) {
                // the element is read even when the block takes none: an element guard fails as it would
                iteration.Add(element);
            }
            iteration.Add(Ast.PreIncrementAssign(counter));
            iteration.Add(body);

            setup.Insert(0, Ast.Assign(counter, Ast.Constant(0L)));
            setup.Add(Ast.Loop(Ast.Block(declared, iteration), exit, cont));
            setup.Add(value);
            type = JT.Obj;
            return Ast.Block(typeof(object), temps, setup);
        }

        // inline blocks being emitted, for `next' (EmitNext)
        private int _inlinedBlocks;
    }
}
