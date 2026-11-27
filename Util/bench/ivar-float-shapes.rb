# Instance variable shapes by the kind of value they hold - Float, Integer, object - for the
# question whether Float instance variables should be stored unboxed (Util/perf/LEDGER.md, gap 2;
# Util/bench/daloze-lessons.md, idea 1). Format of Util/perf/ab.rb: `<shape> <ns> ns <checksum>`
# per shape; shape names as arguments, N from the environment. With BYTES=1 (IronRuby only) each
# line gives bytes allocated per operation instead of ns.
#
#   ruby Util/perf/ab.rb -r 5 Util/bench/ivar-float-shapes.rb cruby=ruby \
#        "base=env IR_CONFIG=Release ../base/ir.sh" "new=env IR_CONFIG=Release ./ir.sh"
$stdout.sync = true
N = (ENV['N'] || 2_000_000).to_i
BYTES = ENV['BYTES'] == '1'
# INSTR=1 (Util/perf/ab.rb -i): instructions per operation as well, the least of the repetitions
INSTR = ENV['INSTR'] && (require_relative '../perf/instructions'; Instructions.open || warn('no instruction counter (perf_event_open)'))

A = Object.new
B = Object.new

class IvBody
  attr_accessor :x, :vx, :m
  def initialize(x, vx, m); @x = x; @vx = vx; @m = m; end
end

class IvF
  def initialize; @x = 0.0; end
  def inc = (@x += 0.1)
  def wloop(n); i = 0; while i < n; @x += 0.1; i += 1; end; @x; end
  def bloop(n) = n.times { @x += 0.1 }
  def x = @x
end
class IvI
  def initialize; @x = 0; end
  def inc = (@x += 1)
  def wloop(n); i = 0; while i < n; @x += 1; i += 1; end; @x; end
  def bloop(n) = n.times { @x += 1 }
  def x = @x
end
class IvO
  def initialize; @x = A; end
  def inc = (@x = @x.equal?(A) ? B : A)
  def wloop(n); i = 0; while i < n; @x = @x.equal?(A) ? B : A; i += 1; end; @x; end
  def bloop(n) = n.times { @x = @x.equal?(A) ? B : A }
  def x = @x
end

KINDS = { 'f' => [IvF, 0.5, 0.25, 0.001], 'i' => [IvI, 5, 3, 1], 'o' => [IvO, A, B, nil] }
SHAPES = %w[meth_f wloop_f bloop_f attr_f read_f meth_i wloop_i bloop_i attr_i read_i meth_o wloop_o bloop_o attr_o read_o]

def body_op(kind, b, c, d, i)
  case kind
  when 'f' then while i < N; b.vx -= d * c.m; i += 1; end; b.vx
  when 'i' then while i < N; b.vx -= d * c.m; i += 1; end; b.vx
  when 'o' then while i < N; b.vx = b.vx.equal?(A) ? c.x : A; i += 1; end; b.vx.equal?(A)
  end
end

def read_op(kind, b, c, i)
  r = 0
  if kind == 'o'
    while i < N; r += 1 if b.x.equal?(c.x); i += 1; end
  else
    while i < N; r += b.x - c.x; i += 1; end
  end
  r
end

def run_shape(name)
  op, kind = name.split('_')
  cls, x, vx, m = KINDS[kind]
  best = nil
  fewest = nil
  r = nil
  6.times do
    o = cls.new
    b = IvBody.new(x, vx, m)
    c = IvBody.new(vx, x, m)
    c0 = INSTR && INSTR.read
    t = BYTES ? System::GC.get_total_allocated_bytes(true) : Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    i = 0
    r = case op
        when 'meth' then while i < N; o.inc; i += 1; end; o.x
        when 'wloop' then o.wloop(N)
        when 'bloop' then o.bloop(N); o.x
        when 'attr' then body_op(kind, b, c, m, i)
        when 'read' then read_op(kind, b, c, i)
        end
    d = BYTES ? System::GC.get_total_allocated_bytes(true) - t : Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    c1 = INSTR && INSTR.read
    fewest = c1 - c0 if c0 && c1 && (fewest.nil? || c1 - c0 < fewest)
    best = d if best.nil? || d < best
  end
  r = r.round(6) if r.is_a?(Float)
  r = r.equal?(A) if r.is_a?(Object) && !r.is_a?(Numeric) && r != true && r != false
  if BYTES
    puts format('%s %.1f B %s', name, best.to_f / N, r)
  else
    puts format('%s %.1f ns %s%s', name, best * 1e9 / N, r, fewest ? format('  %.1f instr', fewest.to_f / N) : '')
  end
end

(ARGV.empty? ? SHAPES : ARGV).each { |s| run_shape(s) }
