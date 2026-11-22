# Differential matrix for the method JIT (-X:JIT, Src/Ruby/Runtime/Jit/): a typed body folds the constants it
# reads (JitCompiler.ResolveConstant) under the runtime's constant version, which moves whenever any constant is
# set or removed, a module is mixed in or an object extended. Each row warms a method of its own a few thousand
# times (the JIT's threshold is 40 calls), then does something to constants, then calls the method again, warms
# it again and calls it again, and prints what each call answered: the value, or the NameError's class and
# message. Run under CRuby and under ./ir.sh - with the JIT on and off - and diff:
#
#   ruby Util/jit-const-matrix.rb > /tmp/mri.txt; echo $?
#   ./ir.sh Util/jit-const-matrix.rb > /tmp/jit.txt; echo $?
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-const-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# All must exit 0, end with the row `end', and be identical. Warnings ("already initialized constant") go to
# stderr, which is not compared. What the JIT did goes to stderr too:
#
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-const-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]'

$stdout.sync = true
require 'tmpdir'

WARM = 3000

class Row
  attr_reader :out
  def initialize; @out = []; end
  def one
    yield
  rescue NameError => e
    [e.class, e.message]
  end
  # the method called WARM times; the last answer is recorded
  def warm(n = WARM)
    v = nil
    n.times { v = one { yield } }
    @out << v
  end
  # one call
  def call(&b)
    @out << one(&b)
  end
end

def row(label)
  r = Row.new
  yield r
  puts "#{label}: #{r.out.inspect}"
end

# warm, event, call, warm again, call again
def around(label, event)
  row(label) do |r|
    r.warm { yield }
    event.call
    r.call { yield }
    r.warm { yield }
    r.call { yield }
  end
end

# ---------------------------------------------------------------- 1. events that must not change the answer

TOPV = "topv"
module QE; module B; R = "qr"; end; end
EVENTS = [
  [:const_set, -> { Object.const_set(:UNRELATED_1, 1) }],
  [:extend, -> { Object.new.extend(Comparable) }],
  [:include, -> { Class.new.include(Comparable) }],
  [:def, -> { Object.class_eval "def __unrelated_def; end" }],
  [:class_body, -> { eval "class UnrelatedBody; Z = 1; end" }],
  [:require_set, -> { require 'set' }],
]
EVENTS.each_with_index do |(name, event), i|
  eval <<~RUBY
    class Ev#{i}
      V = "v#{i}"
      def lex; V; end
      def top; TOPV; end
      def qual; QE::B::R; end
      def glob; ::TOPV; end
    end
  RUBY
  o = Object.const_get("Ev#{i}").new
  around("1. #{name}, lexical", event) { o.lex }
  around("1. #{name}, top-level", event) { o.top }
  around("1. #{name}, qualified", event) { o.qual }
  around("1. #{name}, ::X", event) { o.glob }
end

# ---------------------------------------------------------------- 2. the constant itself

class C2a; X = 1; def m; X; end; end
o = C2a.new
around("2. removed and set again", -> { C2a.send(:remove_const, :X); C2a.const_set(:X, 2) }) { o.m }

class C2b; X = "s"; def m; X; end; end
o = C2b.new
old = C2b::X
around("2. set to an equal object", -> { C2b.send(:remove_const, :X); C2b.const_set(:X, old.dup) }) { [o.m, o.m.equal?(old)] }

class C2c; X = 1; def m; X; end; end
o = C2c.new
around("2. removed", -> { C2c.send(:remove_const, :X) }) { o.m }

class C2d; X = 1; def m; X; end; end
o = C2d.new
around("2. reassigned in place", -> { C2d.const_set(:X, 3) }) { o.m }

# ---------------------------------------------------------------- 3. shadowing

XS3 = :top
module M3; class A; def m; XS3; end; end; end
o = M3::A.new
row("3. lexical: top, M::X, A::X, A::X removed") do |r|
  r.warm { o.m }
  M3.const_set(:XS3, :m)
  r.call { o.m }; r.warm { o.m }
  M3::A.const_set(:XS3, :a)
  r.call { o.m }; r.warm { o.m }
  M3::A.send(:remove_const, :XS3)
  r.call { o.m }; r.warm { o.m }
end

XI3 = :top
module ModXI3; XI3 = :mod; end
class AI3; def m; XI3; end; end
o = AI3.new
around("3. arrives by include", -> { AI3.include(ModXI3) }) { o.m }

XB3 = :top
class BS3; end
class AS3 < BS3; def m; XB3; end; end
o = AS3.new
around("3. superclass gains it", -> { BS3.const_set(:XB3, :sup) }) { o.m }

XP3 = :top
module ModXP3; XP3 = :pre; end
class AP3; def m; XP3; end; end
o = AP3.new
around("3. arrives by prepend", -> { AP3.prepend(ModXP3) }) { o.m }

XG3 = :top
class SG3; def self.m; XG3; end; end
around("3. singleton method", -> { SG3.const_set(:XG3, :sg) }) { SG3.m }

XE3 = :top
class CE3; end
module Other3; CE3.class_eval { def m; XE3; end }; end
o = CE3.new
row("3. class_eval in another lexical scope: top, CE3::X, Other3::X") do |r|
  r.warm { o.m }
  CE3.const_set(:XE3, :ce)
  r.call { o.m }; r.warm { o.m }
  Other3.const_set(:XE3, :other)
  r.call { o.m }; r.warm { o.m }
end

# ---------------------------------------------------------------- 4. qualified reads

module Q4; module B; X = 1; end; end
class C4; def m; Q4::B::X; end; end
o = C4.new
row("4. A::B::X: B::X replaced, A::B replaced, X removed") do |r|
  r.warm { o.m }
  Q4::B.send(:remove_const, :X); Q4::B.const_set(:X, 2)
  r.call { o.m }; r.warm { o.m }
  Q4.send(:remove_const, :B); Q4.const_set(:B, Module.new { const_set(:X, 3) })
  r.call { o.m }; r.warm { o.m }
  Q4::B.send(:remove_const, :X)
  r.call { o.m }; r.warm { o.m }
end

G4 = 1
class C4g; G4 = :shadow; def m; ::G4; end; end
o = C4g.new
around("4. ::X reassigned", -> { Object.send(:remove_const, :G4); Object.const_set(:G4, 2) }) { o.m }

# ---------------------------------------------------------------- 5. private_constant

class P5; X = 1; def m; X; end; end
class P5o; def m; P5::X; end; end
o, e = P5.new, P5o.new
row("5. private_constant: lexical, then A::X from outside") do |r|
  r.warm { o.m }; r.warm { e.m }
  P5.send(:private_constant, :X)
  r.call { o.m }; r.call { e.m }
  r.warm { o.m }; r.warm { e.m }
end

# ---------------------------------------------------------------- 6. autoload

dir = Dir.mktmpdir
File.write(File.join(dir, "au6.rb"), "class AU6; XA6 = :auto; end\n")
File.write(File.join(dir, "au6b.rb"), "class AU6b; XB6 = :auto2; end\n")
XA6 = :top
class AU6; def m; XA6; end; end
o = AU6.new
around("6. autoload in a nearer scope", -> { AU6.autoload(:XA6, File.join(dir, "au6.rb")) }) { o.m }

class AU6b; XB6 = 1; def m; XB6; end; end
o = AU6b.new
around("6. autoload of the constant itself", -> { AU6b.send(:remove_const, :XB6); AU6b.autoload(:XB6, File.join(dir, "au6b.rb")) }) { o.m }

# ---------------------------------------------------------------- 7. const_missing

$missing = []
class CM7; XM7 = 1; def m; XM7; end; end
o = CM7.new
row("7. const_missing") do |r|
  r.warm { o.m }
  CM7.send(:remove_const, :XM7)
  def CM7.const_missing(n); $missing << n; :missing; end
  r.call { o.m }; r.call { o.m }; r.call { o.m }
  r.out << $missing.dup
  r.warm(100) { o.m }
  r.out << $missing.size
end

# ---------------------------------------------------------------- 8. when, Array.new, values

class W8; K = Integer; def m(x); case x when K then :yes else :no end; end; end
o = W8.new
row("8. when C, C reassigned") do |r|
  r.warm { o.m(1) }
  W8.send(:remove_const, :K); W8.const_set(:K, String)
  r.call { o.m(1) }; r.call { o.m("s") }
  r.warm { o.m(1) }; r.call { o.m("s") }
end

class AN8; def m; Array.new(2); end; end
o = AN8.new
around("8. Array.new, Array shadowed", -> { AN8.const_set(:Array, Struct.new(:n)) }) { o.m }

class LV8
  XN = nil; XF = false; XI = 7; XD = 1.5; XS = :s
  def n; XN; end
  def f; XF; end
  def i; XI; end
  def d; XD; end
  def s; XS; end
end
o = LV8.new
[[:n, :XN, 0], [:f, :XF, true], [:i, :XI, 8], [:d, :XD, 2.5], [:s, :XS, :t]].each do |m, c, nv|
  row("8. a constant holding #{LV8.const_get(c).inspect}: unrelated change, then reassigned") do |r|
    r.warm { o.__send__(m) }
    Object.const_set(:"UNRELATED_8#{m}", 1)
    r.call { o.__send__(m) }; r.warm { o.__send__(m) }
    LV8.send(:remove_const, c); LV8.const_set(c, nv)
    r.call { o.__send__(m) }; r.warm { o.__send__(m) }
  end
end

# ---------------------------------------------------------------- 9. a callee reads the constant

class BI9; XQ = 1; def inner; XQ; end; def outer(x); inner + x; end; end
o = BI9.new
row("9. callee reads it: unrelated change, then reassigned") do |r|
  r.warm { o.outer(1) }
  Object.const_set(:UNRELATED_9, 1)
  r.call { o.outer(1) }; r.warm { o.outer(1) }
  BI9.send(:remove_const, :XQ); BI9.const_set(:XQ, 10)
  r.call { o.outer(1) }; r.warm { o.outer(1) }
end

# ---------------------------------------------------------------- 10. two threads

class TH10; XT = :a; def m; XT; end; end
o = TH10.new
WARM.times { o.m }
setter = Thread.new { 2000.times { |i| Object.const_set(:"THR10_#{i}", i) } }
wrong = 0
200_000.times { wrong += 1 unless o.m == :a }
setter.join
puts "10. unrelated constants set on another thread: wrong=#{wrong}"

class TH10b; XT = :a; def m; XT; end; end
o = TH10b.new
WARM.times { o.m }
setter = Thread.new do
  2000.times do |i|
    TH10b.send(:remove_const, :XT)
    TH10b.const_set(:XT, i.even? ? :b : :a)
  end
end
bad = 0
200_000.times do
  begin
    v = o.m
    bad += 1 unless v == :a || v == :b
  rescue NameError
    # between the remove and the set
  end
end
setter.join
puts "10. the constant alternated on another thread: bad=#{bad} now=#{o.m.inspect}"

# ---------------------------------------------------------------- 11. deprecate_constant (last: it is for good)

Warning[:deprecated] = true
$warnings = []
def Warning.warn(msg, **)
  $warnings << msg.sub(/\A.*?warning: /, '')
end
class DP11; X = 1; def m; X; end; end
o = DP11.new
row("11. deprecate_constant: 3 reads") do |r|
  r.warm { o.m }
  $warnings.clear
  DP11.deprecate_constant(:X)
  r.call { o.m }; r.call { o.m }; r.call { o.m }
  # (CRuby 4.0 warns at the first read only, IronRuby at every read: only that it warned is printed)
  r.out << !$warnings.empty? << $warnings.uniq
end

puts 'end'
