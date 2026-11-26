# A block literal inside a block (round thirty-seven, perf/iter): the block that holds it made a scope, and a Proc and a
# BlockParam for the literal, on every call (BlockDefinition.ParkedBlockScopesEnabled, IR_PARKED_BLOCK_SCOPES). ns per call
# of the outer block; the switch is the third arm:
#
#   ruby Util/perf/ab.rb -i Util/bench/nest-shapes.rb "off=env IR_PARKED_BLOCK_SCOPES=0 IR_CONFIG=Release ./ir.sh" \
#        "on=env IR_CONFIG=Release ./ir.sh"
#
# light, each, times, two, kids and el3 are the ones it is for (the outer block runs many times through one BlockParam);
# el2 and once run their outer block twice and once per BlockParam, where nothing is reused (what it costs there);
# flat, noblk and meth have no literal inside a block (controls).
$stdout.sync = true

class O
  def e; yield 1; end
  def kids; yield 1; yield 2; end
  def f; 1; end
  def sh_each(a); s = 0; a.each { |x| s += x }; s; end
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
a1 = [1]
a2 = [1, 2]
a3 = [1, 2, 3]

t("light", N) { |n| s = 0; n.times { o.e { |c| s += c } }; s }
t("each", N)  { |n| s = 0; n.times { a1.each { |c| s += c } }; s }
t("times", N) { |n| s = 0; n.times { 1.times { |c| s += c + 1 } }; s }
t("two", N)   { |n| s = 0; n.times { o.e { |c| s += c }; a1.each { |c| s += c } }; s }
t("kids", N)  { |n| s = 0; n.times { |x| o.kids { |c| s += c + x } }; s }
t("el3", N)   { |n| i = 0; s = 0; while i < n; a3.each { |x| o.e { |c| s += c + x } }; i += 3; end; s }
t("el2", N)   { |n| i = 0; s = 0; while i < n; a2.each { |x| o.e { |c| s += c + x } }; i += 2; end; s }
t("once", N)  { |n| i = 0; s = 0; while i < n; o.e { |x| a1.each { |c| s += c + x } }; i += 1; end; s }
t("flat", N)  { |n| s = 0; n.times { |x| s += x }; s }
t("noblk", N) { |n| s = 0; n.times { s += o.f }; s }
t("meth", N)  { |n| i = 0; s = 0; while i < n; s += o.sh_each(a1); i += 1; end; s }
