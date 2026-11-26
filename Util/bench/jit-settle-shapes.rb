# What a method the JIT specialized costs once its typed body keeps failing (perf/jitsettle): `x + 1' called with an
# Integer and a Float in turn, as a def and as a define_method method; one whose typed body fails on one call in 1000;
# and bodies that never fail, as controls. `<shape> <ns> ns <checksum>' per line, for Util/perf/ab.rb; `ruby
# jit-settle-shapes.rb NAME...' runs only the shapes named; N is the number of calls a section makes (ab.rb -n);
# INSTR=1 (ab.rb -i, or Util/perf/callgrind/count.sh) adds the instructions of a call.
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
  def mixed(x) = x + 1
  define_method(:bmixed) { |x| x + 1 }
  def rare(x) = x + 1
  def plain(x) = x + 1
  def ivar(x) = @a + x
  def two(x, y) = x * y + 1
  define_method(:bplain) { |x| x + 1 }
end
o = B.new
t("mixed_def")  { |n| i = 0; c = 0; while i < n; c += o.mixed(i.odd? ? i : 0.5).to_i; i += 1; end; c }
t("mixed_dm")   { |n| i = 0; c = 0; while i < n; c += o.bmixed(i.odd? ? i : 0.5).to_i; i += 1; end; c }
t("rare_def")   { |n| i = 0; c = 0; while i < n; c += o.rare(i % 1000 == 999 ? 0.5 : i).to_i; i += 1; end; c }
t("plain_def")  { |n| i = 0; c = 0; while i < n; c += o.plain(i); i += 1; end; c }
t("ivar_def")   { |n| i = 0; c = 0; while i < n; c += o.ivar(i); i += 1; end; c }
t("two_def")    { |n| i = 0; c = 0; while i < n; c += o.two(i, 2); i += 1; end; c }
t("plain_dm")   { |n| i = 0; c = 0; while i < n; c += o.bplain(i); i += 1; end; c }
