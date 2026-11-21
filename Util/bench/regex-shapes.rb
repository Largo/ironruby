# What a method with a regex literal costs, light frame or not (MethodDefinition.IsScopeFree, IR_LIGHT_REGEX):
# ns per call of the method, the caller a `while` loop. The switch is the third arm:
#
#   ruby Util/perf/ab.rb -i Util/bench/regex-shapes.rb "off=env IR_LIGHT_REGEX=0 IR_CONFIG=Release ./ir.sh" \
#        "on=env IR_CONFIG=Release ./ir.sh" "all=env IR_LIGHT_REGEX=all IR_CONFIG=Release ./ir.sh"
#
# matchp, rmatchp, lit, space, uri and pathname are light with the switch on (their callees leave $~ alone, or the
# literal goes to a method of the program); gsub, split, kase and eqt only with IR_LIGHT_REGEX=all (their callees
# store the caller's $~, so a light frame makes its scope on the call and calls again); size and tilde are the same
# in every arm (a light frame without a regex; a method that reads $~).
require 'uri'
require 'pathname'
$stdout.sync = true

class H
  def matchp(s) = s.match?(/b/) ? 1 : 0
  def rmatchp(s) = /b/.match?(s) ? 2 : 0
  def lit(s) = /b/.options + s.size
  # Reline::Unicode.space_character?
  def space(s) = s.match?(/\s/) ? 1 : 0
  def uri(s) = URI.encode_www_form_component(s).size
  def pathname(p) = (p + "c").to_s.size

  def gsub(s) = s.gsub(/b/, "x").size
  def split(s) = s.split(/,/).size
  def kase(s)
    case s
    when /z/ then 1
    when /b/ then 2
    else 3
    end
  end
  def eqt(s) = (s =~ /d/) || 0

  def size(s) = s.size
  def tilde(s); s =~ /d/; $~ ? 1 : 0; end
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
s = "abc,def"
w = "a b&c"
pn = Pathname.new("a/b")

t("matchp", N) { |n| i = 0; c = 0; while i < n; c += h.matchp(s); i += 1; end; c }
t("rmatchp", N) { |n| i = 0; c = 0; while i < n; c += h.rmatchp(s); i += 1; end; c }
t("lit", N) { |n| i = 0; c = 0; while i < n; c += h.lit(s); i += 1; end; c }
t("space", N) { |n| i = 0; c = 0; while i < n; c += h.space(w); i += 1; end; c }
t("uri", N / 5) { |n| i = 0; c = 0; while i < n; c += h.uri(w); i += 1; end; c }
t("pathname", N / 5) { |n| i = 0; c = 0; while i < n; c += h.pathname(pn); i += 1; end; c }
t("gsub", N) { |n| i = 0; c = 0; while i < n; c += h.gsub(s); i += 1; end; c }
t("split", N) { |n| i = 0; c = 0; while i < n; c += h.split(s); i += 1; end; c }
t("kase", N) { |n| i = 0; c = 0; while i < n; c += h.kase(s); i += 1; end; c }
t("eqt", N) { |n| i = 0; c = 0; while i < n; c += h.eqt(s); i += 1; end; c }
t("size", N) { |n| i = 0; c = 0; while i < n; c += h.size(s); i += 1; end; c }
t("tilde", N) { |n| i = 0; c = 0; while i < n; c += h.tilde(s); i += 1; end; c }
