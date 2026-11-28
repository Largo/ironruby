# Differential matrix for a method with a lambda literal (`->(x) { }') in a light frame (MethodDefinition.IsScopeFree,
# IR_LIGHT_LAMBDA): the lambda is made over the method's scope, which the frame makes on demand when the literal runs,
# as for a block literal. Each row runs its methods hot, then once more for the row. CRuby's rules the rows check:
#
# - `return' and `next' in a lambda leave the lambda; `break' too; `return' in a block inside a lambda leaves the lambda;
# - a lambda kept past its method's return still reads and writes the method's locals;
# - a lambda checks its arity (ArgumentError), `lambda(&blk)' of a non-lambda Proc stays a proc;
# - binding, eval, local_variables in a lambda see the method's locals; self, __method__, block_given? and yield are
#   the method's;
# - curry, arity, parameters, lambda?;
# - a lambda made only on a rare path; lambdas made in a loop, each over the same scope; threads.
#
#   ruby Util/light-lambda-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/light-lambda-matrix.rb > /tmp/ir.txt && diff /tmp/mri.txt /tmp/ir.txt
#   IR_LIGHT_LAMBDA=0 ./ir.sh Util/light-lambda-matrix.rb > /tmp/off.txt && diff /tmp/mri.txt /tmp/off.txt
#
# Every row must match, and the last line be `end'.

$stdout.sync = true
N = 2000

def row(name, value)
  puts "#{name}: #{value.inspect}"
end

def err
  yield
rescue Exception => e
  [e.class, e.message]
end

def hot(n = N)
  r = nil
  n.times { r = yield }
  r
end

class L
  def ret(x) = (l = ->(y) { return y * 2 if y; :never }; [l.(x), :after])
  def nxt(x) = (l = ->(y) { next y + 1 if y > 0; :neg }; [l.(x), l.(-x)])
  def brk(x) = (l = ->(y) { break y * 3 }; [l.(x), :after])
  def ret_in_block(x)
    l = ->(a) { a.each { |e| return e if e > x }; :none }
    [l.([1, 5, 9]), :after]
  end
  def ret_in_loop(x)
    r = []
    i = 0
    while i < 3
      r << ->(v) { return v + i }.call(x)
      i += 1
    end
    r
  end
  def kept(x)
    count = x
    [-> { count += 1 }, -> { count }]
  end
  def arity_err(x) = (l = ->(a, b) { a + b }; [l.(x, 1), err { l.(x) }, err { l.(1, 2, 3) }])
  def opt(x) = ->(a, b = 2, *r, k: 1, &blk) { [a, b, r, k, blk&.call] }.call(x, 3, 4, k: 5) { :blk }
  def bind(x) = (z = x * 10; l = -> { binding.local_variable_get(:z) + eval("z") + z }; [l.(), local_variables.sort])
  def lv(x) = (q = x; -> { [q, local_variables.sort] }.call)
  def slf(x) = -> { [self.class, __method__, x] }.call
  def bg(x) = -> { [block_given?, (yield x if block_given?)] }.call
  def sets(x) = (v = 0; ->(n) { v = n * x }.call(4); v)
  def curry(x) = ->(a, b, c) { a + b + c }.curry[x][2][3]
  def meta(x) = (l = ->(a, b = 1) { }; [l.arity, l.parameters, l.lambda?, ->(*a) { }.arity, x])
  def from_proc(x) = (pr = proc { |a, b| [a, b] }; err { l = lambda(&pr); [l.lambda?, l.call(x), l.call(x, 2, 3)] })
  def rare(x)
    return x + 1 if x < 1_000_000
    ->(y) { y }.call(x)
  end
  def rare_hit(x) = (x > 5 ? ->(y) { y * x }.(2) : x)
  def many(x) = Array.new(3) { |i| ->(y) { y + i + x } }.map { |l| l.(1) }
  def nested(x) = ->(a) { ->(b) { a + b + x } }.(1).(2)
  def call_with_block(x) = ->(&b) { b.call(x) }.call { |v| v * 7 }
  def raise_in(x) = (l = -> { raise ArgumentError, "in lambda #{x}" }; err { l.() })
  def to_proc_lam(x) = [1, 2].map(&->(v) { v * x })
  def super_like(x) = -> { __method__ }.call
  def defined_yield(x) = -> { defined?(yield) }.call
end

# the same methods, each lambda in a branch: these are light frames (HasUnconditionalLambda)
class LC
  def ret(x) = x ? ((l = ->(y) { return y * 2 if y; :never }; [l.(x), :after])) : :none
  def nxt(x) = x ? ((l = ->(y) { next y + 1 if y > 0; :neg }; [l.(x), l.(-x)])) : :none
  def brk(x) = x ? ((l = ->(y) { break y * 3 }; [l.(x), :after])) : :none
  def ret_in_block(x)
    if x
      l = ->(a) { a.each { |e| return e if e > x }; :none }
      [l.([1, 5, 9]), :after]
    end
  end
  def ret_in_loop(x)
    if x
      r = []
      i = 0
      while i < 3
        r << ->(v) { return v + i }.call(x)
        i += 1
      end
      r
    end
  end
  def kept(x)
    if x
      count = x
      [-> { count += 1 }, -> { count }]
    end
  end
  def arity_err(x) = x ? ((l = ->(a, b) { a + b }; [l.(x, 1), err { l.(x) }, err { l.(1, 2, 3) }])) : :none
  def opt(x) = x ? (->(a, b = 2, *r, k: 1, &blk) { [a, b, r, k, blk&.call] }.call(x, 3, 4, k: 5) { :blk }) : :none
  def bind(x) = x ? ((z = x * 10; l = -> { binding.local_variable_get(:z) + eval("z") + z }; [l.(), local_variables.sort])) : :none
  def lv(x) = x ? ((q = x; -> { [q, local_variables.sort] }.call)) : :none
  def slf(x) = x ? (-> { [self.class, __method__, x] }.call) : :none
  def bg(x) = x ? (-> { [block_given?, (yield x if block_given?)] }.call) : :none
  def sets(x) = x ? ((v = 0; ->(n) { v = n * x }.call(4); v)) : :none
  def curry(x) = x ? (->(a, b, c) { a + b + c }.curry[x][2][3]) : :none
  def meta(x) = x ? ((l = ->(a, b = 1) { }; [l.arity, l.parameters, l.lambda?, ->(*a) { }.arity, x])) : :none
  def from_proc(x) = x ? ((pr = proc { |a, b| [a, b] }; err { l = lambda(&pr); [l.lambda?, l.call(x), l.call(x, 2, 3)] })) : :none
  def rare(x)
    if x
      return x + 1 if x < 1_000_000
      ->(y) { y }.call(x)
    end
  end
  def rare_hit(x) = x ? ((x > 5 ? ->(y) { y * x }.(2) : x)) : :none
  def many(x) = x ? (Array.new(3) { |i| ->(y) { y + i + x } }.map { |l| l.(1) }) : :none
  def nested(x) = x ? (->(a) { ->(b) { a + b + x } }.(1).(2)) : :none
  def call_with_block(x) = x ? (->(&b) { b.call(x) }.call { |v| v * 7 }) : :none
  def raise_in(x) = x ? ((l = -> { raise ArgumentError, "in lambda #{x}" }; err { l.() })) : :none
  def to_proc_lam(x) = x ? ([1, 2].map(&->(v) { v * x })) : :none
  def super_like(x) = x ? (-> { __method__ }.call) : :none
  def defined_yield(x) = x ? (-> { defined?(yield) }.call) : :none
end

class LSub < L
  def ret(x) = ->(v) { super(v) }.call(x)
end

class LCSub < LC
  def ret(x) = x ? ->(v) { super(v) }.call(x) : :none
end

o = L.new
%i[ret nxt brk ret_in_block ret_in_loop arity_err opt bind lv slf sets curry meta from_proc rare rare_hit many nested
   call_with_block raise_in to_proc_lam super_like defined_yield].each do |name|
  row name, [hot { o.send(name, 3) }, o.send(name, 7)]
end
row "bg", [hot { o.bg(2) }, o.bg(2) { |v| v * 5 }]
inc, get = o.kept(10)
row "kept", [inc.(), inc.(), get.(), hot { o.kept(1)[0].call }]
row "super in lambda", [hot { LSub.new.ret(4) }]
row "defined_yield with block", [o.defined_yield(1) { }]
row "rare hit", [o.rare(2_000_000)]
ths = 4.times.map { |t| Thread.new { r = nil; 500.times { r = [o.many(t), o.kept(t)[0].call, o.ret_in_loop(t)] }; r } }
row "threads", ths.map(&:value)
def deep(n) = n == 0 ? -> { :bottom } : ->(l) { l }.call(deep(n - 1))
row "recursion", [deep(50).call]
o = LC.new
%i[ret nxt brk ret_in_block ret_in_loop arity_err opt bind lv slf sets curry meta from_proc rare rare_hit many nested
   call_with_block raise_in to_proc_lam super_like defined_yield].each do |name|
  row "c.#{name}", [hot { o.send(name, 3) }, o.send(name, 7)]
end
row "c.bg", [hot { o.bg(2) }, o.bg(2) { |v| v * 5 }]
cinc, cget = o.kept(10)
row "c.kept", [cinc.(), cinc.(), cget.(), hot { o.kept(1)[0].call }]
row "c.super in lambda", [hot { LCSub.new.ret(4) }]
row "c.defined_yield with block", [o.defined_yield(1) { }]
row "c.rare hit", [o.rare(2_000_000)]
cths = 4.times.map { |t| Thread.new { r = nil; 500.times { r = [o.many(t), o.kept(t)[0].call, o.ret_in_loop(t)] }; r } }
row "c.threads", cths.map(&:value)
def cdeep(n) = n == 0 ? -> { :bottom } : ->(l) { l }.call(cdeep(n - 1))
row "c.recursion", [cdeep(50).call]
row "c.branch not taken", %i[ret nxt brk ret_in_block ret_in_loop bind sets many kept].map { |n| hot { LC.new.send(n, nil) } }
puts "end"
