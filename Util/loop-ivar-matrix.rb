# Differential matrix for instance variable writes inside loops (perf/loopivar): `@x = v' and
# `@x op= v' in a `while' loop, which the OSR tier outlines and compiles to a logged store into self's
# variable (JitCompiler.EmitLoopIVarWrite, JitRuntime.WriteSelfIVar) when IR_LOOP_IVAR_WRITES=1 (off
# by default; run it both ways). A block loop (`times', `each') still refuses them; its rows are here for when it
# does not. Run under CRuby and under ./ir.sh and diff:
#
#   ruby Util/loop-ivar-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/loop-ivar-matrix.rb > /tmp/ir.txt
#   diff /tmp/mri.txt /tmp/ir.txt
#
# Every method is run hot first (HOT calls with long loops) so that its loop is outlined before
# the row's call, and each row then does something the compiled store must get right: Integer,
# Float and String values, an Integer that grows into a Bignum, a type change, a variable the
# object does not have yet, one removed, a frozen self (the FrozenError at the iteration the
# source raises it in, with exactly the writes before it made), a loop left by break and by an
# exception after k writes, a subclass's object with more variables, and other threads reading
# the variable or adding variables to the object while the loop writes.

def t(label)
  print label.ljust(60), ' => '
  begin
    v = yield
    puts v.inspect.gsub(/0x\h+/, '0xXX')
  rescue Exception => e
    puts "#{e.class}: #{e.message.gsub(/0x\h+/, '0xXX')}"
  end
end

HOT = 40
N = 2_000

class W
  attr_reader :c, :f, :s, :a, :b, :z
  def initialize; @c = 0; @f = 0.0; @s = nil; @a = 0; @b = 0; end
  def count_up(n) = (i = 0; while i < n; @c += 1; i += 1; end; @c)
  def add_float(n) = (i = 0; while i < n; @f += 0.5; i += 1; end; @f)
  def last_of(arr) = (i = 0; while i < arr.size; @s = arr[i]; i += 1; end; @s)
  def pair(n) = (i = 0; while i < n; @a = i; @b = @a * 2; i += 1; end; [@a, @b])
  def set_z(n) = (i = 0; while i < n; @z = i; i += 1; end; @z)
  def step_by(n, d) = (i = 0; while i < n; @c += d; i += 1; end; @c)
  def from(n, k) = (i = 0; while i < n; @c += 1 if i >= k; i += 1; end; @c)
  def from_rescued(n, k)
    i = 0
    begin
      while i < n; @c += 1 if i >= k; i += 1; end
      [:done, i, @c]
    rescue => e
      [e.class, i, @c]
    end
  end
  def until_k(k) = (i = 0; while true; @c += 1; i += 1; break if i == k; end; [i, @c])
  def raise_at(n, k)
    i = 0
    begin
      while i < n; @c += 1; x = 100 / (k - i); i += 1; end
      [:done, i, @c, x]
    rescue ZeroDivisionError => e
      [e.class, i, @c]
    end
  end
  def times_up(n) = (n.times { @c += 1 }; @c)
  def each_last(arr) = (arr.each { |x| @s = x }; @s)
  def each_break(arr, k) = (arr.each { |x| @c += 1; break if x == k }; @c)
  def each_float(arr) = (arr.each { |x| @f += x }; @f)
end

class WSub < W
  def initialize; @p = :p; @q = :q; super; @r = :r; end
end

STRS = %w[alpha beta gamma delta]
def warm
  HOT.times do
    w = W.new
    w.count_up(N); w.add_float(N); w.last_of(STRS * 200); w.pair(N); w.set_z(N); w.step_by(N, 3)
    w.from(N, 10); w.from_rescued(N, 10); w.until_k(N); w.raise_at(N, N + 5); w.times_up(N)
    w.each_last(STRS * 200); w.each_break((0...N).to_a, N - 1); w.each_float([0.25] * N)
  end
end
warm

t("count_up") { w = W.new; [w.count_up(N), w.count_up(N), w.c] }
t("add_float") { w = W.new; [w.add_float(N), w.f] }
t("last_of strings") { w = W.new; [w.last_of(STRS * 300), w.s, w.s.frozen?] }
t("pair: read after write in the iteration") { w = W.new; [w.pair(N), w.a, w.b] }
t("a variable not there yet") { w = W.new; [w.instance_variable_defined?(:@z), w.set_z(N), w.instance_variables] }
t("removed, then written again") { w = W.new; w.set_z(N); w.remove_instance_variable(:@z); [w.set_z(N), w.instance_variables] }
t("removed in the middle of the shape") { w = W.new; w.remove_instance_variable(:@f); [w.count_up(N), w.instance_variables] }
t("Integer to Bignum") { w = W.new; w.instance_variable_set(:@c, 2**62); [w.step_by(N, 2**52), w.c.class] }
t("Bignum step") { w = W.new; [w.step_by(N, 2**70), w.c.class] }
t("type change: Float step") { w = W.new; [w.step_by(N, 0.25), w.step_by(N, 1), w.c.class] }
t("type change: String in the variable") { w = W.new; w.instance_variable_set(:@c, 'x'); w.step_by(N, 1) }
t("type change: nil in the variable") { w = W.new; w.instance_variable_set(:@c, nil); w.count_up(N) }
t("frozen before the loop") { w = W.new; w.freeze; [(w.count_up(N) rescue $!.class), w.c] }
t("frozen, the first write at iteration k") { w = W.new; w.freeze; w.from_rescued(N, 1500) }
t("frozen, first write at k, outside rescue") { w = W.new; w.freeze; [(w.from(N, 1500) rescue $!.class), w.c] }
t("break after k writes") { w = W.new; [w.until_k(1234), w.c] }
t("raise after k writes") { w = W.new; [w.raise_at(N, 1234), w.c] }
t("no raise") { w = W.new; w.raise_at(N, N + 7) }
t("subclass object, more variables") { s = WSub.new; [s.count_up(N), s.add_float(N), s.pair(N), s.instance_variables] }
t("singleton class object") { w = W.new; def w.extra = 1; [w.count_up(N), w.extra] }
t("times block") { w = W.new; [w.times_up(N), w.c] }
t("each block, strings") { w = W.new; [w.each_last(STRS * 300), w.s] }
t("each block, break after k") { w = W.new; [w.each_break((0...N).to_a, 777), w.c] }
t("each block, Float") { w = W.new; [w.each_float([0.125] * N), w.f] }
t("each block, frozen") { w = W.new; w.freeze; [(w.each_float([0.125] * N) rescue $!.class), w.f] }
t("instance_variable_get/inspect after") { w = W.new; w.count_up(N); w.add_float(N); [w.instance_variable_get(:@c), w.inspect.sub(/0x\h+/, '')] }
t("Marshal and dup after") { w = W.new; w.count_up(N); d = w.dup; m = Marshal.load(Marshal.dump(w)); [d.c, m.c, d.count_up(N), w.c] }

t("another thread reads while the loop writes") do
  w = W.new
  seen = []
  stop = false
  reader = Thread.new { until stop; v = w.c; seen << v.class unless v.is_a?(Integer) && v >= 0 && v <= 50 * N; end }
  r = 50.times.map { w.count_up(N) }.last
  stop = true
  reader.join
  [r, seen.uniq]
end
t("another thread adds variables while the loop writes") do
  w = W.new
  adder = Thread.new { 300.times { |k| w.instance_variable_set(:"@x#{k}", k) } }
  r = 20.times.map { w.count_up(N) }.last
  adder.join
  [r, w.c, w.instance_variables.size, w.instance_variable_get(:@x299)]
end
t("instance_variable_set from a callee inside the loop") do
  class W
    def bump_by_callee(n) = (i = 0; while i < n; @c += 1; instance_variable_set(:@c, @c + 1) if i % 100 == 0; i += 1; end; @c)
  end
  w = W.new
  5.times { W.new.bump_by_callee(N) }
  [w.bump_by_callee(N), w.c]
end
t("a callee freezes self inside the loop") do
  class W
    def freeze_at(n, k) = (i = 0; while i < n; @c += 1; freeze if i == k; i += 1; end; @c)
  end
  5.times { W.new.freeze_at(N, N + 1) }
  w = W.new
  [(w.freeze_at(N, 600) rescue $!.class), w.c, w.frozen?]
end
