# Differential matrix for eval of a String (Kernel#eval, instance_eval, class_eval and module_eval
# with a string, Binding#eval): RubyUtils.Evaluate. Each row evaluates the same text many times
# ("hot") or many different texts, in the situations where code compiled for one eval could be
# wrong for another: the file and line, the method and block it runs in, the locals it can see
# and those it defines, warnings, syntax errors, magic comments, constants, refinements,
# TracePoint and threads. Run under CRuby and under ./ir.sh - with the JIT on and off - and diff:
#
#   ruby Util/eval-matrix.rb > /tmp/mri.txt; echo $?
#   ./ir.sh Util/eval-matrix.rb > /tmp/jit.txt; echo $?
#   ./ir.sh -X:NoJIT -X:NoOSR Util/eval-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# All must exit 0, end with the row `end', and be identical. Whether the JIT is on goes to stderr.

$stdout.sync = true
FILE = File.basename(__FILE__)
$stderr.puts "[eval-matrix] #{RUBY_ENGINE} #{RUBY_ENGINE == 'ironruby' ? (defined?(IronRuby) ? 'ir' : '') : RUBY_VERSION}"

def frame(line)
  line.sub(/\A.*?#{Regexp.escape(FILE)}:/, '')
end

# Warnings are counted, not printed: the count must be the same for a cold and a hot eval.
WARNINGS = []
module Warning
  def self.warn(msg, category: nil)
    WARNINGS << msg.sub(/\A.*?:\d+: /, '')
  end
end

# 1. the same text, hot
s = 0
3000.times { s += eval('1 + 1') }
puts "1. eval('1 + 1') x3000: #{s}"

# 2. many different texts, more than any cache would hold, then the first again
s = 0
3000.times { |i| s += eval("#{i} * 2") }
puts "2. 3000 different texts: #{s} #{eval('0 * 2')} #{eval('2999 * 2')}"

# 3. __FILE__ and __LINE__, given and not, from one call site and two
r = []
3.times { |i| r << eval("[__FILE__, __LINE__]", nil, "f#{i % 2}.rb", 10 + i) }
3.times { r << eval("\n[__FILE__, __LINE__]", nil, "g.rb", -2) }
3.times { r << eval("[__FILE__, __LINE__]").then { |f, l| [frame(f), l] } }
r << eval("[__FILE__, __LINE__]").then { |f, l| [frame(f), l] }
puts "3. file and line: #{r.inspect}"

# 4. __method__ and the frame label, same text in different methods and a block
def m4a; eval('[__method__, caller_locations(0, 1)[0].label]'); end
def m4b; eval('[__method__, caller_locations(0, 1)[0].label]'); end
def m4c; [1].map { eval('[__method__, caller_locations(0, 1)[0].label]') }[0]; end
r = []
2.times { r << m4a << m4b << m4c << eval('[__method__, caller_locations(0, 1)[0].label]') }
puts "4. __method__ and label: #{r.inspect}"

# 5. a local the eval defines is seen by the next eval in the same binding, not by another
def m5
  b = binding
  r = [eval('defined?(v5)', b)]
  eval('v5 = 1', b)
  r << eval('defined?(v5)', b) << eval('v5 += 1; v5', b) << eval('v5', b) << b.local_variables
  b2 = binding
  r << eval('defined?(v5)', b2) << (eval('v5', b2) rescue $!.class)
  r
end
puts "5. locals defined by eval: #{m5.inspect} #{m5.inspect}"

# 6. the same text sees a local in one binding and a method call in another
def v6; :method; end
def m6a; v6 = :local; [v6, eval('v6')]; end
def m6b; eval('v6'); end
r = []
3.times { r << m6a << m6b }
puts "6. local or method: #{r.inspect}"

# 7. binding inside an eval, and its locals
def m7(a)
  x = a
  2.times.map { eval('y = x * 2; b = binding; [b.local_variables.sort, b.local_variable_get(:y)]') } + [binding.local_variables.sort]
end
puts "7. binding in eval: #{m7(1).inspect} #{m7(5).inspect}"

# 8. warnings are given each time the text is evaluated
WARNINGS.clear
$VERBOSE = true
5.times { eval('x8 = 1; x8 == 1; nil') }
5.times { eval('/[aa]/') }
$VERBOSE = false
3.times { eval('x8 = 1; x8 == 1; nil') }
$VERBOSE = true
2.times { eval('x8 = 1; x8 == 1; nil') }
$VERBOSE = nil
2.times { eval('/[aa]/') }
$VERBOSE = false
puts "8. warnings: #{WARNINGS.size} #{WARNINGS.uniq.inspect}"

# 9. backtrace lines of an exception raised in eval'd code
r = []
4.times do |i|
  begin
    eval("\n\nraise 'x'", nil, 'r.rb', 10 + (i % 2))
  rescue => e
    r << frame(e.backtrace[0])
  end
end
puts "9. backtrace: #{r.inspect}"

# 10. a SyntaxError each time, with its message
r = []
3.times do
  begin
    eval('1 +', nil, 's.rb', 7)
  rescue SyntaxError => e
    r << [e.class, e.message.lines.first.chomp]
  end
end
puts "10. syntax error: #{r.uniq.inspect} #{r.size}"

# 11. frozen string literals, by magic comment and not; a literal is a new string each time
r = []
3.times { r << eval("# frozen_string_literal: true\n'a'").frozen? << eval("'a'").frozen? }
a = 2.times.map { eval("'abc'") }
a[0] << 'd'
puts "11. frozen: #{r.inspect} #{a.inspect} #{a[0].equal?(a[1])}"

# 12. constants: the same text in different modules
module M12a; K = :a; end
module M12b; K = :b; end
r = []
3.times { r << M12a.module_eval('K') << M12b.class_eval('K') << (eval('K') rescue $!.class) }
puts "12. constants: #{r.inspect}"

# 13. instance_eval and class_eval with the same text on different receivers
class C13; def initialize(v); @v = v; end; end
r = []
3.times { |i| r << C13.new(i).instance_eval('@v') << C13.new(i).instance_eval('self.class') }
k1 = Class.new; k2 = Class.new
3.times { k1.class_eval('def m13; :k1; end'); k2.class_eval('def m13; :k2; end') }
r << k1.new.m13 << k2.new.m13
puts "13. instance_eval and class_eval: #{r.inspect}"

# 14. super in eval, the same text in two methods
class P14; def m(a, b = 2); [:p, a, b]; end; def n(*a); [:pn, *a]; end; end
class Q14 < P14; def m(a, b = 3); eval('super'); end; def n(x, y); eval('super'); end; end
r = []
3.times { r << Q14.new.m(1) << Q14.new.n(4, 5) }
puts "14. super: #{r.inspect}"

# 15. TracePoint :script_compiled fires once for each eval
n = 0
tp = TracePoint.new(:script_compiled) { n += 1 }
tp.enable { 5.times { eval('1 + 1') } }
puts "15. script_compiled: #{n}"

# 16. threads evaluating the same and different texts at once
r = 4.times.map do |t|
  Thread.new do
    s = 0
    500.times { |i| s += eval('i + t') + eval("#{i % 50} + 1") }
    s
  end
end.map(&:value)
puts "16. threads: #{r.inspect}"

# 17. a method defined by eval'd text in a method called many times
class C17; end
5.times { |i| C17.class_eval("def m#{i % 2}; #{i}; end") }
puts "17. def in eval: #{C17.new.m0} #{C17.new.m1}"

# 18. a block made in eval'd code keeps the locals of its own eval
procs = 3.times.map { |i| eval("w = i * 10; -> { w }") }
puts "18. closures: #{procs.map(&:call).inspect}"

# 19. visibility set inside the string stays inside it
class C19
  3.times { eval('private'); }
  def pub; end
end
puts "19. visibility: #{C19.public_method_defined?(:pub)}"

# 20. refinements: the same text before and inside a `using'
module R20; refine Integer do; def twice; self * 2; end; end; end
def m20; eval('1.twice rescue :none'); end
r = [m20, eval('1.twice rescue :none')]
using R20
r << eval('1.twice rescue :none') << m20 << eval('1.twice rescue :none')
puts "20. refinements: #{r.inspect}"

puts 'end'
