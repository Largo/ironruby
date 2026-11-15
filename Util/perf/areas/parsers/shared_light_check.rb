# A call site in a method body (a light-scope site) meeting receivers of many classes: shared rules must
# still honour visibility, method_missing, redefinition and refinements. Diff against CRuby.
class Base; def name = "base"; private def secret = 1; protected def prot = 2; end
klasses = 40.times.map { |i| Class.new(Base) { define_method(:idx) { i } } }
klasses[7].class_eval { def name = "seven" }
klasses[9].class_eval { public :secret }
klasses[11].class_eval { def method_missing(n, *a) = "mm:#{n}" }
class Caller
  def call_name(o) = o.name
  def call_secret(o) = o.secret
  def call_prot(o) = o.prot
  def call_cls(o) = o.class.superclass
  def call_frozen(o) = o.frozen?
end
c = Caller.new
out = []
3.times do |round|
  klasses[20].class_eval { def name = "twenty" } if round == 2
  klasses.each_with_index do |k, i|
    o = k.new
    r = [c.call_name(o), c.call_cls(o), c.call_frozen(o)]
    r << (begin; c.call_secret(o); rescue NoMethodError => e; e.message[0, 30]; end)
    r << (begin; c.call_prot(o); rescue NoMethodError => e; e.message[0, 30]; end)
    out << [round, i, *r].inspect
  end
end
module R; refine Base do; def name = "refined"; end; end
using R
class Caller2; def call_name(o) = o.name; end
out << klasses.map { |k| Caller2.new.call_name(k.new) }.uniq.inspect
out << klasses.map { |k| c.call_name(k.new) }.uniq.inspect
puts out
