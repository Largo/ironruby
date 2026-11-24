# The parts of app-shapes.rb's `time` shape, one shape each.
$stdout.sync = true
require 'time'
require 'zlib'
def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
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
ST = ["2026-03-14 15:09:26", "2025-12-31 23:59:59", "2024-02-29 00:00:01", "2001-09-09 01:46:40"].freeze
TS = ST.map { |s| Time.utc(*s.scan(/\d+/).map(&:to_i)) }.freeze
ISO = TS.map(&:iso8601).freeze
def crc(s) = Zlib.crc32(s)
t("strptime", n(2000))  { |n| s = 0; n.times { |i| s += Time.strptime(ST[i & 3] + " UTC", "%Y-%m-%d %H:%M:%S %Z").utc.to_i % 1000 }; s }
t("d_strptime", n(4000)) { |n| s = 0; n.times { |i| s += Date._strptime(ST[i & 3] + " UTC", "%Y-%m-%d %H:%M:%S %Z").size }; s }
t("iso_out", n(20000))  { |n| s = 0; n.times { |i| s += TS[i & 3].iso8601.size }; s }
t("iso_in", n(4000))    { |n| s = 0; n.times { |i| s += Time.iso8601(ISO[i & 3]).to_i % 1000 }; s }
t("strftime", n(20000)) { |n| s = 0; n.times { |i| s += TS[i & 3].strftime("%a %d %b %Y %H:%M").size }; s }
t("parse", n(2000))     { |n| s = 0; n.times { |i| s += Time.parse(ST[i & 3] + " +0000").utc.to_i % 1000 }; s }
t("d_parse", n(2000))   { |n| s = 0; n.times { |i| s += Date._parse(ST[i & 3] + " +0000").size }; s }
t("httpdate", n(20000)) { |n| s = 0; n.times { |i| s += TS[i & 3].httpdate.size }; s }
t("crc", n(20000))      { |n| s = 0; n.times { |i| s += crc(ISO[i & 3]) % 1000 }; s }
