# RuboCop autocorrect of ruby-bench's fixture, in-process: CPU ms per run after warmup.
# Run from the copy of ruby-bench/benchmarks/rubocop with its bundle (see parsers.md).
require "bundler/setup"
require "stringio"
require "rubocop"
class RuboCopRunner < RuboCop::Runner
  def initialize
    super(::RuboCop::Options.new.parse(%w[--stderr --force-exclusion --format RuboCop::Formatter::BaseFormatter --raise-cop-error --autocorrect]).first, ::RuboCop::ConfigStore.new)
  end
  def run(path, contents) = (@options[:stdin] = contents; super([path]))
end
path = File.expand_path("fixture.rb"); contents = File.read(path); runner = RuboCopRunner.new
cpu = -> { Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) }
warm, n = (ARGV[0] || 50).to_i, (ARGV[1] || 50).to_i
ERR = IO.for_fd(2, "w", autoclose: false)
log = ->(s) { ENV["CURVE"] ? File.open(ENV["CURVE"], "a") { |f| f.puts s } : ERR.syswrite(s + "\n") }  # RuboCop takes over $stdout/$stderr
warm.times { |i| t0 = cpu.(); runner.run(path, contents); log.(format("%d %.0f", i, (cpu.() - t0) * 1000)) if ENV["CURVE"] || ENV["MARKS"] }
t = cpu.(); n.times { runner.run(path, contents) }
log.(format("rubocop %.1f ms/run cpu", (cpu.() - t) / n * 1000))
