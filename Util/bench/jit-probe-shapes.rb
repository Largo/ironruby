# The first calls of a method the JIT specializes, while its generic body is still interpreted (perf/jitprobe): each
# section defines a method of its own (a `def' compiled from a string, so that no compiled body is shared) and calls
# it N times - `fresh_mixed' with an Integer and a Float in turn, whose typed body fails on every other call;
# `fresh_int' with Integers, which never fails; `warm_int' a method defined once, as a control. `<shape> <ns> ns
# <checksum>' per line, for Util/perf/ab.rb; N is the calls a section makes (ab.rb -n); INSTR=1 adds instructions.
INSTR = ENV['INSTR'] && (require_relative '../perf/instructions'; Instructions.open || warn('no instruction counter (perf_event_open)'))
# a section is short: the thread's CPU clock, which schedstat updates too seldom for
def cpu
  Process.clock_gettime(Process::CLOCK_THREAD_CPUTIME_ID)
rescue SystemCallError, NameError
  Process.clock_gettime(Process::CLOCK_MONOTONIC)
end
$fresh = 0
def fresh_object
  $fresh += 1
  cls = Class.new
  cls.class_eval("def m(x) = x + #{$fresh}")
  cls.new
end
def t(name, n = 100_000)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  n = ENV['N'].to_i if ENV['N']
  best = 1e9; r = nil; fewest = nil; counted = !!INSTR
  5.times do
    o = fresh_object
    c0 = INSTR && INSTR.read
    t0 = cpu
    r = yield n, o
    d = cpu - t0
    c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1
      fewest = c1 - c0 if fewest.nil? || c1 - c0 < fewest
    else
      counted = false
    end
  end
  printf "%-14s %8.1f ns %s%s\n", name, best * 1e9 / n, r, counted ? format('  %.1f instr', fewest.to_f / n) : ''
end
class W
  def m(x) = x + 1
end
w = W.new
t("fresh_mixed") { |n, o| i = 0; c = 0; while i < n; c += o.m(i.odd? ? i : 0.5).to_i; i += 1; end; c - $fresh * n }
t("fresh_int")   { |n, o| i = 0; c = 0; while i < n; c += o.m(i); i += 1; end; c - $fresh * n }
t("warm_int")    { |n, o| i = 0; c = 0; while i < n; c += w.m(i); i += 1; end; c }
