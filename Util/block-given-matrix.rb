# Differential matrix for `block_given?' in a method that runs without a scope (a light frame,
# MethodDefinition.TransformLightFrame): there the call is answered from the frame's block parameter
# when it resolves to Kernel#block_given? (DispatchIntrinsics.TryBuildBlockGiven) instead of making
# the method's scope for Kernel#block_given? to read. Run it under CRuby and under ./ir.sh with the
# shortcut on and off, and diff:
#
#   ruby Util/block-given-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/block-given-matrix.rb > /tmp/on.txt
#   IR_LIGHT_BLOCK_GIVEN=0 ./ir.sh Util/block-given-matrix.rb > /tmp/off.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/block-given-matrix.rb > /tmp/noframes.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt && diff /tmp/mri.txt /tmp/noframes.txt
#
# Every probe runs N times and reports each distinct answer once, in the order they first appear, so
# the interpreted body, the compiled one and a site whose rule is already bound all meet the case: a
# block_given? of the class's own, one defined or removed after the site is bound, a receiver without
# Kernel, method_missing, a refinement, a TracePoint, a block parameter that was reassigned.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/block-given-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

N = 3000

def t(label)
  print label.ljust(58), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 60]}"
  end
end

# ---------------------------------------------------------------- 1. the plain question

def plain; block_given?; end
def guard; return :no_block unless block_given?; yield; end
def guard_enum; return to_enum(:guard_enum) { 3 } unless block_given?; yield 1; end
def cond; block_given? ? yield : :none; end
def yield_if; yield if block_given?; end
def twice; [block_given?, block_given?]; end
def with_args(a, b = 2); block_given? ? a + b : -a; end
def in_while; i = 0; r = 0; while i < 3; r += 1 if block_given?; i += 1; end; r; end
def after_call; x = [1, 2].size; block_given? && x; end
def named(&blk); block_given?; end
def named_cleared(&blk); blk = nil; block_given?; end
def named_set(&blk); blk = proc { 1 }; block_given?; end
def explicit_self; self.block_given?; end
def kernel_receiver; Kernel.block_given?; end
def with_argument; block_given?(1); end
def not_given; !block_given?; end
def and_ivar; @seen = block_given?; @seen; end

t('no block') { plain }
t('a block') { plain { } }
t('&nil') { plain(&nil) }
t('&proc') { pr = proc { }; plain(&pr) }
t('&:sym') { plain(&:to_s) }
t('&method') { plain(&method(:puts)) }
t('alternating') { |i| i.odd? ? plain { } : plain }
t('guard, no block') { guard }
t('guard, block') { guard { :yielded } }
t('guard with to_enum, no block') { guard_enum.size }
t('guard with to_enum, block') { guard_enum { |x| x + 1 } }
t('conditional, both') { |i| i % 3 == 0 ? cond : cond { :y } }
t('yield if block_given?, both') { |i| i % 3 == 0 ? yield_if : yield_if { :y } }
t('asked twice') { twice { } }
t('with arguments, both') { |i| i.even? ? with_args(1) : with_args(1, 5) { } }
t('in a while loop') { |i| i.even? ? in_while : in_while { } }
t('after a call') { |i| i.even? ? after_call : after_call { } }
t('named block parameter') { |i| i.even? ? named : named { } }
t('named block parameter set to nil') { named_cleared { } }
t('named block parameter set, none given') { named_set }
t('self.block_given?') { |i| i.even? ? explicit_self : explicit_self { } }
t('Kernel.block_given?') { |i| i.even? ? kernel_receiver : kernel_receiver { } }
t('block_given?(1)') { with_argument { } }
t('!block_given?') { |i| i.even? ? not_given : not_given { } }
t('stored in an ivar') { |i| i.even? ? and_ivar : and_ivar { } }

# ---------------------------------------------------------------- 2. who is asked

def in_block; r = nil; [1].each { r = block_given? }; r; end
def in_nested_block; r = nil; [1].each { [2].each { r = block_given? } }; r; end
def in_proc; proc { block_given? }; end
def in_lambda; -> { block_given? }; end
def by_send; send(:block_given?); end
def by_dunder_send; __send__(:block_given?); end
def by_method; method(:block_given?).call; end
def by_eval; eval("block_given?"); end
def by_binding; binding.eval("block_given?"); end
def inner_def; def inner_asked; block_given?; end; inner_asked; end
def calls_plain; plain; end
def passes_on(&b); plain(&b); end
def by_thread; Thread.new { block_given? }.value; end

t('in a block of the method') { |i| i.even? ? in_block : in_block { } }
t('in a nested block') { |i| i.even? ? in_nested_block : in_nested_block { } }
t('in a proc called after the return') { |i| (i.even? ? in_proc : in_proc { }).call }
t('in a lambda called after the return') { |i| (i.even? ? in_lambda : in_lambda { }).call }
t('send(:block_given?)') { |i| i.even? ? by_send : by_send { } }
t('__send__(:block_given?)') { |i| i.even? ? by_dunder_send : by_dunder_send { } }
t('method(:block_given?).call') { |i| i.even? ? by_method : by_method { } } if N <= 3000
t('eval') { |i| i.even? ? by_eval : by_eval { } }
t('binding.eval') { |i| i.even? ? by_binding : by_binding { } }
t('a method defined in the method') { inner_def { } }
t('a call without a block from one with') { calls_plain { } }
t('a block passed on') { |i| i.even? ? passes_on : passes_on { } }
t('in a thread of the method') { |i| i.even? ? by_thread : by_thread { } } if false

class Base
  def asked; block_given?; end
  def self.asked; block_given?; end
end
class Sub < Base
  def asked; super; end
  def asked_explicit(&b); super(&b); end
  def asked_none; super(&nil); end
end
class Base; def asked_explicit; block_given?; end; def asked_none; block_given?; end; end

t('super passes the block implicitly') { |i| i.even? ? Sub.new.asked : Sub.new.asked { } }
t('super(&b)') { |i| i.even? ? Sub.new.asked_explicit : Sub.new.asked_explicit { } }
t('super(&nil)') { Sub.new.asked_none { } }
t('a singleton method') { |i| i.even? ? Base.asked : Base.asked { } }

module Fn
  module_function
  def asked; block_given?; end
end
t('a module function') { |i| i.even? ? Fn.asked : Fn.asked { } }

o = Object.new
def o.asked; block_given?; end
t('a method of one object') { |i| i.even? ? o.asked : o.asked { } }

dm = Class.new { define_method(:asked) { block_given? } }.new
def outer_for_dm(k); k.asked { }; end
t('define_method body: the defining frame') { outer_for_dm(dm) }

s = Struct.new(:a) { def asked; block_given?; end }.new(1)
t('a Struct method') { |i| i.even? ? s.asked : s.asked { } }

# ---------------------------------------------------------------- 3. a block_given? that is not Kernel's

class Own
  def block_given?; :own; end
  def asked; block_given?; end
end
t('the class defines block_given?') { Own.new.asked { } }

class Bare < BasicObject
  def asked; block_given?; end
end
t('a BasicObject: no Kernel') { Bare.new.asked { } }

class Missing < BasicObject
  def asked; block_given?; end
  def method_missing(name, *args); [:missing, name]; end
end
t('a BasicObject with method_missing') { Missing.new.asked { } }

class WithKernel < BasicObject
  include ::Kernel
  def asked; block_given?; end
end
t('a BasicObject including Kernel') { |i| i.even? ? WithKernel.new.asked : WithKernel.new.asked { } }

# the site is bound to Kernel#block_given? first, then the method changes under it
class Later; def asked; block_given?; end; end
later = Later.new
t('defined later on the class') do |i|
  Later.class_eval { def block_given?; :later; end } if i == N / 2
  later.asked { }
end
Later.send(:remove_method, :block_given?)
t('removed again') { later.asked { } }

t('a singleton block_given? on the receiver') do |i|
  def later.block_given?; :singleton; end if i == N / 2
  later.asked { }
end
class << later; remove_method :block_given?; end

module Shadow; def block_given?; :included; end; end
class Later2; def asked; block_given?; end; end
later2 = Later2.new
t('a module included later') do |i|
  Later2.include(Shadow) if i == N / 2
  later2.asked { }
end

class Later3; def asked; block_given?; end; end
later3 = Later3.new
t('a module prepended later') do |i|
  Later3.prepend(Shadow) if i == N / 2
  later3.asked { }
end

class Undef; def asked; block_given?; end; end
undef_ = Undef.new
t('undef_method later') do |i|
  Undef.send(:undef_method, :block_given?) if i == N / 2
  undef_.asked { }
end

class Aliased
  alias_method :given?, :block_given?
  def asked; given?; end
end
t('an alias of block_given?') { |i| i.even? ? Aliased.new.asked : Aliased.new.asked { } }

class AliasOver
  def other; :other; end
  def asked; block_given?; end
end
alias_over = AliasOver.new
t('another method aliased over the name later') do |i|
  AliasOver.send(:alias_method, :block_given?, :other) if i == N / 2
  alias_over.asked { }
end

class Private; def asked; block_given?; end; def with_receiver(o); o.block_given?; end; end
t('an explicit receiver: private') { Private.new.with_receiver(Private.new) { } }

# Kernel#block_given? itself replaced for a while, and put back
class Global; def asked; block_given?; end; end
global = Global.new
t('Kernel#block_given? redefined, then restored') do |i|
  if i == N / 3
    Kernel.module_eval do
      alias_method :__saved_block_given, :block_given?
      def block_given?; :kernel_redefined; end
    end
  elsif i == 2 * N / 3
    Kernel.module_eval do
      alias_method :block_given?, :__saved_block_given
      remove_method :__saved_block_given
    end
  end
  global.asked { }
end

# ---------------------------------------------------------------- 4. refinements

module Refined
  refine Object do
    def block_given?; :refined; end
  end
end

class Plainly; def asked; block_given?; end; end
t('a refinement elsewhere, not used here') { |i| i.even? ? Plainly.new.asked : Plainly.new.asked { } }

module UsesIt
  using Refined
  class Asker; def asked; block_given?; end; end
  def self.run; Asker.new.asked { }; end
end
t('a refinement in use') { UsesIt.run }
t('and still Kernel\'s where it is not') { |i| i.even? ? Plainly.new.asked : Plainly.new.asked { } }

# ---------------------------------------------------------------- 5. tracing

class Traced; def asked; block_given?; end; end
traced = Traced.new
t('TracePoint :c_call sees it') do |i|
  next :skipped unless i == 0 || i == N - 1
  names = []
  tp = TracePoint.new(:c_call) { |e| names << e.method_id if e.method_id == :block_given? }
  tp.enable { traced.asked { } }
  names
end
t('and after the TracePoint') { |i| i.even? ? traced.asked : traced.asked { } }

t('TracePoint :call of the method') do |i|
  next :skipped unless i == N - 1
  names = []
  tp = TracePoint.new(:call) { |e| names << e.method_id }
  tp.enable { traced.asked { } }
  names
end

# ---------------------------------------------------------------- 6. what the frame still does afterwards

def then_binding; g = block_given?; [g, binding.local_variable_get(:g)]; end
def then_locals; g = block_given?; [g, local_variables]; end
def then_block; g = block_given?; r = []; [1, 2].each { |x| r << [x, g] }; r; end
def then_raise; raise ArgumentError, "given: #{block_given?}"; end
def then_return_from_block; [1].each { return block_given? }; :not_reached; end
def then_yield_break; block_given? and yield; :after; end
def then_caller; block_given? ? caller(0, 1)[0][/in .*/] : nil; end
def recursive(n, &b); n == 0 ? block_given? : recursive(n - 1, &b); end

t('then binding') { |i| i.even? ? then_binding : then_binding { } }
t('then local_variables') { |i| i.even? ? then_locals : then_locals { } }
t('then a block reading the answer') { |i| i.even? ? then_block : then_block { } }
t('then raise') { |i| begin; i.even? ? then_raise : then_raise { }; rescue ArgumentError => e; e.message; end }
t('then return from a block') { |i| i.even? ? then_return_from_block : then_return_from_block { } }
t('then a block that breaks') { then_yield_break { break :broke } }
t('then caller') { then_caller { } }
t('recursion passing the block on') { |i| i.even? ? recursive(5) : recursive(5) { } }
