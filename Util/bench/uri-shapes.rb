# The parts of app-shapes.rb's `uri` shape, one shape each.
$stdout.sync = true
require 'uri'
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
URLS = ["https://example.com/a/b/c?x=1&y=two&z=%20sp", "http://user@example.org:8080/path/to/page.html#frag",
        "https://example.com/search?q=ruby+on+dotnet&page=3&sort=desc", "ftp://files.example.net/pub/file.tar.gz"].freeze
US = URLS.map { |u| URI.parse(u) }.freeze
QS = US.map(&:query).compact.freeze
def crc(s) = Zlib.crc32(s)
t("split", n(4000))   { |n| s = 0; n.times { |i| s += URI::RFC3986_PARSER.split(URLS[i & 3]).size }; s }
t("parse", n(4000))   { |n| s = 0; n.times { |i| s += URI.parse(URLS[i & 3]).port.to_i }; s }
t("to_s", n(20000))   { |n| s = 0; n.times { |i| s += US[i & 3].to_s.size }; s }
t("decode", n(4000))  { |n| s = 0; n.times { |i| s += URI.decode_www_form(QS[i & 1]).size }; s }
t("join", n(2000))    { |n| s = 0; n.times { |i| s += URI.join(URLS[i % 3], "../other/x.html?a=1").to_s.size }; s }
t("merge", n(4000))   { |n| s = 0; n.times { |i| s += US[i % 3].merge("../other/x.html?a=1").path.size }; s }
t("encode", n(4000))  { |n| s = 0; n.times { s += crc(URI.encode_www_form([["a", "1 2"], ["b", "x&y"], ["c", "\u00e9"]])) % 1000 }; s }
