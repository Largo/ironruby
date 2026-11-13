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
  def aset(o, x); o.v = x; @h[:k] = "s#{x}"; o.v; end   # attribute and element assignments, interpolation
  def interp(o); "<#{o}|#{@v}>"; end
  def kase(x); case x when Integer then :int when "a", :b then "ab" else [x, :other] end; end
  def loop1(n); i = 0; t = 0; while i < n; t += getter == :c ? 1 : i; i += 1; end; t; end
  def loop2(n); i = 0; while i < n; i += 1; local_variables.size if i == 2; end; [i, binding.local_variable_get(:i)]; end
  def loop3(n); i = 0; t = 0; until i >= n; t += K; i += 1; end; t; end
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
K = 1
class Wrap; def initialize(x); @x = x; end; def to_s; "w#{@x}"; end; end
class B < A; def getter; :b; end; end
class C < A; def getter; :c; end; end
out = []
a = A.new(3); objs = [A.new, B.new, C.new, A.new(5)]
procs = []
20_000.times do |i|
  o = objs[i % 4]
  r = [a.getter, o.getter, a.cmp(i % 7), a.y1 { |x| x * 2 }, a.y2 { |x| procs << proc { x } if i % 5000 == 0; x },
       a.bg, a.bg { 1 }, a.prot(o), a.priv(o), a.mm(o), a.last_new.v, a.seq("abc"), a.seq(:abc), a.seq("abd"),
       a.store(i % 5), a.hstore(i % 3), a.lv(1), a.b.local_variables, a.b.receiver.equal?(a),
       a.aset(o, i % 9), a.interp(Wrap.new(i % 4)), a.kase(i % 3 == 0 ? i : (i % 3 == 1 ? "a" : 1.5)), a.loop1(3), a.loop2(4),
       (i % 2000 == 0 ? a.loop3(5000) : 0)]
  out << r if i % 3333 == 0
  case i
  when 5000 then class A; def getter; :redefined; end; end
  when 7000 then def a.getter; :singleton; end
  when 9000 then class String; alias old_eq ==; def ==(o); o.is_a?(String) && old_eq(o) ? :yes : false; end; end
  when 11000 then class String; alias == old_eq; end
  when 12000 then class A; def initialize(v = 1); @v = v * 2; @arr = []; @h = {}; end; end
  when 13000 then B.class_eval { def cmp(o); :bcmp; end }; objs << B.new
  when 15000 then a.instance_variable_get(:@arr).freeze
  when 16000 then Wrap.class_eval { def to_s; "W#{@x}"; end }; Integer.class_eval { alias old_eqq ===; def ===(o); o == 1.5 || old_eqq(o); end }
  when 17000 then Object.send(:remove_const, :K); Object.const_set(:K, 2); A.class_eval { def getter; :c; end }
  when 18000 then Integer.class_eval { alias === old_eqq }
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

# ---- blocks with loops, case, attribute and element assignment and literals in them ----
lambda do
  class Q
    attr_accessor :n, :a
    def initialize; @n = 0; @a = [0, 0, 0]; end
    def kind(x); case x when Integer then :i when String then "s#{x}" else :o end; end
  end
  q = Q.new; out = []
  kept = []
  30_000.times do |i|
    r = [1, 2, 3].map do |e|
      k = 0
      while k < e; k += 1; q.n += 1; break if k > 2 && i == 15_000; end
      q.a[e - 1] = k
      v = case e when 1 then :one when 2 then "two#{k}" else q.kind(e) end
      kept << binding if i == 10_000 && e == 2
      kept << proc { [k, e] } if i == 20_000 && e == 3
      next :skipped if i == 12_000 && e == 1
      [v, k, q.a.dup, "x" == v, v == :one]
    end
    out << r if i % 4999 == 0 || i == 12_000 || i == 15_000
    case i
    when 7000 then class Q; def kind(x); :redefined; end; end
    when 9000 then class Integer; alias old_lt <; def <(o); old_lt(o); end; end
    when 14_000 then class Integer; alias < old_lt; end
    when 21_000 then q.a.freeze
    end
  rescue => ex
    out << [i, ex.class, ex.message]; q.a = [0, 0, 0]
  end
  out.each { |r| p r }
  p q.n, kept[0].local_variables.sort, kept[0].local_variable_get(:k), kept[1].call
  p (1..4).map { |t| Thread.new { s = 0; 5000.times { [1, 2].each { |e| j = 0; while j < e; j += 1; s += t; end } }; s } }.map(&:value)
  p [1, 2, 3].each { |e| x = 0; x += 1 while x < e; break x * 10 if e == 2 }
  def m; [1, 2, 3].each { |e| i = 0; while i < 5; i += 1; return [e, i] if i == 3 && e == 2; end }; end
  p m
end.call
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
# Attribute calls that a rule serves (not a dispatcher: send, a splat, a keyword hash to a
# writer), with the reader redefined as a method, a singleton reader added and the variable
# removed mid-run; and Array#== against non-arrays, whose respond_to?(:to_ary) symbol the
# storage caches, while to_ary and respond_to? are defined on the operand's class.
class AR; attr_accessor :a; def initialize; @a = 1; end; end
class EqOp; end
aro = AR.new; arout = []; arr = [1]; eqo = EqOp.new
20_000.times do |i|
  aro.send(:a=, i); aro.a = *[i]
  r = [aro.send(:a), aro.a(*[]), aro.public_send(:a), arr == nil, arr == eqo, arr == [1], [] == eqo]
  arout << [i, r] if i % 2500 == 1 || (i > 4_990 && i < 5_010) || (i > 9_990 && i < 10_010) || (i > 12_990 && i < 13_010)
  case i
  when 5000 then class AR; def a; [:method, @a]; end; end
  when 7000 then def aro.a; :singleton; end
  when 10000 then class EqOp; def to_ary; [1]; end; def ==(o); o == [1]; end; end
  when 11000 then aro.singleton_class.send(:remove_method, :a); aro.remove_instance_variable(:@a)
  when 13000 then class EqOp; def respond_to?(m, p = false); m == :to_ary ? false : super; end; end
  when 15000 then class NilClass; def to_ary; []; end; end
  end
end
arout.each { |r| p r }
class NilClass; remove_method :to_ary; end

# light-scope sites served by the fast dispatchers: a core method redefined and restored under
# a hot site, a receiver switching between Integer/Float/nil/String/a user object, private and
# protected targets on an explicit receiver, and a target that starts to need the caller's
# scope (block_given?, binding, $~) while the site is hot.
class LS
  def lt(a, b); a < b; end
  def nq(o); o.nil?; end
  def eq(o); o == 1; end
  def call1(o); o.t1(2); end
  def call0(o); o.t0; end
  def match(s); s =~ /b(c)/; $~ && $~[1]; end
  def p_on(o); o.pv rescue :nomethod; end
  def pr_on(o); o.pt rescue :nomethod; end
  def pt_from_self; pr_on(self); end
  def own; t0; end
  def t0; :t0; end
  protected def pt; :prot; end
  private def pv; :priv; end
end
class LT; def t1(x); x * 3; end; def t0; :lt0; end; def ==(o); :usereq; end; def nil?; :usernil; end; end
ls = LS.new; recv = [1, 2.5, nil, "s", LT.new]; lsout = []
20_000.times do |i|
  r = recv[i % 5]
  row = [ls.lt(i, 9_000), ls.nq(r), ls.eq(r), ls.call1(LT.new), ls.call0(i.odd? ? LT.new : ls),
         ls.match("abc#{i % 3}"), ls.p_on(ls), ls.pr_on(ls), ls.pt_from_self, ls.own]
  lsout << [i, row] if i % 2000 == 7 || (i > 9_995 && i < 10_010)
  case i
  when 4001 then class Integer; alias_method :__lt, :<; def <(o); :redef; end; end
  when 6001 then class Integer; alias_method :<, :__lt; end
  when 8001 then class NilClass; def nil?; :nilredef; end; end
  when 9001 then class NilClass; def nil?; true; end; end
  when 10001 then class LT; def t1(x); block_given? ? :blk : binding.local_variable_get(:x); end; end
  when 12001 then class LT; def t0; [__method__, caller(0, 1).size]; end; end
  when 14001 then class LS; public :pv; end
  when 16001 then def ls.t0; :single; end
  end
end
lsout.each { |r| p r }

# The entry and exit of a light frame inline the common case - no block, no alias call pending, no scope made: a call
# through an alias (__callee__ in a callee that makes its scope), a break from a literal block and a proc passed as the
# block, through hot light methods, with a TracePoint switched on for a while and the frames still reported right.
class LF
  def plain(o); o.nil?; end
  def callee_of; __callee__; end
  def via(o); callee_of; end
  alias via2 via
  alias cal2 callee_of
  def yld(x); yield x; end
  def brk(x); yld(x) { |v| break v * 2 }; end
  def ret(x); [1].each { |v| return v + x }; :none; end
end
lf = LF.new; lfout = []; pr = proc { |v| v + 1 }; calls = 0; tp = TracePoint.new(:call) { calls += 1 }
20_000.times do |i|
  row = [lf.plain(i), lf.via(i), lf.via2(i), lf.cal2, lf.callee_of, lf.brk(i), lf.yld(i, &pr), lf.ret(i)]
  lfout << [i, row] if i % 2500 == 3
  tp.enable if i == 10_000
  tp.disable if i == 10_100
end
lfout.each { |r| p r }
p calls > 0
