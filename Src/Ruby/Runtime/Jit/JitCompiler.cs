/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * Takes a Ruby method AST plus the types the profiling tier observed for its
 * parameters, and builds a *typed* LambdaExpression: Fixnum and Float values live in
 * CLR int/double locals rather than boxed objects, no RubyMethodScope or MutableTuple
 * is allocated, a call to the method itself becomes a direct call to the specialized
 * entry, and there are no dynamic call sites at all.
 *
 * Everything the compiler cannot prove makes it bail out (JitBailout), in which case
 * the method keeps running on the generic body.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Compiler.Ast;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    /// <summary>The type lattice. Deliberately tiny: this is a prototype.</summary>
    internal enum JT {
        None = 0,   // no value yet
        Int,        // CLR int, a Fixnum
        Dbl,        // CLR double, a Float
        Bool,       // CLR bool, TrueClass/FalseClass
        Obj,        // boxed - anything, moved around but never operated on
    }

    internal sealed class JitBailout : Exception {
        internal static readonly JitBailout Instance = new JitBailout();
        private JitBailout() { }
    }

    /// <summary>The result of a successful specialization.</summary>
    internal sealed class JitCode {
        internal MSA.LambdaExpression/*!*/ Lambda;
        internal StrongBox<Delegate>/*!*/ SelfCell;
        internal JT ReturnType;
        internal JT[]/*!*/ ParameterTypes;
        internal bool CanDeopt;
        internal bool EmittedSelfCall;
    }

    internal sealed class JitCompiler {
        private const int MaxNodes = 400;

        private readonly MethodDeclaration/*!*/ _ast;
        private readonly RubyContext/*!*/ _context;
        private readonly int _scopeDepth;

        // Locals, in nested scopes. Ruby has no block scoping, but a local first assigned
        // inside a conditionally executed region is nil on the path that skipped it, which a
        // typed int/double slot cannot represent. Declaring such a local in the region's own
        // DLR block makes any read from outside fail the lookup and bail out, which is exactly
        // the condition under which the two scopings are indistinguishable.
        private readonly List<Dictionary<string, MSA.ParameterExpression>>/*!*/ _vars =
            new List<Dictionary<string, MSA.ParameterExpression>>();
        private readonly List<Dictionary<string, JT>>/*!*/ _types = new List<Dictionary<string, JT>>();
        private readonly List<List<MSA.ParameterExpression>>/*!*/ _declared = new List<List<MSA.ParameterExpression>>();

        private readonly MSA.ParameterExpression/*!*/ _self;
        private readonly JT[]/*!*/ _paramTypes;
        private readonly string/*!*/[]/*!*/ _paramNames;

        // Self-recursion goes through this cell, filled in once the lambda is compiled.
        private readonly StrongBox<Delegate>/*!*/ _selfCell = new StrongBox<Delegate>();
        private JT _selfReturn;
        private Type _selfDelegateType;

        // _pure: nothing in the body is observable part way through, so a deopt may restart it.
        // _canDeopt: the body contains an operation that can signal a deopt.
        private bool _pure = true;
        private bool _canDeopt;
        private bool _selfCall;
        private int _nodes;

        private JitCompiler(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes) {
            _ast = ast;
            _context = context;
            _paramTypes = paramTypes;
            _scopeDepth = ast.DefinedScope.Depth;
            _self = Ast.Parameter(typeof(object), "#self");
            PushScope();

            var mandatory = ast.Parameters.Mandatory;
            _paramNames = new string[mandatory.Length];
            for (int i = 0; i < mandatory.Length; i++) {
                var lv = mandatory[i] as LocalVariable;
                if (lv == null) { throw JitBailout.Instance; }
                _paramNames[i] = lv.Name;
                var p = Ast.Parameter(ClrType(paramTypes[i]), lv.Name);
                _vars[0].Add(lv.Name, p);
                _types[0].Add(lv.Name, paramTypes[i]);
            }
        }

        private void PushScope() {
            _vars.Add(new Dictionary<string, MSA.ParameterExpression>());
            _types.Add(new Dictionary<string, JT>());
            _declared.Add(new List<MSA.ParameterExpression>());
        }

        private List<MSA.ParameterExpression>/*!*/ PopScope() {
            int last = _vars.Count - 1;
            var declared = _declared[last];
            _vars.RemoveAt(last);
            _types.RemoveAt(last);
            _declared.RemoveAt(last);
            return declared;
        }

        private bool TryLookup(string/*!*/ name, out MSA.ParameterExpression p, out JT type) {
            for (int i = _vars.Count - 1; i >= 0; i--) {
                if (_vars[i].TryGetValue(name, out p)) { type = _types[i][name]; return true; }
            }
            p = null;
            type = JT.None;
            return false;
        }

        private MSA.ParameterExpression/*!*/ Declare(string/*!*/ name, JT type) {
            var p = Ast.Parameter(ClrType(type), name);
            int last = _vars.Count - 1;
            _vars[last].Add(name, p);
            _types[last].Add(name, type);
            _declared[last].Add(p);
            return p;
        }

        /// <summary>Emits a conditionally executed statement list in its own local scope.</summary>
        private MSA.Expression/*!*/ EmitRegion(Statements statements, out JT type) {
            PushScope();
            var e = EmitStatements(statements, out type);
            var declared = PopScope();
            return declared.Count > 0 ? Ast.Block(ClrType(type), declared, e) : e;
        }

        private MSA.Expression/*!*/ EmitRegion(RExpr/*!*/ node, out JT type) {
            PushScope();
            var e = Emit(node, out type);
            var declared = PopScope();
            return declared.Count > 0 ? Ast.Block(ClrType(type), declared, e) : e;
        }

        private static Type/*!*/ ClrType(JT t) {
            switch (t) {
                case JT.Int: return typeof(int);
                case JT.Dbl: return typeof(double);
                case JT.Bool: return typeof(bool);
                default: return typeof(object);
            }
        }

        /// <summary>
        /// Compiles a typed body for the given parameter types. Returns null when the method
        /// cannot be specialized.
        /// </summary>
        internal static JitCode TryCompile(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes) {
            // The self-recursive call needs the method's return type before the body has been
            // typed. Guess, compile, and re-run once if the guess was wrong.
            JT guess = JT.None;
            for (int round = 0; round < 3; round++) {
                var c = new JitCompiler(ast, context, paramTypes);
                c._selfReturn = (guess == JT.None) ? JT.Int : guess;
                c._selfDelegateType = DelegateType(paramTypes, c._selfReturn);
                if (c._selfDelegateType == null) { return null; }

                JT actual;
                MSA.Expression body;
                try {
                    body = c.EmitBody(out actual);
                } catch (JitBailout) {
                    return null;
                }

                if (actual != c._selfReturn) {
                    if (guess != JT.None) { return null; }   // did not settle
                    guess = actual;
                    continue;
                }

                // A body that can deopt mid-flight is only safe to restart if nothing it has
                // already done is observable.
                if (c._canDeopt && !c._pure) { return null; }

                var parameters = new List<MSA.ParameterExpression>(paramTypes.Length + 1);
                parameters.Add(c._self);
                var functionScope = new List<MSA.ParameterExpression>(c._declared[0]);
                for (int i = 0; i < c._paramNames.Length; i++) {
                    var p = c._vars[0][c._paramNames[i]];
                    parameters.Add(p);
                    functionScope.Remove(p);
                }
                if (functionScope.Count > 0) {
                    body = Ast.Block(functionScope, body);
                }
                return new JitCode {
                    Lambda = Ast.Lambda(c._selfDelegateType, body, "jit$" + ast.Name, parameters),
                    SelfCell = c._selfCell,
                    ReturnType = actual,
                    ParameterTypes = paramTypes,
                    CanDeopt = c._canDeopt,
                    EmittedSelfCall = c._selfCall,
                };
            }
            return null;
        }

        /// <summary>Func&lt;object self, T1..Tn, TRet&gt;.</summary>
        private static Type DelegateType(JT[]/*!*/ ps, JT ret) {
            var types = new Type[ps.Length + 2];
            types[0] = typeof(object);
            for (int i = 0; i < ps.Length; i++) { types[i + 1] = ClrType(ps[i]); }
            types[ps.Length + 1] = ClrType(ret);
            switch (ps.Length) {
                case 0: return typeof(Func<,>).MakeGenericType(types);
                case 1: return typeof(Func<,,>).MakeGenericType(types);
                case 2: return typeof(Func<,,,>).MakeGenericType(types);
                case 3: return typeof(Func<,,,,>).MakeGenericType(types);
                default: return null;
            }
        }

        // ---- emission ----------------------------------------------------------------------

        private MSA.Expression/*!*/ EmitBody(out JT type) {
            var body = _ast.Body;
            if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                throw JitBailout.Instance;
            }
            return EmitStatements(body.Statements, out type);
        }

        private MSA.Expression/*!*/ EmitStatements(Statements statements, out JT type) {
            if (statements == null || statements.Count == 0) {
                type = JT.Obj;
                return Ast.Constant(null, typeof(object));
            }
            if (statements.Count == 1) {
                return Emit(statements.First, out type);
            }
            var parts = new List<MSA.Expression>();
            foreach (var s in statements.AllButLast) {
                JT ignored;
                parts.Add(Emit(s, out ignored));
            }
            parts.Add(Emit(statements.Last, out type));
            return Ast.Block(ClrType(type), parts);
        }

        private MSA.Expression/*!*/ Emit(RExpr/*!*/ node, out JT type) {
            if (++_nodes > MaxNodes) { throw JitBailout.Instance; }

            var literal = node as Literal;
            if (literal != null) { return EmitLiteral(literal, out type); }

            var local = node as LocalVariable;
            if (local != null) { return EmitLocalRead(local, out type); }

            var assign = node as SimpleAssignmentExpression;
            if (assign != null) { return EmitAssignment(assign, out type); }

            var call = node as MethodCall;
            if (call != null) { return EmitCall(call, out type); }

            var cond = node as ConditionalExpression;
            if (cond != null) { return EmitIfLike(cond.Condition, cond.TrueExpression, cond.FalseExpression, out type); }

            var ifExpr = node as IfExpression;
            if (ifExpr != null) { return EmitIf(ifExpr, out type); }

            var unless = node as UnlessExpression;
            if (unless != null) { return EmitUnless(unless, out type); }

            var not = node as NotExpression;
            if (not != null) {
                type = JT.Bool;
                return Ast.Not(EmitCondition(not.Expression));
            }

            var and = node as AndExpression;
            if (and != null) {
                type = JT.Bool;
                var l = EmitCondition(and.Left);
                PushScope();
                var r = EmitCondition(and.Right);
                if (PopScope().Count > 0) { throw JitBailout.Instance; }
                return Ast.AndAlso(l, r);
            }

            var or = node as OrExpression;
            if (or != null) {
                type = JT.Bool;
                var l = EmitCondition(or.Left);
                PushScope();
                var r = EmitCondition(or.Right);
                if (PopScope().Count > 0) { throw JitBailout.Instance; }
                return Ast.OrElse(l, r);
            }

            var ivar = node as InstanceVariable;
            if (ivar != null) {
                type = JT.Obj;
                return Ast.Call(JitRuntime.M("GetIVar"), Ast.Constant(_context), _self, Ast.Constant(ivar.Name));
            }

            var self = node as SelfReference;
            if (self != null) {
                type = JT.Obj;
                return _self;
            }

            var loop = node as WhileLoopExpression;
            if (loop != null) { return EmitWhile(loop, out type); }

            var body = node as Body;
            if (body != null) {
                if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                    throw JitBailout.Instance;
                }
                return EmitStatements(body.Statements, out type);
            }

            throw JitBailout.Instance;
        }

        private MSA.Expression/*!*/ EmitLiteral(Literal/*!*/ literal, out JT type) {
            object v = literal.Value;
            if (v is int) { type = JT.Int; return Ast.Constant((int)v, typeof(int)); }
            if (v is double) { type = JT.Dbl; return Ast.Constant((double)v, typeof(double)); }
            if (v is bool) { type = JT.Bool; return Ast.Constant((bool)v, typeof(bool)); }
            if (v == null) { type = JT.Obj; return Ast.Constant(null, typeof(object)); }
            throw JitBailout.Instance;
        }

        private MSA.Expression/*!*/ EmitLocalRead(LocalVariable/*!*/ local, out JT type) {
            if (local.DefinitionLexicalDepth != _scopeDepth) { throw JitBailout.Instance; }
            MSA.ParameterExpression p;
            if (!TryLookup(local.Name, out p, out type)) {
                // A local read before any assignment is nil in Ruby; not modelled.
                throw JitBailout.Instance;
            }
            return p;
        }

        private MSA.Expression/*!*/ EmitAssignment(SimpleAssignmentExpression/*!*/ node, out JT type) {
            string op = node.Operation;
            if (op == "&&" || op == "||") { throw JitBailout.Instance; }

            var ivarTarget = node.Left as InstanceVariable;
            if (ivarTarget != null) {
                if (op != null) { throw JitBailout.Instance; }
                JT vt;
                var v = Emit(node.Right, out vt);
                _pure = false;      // an ivar write is observable, so the body may not deopt
                type = JT.Obj;
                return Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, Coerce(v, vt, JT.Obj), Ast.Constant(ivarTarget.Name));
            }

            var target = node.Left as LocalVariable;
            if (target == null || target.DefinitionLexicalDepth != _scopeDepth) { throw JitBailout.Instance; }

            JT rhsType;
            var rhs = Emit(node.Right, out rhsType);

            MSA.ParameterExpression p;
            JT lt;
            bool known = TryLookup(target.Name, out p, out lt);

            if (op != null) {
                // `x op= v'  ->  `x = x op v'
                if (!known) { throw JitBailout.Instance; }
                if (!IsNumeric(lt) || !IsNumeric(rhsType)) { throw JitBailout.Instance; }
                rhs = EmitBinary(op, p, lt, rhs, rhsType, out rhsType);
            }

            if (known) {
                if (lt != rhsType) { throw JitBailout.Instance; }
            } else {
                p = Declare(target.Name, rhsType);
            }
            type = rhsType;
            return Ast.Assign(p, rhs);
        }

        private MSA.Expression/*!*/ EmitIf(IfExpression/*!*/ node, out JT type) {
            var clauses = node.ElseIfClauses;
            MSA.Expression elseExpr = Ast.Constant(null, typeof(object));
            JT elseType = JT.Obj;
            int i = (clauses != null) ? clauses.Count - 1 : -1;
            if (i >= 0 && clauses[i].Condition == null) {
                elseExpr = EmitRegion(clauses[i].Statements, out elseType);
                i--;
            }
            for (; i >= 0; i--) {
                if (clauses[i].Condition == null) { throw JitBailout.Instance; }
                JT thenType;
                var thenExpr = EmitRegion(clauses[i].Statements, out thenType);
                elseExpr = Join(EmitCondition(clauses[i].Condition), thenExpr, thenType, elseExpr, elseType, out elseType);
            }
            JT headType;
            var head = EmitRegion(node.Body, out headType);
            return Join(EmitCondition(node.Condition), head, headType, elseExpr, elseType, out type);
        }

        private MSA.Expression/*!*/ EmitUnless(UnlessExpression/*!*/ node, out JT type) {
            JT thenType;
            var thenExpr = EmitRegion(node.Statements, out thenType);
            MSA.Expression elseExpr = Ast.Constant(null, typeof(object));
            JT elseType = JT.Obj;
            if (node.ElseClause != null) {
                if (node.ElseClause.Condition != null) { throw JitBailout.Instance; }
                elseExpr = EmitRegion(node.ElseClause.Statements, out elseType);
            }
            return Join(Ast.Not(EmitCondition(node.Condition)), thenExpr, thenType, elseExpr, elseType, out type);
        }

        /// <summary>
        /// `while c; body; end' / `until c; body; end'. Any break/next/redo inside makes Emit
        /// bail, so the loop has exactly one exit and its value is always nil.
        /// </summary>
        private MSA.Expression/*!*/ EmitWhile(WhileLoopExpression/*!*/ node, out JT type) {
            if (node.IsPostTest) { throw JitBailout.Instance; }
            var exit = Ast.Label("#while-exit");
            var test = EmitCondition(node.Condition);
            if (!node.IsWhileLoop) { test = Ast.Not(test); }
            JT ignored;
            var body = EmitRegion(node.Statements, out ignored);
            type = JT.Obj;
            return Ast.Block(typeof(object),
                Ast.Loop(Ast.Block(Ast.IfThen(Ast.Not(test), Ast.Break(exit)), body), exit),
                Ast.Constant(null, typeof(object))
            );
        }

        private MSA.Expression/*!*/ EmitIfLike(RExpr/*!*/ c, RExpr/*!*/ t, RExpr/*!*/ f, out JT type) {
            var test = EmitCondition(c);
            JT tt, ft;
            var te = EmitRegion(t, out tt);
            var fe = EmitRegion(f, out ft);
            return Join(test, te, tt, fe, ft, out type);
        }

        private MSA.Expression/*!*/ Join(MSA.Expression/*!*/ test, MSA.Expression/*!*/ a, JT at, MSA.Expression/*!*/ b, JT bt, out JT type) {
            type = Unify(at, bt);
            return Ast.Condition(test, Coerce(a, at, type), Coerce(b, bt, type), ClrType(type));
        }

        private static JT Unify(JT a, JT b) {
            if (a == b) { return a; }
            if (a == JT.Int && b == JT.Dbl) { return JT.Dbl; }
            if (a == JT.Dbl && b == JT.Int) { return JT.Dbl; }
            return JT.Obj;
        }

        private static MSA.Expression/*!*/ Coerce(MSA.Expression/*!*/ e, JT from, JT to) {
            if (from == to) { return e; }
            if (to == JT.Dbl && from == JT.Int) { return Ast.Convert(e, typeof(double)); }
            if (to == JT.Obj) {
                // Fixnum is a boxed int, Float a boxed double, true/false boxed bools.
                return Ast.Convert(e, typeof(object));
            }
            throw JitBailout.Instance;
        }

        /// <summary>
        /// Ruby truthiness. A comparison already produces a CLR bool; an int/float value is
        /// always truthy.
        /// </summary>
        private MSA.Expression/*!*/ EmitCondition(RExpr/*!*/ node) {
            JT t;
            var e = Emit(node, out t);
            switch (t) {
                case JT.Bool: return e;
                case JT.Int:
                case JT.Dbl: return Ast.Block(e, Ast.Constant(true));
                default: throw JitBailout.Instance;
            }
        }

        // ---- calls -------------------------------------------------------------------------

        private MSA.Expression/*!*/ EmitCall(MethodCall/*!*/ node, out JT type) {
            if (node.Block != null) { throw JitBailout.Instance; }
            var args = node.Arguments;
            int argc = (args == null) ? 0 : args.Expressions.Length;

            // Self-recursion: `fib(n - 1)' inside `def fib'. Sound because the entry stub pins
            // the receiver class and the global method version, so no override can slip in.
            if (node.Target == null && !node.IsVariableCall && node.MethodName == _ast.Name && argc == _paramNames.Length) {
                var callArgs = new MSA.Expression[argc + 1];
                callArgs[0] = _self;
                for (int i = 0; i < argc; i++) {
                    JT at;
                    var a = Emit(args.Expressions[i], out at);
                    callArgs[i + 1] = Coerce(a, at, _paramTypes[i]);
                }
                type = _selfReturn;
                _selfCall = true;
                return Ast.Invoke(
                    Ast.Convert(Ast.Field(Ast.Constant(_selfCell), "Value"), _selfDelegateType),
                    callArgs
                );
            }

            // Binary operators on Fixnum/Float.
            if (node.Target != null && argc == 1) {
                JT lt, rt;
                var l = Emit(node.Target, out lt);
                var r = Emit(args.Expressions[0], out rt);
                if (IsNumeric(lt) && IsNumeric(rt)) {
                    return EmitBinary(node.MethodName, l, lt, r, rt, out type);
                }
            }

            throw JitBailout.Instance;
        }

        private static bool IsNumeric(JT t) {
            return t == JT.Int || t == JT.Dbl;
        }

        private MSA.Expression/*!*/ EmitBinary(string/*!*/ op, MSA.Expression/*!*/ l, JT lt, MSA.Expression/*!*/ r, JT rt, out JT type) {
            JT operand = Unify(lt, rt);
            if (operand == JT.Obj) { throw JitBailout.Instance; }
            l = Coerce(l, lt, operand);
            r = Coerce(r, rt, operand);

            bool isInt = (operand == JT.Int);
            switch (op) {
                case "+": type = operand; if (isInt) { _canDeopt = true; return Ast.Call(JitRuntime.M("AddInt"), l, r); } return Ast.Add(l, r);
                case "-": type = operand; if (isInt) { _canDeopt = true; return Ast.Call(JitRuntime.M("SubInt"), l, r); } return Ast.Subtract(l, r);
                case "*": type = operand; if (isInt) { _canDeopt = true; return Ast.Call(JitRuntime.M("MulInt"), l, r); } return Ast.Multiply(l, r);
                case "/": type = operand; if (isInt) { _canDeopt = true; return Ast.Call(JitRuntime.M("DivInt"), l, r); } return Ast.Call(JitRuntime.M("DivDouble"), l, r);
                case "%": type = operand; if (isInt) { _canDeopt = true; return Ast.Call(JitRuntime.M("ModInt"), l, r); } return Ast.Call(JitRuntime.M("ModDouble"), l, r);
                case "<": type = JT.Bool; return Ast.LessThan(l, r);
                case ">": type = JT.Bool; return Ast.GreaterThan(l, r);
                case "<=": type = JT.Bool; return Ast.LessThanOrEqual(l, r);
                case ">=": type = JT.Bool; return Ast.GreaterThanOrEqual(l, r);
                case "==": type = JT.Bool; return Ast.Equal(l, r);
                case "!=": type = JT.Bool; return Ast.NotEqual(l, r);
                default: throw JitBailout.Instance;
            }
        }
    }
}
