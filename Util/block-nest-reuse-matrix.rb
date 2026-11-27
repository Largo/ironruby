# Differential checks for a block that holds a block literal (round thirty-seven, perf/iter).
#
# A block whose body needs its scope reuses the scope of the previous call through the same BlockParam
# (block-scope-reuse-matrix.rb); one with a block literal in it reuses it too, with the Procs of its
# literals, when the last Proc of each was given only to light frames and [BlockNotKept] iterators and
# nothing captured the scope. Each outer block below runs several times through one BlockParam, so the
# second and later calls take that path; every way the inner Proc or the outer scope can escape is a row.
# The output must match CRuby's and the unchanged build's, with IR_PARKED_BLOCK_SCOPES=0 too:
#
#   ruby   Util/block-nest-reuse-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/block-nest-reuse-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# (also with -X:NoJIT -X:NoOSR, IR_LIGHT_FRAMES=0 and IR_PARKED_SCOPES=0)

class O
  def e; yield 1; end
  def e2; yield 1; yield 2; end
  def keep(&b); (@kept ||= []) << b; nil; end
  def kept; @kept; end
  def ivar_keep(&b); @blk = b; nil; end
  def blk; @blk; end
  def yield_self_class; yield self.class; end
end
o = O.new

# the plain case: the inner block reads and writes the outer block's locals and the method's
s = 0
[1, 2, 3, 4].each { |x| y = x * 10; o.e { |c| s += c + x + y } }
p s
r = []
[1, 2, 3].each { |x| o.e2 { |c| r << [x, c] } }
p r
r = []
3.times { |i| [10, 20].each { |v| r << v + i } }
p r

# an outer local starts nil on every call of the outer block, whatever the last call left in it
r = []
[1, 2, 3].each { |x| y ||= x; o.e { |c| y += c }; r << y }
p r
r = []
[1, 2, 3].each { |x| r << defined?(z).inspect; o.e { |c| r << c }; z = x; r << z }
p r

# the inner Proc kept by a method (&b), then called after the outer block has run again
o2 = O.new
[1, 2, 3].each { |x| y = x * 2; o2.keep { [x, y] } }
p o2.kept.map(&:call)
o3 = O.new
[1, 2, 3, 4].each { |x| y = x; if x.even? then o3.keep { y } else o.e { |c| y += c } end }
p o3.kept.map(&:call)

os = [O.new, O.new, O.new]
[0, 1, 2].each { |x| y = x * 3; os[x].ivar_keep { y + x } }
p os.map { |q| q.blk.call }

# the inner literal made several times in one call, the middle one kept
o4 = O.new
[1, 2, 3].each { |x| i = 0; while i < 3; i += 1; if i == 2 then o4.keep { [x, i] } else o.e { |c| c } end; end }
p o4.kept.map(&:call)

# proc, lambda, Proc.new and a block made a method
ps = []
[1, 2, 3].each { |x| y = x + 1; ps << proc { y }; o.e { |c| c } }
p ps.map(&:call)
ls = []
[1, 2, 3].each { |x| y = x + 2; ls << -> { y }; o.e { |c| c } }
p ls.map(&:call)
ls = []
[1, 2, 3].each { |x| y = x + 3; o.e { |c| ls << lambda { y + c } } }
p ls.map(&:call)
class DefIn2
  [1, 2].each { |x| y = x * 5; define_method("m#{x}") { y }; [0].each { |z| z } }
end
p [DefIn2.new.m1, DefIn2.new.m2]

# a Binding of the outer scope, or of the inner block's, taken in the inner block
bs = []
[1, 2, 3].each { |x| y = x * 7; o.e { |c| bs << binding } }
p bs.map { |b| [b.local_variable_get(:x), b.local_variable_get(:y), b.local_variable_get(:c)] }
bs = []
[1, 2, 3].each { |x| y = x * 9; o.e { |c| c }; bs << binding }
p bs.map { |b| [b.local_variable_get(:x), b.local_variable_get(:y)] }
r = []
[1, 2, 3].each { |x| o.e { |c| r << eval("x + c") } }
p r

# a block kept by the core: a Hash's default proc, an Enumerator, a lazy map, a Thread
hs = []
[1, 2, 3].each { |x| hs << Hash.new { |h, k| h[k] = k * x }; o.e { |c| c } }
p hs.map { |h| h[2] }
es = []
[1, 2, 3].each { |x| es << Enumerator.new { |y| y << x; y << x * 2 } }
p es.map(&:to_a)
lz = []
[1, 2, 3].each { |x| lz << [x].lazy.map { |v| v + x } }
p lz.map(&:to_a)
ts = []
[1, 2, 3].each { |x| y = x * 4; ts << Thread.new { y + 1 } }
p ts.map(&:value)

# break, next, return and an exception from the inner block and from the outer one
r = []
[1, 2, 3, 4].each { |x| v = o.e { |c| break x * 100 if x == 2; c + x }; r << v }
p r
r = []
[1, 2, 3, 4].each { |x| v = o.e { |c| next x * 10 if x.odd?; c }; r << v }
p r
r = []
res = [1, 2, 3, 4].each { |x| o.e { |c| r << c + x }; break :out if x == 3 }
p [r, res]
def ret_from_inner(o, a)
  a.each { |x| o.e { |c| return [:ret, x] if x == 2 } }
  :none
end
p [ret_from_inner(o, [1, 2, 3]), ret_from_inner(o, [1, 3]), ret_from_inner(o, [5, 2])]
r = []
[1, 2, 3, 4].each { |x|
  begin
    o.e { |c| raise "boom #{x}" if x == 2; r << x }
  rescue => e
    r << e.message
  end
}
p r
r = []
3.times {
  begin
    [1, 2, 3].each { |x| y = x; o.e { |c| raise ArgumentError, "a#{y}" if y == 2; r << y } }
  rescue ArgumentError => e
    r << e.message
  end
}
p r

# recursion through the outer block's own Proc, and through the iterator
r = []
pr = proc { |n| y = n * 2; o.e { |c| r << [n, y]; pr.call(n - 1) if n > 0; r << [:back, n, y] } }
pr.call(3)
pr.call(1)
p r
def walk(o, n, r)
  [n, n + 10].each { |x| o.e { |c| r << x; walk(o, n - 1, r) if n > 0 && x == n } }
end
r = []
walk(o, 2, r)
p r

# a different self for the same outer block (define_method, instance_exec)
class SelfA; end
class SelfB; end
blk = proc { |x| o.e { |c| [self.class, x] } }
SelfA.define_method(:m, &blk)
SelfB.define_method(:m, &blk)
p [SelfA.new.m(1), SelfB.new.m(2), SelfA.new.m(3), SelfB.new.m(4)]
r = []
[1, 2, 3].each { |x| r << (x.even? ? "s" : 5).instance_exec(x) { |v| o.e { |c| [self, v] } } }
p r

# a fiber suspended in the inner block while the outer block runs again
fs = []
[1, 2, 3].each { |x| y = x; fs << Fiber.new { o.e { |c| Fiber.yield y; y += 10 }; y } }
p fs.map(&:resume)
p fs.map(&:resume)
en = Enumerator.new { |yy| [1, 2, 3].each { |x| o.e2 { |c| yy << [x, c] } } }
p [en.next, en.next, en.next, en.peek]

# two literals in the outer block, one of them kept, and one in a nested module body
o5 = O.new
[1, 2, 3].each { |x| o.e { |c| c }; o5.keep { x } if x != 2; [x].each { |v| v } }
p o5.kept.map(&:call)
r = []
[1, 2].each { |x| class << Object.new; [1].each { |v| v }; end; o.e { |c| r << x } }
p r

# threads running one outer block at once, each checking its own locals
pr2 = proc { |t, k| y = t * 1000 + k; z = nil; o.e { |c| z = y + c }; z == y + 1 }
ok = 4.times.map { |t| Thread.new { (1..2000).all? { |k| pr2.call(t, k) } } }.map(&:value)
p ok
ok = 4.times.map { |t| Thread.new { (1..2000).all? { |k| [k].each { |x| y = t * 1000 + x; o.e { |c| y += c } ; raise "t#{t}" unless y == t * 1000 + x + 1 } } } }.map(&:value)
p ok

# a TracePoint enabled between calls
r = []
tp = TracePoint.new(:b_call) { |t| r << t.binding.local_variable_defined?(:x) if t.path == __FILE__ && t.lineno == __LINE__ + 3 }
[1, 2, 3, 4].each { |x|
  tp.enable if x == 3
  o.e { |c| c + x }
}
tp.disable
p r
