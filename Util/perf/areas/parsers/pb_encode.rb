# protoboeuf encode, steady state: CPU seconds per pass over the 11 messages after warmup.
dir = File.join(ENV["RB_DIR"] || "/root/workspace/ruby-bench", "benchmarks/protoboeuf-encode")   # RB_DIR: the ruby-bench checkout
def make_shareable(x) = x
require File.join(dir, "benchmark_pb")
LOTS = Marshal.load(File.binread(File.join(dir, "encoded_msgs.bin"))).map { |b| ProtoBoeuf::ParkingLot.decode(b) }
n = (ARGV[0] || 5).to_i
cpu = -> { Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) }
3.times { LOTS.each { |l| ProtoBoeuf::ParkingLot.encode(l) } }
t = cpu.(); n.times { LOTS.each { |l| ProtoBoeuf::ParkingLot.encode(l) } }
printf "pb_encode %.3f s/pass  sha=%s\n", (cpu.() - t) / n,
  LOTS.map { |l| ProtoBoeuf::ParkingLot.encode(l) }.join.sum
