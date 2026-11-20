# Shapes for stage 2 of Docs/jit-resume-deopt.md (general calls from a compiled body), in the
# format Util/perf/ab.rb reads: `<shape> <ns> ns <checksum>` per shape; shape names as
# arguments, N from the environment. Modelled on Richards' Scheduler and TaskControlBlock: each
# method below is one the method JIT's screen rejects today for a call it makes.
#
#   ruby Util/perf/ab.rb -r 5 Util/bench/jit-resume2-shapes.rb cruby=ruby \
#        "off=env IR_CONFIG=Release ./ir.sh" "on=env IR_CONFIG=Release ./ir.sh -X:JITResume"
$stdout.sync = true
N = (ENV['N'] || 2_000_000).to_i

class S2Packet
  attr_accessor :link, :identity, :kind, :datum
  def initialize(identity)
    @link = nil
    @identity = identity
    @kind = 1
    @datum = 0
  end
end

class S2Task
  attr_accessor :link, :input, :priority, :holding, :runs
  def initialize(link, priority)
    @link = link
    @input = nil
    @priority = priority
    @holding = false
    @runs = 0
  end

  # add_input_and_check_priority: an ivar test, an attribute write or a self-call, a comparison
  # of two attribute reads, a parameter returned
  def add_input(packet, old_task)
    if @input.nil?
      @input = packet
      @holding = false
      return self if @priority > old_task.priority
    else
      @input = append(packet, @input)
    end
    old_task
  end

  def append(packet, queue)
    packet.link = queue
    packet
  end

  # run_task without its proc: state tests on self, a counter, the next task
  def step
    @runs += 1
    @input = nil
    @link
  end
end

class S2Sched
  attr_reader :count, :steps
  def initialize
    @count = 0
    @steps = 0
    @current_id = 1
    low = S2Task.new(nil, 1)
    high = S2Task.new(low, 5)
    @list = S2Task.new(high, 3)
    @current = low
    @table = [nil, low, high, @list]
  end

  # find_task: a parameter, an element read, a nil test, a call that raises
  def find_task(id)
    t = @table[id]
    raise 'find_task failed' if t.nil?
    t
  end

  # queue_packet: a self-call first, then a nil test with an early return, an ivar increment,
  # two attribute writes on a parameter and a call on a local with two arguments
  def queue_packet(packet)
    task = find_task(packet.identity)
    return nil if task.nil?
    @count += 1
    packet.link = nil
    packet.identity = @current_id
    task.add_input(packet, @current)
  end

  # schedule: no parameters; a loop over a linked list with a call per element
  def schedule
    t = @list
    while t
      if t.holding
        t = t.link
      else
        @steps += 1
        t = t.step
      end
    end
    @steps
  end
end

SHAPES = %w[find_task queue_packet add_input schedule]

def run_shape(name)
  s = S2Sched.new
  packet = S2Packet.new(2)
  task = s.find_task(3)
  other = s.find_task(1)
  r = nil
  20_000.times do
    packet.identity = 2
    case name
    when 'find_task' then r = s.find_task(2)
    when 'queue_packet' then r = s.queue_packet(packet)
    when 'add_input' then r = task.add_input(packet, other)
    when 'schedule' then r = s.schedule
    end
  end
  best = nil
  5.times do
    t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    i = 0
    case name
    when 'find_task' then while i < N; r = s.find_task(2); i += 1; end
    when 'queue_packet' then while i < N; packet.identity = 2; r = s.queue_packet(packet); i += 1; end
    when 'add_input' then while i < N; r = task.add_input(packet, other); i += 1; end
    when 'schedule' then while i < N; r = s.schedule; i += 1; end
    end
    d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    best = d if best.nil? || d < best
  end
  puts format('%s %.1f ns %s', name, best * 1e9 / N, "#{s.count}:#{s.steps}:#{r.class}")
end

(ARGV.empty? ? SHAPES : ARGV).each { |n| run_shape(n) }
