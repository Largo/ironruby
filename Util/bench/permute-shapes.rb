# Shapes for Permute (Util/perf/LEDGER.md gap 4): a call of Integer#downto with a literal block, as AWFY's
# Permute#permute makes one on most of its calls, against the same loop as a `while' and two other calls with a
# literal block, in the format Util/perf/ab.rb reads (`<shape> <ns> ns <checksum>`; N from the environment; INSTR=1
# or Util/perf/callgrind/count.sh for instructions per call).
#
#   ruby Util/perf/ab.rb -i -r 1 -c none -n 20000 Util/bench/permute-shapes.rb \
#        "base=Util/perf/callgrind/count.sh ." "new=Util/perf/callgrind/count.sh ../wt-NAME"
$stdout.sync = true

class PermShape
  attr_reader :count
  def initialize; @count = 0; @v = Array.new(6, 0); end

  # AWFY's Permute#permute and #swap
  def permute(n)
    @count += 1
    if n != 0
      n1 = n - 1
      permute(n1)
      n1.downto(0) do |i|
        swap(n1, i)
        permute(n1)
        swap(n1, i)
      end
    end
  end

  # the same with the block written as a `while'
  def permute_while(n)
    @count += 1
    if n != 0
      n1 = n - 1
      permute_while(n1)
      i = n1
      while i >= 0
        swap(n1, i)
        permute_while(n1)
        swap(n1, i)
        i -= 1
      end
    end
  end

  def swap(i, j)
    tmp = @v[i]
    @v[i] = @v[j]
    @v[j] = tmp
  end

  def dt_call(n); k = n; n.downto(0) { |i| bump(k) }; end
  def tm_call(n); k = n; (n + 1).times { |i| bump(k) }; end
  def wh_call(n); k = n; i = n; while i >= 0; bump(k); i -= 1; end; end
  def bump(k); @count += 1; end
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

o = PermShape.new
N = (ENV["N"] || 1_000_000).to_i
# a call of permute(4): 65 calls of permute, 40 of them with a downto block
t("permute", N / 50) { |n| i = 0; while i < n; o.permute(4); i += 1; end; o.count }
t("permute_while", N / 50) { |n| i = 0; while i < n; o.permute_while(4); i += 1; end; o.count }
t("dt_call", N) { |n| i = 0; while i < n; o.dt_call(1); i += 1; end; o.count }
t("tm_call", N) { |n| i = 0; while i < n; o.tm_call(1); i += 1; end; o.count }
t("wh_call", N) { |n| i = 0; while i < n; o.wh_call(1); i += 1; end; o.count }
