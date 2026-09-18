/* ****************************************************************************
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

using System;
using System.Diagnostics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Methods on Singleton(main).
    /// </summary>
    [RubyModule(RubyClass.MainSingletonName)]
    public static class MainSingletonOps {
        #region Private Instance Methods

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static object/*!*/ Initialize(object/*!*/ self) {
            // TODO:
            throw new NotImplementedException("TODO");
        }

        #endregion

        #region Public Instance Methods

        // The top-level object answers "main" to both, as MRI's does: Kernel#inspect no longer
        // falls back to #to_s, so without its own #inspect main would report itself as
        // "#<Object:0x...>" in NoMethodError messages and under Kernel#p.
        [RubyMethod("to_s", RubyMethodAttributes.PublicInstance)]
        [RubyMethod("inspect", RubyMethodAttributes.PublicInstance)]
        public static MutableString/*!*/ ToS(object/*!*/ self) {
            return MutableString.CreateAscii("main");
        }

        // thread-safe:
        [RubyMethod("public", RubyMethodAttributes.PublicInstance)]
        public static RubyModule/*!*/ SetPublicVisibility(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNullItems]params string/*!*/[]/*!*/ methodNames) {

            return SetVisibility(scope, self, methodNames, RubyMethodAttributes.PublicInstance);
        }

        // thread-safe:
        [RubyMethod("private", RubyMethodAttributes.PublicInstance)]
        public static RubyModule/*!*/ SetPrivateVisibility(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNullItems]params string/*!*/[]/*!*/ methodNames) {

            return SetVisibility(scope, self, methodNames, RubyMethodAttributes.PrivateInstance);
        }

        private static RubyModule/*!*/ SetVisibility(RubyScope/*!*/ scope, object/*!*/ self, string/*!*/[]/*!*/ methodNames, RubyMethodAttributes attributes) {
            Assert.NotNull(scope, self, methodNames);
            RubyModule module;

            // MRI: Method is searched in the class of self (Object), not in the main singleton class.
            // IronRuby specific: If we are in a top-level scope with redirected method lookup module we use that module (hosted scopes).
            var topScope = scope.Top.GlobalScope.TopLocalScope;
            if (scope == topScope && topScope.MethodLookupModule != null) {
                module = topScope.MethodLookupModule;
            } else {
                module = scope.RubyContext.GetClassOf(self);
            }
            ModuleOps.SetMethodAttributes(scope, module, methodNames, attributes);
            return module;
        }

        /// <summary>
        /// The module a definition at the top level lands in: Object, or whatever a hosted scope
        /// redirects method lookup to. The same one `private' and `public' work on up there.
        /// </summary>
        private static RubyModule/*!*/ GetDefinitionModule(RubyScope/*!*/ scope, object/*!*/ self) {
            var topScope = scope.Top.GlobalScope.TopLocalScope;
            if (scope == topScope && topScope.MethodLookupModule != null) {
                return topScope.MethodLookupModule;
            }
            return scope.RubyContext.GetClassOf(self);
        }

        /// <summary>
        /// A method `def' writes at the top level is private, but one main.define_method writes is
        /// public - MRI's define_method always is, and at the top level it is main's own method
        /// rather than Module's that runs.
        /// </summary>
        private static RubySymbol/*!*/ MakePublic(RubyScope/*!*/ scope, RubyModule/*!*/ module, RubySymbol/*!*/ name) {
            ModuleOps.SetMethodAttributes(scope, module, new[] { name.ToString() }, RubyMethodAttributes.PublicInstance);
            return name;
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName) {

            var module = GetDefinitionModule(scope, self);
            return MakePublic(scope, module, ModuleOps.DefineMethod(scope, block, module, methodName));
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]Proc/*!*/ method) {

            var module = GetDefinitionModule(scope, self);
            return MakePublic(scope, module, ModuleOps.DefineMethod(scope, module, methodName, method));
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]RubyMethod/*!*/ method) {

            var module = GetDefinitionModule(scope, self);
            return MakePublic(scope, module, ModuleOps.DefineMethod(scope, module, methodName, method));
        }

        [RubyMethod("define_method", RubyMethodAttributes.PrivateInstance)]
        public static RubySymbol/*!*/ DefineMethod(RubyScope/*!*/ scope, object/*!*/ self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]UnboundMethod/*!*/ method) {

            var module = GetDefinitionModule(scope, self);
            return MakePublic(scope, module, ModuleOps.DefineMethod(scope, module, methodName, method));
        }

        /// <summary>
        /// main.using activates a module's refinements for the rest of the current file or eval string.
        /// It is only legal at the top level: CRuby refuses to let a method body activate refinements,
        /// because the activation would be for the caller's lexical scope rather than its own.
        /// </summary>
        [RubyMethod("using", RubyMethodAttributes.PrivateInstance)]
        public static object Using(RubyScope/*!*/ scope, object/*!*/ self, object module) {
            for (RubyScope s = scope; s != null; s = s.Parent) {
                if (s.Kind == ScopeKind.Method || s.Kind == ScopeKind.BlockMethod) {
                    throw RubyExceptions.CreateRuntimeError("main.using is permitted only at toplevel");
                }
                if (s.Kind == ScopeKind.TopLevel) {
                    break;
                }
            }

            ModuleOps.ActivateRefinements(scope, module);
            return self;
        }

        // thread-safe:
        [RubyMethod("include", RubyMethodAttributes.PublicInstance)]
        public static RubyClass/*!*/ Include(RubyContext/*!*/ context, object/*!*/ self, params RubyModule[]/*!*/ modules) {
            RubyClass result = context.GetClassOf(self);
            result.IncludeModules(modules);
            return result;
        }

        #endregion
    }
}
