# Differential matrix for Hash literals in methods the method JIT compiles (-X:JIT,
# JitCompiler.EmitHashLiteral). Run under CRuby and under ./ir.sh in every mode, and diff:
#
#   ruby Util/jit-hash-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-hash-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-hash-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Every method is called past the JIT's thresholds, then probed. Which ones are specialized:
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-hash-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]' | grep hl_
# The traps: a new Hash on every evaluation (never one shared), keys and values evaluated in
# order, a String key copied and frozen (the literal's String not kept), a deopt after the
# literal (it is no effect, so the generic body may build it again), a literal kept in an
# instance variable by `||=`, and defaults that are Hash literals.

def t(label)
  print label.ljust(52), ' => '
  begin
    v = yield
    puts v.inspect
  rescue Exception => e
    puts "#{e.class}"
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

class HL
  def initialize
    @n = 0
    @h = nil
  end
  attr_reader :n

  def hl_empty; {}; end
  def hl_sym1(x); {a: x}; end
  def hl_sym2(x, y); {a: x, b: y}; end
  def hl_sym3(x); {a: x, b: x + 1, c: x * 2}; end
  def hl_sym5(x); {a: x, b: x, c: x, d: x, e: x}; end
  def hl_str(x); {"k" => x, "l" => x + 1}; end
  def hl_mixed(x); {1 => x, nil => :n, true => :t, false => :f, :s => "v"}; end
  def hl_float(x); {1.5 => x}; end
  def hl_nested(x); {a: {b: [x, {}]}}; end
  def hl_ivar(x); @h = {v: x}; @h; end
  def hl_memo; @m ||= {}; end
  def hl_default(x, opts = {}); opts[:seen] = x; opts; end
  def hl_bignum(x); {v: x * x * x * x}; end
  def hl_float_val(x); {v: x / 2.0}; end
  def hl_after_effect(x); @n += 1; {v: x + 1}; end
  def hl_size(x); {a: x, b: x}.size + x; end
  def hl_recurse(n); n == 0 ? {} : {d: hl_recurse(n - 1)}; end
end

o = HL.new
t("empty")                { warm { o.hl_empty } }
t("empty: a new one each call") { a = o.hl_empty; b = o.hl_empty; [a.equal?(b), a.frozen?] }
t("empty: mutable")       { h = o.hl_empty; h[:x] = 1; [h, o.hl_empty] }
t("sym1")                 { warm { |i| o.hl_sym1(i) } }
t("sym1, a String value") { o.hl_sym1("s") }
t("sym2")                 { warm { |i| o.hl_sym2(i, i * 2) } }
t("sym3")                 { warm { |i| o.hl_sym3(i) } }
t("sym3, a Float")        { o.hl_sym3(1.5) }
t("sym5")                 { warm { |i| o.hl_sym5(i) } }
t("str keys")             { warm { |i| o.hl_str(i) } }
t("str keys frozen, copies") { h = o.hl_str(1); k = h.keys; [k.map(&:frozen?), k[0].equal?(o.hl_str(1).keys[0])] }
t("mixed keys")           { warm { |i| o.hl_mixed(i) } }
t("mixed: value mutable") { h = o.hl_mixed(1); h[:s] << "w"; [h[:s], o.hl_mixed(1)[:s]] }
t("float key")            { warm { |i| o.hl_float(i) } }
t("nested")               { warm { |i| o.hl_nested(i) } }
t("nested: fresh inner")  { a = o.hl_nested(1); b = o.hl_nested(1); a[:a].equal?(b[:a]) }
t("ivar")                 { warm { |i| o.hl_ivar(i) }; [o.hl_ivar(3), o.instance_variable_get(:@h)] }
t("memo")                 { a = warm { o.hl_memo }; a[:k] = 1; [o.hl_memo, a.equal?(o.hl_memo)] }
t("default")              { warm { |i| o.hl_default(i) } }
t("default: fresh each call") { a = o.hl_default(1); b = o.hl_default(2); [a, b, a.equal?(b)] }
t("default: given")       { h = {z: 0}; o.hl_default(5, h); h }
t("bignum value")         { warm { |i| o.hl_bignum(i) }; o.hl_bignum(100_000) }
t("float value")          { warm { |i| o.hl_float_val(i) } }
t("after an effect")      { p2 = HL.new; warm { |i| p2.hl_after_effect(i) }; r = p2.hl_after_effect(2**62); [r, p2.n] }
t("size")                 { warm { |i| o.hl_size(i) } }
t("recurse")              { warm(50) { |i| o.hl_recurse(i % 5) }; h = o.hl_recurse(2000); d = 0; (d += 1; h = h[:d]) while h[:d]; d }
t("compare_by_identity off") { o.hl_str(1).compare_by_identity? }
t("default proc")         { [o.hl_sym1(1).default, o.hl_sym1(1)[:zz]] }
t("class")                { o.hl_empty.class }

class HL
  remove_method :hl_sym1
  def hl_sym1(x); {b: x}; end
end
t("redefined")            { o.hl_sym1(9) }

o.freeze
t("frozen self, ivar store") { o.hl_ivar(1) }
