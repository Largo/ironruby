# Differential matrix for a call of a method made by define_method (RubyLambdaMethodInfo): its arity
# errors, return/next/break, self, __method__, super, block_given? and the block passed in, binding,
# Method introspection, backtraces, closures, define_method with a Method/UnboundMethod/lambda,
# instance_exec, recursion, threads. Every row calls its method thousands of times, so that whatever
# a call site or a method body caches from one call to the next is exercised. Run under CRuby and under
# ./ir.sh - with the JIT on and off, and without light frames - and diff:
#
#   ruby Util/dmeth-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/dmeth-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/dmeth-matrix.rb > /tmp/nojit.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/dmeth-matrix.rb > /tmp/nolight.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt && diff /tmp/mri.txt /tmp/nolight.txt
#
# All must end with the row `end' and be identical. JIT state goes to stderr (IR_JIT_VERBOSE=1).

$stdout.sync = true
FILE = File.basename(__FILE__)
N = 3000

def frame(line)
  line.sub(/\A.*?#{Regexp.escape(FILE)}:/, '')
end

def err
  yield
  :no_error
rescue Exception => e
  [e.class, e.message]
end

def row(name, value)
  puts "#{name}: #{value.inspect}"
end

class Base
  def sup(x) = [:base, x]
  def isup(x) = [:ibase, x]
end

class D < Base
  define_method(:one) { |x| x + 1 }
  define_method(:zero) { 7 }
  define_method(:opt) { |a, b = 2| a + b }
  define_method(:rest) { |a, *r| [a, r.size] }
  define_method(:kw) { |a, k: 1| a + k }
  define_method(:trail) { |a,| a }
  define_method(:ret) { |x| return x * 2 if x.odd?; x }
  define_method(:nxt) { |x| next x * 3 if x.odd?; x }
  define_method(:brk) { |x| break x * 5 if x.odd?; x }
  define_method(:deep_ret) { |x| [1, 2].each { |y| return x + y if x.odd? }; x }
  define_method(:me) { self }
  define_method(:meth) { __method__ }
  define_method(:sup) { |x| super(x) + [:d] }
  define_method(:isup) { |x| super }
  define_method(:bg) { |&b| [block_given?, b ? b.call : nil] }
  define_method(:bg2) { block_given? }
  define_method(:yl) { |&b| [1, 2].map(&b) }
  define_method(:bind) { |x| y = x + 1; binding }
  define_method(:bt) { |x| raise ArgumentError, "bt #{x}" if x % 1000 == 999; x }
  define_method(:keep) { |x| z = x; -> { z } }
  define_method(:keepproc) { |x| proc { return x } }
  define_method(:rec) { |x| x <= 0 ? 0 : 1 + rec(x - 1) }
  define_method(:loc) { |x| v = x; v += 1; v }
  define_method(:ctr) { |x| x }
  define_method(:lr) { |x| y = x if x.odd?; y }
  define_method(:ie) { |x| instance_exec(x) { |y| [self.class, y] } }
  define_method(:lj) { |x| [1].each { break x } }
end

class D
  def plain(x) = x + 100
  define_method(:from_method, instance_method(:plain))
  define_method(:from_lambda, ->(x) { x + 200 })
  define_method(:from_bound, D.new.method(:plain))
  define_method(:from_unbound, Base.instance_method(:isup))
end

d = D.new
s = 0
N.times { |i| s += d.one(i) + d.zero + d.opt(i) + d.opt(i, 1) + d.rest(i, 1, 2)[1] + d.kw(i) + d.kw(i, k: 3) + d.trail(i) }
row "1. arguments", s

r = []
N.times { |i| r << err { d.one } << err { d.one(1, 2) } << err { d.zero(1) } << err { d.opt } << err { d.opt(1, 2, 3) } <<
  err { d.rest } << err { d.kw(1, 2) } << err { d.kw(1, z: 1) } << err { d.trail(1, 2) } << err { d.one(*[1, 2]) } if i % 500 == 0 || i == N - 1 }
row "2. arity errors", r.uniq
row "2a. arity errors counted", r.size

r = [0, 0, 0, 0, 0]
N.times { |i| r[0] += d.ret(i); r[1] += d.nxt(i); r[2] += d.brk(i); r[3] += d.deep_ret(i); r[4] += d.lj(i) }
row "3. return/next/break/nested return/break from each", r

r = 0
N.times { |i| r += 1 if d.me.equal?(d) && d.meth == :meth }
row "4. self and __method__", r

r = []
N.times { |i| r << d.sup(i) if i % 1000 == 0 }
row "5. explicit super", r
row "5a. implicit super", err { d.isup(1) }
row "5b. implicit super hot", (1..N).map { err { d.isup(1) } }.uniq

r = []
N.times { |i| r << [d.bg, d.bg { i }, d.bg2, d.bg2 { }, d.yl { |x| x + i }] if i % 1000 == 0 }
row "6. block_given? and the block", r

r = []
N.times { |i| b = d.bind(i); r << [b.local_variable_get(:x), b.local_variable_get(:y), b.receiver.equal?(d)] if i % 1000 == 0 }
row "7. binding", r

m = d.method(:opt)
row "8. arity/parameters", [D.instance_method(:one).arity, m.arity, d.method(:rest).arity, d.method(:kw).arity, d.method(:trail).arity,
  d.method(:zero).arity, m.parameters, d.method(:kw).parameters, d.method(:trail).parameters, m.owner, m.name]
row "8a. source_location", [m.source_location[0] == __FILE__, d.method(:from_lambda).source_location[0] == __FILE__]

r = []
N.times do |i|
  begin
    d.bt(i)
  rescue ArgumentError => e
    r << [e.message, e.backtrace.first(2).map { |l| frame(l) }, e.backtrace_locations[0].label]
  end
end
row "9. backtraces", r

ls = []
N.times { |i| ls << d.keep(i) }
row "10. closures kept", [ls[0].call, ls[1500].call, ls[-1].call]
ps = []
N.times { |i| ps << d.keepproc(i) if i % 1000 == 0 }
row "10a. return from a proc after its method", ps.map { |pr| err { pr.call } }
counter = 0
D.send(:define_method, :cl) { counter += 1 }
N.times { d.cl }
counter += 10
row "10b. block over a local changed later", [counter, d.cl]

s = 0
N.times { |i| s += d.from_method(i) + d.from_lambda(i) + d.from_bound(i) + d.from_unbound(i)[1] }
row "11. from Method/lambda", [s, err { d.from_lambda }, d.method(:from_lambda).arity, d.method(:from_method).arity]

r = []
N.times { |i| r << d.ie(i) if i % 1000 == 0 }
row "12. instance_exec", r

row "13. recursion", [d.rec(50), (1..N).sum { |i| d.rec(i % 5) }]
row "13a. deep recursion", err { d.rec(1_000_000) }.first

r = []
ths = 4.times.map { |t| Thread.new { x = 0; 20_000.times { |i| x += d.loc(i) + d.ctr(t) }; x } }
row "14. threads", ths.map(&:value)

r = []
N.times { |i| r << d.loc(i) << d.lr(i) }
row "15. locals reset between calls", [r.compact.sum, r.count(nil)]

# A proc made in one call that returns while a second call of the same method is running.
class D
  define_method(:outer) { |pr| pr ? [pr.call, :after] : proc { return :first } }
end
saved = d.outer(nil)
r = []
N.times { r << err { d.outer(saved) } }
row "16. stale return while another call runs", r.uniq

# A method's body returns with `return' from a block yielded to by another define_method method.
class D
  define_method(:yielder) { |&b| [b.call, :after_yield] }
  define_method(:caller_) { |x| yielder { return x } ; :not_here }
end
r = 0
N.times { |i| r += d.caller_(i) }
row "17. return through another bmethod's block", r

row "18. singleton define_method", (o = Object.new; o.define_singleton_method(:sm) { |x| x * 2 }; (1..N).sum { |i| o.sm(i) })
row "19. respond_to and send", [d.respond_to?(:one), (1..N).sum { |i| d.send(:one, i) + d.public_send(:zero) }]

puts 'end'
