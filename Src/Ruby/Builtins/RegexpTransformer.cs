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

        internal static string Transform(string/*!*/ rubyPattern, RubyRegexOptions options, out bool hasGAnchor) {
            // TODO: surrogates (REXML uses this pattern)
            if (rubyPattern == "^[\t\n\r -\uD7FF\uE000-\uFFFD\uD800\uDC00-\uDBFF\uDFFF]*$") {
                hasGAnchor = false;
                return "^(?:[\t\n\r -\uD7FF\uE000-\uFFFD]|[\uD800-\uDBFF][\uDC00-\uDFFF])*$";
            }
            
            RegexpTransformer transformer = new RegexpTransformer(rubyPattern);
            var result = transformer.Transform();
            hasGAnchor = transformer._hasGAnchor;
            return result;
        }

        private RegexpTransformer(string/*!*/ rubyPattern) {
            _rubyPattern = rubyPattern;
            _hasNamedGroup = HasNamedGroup(rubyPattern);
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
            _sb = new StringBuilder(_rubyPattern.Length);
            Parse(false);
            var result = _sb.ToString();
            _sb = null;
            return result;
        }

        private void Parse(bool isSubexpression) {
            int lastEntityIndex = 0;
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
                        ParseEscape();
                        break;

                    case '?':
                    case '*':
                    case '+':
                        if (lastWasQuantifier) {
                            // a*** == (?:(?:a*)*)*
                            _sb.Insert(lastEntityIndex, "(?:");
                            Append(')');
                        }
                        Append((char)c);
                        // A quantifier that got wrapped is already a group, so a further
                        // quantifier can apply to it directly.
                        lastWasQuantifier = !ParsePostQuantifier(lastEntityIndex, true, false);
                        break;

                    case '{': {
                        bool isExactCount;
                        if (ParseConstrainedQuantifier(lastWasQuantifier, lastEntityIndex, out isExactCount)) {
                            lastWasQuantifier = !ParsePostQuantifier(lastEntityIndex, false, isExactCount);
                        } else {
                            goto default;
                        }
                        break;
                    }

                    case '(':
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        ParseGroup();
                        break;

                    case ')':
                        if (isSubexpression) {
                            return;
                        } else {
                            throw MakeError("unmatched close parenthesis");
                        }
                    
                    case '[':
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        ParseCharacterGroup(false).AppendTo(_sb, true);
                        break;

                    case '|':
                        Append('|');
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        break;

                    default:
                        lastEntityIndex = _sb.Length;
                        lastWasQuantifier = false;
                        Append((char)c);
                        break;
                }
            }
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

                Append('(');
                Append('?');

                switch (c) {
                    case '-':
                    case 'i':
                    case 'm':
                    case 'x':
                        while (true) {
                            if (c == 'm') {
                                // Map (?m) to (?s) ie. RegexOptions.SingleLine
                                Append('s');
                            } else if (c == 'i' || c == 'x' || c == '-') {
                                Append((char)c);
                            } else if (c == ':') {
                                Append(':');
                                break;
                            } else if (c == ')' || c == -1) {
                                Back();
                                break;
                            } else {
                                throw MakeError("undefined group option");
                            }
                            c = Read();
                        }
                        break;

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
                        Append('<');
                        c = Read();
                        if (c == '=' || c == '!') {
                            // positive/negative lookbehind assertion
                            Append((char)c);
                        } else {
                            _groupCount++;
                            ParseGroupName(c, '>');
                        }
                        break;

                    case '\'':
                        Append('\'');
                        _groupCount++;
                        ParseGroupName(Read(), '\'');
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
            } else {
                _groupCount++;
                Append('(');
            }
            Parse(true);
            Append(')');
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
            Parse(true);
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

        private void ParseGroupName(int c, int terminator) {
            if (c == terminator || c == -1) {
                throw MakeError("group name is empty");
            }
            while (true) {
                Append((char)c); 
                c = Read();
                if (c == terminator || c == ')') {
                    Append((char)c);
                    break;
                } else if (c == -1) {
                    throw MakeError("unterminated group name");
                }
            }
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
                    // TODO: this can be implemented by copying the pattern with removed groups
                    throw MakeError("\\g not supported");
                
                case 'k':
                    ParseBackreference();
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
                _sb.Append('\\').Append(value);
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
            _sb.Append('\\').Append(value);
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
                _sb.Append("\\k<").Append(number).Append('>');
                return;
            }

            if (IsLevelSpecifier(name)) {
                // \k<name+n> and \k<name-n> are level-scoped references, which .NET has no
                // equivalent for; Ruby itself rejects them outside a subexpression call.
                throw MakeError("invalid group name <" + name + ">");
            }

            _sb.Append("\\k<").Append(name).Append('>');
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

                case 's':
                    return new CharacterSet(@"\s");

                case 'S':
                    return new CharacterSet(@"\S");

                case 'd':
                    return new CharacterSet(@"\d");

                case 'D':
                    return new CharacterSet(@"\D");

                case 'w':
                    return new CharacterSet(@"\w");

                case 'W':
                    return new CharacterSet(@"\W");

                default:
                    // ignore backslash unless needed
                    return new CharacterSet(Escape(escape), true);
            }
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
                if (HasAstral) {
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
                    JoinAstral(_astral, set._astral)
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
            
            string name = _rubyPattern.Substring(start, _index - start);
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
