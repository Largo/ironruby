using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace IronRuby.InitializerGenerator {
    /// <summary>
    /// One Ruby library: the [RubyModule]/[RubyClass] types of a namespace, read from the compilation, and the
    /// LibraryInitializer subclass that registers them.
    ///
    /// This is ClassInitGenerator's LibraryDef reading Roslyn symbols instead of a built assembly; what it
    /// writes is, to the byte, what that tool wrote.
    /// </summary>
    internal sealed class LibraryDef {
        private const string TypeAction = "Action";
        private const string TypeFunction = "Func";
        private const string BuiltinsNamespace = "IronRuby.Builtins";
        private const string ExceptionFactoryPrefix = "ExceptionFactory__";

        private readonly KnownSymbols _known;
        private readonly Action<Diagnostic> _report;
        private readonly string _typeRubyModule, _typeRubyClass;

        // keyed by the full name of the extended type, sorted to keep the output stable
        private readonly SortedDictionary<string, ModuleDef> _moduleDefs = new SortedDictionary<string, ModuleDef>(LegacyCollation.Instance);
        private readonly Dictionary<ITypeSymbol, ModuleDef> _traits = new Dictionary<ITypeSymbol, ModuleDef>(SymbolEqualityComparer.Default);

        // The top-level Ruby name another library of the assembly gives a CLR type it extends -
        // [RubyClass("Thread", Extends = typeof(Thread))] in the builtins - so that a class this library
        // defines in its own unnamed extension of the type is Thread::Mutex, not System::Threading::Thread::Mutex.
        private readonly Dictionary<ITypeSymbol, string> _extendedTypeNames = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);

        // in the order they were first needed
        private readonly List<KeyValuePair<ITypeSymbol, string>> _moduleRefs = new List<KeyValuePair<ITypeSymbol, string>>();
        private readonly List<KeyValuePair<ITypeSymbol, string>> _classRefs = new List<KeyValuePair<ITypeSymbol, string>>();

        public readonly string Namespace;
        public readonly string InitializerName;
        public bool AnyErrors;
        private IndentedWriter _output;

        private bool Builtins {
            get { return Namespace == BuiltinsNamespace; }
        }

        public LibraryDef(KnownSymbols known, string ns, Action<Diagnostic> report) {
            _known = known;
            _report = report;
            Namespace = ns;
            // LibraryInitializer.GetTypeName
            InitializerName = ns.Substring(ns.LastIndexOf('.') + 1) + "LibraryInitializer";
            _typeRubyModule = known.TypeName(known.RubyModule);
            _typeRubyClass = known.TypeName(known.RubyClass);
        }

        /// <summary>
        /// Whether a type of the namespace <paramref name="ns"/> belongs to the library <paramref name="library"/>.
        /// </summary>
        public static bool IsWithin(string ns, string library) {
            return ns != null && ns.StartsWith(library, StringComparison.Ordinal)
                && (ns.Length == library.Length || ns[library.Length] == '.');
        }

        #region Definitions

        private sealed class TypeRef {
            public ModuleDef Definition;
            public readonly ITypeSymbol Type;
            public string RefName;

            public TypeRef(ITypeSymbol type) {
                Type = type;
            }
        }

        private readonly struct MixinRef {
            public readonly TypeRef Module;
            public readonly bool Copy;

            public MixinRef(TypeRef module, bool copy) {
                Module = module;
                Copy = copy;
            }
        }

        private enum ModuleKind {
            Module,
            Singleton,
            Class
        }

        private enum HiddenMethod {
            ClrInvisible,
            Undefined
        }

        private sealed class ModuleDef {
            // Full Ruby name (e.g. "IronRuby::Clr::String")
            public string QualifiedName;

            // Name of the constant defined within declaring module (e.g. "String")
            public string SimpleName;

            // Full Ruby name with '::' replaced by '__' (e.g. "IronRuby__Clr__String").
            public string Id;

            // The trait only extends an existing CLR type, it doesn't define/reopen a Ruby module.
            public bool IsExtension;

            // Defines an Exception class.
            public bool IsException;

            // Aliases for the module (constants defined in the declaring type).
            public readonly List<string> Aliases = new List<string>();

            // The type declaring Ruby methods:
            public INamedTypeSymbol Trait;

            // Type type being extended by trait (same as Trait for non-extension classes):
            public ITypeSymbol Extends;

            // If non-null, the declaring Ruby module is specified here and is different from the declaring CLR type.
            // Target declaration must be within the same library.
            public ITypeSymbol DefineIn;

            public readonly SortedDictionary<string, MethodDef> InstanceMethods = new SortedDictionary<string, MethodDef>(LegacyCollation.Instance);
            public readonly SortedDictionary<string, MethodDef> ClassMethods = new SortedDictionary<string, MethodDef>(LegacyCollation.Instance);
            public readonly SortedDictionary<string, ConstantDef> Constants = new SortedDictionary<string, ConstantDef>(LegacyCollation.Instance);

            public readonly SortedDictionary<string, HiddenMethod> HiddenInstanceMethods = new SortedDictionary<string, HiddenMethod>(LegacyCollation.Instance);
            public readonly SortedDictionary<string, HiddenMethod> HiddenClassMethods = new SortedDictionary<string, HiddenMethod>(LegacyCollation.Instance);

            // { new_name -> old_name }
            public readonly SortedDictionary<string, string> ClassMethodAliases = new SortedDictionary<string, string>(LegacyCollation.Instance);
            public readonly SortedDictionary<string, string> InstanceMethodAliases = new SortedDictionary<string, string>(LegacyCollation.Instance);

            public readonly List<IMethodSymbol> Factories = new List<IMethodSymbol>();

            public readonly List<MixinRef> Mixins = new List<MixinRef>();

            public ModuleKind Kind;
            public bool HasCopyInclusions;

            public TypeRef Super;              // non-null for all classes except for object

            // Non-null for modules nested in other Ruby modules.
            // Doesn't influence dependency, constants for nested types are defined after all classes have been defined.
            public ModuleDef DeclaringModule;
            public string DeclaringTypeRef;

            // Whether the module is defined on Object, so we need to set a constant on Object.
            public bool IsGlobal;

            // Variable name where definition is stored.
            public string Reference;
            public string Definition;

            public int Restrictions;

            // one of the classes and modules RubyContext creates itself
            public bool IsPrimitive;

            private int _dependencyOrder;

            /// <summary>
            /// Indicates the order that things should be generated so mixins/subclasses are always definied before
            /// they are referenced. Each module gets the value of the max of all the types it refers to
            /// plus one.
            /// </summary>
            public int DependencyOrder {
                get {
                    if (_dependencyOrder == 0) {
                        if (Super != null && Super.Definition != null) {
                            _dependencyOrder = Math.Max(Super.Definition.DependencyOrder, _dependencyOrder);
                        }

                        foreach (MixinRef mixin in Mixins) {
                            if (mixin.Module.Definition != null) {
                                _dependencyOrder = Math.Max(mixin.Module.Definition.DependencyOrder, _dependencyOrder);
                            }
                        }
                        _dependencyOrder += 1;
                    }
                    return _dependencyOrder;
                }
            }

            public bool HasInstanceInitializer {
                get {
                    return InstanceMethods.Count > 0 || HiddenInstanceMethods.Count > 0 || InstanceMethodAliases.Count > 0
                        || HasCopyInclusions || IsPrimitive;
                }
            }

            public bool HasConstantsInitializer {
                get { return Constants.Count > 0 || HasCopyInclusions; }
            }

            public bool HasClassInitializer {
                get {
                    return ClassMethods.Count > 0 || HiddenClassMethods.Count > 0 || ClassMethodAliases.Count > 0
                        || HasCopyInclusions || IsPrimitive;
                }
            }

            public const string BasicObjectClassRef = "Context.BasicObjectClass";
            public const string ObjectClassRef = "Context.ObjectClass";
            public const string KernelModuleRef = "Context.KernelModule";
            public const string ModuleClassRef = "Context.ModuleClass";
            public const string ClassClassRef = "Context.ClassClass";

            internal string GetReference(KnownSymbols known, ref int defVariableId) {
                if (Reference == null) {
                    if (KnownSymbols.Same(Extends, known.BasicObject)) {
                        Reference = BasicObjectClassRef;
                    } else if (KnownSymbols.Same(Extends, known.Object)) {
                        Reference = ObjectClassRef;
                    } else if (KnownSymbols.Same(Extends, known.Kernel)) {
                        Reference = KernelModuleRef;
                    } else if (KnownSymbols.Same(Extends, known.RubyModule)) {
                        Reference = ModuleClassRef;
                    } else if (KnownSymbols.Same(Extends, known.RubyClass)) {
                        Reference = ClassClassRef;
                    } else {
                        Definition = Reference = "def" + defVariableId++;
                    }
                }

                return Reference;
            }

            public override string ToString() {
                return QualifiedName;
            }

            internal string GetInitializerDelegates() {
                return
                    (HasInstanceInitializer ? "Load" + Id + "_Instance" : "null") + ", " +
                    (HasClassInitializer ? "Load" + Id + "_Class" : "null") + ", " +
                    (HasConstantsInitializer ? "Load" + Id + "_Constants" : "null");
            }

            internal string GetModuleRestrictions() {
                return "0x" + Restrictions.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private sealed class MethodDef {
            public string Name;
            public readonly List<IMethodSymbol> Overloads = new List<IMethodSymbol>();
            public int Attributes;

            public bool IsRuleGenerator(KnownSymbols known) {
                return Overloads.Count == 1 && KnownSymbols.Same(Overloads[0].ReturnType, known.RuleGenerator) && Overloads[0].Parameters.Length == 0;
            }

            public override string ToString() {
                return Name;
            }
        }

        private sealed class ConstantDef {
            public readonly string Name;
            // a field, or a method taking the module
            public readonly ISymbol Member;

            public ConstantDef(string name, ISymbol member) {
                Name = name;
                Member = member;
            }
        }

        /// <summary>
        /// A [RubyModule], [RubyClass], [RubySingleton] or [RubyException] attribute.
        /// </summary>
        private sealed class ModuleAttribute {
            public string Name;
            public ITypeSymbol Extends, DefineIn, Inherits;
            public int? Restrictions;
            public bool IsClass, IsSingleton, IsException;
        }

        #endregion

        #region Attributes

        private static bool Is(AttributeData attribute, INamedTypeSymbol attributeClass) {
            return attribute.AttributeClass != null && KnownSymbols.IsOrDerivesFrom(attribute.AttributeClass, attributeClass);
        }

        private static bool IsDefined(ISymbol symbol, INamedTypeSymbol attributeClass) {
            foreach (var attribute in symbol.GetAttributes()) {
                if (Is(attribute, attributeClass)) {
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetNamed(AttributeData attribute, string name, out TypedConstant value) {
            foreach (var argument in attribute.NamedArguments) {
                if (argument.Key == name) {
                    value = argument.Value;
                    return true;
                }
            }
            value = default(TypedConstant);
            return false;
        }

        private static ITypeSymbol NamedType(AttributeData attribute, string name) {
            TypedConstant value;
            return TryGetNamed(attribute, name, out value) && value.Kind == TypedConstantKind.Type && value.Value is ITypeSymbol type ?
                KnownSymbols.Normalize(type) : null;
        }

        private static bool NamedBool(AttributeData attribute, string name) {
            TypedConstant value;
            return TryGetNamed(attribute, name, out value) && value.Value is bool b && b;
        }

        private static string StringArgument(AttributeData attribute, int index) {
            return index < attribute.ConstructorArguments.Length && attribute.ConstructorArguments[index].Kind == TypedConstantKind.Primitive ?
                attribute.ConstructorArguments[index].Value as string : null;
        }

        private IEnumerable<ModuleAttribute> GetModuleAttributes(INamedTypeSymbol type) {
            foreach (var attribute in type.GetAttributes()) {
                if (!Is(attribute, _known.RubyModuleAttribute)) {
                    continue;
                }

                var result = new ModuleAttribute {
                    Name = StringArgument(attribute, 0),
                    Extends = NamedType(attribute, "Extends"),
                    DefineIn = NamedType(attribute, "DefineIn"),
                    Inherits = NamedType(attribute, "Inherits"),
                    IsClass = Is(attribute, _known.RubyClassAttribute),
                    IsSingleton = Is(attribute, _known.RubySingletonAttribute),
                    IsException = Is(attribute, _known.RubyExceptionAttribute),
                };

                TypedConstant restrictions;
                if (TryGetNamed(attribute, "Restrictions", out restrictions) && restrictions.Value != null) {
                    result.Restrictions = Convert.ToInt32(restrictions.Value, System.Globalization.CultureInfo.InvariantCulture);
                }

                yield return result;
            }
        }

        public static bool IsModuleDefinition(KnownSymbols known, INamedTypeSymbol type) {
            return IsDefined(type, known.RubyModuleAttribute);
        }

        #endregion

        #region Reflection

        public void ReflectTypes(IReadOnlyList<INamedTypeSymbol> allTypes) {

            foreach (INamedTypeSymbol trait in allTypes) {
                if (trait.ContainingType != null) {
                    continue;
                }
                foreach (ModuleAttribute module in GetModuleAttributes(trait)) {
                    if (module.Extends != null && module.Name != null && module.DefineIn == null && !_extendedTypeNames.ContainsKey(module.Extends)) {
                        _extendedTypeNames.Add(module.Extends, module.Name);
                    }
                }
            }

            foreach (INamedTypeSymbol trait in allTypes) {
                if (!IsWithin(KnownSymbols.NamespaceName(trait), Namespace)) {
                    continue;
                }

                var traitAttributes = trait.GetAttributes();

                foreach (ModuleAttribute module in GetModuleAttributes(trait)) {
                    ModuleDef def = new ModuleDef();

                    if (module.IsClass) {
                        def.Kind = ModuleKind.Class;
                        def.IsException = module.IsException;
                    } else if (module.IsSingleton) {
                        def.Kind = ModuleKind.Singleton;
                        if (module.Extends != null) {
                            LogError(trait, "{0}: Singleton cannot Extend a type", KnownSymbols.FullName(trait));
                            module.Extends = null;
                        }
                    } else {
                        def.Kind = ModuleKind.Module;
                    }

                    if (KnownSymbols.Same(trait, module.Extends)) {
                        LogError(trait, "Module cannot extend itself: {0}", KnownSymbols.FullName(trait));
                        continue;
                    }

                    if (module.Extends != null && module.Name == null) {
                        // extends a CLR type or an existing Ruby library class/module:
                        def.IsExtension = true;
                        def.SimpleName = KnownSymbols.ReflectionName(module.Extends).Replace('`', '_');
                        def.QualifiedName = null;
                        def.DeclaringModule = null;
                        def.IsGlobal = false;
                    } else {
                        def.IsExtension = false;
                        def.SimpleName = module.Name ?? trait.MetadataName;
                        def.QualifiedName = null;   // to be filled in later
                        def.DeclaringModule = null; // to be corrected later for nested modules
                        def.IsGlobal = true;        // to be corrected later for nested modules
                    }

                    def.Trait = trait;
                    def.Extends = module.Extends ?? trait;
                    def.DefineIn = module.DefineIn;

                    // RubyModuleAttribute.GetRestrictions
                    def.Restrictions = module.Restrictions ?? (
                        (Builtins ? _known.RestrictionsBuiltin : 0) |
                        (module.Extends == null ? _known.RestrictionsNoUnderlyingType : 0)
                    );

                    def.Super = null;
                    if (module.IsClass && !KnownSymbols.Same(def.Extends, _known.BasicObject) && def.Extends.TypeKind != TypeKind.Interface) {
                        if (module.Inherits != null) {
                            def.Super = new TypeRef(module.Inherits);
                        } else if (!def.IsExtension && def.Extends.BaseType != null) {
                            def.Super = new TypeRef(def.Extends.BaseType);
                        }
                    }

                    def.HasCopyInclusions = false;
                    foreach (AttributeData includes in traitAttributes) {
                        if (!Is(includes, _known.IncludesAttribute)) {
                            continue;
                        }
                        bool copy = NamedBool(includes, "Copy");
                        if (includes.ConstructorArguments.Length == 1 && includes.ConstructorArguments[0].Kind == TypedConstantKind.Array) {
                            foreach (TypedConstant type in includes.ConstructorArguments[0].Values) {
                                if (type.Value is ITypeSymbol mixin) {
                                    def.Mixins.Add(new MixinRef(new TypeRef(KnownSymbols.Normalize(mixin)), copy));
                                }
                            }
                        }
                        def.HasCopyInclusions |= copy;
                    }

                    string key = KnownSymbols.FullName(def.Extends);
                    if (_moduleDefs.ContainsKey(key)) {
                        LogError(trait, "'{0}' is defined or extended by more than one type of the library {1}", key, Namespace);
                        continue;
                    }

                    _moduleDefs.Add(key, def);
                    _traits.Add(def.Trait, def);

                    // added Ruby methods and constants:
                    ReflectMethods(def);
                    ReflectFieldConstants(def);
                    ReflectAliases(def);

                    foreach (AttributeData method in traitAttributes) {
                        if (Is(method, _known.HideMethodAttribute) || Is(method, _known.UndefineMethodAttribute)) {
                            // hidden CLR methods, undefined methods:
                            string name = StringArgument(method, 0);
                            var dict = NamedBool(method, "IsStatic") ? def.HiddenClassMethods : def.HiddenInstanceMethods;
                            if (name == null) {
                                continue;
                            }
                            if (dict.ContainsKey(name)) {
                                LogError(trait, "Method {0} is already hidden/removed", name);
                            } else {
                                dict.Add(name, Is(method, _known.HideMethodAttribute) ? HiddenMethod.ClrInvisible : HiddenMethod.Undefined);
                            }
                        } else if (Is(method, _known.AliasMethodAttribute)) {
                            // aliased methods:
                            string newName = StringArgument(method, 0), oldName = StringArgument(method, 1);
                            bool isStatic = NamedBool(method, "IsStatic");
                            var aliasDict = isStatic ? def.ClassMethodAliases : def.InstanceMethodAliases;
                            var hiddenDict = isStatic ? def.HiddenClassMethods : def.HiddenInstanceMethods;
                            if (newName == null || oldName == null) {
                                continue;
                            }

                            if (hiddenDict.ContainsKey(newName)) {
                                LogError(trait, "Cannot alias hidden/removed method {0}", newName);
                            } else if (aliasDict.ContainsKey(newName)) {
                                LogError(trait, "Duplicate method alias {0} {1}", newName, oldName);
                            } else {
                                aliasDict.Add(newName, oldName);
                            }
                        }
                    }
                }
            }

            int defVariableId = 1;

            // declaring modules:
            foreach (ModuleDef def in _moduleDefs.Values) {
                if (!def.IsExtension) {
                    if (KnownSymbols.IsGenericTypeDefinition(def.Extends)) {
                        LogError(def.Trait, "Only extension modules or classes can be generic type definitions '{0}'", KnownSymbols.FullName(def.Extends));
                    }

                    // finds the inner most Ruby module def containing this module def:
                    ModuleDef declaringDef = GetDeclaringModuleDef(def);
                    if (declaringDef != null) {
                        def.DeclaringModule = declaringDef;
                        def.IsGlobal = false;

                        // we will need a reference for setting the constant:
                        def.DeclaringTypeRef = declaringDef.GetReference(_known, ref defVariableId);
                        def.GetReference(_known, ref defVariableId);
                    }
                }

                if (def.Kind == ModuleKind.Singleton) {
                    // we need to refer to the singleton object returned from singelton factory:
                    def.GetReference(_known, ref defVariableId);
                }

                if (def.Aliases.Count > 0) {
                    // An alias is a constant set to the module itself (GenerateAliases), so a module with one needs
                    // its reference even when it is global and nothing else refers to it: without it the generated
                    // line was `SetConstant(Context.ObjectClass, "Alias", );'.
                    def.GetReference(_known, ref defVariableId);
                }
            }

            // qualified names, ids:
            foreach (ModuleDef def in _moduleDefs.Values) {
                SetQualifiedName(def);

                if (def.Kind == ModuleKind.Singleton) {
                    def.Id = "__Singleton_" + def.QualifiedName;
                } else {
                    def.Id = def.QualifiedName.Replace(':', '_');
                    // System::IComparable is the name of both IComparable and IComparable<T>: the generic arity keeps
                    // the initializer methods of the two apart
                    if (def.IsExtension && KnownSymbols.IsGenericTypeDefinition(def.Extends)) {
                        def.Id += "_" + ((INamedTypeSymbol)def.Extends).TypeArguments.Length;
                    }
                }

                def.IsPrimitive = !def.IsExtension && (
                       def.QualifiedName == _known.MainSingletonName
                    || KnownSymbols.Same(def.Extends, _known.BasicObject)
                    || KnownSymbols.Same(def.Extends, _known.Kernel)
                    || KnownSymbols.Same(def.Extends, _known.Object)
                    || KnownSymbols.Same(def.Extends, _known.RubyClass)
                    || KnownSymbols.Same(def.Extends, _known.RubyModule)
                );
            }

            // wire-up supers and mixins:
            foreach (ModuleDef def in _moduleDefs.Values) {
                if (def.Super != null) {
                    ModuleDef superDef;
                    if (_moduleDefs.TryGetValue(KnownSymbols.FullName(def.Super.Type), out superDef)) {

                        // define inheritance relationship:
                        def.Super.Definition = superDef;
                        def.Super.RefName = superDef.GetReference(_known, ref defVariableId);

                    } else {

                        // define a class ref-variable for the type of the super class:
                        def.Super.RefName = MakeReference(_classRefs, "classRef", def.Super.Type);

                    }
                } else if (!def.IsExtension && def.Kind == ModuleKind.Class && !KnownSymbols.Same(def.Extends, _known.BasicObject)) {
                    LogError(def.Trait, "Missing super type for type '{0}'", def.QualifiedName);
                }

                // wire-up mixins:
                foreach (MixinRef mixin in def.Mixins) {
                    ModuleDef mixinDef;
                    if (_moduleDefs.TryGetValue(KnownSymbols.FullName(mixin.Module.Type), out mixinDef)) {

                        // define mixin relationship:
                        mixin.Module.Definition = mixinDef;
                        if (!mixin.Copy) {
                            mixin.Module.RefName = mixinDef.GetReference(_known, ref defVariableId);
                        }

                    } else if (!mixin.Copy) {
                        // define a module ref-variable for the type of the mixin:
                        mixin.Module.RefName = MakeReference(_moduleRefs, "moduleRef", mixin.Module.Type);
                    } else {
                        LogError(def.Trait, "Cannot copy-include a mixin not defined in this library ('{0}' includes '{1}')",
                            def.QualifiedName, KnownSymbols.FullName(mixin.Module.Type)
                        );
                    }
                }
            }

            // A module that includes or inherits from itself, directly or through others, has no place in the order the
            // registrations are written in: ModuleDef.DependencyOrder would recurse until the compiler's stack
            // overflowed, which no handler catches and no diagnostic reports.
            var visiting = new Dictionary<ModuleDef, bool>();
            foreach (ModuleDef def in _moduleDefs.Values) {
                CheckInclusionCycles(def, visiting);
            }
        }

        // visiting: true for a module whose supers and mixins are being walked, false for one that is done
        private void CheckInclusionCycles(ModuleDef def, Dictionary<ModuleDef, bool> visiting) {
            if (visiting.ContainsKey(def)) {
                return;
            }
            visiting[def] = true;

            var referred = new List<ModuleDef>();
            if (def.Super != null && def.Super.Definition != null) {
                referred.Add(def.Super.Definition);
            }
            foreach (MixinRef mixin in def.Mixins) {
                if (mixin.Module.Definition != null) {
                    referred.Add(mixin.Module.Definition);
                }
            }

            foreach (ModuleDef other in referred) {
                bool inProgress;
                if (!visiting.TryGetValue(other, out inProgress)) {
                    CheckInclusionCycles(other, visiting);
                } else if (inProgress) {
                    LogError(def.Trait, "'{0}' includes or inherits from '{1}', which includes or inherits from it",
                        def.QualifiedName, other.QualifiedName);
                }
            }
            visiting[def] = false;
        }

        private void SetQualifiedName(ModuleDef def) {
            if (def.QualifiedName == null) {
                if (def.IsExtension) {
                    string name;
                    def.QualifiedName = _extendedTypeNames.TryGetValue(def.Extends, out name) ? name :
                        KnownSymbols.AppendQualifiedName(new StringBuilder(), def.Extends).ToString();
                } else if (def.DeclaringModule == null) {
                    def.QualifiedName = def.SimpleName;
                } else if (DeclaresItself(def)) {
                    // DefineIn naming the module itself, or two modules naming each other: there is no outermost
                    // module to start the name from, and following the chain would never end.
                    LogError(def.Trait, "DefineIn makes '{0}' its own declaring module, directly or through the modules it is declared in",
                        KnownSymbols.FullName(def.Trait));
                    def.DeclaringModule = null;
                    def.QualifiedName = def.SimpleName;
                } else {
                    SetQualifiedName(def.DeclaringModule);
                    def.QualifiedName = def.DeclaringModule.QualifiedName + "::" + def.SimpleName;
                }
            }
        }

        // The chain of declaring modules above def comes back to a module already on it.
        private static bool DeclaresItself(ModuleDef def) {
            var seen = new HashSet<ModuleDef> { def };
            for (ModuleDef outer = def.DeclaringModule; outer != null; outer = outer.DeclaringModule) {
                if (!seen.Add(outer)) {
                    return true;
                }
            }
            return false;
        }

        private ModuleDef GetDeclaringModuleDef(ModuleDef def) {
            ModuleDef declaringDef;
            if (def.DefineIn != null) {
                if (_traits.TryGetValue(def.DefineIn, out declaringDef)) {
                    return declaringDef;
                }
                LogError(def.Trait, "Declaring type specified by DeclareIn parameter '{0}' is not Ruby module definition or is in different library",
                    KnownSymbols.FullName(def.DefineIn));
            }

            INamedTypeSymbol declaringType = def.Trait.ContainingType;
            while (declaringType != null) {
                if (_traits.TryGetValue(declaringType, out declaringDef)) {
                    return declaringDef;
                } else {
                    declaringType = declaringType.ContainingType;
                }
            }
            return null;
        }

        private static string MakeReference(List<KeyValuePair<ITypeSymbol, string>> refs, string prefix, ITypeSymbol typeRef) {
            foreach (var existing in refs) {
                if (KnownSymbols.Same(existing.Key, typeRef)) {
                    return existing.Value;
                }
            }

            string refVariable = prefix + refs.Count;
            refs.Add(new KeyValuePair<ITypeSymbol, string>(typeRef, refVariable));
            return refVariable;
        }

        private void ReflectMethods(ModuleDef moduleDef) {
            foreach (ISymbol member in moduleDef.Trait.GetMembers()) {
                var method = member as IMethodSymbol;
                if (method == null || method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.StaticConstructor) {
                    continue;
                }

                var attributes = method.GetAttributes();
                bool isRubyMethod = false, isConstructor = false, isConstant = false;
                foreach (AttributeData attribute in attributes) {
                    isRubyMethod |= Is(attribute, _known.RubyMethodAttribute);
                    isConstructor |= Is(attribute, _known.RubyConstructorAttribute);
                    isConstant |= Is(attribute, _known.RubyConstantAttribute);
                }

                if (isRubyMethod) {
                    if (!RequireStatic(method)) continue;
                    foreach (AttributeData attribute in attributes) {
                        if (!Is(attribute, _known.RubyMethodAttribute)) {
                            continue;
                        }

                        string name = StringArgument(attribute, 0);
                        if (name == null) {
                            continue;
                        }
                        int methodAttributes = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value != null ?
                            Convert.ToInt32(attribute.ConstructorArguments[1].Value, System.Globalization.CultureInfo.InvariantCulture) : _known.MethodDefault;

                        MethodDef def;

                        SortedDictionary<string, MethodDef> methods =
                            ((methodAttributes & _known.MethodInstance) != 0) ? moduleDef.InstanceMethods : moduleDef.ClassMethods;

                        if (!methods.TryGetValue(name, out def)) {
                            def = new MethodDef();
                            def.Name = name;
                            def.Attributes = methodAttributes;

                            if (Builtins) {
                                def.Attributes |= _known.MethodNoEvent;
                            }

                            methods.Add(name, def);
                        }
                        def.Overloads.Add(method);
                    }
                }

                if (isConstructor) {
                    if (!RequireStatic(method)) continue;
                    moduleDef.Factories.Add(method);
                }

                if (isConstant) {
                    if (!RequireStatic(method)) continue;

                    var parameters = method.Parameters;
                    if (parameters.Length != 1 || !_known.IsAssignableFrom(parameters[0].Type, _known.RubyModule) ||
                        parameters[0].RefKind != RefKind.None || parameters[0].IsOptional) {
                        LogError(method, "Method that defines a constant must take a single parameter of type RubyModule: '{0}'", FormatSignature(method));
                    }

                    ReflectConstants(moduleDef, method);
                }
            }

            VerifyMethods(moduleDef);
        }

        private void VerifyMethods(ModuleDef moduleDef) {
            foreach (var methodDef in moduleDef.InstanceMethods.Values) {
                VerifyMethod(moduleDef, methodDef.Overloads, methodDef, true);
            }

            foreach (var methodDef in moduleDef.ClassMethods.Values) {
                VerifyMethod(moduleDef, methodDef.Overloads, methodDef, false);
            }

            // factories
            VerifyMethod(moduleDef, moduleDef.Factories, null, false);
        }

        private void VerifyMethod(ModuleDef moduleDef, List<IMethodSymbol> overloads, MethodDef methodDef, bool isInstance) {
            foreach (var overload in overloads) {
                var parameterInfos = overload.Parameters;

                if (KnownSymbols.Same(overload.ReturnType, _known.RuleGenerator)) {
                    if (parameterInfos.Length != 0) {
                        LogMethodError("RuleGenerator must be parameterless", methodDef, overload);
                    }

                    if (overloads.Count != 1) {
                        LogMethodError("RuleGenerator must have no overloads", methodDef, overload);
                    }
                } else {

                    bool hasContext = false;
                    bool hasCalledName = false;
                    bool hasBlock = false;
                    bool hasSelf = false;
                    int storageCount = 0;
                    for (int i = 0; i < parameterInfos.Length; i++) {
                        if (parameterInfos[i].RefKind != RefKind.None) {
                            LogMethodError("has ref/out parameter", methodDef, overload);
                        }

                        var type = parameterInfos[i].Type;

                        if (!KnownSymbols.Same(type, _known.RubyCallSiteStorage) && KnownSymbols.IsOrDerivesFrom(type, _known.RubyCallSiteStorage)) {
                            if (hasSelf || hasContext || hasBlock) {
                                LogMethodError("RubyCallSiteStorage must precede all other parameters", methodDef, overload);
                            }
                            storageCount++;
                        } else if (KnownSymbols.Same(type, _known.RubyContext) || KnownSymbols.Same(type, _known.RubyScope)) {
                            if (hasContext) {
                                LogMethodError("has multiple context parameters", methodDef, overload);
                            }

                            if (i - storageCount != 0) {
                                LogMethodError("Context parameter must be the first parameter following optional RubyCallSiteStorage", methodDef, overload);
                            }

                            hasContext = true;
                        } else if (type.SpecialType == SpecialType.System_String && IsDefined(parameterInfos[i], _known.CalledNameAttribute)) {
                            if (hasCalledName || hasBlock || hasSelf || i - storageCount != (hasContext ? 1 : 0)) {
                                LogMethodError("[CalledName] parameter must follow the optional RubyCallSiteStorage and context parameters", methodDef, overload);
                            }

                            hasCalledName = true;
                        } else if (KnownSymbols.Same(type, _known.BlockParam)) {
                            if (hasBlock) {
                                LogMethodError("has multiple block parameters.", methodDef, overload);
                            }

                            // TODO: sites

                            if (i - storageCount != (hasContext ? 1 : 0) + (hasCalledName ? 1 : 0)) {
                                LogMethodError("Block parameter must be the first parameter following optional RubyCallSiteStorage, context and [CalledName] parameters", methodDef, overload);
                            }

                            hasBlock = true;
                        } else if (!hasSelf) {
                            // self
                            if (!isInstance) {
                                INamedTypeSymbol requiredType = moduleDef.Kind == ModuleKind.Module ? _known.RubyModule : _known.RubyClass;

                                if (!_known.IsAssignableFrom(type, requiredType)) {
                                    LogMethodError("Invalid type of self parameter: it must be assignable from '" + KnownSymbols.FullName(requiredType) + "'.", methodDef, overload);
                                }

                                if (parameterInfos[i].Name != "self") {
                                    LogMethodError("Self parameter should be named 'self'.", methodDef, overload);
                                }
                            }

                            hasSelf = true;
                        }
                    }

                    if (!hasSelf) {
                        LogMethodError("Missing self parameter", methodDef, overload);
                    }
                }
            }
        }

        private void ReflectFieldConstants(ModuleDef moduleDef) {
            foreach (ISymbol member in moduleDef.Trait.GetMembers()) {
                if (member is IFieldSymbol field) {
                    ReflectConstants(moduleDef, field);
                }
            }
        }

        private void ReflectConstants(ModuleDef moduleDef, ISymbol member) {
            foreach (AttributeData attr in member.GetAttributes()) {
                if (!Is(attr, _known.RubyConstantAttribute)) {
                    continue;
                }

                string name = StringArgument(attr, 0) ?? member.Name;

                ConstantDef existing;
                if (moduleDef.Constants.TryGetValue(name, out existing)) {
                    LogError(member, "Constant '{0}' defined by multiple members: '{1}' and '{2}'", name, existing.Member.Name, member.Name);
                    continue;
                }

                if (String.IsNullOrEmpty(name) || name[0] < 'A' || name[0] > 'Z') {
                    LogError(member, "Invalid constant name: '{0}' ({1}::{2})", name, KnownSymbols.FullName(member.ContainingType), member.Name);
                    continue;
                }

                moduleDef.Constants.Add(name, new ConstantDef(name, member));
            }
        }

        private void ReflectAliases(ModuleDef moduleDef) {
            // TODO: check for duplicates (in general in the scope of dll)
            foreach (AttributeData attr in moduleDef.Trait.GetAttributes()) {
                if (!Is(attr, _known.RubyConstantAttribute)) {
                    continue;
                }

                string name = StringArgument(attr, 0);
                if (name == null) {
                    LogError(moduleDef.Trait, "Constant alias for module/class must have a name (type '{0}')", KnownSymbols.FullName(moduleDef.Trait));
                    continue;
                }

                if (moduleDef.Aliases.IndexOf(name) != -1) {
                    LogError(moduleDef.Trait, "Duplicate module/class alias '{0}' (type '{1}')", name, KnownSymbols.FullName(moduleDef.Trait));
                }

                moduleDef.Aliases.Add(name);
            }
        }

        /// <summary>
        /// LibraryOverload.EncodeCustomAttributes: the parameter attributes the binder needs, which the
        /// initializer passes along with the delegate so that nothing has to be reflected at startup.
        /// </summary>
        private uint EncodeCustomAttributes(IMethodSymbol method) {
            var ps = method.Parameters;
            if (ps.Length > 15) {
                LogError(method, "A library method cannot have more than 15 parameters: '{0}'", FormatSignature(method));
                return 0;
            }
            uint defaultProtocol = 0;
            uint notNull = 0;

            for (int i = 0; i < ps.Length; i++) {
                defaultProtocol |= IsDefined(ps[i], _known.DefaultProtocolAttribute) ? (1U << i) : 0U;
                notNull |= IsDefined(ps[i], _known.NotNullAttribute) ? (1U << i) : 0U;
            }

            int last = ps.Length - 1;
            // [ParamArray]
            bool isVariadic = last >= 0 && ps[last].IsParams && ps[last].Type is IArrayTypeSymbol;
            if (isVariadic) {
                notNull |= IsDefined(ps[last], _known.NotNullItemsAttribute) ? (1U << last) : 0U;
            }

            return (isVariadic ? 0x80000000 : 0) | (defaultProtocol << 15) | notNull;
        }

        #endregion

        #region Errors

        private static readonly DiagnosticDescriptor _error = new DiagnosticDescriptor(
            "IRGEN001", "Invalid Ruby library definition", "{0}", "IronRuby.InitializerGenerator", DiagnosticSeverity.Error, true
        );

        private bool RequireStatic(IMethodSymbol method) {
            if (!method.IsStatic) {
                LogError(method, "Instance methods not supported (method '{0}.{1}')", _known.TypeName(method.ContainingType), method.Name);
                return false;
            }
            return true;
        }

        private void LogError(ISymbol symbol, string message, params object[] args) {
            Location location = symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None;
            _report(Diagnostic.Create(_error, location, String.Format(System.Globalization.CultureInfo.InvariantCulture, message, args)));
            AnyErrors = true;
        }

        private void LogMethodError(string message, MethodDef methodDef, IMethodSymbol overload) {
            string methodName = (methodDef != null) ? "method \"" + methodDef.Name + '"' : "factory";
            LogError(overload, "{0}: {1} (overload: {2})", methodName, message, FormatSignature(overload));
        }

        private string FormatSignature(IMethodSymbol method) {
            var result = new StringBuilder();
            _known.FormatTypeName(result, method.ReturnType);
            result.Append(' ');
            _known.FormatTypeName(result, method.ContainingType);
            result.Append("::").Append(method.Name).Append('(');
            for (int i = 0; i < method.Parameters.Length; i++) {
                if (i > 0) {
                    result.Append(", ");
                }
                _known.FormatTypeName(result, method.Parameters[i].Type);
                result.Append(' ').Append(method.Parameters[i].Name);
            }
            return result.Append(')').ToString();
        }

        #endregion

        #region Code Generation

        public void GenerateCode(IndentedWriter output) {
            _output = output;

            _output.WriteLine("namespace {0} {{", Namespace);
            _output.Indent++;
            _output.WriteLine("using System;");
            _output.WriteLine("using Microsoft.Scripting.Utils;");
            _output.WriteLine("using System.Runtime.InteropServices;");
            _output.WriteLine();

            _output.WriteLine("public sealed class {0} : {1} {{", InitializerName, "IronRuby.Builtins.LibraryInitializer");
            _output.Indent++;

            _output.WriteLine("protected override void LoadModules() {");
            _output.Indent++;

            GenerateModuleRegistrations();

            _output.Indent--;
            _output.WriteLine("}");
            _output.WriteLine();

            GenerateTraitInitializations();
            GenerateExceptionFactories();

            _output.Indent--;
            _output.WriteLine("}");

            _output.Indent--;
            _output.WriteLine("}");
            _output.WriteLine();
        }

        private ModuleDef GetPrimitive(ITypeSymbol type) {
            ModuleDef result;
            if (!_moduleDefs.TryGetValue(KnownSymbols.FullName(type), out result)) {
                throw new MissingSymbolException("[RubyModule(Extends = typeof(" + KnownSymbols.FullName(type) + "))]");
            }
            return result;
        }

        private void GenerateModuleRegistrations() {

            // primitives:
            if (Builtins) {
                _output.WriteLine("Context.RegisterPrimitives(");
                _output.Indent++;

                _output.WriteLine("Load{0}_Instance,", _known.MainSingletonName);

                _output.WriteLine(GetPrimitive(_known.BasicObject).GetInitializerDelegates() + ",");
                _output.WriteLine(GetPrimitive(_known.Kernel).GetInitializerDelegates() + ",");
                _output.WriteLine(GetPrimitive(_known.Object).GetInitializerDelegates() + ",");
                _output.WriteLine(GetPrimitive(_known.RubyModule).GetInitializerDelegates() + ",");
                _output.WriteLine(GetPrimitive(_known.RubyClass).GetInitializerDelegates());

                _output.Indent--;
                _output.WriteLine(");");
            }

            // generate references:
            foreach (KeyValuePair<ITypeSymbol, string> moduleRef in _moduleRefs) {
                _output.WriteLine("{0} {1} = GetModule(typeof({2}));", _typeRubyModule, moduleRef.Value, _known.TypeName(moduleRef.Key));
            }

            foreach (KeyValuePair<ITypeSymbol, string> classRef in _classRefs) {
                _output.WriteLine("{0} {1} = GetClass(typeof({2}));", _typeRubyClass, classRef.Value, _known.TypeName(classRef.Key));
            }

            _output.WriteLine();
            _output.WriteLine();

            // We need to generate these in proper dependency order
            // also, we want the sort to be stable to improve the
            // quality of the resulting diff of the generated code.
            var worklist = new List<ModuleDef>(_moduleDefs.Values);
            worklist.Sort(delegate(ModuleDef x, ModuleDef y) {
                // if the dependency order is the same, fall back to the type name
                if (x.DependencyOrder == y.DependencyOrder) {
                    return CompareNames(x, y);
                }
                return x.DependencyOrder - y.DependencyOrder;
            });

            // classes:
            foreach (ModuleDef def in worklist) {
                GenerateModuleRegistration(def);
            }

            // add constants for non-global nested modules, adds aliases:
            foreach (ModuleDef def in worklist) {
                if (def.IsGlobal) {
                    GenerateAliases(def, ModuleDef.ObjectClassRef);
                } else if (def.DeclaringTypeRef != null) {
                    GenerateAliases(def, def.DeclaringTypeRef);
                    GenerateSetConstant(def.DeclaringTypeRef, def.SimpleName, def.Reference);
                }
            }
        }

        private void GenerateSetConstant(string owner, string name, string expression) {
            _output.WriteLine("Set{3}Constant({0}, \"{1}\", {2});", owner, name, expression, Builtins ? "Builtin" : null);
        }

        private void GenerateAliases(ModuleDef def, string ownerRef) {
            foreach (string alias in def.Aliases) {
                GenerateSetConstant(ownerRef, alias, def.Reference);
            }
        }

        private void GenerateModuleRegistration(ModuleDef def) {
            if (def.IsPrimitive) {
                _output.WriteLine("// Skipped primitive: {0}", def.QualifiedName);
                return;
            }

            switch (def.Kind) {
                case ModuleKind.Class:
                    if (def.Definition != null) {
                        _output.Write("{0} {1} = ", _typeRubyClass, def.Definition);
                    }

                    if (Builtins && !def.IsExtension) {
                        if (def.QualifiedName == "NilClass") {
                            _output.Write("Context.NilClass = ");
                        } else if (def.QualifiedName == "TrueClass") {
                            _output.Write("Context.TrueClass = ");
                        } else if (def.QualifiedName == "FalseClass") {
                            _output.Write("Context.FalseClass = ");
                        } else if (def.QualifiedName == "Exception") {
                            _output.Write("Context.ExceptionClass = ");
                        } else if (def.QualifiedName == "StandardError") {
                            _output.Write("Context.StandardErrorClass = ");
                        }
                    }

                    if (def.IsExtension) {
                        _output.Write("ExtendClass(typeof({0}), {1}, {2}, {3}, ",
                            _known.TypeName(def.Extends),
                            def.GetModuleRestrictions(),
                            def.Super != null ? def.Super.RefName : "null",
                            def.GetInitializerDelegates()
                        );
                    } else {
                        _output.Write("Define{0}Class(\"{1}\", typeof({2}), {3}, {4}, {5}, ",
                            def.IsGlobal ? "Global" : "",
                            def.QualifiedName,
                            _known.TypeName(def.Extends),
                            def.GetModuleRestrictions(),
                            def.Super != null ? def.Super.RefName : "null",
                            def.GetInitializerDelegates()
                        );
                    }

                    GenerateInclusions(def, true);

                    if (def.Factories.Count > 0) {
                        _output.WriteLine(", ");
                        GenerateDelegatesListCreation(def.Factories);
                    } else if (def.IsException) {
                        _output.WriteLine(", ");
                        GenerateExceptionFactoryDelegateList(def);
                    }

                    _output.WriteLine(");");
                    break;

                case ModuleKind.Module:
                    if (def.Definition != null) {
                        _output.Write("{0} {1} = ", _typeRubyModule, def.Definition);
                    }

                    if (def.IsExtension) {
                        _output.Write("ExtendModule(typeof({0}), {1}, {2}, ",
                            _known.TypeName(def.Extends),
                            def.GetModuleRestrictions(),
                            def.GetInitializerDelegates()
                        );
                    } else {
                        _output.Write("Define{0}Module(\"{1}\", typeof({2}), {3}, {4}, ",
                            def.IsGlobal ? "Global" : "",
                            def.QualifiedName,
                            _known.TypeName(def.Extends),
                            def.GetModuleRestrictions(),
                            def.GetInitializerDelegates()
                        );
                    }

                    GenerateInclusions(def, false);

                    _output.WriteLine(");");
                    break;

                case ModuleKind.Singleton:
                    if (def.Definition != null) {
                        _output.Write("object {0} = ", def.Definition);
                    }

                    _output.Write("DefineSingleton({0}, ", def.GetInitializerDelegates());

                    GenerateInclusions(def, false);

                    _output.WriteLine(");");
                    break;
            }
        }

        private void GenerateInclusions(ModuleDef def, bool makeArray) {
            List<string> mixinRefs = new List<string>();
            AddMixinRefsRecursive(mixinRefs, def);

            if (mixinRefs.Count > 0) {
                if (makeArray) {
                    _output.Write("new {0}[] {{", _typeRubyModule);
                }
                bool first = true;
                foreach (string mixinRef in mixinRefs) {
                    if (first) {
                        first = false;
                    } else {
                        _output.Write(", ");
                    }
                    _output.Write(mixinRef);
                }
                if (makeArray) {
                    _output.Write("}");
                }
            } else {
                _output.Write("{0}.EmptyArray", _typeRubyModule);
            }
        }

        private static void AddMixinRefsRecursive(List<string> mixinRefs, ModuleDef def) {
            foreach (MixinRef mixin in def.Mixins) {
                if (mixin.Copy) {
                    if (mixin.Module.Definition != null) {
                        AddMixinRefsRecursive(mixinRefs, mixin.Module.Definition);
                    }
                } else {
                    mixinRefs.Add(mixin.Module.RefName);
                }
            }
        }

        // A generic type definition has the Ruby name of its non-generic namesake (System::IComparable): the id
        // breaks the tie so that the output does not depend on what else is defined.
        private static int CompareNames(ModuleDef x, ModuleDef y) {
            int result = LegacyCollation.Instance.Compare(x.QualifiedName, y.QualifiedName);
            return (result != 0) ? result : String.CompareOrdinal(x.Id, y.Id);
        }

        private void GenerateTraitInitializations() {
            // Sort the items before we generate them to improve diff quality
            var worklist = new List<ModuleDef>(_moduleDefs.Values);
            worklist.Sort(CompareNames);

            foreach (ModuleDef moduleDef in worklist) {
                GenerateConstantsInitialization(moduleDef);
                GenerateTraitInitialization(moduleDef, true);
                GenerateTraitInitialization(moduleDef, false);
            }
        }

        private void GenerateTraitInitialization(ModuleDef moduleDef, bool isInstance) {
            if (isInstance ? moduleDef.HasInstanceInitializer : moduleDef.HasClassInitializer) {
                _output.WriteLine("private static void Load{0}_{2}({1}/*!*/ module) {{", moduleDef.Id, _typeRubyModule, isInstance ? "Instance" : "Class");
                _output.Indent++;

                GenerateIncludedTraitLoaders(moduleDef, isInstance);
                GenerateHiddenMethods(isInstance ? moduleDef.HiddenInstanceMethods : moduleDef.HiddenClassMethods);
                GenerateMethods(isInstance ? moduleDef.InstanceMethods : moduleDef.ClassMethods);
                GenerateMethodAliases(isInstance ? moduleDef.InstanceMethodAliases : moduleDef.ClassMethodAliases);

                _output.Indent--;
                _output.WriteLine("}");
                _output.WriteLine();
            }
        }

        private void GenerateConstantsInitialization(ModuleDef moduleDef) {
            if (moduleDef.HasConstantsInitializer) {
                _output.WriteLine("private static void Load{0}_Constants({1}/*!*/ module) {{", moduleDef.Id, _typeRubyModule);
                _output.Indent++;

                GenerateIncludedConstantLoaders(moduleDef);
                GenerateConstants(moduleDef);

                _output.Indent--;
                _output.WriteLine("}");
                _output.WriteLine();
            }
        }

        private void GenerateConstants(ModuleDef moduleDef) {
            // add constants on module
            foreach (var constantDef in moduleDef.Constants.Values) {
                GenerateSetConstant("module", constantDef.Name, String.Format("{0}.{1}{2}",
                    _known.TypeName(constantDef.Member.ContainingType),
                    constantDef.Member.Name,
                    constantDef.Member is IMethodSymbol ? "(module)" : null
                ));
            }
            _output.WriteLine();
        }

        private void GenerateIncludedTraitLoaders(ModuleDef moduleDef, bool isInstance) {
            foreach (MixinRef mixin in moduleDef.Mixins) {
                ModuleDef def = mixin.Module.Definition;
                if (mixin.Copy && def != null && (isInstance ? def.HasInstanceInitializer : def.HasClassInitializer)) {
                    _output.WriteLine("Load{0}_{1}(module);", def.Id, isInstance ? "Instance" : "Class");
                }
            }
        }

        private void GenerateIncludedConstantLoaders(ModuleDef moduleDef) {
            foreach (MixinRef mixin in moduleDef.Mixins) {
                ModuleDef def = mixin.Module.Definition;
                if (mixin.Copy && def != null && def.HasConstantsInitializer) {
                    _output.WriteLine("Load{0}_Constants(module);", def.Id);
                }
            }
        }

        private void GenerateHiddenMethods(SortedDictionary<string, HiddenMethod> methods) {
            foreach (KeyValuePair<string, HiddenMethod> entry in methods) {
                if (entry.Value == HiddenMethod.Undefined) {
                    _output.WriteLine("module.{0}(\"{1}\");",
                        Builtins ? "UndefineMethodNoEvent" : "UndefineMethod",
                        entry.Key
                    );
                } else {
                    _output.WriteLine("module.HideMethod(\"{0}\");", entry.Key);
                }
            }
        }

        private void GenerateMethodAliases(SortedDictionary<string, string> aliases) {
            foreach (var alias in aliases) {
                _output.WriteLine("module.AddMethodAlias(\"{0}\", \"{1}\");", alias.Key, alias.Value);
            }
        }

        private void GenerateMethods(SortedDictionary<string, MethodDef> methods) {
            foreach (MethodDef def in methods.Values) {
                if (def.IsRuleGenerator(_known)) {
                    _output.WriteLine("DefineRuleGenerator(module, \"{0}\", 0x{1:x}, {2}.{3}());",
                        def.Name,
                        def.Attributes,
                        _known.TypeName(def.Overloads[0].ContainingType),
                        def.Overloads[0].Name);
                } else {
                    _output.Write("DefineLibraryMethod(module, \"{0}\", 0x{1:x}", def.Name, def.Attributes);

                    _output.WriteLine(", ");
                    _output.Indent++;
                    GenerateParameterAttributes(def.Overloads);
                    _output.Indent--;
                    _output.WriteLine(", ");
                    GenerateDelegatesListCreation(def.Overloads);

                    _output.WriteLine(");");
                }

                _output.WriteLine();
            }
        }

        private void GenerateDelegatesListCreation(List<IMethodSymbol> methods) {
            _output.Indent++;
            bool first = true;
            foreach (IMethodSymbol method in methods) {
                if (first) {
                    first = false;
                } else {
                    _output.WriteLine(", ");
                }
                GenerateDelegateCreation(method);
            }
            _output.Indent--;
            _output.WriteLine();
        }

        private void GenerateParameterAttributes(List<IMethodSymbol> methods) {
            if (methods.Count > _known.MaxOverloads) {
                _output.Write("new[] { ");
            }

            bool first = true;
            foreach (IMethodSymbol method in methods) {
                if (first) {
                    first = false;
                } else {
                    _output.Write(", ");
                }
                _output.Write("0x{0,8:x8}U", EncodeCustomAttributes(method));
            }

            if (methods.Count > _known.MaxOverloads) {
                _output.Write("}");
            }
        }

        private void GenerateExceptionFactoryDelegateList(ModuleDef moduleDef) {
            _output.Write("new {0}{1}({2}.{3}{4})",
                TypeFunction,
                _known.TypeArgs(_known.RubyClass, _known.Object, _known.Exception),
                InitializerName,
                ExceptionFactoryPrefix,
                moduleDef.Id
            );
        }

        private void GenerateExceptionFactories() {
            // TODO: sort by name
            foreach (var moduleDef in _moduleDefs.Values) {
                if (moduleDef.IsException) {
                    // public static Exception/*!*/ Factory(RubyClass/*!*/ self, [DefaultParameterValue(null)]object message) {
                    //     return InitializeException(new Exception(GetClrMessage(self, message)), message);
                    // }

                    _output.WriteLine("public static {0}/*!*/ {1}{2}({3}/*!*/ self, [{4}(null)]object message) {{",
                        _known.TypeName(_known.Exception),
                        ExceptionFactoryPrefix,
                        moduleDef.Id,
                        _typeRubyClass,
                        "DefaultParameterValueAttribute"
                    );
                    _output.Indent++;

                    _output.WriteLine("return {0}.{2}(new {1}({0}.{3}(self, message), ({4})null), message);",
                        "IronRuby.Runtime.RubyExceptionData",
                        _known.TypeName(moduleDef.Extends),
                        "InitializeException",
                        "GetClrMessage",
                        _known.TypeName(_known.Exception)
                    );

                    _output.Indent--;
                    _output.WriteLine("}");
                    _output.WriteLine();
                }
            }
        }

        private void GenerateDelegateCreation(IMethodSymbol method) {
            var paramTypes = new List<ITypeSymbol>();
            foreach (IParameterSymbol parameter in method.Parameters) {
                paramTypes.Add(parameter.Type);
            }

            string delegateType;
            if (!method.ReturnsVoid) {
                delegateType = TypeFunction;
                paramTypes.Add(method.ReturnType);
            } else {
                delegateType = TypeAction;
            }

            _output.Write("new {0}{1}({2}.{3})",
                delegateType,
                _known.FormatTypeArgs(new StringBuilder(), paramTypes).ToString(),
                _known.TypeName(method.ContainingType),
                method.Name
            );
        }

        #endregion
    }
}
