# What a method whose `while' loop has a `break' or `next' costs, light frame or not (MethodDefinition.IsScopeFree,
# IR_LIGHT_BREAK): ns per call of the method, the caller a `while' loop. The switches are the other arms:
#
#   ruby Util/perf/ab.rb -i Util/bench/loop-shapes.rb "off=env IR_LIGHT_BREAK=0 IR_CONFIG=Release ./ir.sh" \
#        "osr0=env IR_LIGHT_OSR=0 IR_CONFIG=Release ./ir.sh" "on=env IR_CONFIG=Release ./ir.sh"
#
# The method JIT refuses a method with a `break', so its loop is fast through OSR: once specialized, the copy is entered
# at every call, and a light frame that made its scope on demand for it (IR_LIGHT_OSR=0) was slower than one with its
# scope made at entry.
#
# brk1, brk4, brk100 and next4 are loops of 1, 4, 100 and 4 iterations that end with a break or skip with next;
# long is one of 10,000 iterations (through OSR); take_while, drop_while, sum_block (Array#sum with a block)
# and rfind are the prelude's; scan is a scanner's loop of calls. begin and memo are `begin ... end' without a rescue
# (IR_LIGHT_BEGIN=0 is their off arm). nobrk4 (a loop without a
# jump, light in both arms) and size (no loop) are the controls.
$stdout.sync = true

class H
  def brk(n)
    i = 0
    while true
      break if i == n
      i += 1
    end
    i
  end

  def nxt(n)
    i = 0
    s = 0
    while i < n
      i += 1
      next if i == 2
      s += i
    end
    s
  end

  def nobrk(n)
    i = 0
    while i < n
      i += 1
    end
    i
  end

  # a scanner's loop: calls in the body, a break on what they answer
  def scan(s)
    pos = 0
    while true
      c = s.getbyte(pos)
      break if c.nil? || c == 32
      pos += 1
    end
    pos
  end

  # `begin ... end' without rescue (IR_LIGHT_BEGIN): a value computed in one, and the memoizing `@x ||= begin ... end'
  def bgn(n)
    x = begin
      y = n + 1
      y * 2
    end
    x + y
  end

  def memo
    @memo ||= begin
      [1, 2].sum
    end
  end

  def size(s) = s.size
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
a = [1, 2, 3]

t("brk1", N) { |n| i = 0; c = 0; while i < n; c += h.brk(1); i += 1; end; c }
t("brk4", N) { |n| i = 0; c = 0; while i < n; c += h.brk(4); i += 1; end; c }
t("brk100", N / 10) { |n| i = 0; c = 0; while i < n; c += h.brk(100); i += 1; end; c }
t("long", [N / 1000, 1].max) { |n| i = 0; c = 0; while i < n; c += h.brk(10_000); i += 1; end; c }
t("next4", N) { |n| i = 0; c = 0; while i < n; c += h.nxt(4); i += 1; end; c }
t("take_while", N) { |n| i = 0; c = 0; while i < n; c += a.take_while { |x| x < 2 }.size; i += 1; end; c }
t("drop_while", N) { |n| i = 0; c = 0; while i < n; c += a.drop_while { |x| x < 2 }.size; i += 1; end; c }
t("sum_block", N) { |n| i = 0; c = 0; while i < n; c += a.sum { |x| x }; i += 1; end; c }
t("rfind", N) { |n| i = 0; c = 0; while i < n; c += a.rfind { |x| x < 3 }; i += 1; end; c }
t("scan", N) { |n| i = 0; c = 0; while i < n; c += h.scan("abcd efg"); i += 1; end; c }
t("begin", N) { |n| i = 0; c = 0; while i < n; c += h.bgn(i); i += 1; end; c }
t("memo", N) { |n| i = 0; c = 0; while i < n; c += h.memo; i += 1; end; c }
t("nobrk4", N) { |n| i = 0; c = 0; while i < n; c += h.nobrk(4); i += 1; end; c }
t("size", N) { |n| i = 0; c = 0; while i < n; c += h.size(a); i += 1; end; c }
