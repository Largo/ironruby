using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IronRuby.InitializerGenerator {
    /// <summary>
    /// The IronRuby types, attributes and constants the generator reads from the compilation, and what it
    /// needs to know about a type the way System.Reflection would tell it.
    /// </summary>
    internal sealed class KnownSymbols {
        public readonly Compilation Compilation;

        public readonly INamedTypeSymbol RubyModuleAttribute, RubyClassAttribute, RubySingletonAttribute, RubyExceptionAttribute;
        public readonly INamedTypeSymbol RubyMethodAttribute, RubyConstructorAttribute, RubyConstantAttribute;
        public readonly INamedTypeSymbol IncludesAttribute, HideMethodAttribute, UndefineMethodAttribute, AliasMethodAttribute;
        public readonly INamedTypeSymbol DefaultProtocolAttribute, CalledNameAttribute, NotNullAttribute, NotNullItemsAttribute;

        public readonly INamedTypeSymbol RubyModule, RubyClass, RubyContext, RubyScope, BlockParam, RubyCallSiteStorage, RuleGenerator;
        public readonly INamedTypeSymbol BasicObject, Kernel, Object, Exception, Expression;

        public readonly int MethodDefault, MethodInstance, MethodNoEvent;
        public readonly int RestrictionsBuiltin, RestrictionsNoUnderlyingType;
        public readonly string MainSingletonName;

        /// <summary>
        /// The largest number of overloads LibraryInitializer.DefineLibraryMethod takes as separate arguments;
        /// a method with more passes arrays.
        /// </summary>
        public readonly int MaxOverloads;

        private KnownSymbols(Compilation compilation, INamedTypeSymbol rubyModuleAttribute) {
            Compilation = compilation;
            RubyModuleAttribute = rubyModuleAttribute;

            RubyClassAttribute = Get("IronRuby.Runtime.RubyClassAttribute");
            RubySingletonAttribute = Get("IronRuby.Runtime.RubySingletonAttribute");
            RubyExceptionAttribute = Get("IronRuby.Runtime.RubyExceptionAttribute");
            RubyMethodAttribute = Get("IronRuby.Runtime.RubyMethodAttribute");
            RubyConstructorAttribute = Get("IronRuby.Runtime.RubyConstructorAttribute");
            RubyConstantAttribute = Get("IronRuby.Runtime.RubyConstantAttribute");
            IncludesAttribute = Get("IronRuby.Runtime.IncludesAttribute");
            HideMethodAttribute = Get("IronRuby.Runtime.HideMethodAttribute");
            UndefineMethodAttribute = Get("IronRuby.Runtime.UndefineMethodAttribute");
            AliasMethodAttribute = Get("IronRuby.Runtime.AliasMethodAttribute");
            DefaultProtocolAttribute = Get("IronRuby.Runtime.DefaultProtocolAttribute");
            CalledNameAttribute = Get("IronRuby.Runtime.CalledNameAttribute");
            NotNullAttribute = Get("Microsoft.Scripting.Runtime.NotNullAttribute");
            NotNullItemsAttribute = Get("Microsoft.Scripting.Runtime.NotNullItemsAttribute");

            RubyModule = Get("IronRuby.Builtins.RubyModule");
            RubyClass = Get("IronRuby.Builtins.RubyClass");
            RubyContext = Get("IronRuby.Runtime.RubyContext");
            RubyScope = Get("IronRuby.Runtime.RubyScope");
            BlockParam = Get("IronRuby.Runtime.BlockParam");
            RubyCallSiteStorage = Get("IronRuby.Runtime.RubyCallSiteStorage");
            RuleGenerator = Get("IronRuby.Runtime.Calls.RuleGenerator");
            BasicObject = Get("IronRuby.Builtins.BasicObject");
            Kernel = Get("IronRuby.Builtins.Kernel");
            Object = compilation.GetSpecialType(SpecialType.System_Object);
            Exception = Get("System.Exception");
            Expression = Get("System.Linq.Expressions.Expression");

            var methodAttributes = Get("IronRuby.Runtime.RubyMethodAttributes");
            MethodDefault = EnumValue(methodAttributes, "Default");
            MethodInstance = EnumValue(methodAttributes, "Instance");
            MethodNoEvent = EnumValue(methodAttributes, "NoEvent");

            var restrictions = Get("IronRuby.Builtins.ModuleRestrictions");
            RestrictionsBuiltin = EnumValue(restrictions, "Builtin");
            RestrictionsNoUnderlyingType = EnumValue(restrictions, "NoUnderlyingType");

            MainSingletonName = "__MainSingleton";
            foreach (var member in RubyClass.GetMembers("MainSingletonName")) {
                if (member is IFieldSymbol field && field.HasConstantValue && field.ConstantValue is string name) {
                    MainSingletonName = name;
                }
            }

            foreach (var member in Get("IronRuby.Builtins.LibraryInitializer").GetMembers("DefineLibraryMethod")) {
                if (member is IMethodSymbol method) {
                    int count = 0;
                    foreach (var parameter in method.Parameters) {
                        if (parameter.Type.SpecialType == SpecialType.System_UInt32) {
                            count++;
                        }
                    }
                    MaxOverloads = Math.Max(MaxOverloads, count);
                }
            }
            if (MaxOverloads == 0) {
                throw new MissingSymbolException("LibraryInitializer.DefineLibraryMethod(..., uint, Delegate)");
            }
        }

        /// <summary>
        /// Null for a compilation that does not reference IronRuby: it has no library to initialize.
        /// </summary>
        public static KnownSymbols Create(Compilation compilation) {
            var moduleAttribute = compilation.GetTypeByMetadataName("IronRuby.Runtime.RubyModuleAttribute");
            return moduleAttribute != null ? new KnownSymbols(compilation, moduleAttribute) : null;
        }

        private INamedTypeSymbol Get(string metadataName) {
            var result = Compilation.GetTypeByMetadataName(metadataName);
            if (result == null) {
                throw new MissingSymbolException(metadataName);
            }
            return result;
        }

        private static int EnumValue(INamedTypeSymbol enumType, string name) {
            foreach (var member in enumType.GetMembers(name)) {
                if (member is IFieldSymbol field && field.HasConstantValue) {
                    return Convert.ToInt32(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            throw new MissingSymbolException(enumType.Name + "." + name);
        }

        #region Types as reflection describes them

        public static bool Same(ISymbol x, ISymbol y) {
            return SymbolEqualityComparer.Default.Equals(x, y);
        }

        public static bool IsOrDerivesFrom(ITypeSymbol type, ITypeSymbol baseType) {
            for (var t = type; t != null; t = t.BaseType) {
                if (Same(t, baseType)) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Type.IsAssignableFrom.
        /// </summary>
        public bool IsAssignableFrom(ITypeSymbol target, ITypeSymbol source) {
            var conversion = Compilation.ClassifyConversion(source, target);
            return conversion.IsIdentity || conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing);
        }

        /// <summary>
        /// typeof(T&lt;&gt;) is the generic type definition, as it is for reflection.
        /// </summary>
        public static ITypeSymbol Normalize(ITypeSymbol type) {
            return type is INamedTypeSymbol named && named.IsUnboundGenericType ? named.OriginalDefinition : type;
        }

        public static bool IsGenericTypeDefinition(ITypeSymbol type) {
            return type is INamedTypeSymbol named && named.IsGenericType && named.IsDefinition;
        }

        public static string NamespaceName(ITypeSymbol type) {
            while (type is IArrayTypeSymbol || type is IPointerTypeSymbol) {
                type = type is IArrayTypeSymbol array ? array.ElementType : ((IPointerTypeSymbol)type).PointedAtType;
            }
            var ns = type.ContainingNamespace;
            return ns == null || ns.IsGlobalNamespace ? null : ns.ToDisplayString();
        }

        /// <summary>
        /// Type.Name: "IDictionary`2", "Byte[]".
        /// </summary>
        public static string ReflectionName(ITypeSymbol type) {
            switch (type) {
                case IArrayTypeSymbol array:
                    return ReflectionName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";
                case IPointerTypeSymbol pointer:
                    return ReflectionName(pointer.PointedAtType) + "*";
                default:
                    return type.MetadataName;
            }
        }

        /// <summary>
        /// Type.FullName: "System.Collections.Generic.IDictionary`2", "Outer+Inner", "System.Byte[]". The type
        /// arguments of a constructed type are written without their assemblies, which is enough to tell two
        /// constructions apart and to order them.
        /// </summary>
        public static string FullName(ITypeSymbol type) {
            switch (type) {
                case IArrayTypeSymbol array:
                    return FullName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";

                case IPointerTypeSymbol pointer:
                    return FullName(pointer.PointedAtType) + "*";

                case INamedTypeSymbol named:
                    var result = new StringBuilder(DefinitionFullName(named));
                    if (named.IsGenericType && !named.IsDefinition) {
                        result.Append('[');
                        for (int i = 0; i < named.TypeArguments.Length; i++) {
                            if (i > 0) {
                                result.Append(',');
                            }
                            result.Append('[').Append(FullName(named.TypeArguments[i])).Append(']');
                        }
                        result.Append(']');
                    }
                    return result.ToString();

                default:
                    return type.Name;
            }
        }

        private static string DefinitionFullName(INamedTypeSymbol type) {
            if (type.ContainingType != null) {
                return DefinitionFullName(type.ContainingType) + "+" + type.MetadataName;
            }
            string ns = NamespaceName(type);
            return ns != null ? ns + "." + type.MetadataName : type.MetadataName;
        }

        /// <summary>
        /// The name a type is written with in the initializers: Func, Action and Expression by their short
        /// names (the generated file imports their namespaces), everything else in full.
        /// </summary>
        private string DispenseName(ITypeSymbol type) {
            string fullName = type is INamedTypeSymbol named ? DefinitionFullName(named) : FullName(type);
            return
                Same(type, Expression) ||
                fullName.StartsWith("System.Action", StringComparison.Ordinal) ||
                fullName.StartsWith("System.Func`", StringComparison.Ordinal) ||
                fullName == "System.Runtime.InteropServices.DefaultParameterValueAttribute" ?
                ReflectionName(type) : fullName;
        }

        /// <summary>
        /// Microsoft.Scripting.Utils.ReflectionUtils.FormatTypeName, the C# spelling of a type.
        /// </summary>
        public StringBuilder FormatTypeName(StringBuilder result, ITypeSymbol type) {
            if (type is INamedTypeSymbol named && named.IsGenericType) {
                string genericName = DispenseName(named.OriginalDefinition).Replace('+', '.');
                int tickIndex = genericName.IndexOf('`');
                result.Append(tickIndex != -1 ? genericName.Substring(0, tickIndex) : genericName);

                if (named.IsDefinition) {
                    result.Append('<');
                    result.Append(',', named.TypeArguments.Length - 1);
                    result.Append('>');
                } else {
                    FormatTypeArgs(result, named.TypeArguments);
                }
            } else if (type is ITypeParameterSymbol) {
                result.Append(type.Name);
            } else if (type is IArrayTypeSymbol array) {
                FormatTypeName(result, array.ElementType);
                result.Append('[').Append(',', array.Rank - 1).Append(']');
            } else if (type is IPointerTypeSymbol pointer) {
                FormatTypeName(result, pointer.PointedAtType);
                result.Append('*');
            } else {
                result.Append(DispenseName(type).Replace('+', '.'));
            }
            return result;
        }

        public StringBuilder FormatTypeArgs(StringBuilder result, IReadOnlyList<ITypeSymbol> types) {
            if (types.Count > 0) {
                result.Append("<");
                for (int i = 0; i < types.Count; i++) {
                    if (i > 0) {
                        result.Append(", ");
                    }
                    FormatTypeName(result, types[i]);
                }
                result.Append(">");
            }
            return result;
        }

        public string TypeName(ITypeSymbol type) {
            return FormatTypeName(new StringBuilder(), type).ToString();
        }

        public string TypeArgs(params ITypeSymbol[] types) {
            return FormatTypeArgs(new StringBuilder(), types).ToString();
        }

        /// <summary>
        /// RubyContext.GetQualifiedNameNoLock without generic arguments: the Ruby name of a CLR type that no
        /// library names, "System::Collections::Generic::IDictionary".
        /// </summary>
        public static StringBuilder AppendQualifiedName(StringBuilder result, ITypeSymbol type) {
            switch (type) {
                case ITypeParameterSymbol _:
                    return result.Append(type.Name);

                case IArrayTypeSymbol array:
                    return AppendQualifiedName(result, array.ElementType).Append('[').Append(',', array.Rank - 1).Append(']');

                case IPointerTypeSymbol pointer:
                    return AppendQualifiedName(result, pointer.PointedAtType).Append('*');
            }

            if (type.ContainingType != null) {
                AppendQualifiedName(result, type.ContainingType);
                result.Append("::");
            } else {
                string ns = NamespaceName(type);
                if (ns != null) {
                    result.Append(ns.Replace(".", "::"));
                    result.Append("::");
                }
            }

            // the name without the generic arity
            return result.Append(type.Name);
        }

        #endregion
    }

    internal sealed class MissingSymbolException : Exception {
        public MissingSymbolException(string name)
            : base("IronRuby's '" + name + "' was not found among the referenced assemblies") {
        }
    }
}
