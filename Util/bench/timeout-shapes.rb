# What Timeout.timeout costs, with and without a raise in its block (Src/StdLib/ironruby/timeout.rb: the watcher thread
# is woken for a new request only when it has to be). us per call; cpu is this thread's time, so the watcher thread's own
# work is not in it, nor in the instruction counts.
#
#   ruby Util/perf/ab.rb -r 6 Util/bench/timeout-shapes.rb "base=env IR_CONFIG=Release ../ironruby/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#   ruby Util/perf/ab.rb -i -r 1 -n 2000 Util/bench/timeout-shapes.rb \
#        "base=env IR_CONFIG=Release Util/perf/callgrind/count.sh ../ironruby" "new=env IR_CONFIG=Release Util/perf/callgrind/count.sh ."
#
# Every line prints the sum of its results next to its time; it must be CRuby's.
$stdout.sync = true
require 'timeout'

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
# INSTR=1 (Util/perf/ab.rb -i) adds a column: the instructions of a call, from the least of the
# seven sections as the time is. See Util/perf/instructions.rb.
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
N = (ENV["N"] || 20_000).to_i
t("raise", N)    { |n| i = 0; r = 0; while i < n; begin; Timeout.timeout(5) { raise "x" }; rescue RuntimeError; r += 1; end; i += 1; end; r }
t("no_raise", N) { |n| i = 0; r = 0; while i < n; r += Timeout.timeout(5) { 1 }; i += 1; end; r }
t("bare", N)     { |n| i = 0; r = 0; while i < n; begin; raise "x"; rescue RuntimeError; r += 1; end; i += 1; end; r }
