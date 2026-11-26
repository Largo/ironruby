# Expression matrix for #dup of a String, a plain Object and an Array (perf/strdup's fast path,
# IR_FAST_DUP), with #clone beside it: what the copy holds - content, encoding, broken bytes,
# frozen and chilled state, instance variables, singleton methods - and that initialize_dup and
# initialize_copy are still called when they are not the built-ins: redefined in String, Object
# or Kernel, through a prepended or included module, on a subclass, refined, traced by
# TracePoint, and restored again. (Util/hash-dup-matrix.rb does the same for Hash.)
#
#   ruby   Util/dup-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/dup-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# Every line is "<source> => <result>" or "<source> !! <ExceptionClass>: <message>". The rows of
# EXPRESSIONS run cold, again and 200 times, and a line says so when a later answer differs from
# the first; those of ONCE change a hook for good and undo it, in order, each run once with its
# dup warm (200 times).
# -*- frozen_string_literal: false -*-

class SubString < String; end
class CopyString < String
  def initialize_copy(o) = (super; @copied = true; self)
end
class Plain
  def initialize(a = 1) = (@a = a)
  attr_reader :a
end
class PlainCopy < Plain
  def initialize_copy(o) = (super; @a = [:copied, o.a]; self)
end
module StrHook
  def initialize_copy(o) = (super; concat("+pre"); self)
end
module StrInc
  def initialize_copy(o) = (super; concat("+inc"); self)
end
module ObjHook
  def initialize_copy(o) = (super; @hooked = true; self)
end
module RefineDup
  refine String do
    def initialize_copy(o) = (super; concat("+ref"); self)
  end
end
module UsingRefineDup
  using RefineDup
  def self.dup_it = "abc".dup
end
S = "abc"
B = "a\xffb".b
U = "\xff\xfe".dup.force_encoding("UTF-8")
O = Plain.new(5)
A = [1, [2], "s"]
def dup_n(x) = (r = nil; 200.times { r = x.dup }; r)
def dups(x) = (d = x.dup; [d, d.class, d.frozen?, d.equal?(x)])
def ivars(x) = x.instance_variables.map { |v| [v, x.instance_variable_get(v)] }

EXPRESSIONS = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  dups(S)
  dups("lit")
  (d = "lit".dup; d << "x"; d)
  dups("x".freeze)
  ("x".freeze.dup << "y")
  (s = "chill"; d = s.dup; d << "!"; [d, s])
  dups(:sym.to_s)
  dups(1.to_s)
  (d = S.dup; d << "d"; [S, d])
  (d = S.dup; d.replace("q"); [S, d])
  [B.dup, B.dup.encoding, B.dup.valid_encoding?]
  [U.dup.encoding, U.dup.valid_encoding?, U.dup.bytes]
  ["é".dup.encoding, "é".dup.ascii_only?, "e".dup.ascii_only?, "é".encode("UTF-16LE").dup.encoding]
  (s = "abc".dup; s.instance_variable_set(:@x, 1); ivars(s.dup))
  (s = "abc".dup; def s.extra = 1; [s.dup.respond_to?(:extra), s.clone.respond_to?(:extra)])
  (s = "abc".dup; s.singleton_class.define_method(:initialize_copy) { |o| super(o); concat("!") }; [s.dup, s.clone])
  [SubString.new("q").dup, SubString.new("q").dup.class]
  [CopyString.new("q").dup, CopyString.new("q").dup.instance_variable_get(:@copied)]
  ("a" * 1000).dup.size
  [String.instance_method(:dup).owner, String.instance_method(:dup).arity, Object.instance_method(:dup).owner]
  (s = "a" * 100; d = s.dup; d[0] = "b"; [s[0], d[0]])
  S.clone
  S.freeze.clone.frozen?
  "lit".clone.frozen?
  (s = "abc".freeze; [s.clone(freeze: false).frozen?, s.clone(freeze: true).frozen?])
  UsingRefineDup.dup_it
  (calls = []; s = "s".dup; tp = TracePoint.new(:c_call) { |t| calls << t.method_id if t.method_id.to_s.start_with?("initialize") }; tp.enable { s.dup }; calls)
  (calls = []; s = "s".dup; s.instance_variable_set(:@i, 1); tp = TracePoint.new(:c_call) { |t| calls << t.method_id if t.method_id.to_s.start_with?("initialize") }; tp.enable { s.dup }; calls)
  (calls = []; o = Object.new; tp = TracePoint.new(:c_call) { |t| calls << t.method_id if t.method_id.to_s.start_with?("initialize") }; tp.enable { o.dup }; calls)
  Thread.new { 100.times.map { S.dup.size }.uniq }.value
  # --- a plain object
  (d = O.dup; [d.class, d.a, d.equal?(O), ivars(d)])
  (o = Plain.new([1]); d = o.dup; d.a.equal?(o.a))
  (o = Plain.new.freeze; [o.dup.frozen?, o.clone.frozen?])
  (o = Plain.new; def o.extra = 1; [o.dup.respond_to?(:extra), o.clone.respond_to?(:extra)])
  (o = Plain.new; o.singleton_class.define_method(:initialize_copy) { |x| super(x); @sing = 1 }; [ivars(o.dup), ivars(o.clone)])
  (d = PlainCopy.new(3).dup; d.a)
  Object.new.dup.class
  BasicObject.new.__send__(:dup) rescue "#{$!.class}: #{$!.message}"
  (o = Object.new; o.instance_variable_set(:@q, 2); ivars(o.dup))
  Class.new(Plain).new(7).dup.a
  # --- an Array
  (d = A.dup; [d, d.equal?(A), d[1].equal?(A[1])])
  (d = A.dup; d << 4; [A.size, d.size])
  [[].dup, [1].freeze.dup.frozen?, [1].freeze.clone.frozen?]
RUBY

ONCE = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  [dup_n(S), dup_n(O).a, dup_n(A)]
  (class String; alias_method :__saved_copy__, :initialize_copy; def initialize_copy(o) = (__saved_copy__(o); concat("+mine"); self); end; dup_n(S))
  (class String; alias_method :initialize_copy, :__saved_copy__; remove_method :__saved_copy__; end; dup_n(S))
  (class Object; def initialize_copy(o) = (super; @obj = 1; self); end; [dup_n(O).instance_variable_get(:@obj), dup_n(S)])
  (class Object; remove_method :initialize_copy; end; [dup_n(O).instance_variables, dup_n(S)])
  (class Object; def initialize_dup(o) = (super; @odup = 1; self); end; [dup_n(O).instance_variable_get(:@odup), dup_n(S).instance_variable_get(:@odup)])
  (class Object; remove_method :initialize_dup; end; [dup_n(O).instance_variables, dup_n(S).instance_variables])
  (module Kernel; alias_method :__saved_dup__, :initialize_dup; def initialize_dup(o) = (__saved_dup__(o); @kdup = 1; self); end; [dup_n(O).instance_variable_get(:@kdup), dup_n(S).instance_variable_get(:@kdup)])
  (module Kernel; alias_method :initialize_dup, :__saved_dup__; remove_method :__saved_dup__; end; [dup_n(O).instance_variables, dup_n(S)])
  (module Kernel; alias_method :__saved_kcopy__, :initialize_copy; def initialize_copy(o) = (__saved_kcopy__(o); @kcopy = 1; self); end; [dup_n(O).instance_variable_get(:@kcopy), dup_n(S)])
  (module Kernel; alias_method :initialize_copy, :__saved_kcopy__; remove_method :__saved_kcopy__; end; dup_n(O).instance_variables)
  (String.prepend(StrHook); dup_n(S))
  (StrHook.send(:remove_method, :initialize_copy); dup_n(S))
  (String.include(StrInc); dup_n(S))
  (Plain.include(ObjHook); dup_n(O).instance_variable_get(:@hooked))
  (ObjHook.send(:remove_method, :initialize_copy); dup_n(O).instance_variables)
  [S.clone, O.clone.a, "x".freeze.clone.frozen?]
RUBY

def clean(v) = v.inspect.gsub(/0x\h+/, "XX")
def run(source)
  "=> #{clean(eval(source))}"
rescue Exception => e
  "!! #{e.class}: #{e.message.gsub(/0x\h+/, "XX")}"
end

EXPRESSIONS.each do |source|
  first = run(source)
  second = run(source)
  warm = nil
  200.times { warm = run(source) }
  line = "#{source} #{first}"
  line += "   [second: #{second}]" if second != first
  line += "   [warm: #{warm}]" if warm != first
  puts line
end
ONCE.each { |source| puts "#{source} #{run(source)}" }
puts "done"
