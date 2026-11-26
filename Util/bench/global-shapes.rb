# What a method that reads or writes a global variable costs, light frame or not (MethodDefinition.IsLightGlobal,
# IR_LIGHT_GLOBALS): ns per call of the method, the caller a `while' loop. The switch is the third arm:
#
#   ruby Util/perf/ab.rb -i Util/bench/global-shapes.rb "off=env IR_LIGHT_GLOBALS=0 IR_CONFIG=Release ./ir.sh" \
#        "on=env IR_CONFIG=Release ./ir.sh"
#
# read, write, opasgn, stdout, verbose, plus, dflt and defd are light with the switch on; puts and lines are the prelude's
# Kernel#puts (it reads $stdout) and String#lines (its helper reads $/); alias reads English.rb's kind of alias of $~, which
# makes the frame's scope on demand on every call (the price of the fallback); ivar and tilde are the same in both arms
# (a light frame without a global; a method that names $~).
$stdout.sync = true

$g = 1
$gw = 0
alias $g_last_match $~

class H
  def read = $g
  def write(v) = $gw = v
  def opasgn = $gw += 1
  def stdout = $stdout.equal?(STDOUT) ? 1 : 0
  def verbose = $VERBOSE ? 1 : 2
  def plus(x) = x + $g
  def dflt(sep = $/) = sep.size
  def defd = defined?($g) ? 1 : 0
  def alias_read = $g_last_match ? 1 : 0

  def ivar = @x ? 1 : 2
  def tilde; $~ ? 1 : 0; end
end

class NullOut
  def write(*a) = a.size
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
h = H.new
N = (ENV['N'] || 500_000).to_i
s = "a\nb\n"

t("read", N) { |n| i = 0; c = 0; while i < n; c += h.read; i += 1; end; c }
t("write", N) { |n| i = 0; c = 0; while i < n; c += h.write(1); i += 1; end; c }
t("opasgn", N) { |n| $gw = 0; i = 0; while i < n; h.opasgn; i += 1; end; $gw }
t("stdout", N) { |n| i = 0; c = 0; while i < n; c += h.stdout; i += 1; end; c }
t("verbose", N) { |n| i = 0; c = 0; while i < n; c += h.verbose; i += 1; end; c }
t("plus", N) { |n| i = 0; c = 0; while i < n; c += h.plus(i & 3); i += 1; end; c }
t("dflt", N) { |n| i = 0; c = 0; while i < n; c += h.dflt; i += 1; end; c }
t("defd", N) { |n| i = 0; c = 0; while i < n; c += h.defd; i += 1; end; c }
t("alias", N) { |n| i = 0; c = 0; while i < n; c += h.alias_read; i += 1; end; c }
t("puts", N / 5) do |n|
  old = $stdout
  $stdout = NullOut.new
  begin
    i = 0
    while i < n
      puts i
      i += 1
    end
  ensure
    $stdout = old
  end
  n
end
t("lines", N / 5) { |n| i = 0; c = 0; while i < n; c += s.lines.size; i += 1; end; c }
t("ivar", N) { |n| i = 0; c = 0; while i < n; c += h.ivar; i += 1; end; c }
t("tilde", N) { |n| i = 0; c = 0; while i < n; c += h.tilde; i += 1; end; c }
