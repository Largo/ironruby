# Core methods on small receivers (four elements, a four-character String), ns per call, to put
# next to CRuby: which core methods are far from CRuby, and - with Util/bench/core-inventory.rb's
# WHERE= - whether those are the ones written in Ruby. See truffleruby-lessons.md.
#
#   IR_CONFIG=Release ./ir.sh Util/bench/core-small.rb
#   ruby Util/bench/core-small.rb
#   ... core-small.rb inject sum            # only these
#   ... core-small.rb --list                # the names
#   ruby Util/perf/ab.rb Util/bench/core-small.rb cruby=ruby "base=..." "new=..." -- inject   # two builds, taking turns
#
# Every line prints the sum of its results next to its time; it must be CRuby's.
$stdout.sync = true
P = Struct.new(:x, :y)
class V; include Comparable; attr_reader :v; def initialize(v); @v = v; end; def <=>(o); @v <=> o.v; end; end
class H
  def initialize; @a = [1,2,3,4]; @b = [5,6,7,8]; @h = {a: 1, b: 2, c: 3, d: 4}; @s = "abcd"; @s4 = "1234"; @sp = " ab "; @v = V.new(1); @i = 5; @n = [[1, 2]]; @nh = [{k: [3]}]; @ki = 0; @f = [1.5, 2.5, 0.5, 1.0]; end
  def m0; s = 0; @a.each { |x| s += x }; s; end
  def m1; s = 0; @a.each_with_index { |x, i| s += x + i }; s; end
  def m3; @a.inject(0) { |s, x| s + x }; end
  def m5; @a.reduce(0) { |s, x| s + x }; end
  def m6; @a.inject(:+); end
  def m7; @a.sum; end
  def m8; @a.sum { |x| x + 1 }; end
  def m9; @a.min; end
  def m10; @a.max; end
  def m11; @a.min { |a, b| a <=> b }; end
  def m12; @a.minmax.size; end
  def m13; @a.min_by { |x| -x }; end
  def m14; @a.zip(@b).size; end
  def m15; @a.uniq.size; end
  def m16; @a.sort_by { |x| -x }.size; end
  def m17; s = 0; @a.each_slice(2) { |x| s += x.size }; s; end
  def m18; s = 0; @a.each_cons(2) { |x| s += x.size }; s; end
  def m19; @a.flat_map { |x| [x, x] }.size; end
  def m20; @a.filter_map { |x| x if x > 2 }.size; end
  def m21; @a.each_with_object([]) { |x, m| m << x }.size; end
  def m22; @a.group_by { |x| x & 1 }.size; end
  def m23; @a.partition { |x| x > 2 }.size; end
  def m24; @a.tally.size; end
  def m25; @a.find { |x| x > 2 }; end
  def m26; @a.find_index { |x| x > 2 }; end
  def m27; @a.count { |x| x > 2 }; end
  def m28; @a.take_while { |x| x < 3 }.size; end
  def m29; @a.first(2).size; end
  def m30; @a.take(2).size; end
  def m31; @a.include?(3) ? 1 : 0; end
  def m32; @a.any? { |x| x > 3 } ? 1 : 0; end
  def m33; @a.all? { |x| x > 0 } ? 1 : 0; end
  def m34; s = 0; @a.reverse_each { |x| s += x }; s; end
  def m35; s = 0; @a.each_index { |i| s += i }; s; end
  def m36; @a.each_with_index.map { |x, i| x + i }.size; end
  def m37; @a.to_h { |x| [x, x] }.size; end
  def m38; s = 0; 4.times { |i| s += i }; s; end
  def m39; s = 0; 1.upto(4) { |i| s += i }; s; end
  def m40; s = 0; 1.step(4) { |i| s += i }; s; end
  def m41; s = 0; (1..4).each { |i| s += i }; s; end
  def m42; (1..4).map { |i| i + 1 }.size; end
  def m43; (1..4).inject(0) { |s, x| s + x }; end
  def m44; (1..4).sum; end
  def m45; s = 0; @h.each { |k, v| s += v }; s; end
  def m46; @h.map { |k, v| v }.size; end
  def m47; s = 0; @h.each_pair { |k, v| s += v }; s; end
  def m48; @h.select { |k, v| v > 2 }.size; end
  def m49; @h.any? { |k, v| v > 3 } ? 1 : 0; end
  def m50; @h.sum { |k, v| v }; end
  def m51; @h.to_a.size; end
  def m52; @h.transform_values { |v| v + 1 }.size; end
  def m53; @h.fetch(:b); end
  def m54; @h.dig(:b); end
  def m55; s = 0; @s.each_char { |c| s += 1 }; s; end
  def m56; @a.tap { |x| x }.size; end
  def m57; @a.then { |x| x.size }; end
  def m58; i = 0; loop { i += 1; break if i == 4 }; i; end
  def m59; Integer(@s4); end
  def m60; Array(@a).size; end
  def m61; String(@s4).size; end
  def m62; P.new(1, 2).x; end
  def m63; @v.between?(@v, @v) ? 1 : 0; end
  def m64; @i.clamp(1, 3); end
  def m65; @n.dig(0, 1); end
  def m66; @s.start_with?("ab") ? 1 : 0; end
  def m67; @s.center(10).size; end
  def m68; @a.map(&:to_s).size; end
  def m69; @n.dig(0).size; end
  def m70; @n.dig(@ki, 0); end
  def m71; @nh.dig(0, :k, 0); end
  def m72; @f.sum { |x| x * 2 }.to_i; end
  def m73; @a.sum(10) { |x| x * 3 }; end
  def m74; String(@i).size; end
  def m76; String(nil).size; end
  def m75; @n.to_h.size; end
  def m77; @a.max_by { |x| x & 1 }; end
  def m78; @a.min_by(2) { |x| -x }.size; end
  def m79; @h.sort_by { |k, v| -v }.size; end
  def m80; @a.max_by(2) { |x| x }.size; end
  def m81; @a.drop_while { |x| x < 3 }.size; end
  def m82; @a.flat_map { |x| x }.size; end
  def m83; @a.uniq { |x| x & 1 }.size; end
  def m84; @sp.strip.size; end
  def m85; @sp.lstrip.size; end
  def m86; @sp.rstrip.size; end
  def m87; (1..4).sort_by { |x| -x }.size; end
  def m88; @h.max_by(2) { |k, v| v }.size; end
  def m89; @h.sort_by { |pair| pair[1] }.size; end
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
    # a read is nil once the counter is lost: then there is no count, rather than one of the sections that had it
    if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end
h = H.new
N = (ENV["N"] || 1_000_000).to_i
t("each", N) { |n| i = 0; s = 0; while i < n; s += h.m0; i += 1; end; s }
t("each_with_index", N) { |n| i = 0; s = 0; while i < n; s += h.m1; i += 1; end; s }
t("inject", N) { |n| i = 0; s = 0; while i < n; s += h.m3; i += 1; end; s }
t("reduce", N) { |n| i = 0; s = 0; while i < n; s += h.m5; i += 1; end; s }
t("inject_sym", N) { |n| i = 0; s = 0; while i < n; s += h.m6; i += 1; end; s }
t("sum", N) { |n| i = 0; s = 0; while i < n; s += h.m7; i += 1; end; s }
t("sum_blk", N) { |n| i = 0; s = 0; while i < n; s += h.m8; i += 1; end; s }
t("min", N) { |n| i = 0; s = 0; while i < n; s += h.m9; i += 1; end; s }
t("max", N) { |n| i = 0; s = 0; while i < n; s += h.m10; i += 1; end; s }
t("min_blk", N) { |n| i = 0; s = 0; while i < n; s += h.m11; i += 1; end; s }
t("minmax", N) { |n| i = 0; s = 0; while i < n; s += h.m12; i += 1; end; s }
t("min_by", N) { |n| i = 0; s = 0; while i < n; s += h.m13; i += 1; end; s }
t("zip", N) { |n| i = 0; s = 0; while i < n; s += h.m14; i += 1; end; s }
t("uniq", N) { |n| i = 0; s = 0; while i < n; s += h.m15; i += 1; end; s }
t("sort_by", N) { |n| i = 0; s = 0; while i < n; s += h.m16; i += 1; end; s }
t("each_slice", N) { |n| i = 0; s = 0; while i < n; s += h.m17; i += 1; end; s }
t("each_cons", N) { |n| i = 0; s = 0; while i < n; s += h.m18; i += 1; end; s }
t("flat_map", N) { |n| i = 0; s = 0; while i < n; s += h.m19; i += 1; end; s }
t("filter_map", N) { |n| i = 0; s = 0; while i < n; s += h.m20; i += 1; end; s }
t("each_with_obj", N) { |n| i = 0; s = 0; while i < n; s += h.m21; i += 1; end; s }
t("group_by", N) { |n| i = 0; s = 0; while i < n; s += h.m22; i += 1; end; s }
t("partition", N) { |n| i = 0; s = 0; while i < n; s += h.m23; i += 1; end; s }
t("tally", N) { |n| i = 0; s = 0; while i < n; s += h.m24; i += 1; end; s }
t("find", N) { |n| i = 0; s = 0; while i < n; s += h.m25; i += 1; end; s }
t("find_index", N) { |n| i = 0; s = 0; while i < n; s += h.m26; i += 1; end; s }
t("count_blk", N) { |n| i = 0; s = 0; while i < n; s += h.m27; i += 1; end; s }
t("take_while", N) { |n| i = 0; s = 0; while i < n; s += h.m28; i += 1; end; s }
t("first_n", N) { |n| i = 0; s = 0; while i < n; s += h.m29; i += 1; end; s }
t("take", N) { |n| i = 0; s = 0; while i < n; s += h.m30; i += 1; end; s }
t("include", N) { |n| i = 0; s = 0; while i < n; s += h.m31; i += 1; end; s }
t("any", N) { |n| i = 0; s = 0; while i < n; s += h.m32; i += 1; end; s }
t("all", N) { |n| i = 0; s = 0; while i < n; s += h.m33; i += 1; end; s }
t("reverse_each", N) { |n| i = 0; s = 0; while i < n; s += h.m34; i += 1; end; s }
t("each_index", N) { |n| i = 0; s = 0; while i < n; s += h.m35; i += 1; end; s }
t("map_w_index", N) { |n| i = 0; s = 0; while i < n; s += h.m36; i += 1; end; s }
t("to_h_blk", N) { |n| i = 0; s = 0; while i < n; s += h.m37; i += 1; end; s }
t("times", N) { |n| i = 0; s = 0; while i < n; s += h.m38; i += 1; end; s }
t("upto", N) { |n| i = 0; s = 0; while i < n; s += h.m39; i += 1; end; s }
t("step", N) { |n| i = 0; s = 0; while i < n; s += h.m40; i += 1; end; s }
t("range_each", N) { |n| i = 0; s = 0; while i < n; s += h.m41; i += 1; end; s }
t("range_map", N) { |n| i = 0; s = 0; while i < n; s += h.m42; i += 1; end; s }
t("range_inject", N) { |n| i = 0; s = 0; while i < n; s += h.m43; i += 1; end; s }
t("range_sum", N) { |n| i = 0; s = 0; while i < n; s += h.m44; i += 1; end; s }
t("hash_each", N) { |n| i = 0; s = 0; while i < n; s += h.m45; i += 1; end; s }
t("hash_map", N) { |n| i = 0; s = 0; while i < n; s += h.m46; i += 1; end; s }
t("hash_each_pair", N) { |n| i = 0; s = 0; while i < n; s += h.m47; i += 1; end; s }
t("hash_select", N) { |n| i = 0; s = 0; while i < n; s += h.m48; i += 1; end; s }
t("hash_any", N) { |n| i = 0; s = 0; while i < n; s += h.m49; i += 1; end; s }
t("hash_sum", N) { |n| i = 0; s = 0; while i < n; s += h.m50; i += 1; end; s }
t("hash_to_a", N) { |n| i = 0; s = 0; while i < n; s += h.m51; i += 1; end; s }
t("hash_trans_v", N) { |n| i = 0; s = 0; while i < n; s += h.m52; i += 1; end; s }
t("hash_fetch", N) { |n| i = 0; s = 0; while i < n; s += h.m53; i += 1; end; s }
t("hash_dig", N) { |n| i = 0; s = 0; while i < n; s += h.m54; i += 1; end; s }
t("str_each_char", N) { |n| i = 0; s = 0; while i < n; s += h.m55; i += 1; end; s }
t("tap", N) { |n| i = 0; s = 0; while i < n; s += h.m56; i += 1; end; s }
t("then", N) { |n| i = 0; s = 0; while i < n; s += h.m57; i += 1; end; s }
t("loop", N) { |n| i = 0; s = 0; while i < n; s += h.m58; i += 1; end; s }
t("Integer()", N) { |n| i = 0; s = 0; while i < n; s += h.m59; i += 1; end; s }
t("Array()", N) { |n| i = 0; s = 0; while i < n; s += h.m60; i += 1; end; s }
t("String()", N) { |n| i = 0; s = 0; while i < n; s += h.m61; i += 1; end; s }
t("struct_new", N) { |n| i = 0; s = 0; while i < n; s += h.m62; i += 1; end; s }
t("comparable", N) { |n| i = 0; s = 0; while i < n; s += h.m63; i += 1; end; s }
t("clamp", N) { |n| i = 0; s = 0; while i < n; s += h.m64; i += 1; end; s }
t("dig_arr", N) { |n| i = 0; s = 0; while i < n; s += h.m65; i += 1; end; s }
t("str_start", N) { |n| i = 0; s = 0; while i < n; s += h.m66; i += 1; end; s }
t("str_center", N) { |n| i = 0; s = 0; while i < n; s += h.m67; i += 1; end; s }
t("sym_to_proc", N) { |n| i = 0; s = 0; while i < n; s += h.m68; i += 1; end; s }
t("dig_arr1", N) { |n| i = 0; s = 0; while i < n; s += h.m69; i += 1; end; s }
t("dig_arr_var", N) { |n| i = 0; s = 0; while i < n; s += h.m70; i += 1; end; s }
t("dig_arr_hash", N) { |n| i = 0; s = 0; while i < n; s += h.m71; i += 1; end; s }
t("sum_blk_float", N) { |n| i = 0; s = 0; while i < n; s += h.m72; i += 1; end; s }
t("sum_blk_init", N) { |n| i = 0; s = 0; while i < n; s += h.m73; i += 1; end; s }
t("String(int)", N) { |n| i = 0; s = 0; while i < n; s += h.m74; i += 1; end; s }
t("String(nil)", N) { |n| i = 0; s = 0; while i < n; s += h.m76; i += 1; end; s }
t("to_h_pairs", N) { |n| i = 0; s = 0; while i < n; s += h.m75; i += 1; end; s }
t("max_by", N) { |n| i = 0; s = 0; while i < n; s += h.m77; i += 1; end; s }
t("min_by_n", N) { |n| i = 0; s = 0; while i < n; s += h.m78; i += 1; end; s }
t("hash_sort_by", N) { |n| i = 0; s = 0; while i < n; s += h.m79; i += 1; end; s }
t("max_by_n", N) { |n| i = 0; s = 0; while i < n; s += h.m80; i += 1; end; s }
t("drop_while", N) { |n| i = 0; s = 0; while i < n; s += h.m81; i += 1; end; s }
t("flat_map_scalar", N) { |n| i = 0; s = 0; while i < n; s += h.m82; i += 1; end; s }
t("uniq_blk", N) { |n| i = 0; s = 0; while i < n; s += h.m83; i += 1; end; s }
t("strip", N) { |n| i = 0; s = 0; while i < n; s += h.m84; i += 1; end; s }
t("lstrip", N) { |n| i = 0; s = 0; while i < n; s += h.m85; i += 1; end; s }
t("rstrip", N) { |n| i = 0; s = 0; while i < n; s += h.m86; i += 1; end; s }
t("range_sort_by", N) { |n| i = 0; s = 0; while i < n; s += h.m87; i += 1; end; s }
t("hash_max_by_n", N) { |n| i = 0; s = 0; while i < n; s += h.m88; i += 1; end; s }
t("hash_sort_by_pair", N) { |n| i = 0; s = 0; while i < n; s += h.m89; i += 1; end; s }
