# What entering and leaving a method's frame costs, light (MethodDefinition.TransformLightFrame) or with its scope:
# ns per call of a method that does almost nothing, the caller a `while' loop, and of String#bytesplice (a large
# light frame). The light frame's switch is the second arm; -X:NoJIT keeps the method JIT from replacing the bodies:
#
#   ruby Util/perf/ab.rb -i Util/bench/frame-cost.rb "light=env IR_CONFIG=Release ./ir.sh -X:NoJIT" \
#        "scoped=env IR_LIGHT_FRAMES=0 IR_CONFIG=Release ./ir.sh -X:NoJIT"
#
# (Util/perf/callgrind/count.sh in place of ./ir.sh where the CPU's counter cannot be opened.)
$stdout.sync = true

class F
  def f0 = nil
  def f1(x) = x
  def fc(x) = x.itself
  def fc3(x) = x.itself.itself.itself
  def fy(x) = yield(x)
  def fbg = block_given? ? 1 : 0
end

def cpu
  File.read("/proc/thread-self/schedstat").to_i / 1e9
rescue SystemCallError
  Process.clock_gettime(Process::CLOCK_MONOTONIC)
end

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
f = F.new
N = (ENV['N'] || 1_000_000).to_i
src = "abcdefgh"

t("f0", N) { |n| i = 0; c = 0; while i < n; f.f0; c += 1; i += 1; end; c }
t("f1", N) { |n| i = 0; c = 0; while i < n; c += f.f1(1); i += 1; end; c }
t("fc", N) { |n| i = 0; c = 0; while i < n; c += f.fc(1); i += 1; end; c }
t("fc3", N) { |n| i = 0; c = 0; while i < n; c += f.fc3(1); i += 1; end; c }
t("fy", N) { |n| i = 0; c = 0; while i < n; c += f.fy(1) { |x| x }; i += 1; end; c }
t("fbg", N) { |n| i = 0; c = 0; while i < n; c += f.fbg; i += 1; end; c }
t("bytesplice", N / 5) { |n| i = 0; c = 0; while i < n; c += src.dup.bytesplice(1, 2, "xy").size; i += 1; end; c }
