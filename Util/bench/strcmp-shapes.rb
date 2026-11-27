# A one-character String compared with a one-character literal, the way AWFY Json's parser does it
# (ledger gap 7): `@current == '"'`, `'"' == @current`, `@current != '"'`, `case @current when ...`,
# `@input[@index] == ...`, in a method the method JIT compiles (jit_*) and in one it declines for
# a `raise` (the rest, as Json's read_string_internal, read_value, read_char are). ns per call of a
# method on H, the caller a `while` loop; the sum of the results is the checksum.
#
#   ruby Util/perf/ab.rb Util/bench/strcmp-shapes.rb cruby=ruby "base=env IR_CONFIG=Release ./ir.sh" \
#        "new=env IR_CONFIG=Release ../wt-json/ir.sh" -- eq ne lit_eq kase ws
#   ruby Util/bench/strcmp-shapes.rb --list
$stdout.sync = true

class H
  def initialize
    @s = 'ab"c d1{-' * 20
    @i = 0
    @c = @s[2]
    @n = nil
  end

  def jit_eq = (@c == '"') ? 1 : 0
  def eq; raise "never" if @n; (@c == '"') ? 1 : 0; end
  def ne; raise "never" if @n; (@c != '"') ? 1 : 0; end
  def lit_eq; raise "never" if @n; ('"' == @c) ? 1 : 0; end
  def kase
    raise "never" if @n
    case @c
    when 'n' then 1
    when 't' then 2
    when '{' then 3
    when '-', '0', '1', '2' then 4
    when '"' then 5
    else 6
    end
  end
  def ws; raise "never" if @n; (' ' == @c || "\t" == @c || "\n" == @c || "\r" == @c) ? 1 : 0; end
  def idx_eq; raise "never" if @n; @i = (@i + 1) % 180; (@s[@i] == '"') ? 1 : 0; end
  def idx; raise "never" if @n; @i = (@i + 1) % 180; @s[@i].size; end
  def plain; raise "never" if @n; @c.size; end
end

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
INSTR = ENV["INSTR"] && (require_relative "../perf/instructions"; Instructions.open || warn("no instruction counter (perf_event_open)"))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; fewest = nil; counted = !!INSTR; sum = nil
  7.times do
    c0 = INSTR && INSTR.read; t0 = cpu; sum = yield n; d = cpu - t0; c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end
h = H.new
N = (ENV["N"] || 1_000_000).to_i
t("jit_eq", N) { |n| i = 0; s = 0; while i < n; s += h.jit_eq; i += 1; end; s }
t("eq", N) { |n| i = 0; s = 0; while i < n; s += h.eq; i += 1; end; s }
t("ne", N) { |n| i = 0; s = 0; while i < n; s += h.ne; i += 1; end; s }
t("lit_eq", N) { |n| i = 0; s = 0; while i < n; s += h.lit_eq; i += 1; end; s }
t("kase", N) { |n| i = 0; s = 0; while i < n; s += h.kase; i += 1; end; s }
t("ws", N) { |n| i = 0; s = 0; while i < n; s += h.ws; i += 1; end; s }
t("idx_eq", N) { |n| i = 0; s = 0; while i < n; s += h.idx_eq; i += 1; end; s }
t("idx", N) { |n| i = 0; s = 0; while i < n; s += h.idx; i += 1; end; s }
t("plain", N) { |n| i = 0; s = 0; while i < n; s += h.plain; i += 1; end; s }
