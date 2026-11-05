/* ****************************************************************************
 *
 * Definite assignment, for the locals compiled code keeps in variables that outlive the Ruby scope
 * they belong to: a block's locals in the loop BlockLoopSite makes of it, or that the loop compiler
 * runs inline (JitCompiler.EmitInlinedIterator), and an inlined callee's locals (JitCompiler.Inline.cs).
 *
 * In Ruby each of those is nil whenever the block is called or the method entered, whatever it held
 * the last time. A variable of the compiled loop holds what the previous iteration left in it. The two
 * cannot be told apart if no read of such a local can happen, on any path, before an assignment of it
 * in the same call - which is what Check proves, or fails to. `t = a[x] if c; t = 0 unless c' is not
 * enough (the analysis does not relate conditions), `if c; t = 1; else; t = 2; end' is.
 *
 * Conservative throughout: an assignment counts only where it surely happens (both arms of an if, not
 * the right of an &&, not a loop's body, not a block), and a node it does not know is walked for reads
 * only, so a local assigned inside one of those is not assigned as far as anything later is concerned.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Compiler.Ast;

namespace IronRuby.Runtime.Osr {
    internal sealed class DefiniteAssignment {
        private readonly Func<LocalVariable, bool>/*!*/ _isFresh;
        private bool _failed;

        private DefiniteAssignment(Func<LocalVariable, bool>/*!*/ isFresh) {
            _isFresh = isFresh;
        }

        private static string/*!*/ Key(LocalVariable/*!*/ local) {
            return local.Name + "@" + local.DefinitionLexicalDepth;
        }

        /// <summary>
        /// Whether no local <paramref name="isFresh"/> picks is read in <paramref name="statements"/> before an
        /// assignment of it, given that <paramref name="assigned"/> are assigned on the way in.
        /// </summary>
        internal static bool Check(IEnumerable<Expression>/*!*/ statements, Func<LocalVariable, bool>/*!*/ isFresh,
            IEnumerable<LocalVariable>/*!*/ assigned) {
            var analysis = new DefiniteAssignment(isFresh);
            var d = new HashSet<string>();
            foreach (var local in assigned) {
                if (local != null) { d.Add(Key(local)); }
            }
            analysis.Statements(statements, d);
            return !analysis._failed;
        }

        // A set of assigned locals; null after a jump (`return', `next'...): no path reaches what follows,
        // which is the set of everything.
        private static HashSet<string> Merge(HashSet<string> a, HashSet<string> b) {
            if (a == null) { return b; }
            if (b == null) { return a; }
            var r = new HashSet<string>(a);
            r.IntersectWith(b);
            return r;
        }

        private HashSet<string> Statements(IEnumerable<Expression> statements, HashSet<string> d) {
            if (statements == null) { return d; }
            foreach (var s in statements) {
                if (d == null || _failed) { return d; }
                d = Expr(s, d);
            }
            return d;
        }

        private void Read(LocalVariable/*!*/ local, HashSet<string> d) {
            if (d != null && _isFresh(local) && !d.Contains(Key(local))) { _failed = true; }
        }

        private HashSet<string> Expr(Expression node, HashSet<string> d) {
            if (node == null || d == null || _failed) { return d; }

            var local = node as LocalVariable;
            if (local != null) {
                Read(local, d);
                return d;
            }

            var assignment = node as SimpleAssignmentExpression;
            if (assignment != null) {
                var target = assignment.Left as LocalVariable;
                if (target != null) {
                    // `x op= v' and `x ||= v' read x first
                    if (assignment.Operation != null) { Read(target, d); }
                    d = Expr(assignment.Right, d);
                    if (d == null) { return null; }
                    var after = new HashSet<string>(d);
                    after.Add(Key(target));
                    return after;
                }
                // a[i] = v, o.x = v, @x = v: their parts are read, in order
                var element = assignment.Left as ArrayItemAccess;
                if (element != null) {
                    d = Expr(element.Array, d);
                    if (element.Arguments != null) {
                        foreach (var a in element.Arguments.Expressions) { d = Expr(a, d); }
                    }
                } else {
                    var attribute = assignment.Left as AttributeAccess;
                    if (attribute != null) {
                        d = Expr(attribute.Qualifier, d);
                    } else if (!(assignment.Left is InstanceVariable)) {
                        ReadsOnly(assignment.Left, d);
                    }
                }
                return Expr(assignment.Right, d);
            }

            var ifExpr = node as IfExpression;
            if (ifExpr != null) {
                var cond = Expr(ifExpr.Condition, d);
                HashSet<string> result = Statements(ifExpr.Body, cond);
                bool hasElse = false;
                var clauses = ifExpr.ElseIfClauses;
                if (clauses != null) {
                    foreach (var clause in clauses) {
                        if (clause.Condition == null) {
                            hasElse = true;
                            result = Merge(result, Statements(clause.Statements, cond));
                        } else {
                            cond = Expr(clause.Condition, cond);
                            result = Merge(result, Statements(clause.Statements, cond));
                        }
                    }
                }
                return hasElse ? result : Merge(result, cond);
            }

            var unless = node as UnlessExpression;
            if (unless != null) {
                var cond = Expr(unless.Condition, d);
                var then = Statements(unless.Statements, cond);
                var els = unless.ElseClause;
                return Merge(then, (els != null && els.Condition == null) ? Statements(els.Statements, cond) : cond);
            }

            var conditional = node as ConditionalExpression;
            if (conditional != null) {
                var cond = Expr(conditional.Condition, d);
                return Merge(Expr(conditional.TrueExpression, cond), Expr(conditional.FalseExpression, cond));
            }

            var and = node as AndExpression;
            if (and != null) {
                var left = Expr(and.Left, d);
                Expr(and.Right, left);
                return left;
            }
            var or = node as OrExpression;
            if (or != null) {
                var left = Expr(or.Left, d);
                Expr(or.Right, left);
                return left;
            }
            var not = node as NotExpression;
            if (not != null) { return Expr(not.Expression, d); }

            var loop = node as WhileLoopExpression;
            if (loop != null) {
                // the body may run no time at all (or, after the condition, once): what it assigns does not count
                if (loop.IsPostTest) {
                    var afterBody = Statements(loop.Statements, d);
                    return Expr(loop.Condition, afterBody ?? d) ?? d;
                }
                var cond = Expr(loop.Condition, d);
                Statements(loop.Statements, cond);
                return cond;
            }

            var sequence = node as BlockExpression;
            if (sequence != null) { return Statements(sequence.Statements, d); }

            var body = node as Body;
            if (body != null && body.RescueClauses == null && body.EnsureStatements == null && body.ElseStatements == null) {
                return Statements(body.Statements, d);
            }

            var jump = node as JumpStatement;
            if (jump != null) {
                if (jump.Arguments != null) {
                    foreach (var a in jump.Arguments.Expressions) { d = Expr(a, d); }
                }
                return null;
            }

            var call = node as MethodCall;
            if (call != null) {
                d = Expr(call.Target, d);
                if (call.Arguments != null) {
                    foreach (var a in call.Arguments.Expressions) { d = Expr(a, d); }
                }
                // a block's body reads what is assigned when the call is made, and assigns nothing for later
                var block = call.Block as BlockDefinition;
                if (block != null) {
                    Statements(block.Body, d);
                } else if (call.Block != null) {
                    ReadsOnly(call.Block, d);
                }
                return d;
            }

            ReadsOnly(node, d);
            return d;
        }

        /// <summary>A node the analysis does not know: every local in it is a read.</summary>
        private void ReadsOnly(Node/*!*/ node, HashSet<string> d) {
            if (d == null || _failed) { return; }
            node.Walk(new ReadWalker(this, d));
        }

        private sealed class ReadWalker : Walker {
            private readonly DefiniteAssignment/*!*/ _owner;
            private readonly HashSet<string>/*!*/ _assigned;

            internal ReadWalker(DefiniteAssignment/*!*/ owner, HashSet<string>/*!*/ assigned) {
                _owner = owner;
                _assigned = assigned;
            }

            public override bool Enter(LocalVariable/*!*/ node) {
                _owner.Read(node, _assigned);
                return true;
            }

            public override bool Enter(MethodCall/*!*/ node) {
                var block = node.Block as BlockDefinition;
                if (block != null) {
                    foreach (var s in block.Body) { s.Walk(this); }
                }
                return true;
            }
        }
    }
}
