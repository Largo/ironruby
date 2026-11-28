# is_a?, kind_of? and instance_of? in hot methods (JitCompiler.EmitTypeTest): one receiver class, two, four, a module
# argument, instance_of?, on self; and `C === x' (JitCompiler.EmitModuleCaseEquality). Every line prints the sum of its results next to its time; it must be CRuby's.
#   ruby Util/perf/ab.rb Util/bench/isa-shapes.rb cruby=ruby "base=env IR_CONFIG=Release ../ironruby/ir.sh" "new=env IR_CONFIG=Release ./ir.sh"
$stdout.sync = true
class IBase; end
class IDerived < IBase; end
class IS
  def isa(x) = x.is_a?(IBase) ? 1 : 0
  def cmp(x) = x.is_a?(Comparable) ? 1 : 0
  def inst(x) = x.instance_of?(IBase) ? 1 : 0
  def num(x)
    if x.is_a?(Integer) then 1
    elsif x.is_a?(Float) then 2
    else 3
    end
  end
  def me = is_a?(IS) ? 1 : 0
  def eqq(x) = IBase === x ? 1 : 0
  def eqq_num(x)
    if Integer === x then 1
    elsif Float === x then 2
    else 3
    end
  end
end
def cpu; Process.clock_gettime(Process::CLOCK_THREAD_CPUTIME_ID); end
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; sum = nil
  7.times do
    t0 = cpu; sum = yield n; d = cpu - t0
    best = d if d < best
  end
  printf "%-16s %9.1f ns  %d\n", name, best * 1e9 / n, sum
end
o = IS.new
d = IDerived.new; b = IBase.new
two = [d, b]
four = [d, 1, "s", nil]
mixed = [1, 1.5, "s", 2]
N = (ENV["N"] || 1_000_000).to_i
t("mono", N)      { |n| i = 0; r = 0; while i < n; r += o.isa(d); i += 1; end; r }
t("poly2", N)     { |n| i = 0; r = 0; while i < n; r += o.isa(two[i & 1]); i += 1; end; r }
t("poly4", N)     { |n| i = 0; r = 0; while i < n; r += o.isa(four[i & 3]); i += 1; end; r }
t("module", N)    { |n| i = 0; r = 0; while i < n; r += o.cmp(i); i += 1; end; r }
t("instance_of", N) { |n| i = 0; r = 0; while i < n; r += o.inst(b); i += 1; end; r }
t("num", N)       { |n| i = 0; r = 0; while i < n; r += o.num(mixed[i & 3]); i += 1; end; r }
t("self", N)      { |n| i = 0; r = 0; while i < n; r += o.me; i += 1; end; r }
t("eqq", N)       { |n| i = 0; r = 0; while i < n; r += o.eqq(four[i & 3]); i += 1; end; r }
t("eqq_num", N)   { |n| i = 0; r = 0; while i < n; r += o.eqq_num(mixed[i & 3]); i += 1; end; r }
