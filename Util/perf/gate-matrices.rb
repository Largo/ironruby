# Step 2 of /perf-gate for the differential matrices, all at once: every matrix under CRuby and
# under each mode on both trees, JOBS processes at a time, each in an empty directory of its own.
# Run one after another, by hand, the 30-odd matrices of a gate in seven modes took most of an
# hour; here every run is a job of a queue, and on eight cores all 99 matrices in six modes on
# both trees (some 2,000 runs) take 25-35 minutes. Name files to run fewer.
#
#   ruby Util/perf/gate-matrices.rb BASE NEW [MATRIX...]     (default: --all)
#     --all              every Util/*-matrix.rb and Util/perf/*-matrix.rb of NEW
#     --modes a,b        only these of the default modes (below)
#     --mode NAME=SPEC   one more mode: the switch of the change, e.g. --mode noregex=IR_LIGHT_REGEX=0
#     --jobs N           (default: the number of CPUs)    --timeout S   per run (default 900)
#     --out DIR          (default: a new directory under /tmp)
#
# A SPEC is environment assignments and then ir arguments: "IR_LIGHT_FRAMES=0", "-X:NoJIT
# -X:NoOSR". Every mode runs on both trees with IR_CONFIG=Release unless it sets IR_CONFIG
# itself, and the matrix files are always NEW's (a change brings its own cases). The default
# modes are the gate's: as built, -X:NoJIT -X:NoOSR, -X:NoJITCalls, IR_LIGHT_FRAMES=0,
# IR_PARKED_SCOPES=0, and the ReadyToRun host (skipped, and said so, when either tree has none
# or one older than its Release build). int-matrix.rb runs through int-matrix.py, which
# restarts it past a stack overflow; inline-matrix.rb runs one case per process, as its header
# asks.
#
# What is compared: stdout and the exit status, with the run's own directory, the two trees'
# paths and object addresses (0x...) written alike, and the lines that differ between two runs
# of CRuby (int-matrix's Integer#hash) masked. For each matrix and mode:
#   same       NEW prints what CRuby prints
#   baseline   NEW differs from CRuby only in lines BASE differs in too ("-3": NEW has three
#              such lines fewer than BASE, a difference the change fixed)
#   DIFF       NEW has a line differing from CRuby that BASE in the same mode does not: the gate
#              fails, unless the run is flaky - every DIFF is run twice more, alone, at the end,
#              and one that then prints what BASE prints is reported as `flaky', not passed over
#   died       CRuby ran to the end (exit 0), BASE and NEW did not, and NEW's output differs from
#              CRuby's no more than BASE's does: the rows after the death are compared by no one,
#              so the gate cannot pass on it - fix the crash (or move the rows past it) first
#   cruby!     CRuby itself did not exit 0: the matrix dies before its last rows, and the rows
#              after the failure are compared by no one (round seventeen); fix the matrix
# Exit status 1 when any run is DIFF, else 2 when any is died (the comparison is incomplete, as
# parallel-tests.sh's 2 is), else 0. OUT/report.txt has the table, OUT/<matrix>/ every output,
# OUT/<matrix>/<mode>.diff the lines a DIFF is about, and OUT/times.tsv each run's seconds (also
# kept in $TMPDIR/gate-matrices-times.tsv, or $GATE_MATRICES_TIMES, to start the slow ones first).
require 'optparse'
require 'fileutils'
require 'tmpdir'
require 'etc'
require 'open3'

DEFAULT_MODES = {
  'jit' => '',
  'nojit' => '-X:NoJIT -X:NoOSR',
  'nojitcalls' => '-X:NoJITCalls',
  'nolight' => 'IR_LIGHT_FRAMES=0',
  'noparked' => 'IR_PARKED_SCOPES=0',
  'r2r' => 'IR_CONFIG=R2R',
}

opts = { jobs: Etc.nprocessors, timeout: 900, modes: DEFAULT_MODES.dup, all: false, out: nil }
OptionParser.new do |o|
  o.banner = 'usage: ruby Util/perf/gate-matrices.rb [options] BASE NEW [MATRIX...]'
  o.on('--all') { opts[:all] = true }
  o.on('--modes LIST') { |l| opts[:modes] = l.split(',').to_h { |m| [m, DEFAULT_MODES.fetch(m)] } }
  o.on('--mode NAME_SPEC') { |s| n, spec = s.split('=', 2); opts[:modes][n] = spec.to_s }
  o.on('--jobs N', Integer) { |n| opts[:jobs] = n }
  o.on('--timeout S', Integer) { |n| opts[:timeout] = n }
  o.on('--out DIR') { |d| opts[:out] = d }
end.parse!
abort 'usage: ruby Util/perf/gate-matrices.rb [options] BASE NEW [MATRIX...]' if ARGV.size < 2
BASE, NEW = ARGV.shift(2).map { |t| File.realpath(t) }
TREES = { 'base' => BASE, 'new' => NEW }
TIMEOUT = opts[:timeout]
OUT = File.expand_path(opts[:out] || Dir.mktmpdir('gate-matrices-'))
FileUtils.mkdir_p(OUT)
CRUBY = ENV['CRUBY'] || 'ruby'
# CRuby is the expectation: without a working 4.0 every unit would read `cruby!' and the run
# would still say PASS, having compared nothing.
begin
  version, status = Open3.capture2(CRUBY, '-e', 'print RUBY_VERSION')
rescue SystemCallError => e
  abort "#{CRUBY}: #{e.message}"
end
abort "#{CRUBY} does not run (exit #{status.exitstatus})" unless status.success?
abort "#{CRUBY} is Ruby #{version}; the matrices' expectation is CRuby 4.0" unless version.start_with?('4.0')
DOTNET = ENV['DOTNET_ROOT'] || '/usr/local/dotnet'

files =
  if ARGV.empty? || opts[:all]
    Dir[File.join(NEW, 'Util/*-matrix.rb')] + Dir[File.join(NEW, 'Util/perf/*-matrix.rb')]
  else
    ARGV.map { |f| File.realpath(File.exist?(f) ? f : File.join(NEW, f)) }
  end
abort 'no matrix files' if files.empty?

# The ReadyToRun host counts only if it is at least as new as the Release build beside it:
# ir.sh falls back to nothing, and an older host is the tree before the change.
skipped = []
if opts[:modes].any? { |_, spec| spec.include?('IR_CONFIG=R2R') }
  stale = TREES.reject do |_, t|
    r2r = File.join(t, 'Src/Console/bin/R2R/net10.0/ir.dll')
    rel = File.join(t, 'Src/Console/bin/Release/net10.0/ir.dll')
    File.exist?(r2r) && File.exist?(rel) && File.mtime(r2r) >= File.mtime(rel)
  end
  unless stale.empty?
    opts[:modes].reject! { |n, spec| spec.include?('IR_CONFIG=R2R') && skipped << n }
    warn "skipping #{skipped.join(', ')}: no ReadyToRun host as new as the Release build in #{stale.values.join(' and ')}"
  end
end
MODES = opts[:modes]

# One launcher per tree and mode, so that int-matrix.py (which takes a single executable) and
# every other run start ir the same way.
LAUNCHERS = {}
TREES.each do |who, tree|
  MODES.each do |mode, spec|
    words = spec.split
    env = words.take_while { |w| w =~ /\A[A-Z_][A-Z0-9_]*=/ }
    args = words.drop(env.size)
    env.unshift('IR_CONFIG=Release') unless env.any? { |e| e.start_with?('IR_CONFIG=') }
    path = File.join(OUT, '.launchers', "#{who}-#{mode}")
    FileUtils.mkdir_p(File.dirname(path))
    File.write(path, "#!/bin/sh\nexec env DOTNET_ROOT=#{DOTNET} #{env.join(' ')} #{tree}/ir.sh #{args.join(' ')} \"$@\"\n")
    File.chmod(0o755, path)
    LAUNCHERS[[who, mode]] = path
  end
end

# A unit is one comparison: a matrix file, or one case of inline-matrix.rb.
Unit = Struct.new(:name, :file, :arg)
units = files.sort.flat_map do |f|
  base = File.basename(f, '.rb')
  if base == 'inline-matrix'
    begin
      list, status = Open3.capture2(CRUBY, f, 'list')
    rescue SystemCallError => e
      abort "#{CRUBY} #{f} list: #{e.message}"
    end
    abort "#{CRUBY} #{f} list failed (#{status})" unless status.success?
    list.split.map { |c| Unit.new("#{base}.#{c}", f, c) }
  else
    [Unit.new(base, f, nil)]
  end
end

def command(unit, who, mode)
  if who.start_with?('cruby')
    [CRUBY, unit.file, *unit.arg]
  elsif File.basename(unit.file) == 'int-matrix.rb'
    # the driver writes its output to a file; `cat' it to stdout like every other run
    ['sh', '-c', 'python3 "$0" out.txt "$1" >&2 && cat out.txt',
     unit.file.sub(/\.rb\z/, '.py'), LAUNCHERS[[who, mode]]]
  else
    [LAUNCHERS[[who, mode]], unit.file, *unit.arg]
  end
end

def normalize(text, dir)
  text = text.gsub(dir, '<cwd>').gsub(File.realpath(dir), '<cwd>')
  TREES.each_value { |t| text = text.gsub(t, '<tree>') }
  text.gsub(/0x[0-9a-f]{6,16}/, '0x...')
end

# Runs one job and returns [normalized stdout + exit line, seconds].
def run(unit, who, mode, tag = nil)
  dir = Dir.mktmpdir('gm-')
  out = File.join(OUT, unit.name, "#{who}#{mode && "-#{mode}"}#{tag}")
  FileUtils.mkdir_p(File.dirname(out))
  t = Process.clock_gettime(Process::CLOCK_MONOTONIC)
  begin
    pid = Process.spawn(*command(unit, who, mode), chdir: dir, in: File::NULL,
                        out: "#{out}.raw", err: "#{out}.err", pgroup: true)
  rescue SystemCallError => e
    # one job that cannot start (a missing CRUBY or launcher) is that job's output, not the end
    # of the run; the message, with its path, goes to .err so both trees' outputs stay alike
    File.write("#{out}.err", "#{[unit.name, who, mode].compact.join(' ')}: #{e.message}\n")
    text = "(could not start: #{e.class})\n"
    File.binwrite("#{out}.txt", text)
    return [text, Process.clock_gettime(Process::CLOCK_MONOTONIC) - t]
  end
  status = nil
  deadline = t + TIMEOUT
  loop do
    _, status = Process.wait2(pid, Process::WNOHANG)
    break if status
    if Process.clock_gettime(Process::CLOCK_MONOTONIC) > deadline
      Process.kill('KILL', -pid) rescue nil
      _, status = Process.wait2(pid)
      File.write("#{out}.err", "\n(killed after #{TIMEOUT}s)\n", mode: 'a')
      break
    end
    sleep 0.05
  end
  text = normalize(File.binread("#{out}.raw").force_encoding('BINARY').scrub, dir)
  text << "\n(exit #{status.exitstatus || "signal #{status.termsig}"})\n"
  File.binwrite("#{out}.txt", text)
  [text, Process.clock_gettime(Process::CLOCK_MONOTONIC) - t]
ensure
  FileUtils.rm_rf(dir) if dir
end

jobs = units.flat_map do |u|
  [[u, 'cruby', nil], [u, 'cruby2', nil]] + MODES.keys.flat_map { |m| [[u, 'base', m], [u, 'new', m]] }
end
# The slow ones first, so the queue does not end on one of them: by how long each took the last
# time (respond-to-matrix takes two minutes under CRuby and eight seconds under IronRuby), with
# int-matrix, which restarts itself, first when there is no last time.
TIMES_CACHE = ENV['GATE_MATRICES_TIMES'] || File.join(Dir.tmpdir, 'gate-matrices-times.tsv')
last = File.exist?(TIMES_CACHE) ? File.readlines(TIMES_CACHE).to_h { |l| s, *k = l.chomp.split("\t"); [k.join("\t"), s.to_f] } : {}
key = ->(u, who, mode) { [u.name, who.sub(/\d\z/, ''), mode.to_s].join("\t") }
jobs.sort_by! { |u, who, mode| -(last[key[u, who, mode]] || (u.name == 'int-matrix' ? 1e6 : 0)) }

results = {}
times = []
lock = Mutex.new
queue = Queue.new
jobs.each { |j| queue << j }
done = 0
started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
workers = Array.new(opts[:jobs]) do
  Thread.new do
    while (j = (queue.pop(true) rescue nil))
      text, secs = run(*j)
      lock.synchronize do
        results[j] = text
        times << [secs.round(1), j[0].name, j[1], j[2]]
        done += 1
        $stderr.print "\r#{done}/#{jobs.size} runs" if $stderr.tty?
      end
    end
  end
end
workers.each(&:join)
$stderr.puts
File.write(File.join(OUT, 'times.tsv'), times.sort.reverse.map { |t| t.join("\t") + "\n" }.join)
times.each { |secs, name, who, mode| last[[name, who.sub(/\d\z/, ''), mode.to_s].join("\t")] = secs }
File.write(TIMES_CACHE, last.map { |k, secs| "#{secs}\t#{k}\n" }.join) rescue nil

# The lines of IR's output that are not CRuby's, as `diff' marks them, without line numbers.
def bad_lines(cruby, ir)
  return [] if cruby == ir
  a = File.join(Dir.tmpdir, "gm-a-#{Process.pid}-#{Thread.current.object_id}")
  b = "#{a}-b"
  File.binwrite(a, cruby); File.binwrite(b, ir)
  out, status = Open3.capture2('diff', a, b)
  raise "diff #{a} #{b}: #{status}" if status.exitstatus.nil? || status.exitstatus > 1
  out.b.lines.grep(/\A> /)
ensure
  File.delete(a, b) rescue nil
end

EXIT0 = "(exit 0)\n"

def verdict(cruby, base, new)
  return ['same', []] if new == cruby
  new_bad = bad_lines(cruby, new)
  base_bad = bad_lines(cruby, base)
  only_new = new_bad - base_bad
  return ['DIFF', only_new] unless only_new.empty?
  # NEW dies as BASE does (a death only NEW has is a DIFF above, by its exit line): what CRuby
  # printed after that point is checked on neither tree
  return ['died', []] if cruby.end_with?(EXIT0) && !new.end_with?(EXIT0)
  fewer = base_bad.size - new_bad.size
  [fewer > 0 ? "baseline -#{fewer}" : 'baseline', []]
end

# A line that differs between CRuby's two runs (Integer#hash is seeded per process) is no
# answer: in every output with CRuby's number of lines it reads the same.
def mask(text, varying)
  return text if varying.empty?
  lines = text.lines
  return text unless lines.size == varying.first
  varying.drop(1).each { |i| lines[i] = "<varies under CRuby>\n" }
  lines.join
end

table = {}
flaky = []
units.each do |u|
  c1, c2 = results[[u, 'cruby', nil]].lines, results[[u, 'cruby2', nil]].lines
  varying = c1.size == c2.size ? [c1.size] + c1.each_index.select { |i| c1[i] != c2[i] } : []
  varying = [] if varying.size == 1
  results.each_key { |k| results[k] = mask(results[k], varying) if k[0] == u }
  cruby = results[[u, 'cruby', nil]]
  MODES.each_key do |m|
    v, lines = verdict(cruby, results[[u, 'base', m]], results[[u, 'new', m]])
    if v == 'DIFF'
      # alone, twice: a run that then prints what CRuby or BASE prints was the load
      again = (1..2).map { |i| verdict(cruby, results[[u, 'base', m]], mask(run(u, 'new', m, ".again#{i}").first, varying)).first }
      if again.include?('died') && !again.include?('DIFF')
        v = 'died'      # alone it dies as BASE does: still not compared to the end
      elsif !again.include?('DIFF')
        v = 'flaky'
        flaky << "#{u.name} #{m}"
      else
        File.binwrite(File.join(OUT, u.name, "#{m}.diff"), lines.join)
      end
    end
    table[[u.name, m]] = v
  end
  table[[u.name, :cruby]] = cruby.end_with?(EXIT0) ? nil : 'cruby!'
end

wall = (Process.clock_gettime(Process::CLOCK_MONOTONIC) - started).round
width = units.map { |u| u.name.size }.max
report = +"#{units.size} matrices, #{MODES.size} modes (#{MODES.map { |n, s| s.empty? ? n : "#{n}: #{s}" }.join('; ')}), " \
         "#{jobs.size} runs, #{opts[:jobs]} at a time: #{wall}s\n"
report << "skipped: #{skipped.join(', ')} (no current ReadyToRun host)\n" unless skipped.empty?
report << "base #{BASE}\nnew  #{NEW}\n\n"
report << "#{''.ljust(width)}  #{MODES.keys.map { |m| m.ljust(12) }.join}\n"
units.each do |u|
  row = MODES.keys.map { |m| table[[u.name, m]].ljust(12) }.join
  report << "#{u.name.ljust(width)}  #{row}#{table[[u.name, :cruby]]}\n"
end
counts = table.reject { |k, _| k[1] == :cruby }.values.map { |v| v.split.first }.tally
diffs = table.select { |k, v| v == 'DIFF' }.keys
died = table.select { |k, v| v == 'died' }.keys
broken = table.select { |k, v| k[1] == :cruby && v }.keys.map(&:first)
report << "\n#{counts.sort.map { |k, n| "#{n} #{k}" }.join(', ')}\n"
report << "flaky (DIFF beside the others, BASE's output twice alone): #{flaky.join('; ')}\n" unless flaky.empty?
report << "CRuby did not exit 0 on: #{broken.join(', ')}\n" unless broken.empty?
unless died.empty?
  report << "died on BASE and NEW alike where CRuby ran to the end - the rows after are compared by no one:\n"
  died.each { |name, m| report << "  #{name} #{m}: #{File.join(OUT, name, "base-#{m}.err")}\n" }
end
diffs.each { |name, m| report << "DIFF #{name} #{m}: #{File.join(OUT, name, "#{m}.diff")}\n" }
status = !diffs.empty? ? 1 : !died.empty? ? 2 : 0
report << ["PASS\n", "FAIL\n", "INCOMPLETE (died)\n"][status]
File.write(File.join(OUT, 'report.txt'), report)
puts report, "(#{OUT}/report.txt)"
exit(status)
