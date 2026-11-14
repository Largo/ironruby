# CPU seconds a fixed amount of each Are-We-Fast-Yet benchmark's work takes, on several trees.
#
#   ruby Util/perf/cputime.rb ROUNDS NAME=CMD... [-- Benchmark...]
#   ruby Util/perf/cputime.rb 5 "base=env IR_CONFIG=Release /abs/base/ir.sh" \
#        "new=env IR_CONFIG=Release /abs/new/ir.sh" "base2=env IR_CONFIG=Release /abs/base/ir.sh"
#
# Each benchmark runs a fixed number of iterations in a fresh process; the figure is the
# least user + system CPU time of ROUNDS runs, the interpreters taken in turn within each
# round. Use it when Util/perf/awfy.rb's wall-clock medians will not hold still: on a host
# shared with other tenants a benchmark's median moved by 30% between two runs of one
# build, while the least CPU time of a fixed run moves by a percent or two. Give the
# baseline twice (as above): the distance between its two columns is the noise, and a
# difference smaller than that is not a result.
#
# It is not a replacement for awfy.rb: start-up and warm-up are in the figure (about a
# second of each run), so it understates a steady-state gain, and CPU time counts the JIT's
# and the GC's background threads, which wall-clock time on an idle machine does not.
require 'shellwords'
OUTER = { 'DeltaBlue' => [150, 1200], 'Richards' => [30, 10], 'Json' => [100, 10], 'Bounce' => [100, 150],
          'Towers' => [80, 60], 'CD' => [8, 100], 'List' => [60, 150], 'Mandelbrot' => [20, 500],
          'NBody' => [10, 250_000], 'Permute' => [60, 100], 'Queens' => [60, 100], 'Sieve' => [100, 300],
          'Storage' => [60, 100] }
root = File.expand_path('../..', __dir__)
home = ENV['PERF_HOME'] || File.join(File.dirname(root), 'ironruby-perf')
harness = File.join(home, 'awfy/benchmarks/Ruby/harness.rb')
abort "no #{harness}: run Util/perf/setup.sh" unless File.file?(harness)

rounds = Integer(ARGV.shift)
i = ARGV.index('--')
only = i ? ARGV[i + 1..] : %w[DeltaBlue Richards Json Bounce Towers]
interps = (i ? ARGV[0...i] : ARGV).map { |s| s.split('=', 2) }
fmt = "%-11s" + " %10s" * interps.size + "\n"
printf fmt, 'cpu s', *interps.map(&:first)
cols = Array.new(interps.size) { [] }
only.each do |b|
  outer, inner = OUTER.fetch(b)
  best = Array.new(interps.size)
  rounds.times do
    interps.each_with_index do |(_, cmd), k|
      before = Process.times
      ok = system("LANG=C.UTF-8 #{cmd} #{harness.shellescape} #{b} #{outer} #{inner} > /dev/null 2>&1")
      after = Process.times
      # a run that failed - a wrong answer raises - is not a sample
      next best[k] = :fail unless ok
      t = (after.cutime - before.cutime) + (after.cstime - before.cstime)
      best[k] = t if best[k].nil? || (best[k] != :fail && t < best[k])
    end
  end
  best.each_with_index { |v, k| cols[k] << (v == :fail ? nil : v) }
  printf fmt, b, *best.map { |v| v == :fail ? 'FAIL' : format('%.2f', v) }
  $stdout.flush
end
interps.each_with_index do |(name, _), k|
  r = cols[k].zip(cols[0]).select { |a, c| a && c && c > 0 }.map { |a, c| a / c }
  printf "geomean %s/%s = %.3f\n", name, interps[0][0], Math.exp(r.sum { |x| Math.log(x) } / r.size) unless r.empty?
end
