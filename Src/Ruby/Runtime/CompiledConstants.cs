/* ****************************************************************************
 *
 * Constants a compiled Ruby body loads without a cast (IR_SEALED_CONSTANTS).
 *
 * LambdaExpression.Compile keeps a tree's object constants in its closure's object[] and
 * loads one with a bounds check and a castclass to the constant's type. In Ruby's compiled
 * bodies the .NET JIT makes each of those a call of the runtime's cast helper (about 17
 * instructions), sealed class or not - it expands such a cast inline in a small lambda, and
 * why not here was not found (perf/lconst). Every dynamic call site of a body is such a
 * constant, loaded each time the call is made: 7 casts a call of a light method that calls one
 * method, 19-24 for `inject' and `tap''s callers - 7-12% of their instructions (callgrind,
 * Util/perf/LEDGER.md: perf/lconst).
 *
 * So, just before an interpreted lambda is compiled to IL (the DLR's LightCompilation.Rewriter,
 * Build/dlr-patches/0111), its tree is rewritten so that a constant of a class is loaded as an
 * object, which needs no cast, and given its class with Unsafe.As, which costs nothing: sound,
 * since a ConstantExpression's value is of its type, and this rewriter makes the call sites'
 * constants itself. A dynamic expression becomes the call LambdaCompiler would emit for it - its
 * site, then the site's Target with the site and the arguments - with its site such a constant.
 * The extension nodes are reduced on the way, as the compiler would reduce them. The interpreter
 * goes on running the tree as it was.
 *
 * ***************************************************************************/

using System;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace IronRuby.Runtime {
    // a DynamicExpressionVisitor: DynamicExpression.Accept calls VisitDynamic only for one; for any other visitor it is an
    // extension node, and reducing it gives the site constant twice (`Constant(site).Target(Constant(site), ...)'), two casts
    internal sealed class CompiledConstantsRewriter : DynamicExpressionVisitor {
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("IR_SEALED_CONSTANTS") != "0";

        /// <summary>Installs the rewriter for the whole process (it holds no state of any runtime): once is enough.</summary>
        internal static void Install() {
            if (Enabled && Microsoft.Scripting.Interpreter.LightCompilation.Rewriter == null) {
                Microsoft.Scripting.Interpreter.LightCompilation.Rewriter = Rewrite;
            }
        }

        internal static LambdaExpression/*!*/ Rewrite(LambdaExpression/*!*/ lambda) {
            if (!Enabled) {
                return lambda;
            }
            try {
                return (LambdaExpression)new CompiledConstantsRewriter().Visit(lambda);
            } catch (TooDeepException) {
                // compiled as it is: LambdaCompiler goes on deep trees on a new thread of its own (StackGuard)
                return lambda;
            }
        }

        // A visitor recurses once a level of the tree, and has no StackGuard as LambdaCompiler does: a body nested some 500
        // levels deep (DeepNesting1, begin/rescue compiled up front) overflowed the stack of the thread that compiled it.
        private sealed class TooDeepException : Exception { }

        public override Expression Visit(Expression node) {
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) {
                throw new TooDeepException();
            }
            return base.Visit(node);
        }

        protected override Expression/*!*/ VisitExtension(Expression/*!*/ node) {
            // the compiler would reduce it; reduced here, its sites and constants are seen
            return node.CanReduce ? Visit(node.ReduceAndCheck()) : base.VisitExtension(node);
        }

        protected override Expression/*!*/ VisitDynamic(DynamicExpression/*!*/ node) {
            ReadOnlyCollection<Expression> arguments = Visit(node.Arguments);
            Type delegateType = node.DelegateType;
            MethodInfo invoke = delegateType.GetMethod("Invoke");
            ParameterInfo[] parameters = invoke.GetParameters();
            if (parameters.Length != arguments.Count + 1 || invoke.ReturnType != node.Type || !delegateType.IsVisible) {
                return node.Update(arguments);
            }
            foreach (var parameter in parameters) {
                if (parameter.ParameterType.IsByRef) {
                    return node.Update(arguments);
                }
            }

            // what LambdaCompiler.EmitDynamicExpression does: the site, its Target, then the arguments
            var site = CallSite.Create(delegateType, node.Binder);
            var siteVariable = Expression.Variable(site.GetType(), "#site");
            var callArguments = new Expression[arguments.Count + 1];
            callArguments[0] = siteVariable;
            for (int i = 0; i < arguments.Count; i++) {
                callArguments[i + 1] = arguments[i];
            }
            return Expression.Block(node.Type, new[] { siteVariable },
                Expression.Assign(siteVariable, Uncast(site, site.GetType())),
                Expression.Invoke(Expression.Field(siteVariable, "Target"), callArguments)
            );
        }

        protected override Expression/*!*/ VisitConstant(ConstantExpression/*!*/ node) {
            object value = node.Value;
            Type type = node.Type;
            // what the compiler emits without a constant of the closure (ILGen.CanEmitConstant), a value type, an object
            if (value == null || type.IsValueType || type == typeof(object) || type == typeof(string) ||
                typeof(Type).IsAssignableFrom(type) || typeof(MethodBase).IsAssignableFrom(type) || type.IsByRef || type.IsPointer) {
                return node;
            }
            return Uncast(value, type);
        }

        private static readonly MethodInfo/*!*/ _UnsafeAs = Array.Find(typeof(Unsafe).GetMethods(),
            m => m.Name == nameof(Unsafe.As) && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(object));

        // `Unsafe.As<type>(value as an object)': no castclass, and the JIT's intrinsic is no code
        private static Expression/*!*/ Uncast(object/*!*/ value, Type/*!*/ type) {
            return Expression.Call(_UnsafeAs.MakeGenericMethod(type), Expression.Constant(value, typeof(object)));
        }
    }
}
