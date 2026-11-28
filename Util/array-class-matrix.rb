# Differential matrix for the class of an Array that is not an IRubyObject (a plain RubyArray): its singleton class,
# instance variables, object id, frozen state, dup and clone, as compiled code and call sites find them. An array no
# runtime has made instance data for is answered its class without the instance data table
# (RubyArray.MayHaveInstanceData), even while some other array - $LOAD_PATH - has a singleton class. Also what a Kernel
# function called with an implicit self resolves to. Run it under CRuby and under ./ir.sh, and diff:
#
#   ruby Util/array-class-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/array-class-matrix.rb > /tmp/ir.txt 2>&1
#   diff /tmp/mri.txt /tmp/ir.txt

def t(label)
  print label.ljust(52), ' => '
  begin
    puts yield.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message.gsub(/0x\h+/, '0x')}"
  end
end

class Probe
  def initialize(a) = @a = a
  def nil_p = @a.nil?
  def cls = @a.class
  def sc = @a.singleton_methods.sort
  def kind = [@a.is_a?(Array), Array === @a, @a.instance_of?(Array)]
  def eq(x) = @a == x
  def hello = @a.respond_to?(:hello) ? @a.hello : :none
end

# hot loops, so that the methods are compiled; then the receivers change under them
def hot(p, n = 3000) = n.times { p.nil_p; p.cls; p.kind; p.hello }

# ---------------------------------------------------------------- 1. a plain array, then one given a singleton class
a = [1, 2]
p1 = Probe.new(a)
hot(p1)
t('plain: nil?, class, kind, hello') { [p1.nil_p, p1.cls, p1.kind, p1.hello] }
def a.hello = :singleton
t('after def a.hello') { [p1.hello, p1.cls, p1.kind, p1.sc, a.singleton_class.instance_methods(false)] }
hot(p1)
t('hot again') { [p1.hello, p1.nil_p] }
def a.nil? = :mine
t('a singleton nil?') { [p1.nil_p, a.nil?] }
b = [3]
pb = Probe.new(b)
hot(pb)
t('another array, unaffected') { [pb.nil_p, pb.hello, pb.sc] }

# ---------------------------------------------------------------- 2. instance data without a singleton class
c = [4]
pc = Probe.new(c)
hot(pc)
c.instance_variable_set(:@iv, 5)
t('an instance variable') { [c.instance_variable_get(:@iv), c.instance_variables, pc.nil_p, pc.cls] }
d = [5]
id = d.object_id
t('object_id stays') { [d.object_id == id, ObjectSpace._id2ref(id).equal?(d)] rescue [d.object_id == id] }
e = [6].freeze
t('frozen') { [e.frozen?, (e << 1 rescue $!.class)] }
f = [7]
f.freeze
t('frozen later') { [f.frozen?, (f << 1 rescue $!.class), Probe.new(f).nil_p] }
g = [8]
g.instance_variable_set(:@x, 1)
g.freeze
t('ivar then frozen') { [g.frozen?, g.instance_variable_get(:@x), (g.instance_variable_set(:@y, 2) rescue $!.class)] }
h = [9]
h.extend(Module.new { def hello = :extended })
t('extend') { [Probe.new(h).hello, h.singleton_class.ancestors.size > Array.ancestors.size] }

# ---------------------------------------------------------------- 3. dup, clone
s = [1]
def s.hello = :s
s.instance_variable_set(:@v, 1)
t('clone keeps the singleton and ivars') { x = s.clone; [Probe.new(x).hello, x.instance_variable_get(:@v)] }
t('dup keeps ivars, not the singleton') { x = s.dup; [Probe.new(x).hello, x.instance_variable_get(:@v)] }
t('clone(freeze: true)') { x = [1].clone(freeze: true); [x.frozen?, Probe.new(x).nil_p] }
t('an Array subclass') { k = Class.new(Array); x = k.new; def x.hello = :sub; [Probe.new(x).hello, Probe.new(x).cls == k] }

# ---------------------------------------------------------------- 4. $LOAD_PATH, threads
t('$LOAD_PATH.resolve_feature_path') { [$LOAD_PATH.singleton_methods.include?(:resolve_feature_path), $LOAD_PATH.resolve_feature_path('erb')&.first] }
t('threads give arrays singleton classes') do
  arrays = Array.new(8) { [_1] }
  probes = arrays.map { Probe.new(_1) }
  ths = arrays.each_with_index.map { |x, i| Thread.new { 200.times { probes[i].hello }; x.define_singleton_method(:hello) { i }; probes[i].hello } }
  ths.map(&:value)
end
t('a singleton defined while another thread calls') do
  x = [0]
  px = Probe.new(x)
  th = Thread.new { r = nil; 20000.times { r = px.hello }; r }
  def x.hello = :late
  th.join
  px.hello
end

# ---------------------------------------------------------------- 5. Kernel functions called with an implicit self
class OwnArray
  def Array(x) = :own
  def call = Array([1])
end
class Missing
  def method_missing(m, *a) = m == :Zz ? :mm : super
  def call = Zz(1)
end
module RefineKernel
  refine Kernel do
    def Array(x) = :refined
  end
end
t('a class with its own Array') { OwnArray.new.call }
t('Kernel#Array is private') { Object.new.Array([1]) }
t('Kernel.Array') { Kernel.Array(nil) }
t('method_missing for a function call') { Missing.new.call }
t('BasicObject has no Array') { Class.new(BasicObject) { def call = Array(1) }.new.call }
t('Kernel#Array redefined, restored') do
  module Kernel; alias __saved_array__ Array; private def Array(x) = :redefined; end
  begin
    [Array(1), OwnArray.new.call]
  ensure
    module Kernel; alias Array __saved_array__; remove_method :__saved_array__; private :Array; end
  end
end
t('restored') { Array(nil) }
using RefineKernel
t('a refinement of Kernel#Array') { Array(1) }
