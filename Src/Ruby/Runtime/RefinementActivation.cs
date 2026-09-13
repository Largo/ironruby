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
using System.Diagnostics;
using IronRuby.Builtins;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime {
    /// <summary>
    /// The set of refinements that are active in a lexical scope.
    ///
    /// Refinements are lexically scoped: `using M' activates M's refinements from that point to the
    /// end of the enclosing file, eval string, class body or block, and in everything nested inside
    /// it.  IronRuby's <see cref="RubyScope"/> parent chain is exactly that lexical nesting (a method
    /// scope's parent is the scope that was current when `def' ran - see RubyMethodInfo.DeclaringScope
    /// - and a block scope's parent is the block's defining scope), which is why the same chain that
    /// backs Module.nesting and constant lookup can back refinement lookup.
    ///
    /// An instance of this class is an immutable snapshot of "which modules were `using'd in which
    /// enclosing scopes".  Instances are canonical per scope: a scope that does not call `using'
    /// shares its parent's instance, and a chain with no `using' anywhere shares <see cref="Empty"/>.
    /// That canonicity is what makes the instance usable as a call-site rule guard - see
    /// RubyCallAction.Resolve, which emits `GetActiveRefinements(scope) == &lt;this instance&gt;'.
    /// </summary>
    public sealed class RefinementActivation {
        public static readonly RefinementActivation/*!*/ Empty = new RefinementActivation(null, null);

        private readonly RefinementActivation _outer;

        // The modules passed to `using' in this scope, in activation order (earliest first).
        // Null for Empty.
        private readonly RubyModule[] _usedModules;

        private RefinementActivation(RefinementActivation outer, RubyModule[] usedModules) {
            _outer = outer;
            _usedModules = usedModules;
        }

        internal static RefinementActivation/*!*/ CreateSingle(RefinementActivation/*!*/ outer, RubyModule/*!*/ usedModule) {
            Assert.NotNull(outer, usedModule);
            return new RefinementActivation(outer, new RubyModule[] { usedModule });
        }

        internal static RefinementActivation/*!*/ Create(RefinementActivation/*!*/ outer, List<RubyModule/*!*/>/*!*/ usedModules) {
            Assert.NotNull(outer, usedModules);
            if (usedModules.Count == 0) {
                return outer;
            }
            return new RefinementActivation(outer, usedModules.ToArray());
        }

        public bool IsEmpty {
            get { return _usedModules == null && _outer == null; }
        }

        /// <summary>
        /// The modules that were passed to `using' and are visible here, innermost scope first and,
        /// within a scope, most recently activated first.  This is Module.used_modules' order.
        /// </summary>
        public void GetUsedModules(List<RubyModule/*!*/>/*!*/ result) {
            for (RefinementActivation a = this; a != null; a = a._outer) {
                if (a._usedModules != null) {
                    for (int i = a._usedModules.Length - 1; i >= 0; i--) {
                        RubyModule m = a._usedModules[i];
                        if (!result.Contains(m)) {
                            result.Add(m);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Appends the refinement modules that are active for <paramref name="refinedModule"/>, most
        /// specific first (innermost scope first, latest `using' first).  A refinement is searched
        /// immediately ahead of the module it refines in the MRO, so the caller splices the result in
        /// front of <paramref name="refinedModule"/>.
        /// </summary>
        internal void GetRefinementsOf(RubyModule/*!*/ refinedModule, List<RubyModule/*!*/>/*!*/ result) {
            for (RefinementActivation a = this; a != null; a = a._outer) {
                if (a._usedModules == null) {
                    continue;
                }
                // later `using' wins over an earlier one in the same scope:
                for (int i = a._usedModules.Length - 1; i >= 0; i--) {
                    a._usedModules[i].GetActiveRefinementsOf(refinedModule, result);
                }
            }
        }

        /// <summary>
        /// All refinement modules visible here, for Module.used_refinements.
        /// </summary>
        public void GetUsedRefinements(List<RubyModule/*!*/>/*!*/ result) {
            var mods = new List<RubyModule>();
            GetUsedModules(mods);
            foreach (RubyModule m in mods) {
                m.GetAllRefinements(result);
            }
        }
    }
}
