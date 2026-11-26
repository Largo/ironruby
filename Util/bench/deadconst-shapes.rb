# Shapes for a statement guarded by a constant that is false (`trace(x) if TRACING', Richards' Scheduler#schedule;
# IR_JIT_DEADCONST, JitCompiler.TryEmitConstantGuarded), in the format Util/perf/ab.rb reads: `<shape> <ns> ns
# <checksum>` per shape, shape names as arguments, N from the environment, INSTR=1 (ab.rb -i) for instructions per
# call. Each guarded shape has a control that is the same code without the guard: on a build that leaves the dead
# statement out the two should count the same.
#
#   ruby Util/perf/ab.rb -i Util/bench/deadconst-shapes.rb \
#        "base=env IR_CONFIG=Release ./ir.sh" "new=env IR_CONFIG=Release ../wt-deadconst/ir.sh"
$stdout.sync = true

TRACING = false
DEBUG_LEVEL = nil
module Conf; VERBOSE = false; end
FSTEP = 0.5

class Stepper
  attr_reader :traced
  def initialize; @traced = 0; @k = 3; end
  def trace(x); @traced += 1; x; end

  # a hot method: a call on self guarded by a false constant, then the work
  def step_dead(x)
    trace(x) if TRACING
    x + @k
  end

  # the same with `unless', a nil constant and a qualified one
  def step_dead2(x)
    trace(x) if DEBUG_LEVEL
    trace(x) if Conf::VERBOSE
    x + @k
  end

  # control: the work alone
  def step_plain(x)
    x + @k
  end

  # a loop inside a method called hot, with the guard in the loop
  def sum_dead(n)
    i = 0
    s = 0
    while i < n
      trace(i) if TRACING
      s += i
      i += 1
    end
    s
  end

  # a Float loop folding a constant: nothing else in it may deopt, so it is the kind of body that is now made one that
  # may (at its loop's safe point, when a constant is reassigned: gap 29)
  def fsum_const(n)
    x = 0.0
    i = 0.0
    while i < n
      x += FSTEP
      i += 1.0
    end
    x
  end

  # control: the step a parameter
  def fsum_plain(n, step)
    x = 0.0
    i = 0.0
    while i < n
      x += step
      i += 1.0
    end
    x
  end

  # control
  def sum_plain(n)
    i = 0
    s = 0
    while i < n
      s += i
      i += 1
    end
    s
  end
end

def trace(x); x; end

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
INSTR = ENV["INSTR"] && (require_relative "../perf/instructions"; Instructions.open || warn("no instruction counter (perf_event_open)"))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; fewest = nil; counted = !!INSTR; sum = nil
  7.times do
    c0 = INSTR && INSTR.read; t0 = cpu; sum = yield n; d = cpu - t0; c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end

o = Stepper.new
N = (ENV["N"] || 1_000_000).to_i
t("m_dead", N) { |n| i = 0; s = 0; while i < n; s += o.step_dead(i); i += 1; end; s + o.traced }
t("m_dead2", N) { |n| i = 0; s = 0; while i < n; s += o.step_dead2(i); i += 1; end; s + o.traced }
t("m_plain", N) { |n| i = 0; s = 0; while i < n; s += o.step_plain(i); i += 1; end; s + o.traced }
# a method with a short loop, called hot: the method JIT's (per call: 8 iterations)
t("mloop_dead", N / 4) { |n| i = 0; s = 0; while i < n; s += o.sum_dead(8); i += 1; end; s + o.traced }
t("mloop_plain", N / 4) { |n| i = 0; s = 0; while i < n; s += o.sum_plain(8); i += 1; end; s + o.traced }
t("mfloop_const", N / 4) { |n| i = 0; s = 0.0; while i < n; s += o.fsum_const(8.0); i += 1; end; s }
t("mfloop_plain", N / 4) { |n| i = 0; s = 0.0; while i < n; s += o.fsum_plain(8.0, 0.5); i += 1; end; s }
# a long loop run once: the loop tier's (OSR); its sum stays an Integer the loop's copy keeps as an int
t("loop_dead", N * 4) { |n| i = 0; s = 0; while i < n; trace(i) if TRACING; s += i & 7; i += 1; end; s }
t("loop_plain", N * 4) { |n| i = 0; s = 0; while i < n; s += i & 7; i += 1; end; s }
