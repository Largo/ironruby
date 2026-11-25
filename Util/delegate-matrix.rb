# Differential matrix for delegation (delegate.rb) and what its hot path leans on: SimpleDelegator and DelegateClass
# over public, private and protected target methods, respond_to?, a target with its own method_missing, blocks,
# keywords, a target that raises (the backtrace lines), __setobj__ and a method redefined on the target in the middle
# of a hot loop; and the three things a delegated call costs that were made cheaper: `defined?(@x)' in a method body
# (SimpleDelegator#__getobj__), Module#const_get answered again for the same constant (Delegator.const_missing, for
# every `Object' its methods name) after the constant is reassigned, removed, deprecated or autoloaded, and
# Kernel#respond_to? called from a method that runs without its scope (refinements included). Run under CRuby and
# under ./ir.sh - with the JIT on, off, and without light frames - and diff:
#
#   ruby Util/delegate-matrix.rb > /tmp/mri.txt 2>&1; echo $?
#   ./ir.sh Util/delegate-matrix.rb > /tmp/jit.txt 2>&1
#   ./ir.sh -X:NoJIT -X:NoOSR Util/delegate-matrix.rb > /tmp/nojit.txt 2>&1
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/delegate-matrix.rb > /tmp/nolight.txt 2>&1
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt && diff /tmp/mri.txt /tmp/nolight.txt
#
# All must exit 0, end with the row `end', and be identical.

require 'delegate'
require 'stringio'
$stdout.sync = true

# Calls the block n times (a hot site), and prints the answers it gave as runs: [answer, how many].
def runs(n = 200)
  out = []
  n.times do |i|
    v = yield(i)
    if out.last && out.last[0] == v then out.last[1] += 1 else out << [v, 1] end
  end
  out
end

def row(label, value)
  puts "#{label}: #{value.inspect}"
end

def try
  yield
rescue Exception => e
  "#{e.class}: #{e.message}"
end

def quiet
  old, $stderr = $stderr, StringIO.new
  [yield, $stderr.string.scan(/warning: .*/)]
ensure
  $stderr = old
end

class Target
  def pub(a, b = 2, *r, k: 0, **o, &blk) = [a, b, r, k, o, blk ? blk.call(a) : nil]
  def each_two = (yield 1; yield 2; :done)
  def boom = raise(ArgumentError, "boom")
  def ==(o) = o.is_a?(Target)
  protected def prot = :prot
  private def priv = :priv
  def call_prot(o) = o.prot
  def method_missing(m, *a) = m.to_s.start_with?("ghost_") ? [:ghost, m, a] : super
  def respond_to_missing?(m, p = false) = m.to_s.start_with?("ghost_") || super
end

class Loud < SimpleDelegator
  def loud_size = __getobj__.size
end
TargetDC = DelegateClass(Target)
class Wrapped < TargetDC
  def initialize(t) = super(t)
  def extra = :extra
end

[["simple", SimpleDelegator.new(Target.new)], ["dc", Wrapped.new(Target.new)]].each do |name, d|
  row "#{name} pub", d.pub(1)
  row "#{name} pub all", d.pub(1, 3, 4, 5, k: 6, z: 7) { |x| x * 10 }
  row "#{name} each_two", (acc = []; [d.each_two { |x| acc << x }, acc])
  # IronRuby says "undefined method" where CRuby says "protected method ... called" for DelegateClass (not checked here)
  row "#{name} prot", try { d.prot }.sub(/: .*/, "")
  row "#{name} priv", try { d.priv }
  row "#{name} ghost", d.ghost_x(1, 2)
  row "#{name} missing", try { d.nothing_here }
  row "#{name} respond pub/prot/priv/ghost/none", [:pub, :prot, :priv, :ghost_y, :none].map { |m| d.respond_to?(m) }
  # the warning's location (uplevel: 3) is IronRuby's delegate.rb line, not the caller's: only its text is compared
  row "#{name} respond private", quiet { [:priv, :prot].map { |m| d.respond_to?(m, true) } }
  row "#{name} method", try { d.method(:pub).call(5) }
  row "#{name} ghost method", try { d.method(:ghost_q).call(1) }
  row "#{name} ==", [d == d, d == Target.new, d.eql?(d), d != d]
  row "#{name} !", !d
  row "#{name} kernel", [d.frozen?, d.nil?, d.is_a?(Target), d.instance_of?(Target), d.class.ancestors.include?(Delegator)]
  err = (d.boom rescue $!)
  row "#{name} boom", [err.class, err.message]
  # IronRuby's backtrace has a frame for BasicObject#__send__ that CRuby's has not: left out
  row "#{name} boom backtrace", err.backtrace.reject { |l| l.include?("__send__") }.first(3).map { |l| l.sub(/^.*\//, "").sub(/:\d+:/, ":N:") }
  row "#{name} hot pub", runs { |i| d.pub(i % 3) }
  row "#{name} hot respond", runs { |i| d.respond_to?(i.even? ? :pub : :priv) }
end

s = Loud.new("abc")
row "loud", [s.loud_size, s.size, s.upcase, s.index("b"), s.start_with?("a"), s.respond_to?(:size), s.respond_to?(:upcase_size)]
row "loud hot", runs { |i| s.size + s.loud_size + s.index("c") + (s.start_with?("ab") ? 1 : 0) }
row "loud setobj mid-loop", runs { |i| s.__setobj__(i < 100 ? "abc" : "abcdef") if i % 50 == 0; s.size }
s.__setobj__("abc")
row "loud redefine mid-loop", runs { |i|
  if i == 100
    class << s.__getobj__; def size = 42; end
  end
  s.size
}
row "unset", try { SimpleDelegator.allocate.size }
row "unset getobj block", SimpleDelegator.allocate.__getobj__ { :none }
row "freeze", (f = SimpleDelegator.new(+"x").freeze; [f.frozen?, try { f.__setobj__("y") }])
row "marshal", Marshal.load(Marshal.dump(SimpleDelegator.new([1, 2]))).then { |m| [m.class, m.size, m.__getobj__] }
row "methods", (SimpleDelegator.new(Target.new).methods & [:pub, :prot, :priv, :__getobj__]).sort
row "public_methods", (SimpleDelegator.new(Target.new).public_methods & [:pub, :prot, :priv]).sort
row "kernel via mm", try { SimpleDelegator.new(BasicObject.new).then { |b| b.respond_to?(:inspect) } }

# defined?(@x) in method bodies
class Ivars
  def a = defined?(@a)
  def g
    unless defined?(@a)
      return yield if block_given?
      raise ArgumentError, "unset"
    end
    @a
  end
  def set(v) = @a = v
  def unset = remove_instance_variable(:@a)
end
iv = Ivars.new
row "ivar defined hot", runs { |i|
  iv.set(i) if i == 50
  iv.unset if i == 150
  [iv.a, iv.g { :blk }, try { iv.g }]
}
frozen_iv = Ivars.new.freeze
row "ivar defined frozen", [frozen_iv.a, frozen_iv.g { 1 }]

# const_get answered again
module Box; V = 1; end
def cget(m, n) = m.const_get(n)
row "const_get reassign", runs { |i|
  if i == 100
    Box.send(:remove_const, :V); Box.const_set(:V, 2)
  end
  cget(Box, :V)
}
row "const_get remove", runs { |i|
  Box.send(:remove_const, :V) if i == 100
  try { cget(Box, :V) }
}
module Box; V = 3; end
module Parent; PV = :parent; end
module Child; end
row "const_get include", runs { |i|
  Child.include(Parent) if i == 100
  try { cget(Child, :PV) }
}
module Child; PV = :child; end
row "const_get shadow", runs { |i| cget(Child, :PV) }
row "const_get object", runs { |i| [cget(Box, :String), try { Box.const_get(:String, false) }] }
module Dep; OLD = :old; end
old_stderr = $stderr
row "const_get deprecate", runs(4) { |i|
  if i == 2
    Dep.deprecate_constant(:OLD)
  end
  require 'stringio'
  $stderr = StringIO.new
  begin
    Warning[:deprecated] = true
    v = cget(Dep, :OLD)
    [v, $stderr.string.sub(/^.*?: /, "")]
  ensure
    $stderr = old_stderr
  end
}
File.write(auto = File.join(Dir.tmpdir, "ir_delegate_matrix_auto_#{$$}.rb"), "module Box; AUTO = :loaded; end\n") if require('tmpdir')
Box.autoload(:AUTO, auto)
row "const_get autoload", runs(5) { |i| cget(Box, :AUTO) }
File.delete(auto)
row "const_get threads", 4.times.map { |t|
  Thread.new { 2000.times.map { |i| cget(Box, i.even? ? :V : :String) }.uniq }
}.map(&:value)
row "const_missing", runs { |i|
  c = Class.new(BasicObject) { def self.const_missing(n) = ::Object.const_get(n); def x = Object; def y = Comparable }
  o = c.new
  [o.x, o.y]
}

# respond_to? from a method without a scope
module Shout
  refine String do
    def shout = upcase + "!"
  end
end
class PlainResp
  def r(o, m) = o.respond_to?(m)
  def rp(o, m) = o.respond_to?(m, true)
end
module UsesRefinement
  using Shout
  class RefResp
    def r(o, m) = o.respond_to?(m)
  end
  row "refined call", "x".shout
end
pr = PlainResp.new; rr = UsesRefinement::RefResp.new
row "respond plain", runs { |i| [pr.r("x", :size), pr.r("x", :shout), pr.rp(Target.new, :priv), pr.r(Target.new, :prot), pr.r(Target.new, :ghost_a)] }
row "respond refined", runs { |i| [rr.r("x", :size), rr.r("x", :shout)] }

klass = Class.new
refinement = Module.new { refine(klass) { def foo; end } }
Module.new do
  using refinement
  row "respond after using in a block", runs(3) { klass.new.respond_to?(:foo) }
end
Module.new do
  send(:using, refinement)
  row "respond after send(:using) in a block", runs(3) { klass.new.respond_to?(:foo) }
end
row "respond outside the using", klass.new.respond_to?(:foo)

row "end", nil
