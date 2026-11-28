# The fixed cost of calling an iterator with a literal block (gap 13, round thirty-seven, perf/iter3): ns per call of a
# method that calls the iterator once over 1, 2 or 4 elements; the caller is a `while' loop.
#
#   ruby Util/perf/ab.rb Util/bench/iter-shapes.rb cruby=ruby "base=env IR_CONFIG=Release ../ironruby/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#
# each/times/map: Array#each, Integer#times, Array#map with `{ |x| s += x }' (map: `{ |x| x + 1 }'); call: the same
# method with no iterator (`s += a[0]'), the floor a method call costs; empty: each with `{ }'.
# nb0, nbp, nbl: a light method that ignores its block called with none, with a stored Proc (`&P') and with a literal
# `{ }'; eachp, eachl: Array#each over one element with `&P' and with `{ }'. A literal costs its caller what a stored
# Proc does not: the method's scope made (or taken parked) and the Proc made over it (round thirty-seven, perf/iter3).
# e1, ep, ept: `a.each { |x| }', `a.each(&PX)', and ep with a literal in a branch never taken (perf/stagea, perf/literalcode).
# nbx, nby, nbz: a body that has a literal (never evaluated here) calling with a stored Proc, without a block, and not at
# all: in such a body a call with a block takes the full site with the scope made on demand (perf/lazyproc,
# Docs/light-frame-literal-blocks.md).
$stdout.sync = true

P = proc { }
PX = proc { |x| }
class O
  def nb; 1; end
  def ny; yield; end
  def nyl(a) = ny { 1 }
  def nb0(a) = nb
  def nbp(a) = nb(&P)
  def nbl(a) = nb { }
  def eachp(a) = a.each(&P).size
  def eachl(a) = a.each { }.size
  def nbx(a); return nb(&P) if a; nb { }; end
  def nby(a); return nb if a; nb { }; end
  def nbz(a); return 1 if a; nb { }; end
  def each(a); s = 0; a.each { |x| s += x }; s; end
  def times(k); s = 0; k.times { |j| s += j }; s; end
  def map(a) = a.map { |x| x + 1 }.size
  def empty(a); a.each { }; 1; end
  def call(a); s = 0; s += a[0]; s; end
  def e1(a); a.each { |x| }; 1; end
  def ep(a); a.each(&PX); 1; end
  def ept(a); return a.each { |y| } if a.nil?; a.each(&PX); 1; end
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
o = O.new
N = (ENV['N'] || 1_000_000).to_i
a1 = [1]; a2 = [1, 2]; a4 = [1, 2, 3, 4]

t("call", N)   { |n| i = 0; s = 0; while i < n; s += o.call(a1); i += 1; end; s }
t("empty1", N) { |n| i = 0; s = 0; while i < n; s += o.empty(a1); i += 1; end; s }
t("each1", N)  { |n| i = 0; s = 0; while i < n; s += o.each(a1); i += 1; end; s }
t("each2", N)  { |n| i = 0; s = 0; while i < n; s += o.each(a2); i += 1; end; s }
t("each4", N)  { |n| i = 0; s = 0; while i < n; s += o.each(a4); i += 1; end; s }
t("times1", N) { |n| i = 0; s = 0; while i < n; s += o.times(1); i += 1; end; s }
t("times2", N) { |n| i = 0; s = 0; while i < n; s += o.times(2); i += 1; end; s }
t("times4", N) { |n| i = 0; s = 0; while i < n; s += o.times(4); i += 1; end; s }
t("map1", N)   { |n| i = 0; s = 0; while i < n; s += o.map(a1); i += 1; end; s }
t("map2", N)   { |n| i = 0; s = 0; while i < n; s += o.map(a2); i += 1; end; s }
t("map4", N)   { |n| i = 0; s = 0; while i < n; s += o.map(a4); i += 1; end; s }
t("nb0", N)    { |n| i = 0; s = 0; while i < n; s += o.nb0(a1); i += 1; end; s }
t("nbp", N)    { |n| i = 0; s = 0; while i < n; s += o.nbp(a1); i += 1; end; s }
t("nbl", N)    { |n| i = 0; s = 0; while i < n; s += o.nbl(a1); i += 1; end; s }
t("eachp", N)  { |n| i = 0; s = 0; while i < n; s += o.eachp(a1); i += 1; end; s }
t("eachl", N)  { |n| i = 0; s = 0; while i < n; s += o.eachl(a1); i += 1; end; s }
t("nbx", N)    { |n| i = 0; s = 0; while i < n; s += o.nbx(a1); i += 1; end; s }
t("nby", N)    { |n| i = 0; s = 0; while i < n; s += o.nby(a1); i += 1; end; s }
t("nbz", N)    { |n| i = 0; s = 0; while i < n; s += o.nbz(a1); i += 1; end; s }
t("e1", N)     { |n| i = 0; s = 0; while i < n; s += o.e1(a1); i += 1; end; s }
t("ep", N)     { |n| i = 0; s = 0; while i < n; s += o.ep(a1); i += 1; end; s }
t("ept", N)    { |n| i = 0; s = 0; while i < n; s += o.ept(a1); i += 1; end; s }
t("nyl", N)    { |n| i = 0; s = 0; while i < n; s += o.nyl(a1); i += 1; end; s }
