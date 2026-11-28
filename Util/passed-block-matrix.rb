# Differential matrix for a named block that a method only passes on (IR_PASSED_BLOCKS: RubyOps.EnterLightMethodPassingBlock,
# HoldPassedBlock, EndPassedBlock; Proc.PassHeldFlag): `def each(&b) = @items.each(&b)'. The caller's literal stays reusable -
# made again for the caller's next call, and its scope parked - unless a call it was passed to may keep it. Each case runs a
# caller hot, so that its literal is reused when it may be, around the call that keeps the block, and then looks at what was
# kept: it must be the block of the call that kept it - its locals, its self, its identity. Run it under CRuby and under ./ir.sh
# with the switch on and off and with the tiers off, and diff:
#
#   ruby Util/passed-block-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/passed-block-matrix.rb > /tmp/on.txt
#   IR_PASSED_BLOCKS=0 ./ir.sh Util/passed-block-matrix.rb > /tmp/off.txt
#   IR_PARKED_SCOPES=0 ./ir.sh Util/passed-block-matrix.rb > /tmp/noparked.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/passed-block-matrix.rb > /tmp/notiers.txt

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

class Store
  def initialize; @items = [1, 2, 3]; @saved = []; end
  attr_reader :saved
  def each_yield; @items.each { |x| yield x }; end                  # a light frame that only yields
  def keep(&b); @saved << b; nil; end                                 # keeps it
  def given?; block_given?; end
  def ident(&b); b; end
end

class Pass
  def initialize(s); @s = s; end
  def each(&b) = @s.each_yield(&b)                                    # passes it to one that only yields
  def each_lib(&b) = [1, 2].each(&b)                                  # to a [BlockNotKept] library method
  def keep(&b) = @s.keep(&b)                                          # to one that keeps it
  def maybe_keep(k, &b); k ? @s.keep(&b) : @s.each_yield(&b); end     # to one or the other
  def given(&b) = @s.given?(&b)
  def ident(&b) = @s.ident(&b)
  def hash_default(&b) = Hash.new(&b)                                 # a library method that keeps it
  def define(name, &b) = self.class.send(:define_method, name, &b)
  def enum(&b) = Enumerator.new(&b)
  def chain(&b) = Pass.new(@s).each(&b)                               # through another method that only passes it on
  def chain_keep(&b) = Pass.new(@s).keep(&b)
end

class Caller
  def initialize(p); @p = p; @n = 0; end
  def sum; c = 0; @p.each { |x| c += x }; c; end
  def sum_lib; c = 0; @p.each_lib { |x| c += x }; c; end
  def keep(n); @p.keep { [n, self.class] }; end
  def maybe(n); c = 0; @p.maybe_keep(n % 1000 == 7) { |*x| c += 1; [n, c] }; c; end
  def given; @p.given { 1 }; end
  def ident(n); @p.ident { n }; end
  def hdef(n); @p.hash_default { |h, k| [n, k] }; end
  def enum(n); @p.enum { |y| y << n; y << n + 1 }; end
  def chain; c = 0; @p.chain { |x| c += x }; c; end
  def chain_keep(n); @p.chain_keep { n * 2 }; end
  def brk; @p.each { |x| break x * 100 if x == 2 }; end
end

t('passed to a light frame that only yields, hot') { s = Store.new; c = Caller.new(Pass.new(s)); r = 0; N.times { r += c.sum }; r }
t('passed to a [BlockNotKept] library method, hot') { c = Caller.new(Pass.new(Store.new)); r = 0; N.times { r += c.sum_lib }; r }
t('passed through two methods, hot') { c = Caller.new(Pass.new(Store.new)); r = 0; N.times { r += c.chain }; r }

t('kept by the receiver: every kept block is its own call\'s') {
  s = Store.new
  c = Caller.new(Pass.new(s))
  N.times { |n| c.keep(n) }
  s.saved.map(&:call).values_at(0, 1, 1500, -1) + [s.saved.map(&:object_id).uniq.size]
}
t('kept on some calls only') {
  s = Store.new
  c = Caller.new(Pass.new(s))
  N.times { |n| c.maybe(n) }
  s.saved.map { |b| b.call(:z) }
}
t('kept through a chain of passing methods') {
  s = Store.new
  c = Caller.new(Pass.new(s))
  N.times { |n| c.chain_keep(n) if n % 500 == 0; c.chain }
  s.saved.map(&:call)
}
t('a Hash default proc kept by Hash.new') {
  c = Caller.new(Pass.new(Store.new))
  hs = []
  N.times { |n| h = c.hdef(n); hs << h if n % 1000 == 1; c.sum }
  hs.map { |h| h[:k] }
}
t('define_method with a passed block') {
  pa = Pass.new(Store.new)
  c = Caller.new(pa)
  N.times { c.sum }
  pa.define(:dm1) { |a| a * 3 }
  N.times { c.sum }
  [pa.dm1(4), Pass.instance_method(:dm1).arity]
}
t('Enumerator.new with a passed block') {
  c = Caller.new(Pass.new(Store.new))
  es = []
  N.times { |n| es << c.enum(n) if n % 1000 == 2; c.sum }
  es.map(&:to_a)
}
t('the block is the same object in both methods') {
  pa = Pass.new(Store.new)
  bl = proc { 1 }
  [pa.ident(&bl).equal?(bl), Caller.new(pa).ident(5).call]
}
t('identity of a literal passed through, kept: distinct per call') {
  c = Caller.new(Pass.new(Store.new))
  ids = Array.new(5) { |n| c.ident(n) }
  [ids.map(&:call), ids.map(&:object_id).uniq.size, ids.map(&:lambda?).uniq]
}
t('block_given? in the receiver') { c = Caller.new(Pass.new(Store.new)); [c.given, Pass.new(Store.new).given] }
t('break from a passed block') { c = Caller.new(Pass.new(Store.new)); r = nil; N.times { r = c.brk }; r }
t('a Method passed as a block') {
  pa = Pass.new(Store.new)
  m = 10.method(:+)
  r = []
  pa.each(&m) rescue r << :err
  r << [1, 2].map(&m)
  r
}
t('a lambda passed through') {
  pa = Pass.new(Store.new)
  l = ->(x) { x * 2 }
  out = []
  pa.each { |x| out << x }
  [pa.ident(&l).lambda?, (pa.each(&l) rescue :argerr), out]
}
t('an exception while the block is held') {
  c = Caller.new(Pass.new(Store.new))
  errs = 0
  N.times { |n| begin; c.instance_variable_get(:@p).each { |x| raise 'x' if x == 3 && n % 500 == 0 }; rescue RuntimeError; errs += 1; end; c.sum }
  [errs, c.sum]
}
t('threads passing blocks through one method') {
  pa = Pass.new(Store.new)
  ths = Array.new(4) { |ti| Thread.new { c = Caller.new(pa); r = 0; 2000.times { r += c.sum }; [ti, r] } }
  ths.map(&:value)
}
t('threads: kept blocks while others pass') {
  s = Store.new
  pa = Pass.new(s)
  ths = Array.new(4) { |ti| Thread.new { c = Caller.new(pa); 1000.times { |n| c.keep(n * 4 + ti) if n % 250 == 0; c.sum } } }
  ths.each(&:join)
  s.saved.map { |b| b.call[0] }.sort
}
