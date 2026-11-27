# What a String's representation switch and its writes cost (perf/strswitch): getbyte and bytes on a string held as
# characters (each switches it to bytes), index after setbyte (back to characters), force_encoding there and back, and
# the writes that now check their content is still the string's - <<, insert, setbyte, slice! - with size as a control.
# ns per operation, the caller a `while' loop; with INSTR=1 (Util/perf/callgrind/count.sh) instructions too.
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
MB = ("aé" * 20).freeze
ASC = ("abcdefghij" * 4).freeze

t("getbyte_switch", N) { |n| i = 0; c = 0; while i < n; t = MB.dup; t << "é"; c += t.getbyte(1) + i; i += 1; end; c }
t("bytes_switch", N) { |n| i = 0; c = 0; while i < n; t = MB.dup; t << "é"; c += t.bytes.size + i; i += 1; end; c }
t("index_back", N) { |n| i = 0; c = 0; t = MB.dup; while i < n; t.setbyte(0, 98); c += t.index("é") + i; i += 1; end; c }
t("force_enc", N) { |n| i = 0; c = 0; t = MB.dup; while i < n; t.force_encoding("BINARY"); t.force_encoding("UTF-8"); c += i; i += 1; end; c }
t("append", N) { |n| i = 0; c = 0; t = +""; while i < n; t << "a"; c += i; i += 1; end; c + t.size }
t("insert", N / 10) { |n| i = 0; c = 0; t = ASC.dup; while i < n; t.insert(5, "x"); t.slice!(5, 1); c += i; i += 1; end; c + t.size }
t("setbyte", N) { |n| i = 0; c = 0; t = ASC.b; while i < n; t.setbyte(i & 31, 97); c += i; i += 1; end; c }
t("size", N) { |n| i = 0; c = 0; while i < n; c += MB.size + i; i += 1; end; c }
t("plain", N) { |n| i = 0; c = 0; while i < n; c += plain(i); i += 1; end; c }
