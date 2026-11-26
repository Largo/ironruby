# Differential matrix for methods with keyword parameters beside a rest or post parameter that run without a scope (a
# light frame, MethodDefinition.IsLightFrameCandidate, IR_LIGHT_KEYWORD_REST). Run it under CRuby and under ./ir.sh with
# the shape on and off, and diff:
#
#   ruby Util/keyword-rest-light-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/keyword-rest-light-matrix.rb > /tmp/on.txt 2>&1
#   IR_LIGHT_KEYWORD_REST=0 ./ir.sh Util/keyword-rest-light-matrix.rb > /tmp/off.txt 2>&1
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/keyword-rest-light-matrix.rb > /tmp/noframes.txt 2>&1
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt && diff /tmp/mri.txt /tmp/noframes.txt
#
# Such a signature is lowered to one splat, `def m(*?args?)', and a prologue that takes the keyword hash off its end
# and binds the positionals out of it by hand (PrismAstBridge.LowerGeneralParameters): RubyOps.PopKeywordArguments,
# CheckSplatArity, ShiftSplatArgument, ShiftOptionalSplatArgument, ShiftRestSplatArguments. The probes cover the
# signatures (a rest, required and optional positionals before it, a post parameter, **rest, **nil, an anonymous
# rest), the binding (nothing, too few, a positional Hash, keywords, unknown ones, a splat, the caller's Array left
# alone), the defaults (an earlier parameter, a call), what reads the frame (binding, local_variables, block_given?,
# yield, zsuper, raise and its backtrace, caller), the other ways in (send, public_send, Method#call, to_proc), the
# method changing under a bound site, recursion, TracePoint, and prelude methods of this shape (String#each_line,
# String#lines, Data#initialize, Array#sample, Kernel#warn), and a call that passes `**h' on (IR_LIGHT_KEYWORD_SPLAT,
# RubyOps.KeywordSplatArgument and PassOnKeywordRest: nil, an empty Hash, to_hash, the caller's Hash not aliased).
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/keyword-rest-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
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

def err
  yield
rescue Exception => e
  "#{e.class}: #{e.message[0, 80]}"
end

# ---------------------------------------------------------------- 1. signatures and binding

def rest_kw(*a, k: 1); [a, k]; end
def req_rest_kw(x, *r, k:); [x, r, k]; end
def opt_rest_kw(x, y = 2, *r, k: 1); [x, y, r, k]; end
def post_kw(x, *r, z, k: 0); [x, r, z, k]; end
def all_kw(a, b = 5, *c, d, e: 2, **o); [a, b, c, d, e, o]; end
def rest_kwrest(*a, **o); [a, o]; end
def rest_nokw(x, *r, **nil); [x, r]; end
def anon_rest(*, k: 1); k; end
def post_only(x, *, z, k: 0); [x, z, k]; end

t('rest_kw()') { rest_kw }
t('rest_kw(1, 2)') { rest_kw(1, 2) }
t('rest_kw(1, k: 3)') { rest_kw(1, k: 3) }
t('rest_kw({k: 3}) positional Hash') { rest_kw({k: 3}) }
t('rest_kw(**{})') { rest_kw(**{}) }
t('rest_kw(j: 1) unknown') { err { rest_kw(j: 1) } }
t('req_rest_kw missing keyword') { err { req_rest_kw(1) } }
t('req_rest_kw too few') { err { req_rest_kw(k: 1) } }
t('req_rest_kw(1, 2, 3, k: 4)') { req_rest_kw(1, 2, 3, k: 4) }
t('opt_rest_kw 1 / 2 / 3 positionals') { |i| opt_rest_kw(*[1, 2, 3][0, i % 3 + 1]) }
t('opt_rest_kw with k') { |i| opt_rest_kw(*[1, 2, 3][0, i % 3 + 1], k: i % 2) }
t('post_kw(1, 2)') { post_kw(1, 2) }
t('post_kw(1, 2, 3, 4, k: 5)') { post_kw(1, 2, 3, 4, k: 5) }
t('post_kw too few') { err { post_kw(1) } }
t('all_kw') { |i| err { all_kw(*(1..(i % 5)).to_a, e: 7, z: 8) } }
t('rest_kwrest') { |i| rest_kwrest(*[1, 2][0, i % 3], **(i.even? ? {a: 1} : {})) }
t('rest_kwrest string key') { rest_kwrest(1, **{"s" => 2}) }
t('rest_nokw') { rest_nokw(1, 2) }
t('rest_nokw with keywords') { err { rest_nokw(1, k: 2) } }
t('rest_nokw with a positional Hash') { rest_nokw(1, {k: 2}) }
t('anon_rest') { |i| anon_rest(1, 2, k: i % 2) }
t('post_only') { post_only(1, 2, 3, k: 4) }
arr = [1, 2, 3]
t('splat leaves the caller\'s Array') { |i| [req_rest_kw(*arr, k: i % 2), post_kw(*arr), arr] }
h = {k: 9}
t('double splat leaves the caller\'s Hash') { [rest_kw(1, **h), rest_kwrest(**h), h] }
t('nil and false') { [rest_kw(nil, k: nil), post_kw(nil, false)] }

# ---------------------------------------------------------------- 2. defaults

def counter; @c = (@c || 0) + 1; end
def default_reads(x, y = x * 2, *r, k: y + 1); [x, y, r, k]; end
def default_calls(x, *r, k: counter); k.is_a?(Integer); end
t('defaults read earlier parameters') { |i| default_reads(*[1, 2][0, i % 2 + 1]) }
t('default calls a method') { default_calls(1) }

# ---------------------------------------------------------------- 3. what the frame still does

def f_binding(x, *r, k: 1); binding.local_variable_get(:r) + [binding.local_variable_get(:k)]; end
def f_locals(x, *r, z, k: 1); local_variables.sort; end
def f_block(*a, k: 1); block_given? ? yield(a, k) : :none; end
def f_raise(*a, k: 1); raise ArgumentError, "k=" + k.to_s; end
def f_caller(*a, k: 1); caller(0, 1)[0][/in .*/]; end
def f_recurse(n, *r, k: 0); n == 0 ? k : f_recurse(n - 1, *r, k: k + 1); end
def f_in_block(*a, k: 1); a.map { |v| v + k }; end
t('binding') { f_binding(1, 2, 3, k: 4) }
t('local_variables') { f_locals(1, 2) }
t('block_given? and yield') { |i| i.even? ? f_block(1, k: 2) : f_block(1, k: 2) { |a, k| [a, k] } }
t('raise') { err { f_raise(1) } }
t('raise backtrace label') { begin; f_raise; rescue => e; e.backtrace[0][/in .*/]; end }
t('caller') { f_caller }
t('recursion') { f_recurse(20, 1, 2) }
t('a block reads the parameters') { f_in_block(1, 2, k: 10) }

class ZBase
  def m(*a, k: 0); [:base, a, k]; end
  def p2(x, *r, z, k: 0); [:base, x, r, z, k]; end
end
class ZSub < ZBase
  def m(*a, k: 0); [:sub, super]; end
  def p2(x, *r, z, k: 0); r = [:changed]; [:sub, super]; end
  def explicit(*a, k: 0); [:explicit, method(:m).super_method.call(*a, k: k)]; end
end
t('zsuper with a rest and keywords') { |i| ZSub.new.m(*[1, 2][0, i % 3], k: i % 2) }
t('zsuper after reassigning the rest') { ZSub.new.p2(1, 2, 3, k: 4) }
t('super_method') { ZSub.new.explicit(1, k: 2) }

# ---------------------------------------------------------------- 4. other ways in

t('send') { send(:req_rest_kw, 1, 2, k: 3) }
PS = Object.new
def PS.g(*a, k: 1) = [a, k]
t('public_send') { PS.public_send(:g, 1, k: 2) }
t('Method#call') { method(:opt_rest_kw).call(1, k: 5) }
t('to_proc') { [[1, 2]].map(&method(:post_kw)) }
t('arity and parameters') { [method(:rest_kw).arity, method(:req_rest_kw).arity, method(:post_kw).parameters] }

class Redef
  def r(*a, k: 1); [:first, a, k]; end
end
rd = Redef.new
t('redefined after the site is bound') do |i|
  Redef.class_eval { remove_method :r; def r(*a, k: 1); [:second, a, k]; end } if i == 1500
  rd.r(i % 2, k: 0)
end

calls = []
tp = TracePoint.new(:call) { |tp| calls << tp.method_id if tp.method_id == :rest_kw }
t('TracePoint :call') do |i|
  tp.enable if i == 1000
  r = rest_kw(1, k: 2)
  tp.disable if i == 2000
  r
end
puts "traced #{calls.size} calls"

# ---------------------------------------------------------------- 5. passing `**h' on (IR_LIGHT_KEYWORD_SPLAT)

def kw_inner(*a, **o); o[:mutated] = true unless o.empty?; [a, o]; end
def pass_opts(*args, **opts); kw_inner(*args, **opts); end
def pass_only(**opts); kw_inner(**opts); end
def pass_any(x, h); kw_inner(x, **h); end
class ToHash; def to_hash = {th: 1}; end
class PassBase; def pk(a, **rest) = [:base, a, rest]; end
class PassSub < PassBase
  def pk(a, **rest); [:sub, super]; end
  def pk2(a, **rest); rest[:extra] = 1; [:sub2, method(:pk).super_method.call(a, **rest)]; end
end
held = {k: 1}
t('**opts passed on') { |i| pass_opts(*[1, 2][0, i % 3], **(i.even? ? {k: i % 2} : {})) }
t('**opts alone') { |i| pass_only(**(i.even? ? {} : {a: 1})) }
t('the callee does not alias the hash') { [pass_any(1, held), held] }
t('**nil') { pass_any(1, nil) }
t('**{}') { pass_any(1, {}) }
t('**obj with to_hash') { pass_any(1, ToHash.new) }
t('**obj without to_hash') { |i| err { pass_any(1, [1, :x, Object][i % 3]) } }
t('**string keys') { pass_any(1, {"s" => 1}) }
t('zsuper passes **rest on') { |i| PassSub.new.pk(1, **(i.even? ? {} : {k: 2})) }
t('**rest changed, then passed on') { PassSub.new.pk2(1, k: 2) }

# ---------------------------------------------------------------- 6. prelude methods of this shape

D = Data.define(:x, :y)
t('String#each_line(chomp:)') { "a\nb\n".each_line(chomp: true).to_a }
t('String#lines') { |i| "a\nb\n".lines(chomp: i.even?) }
t('String#each_line(sep, chomp:)') { "a,b,".each_line(",", chomp: true).to_a }
t('Data#initialize') { |i| i.even? ? D.new(1, 2) : D.new(x: 1, y: 2) }
t('Data#initialize missing') { err { D.new(1) } }
t('Array#sample(random:)') { [1, 2, 3].sample(3, random: Random.new(1)).sort }
SEEN = []
WARN_TO = Object.new
def WARN_TO.write(*s); SEEN.concat(s); s.sum(&:size); end
t('Kernel#warn') do
  saved, $stderr = $stderr, WARN_TO
  warn "w1", "w2"
  warn "w3", category: :experimental
  $stderr = saved
  out = SEEN.join
  SEEN.clear
  out
end
