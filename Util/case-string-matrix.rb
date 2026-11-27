# Differential matrix for `case' over String literals (CaseExpression, CaseStringSite: a `case' whose
# `when's are all ASCII String literals looks its subject up once, as MRI's opt_case_dispatch does).
#
#   ruby    Util/case-string-matrix.rb > /tmp/case.mri
#   ./ir.sh Util/case-string-matrix.rb > /tmp/case.ir
#   IR_CASE_DISPATCH=0 ./ir.sh Util/case-string-matrix.rb > /tmp/case.off
#   diff /tmp/case.mri /tmp/case.ir
#
# Every subject goes through each `case' in a method the method JIT compiles and in one it declines (a
# `raise' with a call in its argument, as AWFY Json's read_value), many times, so that both compilers,
# both tiers and the sites' bindings are exercised; then String#=== is redefined, prepended over and
# refined, and the same `case's run again.

class SubStr < String; end
$calls = 0
class ToStr; def initialize(s) = @s = s; def to_str = @s; def ==(o) = ($calls += 1; @s == o); end
class EqOnly; def ==(o) = true; end

SUBJECTS = {
  "read q" => 'a"b'[1], "read n" => 'xn'[1], "read digit" => '19'[1], "read dash" => '-'[0], "read brace" => '{x'[0],
  "read other" => 'z'[0], "lit q" => '"', "built q" => +"" << '"', "multi" => "true", "multi2" => "null",
  "multi3" => "nul", "empty" => "", "utf8" => "é", "utf8 n" => "ñ", "binary q" => '"'.b, "binary hi" => "\xE9".b,
  "broken" => "\xE9".dup.force_encoding("UTF-8"), "usascii q" => '"'.dup.force_encoding("US-ASCII"),
  "utf16 q" => '"'.encode("UTF-16LE"), "eucjp" => "\xA4\xA2".dup.force_encoding("EUC-JP"), "frozen q" => '"'.freeze,
  "substr q" => SubStr.new('"'), "nil" => nil, "sym" => :n, "int" => 1, "float" => 1.0, "tostr" => ToStr.new('"'),
  "eqonly" => EqOnly.new, "chars n" => "n".chars[0], "slice multi" => "xtrue"[1..],
}

def value_case(c)
  case c
  when 'n' then :null
  when 't' then :true
  when 'f' then :false
  when '"' then :string
  when '[' then :array
  when '{' then :object
  when '-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' then :number
  else :other
  end
end

def value_case_declined(c)
  raise expected('value') if c.equal?(:never)
  case c
  when 'n' then :null
  when 't' then :true
  when '"' then :string
  when '-', '0', '1', '9' then :number
  else :other
  end
end
def expected(x) = "expected #{x}"

def multi_case(c)
  raise expected('x') if c.equal?(:never)
  case c
  when 'true', 'null' then :word
  when 'n', 'nul' then :short
  when '' then :empty
  when 'true' then :duplicate
  end
end

def dup_case(c)
  case c
  when '"' then 1
  when 'x', '"' then 2
  end
end

def mixed_case(c)
  case c
  when 'n' then 1
  when 'é' then 2
  when /\d/ then 3
  else 4
  end
end

def no_value_case(c)
  case
  when c == '"' then 1
  else 2
  end
end

CASES = %i[value_case value_case_declined multi_case dup_case mixed_case no_value_case]

def run(label)
  puts "== #{label}"
  SUBJECTS.each do |name, s|
    CASES.each do |m|
      $calls = 0
      r = begin
        a = 30.times.map { send(m, s) }.uniq
        a.size == 1 ? a[0].inspect : "UNSTABLE #{a.inspect}"
      rescue Exception => e
        "#{e.class}: #{e.message}"
      end
      puts "#{name}\t#{m}\t#{r}\tcalls=#{$calls}"
    end
  end
end

run "library String#==="

eval <<~'RUBY'
  # frozen_string_literal: true
  def frozen_case(c)
    case c
    when 'n' then :null
    when '"' then :string
    else :other
    end
  end
RUBY
puts "== frozen_string_literal"
SUBJECTS.each { |name, s| puts "#{name}\tfrozen_case\t#{(30.times.map { frozen_case(s) }.uniq.inspect rescue $!.class)}" }

module Ref
  refine String do
    def ===(o) = ($calls += 1; (o.respond_to?(:to_str) && o.to_str == "z") || super)
  end
end
using Ref
def refined_case(c)
  case c
  when 'n' then :null
  when '"' then :string
  else :other
  end
end
puts "== refined String#==="
SUBJECTS.each { |name, s| $calls = 0; r = (30.times.map { refined_case(s) }.uniq.inspect rescue $!.class); puts "#{name}\trefined_case\t#{r}\tcalls=#{$calls}" }
run "after `using' (the methods above were defined before it)"

module Loud
  def ===(o)
    $calls += 1
    super
  end
end
String.prepend(Loud)
run "String#=== prepended over"

# last: the library String#=== is gone for good
class String
  def ===(o) = ($calls += 1; o.is_a?(String) ? (self == o || (o.size == 1 && o == "z")) : false)
end
run "String#=== redefined (z matches every one-character literal; Loud still prepended)"
