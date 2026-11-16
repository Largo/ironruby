# Steady-state time of Are-We-Fast-Yet benchmarks on several interpreters, side by side.
#
#   ruby Util/perf/awfy.rb SECS NAME=CMD... [-- Benchmark...]
#   ruby Util/perf/awfy.rb 12 cruby=ruby "ir=env IR_CONFIG=Release ./ir.sh" \
#        "truffle=$PERF_HOME/truffleruby/bin/truffleruby" -- Bounce Richards
#
# Each benchmark runs on each interpreter for SECS seconds (AWFY's harness with a large
# outer-iteration count, killed at the deadline); the figure is the median of the second
# half of the iterations it printed, in microseconds, so warm-up (TruffleRuby's compiler,
# IronRuby's JIT tiers) is out of it. Lower is better. The last lines are each column's
# geomean relative to the first column. A benchmark raises on a wrong answer, which shows
# as FAIL: a fast wrong answer never gets a number.
#
# The inner-iteration counts are Util/benchsuite/catalog.rb's (AWFY's rebench.conf / 10).
#
# These thirteen are the default. The fourteenth, Havlak, makes the suite whole and is run only
# when asked for - `--whole` anywhere before `--`, or its name after it:
#
#   ruby Util/perf/awfy.rb 12 --whole "base=env IR_CONFIG=Release ./ir.sh" "new=env IR_CONFIG=Release ../wt-x/ir.sh"
#
# AWFY's som.rb reopens Set, which Ruby 4.0 made a core class, so CRuby 4.0 fails it (a FAIL in
# its column, and no part in its geomean); IronRuby and TruffleRuby run it. One iteration takes
# seconds whatever the inner count, so it gets at least a minute per interpreter: a whole run
# of two trees is about seven minutes, the default about five. Use it when deciding on a
# change and after a merge, not while iterating.
INNER = { 'DeltaBlue' => 1200, 'Richards' => 10, 'Json' => 10, 'CD' => 100, 'Bounce' => 150,
          'List' => 150, 'Mandelbrot' => 500, 'NBody' => 250_000, 'Permute' => 100,
          'Queens' => 100, 'Sieve' => 300, 'Storage' => 100, 'Towers' => 60 }
WHOLE = { 'Havlak' => 15 }
SLOW_SECS = 60
require 'shellwords'
root = File.expand_path('../..', __dir__)
home = ENV['PERF_HOME'] || File.join(File.dirname(root), 'ironruby-perf')
dir = File.join(home, 'awfy/benchmarks/Ruby')
abort "no #{dir}: run Util/perf/setup.sh" unless File.directory?(dir)
# CRuby buffers stdout on a pipe and the run ends with SIGKILL: without this it prints nothing.
sync = File.join(__dir__, 'sync.rb')

secs = Float(ARGV.shift)
i = ARGV.index('--')
only = i ? ARGV[i + 1..] : []
whole = (i ? ARGV[0...i] : ARGV).include?('--whole')
ARGV.delete_at(ARGV.index('--whole')) if whole
i = ARGV.index('--')
interps = (i ? ARGV[0...i] : ARGV).map { |s| s.split('=', 2) }
fmt = "%-11s" + " %12s" * interps.size + "\n"
printf fmt, 'benchmark', *interps.map(&:first)
cols = Array.new(interps.size) { [] }
INNER.merge(WHOLE).each do |b, inner|
  next unless only.empty? ? (whole || !WHOLE.key?(b)) : only.include?(b)
  limit = WHOLE.key?(b) ? [secs, SLOW_SECS].max : secs
  row = interps.map do |name, cmd|
    # cmd is a command line, as documented; the paths this script adds are quoted (a checkout or PERF_HOME with a space)
    started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
    out = IO.popen("LANG=C.UTF-8 timeout -s KILL #{limit} #{cmd} #{"-r#{sync}".shellescape} #{File.join(dir, 'harness.rb').shellescape} #{b} 100000 #{inner} 2>&1", &:read)
    # The run is meant to end at the deadline, killed (137 from timeout). One that ended by itself with an
    # error - a wrong answer raises - is a failure however many iterations it printed first, and so is one
    # killed before the deadline (the OOM killer, a kill -9 from elsewhere), which is 137 or a signal too.
    st = $?
    at_deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) - started >= limit - 0.5
    ended_as_meant = st.exitstatus == 0 || at_deadline && (st.signaled? || st.exitstatus == 137)
    t = out.scan(/runtime: (\d+)us/).flatten.map(&:to_i)
    if t.empty? || !ended_as_meant
      # what the run said last is the only account of why there is no number
      $stderr.puts "#{b} on #{name}: #{st.signaled? ? "signal #{st.termsig}" : "exit #{st.exitstatus}"}#{" before the deadline" unless at_deadline}, #{t.size} iterations; its last lines:"
      out.lines.last(6).each { |l| $stderr.puts "  #{l.chomp}" }
      next nil
    end
    t[t.size / 2..].sort.then { |h| h[h.size / 2] }
  end
  row.each_with_index { |v, k| cols[k] << v }
  printf fmt, b, *row.map { |v| v || 'FAIL' }
  $stdout.flush
end
interps.each_with_index do |(name, _), k|
  r = cols[k].zip(cols[0]).select { |a, b| a && b }.map { |a, b| a.to_f / b }
  printf "geomean %s/%s = %.3f\n", name, interps[0][0], Math.exp(r.sum { |x| Math.log(x) } / r.size) unless r.empty?
end
