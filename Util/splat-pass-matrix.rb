# Differential matrix for methods that pass a splatted argument on (`inner(*args)', `inner(*args, &block)',
# `[*a]', `yield(*a)') and run without a scope (a light frame, MethodDefinition.IsLightFrameCandidate).
# Run it under CRuby and under ./ir.sh with such frames on and off, and diff:
#
#   ruby Util/splat-pass-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/splat-pass-matrix.rb > /tmp/on.txt
#   IR_LIGHT_SPLAT=0 ./ir.sh Util/splat-pass-matrix.rb > /tmp/off.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt
#
# Every probe runs N times and reports each distinct answer once, so the interpreted body, the compiled
# one and a site whose rule is already bound all meet the case: what the callee is given for every kind
# of splatted value (nothing, nil, an Array, a non-Array, #to_a that raises or answers a non-Array), the
# Array being the callee's own, splats among other arguments, with a block, keywords and ruby2_keywords,
# who is asked (private, protected, method_missing, refined, redefined), other ways in (send, super,
# yield), what a scope made on demand (binding, local_variables, eval, caller, __method__) sees, and
# the frames in a backtrace.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/splat-pass-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

N = 3000

# n: a probe that makes many calls of its own each time (deep recursion) is hot after a few
def t(label, n = N)
  print label.ljust(52), ' => '
  seen = []
  begin
    n.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 70]}"
  end
end

def label(line) = line[/'(?:[^']*[#.])?([^']*)'/, 1]

# ---------------------------------------------------------------- 1. what the callee is given

def take(*a) = a
def two(x, y) = [x, y]
def opt(x, y = :d) = [x, y]
def blk(*a, &b) = [a, b ? b.call(*a) : :noblock]

def w_take(*a) = take(*a)
def w_local(x) = take(*x)
def w_nil = take(*nil)
def w_lead(*a) = take(0, *a)
def w_mid(*a) = take(1, *a, 2)
def w_two_splats(a, b) = take(*a, *b)
def w_two_mid(a, b) = take(:x, *a, :y, *b, :z)
def w_two(*a) = two(*a)
def w_opt(*a) = opt(*a)
def w_same(*a) = take(*a).equal?(a)
def w_mut(*a); r = take(*a) << :added; [a, r]; end
def w_pass(*a, &b) = blk(*a, &b)
def w_nested(*a) = w_take(*a)
def w_deeper(*a) = w_nested(*a, *a)
def w_target(o, *a) = o.take(*a)
def w_endless(*a) = take(*a, *a)
def w_cond(*a) = a.empty? ? take(*nil) : take(*a)

class ToA; def initialize(v) = @v = v; def to_a = @v; end
class ToARaises; def to_a = raise(IndexError, "to_a raised"); end
class NoToA; end
class PrivToA; private def to_a = [:private]; end
class ToAMissing; def method_missing(n, *) = n == :to_a ? [:mm] : super; def respond_to_missing?(n, p = false) = n == :to_a || super; end

t('a splat of nothing') { w_take }
t('a splat of one') { w_take(1) }
t('a splat of many') { w_take(1, 2, 3) }
t('a splat of nil') { w_nil }
t('a local nil') { w_local(nil) }
t('a local Array') { w_local([1, [2]]) }
t('a local Array is not the callee\'s') { a = [1]; w_local(a).equal?(a) }
t('a local Integer') { w_local(5) }
t('a local String') { w_local("s") }
t('a local Hash') { w_local({ a: 1 }) }
t('a local Range') { w_local(1..3) }
t('a local Struct') { w_local(Struct.new(:a, :b).new(1, 2)) }
t('an object with to_a') { w_local(ToA.new([7, 8])) }
t('to_a answers nil') { w_local(ToA.new(nil)).size }
t('to_a answers a non-Array') { begin; w_local(ToA.new(3)); rescue TypeError => e; e.message; end }
t('to_a answers a String') { begin; w_local(ToA.new("s")); rescue TypeError => e; e.message; end }
t('to_a raises') { begin; w_local(ToARaises.new); rescue IndexError => e; e.message; end }
t('no to_a') { w_local(NoToA.new).size }
t('a private to_a') { w_local(PrivToA.new) }
t('to_a by method_missing') { w_local(ToAMissing.new) }
t('a leading argument') { w_lead(1, 2) }
t('a splat in the middle') { w_mid(:a, :b) }
t('a splat of nothing in the middle') { w_mid }
t('two splats') { w_two_splats([1], [2, 3]) }
t('two splats of nil') { w_two_splats(nil, nil) }
t('two splats and arguments between') { w_two_mid([1], nil) }
t('into fixed parameters') { w_two(1, 2) }
t('into fixed parameters, too few') { begin; w_two(1); rescue ArgumentError => e; e.message; end }
t('into fixed parameters, too many') { begin; w_two(1, 2, 3); rescue ArgumentError => e; e.message; end }
t('into an optional parameter') { [w_opt(1), w_opt(1, 2)] }
t('the callee\'s Array is its own') { w_same(1, 2) }
t('the callee mutates its Array') { w_mut(1) }
t('a block passed with it') { w_pass(1, 2) { |x, y| x + y } }
t('no block passed with it') { w_pass(1) }
t('a lambda passed with it') { w_pass(1, 2, &->(x, y) { x * y }) }
t('two layers') { w_nested(4, 5) }
t('three layers, splatted twice') { w_deeper(1, 2) }
t('on a receiver') { o = Object.new; def o.take(*a) = [:o, a]; w_target(o, 1) }
t('the same Array twice') { w_endless(1, 2) }
t('a splat on each branch') { [w_cond, w_cond(3)] }

# ---------------------------------------------------------------- 2. other places a splat is written

def a_array(*a) = [*a, 1]
def a_array_nil(x) = [*x]
def a_array_only(*a) = [*a]
def a_return(*a) = (return *a)
def a_return_one(x) = (return *x)
def a_yield(*a) = yield(*a)
def a_yield_mid(*a) = yield(0, *a)
def a_when(x, *a) = case x; when *a then :in; else :out; end
def a_aset(h, k, v) = (h[*k] = v)
def a_masgn(*a); x, y = *a; [x, y]; end
def a_assign(x); y = *x; y; end
def a_lit(*a) = take(*a).map { |x| x * 2 }
def a_lit_blk(*a) = blk(*a) { |*x| x.sum }
def a_interp(*a) = "#{take(*a)}"
def a_and(*a) = a.empty? || take(*a)

t('an Array literal') { a_array(2, 3) }
t('an Array literal of nil') { a_array_nil(nil) }
t('an Array literal of a non-Array') { a_array_nil(ToA.new([4])) }
t('an Array literal is a new Array') { a = [1]; a_array_only(*a).equal?(a) }
t('return of a splat') { [a_return, a_return(1), a_return(1, 2)] }
t('return of a splat of nil') { a_return_one(nil) }
t('yield of a splat') { a_yield(1, 2) { |x, y| [x, y] } }
t('yield of a splat to one parameter') { a_yield(1, 2) { |x| x } }
t('yield of a splat to a lambda') { begin; a_yield(1, 2, &->(x) { x }); rescue ArgumentError => e; e.message; end }
t('yield of a splat of nothing') { a_yield { |*x| x } }
t('yield of a splat in the middle') { a_yield_mid(1) { |*x| x } }
t('yield of a splat without a block') { begin; a_yield(1); rescue LocalJumpError => e; e.message; end }
t('when of a splat') { [a_when(2, 1, 2), a_when(3, 1, 2), a_when(1)] }
t('an element assignment with a splat') { h = {}; a_aset(h, [:k], 1); h }
t('an element assignment to an Array') { a = [0, 0, 0]; a_aset(a, [1, 2], [9]); a }
t('a multiple assignment of a splat') { a_masgn(1, 2) }
t('an assignment of a splat') { [a_assign(nil), a_assign(1), a_assign([2])] }
t('a literal block with it') { a_lit(1, 2) }
t('a literal block on the splat call') { a_lit_blk(1, 2) }
t('interpolation') { a_interp(1) }
t('an and') { [a_and, a_and(2)] }

# ---------------------------------------------------------------- 3. keywords

def kw(*a, k: 0, **o) = [a, k, o]
def kw_only(k:) = k
def k_pass(*a) = kw(*a)
def k_with(*a) = kw(*a, k: 1)
def k_dsplat(a, h) = kw(*a, **h)
def k_hash_last(*a) = kw(*a, {})
def k_only(*a) = kw_only(*a)
ruby2_keywords def k_r2k(*a) = kw(*a)
ruby2_keywords def k_r2k_only(*a) = kw_only(*a)
ruby2_keywords def k_r2k_two(*a) = k_r2k(*a)
def k_wrap_kw(*a, **o) = kw(*a, **o)

t('a Hash passed through a splat') { k_pass(1, k: 5) }
t('a Hash passed through a splat, only') { k_pass(k: 5) }
t('a splat with a keyword') { k_with(1) }
t('a splat with a keyword, Hash in the splat') { k_with(1, { k: 3 }) }
t('a splat and a double splat') { k_dsplat([1], { k: 2, z: 3 }) }
t('a splat and an empty double splat') { k_dsplat([1], {}) }
t('a splat and an empty Hash') { k_hash_last(1) }
t('keywords into a keyword-only method') { begin; k_only(k: 4); rescue ArgumentError => e; e.message; end }
t('ruby2_keywords') { k_r2k(1, k: 6) }
t('ruby2_keywords, only keywords') { k_r2k_only(k: 7) }
t('ruby2_keywords, a plain Hash') { k_r2k(1, { k: 8 }) }
t('ruby2_keywords, two layers') { k_r2k_two(1, k: 9) }
t('a keyword wrapper') { k_wrap_kw(1, k: 10, z: 1) }
t('the flagged Hash is its own') { h = { k: 1 }; k_r2k(h)[0][0].equal?(h) }

# ---------------------------------------------------------------- 4. who is asked

class Target
  def m(*a) = [:target, a]
  def via(*a) = m(*a)
  def via_self(*a) = self.m(*a)
end
class Target2 < Target; def m(*a) = [:target2, a]; end
module Prep; def m(*a) = [:prep, a]; end
class BO < BasicObject; def go(*a) = m(*a); def m(*a) = [:bo, a]; end
class MM; def go(*a) = nothing(*a); def method_missing(n, *a) = [n, a]; def respond_to_missing?(*) = true; end
class Priv
  def go(o, *a) = o.secret(*a)
  def go_self(*a) = self.secret(*a)
  def go_implicit(*a) = secret(*a)
  private def secret(*a) = [:secret, a]
end
class Prot; def go(o, *a) = o.guarded(*a); protected def guarded(*a) = [:guarded, a]; end

t('the class\'s own') { Target.new.via(1) }
t('a subclass\'s own') { Target2.new.via(1) }
t('a singleton method') { o = Target.new; def o.m(*a) = [:single, a]; o.via(2) }
t('removed after the site is bound') { |i| c = Class.new(Target) { def m(*a) = [:c, a] }; c.send(:remove_method, :m) if i.odd?; c.new.via(3) }
t('undefined after the site is bound') { |i| c = Class.new(Target) { def m(*a) = [:c, a] }; c.send(:undef_method, :m) if i.odd?; begin; c.new.via(3); rescue NoMethodError => e; e.class; end }
t('aliased over after the site is bound') { |i| c = Class.new(Target) { def m(*a) = [:c, a]; def other(*a) = [:other, a] }; c.send(:alias_method, :m, :other) if i.odd?; c.new.via(4) }
t('prepended later') { |i| c = Class.new(Target); c.prepend(Prep) if i.odd?; c.new.via(5) }
t('included later') { |i| c = Class.new(Target) { def via(*a) = m2(*a) }; m = Module.new { def m2(*a) = [:inc, a] }; c.include(m) if i.odd?; begin; c.new.via(6); rescue NoMethodError => e; e.class; end }
t('BasicObject') { BO.new.go(1) }
t('method_missing') { MM.new.go(1, 2) }
t('method_missing, a splat of nothing') { MM.new.go }
t('a private method with a receiver') { begin; Priv.new.go(Priv.new, 1); rescue NoMethodError => e; e.message[0, 30]; end }
t('a private method on self.') { Priv.new.go_self(1) }
t('a private method, implicit self') { Priv.new.go_implicit(1) }
t('a protected method') { Prot.new.go(Prot.new, 1) }
t('a protected method from outside') { begin; Prot.new.go(Object.new, 1); rescue NoMethodError => e; e.class; end }
t('redefined after the site is bound') { |i| Target.class_eval { def m(*a) = [:redefined, a] } if i == 1500; Target.new.via(7) }

module Kernel; alias_method :__splat_matrix_format, :format; end
def r_format(*a) = format(*a)
t('a Kernel method replaced and put back') do |i|
  if i == 1000
    module Kernel; def format(*a) = [:replaced, a]; end
  elsif i == 2000
    module Kernel; alias_method :format, :__splat_matrix_format; end
  end
  r_format("%d", 1)
end

# ---------------------------------------------------------------- 5. other ways in

class Base; def via(*a) = [:base, a]; def kw_via(*a, k: 0) = [:base, a, k]; end
class Sup < Base; def via(*a) = super(*a); end
class Sup2 < Base; def via(*a) = super(0, *a); end
class ZSup < Base; def via(*a) = super; end
class SupKw < Base; ruby2_keywords def kw_via(*a) = super(*a); end

def s_send(*a) = send(*a)
def s_send_name(*a) = send(:take, *a)
def s_public_send(*a) = public_send(*a)
def s___send__(*a) = __send__(*a)
def s_call(m, *a) = m.call(*a)
def s_proc(pr, *a) = pr.(*a)
def s_bind_call(*a) = Object.instance_method(:take).bind_call(self, *a)
module MF; module_function; def mf(*a) = MF.helper(*a); def helper(*a) = [:mf, a]; end
def define_wrapper; self.class.send(:define_method, :dm_wrap) { |*a| take(*a) }; end

t('super(*args)') { Sup.new.via(1, 2) }
t('super with a splat in the middle') { Sup2.new.via(1) }
t('zsuper') { ZSup.new.via(1) }
t('super with ruby2_keywords') { SupKw.new.kw_via(1, k: 2) }
t('send(*args)') { s_send(:take, 1, 2) }
t('send(:name, *args)') { s_send_name(3) }
t('send(*args) of a private method') { s_send(:two, 1, 2) }
t('send(*args) of nothing') { begin; s_send; rescue ArgumentError => e; e.message; end }
t('public_send(*args) of a private method') { begin; s_public_send(:two, 1, 2); rescue NoMethodError => e; e.class; end }
t('public_send(*args)') { s_public_send(:inspect).class }
t('__send__(*args)') { s___send__(:take, 4) }
t('send(*args) of block_given?') { [s_send(:block_given?), s_send(:block_given?) { }] }
t('Method#call(*args)') { s_call(method(:take), 1) }
t('Proc#call(*args)') { s_proc(proc { |*x| x }, 1, 2) }
t('a lambda called with a splat') { begin; s_proc(->(x) { x }, 1, 2); rescue ArgumentError => e; e.message; end }
t('bind_call(*args)') { s_bind_call(5) }
t('module_function') { MF.mf(1) }
t('define_method wrapper') { define_wrapper; dm_wrap(1, 2) }
t('send into the wrapper') { send(:w_take, 6) }
t('Method#to_proc of the wrapper') { [[1, 2]].map(&method(:w_take)) }
t('Symbol#to_proc of the wrapper') { [Target.new].map(&:via) }
t('bind_call of the wrapper') { Object.instance_method(:w_take).bind_call(self, 7) }

# ---------------------------------------------------------------- 6. the callee reads the caller's frame

def f_binding(*a) = binding(*a).local_variable_get(:a)
def f_locals(*a) = local_variables(*a)
def f_locals_more(*a); x = 1; local_variables(*a).sort; end
def f_block_given(*a) = block_given?(*a)
def f_eval(*a); x = 5; eval(*a); end
def f_caller(*a) = label(caller(*a)[0])
def f_caller_locations(*a) = caller_locations(*a)[0].label
def f_method(*a) = __method__(*a)
def f_iterator(*a) = respond_to?(:take, *a)
def f_binding_after(*a); take(*a); binding.local_variable_get(:a); end
def f_locals_after(*a); take(*a); local_variables; end
def f_eval_after(*a); take(*a); eval("a"); end
def f_reassign(*a); a = [:other]; take(*a); end
def f_local_after(*a); r = take(*a); r.size; end
def f_recurse(n, *a) = n == 0 ? a : f_recurse(n - 1, *a, n)
class CountToA; attr_reader :n; def initialize = @n = 0; def to_a = (@n += 1; []); end
def f_to_a_once(x) = [local_variables(*x), x.n]
def f_to_a_once_mm(o, x) = [o.nothing(*x), x.n]

t('binding(*a)') { f_binding }
t('local_variables(*a)') { f_locals }
t('local_variables(*a), more locals') { f_locals_more }
t('block_given?(*a)') { [f_block_given, f_block_given { }] }
t('eval(*a) of a local') { f_eval("x + 1") }
t('eval(*a) of the rest parameter') { f_eval("a.size") }
t('eval(*a) of nothing') { begin; f_eval; rescue ArgumentError => e; e.message; end }
t('caller(*a)') { f_caller(0, 1) }
t('caller(*a) of nothing') { f_caller }
t('caller_locations(*a)') { f_caller_locations(0, 1) }
t('__method__(*a)') { f_method }
t('__method__(*a) with an argument') { begin; f_method(1); rescue ArgumentError => e; e.message; end }
t('respond_to?(*a)') { f_iterator(true) }
t('binding after') { f_binding_after(1, 2) }
t('local_variables after') { f_locals_after(1) }
t('eval after') { f_eval_after(1, 2) }
t('reassigning the rest parameter') { f_reassign(1) }
t('a local after') { f_local_after(1, 2, 3) }
t('recursion') { f_recurse(5) }
# 300 calls a time, each passing a longer splat on: 50 times is 15,000 calls, past every
# threshold, where N times took minutes (the copying is quadratic, and slower than CRuby's)
t('deep recursion', 50) { f_recurse(300).size }
t('to_a once when the call needs the scope') { f_to_a_once(CountToA.new) }
t('to_a once into method_missing') { f_to_a_once_mm(MM.new, CountToA.new) }

# ---------------------------------------------------------------- 7. errors and backtraces

def b_inner(*a) = raise(ArgumentError, "inner #{a.size}")
def b_wrap(*a) = b_inner(*a)
def b_wrap2(*a) = b_wrap(*a)
def b_raise(*a) = raise(*a)
def b_arity(*a) = two(*a)
def b_frames(*a) = caller(0, 3).map { |l| label(l) }
def b_wrap_frames(*a) = b_frames(*a)

t('raise through the wrapper') { begin; b_wrap2(1); rescue ArgumentError => e; [e.message, e.backtrace[0, 3].map { |l| label(l) }]; end }
t('raise(*a)') { begin; b_raise(TypeError, "splat"); rescue TypeError => e; [e.message, label(e.backtrace[0])]; end }
t('raise(*a) of a String') { begin; b_raise("plain"); rescue RuntimeError => e; [e.message, label(e.backtrace[0])]; end }
t('raise(*a) of nothing') { begin; b_raise; rescue RuntimeError => e; e.message; end }
t('an arity error\'s frame') { begin; b_arity(1, 2, 3); rescue ArgumentError => e; e.backtrace[0, 2].map { |l| label(l) }; end }
t('the callee\'s caller frames') { b_wrap_frames(1) }
t('a NoMethodError of the receiver') { begin; w_target(nil, 1); rescue NoMethodError => e; e.message[0, 40]; end }

# ---------------------------------------------------------------- 8. core methods

t('String#strip') { [" a ".strip, "xxaxx".strip("x"), "xyaxy".strip("x", "y")] }
t('String#lstrip and rstrip') { ["xxaxx".lstrip("x"), "xxaxx".rstrip("x")] }
t('String#strip of a bad selector') { begin; "a".strip(1); rescue TypeError => e; e.message; end }

# ---------------------------------------------------------------- 9. tracing

def traced(*a) = take(*a)
def traced_c(*a) = [1, 2].push(*a)
t('TracePoint :call sees the wrapper') do
  seen = []
  tp = TracePoint.new(:call) { |e| seen << e.method_id if e.method_id == :traced || e.method_id == :take }
  tp.enable { traced(1) }
  seen
end
t('TracePoint :c_call sees the call') do
  seen = []
  tp = TracePoint.new(:c_call) { |e| seen << e.method_id if e.method_id == :push }
  tp.enable { traced_c(3) }
  seen
end
t('TracePoint :call, binding of the wrapper') do
  seen = []
  tp = TracePoint.new(:call) { |e| seen << e.binding.local_variable_get(:a) if e.method_id == :traced }
  tp.enable { traced(1, 2) }
  seen
end
t('after the TracePoint') { [traced(1), traced_c(4)] }

# ---------------------------------------------------------------- 10. refinements (last: `using' keeps every method
# defined after it in this file out of light frames)

def r_before(*a) = 1.refined_m(*a)
module Ref; refine(Integer) { def refined_m(*a) = [:refined, a] }; end
module RefUnused; refine(Integer) { def unused_m(*a) = [:unused, a] }; end
t('a refinement not in use where it is written') { begin; r_before(1); rescue NoMethodError => e; e.class; end }
using Ref
def r_refined(*a) = 1.refined_m(*a)
def r_unused(*a) = 1.unused_m(*a)
t('a refined method') { r_refined(1, 2) }
t('a refinement that exists but is not used') { begin; r_unused(1); rescue NoMethodError => e; e.class; end }
