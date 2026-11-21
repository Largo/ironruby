# Differential matrix for methods with a regex literal (`s.match?(/x/)', `/x/.match?(s)', `/a#{x}/') that run without a
# scope (a light frame, MethodDefinition.IsScopeFree). Run it under CRuby and under ./ir.sh with the shape on and off,
# and diff:
#
#   ruby Util/regex-light-matrix.rb > mri.txt
#   ./ir.sh Util/regex-light-matrix.rb > on.txt
#   IR_LIGHT_REGEX=0 ./ir.sh Util/regex-light-matrix.rb > off.txt
#   diff mri.txt on.txt && diff mri.txt off.txt
#
# A literal reads no scope, but the method it is passed to may set $~, which is the frame's own in CRuby (and the
# thread's): the probes look at $~, $1, $&, $` and Regexp.last_match after each kind of call - in the method, in a block
# of it, through binding and eval, in the caller (which must keep its own), after a callee that matches - and at the
# literal itself (one frozen object per literal, `o', interpolation), with the callee redefined after the site is
# bound, method_missing, a refinement, threads, and recursion a million deep.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/regex-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

N = 3000

def t(label)
  print label.ljust(52), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 70]}"
  end
end

# The caller's match data, which no callee may change: [$~[0], $1, $&, $`, Regexp.last_match(1)] after the call.
def kept
  "keep" =~ /(ke)ep/
  r = yield
  [r, $~ && $~[0], $1, $&, $`, Regexp.last_match(1)]
end

# ---------------------------------------------------------------- 1. a literal that is only a value

def lit = /x/
def lit_opts = [/a/i, /a/m, /a/x, /a b/ix, /あ/, /\//]
def lit_arr(s) = [s, /b(.)/]
def interp(x) = /a#{x}b/
def interp_once(x) = /a#{x}b/o
def interp_opts(x) = /#{x}/i
class Weird; def to_s; "zz" =~ /(z)/; "w#{$1}"; end; end

t("literal") { lit }
t("literal frozen") { lit.frozen? }
t("literal same object across calls") { lit.equal?(lit) }
t("literal options") { lit_opts }
t("literal in an array") { |i| lit_arr(i % 2) }
t("interpolated") { |i| interp(i % 3) }
t("interpolated new object each call") { interp(1).equal?(interp(1)) }
t("interpolated, o") { |i| interp_once(i % 3) }
t("interpolated, o, same object") { interp_once(5).equal?(interp_once(6)) }
t("interpolated, options") { interp_opts("A") =~ "a" }
t("interpolated, to_s matches, caller kept") { kept { interp(Weird.new) } }

# ---------------------------------------------------------------- 2. callees that leave $~ alone

def m_matchp(s) = s.match?(/a(.)/)
def m_split(s) = s.split(/,/)
def m_split_limit(s) = s.split(/(,)/, 2)
def m_tr(s) = s.tr("a", "b") && s.match?(/b/)
def m_rmatchp(s) = /a/.match?(s)
def m_source = /a(b)/.source
def m_union(s) = Regexp.union(/a/, /b/).match?(s)
def m_grep_none(a) = a.any?(/b/)

t("match?") { |i| m_matchp(i.even? ? "xab" : "q") }
t("match? caller kept") { kept { m_matchp("xab") } }
t("split") { m_split("a,b,,c") }
t("split caller kept") { kept { m_split("a,b") } }
t("split limit, groups") { m_split_limit("a,b,c") }
t("tr then match?") { m_tr("abc") }
t("Regexp#match?") { m_rmatchp("cab") }
t("Regexp#match? caller kept") { kept { m_rmatchp("cab") } }
t("source") { m_source }
t("union") { m_union("xb") }
t("any? pattern caller kept") { kept { m_grep_none(%w[ab c]) } }

# ---------------------------------------------------------------- 3. callees that set the frame's $~

def m_gsub(s) = s.gsub(/a/, "b")
def m_gsub_g(s) = s.gsub(/(a)(.)/, '\2\1')
def m_sub(s) = s.sub(/a/, "")
def m_sub_bang(s) = s.dup.sub!(/a/, "")
def m_gsub_hash(s) = s.gsub(/[ab]/, "a" => "1", "b" => "2")
def m_gsub_blk(s) = s.gsub(/(a)/) { $1.upcase + $~.pre_match.size.to_s }
def m_scan(s) = s.scan(/(\w)(\d)/)
def m_scan_blk(s); r = []; s.scan(/(\w)\d/) { r << $1 << $& }; r; end
def m_match(s) = s.match(/b(.)/)
def m_match_pos(s) = s.match(/b(.)/, 2)
def m_rmatch(s) = /b(.)/.match(s)
def m_eqt(s) = s =~ /b(.)/
def m_eqt_r(s) = /b(.)/ =~ s
def m_neqt(s) = s !~ /b/
def m_aref(s) = s[/b(.)/, 1]
def m_slice(s) = s.slice(/b./)
def m_index(s) = s.index(/c/)
def m_rindex(s) = s.rindex(/a/)
def m_start(s) = s.start_with?(/ab/)
def m_partition(s) = s.partition(/b/)
def m_case_eq(s) = /b/ === s
def m_case(s)
  case s
  when /\Aa/ then 1
  when /b/, /c/ then 2
  else 3
  end
end
def m_grep(a) = a.grep(/b(.)/)
def m_grep_blk(a) = a.grep(/b(.)/) { $1 }
def m_nil_eqt(x) = x =~ /a/
def m_sym(s) = s.to_sym =~ /b/

t("gsub") { m_gsub("aXa") }
t("gsub caller kept") { kept { m_gsub("aXa") } }
t("gsub backrefs") { m_gsub_g("ab ac") }
t("sub caller kept") { kept { m_sub("bab") } }
t("sub! caller kept") { kept { m_sub_bang("bab") } }
t("gsub hash caller kept") { kept { m_gsub_hash("abc") } }
t("gsub block reads $1 caller kept") { kept { m_gsub_blk("xaya") } }
t("scan caller kept") { kept { m_scan("a1b2") } }
t("scan block caller kept") { kept { m_scan_blk("a1b2") } }
t("match caller kept") { kept { m_match("abc") } }
t("match pos") { |i| m_match_pos(i.even? ? "abcbd" : "ab") }
t("Regexp#match caller kept") { kept { m_rmatch("abc") } }
t("=~ caller kept") { kept { m_eqt("abc") } }
t("=~ no match caller kept") { kept { m_eqt("zzz") } }
t("regex =~ caller kept") { kept { m_eqt_r("abc") } }
t("!~ caller kept") { kept { m_neqt("abc") } }
t("[] caller kept") { kept { m_aref("abc") } }
t("slice caller kept") { kept { m_slice("abc") } }
t("index rindex caller kept") { kept { [m_index("abc"), m_rindex("aba")] } }
t("start_with? caller kept") { kept { m_start("abc") } }
t("partition caller kept") { kept { m_partition("abc") } }
t("=== caller kept") { kept { m_case_eq("abc") } }
t("case/when") { |i| m_case(%w[ax xb xc zz][i % 4]) }
t("case/when caller kept") { kept { m_case("xb") } }
t("grep caller kept") { kept { m_grep(%w[abc bd x]) } }
t("grep block caller kept") { kept { m_grep_blk(%w[abc bd x]) } }
t("=~ on nil") { m_nil_eqt(nil) }
t("Symbol =~ caller kept") { kept { m_sym("abc") } }

# ---------------------------------------------------------------- 4. the method's own $~ after the call

def own_gsub(s); s.gsub(/(a)/, "b"); [$~ && $~[0], $1, $&, $`, Regexp.last_match(1)]; end
def own_matchp(s); "q" =~ /(q)/; s.match?(/a/); [$~[0], $1]; end
def own_split(s); "q" =~ /(q)/; s.split(/,/); $~; end
def own_case(s); case s; when /(b)(.)/ then [$1, $2, $~.post_match]; else $~; end; end
def own_block(s); s.sub(/(b)/, ""); [1].map { [$~ && $~[0], $1] }; end
def own_block_m(s); [1].map { s =~ /(c)/ }; [$~ && $~[0], $1]; end
def own_binding(s); s.sub(/(b)/, ""); binding.eval("[$~ && $~[0], $1]"); end
def own_binding_matchp(s); s.match?(/b/); binding.eval("$~"); end
def own_eval(s); s.gsub(/(a)/, ""); eval("$1"); end
def own_last_match(s); s.sub(/(b)/, ""); Regexp.last_match(1); end
def own_last_match_p(s); s.match?(/b/); Regexp.last_match; end
def own_late(s, r); s.sub(r, ""); Regexp.last_match(0); end
def own_named(s); /(?<x>b)(?<y>.)/ =~ s; [x, y]; end
def own_if(s); $_ = s; if /b/ then :y else :n end; end
def own_two(s); s =~ /(a)/; a = $1; s =~ /(c)/; [a, $1]; end
def own_lvars(s); s.match?(/a/); local_variables; end

t("own $~ after gsub") { own_gsub("xab") }
t("own $~ after match?") { own_matchp("abc") }
t("own $~ after split") { own_split("a,b") }
t("own $~ in when") { |i| own_case(i.even? ? "abc" : "zz") }
t("own $~ in a block after sub") { own_block("abc") }
t("block's match seen by the method") { own_block_m("abc") }
t("binding after sub") { own_binding("abc") }
t("binding after match?") { own_binding_matchp("abc") }
t("eval after gsub") { own_eval("xa") }
t("Regexp.last_match after sub") { own_last_match("abc") }
t("Regexp.last_match after match?") { own_last_match_p("abc") }
t("Regexp.last_match, scope made late") { own_late("abc", /b/) }
t("named captures") { |i| own_named(i.even? ? "abc" : "zz") }
t("if /re/ reads $_") { |i| own_if(i.even? ? "abc" : "zz") }
t("two matches") { own_two("abc") }
t("local_variables after match?") { own_lvars("a") }

# ---------------------------------------------------------------- 5. callers and callees

def inner(s) = s.gsub(/a/, "x")
def inner_p(s) = s.match?(/a/)
def outer(s); "q" =~ /(q)/; inner(s); inner_p(s); [$~[0], $1]; end
def n2(s) = s.gsub(/a/, "")
def n1(s) = n2(s) + s.sub(/b/, "") + (s.match?(/c/) ? "c" : "")
def via_send(s) = s.send(:gsub, /a/, "y")
def via_public_send(s) = s.public_send(:match?, /a/)
def via_method(s) = s.method(:sub).call(/a/, "z")
def via_block_pass(a) = a.map(&:to_s).grep(/1/)

t("outer keeps its own across callees") { outer("abc") }
t("nested") { kept { n1("abc") } }
t("send caller kept") { kept { via_send("abc") } }
t("public_send") { via_public_send("abc") }
t("Method#call caller kept") { kept { via_method("abc") } }
t("block pass then grep caller kept") { kept { via_block_pass([1, 12, 3]) } }
t("thread") { Thread.new { "x" =~ /(x)/; [inner("aa"), inner_p("a"), $~[0], $1] }.value }
t("thread does not see main's") { "m" =~ /m/; Thread.new { inner("a"); $~ }.value }

# ---------------------------------------------------------------- 6. receivers that are not Strings

class MM
  def method_missing(n, *a) = [n, *a.map { |x| x.is_a?(Regexp) ? x.source : x }]
  def respond_to_missing?(*) = true
end
class OwnGsub; def gsub(r, s) = [:own, r.source, s]; def match?(r) = [:own_p, r.source]; end
class PrivGsub; private def gsub(*) = :private; def method_missing(*a) = [:mm, a.first]; end
def m_any_gsub(o) = o.gsub(/a/, "")
def m_any_matchp(o) = o.match?(/a/)

t("method_missing gsub") { m_any_gsub(MM.new) }
t("method_missing match?") { m_any_matchp(MM.new) }
t("own gsub") { m_any_gsub(OwnGsub.new) }
t("own match?") { m_any_matchp(OwnGsub.new) }
t("private gsub, explicit receiver") { m_any_gsub(PrivGsub.new) }
t("polymorphic") { |i| m_any_matchp([MM.new, "ab", OwnGsub.new][i % 3]) }
t("nil receiver") { begin; m_any_gsub(nil); rescue NoMethodError => e; e.name; end }

# ---------------------------------------------------------------- 7. redefined after the site is bound

def r_gsub(s) = s.gsub(/a/, "-")
def r_matchp(s) = s.match?(/a/)
def r_case(s) = case s when /a/ then :a else :other end
t("gsub before") { kept { r_gsub("aa") } }
t("match? before") { r_matchp("aa") }
t("case before") { r_case("aa") }
class String
  alias __orig_gsub gsub
  alias __orig_matchp match?
  def gsub(*a) = "redefined #{a.size} #{$~.inspect}"
  def match?(*a) = :redefined
end
class Regexp
  alias __orig_eqq ===
  def ===(o) = o == "zz"
end
t("gsub redefined") { kept { r_gsub("aa") } }
t("match? redefined") { r_matchp("aa") }
t("case, Regexp#=== redefined") { [r_case("aa"), r_case("zz")] }
class String
  alias gsub __orig_gsub
  alias match? __orig_matchp
end
class Regexp
  alias === __orig_eqq
end
t("gsub put back") { kept { r_gsub("aa") } }
t("match? put back") { r_matchp("aa") }
t("case put back") { [r_case("aa"), r_case("zz")] }

# ---------------------------------------------------------------- 8. tracing

def tr_gsub(s) = s.gsub(/a/, "")
def tr_matchp(s) = s.match?(/a/)
t("c_call trace") do
  calls = []
  tp = TracePoint.new(:c_call) { |e| calls << e.method_id if %i[gsub match?].include?(e.method_id) }
  tp.enable { tr_gsub("a"); tr_matchp("a") }
  calls
end
t("call trace") do
  calls = []
  tp = TracePoint.new(:call) { |e| calls << e.method_id if %i[tr_gsub tr_matchp].include?(e.method_id) }
  tp.enable { tr_gsub("a"); tr_matchp("a") }
  calls
end
t("after tracing") { [tr_gsub("ab"), tr_matchp("b")] }

# ---------------------------------------------------------------- 9. raising and recursion

def raise_in(s) = s.match?(/a/) && Integer(s)
t("raise label") { begin; raise_in("a"); rescue ArgumentError => e; e.backtrace_locations[1].label; end }

def deep_p(n) = n == 0 ? 0 : deep_p(n - 1) + ("a".match?(/a/) ? 1 : 0)
def deep_g(n) = n == 0 ? "" : deep_g(n - 1).gsub(/a/, "")
def deep_s(n) = n == 0 ? [] : "a,b".split(/,/) + deep_s(n - 1)
def deep_i(n) = n == 0 ? // : /#{deep_i(n - 1).source}/
puts "recursion with match?: " + (begin; deep_p(1_000_000).to_s; rescue SystemStackError; "SystemStackError"; end)
puts "recursion with gsub: " + (begin; deep_g(1_000_000).to_s; rescue SystemStackError; "SystemStackError"; end)
puts "recursion with split: " + (begin; deep_s(1_000_000).size.to_s; rescue SystemStackError; "SystemStackError"; end)
puts "recursion through interpolation: " + (begin; deep_i(1_000_000).to_s; rescue SystemStackError; "SystemStackError"; end)

# ---------------------------------------------------------------- 10. a refinement (in force for the rest of the file)

module RefineEq
  refine String do
    def =~(o) = :refined
    def match?(*) = :refined_p
  end
end
using RefineEq
def ref_eqt(s) = s =~ /a/
def ref_matchp(s) = s.match?(/a/)
t("refined =~") { ref_eqt("a") }
t("refined match?") { ref_matchp("a") }
t("unrefined method, defined before using") { m_matchp("xab") }
