# Times each operation of app-shapes.rb's `set` alone (Set is core in CRuby 4.0, set.c, and
# Ruby in IronRuby, Src/StdLib/ironruby/set.rb). Prints `<shape> <ns> ns <checksum>`; the
# checksums must equal CRuby's.
#
#   ruby Util/bench/set-shapes.rb [shape...]
#   ruby Util/perf/ab.rb Util/bench/set-shapes.rb cruby=ruby "new=env IR_CONFIG=Release ./ir.sh"
$stdout.sync = true
def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
# INSTR=1 (Util/perf/ab.rb -i) adds a column: the instructions of a call, from the least of the
# seven sections as the time is. See Util/perf/instructions.rb.
INSTR = ENV["INSTR"] && (require_relative "../perf/instructions"; Instructions.open || warn("no instruction counter (perf_event_open)"))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; fewest = nil; counted = !!INSTR; sum = nil
  7.times do
    c0 = INSTR && INSTR.read; t0 = cpu; sum = yield n; d = cpu - t0; c1 = INSTR && INSTR.read
    best = d if d < best
    # a read is nil once the counter is lost: then there is no count, rather than one of the sections that had it
    if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end
N = (ENV["N"] || 1000).to_i
def n(c) = [c * N / 1000, 1].max
A = Set.new(1..200)
B = Set.new((100..300).step(2))
D = A & B
R = (1..200)
AR = R.to_a
EN = (100..300).step(2)
S3 = Set[1, 2, 3]
W = %w[apple pear apple plum fig pear kiwi fig]
T = Set[1000]
t("new_range", n(300))   { |n| s = 0; n.times { s += Set.new(R).size }; s }
t("new_step", n(300))    { |n| s = 0; n.times { s += Set.new(EN).size }; s }
t("new_array", n(300))   { |n| s = 0; n.times { s += Set.new(AR).size }; s }
t("new_empty", n(30000)) { |n| s = 0; n.times { s += Set.new.size }; s }
t("brackets", n(30000))  { |n| s = 0; n.times { s += Set[1, 2, 3].size }; s }
t("union", n(300))       { |n| s = 0; n.times { s += (A | B).size }; s }
t("minus", n(300))       { |n| s = 0; n.times { s += (A - S3).size }; s }
t("and", n(300))         { |n| s = 0; n.times { s += (A & B).size }; s }
t("xor", n(300))         { |n| s = 0; n.times { s += (A ^ B).size }; s }
t("subset", n(3000))     { |n| s = 0; n.times { s += 1 if D.subset?(A) }; s }
t("superset", n(3000))   { |n| s = 0; n.times { s += 1 if A.superset?(D) }; s }
t("disjoint", n(30000))  { |n| s = 0; n.times { s += 1 if A.disjoint?(T) }; s }
t("add_words", n(10000)) { |n| s = 0; n.times { w = Set.new; W.each { |x| w << x }; s += w.size }; s }
t("include", n(100000))  { |n| s = 0; n.times { s += 1 if A.include?(150) }; s }
t("each", n(1000))       { |n| s = 0; n.times { A.each { |x| s += x } }; s }
t("select_sum", n(1000)) { |n| s = 0; n.times { s += A.select(&:even?).sum }; s }
t("map_sum", n(1000))    { |n| s = 0; n.times { s += D.map { |x| x * 2 }.sum }; s }
t("size", n(100000))     { |n| s = 0; n.times { s += A.size }; s }
