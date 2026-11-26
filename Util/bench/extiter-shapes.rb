# External iteration (Enumerator#next, #peek) over sources whose #each needs no fiber, and controls
# that keep theirs (LEDGER gap 35). Each shape makes an enumerator and takes ITEMS values from it with
# #next, so a row is the cost of one #next (the enumerator's making spread over the items).
$stdout.sync = true
# Process CPU, not the thread's: a fiber is another thread here, and its work is the point.
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; sum = nil
  7.times do
    t0 = cpu; sum = yield n; d = cpu - t0
    best = d if d < best
  end
  printf "%-16s %9.1f ns  %d\n", name, best * 1e9 / n, sum
end
N = (ENV["N"] || 1000).to_i
def n(c) = [c * N / 1000, 1].max

SMALL = [1, 2, 3, 4]
LARGE = (1..1000).to_a
HSH = (1..100).to_h { |i| [i, i * 2] }

# all of e's values with #next: the loop ends on StopIteration, as loop { } does
def drain(e)
  s = 0
  begin
    while true
      v = e.next
      s += v.is_a?(Array) ? v[0] + v[1].to_i : v
    end
  rescue StopIteration
  end
  s
end

def peeks(e)
  s = 0
  begin
    while true
      s += e.peek
      e.next
    end
  rescue StopIteration
  end
  s
end

# per #next: the count is in items
t("ary_small", n(200_000))   { |n| s = 0; (n / 4).times { s += drain(SMALL.each) }; s }
t("ary_large", n(200_000))   { |n| s = 0; (n / 1000).times { s += drain(LARGE.each) }; s }
t("range", n(200_000))       { |n| s = 0; (n / 1000).times { s += drain((1..1000).each) }; s }
t("hash", n(100_000))        { |n| s = 0; (n / 100).times { s += drain(HSH.each) }; s }
t("loop_small", n(200_000)) { |n| s = 0; (n / 4).times { e = SMALL.each; loop { s += e.next } }; s }
t("rescue_end", n(20_000))   { |n| s = 0; e = SMALL.each; n.times { begin; e.next; rescue StopIteration; s += 1; end }; s }
# a control: Array#zip takes an enumerator's items with #each (MRI's take_items), not with #next
t("zip_short", n(20_000))    { |n| s = 0; (n / 8).times { SMALL.zip(SMALL.each, [1, 2].each) { |a, b, c| s += a + b + c.to_i } }; s }
t("peek", n(100_000))        { |n| s = 0; (n / 1000).times { s += peeks(LARGE.each) }; s }
t("map_next", n(20_000))     { |n| s = 0; (n / 1000).times { s += drain(LARGE.map) }; s }
t("ewi_next", n(20_000))     { |n| s = 0; (n / 1000).times { s += drain(LARGE.each.with_index) }; s }
t("select_next", n(20_000))  { |n| s = 0; (n / 1000).times { s += drain(LARGE.select) }; s }
t("lazy_next", n(5_000))     { |n| s = 0; (n / 1000).times { s += drain(LARGE.lazy.map { |x| x + 1 }) }; s }
t("produce", n(20_000))      { |n| s = 0; (n / 1000).times { e = Enumerator.produce(1) { |x| x + 1 }; 1000.times { s += e.next } }; s }
t("zip_enum", n(20_000))     { |n| s = 0; (n / 1000).times { LARGE.zip(LARGE.each) { |a, b| s += a + b } }; s }
# controls: these keep their fiber (a generator; map, select, with_index, lazy and produce above too)
t("generator", n(20_000))    { |n| s = 0; (n / 1000).times { s += drain(Enumerator.new { |y| LARGE.each { |x| y << x } }) }; s }
