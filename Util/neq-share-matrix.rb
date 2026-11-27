# Differential matrix for `a != b' bound as one rule shared by receivers of several classes (gap 10,
# DispatchIntrinsics.IsSharableNotEquals, IR_SHARED_NEQ). Each row runs one `!=' site hot over objects of
# several plain classes, then changes what `!=' or `==' means for one of them - `==' defined on a class, on a
# module included later, on a singleton class; `!=' defined; method_missing; refinements; BasicObject
# subclasses; Comparable; Integer against Float; Struct - and runs the site again. Run under CRuby and ./ir.sh
# (JIT on and off, light frames off, the switch off) and diff:
#
#   ruby Util/neq-share-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/neq-share-matrix.rb > /tmp/ir.txt && diff /tmp/mri.txt /tmp/ir.txt
#   IR_SHARED_NEQ=0 ./ir.sh Util/neq-share-matrix.rb > /tmp/off.txt && diff /tmp/mri.txt /tmp/off.txt
#
# Every row must match, and the last line be `end'.

$stdout.sync = true
N = 3000

def row(name, value)
  puts "#{name}: #{value.inspect}"
end

def err
  yield
rescue Exception => e
  [e.class, e.message]
end

# one `!=' site, called over objs against other; the count of true answers
def neq_site(objs, other, n = N)
  c = 0
  n.times { |i| c += 1 if objs[i % objs.size] != other }
  c
end

# a second site, so that a rule shared between sites is exercised too
def neq_site2(objs, other, n = N)
  c = 0
  i = 0
  while i < n
    c += 1 if objs[i % objs.size] != other
    i += 1
  end
  c
end

def fresh(k) = (1..k).map { Class.new }

cs = fresh(8)
objs = cs.map(&:new)
row "1. plain, 1..8 classes", [1, 2, 3, 4, 8].map { |k| [neq_site(objs.first(k), objs[0]), neq_site2(objs.first(k), objs[1])] }

cs = fresh(4); objs = cs.map(&:new)
r = [neq_site(objs, objs[2])]
cs[2].class_eval { def ==(o) = true }
r << neq_site(objs, objs[3]) << neq_site2(objs, objs[0])
row "2. == defined on one class after the site bound", r

cs = fresh(4); objs = cs.map(&:new)
r = [neq_site(objs, objs[1])]
cs[1].class_eval { def !=(o) = :custom }
r << neq_site(objs, objs[1]) << (objs[1] != 5)
row "3. != defined on one class", r

cs = fresh(4); objs = cs.map(&:new)
r = [neq_site(objs, objs[0])]
m = Module.new { def ==(o) = o.is_a?(Integer) }
cs[0].include(m); cs[3].include(m)
r << neq_site(objs, 1) << neq_site(objs, objs[0]) << neq_site2(objs, 2)
row "4. == from a module included later", r

cs = fresh(4); objs = cs.map(&:new)
r = [neq_site(objs, objs[0])]
m = Module.new { def !=(o) = false }
cs[2].include(m)
r << neq_site(objs, objs[0]) << neq_site2(objs, objs[2])
row "5. != from a module included later", r

cs = fresh(3); objs = cs.map(&:new)
r = [neq_site(objs, objs[1])]
def (objs[1]).==(o) = true
r << neq_site(objs, :x) << neq_site2(objs, objs[0])
row "6. == on a singleton class", r

cs = fresh(3); objs = cs.map(&:new)
r = [neq_site(objs, objs[0])]
objs[0].singleton_class
r << neq_site(objs, objs[0])
row "6a. a singleton class without ==", r

class MM
  def method_missing(name, *args) = [:mm, name, args]
  def respond_to_missing?(*) = true
end
class MMU < MM
  undef_method :!=
end
cs = fresh(2); objs = cs.map(&:new) + [MMU.new, MM.new]
row "7. method_missing (undef !=)", [neq_site(objs.first(2), objs[0]), err { objs[2] != 1 }, neq_site(objs.values_at(0, 1, 3), objs[3])]

module RefEq
  refine(Object) { def ==(o) = true }
end
module RefNeq
  refine(Object) { def !=(o) = :refined }
end
class UsesRef
  using RefEq
  def self.neq(objs, other) = (c = 0; N.times { |i| c += 1 if objs[i % objs.size] != other }; c)
end
class UsesRefNeq
  using RefNeq
  def self.neq(objs, other) = (r = nil; N.times { |i| r = (objs[i % objs.size] != other) }; r)
end
cs = fresh(3); objs = cs.map(&:new)
row "8. refinements of == and !=", [UsesRef.neq(objs, objs[0]), UsesRefNeq.neq(objs, objs[0]), neq_site(objs, objs[0])]

b1 = Class.new(BasicObject); b2 = Class.new(BasicObject)
bo = [b1.new, b2.new]
row "9. BasicObject subclasses", [neq_site(bo, bo[0]), neq_site2(bo, bo[1])]
b1.class_eval { def ==(o) = true }
row "9a. == on a BasicObject subclass", [neq_site(bo, bo[1])]

row "10. Integer against Float", [neq_site([1, 2, 3], 1.0), neq_site([1.0, 2, 3.5], 1), neq_site2([1, 2**70, 3], 2**70)]

cmp = Class.new { include Comparable; attr_reader :v; def initialize(v) = (@v = v); def <=>(o) = v <=> o.v }
c1, c2 = cmp.new(1), cmp.new(1)
p1 = Class.new.new
row "11. Comparable's ==", [neq_site([c1, c2, p1], c1), neq_site2([p1, c2], c2)]

S = Struct.new(:a)
row "12. Struct and plain", [neq_site([S.new(1), S.new(1), Object.new], S.new(1))]

row "13. nil, true, String, Symbol and plain", [neq_site([nil, true, false, "a", :a, Object.new], nil), neq_site2(["a", "a".dup, Object.new], "a")]

cs = fresh(4); objs = cs.map(&:new)
r = [neq_site(objs, objs[0])]
cs[1].class_eval { private def ==(o) = true }
r << neq_site(objs, objs[1])
cs[3].class_eval { protected def ==(o) = true }
r << neq_site(objs, objs[3])
row "14. private and protected ==", r

cs = fresh(4); objs = cs.map(&:new)
r = [neq_site(objs, objs[0])]
cs[0].class_eval { def ==(o) = true }
cs[0].class_eval { remove_method :== }
r << neq_site(objs, objs[0])
Object.class_eval { def ==(o) = equal?(o) || o == :eq_anything }
r << neq_site(objs, :eq_anything) << neq_site2(objs, objs[1])
Object.class_eval { remove_method :== }
r << neq_site(objs, :eq_anything)
row "15. == defined then removed, and on Object", r

cs = fresh(6); objs = cs.map(&:new)
ths = 4.times.map { |t| Thread.new { neq_site(objs, objs[t]) + neq_site2(objs, objs[t + 1]) } }
row "16. threads", ths.map(&:value)

puts "end"
