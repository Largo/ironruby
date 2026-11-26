# Expression matrix for creating Struct objects and Struct classes (LEDGER gaps 24 and 19,
# perf/struct): keyword_init true, false and nil, missing and unknown keywords, too many
# arguments, subclasses whose initialize calls super, redefined new and initialize, a
# refinement, a Struct made in a thread, and what a new class answers.
#
#   ruby   Util/struct-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/struct-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# Every line is "<source> => <result>" or "<source> !! <ExceptionClass>: <message>". Object
# ids are taken out of inspect. Each expression runs twice and then in a loop of 200, so a
# path that only a warm call site takes is probed too; a line says so when the warm answer
# differs from the first.

KT = Struct.new(:a, :b, keyword_init: true)
KF = Struct.new(:a, :b, keyword_init: false)
KN = Struct.new(:a, :b)
K1 = Struct.new(:a, keyword_init: 1)
KE = Struct.new(keyword_init: true)
class SubKT < KT; end
class SubKN < KN; end
class InitKT < KT
  def initialize(a:, b: 20)
    super(a: a * 10, b: b)
  end
end
class InitPos < KN
  def initialize(*args, **kw)
    super
    self.b ||= :default
  end
end
class InitHashKT < KT
  def initialize(h)
    super(h)
  end
end
class NewKT < KT
  def self.new(*args, **kw)
    o = super
    o.a = [:via_new, o.a]
    o
  end
end
class InitLate < KT; end
class InitLate
  def initialize(**kw)
    super(**kw.transform_values { |v| v.to_s })
  end
end
class FrozenKT < KT
  def initialize(**kw)
    super
    freeze
  end
end
module RefineKT
  refine KT do
    def a = :refined
  end
end
module UsingRefine
  using RefineKT
  def self.read = KT.new(a: 1).a
end
KTH = Thread.new { Struct.new(:x, :y, keyword_init: true) }.value
KNH = Thread.new { Struct.new(:x, :y) }.value
HD = Hash.new(:dflt).merge(a: 1)

EXPRESSIONS = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  # --- keyword_init: true
  KT.new(a: 1, b: 2).to_a
  KT.new(a: 1).to_a
  KT.new(b: 2).to_a
  KT.new.to_a
  KT.new(**{}).to_a
  KT.new({a: 1, b: 2}).to_a
  KT.new({a: 1}).to_a
  KT.new({"a" => 1})
  KT.new(c: 1)
  KT.new(a: 1, c: 1, d: 2)
  KT.new("a" => 1)
  KT.new(1)
  KT.new(1, 2)
  KT.new(1, a: 2)
  KT.new({a: 1}, {b: 2})
  KT.new(**HD).to_a
  KT.new(b: 2, a: 1).to_h
  KT.new(a: 1, b: 2) == KT.new(b: 2, a: 1)
  KT.new(a: 1, b: 2).inspect
  KT.new(a: [1], b: {c: 1}).to_h
  KT.keyword_init?
  KT.members
  KT.inspect.sub(/0x\h+/, "XX")
  KT.new(a: 1).frozen?
  KT.new(a: 1).freeze.frozen?
  (s = KT.new(a: 1).freeze; s.a = 2)
  (s = KT.new(a: 1); s.send(:initialize, b: 3); s.to_a)
  (s = KT.new(a: 1).freeze; s.send(:initialize, b: 3))
  (h = {a: 1}; s = KT.new(**h); h[:a] = 9; s.a)
  (k = {a: {x: 1}}; s = KT.new(**k); s.a.equal?(k[:a]))
  KE.new.to_a
  KE.new(a: 1)
  K1.new(a: 1, b: 2).to_a
  K1.keyword_init?
  K1.new(1)
  # --- keyword_init: false
  KF.new(a: 1, b: 2).to_a
  KF.new(1, 2).to_a
  KF.new(1, b: 2).to_a
  KF.new(1, 2, 3)
  KF.keyword_init?
  # --- keyword_init: nil (the default): both
  KN.new(a: 1, b: 2).to_a
  KN.new(a: 1).to_a
  KN.new(c: 3)
  KN.new(1, 2).to_a
  KN.new(1).to_a
  KN.new(1, b: 2).to_a
  KN.new({a: 1, b: 2}).to_a
  KN.new(1, 2, 3)
  KN.new("a" => 1).to_a
  KN.new(**{}).to_a
  KN.keyword_init?
  KN.new(a: 1, b: 2).inspect
  # --- subclasses
  SubKT.new(a: 1, b: 2).to_a
  SubKT.new(c: 1)
  SubKT.new(1, 2)
  SubKT.keyword_init?
  SubKN.new(a: 1, b: 2).to_a
  SubKN.new(1, 2).to_a
  SubKN.keyword_init?
  InitKT.new(a: 1).to_a
  InitKT.new(a: 1, b: 2).to_a
  InitKT.new(b: 1)
  InitPos.new(1).to_a
  InitPos.new(a: 1).to_a
  InitPos.new(1, 2).to_a
  InitHashKT.new({a: 1, b: 2}).to_a
  InitHashKT.new(a: 5).to_a
  NewKT.new(a: 1, b: 2).to_a
  InitLate.new(a: 1, b: 2).to_a
  FrozenKT.new(a: 1).frozen?
  FrozenKT.new(a: 1).to_a
  UsingRefine.read
  KT.new(a: 1).a
  KTH.new(x: 1, y: 2).to_a
  KTH.new(z: 1)
  KNH.new(x: 1).to_a
  KNH.new(1, 2).to_a
  Thread.new { KT.new(a: 1, b: 2).to_a }.value
  # --- making a class
  Struct.new(:a).new(1).a
  Struct.new(:a).members
  (Struct.send(:remove_const, :Abc) if Struct.const_defined?(:Abc, false); Struct.new("Abc", :a).name)
  Struct.new(:a, :a)
  Struct.new(:a=)
  Struct.new(:a, keyword_init: true, x: 1)
  Struct.new(:a, foo: 1, bar: 2)
  Struct.new(:a) { def x = a * 2 }.new(3).x
  Struct.new(:a, :b).new(1, 2, 3)
  Struct.new(:a).new(1).frozen?
  Struct.new(:a).singleton_methods.sort
  Struct.new(:a, keyword_init: true).singleton_methods.sort
  Struct.new(:a).instance_methods(false).sort
  Struct.new(:a, :b, keyword_init: true).inspect.sub(/0x\h+/, "XX")
  (c = Struct.new(:a); c.new(1) == c.new(1))
  (c = Struct.new(:a); c[5].a)
  (c = Struct.new(:a); d = Class.new(c); d.new(1).a)
  (c = Struct.new(:a); def c.new(*) = :mine; c.new(1))
  (c = Struct.new(:a); c.define_method(:initialize) { |*a| super(*a.map { _1 * 2 }) }; c.new(4).a)
  (c = Struct.new(:a, keyword_init: true); c.define_method(:initialize) { |**k| super(**k.transform_values { _1 + 1 }) }; c.new(a: 4).a)
  (c = Struct.new(:a); c.class_eval { def a = 42 }; c.new(1).a)
  (c = Struct.new(:a); s = c.new(1); c.class_eval { undef_method :a }; s.a)
  (c = Struct.new(:a); c.new(1).respond_to?(:a=))
  Struct.new(:a).new(1).hash == Struct.new(:a).new(1).hash
RUBY

def clean(v) = v.inspect.gsub(/0x\h+/, "XX")
def run(source)
  "=> #{clean(eval(source))}"
rescue Exception => e
  "!! #{e.class}: #{e.message.gsub(/0x\h+/, "XX")}"
end

EXPRESSIONS.each do |source|
  first = run(source)
  second = run(source)
  warm = nil
  200.times { warm = run(source) }
  line = "#{source} #{first}"
  line += "   [second: #{second}]" if second != first
  line += "   [warm: #{warm}]" if warm != first
  puts line
end
puts "done"
