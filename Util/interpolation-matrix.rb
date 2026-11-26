# Differential matrix for methods whose body interpolates ("a#{x}") and run without a scope (a light
# frame, MethodDefinition.IsLightFrameCandidate). Run it under CRuby and under ./ir.sh with such frames
# on and off, and with the JIT off, and diff:
#
#   ruby Util/interpolation-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/interpolation-matrix.rb > /tmp/on.txt
#   IR_LIGHT_INTERPOLATION=0 ./ir.sh Util/interpolation-matrix.rb > /tmp/off.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/interpolation-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Every probe runs N times and reports each distinct answer once, so the interpreted body, the compiled
# one and a site whose rule is already bound all meet the case: what #to_s each kind of part is asked
# (a String, nil, an Integer, a user class, #to_s that answers a non-String, raises, is missing, private,
# answered by method_missing, redefined between calls, prepended later), parts that read the frame
# (binding, local_variables, eval, caller, __method__, block_given?), parts that yield, pass a block or
# splat, encodings and frozen-ness of the result, the frames in a backtrace raised from a part, tracing,
# threads, recursion through #to_s, and - last, because `using' keeps every method defined after it out
# of light frames - refinements of #to_s in use before and after the method was defined.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/interpolation-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
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

def label(line) = line[/'(?:[^']*[#.])?([^']*)'/, 1]

# ---------------------------------------------------------------- 1. what each part is asked

def s_one(x) = "a#{x}"
def s_only(x) = "#{x}"
def s_two(x, y) = "<#{x}|#{y}>"
def s_nested(x) = "a#{"b#{x}c"}d"
def s_words(x) = %W[a#{x} b]
def s_heredoc(x)
  <<~EOS
    one #{x}
    two #{x.inspect}
  EOS
end

class Plain; def to_s = "plain"; end
class NonString; def to_s = 42; end
class NilString; def to_s = nil; end
class SubString < String; end
class ReturnsSub; def to_s = SubString.new("sub"); end
class ToSRaises; def to_s = raise(IndexError, "to_s raised"); end
class PrivToS; private def to_s = "private"; end
class MMToS; undef_method :to_s; def method_missing(n, *) = n == :to_s ? "mm" : super; def respond_to_missing?(n, p = false) = n == :to_s || super; end
class Redef; def to_s = "first"; end
class Pre; def to_s = "pre"; end
class Single; end
class Counted; attr_reader :n; def initialize = @n = 0; def to_s = (@n += 1).to_s; end

t('a String part') { s_one("s") }
t('a String part is copied') { s = +"s"; r = s_only(s); [r, r.equal?(s)] }
t('a String subclass part') { s_only(SubString.new("x")).class }
t('a Symbol part') { s_one(:sym) }
t('nil') { s_one(nil) }
t('true and false') { s_two(true, false) }
t('an Integer') { s_one(12) }
t('a big Integer') { s_one(2**70) }
t('a Float') { s_one(1.5) }
t('an Array') { s_one([1, "a", nil]) }
t('a Hash') { s_one({ a: 1 }) }
t('a Range') { s_one(1..2) }
t('a user to_s') { s_one(Plain.new) }
t('to_s answers an Integer') { s_one(NonString.new).sub(/0x\h+/, 'X') }
t('to_s answers nil') { s_one(NilString.new).sub(/0x\h+/, 'X') }
t('to_s answers a String subclass') { r = s_only(ReturnsSub.new); [r, r.class] }
t('to_s raises') { begin; s_one(ToSRaises.new); rescue IndexError => e; [e.message, label(e.backtrace[0]), label(e.backtrace[1])]; end }
t('a private to_s') { s_one(PrivToS.new) }
t('to_s by method_missing') { s_one(MMToS.new) }
t('to_s of a BasicObject') { begin; s_one(BasicObject.new); rescue NoMethodError => e; e.message; end }
t('to_s once per part') { c = Counted.new; [s_two(c, c), c.n] }
t('to_s redefined between calls') do |i|
  Redef.class_eval { def to_s = "second" } if i == N / 2
  s_one(Redef.new)
end
t('a module prepended later') do |i|
  Pre.prepend(Module.new { def to_s = "prepended" }) if i == N / 2
  s_one(Pre.new)
end
t('a singleton to_s') { o = Single.new; def o.to_s = "single"; s_one(o) }
t('nested') { s_nested(1) }
t('%W') { s_words(1) }
t('a heredoc') { s_heredoc(:h) }

# ---------------------------------------------------------------- 2. the result

def e_cat(a, b) = "#{a}#{b}"
def e_lit(x) = "é#{x}"
def e_bin_lit(x) = "\xff#{x}".b

t('UTF-8 parts') { r = e_cat("a", "é"); [r, r.encoding] }
t('binary parts') { r = e_cat("\xff".b, "\xfe".b); [r, r.encoding] }
t('a binary ASCII part and a UTF-8 part') { r = e_cat("a".b, "é"); [r, r.encoding] }
t('US-ASCII parts') { r = e_cat("a".encode("US-ASCII"), "b".encode("US-ASCII")); [r, r.encoding] }
t('a US-ASCII part and a UTF-8 part') { r = e_cat("a".encode("US-ASCII"), "é"); [r, r.encoding] }
t('a UTF-8 literal and an ASCII part') { r = e_lit("a"); [r, r.encoding] }
t('a UTF-8 literal and a binary part') { begin; e_lit("\xff".b); rescue Encoding::CompatibilityError => e; e.message; end }
t('incompatible parts') { begin; e_cat("é", "\xff".b); rescue Encoding::CompatibilityError => e; e.message; end }
t('a Symbol part of another encoding') { r = e_cat("é", :"ü"); [r, r.encoding] }
t('a binary literal') { r = e_bin_lit("a"); [r, r.encoding] }
t('the result is not frozen') { r = s_one(1); [r.frozen?, (r << "x")] }
t('a new String per call') { s_one(1).equal?(s_one(1)) }
eval(<<~RUBY, TOPLEVEL_BINDING, "frozen.rb", 1)
  # frozen_string_literal: true
  def fsl(x) = "a\#{x}"
  def fsl_plain = "plain"
RUBY
t('frozen_string_literal: interpolated') { r = fsl(1); [r, r.frozen?] }
t('frozen_string_literal: plain') { fsl_plain.frozen? }

# ---------------------------------------------------------------- 3. where it is written

def w_raise(x) = raise(ArgumentError, "bad #{x}")
def w_raise_if(x); raise ArgumentError, "bad #{x.inspect}" unless x.is_a?(Integer); x; end
def w_default(x, y = "d#{x}") = y
def w_while(n); s = +""; i = 0; while i < n; s << "#{i},"; i += 1; end; s; end
def w_case(x)
  case x
  when Integer then "int #{x}"
  when String then "str #{x}"
  else "other #{x.class}"
  end
end
def w_rest(*a) = "rest #{a} #{a.size}"
def w_ternary(x) = x ? "yes #{x}" : "no #{x}"
def w_and(x) = x && "and #{x}"
def w_ivar(x) = "#{@iv} #{x}"
def w_const(x) = "#{RUBY_ENGINE.class} #{x}"
def w_local(x); y = x * 2; "#{x} #{y}"; end
def w_twice(x) = "#{x}" + "#{x}"
def w_send(x) = "#{x}".send(:upcase)
def w_call(x) = "a#{x}".length

t('raise with an interpolated message') { begin; w_raise(1); rescue ArgumentError => e; [e.message, label(e.backtrace[0])]; end }
t('raise only when bad') { [w_raise_if(1), (begin; w_raise_if(:x); rescue ArgumentError => e; e.message; end)] }
t('a default argument') { [w_default(1), w_default(1, 2)] }
t('a while loop') { w_while(3) }
t('case/when') { [w_case(1), w_case("s"), w_case(nil)] }
t('a rest parameter') { [w_rest, w_rest(1, :a)] }
t('a ternary') { [w_ternary(1), w_ternary(nil)] }
t('an and') { [w_and(1), w_and(nil)] }
t('an instance variable') { w_ivar(1) }
t('a constant') { w_const(1) }
t('a local') { w_local(2) }
t('twice') { w_twice(:a) }
t('the result as a receiver') { [w_send(:a), w_call(12)] }

# ---------------------------------------------------------------- 4. parts that read the frame

def take(*a, &b) = b ? [a, b.call] : a
def yielder(x) = "#{yield x}"
def f_binding(x) = "#{binding.local_variable_get(:x)}"
def f_binding_locals(x) = "#{binding.local_variables}"
def f_local_variables(x) = "#{local_variables}"
def f_block_given(x) = "#{block_given?} #{x}"
def f_method(x) = "#{__method__} #{x}"
def f_caller(x) = "#{label(caller(0, 1)[0])} #{x}"
def f_eval(x) = "#{eval('x')}"
def f_binding_after(x); s = "#{x}"; [s, binding.local_variable_get(:x)]; end
def f_pass(x, &b) = "#{take(x, &b)}"
def f_splat(*a) = "#{take(*a)}"
def f_set(x) = "#{binding.local_variable_set(:x, 5)} #{x}"

t('a part that yields') { yielder(2) { |v| v * 3 } }
t('a part that yields with no block') { begin; yielder(2); rescue LocalJumpError => e; e.message; end }
t('binding') { f_binding(1) }
t('binding.local_variables') { f_binding_locals(1) }
t('local_variables') { f_local_variables(1) }
t('block_given?') { [f_block_given(1), f_block_given(2) { }] }
t('__method__') { f_method(1) }
t('caller') { f_caller(1) }
t('eval') { f_eval(1) }
t('binding after') { f_binding_after(1) }
t('a part that passes a block on') { [f_pass(1) { :b }, f_pass(2)] }
t('a part that splats') { [f_splat, f_splat(1, 2)] }
t('local_variable_set in a part') { f_set(1) }

# ---------------------------------------------------------------- 5. errors and backtraces

def b_inner(x) = "inner #{x}"
def b_outer(x) = "outer #{b_inner(x)}"
def b_raise_inner(x) = raise(TypeError, "deep #{x}")
def b_raise_outer(x) = "outer #{b_raise_inner(x)}"
def b_heredoc(x)
  s = <<~EOS
    first
    second #{x}
  EOS
  s
end
def b_frames(x) = "#{caller(0, 3).map { |l| label(l) }}"

t('nested calls') { b_outer(1) }
t('a raise in a part') { begin; b_raise_outer(1); rescue TypeError => e; [e.message, e.backtrace[0, 2].map { |l| label(l) }]; end }
t('to_s raises in a heredoc') { begin; b_heredoc(ToSRaises.new); rescue IndexError => e; e.backtrace[0, 2].map { |l| label(l) }; end }
def b_line(x)
  y = 1
  "a#{x}#{y}"
end
def b_line_scope(x)
  y = binding.local_variable_get(:x)
  "a#{y}"
end
t('to_s raises: the frame names the line') do
  [b_line(ToSRaises.new), b_line_scope(ToSRaises.new)] rescue $!.backtrace_locations[1].lineno - __LINE__
end
# (not the line of a heredoc's part that is a local variable: IronRuby names the heredoc's statement, CRuby the
# part's own line)
t('a frame list in a part') { b_frames(1) }

class Deep; def to_s = deep_s(self); end
def deep_s(o) = "<#{o}>"
t('recursion through to_s') { deep_s(Deep.new) }
def r_count(n) = n == 0 ? "0" : "#{r_count(n - 1)}".size.to_s
t('recursion') { r_count(50) }

# ---------------------------------------------------------------- 6. tracing and threads

def traced_endless(x) = "t#{x}"
def traced_endless_parens(x) = ("t#{x}")
t('TracePoint :line, an endless def') do
  seen = []
  tp = TracePoint.new(:line) { |e| seen << [e.method_id, e.lineno - __LINE__] if e.method_id.to_s.start_with?("traced_endless") }
  tp.enable { traced_endless(1); traced_endless_parens(2) }
  seen
end
def traced(x)
  "t#{x.fdiv(1)}"
end
t('TracePoint :call sees it') do
  seen = []
  tp = TracePoint.new(:call) { |e| seen << e.method_id if e.method_id == :traced }
  tp.enable { traced(-1) }
  seen
end
t('TracePoint :c_call sees a call in a part') do
  seen = []
  tp = TracePoint.new(:c_call) { |e| seen << e.method_id if e.method_id == :fdiv }
  tp.enable { traced(-2) }
  seen
end
t('TracePoint :line') do
  seen = []
  tp = TracePoint.new(:line) { |e| seen << e.lineno - __LINE__ if e.method_id == :traced }
  tp.enable { traced(-3) }
  seen
end
t('TracePoint :call, binding') do
  seen = []
  tp = TracePoint.new(:call) { |e| seen << e.binding.local_variable_get(:x) if e.method_id == :traced }
  tp.enable { traced(-4) }
  seen
end
t('after the TracePoint') { traced(-5) }
t('threads') do |i|
  i % 100 == 0 ? Array.new(4) { |k| Thread.new { Array.new(50) { |j| s_two(k, j) }.last } }.map(&:value) : true
end

# ---------------------------------------------------------------- 7. refinements (last: `using' keeps every method
# defined after it in this file out of light frames)

class Shown; def to_s = "shown"; end
class ShownSub < Shown; end
def r_before(x) = "#{x}"
def r_definer; def r_nested(x) = "n#{x}"; end
r_definer

module RefToS
  refine(Integer) { def to_s = "refined int" }
  refine(Symbol) { def to_s = "refined sym" }
  refine(NilClass) { def to_s = "refined nil" }
  refine(Shown) { def to_s = "refined shown" }
end
module RefUnused; refine(Float) { def to_s = "refined float" }; end
def r_float(x) = "#{x}"

t('a refinement not in use') { [r_before(1), r_before(:s), r_before(nil), r_before(Shown.new), r_float(1.5)] }
t('a refinement not in use, subclass') { r_before(ShownSub.new) }
r_definer
using RefToS
r_definer
def r_after(x) = "#{x}"
t('defined before using') { [r_before(1), r_before(:s), r_before(nil), r_before(Shown.new)] }
t('defined before, subclass') { r_before(ShownSub.new) }
t('a nested def run again after using') { r_nested(1) }
# (not a Symbol: CRuby interpolates one without asking a refined #to_s, IronRuby asks it)
t('defined after using') { [r_after(1), r_after(nil), r_after(Shown.new)] }
t('defined after, subclass') { r_after(ShownSub.new) }
t('a refinement that exists but is not used') { r_after(1.5) }
t('at the call site') { [r_before(2), "#{2}"] }

require 'tmpdir'
Dir.mktmpdir do |dir|
  path = File.join(dir, "loaded.rb")
  File.write(path, "def ld(x) = \"\#{x}\"\n")
  load path
end
t('a file loaded after using') { [ld(1), ld(nil), ld(Shown.new)] }

module InModule
  using(Module.new { refine(Integer) { def to_s = "module-refined" } })
  def self.m(x) = "#{x}"
end
t('using in a module body') { [InModule.m(1), r_before(1)] }

eval(<<~RUBY, TOPLEVEL_BINDING, "other.rb", 1)
  module RefEval; refine(Integer) { def to_s = "eval-refined" }; end
  def ev_before(x) = "\#{x}"
  using RefEval
  def ev_after(x) = "\#{x}"
RUBY
t('refine and using in an eval') { [ev_before(1), ev_after(1), r_before(1), r_after(1)] }

class KlassEval; end
KlassEval.class_eval(<<~RUBY)
  def ce(x) = "\#{x}"
RUBY
t('a class_eval string after using') { KlassEval.new.ce(1) }

KlassEval.define_method(:dm) { |x| "#{x}" }
t('define_method after using') { KlassEval.new.dm(1) }

def r_late(x) = "#{x}"
begin
  [1].each { using(Module.new { refine(Integer) { def to_s = "late" } }) }
rescue => e
  puts "using in a block: #{e.class}: #{e.message}"
end
t('using in a block') { [r_late(1), r_before(1)] }
begin
  send(:using, Module.new { refine(Integer) { def to_s = "sent" } })
rescue => e
  puts "send(:using): #{e.class}: #{e.message}"
end
def r_sent(x) = "#{x}"
t('send(:using)') { [r_sent(1), r_before(1), r_after(1)] }
