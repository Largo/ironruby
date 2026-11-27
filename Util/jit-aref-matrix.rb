# Differential matrix for `a[i]` in methods the method JIT compiles once some Array has a singleton `[]`
# (as CSV's `NO_QUOTED_FIELDS` has: `def NO_QUOTED_FIELDS.[](_index)`), JitCompiler.EmitIndexRead. Run under
# CRuby and under ./ir.sh in every mode, and diff:
#
#   ruby Util/jit-aref-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-aref-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-aref-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Which methods are specialized:
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-aref-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]' | grep ar_
# The traps: the Array with the singleton `[]` must reach it from a body compiled before and after it was defined;
# every other Array keeps Array#[] (negative index, out of range, frozen); a singleton method_missing does not
# change `[]`; a subclass's own `[]`, a refinement and a redefinition of Array#[] are dispatched to.

def t(label)
  print label.ljust(52), ' => '
  begin
    v = yield
    puts v.inspect
  rescue Exception => e
    puts e.class
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

class AR
  def initialize(a); @a = a; end
  def ar_top = @a[-1]
  def ar_at(i) = @a[i]
  def ar_first(x) = x[0]
  def ar_sum(x) = x[0] + x[-1]
end

o = AR.new([1, 2, 3])
t("before: top")                   { warm { o.ar_top } }
t("before: at, out of range")      { warm { |i| o.ar_at(i % 5 - 2) } }
t("before: first")                 { warm { |i| o.ar_first([i, 0]) } }

SPECIAL = [7, 8, 9]
def SPECIAL.[](i) = "special #{i}"

t("after: special, compiled before") { AR.new(SPECIAL).ar_top }
t("after: first on special")       { o.ar_first(SPECIAL) }
t("after: top")                    { warm { o.ar_top } }
t("after: at, negative")           { warm { |i| o.ar_at(-1 - i % 3) } }
t("after: at, out of range")       { [o.ar_at(5), o.ar_at(-5)] }
t("after: first")                  { warm { |i| o.ar_first([i, 0]) } }
t("after: sum")                    { warm { |i| o.ar_sum([i, 1, 2]) } }
t("after: special again")          { [o.ar_first(SPECIAL), AR.new(SPECIAL).ar_top, SPECIAL.first] }
t("after: frozen")                 { o.ar_first([4, 5].freeze) }
t("after: dup of special")         { o.ar_first(SPECIAL.dup) }
t("after: clone of special")       { o.ar_first(SPECIAL.clone) }

MM = [1]
def MM.method_missing(n, *a) = :mm
t("method_missing singleton")      { [o.ar_first(MM), warm { |i| o.ar_first([i]) }] }

class SubA < Array
  def [](i) = :sub
end
t("subclass with []")              { o.ar_first(SubA.new([1])) }
t("hash")                          { o.ar_first({0 => :h}) }
t("string")                        { o.ar_first("xyz") }
t("nil")                           { o.ar_first(nil) }

LATE = [5, 6]
t("late: before its singleton")    { o.ar_first(LATE) }
def LATE.[](i) = :late
t("late: after its singleton")     { [o.ar_first(LATE), warm { |i| o.ar_first([i]) }] }

module ArefRefinement
  refine Array do
    def [](i) = :refined
  end
end
class AR
  using ArefRefinement
  def ar_refined(x) = x[0]
end
t("refined")                       { warm { o.ar_refined([1]) } }

class Array
  alias_method :__orig_aref, :[]
  def [](*a) = :redefined
end
t("Array#[] redefined")            { o.ar_first([1]) }
class Array
  alias_method :[], :__orig_aref
end
t("Array#[] restored")             { warm { |i| o.ar_first([i]) } }
