# What a method with `rescue', `else' or `ensure' costs, light frame or not (MethodDefinition.IsScopeFree, IR_LIGHT_RESCUE):
# ns per call of the method, the caller a `while' loop. The switch is the other arm:
#
#   ruby Util/perf/ab.rb -i Util/bench/rescue-shapes.rb "off=env IR_LIGHT_RESCUE=0 IR_CONFIG=Release ./ir.sh" \
#        "on=env IR_CONFIG=Release ./ir.sh"
#
# Each body asks is_a?, which the method JIT's screen refuses, as it refuses most methods of a program (Util/perf/census.rb):
# so the method runs with its frame, a light one or one with a scope. rescue is a rescue clause that is not entered;
# ensure an ensure clause; rescue_e a `rescue => e' not entered; else_ an else clause; begin_r a `begin'/`rescue' in the
# body; modifier a rescue modifier; raised1 a rescue entered once in a hundred calls (by a raise in the body); loop_break
# Kernel#loop left by a break, loop_stop by a StopIteration of an Enumerator (both in the prelude, whose `loop' has a
# `begin'/`rescue StopIteration'). plain, popt and yield1 have no handler and are the controls.
$stdout.sync = true

class H
  def rescue(a)
    a.is_a?(Integer) ? a : 0
  rescue StandardError
    0
  end

  def ensure(a)
    a.is_a?(Integer) ? a : 0
  ensure
    @done = true
  end

  def rescue_e(a)
    a.is_a?(Integer) ? a : 0
  rescue ArgumentError, TypeError => e
    e.message.size
  end

  def else_(a)
    b = a.is_a?(Integer) ? a : 0
  rescue StandardError
    0
  else
    b + 1
  end

  def begin_r(a)
    b = begin
      a.is_a?(Integer) ? a : 0
    rescue TypeError
      0
    end
    b + 1
  end

  def modifier(a) = (a.is_a?(Integer) ? a : raise(TypeError)) rescue 0

  def raised1(a)
    raise TypeError if a % 100 == 0
    a.is_a?(Integer) ? a : 0
  rescue TypeError
    1
  end

  def loop_break(a)
    k = 0
    loop do
      k += 1
      break if k > 1
    end
    a.is_a?(Integer) ? a + k : 0
  end

  def plain(a) = a.is_a?(Integer) ? a : 0
  def popt(a, b = 2) = a.is_a?(Integer) ? a + b : 0
  def yield1(a) = yield(a)
end

def cpu
  File.read("/proc/thread-self/schedstat").to_i / 1e9
rescue SystemCallError
  Process.clock_gettime(Process::CLOCK_MONOTONIC)
end

# INSTR=1 (Util/perf/ab.rb -i) adds a column: the instructions of a call, from the least of the seven sections as the
# time is. See Util/perf/instructions.rb.
INSTR = ENV['INSTR'] && (require_relative '../perf/instructions'; Instructions.open || warn('no instruction counter (perf_event_open)'))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9
  fewest = nil
  counted = !!INSTR
  sum = nil
  7.times do
    c0 = INSTR && INSTR.read
    t0 = cpu
    sum = yield n
    d = cpu - t0
    c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1
      c = c1 - c0
      fewest = c if fewest.nil? || c < fewest
    else
      counted = false
    end
  end
  printf "%-12s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format('  %.1f instr', fewest.to_f / n) : ''
end
h = H.new
N = (ENV['N'] || 500_000).to_i

t("rescue", N) { |n| i = 0; c = 0; while i < n; c += h.rescue(i); i += 1; end; c }
t("ensure", N) { |n| i = 0; c = 0; while i < n; c += h.ensure(i); i += 1; end; c }
t("rescue_e", N) { |n| i = 0; c = 0; while i < n; c += h.rescue_e(i); i += 1; end; c }
t("else_", N) { |n| i = 0; c = 0; while i < n; c += h.else_(i); i += 1; end; c }
t("begin_r", N) { |n| i = 0; c = 0; while i < n; c += h.begin_r(i); i += 1; end; c }
t("modifier", N) { |n| i = 0; c = 0; while i < n; c += h.modifier(i); i += 1; end; c }
t("raised1", N / 10) { |n| i = 0; c = 0; while i < n; c += h.raised1(i); i += 1; end; c }
t("loop_break", N / 5) { |n| i = 0; c = 0; while i < n; c += h.loop_break(i); i += 1; end; c }
t("loop_stop", N / 50) { |n| i = 0; c = 0; while i < n; e = [1, 2].each; loop { c += e.next }; i += 1; end; c }
t("plain", N) { |n| i = 0; c = 0; while i < n; c += h.plain(i); i += 1; end; c }
t("popt", N) { |n| i = 0; c = 0; while i < n; c += h.popt(i); i += 1; end; c }
t("yield1", N) { |n| i = 0; c = 0; while i < n; c += h.yield1(i) { |x| x }; i += 1; end; c }
