# URI.decode_www_form (uri-shapes.rb's `decode`) split into the core calls it makes per pair.
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
T = URI::TBLDECWWWCOMP_
t("decode", n(4000))    { |n| s = 0; n.times { |i| s += URI.decode_www_form(QS[i & 1]).size }; s }
t("b", n(20000))        { |n| s = 0; n.times { |i| s += QS[i & 1].b.bytesize }; s }
t("each_line", n(8000)) { |n| s = 0; n.times { |i| QS[i & 1].each_line("&") { |l| s += l.size } }; s }
t("chomp!", n(20000))   { |n| s = 0; n.times { |i| x = +"x=1&"; x.chomp!("&"); s += x.size }; s }
t("partition", n(20000)) { |n| s = 0; n.times { |i| s += "page=3".partition("=")[2].size }; s }
t("gsub!", n(20000))    { |n| s = 0; n.times { |i| x = +"%20sp+q"; x.gsub!(/\+|%\h\h/, T); s += x.size }; s }
t("scrub!", n(20000))   { |n| s = 0; n.times { |i| x = +"two"; x.force_encoding(Encoding::UTF_8); x.scrub!; s += x.size }; s }
