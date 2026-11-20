# Shapes for a `raise' in a method JIT typed body (JitCompiler.EmitRaise: the raise is a deopt, and the
# generic body raises), in the format Util/perf/ab.rb reads: `<shape> <ns> ns <checksum>` per shape; shape
# names as arguments, N from the environment.
#
#   ruby Util/perf/ab.rb -r 5 Util/bench/jit-raise-shapes.rb cruby=ruby "base=env IR_CONFIG=Release ../base/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#
# lookup, caller, check: the raise is never taken (what compiling such methods buys). every, half, 1k, burst:
# the raise is taken every call, every second call, one call in a thousand, and in runs of 500 calls every
# 5000 (what it costs when taken), each caught by a rescue in the caller; they run N / 20 iterations.
$stdout.sync = true
N = (ENV['N'] || 3_000_000).to_i

class RsScheduler
  def initialize
    @task_table = Array.new(8) { |i| i * 3 + 1 }
    @queued = 0
  end
  # Richards' Scheduler#find_task: a lookup that raises on a miss
  def find_task(identity)
    t = @task_table[identity]
    raise 'find_task failed' unless t
    t
  end
  # Richards' Scheduler#queue_packet: a caller that binds it
  def queue_packet(identity) = find_task(identity) + 1
  # an argument check at the top of an arithmetic method
  def scale(x, y)
    raise ArgumentError, 'negative scale' if x < 0
    x * y + 1
  end
end

SHAPES = %w[lookup caller check every half 1k burst]

def run_shape(name)
  s = RsScheduler.new
  r = 0
  best = nil
  n = %w[lookup caller check].include?(name) ? N : N / 20
  6.times do
    t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    i = 0
    case name
    when 'lookup' then while i < n; r += s.find_task(i & 7); i += 1; end
    when 'caller' then while i < n; r += s.queue_packet(i & 7); i += 1; end
    when 'check'  then while i < n; r += s.scale(i & 15, 3); i += 1; end
    when 'every'  then while i < n; r += (begin; s.find_task(9); rescue RuntimeError; 1; end); i += 1; end
    when 'half'   then while i < n; r += (begin; s.find_task((i & 1) * 9); rescue RuntimeError; 1; end); i += 1; end
    when '1k'     then while i < n; r += (begin; s.find_task(i % 1000 == 0 ? 9 : 3); rescue RuntimeError; 1; end); i += 1; end
    when 'burst'  then while i < n; r += (begin; s.find_task(i % 5000 < 500 ? 9 : 3); rescue RuntimeError; 1; end); i += 1; end
    end
    d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    best = d if best.nil? || d < best
  end
  puts format('%s %.1f ns %s', name, best * 1e9 / n, r)
end

(ARGV.empty? ? SHAPES : ARGV).each { |s| run_shape(s) }
