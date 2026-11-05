# Differential matrix for iterators run inline by the loop and method JIT
# (Src/Ruby/Runtime/Jit/JitCompiler.Iterators.cs): Array#each, Integer#times, #upto, #downto and
# (a..b).each with a literal block, inside an outlined loop or a block BlockLoopSite outlines, and in
# a method the JIT compiles. Run it under CRuby and under ./ir.sh with OSR and the JIT on and off:
#
#   ruby Util/osr-iterator-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/osr-iterator-matrix.rb > /tmp/osr.txt
#   ./ir.sh -X:NoOSR Util/osr-iterator-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoOSR -X:NoJIT Util/osr-iterator-matrix.rb > /tmp/generic.txt
#   diff /tmp/mri.txt /tmp/osr.txt && diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/generic.txt
#
#   IR_OSR_VERBOSE=1 IR_JIT_VERBOSE=1 ./ir.sh Util/osr-iterator-matrix.rb 2>&1 >/dev/null | grep -a 'osr\]\|jit\]'

def t(label)
  print label.ljust(60), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts e.class
  end
end

N = 3000

# ---------------------------------------------------------------- 1. nested in an outlined block

bodies = Array.new(5) { |k| [k * 1.0, k * 2.0, k * 3.0] }
t('nbody: times { each { each { } } } of Floats') do
  s = 0.0
  N.times do
    bodies.each do |b1|
      bodies.each do |b2|
        dx = b1[0] - b2[0]; dy = b1[1] - b2[1]
        s += dx * dx + dy * dy
      end
    end
  end
  s
end
ib = Array.new(5) { |k| [k, k * 2, k * 3] }
t('the same of Integers') do
  s = 0
  N.times { ib.each { |p| ib.each { |q| s += (p[0] - q[0]) * (p[1] - q[2]) } } }
  s
end
mixed = bodies.map(&:dup)
t('a Float inner array meets an Integer, part way') do
  s = 0.0
  N.times do |k|
    mixed[3][0] = 7 if k == 2500
    mixed.each { |b1| mixed.each { |b2| s += b1[0] - b2[0] } }
  end
  [s, mixed[3][0]]
end
nils = bodies.map(&:dup)
t('a nil in an inner array, part way') do
  s = 0.0
  N.times do |k|
    nils[2][1] = nil if k == 2500
    nils.each { |b1| nils.each { |b2| s += b1[1] - b2[1] } }
  end
  s
end

# ---------------------------------------------------------------- 2. in a while, each kind

def kinds(a, n)
  s = 0
  i = 0
  while i < n
    a.each { |x| s += x }
    3.times { |k| s += k }
    i.upto(i + 2) { |k| s += k }
    (i + 2).downto(i) { |k| s -= k }
    (0..2).each { |k| s += k * i }
    (0...3).each { |k| s += k }
    (5..4).each { |k| s += 1000 }
    (5...5).each { |k| s += 1000 }
    i += 1
  end
  s
end
t('each, times, upto, downto, ranges in a while') { kinds([1, 2, 3], N) }

def values(a, n)
  r = nil
  i = 0
  while i < n
    r = [a.each { |x| x }, 4.times { |k| k }, 2.upto(5) { |k| k }, 9.downto(7) { |k| k }, (1..3).each { |k| k }, (1...3).each { |k| k }]
    i += 1
  end
  r
end
t('the values the iterators answer') { values([7, 8], N) }

def nexts(a, n)
  s = 0
  i = 0
  while i < n
    a.each do |x|
      next if x & 1 == 1
      s += x
    end
    10.times do |k|
      next unless k > 6
      s += k
    end
    i += 1
  end
  s
end
t('next in inline blocks') { nexts((1..9).to_a, N) }

def shadow(a, n)
  x = 100
  s = 0
  i = 0
  while i < n
    a.each { |x| s += x }
    s += x
    i += 1
  end
  [s, x]
end
t('a block parameter shadowing a local') { shadow([1, 2], N) }

def block_locals(a, n)
  s = 0.0
  i = 0
  while i < n
    a.each do |x|
      d = x * 0.5
      e = d + 1
      s += e
    end
    i += 1
  end
  s
end
t('block locals assigned first') { block_locals([1, 2, 3], N) }

def writes_outer(a, n)
  last = 0
  count = 0
  i = 0
  while i < n
    a.each do |x|
      last = x
      count += 1
    end
    i += 1
  end
  [last, count]
end
t('a block writing the loop\'s locals') { writes_outer([4, 5, 6], N) }

def stores(fb, n)
  i = 0
  while i < n
    (0...fb.size).each { |k| fb[k] = fb[k] + 1 }
    i += 1
  end
  fb.sum
end
t('stores in an inline block') { stores(Array.new(8, 0), N) }

def overflow(n, big)
  s = 0
  i = 0
  while i < n
    3.times { |k| s += big }
    i += 1
  end
  s
end
t('an Integer out of Int64 in an inline block') { overflow(N, 2**61) }

def big_bounds(n, g, h, m, top)
  s = 0
  i = 0
  while i < n
    g.upto(g + 2) { |k| s += k - g }
    (h - 2).upto(h + 1) { |k| s += k }
    (m + 1).downto(m - 1) { |k| s += k }
    (top - 2..top).each { |k| s += k }
    (top - 2...top).each { |k| s += k }
    i += 1
  end
  s
end
t('bounds past Int32, near Int64') { big_bounds(N, 2**62, 2**31, -2**31, 2**31 - 1) }
t('upto to Int64.MaxValue') { s = 0; i = 0; while i < N; (2**63 - 3).upto(2**63 - 1) { |k| s += 1 }; i += 1; end; s }

def float_range(n)
  i = 0
  while i < n
    (0.5..2.5).each { |k| k }
    i += 1
  end
end
t('a Float range') { float_range(N) }

def nonint_times(n, x)
  s = 0
  i = 0
  while i < n
    x.times { |k| s += k }
    i += 1
  end
  s
end
t('times of a Float') { nonint_times(N, 2.5) }
t('times of a negative number') { nonint_times(N, -3) }

# ---------------------------------------------------------------- 3. in methods the JIT compiles

def jit_sum(a)
  s = 0
  a.each { |x| s += x * 2 }
  4.times { |k| s += k }
  s
end
t('a JIT method with inline blocks') { (0...N).sum { |k| jit_sum([k, k + 1, k + 2]) } }

def jit_range(lo, hi)
  s = 0
  (lo..hi).each do |k|
    next if k == lo
    s += k
  end
  s
end
t('a JIT method: (lo..hi).each, next') { (0...N).sum { |k| jit_range(k, k + 5) } }

# ---------------------------------------------------------------- 4. redefined

class Array
  alias_method :__matrix_each, :each
  def each(&b) = __matrix_each { |x| b.call(x * 10) }
end
t('Array#each redefined') { kinds([1, 2, 3], N) }
class Array
  alias_method :each, :__matrix_each
  remove_method :__matrix_each
end
t('Array#each back') { kinds([1, 2, 3], N) }

class Integer
  alias_method :__matrix_times, :times
  def times = 0
end
t('Integer#times redefined') { kinds([1, 2, 3], N) }
class Integer
  alias_method :times, :__matrix_times
  remove_method :__matrix_times
end

class Range
  alias_method :__matrix_each, :each
  def each = self
end
t('Range#each redefined') { kinds([1, 2, 3], N) }
class Range
  alias_method :each, :__matrix_each
  remove_method :__matrix_each
end
t('all back') { kinds([1, 2, 3], N) }
