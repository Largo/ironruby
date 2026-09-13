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
using System.Collections.Generic;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Refinement, the class of the anonymous module Module#refine returns.
    ///
    /// It is a Module subclass, but the parts of Module that splice a module into somebody's ancestors
    /// are removed: a refinement is only ever reached through the lexical activation in
    /// RubyCallAction.Resolve, never through the MRO, so include/prepend/extend on one would be a lie.
    /// RubyContext.RefinementClass undefines append_features, prepend_features and extend_object on it
    /// for the same reason.
    /// </summary>
    [RubyClass("Refinement", Inherits = typeof(RubyModule))]
    public static class RefinementOps {

        [RubyMethod("target")]
        public static RubyModule GetTarget(RubyModule/*!*/ self) {
            return self.RefinedModule;
        }

        [RubyMethod("include")]
        public static RubyModule Include(RubyModule/*!*/ self, params object[]/*!*/ modules) {
            throw RubyExceptions.CreateTypeError("Refinement#include has been removed");
        }

        [RubyMethod("prepend")]
        public static RubyModule Prepend(RubyModule/*!*/ self, params object[]/*!*/ modules) {
            throw RubyExceptions.CreateTypeError("Refinement#prepend has been removed");
        }

        /// <summary>
        /// Copies a module's instance methods into the refinement.  Unlike include it does not create an
        /// ancestor relationship - the methods become the refinement's own, which is the only shape a
        /// refinement can take.
        /// </summary>
        [RubyMethod("import_methods", RubyMethodAttributes.PrivateInstance)]
        public static RubyModule/*!*/ ImportMethods(RubyModule/*!*/ self, params object[]/*!*/ modules) {
            foreach (object obj in modules) {
                RubyModule module = obj as RubyModule;
                if (module == null || module.IsClass) {
                    throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Module)",
                        self.Context.GetClassDisplayName(obj)
                    );
                }
            }

            foreach (object obj in modules) {
                RubyModule module = (RubyModule)obj;
                var members = new List<KeyValuePair<string, RubyMemberInfo>>();
                using (self.Context.ClassHierarchyLocker()) {
                    module.ForEachMember(false, RubyMethodAttributes.DefaultVisibility, (name, owner, member) => {
                        members.Add(new KeyValuePair<string, RubyMemberInfo>(name, member));
                    });
                }
                foreach (var entry in members) {
                    self.SetMethodNoEvent(self.Context, entry.Key, entry.Value);
                }
            }
            return self;
        }
    }
}
