# Differential matrix for methods whose body has a Hash literal ({a: x}, {"k" => v}, {}) and run without a
# scope (a light frame, MethodDefinition.IsLightFrameCandidate). Run it under CRuby and under ./ir.sh with such
# frames on and off, and with the JIT off, and diff:
#
#   ruby Util/hash-literal-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/hash-literal-matrix.rb > /tmp/on.txt
#   IR_LIGHT_HASH=0 ./ir.sh Util/hash-literal-matrix.rb > /tmp/off.txt
#   ./ir.sh -X:NoAdaptiveCompilation Util/hash-literal-matrix.rb > /tmp/interp.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt && diff /tmp/mri.txt /tmp/interp.txt
#
# Every probe runs N times and reports each distinct answer once, so the interpreted body, the compiled one and
# a site whose rule is already bound all meet the case: each way a literal is compiled (empty, Symbol keys up to
# three and more, String keys, other keys, `**h'), the order keys and values are evaluated and hashed in,
# duplicate keys, the String key's frozen copy, the result being a new plain Hash on every call (no default,
# no compare_by_identity, mutation not seen by the next call), literals in blocks, with yields, in defaults, in
# loops, recursion, exceptions and their backtraces, defined?, tracing, threads, and the keyword hash a call
# site builds (`f(a: x)') next to the literal it is not. Sections 7 to 13 are about the call a light frame makes
# with keywords and about what may outlive it: the callee's visibility, a callee that reads the caller's scope, the
# callee or a key's #hash changing half way through the N calls, a literal in a method whose scope is parked for
# its next call, a key's #hash or #eql? recursing a million deep through a literal, and a refinement in use where
# the caller is defined. The script ends with `alive'.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/hash-literal-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

N = 3000

def t(label)
  print label.ljust(52), ' => '
  seen = []
  at = []
  begin
    N.times do |i|
      v = yield i
      next if seen.include?(v)
      seen << v
      at << i
    end
    puts answers(seen, at)
  rescue Exception => e
    puts "#{answers(seen, at)} then #{e.class}: #{e.message[0, 70]}"
  end
end

# Each distinct answer once, in order. One that first appears after the first two calls says at which call (`@1500'):
# a change made half way must show at that call and the next, not some calls later.
def answers(seen, at) = seen.each_with_index.map { |v, k| at[k] < 2 ? v.inspect : "#{v.inspect}@#{at[k]}" }.join(', ')

def label(line) = line[/'(?:[^']*[#.])?([^']*)'/, 1]

# ---------------------------------------------------------------- 1. each way a literal is compiled

def h_empty = {}
def h_sym1(x) = { a: x }
def h_sym2(x, y) = { a: x, b: y }
def h_sym3(x, y, z) = { a: x, b: y, c: z }
def h_sym4(x) = { a: x, b: x, c: x, d: x }
def h_str(x) = { "k" => x }
def h_str2(x) = { "k" => x, "l" => x }
def h_int(x) = { 1 => x, 2.5 => x, nil => x }
def h_mixed(x) = { a: x, "b" => x, 3 => x }
def h_key(k, v) = { k => v }
def h_keys(k1, k2) = { k1 => 1, k2 => 2 }
def h_short(a, b) = { a:, b: }
def h_nested(x) = { a: { b: { c: x } }, d: [x, { e: x }] }
def h_dsplat(o) = { a: 1, **o, b: 2 }
def h_dsplat_only(o) = { **o }
def h_dsym(x) = { "a#{x}": 1 }
def h_ivar(x) = (@h = { v: x }; @h)
H_CONST = { k: 1 }
def h_const(x) = { c: H_CONST, x: x }

class ToHash; def to_hash = { th: 1 }; end
class NotHash; end

t('{}') { h_empty }
t('{} is a Hash') { h_empty.class }
t('{a: x}') { h_sym1(1) }
t('{a: x, b: y}') { h_sym2(1, nil) }
t('{a:, b:, c:}') { h_sym3(1, 2, 3) }
t('four Symbol keys') { h_sym4(:v) }
t('a String key') { h_str(1) }
t('two String keys') { h_str2(1) }
t('Integer, Float and nil keys') { h_int(1) }
t('mixed keys') { h_mixed(1) }
t('{a:, b:} shorthand') { h_short(1, 2) }
t('nested') { h_nested(1) }
t('**h') { h_dsplat({ c: 3 }) }
t('**h overrides and is overridden') { h_dsplat({ a: 9, b: 9 }) }
t('**nil') { h_dsplat(nil) }
t('**to_hash') { h_dsplat(ToHash.new) }
t('**not a hash') { begin; h_dsplat(NotHash.new); rescue TypeError => e; e.message.sub(/0x\h+/, 'X'); end }
t('{**h} is a copy') { h = { a: 1 }; r = h_dsplat_only(h); [r, r.equal?(h)] }
t('dynamic Symbol key') { h_dsym(1) }
t('stored in an ivar') { h_ivar(1) }
t('a constant value') { h_const(1) }

# ---------------------------------------------------------------- 2. order and keys

$log = []
def logged(x) = ($log << x; x)
class LoudKey
  attr_reader :n
  def initialize(n) = @n = n
  def hash = ($log << "hash#{@n}"; @n.hash)
  def eql?(o) = ($log << "eql#{@n}"; o.is_a?(LoudKey) && o.n == @n)
  def inspect = "K#{@n}"
end
def o_order = { logged(:k1) => logged(1), logged(:k2) => logged(2), logged(:k3) => logged(3) }
def o_order4 = { logged("k1") => logged(1), logged(:k2) => logged(2), logged(3) => logged(3), logged(:k4) => logged(4) }
def o_loud(a, b) = { a => logged(:va), b => logged(:vb) }
def o_dup(k) = { k => 1, :x => 2, k => 3 }
def o_dup_str(a, b) = { a => 1, b => 2 }

# the log is copied and the Hash inspected right after the call: comparing answers calls #hash and #eql? again
t('evaluation order, three') { $log = []; r = o_order; [r, $log.dup] }
t('evaluation order, four') { $log = []; r = o_order4; [r, $log.dup] }
t('hash and eql? order') { $log = []; r = o_loud(LoudKey.new(1), LoudKey.new(2)); [r.inspect, $log.dup] }
t('equal keys: hash and eql?') { $log = []; r = o_loud(LoudKey.new(1), LoudKey.new(1)); [r.inspect, $log.dup] }
t('duplicate keys keep the last value') { o_dup(:x) }
t('duplicate keys keep the first key') { a = +"k"; b = +"k"; r = o_dup_str(a, b); [r, r.keys[0].equal?(a)] }
t('a String key is a frozen copy') { s = +"key"; r = h_key(s, 1); k = r.keys[0]; [k.frozen?, k.equal?(s), s.frozen?, (s << "!")] }
t('a frozen String key is kept') { s = "fk".freeze; h_key(s, 1).keys[0].equal?(s) }
t('a String subclass key is kept, unfrozen') { c = Class.new(String); k = c.new("x"); r = h_key(k, 1).keys[0]; [r.class == c, r.equal?(k), r.frozen?] }
t('a String subclass key by []=') { c = Class.new(String); k = c.new("x"); h = {}; h[k] = 1; [h.keys[0].equal?(k), k.frozen?] }
t('an Array key') { a = [1]; r = h_key(a, 2); [r, r.keys[0].equal?(a)] }
t('a NaN key') { r = h_key(Float::NAN, 1); [r.size, r.key?(Float::NAN)] }
t('0 and 0.0 keys') { h_keys(0, 0.0) }
t('1 and 1r keys') { h_keys(1, 1r) }

# ---------------------------------------------------------------- 3. the result

def r_fill(n); h = {}; i = 0; while i < n; h[i] = i * i; i += 1; end; h; end
def r_mutate; h = { a: 1 }; h[:b] = 2; h; end
def r_cbi; h = {}; h.compare_by_identity; h; end
def r_cbi_after; {} ; end

t('a new Hash on every call') { h_sym1(1).equal?(h_sym1(1)) }
t('{} is new on every call') { h_empty.equal?(h_empty) }
t('mutation does not leak') { r = h_sym1(1); r[:z] = 1; h_sym1(1) }
t('mutation of {} does not leak') { r = h_empty; r[:z] = 1; h_empty }
t('filled in a loop') { r_fill(5) }
t('mutated before return') { r_mutate }
t('compare_by_identity not inherited') { r_cbi.compare_by_identity?; r_cbi_after.compare_by_identity? }
t('no default, no default_proc') { r = h_sym1(1); [r.default, r.default_proc, r[:missing]] }
t('not frozen') { [h_sym1(1).frozen?, h_empty.frozen?, h_str(1).frozen?] }
t('ordering') { h_sym3(3, 2, 1).to_a }
t('Symbol keys look up') { r = h_sym4(1); [r[:a], r[:d], r.key?(:c), r.fetch(:b)] }
t('String keys look up') { r = h_str2(1); [r["k"], r["l"], r.key?("k")] }
t('rehash after mutating an Array key') { a = [1]; r = h_key(a, 1); a << 2; [r[a], r.rehash[a]] }
t('instance_variables of the result') { h_sym1(1).instance_variables }
t('singleton class of the result') { r = h_sym1(1); def r.x = 1; [r.x, h_sym1(1).respond_to?(:x)] }

# ---------------------------------------------------------------- 4. where it is written

def w_block(a) = a.map { |x| { x => x } }
def w_block_out(a, y) = a.map { |x| { x => y } }
def w_block_and_hash(a) = [{ n: a.size }, a.map { |x| x * 2 }]
def w_yield(x) = { a: yield(x) }
def w_yield2(x) = (h = { a: x }; yield h; h)
def w_default(x, h = {}) = (h[:x] = x; h)
def w_default_sym(x, h = { d: 1 }) = (h[:x] = x; h)
def w_while(n); r = []; i = 0; while i < n; r << { i: i }; i += 1; end; r; end
def w_case(x)
  case x
  when Integer then { int: x }
  when String then { str: x }
  else {}
  end
end
def w_if(x) = x ? { t: x } : { f: x }
def w_and(x) = x && { v: x }
def w_return(x); return { early: x } if x > 0; { late: x }; end
def w_arg(x) = w_take({ a: x })
def w_take(h) = h
def w_recv(x) = { a: x }.merge({ b: x })
def w_index(x) = { a: x }[:a]
def w_send(x) = send(:h_sym1, x)
def w_attr(o, x) = (o.v = { v: x }; o.v)
S = Struct.new(:v)
def w_elem(h, x) = (h[:k] = { v: x }; h)
def w_range(x) = { r: (1..x) }
def w_str(x) = { s: "a#{x}" }
def w_arr(x) = [{ a: x }, [{ b: x }]]
def w_recur(n) = n == 0 ? {} : { n => w_recur(n - 1) }
def w_proc(x) = proc { { p: x } }
def w_lambda(x) = -> { { l: x } }
define_method(:w_dm) { |x| { dm: x } }
def w_binding(x) = ({ b: binding.local_variable_get(:x) })
def w_lvars(x) = { l: local_variables }
def w_method(x) = { m: __method__ }
def w_bg(x) = { bg: block_given? }
def w_caller(x) = { c: caller(0, 1).map { |l| label(l) } }
def w_eval(x) = eval("{ e: x }")
def w_inst_eval(x) = instance_eval { { ie: x } }

t('in a block') { w_block([1, 2]) }
t('in a block, reading an outer local') { w_block_out([1, 2], :y) }
t('beside a literal block') { w_block_and_hash([1, 2]) }
t('with yield') { w_yield(2) { |v| v * 3 } }
t('yields the hash it returns') { w_yield2(1) { |h| h[:y] = 1 } }
t('{} default') { [w_default(1), w_default(2)] }
t('{} default given') { h = {}; [w_default(1, h).equal?(h), h] }
t('{d: 1} default') { [w_default_sym(1), w_default_sym(2)] }
t('in a while loop') { w_while(3) }
t('in case/when') { [w_case(1), w_case("s"), w_case(nil)] }
t('in ?:') { [w_if(1), w_if(nil)] }
t('in &&') { [w_and(1), w_and(nil)] }
t('early return') { [w_return(1), w_return(-1)] }
t('as an argument') { w_arg(1) }
t('as a receiver') { w_recv(1) }
t('indexed') { w_index(1) }
t('through send') { w_send(1) }
t('attribute assignment') { w_attr(S.new, 1) }
t('element assignment') { w_elem({}, 1) }
t('with a Range') { w_range(2) }
t('with an interpolation') { w_str(2) }
t('in an Array literal') { w_arr(1) }
t('recursion') { w_recur(3) }
t('in a proc called later') { w_proc(1).call }
t('in a lambda called later') { w_lambda(1).call }
t('define_method') { w_dm(1) }
t('with binding') { w_binding(1) }
t('with local_variables') { w_lvars(1) }
t('with __method__') { w_method(1) }
t('with block_given?') { [w_bg(1), w_bg(1) {}] }
t('with caller') { w_caller(1) }
t('with eval') { w_eval(1) }
t('with instance_eval') { w_inst_eval(1) }

# ---------------------------------------------------------------- 5. errors, defined?, tracing, threads

class HashRaises; def hash = raise(IndexError, "hash raised"); end
def e_value(x) = { a: x.fetch(9) }
def e_key(x) = { x.fetch(9) => 1 }
def e_loud(k) = { k => 1 }
def e_four(x) = { a: 1, b: 2, c: 3, d: x.fetch(9) }
def e_str(x) = { "s" => x.fetch(9) }
def d_lit(x) = defined?({ a: x })
def d_missing(x) = defined?({ a: x.nope })
def d_missing_key(x) = defined?({ x.nope => 1, b: 2 })
def d_empty = defined?({})
def traced(x) = { tr: x }

t('raise in a value') { begin; e_value([]); rescue IndexError => e; [e.message, e.backtrace[0, 3].map { |l| label(l) }]; end }
t('raise in a key') { begin; e_key([]); rescue IndexError => e; [e.message, e.backtrace[0, 3].map { |l| label(l) }]; end }
t('raise in #hash of a key') { begin; e_loud(HashRaises.new); rescue IndexError => e; [e.message, e.backtrace[1, 2].map { |l| label(l) }]; end }
t('raise in a fourth value') { begin; e_four([]); rescue IndexError => e; [e.message, e.backtrace[1, 2].map { |l| label(l) }]; end }
t('raise in a String-keyed value') { begin; e_str([]); rescue IndexError => e; [e.message, e.backtrace[1, 2].map { |l| label(l) }]; end }
t('raise line') { begin; e_value([]); rescue IndexError => e; e.backtrace_locations[1].lineno - __LINE__; end }
t('defined? of a literal') { d_lit(1) }
t('defined? {}') { d_empty }
t('defined? of a literal whose value is a missing method') { [d_missing(1), d_missing_key(1)] }
t('TracePoint :call sees it') do
  seen = []
  tp = TracePoint.new(:call) { |e| seen << e.method_id if e.method_id == :traced }
  tp.enable { traced(1) }
  seen
end
t('TracePoint :call, binding') do
  seen = []
  tp = TracePoint.new(:call) { |e| seen << e.binding.local_variable_get(:x) if e.method_id == :traced }
  tp.enable { traced(7) }
  seen
end
t('TracePoint :c_call in a value') do
  seen = []
  tp = TracePoint.new(:c_call) { |e| seen << e.method_id if e.method_id == :fdiv }
  tp.enable { h_sym1(1.fdiv(2)) }
  seen
end
t('after the TracePoint') { traced(-5) }
t('threads') do |i|
  i % 100 == 0 ? Array.new(4) { |k| Thread.new { Array.new(50) { |j| h_sym2(k, j) }.last } }.map(&:value) : true
end

# ---------------------------------------------------------------- 6. the keyword hash of a call site

def kw(a:, b: 2) = [a, b]
def kw_rest(**o) = o
def kw_opt(a: 1) = a
def pos(h) = h
def pos_rest(*r) = r
def pos_opt(x = :none, h = {}) = [x, h]
ruby2_keywords def r2k(*args) = args
def k_call(x) = kw(a: x)
def k_call2(x) = kw(a: x, b: x)
def k_rest(x) = kw_rest(a: x, "s" => x)
def k_pos(x) = pos(a: x)
def k_pos_rest(x) = pos_rest(1, a: x)
def k_opt(x) = pos_opt(a: x)
def k_dsplat(h) = kw(**h)
def k_dsplat_more(h) = kw_rest(z: 1, **h)
def k_empty_dsplat(h) = pos_rest(**h)
def k_r2k(x) = r2k(a: x)
def k_lit_vs_kw(x) = [pos_rest({ a: x }), pos_rest(a: x)]
def k_kw_opt(x) = kw_opt(a: x)
def k_unknown(x) = kw(a: x, c: x)
def k_send(x) = send(:kw, a: x)
def k_yield(x) = yield(a: x)
def k_block(x) = [x].map { |v| kw(a: v) }
def k_lib(s) = s.encode("US-ASCII", undef: :replace, invalid: :replace)
def k_struct(x) = KS.new(a: x)
KS = Struct.new(:a, keyword_init: true)
def k_dsplat_nil(x) = kw_rest(**nil)
class KMM; def method_missing(n, *a, **k) = [n, a, k]; def respond_to_missing?(*) = true; end
def k_mm(o, x) = o.anything(a: x)

t('f(a: x)') { k_call(1) }
t('f(a: x, b: y)') { k_call2(1) }
t('f(a: x, "s" => x) to **o') { k_rest(1) }
t('f(a: x) to a positional') { r = k_pos(1); [r, r.frozen?] }
t('f(1, a: x) to *r') { k_pos_rest(1) }
t('f(a: x) to optionals') { k_opt(1) }
t('f(**h)') { k_dsplat({ a: 1 }) }
t('f(**h) does not change h') { h = { a: 1 }; k_dsplat(h); h }
t('f(z: 1, **h)') { h = { y: 2 }; [k_dsplat_more(h), h] }
t('f(**{}) passes nothing') { k_empty_dsplat({}) }
t('ruby2_keywords') { r = k_r2k(1); [r, Hash.ruby2_keywords_hash?(r.last)] }
t('literal and keywords to *r') { k_lit_vs_kw(1) }
t('optional keyword') { k_kw_opt(5) }
t('unknown keyword') { begin; k_unknown(1); rescue ArgumentError => e; e.message; end }
t('send with keywords') { k_send(1) }
t('yield with keywords') { k_yield(1) { |**k| k } }
t('yield keywords to a positional') { k_yield(1) { |h| h } }
t('keywords in a block') { k_block(1) }
t('keywords to a library method') { k_lib("aé") }
t('keyword_init Struct') { k_struct(1).a }
t('**nil') { k_dsplat_nil(1) }
t('keywords to method_missing') { k_mm(KMM.new, 1) }

def guard
  yield
rescue Exception => e
  "#{e.class}: #{e.message[0, 60]}"
end

# ---------------------------------------------------------------- 7. the callee's visibility
# A call a light frame makes with the frame in place of its scope must still be the source's call: private
# and protected methods, method_missing.
class Vis
  def pub(a:) = [:pub, a]
  def call_priv(x) = priv(a: x)
  def call_priv_self(x) = self.priv(a: x)
  def call_prot(o, x) = o.prot(a: x)
  private def priv(a:) = [:priv, a]
  protected def prot(a:) = [:prot, a]
end
class Other
  def call_prot(o, x) = o.prot(a: x)
  def call_priv(o, x) = o.priv(a: x)
end
class Missing
  def method_missing(name, *args, **kw) = [name, args, kw]
  def respond_to_missing?(*) = true
  def go(x) = nope(1, a: x)
  def go_self(x) = self.nope(a: x)
end

t('private, implicit self') { |i| Vis.new.call_priv(i % 2) }
t('private, self.') { |i| Vis.new.call_priv_self(i % 2) }
t('protected, same class') { |i| Vis.new.call_prot(Vis.new, i % 2) }
t('protected, other class') { |i| guard { Other.new.call_prot(Vis.new, i % 2) } }
t('private, other class') { |i| guard { Other.new.call_priv(Vis.new, i % 2) } }
t('method_missing') { |i| Missing.new.go(i % 2) }
t('method_missing, self.') { |i| Missing.new.go_self(i % 2) }

# ---------------------------------------------------------------- 8. a callee that reads the caller's scope
def scope_reader(x) = eval("x + 1", binding, __FILE__, __LINE__).then { |v| { v: v } }
def lvars(x, y = 2) = { names: local_variables, sum: x + y }
def blk_kw(x) = { given: block_given?, v: Integer(x.to_s, exception: false) }
def warn_kw(x) = (warn("w", uplevel: 0) if x < 0)
def raise_kw(x) = raise(ArgumentError, "m#{x}", cause: nil)
def send_kw(o, x) = o.send(:pub, a: x)
def psend_kw(o, x) = o.public_send(:pub, a: x)
def meth_kw(o, x) = o.method(:pub).call(a: x)
def struct_kw(x) = KS2.new(a: x, b: { c: x })
KS2 = Struct.new(:a, :b, keyword_init: true)
t('eval with binding, then a literal') { |i| scope_reader(i % 2) }
t('local_variables in a literal') { |i| lvars(i % 2) }
t('block_given? and a keyword call') { |i| [blk_kw(i % 2), blk_kw(i % 2) { }] }
t('warn with uplevel, not reached') { |i| warn_kw(i % 2) }
t('raise with cause:') { |i| guard { raise_kw(i % 2) } }
t('send with keywords') { |i| send_kw(Vis.new, i % 2) }
t('public_send with keywords') { |i| psend_kw(Vis.new, i % 2) }
t('Method#call with keywords') { |i| meth_kw(Vis.new, i % 2) }
t('keyword_init Struct') { |i| struct_kw(i % 2).to_h }

# ---------------------------------------------------------------- 9. the callee changes in the middle
# The site's rule is bound by then: each change must be seen by the next call.
class Chg
  def f(a:) = [:v1, a]
  def go(x) = f(a: x)
  def go_lit(x) = f(**{ a: x })
end
c = Chg.new
t('callee redefined half way') do |i|
  Chg.class_eval { def f(a:, b: 9) = [:v2, a, b] } if i == N / 2
  c.go(i % 2)
end
t('callee made private half way') do |i|
  Chg.send(:private, :f) if i == N / 2
  c.go(i % 2)
end
t('singleton method half way') do |i|
  c.define_singleton_method(:f) { |a:| [:single, a] } if i == N / 2
  c.go(i % 2)
end
t('callee takes a positional Hash half way') do |i|
  Chg.class_eval { def f(h = nil, **k) = [:v3, h, k] } if i == N / 2
  Chg.new.go(i % 2)
end
t('callee takes only a positional Hash half way') do |i|
  Chg.class_eval { def f(h) = [:v4, h] } if i == N / 2
  Chg.new.go(i % 2)
end
t('callee removed half way') do |i|
  Chg.send(:remove_method, :f) if i == N / 2
  guard { Chg.new.go(i % 2) }
end
class Chg2
  def f(a:) = a
  def go(x) = f(a: x)
end
t('callee replaced by one that reads the scope') do |i|
  Chg2.class_eval { def f(a:) = block_given? ? [:blk, a] : [:none, a] } if i == N / 2
  [Chg2.new.go(i % 2), Chg2.new.go(i % 2) { }]
end
t('unknown keyword after a redefinition') do |i|
  Chg2.class_eval { def f(b:) = b } if i == N / 2
  guard { Chg2.new.go(i % 2) }
end


# ---------------------------------------------------------------- 10. a literal whose pieces change under it
class HK
  attr_reader :v
  def initialize(v) = @v = v
  def hash = @v.hash
  def eql?(o) = o.is_a?(HK) && @v == o.v
end
def two(a, b) = { a => 1, b => 2 }
t('keys that are eql? or not') { |i| two(HK.new(1), HK.new(i % 2)).size }
t('#hash redefined half way') do |i|
  HK.class_eval { def hash = 7 } if i == N / 2
  two(HK.new(1), HK.new(i % 2)).size
end
t('#eql? redefined half way') do |i|
  HK.class_eval { def eql?(o) = false } if i == N / 2
  two(HK.new(1), HK.new(1)).size
end
t('String#freeze redefined') do |i|
  String.class_eval { alias_method :__f, :freeze; def freeze = __f } if i == N / 2
  s = +"k"
  h = { s => i % 2 }
  [h.keys[0].frozen?, s.frozen?, h.keys[0].equal?(s)]
end
t('Hash.new redefined, a literal does not call it') do |i|
  Hash.singleton_class.class_eval { alias_method :__n, :new; def new(*) = raise("called") } if i == N / 2
  r = guard { { a: i % 2 } }
  Hash.singleton_class.class_eval { alias_method :new, :__n } if i == N - 1
  r
end

# ---------------------------------------------------------------- 11. a literal in a method whose scope is parked
# A method whose literal blocks all reuse their Procs keeps its scope for its next call (IR_PARKED_SCOPES,
# Util/parked-scope-matrix.rb). With a Hash literal or keywords in it, it is a light frame that parks: nothing of
# one call may show in the next, whether the call returned, raised, threw or returned from the block.
class ParkedLit
  def initialize = @s = [1, 2, 3]
  def each = 3.times { |i| yield @s[i] }
  def each_lit = 3.times { |i| yield({ i: i, v: @s[i] }) }
  def each_kw = 3.times { |i| yield(i: i, v: @s[i]) }
  def lit_local(x); h = { n: x }; 3.times { |i| h[:n] += @s[i] }; h; end
  def lit_in_block(x); r = nil; 3.times { |i| r = { x => i } }; r; end
  def lit_default(x, h = {}); 3.times { |i| h[i] = x }; h; end
  def kw_out(x); r = 0; 3.times { |i| r += kw(a: x, b: i) }; r; end
  def kw(a:, b: 0) = a + b
  def keep(x); h = { x: x }; 3.times { |i| h[i] = binding if i == 1 }; h; end
  def lam(x) = { f: [0, 1, 2].map { |i| -> { i + x } } }
  def raise_mid(x); h = { x: x }; 3.times { |i| raise IndexError, h.inspect if i == x }; h; end
  def thrown(x); h = { x: x }; 3.times { |i| throw :out, h if i == x }; h; end
  def early(x); h = { x: x }; 3.times { |i| return h.merge(i: i) if i == x }; h; end
  def rec(n); h = { n: n }; 1.times { |_| h[:d] = n > 0 ? rec(n - 1) : nil }; h; end
end

v = ParkedLit.new
t('yield a literal from a parked block') { |i| a = []; v.each_lit { |h| a << h }; [a, a.map(&:object_id).uniq.size] }
t('yield keywords from a parked block') { |i| a = []; v.each_kw { |**h| a << h }; a }
t('a literal in a local, changed by the block') { |i| v.lit_local(i % 2) }
t('the result is not the next call\'s') { |i| r = v.lit_local(0); r[:n] += 100; [r, v.lit_local(0)] }
t('a literal made in the block') { |i| v.lit_in_block(i % 2) }
t('a `{}\' default') { |i| [v.lit_default(i % 2), v.lit_default(1).equal?(v.lit_default(1))] }
t('a default given by the caller') { |i| h = { z: 1 }; v.lit_default(i % 2, h).equal?(h) }
t('keywords from a parked block') { |i| v.kw_out(i % 2) }
t('binding kept, each call its own literal') do |i|
  b1 = v.keep(1)[1]; b2 = v.keep(2)[1]
  [b1.local_variable_get(:h)[:x], b2.local_variable_get(:h)[:x], b1.local_variable_get(:h).equal?(b2.local_variable_get(:h)), b1.local_variable_get(:x)]
end
t('lambdas in a literal') { |i| a = v.lam(i % 2)[:f]; b = v.lam(10)[:f]; [a.map(&:call), b.map(&:call)] }
t('raise in the block, then a normal call') { |i| r = (v.raise_mid(i % 4) rescue $!.message); [r, v.raise_mid(9)] }
t('throw from the block') { |i| [catch(:out) { v.thrown(i % 4) }, v.thrown(9)] }
t('return from the block') { |i| [v.early(i % 4), v.early(9)] }
t('recursion through the block') { |i| v.rec(3) }
t('threads') do |i|
  next :skipped unless i % 500 == 0
  pvt_r = Array.new(4) { |pvt_k| Thread.new { Array.new(50) { v.lit_local(pvt_k) } } }.map(&:value)
  pvt_r.map(&:uniq)
end

# ---------------------------------------------------------------- 12. recursion through a key's #hash or #eql?
# The helper that makes a literal is no call site, but it calls the keys' #hash and #eql?: a method whose only call
# is that helper may still recurse, and its light frame must check the stack (LightFrame.MakesCalls), or the CLR
# stack overflows and ends the process. Each probe recurses a million deep, once (each level is a call, so the body
# runs interpreted and compiled), and prints SystemStackError. The Integer keys after the one that recurses are for
# CRuby: they keep its VM stack running out before its machine stack, whose overflow in C it cannot rescue.
class DeepKey
  def initialize(nxt) = @nxt = nxt
  def hash
    @h = { @nxt => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 6, 7 => 7, 8 => 8,
           9 => 9, 10 => 10, 11 => 11, 12 => 12, 13 => 13, 14 => 14, 15 => 15, 16 => 16 }
    0
  end
end
class DeepMixedKey
  def initialize(nxt) = @nxt = nxt
  def hash
    @h = { @nxt => 0, :a => 1, "b" => 2, 3 => 3, 4.5 => 4, nil => 5, true => 6, false => 7, 8 => 8,
           9 => 9, 10 => 10, 11 => 11, 12 => 12, 13 => 13, 14 => 14, 15 => 15, 16 => 16 }
    0
  end
end
class DeepEql
  def initialize(a, b) = (@a = a; @b = b)
  def hash = 0
  def eql?(other)
    @h = { @a => 0, @b => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 6, 7 => 7, 8 => 8,
           9 => 9, 10 => 10, 11 => 11, 12 => 12, 13 => 13, 14 => 14, 15 => 15, 16 => 16 } if @a
    false
  end
end
class DeepKeywordKey
  def initialize(nxt) = @nxt = nxt
  def hash
    @h = sink(@nxt => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 6, 7 => 7, 8 => 8,
              9 => 9, 10 => 10, 11 => 11, 12 => 12, 13 => 13, 14 => 14, 15 => 15, 16 => 16)
    0
  end
  def sink(h) = h
end
class DeepToHash
  def initialize(nxt) = @nxt = nxt
  def to_hash
    @a = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, { **@nxt }]
    {}
  end
end

def deep_chain(depth) = (n = nil; depth.times { n = yield(n) }; n)
def deep(label)
  print label.ljust(52), ' => '
  puts(begin; yield; "returned"; rescue SystemStackError => e; e.class; end)
end

DEEP = 1_000_000
deep('#hash of the key of a literal') { deep_chain(DEEP) { |n| DeepKey.new(n) }.hash }
deep('#hash of a key among literal ones') { deep_chain(DEEP) { |n| DeepMixedKey.new(n) }.hash }
deep('#eql? of two keys whose #hash is the same') { a, b = deep_chain(DEEP) { |n| n ? [DeepEql.new(*n), DeepEql.new(*n)] : [nil, nil] }; { a => 0, b => 1 } }
deep('#hash of the key of a call\'s hash') { deep_chain(DEEP) { |n| DeepKeywordKey.new(n) }.hash }
deep('#to_hash of a **splat') { { **deep_chain(DEEP) { |n| DeepToHash.new(n || {}) } } }

# ---------------------------------------------------------------- 13. a refinement where the caller is defined
# Last in the file: a method defined where a refinement is in use is never a light frame, so every
# method after `using' is one with a scope.
module Loud
  refine String do
    def center(width, pad = " ", loud: false) = loud ? "LOUD" : super(width, pad)
    def tag(a:) = "refined #{a}"
  end
end
class String; def tag(a:) = "plain #{a}"; end
def unrefined_tag(s, x) = s.tag(a: x)
using Loud
def refined_tag(s, x) = s.tag(a: x)
def refined_center(s) = s.center(5, loud: true)
t('unrefined caller') { |i| unrefined_tag("s", i % 2) }
t('refined caller') { |i| refined_tag("s", i % 2) }
t('refined, keyword only it knows') { |_| refined_center("s") }
puts "alive"
