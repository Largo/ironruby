# Differential matrix for `C === x' with C a class or module constant in methods the method JIT compiles
# (JitCompiler.EmitModuleCaseEquality, JitRuntime.ModuleCaseEquals). Run under CRuby and under ./ir.sh in every mode:
#
#   ruby Util/jit-caseeq-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-caseeq-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-caseeq-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Which ones are specialized:
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-caseeq-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]' | grep CE
# The traps: Module#=== does not ask x (an is_a? of x's own, a BasicObject, method_missing), C's own === (a `def C.===',
# a singleton class reopened, Module#=== redefined or refined), modules included or extended after the body is hot,
# the constant reassigned to another class and to a Regexp, and a SCREAMING_CASE Regexp constant, whose === sets $~.

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

module Tag; end
module LateTag; end
class Animal; end
class Dog < Animal; end
class Odd; end
Swapped = Animal
WORD = /\A[a-z]+\z/

class CE
  def int(x) = Integer === x ? 1 : 0
  def str(x) = String === x
  def num(x) = Numeric === x
  def cmp(x) = Comparable === x
  def animal(x) = Animal === x
  def tag(x) = Tag === x
  def late(x) = LateTag === x
  def odd(x) = Odd === x
  def mod(x) = Module === x
  def klass(x) = Class === x
  def basic(x) = BasicObject === x
  def swapped(x) = Swapped === x
  def word(x) = WORD === x ? $~[0] : nil
  def kind(x)
    if Integer === x then :int
    elsif Float === x then :float
    elsif String === x then :str
    elsif Array === x then :ary
    else :other
    end
  end
end

o = CE.new
d = Dog.new
t("Integer ===, hot")                    { warm { |i| o.int(i) } }
t("Integer === others")                  { [o.int(2**70), o.int(1.5), o.int(nil), o.int("1"), o.int(Integer)] }
t("String ===")                          { warm { |i| o.str("s#{i}") }; [o.str(:s), o.str(Class.new(String).new("x")), o.str(nil)] }
t("Numeric ===")                         { warm { |i| o.num(i * 0.5) }; [o.num(1r), o.num(1i), o.num("1")] }
t("Comparable ===")                      { warm { |i| o.cmp(i) }; [o.cmp("s"), o.cmp([]), o.cmp(nil)] }
t("Animal ===")                          { warm { |i| o.animal(i.odd? ? d : Animal.new) }; [o.animal(Object.new), o.animal(Dog)] }
t("Module === and Class ===")            { warm { o.mod(String); o.klass(Tag) }; [o.mod(Tag), o.mod(1), o.klass(String), o.klass(Tag), o.klass(String.singleton_class)] }
t("BasicObject ===")                     { warm { |i| o.basic(i) }; [o.basic(nil), o.basic(BasicObject.new)] }
t("a kind chain")                        { warm { |i| o.kind([1, 1.5, "s", [], nil][i % 5]) }; [o.kind(2**70), o.kind({})] }

# Module#=== does not ask x
class Liar
  def is_a?(c) = true
  def kind_of?(c) = true
end
class Ghost < BasicObject
  def method_missing(*) = :missing
end
t("x's is_a? is not asked")              { warm { o.animal(d) }; [o.animal(Liar.new), o.int(Liar.new)] }
t("a BasicObject x")                     { warm { o.animal(d) }; o.animal(Ghost.new) }

# included and extended after the body is hot
t("Tag === before")                      { warm { o.tag(d) } }
class Animal; include Tag; end
t("include after hot")                   { [o.tag(d), o.tag(Animal.new), o.tag(Odd.new)] }
t("extend after hot")                    { x = Odd.new; warm { o.late(x) }; x.extend(LateTag); [o.late(x), o.late(Odd.new)] }

# C's own ===
t("Odd === before")                      { warm { o.odd(Odd.new) } }
def Odd.===(x) = :own
t("def Odd.=== after hot")               { [o.odd(Odd.new), o.odd(1)] }
class << Odd; remove_method :===; end
t("removed")                             { [o.odd(Odd.new), o.odd(1)] }
class Module
  alias_method :__orig_eqq, :===
  def ===(x) = :module
end
t("Module#=== redefined")                { [o.int(1), o.str("s"), o.tag(d)] }
class Module
  alias_method :===, :__orig_eqq
end
t("Module#=== restored")                 { [o.int(1), o.str("s"), o.tag(d)] }

module EqqRefinement
  refine Module do
    def ===(x) = :refined
  end
end
class CE
  using EqqRefinement
  def refined(x) = Integer === x
end
t("refined")                             { warm { |i| o.refined(i) } }

# the constant reassigned
t("Swapped ===")                         { warm { o.swapped(d) } }
Object.send(:remove_const, :Swapped); Object.const_set(:Swapped, Odd)
t("Swapped to another class")            { [o.swapped(d), o.swapped(Odd.new)] }
Object.send(:remove_const, :Swapped); Object.const_set(:Swapped, /\Ad/)
t("Swapped to a Regexp")                 { [o.swapped("dog"), o.swapped("cat"), $~ && $~[0]] }
t("a Regexp constant sets $~")           { warm { |i| o.word("w#{i % 2 == 0 ? 'a' : 'b'}") }; [o.word("hello"), o.word("Hi"), o.word(5)] }
