# What a Hash literal costs in a method the method JIT can take (JitCompiler.EmitHashLiteral): ns per call of the
# method, the caller a `while' loop. empty `{}', sym2 `{a: x, b: x + 1}', str1 `{"k" => x}', init an initialize-like
# method that stores a Hash, an Array and an argument in instance variables, default a `(x, opts = {})' called
# without opts; arr2 `[x, x + 1]' is the control (the typed body took Array literals already).
#
#   ruby Util/perf/ab.rb -r 6 Util/bench/jit-hash-shapes.rb "base=env IR_CONFIG=Release ../ironruby/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#   ruby Util/perf/ab.rb -i -r 1 -n 20000 Util/bench/jit-hash-shapes.rb \
#        "base=env IR_CONFIG=Release Util/perf/callgrind/count.sh ../ironruby" "new=env IR_CONFIG=Release Util/perf/callgrind/count.sh ."
#
# Every line prints the sum of its results next to its time; it must be CRuby's.
$stdout.sync = true

class HlShapes
  def empty(x) = {}
  def sym2(x) = {a: x, b: x + 1}
  def str1(x) = {"k" => x}
  def init(x)
    @h = {}
    @a = []
    @x = x
    x
  end
  def default(x, opts = {}) = opts.size + x
  def arr2(x) = [x, x + 1]
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
s = HlShapes.new
N = (ENV["N"] || 1_000_000).to_i
t("empty", N)   { |n| i = 0; r = 0; while i < n; r += s.empty(i).size; i += 1; end; r }
t("sym2", N)    { |n| i = 0; r = 0; while i < n; r += s.sym2(i).size; i += 1; end; r }
t("str1", N)    { |n| i = 0; r = 0; while i < n; r += s.str1(i).size; i += 1; end; r }
t("init", N)    { |n| i = 0; r = 0; while i < n; r += s.init(i & 7); i += 1; end; r }
t("default", N) { |n| i = 0; r = 0; while i < n; r += s.default(i & 7); i += 1; end; r }
t("arr2", N)    { |n| i = 0; r = 0; while i < n; r += s.arr2(i).size; i += 1; end; r }
