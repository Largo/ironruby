# External iteration (Enumerator#next, #peek, #rewind, #feed...) over the sources a cursor walks
# without a fiber (Enumerator.cs) and over the ones that keep their fiber, and the StopIteration that
# ends it: its result, its cause, its backtrace. Prints one line per probe; CRuby 4.0, the unchanged
# build and the candidate must print the same (LEDGER gap 35, perf/extiter).
#
#   ruby Util/extiter-matrix.rb > a; ./ir.sh Util/extiter-matrix.rb > b; diff a b
$stdout.sync = true

def probe(name)
  r = begin
    yield
  rescue Exception => e
    "#{e.class}: #{e.message}"
  end
  puts "#{name}: #{r.inspect}"
end

# a backtrace without the path of this file
def bt(e) = (e.backtrace || []).map { |l| l.sub(/\A.*extiter-matrix\.rb:/, "") }.first(4)

def drain(e)
  out = []
  loop { out << e.next }
  out
end

def stop_of(e)
  loop { e.next }
  begin
    e.next
  rescue StopIteration => s
    s
  end
end

# --- what each source yields, and what #each answers --------------------------------------
probe("ary")          { drain([1, 2, 3].each) }
probe("ary_empty")    { drain([].each) }
probe("range")        { drain((1..4).each) }
probe("range_excl")   { drain((1...4).each) }
probe("range_endless"){ e = (1..).each; [e.next, e.next, e.next] }
probe("hash")         { drain({ a: 1, b: 2 }.each) }
probe("hash_pair")    { drain({ a: 1, b: 2 }.each_pair) }
probe("hash_keys")    { drain({ a: 1, b: 2 }.each_key) }
probe("hash_values")  { drain({ a: 1, b: 2 }.each_value) }
probe("times")        { drain(3.times) }
probe("upto")         { drain(1.upto(3)) }
probe("downto")       { drain(3.downto(1)) }
probe("each_index")   { drain(%w[a b c].each_index) }
probe("reverse_each") { drain([1, 2, 3].reverse_each) }
probe("ewi")          { drain([:a, :b].each_with_index) }
probe("each_slice")   { drain([1, 2, 3, 4, 5].each_slice(2)) }
probe("each_cons")    { drain([1, 2, 3, 4].each_cons(2)) }
probe("bytes")        { drain("ab".each_byte) }
probe("chars")        { drain("ab".each_char) }
probe("struct")       { S1 = Struct.new(:a, :b) unless defined?(S1); drain(S1.new(1, 2).each) }
probe("with_index")   { drain([:a, :b].each.with_index(1)) }
probe("map")          { drain([1, 2].map) }
probe("select")       { drain([1, 2].select) }
probe("lazy")         { drain([1, 2, 3].lazy.map { |x| x * 2 }) }
probe("generator")    { drain(Enumerator.new { |y| y << 1; y.yield 2, 3; y << 4; :gen_done }) }
probe("produce")      { e = Enumerator.produce(1) { |x| x * 2 }; [e.next, e.next, e.next, e.next] }
probe("produce_stop") { e = Enumerator.produce(1) { |x| x < 3 ? x + 1 : raise(StopIteration) }; drain(e) }
probe("chain")        { drain([1].each + [2, 3]) }

# --- StopIteration#result: what #each answered ---------------------------------------------
a = [1, 2]
probe("result_ary")   { stop_of(a.each).result.equal?(a) }
probe("result_hash")  { h = { x: 1 }; stop_of(h.each).result.equal?(h) }
probe("result_range") { stop_of((1..2).each).result }
probe("result_times") { stop_of(2.times).result }
probe("result_gen")   { stop_of(Enumerator.new { |y| y << 1; :gen_result }).result }
probe("result_map")   { stop_of([1, 2].map).result }
probe("result_slice") { stop_of([1, 2, 3].each_slice(2)).result }
probe("loop_value")   { e = [1, 2].each; loop { e.next } }
probe("loop_gen")     { e = Enumerator.new { |y| y << 1; 42 }; loop { e.next } }
probe("loop_nested")  { e = [1].each; f = [2, 3].each; r = loop { e.next; loop { f.next } }; r }
probe("loop_other_stop") { loop { raise StopIteration, "mine" } }
probe("stop_message") { e = [].each; begin; e.next; rescue StopIteration => s; [s.class, s.message, s.result]; end }

# --- the end, raised again: a new StopIteration each time whose cause is the first ---------
probe("stop_again") do
  e = [1].each
  e.next
  s1 = (e.next rescue $!)
  s2 = (e.next rescue $!)
  s3 = (e.next rescue $!)
  [s1.equal?(s2), s2.cause.equal?(s1), s3.cause.equal?(s1), s1.cause, s2.result, s3.message]
end
probe("stop_again_dropped") do
  e = [1].each
  e.next
  begin; e.next; rescue StopIteration; end
  s2 = (e.next rescue $!)
  [s2.cause.class, s2.cause.result, s2.cause.message]
end
probe("stop_again_peek") do
  e = [1].each
  e.next
  s1 = (e.peek rescue $!)
  s2 = (e.next rescue $!)
  [s2.cause.equal?(s1), s2.result]
end
probe("stop_gen_again") do
  e = Enumerator.new { |y| y << 1; :r }
  e.next
  s1 = (e.next rescue $!)
  s2 = (e.next rescue $!)
  [s2.cause.equal?(s1), s2.result, s1.result]
end

# --- backtraces of the end ----------------------------------------------------------------
def bt_direct
  e = [1].each
  e.next
  begin
    e.next
  rescue StopIteration => s
    bt(s)
  end
end
probe("bt_direct") { bt_direct }

def bt_inner(e) = e.next
def bt_nested
  e = [].each
  begin
    bt_inner(e)
  rescue StopIteration => s
    bt(s)
  end
end
probe("bt_nested") { bt_nested }

def bt_global
  e = (1..1).each
  e.next
  begin
    e.next
  rescue StopIteration
    bt($!)
  end
end
probe("bt_global") { bt_global }

# the first end rescued without a look, the cause read later in the same frame
def bt_cause_same
  e = [1].each
  e.next
  begin; e.next; rescue StopIteration; end
  s2 = (e.next rescue $!)
  [bt(s2.cause), bt(s2)]
end
probe("bt_cause_same") { bt_cause_same }

# the first end rescued and dropped in a frame that has returned, the cause read later
def bt_first_end(e)
  begin; e.next; rescue StopIteration; end
  nil
end
def bt_cause_later
  e = [1].each
  e.next
  bt_first_end(e)
  s2 = (e.next rescue $!)
  [bt(s2.cause), bt(s2)]
end
probe("bt_cause_later") { bt_cause_later }

def bt_loop_kept
  e = [1].each
  loop { e.next }
  s2 = (e.next rescue $!)
  [bt(s2.cause).size > 0, s2.cause.class, bt(s2)]
end
probe("bt_loop_kept") { bt_loop_kept }

def bt_full_message
  e = [].each
  begin
    e.next
  rescue StopIteration => s
    s.full_message(highlight: false).lines.first.sub(/\A.*extiter-matrix\.rb:/, "")
  end
end
probe("bt_full_message") { bt_full_message }

probe("bt_locations") do
  e = [].each
  begin
    e.next
  rescue StopIteration => s
    s.backtrace_locations.first(2).map { |l| [l.lineno, l.label] }
  end
end

# a StopIteration kept by a rescue, the frame left, then read
def keep_stop
  e = [].each
  begin
    e.next
  rescue StopIteration => s
    $kept = s
  end
  nil
end
probe("bt_kept_late") { keep_stop; bt($kept) }

def raise_again
  e = [].each
  begin
    e.next
  rescue StopIteration
    raise
  end
end
probe("bt_reraise") { begin; raise_again; rescue StopIteration => s; bt(s); end }

def ensure_pass
  e = [].each
  begin
    e.next
  ensure
    $ens = 1
  end
end
probe("bt_through_ensure") { begin; ensure_pass; rescue StopIteration => s; [bt(s), $ens]; end }

probe("stop_cause_of_new") do
  e = [].each
  begin
    begin
      e.next
    rescue StopIteration
      raise ArgumentError, "x"
    end
  rescue ArgumentError => a
    [a.cause.class, bt(a.cause)]
  end
end

# --- peek, next_values, peek_values ---------------------------------------------------------
probe("peek")         { e = [1, 2].each; [e.peek, e.peek, e.next, e.peek, e.next, (e.peek rescue $!.class)] }
probe("peek_mutate")  { e = [[1]].each; v = e.peek; v << 2; [e.next] }
probe("next_values")  { e = [1, nil, [2]].each; [e.next_values, e.next_values, e.next_values] }
probe("peek_values")  { e = [1].each; v = e.peek_values; v << 9; [e.peek_values, e.next_values] }
probe("nv_hash")      { e = { a: 1 }.each; [e.peek_values, e.next_values] }
probe("nv_ewi")       { e = [:a].each_with_index; [e.peek_values, e.next_values] }
probe("nv_generator") { e = Enumerator.new { |y| y.yield; y.yield 1; y.yield 1, 2; y.yield nil }; [e.next_values, e.next_values, e.next_values, e.next_values] }
probe("next_gen_multi") { e = Enumerator.new { |y| y.yield; y.yield 1, 2 }; [e.next, e.next] }

# --- rewind, feed, size -------------------------------------------------------------------
probe("rewind")       { e = [1, 2].each; a = e.next; e.rewind; [a, e.next, e.next] }
probe("rewind_done")  { e = [1].each; e.next; (e.next rescue nil); e.rewind; [e.next] }
probe("rewind_gen")   { e = Enumerator.new { |y| y << 1; y << 2 }; e.next; e.rewind; [e.next, e.next] }
probe("rewind_obj") do
  o = Object.new
  def o.each; yield 1; yield 2; end
  def o.rewind; $rewound = true; end
  $rewound = false
  e = o.to_enum
  e.next
  e.rewind
  [$rewound, e.next]
end
probe("feed_map")     { e = [1, 2].map; e.next; e.feed :a; e.next; e.feed :b; stop_of(e).result }
probe("feed_select")  { e = [1, 2, 3].select; e.next; e.feed true; e.next; e.next; e.feed 1; stop_of(e).result }
probe("feed_each")    { e = [1, 2].each; e.next; e.feed :x; e.next; stop_of(e).result }
probe("feed_twice")   { e = [1].each; e.feed 1; (e.feed 2 rescue "#{$!.class}: #{$!.message}") }
probe("feed_gen")     { got = []; e = Enumerator.new { |y| got << y.yield(1); got << y.yield(2) }; e.next; e.feed :f; e.next; (e.next rescue nil); got }
probe("size")         { [[1, 2].each.size, (1..4).each.size, 3.times.size, {a: 1}.each.size, Enumerator.new { }.size, [1].each_slice(2).size] }
probe("size_after_next") { e = [1, 2, 3].each; e.next; e.size }

# --- an Array changed during the iteration: Array#each reads the length at each step ------
probe("append") do
  a = [1, 2]
  e = a.each
  out = [e.next]
  a << 3
  out << e.next << e.next
  out << (e.next rescue :stop)
end
probe("append_after_end") { a = [1]; e = a.each; e.next; (e.next rescue nil); a << 2; (e.next rescue :stop) }
probe("shrink")       { a = [1, 2, 3]; e = a.each; x = e.next; a.clear; [x, (e.next rescue :stop)] }
probe("replace")      { a = [1, 2, 3]; e = a.each; x = e.next; a[1] = :z; [x, e.next] }
probe("unshift")      { a = [1, 2]; e = a.each; x = e.next; a.unshift(0); [x, e.next, e.next] }
probe("hash_add") do
  h = { a: 1 }
  e = h.each
  e.next
  begin
    h[:b] = 2
    :added
  rescue => x
    x.class
  end
end
probe("hash_delete")  { h = { a: 1, b: 2, c: 3 }; e = h.each; x = e.next; h.delete(:b); [x, e.next, (e.next rescue :stop)] }

# --- a redefined or prepended #each, before and after the enumerator is made ---------------
class RedefA < Array
end
probe("subclass_each") do
  c = Class.new(Array) { def each; yield :sub; self; end }
  drain(c.new([1, 2]).each)
end
probe("singleton_each") do
  a = [1, 2]
  def a.each; yield :single; self; end
  drain(a.each)
end
probe("redefined_before_next") do
  a = RedefA.new([1, 2])
  e = a.each
  RedefA.class_eval { def each; yield :late; self; end }
  r = drain(e)
  RedefA.class_eval { remove_method :each }
  r
end
probe("redefined_during") do
  a = RedefA.new([1, 2, 3])
  e = a.each
  x = e.next
  RedefA.class_eval { def each; yield :late; self; end }
  r = [x, e.next, e.next]
  RedefA.class_eval { remove_method :each }
  r
end
module PrependEach
  def each
    return to_enum(:each) unless block_given?
    super { |x| yield x * 10 }
  end
end
class PrepA < Array; end
probe("prepended_before") do
  PrepA.prepend(PrependEach)
  drain(PrepA.new([1, 2]).each)
end
class PrepB < Array; end
probe("prepended_after_make") do
  e = PrepB.new([1, 2]).each
  PrepB.prepend(PrependEach)
  drain(e)
end
probe("hash_redef") { h = { a: 1 }; def h.each; yield :h; self; end; drain(h.each) }
probe("range_redef") { r = (1..2); def r.each; yield :r; self; end; drain(r.each) }
probe("to_enum_each") { a = [1, 2]; drain(a.to_enum) }
probe("enum_for_args") { drain([1, 2, 3].enum_for(:each_slice, 2)) }

# --- each_slice / map and friends on an enumerator ---------------------------------------------
probe("each_slice_next") { e = [1, 2, 3].each_slice(2); [e.next, e.next, (e.next rescue :stop)] }
probe("map_next")     { e = [1, 2].map; [e.next, e.next, (e.next rescue :stop)] }
probe("map_with_index") { drain([5, 6].map.with_index) }
probe("each_with_object") { drain([1, 2].each_with_object([])) }
probe("ewi_on_hash")  { drain({ a: 1 }.each_with_index) }
probe("lazy_select")  { e = (1..Float::INFINITY).lazy.select(&:even?).map { |x| x * 3 }; [e.next, e.next, e.peek] }
probe("lazy_take")    { drain((1..).lazy.map { |x| x + 1 }.take(3)) }
probe("lazy_size")    { [(1..3).lazy.map { |x| x }.size, (1..3).lazy.select { true }.size] }
probe("yielder_proc") { drain(Enumerator.new { |y| [1, 2].each(&y) }) }
probe("yielder_chain"){ drain(Enumerator.new { |y| y << 1 << 2 }) }

# --- exceptions inside the source ---------------------------------------------------------------
probe("gen_raises") do
  e = Enumerator.new { |y| y << 1; raise ArgumentError, "boom" }
  [e.next, (e.next rescue "#{$!.class}: #{$!.message}"), e.next]
end

# --- copies, frozen, inspect ------------------------------------------------------------------
probe("dup_fresh")    { e = [1, 2].each; d = e.dup; [d.next, e.next] }
probe("dup_started")  { e = [1, 2].each; e.next; (e.dup rescue "#{$!.class}: #{$!.message}") }
probe("dup_done")     { e = [1].each; e.next; (e.next rescue nil); d = e.dup; [d.next] }
probe("frozen")       { e = [1].each.freeze; (e.next rescue $!.class) }
probe("inspect_started") { e = [1, 2].each; e.next; e.inspect }

# --- threads: a cursor belongs to the thread that started it ----------------------------------
probe("thread_fresh") { e = [1, 2].each; Thread.new { e.next }.value }
probe("thread_started") do
  e = [1, 2, 3].each
  e.next
  Thread.new { begin; e.next; rescue => x; "#{x.class}: #{x.message}"; end }.value
end
probe("thread_peeked") do
  e = [1, 2, 3].each
  e.peek
  Thread.new { begin; e.next; rescue => x; "#{x.class}: #{x.message}"; end }.value
end
probe("thread_done") do
  e = [1].each
  e.next
  (e.next rescue nil)
  Thread.new { begin; e.next; rescue => x; [x.class, x.cause.class]; end }.value
end
probe("thread_gen") do
  e = Enumerator.new { |y| y << 1; y << 2 }
  e.next
  Thread.new { begin; e.next; rescue => x; "#{x.class}: #{x.message}"; end }.value
end
probe("thread_after") { e = [1, 2].each; Thread.new { e.next }.join; (e.next rescue "#{$!.class}: #{$!.message}") }
probe("thread_each_own") do
  e = [1, 2, 3].each
  4.times.map { Thread.new { f = e.dup; [f.next, f.next] }.value }.uniq
end

# --- an abandoned enumerator ------------------------------------------------------------------
probe("abandoned") do
  50.times { e = [1, 2, 3].each; e.next }
  50.times { e = Enumerator.new { |y| y << 1; y << 2 }; e.next }
  GC.start
  :ok
end

# --- zip and friends that take values with #next -----------------------------------------------
probe("zip_enum")     { [1, 2, 3].zip([4, 5].each) }
probe("zip_gen")      { [1, 2].zip(Enumerator.new { |y| y << :a }) }
probe("zip_block")    { r = []; [1, 2].zip((3..4).each) { |x| r << x }; r }

# --- Kernel#loop reads the result of the StopIteration it rescues -------------------------------
class MyStop < StopIteration
  def result = [:mine, super]
end
probe("loop_sub_result") { loop { raise MyStop } }
probe("loop_sub_kept") do
  $seen = nil
  c = Class.new(StopIteration) { def result; $seen = self; :c; end }
  r = loop { raise c, "kept" }
  [r, $seen.class == c, $seen.message, bt($seen).size > 0]
end
probe("loop_singleton_result") do
  s = StopIteration.new("s")
  def s.result = :single
  loop { raise s }
end
probe("loop_redefined_result") do
  StopIteration.class_eval { alias_method :__matrix_result__, :result; remove_method :result; def result = [:redef, __matrix_result__] }
  e = [1].each
  r = loop { e.next }
  StopIteration.class_eval { remove_method :result; alias_method :result, :__matrix_result__; remove_method :__matrix_result__ }
  r
end
probe("loop_after_restore") { e = [1].each; loop { e.next } }
probe("loop_dollar_bang") { e = [].each; loop { e.next }; $!.inspect }
probe("loop_inner_rescue") { e = [].each; loop { begin; e.next; rescue StopIteration => s; break [s.class, s.result.size]; end } }
probe("loop_ensure") { x = []; e = [1].each; r = loop { begin; e.next; ensure; x << $!.class; end }; [r, x] }
probe("loop_tracepoint") do
  seen = []
  tp = TracePoint.new(:raise) { |t| seen << t.raised_exception }
  e = [1].each
  r = tp.enable { loop { e.next } }
  s = seen.last
  [r, seen.size <= 1, s.nil? || bt(s).size > 0]
end
probe("loop_kept_cause_later") do
  e = [1].each
  loop { e.next }
  s = (e.next rescue $!)
  [s.cause.result, bt(s.cause).first.sub(/\A\d+:/, "")]
end

# the first end dropped three frames down, its frames gone, the cause read from a block elsewhere
def deep_drop(e)
  begin; e.next; rescue StopIteration; end
  nil
end
def deep_d3(e) = deep_drop(e)
def deep_d2(e) = deep_d3(e)
def deep_d1(e) = deep_d2(e)
def deep_later(e)
  [1].each { s = (e.next rescue $!); return [bt(s.cause), bt(s)] }
end
probe("bt_cause_deep") { e = [1].each; e.next; deep_d1(e); deep_later(e) }
probe("bt_cause_deep_hot") { r = nil; 100.times { e = [1].each; e.next; deep_d1(e); r = deep_later(e) }; r }
