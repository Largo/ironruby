/* ****************************************************************************
 *
 * Self's instance variable writes held back to the end of the body ("sunk stores").
 *
 * A typed body may deopt only while nothing it did is observable (_deoptAfterEffect): the deopt
 * restarts the generic body from the top. Json's `Parser#read' writes @line, @column and @index and
 * then reads @input.length and @input[@index], which deopt when @input is not a plain String, so it
 * was declined. Here the writes to self's instance variables are kept in locals of the body (one
 * value and one `written' flag per variable) and stored all at once where the body ends: every
 * deopt of the body then comes before every store, and a restart finds nothing done.
 *
 * Why that is the same as storing them where the source does:
 *   - In this thread nothing can see self's variables between the write and the end of the body:
 *     the body is held to operations that run no Ruby code and read no instance variable but
 *     self's own through GetIVar (an allow-list of the helpers it calls, IsSinkSafe, checked on the
 *     emitted tree; anything else compiles the body without sinking). A read of a variable the body
 *     has written is answered from the local - the value the store would have left there.
 *   - Another thread (there is no GVL) may see the stores later than the source makes them, after
 *     the body's later reads of other variables. That is a store moved past loads of other
 *     locations, which the CLR's memory model (and x86's) already lets the generic body's own
 *     plain field stores do: no other thread can tell the two apart by anything the generic body
 *     guarantees. A variable the body did not write is never stored, so no other thread's store
 *     is overwritten by a value this one read earlier.
 *   - A frozen self: the first store's frozen check deopts, before any store, and the generic body
 *     raises the FrozenError from the write the source makes, after whatever that write evaluated.
 *   - No exception but a deopt can leave such a body: its helpers throw nothing else, and it has no
 *     safe point (no call, no loop) for a Thread#raise to land at.
 * A variable written both as an Int32 and as an Int64 (Bounce's `@x += @x_vel' and then `@x = x_limit') is held in
 * one long and stored through NarrowLong, the box the generic body stores (TrySinkIVarWrite, a round more).
 * Not with a loop, a self-call, an explicit return, fallbacks, or any effect other than these
 * writes (_pure at the end of the body): those are what IsSinkSafe's retry leaves to the old rule.
 * Not tried at all for a body with a `while' or with keyword or optional parameters (TryCompile).
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Reflection;
using IronRuby.Compiler.Ast;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    internal sealed partial class JitCompiler {
        private sealed class SunkIVar {
            internal string Name;
            internal JT Type;
            internal MSA.ParameterExpression Value, Written;
            // the run-time position of its first write among the held variables (0: not written)
            internal MSA.ParameterExpression Order;
            // the first and last of the body's held writes (numbered as emitted, which is the order they run in) that are
            // of this variable
            internal int FirstSite, LastSite;
        }

        // Sinking is tried for this body (TryCompile turns it off for a retry); the variables held back,
        // in the order first written; those written by a top-level statement (so written on every path
        // that reaches a later read); and why the body cannot keep them, if it cannot.
        private bool _sinkStores;
        private List<SunkIVar> _sunk;
        private HashSet<string> _sunkDefinite;
        private string _sinkObstacle;
        // `if (#order-x == 0) #order-x = ++#order' at each held write: the order the variables were first written
        // in, which the stores keep when it may differ from the source's (FinishSunkStores; removed where it may not)
        private MSA.ParameterExpression _sinkCounter;
        private HashSet<MSA.Expression> _sinkOrderings;
        private int _sinkSites;
        // Variables held as a long because the body writes them both as an Int and as a Lng (TryCompile carries the
        // set from round to round), and those whose two integer types this round met: they ask for a round that
        // holds them so. In practice that is one round per variable, not one in all: the first conflicting write
        // is stored where it stands, the body is no longer pure, and every later write leaves TrySinkIVarWrite
        // before its own conflict is looked at (Ball#bounce: a round for @x, then one for @y). The rounds end,
        // since each adds a name; a body that can never hold its writes may still spend one on a name first.
        internal HashSet<string> _sinkWide;
        internal List<string> _sinkWiden;

        private SunkIVar FindSunk(string/*!*/ name) {
            if (_sunk != null) {
                foreach (var s in _sunk) { if (s.Name == name) { return s; } }
            }
            return null;
        }

        private bool IsTopStatement(RExpr/*!*/ node) {
            if (_ast == null || _ast.Body.Statements == null) { return false; }
            foreach (var s in _ast.Body.Statements) { if (s == node) { return true; } }
            return false;
        }

        /// <summary>`@x = v' held back (see the top of this file), or null for the ordinary store.</summary>
        private MSA.Expression TrySinkIVarWrite(SimpleAssignmentExpression/*!*/ node, string/*!*/ name, MSA.Expression/*!*/ v, JT vt, bool conditional, out JT type) {
            type = vt;
            if (!_sinkStores || _loop != null || vt == JT.None || vt == JT.Never) { return null; }
            if (!_pure || _effectNotOnSelf) {
                // something observable came first: the order of the two is the source's only if stored here
                if (_sunk != null && _sinkObstacle == null) { _sinkObstacle = "an effect before a held-back write"; }
                return null;
            }
            // An Integer written as an Int32 and as an Int64 is one Ruby class in two representations: held in one long,
            // and stored through NarrowLong, which boxes a value that fits an Int32 as the Int32 the generic body stores.
            // A Float and an Integer, or a number and an object, are not widened: a Float is stored raw (SetIVarDouble)
            // and an Integer boxed, and one typed local cannot say which of the two classes the variable holds.
            if (vt == JT.Int && _sinkWide != null && _sinkWide.Contains(name)) {
                v = Coerce(v, JT.Int, JT.Lng);
                vt = JT.Lng;
                type = JT.Lng;
            }
            var sunk = FindSunk(name);
            if (sunk == null) {
                sunk = new SunkIVar {
                    Name = name, Type = vt,
                    Value = Ast.Variable(ClrType(vt), "#sunk" + name),
                    Written = Ast.Variable(typeof(bool), "#written" + name),
                    Order = Ast.Variable(typeof(int), "#order" + name),
                };
                if (_sunk == null) {
                    _sunk = new List<SunkIVar>(); _sunkDefinite = new HashSet<string>();
                    _sinkCounter = Ast.Variable(typeof(int), "#order");
                    _sinkOrderings = new HashSet<MSA.Expression>();
                }
                sunk.FirstSite = _sinkSites;
                _sunk.Add(sunk);
            } else if (sunk.Type != vt) {
                if (IsInteger(sunk.Type) && IsInteger(vt) && (_sinkWide == null || !_sinkWide.Contains(name))) {
                    if (_sinkWiden == null) { _sinkWiden = new List<string>(); }
                    if (!_sinkWiden.Contains(name)) { _sinkWiden.Add(name); }
                    if (_sinkObstacle == null) {
                        _sinkObstacle = "writes of " + name + " as " + sunk.Type + " and " + vt;
                    }
                    return null;
                }
                _sinkObstacle = "writes of " + name + " as " + sunk.Type + " and " + vt;
                return null;
            }
            _ivarWrite = true;
            sunk.LastSite = _sinkSites++;
            // the store of `@x ||= v' is made on some paths only, even as a top-level statement
            if (!conditional && IsTopStatement(node)) { _sunkDefinite.Add(name); }
            var temp = Ast.Variable(ClrType(vt), "#value");
            var ordering = Ast.IfThen(Ast.Equal(sunk.Order, Ast.Constant(0)),
                Ast.Assign(sunk.Order, Ast.PreIncrementAssign(_sinkCounter)));
            _sinkOrderings.Add(ordering);
            return Ast.Block(ClrType(vt), new[] { temp },
                Ast.Assign(temp, v),
                ordering,
                Ast.Assign(sunk.Value, temp),
                Ast.Assign(sunk.Written, Ast.Constant(true)),
                temp
            );
        }

        /// <summary>A read of a variable the body has held a write of back: the local, or the variable if no write ran.</summary>
        private MSA.Expression TrySunkIVarRead(string/*!*/ name, out JT type) {
            type = JT.Obj;
            var sunk = FindSunk(name);
            if (sunk == null) { return null; }
            if (_sunkDefinite.Contains(name)) {
                type = sunk.Type;
                return sunk.Value;
            }
            return Ast.Condition(sunk.Written, Coerce(sunk.Value, sunk.Type, JT.Obj),
                Ast.Call(JitRuntime.M("GetIVar"), Ast.Constant(_context), _self, Ast.Constant(new InstanceVariableSite(name))));
        }

        /// <summary>
        /// The body with its held-back writes stored at the end, or the body as it is if it has none. Called with the
        /// body's effects as emitted: the stores are this body's effects from here on, and their frozen check its last
        /// deopt. A body whose value is that of its last statement, a held-back write of a number, answers the box it
        /// stores (as SetIVar would), not a second one boxed by its entry: <paramref name="type"/> becomes Obj then.
        /// </summary>
        private MSA.Expression/*!*/ FinishSunkStores(MSA.Expression/*!*/ body, ref JT type) {
            if (_sunk == null) { return body; }
            if (_sinkObstacle == null && !_pure) { _sinkObstacle = "an effect other than the held-back writes"; }
            if (_sinkObstacle == null) {
                var check = new SinkChecker(_sunk);
                check.Visit(body);
                _sinkObstacle = check.Obstacle;
            }
            if (_sinkObstacle != null) { return body; }

            // the last statement's held-back write, whose value is the body's
            SunkIVar last = null;
            var statements = _ast.Body.Statements;
            var lastWrite = (statements != null && statements.Count > 0) ? statements.Last as SimpleAssignmentExpression : null;
            var lastTarget = (lastWrite != null) ? lastWrite.Left as InstanceVariable : null;
            if (lastTarget != null) {
                last = FindSunk(lastTarget.Name);
                if (last != null && (last.Type == JT.Obj || last.Type == JT.Dbl || last.Value.Type != body.Type || !_sunkDefinite.Contains(last.Name))) {
                    last = null;
                }
            }
            var boxed = (last != null) ? Ast.Variable(typeof(object), "#boxed") : null;

            // The order of the stores is the order in which a variable the object does not have yet is added, which
            // instance_variables, inspect and Marshal show. The writes run in the order they were emitted (no loop,
            // no jump), so when every write of each variable comes before every write of the next one (in the order
            // of their first writes) the variables are first written in that order on any path, whichever of them
            // it skips (Json's read: @line, @column, @index, then @current in either arm). Otherwise - `if c then
            // @b = 1 else @a = 1; @b = 2 end' - the stores follow the order recorded as the body ran (#order-x).
            bool ordered = false;
            for (int i = 1; i < _sunk.Count; i++) {
                if (_sunk[i - 1].LastSite > _sunk[i].FirstSite) { ordered = true; }
            }
            if (!ordered) { body = new OrderingRemover(_sinkOrderings).Visit(body); }

            var variables = new List<MSA.ParameterExpression>();
            var all = new List<MSA.Expression>();
            var storeOf = new Dictionary<SunkIVar, MSA.Expression>();
            foreach (var s in _sunk) {
                variables.Add(s.Value);
                variables.Add(s.Written);
                all.Add(Ast.Assign(s.Written, Ast.Constant(false)));
                all.Add(Ast.Assign(s.Value, Ast.Default(s.Value.Type)));
                if (ordered) {
                    variables.Add(s.Order);
                    all.Add(Ast.Assign(s.Order, Ast.Constant(0)));
                }
                var site = Ast.Constant(new InstanceVariableSite(s.Name));
                if (s == last) {
                    storeOf[s] = Ast.Block(typeof(void),
                        Ast.Assign(boxed, Coerce(s.Value, s.Type, JT.Obj)),
                        Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, boxed, site));
                    continue;
                }
                storeOf[s] = (s.Type == JT.Dbl)
                    ? Ast.Call(JitRuntime.M("SetIVarDouble"), Ast.Constant(_context), _self, s.Value, site)
                    : Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, Coerce(s.Value, s.Type, JT.Obj), site);
            }
            var stores = new List<MSA.Expression>();
            if (!ordered) {
                // (the last statement's variable is written on every path)
                foreach (var s in _sunk) { stores.Add((s == last) ? storeOf[s] : Ast.IfThen(s.Written, storeOf[s])); }
            } else {
                // the k-th store is that of the variable written k-th: a chain of tests per position
                variables.Add(_sinkCounter);
                all.Add(Ast.Assign(_sinkCounter, Ast.Constant(0)));
                for (int k = 1; k <= _sunk.Count; k++) {
                    MSA.Expression chain = Ast.Empty();
                    for (int i = _sunk.Count - 1; i >= 0; i--) {
                        chain = Ast.IfThenElse(Ast.Equal(_sunk[i].Order, Ast.Constant(k)), storeOf[_sunk[i]], chain);
                    }
                    stores.Add(chain);
                }
            }
            // SetIVar deopts on a frozen self, at the first store, before anything observable: as for a body's first
            // write where the source makes it, _ivarWrite (set by TrySinkIVarWrite) is what says so, not _canDeopt,
            // which would make a caller binding this body count a deopt after its own effects.
            _pure = false;

            // No try: the body calls nothing but the helpers IsSinkSafe lets through, which throw nothing but a deopt,
            // and it has no safe point a Thread#raise could land at (no call, no loop), so no other exception can
            // leave it with a write held back.
            var result = Ast.Variable(body.Type, "#result");
            variables.Add(result);
            all.Add(Ast.Assign(result, body));
            all.AddRange(stores);
            if (last != null) {
                variables.Add(boxed);
                all.Add(boxed);
                type = JT.Obj;
                return Ast.Block(typeof(object), variables, all);
            }
            all.Add(result);
            return Ast.Block(body.Type, variables, all);
        }

        /// <summary>
        /// What a body that holds writes back may call: helpers that run no Ruby code and read no instance variable
        /// but self's through GetIVar (whose reads of a held-back variable TrySunkIVarRead has already answered).
        /// Anything else - an invoked lambda (an inlined or bound callee), an attribute reader's read, a fallback -
        /// could read a held-back variable, of self or of an object that is self, and compiles the body without.
        /// </summary>
        private static readonly HashSet<string>/*!*/ _sinkSafeHelpers = new HashSet<string> {
            "GetIVar", "GetIVarDouble", "GetIVarSpeculateDouble", "GuardInt", "GuardLong", "GuardDouble", "SpeculateDouble", "StringLength", "StringIndex", "StringEqualsLiteral", "StringEqualsString", "CaseString",
            // Integer arithmetic that deopts on overflow, a zero divisor or a shift it cannot make
            "AddInt", "SubInt", "MulInt", "DivInt", "ModInt", "ShrInt", "AddLong", "SubLong", "MulLong", "DivLong", "ModLong",
            "ShlLong", "ShrLong", "AbsLong", "MathSqrt",
            // a long boxed as an Integer (an Int32 from the small-integer cache when it fits): no Ruby code, no throw
            "NarrowLong",
            // `raise' (JitCompiler.EmitRaise): a deopt and nothing else
            "RaiseDeopt",
            // `Array.new(n, v)' (JitCompiler.EmitArrayConstruction): an allocation nothing sees until it is
            // stored, and a deopt on a size out of range; run twice, it only makes a second Array
            "NewArray", "NewArrayOfSize", "NewArrayOfCheckedSize", "NewArrayOfLongSize", "NewArrayOfObjectSize",
            // an Array's or a Hash's count, a deopt on anything else (JitRuntime.SizeOf): no Ruby code and no instance
            // variable read; its receiver, read from self, is GetIVar's or a held write's
            "SizeOf",
        };

        // Integer#abs of an Int32 (widened, so it cannot overflow) and Float#abs (JitCompiler.EmitCall)
        private static readonly MethodInfo/*!*/ _absLong = typeof(Math).GetMethod("Abs", new[] { typeof(long) });
        private static readonly MethodInfo/*!*/ _absDouble = typeof(Math).GetMethod("Abs", new[] { typeof(double) });
        // a CLR bool as Ruby's true or false (Coerce): one of two cached boxes, no Ruby code, no throw; and the truth of
        // an object (IsTrueMethod, RubyOps.IsTrue): a test for nil and false, which calls nothing
        private static readonly MethodInfo/*!*/ _booleanToObject = typeof(Microsoft.Scripting.Runtime.ScriptingRuntimeHelpers).GetMethod("BooleanToObject");

        private static bool HasWhileLoop(MethodDeclaration/*!*/ ast) {
            var finder = new LoopFinder();
            finder.Walk(ast.Body);
            return finder.Found;
        }

        private sealed class LoopFinder : Walker {
            internal bool Found;

            public override bool Enter(WhileLoopExpression/*!*/ node) {
                Found = true;
                return false;
            }
        }

        private sealed class OrderingRemover : MSA.ExpressionVisitor {
            private readonly HashSet<MSA.Expression> _orderings;
            internal OrderingRemover(HashSet<MSA.Expression> orderings) { _orderings = orderings; }
            public override MSA.Expression Visit(MSA.Expression node) {
                return (node != null && _orderings.Contains(node)) ? Ast.Empty() : base.Visit(node);
            }
        }

        private sealed class SinkChecker : MSA.ExpressionVisitor {
            internal string Obstacle;
            private readonly HashSet<MSA.ParameterExpression> _locals = new HashSet<MSA.ParameterExpression>();

            internal SinkChecker(List<SunkIVar> sunk) {
                foreach (var s in sunk) { _locals.Add(s.Value); _locals.Add(s.Written); }
            }

            protected override MSA.Expression VisitMethodCall(MSA.MethodCallExpression node) {
                var m = node.Method;
                if (Obstacle == null && !IsSafe(m)) {
                    Obstacle = "a call of " + m.DeclaringType.Name + "." + m.Name;
                }
                return base.VisitMethodCall(node);
            }

            private static bool IsSafe(MethodInfo/*!*/ m) {
                if (m.DeclaringType == typeof(JitRuntime)) { return _sinkSafeHelpers.Contains(m.Name); }
                return m == _absLong || m == _absDouble || m == _booleanToObject || m == IsTrueMethod;
            }

            protected override MSA.Expression VisitInvocation(MSA.InvocationExpression node) {
                if (Obstacle == null) { Obstacle = "an invoked body"; }
                return base.VisitInvocation(node);
            }

            protected override MSA.Expression VisitTry(MSA.TryExpression node) {
                if (Obstacle == null) { Obstacle = "a try"; }
                return base.VisitTry(node);
            }

            protected override MSA.Expression VisitGoto(MSA.GotoExpression node) {
                if (Obstacle == null) { Obstacle = "a jump"; }
                return base.VisitGoto(node);
            }

            protected override MSA.Expression VisitExtension(MSA.Expression node) {
                if (Obstacle == null) { Obstacle = "an extension node " + node.GetType().Name; }
                return node;
            }

            protected override MSA.Expression VisitDynamic(MSA.DynamicExpression node) {
                if (Obstacle == null) { Obstacle = "a dynamic call"; }
                return base.VisitDynamic(node);
            }
        }
    }
}
