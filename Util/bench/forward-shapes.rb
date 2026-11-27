# What a forwarding wrapper pays on top of a fixed-argument call, part by part: a `&k` parameter left unused, a block
# argument passed on (`&k`, `&nil`), a rest parameter splatted on, `...`, and `(*, &)`; the callee `two(a, b)` uses no block.
#   ruby Util/perf/ab.rb -i -r 1 -n 20000 Util/bench/forward-shapes.rb "base=env IR_CONFIG=Release Util/perf/callgrind/count.sh ."
# Every line prints the sum of its results next to its time; it must be CRuby's.
$stdout.sync = true
class FT; def two(a, b) = a; end
class FW
  def initialize(t) = @t = t
  def fixed(a, b) = @t.two(a, b)
  def fixed_blk(a, b, &k) = @t.two(a, b, &k)
  def rest(*a) = @t.two(*a)
  def rest_blk(*a, &k) = @t.two(*a, &k)
  def dots(...) = @t.two(...)
  def anon(*, &) = @t.two(*, &)
  def param_only(a, b, &k) = @t.two(a, b)
  def pass_nil(a, b) = @t.two(a, b, &nil)
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
o = FW.new(FT.new)
N = (ENV["N"] || 200_000).to_i
t("fixed", N)     { |n| i = 0; r = 0; while i < n; r += o.fixed(1, 2); i += 1; end; r }
t("fixed_blk", N) { |n| i = 0; r = 0; while i < n; r += o.fixed_blk(1, 2); i += 1; end; r }
t("rest", N)      { |n| i = 0; r = 0; while i < n; r += o.rest(1, 2); i += 1; end; r }
t("rest_blk", N)  { |n| i = 0; r = 0; while i < n; r += o.rest_blk(1, 2); i += 1; end; r }
t("dots", N)      { |n| i = 0; r = 0; while i < n; r += o.dots(1, 2); i += 1; end; r }
t("param_only", N) { |n| i = 0; r = 0; while i < n; r += o.param_only(1, 2); i += 1; end; r }
t("pass_nil", N)   { |n| i = 0; r = 0; while i < n; r += o.pass_nil(1, 2); i += 1; end; r }
t("anon", N)      { |n| i = 0; r = 0; while i < n; r += o.anon(1, 2); i += 1; end; r }
