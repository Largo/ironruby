# Differential matrix for methods that pass a block on (`inner(&block)', `each(&:sym)') and run without
# a scope (a light frame, MethodDefinition.IsLightFrameCandidate). Run it under CRuby and under ./ir.sh
# with such frames on and off, and diff:
#
#   ruby Util/block-pass-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/block-pass-matrix.rb > /tmp/on.txt
#   IR_LIGHT_BLOCK_PASS=0 ./ir.sh Util/block-pass-matrix.rb > /tmp/off.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt
#
# Every probe runs N times and reports each distinct answer once, so the interpreted body, the compiled
# one and a site whose rule is already bound all meet the case: what the callee is given for every kind
# of block argument, what it does with it (yield, block_given?, define_method, instance_exec, proc,
# lambda), how break, next, return, throw and retry in the block leave the methods between, what a
# scope made on demand (binding, local_variables, eval, a TracePoint) sees, errors, and the frames in a
# backtrace.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/block-pass-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
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

# ---------------------------------------------------------------- 1. what the callee is given

def take(&b); b; end
def yield1; yield 1; end
def given?; block_given?; end
def kind(&b); b.nil? ? :none : b.lambda? ? :lambda : :proc; end

def w_take(&b); take(&b); end
def w_yield(&b); yield1(&b); end
def w_given(&b); given?(&b); end
def w_kind(&b); kind(&b); end
def w_nil; given?(&nil); end
def w_sym; [1, 2].map(&:to_s); end
def w_arg(x, &b); x.map(&b); end
def w_two(&b); w_yield(&b); end
def w_three(&b); w_two(&b); end
def w_endless(&b) = yield1(&b)
def w_ivar; yield1(&@blk); end
def w_local(f); yield1(&f); end
def w_anon(&) = yield1(&)

t('a block, passed on') { w_yield { |x| x + 1 } }
t('no block, passed on') { w_given }
t('a block, block_given? in the callee') { w_given { } }
t('the same Proc object') { pr = proc { }; w_take(&pr).equal?(pr) }
t('a literal block is a proc') { w_kind { } }
t('a lambda stays a lambda') { w_kind(&->(x) { x }) }
t('no block is nil') { w_take.inspect }
t('&nil') { w_nil }
t('&:sym') { w_sym }
t('a block through an argument') { w_arg([1, 2]) { |x| x * 3 } }
t('two layers') { w_two { |x| x * 5 } }
t('three layers') { w_three { |x| x * 7 } }
t('an endless def') { w_endless { |x| -x } }
t('&@ivar, nil') { @blk = nil; begin; w_ivar; rescue LocalJumpError => e; e.class; end }
t('&@ivar, a Proc') { @blk = proc { |x| x * 11 }; w_ivar }
t('a local Proc') { w_local(proc { |x| x + 100 }) }
t('a local Method') { w_local(1.method(:+)) }
t('a local Symbol') { w_local(:succ) }
t('a local with to_proc') { o = Object.new; def o.to_proc = proc { |x| [:tp, x] }; w_local(o) }
t('a local whose to_proc is not a Proc') { o = Object.new; def o.to_proc = 1; begin; w_local(o); rescue TypeError => e; e.message; end }
t('a local without to_proc') { begin; w_local(1); rescue TypeError => e; e.message; end }
t('a local nil') { begin; w_local(nil); rescue LocalJumpError => e; e.class; end }
t('anonymous &') { w_anon { |x| [:anon, x] } }

# ---------------------------------------------------------------- 2. what the callee does with it

def dm(name, &b); self.class.send(:define_method, name, &b); end
def w_ie(o, &b); o.instance_exec(5, &b); end
def w_proc(&b); proc(&b); end
def w_lambda(&b); lambda(&b); end
def w_binding_callee(&b); binding_of(&b); end
def binding_of(&b); b.binding.local_variable_get(:v); end
def w_each(a, &b); a.each(&b); end
def w_each_with_index(a, &b); a.each_with_index(&b); end
def w_send(&b); send(:yield1, &b); end
def w_public_send(&b); public_send(:yield1, &b); end
def w_call(&b); method(:yield1).call(&b); end
def w_tap(&b); 42.tap(&b); end
def w_then(&b); 42.then(&b); end
def w_to_enum(a, &b); a.map(&b); end

t('define_method') { dm(:dm_probe) { |x| x * 2 }; dm_probe(4) }
t('instance_exec') { w_ie("s") { |x| [self, x] } }
t('Kernel#proc') { w_proc { 9 }.call }
t('Kernel#lambda keeps a proc a proc') { w_lambda { 9 }.lambda? }
t('the block\'s binding') { v = 77; w_binding_callee { v } }
t('Array#each') { r = []; w_each([1, 2]) { |x| r << x }; r }
t('each_with_index') { r = []; w_each_with_index([:a]) { |x, i| r << [x, i] }; r }
t('send') { w_send { |x| x + 1 } }
t('public_send') { w_public_send { |x| x + 2 } }
t('Method#call') { w_call { |x| x + 3 } }
t('tap') { r = nil; w_tap { |x| r = x }; r }
t('then') { w_then { |x| x + 1 } }
t('map without a block is an Enumerator') { w_to_enum([1]).class }

# ---------------------------------------------------------------- 3. leaving through the block

def r_return_in_block; w_yield { return :from_block }; :after; end
def r_return_through_two; w_two { return :two }; :after; end
def r_break; w_yield { break :broke }; end
def r_break_each; w_each([1, 2, 3]) { |x| break x * 10 if x == 2 }; end
def r_next; w_yield { next :nexted }; end
def r_proc_return_orphan; proc { return :orphan }; end
def r_lambda_return; w_yield(&->(x) { return x + 1 }); end
def r_throw; catch(:t) { w_yield { throw :t, :thrown } }; end
def r_raise; w_yield { raise "boom" }; rescue => e; e.message; end
def r_ensure; log = []; begin; w_yield { log << :in; raise "x" }; rescue; log << :rescued; ensure; log << :ensure; end; log; end
def r_retry; n = 0; begin; n += 1; w_yield { raise "again" if n < 3; n }; rescue; retry; end; end
def r_value_used; x = w_yield { 5 }; x + 1; end
def r_break_proc; proc { break 1 }; end

t('return in the block') { r_return_in_block }
t('return through two layers') { r_return_through_two }
t('break') { r_break }
t('break from each') { r_break_each }
t('next') { r_next }
t('return in an orphan proc') { begin; w_yield(&r_proc_return_orphan); rescue LocalJumpError => e; e.reason; end }
t('return in a lambda') { r_lambda_return }
t('throw') { r_throw }
t('raise') { r_raise }
t('ensure runs') { r_ensure }
t('retry in a rescue around it') { r_retry }
t('the value') { r_value_used }
t('break in an orphan proc') { begin; w_yield(&r_break_proc); rescue LocalJumpError => e; e.reason; end }

# ---------------------------------------------------------------- 4. what the frame still does

def f_binding(&b); yield1(&b); binding.local_variable_defined?(:b); end
def f_locals(&b); yield1(&b); local_variables; end
def f_eval(&b); yield1(&b); eval("b").class; end
def f_local_after(&b); r = yield1(&b); r * 2; end
def f_block_given(&b); yield1(&b) if block_given?; end
def f_raise(&b); yield1(&b); raise ArgumentError, "label"; end
def f_caller(&b); yield1(&b); caller(0, 1)[0][/'.*'/]; end
def f_recurse(n, &b); n == 0 ? yield1(&b) : f_recurse(n - 1, &b); end
def f_method(&b); yield1(&b); __method__; end
def f_reassign(&b); b = proc { |x| [:other, x] }; yield1(&b); end
def f_nil_reassign(&b); b = nil; given?(&b); end
def f_later_literal(&b); r = 0; yield1(&b); [1].each { |x| r += x }; r; end

t('binding after') { f_binding { } }
t('local_variables after') { f_locals { } }
t('eval after') { f_eval { } }
t('a local after') { f_local_after { |x| x + 1 } }
t('block_given? guard') { [f_block_given { 3 }, f_block_given] }
t('raise and its frame') { begin; f_raise { }; rescue ArgumentError => e; [e.message, e.backtrace[0][/'.*'/]]; end }
t('caller') { f_caller { } }
t('the block\'s backtrace frames') { w_two { caller(0, 4).map { _1[/'.*'/] } } }
t('recursion') { f_recurse(5) { |x| x + 1 } }
t('deep recursion') { f_recurse(500) { |x| x + 2 } }
t('__method__') { f_method { } }
t('reassigning the block parameter') { f_reassign { } }
t('reassigning it to nil') { f_nil_reassign { } }
t('a later literal block') { f_later_literal { } }

# ---------------------------------------------------------------- 5. who is asked

class Target
  def yield1; yield 2; end
  def via(&b); yield1(&b); end
end
class Target2 < Target; def yield1; yield 3; end; end
module Prep; def yield1; yield 4; end; end
class BO < BasicObject; def go(&b); m(&b); end; def m; yield 6; end; end
class MM; def go(&b); nothing(&b); end; def method_missing(n, *a, &b) = [n, b.call]; def respond_to_missing?(*) = true; end
class Priv; def go(o, &b); o.secret(&b); end; private def secret; yield; end; end
class Prot; def go(o, &b); o.guarded(&b); end; protected def guarded; yield 8; end; end

t('the class\'s own') { Target.new.via { |x| x } }
t('a subclass\'s own') { Target2.new.via { |x| x } }
t('a singleton method') { o = Target.new; def o.yield1; yield 5; end; o.via { |x| x } }
t('removed after the site is bound') { |i| c = Class.new(Target) { def yield1; yield 11; end }; c.send(:remove_method, :yield1) if i.odd?; c.new.via { |x| x } }
t('prepended later') { |i| c = Class.new(Target); c.prepend(Prep) if i.odd?; c.new.via { |x| x } }
t('BasicObject') { BO.new.go { |x| x } }
t('method_missing') { MM.new.go { 7 } }
t('a private method with a receiver') { begin; Priv.new.go(Priv.new) { }; rescue NoMethodError => e; e.message[0, 30]; end }
t('a protected method') { Prot.new.go(Prot.new) { |x| x } }
t('a protected method from outside') { begin; Prot.new.go(Object.new) { }; rescue NoMethodError => e; e.class; end }
t('defined after the site is bound') { |i| Target.class_eval { def yield1; yield 10; end } if i == 1500; Target.new.via { |x| x } }

module Ref; refine(Integer) { def refined_y; yield 99; end }; end
using Ref
def r_refined(&b); 1.refined_y(&b); end
t('a refined method') { r_refined { |x| x } }

# ---------------------------------------------------------------- 6. other ways in

class Delegator1; def initialize(t) = @t = t; def each(&b) = @t.each(&b); end
def define_wrapper; self.class.send(:define_method, :dm_wrap) { |&b| yield1(&b) }; end
module MF; module_function; def mf(&b) = [1].map(&b); end

t('a delegator\'s each') { r = []; Delegator1.new([1, 2]).each { |x| r << x }; r }
t('define_method wrapper') { define_wrapper; dm_wrap { |x| x + 40 } }
t('module_function') { MF.mf { |x| x + 1 } }
t('send into the wrapper') { send(:w_yield) { |x| x + 50 } }
t('Method#to_proc of the wrapper') { [1].map(&method(:w_kind)) }
t('bind_call') { Object.instance_method(:w_yield).bind_call(self) { |x| x + 60 } }
t('super passes the block') { Class.new(Target) { def via(&b) = super(&b) }.new.via { |x| x + 70 } }
t('zsuper passes the block') { Class.new(Target) { def via(&b) = super }.new.via { |x| x + 80 } }

# ---------------------------------------------------------------- 7. tracing

def traced(&b); yield1(&b); end
t('TracePoint :call sees the wrapper') do
  seen = []
  tp = TracePoint.new(:call) { |e| seen << e.method_id if e.method_id == :traced || e.method_id == :yield1 }
  tp.enable { traced { } }
  seen
end
t('TracePoint :c_call sees the call') do
  seen = []
  tp = TracePoint.new(:c_call) { |e| seen << e.method_id if e.method_id == :map }
  tp.enable { w_arg([1]) { } }
  seen
end
t('after the TracePoint') { traced { |x| x + 1 } }
