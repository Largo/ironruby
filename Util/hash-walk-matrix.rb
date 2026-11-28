# Differential matrix for the Enumerable methods written in Ruby (the prelude) when the receiver is a
# Hash: filter_map, min_by, max_by, sort_by, min_by(n), max_by(n), flat_map, collect_concat. A Hash
# whose #each is the built-in one is walked with a one-value block (__plain_each__?, IR_HASH_WALK),
# anything else with the generic `|*values|` block. Run it under CRuby and under ./ir.sh, and diff:
#
#   ruby Util/hash-walk-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/hash-walk-matrix.rb > /tmp/on.txt 2>&1
#   IR_HASH_WALK=0 ./ir.sh Util/hash-walk-matrix.rb > /tmp/off.txt 2>&1
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt

def t(label)
  print label.ljust(50), ' => '
  begin
    puts yield.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message.sub(/0x\h+/, '0x')}"
  end
end

def h = { a: 3, b: 1, c: 2 }

METHODS = {
  filter_map: ->(r, &b) { r.filter_map(&b) },
  min_by: ->(r, &b) { r.min_by(&b) },
  max_by: ->(r, &b) { r.max_by(&b) },
  sort_by: ->(r, &b) { r.sort_by(&b) },
  'min_by(2)': ->(r, &b) { r.min_by(2, &b) },
  'max_by(2)': ->(r, &b) { r.max_by(2, &b) },
  flat_map: ->(r, &b) { r.flat_map(&b) },
  collect_concat: ->(r, &b) { r.collect_concat(&b) },
  group_by: ->(r, &b) { r.group_by(&b) },
  uniq: ->(r, &b) { r.uniq(&b) },
  each_with_object: ->(r, &b) { $ewo = []; [r.each_with_object($ewo) { |*a| $ewo << b.(a[0]) }, r.each_with_object([], &b).size] },
}

# ---------------------------------------------------------------- 1. what the block is given
BLOCKS = {
  '|x|' => proc { |x| x.inspect },
  '|k, v|' => proc { |k, v| [v, k.to_s] },
  '|k|' => proc { |k| k.inspect },
  '|*a|' => proc { |*a| a.inspect },
  '|(k, v)|' => proc { |(k, v)| v },
  '|k, (v)|' => proc { |k, (v)| v.inspect },
  '|x, *r|' => proc { |x, *r| [r.size, x.to_s] },
  'lambda (x)' => ->(x) { x.inspect },
  'lambda (k, v)' => ->(k, v) { v },
  'lambda (*a)' => ->(*a) { a.size },
  '&:first' => :first.to_proc,
  '&:last' => :last.to_proc,
  'method(1 arg)' => Kernel.method(:Array).to_proc,
}
METHODS.each do |name, m|
  BLOCKS.each do |bname, b|
    t("#{name} #{bname}") { m.(h, &b) }
  end
end

# ---------------------------------------------------------------- 2. the Hash
class SubHash < Hash; end
class OwnEach < Hash
  def each
    yield :own, 1
    yield [:pair, 2]
    self
  end
end
module PrependEach
  def each(&b) = (yield :prepended, 0; super)
end
class PrependedHash < Hash; prepend PrependEach; end
module RefineEach
  refine Hash do
    def each = yield(:refined, 0)
  end
end

METHODS.each do |name, m|
  t("#{name} empty") { m.({}) { |k, v| v } }
  t("#{name} one") { m.({ z: 9 }) { |k, v| [v] } }
  t("#{name} subclass") { m.(SubHash[h]) { |k, v| [v] } }
  t("#{name} subclass with its own #each") { m.(OwnEach[h]) { |k, v| [v.to_s] } }
  t("#{name} a prepended #each") { m.(PrependedHash[h]) { |k, v| [v] } }
  t("#{name} a singleton #each") do
    x = h
    def x.each = yield(:single, 5)
    m.(x) { |k, v| [v] }
  end
  t("#{name} compare_by_identity") { x = {}.compare_by_identity; x["a".dup] = 2; x["a".dup] = 1; m.(x) { |k, v| [v] } }
  t("#{name} default proc not called") { x = Hash.new { |hh, k| raise "default #{k}" }; x[:q] = 1; m.(x) { |k, v| [v] } }
  t("#{name} nil keys and values") { m.({ nil => nil, 1 => 2 }) { |k, v| [v.to_i] } }
  t("#{name} frozen") { m.(h.freeze) { |k, v| [v] } }
  t("#{name} adding a key in the block") { x = h; m.(x) { |k, v| x[:"n#{v}"] = 0; [v] } }
  t("#{name} deleting a later key") { x = h; m.(x) { |k, v| x.delete(:c); [v] } }
  t("#{name} replacing a later value") { x = h; m.(x) { |k, v| x[:c] = 7; [v] } }
  t("#{name} break") { m.(h) { |k, v| break [:broke, k] if v == 1; [v] } }
  t("#{name} next") { m.(h) { |k, v| next [-v] } }
  t("#{name} raise, frames") do
    m.(h) { raise "x" }
  rescue => e
    e.backtrace_locations.first(3).map(&:label)
  end
  t("#{name} throw") { catch(:t) { m.(h) { |k, v| throw :t, k } } }
end

# ---------------------------------------------------------------- 3. Hash#each redefined, then restored
t('a redefined Hash#each') do
  class Hash; alias __saved_each__ each; def each = yield(:redef, 4); end
  begin
    [h.min_by { |k, v| v }, h.sort_by { |k, v| v }, h.filter_map { |k, v| v }, h.flat_map { |k, v| [v] }]
  ensure
    class Hash; alias each __saved_each__; remove_method :__saved_each__; end
  end
end
t('restored') { [h.min_by { |k, v| v }, h.sort_by { |k, v| v }, h.filter_map { |k, v| v }] }
t('Hash#each aliased to each_pair\'s body') do
  class Hash; alias __saved_each__ each; alias each each_pair; end
  begin
    h.max_by { |k, v| v }
  ensure
    class Hash; alias each __saved_each__; remove_method :__saved_each__; end
  end
end

# ---------------------------------------------------------------- 4. answers
t('flat_map answers: to_ary, nil, a bad to_ary') do
  o = Object.new; def o.to_ary = [:t]
  bad = Object.new; def bad.to_ary = :no
  [h.flat_map { |k, v| v == 1 ? o : nil }, (h.flat_map { |k, v| bad } rescue $!.class)]
end
t('min_by keys that do not compare') { h.min_by { |k, v| v == 1 ? "s" : v } }
t('max_by NaN key') { h.max_by { |k, v| v == 2 ? Float::NAN : v.to_f } }
t('min_by ties: the first') { { a: 1, b: 1 }.min_by { 0 } }
t('max_by ties: the first') { { a: 1, b: 1 }.max_by { 0 } }
t('no block: enumerator sizes') { [h.min_by.size, h.sort_by.size, h.filter_map.size, h.flat_map.size, h.max_by(2).size] }
t('enumerator, then each') { h.min_by.each { |k, v| v } }

# ---------------------------------------------------------------- 5. any?, all?, none? (any? is Hash's own in CRuby: rb_hash_any_p)
PRED = {
  'any?': ->(r, *a, &b) { r.any?(*a, &b) },
  'all?': ->(r, *a, &b) { r.all?(*a, &b) },
  'none?': ->(r, *a, &b) { r.none?(*a, &b) },
}
PBLOCKS = {
  '|x|' => proc { |x| x[1] > 2 },
  '|k, v|' => proc { |k, v| v > 2 },
  '|k|' => proc { |k| k.is_a?(Array) },
  '|*a|' => proc { |*a| a.size == 1 && a[0][1] > 2 },
  '|(k, v)|' => proc { |(k, v)| v > 2 },
  '|x, *r|' => proc { |x, *r| r.empty? && x == :c },
  'lambda (x)' => ->(x) { x[1] > 2 },
  'lambda (k, v)' => ->(k, v) { v > 2 },
  '&:frozen?' => :frozen?.to_proc,
  'method(2 args)' => Object.new.tap { |o| def o.m(k, v) = v > 2 }.method(:m).to_proc,
  'method(1 arg)' => Object.new.tap { |o| def o.m(x) = x[1] > 2 }.method(:m).to_proc,
}
class EqqAll; def ===(x) = (($eqq ||= []) << x; false); end
PRED.each do |name, m|
  PBLOCKS.each do |bname, b|
    t("#{name} #{bname}") { m.(h, &b) }
  end
  t("#{name} no block") { [m.(h), m.({}), m.({ nil => nil })] }
  t("#{name} empty with a block") { m.({}) { raise "called" } }
  t("#{name} a pattern") { [m.(h, [:a, 3]), m.(h, Array), m.(h, [:z, 0])] }
  t("#{name} a pattern sees the pair") { $eqq = []; m.(h, EqqAll.new); $eqq }
  t("#{name} a pattern and a block") { m.(h, Array) { raise "called" } }
  t("#{name} 2 arguments") { m.(h, 1, 2) }
  t("#{name} subclass") { m.(SubHash[h]) { |k, v| v > 2 } }
  t("#{name} subclass with its own #each") { m.(OwnEach[h]) { |k, v| v == 2 } }
  t("#{name} a prepended #each") { m.(PrependedHash[h]) { |k, v| v == 0 } }
  t("#{name} a singleton #each") { x = h; def x.each = yield(:single, 5); m.(x) { |k, v| v == 5 } }
  t("#{name} compare_by_identity") { x = {}.compare_by_identity; x["a".dup] = 2; x["a".dup] = 1; m.(x) { |k, v| v == 1 } }
  t("#{name} default proc not called") { x = Hash.new { |hh, k| raise "default #{k}" }; x[:q] = 1; m.(x) { |k, v| v == 1 } }
  t("#{name} adding a key in the block") { x = h; m.(x) { |k, v| x[:"n#{v}"] = 0; false } }
  t("#{name} deleting a later key") { x = h; seen = []; m.(x) { |k, v| x.delete(:c); seen << k; false }; seen }
  t("#{name} replacing a later value") { x = h; seen = []; m.(x) { |k, v| x[:c] = 7; seen << v; false }; seen }
  t("#{name} break") { m.(h) { |k, v| break [:broke, k] if v == 1; false } }
  t("#{name} next") { m.(h) { |k, v| next v == 1 } }
  t("#{name} raise, frames") do
    m.(h) { raise "x" }
  rescue => e
    e.backtrace_locations.first(3).map(&:label)
  end
  t("#{name} throw") { catch(:t) { m.(h) { |k, v| throw :t, k } } }
  t("#{name} walk order and stop") { seen = []; r = m.(h) { |k, v| seen << k; v == 1 }; [r, seen] }
  t("#{name} a frozen Hash") { m.(h.freeze) { |k, v| v == 1 } }
end
t('any? owner, arity, parameters') { mm = {}.method(:any?); [mm.owner, mm.arity, mm.parameters] }
t('any? with a redefined Hash#each') do
  class Hash; alias __saved_each__ each; def each = yield(:redef, 4); end
  begin
    [h.any? { |k, v| k == :redef }, h.all? { |k, v| k == :redef }]
  ensure
    class Hash; alias each __saved_each__; remove_method :__saved_each__; end
  end
end
t('any? of ENV-like: a Struct, a Range') { [Struct.new(:a).new(1).any? { |x| x == 1 }, (1..3).any? { |x| x > 2 }] }

# ---------------------------------------------------------------- 5b. each_with_object's own block shapes (the memo is the second value)
EWO = {
  '|x, m|' => proc { |x, m| m << x },
  '|(k, v), m|' => proc { |(k, v), m| m << v },
  '|k, v|' => proc { |k, v| v << k },
  '|*a|' => proc { |*a| a[1] << a.size },
  '|x|' => proc { |x| $ewo2 << x },
  'lambda (x, m)' => ->(x, m) { m << x },
  'lambda ((k, v), m)' => ->((k, v), m) { m << k },
  'lambda (x)' => ->(x) { $ewo2 << x },
  'method(2 args)' => Object.new.tap { |o| def o.m(x, m) = m << x }.method(:m).to_proc,
  '&:push' => :push.to_proc,
}
EWO.each do |bname, b|
  t("each_with_object #{bname}") { $ewo2 = []; [h.each_with_object([], &b), $ewo2] }
end
t('each_with_object no block') { e = h.each_with_object([]); [e.size, e.next] }
t('each_with_object, a subclass with its own #each') { OwnEach[h].each_with_object([]) { |x, m| m << x } }
t('group_by and uniq, |*a| keys') { [h.group_by { |*a| a.size }, h.uniq { |*a| a.size }] }
t('uniq without a block') { { a: 1, b: 1 }.uniq }

# ---------------------------------------------------------------- 6. a refined Hash#each (last: `using` reaches every later line of the file)
using RefineEach
t('min_by under a refined Hash#each') { h.min_by { |k, v| v } }
t('sort_by under a refined Hash#each') { h.sort_by { |k, v| v } }
t('flat_map under a refined Hash#each') { h.flat_map { |k, v| [v] } }
t('any? under a refined Hash#each') { h.any? { |k, v| k == :refined } }
t('all? under a refined Hash#each') { h.all? { |k, v| k == :refined } }
