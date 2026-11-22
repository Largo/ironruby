# Differential matrix for parked method scopes (RubyOps.CreateMethodScopeParked): a light-frame method whose
# literal blocks all reuse their Procs (`def each; (a..b).each { |i| yield @s[i] }; end', som.rb's Vector#each)
# keeps its scope and those Procs, once a call has returned normally and nothing captured them, for the next call
# of the same body. Every row is a way the scope, a Proc or a BlockParam could outlive the call or be shared with
# another call. Run it under CRuby and under ./ir.sh with the parking on and off, and diff:
#
#   ruby Util/parked-scope-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/parked-scope-matrix.rb > /tmp/on.txt
#   IR_PARKED_SCOPES=0 ./ir.sh Util/parked-scope-matrix.rb > /tmp/off.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/parked-scope-matrix.rb > /tmp/noframes.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/parked-scope-matrix.rb > /tmp/notiers.txt
#
# IR_PARKED_SCOPES=verbose names on stderr each method compiled with a parking slot.
#
# Each probe runs N times and prints each distinct answer once, so the first call (a new scope), the
# second (the parked one taken) and calls with a bound site all meet the case.

N = 3000

def t(label)
  print label.ljust(58), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 60]}"
  end
end

class V
  attr_reader :tag
  def initialize(tag, *items); @tag = tag; @storage = items; @first_idx = 0; @last_idx = items.size; end
  def each; (@first_idx..(@last_idx - 1)).each { |i| yield @storage[i] }; end
  def each_self; (@first_idx..(@last_idx - 1)).each { |i| yield @tag }; end
  def two; (0..0).each { |i| yield :a }; [1].each { |x| yield :b }; end
  def twice; 2.times { |k| (0..0).each { |i| yield k } }; end
  def loop_lit; r = []; i = 0; while i < 3; [i].each { |x| r << x }; i += 1; end; r; end
  def each_match(s); (0..0).each { |i| s =~ /(b+)/ }; $1; end
  def each_method; (0..0).each { |i| yield __method__ }; end
  def each_given; (0..0).each { |i| yield block_given? }; end
  def each_binding; b = nil; (0..0).each { |i| b = binding }; b; end
  def each_local(x); (0..0).each { |i| yield x }; end
  def each_brk; (0..5).each { |i| break i * 10 if i == 2 }; end
  def each_next; r = []; (0..3).each { |i| next if i.odd?; r << i }; r; end
  def each_raise; (0..0).each { |i| raise ArgumentError, "x#{i}" }; end
  def each_throw; (0..0).each { |i| throw :t, @tag }; end
  def each_redo; n = 0; (0..0).each { |i| n += 1; redo if n < 3 }; n; end
  # what W#each_zsuper's super() reaches
  alias each_zsuper each
end
class W < V
  def each; super; end              # zsuper
  def each_zsuper; super(); end     # super() with no arguments, the block passed implicitly
end

$keep = []
def keep_block(&b); $keep << b; b; end

v1 = V.new(:v1, 1, 2, 3)
v2 = V.new(:v2, 7)

# ---------------------------------------------------------------- 1. plain calls, another self each call

t('each sums') { |i| s = 0; v1.each { |x| s += x }; s }
t('each over two receivers alternately') { |i| r = []; (i.even? ? v1 : v2).each { |x| r << x }; r }
t('each_self sees the self of this call') { |i| o = i.even? ? v1 : v2; a = []; o.each_self { |x| a << x }; a }
t('two literals in one method') { |i| a = []; v1.two { |x| a << x }; a }
t('a literal nested in a literal') { |i| a = []; v1.twice { |x| a << x }; a }
t('a literal in a while') { |i| v1.loop_lit }
t('the caller block differs each call') { |i| a = []; (i.even? ? v1.each { |x| a << x } : v1.each { |x| a << -x }); a }
t('each_local with an argument') { |i| r = nil; v1.each_local(i % 3) { |x| r = x }; r }

# ---------------------------------------------------------------- 2. what the frame still answers

t('$~ and $1 per call') { |i| v1.each_match(i.even? ? 'abbc' : 'xbz') }
t('__method__ in the block') { |i| r = nil; v1.each_method { |x| r = x }; r }
t('block_given? in the block') { |i| r = nil; v1.each_given { |x| r = x }; r }
t('binding from the block, then another call') { |i| b = v1.each_binding; v2.each { }; b.receiver.tag }
t('binding local_variables') { |i| v1.each_binding.local_variables }

# ---------------------------------------------------------------- 3. the block kept or the call left early

t('caller keeps its block with &b') { |i| $keep.clear; v1.each(&keep_block { |x| x * 2 }); $keep[0].call(5) }
t('caller block as a proc called later') { |i| pr = proc { |x| x + i }; v1.each(&pr); pr.call(1) - i }
t('break out of the inner literal') { |i| v1.each_brk }
t('break out of the caller block') { |i| v1.each { |x| break x * 3 } }
t('next in the inner literal') { |i| v1.each_next }
t('redo in the inner literal') { |i| v1.each_redo }
def ret1(v); v.each { |x| return x }; :no; end
t('return from the caller block') { |i| ret1(v1) }
t('raise from the inner literal') { |i| begin; v1.each_raise; rescue ArgumentError => e; e.message; end }
t('raise from the caller block, then a call') { |i| begin; v1.each { |x| raise 'e' if x == 2 }; rescue => e; end; s = 0; v1.each { |x| s += x }; [e.message, s] }
t('throw from the inner literal') { |i| catch(:t) { v2.each_throw } }
t('retry around a call') { |i| n = 0; begin; n += 1; v1.each { |x| raise 'r' if n < 2 }; rescue; retry; end; n }
t('caller block makes a closure') { |i| ps = []; v1.each { |x| ps << -> { x } }; ps.map(&:call) }

# ---------------------------------------------------------------- 4. recursion, enumerators, fibers

$depth = 0
t('recursion through the same body') { |i| a = []; v1.each { |x| v2.each { |y| v1.each { |z| a << [x, y, z] } } }; a.size }
def deep(v, n); return n if n == 0; r = nil; v.each { |x| r = deep(v, n - 1) }; r; end
t('deep recursion through each') { |i| deep(v2, 200) }
def forever(v); v.each { |x| forever(v) }; end
t('runaway recursion is a SystemStackError') { |i| next :skip if i > 2; begin; forever(v2); rescue SystemStackError; :sse; end }
t('to_enum(:each) external next') { |i| e = v1.to_enum(:each); a = [e.next, e.next]; v1.each { }; a << e.next; a }
t('enumerator abandoned, then a call') { |i| e = v1.to_enum(:each); e.next; s = 0; v1.each { |x| s += x }; s }
t('fiber suspended inside each') { |i| f = Fiber.new { v1.each { |x| Fiber.yield x } ; :done }; a = [f.resume]; s = 0; v1.each { |x| s += x }; a << f.resume << f.resume << f.resume; a << s }
t('method(:each).to_proc') { |i| s = 0; v1.method(:each).to_proc.call { |x| s += x }; s }
t('instance_exec with each') { |i| a = []; v2.instance_exec { each { |x| a << [x, @tag] } }; a }
t('send and public_send') { |i| a = []; v2.send(:each) { |x| a << x }; v2.public_send(:each) { |x| a << x }; a }
t('super and zsuper pass the block') { |i| w = W.new(:w, 4, 5); a = []; w.each { |x| a << x }; w.each_zsuper { |x| a << x }; a }
t('define_method with each') { |i| V.send(:define_method, :dm) { |&b| each(&b) }; a = []; v2.dm { |x| a << x }; a }

# ---------------------------------------------------------------- 5. threads (no GVL) and tracing

t('four threads in each at once') { |i| next :skip if i > 20; ts = 4.times.map { |k| Thread.new { s = 0; o = V.new(k, k, k + 1); 200.times { o.each { |x| s += x } }; s == 200 * (2 * k + 1) } }; ts.map(&:value).all? }
t('shared receiver from four threads') { |i| next :skip if i > 20; ts = 4.times.map { Thread.new { s = 0; 300.times { v1.each { |x| s += x } }; s } }; ts.map(&:value).uniq }
t('thread killed inside each, then a call') { |i| next :skip if i > 20; q = Queue.new; th = Thread.new { v1.each { |x| q << 1; sleep } }; q.pop; th.kill; th.join; s = 0; v1.each { |x| s += x }; s }
t('Thread#raise inside each, then a call') { |i| next :skip if i > 20; q = Queue.new; th = Thread.new { begin; v1.each { |x| q << 1; sleep }; rescue => e; e.message; end }; q.pop; th.raise('tr'); r = th.value; s = 0; v1.each { |x| s += x }; [r, s] }
t('TracePoint :call keeps binding') { |i| next :skip if i > 20; bs = []; tp = TracePoint.new(:call) { |x| bs << x.binding if x.method_id == :each_local }; tp.enable { v1.each_local(i) { } }; v1.each_local(99) { }; bs.map { |b| b.local_variable_get(:x) } }
def ps_short_sum(a); t = 0.0; a.each { |x| t += x * 1.5 }; t; end
t('four threads taking and parking one slot') { |i| next :skip if i > 2; 4.times.map { |k| Thread.new { s = 0.0; 20000.times { s += ps_short_sum([1.0, 2.0, k.to_f]) }; s } }.map(&:value) }
t('ObjectSpace untouched') { |i| s = 0; v1.each { |x| s += x }; ObjectSpace.respond_to?(:each_object) && s }

# A literal in a `class << self' body inside the method has no block around it either, but it belongs to the module
# body's scope, not the method's: it is not parked with the method (the method used not to compile at all).
class PsSingleton
  def run(n)
    s = 0
    class << self
      [1, 2].each { |x| (@acc ||= []) << x }
    end
    [n, n].each { |y| s += y }
    s
  end
  def acc = singleton_class.instance_variable_get(:@acc).size
end
pss = PsSingleton.new
t('a literal in a class << self body inside the method') { |i| [pss.run(i % 3), pss.acc > 0] }

# ---------------------------------------------------------------- 6. the method's own block handed on (super: not parked)

class P1; def run(&b); $kb = b; yield 1; end; end
class P2 < P1; def run; [0].each { |z| z }; super; end; end
class P3; def run; [0].each { |z| z }; yield 2; end; end
t('super hands the block on, kept, break called later') { |i| P2.new.run { |x| break x }; P2.new.run { }; begin; $kb.call; rescue LocalJumpError => e; e.reason; end }
t('a kept caller block after another call of the body') { |i| pr = nil; P3.new.run { |x| pr = proc { break x } }; P3.new.run { }; begin; pr.call; rescue LocalJumpError => e; e.reason; end }
puts 'end'
