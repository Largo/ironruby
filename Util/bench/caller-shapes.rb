# Kernel#caller, #caller_locations and what walks the stack the way they do - the default file name of an
# eval ("(eval at file:line)"), warn(uplevel:), a rescued exception's backtrace - at 5 and 50 Ruby frames
# deep, ns per call, to put next to CRuby (gap 31 of Util/perf/LEDGER.md). Their cost grows with the
# depth of the stack, the CLR's frames below the Ruby code included.
#
#   IR_CONFIG=Release ./ir.sh Util/bench/caller-shapes.rb
#   ruby Util/bench/caller-shapes.rb
#   ... caller-shapes.rb loc1_5 eval_50     # only these
#   ... caller-shapes.rb --list             # the names
#   ruby Util/perf/ab.rb Util/bench/caller-shapes.rb cruby=ruby "base=..." "new=..."   # two builds, taking turns
#
# Every line prints the sum of its results next to its time; it must be CRuby's.
$stdout.sync = true
require 'stringio'

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end

# INSTR=1 (Util/perf/ab.rb -i) adds a column: the instructions of a call, from the least of the
# seven sections as the time is. See Util/perf/instructions.rb.
INSTR = ENV["INSTR"] && (require_relative "../perf/instructions"; Instructions.open || warn("no instruction counter (perf_event_open)"))

# The shape runs `depth' frames deep: each level a method calling the next.
def deep(depth, &b) = depth == 0 ? b.call : deep(depth - 1, &b)

def t(name, n, depth)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; fewest = nil; counted = !!INSTR; sum = nil
  deep(depth) do
    7.times do
      c0 = INSTR && INSTR.read; t0 = cpu; sum = yield n; d = cpu - t0; c1 = INSTR && INSTR.read
      best = d if d < best
      if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
    end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end

N = (ENV["N"] || 2_000).to_i
$stderr_sink = StringIO.new

[5, 50].each do |d|
  t("loc1_#{d}", N, d) { |n| i = 0; s = 0; while i < n; s += caller_locations(1, 1)[0].lineno; i += 1; end; s }
  t("caller1_#{d}", N, d) { |n| i = 0; s = 0; while i < n; s += caller(1, 1)[0].size; i += 1; end; s }
  t("caller0_#{d}", N, d) { |n| i = 0; s = 0; while i < n; s += caller(0).size; i += 1; end; s }
  t("eval_#{d}", N, d) { |n| i = 0; s = 0; while i < n; s += eval('1 + 1'); i += 1; end; s }
  t("eval_file_#{d}", N, d) { |n| i = 0; s = 0; while i < n; s += eval('1 + 1', nil, 'f.rb', 1); i += 1; end; s }
  t("warn_up_#{d}", N, d) do |n|
    old = $stderr; $stderr = $stderr_sink
    i = 0; s = 0; while i < n; $stderr_sink.truncate(0); $stderr_sink.rewind; warn('x', uplevel: 1); s += $stderr_sink.size; i += 1; end
    $stderr = old
    s
  end
  t("raise_bt_#{d}", N, d) { |n| i = 0; s = 0; while i < n; begin; raise 'x'; rescue => e; s += e.backtrace.size; end; i += 1; end; s }
end
