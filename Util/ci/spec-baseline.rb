# Compares ruby/spec results against a checked-in list of the examples known to fail.
#
#   ruby Util/ci/spec-baseline.rb BASELINE RECORD...           check
#   ruby Util/ci/spec-baseline.rb --write BASELINE RECORD...   rewrite BASELINE from the records
#
# RECORDs are what Util/ci/record_formatter.rb writes, one per mspec-run.  BASELINE
# names every failing example, one per line, as "<spec file>: <description>", grouped
# under the directory it belongs to.  A line that starts with "? " is an example that
# is known to fail only sometimes: either outcome is accepted.  "#" starts a comment.
#
# The check fails when
#   - an example fails that the baseline does not list, or
#   - a run did not reach its end (it crashed or hung: its record has no DONE).
# An example the baseline lists that now passes is reported, with the line to delete,
# but does not fail the check - the same contract as the IronRuby.Tests count, which
# fails only when there are more failures than recorded.  The list is exact, never
# head-room: every entry is one example that fails today.

Entry = Struct.new(:file, :description, :flaky)

def dir_of(file)
  File.dirname(file)
end

def read_baseline(path)
  return [] unless File.exist?(path)
  File.readlines(path, chomp: true).filter_map do |line|
    next if line.strip.empty? || line.start_with?("#")
    flaky = line.start_with?("? ")
    line = line[2..] if flaky
    file, description = line.split(": ", 2)
    abort "#{path}: cannot parse #{line.inspect}" unless file&.end_with?(".rb") && description
    Entry.new(file, description, flaky)
  end
end

Run = Struct.new(:record, :done, :tally, :last_file, :failures)

def read_record(path)
  run = Run.new(path, false, nil, nil, [])
  File.foreach(path, chomp: true) do |line|
    kind, file, description = line.split("\t", 3)
    case kind
    when "LOAD" then run.last_file = file
    when "FAIL", "ERROR" then run.failures << Entry.new(file, description, false)
    when "DONE" then run.done = true; run.tally = file
    end
  end
  run
end

write = ARGV.delete("--write")
baseline_path, *record_paths = ARGV
abort "usage: #{$0} [--write] BASELINE RECORD..." if baseline_path.nil? || record_paths.empty?

runs = record_paths.map { |p| read_record(p) }
failing = runs.flat_map(&:failures).uniq { |e| [e.file, e.description] }
key = ->(e) { [e.file, e.description] }

if write
  old = read_baseline(baseline_path)
  header = File.exist?(baseline_path) ? File.readlines(baseline_path).take_while { |l| l.start_with?("#") } : []
  flaky = old.select(&:flaky)
  entries = (failing.reject { |e| flaky.any? { |f| key[f] == key[e] } } + flaky)
  File.open(baseline_path, "w") do |f|
    f.print header.join
    entries.group_by { |e| dir_of(e.file) }.sort.each do |dir, list|
      f.puts "", "# #{dir}: #{list.count { !_1.flaky }} failing#{", #{list.count(&:flaky)} flaky" if list.any?(&:flaky)}"
      list.sort_by { |e| [e.file, e.description] }.each do |e|
        f.puts "#{"? " if e.flaky}#{e.file}: #{e.description}"
      end
    end
  end
  puts "wrote #{entries.size} entries to #{baseline_path}"
  exit
end

baseline = read_baseline(baseline_path)
known = baseline.to_h { |e| [key[e], e] }
now = failing.to_h { |e| [key[e], e] }

new_failures = failing.reject { |e| known.key?(key[e]) }
fixed = baseline.reject { |e| e.flaky || now.key?(key[e]) }
unfinished = runs.reject(&:done)

puts "Failing examples by directory (now / baseline):"
dirs = (failing.map { dir_of(_1.file) } + baseline.map { dir_of(_1.file) }).uniq.sort
runs.each do |run|
  puts "  #{File.basename(run.record, ".*")}: #{run.done ? run.tally : "DID NOT FINISH (last file #{run.last_file})"}"
end
puts
dirs.each do |dir|
  n = failing.count { dir_of(_1.file) == dir }
  b = baseline.count { dir_of(_1.file) == dir && !_1.flaky }
  puts format("  %-40s %4d / %4d", dir, n, b)
end
puts
puts "Failing examples by file (now / baseline):"
(failing.map(&:file) + baseline.map(&:file)).uniq.sort.each do |file|
  n = failing.count { _1.file == file }
  b = baseline.count { _1.file == file && !_1.flaky }
  puts format("  %-60s %4d / %4d", file, n, b)
end

github = ENV["GITHUB_ACTIONS"]
unless fixed.empty?
  puts
  puts "#{fixed.size} example(s) in the baseline pass now - delete these lines from #{baseline_path}:"
  fixed.each { |e| puts "  #{e.file}: #{e.description}" }
  puts "::warning::#{fixed.size} baseline example(s) pass now; remove them from #{baseline_path}" if github
end

status = 0
unless new_failures.empty?
  puts
  puts "#{new_failures.size} example(s) fail that the baseline does not list:"
  new_failures.each { |e| puts "  #{e.file}: #{e.description}" }
  puts "::error::#{new_failures.size} new ruby/spec failure(s); see the log above" if github
  status = 1
end
unless unfinished.empty?
  puts
  unfinished.each do |run|
    puts "#{run.record} did not finish: it crashed or hung in #{run.last_file || "(before the first file)"}"
    puts "::error::#{File.basename(run.record)} did not finish (last file #{run.last_file})" if github
  end
  status = 1
end
puts
puts status == 0 ? "OK: no failures beyond the baseline." : "FAILED"
exit status
