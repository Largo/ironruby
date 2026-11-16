# Shapes for stage 1 of Docs/jit-resume-deopt.md, in the format Util/perf/ab.rb reads:
# `<shape> <ns> ns <checksum>` per shape; shape names as arguments, N from the environment.
#
#   ruby Util/perf/ab.rb -r 5 Util/bench/jit-resume-shapes.rb cruby=ruby "off=env IR_CONFIG=Release ./ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh -X:JITResume"
$stdout.sync = true
N = (ENV['N'] || 3_000_000).to_i

class RsTask
  attr_accessor :task_holding, :flag
  attr_reader :link
  def initialize(link); @link = link; @task_holding = false; @flag = nil; end
end

class RsSched
  attr_reader :hold_count
  def initialize
    @hold_count = 0
    @current_task = RsTask.new(RsTask.new(nil))
    @x = 1
  end

  # Richards' Scheduler#hold_self: an ivar increment, an attr write on another object, an
  # attr read on it.
  def hold_self
    @hold_count += 1
    @current_task.task_holding = true
    @current_task.link
  end

  # an ivar write, then one attr write in statement position, then a literal
  def hold_w
    @hold_count += 1
    @current_task.task_holding = true
    nil
  end

  # an ivar write, then two attr writes and a read
  def hold_ww
    @hold_count += 1
    @current_task.task_holding = true
    @current_task.flag = false
    @current_task.link
  end

  # control: nothing after the effect; compiled today
  def bump
    @hold_count += 1
  end

  # control: no effect at all; compiled today
  def peek
    @current_task.link
  end
end

SHAPES = %w[hold_self hold_w hold_ww bump peek]

def run_shape(name)
  s = RsSched.new
  r = nil
  # warm up past the JIT's threshold
  20_000.times { r = s.send(name) }
  best = nil
  5.times do
    t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    i = 0
    case name
    when 'hold_self' then while i < N; r = s.hold_self; i += 1; end
    when 'hold_w'    then while i < N; r = s.hold_w; i += 1; end
    when 'hold_ww'   then while i < N; r = s.hold_ww; i += 1; end
    when 'bump'      then while i < N; r = s.bump; i += 1; end
    when 'peek'      then while i < N; r = s.peek; i += 1; end
    end
    d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    best = d if best.nil? || d < best
  end
  sum = "#{s.hold_count}:#{r.class}"
  puts format('%s %.1f ns %s', name, best * 1e9 / N, sum)
end

(ARGV.empty? ? SHAPES : ARGV).each { |n| run_shape(n) }
