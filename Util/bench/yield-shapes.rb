# A `while ... yield` written in Ruby (gap 18, perf/yieldloop): a user-written #each over an Array and the prelude's
# methods made of such a loop, against Array#each in C#. ns per ELEMENT (each shape divides by the elements it yields):
#
#   ruby Util/perf/ab.rb -i Util/bench/yield-shapes.rb "base=env IR_CONFIG=Release ../ironruby/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#
# ueachK: `def each; i = 0; while i < @a.size; yield @a[i]; i += 1; end; end` over K elements with `{ |x| s += x }`;
# ueach4n: the same with a block that does nothing; cyield4: Array#each over 4, the C# iterator (control);
# take_while, sum_blk, uniq_blk, to_h_blk: the prelude's `while ... yield` methods over 100 elements;
# loop100 is the same loop with the yield taken out and the element read kept, and ueach100e yields to an empty block:
# their difference is what the yield costs, and loop100 with the tiers on (-X:NoJIT -X:NoOSR off) what the loop around a
# yield loses, the typed copy that the loop compiler gives up on at a YieldCall (round thirty-seven, see the ledger).
$stdout.sync = true

class L
  def initialize(a) = @a = a
  def each
    i = 0
    while i < @a.size
      yield @a[i]
      i += 1
    end
    self
  end

  def loop
    i = 0
    while i < @a.size
      @a[i]
      i += 1
    end
    self
  end
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
N = (ENV['N'] || 400_000).to_i
a1 = [1]; a4 = [1, 2, 3, 4]; a100 = (1..100).to_a
l1 = L.new(a1); l4 = L.new(a4); l100 = L.new(a100)

t("ueach1", N)     { |n| s = 0; i = 0; while i < n; l1.each { |x| s += x }; i += 1; end; s }
t("ueach4", N)     { |n| s = 0; i = 0; while i < n; l4.each { |x| s += x }; i += 4; end; s }
t("ueach100", N)   { |n| s = 0; i = 0; while i < n; l100.each { |x| s += x }; i += 100; end; s }
t("ueach100e", N)  { |n| s = 0; i = 0; while i < n; l100.each { |x| }; s += 1; i += 100; end; s }
t("loop100", N)    { |n| s = 0; i = 0; while i < n; l100.loop; s += 1; i += 100; end; s }
t("ueach4n", N)    { |n| s = 0; i = 0; while i < n; l4.each { |x| }; s += 1; i += 4; end; s }
t("cyield4", N)    { |n| s = 0; i = 0; while i < n; a4.each { |x| s += x }; i += 4; end; s }
t("take_while", N) { |n| s = 0; i = 0; while i < n; s += a100.take_while { |x| x < 1000 }.size; i += 100; end; s }
t("sum_blk", N)    { |n| s = 0; i = 0; while i < n; s += a100.sum { |x| x }; i += 100; end; s }
t("uniq_blk", N)   { |n| s = 0; i = 0; while i < n; s += a100.uniq { |x| x }.size; i += 100; end; s }
t("to_h_blk", N)   { |n| s = 0; i = 0; while i < n; s += a100.to_h { |x| [x, x] }.size; i += 100; end; s }
