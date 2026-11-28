# Integer instance variables stored raw (perf/intivar; RubyInstanceData, "Float locations"): what the
# typed code's writes and reads of an Integer variable cost, and what raw storage costs the generic
# code that reads the same objects. Format of Util/perf/ab.rb: `<shape> <ns> ns <checksum>` per shape;
# shape names as arguments, N from the environment. BYTES=1 (IronRuby only): bytes allocated per
# operation instead of ns. INSTR=1 (ab.rb -i): instructions per operation as well.
#
#   ruby Util/perf/ab.rb -r 5 Util/bench/intivar-shapes.rb cruby=ruby \
#        "base=env IR_CONFIG=Release ../base/ir.sh" "off=env IR_RAW_INTS=0 IR_CONFIG=Release ./ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#
#   counter      `@n += 1' in a method the JIT types, above the small-integer cache (a write each call)
#   counter_s    the same, the value staying in the cache (0..511)
#   seed         AWFY's Random#next (som.rb): `@seed = ((@seed * 1309) + 13849) & 65535'
#   bounce       AWFY's Ball#bounce: four Integer variables read and written
#   bounce_new   AWFY's Bounce as it is: 100 new Balls, each bounced 50 times (ns per bounce)
#   attr         `o.w = o.w + 1' in a typed method (WriteAttr)
#   read_typed   `@n * 2' of a raw variable in a typed method
#   read_raw     a generic read (a method the JIT refuses) of an object whose class is born raw
#   read_plain   the same of an object of a class never promoted: the control for read_raw
#   read_chg     a generic read after every typed write: the box a raw value costs its generic reader
$stdout.sync = true
N = (ENV['N'] || 2_000_000).to_i
BYTES = ENV['BYTES'] == '1'
INSTR = ENV['INSTR'] && (require_relative '../perf/instructions'; Instructions.open || warn('no instruction counter (perf_event_open)'))

class Cnt
  attr_accessor :w
  def initialize(n) = (@n = n; @w = n; @k = 0)
  def inc = (@n += 1; nil)
  def inc_s = (@n = (@n + 1) & 511; nil)
  def twice = (@n * 2)
  def n = @n
  def gread = (begin; @n; rescue; nil; end)     # refused by the JIT (rescue): the generic read
end
class Plain
  def initialize(n) = (@n = n; @w = n; @k = 0)
  def gread = (begin; @n; rescue; nil; end)
end
def bump(o) = (o.w = o.w + 1; nil)

class Rnd
  def initialize = (@seed = 74755)
  def next = (@seed = ((@seed * 1309) + 13849) & 65535; @seed)
end
class Ball
  def initialize(r)
    @x = r.next % 500; @y = r.next % 500
    @x_vel = (r.next % 300) - 150; @y_vel = (r.next % 300) - 150
  end
  def bounce
    x_limit = 500; y_limit = 500; bounced = false
    @x += @x_vel; @y += @y_vel
    if @x > x_limit then @x = x_limit; @x_vel = 0 - @x_vel.abs; bounced = true end
    if @x < 0 then @x = 0; @x_vel = @x_vel.abs; bounced = true end
    if @y > y_limit then @y = y_limit; @y_vel = 0 - @y_vel.abs; bounced = true end
    if @y < 0 then @y = 0; @y_vel = @y_vel.abs; bounced = true end
    bounced
  end
  def pos = @x * 1000 + @y
end

# the typed methods hot, and Cnt born raw (on a build that stores Integers raw), before anything is timed
warm = Cnt.new(5000)
3000.times { warm.inc; warm.inc_s; warm.twice; bump(warm) }

SHAPES = %w[counter counter_s seed bounce bounce_new attr read_typed read_raw read_plain read_chg]

def run_once(name)
  i = 0
  case name
  when 'counter' then o = Cnt.new(5000); while i < N; o.inc; i += 1; end; o.n
  when 'counter_s' then o = Cnt.new(0); while i < N; o.inc_s; i += 1; end; o.n
  when 'seed' then r = Rnd.new; s = 0; while i < N; s += r.next; i += 1; end; s
  when 'bounce' then r = Rnd.new; b = Ball.new(r); c = 0; while i < N; c += 1 if b.bounce; i += 1; end; c * 1_000_000 + b.pos
  when 'bounce_new'
    r = Rnd.new; c = 0
    while i < N
      balls = Array.new(100) { Ball.new(r) }
      k = 0
      while k < 50; j = 0; while j < 100; c += 1 if balls[j].bounce; j += 1; end; k += 1; end
      i += 5000
    end
    c
  when 'attr' then o = Cnt.new(5000); while i < N; bump(o); i += 1; end; o.w
  when 'read_typed' then o = Cnt.new(5000); o.inc; o.inc; s = 0; while i < N; s += o.twice; i += 1; end; s
  when 'read_raw' then o = Cnt.new(5000); o.inc; o.inc; s = 0; while i < N; s += o.gread; i += 1; end; s
  when 'read_plain' then o = Plain.new(5002); s = 0; while i < N; s += o.gread; i += 1; end; s
  when 'read_chg' then o = Cnt.new(5000); s = 0; while i < N; o.inc; s += o.gread; i += 1; end; s
  end
end

def run_shape(name)
  best = nil
  fewest = nil
  r = nil
  6.times do
    c0 = INSTR && INSTR.read
    t = BYTES ? System::GC.get_total_allocated_bytes(true) : Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    r = run_once(name)
    d = BYTES ? System::GC.get_total_allocated_bytes(true) - t : Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    c1 = INSTR && INSTR.read
    fewest = c1 - c0 if c0 && c1 && (fewest.nil? || c1 - c0 < fewest)
    best = d if best.nil? || d < best
  end
  if BYTES
    puts format('%s %.1f B %s', name, best.to_f / N, r)
  else
    puts format('%s %.1f ns %s%s', name, best * 1e9 / N, r, fewest ? format('  %.1f instr', fewest.to_f / N) : '')
  end
end

(ARGV.empty? ? SHAPES : ARGV).each { |s| run_shape(s) }
