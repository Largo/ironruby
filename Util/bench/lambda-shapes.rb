# What a method with a lambda literal costs, light frame or not (MethodDefinition.IsScopeFree, IR_LIGHT_LAMBDA): ns per
# call of the method, the caller a `while` loop.
#
#   ruby Util/perf/ab.rb -r 10 Util/bench/lambda-shapes.rb "off=env IR_LIGHT_LAMBDA=0 IR_CONFIG=Release ./ir.sh" \
#        "on=env IR_CONFIG=Release ./ir.sh"
#
# rare and branch make their lambda on no call (or one in eight): the light frame makes no scope then. always makes and
# calls one on every call, kept returns one: the scope is made either way, the light frame later. size is a control
# without a lambda.
$stdout.sync = true

class H
  def rare(x) = x < 0 ? ->(y) { y - x }.call(1) : x + 1
  def branch(x) = (x & 7) == 0 ? ->(y) { y + x }.call(1) : x - 1
  def always(x) = ->(y) { y + x }.call(1)
  def kept(x) = ->(y) { y + x }
  def size(x) = x + 1
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
h = H.new
N = (ENV['N'] || 500_000).to_i

t("rare", N) { |n| i = 0; c = 0; while i < n; c += h.rare(i); i += 1; end; c }
t("branch", N) { |n| i = 0; c = 0; while i < n; c += h.branch(i); i += 1; end; c }
t("always", N) { |n| i = 0; c = 0; while i < n; c += h.always(i); i += 1; end; c }
t("kept", N) { |n| i = 0; c = 0; while i < n; c += h.kept(i).call(1); i += 1; end; c }
t("size", N) { |n| i = 0; c = 0; while i < n; c += h.size(i); i += 1; end; c }
