# A raise rescued through the library frames real code puts around it (Monitor, Mutex, File.open, Timeout, iterators):
# us per raise, best of five, against a bare raise.
#   IR_CONFIG=Release ./ir.sh Util/bench/raise-through-shapes.rb; ruby Util/bench/raise-through-shapes.rb
$stdout.sync = true
require 'monitor'; require 'timeout'; require 'tempfile'
N = (ENV['N'] || 1000).to_i
M = Monitor.new; MX = Mutex.new
path = File.join(Dir.tmpdir, "ens2-#{$$}"); File.write(path, "x")
def run(name)
  best = nil
  5.times do
    t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    N.times { begin; yield; rescue RuntimeError; end }
    d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    best = d if best.nil? || d < best
  end
  printf "%-22s %8.2f us\n", name, best * 1e6 / N
end
run("bare raise")          { raise "x" }
run("Monitor#synchronize") { M.synchronize { raise "x" } }
run("Mutex#synchronize")   { MX.synchronize { raise "x" } }
run("File.open block")     { File.open(path) { raise "x" } }
run("Timeout.timeout")     { Timeout.timeout(5) { raise "x" } }
run("Array#each")          { [1].each { raise "x" } }
run("Integer#times")       { 1.times { raise "x" } }
run("Hash#each")           { {a: 1}.each { raise "x" } }
run("loop")                { loop { raise "x" } }
run("tap")                 { 1.tap { raise "x" } }
File.delete(path)
