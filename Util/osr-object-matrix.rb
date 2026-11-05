# Differential matrix for what an outlined (OSR) loop does with objects: instance variable writes
# (JitCompiler.EmitLoopIVarWrite, JitRuntime.WriteIVar), Struct member reads (JitRuntime.ReadStructMember)
# and `a[i] ||= v' / `a[i] &&= v' of an Array the loop indexes (EmitArrayLocalLogicalWrite).
# Run it under CRuby and under ./ir.sh with OSR on and off, and diff:
#
#   ruby Util/osr-object-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/osr-object-matrix.rb > /tmp/osr.txt
#   ./ir.sh -X:NoOSR -X:NoJIT Util/osr-object-matrix.rb > /tmp/generic.txt
#   diff /tmp/mri.txt /tmp/osr.txt && diff /tmp/mri.txt /tmp/generic.txt
#
# Every loop runs past the OSR threshold (2000 back edges) before it meets what it probes: a write
# that must be taken back when a later deopt re-runs the iteration, a frozen self, a variable the
# object does not have yet, a redefined or private Struct reader, an Array that would have to grow.
#
#   IR_OSR_VERBOSE=1 ./ir.sh Util/osr-object-matrix.rb 2>&1 >/dev/null | grep -a 'osr\]'

def t(label)
  print label.ljust(60), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts e.class
  end
end

N = 6000

# ---------------------------------------------------------------- 1. instance variable writes

class Counter
  attr_reader :n, :f, :log
  def initialize; @n = 0; @f = 0.0; @log = 0; end

  def count(k)
    i = 0
    while i < k
      @n = @n + 1
      @f += 0.5
      i += 1
    end
    [@n, @f]
  end

  # @n is written, then the multiplication overflows out of Int64 in the same iteration: the
  # generic loop re-runs the iteration, which must start from @n as it was
  def grow(k, big)
    i = 0
    while i < k
      @n += 1
      @log = i * big
      i += 1
    end
    [@n, @log]
  end

  def first_set(k)
    i = 0
    while i < k
      @fresh = i if i > 3000
      i += 1
    end
    @fresh
  end

  def any(k, v)
    i = 0
    while i < k
      @n = (i == k - 1) ? v : i
      i += 1
    end
    @n
  end
end

c = Counter.new
t('@n = @n + 1, @f += 0.5') { c.count(N) }
t('again, the copy re-entered') { c.count(N) }
t('a write, then Int64 overflow') { Counter.new.grow(N, 2**40) }
t('a write, then Bignum all along') { Counter.new.grow(N, 2**62) }
t('a variable set for the first time in the loop') { Counter.new.first_set(N) }
t('a String into an Integer variable') { Counter.new.any(N, 'x') }
fz = Counter.new
fz.count(N)
fz.freeze
t('frozen self') { fz.count(N) }
t('frozen self, unchanged') { [fz.n, fz.f] }

class Counter2 < Counter
  def initialize; super; @extra = 1; end
end
t('a subclass with another shape') { Counter2.new.count(N) }

# ---------------------------------------------------------------- 2. Struct members

Seg = Struct.new(:x1, :x2, :dist, :tex)

def seg_sum(segs)
  s = 0
  t = 0.0
  i = 0
  while i < segs.size
    seg = segs[i]
    j = 0
    while j < 40
      s += seg.x2 - seg.x1 + j
      t += seg.dist
      j += 1
    end
    i += 1
  end
  [s, t]
end

segs = Array.new(300) { |k| Seg.new(k, k + 7, k * 0.5, k & 7) }
t('Struct reads') { seg_sum(segs) }
odd = segs.dup
odd[150] = Seg.new(1, nil, 2.0, 0)
t('a nil member') { seg_sum(odd) }
odd[150] = Seg.new(1, 2.5, 2.0, 0)
t('a Float where Integers were') { seg_sum(odd) }
Other = Struct.new(:dist, :x2, :x1)
odd[150] = Other.new(3.0, 9, 4)
t('another Struct, members in another order') { seg_sum(odd) }
SubSeg = Class.new(Seg)
odd[150] = SubSeg.new(1, 5, 1.0, 0)
t('a Struct subclass') { seg_sum(odd) }
Plain = Class.new { attr_reader :x1, :x2, :dist; def initialize; @x1 = 1; @x2 = 3; @dist = 0.25; end }
odd[150] = Plain.new
t('an object with attr_readers') { seg_sum(odd) }
class Seg
  def x2 = 1000
end
t('Seg#x2 redefined') { seg_sum(segs) }
class Seg
  remove_method :x2
end
t('Seg#x2 removed: no reader at all') { seg_sum(segs) }
P2 = Struct.new(:x1, :x2, :dist)
class P2
  private :x2
end
t('a private member reader') { seg_sum([P2.new(1, 2, 3.0)] * 300) }

# ---------------------------------------------------------------- 3. a[i] ||= v, a[i] &&= v

def mark(spans, n, x)
  y = 0
  r = 0
  while y < n
    v = (spans[y % spans.size] ||= x + y)
    r += v
    y += 1
  end
  r
end

spans = Array.new(240)
t('a[i] ||= v over nils') { mark(spans, N, 7) }
t('a[i] ||= v, all set') { mark(spans, N, 1000) }
fs = Array.new(240) { |i| i.even? ? false : nil }
t('a[i] ||= v over false and nil') { [mark(fs, N, 3), fs.first(4)] }
t('a[i] ||= v past the end (grows)') { short = Array.new(10); mark(short, N, 1); }
fr = Array.new(240).freeze
t('a[i] ||= v of a frozen Array') { mark(fr, N, 1) }
fr2 = Array.new(240, 5).freeze
t('a[i] ||= v of a frozen Array, nothing to store') { mark(fr2, N, 1) }

def unmark(spans, n)
  y = 0
  while y < n
    spans[y % spans.size] &&= y
    y += 1
  end
  spans.compact.size
end
um = Array.new(240) { |i| i % 3 == 0 ? nil : i }
t('a[i] &&= v') { [unmark(um, N), um.first(6)] }

def fidx(a, n)
  i = 0
  while i < n
    a[i * 0.5] ||= i
    i += 1
  end
  a.compact.size
end
t('a Float index') { fidx(Array.new(N), N) }

# ---------------------------------------------------------------- 4. self.x = v

class Writer
  attr_accessor :v
  attr_writer :hidden
  private :hidden=
  def initialize; @v = 0; @hidden = 0; end
  def run(k)
    i = 0
    while i < k
      self.v = i
      self.hidden = i * 2
      i += 1
    end
    [@v, @hidden]
  end
end
t('self.v = i, a private self.hidden =') { Writer.new.run(N) }
w = Writer.new
w.run(N)
class Writer
  def v=(x); @v = -x; end
end
t('Writer#v= redefined') { w.run(N) }
w.freeze
t('frozen self') { w.run(N) }
