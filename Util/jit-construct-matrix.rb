# Differential matrix for constructions in bodies the method JIT compiles, and deopts after
# them (gap 3 of Util/perf/LEDGER.md, CD's RedBlackTree#tree_insert). Run it like
# Util/jit-matrix.rb, under CRuby and under ./ir.sh with the JIT on and off, and diff:
#
#   ruby Util/jit-construct-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-construct-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT Util/jit-construct-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# What it pins: `initialize' runs exactly once per `new', and no object is seen half-built,
# when something after the construction deopts (an Integer overflowing, a key of another
# class, a frozen node), when initialize is redefined after warm-up (to one that counts, one
# that publishes self, one that raises), and when the class is reopened. A deopt restarts the
# generic body from the start, so a compiled body that constructed before a deopt would run
# initialize twice: these cases are what any version of "construct mid-body" has to pass.
#
# The methods named sp_* are shapes the JIT compiles today; no_* are shapes it declines today
# (a construction that is not the last statement), the ones gap 3 is about. To see which:
#
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-construct-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]'

def t(label)
  print label.ljust(56), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts e.class
  end
end

HOT = 120
def warm
  i = 0
  while i < HOT
    yield i
    i += 1
  end
end

$inits = 0
$published = []

class CtPair
  attr_accessor :a, :b, :sum
  def initialize(a, b)
    @a = a
    @b = b
    @sum = nil
  end
end

class CtMaker
  # the construction ends the body: compiled today
  def sp_tail(a, b) = CtPair.new(a + b, b)

  # constructed, then an operation that can deopt (Integer overflow), then the object written
  def no_mid_overflow(a, b)
    z = CtPair.new(a, b)
    z.sum = a + b
    z
  end

  # constructed, written, then a comparison that deopts on a key of another class
  def no_mid_compare(a, b)
    z = CtPair.new(a, b)
    z.sum = 0
    z.sum = 1 if a < b
    z
  end
end

def show(o) = o.is_a?(CtPair) ? [o.a, o.b, o.sum] : o

m = CtMaker.new
warm { |i| m.sp_tail(i, 1); m.no_mid_overflow(i, 2); m.no_mid_compare(i, 3) }
t('tail: warm') { show(m.sp_tail(3, 4)) }
t('mid overflow: warm') { show(m.no_mid_overflow(3, 4)) }
t('mid compare: warm') { show(m.no_mid_compare(3, 4)) }
t('mid overflow: Bignum after the construction') { show(m.no_mid_overflow(2**62, 2**62)) }
t('mid compare: a Float key') { show(m.no_mid_compare(1.5, 2)) }
t('mid compare: a String against an Integer') { show(m.no_mid_compare('a', 2)) }

# initialize redefined after warm-up to one that counts: one run per new, whatever deopts
CtPair.class_eval do
  def initialize(a, b)
    $inits += 1
    @a = a
    @b = b
    @sum = nil
  end
end
$inits = 0
t('counting init: tail, Bignum argument') { [show(m.sp_tail(2**62, 2**62)), $inits] }
$inits = 0
t('counting init: mid, overflow after it') { [show(m.no_mid_overflow(2**62, 2**62)), $inits] }
$inits = 0
t('counting init: mid, Float key after it') { [show(m.no_mid_compare(1.5, 2)), $inits] }
$inits = 0
warm { |i| m.no_mid_overflow(i, 1); m.no_mid_compare(i, 1) }
t('counting init: re-warmed, one call each') { [show(m.no_mid_overflow(2**62, 1)), show(m.no_mid_compare(2.5, 1)), $inits - 2 * HOT] }

# initialize that publishes self before it is done: the object seen must be the one returned,
# once, and complete by the time the caller looks
CtPair.class_eval do
  def initialize(a, b)
    $published << self
    @a = a
    @b = b
    @sum = nil
  end
end
$published = []
r = m.no_mid_overflow(2**62, 1)
t('publishing init: overflow after it') { [$published.size, $published[0].equal?(r), show($published[0])] }
$published = []
r = m.no_mid_compare(1.5, 1)
t('publishing init: Float key after it') { [$published.size, $published[0].equal?(r), show($published[0])] }

# initialize that raises: nothing written after it, the exception the caller's
CtPair.class_eval do
  def initialize(a, b)
    $inits += 1
    raise ArgumentError, 'no' if a == :bad
    @a = a
    @b = b
  end
end
$inits = 0
t('raising init: mid') { [(m.no_mid_overflow(:bad, 1) rescue $!.class), $inits] }
t('raising init: tail') { [(m.sp_tail(:bad, 1) rescue $!.class), $inits] }

# The class reopened: initialize back to the plain one, a writer that counts
CtPair.class_eval do
  def initialize(a, b) = (@a = a; @b = b; @sum = nil)
  def sum=(v)
    $inits += 1
    @sum = v
  end
end
$inits = 0
t('reopened: a counting writer, overflow') { [show(m.no_mid_overflow(2**62, 2**62)), $inits] }
$inits = 0
t('reopened: a counting writer, Float key') { [show(m.no_mid_compare(1.5, 2)), $inits] }

# The tree_insert shape (CD's RedBlackTree): walk, construct, link, a compare after the link.
class CtNode
  attr_reader :key
  attr_accessor :value, :left, :right, :parent
  def initialize(key, value)
    @key = key
    @value = value
    @left = nil
    @right = nil
    @parent = nil
  end
end

class CtTree
  attr_reader :root
  def initialize = (@root = nil)

  def no_insert(key, value)
    y = nil
    x = @root
    while x
      y = x
      if key < x.key
        x = x.left
      elsif key > x.key
        x = x.right
      else
        old = x.value
        x.value = value
        return [false, old]
      end
    end
    z = CtNode.new(key, value)
    z.parent = y
    if y.nil?
      @root = z
    elsif key < y.key
      y.left = z
    else
      y.right = z
    end
    [true, z.key]
  end

  def keys(n = @root, acc = [])
    return acc unless n
    keys(n.left, acc)
    acc << n.key
    keys(n.right, acc)
  end

  def size = keys.size
end

tr = CtTree.new
warm { |i| tr.no_insert((i * 37) % 101, i) }
t('tree: warm') { [tr.size, tr.keys.first(5), tr.no_insert(5, :again)] }
t('tree: a Float key') { [tr.no_insert(50.5, :f), tr.size] }
t('tree: a Bignum key') { [tr.no_insert(2**70, :big), tr.size, tr.keys.last.class] }
t('tree: a String key') { [(tr.no_insert('s', 1) rescue $!.class), tr.size] }
CtNode.class_eval do
  def initialize(key, value)
    $inits += 1
    @key = key
    @value = value
  end
end
$inits = 0
t('tree: counting init, Float key') { [tr.no_insert(60.5, :g), $inits, tr.size] }
$inits = 0
t('tree: counting init, String key') { [(tr.no_insert('t', 1) rescue $!.class), $inits, tr.size] }
tf = CtTree.new
warm { |i| tf.no_insert(i, i) }
tf.root.freeze
t('tree: a frozen root') { [(tf.no_insert(-1, 0) rescue $!.class), tf.size] }
