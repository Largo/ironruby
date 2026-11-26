# Differential matrix for a block literal with another literal nested in it (IR_NESTED_PROCS: Src/Ruby/Compiler/Ast/
# BlockDefinition.cs, RubyOps.KeepEnclosingProcs). Such a block's Proc is made again for the next call of its method, and
# the method's scope is parked with it, on every call that made no nested Proc - NBody's `advance', whose nested
# `(i + 1)...n).each' runs in place in the outlined loop of its `each_index' block. A call that does make the nested Proc
# must keep everything that Proc reaches: the enclosing blocks' Procs and scopes, and the method's scope with its locals and
# self. Run it under CRuby and under ./ir.sh with the switch on and off, and with the tiers off, and diff:
#
#   ruby Util/nested-proc-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/nested-proc-matrix.rb > /tmp/on.txt
#   IR_NESTED_PROCS=0 ./ir.sh Util/nested-proc-matrix.rb > /tmp/off.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/nested-proc-matrix.rb > /tmp/notiers.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt && diff /tmp/mri.txt /tmp/notiers.txt
#
# Every method runs hot (thousands of calls that make no nested Proc, so that its scope is parked and its Procs made
# again) before and around the calls that make one and keep it; what was kept is called at the end.

def t(label)
  print label.ljust(64), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts "#{e.class}: #{e.message}"
  end
end

N = 3000
KEEP = [7, 1500, 1501, 2999]

class Keeper
  def initialize; @kept = []; end
  attr_reader :kept
  def keep(&b); @kept << b; b; end
end

# ---------------------------------------------------------------- 1. NBody's shape, a nested Proc kept now and then

class Bodies
  def initialize(n); @a = Array.new(n) { |i| i * 1.5 }; @k = Keeper.new; @s = 0.0; end
  attr_reader :k, :s
  def advance(dt, keep)
    @a.each_index do |i|
      x = @a[i]
      ((i + 1)...@a.size).each do |j|
        @s += (x - @a[j]) * dt
      end
      @k.keep { [i, x, dt, self.class] } if keep
    end
  end
  def advance2(dt, keep)
    @a.each_index do |i|
      x = @a[i]
      ((i + 1)...@a.size).each do |j|
        @s += (x - @a[j]) * dt
        @k.keep { [i, j, x, dt] } if keep && j == i + 1
      end
    end
  end
end

t('kept in the outer block') {
  b = Bodies.new(5)
  N.times { |n| b.advance(n * 0.5, KEEP.include?(n)) }
  [b.s, b.k.kept.map(&:call)]
}
t('kept in the inner block') {
  b = Bodies.new(5)
  N.times { |n| b.advance2(n * 0.5, KEEP.include?(n)) }
  [b.s, b.k.kept.map(&:call)]
}
t('kept: a String element in the middle') {
  b = Bodies.new(4)
  r = []
  N.times do |n|
    b.instance_variable_get(:@a)[2] = (n == 1500 ? 'x' : 3.0)
    begin
      b.advance(1.0, n == 1500 || n == 1600)
    rescue TypeError, NoMethodError => e
      r << [n, e.class]
    end
  end
  [r, b.k.kept.map(&:call)]
}

# ---------------------------------------------------------------- 2. the method's locals and self, as a kept Proc sees them

class M
  def initialize(k); @k = k; end
  def m(x, keep)
    y = x * 2
    [1, 2].each do |e|
      [10].each do |f|
        @k.keep { [x, y, e, f, self.object_id == @id] } if keep
      end
      y += e
    end
    y
  end
  def mark; @id = object_id; self; end
end

t('method locals seen by a kept Proc after later calls') {
  k = Keeper.new
  o = M.new(k).mark
  s = 0
  N.times { |n| s += o.m(n, KEEP.include?(n)) }
  [s, k.kept.map(&:call)]
}
t('two receivers, alternating') {
  k = Keeper.new
  a = M.new(k).mark
  b = M.new(k).mark
  N.times { |n| (n.even? ? a : b).m(n, n % 997 == 0) }
  k.kept.map(&:call)
}

# ---------------------------------------------------------------- 3. what a nested literal can be

class Kinds
  def lam(n); r = nil; [n].each { |x| r = -> { x + n } if n % 1000 == 0 }; r; end
  def prc(n); r = nil; [n].each { |x| r = proc { x * n } if n % 1000 == 0 }; r; end
  def pnew(n); r = nil; [n].each { |x| r = Proc.new { x - n } if n % 1000 == 0 }; r; end
  def meth(n); r = nil; [n].each { |x| r = binding if n % 1000 == 0; x }; r; end
  def dm(n); [n].each { |x| self.class.send(:define_method, :"dm_#{n}") { x + 1 } if n % 1000 == 0 }; nil; end
  def thr(n); r = nil; [n].each { |x| r = Thread.new { x * 3 } if n % 1000 == 0 }; r; end
  def three(n); r = nil; [n].each { |x| [x].each { |y| [y].each { |z| r = proc { [x, y, z, n] } if n % 1000 == 0 } } }; r; end
end

t('lambda, proc, Proc.new, binding, define_method, Thread') {
  o = Kinds.new
  out = []
  N.times do |n|
    l = o.lam(n); out << [:lam, l.call] if l
    pr = o.prc(n); out << [:prc, pr.call] if pr
    pn = o.pnew(n); out << [:pnew, pn.call] if pn
    bd = o.meth(n); out << [:binding, bd.local_variable_get(:n)] if bd
    o.dm(n)
    th = o.thr(n); out << [:thread, th.value] if th
    tr = o.three(n); out << [:three, tr.call] if tr
  end
  out + [Kinds.new.dm_2000]
}
t('kept Procs called after the method ran on') {
  o = Kinds.new
  ks = []
  N.times { |n| r = o.three(n); ks << r if r }
  ks.map(&:call)
}

# ---------------------------------------------------------------- 4. the outer literal made twice in one call

class Twice
  def initialize; @kept = []; end
  attr_reader :kept
  def m(n)
    i = 0
    while i < 2
      [i].each do |v|
        [v].each { |w| @kept << proc { [n, v, w, i] } if n % 700 == 0 && w == 0 }
      end
      i += 1
    end
    i
  end
end

t('outer literal evaluated twice per call') {
  o = Twice.new
  N.times { |n| o.m(n) }
  o.kept.map(&:call)
}

# ---------------------------------------------------------------- 5. jumps through a reused Proc

class Jumps
  def brk(n); [1, 2, 3].each { |x| [x].each { |y| break if y == 2 }; break x * n if x == 3 }; end
  def nxt(n); [1, 2, 3].map { |x| [x].each { |y| next }; next x + n }; end
  def ret(n); [1, 2, 3].each { |x| [x].each { |y| return y * n if y == 2 } }; :none; end
end

t('break, next, return across nested blocks') {
  o = Jumps.new
  s = []
  N.times { |n| s << [o.brk(n), o.nxt(n), o.ret(n)] if n % 1000 == 999; o.brk(n); o.nxt(n); o.ret(n) }
  s
}

# ---------------------------------------------------------------- 6. recursion, fibers, threads

class Rec
  def initialize; @kept = []; end
  attr_reader :kept
  def m(d, n)
    [d].each do |x|
      [x].each { |y| @kept << proc { [y, d, n] } if n % 1000 == 0 && y == 0 }
      m(d - 1, n) if d > 0
    end
    d
  end
end

t('recursive calls of the method') {
  o = Rec.new
  N.times { |n| o.m(3, n) }
  o.kept.map(&:call)
}
t('enumerator over a method with nested blocks') {
  o = Bodies.new(4)
  e = Enumerator.new { |y| 200.times { |n| o.advance(1.0, n == 100); y << o.s } }
  vals = []
  vals << e.next while vals.size < 150
  [vals.last, o.k.kept.size, o.k.kept.map(&:call)]
}
t('threads calling one method') {
  k = Keeper.new
  objs = Array.new(4) { M.new(k).mark }
  ths = objs.each_with_index.map { |o, ti| Thread.new { s = 0; 1000.times { |n| s += o.m(n + ti, n == 500) }; s } }
  [ths.map(&:value), k.kept.map(&:call).sort]
}

# ---------------------------------------------------------------- 7. the inner Proc as an object

class Ident
  def m(n, out)
    [1].each { |x| [x].each { |y| out << proc { y } } if n % 1000 == 0 }
  end
end

t('nested Procs are distinct objects') {
  o = Ident.new
  out = []
  N.times { |n| o.m(n, out) }
  [out.size, out.map(&:object_id).uniq.size, out.map(&:call)]
}
