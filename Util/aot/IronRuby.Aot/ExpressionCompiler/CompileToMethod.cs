using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq.Expressions;
using System.Reflection.Emit;

namespace IronRuby.Aot.Compiler {
    /// <summary>
    /// LambdaExpression.CompileToMethod for .NET Core: compiles a lambda into the body of a static
    /// MethodBuilder (e.g. one of a PersistedAssemblyBuilder's), setting its signature. Nested
    /// lambdas become private static methods of the same type. Constants must be IL literals
    /// (primitives, strings, null, Type, MethodBase); anything else throws, as it did on .NET
    /// Framework. DynamicExpressions must be rewritten away first.
    /// </summary>
    public static class ExpressionCompiler {
        public static void CompileToMethod(LambdaExpression lambda, MethodBuilder method) {
            if (!method.IsStatic) throw new ArgumentException("method must be static", nameof(method));
            LambdaCompiler.Compile(lambda, method, null);
        }

        private static readonly object _jitLock = new object();

        /// <summary>
        /// The same for a caller that compiles at run time, from any thread (the method JIT, -X:JITMethods):
        /// one compilation at a time, and nothing kept of it afterwards. A call the tree makes to a key of
        /// <paramref name="substitutions"/> is emitted as a call to its value: a tree cannot name a
        /// MethodBuilder (Expression.Call asks it for its parameters, which it has none of until its type
        /// is created), so a method's call to itself names a stand-in of the same signature.
        /// </summary>
        public static void CompileToMethod(LambdaExpression lambda, MethodBuilder method, IDictionary<MethodInfo, MethodInfo> substitutions) {
            if (!method.IsStatic) throw new ArgumentException("method must be static", nameof(method));
            lock (_jitLock) {
                LambdaCompiler.MethodSubstitutions = substitutions;
                try {
                    LambdaCompiler.Compile(lambda, method, null);
                } finally {
                    LambdaCompiler.MethodSubstitutions = null;
                    LambdaCompiler.CreatedMethods.Clear();
                }
            }
        }

        /// <summary>
        /// What LambdaExpression.Compile does - a DynamicMethod behind a delegate, its constants in a
        /// closure - except that a call the tree makes to <paramref name="selfCall"/> is emitted as a
        /// call of the DynamicMethod itself, with its closure (-X:JITMethods=dynamic).
        /// </summary>
        public static Delegate CompileDynamic(LambdaExpression lambda, MethodInfo selfCall) {
            lock (_jitLock) {
                LambdaCompiler.SelfCall = selfCall;
                try {
                    return LambdaCompiler.Compile(lambda, null);
                } finally {
                    LambdaCompiler.SelfCall = null;
                }
            }
        }
    }
}
