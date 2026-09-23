# Differential matrix for deep recursion: every shape below must raise SystemStackError -
# never kill the process - and the program must carry on afterwards, recursing again included.
# Run under CRuby and under ./ir.sh and diff stdout:
#
#   ruby    Util/stack-depth-matrix.rb > /tmp/cruby.txt 2> /tmp/cruby-depth.txt
#   ./ir.sh Util/stack-depth-matrix.rb > /tmp/ir.txt    2> /tmp/ir-depth.txt
#   diff -u /tmp/cruby.txt /tmp/ir.txt
#
# stdout has, for each shape, the class and message of what was raised and whether the
# statement after it ran. How deep each one got differs between implementations (and between
# a thread and the main thread), so that goes to stderr: compare it by eye, don't diff it.
#
# A shape that crashes the process ends the output early: the diff shows where.

$stdout.sync = true
$depth = 0

def depth_report(label)
  $stderr.puts "#{label.ljust(40)} depth #{$depth}"
end

# Runs the block, which is expected to recurse until the stack runs out.
def shape(label)
  $depth = 0
  result = begin
    v = yield
    "no error: #{v.inspect[0, 60]}"
  rescue SystemStackError => e
    "#{e.class}: #{e.message}"
  rescue Exception => e
    "other #{e.class}: #{e.message[0, 80]}"
  end
  next_ran = true
  puts "#{label.ljust(40)} => #{result}; next statement ran: #{next_ran}"
  depth_report(label)
end

# ---- methods ---------------------------------------------------------------

def plain(n)
  $depth = n
  plain(n + 1)
end
shape("plain method") { plain(0) }
shape("plain method, again") { plain(0) }

def two_args(a, b)
  $depth = a
  two_args(a + 1, b)
end
shape("two arguments") { two_args(0, :x) }

def hot(n) = n == 0 ? 0 : 1 + hot(n - 1)
hot(100) ; hot(100) ; 50.times { hot(20) }     # past the JIT's threshold
shape("JIT-hot method, shallow") { hot(5000) }
shape("JIT-hot method, too deep") { hot(10_000_000) }
shape("JIT-hot method, shallow again") { hot(5000) }

def hot_float(x) = x <= 0.0 ? 0.0 : 1.0 + hot_float(x - 1.0)
50.times { hot_float(20.0) }
shape("JIT-hot float method, too deep") { hot_float(1.0e9) }

def with_ensure(n)
  $depth = n
  with_ensure(n + 1)
ensure
  $ensured = true
end
$ensured = false
shape("method with ensure") { with_ensure(0) }
puts "ensure clauses ran: #{$ensured}"

def with_rescue(n)
  $depth = n
  with_rescue(n + 1)
rescue ArgumentError
  :never
end
shape("method with a non-matching rescue") { with_rescue(0) }

def kw(n, k: 1)
  $depth = n
  kw(n + 1, k: k)
end
shape("keyword arguments") { kw(0) }

def splat(*a)
  $depth = a[0]
  splat(a[0] + 1, *a[1..])
end
shape("splat arguments") { splat(0, 1, 2) }

# ---- blocks, procs, lambdas -----------------------------------------------

def yielder(n) = yield(n)
def via_block(n)
  $depth = n
  yielder(n) { |k| via_block(k + 1) }
end
shape("block (yield)") { via_block(0) }

pr = proc { |n| $depth = n; pr.call(n + 1) }
shape("proc") { pr.call(0) }

la = ->(n) { $depth = n; la.(n + 1) }
shape("lambda") { la.(0) }

class DM
  define_method(:dm) { |n| $depth = n; dm(n + 1) }
end
shape("define_method") { DM.new.dm(0) }

class MM
  def method_missing(name, n)
    return super unless name == :deep
    $depth = n
    deep(n + 1)
  end

  def respond_to_missing?(name, priv = false) = name == :deep || super
end
shape("method_missing") { MM.new.deep(0) }

def via_send(n)
  $depth = n
  send(:via_send, n + 1)
end
shape("send") { via_send(0) }

class PublicSend
  def deep(n)
    $depth = n
    public_send(:deep, n + 1)
  end
end
shape("public_send") { PublicSend.new.deep(0) }

def via_method_object(n)
  $depth = n
  method(:via_method_object).call(n + 1)
end
shape("Method#call") { via_method_object(0) }

def via_instance_eval(n)
  $depth = n
  instance_eval { via_instance_eval(n + 1) }
end
shape("instance_eval") { via_instance_eval(0) }

def via_each(n)
  $depth = n
  [n].each { |k| via_each(k + 1) }
end
shape("Array#each -> block -> each") { via_each(0) }

def via_map(n)
  $depth = n
  [n].map { |k| via_map(k + 1) }
end
shape("Array#map -> block -> map") { via_map(0) }

def via_times(n)
  $depth = n
  1.times { via_times(n + 1) }
end
shape("Integer#times -> block") { via_times(0) }

# ---- recursion through builtins -------------------------------------------

class SelfToS
  def to_s = "<#{self}>"          # string interpolation calls to_s
end
shape("to_s interpolating itself") { SelfToS.new.to_s }

# Not here: `def inspect = [self].inspect' and `def ==(o) = [self] == [o]'. The recursion runs
# through MRI's C stack (Array#inspect, Array#== -> the Ruby method -> Array#...), and MRI 4.0
# cannot rescue the SystemStackError that raises: it goes straight past `rescue
# SystemStackError' and ends the program.

class Cmp
  include Comparable
  def <=>(other) = (self == other) ? 0 : 1    # Comparable#== calls <=>
end
shape("Comparable#== calling <=> calling ==") { Cmp.new == Cmp.new }

class SelfHash
  def hash = [self].hash
end
shape("hash through Array#hash") { SelfHash.new.hash.class }

# Self-referential structures are not deep: MRI's recursion guard prints [...] and {...}.
a = []; a << a
shape("inspect of a self-referential array") { a.inspect }
h = {}; h[:h] = h
shape("inspect of a self-referential hash") { h.inspect }
shape("hash of a self-referential array") { a.hash.class }
shape("flatten of a self-referential array") { a.flatten }

# A fresh structure for each: MRI's recursion guard is left holding the elements an overflow
# unwound through, and a second walk over the same structure then stops early at "[...]".
N = 200_000
def nested_array = (1..N).reduce([]) { |a, _| [a] }
def nested_hash = (1..N).reduce({}) { |h, _| {k: h} }
shape("deeply nested Array#inspect") { nested_array.inspect.size }
shape("deeply nested Array#to_s") { nested_array.to_s.size }
shape("deeply nested Array#hash") { nested_array.hash.class }
shape("deeply nested Array#==") { nested_array == nested_array }
shape("deeply nested Array#eql?") { nested_array.eql?(nested_array) }
shape("deeply nested Array#<=>") { nested_array <=> nested_array }
shape("deeply nested Array#flatten") { nested_array.flatten.size }
shape("deeply nested Hash#hash") { nested_hash.hash.class }
shape("deeply nested Hash#inspect") { nested_hash.inspect.size }
shape("deeply nested Hash#==") { nested_hash == nested_hash }
shape("deeply nested Marshal.dump") { Marshal.dump(nested_array).size }
nested = nested_array
nh = nested_hash
begin; nested.inspect; rescue SystemStackError; end
shape("nested structures still usable") { [nested.equal?(nested), nh.size] }

# ---- threads and fibers ---------------------------------------------------

shape("inside a Thread") do
  Thread.new do
    begin
      plain(0)
    rescue SystemStackError => e
      "in thread: #{e.class}"
    end
  end.value
end
depth_report("inside a Thread (depth reached)")

shape("inside a Thread, uncaught, Thread#value") do
  t = Thread.new { plain(0) }
  t.report_on_exception = false
  t.value
end

shape("inside a Fiber") do
  Fiber.new do
    begin
      plain(0)
    rescue SystemStackError => e
      "in fiber: #{e.class}"
    end
  end.resume
end

shape("inside a Fiber, uncaught, resume") { Fiber.new { plain(0) }.resume }

# ---- what the exception is ------------------------------------------------

caught = begin
  begin
    plain(0)
  rescue => e                       # a bare rescue is StandardError: must not catch it
    "bare rescue caught #{e.class}"
  end
rescue SystemStackError
  "not caught by a bare rescue"
end
puts "bare rescue: #{caught}"

caught = begin
  plain(0)
rescue Exception => e
  "rescue Exception caught #{e.class}"
end
puts "rescue Exception: #{caught}"

puts "SystemStackError.ancestors: #{SystemStackError.ancestors.take_while { |c| c != Kernel }.inspect}"
puts "is a StandardError: #{SystemStackError < StandardError ? true : false}"

err = begin; plain(0); rescue SystemStackError => e; e; end
puts "message: #{err.message.inspect}"
puts "backtrace is an Array of Strings: #{err.backtrace.is_a?(Array) && err.backtrace.all?(String)}"
puts "backtrace names the method: #{err.backtrace.first.to_s.include?('plain')}"
puts "backtrace_locations: #{err.backtrace_locations.is_a?(Array)}"
$stderr.puts "backtrace length: #{err.backtrace.size}"

# rescue, retry a shallower recursion from the rescue clause
r = begin
  plain(0)
rescue SystemStackError
  hot(1000)
end
puts "recursing from inside the rescue clause: #{r}"

r = 0
3.times do
  begin
    plain(0)
  rescue SystemStackError
    r += 1
  end
end
puts "three overflows in a row: #{r}"

puts "done"
