# Differential matrix for methods the method JIT compiles for a call without a block (JitCode.AssumesNoBlock): a
# block parameter only passed on (`def w(a, &k) = @t.m(a, &k)', JitStub.BlockParameterOnlyPassed), `&nil', and
# `block_given?' (JitCompiler.EmitBlockGiven). Run under CRuby and under ./ir.sh in every mode, and diff:
#
#   ruby Util/jit-blockparam-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-blockparam-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-blockparam-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Which ones are specialized:
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-blockparam-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]' | grep BP
# The traps: a call with a block after the body is hot (the entry's block test), a method the profile saw called with
# a block, `break' through the passed block, `&obj' with to_proc (called once, by the outer call), a Symbol and a lambda
# passed on, block_given? in the callee, super with the implicit block, the parameter read, assigned or reached through
# a binding, a deopt with a block in flight, block_given? redefined, refined or in a loop, `&nil' with a block given.

def t(label)
  print label.ljust(52), ' => '
  begin
    v = yield
    puts v.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message[0, 60]}"
  end
end

HOT = 300

def warm(n = HOT)
  i = 0
  r = nil
  while i < n
    r = yield i
    i += 1
  end
  r
end

class Callee
  def two(a, b) = a + b
  def two_y(a, b) = block_given? ? yield(a, b) : a + b
  def given? = block_given?
  def kind(&b) = b.nil? ? :none : (b.lambda? ? :lambda : :proc)
  def leaf(a) = block_given? ? a * -1 : a
end

class BPBase
  def sup(a, &k) = block_given? ? yield(a) : a * 10
end

class BP < BPBase
  def initialize; @t = Callee.new; end
  def fwd(a, b, &k) = @t.two(a, b, &k)
  def fwd_y(a, b, &k) = @t.two_y(a, b, &k)
  def fwd_given(&k) = @t.given?(&k)
  def fwd_kind(&k) = @t.kind(&k)
  def fwd2(a, b, &k) = fwd_y(a, b, &k)
  def anon(a, b, &) = @t.two_y(a, b, &)
  def nil_pass(a, b) = @t.two_y(a, b, &nil)
  def given(a) = block_given? ? a * -1 : a + 1
  def given_self(a) = block_given? ? a : leaf2(a)
  def leaf2(a) = a * 2
  def inl(a, &k) = leaf(a, &k)
  def leaf(a) = block_given? ? a * -1 : a
  def read_k(a, &k) = k ? -a : a
  def assign_k(a, &k)
    k = proc { |x, y| x * y } if a > 1000
    @t.two_y(a, 1, &k)
  end
  def sup(a, &k) = super
  def sup_explicit(a, &k) = @t.two_y(a, 1, &k)
  def bind_k(a, &k) = binding.local_variable_get(:k) ? -a : a
  def deopt(a, b, &k) = @t.two_y(a, b, &k) + 1
  def missing(a, &k) = @t.nosuch(a, &k)
end

o = BP.new
t("fwd, no block")                     { warm { |i| o.fwd(i, 1) } }
t("fwd, then a block (two ignores it)") { o.fwd(1, 2) { :x } }
t("fwd_y, no block")                   { warm { |i| o.fwd_y(i, 1) } }
t("fwd_y, then a block")               { o.fwd_y(3, 4) { |a, b| a * b } }
t("fwd_y, block then none")            { [o.fwd_y(3, 4) { |a, b| a - b }, o.fwd_y(3, 4)] }
t("fwd_y: break through the block")    { o.fwd_y(1, 2) { break :broke } }
t("fwd_y: break in a loop")            { r = []; [1, 2, 3].each { |i| r << o.fwd_y(i, 0) { |a, _| break a * 100 if a == 2; a } }; r }
t("fwd_y: next in the block")          { o.fwd_y(5, 6) { next :nexted } }
t("fwd_y: return from a lambda block") { l = ->(a, b) { return a + b + 100 }; o.fwd_y(1, 2, &l) }
t("fwd_y: &obj to_proc, called once")  { n = 0; ob = Object.new; ob.define_singleton_method(:to_proc) { n += 1; proc { |a, b| a * b * 10 } }; [o.fwd_y(2, 3, &ob), n] }
t("fwd_y: &obj to_proc returns nil")   { ob = Object.new; def ob.to_proc = nil; o.fwd_y(2, 3, &ob) }
t("fwd_y: &:sym")                      { o.fwd_y(2, 3, &:+) }
t("fwd_y: &nil")                       { o.fwd_y(2, 3, &nil) }
t("fwd_y: via send with a block")      { o.send(:fwd_y, 2, 3) { |a, b| [a, b] } }
t("fwd_y: via Method#call")            { m = o.method(:fwd_y); [m.call(2, 3), m.call(2, 3) { |a, b| a - b }] }
t("fwd_y: via Method#to_proc")         { m = o.method(:fwd_y).to_proc; m.call(4, 5) }
t("fwd_given, no block")               { warm { o.fwd_given } }
t("fwd_given, a block")                { o.fwd_given { } }
t("fwd_kind")                          { warm { o.fwd_kind }; [o.fwd_kind, o.fwd_kind { }, o.fwd_kind(&-> { })] }
t("fwd2, two layers")                  { warm { |i| o.fwd2(i, 1) }; [o.fwd2(1, 1), o.fwd2(2, 3) { |a, b| a * b }] }
t("anon &")                            { warm { |i| o.anon(i, 1) }; [o.anon(1, 1), o.anon(2, 3) { |a, b| a * b }] }
t("nil_pass")                          { warm { |i| o.nil_pass(i, 1) } }
t("nil_pass with a block")             { o.nil_pass(2, 3) { :never } }
t("given")                             { warm { |i| o.given(i) } }
t("given, then a block")               { [o.given(5) { }, o.given(5)] }
t("given_self")                        { warm { |i| o.given_self(i) }; [o.given_self(3) { }, o.given_self(3)] }
t("inl: inlined callee's block_given?") { warm { |i| o.inl(i) }; [o.inl(4), o.inl(4) { }] }
t("read_k")                            { warm { |i| o.read_k(i) }; [o.read_k(4), o.read_k(4) { }] }
t("assign_k")                          { warm { |i| o.assign_k(i) }; [o.assign_k(5), o.assign_k(2000), o.assign_k(5) { |a, b| a - b }] }
t("super with the implicit block")     { warm { |i| o.sup(i) }; [o.sup(3), o.sup(3) { |a| a + 1 }] }
t("sup_explicit")                      { warm { |i| o.sup_explicit(i) }; [o.sup_explicit(3), o.sup_explicit(3) { |a, b| a * b }] }
t("bind_k")                            { warm { |i| o.bind_k(i) }; [o.bind_k(3), o.bind_k(3) { }] }
t("deopt: no block")                   { warm { |i| o.deopt(i, 1) }; o.deopt(2**62, 2**62) }
t("deopt: a block in flight")          { o.deopt(2**62, 2**62) { |a, b| (a + b).to_s.size } }
t("deopt: Float")                      { [o.deopt(1.5, 1), o.deopt(1.5, 1) { |a, b| a * b }] }
t("missing")                           { o.missing(1) }
t("missing with a block")              { o.missing(1) { } }

# called with a block from the start: the profile saw one
p2 = BP.new
t("always a block")                    { warm { |i| p2.fwd_y(i, 1) { |a, b| a - b } } }
t("always a block, then none")         { [p2.fwd_y(7, 1), p2.fwd_y(7, 1) { |a, b| a * b }] }
t("given, always a block")             { warm { |i| p2.given(i) { } } }
t("given, always a block, then none")  { p2.given(7) }

# a block sometimes
p3 = BP.new
t("a block every third call")          { s = 0; warm { |i| s += (i % 3 == 0) ? p3.fwd_y(i, 1) { |a, b| a * b } : p3.fwd_y(i, 1) }; s }

# block_given? redefined in the class after the body is hot
class GivenRedef
  def g(a) = block_given? ? :yes : a
end
gr = GivenRedef.new
t("block_given? before")               { warm { |i| gr.g(i) } }
class GivenRedef
  def block_given? = true
end
t("block_given? redefined")            { [gr.g(1), gr.g(1) { }] }

# Kernel#block_given? refined where the method is written
module GivenRefinement
  refine Object do
    def block_given? = :refined
  end
end
class GivenRefined
  using GivenRefinement
  def g(a) = block_given? ? a : -a
end
gref = GivenRefined.new
t("block_given? refined")              { warm { |i| gref.g(i) } }

# block_given? in a while loop (an outlined loop runs in the generic frame)
class GivenLoop
  def g(n)
    i = 0
    s = 0
    while i < n
      s += block_given? ? 2 : 1
      i += 1
    end
    s
  end
end
gl = GivenLoop.new
t("block_given? in a loop")            { warm(50) { gl.g(2000) } }
t("block_given? in a loop, a block")   { gl.g(2000) { } }

# a singleton method on the receiver, and another class
t("singleton fwd_y")                   { s = BP.new; def s.fwd_y(a, b, &k) = :single; [s.fwd_y(1, 2), o.fwd_y(1, 2)] }
class BP2 < BP; def two_y(*) = :sub; end
t("subclass")                          { b2 = BP2.new; warm { |i| b2.fwd_y(i, 1) }; [b2.fwd_y(1, 1), b2.fwd_y(1, 1) { |a, b| a * b }] }

# the callee redefined after the forwarding body is hot
class Callee
  def two_y(a, b) = block_given? ? [:redef, yield(a, b)] : [:redef, a]
end
t("callee redefined")                  { [o.fwd_y(1, 2), o.fwd_y(1, 2) { |a, b| a + b }] }
