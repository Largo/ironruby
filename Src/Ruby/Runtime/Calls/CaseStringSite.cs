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

using System.Collections.Generic;
using IronRuby.Builtins;

namespace IronRuby.Runtime.Calls {

    /// <summary>
    /// A `case' whose `when's are all String literals, ASCII in an ASCII-compatible encoding, dispatched
    /// the way MRI's opt_case_dispatch does it: the subject is looked up among the literals once, and
    /// the first clause with an equal one is taken - as long as String#=== is still the library's own
    /// method, which compares contents and calls nothing. Otherwise - the subject not a String, or one
    /// the lookup cannot read in place, String#=== redefined, aliased or prepended over, a refinement
    /// about - the compiled code makes the `===' calls as written (CaseExpression). A `case' over a
    /// parser's current character (AWFY Json's read_value, eleven literals) made a String per literal
    /// and a call per `when' until one matched.
    /// </summary>
    public sealed class CaseStringSite {
        /// <summary>Returned when the `===' calls have to be made as written.</summary>
        public const int NotHandled = -2;

        /// <summary>Returned when no literal equals the subject.</summary>
        public const int NoClause = -1;

        private sealed class Binding {
            internal readonly RubyContext/*!*/ Context;
            internal readonly int Version;
            internal readonly bool Intact;

            internal Binding(RubyContext/*!*/ context, int version, bool intact) {
                Context = context;
                Version = version;
                Intact = intact;
            }
        }

        // public, as are Literals and Clauses: the startup library (Util/aot) re-creates the site at load time
        private readonly string/*!*/[]/*!*/ _literals;
        private readonly int[]/*!*/ _clauses;

        // the clause of each one-character literal, by its character, and of each longer one
        private readonly int[]/*!*/ _byChar;
        private readonly Dictionary<string, int>/*!*/ _byString;
        private volatile Binding _binding;

        /// <param name="literals">Every literal of every `when', in order: the characters of each.</param>
        /// <param name="clauses">The index of the `when' each literal is in.</param>
        public CaseStringSite(string/*!*/[]/*!*/ literals, int[]/*!*/ clauses) {
            _literals = literals;
            _clauses = clauses;
            _byChar = new int[128];
            for (int i = 0; i < _byChar.Length; i++) {
                _byChar[i] = NoClause;
            }
            _byString = new Dictionary<string, int>(System.StringComparer.Ordinal);
            // the first `when' with a literal wins, as the calls in order would have it
            for (int i = 0; i < literals.Length; i++) {
                string literal = literals[i];
                if (literal.Length == 1) {
                    if (_byChar[literal[0]] == NoClause) {
                        _byChar[literal[0]] = clauses[i];
                    }
                } else if (!_byString.ContainsKey(literal)) {
                    _byString.Add(literal, clauses[i]);
                }
            }
        }

        public string/*!*/[]/*!*/ Literals => _literals;
        public int[]/*!*/ Clauses => _clauses;

        /// <summary>The index of the clause to take, <see cref="NoClause"/>, or <see cref="NotHandled"/>.</summary>
        internal int Dispatch(RubyContext/*!*/ context, object value) {
            var str = value as MutableString;
            if (str == null || context.HasRefinements) {
                return NotHandled;
            }

            var binding = _binding;
            if (binding == null || binding.Version != RubyModule.GlobalMethodVersion || binding.Context != context) {
                _binding = binding = Bind(context);
            }
            if (!binding.Intact) {
                return NotHandled;
            }

            return str.FindAsciiLiteral(_byChar, _byString);
        }

        private static Binding/*!*/ Bind(RubyContext/*!*/ context) {
            // read before the lookup: a change that races with it leaves the binding stale, not wrong
            int version = RubyModule.GlobalMethodVersion;
            var lookup = context.GetClass(typeof(MutableString)).ResolveMethod("===", VisibilityContext.AllVisible);
            return new Binding(context, version, lookup.Found && IsLibraryEquals(lookup.Info));
        }

        // MutableStringOps' ==/=== overloads: two StringEquals (a String, a CLR string) and Equals (anything
        // else, through to_str)
        private static bool IsLibraryEquals(RubyMemberInfo/*!*/ method) {
            var libraryMethod = method as RubyLibraryMethodInfo;
            if (libraryMethod == null) {
                return false;
            }
            foreach (var member in libraryMethod.GetMembers()) {
                if ((member.Name != "StringEquals" && member.Name != "Equals") ||
                    member.DeclaringType.FullName != "IronRuby.Builtins.MutableStringOps") {
                    return false;
                }
            }
            return true;
        }
    }
}
