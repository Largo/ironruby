# Differential matrix for `begin`/`rescue`/`else`/`ensure` in methods the method JIT could
# take (-X:JIT, Src/Ruby/Runtime/Jit/; gap 22 in Util/perf/LEDGER.md). Run under CRuby and
# under ./ir.sh in every mode, and diff:
#
#   ruby Util/jit-rescue-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-rescue-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-rescue-matrix.rb > /tmp/nojit.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/jit-rescue-matrix.rb > /tmp/nolight.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt && diff /tmp/mri.txt /tmp/nolight.txt
#
# Every method is called past the JIT's thresholds with the types it would be specialized
# for and raises on some of those calls, so that a typed body which takes the handler is
# entered both ways. The traps are the ones a deopt to the generic body falls into: an
# effect before the raise must happen once (the generic body runs from the top), the
# class list of a rescue is evaluated after the inner ensure clauses have run, `$!` is
# set in the handler and restored after it, `retry` re-runs the protected part only, an
# `ensure` runs once whether the body returns, raises, throws or is timed out.

def t(label)
  print label.ljust(56), ' => '
  begin
    v = yield
    puts v.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message}"
  end
end

HOT = 300

def warm(n = HOT)
  i = 0
  acc = 0
  while i < n
    v = yield i
    acc += v if v.is_a?(Integer)
    i += 1
  end
  acc
end

class P
  def initialize; @n = 0; @e = 0; @log = []; end
  attr_reader :n, :e, :log

  # 1. a pure protected part, never raising
  def pure(x)
    begin
      x + 1
    rescue StandardError
      0
    end
  end

  # 2. a pure protected part that raises on some calls (ZeroDivisionError)
  def div(x)
    begin
      100 / x
    rescue ZeroDivisionError
      -1
    end
  end

  # 3. an effect before the raise: the write must happen once per call
  def eff(x)
    @n += 1
    begin
      @n += 1
      100 / x
    rescue ZeroDivisionError
      @n += 10
      -1
    end
  end

  # 4. retry: the protected part runs again, what came before it does not
  def ret(x)
    @n += 1
    tries = 0
    begin
      tries += 1
      100 / (x - tries + 2)
    rescue ZeroDivisionError
      retry if tries < 3
      -tries
    end
  end

  def klasses
    @log << :klasses
    ZeroDivisionError
  end

  # 5. a rescue class list that is a method call: evaluated only when something is raised
  def klist(x)
    begin
      100 / x
    rescue klasses
      -1
    end
  end

  # 6. ensure, with a return in the body
  def ens_ret(x)
    begin
      return x * 2 if x.odd?
      x
    ensure
      @e += 1
    end
  end

  # 7. a return in the ensure clause swallows the exception
  def ens_swallow(x)
    begin
      100 / x
    ensure
      return -7 if x == 0
    end
  end

  # 8. $! in the handler and after it
  def bang(x)
    begin
      100 / x
    rescue ZeroDivisionError
      $!.class.name.size
    end
  end

  def callee(x)
    @log << x if x % 50 == 0
    raise ArgumentError, "big" if x > 250
    x
  end

  # 9. an exception from a callee that made an effect before raising
  def from_callee(x)
    begin
      callee(x) + 1
    rescue ArgumentError
      -1
    end
  end

  # 10. a rescue modifier
  def modifier(x)
    (100 / x) rescue -2
  end

  # 11. else and ensure together
  def full(x)
    begin
      v = 100 / x
    rescue ZeroDivisionError
      v = -1
    else
      @n += 1
    ensure
      @e += 1
    end
    v
  end

  # 12. the inner ensure runs before the outer rescue's class list is evaluated
  def order(x)
    begin
      begin
        100 / x
      ensure
        @log << :inner_ensure
      end
    rescue klasses
      @log << :rescued
      -1
    end
  end

  # 13. a raise in the protected part (a deopt in a typed body), with an ensure
  def raise_ens(x)
    begin
      raise ArgumentError, "x=#{x}" if x > 250
      x + 1
    ensure
      @e += 1
    end
  end

  # 14. a raise in the handler: its cause is the rescued exception
  def reraise(x)
    begin
      100 / x
    rescue ZeroDivisionError
      raise ArgumentError, "wrapped"
    end
  end

  def thrower(x)
    throw :out, x if x > 250
    x
  end

  # 15. throw through an ensure
  def thr(x)
    begin
      thrower(x)
    ensure
      @e += 1
    end
  end

  # 16. a rescue that does not match lets the exception through, after the ensure
  def nomatch(x)
    begin
      100 / x
    rescue ArgumentError
      -1
    ensure
      @e += 1
    end
  end

  # 17. a rescue with no class takes StandardError only
  def bare(x)
    begin
      x > 250 ? Integer("zz") : x
    rescue
      -3
    end
  end

  # 18. an ensure writing self: frozen self
  def ens_frozen(x)
    begin
      x + 1
    ensure
      @e = x
    end
  end

  # 19. a float body
  def fdiv(x)
    begin
      Integer === x ? 1.0 / x : x
    rescue TypeError
      -1.0
    end
  end
end

o = P.new
t("1. pure")                { warm { |i| o.pure(i) } }
t("1. pure, a Float")       { o.pure(1.5) }
t("1. pure, a String")      { o.pure("a") }
t("2. div, 0 every 7th")    { warm { |i| o.div(i % 7) } }
t("2. div after warm, 0")   { o.div(0) }
t("3. eff")                 { o = P.new; [warm { |i| o.eff(i % 5) }, o.n] }
t("4. retry")               { o = P.new; [warm { |i| o.ret(i % 4) }, o.n] }
t("5. class list a call")   { o = P.new; [warm { |i| o.klist(i % 9) }, o.log.size] }
t("5. class list, wrong")   { o.klist(nil) }
t("6. ensure, return")      { o = P.new; [warm { |i| o.ens_ret(i) }, o.e] }
t("7. ensure swallows")     { warm { |i| o.ens_swallow(i % 3) } }
t("8. $! in the handler")   { [warm { |i| o.bang(i % 3) }, $!] }
t("8. $! restored")         { begin; raise "outer"; rescue; o.bang(0); $!.message; end }
t("9. from a callee")       { o = P.new; [warm { |i| o.from_callee(i) }, o.log] }
t("10. modifier")           { warm { |i| o.modifier(i % 4) } }
t("11. else and ensure")    { o = P.new; [warm { |i| o.full(i % 6) }, o.n, o.e] }
t("12. order")              { o = P.new; warm { |i| o.order(i % 2 + 1) }; o.order(0); o.log.last(3) }
t("13. raise and ensure")   { o = P.new; r = warm(250) { |i| o.raise_ens(i) }; [r, o.e] }
t("13. raises")             { o.raise_ens(300) }
t("13. ensure ran")         { o.e }
t("14. cause")              { warm(100) { |i| o.reraise(i + 1) }; begin; o.reraise(0); rescue => e; [e.class, e.cause.class]; end }
t("15. throw through")      { o = P.new; [catch(:out) { warm { |i| o.thr(i) } }, o.e] }
t("16. no match")           { o = P.new; warm(100) { |i| o.nomatch(i + 1) }; o.nomatch(0) }
t("16. ensure ran")         { o.e }
t("17. bare rescue")        { warm { |i| o.bare(i) } }
t("17. NoMethodError")      { begin; o.bare(nil); rescue NoMethodError => e; e.class; end }
t("18. ensure, frozen")     { o = P.new; warm { |i| o.ens_frozen(i) }; o.freeze; begin; o.ens_frozen(5); rescue FrozenError => e; e.class; end }
t("18. value kept")         { o.e }
t("19. float")              { warm { |i| o.fdiv(i + 1) }; [o.fdiv(4), o.fdiv(0.5)] }

# 20. Timeout through a hot method with a rescue: Timeout's exception is not a StandardError
require 'timeout'
o = P.new
t("20. Timeout through rescue") do
  Timeout.timeout(0.2) { loop { o.pure(1); o.div(1); sleep 0.001 } }
end
t("20. still works")        { o.div(0) }

# 21. a method redefined after it turned hot
class P
  remove_method :pure
  def pure(x)
    begin
      x - 1
    rescue StandardError
      0
    end
  end
end
t("21. redefined")          { o.pure(10) }
t("22. $! at the end")      { $!.inspect }
