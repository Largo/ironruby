/* ****************************************************************************
 *
 * -X:JITMethods - a prototype: the method JIT's code as static methods of a runtime assembly.
 *
 * -X:JIT compiles a typed body with LambdaExpression.Compile, which makes a DynamicMethod
 * behind a delegate; the body's call to itself (`fib(n - 1)' in `def fib') is then a delegate
 * invocation through JitCode.SelfCell, and the .NET JIT can inline neither that call nor any
 * other call from one compiled Ruby method into another. With -X:JITMethods the same tree is
 * emitted into a static method of a type in an AssemblyBuilder, and the self-call is an IL
 * `call' of that method.
 *
 *   -X:JITMethods            a non-collectible assembly (AssemblyBuilderAccess.Run), shared by
 *                            every method: nothing emitted is ever unloaded
 *   -X:JITMethods=collect    an assembly of its own for every body (RunAndCollect), unloaded
 *                            once nothing refers to it
 *   -X:JITMethods=dynamic    no assembly at all: a DynamicMethod as before, but one that calls
 *                            itself by `call' and not through the cell - the control for how
 *                            much of the gain is the call and how much the assembly
 *   ...,entry                the entry is emitted as well (JitStub.BuildEntry), and calls the
 *                            typed body directly; without it the entry stays a DynamicMethod
 *                            that reaches the body through a delegate, as before
 *   ...,tiered               the methods are left to tiered compilation; without it they are
 *                            compiled fully optimized at once, as a DynamicMethod is
 *   ...,readonly             the lifted constants are static readonly fields, set by the
 *                            type's initializer, not plain static fields set by reflection
 *
 * What a tree holds that IL cannot: live objects (the context, the instance variable and
 * attribute sites, a pinned class, boxed literals, a symbol). Each becomes a static field of
 * the emitted type, assigned once the type is created (Lifter).
 *
 * The compiler that emits a tree into a MethodBuilder is the port of the .NET Framework's
 * LambdaCompiler in Util/aot/IronRuby.Aot/ExpressionCompiler; System.Linq.Expressions on .NET
 * has no CompileToMethod. It is not part of IronRuby.dll: Src/JitEmit builds it into
 * IronRuby.JitEmit.dll, which is loaded from next to IronRuby.dll the first time the switch
 * needs it. Without that file a body is compiled as it is without the switch.
 *
 * A frame of an emitted body is a Ruby frame in a backtrace as a DynamicMethod's is: the method
 * keeps the lambda's name, which is what RubyStackTraceBuilder decodes.
 *
 * Known gaps of the prototype (see Util/bench/jit-methods.md):
 *   - the Run assembly, and the constants its fields hold - the RubyContext among them - live
 *     as long as the process, whatever becomes of the runtime that made them;
 *   - the entry JitKeywordCall calls (JitStub.BuildDirectEntry) and -X:OSR's loops are still
 *     DynamicMethods.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;

namespace System.Runtime.CompilerServices {
    /// <summary>
    /// The runtime skips access checks from an assembly carrying this attribute to the one it names,
    /// whichever assembly defines the attribute (System.Reflection.DispatchProxy has its own copy
    /// too). The emitted code calls JitRuntime's helpers and holds its sites, which are internal -
    /// a DynamicMethod gets the same with its skipVisibility flag.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute {
        public IgnoresAccessChecksToAttribute(string assemblyName) {
            AssemblyName = assemblyName;
        }

        public string AssemblyName { get; private set; }
    }
}

namespace IronRuby.Runtime.Jit {
    internal sealed class JitMethodEmitter {
        private readonly string/*!*/ _options;
        private readonly bool _collectible, _tiered, _readonly, _dynamic;
        internal readonly bool Entries;

        internal static int Emitted, Failed;

        private JitMethodEmitter(string/*!*/ options) {
            _options = options;
            foreach (string option in options.Split(',')) {
                switch (option) {
                    case "collect": _collectible = true; break;
                    case "dynamic": _dynamic = true; break;
                    case "entry": Entries = true; break;
                    case "tiered": _tiered = true; break;
                    case "readonly": _readonly = true; break;
                }
            }
        }

        // One option string per runtime, and nearly always one runtime.
        private static JitMethodEmitter _last;

        /// <summary>The emitter for the context's -X:JITMethods, null without the switch.</summary>
        internal static JitMethodEmitter For(RubyContext/*!*/ context) {
            string options = context.RubyOptions.JitMethods;
            if (options == null) { return null; }
            var emitter = _last;
            if (emitter == null || emitter._options != options) {
                _last = emitter = new JitMethodEmitter(options);
            }
            return emitter;
        }

        // ---- the tree compiler ---------------------------------------------------------------

        private static Action<LambdaExpression, MethodBuilder, IDictionary<MethodInfo, MethodInfo>> _compiler;
        private static bool _compilerMissing;

        private static Action<LambdaExpression, MethodBuilder, IDictionary<MethodInfo, MethodInfo>> Compiler() {
            if (_compiler != null || _compilerMissing) { return _compiler; }
            try {
                string directory = Path.GetDirectoryName(typeof(JitMethodEmitter).Assembly.Location);
                var assembly = Assembly.LoadFrom(Path.Combine(directory, "IronRuby.JitEmit.dll"));
                var method = assembly.GetType("IronRuby.Aot.Compiler.ExpressionCompiler", true).GetMethod("CompileToMethod",
                    new[] { typeof(LambdaExpression), typeof(MethodBuilder), typeof(IDictionary<MethodInfo, MethodInfo>) });
                _compiler = (Action<LambdaExpression, MethodBuilder, IDictionary<MethodInfo, MethodInfo>>)method.CreateDelegate(
                    typeof(Action<LambdaExpression, MethodBuilder, IDictionary<MethodInfo, MethodInfo>>));
            } catch (Exception e) {
                _compilerMissing = true;
                Console.Error.WriteLine("ironruby: warning: -X:JITMethods needs IronRuby.JitEmit.dll next to IronRuby.dll ({0}); compiling as without it",
                    e.Message);
            }
            return _compiler;
        }

        private static Func<LambdaExpression, MethodInfo, Delegate> _dynamicCompiler;

        private static Func<LambdaExpression, MethodInfo, Delegate> DynamicCompiler() {
            if (_dynamicCompiler == null && Compiler() != null) {
                var method = _compiler.Method.DeclaringType.GetMethod("CompileDynamic", new[] { typeof(LambdaExpression), typeof(MethodInfo) });
                _dynamicCompiler = (Func<LambdaExpression, MethodInfo, Delegate>)method.CreateDelegate(typeof(Func<LambdaExpression, MethodInfo, Delegate>));
            }
            return _dynamicCompiler;
        }

        // ---- assemblies ----------------------------------------------------------------------

        // Everything below is done under this lock: a ModuleBuilder is not for two threads at once.
        private static readonly object/*!*/ _lock = new object();
        private static ModuleBuilder _runModule;
        private static int _id;

        private static readonly ConstructorInfo/*!*/ IgnoresAccessChecksTo =
            typeof(IgnoresAccessChecksToAttribute).GetConstructor(new[] { typeof(string) });

        private static ModuleBuilder/*!*/ NewModule(string/*!*/ name, AssemblyBuilderAccess access) {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), access);
            assembly.SetCustomAttribute(new CustomAttributeBuilder(IgnoresAccessChecksTo,
                new object[] { typeof(JitMethodEmitter).Assembly.GetName().Name }));
            return assembly.DefineDynamicModule(name);
        }

        private ModuleBuilder/*!*/ Module(int id) {
            if (_collectible) {
                return NewModule("IronRuby.JitCode." + id, AssemblyBuilderAccess.RunAndCollect);
            }
            return _runModule ?? (_runModule = NewModule("IronRuby.JitCode", AssemblyBuilderAccess.Run));
        }

        internal const string TypeNamePrefix = "Jit$";

        /// <summary>Whether a type is one this emitter made (for RubyStackTraceBuilder).</summary>
        internal static bool IsEmitted(Type/*!*/ type) {
            return type.Name.StartsWith(TypeNamePrefix, StringComparison.Ordinal) && type.Assembly.IsDynamic;
        }

        // The methods emitted for a typed body, by the delegate handed out for it: an entry emitted
        // later calls the method and not the delegate.
        private static readonly ConditionalWeakTable<Delegate, MethodInfo>/*!*/ _methods = new ConditionalWeakTable<Delegate, MethodInfo>();

        // ---- emitting ------------------------------------------------------------------------

        /// <summary>
        /// The typed body as a static method; its self-calls are calls of that method. Null if it
        /// could not be emitted, and the caller then compiles the lambda.
        /// </summary>
        internal Delegate EmitTyped(JitCode/*!*/ code) {
            return _dynamic ? EmitDynamic(code) : Emit(code.Lambda, code.SelfCell);
        }

        // dynamic: the same tree but for its self-calls, compiled to a DynamicMethod that calls itself
        private Delegate EmitDynamic(JitCode/*!*/ code) {
            var compiler = DynamicCompiler();
            if (compiler == null) { return null; }
            try {
                var rewriter = new Lifter(null, false) { SelfCell = code.SelfCell, SelfCall = SelfCallStandIn(code.Lambda) };
                var result = compiler((LambdaExpression)rewriter.Visit(code.Lambda), rewriter.SelfCall);
                Interlocked.Increment(ref Emitted);
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] compiled {0} as a DynamicMethod with {1} direct call(s)", code.Lambda.Name, rewriter.DirectCalls);
                }
                return result;
            } catch (Exception e) {
                Interlocked.Increment(ref Failed);
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] -X:JITMethods: {0} not compiled: {1}: {2}", code.Lambda.Name, e.GetType().Name, e.Message);
                }
                return null;
            }
        }

        /// <summary>
        /// The entry as a static method: a typed body it invokes through a delegate EmitTyped made
        /// is called directly, one it holds as a lambda is part of it as before.
        /// </summary>
        internal Delegate EmitEntry(LambdaExpression/*!*/ entry) {
            return Emit(entry, null);
        }

        private Delegate Emit(LambdaExpression/*!*/ lambda, StrongBox<Delegate> selfCell) {
            var compiler = Compiler();
            if (compiler == null) { return null; }
            try {
                lock (_lock) {
                    int id = ++_id;
                    string name = lambda.Name ?? "body";
                    // the method keeps the lambda's name, which is what a backtrace decodes (JitStub.Named)
                    var type = Module(id).DefineType(TypeNamePrefix + id,
                        TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.Class);
                    var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);

                    var substitutions = new Dictionary<MethodInfo, MethodInfo>();
                    var lifter = new Lifter(type, _readonly);
                    if (selfCell != null) {
                        lifter.SelfCell = selfCell;
                        lifter.SelfCall = SelfCallStandIn(lambda);
                        substitutions.Add(lifter.SelfCall, method);
                    }
                    var lifted = (LambdaExpression)lifter.Visit(lambda);
                    compiler(lifted, method, substitutions);
                    if (!_tiered) {
                        // after the compiler has set the signature: a MethodBuilder takes no parameters once it has these
                        method.SetImplementationFlags(MethodImplAttributes.IL | MethodImplAttributes.AggressiveOptimization);
                    }

                    if (_readonly && lifter.Fields.Count > 0) {
                        EmitInitializer(type, lifter, id);
                    }

                    Type created;
                    if (_readonly) {
                        lock (_handoff) { _handoff[id] = lifter.Values.ToArray(); }
                        try {
                            created = type.CreateType();
                            RuntimeHelpers.RunClassConstructor(created.TypeHandle);
                        } finally {
                            lock (_handoff) { _handoff.Remove(id); }
                        }
                    } else {
                        created = type.CreateType();
                        for (int i = 0; i < lifter.Fields.Count; i++) {
                            created.GetField(lifter.Fields[i].Name, BindingFlags.Public | BindingFlags.Static).SetValue(null, lifter.Values[i]);
                        }
                    }

                    var emitted = created.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
                    var result = emitted.CreateDelegate(lambda.Type);
                    _methods.Add(result, emitted);
                    Interlocked.Increment(ref Emitted);
                    if (JitRuntime.Verbose) {
                        Console.Error.WriteLine("[jit] emitted {0} as {1} ({2}, {3} constant field(s){4})", name, created.FullName,
                            _collectible ? "collectible" : "not collectible", lifter.Fields.Count,
                            lifter.DirectCalls > 0 ? ", " + lifter.DirectCalls + " direct call(s)" : "");
                    }
                    return result;
                }
            } catch (Exception e) {
                Interlocked.Increment(ref Failed);
                if (JitRuntime.Verbose) {
                    Console.Error.WriteLine("[jit] -X:JITMethods: {0} not emitted: {1}: {2}", lambda.Name, e.GetType().Name, e.Message);
                }
                return null;
            }
        }

        // ---- constants -----------------------------------------------------------------------

        // readonly: what a type's initializer assigns to its fields, from EmitInitializer to the initializer's run.
        private static readonly Dictionary<int, object[]>/*!*/ _handoff = new Dictionary<int, object[]>();

        /// <summary>Called by an emitted type's initializer.</summary>
        public static object[] TakeConstants(int id) {
            lock (_handoff) { return _handoff[id]; }
        }

        // static T() { object[] values = TakeConstants(id); k0 = (T0)values[0]; ... }
        private static void EmitInitializer(TypeBuilder/*!*/ type, Lifter/*!*/ lifter, int id) {
            var il = type.DefineTypeInitializer().GetILGenerator();
            var values = il.DeclareLocal(typeof(object[]));
            il.Emit(OpCodes.Ldc_I4, id);
            il.Emit(OpCodes.Call, typeof(JitMethodEmitter).GetMethod("TakeConstants"));
            il.Emit(OpCodes.Stloc, values);
            for (int i = 0; i < lifter.Fields.Count; i++) {
                il.Emit(OpCodes.Ldloc, values);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Unbox_Any, lifter.Fields[i].FieldType);
                il.Emit(OpCodes.Stsfld, lifter.Fields[i]);
            }
            il.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Replaces what IL cannot hold: a constant that is not a literal by a static field of the type
        /// being emitted, a self-call through the cell by a call of the stand-in for the method being
        /// emitted, and the invocation of an emitted body's delegate by a call of its method.
        /// </summary>
        private sealed class Lifter : ExpressionVisitor {
            private readonly TypeBuilder _type;
            private readonly bool _readonly;
            internal readonly List<FieldBuilder>/*!*/ Fields = new List<FieldBuilder>();
            internal readonly List<object>/*!*/ Values = new List<object>();
            internal StrongBox<Delegate> SelfCell;
            internal MethodInfo SelfCall;
            internal int DirectCalls;

            internal Lifter(TypeBuilder type, bool @readonly) {
                _type = type;
                _readonly = @readonly;
            }

            protected override Expression VisitConstant(ConstantExpression node) {
                object value = node.Value;
                // (no type: a DynamicMethod, whose constants the expression compiler puts in its closure)
                if (value == null || _type == null || IsLiteral(node.Type)) { return node; }

                // one field for an object and the type it is used as
                for (int i = 0; i < Values.Count; i++) {
                    if (ReferenceEquals(Values[i], value) && Fields[i].FieldType == node.Type) {
                        return Expression.Field(null, Fields[i]);
                    }
                }
                var field = _type.DefineField("k" + Fields.Count, node.Type,
                    FieldAttributes.Public | FieldAttributes.Static | (_readonly ? FieldAttributes.InitOnly : 0));
                Fields.Add(field);
                Values.Add(value);
                return Expression.Field(null, field);
            }

            // what ILGen.CanEmitConstant says of a type (the expression compiler's)
            private static bool IsLiteral(Type/*!*/ type) {
                switch (Type.GetTypeCode(type)) {
                    case TypeCode.Boolean:
                    case TypeCode.SByte:
                    case TypeCode.Int16:
                    case TypeCode.Int32:
                    case TypeCode.Int64:
                    case TypeCode.Single:
                    case TypeCode.Double:
                    case TypeCode.Char:
                    case TypeCode.Byte:
                    case TypeCode.UInt16:
                    case TypeCode.UInt32:
                    case TypeCode.UInt64:
                    case TypeCode.Decimal:
                    case TypeCode.String:
                        return true;
                }
                return false;
            }

            protected override Expression VisitInvocation(InvocationExpression node) {
                // ((TDelegate)selfCell.Value)(args): JitCompiler.EmitCall's self-call
                var convert = node.Expression as UnaryExpression;
                if (SelfCell != null && convert != null && convert.NodeType == ExpressionType.Convert) {
                    var member = convert.Operand as MemberExpression;
                    var cell = (member != null) ? member.Expression as ConstantExpression : null;
                    if (cell != null && ReferenceEquals(cell.Value, SelfCell)) {
                        DirectCalls++;
                        return Expression.Call(SelfCall, Visit(node.Arguments));
                    }
                }

                // typed(args), typed the delegate of an emitted body: JitStub.BuildEntry's call
                var target = node.Expression as ConstantExpression;
                MethodInfo emitted;
                if (target != null && target.Value is Delegate && _methods.TryGetValue((Delegate)target.Value, out emitted)) {
                    DirectCalls++;
                    return Expression.Call(emitted, Visit(node.Arguments));
                }

                return base.VisitInvocation(node);
            }
        }

        // ---- the self-call's stand-in --------------------------------------------------------
        // Expression.Call needs a method it can ask for its parameters, which a MethodBuilder cannot
        // answer before its type is created: the tree calls one of these, closed over the body's
        // signature, and the expression compiler emits the call to the MethodBuilder in its place.

        private static MethodInfo/*!*/ SelfCallStandIn(LambdaExpression/*!*/ lambda) {
            var types = new Type[lambda.Parameters.Count + 1];
            for (int i = 0; i < lambda.Parameters.Count; i++) {
                types[i] = lambda.Parameters[i].Type;
            }
            types[types.Length - 1] = lambda.ReturnType;
            return typeof(JitMethodEmitter).GetMethod("SelfCall" + lambda.Parameters.Count).MakeGenericMethod(types);
        }

        public static R SelfCall1<A, R>(A a) { throw new InvalidOperationException(); }
        public static R SelfCall2<A, B, R>(A a, B b) { throw new InvalidOperationException(); }
        public static R SelfCall3<A, B, C, R>(A a, B b, C c) { throw new InvalidOperationException(); }
        public static R SelfCall4<A, B, C, D, R>(A a, B b, C c, D d) { throw new InvalidOperationException(); }
        public static R SelfCall5<A, B, C, D, E, R>(A a, B b, C c, D d, E e) { throw new InvalidOperationException(); }
        public static R SelfCall6<A, B, C, D, E, F, R>(A a, B b, C c, D d, E e, F f) { throw new InvalidOperationException(); }
        public static R SelfCall7<A, B, C, D, E, F, G, R>(A a, B b, C c, D d, E e, F f, G g) { throw new InvalidOperationException(); }
    }
}
