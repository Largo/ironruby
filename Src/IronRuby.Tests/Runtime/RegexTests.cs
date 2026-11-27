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
using System.Text.RegularExpressions;
using IronRuby.Builtins;

namespace IronRuby.Tests {
    public partial class Tests {
        public void Regex1() {
            AssertOutput(delegate() {
                CompilerTest(@"
r = /foo/imx
puts r.to_s
puts r.inspect

puts s = /xx#{r}xx#{r}xx/i.to_s
puts t = /yy#{s}yy/.to_s
");
            }, @"
(?mix:foo)
/foo/mix
(?i-mx:xx(?mix:foo)xx(?mix:foo)xx)
(?-mix:yy(?i-mx:xx(?mix:foo)xx(?mix:foo)xx)yy)
");
        }

        public void Regex2() {
            TestOutput(@"
puts(/#{/a/}/)
puts(/#{nil}#{/a/}#{nil}/)
puts(/#{/a/}b/)
puts(/b#{/a/}/)
", @"
(?-mix:a)
(?-mix:a)
(?-mix:(?-mix:a)b)
(?-mix:b(?-mix:a))
");

            // A MatchData keeps the subject as it was when it matched: a later change to the
            // string must not show through #pre_match, #[] or #string. Regexp#match,
            // String#match and the $~ String#scan leaves behind used to share the live string.
            TestOutput(@"
def show(md) p [md.pre_match, md[0], md[1], md.post_match, md.string, md.string.frozen?] end
s = 'hello world'.dup; md = /o (w)/.match(s); s.replace('ZZZZZZZZZZZZZZZZ'); show md
s = 'hello world'.dup; md = s.match(/o (w)/); s.replace('ZZZZZZZZZZZZZZZZ'); show md
s = 'hello world'.dup; md = s.match(/o(r)/, 5); s[0, 1] = 'J'; show md
s = 'hello world'.dup; s.scan(/o(.)/); md = $~; s << '!!'; s[0] = 'J'; show md
s = 'hello world'.dup; s =~ /o (w)/; md = $~; s.insert(0, 'AAA'); show md
s = ""h\u00e9llo w\u00f6rld"".dup; md = /\u00f6(r)/.match(s); s.upcase!; p [md.begin(0), md.pre_match.size, md[1], md.post_match, md.string.ascii_only?, md.string.frozen?]
s = 'hello world'.dup; mds = []; s.scan(/o(.)/) { mds << $~ }; s.clear; mds.each { |m| show m }
", @"
[""hell"", ""o w"", ""w"", ""orld"", ""hello world"", true]
[""hell"", ""o w"", ""w"", ""orld"", ""hello world"", true]
[""hello w"", ""or"", ""r"", ""ld"", ""hello world"", true]
[""hello w"", ""or"", ""r"", ""ld"", ""hello world"", true]
[""hell"", ""o w"", ""w"", ""orld"", ""hello world"", true]
[7, 7, ""r"", ""ld"", false, true]
[""hell"", ""o "", "" "", ""world"", ""hello world"", true]
[""hello w"", ""or"", ""r"", ""ld"", ""hello world"", true]
");
        }

        [Options(NoRuntime = true)]
        public void RegexTransform1() {
            TestCorrectPatternTranslation(@"", @"");

            // escapes
            TestCorrectPatternTranslation(@"\\", @"\\");
            TestCorrectPatternTranslation(@"\_", @"_");
            // An octal escape is decoded to the character it denotes, the same way \x is.
            TestCorrectPatternTranslation(@"abc\0\01\011", "abc\u0000\u0001\\\t");
            TestCorrectPatternTranslation(@"\n\t\r\f\v\a\e\b\A\B\Z\z", "\\\n\\\t\\\r\\\f\\\v\a\u001B\\b\\A\\B\\Z\\z");
            TestCorrectPatternTranslation(@"[\n\t\r\f\v\a\e\b\A\B\Z\z]", "[\\\n\\\t\\\r\\\f\\\v\a\u001B\bABZz]");
            TestCorrectPatternTranslation(@"\G", RubyRegexOptions.NONE, @"\G", true);
            
            // meta-characters
            TestCorrectPatternTranslation(@"\xd", "\\\u000d");
            TestCorrectPatternTranslation(@"\xdz", "\\\u000dz");
            TestCorrectPatternTranslation(@"\*", @"\*");
            TestCorrectPatternTranslation(@"\[", @"\[");
            TestCorrectPatternTranslation(@"\#", @"\#");
            TestCorrectPatternTranslation(@"\0", "\u0000");
            TestCorrectPatternTranslation(@"\x09\x0a\x0d\x20\u0009\u000a\u000d\u0020\u{9 a d 20}",
                                           "\\\u0009\\\u000a\\\u000d\\\u0020\\\u0009\\\u000a\\\u000d\\\u0020\\\u0009\\\u000a\\\u000d\\\u0020");
            TestCorrectPatternTranslation(@"[a\-z]", @"[a\-z]");

            // Unicode escapes
            TestCorrectPatternTranslation(@"\u002a", @"\*");
            TestCorrectPatternTranslation(@"\u005b", @"\[");
            TestCorrectPatternTranslation(@"\u{005b}", @"\[");
            TestCorrectPatternTranslation(@"\u{5b 2a}", @"\[\*");

            // POSIX classes and properties are spelled out from Onigmo's own tables (see
            // UnicodeProperties), so these check what the translation matches rather than its
            // text; every expectation was checked against CRuby 4.0.6. A class with members
            // outside the BMP gets surrogate-pair alternatives, which the non-BMP characters here
            // exercise.
            TestClassTranslation(@"[x[:alnum:]y]", "xyaZ5\u00e9\u0663\U0001D400", "-_ \U0001F600");
            TestClassTranslation(@"[a[^:alnum:]b]", "abxZ5-\U0001F600", ":lnum");
            TestClassTranslation(@"[[:alpha:]]", "aZ\u00e9\u03b1\u6f22\U00020000", "5_- \U0001F600");
            TestClassTranslation(@"[[:^alpha:]]", "5_- \U0001F600", "aZ\u00e9\u6f22\U00020000");
            TestClassTranslation(@"[[:ascii:]]", "\u0000A\u007f", "\u0080\u00e9\U0001F600");
            TestClassTranslation(@"[[:^ascii:]]", "\u0080\u00e9\U0001F600", "\u0000A\u007f");
            TestClassTranslation(@"[[:blank:]]", " \u0009\u3000", "\u000aa");
            TestClassTranslation(@"[[:cntrl:]]", "\u0000\u001f\u007f\u0085", "a \u00a0");
            TestClassTranslation(@"[[:digit:]]", "09\u0663\uff10\U0001D7CE", "a\u00b2\u00bd");
            TestClassTranslation(@"[[:^digit:]]", "a\u00b2\U0001F600", "0\u0663\U0001D7CE");
            TestClassTranslation(@"[[:lower:]]", "a\u00df\u03b1\U00010428", "A1\U00010400");
            TestClassTranslation(@"[[:punct:]]", "!$+<^`|~\u00bf", "a1 ");
            TestClassTranslation(@"[[:space:]]", " \u0009\u000a\u000b\u000c\u000d\u0085\u00a0\u2028\u3000", "a\u200b");
            TestClassTranslation(@"[[:upper:]]", "A\u03a9\U00010400", "a1\U00010428");
            TestClassTranslation(@"[[:xdigit:]]", "09afAF", "gG\uff10");
            TestClassTranslation(@"[[:word:]]", "a_1\u00e9\u0301\u200c\U0001D400", "-! \U0001F600");
            TestClassTranslation(@"(?a)[[:alpha:]]", "aZ", "\u00e9\u03b1\U00020000");
            TestClassTranslation(@"(?a)[[:^alpha:]]", "1\u00e9\u3042\U00020000", "aZ");
            TestClassTranslation(@"\p{L}", "a\u00e9\u6f22\U0001D400", "1_ ");
            TestClassTranslation(@"\P{L}", "1_ \U0001F600", "a\u00e9\u6f22\U0001D400");
            TestClassTranslation(@"\p{^L}", "1_ \U0001F600", "a\u00e9\u6f22\U0001D400");
            TestClassTranslation(@"\P{^L}", "a\u00e9\u6f22\U0001D400", "1_ ");
            TestClassTranslation(@"\p{Alnum}", "a1\u0663\U0001D400", "-_\U0001F600");
            TestClassTranslation(@"[a\p{Alnum}b]", "ab1\U0001D400", "-_\U0001F600");
            TestClassTranslation(@"[^\p{Alnum}]", "-_ \U0001F600", "a1\U0001D400");
            TestClassTranslation(@"[\P{Alnum}]", "-_ \U0001F600", "a1\U0001D400");
            TestClassTranslation(@"[\p{Alnum}-]", "a1-", "_ ");
            TestClassTranslation(@"\p{Punct}", "!_\u00bf", "$+a");
            TestClassTranslation(@"\p{Word}", "a_\u0301\U0001D400", "-\U0001F600");
            TestClassTranslation(@"\p{XDigit}", "0aF", "g\uff10");
            TestClassTranslation(@"\p{Emoji}", "#*09\u00a9\u263a\U0001F600", "a \u00e9");
            TestClassTranslation(@"\P{Emoji}", "a\u00e9\U00010400", "#0\u263a\U0001F600");
            TestClassTranslation(@"[\p{Emoji}&&[^\d#*]]", "\u00a9\u263a\U0001F600\U0001F44D", "#*09a");
            TestClassTranslation(@"[^\p{Emoji}&&[^\d#*]]", "#*0a\U00010400", "\u00a9\u263a\U0001F600");
            TestClassTranslation(@"[\p{Emoji}&&[^\u{1f600}-\u{1f64f}]]", "\u263a\U0001F44D1", "\U0001F600\U0001F64Fa");
            TestClassTranslation(@"\p{Emoji_Presentation}", "\u231a\U0001F600", "\u263a1");
            TestClassTranslation(@"\p{Extended_Pictographic}", "\u00a9\u263a\U0001F600\U0001FFFD", "1#");
            TestClassTranslation(@"\p{Han}", "\u6f22\u3005\U00020000", "a\u3042");
            TestClassTranslation(@"\p{Hiragana}", "\u3042\u309d", "\u30a2\u309b");
            TestClassTranslation(@"\p{Greek}", "\u03b1\u1f00\U00010140", "a\u03e2");
            TestClassTranslation(@"\p{In_Greek_and_Coptic}", "\u03b1\u03e2", "\u1f00a");
            TestClassTranslation(@"\p{Latin}", "a\u00e9\u1e00\uff21", "\u03b11");
            TestClassTranslation(@"\p{Any}", "\u0000a\uffff\U0001F600\U0010FFFF", "");
            TestClassTranslation(@"\p{Assigned}", "a\U0001F600", "\u0378\U0010FFFF");
            TestClassTranslation(@"\p{Age=6.0}", "a\U0001F601", "\U0001F600\U0001F97A");
            TestClassTranslation(@"\p{alpha}", "a\u00e9", "1");
            TestClassTranslation(@"\p{ ALPHA }", "a\u00e9", "1");
            TestClassTranslation(@"\p{Lc}", "aA\u01c5", "\u02b01");
            TestClassTranslation(@"\p{Cs}", "", "a\U0001F600");

            // the rendering itself, for small sets
            TestCorrectPatternTranslation(@"[[:xdigit:]]", "[0-9A-Fa-f]");
            TestCorrectPatternTranslation(@"\p{XDigit}", "[0-9A-Fa-f]");
            TestCorrectPatternTranslation(@"[[:^xdigit:]]", "(?:[\0-\uffff-[0-9A-Fa-f\\ud800-\\udfff]]|[\\ud800-\\udbff][\\udc00-\\udfff])");
            TestCorrectPatternTranslation(@"\p{Emoji_Modifier}", "(?:\\ud83c[\\udffb-\\udfff])");
            // a * or + loop over a class holding every non-BMP character stays a class loop,
            // and may not stop between the halves of a surrogate pair
            TestCorrectPatternTranslation(@"\P{XDigit}*", "[\0-\uffff-[0-9A-Fa-f]]*(?<![\\ud800-\\udbff])");
            TestCorrectPatternTranslation(@"[^a]+?", "[\0-\uffff-[a]]+?(?<![\\ud800-\\udbff])");
       
            // possessive quantifiers
            TestCorrectPatternTranslation(@"xyza*+", @"xyz(?>a*)");
            TestCorrectPatternTranslation(@"x[a-b]*+", @"x(?>[a-b]*)");
            TestCorrectPatternTranslation(@"x[a-b]*+*+", @"x(?>(?>[a-b]*)*)");
            TestCorrectPatternTranslation(@"x[a-b]{1,2}+", @"x(?:[a-b]{1,2})+");
            TestCorrectPatternTranslation(@"x{1,2,*+", @"x{1,2(?>,*)");
            TestCorrectPatternTranslation(@"x{1,2*+", @"x{1,(?>2*)");
            TestCorrectPatternTranslation(@"x{1,*+", @"x{1(?>,*)");
            TestCorrectPatternTranslation(@"x{,*+", @"x{(?>,*)");
            TestCorrectPatternTranslation(@"x{1*+", @"x{(?>1*)");
            TestCorrectPatternTranslation(@"x{*+", @"x(?>{*)");

            // ranges
            TestCorrectPatternTranslation("[a-z]", "[a-z]");
            TestCorrectPatternTranslation(@"[\u{40}-z]", "[\u0040-z]");
            TestCorrectPatternTranslation(@"[\u{40}-\u{60}]", "[\u0040-\u0060]");
            TestCorrectPatternTranslation(@"[\x40-\x60]", "[\u0040-\u0060]");
            TestCorrectPatternTranslation(@"[\001-\7]", "[\u0001-\u0007]");
            TestCorrectPatternTranslation(@"[\u{2a}-\u{2b}]", "[\\*-\\+]");
            TestCorrectPatternTranslation(@"[x-]", @"[x\-]");
            TestCorrectPatternTranslation(@"[---]", @"[\u002d-\u002d]");
            TestCorrectPatternTranslation(@"[\u{1 2 40}-\u{60 3 4}]", "[\u0001\u0002\u0040-\u0060\u0003\u0004]");
            TestCorrectPatternTranslation(@"[\u{1 2 40}-\u0060]", "[\u0001\u0002\u0040-\u0060]");
            TestCorrectPatternTranslation(@"[\u{1 2 40}-z]", "[\u0001\u0002\u0040-z]");
            TestCorrectPatternTranslation(@"[\x3f-\u{40 1 2}]", "[\\\u003f-\u0040\u0001\u0002]");
            // \w is ASCII only in Ruby unless (?u) is in effect, so it is expanded rather than
            // handed to .NET, whose \w is Unicode aware. Checked against CRuby 4.0.6:
            //   /[\w-]+/.match("a-\u3042")  =>  "a-"
            TestCorrectPatternTranslation(@"[\w-]", @"[a-zA-Z0-9_\-]");

            // character set operations
            TestCorrectPatternTranslation("[a-z&&d-e]", "[de]");
            TestCorrectPatternTranslation("[a-z&&[d-e&&e-f]]", "[e]");
            TestCorrectPatternTranslation("[a-z&&^[b[^c]]]", "[abd-z]");
            TestCorrectPatternTranslation("[a-z&&[^b[^c]]]", "[c]");
            TestCorrectPatternTranslation("[[^a-z][e-f][^b-q]]", "(?:[\0-\uffff-[b-dg-q\\ud800-\\udfff]]|[\\ud800-\\udbff][\\udc00-\\udfff])");
            TestCorrectPatternTranslation("[&&d-e]", "[a-[a]]");
            TestCorrectPatternTranslation("[a-z&&[d-e&&e-f]x&&^[b[^c]]]", "[ex]");
            TestCorrectPatternTranslation("[^[a-b][c-d][^e-f]&&[a-z&&[^d-e]]]", "(?:[\0-\uffff-[a-cg-z\\ud800-\\udfff]]|[\\ud800-\\udbff][\\udc00-\\udfff])");

            // groups
            TestCorrectPatternTranslation("((((a))))", "((((a))))");
            TestCorrectPatternTranslation("(?<name>a)", "(?<name>a)");
            TestCorrectPatternTranslation("(?:a)", "(?:a)");
            TestCorrectPatternTranslation("(?:a)", "(?:a)");
            TestCorrectPatternTranslation("(?mix-mix)", "(?six-six)");
            TestCorrectPatternTranslation("(?mix-mix:)", "(?six-six:)");
            TestCorrectPatternTranslation("(?mix-mix:a)", "(?six-six:a)");
            TestCorrectPatternTranslation("(?-mix:a)", "(?-six:a)");
            TestCorrectPatternTranslation("(?m:a)", "(?s:a)");
            TestCorrectPatternTranslation("(?mi:a)", "(?si:a)");
            TestCorrectPatternTranslation("(?m)", "(?s)");
            // In Ruby "name1-name2" is just a group name (CRuby: names => ["name2", "name1-name2"]),
            // not a .NET balancing group, so it is given a .NET-safe name.
            TestCorrectPatternTranslation("(?<name2>)(?<name1-name2>a)", "(?<name2>)(?<__irnname1_002dname2>a)");
            TestCorrectPatternTranslation("(?'name2')(?'name1-name2'a)", "(?'name2')(?'__irnname1_002dname2'a)");
            TestCorrectPatternTranslation("(?=)", "(?=)");
            TestCorrectPatternTranslation("(?=x)", "(?=x)");
            TestCorrectPatternTranslation("(?<=)", "(?<=)");
            TestCorrectPatternTranslation("(?<=x)", "(?<=x)");
            TestCorrectPatternTranslation("(?<!)", "(?<!)");
            TestCorrectPatternTranslation("(?<!x)", "(?<!x)");
            TestCorrectPatternTranslation("(?>)", "(?>)");
            TestCorrectPatternTranslation("(?>x)", "(?>x)");
            TestCorrectPatternTranslation("(?>(?=(?<!f)(o)(o))(?<bar>))", "(?>(?=(?<!f)(?:o)(?:o))(?<bar>))");
            
            // backreferences:
            // A numbered backreference is invalid once the pattern declares a named group, so
            // the two forms cannot appear together. Both halves checked against CRuby 4.0.6:
            //   Regexp.new("(x) (?'name') \\k<1>")  =>  numbered backref/call is not allowed. (use name)
            TestCorrectPatternTranslation(@"(x) (y) \k<1> \k'2'", @"(x) (y) \k<1> \k<2>");
            // once a pattern has a named group its plain groups do not capture (Onigmo)
            TestCorrectPatternTranslation(@"(x) (?'name') \k<name> \k'name'", @"(?:x) (?'name') \k<name> \k<name>");
            // a backreference inside the group it refers to never matches (Onigmo resets the group)
            TestCorrectPatternTranslation(@"(a\1?){2}", @"(a(?!)?){2}");

            // subexpression calls: the called group is copied under its own number, nested groups too
            TestCorrectPatternTranslation(@"((a)b)\g<1>", @"((a)b)(?<1>(?<2>a)b)");

            // error: TestCorrectPatternTranslation("(?<a)b>c)", "(?<a)b>c)");
        }

        // Translates a class and checks, with the .NET regex, that it matches each character of
        // members as a whole and none of those of nonMembers (a surrogate pair is one character).
        private void TestClassTranslation(string/*!*/ pattern, string/*!*/ members, string/*!*/ nonMembers) {
            bool hasGAnchor;
            string actual = RegexpTransformer.Transform(pattern, RubyRegexOptions.NONE, out hasGAnchor);
            var regex = new Regex(@"\A(?:" + actual + @")\z", RubyRegex.ToClrOptions(RubyRegexOptions.NONE));
            for (int i = 0; i < members.Length; i += Char.IsHighSurrogate(members[i]) ? 2 : 1) {
                string c = members.Substring(i, Char.IsHighSurrogate(members[i]) ? 2 : 1);
                Assert(regex.IsMatch(c), pattern + " should match U+" + Char.ConvertToUtf32(c, 0).ToString("X4"));
            }
            for (int i = 0; i < nonMembers.Length; i += Char.IsHighSurrogate(nonMembers[i]) ? 2 : 1) {
                string c = nonMembers.Substring(i, Char.IsHighSurrogate(nonMembers[i]) ? 2 : 1);
                Assert(!regex.IsMatch(c), pattern + " should not match U+" + Char.ConvertToUtf32(c, 0).ToString("X4"));
            }
            Assert(!hasGAnchor);
        }

        //[DebuggerHidden]
        private void TestCorrectPatternTranslation(string/*!*/ pattern, string/*!*/ expected) {
            TestCorrectPatternTranslation(pattern, RubyRegexOptions.NONE, expected, false);
        }

        //[DebuggerHidden]
        private void TestCorrectPatternTranslation(string/*!*/ pattern, RubyRegexOptions options, string/*!*/ expected, bool expectedGAnchor) {
            bool hasGAnchor;
            string actual = RegexpTransformer.Transform(pattern, options, out hasGAnchor);
            // a class with members outside the BMP gets a surrogate-pair alternative after it (see
            // TestClassTranslation); the BMP part is what these expectations spell out
            if (actual != expected && actual.StartsWith("(?:" + expected + "|", StringComparison.Ordinal) && actual.EndsWith(")", StringComparison.Ordinal)) {
                expected = actual;
            }
            AreEqual(expected, actual);
            new Regex(expected);
            Assert(hasGAnchor == expectedGAnchor);
        }

        [Options(NoRuntime = true)]
        public void RegexTransform2() {
            string p = @"^
        ([a-zA-Z][-+.a-zA-Z\d]*):                     (?# 1: scheme)
        (?:
           ((?:[-_.!~*'()a-zA-Z\d;?:@&=+$,]|%[a-fA-F\d]{2})(?:[-_.!~*'()a-zA-Z\d;/?:@&=+$,\[\]]|%[a-fA-F\d]{2})*)              (?# 2: opaque)
        |
           (?:(?:
             //(?:
                 (?:(?:((?:[-_.!~*'()a-zA-Z\d;:&=+$,]|%[a-fA-F\d]{2})*)@)?  (?# 3: userinfo)
                   (?:((?:(?:(?:[a-zA-Z\d](?:[-a-zA-Z\d]*[a-zA-Z\d])?)\.)*(?:[a-zA-Z](?:[-a-zA-Z\d]*[a-zA-Z\d])?)\.?|\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}|\[(?:(?:[a-fA-F\d]{1,4}:)*(?:[a-fA-F\d]{1,4}|\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})|(?:(?:[a-fA-F\d]{1,4}:)*[a-fA-F\d]{1,4})?::(?:(?:[a-fA-F\d]{1,4}:)*(?:[a-fA-F\d]{1,4}|\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}))?)\]))(?::(\d*))?))?(?# 4: host, 5: port)
               |
                 ((?:[-_.!~*'()a-zA-Z\d$,;+@&=+]|%[a-fA-F\d]{2})+)           (?# 6: registry)
               )
             |
             (?!//))                              (?# XXX: '//' is the mark for hostport)
             (/(?:[-_.!~*'()a-zA-Z\d:@&=+$,]|%[a-fA-F\d]{2})*(?:;(?:[-_.!~*'()a-zA-Z\d:@&=+$,]|%[a-fA-F\d]{2})*)*(?:/(?:[-_.!~*'()a-zA-Z\d:@&=+$,]|%[a-fA-F\d]{2})*(?:;(?:[-_.!~*'()a-zA-Z\d:@&=+$,]|%[a-fA-F\d]{2})*)*)*)?              (?# 7: path)
           )(?:\?((?:[-_.!~*'()a-zA-Z\d;/?:@&=+$,\[\]]|%[a-fA-F\d]{2})*))?           (?# 8: query)
        )
        (?:\#((?:[-_.!~*'()a-zA-Z\d;/?:@&=+$,\[\]]|%[a-fA-F\d]{2})*))?            (?# 9: fragment)
      $";

            // ^ is expanded: .NET's Multiline ^ also matches the empty line it considers a
            // trailing \n to open, which Ruby has no equivalent of.
            string e = @"(?:\A|(?<=\n)(?!\z))
        ([a-zA-Z][\-+.a-zA-Z0-9]*):                     
        (?:
           ((?:[\-_.!~*'()a-zA-Z0-9;?:@&=+$,]|%[a-fA-F0-9]{2})(?:[\-_.!~*'()a-zA-Z0-9;/?:@&=+$,\[\]]|%[a-fA-F0-9]{2})*)              
        |
           (?:(?:
             //(?:
                 (?:(?:((?:[\-_.!~*'()a-zA-Z0-9;:&=+$,]|%[a-fA-F0-9]{2})*)@)?  
                   (?:((?:(?:(?:[a-zA-Z0-9](?:[\-a-zA-Z0-9]*[a-zA-Z0-9])?)\.)*(?:[a-zA-Z](?:[\-a-zA-Z0-9]*[a-zA-Z0-9])?)\.?|[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}|\[(?:(?:[a-fA-F0-9]{1,4}:)*(?:[a-fA-F0-9]{1,4}|[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})|(?:(?:[a-fA-F0-9]{1,4}:)*[a-fA-F0-9]{1,4})?::(?:(?:[a-fA-F0-9]{1,4}:)*(?:[a-fA-F0-9]{1,4}|[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}))?)\]))(?::([0-9]*))?))?
               |
                 ((?:[\-_.!~*'()a-zA-Z0-9$,;+@&=+]|%[a-fA-F0-9]{2})+)           
               )
             |
             (?!//))                              
             (/(?:[\-_.!~*'()a-zA-Z0-9:@&=+$,]|%[a-fA-F0-9]{2})*(?:;(?:[\-_.!~*'()a-zA-Z0-9:@&=+$,]|%[a-fA-F0-9]{2})*)*(?:/(?:[\-_.!~*'()a-zA-Z0-9:@&=+$,]|%[a-fA-F0-9]{2})*(?:;(?:[\-_.!~*'()a-zA-Z0-9:@&=+$,]|%[a-fA-F0-9]{2})*)*)*)?              
           )(?:\?((?:[\-_.!~*'()a-zA-Z0-9;/?:@&=+$,\[\]]|%[a-fA-F0-9]{2})*))?           
        )
        (?:\#((?:[\-_.!~*'()a-zA-Z0-9;/?:@&=+$,\[\]]|%[a-fA-F0-9]{2})*))?            
      $";
            
            bool hasGAnchor;
            string t = RegexpTransformer.Transform(p, RubyRegexOptions.Extended | RubyRegexOptions.Multiline, out hasGAnchor);
            Assert(e == t);
            Assert(!hasGAnchor);
            new Regex(t);
        }

        public void RegexEscape1() {
            string[] patterns = new string[] {
                @"", 
                @"",

                @"\", 
                @"\\",

                @"(*)", 
                @"\(\*\)",

                "$_^_|_[_]_(_)_\\_._#_-_{_}_*_+_?_\t_\n_\r_\f_\v_\a_\b",
                @"\$_\^_\|_\[_\]_\(_\)_\\_\._\#_\-_\{_\}_\*_\+_\?_\t_\n_\r_\f_\v_" + "\a_\b"
            };

            for (int i = 0; i < patterns.Length; i += 2) {
                string expected = patterns[i + 1];
                string actual = RubyRegex.Escape(patterns[i]);
                Assert(actual == expected);
            }
        }

        public void RegexCondition1() {
            AssertOutput(delegate() {
                CompilerTest(@"
$_ = 'foo'
if /(foo)/ then
  puts $1
end
", 1, 0);
            }, @"
foo
");
        }

        public void RegexCondition2() {
            AssertOutput(delegate() {
                CompilerTest(@"
z = /foo/
puts(z =~ 'xxxfoo')

class Regexp
  def =~ a
    '=~'    
  end
end

z = /foo/
puts(z =~ 'foo')

puts(/foo/ =~ 'xxxfoo')     # a literal calls a redefined Regexp#=~ too (MRI 1.9+)
");
            }, @"
3
=~
=~
");
        }

        /// <summary>
        /// Byte escapes in a regexp of a multibyte encoding spell characters of it (/\xa4\xa2/e is one
        /// character), and every match operation answers character offsets: the regex runs on the
        /// string's own characters rather than on a k-coded decoding of its bytes.
        /// </summary>
        public void Regex_ByteEscapes1() {
            TestOutput(@"
s = ""あaいbあ"".encode(""EUC-JP"")
re = Regexp.new('\xa4\xa2'.force_encoding(""EUC-JP""))
p s =~ re, s.index(re, 1), s.rindex(re), re.encoding
m = s.match(re, 1)
p [m.begin(0), m.end(0), m.byteoffset(0), m.pre_match.size, $~.begin(0)]
p s.scan(re).size, s.gsub(re, ""x"").encode(""UTF-8""), s.index(/b/e, 2), s =~ /\xa4\xa4/e
w = ""あaいb"".encode(""Windows-31J"")
p w =~ /\x82\xa2/s, w.split(/\x82\xa2/s).map(&:bytesize)
p ""ああ"".encode(""EUC-JP"") =~ Regexp.new('\xa4\xa2*'.force_encoding(""EUC-JP"")), $~[0].bytesize
p ""あaいb"" =~ /\xe3\x81\x84/, ""あaいb"".match(/b/u).byteoffset(0), ""あaいb"".match(/[\xe3\x81\x82-\xe3\x81\x84]+/)[0].size
p((Regexp.new('\xe3\x81') rescue $!), (Regexp.new('\xff', Regexp::IGNORECASE) rescue $!))
require 'strscan'
ss = StringScanner.new(s)
p ss.scan(re).bytesize, ss.scan(/a/e), ss.scan(/./e).bytesize, ss.pos, ss.charpos
p((""abc"".encode(""UTF-16LE"") =~ /b/ rescue $!), (""abc"".encode(""UTF-16LE"") =~ /b/n rescue $!))
p((""\xff"".force_encoding(""BINARY"").sub(""é"", """") rescue $!), (""abc"".encode(""UTF-16LE"").sub(""b"", """") rescue $!))
", @"
0
4
4
#<Encoding:EUC-JP>
[4, 5, [6, 8], 4, 4]
2
""xaいbx""
3
2
2
[3, 1]
0
4
2
[7, 8]
1
#<RegexpError: too short escaped multibyte character: /\xe3\x81/>
#<RegexpError: invalid multibyte escape: /\xff/i>
2
""a""
2
5
3
#<Encoding::CompatibilityError: incompatible encoding regexp match (US-ASCII regexp with UTF-16LE string)>
#<Encoding::CompatibilityError: incompatible encoding regexp match (US-ASCII regexp with UTF-16LE string)>
#<Encoding::CompatibilityError: incompatible character encodings: BINARY (ASCII-8BIT) and UTF-8>
#<Encoding::CompatibilityError: incompatible character encodings: UTF-16LE and UTF-8>
");
        }

        /// <summary>
        /// Onigmo reads no escape inside a comment, so a byte escape that spells no character there
        /// is no error: (?#...), and under /x, or (?x) for the rest of a group, # to the end of the line.
        /// </summary>
        public void Regex_ByteEscapeComments1() {
            TestOutput(@"
p Regexp.new(""a(?#\\xff)b"") =~ ""ab"", Regexp.new(""a # \\xff\nb"", Regexp::EXTENDED) =~ ""ab""
p Regexp.new(""(?x)a # \\xff\nb"") =~ ""ab"", Regexp.new(""(?x:a # \\xff\n)b"") =~ ""ab"", Regexp.new(""a(?#\\)\\xff)b"") =~ ""ab""
p /a#\xff/x =~ ""a"", /a(?#\xff)/ =~ ""a"", Regexp.new(""a # caf\\xc3\\xa9\n\\xc3\\xa9"", Regexp::EXTENDED) =~ ""aé""
p((Regexp.new(""(?x:a)# \\xff"") rescue $!), (Regexp.new(""[#\\xff]"", Regexp::EXTENDED) rescue $!))
", @"
0
0
0
0
0
0
0
0
#<RegexpError: invalid multibyte escape: /(?x:a)# \xff/>
#<RegexpError: invalid multibyte escape: /[#\xff]/x>
");
        }

        
        /// <summary>
        /// $~ after a String#sub or #sub! with a block is what the block left it: a match made inside it, nil after
        /// a failed one (#137). rb_str_sub_bang does not set it again after the block; IronRuby reset it to the
        /// sub's own match. #gsub and #gsub! do reset it to their last match, and #scan leaves the block's. The
        /// expected output is CRuby 4.0's.
        /// </summary>
        public void Regex_BackrefAfterBlockSub1() {
            TestOutput(@"
def a; r = ""ab"".sub(/(.)/) { ""x"".match(/(.)/); ""z"" }; puts ""sub blk match: #{$~&.[](0).inspect} #{r.inspect}""; end
def b; r = ""ab"".sub(/(.)/) { ""z"" }; puts ""sub blk none: #{$~&.[](0).inspect} #{r.inspect}""; end
def c; r = ""ab"".sub(/(.)/) { ""x"".match(/q/); ""z"" }; puts ""sub blk failed: #{$~&.[](0).inspect} #{r.inspect}""; end
def d; s = +""ab""; r = s.sub!(/(.)/) { ""x"".match(/(.)/); ""z"" }; puts ""sub! blk match: #{$~&.[](0).inspect} #{r.inspect}""; end
def e; r = ""ab"".gsub(/(.)/) { ""x"".match(/(.)/); ""z"" }; puts ""gsub blk match: #{$~&.[](0).inspect} #{r.inspect}""; end
def f; r = ""ab"".gsub(/(.)/) { $1.upcase }; puts ""gsub blk none: #{$~&.[](0).inspect} #{r.inspect}""; end
def g; r = ""ab"".gsub(/(.)/) { ""x"".match(/q/); ""z"" }; puts ""gsub blk failed: #{$~&.[](0).inspect} #{r.inspect}""; end
def h; s = +""ab""; r = s.gsub!(/(.)/) { ""y"".match(/(.)/); ""z"" }; puts ""gsub! blk match: #{$~&.[](0).inspect} #{r.inspect}""; end
def i; r = ""ab"".sub(""b"") { ""x"" =~ /x/; ""z"" }; puts ""sub str blk: #{$~&.[](0).inspect} #{r.inspect}""; end
def j; ""12"".scan(/(.)/) { ""y"".match(/(.)/) }; puts ""scan blk: #{$~&.[](0).inspect}""; end
def k; ""12"".scan(/(.)/) { ""y"".match(/(.)/); break }; puts ""scan blk break: #{$~&.[](0).inspect}""; end
def l; r = []; ""ab"".gsub(/(.)/) { r << $~[0]; ""x"".match(/./); r << $~[0]; ""z"" }; puts ""gsub inside: #{r.inspect} after #{$~&.[](0).inspect}""; end
def m; ""ab"".sub(/(.)/) { ""x"".match(/(.)/); ""z"" }; puts ""sub then $1: #{$1.inspect} #{$`.inspect}""; end
def n; ""ab"".sub(/z/) { 1 }; puts ""sub nomatch: #{$~.inspect}""; end
%i[a b c d e f g h i j k l m n].each { |x| send(x) }
", @"
sub blk match: ""x"" ""zb""
sub blk none: ""a"" ""zb""
sub blk failed: nil ""zb""
sub! blk match: ""x"" ""zb""
gsub blk match: ""b"" ""zz""
gsub blk none: ""b"" ""AB""
gsub blk failed: ""b"" ""zz""
gsub! blk match: ""b"" ""zz""
sub str blk: ""x"" ""az""
scan blk: ""2""
scan blk break: ""y""
gsub inside: [""a"", ""x"", ""b"", ""x""] after ""b""
sub then $1: ""x"" """"
sub nomatch: nil
");
        }

        /// <summary>
        /// $~.string keeps a String subclass's class and is a frozen receiver itself (rb_str_new_frozen),
        /// except after String#sub: rb_str_sub matches on str_duplicate(rb_cString, str), so there it is
        /// a String copy even for a subclass or a frozen receiver (#89). The expected output is CRuby 4.0's.
        /// </summary>
        public void Regex_MatchStringClassAndIdentity1() {
            TestOutput(@"
class S < String; end
def t(label, src, m) = puts([label, m.string.class, m.string.equal?(src), m.string.frozen?, m.string].inspect)
s = S.new(""abc""); f = ""xyz"".freeze; fs = S.new(""xyz"").freeze; u = +""hello""
s =~ /b/; t(:match_sub, s, $~)
f =~ /y/; t(:match_frozen, f, $~)
fs =~ /y/; t(:match_frozen_sub, fs, $~)
u =~ /l/; t(:match_plain, u, $~)
s.sub(/a/, ""q""); t(:sub, s, $~)
f.sub(/x/, ""q""); t(:sub_frozen, f, $~)
fs.sub(/x/) { ""q"" }; t(:sub_blk_frozen_sub, fs, $~)
s.sub(/a/, ""a"" => ""q""); t(:sub_hash, s, $~)
s.sub(""a"", ""q""); t(:sub_strpat, s, $~)
u.sub(/l/, ""q""); t(:sub_plain, u, $~)
s1 = S.new(""abc""); s1.sub!(/a/, ""q""); t(:sub_bang, s1, $~)
s.gsub(/a/, ""q""); t(:gsub, s, $~)
f.gsub(/x/, ""q""); t(:gsub_frozen, f, $~)
f.scan(/./); t(:scan_frozen, f, $~)
s2 = S.new(""abc""); m = s2.match(/b/); s2 << ""zz""; t(:mutated, s2, m)
", @"
[:match_sub, S, false, true, ""abc""]
[:match_frozen, String, true, true, ""xyz""]
[:match_frozen_sub, S, true, true, ""xyz""]
[:match_plain, String, false, true, ""hello""]
[:sub, String, false, true, ""abc""]
[:sub_frozen, String, false, true, ""xyz""]
[:sub_blk_frozen_sub, String, false, true, ""xyz""]
[:sub_hash, String, false, true, ""abc""]
[:sub_strpat, String, false, true, ""abc""]
[:sub_plain, String, false, true, ""hello""]
[:sub_bang, S, false, true, ""abc""]
[:gsub, S, false, true, ""abc""]
[:gsub_frozen, String, true, true, ""xyz""]
[:scan_frozen, String, true, true, ""xyz""]
[:mutated, S, false, true, ""abc""]
");
        }

        /// <summary>
        /// Regexps of the same pattern, options and timeout share the translated CLR regex (#89):
        /// errors are still raised every time, \G and named groups still work, and each Regexp
        /// still counts its own way up to being compiled. The expected output is CRuby 4.0's.
        /// </summary>
        public void Regex_SharedTranslation1() {
            TestOutput(@"
r = (1..3).map { Regexp.new(""(?<w>\\w+)@(\\w+)"") }
p [r.map(&:names).uniq, r.map { ""joe@ex"" =~ it }, r[0] == r[2], r[0].equal?(r[1])]
p [Regexp.new(""ab"", Regexp::IGNORECASE) =~ ""xAB"", Regexp.new(""ab"") =~ ""xAB"", Regexp.new(""a.c"", Regexp::MULTILINE) =~ ""a\nc"", Regexp.new(""a.c"") =~ ""a\nc""]
g = Regexp.new(""\\Gab""); h = Regexp.new(""\\Gab"")
p [""xab"".index(g, 1), ""xab"".index(h, 0), ""abab"".scan(h), ""xabab"".scan(g)]
3.times { p((Regexp.new(""a(b"") rescue $!.class)) }
t1 = Regexp.new(""a+"", timeout: 1.5); t2 = Regexp.new(""a+"")
p [t1.timeout, t2.timeout, ""baa""[t1], ""baa""[t2]]
x = ""ab""
p (0...9000).map { |i| ""zab#{i % 3}"" =~ /#{x}#{i % 3}/ }.uniq
p (1..5).map { |i| /(#{i})-(?<n>x)?/.names }
src = ""(\\w+)@(\\w+)\\.com""
12000.times { ""joe@example.com"" =~ Regexp.new(src) }
hot = Regexp.new(src)
12000.times { ""joe@example.com"" =~ hot }
p [""a joe@example.com b"".match(Regexp.new(src)).captures, $~.begin(0), ""no"" =~ Regexp.new(src)]
", @"
[[[""w""]], [0, 0, 0], true, false]
[1, nil, 0, nil]
[1, nil, [""ab"", ""ab""], []]
RegexpError
RegexpError
RegexpError
[1.5, nil, ""aa"", ""aa""]
[1]
[[""n""], [""n""], [""n""], [""n""], [""n""]]
[[""joe"", ""example""], 2, nil]
");
        }

        public void Regex_SharedTranslation2() {
            var field = typeof(RubyRegex).GetField("_cachedRegex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var pattern = "shared(tr)+anslation2";
            var a = new RubyRegex(MutableString.CreateAscii(pattern), RubyRegexOptions.NONE);
            var b = new RubyRegex(MutableString.CreateAscii(pattern), RubyRegexOptions.NONE);
            var c = new RubyRegex(MutableString.CreateAscii(pattern), RubyRegexOptions.IgnoreCase);
            Assert(field.GetValue(a) != null && field.GetValue(a) == field.GetValue(b));
            Assert(field.GetValue(a) != field.GetValue(c));

            // a tiers up on its own uses; b, which already has its regex, is not affected, and a
            // Regexp made afterwards starts with the compiled one
            var input = MutableString.CreateAscii("x sharedtrtranslation2 y");
            for (int i = 0; i < 10001; i++) {
                Assert(a.Match(input) != null);
            }
            var compiled = (Regex)field.GetValue(a);
            Assert((compiled.Options & RegexOptions.Compiled) != 0);
            Assert(field.GetValue(b) != compiled);
            var d = new RubyRegex(MutableString.CreateAscii(pattern), RubyRegexOptions.NONE);
            Assert(field.GetValue(d) == compiled);
            Assert(d.Match(input).GetGroupStart(1) == 10); // the last "tr"
        }

        /// <summary>
        /// The encoding of a regexp literal and MRI's preprocessing of its pieces: a \x escape above
        /// 0x7f makes a US-ASCII file's literal binary, a literal interpolating only string literals
        /// is built once and a SyntaxError if it is invalid, each literal piece of an interpolated
        /// one must be valid alone, and the interpolated pieces must be valid and agree on an
        /// encoding (rb_reg_preprocess_dregexp). A \u escape cannot meet bytes of another encoding.
        /// </summary>
        public void Regex_LiteralEncodings1() {
            TestOutput(@"
require 'ruby4'
def t(code)
  eval(code).encoding
rescue SyntaxError, RegexpError, ArgumentError => e
  e.class
end
p t(%Q{# encoding: US-ASCII\n/\\x80/})
p t(%Q{/a\#{""\\x80""}/})
p t(%Q{/\\xc2\#{ }\\xa1/u})
p t(%Q{/\\xc2\\xa1\#{ }/u})
s1, s2 = '\xc2', '\xa1'
p((/#{s1}#{s2}/ rescue $!))
x = ""\x80""
p((/#{x}/ rescue $!))
p((Regexp.new(""\xc2\xa1\\u{6666}"".b) rescue $!))
p((Regexp.new(""\\u{6666}\\xc2\\xa1"".b) rescue $!))
p 2.times.map { /a#{""b""}c/ }.uniq(&:object_id).size
r = Regexp.new(""\xa4\xa2"".b.force_encoding(""EUC-JP""))
s = ""\x82\xa0"".b.force_encoding(""Windows-31J"")
p((/#{s}#{r}/ rescue $!))
", @"
#<Encoding:BINARY (ASCII-8BIT)>
SyntaxError
SyntaxError
#<Encoding:UTF-8>
#<ArgumentError: too short escaped multibyte character>
#<ArgumentError: invalid multibyte character>
#<RegexpError: UTF-8 character in non UTF-8 regexp: /\xC2\xA1\u{6666}/>
#<RegexpError: escaped non ASCII character in UTF-8 regexp: /\u{6666}\xc2\xa1/>
1
#<RegexpError: encoding mismatch in dynamic regexp : Windows-31J and EUC-JP>
");
        }

        /// <summary>
        /// A character property pins a regexp to the encoding of its source, as MRI's
        /// rb_reg_preprocess does (Regexp::FIXEDENCODING, and no match against a string of another
        /// encoding that is not ASCII only); a property name an encoding does not know is a RegexpError.
        /// Util/regexp-property-matrix.rb has the rest.
        /// </summary>
        public void Regex_PropertyEncoding1() {
            TestOutput(@"
require 'ruby4'
def t(l)
  v = begin; yield; rescue => e; [e.class, e.message]; end
  puts ""#{l}: #{v.inspect}""
end
def d(r) = [r.options, r.encoding, r.fixed_encoding?]
t('literal') { d(/\p{L}/) + [/\p{L}/.to_s.encoding] }
t('escaped') { d(/\\p{L}/) }
t('comment') { [d(/(?#\p{L})x/), d(/x # \p{L}
/x)] }
t('union') { d(Regexp.union(/x/, /\p{L}/)) }
t('interpolation') { d(/#{/x/}\p{N}/) }
t('binary') { d(Regexp.new('\p{Alpha}'.b)) }
t('match euc-jp') { [0xa4a2].pack('n').force_encoding('EUC-JP') =~ /\p{L}/ }
t('match ascii only') { 'ab'.encode('EUC-JP') =~ /\p{L}/ }
t('match utf-16') { 'ab'.encode('UTF-16LE') =~ /\p{L}/ }
t('name in us-ascii') { Regexp.new('\p{L}'.encode('US-ASCII')) }
t('name in euc-jp') { [Regexp.new('\p{Hiragana}'.encode('EUC-JP')).encoding, (Regexp.new('\p{Hangul}'.encode('EUC-JP')) rescue $!.message)] }
t('marshal') { d(Marshal.load(Marshal.dump(/\p{L}/))) }
t('fragment') { d(/#{'\p{Alpha}'.encode('US-ASCII')}/) }
t('fragments') { /#{'\p{Alpha}'.encode('US-ASCII')}\p{L}/ }
", @"
literal: [16, #<Encoding:UTF-8>, true, #<Encoding:UTF-8>]
escaped: [0, #<Encoding:US-ASCII>, false]
comment: [[0, #<Encoding:US-ASCII>, false], [2, #<Encoding:US-ASCII>, false]]
union: [16, #<Encoding:UTF-8>, true]
interpolation: [16, #<Encoding:UTF-8>, true]
binary: [16, #<Encoding:BINARY (ASCII-8BIT)>, true]
match euc-jp: [Encoding::CompatibilityError, ""incompatible encoding regexp match (UTF-8 regexp with EUC-JP string)""]
match ascii only: 0
match utf-16: [Encoding::CompatibilityError, ""incompatible encoding regexp match (UTF-8 regexp with UTF-16LE string)""]
name in us-ascii: [RegexpError, ""invalid character property name {L}: /\\p{L}/""]
name in euc-jp: [#<Encoding:EUC-JP>, ""invalid character property name {Hangul}: /\\p{Hangul}/""]
marshal: [16, #<Encoding:UTF-8>, true]
fragment: [16, #<Encoding:US-ASCII>, true]
fragments: [RegexpError, ""encoding mismatch in dynamic regexp : US-ASCII and UTF-8""]
");
        }

        /// <summary>
        /// Regexp#inspect writes for the default internal encoding, or else the default external one
        /// (rb_reg_desc), as String#inspect does: under LANG=C, Regexp.new("\u3042").inspect is
        /// "/\\u3042/", not the character. A pattern that has to be escaped at all - a '/' in it, or
        /// an unprintable ASCII character - has every non-ASCII character escaped, even one the result
        /// could hold: \uXXXX in a Unicode regexp, \x{NNNN} (its bytes) in EUC-JP, \xNN for a byte.
        /// </summary>
        public void Regex_InspectResultEncoding1() {
            TestOutput(@"
require 'ruby4'
def t(ext, int = nil)
  Encoding.default_external = ext
  Encoding.default_internal = int
  rs = [Regexp.new(""\u3042""), Regexp.new(""\u3042/""), Regexp.new(""\u{1F600}\x01""), Regexp.new(""\\\u3042""),
        Regexp.new(""\xA4\xA2/"".b.force_encoding(""EUC-JP"")), Regexp.new(""\xA4\xA2"".b.force_encoding(""EUC-JP"")),
        Regexp.new(""\xE9"".b.force_encoding(""Windows-1252"")), Regexp.new(""a\x80"".b), Regexp.new(""\u3042"", Regexp::IGNORECASE)]
  puts ""#{ext} #{int}: "" + rs.map { |r| s = r.inspect; s.ascii_only? ? s : ""<#{s.encoding} #{s.b.unpack1('H*')}>"" }.join(' ')
ensure
  Encoding.default_internal = nil
  Encoding.default_external = 'UTF-8'
end
t('UTF-8')
t('US-ASCII')
t('EUC-JP')
t('BINARY')
t('US-ASCII', 'UTF-8')
", @"
UTF-8 : <UTF-8 2fe381822f> /\u3042\// /\u{1F600}\x01/ <UTF-8 2f5ce381822f> /\x{A4A2}\// /\x{A4A2}/ /\xE9/ /a\x80/ <UTF-8 2fe381822f69>
US-ASCII : /\u3042/ /\u3042\// /\u{1F600}\x01/ <UTF-8 2f5ce381822f> /\x{A4A2}\// /\x{A4A2}/ /\xE9/ /a\x80/ /\u3042/i
EUC-JP : /\u3042/ /\u3042\// /\u{1F600}\x01/ <UTF-8 2f5ce381822f> /\x{A4A2}\// <EUC-JP 2fa4a22f> /\xE9/ /a\x80/ /\u3042/i
BINARY : /\u3042/ /\u3042\// /\u{1F600}\x01/ <UTF-8 2f5ce381822f> /\x{A4A2}\// /\x{A4A2}/ /\xE9/ <ASCII-8BIT 2f61802f> /\u3042/i
US-ASCII UTF-8: <UTF-8 2fe381822f> /\u3042\// /\u{1F600}\x01/ <UTF-8 2f5ce381822f> /\x{A4A2}\// /\x{A4A2}/ /\xE9/ /a\x80/ <UTF-8 2fe381822f69>
");
        }

        /// <summary>
        /// A non-ASCII byte right after a backslash, in a single byte encoding, does not pin the
        /// regexp to the source's encoding (MRI's unescape_nonascii copies it "as is"), so
        /// Regexp.new("\\\xFF".b) is US-ASCII and not fixed; it is compiled again in the encoding of
        /// a non-ASCII string it is matched against, and fails where the byte is no character.
        /// Util/regexp-escaped-byte-matrix.rb has the rest.
        /// </summary>
        public void Regex_EscapedByteEncoding1() {
            TestOutput(@"
require 'ruby4'
def t(l)
  v = begin; yield; rescue => e; [e.class, e.message]; end
  puts ""#{l}: #{v.inspect}""
end
def d(r) = [r.encoding, r.source.encoding, r.fixed_encoding?, r.options]
r = Regexp.new(""\\\xFF"".b)
t('escaped byte') { d(r) + [r.inspect.encoding, r =~ ""a"", r =~ ""\xFF"".b] }
t('preceded') { d(Regexp.new(""a\\\xFF"".b)) }
t('escaped backslash') { d(Regexp.new(""\\\\\xFF"".b)) }
t('unescaped byte') { d(Regexp.new(""\xFF"".b)) }
t('two bytes') { d(Regexp.new(""\\\xA4\xA2"".b)) }
t('latin-1') { d(Regexp.new(""\\\xFF"".b.force_encoding(""ISO-8859-1""))) }
t('euc-jp') { d(Regexp.new(""\\\xA4\xA2"".b.force_encoding(""EUC-JP""))) }
t('euc-jp invalid') { Regexp.new(""\\\xFF"".b.force_encoding(""EUC-JP"")) }
t('utf-8 invalid') { Regexp.new(""\\\xFF"".b.force_encoding(""UTF-8""), Regexp::IGNORECASE).encoding }
t('noencoding') { d(Regexp.new(""\\\xFF"".b, Regexp::NOENCODING)) }
t('noencoding latin-1') { Regexp.new(""\\\xFF"".b.force_encoding(""ISO-8859-1""), Regexp::NOENCODING) }
t('fixedencoding') { d(Regexp.new(""\\\xFF"".b, Regexp::FIXEDENCODING)) }
t('match latin-1') { Regexp.new(""\\\xFF"".b) =~ ""\xFF"".b.force_encoding(""ISO-8859-1"") }
t('match utf-8') { Regexp.new(""\\\xFF"".b) =~ ""\u00FF"" }
t('source again') { Regexp.new(r.source) }
t('interpolated') { /#{r}/ }
", @"
escaped byte: [#<Encoding:US-ASCII>, #<Encoding:US-ASCII>, false, 0, #<Encoding:US-ASCII>, nil, 0]
preceded: [#<Encoding:US-ASCII>, #<Encoding:US-ASCII>, false, 0]
escaped backslash: [#<Encoding:BINARY (ASCII-8BIT)>, #<Encoding:BINARY (ASCII-8BIT)>, true, 16]
unescaped byte: [#<Encoding:BINARY (ASCII-8BIT)>, #<Encoding:BINARY (ASCII-8BIT)>, true, 16]
two bytes: [#<Encoding:BINARY (ASCII-8BIT)>, #<Encoding:BINARY (ASCII-8BIT)>, true, 16]
latin-1: [#<Encoding:US-ASCII>, #<Encoding:US-ASCII>, false, 0]
euc-jp: [#<Encoding:EUC-JP>, #<Encoding:EUC-JP>, true, 16]
euc-jp invalid: [RegexpError, ""invalid multibyte character: /\\\xFF/""]
utf-8 invalid: [RegexpError, ""invalid multibyte character: /\\\xFF/i""]
noencoding: [#<Encoding:US-ASCII>, #<Encoding:US-ASCII>, false, 32]
noencoding latin-1: [RegexpError, ""/.../n has a non escaped non ASCII character in non ASCII-8BIT script: /\\\xFF/""]
fixedencoding: [#<Encoding:BINARY (ASCII-8BIT)>, #<Encoding:BINARY (ASCII-8BIT)>, true, 16]
match latin-1: 0
match utf-8: [ArgumentError, ""regexp preprocess failed: invalid multibyte character""]
source again: [RegexpError, ""invalid multibyte character: /\\\xFF/""]
interpolated: [ArgumentError, ""invalid multibyte character""]
");
        }

        /// <summary>
        /// MRI's unescape_nonascii (RubyRegex.Preprocess) beyond an escaped byte: a comment pins the
        /// encoding only in UTF-8, and the bytes of a # comment in extended mode are not checked;
        /// it stops at the first thing wrong, so an escape's error comes before the bytes after it.
        /// A US-ASCII regexp holding bytes outside ASCII writes those as \xHH in its text, is
        /// compiled again in the encoding of the string it is matched against, makes a union fail
        /// as rb_str_append does, and equals a regexp with the same source bytes.
        /// </summary>
        public void Regex_EscapedByteEncoding2() {
            TestOutput(@"
require 'ruby4'
def t(l)
  v = begin; yield; rescue => e; [e.class, e.message]; end
  puts ""#{l}: #{v.inspect}""
end
def d(r) = [r.encoding, r.fixed_encoding?, r.to_s.b]
t('comment') { d(Regexp.new(""(?#\xFF)a"".b)) }
t('comment utf-8') { d(Regexp.new(""(?#\xC3\xA9)a"".b.force_encoding(""UTF-8""))) }
t('extended comment') { d(Regexp.new(""a # \xFF"".b, Regexp::EXTENDED)) }
t('inline extended') { d(Regexp.new(""(?x:a # \xFF\n)\xFE"".b)) }
t('unchecked comment') { d(Regexp.new(""a # \xFF"".b.force_encoding(""UTF-8""), Regexp::EXTENDED)) }
t('hex escape first') { Regexp.new(""\\x\xFF"".b.force_encoding(""UTF-8"")) }
t('control escape') { Regexp.new(""\\c\xFF"".b) }
t('unmatched') { Regexp.new(""(?i:a"") }
t('windows-1252') { Regexp.new(""\\\x80"".b) =~ ""\x80"".b.force_encoding(""Windows-1252"") }
t('euro') { Regexp.new(""\\\x80"".b.force_encoding(""Windows-1252"")) =~ ""\u20AC"".encode(""Windows-1252"") }
r = Regexp.new(""\\\xFF"".b)
t('union') { [Regexp.union(r, ""a"").encoding, (Regexp.union(r, ""\u00FF"") rescue $!.message), (Regexp.union(""\u00FF"", r) rescue $!.message)] }
t('union utf-8') { Regexp.union(r, /\u00FF/) }
t('equal') { m = Regexp.new(""a # \xFF"".b, Regexp::EXTENDED); [m == Regexp.new(m.source, m.options), Marshal.load(Marshal.dump(m)).encoding] }
", @"
comment: [#<Encoding:US-ASCII>, false, ""(?-mix:(?#\\xFF)a)""]
comment utf-8: [#<Encoding:UTF-8>, true, ""(?-mix:(?#\xC3\xA9)a)""]
extended comment: [#<Encoding:US-ASCII>, false, ""(?x-mi:a # \\xFF)""]
inline extended: [#<Encoding:BINARY (ASCII-8BIT)>, true, ""(?-mix:(?x:a # \xFF\n)\xFE)""]
unchecked comment: [#<Encoding:UTF-8>, true, ""(?x-mi:a # \\xFF)""]
hex escape first: [RegexpError, ""invalid hex escape: /\\x\\xFF/""]
control escape: [RegexpError, ""too short control escape: /\\c\\xFF/""]
unmatched: [RegexpError, ""end pattern with unmatched parenthesis: /(?i:a/""]
windows-1252: 0
euro: 0
union: [#<Encoding:US-ASCII>, ""incompatible character encodings: US-ASCII and UTF-8"", ""incompatible character encodings: UTF-8 and US-ASCII""]
union utf-8: [RegexpError, ""invalid multibyte character: /(?-mix:\\\xFF)|(?-mix:\\u00FF)/""]
equal: [true, #<Encoding:US-ASCII>]
");
        }

        /// <summary>
        /// MRI's unescape_nonascii keeps one fixed encoding: a character, an escape of bytes and a
        /// \u escape that disagree are an error, and escaped bytes outside ASCII have to make a
        /// whole character. An interpolated regexp reads each fragment so, /n ones as BINARY, and
        /// Regexp.union classifies a string by its encoding and ASCII content alone.
        /// </summary>
        public void Regex_EscapedByteEncoding3() {
            TestOutput(@"
require 'ruby4'
def t(l)
  v = begin; r = yield; r.is_a?(Regexp) ? [r.encoding, r.fixed_encoding?] : r; rescue => e; [e.class, e.message]; end
  puts ""#{l}: #{v.inspect}""
end
L = ->(s) { s.encode(""ISO-8859-1"") }
t('escape then literal') { Regexp.new(L[""\\u00E9\u00e9""]) }
t('escape then byte') { Regexp.new(L[""\\u00E9\\xE9""]) }
t('byte then escape') { Regexp.new('\xFF\u00e9'.b) }
t('class') { Regexp.new('[\u00e9-\xFF]'.b) }
t('escaped character') { Regexp.new('\xC3\xA9') }
t('cut short') { Regexp.new('\xC3') }
t('not a character') { Regexp.new('\xC3\x41') }
t('meta') { Regexp.new('\M-\C-a') }
t('surrogate') { Regexp.new('\uD800\x') }
t('list space') { r = Regexp.new('\u{ 41 42 }'); r =~ ""AB"" }
t('fragment') { s = '\xC3z'; /#{s}/ }
t('fragment n') { s = '\x'; /#{s}/n }
t('fragments') { s = '\u00e9'; /#{s}\xE9/n }
t('union string') { Regexp.union('\M-a', L[""\u00e9""]) }
t('retry') { r = Regexp.allocate; (r.send(:initialize, ""\u00e9"", Regexp::NOENCODING) rescue nil); r.send(:initialize, ""a""); r.source }
t('translated') { Regexp.new('\xC3\xA9)') }
", @"
escape then literal: [RegexpError, ""non ASCII character in UTF-8 regexp: /\\u00E9\\xE9/""]
escape then byte: [RegexpError, ""escaped non ASCII character in UTF-8 regexp: /\\u00E9\\xE9/""]
byte then escape: [RegexpError, ""UTF-8 character in non UTF-8 regexp: /\\xFF\\u00e9/""]
class: [RegexpError, ""escaped non ASCII character in UTF-8 regexp: /[\\u00e9-\\xFF]/""]
escaped character: [#<Encoding:UTF-8>, true]
cut short: [RegexpError, ""too short escaped multibyte character: /\\xC3/""]
not a character: [RegexpError, ""invalid multibyte escape: /\\xC3\\x41/""]
meta: [RegexpError, ""invalid multibyte escape: /\\M-\\C-a/""]
surrogate: [RegexpError, ""invalid Unicode range: /\\uD800\\x/""]
list space: 0
fragment: [ArgumentError, ""too short escaped multibyte character""]
fragment n: [ArgumentError, ""invalid hex escape""]
fragments: [RegexpError, ""encoding mismatch in dynamic regexp : UTF-8 and ASCII-8BIT""]
union string: [#<Encoding:ISO-8859-1>, true]
retry: ""a""
translated: [RegexpError, ""unmatched close parenthesis: /\\xC3\\xA9)/""]
");
        }

        /// <summary>
        /// From a scan pointer inside a character of a valid subject, MRI's strscan matches the rest as a
        /// string of its own: the split character's bytes are characters there ("\xA9llo"). A subject that
        /// is broken as a whole still raises.
        /// </summary>
        public void StringScanner_InsideCharacter1() {
            TestOutput(@"
require 'strscan'
s = StringScanner.new(""h\u00e9llo w, x"")
s.pos = 2
p s.scan(/\S+/), s.pos, s.matched, s.pre_match.bytes, s[0]
s.pos = 2
p s.skip(/\s+/), s.exist?(/ /), s.match?(/\w+/), s.scan_until(/,/), s.pos
e = StringScanner.new(""\xA4\xA2a b"".dup.force_encoding(""EUC-JP""))
e.pos = 1
p e.scan(/\S+/), e.pos, e.rest
e.pos = 1
p e.scan_until(/ /), e.charpos
b = StringScanner.new(""ab\xE9c d"".dup.force_encoding(""UTF-8""))
b.pos = 2
p((b.scan(/\S+/) rescue $!))
", @"
""\xA9llo""
6
""\xA9llo""
[104, 195]
""\xA9llo""
nil
5
nil
""\xA9llo w,""
9
""\xA2a""
3
"" b""
""\xA2a ""
3
#<ArgumentError: invalid byte sequence in UTF-8>
");
        }

        /// <summary>
        /// A subject replaced by a shorter one under the scan pointer: MRI's scans answer nil, #rest is
        /// empty, #bol? is nil and #charpos counts the bytes beyond the end as characters.
        /// </summary>
        public void StringScanner_ShorterSubject1() {
            TestOutput(@"
require 'strscan'
s = +""hello world""
ss = StringScanner.new(s)
ss.scan(/\S+/); ss.skip(/\s+/)
s.replace(""a b"")
p [ss.scan(/\S+/), ss[0], ss.pos, ss.matched, ss.pre_match, ss.rest, ss.charpos, ss.getch, ss.bol?, ss.eos?]
p [ss.peek(2), ss.rest_size, ss.check(/x/), ss.skip(""a""), ss.exist?(/a/), ss.rest?, ss.inspect]
", @"
[nil, nil, 6, nil, nil, """", 6, nil, nil, true]
["""", 0, nil, nil, nil, false, ""#<StringScanner fin>""]
");
        }

        /// <summary>
        /// A String pattern whose encoding is incompatible with the subject's is an error to MRI's strscan
        /// (rb_enc_check), not a miss; an ASCII pattern, or an ASCII-only subject, is compatible.
        /// </summary>
        public void StringScanner_LiteralEncoding1() {
            TestOutput(@"
require 'strscan'
b = StringScanner.new(""ab\xE9c"".b)
p((b.scan(""\u00e9"") rescue $!), (b.scan(""a"") rescue $!), (b.skip_until(""\u00e9"") rescue $!))
e = StringScanner.new(""\xA4\xA2a"".dup.force_encoding(""EUC-JP""))
e.pos = 3
p((e.scan(""\u00e9"") rescue $!), (e.check("""") rescue $!))
a = StringScanner.new(""abc"")
p((a.scan(""\u00e9"") rescue $!))
u = StringScanner.new(""h\u00e9"")
p((u.scan(""\xE9"".b) rescue $!))
", @"
#<Encoding::CompatibilityError: incompatible character encodings: BINARY (ASCII-8BIT) and UTF-8>
""a""
#<Encoding::CompatibilityError: incompatible character encodings: BINARY (ASCII-8BIT) and UTF-8>
#<Encoding::CompatibilityError: incompatible character encodings: EUC-JP and UTF-8>
""""
nil
#<Encoding::CompatibilityError: incompatible character encodings: UTF-8 and BINARY (ASCII-8BIT)>
");
        }

        /// <summary>
        /// A BINARY subject is matched with Onigmo's ASCII-8BIT ctype table (MRI's
        /// rb_reg_prepare_enc compiles a regexp that is not fixed-encoding for the subject's
        /// encoding): no letters, punctuation, word characters or case pairs above 0x7F, where .NET
        /// sees U+0080-U+00FF. ISO-8859-1 and UTF-8 subjects keep their own letters, and a BINARY
        /// regexp's \p{Punct} is [[:punct:]] even on ASCII text.
        /// </summary>
        /// <summary>
        /// Against an ISO-8859-1 subject Onigmo uses its ISO-8859-1 ctype table, not Unicode's:
        /// the superscripts and vulgar fractions are word characters, U+00A7 and U+00B6 are not
        /// punctuation but the soft hyphen is, U+0085 is neither a space nor a line break, and
        /// /i folds no case pair onto U+00DF, U+00FF or U+00B5.
        /// </summary>
        public void Regex_Latin1Ctype1() {
            TestOutput(@"
def l(s) = s.dup.force_encoding(""ISO-8859-1"")
s = l(""\xAA\xA7\xB2\xA0\xD7\xB6\xAD\xBC"")
p s.scan(/[[:word:]]/).map(&:ord)
p s.scan(/(?u)\w/).map(&:ord)
p s.scan(/[[:punct:]]/).map(&:ord)
p s.scan(/[[:space:]]|\s/).map(&:ord)
p s.scan(/(?a)[[:alpha:]]/).map(&:ord)
p s.scan(/\b./).map(&:ord)
p l(""a\x85"").scan(/\R/).size
p l(""\xDF\xFF\xB5\xC0"").scan(/[[:upper:]]/i).map(&:ord)
p l(""abc"").scan(/\w/).size
", @"
[170, 178, 188]
[170, 178, 188]
[173]
[160]
[170]
[170, 167, 178, 160, 188]
0
[192]
3
");
        }

        public void Regex_BinaryCtype1() {
            TestOutput(@"
require 'ruby4'
def t(l)
  v = begin; yield; rescue => e; [e.class, e.message]; end
  puts ""#{l}: #{v.inspect}""
end
s = ""a\xE9b\xFF c\xC9"".b
t('property /n') { ""\xff"".b =~ /\p{Alpha}/n }
t('posix') { ""\xff"".b =~ Regexp.new('[[:alpha:]]'.b) }
t('word') { ""\xff"".b =~ Regexp.new('\p{Word}'.b) }
t('lower') { ""\xff"".b =~ Regexp.new('\p{Lower}'.b) }
t('negated') { [""\xff"".b =~ /[[:^alpha:]]/, ""\xff"".b =~ /\P{Alpha}/n] }
t('\w') { [""\xff"".b =~ /\w/n, ""\xff"".b =~ /(?u)\w/] }
t('punct') { [""$"".b =~ Regexp.new('\p{Punct}'.b), ""$\xff"".b =~ /[[:punct:]]+/, ""\xab"".b =~ /[[:punct:]]/] }
t('\b') { [s.scan(/\b./), s.split(/\b/)] }
t('\R') { [""\x85"".b =~ /\R/, ""\r\n\x85"".b[/\R+/]] }
t('scan') { s.scan(/[[:alpha:]]+/) }
t('gsub') { s.gsub(/\p{^Alpha}/n, '.') }
t('/i literal') { [""\xC9"".b =~ /\xE9/in, ""\xC9"".b =~ Regexp.new(""\xE9"".b, Regexp::IGNORECASE), ""\xE9"".b =~ /\xE9/in] }
t('/i class') { [""\xC9"".b =~ /[\xE0-\xEF]/in, ""\xC9"".b =~ /[^\xE9]/in, ""A\xE9"".b =~ /[^a]/in, ""A\xE9"".b =~ /a/in] }
t('/i posix') { s.scan(/[[:upper:]]+/i) }
t('(?i)') { ""\xC9a"".b =~ /(?i:\xE9)|(?i)A/n }
t('one regexp') { re = /[[:alpha:]]/; [re.match?(""\u00e9""), re.match?(""\xE9"".b), re.match?(""\u00e9""), re.match?(""\xE9"".dup.force_encoding(""ISO-8859-1""))] }
t('iso-8859-1') { [""\xE9"".dup.force_encoding(""ISO-8859-1"") =~ /[[:alpha:]]/, ""\xC9"".dup.force_encoding(""ISO-8859-1"") =~ Regexp.new(""\xE9"".dup.force_encoding(""ISO-8859-1""), Regexp::IGNORECASE)] }
t('utf-8') { [""\u00e9"" =~ /\p{Alpha}/, ""\u00e9"" =~ /\b/] }
t('absent /i') { a = ""\xE9a"".b; [a.match(/(?~\xE9a)/in, 0)[0], a.match(/(?~\xE9a)/in, 1)[0], ""\xC9a"".b.match(/(?~\xE9a)/in)[0], a.match(/(?~\xC9a)/in)[0]] }
", @"
property /n: nil
posix: nil
word: nil
lower: nil
negated: [0, 0]
\w: [nil, nil]
punct: [0, 0, nil]
\b: [[""a"", ""\xE9"", ""b"", ""\xFF"", ""c"", ""\xC9""], [""a"", ""\xE9"", ""b"", ""\xFF "", ""c"", ""\xC9""]]
\R: [nil, ""\r\n""]
scan: [""a"", ""b"", ""c""]
gsub: ""a.b..c.""
/i literal: [nil, nil, 0]
/i class: [nil, 0, 1, 0]
/i posix: [""a"", ""b"", ""c""]
(?i): 1
one regexp: [true, false, true, true]
iso-8859-1: [0, 0]
utf-8: [0, 0]
absent /i: [""\xE9"", ""a"", ""\xC9a"", ""\xE9a""]
");
        }
    }
}


