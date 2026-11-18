# Shapes for bodies that hold their writes of self's instance variables back (JitCompiler.Sink.cs), in the
# format Util/perf/ab.rb reads: `<shape> <ns> ns <checksum>` per shape; shape names as arguments, N from
# the environment. `bounce' is Bounce's Ball#bounce called as the benchmark calls it; `two_types' and
# `one_type' are the two probes of a variable written as an Int and as a Long; the rest are the bodies of
# round seventeen's writes.rb (`mc' and `ms', writes in both arms of an `if', were level or slower there).
#
#   ruby Util/perf/ab.rb -r 5 Util/bench/jit-bounce-shapes.rb cruby=ruby "base=env IR_CONFIG=Release ../base/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
$stdout.sync = true
N = (ENV['N'] || 3_000_000).to_i

class BsBall
  attr_reader :x, :y, :x_vel, :y_vel
  def initialize(x, y, xv, yv); @x = x; @y = y; @x_vel = xv; @y_vel = yv; end

  def bounce
    x_limit = y_limit = 500
    bounced = false

    @x += @x_vel
    @y += @y_vel

    if @x > x_limit
      @x = x_limit
      @x_vel = 0 - @x_vel.abs
      bounced = true
    end

    if @x < 0
      @x = 0
      @x_vel = @x_vel.abs
      bounced = true
    end

    if @y > y_limit
      @y = y_limit
      @y_vel = 0 - @y_vel.abs
      bounced = true
    end

    if @y < 0
      @y = 0
      @y_vel = @y_vel.abs
      bounced = true
    end

    bounced
  end
end

class BsW
  attr_reader :a, :b, :i, :w, :x, :y
  def initialize; @a = 0; @b = 0; @i = 0; @w = 0; @x = 0; @y = 0; @vx = 3; @vy = 5; @v = 7; end
  def two_types; lim = 500; @x += @v; if @x > lim; @x = lim; end; nil; end
  def one_type; @y += @v; if @y > 500; @y = @y - 500; end; nil; end
  def w2(a, b); @a = a; @b = b; nil; end
  def w3(c); @w = c; @x = c + 1; @i += 1; nil; end
  def mc(c); if c > 0 then @w = 1; @x = 1 else @x = 2; @w = 2 end; @i += 1; nil; end
  def ms(c); if c > 0 then @w = 1; @x = 1 else @w = 2; @x = 2 end; @i += 1; nil; end
  def upd; @x += @vx; @y += @vy; nil; end
  def cnt(c); @i += 1; @a = c if c > 1000; nil; end
  def one(c); @a = c; nil; end
end

SHAPES = %w[bounce two_types one_type one w2 w3 mc ms upd cnt]

def run_shape(name)
  best = nil
  sum = nil
  6.times do
    o = BsW.new
    balls = Array.new(100) { |k| BsBall.new(k * 5, 500 - k * 5, (k % 300) - 150, 150 - (k % 300)) }
    t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    i = 0
    bounces = 0
    case name
    when 'bounce'
      # Bounce's benchmark: every ball bounced in turn, the bounces counted
      while i < N
        bounces += 1 if balls[i % 100].bounce
        i += 1
      end
    when 'two_types' then while i < N; o.two_types; i += 1; end
    when 'one_type'  then while i < N; o.one_type; i += 1; end
    when 'w2'  then while i < N; o.w2(i, 7); i += 1; end
    when 'w3'  then while i < N; o.w3(i); i += 1; end
    when 'mc'  then while i < N; o.mc(i & 1); i += 1; end
    when 'ms'  then while i < N; o.ms(i & 1); i += 1; end
    when 'upd' then while i < N; o.upd; i += 1; end
    when 'cnt' then while i < N; o.cnt(i & 2047); i += 1; end
    when 'one' then while i < N; o.one(i); i += 1; end
    end
    d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    best = d if best.nil? || d < best
    sum = [bounces, o.a, o.b, o.i, o.w, o.x, o.y, balls.sum { |b| b.x + 3 * b.y + 7 * b.x_vel + 11 * b.y_vel }]
  end
  puts format('%s %.1f ns %s', name, best * 1e9 / N, sum.inspect.delete(' '))
end

(ARGV.empty? ? SHAPES : ARGV).each { |s| run_shape(s) }
