# Bytes allocated per iteration of Are-We-Fast-Yet benchmarks on one or more IronRuby trees.
#
#   ruby Util/perf/alloc.rb NAME=CMD... [-- Benchmark...]
#   ruby Util/perf/alloc.rb "base=env IR_CONFIG=Release /home/user/ironruby/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh" -- DeltaBlue Json
#
# Each benchmark runs twice per tree, for LO and HI outer iterations (inner counts as in
# Util/perf/awfy.rb), and prints System::GC.GetTotalAllocatedBytes(true) at exit; the figure
# is (bytes(HI) - bytes(LO)) / (HI - LO), so start-up, loading and JIT warm-up cancel out.
# Unlike time it does not depend on the machine's load: a difference of a few bytes per
# iteration is real. The last lines give each column's geomean relative to the first.
# ALLOC_LO / ALLOC_HI override the iteration counts (default 20 and 60).
require 'shellwords'
INNER = { 'DeltaBlue' => 1200, 'Richards' => 10, 'Json' => 10, 'CD' => 100, 'Bounce' => 150,
          'List' => 150, 'Mandelbrot' => 500, 'NBody' => 250_000, 'Permute' => 100,
          'Queens' => 100, 'Sieve' => 300, 'Storage' => 100, 'Towers' => 60 }
root = File.expand_path('../..', __dir__)
home = ENV['PERF_HOME'] || File.join(File.dirname(root), 'ironruby-perf')
dir = File.join(home, 'awfy/benchmarks/Ruby')
abort "no #{dir}: run Util/perf/setup.sh" unless File.directory?(dir)
lo = Integer(ENV['ALLOC_LO'] || 20)
hi = Integer(ENV['ALLOC_HI'] || 60)

require 'tmpdir'
probe = File.join(Dir.tmpdir, "ir-alloc-probe-#{Process.pid}.rb")
File.write(probe, "at_exit { $stderr.puts \"ALLOCATED #{'#{'}System::GC.get_total_allocated_bytes(true)}\" }\n")
at_exit { File.delete(probe) if File.exist?(probe) }

i = ARGV.index('--')
only = i ? ARGV[i + 1..] : []
interps = (i ? ARGV[0...i] : ARGV).map { |s| s.split('=', 2) }
fmt = "%-11s" + " %14s" * interps.size + "\n"
printf fmt, 'bytes/iter', *interps.map(&:first)
cols = Array.new(interps.size) { [] }
INNER.each do |b, inner|
  next unless only.empty? || only.include?(b)
  row = interps.map do |_, cmd|
    n = [lo, hi].map do |iters|
      out = IO.popen("LANG=C.UTF-8 #{cmd} #{"-r#{probe}".shellescape} #{File.join(dir, 'harness.rb').shellescape} #{b} #{iters} #{inner} 2>&1", &:read)
      # The probe prints from at_exit, so a run that raised - a wrong answer does - prints a count too:
      # only a run that ended with status 0 is a sample.
      out[/ALLOCATED (\d+)/, 1]&.to_i if $?.success? && out =~ /#{b}: iterations=/
    end
    n.all? ? (n[1] - n[0]) / (hi - lo) : nil
  end
  row.each_with_index { |v, k| cols[k] << v }
  printf fmt, b, *row.map { |v| v || 'FAIL' }
  $stdout.flush
end
interps.each_with_index do |(name, _), k|
  r = cols[k].zip(cols[0]).select { |a, b| a && b && b > 0 }.map { |a, b| a.to_f / b }
  # A benchmark that allocates next to nothing can come out at or below zero (the two runs' start-up
  # differs by more than its iterations allocate): it has no ratio, and is left out of the geomean.
  r = r.select { |x| x.finite? && x > 0 }
  printf "geomean %s/%s = %.3f\n", name, interps[0][0], Math.exp(r.sum { |x| Math.log(x) } / r.size) unless r.empty?
end
