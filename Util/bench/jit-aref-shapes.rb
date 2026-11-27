# What `a[i]' costs in a method the method JIT can take once some Array has a singleton `[]' - as every program that
# loads CSV has (CSV::FieldsConverter::NO_QUOTED_FIELDS) - JitCompiler.EmitIndexRead with JitRuntime.IndexIntChecked:
# ns per call of the method, the caller a `while' loop. top `@a[-1]', at `@a[i]', pair `x[0]' on a parameter; size
# `@a.size' is the control (not affected).
#
#   ruby Util/perf/ab.rb -r 6 Util/bench/jit-aref-shapes.rb "base=env IR_CONFIG=Release ../ironruby/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#   ruby Util/perf/ab.rb -i -r 1 -n 20000 Util/bench/jit-aref-shapes.rb \
#        "base=env IR_CONFIG=Release Util/perf/callgrind/count.sh ../ironruby" "new=env IR_CONFIG=Release Util/perf/callgrind/count.sh ."
#
# Every line prints the sum of its results next to its time; it must be CRuby's.
$stdout.sync = true
require 'csv'

class ArShapes
  def initialize; @a = [1, 2, 3, 4]; end
  def top = @a[-1]
  def at(i) = @a[i]
  def pair(x) = x[0]
  def size = @a.size
end

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
s = ArShapes.new
N = (ENV["N"] || 1_000_000).to_i
pr = [5, 6]
t("top", N)  { |n| i = 0; r = 0; while i < n; r += s.top; i += 1; end; r }
t("at", N)   { |n| i = 0; r = 0; while i < n; r += s.at(i & 3); i += 1; end; r }
t("pair", N) { |n| i = 0; r = 0; while i < n; r += s.pair(pr); i += 1; end; r }
t("size", N) { |n| i = 0; r = 0; while i < n; r += s.size; i += 1; end; r }
