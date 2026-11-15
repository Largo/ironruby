# Concurrent::Map#[] / #[]= / fetch_or_store, as Rails' caches use them; least-of-5 CPU per op.
#   GEM_HOME=... ir.sh Util/perf/areas/rails/cmap.rb       (needs concurrent-ruby installed)
require "concurrent/map"
m = Concurrent::Map.new
keys = (0...64).map { |i| "k#{i}".freeze }
keys.each { |k| m[k] = k }
N = 400_000
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
{ "[]" => -> { i = 0; while i < N; m[keys[i & 63]]; i += 1; end },
  "[]=" => -> { i = 0; while i < N; m[keys[i & 63]] = i; i += 1; end },
  "fetch_or_store" => -> { i = 0; while i < N; m.fetch_or_store(keys[i & 63]) { 1 }; i += 1; end },
  "compute_if_absent" => -> { i = 0; while i < N; m.compute_if_absent(keys[i & 63]) { 1 }; i += 1; end },
}.each do |name, f|
  f.call
  best = 5.times.map { t = cpu; f.call; cpu - t }.min
  printf "%-18s %6.1f ns/op\n", name, best * 1e9 / N
end
p m.size
