# Differential matrix for multiple assignment in methods the method JIT compiles (-X:JIT,
# JitCompiler.EmitParallelAssignment). Run under CRuby and under ./ir.sh in every mode, and diff:
#
#   ruby Util/jit-masgn-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-masgn-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-masgn-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Every method is called past the JIT's thresholds, then probed. Which ones are specialized:
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-masgn-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]' | grep ma_
# The traps: every right-hand value is evaluated before any target is written (a swap), left to
# right; a missing value is nil and an extra one is evaluated and dropped; a local keeps its
# type (Int to Bignum after the swap, a Float, an object); an instance variable write deopts on
# a frozen self, after which the generic body must not repeat what came before; the value of
# the expression is an Array of the right-hand values where it is used.

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

class MA
  def initialize; @a = 0; @b = 0; @n = 0; @log = []; end
  attr_reader :a, :b, :n, :log

  def ma_swap(x, y)
    x, y = y, x
    x - y
  end
  def ma_three(x)
    a, b, c = x, x + 1, x * 2
    a + b + c
  end
  def ma_rotate(x, y, z)
    x, y, z = y, z, x
    x * 100 + y * 10 + z
  end
  def ma_ivars(x, y)
    @a, @b = x, y
    @a + @b
  end
  def ma_ivar_swap
    @a, @b = @b, @a
    @a
  end
  def ma_mixed(x)
    v, @a = x + 1, x - 1
    v + @a
  end
  def ma_missing(x)
    a, b, c = x, x
    c.nil? ? a + b : -1
  end
  def ma_extra(x)
    a, b = x, x + 1, x + 2
    a + b
  end
  def ma_value(x, y)
    a, b = x, y
  end
  def ma_value_used(x, y)
    r = (a, b = x, y)
    r.size + a + b
  end
  def ma_float(x)
    a, b = x * 1.5, x
    a + b
  end
  def ma_objects(x)
    a, b = "s#{x}", [x]
    a.size + b.size
  end
  def ma_overflow(x)
    a, b = x * x, x
    a + b
  end
  def ma_effect(x)
    @n += 1
    a, b = x + 1, x * 3
    a + b
  end
  def ma_frozen(x)
    @a, @b = x, x + 1
    x
  end
  def note(v); @log << v; v; end
  def ma_order(x)
    a, b = note(x), note(x + 1)
    a + b
  end
  def ma_in_loop(n)
    a = 0
    b = 1
    i = 0
    while i < n
      a, b = b, a + b
      i += 1
    end
    a
  end
end

o = MA.new
t("swap")                  { warm { |i| o.ma_swap(i, 1) } }
t("swap, Floats")          { o.ma_swap(1.5, 0.25) }
t("swap, Strings")         { begin; o.ma_swap("a", "b"); rescue NoMethodError => e; e.class; end }
t("three")                 { warm { |i| o.ma_three(i) } }
t("three, a Bignum")       { o.ma_three(2**62) }
t("rotate")                { warm { |i| o.ma_rotate(1, 2, i % 10) } }
t("ivars")                 { warm { |i| o.ma_ivars(i, 2) }; [o.ma_ivars(5, 6), o.a, o.b] }
t("ivar swap")             { o = MA.new; o.ma_ivars(1, 2); warm(301) { o.ma_ivar_swap }; [o.a, o.b] }
t("mixed")                 { warm { |i| o.ma_mixed(i) }; [o.ma_mixed(10), o.a] }
t("missing")               { warm { |i| o.ma_missing(i) } }
t("extra")                 { warm { |i| o.ma_extra(i) } }
t("value")                 { warm { |i| o.ma_value(i, 2) } }
t("value, objects")        { o.ma_value("a", nil) }
t("value used")            { warm { |i| o.ma_value_used(i, 1) } }
t("float")                 { warm { |i| o.ma_float(i) } }
t("objects")               { warm { |i| o.ma_objects(i) } }
t("overflow")              { warm { |i| o.ma_overflow(i) }; o.ma_overflow(2**40) }
t("effect then a deopt")   { p2 = MA.new; warm { |i| p2.ma_effect(i) }; [p2.ma_effect(2**62), p2.n] }
t("order")                 { p2 = MA.new; warm(100) { |i| p2.ma_order(i) }; [p2.ma_order(7), p2.log.last(4)] }
t("in a loop")             { warm(50) { |i| o.ma_in_loop(i % 20) }; [o.ma_in_loop(10), o.ma_in_loop(100)] }
t("frozen")                { p2 = MA.new; warm { |i| p2.ma_frozen(i) }; p2.freeze; [(p2.ma_frozen(1) rescue $!.class), p2.a, p2.b] }

class MA
  remove_method :ma_swap
  def ma_swap(x, y)
    x, y = y, x
    x + y
  end
end
t("redefined")             { o.ma_swap(3, 4) }
