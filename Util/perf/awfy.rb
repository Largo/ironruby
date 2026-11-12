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
# Havlak is left out: AWFY's som.rb reopens Set, which fails on Ruby 4.0 itself.
INNER = { 'DeltaBlue' => 1200, 'Richards' => 10, 'Json' => 10, 'CD' => 100, 'Bounce' => 150,
          'List' => 150, 'Mandelbrot' => 500, 'NBody' => 250_000, 'Permute' => 100,
          'Queens' => 100, 'Sieve' => 300, 'Storage' => 100, 'Towers' => 60 }
root = File.expand_path('../..', __dir__)
home = ENV['PERF_HOME'] || File.join(File.dirname(root), 'ironruby-perf')
dir = File.join(home, 'awfy/benchmarks/Ruby')
abort "no #{dir}: run Util/perf/setup.sh" unless File.directory?(dir)
# CRuby buffers stdout on a pipe and the run ends with SIGKILL: without this it prints nothing.
sync = File.join(__dir__, 'sync.rb')

secs = Float(ARGV.shift)
i = ARGV.index('--')
only = i ? ARGV[i + 1..] : []
interps = (i ? ARGV[0...i] : ARGV).map { |s| s.split('=', 2) }
fmt = "%-11s" + " %12s" * interps.size + "\n"
printf fmt, 'benchmark', *interps.map(&:first)
cols = Array.new(interps.size) { [] }
INNER.each do |b, inner|
  next unless only.empty? || only.include?(b)
  row = interps.map do |_, cmd|
    out = IO.popen("LANG=C.UTF-8 timeout -s KILL #{secs} #{cmd} -r#{sync} #{dir}/harness.rb #{b} 100000 #{inner} 2>&1", &:read)
    t = out.scan(/runtime: (\d+)us/).flatten.map(&:to_i)
    t.empty? ? nil : t[t.size / 2..].sort.then { |h| h[h.size / 2] }
  end
  row.each_with_index { |v, k| cols[k] << v }
  printf fmt, b, *row.map { |v| v || 'FAIL' }
  $stdout.flush
end
interps.each_with_index do |(name, _), k|
  r = cols[k].zip(cols[0]).select { |a, b| a && b }.map { |a, b| a.to_f / b }
  printf "geomean %s/%s = %.3f\n", name, interps[0][0], Math.exp(r.sum { |x| Math.log(x) } / r.size) unless r.empty?
end
