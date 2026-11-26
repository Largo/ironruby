# Calls of methods made by define_method (gap 23 of Util/perf/LEDGER.md), against a `def' of the same
# body: no, one and two arguments, an instance variable read, a call to another method of self, and a
# body over a captured local: `x + captured', the per-name hash reader and writer stdlib and gems define
# (`%i[a b].each { |name| define_method(name) { @h[name] } }'), a captured constant value, a captured factor.
# `<shape> <ns> ns <checksum>' per line, for Util/perf/ab.rb; `ruby dmeth-shapes.rb NAME...' runs only the shapes
# named; N is the number of calls a section makes (ab.rb -n); INSTR=1 (ab.rb -i, or Util/perf/callgrind/count.sh)
# adds the instructions of a call, from the least of the sections as the time is.
INSTR = ENV['INSTR'] && (require_relative '../perf/instructions'; Instructions.open || warn('no instruction counter (perf_event_open)'))
def cpu
  File.read("/proc/thread-self/schedstat").to_i / 1e9
rescue SystemCallError
  Process.clock_gettime(Process::CLOCK_MONOTONIC)
end
def t(name, n = 2_000_000)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  n = ENV['N'].to_i if ENV['N']
  best = 1e9; r = nil; fewest = nil; counted = !!INSTR
  5.times do
    c0 = INSTR && INSTR.read
    t0 = cpu
    r = yield n
    d = cpu - t0
    c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1
      fewest = c1 - c0 if fewest.nil? || c1 - c0 < fewest
    else
      counted = false
    end
  end
  printf "%-14s %8.1f ns %s%s\n", name, best * 1e9 / n, r, counted ? format('  %.1f instr', fewest.to_f / n) : ''
end
class B
  def initialize = (@a = 3)
  def helper(x) = x * 2
  define_method(:bm0) { 1 }
  define_method(:bm1) { |x| x + 1 }
  define_method(:bm2) { |x, y| x + y }
  define_method(:bmivar) { @a }
  define_method(:bmcall) { |x| helper(x) + 1 }
  captured = 1
  define_method(:bmcapt) { |x| x + captured }
  def initialize_h = (@h = { a: 1 })
  %i[a].each do |name|
    define_method(:"bmh_#{name}") { @h[name] }
    define_method(:"bmh_#{name}=") { |v| @h[name] = v }
  end
  code = 404
  define_method(:bmconst) { code }
  def self.scaled(name, k) = define_method(name) { |x| x * k }
  scaled(:bmscaled, 3)
  def dmh_a = @h[:a]
  def dmh_a=(v)
    @h[:a] = v
  end
  def dmconst = 404
  def dmscaled(x) = x * 3
  def dm0 = 1
  def dm1(x) = x + 1
  def dm2(x, y) = x + y
  def dmivar = @a
  def dmcall(x) = helper(x) + 1
end
o = B.new
o.initialize_h
t("bm0")     { |n| i = 0; c = 0; while i < n; c += o.bm0; i += 1; end; c }
t("dm0")     { |n| i = 0; c = 0; while i < n; c += o.dm0; i += 1; end; c }
t("bm1")     { |n| i = 0; c = 0; while i < n; c = o.bm1(c); i += 1; end; c }
t("dm1")     { |n| i = 0; c = 0; while i < n; c = o.dm1(c); i += 1; end; c }
t("bm2")     { |n| i = 0; c = 0; while i < n; c += o.bm2(i, 1); i += 1; end; c }
t("dm2")     { |n| i = 0; c = 0; while i < n; c += o.dm2(i, 1); i += 1; end; c }
t("bmivar")  { |n| i = 0; c = 0; while i < n; c += o.bmivar; i += 1; end; c }
t("dmivar")  { |n| i = 0; c = 0; while i < n; c += o.dmivar; i += 1; end; c }
t("bmcall")  { |n| i = 0; c = 0; while i < n; c += o.bmcall(i); i += 1; end; c }
t("dmcall")  { |n| i = 0; c = 0; while i < n; c += o.dmcall(i); i += 1; end; c }
t("bmcapt")  { |n| i = 0; c = 0; while i < n; c = o.bmcapt(c); i += 1; end; c }
t("bmh")     { |n| i = 0; c = 0; while i < n; c += o.bmh_a; i += 1; end; c }
t("dmh")     { |n| i = 0; c = 0; while i < n; c += o.dmh_a; i += 1; end; c }
t("bmhset")  { |n| i = 0; while i < n; o.bmh_a = i; i += 1; end; o.dmh_a }
t("dmhset")  { |n| i = 0; while i < n; o.dmh_a = i; i += 1; end; o.dmh_a }
t("bmconst") { |n| i = 0; c = 0; while i < n; c += o.bmconst; i += 1; end; c }
t("dmconst") { |n| i = 0; c = 0; while i < n; c += o.dmconst; i += 1; end; c }
t("bmscaled") { |n| i = 0; c = 0; while i < n; c += o.bmscaled(i); i += 1; end; c }
t("dmscaled") { |n| i = 0; c = 0; while i < n; c += o.dmscaled(i); i += 1; end; c }
