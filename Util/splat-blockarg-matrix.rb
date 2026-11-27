# Differential matrix for a call that splats and passes a block argument (`f(*a, &b)', `f(x, *a, &b)', `...'): the
# order CRuby 4.0 evaluates them in, and when it passes a trailing splat's list itself, expanded after the block
# argument's #to_proc - a block argument that is a variable, a constant or a literal - and when it copies it first - any
# other block argument, a method call say (MethodCall.TransformRead; IR_SPLAT_BY_REFERENCE=0 always copies, as IronRuby
# used to).
#
#   ruby Util/splat-blockarg-matrix.rb > mri.txt
#   ./ir.sh Util/splat-blockarg-matrix.rb > on.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/splat-blockarg-matrix.rb > scoped.txt
#   diff mri.txt on.txt && diff mri.txt scoped.txt
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

def f(*x, &b) = [x, b && b.call]
def two(a, b) = [a, b]
def opt(a, b = :d) = [a, b]
def kw(a, b, k: :k) = [a, b, k]
def blk(a, b, &k) = [a, b, k && k.call]
def keeps(*x) = (x << :kept; x)

class Grow; def initialize(a) = @a = a; def to_proc = (a = @a; a << 3; proc { :g }); end
class Shrink; def initialize(a) = @a = a; def to_proc = (a = @a; a.pop; proc { :s }); end
class ToA; def initialize(log) = @log = log; def to_a = (@log << :to_a; [7, 8]); end
class LogProc; def initialize(log) = @log = log; def to_proc = (@log << :to_proc; proc { :p }); end

# ---------------------------------------------------------------- 1. order, and the list passed itself
t("receiver, args, to_a, to_proc order") do |i|
  log = []
  r = send(:f, (log << :arg; 1), *ToA.new(log), &LogProc.new(log))
  [r, log]
end
t("&local: to_proc grows the list, callee sees it") { |i| a = [1, 2]; g = Grow.new(a); [f(*a, &g), a] }
t("&local, a leading argument") { |i| a = [1, 2]; g = Grow.new(a); f(0, *a, &g) }
t("&local, an item after the splat") { |i| a = [1, 2]; g = Grow.new(a); f(*a, 9, &g) }
t("&local shrinks it") { |i| a = [1, 2, 3]; g = Shrink.new(a); f(*a, &g) }
t("&@ivar, fixed-arity callee: grown list too long") { |i| a = [1, 2]; @g = Grow.new(a); (two(*a, &@g) rescue [$!.class, $!.message]) }
t("&$gvar, fixed-arity callee: shrunk list too short") { |i| a = [1, 2]; $g = Shrink.new(a); (two(*a, &$g) rescue [$!.class, $!.message]) }
GROW = []; class << GROW; def to_proc = (self << 3; proc { :c }); end
t("&CONST that is the splatted list") { |i| GROW.clear.push(1, 2); f(*GROW, &GROW) }
t("&(expr): a call copies; to_proc grows it unseen") { |i| a = [1, 2]; [f(*a, &Grow.new(a)), a] }
t("to_proc grows it, with a leading argument") { |i| a = [1, 2]; f(0, *a, &Grow.new(a)) }
t("to_proc grows it, an item after the splat") { |i| a = [1, 2]; f(*a, 9, &Grow.new(a)) }
t("to_proc shrinks it") { |i| a = [1, 2, 3]; f(*a, &Shrink.new(a)) }
t("fixed-arity callee: grown list is too long") { |i| a = [1, 2]; (two(*a, &Grow.new(a)) rescue [$!.class, $!.message]) }
t("fixed-arity callee: shrunk list is too short") { |i| a = [1, 2]; (two(*a, &Shrink.new(a)) rescue [$!.class, $!.message]) }
t("a Proc block argument, no to_proc") { |i| a = [1, 2]; pr = proc { :pr }; [f(*a, &pr), a] }
t("&nil") { |i| a = [1, 2]; f(*a, &nil) }
t("&:sym") { |i| a = ["x", "y"]; f(*a, &:upcase) }
t("&method(:m)") { |i| a = [1, 2]; f(*a, &method(:keeps)) }

# ---------------------------------------------------------------- 2. what the callee gets
t("callee's rest is a copy: mutating it leaves the list") { |i| a = [1, 2]; pr = proc { }; [keeps(*a, &pr), a] }
t("length that does not match") { |i| a = [1, 2, 3]; (two(*a, &nil) rescue [$!.class, $!.message]) }
t("list changing length between calls") { |i| a = (i.even? ? [1, 2] : [1]); (two(*a, &nil) rescue [$!.class, $!.message]) }
t("non-Array splat: to_a") { |i| (two(*ToA.new([]), &nil)) }
t("nil splat") { |i| (f(*nil, &nil)) }
t("optional-parameter callee") { |i| a = (i.even? ? [1] : [1, 2]); opt(*a, &nil) }
t("keyword callee") { |i| a = [1, 2]; kw(*a, &nil) }
t("block callee, block given") { |i| a = [1, 2]; pr = proc { :given }; blk(*a, &pr) }
t("ruby2_keywords hash as last element") do |i|
  h = Hash.ruby2_keywords_hash({k: :r2k})
  a = [1, 2, h]
  kw(*a, &nil)
end
t("frozen list") { |i| a = [1, 2].freeze; two(*a, &nil) }

# ---------------------------------------------------------------- 3. forwarders
def fwd_rest_blk(*a, &b) = f(*a, &b)
def fwd_dots(...) = f(...)
def fwd_lead(x, ...) = f(x, ...)
def fwd_anon(*, &) = f(*, &)
def fwd_two(...) = fwd_dots(...)
t("(*a, &b) forward, with and without a block") { |i| i.even? ? fwd_rest_blk(1, 2) { :b } : fwd_rest_blk(1, 2) }
t("(...) forward") { |i| i.even? ? fwd_dots(1, 2) { :b } : fwd_dots(1, 2) }
t("(x, ...) forward") { |i| fwd_lead(1, 2) }
t("(*, &) forward") { |i| fwd_anon(1, 2) { :anon } }
t("two layers of ...") { |i| fwd_two(1, 2) { :two } }

class Redef; def m(a, b) = :first; def call(a) = m(*a, &nil); end
t("callee redefined after the site bound") do |i|
  o = Redef.new
  Redef.class_eval { def m(a, b) = :second } if i == 1500
  o.call([1, 2])
end
class MM; def method_missing(n, *a, &b) = [n, a, b && b.call]; def respond_to_missing?(*) = true; def go(a) = ghost(*a, &proc { :mm }); end
t("method_missing callee") { |i| MM.new.go([1, 2]) }
t("break through a passed block") { |i| a = [1, 2]; [1].each { |_| blk(*a, &proc { break :broke }) } }
class Sup; def m(a, b, &k) = [a, b, k && k.call]; end
class Sub < Sup; def m(*a, &k) = super(*a, &k); end
t("super(*a, &k)") { |i| Sub.new.m(1, 2) { :sup } }
