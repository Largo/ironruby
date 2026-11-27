# Differential matrix for `recv.m op= v' (a MemberAssignmentExpression: `o.x += 1', `o.x ||= v', `self.x &&= v',
# `o&.x += 1') in a method that runs without a scope (a light frame, MethodDefinition.IsScopeFree,
# IR_LIGHT_MEMBER_OPASSIGN): the read of m, the operator and the write of m= are three call sites with a light scope,
# as `o.x = v' and `@x += v' are. Run it under CRuby and under ./ir.sh with the switch on and off, and diff:
#
#   ruby Util/light-member-opassign-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/light-member-opassign-matrix.rb > /tmp/on.txt
#   IR_LIGHT_MEMBER_OPASSIGN=0 ./ir.sh Util/light-member-opassign-matrix.rb > /tmp/off.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/light-member-opassign-matrix.rb > /tmp/noframes.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt && diff /tmp/mri.txt /tmp/noframes.txt
#
# Every probe runs N times and reports each distinct answer once, in the order they first appear, so the
# interpreted body, the compiled one and a site whose rule is already bound all meet the case.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/light-member-opassign-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

N = 3000

def t(label)
  print label.ljust(58), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 60]}"
  end
end

class Box
  attr_accessor :x, :list
  def initialize(x = 0) = (@x = x; @list = [])
end

# ---------------------------------------------------------------- 1. the operators

def plus(o) = (o.x += 1)
def minus(o, v) = (o.x -= v)
def ored(o, v) = (o.x ||= v)
def anded(o, v) = (o.x &&= v)
def shifted(o, v) = (o.list << v; o.list.size)
def append_op(o, v) = (o.list += [v]; o.list.size)
def safe(o) = (o&.x += 1)
def safe_or(o, v) = (o&.x ||= v)


t("o.x += 1") { |i| plus(Box.new(i % 3)) }
t("o.x -= v") { |i| minus(Box.new(10), i % 3) }
t("o.x ||= v on nil and on a value") { |i| ored(Box.new(i.odd? ? nil : :set), :v) }
t("o.x &&= v on nil and on a value") { |i| anded(Box.new(i.odd? ? nil : :set), :v) }
t("o.list << v, o.list += [v]") { |i| bx = Box.new; [shifted(bx, i), append_op(bx, i)] }
t("o&.x += 1, on nil and on an object") { |i| safe(i.odd? ? nil : Box.new(i % 3)) }
t("o&.x ||= v, on nil and on an object") { |i| safe_or(i.odd? ? nil : Box.new(nil), :w) }

# ---------------------------------------------------------------- 2. the value of the expression

class Odd
  def x = 5
  def x=(v)
    @stored = v
    :ignored
  end
  attr_reader :stored
end
def value_of(o) = (o.x += 2)
t("the value is the rhs, not what x= returns") { |i| o = Odd.new; [value_of(o), o.stored] }

# ---------------------------------------------------------------- 3. private setter on self, and on another object

class Priv
  def initialize = (@x = 1)
  def bump = (self.x += 1)
  def bump_other(o) = (o.x += 1)
  private
  attr_accessor :x
end
t("self.x += 1 with a private accessor") { |i| Priv.new.bump }
t("o.x += 1 with a private accessor") { |i| Priv.new.bump_other(Priv.new) }

# ---------------------------------------------------------------- 4. who is asked, redefined after the site bound

class Re
  attr_accessor :x
end
def re_plus(o) = (o.x += 1)
t("x= redefined and removed after binding") do |i|
  o = Re.new; o.x = 0
  Re.class_eval { define_method(:x=) { |val| @x = val * 10 } } if i == 1000
  Re.class_eval { remove_method :x=; attr_writer :x } if i == 2000
  [re_plus(o), o.x]
end
t("x on a singleton class") do |i|
  o = Re.new; o.x = 1
  def o.x = 100 if i % 3 == 0
  re_plus(o)
end

class MM
  def method_missing(name, *args) = name.to_s.end_with?("=") ? (@last = [name, *args]) : 7
  def respond_to_missing?(*) = true
  attr_reader :last
end
t("method_missing for x and x=") { |i| o = MM.new; [re_plus(o), o.last] }

class BO < BasicObject
  def x = 1
  def x=(v)
    @v = v
  end
  def v = @v
end
t("a BasicObject receiver") { |i| o = BO.new; [re_plus(o), o.v] }

module RefX
  refine(Re) { def x = 1000 }
end
class UsingRef
  using RefX
  def self.plus(o) = (o.x += 1)
end
t("a refinement of x in force") { |i| o = Re.new; o.x = 0; UsingRef.plus(o) }
t("the same object outside it") { |i| o = Re.new; o.x = 0; re_plus(o) }

t("errors: no x=, nil receiver, frozen") do |i|
  r = []
  r << (begin; re_plus(Object.new); rescue NoMethodError => e; e.message; end)
  r << (begin; re_plus(nil); rescue NoMethodError => e; e.message; end)
  r << (begin; o = Re.new; o.x = 1; o.freeze; re_plus(o); rescue => e; e.class; end)
  r
end

# ---------------------------------------------------------------- 5. what the frame still does afterwards

def with_binding(o) = (o.x += 1; _v = 5; binding.local_variable_get(:_v) + o.x)
def with_locals(o) = (o.x += 1; _w = 2; local_variables)
def with_eval(o) = (o.x += 1; eval("o.x * 2"))
def with_block(o) = (o.x += 1; [1, 2].map { |k| o.x += k; o.x })
def with_raise(o)
  o.x += 1
  raise ArgumentError, "boom"
end
def recursive(o, n) = n.zero? ? o.x : (o.x += 1; recursive(o, n - 1))
def with_method_name(o) = (o.x += 1; __method__)
def with_block_given(o) = (o.x += 1; block_given?)

t("binding after") { |i| with_binding(Box.new(i % 2)) }
t("local_variables after") { |i| with_locals(Box.new) }
t("eval after") { |i| with_eval(Box.new(i % 3)) }
t("a block that op-assigns too") { |i| with_block(Box.new) }
t("raise: the label in the backtrace") { |i| begin; with_raise(Box.new); rescue => e; e.backtrace_locations[0].label; end }
t("recursion") { |i| recursive(Box.new, 5) }
t("__method__ and block_given?") { |i| [with_method_name(Box.new), with_block_given(Box.new)] }

# ---------------------------------------------------------------- 6. other ways in

class Via
  attr_accessor :x
  def initialize = (@x = 0)
  def inc = (self.x += 1)
  define_method(:dinc) { self.x += 1 }
end
v = Via.new
t("send, public_send, Method#call, define_method") { |i| [v.send(:inc), v.public_send(:inc), v.method(:inc).call, v.dinc].size }
t("value after the above") { |i| v.x > 0 }

# ---------------------------------------------------------------- 7. tracing and threads

def traced(o) = (o.x += 1)
calls = []
# (only the method's own :call: IronRuby's TracePoint does not report attr_accessor's x and x= as :c_call, on the base too)
tp = TracePoint.new(:call, :c_call) { |tr| calls << [tr.event, tr.method_id] if tr.method_id == :traced }
tp.enable { traced(Box.new) }
t("TracePoint :call and :c_call saw") { |i| calls.uniq }
t("after the TracePoint is disabled") { |i| traced(Box.new(i % 2)) }

ths = 4.times.map { Thread.new { 5000.times { plus(Box.new) }; :done } }
t("threads") { |i| ths.map(&:value) }

puts "end"
