# Differential matrix for Kernel#String (rb_String: rb_check_string_type, then #to_s through rb_convert_type):
# which conversion it calls, what it answers, the errors and their messages. A receiver whose class has no #to_str
# and the built-in #respond_to? and #respond_to_missing? is converted by its #to_s from C# (__ir_plain_to_s__,
# IR_PLAIN_TO_S); everything else by the prelude's __ir_string__. Run it under CRuby and under ./ir.sh, and diff:
#
#   ruby Util/kernel-string-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/kernel-string-matrix.rb > /tmp/on.txt 2>&1
#   IR_PLAIN_TO_S=0 ./ir.sh Util/kernel-string-matrix.rb > /tmp/off.txt 2>&1
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt

def t(label)
  print label.ljust(48), ' => '
  begin
    puts yield.inspect.gsub(/0x\h+/, '0x')
  rescue Exception => e
    puts "#{e.class}: #{e.message.gsub(/0x\h+/, '0x')}"
  end
end

def r(x)
  s = String(x)
  [s, s.class, s.frozen?, s.encoding.to_s, s.equal?(x)]
end

# ---------------------------------------------------------------- 1. the common receivers
class SubString < String; end
Point = Struct.new(:x)
t('a String') { r("s") }
t('a frozen String') { r("f".freeze) }
t('a String subclass') { r(SubString.new("u")) }
t('a binary String') { r("\xff".b) }
t('nil') { r(nil) }
t('nil twice, the same object') { String(nil).equal?(String(nil)) }
t('true, false') { [r(true), r(false)] }
t('an Integer') { r(5) }
t('a negative Integer, 0') { [r(-12), r(0)] }
t('a Bignum') { r(2**70) }
t('a Float') { [r(1.5), r(1e20), r(-0.0), r(1.0 / 3)] }
t('NaN, Infinity') { [r(Float::NAN), r(-Float::INFINITY)] }
t('a Symbol') { r(:sym) }
t('a Symbol twice, new Strings') { String(:s).equal?(String(:s)) }
t('an operator Symbol') { r(:+) }
t('a Rational, a Complex') { [r(3r), r(1i)] }
t('an Array') { r([1, "a", nil]) }
t('a Hash') { r({ a: 1 }) }
t('a Range') { r(1..2) }
t('a Struct') { r(Point.new(1)) }
t('a Class, a Module') { [r(Integer), r(Kernel)] }
t('an Object') { String(Object.new).sub(/0x\h+/, '') }
t('a Proc') { String(proc {}).sub(/0x\h+ .*>/, '>') }
t('an Encoding, a Regexp') { [r(Encoding::UTF_8), r(/a/i)] }
t('main') { r(self) }

# ---------------------------------------------------------------- 2. #to_str and #to_s of a class of its own
class ToStr; def to_str = "via to_str"; def to_s = "via to_s"; end
class ToStrNil; def to_str = nil; def to_s = "to_s after nil"; end
class ToStrBad; def to_str = 5; end
class ToStrPrivate; private def to_str = "private to_str"; end
class ToS; def to_s = "plain"; end
class ToSPrivate; private def to_s = "private to_s"; end
class ToSProtected; protected def to_s = "protected to_s"; end
class ToSInt; def to_s = 5; end
class ToSNil; def to_s = nil; end
class ToSFalse; def to_s = false; end
class ToSSym; def to_s = :sym; end
class ToSSub; def to_s = SubString.new("sub"); end
class NoToS; undef_method :to_s; end
class RespondNo; def respond_to?(m, priv = false) = m == :to_s ? false : super; end
class RespondYes; def respond_to?(m, priv = false) = m == :to_str ? true : super; def method_missing(m, *a) = m == :to_str ? "mm to_str" : super; end
class MissingToStr; def respond_to_missing?(m, priv = false) = m == :to_str || super; def method_missing(m, *a) = m == :to_str ? "rtm to_str" : super; end
class OnlyMissing; def method_missing(m, *a) = m == :to_str ? "mm only" : super; end
class MissingToS; undef_method :to_s; def method_missing(m, *a) = m == :to_s ? "mm to_s" : super; end
class RaisingToS; def to_s = raise(ArgumentError, "from to_s"); end
class CountToS; attr_reader :n; def to_s = (@n = (@n || 0) + 1; 7); end
Anon = Class.new { def to_s = 5 }

t('to_str first') { r(ToStr.new) }
t('to_str nil, then to_s') { r(ToStrNil.new) }
t('to_str an Integer') { String(ToStrBad.new) }
t('a private to_str') { r(ToStrPrivate.new) }
t('a to_s of its own') { r(ToS.new) }
t('a private to_s') { r(ToSPrivate.new) }
t('a protected to_s') { r(ToSProtected.new) }
t('to_s an Integer') { String(ToSInt.new) }
t('to_s nil') { String(ToSNil.new) }
t('to_s false') { String(ToSFalse.new) }
t('to_s a Symbol') { String(ToSSym.new) }
t('to_s a String subclass') { r(ToSSub.new) }
t('no to_s') { String(NoToS.new) }
t('respond_to? says no to to_s') { String(RespondNo.new) }
t('respond_to? says yes to to_str') { String(RespondYes.new) }
t('respond_to_missing? and method_missing') { String(MissingToStr.new) }
t('method_missing only') { String(OnlyMissing.new).sub(/0x\h+/, '') }
t('method_missing for to_s') { String(MissingToS.new) }
t('to_s raises') { String(RaisingToS.new) }
t('to_s is called once') { c = CountToS.new; begin; String(c); rescue TypeError; end; c.n }
t('an anonymous class, to_s an Integer') { String(Anon.new) }
t('a singleton to_s') { o = Object.new; def o.to_s = "single"; r(o) }
t('a singleton to_str') { o = Object.new; def o.to_str = "single str"; r(o) }

# ---------------------------------------------------------------- 3. BasicObject
class BasicToS < BasicObject; def to_s = "basic"; end
class BasicMissing < BasicObject; def method_missing(m, *a) = m == :to_s ? "bm" : super; end
t('a BasicObject') { String(BasicObject.new) }
t('a BasicObject with to_s') { String(BasicToS.new) }
t('a BasicObject with method_missing') { String(BasicMissing.new) }

# ---------------------------------------------------------------- 4. the core classes changed, and back
t('Integer#to_s redefined') do
  class Integer; alias __saved_to_s__ to_s; def to_s(*) = "int!"; end
  begin
    [String(5), String(2**70)]
  ensure
    class Integer; alias to_s __saved_to_s__; remove_method :__saved_to_s__; end
  end
end
t('restored') { [String(5), String(2**70)] }
t('NilClass#to_s redefined to an Integer') do
  class NilClass; alias __saved_to_s__ to_s; def to_s = 0; end
  begin
    String(nil)
  ensure
    class NilClass; alias to_s __saved_to_s__; remove_method :__saved_to_s__; end
  end
end
t('Symbol#to_str defined') do
  class Symbol; def to_str = "sym str"; end
  begin
    r(:q)
  ensure
    class Symbol; remove_method :to_str; end
  end
end
t('Float#respond_to? redefined') do
  class Float; def respond_to?(m, priv = false) = m == :to_s ? false : super; end
  begin
    String(1.5)
  ensure
    class Float; remove_method :respond_to?; end
  end
end
t('Integer#respond_to_missing? and method_missing') do
  class Integer
    def respond_to_missing?(m, priv = false) = m == :to_str || super
    def method_missing(m, *a) = m == :to_str ? "int str" : super
  end
  begin
    String(9)
  ensure
    class Integer; remove_method :respond_to_missing?, :method_missing; end
  end
end
t('to_str added after a first call') do
  k = Class.new { def to_s = "s1" }
  o = k.new
  first = String(o)
  k.define_method(:to_str) { "now to_str" }
  [first, String(o)]
end
t('to_s made private after a first call') do
  k = Class.new { def to_s = "s2" }
  o = k.new
  first = String(o)
  k.send(:private, :to_s)
  [first, String(o)]
end
t('a module with to_str included later') do
  k = Class.new { def to_s = "s3" }
  o = k.new
  first = String(o)
  k.include(Module.new { def to_str = "mod str" })
  [first, String(o)]
end

# ---------------------------------------------------------------- 5. the method
t('Kernel.String, method(:String)') { [Kernel.String(5), method(:String).call(:a), Kernel.method(:String).call(nil)] }
t('arity, owner, parameters') { m = Kernel.instance_method(:String); [m.arity, m.owner, m.parameters.map(&:first)] }
t('String()') { String() }
t('String(1, 2)') { String(1, 2) }
t('from a thread') { 4.times.map { |i| Thread.new { String(i) } }.map(&:value) }
t('backtrace from to_s') do
  String(RaisingToS.new)
rescue => e
  e.backtrace_locations.first(3).map(&:label)
end

# ---------------------------------------------------------------- 6. a refinement of #to_s is not seen (rb_String calls it from C)
module RefineToS
  refine Integer do
    def to_s(*) = "refined"
  end
  refine ToS do
    def to_s = "refined ToS"
  end
end
using RefineToS
t('a refined Integer#to_s') { [String(5), 5.to_s] }
t('a refined to_s of a class of its own') { String(ToS.new) }
class ToStrTrue; def to_str = true; end
class ToSTrue; def to_s = true; end
class ToStrNilToSNil; def to_str = nil; def to_s = nil; end
t('to_str true') { String(ToStrTrue.new) }
t('to_s true') { String(ToSTrue.new) }
t('to_str nil, then to_s nil') { String(ToStrNilToSNil.new) }
t('TrueClass#to_s redefined to an Integer') do
  class TrueClass; alias __saved_to_s__ to_s; def to_s = 0; end
  begin
    String(true)
  ensure
    class TrueClass; alias to_s __saved_to_s__; remove_method :__saved_to_s__; end
  end
end

# ---------------------------------------------------------------- 7. rb_check_funcall for to_str: a private one, method_missing
class MissingRaises; def method_missing(m, *a) = m == :to_str ? raise(NoMethodError.new("no", :to_str)) : super; def to_s = "after mm"; end
class MissingOther; def method_missing(m, *a) = m == :to_str ? raise(NoMethodError.new("other", :other)) : super; end
class MissingArg; def method_missing(m, *a) = m == :to_str ? raise(ArgumentError, "from mm") : super; end
class MissingNil; def method_missing(m, *a) = m == :to_str ? nil : super; def to_s = "mm nil then to_s"; end
class MissingInt; def method_missing(m, *a) = m == :to_str ? 3 : super; end
class PrivRespond; private def respond_to?(m, priv = false) = m == :to_str; def method_missing(m, *a) = m == :to_str ? "pr" : super; end
class ProtToStr; protected def to_str = "protected to_str"; end
t('method_missing raises NoMethodError for to_str') { String(MissingRaises.new) }
t('method_missing raises NoMethodError for another') { String(MissingOther.new) }
t('method_missing raises ArgumentError') { String(MissingArg.new) }
t('method_missing answers nil') { String(MissingNil.new) }
t('method_missing answers an Integer') { String(MissingInt.new) }
t('a private respond_to?') { String(PrivRespond.new) }
t('a protected to_str') { String(ProtToStr.new) }
class NoToSMissingRaises < BasicObject; def method_missing(m, *a) = m == :to_s ? ::Kernel.raise(::NoMethodError.new("x", :to_s)) : super; end
class NoToSMissingOther; undef_method :to_s; def method_missing(m, *a) = m == :to_s ? raise(NoMethodError.new("y", :other)) : super; end
class PrivRespondToS; undef_method :to_s; private def respond_to?(m, priv = false) = false; end
t('BasicObject, method_missing raises for to_s') { String(NoToSMissingRaises.new) }
t('no to_s, method_missing raises for another') { String(NoToSMissingOther.new) }
t('no to_s, a private respond_to? says no') { String(PrivRespondToS.new) }
