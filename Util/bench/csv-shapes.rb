# The parts of app-shapes.rb's `csv_parse` shape, one shape each.
$stdout.sync = true
require 'csv'
require 'zlib'
# Process CPU, not the thread's: an Enumerator#next runs the parser on a fiber, which is another thread here.
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
INSTR = ENV["INSTR"] && (require_relative "../perf/instructions"; Instructions.open || warn("no instruction counter (perf_event_open)"))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; fewest = nil; counted = !!INSTR; sum = nil
  7.times do
    c0 = INSTR && INSTR.read; t0 = cpu; sum = yield n; d = cpu - t0; c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end
N = (ENV["N"] || 1000).to_i
def n(c) = [c * N / 1000, 1].max
TABLE = (["id,name,price,qty,date"] + (1..20).map { |i| "#{i},item #{i},#{i * 1.25},#{i % 7},2026-0#{i % 9 + 1}-1#{i % 10}" }).join("\n") + "\n"
ROWS = CSV.parse(TABLE, headers: true, converters: :numeric, header_converters: :symbol)
t("plain", n(400))      { |n| s = 0; n.times { s += CSV.parse(TABLE).size }; s }
t("headers", n(400))    { |n| s = 0; n.times { s += CSV.parse(TABLE, headers: true).size }; s }
t("numeric", n(400))    { |n| s = 0; n.times { s += CSV.parse(TABLE, converters: :numeric).size }; s }
t("hsym", n(400))       { |n| s = 0; n.times { s += CSV.parse(TABLE, headers: true, header_converters: :symbol).size }; s }
t("full", n(400))       { |n| s = 0; n.times { s += CSV.parse(TABLE, headers: true, converters: :numeric, header_converters: :symbol).size }; s }
t("new_only", n(4000))  { |n| s = 0; n.times { s += CSV.new(TABLE, headers: true, converters: :numeric, header_converters: :symbol).lineno }; s }
t("row_sym", n(4000))   { |n| s = 0; n.times { ROWS.each { |r| s += r[:id] + r[:qty] } }; s }
t("split", n(20000))    { |n| s = 0; n.times { s += "1,item 1,1.25,1,2026-02-11".split(",", -1).size }; s }
t("float", n(20000))    { |n| s = 0; n.times { s += Float("1.25").to_i + Integer("17") }; s }
