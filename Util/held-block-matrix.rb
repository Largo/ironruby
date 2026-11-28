# Differential matrix for a literal block given to a light-frame method whose scope is made (Proc.HeldFlag,
# IR_SCOPE_HELD_BLOCKS): the scope holds the block, which is not reusable until the scope is parked; parking proves that
# nothing kept the scope. Each case is a caller whose literal is reused for its next call (and its scope parked) unless the
# callee's scope escaped - through a binding, a Proc of the callee's that is kept, an exception, a fiber or a thread - and
# then the kept thing reaches the caller's block, which must still be the block of the call it was given in: its self, its
# locals, its object identity. Run it under CRuby and under ./ir.sh with the switch on and off and with the tiers off, and diff:
#
#   ruby Util/held-block-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/held-block-matrix.rb > /tmp/on.txt
#   IR_SCOPE_HELD_BLOCKS=0 ./ir.sh Util/held-block-matrix.rb > /tmp/off.txt
#   IR_PARKED_SCOPES=0 ./ir.sh Util/held-block-matrix.rb > /tmp/noparked.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/held-block-matrix.rb > /tmp/notiers.txt

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

class Vec
  def initialize(n); @s = Array.new(n) { |i| i + 1 }; @first = 0; @last = n; @kept = []; end
  attr_reader :kept
  # the shape the change is for: a light frame whose literal needs its scope, given a literal by its caller
  def each; (@first..(@last - 1)).each { |i| yield @s[i] }; end
  def each_bind(take); b = nil; (@first..(@last - 1)).each { |i| yield @s[i]; b = binding if take && i == 0 }; b; end
  def each_keep(take); (@first..(@last - 1)).each { |i| yield @s[i] }; @kept << proc { |x| yield x } if take; nil; end
  def each_raise(at); (@first..(@last - 1)).each { |i| raise IndexError, "at #{i}" if i == at; yield @s[i] }; end
  def each_fiber; (@first..(@last - 1)).each { |i| Fiber.yield(yield(@s[i])) }; end
end

class Caller
  def initialize(v, tag); @v = v; @tag = tag; end
  def sum; c = 0; @v.each { |x| c += x }; c; end
  def sum_bind(n); c = 0; b = @v.each_bind(n % 1000 == 1) { |x| c += x * 10 + n }; [c, b]; end
  def sum_keep(n); c = [n]; @v.each_keep(n % 1000 == 2) { |x| c << x; [@tag, n, x] }; c.size; end
  def sum_raise(n); c = 0; @v.each_raise(n % 700 == 3 ? 1 : -1) { |x| c += x }; c; end
  def tag; @tag; end
end

t('the shape, hot') { v = Vec.new(4); c = Caller.new(v, :a); s = 0; N.times { s += c.sum }; s }

t('a binding of the callee kept: yield through it later') {
  v = Vec.new(3)
  c = Caller.new(v, :a)
  kept = []
  N.times { |n| r, b = c.sum_bind(n); kept << [n, b] if b }
  N.times { |n| c.sum_bind(n + 5) }
  kept.map { |n, b| [n, b.eval('yield 100'), b.receiver.equal?(v)] }
}

t('a Proc of the callee kept: it yields to the caller block later') {
  v = Vec.new(3)
  a = Caller.new(v, :a)
  bb = Caller.new(v, :b)
  N.times { |n| (n.even? ? a : bb).sum_keep(n) }
  v.kept.map { |pr| pr.call(7) }
}

t('an exception leaves the callee after its scope is made') {
  v = Vec.new(3)
  c = Caller.new(v, :a)
  errs = []
  s = 0
  N.times { |n| begin; s += c.sum_raise(n); rescue IndexError => e; errs << [n, e.message]; end }
  [s, errs.first(3), errs.size]
}

t('a callee suspended in a fiber while the caller runs again') {
  v = Vec.new(3)
  c = Caller.new(v, :a)
  N.times { c.sum }
  seen = []
  f = Fiber.new { v.each_fiber { |x| seen << [:fiber, x]; x * 2 } }
  r1 = f.resume
  mid = Array.new(100) { c.sum }.last
  r2 = f.resume
  [r1, mid, r2, seen]
}

t('threads: one caller per thread, one callee shared') {
  v = Vec.new(5)
  ths = Array.new(4) { |ti| Thread.new { c = Caller.new(v, ti); s = 0; 3000.times { s += c.sum }; s } }
  ths.map(&:value)
}

t('threads: bindings kept while others call') {
  v = Vec.new(3)
  ths = Array.new(4) do |ti|
    Thread.new do
      c = Caller.new(v, ti)
      kept = []
      2000.times { |n| r, b = c.sum_bind(n * 4 + ti); kept << b if b }
      kept.map { |b| b.eval('yield 1') }
    end
  end
  ths.map(&:value)
}

t('block_given? and the block identity across calls') {
  k = Class.new do
    def initialize; @a = [1, 2]; end
    def m; seen = nil; (0..1).each { |i| seen = block_given?; yield @a[i] }; seen; end
  end.new
  c = Class.new do
    def initialize(k); @k = k; @ids = []; end
    attr_reader :ids
    def go(n); @k.m { |x| x + n }; end
  end.new(k)
  r = nil
  N.times { |n| r = c.go(n) }
  r
}

t('break from the caller block through the callee') {
  v = Vec.new(4)
  k = Class.new { def initialize(v); @v = v; end; def first_big(lim); @v.each { |x| break x if x > lim }; end }.new(v)
  s = []
  N.times { |n| s << k.first_big(n % 4) if n > N - 5 }
  s
}
