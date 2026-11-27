# What starting a fiber costs (LEDGER gap 35): a fiber is a thread here. One kind per process, in
# blocks of 500, so that warm-up and the threads abandoned fibers leave behind show block by block.
#
#   ./ir.sh Util/bench/fiber-start.rb KIND [BLOCKS]
#   for k in $(ruby Util/bench/fiber-start.rb); do ./ir.sh Util/bench/fiber-start.rb $k; done
#
# Wall clock and process CPU (CLOCK_PROCESS_CPUTIME_ID: the fiber's thread and the spinning of
# both sides of a switch are counted), and the process's threads after each block.
$stdout.sync = true
def th = File.read("/proc/self/status")[/^Threads:\s+(\d+)/, 1].to_i
def wall = Process.clock_gettime(Process::CLOCK_MONOTONIC)
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
Q = Queue.new
KINDS = {
  # a Ruby thread: started and joined; started and left waiting, as an abandoned fiber's is
  "thread_join"    => -> { Thread.new { }.join },
  "thread_park"    => -> { Thread.new { Q << 1; sleep }; Q.pop },
  # a fiber that finishes runs on a pooled thread after the first few
  "fiber_complete" => -> { Fiber.new { 1 }.resume },
  "fiber_finish"   => -> { f = Fiber.new { Fiber.yield 1; 2 }; f.resume; f.resume },
  # one that is left suspended keeps its thread until a GC finds its handle
  "fiber_abandon"  => -> { Fiber.new { Fiber.yield 1 }.resume },
  # enumerators with a fiber: drained, and left after one #next
  "gen_drain"      => -> { e = Enumerator.new { |y| y << 1 }; e.next; (e.next rescue nil) },
  "gen_abandon"    => -> { Enumerator.new { |y| y << 1; y << 2 }.next },
  "lazy_abandon"   => -> { [1, 2].lazy.map { |x| x }.next },
}
abort(KINDS.keys.join("\n")) unless ARGV[0]
kind = ARGV[0]
make = KINDS.fetch(kind)
(ARGV[1] || 6).to_i.times do |j|
  n = 500
  w0 = wall; c0 = cpu
  n.times { make.call }
  printf "%-14s block %d: %7.1f us wall %7.1f us cpu, threads %d\n", kind, j, (wall - w0) * 1e6 / n, (cpu - c0) * 1e6 / n, th
end
