# Differential matrix for a method's keyword prologue (PrismAstBridge.LowerKeywords): the separation
# checks (the last positional is not the keyword hash, the hidden keyword parameter is one), the
# missing/unknown checks guarded by KeywordArgumentsMatch, and the reads with their defaults. Written for
# perf/kwprologue (gap 38 of Util/perf/LEDGER.md), which folded the checks into one runtime call and was
# rejected; any change to the prologue's checks or their order is what it is for. Run it under CRuby and
# under ./ir.sh in every mode, and diff:
#
#   ruby Util/kw-prologue-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/kw-prologue-matrix.rb > /tmp/ir.txt 2>&1
#   ./ir.sh -X:NoJIT -X:NoOSR Util/kw-prologue-matrix.rb > /tmp/nojit.txt 2>&1
#   diff /tmp/mri.txt /tmp/ir.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# The probes are what decides which error a call gets when several things are wrong at once (a missing
# positional and an unknown keyword, missing and unknown keywords, a positional Hash), the defaults (side
# effects, their order, one reading an earlier parameter or keyword, one that raises), ruby2_keywords, and
# the other ways in: send, method_missing, super, define_method, Method#call, a module function.

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
  "#{e.class}: #{e.message[0, 90]}"
end

def opt(a, k: 1) = [a, k]
def req(a, k:) = [a, k]
def two(a, b, j: 1, k:) = [a, b, j, k]
def none(k: 1, j: 2) = [k, j]
def with_rest(a, k: 1, **o) = [a, k, o]
def opt_pos(a, b = 2, k: 1) = [a, b, k]

# ---------------------------------------------------------------- 1. which error wins

t('opt: no positional, a keyword') { err { opt(k: 2) } }
t('opt: no positional, an unknown keyword') { err { opt(z: 2) } }
t('opt: too many positionals and an unknown') { err { opt(1, 2, z: 3) } }
t('opt: unknown keywords') { err { opt(1, z: 2, y: 3) } }
t('req: missing') { err { req(1) } }
t('req: missing and unknown') { err { req(1, z: 2) } }
t('req: no positional, missing') { err { req } }
t('req: positional Hash') { err { req({k: 1}) } }
t('req: positional Hash and keywords') { err { req({k: 1}, k: 2) } }
t('two: one positional, keywords') { err { two(1, k: 2) } }
t('two: missing k, unknown z') { err { two(1, 2, z: 3) } }
t('two: all there') { two(1, 2, k: 3) }
t('none: a positional Hash') { err { none({k: 1}) } }
t('none: unknown') { err { none(x: 1) } }
t('none: string key') { err { none("k" => 1) } }
t('with_rest: anything') { with_rest(1, z: 2, k: 3) }
t('with_rest: no positional') { err { with_rest(k: 2) } }
t('opt_pos: Hash as the optional') { opt_pos(1, {k: 5}) }
t('opt_pos: Hash and keywords') { opt_pos(1, {k: 5}, k: 6) }
t('opt_pos: too many') { err { opt_pos(1, 2, 3, k: 4) } }
t('**{} and **nil') { [opt(1, **{}), opt(1, **nil), none(**{})] }
t('given, omitted, mixed') { |i| [opt(i % 2), opt(i % 2, k: i % 3), req(1, k: i % 2)] }

# ---------------------------------------------------------------- 2. defaults

$log = []
def logged(v) = ($log << v; v)
def order(a, x: logged(:x), y: logged(:y)) = [a, x, y]
def reads(a, b: a + 1, c: b * 2) = [a, b, c]
def raises_default(a, k: raise("default ran")) = [a, k]
t('defaults run in order, only omitted') { |i| $log.clear; r = (i.even? ? order(1) : order(1, x: 0)); [r, $log.dup] }
t('defaults read earlier parameters') { |i| i.even? ? reads(1) : reads(1, b: 10) }
t('a default that raises') { |i| err { i.even? ? raises_default(1) : raises_default(1, k: 2) } }
t('a default not run on an error') { $log.clear; [err { order(1, z: 1) }, $log.dup] }

# ---------------------------------------------------------------- 3. ruby2_keywords and other ways in

ruby2_keywords def fwd(*args) = req(*args)
class MM; def method_missing(n, a, k: 0) = [n, a, k]; def respond_to_missing?(*) = true; end
class KB; def m(a, k: 1) = [:base, a, k]; def m2(a, k: 1) = [:base2, a, k]; end
class KS < KB
  def m(a, k: 1) = [:sub, super]
  def m2(a, k: 1) = [:sub2, super(a, k: k + 1)]
end
class KD; define_method(:dm) { |a, k: 1| [a, k] }; end
module KF; module_function def f(a, k: 1) = [a, k]; end
t('ruby2_keywords forwards keywords') { fwd(1, k: 2) }
t('ruby2_keywords, missing') { err { fwd(1) } }
t('send') { [send(:req, 1, k: 2), err { send(:req, 1) }] }
t('method_missing with keywords') { MM.new.zz(1, k: 3) }
t('zsuper') { KS.new.m(1, k: 2) }
t('explicit super') { KS.new.m2(1) }
t('define_method') { [KD.new.dm(1), KD.new.dm(1, k: 2), err { KD.new.dm(1, z: 2) }] }
t('Method#call') { [method(:opt).call(1, k: 3), err { method(:req).call(1) }] }
t('module function') { [KF.f(1), KF.f(1, k: 2)] }
t('arity and parameters') { [method(:two).arity, method(:two).parameters] }
