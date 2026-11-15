# Times a file of shapes under several interpreters, taking turns, and prints one row per shape.
#
#   ruby Util/perf/ab.rb [-r ROUNDS] [-c CPUS] [-n ITERATIONS] FILE NAME=CMD... [-- SHAPE...]
#
#   ruby Util/perf/ab.rb Util/bench/prelude-cost.rb cruby=ruby \
#        "master=env IR_CONFIG=Release ../wt-base/ir.sh" "new=env IR_CONFIG=Release ./ir.sh" -- g_bg n_splat
#   ruby Util/perf/ab.rb -r 6 Util/bench/core-small.rb "master=..." "new=..." \
#        "off=env IR_LIGHT_REST=0 IR_CONFIG=Release ./ir.sh" -- each_with_index
#
# FILE prints `<shape> <ns> ns <checksum>` per shape and takes shape names as arguments
# (Util/bench/prelude-cost.rb, Util/bench/core-small.rb; Util/perf/micro.rb's `ns/op` lines too).
# Each round runs FILE once under every interpreter, in the order given, a fresh process each; a
# cell is the least of its rounds. Rows come in the order of the shapes after `--`. The last
# column is the last interpreter over the one before it.
# Util/perf/micro.rb's lines are read too, but it times wall clock, prints no checksum and has
# no N: prefer the two files above for a comparison.
#
# What it is for: a difference of 10-20% between two builds on a shared machine. So it
#   - pins every run to the two CPUs that were idlest in the second before it starts (-c 2,3
#     to choose; -c none not to pin): other sessions pin too, and to the same ones if all are told
#     the same pair;
#   - takes turns, so that a load that comes and goes falls on every interpreter alike;
#   - shows each cell's spread, greatest over least in percent (from two rounds on), and marks the
#     last column `overlap` when the last two builds' rounds overlap - some round of the slower
#     build was faster than some round of the faster one. Such a row has not shown a difference:
#     run that shape alone, with more rounds, and with the change's switch off as one more
#     interpreter;
#   - says VALUE DIFFERS when the interpreters' checksums for a shape are not all the same: that
#     row is a wrong answer, not a time.
# -n sets the N environment variable the shape files read their iteration count from.
require 'shellwords'
require 'optparse'

rounds = 3
cpus = 'auto'
n = nil
only = []
argv = ARGV.dup
if (i = argv.index('--'))
  only = argv[(i + 1)..]
  argv = argv[0...i]
end
OptionParser.new do |o|
  o.on('-r N', Integer) { |v| rounds = v }
  o.on('-c CPUS') { |v| cpus = v }
  o.on('-n N') { |v| n = v }
end.parse!(argv)
file = argv.shift
interps = argv.map { |s| s.split('=', 2) }
abort "usage: ruby #{$0} [-r ROUNDS] [-c CPUS] [-n ITERATIONS] FILE NAME=CMD... [-- SHAPE...]" if file.nil? || interps.empty? || interps.any? { |_, c| c.nil? }
abort "#{file}: no such file" unless File.file?(file)
dups = interps.map(&:first).tally.select { |_, c| c > 1 }.keys
abort "each interpreter needs a name of its own: #{dups.join(', ')} given twice" unless dups.empty?

def idlest_cpus(count)
  read = -> { File.readlines('/proc/stat').grep(/^cpu\d/).to_h { |l| a = l.split; [a[0][3..].to_i, [a[1..3].sum(&:to_i) + a[6..8].sum(&:to_i), a[4].to_i + a[5].to_i]] } }
  a = read.call
  sleep 1
  b = read.call
  allowed = (File.read('/proc/self/status')[/^Cpus_allowed_list:\s*(\S+)/, 1] || '').split(',').flat_map { |r| x, y = r.split('-').map(&:to_i); (x..(y || x)).to_a }
  busy = b.to_h { |cpu, (work, idle)| dw = work - a[cpu][0]; di = idle - a[cpu][1]; [cpu, dw.to_f / [dw + di, 1].max] }
  busy.select { |cpu, _| allowed.empty? || allowed.include?(cpu) }.sort_by { |cpu, share| [share, cpu] }.first(count).map(&:first).sort
end

prefix =
  if cpus == 'none' || !File.readable?('/proc/stat') || !system('command -v taskset >/dev/null 2>&1')
    ''
  else
    list = cpus == 'auto' ? idlest_cpus(2).join(',') : cpus
    $stderr.puts "pinned to CPUs #{list}; load #{File.read('/proc/loadavg').split.first(3).join(' ')}"
    "taskset -c #{list} "
  end

times = Hash.new { |h, k| h[k] = Hash.new { |g, j| g[j] = [] } }   # shape -> interpreter -> [ns]
sums = Hash.new { |h, k| h[k] = {} }
rounds.times do |round|
  interps.each do |name, cmd|
    out = IO.popen("LANG=C.UTF-8 #{"N=#{n.shellescape} " if n}#{prefix}#{cmd} #{file.shellescape} #{only.shelljoin} 2>&1", &:read)
    abort "#{name} (#{cmd}) failed with #{$?.exitstatus || $?.termsig}:\n#{out.lines.last(15).join}" unless $?.success?
    rows = out.scan(/^(\S+)\s+([\d.]+) ns(?:\/op)?(?:[ \t]+(\S+))?/)
    abort "#{name} (#{cmd}) printed no shape:\n#{out.lines.last(15).join}" if rows.empty?
    rows.each do |shape, ns, sum|
      times[shape][name] << ns.to_f
      (sums[shape][name] ||= []) << sum if sum
    end
  end
  $stderr.print "\rround #{round + 1} of #{rounds}"
end
$stderr.puts

names = interps.map(&:first)
last, prev = names[-1], names[-2]
# with one round there is no spread to show, and a (0) would read as a steady cell
printf "%-16s" + " %14s" * names.size + "  %s\n", rounds > 1 ? 'ns (spread %)' : 'ns', *names, prev ? "#{last}/#{prev}" : ''
order = times.keys
order = only.select { |s| times.key?(s) } + (order - only) unless only.empty?
order.each do |shape|
  by = times[shape]
  cells = names.map do |name|
    v = by[name]
    next '-' if v.empty?
    next format('%.1f', v.min) if rounds == 1
    # a cell of 0 ns is a count too short for the timer's grain (see -n): no spread to give
    v.min > 0 ? format('%.1f (%d)', v.min, ((v.max / v.min - 1) * 100).round) : format('%.1f (-n?)', v.min)
  end
  ratio = prev && !by[last].empty? && !by[prev].empty? && by[prev].min > 0 ? format('%.2f', by[last].min / by[prev].min) : ''
  # the two builds' ranges overlap: every round of one fits between the rounds of the other
  if rounds > 1 && ratio != ''
    a, b = by[prev], by[last]
    ratio += ' overlap' if a.min <= b.max && b.min <= a.max
  end
  differs = sums[shape].values.flatten.uniq.size > 1 ? '  VALUE DIFFERS' : ''
  printf "%-16s" + " %14s" * names.size + "  %s%s\n", shape, *cells, ratio, differs
end
