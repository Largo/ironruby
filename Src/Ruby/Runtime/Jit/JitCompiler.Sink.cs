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
 * Not with a loop, a self-call, an explicit return, fallbacks, or any effect other than these
 * writes (_pure at the end of the body): those are what IsSinkSafe's retry leaves to the old rule.
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

    internal sealed partial class JitCompiler {
        private sealed class SunkIVar {
            internal string Name;
            internal JT Type;
            internal MSA.ParameterExpression Value, Written;
        }

        // Sinking is tried for this body (TryCompile turns it off for a retry); the variables held back,
        // in the order first written; those written by a top-level statement (so written on every path
        // that reaches a later read); and why the body cannot keep them, if it cannot.
        private bool _sinkStores;
        private List<SunkIVar> _sunk;
        private HashSet<string> _sunkDefinite;
        private string _sinkObstacle;

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
        private MSA.Expression TrySinkIVarWrite(SimpleAssignmentExpression/*!*/ node, string/*!*/ name, MSA.Expression/*!*/ v, JT vt, out JT type) {
            type = vt;
            if (!_sinkStores || _loop != null || vt == JT.None || vt == JT.Never) { return null; }
            if (!_pure || _effectNotOnSelf) {
                // something observable came first: the order of the two is the source's only if stored here
                if (_sunk != null) { _sinkObstacle = "an effect before a held-back write"; }
                return null;
            }
            var sunk = FindSunk(name);
            if (sunk == null) {
                sunk = new SunkIVar {
                    Name = name, Type = vt,
                    Value = Ast.Variable(ClrType(vt), "#sunk" + name),
                    Written = Ast.Variable(typeof(bool), "#written" + name),
                };
                if (_sunk == null) { _sunk = new List<SunkIVar>(); _sunkDefinite = new HashSet<string>(); }
                _sunk.Add(sunk);
            } else if (sunk.Type != vt) {
                _sinkObstacle = "writes of " + name + " as " + sunk.Type + " and " + vt;
                return null;
            }
            _ivarWrite = true;
            if (IsTopStatement(node)) { _sunkDefinite.Add(name); }
            var temp = Ast.Variable(ClrType(vt), "#value");
            return Ast.Block(ClrType(vt), new[] { temp },
                Ast.Assign(temp, v),
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

            var variables = new List<MSA.ParameterExpression>();
            var all = new List<MSA.Expression>();
            var stores = new List<MSA.Expression>();
            foreach (var s in _sunk) {
                variables.Add(s.Value);
                variables.Add(s.Written);
                all.Add(Ast.Assign(s.Written, Ast.Constant(false)));
                all.Add(Ast.Assign(s.Value, Ast.Default(s.Value.Type)));
                var site = Ast.Constant(new InstanceVariableSite(s.Name));
                if (s == last) {
                    stores.Add(Ast.Assign(boxed, Coerce(s.Value, s.Type, JT.Obj)));
                    stores.Add(Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, boxed, site));
                    continue;
                }
                stores.Add(Ast.IfThen(s.Written, (s.Type == JT.Dbl)
                    ? Ast.Call(JitRuntime.M("SetIVarDouble"), Ast.Constant(_context), _self, s.Value, site)
                    : Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, Coerce(s.Value, s.Type, JT.Obj), site)));
            }
            // SetIVar deopts on a frozen self: before every store, so the restart finds nothing done
            _canDeopt = true;
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
            "ShlLong", "ShrLong",
        };

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
                // checked arithmetic and conversions of numbers
                return m.DeclaringType == typeof(Math) || m.DeclaringType == typeof(Convert);
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
