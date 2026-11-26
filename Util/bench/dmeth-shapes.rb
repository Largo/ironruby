# Calls of methods made by define_method (gap 23 of Util/perf/LEDGER.md), against a `def' of the same
# body: no, one and two arguments, an instance variable read, a call to another method of self, and a
# body over a captured local (which the method JIT declines). `<shape> <ns> ns <checksum>' per line,
# for Util/perf/ab.rb; `ruby dmeth-shapes.rb NAME...' runs only the shapes named; N scales the counts.
SCALE = (ENV["N"] || "1").to_f
def t(name, n = 2_000_000)
  return unless ARGV.empty? || ARGV.include?(name)
  n = (n * SCALE).to_i
  best = 1e9; r = nil
  5.times { t0 = Process.clock_gettime(Process::CLOCK_MONOTONIC); r = yield n; d = Process.clock_gettime(Process::CLOCK_MONOTONIC) - t0; best = d if d < best }
  printf "%-14s %8.1f ns %s\n", name, best * 1e9 / n, r
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
  def dm0 = 1
  def dm1(x) = x + 1
  def dm2(x, y) = x + y
  def dmivar = @a
  def dmcall(x) = helper(x) + 1
end
o = B.new
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
