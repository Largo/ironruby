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
using IronRuby.Builtins;
using System.Text;

namespace IronRuby.Tests {
    public partial class Tests {
        public void StringsPlus() {
            AssertExceptionThrown<InvalidOperationException>(delegate() {
                CompilerTest(@"
puts 'foo' + nil
");
            });
        }

        public void Strings0() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts ""foo""
puts ""foo"" 'bar' ""baz""
");
            }, @"
foo
foobarbaz
");
        }

        public void Strings1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts ""foo#{""bar""}baz""
");
            }, @"
foobarbaz
");
        }

        public void Strings2() {
            TestOutput(@"
puts ""foo#{1;2;3}baz""
", @"
foo3baz
", 2);
        }

        public void Strings3() {
            TestOutput(@"
class String; def to_s; 'S'; end; end
class Integer; def to_s; 'N'; end; end

p """"
puts ""#{1}""
puts ""#{1}-""
puts ""-#{1}""
puts ""-#{1}-""
puts ""#{1}#{1}""
puts ""#{1}+#{1}""
puts ""-#{1}+#{1}""
puts ""-#{1}+#{1}-""

puts ""-#{x = 'bob'}-""
", @"
""""
N
N-
-N
-N-
NN
N+N
-N+N
-N+N-
-bob-
", 1);
        }

        public void Strings4() {
            AssertOutput(delegate() {
                CompilerTest(@"
p ""#{nil}""
p ""#{nil}-""
p ""-#{nil}""
p ""-#{nil}-""
p ""#{nil}#{nil}""
p ""-#{nil}+#{nil}-""
");
            }, @"
""""
""-""
""-""
""--""
""""
""-+-""
");
        }

        public void Strings5() {
            AssertOutput(delegate() {
                CompilerTest(@"
class String; def to_s; 'S'; end; end
class Integer; def to_s; 'N'; end; end

puts :""#{1}""
puts :""#{1}-""
puts :""-#{1}""
puts :""-#{1}-""
puts :""#{1}#{1}""
puts :""#{1}+#{1}""
puts :""-#{1}+#{1}""
puts :""-#{1}+#{1}-""

puts ""-#{x = 'bob'}-""
", 1, 0);
            }, @"
N
N-
-N
-N-
NN
N+N
-N+N
-N+N-
-bob-
");
        }

        public void Strings6() {
            TestOutput(@"
p :""#{}""
p :""#{}#{}""
p :""#{}#{''}#{}""

p :""#{nil}a""
p :""a#{nil}""
p :""a#{nil}b""
p :""a#{nil}b#{nil}c""
", @"
:""""
:""""
:""""
:a
:a
:ab
:abc
");
        }

        public void Strings7() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 'foobarbaz'[3,3]
");
            }, "bar");
        }

        public void Strings8() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 'foo  bar'.split
");
            }, @"
foo
bar
");
        }

        /// <summary>
        /// Mixing incompatible encodings: a \u escape makes a literal UTF-8, so a non-ASCII byte of
        /// a binary source cannot join it in the same literal (a syntax error) or later (+).
        /// </summary>
        public void Strings9() {
            var log = new LoggingErrorSink();
            CompilerTest("# encoding: ascii-8bit\n\"\\u03a3\\xff\"", log);
            Assert(log.Errors.Count == 1 && log.Errors[0].Message.Contains("UTF-8 mixed within ASCII-8BIT source"), DescribeLog(log));

            TestOutput(@"
begin
  ""\xff"".force_encoding(Encoding::BINARY) + ""\u03a3""
rescue => e
  p e.class
end
", @"
Encoding::CompatibilityError
");
        }

        /// <summary>
        /// Embedded string does call "to_s" w/o calling "respond_to?" first.
        /// </summary>
        public void ToSConversion1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class X
  def respond_to? name
    puts name
  end
  
  def to_s
    'TO_S'
  end

  puts ""#{new}""
end
");
            }, @"
TO_S
");
        }

        public void ToSConversion2() {
            AssertOutput(delegate() {
                CompilerTest(@"
class NilClass
  def to_s; 'NULL1'; end
  puts ""#{nil}""

  def to_s; 'NULL2'; end
  puts ""#{nil}""
end

class SubString < String
  def to_s; 'XXX'; end
  puts ""#{new 'SUB'}""
end
");
            }, @"
NULL1
NULL2
SUB
");
        }

        public void Symbols1() {

            RubySymbol a, b;
            a = Context.CreateSymbol(MutableString.CreateBinary(Encoding.ASCII.GetBytes("foo"), RubyEncoding.Binary), false);
            b = Context.CreateSymbol(MutableString.CreateMutable("foo", RubyEncoding.UTF8), false);
            Assert(a.Equals(b));
        }
        
        [Options(NoRuntime = true)]
        private void Inspect2() {
            const char sq = '\'';

            var sjisEncoding = RubyEncoding.SJIS;
            // あ
            var sjisWide = new byte[] { 0x82, 0xa0 };
            // \u{12345} in UTF-8:
            var utf8 = new byte[] { 0xF0, 0x92, 0x8D, 0x85 };
            // \u{12345} in UTF-16: U+d808 U+df45 
            var utf16 = Encoding.UTF8.GetString(utf8);

            string s;

            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(utf8, RubyEncoding.Binary), false, sq).ToString();
            Assert(s == @"'\xF0\x92\x8D\x85'");

            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(utf8, RubyEncoding.Binary), true, sq).ToString();
            Assert(s == @"'\xF0\x92\x8D\x85'");

            // #inspect prints characters the encoding can represent (Ruby >= 2.0);
            // only #dump escapes them all.
            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(utf8, RubyEncoding.UTF8), false, sq).ToString();
            Assert(s == sq + utf16 + sq);

            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(utf8, RubyEncoding.UTF8), true, sq).ToString();
            Assert(s == @"'\u{12345}'");

            // incomplete character:
            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.Create("\ud808\udf45\ud808", RubyEncoding.UTF8), false, sq).ToString();
            Assert(s == sq + utf16 + @"\u{d808}" + sq);
            
            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(sjisWide, sjisEncoding), false, sq).ToString();
            Assert(s == @"'\x82\xA0'");

            s = MutableStringOps.GetQuotedStringRepresentation(MutableString.CreateBinary(sjisWide, sjisEncoding), true, sq).ToString();
            Assert(s == @"'\x82\xA0'");
        }

        /// <summary>
        /// #start_with? and #end_with? insist on a character boundary the way MRI does, which is not the
        /// same test for both on bytes that are invalid UTF-8: #end_with? needs the suffix to start on a
        /// lead byte, #start_with? steps over the character the offset is in (a truncated sequence
        /// counting with the length its lead byte announces). Neither walks the string from its start
        /// any more, and a single byte encoding answers without looking. String#sum adds in 64 bits.
        /// </summary>
        public void String_CharacterBoundaries1() {
            TestOutput(@"
require 'ruby4'
u = ->(s) { s.dup.force_encoding(""UTF-8"") }
[[""\xA9\xA9"", ""\xA9""], [""\xA9"" * 6, ""\xA9"" * 5], [""\xC3\xA9"", ""\xC3""], [""\xC3\xA9"", ""\xA9""],
 [""\xE3\x81"", ""\xE3""], [""\xE3\x81"", ""\x81""], [""\xED\xA0\x80"", ""\xED""], [""\xC3\xC3\xA9"", ""\xC3""],
 [""\xC3\xC3\xA9"", ""\xC3\xA9""], [""\xF0\x9F\x98"", ""\xF0\x9F""], [""xx\xBF"", ""\xBF""], [""\xE3\x81\x82\x81"", ""\xE3\x81\x82""],
 [""aé"", ""é""], [""éa"", ""a""]].each do |s, a|
  p [u.(s).start_with?(u.(a)), u.(s).end_with?(u.(a))]
end
b = (""x"" * 1000 + ""yz"").b
p [b.end_with?(""yz""), b.end_with?(""y""), b.start_with?(""xx""), b.force_encoding(""ISO-8859-1"").end_with?(""z"")]
p [""abc"".sum, ""abc"".sum(0), ""abc"".sum(1), (""\xFF"" * 1000).b.sum(8), (""\xFF"" * 20000).b.sum(64), (""\xFF"" * 20000).b.sum(16), ""x"".sum(100), """".sum]
", @"
[true, false]
[false, false]
[false, false]
[false, false]
[false, false]
[false, false]
[true, false]
[true, false]
[false, true]
[false, false]
[false, false]
[true, false]
[false, true]
[false, true]
[true, false, true, true]
[294, 294, 0, 24, 5100000, 53728, 120, 0]
");
        }

        /// <summary>
        /// U+FFFF is an ordinary character once a string has been mutated into its char-array form;
        /// it used to be mistaken for a decoder placeholder and #chars raised.
        /// </summary>
        public void String_NonCharacter1() {
            TestOutput(@"
s = ""a\uFFFFb"".dup
s << ""c""
s[0] = ""x""
p s.length, s.chars.map(&:ord), s.each_char.count
t = ""ab\u{1F600}"".dup
t[0] = ""Z""
p t.chars.map(&:ord)
", @"
4
[120, 65535, 98, 99]
4
[90, 98, 128512]
");
        }

        /// <summary>
        /// #count, #delete and #squeeze answer an ASCII character from a bitmask CharacterSelector
        /// computes when it is parsed, negation and intersection included; anything else goes
        /// through the selector's sets and ranges as before.
        /// </summary>
        /// <summary>
        /// A carry out of a wrapped digit or letter skips over a separator to the next
        /// alphanumeric only when the two are of the same kind (rb_str_succ).
        /// </summary>
        public void String_SuccSeparators1() {
            TestOutput(@"
p ""a-9"".succ, ""1.9"".succ, ""a.z"".succ, ""9-z"".succ, ""Az-9"".succ, ""Zz"".succ, ""zz-99"".succ, ""a-b-9"".succ
", @"
""a-10""
""2.0""
""b.a""
""9-aa""
""Az-10""
""AAa""
""zz-100""
""a-b-10""
");
        }

        public void String_CountDeleteSqueeze1() {
            TestOutput(@"
require 'ruby4'
s = ""hello world""
p [s.count(""lo""), s.count(""a-y""), s.count(""^l""), s.count(""lo"", ""o""), s.count(""^""), s.count(""\\^""), ""a^b"".count(""^""), ""x-y"".count(""x-""), s.count(""a-z"", ""^aeiou""), s.count(""^a-z"")]
e = ""héllo wörld""
p [e.count(""é-ö""), e.count(""^l""), e.delete(""é"").size, e.delete(""^é"").bytes, e.squeeze.size, ""a\u{1F600}\u{1F600}b"".count(""\u{1F600}""), ""a\u{1F600}\u{1F600}b"".squeeze.size]
p [s.delete(""l""), s.delete(""a-z""), s.delete(""^l""), s.delete(""lo"", ""o""), ""aaabbb  ccc"".squeeze, ""aaabbb"".squeeze(""a""), ""aa"".squeeze!(""b"")]
x = +""abc""
p [x.delete!(""x""), x.delete!(""b""), x]
y = ""\xFF\xFF\x80"".b
p [y.count(""\xFF"".b), y.delete(""\x80"".b).bytes, y.squeeze.bytes]
p((""abc"".count(""c-a"") rescue $!.message))
", @"
[5, 10, 8, 2, 0, 0, 1, 2, 7, 1]
[2, 8, 10, [195, 169], 10, 2, 3]
[""heo word"", "" "", ""lll"", ""hell wrld"", ""ab c"", ""abbb"", nil]
[nil, ""ac"", ""ac""]
[2, [255, 255], [255, 128]]
""invalid range \""c-a\"" in string transliteration""
");
        }

        /// <summary>
        /// #count, #delete and #squeeze read the characters in place, from a string held as bytes as well as one
        /// held as characters (#89). #squeeze! and #delete! check in MRI's order: #delete! answers nil for an empty
        /// receiver before anything else, both set up the character sets before refusing a frozen receiver, and
        /// #squeeze! refuses a frozen one even with nothing to squeeze. The expected output is CRuby 4.0's.
        /// </summary>
        public void String_CountDeleteSqueeze2() {
            TestOutput(@"
require 'ruby4'
class S < String; end
bin = [0x61, 0x61, 0x20, 0x20, 0x62, 0xff, 0xff].pack(""C*"")
asc = [0x61, 0x61, 0x6c, 0x6c, 0x20, 0x20, 0x62].pack(""C*"").force_encoding(""UTF-8"")
lat = ""caf\xE9\xE9  x"".force_encoding(""ISO-8859-1"")
p [bin.count(""a\xff"".b), bin.delete(""a"").bytes, bin.squeeze.bytes, bin.squeeze(""\xff"".b).bytes, bin.delete(""^a"").bytes]
p [asc.count(""l ""), asc.delete(""l""), asc.squeeze, asc.squeeze(""l""), asc.delete(""a-z""), asc.count(""^a-z"")]
p [lat.count(""\xE9"".force_encoding(""ISO-8859-1"")), lat.squeeze.bytes, lat.delete("" "").bytes, lat.squeeze.encoding]
p [S.new(""aab"").squeeze.class, S.new(""aab"").delete(""z"").class, S.new(""ab"").squeeze.class]
x = +""aabbcc""; p [x.squeeze!, x, x.squeeze!, x.delete!(""z""), x.delete!(""b""), x]
p [("""".delete rescue $!), """".delete!, """".freeze.delete!(""a""), ("""".freeze.squeeze! rescue $!.class)]
p [(""a"".freeze.delete!(""z"") rescue $!.class), (""a"".freeze.squeeze!(""z"") rescue $!.class), (""a"".freeze.delete!(""z-a"") rescue $!.class), (""a"".freeze.squeeze!(""z-a"") rescue $!.class)]
s = ""ab"" * 3000; p [s.count(""a""), s.delete(""a"").size, (s + s.reverse).squeeze.size, (""x"" * 5000).squeeze]
y = +""aab""; z = y.squeeze; y << ""b""; p [z, y]
", @"
[4, [32, 32, 98, 255, 255], [97, 32, 98, 255], [97, 97, 32, 32, 98, 255], [97, 97]]
[4, ""aa  b"", ""al b"", ""aal  b"", ""  "", 2]
[2, [99, 97, 102, 233, 32, 120], [99, 97, 102, 233, 233, 120], #<Encoding:ISO-8859-1>]
[String, String, String]
[""ac"", ""ac"", nil, nil, ""ac"", ""ac""]
["""", nil, nil, FrozenError]
[FrozenError, FrozenError, ArgumentError, ArgumentError]
[3000, 3000, 11999, ""x""]
[""ab"", ""aabb""]
");
        }

        /// <summary>
        /// #count, #delete and #squeeze read ASCII characters through a table, count a single
        /// character with a vectorized search and skip the non-ASCII test when no non-ASCII
        /// character can be selected (#89). The expected output is CRuby 4.0's.
        /// </summary>
        public void String_CountDeleteSqueeze3() {
            TestOutput(@"
require 'ruby4'
s = ""hello world, all well "" * 60
u = ""héllo wörld ああ "" * 40
b = (""hello\xff\xffworld  "".b) * 50
p [s.count(""l""), s.count(""lo""), s.count(""a-y""), s.count(""^l""), s.count(""a-z"", ""^l""), s.count(""^a-k"", ""lo""), s.count(""l"", ""^あ"")]
p [s.delete(""l"").size, s.delete(""lo"").size, s.delete(""^l"").size, s.delete(""a-z"", ""^l"").size, s.delete("" "")[0, 20]]
p [s.squeeze.size, s.squeeze(""l"").size, s.squeeze(""a-z"").size, s.squeeze(""^l"").size, s.squeeze[0, 20]]
p [u.count(""l""), u.count(""é""), u.count(""^l""), u.count(""lあ""), u.count(""あ"", ""^a""), u.count(""a-z"", ""\u0000-ÿ"")]
p [u.delete(""l"")[0, 12], u.delete(""あ"").size, u.delete(""^あ"").size, u.squeeze(""あ"")[0, 12], u.squeeze[0, 12]]
p [b.count(""l""), b.count(""\xff"".b), b.count(""^l""), b.count(""\x80-\xff"".b), b.delete(""\xff"".b).size, b.squeeze.size, b.squeeze(""\xff"".b).size, b.squeeze("" "").bytes.first(14)]
t = +""aaaああbbb""; p [t.squeeze!(""あ""), t, t.delete!(""b""), t, t.squeeze!(""^a""), t]
p [""xyz"".count(""x""), """".count(""x""), ""x"".squeeze, ""xx"".squeeze, ""ああ"".squeeze, ""aab"".count(""a"", ""a""), ""aあ"".count(""a-あ"")]
", @"
[420, 540, 1020, 900, 600, 540, 420]
[900, 780, 420, 720, ""helloworld,allwellhe""]
[1140, 1140, 1140, 1320, ""helo world, al wel h""]
[120, 40, 480, 200, 80, 320]
[""héo wörd ああ "", 520, 80, ""héllo wörld "", ""hélo wörld あ""]
[150, 100, 550, 100, 600, 550, 650, [104, 101, 108, 108, 111, 255, 255, 119, 111, 114, 108, 100, 32, 104]]
[""aaaあ"", ""aaaあ"", ""aaaあ"", ""aaaあ"", nil, ""aaaあ""]
[1, 0, ""x"", ""x"", ""あ"", 2, 2]
");
        }

        /// <summary>
        /// String#dump writes a non-ASCII byte of a string outside UTF-8 as \xHH, as MRI's rb_str_dump does. A string
        /// that had been read by character (#length, #inspect) wrote its characters inspect's way ("\x{A4A2}"), or an
        /// invalid byte as the byte itself, which made CRuby's test_m17n_comb fail on its own descriptions.
        /// </summary>
        public void String_DumpNonUnicode1() {
            TestOutput(@"
s = ""\xA4\xA2"".dup.force_encoding('EUC-JP')
s.length
puts s.dump
t = ""\xA1"".dup.force_encoding('EUC-JP')
t.inspect
puts t.dump
u = ""\x82\xA0#{'#'}{\""\\"".dup.force_encoding('Shift_JIS')
u.size
puts u.dump
", @"
""\xA4\xA2""
""\xA1""
""\x82\xA0\#{\""\\""
");
        }

        /// <summary>
        /// String#&lt;=&gt; with an object that cannot be a String asks it the other way round and answers -rb_cmpint of its
        /// answer, as MRI's rb_invcmp does: -1, 0 or 1, whatever number came back. 2**100 came back as -(2**100).
        /// </summary>
        public void String_CompareInverse1() {
            TestOutput(@"
def cmp(answer)
  o = Object.new
  o.define_singleton_method(:<=>) { |_| answer }
  'foo' <=> o
rescue => e
  e
end
p cmp(2**100), cmp(-(2**100)), cmp(1.5), cmp(0), cmp(nil), cmp('x')
", @"
-1
1
-1
0
nil
#<ArgumentError: comparison of String with 0 failed>
");
        }

        /// <summary>
        /// each_line('', chomp: true) takes the newline and the blank line off a paragraph a blank line ended, and leaves
        /// the last paragraph, which none ended, as it is, trailing newline and all (rb_str_enumerate_lines).
        /// </summary>
        public void String_EachLineParagraphChomp1() {
            TestOutput(@"
require 'ruby4'
p ""hello\n\n\nworld\n"".each_line('', chomp: true).to_a
p ""a\n\n\nb\n\n"".lines('', chomp: true), ""a\r\n\r\nb\r\n"".lines('', chomp: true), ""\n\na\n\n"".lines('', chomp: true)
p ""a\n\n\nb\n"".lines(''), ""a\nb\n\nc"".lines('', chomp: true)
", @"
[""hello"", ""world\n""]
[""a"", ""b""]
[""a"", ""b\r\n""]
["""", ""a""]
[""a\n\n"", ""b\n""]
[""a\nb"", ""c""]
");
        }

        /// <summary>
        /// String#upto (MRI's str_upto_each) yields a copy of the receiver and works out the next string before the
        /// block runs, so a block that changes the string it got changes neither the receiver nor the walk; and the walk
        /// starts whenever the receiver sorts before the end, a longer receiver included.
        /// </summary>
        public void String_UptoCopies1() {
            TestOutput(@"
require 'ruby4'
first = +""ぁ""
count = 0
r = first.upto(""ん"") { |s| count += 1; s.replace(""ん"") }
p r.equal?(first), first == ""\u3041"", count
def t(a, b, *x) = (r = []; a.upto(b, *x) { |s| r << s }; r)
p t('aa', 'b'), t('b', 'aa'), t('az', 'bb', true), t('y', 'ab'), t('a', 'a', true)
", @"
true
true
83
[""aa""]
[]
[""az"", ""ba""]
[]
[]
");
        }

        /// <summary>
        /// start_with?(regexp) matches at the start only (rb_reg_start_with_p): a match further on is no match, and leaves
        /// $~ nil rather than set to it.
        /// </summary>
        public void String_StartWithRegexpLastMatch1() {
            TestOutput(@"
p 'hello'.start_with?(/hel/), $&
p 'hello'.start_with?(/el/), $~
p 'hello'.start_with?(/x/), $~
", @"
true
""hel""
false
nil
false
nil
");
        }

        /// <summary>
        /// A string in a dummy encoding (ISO-2022-JP) inspects byte by byte, but the control characters with a C escape keep
        /// its short form, as in MRI's rb_str_inspect: "\e", not "\x1B".
        /// </summary>
        public void String_InspectDummyControl1() {
            TestOutput(@"
puts ""\n\r\t\f\v\b\a\e\""\\#a\x7f"".dup.force_encoding('ISO-2022-JP').inspect
", @"
""\n\r\t\f\v\b\a\e\x22\x5C\x23\x61\x7F""
");
        }

        /// <summary>
        /// MRI's lstrip reads characters only as far as the leading whitespace goes, and an invalid one there is an
        /// ArgumentError; rstrip refuses any string with an invalid byte, and a dummy encoding, with
        /// Encoding::CompatibilityError; strip does the left side first. IronRuby raised ArgumentError for any invalid
        /// string lstrip or strip met, and stripped a dummy-encoded string from the right.
        /// </summary>
        public void String_StripInvalid1() {
            TestOutput(@"
def u(s) = s.dup.force_encoding('UTF-8')
[u(""a\x80""), u("" \x80""), u(""  a \x80 "")].each do |s|
  p [:lstrip, :rstrip, :strip, :lstrip!, :rstrip!, :strip!].map { |m| (s.dup.send(m) rescue $!.class) }
end
j = "" ab "".dup.force_encoding('ISO-2022-JP')
p((j.rstrip rescue $!), (j.strip rescue $!), j.lstrip.bytes)
", @"
[""a\x80"", Encoding::CompatibilityError, Encoding::CompatibilityError, nil, Encoding::CompatibilityError, Encoding::CompatibilityError]
[ArgumentError, Encoding::CompatibilityError, ArgumentError, ArgumentError, Encoding::CompatibilityError, ArgumentError]
[""a \x80 "", Encoding::CompatibilityError, Encoding::CompatibilityError, ""a \x80 "", Encoding::CompatibilityError, Encoding::CompatibilityError]
#<Encoding::CompatibilityError: incompatible encoding with this operation: ISO-2022-JP>
#<Encoding::CompatibilityError: incompatible encoding with this operation: ISO-2022-JP>
[97, 98, 32]
");
        }

        /// <summary>
        /// String#initialize takes encoding: and capacity:, as MRI's rb_str_init does, so a subclass's super and a call
        /// through #send can give them: the keywords and the argument are checked first, then a frozen receiver is a
        /// FrozenError, and the string is emptied when there is nothing to copy. They were "no implicit conversion of
        /// Hash into String".
        /// </summary>
        public void String_InitializeKeywords1() {
            TestOutput(@"
require 'ruby4'
def t; yield; rescue Exception => e; e; end
s = +'abc'
p t { s.send(:initialize, capacity: 10) }, s
p t { s.send(:initialize, 'xy', encoding: 'euc-jp', capacity: 3) }, s.encoding
p t { s.send(:initialize, foo: 1, bar: 2) }, t { s.send(:initialize, 1, capacity: 1) }, t { s.send(:initialize, 'a', capacity: 'x') }
f = 'x'.freeze
p t { f.send(:initialize, capacity: 1) }, t { f.send(:initialize, encoding: 'utf-8') }, t { f.send(:initialize, foo: 1) }, t { f.send(:initialize) }
class S2 < String; def initialize(*a, **k); super; @x = 1; end; end
p S2.new('abc', capacity: 5), S2.new(encoding: 'euc-jp').encoding
", @"
""""
""""
""xy""
#<Encoding:EUC-JP>
#<ArgumentError: unknown keywords: :foo, :bar>
#<TypeError: no implicit conversion of Integer into String>
#<TypeError: no implicit conversion of String into Integer>
#<FrozenError: can't modify frozen String: ""x"">
#<FrozenError: can't modify frozen String: ""x"">
#<ArgumentError: unknown keyword: :foo>
""x""
""abc""
#<Encoding:EUC-JP>
");
        }

        /// <summary>
        /// to_i, hex and oct read digits as ASCII bytes and refuse an encoding that does not write them so, as MRI's
        /// rb_must_asciicompat does: "0" in UTF-32LE was 0.
        /// </summary>
        public void String_ToIntAsciiIncompatible1() {
            TestOutput(@"
require 'ruby4'
def t; yield; rescue Exception => e; e; end
p t { '0'.encode('utf-32le').to_i }, t { '12'.encode('utf-16be').to_i(16) }, t { '1'.encode('utf-16le').hex }, t { '7'.dup.force_encoding('ISO-2022-JP').oct }
p '12'.to_i, '12'.b.to_i(16), 'ff'.hex
", @"
#<Encoding::CompatibilityError: ASCII incompatible encoding: UTF-32LE>
#<Encoding::CompatibilityError: ASCII incompatible encoding: UTF-16BE>
#<Encoding::CompatibilityError: ASCII incompatible encoding: UTF-16LE>
#<Encoding::CompatibilityError: ASCII incompatible encoding: ISO-2022-JP>
12
18
255
");
        }

        /// <summary>
        /// String#undump answers UTF-8 once it has read a \u escape, whatever the receiver's encoding, and refuses \u
        /// next to a \x escape of a non-ASCII byte, as MRI's undump_after_backslash does.
        /// </summary>
        public void String_UndumpUnicodeEncoding1() {
            TestOutput(@"
require 'ruby4'
def t; yield; rescue Exception => e; e; end
p t { '""\\u3042""'.encode('EUC-JP').undump.encoding }, t { '""\\xA4\\xA2""'.encode('EUC-JP').undump.encoding }
p t { '""\\xE3\\u3042""'.undump }, t { '""\\u3042\\xE3""'.undump }, t { '""\\x41\\u0042""'.undump }
", @"
#<Encoding:UTF-8>
#<Encoding:EUC-JP>
#<RuntimeError: hex escape and Unicode escape are mixed>
#<RuntimeError: hex escape and Unicode escape are mixed>
""AB""
");
        }

        /// <summary>
        /// String#ord and #codepoints answer the character's code in its encoding. A four byte character whose
        /// first byte is above 0x7F (GB18030's, EUC-TW's) has a code above the int range - 2217845808, where
        /// the bytes shifted into an int came out negative - and #ord of a UTF-16 or UTF-32 string held as
        /// bytes answered its first byte instead of its first character (issue 214's leftovers).
        /// </summary>
        public void String_OrdWide1() {
            TestOutput(@"
def t(l) = puts(""#{l}: #{(yield rescue [$!.class, $!.message]).inspect}"")
{ 'GB18030' => ['843130a4', '8431a430', '81308130', '90308130', 'e3329a35', 'a1a1', '61'], 'EUC-TW' => ['8ea1a1a1', '8ea2c1c1', 'a4a1'],
  'EUC-JP' => ['8fb0a1', 'a4a2'], 'Shift_JIS' => ['82a0'], 'UTF-16LE' => ['4230', '3dd800de', '6100'], 'UTF-16BE' => ['3042'],
  'UTF-32LE' => ['42300000', '00f60100'], 'UTF-32BE' => ['00003042', '0010ffff'], 'UTF-8' => ['e38182', 'f09f9880'] }.each do |e, list|
  list.each do |hex|
    s = [hex].pack('H*').force_encoding(e)
    t(""#{e} #{hex}"") { [s.ord, s.ord.class, s.codepoints, (s * 2).each_codepoint.to_a, (s + s).ord] }
  end
end
", @"
GB18030 843130a4: [ArgumentError, ""invalid byte sequence in GB18030""]
GB18030 8431a430: [2217845808, Integer, [2217845808], [2217845808, 2217845808], 2217845808]
GB18030 81308130: [2167439664, Integer, [2167439664], [2167439664, 2167439664], 2167439664]
GB18030 90308130: [2419097904, Integer, [2419097904], [2419097904, 2419097904], 2419097904]
GB18030 e3329a35: [3811744309, Integer, [3811744309], [3811744309, 3811744309], 3811744309]
GB18030 a1a1: [41377, Integer, [41377], [41377, 41377], 41377]
GB18030 61: [97, Integer, [97], [97, 97], 97]
EUC-TW 8ea1a1a1: [2392957345, Integer, [2392957345], [2392957345, 2392957345], 2392957345]
EUC-TW 8ea2c1c1: [2393031105, Integer, [2393031105], [2393031105, 2393031105], 2393031105]
EUC-TW a4a1: [42145, Integer, [42145], [42145, 42145], 42145]
EUC-JP 8fb0a1: [9416865, Integer, [9416865], [9416865, 9416865], 9416865]
EUC-JP a4a2: [42146, Integer, [42146], [42146, 42146], 42146]
Shift_JIS 82a0: [33440, Integer, [33440], [33440, 33440], 33440]
UTF-16LE 4230: [12354, Integer, [12354], [12354, 12354], 12354]
UTF-16LE 3dd800de: [128512, Integer, [128512], [128512, 128512], 128512]
UTF-16LE 6100: [97, Integer, [97], [97, 97], 97]
UTF-16BE 3042: [12354, Integer, [12354], [12354, 12354], 12354]
UTF-32LE 42300000: [12354, Integer, [12354], [12354, 12354], 12354]
UTF-32LE 00f60100: [128512, Integer, [128512], [128512, 128512], 128512]
UTF-32BE 00003042: [12354, Integer, [12354], [12354, 12354], 12354]
UTF-32BE 0010ffff: [1114111, Integer, [1114111], [1114111, 1114111], 1114111]
UTF-8 e38182: [12354, Integer, [12354], [12354, 12354], 12354]
UTF-8 f09f9880: [128512, Integer, [128512], [128512, 128512], 128512]
");
        }

        /// <summary>
        /// Integer#chr and String#&lt;&lt; with an Integer, in the multibyte encodings: MRI's rb_enc_uint_chr takes the
        /// bytes of the number and accepts them when they are exactly one well formed character of the encoding -
        /// "invalid codepoint" when they are not, "out of char range" when the number is wider than the encoding's
        /// longest character. 0x80 and 0x100 were bytes of EUC-KR, 0x8431A430 (GB18030) and 0x8EA1A1A1 (EUC-TW)
        /// were out of range, and #&lt;&lt; refused every number above the int range as a bignum (Util/chr-matrix.rb).
        /// </summary>
        public void Integer_ChrMultibyte1() {
            TestOutput(@"
def t(l)
  v = begin; yield; rescue => e; ""#{e.class}: #{e.message}""; end
  puts ""#{l}: #{v.is_a?(String) && v.encoding != Encoding::UTF_8 ? v.unpack1('H*') : v}""
end
big = 2**64
{ 'GB18030' => [0x80, 0x100, 0x8130, 0x8140, 0x817f, 0x10000, 0x81308130, 0x84328130, 0x81308140, 0xffffffff],
  'EUC-TW' => [0xa1a1, 0x8ea1, 0x8ea1a1a1, 0x8eb1a1a1], 'EUC-KR' => [0x80, 0x100, 0xa1a1, 0xfe40, 0x10000],
  'Big5' => [0xa140, 0xff40, 0x10000], 'GBK' => [0x81, 0x8130, 0x8140], 'EUC-JP' => [0x8ee0, 0x8fa1a1, 0x1000000],
  'Emacs-Mule' => [0x80, 0x81fe, 0x8180, 0x90a1a1, 0x9ae0a0, 0x9aa0a1, 0x9cf0a0a0, 0x9df4a0a0],
  'stateless-ISO-2022-JP' => [0xa1a1, 0x8ea1], 'UTF8-MAC' => [0x80, 0x3042, 0x10ffff, 0x110000],
  'UTF-8' => [0xfffffffd, 0xfffffffe], 'UTF-32BE' => [0x3042, 0xd800, 0x110000, 0x7fffffff, 0x80000000],
  'UTF-32LE' => [0x81308130], 'UTF-16LE' => [0x110000, 0x81308130], 'US-ASCII' => [0x80, 0x100] }.each do |e, list|
  (list + [2**32, big, -1]).each do |n|
    t(""#{e} #{n.to_s(16)} chr"") { n.chr(e) }
    t(""#{e} #{n.to_s(16)} <<"") { (+'').force_encoding(e) << n }
  end
end
t('concat') { (+'').force_encoding('GB18030').concat(0x41, 0x8431a430, 0xa1a1) }
t('round trip') { s = [0x84, 0x31, 0xa4, 0x30].pack('C*').force_encoding('GB18030'); [s.ord, s.ord.chr('GB18030') == s] }
# %c puts the bytes of the number and does not ask whether they are a character
t('%c EUC-KR 0x100') { format('%c'.encode('EUC-KR'), 0x100) }
t('%c Big5 0x3042') { format('<%c>'.encode('Big5'), 0x3042) }
t('%c EUC-KR 0xa1a1') { '%c'.encode('EUC-KR') % 0xa1a1 }
", @"
GB18030 80 chr: RangeError: invalid codepoint 0x80 in GB18030
GB18030 80 <<: RangeError: invalid codepoint 0x80 in GB18030
GB18030 100 chr: RangeError: invalid codepoint 0x100 in GB18030
GB18030 100 <<: RangeError: invalid codepoint 0x100 in GB18030
GB18030 8130 chr: RangeError: invalid codepoint 0x8130 in GB18030
GB18030 8130 <<: RangeError: invalid codepoint 0x8130 in GB18030
GB18030 8140 chr: 8140
GB18030 8140 <<: 8140
GB18030 817f chr: RangeError: invalid codepoint 0x817F in GB18030
GB18030 817f <<: RangeError: invalid codepoint 0x817F in GB18030
GB18030 10000 chr: RangeError: invalid codepoint 0x10000 in GB18030
GB18030 10000 <<: RangeError: invalid codepoint 0x10000 in GB18030
GB18030 81308130 chr: 81308130
GB18030 81308130 <<: 81308130
GB18030 84328130 chr: 84328130
GB18030 84328130 <<: 84328130
GB18030 81308140 chr: RangeError: invalid codepoint 0x81308140 in GB18030
GB18030 81308140 <<: RangeError: invalid codepoint 0x81308140 in GB18030
GB18030 ffffffff chr: RangeError: invalid codepoint 0xFFFFFFFF in GB18030
GB18030 ffffffff <<: RangeError: invalid codepoint 0xFFFFFFFF in GB18030
GB18030 100000000 chr: RangeError: 4294967296 out of char range
GB18030 100000000 <<: RangeError: 4294967296 out of char range
GB18030 10000000000000000 chr: RangeError: bignum out of char range
GB18030 10000000000000000 <<: RangeError: bignum out of char range
GB18030 -1 chr: RangeError: -1 out of char range
GB18030 -1 <<: RangeError: -1 out of char range
EUC-TW a1a1 chr: a1a1
EUC-TW a1a1 <<: a1a1
EUC-TW 8ea1 chr: RangeError: invalid codepoint 0x8EA1 in EUC-TW
EUC-TW 8ea1 <<: RangeError: invalid codepoint 0x8EA1 in EUC-TW
EUC-TW 8ea1a1a1 chr: 8ea1a1a1
EUC-TW 8ea1a1a1 <<: 8ea1a1a1
EUC-TW 8eb1a1a1 chr: RangeError: invalid codepoint 0x8EB1A1A1 in EUC-TW
EUC-TW 8eb1a1a1 <<: RangeError: invalid codepoint 0x8EB1A1A1 in EUC-TW
EUC-TW 100000000 chr: RangeError: 4294967296 out of char range
EUC-TW 100000000 <<: RangeError: 4294967296 out of char range
EUC-TW 10000000000000000 chr: RangeError: bignum out of char range
EUC-TW 10000000000000000 <<: RangeError: bignum out of char range
EUC-TW -1 chr: RangeError: -1 out of char range
EUC-TW -1 <<: RangeError: -1 out of char range
EUC-KR 80 chr: RangeError: invalid codepoint 0x80 in EUC-KR
EUC-KR 80 <<: RangeError: invalid codepoint 0x80 in EUC-KR
EUC-KR 100 chr: RangeError: invalid codepoint 0x100 in EUC-KR
EUC-KR 100 <<: RangeError: invalid codepoint 0x100 in EUC-KR
EUC-KR a1a1 chr: a1a1
EUC-KR a1a1 <<: a1a1
EUC-KR fe40 chr: RangeError: invalid codepoint 0xFE40 in EUC-KR
EUC-KR fe40 <<: RangeError: invalid codepoint 0xFE40 in EUC-KR
EUC-KR 10000 chr: RangeError: 65536 out of char range
EUC-KR 10000 <<: RangeError: 65536 out of char range
EUC-KR 100000000 chr: RangeError: 4294967296 out of char range
EUC-KR 100000000 <<: RangeError: 4294967296 out of char range
EUC-KR 10000000000000000 chr: RangeError: bignum out of char range
EUC-KR 10000000000000000 <<: RangeError: bignum out of char range
EUC-KR -1 chr: RangeError: -1 out of char range
EUC-KR -1 <<: RangeError: -1 out of char range
Big5 a140 chr: a140
Big5 a140 <<: a140
Big5 ff40 chr: RangeError: invalid codepoint 0xFF40 in Big5
Big5 ff40 <<: RangeError: invalid codepoint 0xFF40 in Big5
Big5 10000 chr: RangeError: 65536 out of char range
Big5 10000 <<: RangeError: 65536 out of char range
Big5 100000000 chr: RangeError: 4294967296 out of char range
Big5 100000000 <<: RangeError: 4294967296 out of char range
Big5 10000000000000000 chr: RangeError: bignum out of char range
Big5 10000000000000000 <<: RangeError: bignum out of char range
Big5 -1 chr: RangeError: -1 out of char range
Big5 -1 <<: RangeError: -1 out of char range
GBK 81 chr: RangeError: invalid codepoint 0x81 in GBK
GBK 81 <<: RangeError: invalid codepoint 0x81 in GBK
GBK 8130 chr: RangeError: invalid codepoint 0x8130 in GBK
GBK 8130 <<: RangeError: invalid codepoint 0x8130 in GBK
GBK 8140 chr: 8140
GBK 8140 <<: 8140
GBK 100000000 chr: RangeError: 4294967296 out of char range
GBK 100000000 <<: RangeError: 4294967296 out of char range
GBK 10000000000000000 chr: RangeError: bignum out of char range
GBK 10000000000000000 <<: RangeError: bignum out of char range
GBK -1 chr: RangeError: -1 out of char range
GBK -1 <<: RangeError: -1 out of char range
EUC-JP 8ee0 chr: 8ee0
EUC-JP 8ee0 <<: 8ee0
EUC-JP 8fa1a1 chr: 8fa1a1
EUC-JP 8fa1a1 <<: 8fa1a1
EUC-JP 1000000 chr: RangeError: 16777216 out of char range
EUC-JP 1000000 <<: RangeError: 16777216 out of char range
EUC-JP 100000000 chr: RangeError: 4294967296 out of char range
EUC-JP 100000000 <<: RangeError: 4294967296 out of char range
EUC-JP 10000000000000000 chr: RangeError: bignum out of char range
EUC-JP 10000000000000000 <<: RangeError: bignum out of char range
EUC-JP -1 chr: RangeError: -1 out of char range
EUC-JP -1 <<: RangeError: -1 out of char range
Emacs-Mule 80 chr: RangeError: invalid codepoint 0x80 in Emacs-Mule
Emacs-Mule 80 <<: RangeError: invalid codepoint 0x80 in Emacs-Mule
Emacs-Mule 81fe chr: 81fe
Emacs-Mule 81fe <<: 81fe
Emacs-Mule 8180 chr: RangeError: invalid codepoint 0x8180 in Emacs-Mule
Emacs-Mule 8180 <<: RangeError: invalid codepoint 0x8180 in Emacs-Mule
Emacs-Mule 90a1a1 chr: 90a1a1
Emacs-Mule 90a1a1 <<: 90a1a1
Emacs-Mule 9ae0a0 chr: 9ae0a0
Emacs-Mule 9ae0a0 <<: 9ae0a0
Emacs-Mule 9aa0a1 chr: RangeError: invalid codepoint 0x9AA0A1 in Emacs-Mule
Emacs-Mule 9aa0a1 <<: RangeError: invalid codepoint 0x9AA0A1 in Emacs-Mule
Emacs-Mule 9cf0a0a0 chr: 9cf0a0a0
Emacs-Mule 9cf0a0a0 <<: 9cf0a0a0
Emacs-Mule 9df4a0a0 chr: RangeError: invalid codepoint 0x9DF4A0A0 in Emacs-Mule
Emacs-Mule 9df4a0a0 <<: RangeError: invalid codepoint 0x9DF4A0A0 in Emacs-Mule
Emacs-Mule 100000000 chr: RangeError: 4294967296 out of char range
Emacs-Mule 100000000 <<: RangeError: 4294967296 out of char range
Emacs-Mule 10000000000000000 chr: RangeError: bignum out of char range
Emacs-Mule 10000000000000000 <<: RangeError: bignum out of char range
Emacs-Mule -1 chr: RangeError: -1 out of char range
Emacs-Mule -1 <<: RangeError: -1 out of char range
stateless-ISO-2022-JP a1a1 chr: RangeError: invalid codepoint 0xA1A1 in stateless-ISO-2022-JP
stateless-ISO-2022-JP a1a1 <<: RangeError: invalid codepoint 0xA1A1 in stateless-ISO-2022-JP
stateless-ISO-2022-JP 8ea1 chr: 8ea1
stateless-ISO-2022-JP 8ea1 <<: 8ea1
stateless-ISO-2022-JP 100000000 chr: RangeError: 4294967296 out of char range
stateless-ISO-2022-JP 100000000 <<: RangeError: 4294967296 out of char range
stateless-ISO-2022-JP 10000000000000000 chr: RangeError: bignum out of char range
stateless-ISO-2022-JP 10000000000000000 <<: RangeError: bignum out of char range
stateless-ISO-2022-JP -1 chr: RangeError: -1 out of char range
stateless-ISO-2022-JP -1 <<: RangeError: -1 out of char range
UTF8-MAC 80 chr: c280
UTF8-MAC 80 <<: c280
UTF8-MAC 3042 chr: e38182
UTF8-MAC 3042 <<: e38182
UTF8-MAC 10ffff chr: f48fbfbf
UTF8-MAC 10ffff <<: f48fbfbf
UTF8-MAC 110000 chr: RangeError: 1114112 out of char range
UTF8-MAC 110000 <<: RangeError: 1114112 out of char range
UTF8-MAC 100000000 chr: RangeError: 4294967296 out of char range
UTF8-MAC 100000000 <<: RangeError: 4294967296 out of char range
UTF8-MAC 10000000000000000 chr: RangeError: bignum out of char range
UTF8-MAC 10000000000000000 <<: RangeError: bignum out of char range
UTF8-MAC -1 chr: RangeError: -1 out of char range
UTF8-MAC -1 <<: RangeError: -1 out of char range
UTF-8 fffffffd chr: RangeError: 4294967293 out of char range
UTF-8 fffffffd <<: RangeError: 4294967293 out of char range
UTF-8 fffffffe chr: RangeError: invalid codepoint 0xFFFFFFFE in UTF-8
UTF-8 fffffffe <<: RangeError: invalid codepoint 0xFFFFFFFE in UTF-8
UTF-8 100000000 chr: RangeError: 4294967296 out of char range
UTF-8 100000000 <<: RangeError: 4294967296 out of char range
UTF-8 10000000000000000 chr: RangeError: bignum out of char range
UTF-8 10000000000000000 <<: RangeError: bignum out of char range
UTF-8 -1 chr: RangeError: -1 out of char range
UTF-8 -1 <<: RangeError: -1 out of char range
UTF-32BE 3042 chr: 00003042
UTF-32BE 3042 <<: 00003042
UTF-32BE d800 chr: RangeError: invalid codepoint 0xD800 in UTF-32BE
UTF-32BE d800 <<: RangeError: invalid codepoint 0xD800 in UTF-32BE
UTF-32BE 110000 chr: RangeError: invalid codepoint 0x110000 in UTF-32BE
UTF-32BE 110000 <<: RangeError: invalid codepoint 0x110000 in UTF-32BE
UTF-32BE 7fffffff chr: RangeError: invalid codepoint 0x7FFFFFFF in UTF-32BE
UTF-32BE 7fffffff <<: RangeError: invalid codepoint 0x7FFFFFFF in UTF-32BE
UTF-32BE 80000000 chr: 80000000
UTF-32BE 80000000 <<: 80000000
UTF-32BE 100000000 chr: RangeError: 4294967296 out of char range
UTF-32BE 100000000 <<: RangeError: 4294967296 out of char range
UTF-32BE 10000000000000000 chr: RangeError: bignum out of char range
UTF-32BE 10000000000000000 <<: RangeError: bignum out of char range
UTF-32BE -1 chr: RangeError: -1 out of char range
UTF-32BE -1 <<: RangeError: -1 out of char range
UTF-32LE 81308130 chr: 30813081
UTF-32LE 81308130 <<: 30813081
UTF-32LE 100000000 chr: RangeError: 4294967296 out of char range
UTF-32LE 100000000 <<: RangeError: 4294967296 out of char range
UTF-32LE 10000000000000000 chr: RangeError: bignum out of char range
UTF-32LE 10000000000000000 <<: RangeError: bignum out of char range
UTF-32LE -1 chr: RangeError: -1 out of char range
UTF-32LE -1 <<: RangeError: -1 out of char range
UTF-16LE 110000 chr: RangeError: invalid codepoint 0x110000 in UTF-16LE
UTF-16LE 110000 <<: RangeError: invalid codepoint 0x110000 in UTF-16LE
UTF-16LE 81308130 chr: RangeError: invalid codepoint 0x81308130 in UTF-16LE
UTF-16LE 81308130 <<: RangeError: invalid codepoint 0x81308130 in UTF-16LE
UTF-16LE 100000000 chr: RangeError: 4294967296 out of char range
UTF-16LE 100000000 <<: RangeError: 4294967296 out of char range
UTF-16LE 10000000000000000 chr: RangeError: bignum out of char range
UTF-16LE 10000000000000000 <<: RangeError: bignum out of char range
UTF-16LE -1 chr: RangeError: -1 out of char range
UTF-16LE -1 <<: RangeError: -1 out of char range
US-ASCII 80 chr: RangeError: invalid codepoint 0x80 in US-ASCII
US-ASCII 80 <<: 80
US-ASCII 100 chr: RangeError: 256 out of char range
US-ASCII 100 <<: RangeError: 256 out of char range
US-ASCII 100000000 chr: RangeError: 4294967296 out of char range
US-ASCII 100000000 <<: RangeError: 4294967296 out of char range
US-ASCII 10000000000000000 chr: RangeError: bignum out of char range
US-ASCII 10000000000000000 <<: RangeError: bignum out of char range
US-ASCII -1 chr: RangeError: -1 out of char range
US-ASCII -1 <<: RangeError: -1 out of char range
concat: 418431a430a1a1
round trip: [2217845808, true]
%c EUC-KR 0x100: 0100
%c Big5 0x3042: 3c30423e
%c EUC-KR 0xa1a1: a1a1
");
        }

        /// <summary>
        /// The %c directive, as MRI's rb_str_format has it: the argument is read as an int (NUM2INT, with its
        /// messages), the encoding of the format says how long the character of the number is, "invalid character"
        /// when it has no length for it, and that many bytes of the number are put without asking whether they are a
        /// well formed character - "\x80" in Shift_JIS, "\x01\x00" in EUC-KR, a surrogate's three bytes in UTF-8. A
        /// single byte encoding refuses a number above 0xFF with a RangeError instead (Util/format-c-matrix.rb).
        /// </summary>
        public void Format_Char2() {
            TestOutput(@"
def t(l)
  v = begin; yield; rescue => e; [e.class, e.message]; end
  puts ""#{l}: #{v.is_a?(String) ? ""#{v.encoding} #{v.unpack1('H*')}"" : v.inspect}""
end
{ 'UTF-8' => [0x41, 0x3042, 0xd800, 0x10ffff, 0x110000], 'US-ASCII' => [0x7f, 0x80, 0x100], 'BINARY' => [0xff, 0x100],
  'ISO-8859-1' => [0xe9, 0x100], 'Shift_JIS' => [0x80, 0x81, 0xa1, 0x100, 0x3042, 0x8140, 0x81fd, 0x10000],
  'EUC-JP' => [0x80, 0x8140, 0x8ee0, 0xa1a1, 0x8fa1a1, 0x10000], 'EUC-KR' => [0x80, 0x100, 0xa1a1, 0x10000],
  'GB18030' => [0x100, 0x10000, 0x7fffffff], 'EUC-TW' => [0x8ea1a1], 'Emacs-Mule' => [0x80, 0x8100, 0x10ffff, 0x7fffffff],
  'CESU-8' => [0x10000], 'UTF8-MAC' => [0x3042] }.each do |e, list|
  list.each { |n| t(""#{e} #{n.to_s(16)}"") { '<%c>'.dup.force_encoding(e) % n } }
end
[2**31, -2**31, -2**31 - 1, 2**64, -1, 65.9, 1e20, :a, 'xy', ''].each { |v| t(""arg #{v.inspect}"") { '%c' % [v] } }
t('width') { '%3c|%-3c|' % [0x3042, 0x41] }
t('width Shift_JIS') { format('%-3c|'.dup.force_encoding('Shift_JIS'), 0xa1) }
t('then %s') { format('%c%s'.dup.force_encoding('Shift_JIS'), 0xa1, [0x3042].pack('U')) }
t('then %s US-ASCII') { format('%c%s'.dup.force_encoding('US-ASCII'), 0xa1, [0x3042].pack('U')) }
t('%s then %c') { format('%s%c'.dup.force_encoding('Shift_JIS'), [0x3042].pack('U'), 0xa1).unpack1('H*') }
t('bytes kept') { [format('<%c>'.dup.force_encoding('EUC-JP'), 0x8fa2b7), format('<%c>'.dup.force_encoding('CESU-8'), 0xdc80)].map { _1.unpack1('H*') } }
", @"
UTF-8 41: UTF-8 3c413e
UTF-8 3042: UTF-8 3ce381823e
UTF-8 d800: UTF-8 3ceda0803e
UTF-8 10ffff: UTF-8 3cf48fbfbf3e
UTF-8 110000: [ArgumentError, ""invalid character""]
US-ASCII 7f: US-ASCII 3c7f3e
US-ASCII 80: ASCII-8BIT 3c803e
US-ASCII 100: [RangeError, ""256 out of char range""]
BINARY ff: ASCII-8BIT 3cff3e
BINARY 100: [RangeError, ""256 out of char range""]
ISO-8859-1 e9: ISO-8859-1 3ce93e
ISO-8859-1 100: [RangeError, ""256 out of char range""]
Shift_JIS 80: Shift_JIS 3c803e
Shift_JIS 81: [ArgumentError, ""invalid character""]
Shift_JIS a1: Shift_JIS 3ca13e
Shift_JIS 100: [ArgumentError, ""invalid character""]
Shift_JIS 3042: Shift_JIS 3c30423e
Shift_JIS 8140: Shift_JIS 3c81403e
Shift_JIS 81fd: [ArgumentError, ""invalid character""]
Shift_JIS 10000: [ArgumentError, ""invalid character""]
EUC-JP 80: [ArgumentError, ""invalid character""]
EUC-JP 8140: [ArgumentError, ""invalid character""]
EUC-JP 8ee0: EUC-JP 3c8ee03e
EUC-JP a1a1: EUC-JP 3ca1a13e
EUC-JP 8fa1a1: EUC-JP 3c8fa1a13e
EUC-JP 10000: [ArgumentError, ""invalid character""]
EUC-KR 80: EUC-KR 3c803e
EUC-KR 100: EUC-KR 3c01003e
EUC-KR a1a1: EUC-KR 3ca1a13e
EUC-KR 10000: [ArgumentError, ""invalid character""]
GB18030 100: GB18030 3c01003e
GB18030 10000: GB18030 3c0100003e
GB18030 7fffffff: GB18030 3c7fffffff3e
EUC-TW 8ea1a1: EUC-TW 3c8ea1a13e
Emacs-Mule 80: [ArgumentError, ""invalid character""]
Emacs-Mule 8100: Emacs-Mule 3c81003e
Emacs-Mule 10ffff: Emacs-Mule 3c10ff3e
Emacs-Mule 7fffffff: Emacs-Mule 3c7fffff3e
CESU-8 10000: CESU-8 3ceda080edb0803e
UTF8-MAC 3042: UTF8-MAC 3ce381823e
arg 2147483648: [RangeError, ""integer 2147483648 too big to convert to 'int'""]
arg -2147483648: [ArgumentError, ""invalid character""]
arg -2147483649: [RangeError, ""integer -2147483649 too small to convert to 'int'""]
arg 18446744073709551616: [RangeError, ""bignum too big to convert into 'long'""]
arg -1: [ArgumentError, ""invalid character""]
arg 65.9: UTF-8 41
arg 1.0e+20: [RangeError, ""float 1e+20 out of range of integer""]
arg :a: [TypeError, ""no implicit conversion of Symbol into Integer""]
arg ""xy"": UTF-8 78
arg """": UTF-8 
width: UTF-8 2020e381827c4120207c
width Shift_JIS: Shift_JIS a120207c
then %s: [Encoding::CompatibilityError, ""incompatible character encodings: Shift_JIS and UTF-8""]
then %s US-ASCII: [Encoding::CompatibilityError, ""incompatible character encodings: BINARY (ASCII-8BIT) and UTF-8""]
%s then %c: US-ASCII 65333831383263326131
bytes kept: [""3c8fa2b73e"", ""3cedb2803e""]
");
        }
    }
}