# tinygql: parse ruby-bench's negotiate.gql, CPU ms per parse after warmup.
lib = Dir[File.join(ENV.fetch("GEM_HOME", Gem.dir), "gems/tinygql-0.3.1/lib")]
# without it `require` would take whatever other version is installed, and the two engines could time different parsers
abort "tinygql 0.3.1 is not installed under GEM_HOME" if lib.empty?
$LOAD_PATH.unshift(*lib)
require "tinygql"
file = File.read(File.join(ENV["RB_DIR"] || "/root/workspace/ruby-bench", "benchmarks/tinygql/negotiate.gql"))   # RB_DIR: the ruby-bench checkout
cpu = -> { Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) }
warm, n = (ARGV[0] || 100).to_i, (ARGV[1] || 100).to_i
warm.times { TinyGQL.parse file }
t = cpu.(); n.times { TinyGQL.parse file }
printf "tinygql %.2f ms/parse cpu\n", (cpu.() - t) / n * 1000
