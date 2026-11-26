# Differential matrix for a call that passes keywords to a method whose rest parameter takes them
# (`def m(*a)` called `m(k: 1)`): the rule unmarks the keyword hash it puts in the rest
# (RubyOps.NormalizeRestArgument), since in an array it is a positional Hash and passing the array on
# must not make it keywords again - and a ruby2_keywords method marks it the other way. Run it under
# CRuby and under ./ir.sh, and diff:
#
#   ruby Util/kwrest-call-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/kwrest-call-matrix.rb > /tmp/on.txt 2>&1
#   IR_KWREST_IN_PLACE=0 ./ir.sh Util/kwrest-call-matrix.rb > /tmp/off.txt 2>&1
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt
#
# Every probe runs N times and reports each distinct answer once, so the rule, the interpreted body
# and the compiled one all meet the case: the hash in the rest passed on (positionally, splatted,
# double-splatted), changed, frozen, kept and passed again; ruby2_keywords; `**{}', `**nil', a Hash
# given positionally to `**kw', `**nil' methods; send, public_send, method_missing, super (zsuper and
# explicit), define_method, Method#call, to_proc, Enumerator replay, and the caller's own Hash.

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

def takes_kw(*a, **kw) = [a, kw]
def takes_k(*a, k: 0) = [a, k]
def no_kw(*a) = a
def no_kw2(x, *a) = [x, a]

# ---------------------------------------------------------------- 1. what the rest holds and passes on

def rest(*a) = a
def rest_pass_splat(*a) = takes_kw(*a)
def rest_pass_last(*a) = takes_kw(a.last)
def rest_pass_dsplat(*a) = takes_kw(**a.last)
def rest_mutate(*a); a.last[:added] = 1; a; end
def rest_frozen?(*a) = a.last.frozen?
def rest_identity(*a) = a.last

t('rest gets a Hash') { |i| rest(1, k: i % 2) }
t('passed on splatted: positional') { rest_pass_splat(1, k: 2) }
t('passed on as an argument: positional') { rest_pass_last(k: 2) }
t('passed on double-splatted: keywords') { rest_pass_dsplat(k: 2) }
t('passed on to k:') { err { takes_k(*rest(k: 2)) } }
t('mutated in the method') { rest_mutate(k: 2) }
t('a fresh hash each call') { |i| h = rest_identity(k: 1); h[:n] = i; rest_identity(k: 1) }
t('not frozen') { rest_frozen?(k: 1) }
t('no keywords') { rest(1, 2) }
t('**{}') { rest(1, **{}) }
t('**nil') { rest(1, **nil) }
t('positional Hash') { rest(1, {k: 2}) }
t('string keys') { rest("s" => 1) }
t('several keywords') { rest(a: 1, b: 2, c: 3, d: 4) }
t('leading mandatory and the hash') { |i| err { no_kw2(*([1][0, i % 2]), k: 2) } }
t('hash only, to a mandatory') { no_kw2(k: 2) }

# ---------------------------------------------------------------- 2. the caller's hash

h = {k: 1}
t('**h: the callee does not alias it') { r = rest_mutate(**h); [r, h] }
h2 = {k: 1}.freeze
t('**frozen') { [rest_frozen?(**h2), rest_mutate(**h2), h2] }
t('positional h: the same object') { |i| rest_identity(h).equal?(h) }
t('positional h stays positional after') { [rest_pass_splat(h), h] }
kept = []
def keep(*a, into:) = into << a
t('a kept hash passed again') do |i|
  kept.clear
  r = rest(k: i % 2)
  kept << r.last
  [takes_kw(*r), takes_kw(*kept), kept.last.frozen?]
end

# ---------------------------------------------------------------- 3. ruby2_keywords

ruby2_keywords def r2k(*a) = takes_kw(*a)
ruby2_keywords def r2k_last(*a) = a.last
ruby2_keywords def r2k_mutate(*a); a.last[:x] = 1; takes_kw(*a); end
t('ruby2_keywords passes keywords on') { r2k(1, k: 2) }
t('ruby2_keywords, positional Hash') { r2k(1, {k: 2}) }
t('ruby2_keywords, flagged') { Hash.ruby2_keywords_hash?(r2k_last(k: 1)) }
t('ruby2_keywords, positional not flagged') { Hash.ruby2_keywords_hash?(r2k_last({k: 1})) }
t('ruby2_keywords, mutated then passed') { r2k_mutate(k: 1) }
t('ruby2_keywords later') do |i|
  Object.send(:ruby2_keywords, :rest_pass_splat) if i == 1500
  rest_pass_splat(1, k: 2)
end
t('ruby2_keywords hash into a plain rest') { |i| rest(*[1, r2k_last(k: i % 2)]) }

# ---------------------------------------------------------------- 4. other ways in

class MM
  def method_missing(name, *args) = [name, args]
  def respond_to_missing?(*) = true
end
class Base; def m(*a) = [:base, a]; def mk(*a, **kw) = [:base, a, kw]; def m2(*a) = [:base2, a]; end
class Sub < Base
  def m(*a) = [:sub, super]
  def mk(*a, **kw) = [:sub, super]
  def m2(*a) = [:sub2, super(*a)]
end
class DM; define_method(:dm) { |*a| a }; end
t('send') { send(:rest, 1, k: 2) }
t('public_send') { 1.public_send(:then) { rest(k: 2) } }
t('__send__ to a kw method') { __send__(:takes_kw, 1, k: 2) }
t('method_missing') { MM.new.foo(1, k: 2) }
t('zsuper into a rest') { Sub.new.m(1, k: 2) }
t('zsuper with **kw') { Sub.new.mk(1, k: 2) }
t('explicit super(*a)') { Sub.new.m2(1, k: 2) }
t('define_method |*a|') { DM.new.dm(1, k: 2) }
t('Method#call') { method(:rest).call(1, k: 2) }
t('to_proc') { [[1]].map(&method(:rest)) }
def rest_each(*a) = a.each { |x| yield x }
t('Enumerator replay') { to_enum(:rest_each, 1, k: 2).to_a }
t('block |*a| yielded keywords') { [1].map { |x| ->(*a) { a }.call(x, k: 2) } }

# ---------------------------------------------------------------- 5. **nil and keyword-taking methods given a Hash

def nokw(*a, **nil) = a
t('**nil, keywords') { err { nokw(1, k: 2) } }
t('**nil, positional Hash') { nokw(1, {k: 2}) }
t('**kw, positional Hash') { takes_kw(1, {k: 2}) }
t('**kw, keywords') { takes_kw(1, k: 2) }
t('k:, positional Hash') { takes_k({k: 2}) }
