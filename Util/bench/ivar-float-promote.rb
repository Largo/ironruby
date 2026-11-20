# What promoting an object to raw Float storage costs (RubyInstanceData, "Float locations"): a new
# object with Float variables `x, y', then k typed Float stores into x through attr_writer in a
# compiled method, for k = 0, 1, 2 and 10 - in ns and (BYTES=1, IronRuby) bytes per object.
# Format of Util/perf/ab.rb.
$stdout.sync = true
N = (ENV['N'] || 300_000).to_i
BYTES = ENV['BYTES'] == '1'
class PrPoint
  attr_accessor :x, :y
  def initialize(x, y) = (@x = x; @y = y)
end
def pr_store(o, d) = (o.x = o.x + d)
def pr_make(i) = PrPoint.new(i * 0.5, 1.5)
def pr_run(k)
  i = 0
  s = 0.0
  while i < N
    o = pr_make(i)
    j = 0
    while j < k; pr_store(o, 0.25); j += 1; end
    s += o.x
    i += 1
  end
  s
end
SHAPES = %w[promote0 promote1 promote2 promote10]
(ARGV.empty? ? SHAPES : ARGV).each do |name|
  k = name[/\d+/].to_i
  best = nil
  r = nil
  6.times do
    t = BYTES ? System::GC.get_total_allocated_bytes(true) : Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    r = pr_run(k)
    d = BYTES ? System::GC.get_total_allocated_bytes(true) - t : Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    best = d if best.nil? || d < best
  end
  puts BYTES ? format('%s %.1f B %s', name, best.to_f / N, r) : format('%s %.1f ns %s', name, best * 1e9 / N, r)
end
