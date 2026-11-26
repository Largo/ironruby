# Differential matrix for a statement guarded by a constant (`trace(x) if TRACING', `log(x) unless QUIET'): the
# method JIT and the loop tiers fold the constant (JitCompiler.ResolveConstant), and with IR_JIT_DEADCONST (on unless
# =0) leave out the arm the constant's value makes dead, under the constant's revalidation (JitStub.
# RevalidateConstants at a method's entry, JitLoopGuard at a loop's safe point). Each row warms a method of its own
# past the JIT's thresholds with the guard dead, changes the constant - reassigned, removed, set to nil or 0, shadowed
# by a nearer one - and calls again; the threaded rows change it while a compiled loop runs, which must see it at its
# next iteration as CRuby's next read does. Run under CRuby and under ./ir.sh - with the JIT on, off, without
# JIT calls and with the switch off - and diff:
#
#   ruby Util/deadconst-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/deadconst-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/deadconst-matrix.rb > /tmp/nojit.txt
#   ./ir.sh -X:NoJITCalls Util/deadconst-matrix.rb > /tmp/nocalls.txt
#   IR_JIT_DEADCONST=0 ./ir.sh Util/deadconst-matrix.rb > /tmp/off.txt
#   for f in jit nojit nocalls off; do diff /tmp/mri.txt /tmp/$f.txt; done
#
# What the JIT did goes to stderr: IR_JIT_VERBOSE=1 IR_OSR_VERBOSE=1 ./ir.sh Util/deadconst-matrix.rb 2>&1 >/dev/null

$stdout.sync = true

WARM = 3000

# Reassigns a constant without CRuby's "already initialized constant" warning: the warning is given only when
# $VERBOSE is not nil.
def reassign(mod, name, value)
  verbose, $VERBOSE = $VERBOSE, nil
  mod.const_set(name, value)
ensure
  $VERBOSE = verbose
end

class Probe
  attr_reader :notes
  def initialize; @notes = []; end
  def note(x); @notes << x; x; end
end

def answer
  yield
rescue NameError => e
  [e.class, e.message]
end

# warm with the guard dead, change, call, warm again, call again; print each call's answer and the notes taken
def around(label, o, change)
  v = nil
  WARM.times { |i| v = answer { yield o, i } }
  out = [v, o.notes.size]
  change.call
  out << answer { yield o, 1 } << o.notes.size
  WARM.times { |i| v = answer { yield o, i } }
  out << v << o.notes.size
  puts "#{label}: #{out.inspect}"
end

# ---------------------------------------------------------------- 1. method bodies, between calls

DF1 = false
class P1 < Probe; def m(x); note(x) if DF1; x + 1; end; end
around("1. if FALSE, reassigned true", P1.new, -> { reassign(Object, :DF1, true) }) { |o, i| o.m(i) }

DF2 = false
class P2 < Probe; def m(x); note(x) if DF2; end; end
around("2. if FALSE as the value", P2.new, -> { reassign(Object, :DF2, true) }) { |o, i| o.m(i) }

DT3 = true
class P3 < Probe; def m(x); note(x) unless DT3; x * 2; end; end
around("3. unless TRUE, reassigned false", P3.new, -> { reassign(Object, :DT3, false) }) { |o, i| o.m(i) }

DF4 = false
class P4 < Probe
  def m(x)
    if DF4
      note(x)
      :on
    else
      x + 2
    end
  end
end
around("4. if/else, reassigned true", P4.new, -> { reassign(Object, :DF4, true) }) { |o, i| o.m(i) }

DT5 = true
class P5 < Probe
  def m(x)
    if DT5
      x - 1
    else
      note(x)
    end
  end
end
around("5. if TRUE with a dead else, reassigned false", P5.new, -> { reassign(Object, :DT5, false) }) { |o, i| o.m(i) }

module Cfg6; DEBUG = false; end
class P6 < Probe; def m(x); note(x) if Cfg6::DEBUG; x + 6; end; end
around("6. qualified, reassigned true", P6.new, -> { reassign(Cfg6, :DEBUG, true) }) { |o, i| o.m(i) }

NIL7 = nil
class P7 < Probe; def m(x); note(x) if NIL7; x + 7; end; end
around("7. nil, set to 0 (truthy)", P7.new, -> { reassign(Object, :NIL7, 0) }) { |o, i| o.m(i) }

DF8 = false
class P8 < Probe; def m(x); note(x) if DF8; x + 8; end; end
around("8. removed", P8.new, -> { Object.send(:remove_const, :DF8) }) { |o, i| o.m(i) }

DF9 = false
class P9 < Probe; def m(x); note(x) if DF9; x + 9; end; end
around("9. shadowed by a nearer constant", P9.new, -> { P9.const_set(:DF9, true) }) { |o, i| o.m(i) }

DF10 = false
class P10Base < Probe; end
class P10 < P10Base; def m(x); note(x) if DF10; x + 10; end; end
around("10. shadowed through a superclass", P10.new, -> { P10Base.const_set(:DF10, :yes) }) { |o, i| o.m(i) }

DF11 = false
class P11 < Probe; def m(x); note(x) if DF11; x + 11; end; end
around("11. set to an equal false again", P11.new, -> { reassign(Object, :DF11, false) }) { |o, i| o.m(i) }

DF12 = false
class P12 < Probe; def m(x); y = (note(x) if DF12); y.inspect; end; end
around("12. the value assigned", P12.new, -> { reassign(Object, :DF12, 12) }) { |o, i| o.m(i) }

DF13 = false
class P13 < Probe
  def m(x); helper(x) + 1; end
  def helper(x); note(x) if DF13; x; end
end
around("13. in a callee", P13.new, -> { reassign(Object, :DF13, true) }) { |o, i| o.m(i) }

DF14 = false
class P14 < Probe
  def m(n)
    i = 0
    s = 0
    while i < n
      note(i) if DF14
      s += i
      i += 1
    end
    s
  end
end
around("14. in a method's while", P14.new, -> { reassign(Object, :DF14, true) }) { |o, i| o.m(i % 7) }

DF15 = false
class P15 < Probe
  def m(n)
    s = 0
    n.times { |i| note(i) if DF15; s += i }
    s
  end
end
around("15. in a times block", P15.new, -> { reassign(Object, :DF15, true) }) { |o, i| o.m(i % 7) }

DT16 = true
class P16 < Probe; def m(x); note(x) if DT16; x + 16; end; end
around("16. if TRUE (live), reassigned false", P16.new, -> { reassign(Object, :DT16, false) }) { |o, i| o.m(i) }

# ---------------------------------------------------------------- 2. while a compiled loop runs

# Another thread reassigns the constant while the loop spins; the loop must see it at an iteration after that, as
# CRuby's next read does. The bound is reached only by a loop that never sees it. LIMIT_I is an Integer the typed
# bodies, warmed with small ones, take as an int (a larger one goes to the generic body and the loop tier).
LIMIT = 3_000_000_000
LIMIT_I = 2_000_000_000

DF20 = false
class P20 < Probe
  def spin(n)
    i = 0
    while i < n
      return :seen if DF20
      i += 1
    end
    :limit
  end
end
o20 = P20.new
w20 = nil
WARM.times { w20 = o20.spin(5) }
t20 = Thread.new { sleep 0.1; reassign(Object, :DF20, true) }
r20 = o20.spin(LIMIT_I)
t20.join
puts "20. a method's while, reassigned by another thread: #{[w20, r20].inspect}"

DF21 = false
i21 = 0
t21 = Thread.new { sleep 0.1; reassign(Object, :DF21, true) }
r21 = :limit
while i21 < LIMIT
  if DF21
    r21 = :seen
    break
  end
  i21 += 1
end
t21.join
puts "21. a top-level while, reassigned by another thread: #{r21.inspect}"

DF22 = false
class P22 < Probe
  def spin(n)
    i = 0
    s = 0
    while i < n
      note(i) if DF22
      break unless @notes.empty?
      s += 1
      i += 1
    end
    @notes.empty? ? :limit : :seen
  end
end
o22 = P22.new
w22 = nil
WARM.times { w22 = o22.spin(5) }
t22 = Thread.new { sleep 0.1; reassign(Object, :DF22, true) }
r22 = o22.spin(LIMIT_I)
t22.join
puts "22. a dropped call in a method's while, reassigned by another thread: #{[w22, r22].inspect}"

DF23 = false
class P23 < Probe
  def spin(n)
    s = 0
    n.times do |i|
      break if DF23
      s += 1
    end
    s < n ? :seen : :limit
  end
end
o23 = P23.new
w23 = nil
WARM.times { w23 = o23.spin(5) }
t23 = Thread.new { sleep 0.1; reassign(Object, :DF23, true) }
r23 = o23.spin(LIMIT_I)
t23.join
puts "23. a times block, reassigned by another thread: #{[w23, r23].inspect}"

DF24 = false
class P24 < Probe
  def m(x); note(x) if DF24; x; end
end
o24 = P24.new
WARM.times { |i| o24.m(i) }
t24 = Thread.new { sleep 0.05; reassign(Object, :DF24, true) }
k24 = 0
k24 += 1 while o24.notes.empty? && (o24.m(k24) || true) && k24 < 200_000_000
t24.join
puts "24. a method called in a loop, reassigned by another thread: #{o24.notes.empty? ? :limit : :seen}"

# A loop that has nothing else that may deopt (a Float counter does not overflow): the safe point must still see the
# reassignment, which a body that may not deopt goes on past (JitRuntime.MethodLoopSafePointSlow). The guarded
# statement is a call, which keeps the method from the typed body where nothing is left out under a constant. (With
# `return :seen if DF25' the unchanged build compiles it and answers :limit, as with the switch off: a folded
# constant in such a loop, Util/perf/LEDGER.md gap 29.)
DF25 = false
class P25 < Probe
  def spin(n)
    x = 0.0
    while x < n
      return note(:seen) if DF25
      x += 1.0
    end
    :limit
  end
end
o25 = P25.new
w25 = nil
WARM.times { w25 = o25.spin(5.0) }
t25 = Thread.new { sleep 0.1; reassign(Object, :DF25, true) }
r25 = o25.spin(2.0e9)
t25.join
puts "25. a method's while that cannot deopt otherwise, reassigned by another thread: #{[w25, r25].inspect}"

puts "end"
