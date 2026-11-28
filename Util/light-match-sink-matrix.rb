# Differential matrix for the frame-local $~ of a method that runs in a light frame and gives a regex to a String,
# Symbol or Regexp method that stores $~ (MethodDefinition.IsMatchSink, RubyCallFlags.HasMatchSink,
# IR_LIGHT_MATCH_SINK). CRuby's rules, which each row checks:
#
# - $~ is the frame's own: a method's match does not set its caller's $~, and leaves it as it was (nil or a match);
# - a block sees its method's $~ (and sets it), a lambda too;
# - $~ is per thread as well as per frame;
# - $1..$9, $&, $`, $', $+ and Regexp.last_match read $~; `$~ = nil' and `$~ = md' set it, any other value raises;
# - binding, eval and a string instance_eval read the frame's $~, and so does a TracePoint's binding;
# - each of =~, ===, `when', match, gsub, sub, scan, split, index, rindex, [], slice, start_with?, partition,
#   rpartition, byteindex and their bang forms sets $~ (or clears it on no match);
# - a method calling String#=~ with an object of its own: that object's =~ runs in its own frame.
#
# A method that nothing in reads its $~ keeps it in a sink of its light frame's own, and puts it into the scope if one is
# made later (RubyOps.TakeMatchSink); rows 8-13 are about that.
#
# Every method is called hot (N times) so that its sites are bound and compiled, then once more for the row.
# Run under CRuby and ./ir.sh (JIT on and off, light frames off, the switch off) and diff:
#
#   ruby Util/light-match-sink-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/light-match-sink-matrix.rb > /tmp/ir.txt && diff /tmp/mri.txt /tmp/ir.txt
#   IR_LIGHT_MATCH_SINK=0 ./ir.sh Util/light-match-sink-matrix.rb > /tmp/off.txt && diff /tmp/mri.txt /tmp/off.txt
#
# Every row must match, and the last line be `end'.

$stdout.sync = true
N = 2000

def row(name, value)
  puts "#{name}: #{value.inspect}"
end



def hot(n = N)
  r = nil
  n.times { r = yield }
  r
end

class Sinks
  def eqt(s) = s =~ /b+/
  def eqt_r(s, r) = s =~ r
  def treq(s) = /b+/ === s
  def kase(s)
    case s
    when /z/ then :z
    when /b+/ then :b
    else :none
    end
  end
  def match(s) = s.match(/b+/) ? 1 : 0
  def gsub(s) = s.gsub(/b/, "x")
  def gsub_hash(s) = s.gsub(/b/, "b" => "y")
  def sub(s) = s.sub(/b/, "x")
  def gsubb(s) = s.dup.gsub!(/b/, "x")
  def subb(s) = s.dup.sub!(/b/, "x")
  def scan(s) = s.scan(/b/)
  def split(s) = s.split(/b/)
  def index(s) = s.index(/b/)
  def rindex(s) = s.rindex(/b/)
  def aref(s) = s[/b+/]
  def slice(s) = s.slice(/b+/)
  def sliceb(s) = s.dup.slice!(/b+/)
  def aset(s) = (t = s.dup; t[/b+/] = "Q"; t)
  def startw(s) = s.start_with?(/a/)
  def part(s) = s.partition(/b+/)
  def rpart(s) = s.rpartition(/b/)
  def bindex(s) = s.byteindex(/b/)
  def sym(s) = s.to_sym =~ /b/
  def sym_match(s) = s.to_sym.match(/b/) ? 1 : 0
  def notm(s) = s !~ /b/
  def grep(a) = a.grep(/b/)
end

S = Sinks.new
STR = "abbc"
NAMES = %i[eqt treq kase match gsub gsub_hash sub gsubb subb scan split index rindex aref slice sliceb aset startw part rpart bindex sym sym_match notm]

# 1. the caller's $~ after each method, when the caller had none and when it had one of its own
def caller_nil(name)
  $~ = nil
  r = S.send(name, STR)
  [r, $~ && $~[0]]
end
def caller_set(name)
  "zz" =~ /z/
  r = S.send(name, STR)
  [r, $~ && $~[0]]
end
NAMES.each do |name|
  hot { caller_nil(name) }
  row "1. #{name}", [caller_nil(name), caller_set(name), hot { caller_set(name) }]
end
row "1a. grep", [hot { S.grep(%w[ab cd]) }, ($~ = nil; S.grep(%w[ab]); $~ && $~[0])]
row "1b. =~ with a Regexp in a variable", [hot { S.eqt_r(STR, /c/) }, ("zz" =~ /z/; S.eqt_r(STR, /c/); $~ && $~[0])]
row "1c. no match", [hot { S.eqt("xyz") }, ("zz" =~ /z/; S.eqt("xyz"); $~ && $~[0]), S.gsub("xyz"), S.split("xyz")]

# 2. methods that read their own $~ after the match: these must see it
class Observers
  def tilde(s); s =~ /b+/; $~ && $~[0]; end
  def amp(s); s.sub(/b+/, ""); $&; end
  def num(s); s.match(/(b)(c)/); [$1, $2, $3]; end
  def pre_post(s); s =~ /b+/; [$`, $']; end
  def plus(s); s =~ /(a)|(b)/; $+; end
  def last(s); s.index(/b+/); Regexp.last_match && Regexp.last_match(0); end
  def bind(s); s.scan(/b/); binding.eval("$~ && $~[0]"); end
  def ev(s); s =~ /bc/; eval("$~[0]"); end
  # not instance_eval("$~"): IronRuby's string instance_eval does not see its caller's $~ (with or without the switch)
  def ieval(s); s =~ /bc/; instance_eval("$~[0]"); end
  def blk(s); s =~ /b+/; [1].map { $~ && $~[0] }; end
  def blk_sets(s); [s].each { |x| x =~ /c/ }; $~ && $~[0]; end
  def lam(s); s =~ /b+/; l = -> { $~ && $~[0] }; l.call; end
  def assign_nil(s); s =~ /b+/; $~ = nil; [$~, $&]; end
  def assign_md(s, md); s =~ /b+/; $~ = md; [$~[0], $&]; end
  def assign_bad(s); s =~ /b+/; $~ = 1; end
  def defined_q(s); s =~ /b+/; [defined?($~), defined?($1), defined?($&)]; end
  def kase(s)
    case s
    when /(b+)/ then $1
    end
  end
  def send_last(s); s.split(/b/); Regexp.send(:last_match, 0); end
  def meth(s); s =~ /c/; method(:md0_here).call; end
  def md0_here = $~ && $~[0]
  def callee_does_not_see(s); s =~ /c/; md0_here; end
  def when_then_tilde(s)
    r = case s
        when /z/ then 1
        when /bb/ then 2
        end
    [r, $~ && $~[0]]
  end
  def gsub_block(s) = s.gsub(/b/) { $~[0].upcase }
  def scan_block(s); r = []; s.scan(/b(.)/) { r << $1 }; r; end
end
O = Observers.new
md = /(x)/.match("x")
%i[tilde amp num pre_post plus last bind ev blk blk_sets lam assign_nil defined_q kase send_last meth callee_does_not_see when_then_tilde gsub_block scan_block].each do |name|
  row "2. #{name}", [hot { O.send(name, STR) }, ("zz" =~ /z/; O.send(name, STR); $~ && $~[0])]
end
row "2. assign_md", [hot { O.assign_md(STR, md) }, ("zz" =~ /z/; O.assign_md(STR, md); $~ && $~[0])]
row "2. assign_bad", [(O.assign_bad(STR) rescue [$!.class, $!.message])]

# 3. a global alias of $~ read in the method
alias $mm $~
class AliasReader
  def read(s); s =~ /b+/; $mm && $mm[0]; end
end
row "3. alias $mm $~", [hot { AliasReader.new.read(STR) }]

# 4. threads: each thread's $~ is its own, and a method's match sets no thread's
ths = 4.times.map do |t|
  Thread.new do
    "t#{t}" =~ /t(\d)/
    r = hot(500) { S.gsub(STR) + S.split(STR).join + S.kase(STR).to_s }
    [r, $1]
  end
end
row "4. threads", ths.map(&:value)

# 5. refinements of a sink name in a method that is otherwise a sink
module RefGsub
  refine(String) { def gsub(*) = :refined }
end
class UsesRefGsub
  using RefGsub
  def g(s) = s.gsub(/b/, "x")
end
row "5. refined gsub", [hot { UsesRefGsub.new.g(STR) }, ("zz" =~ /z/; UsesRefGsub.new.g(STR); $~ && $~[0])]

# 6. String#=~ with an object of the program's own; a String subclass with a Ruby gsub
class MyMatcher
  def =~(s) = (s =~ /c/; [:mine, $~ && $~[0]])
end
class Sinks
  def eqt_obj(s, o) = s =~ o
end
class MyString < String
  def gsub(*) = [:sub, super]
end
row "6. String#=~ with an object", [hot { S.eqt_obj(STR, MyMatcher.new) }, ("zz" =~ /z/; S.eqt_obj(STR, MyMatcher.new); $~ && $~[0])]
row "6a. String subclass", [hot { S.gsub(MyString.new(STR)) }, ("zz" =~ /z/; S.gsub(MyString.new(STR)); $~ && $~[0])]
row "6b. Symbol receiver", [hot { S.eqt(:abbc) }, ("zz" =~ /z/; S.eqt(:abbc); $~ && $~[0])]

# 7. gsub, sub and =~ redefined on String after the sites are bound, and removed again
row "7. before", [hot { S.gsub(STR) }, hot { S.eqt(STR) }]
class String
  alias_method :__orig_gsub, :gsub
  remove_method :gsub
  def gsub(*a) = [:redefined, $~]
end
row "7. redefined", [S.gsub(STR), hot { S.gsub(STR) }, ("zz" =~ /z/; S.gsub(STR); $~ && $~[0])]
class String
  remove_method :gsub
  alias_method :gsub, :__orig_gsub
  remove_method :__orig_gsub
end
row "7. restored", [S.gsub(STR), hot { S.gsub(STR) }, ("zz" =~ /z/; S.gsub(STR); $~ && $~[0])]

# 8. TracePoint: enabled before the call, its :return binding sees the method's $~
def traced(s)
  s =~ /b+/
  1
end
seen = []
tp = TracePoint.new(:return) { |t| seen << t.binding.eval("$~ && $~[0]") if t.method_id == :traced }
hot { traced(STR) }
tp.enable { traced(STR) }
row "8. TracePoint :return enabled before", seen
seen = []
tp2 = TracePoint.new(:c_return) { |t| seen << [t.method_id, t.return_value] if t.method_id == :=~ }
tp2.enable { traced(STR) }
row "8a. TracePoint :c_return", seen
row "8b. after tracing", [hot { traced(STR) }, ("zz" =~ /z/; traced(STR); $~ && $~[0])]

# 9. a method that both makes its scope for another call (respond_to?, block_given?) and matches
class Mixed
  def rt(s); s =~ /b/; respond_to?(:rt) ? s.gsub(/c/, "") : nil; end
  def bg(s); s.sub(/a/, "") + (block_given? ? "y" : "n"); end
  def kw(s, x: 1) = s.split(/b/).size + x
  def opt(s, n = s.index(/c/)) = n
  def rescue_m(s)
    Integer(s)
  rescue ArgumentError
    s.gsub(/b/, "")
  end
end
mx = Mixed.new
row "9. scope made late", [hot { [mx.rt(STR), mx.bg(STR), mx.kw(STR), mx.opt(STR), mx.rescue_m(STR)] }, ("zz" =~ /z/; mx.rt(STR); mx.opt(STR); mx.rescue_m(STR); $~ && $~[0])]

# 10. TracePoint enabled by a callee after the method's match: its :line and :return bindings see the match
def mid_enabler
  $mid_tp = TracePoint.new(:line, :return) { |t| $mid_seen << [t.event, t.binding.eval("$~ && $~[0]")] if t.method_id == :mid }
  $mid_tp.enable
end
def mid(s)
  s =~ /b+/
  mid_enabler
  1
end
def mid_plain(s)
  s =~ /b+/
  1
end
hot { mid_plain(STR) }
$mid_seen = []
mid(STR)
$mid_tp.disable
row "10. TracePoint enabled mid-frame", $mid_seen

# 11. a library method that runs Ruby code after it has stored $~: sub with a Hash whose default proc calls a method
# that matches too (each frame keeps its own $~), and the same seen by a TracePoint
class Nested
  def inner(s) = s.sub(/q+/, "Q")
  def outer(s, h); r = s.sub(/b+/, h); [r, Nested.peek_line]; end
  def self.peek_line = nil
end
nh = Hash.new { |_, k| Nested.new.inner("qq" + k) }
row "11. sub with a default proc that matches", [hot { Nested.new.outer(STR, nh) }, ("zz" =~ /z/; Nested.new.outer(STR, nh); $~ && $~[0])]
seen = []
tp3 = TracePoint.new(:return) { |t| seen << [t.method_id, t.binding.eval("$~ && $~[0]")] if t.method_id == :outer || t.method_id == :inner }
tp3.enable { Nested.new.outer(STR, nh) }
row "11a. their $~ seen by a TracePoint", seen

# 12. String#=~ with an object whose =~ is private or protected: called anyway, as rb_funcall does
class PrivMatcher
  private def =~(_) = :private_called
end
class ProtMatcher
  protected def =~(_) = :protected_called
end
row "12. String#=~ with a private =~", [hot { (S.eqt_obj(STR, PrivMatcher.new) rescue $!.class) }, (S.eqt_obj(STR, ProtMatcher.new) rescue $!.class)]

# 13. a method that raises after its match, and methods suspended in Fibers between matches: each frame's $~ its own
class Raiser
  def boom(s); s.sub(/b+/, ""); raise ArgumentError, s; end
  def fib(s, t); a = s.index(/b+/); Fiber.yield a; b = t.index(/c/); [a, b]; end
end
rz = Raiser.new
r = [hot { (rz.boom(STR) rescue $!.message) }, ("zz" =~ /z/; (rz.boom(STR) rescue nil); $~ && $~[0])]
f1 = Fiber.new { rz.fib("abbc", "xc") }
f2 = Fiber.new { rz.fib("bb", "ccc") }
r << f1.resume << f2.resume << f1.resume << f2.resume
seen = []
tp4 = TracePoint.new(:raise) { |t| seen << t.binding.eval("$~ && $~[0]") if t.method_id == :boom }
tp4.enable { rz.boom(STR) rescue nil }
r << seen
row "13. raise after the match, Fibers", r

puts "end"
