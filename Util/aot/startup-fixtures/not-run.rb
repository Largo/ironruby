# Compiled into a library of its own by check-startup.sh: what README part A lists as not run in compiled code, which
# must print the same compiled as from source (startup-fixtures/main.rb prints it).
module StartupFixture
  def self.evals
    x = 41
    [eval("x + 1"), binding.local_variable_get(:x), binding.eval("x * 2"), [1].map { |z| binding }.first.local_variables.sort]
  end

  def self.binding_set
    w = 1
    binding.local_variable_set(:w, 9)
    w
  end

  class Defined
    define_method(:sum) { |a, b = 2| a + b }
    [:one, :two].each { |n| define_method(n) { n } }
  end

  def self.defined
    d = Defined.new
    [d.sum(1), d.sum(1, 5), d.one, d.two, Defined.instance_method(:sum).arity, Defined.instance_method(:sum).parameters]
  end

  def self.traced = 1 + 1

  def self.trace
    events = []
    tp = TracePoint.new(:call, :return, :line) { |t| events << [t.event, t.method_id, t.lineno] }
    tp.enable { traced }
    events
  end

  def self.string_evals
    o = Object.new
    o.instance_eval("@q = 3")
    [o.instance_variable_get(:@q), class_eval("1 + 2")]
  end

  # A noted call (const_set, autoload, require_relative, __dir__) evaluates its arguments in order: n is read before the
  # argument after it assigns it, in a method and in a block.
  def self.noted_argument_order(v)
    n = :X
    m = Module.new
    m.const_set(n, (n = :Y; v))
    [:P].each { |k| m.autoload(k, (k = :Q; "startup_fixture_nothing")) }
    [m.constants.sort, n, m.autoload?(:P), m.autoload?(:Q)]
  end

  def self.tolerant = [1, 2].map { |i| (yield i rescue :raised) }

  # A noted call that raises before it takes its note (an invalid constant name, an empty autoload path) leaves none
  # behind for an eval in the same scope: __dir__ there is the eval's (nil), not this file's.
  def self.failed_notes
    pr = proc { |i| i == 1 ? const_set(:lowercase, 1) : eval("__dir__") }
    (pr.(1) rescue nil)
    [pr.(2), tolerant { |i| i == 1 ? autoload(:Empty, "") : eval("__dir__") }]
  end

  # const_set and autoload record the location of the line that calls them, in a block as anywhere
  %i[A B].each_with_index { |name, i| const_set(name, i) }
  [:Later].each { |name| autoload(name, "startup_fixture_later") }
  C = 3
end
