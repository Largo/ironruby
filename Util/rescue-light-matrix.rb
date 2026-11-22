# Differential matrix for methods whose body has `rescue', `else' or `ensure' and that run without a scope (a light
# frame, MethodDefinition.IsScopeFree). Run it under CRuby and under ./ir.sh with the shape on and off, and diff:
#
#   ruby Util/rescue-light-matrix.rb > mri.txt
#   ./ir.sh Util/rescue-light-matrix.rb > on.txt
#   IR_LIGHT_RESCUE=0 ./ir.sh Util/rescue-light-matrix.rb > off.txt
#   diff mri.txt on.txt && diff mri.txt off.txt
#
# and with IR_LIGHT_FRAMES=0 and -X:NoJIT -X:NoOSR.
#
# A body with exception handling saves and restores $! (the runtime's, not the scope's), and its handler matches the
# exception, captures its backtrace and runs the clauses; a light frame makes its scope on demand there, so the path that
# raises nothing makes none. The probes cover the clause forms (no class, one, several, a splat of a constant, a class
# in a parameter, `=> e', `=> @ivar'), $! inside and after the handler and in the caller (also a caller that is itself
# in a rescue), `else', `ensure' left normally, by a raise, a return, a break or next of a block, a throw, and one whose
# `return' swallows the exception, `retry', a raise several frames down, backtraces and caller inside the handler, what
# reads the frame there (binding, local_variables, eval, block_given?), re-raise and Exception#cause, exceptions that
# are not StandardErrors, Thread#raise, Kernel#loop and StopIteration, a redefinition half way, a million deep, threads,
# TracePoint :raise and :rescue, and a refinement.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/rescue-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.
#
# A difference of the unchanged build from CRuby 4.0 the probes are written around: TracePoint's :rescue event reports
# the line of the rescue clause's body in CRuby, and the line of the raise (or the line after it) in IronRuby.

N = 3000

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

def guard
  yield
rescue Exception => e
  "#{e.class}: #{e.message[0, 90]}"
end

def short(line)
  line[/\w[\w-]*\.rb:\d+:in .*/]
end

# ---------------------------------------------------------------- 1. clause forms

class E1 < StandardError; end
class E2 < StandardError; end
ERRS = [E1, E2]

class R1
  def bare(x)
    begin
      raise E1, "bare" if x == 1
      x + 1
    rescue
      :rescued
    end
  end

  def one(x)
    x.fetch(0)
  rescue IndexError
    :index
  end

  def several(x)
    raise x if x
    :none
  rescue ArgumentError, E1, TypeError
    :caught
  end

  def splat(x)
    raise x, "s" if x
    :none
  rescue *ERRS
    :splat
  end

  def local_class(x, k)
    raise x, "l" if x
    :none
  rescue k
    :local
  end

  def named(x)
    raise E2, "named #{x}" if x > 0
    x
  rescue E2 => e
    [e.class, e.message]
  end

  def to_ivar(x)
    raise E1, "iv" if x
    @err = nil
    :none
  rescue => @err
    @err.message
  end
  attr_reader :err

  def two_clauses(x)
    raise x
  rescue E1
    :first
  rescue E2, ArgumentError
    :second
  rescue
    :third
  end

  def no_match(x)
    raise E2, "through" if x
    :none
  rescue E1
    :wrong
  end

  def value_of_body(x)
    x * 2
  rescue
    :never
  end

  def not_a_class(x)
    raise E1 if x
    :none
  rescue 42
    :never
  end
end
o1 = R1.new

t('bare rescue') { |i| [i % 2, o1.bare(i % 2)] }
t('one class, raised by a callee') { |i| [i % 2, o1.one(i % 2 == 0 ? [] : [7])] }
t('several classes') { |i| x = [nil, E1, TypeError][i % 3]; [x, o1.several(x)] }
t('a splat of a constant') { |i| x = [nil, E1, E2][i % 3]; [x, o1.splat(x)] }
t('a class in a parameter') { |i| x = [nil, E1][i % 2]; [x, o1.local_class(x, E1)] }
t('a class in a parameter, no match') { |i| guard { o1.local_class(E2, E1) } }
t('=> e') { |i| [i % 2, o1.named(i % 2)] }
t('=> @ivar') { |i| x = i % 2 == 1; [x, o1.to_ivar(x), o1.err&.message] }
t('three clauses') { |i| x = [E1, E2, RuntimeError, ArgumentError][i % 4]; [x, o1.two_clauses(x)] }
t('no clause matches') { |i| [i % 2, guard { o1.no_match(i % 2 == 1) }] }
t('value of the body') { |i| o1.value_of_body(i % 3) }
t('rescue 42') { |i| [i % 2, guard { o1.not_a_class(i % 2 == 1) }] }

# ---------------------------------------------------------------- 2. $!

class R2
  def read_in_handler(x)
    raise E1, "in #{x}" if x
    :none
  rescue E1
    $!.message
  end

  def read_after(x)
    begin
      raise E1, "after" if x
    rescue E1
      :ok
    end
    $!
  end

  def swallow(x)
    raise E2, "swallowed" if x
    :none
  rescue E2
    :swallowed
  end

  def ensure_reads(x)
    raise E1, "ens" if x
    :none
  ensure
    @seen = $!&.message
  end
  attr_reader :seen
end
o2 = R2.new

t('$! inside the handler') { |i| o2.read_in_handler(i % 2 == 1) }
t('$! after the handler') { |i| o2.read_after(i % 2 == 1) }
t('$! in the caller after the method') { |i| r = o2.swallow(i % 2 == 1); [r, $!] }
t('$! in a caller that is in a rescue') do |i|
  begin
    raise E1, "outer"
  rescue E1
    r = o2.swallow(i % 2 == 1)
    [r, $!.message]
  end
end
t('$! after a caller rescue ends') do |i|
  begin
    raise E1, "outer"
  rescue E1
    o2.swallow(i % 2 == 1)
  end
  $!
end
t('$! inside ensure') { |i| [guard { o2.ensure_reads(i % 2 == 1) }, o2.seen] }

# ---------------------------------------------------------------- 3. else

class R3
  def with_else(x)
    raise E1 if x == 1
    @log = [:body]
  rescue E1
    @log = [:rescue]
  else
    @log << :else
    :else_value
  end
  attr_reader :log

  def else_raises(x)
    x + 1
  rescue TypeError
    :never
  else
    raise E2, "from else"
  end

  def else_ensure(x)
    raise E1 if x
    @log = [:body]
  rescue E1
    @log = [:rescue]
    :rescued
  else
    @log << :else
    :else
  ensure
    @log << :ensure
    :ignored
  end
end
o3 = R3.new

t('else when nothing is raised') { |i| x = i % 2; [x, o3.with_else(x), o3.log] }
t('an exception from else is not rescued') { |i| guard { o3.else_raises(i % 2) } }
t('else and ensure') { |i| x = i % 2 == 1; [x, o3.else_ensure(x), o3.log] }

# ---------------------------------------------------------------- 4. ensure

class R4
  def initialize
    @log = []
  end

  def take
    r = @log
    @log = []
    r
  end

  def normal(x)
    x + 1
  ensure
    @log << :ensure
  end

  def on_raise(x)
    raise E1, "r" if x
    :none
  ensure
    @log << :ensure
  end

  def on_return(x)
    begin
      return :early if x
      @log << :body
    ensure
      @log << :ensure
    end
    :late
  end

  def yields
    yield
    :after_yield
  ensure
    @log << :ensure
  end

  def throws(x)
    throw :tag, x
  ensure
    @log << :ensure
  end

  def swallow_by_return(x)
    raise E1, "gone" if x
    :none
  ensure
    return :swallowed
  end

  def ensure_value(x)
    x
  ensure
    :not_the_value
  end

  def nested(x)
    begin
      begin
        raise E1, "inner" if x
        :none
      ensure
        @log << :inner
      end
    rescue E1
      @log << :rescue
      :rescued
    ensure
      @log << :outer
    end
  end

  def while_break(n)
    i = 0
    while true
      begin
        break if i == n
        i += 1
      ensure
        @log << i
      end
    end
    i
  end
end
o4 = R4.new

class R4Return
  def run(o)
    o.yields { return :returned }
    :no
  end
end

t('ensure on a normal exit') { |i| [o4.normal(i % 2), o4.take] }
t('ensure on raise') { |i| [guard { o4.on_raise(i % 2 == 1) }, o4.take] }
t('ensure on return') { |i| [o4.on_return(i % 2 == 1), o4.take] }
t('ensure on break out of a block') { |i| [[1, 2].each { o4.yields { break :broke } }, o4.take] }
t('ensure on next in a block') { |i| [o4.yields { next :nexted }, o4.take] }
t('ensure on a block\'s return') { |i| [R4Return.new.run(o4), o4.take] }
t('ensure on throw') { |i| [catch(:tag) { o4.throws(i % 2) }, o4.take] }
t('ensure whose return swallows the exception') { |i| [o4.swallow_by_return(i % 2 == 1), $!] }
t('the ensure value is not the value') { |i| o4.ensure_value(i % 2) }
t('nested ensure and rescue') { |i| [o4.nested(i % 2 == 1), o4.take] }
t('ensure in a loop with break') { |i| [o4.while_break(i % 3), o4.take] }

# ---------------------------------------------------------------- 5. retry, depth, backtraces

class R5
  def retries(n)
    k = 0
    begin
      k += 1
      raise E1, "again" if k < n
      k
    rescue E1
      retry
    end
  end

  def level3(x) = x ? raise(E2, "deep") : :fine
  def level2(x) = level3(x)
  def level1(x) = level2(x)
  def from_deep(x)
    level1(x)
  rescue E2 => e
    [e.message, e.backtrace[0, 4].map { |l| short(l) }]
  end

  def bt(x)
    raise E1, "bt" if x
    :none
  rescue E1 => e
    [e.backtrace[0, 2].map { |l| short(l) }, e.backtrace_locations[0, 2].map { |l| [l.lineno, l.label, l.base_label] }]
  end

  def callers(x)
    raise E1 if x
    :none
  rescue E1
    [caller(0, 2).map { |l| short(l) }, caller_locations(0, 2).map { |l| [l.lineno, l.label] }]
  end

  def inner_raise(x)
    raise E1, "inner" if x
    :none
  end
  def outer_handler(x)
    inner_raise(x)
  rescue E1 => e
    [short(e.backtrace[0]), short(e.backtrace[1]), e.backtrace_locations[1].label]
  end
end
o5 = R5.new

t('retry with a counter') { |i| o5.retries(i % 4 + 1) }
t('raised three frames down') { |i| o5.from_deep(i % 2 == 1) }
t('backtrace and backtrace_locations') { |i| o5.bt(i % 2 == 1) }
t('caller and caller_locations in the handler') { |i| o5.callers(i % 2 == 1) }
t('backtrace of a callee\'s raise') { |i| o5.outer_handler(i % 2 == 1) }
t('the uncaught one\'s backtrace') do |i|
  begin
    o1.no_match(true)
  rescue E2 => e
    e.backtrace[0, 2].map { |l| short(l) }
  end
end

# ---------------------------------------------------------------- 6. what reads the frame

class R6
  def frame_in_handler(x)
    y = x * 2
    raise E1 if x > 0
    :none
  rescue E1
    [local_variables, binding.local_variable_get(:y), eval("y + 1"), block_given?]
  end

  def frame_in_ensure(x)
    y = x + 10
    :value
  ensure
    @got = [local_variables, binding.local_variable_get(:y), eval("y"), block_given?]
  end
  attr_reader :got

  def body_local(x)
    a = x + 1
    b = a * 2
    raise E1 if x > 0
    b
  rescue E1
    [a, b]
  end

  def local_after_raise(x)
    a = 1
    raise E1 if x
    a = 2
  rescue E1
    a
  end

  def given_in_handler
    raise E1
  rescue E1
    block_given? ? yield : :no_block
  end
end
o6 = R6.new

t('binding, local_variables, eval, block_given?') { |i| o6.frame_in_handler(i % 2) }
t('the same with a block') { |i| o6.frame_in_handler(i % 2) { } }
t('the same in ensure') { |i| [o6.frame_in_ensure(i % 2), o6.got] }
t('a local of the body read in the handler') { |i| o6.body_local(i % 2) }
t('a local not assigned past the raise') { |i| o6.local_after_raise(i % 2 == 1) }
t('block_given? and yield in the handler') { |i| [o6.given_in_handler, o6.given_in_handler { :blk }] }

# ---------------------------------------------------------------- 7. re-raise, cause, other exceptions

class R7
  def reraise(x)
    raise E1, "re #{x}"
  rescue E1
    raise
  end

  def with_cause(x)
    raise E1, "first"
  rescue E1
    raise E2, "second" if x
    :none
  end

  def cause_from_csharp(x)
    raise E1, "first"
  rescue E1
    x / 0
  end

  def bare_rescue_only(e)
    raise e
  rescue
    :rescued
  end

  def exception_class(e)
    raise e
  rescue Exception => ex
    ex.class
  end
end
o7 = R7.new

t('raise with no argument') do |i|
  begin
    o7.reraise(i % 2)
  rescue E1 => e
    [e.message, short(e.backtrace[0]), e.cause]
  end
end
t('Exception#cause') do |i|
  begin
    o7.with_cause(true)
  rescue E2 => e
    [e.message, e.cause.class, e.cause.message, e.cause.cause]
  end
end
t('the cause of a C# exception') do |i|
  begin
    o7.cause_from_csharp(i % 2 + 1)
  rescue ZeroDivisionError => e
    [e.message, e.cause.message]
  end
end
t('Interrupt passes a bare rescue') { |i| guard { o7.bare_rescue_only(Interrupt) } }
t('SystemExit passes a bare rescue') { |i| guard { o7.bare_rescue_only(SystemExit) } }
t('ScriptError passes a bare rescue') { |i| guard { o7.bare_rescue_only(NotImplementedError) } }
t('rescue Exception takes them') { |i| [Interrupt, SystemExit, E1, NotImplementedError].map { |e| o7.exception_class(e) } }

# ---------------------------------------------------------------- 8. Thread#raise, Kernel#loop

class R8
  def spin(q)
    q << :ready
    n = 0
    while true
      n += 1
      Thread.pass
    end
  rescue RuntimeError => e
    e.message
  ensure
    q << :ensure
  end

  def loop_stop(e)
    loop do
      e.next
    end
  end

  def loop_result
    e = [1, 2].each
    loop { e.next }
  end
end
o8 = R8.new

t('Thread#raise into a loop with a rescue', 20) do |i|
  q = Queue.new
  th = Thread.new { o8.spin(q) }
  q.pop
  th.raise("stop #{i % 2}")
  [th.value, q.pop]
end
t('StopIteration ends Kernel#loop') { |i| e = [1, 2, 3].each; o8.loop_stop(e) }
t('the result of Kernel#loop') { |i| o8.loop_result }
t('Kernel#loop with a break') { |i| n = 0; loop { n += 1; break n * 10 if n > i % 3 } }
t('StopIteration raised by hand') { |i| loop { raise StopIteration, "by hand" } }

# ---------------------------------------------------------------- 9. redefinition, depth, threads

class R9
  def m(x)
    raise E1 if x
    :first
  rescue E1
    :first_rescued
  end

  def forever(n)
    forever(n + 1)
  rescue ArgumentError
    :never
  end

  def top
    forever(0)
  rescue SystemStackError => e
    e.class
  end

  def work(n)
    raise E1, n.to_s if n % 3 == 0
    n
  rescue E1 => e
    -e.message.to_i
  end
end
o9 = R9.new

t('redefined half way') do |i|
  if i == N / 2
    R9.class_eval do
      def m(x)
        raise E2 if x
        :second
      rescue E2
        :second_rescued
      end
    end
  end
  o9.m(i % 2 == 1)
end
t('a million deep, rescued at the top', 3) { |i| o9.top }
t('four threads raising and rescuing', 30) do |i|
  4.times.map { |k| Thread.new { s = 0; 3000.times { |n| s += o9.work(n + k) }; s } }.map(&:value)
end

# ---------------------------------------------------------------- 10. rescue in a block, the modifier, other targets

class R11
  attr_accessor :last, :n

  def each_rescue(list)
    out = []
    list.each do |x|
      begin
        out << 10 / x
      rescue ZeroDivisionError => e
        out << [e.message, $!.class]
      end
    end
    out
  end

  def break_in_block_rescue(list)
    list.each do |x|
      begin
        raise E1 if x == 0
      rescue E1
        break :broke
      end
    end
  end

  def return_in_block_rescue(list)
    list.each do |x|
      begin
        raise E1 if x == 0
      rescue E1
        return :returned
      ensure
        @n = x
      end
    end
    :done
  end

  def retry_in_block(list)
    k = 0
    list.map do |x|
      begin
        k += 1
        raise E1 if k < 3
        [x, k]
      rescue E1
        retry
      end
    end
  end

  def modifier(x) = (10 / x rescue :zero)

  def modifier_assign(x)
    y = Integer(x) rescue -1
    y
  end

  def target_attr(x)
    raise E1, "attr" if x
    :none
  rescue => self.last
    last.message
  end

  def ensure_with_block(list)
    list.sum { |x| x * 2 }
  ensure
    @n = list.size
  end
end
o11 = R11.new

t('rescue in a block of the method') { |i| [o11.each_rescue([1, i % 2, 5]), $!] }
t('break from a rescue clause in a block') { |i| o11.break_in_block_rescue([1, i % 2, 3]) }
t('return from a rescue clause in a block') { |i| [o11.return_in_block_rescue([1, i % 2, 3]), o11.n] }
t('retry in a block') { |i| o11.retry_in_block([i % 2, 7]) }
t('rescue modifier') { |i| o11.modifier(i % 3) }
t('rescue modifier in an assignment') { |i| o11.modifier_assign(["1", "x", nil][i % 3]) }
t('rescue => an attribute') { |i| x = i % 2 == 1; [o11.target_attr(x), o11.last&.message] }
t('ensure in a method with a literal block') { |i| [o11.ensure_with_block([1, i % 3]), o11.n] }

# ---------------------------------------------------------------- 11. TracePoint, refinements

class R10
  def traced(x)
    raise E1, "tp" if x
    :none
  rescue E1
    :rescued
  end
end
o10 = R10.new
t('TracePoint :raise and :rescue') do |i|
  if i % 500 == 0
    seen = []
    tp = TracePoint.new(:raise, :rescue) { |p| seen << [p.event, p.method_id, p.event == :raise ? p.lineno : nil, p.raised_exception.class] if p.method_id == :traced }
    tp.enable { o10.traced(true); o10.traced(false) }
    seen
  else
    o10.traced(i % 2 == 1)
  end
end
t('after the TracePoint') { |i| o10.traced(i % 2 == 1) }

module RRefine
  refine Integer do
    def risky = self > 0 ? raise(E1, "refined") : :ok
  end
end
using RRefine
class R10R
  def call_refined(x)
    x.risky
  rescue E1 => e
    e.message
  end
end
o10r = R10R.new
t('refinement active where defined') { |i| o10r.call_refined(i % 2) }
