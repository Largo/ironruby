# What `s + "lit"' and `s << "lit"' cost in a method the method JIT can take (JitCompiler.EmitStringLiteralConcat):
# ns per call of the method, the caller a `while' loop. plus `s + ":"', plus2 `s + ":" + "x"', append `buf << "x"' (the
# buffer cleared every 1000 calls), userinfo `@user + ":"' on an instance variable; eq `s == "lit"' is the control (the
# typed body took it already).
#
#   ruby Util/perf/ab.rb -r 6 Util/bench/jit-strop-shapes.rb "base=env IR_CONFIG=Release ../ironruby/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#   ruby Util/perf/ab.rb -i -r 1 -n 20000 Util/bench/jit-strop-shapes.rb \
#        "base=env IR_CONFIG=Release Util/perf/callgrind/count.sh ../ironruby" "new=env IR_CONFIG=Release Util/perf/callgrind/count.sh ."
#
# Every line prints the sum of its results next to its time; it must be CRuby's.
$stdout.sync = true

class SoShapes
  def initialize; @user = +"user"; end
  def plus(s) = s + ":"
  def plus2(s) = s + ":" + "x"
  def append(s) = s << "x"
  def userinfo = @user + ":"
  def eq(s) = s == "lit"
end

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
# INSTR=1 (Util/perf/ab.rb -i) adds a column: the instructions of a call, from the least of the
# seven sections as the time is. See Util/perf/instructions.rb.
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
s = SoShapes.new
N = (ENV["N"] || 1_000_000).to_i
str = +"abc"
t("plus", N)     { |n| i = 0; r = 0; while i < n; r += s.plus(str).size; i += 1; end; r }
t("plus2", N)    { |n| i = 0; r = 0; while i < n; r += s.plus2(str).size; i += 1; end; r }
t("append", N)   { |n| i = 0; r = 0; b = +""; while i < n; r += s.append(b).size; b.clear if i % 1000 == 999; i += 1; end; r }
t("userinfo", N) { |n| i = 0; r = 0; while i < n; r += s.userinfo.size; i += 1; end; r }
t("eq", N)       { |n| i = 0; r = 0; while i < n; r += 1 if s.eq(str); i += 1; end; r }
