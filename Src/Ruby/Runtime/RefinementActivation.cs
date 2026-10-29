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
using System.Runtime.CompilerServices;
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
    /// enclosing scopes".  Instances are canonical: a scope that does not call `using' shares its
    /// parent's instance, a chain with no `using' anywhere shares <see cref="Empty"/>, and two tables
    /// with the same contents are the same instance (see Intern).  That canonicity is what makes the
    /// instance usable as a call-site rule guard - see RubyCallAction.Resolve, which emits
    /// `GetActiveRefinements(scope) == &lt;this instance&gt;'.
    /// </summary>
    public sealed class RefinementActivation {
        public static readonly RefinementActivation/*!*/ Empty = new RefinementActivation(null, null, null);

        // Bounds a runtime's intern table, for a program that `using's a fresh anonymous module over
        // and over. Past it, tables are simply not shared: a guard then fails where it need not.
        private const int MaxInterned = 4096;

        private readonly RefinementActivation _outer;

        // The modules passed to `using' in this scope, in activation order (earliest first).
        // Null for Empty.
        private readonly RubyModule[] _usedModules;

        // Parallel to _usedModules: the refinements `using' found in the module when it ran, or null
        // for an activation that follows the module as it grows (a refine block and the methods defined
        // in it see every refinement their module declares, later ones included).  CRuby's `using'
        // copies the module's { refined class -> refinement } table into the cref, so a refinement of a
        // class the module had not refined yet is not picked up, while methods added to an already
        // copied refinement are - it is the same module.
        private readonly HashSet<RubyModule>[] _snapshots;

        private RefinementActivation(RefinementActivation outer, RubyModule[] usedModules, HashSet<RubyModule>[] snapshots) {
            _outer = outer;
            _usedModules = usedModules;
            _snapshots = snapshots;
        }

        internal static RefinementActivation/*!*/ CreateSingle(RefinementActivation/*!*/ outer, RubyModule/*!*/ usedModule) {
            Assert.NotNull(outer, usedModule);
            return Intern(new RefinementActivation(outer, new RubyModule[] { usedModule }, new HashSet<RubyModule>[1]));
        }

        internal static RefinementActivation/*!*/ Create(RefinementActivation/*!*/ outer, List<RubyModule/*!*/>/*!*/ usedModules,
            List<HashSet<RubyModule>>/*!*/ snapshots) {
            Assert.NotNull(outer, usedModules, snapshots);
            Debug.Assert(usedModules.Count == snapshots.Count);
            if (usedModules.Count == 0) {
                return outer;
            }
            return Intern(new RefinementActivation(outer, usedModules.ToArray(), snapshots.ToArray()));
        }

        /// <summary>
        /// The instance with <paramref name="candidate"/>'s contents.  Every `using' anywhere makes every
        /// scope recompute its table (RubyContext.RefinementVersion); interning is what lets the scopes it
        /// did not change come back with the very instance their call sites' rules were guarded on, so a
        /// file that `using's something does not re-bind the refined calls of every other file.
        /// </summary>
        private static RefinementActivation/*!*/ Intern(RefinementActivation/*!*/ candidate) {
            var table = candidate._usedModules[0].Context.RefinementActivations;
            lock (table) {
                RefinementActivation existing;
                if (table.TryGetValue(candidate, out existing)) {
                    return existing;
                }
                if (table.Count < MaxInterned) {
                    table.Add(candidate, candidate);
                }
                return candidate;
            }
        }

        /// <summary>
        /// Equality of contents: the same outer table (itself interned, so compared by reference), the same
        /// modules in the same order, and for each the same snapshot of its refinements.
        /// </summary>
        internal static readonly IEqualityComparer<RefinementActivation>/*!*/ ContentComparer = new ContentEqualityComparer();

        private sealed class ContentEqualityComparer : IEqualityComparer<RefinementActivation> {
            public bool Equals(RefinementActivation x, RefinementActivation y) {
                if (ReferenceEquals(x, y)) {
                    return true;
                }
                if (x == null || y == null || !ReferenceEquals(x._outer, y._outer) ||
                    x._usedModules == null || y._usedModules == null || x._usedModules.Length != y._usedModules.Length) {
                    return false;
                }
                for (int i = 0; i < x._usedModules.Length; i++) {
                    if (!ReferenceEquals(x._usedModules[i], y._usedModules[i])) {
                        return false;
                    }
                    HashSet<RubyModule> xs = x._snapshots[i], ys = y._snapshots[i];
                    if (xs == null ? ys != null : (ys == null || !xs.SetEquals(ys))) {
                        return false;
                    }
                }
                return true;
            }

            public int GetHashCode(RefinementActivation obj) {
                int hash = (obj._outer != null) ? RuntimeHelpers.GetHashCode(obj._outer) : 0;
                if (obj._usedModules != null) {
                    for (int i = 0; i < obj._usedModules.Length; i++) {
                        hash = hash * 31 + RuntimeHelpers.GetHashCode(obj._usedModules[i]);
                        hash = hash * 31 + ((obj._snapshots[i] != null) ? obj._snapshots[i].Count + 1 : 0);
                    }
                }
                return hash;
            }
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
                    a._usedModules[i].GetActiveRefinementsOf(refinedModule, result, a._snapshots[i]);
                }
            }
        }

        /// <summary>
        /// All refinement modules visible here, for Module.used_refinements.
        /// </summary>
        public void GetUsedRefinements(List<RubyModule/*!*/>/*!*/ result) {
            var mods = new List<RubyModule>();
            GetUsedModules(mods);
            var all = new List<RubyModule>();
            foreach (RubyModule m in mods) {
                all.Clear();
                m.GetAllRefinements(all);
                foreach (RubyModule r in all) {
                    if (!result.Contains(r) && IsVisible(m, r)) {
                        result.Add(r);
                    }
                }
            }
        }

        // False if every `using' of module m visible here predates refinement r.
        private bool IsVisible(RubyModule/*!*/ m, RubyModule/*!*/ r) {
            for (RefinementActivation a = this; a != null; a = a._outer) {
                if (a._usedModules != null) {
                    for (int i = 0; i < a._usedModules.Length; i++) {
                        if (a._usedModules[i] == m && (a._snapshots[i] == null || a._snapshots[i].Contains(r))) {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
