# What a method with keyword parameters costs, light frame or not (MethodDefinition.IsLightFrameCandidate,
# IR_LIGHT_KEYWORDS): ns per call of the method, the caller a `while' loop. The switch is the other arm:
#
#   ruby Util/perf/ab.rb -i Util/bench/keyword-shapes.rb "off=env IR_LIGHT_KEYWORDS=0 IR_CONFIG=Release ./ir.sh" \
#        "on=env IR_CONFIG=Release ./ir.sh"
#
# Each body asks is_a?, which the method JIT's screen refuses, as it refuses most methods of a program (Util/perf/census.rb):
# so the method runs with its frame, a light one or one with a scope. kreq is a required keyword; kopt and kabsent an
# optional one, given and left out; ktwo two keywords; kpos keywords after two positionals; kafter a keyword after an
# optional positional; kbody a keyword method with a small body; kdefault a default that calls a method; krest a
# **rest. kjit is a keyword method the method JIT specializes, frame or not. plain (a positional), popt (an optional
# positional) and yield1 (a yield) have no keywords and are the controls.
$stdout.sync = true

class H
  def kreq(k:) = k.is_a?(Integer) ? k : 0
  def kopt(k: 1) = k.is_a?(Integer) ? k : 0
  def ktwo(k:, j: 2) = k.is_a?(Integer) ? k + j : 0
  def kpos(a, b, k: 1) = a.is_a?(Integer) ? a + b + k : 0
  def kafter(a, b = 2, k: 1) = a.is_a?(Integer) ? a + b + k : 0
  def kbody(x, scale: 2)
    y = x * scale
    z = y + 1
    z.is_a?(Integer) ? z - x : 0
  end
  def kdefault(k: base) = k.is_a?(Integer) ? k : 0
  def base = 1
  def krest(k: 1, **o) = k + o.size
  def kjit(k:) = k

  def plain(a) = a.is_a?(Integer) ? a : 0
  def popt(a, b = 2) = a.is_a?(Integer) ? a + b : 0
  def yield1(a) = yield(a)
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

t("kreq", N) { |n| i = 0; c = 0; while i < n; c += h.kreq(k: i); i += 1; end; c }
t("kopt", N) { |n| i = 0; c = 0; while i < n; c += h.kopt(k: i); i += 1; end; c }
t("kabsent", N) { |n| i = 0; c = 0; while i < n; c += h.kopt; i += 1; end; c }
t("ktwo", N) { |n| i = 0; c = 0; while i < n; c += h.ktwo(k: i, j: 3); i += 1; end; c }
t("kpos", N) { |n| i = 0; c = 0; while i < n; c += h.kpos(i, 2, k: 3); i += 1; end; c }
t("kafter", N) { |n| i = 0; c = 0; while i < n; c += h.kafter(i, k: 3); i += 1; end; c }
t("kbody", N) { |n| i = 0; c = 0; while i < n; c += h.kbody(i, scale: 3); i += 1; end; c }
t("kdefault", N) { |n| i = 0; c = 0; while i < n; c += h.kdefault; i += 1; end; c }
t("krest", N) { |n| i = 0; c = 0; while i < n; c += h.krest(k: i, z: 2); i += 1; end; c }
t("kjit", N) { |n| i = 0; c = 0; while i < n; c += h.kjit(k: i); i += 1; end; c }
t("plain", N) { |n| i = 0; c = 0; while i < n; c += h.plain(i); i += 1; end; c }
t("popt", N) { |n| i = 0; c = 0; while i < n; c += h.popt(i); i += 1; end; c }
t("yield1", N) { |n| i = 0; c = 0; while i < n; c += h.yield1(i) { |x| x }; i += 1; end; c }
