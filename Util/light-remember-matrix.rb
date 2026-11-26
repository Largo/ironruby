# Differential matrix for a light frame's call whose light site answers RubyOps.LightScopeRequired (the frame then makes
# its scope and calls again through the ordinary site; LightFrameRewriter.MakeLightCall) - and for a site that would
# remember that answer and skip the light site from then on (IR_LIGHT_REMEMBER, tried in perf/lsite and rejected; see
# Util/perf/LEDGER.md gap 16). Run it under CRuby and under ./ir.sh in every mode, and diff:
#
#   ruby Util/light-remember-matrix.rb > mri.txt
#   ./ir.sh Util/light-remember-matrix.rb > on.txt
#   IR_LIGHT_REGEX=all ./ir.sh Util/light-remember-matrix.rb > all.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/light-remember-matrix.rb > off.txt
#   diff mri.txt on.txt && diff mri.txt all.txt && diff mri.txt off.txt
#
# The probes: a site that needed the scope once and then gets a receiver whose method does not, and the reverse;
# the callee redefined half way and put back; a refinement; method_missing; a protected method; super into a method
# that reads the caller's scope; block_given? in a block's frame; binding; and two threads sharing one site. Each
# reports the call's value and the caller's $~ after it, which no callee may change.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/light-remember-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

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

# the caller's own match data after the call, which the call must leave alone
def kept
  "keep" =~ /(ke)ep/
  r = yield
  [r, $~ && $~[0], $1]
end

# A receiver whose #sub, #gsub, #split, #=~ and #prot need no caller's scope.
class Plain
  def sub(*a) = [:plain_sub, a.size]
  def gsub(*a) = [:plain_gsub, a.size]
  def split(*a) = [:plain_split, a.size]
  def =~(o) = :plain_match
  def prot = :plain_prot
end

# ---------------------------------------------------------------- 1. one site, receivers that need the scope and not

# String#sub/gsub/split/=~ store the caller's $~ (the light site answers LightScopeRequired); Plain's do not.
def l_sub(o) = o.sub("a", "x")
def l_gsub(o, r) = o.gsub(r, "x")
def l_split(o, r) = o.split(r)
def l_eqt(o, r) = o =~ r
def l_pass(o) = o.size

t("sub: String first, then Plain") { |i| kept { l_sub(i < N / 2 ? "abc" : Plain.new) } }
t("sub: Plain first, then String") { |i| kept { l_sub(i < N / 2 ? Plain.new : "abc") } }
t("sub: alternating") { |i| kept { l_sub(i.even? ? "abc" : Plain.new) } }
t("gsub with a Regexp: String, then Plain, then String") { |i| kept { l_gsub(i % 1000 < 500 ? "aba" : Plain.new, /a/) } }
t("gsub with a String pattern: alternating") { |i| kept { l_gsub(i.odd? ? "aba" : Plain.new, "a") } }
t("split: String, then Plain") { |i| kept { l_split(i < 10 ? "a,b" : Plain.new, /,/) } }
t("=~: Plain, then String, then nil") { |i| kept { l_eqt(i < 1000 ? Plain.new : (i < 2000 ? "xxa" : nil), /a/) } }
t("a site that never needs the scope") { |i| kept { l_pass(i.even? ? "abc" : [1, 2]) } }

# ---------------------------------------------------------------- 2. the callee redefined half way, and put back

class String
  alias_method :__lr_orig_tr, :tr
end
def l_tr(s) = s.tr("a", "b")
t("String#tr, redefined at 1000 and put back at 2000") do |i|
  if i == 1000
    String.class_eval { remove_method :tr; def tr(*) = :redefined_tr }
  elsif i == 2000
    String.class_eval { remove_method :tr; alias_method :tr, :__lr_orig_tr }
  end
  kept { l_tr("aaa") }
end

def l_sub2(s) = s.sub(/a/, "z")
t("String#sub replaced by a Ruby method at 1000, back at 2000") do |i|
  if i == 1000
    String.class_eval { alias_method :__lr_orig_sub, :sub; remove_method :sub; def sub(*a) = [:ruby_sub, a.size] }
  elsif i == 2000
    String.class_eval { remove_method :sub; alias_method :sub, :__lr_orig_sub }
  end
  kept { l_sub2("bab") }
end

# ---------------------------------------------------------------- 3. method_missing, protected, private

class Ghost
  def method_missing(name, *a) = name == :sub ? [:ghost, a.size] : super
  def respond_to_missing?(name, p = false) = name == :sub || super
end
t("sub: method_missing, then String, then method_missing") { |i| kept { l_sub(i % 1500 < 700 ? Ghost.new : "abc") } }

class Prot
  def call_prot(o) = o.prot
  protected def prot = :protected_prot
end
t("protected method of the same class, then a public one") { |i| Prot.new.call_prot(i < N / 2 ? Prot.new : Plain.new) }
t("protected method, alternating with a public one") { |i| Prot.new.call_prot(i.even? ? Plain.new : Prot.new) }

class Priv
  def call_priv(o) = o.secret
  private def secret = :secret
end
t("private method with an explicit receiver, then public") do |i|
  begin
    Priv.new.call_priv(i < 10 ? Priv.new : Plain.new.tap { |p| def p.secret = :public_secret })
  rescue NoMethodError => e
    e.class
  end
end

# ---------------------------------------------------------------- 4. super into a method that reads the caller's scope

class SubString < String
  def sub(*a) = super
  def gsub(*a, &b) = super
end
t("super into String#sub: then a plain receiver") { |i| kept { l_sub(i < N / 2 ? SubString.new("abc") : Plain.new) } }
t("super into String#gsub with a block") { |i| kept { SubString.new("aba").gsub(/a/) { |m| [m, $&].inspect } } }

# ---------------------------------------------------------------- 5. block_given?, binding, eval from the same site

def l_bg_block(&b) = [1].map { |_| block_given? }.first
t("block_given? in a block of the method") { |i| i.even? ? l_bg_block : l_bg_block { } }

def l_bnd(o, x) = o.respond_to?(:sub) ? [o.sub("a", "q"), binding.local_variable_get(:x)] : x
t("sub then binding in the same frame") { |i| kept { l_bnd(i.even? ? "abc" : Plain.new, i % 3) } }

# ---------------------------------------------------------------- 6. two threads on one site

def l_thread_sub(o) = o.sub(/b/, "B")
# each thread in a method of its own, whose $~ is its own
def l_thread_worker(k)
  bad = 0
  2000.times do |j|
    "mine" =~ /(mi)ne/
    o = (j + k).even? ? "abc" : Plain.new
    r = l_thread_sub(o)
    bad += 1 unless r == (o.is_a?(String) ? "aBc" : [:plain_sub, 2]) && $1 == "mi"
  end
  bad
end
t("two threads, one site, mixed receivers") do |i|
  next nil unless i % 300 == 0
  2.times.map { |k| Thread.new { l_thread_worker(k) } }.map(&:value)
end

# ---------------------------------------------------------------- 7. a refinement (in force for the rest of the file)

module RefineSub
  refine Plain do
    def sub(*a) = :refined_plain_sub
  end
end

def l_sub_unrefined(o) = o.sub("a", "w")
using RefineSub
def l_sub_refined(o) = o.sub("a", "w")
t("refined Plain#sub, then String") { |i| kept { l_sub_refined(i < N / 2 ? Plain.new : "abc") } }
t("String, then refined Plain#sub") { |i| kept { l_sub_refined(i < N / 2 ? "abc" : Plain.new) } }
t("method defined before using: Plain, String") { |i| kept { l_sub_unrefined(i.even? ? Plain.new : "abc") } }
