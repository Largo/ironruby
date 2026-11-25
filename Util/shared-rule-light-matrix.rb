# Differential matrix for calls on a CLR receiver (an Integer) from methods that run without a scope (a light frame,
# MethodDefinition.IsScopeFree), whose sites share one rule per binder (RubyCallAction.BindShared) and find it in the
# binder's rule cache at their first call. Run it under CRuby and under ./ir.sh with light frames on and off, and diff:
#
#   ruby Util/shared-rule-light-matrix.rb > mri.txt
#   ./ir.sh Util/shared-rule-light-matrix.rb > on.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/shared-rule-light-matrix.rb > off.txt
#   diff mri.txt on.txt && diff mri.txt off.txt
#
# A shared rule for an Integer receiver tests the class version only, and the version set it holds is filled by binds.
# Once the interpreted rule is compiled (on a pool thread), the compiled one goes to the binder's cache too, and a new
# site's first call takes it from there without a bind. Each probe makes the rule hot from many new sites and one hot
# site, waits for the compile, then changes the hierarchy and calls again from new sites and the hot one: a method
# defined, redefined, removed or made private on Integer, Comparable, Kernel, Object or BasicObject, a module included
# or prepended, and refinements (`using`) of both kinds of call, one Integer has a method for (- and +) and one it
# inherits. Integer#+ has a module prepended above, and its refinement must still win, as in CRuby.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/shared-rule-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

FRESH = 80
HOT = 400

def guard
  yield
rescue Exception => e
  "#{e.class}: #{e.message[/\A[^\n]*/][0, 80]}"
end

$fresh = 0

# A new light method whose body is the call: its site's first call goes to the binder's rule cache.
# (Counted with succ: Integer#+ is redefined below.)
def fresh(src)
  name = :"fresh#{$fresh = $fresh.succ}"
  Object.class_eval("def #{name}(a) = #{src}", __FILE__, __LINE__)
  name
end

def calls(src, hot, recv)
  seen = []
  FRESH.times do
    v = guard { send(fresh(src), recv) }
    seen << v unless seen.include?(v)
  end
  HOT.times do
    v = guard { send(hot, recv) }
    seen << v unless seen.include?(v)
  end
  seen
end

def probe(label, src, hot, recv = 5)
  calls(src, hot, recv)
  sleep 0.2 # the rule's compile, on a pool thread
  before = calls(src, hot, recv)
  yield
  after = calls(src, hot, recv)
  puts "#{label.ljust(46)} => #{before.map(&:inspect).join(', ')} | #{after.map(&:inspect).join(', ')}"
end

def zz_hot(a) = a.zz
def plus_hot(a) = a + 1
def big_hot(a) = a + 1

# ---------------------------------------------------------------- 1. a method Integer inherits

class BasicObject
  def zz = :BasicObject
end

module M1; def zz = :M1; end
module P1; def zz = :P1; end

z = "a.zz"
probe("Kernel#zz defined", z, :zz_hot) { module Kernel; def zz = :Kernel; end }
probe("Object#zz defined", z, :zz_hot) { class Object; def zz = :Object; end }
probe("Comparable#zz defined", z, :zz_hot) { module Comparable; def zz = :Comparable; end }
probe("Integer.include M1", z, :zz_hot) { Integer.include M1 }
probe("Integer#zz defined", z, :zz_hot) { class Integer; def zz = :Integer; end }
probe("Integer#zz redefined", z, :zz_hot) { class Integer; def zz = :Integer2; end }
probe("Integer.prepend P1", z, :zz_hot) { Integer.prepend P1 }
probe("P1#zz removed", z, :zz_hot) { P1.send(:remove_method, :zz) }
probe("Integer#zz made private", z, :zz_hot) { Integer.send(:private, :zz) }
probe("Integer#zz made public", z, :zz_hot) { Integer.send(:public, :zz) }
probe("Integer#zz removed", z, :zz_hot) { Integer.send(:remove_method, :zz) }
probe("M1#zz removed", z, :zz_hot) { M1.send(:remove_method, :zz) }
probe("Comparable#zz removed", z, :zz_hot) { Comparable.send(:remove_method, :zz) }
probe("Object#zz made private", z, :zz_hot) { Object.send(:private, :zz) }
probe("Object#zz removed", z, :zz_hot) { Object.send(:remove_method, :zz) }
probe("Kernel#zz removed", z, :zz_hot) { Kernel.send(:remove_method, :zz) }
probe("BasicObject#zz made private", z, :zz_hot) { BasicObject.send(:private, :zz) }
probe("BasicObject#zz made public", z, :zz_hot) { BasicObject.send(:public, :zz) }
probe("BasicObject#zz removed", z, :zz_hot) { BasicObject.send(:remove_method, :zz) }
probe("BasicObject#zz defined again", z, :zz_hot) { class BasicObject; def zz = :BasicObject2; end }
probe("Integer undef_method zz", z, :zz_hot) { Integer.send(:undef_method, :zz) }
probe("Integer#zz defined after undef", z, :zz_hot) { class Integer; def zz = :Integer3; end }

# ---------------------------------------------------------------- 2. a method Integer has (Integer#+)

p = "a + 1"
probe("Comparable#+ defined", p, :plus_hot) { module Comparable; def +(o) = :Comparable; end }
probe("Kernel#+ defined", p, :plus_hot) { module Kernel; def +(o) = :Kernel; end }
probe("Object#+ defined", p, :plus_hot) { class Object; def +(o) = :Object; end }
probe("BasicObject#+ defined", p, :plus_hot) { class BasicObject; def +(o) = :BasicObject; end }
probe("Integer#+ redefined", p, :plus_hot) { class Integer; alias_method :__plus, :+; def +(o) = :Integer; end }
probe("Integer#+ made private", p, :plus_hot) { Integer.send(:private, :+) }
probe("Integer#+ made public", p, :plus_hot) { Integer.send(:public, :+) }
probe("Integer#+ restored", p, :plus_hot) { class Integer; alias_method :+, :__plus; end }
probe("Integer.prepend (+)", p, :plus_hot) { Integer.prepend(Module.new { def +(o) = [:P, super] }) }
probe("Integer.include (+), shadowed", p, :plus_hot) { Integer.include(Module.new { def +(o) = :I }) }
probe("Bignum receiver, Object#+ removed", p, :big_hot, 2**70) { Object.send(:remove_method, :+) }

# ---------------------------------------------------------------- 3. refinements

module RefZ
  refine Integer do
    def zz = :refined
    def -(o) = [:refined_minus, o]
    def +(o) = [:refined_plus, o]
  end
end

class Integer; def zz = :Integer4; end

def zz_before_using(a) = a.zz
def minus_before_using(a) = a - 1
def plus_before_using(a) = a + 1

probe("before using: zz", z, :zz_hot) { }
probe("before using: -", "a - 1", :minus_before_using) { }
probe("before using: +", "a + 1", :plus_before_using) { }

using RefZ

def zz_after_using(a) = a.zz
def minus_after_using(a) = a - 1
def plus_after_using(a) = a + 1

r = []
HOT.times do
  r << zz_before_using(3) << zz_after_using(3) << minus_before_using(3) << minus_after_using(3)
  r << plus_before_using(3) << plus_after_using(3) << send(fresh("a + 1"), 3)
end
puts "after using, methods by place".ljust(46) + " => " + r.uniq.map(&:inspect).join(', ')

module RefZ
  refine Integer do
    def zz = :refined2
  end
end
r = []
HOT.times { r << zz_before_using(3) << zz_after_using(3) }
puts "refinement redefined".ljust(46) + " => " + r.uniq.map(&:inspect).join(', ')

class Integer; def zz = :Integer5; end
r = []
HOT.times { r << zz_before_using(3) << zz_after_using(3) << send(fresh("a.zz"), 3) }
puts "Integer#zz redefined under using".ljust(46) + " => " + r.uniq.map(&:inspect).join(', ')
