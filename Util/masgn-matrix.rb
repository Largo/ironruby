# Differential matrix for methods and blocks with a multiple assignment (`a, b = b, a', `x, *rest = list') that run
# without a scope (a light frame, MethodDefinition.IsScopeFree). Run it under CRuby and under ./ir.sh with the shape
# on and off, and diff:
#
#   ruby Util/masgn-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/masgn-matrix.rb > /tmp/on.txt
#   IR_LIGHT_MASGN=0 ./ir.sh Util/masgn-matrix.rb > /tmp/off.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt
#
# Every probe runs N times and reports each distinct answer once, so the interpreted body and the compiled one both
# meet the case: what each left value gets for every kind of right side (#to_ary of an Array, nil, a non-Array, an
# object whose #to_ary is a Ruby method, defined later, and a wrong one), the value of the assignment, the order
# receivers and values are evaluated in, attribute and element writes (private, method_missing, redefined after the
# site is bound), what a scope made on demand sees, blocks, and recursion through #to_ary.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/masgn-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

N = 3000

def t(label)
  print label.ljust(52), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 70]}"
  end
end

# ---------------------------------------------------------------- 1. what the left values get

def swap(a, b); a, b = b, a; [a, b]; end
def value_of(a, b); x, y = a, b; end
def one(v); a, b = v; [a, b]; end
def one_value(v); a, b = v; end
def three(v); a, b, c = v; [a, b, c]; end
def head(v); a, *b = v; [a, b]; end
def mid(v); a, *b, c = v; [a, b, c]; end
def tail(v); *a, b = v; [a, b]; end
def nested(v); a, (b, c), d = v; [a, b, c, d]; end
def placeholder(v); a, = v; a; end
def splat_right(v); a, b = *v; [a, b]; end
def splat_among(v); a, b, c = 0, *v; [a, b, c]; end
def ivars(v); @a, @b = v; [@a, @b]; end
def mixed(v); @a, b, *c = v, 1; [@a, b, c]; end
def loop_swap(n); a, b = 0, 1; i = 0; while i < n; a, b = b, a + b; i += 1; end; a; end

class ToAry; def initialize(v); @v = v; end; def to_ary; @v; end; end
class NilToAry; def to_ary; nil; end; end
class BadToAry; def to_ary; 1; end; end
class NoToAry; end
class MissingToAry; def respond_to_missing?(n, p = false) = n == :to_ary; def method_missing(n, *a) = n == :to_ary ? [7, 8] : super; end
class LaterToAry; end
class SubArray < Array; def [](i) = :sub; end

t('swap') { |i| swap(i % 3, :b) }
t('the value of x, y = a, b') { value_of(1, 2) }
t('an Array') { one([1, 2]) }
t('a longer Array') { one([1, 2, 3]) }
t('a shorter Array') { one([1]) }
t('an empty Array') { one([]) }
t('nil') { one(nil) }
t('an Integer') { one(5) }
t('a String') { one("s") }
t('a Hash') { one({ a: 1 }) }
t('to_ary') { one(ToAry.new([3, 4])) }
t('to_ary returning nil') { one(NilToAry.new).map(&:class) }
t('to_ary returning 1') { one(BadToAry.new) }
t('no to_ary') { o = NoToAry.new; one(o)[0].equal?(o) }
t('to_ary through method_missing') { one(MissingToAry.new) }
t('to_ary defined after the site is bound') { |i| LaterToAry.define_method(:to_ary) { [9, 9] } if i == 1500; one(LaterToAry.new).map(&:class) }
t('an Array subclass') { one(SubArray[1, 2]) }
t('the value of a, b = v is v') { v = [1, 2]; one_value(v).equal?(v) }
t('the value of a, b = nil') { one_value(nil) }
t('the value of a, b = to_ary') { o = ToAry.new([1]); one_value(o).equal?(o) }
t('three') { three([1, 2]) }
t('head') { head([1, 2, 3]) }
t('head of nil') { head(nil) }
t('mid') { mid([1, 2, 3, 4]) }
t('mid of one') { mid([1]) }
t('tail') { tail([1, 2, 3]) }
t('nested') { nested([1, [2, 3], 4]) }
t('nested of a non-Array') { nested([1, 2, 4]) }
t('nested to_ary') { nested([1, ToAry.new([5, 6]), 4]) }
t('placeholder') { placeholder([1, 2]) }
t('splat right') { splat_right(1..3) }
t('splat right nil') { splat_right(nil) }
t('splat among') { splat_among([1, 2]) }
t('instance variables') { ivars([1, 2]) }
t('mixed') { mixed(3) }
t('in a loop') { loop_swap(10) }
t('frozen self, ivars') { o = Object.new; def o.w = (@a, @b = 1, 2); o.freeze; begin; o.w; rescue => e; e.class; end }

# ---------------------------------------------------------------- 2. attribute and element writes

class Box
  attr_accessor :x, :y
  def set(a, b); self.x, self.y = a, b; [x, y]; end
  def set_private(a, b); self.p, self.q = a, b; [@p, @q]; end
  def other(o, a); o.x, o.y = a, a; [o.x, o.y]; end
  def items(a, i, j); a[i], a[j] = a[j], a[i]; a; end
  def items_nested(a); a[0], (a[1], a[2]) = 9, [8, 7]; a; end
  private
  def p=(v); @p = v; end
  def q=(v); @q = v; end
end
class Ghost
  def method_missing(n, *a) = (@log ||= []) << [n, a]
  def respond_to_missing?(*) = true
  def log = @log
end
class Logged; def initialize(log) = @log = log; def x=(v); @log << [:x=, v]; end; def y=(v); @log << [:y=, v]; end; end
def order(log)
  a = Logged.new(log); b = Logged.new(log)
  (log << :a; a).x, (log << :b; b).y = (log << 1; 1), (log << 2; 2)
  log
end
class Redef; attr_reader :x, :y; def x=(v); @x = v; end; def y=(v); @y = v; end; end

t('attribute writes on self') { Box.new.set(1, 2) }
t('private writers on self') { Box.new.set_private(1, 2) }
t('attribute writes on another') { Box.new.other(Box.new, 3) }
t('element swap') { Box.new.items([1, 2, 3], 0, 2) }
t('element swap, Hash') { h = { a: 1, b: 2 }; Box.new.items(h, :a, :b) }
t('nested element writes') { Box.new.items_nested([1, 2, 3]) }
t('a private writer on another') { begin; o = Box.new; Box.new.instance_eval { o.p, o.q = 1, 2 }; rescue NoMethodError => e; e.message[0, 30]; end }
t('method_missing writers') { g = Ghost.new; Box.new.other(g, 1); g.log }
t('evaluation order') { order([]) }
t('a writer redefined after binding') { |i| Redef.class_eval { def x=(v); @x = v * 10; end } if i == 1500; Box.new.other(Redef.new, 1) }
t('a frozen receiver') { begin; Box.new.other(Box.new.freeze, 1); rescue FrozenError => e; e.class; end }

class Prot; attr_reader :x, :y; protected; attr_writer :x, :y; end
class Unrelated; def set(o); o.x, o.y = 1, 2; end; end
class Prot; def set(o); o.x, o.y = 1, 2; [o.x, o.y]; end; end
class PrivSub < Box; private :x=; end
t('a protected writer from its class') { Prot.new.set(Prot.new) }
t('a protected writer from an unrelated class') { begin; Unrelated.new.set(Prot.new); rescue NoMethodError => e; e.message[0, 30]; end }
t('a writer made private in a subclass') { begin; Box.new.other(PrivSub.new, 1); rescue NoMethodError => e; e.message[0, 30]; end }

module RefineX; refine(Box) { def x=(v); @x = [:refined, v]; end }; end
def unrefined_other(o, a); o.x, o.y = a, a; [o.x, o.y]; end
using RefineX
def refined_other(o, a); o.x, o.y = a, a; [o.x, o.y]; end
t('a refined writer in use') { refined_other(Box.new, 1) }
t('a refined writer not in use') { unrefined_other(Box.new, 1) }

# ---------------------------------------------------------------- 3. what a scope made on demand sees

def masgn_binding(v); a, b = v; binding.local_variable_get(:b); end
def masgn_locals(v); a, *b = v; local_variables; end
def masgn_eval(v); a, b = v; eval("a + b"); end
def masgn_block(v); a, b = v; [1].map { |x| x + a + b }; end
def masgn_block_writes(v); a, b = v; [1].each { |x| a = x * 100 }; a + b; end
def masgn_in_block(v); a = 0; b = 0; v.each { |x| a, b = b, a + x }; [a, b]; end
def masgn_in_block_value(v); v.map { |x| p, q = x; q }; end
def masgn_in_nested_block(v); s = 0; v.each { |x| [x].each { |y| s, _ = s + y, 0 } }; s; end
def masgn_raise(v); a, b = v; raise ArgumentError, "x" if a; b; end
def masgn_caller(v); a, b = v; caller(0, 1)[0][/'.*'/]; end
def masgn_method(v); a, b = v; __method__; end
def masgn_block_given(v); a, b = v; block_given? ? yield(a, b) : b; end
def masgn_yield(v); a, b = yield(v); [b, a]; end

t('binding') { masgn_binding([1, 2]) }
t('local_variables') { masgn_locals([1, 2]) }
t('eval') { masgn_eval([1, 2]) }
t('a block reads') { masgn_block([1, 2]) }
t('a block writes') { masgn_block_writes([1, 2]) }
t('in a block') { masgn_in_block([1, 2, 3]) }
t('in a block, its value') { masgn_in_block_value([[1, 2], [3, 4]]) }
t('in a nested block') { masgn_in_nested_block([1, 2, 3]) }
t('raise and the frame') { begin; masgn_raise([1, 2]); rescue ArgumentError => e; e.backtrace[0][/'.*'/]; end }
t('caller') { masgn_caller([1, 2]) }
t('__method__') { masgn_method([1, 2]) }
t('block_given? with') { masgn_block_given([1, 2]) { |a, b| a - b } }
t('block_given? without') { masgn_block_given([1, 2]) }
t('yield on the right') { masgn_yield(5) { |v| [v, v + 1] } }
t('send') { send(:one, [1, 2]) }
t('Method#call') { method(:one).call([1, 2]) }
t('define_method') { o = Object.new; o.define_singleton_method(:m) { |v| a, b = v; [b, a] }; o.m([1, 2]) }

# ---------------------------------------------------------------- 4. tracing

t('TracePoint :call sees it') do
  seen = []
  tp = TracePoint.new(:call) { |x| seen << x.method_id if x.method_id == :swap }
  tp.enable { swap(1, 2) }
  seen
end
t('TracePoint :call sees to_ary') do
  seen = []
  tp = TracePoint.new(:call) { |x| seen << x.method_id if x.method_id == :to_ary }
  tp.enable { one(ToAry.new([1, 2])) }
  seen
end
t('after tracing') { swap(1, 2) }

# ---------------------------------------------------------------- 5. recursion through #to_ary

class Rec; def to_ary; deep(self); end; end
def deep(v); a, b = v; a; end
def deep_items(n); a, b = (n > 0 ? deep_items(n - 1) : 0), 1; a + b; end
def deep_ivars(o); @a, @b = o; @a; end
class RecIvar; def to_ary; deep_ivars(self); end; end

puts "recursion through to_ary: " + (begin; deep(Rec.new); "no error"; rescue SystemStackError; "SystemStackError"; end)
puts "recursion through to_ary, ivars: " + (begin; deep_ivars(RecIvar.new); "no error"; rescue SystemStackError; "SystemStackError"; end)
puts "recursion on the right: " + (begin; deep_items(1_000_000).to_s; rescue SystemStackError; "SystemStackError"; end)
puts "recursion on the right, shallow: " + deep_items(1000).to_s
