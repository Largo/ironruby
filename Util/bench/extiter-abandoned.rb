# Enumerators that are abandoned after one #next: threads and memory left once they are collected
# (LEDGER gap 35). A fiber-backed enumerator here is a thread; its handle's finalizer unwinds the
# thread once nothing reaches the handle, which a thread's own stack cannot see past: when a block
# that runs on the fiber closes over a scope that holds the enumerator, the thread keeps it alive.
#
#   ./ir.sh Util/bench/extiter-abandoned.rb KIND [COUNT]      # one kind per process (COUNT 10,000)
#   for k in $(ruby Util/bench/extiter-abandoned.rb); do ./ir.sh Util/bench/extiter-abandoned.rb $k; done
#
# CRuby collects every kind: 2 threads and about 20 MB at the end.
$stdout.sync = true
def st = File.read("/proc/self/status").scan(/^(VmRSS|Threads):\s+(\d+)/).to_h
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
def make_lazy = [1, 2, 3].lazy.map { |x| x * 2 }
def make_lazy_sym = (1..Float::INFINITY).lazy.select(&:even?).map(&:succ)
def make_gen = Enumerator.new { |y| y << 1; y << 2 }
KINDS = {
  # made in a method of its own: nothing on the fiber reaches the enumerator
  "lazy_nolocal"      => -> { make_lazy.next },
  "gen_nolocal"       => -> { make_gen.next },
  # held in a local, its blocks Symbol#to_procs or made elsewhere: the same
  "lazy_local_sym"    => -> { e = (1..Float::INFINITY).lazy.select(&:even?).map(&:succ); e.next },
  "lazy_method_local" => -> { e = make_lazy_sym; e.next; e.peek },
  # the block runs on the fiber and its scope holds the enumerator: kept while the thread waits
  "lazy_block_local"  => -> { e = [1, 2, 3].lazy.map { |x| x * 2 }; e.next },
  "gen_block_local"   => -> { e = Enumerator.new { |y| y << 1; y << 2 }; e.next },
  # a cursor: no fiber at all
  "map_local"         => -> { e = [1, 2, 3].map; e.next },
  "hash_select_local" => -> { e = { a: 1, b: 2 }.select; e.next },
  "filter_map_local"  => -> { e = [1, 2, 3].filter_map; e.next },
  "flat_map_local"    => -> { e = [1, 2, 3].flat_map; e.next },
  "ewo_local"         => -> { e = { a: 1, b: 2 }.each_with_object([]); e.next },
}
abort(KINDS.keys.join("\n")) unless ARGV[0]
kind = ARGV[0]
n = (ARGV[1] || 10_000).to_i
make = KINDS.fetch(kind)
t0 = cpu
n.times { make.call }
t = (cpu - t0) * 1e6 / n
a = st
3.times { GC.start; sleep 1 }
b = st
printf "%-18s %6d: %7.1f us each; threads %5s -> %5s, RSS %4d -> %4d MB\n", kind, n, t, a["Threads"], b["Threads"], a["VmRSS"].to_i / 1024, b["VmRSS"].to_i / 1024
