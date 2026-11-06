# Differential matrix for the numeric library calls the loop and method JIT compile inline
# (Src/Ruby/Runtime/Jit/JitCompiler.Numeric.cs: abs, ceil, floor, round, clamp, [a, b].min/max)
# and for Range#each and `next' in a block the iterators outline (Src/Ruby/Runtime/Osr/BlockLoopSite.cs).
# Run it under CRuby and under ./ir.sh with OSR and the JIT on and off, and diff:
#
#   ruby Util/osr-numeric-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/osr-numeric-matrix.rb > /tmp/osr.txt
#   ./ir.sh -X:NoOSR -X:NoJIT Util/osr-numeric-matrix.rb > /tmp/generic.txt
#   diff /tmp/mri.txt /tmp/osr.txt && diff /tmp/mri.txt /tmp/generic.txt
#
# All three must be identical. Every loop runs past the OSR threshold (2000 back edges) before it
# meets the value it probes, and every method is called past the JIT's threshold, so the compiled
# copy is what meets a NaN, a min past its max, an Integer where Floats were, a Float with no
# Integer value, a redefined <=>. To see what was compiled:
#
#   IR_OSR_VERBOSE=1 IR_JIT_VERBOSE=1 ./ir.sh Util/osr-numeric-matrix.rb 2>&1 >/dev/null | grep -a 'osr\]\|jit\]'

def t(label)
  print label.ljust(64), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    # the class only: CRuby words a comparison error of a literal's min/max (opt_newarray_send)
    # unlike its Array#min, and IronRuby has the one Array#min
    puts e.class
  end
end

N = 6000

# ---------------------------------------------------------------- 1. in a loop, over Arrays

def unary_loop(a, op)
  s = []
  i = 0
  while i < a.size
    v = a[i]
    s << (op == 0 ? v.abs : op == 1 ? v.ceil : op == 2 ? v.floor : v.round)
    i += 1
  end
  s
end

floats = Array.new(N) { |i| (i - 3000) * 0.75 }
ints = Array.new(N) { |i| (i - 3000) * 7 }
4.times { |op| t("float op #{op}") { r = unary_loop(floats, op); [r.sum, r[2999], r[3001], r.last] } }
4.times { |op| t("int op #{op}") { r = unary_loop(ints, op); [r.sum, r.first, r.last] } }
halves = Array.new(N) { |i| (i - 3000) + 0.5 }
t('round of halves, away from zero') { r = unary_loop(halves, 3); [r.sum, r[2999], r[3000], r[3001]] }
edge = floats.dup
edge[5000] = -0.0
t('abs of -0.0') { unary_loop(edge, 0)[5000] }
edge[5001] = Float::NAN
t('ceil of NaN') { unary_loop(edge, 1) }
edge[5001] = Float::INFINITY
t('floor of Infinity') { unary_loop(edge, 2) }
edge[5001] = 1e300
t('round of 1e300') { unary_loop(edge, 3)[5001] }
t('abs of 1e300') { unary_loop(edge, 0)[5001] }
big = ints.dup
big[5000] = -2**31
t('abs of -2**31') { unary_loop(big, 0)[5000] }
big[5000] = -2**63
t('abs of -2**63') { unary_loop(big, 0)[5000] }
big[5000] = 2**70
t('ceil of a Bignum') { unary_loop(big, 1)[5000] }

def clamp_loop(a, lo, hi)
  s = 0
  i = 0
  while i < a.size
    s += a[i].clamp(lo, hi)
    i += 1
  end
  s
end

t('Integer clamp') { clamp_loop(ints, -100, 300) }
t('Float clamp') { clamp_loop(floats, -10.5, 20.25) }
t('Float clamp, Integer bounds') { clamp_loop(floats, -10, 20) }
t('Integer clamp, Float bounds') { clamp_loop(ints, -10.5, 20.5) }
t('clamp, min past max') { clamp_loop(ints, 5, 4) }
t('clamp, NaN bound') { clamp_loop(floats, Float::NAN, 1.0) }
nan = floats.dup
nan[4000] = Float::NAN
t('clamp of NaN') { clamp_loop(nan, 0.0, 1.0) }
mixed = ints.dup
mixed[4500] = 2.5
t('clamp, a Float among Integers') { clamp_loop(mixed, 0, 10) }
neg0 = Array.new(N, -0.0)
t('clamp of -0.0 to 0.0..1.0') { r = 0; i = 0; while i < N; r = neg0[i].clamp(0.0, 1.0); i += 1; end; [r, 1.0 / r] }
t('clamp to 0.0..1.0 of 0.0 (min)') { r = 0; i = 0; while i < N; r = 0.0.clamp(-0.0, 1.0); i += 1; end; 1.0 / r }

def minmax_loop(a, b)
  s = 0
  t = 0
  i = 0
  while i < a.size
    s += [a[i], b].min
    t += [a[i], b, i].max
    i += 1
  end
  [s, t]
end

t('min/max of Integers') { minmax_loop(ints, 100) }
t('min/max of Floats') { minmax_loop(floats, 0.5).map { |v| v.round(6) } }
t('min/max, an Integer and Floats') { minmax_loop(floats, 1) }
mm = ints.dup
mm[4000] = nil
t('min/max with a nil') { minmax_loop(mm, 3) }
mm[4000] = 2**65
t('min/max with a Bignum') { minmax_loop(mm, 3) }
t('min/max with NaN') { minmax_loop(nan, 0.5) }
t('min of 0.0 and -0.0 keeps the first') { r = 0; i = 0; while i < N; r = [0.0, -0.0].min; i += 1; end; 1.0 / r }
t('max of -0.0 and 0.0 keeps the first') { r = 0; i = 0; while i < N; r = [-0.0, 0.0].max; i += 1; end; 1.0 / r }

# ---------------------------------------------------------------- 2. in methods the JIT compiles

def light(level, dist)
  lightnum = (level >> 4).clamp(0, 15)
  start = ((15 - lightnum) * 64) / 16
  z = (dist / 16.0).to_i.clamp(0, 127)
  (start - 10.0 / (z + 1)).floor.clamp(0, 31)
end
t('light, a JIT method') { (0...N).sum { |i| light(i % 256, i * 0.37) } }
t('light, distance NaN') { light(100, Float::NAN) }
t('light, distance Infinity') { light(100, Float::INFINITY) }

def span(a, b, c) = [a, b].max - [b, c].min + (a - c).abs
t('span, a JIT method') { (0...N).sum { |i| span(i, 3000, i % 97) } }
t('span, Floats') { (0...N).sum { |i| span(i * 0.5, 30.0, 2.25) } }
t('span, mixed') { span(1, 2.0, 3) }

# ---------------------------------------------------------------- 3. Range#each, and `next'

def range_sum(a, b, excl)
  s = 0
  (excl ? (a...b) : (a..b)).each do |x|
    s += x * 2
  end
  s
end
t('(a..b).each') { (0...200).sum { |k| range_sum(k, k + 100, false) } }
t('(a...b).each') { (0...200).sum { |k| range_sum(k, k + 100, true) } }
t('(a...a).each') { range_sum(5, 5, true) }
t('to Int32.MaxValue') { range_sum(2**31 - 40, 2**31 - 1, false) }
t('past Int32.MaxValue') { range_sum(2**31 - 40, 2**31 + 5, false) }
t('down from Int32.MinValue') { range_sum(-2**31, -2**31 + 30, true) }
t('overflowing the sum') { s = 0; (0..5000).each { |x| s += x * 2**50 }; s }

def range_next(a, b, cut)
  s = 0
  (a..b).each do |x|
    next if x % 3 == 0
    next unless x < cut
    s += x
  end
  s
end
t('next in (a..b).each') { (0...200).sum { |k| range_next(k, k + 300, k + 250) } }

def times_next(n)
  s = 0
  n.times do |i|
    next if i.odd?
    s += i
  end
  s
end
t('next in times') { (0...100).sum { |k| times_next(1000 + k) } }

def each_next(a)
  s = 0
  a.each do |v|
    next unless v
    s += v
  end
  s
end
nils = Array.new(N) { |i| i % 5 == 0 ? nil : i }
t('next in each') { 30.times.sum { each_next(nils) } }

def map_next(a)
  a.map do |v|
    next 0 unless v
    v * 2
  end
end
t('next with a value in map (not outlined)') { map_next(nils).sum }

def upto_next(a, b)
  s = 0
  a.upto(b) do |x|
    next if x == b - 1
    s += x
  end
  s
end
t('next in upto, deopt part way') { r = (0...50).sum { |k| upto_next(k, k + 500) }; r + upto_next(2**62 - 600, 2**62) }

def sprite(fb, col, x, y0, y1)
  (y0..y1).each do |y|
    ty = ((y - y0) * 64 / (y1 - y0 + 1)).to_i.clamp(0, 63)
    c = col[ty]
    next unless c
    fb[y * 8 + x] = c
  end
end
col = Array.new(64) { |i| i % 9 == 0 ? nil : i * 3 }
sfb = Array.new(8 * 400, 0)
t('a sprite column') { 300.times { |k| sprite(sfb, col, k & 7, k % 50, 350 - k % 40) }; sfb.sum }

# ---------------------------------------------------------------- 4. redefined

class Integer
  alias_method :__matrix_cmp, :<=>
  def <=>(o) = -__matrix_cmp(o)
end
t('Integer#<=> reversed: clamp') { clamp_loop(ints, -100, 300) }
t('Integer#<=> reversed: min/max') { minmax_loop(ints, 100) }
class Integer
  alias_method :<=>, :__matrix_cmp
  remove_method :__matrix_cmp
end
t('Integer#<=> back: clamp') { clamp_loop(ints, -100, 300) }

class Float
  def floor = 42
end
t('Float#floor redefined') { unary_loop(floats, 2).sum }
class Float
  remove_method :floor
end
t('Float#floor back') { unary_loop(floats, 2).sum }

class Array
  def max = :mine
end
t('Array#max redefined') { minmax_loop(ints, 100) }
class Array
  remove_method :max
end
t('Array#max back') { minmax_loop(ints, 100) }

# ---------------------------------------------------------------- 5. shifts

def shift_loop(a, k, left)
  s = 0
  i = 0
  while i < a.size
    s += left ? (a[i] << k) : (a[i] >> k)
    i += 1
  end
  s
end
t('>> 4') { shift_loop(ints, 4, false) }
t('<< 4') { shift_loop(ints, 4, true) }
t('>> -3') { shift_loop(ints, -3, false) }
t('<< -3') { shift_loop(ints, -3, true) }
t('>> 64') { shift_loop(ints, 64, false) }
t('>> 200') { shift_loop(ints, 200, false) }
t('<< 40 into a Bignum') { shift_loop(ints, 40, true) }
t('<< 60 into a Bignum') { shift_loop(ints, 60, true) }
t('>> 2**64 (a Bignum count)') { shift_loop(ints, 2**64, false) }
t('a Float count') { shift_loop(ints, 2.5, false) }
lst = []
t('Array#<< still appends') { i = 0; while i < N; lst << i; i += 1; end; lst.size }

# ---------------------------------------------------------------- 6. predicates, unary minus

def preds(a)
  e = o = z = 0
  n = 0
  i = 0
  while i < a.size
    v = a[i]
    e += 1 if v.even?
    o += 1 if v.odd?
    z += 1 if v.zero?
    n += -v + +v - -v
    i += 1
  end
  [e, o, z, n]
end
t('even?, odd?, zero?, -x, +x') { preds(ints) }
t('of Int64 and negative values') { preds(Array.new(N) { |i| (i - 3000) * 2**40 }) }
t('-x of -2**63') { preds(Array.new(N) { |i| i == 5000 ? -2**63 : i }) }
t('zero? of Floats') { r = 0; i = 0; while i < N; r += 1 if floats[i].zero?; i += 1; end; r }
nn = Array.new(N) { |i| i % 3 == 0 ? nil : i }
t('nil? of nil and Integers') { r = 0; i = 0; while i < N; r += 1 if nn[i].nil?; i += 1; end; r }
class NilLike; def nil? = true; end
nn2 = nn.dup; nn2[5000] = NilLike.new
t('nil? redefined on one class') { r = 0; i = 0; while i < N; r += 1 if nn2[i].nil?; i += 1; end; r }

# ---------------------------------------------------------------- 7. x & mask is an Int32

def masks(a)
  s = 0
  i = 0
  while i < a.size
    v = a[i]
    s += (v & 63) * 64 + (v & 0x7FFFFFFF) + (-v & 1023)
    i += 1
  end
  s
end
t('masks of Int64 values') { masks(Array.new(N) { |i| (i - 3000) * 2**40 + i }) }
t('masks of negative Int32 values') { masks(Array.new(N) { |i| -i * 7919 }) }
t('a Bignum among them') { masks(Array.new(N) { |i| i == 5000 ? 2**70 + 5 : i }) }
t('Float#to_i & 63') { r = 0; i = 0; while i < N; r += ((i * -1.37).to_i & 63) * 64; i += 1; end; r }
