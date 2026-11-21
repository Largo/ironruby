# Differential matrix for methods that call `super' and run without a scope (a light frame, MethodDefinition.IsScopeFree).
# Run it under CRuby and under ./ir.sh with the shape on and off, and diff:
#
#   ruby Util/super-light-matrix.rb > mri.txt
#   ./ir.sh Util/super-light-matrix.rb > on.txt
#   IR_LIGHT_SUPER=0 ./ir.sh Util/super-light-matrix.rb > off.txt
#   diff mri.txt on.txt && diff mri.txt off.txt
#
# A super call looks the method up from the module the method was defined in, by the name it was defined under, on the
# method's self, and passes the method's block unless given one; a parameterless super passes the parameters' current
# values. The probes cover each kind of argument list and block, the method super reaches (Ruby, C#, method_missing, one
# that reads its caller's frame: binding, local_variables, block_given?, $~), the hierarchy changing under a bound site
# (redefined, removed, a module included or prepended), other ways in (alias, define_method, bind_call, send, a
# singleton method, a refinement), visibility, super in a block of the method, threads, and recursion a million deep.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/super-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

N = 3000

def t(label)
  print label.ljust(52), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 70]}"
  end
end

# ---------------------------------------------------------------- 1. arguments

class P1
  def two(a, b) = [:P1, a, b]
  def rest(*r) = [:P1, r]
  def opt(a, b = 2) = [:P1, a, b]
  def kw(a, k: 0, **o) = [:P1, a, k, o]
  def blk(x) = [:P1, x, block_given? ? yield(x) : :noblock]
  def nothing = [:P1, :nothing]
end

class C1 < P1
  def two(a, b) = super(b, a)
  def rest(a, *r)
    super
  end
  def opt(a, b = 7)
    b = b * 10
    super
  end
  def kw(a) = super(a, k: a + 1)
  def blk(x)
    super
  end
  def nothing
    super()
  end
end

class C1b < P1
  def two(a, b)
    super
  end
  def rest(a, *r) = super(a, *r, :tail)
  def opt(a, b = 7)
    a = :changed
    super(a)
  end
  def kw(a, h) = super(a, **h)
  def blk(x, &b) = super(x, &b)
  def nothing = super
end

class C1c < P1
  def blk(x) = super(x, &nil)
  def rest(*r) = super(*r, *r)
  def two(a, b) = super(*[a, b])
end

t("explicit, swapped") { |i| C1.new.two(i % 3, :b) }
t("zsuper, two") { |i| C1b.new.two(i % 2, nil) }
t("zsuper, rest") { |i| C1.new.rest(i % 2, *[:x] * (i % 3)) }
t("explicit splat, rest") { |i| C1b.new.rest(i % 2, *[:y] * (i % 2)) }
t("rest twice") { |i| C1c.new.rest(*[1, 2].first(i % 3)) }
t("splat array to two") { |i| C1c.new.two(i % 2, 5) }
t("zsuper, optional reassigned") { |i| i.even? ? C1.new.opt(1) : C1.new.opt(1, 3) }
t("explicit, mandatory reassigned") { |i| C1b.new.opt(i % 2) }
t("explicit keywords") { |i| C1.new.kw(i % 3) }
t("keyword splat") { |i| C1b.new.kw(1, i.even? ? {} : { k: 5, z: 1 }) }
t("zsuper, block implicit") { |i| C1.new.blk(i % 2) { |v| v + 100 } }
t("zsuper, no block") { |i| C1.new.blk(i % 2) }
t("explicit &b") { |i| i.even? ? C1b.new.blk(1) { |v| -v } : C1b.new.blk(2) }
t("explicit &nil drops the block") { C1c.new.blk(3) { :never } }
t("super() no args") { C1.new.nothing }
t("zsuper no params") { C1b.new.nothing }
t("wrong arity to super") { |i| C1b.new.two(i, 1) && (i == 2999 ? P1.instance_method(:two).bind_call(C1.new, 1) : 0) }

# ---------------------------------------------------------------- 2. initialize, C# and core methods

class P2
  attr_reader :a, :b
  def initialize(a, b = :pb)
    @a = a
    @b = b
  end
end
class C2 < P2
  def initialize(a)
    super(a, a.to_s)
    @c = 3
  end
  def all = [a, b, @c]
end
class C2z < P2
  def initialize(a, b)
    super
    @c = 4
  end
  def all = [a, b, @c]
end
class C2o
  def initialize(x)
    super()
    @x = x
  end
  attr_reader :x
end
class C2bad
  def initialize(x)
    super
    @x = x
  end
end
class MyArr < Array
  def push(x) = super(x * 2)
  def initialize(n)
    super(n)
  end
end
class MyHash < Hash
  def [](k) = super || :dflt
end
class MyStr < String
  def +(o) = super(o.to_s)
  def upcase = super + "!"
  def match?(r) = super
  def =~(r) = super
end
class MyErr < StandardError
  def initialize(m)
    super("my: #{m}")
  end
end
class Obj2
  def to_s = super.sub(/0x\h+/, "X")
  def respond_to?(m, p = false) = m == :zz || super
  def freeze = super
end
class StructSub < Struct.new(:p, :q)
  def initialize(p)
    super(p, p * 2)
  end
end

t("initialize explicit") { |i| C2.new(i % 3).all }
t("initialize zsuper") { |i| C2z.new(i % 2, :z).all }
t("initialize into Object#initialize") { |i| C2o.new(i % 2).x }
t("initialize zsuper into Object, 1 arg") { C2bad.new(1).class }
t("Array subclass push, initialize") { |i| MyArr.new(i % 2).push(i % 3).to_a }
t("Hash subclass []") { |i| MyHash.new.merge!(a: 1)[i.even? ? :a : :b] }
t("String subclass +, upcase") { |i| (MyStr.new("a") + i % 2).upcase }
t("String subclass match?") { MyStr.new("abc").match?(/b/) }
t("Exception initialize") { |i| MyErr.new(i % 2).message }
t("Kernel#to_s") { Obj2.new.to_s }
t("respond_to?") { |i| Obj2.new.respond_to?(i.even? ? :zz : :to_s) }
t("freeze") { Obj2.new.freeze.frozen? }
t("Struct initialize") { |i| StructSub.new(i % 2).to_a }

class << (CN = Class.new(P2))
  def new(*args) = super
end
class CN2 < P2
  def self.new(a) = super(a, :n2)
end
t("singleton new into Class#new") { |i| CN.new(i % 2).b }
t("self.new explicit") { |i| CN2.new(i % 2).b }

# ---------------------------------------------------------------- 3. callees that read the caller's frame

class P3
  def bg = block_given?
end
class C3 < P3
  def binding
    x = 1
    x += 1
    super
  end
  def local_variables
    y = 2
    y += 0
    super
  end
  def block_given? = super
  def bg = super
  def eval(s) = super
  def caller_label = caller_locations(1, 1)[0].label
  def __method__ = super
end
class C3b < C3
  def caller_label = super
end

t("binding via super") { |i| b = C3.new.binding; [b.local_variables, b.local_variable_get(:x), i > 10] }
t("local_variables via super") { C3.new.local_variables }
t("block_given? via super, block") { C3.new.block_given? { } }
t("block_given? via super, none") { C3.new.block_given? }
t("Ruby bg via zsuper, block") { |i| i.even? ? C3.new.bg { } : C3.new.bg }
t("eval via super") { C3.new.eval("s.size") }
t("caller label through super") { C3b.new.caller_label }
t("__method__ via super") { C3.new.__method__ }

def keep_match
  "keep" =~ /(ke)ep/
  r = yield
  [r, $~ && $~[0], $1]
end
t("String#=~ via super, caller's $~ kept") { keep_match { MyStr.new("abc") =~ /b(c)/ } }
t("String#match? via super, caller's $~ kept") { keep_match { MyStr.new("abc").match?(/b(c)/) } }

# ---------------------------------------------------------------- 4. the hierarchy changes under a bound site

class P4; def f = :p4; def g = :g4; end
class C4 < P4; def f = [:c4, super]; def g = [:c4g, super]; end
t("parent redefined half way") do |i|
  P4.class_eval { def f = :p4new } if i == 1500
  C4.new.f
end
t("parent method removed half way") do |i|
  P4.send(:remove_method, :g) if i == 1500
  C4.new.g
end

class A5; def f = :a5; end
class B5 < A5; end
class C5 < B5; def f = [:c5, super]; end
module M5; def f = [:m5, super]; end
module M5p; def f = [:m5p, super]; end
t("module included half way") do |i|
  B5.include(M5) if i == 1000
  B5.prepend(M5p) if i == 2000
  C5.new.f
end

class P6; end
class C6 < P6
  def f(x) = [:c6, super]
end
class P6; def method_missing(n, *a) = [:mm, n, a]; def respond_to_missing?(*) = true; end
t("method_missing reached by super") { |i| C6.new.f(i % 2) }
class C6b; def f(x) = super; end
t("no super method, NoMethodError") { |i| C6b.new.f(i) }

module M7; def f = [:m7, super]; end
class X7; def f = :x7; include M7; end
class Y7; def f = :y7; include M7; end
t("one module, two classes") { |i| (i.even? ? X7 : Y7).new.f }

class P8; def f = :p8; end
c8 = 2.times.map { Class.new(P8) { def f = [:anon, super] } }
t("Class.new body run twice") { |i| c8[i % 2].new.f }

# ---------------------------------------------------------------- 5. other ways in

class P9; def f(a = 0) = [:p9, a]; def h = :h9; end
class C9 < P9
  def f(a = 1) = [:c9, super]
  alias g f
  define_method(:dm, instance_method(:f))
  def h = [:c9h, super]
end
t("alias calls super of the defined name") { |i| C9.new.g(i % 2) }
t("define_method from instance_method") { |i| C9.new.dm(i % 2) rescue $!.class }
t("send") { |i| C9.new.send(:f, i % 2) }
t("public_send") { C9.new.public_send(:h) }
t("Method#call") { C9.new.method(:f).call(5) }
t("to_proc") { [7].map(&C9.new.method(:f)) }
t("bind_call") { C9.instance_method(:h).bind_call(C9.new) }

module M10; def f = [:m10, super]; end
class P10; def f = :p10; end
t("module method bind_call on non-includer") { M10.instance_method(:f).bind_call(P10.new) }

o11 = P9.new
def o11.f(a = 3) = [:sing, super]
t("singleton method") { |i| o11.f(i % 2) }

module MF12
  module_function
  def f = super
end
t("module_function super") { MF12.f rescue $!.class }

class P13; def f(*a, **k) = [a, k]; end
class C13 < P13
  ruby2_keywords def f(*a)
    super
  end
end
t("ruby2_keywords zsuper") { |i| C13.new.f(1, k: i % 2) }

# ---------------------------------------------------------------- 6. visibility

class P14
  def pub = :pub
  private def priv = :priv
  protected def prot = :prot
end
class C14 < P14
  def priv = [:c, super]
  def prot = [:c, super]
  private def pub = [:cpriv, super]
  def callpub = pub
end
t("super to private") { C14.new.priv }
t("super to protected") { C14.new.prot }
t("private method super to public") { C14.new.callpub }
t("private called with receiver") { C14.new.pub rescue $!.class }

# ---------------------------------------------------------------- 7. super in a block of the method

class P15; def f(a = 0) = [:p15, a]; def each2 = [yield(1), yield(2)]; end
class C15 < P15
  def f(a = 9) = [1].map { super }.first
  def g(a) = [1].map { |x| f(a + x) }
  def each2
    super
  end
  def blk_ret(a)
    [1].each { return super(a) }
    :not
  end
end
t("zsuper in a block") { |i| C15.new.f(i % 2) }
t("super in block via f") { C15.new.g(1) }
t("implicit block, break") { |i| C15.new.each2 { |x| break :br if i.even? && x == 1; x } }
t("implicit block, next") { C15.new.each2 { |x| next x * 3 } }
t("return from block with super") { |i| C15.new.blk_ret(i % 2) }

# The method's block, passed on by super to a method that keeps it: a literal block must not be made again for the next
# call while the Proc is kept.
class P19; def keep(&b) = b; end
class C19 < P19; def keep = super; end
class C19b < P19; def keep = [1].map { super }.first; end
class C19c < P19; def keep = super(); end
class C19d < P19; def keep(&b) = super(&b); end
def mk19(c, i) = c.new.keep { i }
[C19, C19b, C19c, C19d].each do |c|
  t("block kept through super, #{c}") { |i| [mk19(c, i), mk19(c, i + 1)].map(&:call) == [i, i + 1] }
end

# ---------------------------------------------------------------- 8. threads, depth, tracing

class P17; def f(x) = x + 1; end
class C17 < P17; def f(x) = super * 2; end
t("threads") do |i|
  next 0 unless i % 300 == 0
  4.times.map { |k| Thread.new { s = 0; 2000.times { |j| s += C17.new.f(j + k) }; s } }.map(&:value)
end

class P18; def r(n) = n == 0 ? 0 : r(n - 1); end
class C18 < P18; def r(n) = super; end
puts "recursion through super: " + (begin; C18.new.r(1_000_000).to_s; rescue SystemStackError; "SystemStackError"; end)
puts "recursion through super again: " + (begin; C18.new.r(1000).to_s; rescue SystemStackError; "SystemStackError"; end)

calls = []
# (:c_call is left out: IronRuby does not report a super call into a C# method as one, with or without light frames)
tp = TracePoint.new(:call) { |x| calls << [x.event, x.method_id] if x.method_id == :f || x.method_id == :push }
tp.enable { C17.new.f(1); MyArr.new(0).push(1) }
p calls
t("after the TracePoint") { |i| C17.new.f(i % 2) }

# ---------------------------------------------------------------- 8b. errors of the call, a shared site, a value
def guard2
  yield
rescue Exception => e
  "#{e.class}: #{e.message}"
end
class P17; def two(a, b) = [a, b]; def r(a) = a * 2; def kw(a, k:) = [a, k]; end
class C17 < P17
  def two(a, b) = super(a)
  def many(a) = super(a, a, a)
  def r(a) = ([1].each { return super(a) }; :no)
  def kw(a, k: 1) = super(a)
end
class D17 < P17; def two(a, b) = super(a, b, 3); def kw(a, k: 1) = super(a, z: 1); end
t("too few arguments through super") { |i| guard2 { C17.new.two(i % 2, 1) } }
t("too many arguments through super") { |i| guard2 { D17.new.two(i % 2, 1) } }
t("no superclass method, three arguments") { |i| guard2 { C17.new.many(i % 2) } }
t("return super from a block") { |i| C17.new.r(i % 2) }
t("missing keyword through super") { |i| guard2 { C17.new.kw(i % 2) } }
t("unknown keyword through super") { |i| guard2 { D17.new.kw(i % 2) } }
module M17; def f(x) = [:m, super]; end
class X17a; def f(x) = [:x, x]; end
class Y17a; def f(x) = [:y, x]; end
class X17 < X17a; include M17; end
class Y17 < Y17a; include M17; end
t("one module's super site, two classes in turn") { |i| (i.odd? ? X17 : Y17).new.f(i % 2) }
class V17 < Array; def initialize(n) = super(n, 0); end
t("Array#initialize with a value through super") { |i| V17.new(i % 3) }

# ---------------------------------------------------------------- 9. refinements

class P16; def f = :p16; end
class C16 < P16; def f = [:c16, super]; end
module R16
  refine P16 do
    def f = [:refined, super]
  end
  refine C16 do
    def g = [:g, f]
  end
end
t("refinement not in use, child super") { C16.new.f }
using R16
t("refinement active, super from refined") { P16.new.f }
t("refinement active, child super") { C16.new.f }
t("refinement active, refined calls f") { C16.new.g }
