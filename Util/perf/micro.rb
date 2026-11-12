# What single operations cost, in ns each: a method per shape, called from a `while` loop
# (so the loop runs under -X:OSR and the method under -X:JIT, as hot code does).
#
#   IR_CONFIG=Release ./ir.sh Util/perf/micro.rb
#   ruby Util/perf/micro-table.rb cruby=ruby "ir=env IR_CONFIG=Release ./ir.sh"
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
end
class P2; def initialize(a, b); @a = a; @b = b; end; def foo; 1; end; end
class P3; def foo; 2; end; end
def t(name, n = 3_000_000)
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
