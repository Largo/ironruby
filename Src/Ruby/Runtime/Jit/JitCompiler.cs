/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * Takes a Ruby method AST plus the types the profiling tier observed for its
 * parameters, and builds a *typed* LambdaExpression: Integer and Float values live in
 * CLR int/long/double locals rather than boxed objects, no RubyMethodScope or MutableTuple
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
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    /// <summary>The type lattice. Deliberately tiny: this is a prototype.</summary>
    internal enum JT {
        None = 0,   // no value yet
        Never,      // bottom: the expression never produces a value (it jumped away). Unifies
                    // with anything, and is coerced by evaluating it and then falling into an
                    // unreachable default of whatever type the context wanted.
        Int,        // CLR int, an Integer that fits in an Int32
        Lng,        // CLR long, an Integer that fits in an Int64 but not an Int32 - and, inside
                    // the specialized region, any integer at all: integer arithmetic happens in
                    // long, and a result narrows back to Int only where it has to.
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
        internal const int MaxNodes = 400;

        private readonly MethodDeclaration _ast;
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

        // Explicit `return'. The label is created on the first one met, typed by the return type
        // the round is compiling under (_selfReturn); _returnUnion is what the values actually
        // seen so far unify to, which is how the round after this one is typed. _retype is set
        // when a return value cannot flow into the current guess at all, so the round is
        // abandoned and re-run against the wider type rather than rejected.
        private MSA.LabelTarget _returnLabel;
        private JT _returnUnion = JT.None;
        private JT _retype = JT.None;
        private bool _firstRound;

        /// <summary>The reason the last bailout gave, for IR_JIT_VERBOSE=1. Not on a fast path.</summary>
        private string _bailReason;

        private JitBailout/*!*/ Bail(string/*!*/ reason) {
            if (JitRuntime.Verbose && _bailReason == null) { _bailReason = reason; }
            return JitBailout.Instance;
        }

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

        // ---- loop mode -------------------------------------------------------------------
        // -X:OSR points the same emitter at a `while' loop rather than a method body. The
        // difference is where the locals live: in a method they are parameters and CLR locals,
        // in a loop they are slots of the enclosing scope's MutableTuple, bound on demand as
        // the emitter meets them and loaded into typed CLR locals by the entry JitLoopCompiler
        // builds around this. Everything between those two edges is the same code.
        private readonly WhileLoopExpression _loop;
        private readonly Func<LocalVariable, JT, JitLoopSlot> _bindSlot;
        private readonly List<JitLoopSlot> _slots;
        private MSA.LabelTarget _breakLabel, _continueLabel;

        private JitCompiler(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context, int scopeDepth,
            Func<LocalVariable, JT, JitLoopSlot>/*!*/ bindSlot) {
            _ast = null;
            _loop = loop;
            _context = context;
            _scopeDepth = scopeDepth;
            _bindSlot = bindSlot;
            _slots = new List<JitLoopSlot>();
            _paramTypes = new JT[0];
            _paramNames = new string[0];
            _self = Ast.Parameter(typeof(object), "#self");
            PushScope();
        }

        /// <summary>
        /// Emits a typed body for one `while' loop. The slots it bound - the scope locals it
        /// touched, and the type each of them held when the loop turned hot - come back in
        /// <paramref name="slots"/>; the caller loads and stores them.
        /// </summary>
        internal static MSA.Expression TryCompileLoop(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context,
            int scopeDepth, Func<LocalVariable, JT, JitLoopSlot>/*!*/ bindSlot, out List<JitLoopSlot> slots, out bool canDeopt) {

            slots = null;
            canDeopt = false;
            var c = new JitCompiler(loop, context, scopeDepth, bindSlot);
            MSA.Expression body;
            try {
                JT type;
                body = c.EmitWhile(loop, out type);
            } catch (JitBailout) {
                return null;
            }
            // A deopt restores the locals to what they were at the top of the iteration and
            // hands the loop back, so the iteration is re-run: it must not have been observable.
            if (c._canDeopt && !c._pure) { return null; }
            slots = c._slots;
            canDeopt = c._canDeopt;
            return body;
        }

        /// <summary>The key a local is looked up under: names repeat across lexical depths.</summary>
        private string/*!*/ Key(LocalVariable/*!*/ local) {
            return (_loop == null) ? local.Name : local.Name + "@" + local.DefinitionLexicalDepth;
        }

        /// <summary>
        /// In loop mode a local the emitter has not met yet is bound to its tuple slot, typed
        /// by the value it holds right now - which is the only profile a loop that is already
        /// running needs.
        /// </summary>
        private bool BindLoopLocal(LocalVariable/*!*/ local, JT hint, out MSA.ParameterExpression p, out JT type) {
            var slot = _bindSlot(local, hint);
            if (slot == null) { p = null; type = JT.None; throw JitBailout.Instance; }
            _slots.Add(slot);
            _slotByVariable.Add(slot.Variable, slot);
            _vars[0].Add(Key(local), slot.Variable);
            _types[0].Add(Key(local), slot.Type);
            p = slot.Variable;
            type = slot.Type;
            return true;
        }

        private readonly Dictionary<MSA.ParameterExpression, JitLoopSlot> _slotByVariable =
            new Dictionary<MSA.ParameterExpression, JitLoopSlot>();

        /// <summary>
        /// The load of a slot the loop writes before it reads - a temporary of its own. What
        /// the slot held when the loop turned hot says nothing about what it holds on a later
        /// entry (the scope may be brand new, and the slot nil), so such a slot has no type
        /// guard; instead a flag says whether this run has written it. Reading it before that
        /// means the Ruby code is reading nil, which the typed body has no value for: deopt,
        /// and let the loop itself raise whatever it raises.
        /// </summary>
        private MSA.Expression/*!*/ LoadLoopSlot(MSA.ParameterExpression/*!*/ p) {
            JitLoopSlot slot;
            if (_loop != null && _slotByVariable.TryGetValue(p, out slot) && slot.Assigned != null) {
                _canDeopt = true;
                return Ast.Block(p.Type,
                    Ast.IfThen(Ast.Not(slot.Assigned), Ast.Throw(Ast.Call(JitRuntime.M("OsrUnsetSlot")))),
                    p
                );
            }
            return p;
        }

        /// <summary>The store into such a slot, which is what puts its flag up.</summary>
        private MSA.Expression/*!*/ StoreLoopSlot(MSA.ParameterExpression/*!*/ p, MSA.Expression/*!*/ rhs) {
            JitLoopSlot slot;
            if (_slotByVariable.TryGetValue(p, out slot) && slot.Assigned != null) {
                // The flag goes up after the right hand side has run, not before: `x += 1'
                // reads x, and that read has to still see the slot as unwritten.
                return Ast.Block(p.Type, Ast.Assign(p, rhs), Ast.Assign(slot.Assigned, Ast.Constant(true)), p);
            }
            return Ast.Assign(p, rhs);
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
                case JT.Lng: return typeof(long);
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
            // Rounds: each retry widens the guess (Int -> Lng -> Dbl/Obj), so the lattice bounds
            // how many there can be; a guess that does not settle is rejected.
            JT guess = JT.None;
            for (int round = 0; round < 5; round++) {
                var c = new JitCompiler(ast, context, paramTypes);
                c._firstRound = (guess == JT.None);
                c._selfReturn = c._firstRound ? JT.Int : guess;
                c._selfDelegateType = DelegateType(paramTypes, c._selfReturn);
                if (c._selfDelegateType == null) { return null; }

                JT actual;
                MSA.Expression body;
                try {
                    body = c.EmitBody(out actual);
                } catch (JitBailout) {
                    // A `return' whose value does not fit the type this round assumed is not a
                    // rejection, it is a re-run against the type that does fit.
                    if (c._retype != JT.None && c._retype != c._selfReturn) { guess = c._retype; continue; }
                    if (JitRuntime.Verbose) {
                        Console.Error.WriteLine("[jit] rejected {0} arity {1}: {2}", ast.Name, paramTypes.Length,
                            c._bailReason ?? "unsupported construct");
                    }
                    return null;
                }

                // The method's value is the value it falls off with, unified with every value an
                // explicit `return' hands back.
                JT result = (c._returnUnion == JT.None) ? actual : Merge(actual, c._returnUnion);
                if (result != c._selfReturn) {
                    if (result == guess) { return null; }    // did not settle
                    guess = result;
                    continue;
                }

                // The return label carries every explicit `return'; the body falls into it.
                if (actual != result || c._returnLabel != null) {
                    body = c.Coerce(body, actual, result);
                }
                if (c._returnLabel != null) {
                    body = Ast.Label(c._returnLabel, body);
                }
                actual = result;

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

        /// <summary>
        /// The node dispatch. ADDING A CASE HERE MEANS ADDING ONE TO <see cref="JitScreen"/> at
        /// the bottom of this file: the screen decides, before a method is ever wrapped, whether
        /// this method could accept its body, and a node kind it has not been told about is a
        /// method the JIT quietly stops speeding up. IR_JIT_VERBOSE=1 prints the histogram of
        /// node kinds the screen rejected, so the omission shows up there.
        /// </summary>
        private MSA.Expression/*!*/ Emit(RExpr/*!*/ node, out JT type) {
            if (++_nodes > MaxNodes) { throw Bail("too many nodes"); }

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
                // `!x' is a call of x's #! - which the program may have redefined.
                JT operandType;
                var operand = EmitCondition(not.Expression, out operandType);
                RequireBuiltin(operandType, "!");
                type = JT.Bool;
                return Ast.Not(operand);
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
                return Ast.Call(JitRuntime.M("GetIVar"), Ast.Constant(_context), _self, Ast.Constant(new InstanceVariableSite(ivar.Name)));
            }

            var self = node as SelfReference;
            if (self != null) {
                type = JT.Obj;
                return _self;
            }

            var loop = node as WhileLoopExpression;
            if (loop != null) { return EmitWhile(loop, out type); }

            if (_loop != null && _breakLabel != null) {
                var brk = node as BreakStatement;
                if (brk != null) {
                    if (brk.Arguments != null) { throw JitBailout.Instance; }
                    type = JT.Obj;
                    return Ast.Block(typeof(object), Ast.Break(_breakLabel), Ast.Constant(null, typeof(object)));
                }
                var nxt = node as NextStatement;
                if (nxt != null) {
                    if (nxt.Arguments != null) { throw JitBailout.Instance; }
                    type = JT.Obj;
                    return Ast.Block(typeof(object), Ast.Continue(_continueLabel), Ast.Constant(null, typeof(object)));
                }
            }

            var ret = node as ReturnStatement;
            if (ret != null) { return EmitReturn(ret, out type); }

            var body = node as Body;
            if (body != null) {
                if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                    throw JitBailout.Instance;
                }
                return EmitStatements(body.Statements, out type);
            }

            throw Bail("node: " + node.GetType().Name);
        }

        private MSA.Expression/*!*/ EmitLiteral(Literal/*!*/ literal, out JT type) {
            object v = literal.Value;
            if (v is int) { type = JT.Int; return Ast.Constant((int)v, typeof(int)); }
            if (v is long) { type = JT.Lng; return Ast.Constant((long)v, typeof(long)); }
            if (v is double) { type = JT.Dbl; return Ast.Constant((double)v, typeof(double)); }
            if (v is bool) { type = JT.Bool; return Ast.Constant((bool)v, typeof(bool)); }
            if (v == null) { type = JT.Obj; return Ast.Constant(null, typeof(object)); }
            throw Bail("literal: " + (v == null ? "nil" : v.GetType().Name));
        }

        private MSA.Expression/*!*/ EmitLocalRead(LocalVariable/*!*/ local, out JT type) {
            if (_loop == null && local.DefinitionLexicalDepth != _scopeDepth) { throw JitBailout.Instance; }
            MSA.ParameterExpression p;
            if (!TryLookup(Key(local), out p, out type)) {
                if (_loop != null) {
                    BindLoopLocal(local, JT.None, out p, out type);
                    return LoadLoopSlot(p);
                }
                // A local read before any assignment is nil in Ruby; not modelled.
                throw Bail("local read before assignment: " + local.Name);
            }
            return (_loop != null) ? LoadLoopSlot(p) : p;
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
                return Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, Coerce(v, vt, JT.Obj), Ast.Constant(new InstanceVariableSite(ivarTarget.Name)));
            }

            var target = node.Left as LocalVariable;
            if (target == null || (_loop == null && target.DefinitionLexicalDepth != _scopeDepth)) { throw JitBailout.Instance; }

            JT rhsType;
            var rhs = Emit(node.Right, out rhsType);

            MSA.ParameterExpression p;
            JT lt;
            bool known = TryLookup(Key(target), out p, out lt);
            if (!known && _loop != null) {
                // The slot exists in the tuple whether or not the loop has read it yet, and it
                // is written back on the way out, so it has to be bound rather than declared.
                known = BindLoopLocal(target, rhsType, out p, out lt);
            }

            if (op != null) {
                // `x op= v'  ->  `x = x op v'
                if (!known) { throw JitBailout.Instance; }
                if (!IsNumeric(lt) || !IsNumeric(rhsType)) { throw JitBailout.Instance; }
                rhs = EmitBinary(op, (_loop != null) ? LoadLoopSlot(p) : p, lt, rhs, rhsType, out rhsType);
            }

            if (known) {
                if (lt != rhsType) {
                    // Int and Lng are two representations of one Ruby class, so a store across
                    // them is a widening or a narrowing, not a type change. Anything else would
                    // change the value's class, which one typed slot cannot express.
                    if (!IsInteger(lt) || !IsInteger(rhsType)) { throw JitBailout.Instance; }
                    rhs = Coerce(rhs, rhsType, lt);
                    rhsType = lt;
                }
            } else {
                p = Declare(target.Name, rhsType);
            }
            type = rhsType;
            return (_loop != null) ? StoreLoopSlot(p, rhs) : (MSA.Expression)Ast.Assign(p, rhs);
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
            var cont = Ast.Label("#while-continue");
            var savedBreak = _breakLabel;
            var savedContinue = _continueLabel;
            _breakLabel = exit;
            _continueLabel = cont;
            var test = EmitCondition(node.Condition);
            if (!node.IsWhileLoop) { test = Ast.Not(test); }
            JT ignored;
            var body = EmitRegion(node.Statements, out ignored);
            _breakLabel = savedBreak;
            _continueLabel = savedContinue;
            type = JT.Obj;

            // The outlined loop keeps a copy of its locals as they were at the top of the
            // iteration. A deopt part way through one restores them, so the loop is handed back
            // at an iteration boundary and the generic copy re-runs the iteration from there.
            MSA.Expression snapshot = (node == _loop) ? EmitSnapshot() : null;

            var iteration = (snapshot != null)
                ? Ast.Block(snapshot, Ast.IfThen(Ast.Not(test), Ast.Break(exit)), body)
                : Ast.Block(Ast.IfThen(Ast.Not(test), Ast.Break(exit)), body);

            return Ast.Block(typeof(object),
                Ast.Loop(iteration, exit, cont),
                Ast.Constant(null, typeof(object))
            );
        }

        /// <summary>
        /// `snap_x = x' for every slot bound so far. Emitted lazily, after the body, because
        /// the body is what binds them; the DLR block it produces runs before the test.
        /// </summary>
        private MSA.Expression/*!*/ EmitSnapshot() {
            if (_slots.Count == 0) { return AstUtils.Empty(); }
            var copies = new List<MSA.Expression>(_slots.Count);
            foreach (var slot in _slots) {
                copies.Add(Ast.Assign(slot.Snapshot, slot.Variable));
                if (slot.Assigned != null) {
                    copies.Add(Ast.Assign(slot.AssignedSnapshot, slot.Assigned));
                }
            }
            return Ast.Block(copies);
        }

        /// <summary>
        /// `return v' / `return'. The value leaves through a DLR return label typed by the
        /// return type this round is compiling under, which is the same type the value the body
        /// falls off with is given - so an early `return n' and a trailing `return f(n)' unify
        /// exactly the way the two branches of an `if' do. A round whose guess turns out to be
        /// too narrow for a value some `return' produces is abandoned and re-run wider.
        ///
        /// The value is JT.Never: control does not continue here, so whatever the context wanted
        /// this expression to be, it may be.
        /// </summary>
        private MSA.Expression/*!*/ EmitReturn(ReturnStatement/*!*/ node, out JT type) {
            // In loop mode the body being emitted is one loop outlined out of its method, entered
            // and left by the OSR site. A `return' there returns from the *method*, which the
            // outlined copy has no way to do.
            if (_loop != null) { throw Bail("return inside an outlined loop"); }

            var args = node.Arguments;
            JT vt;
            MSA.Expression value;
            if (args == null || args.Expressions.Length == 0) {
                vt = JT.Obj;
                value = Ast.Constant(null, typeof(object));
            } else if (args.Expressions.Length == 1 && !(args.Expressions[0] is SplattedArgument)) {
                value = Emit(args.Expressions[0], out vt);
            } else {
                throw Bail("return of several values");
            }

            if (vt == JT.Never) {
                // `return (return v)' - only the inner jump ever happens.
                type = JT.Never;
                return value;
            }

            _returnUnion = (_returnUnion == JT.None) ? vt : Merge(_returnUnion, vt);
            if (Merge(vt, _selfReturn) != _selfReturn) {
                // The first round's Int is an assumption, not an observation, so it does not
                // take part in the merge: a Float `return x' makes the next round Float, not Obj.
                _retype = _firstRound ? vt : Merge(_selfReturn, vt);
                throw Bail("return value wider than this round's return type");
            }

            if (_returnLabel == null) { _returnLabel = Ast.Label(ClrType(_selfReturn), "#return"); }
            type = JT.Never;
            return Ast.Return(_returnLabel, Coerce(value, vt, _selfReturn), typeof(object));
        }

        private MSA.Expression/*!*/ EmitIfLike(RExpr/*!*/ c, RExpr/*!*/ t, RExpr/*!*/ f, out JT type) {
            var test = EmitCondition(c);
            JT tt, ft;
            var te = EmitRegion(t, out tt);
            var fe = EmitRegion(f, out ft);
            return Join(test, te, tt, fe, ft, out type);
        }

        private MSA.Expression/*!*/ Join(MSA.Expression/*!*/ test, MSA.Expression/*!*/ a, JT at, MSA.Expression/*!*/ b, JT bt, out JT type) {
            type = Merge(at, bt);
            return Ast.Condition(test, Coerce(a, at, type), Coerce(b, bt, type), ClrType(type));
        }

        /// <summary>
        /// The type of a value that comes from one of two control-flow paths - the arms of an
        /// `if', the fall-off value and the explicit returns. Int and Lng are two
        /// representations of one Ruby class, so they merge to Lng. An Integer and a Float do
        /// NOT merge to Float: converting the Integer would change its class (`c ? 1.5 : 2' is
        /// 2, not 2.0), so a mixed pair is boxed.
        /// </summary>
        private static JT Merge(JT a, JT b) {
            if (a == b) { return a; }
            // A path that jumped away contributes no value, so the other one decides.
            if (a == JT.Never) { return b; }
            if (b == JT.Never) { return a; }
            if (IsInteger(a) && IsInteger(b)) { return JT.Lng; }
            return JT.Obj;
        }

        /// <summary>
        /// The operand type of a binary numeric operation: Ruby promotes an Integer operand of
        /// Float arithmetic to Float, so here - and only here - Int op Dbl is Dbl.
        /// </summary>
        private static JT Unify(JT a, JT b) {
            if (a == b) { return a; }
            if (IsNumeric(a) && IsNumeric(b)) {
                // int < long < double: the wider of the two wins.
                return (a == JT.Dbl || b == JT.Dbl) ? JT.Dbl : JT.Lng;
            }
            return JT.Obj;
        }

        private MSA.Expression/*!*/ Coerce(MSA.Expression/*!*/ e, JT from, JT to) {
            if (from == to) { return e; }
            if (from == JT.Never) {
                // e always jumps away, so what follows it is unreachable and only there to give
                // the expression the static type its context wants.
                return Ast.Block(ClrType(to), e, Ast.Default(ClrType(to)));
            }
            if (to == JT.Dbl && (from == JT.Int || from == JT.Lng)) { return Ast.Convert(e, typeof(double)); }
            if (to == JT.Lng && from == JT.Int) { return Ast.Convert(e, typeof(long)); }
            if (to == JT.Int && from == JT.Lng) {
                // Only where the value has to be an int again: an int local, an int parameter of
                // the direct self-call. Out of range means the Ruby value really did outgrow an
                // Int32, and the generic body takes it from there.
                _canDeopt = true;
                return FuseIntOp(e) ?? Ast.Call(JitRuntime.M("DemoteToInt"), e);
            }
            if (to == JT.Obj) {
                // A long leaves through the representation funnel: a value that fits in an Int32
                // IS an Int32, which is what #hash, #eql? and Hash keys are defined on.
                if (from == JT.Lng) { return Ast.Call(JitRuntime.M("NarrowLong"), e); }
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
            return EmitCondition(node, out t);
        }

        private MSA.Expression/*!*/ EmitCondition(RExpr/*!*/ node, out JT t) {
            var e = Emit(node, out t);
            switch (t) {
                case JT.Bool: return e;
                case JT.Int:
                case JT.Lng:
                case JT.Dbl: return Ast.Block(e, Ast.Constant(true));
                default: throw Bail("condition of type " + t);
            }
        }

        // ---- calls -------------------------------------------------------------------------

        private MSA.Expression/*!*/ EmitCall(MethodCall/*!*/ node, out JT type) {
            if (node.Block != null) { throw Bail("call with a block: " + node.MethodName); }
            var args = node.Arguments;
            int argc = (args == null) ? 0 : args.Expressions.Length;

            // Self-recursion: `fib(n - 1)' inside `def fib'. Sound because the entry stub pins
            // the receiver class and the global method version, so no override can slip in.
            if (_ast != null && node.Target == null && !node.IsVariableCall && node.MethodName == _ast.Name && argc == _paramNames.Length) {
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

            throw Bail("call: " + node.MethodName + "/" + argc);
        }

        private static bool IsNumeric(JT t) {
            return t == JT.Int || t == JT.Lng || t == JT.Dbl;
        }

        /// <summary>The two representations of a Ruby Integer the specialized code carries.</summary>
        private static bool IsInteger(JT t) {
            return t == JT.Int || t == JT.Lng;
        }

        /// <summary>
        /// `x = a op b' where a, b and x are all ints. Computing in long and narrowing back is
        /// correct but is a widening, an operation and a range check where one int operation with
        /// one check will do - and that shape is most of every int loop, so it is worth spotting:
        /// an int-typed operand pair under a narrowing back to int fuses into the int helper it
        /// came from. The two deopt on exactly the same values.
        /// </summary>
        private static MSA.Expression FuseIntOp(MSA.Expression/*!*/ e) {
            var bin = e as MSA.BinaryExpression;
            if (bin != null) {
                string name;
                switch (bin.NodeType) {
                    case MSA.ExpressionType.Add: name = "AddInt"; break;
                    case MSA.ExpressionType.Subtract: name = "SubInt"; break;
                    case MSA.ExpressionType.Multiply: name = "MulInt"; break;
                    default: return null;
                }
                var l = Narrowed(bin.Left);
                var r = Narrowed(bin.Right);
                return (l != null && r != null) ? Ast.Call(JitRuntime.M(name), l, r) : null;
            }
            var call = e as MSA.MethodCallExpression;
            if (call != null && call.Object == null && call.Arguments.Count == 2) {
                string name = (call.Method.Name == "DivLong") ? "DivInt"
                    : (call.Method.Name == "ModLong") ? "ModInt" : null;
                if (name == null) { return null; }
                var l = Narrowed(call.Arguments[0]);
                var r = Narrowed(call.Arguments[1]);
                return (l != null && r != null) ? Ast.Call(JitRuntime.M(name), l, r) : null;
            }
            return null;
        }

        /// <summary>The int this long operand was widened from, if it was one.</summary>
        private static MSA.Expression Narrowed(MSA.Expression/*!*/ e) {
            var unary = e as MSA.UnaryExpression;
            if (unary != null && unary.NodeType == MSA.ExpressionType.Convert
                && unary.Type == typeof(long) && unary.Operand.Type == typeof(int)) {
                return unary.Operand;
            }
            var constant = e as MSA.ConstantExpression;
            if (constant != null && constant.Type == typeof(long)) {
                long v = (long)constant.Value;
                if (v >= Int32.MinValue && v <= Int32.MaxValue) { return Ast.Constant((int)v, typeof(int)); }
            }
            return null;
        }

        // ---- the operators the body inlines must be the built-in ones ----------------------
        // Inlining `a + b' as a CLR add is only right while Integer#+ is the library method.
        // GlobalMethodVersion covers a redefinition AFTER specialization - the entry guard then
        // fails - but not one that happened before: a method that first turns hot after
        // `class Integer; def +(o) ... end; end' would otherwise inline the operator the program
        // replaced. So every operator is resolved, at compile time, on the class of its receiver,
        // and must be a library method registered under that very name (which also rejects
        // `alias_method :-, :+', a library method under someone else's name).

        private RubyClass/*!*/ ClassOfType(JT t, bool value) {
            object sample = (t == JT.Dbl) ? (object)0.0 : (t == JT.Bool) ? (object)value : (object)0;
            return JitRuntime.ClassOf(_context, sample);
        }

        private static bool IsBuiltinMethod(RubyClass/*!*/ cls, string/*!*/ name) {
            var resolved = cls.ResolveMethod(name, VisibilityContext.AllVisible);
            var info = resolved.Found ? resolved.Info as RubyLibraryMethodInfo : null;
            if (info == null) { return false; }
            foreach (var member in info.GetMembers()) {
                bool named = false;
                foreach (RubyMethodAttribute a in member.GetCustomAttributes(typeof(RubyMethodAttribute), false)) {
                    if (a.Name == name) { named = true; break; }
                }
                if (!named) { return false; }
            }
            return true;
        }

        /// <summary>`op' called on a receiver of type t is the built-in method; bail out if not.</summary>
        private void RequireBuiltin(JT t, string/*!*/ op) {
            bool ok = (t == JT.Bool)
                ? IsBuiltinMethod(ClassOfType(t, true), op) && IsBuiltinMethod(ClassOfType(t, false), op)
                : IsBuiltinMethod(ClassOfType(t, false), op);
            // BasicObject#!= answers by calling ==.
            if (ok && op == "!=") { RequireBuiltin(t, "=="); }
            if (!ok) { throw Bail("operator " + op + " on " + t + " is not the built-in one"); }
        }

        private MSA.Expression/*!*/ EmitBinary(string/*!*/ op, MSA.Expression/*!*/ l, JT lt, MSA.Expression/*!*/ r, JT rt, out JT type) {
            JT operand = Unify(lt, rt);
            if (operand == JT.Obj) { throw Bail("operator " + op + " on " + lt + ", " + rt); }
            // Ruby dispatches on the receiver: `1 + 2.0' is Integer#+.
            RequireBuiltin(lt, op);

            if (operand == JT.Dbl) {
                l = Coerce(l, lt, JT.Dbl);
                r = Coerce(r, rt, JT.Dbl);
                switch (op) {
                    case "+": type = JT.Dbl; return Ast.Add(l, r);
                    case "-": type = JT.Dbl; return Ast.Subtract(l, r);
                    case "*": type = JT.Dbl; return Ast.Multiply(l, r);
                    case "/": type = JT.Dbl; return Ast.Call(JitRuntime.M("DivDouble"), l, r);
                    case "%": type = JT.Dbl; return Ast.Call(JitRuntime.M("ModDouble"), l, r);
                    default: return EmitCompare(op, l, r, out type);
                }
            }

            // Integers. `operand' is Int exactly when both sides are, and that is the one case
            // in which the CLR operation cannot overflow a long and so needs no check at all:
            // two Int32s added, subtracted or multiplied in 64 bits always fit. So `int op int'
            // that overflows produces a long rather than deopting, which is what the runtime
            // does too - it is only the step out of Int64 that has nowhere typed left to go.
            bool bothInt = (operand == JT.Int);
            switch (op) {
                case "<": case ">": case "<=": case ">=": case "==": case "!=":
                    // A comparison never overflows, so two ints are compared as ints and a mixed
                    // pair widens to long. Both are exact.
                    return EmitCompare(op, Coerce(l, lt, operand), Coerce(r, rt, operand), out type);

                case "&": case "|": case "^": {
                    // Bitwise operations on integers stay within the range of their operands.
                    type = operand;
                    var bl = Coerce(l, lt, operand);
                    var br = Coerce(r, rt, operand);
                    if (op == "&") { return Ast.And(bl, br); }
                    if (op == "|") { return Ast.Or(bl, br); }
                    return Ast.ExclusiveOr(bl, br);
                }
            }

            l = Coerce(l, lt, JT.Lng);
            r = Coerce(r, rt, JT.Lng);
            type = JT.Lng;
            switch (op) {
                case "+": if (bothInt) { return Ast.Add(l, r); } _canDeopt = true; return Ast.Call(JitRuntime.M("AddLong"), l, r);
                case "-": if (bothInt) { return Ast.Subtract(l, r); } _canDeopt = true; return Ast.Call(JitRuntime.M("SubLong"), l, r);
                case "*": if (bothInt) { return Ast.Multiply(l, r); } _canDeopt = true; return Ast.Call(JitRuntime.M("MulLong"), l, r);
                case "/": _canDeopt = true; return Ast.Call(JitRuntime.M("DivLong"), l, r);
                case "%": _canDeopt = true; return Ast.Call(JitRuntime.M("ModLong"), l, r);
                default: throw Bail("integer operator " + op);
            }
        }

        private MSA.Expression/*!*/ EmitCompare(string/*!*/ op, MSA.Expression/*!*/ l, MSA.Expression/*!*/ r, out JT type) {
            type = JT.Bool;
            switch (op) {
                case "<": return Ast.LessThan(l, r);
                case ">": return Ast.GreaterThan(l, r);
                case "<=": return Ast.LessThanOrEqual(l, r);
                case ">=": return Ast.GreaterThanOrEqual(l, r);
                case "==": return Ast.Equal(l, r);
                case "!=": return Ast.NotEqual(l, r);
                default: throw Bail("float operator " + op);
            }
        }
    }

    /// <summary>
    /// The static pre-screen, asked once per method before it is wrapped: *could* this body ever
    /// compile?
    ///
    /// Why it exists. Without it the only answer comes from TryCompile, which cannot run until
    /// the profiler has seen Threshold calls - and by then the trampoline is the delegate the
    /// runtime cached in RubyMethodBody._delegate and baked into call site rules, so even a
    /// perfect give-up leaves one permanent extra indirect call on a method the JIT never helps.
    /// Most rejections are not about types at all: `def fib(n) ... return n ... end' is declined
    /// only because Emit has no case for ReturnStatement, and no amount of profiling will change
    /// that. Those methods are better off never being wrapped.
    ///
    /// The contract. This walk rejects only what JitCompiler.Emit refuses for reasons that are
    /// visible in the syntax tree: a node kind it does not dispatch on, an operator EmitBinary
    /// does not know, a rescue/else/ensure, a `do' block, a post-test while, a local from an
    /// enclosing scope. Wherever Emit's answer depends on the *types* the profiler will observe -
    /// "is this operand numeric", "is this condition a bool", "did the self-call's return type
    /// settle" - the screen says yes. It is a pre-screen, not a decision: a false yes costs what
    /// the old code always cost, a false no costs a specialization.
    ///
    /// KEEPING IT HONEST. It is directly below Emit because the two have to move together, and
    /// each direction of drift is a statistic rather than silent slowness (IR_JIT_VERBOSE=1):
    ///   - too permissive (the screen passes a body Emit then refuses): the method is wrapped,
    ///     profiled and declined, which is JitRuntime.Rejected, and each one is named on stderr.
    ///     With the screen in place that count should be near zero.
    ///   - too strict (Emit grew a case the screen was not told about): the node kind appears in
    ///     the "screen rejected" histogram printed on the way out, so a newly supported node kind
    ///     sitting at the top of that histogram is the drift, visible without reading any code.
    /// </summary>
    internal sealed class JitScreen {
        private readonly int _scopeDepth;
        private readonly string/*!*/ _methodName;
        private readonly int _arity;
        private int _nodes;

        /// <summary>The node kind that said no, for the histogram.</summary>
        private string _rejectedBy;

        private JitScreen(MethodDeclaration/*!*/ ast) {
            _scopeDepth = ast.DefinedScope.Depth;
            _methodName = ast.Name;
            _arity = ast.Parameters.Mandatory.Length;
        }

        /// <summary>
        /// False when no assignment of parameter types could ever make TryCompile succeed for
        /// this body. <paramref name="rejectedBy"/> names the node kind that decided it.
        /// </summary>
        internal static bool CanEverCompile(MethodDeclaration/*!*/ ast, out string rejectedBy) {
            var s = new JitScreen(ast);
            bool ok;
            try {
                ok = s.WalkBody(ast.Body);
            } catch (Exception) {
                // A screen that throws must not take the process down with it; it just means the
                // method is not wrapped.
                ok = false;
                s._rejectedBy = "screen-error";
            }
            rejectedBy = s._rejectedBy;
            return ok;
        }

        private bool No(object/*!*/ node) {
            if (_rejectedBy == null) { _rejectedBy = node.GetType().Name; }
            return false;
        }

        /// <summary>Mirrors JitCompiler.EmitBody.</summary>
        private bool WalkBody(Body/*!*/ body) {
            if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                return No(body);
            }
            return WalkStatements(body.Statements);
        }

        private bool WalkStatements(Statements statements) {
            if (statements == null) { return true; }
            foreach (var s in statements) {
                if (!Walk(s)) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Mirrors JitCompiler.EmitCondition, whose operand has to end up Bool or numeric. Only
        /// the forms that are unconditionally JT.Obj can be ruled out here - `if @x', `while
        /// self', `x ? a : b' on nil - and those are common enough to be worth ruling out.
        /// </summary>
        private bool WalkCondition(RExpr/*!*/ node) {
            if (node is InstanceVariable || node is SelfReference) { return No(node); }
            var literal = node as Literal;
            if (literal != null && literal.Value == null) { return No(node); }
            return Walk(node);
        }

        /// <summary>
        /// An operand of a binary operator, or the right side of `x op= v': EmitCall and
        /// EmitAssignment only go on when both sides are numeric, and an instance variable, self,
        /// or a nil/true/false literal never is - `@count + 1' is refused whatever gets profiled.
        /// </summary>
        private bool WalkOperand(RExpr/*!*/ node) {
            if (node is InstanceVariable || node is SelfReference) { return No(node); }
            var literal = node as Literal;
            if (literal != null && !(literal.Value is int || literal.Value is long || literal.Value is double)) { return No(node); }
            return Walk(node);
        }

        /// <summary>
        /// Mirrors JitCompiler.Emit, case for case and in the same order. Read the two side by
        /// side; that is the point of them being in one file.
        /// </summary>
        private bool Walk(RExpr/*!*/ node) {
            // Emit gives up past MaxNodes. The screen's count is not exactly Emit's, so it only
            // rules out a body that is too big by any counting - the walk stays bounded either way.
            if (++_nodes > 2 * JitCompiler.MaxNodes) { return No(node); }

            var literal = node as Literal;
            if (literal != null) {
                object v = literal.Value;
                return (v is int || v is long || v is double || v is bool || v == null) || No(node);
            }

            var local = node as LocalVariable;
            if (local != null) {
                // A local from an enclosing scope is never read from a typed body. (Emit also
                // bails on a read before any assignment, which is a flow property: say yes.)
                return local.DefinitionLexicalDepth == _scopeDepth || No(node);
            }

            var assign = node as SimpleAssignmentExpression;
            if (assign != null) {
                string op = assign.Operation;
                if (op == "&&" || op == "||") { return No(node); }
                // `x op= v' becomes `x = x op v', so an operator EmitBinary does not know is out.
                if (op != null && !IsBinaryOperator(op)) { return No(node); }

                var ivarTarget = assign.Left as InstanceVariable;
                if (ivarTarget != null) {
                    return (op == null) ? Walk(assign.Right) : No(node);
                }
                var target = assign.Left as LocalVariable;
                if (target == null || target.DefinitionLexicalDepth != _scopeDepth) { return No(node); }
                return (op != null) ? WalkOperand(assign.Right) : Walk(assign.Right);
            }

            var call = node as MethodCall;
            if (call != null) { return WalkCall(call); }

            var cond = node as ConditionalExpression;
            if (cond != null) {
                return WalkCondition(cond.Condition) && Walk(cond.TrueExpression) && Walk(cond.FalseExpression);
            }

            var ifExpr = node as IfExpression;
            if (ifExpr != null) {
                var clauses = ifExpr.ElseIfClauses;
                if (clauses != null) {
                    for (int i = 0; i < clauses.Count; i++) {
                        var c = clauses[i];
                        if (c.Condition == null) {
                            // Only the last clause may be the `else'.
                            if (i != clauses.Count - 1) { return No(c); }
                        } else if (!WalkCondition(c.Condition)) {
                            return false;
                        }
                        if (!WalkStatements(c.Statements)) { return false; }
                    }
                }
                return WalkCondition(ifExpr.Condition) && WalkStatements(ifExpr.Body);
            }

            var unless = node as UnlessExpression;
            if (unless != null) {
                var els = unless.ElseClause;
                if (els != null) {
                    if (els.Condition != null) { return No(els); }
                    if (!WalkStatements(els.Statements)) { return false; }
                }
                return WalkCondition(unless.Condition) && WalkStatements(unless.Statements);
            }

            var not = node as NotExpression;
            if (not != null) { return WalkCondition(not.Expression); }

            var and = node as AndExpression;
            if (and != null) { return WalkCondition(and.Left) && WalkCondition(and.Right); }

            var or = node as OrExpression;
            if (or != null) { return WalkCondition(or.Left) && WalkCondition(or.Right); }

            if (node is InstanceVariable || node is SelfReference) { return true; }

            var loop = node as WhileLoopExpression;
            if (loop != null) {
                if (loop.IsPostTest) { return No(node); }
                return WalkCondition(loop.Condition) && WalkStatements(loop.Statements);
            }

            // `return' / `return v'. Emit's ReturnStatement case lands in a parallel change; the
            // screen already says yes so that the two merge into a working whole. Until then a
            // method with a `return' is wrapped and declined, which is what it always was. A
            // multi-value return builds an Array, which no typed body produces.
            var ret = node as ReturnStatement;
            if (ret != null) {
                var retArgs = ret.Arguments;
                if (retArgs == null || retArgs.Expressions.Length == 0) { return true; }
                if (retArgs.Expressions.Length != 1) { return No(node); }
                return Walk(retArgs.Expressions[0]);
            }

            // break/next are only emitted in loop mode, which a method body never is, so they
            // fall through to the rejection below along with every other unknown node.

            var body = node as Body;
            if (body != null) { return WalkBody(body); }

            return No(node);
        }

        /// <summary>Mirrors JitCompiler.EmitCall: a self-recursive call, or a binary operator.</summary>
        private bool WalkCall(MethodCall/*!*/ node) {
            if (node.Block != null) { return No(node); }
            var args = node.Arguments;
            int argc = (args == null) ? 0 : args.Expressions.Length;

            if (node.Target == null) {
                if (node.IsVariableCall || node.MethodName != _methodName || argc != _arity) { return No(node); }
            } else if (argc != 1 || !IsBinaryOperator(node.MethodName)) {
                // Emit only reaches EmitBinary for a one-argument call on a target, and only
                // EmitBinary's own operators survive there whatever the operand types are.
                return No(node);
            } else {
                return WalkOperand(node.Target) && WalkOperand(args.Expressions[0]);
            }

            // The self-call: each argument is coerced to its parameter's profiled type, which is
            // a question about types, so any argument Emit can emit at all will do.
            for (int i = 0; i < argc; i++) {
                if (!Walk(args.Expressions[i])) { return false; }
            }
            return true;
        }

        /// <summary>The operators JitCompiler.EmitBinary and EmitCompare between them handle.</summary>
        private static bool IsBinaryOperator(string/*!*/ op) {
            switch (op) {
                case "+": case "-": case "*": case "/": case "%":
                case "<": case ">": case "<=": case ">=": case "==": case "!=":
                case "&": case "|": case "^":
                    return true;
                default:
                    return false;
            }
        }
    }
}
