# Differential matrix for `x.is_a?(C)', `x.kind_of?(C)' and `x.instance_of?(C)' in methods the method JIT
# compiles (JitCompiler.EmitTypeTest, JitRuntime.IsKindOf). Run under CRuby and under ./ir.sh in every mode, and diff:
#
#   ruby Util/jit-isa-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-isa-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-isa-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Which ones are specialized:
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-isa-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]' | grep TT
# The traps: a module included, prepended or extended after the body is hot, a singleton class, a class reopened,
# is_a? redefined in a class, a singleton or a refinement, a BasicObject (no is_a?), method_missing, an argument that
# is no module (TypeError), the constant reassigned, Comparable and Enumerable, instance_of? with a singleton class.

def t(label)
  print label.ljust(52), ' => '
  begin
    v = yield
    puts v.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message[0, 70]}"
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

module Mixed; end
module Late; end
module Pre; end
class Base; end
class Derived < Base; end
class Other; end
NotAModule = 5
Swapped = Base

class TT
  def isa_base(x) = x.is_a?(Base) ? 1 : 0
  def kind_base(x) = x.kind_of?(Base) ? 1 : 0
  def inst_base(x) = x.instance_of?(Base) ? 1 : 0
  def isa_mixed(x) = x.is_a?(Mixed)
  def isa_late(x) = x.is_a?(Late)
  def isa_pre(x) = x.is_a?(Pre)
  def isa_cmp(x) = x.is_a?(Comparable)
  def isa_enum(x) = x.is_a?(Enumerable)
  def isa_int(x) = x.is_a?(Integer)
  def isa_num(x) = x.is_a?(Numeric)
  def isa_str(x) = x.is_a?(String)
  def isa_mod(x) = x.is_a?(Module)
  def isa_class(x) = x.is_a?(Class)
  def isa_bad(x) = x.is_a?(NotAModule)
  def isa_swapped(x) = x.is_a?(Swapped)
  def isa_basic(x) = x.is_a?(BasicObject)
  def inst_obj(x) = x.instance_of?(Object)
  def self_isa = is_a?(TT)
  def self_kind = kind_of?(Comparable)
  def branch(x)
    if x.is_a?(Integer)
      x + 1
    elsif x.is_a?(Float)
      x * 2
    else
      0
    end
  end
end

o = TT.new
d = Derived.new
b = Base.new
t("is_a?(Base), hot")                    { warm { |i| o.isa_base(i.odd? ? d : b) } }
t("is_a?, other receivers")              { [o.isa_base(Other.new), o.isa_base(1), o.isa_base(nil), o.isa_base("s"), o.isa_base(:s), o.isa_base(1.5), o.isa_base([1]), o.isa_base({}), o.isa_base(true)] }
t("kind_of?")                            { warm { |i| o.kind_base(d) }; [o.kind_base(b), o.kind_base(Other.new), o.kind_base(Base)] }
t("instance_of?")                        { warm { |i| o.inst_base(b) }; [o.inst_base(b), o.inst_base(d), o.inst_base(Base)] }
t("instance_of?, singleton class")       { s = Base.new; def s.x = 1; [o.inst_base(s), o.isa_base(s), s.singleton_class.instance_of?(Class)] }
t("instance_of?(Object)")                { warm { o.inst_obj(Object.new) }; [o.inst_obj(Object.new), o.inst_obj(b), o.inst_obj(1), o.inst_obj(nil)] }
t("is_a?(Comparable)")                   { warm { |i| o.isa_cmp(i) }; [o.isa_cmp(1), o.isa_cmp("s"), o.isa_cmp(1.5), o.isa_cmp([]), o.isa_cmp(b), o.isa_cmp(nil)] }
t("is_a?(Enumerable)")                   { warm { |i| o.isa_enum([i]) }; [o.isa_enum([]), o.isa_enum({}), o.isa_enum(1..2), o.isa_enum("s"), o.isa_enum(Struct.new(:a).new(1))] }
t("is_a?(Integer)")                      { warm { |i| o.isa_int(i) }; [o.isa_int(2**70), o.isa_int(1.0), o.isa_int(nil)] }
t("is_a?(Numeric)")                      { warm { |i| o.isa_num(i * 1.5) }; [o.isa_num(1r), o.isa_num(1i), o.isa_num("1")] }
t("is_a?(String)")                       { warm { |i| o.isa_str("a#{i}") }; [o.isa_str(:a), o.isa_str(Class.new(String).new("x"))] }
t("is_a?(Module), classes")              { warm { o.isa_mod(String) }; [o.isa_mod(Comparable), o.isa_mod(1), o.isa_mod(Class.new.singleton_class)] }
t("is_a?(Class), singleton classes")     { warm { o.isa_class(Base) }; [o.isa_class(Base.singleton_class), o.isa_class(Mixed), o.isa_class(b.singleton_class)] }
t("is_a?(BasicObject)")                  { warm { |i| o.isa_basic(i) }; [o.isa_basic(nil), o.isa_basic(BasicObject)] }
t("self is_a?")                          { warm { o.self_isa }; [o.self_isa, o.self_kind] }
t("branch")                              { warm { |i| o.branch(i) }; [o.branch(2**70), o.branch(1.5), o.branch("x"), o.branch(nil)] }

# included, prepended and extended after the body is hot
t("is_a?(Mixed) before")                 { warm { o.isa_mixed(b) } }
class Base; include Mixed; end
t("include after hot")                   { [o.isa_mixed(b), o.isa_mixed(d), o.isa_mixed(Other.new)] }
t("is_a?(Pre) before")                   { warm { o.isa_pre(d) } }
class Derived; prepend Pre; end
t("prepend after hot")                   { [o.isa_pre(d), o.isa_pre(b)] }
t("is_a?(Late) before")                  { x = Other.new; warm { o.isa_late(x) } }
t("extend after hot")                    { x = Other.new; r1 = o.isa_late(x); x.extend(Late); [r1, o.isa_late(x), o.isa_late(Other.new)] }
t("extend a String")                     { s = +"str"; warm { o.isa_late(s) }; s.extend(Late); [o.isa_late(s), o.isa_late("other")] }
t("Integer includes after hot")          { warm { |i| o.isa_late(i) }; class Integer; include Late; end; [o.isa_late(1), o.isa_late(2**70), o.isa_late(1.5)] }

# an argument that is no module, and a constant reassigned
t("is_a?(5)")                            { o.isa_bad(1) }
t("is_a?(5), hot")                       { warm { |i| (o.isa_bad(i) rescue $!.class) } }
t("constant swapped")                    { warm { o.isa_swapped(d) }; Object.send(:remove_const, :Swapped); Object.const_set(:Swapped, Other); [o.isa_swapped(d), o.isa_swapped(Other.new)] }
t("constant swapped to a non-module")    { Object.send(:remove_const, :Swapped); Object.const_set(:Swapped, :sym); (o.isa_swapped(d) rescue $!.class) }

# receivers without Kernel's is_a?
class Bare < BasicObject; end
class Ghost < BasicObject
  def method_missing(name, *args) = [:missing, name, args.size]
end
t("BasicObject receiver")                { warm { o.isa_base(b) }; o.isa_base(Bare.new) }
t("method_missing receiver")             { warm { o.isa_mixed(b) }; [o.isa_mixed(Ghost.new), o.isa_base(Ghost.new)] }

# is_a? redefined
class Liar
  def is_a?(c) = :lies
  def kind_of?(c) = :lies_too
end
t("is_a? redefined in a class")          { warm { o.isa_base(b) }; [o.isa_base(Liar.new), o.kind_base(Liar.new)] }
t("is_a? in a singleton")                { x = Base.new; warm { o.isa_base(x) }; def x.is_a?(c) = 7; o.isa_base(x) }
t("is_a? redefined on Base later")       { warm { o.isa_base(d) }; class Base; def is_a?(c) = false; end; [o.isa_base(d), o.isa_base(b)] }
class Base; remove_method :is_a?; end
t("is_a? restored")                      { [o.isa_base(d), o.isa_base(b)] }
t("Kernel#is_a? redefined")              { warm { o.isa_cmp(1) }; module Kernel; alias_method :__orig_is_a, :is_a?; def is_a?(c) = :kernel; end; o.isa_cmp(1) }
module Kernel; alias_method :is_a?, :__orig_is_a; end
t("Kernel#is_a? restored")               { o.isa_cmp(1) }

module IsARefinement
  refine Object do
    def is_a?(c) = :refined
  end
end
class TT
  using IsARefinement
  def isa_refined(x) = x.is_a?(Base)
end
t("refined")                             { warm { o.isa_refined(b) } }
