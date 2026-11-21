# Differential matrix for methods whose `while'/`until' loop has a `break', `next' or `redo' and that run without a
# scope (a light frame, MethodDefinition.IsScopeFree), and for a plain `begin ... end' in such a method. Run it under
# CRuby and under ./ir.sh with the shape on and off, and diff:
#
#   ruby Util/loop-light-matrix.rb > mri.txt
#   ./ir.sh Util/loop-light-matrix.rb > on.txt
#   IR_LIGHT_BREAK=0 ./ir.sh Util/loop-light-matrix.rb > off.txt
#   diff mri.txt on.txt && diff mri.txt off.txt
#
# and with IR_LIGHT_OSR=0 (a light frame's OSR site given a scope made on demand, not the declaring scope; section 9),
# IR_LIGHT_FRAMES=0 and -X:NoJIT -X:NoOSR.
#
# A break, next or redo in a loop of the method's own body is a jump to one of the loop's labels, with the break's value
# stored in the loop's result; none of them reads the scope. The probes cover each loop form, the loop's value, nested
# loops, jumps under conditions, blocks around and inside the loop, callees that make the frame's scope (binding,
# local_variables, block_given?, a $~-setting call, eval), ensure and rescue in a callee, a loop long enough for OSR,
# register locals and a large body, threads, recursion a million deep, and a TracePoint for :line; and `begin ... end'
# in its forms (IR_LIGHT_BEGIN=0 keeps those out). Section 10 has such loops in methods the method JIT specializes.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/loop-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

N = 3000

def t(label, n = N)
  print label.ljust(52), ' => '
  seen = []
  begin
    n.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 70]}"
  end
end

# ---------------------------------------------------------------- 1. loop forms

def w_break(n)
  i = 0
  while true
    break if i == n
    i += 1
  end
  i
end

def w_break_value(n)
  i = 0
  x = while i < 100
    break i * 10 if i == n
    i += 1
  end
  [i, x]
end

def u_break(n)
  i = 0
  r = until i > 50
    break :early if i == n
    i += 1
  end
  [i, r]
end

def post_break(n)
  i = 0
  r = begin
    i += 1
    break [:post, i] if i >= n
  end while i < 5
  [i, r]
end

def post_until(n)
  i = 0
  begin
    i += 1
    break if i == n
  end until i > 3
  i
end

def w_break_multi(n)
  r = while true
    break n, n + 1
  end
  r
end

def w_break_splat(a)
  while true
    break *a
  end
end

def w_break_nil
  x = while true
    break
  end
  x.inspect
end

def w_value_unbroken(n)
  i = 0
  x = while i < n
    i += 1
  end
  [i, x]
end

t("while break")                 { |i| w_break(i % 5) }
t("while break value")           { |i| w_break_value(i % 3 + (i % 200 == 0 ? 200 : 0)) }
t("until break")                 { |i| u_break(i % 4 == 0 ? 99 : i % 4) }
t("begin end while break")       { |i| post_break(i % 8) }
t("begin end until break")       { |i| post_until(i % 6) }
t("break a, b")                  { |i| w_break_multi(i % 2) }
t("break *a")                    { |i| w_break_splat(i % 3 == 0 ? [] : i % 3 == 1 ? [1] : [1, 2]) }
t("bare break")                  { |i| w_break_nil }
t("while without break, value")  { |i| w_value_unbroken(i % 3) }

# ---------------------------------------------------------------- 2. next, redo, nesting, conditions

def w_next(n)
  i = 0
  s = 0
  while i < n
    i += 1
    next if i.odd?
    next 99 if i == 4
    s += i
  end
  s
end

def u_next(n)
  i = 0
  s = []
  until i >= n
    i += 1
    next unless i % 3 == 0
    s << i
  end
  s
end

def w_redo(n)
  i = 0
  tries = 0
  out = []
  while i < n
    i += 1
    tries += 1
    redo if tries.even? && i == 2
    out << [i, tries]
  end
  out
end

def nested_inner(n)
  out = []
  i = 0
  while i < 3
    i += 1
    j = 0
    while true
      j += 1
      break if j > n
      next if j == 1
      out << [i, j]
    end
  end
  out
end

def nested_outer(n)
  i = 0
  r = while i < 10
    i += 1
    j = 0
    while j < 10
      j += 1
      break if j == 2
    end
    break [i, j] if i == n
  end
  r
end

def nested_redo(n)
  out = []
  a = 0
  while a < 2
    a += 1
    b = 0
    c = 0
    while b < n
      b += 1
      c += 1
      redo if c == 1
    end
    out << [a, b, c]
  end
  out
end

def w_if_case_and(n)
  i = 0
  while i < 20
    i += 1
    if i > 15
      break :if
    elsif i == n
      case i
      when 3 then break :case3
      when 5 then next
      else
        i == 7 && (break :and)
        i == 8 || (break :or)
      end
    end
    x = i == 9 ? (break :ternary) : i
    i = x
  end
end

t("while next")                  { |i| w_next(i % 7) }
t("until next")                  { |i| u_next(i % 10) }
t("while redo")                  { |i| w_redo(i % 4) }
t("nested, break inner")         { |i| nested_inner(i % 4) }
t("nested, break outer")         { |i| nested_outer(i % 5) }
t("nested redo")                 { |i| nested_redo(i % 3) }
t("break in if/case/&&/||/?:")   { |i| w_if_case_and(i % 12) }

# ---------------------------------------------------------------- 3. blocks around and inside the loop

def while_in_block(n)
  [1, 2].map do |k|
    i = 0
    while true
      i += 1
      break i * k if i >= n
    end
  end
end

def block_in_while(n)
  i = 0
  r = while i < 5
    i += 1
    v = [1, 2, 3, 4].each { |x| break x * 100 if x == n }
    break v if v.is_a?(Integer)
  end
  [i, r]
end

def block_in_while_next(n)
  i = 0
  s = 0
  while i < 4
    i += 1
    s += [1, 2, 3].map { |x| next 0 if x == n; x }.sum
  end
  s
end

def yield_in_while(n)
  i = 0
  while i < 10
    i += 1
    break i if yield(i) == n
  end
end

def proc_break_through(n)
  i = 0
  while i < 5
    i += 1
    [1].each { break } if i == n
  end
  i
end

t("while in a block")            { |i| while_in_block(i % 3 + 1) }
t("block in while, break block") { |i| block_in_while(i % 6) }
t("block in while, next block")  { |i| block_in_while_next(i % 4) }
t("yield in while")              { |i| yield_in_while(i % 4) { |x| x * 2 } }
t("yield in while, block breaks"){ |i| [yield_in_while(3) { |x| break :out if x == 2 + i % 2; x }] }
t("break of a block in while")   { |i| proc_break_through(i % 6) }

# ---------------------------------------------------------------- 4. callees that make the frame's scope

def with_binding(n)
  i = 0
  while true
    i += 1
    b = binding
    break b.local_variable_get(:i) * 10 if i == n
  end
end

def with_local_variables(n)
  i = 0
  while i < 5
    i += 1
    lv = local_variables if i == n
    break if lv
  end
  [i, lv]
end

def with_block_given(n)
  i = 0
  while i < 5
    i += 1
    break block_given? if i == n
  end
end

def with_match(s, r)
  i = 0
  while i < s.size
    m = r.match(s, i)
    break m && m[0] if m
    i += 1
  end
end

def with_eval_break(n)
  i = 0
  r = while i < 5
    i += 1
    break eval("i * 3") if i == n
  end
  [i, r]
end

def with_eval_next(n)
  s = 0
  i = 0
  while i < 5
    i += 1
    next eval("s += 100") if i == n
    s += i
  end
  s
end

t("binding then break")          { |i| with_binding(i % 3 + 1) }
t("local_variables then break")  { |i| with_local_variables(i % 7) }
t("block_given? then break")     { |i| i.odd? ? with_block_given(i % 6) { } : with_block_given(i % 6) }
t("$~-setting call then break")  { |i| with_match(["xaby", "zzz", "b"][i % 3], /a?b/) }
t("eval then break")         { |i| with_eval_break(i % 7) }
t("eval then next")          { |i| with_eval_next(i % 6) }

# ---------------------------------------------------------------- 5. ensure and rescue in a callee

$log = []
def ens(x)
  x
ensure
  $log << x if x == 3
end

def raiser(x)
  raise ArgumentError, "bad #{x}" if x == 2
  x
rescue ArgumentError => e
  e.message
end

def raiser_through(x)
  raise ArgumentError, "out #{x}" if x == 4
  x
end

def with_ensure(n)
  i = 0
  while true
    i += 1
    break ens(i) if i == n
    ens(i)
  end
end

def with_rescue(n)
  i = 0
  out = nil
  while i < 5
    i += 1
    out = raiser(i)
    break if i == n
  end
  [i, out]
end

def with_raise_through(n)
  i = 0
  while i < 5
    i += 1
    raiser_through(i)
    break :done if i == n
  end
end

t("ensure in a callee")          { |i| with_ensure(i % 5 + 1) }
puts "ensure log: #{$log.size}"
t("rescue in a callee")          { |i| with_rescue(i % 6) }
t("raise out of the loop")       { |i| begin; with_raise_through(i % 6); rescue ArgumentError => e; e.message; end }

# ---------------------------------------------------------------- 6. long loops (OSR), locals, large bodies

def long_loop(n)
  i = 0
  s = 0
  while true
    s += i
    i += 1
    break if i == n
  end
  [i, s]
end

def long_loop_next(n)
  i = 0
  s = 0
  while i < n
    i += 1
    next if i % 3 == 0
    s += i
  end
  s
end

def locals_after(n)
  a = 1
  b = nil
  c = nil
  i = 0
  while i < 10
    i += 1
    b = i * 2
    c = b + a
    break if i == n
  end
  [a, b, c, i]
end

def small(n)
  while true
    break n
  end
end

def big(n)
  r = []
  i = 0
  while i < 3
    i += 1
    r << n.to_s << n.succ << n.pred << n.abs << n.even? << n.odd? << n.zero? << n.to_f << n.inspect << n.hash.class
    r << n.size << n.bit_length << n.ceil << n.floor << n.round << n.truncate << n.to_i << n.to_r.class << n.chr.size
    r << n.digits.size << n.class << n.frozen? << n.integer? << n.finite? << n.infinite? << n.positive? << n.negative?
    r << n.nonzero? << n.ord << n.numerator << n.denominator << (n + 1) << (n - 1) << (n * 2) << (n % 3) << (n / 2)
    break if i == n
  end
  r.size
end

t("long loop, break at the end", 300) { |i| long_loop(5000 + i % 3) }
t("long loop, next", 300)        { |i| long_loop_next(5000 + i % 3) }
t("locals written before break") { |i| locals_after(i % 12) }
t("small body")                  { |i| small(i % 3) }
t("more than 40 calls")          { |i| big(i % 4) }

# ---------------------------------------------------------------- 7. threads, depth, tracing

t("threads") do |i|
  next 0 unless i % 300 == 0
  4.times.map { |k| Thread.new { s = 0; 2000.times { |j| s += w_break(j % 7) + w_next(j % 9) + nested_outer(j % 5 + 1)[0] }; s + k } }.map(&:value)
end

def deep(n)
  while true
    break if n > 0
    return 0
  end
  deep(n - 1)
end
puts "recursion through a loop: " + (begin; deep(1_000_000).to_s; rescue SystemStackError; "SystemStackError"; end)
puts "recursion through a loop again: " + (begin; deep(1000).to_s; rescue SystemStackError; "SystemStackError"; end)

TP_LINE = __LINE__
lines = []
tp = TracePoint.new(:line) { |x| lines << x.lineno if x.path == __FILE__ && x.lineno < TP_LINE }
tp.enable { w_break(2); w_next(4); nested_outer(1) }
p lines
t("after the TracePoint")        { |i| [w_break(i % 3), w_next(i % 5)] }

# ---------------------------------------------------------------- 8. begin ... end

def plain_begin(n)
  x = begin
    y = n * 2
    y + 1
  end
  [x, y]
end

def begin_in_while(n)
  i = 0
  while true
    begin
      i += 1
      break i if i >= n
    end
  end
end

def begin_value(n)
  (begin; n; end) + begin
    n + 1
  end
end

def begin_empty = begin; end

def begin_block(n)
  r = begin
    [1, 2, 3].map { |x| x * n }
  end
  r.sum
end

def begin_arg(n) = [begin; n * 3; end, begin n end]

def begin_until_next(n)
  i = 0
  s = 0
  begin
    i += 1
    next if i == n
    s += i
  end until i >= 5
  s
end

def begin_binding(n)
  x = begin
    y = n
    binding.local_variable_get(:y) + 1
  end
  [x, y]
end

t("plain begin")                 { |i| plain_begin(i % 3) }
t("empty begin")                 { |i| begin_empty }
t("begin with a block")          { |i| begin_block(i % 3) }
t("begin as arguments")          { |i| begin_arg(i % 2) }
t("begin end until, next")       { |i| begin_until_next(i % 6) }
t("binding in begin")            { |i| begin_binding(i % 2) }
t("begin in while")              { |i| begin_in_while(i % 4) }
t("begin value")                 { |i| begin_value(i % 2) }

# ---------------------------------------------------------------- 10. the method JIT's typed body
# The methods of this section are ones the method JIT specializes (JitCompiler.EmitWhile): a break or next in a loop of
# the typed body is a jump to the loop's labels, a break's value stored in the loop's result. Each is hot well before the
# probe crosses an edge: an Integer accumulator that leaves Int64 (a deopt after the loop has run), a Float loop, a
# callee redefined part way through. IR_JIT_VERBOSE=1 says `[jit] specialized' for each.

def j_int_break(n)
  i = 0
  s = 0
  while true
    break if i >= n
    s += i
    i += 1
  end
  s
end

def j_float_break(x)
  y = 0.0
  while y < 100.0
    y += x
    break if y > 10.0
  end
  y
end

def j_break_value(n)
  i = 0
  r = while i < 10
    break i * 2 if i == n
    i += 1
  end
  r
end

def j_break_loop_value(n)
  i = 0
  while true
    i += 1
    break i if i >= n
  end
end

def j_break_float_value(n)
  i = 0
  v = while i < n
    i += 1
    break 1.5 if i == 3
    break if i == 5
  end
  v
end

def j_next(n)
  i = 0
  s = 0
  while i < n
    i += 1
    next if i % 3 == 0
    s += i
  end
  s
end

def j_until_next(n)
  i = 0
  s = 0
  until i >= n
    i += 1
    next unless i % 2 == 1
    s += i
  end
  s
end

def j_nested(n)
  i = 0
  s = 0
  while i < n
    j = 0
    while true
      break if j >= i
      j += 1
      next if j == 2
      s += j
    end
    i += 1
    break if s > 50
  end
  s
end

def j_nested_value(n)
  i = 0
  s = 0
  while i < n
    v = while true
      break i * 3 if i % 2 == 1
      break
    end
    s += v if v
    i += 1
  end
  s
end

def j_overflow(n)
  x = 1
  i = 0
  while true
    x *= n
    i += 1
    break if i >= 4
  end
  x
end

def j_float_next(x)
  y = 0.0
  k = 0
  while k < 8
    k += 1
    next if k == 4
    y += x
  end
  y
end

def j_callee(x)
  x + 1
end

def j_calls(n)
  i = 0
  s = 0
  while true
    s += j_callee(i)
    i += 1
    break if i >= n
  end
  s
end

t("jit: Integer loop, break")            { |i| j_int_break(i % 7) }
t("jit: Float loop, break")              { |i| j_float_break(0.75 + (i % 5) * 1.5) }
t("jit: break with a value")             { |i| j_break_value(i % 12) }
t("jit: break as the method's value")    { |i| j_break_loop_value(i % 6 + 1) }
t("jit: break 1.5, bare break, nil")     { |i| j_break_float_value(i % 7) }
t("jit: next")                           { |i| j_next(i % 9) }
t("jit: until, next")                    { |i| j_until_next(i % 9) }
t("jit: nested, break and next")         { |i| j_nested(i % 11) }
t("jit: nested loop's break value")      { |i| j_nested_value(i % 6) }
t("jit: break after a deopt point")      { |i| j_overflow(i < 2500 ? (i % 5) + 2 : 3_000_000_000) }
t("jit: Float loop, next")               { |i| j_float_next(0.25 * (i % 4)) }
t("jit: callee redefined")               { |i| (eval "def j_callee(x); x + 2; end") if i == 1500; j_calls(i % 5 + 1) }

# ---------------------------------------------------------------- 9. what an OSR'd loop of a light frame reads from its scope
# A light frame hands its loop's OSR site the declaring scope: its constants resolve there, and its refinements are none.

class Konst
  LIMIT = 4000
  def count
    i = 0
    while true
      i += 1
      break if i >= LIMIT
    end
    i
  end
end

t("constant in an OSR'd loop", 300)  { |i| Konst.new.count }
Konst.send(:remove_const, :LIMIT)
Konst.const_set(:LIMIT, 4100)
t("the constant redefined", 300)     { |i| Konst.new.count }

module RefInt
  refine Integer do
    def +(o) = 42
  end
end

def before_using(n)
  i = 0
  s = 0
  while true
    s += i
    i += 1
    break if i >= n
  end
  s
end

# (last: `using' reaches the code after it in this file)
using RefInt
def after_using(n)
  i = 0
  s = 0
  while true
    s = s + i
    i = i.succ
    break if i >= n
  end
  s
end
t("defined before a using", 300)     { |i| before_using(4500) }
t("defined after a using", 300)      { |i| after_using(4500) }
