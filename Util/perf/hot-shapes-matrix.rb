# Differential matrix for the fast paths of calls and blocks: methods that run without a
# scope until a call needs one, the dispatcher chain of a polymorphic site, the JIT's
# `s == "lit"`, final `C.new` and final element store, and the reused Proc of a block
# literal that is only yielded to. Each shape runs hot (20,000 iterations, past the JIT
# thresholds) while what must invalidate it happens in the middle: a method redefined, a
# singleton method added to the cached receiver, String#== replaced and restored,
# `initialize` redefined, a new subclass method, a frozen Array, a block kept as a proc.
#
#   ruby Util/perf/hot-shapes-matrix.rb > cruby.txt
#   IR_CONFIG=Release ./ir.sh Util/perf/hot-shapes-matrix.rb > ir.txt
#   IR_CONFIG=Release ./ir.sh -X:NoJIT -X:NoOSR Util/perf/hot-shapes-matrix.rb > ir-nojit.txt
#
# All three outputs must be identical. Add a shape here with every new shortcut.
class A
  attr_accessor :v
  def initialize(v = 1); @v = v; @arr = [1, 2, 3]; @h = {}; end
  def getter; v; end
  def cmp(o); @v < o; end
  def y1; yield @v.abs; end                 # light method with a call that yields
  def y2; yield v; end
  def keep; @blk = y3 { |x| x + 1 }; end
  def y3; yield 1; end
  def bg; block_given? ? yield : :none; end
  def b; binding; end
  def lv(a); local_variables; end
  def prot(o); o.p1; end
  def priv(o); o.p2 rescue :nomethod; end
  def mm(o); o.nosuch(1); end
  def last_new; A.new(7); end
  def seq(s); s == "abc"; end
  def store(i); @arr[i] = i; end
  def hstore(k); @h[k] = k; end
  def rec(n); n == 0 ? 0 : 1 + rec(n - 1); end
  def bt; raise "x"; end
  def call_bt; bt; end
  def cl; caller(0, 2).map { |l| l[/`.*|'.*/] }; end
  def call_cl; cl; end
  protected def p1; :prot; end
  private def p2; :priv; end
  def method_missing(n, *a); n == :nosuch ? [:mm, a] : super; end
  def respond_to_missing?(*) = true
end
class B < A; def getter; :b; end; end
class C < A; def getter; :c; end; end
out = []
a = A.new(3); objs = [A.new, B.new, C.new, A.new(5)]
procs = []
20_000.times do |i|
  o = objs[i % 4]
  r = [a.getter, o.getter, a.cmp(i % 7), a.y1 { |x| x * 2 }, a.y2 { |x| procs << proc { x } if i % 5000 == 0; x },
       a.bg, a.bg { 1 }, a.prot(o), a.priv(o), a.mm(o), a.last_new.v, a.seq("abc"), a.seq(:abc), a.seq("abd"),
       a.store(i % 5), a.hstore(i % 3), a.lv(1), a.b.local_variables, a.b.receiver.equal?(a)]
  out << r if i % 3333 == 0
  case i
  when 5000 then class A; def getter; :redefined; end; end
  when 7000 then def a.getter; :singleton; end
  when 9000 then class String; alias old_eq ==; def ==(o); o.is_a?(String) && old_eq(o) ? :yes : false; end; end
  when 11000 then class String; alias == old_eq; end
  when 12000 then class A; def initialize(v = 1); @v = v * 2; @arr = []; @h = {}; end; end
  when 13000 then B.class_eval { def cmp(o); :bcmp; end }; objs << B.new
  when 15000 then a.instance_variable_get(:@arr).freeze
  end
rescue => e
  out << [i, e.class, e.message]
  a.instance_variable_set(:@arr, [1, 2, 3]) if FrozenError === e
end
out.each { |r| p r }
p procs.map(&:call)
p a.keep, a.rec(500)
begin; a.call_bt; rescue => e; p e.backtrace.first(3).map { |l| l[/[`'].*/] }; end
p a.call_cl
t = (1..4).map { |k| Thread.new { s = 0; 20_000.times { |i| s += a.y3 { |x| x + k } }; s } }.map(&:value)
p t
def deep(n); deep(n + 1); end
begin; deep(0); rescue SystemStackError => e; p e.class; end
e = [1, 2, 3].each
p a.y3 { |x| e.next + x }, a.y3 { |x| Fiber.new { Fiber.yield x; 9 }.resume }

# Blocks whose body makes calls, or reads the enclosing scope's locals, run without a scope until a call
# needs one, and a literal given to an iterator that only yields (Array#each, Integer#times, Range#each,
# map, select, Hash#each) reuses its Proc and BlockParam within a frame: a method called from the block
# starts to read the caller's binding, protected and missing methods, a binding or proc taken in the
# block on one call of the iterator and used after later calls, yield, break and return in such a block.
class D
  def initialize; @k = 2; end
  def q(e); e + @k; end
  protected def pr(e); e; end
  def vec(i); r = 0; (0..2).each { |j| r += yield j }; return r if i.odd?; [1, 2].each { |e| return -e if yield(e) == :no }; r; end
  def run(n, other)
    s = 0; kept = []; seen = -> { s }
    i = 0
    while i < n
      [1, 2].each { |e| r = q(e); s += r.is_a?(Binding) ? r.local_variable_get(:e) * 100 : r }
      3.times { |e| s += other.pr(e) + @k }
      (0..1).each { |e| kept << binding if i % 4000 == 7 && e == 1 }
      s += [3, 4].map { |e| e * seen.call % 7 }.sum
      {a: 1}.each { |k, v| s += v if k == :a }
      s += vec(i) { |x| kept << binding if i % 4000 == 11 && x == 2; break 100 if i % 3000 == 5 && x == 1; x }
      [5].select { |e| kept << proc { e + i } if i % 5000 == 3; e.odd? }
      case i
      when 6000 then D.class_eval { define_method(:q) { |e| binding } }
      when 9000 then D.class_eval { def q(e); nosuch(e); end; def method_missing(n, *a) = n == :nosuch ? a[0] * 3 : super }
      when 12000 then D.class_eval { def q(e); e - 1; end }
      end
      i += 1
    end
    [s, kept.map { |b| b.is_a?(Binding) ? [b.local_variable_get(b.local_variable_defined?(:e) ? :e : :x), b.local_variables.sort] : b.call }]
  end
end
p D.new.run(15_000, D.new)
t = (1..4).map { |k| Thread.new { s = 0; 20_000.times { |i| [k, i].each { |e| s += e.succ } }; s } }.map(&:value)
p t
