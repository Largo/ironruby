/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

namespace IronRuby.Tests {
    public partial class Tests {
        // Core methods the method JIT's typed bodies compute themselves (Src/Ruby/Runtime/Jit):
        // each shape is made hot on the kind of receiver it is compiled for, then given the others,
        // and the core method is redefined in the middle of a run. Util/jit-matrix.rb has more.

        /// <summary>String#length and #size: characters in the String's encoding, not bytes or UTF-16 units.</summary>
        public void JitCore_StringLength1() {
            int specialized = IronRuby.Runtime.Jit.JitRuntime.Specialized;
            TestOutput(@"
class SlBox
  def initialize(s) = (@input = s; @index = 0)
  def more = @index < @input.length
  def len(s) = s.length
  def hash_of(s) = s.size * 1_402_589
  def slot(s) = hash_of(s) & 15
end
class SlString < String
  def length = 99
end
b = SlBox.new('abc')
200.times { |i| b.len('abcd'); b.hash_of('ab' * (i % 4)); b.slot('xyz'); b.more }
p [""héllo"", ""\xff\xfe"".dup.force_encoding('UTF-8'), ""\xff"".dup.force_encoding('BINARY'), ""abé"".encode('UTF-16LE'), ""\u{1F600}x"",
   SlString.new('abc'), [1, 2]].map { |s| [b.len(s), b.slot(s)] }
p((b.len(nil) rescue $!.class))
seen = []
200.times do |i|
  class String; remove_method :length; def length = -1; end if i == 100
  seen << b.len('abcd')
end
p seen.uniq
", @"
[[5, 1], [2, 10], [1, 13], [3, 7], [2, 10], [99, 7], [2, 10]]
NoMethodError
[4, -1]
");
            int specializedHere = IronRuby.Runtime.Jit.JitRuntime.Specialized - specialized;
            Assert(specializedHere >= 3, System.String.Format("methods specialized: {0} (expected at least 3)", specializedHere));
        }

        /// <summary>Integer#abs (an Int32's is an Int64, -2**63's a Bignum) and Float#abs (-0.0 is 0.0, NaN stays NaN).</summary>
        public void JitCore_Abs1() {
            int specialized = IronRuby.Runtime.Jit.JitRuntime.Specialized;
            TestOutput(@"
class AbBall
  def initialize(v) = (@v = v)
  attr_accessor :v
  def bounce = (@v = 0 - @v.abs)
  def ab(x) = x.abs
  def fab(x) = x.abs + 0.5
end
b = AbBall.new(3)
200.times { |i| b.v = i - 100; b.bounce; b.ab(i - 100); b.fab(i * -0.25) }
p [-2**31, -2**63, -2**64, 2**62, -5].map { |x| b.ab(x) }
p [-0.0, 0.0 / 0.0, -1.0 / 0.0].map { |x| b.fab(x) }, 1.0 / b.ab(-0.0)
b.v = -2**63
p b.bounce
p((b.ab(nil) rescue $!.class))
seen = []
200.times do |i|
  class Integer; alias_method :ab_orig, :abs; def abs = 1000; end if i == 100
  seen << b.ab(-3)
end
p seen.uniq
", @"
[2147483648, 9223372036854775808, 18446744073709551616, 4611686018427387904, 5]
[0.5, NaN, Infinity]
Infinity
-9223372036854775808
NoMethodError
[3, 1000]
");
            int specializedHere = IronRuby.Runtime.Jit.JitRuntime.Specialized - specialized;
            Assert(specializedHere >= 3, System.String.Format("methods specialized: {0} (expected at least 3)", specializedHere));
        }

        /// <summary>`@storage == nil' on an Array: false, unless nil has to_ary, when Array#== asks nil == ary.</summary>
        public void JitCore_CollectionEqualsNil1() {
            int specialized = IronRuby.Runtime.Jit.JitRuntime.Specialized;
            TestOutput(@"
class EnVec
  def initialize(s) = (@storage = s)
  attr_accessor :storage
  def at(idx)
    return nil if @storage == nil || idx >= @storage.length
    @storage[idx]
  end
  def cap = @storage == nil ? 0 : @storage.size
end
class EnArray < Array
  def ==(o) = :en_array_eq
end
v = EnVec.new([1, 2, 3])
200.times { |i| v.at(i % 4); v.cap }
p [[4, 5], nil, { 0 => :h }, EnArray[1, 2], 'str'].map { |s| v.storage = s; [v.at(0), v.cap] }
v.storage = [1]
seen = []
200.times do |i|
  class NilClass; def to_ary = []; def ==(o) = o.is_a?(Array) || equal?(o); end if i == 100
  seen << v.cap
end
p seen.uniq
", @"
[[4, 2], [nil, 0], [:h, 1], [nil, 0], [""s"", 3]]
[1, 0]
");
            int specializedHere = IronRuby.Runtime.Jit.JitRuntime.Specialized - specialized;
            Assert(specializedHere >= 2, System.String.Format("methods specialized: {0} (expected at least 2)", specializedHere));
        }
        /// <summary>
        /// `a != nil' with `!=' made private or protected in Array or Hash, where the method is BasicObject's (the class
        /// keeps a forwarder whose visibility is the call's); an Int64 abs and -2**63; an attr_reader named abs.
        /// </summary>
        public void JitCore_Review1() {
            int specialized = IronRuby.Runtime.Jit.JitRuntime.Specialized;
            TestOutput(@"
def ne(a) = (a != nil)
def neh(h) = (h != nil)
def la(m, n)
  x = m * n
  x.abs
end
class V
  attr_reader :abs
  def initialize(a) = (@abs = a)
end
def va(v) = v.abs + 1
a = [1]
h = { 1 => 2 }
v = V.new(3)
200.times { |i| ne(a); neh(h); la(i + 1, -3_000_000_000); va(v) }
seen = []
200.times do |i|
  class Array; private :!=; end if i == 100
  seen << (ne(a) rescue $!.class)
end
p seen.uniq
class Hash; protected :!=; end
p((neh(h) rescue $!.message))
p [la(7, -3_000_000_000), la(-2**62, 2), va(v), va(V.new(-1.5)), va(-4)]
", @"
[true, NoMethodError]
""protected method '!=' called for an instance of Hash""
[21000000000, 9223372036854775808, 4, -0.5, 5]
");
            int specializedHere = IronRuby.Runtime.Jit.JitRuntime.Specialized - specialized;
            Assert(specializedHere >= 4, System.String.Format("methods specialized: {0} (expected at least 4)", specializedHere));
        }

        /// <summary>
        /// `NONE == x', `NONE != x' with NONE a constant whose value is nil (Richards' NO_WORK): its storage holds
        /// ConstantSiteCache.WeakNull, unlike an autoload not yet run, and the comparison is `x is nil'; then
        /// NilClass#== redefined and the constant removed in the middle of a run.
        /// </summary>
        public void JitCore_NilConstant1() {
            int specialized = IronRuby.Runtime.Jit.JitRuntime.Specialized;
            TestOutput(@"
NONE = nil
class Q
  def initialize(h) = (@head = h)
  attr_accessor :head
  def eq = (NONE == @head)
  def ne = (NONE != @head)
end
class Base
  INH = nil
end
class Sub < Base
  def inh(x) = (INH == x)
end
q = Q.new(:a)
s = Sub.new
200.times { |i| q.head = i.even? ? :a : [i]; q.eq; q.ne; s.inh(nil) }
q.head = nil
p [q.eq, q.ne, s.inh(nil), s.inh(0)]
seen = []
200.times do |i|
  q.head = i.even? ? nil : :a
  if i == 100
    class NilClass
      alias_method :orig_eq, :==
      def ==(o) = :nil_eq
    end
  end
  seen << q.eq << q.ne
end
p seen.uniq
class NilClass
  remove_method :==, :orig_eq
end
seen = []
200.times do |i|
  Base.send(:remove_const, :INH) if i == 100
  seen << (s.inh(nil) rescue $!.class)
end
p seen.uniq
", @"
[true, false, true, false]
[true, false, :nil_eq]
[true, NameError]
");
            int specializedHere = IronRuby.Runtime.Jit.JitRuntime.Specialized - specialized;
            Assert(specializedHere >= 3, System.String.Format("methods specialized: {0} (expected at least 3)", specializedHere));
        }

        /// <summary>
        /// Float instance variables the typed code stores raw (RubyInstanceData, "Float locations"): `@x op= f',
        /// `@x = f' and `o.x = f' hot, then everything generic reads, copies, replaces or refuses the raw value.
        /// </summary>
        public void JitCore_FloatIVars1() {
            int specialized = IronRuby.Runtime.Jit.JitRuntime.Specialized;
            TestOutput(@"
class FiObj
  attr_accessor :v
  def initialize = (@x = 0.0; @v = 0.5)
  def add(d) = (@x += d)
  def put(d) = (@x = d * 1.0)
  def get = @x + 0.0
  def x = @x
end
def fi_attr(o, d) = (o.v = o.v * d)
o = FiObj.new
200.times { |i| o.put(i * 0.5); o.add(0.25); o.get; fi_attr(o, 1.0) }
o.put(2.5); fi_attr(o, 2.0)
p [o.x, o.v, o.instance_variable_get(:@x), o.instance_variables, o.x.equal?(o.x), o.x.object_id == o.x.object_id]
d = o.dup; d.add(1.0); fi_attr(d, 2.0)
p [o.x, o.v, d.x, d.v]
p Marshal.load(Marshal.dump(o)).x
o.put(-0.0); p [o.x, 1 / o.x]
o.instance_variable_set(:@x, nil); p((o.add(1.0) rescue $!.class))
o.instance_variable_set(:@x, 2); p [o.add(0.5), o.get]
o.put(1.5); p o.remove_instance_variable(:@x), o.instance_variables
o.put(3.5); o.freeze
p [(o.put(1.0) rescue $!.class), (o.add(1.0) rescue $!.class), (fi_attr(o, 3.0) rescue $!.class), o.x, o.v]
", @"
[2.5, 1.0, 2.5, [:@x, :@v], true, true]
[2.5, 1.0, 3.5, 2.0]
2.5
[-0.0, -Infinity]
NoMethodError
[2.5, 2.5]
1.5
[:@v]
[FrozenError, FrozenError, FrozenError, 3.5, 1.0]
");
            int specializedHere = IronRuby.Runtime.Jit.JitRuntime.Specialized - specialized;
            Assert(specializedHere >= 4, System.String.Format("methods specialized: {0} (expected at least 4)", specializedHere));
        }

        // A Float CRuby holds as an immediate has the object_id its value gives (RubyUtils.TryGetFlonumId): the
        // same for every Float of that value, where an id per box differed between two reads of one variable
        // that typed code had stored raw. Outside the flonum range (-0.0, 1.0e308) it stays per object.
        public void FloatObjectId1() {
            TestOutput(@"
p 1.5.object_id, 0.0.object_id, (-1.5).object_id
a = 1.5; b = 0.5 * 3
p a.object_id == b.object_id, (-0.0).object_id == (-0.0).object_id, 1.0e308.object_id == 1.0e308.object_id
x = 1.0e308; p x.object_id == x.object_id
", @"
-18014398509481982
-9223372036854775806
-18014398509481978
true
false
false
true
");
        }
    }
}
