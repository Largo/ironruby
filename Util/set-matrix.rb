# Differential matrix for Set's constructors and operators (Src/StdLib/ironruby/set.rb):
# Set.new and merge from a Set, an Array, a Range, an arithmetic sequence and other
# enumerables, |, -, &, ^, subset?, superset?, proper_subset?, proper_superset?, intersect?
# and disjoint?, for Set, a subclass (Set::SubclassCompatible) and a Set::CoreSet subclass.
#   ruby Util/set-matrix.rb                   # CRuby reference
#   ./ir.sh Util/set-matrix.rb                # IronRuby; the same with -X:NoJIT -X:NoOSR
#                                             # and with IR_LIGHT_FRAMES=0
# Each row is evaluated once cold and again after thousands of runs, some with a method
# of Set or Range redefined or a singleton method added half way.

$log = []
class Sub < Set
  def add(o) = ($log << [:sub_add, o]; super)
  def each(&b) = ($log << :sub_each; super)
  def include?(o) = ($log << [:sub_inc, o]; super)
end
class Core < Set::CoreSet
  def add(o) = ($log << [:core_add, o]; super)
  def each(&b) = ($log << :core_each; super)
  def include?(o) = ($log << [:core_inc, o]; super)
  def delete?(o) = ($log << [:core_del, o]; super)
  def size = ($log << :core_size; super)
end
class Arr < Array
  def each = ($log << :arr_each; super)
  def size = ($log << :arr_size; super)
  def [](i) = ($log << :arr_at; super)
end
class Rng < Range
  def each = ($log << :rng_each; super)
end
class Key
  attr_reader :k
  def initialize(k) = @k = k
  def hash = ($log << [:hash, @k]; @k.hash)
  def eql?(o) = ($log << [:eql, @k]; o.is_a?(Key) && o.k == @k)
  def inspect = "K#{@k}"
end
class Each2
  include Enumerable
  def each = (yield 1; yield 2, 3; yield; yield [4, 5]; self)
end
class OwnEntry
  include Enumerable
  def each = (yield 9)
  def each_entry = (yield 1; yield 2)
end
class OnlyEach
  def each = (yield :x; yield :y)
end
class OnlyEntry
  def each_entry = (yield :e)
end

def row(name)
  $log = []
  v = begin
        r = yield
        "#{r.class} #{r.inspect}#{r.frozen? ? ' frozen' : ''}"
      rescue Exception => e
        "!#{e.class}: #{e.message}"
      end
  puts "#{name.ljust(44)} #{v}#{$log.empty? ? '' : " #{$log.inspect}"}"
end

ROWS = {
  "new range" => -> { Set.new(1..6) },
  "new endless step" => -> { Set.new((1..12).step(5)) },
  "new int step" => -> { Set.new(1.step(10, 4)) },
  "new float step" => -> { Set.new(1.step(2, 0.5)) },
  "new string range" => -> { Set.new("a".."d") },
  "new beginless range" => -> { Set.new(..3) },
  "new Rng" => -> { Set.new(Rng.new(1, 3)) },
  "new array" => -> { Set.new([3, 1, 3, "s", nil]) },
  "new Arr" => -> { Set.new(Arr[1, 2, 2]) },
  "new hash" => -> { Set.new({a: 1, b: 2}) },
  "new struct" => -> { Set.new(Struct.new(:a, :b).new(1, 2)) },
  "new Each2" => -> { Set.new(Each2.new) },
  "new OwnEntry" => -> { Set.new(OwnEntry.new) },
  "new OnlyEach" => -> { Set.new(OnlyEach.new) },
  "new OnlyEntry" => -> { Set.new(OnlyEntry.new) },
  "new enumerator" => -> { Set.new([1, 2].each_slice(1)) },
  "new 5" => -> { Set.new(5) },
  "new keys" => -> { Set.new([Key.new(1), Key.new(1), Key.new(2)]) },
  "new block" => -> { Set.new(1..3) { |x| x * 10 } },
  "new Arr block" => -> { Set.new(Arr[1, 2]) { |x| x * 10 } },
  "Sub.new block" => -> { Sub.new([1, 2]) { |x| -x } },
  "Core.new block" => -> { Core.new(Each2.new) { |x| [x] } },
  "new string key" => -> { s = +"str"; r = Set.new([s]); [r.first.frozen?, r.first.equal?(s)] },
  "new Set" => -> { Set.new(Set[1, 2]) },
  "new Sub" => -> { Set.new(Sub[1, 2]) },
  "new Core" => -> { Set.new(Core[1, 2]) },
  "Sub.new array" => -> { Sub.new([1, 2]) },
  "Sub.new range" => -> { Sub.new(1..2) },
  "Core.new array" => -> { Core.new([1, 2]) },
  "Core.new range" => -> { Core.new(1..2) },
  "Core.new Each2" => -> { Core.new(Each2.new) },
  "Core.new Set" => -> { Core.new(Set[7]) },
  "brackets" => -> { Set[1, 2, 1] },
  "Core brackets" => -> { Core[1, 2, 1] },
  "identity new" => -> { a = "x"; s = Set.new.compare_by_identity; s.merge([a, a, "x".dup]); [s.size, s.compare_by_identity?] },
  "merge many" => -> { Set[1].merge([2], 3..4, Set[5]) },
  "merge frozen" => -> { Set[1].freeze.merge([2]) },
  "merge in each" => -> { s = Set[1]; s.each { s.merge([2]) } },
  "merge self in each" => -> { s = Set[1]; s.each { s.merge(s) } },
  "merge non-enum" => -> { Set[1].merge(Object.new) },
  "union" => -> { Set[1, 2] | Set[2, 3] },
  "union array" => -> { Set[1, 2] | [3, 1] },
  "union range" => -> { Set[1, 2] | (2..4) },
  "union non-enum" => -> { Set[1] | 3 },
  "union Sub" => -> { Set[1] | Sub[5] },
  "union Core" => -> { Set[1] | Core[5] },
  "Sub union" => -> { Sub[1] | [2] },
  "Core union" => -> { Core[1] | [2] },
  "Core union Core" => -> { Core[1] | Core[2] },
  "union frozen" => -> { Set[1].freeze | [2] },
  "union identity" => -> { s = Set["a"].compare_by_identity | ["a"]; [s.size, s.compare_by_identity?] },
  "minus" => -> { Set[1, 2, 3] - Set[2] },
  "minus array" => -> { Set[1, 2, 3] - [3, 4] },
  "minus range" => -> { Set[1, 2, 3] - (2..9) },
  "minus Sub" => -> { Set[1, 2] - Sub[1] },
  "minus Core" => -> { Set[1, 2] - Core[1] },
  "Sub minus" => -> { Sub[1, 2] - [1] },
  "Core minus" => -> { Core[1, 2] - [1] },
  "Core minus Core" => -> { Core[1, 2] - Core[2] },
  "minus non-enum" => -> { Set[1] - 3 },
  "minus frozen" => -> { Set[1, 2].freeze - [1] },
  "and" => -> { Set[1, 2, 3] & Set[3, 2, 9] },
  "and larger" => -> { Set[1, 2] & Set[3, 2, 9, 1] },
  "and array" => -> { Set[1, 2, 3] & [3, 1, 7] },
  "and Sub" => -> { Set[1, 2] & Sub[1, 2, 3] },
  "and Core" => -> { Set[1, 2, 3, 4] & Core[1] },
  "Sub and" => -> { Sub[1, 2] & [1] },
  "Sub and Set" => -> { Sub[1, 2] & Set[1] },
  "Core and" => -> { Core[1, 2] & [1] },
  "Core and Core" => -> { Core[1, 2] & Core[2, 3] },
  "and non-enum" => -> { Set[1] & 3 },
  "and identity" => -> { s = Set["a"].compare_by_identity & ["a"]; [s.size, s.compare_by_identity?] },
  "and keys" => -> { Set[Key.new(1), Key.new(2)] & Set[Key.new(2)] },
  "and frozen" => -> { Set[1, 2].freeze & Set[2] },
  "xor" => -> { Set[1, 2] ^ Set[2, 3] },
  "xor array" => -> { Set[1, 2] ^ [2, 3, 3] },
  "xor Sub" => -> { Set[1, 2] ^ Sub[2, 3] },
  "Sub xor" => -> { Sub[1, 2] ^ [3] },
  "Core xor" => -> { Core[1, 2] ^ [2, 3] },
  "Core xor Core" => -> { Core[1, 2] ^ Core[2, 3] },
  "xor non-enum" => -> { Set[1] ^ 3 },
  "xor identity" => -> { s = Set["a"].compare_by_identity ^ ["a"]; [s.size, s.compare_by_identity?] },
  "xor frozen" => -> { Set[1, 2].freeze ^ [2] },
  "subset" => -> { [Set[1].subset?(Set[1, 2]), Set[1, 3].subset?(Set[1, 2]), Set[1, 2] <= Set[1, 2], Set[1, 2] < Set[1, 2], Set[1] < Set[1, 2]] },
  "superset" => -> { [Set[1, 2].superset?(Set[1]), Set[1].superset?(Set[1, 2]), Set[1, 2] >= Set[1, 2], Set[1, 2] > Set[1, 2], Set[1, 2] > Set[1]] },
  "subset Sub" => -> { [Set[1].subset?(Sub[1, 2]), Set[1] < Sub[1, 2], Set[1, 2].superset?(Sub[1]), Set[1, 2] > Sub[1]] },
  "subset Core" => -> { [Set[1].subset?(Core[1, 2]), Set[1] < Core[1, 2], Set[1, 2].superset?(Core[1]), Set[1, 2] > Core[1]] },
  "Sub subset" => -> { [Sub[1].subset?(Set[1, 2]), Sub[1] < Sub[1, 2], Sub[1, 2].superset?(Core[1]), Sub[1, 2] > Set[1]] },
  "Core subset" => -> { [Core[1].subset?(Set[1, 2]), Core[1] < Core[1, 2], Core[1, 2].superset?(Sub[1]), Core[1, 2] > Set[1]] },
  "subset array" => -> { Set[1].subset?([1]) },
  "superset array" => -> { Set[1] >= [1] },
  "subset identity" => -> { a = "a".freeze; b = "a".dup.freeze; [Set[a].compare_by_identity <= Set[b], Set[b] <= Set[a].compare_by_identity, Set[a] <= Set[a].compare_by_identity, Set[a] <= Set[b].compare_by_identity] },
  "subset keys" => -> { Set[Key.new(1)] <= Set[Key.new(1), Key.new(2)] },
  "intersect" => -> { [Set[1].intersect?(Set[1, 2]), Set[1, 2, 3].intersect?(Set[4]), Set[1].intersect?([1]), Set[1].intersect?(2..3)] },
  "disjoint" => -> { [Set[1].disjoint?(Set[1, 2]), Set[1, 2, 3].disjoint?(Set[4]), Set[1].disjoint?([1]), Set[1].disjoint?(2..3)] },
  "intersect Sub Core" => -> { [Set[1].intersect?(Sub[1]), Set[1].disjoint?(Core[1]), Sub[1].intersect?(Core[1]), Core[1].disjoint?(Sub[1])] },
  "intersect non-enum" => -> { Set[1].intersect?(3) },
  "intersect keys" => -> { Set[Key.new(1), Key.new(2)].intersect?(Set[Key.new(2)]) },
}

ROWS.each { |name, f| row(name, &f) }
2000.times { ROWS.each_value { |f| $log = []; f.call rescue nil } }
puts "-- hot"
ROWS.each { |name, f| row(name, &f) }

# something changes in the middle of hot loops
class Set
  alias __m_keep_add add
  def add(o) = ($log << [:set_add, o]; __m_keep_add(o))
end
puts "-- Set#add redefined"
ROWS.each { |name, f| row(name, &f) }
class Set
  alias add __m_keep_add
end
class Range
  alias __m_keep_each each
  def each(&b) = ($log << :range_each; __m_keep_each(&b))
end
puts "-- Range#each redefined"
2.times { ["new range", "union range", "new beginless range"].each { |n| row(n, &ROWS[n]) } }
class Range
  alias each __m_keep_each
end
s = Set[1, 2]
def s.each(&b) = ($log << :singleton_each; super)
def s.add(o) = ($log << :singleton_add; super)
puts "-- singleton methods"
row("single union") { s | [3] }
row("single and") { s & Set[2] }
row("single subset") { s <= Set[1, 2, 3] }
row("new from single") { Set.new(s) }
row("merge single") { Set[9].merge(s) }
puts "-- hot again"
2000.times { ROWS.each_value { |f| $log = []; f.call rescue nil } }
ROWS.each { |name, f| row(name, &f) }
