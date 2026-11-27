# Differential checks for Array#find, #detect, #find_index, #index, #any?, #all?, #none? and Integer#upto and #downto
# (round thirty-seven, perf/iter2): the Array methods are Array's own, as in CRuby 4.0, and with Integer#upto and #downto
# they are [BlockNotKept], so a method that gives them a literal block may park its scope with the block's Proc
# (MethodDefinition.IsNeverParkedIterator, RubyOps.TakeParkedScope). Each method below is called several times, so the
# later calls take the parked scope and Proc; every way the Proc or the scope can escape is a row. The output must match
# CRuby's and the unchanged build's, with IR_PARKED_FIND=0 and IR_PARKED_SCOPES=0 too:
#
#   ruby   Util/parked-iterator-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/parked-iterator-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
$stdout.sync = true
module Warning
  def self.warn(msg, **) = $stdout.print("W: #{msg.sub(/\A.*?warning: /, '')}")
end

def rows(name) = puts("#{name}: #{yield.inspect}")

# what the methods answer, called again and again
class F
  def find_gt(a, k) = a.find { |x| x > k }
  def detect_gt(a, k) = a.detect { |x| x > k }
  def find_none(a, k) = a.find(-> { :none }) { |x| x > k }
  def find_nil(a, k) = a.find(nil) { |x| x > k }
  def find_index_gt(a, k) = a.find_index { |x| x > k }
  def index_gt(a, k) = a.index { |x| x > k }
  def any_gt(a, k) = a.any? { |x| x > k }
  def all_gt(a, k) = a.all? { |x| x > k }
  def none_gt(a, k) = a.none? { |x| x > k }
  def upto_sum(a, b); s = 0; a.upto(b) { |i| s += i }; s; end
  def downto_sum(a, b); s = 0; a.downto(b) { |i| s += i }; s; end
  def local_seen(a, k); seen = []; r = a.find { |x| seen << x; x > k }; [r, seen]; end
end
f = F.new
a = [3, 1, 4, 1, 5, 9, 2, 6]
rows("find") { [0, 3, 8, 9, 2].map { |k| f.find_gt(a, k) } }
rows("detect") { [0, 3, 8, 9, 2].map { |k| f.detect_gt(a, k) } }
rows("find ifnone") { [0, 9, 3, 9].map { |k| f.find_none(a, k) } }
rows("find nil") { [0, 9].map { |k| f.find_nil(a, k) } }
rows("find_index") { [0, 3, 9, 5].map { |k| f.find_index_gt(a, k) } }
rows("index") { [0, 3, 9, 5].map { |k| f.index_gt(a, k) } }
rows("any?") { [0, 9, 8, 100].map { |k| f.any_gt(a, k) } }
rows("all?") { [0, 1, -1, 9].map { |k| f.all_gt(a, k) } }
rows("none?") { [0, 9, 8, 100].map { |k| f.none_gt(a, k) } }
rows("upto") { [[1, 4], [5, 4], [-2, 2], [1, 1]].map { |x, y| f.upto_sum(x, y) } }
rows("downto") { [[4, 1], [4, 5], [2, -2], [1, 1]].map { |x, y| f.downto_sum(x, y) } }
rows("locals fresh") { [2, 5, 0].map { |k| f.local_seen(a, k) } }
rows("empty") { [f.find_gt([], 0), f.any_gt([], 0), f.all_gt([], 0), f.none_gt([], 0), f.find_index_gt([], 0)] }
rows("bignum upto") { [f.upto_sum(2**64, 2**64 + 2), f.downto_sum(2**64 + 1, 2**64)] }
rows("float upto") { (f.upto_sum(1, 2.5) rescue $!.class) }

# no block, a pattern, a Symbol, a Proc, a lambda
rows("no block") { [[1].find.class, [1].find(-> { 0 }).each { false }, [nil, 1].any?, [1, nil].all?, [nil].none?, 1.upto(3).to_a] }
rows("pattern") { [a.any?(Integer), a.all?(1..9), a.none?(String), %w[ab cd].any?(/c/), [].all?(1)] }
rows("pattern+block") { [a.any?(9) { |x| x > 100 }, a.all?(Integer) { false }] }
rows("symbol") { [a.find(&:even?), a.any?(&:zero?), a.find_index(&:even?), [[1, 2]].find(&:last)] }
rows("lambda arity") { [([[1, 2]].find(&->(x, y) { y == 2 }) rescue $!.class), [[1, 2]].find(&->(x) { x == [1, 2] }),
                        ([[1, 2]].any?(&->(x, y) { true }) rescue $!.class), [[1, 2]].find { |x, y| y == 2 }] }
pr = proc { |x| x > 4 }
rows("stored proc") { 3.times.map { [a.find(&pr), a.any?(&pr), a.find_index(&pr)] } }

# break, next, return, redo-free retry by hand, and an exception from the block
class F
  def find_break(a) = a.find { |x| break [:broke, x] if x == 4; false }
  def any_next(a) = a.any? { |x| next false if x < 5; true }
  def find_return(a); a.find { |x| return [:ret, x] if x == 5; false }; :none; end
  def upto_break(n) = 1.upto(n) { |i| break i * 10 if i == 3 }
  def downto_return(n); n.downto(1) { |i| return i if i == 2 }; :none; end
  def find_raise(a, k)
    a.find { |x| raise ArgumentError, "at #{x}" if x == k; false }
  rescue ArgumentError => e
    e.message
  end
end
rows("break") { 3.times.map { f.find_break(a) } }
rows("next") { 3.times.map { f.any_next(a) } }
rows("return") { 3.times.map { [f.find_return(a), f.find_return([1])] } }
rows("upto break") { 3.times.map { [f.upto_break(5), f.upto_break(2)] } }
rows("downto return") { 3.times.map { [f.downto_return(4), f.downto_return(1)] } }
rows("raise") { [f.find_raise(a, 4), f.find_raise(a, 100), f.find_raise(a, 9), f.find_raise(a, 3)] }

# the block's Proc or scope escapes: binding, a proc, a lambda, a method made in it, eval
class F
  def find_binding(a, k); bs = []; a.find { |x| bs << binding; x > k }; bs.map { |b| b.local_variable_get(:x) }; end
  def any_proc(a, k); ps = []; y = k; a.any? { |x| ps << proc { [x, y] }; x > k }; ps.map(&:call); end
  def upto_lambda(n); ls = []; 1.upto(n) { |i| ls << -> { i * n } }; ls.map(&:call); end
  def find_eval(a, k) = a.find { |x| eval("x > k") }
  def find_method(a, k); o = Object.new; a.find { |x| o.define_singleton_method(:v) { x }; x > k }; o.v; end
  def find_outer_binding(a, k); r = a.find { |x| x > k }; b = binding; [r, b.local_variable_get(:k)]; end
end
rows("binding") { [f.find_binding(a, 3), f.find_binding(a, 0), f.find_binding(a, 4)] }
rows("proc") { [f.any_proc([1, 2, 3], 2), f.any_proc([5, 6], 0), f.any_proc([7], 9)] }
rows("lambda") { [f.upto_lambda(3), f.upto_lambda(1), f.upto_lambda(2)] }
rows("eval") { [f.find_eval(a, 3), f.find_eval(a, 5), f.find_eval(a, 0)] }
rows("method made") { [f.find_method(a, 3), f.find_method(a, 8), f.find_method(a, 4)] }
rows("outer binding") { [f.find_outer_binding(a, 3), f.find_outer_binding(a, 5)] }

# the receiver is not a plain Array: Enumerable's find, a subclass, a redefined Array#each, Hash, Range, Struct
class EachSub < Array; def each; yield 99; end; end
class FindSub < Array; def find; :own; end; end
class F
  def hash_find(h, v) = h.find { |k, x| x == v }
  def range_any(r, k) = r.any? { |x| x > k }
  def struct_find(s, k) = s.find { |x| x > k }
  def enum_find(e, k) = e.find { |x| x > k }
end
S3 = Struct.new(:a, :b, :c)
class Kept
  include Enumerable
  def each(&b); @b = b; [1, 2, 3].each(&b); self; end
  def again(x) = @b.call(x)
end
rows("subclass each") { [EachSub[1, 2].find { |x| x > 0 }, EachSub[1, 2].any? { |x| x == 99 }, EachSub[1].find_index { true }] }
rows("subclass find") { [FindSub[1].find] }
rows("hash") { [{ a: 1, b: 2 }, { c: 3 }, { a: 1, b: 2 }].map { |h| f.hash_find(h, 2) } }
rows("range") { [[1..5, 3], [1..2, 3], [1..5, 3]].map { |r, k| f.range_any(r, k) } }
rows("struct") { [S3.new(1, 2, 3), S3.new(5, 6, 7), S3.new(1, 2, 3)].map { |s| f.struct_find(s, 1) } }
kept = Kept.new
rows("kept by each") { r = 3.times.map { |i| f.enum_find(kept, i) }; [r, (kept.again(5) rescue $!.class), (kept.again(0) rescue $!.class), f.enum_find(kept, 1)] }
class Array
  alias each_orig each
  def each(&b) = each_orig(&b)
end
rows("Array#each redefined") { [f.find_gt(a, 4), f.any_gt(a, 8), f.find_index_gt(a, 4)] }
class Array
  alias each each_orig
end

# the array changes while it is searched
rows("mutation") { b = [1, 2, 3]; r = b.find { |x| b << x + 10 if b.size < 6; x > 11 }; [r, b] }
rows("shrink") { b = [1, 2, 3, 4]; r = b.any? { |x| b.pop; x > 3 }; [r, b] }

# recursion through the iterator, re-entrancy, a fiber and threads
class F
  def deep(n) = [n].find { |x| x == 0 || deep(x - 1) } && n
  def nest(a, k) = a.find { |x| a.any? { |y| y > x + k } }
  def count_up(n); s = []; 1.upto(n) { |i| s << (i < n ? count_up(i) : i) }; s; end
  def fiber_find(a, k) = a.find { |x| Fiber.yield x; x > k }
end
rows("recursion") { [f.deep(5), f.deep(0), f.deep(3)] }
rows("re-entrant") { [f.nest(a, 4), f.nest(a, 8), f.nest(a, 0)] }
rows("upto recursion") { [f.count_up(3), f.count_up(1)] }
fb = [Fiber.new { f.fiber_find([1, 2, 3], 1) }, Fiber.new { f.fiber_find([4, 5], 4) }]
rows("fiber") { r = []; 3.times { fb.each { |x| r << x.resume if x.alive? } }; r }
ok = 4.times.map { |t| Thread.new { (1..3000).all? { |k| f.find_gt([k, t, k + t], k) == k + t && f.upto_sum(t, t + 2) == 3 * t + 3 && f.any_gt([t], t - 1) } } }.map(&:value)
rows("threads") { ok }

# a TracePoint enabled between calls, and the backtrace from the block
rows("trace") { r = []; tp = TracePoint.new(:b_call) { |t| r << t.binding.local_variable_defined?(:x) if t.path == __FILE__ }
  4.times { |i| tp.enable if i == 2; f.find_gt([1, 2], 1) }; tp.disable; r }
def bt(m) = [1].public_send(m) { break caller(0, 2).map { |l| l[/'.*'/] } }
rows("frames") { [bt(:find), bt(:detect), bt(:any?), bt(:all?), bt(:none?), bt(:find_index), bt(:index)] }
rows("owners") { %i[find detect any? all? none? find_index index].map { |m| Array.instance_method(m).owner } }
rows("same method") { [Array.instance_method(:find) == Array.instance_method(:detect), [].method(:find).arity, [].method(:any?).arity] }
