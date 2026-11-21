# Differential matrix for the method JIT (-X:JIT, Src/Ruby/Runtime/Jit/): an interpolated String
# ("a#{x}") in a body the JIT compiles (JitCompiler.EmitInterpolation). The typed body makes the
# String the generic body makes; a part that is a String is taken as it is, an Integer, Float,
# Symbol, nil, true or false is converted as the generic body converts it while its class's #to_s
# is the library one, and anything else deopts, so that no #to_s written in Ruby runs from a typed
# body. Run under CRuby and under ./ir.sh - with the JIT on and off - and diff:
#
#   ruby Util/jit-interp-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-interp-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-interp-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Every method is called well past the JIT's hotness threshold: each row prints what the calls
# answered (each distinct answer once), and for a String its encoding and frozen state. Which
# methods were specialized is not part of the output:
#
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-interp-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]'
#
# The methods named si_* are shapes the JIT is expected to specialize.

def t(label)
  print label.ljust(56), ' => '
  begin
    v = yield
    puts v.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message[0, 60]}"
  end
end

HOT = 120

# Calls the block HOT times and answers the distinct values, each String with its encoding and
# whether it is frozen.
def hot
  seen = []
  HOT.times do |i|
    v = yield i
    v = [v, v.encoding.to_s, v.frozen?] if v.is_a?(String)
    seen << v unless seen.include?(v)
  end
  seen
end

def label(line) = line[/'(?:[^']*[#.])?([^']*)'/, 1]

class Plain; def to_s = "plain"; end
class NonString; def to_s = 42; end
class PrivToS; private def to_s = "private"; end
class Counted; attr_reader :n; def initialize = @n = 0; def to_s = (@n += 1).to_s; end
class SubString < String; end

class Si
  attr_accessor :a, :b, :n, :s
  def initialize = (@a = 1; @b = :sym; @n = 0; @s = nil)
  def si_one(x) = "n=#{x}"
  def si_only(x) = "#{x}"
  def si_two(x, y) = "<#{x}|#{y}>"
  def si_arith(x) = "#{x * 2}"
  def si_float(x) = "#{x / 2.0}"
  def si_ivars = "#{@a}/#{@b}"
  def si_local(x)
    y = x + 1
    "#{y}"
  end
  def si_cond(x) = x > 0 ? "pos #{x}" : "neg #{x}"
  def si_nested(x) = "a#{"b#{x}c"}d"
  def si_empty_part(x) = "a#{}b#{x}"
  def si_words(x) = %W[a#{x} b]
  def si_heredoc(x)
    <<~EOS
      one #{x}
      two
    EOS
  end
  def si_utf8(x) = "é#{x}"
  def si_cat(x, y) = "#{x}#{y}"
  def si_store(x) = (@s = "v#{x}"; @s)
  def si_after_store(x) = (@n = x; "#{@n}")
  def si_write_then(x) = "#{@n += 1}#{x}"
  def si_then_write(x) = "#{x}#{@n += 1}"
  def si_div(x, y) = "#{x / y}"
  def si_compare(x) = "#{x}" == "1"
  def si_length(x) = "ab#{x}".length
  def si_symbol(x) = :"s#{x}"
  def si_deep(n) = n == 0 ? "" : "#{si_deep(n - 1)}"
  def si_deep2(n)
    s = n == 0 ? "" : si_deep2(n - 1)
    "#{s}"
  end
  def si_bool(x) = "#{x > 1}"
end

o = Si.new

puts '--- what each part is'
t('a String') { hot { |_| o.si_one("s") } }
t('a String is copied') { s = +"s"; hot { |_| r = o.si_only(s); [r, r.equal?(s)] } }
t('a String subclass') { hot { |_| r = o.si_only(SubString.new("x")); [r, r.class] } }
t('an Integer') { hot { |i| o.si_one(i % 3) } }
t('a big Integer') { hot { |_| o.si_one(2**70) } }
t('a negative Integer') { hot { |_| o.si_one(-5) } }
t('a Float') { hot { |_| o.si_one(1.5) } }
t('a Float, special') { [o.si_one(0.0 / 0), o.si_one(1e100), o.si_one(-0.0)] }
t('a Symbol') { hot { |_| o.si_one(:sym) } }
t('nil') { hot { |_| o.si_one(nil) } }
t('true and false') { hot { |_| o.si_two(true, false) } }
t('a user to_s') { hot { |_| o.si_one(Plain.new) } }
t('to_s answers an Integer') { hot { |_| o.si_one(NonString.new).sub(/0x\h+/, 'X') } }
t('a private to_s') { hot { |_| o.si_one(PrivToS.new) } }
t('an Array') { hot { |_| o.si_one([1, :a, nil]) } }
t('a Class') { hot { |_| o.si_one(Si) } }
t('to_s once per part') { c = Counted.new; hot { |_| o.si_two(c, c) }; c.n }
t('an Integer, then a user object') { hot { |i| o.si_one(i < 60 ? i % 2 : Plain.new) } }
t('a user object, then an Integer') { hot { |i| o.si_one(i < 60 ? Plain.new : i % 2) } }

puts '--- typed parts'
t('Integer arithmetic') { hot { |i| o.si_arith(i % 4) } }
t('Integer arithmetic, overflow') { hot { |_| o.si_arith(2**62) } }
t('Integer arithmetic on a Float') { hot { |_| o.si_arith(1.25) } }
t('Float division') { hot { |i| o.si_float(i % 3) } }
t('instance variables') { hot { |_| o.si_ivars } }
t('instance variables change') { r = hot { |i| o.a = i % 2 == 0 ? 1 : "x"; o.si_ivars }; o.a = 1; r }
t('a local') { hot { |i| o.si_local(i % 3) } }
t('a local, overflow') { hot { |_| o.si_local(2**63 - 1) } }
t('a condition') { hot { |i| o.si_cond(i % 3 - 1) } }
t('a comparison') { hot { |i| o.si_bool(i % 3) } }
t('nested') { hot { |_| o.si_nested(1) } }
t('an empty part') { hot { |_| o.si_empty_part(2) } }
t('%W') { hot { |_| o.si_words(1) } }
t('a heredoc') { hot { |_| o.si_heredoc(:h) } }
t('divided by zero') { hot { |i| o.si_div(6, i < 100 ? 2 : 0) } }
t('divided by zero, the frames') { begin; o.si_div(1, 0); rescue ZeroDivisionError => e; e.backtrace[0, 2].map { |l| label(l) }; end }
t('compared') { hot { |i| o.si_compare(i % 2) } }
t('its length') { hot { |i| o.si_length(i % 2 == 0 ? 7 : "xyz") } }
t('a Symbol made by interpolation') { hot { |_| o.si_symbol(1) } }

puts '--- encodings'
t('UTF-8 parts') { hot { |_| o.si_cat("a", "é") } }
t('binary parts') { hot { |_| o.si_cat("\xff".b, "\xfe".b) } }
t('a binary ASCII part and a UTF-8 part') { hot { |_| o.si_cat("a".b, "é") } }
t('US-ASCII parts') { hot { |_| o.si_cat("a".encode("US-ASCII"), "b".encode("US-ASCII")) } }
t('an Integer and a Symbol') { hot { |_| o.si_cat(1, :b) } }
t('a UTF-8 literal and an Integer') { hot { |_| o.si_utf8(1) } }
t('a UTF-8 literal and a binary part') { hot { |_| o.si_utf8("\xff".b) } }
t('incompatible parts') { hot { |_| o.si_cat("é", "\xff".b) } }

puts '--- effects and deopts'
t('stored in an ivar') { r = hot { |i| o.si_store(i % 2) }; [r, o.s] }
t('after an ivar write') { hot { |i| o.si_after_store(i % 2) } }
t('a write, then an Integer part') { o.n = 0; r = hot { |_| o.si_write_then(7) }; [r.size, o.n] }
t('a write, then a user part') { o.n = 0; r = hot { |_| o.si_write_then(Plain.new) }; [r.size, o.n] }
t('a write, then a Counted part') { o.n = 0; c = Counted.new; hot { |_| o.si_write_then(c) }; [o.n, c.n] }
t('an Integer part, then a write') { o.n = 0; r = hot { |_| o.si_then_write(7) }; [r.size, o.n] }
t('a user part, then a write') { o.n = 0; r = hot { |_| o.si_then_write(Plain.new) }; [r.size, o.n] }
t('a Counted part, then a write') { o.n = 0; c = Counted.new; hot { |_| o.si_then_write(c) }; [o.n, c.n] }
t('a write on a frozen self') { f = Si.new.freeze; begin; f.si_write_then(1); rescue => e; e.class; end }

puts '--- to_s redefined after the bodies were compiled'
t('before') { hot { |_| [o.si_one(nil), o.si_one(:s), o.si_one(1), o.si_one(true)] } }
class NilClass; alias_method :__si_to_s, :to_s; def to_s = "NIL"; end
t('NilClass#to_s redefined') { hot { |_| o.si_one(nil) } }
class NilClass; alias_method :to_s, :__si_to_s; end
t('NilClass#to_s restored') { hot { |_| o.si_one(nil) } }
class Symbol; alias_method :__si_to_s, :to_s; def to_s = "SYM"; end
t('Symbol#to_s redefined') { hot { |_| o.si_one(:s) } }
class Symbol; alias_method :to_s, :__si_to_s; end
t('Symbol#to_s restored') { hot { |_| o.si_one(:s) } }
class Integer; alias_method :__si_to_s, :to_s; def to_s(*) = "INT"; end
t('Integer#to_s redefined') { hot { |_| o.si_one(3) } }
class Integer; alias_method :to_s, :__si_to_s; end
t('Integer#to_s restored') { hot { |_| o.si_one(3) } }
class TrueClass; private def to_s = "TRUE"; end
t('TrueClass#to_s private') { hot { |_| o.si_one(true) } }
class Float; prepend(Module.new { def to_s = "FLT" }); end
t('Float#to_s prepended') { hot { |_| o.si_one(1.5) } }

puts '--- recursion and threads'
t('self-recursion') { hot { |_| o.si_deep(30) } }
t('self-recursion a million deep') { o.si_deep(1_000_000) }
t('self-recursion, then the part') { hot { |_| o.si_deep2(30) } }
t('self-recursion, then the part, a million deep') { o.si_deep2(1_000_000) }
class Wrap; def initialize(i) = @i = i; def to_s = "<#{@i}>"; end
t('nested to_s') { hot { |_| Wrap.new(Wrap.new(1)).to_s } }
t('nested to_s a million deep') { w = 0; 1_000_000.times { w = Wrap.new(w) }; o.si_one(w).size }
t('threads') do
  Array.new(4) { |k| Thread.new { Array.new(500) { |j| o.si_two(k, j % 7) }.uniq.size } }.map(&:value)
end

puts '--- frozen_string_literal: true'
eval(<<~'RUBY', nil, 'si_frozen.rb', 1)
  # frozen_string_literal: true
  class SiFrozen
    def si_one(x) = "a#{x}"
    def si_lit_only = "a#{}b"
  end
RUBY
f = SiFrozen.new
t('interpolated under frozen_string_literal') { hot { |i| f.si_one(i % 2) } }
t('a new String per call') { f.si_one(1).equal?(f.si_one(1)) }
t('empty interpolation under frozen_string_literal') { hot { |_| f.si_lit_only } }
