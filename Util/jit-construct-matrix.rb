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
# when something after the construction deopts (an Integer overflowing, a key of a class the
# site has not seen, a frozen node), when initialize is redefined after warm-up (to one that
# counts, one that publishes self, one that raises), and when the class is reopened. A deopt
# restarts the generic body from the start, so a compiled body that constructed before a
# deopt would run initialize twice: these cases are what any version of "construct mid-body"
# has to pass.
#
# A probe only tests that if the entry accepts it and the deopt comes after the construction.
# So each probe passes the types the method was warmed with (Int64 values, not Int32 ones, so
# that an overflow is an overflow and not an entry rejection; the other class sits inside an
# object, not in an argument), each class_eval is followed by a warm-up again, and the cases
# marked `after' are checked on IronRuby with the JIT on, through its counters: a probe that
# meets an invalidation fails (it would test the generic body), and so does one that does
# not deopt once its method compiles. The check writes to stderr and makes the exit status 1,
# so stdout stays the same on every engine. Today the no_* methods are declined at `new' and
# the check says so on stderr instead.
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

# The JIT's statistics (JitRuntime, internal), or nil where there is no JIT to ask: on CRuby,
# and with -X:NoJIT, where nothing is ever specialized. Read by reflection, as is whether one
# method's body is specialized right now (its JitStub's state), since the counters are only
# totals: a callee specializing again during a warm-up is not the method under test compiling.
if RUBY_ENGINE == 'ironruby'
  JIT_BF = System::Reflection::BindingFlags
  JIT_STATIC = JIT_BF.NonPublic | JIT_BF.Static
  JIT_INSTANCE = JIT_BF.NonPublic | JIT_BF.Public | JIT_BF.Instance
  JIT_RUNTIME = System::Type.get_type('IronRuby.Runtime.Jit.JitRuntime, IronRuby')
  JIT_STATS = %w[Specialized Deopts Invalidations].map { |n| JIT_RUNTIME.get_field(n, JIT_STATIC) }
  UNBOUND_METHOD_TYPE = System::Type.get_type('IronRuby.Builtins.UnboundMethod, IronRuby.Libraries')

  def jit_member(o, name)
    t = o.is_a?(UnboundMethod) ? UNBOUND_METHOD_TYPE : o.class.to_clr_type
    while t
      f = t.get_field(name, JIT_INSTANCE)
      return f.get_value(o) if f
      pr = t.get_property(name, JIT_INSTANCE)
      return pr.get_value(o, nil) if pr
      t = t.base_type
    end
    raise "construct-matrix: no #{name} on #{o.class}"
  end

  # mod#name's JitStub, or nil when the method is not wrapped (the screen turned it away)
  def jit_stub(mod, name)
    d = jit_member(jit_member(jit_member(mod.instance_method(name), 'Info'), 'Body'), '_delegate')
    stub = d && d.target
    stub if stub && stub.class.to_clr_type.name.start_with?('JitStub')
  end

  # [specialized now (JitStub's Specialized = 1), times its specialization was dropped]
  def jit_method_state(mod, name)
    stub = jit_stub(mod, name)
    stub ? [jit_member(stub, '_state') == 1, jit_member(stub, '_respecializations')] : [false, 0]
  end
else
  JIT_STATS = nil
end
def jit_stats = JIT_STATS && JIT_STATS.map { |f| f.get_value(nil) }
$check_failed = false

def warm_up
  warm { |i| yield i }
end

# A probe meant to deopt after the construction in mod#name: it fails if the method's
# specialization is dropped during it (the probe would meet a body warmed for nothing: warm it
# up again after a class_eval), or if the method is specialized when the probe starts and the
# probe does not deopt.
def after(mod, name, label, &probe)
  stats = jit_stats
  on = stats && stats[0] > 0
  compiled, dropped = jit_method_state(mod, name) if on
  t(label, &probe)
  return unless on
  deopts = jit_stats[1] - stats[1]
  if jit_method_state(mod, name)[1] > dropped
    warn "construct-matrix: '#{label}': #{mod}##{name} was invalidated: warm it up again first"
    $check_failed = true
  elsif compiled && deopts == 0
    warn "construct-matrix: '#{label}' did not deopt in compiled #{mod}##{name}: the probe tests nothing"
    $check_failed = true
  elsif !compiled
    warn "construct-matrix: '#{label}': #{mod}##{name} is not compiled, semantics only"
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

class CtKeyed
  attr_reader :key
  def initialize(key) = (@key = key)
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

  # constructed, written, then a comparison with a key read from another object, which
  # deopts when that key is of a class the comparison has not seen
  def no_mid_compare(a, o)
    z = CtPair.new(a, o)
    z.sum = 0
    z.sum = 1 if a < o.key
    z
  end
end

def show(o) = o.is_a?(CtPair) ? [o.a, o.b.is_a?(CtKeyed) ? o.b.key : o.b, o.sum] : o

m = CtMaker.new
BIG = 2**40
INT_KEY = CtKeyed.new(BIG + 5)
def warm_all(m)
  warm_up { |i| m.sp_tail(BIG + i, BIG) }
  warm_up { |i| m.no_mid_overflow(BIG + i, BIG) }
  warm_up { |i| m.no_mid_compare(BIG + i, INT_KEY) }
end
warm_all(m)
t('tail: warm') { show(m.sp_tail(BIG + 3, 4)) }
t('mid overflow: warm') { show(m.no_mid_overflow(BIG + 3, 4)) }
t('mid compare: warm') { show(m.no_mid_compare(BIG + 3, INT_KEY)) }
after(CtMaker, :no_mid_overflow, 'mid overflow: Bignum after the construction') { show(m.no_mid_overflow(2**62, 2**62)) }
warm_all(m)
after(CtMaker, :no_mid_compare, 'mid compare: a Float key') { show(m.no_mid_compare(BIG, CtKeyed.new(BIG + 0.5))) }
warm_all(m)
after(CtMaker, :no_mid_compare, 'mid compare: a String key') { show(m.no_mid_compare(BIG, CtKeyed.new('s'))) }

# initialize redefined after warm-up to one that counts: one run per new, whatever deopts
CtPair.class_eval do
  remove_method :initialize
  def initialize(a, b)
    $inits += 1
    @a = a
    @b = b
    @sum = nil
  end
end
warm_all(m)
$inits = 0
t('counting init: tail, overflow before it') { [show(m.sp_tail(2**62, 2**62)), $inits] }
warm_all(m)
$inits = 0
after(CtMaker, :no_mid_overflow, 'counting init: mid, overflow after it') { [show(m.no_mid_overflow(2**62, 2**62)), $inits] }
warm_all(m)
$inits = 0
after(CtMaker, :no_mid_compare, 'counting init: mid, Float key after it') { [show(m.no_mid_compare(BIG, CtKeyed.new(1.5))), $inits] }

# initialize that publishes self before it is done: the object seen must be the one returned,
# once, and complete by the time the caller looks
CtPair.class_eval do
  remove_method :initialize
  def initialize(a, b)
    $published << self
    @a = a
    @b = b
    @sum = nil
  end
end
warm_all(m)
$published = []
r = nil
after(CtMaker, :no_mid_overflow, 'publishing init: overflow after it') { r = m.no_mid_overflow(2**62, 1); [$published.size, $published[0].equal?(r), show($published[0])] }
warm_all(m)
$published = []
after(CtMaker, :no_mid_compare, 'publishing init: Float key after it') { r = m.no_mid_compare(BIG, CtKeyed.new(1.5)); [$published.size, $published[0].equal?(r), show($published[0])] }

# initialize that raises: nothing written after it, the exception the caller's
CtPair.class_eval do
  remove_method :initialize
  def initialize(a, b)
    $inits += 1
    raise ArgumentError, 'no' if a == BIG - 1
    @a = a
    @b = b
  end
end
warm_all(m)
$inits = 0
t('raising init: mid') { [(m.no_mid_overflow(BIG - 1, 1) rescue $!.class), $inits] }
t('raising init: tail') { [(m.sp_tail(BIG - 2, 1) rescue $!.class), $inits] }

# The class reopened: initialize back to the plain one, a writer that counts
CtPair.class_eval do
  remove_method :initialize
  def initialize(a, b) = (@a = a; @b = b; @sum = nil)
  remove_method :sum=
  def sum=(v)
    $inits += 1
    @sum = v
  end
end
warm_all(m)
$inits = 0
after(CtMaker, :no_mid_overflow, 'reopened: a counting writer, overflow') { [show(m.no_mid_overflow(2**62, 2**62)), $inits] }
warm_all(m)
$inits = 0
after(CtMaker, :no_mid_compare, 'reopened: a counting writer, Float key') { [show(m.no_mid_compare(BIG, CtKeyed.new(1.5))), $inits] }

# The tree_insert shape (CD's RedBlackTree#tree_insert): walk with compare_to, an
# InsertResult constructed in the loop on a hit, construct the node, link it, a compare_to
# after the link, the result constructed at the end. CD's keys are Vector2Ds compared by
# Float coordinates; these are CtKeys over a number, so a Float key can sit in a tree of
# Integer ones.
class CtKey
  attr_reader :x
  def initialize(x) = (@x = x)
  def compare_to(other)
    ox = other.x
    return -1 if x < ox
    return 1 if x > ox
    0
  end
end

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

class CtResult
  attr_reader :new_entry, :new_node, :old_value
  def initialize(new_entry, new_node, old_value)
    @new_entry = new_entry
    @new_node = new_node
    @old_value = old_value
  end
  def show = [new_entry, new_node && new_node.key.x, old_value]
end

class CtTree
  attr_reader :root
  def initialize = (@root = nil)

  def no_insert(key, value)
    y = nil
    x = @root
    while x
      y = x
      comparison = key.compare_to(x.key)
      if comparison < 0
        x = x.left
      elsif comparison > 0
        x = x.right
      else
        old = x.value
        x.value = value
        return CtResult.new(false, nil, old)
      end
    end
    z = CtNode.new(key, value)
    z.parent = y
    if y.nil?
      @root = z
    elsif key.compare_to(y.key) < 0
      y.left = z
    else
      y.right = z
    end
    CtResult.new(true, z, nil)
  end

  def keys(n = @root, acc = [])
    return acc unless n
    keys(n.left, acc)
    acc << n.key.x
    keys(n.right, acc)
  end

  def size = keys.size
end

# A node linked in by hand, so a key of another class can sit where the walk passes it but
# the compare after the link has never seen it.
def ct_link(parent, side, x)
  n = CtNode.new(CtKey.new(x), :manual)
  n.parent = parent
  parent.send("#{side}=", n)
  n
end

# A tree whose root's right child holds a Float key with both children in place: inserts
# above 2000 walk through the Float node (the walk's compare_to sees a Float) but are linked
# under Integer nodes (the compare_to after the link only ever sees Integers).
def ct_tree
  tr = CtTree.new
  tr.no_insert(CtKey.new(1000), 0)
  f = ct_link(tr.root, :right, 2000.5)
  ct_link(f, :left, 1500)
  ct_link(f, :right, 3000)
  tr
end
def warm_tree(tr)
  warm_up { |i| tr.no_insert(CtKey.new(3001 + (i * 37) % 997), i) }
end

tr = ct_tree
warm_tree(tr)
t('tree: warm') { [tr.size, tr.keys.first(5), tr.no_insert(CtKey.new(1500), :again).show] }
# a Float leaf, hung where an insert of 1600 ends: the compare after the link sees a Float
ct_link(tr.root.right.left, :right, 1700.5)
after(CtTree, :no_insert, 'tree: a Float node after the link') { [tr.no_insert(CtKey.new(1600), :f).show, tr.size] }
warm_tree(tr)
ct_link(tr.root.right.left.right, :right, 1800.5)
t('tree: a Bignum key, deopts in the walk') { [tr.no_insert(CtKey.new(2**70), :big).show, tr.size] }
warm_tree(tr)
t('tree: a String key') { [(tr.no_insert(CtKey.new('s'), 1) rescue $!.class), tr.size] }

CtNode.class_eval do
  remove_method :initialize
  def initialize(key, value)
    $inits += 1
    @key = key
    @value = value
  end
end
tr = ct_tree
warm_tree(tr)
ct_link(tr.root.right.left, :right, 1700.5)
$inits = 0
after(CtTree, :no_insert, 'tree: counting init, Float node after the link') { [tr.no_insert(CtKey.new(1600), :g).show, $inits, tr.size] }

tf = ct_tree
warm_tree(tf)
ct_link(tf.root, :left, 500).freeze
$inits = 0
after(CtTree, :no_insert, 'tree: a frozen node to link under') { [(tf.no_insert(CtKey.new(400), 0) rescue $!.class), $inits, tf.size] }

# ---- Array.new(n), Array.new(n, v) mid-body (JitCompiler.IsArrayConstruction) ----------
# With the built-in Array.new and Array#initialize and an Integer size, the construction
# runs no Ruby code, so the JIT takes it anywhere in a body: these pin that a deopt after it
# returns what the generic body returns, that each call makes Arrays of its own, and that
# whatever makes it run Ruby code again (a size that is not an Integer, a redefined
# initialize or new, a refinement, another constant named Array) gets the generic answer.
class ArPt
  attr_reader :key
  def initialize(key) = (@key = key)
end

class ArMaker
  attr_reader :st

  def an_empty(n)
    a = Array.new
    @st = a
    n + 1
  end

  def an_sized(n)
    a = Array.new(n)
    k = n + 1
    @st = a
    k
  end

  def an_filled(n, v)
    a = Array.new(n, v)
    @st = Array.new(2)
    a
  end

  def an_lit(v)
    @st = Array.new(3, v)
    x = v + 1
    @st2 = ::Array.new(2, x)
    x
  end
  def st2 = @st2

  def an_size(n)
    a = Array.new(n, 0)
    b = a.size
    b + n
  end

  def an_first(n, v)
    a = Array.new(n, v)
    w = v + 1
    a[0]
  end

  def an_overflow(n, b)
    a = Array.new(n, b)
    @st = a
    b + b
  end

  def an_compare(n, k, o)
    a = Array.new(n, k)
    return [1, a] if k < o.key
    [0, a]
  end

  def an_block(n)
    a = Array.new(n) { |i| i }
    @st = a
    n
  end

  def an_splat(*args)
    a = Array.new(*args)
    @st = a
    1
  end

  def an_sub(n)
    a = ArSubArray.new(n)
    @st = a
    n
  end
end
class ArSubArray < Array; end

class ArLexical
  Array = Struct.new(:n)
  def an_lex(k)
    a = Array.new(k)
    x = k + 1
    [a.n, x]
  end
end

am = ArMaker.new
AR_KEY = ArPt.new(BIG + 5)
AR_O = Object.new
def ar_warm(am)
  warm_up { |i| am.an_empty(i) }
  warm_up { |i| am.an_sized(i % 5) }
  warm_up { |i| am.an_filled(i % 4, AR_O) }
  warm_up { |i| am.an_lit(i) }
  warm_up { |i| am.an_size(i % 6) }
  warm_up { |i| am.an_first(1 + i % 3, i) }
  warm_up { |i| am.an_overflow(2, BIG + i) }
  warm_up { |i| am.an_compare(2, BIG + i, AR_KEY) }
  warm_up { |i| am.an_block(i % 4) }
  warm_up { |i| am.an_splat(i % 3, i) }
  warm_up { |i| am.an_sub(i % 3) }
end
ar_warm(am)
lx = ArLexical.new
warm_up { |i| lx.an_lex(i) }

t('arr: empty, stored') { [am.an_empty(4), am.st] }
t('arr: sized, stored') { [am.an_sized(3), am.st] }
t('arr: filled, returned') { [am.an_filled(3, :x), am.st] }
t('arr: literal size, ::Array') { [am.an_lit(7), am.st, am.st2] }
t('arr: size used after') { am.an_size(4) }
t('arr: a[0] after') { am.an_first(2, 9) }
t('arr: two calls, two arrays') { r1 = am.an_filled(2, 1); r2 = am.an_filled(2, 1); r1 << 5; [r1.equal?(r2), r1, r2] }
t('arr: one object in each slot') { a = am.an_filled(2, AR_O); [a[0].equal?(AR_O), a[1].equal?(AR_O), a.size] }
t('arr: zero size') { am.an_filled(0, 1) }
after(ArMaker, :an_overflow, 'arr: overflow after the construction') { [am.an_overflow(2, 2**62), am.st] }
ar_warm(am)
after(ArMaker, :an_compare, 'arr: a Float key after the construction') { am.an_compare(2, BIG, ArPt.new(BIG + 0.5)) }
ar_warm(am)
t('arr: a String key after the construction') { (am.an_compare(2, BIG, ArPt.new('s')) rescue $!.class) }
ar_warm(am)
t('arr: negative size') { [(am.an_sized(-1) rescue [$!.class, $!.message])] }
ar_warm(am)
t('arr: negative size, filled') { [(am.an_filled(-3, 1) rescue [$!.class, $!.message])] }
ar_warm(am)
t('arr: nil size') { (am.an_sized(nil) rescue $!.class) }
ar_warm(am)
t('arr: Float size') { [am.an_filled(2.5, 1), (am.an_sized(2.5) rescue $!.class), am.st] }
ar_warm(am)
t('arr: String size') { (am.an_sized('3') rescue $!.class) }
ar_warm(am)
t('arr: 2**62 size') { (am.an_filled(2**62, 1) rescue $!.class) }
ar_warm(am)
# CRuby raises RangeError (bignum too big to convert into 'long'), IronRuby ArgumentError
t('arr: 2**64 size') { (am.an_filled(2**64, 1) rescue $!.is_a?(StandardError)) }
ar_warm(am)
t('arr: to_int size') { o = Object.new; def o.to_int = 2; [am.an_filled(o, :t), am.st, (am.an_sized(o) rescue $!.class), am.st] }
ar_warm(am)
t('arr: block, not the construction') { [am.an_block(3), am.st] }
t('arr: splat, not the construction') { [am.an_splat(2, :s), am.st, am.an_splat, am.st] }
t('arr: subclass') { [am.an_sub(2), am.st, am.st.class] }
t('arr: lexical Array constant') { lx.an_lex(3) }

# Array#initialize redefined after warm-up, to one that counts: CRuby's new calls it. (It
# does not call the original through an alias: on IronRuby that raises a CLR
# NullReferenceException from Array.new(n, v).)
$ar_inits = 0
class Array
  alias_method :ar_matrix_init, :initialize
  def initialize(n = 0, v = nil)
    $ar_inits += 1
    concat([v] * n)
  end
end
ar_warm(am)
$ar_inits = 0
t('arr: counting initialize') { [am.an_sized(2), am.st, am.an_filled(2, 1), am.an_lit(1), am.st, am.st2, $ar_inits] }
class Array
  alias_method :initialize, :ar_matrix_init
  private :initialize
  remove_method :ar_matrix_init
end
ar_warm(am)
t('arr: initialize restored') { [am.an_sized(2), am.st, am.an_filled(2, 1)] }

# a singleton new on Array, then removed
def Array.new(*a) = [:singleton, a]
ar_warm(am)
t('arr: singleton new') { [am.an_sized(2), am.st, am.an_filled(2, 1)] }
class << Array
  remove_method :new
end
ar_warm(am)
t('arr: singleton new removed') { [am.an_sized(2), am.st, am.an_filled(2, 1)] }

# a module prepended to Array's singleton class: it cannot be taken away again, so it does
# nothing until switched on. (Two arguments are not passed to super: on IronRuby that raises
# a CLR NullReferenceException from Array#initialize.)
module ArNewPrepend
  def new(*a)
    return [:prepended, a] if $ar_prepended
    a.size == 2 ? [a[1]] * a[0] : super
  end
end
Array.singleton_class.prepend(ArNewPrepend)
ar_warm(am)
$ar_prepended = true
t('arr: prepended new') { [am.an_sized(2), am.st, am.an_filled(2, 1)] }
$ar_prepended = false

# a refinement of Array.new active where the method was written
module ArNewRefinement
  refine Array.singleton_class do
    def new(*a) = [:refined, a]
  end
end
using ArNewRefinement
class ArRefined
  def an_ref(n)
    a = Array.new(n)
    x = n + 1
    [a, x]
  end
end
ar = ArRefined.new
warm_up { |i| ar.an_ref(i % 3) }
# CRuby's an_ref answers the refined new's [:refined, [2]]; IronRuby's ignores the refinement
# here (it does not in a file of its own), so only what the two agree on is printed
t('arr: refined new') { r = ar.an_ref(2); [r.class, r[0].class, r[1]] }

exit 1 if $check_failed
