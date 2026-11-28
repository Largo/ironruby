# Differential checks for a `while ... yield` loop in a method (gap 18; the rows a typed copy of such a loop must pass,
# Docs/jit-resume-deopt.md, "Stage 3a: a yield in an outlined loop"). Every iterator below runs hot - several hundred
# calls over a hundred elements - so that the loop tier sees it, and each row then makes the block do what the copy
# would have to get right after the yield: break, next, redo, raise, redefine Integer#+ or Array#[], freeze or change
# the array, enable a TracePoint, re-enter the iterator, run on four threads. The output must match CRuby's:
#
#   ruby   Util/yield-loop-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/yield-loop-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# (also with -X:NoJIT -X:NoOSR, IR_LIGHT_FRAMES=0, IR_PARKED_SCOPES=0 and the change's own switch)
$stdout.sync = true

def rows(name) = puts("#{name}: #{yield.inspect}")

class L
  attr_reader :log
  def initialize(a) = (@a = a; @log = [])
  def each
    return to_enum(:each) unless block_given?
    i = 0
    while i < @a.size
      yield @a[i]
      i += 1
    end
    self
  end
  def each_ensure
    i = 0
    begin
      while i < @a.size
        yield @a[i]
        i += 1
      end
    ensure
      @log << [:ensure, i]
    end
  end
  def each_rescue
    i = 0
    begin
      while i < @a.size
        yield @a[i]
        i += 1
      end
      :done
    rescue => e
      [e.message, i]
    end
  end
  def map_like
    r = []
    i = 0
    while i < @a.size
      r << yield(@a[i])
      i += 1
    end
    r
  end
  def each2
    i = 0
    while i < @a.size
      yield @a[i], i
      i += 1
    end
    i
  end
  def each_block(&b)
    i = 0
    while i < @a.size
      yield @a[i]
      i += 1
    end
    b
  end
  def each_last
    i = 0
    x = nil
    while i < @a.size
      x = @a[i]
      yield x
      i += 1
    end
    [i, x]
  end
end

A100 = (1..100).to_a.freeze
l = L.new(A100.dup)
hot = 0
400.times { l.each { |x| hot += x } }
# every iterator of L hot too, so that the rows below run in the loops' compiled copies (IR_OSR_VERBOSE=1: specialized,
# and the hazards' yield-jumps and yield-tails), not only in the generic loops
400.times do
  l.each_last { }; l.map_like { |x| x }; l.each2 { }; l.each_block { }
  L.new(A100.dup).each_ensure { }; L.new(A100.dup).each_rescue { }
end
rows("hot") { hot }
rows("sizes") { [[], [7], [1, 2, 3, 4], A100].map { |a| s = 0; L.new(a).each { |x| s += x }; s } }
rows("returns self") { l.each { }.equal?(l) }
rows("enumerator") { l.each.first(3) }

# break, next and redo from the block
rows("break") { 3.times.map { |k| l.each { |x| break [:broke, x] if x == 50 + k } } }
rows("break ensure") { e = L.new(A100.dup); r = e.each_ensure { |x| break x * 2 if x == 37 }; [r, e.log] }
rows("next") { l.map_like { |x| next -x if x.odd?; x }.first(6) }
rows("redo") { seen = []; tries = 0; l.map_like { |x| tries += 1; redo if x == 3 && tries < 5; seen << x; x }.size.then { [seen.first(5), tries] } }
rows("break each2") { l.each2 { |x, i| break [x, i] if i == 99 } }
rows("each2") { l.each2 { |x, i| } }

# a raise in the block: the iterator's rescue and ensure see the loop's own counter
rows("raise rescue") { L.new(A100.dup).each_rescue { |x| raise "at #{x}" if x == 42 } }
rows("raise ensure") { e = L.new(A100.dup); r = (e.each_ensure { |x| raise ArgumentError if x == 64 } rescue $!.class); [r, e.log] }
rows("raise outside") { s = 0; r = (l.each { |x| s += x; raise IndexError, "#{x}" if x == 99 } rescue $!.message); [r, s] }
rows("throw") { catch(:t) { l.each { |x| throw :t, x if x == 77 }; :none } }

# the block changes the world under the loop: Integer#+, Array#[], the array, a freeze
rows("redefine +") {
  calls = []
  r = l.each_last { |x|
    if x == 10
      Integer.class_eval { alias_method :__ylm_plus, :+; define_method(:+) { |o| calls << [self, o] if o == 1 && calls.size < 3; __ylm_plus(o) } }
    elsif x == 12
      Integer.class_eval { alias_method :+, :__ylm_plus; remove_method :__ylm_plus }
    end
  }
  [r, calls]
}
rows("redefine []") {
  reads = []
  e = L.new(A100.dup)
  r = e.each_last { |x|
    if x == 20
      Array.class_eval { alias_method :__ylm_at, :[]; define_method(:[]) { |*a| reads << a if reads.size < 3; __ylm_at(*a) } }
    elsif x == 22
      Array.class_eval { alias_method :[], :__ylm_at; remove_method :__ylm_at }
    end
  }
  [r, reads]
}
rows("grow") { a = [1, 2, 3]; s = []; L.new(a).each { |x| s << x; a << x + 10 if a.size < 8 }; s }
rows("shrink") { a = (1..10).to_a; s = []; L.new(a).each { |x| s << x; a.pop }; s }
rows("freeze") { a = (1..5).to_a; s = []; L.new(a).each { |x| s << x; a.freeze if x == 2 }; [s, a.frozen?] }
rows("ivar swap") { e = L.new([1, 2, 3]); s = []; e.each { |x| s << x; e.instance_variable_set(:@a, [7, 8, 9, 10]) if x == 1 }; s }

# a TracePoint enabled from the block sees the rest of the iteration
rows("trace line") {
  ev = []
  tp = TracePoint.new(:line, :return) { |t| ev << [t.event, t.lineno - L.instance_method(:each).source_location[1], t.method_id] if t.path == __FILE__ && t.method_id == :each }
  L.new((1..6).to_a).each { |x| tp.enable if x == 3; tp.disable if x == 5 }
  ev.first(12)
}
rows("trace binding") {
  vals = []
  tp = TracePoint.new(:line) { |t| vals << t.binding.local_variable_get(:i) if t.path == __FILE__ && t.method_id == :each }
  L.new((1..4).to_a).each { |x| tp.enable if x == 2; tp.disable if x == 3 }
  vals
}

# the block passed in other forms, re-entrancy, a fiber, threads, an iterator that keeps its block
rows("lambda") { s = 0; l.each(&->(x) { s += x }); s }
rows("method") { $ylm = 0; def ylm_add(x) = $ylm += x; l.each(&method(:ylm_add)); $ylm }
rows("symbol") { L.new(%w[a b]).map_like(&:upcase) }
rows("kept block") { b = l.each_block { |x| x }; [b.class, b.call(5)] }
rows("re-entrant") { s = 0; small = L.new([1, 2, 3]); small.each { |x| small.each { |y| s += x * y } }; s }
rows("recursion") { r = []; t = nil; t = ->(n) { L.new((1..n).to_a).each { |x| r << [n, x]; t.(x - 1) if x == n && n > 1 } }; t.(3); r }
rows("fiber") { f = Fiber.new { l.each { |x| Fiber.yield x if x % 40 == 0 }; :end }; [f.resume, f.resume, f.resume] }
rows("threads") { 4.times.map { |t| Thread.new { ylm_s = 0; 50.times { l.each { |x| ylm_s += x * t } }; ylm_s } }.map(&:value) }
rows("big counter") {
  big = Class.new {
    def each
      i = 2**62
      while i < 2**64
        yield i
        i += 2**61
      end
      i
    end
  }.new
  r = []; e = big.each { |x| r << x }; [r.size, e, e.class]
}
# the same past Int64 once the copy runs the loop: its `i += 2**49' overflows in the statements after the yield, which
# then run in the generic loop from the locals as they were right after the yield (OsrYieldOutcome.RunTail)
rows("big counter hot") {
  big2 = Class.new {
    def each
      i = 0
      while i < 2**64
        yield i
        i += 2**49
      end
      i
    end
  }.new
  n = 0; last = nil
  e = big2.each { |x| n += 1; last = x }
  [n, last, e, e.class]
}
# an Integer counter that is still an Int32 when the copy is compiled (about 2,000 back edges) and outgrows it at the
# 2,048th: the overflow is a deopt in the statements after the yield, after the block ran for that element
rows("int overflow hot") {
  ov = Class.new {
    def each(lim, step)
      i = 0
      while i < lim
        yield i
        i += step
      end
      i
    end
  }.new
  n = 0; s = 0
  e = ov.each(17179869184, 1048576) { |x| n += 1; s += x }
  [n, s, e]
}
