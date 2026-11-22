# frozen_string_literal: true
# Summarizes a Util/cruby-test/sweep.sh output directory: one line per test file, then totals.
#
#   ruby Util/cruby-test/summary.rb <out> [baseline_out]
#
# A file without test-unit's summary line crashed, hung (status 124) or failed to load; its
# tests are in no count, so the totals of two sweeps compare only where both have a summary.
# With a baseline it also lists the files whose numbers moved.
#
# "Passing" is the tests named in no failure, error or timeout report, less the skips (which the
# report does not name): one test can be reported twice (a failing body and a teardown that
# raises), so the failures and errors on the summary line can add up to more tests than failed.
def read(dir)
  Dir[File.join(dir, "*.status")].sort.to_h do |st|
    name = File.basename(st, ".status")
    log = File.read(File.join(dir, "#{name}.log"), mode: "rb") rescue ""
    m = log.scan(/^(\d+) tests, (\d+) assertions, (\d+) failures, (\d+) errors, (\d+) skips/).last
    reported = log.scan(/^ *\d+\) (?:Failure|Error|Timeout):\n(\S+?#\S+?)(?: \[|:$|$)/).flatten.uniq.size
    [name, { status: File.read(st).to_i, counts: m&.map(&:to_i), reported: reported }]
  end
end

def bad(r) = r[:counts] ? r[:counts][2] + r[:counts][3] : nil

# The exit status is otherwise failures+errors (mod 256); only timeout(1)'s mean the process was
# stopped, which a file can be after it printed its summary.
def timed_out(r) = [124, 137].include?(r[:status])

def passing(r)
  t, _, f, e, s = r[:counts]
  t - (f + e > 0 && r[:reported] > 0 ? r[:reported] : f + e) - s
end

cur = read(ARGV[0] || abort("usage: summary.rb <out> [baseline_out]"))
base = ARGV[1] && read(ARGV[1])

tot = [0, 0, 0, 0, 0]
pass = 0
nosummary = []
cur.each do |name, r|
  if r[:counts]
    r[:counts].each_with_index { |c, i| tot[i] += c }
    pass += passing(r)
    t, _, f, e, s = r[:counts]
    puts format("%-32s %5d tests %5d failures %5d errors %4d skips%s", name, t, f, e, s,
                timed_out(r) ? "  TIMED OUT after its summary" : "")
  else
    nosummary << name
    puts format("%-32s NO SUMMARY (exit %d%s)", name, r[:status], r[:status] == 124 ? ", timed out" : "")
  end
end
clean = cur.count { |_, r| r[:counts] && bad(r) == 0 && !timed_out(r) }
puts
puts format("%d files: %d clean, %d with failures, %d without a summary", cur.size, clean,
            cur.size - clean - nosummary.size, nosummary.size)
puts format("%d tests, %d assertions, %d failures, %d errors, %d skips; %d passing", *tot, pass)

if base
  puts "\nChanged against the baseline:"
  (cur.keys | base.keys).sort.each do |name|
    c, b = cur[name], base[name]
    next if c && b && c[:counts] == b[:counts] && (c[:counts] ? timed_out(c) == timed_out(b) : c[:status] == b[:status])
    show = lambda do |r|
      next "absent" unless r
      next "no summary (#{r[:status]})" unless r[:counts]
      "#{r[:counts][0]}t/#{bad(r)}bad#{timed_out(r) ? ", timed out" : ""}"
    end
    puts format("  %-32s %s -> %s", name, show[b], show[c])
  end
end
