#!/usr/bin/env ruby
# Times the method JIT with -X:JITMethods off and in each of its modes, and prints a table.
# Run it with CRuby, on a Release build that has IronRuby.JitEmit.dll
# (dotnet build Src/Console/Ruby.Console.csproj -c Release -p:IronRubyJitEmit=true ...):
#
#   ruby Util/bench/jit-methods-run.rb [repetitions] [benchmark...]
#
# A benchmark is a name of Util/bench/jit-methods.rb, or bench:<name> for one of bench.rb.
# Every run is a fresh process pinned to CPUS (taskset), and the modes take turns - off, run,
# collect, ... then the same again, starting one further on - so that a busy minute on the
# machine lands on all of them. Each run's value is compared with CRuby's; a different one is
# reported and its times left out.
#
# Two tables for jit-methods.rb's benchmarks, which run their timed section five times in a
# process: the first run, which has the compilation in it, and the fastest later run. A cell
# is the best of the repetitions, the median in parentheses, and the best as a multiple of
# "off". bench.rb's run once, so they are in the first table only.
#
# The seconds are CPU time of the benchmark's thread (BENCH_CLOCK=wall for the wall clock): on
# a machine other people are using, the wall time of a run says more about them.
#
#   MODES="off run run,entry"   the modes to run (off, or the options after -X:JITMethods=)
#   CPUS=5,6                    the CPUs to pin to
#   IR_TFM=net11.0              the framework, as for ir.sh
HERE = File.dirname(File.expand_path(__FILE__))
ROOT = File.dirname(File.dirname(HERE))
IR = ENV["IR"] || File.join(ROOT, "ir.sh")
CPUS = ENV["CPUS"] || "5,6"
MODES = (ENV["MODES"] || "off dynamic run collect run,entry collect,entry").split
ENV["IR_CONFIG"] ||= "Release"
ENV["BENCH_VALUE"] = "1"
ENV["BENCH_CLOCK"] ||= "cpu"

reps = (ARGV.first =~ /\A\d+\z/) ? ARGV.shift.to_i : 7
names = ARGV.empty? ? `ruby #{File.join(HERE, "jit-methods.rb")} --list`.split : ARGV

# [value, first run's seconds, fastest later run's seconds or nil]
def run(command, name)
  mine = !name.start_with?("bench:")
  file, bench = mine ? ["jit-methods.rb", name] : ["bench.rb", name.sub("bench:", "")]
  out = IO.popen(["taskset", "-c", CPUS, *command, File.join(HERE, file), bench], err: File::NULL, &:read).lines.map(&:chomp)
  return nil unless $?.success? && out.size >= (mine ? 3 : 2)
  mine ? [out[-3], out[-2].to_f, out[-1].to_f] : [out[-2], out[-1].to_f, nil]
end

def median(a) = a.sort[a.size / 2]

def row(name, expected, cruby, times)
  off = times["off"].min
  cells = MODES.map do |mode|
    t = times[mode]
    next "-" if t.nil? || t.empty?
    cell = format("%.3f (%.3f)", t.min, median(t))
    cell += format(" **%.2fx**", t.min / off) if off && mode != "off"
    cell
  end
  "| #{name} = #{expected} | #{format('%.3f', cruby)} | #{cells.join(' | ')} |"
end

system("dotnet", "build-server", "shutdown", out: File::NULL, err: File::NULL)

wrong = []
first_rows = []
steady_rows = []
names.each do |name|
  cruby = Array.new(3) { run(["ruby"], name) }
  expected = cruby.first[0]
  first = Hash.new { |h, k| h[k] = [] }
  steady = Hash.new { |h, k| h[k] = [] }
  reps.times do |r|
    MODES.rotate(r).each do |mode|
      options = (mode == "off") ? [] : ["-X:JITMethods=#{mode}"]
      value, t1, t2 = run([IR, *options], name)
      if value == expected
        first[mode] << t1
        steady[mode] << t2 if t2
      else
        wrong << "#{name} #{mode}: #{value.inspect}, CRuby #{expected.inspect}"
      end
    end
  end
  first_rows << row(name, expected, cruby.map { |c| c[1] }.min, first)
  steady_rows << row(name, expected, cruby.map { |c| c[2] }.min, steady) if cruby.first[2]
  $stderr.puts first_rows.last
end

header = "| benchmark = value | CRuby | " + MODES.join(" | ") + " |\n|---|" + "---:|" * (MODES.size + 1)
clock = (ENV["BENCH_CLOCK"] == "cpu") ? "CPU seconds of the main thread" : "seconds"
puts "First run in the process (#{clock}; compilation included)", "", header, first_rows, ""
puts "Fastest later run in the same process (#{clock})", "", header, steady_rows, "" unless steady_rows.empty?
puts wrong.empty? ? "Every value is CRuby's (#{RUBY_DESCRIPTION})." : "WRONG VALUES:\n" + wrong.join("\n")
