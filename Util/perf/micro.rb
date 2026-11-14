# What single operations cost, in ns each: a method per shape, called from a `while` loop
# (so the loop runs under -X:OSR and the method under -X:JIT, as hot code does).
#
#   IR_CONFIG=Release ./ir.sh Util/perf/micro.rb
#   ruby Util/perf/micro-table.rb cruby=ruby "ir=env IR_CONFIG=Release ./ir.sh"
#
#   IR_CONFIG=Release ./ir.sh Util/perf/micro.rb veach4 rangelit     # only these shapes (one per process is steadiest)
#
# Best of five runs of each. `empty` is the floor: a call of a method with no body.
# TruffleRuby prints fractions of a ns for most of these, because it inlines the call and
# then deletes the loop; its column says "free", not how fast a call is. Add a shape here
# when a benchmark turns out to be slow for a reason none of these isolates.
$stdout.sync = true
class P
  attr_accessor :a
  def initialize; @x = 1; @v = 2; @a = 3; @arr = [1,2,3,4]; end
  def ivar_rw; @x += @v; @x = 0 if @x > 500; end
  def getter; a; end
  def empty; end
  def cmp(o); @x < o; end
  def absm; @v = 0 - @v.abs; end
  def arr; @arr[1] = @arr[2]; end
  def nilq(o); o.nil?; end
  def eqq(o); o == self; end
  def yielder; yield 1; end
  def blk_each(a); s = 0; a.each { |e| s += e }; s; end
  def whilei(n); i = 0; while i < n; i += 1; end; i; end
  def newobj; P2.new(1, 2); end
  def sym_case(s); case s when :a then 1 when :b then 2 else 3 end; end
  def str_eq(s); s == "abc"; end
  def poly(o); o.foo; end
  def ws(c); ' ' == c || "\t" == c || "\n" == c || "\r" == c; end
  def swap(i, j); t = @arr[i]; @arr[i] = @arr[j]; @arr[j] = t; end
  def locattr(m); a = m.a; b = m.b; b.x - a.x < 1.5; end
  def sqrtw(n); i = 0; s = 0.0; f = 1.5; while i < n; s += Math.sqrt(f + i); i += 1; end; s; end
  def rangeeach(n); s = 0.0; (n / 1000).times { (0...1000).each { |i| s += i * 0.5 } }; s; end
  def half(f); f < 0.0 ? 0.0 : f * 0.5; end
  def selfcall(f); half(f) + half(f + 1.0); end
  def blkattr(bs, n); (n / 20).times { 20.times { |i| b = bs[i]; b.v -= b.x; b.c += 2 } }; end
  def short4(bs); s = 0.0; bs.each { |b| s += b.v * 0.5 + 0.25 }; s; end
  def sh_each(a); s = 0; a.each { |x| s += x }; s; end
  def sh_times(k); s = 0; k.times { |j| s += j }; s; end
  def sh_map(a); a.map { |x| x + 1 }; end
  def sh_any(a); a.any? { |x| x > 1000 }; end
end
# round nine's shapes: SOM's Vector#each (a Ruby each yielding the elements of an ivar Array through Range#each's
# block, as DeltaBlue, CD, Json and Havlak iterate), and a method that makes a Range from its operands (Json's
# `@input[@capture_start..end_]')
class PVec
  def initialize(n); @storage = Array.new(n) { |i| i }; @first_idx = 0; @last_idx = n; end
  def each; (@first_idx..(@last_idx - 1)).each { |i| yield @storage[i] }; end
  def sum; s = 0; each { |e| s += e }; s; end
  def slice(e); @storage[@first_idx..e]; end
end
class P2; def initialize(a, b); @a = a; @b = b; end; def foo; 1; end; end
class P3; def foo; 2; end; end
class PV; attr_reader :x; def initialize(x); @x = x; end; end
class PM; attr_reader :a, :b; def initialize(a, b); @a = a; @b = b; end; end
class PB; attr_accessor :x, :v, :c; def initialize; @x = 1.5; @v = 0.0; @c = 0; end; end
# `ruby micro.rb NAME...' runs only the shapes named, so that one can have a process of its own: a
# shape that compiles differently once another has run is measured as it is alone.
def t(name, n = 3_000_000)
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9
  5.times { t0 = Process.clock_gettime(Process::CLOCK_MONOTONIC); yield n; d = Process.clock_gettime(Process::CLOCK_MONOTONIC) - t0; best = d if d < best }
  printf "%-12s %7.1f ns/op\n", name, best * 1e9 / n
end
o = P.new; a4 = [1,2,3,4]; p2 = P2.new(1,2); p3 = P3.new
t("empty")   { |n| i = 0; while i < n; o.empty; i += 1; end }
t("ivar_rw") { |n| i = 0; while i < n; o.ivar_rw; i += 1; end }
t("getter")  { |n| i = 0; while i < n; o.getter; i += 1; end }
t("attr")    { |n| i = 0; while i < n; o.a; i += 1; end }
t("cmp")     { |n| i = 0; while i < n; o.cmp(i); i += 1; end }
t("abs")     { |n| i = 0; while i < n; o.absm; i += 1; end }
t("arr")     { |n| i = 0; while i < n; o.arr; i += 1; end }
t("nil?")    { |n| i = 0; while i < n; o.nilq(i); i += 1; end }
t("==")      { |n| i = 0; while i < n; o.eqq(o); i += 1; end }
t("yield")   { |n| i = 0; while i < n; o.yielder { |x| x }; i += 1; end }
t("each4")   { |n| i = 0; while i < n; o.blk_each(a4); i += 1; end }
t("while10") { |n| i = 0; while i < n; o.whilei(10); i += 1; end }
t("new2")    { |n| i = 0; while i < n; o.newobj; i += 1; end }
t("symcase") { |n| i = 0; while i < n; o.sym_case(:b); i += 1; end }
t("str==")   { |n| i = 0; while i < n; o.str_eq("abd"); i += 1; end }
t("poly2")   { |n| i = 0; while i < n; o.poly(i.odd? ? p2 : p3); i += 1; end }
t("blockeach") { |n| (n/4).times { a4.each { |e| o.cmp(e) } } }
# round six's shapes: a literal as the receiver of == (Json's is_white_space), two element stores in a
# method (Permute's swap), Float attr reads on locals assigned from attr reads (CD's is_in_voxel)
sx = "x"; pm = PM.new(PV.new(1.5), PV.new(2.5))
t("lit==")   { |n| i = 0; while i < n; o.ws(sx); i += 1; end }
t("swap")    { |n| i = 0; while i < n; o.swap(1, 2); i += 1; end }
t("locattr") { |n| i = 0; while i < n; o.locattr(pm); i += 1; end }
# round eight's shapes, each a loop of its own (ns per iteration of it, not per call): Math.sqrt in a while loop,
# a long Range#each, attr op-assigns on a local a times block assigns
pbs = Array.new(20) { PB.new }
t("sqrtw")     { |n| o.sqrtw(n) }
t("rangeeach") { |n| o.rangeeach(n) }
t("blkattr")   { |n| o.blkattr(pbs, n) }
# round nine's shapes: a method calling a small pure Float method on self twice (CD's compare_to), then
# PVec's (above)
t("selfcall")  { |n| i = 0; f = 1.5; while i < n; o.selfcall(f); i += 1; end }
v1 = PVec.new(1); v2 = PVec.new(2); v4 = PVec.new(4); v100 = PVec.new(100)
t("veach1", 1_000_000) { |n| i = 0; while i < n; v1.sum; i += 1; end }
t("veach2", 1_000_000) { |n| i = 0; while i < n; v2.sum; i += 1; end }
t("veach4", 1_000_000) { |n| i = 0; while i < n; v4.sum; i += 1; end }
t("veach100", 50_000)  { |n| i = 0; while i < n; v100.sum; i += 1; end }
t("rangelit")          { |n| i = 0; while i < n; v4.slice(2); i += 1; end }
# a short run, an Array#each over four with Float arithmetic, called from a while (ns per call)
pb4 = pbs.first(4)
t("short4")    { |n| i = 0; while i < n; o.short4(pb4); i += 1; end }
# short runs of the core iterators at k = 1, 2, 4 (below the block's floor, BlockLoopInfo.Floor, the generic
# yield loop, at or above it the compiled copy): ns per call of the iterator, entry included
[1, 2, 4].each do |k|
  ak = Array.new(k) { |j| j + 1 }
  t("s_each#{k}")  { |n| i = 0; while i < n; o.sh_each(ak); i += 1; end }
  t("s_times#{k}") { |n| i = 0; while i < n; o.sh_times(k); i += 1; end }
  t("s_map#{k}")   { |n| i = 0; while i < n; o.sh_map(ak); i += 1; end }
  t("s_any#{k}")   { |n| i = 0; while i < n; o.sh_any(ak); i += 1; end }
end
# round ten's shapes: a cursor walking a linked list (List's is_shorter_than: `xt = x; ... xt = xt.nxt'), and a
# lookup in a binary tree of 300 keys whose `compare_to' calls a leaf on self (CD's RedBlackTree#find_node)
class PEl; attr_accessor :val, :nxt; def initialize(v, n); @val = v; @nxt = n; end; end
class PKey
  attr_reader :x, :y
  def initialize(x, y); @x = x; @y = y; end
  def cmpn(a, b); return 0 if a == b; return -1 if a < b; 1; end
  def compare_to(o); r = cmpn(@x, o.x); return r if r != 0; cmpn(@y, o.y); end
end
class PNode; attr_accessor :key, :left, :right; def initialize(k); @key = k; end; end
class PTree
  def initialize; @root = nil; end
  def shorter(x, y); xt = x; yt = y; while yt; return true unless xt; xt = xt.nxt; yt = yt.nxt; end; false; end
  def find(key); c = @root; while c; r = key.compare_to(c.key); return c if r == 0; if r < 0; c = c.left; else; c = c.right; end; end; nil; end
  def add(key)
    n = PNode.new(key); c = @root
    return @root = n unless c
    loop { if key.compare_to(c.key) < 0; (c.left = n; break) unless c.left; c = c.left; else; (c.right = n; break) unless c.right; c = c.right; end }
  end
end
pt = PTree.new; pl6 = nil; 6.times { |j| pl6 = PEl.new(j, pl6) }; pl4 = pl6.nxt.nxt
pkeys = Array.new(300) { |j| PKey.new((j * 7919) % 300 + 0.5, j * 1.0) }; pkeys.each { |k| pt.add(k) }
t("listwalk")  { |n| i = 0; while i < n; pt.shorter(pl4, pl6); i += 1; end }
t("treefind", 1_000_000) { |n| i = 0; while i < n; pt.find(pkeys[i % 300]); i += 1; end }
