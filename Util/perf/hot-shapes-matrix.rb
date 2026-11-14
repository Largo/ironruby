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
  # rows at the multiples of 2000 too, where loop3 runs: after K is replaced its result has to change
  out << r if i % 3333 == 0 || i % 2000 == 0
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
    # via runs on every iteration, on both receivers, so that its attribute sites are hot; rows at both parities
    read = o.rd; via = ivo.via(o)
    ivout << [i, read, via, o.instance_variables] if i % 2500 < 2 || (i > 14_990 && i < 15_010)
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

# Light methods and scope-free blocks that make calls keep their locals in CLR variables until a
# scope is made: a local written, then read after a call that starts to make the scope (eval,
# binding, local_variable_set through the binding), the method JIT off and on, and the call
# redefined mid-run so that the first scope appears with locals already assigned.
class RL
  def g(x); x + 1; end
  def m1(x); y = g(x); z = y * 2; [x, y, z]; end
  def m2(x); y = g(x); w = (x % 3 == 0 ? binding : nil); w&.local_variable_set(:y, -1); [y, w ? w.local_variables : 0]; end
  def m3(x); y = g(x); s = eval("y + 100"); y += 1; [y, s, defined?(y)]; end
  def m4(a); t = 0; a.each { |e| u = g(e); t += u; binding.local_variable_set(:u, 0) if e == 2; t += u }; t; end
end
rl = RL.new; rlout = []
20_000.times do |i|
  r = [rl.m1(i), rl.m2(i), rl.m3(i), rl.m4([1, 2, 3])]
  rlout << [i, r] if i % 2500 == 3 || (i > 9_998 && i < 10_003)
  case i
  when 5000 then RL.class_eval { def g(x); [x].sum + 1; end }
  when 10000 then RL.class_eval { def g(x); local_variables.size + x; end }
  when 15000 then RL.class_eval { def g(x); x + 1; end }
  end
end
rlout.each { |r| p r }

# Integer results past the DLR's box cache (shared boxes) and one-character slices of a String
# (a shared CLR string under a new String): identity, mutation and frozen-ness of each result,
# with Integer#+ and String#[] redefined and restored mid-run.
src = +"ab\ncd"; chout = []
20_000.times do |i|
  n = 40_000 + i; m = (n + 1) - 1; x = (n ^ 0x55) ^ 0x55; d = (n * 2) / 2; sh = (n << 1) >> 1
  c = src[i % 5]; c2 = src[i % 5]
  same = c.equal?(c2); c << "!" if i.odd?
  r = [m.equal?(n), x.object_id == n.object_id, d == sh, m.frozen?, c, c2, same, c.frozen?, src, (-1500 - i % 3) + 0]
  chout << [i, r] if i % 2500 == 1 || (i > 9_998 && i < 10_002)
  case i
  when 5000 then Integer.class_eval { alias_method :__plus, :+; def +(o); __plus(o); end }
  when 7000 then Integer.class_eval { alias_method :+, :__plus }
  when 10000 then String.class_eval { alias_method :__aref, :[]; def [](*a); __aref(*a).upcase; end }
  when 12000 then String.class_eval { alias_method :[], :__aref }
  end
end
chout.each { |r| p r }
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
# the JIT's identity calls on any receiver - `o.nil?', `o == x', `o != x', `o.equal?(x)' -
# at sites that see a plain object, nil, true, a Symbol, an Integer, a String, an object with its
# own == / nil?, a BasicObject; while Kernel#nil? and BasicObject#== are redefined and restored,
# a singleton method is added to the receiver, a subclass overrides, a module is prepended to
# Kernel, nil? is made private in a subclass.
class ID
  def nq(o); o.nil?; end
  def eq(o, x); o == x; end
  def ne(o, x); o != x; end
  def eql(o, x); o.equal?(x); end
  def eqs(o); o == self; end
  def nqo(o); o.nil?; end
  def eqo(o); o == self; end
end
class IDSub < ID; end
class IDOwn; def ==(o); :owneq; end; def nil?; :ownnil; end; end
class IDPriv; private def nil?; true; end; end
class IDBasic < BasicObject; def method_missing(n, *a); :"mm_#{n}"; end; end
module IDPre; def nil?; :prenil; end; end
id = ID.new; plain = Object.new; same = IDSub.new
idrecv = [plain, nil, true, :sym, 5, "s", IDOwn.new, IDBasic.new, same, 2**70, 1.5]
idout = []
20_000.times do |i|
  r = idrecv[i % idrecv.size]
  row = [id.nq(r), id.eq(r, r), id.eq(r, plain), id.ne(r, plain), id.ne(r, r), id.eql(r, r), id.eql(r, 5),
         id.nqo(plain), id.eqo(plain), id.eqo(id), (id.nqo(IDPriv.new) rescue :private), same.eqs(same)]
  # and the iterations right after each redefinition below (x001), one per receiver, while it is in force
  idout << [i, row.map { |x| x.inspect }] if i % 2000 == 7 || (i > 9_995 && i < 10_010) || (i % 1000).between?(2, 12)
  case i
  when 3001 then class Object; def nil?; :objnil; end; end
  when 4001 then class Object; remove_method :nil?; end
  when 5001 then class BasicObject; alias_method :__ideq, :==; def ==(o); :basiceq; end; end
  when 6001 then class BasicObject; alias_method :==, :__ideq; end
  when 7001 then def plain.nil?; :single; end; def plain.==(o); :singleeq; end
  when 8001 then class IDSub; def ==(o); :subeq; end; end
  when 9001 then Kernel.prepend(IDPre)
  when 11001 then class NilClass; def nil?; :nilredef; end; end
  when 12001 then class NilClass; remove_method :nil?; end
  when 13001 then class Object; def ==(o); :objeq; end; end
  when 14001 then class Object; remove_method :==; end
  end
end
idout.each { |r| p r }

# Proc#call and a yield in a block hand a BlockParam on from one call of the proc to the next
# (RubyOps.TakeBfcForProcCall, TakeBfcForBlockYield): recursion, a captured scope (binding,
# a proc made in the body), break/next/return, lambdas, threads, and a redefinition mid-run.
class PC
  def initialize; @keep = []; @f = proc { |a, b| a + b }; @g = proc { |n| n <= 1 ? 1 : n * @g.call(n - 1) }; end
  def run(i); [@f.call(i, 1), @g.call(i % 6)]; end
  def kept(i); pr = proc { |x| y = x * 2; @keep << binding if x % 4000 == 0; y }; [pr.call(i), pr.call(i + 1)]; end
  def kept_values; @keep.map { |b| b.local_variable_get(:y) }; end
  def nxt(i); pr = proc { |x| next :odd if x.odd?; x }; pr.call(i); end
  def brk(i); pr = proc { |x| break x }; pr.call(i); rescue LocalJumpError => e; [e.reason, e.exit_value]; end
  def lam(i); l = lambda { |x| return x * 3 }; l.call(i) + l.(1); end
  def each2; (0..1).each { |k| yield k }; end
  def ysum(i); s = 0; each2 { |k| s += k + i }; s; end
  def ykeep(i); r = nil; each2 { |k| r = binding if k == 1 && i % 5000 == 1 }; r && r.local_variable_get(:k); end
  def ybrk(i); each2 { |k| break [:b, k] if k == i % 2 }; end
  def yret(i); each2 { |k| return [:r, k] if k == 1 }; :none; end
end
pc = PC.new; pcout = []
th = 3.times.map { |t| Thread.new { f = proc { |a| a * 2 }; s = 0; 5000.times { |i| s += f.call(i) + pc.ysum(t) }; s } }.map(&:value)
20_000.times do |i|
  r = [pc.run(i), pc.kept(i), pc.nxt(i), pc.brk(i), pc.lam(i), pc.ysum(i), pc.ykeep(i), pc.ybrk(i), pc.yret(i)]
  pcout << [i, r] if i % 3000 == 1 || (i > 9_998 && i < 10_003)
  if i == 10_000
    class PC; def each2; (0..2).each { |k| yield k * 10 }; end; end
  end
end
pcout.each { |r| p r }
p pc.kept_values, th

# Light frames with a while loop (locals in registers until the OSR site wants a tuple), with a
# literal block (the scope is made where the block is), with optional parameters (rewritten
# defaults): closures, binding, return/break from the block, defaults with side effects, that
# refer to earlier parameters or raise, and a redefinition mid-run.
$lfside = 0
class LF
  def initialize(size = 0, extra = [size]); @s = size == 0 ? nil : Array.new(size); @e = extra; end
  def info; [@s && @s.size, @e]; end
  def walk(n); i = 0; acc = 0; while i < n; acc += i; i += 1; end; [i, acc]; end
  def grow(n); a = n; if n.odd?; [1, 2].each_index { |k| a += k }; end; a; end
  def clo(n); b = n; pr = proc { b += 1 }; pr.call; [b, pr.call, b]; end
  def bnd(n); x = n * 2; bs = [0].map { |_| binding }; bs[0].local_variable_set(:x, 7) if n % 2 == 0; x; end
  def ret(n); [1, 2, 3].each { |v| return v * 10 if v == n % 4 }; :none; end
  def brk(n); [1, 2, 3].each { |v| break v if v == n % 4 }; end
  def opt(a, b = a + 1, c = ($lfside += 1)); [a, b, c - c]; end
  def optr(a = (raise ArgumentError, "no a")); a; end
  def opts(s = "lit"); s << "!"; s; end
end
lf = LF.new; lfout = []
20_000.times do |i|
  r = [LF.new(i % 3).info, lf.walk(i % 50), lf.grow(i), lf.clo(i), lf.bnd(i), lf.ret(i), lf.brk(i), lf.opt(i),
       lf.opt(i, 2), (lf.optr rescue $!.message), lf.optr(i), lf.opts, lf.opts("x".dup)]
  lfout << [i, r] if i % 3000 == 2 || (i > 9_998 && i < 10_003)
  if i == 10_000
    class LF; def grow(n); n.odd? ? [7].map { |k| k + n } : n; end; end
  end
end
lfout.each { |r| p r }
p $lfside

# A refinement of nil? / == active in the calling method's file.
module IDRefine
  refine(Object) { def nil?; :refnil; end; def ==(o); :refeq; end }
end
using IDRefine
class IDR
  def nq(o); o.nil?; end
  def eq(o); o == self; end
end
idr = IDR.new; idrout = []
# called on every iteration, so that both methods turn hot, with rows kept at both parities of i
# (nq sees an object and nil)
20_000.times do |i|
  row = [idr.nq(i.odd? ? idr : nil), idr.eq(idr)]
  idrout << row if i % 5000 < 2
end
p idrout

# ---- recursion through a method that passes a literal block (review of #215) ----
# Such a method is a light frame whose only call is one LightScopeCall leaves alone; it must
# still check the stack, or this ends the process instead of raising.
lambda do
  rb = Class.new do
    def deep; deep { }; end
    def deeper(n); deeper(n + 1) { n }; end
    def each_deep(a); a.each { |e| each_deep(a) }; end
  end.new
  # SystemStackError is not a StandardError: a rescue modifier would let it through
  overflow = ->(&call) { begin; call.(); :returned; rescue SystemStackError => e; e.class; end }
  p overflow.() { rb.deep }, overflow.() { rb.deeper(0) }, overflow.() { rb.each_deep([1]) }
end.call

# ---- return from a block literal of a light-frame method called with a literal block ----
# The scope such a method makes for its block literal has the call's flow-control frame, the
# caller's literal block, and a return in the block literal targets that: the method has to
# take it as its own return (`def d; [1].each { return 5 }; end; d { }' leaked an uncaught
# MethodUnwinder), however it gets there - an iterator, a yield before it, a nested block.
lambda do
  rr = Class.new do
    def d1(i) = [i].each { |v| return v * 2 if v.odd?; v }
    def d2(i) = [i].each { |v| [v].each { |w| return w * 3 if w.odd? }; v }
    def d3(i) = i.times { |v| return v if v == 2 }
    def d4(i) = [i].map { |v| break 7 if v == 3; return v if v.odd?; v }
    def d6(i) = [i].each { |v| return yield(v) if v.odd? }
  end.new
  un = ->(x) { x.is_a?(Array) ? x[0] : x }
  s = 0
  20_000.times do |i|
    s += un.(rr.d1(i) { 1 }) + un.(rr.d2(i) { 1 }) + rr.d3(i % 5) { 1 } + un.(rr.d4(i % 7) { 1 })
    s += un.(rr.d6(i % 9) { |x| x + 1 }) + un.(rr.d6(i % 9, &->(x) { x + 2 })) + un.(rr.d1(i, &:itself))
    s += un.(rr.d1(i)) if i.odd?
  end
  p s, (rr.d6(1) rescue $!.message)
end.call

# ---- one-character slices: `s[i]' of an ASCII character shares one owner-less content ----
# Every such String is still a String of its own: mutated in every way while others from the
# same call site are alive, compared with ==/eql?/equal?/<=>/hash across encodings, with
# String#== replaced and restored mid-run, and read and mutated from four threads at once.
# A method, not a lambda: the threads' locals must not be the script's own (`e' above).
def one_char_slices
  srcs = ["abc\"\\ xé", "abc".b + "\xff".b, "abc".encode("US-ASCII"), ("ab" * 3) + "c",
          "ab\xffc".dup.force_encoding("UTF-8"), "ab".encode("UTF-16LE"), "ab".encode("Shift_JIS"),
          "ab".encode("EUC-JP")]
  ops = [
    ->(c) { c << "z" }, ->(c) { c.replace("q") }, ->(c) { c.force_encoding("UTF-16LE"); v = [c.valid_encoding?, c.bytesize]; c.force_encoding("UTF-8"); v },
    ->(c) { c.setbyte(0, 0x7a) }, ->(c) { c.upcase! }, ->(c) { c.instance_variable_set(:@x, 1) },
    ->(c) { def c.hi = :hi; c.hi }, ->(c) { c.freeze; (c << "x" rescue $!.class) },
    ->(c) { c.dup << "d" }, ->(c) { c.clone << "e" }, ->(c) { c.encode("UTF-16BE") },
    ->(c) { c.force_encoding("BINARY") }, ->(c) { c.prepend("p") }, ->(c) { c.clear },
    ->(c) { c.insert(0, "é") }, ->(c) { c.squeeze!; c.succ! }, ->(c) { c.chop! },
    ->(c) { {c => 1}.keys[0].frozen? }, ->(c) { Marshal.load(Marshal.dump(c)) },
    ->(c) { c.to_sym.to_s }, ->(c) { c.unpack("C*") }, ->(c) { c.valid_encoding? },
  ]
  out = []
  20_000.times do |i|
    s = srcs[i % srcs.size]
    k = i % s.size
    a = s[k]; b = s[k]; c = s.slice(k); d = s[k]
    r = ops[i % ops.size].(c) rescue $!.class
    if i % 997 == 0 || i < srcs.size * ops.size
      out << [i % srcs.size, k, a, b, c, d, r, a.encoding, c.encoding, a == b, a.eql?(b), a.equal?(b),
              a == c, c == a, a.eql?(c), a <=> c, a.hash == b.hash, a.hash == c.hash, a.frozen?, c.frozen?,
              a.valid_encoding?, a.ascii_only?, c.valid_encoding?, a.bytes, c.bytes, a == "a", "a" == a,
              a == "a".b, a == "a".encode("UTF-16LE"), a != "b", a.instance_variables, b.singleton_methods,
              a.class, d.size, d.bytesize]
    end
  end
  out.each { |x| p x }
  sub = Class.new(String)
  q = sub.new("xyz")
  p [q[1].class, q[1] == "y", "y" == q[1], q[1].equal?(q[1])]
  w = ObjectSpace::WeakMap.new; k1 = "ab"[0]; k2 = "ab"[0]; w[k1] = 1
  p [w.key?(k1), w.key?(k2), {k1 => 1}[k2], k1.hash == k2.hash]
  # String#== redefined and restored while the slices compare hot
  t = 0
  20_000.times do |i|
    if i == 7_000
      String.class_eval { alias_method :__eq_saved, :==; def ==(o) = :redef }
    elsif i == 14_000
      String.class_eval { alias_method :==, :__eq_saved; remove_method :__eq_saved }
    end
    x = "ab"[i % 2]
    v = (x == "a")
    t += v == :redef ? 100 : (v ? 1 : 0)
    t += 1000 if "b" == x && i.odd?
  end
  p t
  # four threads: each mutates its own slices while comparing others from the same source
  src = "abcdefgh" * 4
  res = 4.times.map do |n|
    Thread.new do
      acc = []
      5_000.times do |i|
        c = src[(i + n) % src.size]
        e = src[(i + n) % src.size]
        c << n.to_s if i.even?
        c.upcase! if i % 3 == 0
        acc << [c, e == src[(i + n) % src.size], e.size] if i % 500 == 0
      end
      acc
    end
  end.map(&:value)
  p res, src
end
one_char_slices
# ---- a block that leaves the method through its block parameter (second review of #215) ----
# `def capture(&b); b; end' is a light frame, but not one that "can only yield to its block":
# each evaluation of the literal must give a Proc of its own.
lambda do
  kb = Class.new do
    def capture(&b); b; end
    def capture2(&b) = b
    def keep(&b); @kept = b; nil; end
    def forward(&b); capture(&b); end
    attr_reader :kept
  end.new
  saved = []
  i = 0
  while i < 3
    saved << kb.capture { i }
    i += 1
  end
  p saved[0].equal?(saved[1]), saved.map(&:object_id).uniq.size, saved.map(&:call)
  s2 = []; s3 = []; s4 = []
  5000.times do |k|
    pr = kb.capture2 { k }
    s2 << pr if k % 1000 == 0
    kb.keep { k * 2 }
    s3 << kb.kept if k % 1000 == 1
    s4 << kb.forward { k + 1 } if k % 1000 == 2
  end
  p s2.map(&:call), s3.map(&:call), s4.map(&:call), (s2 + s3 + s4).map(&:object_id).uniq.size
end.call

# ---- `"lit" == v' and `"lit" != v' in a method-JIT body, and `v == "lit"' ----
# The JIT compares a String v with an ASCII literal by its characters when v holds them in an
# encoding where ASCII is its own bytes (MutableString.EqualsAsciiLiteral), and through
# String#== otherwise. v of every kind: other encodings (binary, US-ASCII, UTF-16LE, an
# invalid byte, a one-byte slice of a multi-byte character), a String subclass, nil, an
# Integer, an object with to_str; in the middle NilClass gains to_str and String#== is replaced.
lambda do
  rl = Class.new do
    def initialize; @c = nil; end
    attr_writer :c
    def ws; ' ' == @c || "\t" == @c || "\n" == @c || "\r" == @c; end
    def ne; "a" != @c; end
    def em; "" == @c; end
    def fw; @c == "ab"; end
    def u8; "é" == @c; end
  end.new
  sub = Class.new(String)
  ts = Object.new
  def ts.to_str; "a"; end
  def ts.==(o); o == "a"; end
  vals = [" ", "\t", "x", "a", "ab", "", nil, 1, :a, " ".b, "a".b, "a".encode("US-ASCII"), "a".encode("UTF-16LE"),
          "\xFF".b, "\xFF", "é"[0].b, "é".b[0], "é", "é".b, "é".encode("ISO-8859-1"), sub.new("a"), sub.new(" "), ts,
          "ab".force_encoding("UTF-16LE"), +"a"]
  out = []
  20_000.times do |i|
    v = vals[i % vals.size]
    rl.c = v
    r = [rl.ws, rl.ne, rl.em, rl.fw, rl.u8]
    out << [i % vals.size, r] if i >= 20_000 - vals.size
    if i == 10_000
      NilClass.class_eval { def to_str; "a"; end; def ==(o); :nil_eq; end }
    elsif i == 12_000
      NilClass.class_eval { remove_method :to_str; remove_method :== }
    elsif i == 14_000
      String.class_eval { alias_method :__eq, :==; def ==(o); __eq(o) || o.equal?(:a); end }
    elsif i == 16_000
      String.class_eval { alias_method :==, :__eq; remove_method :__eq }
    end
    out << [:mid, i, r] if i == 11_000 || i == 15_000
  end
  out.each { |x| p x }
end.call

# ---- the same comparisons with v in many encodings, retagged and transcoded ----
# The character comparison is only right where ASCII characters are their own bytes: not in
# UTF-16/32, not in a dummy encoding (UTF-7, ISO-2022-JP), not in EBCDIC. Each String is made
# both ways - force_encoding keeps the bytes, encode keeps the characters - and compared hot.
lambda do
  le = Class.new do
    attr_writer :c
    def rev; 'a' == @c; end
    def revn; 'a' != @c; end
    def fwd; @c == 'a'; end
    def two; 'ab' == @c; end
    def emp; '' == @c; end
    def cas; case @c when 'a' then 1 when 'ab' then 2 else 3 end; end
  end.new
  le.c = "a"
  20_000.times { le.rev; le.revn; le.fwd; le.two; le.emp; le.cas }
  probe = lambda do |v|
    le.c = v
    # (inspect, not `x == true': this file's `using IDRefine' above makes Object#== answer :refeq from here on)
    [le.rev, le.revn, le.fwd, le.two, le.emp, le.cas].map { |x| x.inspect[0] }.join
  end
  %w[UTF-8 US-ASCII BINARY ISO-8859-1 Windows-1252 Shift_JIS EUC-JP Windows-31J GB18030 Big5 KOI8-R
     UTF-16LE UTF-16BE UTF-32LE UTF-16 UTF-32 UTF-7 ISO-2022-JP CP50221 IBM037].each do |name|
    enc = Encoding.find(name)
    row = ["a", "ab", "b", ""].flat_map do |s|
      [(probe.(s.dup.force_encoding(enc)) rescue $!.class), (probe.(s.encode(enc)) rescue $!.class)]
    end
    puts "#{name} #{row.join(' ')}"
  end
  [[97].pack('C'), (+"x").replace("a"), (+"") << "a", (+"").b << "a", :a.to_s, "a\0", "A", "ａ", "á"].each { |v| p [v, probe.(v)] }
end.call

# ---- a one-character slice compared with literals in compiled methods, then changed ----
# `s[i]' of an ASCII character has a shared content (SharedAsciiContents); the compiled
# comparisons read it as it is (MutableString.EqualsAsciiLiteral). The slice is still a
# String of its own: appended to, replaced and retagged while the parser's next slices
# and its source are compared again.
lambda do
  pr = Class.new do
    def initialize(s); @input = s; @i = 0; @current = nil; end
    def read; @current = @input[@i]; @i = (@i + 1) % @input.size; @current; end
    def ws; ' ' == @current || "\t" == @current || "\n" == @current || "\r" == @current; end
    def digit; '0' == @current || '5' == @current || '9' == @current; end
    def fwd; @current == "a"; end
    def ne; "a" != @current; end
    def cas; case @current when 'a' then 1 when ' ' then 2 else 3 end; end
    def all; [ws, digit, fwd, ne, cas].map { |x| x.inspect[0] }.join; end
    attr_reader :input
  end.new("a 5\tz9\nA".dup)
  out = []
  30_000.times do |k|
    c = pr.read
    r = pr.all
    out << [c.bytes, r] if k >= 30_000 - 8
    next unless k % 7_000 == 6_999
    c << "!"; out << [:appended, c.bytes, pr.all]
    c.replace(" "); out << [:replaced, c.bytes, pr.all]
    c.force_encoding("UTF-16LE"); out << [:retagged, c.bytes, c.encoding.name, pr.all]
    c.force_encoding("BINARY"); out << [:binary, c.bytes, pr.all]
  end
  out.each { |x| p x }
  p pr.input
end.call
