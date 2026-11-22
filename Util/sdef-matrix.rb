# Differential matrix for singleton classes and methods defined at run time: what a hot call site answers
# before and after each event that changes the methods its receiver's class has (a singleton method on an
# object a site has cached, on a fresh object, extend, include into a singleton class, Class.new with
# methods, define_method, remove_method, undef_method, alias_method, private, module_function, a
# refinement), respond_to? sites that see many singleton classes, OpenStruct used as applications use it,
# and two threads defining singleton methods while a third site calls through them. Run under CRuby and
# under ./ir.sh - with the JIT on, off, and without light frames - and diff:
#
#   ruby Util/sdef-matrix.rb > /tmp/mri.txt; echo $?
#   ./ir.sh Util/sdef-matrix.rb > /tmp/jit.txt; echo $?
#   ./ir.sh -X:NoJIT -X:NoOSR Util/sdef-matrix.rb > /tmp/nojit.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/sdef-matrix.rb > /tmp/nolight.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt && diff /tmp/mri.txt /tmp/nolight.txt
#
# All must exit 0, end with the row `end', and be identical. The JIT's state goes to stderr.

require 'ostruct'
$stdout.sync = true

# Calls the block n times (a hot site), and prints the answers it gave as runs: [answer, how many].
def runs(n = 200)
  out = []
  n.times do |i|
    v = yield(i)
    if out.last && out.last[0] == v then out.last[1] += 1 else out << [v, 1] end
  end
  out
end

def row(label, value)
  puts "#{label}: #{value.inspect}"
end

def call_x(o) = o.x
def call_y(o) = (o.y rescue $!.class)
def call_priv(o) = (o.z rescue $!.class)

# ---------------------------------------------------------------- 1. a site that cached o.x, then def o.x

class C1; def x = :class; end
o = C1.new
row "def o.x after cache", runs { |i| (def o.x = :single) if i == 100; call_x(o) }
o2 = C1.new
row "other object untouched", runs { call_x(o2) }
o3 = C1.new
row "define_singleton_method", runs { |i| o3.define_singleton_method(:x) { :dsm } if i == 50; call_x(o3) }
o4 = C1.new
row "redefine singleton repeatedly", runs { |i| (o4.define_singleton_method(:x) { i / 40 }) if i % 40 == 0; call_x(o4) }
o5 = C1.new; def o5.x = :s5
row "remove singleton method", runs { |i| o5.singleton_class.send(:remove_method, :x) if i == 70; call_x(o5) }
o6 = C1.new
row "undef in singleton", runs { |i| o6.singleton_class.send(:undef_method, :x) if i == 70; (call_x(o6) rescue $!.class) }
o7 = C1.new
row "singleton_class then def", runs { |i| o7.singleton_class if i == 20; (def o7.x = :late) if i == 120; call_x(o7) }

# ---------------------------------------------------------------- 2. fresh objects through one site

class C2; def x = 1; end
row "fresh singleton each call", runs { |i| f = C2.new; f.define_singleton_method(:x) { i % 3 } if i.odd?; call_x(f) }.size
row "fresh def each call", runs(100) { |i| f = C2.new; (def f.x = 7) if i % 4 == 0; call_x(f) }.map(&:first).tally
row "fresh singleton_class only", runs { |i| f = C2.new; f.singleton_class; call_x(f) }

# ---------------------------------------------------------------- 3. extend, include into a singleton, Class.new

module M3; def x = :m3; def y = :m3y; end
module N3; def x = :n3; end
c = C1.new
row "extend", runs { |i| c.extend(M3) if i == 60; [call_x(c), call_y(c)] }
d = C1.new
row "include into singleton", runs { |i| d.singleton_class.include(N3) if i == 60; call_x(d) }
e = C1.new; e.extend(M3)
row "extend then def", runs { |i| (def e.x = :own) if i == 100; call_x(e) }
row "Class.new with methods", runs { |i| k = Class.new(C1) { define_method(:x) { i % 2 } if i > 100 }; call_x(k.new) }.map(&:first).tally

# ---------------------------------------------------------------- 4. definitions in the class

class C4; def x = 1; end
a = C4.new; s = C4.new; def s.y = :sy
row "define_method in class", runs { |i| C4.define_method(:x) { 2 } if i == 50; [call_x(a), call_x(s)] }
row "alias_method", runs { |i| C4.alias_method(:y, :x) if i == 50; call_y(a) }
row "singleton over alias", runs { call_y(s) }
row "remove_method in class", runs { |i| C4.send(:remove_method, :y) if i == 50; call_y(a) }
class C4; def z = :z; end
row "private", runs { |i| C4.send(:private, :z) if i == 50; call_priv(a) }
row "public in singleton", runs { |i| s.singleton_class.send(:public, :z) if i == 50; [call_priv(s), call_priv(a)] }

module MF; def mf = :mf; end
row "module_function", runs { |i| MF.send(:module_function, :mf) if i == 50; (MF.mf rescue $!.class) }

# ---------------------------------------------------------------- 5. a refinement

class C5; def x = :plain; end
module R5; refine(C5) { def x = :refined }; end
f5 = C5.new; def f5.x = :single
module Use5
  using R5
  def self.go(o) = o.x
end
row "refined vs singleton", runs { |i| o = i.even? ? C5.new : f5; Use5.go(o) }.size
row "refined fresh singleton", runs { |i| o = C5.new; o.singleton_class if i.odd?; Use5.go(o) }

# ---------------------------------------------------------------- 6. respond_to? through many singleton classes

def rt2(o, n) = o.respond_to?(n, true)
def rt1(o, n) = o.respond_to?(n)
def rtl(o) = o.respond_to?(:x)
class C6; def x = 1; private def pv = 2; end
row "respond_to?(n, true) fresh", runs { |i| o = C6.new; o.define_singleton_method(:q) { } if i % 3 == 0; [rt2(o, :q), rt2(o, :pv), rt2(o, :x)] }
row "respond_to?(n) fresh", runs { |i| o = C6.new; o.define_singleton_method(:q) { } if i % 3 == 0; [rt1(o, :q), rt1(o, :pv)] }
row "respond_to?(:x) literal", runs { |i| o = C6.new; o.singleton_class.send(:undef_method, :x) if i % 5 == 0; rtl(o) }
class C6M; def respond_to_missing?(n, p = false) = n == :ghost; end
row "respond_to_missing? mix", runs { |i| o = i.even? ? C6.new : C6M.new; o.singleton_class; [rt2(o, :ghost), rt1(o, :ghost)] }
class C6R; def respond_to?(n, p = false) = :custom; end
row "custom respond_to? mix", runs { |i| o = i % 3 == 0 ? C6R.new : C6.new; def o.w = 1 if i.odd?; rt2(o, :w) }
row "send on fresh singletons", runs { |i| o = C6.new; o.define_singleton_method(:q) { i % 2 }; o.send(:q) }

# ---------------------------------------------------------------- 7. OpenStruct

os = OpenStruct.new(a: 1, b: 2, c: 3)
row "os.to_h", os.to_h
row "os ==", [os == OpenStruct.new(a: 1, b: 2, c: 3), os == OpenStruct.new(a: 1)]
row "os.dig", OpenStruct.new(a: { b: [1, 2] }).dig(:a, :b, 1)
row "each_pair", os.each_pair.to_a
row "respond_to?", [os.respond_to?(:a), os.respond_to?(:a=), os.respond_to?(:zz), os.respond_to?(:class)]
os.d = 4
row "setter later", [os.d, os.respond_to?(:d), os.to_h]
row "delete_field", [os.delete_field(:d), os.respond_to?(:d), (os.d rescue $!.class), os.to_h]
dd = os.dup; dd.a = 100
row "dup", [os.a, dd.a]
row "Marshal", Marshal.load(Marshal.dump(os)).to_h
row "protected names", OpenStruct.new(class: 1, object_id: 2, method: 3).then { |x| [x[:class], x.class, x[:method]] }
row "os hot", runs(300) { |i| OpenStruct.new(a: i % 2, b: 2, c: 3).then { |x| [x.a, x.b, x.respond_to?(:c), x.to_h.size] } }
row "os subclass", Class.new(OpenStruct) { def a = :mine }.new(a: 1, b: 2).then { |x| [x.a, x.b, x[:a]] }

# ---------------------------------------------------------------- 8. threads

class C8; def x = :base; end
hot = C8.new
stop = false
bad = []
t1 = Thread.new { 3000.times { |i| o = C8.new; o.define_singleton_method(:x) { i }; bad << :t1 unless o.x == i } }
t2 = Thread.new do
  until stop
    v = call_x(hot)
    bad << [:t2, v] unless v == :base || v.is_a?(Integer)
  end
end
t3 = Thread.new { 300.times { |i| hot.define_singleton_method(:x) { i }; bad << :t3 unless call_x(hot) == i; Thread.pass } }
t1.join; t3.join; stop = true; t2.join
row "threads", [bad.first(3), call_x(hot)]

$stderr.puts "jit=#{defined?(IronRuby) ? 'ironruby' : 'cruby'}"
puts "end"
