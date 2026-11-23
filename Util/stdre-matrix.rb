# Differential matrix for MatchData's named-group lookups (MatchData#[] by name and Symbol,
# #begin/#end/#offset/#byteoffset by name, #names, #named_captures, #captures, #deconstruct_keys,
# Regexp.last_match(name)), over patterns of one group, many groups (URI's RFC 3986 pattern),
# a repeated name, a name that did not take part, an undefined name, non-ASCII names and
# subjects, a MatchData that was dup'ed, and the same Regexp against strings of two encodings.
# Every row runs cold (first call) and hot (the 200th). Run under CRuby and IronRuby, JIT on
# and off, and diff:
#
#   ruby Util/stdre-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/stdre-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/stdre-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# All must end with the row `end' and be identical.

require 'uri'
$stdout.sync = true

def row(label)
  first = last = nil
  200.times do |i|
    v = begin
      yield
    rescue => e
      [e.class, e.message]
    end
    first = v if i == 0
    last = v
  end
  puts "#{label}: #{first.inspect}#{first == last ? '' : " HOT #{last.inspect}"}"
end

U = "http://user@example.com:8080/a/b/c.html?x=1&y=two#frag"
R = URI::RFC3986_Parser::RFC3986_URI
row("uri names") { m = R.match(U); %w[scheme userinfo host port path-abempty path-rootless query fragment].map { |n| m[n] } }
row("uri sym") { m = R.match(U); [m[:host], m[:port], m[:"path-rootless"]] }
row("uri offsets") { m = R.match(U); [m.begin("host"), m.end(:host), m.offset("query"), m.byteoffset("fragment"), m.begin("path-rootless")] }
row("uri names list") { R.match(U).names.size }
row("uri named_captures") { R.match("mailto:a@b").named_captures.compact }
row("undefined") { /(?<a>x)/.match("x")["b"] }
row("undefined sym") { /(?<a>x)/.match("x")[:b] }
row("undefined begin") { /(?<a>x)/.match("x").begin("b") }
row("nonparticipating") { m = /(?<a>x)|(?<b>y)/.match("y"); [m[:a], m[:b], m.begin(:a), m.offset(:b)] }
row("dup name first") { m = /(?<a>x)|(?<a>y)/.match("x"); [m[:a], m.begin(:a), m.names, m.named_captures] }
row("dup name second") { m = /(?<a>x)|(?<a>y)/.match("y"); [m[:a], m.begin(:a), m.captures, m.deconstruct_keys(nil)] }
row("dup name both") { m = /(?<a>x)(?<a>y)/.match("xy"); [m[:a], m.offset(:a), m.size] }
row("non-ascii name") { m = /(?<é>.)(?<ü>.)/.match("añb"); [m["é"], m[:ü], m.begin("ü"), m.byteoffset("ü"), m.names] }
row("non-ascii subject") { m = /(?<w>\w+)$/.match("héllo wörld"); [m[:w], m.begin(:w), m.byteoffset(:w)] }
row("last_match") { /(?<k>\d+)/ =~ "ab12"; [Regexp.last_match(:k), Regexp.last_match("k"), $~[:k]] }
row("dup md") { m = /(?<k>\d+)/.match("ab12").dup; [m[:k], m.names, m.begin(:k)] }
row("two encodings") { re = /(?<k>.)/; [re.match("a")[:k].encoding, re.match("a".b)[:k].encoding, re.match("é")[:k]] }
row("scan $~") { r = []; "a1b2".scan(/(?<d>\d)/) { r << $~[:d] }; r }
row("gsub $~") { "a1b2".gsub(/(?<d>\d)/) { $~[:d] * 2 } }
row("named = locals") { if /(?<yy>\d+)-(?<mm>\d+)/ =~ "2026-03" then [yy, mm] end }
row("deconstruct_keys") { /(?<a>.)(?<b>.)/.match("xy").deconstruct_keys([:b, :a]) }
row("values_at") { /(?<a>.)(?<b>.)/.match("xy").values_at(:b, "a", 0) }
row("match by name") { m = /(?<a>.)(?<b>.)?/.match("x"); [m.match(:a), m.match_length("a"), (m.match(:c) rescue $!.message)] }
row("URI.parse") { u = URI.parse(U); [u.scheme, u.userinfo, u.host, u.port, u.path, u.query, u.fragment] }
row("URI.join") { URI.join(U, "../other/x.html?a=1").to_s }
row("URI opaque") { u = URI.parse("mailto:a@b.c?subject=hi"); [u.class, u.opaque, u.to] }
puts "end"
