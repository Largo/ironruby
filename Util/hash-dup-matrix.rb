# Expression matrix for Hash#dup (perf/struct2's fast path, IR_FAST_DUP): what the copy holds -
# entries, order, default value and proc, compare_by_identity, instance variables, frozenness,
# singleton methods - and that initialize_dup and initialize_copy are still called when they are
# not the built-ins: redefined in Hash, in Object or Kernel, through a prepended or included
# module, on a subclass, traced by TracePoint, and restored again.
#
#   ruby   Util/hash-dup-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/hash-dup-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# Every line is "<source> => <result>" or "<source> !! <ExceptionClass>: <message>". Each runs
# cold, again and then 200 times, and a line says so when a later answer differs from the first.
# The rows that redefine a hook (ONCE) come last, run once each, and undo what they did, in order.

class SubHash < Hash; end
class InitHash < Hash
  def initialize_copy(o) = (super; self[:copied] = true; self)
end
module LogCopy
  def initialize_copy(o) = (super; self[:via] = :prepended; self)
end
module IncCopy
  def initialize_copy(o) = (super; self[:via] = :included; self)
end
module RefineDup
  refine Hash do
    def initialize_copy(o) = (super; self[:via] = :refined; self)
  end
end
module UsingRefineDup
  using RefineDup
  def self.dup_it = {a: 1}.dup
end
H = {a: 1, b: [2], "c" => 3}

EXPRESSIONS = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  H.dup
  H.dup == H
  H.dup.equal?(H)
  H.dup[:b].equal?(H[:b])
  (d = H.dup; d[:z] = 1; [H.key?(:z), d.size])
  {}.dup
  (h = {a: 1, b: 2, c: 3}; h.delete(:a); h.delete(:b); h[:d] = 4; d = h.dup; [d, d.keys])
  (h = Hash.new(5); h[:a] = 1; d = h.dup; [d[:x], d.default])
  (h = Hash.new { |hh, k| k.to_s * 2 }; d = h.dup; [d[:ab], d.default_proc.equal?(h.default_proc)])
  (h = {}.compare_by_identity; k = "k"; h[k] = 1; d = h.dup; [d.compare_by_identity?, d["k"], d[k]])
  (h = {a: 1}; h.instance_variable_set(:@x, 9); d = h.dup; [d.instance_variable_get(:@x), d.instance_variables])
  (h = {a: 1}.freeze; d = h.dup; [d.frozen?, d])
  (h = {a: 1}; def h.extra = 1; d = h.dup; [d.respond_to?(:extra), d])
  (h = {a: 1}; h.singleton_class.define_method(:initialize_copy) { |o| super(o); self[:sing] = 1 }; h.dup)
  SubHash[a: 1].dup.class
  SubHash[a: 1].dup
  InitHash[a: 1].dup
  (h = {a: 1}; h.each { h.dup }; h)
  (h = {a: 1}; r = nil; h.each { r = h.dup; r[:new] = 1 }; r)
  (h = {"s" => 1}; d = h.dup; d["s"] += 1; [h, d])
  Thread.new { 100.times.map { H.dup.size }.uniq }.value
  (ks = (1..20).to_a; h = ks.to_h { [_1, _1 * 2] }; d = h.dup; [d.size, d[20], d.keys == ks])
  UsingRefineDup.dup_it
  (calls = []; tp = TracePoint.new(:c_call) { |t| calls << t.method_id if t.method_id.to_s.start_with?("initialize") }; tp.enable { {a: 1}.dup }; calls)
RUBY

# These change the hooks for good and undo it, in order: each runs once, its dup warm (200 times).
ONCE = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  (r = nil; 200.times { r = {a: 1}.dup }; r)
  (class Hash; alias_method :__saved_copy__, :initialize_copy; def initialize_copy(o) = (__saved_copy__(o); self[:mine] = 1; self); end; (r = nil; 200.times { r = {a: 1}.dup }; r))
  (class Hash; alias_method :initialize_copy, :__saved_copy__; remove_method :__saved_copy__; end; (r = nil; 200.times { r = {a: 1}.dup }; r))
  (class Object; def initialize_dup(o) = (super; self[:obj] = 1 if is_a?(Hash); self); end; (r = nil; 200.times { r = {a: 1}.dup }; r))
  (class Object; remove_method :initialize_dup; end; (r = nil; 200.times { r = {a: 1}.dup }; r))
  (module Kernel; alias_method :__saved_dup__, :initialize_dup; def initialize_dup(o) = (__saved_dup__(o); self[:kernel] = 1 if is_a?(Hash); self); end; (r = nil; 200.times { r = {a: 1}.dup }; r))
  (module Kernel; alias_method :initialize_dup, :__saved_dup__; remove_method :__saved_dup__; end; (r = nil; 200.times { r = {a: 1}.dup }; r))
  (Hash.prepend(LogCopy); (r = nil; 200.times { r = {a: 1}.dup }; r))
  (LogCopy.send(:remove_method, :initialize_copy); (r = nil; 200.times { r = {a: 1}.dup }; r))
  (Hash.include(IncCopy); (r = nil; 200.times { r = {a: 1}.dup }; r))
  ({a: 1}.clone)
  ({a: 1}.freeze.clone.frozen?)
RUBY

def clean(v) = v.inspect.gsub(/0x\h+/, "XX")
def run(source)
  "=> #{clean(eval(source))}"
rescue Exception => e
  "!! #{e.class}: #{e.message.gsub(/0x\h+/, "XX")}"
end

EXPRESSIONS.each do |source|
  first = run(source)
  second = run(source)
  warm = nil
  200.times { warm = run(source) }
  line = "#{source} #{first}"
  line += "   [second: #{second}]" if second != first
  line += "   [warm: #{warm}]" if warm != first
  puts line
end
ONCE.each { |source| puts "#{source} #{run(source)}" }
puts "done"
