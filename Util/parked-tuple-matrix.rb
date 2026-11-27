# Differential matrix for the locals tuple a method that parks its scope keeps for its next call (IR_PARKED_LOCALS:
# RubyOps.EndPark, TakeParkedLocals; ScopeBuilder.MakeLocalsStorage, MaterializeLocals). The tuple is cleared when the scope
# is parked and taken by the next call's entry, or with the scope by a light frame that keeps its locals in CLR variables.
# Run it under CRuby and under ./ir.sh with the switch on and off and with parked scopes and the tiers off, and diff:
#
#   ruby Util/parked-tuple-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/parked-tuple-matrix.rb > /tmp/on.txt
#   IR_PARKED_LOCALS=0 ./ir.sh Util/parked-tuple-matrix.rb > /tmp/off.txt
#   IR_PARKED_SCOPES=0 ./ir.sh Util/parked-tuple-matrix.rb > /tmp/noparked.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/parked-tuple-matrix.rb > /tmp/notiers.txt
#
# Every method runs thousands of times, parking its scope, around the case it probes. Their block literals are given to
# core iterators that do not keep them (each, map, times), which is what lets them park.

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

class P
  def initialize; @a = [1, 2, 3]; @kept = []; end
  attr_reader :kept

  # a local assigned on some calls only: nil on the others
  def maybe(set)
    x = 41 if set
    @a.each { |e| x = x.to_i + e if set }
    [x, defined?(x), local_variables.sort]
  end

  # a binding taken on some calls: it keeps its own locals
  def bind(v)
    w = v * 2
    @a.each { |e| w += e }
    (v % 1000 == 7) ? binding : w
  end

  # a Proc of the method's own scope kept on some calls (`proc' keeps the block it is given)
  def keep(v)
    u = v + 1
    @a.map { |e| e + u }
    @kept << proc { [u, v] } if v % 1000 == 3
    u
  end

  # an exception leaving after its locals are set
  def raising(v)
    q = v
    @a.each { |e| q += e }
    raise ArgumentError, "boom #{q}" if v % 700 == 5
    q
  end

  # many locals: more than the tuple that is kept may hold
  def many(v)
    a1 = v; a2 = a1 + 1; a3 = a2 + 1; a4 = a3 + 1; a5 = a4 + 1; a6 = a5 + 1; a7 = a6 + 1; a8 = a7 + 1; a9 = a8 + 1
    b1 = a9 + 1; b2 = b1 + 1; b3 = b2 + 1; b4 = b3 + 1; b5 = b4 + 1; b6 = b5 + 1; b7 = b6 + 1; b8 = b7 + 1; b9 = b8 + 1
    c = nil
    @a.each { |e| c = b9 + e } if v.odd?
    [a1, b9, c]
  end

  # recursion: each level has its own locals
  def rec(d, acc)
    here = d * 10
    @a.each { |e| here += e }
    inner = (d > 0) ? rec(d - 1, acc) : acc
    [here, inner]
  end

  # many calls a light frame makes: its locals in CLR variables until the scope is made
  def light(v, make)
    k = v
    j = nil
    @a.each { |e| j = (j || 0) + e + k } if make
    [k, j]
  end
end

t('a local set on some calls is nil on the others') {
  o = P.new
  r = []
  N.times { |n| v = o.maybe(n.even?); r << v if n > N - 4 }
  r
}
t('local_variables and defined? after reuse') { o = P.new; N.times { |n| o.maybe(n % 3 == 0) }; [o.maybe(false), o.maybe(true)] }
t('a binding taken in the middle keeps its own locals') {
  o = P.new
  bs = []
  N.times { |n| r = o.bind(n); bs << r if r.is_a?(Binding) }
  bs.map { |b| [b.local_variable_get(:v), b.local_variable_get(:w), b.local_variables.sort] }
}
t('a binding written after later calls') {
  o = P.new
  b = nil
  N.times { |n| r = o.bind(n); b = r if r.is_a?(Binding) && b.nil? }
  b.local_variable_set(:w, :changed)
  N.times { |n| o.bind(n + 1) }
  [b.local_variable_get(:w), b.local_variable_get(:v)]
}
t('a Proc of the scope kept in the middle') {
  o = P.new
  N.times { |n| o.keep(n) }
  o.kept.map(&:call)
}
t('an exception after the locals are set') {
  o = P.new
  errs = []
  s = 0
  N.times do |n|
    begin
      s += o.raising(n)
    rescue ArgumentError => e
      errs << e.message
    end
  end
  [s, errs]
}
t('more locals than a kept tuple holds') { o = P.new; r = []; N.times { |n| r << o.many(n) if n > N - 3 }; r }
t('recursion') { o = P.new; r = nil; 500.times { |n| r = o.rec(4, n) }; r }
t('a light frame that makes its scope on some calls') {
  o = P.new
  r = []
  N.times { |n| v = o.light(n, n % 3 != 0); r << v if n > N - 4 }
  r
}
t('threads calling one method') {
  o = P.new
  ths = Array.new(4) { |ti| Thread.new { s = 0; nils = 0; 2000.times { |n| x, = o.maybe((n + ti).even?); x.nil? ? nils += 1 : s += x }; [s, nils] } }
  ths.map(&:value)
}
t('threads: bindings taken while others call') {
  o = P.new
  ths = Array.new(4) { |ti| Thread.new { bs = []; 3000.times { |n| r = o.bind(n * 4 + ti); bs << r if r.is_a?(Binding) }; bs.map { |b| [b.local_variable_get(:v), b.local_variable_get(:w)] } } }
  ths.map(&:value).flatten(1).sort
}

class Fib
  def initialize; @a = [1, 2]; end
  def gen(y, v)
    loc = v
    @a.each { |e| y << (loc + e) }
    loc
  end
end

t('a call suspended in a fiber while the method runs again') {
  f = Fib.new
  N.times { |n| f.gen([], n) }
  e = Enumerator.new { |y| f.gen(y, 100) }
  first = e.next
  mid = Array.new(50) { |n| f.gen([], n) }.last
  second = e.next
  [first, mid, second, (e.next rescue :done)]
}
t('eval in the method sees its own locals') {
  k = Class.new { def m(v); z = v; [1].each { |e| z += e }; eval('z'); end }.new
  r = nil
  N.times { |n| r = k.m(n) }
  r
}
t('TracePoint :return binding') {
  o = P.new
  N.times { |n| o.maybe(n.odd?) }
  seen = []
  tp = TracePoint.new(:return) { |tpp| seen << tpp.binding.local_variable_get(:x) if tpp.method_id == :maybe }
  tp.enable { o.maybe(true); o.maybe(false) }
  [seen, o.maybe(false)]
}
