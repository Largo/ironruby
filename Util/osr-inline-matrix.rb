# Differential matrix for Ruby methods an outlined loop compiles in instead of calling
# (Src/Ruby/Runtime/Jit/JitCompiler.Inline.cs), and for the definite-assignment rule that lets a
# block's or a callee's locals be variables of the compiled loop (Src/Ruby/Runtime/Osr/DefiniteAssignment.cs).
# Run it under CRuby and under ./ir.sh with OSR and the JIT on and off, and diff:
#
#   ruby Util/osr-inline-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/osr-inline-matrix.rb > /tmp/osr.txt
#   ./ir.sh -X:NoOSR -X:NoJIT Util/osr-inline-matrix.rb > /tmp/generic.txt
#   diff /tmp/mri.txt /tmp/osr.txt && diff /tmp/mri.txt /tmp/generic.txt
#
#   IR_OSR_VERBOSE=1 ./ir.sh Util/osr-inline-matrix.rb 2>&1 >/dev/null | grep -a 'osr\]'

def t(label)
  print label.ljust(60), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts "#{e.class}: #{e.message[0, 50]}"
  end
end

N = 4000
P = Struct.new(:lo, :hi)

# ---------------------------------------------------------------- 1. self calls

class Shade
  attr_reader :hits
  def initialize; @hits = 0; @k = 3; end

  def light(level, d)
    return 0 if d > 900
    return 31 if d < 1
    l = 33 - (level >> 3) + (d / 64.0).to_i
    l.clamp(0, 31)
  end

  def maybe(i) = i.odd? ? nil : i
  def half(i) = i & 1 == 0 ? i / 2 : i * 0.5
  def count = @hits += 1
  def scaled(v) = v * @k

  def run(n)
    s = 0
    i = 0
    while i < n
      s += light(i & 255, i * 1.5) + scaled(i & 7)
      count
      i += 1
    end
    [s, @hits]
  end

  def run_mixed(n)
    s = 0
    i = 0
    while i < n
      h = half(i)
      s += h
      i += 1
    end
    s
  end
end
sh = Shade.new
t('self calls, early returns, ivar writes') { sh.run(N) }
t('again') { sh.run(N) }
t('a method that answers an Integer or a Float') { Shade.new.run_mixed(N) }

class Shade
  def scaled(v) = v * 100
end
t('the callee redefined') { sh.run(N) }

class Shade2 < Shade
  def light(level, d) = 1
end
t('self of a subclass that overrides the callee') { Shade2.new.run(N) }
t('self of the class again') { sh.run(N) }
s1 = Shade.new
def s1.light(level, d) = 2
t('self with a singleton method') { s1.run(N) }

# ---------------------------------------------------------------- 2. calls on other objects

class Tex
  def initialize(w); @w = w; @cols = Array.new(w) { |c| Array.new(16) { |r| (c * 16 + r) & 255 } }; end
  def column(x) = @cols[x % @w]
  def width = @w
  private def secret = 1
end
class Other
  def column(x) = [7] * 16
  def width = 1
end

def sample(tex, n)
  s = 0
  i = 0
  while i < n
    col = tex.column(i)
    s += col[i & 15] + tex.width
    i += 1
  end
  s
end
tx = Tex.new(8)
t('o.m(x) on a local') { sample(tx, N) }
t('another object of the class') { sample(Tex.new(5), N) }
t('an object of another class') { sample(Other.new, N) }

def call_private(o, n)
  i = 0
  while i < n
    o.secret
    i += 1
  end
end
t('a private method on another receiver') { call_private(tx, N) }

# ---------------------------------------------------------------- 3. arguments

def span(p, x) = (x - p.lo) * (p.hi - x)
def struct_args(n, p)
  s = 0
  i = 0
  while i < n
    s += span(p, i & 15)
    i += 1
  end
  s
end
t('a Struct argument read in the callee') { struct_args(N, P.new(2, 9)) }
t('another Struct class with those members') { struct_args(N, Struct.new(:hi, :lo).new(9, 2)) }

def fill(fb, i, v) = fb[i % fb.size] = v
def array_args(fb, n)
  i = 0
  while i < n
    fill(fb, i, i * 2)
    i += 1
  end
  fb.sum
end
t('an Array argument written in the callee') { array_args(Array.new(16, 0), N) }
fz = Array.new(16, 0).freeze
t('a frozen one') { array_args(fz, N) }

def reassign(a, b)
  a = a + b
  a * 2
end
def reassigning(n)
  s = 0
  i = 0
  while i < n
    s += reassign(i, 3)
    i += 1
  end
  s
end
t('a parameter the callee assigns') { reassigning(N) }

# ---------------------------------------------------------------- 4. deopts and raises in callees

class Acc
  attr_reader :total, :log
  def initialize; @total = 0; @log = []; end
  def add(v, big)
    @total += v
    v * big
  end
end
def acc_loop(a, n, big)
  s = 0
  i = 0
  while i < n
    s += a.add(1, big)
    i += 1
  end
  [s, a.total]
end
t('an ivar write, then Int64 overflow in the callee') { acc_loop(Acc.new, N, 2**62) }
t('the same without overflow') { acc_loop(Acc.new, N, 2) }

def divide(a, b) = a / b
def div_loop(n, z)
  s = 0
  i = 0
  while i < n
    s += divide(i, i == 3000 ? z : 1)
    i += 1
  end
  s
end
t('ZeroDivisionError from a callee') { div_loop(N, 0) }
t('its backtrace names the callee') do
  begin
    div_loop(N, 0)
  rescue ZeroDivisionError => e
    e.backtrace_locations.first(2).map { |l| l.label }
  end
end

def fact(k) = k <= 1 ? 1 : k * fact(k - 1)
def rec_loop(n)
  s = 0
  i = 0
  while i < n
    s += fact(i & 7)
    i += 1
  end
  s
end
t('a recursive callee') { rec_loop(N) }

# ---------------------------------------------------------------- 5. callees not compiled in

def opt(a, b = 2) = a + b
def with_block(a) = block_given? ? yield(a) : a
def name_of(a) = __method__
def nil_local(a)
  x = a if a > 100
  x
end
def not_inlined(n)
  s = []
  i = 0
  while i < n
    s = [opt(i), with_block(i), name_of(i), nil_local(i)]
    i += 1
  end
  s
end
t('optional args, a block, __method__, a nil local') { not_inlined(N) }

class MM
  def method_missing(name, *a) = name == :ghost ? 5 : super
  def respond_to_missing?(n, p = false) = n == :ghost || super
end
def mm_loop(o, n)
  s = 0
  i = 0
  while i < n
    s += o.ghost
    i += 1
  end
  s
end
t('method_missing') { mm_loop(MM.new, N) }

# ---------------------------------------------------------------- 6. constants of the callee's scope

module Pal
  WIDTH = 64
  class Map
    def wrap(x) = x % WIDTH
  end
end
WIDTH = 10
def const_loop(m, n)
  s = 0
  i = 0
  while i < n
    s += m.wrap(i) + WIDTH
    i += 1
  end
  s
end
t('a callee\'s constant resolves in its own scope') { const_loop(Pal::Map.new, N) }
Pal::Map.class_eval { def wrap(x) = x % 3 }
t('after the callee changed') { const_loop(Pal::Map.new, N) }

# ---------------------------------------------------------------- 7. block locals, definite assignment

def da_block(a, n)
  s = 0
  n.times do |i|
    if i.even?
      t = a[i % a.size]
    else
      t = 1000
    end
    s += t
  end
  s
end
t('a block local assigned in both arms') { da_block([1, 2, 3], N) }

def da_maybe(n)
  r = []
  n.times do |i|
    v = i if i > n - 3
    r << v if i > n - 5
  end
  r
end
t('a block local read where it may be unassigned (nil)') { da_maybe(N) }

def da_or(n)
  r = 0
  n.times do |i|
    x ||= i
    r += x
  end
  r
end
t('x ||= i of a block local') { da_or(N) }

# ---------------------------------------------------------------- 8. TracePoint

def traced(n)
  sh = Shade.new
  calls = 0
  tp = TracePoint.new(:call) { |e| calls += 1 if e.method_id == :light }
  sh.run(n)
  tp.enable { sh.run(n) }
  calls
end
t('TracePoint :call sees every inlined call') { traced(N) }

# ---------------------------------------------------------------- 9. receivers read out of Arrays

class Wall
  def initialize(textures); @textures = textures; end
  def texel(t, x, y)
    tex = @textures[t]
    col = tex.column(x)
    col[y & 15] + tex.width
  end
  def draw(n)
    s = 0
    i = 0
    while i < n
      s += texel(i % @textures.size, i, i >> 2)
      i += 1
    end
    s
  end
end
same = Array.new(6) { |k| Tex.new(4 + k) }
t('a receiver from an Array of one class') { Wall.new(same).draw(N) }
mixed_tex = same.dup
mixed_tex[3] = Other.new
t('one element of another class') { Wall.new(mixed_tex).draw(N) }
holey_tex = same.dup
holey_tex[2] = nil
t('a nil element reaching a call') { Wall.new(holey_tex).draw(N) }

Seg2 = Struct.new(:x1, :x2, :back)
Sec = Struct.new(:ceil)
def seg_columns(segs)
  s = 0
  segs.each do |seg|
    back = seg.back
    (seg.x1..seg.x2).each do |x|
      s += x
      s += back.ceil if back
    end
  end
  s
end
early_nil = Array.new(400) { |k| Seg2.new(k, k + 3 + (k % 7), k < 300 ? nil : Sec.new(k)) }
t('a receiver nil for a while, then an object') { 5.times.map { seg_columns(early_nil) }.last }

def short_heavy(rows)
  s = 0
  rows.each do |row|
    (0...row.size).each do |k|
      next if row[k] == 3
      s += Shade.new.light(row[k], k * 3.0)
    end
  end
  s
end
rows = Array.new(300) { |k| Array.new(2 + k % 9) { |j| (j * k) & 7 } }
t('short ranges entering a heavy outlined block') { 4.times.map { short_heavy(rows) }.last }
t('map and select over short Arrays') { (0...3000).sum { |k| a = [k, k + 1, k + 2]; a.map { |v| v * 2 }.sum + a.select { |v| v.odd? }.size } }
