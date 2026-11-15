#!/usr/bin/env ruby
# Times the method JIT with -X:JITMethods and -X:JITCalls off and in each of their modes, and
# prints a table.
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
#   MODES="off run run,entry"   the modes to run: off, or the options after -X:JITMethods=;
#                               `calls' is -X:JITCalls, alone or joined to a -X:JITMethods
#                               mode by a + ("run,tiered+calls")
#   CPUS=5,6                    the CPUs to pin to; by default 5 and 6 where this process may
#                               use them, otherwise the last two it may use
#   IR_TFM=net11.0              the framework, as for ir.sh
require "open3"

HERE = File.dirname(File.expand_path(__FILE__))
ROOT = File.dirname(File.dirname(HERE))
IR = ENV["IR"] || File.join(ROOT, "ir.sh")

# The CPUs this process may run on ("0-3,8" in /proc/self/status), or nil where that is not
# known. taskset refuses a list with no CPU of them in it, and every run would then fail.
def allowed_cpus
  list = File.read("/proc/self/status")[/^Cpus_allowed_list:\s*(\S+)/, 1] or return nil
  list.split(",").flat_map do |range|
    low, high = range.split("-").map(&:to_i)
    (low..(high || low)).to_a
  end
rescue SystemCallError
  nil
end

def default_cpus
  allowed = allowed_cpus
  return "5,6" if allowed.nil? || (allowed.include?(5) && allowed.include?(6))
  allowed.last(2).join(",")
end

CPUS = ENV["CPUS"] || default_cpus
MODES = (ENV["MODES"] || "off dynamic run collect run,entry collect,entry").split
ENV["IR_CONFIG"] ||= "Release"
ENV["BENCH_VALUE"] = "1"
ENV["BENCH_CLOCK"] ||= "cpu"

reps = (ARGV.first =~ /\A\d+\z/) ? ARGV.shift.to_i : 7
# no shell: a checkout path with a space in it would otherwise list nothing
names = ARGV
if names.empty?
  names = IO.popen(["ruby", File.join(HERE, "jit-methods.rb"), "--list"], &:read).split
  abort "jit-methods.rb --list failed" unless $?.success?
end
abort "no benchmarks to run" if names.empty?

# [value, first run's seconds, fastest later run's seconds or nil]; a run that failed is a
# String instead, saying how: its exit status and what it wrote to stderr.
def run(command, name)
  mine = !name.start_with?("bench:")
  file, bench = mine ? ["jit-methods.rb", name] : ["bench.rb", name.sub("bench:", "")]
  out, err, status = Open3.capture3("taskset", "-c", CPUS, *command, File.join(HERE, file), bench)
  out = out.lines.map(&:chomp)
  unless status.success? && out.size >= (mine ? 3 : 2)
    how = status.success? ? "printed #{out.size} lines" : status.to_s
    return err.strip.empty? ? how : "#{how}: #{err.strip}"
  end
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
  # Before anything is measured: a reference run that failed would otherwise only be found
  # when the table is made, after every IronRuby run of the benchmark.
  failed = cruby.grep(String).first
  abort "#{name}: a CRuby reference run failed (CPUS=#{CPUS}): #{failed}" if failed
  expected = cruby.first[0]
  first = Hash.new { |h, k| h[k] = [] }
  steady = Hash.new { |h, k| h[k] = [] }
  reps.times do |r|
    MODES.rotate(r).each do |mode|
      options = (mode == "off") ? [] : mode.split("+").map do |m|
        (m == "calls") ? "-X:JITCalls" : "-X:JITMethods=#{m}"
      end
      result = run([IR, *options], name)
      if result.is_a?(String)
        wrong << "#{name} #{mode}: the run failed, #{result}"
      elsif result[0] == expected
        first[mode] << result[1]
        steady[mode] << result[2] if result[2]
      else
        wrong << "#{name} #{mode}: #{result[0].inspect}, CRuby #{expected.inspect}"
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
