/* ****************************************************************************
 *
 * -X:OSR for the loops the core iterators run: `n.times { }', `a.upto(b) { }',
 * `a.downto(b) { }', and Array's `each', `each_index', `map', `select', `any?', `all?' and `none?'.
 *
 * Those loops are C# loops in the library that yield to a block, so there is no `while' for
 * OsrLoopSite to replace, and every iteration pays for a block invocation and a dynamic call
 * site per operator in the body. TruffleRuby gets out of that by inlining the block into the
 * iterator's loop. This does the same thing in the shape OSR already knows how to specialize:
 * once a block has been yielded to often enough by one of these iterators, it is handed to
 * JitLoopCompiler as the `while' loop the iterator and the block make together:
 *
 *     n.times { |i| body }           while #i < #n;     i = #i;      body; #i += 1; end
 *     a.upto(b) { |i| body }         while #i <= #n;    i = #i;      body; #i += 1; end
 *     (a..b).each { |i| body }       the same, Range#each over Integers (a...b: #n is b - 1)
 *     a.downto(b) { |i| body }       while #i >= #n;    i = #i;      body; #i -= 1; end
 *     array.each { |x| body }        while #i < #a.size; x = #a[#i]; body; #i += 1; end
 *     array.each_index { |i| body }  while #i < #a.size; i = #i;     body; #i += 1; end
 *
 * and for map, select and any? the block's value - the last statement of the body - goes into
 * `#r[#i] = value', `if value; #r[#k] = #a[#i]; #k += 1; end' and `if value; #k = 1; break; end'
 * (`unless' for all?), #r a result Array the iterator lends the copy, cut back afterwards to
 * what the iterations it ran produced.
 *
 * The iterator runs the rest of its iterations in the typed copy. The counter lives in a
 * tuple of the block's own lexical depth, made for the run, next to the block's parameter; the
 * outer locals the body reads and writes are reached through the tuples of the scopes the block
 * was defined in, exactly the ones the block's compiled body reaches them through.
 *
 * The iterator is still the method that is running (its frame is in a backtrace, a redefinition
 * of Integer#times never gets here) and the copy only exists for bodies whose iterations cannot
 * be observed part way through, so handing the loop back is as invisible as it is for a `while':
 * a deopt restores the locals to the top of the iteration, the counter says which one that is,
 * and the iterator yields from there.
 *
 * What the screen below keeps out is what a `while' and a block do differently: the jumps
 * (`break' leaves the iterator with a value, `next' ends the iteration, `redo' restarts it, a
 * `return' leaves the method), and block-local variables, which are fresh - nil - on every
 * iteration, where a loop's locals would carry over. The block's single plain parameter is
 * assigned at the top of every iteration, and so is a local whose first mention is a statement
 * of the body that assigns it (`x = i * 2; s += x'); a block with any other local is not
 * outlined.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using O = System.Object;

namespace IronRuby.Runtime.Osr {
    public enum BlockLoopKind {
        Times = 0,
        UpTo = 1,
        DownTo = 2,
        Each = 3,
        Map = 4,
        Select = 5,
        // any?, none?: stops at the first element the block is truthy for; all?: at the first falsy one
        FindTruthy = 6,
        FindFalsy = 7,
        // Array#each_index: Each's loop, yielding the index rather than the element
        EachIndex = 8,
    }

    /// <summary>
    /// What a block literal needs to be outlined into an iterator's loop: one per block literal
    /// that passed the screen, on its BlockDispatcher, and the sites for each iterator it is given to.
    /// </summary>
    internal sealed class BlockLoopInfo {
        private const int KindCount = 9;

        internal readonly BlockDefinition/*!*/ Ast;
        internal readonly RubyContext/*!*/ Context;
        internal readonly int Depth;
        // the block's one parameter, or null if it has none
        internal readonly LocalVariable Parameter;
        // the block's other locals, each assigned by a statement of the body before anything reads it
        internal readonly LocalVariable/*!*/[]/*!*/ Locals;
        internal readonly string/*!*/ Name;
        internal readonly BlockLoopSite[]/*!*/ Sites = new BlockLoopSite[KindCount];
        // the shortest run of Times, UpTo, DownTo, Range#each, Array#each and #each_index worth entering the copy for
        // (BlockLoopSite.MinRun, BlockLoopSite.MinMediumRun, BlockLoopSite.MinLightRun)
        internal readonly int Floor;

        private BlockLoopInfo(BlockDefinition/*!*/ ast, RubyContext/*!*/ context, int depth, LocalVariable parameter,
            LocalVariable/*!*/[]/*!*/ locals, string/*!*/ name, int calls) {
            Ast = ast;
            Floor = (calls >= BlockLoopSite.HeavyBody) ? BlockLoopSite.MinRun :
                (calls >= BlockLoopSite.MediumBody) ? BlockLoopSite.MinMediumRun : BlockLoopSite.MinLightRun;
            Locals = locals;
            Context = context;
            Depth = depth;
            Parameter = parameter;
            Name = name;
        }

        /// <summary>
        /// Null when the block can never be outlined: anything but at most one plain parameter, or a
        /// body with a jump, a block-local variable it may read before it assigns it, or a block of its own.
        /// </summary>
        internal static BlockLoopInfo TryCreate(BlockDefinition/*!*/ ast, RubyContext/*!*/ context, int depth, string/*!*/ name) {
            if (depth <= 0) { return null; }

            var parameters = ast.Parameters;
            if (parameters.Optional.Length != 0 || parameters.Unsplat != null || parameters.Block != null ||
                parameters.SuperSplat != null || parameters.Mandatory.Length > 1) {
                return null;
            }
            var signature = parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords || signature.MaxPositionalCount != parameters.Mandatory.Length)) {
                return null;
            }

            LocalVariable parameter = null;
            if (parameters.Mandatory.Length == 1) {
                parameter = parameters.Mandatory[0] as LocalVariable;
                if (parameter == null || parameter.DefinitionLexicalDepth != depth) {
                    return null;
                }
            }

            List<LocalVariable> locals;
            int calls;
            if (ast.Body.Count == 0 || !Screen.Accepts(ast.Body, depth, parameter, out locals, out calls)) {
                return null;
            }
            return new BlockLoopInfo(ast, context, depth, parameter, locals.ToArray(), name, calls);
        }

        internal BlockLoopSite/*!*/ GetSite(BlockLoopKind kind) {
            var site = Sites[(int)kind];
            if (site == null) {
                Interlocked.CompareExchange(ref Sites[(int)kind], new BlockLoopSite(this, kind), null);
                site = Sites[(int)kind];
            }
            return site;
        }

        /// <summary>The block-level constructs a `while' would run differently.</summary>
        private sealed class Screen : Walker {
            private readonly int _depth;
            private readonly LocalVariable _parameter;
            private int _loops;
            private int _calls;
            private bool _rejected;

            private Screen(int depth, LocalVariable parameter) {
                _depth = depth;
                _parameter = parameter;
            }

            // The block's own locals other than its parameter: each the target of a statement of the
            // body's own, `x = v', before anything else in the body mentions it (see Enter(LocalVariable)).
            private readonly List<LocalVariable>/*!*/ _locals = new List<LocalVariable>();

            internal static bool Accepts(Statements/*!*/ body, int depth, LocalVariable parameter, out List<LocalVariable> locals, out int calls) {
                var screen = new Screen(depth, parameter);
                locals = screen._locals;
                calls = 0;
                foreach (var statement in body) {
                    var assignment = statement as SimpleAssignmentExpression;
                    var target = (assignment != null && assignment.Operation == null) ? assignment.Left as LocalVariable : null;
                    if (target != null && target.DefinitionLexicalDepth == depth && !screen.IsKnown(target)) {
                        // the value first, which may not read the local it is about to be stored in
                        assignment.Right.Walk(screen);
                        screen._locals.Add(target);
                    } else {
                        statement.Walk(screen);
                    }
                    if (screen._rejected) { return false; }
                }
                calls = screen._calls;
                return true;
            }

            private bool IsKnown(LocalVariable/*!*/ local) {
                if (_parameter != null && local.Name == _parameter.Name) { return true; }
                foreach (var known in _locals) {
                    if (known.Name == local.Name) { return true; }
                }
                return false;
            }

            private bool Reject() {
                _rejected = true;
                return false;
            }

            public override bool Enter(WhileLoopExpression/*!*/ node) { _loops++; return !_rejected; }
            public override void Exit(WhileLoopExpression/*!*/ node) { _loops--; }

            // In a `while' of the body's own these jump within the body; at the block's level they
            // leave the iteration or the iterator.
            public override bool Enter(BreakStatement/*!*/ node) { return _loops > 0 ? !_rejected : Reject(); }
            public override bool Enter(NextStatement/*!*/ node) { return _loops > 0 ? !_rejected : Reject(); }
            public override bool Enter(RedoStatement/*!*/ node) { return _loops > 0 ? !_rejected : Reject(); }
            public override bool Enter(RetryStatement/*!*/ node) { return Reject(); }
            public override bool Enter(ReturnStatement/*!*/ node) { return Reject(); }
            public override bool Enter(YieldCall/*!*/ node) { return Reject(); }
            public override bool Enter(SuperCall/*!*/ node) { return Reject(); }
            public override bool Enter(BlockDefinition/*!*/ node) { return Reject(); }
            public override bool Enter(BlockReference/*!*/ node) { return Reject(); }

            // The Walker does not go into the block of a call (`a.each { }', `m(&b)'), so the two above
            // never see one: catch it at the call. The loop emitter cannot take a call with a block either
            // (JitCompiler: "call with a block"), so without this a block like `n.times { a.each { } }' was
            // let through, and the first entry spent a whole OSR compile (~25ms) only to retire the site.
            public override bool Enter(MethodCall/*!*/ node) { _calls++; return node.Block != null ? Reject() : !_rejected; }

            public override bool Enter(LocalVariable/*!*/ node) {
                // A local of the block's own is nil at the top of every iteration, which a loop slot
                // carried over from the iteration before is not. The parameter is assigned at the top of
                // every iteration, and so is a local whose first mention is a statement that assigns it;
                // any other is out.
                if (node.DefinitionLexicalDepth < 0 || (node.DefinitionLexicalDepth >= _depth &&
                    (node.DefinitionLexicalDepth != _depth || !IsKnown(node)))) {
                    return Reject();
                }
                return !_rejected;
            }
        }
    }

    /// <summary>
    /// One block literal given to one kind of iterator: the synthesized loop, and the OsrLoopSite
    /// that counts, compiles, deopts and retires it exactly as it would a `while'.
    /// </summary>
    public sealed class BlockLoopSite {
        /// <summary>
        /// Iterations an entry must have left for the copy to be worth entering (Times, UpTo, DownTo,
        /// Range#each, Array#each), for a block of HeavyBody calls or more: NBody's float arithmetic over
        /// attribute reads, whose inner `((i + 1)...n).each' runs 4, 3, 2, 1. An entry reuses the site's
        /// argument array and tuple (Run), so it allocates nothing, but it still costs about what three or
        /// four yields of a light block do (the copy's guards and its Array check, the tuple, the scope
        /// chain); a heavy body saves more than that in its first iteration.
        /// </summary>
        public const int MinRun = 1;

        /// <summary>
        /// The same for a block of MediumBody calls or more: `s += x * 3 + x * 5 - x / 2 + (x &amp; 7)' over one
        /// element took 140-160 ns in the copy against 96-107 through the yield, over two the same, over
        /// four 182 against 300.
        /// </summary>
        public const int MinMediumRun = 2;

        /// <summary>The same for a lighter block (`s += x'): measured, five is the shortest run no slower in the copy.</summary>
        public const int MinLightRun = 5;

        /// <summary>
        /// Method calls (operators and attribute reads included) that make a block body heavy: an entry
        /// costs about 45 ns more than a yield, and a call the copy types saves 4 or 5.
        /// </summary>
        internal const int HeavyBody = 12;

        /// <summary>Method calls that make a block body worth entering for two iterations.</summary>
        internal const int MediumBody = 6;

        /// <summary>
        /// The same for Array#map, #select, #any?, #all? and #none?, whose entry also grows and trims
        /// the result (map, select) or ends a search early: below sixteen they measured slower.
        /// </summary>
        public const int MinCollectRun = 16;

        private readonly BlockLoopInfo/*!*/ _info;
        private readonly BlockLoopKind _kind;
        private readonly OsrLoopSite/*!*/ _osr;

        // the tuple of the block's own depth: its locals, then the counter, the limit (or the Array),
        // the result Array of a map or select, and how many elements a select has kept
        private readonly int _tupleSize, _counterIndex, _limitIndex, _resultIndex, _keptIndex;
        private readonly Type/*!*/ _tupleType;

        internal BlockLoopSite(BlockLoopInfo/*!*/ info, BlockLoopKind kind) {
            _info = info;
            _kind = kind;
            _osr = new OsrLoopSite(info.Name + " (" + kind.ToString().ToLowerInvariant() + ")");

            // the block's locals keep the slots its body gives them, the counter and the limit go after
            int first = (info.Parameter != null) ? info.Parameter.ClosureIndex + 1 : 0;
            bool compiled = info.Parameter == null || info.Parameter.ClosureIndex >= 0;
            foreach (var local in info.Locals) {
                first = Math.Max(first, local.ClosureIndex + 1);
                compiled &= local.ClosureIndex >= 0;
            }
            _counterIndex = first;
            _limitIndex = first + 1;
            _resultIndex = first + 2;
            _keptIndex = first + 3;
            // Every slot an object, to a power of two, rather than padded with DynamicNull: the tuple is
            // then a MutableTuple<object, ...> whose slots Clear, Store and Load reach directly.
            int size = 4;
            while (size < first + 4) { size *= 2; }
            _tupleSize = size;
            var types = new Type[_tupleSize];
            for (int i = 0; i < types.Length; i++) { types[i] = typeof(object); }
            _tupleType = MutableTuple.MakeTupleType(types);

            if (!compiled) {
                // the block has not been compiled the way this expects: never enter
                Retire();
                return;
            }

            int depth = info.Depth;
            var tupleArgIndex = new Dictionary<int, int>();
            for (int d = depth; d >= 0; d--) {
                tupleArgIndex.Add(d, depth - d);
            }
            _osr.LoopAst = MakeLoop(info, kind, depth, _counterIndex, _limitIndex, _resultIndex, _keptIndex);
            _osr.Context = info.Context;
            _osr.LongRuns = true;
            _osr.ScopeDepth = depth;
            _osr.TupleArgIndex = tupleArgIndex;
            _osr.SelfArgIndex = depth + 1;
            _osr.ScopeArgIndex = depth + 2;
        }

        private void Retire() {
            _osr.Counting = false;
            _osr.Countdown = Int64.MaxValue;
        }

        private static LocalVariable/*!*/ Hidden(string/*!*/ name, int depth, int index, SourceSpan location) {
            var local = new LocalVariable(name, location, depth);
            local.SetClosureIndex(index);
            return local;
        }

        private static WhileLoopExpression/*!*/ MakeLoop(BlockLoopInfo/*!*/ info, BlockLoopKind kind, int depth,
            int counterIndex, int limitIndex, int resultIndex, int keptIndex) {
            SourceSpan location = info.Ast.Location;
            var counter = Hidden("#i", depth, counterIndex, location);
            var limit = Hidden("#n", depth, limitIndex, location);
            var result = Hidden("#r", depth, resultIndex, location);
            var kept = Hidden("#k", depth, keptIndex, location);

            Expression condition;
            Expression element;
            switch (kind) {
                case BlockLoopKind.Times:
                    condition = new MethodCall(counter, "<", new Arguments(limit), location);
                    element = counter;
                    break;
                case BlockLoopKind.UpTo:
                    condition = new MethodCall(counter, "<=", new Arguments(limit), location);
                    element = counter;
                    break;
                case BlockLoopKind.DownTo:
                    condition = new MethodCall(counter, ">=", new Arguments(limit), location);
                    element = counter;
                    break;
                case BlockLoopKind.Each:
                case BlockLoopKind.Map:
                case BlockLoopKind.Select:
                case BlockLoopKind.FindTruthy:
                case BlockLoopKind.FindFalsy:
                    condition = new MethodCall(counter, "<", new Arguments(new MethodCall(limit, "size", null, location)), location);
                    element = new MethodCall(limit, "[]", new Arguments(counter), location);
                    break;
                case BlockLoopKind.EachIndex:
                    // the length read afresh every step, as rb_ary_each_index does
                    condition = new MethodCall(counter, "<", new Arguments(new MethodCall(limit, "size", null, location)), location);
                    element = counter;
                    break;
                default:
                    throw new ArgumentException("kind");
            }

            var statements = new Statements();
            if (info.Parameter != null) {
                statements.Add(new SimpleAssignmentExpression(info.Parameter, element, null, location));
            }
            var body = info.Ast.Body;
            int last = body.Count - 1;
            for (int i = 0; i < last; i++) {
                statements.Add(body[i]);
            }
            switch (kind) {
                case BlockLoopKind.Map:
                    // #r[#i] = <the block's value>, #r the result, which Run has made as long as the receiver
                    statements.Add(new SimpleAssignmentExpression(
                        new ArrayItemAccess(result, new Arguments(counter), null, location), body[last], null, location));
                    break;
                case BlockLoopKind.Select:
                    // if <the block's value>; #r[#k] = #a[#i]; #k += 1; end - the element as it is once
                    // the block has run, as Select's own loop adds it
                    var keep = new Statements();
                    keep.Add(new SimpleAssignmentExpression(new ArrayItemAccess(result, new Arguments(kept), null, location),
                        new MethodCall(limit, "[]", new Arguments(counter), location), null, location));
                    keep.Add(new SimpleAssignmentExpression(kept, Literal.Integer(1, location), "+", location));
                    statements.Add(new IfExpression(body[last], keep, new List<ElseIfClause>(), location));
                    break;
                case BlockLoopKind.FindTruthy:
                case BlockLoopKind.FindFalsy:
                    // if <the block's value>; #k = 1; break; end - the counter stays at the element found
                    var found = new Statements();
                    found.Add(new SimpleAssignmentExpression(kept, Literal.Integer(1, location), null, location));
                    found.Add(new BreakStatement(null, location));
                    statements.Add(kind == BlockLoopKind.FindTruthy
                        ? (Expression)new IfExpression(body[last], found, new List<ElseIfClause>(), location)
                        : new UnlessExpression(body[last], found, null, location));
                    break;
                default:
                    statements.Add(body[last]);
                    break;
            }
            statements.Add(new SimpleAssignmentExpression(counter, Literal.Integer(1, location),
                kind == BlockLoopKind.DownTo ? "-" : "+", location));

            return new WhileLoopExpression(condition, true, false, statements, location);
        }

        /// <summary>
        /// The site for this block given to an iterator of this kind, or null when the block can
        /// never be outlined. The iterator reads Countdown into a local on entry, counts its yields
        /// down with it, and calls Run when it reaches zero.
        /// </summary>
        public static BlockLoopSite For(BlockParam/*!*/ block, BlockLoopKind kind) {
            var proc = block.Proc;
            var info = proc.Dispatcher.LoopInfo;
            if (info == null || proc.Kind == ProcKind.Lambda) { return null; }
            return info.GetSite(kind);
        }

        /// <summary>
        /// For, for an iterator of Times, UpTo, DownTo or Each whose run is <paramref name="run"/> long:
        /// null too when the run is shorter than the block's floor (MinRun, MinLightRun), which then
        /// never asks for the site. The iterator enters the copy only with Floor iterations or more left.
        /// </summary>
        public static BlockLoopSite For(BlockParam/*!*/ block, BlockLoopKind kind, long run) {
            var proc = block.Proc;
            var info = proc.Dispatcher.LoopInfo;
            if (info == null || proc.Kind == ProcKind.Lambda || run < info.Floor) { return null; }
            return info.GetSite(kind);
        }

        /// <summary>The shortest run left this site's iterator enters the copy for (For).</summary>
        public int Floor {
            get { return _info.Floor; }
        }

        /// <summary>Yields left before the next entry asks for the copy.</summary>
        public long Countdown {
            get { return _osr.Countdown; }
        }

        /// <summary>
        /// On the way out: what this entry did not spend, so that entries that leave early (a break, or a
        /// search that stops) still add up. A loop shorter than the block's Floor never gets here: its iterator does
        /// not ask for the site at all, which keeps a hot short loop (`a.each { }' over a handful of
        /// elements, nested in a long one) the code it was before.
        /// </summary>
        public void Leave(long countdown) {
            if (_osr.Counting && countdown < _osr.Countdown) {
                _osr.Countdown = countdown;
            }
        }

        /// <summary>
        /// Runs the counted iterations from <paramref name="i"/> on in the typed copy (Times, UpTo,
        /// DownTo) and answers the counter the iterator resumes at: the end of the range when the copy
        /// ran it all, the iteration it handed back otherwise, untouched if there was no copy to run.
        /// </summary>
        public int Run(BlockParam/*!*/ block, int i, int limit, ref long countdown) {
            Debug.Assert(_kind <= BlockLoopKind.DownTo);
            int kept;
            Run(block, i, ScriptingRuntimeHelpers.Int32ToObject(limit), null, 0, out i, out kept);
            countdown = _osr.Countdown;
            return i;
        }

        /// <summary>Array#each's and #each_index's: runs from index <paramref name="i"/> on, answers the index to resume at.</summary>
        public int Run(BlockParam/*!*/ block, RubyArray/*!*/ array, int i, ref long countdown) {
            Debug.Assert(_kind == BlockLoopKind.Each || _kind == BlockLoopKind.EachIndex);
            int kept;
            Run(block, i, array, null, 0, out i, out kept);
            countdown = _osr.Countdown;
            return i;
        }

        /// <summary>
        /// Array#map's and #select's: runs from index <paramref name="i"/> on, the block's values going
        /// into <paramref name="result"/> (map: one per element, select: the elements kept), and answers
        /// the index to resume at, with <paramref name="result"/> holding exactly what the iterations
        /// before it produced.
        /// </summary>
        public int Run(BlockParam/*!*/ block, RubyArray/*!*/ array, RubyArray/*!*/ result, int i, ref long countdown) {
            Debug.Assert(_kind == BlockLoopKind.Map || _kind == BlockLoopKind.Select);
            // The copy stores into the result rather than appends to it, so it gets room for every
            // element there is, which no Ruby code sees before it is cut back to what was produced.
            int kept = result.Count;
            int room = array.Count - kept;
            if (room > 0) { result.AddMultiple(room, null); }
            Run(block, i, array, result, kept, out i, out kept);
            if (_kind == BlockLoopKind.Map) { kept = i; }
            if (result.Count > kept) { result.RemoveRange(kept, result.Count - kept); }
            countdown = _osr.Countdown;
            return i;
        }

        /// <summary>
        /// Array#any?, #all? and #none?'s: runs from index <paramref name="i"/> on and answers the index
        /// to resume at, or the index of the element the search stops at, which <paramref name="found"/>
        /// then says it is.
        /// </summary>
        public int Run(BlockParam/*!*/ block, RubyArray/*!*/ array, int i, ref long countdown, out bool found) {
            Debug.Assert(_kind == BlockLoopKind.FindTruthy || _kind == BlockLoopKind.FindFalsy);
            int kept;
            Run(block, i, array, null, 0, out i, out kept);
            found = (kept == 1);
            countdown = _osr.Countdown;
            return i;
        }

        private void Run(BlockParam/*!*/ block, int from, object limit, RubyArray result, int keptBefore, out int resume, out int kept) {
            var proc = block.Proc;
            RubyScope scope = proc.LocalScope;
            int depth = _info.Depth;

            // The argument array and the block's tuple are kept for the next entry, so that a short run
            // does not pay two allocations to get in. Ruby threads run in parallel and a run can re-enter
            // its own site (a recursive method whose block loop is compiled), so an entry takes the pair
            // for itself and allocates its own when another entry has it.
            object[] args = Interlocked.Exchange(ref _spare, null);
            MutableTuple own;
            if (args != null) {
                own = (MutableTuple)args[0];
            } else {
                own = MutableTuple.MakeTuple(_tupleType, new object[_tupleSize]);
                args = new object[depth + 3];
                args[0] = own;
            }
            try {
                RunIn(args, own, block, scope, depth, from, limit, result, keptBefore, out resume, out kept);
            } finally {
                // Nothing a run held stays reachable from the site: the block's locals, the receiver,
                // the result, self and the scopes. A copy that stopped part way (an exception, a throw)
                // leaves its locals in the tuple too, which the next entry must not start from.
                Clear(own, _tupleSize);
                Array.Clear(args, 1, args.Length - 1);
                Volatile.Write(ref _spare, args);
            }
        }

        // The block's tuple holds objects only, so up to sixteen slots it is a MutableTuple<object, ...> of
        // 4, 8 or 16, each deriving from the one half its size. Its slots are then properties this code
        // can set and clear directly: MutableTuple.SetValue is a virtual call, a switch and a cast per
        // slot, which was most of what an entry cost once it allocated nothing.
        private static void Clear(MutableTuple/*!*/ tuple, int size) {
            if (size <= 16 && tuple is MutableTuple<O, O, O, O> t4) {
                t4.Item000 = null; t4.Item001 = null; t4.Item002 = null; t4.Item003 = null;
                if (tuple is MutableTuple<O, O, O, O, O, O, O, O> t8) {
                    t8.Item004 = null; t8.Item005 = null; t8.Item006 = null; t8.Item007 = null;
                    if (tuple is MutableTuple<O, O, O, O, O, O, O, O, O, O, O, O, O, O, O, O> t16) {
                        t16.Item008 = null; t16.Item009 = null; t16.Item010 = null; t16.Item011 = null;
                        t16.Item012 = null; t16.Item013 = null; t16.Item014 = null; t16.Item015 = null;
                    }
                }
                return;
            }
            for (int i = 0; i < size; i++) {
                tuple.SetNestedValue(size, i, null);
            }
        }

        private static void Store(MutableTuple/*!*/ tuple, int size, int index, object value) {
            if (size <= 16) {
                switch (index) {
                    case 0: ((MutableTuple<O, O, O, O>)tuple).Item000 = value; return;
                    case 1: ((MutableTuple<O, O, O, O>)tuple).Item001 = value; return;
                    case 2: ((MutableTuple<O, O, O, O>)tuple).Item002 = value; return;
                    case 3: ((MutableTuple<O, O, O, O>)tuple).Item003 = value; return;
                    case 4: ((MutableTuple<O, O, O, O, O, O, O, O>)tuple).Item004 = value; return;
                    case 5: ((MutableTuple<O, O, O, O, O, O, O, O>)tuple).Item005 = value; return;
                    case 6: ((MutableTuple<O, O, O, O, O, O, O, O>)tuple).Item006 = value; return;
                    case 7: ((MutableTuple<O, O, O, O, O, O, O, O>)tuple).Item007 = value; return;
                }
            }
            tuple.SetNestedValue(size, index, value);
        }

        private static object Load(MutableTuple/*!*/ tuple, int size, int index) {
            if (size <= 16) {
                switch (index) {
                    case 0: return ((MutableTuple<O, O, O, O>)tuple).Item000;
                    case 1: return ((MutableTuple<O, O, O, O>)tuple).Item001;
                    case 2: return ((MutableTuple<O, O, O, O>)tuple).Item002;
                    case 3: return ((MutableTuple<O, O, O, O>)tuple).Item003;
                    case 4: return ((MutableTuple<O, O, O, O, O, O, O, O>)tuple).Item004;
                    case 5: return ((MutableTuple<O, O, O, O, O, O, O, O>)tuple).Item005;
                    case 6: return ((MutableTuple<O, O, O, O, O, O, O, O>)tuple).Item006;
                    case 7: return ((MutableTuple<O, O, O, O, O, O, O, O>)tuple).Item007;
                }
            }
            return tuple.GetNestedValue(size, index);
        }

        // An argument array whose first element is the block's tuple, both cleared, for the next entry to take.
        private object[] _spare;

        private void RunIn(object[]/*!*/ args, MutableTuple/*!*/ own, BlockParam/*!*/ block, RubyScope scope, int depth,
            int from, object limit, RubyArray result, int keptBefore, out int resume, out int kept) {
            Store(own, _tupleSize, _counterIndex, ScriptingRuntimeHelpers.Int32ToObject(from));
            Store(own, _tupleSize, _limitIndex, limit);
            Store(own, _tupleSize, _resultIndex, result);
            Store(own, _tupleSize, _keptIndex, ScriptingRuntimeHelpers.Int32ToObject(keptBefore));

            // the tuples by lexical depth, the block's own first, then the scopes it was defined in,
            // the same chain its compiled body walks with GetParentScope
            RubyScope s = scope;
            for (int k = 1; k <= depth && s != null; k++) {
                args[k] = s.Locals;
                s = s.Parent;
            }
            args[depth + 1] = block.Self;
            args[depth + 2] = scope;

            // Whatever the copy did - ran to the end, handed the loop back, never ran - the counter in
            // the tuple is the iteration to go on from, and the locals are as of the top of it.
            _osr.Run(args);
            object next = Load(own, _tupleSize, _counterIndex);
            resume = (next is int) ? (int)next : from;
            object keptAfter = Load(own, _tupleSize, _keptIndex);
            kept = (keptAfter is int) ? (int)keptAfter : keptBefore;
        }
    }
}
