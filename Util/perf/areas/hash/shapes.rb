# Hash/Set micro shapes. ruby shapes.rb [filter]  -> "name cpu_ms checksum" per shape
# Least-of-3 CPU time of a fixed amount of work after one warm-up run.
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
class K
  attr_reader :v
  def initialize(v) = @v = v
  def hash = @v.hash
  def eql?(o) = o.is_a?(K) && o.v == @v
end
SYMS = (0...64).map { |i| :"s#{i}" }
STRS = (0...64).map { |i| "str#{i}" }
ARRS = (0...64).map { |i| [i, i + 1] }
KS = (0...64).map { |i| K.new(i) }
BIG = (0...1000).to_h { |i| [i, i] }
SMALL = { a: 1, b: 2, c: 3, d: 4, e: 5 }
DATA_ = (0...1000).map { |i| i % 37 }
S = {
  'aref_sym'   => -> { h = SYMS.to_h { [_1, 1] }; s = 0; 3000.times { SYMS.each { |k| s += h[k] } }; s },
  'aref_str'   => -> { h = STRS.to_h { [_1, 1] }; s = 0; 3000.times { STRS.each { |k| s += h[k] } }; s },
  'aref_int'   => -> { s = 0; 200.times { 1000.times { |i| s += BIG[i] } }; s },
  'aref_arr'   => -> { h = ARRS.to_h { [_1, 1] }; s = 0; 1000.times { ARRS.each { |k| s += h[k] } }; s },
  'aref_custom'=> -> { h = KS.to_h { [_1, 1] }; s = 0; 1000.times { KS.each { |k| s += h[k] } }; s },
  'aset_int'   => -> { 100.times.sum { h = {}; 1000.times { |i| h[i] = i }; h.size } },
  'aset_str'   => -> { 1000.times.sum { h = {}; STRS.each { |k| h[k] = 1 }; h.size } },
  'literal_small' => -> { s = 0; 200_000.times { |i| h = { a: i, b: 2, c: 3 }; s += h[:a] }; s },
  'promote'    => -> { 100_000.times.sum { h = { a: 1, b: 2, c: 3 }; h[:key] = 1; h.size } },
  'each'       => -> { s = 0; 200.times { BIG.each { |k, v| s += v } }; s },
  'each_pair'  => -> { s = 0; 200.times { BIG.each_pair { |k, v| s += k } }; s },
  'map'        => -> { 200.times.sum { BIG.map { |k, v| v }.size } },
  'select'     => -> { 200.times.sum { BIG.select { |k, v| v.even? }.size } },
  'transform_values' => -> { 200.times.sum { BIG.transform_values { _1 + 1 }.size } },
  'group_by'   => -> { 200.times.sum { BIG.group_by { |k, v| v % 10 }.size } },
  'to_a'       => -> { 500.times.sum { BIG.to_a.size } },
  'merge'      => -> { 500.times.sum { BIG.merge(SMALL).size } },
  'merge_small'=> -> { 100_000.times.sum { SMALL.merge(x: 1).size } },
  'dup'        => -> { 500.times.sum { BIG.dup.size } },
  'default_proc' => -> { 100.times.sum { h = Hash.new { |hh, k| hh[k] = k }; 1000.times { |i| h[i] }; h.size } },
  'compare_by_id' => -> { h = {}.compare_by_identity; STRS.each { h[_1] = 1 }; s = 0; 3000.times { STRS.each { |k| s += h[k] } }; s },
  'key?'       => -> { s = 0; 3000.times { SYMS.each { |k| s += 1 if SMALL.key?(k) } }; s },
  'fetch'      => -> { s = 0; 200_000.times { s += SMALL.fetch(:c) }; s },
  'dig'        => -> { h = { a: { b: { c: 1 } } }; s = 0; 200_000.times { s += h.dig(:a, :b, :c) }; s },
  'delete_iter'=> -> { 200.times.sum { h = BIG.dup; h.each { |k, v| h.delete(k) if k.odd? }; h.size } },
  'delete_if'  => -> { 200.times.sum { h = BIG.dup; h.delete_if { |k, v| k.odd? }; h.size } },
  'set_include'=> -> { st = Set.new(0...1000); s = 0; 200.times { 1000.times { |i| s += 1 if st.include?(i) } }; s },
  'set_add'    => -> { 100.times.sum { st = Set.new; 1000.times { |i| st << i }; st.size } },
  'uniq'       => -> { 500.times.sum { DATA_.uniq.size } },
  'tally'      => -> { 500.times.sum { DATA_.tally.size } },
  'ary_group_by' => -> { 500.times.sum { DATA_.group_by { _1 % 7 }.size } },
}
f = ARGV[0] ? Regexp.new(ARGV[0]) : nil
S.each do |n, b|
  next if f && n !~ f
  r = b.call
  t = 3.times.map { t0 = cpu; b.call; cpu - t0 }.min
  puts "%-18s %8.1f %s" % [n, t * 1000, r]
end
