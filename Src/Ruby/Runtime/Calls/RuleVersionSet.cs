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

using System.Linq.Expressions;

using System;
using System.Reflection;
using System.Threading;
using Microsoft.Scripting.Interpreter;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Calls {
    /// <summary>
    /// The class versions (<see cref="VersionHandle.Method"/>) a shared call-site rule holds for.
    ///
    /// A rule bound for a receiver whose class is an <see cref="IRubyObject"/> is valid as long as the
    /// receiver's immediate class has the version it was bound against. Method versions are unique
    /// across all classes and never reused, so a version identifies a class in one particular state.
    /// When receivers of many classes - or one class in many states - resolve a call to the same method,
    /// one rule can serve all of them: its test checks the receiver's class version against this set,
    /// and every version is added only after the method has been resolved again for that class
    /// (see RubyCallAction.BindShared). This saves building and compiling a rule per class, which is
    /// what calls in class bodies (private, attr_reader, alias_method, const_set...) and calls on
    /// classes whose version was bumped by a method definition elsewhere would otherwise cost.
    /// </summary>
    public sealed class RuleVersionSet {
        // open addressing, 0 marks an empty slot (method versions start at 1);
        // readers see either the old or the new array, an insert writes a single slot:
        private int[] _slots;
        private int _count;

        // stop growing: further classes get rules of their own
        private const int MaxCount = 4096;

        /// <summary>
        /// The method the rule calls; set while the rule is built (under the class hierarchy lock).
        /// </summary>
        internal RubyMemberInfo Member;

        internal RuleVersionSet() {
            _slots = new int[8];
        }

        internal int Count {
            get { return _count; }
        }

        public bool Contains(int version) {
            int[] slots = Volatile.Read(ref _slots);
            int mask = slots.Length - 1;
            int i = version & mask;
            while (true) {
                int slot = slots[i];
                if (slot == version) {
                    return slot != 0;
                }
                if (slot == 0) {
                    return false;
                }
                i = (i + 1) & mask;
            }
        }

        /// <summary>
        /// Adds a version the rule has been verified to hold for. Returns false if the set is full.
        /// </summary>
        internal bool TryAdd(int version) {
            if (version == 0) {
                return false;
            }

            lock (this) {
                if (Contains(version)) {
                    return true;
                }

                if (_count >= MaxCount) {
                    return false;
                }

                int[] slots = _slots;
                if ((_count + 1) * 2 > slots.Length) {
                    var newSlots = new int[slots.Length * 2];
                    foreach (int v in slots) {
                        if (v != 0) {
                            Insert(newSlots, v);
                        }
                    }
                    Insert(newSlots, version);
                    Volatile.Write(ref _slots, newSlots);
                } else {
                    Insert(slots, version);
                }
                _count++;
                return true;
            }
        }

        private static void Insert(int[]/*!*/ slots, int version) {
            int mask = slots.Length - 1;
            int i = version & mask;
            while (slots[i] != 0) {
                i = (i + 1) & mask;
            }
            slots[i] = version;
        }

        public static bool IsValid(RuleVersionSet/*!*/ set, int version) {
            return set.Contains(version);
        }

        private static MethodInfo _IsValidMethod;

        /// <summary>
        /// (#{versionExpression}) in #{set}
        /// </summary>
        internal Expression/*!*/ MakeTest(Expression/*!*/ versionExpression) {
            var method = _IsValidMethod ?? (_IsValidMethod = CallInstruction.CacheFunc<RuleVersionSet, int, bool>(IsValid));
            return Expression.Call(method, AstUtils.Constant(this), versionExpression);
        }
    }
}
