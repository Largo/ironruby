# protoboeuf decode, steady state: CPU seconds for N passes after warmup.
dir = File.join(ENV["RB_DIR"] || "/root/workspace/ruby-bench", "benchmarks/protoboeuf")   # RB_DIR: the ruby-bench checkout
def make_shareable(x) = x
require File.join(dir, "benchmark_pb")
MSGS = Marshal.load(File.binread(File.join(dir, "encoded_msgs.bin")))
n = (ARGV[0] || 5).to_i
cpu = -> { Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) }
3.times { MSGS.each { |b| ProtoBoeuf::ParkingLot.decode(b) } }
t = cpu.(); n.times { MSGS.each { |b| ProtoBoeuf::ParkingLot.decode(b) } }
printf "pb_decode %.3f s/pass\n", (cpu.() - t) / n
