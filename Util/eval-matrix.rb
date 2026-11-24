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

# 16. threads evaluating the same and different texts at once (each its own sum: a top-level `s'
# would be one variable for all four)
r = 4.times.map do |t|
  Thread.new do
    sum16 = 0
    500.times { |i| sum16 += eval('i + t') + eval("#{i % 50} + 1") }
    sum16
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

# 21. the same text, file and line before and inside a `using', and in a method defined before it
def m21; eval('1.thrice rescue :none', nil, 'u.rb', 1); end
module R21; refine Integer do; def thrice; self * 3; end; end; end
r = [m21, eval('1.thrice rescue :none', nil, 'u.rb', 1)]
using R21
r << eval('1.thrice rescue :none', nil, 'u.rb', 1) << m21 << eval('1.thrice rescue :none', nil, 'u.rb', 1)
puts "21. refinements, one key: #{r.inspect}"

# 22. the same text, file and line in methods of different classes: constants and class variables
class A22; K = :a; @@cv = :ca; def k; eval('[K, @@cv, Module.nesting]', nil, 'k.rb', 1); end; end
class B22; K = :b; @@cv = :cb; def k; eval('[K, @@cv, Module.nesting]', nil, 'k.rb', 1); end; end
r = []
3.times { r << A22.new.k << B22.new.k << A22.new.instance_eval('K rescue :ne', 'k.rb', 1) << A22.class_eval('K', 'k.rb', 1) << B22.class_eval('K', 'k.rb', 1) }
puts "22. constants, one key: #{r.inspect}"

# 23. a class body in eval'd text, hot: its frame in a backtrace, through an ensure, and a value out of it
def m23r; raise ArgumentError; ensure; $z23 = 2; end
def m23; eval "class ::C23\n begin\n  m23r\n ensure\n  $z23 = 1\n end\nend"; end
def t23; m23; rescue => e; e.backtrace.first(3).map { |l| l[/:\d+:in '[^']*'/] }; end
r = 300.times.map { t23 }
v = 300.times.map { eval("class C23; [self, 1]; end") }
puts "23. class body: #{r.uniq.inspect} #{r.size} #{v.uniq.inspect}"

# 24. literals inside a method or block the eval'd text defines, which are compiled at their first
# call and not with the text: each eval of the same text makes a method with its own `/o' regexp,
# and a literal regexp or range is one object per method, not one for every eval of the text
r = []
3.times { |i| eval("def evo24(x) = /\#{x}/o"); r << evo24("v#{i}") << evo24("w#{i}") }
a = []
2.times { eval("def evl24 = /lit/"); a << evl24 << evl24 }
b = []
2.times { eval("def evr24 = (1..2)"); b << evr24 }
c = []
2.times { eval("define_method(:evb24) { /blk/ }"); c << evb24 }
puts "24. literals in defs: #{r.inspect} #{[a[0].equal?(a[1]), a[0].equal?(a[2]), b[0].equal?(b[1]), c[0].equal?(c[1])].inspect}"

# 25. blocks and loops of eval'd text that run past the interpreter's compilation threshold (an
# eval's is higher than other code's): a method redefined, a closure local, a /o regexp, break and
# next while they run; a while loop whose locals the binding keeps; a define_method block called on
def f25(i) = i
src25 = "acc = []; o = nil\n1500.times { |i| (def f25(i) = -i) if i == 700; (def f25(i) = i * 2) if i == 1200\n" \
        "o = /o\#{i}/o; next if i.odd?; break if i == 1400; acc << f25(i) }\n" \
        "j = 0; s = 0; while j < 3000; s += j; j += 1; end\n" \
        "define_method(:g25) { |x| x + j }\n[acc.size, acc.sum, acc[349..352], o, s]"
r = 3.times.map { eval(src25) }
g = (1..2500).sum { |x| g25(x) }
b25 = binding; eval("k25 = 0; 2000.times { k25 += 1 }", b25)
puts "25. hot blocks in eval: #{r.uniq.inspect} #{r.size} #{g} #{b25.local_variable_get(:k25)}"

# 26-31: the trees of evaluated strings are kept (EvalAstCache, keyed by the text, file, line,
# outer locals and encoding) and transformed and compiled afresh at each eval.
# 26. the same text where a name is a method, a parameter, or a local an earlier eval of the
# binding defined
def x26 = :meth
def a26 = eval("x26")
def b26(x26) = eval("x26")
def c26
  b = binding
  r = [eval("defined?(z26) ? z26 : :none", b)]
  eval("z26 = 7", b)
  r << eval("defined?(z26) ? z26 : :none", b) << eval("x26", b)
end
r = 600.times.map { |i| i.even? ? a26 : b26(i % 3) }
puts "26. names: #{r.tally.sort_by(&:to_s).inspect} #{300.times.map { c26 }.uniq.inspect}"

# 27. the same text with a different file and line, hot (a line of 0 is left out: its backtrace
# line differs on the baseline too)
r = 600.times.map { |i| eval("[__FILE__, __LINE__, __method__.inspect, caller(0)[0].split('/').last]", nil, "f#{i % 2}.rb", [-1, 1, 7][i % 3]) }
puts "27. file and line: #{r.uniq.sort.inspect}"

# 28. the same characters in strings of other encodings, and with a magic comment
u = "['é'.encoding.to_s, 'é'.bytesize, __ENCODING__.to_s]"
r = 300.times.map { |i| eval(i % 3 == 0 ? u : i % 3 == 1 ? u.encode("ISO-8859-1") : u.b) }
m = 200.times.map { |i| eval(i.even? ? "# encoding: us-ascii\n__ENCODING__.to_s" : "__ENCODING__.to_s") }
puts "28. encodings: #{r.uniq.inspect} #{m.uniq.inspect}"

# 29. a syntax error and an error found while compiling, the same each time after the text was
# evaluated without one in another place
e = 3.times.map { begin; eval("1 +"); rescue SyntaxError => x; x.message.lines.first.chomp; end }
f = 3.times.map { |i| begin; eval("break", nil, "b29.rb", 5); rescue SyntaxError => x; x.message[/\A[^\n]*/]; end }
g = [1, 2].map { |v| eval("[v * 2].each { |w| break w + 1 }") }
puts "29. errors: #{e.uniq.inspect} #{f.uniq.inspect} #{g.inspect}"

# 30. an ERB template rendered hot, with other values each time, and one that defines a method
# with a /o regexp and a literal each time it is rendered
require 'erb'
t30 = ERB.new("<% items.each do |it| -%><%= title %>:<%= it * k %>,<% end -%>|<%= defined?(extra) ? extra : '-' %>", trim_mode: '-')
r = 400.times.map { |i| t30.result_with_hash(title: "t#{i % 4}", items: [1, 2, i % 5], k: i % 7) }
x = 3.times.map { t30.result_with_hash(title: "x", items: [], k: 1, extra: "E") }
d30 = ERB.new("<% def d30(v) = [/a\#{v}/o, /lit/] %><%= d30(n).first.source %>")
o = 3.times.map { |n| d30.result_with_hash(n: n) + d30(9).first.source + d30(9)[1].equal?(d30(8)[1]).to_s }
puts "30. erb: #{r.sum(&:size)} #{r.uniq.size} #{r[0..1].inspect} #{x.uniq.inspect} #{o.inspect}"

# 31. threads rendering the same template and evaluating the same text with other locals at once
r = 6.times.map do |n|
  Thread.new do
    s31 = 0
    300.times { |i| s31 += t30.result_with_hash(title: "t", items: [n, i % 3], k: n).size + eval("n * i + 1") }
    s31
  end
end.map(&:value)
puts "31. threads: #{r.inspect}"

# 32. state-keeping literals nested where a walk of the tree could miss them - a call's block, a
# parallel assignment's right side, a hash value, an interpolation, a keyword argument, a lambda -
# each a /o regexp evaluated once per eval, and regexp and range literals new objects per eval
def kw32(k:) = k
r = 3.times.map do |i|
  [eval("[1].map { /b\#{i}/o }.first.source"), eval("_a, b32 = 1, /p\#{i}/o; b32.source"),
   eval("{ k: /h\#{i}/o }[:k].source"), eval("\"\#{/s\#{i}/o.source}\""), eval("kw32(k: /k\#{i}/o).source"),
   eval("-> { /l\#{i}/o }.call.source"), eval("[1].each_with_object([]) { |_, a| a << (i..i + 1) }.first")]
end
l = 2.times.map { eval("[1].map { /lit/ }.first") }
g = 2.times.map { eval("[1].map { (1..2) }.first") }
puts "32. nested literals: #{r.inspect} #{l[0].equal?(l[1])} #{g[0].equal?(g[1])}"

# 33. a string literal is a new String at each eval of a text run hot, `.freeze' the same one
s33 = 2500.times.map { eval("'ab'") }
s33[0] << 'c'
f33 = 2.times.map { eval("'fz'.freeze") }
puts "33. string literals: #{s33.map(&:object_id).uniq.size} #{s33[1]} #{s33[0]} #{eval("'ab'")} #{f33[0].equal?(f33[1])} #{s33[5].frozen?}"

# 34. the same text, file and line hot past the compilation threshold: a method it calls redefined,
# a singleton method added to its receiver, made private, and an exception raised through it
def m34(i) = i
o34 = Object.new
def o34.v = 1
acc = 0
bt = nil
3000.times do |i|
  def m34(i) = -i if i == 1000
  (def o34.v = 2) if i == 1500
  o34.singleton_class.send(:private, :v) if i == 2000
  begin
    acc += eval("m34(i) + o34.v", nil, "m34.rb", 3)
  rescue NoMethodError => e
    bt ||= [e.message[/private method '[^']*'/], e.backtrace[0].split('/').last]
    acc += 1
  end
end
puts "34. hot and changed: #{acc} #{bt.inspect}"

# 35. a block of the same text run past the threshold in evals in two methods: its frame labels
def l35a = eval("[1, 2].map { caller(0)[0][/'.*'/] }.uniq", nil, "l35.rb", 1)
def l35b = eval("[1, 2].map { caller(0)[0][/'.*'/] }.uniq", nil, "l35.rb", 1)
r = 700.times.map { |i| i.even? ? l35a : l35b }
puts "35. labels: #{r.uniq.inspect}"

puts 'end'
