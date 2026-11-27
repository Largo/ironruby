# What copying a String costs (perf/strcow): a dup, a `+` and slices of short and long strings, held as characters
# (a string that was appended to), as an immutable CLR string (a literal) and frozen. ns per operation, the caller a
# `while' loop; with INSTR=1 (Util/perf/ab.rb -i, Util/perf/callgrind/count.sh) instructions too. plain, size and
# bytesize are controls.
$stdout.sync = true
def plain(a) = a
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
  printf "%-14s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format('  %.1f instr', fewest.to_f / n) : ''
end
N = (ENV['N'] || 200_000).to_i
SHORT = ("a" * 10).tap { |s| s << "b" }
LONG = ("a" * 10_000).tap { |s| s << "b" }
LIT = "a" * 10_001
FROZEN = ("a" * 10_001).freeze
L = "abc".dup
R = "def".dup

t("dup_short", N) { |n| i = 0; c = 0; while i < n; c += SHORT.dup.bytesize + i; i += 1; end; c }
t("dup_long", N) { |n| i = 0; c = 0; while i < n; c += LONG.dup.bytesize + i; i += 1; end; c }
t("plus", N) { |n| i = 0; c = 0; while i < n; c += (L + R).bytesize + i; i += 1; end; c }
t("plus_long", N / 10) { |n| i = 0; c = 0; while i < n; c += (LONG + R).bytesize + i; i += 1; end; c }
t("slice_short", N) { |n| i = 0; c = 0; while i < n; c += SHORT[1, 5].bytesize + i; i += 1; end; c }
t("slice_mid", N / 10) { |n| i = 0; c = 0; while i < n; c += LONG[1, 5000].bytesize + i; i += 1; end; c }
t("slice_tail", N / 10) { |n| i = 0; c = 0; while i < n; c += LONG[5000..].bytesize + i; i += 1; end; c }
t("slice_lit", N / 10) { |n| i = 0; c = 0; while i < n; c += LIT[1, 5000].bytesize + i; i += 1; end; c }
t("slice_frozen", N / 10) { |n| i = 0; c = 0; while i < n; c += FROZEN[1, 5000].bytesize + i; i += 1; end; c }
t("size", N) { |n| i = 0; c = 0; while i < n; c += LONG.size + i; i += 1; end; c }
t("plain", N) { |n| i = 0; c = 0; while i < n; c += plain(i); i += 1; end; c }
