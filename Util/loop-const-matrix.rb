# Differential matrix for outlined (OSR) loops that fold constants (JitLoopCompiler, gap 29): a loop long enough to be
# outlined (IR_OSR_THRESHOLD, 2,000 back edges) reads constants; between its entries something happens to constants -
# a new one elsewhere, an extend, a require, the same value set again, a removal, a reassignment, a nearer one
# lexically or through the ancestors, an autoload - and each entry's sum is printed. The loops are written in blocks,
# which the method JIT does not take, so they are outlined with the JIT on as well as off. Run under CRuby and under
# ./ir.sh in each mode and diff:
#
#   ruby Util/loop-const-matrix.rb > /tmp/mri.txt; echo $?
#   ./ir.sh Util/loop-const-matrix.rb > /tmp/jit.txt; echo $?
#   ./ir.sh -X:NoJIT Util/loop-const-matrix.rb > /tmp/nojit.txt
#   IR_LOOP_CONSTANTS=0 ./ir.sh Util/loop-const-matrix.rb > /tmp/off.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt && diff /tmp/mri.txt /tmp/off.txt
#
# All must exit 0, end with the row `end', and be identical. Warnings ("already initialized constant") go to stderr,
# which is not compared; IR_OSR_VERBOSE=1 says what the loops did.
$stdout.sync = true
$VERBOSE = nil
require 'tmpdir'

N = 3000
def run = yield
def row(label, value) = puts("#{label}: #{value.inspect}")
# n entries of the loop the block runs, with what `between' does before each one; the sums, as runs [sum, how many]
def entries(n, between)
  out = []
  n.times { |k| between&.call(k); out << yield }
  out.chunk_while { |a, b| a == b }.map { |c| [c[0], c.size] }
end

# ---------------------------------------------------------------- 1. events that must not change the answer
TOP = 2
module G; module P; RUBY = 3; end; end
module M1; end
DIR = Dir.mktmpdir("loopconst")
loop1 = -> { run { i = 0; s = 0; while i < N; s += TOP + G::P::RUBY; i += 1; end; s } }
row "nothing", entries(12, nil) { loop1.() }
row "new constant elsewhere", entries(12, ->(k) { Object.const_set("NEW1_#{k}", k) }) { loop1.() }
row "extend", entries(12, ->(k) { Object.new.extend(M1) }) { loop1.() }
row "singleton class", entries(12, ->(k) { Object.new.singleton_class }) { loop1.() }
row "require", entries(12, ->(k) { File.write("#{DIR}/r1_#{k}.rb", "R1_#{k} = #{k}\n"); require "#{DIR}/r1_#{k}" }) { loop1.() }
row "same value set again", entries(12, ->(k) { Object.const_set(:TOP, TOP) }) { loop1.() }
row "include into an unrelated class", entries(12, ->(k) { Class.new.include(Module.new { const_set(:TOP, 99) }) }) { loop1.() }

# ---------------------------------------------------------------- 2. changes that must
TOP2 = 1
loop2 = -> { run { i = 0; s = 0; while i < N; s += TOP2; i += 1; end; s } }
row "reassigned", entries(12, ->(k) { Object.const_set(:TOP2, k) if k % 4 == 0 }) { loop2.() }
TOP3 = 1
loop3 = -> { run { i = 0; s = 0; while i < N; s += (defined?(TOP3) ? TOP3 : 0); i += 1; end; s } }
row "removed and set again", entries(12, ->(k) { Object.send(:remove_const, :TOP3) if k == 4; Object.const_set(:TOP3, 7) if k == 8 }) { loop3.() }
TOP4 = 1
loop4 = -> { begin; run { i = 0; s = 0; while i < N; s += TOP4; i += 1; end; s }; rescue NameError => e; e.class; end }
row "removed for good", entries(8, ->(k) { Object.send(:remove_const, :TOP4) if k == 4 }) { loop4.() }

# a nearer constant appears between entries: lexically, through a superclass, an included and a prepended module
X5 = 1
module L5
  def self.go = run { i = 0; s = 0; while i < N; s += X5; i += 1; end; s }
end
row "nearer lexical", entries(10, ->(k) { L5.const_set(:X5, 10) if k == 5 }) { L5.go }
class B6; end
class C6 < B6
  def self.go = run { i = 0; s = 0; while i < N; s += X6; i += 1; end; s }
end
X6 = 1
row "nearer in a superclass", entries(10, ->(k) { B6.const_set(:X6, 20) if k == 5 }) { C6.go }
X7 = 1
class C7
  def self.go = run { i = 0; s = 0; while i < N; s += X7; i += 1; end; s }
end
row "nearer by include", entries(10, ->(k) { C7.include(Module.new { const_set(:X7, 30) }) if k == 5 }) { C7.go }
module Q8; X = 1; end
module R8; end
loop8 = -> { run { i = 0; s = 0; while i < N; s += Q8::X; i += 1; end; s } }
# (not `Q8.prepend(Module.new { const_set(:X, 40) })`: IronRuby finds the prepended X in every mode, CRuby Q8's own)
row "qualified, its path replaced", entries(10, ->(k) { Object.const_set(:Q8, Module.new { const_set(:X, 50) }) if k == 5 }) { loop8.() }

# an autoload registered nearer between entries, and a loop entered while an autoload of that name is in flight
X9 = 1
File.write("#{DIR}/x9.rb", "module L9; X9 = 9; end\n")
module L9
  def self.go = run { i = 0; s = 0; while i < N; s += X9; i += 1; end; s }
end
row "autoload registered nearer", entries(8, ->(k) { L9.autoload(:X9, "#{DIR}/x9.rb") if k == 4 }) { L9.go }
X10 = 1
File.write("#{DIR}/x10.rb", "$x10_inside = 4.times.map { L10.go }\nmodule L10; X10 = 10; end\n")
module L10
  def self.go = run { i = 0; s = 0; while i < N; s += X10; i += 1; end; s }
end
before = 6.times.map { L10.go }
L10.autoload(:X10, "#{DIR}/x10.rb")
row "loop entered during the autoload", [before, (L10::X10 rescue $!.class), $x10_inside, L10.go]

# ---------------------------------------------------------------- 3. another thread
TOP11 = 1
loop11 = -> { run { i = 0; s = 0; while i < N; s += TOP11; i += 1; end; s } }
stop = false
definer = Thread.new { k = 0; until stop; Object.const_set("T11_#{k % 500}", k); Object.new.extend(M1); k += 1; end }
sums = 400.times.map { loop11.() }
stop = true; definer.join
row "another thread defining elsewhere", sums.uniq
X12 = 1
module L12
  def self.go = run { i = 0; s = 0; while i < N; s += X12; i += 1; end; s }
end
seen = []
t = Thread.new { k = 0; until (k += 1) > 20000 || seen.last == 100 * N; seen << L12.go; Thread.pass; end }
sleep 0.01 until seen.size > 50
L12.const_set(:X12, 100)
t.join
row "nearer defined on another thread (between entries)", [seen.first, seen.last, seen.uniq.size <= 2, seen.drop_while { |v| v == N }.uniq]

puts "end"
