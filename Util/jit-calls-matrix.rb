# Differential matrix for -X:JITCalls (Src/Ruby/Runtime/Jit/JitStub.cs, BindCallee): a method the
# method JIT compiles calls the typed bodies of the other methods it calls on self directly.
# Run under CRuby and under ./ir.sh in each mode, and diff - the outputs must be identical:
#
#   ruby Util/jit-calls-matrix.rb > /tmp/mri.txt
#   ./ir.sh -X:JITCalls Util/jit-calls-matrix.rb > /tmp/calls.txt
#   ./ir.sh -X:JITCalls -X:JITMethods=run,tiered Util/jit-calls-matrix.rb > /tmp/methods.txt
#
# Each caller is made hot with the types it is to be compiled for, and then probed where the
# direct call has to give way: the callee redefined, overridden in a subclass or by a singleton
# method, a deopt inside the callee, an argument the callee was not compiled for, a frozen
# receiver, a constant the callee folded. What was bound is not part of the output; to see it:
#
#   IR_JIT_VERBOSE=1 ./ir.sh -X:JITCalls Util/jit-calls-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]'

def t(label)
  print label.ljust(48), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts e.class
  end
end

HOT = 120

def hot(*args)
  r = nil
  HOT.times { r = yield(*args) }
  r
end

# ---- a chain of small callees ---------------------------------------------------------

def sq(x) = x * x
def norm2(x, y) = sq(x) + sq(y)
def norm_sum(n)
  s = 0; i = 0
  while i < n
    s += norm2(i, 7)
    i += 1
  end
  s
end

t('chain: hot') { hot { norm_sum(50) } }
t('chain: Int32 -> Int64 in the callee') { norm2(100_000, 100_000) }
t('chain: Int64 -> Bignum, a deopt in the callee') { norm2(4_000_000_000, 4_000_000_000) }
t('chain: a Float where an Integer was profiled') { norm2(1.5, 2) }
t('chain: a String') { norm2("a", 2) }
t('chain: after the deopts') { norm_sum(50) }

# ---- the callee redefined, then again ----------------------------------------------------

def step(x) = x + 1
def run_steps(n)
  s = 0; i = 0
  while i < n
    s = step(s)
    i += 1
  end
  s
end

t('redefine: hot') { hot { run_steps(10) } }
def step(x) = x + 2
t('redefine: callee redefined') { run_steps(10) }
t('redefine: hot again') { hot { run_steps(10) } }
def step(x) = x * 1.5 + 1
t('redefine: callee now returns a Float') { run_steps(10) }
t('redefine: hot again, Floats') { hot { run_steps(10) } }
alias old_step step
def step(x) = old_step(x) + 1
t('redefine: callee wraps its alias') { hot { run_steps(10) } }

# ---- a helper next to a self-call ----------------------------------------------------------

def small?(k) = k < 2
def fibh(k) = small?(k) ? k : fibh(k - 1) + fibh(k - 2)

t('recursive with a helper') { fibh(22) }
t('recursive with a helper: Int64') { fibh(50 - 20) + 2**40 }

def odd?(k) = k == 0 ? false : even?(k - 1)
def even?(k) = k == 0 ? true : odd?(k - 1)

t('mutual recursion: hot') { hot { even?(30) } }
t('mutual recursion: odd') { odd?(31) }

def runaway(k) = runaway_helper(k) + 1
def runaway_helper(k) = runaway(k + 1)
t('mutual recursion without an end') { runaway(0) }

# ---- overriding: a subclass, a singleton method, a module ----------------------------------

class Shape
  def initialize(w) = @w = w
  def scale = 2
  def side(x) = x * scale
  def area(x) = side(x) * side(x)
  def w = @w
  def width_plus(x) = w + x
end

class Wide < Shape
  def scale = 3
end

s = Shape.new(5)
t('class: hot') { hot { s.area(4) } }
t('class: a subclass overriding the callee') { Wide.new(5).area(4) }
t('class: back to the class') { s.area(4) }
w = Wide.new(6)
t('class: the subclass, hot') { hot { w.area(4) } }
t('class: both again') { [s.area(4), w.area(4)] }

one = Shape.new(7)
def one.scale = 10
t('class: a singleton method on one object') { one.area(4) }
t('class: the others still') { [s.area(4), w.area(4)] }

module Tripler
  def scale = 30
end
t('class: an object extended by a module') { o = Shape.new(1); o.extend(Tripler); o.area(4) }
class Shape
  prepend(Module.new { def scale = super + 100 })
end
t('class: a module prepended to the class') { [s.area(4), w.area(4)] }
t('class: hot under the prepended module') { hot { s.area(4) } }

t('class: callee reads an instance variable') { hot { s.width_plus(1) } }
t('class: ... of another object') { Shape.new(40).width_plus(1) }
t('class: ... a Float') { Shape.new(1.5).width_plus(1) }
t('class: ... nil') { Shape.new(nil).width_plus(1) }

# a method with an optional parameter has a body per argument count, and each one that binds a
# call needs the receiver class its callee was resolved on (the callee is made hot first: such
# a caller does not wait for it)
class OptBase
  def scale(x)
    i = 0
    while i < 3
      i += 1
    end
    x * 2
  end
  def f(x = 1 + 1) = scale(x)
end
class OptDerived < OptBase
  def scale(x) = x * 3
end
opt = OptBase.new
t('an optional parameter: callee, then caller') { hot { opt.scale(2) }; [hot { opt.f }, hot { opt.f(5) }] }
t('... the callee overridden in a subclass') { d = OptDerived.new; [d.f, d.f(5), opt.f, opt.f(5)] }

# ---- visibility and the receiver ---------------------------------------------------------------

class Vis
  def pub(x) = priv(x) + prot(x) + self.priv(x)
  def with_self(x) = self.twice(x) + twice(x)
  def twice(x) = x * 2
  private def priv(x) = x + 1
  protected def prot(x) = x + 2
end

v = Vis.new
t('private and protected callees') { hot { v.pub(10) } }
t('self.callee') { hot { v.with_self(10) } }
class Vis
  def twice(x) = x * 3
end
t('self.callee, redefined') { v.with_self(10) }

class Vis
  private :twice
end
t('callee made private') { hot { v.with_self(10) } }
t('... and called from outside') { v.twice(1) }

class Fallback
  def base(x) = x + 1
  def calls_base(x) = base(x) * 2
end
class Fallback2 < Fallback
  def base(x) = x + 10
end
f2 = Fallback2.new
t('override: hot') { hot { f2.calls_base(1) } }
Fallback2.send(:remove_method, :base)
t('override removed: the inherited callee') { hot { f2.calls_base(1) } }
Fallback.send(:undef_method, :base)
t('callee undefined') { f2.calls_base(1) }

class Missing
  def call_it(x) = ghost(x) + 1
  def method_missing(name, *args) = name == :ghost ? args[0] * 100 : super
  def respond_to_missing?(name, priv = false) = name == :ghost || super
end
m = Missing.new
t('method_missing callee') { hot { m.call_it(2) } }
class Missing
  def ghost(x) = x
end
t('method_missing callee, then defined') { hot { m.call_it(2) } }

# ---- refinements ----------------------------------------------------------------------------

class Refd
  def val(x) = x + 1
  def plain(x) = val(x) * 2
end
module RefdTimes
  refine Refd do
    def val(x) = x + 100
  end
end
rf = Refd.new
t('refinement: caller defined before using') { hot { rf.plain(1) } }
using RefdTimes
class Refd
  def refined(x) = val(x) * 2
  def refined_self(x) = self.val(x) * 2
end
t('refinement: caller defined after') { hot { rf.refined(1) } }
t('refinement: self.callee') { hot { rf.refined_self(1) } }
t('refinement: the earlier caller still') { hot { rf.plain(1) } }
module RefdTimes
  refine Refd do
    def val(x) = x + 1000
  end
end
t('refinement: the refined callee redefined') { [rf.refined(1), rf.plain(1)] }
t('refinement: hot again') { hot { rf.refined(1) } }

# ---- callees the compiled call cannot take: still right ------------------------------------

def opt(a, b = 10) = a + b
def calls_opt(a) = opt(a) + opt(a, 1)
t('callee with an optional parameter') { hot { calls_opt(1) } }

def kw(a, k: 2) = a * k
def calls_kw(a) = kw(a) + kw(a, k: 3)
t('callee with a keyword') { hot { calls_kw(1) } }

def blk(a) = yield(a)
def calls_blk(a) = blk(a) { |x| x + 1 }
t('callee with a block') { hot { calls_blk(1) } }

def splat(*a) = a.size
def calls_splat(a) = splat(a, a) + 1
t('callee with a splat') { hot { calls_splat(1) } }

def builtin(a) = Integer(a) + 1
t('a Kernel method') { hot { builtin(1) } }

def bare = 21
def calls_bare = bare + bare
t('a call without parentheses or arguments') { hot { calls_bare } }

def wrong_argc(a) = sq(a, a)
t('wrong number of arguments') { hot { wrong_argc(1) } }

# ---- deopts inside the callee -------------------------------------------------------------------

def quot(a, b) = a / b
def avg(a, b) = quot(a + b, 2)
def ratio(a, b) = quot(a, b) + 1

t('deopt: hot') { hot { [avg(10, 20), ratio(10, 3)] } }
t('deopt: division by zero in the callee') { ratio(1, 0) }
t('deopt: after it') { ratio(10, 3) }
t('deopt: again and again') { r = 0; 300.times { |i| r += (ratio(10, i % 3) rescue -1) }; r }
t('deopt: still right') { [avg(10, 20), ratio(10, 3)] }

def bt_inner(a, b) = a / b
def bt_outer(a, b)
  x = bt_inner(a, b)
  x + 1
end
hot { bt_outer(4, 2) }
begin
  bt_outer(1, 0)
rescue ZeroDivisionError => e
  puts 'backtrace of an error in a callee:'
  puts e.backtrace.first(3).map { |l| '  ' + l[/[^\/]*\.rb:.*/] }
end

# ---- arguments ------------------------------------------------------------------------------------

def half(x) = x / 2
def halves(a) = half(a) + half(a + 1)
t('args: Integers') { hot { halves(9) } }
t('args: a Float through an Integer caller') { halves(9.0) }
t('args: Floats, hot') { hot { halves(9.0) } }
t('args: Integers again') { halves(9) }

def fhalf(x) = x / 2
def int_to_float_callee(a) = fhalf(a) + fhalf(7)
HOT.times { fhalf(3.0) }
t('args: an Integer literal, callee compiled for Floats') { hot { int_to_float_callee(9.0) } }

def pick(a, i) = a[i]
def pick_sum(a) = pick(a, 0) + pick(a, 1) + 1
t('args: an element as the result') { hot { pick_sum([1, 2]) } }
t('args: ... a Float element') { pick_sum([1.5, 2]) }
t('args: ... a nil element') { pick_sum([nil, 2]) }

def ident(x) = x
def use_ident(a) = ident(a) + ident(a)
t('args: a callee that only passes on') { hot { use_ident(4) } }
t('args: ... a String') { use_ident("ab") }

def truthy(x) = x > 3
def count_big(n)
  c = 0; i = 0
  while i < n
    c += 1 if truthy(i)
    i += 1
  end
  c
end
t('args: a boolean result in a condition') { hot { count_big(10) } }

def nothing(x) = nil
def use_nothing(x) = nothing(x)
t('args: a nil result') { hot { use_nothing(1) } }

# ---- effects ----------------------------------------------------------------------------------------

class Counter
  def initialize = @n = 0
  def n = @n
  def set(v) = @n = v
  def set_twice(v)
    set(v)
    set(v + 1)
  end
  def set_and_divide(v, d)
    set(v)
    v / d
  end
end

c = Counter.new
t('ivar write in a callee') { hot { c.set_twice(5) }; c.n }
t('ivar write in a callee, frozen receiver') { f = Counter.new; f.freeze; f.set_twice(5) }
t('ivar write, then a deopt in the caller') { [(c.set_and_divide(9, 0) rescue :zero), c.n] }
t('ivar write, hot with the divide') { hot { c.set_and_divide(8, 2) }; c.n }
t('ivar write, then a zero divisor') { [(c.set_and_divide(11, 0) rescue :zero), c.n] }

$calls = 0
def bump(x)
  $calls = $calls + 1
  x
end
def bump_twice(x) = bump(x) + bump(x)
t('a global written by the callee') { hot { bump_twice(1) }; $calls }
t('... and a deopt after it') { $calls = 0; [(bump_twice(2**62) rescue :err), $calls] }

def fill(h, k)
  h[k] = k
  h.size
end
def fill_two(h, k) = fill(h, k) + fill(h, k + 1)
t('a Hash written by the callee') { h = {}; hot(h) { |x| fill_two(x, x.size) }; h.size }

def loop_callee(n)
  s = 0; i = 0
  while i < n
    s += i
    i += 1
  end
  s
end
def store_and_loop(a, n)
  a[0] = n
  loop_callee(n) + a[0]
end
t('a loop in the callee, an element write in the caller') { hot { store_and_loop([0], 10) } }

# ---- constants ------------------------------------------------------------------------------------------

LIMIT = 10
def under?(x) = x < LIMIT
def count_under(n)
  c = 0; i = 0
  while i < n
    c += 1 if under?(i)
    i += 1
  end
  c
end
t('a constant folded in the callee') { hot { count_under(30) } }
Object.send(:remove_const, :LIMIT)
LIMIT = 20
t('... reassigned') { count_under(30) }
t('... hot again') { hot { count_under(30) } }

# ---- Integer#+ redefined, a TracePoint ---------------------------------------------------------------------

def plus1(x) = x + 1
def plus2(x) = plus1(plus1(x))
t('operator: hot') { hot { plus2(1) } }
class Integer
  alias_method :jit_calls_plus, :+
  def +(other) = jit_calls_plus(other).jit_calls_plus(1000)
end
t('operator: Integer#+ redefined') { plus2(1) }
class Integer
  alias_method :+, :jit_calls_plus
end
t('operator: restored') { hot { plus2(1) } }

seen = []
tp = TracePoint.new(:call) { |x| seen << x.method_id }
tp.enable { plus2(1) }
t('TracePoint sees the callee') { seen }

# ---- threads ---------------------------------------------------------------------------------------------------

def tsq(x) = x * x
def tsum(n)
  s = 0; i = 0
  while i < n
    s += tsq(i)
    i += 1
  end
  s
end
t('four threads making one caller hot') { 4.times.map { Thread.new { hot { tsum(100) } } }.map(&:value) }
