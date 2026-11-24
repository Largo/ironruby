# Differential matrix for Forwardable's delegators (and the `...' and `defined?(obj.m)' they are made of): the output must
# be CRuby 4.0's, with the method JIT and light frames on, under -X:NoJIT -X:NoOSR, and under IR_LIGHT_FRAMES=0.
#   ruby Util/forwardable-matrix.rb > cruby.txt; ./ir.sh Util/forwardable-matrix.rb | diff cruby.txt -
require 'forwardable'

# Forwardable's warning about a private target, counted, with the paths left out (they name each one's forwardable.rb)
WARNINGS = Hash.new(0)
def Warning.warn(message, **) = WARNINGS[message.gsub(/\S*\/([\w-]+\.rb):\d+/, '\\1').chomp] += 1

def show(label)
  r = begin
    yield.inspect
  rescue Exception => e
    "#{e.class}: #{e.message.sub(/ for an instance of .*\z/, '')}"
  end
  puts "#{label}: #{r}"
end

class Target
  def initialize; @log = []; end
  attr_reader :log
  def plain = :plain
  def args(a, b = 2, *r, k: 0, **o, &blk) = [a, b, r, k, o, blk && blk.call(a)]
  def kw(k:, j: 1) = [k, j]
  def opt_hash(h = nil) = h
  def raises = raise(ArgumentError, "boom")
  def yielder = yield(1, 2)
  def method_missing(name, *a, **k, &b) = name.to_s.start_with?("mm_") ? [name, a, k, b && b.call] : super
  def respond_to_missing?(name, p = false) = name.to_s.start_with?("mm_") || super
  def protected_one = :prot
  protected :protected_one
  private def private_one = :priv
  def redef = 1
end

class Holder
  extend Forwardable
  CONST = [3, 1, 2]
  def initialize; @t = Target.new; @a = [1, 2, 3]; @h = { a: 1 }; end
  attr_reader :t
  def_delegators :@t, :plain, :args, :kw, :opt_hash, :raises, :yielder, :mm_x, :protected_one, :redef, :log
  def_delegator :@t, :private_one
  def_delegator :@a, :size
  def_delegator :@a, :each
  def_delegator :@a, :<<, :push_a
  def_delegator :@a, :[], :at
  def_delegator :@h, :fetch
  def_delegator :t, :plain, :via_method
  def_delegator :'Holder::CONST', :sort, :const_sort
  def_delegator :@missing, :size, :nil_size
  private def secret = Target.new
  def_delegator :secret, :plain, :via_private
end
h = Holder.new
show("plain") { h.plain }
show("args1") { h.args(1) }
show("args2") { h.args(1, 5, 6, 7, k: 3, z: 4) { |x| x * 10 } }
show("args_hash_pos") { h.args({ k: 9 }) }
show("kw") { h.kw(k: 1) }
show("kw_splat") { o = { k: 2, j: 3 }; h.kw(**o) }
show("kw_missing") { h.kw }
show("kw_bad") { h.kw(k: 1, q: 2) }
show("opt_hash") { h.opt_hash(a: 1) }
show("opt_hash_empty") { h.opt_hash(**{}) }
show("splat") { a = [1, 2, 3]; h.args(*a) }
show("arity") { h.method(:args).arity }
show("params") { h.method(:args).parameters }
show("yielder") { h.yielder { |a, b| a + b } }
show("yielder_noblock") { h.yielder }
show("each") { s = 0; h.each { |x| s += x }; s }
show("each_enum") { h.each.to_a }
show("push") { h.push_a(4); h.size }
show("at") { h.at(1) }
show("fetch") { h.fetch(:a) }
show("fetch_default") { h.fetch(:b, 7) }
show("fetch_block") { h.fetch(:b) { |k| k.to_s } }
show("fetch_missing") { h.fetch(:zz) }
show("via_method") { h.via_method }
show("via_private") { h.via_private }
show("const") { h.const_sort }
show("nil_size") { h.nil_size }
show("mm") { h.mm_x(1, k: 2) { 3 } }
show("protected") { h.protected_one }
show("private") { h.private_one }
show("wrong_arity") { h.plain(1) }
show("raises") { h.raises }
show("backtrace") do
  begin
    h.raises
  rescue => e
    e.backtrace.grep_v(/forwardable-matrix/).map { |l| l.sub(/:in .*/, "").sub(/.*\//, "") }.first(1) +
      [e.backtrace.grep(/forwardable-matrix/).first.to_s.sub(/.*:(\d+):in.*/, '\1')]
  end
end
show("hot") do
  s = 0
  20_000.times { |i| s += h.size + h.at(i % 3) + h.fetch(:a) + h.via_method.size + (h.kw(k: i)[0] & 1) }
  s
end
show("hot_block") { s = 0; 5000.times { h.each { |x| s += x } }; s }
show("hot_redef") do
  s = 0
  3000.times do |i|
    if i == 1500
      Target.class_eval { def redef = 2 }
    end
    s += h.redef
  end
  s
end
show("hot_private_switch") do
  s = 0
  3000.times do |i|
    Target.send(:private, :redef) if i == 1000
    Target.send(:public, :redef) if i == 2000
    s += h.redef
  end
  s
end
show("hot_mm") { r = nil; 3000.times { |i| r = h.mm_x(i) }; r }

# the same shapes written by hand
class Hand
  def initialize; @t = Target.new; end
  def fwd(...) = @t.args(...)
  def lead(a, ...) = @t.args(a, ...)
  def guard(...)
    _ = @t
    if defined?(_.protected_one) then :defined else :undefined end
  end
  def guard_pub(...)
    _ = @t
    defined?(_.plain)
  end
  def guard_mm = (_ = @t; defined?(_.mm_y))
  def guard_none = (_ = @t; defined?(_.nope))
  def guard_raise = defined?(nil.foo.bar)
  def guard_self = (_ = self; defined?(_.guard_none))
  def guard_hidden = (_ = self; defined?(_.hidden))
  private def hidden = 1
  protected def prot = 1
  def guard_prot(o) = defined?(o.prot)
end
x = Hand.new
show("dots") { x.fwd(1, 2, 3, k: 4, q: 5) { |a| a } }
show("dots_hash") { x.fwd({ k: 1 }) }
show("dots_lead") { x.lead(1, k: 2) }
show("dots_none") { x.fwd }
show("guard") { x.guard }
show("guard_pub") { x.guard_pub }
show("guard_mm") { x.guard_mm }
show("guard_none") { x.guard_none }
show("guard_raise") { x.guard_raise }
show("guard_self") { x.guard_self }
show("guard_hidden") { x.guard_hidden }
show("guard_prot") { x.guard_prot(Hand.new) }
show("guard_hot") { r = []; 3000.times { |i| r = [x.guard, x.guard_pub, x.guard_mm, x.guard_none, x.guard_prot(x)] }; r }
WARNINGS.each { |m, n| puts "warning x#{n}: #{m}" }
