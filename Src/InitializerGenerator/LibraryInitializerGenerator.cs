using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace IronRuby.InitializerGenerator {
    /// <summary>
    /// Writes the library initializers of the assembly being compiled.
    ///
    /// IronRuby does not find [RubyModule], [RubyClass], [RubyMethod] and [RubyConstant] by reflection at run
    /// time: a LibraryInitializer subclass per library namespace registers them, with every method's delegate
    /// and parameter attributes spelled out. Those classes used to be a checked-in file that a separate tool
    /// (ClassInitGenerator) regenerated from the built assembly, so a method added without rerunning it was
    /// never registered, and a changed parameter attribute silently kept its old meaning. Here they are made
    /// from the same sources the compiler is compiling, in the same build.
    ///
    /// A library is a namespace that has [RubyModule] types and is not nested in another one that has them;
    /// the types of nested namespaces belong to it. Its initializer is &lt;last part of the namespace&gt;LibraryInitializer.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class LibraryInitializerGenerator : IIncrementalGenerator {
        internal const string HintName = "Initializers.Generated.cs";

        private static readonly DiagnosticDescriptor _failure = new DiagnosticDescriptor(
            "IRGEN999", "Library initializer generator failed", "The library initializers were not generated: {0}",
            "IronRuby.InitializerGenerator", DiagnosticSeverity.Error, true
        );

        private const string LicenseStatement =
@"/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

";

        public void Initialize(IncrementalGeneratorInitializationContext context) {
            context.RegisterSourceOutput(context.CompilationProvider, static (output, compilation) => {
                // An exception escaping a generator is only a warning (CS8785) and the build goes on without
                // its output: an assembly with no initializers at all. Make it the error it is.
                try {
                    string source = Generate(compilation, output.ReportDiagnostic);
                    if (source != null) {
                        output.AddSource(HintName, SourceText.From(source, Encoding.UTF8));
                    }
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception e) {
                    output.ReportDiagnostic(Diagnostic.Create(_failure, Location.None,
                        e is MissingSymbolException ? e.Message : e.ToString().Replace("\r", "").Replace("\n", " | ")));
                }
            });
        }

        /// <summary>
        /// The source of the initializers; null if the compilation defines no Ruby library or one is invalid
        /// (which has been reported).
        /// </summary>
        internal static string Generate(Compilation compilation, Action<Diagnostic> report) {
            var known = KnownSymbols.Create(compilation);
            if (known == null) {
                return null;
            }

            var allTypes = new List<INamedTypeSymbol>();
            CollectTypes(compilation.Assembly.GlobalNamespace, allTypes);

            var namespaces = new SortedSet<string>(StringComparer.Ordinal);
            foreach (INamedTypeSymbol type in allTypes) {
                string ns = KnownSymbols.NamespaceName(type);
                if (ns != null && LibraryDef.IsModuleDefinition(known, type)) {
                    namespaces.Add(ns);
                }
            }

            var libraries = new List<LibraryDef>();
            foreach (string ns in namespaces) {
                bool nested = false;
                foreach (string other in namespaces) {
                    nested |= other.Length < ns.Length && LibraryDef.IsWithin(ns, other);
                }
                if (!nested) {
                    libraries.Add(new LibraryDef(known, ns, report));
                }
            }

            if (libraries.Count == 0) {
                return null;
            }

            bool anyErrors = false;
            foreach (LibraryDef library in libraries) {
                library.ReflectTypes(allTypes);
                anyErrors |= library.AnyErrors;
            }

            if (anyErrors) {
                return null;
            }

            var output = new IndentedWriter();
            output.WriteRaw(LicenseStatement.Replace("\r", ""));
            output.WriteLine("#pragma warning disable 169 // mcs: unused private method");

            foreach (LibraryDef library in libraries) {
                output.WriteLine("[assembly: {2}(typeof({0}.{1}))]", library.Namespace, library.InitializerName, "IronRuby.Runtime.RubyLibraryAttribute");
            }

            output.WriteLine();

            foreach (LibraryDef library in libraries) {
                library.GenerateCode(output);
            }

            return output.ToString();
        }

        private static void CollectTypes(INamespaceSymbol ns, List<INamedTypeSymbol> result) {
            var types = new List<INamedTypeSymbol>(ns.GetTypeMembers());
            types.Sort((x, y) => String.CompareOrdinal(x.MetadataName, y.MetadataName));
            foreach (INamedTypeSymbol type in types) {
                CollectNestedTypes(type, result);
            }

            var namespaces = new List<INamespaceSymbol>(ns.GetNamespaceMembers());
            namespaces.Sort((x, y) => String.CompareOrdinal(x.Name, y.Name));
            foreach (INamespaceSymbol nested in namespaces) {
                CollectTypes(nested, result);
            }
        }

        private static void CollectNestedTypes(INamedTypeSymbol type, List<INamedTypeSymbol> result) {
            result.Add(type);
            foreach (INamedTypeSymbol nested in type.GetTypeMembers()) {
                CollectNestedTypes(nested, result);
            }
        }
    }
}
