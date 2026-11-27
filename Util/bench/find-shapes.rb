# Array#find, #detect, #find_index, #any? and #all? given a literal block by a method (round thirty-seven, perf/iter2): they
# are Array's own (find walked #each through Enumerable's C# before) and [BlockNotKept], so the method parks its scope with
# the block's Proc (IR_PARKED_FIND). ns per call of the method; the switch is the third arm:
#
#   ruby Util/perf/ab.rb -i Util/bench/find-shapes.rb "base=env IR_CONFIG=Release ../ironruby/ir.sh" \
#        "off=env IR_PARKED_FIND=0 IR_CONFIG=Release ./ir.sh" "on=env IR_CONFIG=Release ./ir.sh"
#
# each1, times1 and map1 are controls (already parked before); upto and downto are not parked (tried, see the ledger's
# perf/iter2: more instructions, the same time).
$stdout.sync = true

class O
  def find(a) = a.find { |x| x > 2 }
  def detect(a) = a.detect { |x| x > 2 }
  def find_index(a) = a.find_index { |x| x > 2 }
  def any(a) = a.any? { |x| x > 1000 } ? 1 : 0
  def all(a) = a.all? { |x| x > 0 } ? 1 : 0
  def upto(k); s = 0; 1.upto(k) { |j| s += j }; s; end
  def downto(k); s = 0; k.downto(1) { |j| s += j }; s; end
  def each(a); s = 0; a.each { |x| s += x }; s; end
  def times(k); s = 0; k.times { |j| s += j + 1 }; s; end
  def map(a) = a.map { |x| x + 1 }.size
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
o = O.new
N = (ENV['N'] || 500_000).to_i
a1 = [3]
a4 = [1, 2, 0, 3]

t("find1", N)     { |n| i = 0; s = 0; while i < n; s += o.find(a1); i += 1; end; s }
t("find4", N)     { |n| i = 0; s = 0; while i < n; s += o.find(a4); i += 1; end; s }
t("detect1", N)   { |n| i = 0; s = 0; while i < n; s += o.detect(a1); i += 1; end; s }
t("findidx4", N)  { |n| i = 0; s = 0; while i < n; s += o.find_index(a4); i += 1; end; s }
t("any1", N)      { |n| i = 0; s = 0; while i < n; s += o.any(a1); i += 1; end; s }
t("any4", N)      { |n| i = 0; s = 0; while i < n; s += o.any(a4); i += 1; end; s }
t("all4", N)      { |n| i = 0; s = 0; while i < n; s += o.all(a4); i += 1; end; s }
t("upto1", N)     { |n| i = 0; s = 0; while i < n; s += o.upto(1); i += 1; end; s }
t("upto4", N)     { |n| i = 0; s = 0; while i < n; s += o.upto(4); i += 1; end; s }
t("downto4", N)   { |n| i = 0; s = 0; while i < n; s += o.downto(4); i += 1; end; s }
t("each1", N)     { |n| i = 0; s = 0; while i < n; s += o.each(a1); i += 1; end; s }
t("times1", N)    { |n| i = 0; s = 0; while i < n; s += o.times(1); i += 1; end; s }
t("map1", N)      { |n| i = 0; s = 0; while i < n; s += o.map(a1); i += 1; end; s }
