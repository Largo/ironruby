# Methods whose block literals have literals nested in them, for IR_NESTED_PROCS (Util/perf/LEDGER.md, gap 2;
# RubyOps.KeepEnclosingProcs): such a literal reuses its Proc, and its method parks its scope, on the calls that make
# no nested Proc. Format of Util/perf/ab.rb: `<shape> <ns> ns <checksum>` per shape (`  <n> instr' with INSTR=1);
# shape names as arguments, N from the environment. BYTES=1 (IronRuby only) prints bytes per call instead of ns.
#
#   ruby Util/perf/ab.rb -i Util/bench/nested-proc-shapes.rb \
#        "off=env IR_NESTED_PROCS=0 Util/perf/callgrind/count.sh ../wt" "on=Util/perf/callgrind/count.sh ../wt"
#
#   advance   NBody's `advance' over five bodies: the nested `Range#each' runs in place in the outlined
#             `each_index' loop, so no call makes a nested Proc - the shape the change is for
#   nest      the same nesting over Integers, a light body
#   always    a nested literal whose Proc every call makes (`Array#each' inside `Hash#each', which is not
#             outlined): the slot tries 64 calls, gives up, and the nested Proc clears the reuse - the cost of
#             the change where it cannot help
#   lambda    a nested lambda made on every call
#   worker    Richards' worker: a stored Proc called with Proc#call, `times' nested in it
#   flat      one literal, no nesting: a control, which the change does not touch
$stdout.sync = true
N = (ENV['N'] || 200_000).to_i
BYTES = ENV['BYTES'] == '1'

class Body
  attr_accessor :x, :y, :vx, :vy, :mass
  def initialize(x, y, vx, vy, mass); @x = x; @y = y; @vx = vx; @vy = vy; @mass = mass; end
end

class Sys
  def initialize
    @bodies = Array.new(5) { |i| Body.new(i * 1.5, i * 0.5 - 1.0, 0.01 * i, -0.02 * i, 1.0 + i) }
    @ints = [1, 2, 3, 4, 5]
    @h = { a: [1, 2], b: [3] }
  end

  def advance(dt)
    @bodies.each_index do |i|
      i_body = @bodies[i]
      ((i + 1)...@bodies.size).each do |j|
        j_body = @bodies[j]
        dx = i_body.x - j_body.x
        dy = i_body.y - j_body.y
        d2 = (dx * dx) + (dy * dy) + 0.01
        mag = dt / (d2 * Math.sqrt(d2))
        i_body.vx -= dx * j_body.mass * mag
        i_body.vy -= dy * j_body.mass * mag
        j_body.vx += dx * i_body.mass * mag
        j_body.vy += dy * i_body.mass * mag
      end
    end
    @bodies.each do |b|
      b.x += dt * b.vx
      b.y += dt * b.vy
    end
  end

  def energy
    e = 0.0
    @bodies.each { |b| e += b.mass * (b.vx * b.vx + b.vy * b.vy) + b.x + b.y }
    e
  end

  def nest
    s = 0
    @ints.each_index do |i|
      a = @ints[i]
      ((i + 1)...@ints.size).each do |j|
        s += a * @ints[j] + j - i + (a & 3) + (j ^ 1) + a - 1
      end
    end
    s
  end

  def always
    s = 0
    @h.each { |_, v| v.each { |x| s += x } }
    s
  end

  def lambda_made
    s = 0
    @ints.each { |x| f = -> { x * 2 }; s += f.call }
    s
  end

  def flat
    s = 0
    @ints.each { |x| s += x }
    s
  end
end

class Worker
  def initialize
    @count = 0
    data = [0] * 4
    @fn = proc do |w|
      4.times do |i|
        @count += 1
        @count = 1 if @count > 26
        data[i] = 65 + @count - w
      end
      data[3]
    end
  end
  def run(w) = @fn.call(w)
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
    b0 = BYTES && System::GC.get_allocated_bytes_for_current_thread
    c0 = INSTR && INSTR.read
    t0 = cpu
    sum = yield n
    d = cpu - t0
    c1 = INSTR && INSTR.read
    d = (System::GC.get_allocated_bytes_for_current_thread - b0) / 1e9 if BYTES
    best = d if d < best
    if c0 && c1
      c = c1 - c0
      fewest = c if fewest.nil? || c < fewest
    else
      counted = false
    end
  end
  sum = sum.round(6) if sum.is_a?(Float)
  printf "%-12s %9.1f %s  %s%s\n", name, best * 1e9 / n, BYTES ? 'B ' : 'ns', sum, counted ? format('  %.1f instr', fewest.to_f / n) : ''
end

t("advance", N) { |n| s = Sys.new; i = 0; while i < n; s.advance(0.01); i += 1; end; s.energy }
t("nest", N) { |n| s = Sys.new; i = 0; c = 0; while i < n; c += s.nest; i += 1; end; c }
t("always", N) { |n| s = Sys.new; i = 0; c = 0; while i < n; c += s.always; i += 1; end; c }
t("lambda", N) { |n| s = Sys.new; i = 0; c = 0; while i < n; c += s.lambda_made; i += 1; end; c }
t("worker", N) { |n| w = Worker.new; i = 0; c = 0; while i < n; c += w.run(i & 1); i += 1; end; c }
t("flat", N) { |n| s = Sys.new; i = 0; c = 0; while i < n; c += s.flat; i += 1; end; c }
