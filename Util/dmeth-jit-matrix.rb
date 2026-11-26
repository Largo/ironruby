# Differential matrix for the method JIT taking the body of a method made by define_method (a block body,
# RubyLambdaMethodInfo; IR_JIT_DMETH). Every probe calls its method thousands of times, past the JIT's
# thresholds, so that a typed body is made where one can be and the rows see what it computes, what it
# declines, and what happens when its guards fail (redefinition, remove_method, another receiver class,
# overflow). Util/dmeth-matrix.rb covers the call itself (arity errors, flow control, binding, threads).
# Run under CRuby and under ./ir.sh - with the JIT on, off, without direct calls and with the switch off:
#
#   ruby Util/dmeth-jit-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/dmeth-jit-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/dmeth-jit-matrix.rb > /tmp/nojit.txt
#   ./ir.sh -X:NoJITCalls Util/dmeth-jit-matrix.rb > /tmp/nocalls.txt
#   IR_JIT_DMETH=0 ./ir.sh Util/dmeth-jit-matrix.rb > /tmp/off.txt
#
# All must end with the row `end' and be identical. JIT decisions go to stderr (IR_JIT_VERBOSE=1).

$stdout.sync = true
FILE = File.basename(__FILE__)
N = 4000

def err
  yield
  :no_error
rescue Exception => e
  [e.class, e.message]
end

def row(name, value)
  puts "#{name}: #{value.inspect}"
end

# the values of every 997th call and of the last
def hot(n = N)
  r = []
  v = nil
  n.times { |i| v = yield(i); r << v if i % 997 == 0 }
  r << v
end

K = 7

class P
  def initialize(a) = (@a = a; @f = 1.5)
  attr_reader :a
  def helper(x) = x * 2
  define_method(:zero) { 42 }
  define_method(:one) { |x| x + 1 }
  define_method(:two) { |x, y| x * y + 1 }
  define_method(:three) { |x, y, z| x - y + z }
  define_method(:ivar) { @a }
  define_method(:ivar_plus) { |x| @a + x }
  define_method(:flt) { |x| @f * x }
  define_method(:call_other) { |x| helper(x) + 1 }
  define_method(:attr) { |x| a + x }
  define_method(:const) { |x| K + x }
  define_method(:selfie) { self }
  define_method(:store) { |x| @b = x; @b + 1 }
  define_method(:cond) { |x| x > 10 ? x - 10 : x + 10 }
  define_method(:loop_) { |n| s = 0; i = 0; while i < n; s += i; i += 1; end; s }
  define_method(:ret) { |x| return x * 2 if x.odd?; x }
  define_method(:big) { |x| x * 4611686018427387904 }
  define_method(:div) { |x, y| x / y }
  define_method(:fib) { |n| n < 2 ? n : fib(n - 1) + fib(n - 2) }
  define_method(:opt) { |x, y = 5| x + y }
  define_method(:raiser) { |x| raise ArgumentError, "r#{x}" if x == N - 1; x }
end

p1 = P.new(3)
row "1. zero/one/two/three", [hot { p1.zero }, hot { |i| p1.one(i) }, hot { |i| p1.two(i, 3) }, hot { |i| p1.three(i, 2, 1) }]
row "2. ivars", [hot { p1.ivar }, hot { |i| p1.ivar_plus(i) }, hot { |i| p1.flt(i) }]
row "3. calls", [hot { |i| p1.call_other(i) }, hot { |i| p1.attr(i) }, hot { |i| p1.const(i) }, hot { p1.selfie.equal?(p1) }]
row "4. store, cond, loop", [hot { |i| p1.store(i) }, p1.instance_variable_get(:@b), hot { |i| p1.cond(i % 20) }, hot { |i| p1.loop_(i % 30) }]
row "5. return", [hot { |i| p1.ret(i) }, (0..9).map { |i| p1.ret(i) }]
row "6. overflow, division", [hot { |i| p1.big(i) }, hot { |i| p1.div(i, 3) }, err { p1.div(1, 0) }, hot { |i| p1.div(i + 0.5, 2) }]
row "7. recursion", [hot(200) { |i| p1.fib(i % 15) }, p1.fib(20)]
row "8. optional", [hot { |i| p1.opt(i) }, hot { |i| p1.opt(i, 1) }]
row "9. raise", [err { hot { |i| p1.raiser(i) } }]
begin
  p1.raiser(N - 1)
rescue ArgumentError => e
  row "9a. backtrace", [e.backtrace_locations[0].label, e.backtrace_locations[0].lineno == P.instance_method(:raiser).source_location[1]]
end
row "10. mixed argument types", [hot { |i| p1.one(i.odd? ? i : i.to_f) }, hot { |i| p1.one(i.odd? ? i : 2**70) }, err { p1.one("s") }]
row "10a. arity errors hot", [err { p1.one }, err { p1.two(1) }, err { p1.zero(1) }, err { p1.opt }, err { p1.opt(1, 2, 3) }]

# Captured locals of the defining scope: shared, and writes are seen outside.
class Q
  base = 10
  define_method(:read_captured) { |x| x + base }
  define_method(:set_base) { |v| base = v }
  total = 0
  define_method(:acc) { |x| total += x }
  define_method(:total) { total }
end
q = Q.new
r1 = hot { |i| q.read_captured(i) }
q.set_base(100)
row "11. captured locals", [r1, hot { |i| q.read_captured(i) }, hot { |i| q.acc(1) }, q.total]

# yield and block_given? see the defining method's block, not the call's.
class Y
  def self.make(name)
    define_method(name) { |x| block_given? ? yield + x : -x }
  end
  make(:with_block) { 1000 }
  make(:without_block)
end
y = Y.new
row "12. yield/block_given? of the defining scope", [hot { |i| y.with_block(i) }, hot { |i| y.without_block(i) }, hot { |i| y.with_block(i) { 5 } }]

class YBase
  def m(x) = [:base, x]
  def n(x) = x * 10
end
class YSub < YBase
  define_method(:m) { |x| super }
  define_method(:n) { |x| super(x) + 1 }
end
ys = YSub.new
row "13. implicit super raises", [err { ys.m(1) }, hot { err { ys.m(1) } }]
row "13a. explicit super", [hot { |i| ys.n(i) }]

# __method__ under an alias, binding, instance_exec of the same Proc.
class R
  define_method(:who) { __method__ }
  alias_method :who2, :who
  define_method(:bnd) { |x| _y = x + 1; binding }
  PR = proc { |x| x + @v }
  define_method(:viapr, &PR)
  def initialize = (@v = 9)
end
rr = R.new
row "14. __method__", [hot { rr.who }, hot { rr.who2 }]
row "15. binding", [hot { |i| b = rr.bnd(i); [b.local_variable_get(:x), b.local_variable_get(:_y), b.receiver.equal?(rr)] }]
row "16. instance_exec of the same Proc", [hot { |i| rr.viapr(i) }, hot { |i| rr.instance_exec(i, &R::PR) }, hot { |i| Struct.new(:v).new(3).instance_exec(i) { |z| z } }]

# define_method with a Method, an UnboundMethod, a lambda.
class M
  def plain(x) = x + 100
  define_method(:from_unbound, instance_method(:plain))
  define_method(:from_bound, M.new.method(:plain))
  define_method(:from_lambda, ->(x) { x + 200 })
  define_method(:from_lambda_ret, ->(x) { return x + 300 if x > 0; 0 })
end
mm = M.new
row "17. Method/UnboundMethod/lambda", [hot { |i| mm.from_unbound(i) }, hot { |i| mm.from_bound(i) }, hot { |i| mm.from_lambda(i) }, hot { |i| mm.from_lambda_ret(i) },
  err { mm.from_lambda }]

# Redefinition and remove_method while the body is hot; an override in a subclass; a module in two classes.
class H
  define_method(:h) { |x| x + 1 }
end
hh = H.new
r1 = hot { |i| hh.h(i) }
H.define_method(:h) { |x| x + 2 }
r2 = hot { |i| hh.h(i) }
class H; def h(x) = x + 3; end
r3 = hot { |i| hh.h(i) }
H.define_method(:h) { |x| x + 4 }
r4 = hot { |i| hh.h(i) }
H.send(:remove_method, :h)
row "18. redefinition and remove_method", [r1, r2, r3, r4, err { hh.h(1) }]

class HS < P
  define_method(:one) { |x| x + 1000 }
end
hs = HS.new(1)
row "19. subclass override", [hot { |i| p1.one(i) }, hot { |i| hs.one(i) }, hot { |i| (i.odd? ? p1 : hs).one(i) }]

module Mx
  define_method(:mx) { |x| @a + x }
end
class A1; include Mx; def initialize = (@a = 1); end
class A2; include Mx; def initialize = (@a = 2.5); end
a1, a2 = A1.new, A2.new
row "20. module in two classes", [hot { |i| a1.mx(i) }, hot { |i| a2.mx(i) }, hot { |i| (i.odd? ? a1 : a2).mx(i) }]

o = Object.new
o.define_singleton_method(:sm) { |x| x * 2 }
o.instance_variable_set(:@a, 4)
row "21. singleton", [hot { |i| o.sm(i) }]
class << o
  define_method(:sm2) { |x| @a * x }
end
row "21a. singleton class body", [hot { |i| o.sm2(i) }]

row "22. send and method", [hot { |i| p1.send(:one, i) }, hot { |i| p1.method(:two).call(i, 2) }, P.instance_method(:two).arity, P.instance_method(:opt).arity]

ths = 4.times.map { |t| Thread.new { x = 0; 20_000.times { |i| x += p1.two(i, t) + p1.ivar_plus(t) }; x } }
row "23. threads", ths.map(&:value)

# frozen receiver: a store raises
pf = P.new(1)
hot { |i| pf.store(i) }
pf.freeze
row "24. store into a frozen receiver", [err { pf.store(1) }.first]

# refinements visible where the block was written
module Ref
  refine Integer do
    def +(o) = 99
  end
end
class Rf
  using Ref
  define_method(:radd) { |x| x + 1 }
end
row "25. refinements", [hot { |i| Rf.new.radd(i) }]

# Where the block was written: in a method, in Class.new, in a class_eval string; one Proc under two names.
class W
  def self.make(name) = define_method(name) { |x| x * 3 }
  make(:w3)
  make(:w3b)
  class_eval "define_method(:ev) { |x| x + 5 }"
  PR2 = proc { |x| x + @k.to_i }
  define_method(:n1, &PR2)
  define_method(:n2, &PR2)
  define_method(:defl) { |x, y = (x * 2)| x + y }
  define_method(:orassign) { |x| @memo ||= x; @memo + x }
  define_method(:keephash) { |h| h }
  define_method(:priv) { |x| x - 1 }
  private :priv
  def callpriv(x) = priv(x)
  def takes(x, **kw) = [x, kw]
end
ww = W.new
cn = Class.new { define_method(:cn) { |x| x << 1 } }.new
row "26. written in a method, Class.new, class_eval", [hot { |i| ww.w3(i) }, hot { |i| ww.w3b(i) }, hot { |i| cn.cn(i) }, hot { |i| ww.ev(i) }]
row "27. one Proc, two names", [hot { |i| ww.n1(i) }, hot { |i| ww.n2(i) }]
row "28. default, ||=", [hot { |i| ww.defl(i) }, hot { |i| ww.defl(i, 1) }, hot { |i| ww.orassign(i) }]
row "29. a keyword hash kept", [hot { |i| ww.takes(ww.keephash(a: i)) }]
row "30. private", [hot { |i| ww.callpriv(i) }, err { ww.priv(1) }]

# Constants resolve where the block was written: in Class.new's block that is outside the new class.
kc = Class.new { const_set(:K, 99); define_method(:kk) { |x| K + x } }.new
module KM
  K = 500
  KC = Class.new { define_method(:kk) { |x| K + x } }
end
row "31. constants in Class.new", [hot { |i| kc.kk(i) }, hot { |i| KM::KC.new.kk(i) }]

puts 'end'
