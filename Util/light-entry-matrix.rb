# Differential matrix for what a method's entry takes from its caller: the name it was called by through an alias
# (__callee__; RubyOps.MarkAliasCall and TakePendingCallee) and the scope a TracePoint for :call makes at a light
# frame's entry (LightFrame.Enter) - what IR_LIGHT_ENTRY changes. Run it under CRuby and under ./ir.sh in every mode:
#
#   ruby Util/light-entry-matrix.rb > mri.txt
#   ./ir.sh Util/light-entry-matrix.rb > on.txt
#   IR_LIGHT_ENTRY=0 ./ir.sh Util/light-entry-matrix.rb > off.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/light-entry-matrix.rb > scoped.txt
#   diff mri.txt on.txt && diff mri.txt off.txt && diff mri.txt scoped.txt
#
# A name is pending from the alias call until the next method is entered on that thread: the probes call through
# aliases light methods and scoped ones, by send, Method#call and define_method, from many threads at once, with an
# alias call that raises before its method is entered followed by an ordinary call, an alias call whose argument
# calls another method through an alias, recursion through an alias, and a million-deep recursion through a light frame.

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

class C
  # light: no scope needed
  def light_name = __callee__
  def light_meth = __method__
  def light_arg(x) = [__callee__, x]
  def plain = :plain
  # scoped: a block literal that reads a local
  def scoped_name
    y = 1
    [1].map { |_| y }
    __callee__
  end
  alias light_alias light_name
  alias_method :light_alias2, :light_name
  alias light_meth_alias light_meth
  alias light_arg_alias light_arg
  alias scoped_alias scoped_name

  def two(a, b) = [__callee__, a, b]
  alias two_alias two

  def method_missing(name, *a)
    name == :ghost ? [:mm, __callee__, a] : super
  end
  def respond_to_missing?(name, p = false) = name == :ghost || super
  alias mm_alias method_missing

  define_method(:dm_name) { __callee__ }
  alias dm_alias dm_name
end

o = C.new

# ---------------------------------------------------------------- 1. the name by alias, light and scoped
t("light by its name") { o.light_name }
t("light by alias") { o.light_alias }
t("light by alias_method") { o.light_alias2 }
t("__method__ by alias") { o.light_meth_alias }
t("light by alias with an argument") { |i| o.light_arg_alias(i % 2) }
t("scoped by its name") { o.scoped_name }
t("scoped by alias") { o.scoped_alias }
t("alternating alias and name") { |i| i.even? ? o.light_alias : o.light_name }
t("define_method body by alias") { o.dm_alias }

# ---------------------------------------------------------------- 2. other ways in
t("send to the alias") { o.send(:light_alias) }
t("public_send to the alias") { o.public_send(:light_alias) }
t("Method#call of the alias") { o.method(:light_alias).call }
t("UnboundMethod#bind_call of the alias") { C.instance_method(:light_alias).bind_call(o) }
t("method_missing by alias") { o.mm_alias(:ghost, 1) }

# ---------------------------------------------------------------- 3. a name that is never taken by its method
t("alias call that raises (arity), then by name") do
  r = begin; o.two_alias(1); rescue ArgumentError => e; e.class; end
  [r, o.light_name, o.plain, o.light_alias]
end
t("alias call whose argument calls another alias") { o.light_arg_alias(o.light_alias) }
t("alias whose arguments are both alias calls") { o.two_alias(o.light_alias2, o.scoped_alias) }

# ---------------------------------------------------------------- 4. recursion, threads, tracing
class C
  def countdown(n) = n == 0 ? __callee__ : down(n - 1)
  alias down countdown
end
t("recursion through an alias") { |i| o.countdown(i % 5) }

def thread_names(o, k)
  bad = 0
  3000.times do |j|
    if (j + k).even?
      bad += 1 unless o.light_alias == :light_alias && o.light_name == :light_name
    else
      bad += 1 unless o.scoped_alias == :scoped_alias && o.plain == :plain
    end
  end
  bad
end
t("four threads calling aliases and names") do |i|
  next nil unless i % 500 == 0
  4.times.map { |k| Thread.new { thread_names(o, k) } }.map(&:value)
end

def traced_calls(o)
  seen = []
  tp = TracePoint.new(:call) { |tp| seen << [tp.method_id, tp.callee_id] if tp.defined_class == C }
  tp.enable { o.light_alias; o.light_name; o.light_arg(1) }
  seen
end
t("TracePoint :call method_id and callee_id") { traced_calls(o) }
t("after the TracePoint, by alias") { o.light_alias }

# ---------------------------------------------------------------- 5. deep recursion through a light frame
def deep_light(n) = n == 0 ? 0 : deep_light(n - 1)
def deep_alias_light(n) = n == 0 ? 0 : deep_alias2(n - 1)
alias deep_alias2 deep_alias_light
puts "recursion a million deep: " + (begin; deep_light(1_000_000).to_s; rescue SystemStackError; "SystemStackError"; end)
puts "recursion a million deep by alias: " + (begin; deep_alias_light(1_000_000).to_s; rescue SystemStackError; "SystemStackError"; end)
puts "and after: #{o.light_alias.inspect} #{o.light_name.inspect}"
