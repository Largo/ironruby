# What a multiple assignment costs in a method the method JIT can take (JitCompiler.EmitParallelAssignment): ns per
# call of the method, the caller a `while' loop. swap `x, y = y, x', three `a, b, c = x, x + 1, x * 2', ivars
# `@a, @b = x, y', value the expression's Array used; seq `a = x; b = x + 1' is the control (taken already).
#
#   ruby Util/perf/ab.rb -r 6 Util/bench/jit-masgn-shapes.rb "base=env IR_CONFIG=Release ../ironruby/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#   ruby Util/perf/ab.rb -i -r 1 -n 20000 Util/bench/jit-masgn-shapes.rb \
#        "base=env IR_CONFIG=Release Util/perf/callgrind/count.sh ../ironruby" "new=env IR_CONFIG=Release Util/perf/callgrind/count.sh ."
#
# Every line prints the sum of its results next to its time; it must be CRuby's.
$stdout.sync = true

class MaShapes
  def initialize; @a = 0; @b = 0; end
  def swap(x, y)
    x, y = y, x
    x - y
  end
  def three(x)
    a, b, c = x, x + 1, x * 2
    a + b + c
  end
  def ivars(x, y)
    @a, @b = x, y
    x
  end
  def value(x) = (a, b = x, x + 1).size
  def seq(x)
    a = x
    b = x + 1
    a + b
  end
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
s = MaShapes.new
N = (ENV["N"] || 1_000_000).to_i
t("swap", N)  { |n| i = 0; r = 0; while i < n; r += s.swap(i, 3); i += 1; end; r }
t("three", N) { |n| i = 0; r = 0; while i < n; r += s.three(i); i += 1; end; r }
t("ivars", N) { |n| i = 0; r = 0; while i < n; r += s.ivars(i, 2); i += 1; end; r }
t("value", N) { |n| i = 0; r = 0; while i < n; r += s.value(i); i += 1; end; r }
t("seq", N)   { |n| i = 0; r = 0; while i < n; r += s.seq(i); i += 1; end; r }
