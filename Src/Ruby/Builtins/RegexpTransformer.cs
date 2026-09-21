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
using System.Text;
using System.Text.RegularExpressions;
using IronRuby.Compiler;
using IronRuby.Runtime;
using System.Collections.Generic;

namespace IronRuby.Builtins {
    /// <summary>
    /// Converts a Ruby regexp pattern to a CLR pattern
    /// </summary>
    internal sealed class RegexpTransformer {
        private readonly string/*!*/ _rubyPattern;
        private int _index;
        private StringBuilder/*!*/ _sb;
        private bool _hasGAnchor;

        // Capturing groups opened so far. Ruby resolves relative references such as \k<-1> and
        // (?(<-1>)...) against this, and reads \N as a backreference only while N is within it.
        private int _groupCount;

        // True when the pattern declares a named group anywhere. Ruby then rejects every numbered
        // backreference in the pattern, including one written before the named group.
        private readonly bool _hasNamedGroup;

        // Nesting depth of groups and of the absent operator's sub-buffer. \K rewrites everything
        // emitted so far as a lookbehind, which is only meaningful at the top level.
        private int _groupDepth;
        private int _absentDepth;

        /// <summary>
        /// Which repertoire \w \d \s and the POSIX bracket classes range over, selected by the
        /// (?a) (?d) (?u) inline modifiers. .NET has no equivalent flag, so the expansions have to
        /// differ instead. Ruby's default is (?d): \w and friends are ASCII only while the POSIX
        /// classes are Unicode aware.
        /// </summary>
        private enum CharacterClassMode {
            Default,
            Ascii,
            Unicode,
        }

        private CharacterClassMode _characterClassMode;

        /// <summary>
        /// A capturing group as a subexpression call or a loop copy needs it: its Ruby body and
        /// the parser state at the start of that body, so that the body can be transformed again
        /// with its nested groups getting the numbers and names they have in the original.
        /// </summary>
        private sealed class GroupDefinition {
            public string/*!*/ Source;
            public int Number;
            public string Name;
            public int Occurrence;
            public Dictionary<string, int> OccurrencesBefore;
            public CharacterClassMode Mode;
        }

        /// <summary>
        /// State shared by a transformer and every sub-transformer it spawns to copy a group.
        /// </summary>
        private sealed class SharedState {
            // Every capturing group, recorded in a prescan so that a call may precede - or sit
            // inside - the group it calls.
            public readonly Dictionary<int, GroupDefinition>/*!*/ ByNumber = new Dictionary<int, GroupDefinition>();
            public readonly Dictionary<string, GroupDefinition>/*!*/ ByName = new Dictionary<string, GroupDefinition>();

            // Keys ("name" or "#number") of the groups whose body is being emitted - parsed in
            // place or copied for a call. A call to one of them is recursion; a backreference to
            // one of them never matches, as in Onigmo.
            public readonly List<string>/*!*/ OpenGroups = new List<string>();

            // Groups some \k<name+level> refers to, and the hidden per-level groups made for them.
            public readonly HashSet<string>/*!*/ LevelReferenced = new HashSet<string>();
            public readonly Dictionary<string, int>/*!*/ LevelGroups = new Dictionary<string, int>();
            public readonly HashSet<int>/*!*/ DefinedLevelGroups = new HashSet<int>();

            public string RootPattern;
            public bool Collecting;
            public int HiddenGroupCount;
            public RegexOptions ClrOptions = RegexOptions.Multiline | RegexOptions.CultureInvariant;
            public int MaxCallDepth = DefaultMaxCallDepth;
            public int Emitted;
        }

        private sealed class ExpansionTooLargeException : Exception {
        }

        // .NET's Regex cannot recurse, so a recursive subexpression call is expanded inline this
        // many levels deep; deeper nesting fails to match. When the expansion grows too large
        // (a group calling itself more than once per level grows exponentially) the depth is
        // reduced until it fits.
        private const int DefaultMaxCallDepth = 50;
        private const int ExpansionBudget = 200000;

        // Hidden groups emitted for level-scoped backreferences are numbered from here, above any
        // group Ruby numbers, so MatchData can leave them out (see IsHiddenGroupNumber).
        internal const int HiddenGroupBase = 100000;

        private SharedState/*!*/ _state;
        private RegexOptions _clrOptions = RegexOptions.Multiline | RegexOptions.CultureInvariant;

        // Set on a sub-transformer that copies a group - for a \g<...> call or to unroll a loop:
        // the copy's groups are emitted under the numbers and names of the groups they copy.
        private bool _copyGroups;

        // How many subexpression calls deep the text being emitted is.
        private int _callDepth;

        // Backreferences and conditionals emitted so far, and empty capturing groups: a loop
        // whose body contains either can behave differently after an empty iteration (see
        // RewriteLoop).
        private int _backrefCount;
        private int _emptyCaptureCount;

        /// <summary>A quantified group, as RewriteLoop needs it.</summary>
        private sealed class LoopBody {
            public string Source;
            public int GroupCountBefore;
            public int GroupCountAfter;
            public int BackrefCountBefore;
            public int EmptyCaptureCountBefore;
            public Dictionary<string, int> OccurrencesBefore;
            public CharacterClassMode Mode;
        }

        // Onigmo's warnings about a quantifier applied to a quantifier (regparse.c set_quantifier).
        private List<string> _warnings;

        // The last quantifier as one of Onigmo's "popular" ones - ? * + ?? *? +? - or null.
        private string _lastQuantifierKind;

        private static readonly string[] PopularQuantifiers = { "?", "*", "+", "??", "*?", "+?" };

        // ReduceTypeTable[inner, outer]: 0 as is, 1 redundant, otherwise the index into ReducedQuantifiers
        private static readonly int[,] ReduceTypeTable = {
            /* '?' '*' '+' '??' '*?' '+?'   outer / inner */
            { 1, 2, 2, 4, 3, 0 },  /* '?'  */
            { 1, 1, 1, 5, 5, 1 },  /* '*'  */
            { 2, 2, 1, 0, 5, 1 },  /* '+'  */
            { 1, 3, 3, 1, 3, 3 },  /* '??' */
            { 1, 1, 1, 1, 1, 1 },  /* '*?' */
            { 0, 6, 1, 3, 3, 1 },  /* '+?' */
        };

        private static readonly string[] ReducedQuantifiers = { "", "", "*", "*?", "??", "+ and ??", "+? and ?" };

        private void CheckNestedQuantifier(string/*!*/ inner, string/*!*/ outer) {
            int i = Array.IndexOf(PopularQuantifiers, inner);
            int o = Array.IndexOf(PopularQuantifiers, outer);
            if (i < 0 || o < 0) {
                return;
            }

            int reduction = ReduceTypeTable[i, o];
            if (reduction == 0) {
                return;
            }

            if (_warnings == null) {
                _warnings = new List<string>();
            }
            _warnings.Add(reduction == 1
                ? "regular expression has redundant nested repeat operator '" + inner + "'"
                : "nested repeat operator '" + inner + "' and '" + outer + "' was replaced with '" + ReducedQuantifiers[reduction] + "' in regular expression"
            );
        }

        /// <summary>
        /// The warnings Onigmo gives while compiling the pattern, without the ": /pattern/" MRI
        /// appends. Null if there are none or the pattern does not compile.
        /// </summary>
        internal static List<string> GetWarnings(string/*!*/ rubyPattern) {
            var transformer = new RegexpTransformer(rubyPattern);
            try {
                transformer.Transform();
            } catch (Exception) {
                return null;
            }
            return transformer._warnings;
        }

        internal static string Transform(string/*!*/ rubyPattern, RubyRegexOptions options, out bool hasGAnchor) {
            // TODO: surrogates (REXML uses this pattern)
            if (rubyPattern == "^[\t\n\r -\uD7FF\uE000-\uFFFD\uD800\uDC00-\uDBFF\uDFFF]*$") {
                hasGAnchor = false;
                return "^(?:[\t\n\r -\uD7FF\uE000-\uFFFD]|[\uD800-\uDBFF][\uDC00-\uDFFF])*$";
            }
            
            RegexpTransformer transformer = new RegexpTransformer(rubyPattern);
            transformer._clrOptions = RubyRegex.ToClrOptions(options);
            var result = transformer.Transform();
            hasGAnchor = transformer._hasGAnchor;
            return result;
        }

        private RegexpTransformer(string/*!*/ rubyPattern) {
            _rubyPattern = rubyPattern;
            _groupNameCounts = CountGroupNames(rubyPattern);
            _hasNamedGroup = _groupNameCounts.Count > 0;
            _state = new SharedState();
        }

        /// <summary>
        /// A transformer for <paramref name="source"/>, the body of a group of the pattern
        /// <paramref name="parent"/> is transforming, that emits the body's groups under the
        /// numbers and names they have in the whole pattern.
        /// </summary>
        private RegexpTransformer(RegexpTransformer/*!*/ parent, string/*!*/ source, int groupCount,
            Dictionary<string, int> occurrences, CharacterClassMode mode, int callDepth) {
            _rubyPattern = source;
            _groupNameCounts = parent._groupNameCounts;
            _hasNamedGroup = parent._hasNamedGroup;
            _state = parent._state;
            _copyGroups = true;
            _groupCount = groupCount;
            if (occurrences != null) {
                _groupNameOccurrences = new Dictionary<string, int>(occurrences);
            }
            _characterClassMode = mode;
            _callDepth = callDepth;
            // a copy is never the top level of the pattern (\K)
            _groupDepth = 1;
        }

        private string/*!*/ TransformCopy(RegexpTransformer/*!*/ copy) {
            string result = copy.Transform();
            _hasGAnchor |= copy._hasGAnchor;
            _backrefCount += copy._backrefCount;
            _state.Emitted += result.Length;
            if (_state.Emitted > ExpansionBudget) {
                throw new ExpansionTooLargeException();
            }
            return result;
        }

        internal static bool IsHiddenGroupNumber(int number) {
            return number >= HiddenGroupBase;
        }

        private static string/*!*/ GroupKey(int number, string name) {
            return name ?? "#" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void OpenGroup(string/*!*/ key) {
            _state.OpenGroups.Add(key);
        }

        private void CloseGroup(string/*!*/ key) {
            _state.OpenGroups.RemoveAt(_state.OpenGroups.LastIndexOf(key));
        }

        private bool IsOpenGroup(string/*!*/ key) {
            return _state.OpenGroups.Contains(key);
        }

        /// <summary>
        /// The number of the hidden group that records what group <paramref name="key"/> captures
        /// at subexpression call level <paramref name="level"/>.
        /// </summary>
        private int GetLevelGroup(string/*!*/ key, int level) {
            string id = key + "@" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int number;
            if (!_state.LevelGroups.TryGetValue(id, out number)) {
                number = AllocateHiddenGroup();
                _state.LevelGroups.Add(id, number);
            }
            return number;
        }

        private int AllocateHiddenGroup() {
            return HiddenGroupBase + _state.HiddenGroupCount++;
        }

        /// <summary>
        /// Opens the hidden per-level group of a capturing group when a level-scoped
        /// backreference refers to it. Returns whether one was opened.
        /// </summary>
        private bool OpenLevelGroup(string/*!*/ key) {
            if (_state.Collecting || !_state.LevelReferenced.Contains(key)) {
                return false;
            }
            int number = GetLevelGroup(key, _callDepth);
            _state.DefinedLevelGroups.Add(number);
            _sb.Append("(?<").Append(number).Append('>');
            return true;
        }

        // How many groups the pattern declares under each name. Ruby lets several groups share a
        // name and numbers them all; .NET would merge them into one group, so every occurrence
        // after the first is given a name of its own (see DuplicateGroupName).
        private readonly Dictionary<string, int>/*!*/ _groupNameCounts;
        private readonly Dictionary<string, int>/*!*/ _groupNameOccurrences = new Dictionary<string, int>();

        private const string DuplicateGroupNameSeparator = "__ir";

        // A name .NET would not accept - Ruby allows `(?<a+>...)' for a group only ever called
        // with \g - is spelled with its other characters as hex codes behind this prefix.
        private const string EncodedGroupNamePrefix = "__irn";

        private static string/*!*/ DuplicateGroupName(string/*!*/ name, int occurrence) {
            string clrName = IsClrGroupName(name) ? name : EncodeGroupName(name);
            return occurrence <= 1 ? clrName : clrName + DuplicateGroupNameSeparator + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool IsClrGroupName(string/*!*/ name) {
            if (name.Length == 0 || !(Char.IsLetter(name[0]) || name[0] == '_')) {
                return false;
            }
            foreach (char c in name) {
                if (!Char.IsLetterOrDigit(c) && c != '_') {
                    return false;
                }
            }
            return true;
        }

        private static string/*!*/ EncodeGroupName(string/*!*/ name) {
            var result = new StringBuilder(EncodedGroupNamePrefix);
            foreach (char c in name) {
                if (Char.IsLetterOrDigit(c)) {
                    result.Append(c);
                } else {
                    result.Append('_').Append(((int)c).ToString("x4"));
                }
            }
            return result.ToString();
        }

        private static string/*!*/ DecodeGroupName(string/*!*/ clrName) {
            if (!clrName.StartsWith(EncodedGroupNamePrefix, StringComparison.Ordinal)) {
                return clrName;
            }
            var result = new StringBuilder();
            for (int i = EncodedGroupNamePrefix.Length; i < clrName.Length; i++) {
                if (clrName[i] == '_' && i + 4 < clrName.Length) {
                    result.Append((char)Convert.ToInt32(clrName.Substring(i + 1, 4), 16));
                    i += 4;
                } else {
                    result.Append(clrName[i]);
                }
            }
            return result.ToString();
        }

        /// <summary>
        /// The Ruby name of a .NET group: the name the pattern gave it, whichever occurrence of
        /// the name it is. Null for a group .NET only knows by number.
        /// </summary>
        internal static string GetRubyGroupName(string/*!*/ clrName) {
            if (clrName.Length == 0 || Char.IsDigit(clrName[0])) {
                return null;
            }
            int separator = clrName.LastIndexOf(DuplicateGroupNameSeparator, StringComparison.Ordinal);
            if (separator > 0 && separator + DuplicateGroupNameSeparator.Length < clrName.Length) {
                for (int i = separator + DuplicateGroupNameSeparator.Length; i < clrName.Length; i++) {
                    if (!Char.IsDigit(clrName[i])) {
                        return clrName;
                    }
                }
                return DecodeGroupName(clrName.Substring(0, separator));
            }
            return DecodeGroupName(clrName);
        }

        private static Dictionary<string, int>/*!*/ CountGroupNames(string/*!*/ pattern) {
            var result = new Dictionary<string, int>();
            bool inCharacterClass = false;
            for (int i = 0; i < pattern.Length; i++) {
                char c = pattern[i];
                if (c == '\\') {
                    i++;
                } else if (inCharacterClass) {
                    inCharacterClass = c != ']';
                } else if (c == '[') {
                    inCharacterClass = true;
                } else if (c == '(' && i + 2 < pattern.Length && pattern[i + 1] == '?') {
                    char d = pattern[i + 2];
                    char terminator;
                    if (d == '\'') {
                        terminator = '\'';
                    } else if (d == '<' && i + 3 < pattern.Length && pattern[i + 3] != '=' && pattern[i + 3] != '!') {
                        terminator = '>';
                    } else {
                        continue;
                    }
                    int end = pattern.IndexOf(terminator, i + 3);
                    if (end > i + 3) {
                        string name = pattern.Substring(i + 3, end - i - 3);
                        int count;
                        result.TryGetValue(name, out count);
                        result[name] = count + 1;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Whether the pattern opens a named group anywhere. Has to be known up front: Ruby
        /// rejects (a)(?&lt;a&gt;a)\1 as well as (?&lt;a&gt;a)\1, so the decision cannot wait
        /// until the named group is reached.
        /// </summary>
        private static bool HasNamedGroup(string/*!*/ pattern) {
            bool inCharacterClass = false;
            for (int i = 0; i < pattern.Length; i++) {
                char c = pattern[i];
                if (c == '\\') {
                    i++;
                } else if (inCharacterClass) {
                    inCharacterClass = c != ']';
                } else if (c == '[') {
                    inCharacterClass = true;
                } else if (c == '(' && i + 2 < pattern.Length && pattern[i + 1] == '?') {
                    char d = pattern[i + 2];
                    if (d == '\'') {
                        return true;
                    }
                    // (?<= and (?<! are lookbehind, not a named group
                    if (d == '<' && i + 3 < pattern.Length && pattern[i + 3] != '=' && pattern[i + 3] != '!') {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// A group name that carries a level specifier - anything with a '+' or a '-' in it - can
        /// be declared but not referenced; Ruby raises when one is used in \k&lt;&gt; or in a
        /// conditional. .NET rejects such names outright, so this only has to produce the error.
        /// </summary>
        private static bool IsLevelSpecifier(string/*!*/ name) {
            return name.IndexOf('+') >= 0 || name.IndexOf('-') >= 0;
        }

        /// <summary>
        /// Parses <paramref name="text"/> as Ruby writes a group number in a reference: an
        /// optional '-' for a relative index, then decimal digits which may have leading zeros.
        /// Returns the absolute group number, or -1 when the text is not a number at all.
        /// </summary>
        private int ResolveGroupNumber(string/*!*/ text) {
            if (text.Length == 0) {
                return -1;
            }
            bool relative = text[0] == '-';
            int i = relative ? 1 : 0;
            if (i == text.Length) {
                return -1;
            }

            int value = 0;
            for (; i < text.Length; i++) {
                if (!Tokenizer.IsDecimalDigit(text[i])) {
                    return -1;
                }
                value = value * 10 + (text[i] - '0');
            }

            if (relative) {
                value = _groupCount + 1 - value;
            }
            if (value <= 0) {
                throw MakeError("invalid group name <" + text + ">");
            }
            return value;
        }

        #region Buffer Ops

        private int Peek() {
            return _index < _rubyPattern.Length ? _rubyPattern[_index] : -1;
        }

        private int Peek(int disp) {
            int i = _index + disp;
            return i < _rubyPattern.Length ? _rubyPattern[i] : -1;
        }

        private int Read() {
            return _index < _rubyPattern.Length ? _rubyPattern[_index++] : -1;
        }

        private void Back() {
            _index--;
            Debug.Assert(_index >= 0);
        }
        
        private void Skip(int n) {
            _index += n;
            Debug.Assert(_index <= _rubyPattern.Length);
        }

        private void Skip() {
            Skip(1);
        }

        private void Skip(char c) {
            Debug.Assert(Peek() == c);
            Skip();
        }

        private bool Read(int c) {
            if (Peek() == c) {
                Skip();
                return true;
            } else {
                return false;
            }
        }

        private void Append(char c) {
            _sb.Append(c);
        }

        private void AppendEscaped(int c) {
            AppendEscaped(_sb, c);
        }

        private static StringBuilder/*!*/ AppendEscaped(StringBuilder/*!*/ builder, string/*!*/ str) {
            for (int i = 0; i < str.Length; i++) {
                AppendEscaped(builder, str[i]);
            }
            return builder;
        }

        private static StringBuilder/*!*/ AppendEscaped(StringBuilder/*!*/ builder, int c) {
            if (IsMetaCharacter(c)) {
                builder.Append('\\');
            }
            builder.Append((char)c);
            return builder;
        }

        private static string/*!*/ Escape(int c) {
            return IsMetaCharacter(c) ? "\\" + (char)c : ((char)c).ToString();
        }

        private static bool IsMetaCharacter(int c) {
            switch (c) {
                case '$':
                case '^':
                case '|':
                case '[':
                case ']':
                case '(':
                case ')':
                case '\\':
                case '.':
                case '#':
                case '-':
                case '{':
                case '}':
                case '*':
                case '+':
                case '?':
                case '\n':
                case '\r':
                case '\t':
                case ' ':
                    return true;
            }

            return false;
        }

        #endregion

        private Exception/*!*/ MakeError(string/*!*/ message) {
            // MRI quotes the offending pattern as a regexp literal: "invalid hex escape: /\xn/".
            return new RegexpError(message + ": /" + _rubyPattern + "/");
        }

        private string/*!*/ Transform() {
            if (_copyGroups) {
                return TransformBody();
            }

            // A call may precede the group it calls, and a level-scoped backreference decides how
            // the group it refers to is emitted, so both need the whole pattern scanned first.
            bool prescan = _rubyPattern.IndexOf("\\g", StringComparison.Ordinal) >= 0
                || _rubyPattern.IndexOf("\\k", StringComparison.Ordinal) >= 0;

            for (int depth = DefaultMaxCallDepth; ; depth /= 2) {
                _state = new SharedState();
                _state.MaxCallDepth = depth;
                _state.ClrOptions = _clrOptions;
                _state.RootPattern = _rubyPattern;
                if (prescan) {
                    var collector = new RegexpTransformer(_rubyPattern);
                    collector._state = _state;
                    _state.Collecting = true;
                    collector.TransformBody();
                    _state.Collecting = false;
                    _state.OpenGroups.Clear();
                }

                _index = 0;
                _groupCount = 0;
                _groupDepth = 0;
                _absentDepth = 0;
                _hasGAnchor = false;
                _warnings = null;
                _backrefCount = 0;
                _characterClassMode = CharacterClassMode.Default;
                _groupNameOccurrences.Clear();
                try {
                    string result = TransformBody();
                    // A level-scoped backreference may name a level no group is ever emitted at;
                    // it never matches, but .NET requires the group to exist.
                    StringBuilder undefined = null;
                    foreach (int number in _state.LevelGroups.Values) {
                        if (!_state.DefinedLevelGroups.Contains(number)) {
                            if (undefined == null) {
                                undefined = new StringBuilder(result).Append("(?:(?!)");
                            }
                            undefined.Append("(?<").Append(number).Append(">)");
                        }
                    }
                    return undefined != null ? undefined.Append(")?").ToString() : result;
                } catch (ExpansionTooLargeException) {
                    if (depth <= 1) {
                        throw MakeError("too big regular expression");
                    }
                }
            }
        }

        private string/*!*/ TransformBody() {
            _sb = new StringBuilder(_rubyPattern.Length);
            Parse(false);
            var result = _sb.ToString();
            _sb = null;
            return result;
        }

        private void Parse(bool isSubexpression) {
            int lastEntityIndex = 0;
            // The group the next quantifier would apply to, when its loop needs RewriteLoop.
            LoopBody lastEntity = null;
            // Ruby allows a quantifier to be quantified again (a***, a+?*); .NET rejects that as a
            // nested quantifier, so the whole quantified entity has to be wrapped first.
            bool lastWasQuantifier = false;
            int c;
            while (true) {
                switch (c = Read()) {
                    case -1:
                        if (isSubexpression) {
                            throw MakeError("end pattern in group");
                        }
                        return;
                    
                    case '\\':
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        ParseEscape();
                        break;

                    case '?':
                    case '*':
                    case '+': {
                        string inner = lastWasQuantifier ? _lastQuantifierKind : null;
                        if (c != '?' && !lastWasQuantifier && Peek() != '+' &&
                            RewriteLoop(lastEntity, lastEntityIndex, c == '+' ? 1 : 0, -1, Peek() == '?')) {
                            _lastQuantifierKind = ((char)c).ToString() + (Peek() == '?' ? "?" : "");
                            Read('?');
                            lastWasQuantifier = true;
                            break;
                        }
                        if (lastWasQuantifier) {
                            // a*** == (?:(?:a*)*)*
                            _sb.Insert(lastEntityIndex, "(?:");
                            Append(')');
                        }
                        Append((char)c);
                        int next = Peek();
                        // `?+ *+ ++` are possessive, not a quantifier applied to a quantifier
                        string kind = (next == '+') ? null : ((char)c).ToString() + (next == '?' ? "?" : "");
                        if (inner != null && kind != null) {
                            CheckNestedQuantifier(inner, kind);
                        }
                        _lastQuantifierKind = kind;
                        // A quantifier that got wrapped is already a group, so a further
                        // quantifier can apply to it directly.
                        lastWasQuantifier = !ParsePostQuantifier(lastEntityIndex, true, false);
                        break;
                    }

                    case '{': {
                        bool isExactCount;
                        string inner = lastWasQuantifier ? _lastQuantifierKind : null;
                        int start = _index;
                        int min, max, end;
                        if (!lastWasQuantifier && lastEntity != null && TryReadInterval(out min, out max, out end) &&
                            (end == _rubyPattern.Length || _rubyPattern[end] != '+') &&
                            // {n}? is a quantified quantifier, not a lazy one
                            (max != min || end == _rubyPattern.Length || _rubyPattern[end] != '?') &&
                            RewriteLoop(lastEntity, lastEntityIndex, min, max, end < _rubyPattern.Length && _rubyPattern[end] == '?')) {
                            _index = end;
                            if (Peek() == '?') {
                                Skip();
                            }
                            _lastQuantifierKind = null;
                            lastWasQuantifier = true;
                            break;
                        }
                        if (ParseConstrainedQuantifier(lastWasQuantifier, lastEntityIndex, out isExactCount)) {
                            // {0,1} is Onigmo's ?, the one interval that counts as a popular quantifier here
                            int next = Peek();
                            string kind = (_rubyPattern.Substring(start, _index - start) == "0,1}")
                                ? (next == '?' ? "??" : "?")
                                : null;
                            if (inner != null && kind != null) {
                                CheckNestedQuantifier(inner, kind);
                            }
                            if (kind != null && next == '+') {
                                // {n,m}+ is not possessive in Ruby: the + quantifies again
                                CheckNestedQuantifier(kind, "+");
                                kind = null;
                            }
                            _lastQuantifierKind = kind;
                            lastWasQuantifier = !ParsePostQuantifier(lastEntityIndex, false, isExactCount);
                        } else {
                            goto default;
                        }
                        break;
                    }

                    case '(': {
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = new LoopBody {
                            GroupCountBefore = _groupCount,
                            BackrefCountBefore = _backrefCount,
                            EmptyCaptureCountBefore = _emptyCaptureCount,
                            OccurrencesBefore = _hasNamedGroup ? new Dictionary<string, int>(_groupNameOccurrences) : null,
                            Mode = _characterClassMode,
                        };
                        int entityStart = _index - 1;
                        ParseGroup();
                        lastEntity.Source = _rubyPattern.Substring(entityStart, _index - entityStart);
                        lastEntity.GroupCountAfter = _groupCount;
                        if (lastEntity.GroupCountBefore == _groupCount ||
                            (lastEntity.BackrefCountBefore == _backrefCount && lastEntity.EmptyCaptureCountBefore == _emptyCaptureCount)) {
                            // no capture that an empty iteration could set, or nothing that could
                            // observe it: .NET's loop gives the same result
                            lastEntity = null;
                        }
                        break;
                    }

                    case ')':
                        if (isSubexpression) {
                            return;
                        } else {
                            throw MakeError("unmatched close parenthesis");
                        }
                    
                    case '[':
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        ParseCharacterGroup(false).AppendTo(_sb, true);
                        break;

                    case '|':
                        Append('|');
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        break;

                    case '^':
                        // RegexOptions.Multiline is always on, and .NET's ^ then also matches the
                        // empty line it considers a trailing \n to open. Ruby has no such line:
                        // ^ matches at the start of the string and after a \n that is not the last
                        // character. "a\n\nb".scan(/^/).size is 3 in Ruby, 4 in .NET.
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        _sb.Append("(?:\\A|(?<=\\n)(?!\\z))");
                        break;

                    default:
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        lastEntity = null;
                        Append((char)c);
                        break;
                }
            }
        }

        /// <summary>
        /// Reads the interval of a {n,m} {n,} {,m} or {n} quantifier whose '{' has just been read,
        /// without consuming it. <paramref name="max"/> is -1 when unbounded;
        /// <paramref name="end"/> is the index just past the '}'.
        /// </summary>
        private bool TryReadInterval(out int min, out int max, out int end) {
            min = max = end = -1;
            int i = _index;
            int n = -1, m = -1;
            bool comma = false;
            while (true) {
                if (i >= _rubyPattern.Length) {
                    return false;
                }
                char c = _rubyPattern[i++];
                if (c == '}') {
                    break;
                } else if (c == ',' && !comma) {
                    comma = true;
                } else if (Tokenizer.IsDecimalDigit(c)) {
                    int value = comma ? m : n;
                    value = (value < 0 ? 0 : value) * 10 + (c - '0');
                    if (value > 100000) {
                        return false;
                    }
                    if (comma) { m = value; } else { n = value; }
                } else {
                    return false;
                }
            }
            if (n < 0 && m < 0) {
                return false;
            }
            min = n < 0 ? 0 : n;
            max = comma ? m : min;
            end = i;
            return max < 0 || max >= min;
        }

        /// <summary>
        /// Onigmo ends a loop after an iteration that matched the empty string only if the
        /// iteration left the captures as they were; .NET ends it after any empty iteration past
        /// the minimum count. It matters when a later iteration can observe the capture - through
        /// a backreference, as in (a|\2b|())* on "aaabbb", or through the capture's final value.
        /// Such a loop of a group X is rewritten so that no .NET loop has to go on past an empty
        /// iteration: iterations are grouped into units of up to K+1, X' being a copy of X whose
        /// groups keep their numbers, and only a unit that is empty as a whole ends the loop.
        /// A unit only continues while it is still empty (E: the input left is the same as at
        /// the unit start S), so each run of iterations divides into units in exactly one way.
        ///
        ///   X*      (?:S X(?:E X'(?:E X')?)?)*       (K = 2; lazily (?:S X(?:|E X'(?:|E X')))*?)
        ///   X{n,}   X{n}(?:S X'(?:E X'(?:E X')?)?)*
        ///   X{n,m}  X{n}(?:X'(?:X')?)?         (the optional iterations nested: no loop at all)
        ///
        /// K bounds how many empty iterations in a row are possible; each of them has to set
        /// another group, so it is one less than the groups in X, at most 3.
        /// Only loops of a group that can match the empty string and has a backreference or an
        /// empty capture () in its body are rewritten - the units cost more: Onigmo itself only
        /// checks the captures after an empty iteration when a backreference could observe them.
        /// </summary>
        private bool RewriteLoop(LoopBody entity, int entityIndex, int min, int max, bool lazy) {
            if (entity == null || _state.Collecting || _absentDepth != 0) {
                return false;
            }
            Debug.Assert(entity.Source != null);
            if (!CanMatchEmpty(_sb.ToString(entityIndex, _sb.Length - entityIndex))) {
                return false;
            }

            // An optional part: (?:...)? or, lazily, (?:|...) - .NET overflows its backtracking
            // stack on (?:...)?? nested in a lazy loop.
            string open = lazy ? "(?:|" : "(?:";
            string close = lazy ? ")" : ")?";
            if (max < 0) {
                int units = Math.Max(1, Math.Min(entity.GroupCountAfter - entity.GroupCountBefore - 1, 3));
                string copy = CopyLoopBody(entity);
                // A unit goes on only while it is still empty, so that a run of iterations can
                // be cut into units in just one way: otherwise a failing match would try them all.
                // The unit start is remembered as the rest of the input, in a hidden group.
                int rest = AllocateHiddenGroup();
                string start = "(?=(?<" + rest + ">[\\s\\S]*))";
                string stillEmpty = "(?=\\k<" + rest + ">\\z)";
                string more = "";
                for (int i = 0; i < units; i++) {
                    more = open + stillEmpty + copy + more + close;
                }
                if (min == 0) {
                    _sb.Insert(entityIndex, "(?:" + start);
                    _sb.Append(more).Append(")*");
                } else {
                    if (min > 1) {
                        _sb.Append('{').Append(min).Append('}');
                    }
                    _sb.Append("(?:").Append(start).Append(copy).Append(more).Append(")*");
                }
                if (lazy) {
                    _sb.Append('?');
                }
                return true;
            }

            int optional = max - min;
            if (optional <= 0) {
                return false;
            }
            string body = CopyLoopBody(entity);
            if ((long)body.Length * optional > ExpansionBudget / 10) {
                return false;
            }
            if (min == 0) {
                _sb.Insert(entityIndex, open);
                optional--;
            } else if (min > 1) {
                _sb.Append('{').Append(min).Append('}');
            }
            for (int i = 0; i < optional; i++) {
                _sb.Append(open).Append(body);
            }
            for (int i = 0; i < optional; i++) {
                _sb.Append(close);
            }
            if (min == 0) {
                _sb.Append(close);
            }
            return true;
        }

        /// <summary>
        /// Whether the translated group can match the empty string - when unsure, true. A
        /// backreference to a group outside of it makes .NET reject the pattern alone.
        /// </summary>
        private bool CanMatchEmpty(string/*!*/ clrPattern) {
            try {
                return new Regex("\\A(?:" + clrPattern + ")\\z", _state.ClrOptions, TimeSpan.FromSeconds(1)).IsMatch(String.Empty);
            } catch (ArgumentException) {
                return true;
            } catch (RegexMatchTimeoutException) {
                return true;
            }
        }

        private string/*!*/ CopyLoopBody(LoopBody/*!*/ entity) {
            return TransformCopy(new RegexpTransformer(this, entity.Source, entity.GroupCountBefore, entity.OccurrencesBefore, entity.Mode, _callDepth));
        }

        // {n,m}
        // {n,}
        // {,m}
        // {n}
        private bool ParseConstrainedQuantifier(bool lastWasQuantifier, int lastEntityIndex, out bool isExactCount) {
            Debug.Assert(_rubyPattern[_index - 1] == '{');
            isExactCount = false;

            int c;
            int m = -1;
            bool hasDigits = false;
            bool hasDigitsAfterComma = false;

            int i = 0;
            while (true) {
                c = Peek(i++);
                if (c == ',') {
                    if (m != -1) {
                        // not a quantifier
                        return false;
                    }
                    m = i;                    
                } else if (c == '}') {
                    break;
                } else if (!Tokenizer.IsDecimalDigit(c)) {
                    return false;
                } else {
                    hasDigits = true;
                    if (m != -1) {
                        hasDigitsAfterComma = true;
                    }
                }
            }

            // {} and {,} carry no count: Ruby and .NET both read them as literal text, so they go
            // through untouched and are not treated as a quantifier for the purposes below.
            if (hasDigits) {
                if (lastWasQuantifier) {
                    // a*{2} == (?:a*){2}
                    _sb.Insert(lastEntityIndex, "(?:");
                    Append(')');
                }
                if (m == 1 && hasDigitsAfterComma) {
                    // Ruby's {,m} means {0,m}; .NET has no such form and reads it as literal text.
                    _sb.Append("{0");
                    _sb.Append(_rubyPattern, _index, i);
                } else {
                    _sb.Append(_rubyPattern, _index - 1, i + 1);
                    isExactCount = m == -1;
                }
            } else {
                _sb.Append(_rubyPattern, _index - 1, i + 1);
            }
            _index += i;
            return true;
        }

        /// <summary>Returns true when the quantified entity was wrapped in a group.</summary>
        private bool ParsePostQuantifier(int lastEntityIndex, bool possessive, bool questionIsQuantifier) {
            int c = Peek();

            if (c == '+') {
                // nested possessive quantifiers not directly supported by Regex:
                Skip();
                _sb.Insert(lastEntityIndex, possessive ? "(?>" : "(?:");
                Append(')');
                if (!possessive) {
                    Append('+');
                }
                return true;
            } else if (c == '?') {
                Skip();
                if (questionIsQuantifier) {
                    // After an exact count Ruby reads ? as another quantifier: a{1}? == (?:a{1})?.
                    // After a range it is the ordinary laziness marker: a{1,2}? matches one 'a'.
                    _sb.Insert(lastEntityIndex, "(?:");
                    _sb.Append(")?");
                    return true;
                }
                Append('?');
            }
            return false;
        }

        //
        // (?#...)            comment
        // (?imx-imx)         option on/off
        // (?imx-imx:subexp)  option on/off for subexp
        // (?:subexp)         not captured group
        // (?=subexp)         look-ahead
        // (?!subexp)         negative look-ahead
        // (?<=subexp)        look-behind
        // (?<!subexp)        negative look-behind
        // (?>subexp)         atomic group
        //                    don't backtrack in subexp.
        // 
        // (?<name>subexp)
        // (?'name'subexp)
        //
        // (subexp)           captured group
        //
        private void ParseGroup() {
            Debug.Assert(_rubyPattern[_index - 1] == '(');
            int groupNumber = -1;
            string groupName = null;
            if (Read('?')) {
                int c = Read();
                if (c == '#') {
                    while (true) {
                        c = Read();
                        if (c == -1) {
                            throw MakeError("end pattern in group");
                        }
                        if (c == ')') {
                            break;
                        }
                    }
                    return;
                }

                if (c == '~') {
                    ParseAbsentExpression();
                    return;
                }

                if (c == '-' || c == 'i' || c == 'm' || c == 'x' || c == 'a' || c == 'd' || c == 'u') {
                    ParseGroupOptions(c);
                    return;
                }

                Append('(');
                Append('?');

                switch (c) {
                    case ':':
                        // non-captured group
                        Append(':');
                        break;

                    case '=':
                        // positive lookahead
                        Append('=');
                        break;

                    case '!':
                        // negative lookahead
                        Append('!');
                        break;

                    case '>':
                        // greedy subexpression
                        Append('>');
                        break;

                    case '<':
                        c = Read();
                        if (c == '=' || c == '!') {
                            // positive/negative lookbehind assertion
                            Append('<');
                            Append((char)c);
                        } else {
                            _groupCount++;
                            groupNumber = _groupCount;
                            groupName = ParseGroupName(c, '>', '<');
                        }
                        break;

                    case '\'':
                        _groupCount++;
                        groupNumber = _groupCount;
                        groupName = ParseGroupName(Read(), '\'', '\'');
                        break;

                    case '(': {
                        // conditional group: (?(1)yes|no), (?(<name>)...) or (?('name')...).
                        // .NET has the same construct but spells the named form without the
                        // brackets Onigmo requires around the name.
                        Append('(');
                        int delimiter = Read();
                        int closing = (delimiter == '<') ? '>' : (delimiter == '\'') ? '\'' : -1;
                        if (closing == -1) {
                            Back();
                        }

                        var condition = new StringBuilder();
                        while (true) {
                            c = Read();
                            if (c == -1) {
                                throw MakeError("end pattern in group");
                            }
                            if (c == (closing == -1 ? ')' : closing)) {
                                if (closing != -1 && Read() != ')') {
                                    throw MakeError("invalid conditional pattern");
                                }
                                break;
                            }
                            condition.Append((char)c);
                        }

                        string name = condition.ToString();
                        _backrefCount++;
                        int number = ResolveGroupNumber(name);
                        if (number >= 0) {
                            // (?(01)...), (?(<-1>)...) - .NET only understands a plain number
                            _sb.Append(number);
                        } else if (closing == -1) {
                            // Ruby requires <> or '' around a name; .NET would accept a bare one
                            throw MakeError("invalid conditional pattern");
                        } else if (IsLevelSpecifier(name)) {
                            throw MakeError("invalid group name <" + name + ">");
                        } else {
                            _sb.Append(name);
                        }
                        Append(')');
                        break;
                    }

                    default:
                        throw MakeError("undefined group option");
                }
            } else if (_hasNamedGroup) {
                // with a named group anywhere in the pattern, Ruby's plain groups do not capture
                _sb.Append("(?:");
            } else {
                _groupCount++;
                groupNumber = _groupCount;
                if (_copyGroups) {
                    // a copy's group is the same group as the one it copies
                    _sb.Append("(?<").Append(groupNumber).Append('>');
                } else {
                    _sb.Append('(');
                }
            }
            var savedMode = _characterClassMode;
            int bodyStart = _index;
            GroupDefinition definition = null;
            bool hasLevelGroup = false;
            if (groupNumber >= 0) {
                OpenGroup("#" + groupNumber);
                if (groupName != null) {
                    OpenGroup(groupName);
                }
                hasLevelGroup = OpenLevelGroup(GroupKey(groupNumber, groupName));
                if (!_copyGroups) {
                    int occurrence = 0;
                    if (groupName != null) {
                        _groupNameOccurrences.TryGetValue(groupName, out occurrence);
                    }
                    definition = new GroupDefinition {
                        Number = groupNumber,
                        Name = groupName,
                        Occurrence = occurrence,
                        OccurrencesBefore = _hasNamedGroup ? new Dictionary<string, int>(_groupNameOccurrences) : null,
                        Mode = _characterClassMode,
                    };
                }
            }
            _groupDepth++;
            Parse(true);
            _groupDepth--;
            _characterClassMode = savedMode;
            if (groupNumber >= 0) {
                CloseGroup("#" + groupNumber);
                if (groupName != null) {
                    CloseGroup(groupName);
                }
                if (hasLevelGroup) {
                    Append(')');
                }
                string body = _rubyPattern.Substring(bodyStart, _index - 1 - bodyStart);
                if (body.Length == 0) {
                    // () - a capture that is only ever set without consuming anything (see RewriteLoop)
                    _emptyCaptureCount++;
                }
                if (definition != null) {
                    // _index is now just past the ')' Parse consumed
                    definition.Source = body;
                    _state.ByNumber[groupNumber] = definition;
                    if (groupName != null && !_state.ByName.ContainsKey(groupName)) {
                        _state.ByName[groupName] = definition;
                    }
                }
            }
            Append(')');
        }

        //
        // (?imxadu-imx)          option on/off for the rest of the enclosing group
        // (?imxadu-imx:subexp)   option on/off for subexp
        //
        // a, d and u select the repertoire of \w, \d, \s and the POSIX bracket classes. .NET has
        // no such flag, so they are not emitted; they change how those classes are expanded.
        //
        private void ParseGroupOptions(int c) {
            var flags = new StringBuilder();
            var mode = _characterClassMode;
            bool isScoped = false;

            while (true) {
                if (c == 'm') {
                    // Map (?m) to (?s) ie. RegexOptions.SingleLine
                    flags.Append('s');
                } else if (c == 'i' || c == 'x' || c == '-') {
                    flags.Append((char)c);
                } else if (c == 'a') {
                    mode = CharacterClassMode.Ascii;
                } else if (c == 'd') {
                    mode = CharacterClassMode.Default;
                } else if (c == 'u') {
                    mode = CharacterClassMode.Unicode;
                } else if (c == ':') {
                    isScoped = true;
                    break;
                } else if (c == ')') {
                    break;
                } else if (c == -1) {
                    throw MakeError("end pattern in group");
                } else {
                    throw MakeError("undefined group option");
                }
                c = Read();
            }

            string options = flags.ToString();
            if (options == "-") {
                // a lone '-' turns nothing off and .NET rejects it
                options = "";
            }

            if (!isScoped) {
                _characterClassMode = mode;
                if (options.Length != 0) {
                    _sb.Append("(?").Append(options).Append(')');
                }
                return;
            }

            _sb.Append("(?").Append(options).Append(':');
            var savedMode = _characterClassMode;
            _characterClassMode = mode;
            _groupDepth++;
            Parse(true);
            _groupDepth--;
            _characterClassMode = savedMode;
            _sb.Append(')');
        }

        /// <summary>
        /// (?~E) matches the longest string that contains no match of E.
        ///
        /// .NET has no absent operator but does support variable-length lookbehind, so
        /// "consume one character provided we have not just completed an E" renders it.
        /// (?:(?!E)[\s\S])* is NOT equivalent: the lookahead sees past the intended match end
        /// and stops too early ("xfooy" would give "x" where Onigmo gives "xfo").
        ///
        /// The lookbehind alone is still not enough, because it also sees occurrences of E that
        /// began before the match started - "foo".scan(/(?~foo)/) would give ["fo", "", ""] where
        /// Onigmo gives ["fo", "o", ""]. An E of fixed length L can only be completed inside the
        /// match once L-1 characters have been consumed, so the first L-1 characters are consumed
        /// unconditionally. Both parts are greedy and consuming in the unconstrained part is never
        /// worse, so the result is still the longest match.
        ///
        /// When E has no computable fixed length the plain lookbehind form is emitted; it is exact
        /// for a match at the start of the input and conservative elsewhere.
        /// </summary>
        private void ParseAbsentExpression() {
            var outer = _sb;
            _sb = new StringBuilder();
            _absentDepth++;
            Parse(true);
            _absentDepth--;
            string absent = _sb.ToString();
            _sb = outer;

            if (absent.Length == 0) {
                // (?~) can never complete, so it matches everything remaining.
                _sb.Append("[\\s\\S]*");
                return;
            }

            int fixedLength = GetLiteralLength(absent);
            _sb.Append("(?:");
            if (fixedLength > 1) {
                _sb.Append("[\\s\\S]{0,").Append(fixedLength - 1).Append('}');
            }
            _sb.Append("(?:[\\s\\S](?<!").Append(absent).Append("))*)");
        }

        /// <summary>
        /// The number of characters a translated pattern matches when it is a plain literal
        /// sequence, or -1 when it contains anything whose length is not fixed and obvious.
        /// </summary>
        private static int GetLiteralLength(string/*!*/ pattern) {
            int length = 0;
            for (int i = 0; i < pattern.Length; i++) {
                char c = pattern[i];
                if (c == '\\') {
                    if (i + 1 == pattern.Length || !IsMetaCharacter(pattern[i + 1])) {
                        return -1;
                    }
                    i++;
                } else if (IsMetaCharacter(c)) {
                    return -1;
                }
                length++;
            }
            return length;
        }

        /// <summary>
        /// Reads a group name and emits the .NET spelling of the declaration. Returns the name.
        /// </summary>
        private string/*!*/ ParseGroupName(int c, int terminator, int opening) {
            if (c == terminator || c == -1) {
                throw MakeError("group name is empty");
            }
            var name = new StringBuilder();
            int closing;
            while (true) {
                name.Append((char)c);
                c = Read();
                if (c == terminator || c == ')') {
                    closing = c;
                    break;
                } else if (c == -1) {
                    throw MakeError("unterminated group name");
                }
            }

            if (name[0] == '-' || Tokenizer.IsDecimalDigit(name[0])) {
                throw MakeError("invalid group name <" + name + ">");
            }

            string text = name.ToString();
            int occurrence;
            _groupNameOccurrences.TryGetValue(text, out occurrence);
            _groupNameOccurrences[text] = ++occurrence;

            Append((char)opening);
            _sb.Append(DuplicateGroupName(text, occurrence));
            Append((char)closing);
            return text;
        }

        #region Escapes

        private void ParseEscape() {
            int c = Read();
            if (c == -1) {
                throw MakeError("too short escape sequence");
            }
            ParseEscape(c);
        }
        
        // escape outside of character group
        private void ParseEscape(int escape) {
            switch (escape) {
                case 'A':   // beginning a string
                case 'b':   // word boundary
                case 'B':   // not a word boundary
                case 'Z':   // end of string or a new line
                case 'z':   // end of string
                    Append('\\');
                    Append((char)escape);
                    break;

                // \g<n>
                // \g'n'
                // \g<-n>         
                // \g'-n'
                // \g<name>
                // \g'name'
                case 'g':
                    ParseSubexpressionCall();
                    break;
                
                case 'k':
                    ParseBackreference();
                    break;

                case 'X':
                    // An extended grapheme cluster, which .NET has no escape for. This covers what
                    // text commonly holds (UAX #29 without the Hangul and prepend rules): CRLF, a
                    // regional indicator pair (a flag), and a code point followed by combining
                    // marks, variation selectors, emoji modifiers and tags, or joined to further
                    // code points by ZWJ. Atomic, like Onigmo's.
                    _sb.Append(
                        "(?>\\r\\n|\\uD83C[\\uDDE6-\\uDDFF]\\uD83C[\\uDDE6-\\uDDFF]|" +
                        "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF])" +
                        "(?:\\p{M}|[\\uFE00-\\uFE0F\\u200C]|\\uD83C[\\uDFFB-\\uDFFF]|\\uDB40[\\uDC20-\\uDC7F]|" +
                        "\\u200D(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF]))*)"
                    );
                    break;

                case 'R':
                    // A generic line break: CRLF as a unit, or any single line terminator.
                    // Atomic so that CRLF never backtracks into matching just the CR.
                    _sb.Append("(?>\\r\\n|[\\n\\v\\f\\r\\u0085\\u2028\\u2029])");
                    break;

                case 'K':
                    // Keep: everything matched so far is excluded from the reported match. .NET
                    // has no such operator but does support variable-length lookbehind, so the
                    // pattern emitted so far becomes the lookbehind of what follows.
                    if (_absentDepth != 0 || _groupDepth != 0) {
                        throw MakeError("\\K is only supported at the top level of a pattern");
                    }
                    _sb.Insert(0, "(?<=").Append(')');
                    break;

                case 'G':   // start position
                    _hasGAnchor = true;
                    Append('\\');
                    Append((char)escape);
                    break;

                case 'u':
                    if (Peek() == '{') {
                        // \u{1234 12345 123}
                        foreach (var codepoint in ParseUnicodeEscapeList()) {
                            AppendUnicodeCodePoint(_sb, codepoint);
                        }
                    } else {
                        // \u1234
                        AppendUnicodeCodePoint(_sb, ParseUnicodeEscape());
                    }
                    break;
                    
                default:
                    if (Tokenizer.IsDecimalDigit(escape)) {
                        ParseNumericEscape(escape);
                        break;
                    }

                    ParseCharacterEscape(escape).AppendTo(_sb, false);
                    break;
            }
        }

        // \g<n>  \g'n'  \g<-n>  \g'-n'  \g<name>  \g'name'  \g<0>
        //
        // A subexpression call re-runs a group's pattern at this point. .NET has no such construct,
        // so the group's Ruby source is transformed again and spliced in here. The copy is emitted
        // under the called group's own name or number, and so are the groups nested in it, which
        // .NET permits and treats as the same groups: as in Onigmo, running the copy updates those
        // groups' captures, and the numbering of the rest of the pattern is unchanged.
        //
        // A recursive call is expanded the same way, up to SharedState.MaxCallDepth levels deep;
        // at that depth the call fails to match. Input nested deeper than that is not matched.
        private void ParseSubexpressionCall() {
            int terminator;
            int c = Read();
            if (c == '<') {
                terminator = '>';
            } else if (c == '\'') {
                terminator = '\'';
            } else {
                throw MakeError("invalid group call");
            }

            var reference = new StringBuilder();
            while (true) {
                c = Read();
                if (c == terminator) {
                    break;
                } else if (c == -1) {
                    throw MakeError("invalid group name");
                }
                reference.Append((char)c);
            }

            string name = reference.ToString();
            if (name.Length == 0) {
                throw MakeError("group name is empty");
            }

            if (_state.Collecting) {
                // the groups are not all known yet; this pass only records them
                return;
            }

            // Unlike \k<...>, a call may name a group whose name carries a '+' or a '-'.
            GroupDefinition definition;
            string key;
            if (name == "0") {
                // \g<0> calls the whole pattern
                key = "#0";
                definition = new GroupDefinition { Source = _state.RootPattern, Number = 0 };
            } else if (IsGroupNumber(name)) {
                if (_hasNamedGroup) {
                    throw MakeError("numbered backref/call is not allowed. (use name)");
                }
                int number = ResolveGroupNumber(name);
                key = "#" + number;
                if (!_state.ByNumber.TryGetValue(number, out definition)) {
                    throw MakeError("undefined group <" + name + ">");
                }
            } else {
                key = name;
                if (!_state.ByName.TryGetValue(name, out definition)) {
                    throw MakeError("undefined group name <" + name + ">");
                }
            }

            bool recursive = key == "#0" || IsOpenGroup(key);
            if (recursive && _callDepth >= _state.MaxCallDepth) {
                _sb.Append("(?!)");
                return;
            }

            int depth = _callDepth + 1;
            var copy = new RegexpTransformer(this, definition.Source, definition.Number, definition.OccurrencesBefore, definition.Mode, depth);
            string numberKey = "#" + definition.Number;
            OpenGroup(numberKey);
            if (definition.Name != null) {
                OpenGroup(definition.Name);
            }
            string body;
            try {
                body = TransformCopy(copy);
            } finally {
                CloseGroup(numberKey);
                if (definition.Name != null) {
                    CloseGroup(definition.Name);
                }
            }

            if (definition.Number == 0) {
                _sb.Append("(?:").Append(body).Append(')');
                return;
            }

            _sb.Append("(?<").Append(definition.Name != null ? DuplicateGroupName(definition.Name, definition.Occurrence) : definition.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('>');
            string levelKey = GroupKey(definition.Number, definition.Name);
            if (_state.LevelReferenced.Contains(levelKey)) {
                int level = GetLevelGroup(levelKey, depth);
                _state.DefinedLevelGroups.Add(level);
                _sb.Append("(?<").Append(level).Append('>').Append(body).Append(')');
            } else {
                _sb.Append(body);
            }
            _sb.Append(')');
        }

        private static bool IsGroupNumber(string/*!*/ text) {
            int i = (text.Length != 0 && text[0] == '-') ? 1 : 0;
            if (i == text.Length) {
                return false;
            }
            for (; i < text.Length; i++) {
                if (!Tokenizer.IsDecimalDigit(text[i])) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// \N after a backslash is a backreference, an octal escape or plain text depending on the
        /// number and on how many groups have been opened. .NET decides differently from Ruby, so
        /// the choice has to be made here.
        /// </summary>
        private void ParseNumericEscape(int firstDigit) {
            if (firstDigit == '0') {
                // A leading zero always means an octal escape: (a)\01 matches "a\x01", not "aa".
                Back();
                AppendEscaped(ParseSingleByteCharacterEscape(Read()));
                return;
            }

            int start = _index - 1;
            int value = 0;
            _index = start;
            while (Tokenizer.IsDecimalDigit(Peek())) {
                if (value < 100000) {
                    value = value * 10 + (Read() - '0');
                } else {
                    Skip();
                }
            }
            int digitCount = _index - start;

            if (value > 1000) {
                // Onigmo gives up on a number this large and matches the digits as literal text.
                AppendEscaped(_sb, _rubyPattern.Substring(start, digitCount));
                return;
            }

            if (value <= _groupCount) {
                if (_hasNamedGroup) {
                    throw MakeError("numbered backref/call is not allowed. (use name)");
                }
                AppendBackreference(value, false);
                return;
            }

            if (value >= 10) {
                // Not a group that exists, and too long to be a forward reference: Ruby re-reads
                // the digits as an octal escape, so \10 is "\b".
                _index = start;
                AppendEscaped(ParseSingleByteCharacterEscape(Read()));
                return;
            }

            // A forward reference below 10. .NET accepts these as long as the group eventually
            // appears; it reports its own error if it does not.
            if (_hasNamedGroup) {
                throw MakeError("numbered backref/call is not allowed. (use name)");
            }
            AppendBackreference(value, false);
        }

        /// <summary>
        /// A backreference to a group that has not been closed yet - \1 inside (a\1?)+ - never
        /// matches in Onigmo, even when an earlier iteration captured the group; in .NET it would
        /// match that earlier capture.
        /// </summary>
        private void AppendBackreference(int number, bool bracketed) {
            _backrefCount++;
            if (IsOpenGroup("#" + number)) {
                _sb.Append("(?!)");
            } else if (bracketed) {
                _sb.Append("\\k<").Append(number).Append('>');
            } else {
                _sb.Append('\\').Append(number);
            }
        }

        // \k<n>
        // \k'n'
        // \k<-n>         
        // \k'-n'
        // \k<m+n>
        // \k<m-n>
        // \k'm+n'
        // \k'm-n'
        // \k<name>
        // \k'name'
        // \k<name+n>
        // \k<name-n>
        // \k'name+n'
        // \k'name-n'
        private void ParseBackreference() {
            Debug.Assert(_rubyPattern[_index - 1] == 'k');
            
            int terminator;
            int c = Read();
            if (c == '<') {
                terminator = '>';                
            } else if (c == '\'') {
                terminator = '\'';
            } else {
                throw MakeError("invalid back reference");
            }

            var reference = new StringBuilder();
            while (true) {
                c = Read();
                if (c == terminator) {
                    break;
                } else if (c == -1) {
                    throw MakeError("invalid group name");
                }
                reference.Append((char)c);
            }

            string name = reference.ToString();
            if (name.Length == 0) {
                throw MakeError("group name is empty");
            }

            // \k<0> and \k<-n> that reaches past the first group are rejected by ResolveGroupNumber.
            int number = ResolveGroupNumber(name);
            if (number >= 0) {
                if (_hasNamedGroup) {
                    throw MakeError("numbered backref/call is not allowed. (use name)");
                }
                AppendBackreference(number, true);
                return;
            }

            if (IsLevelSpecifier(name)) {
                ParseLevelBackreference(name);
                return;
            }

            _backrefCount++;
            int count;
            if (_groupNameCounts.TryGetValue(name, out count) && count > 1) {
                // a name several groups share refers to whichever of them has matched
                _sb.Append("(?:");
                for (int i = count; i >= 1; i--) {
                    _sb.Append("\\k<").Append(DuplicateGroupName(name, i)).Append('>');
                    if (i > 1) {
                        _sb.Append('|');
                    }
                }
                _sb.Append(')');
                return;
            }

            if (IsOpenGroup(name)) {
                // see AppendBackreference
                _sb.Append("(?!)");
                return;
            }
            _sb.Append("\\k<").Append(name).Append('>');
        }

        /// <summary>
        /// \k<name+n> and \k<name-n> refer to what the group captured at the subexpression call
        /// level n above or below the reference's own (\k<name-0>: the same level). Each level
        /// of an expanded call is a separate copy, so a group referred to this way is wrapped,
        /// in every copy, in a hidden group of its own for that level (see OpenLevelGroup).
        /// </summary>
        private void ParseLevelBackreference(string/*!*/ reference) {
            int separator = Math.Max(reference.LastIndexOf('+'), reference.LastIndexOf('-'));
            string name = reference.Substring(0, separator);
            string levelText = reference.Substring(separator + 1);
            int offset = 0;
            bool valid = separator > 0 && levelText.Length > 0;
            foreach (char d in levelText) {
                if (!Tokenizer.IsDecimalDigit(d) || offset > 1000) {
                    valid = false;
                    break;
                }
                offset = offset * 10 + (d - '0');
            }

            string key = null;
            if (valid) {
                int number = IsGroupNumber(name) ? ResolveGroupNumber(name) : -1;
                if (number >= 0) {
                    if (_hasNamedGroup) {
                        throw MakeError("numbered backref/call is not allowed. (use name)");
                    }
                    key = "#" + number;
                } else if (_groupNameCounts.ContainsKey(name)) {
                    key = name;
                }
            }
            if (key == null) {
                throw MakeError("invalid group name <" + reference + ">");
            }

            _backrefCount++;
            _state.LevelReferenced.Add(key);
            int level = _callDepth + (reference[separator] == '+' ? offset : -offset);
            if (level < 0 || _state.Collecting) {
                _sb.Append("(?!)");
            } else {
                _sb.Append("\\k<").Append(GetLevelGroup(key, level)).Append('>');
            }
        }

        //
        // group_escape ::= 
        //     '\' '\'
        //   | '\' 'c' character
        //   | '\' 'C' '-' character
        //   | '\' 'M' '-' character
        //   | '\' 'M' '-' '\' 'C' '-' character
        //   | '\' 'p' '{' character_name '}'
        //   | '\' 'P' '{' character_name '}'
        //   | '\' 'x' hex_digit{1-2}
        //   | '\' 'u' hex_digit{4}
        //   | '\' octal{1-3}
        //   | '\' 'h'                                                 # hex digit
        //   | '\' 'H'                                                 # non-hex digit
        //   | '\' 's'                                                 # whitespace 
        //   | '\' 'S'                                                 # non-whitespace 
        //   | '\' 'w'                                                 # word character
        //   | '\' 'W'                                                 # non-word character
        //   | '\' 'd'                                                 # decimal digit 
        //   | '\' 'D'                                                 # non-decimal digit 
        //   | '\' 'b'                                                 # \u0008
        //   | '\' 't'                                                 # \u0009
        //   | '\' 'n'                                                 # \u000A
        //   | '\' 'v'                                                 # \u000B
        //   | '\' 'f'                                                 # \u000C
        //   | '\' other-character                                           
        //
        private int ParseSingleByteCharacterEscape(int escape) {
            bool hasControlModifier = false, hasMetaModifier = false;
            return ParseSingleByteCharacterEscape(escape, ref hasControlModifier, ref hasMetaModifier);
        }

        private int ParseSingleByteCharacterEscape(int escape, ref bool hasControlModifier, ref bool hasMetaModifier) {
            int c;
            switch (escape) {
                case -1: 
                    throw MakeError("too short escape sequence");

                case '\\': return '\\';
                case 'n': return '\n';
                case 't': return '\t';
                case 'r': return '\r';
                case 'f': return '\f';
                case 'v': return '\v';
                case 'a': return '\a';
                case 'e': return 27;
                case 'b': return '\b';
                
                case 'M':
                    if (!Read('-')) {
                        throw MakeError("too short meta escape");
                    }
                    if (hasMetaModifier) {
                        throw MakeError("duplicate meta escape");
                    }

                    hasMetaModifier = true;
                    c = Read();
                    if (c == -1) {
                        throw MakeError("too short escape sequence");
                    }
                    if (c == '\\') {
                        c = ParseSingleByteCharacterEscape(Read(), ref hasControlModifier, ref hasMetaModifier);
                    }
                    
                    return (c & 0xff) | 0x80;

                case 'C':
                    if (!Read('-')) {
                        throw MakeError("too short control escape");
                    }
                    goto case 'c';

                case 'c':
                    c = Read();
                    if (c == -1) {
                        throw MakeError("too short escape sequence");
                    }
                    if (hasControlModifier) {
                        throw MakeError("duplicate control escape");
                    }

                    hasControlModifier = true;
                    if (c == '\\') {
                        c = ParseSingleByteCharacterEscape(Read(), ref hasControlModifier, ref hasMetaModifier);
                    }
                    
                    return c & 0x9f;

                case 'x':
                    // hexa
                    c = Peek();
                    int d1 = Tokenizer.ToDigit(c);
                    if (d1 > 15) {
                        throw MakeError("invalid hex escape");
                    }
                    Skip();

                    c = Peek();
                    int d2 = Tokenizer.ToDigit(c);
                    if (d2 > 15) {
                        return d1;
                    }
                    Skip();

                    return (d1 << 4) | d2;
                
                default:
                    // octal
                    int o1 = Tokenizer.ToDigit(escape);
                    if (o1 > 7) {
                        return -1;
                    }

                    int o2 = Tokenizer.ToDigit(Peek());
                    if (o2 > 7) {
                        return o1;
                    }
                    Skip();

                    int o3 = Tokenizer.ToDigit(Peek());
                    if (o3 > 7) {
                        return (o1 << 3) | o2;
                    }
                    Skip();

                    return (o1 << 6) | (o2 << 3) | o3;
            }
        }

        private CharacterSet/*!*/ ParseCharacterEscape(int escape) {
            int result = ParseSingleByteCharacterEscape(escape);
            if (result != -1) {
                return new CharacterSet(Escape(result), true);
            }
                    
            switch (escape) {
                case 'h':
                case 'H': 
                    return MakePosixCharacterClass(PosixCharacterClass.XDigit, escape == 'h');
                    
                case 'p':
                case 'P':
                    return ParseCharacterCategoryName(escape);

                // \w, \d and \s are ASCII only in Ruby unless (?u) is in effect, where .NET's
                // \w, \d and \s are always Unicode aware.
                case 's':
                    return MakeAsciiAware(@"\s", "\u0020\u0009-\u000d", true);

                case 'S':
                    return MakeAsciiAware(@"\S", "\u0020\u0009-\u000d", false);

                case 'd':
                    return MakeAsciiAware(@"\d", "0-9", true);

                case 'D':
                    return MakeAsciiAware(@"\D", "0-9", false);

                case 'w':
                    return MakeAsciiAware(@"\w", "a-zA-Z0-9_", true);

                case 'W':
                    return MakeAsciiAware(@"\W", "a-zA-Z0-9_", false);

                default:
                    // ignore backslash unless needed
                    return new CharacterSet(Escape(escape), true);
            }
        }

        /// <summary>
        /// Picks between the Unicode-aware .NET shorthand and an explicit ASCII set.
        /// </summary>
        private CharacterSet/*!*/ MakeAsciiAware(string/*!*/ unicodeShorthand, string/*!*/ asciiSet, bool positive) {
            if (_characterClassMode == CharacterClassMode.Unicode) {
                return new CharacterSet(positive ? unicodeShorthand : unicodeShorthand.ToUpperInvariant());
            }
            var result = new CharacterSet(asciiSet);
            return positive ? result : result.Complement();
        }

        #endregion

        #region Unicode Codepoints

        // Peeks exactly 4 hexadecimal characters (\uFFFF).
        private int ParseUnicodeEscape() {
            int d4 = Tokenizer.ToDigit(Read());
            int d3 = Tokenizer.ToDigit(Read());
            int d2 = Tokenizer.ToDigit(Read());
            int d1 = Tokenizer.ToDigit(Read());

            if (d1 >= 16 || d2 >= 16 || d3 >= 16 || d4 >= 16) {
                throw MakeError("invalid Unicode escape");
            }

            int codepoint = (d4 << 12) | (d3 << 8) | (d2 << 4) | d1;
            if (codepoint >= 0xd800 && codepoint <= 0xdfff) {
                throw MakeError("invalid Unicode range");
            }
            return codepoint;
        }

        private IEnumerable<int>/*!*/ ParseUnicodeEscapeList() {
            Skip('{');
            while (true) {
                int codepoint = ParseUnicodeCodePoint();
                int c = Read();
                yield return codepoint;
                if (c == '}') {
                    break;
                }
                if (c != ' ') {
                    throw MakeError("invalid Unicode list");
                }
            }
        }

        // Parses [0-9A-F]{1,6}
        private int ParseUnicodeCodePoint() {
            int codepoint = 0;
            int i = 0;
            while (true) {
                int digit = Tokenizer.ToDigit(Peek());
                if (digit >= 16) {
                    break;
                }

                if (i < 7) {
                    codepoint = (codepoint << 4) | digit;
                }

                i++;
                Skip();
            }

            if (i == 0) {
                throw MakeError("invalid Unicode list");
            }

            // \u{} takes at most six hexadecimal digits; more is a range error even when the
            // leading digits are zeros, which is what MRI reports for \u{0ffffff}.
            if (i > 6) {
                throw MakeError("invalid Unicode range");
            }

            return codepoint;
        }

        private string/*!*/ UnicodeCodePointToString(int codepoint) {
            var sb = new StringBuilder(2);
            AppendUnicodeCodePoint(sb, codepoint);
            return sb.ToString();
        }

        /// <summary>
        /// A character-class member for a single codepoint. Non-BMP codepoints cannot be class
        /// members in .NET (see CharacterSet._astral), so they get their own representation.
        /// </summary>
        private CharacterSet/*!*/ MakeCodePointSet(int codepoint) {
            if (codepoint >= 0xd800 && codepoint <= 0xdfff || codepoint > 0x10ffff) {
                throw MakeError("invalid Unicode range");
            }
            return (codepoint >= 0x10000)
                ? CharacterSet.MakeAstralCharacter(codepoint)
                : new CharacterSet(UnicodeCodePointToString(codepoint), true);
        }

        private void AppendUnicodeCodePoint(StringBuilder/*!*/ builder, int codepoint) {
            if (codepoint >= 0xd800 && codepoint <= 0xdfff || codepoint > 0x10ffff) {
                throw MakeError("invalid Unicode range");
            } else if (codepoint < 0x10000) {
                AppendEscaped(builder, codepoint);
            } else {
                codepoint -= 0x10000;
                builder.Append((char)((codepoint / 0x400) + 0xd800));
                builder.Append((char)((codepoint % 0x400) + 0xdc00));
            }
        }

        #endregion

        #region Chracter Groups

        // [include - [exclude]]
        // ^[include - [exclude]] == [p{All} - [include - [exclude]]
        private sealed class CharacterSet {
            public static readonly CharacterSet Empty = new CharacterSet();

            private readonly bool _negated;
            private readonly string/*!*/ _include;
            private readonly CharacterSet/*!*/ _exclude;
            private readonly bool _isSingleCharacter;

            // .NET's Regex matches UTF-16 code units, so a non-BMP codepoint cannot be a member
            // of a character class: [\uD83E\uDD8A] would match either surrogate half on its own.
            // Such members are held aside as an alternation of surrogate-pair sequences and the
            // whole class is emitted as (?:[bmp members]|<pairs>).
            private readonly string/*!*/ _astral = "";
            // The single codepoint this set stands for, if it is exactly one non-BMP character.
            // Needed to build [x-y] ranges, whose endpoints are parsed as separate sets.
            private readonly int _astralCodepoint = -1;

            public CharacterSet() {
                _include = "";
                _exclude = this;
                _isSingleCharacter = false;
            }
            
            public CharacterSet(string/*!*/ include)
                : this(false, include, Empty) {
            }

            public CharacterSet(string/*!*/ include, bool isSingleCharacter)
                : this(false, include, Empty) {
                _isSingleCharacter = isSingleCharacter;
            }

            public CharacterSet(bool negate, string/*!*/ include)
                : this(negate, include, Empty) {
            }

            public CharacterSet(string/*!*/ include, CharacterSet/*!*/ exclude)
                : this(false, include, exclude) {
            }

            public CharacterSet(bool negate, string/*!*/ include, CharacterSet/*!*/ exclude) {
                Assert.NotNull(include, exclude);
                _negated = negate;
                _include = include;
                _exclude = exclude;
            }

            private CharacterSet(string/*!*/ astral, int astralCodepoint) {
                _include = "";
                _exclude = Empty;
                _astral = astral;
                _astralCodepoint = astralCodepoint;
            }

            /// <summary>A set holding the single non-BMP codepoint <paramref name="codepoint"/>.</summary>
            internal static CharacterSet/*!*/ MakeAstralCharacter(int codepoint) {
                return new CharacterSet(SurrogatePair(codepoint), codepoint);
            }

            /// <summary>A set holding the inclusive non-BMP range [<paramref name="low"/>, <paramref name="high"/>].</summary>
            internal static CharacterSet/*!*/ MakeAstralRange(int low, int high) {
                return new CharacterSet(SurrogateRange(low, high), -1);
            }

            internal bool IsAstralCharacter {
                get { return _astralCodepoint >= 0; }
            }

            internal int AstralCodepoint {
                get { return _astralCodepoint; }
            }

            internal bool HasAstral {
                get { return _astral.Length != 0; }
            }

            private static string/*!*/ Unit(int c) {
                return "\\u" + c.ToString("x4");
            }

            private static string/*!*/ SurrogatePair(int codepoint) {
                int v = codepoint - 0x10000;
                return Unit(0xd800 + (v >> 10)) + Unit(0xdc00 + (v & 0x3ff));
            }

            private static string/*!*/ SurrogateRange(int low, int high) {
                int lowLead = 0xd800 + ((low - 0x10000) >> 10), lowTrail = 0xdc00 + ((low - 0x10000) & 0x3ff);
                int highLead = 0xd800 + ((high - 0x10000) >> 10), highTrail = 0xdc00 + ((high - 0x10000) & 0x3ff);

                if (lowLead == highLead) {
                    return Unit(lowLead) + "[" + Unit(lowTrail) + "-" + Unit(highTrail) + "]";
                }

                var sb = new StringBuilder();
                sb.Append(Unit(lowLead)).Append('[').Append(Unit(lowTrail)).Append("-\\udfff]");
                if (lowLead + 1 <= highLead - 1) {
                    sb.Append("|[").Append(Unit(lowLead + 1)).Append('-').Append(Unit(highLead - 1)).Append("][\\udc00-\\udfff]");
                }
                sb.Append('|').Append(Unit(highLead)).Append("[\\udc00-").Append(Unit(highTrail)).Append(']');
                return sb.ToString();
            }

            private CharacterSet/*!*/ RequireNoAstral(string/*!*/ operation) {
                // the non-BMP members a POSIX class brings along are dropped instead, which leaves
                // such a class as it was before they were added
                if (HasAstral && !_astralOptional) {
                    // The surrogate-pair alternation is not a character class, so it cannot take
                    // part in [a-[b]] subtraction or && intersection.
                    throw new RegexpError("non-BMP character is not supported in a " + operation + " character class");
                }
                return this;
            }

            public string/*!*/ Include {
                get { return _include; }
            }

            public bool IsEmpty {
                get { return _include.Length == 0 && _astral.Length == 0 && !_negated; }
            }

            public bool IsSingleCharacter {
                get { return _isSingleCharacter; }
            }

            private CharacterSet(bool negate, string/*!*/ include, CharacterSet/*!*/ exclude, string/*!*/ astral)
                : this(negate, include, exclude) {
                _astral = astral;
            }

            // The non-BMP members came from a POSIX class ([[:lower:]] and friends) and can be
            // left out where a character class operation cannot keep them.
            private readonly bool _astralOptional;

            private CharacterSet(bool negate, string/*!*/ include, CharacterSet/*!*/ exclude, string/*!*/ astral, bool astralOptional)
                : this(negate, include, exclude, astral) {
                _astralOptional = astralOptional;
            }

            /// <summary>This set plus non-BMP members that set operations may drop (see _astralOptional).</summary>
            internal CharacterSet/*!*/ WithOptionalAstral(string/*!*/ astral) {
                Debug.Assert(!_negated);
                return new CharacterSet(false, _include, _exclude, JoinAstral(_astral, astral), !HasAstral || _astralOptional);
            }

            private static string/*!*/ JoinAstral(string/*!*/ a, string/*!*/ b) {
                return (a.Length == 0) ? b : (b.Length == 0) ? a : a + "|" + b;
            }

            internal CharacterSet/*!*/ GetIncludedSet() {
                return new CharacterSet(_include, _isSingleCharacter);
            }

            internal CharacterSet/*!*/ Complement() {
                RequireNoAstral("negated");
                return new CharacterSet(!_negated, _include, _exclude);
            }

            internal CharacterSet/*!*/ Subtract(CharacterSet/*!*/ set) {
                if (IsEmpty || set.IsEmpty) {
                    return this;
                }
                RequireNoAstral("subtracted");
                set.RequireNoAstral("subtracted");

                if (_negated) {
                    if (set._negated) {
                        // (^A) \ (^B) = ^A and B = B \ A
                        return set.Complement().Subtract(Complement());
                    } else {
                        // (^A) \ B == ^(A or B)
                        return Complement().Union(set).Complement();
                    }
                } else if (set._negated) {
                    // A \ ^(B) == A and B
                    return Intersect(set.Complement());
                }

                // (a \ B) \ C == a \ (B or C)
                return new CharacterSet(_include, _exclude.Union(set));
            }

            internal CharacterSet/*!*/ Union(CharacterSet/*!*/ set) {
                if (IsEmpty) {
                    return set;
                } else if (set.IsEmpty) {
                    return this;
                }

                if (_negated) {
                    if (set._negated) {
                        // ^A or ^B == ^(A and B)
                        return Complement().Intersect(set.Complement()).Complement();
                    } else {
                        // ^A or B == ^(A \ B)
                        return Complement().Subtract(set).Complement();
                    }
                } else if (set._negated) {
                    // A or ^B == ^(B \ A)
                    return set.Complement().Subtract(this).Complement();
                }

                // (a \ B) or (c \ D) == (a or c) \ ((D \ a) or (B \ c) or (B and D))
                //
                // Proof: 
                // (a \ B) or (c \ D) == 
                // (a and ^B) or (c and ^D) == 
                // (a or c) and (a or ^D) and (^B or c) and (^B or ^D) ==
                // (a or c) \ (^(a or ^D) or ^(^B or c) or ^(^B or ^D)) ==
                // (a or c) \ ((D \ a) or (B \ c) or (B and D))                QED
                return new CharacterSet(false, _include + set._include,
                    set._exclude.Subtract(GetIncludedSet()).
                        Union(this._exclude.Subtract(set.GetIncludedSet())).
                        Union(this._exclude.Intersect(set._exclude)),
                    JoinAstral(_astral, set._astral),
                    (!HasAstral || _astralOptional) && (!set.HasAstral || set._astralOptional)
                );
            }

            internal CharacterSet/*!*/ Intersect(CharacterSet/*!*/ set) {
                if (IsEmpty || set.IsEmpty) {
                    return Empty;
                }
                RequireNoAstral("intersected");
                set.RequireNoAstral("intersected");

                if (_negated) {
                    if (set._negated) {
                        // ^A and ^B == ^(A or B)
                        return Complement().Union(set.Complement()).Complement();
                    } else {
                        // ^A and B = B \ A
                        return set.Subtract(Complement());
                    }
                } else if (set._negated) {
                    // A and ^B = A \ B
                    return Subtract(set.Complement());
                }

                // (a \ B) and (c \ D) == a \ ^(c \ (B or D))
                // 
                // Proof:
                // (a \ B) and (c \ D) == 
                // (a and ^B) and (c and ^D) ==
                // a \ ^(c and ^B and ^D) ==
                // a \ ^(c \ (B or D))          QED
                return new CharacterSet(_include, new CharacterSet(true, set._include, _exclude.Union(set._exclude)));
            }

            public StringBuilder/*!*/ AppendTo(StringBuilder/*!*/ sb, bool parenthesize) {
                if (HasAstral) {
                    sb.Append("(?:");
                    if (_include.Length != 0 || !_exclude.IsEmpty) {
                        sb.Append('[').Append(_include);
                        if (!_exclude.IsEmpty) {
                            sb.Append('-');
                            _exclude.AppendTo(sb, true);
                        }
                        sb.Append("]|");
                    }
                    sb.Append(_astral);
                    sb.Append(')');
                    return sb;
                }
                if (IsEmpty) {
                    if (_negated) {
                        sb.Append("[\0-\uffff]");
                    } else {
                        sb.Append("[a-[a]]");
                    }
                } else if (IsSingleCharacter && !parenthesize) {
                    sb.Append(_include);
                } else {
                    if (_negated) {
                        sb.Append("[\0-\uffff-");
                    }
                    sb.Append('[');
                    sb.Append(_include);
                    if (!_exclude.IsEmpty) {
                        sb.Append('-');
                        _exclude.AppendTo(sb, true);
                    }
                    sb.Append(']');
                    if (_negated) {
                        sb.Append(']');
                    }
                }
                return sb;
            }

            public override string/*!*/ ToString() {
                return IsEmpty ? String.Empty : AppendTo(new StringBuilder(), false).ToString();
            }
        }

        //
        // group ::= '[' negation_opt intersection ']'
        //
        // negation_opt ::= '^' | <empty>
        //
        private CharacterSet/*!*/ ParseCharacterGroup(bool nested) {
            Debug.Assert(_rubyPattern[_index - 1] == '[');

            bool positive = !Read('^');

            // [:alnum:]
            // [^:alnum:]
            if (nested) {
                var posixClass = ParsePosixCharacterClass(positive);
                if (posixClass != null) {
                    return posixClass;
                }
            }

            var result = ParseCharacterGroupIntersections();
            if (!positive) {
                result = result.Complement();
            }
            Debug.Assert(Peek() == -1 || Peek() == ']');
            Read(']');
            return result;
        }

        // 
        // intersection ::= intersection '&' '&' union
        //                | union
        // 
        private CharacterSet/*!*/ ParseCharacterGroupIntersections() {
            CharacterSet result = null;
            int c;
            while ((c = Peek()) != -1 && c != ']') {
                // eats &&
                var set = ParseCharacterGroupUnion();

                // result = result and set
                result = (result != null) ? result.Intersect(set) : set;
            }

            if (result == null) {
                throw MakeError((c == -1) ? "premature end of char-class" : "empty char-class");
            }

            return result;
        }

        //
        // union ::= union term
        //         | term
        //
        // term ::= escape
        //        | group
        //        | posix_character_class
        //        | character '-' character
        //        | character
        //
        // posix_character_class ::= '[' negation_opt ':' posix_character_class_name ':' ']' 
        //
        private CharacterSet ParseCharacterGroupUnion() {
            CharacterSet result = CharacterSet.Empty;

            // \u{1 2 3} produces multiple characters, the first and the last might be range bounds:
            IEnumerator<int> codepoints = null;

            while (true) {
                bool mayStartRange;
                var set = ParseCharacter(ref codepoints, out mayStartRange);
                if (set == null) {
                    break;
                }

                if (codepoints == null && Read('-')) {
                    // [a-]
                    // [a-&&b]
                    bool mayEndRange;
                    var rangeEnd = ParseCharacter(ref codepoints, out mayEndRange);
                    if (rangeEnd == null) {
                        result = result.Union(set).Union(new CharacterSet(@"\-", true));
                        break;
                    }

                    // [a-b]-z
                    // \p{L}-z
                    if (!mayStartRange || !(set.IsSingleCharacter || set.IsAstralCharacter)) {
                        throw MakeError("char-class value at start of range");
                    }

                    // a-[a-z]
                    // a-\p{L}
                    if (!mayEndRange || !(rangeEnd.IsSingleCharacter || rangeEnd.IsAstralCharacter)) {
                        throw MakeError("char-class value at end of range");
                    }

                    if (set.IsAstralCharacter || rangeEnd.IsAstralCharacter) {
                        if (!set.IsAstralCharacter || !rangeEnd.IsAstralCharacter) {
                            // A range straddling U+FFFF would need both a class and an alternation.
                            throw MakeError("char-class range crosses the BMP boundary");
                        }
                        if (set.AstralCodepoint > rangeEnd.AstralCodepoint) {
                            throw MakeError("empty range in char class");
                        }
                        set = CharacterSet.MakeAstralRange(set.AstralCodepoint, rangeEnd.AstralCodepoint);
                    } else {
                        set = new CharacterSet(set.Include + "-" + rangeEnd.Include);
                    }
                }

                result = result.Union(set);
            }
            return result;
        }

        private CharacterSet ParseCharacter(ref IEnumerator<int> codepoints, out bool mayStartRange) {
            if (codepoints != null) {
                mayStartRange = true;
                int current = codepoints.Current;
                if (!codepoints.MoveNext()) {
                    codepoints = null;
                }
                return MakeCodePointSet(current);
            }

            int c;
            switch (c = Read()) {
                case -1:
                    throw MakeError("premature end of char-class");

                case ']':
                    Back();
                    mayStartRange = false;
                    return null;

                case '&':
                    if (Read('&')) {
                        mayStartRange = false;
                        return null;
                    }
                    goto default;

                case '\\':
                    int escape = Read();
                    if (escape == 'u') {
                        int codepoint;
                        if (Peek() == '{') {
                            codepoints = ParseUnicodeEscapeList().GetEnumerator();
                            if (!codepoints.MoveNext()) {
                                throw MakeError("invalid Unicode list");
                            }
                            codepoint = codepoints.Current;
                            if (!codepoints.MoveNext()) {
                                codepoints = null;
                            }
                        } else {
                            codepoint = ParseUnicodeEscape();
                        }
                        mayStartRange = true;
                        return MakeCodePointSet(codepoint);
                    } else {
                        mayStartRange = true;
                        return ParseCharacterEscape(escape);
                    }

                case '[':
                    mayStartRange = false;
                    return ParseCharacterGroup(true);

                case '-':
                    // warning: character class has '-' without escape
                    mayStartRange = true;
                    return new CharacterSet(@"\-", true);

                default:
                    mayStartRange = true;
                    if (c >= 0xd800 && c <= 0xdbff && Peek() >= 0xdc00 && Peek() <= 0xdfff) {
                        // a literal non-BMP character, written as its surrogate pair
                        return CharacterSet.MakeAstralCharacter(Char.ConvertToUtf32((char)c, (char)Read()));
                    }
                    return new CharacterSet(((char)c).ToString(), true);
            }
        }

        private enum PosixCharacterClass {
            Alnum,
            Alpha,
            Ascii,
            Blank,
            Cntrl,
            Digit,
            Graph,
            Lower,
            Print,
            Punct,
            Space,
            Upper,
            XDigit,
            Word,
        }

        //
        //  \p{property-name}
        //  \p{^property-name}    (negative)
        //  \P{property-name}     (negative)
        //          
        // Property-name:
        //          
        //  + works on all encodings
        //    Alnum, Alpha, Blank, Cntrl, Digit, Graph, Lower,
        //    Print, Punct, Space, Upper, XDigit, Word, ASCII,
        //          
        //  + works on EUC_JP, Shift_JIS
        //    Hiragana, Katakana
        //          
        //  + works on UTF8, UTF16, UTF32
        //    Any, Assigned, C, Cc, Cf, Cn, Co, Cs, L, Ll, Lm, Lo, Lt, Lu,
        //    M, Mc, Me, Mn, N, Nd, Nl, No, P, Pc, Pd, Pe, Pf, Pi, Po, Ps,
        //    S, Sc, Sk, Sm, So, Z, Zl, Zp, Zs, 
        //    Arabic, Armenian, Bengali, Bopomofo, Braille, Buginese,
        //    Buhid, Canadian_Aboriginal, Cherokee, Common, Coptic,
        //    Cypriot, Cyrillic, Deseret, Devanagari, Ethiopic, Georgian,
        //    Glagolitic, Gothic, Greek, Gujarati, Gurmukhi, Han, Hangul,
        //    Hanunoo, Hebrew, Hiragana, Inherited, Kannada, Katakana,
        //    Kharoshthi, Khmer, Lao, Latin, Limbu, Linear_B, Malayalam,
        //    Mongolian, Myanmar, New_Tai_Lue, Ogham, Old_Italic, Old_Persian,
        //    Oriya, Osmanya, Runic, Shavian, Sinhala, Syloti_Nagri, Syriac,
        //    Tagalog, Tagbanwa, Tai_Le, Tamil, Telugu, Thaana, Thai, Tibetan,
        //    Tifinagh, Ugaritic, Yi
        //
        /// <summary>
        /// Onigmo matches a property name ignoring case, underscores, hyphens and spaces, and
        /// knows the long Unicode names ("Letter") beside the two-letter ones ("L"); the CLR
        /// knows only the exact two-letter forms.  Names not listed here are handed on as they
        /// were written - the block names ("IsGreek") and the script names are matched further
        /// down, and an unknown one has to reach the CLR to be reported as unknown.
        /// </summary>
        private static string/*!*/ NormalizeCharacterCategoryName(string/*!*/ name) {
            var key = new StringBuilder(name.Length);
            foreach (char c in name) {
                if (c != '_' && c != '-' && c != ' ') {
                    key.Append(Char.ToLowerInvariant(c));
                }
            }

            string canonical;
            return _characterCategoryAliases.TryGetValue(key.ToString(), out canonical) ? canonical : name;
        }

        private static readonly Dictionary<string, string> _characterCategoryAliases =
            new Dictionary<string, string>() {
                // POSIX classes, which Ruby also spells in any case
                { "alnum", "Alnum" }, { "alpha", "Alpha" }, { "blank", "Blank" }, { "cntrl", "Cntrl" },
                { "digit", "Digit" }, { "graph", "Graph" }, { "lower", "Lower" }, { "print", "Print" },
                { "punct", "Punct" }, { "space", "Space" }, { "upper", "Upper" }, { "xdigit", "XDigit" },
                { "ascii", "ASCII" }, { "word", "Word" }, { "any", "Any" }, { "assigned", "Assigned" },

                // general categories: the short name in any case, and the long name
                { "l", "L" }, { "letter", "L" },
                { "lc", "Lc" }, { "casedletter", "Lc" },
                { "lu", "Lu" }, { "uppercaseletter", "Lu" },
                { "ll", "Ll" }, { "lowercaseletter", "Ll" },
                { "lt", "Lt" }, { "titlecaseletter", "Lt" },
                { "lm", "Lm" }, { "modifierletter", "Lm" },
                { "lo", "Lo" }, { "otherletter", "Lo" },
                { "m", "M" }, { "mark", "M" }, { "combiningmark", "M" },
                { "mn", "Mn" }, { "nonspacingmark", "Mn" },
                { "mc", "Mc" }, { "spacingmark", "Mc" }, { "spacingcombiningmark", "Mc" },
                { "me", "Me" }, { "enclosingmark", "Me" },
                { "n", "N" }, { "number", "N" },
                { "nd", "Nd" }, { "decimalnumber", "Nd" }, { "digitnumber", "Nd" },
                { "nl", "Nl" }, { "letternumber", "Nl" },
                { "no", "No" }, { "othernumber", "No" },
                { "p", "P" }, { "punctuation", "P" },
                { "pc", "Pc" }, { "connectorpunctuation", "Pc" },
                { "pd", "Pd" }, { "dashpunctuation", "Pd" },
                { "ps", "Ps" }, { "openpunctuation", "Ps" },
                { "pe", "Pe" }, { "closepunctuation", "Pe" },
                { "pi", "Pi" }, { "initialpunctuation", "Pi" },
                { "pf", "Pf" }, { "finalpunctuation", "Pf" },
                { "po", "Po" }, { "otherpunctuation", "Po" },
                { "s", "S" }, { "symbol", "S" },
                { "sm", "Sm" }, { "mathsymbol", "Sm" },
                { "sc", "Sc" }, { "currencysymbol", "Sc" },
                { "sk", "Sk" }, { "modifiersymbol", "Sk" },
                { "so", "So" }, { "othersymbol", "So" },
                { "z", "Z" }, { "separator", "Z" },
                { "zs", "Zs" }, { "spaceseparator", "Zs" },
                { "zl", "Zl" }, { "lineseparator", "Zl" },
                { "zp", "Zp" }, { "paragraphseparator", "Zp" },
                { "c", "C" }, { "other", "C" },
                { "cc", "Cc" }, { "control", "Cc" },
                { "cf", "Cf" }, { "format", "Cf" },
                { "cs", "Cs" }, { "surrogate", "Cs" },
                { "co", "Co" }, { "privateuse", "Co" },
                { "cn", "Cn" }, { "unassigned", "Cn" },
            };

        private CharacterSet/*!*/ ParseCharacterCategoryName(int escape) {
            bool positive = escape == 'p';

            int c = Peek();
            if (c != '{') {
                throw MakeError("invalid Unicode property");
            }
            Skip();

            // CLR doesn't support ^:
            if (Peek() == '^') {
                positive = !positive;
                Skip();
            }

            int start = _index;

            while ((c = Peek()) != '}' && c != -1) {
                Skip();
            }

            // trailing }
            if (c == -1) {
                throw MakeError("invalid Unicode property");
            }
            
            string name = NormalizeCharacterCategoryName(_rubyPattern.Substring(start, _index - start));
            Skip();

            var script = MakeScriptCharacterClass(name);
            if (script != null) {
                return positive ? script : script.Complement();
            }

            switch (name) {
                // CLR unsupported, any encoding:
                case "Alnum": return MakePosixCharacterClass(PosixCharacterClass.Alnum, positive); 
                case "Alpha": return MakePosixCharacterClass(PosixCharacterClass.Alpha, positive); 
                case "Blank": return MakePosixCharacterClass(PosixCharacterClass.Blank, positive); 
                case "Cntrl": return MakePosixCharacterClass(PosixCharacterClass.Cntrl, positive); 
                case "Digit": return MakePosixCharacterClass(PosixCharacterClass.Digit, positive); 
                case "Graph": return MakePosixCharacterClass(PosixCharacterClass.Graph, positive); 
                case "Lower": return MakePosixCharacterClass(PosixCharacterClass.Lower, positive); 
                case "Print": return MakePosixCharacterClass(PosixCharacterClass.Print, positive); 
                case "Punct": return MakePosixCharacterClass(PosixCharacterClass.Punct, positive); 
                case "Space": return MakePosixCharacterClass(PosixCharacterClass.Space, positive); 
                case "Upper": return MakePosixCharacterClass(PosixCharacterClass.Upper, positive); 
                case "XDigit": return MakePosixCharacterClass(PosixCharacterClass.XDigit, positive);
                case "ASCII": return MakePosixCharacterClass(PosixCharacterClass.Ascii, positive);
                case "Word": return MakePosixCharacterClass(PosixCharacterClass.Word, positive); 

                // CLR unsupported, Unicode only:
                case "Any":
                    // conjunction of any two disjunctive categories:
                    if (positive) {
                        return new CharacterSet(@"\P{L}\P{N}");
                    } else {
                        return new CharacterSet(@"\p{L}", new CharacterSet(@"\p{L}"));
                    }

                case "Assigned":
                    positive = !positive;
                    name = "Cn";
                    goto default;

                case "Lc":
                case "Cased_Letter":
                    // Onigmo's grouped general category; the CLR knows only the three
                    // single categories it stands for. rdoc's Text module asks for it.
                    if (positive) {
                        return new CharacterSet(@"\p{Lu}\p{Ll}\p{Lt}");
                    } else {
                        return new CharacterSet(@"\P{Lu}", new CharacterSet(@"\p{Ll}\p{Lt}"));
                    }

                case "Arabic": 
                case "Armenian": 
                case "Bengali": 
                case "Bopomofo": 
                case "Buhid": 
                case "Cherokee": 
                case "Cyrillic": 
                case "Devanagari": 
                case "Ethiopic": 
                case "Georgian":
                case "Greek": 
                case "Gujarati": 
                case "Gurmukhi": 
                case "Hanunoo": 
                case "Hebrew": 
                case "Kannada": 
                case "Khmer": 
                case "Lao": 
                case "Limbu": 
                case "Malayalam":
                case "Mongolian": 
                case "Myanmar": 
                case "Ogham": 
                case "Oriya": 
                case "Runic": 
                case "Sinhala": 
                case "Syriac":
                case "Tagalog": 
                case "Tagbanwa": 
                case "TaiLe": 
                case "Tamil": 
                case "Telugu":
                case "Thaana": 
                case "Thai": 
                case "Tibetan":
                    // For these scripts .NET happens to have a block of the same name. A block is
                    // not a script, so this over-matches at the edges, but it is what is available.
                    name = "Is" + name;
                    goto default;

                case "Canadian_Aboriginal":
                    name = "IsUnifiedCanadianAboriginalSyllabics";
                    goto default;

                default:
                    return new CharacterSet(@"\" + (positive ? 'p' : 'P') + "{" + name + "}");
            }
        }

        /// <summary>
        /// Unicode *script* properties for the scripts .NET has no identically named block for.
        /// .NET's Regex knows nothing about scripts, only about a fixed list of Unicode 4.0 era
        /// BMP blocks, so these are approximations: a script's codepoints are enumerated as
        /// explicit ranges. Returns null for a name this method does not handle.
        /// </summary>
        private CharacterSet MakeScriptCharacterClass(string/*!*/ name) {
            switch (name) {
                case "Han":
                    // CJK Radicals Supplement, Kangxi Radicals, the Han characters scattered
                    // through CJK Symbols and Punctuation, Extension A, the URO and the
                    // compatibility ideographs. Non-BMP extensions are out of reach (see
                    // CharacterSet._astral: a class cannot hold a surrogate pair).
                    return new CharacterSet("\u2e80-\u2e99\u2e9b-\u2ef3\u2f00-\u2fd5\u3005\u3007" +
                        "\u3021-\u3029\u3038-\u303b\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufa6d\ufa70-\ufad9");

                case "Hangul":
                    // Jamo, Compatibility Jamo, Jamo Extended-A/B and the syllables block.
                    return new CharacterSet("\u1100-\u11ff\u302e\u302f\u3131-\u318e\ua960-\ua97c" +
                        "\uac00-\ud7a3\ud7b0-\ud7c6\ud7cb-\ud7fb\uffa0-\uffbe\uffc2-\uffc7" +
                        "\uffca-\uffcf\uffd2-\uffd7\uffda-\uffdc");

                case "Latin":
                    return new CharacterSet(@"\p{IsBasicLatin}\p{IsLatin-1Supplement}\p{IsLatinExtended-A}" +
                        @"\p{IsLatinExtended-B}\p{IsLatinExtendedAdditional}" +
                        "\u2c60-\u2c7f\ua720-\ua7ff\ufb00-\ufb06\uff21-\uff3a\uff41-\uff5a");

                case "Braille":
                    return new CharacterSet(@"\p{IsBraillePatterns}");

                case "Hiragana":
                    // Not the whole IsHiragana block: U+3099-U+309C are Inherited/Common and
                    // U+309B/U+309C are not Hiragana either.
                    return new CharacterSet("\u3041-\u3096\u309d-\u309f");

                case "Katakana":
                    // U+30A0, U+30FB and U+30FC (the prolonged sound mark) sit inside the
                    // IsKatakana block but are script Common, so the block over-matches.
                    return new CharacterSet("\u30a1-\u30fa\u30fd-\u30ff\u31f0-\u31ff" +
                        "\u32d0-\u32fe\u3300-\u3357\uff66-\uff6f\uff71-\uff9d");

                case "Coptic":
                    return new CharacterSet("\u03e2-\u03ef\u2c80-\u2cff\u2e00-\u2e01");

                case "Glagolitic":
                    return new CharacterSet("\u2c00-\u2c5f");

                case "Tifinagh":
                    return new CharacterSet("\u2d30-\u2d7f");

                case "Syloti_Nagri":
                    return new CharacterSet("\ua800-\ua82c");

                case "New_Tai_Lue":
                    return new CharacterSet("\u1980-\u19df");

                case "Buginese":
                    return new CharacterSet("\u1a00-\u1a1f");

                case "Yi":
                    return new CharacterSet(@"\p{IsYiSyllables}\p{IsYiRadicals}");

                case "Common":
                case "Inherited":
                case "Cypriot":
                case "Deseret":
                case "Gothic":
                case "Kharoshthi":
                case "Linear_B":
                case "Old_Italic":
                case "Old_Persian":
                case "Osmanya":
                case "Shavian":
                    // Either not a block at all (Common, Inherited span the whole repertoire) or
                    // entirely outside the BMP, which .NET cannot address in a character class.
                    throw MakeError("character property '" + name + "' is not supported");

                default:
                    return null;
            }
        }

        // [:xxx:] in character class
        // [^:xxx:] in character class
        private CharacterSet ParsePosixCharacterClass(bool positive) {
            int i = 0;
            if (Peek(i) == ':') {
                i++;
            } else {
                return null;
            }

            int start = _index + i;

            int c;
            while ((c = Peek(i)) != ':' && c != -1) {
                i++;
            }

            if (c == -1 || Peek(i + 1) != ']') {
                return null;
            }

            string name = _rubyPattern.Substring(start, _index + i - start);
            _index += i + 2;

            return MakePosixCharacterClass(ParsePosixClass(name), positive);
        }

        private PosixCharacterClass ParsePosixClass(string/*!*/ name) {
            switch (name) {
                case "alnum": return PosixCharacterClass.Alnum;
                case "alpha": return PosixCharacterClass.Alpha;
                case "ascii": return PosixCharacterClass.Ascii;
                case "blank": return PosixCharacterClass.Blank;
                case "cntrl": return PosixCharacterClass.Cntrl;
                case "digit": return PosixCharacterClass.Digit;
                case "graph": return PosixCharacterClass.Graph;
                case "lower": return PosixCharacterClass.Lower;
                case "print": return PosixCharacterClass.Print;
                case "punct": return PosixCharacterClass.Punct;
                case "space": return PosixCharacterClass.Space;
                case "upper": return PosixCharacterClass.Upper;
                case "xdigit": return PosixCharacterClass.XDigit;
                case "word": return PosixCharacterClass.Word;
                default: 
                    throw MakeError("invalid POSIX bracket type");
            }
        }

        private CharacterSet MakePosixCharacterClass(PosixCharacterClass charClass, bool positive) {
            if (_characterClassMode == CharacterClassMode.Ascii) {
                // (?a) restricts the POSIX classes to ASCII. A negated class still matches
                // non-ASCII: (?a)[[:^alpha:]] accepts a Hiragana character.
                var ascii = MakePosixCharacterClassCore(charClass, true)
                    .Intersect(new CharacterSet(@"\p{IsBasicLatin}"));
                return positive ? ascii : ascii.Complement();
            }
            return MakePosixCharacterClassCore(charClass, positive);
        }

        private CharacterSet MakePosixCharacterClassCore(PosixCharacterClass charClass, bool positive) {
            var result = MakeBmpPosixCharacterClass(charClass, positive);
            if (positive) {
                // .NET matches UTF-16 code units, so a class alone never matches a character
                // outside the BMP; Onigmo's POSIX classes range over all of Unicode
                string astral = GetAstralPosixMembers(charClass);
                if (astral != null) {
                    result = result.WithOptionalAstral(astral);
                }
            }
            return result;
        }

        private static readonly Dictionary<PosixCharacterClass, string> _astralPosixMembers = new Dictionary<PosixCharacterClass, string>();

        private static string GetAstralPosixMembers(PosixCharacterClass charClass) {
            Func<System.Globalization.UnicodeCategory, bool> member;
            switch (charClass) {
                case PosixCharacterClass.Lower: member = c => c == System.Globalization.UnicodeCategory.LowercaseLetter; break;
                case PosixCharacterClass.Upper: member = c => c == System.Globalization.UnicodeCategory.UppercaseLetter; break;
                case PosixCharacterClass.Alpha: member = c => IsLetter(c) || c == System.Globalization.UnicodeCategory.LetterNumber; break;
                case PosixCharacterClass.Alnum: member = c => IsLetter(c) || c == System.Globalization.UnicodeCategory.LetterNumber || c == System.Globalization.UnicodeCategory.DecimalDigitNumber; break;
                case PosixCharacterClass.Digit: member = c => c == System.Globalization.UnicodeCategory.DecimalDigitNumber; break;
                case PosixCharacterClass.Word:
                    member = c => IsLetter(c) || c == System.Globalization.UnicodeCategory.NonSpacingMark || c == System.Globalization.UnicodeCategory.SpacingCombiningMark ||
                        c == System.Globalization.UnicodeCategory.EnclosingMark || c == System.Globalization.UnicodeCategory.DecimalDigitNumber ||
                        c == System.Globalization.UnicodeCategory.LetterNumber || c == System.Globalization.UnicodeCategory.ConnectorPunctuation;
                    break;
                case PosixCharacterClass.Print:
                    member = c => c != System.Globalization.UnicodeCategory.Control && c != System.Globalization.UnicodeCategory.OtherNotAssigned &&
                        c != System.Globalization.UnicodeCategory.Surrogate && c != System.Globalization.UnicodeCategory.LineSeparator &&
                        c != System.Globalization.UnicodeCategory.ParagraphSeparator;
                    break;
                case PosixCharacterClass.Graph:
                    member = c => c != System.Globalization.UnicodeCategory.Control && c != System.Globalization.UnicodeCategory.OtherNotAssigned &&
                        c != System.Globalization.UnicodeCategory.Surrogate && c != System.Globalization.UnicodeCategory.LineSeparator &&
                        c != System.Globalization.UnicodeCategory.ParagraphSeparator && c != System.Globalization.UnicodeCategory.SpaceSeparator;
                    break;
                case PosixCharacterClass.Punct:
                    member = c => c >= System.Globalization.UnicodeCategory.ConnectorPunctuation && c <= System.Globalization.UnicodeCategory.OtherPunctuation;
                    break;
                default:
                    return null;
            }

            lock (_astralPosixMembers) {
                string result;
                if (!_astralPosixMembers.TryGetValue(charClass, out result)) {
                    _astralPosixMembers[charClass] = result = BuildAstralMembers(member);
                }
                return result;
            }
        }

        private static bool IsLetter(System.Globalization.UnicodeCategory c) {
            return c <= System.Globalization.UnicodeCategory.OtherLetter;
        }

        /// <summary>
        /// The non-BMP code points in a category set as surrogate pairs: one character class of
        /// trailing surrogates per leading one (or run of leading ones with the same trailing
        /// class), behind a lookahead that turns away anything that is not a leading surrogate.
        /// </summary>
        private static string/*!*/ BuildAstralMembers(Func<System.Globalization.UnicodeCategory, bool>/*!*/ member) {
            var alternatives = new List<KeyValuePair<int, string>>();
            var trails = new StringBuilder();
            for (int lead = 0; lead < 0x400; lead++) {
                trails.Length = 0;
                int rangeStart = -1;
                for (int trail = 0; trail <= 0x400; trail++) {
                    bool isMember = trail < 0x400 &&
                        member(System.Globalization.CharUnicodeInfo.GetUnicodeCategory(0x10000 + (lead << 10) + trail));
                    if (isMember && rangeStart < 0) {
                        rangeStart = trail;
                    } else if (!isMember && rangeStart >= 0) {
                        trails.Append("\\u").Append((0xdc00 + rangeStart).ToString("x4"));
                        if (trail - 1 > rangeStart) {
                            trails.Append("-\\u").Append((0xdc00 + trail - 1).ToString("x4"));
                        }
                        rangeStart = -1;
                    }
                }
                if (trails.Length > 0) {
                    alternatives.Add(new KeyValuePair<int, string>(lead, trails.ToString()));
                }
            }

            if (alternatives.Count == 0) {
                return "[a-[a]]";
            }

            var result = new StringBuilder("(?=[\\ud800-\\udbff])(?:");
            for (int i = 0; i < alternatives.Count; ) {
                int j = i;
                while (j + 1 < alternatives.Count && alternatives[j + 1].Key == alternatives[j].Key + 1 &&
                    alternatives[j + 1].Value == alternatives[i].Value) {
                    j++;
                }
                if (i > 0) {
                    result.Append('|');
                }
                result.Append('[').Append("\\u").Append((0xd800 + alternatives[i].Key).ToString("x4"));
                if (j > i) {
                    result.Append("-\\u").Append((0xd800 + alternatives[j].Key).ToString("x4"));
                }
                result.Append("][").Append(alternatives[i].Value).Append(']');
                i = j + 1;
            }
            return result.Append(')').ToString();
        }

        private CharacterSet MakeBmpPosixCharacterClass(PosixCharacterClass charClass, bool positive) {
            switch (charClass) {
                case PosixCharacterClass.Alnum:
                    if (positive) {
                        return new CharacterSet(@"\p{L}\p{Nd}\p{Nl}");
                    } else {
                        return new CharacterSet(@"\P{L}", new CharacterSet(@"\p{Nd}\p{Nl}"));
                    }

                case PosixCharacterClass.Alpha:
                    if (positive) {
                        return new CharacterSet(@"\p{L}\p{Nl}");
                    } else {
                        return new CharacterSet(@"\P{L}", new CharacterSet(@"\p{Nl}"));
                    }

                case PosixCharacterClass.Ascii:
                    if (positive) {
                        return new CharacterSet(@"\p{IsBasicLatin}"); 
                    } else {
                        return new CharacterSet(@"\P{IsBasicLatin}"); 
                    }

                case PosixCharacterClass.Blank:
                    if (positive) {
                        return new CharacterSet("\\p{Zs}\t"); 
                    } else {
                        return new CharacterSet(@"\P{Zs}", new CharacterSet("\t")); 
                    }
                    
                case PosixCharacterClass.Cntrl:
                    if (positive) {
                        return new CharacterSet(@"\p{Cc}"); 
                    } else {
                        return new CharacterSet(@"\P{Cc}"); 
                    }

                case PosixCharacterClass.Digit:
                    if (positive) {
                        return new CharacterSet(@"\p{Nd}"); 
                    } else {
                        return new CharacterSet(@"\P{Nd}"); 
                    }

                case PosixCharacterClass.Graph:
                    if (positive) {
                        return new CharacterSet(@"\P{Z}", new CharacterSet(@"\p{Cc}\p{Cn}\p{Cs}"));
                    } else {
                        return new CharacterSet(@"\p{Z}\p{Cc}\p{Cn}\p{Cs}");
                    }

                case PosixCharacterClass.Lower:
                    // TODO: there are some differences (Unicode version?)
                    if (positive) {
                        return new CharacterSet(@"\p{Ll}"); 
                    } else {
                        return new CharacterSet(@"\P{Ll}"); 
                    }

                case PosixCharacterClass.Print:
                    if (positive) {
                        return new CharacterSet(@"\P{Zl}", new CharacterSet(@"\p{Zp}\p{Cc}\p{Cn}\p{Cs}"));
                    } else {
                        return new CharacterSet(@"\p{Zl}\p{Zp}\p{Cc}\p{Cn}\p{Cs}");
                    }

                case PosixCharacterClass.Punct:
                    if (positive) {
                        return new CharacterSet(@"\p{P}"); 
                    } else {
                        return new CharacterSet(@"\P{P}"); 
                    }

                case PosixCharacterClass.Space:
                    if (positive) {
                        return new CharacterSet("\\p{Z}\u0085\u0009-\u000d"); 
                    } else {
                        return new CharacterSet(@"\P{Z}", new CharacterSet("\u0085\u0009-\u000d")); 
                    }

                case PosixCharacterClass.Upper:
                    // TODO: there are some differences (Unicode version?)
                    if (positive) {
                        return new CharacterSet(@"\p{Lu}"); 
                    } else {
                        return new CharacterSet(@"\P{Lu}"); 
                    }

                case PosixCharacterClass.XDigit:
                    if (positive) {
                        return new CharacterSet("a-fA-F0-9"); 
                    } else {
                        return new CharacterSet(true, "a-fA-F0-9");
                    }

                case PosixCharacterClass.Word:
                    if (positive) {
                        return new CharacterSet("\\p{L}\\p{M}\\p{Nd}\\p{Nl}\\p{Pc}\u200c\u200d");
                    } else {
                        return new CharacterSet(@"\P{L}", new CharacterSet("\\p{M}\\p{Nd}\\p{Nl}\\p{Pc}\u200c\u200d"));
                    }
            }

            throw Assert.Unreachable;
        }

        #endregion
    }
}
