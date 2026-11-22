# Differential matrix for methods with keyword parameters that run without a scope (a light frame,
# MethodDefinition.IsLightFrameCandidate). Run it under CRuby and under ./ir.sh with the shape on and off, and diff:
#
#   ruby Util/keyword-light-matrix.rb > mri.txt
#   ./ir.sh Util/keyword-light-matrix.rb > on.txt
#   IR_LIGHT_KEYWORDS=0 ./ir.sh Util/keyword-light-matrix.rb > off.txt
#   diff mri.txt on.txt && diff mri.txt off.txt
#
# Keyword parameters are lowered onto a hidden trailing parameter that holds the call's keyword hash, and a prologue
# in the body checks it and binds each keyword to its local (PrismAstBridge.LowerKeywords): the defaults run there, in
# order, only for the keywords the call left out. The probes cover the binding (required, optional, mixed, with
# positionals, after optional positionals, with a rest), the errors (missing, unknown, a positional Hash, the count),
# the defaults (a literal, an earlier parameter, a call, a raise), **rest, **nil and **opts passed on, what reads the
# frame (binding, local_variables, eval, caller_locations, a reserved word read through the binding, eval("super")),
# blocks, yield and super, the other ways in (send, Method#call, to_proc, define_method), the method changing under a
# bound site, backtraces, recursion a million deep, threads, TracePoint and a refinement.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/keyword-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.
#
# Two differences of the unchanged build from CRuby 4.0 the probes are written around: local_variables lists keyword
# parameters in declaration order where CRuby lists the required ones first (`def m(k: 1, j:)'), and an exception
# raised with an interpolated message (`raise E, "a#{x}"') reports the line of the `def' in its backtrace.

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

def guard
  yield
rescue Exception => e
  "#{e.class}: #{e.message[0, 90]}"
end

# ---------------------------------------------------------------- 1. binding

class K1
  def req(k:) = [:req, k]
  def opt(k: 1) = [:opt, k]
  def mixed(j:, k: 2) = [:mixed, j, k]
  def pos(a, b, k: 3, j:) = [:pos, a, b, k, j]
  def two_opt(k: 1, j: 2) = [:two, k, j]
  def after_opt(a, b = 2, k: 3) = [:after_opt, a, b, k]
  def with_rest(a, *r, k: 4) = [:with_rest, a, r, k]
  def with_post(a, *r, z, k: 5) = [:with_post, a, r, z, k]
  def reassign(k: 1)
    k = k * 10
    [:reassign, k]
  end
  def body(x, k: 1)
    y = x + k
    z = y * 2
    [y, z]
  end
end
o1 = K1.new

t('required') { |i| o1.req(k: i % 3) }
t('optional present') { |i| o1.opt(k: i % 3) }
t('optional absent') { |i| o1.opt }
t('mixed, both') { |i| o1.mixed(j: i % 2, k: :x) }
t('mixed, reversed order') { |i| o1.mixed(k: :x, j: i % 2) }
t('mixed, required only') { |i| o1.mixed(j: i % 2) }
t('positionals and keywords') { |i| o1.pos(i % 2, :b, j: :j) }
t('two optional, one given') { |i| o1.two_opt(j: i % 2) }
t('after an optional positional') { |i| [o1.after_opt(i % 2), o1.after_opt(1, 9), o1.after_opt(1, k: 7), o1.after_opt(1, 8, k: 6)] }
t('with a rest') { |i| [o1.with_rest(i % 2), o1.with_rest(1, 2, 3, k: 0)] }
t('with a post') { |i| [o1.with_post(i % 2, :z), o1.with_post(1, 2, 3, k: 0)] }
t('keyword reassigned') { |i| o1.reassign(k: i % 2) }
t('a body of locals') { |i| [o1.body(i % 3), o1.body(1, k: i % 2)] }
t('keywords from a Hash splat') { |i| h = { k: i % 2 }; o1.opt(**h) }
t('empty Hash splat') { |i| h = {}; o1.opt(**h) }

# ---------------------------------------------------------------- 2. errors

t('missing keyword') { |i| guard { o1.req } }
t('missing keywords') { |i| guard { o1.pos(1, 2) } }
t('two missing keywords') { |i| guard { Class.new { def m(a:, b:) = a }.new.m } }
t('unknown keyword') { |i| guard { o1.opt(z: i % 2) } }
t('unknown keywords') { |i| guard { o1.opt(z: 1, y: 2) } }
t('unknown and missing') { |i| guard { o1.req(z: 1) } }
t('positional Hash for keywords') { |i| guard { o1.opt({ k: i % 2 }) } }
t('positional Hash for required') { |i| guard { o1.req({ k: 1 }) } }
t('too many positionals') { |i| guard { o1.pos(1, 2, 3, j: 1) } }
t('too few positionals') { |i| guard { o1.pos(1, j: 1) } }
t('String keys splatted') { |i| guard { o1.opt(**{ 'k' => i % 2 }) } }
t('nil for keywords') { |i| guard { o1.opt(nil) } }

def r2k_kw(k: 0) = [:r2k, k]
class << self
  ruby2_keywords def r2k(*args) = r2k_kw(*args)
end
t('ruby2_keywords passes on') { |i| r2k(k: i % 2) }
t('ruby2_keywords, positional Hash') { |i| guard { r2k({ k: i % 2 }) } }

# ---------------------------------------------------------------- 3. defaults

$log = []
def logged(x)
  $log << x
  x
end
class K3
  def literal(k: :lit, s: 'str', a: [1, 2]) = [k, s, a]
  def earlier(a, k: a + 1, j: k * 2) = [a, k, j]
  def calls(k: logged(:k), j: logged(:j)) = [k, j]
  def raises(k: raise(ArgumentError, 'from the default')) = k
  def self_call(k: helper) = k
  def ivar(k: @v) = k
  def const(k: Limit) = k
  def block_default(k: block_given?) = k
  def yield_default(k: yield) = k
  def helper = :helped
  Limit = 42
end
o3 = K3.new

t('literal defaults') { |i| o3.literal }
t('literal defaults, String not shared') { |i| a = o3.literal[1]; b = o3.literal[1]; a.equal?(b) }
t('default reads an earlier parameter') { |i| [o3.earlier(i % 2), o3.earlier(1, k: 5), o3.earlier(1, j: 0)] }
t('defaults call methods, in order') { |i| $log.clear; v = o3.calls; [v, $log.dup] }
t('default runs only when absent') { |i| $log.clear; v = o3.calls(k: 1); [v, $log.dup] }
t('no default runs when all given') { |i| $log.clear; v = o3.calls(j: 2, k: 1); [v, $log.dup] }
t('default raises') { |i| guard { o3.raises } }
t('default that raises not run') { |i| o3.raises(k: i % 2) }
t('default calls self') { |i| o3.self_call }
t('default reads an ivar') { |i| o3.ivar }
t('default reads a constant') { |i| o3.const }
t('default asks block_given?') { |i| [o3.block_default, o3.block_default { }] }
t('default yields') { |i| [o3.yield_default { i % 2 }, o3.yield_default(k: 9) { 0 }] }

# ---------------------------------------------------------------- 4. **rest, **nil, passing on

class K4
  def rest(k: 1, **o) = [k, o]
  def only_rest(**o) = o
  def anon(**) = inner(**)
  def inner(**o) = o
  def nokey(a, **nil) = a
  def nokey_only(**nil) = :none
  def pass_on(**opts) = target(**opts)
  def pass_named(k: 1, j: 2) = target(k: k, j: j)
  def target(k: 0, j: 0) = [:target, k, j]
  def rest_mutated(**o)
    o[:added] = 1
    o
  end
end
o4 = K4.new

t('**rest with extras') { |i| o4.rest(k: 2, z: i % 2) }
t('**rest without') { |i| o4.rest }
t('only **rest') { |i| o4.only_rest(a: i % 2, b: 2) }
t('only **rest, nothing') { |i| o4.only_rest }
t('only **rest, String keys') { |i| o4.only_rest(**{ 's' => i % 2 }) }
t('anonymous ** passed on') { |i| o4.anon(a: i % 2) }
t('**nil') { |i| o4.nokey(i % 2) }
t('**nil given keywords') { |i| guard { o4.nokey(1, k: 1) } }
t('**nil only, given keywords') { |i| guard { o4.nokey_only(k: 1) } }
t('**nil given an empty splat') { |i| h = {}; o4.nokey(i % 2, **h) }
t('**opts passed on') { |i| o4.pass_on(k: i % 2) }
t('**opts passed on, unknown') { |i| guard { o4.pass_on(z: 1) } }
t('keywords passed on by name') { |i| o4.pass_named(j: i % 2) }
t('**rest is a new Hash each call') { |i| a = o4.rest_mutated; b = o4.rest_mutated; [a, a.equal?(b), a.frozen?] }
t('**rest is not the caller Hash') { |i| h = { a: 1 }; r = o4.rest_mutated(**h); [h, r] }

# ---------------------------------------------------------------- 5. the frame

class K5
  def lvars(a, j:, k: 1) = local_variables
  def bind(k: 1) = binding.local_variables
  def bind_get(k: 1) = binding.local_variable_get(:k)
  def bind_set(k: 1)
    binding.local_variable_set(:k, 5)
    k
  end
  def reserved(if: 1, class: 2, end: 3) = [binding.local_variable_get(:if), binding.local_variable_get(:class), binding.local_variable_get(:end)]
  def evals(k: 1) = eval('k + 1')
  def label(k: 1) = caller_locations(0, 1)[0].label
  def caller_label(k: 1) = callee_label
  def callee_label = caller_locations(1, 1)[0].label
  def meth(k: 1) = [__method__, k]
  def keep_binding(k: 1) = binding
  def bvars(a, k: 2) = [1].map { |x| x + a + k }
  def bset(k: 2)
    [1].each { |x| k = k + x }
    k
  end
end
o5 = K5.new

t('local_variables') { |i| o5.lvars(1, j: 2) }
t('binding.local_variables') { |i| o5.bind }
t('binding.local_variable_get') { |i| o5.bind_get(k: i % 2) }
t('binding.local_variable_set') { |i| o5.bind_set(k: i % 2) }
t('reserved-word keywords') { |i| o5.reserved(if: i % 2) }
t('eval reads a keyword') { |i| o5.evals(k: i % 2) }
t('caller_locations label') { |i| o5.label }
t('callee reads the caller label') { |i| o5.caller_label }
t('__method__') { |i| o5.meth(k: i % 2) }
t('binding kept after return') { |i| b = o5.keep_binding(k: i % 2); [b.local_variable_get(:k), b.local_variables] }
t('block reads keywords') { |i| o5.bvars(i % 2) }
t('block writes a keyword') { |i| o5.bset(k: i % 2) }

# ---------------------------------------------------------------- 6. blocks, yield, super

class K6Base
  def go(a, k: 0, **o) = [:base, a, k, o, block_given? ? yield : :noblock]
  def two(k: 0, j: 0) = [:base, k, j]
end
class K6 < K6Base
  def with_block(k: 1, &b) = [k, b.call]
  def yields(k: 1) = yield(k)
  def given(k: 1) = block_given?
  def go(a, k: 1, **o)
    super
  end
  def two(k: 1, j: 2)
    k = k + 100
    super
  end
  def explicit(k: 1) = method(:go).super_method.call(1, k: k)
end
class K6Child < K6
  def two(k: 1, j: 2) = binding.eval('super')
end
o6 = K6.new

t('a block parameter') { |i| o6.with_block(k: i % 2) { :blk } }
t('yield with a keyword') { |i| o6.yields(k: i % 2) { |x| x * 2 } }
t('block_given?') { |i| [o6.given, o6.given { }] }
t('zsuper passes the keywords') { |i| o6.go(i % 2, k: 5, z: 6) }
t('zsuper passes the default') { |i| o6.go(i % 2) }
t('zsuper passes the block') { |i| o6.go(1) { :b } }
t('zsuper passes a reassigned keyword') { |i| [o6.two(k: i % 2), o6.two] }
t('super_method with keywords') { |i| o6.explicit(k: i % 2) }
t('binding.eval super') { |i| guard { K6Child.new.two(k: i % 2) } }

# ---------------------------------------------------------------- 7. reflection and other ways in

class K7
  def m(a, b = 1, *r, c, k:, j: 2, **o, &blk) = [a, k, j]
  def s(k: 1, j:) = [k, j]
  define_method(:dm) { |k: 1, j: 2| [k, j] }
end
o7 = K7.new

t('parameters') { |i| [K7.instance_method(:m).parameters, K7.instance_method(:s).parameters] }
t('arity') { |i| [K7.instance_method(:m).arity, K7.instance_method(:s).arity, o1.method(:opt).arity, o1.method(:pos).arity] }
t('send') { |i| o7.send(:s, j: i % 2) }
t('__send__') { |i| o7.__send__(:s, k: 0, j: i % 2) }
t('public_send') { |i| o7.public_send(:s, j: i % 2) }
t('Method#call') { |i| o7.method(:s).call(j: i % 2) }
t('Method#to_proc') { |i| o7.method(:s).to_proc.call(j: i % 2) }
t('double splat in a block') { |i| [{ j: i % 2 }].map { |h| o7.s(**h) } }
t('bind_call') { |i| K7.instance_method(:s).bind_call(o7, j: i % 2) }
t('define_method with keywords') { |i| o7.dm(j: i % 2) }
t('send with a missing keyword') { |i| guard { o7.send(:s) } }

# ---------------------------------------------------------------- 8. the method changing under a bound site

class K8
  def m(k: 1) = [:first, k]
end
o8 = K8.new
t('redefined half way') do |i|
  if i == N / 2
    K8.class_eval { def m(k: 1, j: 2) = [:second, k, j] }
  end
  o8.m(k: i % 2)
end
class K8b
  def m(k: 1) = [:first, k]
end
o8b = K8b.new
t('singleton added half way') do |i|
  if i == N / 2
    def o8b.m(k: 0) = [:singleton, k]
  end
  o8b.m(k: i % 2)
end
class K8c
  def m(k: 1) = [:first, k]
end
o8c = K8c.new
t('keyword removed half way') do |i|
  if i == N / 2
    K8c.class_eval { def m = :none }
  end
  guard { o8c.m(k: i % 2) }
end

# ---------------------------------------------------------------- 9. exceptions, recursion, threads

class K9
  def boom(k: 1)
    raise ArgumentError, k.to_s
  end
  def deep(n, k: 0) = n == 0 ? k : deep(n - 1, k: k + 1)
  def forever(k: 0) = forever(k: k + 1)
end
o9 = K9.new

t('raise and its backtrace line') do |i|
  begin
    o9.boom(k: i % 2)
  rescue ArgumentError => e
    [e.message, e.backtrace[0][/\w+\.rb:\d+:in .*/], e.backtrace_locations[0].label]
  end
end
t('recursion a thousand deep') { |i| o9.deep(1000, k: i % 2) }
t('recursion a million deep') { |i| i < 3 ? guard { o9.forever }[0, 14] : :skip }
t('four threads') do |i|
  if i % 100 == 0
    4.times.map { |n| Thread.new { s = 0; 2000.times { |x| s += o1.pos(x, n, j: 1, k: 2)[3] }; s } }.map(&:value)
  else
    :skip
  end
end

# ---------------------------------------------------------------- 10. TracePoint, refinements

class K10
  def traced(k: 1) = k + 1
end
o10 = K10.new
t('TracePoint :call and :return') do |i|
  if i % 500 == 0
    seen = []
    tp = TracePoint.new(:call, :return) { |p| seen << [p.event, p.method_id, p.event == :return ? p.return_value : nil] if p.method_id == :traced }
    tp.enable { o10.traced(k: i % 2) }
    seen
  else
    o10.traced(k: i % 2)
  end
end
t('after the TracePoint') { |i| o10.traced }

module KRefine
  refine Integer do
    def plus_kw(k: 1) = self + k
  end
end
using KRefine
class K10R
  def call_refined(x, k: 2) = x.plus_kw(k: k)
end
o10r = K10R.new
t('refinement active where defined') { |i| [o10r.call_refined(i % 2), o10r.call_refined(1, k: 5)] }
