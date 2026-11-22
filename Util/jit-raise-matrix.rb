# Differential matrix for the method JIT (-X:JIT, Src/Ruby/Runtime/Jit/): a `raise' in a typed body is a
# deopt (JitCompiler.EmitRaise). The typed body throws its deopt where the source raises, the generic body
# runs again from the top and makes the call itself, so everything about the exception - class, message,
# backtrace, cause, $!, TracePoint's raise event, the callers' rescue and ensure - comes from the generic
# body. Run under CRuby and under ./ir.sh - with the JIT on and off - and diff:
#
#   ruby Util/jit-raise-matrix.rb > /tmp/mri.txt; echo $?
#   ./ir.sh Util/jit-raise-matrix.rb > /tmp/jit.txt; echo $?
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-raise-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# All must exit 0, end with the row `end', and be identical.
#
# Each row drives a method of its own 200 times (the JIT's threshold is 40 calls) and has it raise on the
# first call, on call 60 (the first hot one) and on call 130, unless the row says otherwise. Methods named
# r_* are expected to be SPECIALIZED; no_* to be declined (the answers must be right either way):
#
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-raise-matrix.rb 2>&1 >/dev/null | grep -a 'jit\] \(specialized\|declined\|screen\)'

$stdout.sync = true
FILE = File.basename(__FILE__)

# A backtrace line without its directory, which differs between the engines' checkouts.
def frame(line)
  line.sub(/\A.*?#{Regexp.escape(FILE)}:/, '')
end

def describe(e)
  [e.class, e.message, (e.backtrace || []).reject { |l| l.include?("'Kernel#raise'") }.first(3).map { |l| frame(l) },
   e.backtrace_locations && e.backtrace_locations.first && e.backtrace_locations.first.lineno,
   e.cause && [e.cause.class, e.cause.message], e.equal?($!)]
end

RAISE_AT = [0, 60, 130]

# Calls the block 200 times with (k, whether to raise) and prints what was rescued: each exception in full the
# first time its description is seen, then the calls it was raised at.
def drive(label, at = RAISE_AT, n = 200)
  seen = {}
  order = []
  values = []
  n.times do |k|
    begin
      v = yield(k, at.include?(k))
      values << v if at.include?(k)
    rescue Exception => e
      d = describe(e)
      (seen[d] ||= (order << d; [])) << k
      values << :rescued
    end
  end
  puts "#{label}: after=#{$!.inspect} values=#{values.inspect}"
  order.each { |d| puts "  #{d.inspect} at #{seen[d].inspect}" }
end

# full_message without the directories in it.
# (The messages below are built with `+', not interpolated: IronRuby, with the JIT or without, gives a raise whose
# arguments interpolate a String the line of the statement before it in the backtrace, which is not this matrix's.)
def full(e)
  e.full_message(highlight: false).lines.map { |l| frame(l).sub(/\A\s*from /, 'from ') }.join.inspect
end

# ---------------------------------------------------------------- 1. what is raised

class A1
  def initialize; @t = { 1 => 10 }; end
  def r_str(i)
    t = @t[i]
    raise 'missing' unless t
    t
  end
  def r_cls(i)
    t = @t[i]
    raise ArgumentError unless t
    t
  end
  def r_cls_msg(i)
    t = @t[i]
    raise ArgumentError, 'bad key' unless t
    t
  end
  def r_inst(i)
    t = @t[i]
    raise KeyError.new('no key') unless t
    t
  end
  def r_cause(i)
    t = @t[i]
    raise TypeError, 'with cause', cause: IndexError.new('the cause') unless t
    t
  end
  def r_bt(i)
    t = @t[i]
    raise IOError, 'given trace', ['here:1', 'there:2'] unless t
    t
  end
  def r_obj(i)
    t = @t[i]
    raise EXC_OBJ, 'via exception' unless t
    t
  end
  # (describe leaves out the 'Kernel#raise' frame CRuby's backtrace begins with for this one: IronRuby's has never
  # had it, with the JIT or without)
  def r_nonexc(i)
    t = @t[i]
    raise 42 unless t
    t
  end
  def r_bare(i)
    t = @t[i]
    raise unless t
    t
  end
  def r_fail(i)
    t = @t[i]
    fail 'failed' unless t
    t
  end
  def r_fail_cls(i)
    t = @t[i]
    fail NameError, 'failed name' unless t
    t
  end
end

class ExcMaker
  def exception(msg = nil) = RangeError.new("made: #{msg}")
end
EXC_OBJ = ExcMaker.new

a1 = A1.new
%i[r_str r_cls r_cls_msg r_inst r_cause r_bt r_obj r_nonexc r_bare r_fail r_fail_cls].each do |m|
  drive("1. #{m}") { |k, miss| a1.__send__(m, miss ? 2 : 1) }
end
# bare raise with $! set: called from a rescue clause, it re-raises what is being handled
drive('1. r_bare inside a rescue') do |k, miss|
  begin
    raise ZeroDivisionError, 'outer'
  rescue ZeroDivisionError
    a1.r_bare(miss ? 2 : 1)
  end
end
# full_message of one, raised hot
begin
  a1.r_cls_msg(2)
rescue => e
  puts "1. full_message: #{full(e)}"
end

# ---------------------------------------------------------------- 2. arguments with effects, evaluated once per raise

class A2
  attr_reader :built
  def initialize; @t = { 1 => 10 }; @built = 0; end
  def message_for(i) = (@built += 1; "built #{i} #{@built}")
  def r_effect_arg(i)
    t = @t[i]
    raise ArgumentError, message_for(i) unless t
    t
  end
end
a2 = A2.new
drive('2. r_effect_arg') { |k, miss| a2.r_effect_arg(miss ? 2 : 1) }
puts "2. message built #{a2.built} times"

# ---------------------------------------------------------------- 3. where the raise stands

class A3
  def r_if(i)
    raise 'if' if i < 0
    i + 1
  end
  def r_unless(i)
    raise 'unless' unless i >= 0
    i + 2
  end
  def r_case(i)
    case i
    when 0 then 100
    when -1 then raise RangeError, 'case'
    else i * 2
    end
  end
  def r_or(i)
    (i >= 0 || raise('or')) && i + 3
  end
  def r_and(i)
    i < 0 and raise('and')
    i + 4
  end
  def r_last(i)
    x = i + 5
    raise 'last ' + x.to_s
  end
  def r_arms(i)
    if i < 0
      raise 'then arm'
    else
      raise 'else arm' if i > 1000
      i + 6
    end
  end
end
a3 = A3.new
%i[r_if r_unless r_case r_or r_and r_arms].each do |m|
  drive("3. #{m}") { |k, miss| a3.__send__(m, miss ? -1 : k) }
end
drive('3. r_arms else', [5, 70]) { |k, miss| a3.r_arms(miss ? 2000 : k) }
drive('3. r_last', (0...200).to_a) { |k, _| a3.r_last(k) }

# ---------------------------------------------------------------- 4. after held-back writes, and after an effect that is not

class A4
  attr_reader :a, :b
  attr_accessor :other
  def initialize; @a = 0; @b = 0; @other = Box.new; end
  def r_held(i)
    @a = @a + 1
    @b = i
    raise ArgumentError, 'held ' + @a.to_s + ' ' + @b.to_s if i < 0
    @a + @b
  end
  # an attribute written on another object is observable: the raise after it is declined
  def no_effect_first(i)
    @other.v = i
    raise 'after an effect' if i < 0
    i
  end
end
class Box; attr_accessor :v; end
a4 = A4.new
drive('4. r_held') { |k, miss| [a4.r_held(miss ? -k - 1 : k), a4.a, a4.b] }
puts "4. variables after: #{[a4.a, a4.b].inspect}"
drive('4. no_effect_first') { |k, miss| a4.no_effect_first(miss ? -k - 1 : k) }
puts "4. other.v after: #{a4.other.v.inspect}"

# ---------------------------------------------------------------- 5. the caller's ensure, rescue, retry; a block; catch/throw

class A5
  def initialize; @t = { 1 => 10 }; end
  def r_find(i)
    t = @t[i]
    raise IndexError, 'no ' + i.to_s unless t
    t
  end
  def no_yields(i)
    yield i
  end
end
a5 = A5.new
ensured = 0
drive('5. caller ensure') do |k, miss|
  begin
    a5.r_find(miss ? 2 : 1)
  ensure
    ensured += 1
  end
end
puts "5. ensured #{ensured}"
drive('5. caller rescue') do |k, miss|
  begin
    a5.r_find(miss ? 2 : 1)
  rescue IndexError => e
    [:caught, e.message, $!.equal?(e)]
  end
end
retries = 0
drive('5. caller retry') do |k, miss|
  tries = 0
  begin
    tries += 1
    a5.r_find(miss && tries < 3 ? 2 : 1)
  rescue IndexError
    retries += 1
    retry
  end
end
puts "5. retries #{retries}"
drive('5. raise in a yielded block') do |k, miss|
  a5.no_yields(k) { |x| miss ? raise(EncodingError, 'block ' + x.to_s) : x }
end
drive('5. catch/throw around') do |k, miss|
  catch(:out) do
    v = a5.r_find(1)
    throw :out, [:thrown, k] if miss
    v
  end
end
drive('5. catch around a raise') do |k, miss|
  catch(:out) { a5.r_find(miss ? 2 : 1) }
end

# ---------------------------------------------------------------- 6. a `raise' that is not Kernel#raise, each returning normally

class B6Public
  def raise(*a) = (@calls = (@calls || 0) + 1; nil)
  def r_own(i)
    raise 'own public' if i < 0
    i
  end
end
class B6Private
  def r_own(i)
    raise 'own private' if i < 0
    i
  end
  private def raise(*a) = :private_raise
end
module RaiseMod
  def raise(*a) = :module_raise
end
class B6Module
  include RaiseMod
  def r_own(i)
    raise 'from module' if i < 0
    i
  end
end
module RaiseRef
  refine Object do
    def raise(*a) = :refined_raise
  end
end
module RefinedHere
  using RaiseRef
  class B6Refined
    def r_own(i)
      raise 'refined' if i < 0
      i
    end
  end
end
class B6Single
  def r_own(i)
    raise 'single' if i < 0
    i
  end
end
class B6Missing
  undef_method :raise
  def method_missing(name, *a) = (name == :raise ? :missing_raise : super)
  def respond_to_missing?(*) = true
  def r_own(i)
    raise 'missing' if i < 0
    i
  end
end
[B6Public, B6Private, B6Module, RefinedHere::B6Refined, B6Missing].each do |c|
  o = c.new
  drive("6. #{c.name.split('::').last}") { |k, miss| [o.r_own(miss ? -1 : k)] }
  # B6Public#raise counts its calls: one per scheduled probe, none skipped or repeated
  puts "  B6Public#raise called #{o.instance_variable_get(:@calls).inspect} times" if c == B6Public
end
plain = B6Single.new
single = B6Single.new
def single.raise(*a) = :singleton_raise
drive('6. singleton receiver', [0, 60, 61, 130]) { |k, miss| [(miss ? single : plain).r_own(miss ? -1 : k)] }
drive('6. plain receiver after it') { |k, miss| [plain.r_own(miss ? -1 : k)] }

# Kernel#raise and #fail redefined part way through, and put back
class B6Kernel
  def r_k(i)
    raise 'kernel' if i < 0
    i
  end
  def r_f(i)
    fail 'kernel fail' if i < 0
    i
  end
end
bk = B6Kernel.new
module Kernel
  alias_method :__matrix_raise, :raise
  alias_method :__matrix_fail, :fail
end
[[:r_k, :raise], [:r_f, :fail]].each do |m, name|
  out = []
  200.times do |k|
    if k == 100
      Kernel.module_eval { define_method(name) { |*a| :redefined }; private name }
    elsif k == 140
      Kernel.module_eval { alias_method name, :"__matrix_#{name}"; private name }
    end
    begin
      v = bk.__send__(m, k % 20 == 0 ? -1 : k)
      out << [k, v] if v == :redefined || v == nil
    rescue => e
      out << [k, e.class, e.message]
    end
  end
  puts "6. Kernel##{name} redefined at 100, back at 140: #{out.inspect}"
end

# ---------------------------------------------------------------- 7. TracePoint's raise event, a raise in a thread

class C7
  def initialize; @t = { 1 => 10 }; end
  def r_tp(i)
    t = @t[i]
    raise ArgumentError, 'tp ' + i.to_s unless t
    t
  end
end
c7 = C7.new
events = []
tp = TracePoint.new(:raise) { |x| events << [x.lineno, File.basename(x.path), x.raised_exception.class, x.raised_exception.message] }
drive('7. TracePoint part way', [10, 70, 150]) do |k, miss|
  tp.enable if k == 100
  c7.r_tp(miss ? 2 : 1)
end
tp.disable
puts "7. raise events: #{events.inspect}"

Thread.report_on_exception = true
th_out = []
th = Thread.new do
  200.times { |k| begin; c7.r_tp(k % 50 == 49 ? 2 : 1); rescue => e; th_out << [k, e.message]; end }
  Thread.current.report_on_exception = false
  c7.r_tp(3)
end
r = begin; th.join; :joined; rescue => e; [e.class, e.message]; end
puts "7. thread: #{th_out.inspect} join: #{r.inspect}"

# ---------------------------------------------------------------- 8. a caller that binds the method

class D8
  def initialize; @t = { 1 => 10, 3 => 30 }; end
  def r_lookup(i)
    t = @t[i]
    raise 'lookup ' + i.to_s + ' failed' unless t
    t
  end
  def r_q(i) = r_lookup(i)
  def r_q2(i) = r_lookup(i) + 1
end
d8 = D8.new
drive('8. r_q') { |k, miss| d8.r_q(miss ? 2 : (k.odd? ? 1 : 3)) }
d8b = D8.new
drive('8. r_q2, first raise hot', [90, 91, 180]) { |k, miss| d8b.r_q2(miss ? 2 : 1) }

# ---------------------------------------------------------------- 9. how often it raises: the answer, not the time

class E9
  def initialize; @t = { 1 => 10 }; end
  def r_freq(i)
    t = @t[i]
    raise 'miss' unless t
    t
  end
end
e9 = E9.new
[[1, 'every call'], [2, 'every second call'], [1000, 'one call in a thousand']].each do |every, what|
  caught = 0
  sum = 0
  5000.times { |k| begin; sum += e9.r_freq(k % every == 0 ? 2 : 1); rescue RuntimeError; caught += 1; end }
  puts "9. #{what}: caught #{caught} sum #{sum}"
end
# Integer("x") rescue nil-style control flow
class E9b
  def r_int(s)
    raise ArgumentError, 'invalid value for Integer(): ' + s.inspect unless s == '1'
    1
  end
end
e9b = E9b.new
n = 0
3000.times { |k| n += (e9b.r_int(k.even? ? '1' : 'x') rescue 0) }
puts "9. rescue modifier: #{n}"

# ---------------------------------------------------------------- 10. `Kernel.raise' and `::Kernel.raise', as the prelude writes them
# (The screen refuses a call of raise with a receiver, so on the current build the r_k_* methods are never wrapped:
# the rows pin the answers for the day `Kernel.raise' becomes the same deopt as a bare `raise'.)

class K10
  attr_reader :a, :b
  def initialize; @t = { 1 => 10 }; @a = 0; @b = 0; end
  def r_k_cls_msg(i)
    t = @t[i]
    Kernel.raise(TypeError, 'k cls msg') unless t
    t
  end
  def r_k_top(i)
    t = @t[i]
    ::Kernel.raise(TypeError, 'top') unless t
    t
  end
  def r_k_fail(i)
    t = @t[i]
    Kernel.fail('k failed') unless t
    t
  end
  def r_k_top_fail(i)
    t = @t[i]
    ::Kernel.fail ArgumentError, 'top failed' unless t
    t
  end
  def r_k_bare(i)
    t = @t[i]
    ::Kernel.raise unless t
    t
  end
  def r_k_cls(i)
    t = @t[i]
    Kernel.raise ArgumentError unless t
    t
  end
  def r_k_inst(i)
    t = @t[i]
    ::Kernel.raise(KeyError.new('no key')) unless t
    t
  end
  def r_k_cause(i)
    t = @t[i]
    ::Kernel.raise(TypeError, 'with cause', cause: IndexError.new('the cause')) unless t
    t
  end
  def r_k_if(i)
    ::Kernel.raise(RangeError, 'if') if i < 0
    i + 1
  end
  def r_k_ternary(i)
    i < 0 ? ::Kernel.raise(RangeError, 'ternary') : i + 2
  end
  def r_k_or(i)
    i >= 0 or ::Kernel.raise(RangeError, 'or')
    i + 3
  end
  def r_k_last(i)
    x = i + 5
    ::Kernel.raise(RangeError, 'last ' + x.to_s)
  end
  def r_k_held(i)
    @a = @a + 1
    @b = i
    ::Kernel.raise(ArgumentError, 'held ' + @a.to_s + ' ' + @b.to_s) if i < 0
    @a + @b
  end
end
k10 = K10.new
%i[r_k_cls_msg r_k_top r_k_fail r_k_top_fail r_k_bare r_k_cls r_k_inst r_k_cause].each do |m|
  drive("10. #{m}") { |k, miss| k10.__send__(m, miss ? 2 : 1) }
end
drive('10. r_k_bare inside a rescue') do |k, miss|
  begin
    raise ZeroDivisionError, 'outer'
  rescue ZeroDivisionError
    k10.r_k_bare(miss ? 2 : 1)
  end
end
%i[r_k_if r_k_ternary r_k_or].each do |m|
  drive("10. #{m}") { |k, miss| k10.__send__(m, miss ? -1 : k) }
end
drive('10. r_k_last', (0...200).to_a) { |k, _| k10.r_k_last(k) }
drive('10. r_k_held') { |k, miss| [k10.r_k_held(miss ? -k - 1 : k), k10.a, k10.b] }
puts "10. variables after: #{[k10.a, k10.b].inspect}"

# a class with a `raise' of its own: `::Kernel.raise' is not it
class K10Own
  def raise(*a) = :own_raise
  def r_k_own(i)
    ::Kernel.raise(TypeError, 'not own') if i < 0
    Kernel.raise(TypeError, 'not own either') if i > 1000
    i
  end
end
ko = K10Own.new
drive('10. r_k_own') { |k, miss| [ko.r_k_own(miss ? -1 : k)] }
drive('10. r_k_own unqualified', [3, 80]) { |k, miss| [ko.r_k_own(miss ? 2000 : k)] }

# a lexically scoped constant Kernel that is not the module
class K10Lex
  Kernel = Object.new
  def no_lex(i)
    Kernel.raise('lexical') if i < 0
    i
  end
  def r_lex_top(i)
    ::Kernel.raise(TypeError, 'top past the lexical one') if i < 0
    i
  end
end
kl = K10Lex.new
drive('10. no_lex') { |k, miss| [kl.no_lex(miss ? -1 : k)] }
drive('10. r_lex_top') { |k, miss| [kl.r_lex_top(miss ? -1 : k)] }

# Kernel.raise (the module's singleton method) redefined after warm-up, and put back
class K10Sing
  def r_k_sing(i)
    ::Kernel.raise(TypeError, 'sing') if i < 0
    [:after, i]
  end
  def r_k_sing_fail(i)
    Kernel.fail(TypeError, 'sing fail') if i < 0
    [:after, i]
  end
end
ks = K10Sing.new
Kernel.singleton_class.alias_method :__matrix_sraise, :raise
Kernel.singleton_class.alias_method :__matrix_sfail, :fail
[[:r_k_sing, :raise], [:r_k_sing_fail, :fail]].each do |m, name|
  out = []
  200.times do |k|
    if k == 100
      Kernel.singleton_class.define_method(name) { |*a| 42 }
    elsif k == 130
      Kernel.singleton_class.define_method(name) { |*a| Integer('not a number') }
    elsif k == 160
      Kernel.singleton_class.alias_method name, :"__matrix_s#{name}"
    end
    begin
      v = ks.__send__(m, k % 10 == 0 ? -1 : k)
      out << [k, v] if k % 10 == 0
    rescue => e
      out << [k, e.class, e.message]
    end
  end
  puts "10. Kernel.#{name} redefined at 100, raising another class at 130, back at 160: #{out.inspect}"
end

# Kernel#raise (the instance method) redefined part way through: Kernel.raise is the singleton's, not it
class K10Kernel
  def r_kk(i)
    ::Kernel.raise('kernel') if i < 0
    i
  end
  def r_kf(i)
    Kernel.fail('kernel fail') if i < 0
    i
  end
end
kk = K10Kernel.new
[[:r_kk, :raise], [:r_kf, :fail]].each do |m, name|
  out = []
  200.times do |k|
    if k == 100
      Kernel.module_eval { define_method(name) { |*a| :redefined }; private name }
    elsif k == 140
      Kernel.module_eval { alias_method name, :"__matrix_#{name}"; private name }
    end
    begin
      v = kk.__send__(m, k % 20 == 0 ? -1 : k)
      out << [k, v] if v == :redefined || v == nil
    rescue => e
      out << [k, e.class, e.message]
    end
  end
  puts "10. Kernel##{name} redefined at 100, back at 140, Kernel.#{name} called: #{out.inspect}"
end

puts 'end'
