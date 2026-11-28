# Shapes for CD's red-black tree (Util/perf/LEDGER.md gap 3), modelled on AWFY cd.rb's RedBlackTree, Node and
# Vector2D: get (find_node, which inlines Vector2D#compare_to), put (tree_insert, rejected at its `Node.new'), a walk
# by Node#successor (an identity `==' on a local nothing samples), and lookups whose keys are of one class for a
# while and then of another, as CD's voxel map and its call-sign map take turns (a caller bound to a callee that is
# compiled again for the second class). Format of Util/perf/ab.rb: `<shape> <ns> ns <checksum>`, N from the
# environment, INSTR=1 or Util/perf/callgrind/count.sh for instructions.
#
#   ruby Util/perf/ab.rb -i -r 2 -c none -n 20000 Util/bench/cdtree-shapes.rb \
#        "base=Util/perf/callgrind/count.sh ." "new=Util/perf/callgrind/count.sh ../wt-NAME"
$stdout.sync = true

class TV2
  attr_reader :x, :y
  def initialize(x, y); @x = x; @y = y; end
  def compare_to(other)
    result = compare_numbers(@x, other.x)
    return result if result != 0
    compare_numbers(@y, other.y)
  end
  def compare_numbers(a, b)
    if a == b
      return 0
    end
    if a < b
      return -1
    end
    if a > b
      return 1
    end
    # We say that NaN is smaller than non-NaN.
    return 1 if a == a
    -1
  end
end

class TSign
  attr_reader :value
  def initialize(value); @value = value; end
  def compare_to(other)
    (@value == other.value) ? 0 : ((@value < other.value) ? -1 : 1)
  end
end

class TNode2
  attr_reader :key
  attr_accessor :value, :left, :right, :parent, :color
  def initialize(key, value)
    @key = key
    @value = value
    @left = nil
    @right = nil
    @parent = nil
    @color = :red
  end
  def successor
    x = self
    return tree_minimum2(x.right) if x.right
    y = x.parent
    while y && x == y.right
      x = y
      y = y.parent
    end
    y
  end
end

def tree_minimum2(x)
  current = x
  current = current.left while current.left
  current
end

class TInsert
  attr_reader :is_new_entry, :new_node, :old_value
  def initialize(is_new_entry, new_node, old_value); @is_new_entry = is_new_entry; @new_node = new_node; @old_value = old_value; end
end

class TTree
  attr_reader :root
  def initialize; @root = nil; end
  def get(key)
    node = find_node(key)
    return nil unless node
    node.value
  end
  def find_node(key)
    current = @root
    while current
      comparison_result = key.compare_to(current.key)
      return current if comparison_result == 0
      if comparison_result < 0
        current = current.left
      else
        current = current.right
      end
    end
    nil
  end
  # tree_insert without the rebalancing put does after it: the shape the JIT rejects at `TNode2.new'
  def put(key, value)
    tree_insert(key, value).is_new_entry
  end
  def tree_insert(key, value)
    y = nil
    x = @root
    while x
      y = x
      comparison_result = key.compare_to(x.key)
      if comparison_result < 0
        x = x.left
      elsif comparison_result > 0
        x = x.right
      else
        old_value = x.value
        x.value = value
        return TInsert.new(false, nil, old_value)
      end
    end
    z = TNode2.new(key, value)
    z.parent = y
    unless y
      @root = z
    else
      if key.compare_to(y.key) < 0
        y.left = z
      else
        y.right = z
      end
    end
    TInsert.new(true, z, nil)
  end
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

KEYS = (0...64).map { |i| TV2.new(((i * 37) % 64).to_f, (i % 8).to_f) }
SIGNS = (0...64).map { |i| TSign.new((i * 29) % 64) }
vtree = TTree.new
KEYS.each_with_index { |k, i| vtree.put(k, i) }
stree = TTree.new
SIGNS.each_with_index { |k, i| stree.put(k, i) }
NODES = []
cur = tree_minimum2(vtree.root)
while cur
  NODES << cur
  cur = cur.successor
end

N = (ENV["N"] || 1_000_000).to_i
t("get64", N) { |n| i = 0; s = 0; while i < n; s += vtree.get(KEYS[i & 63]); i += 1; end; s }
# a put of an existing key (the loop's update and return) and, every 64th, a fresh tree's first insert
t("put64", N) { |n| i = 0; s = 0; while i < n; s += 1 if vtree.put(KEYS[i & 63], i); i += 1; end; s }
t("put_new", N / 64 * 64) { |n| i = 0; s = 0; tr = nil; while i < n; tr = TTree.new if (i & 63) == 0; s += 1 if tr.put(KEYS[i & 63], i); i += 1; end; s }
t("successor", N) { |n| i = 0; s = 0; while i < n; nx = NODES[i & 63].successor; s += 1 if nx; i += 1; end; s }
# keys of one class for 4,096 lookups, then of the other: a caller (get) bound to a callee (find_node) that is
# compiled again when the second class arrives
t("phases", N) { |n| i = 0; s = 0; while i < n; s += ((i >> 12).even? ? vtree.get(KEYS[i & 63]) : stree.get(SIGNS[i & 63])); i += 1; end; s }
