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
# site builds (`f(a: x)') next to the literal it is not.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/hash-literal-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
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
# (not whether it is frozen: IronRuby freezes a String subclass key, CRuby 4.0 does not, light frame or not)
t('a String subclass key') { c = Class.new(String); h_key(c.new("x"), 1).keys[0].class == c }
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
# (not of a literal whose value calls a missing method: CRuby answers nil, IronRuby "expression", light frame or not)
def d_lit(x) = defined?({ a: x })
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
