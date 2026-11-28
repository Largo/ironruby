# Instance variable writes inside loops (perf/loopivar): `while' loops that write self's variables, which the
# OSR tier outlines with IR_LOOP_IVAR_WRITES=1 (JitCompiler.EmitLoopIVarWrite; off by default), and
# block loops that do, which it still does not (times_count, each_last: controls with while_plain). Format of
# Util/perf/ab.rb: `<shape> <ns> ns <checksum>` per shape, ns per iteration of the loop; shape names as
# arguments, N from the environment. BYTES=1 (IronRuby only): bytes allocated per
# iteration instead. INSTR=1 (ab.rb -i): instructions per iteration as well.
#
#   ruby Util/perf/ab.rb -r 5 Util/bench/loopivar-shapes.rb cruby=ruby \
#        "base=env IR_CONFIG=Release ../base/ir.sh" "off=env IR_CONFIG=Release ./ir.sh" \
#        "new=env IR_LOOP_IVAR_WRITES=1 IR_CONFIG=Release ./ir.sh"
#
#   while_count   `@n += 1' past the small-integer cache in a `while'
#   while_small   `@n = (@n + 1) & 511' (cached boxes)
#   while_float   `@f += 0.5'
#   while_two     `@a = i; @b = @a + @b' (a read of a variable the iteration wrote)
#   times_count   `n.times { @n += 1 }'
#   each_last     `arr.each { |x| @last = x }'
#   while_plain   a `while' with no instance variable write: the control
$stdout.sync = true
N = (ENV['N'] || 1_000_000).to_i
BYTES = ENV['BYTES'] == '1'
INSTR = ENV['INSTR'] && (require_relative '../perf/instructions'; Instructions.open || warn('no instruction counter (perf_event_open)'))

class L
  def initialize; @n = 5000; @f = 0.0; @a = 0; @b = 0; @last = nil; end
  def while_count(n) = (i = 0; while i < n; @n += 1; i += 1; end; @n)
  def while_small(n) = (i = 0; while i < n; @n = (@n + 1) & 511; i += 1; end; @n)
  def while_float(n) = (i = 0; while i < n; @f += 0.5; i += 1; end; @f)
  def while_two(n) = (i = 0; while i < n; @a = i; @b = (@a + @b) & 65535; i += 1; end; @b)
  def times_count(n) = (n.times { @n += 1 }; @n)
  def each_last(arr) = (arr.each { |x| @last = x }; @last.size)
  def while_plain(n) = (i = 0; s = 0; while i < n; s = (s + i) & 65535; i += 1; end; s)
end
ARR = (%w[alpha beta gamma delta] * (N / 4 + 1)).first(N).freeze
SHAPES = %w[while_count while_small while_float while_two times_count each_last while_plain]

def run_once(o, name)
  name == 'each_last' ? o.each_last(ARR) : o.send(name, N)
end

def run_shape(name)
  best = nil
  fewest = nil
  r = nil
  # the loop hot before anything is timed
  3.times { run_once(L.new, name) }
  6.times do
    o = L.new
    c0 = INSTR && INSTR.read
    t = BYTES ? System::GC.get_total_allocated_bytes(true) : Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    r = run_once(o, name)
    d = BYTES ? System::GC.get_total_allocated_bytes(true) - t : Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    c1 = INSTR && INSTR.read
    fewest = c1 - c0 if c0 && c1 && (fewest.nil? || c1 - c0 < fewest)
    best = d if best.nil? || d < best
  end
  r = r.round(6) if r.is_a?(Float)
  if BYTES
    puts format('%s %.1f B %s', name, best.to_f / N, r)
  else
    puts format('%s %.1f ns %s%s', name, best * 1e9 / N, r, fewest ? format('  %.1f instr', fewest.to_f / N) : '')
  end
end

(ARGV.empty? ? SHAPES : ARGV).each { |s| run_shape(s) }
