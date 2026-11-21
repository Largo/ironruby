# Differential matrix for Binding#local_variable_get, #local_variable_set, #local_variable_defined?
# and #local_variables (the prelude's `class Binding' in Src/StdLib/ironruby/ruby4.rb and
# Src/Libraries/Builtins/BindingOps.cs): where a new variable lands, which variables a binding sees,
# the order of #local_variables, the errors for wrong names, isolated Procs, threads and ERB.
# Run under CRuby and under ./ir.sh - with the JIT on and off and without light frames - and diff:
#
#   ruby Util/binding-lvar-matrix.rb > /tmp/mri.txt; echo $?
#   ./ir.sh Util/binding-lvar-matrix.rb > /tmp/ir.txt; echo $?
#   ./ir.sh -X:NoJIT -X:NoOSR Util/binding-lvar-matrix.rb > /tmp/nojit.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/binding-lvar-matrix.rb > /tmp/nolf.txt
#   diff /tmp/mri.txt /tmp/ir.txt && diff /tmp/mri.txt /tmp/nojit.txt && diff /tmp/mri.txt /tmp/nolf.txt
#
# All must exit 0, end with the row `end', and be identical. Also run with -w: neither engine warns.

$stdout.sync = true
require 'erb'

def row(label, value)
  puts "#{label}: #{value.inspect}"
end

# What a call answered or raised: the exception's class and message, with the binding's address
# (in "for #<Binding:0x...>") taken out, since it differs between runs and engines.
def try
  yield
rescue Exception => e
  [e.class, e.message.gsub(/0x\h+/, '0x')]
end

# --- values of every kind, round trip -----------------------------------------------------------
def values
  b = binding
  o = Object.new
  def o.inspect; "obj"; end
  [nil, false, true, 42, 1.5, 2**70, :sym, "str", [1], { a: 1 }, o, 1..2].each_with_index do |v, i|
    b.local_variable_set(:"v#{i}", v)
    row "value #{i}", [b.local_variable_get(:"v#{i}").equal?(v) || b.local_variable_get(:"v#{i}") == v,
                       b.local_variable_defined?(:"v#{i}"), b.local_variables.size]
  end
  s = +"abc"
  b.local_variable_set(:s, s)
  b.eval("s << 'd'")
  row "mutated string", [s, b.local_variable_get(:s), b.local_variable_get(:s).equal?(s)]
  b.local_variable_get(:s) << "e"
  row "mutated through get", s
  row "set returns", [b.local_variable_set(:r, 5), try { b.local_variable_set(:r, nil) }]
end
values

# --- names --------------------------------------------------------------------------------------
class ToStrName
  def initialize(s); @s = s; end
  def to_str; @s; end
end

def names
  a = 1
  b = binding
  row "symbol name", b.local_variable_get(:a)
  row "string name", b.local_variable_get("a")
  row "to_str name", b.local_variable_get(ToStrName.new("a"))
  row "to_str set", [b.local_variable_set(ToStrName.new("ts"), 3), b.local_variable_get(:ts)]
  ["@a", "$a", "A", "a b", "", "1a", :_1, :_9, :it, "a?", "a!", "a=", "@@a", "$~", "self", "nil",
   "__FILE__", "défini", :"日本", 1, nil, 1.5, Object.new].each do |n|
    label = n.is_a?(Object) && !n.is_a?(String) && !n.is_a?(Symbol) && !n.is_a?(Numeric) && !n.nil? ? "Object" : n.inspect
    row "name #{label} get", try { b.local_variable_get(n) }
    row "name #{label} defined?", try { b.local_variable_defined?(n) }
    row "name #{label} set", try { b.local_variable_set(n, 1) }
  end
  row "after names", b.local_variables
  row "non-ascii get", try { b.local_variable_get(:"défini") }
  row "non-ascii get2", try { b.local_variable_get(:"日本") }
end
names

# --- defined? -----------------------------------------------------------------------------------
def a_method; end
CONST_X = 1

def defs
  x = nil
  b = binding
  row "defined nil", [b.local_variable_defined?(:x), b.local_variable_get(:x)]
  row "defined later", [b.local_variable_defined?(:later), try { b.local_variable_get(:later) }]
  row "defined self", try { b.local_variable_defined?(:self) }
  row "defined method", try { b.local_variable_defined?(:a_method) }
  row "defined const", try { b.local_variable_defined?(:CONST_X) }
  row "defined missing", [b.local_variable_defined?(:nope), try { b.local_variable_get(:nope) }]
  row "locals with later", b.local_variables
  later = 1
  row "later now", [b.local_variable_get(:later), later]
end
defs

def kw(if: 1, alias: 2, class: 3)
  binding
end

def keywords
  b = kw(if: 10)
  row "kw locals", b.local_variables
  row "kw get", [b.local_variable_get(:if), b.local_variable_get(:alias), b.local_variable_get("class")]
  row "kw defined", [b.local_variable_defined?(:if), b.local_variable_defined?(:while)]
  row "kw set", try { b.local_variable_set(:if, 11) }
  row "kw get after set", try { b.local_variable_get(:if) }
  row "kw new", try { b.local_variable_set(:while, 1) }
  row "kw new get", try { b.local_variable_get(:while) }
  row "kw locals after", b.local_variables
end
keywords

# --- where a new variable lands -----------------------------------------------------------------
def lands
  a = 1
  b = binding
  row "new set", b.local_variable_set(:n, 2)
  row "new in b", [b.local_variable_defined?(:n), b.local_variable_get(:n), b.local_variables, b.local_variables.size]
  row "new via eval", b.eval("n")
  b2 = binding
  row "new in later binding", [b2.local_variable_defined?(:n), b2.local_variables]
  row "new in method eval", try { eval("n") }
  b.local_variable_set(:m, 3)
  b.eval("q = 4; r = 5")
  b.local_variable_set(:z, 6)
  row "order", b.local_variables
  row "order size", b.local_variables.size
  row "a via b", [b.local_variable_set(:a, 10), a]
  a = 11
  row "a after method", b.local_variable_get(:a)
  b2.local_variable_set(:only2, 1)
  row "b sees b2's", [b.local_variable_defined?(:only2), b2.local_variable_defined?(:only2)]
  row "method locals", binding.local_variables
end
lands

def own_local_then_read
  b = binding
  b.local_variable_set(:v, 1)
  v = 2 if false
  row "method sees set of own local", [v, b.local_variable_get(:v)]
  b.local_variable_set(:v, 3)
  row "method sees set of own local 2", v
end
own_local_then_read

# --- dup and clone ------------------------------------------------------------------------------
def dups
  a = 1
  b = binding
  b.local_variable_set(:pre, 1)
  d = b.dup
  c = b.clone
  d.local_variable_set(:dd, 1)
  d.local_variable_set(:a, 9)
  d.local_variable_set(:pre, 2)
  c.local_variable_set(:cc, 1)
  b.local_variable_set(:bb, 1)
  row "dup b", [b.local_variables, b.local_variable_get(:pre), a]
  row "dup d", [d.local_variables, d.local_variable_get(:pre)]
  row "dup c", [c.local_variables, c.local_variable_get(:pre)]
  row "dup cross", [b.local_variable_defined?(:dd), d.local_variable_defined?(:bb), d.local_variable_defined?(:cc)]
end
dups

def dup_chains
  a = 1
  b = binding
  d1 = b.dup
  d2 = b.dup
  b.local_variable_set(:after, 1)
  d1.local_variable_set(:one, 1)
  d3 = d1.dup
  d1.local_variable_set(:later1, 1)
  d3.local_variable_set(:a, 2)
  row "dup chain", [b.local_variables, d1.local_variables, d2.local_variables, d3.local_variables, a]
  b.local_variable_set(:before_dup, 1)
  d4 = b.dup
  b.local_variable_set(:after_dup, 1)
  d4.local_variable_set(:before_dup, 2)
  row "dup after set", [d4.local_variables, b.local_variables, b.local_variable_get(:before_dup)]
end
dup_chains

# --- blocks and procs ---------------------------------------------------------------------------
def blocks
  a = 1
  [1].each do |x|
    c = 2
    bb = binding
    row "block locals", bb.local_variables
    bb.local_variable_set(:a, 7)
    bb.local_variable_set(:nn, 1)
    bb.local_variable_set(:x, 5)
    row "block after set", [a, x, bb.local_variables, binding.local_variables]
    [2].each do |a|
      inner = binding
      row "shadow locals", inner.local_variables
      inner.local_variable_set(:a, 99)
      row "shadow get", [a, inner.local_variable_get(:a)]
    end
  end
  row "method a", a
  [1].each { a = 3 }
  pr = proc { y = 1; proc { z = 2; binding } }
  inner = pr.call.call
  row "nested procs", inner.local_variables
  inner.local_variable_set(:a, 4)
  inner.local_variable_set(:w, 5)
  row "nested procs set", [a, inner.local_variables, inner.local_variable_get(:z), inner.local_variable_get(:y)]
  q = 1
  pb = proc { q }.binding
  row "proc binding", [pb.local_variables, pb.local_variable_get(:q)]
  pb.local_variable_set(:q, 2)
  pb.local_variable_set(:fresh, 3)
  row "proc binding set", [q, pb.local_variable_get(:fresh), proc { q }.binding.local_variable_defined?(:fresh)]
  it_b = [5].map { it; binding }.first
  row "it binding", [it_b.local_variables.include?(:it), try { it_b.local_variable_defined?(:it) }, try { it_b.local_variable_get(:it) }]
  n_b = [5].map { _1; binding }.first
  row "_1 binding", [n_b.local_variables.include?(:_1), try { n_b.local_variable_get(:_1) }]
end
blocks

class Definer
  define_method(:dm) do |p = 1|
    local = 2
    b = binding
    b.local_variable_set(:local, 3)
    b.local_variable_set(:fresh, 4)
    [local, b.local_variables, b.local_variable_get(:p), b.local_variable_get(:fresh)]
  end
end
row "define_method", Definer.new.dm

def held
  a = 1
  binding
end
hb = held
hb.local_variable_set(:a, 2)
hb.local_variable_set(:after, 3)
row "held", [hb.local_variables, hb.local_variable_get(:a), hb.local_variable_get(:after)]

def frame_boundaries
  outer_m = 1
  r = []
  klass = Class.new do
    in_class = 1
    r << binding.local_variables
    define_method(:x) { binding.local_variables }
  end
  r << klass.new.x
  mod = Module.new
  r << mod.module_eval { binding.local_variables }
  r << mod.module_eval("binding.local_variables")
  o = Object.new
  r << o.instance_eval { binding.local_variables }
  r << o.instance_eval("binding.local_variables")
  ib = o.instance_eval { binding }
  ib.local_variable_set(:outer_m, 5)
  ib.local_variable_set(:ie_new, 6)
  r << outer_m << ib.local_variable_get(:ie_new)
  sb = o.instance_eval("ses = 1; binding")
  sb.local_variable_set(:se_new, 2)
  r << sb.local_variables << sb.local_variable_get(:ses)
  cb = mod.class_eval("ces = 1; binding")
  cb.local_variable_set(:outer_m, 8)
  r << cb.local_variables << outer_m
  r << defined?(ses).inspect << defined?(se_new).inspect
  row "frame boundaries", r
end
frame_boundaries

# --- top level ----------------------------------------------------------------------------------
top_a = 1
tb = binding
tb.local_variable_set(:top_new, 2)
row "top binding", [tb.local_variables.include?(:top_a), tb.local_variables.include?(:top_new), tb.local_variable_get(:top_a)]
row "top later binding", binding.local_variable_defined?(:top_new)
tb.local_variable_set(:top_a, 3)
row "top set existing", top_a
row "TOPLEVEL_BINDING", [TOPLEVEL_BINDING.local_variable_defined?(:top_a), TOPLEVEL_BINDING.local_variable_defined?(:tb)]
TOPLEVEL_BINDING.local_variable_set(:tl_new, 1)
row "TOPLEVEL_BINDING new", [TOPLEVEL_BINDING.local_variable_get(:tl_new), TOPLEVEL_BINDING.local_variable_defined?(:tl_new),
                             binding.local_variable_defined?(:tl_new)]
tdup = TOPLEVEL_BINDING.dup
tdup.local_variable_set(:tdup_new, 1)
row "TOPLEVEL_BINDING dup", [TOPLEVEL_BINDING.local_variable_defined?(:tdup_new), TOPLEVEL_BINDING.dup.local_variable_defined?(:tdup_new),
                             tdup.local_variable_defined?(:tl_new), TOPLEVEL_BINDING.local_variable_defined?(:tl_new)]

# --- isolated procs -----------------------------------------------------------------------------
def isolated
  outer = 1
  pr = Ractor.make_shareable(nil.instance_eval { proc { inside = 2; binding } }) rescue (return row("isolated", try { raise $! }))
  ib = pr.call
  row "isolated locals", ib.local_variables
  row "isolated defined outer", try { ib.local_variable_defined?(:outer) }
  row "isolated get outer", try { ib.local_variable_get(:outer) }
  row "isolated set outer", try { ib.local_variable_set(:outer, 5) }
  row "isolated outer after", outer
  row "isolated get inside", try { ib.local_variable_get(:inside) }
  row "isolated set new", try { ib.local_variable_set(:nw, 1) }
  row "isolated get new", try { ib.local_variable_get(:nw) }
  row "isolated eval outer", try { ib.eval("outer") }
  row "isolated locals after", ib.local_variables
end
isolated

# --- threads ------------------------------------------------------------------------------------
def threads
  b = binding
  ts = 2.times.map do |t|
    Thread.new do
      5000.times { |i| b.local_variable_set(:"t#{t}_#{i}", i) }
    end
  end
  ts.each(&:join)
  ok = 2.times.all? { |t| 5000.times.all? { |i| b.local_variable_get(:"t#{t}_#{i}") == i } }
  row "threads", [ok, b.local_variables.size, Thread.current[:__ir_binding_value__]]
end
threads

# --- a body of more than 40 calls (MethodDefinition.MaxRegisterLocalsCalls) -------------------------
# Its locals live in their tuple, not in CLR variables: the scope made on demand at any of its calls - the first,
# a later one, one in a loop, one after a block or eval wrote a local - sees each local's value at that point, and
# the body sees what was written through it.
def blv_big(x)
  z = x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs
  a = 1
  b = 2
  r1 = binding.local_variable_get(:a)
  a = 10
  r2 = binding.local_variable_get(:a)
  binding.local_variable_set(:b, 99)
  r3 = b
  i = 0
  r4 = []
  while i < 3
    a += 1
    r4 << binding.local_variable_get(:a)
    i += 1
  end
  [1].each { |v| a = v + 100 }
  r5 = binding.local_variable_get(:a)
  eval("b = 7")
  r6 = [b, local_variables.sort]
  [z, r1, r2, r3, r4, r5, r6]
end
row "large body, scope made at later calls", blv_big(-1)
row "large body, again", blv_big(-2)

def blv_big_rescue(x)
  z = x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs
  a = 1
  begin
    a = 2
    raise "e" if a > 1
  rescue => e
    a = 3
    r1 = [binding.local_variable_get(:a), e.message]
  ensure
    a += 1
    r2 = binding.local_variable_get(:a)
  end
  [z, a, r1, r2]
end
row "large body, rescue and ensure", blv_big_rescue(-1)

def blv_big_deep(n, x)
  z = x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs + x.abs
  n == 0 ? z : blv_big_deep(n - 1, x)
end
row "large body, a million deep", (begin; blv_big_deep(1_000_000, 1); rescue SystemStackError => e; e.class; end)


# --- ERB ----------------------------------------------------------------------------------------
erb = ERB.new("<%= defined?(a) ? a : '-' %>,<%= defined?(b) ? b : '-' %>")
row "erb 1", erb.result_with_hash(a: 1)
row "erb 2", erb.result_with_hash(b: 2)
row "erb 3", erb.result_with_hash(a: "x", b: nil)
erb2 = ERB.new("<%= local_variables.sort.inspect %>")
row "erb locals", erb2.result_with_hash(k1: 1, k2: 2)

# `using' in an eval through a binding activates where the binding was taken, for the binding, its copies and the
# copies of those: a copy's scope of its own holds only the locals defined through it.
module BlvR; refine(Integer) { def blv_twice = self * 2 }; end
module BlvHost
  B = binding
end
BlvHost::B.eval("using BlvR")
blv_d = BlvHost::B.dup
row "using through a binding, its dup", (blv_d.eval("3.blv_twice") rescue $!.class)
row "using through a binding, itself", (BlvHost::B.eval("4.blv_twice") rescue $!.class)
row "using through a binding, a dup of a dup", (BlvHost::B.dup.dup.eval("5.blv_twice") rescue $!.class)
module BlvR2; refine(Integer) { def blv_thrice = self * 3 }; end
blv_x = binding
blv_y = blv_x.dup
blv_y.eval("using BlvR2")
row "using through a dup, the dup", (blv_y.eval("6.blv_thrice") rescue $!.class)
row "using through a dup, the original", (blv_x.eval("7.blv_thrice") rescue $!.class)

puts "end"
