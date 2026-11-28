# Differential matrix for a call that splats a list and then a keyword hash (`f(*a, **h)', the forwards of `(*a, **h)'
# and `(*, **)'), which passes the list itself and, for an empty or nil h, no keyword argument at all
# (Arguments.TransformToCallInternal; IR_KWSPLAT_BY_REFERENCE=0 copied the list and appended h's one-or-zero element
# array); and a parameterless `super' in a method with an anonymous `*' (PrismAstBridge.CanReadBackPositionals).
#
#   ruby Util/kwsplat-forward-matrix.rb > mri.txt
#   ./ir.sh Util/kwsplat-forward-matrix.rb > on.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/kwsplat-forward-matrix.rb > scoped.txt
#   diff mri.txt on.txt && diff mri.txt scoped.txt
#
# The ruby2_keywords probes and the default-proc one differed from CRuby 4.0 until a call with an explicit `**' stopped
# making a marked hash keywords (RubyCallFlags.HasRuby2KeywordsSplat) and `**h' passed a dup of h (RubyOps.
# KeywordSplatArgument).
#
# Each probe runs 3000 times (interpreted, then compiled) and prints every distinct answer once.

N = 3000

def t(label)
  print label.ljust(56), ' => '
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

def two(a, b) = [a, b]
def opt(a, b = :d) = [a, b]
def kw(a, k: :k) = [a, k]
def kwr(*a, **k) = [a, k]
def any(*a, **k, &b) = [a, k, b && b.call]
def mut(*a, **k) = (k[:added] = 1; a << :added; [a, k])
def pos(a, b) = [a, b.class]

class ToHash; def initialize(log, h) = (@log = log; @h = h); def to_hash = (@log << :to_hash; @h); end
class Grow; def initialize(a) = @a = a; def to_hash = (@a << 3; {}); end
class BadHash; def to_hash = 1; end

def fwd(*a, **k) = any(*a, **k)
def fwd_anon(*, **) = any(*, **)
def fwd_anon_blk(*, **, &) = any(*, **, &)
def fwd_two(*, **) = two(*, **)
def fwd_opt(*, **) = opt(*, **)
def fwd_kw(*, **) = kw(*, **)
def fwd_mut(*, **) = mut(*, **)
def fwd_pos(*, **) = pos(*, **)

# ---------------------------------------------------------------- 1. empty and nil keyword splats pass nothing
t("f(*a, **{}) to a fixed-arity callee") { |i| a = [1, 2]; h = {}; two(*a, **h) }
t("f(*a, **nil)") { |i| a = [1, 2]; h = nil; two(*a, **h) }
t("f(*a, **h), h empty then not") { |i| a = [1]; h = i.even? ? {} : {k: i % 3}; (kw(*a, **h) rescue [$!.class, $!.message]) }
t("too many: f(*a, **h) with h given to two(a, b)") { |i| a = [1, 2]; h = {k: 1}; (two(*a, **h) rescue [$!.class, $!.message]) }
t("too few") { |i| a = [1]; h = {}; (two(*a, **h) rescue [$!.class, $!.message]) }
t("list changing length between calls") { |i| a = i.even? ? [1, 2] : [1, 2, 3]; h = {}; (two(*a, **h) rescue [$!.class, $!.message]) }
t("optional-parameter callee") { |i| a = i.even? ? [1] : [1, 2]; h = {}; opt(*a, **h) }
t("optional-parameter callee, keywords as positional") { |i| a = [1]; h = {k: 1}; opt(*a, **h) }
t("keywords and a positional Hash") { |i| a = [1, {p: 1}]; h = {k: 2}; kwr(*a, **h) }
t("positional Hash, empty keywords") { |i| a = [1, {p: 1}]; h = {}; kwr(*a, **h) }
t("positional Hash to a keyword callee, empty keywords") { |i| a = [{p: 1}]; h = {}; (kw(*a, **h) rescue [$!.class, $!.message]) }
t("nil splat and nil keywords") { |i| a = nil; h = nil; kwr(*a, **h) }

# ---------------------------------------------------------------- 2. the callee's arguments are its own
t("callee mutates its **k and *a") { |i| a = [1]; h = {k: 1}; r = mut(*a, **h); [r, a, h] }
t("callee mutates them, empty keywords") { |i| a = [1]; h = {}; r = mut(*a, **h); [r, a, h] }
t("frozen list and hash") { |i| a = [1, 2].freeze; h = {}.freeze; two(*a, **h) }
t("frozen hash given") { |i| a = [1].freeze; h = {k: 3}.freeze; r = mut(*a, **h); [r, h.frozen?] }
t("compare_by_identity hash") { |i| a = [1]; h = {}.compare_by_identity; h[:k] = 1; kwr(*a, **h) }
t("hash with a default proc") { |i| a = [1]; h = Hash.new { 0 }; h[:k] = 1; r = kwr(*a, **h); [r, r[1].default_proc.nil?] }

# ---------------------------------------------------------------- 3. conversions
t("non-Hash **obj: to_hash, order") { |i| log = []; a = [1]; o = ToHash.new(log, {k: 1}); [kwr(*a, **o), log] }
t("non-Hash **obj returning empty") { |i| log = []; a = [1, 2]; o = ToHash.new(log, {}); [two(*a, **o), log] }
t("to_hash that grows the splatted list") { |i| a = [1, 2]; o = Grow.new(a); [kwr(*a, **o), a] }
t("to_hash returning a non-Hash") { |i| a = [1]; o = BadHash.new; (kwr(*a, **o) rescue [$!.class, $!.message]) }
t("**obj with no to_hash") { |i| a = [1]; o = Object.new; (kwr(*a, **o) rescue [$!.class]) }
t("non-Array *obj: to_a") { |i| a = 1..2; h = {}; two(*a, **h) }
t("keyword hash from a call: evaluated after the list") { |i| a = [1, 2]; r = kwr(*a, **(a << 9; {k: 1})); [r, a] }
t("non-Symbol keys") { |i| a = [1]; h = {"s" => 1, 2 => 3}; kwr(*a, **h) }

# ---------------------------------------------------------------- 4. ruby2_keywords
ruby2_keywords def r2k_fwd(*a) = kwr(*a)
ruby2_keywords def r2k_fwd_kw(*a) = (h = {}; kwr(*a, **h))
t("ruby2_keywords hash passed on") { |i| r2k_fwd(1, k: 2) }
t("ruby2_keywords hash with **{} after it") { |i| r2k_fwd_kw(1, k: 2) }
t("ruby2_keywords hash flag after **{}") do |i|
  h = Hash.ruby2_keywords_hash({k: 1})
  a = [1, h]; e = {}
  r = kwr(*a, **e)
  [r[0].last.equal?(h), r[1]]
end

# ---------------------------------------------------------------- 5. forwarders
t("(*a, **k) forward") { |i| i.even? ? fwd(1, 2, k: 3) : fwd(1, 2) }
t("(*, **) forward") { |i| i.even? ? fwd_anon(1, 2, k: 3) : fwd_anon(1) }
t("(*, **, &) forward, with and without a block") { |i| i.even? ? fwd_anon_blk(1, k: 3) { :b } : fwd_anon_blk(1) }
t("(*, **) to a fixed-arity callee") { |i| (fwd_two(*[1, 2][0, 1 + i % 2]) rescue [$!.class, $!.message]) }
t("(*, **) to a fixed-arity callee, keywords given") { |i| (fwd_two(1, k: 1) rescue [$!.class, $!.message]) }
t("(*, **) to an optional-parameter callee") { |i| i.even? ? fwd_opt(1) : fwd_opt(1, k: 2) }
t("(*, **) to a keyword callee") { |i| (i.even? ? fwd_kw(1) : fwd_kw(1, k: 2)) rescue [$!.class, $!.message] }
t("(*, **) to a callee that mutates") { |i| a = [1]; h = {k: 1}; r = fwd_mut(*a, **h); [r, a, h] }
t("(*, **) positional Hash stays positional") { |i| fwd_pos(1, {p: 1}) }
t("(*, **) positional Hash and keywords") { |i| (fwd_pos(1, {p: 1}, k: 2) rescue [$!.class, $!.message]) }

class Redef; def m(a, b) = :first; def call(*, **) = m(*, **); end
t("callee redefined after the site bound") do |i|
  o = Redef.new
  Redef.class_eval { def m(a, b, **k) = [:second, k] } if i == 1500
  o.call(1, 2)
end
class MM; def method_missing(n, *a, **k, &b) = [n, a, k, b && b.call]; def respond_to_missing?(*) = true
  def go(*, **) = ghost(*, **); end
t("method_missing callee") { |i| i.even? ? MM.new.go(1, 2) : MM.new.go(1, k: 2) }

# ---------------------------------------------------------------- 6. super with an anonymous rest
class SupP; def m(*a, **k, &b) = [a, k, b && b.call]; def o(a, b = :d, k: :k) = [a, b, k]; end
class SupA < SupP; def m(*, **, &) = super(*, **, &); end
class SupZ < SupP; def m(*, **) = super; end
class SupZK < SupP; def m(x, *, y, k: 1, **) = super; end
class SupZO < SupP; def o(*, k: 0) = super; end
class SupZG < SupP; def m(*, **) = (g(*); super); def g(*r) = (r.clear; r << :g); end
t("super(*, **, &)") { |i| i.even? ? SupA.new.m(1, 2, k: 3) { :blk } : SupA.new.m }
t("zsuper from (*, **)") { |i| i.even? ? SupZ.new.m(1, 2, k: 3) { :blk } : SupZ.new.m }
t("zsuper from (x, *, y, k: 1, **)") { |i| i.even? ? SupZK.new.m(1, 2, 3, 4, k: 5, q: 6) : SupZK.new.m(1, 2) }
t("zsuper from (*, k: 0) to an optional-parameter parent") { |i| i.even? ? SupZO.new.o(1) : SupZO.new.o(1, 2, k: 3) }
t("zsuper after the rest was passed on and mutated") { |i| SupZG.new.m(7, 8, k: 1) }
class SupE < SupP; def m(a, *, d:) = eval('super'); end
class SupE2 < SupP; def m(*, **) = eval('super'); end
class SupE3 < SupP; def m(*, **) = [1].map { eval('super') }.first; end
t("eval('super') from (a, *, d:)") { |i| SupE.new.m(1, 2, 3, d: 4) }
t("eval('super') from (*, **)") { |i| i.even? ? SupE2.new.m(1, 2, k: 3) : SupE2.new.m }
t("eval('super') in a block from (*, **)") { |i| SupE3.new.m(1, k: 2) }
def y_splat(args) = yield(*args)
def y_splat_kw(args) = yield(*args, **{})
t("yield(*args) with a ruby2_keywords hash") { |i| y_splat([1, Hash.ruby2_keywords_hash({k: i % 2})]) { |*x, **k| [x, k] } }
t("yield(*args, **{}) keeps it positional") { |i| r = y_splat_kw([1, Hash.ruby2_keywords_hash({k: 1})]) { |*x, **k| [x, k] }; [r, Hash.ruby2_keywords_hash?(r[0].last)] }
t("yield(*args) to a lambda") { |i| y_splat([Hash.ruby2_keywords_hash({k: 1})], &->(*x, **k) { [x, k] }) }
t("Method#to_proc and Class#new pass it on positional") do |i|
  h = Hash.ruby2_keywords_hash({k: 1}); a = [1, h]; e = {}
  c = Class.new { attr_reader :v; def initialize(*x, **k) = @v = [x, k] }
  [method(:kwr).to_proc.call(*a, **e), c.new(*a, **e).v, send(:kwr, *a, **e)]
end
