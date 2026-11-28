# Differential matrix for operations on an object in methods the method JIT compiles: `x.to_i' (JitRuntime.ToIntegerObject),
# `x.empty?' of a String or of a receiver of several types (StringEmpty, IsEmptyOrString), `!x' of any object (NotObject),
# writes of an instance variable as an object and as a boolean held back (JitCompiler.Sink.cs, _sinkBoxed), and `x.call'
# refused by the screen. Run under CRuby and under ./ir.sh in every mode, and diff:
#
#   ruby Util/jit-objops-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-objops-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-objops-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Which ones are specialized:
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-objops-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]' | grep OO
# The traps: Strings to_i parses other than as plain digits (signs, blanks, underscores, a Bignum, other digits, an
# ASCII-incompatible encoding), receivers without the method, a String subclass, singleton methods, to_i, empty? and !
# redefined or refined, a frozen self after held-back writes, the order of a held-back object's first writes.

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

class OO
  def to_i_of(x) = x.to_i
  def empty_of(x) = x.empty?
  def not_of(x) = !x
  def set_port(v)
    v = v.empty? ? nil : v.to_i unless !v || v.kind_of?(Integer)
    @port = v
  end
  attr_reader :port, :a, :b, :c
  def held(h)
    @a = h[:a]
    if h[:flag]
      @b = true
      @c = h[:c].is_a?(Hash) ? h[:c][:x] : false
    else
      @b = false
      @c = false
    end
    @a
  end
  def calls(x) = x.call
end

o = OO.new
t("to_i of a String, hot")               { warm { |i| o.to_i_of(i.to_s) } }
t("to_i of Strings")                     { ["007", "", " 12", "-5", "+5", "1_000", "12abc", "9" * 18, "9" * 19, "9" * 30, "0x1A", "１２", "12\n"].map { |s| o.to_i_of(s) } }
t("to_i of other encodings")             { ["12".b, "12".encode("US-ASCII"), "12".force_encoding("Shift_JIS")].map { |s| o.to_i_of(s) } }
t("to_i, UTF-16LE")                      { o.to_i_of("12".encode("UTF-16LE")) }
t("to_i of non-Strings")                 { [o.to_i_of(nil), o.to_i_of(5), o.to_i_of(2**70), o.to_i_of(1.9), o.to_i_of(-1.9), o.to_i_of(3r)] }
t("to_i of a Symbol")                    { o.to_i_of(:a) }
t("to_i of an Object")                   { o.to_i_of(Object.new) }
t("to_i of a String subclass")           { c = Class.new(String) { def to_i = 99 }; [o.to_i_of(c.new("5")), o.to_i_of(Class.new(String).new("6"))] }
t("to_i, singleton")                     { s = +"7"; def s.to_i = 70; [o.to_i_of(s), o.to_i_of("7")] }
t("to_i, frozen and chilled")            { [o.to_i_of("42".freeze), o.to_i_of("43")] }
t("to_i, value kinds")                   { r = o.to_i_of("123"); [r, r.class, r.equal?(123)] }

t("empty? of a String, hot")             { warm { |i| o.empty_of(i.even? ? "" : "x") } }
t("empty? of others")                    { [o.empty_of([]), o.empty_of([1]), o.empty_of({}), o.empty_of({a: 1}), o.empty_of(+""), o.empty_of("\0")] }
t("empty? of nil")                       { o.empty_of(nil) }
t("empty? of 5")                         { o.empty_of(5) }
t("empty?, String subclass")             { c = Class.new(String) { def empty? = :sub }; o.empty_of(c.new("")) }
t("empty?, singleton")                   { s = +""; def s.empty? = :single; o.empty_of(s) }

t("! of objects, hot")                   { warm { |i| o.not_of(i.even? ? "s" : Object.new) } }
t("! of everything")                     { [nil, false, true, 0, "", [], :s, 1.5, Object.new].map { |x| o.not_of(x) } }
t("!, a class with its own !")           { c = Class.new { def ! = :bang }; o.not_of(c.new) }
t("!, singleton")                        { x = Object.new; def x.! = :single; o.not_of(x) }
t("!, BasicObject")                      { o.not_of(BasicObject.new) }

t("set_port, mixed, hot")                { warm { |i| o.set_port(["80", nil, 443, "", "8080"][i % 5]) } }
t("set_port values")                     { ["80", nil, 443, "", "8080", " 8", "0x10"].map { |v| o.set_port(v); o.port } }
t("set_port, false")                     { o.set_port(false); o.port }
t("set_port, Array")                     { o.set_port([]); o.port }
t("set_port, Symbol")                    { o.set_port(:x) }

t("held, hot")                           { q = OO.new; warm { |i| q.held({ a: i, flag: i.odd?, c: (i % 3 == 0) ? { x: i } : 1 }) }; [q.a, q.b, q.c] }
t("held, order of first writes")         { q = OO.new; q.held({ a: 1, flag: false }); q.instance_variables }
t("held, then frozen")                   { q = OO.new; warm { |i| q.held({ a: i, flag: true, c: {} }) }; q.freeze; [(q.held({ a: 9 }) rescue $!.class), q.a] }
t("held, a default proc")                { q = OO.new; h = Hash.new { |_, k| k == :a ? :dflt : nil }; [q.held(h), q.a, q.b] }
t("held, a deopt after the writes")      { q = OO.new; warm { |i| q.held({ a: i, flag: true, c: {} }) }; h = { a: 1, flag: true, c: Class.new(Hash).new }; [q.held(h), q.c] }

t("call, hot")                           { pr = proc { 1 }; warm { o.calls(pr) } }
t("call of a lambda and a Method")       { [o.calls(-> { 2 }), o.calls(1.method(:to_s))] }

class String
  alias_method :__orig_to_i, :to_i
  def to_i = -1
end
t("String#to_i redefined")               { [o.to_i_of("5"), o.set_port("80"), o.port] }
class String
  alias_method :to_i, :__orig_to_i
  alias_method :__orig_empty, :empty?
  def empty? = true
end
t("String#empty? redefined")             { [o.empty_of("x"), o.set_port("80"), o.port] }
class String
  alias_method :empty?, :__orig_empty
end
class NilClass
  alias_method :__orig_to_i, :to_i
  def to_i = -2
end
t("NilClass#to_i redefined")             { o.to_i_of(nil) }
class NilClass
  alias_method :to_i, :__orig_to_i
end
class BasicObject
  alias_method :__orig_not, :!
  def ! = :redefined
end
t("BasicObject#! redefined")             { [o.not_of("s"), o.not_of(nil), o.not_of(Object.new)] }
class BasicObject
  alias_method :!, :__orig_not
end
t("restored")                            { [o.to_i_of("5"), o.empty_of(""), o.not_of("s"), o.set_port("80"), o.port] }

module ToIRefinement
  refine String do
    def to_i = :refined
  end
end
class OO
  using ToIRefinement
  def to_i_refined(x) = x.to_i
end
t("refined")                             { warm { o.to_i_refined("5") } }
