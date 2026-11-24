# Differential matrix for MatchData#[] (MatchDataOps' own since round 35), String#each_line's fast
# path and the URI library that leans on both.
# Prints the same on CRuby 4.0 and IronRuby (with and without the JIT):
#   ruby Util/uri-matrix.rb > a; ./ir.sh Util/uri-matrix.rb > b; diff a b
# Left out, as they differ on the unchanged build too: m[nil] (CRuby: "no implicit conversion from
# nil to integer") and an undefined non-ASCII group name (CRuby escapes it in the message).
require "uri"

def row(label)
  r = begin
    v = yield
    v.inspect
  rescue Exception => e
    "#{e.class}: #{e.message}"
  end
  puts "#{label}: #{r}"
end

M = /(?<a>x)(?<b-c>y)?(z)?(?<a>w)?/.match("xz")
N = /(\d)(\d)?(\d)/.match("a12b")
U = /(?<é>.)(?<n>.)/.match("éü")
class SubStr < String; end
keyobj = Object.new; def keyobj.to_str = "a"; def keyobj.inspect = "to_str"
intobj = Object.new; def intobj.to_int = 1; def intobj.inspect = "to_int"
[M, N, U].each_with_index do |m, i|
  [0, 1, 2, 3, 4, -1, -4, -5, 9, 1.9, "a", :a, "b-c", :"b-c", "zz", :zz, "", :n, SubStr.new("a"),
   keyobj, intobj, 0..1, 1..-1, 3..1, -30..2, 2.., ..1, 0...0, 9..10, (0..).step(2)].each do |k|
    row("m#{i}[#{k.inspect}]") { m[k] }
  end
  [[0, 2], [1, 1], [-1, 5], [9, 1], [1, -1], ["a", 1]].each { |a| row("m#{i}#{a.inspect}") { m[*a] } }
  row("m#{i}[]") { m[] }
  row("m#{i}[1,2,3]") { m[1, 2, 3] }
  row("m#{i} enc") { m.names.map { |n| (m[n] || "").encoding } }
end
row("frozen?") { M["a"].frozen? }
row("dup") { s = M[:a]; s << "!"; [s, M[:a]] }
class MatchData; alias __m_orig :[]; def [](*a) = "redef #{a.inspect}"; end
row("redefined") { M["a"] }
class MatchData; alias [] __m_orig; end
row("restored") { M["a"] }

URLS = ["https://example.com/a/b/c?x=1&y=two&z=%20sp", "http://user:pw@example.org:8080/path/to/page.html#frag",
        "ftp://files.example.net/pub/file.tar.gz;type=i", "http://[::1]:80/x", "http://[fe80::1%25eth0]/",
        "http://[v1.fe80::a+en1]/", "http://192.168.0.1:/", "mailto:a@b.c?subject=hi", "urn:isbn:0451450523",
        "//host/only", "relative/path?q#f", "../up", "?q=1", "#frag", "", "http://a b", "http://ex.com/%zz",
        "http://é.com/", "HTTP://EX.COM/", "file:///etc/hosts", "http://u%40x@h/", "ws://h:65536/x",
        "http://h/p?a=%E3%81%82&b", "foo+bar.baz-1://x/y", "1http://x", "http://h:port/"]
URLS.each do |u|
  row("split #{u}") { URI::RFC3986_PARSER.split(u) }
  row("parse #{u}") { x = URI.parse(u); [x.class, x.to_s, x.scheme, x.userinfo, x.host, x.port, x.path, x.query, x.fragment, x.opaque] }
  row("join #{u}") { URI.join("http://h/a/b/c", u).to_s }
  row("decode #{u}") { URI.decode_www_form(URI.parse(u).query.to_s) }
end
row("rfc2396") { URI::RFC2396_PARSER.split("http://user@h:1/p?q#f") }
row("join2") { URI.join("https://example.com/a/b/c?x=1", "../other/x.html?a=1").to_s }

# String#each_line's fast path (one String separator, no chomp:), which decode_www_form takes.
sepobj = Object.new; def sepobj.to_str = "&"; def sepobj.inspect = "to_str"
["a&b&&c&", "", "&", "a\nb\n\nc", "é&ü&", "a&b".encode("UTF-16LE"), "\xff&\xfe".b, "a&b".force_encoding("US-ASCII"),
 SubStr.new("x&y")].each do |s|
  ["&", "&&", "", "\n", "é", "\xff".b, "&".encode("UTF-16LE"), SubStr.new("&"), sepobj, nil, :x].each do |sep|
    # these differ on the unchanged build too: CRuby yields "" for "".each_line(nil) and checks the type late
    next if s.empty? && (sep.nil? || sep == :x)
    row("each_line #{s.inspect} #{sep.inspect}") { r = []; x = s.each_line(sep) { |l| r << [l, l.encoding.to_s, l.class] }; [x.equal?(s), r] }
    row("each_line chomp #{s.inspect} #{sep.inspect}") { r = []; s.each_line(sep, chomp: true) { |l| r << l }; r }
  end
end
row("each_line break") { "a&b&c".each_line("&") { |l| break l } }
row("each_line lambda") { r = []; "a&b".each_line("&", &->(l) { r << l }); r }
row("each_line lambda2") { "a&b".each_line("&", &->(l, m) { }) }
row("each_line 2 args") { "a&b".each_line("&", 2) { } }
row("each_line enum") { "a&b".each_line("&").to_a }
row("each_line mutate") { s = +"a&b&c"; r = []; s.each_line("&") { |l| r << l; s << "&z" if r.size == 1 }; [r, s] }
row("each_line frozen dup") { r = []; "a&b".freeze.each_line("&") { |l| r << l.frozen? }; r }
row("each_line $/") { r = []; "a\nb".each_line { |l| r << l }; r }
