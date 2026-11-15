# Differential matrix for methods with a rest parameter (`def f(*args)') that run without a scope
# (a light frame, MethodDefinition.IsLightFrameCandidate). Run it under CRuby and under ./ir.sh with
# such frames on and off, and diff:
#
#   ruby Util/light-rest-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/light-rest-matrix.rb > /tmp/on.txt
#   IR_LIGHT_REST=0 ./ir.sh Util/light-rest-matrix.rb > /tmp/off.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt
#
# Every probe runs N times and reports each distinct answer once, so the interpreted body and the
# compiled one both meet the case: what the rest Array holds for every way of passing arguments, that
# it is the method's own Array, what a scope made on demand (binding, local_variables, eval, a block,
# a TracePoint) sees of it, arity errors, and the frame in a backtrace.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/light-rest-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
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

# ---------------------------------------------------------------- 1. what the Array holds

def rest(*a); a; end
def lead(x, *a); [x, a]; end
def lead2(x, y, *a); [x, y, a]; end
def opt(x, y = 5, *a); [x, y, a]; end
def anon(*); 1; end
def size_of(*a); a.size; end
def first_of(*a); a[0]; end
def cls(*a); a.class; end

t('no arguments') { rest }
t('one') { rest(1) }
t('three') { rest(1, :b, "c") }
t('a nil') { rest(nil) }
t('an Array argument') { rest([1, 2]) }
t('a splat') { rest(*[1, 2]) }
t('a splat of nothing') { rest(*[]) }
t('a splat among arguments') { rest(0, *[1, 2], 3) }
t('two splats') { rest(*[1], *[2, 3]) }
t('a splat of nil') { rest(*nil) }
t('a splat of a non-Array') { rest(*1) }
t('a splat of to_a') { rest(*(1..3)) }
t('a Hash argument') { rest({ a: 1 }) }
t('keywords become a Hash') { rest(a: 1, b: 2) }
t('an argument and keywords') { rest(1, a: 1) }
t('a double splat') { h = { a: 1 }; rest(**h) }
t('an empty double splat') { h = {}; rest(**h) }
t('leading parameter') { lead(1) }
t('leading parameter and rest') { lead(1, 2, 3) }
t('leading parameter from a splat') { lead(*[1, 2, 3]) }
t('two leading') { lead2(1, 2, 3, 4) }
t('optional not given') { opt(1) }
t('optional given') { opt(1, 2) }
t('optional and rest') { opt(1, 2, 3, 4) }
t('anonymous rest') { anon(1, 2, 3) }
t('size') { |i| size_of(*([0] * (i % 4))) }
t('first') { |i| first_of(i % 3, 9) }
t('class') { cls(1) }
t('many arguments') { rest(*(1..40)).size }
t('alternating counts') { |i| i.even? ? rest : rest(1, 2) }
t('with a block ignored') { rest(1) { } }

# ---------------------------------------------------------------- 2. the Array is the method's own

def same(*a); a.equal?(a); end
def mutate(*a); a << 9; a; end
def assign(*a); a = [7]; a; end
def frozen(*a); a.frozen?; end
def keep(*a); @kept = a; end
def clear_it(*a); a.clear; a; end

t('identity within a call') { same(1) }
t('a new Array per call') { rest(1).equal?(rest(1)) }
t('not the splatted Array') { x = [1, 2]; rest(*x).equal?(x) }
t('the splatted Array is untouched') { x = [1, 2]; mutate(*x); x }
t('appended to') { mutate(1) }
t('reassigned') { assign(1, 2) }
t('not frozen') { frozen(1) }
t('kept in an ivar, each its own') { a = keep(1); b = keep(2); [a, b, a.equal?(b)] }
t('cleared') { clear_it(1, 2) }

# ---------------------------------------------------------------- 3. the scope made on demand sees it

def by_binding(*a); binding.local_variable_get(:a); end
def by_binding_set(*a); binding.local_variable_set(:a, [0]); a; end
def by_locals(x, *a); local_variables; end
def by_eval(*a); eval("a"); end
def by_eval_set(*a); eval("a = [5]"); a; end
def in_block(*a); r = nil; [1].each { r = a }; r; end
def block_writes(*a); [1].each { a = [:w] }; a; end
def in_proc(*a); proc { a }; end
def in_while(*a); i = 0; s = 0; while i < a.size; s += a[i]; i += 1; end; s; end
def passes_on(*a); rest(*a); end
def passes_on_more(*a); rest(0, *a, 9); end
def to_other(*a); lead(*a); end
def with_ivar(*a); @v = a.size; @v + a.size; end
def with_case(*a); case a.size when 0 then :none when 1 then :one else :many end; end
def with_interp(*a); "#{a.size}:#{a.first}"; end
def named_method(*a); __method__; end
def with_yield(*a); yield(*a); end
def with_yield_array(*a); yield a; end
def block_given(*a); block_given? ? a : nil; end
def recursive(*a); a.empty? ? :done : recursive(*a[1..]); end
def returns_early(*a); return :early if a.empty?; a.last; end
def with_cond(*a); a.empty? ? 0 : a[0]; end
def with_op_assign(*a); a += [1]; a; end

t('binding.local_variable_get') { by_binding(1, 2) }
t('binding.local_variable_set') { by_binding_set(1) }
t('local_variables') { by_locals(1, 2) }
t('eval reads it') { by_eval(1, 2) }
t('eval assigns it') { by_eval_set(1) }
t('a block reads it') { in_block(1, 2) }
t('a block assigns it') { block_writes(1) }
t('a proc keeps it') { in_proc(1, 2).call }
t('a while loop over it') { in_while(1, 2, 3) }
t('passed on with a splat') { passes_on(1, 2) }
t('passed on among arguments') { passes_on_more(1, 2) }
t('passed on to leading parameters') { to_other(1, 2, 3) }
t('with an ivar') { Object.new.instance_eval { with_ivar(1, 2) } }
t('case over its size') { |i| with_case(*([0] * (i % 3))) }
t('interpolated') { with_interp(4, 5) }
t('__method__') { named_method(1) }
t('yield(*a)') { with_yield(1, 2) { |x, y| [x, y] } }
t('yield a') { with_yield_array(1, 2) { |x| x } }
t('block_given?') { |i| i.even? ? block_given(1) : block_given(1) { } }
t('recursion') { recursive(1, 2, 3) }
t('early return') { |i| i.even? ? returns_early : returns_early(1, 2) }
t('conditional') { |i| i.even? ? with_cond : with_cond(3) }
t('op-assign') { with_op_assign(0) }

# ---------------------------------------------------------------- 4. other ways of being called

class K
  def rest(*a); a; end
  def self.rest(*a); a; end
  def method_missing(name, *a); [name, a]; end
  def respond_to_missing?(*) = true
  def initialize(*a); @a = a; end
  attr_reader :a
  def priv(*a); hidden(*a); end
  private def hidden(*a); a; end
end
class KSub < K
  def rest(*a); super; end
  def rest_explicit(*a); method(:rest).super_method.call(*a); end
end
k = K.new

t('send') { k.send(:rest, 1, 2) }
t('public_send with a splat') { k.public_send(:rest, *[1, 2]) }
t('Method#call') { k.method(:rest).call(1, 2) }
t('Method#to_proc') { k.method(:rest).to_proc.call(1, 2) }
t('UnboundMethod#bind_call') { K.instance_method(:rest).bind_call(k, 1) }
t('a singleton method') { K.rest(1, 2) }
t('method_missing') { k.nothing(1, 2) }
t('initialize') { K.new(1, 2).a }
t('a private method') { k.priv(1, 2) }
t('super with implicit arguments') { KSub.new.rest(1, 2) }
t('through super_method') { KSub.new.rest_explicit(1, 2) }
t('define_method with a rest') { Class.new { define_method(:r) { |*a| a } }.new.r(1, 2) }
t('&method as a block') { [[1, 2], [3]].map(&k.method(:rest)) }
t('a Symbol to_proc of it') { [k].map(&:rest) }
t('arity') { [method(:rest).arity, method(:lead).arity, method(:opt).arity, method(:anon).arity] }
t('parameters') { [method(:rest).parameters, method(:lead).parameters, method(:opt).parameters, method(:anon).parameters] }

r2 = Class.new do
  ruby2_keywords def fwd(*a); target(*a); end
  def target(*a, **kw); [a, kw]; end
  def plain(*a); target(*a); end
end.new
t('ruby2_keywords passes keywords on') { r2.fwd(1, k: 2) }
t('without it they stay a Hash') { r2.plain(1, k: 2) }

# ---------------------------------------------------------------- 5. errors and frames

def needs_one(x, *a); a; end
def raises(*a); raise ArgumentError, "got #{a.size}"; end
def calls_missing(*a); a.no_such_method; end
def frame_label(*a); caller(0, 1)[0][/in .*/]; end
def frame_locations(*a); caller_locations(0, 1)[0].label; end
def deep(n, *a); n == 0 ? caller(0).size > 3 : deep(n - 1, *a); end

t('too few arguments') { needs_one }
t('an exception in the body') { |i| begin; raises(1, 2); rescue ArgumentError => e; [e.message, e.backtrace[0][/in .*/]]; end }
t('NoMethodError on the rest') { |i| begin; calls_missing(1); rescue NoMethodError => e; e.backtrace[0][/in .*/]; end }
t('caller') { frame_label(1) }
t('caller_locations') { frame_locations(1) }
t('frames of a recursion') { deep(4, 1) }

t('TracePoint :call sees the parameters') do |i|
  next :skipped unless i == 0 || i == N - 1
  seen = []
  tp = TracePoint.new(:call) { |e| seen << [e.method_id, e.binding.local_variable_get(:a)] if e.method_id == :rest }
  tp.enable { rest(1, 2) }
  seen
end
t('and after the TracePoint') { rest(1, 2) }
