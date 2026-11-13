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

# Hot `@x` reads and writes (inlined shape check and slot access) and attr_reader/attr_writer
# calls while the receiver's shape changes under the site: a variable added, one removed, the
# object frozen, a singleton class, and objects of other classes with another slot order.
class IV
  attr_reader :x; attr_writer :y
  def initialize; @x = 1; @y = 2; end
  def rd; [@x, @y, @z]; end
  def wr(v); @x = v; @y = v + 1; end
  def via(o); o.y = 5; o.x; end
end
class IV2 < IV; def initialize; @y = 20; @q = 0; @x = 10; end; end
ivo = IV.new; others = [IV2.new, IV.new]; ivout = []
20_000.times do |i|
  o = i.odd? ? ivo : others[i % 2]
  begin
    o.wr(i)
    ivout << [i, o.rd, ivo.via(o), o.instance_variables] if i % 2500 == 1 || (i > 14_990 && i < 15_010 && i.odd?)
  rescue => e
    ivout << [i, e.class, e.message[/can.t modify frozen IV/]] if i < 15_010
  end
  case i
  when 3001 then ivo.instance_variable_set(:@z, :added)
  when 5001 then ivo.remove_instance_variable(:@x)
  when 7001 then def ivo.extra; end
  when 9001 then ivo.instance_variable_set(:@x, :back)
  when 11001 then 30.times { |k| ivo.instance_variable_set(:"@v#{k}", k) }
  when 15001 then ivo.freeze
  end
end
ivout.each { |r| p r }
p ivo.rd, others.map(&:rd)
