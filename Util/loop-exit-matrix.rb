# Differential matrix for what an OSR loop's typed copy hands back at its exits (IR_LOOP_EXITS: Src/Ruby/Runtime/Jit/
# JitLoopCompiler.cs): an Int32 goes home through the small-integer cache, a local the copy never assigns is not stored
# back, and a block loop's copy (BlockLoopSite) stores none of its block's own locals. Run it under CRuby and under ./ir.sh
# with the switch on and off and with the tiers off, and diff:
#
#   ruby Util/loop-exit-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/loop-exit-matrix.rb > /tmp/on.txt
#   IR_LOOP_EXITS=0 ./ir.sh Util/loop-exit-matrix.rb > /tmp/off.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/loop-exit-matrix.rb > /tmp/notiers.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt && diff /tmp/mri.txt /tmp/notiers.txt
#
# Every loop runs well past the OSR threshold (2000 back edges or yields) before the case it probes, so the typed copy
# is what meets it. The last section has an Integer instance variable updated in a hot loop: past Int32, turned into a
# Float or a String, and read by another thread.

def t(label)
  print label.ljust(64), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts "#{e.class}: #{e.message}"
  end
end

N = 6000

# ---------------------------------------------------------------- 1. Integers going home around the cache's bounds

t('while: counter ends at 999, 1000, 1001') { [999, 1000, 1001].map { |m| i = 0; while i < m * 3 + N; i += 1; end; i - m * 3 - N + m } }
t('while: local ends at -100, -101') { [-100, -101].map { |m| i = 0; k = 0; while i < N; i += 1; k = m if i == N; end; [k, k.equal?(m)] } }
t('while: local ends past Int32') { i = 0; k = 2**31 - 3000; while i < N; i += 1; k += 1; end; [k, k.class] }
t('while: local ends past Int64') { i = 0; k = 2**63 - 3000; while i < N; i += 1; k += 1; end; [k, k.class] }
t('times: an outer Integer') { s = 0; N.times { |i| s += i & 7 }; s }
t('each: an outer Integer near the bound') { s = 990; a = Array.new(N, 0); a[-1] = 20; a.each { |x| s += x }; s }
t('object_id of an Integer that went home') { i = 0; while i < N; i += 1; end; [i.object_id == N.object_id, i.equal?(N)] }

# ---------------------------------------------------------------- 2. locals the copy only reads

class Reader
  def initialize; @a = Array.new(50) { |i| i * 0.5 }; end
  def scaled(dt)
    s = 0.0
    @a.each { |x| s += x * dt }
    [s, dt, dt.class]
  end
  def int_read(k)
    s = 0
    N.times { |i| s += k }
    [s, k]
  end
  def changed_by_callee(dt)
    s = 0.0
    f = proc { dt = 2.0 }
    i = 0
    while i < N
      s += dt
      f.call if i == N / 2
      i += 1
    end
    [s, dt]
  end
end

t('a Float parameter only read in an each block') { r = Reader.new; 3000.times { r.scaled(0.25) }; r.scaled(0.75) }
t('an Integer parameter only read in a times block') { r = Reader.new; 50.times { r.int_read(3) }; r.int_read(2**40) }
t('a local the loop reads that a proc changes') { r = Reader.new; r.changed_by_callee(1.0) }

t('a local another thread changes while the loop reads it') {
  dt = 1.0
  q1 = Queue.new
  q2 = Queue.new
  th = Thread.new { q1.pop; dt = 3.0; q2 << :done }
  s = 0.0
  i = 0
  while i < N
    s += dt
    if i == N / 2
      q1 << :go
      q2.pop
    end
    i += 1
  end
  th.join
  [s, dt]
}

# ---------------------------------------------------------------- 3. a block loop's own locals

t('block parameter and locals not visible after the block') {
  x = :outer
  r = [1, 2, 3] * 2000
  s = 0
  r.each { |e| y = e * 2; s += y }
  [s, x, defined?(y), defined?(e)]
}
t('block locals shadowing nothing, read again next call') {
  out = []
  3.times do |k|
    a = Array.new(N) { |i| i + k }
    t = 0
    a.each_index { |i| v = a[i] - k; t += v }
    out << t
  end
  out
}
t('a deopt in a block loop: the iteration redone') {
  a = Array.new(N) { |i| i }
  a[N - 10] = 1.5
  s = 0
  a.each { |e| w = e * 2; s += w }
  s
}
t('a deopt in each_index: the parameter after it') {
  a = Array.new(N) { |i| i }
  a[N / 2] = 'x'
  r = []
  a.each_index { |i| v = a[i]; r << i if v.is_a?(String) }
  r
}
t('nested: the outer block parameter after the inner loop') {
  a = Array.new(100) { |i| i * 0.5 }
  s = 0.0
  40.times do
    a.each_index do |i|
      x = a[i]
      ((i + 1)...a.size).each { |j| s += x - a[j] }
    end
  end
  s
}

# ---------------------------------------------------------------- 4. an Integer instance variable in a hot loop

class Counter
  attr_accessor :n, :log
  def initialize(n); @n = n; @log = []; end
  def bump(k); i = 0; while i < k; @n += 1; i += 1; end; @n; end
  def bump_each(a); a.each { |x| @n += x }; @n; end
  def bump_to(k, at, value); i = 0; while i < k; @n = value if i == at; @n += 1; i += 1; end; @n; end
end

t('Integer ivar: past Int32 in the middle of a loop') { c = Counter.new(2**31 - 3000); c.bump(N); [c.n, c.n.class] }
t('Integer ivar: past Int64 in the middle of a loop') { c = Counter.new(2**63 - 3000); c.bump(N); [c.n, c.n.class] }
t('Integer ivar: in an each block, past Int32') { c = Counter.new(2**31 - 100); c.bump_each(Array.new(N, 1)); c.n }
t('Integer ivar: set to a Float in the middle') { c = Counter.new(0); c.bump(N); c.bump_to(N, N / 2, 0.5) }
t('Integer ivar: set to a String in the middle') { c = Counter.new(0); c.bump(N); c.bump_to(N, N / 2, 'x') }
t('Integer ivar: instance_variable_set from outside, then the loop') {
  c = Counter.new(0); c.bump(N); c.instance_variable_set(:@n, 2**40); c.bump(N)
}
t('Integer ivar: a subclass with more ivars') {
  k = Class.new(Counter) { def initialize(n); super; @m = 1; @o = 2; end }
  c = Counter.new(0); c.bump(N)
  d = k.new(5); [d.bump(N), c.bump(N)]
}
t('Integer ivar: read by another thread while a loop updates it') {
  c = Counter.new(0)
  c.bump(N)
  seen = []
  stop = false
  reader = Thread.new { last = -1; until stop; v = c.n; seen << :backwards if v < last; last = v; Thread.pass; end; seen.uniq }
  20.times { c.bump(N) }
  stop = true
  [reader.value, c.n]
}
