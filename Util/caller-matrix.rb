# Differential matrix for Kernel#caller, #caller_locations, the default file name of an eval
# ("(eval at file:line)"), warn(uplevel:) and __dir__: what each reports from the shapes of frame
# IronRuby has - interpreted and compiled methods and blocks, light frames, typed (JIT) bodies,
# OSR loops, define_method, module bodies, eval'd code, the core methods written in Ruby, threads.
# Run under CRuby and under ./ir.sh, with the JIT on and off and without light frames, and diff:
#
#   ruby Util/caller-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/caller-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/caller-matrix.rb > /tmp/nojit.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/caller-matrix.rb > /tmp/nolight.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt && diff /tmp/mri.txt /tmp/nolight.txt
#
# All must end with the row `end'. Each row runs its shape 300 times (past the interpreter's
# compilation and the JIT's thresholds) and prints each distinct answer once, with the calls
# that gave it: a row with more than one answer changed its mind when the code got hot.

$stdout.sync = true
FILE = File.basename(__FILE__)
DIR = File.dirname(File.expand_path(__FILE__))

# A frame or a file name without the directory, which differs between the engines' checkouts.
def strip(s)
  s.is_a?(String) ? s.gsub(DIR + "/", '').gsub(/(?<=[\s(]|\A)\S*?#{Regexp.escape(FILE)}/, FILE) : s
end

def loc(l)
  [l.path, l.lineno, l.label, l.base_label, l.absolute_path, l.to_s, l.inspect].map { |x| strip(x) }
end

# Everything the probe reports, from the frame that called it.
def probe
  r = [caller(0, 5), caller(1, 2), caller, caller_locations(1, 1).map { |l| loc(l) }, caller_locations(0, 3).map { |l| l.label }]
  r << eval('__FILE__')
  r << eval('[__FILE__, __LINE__]')
  r << eval('eval("__FILE__")')
  r.map { |x| x.is_a?(Array) ? x.map { |y| y.is_a?(Array) ? y : strip(y) } : strip(x) }
end

# The line of an eval in a statement that spans lines: the eval's own.
def multiline
  [1,
   eval('__FILE__')].last
end

# caller with the driver's own frames cut off: the frames below `drive' are the same in every row.
def cut(r)
  return r unless r.is_a?(Array)
  r.map { |x| x.is_a?(Array) && x.all? { |y| y.is_a?(String) } ? x.take_while { |y| !y.include?("'drive'") && !y.include?("'Object#drive'") } : x }
end

def drive(label, n = 300)
  seen = {}
  order = []
  n.times do |k|
    r = cut(yield(k))
    (seen[r] ||= (order << r; [])) << k
  end
  puts "#{label}:"
  order.each { |r| puts "  #{r.inspect} at #{seen[r].size == n ? 'all' : seen[r].first(5).inspect}" }
end

# 1. methods calling methods
def m1a(k) = m1b(k)
def m1b(k)
  probe
end
drive('1. method chain') { |k| m1a(k) }

# 2. a light frame (no locals, no block): a one-liner around the probe
def m2(k) = probe
drive('2. light frame') { |k| m2(k) }

# 3. blocks: of a library method, nested, of a Ruby method that yields
def m3a(k)
  [k].each { |x| return probe }
end
def m3yield
  yield
end
def m3b(k)
  m3yield { [1].map { |y| m3yield { probe } } }.first
end
drive('3a. block in method') { |k| m3a(k) }
drive('3b. nested blocks') { |k| m3b(k) }

# 4. define_method, a lambda, a proc, a method object
class C4
  define_method(:dm) { |k| probe }
  def via_send(k) = send(:dm, k)
end
L4 = lambda { |k| probe }
drive('4a. define_method') { |k| C4.new.dm(k) }
drive('4b. send') { |k| C4.new.via_send(k) }
drive('4c. lambda') { |k| L4.(k) }
drive('4d. method object') { |k| C4.new.method(:dm).call(k) }

# 5. a typed body: integer arithmetic around the call
def m5(i)
  a = i + 1
  r = probe
  a * 2
  r
end
drive('5. typed body') { |k| m5(k) }

# 6. a loop (OSR) in a method
def m6(n)
  i = 0
  r = nil
  while i < n
    r = probe if i == n - 1
    i += 1
  end
  r
end
drive('6. OSR loop', 30) { |k| m6(200) }

# 7. module and class bodies, and the top level
$r7 = []
3.times do
  class C7
    $r7 << probe
    module M7
      $r7 << probe
    end
  end
end
puts "7. class and module bodies: #{$r7.uniq.inspect}"
puts "7b. top level: #{probe.inspect}"

# 8. eval'd code: the probe in an eval, an eval in an eval, with a binding, instance_eval and class_eval of strings
def m8a(k) = eval('probe')
def m8b(k) = eval('eval("probe")')
def m8c(k) = eval('probe', binding)
def m8d(k) = eval('probe', nil, 'given.rb', 10)
drive('8a. eval') { |k| m8a(k) }
drive('8b. eval in eval') { |k| m8b(k) }
drive('8c. eval with binding') { |k| m8c(k) }
drive('8d. eval with file') { |k| m8d(k) }
drive('8e. instance_eval string') { |k| Object.new.instance_eval('probe') }
drive('8f. class_eval block') { |k| C4.class_eval { probe } }

# 9. core methods written in Ruby around a block
drive('9a. sort_by') { |k| [2, 1].sort_by { |x| $r9 = probe; x }; $r9 }
drive('9b. inject') { |k| [1, 2].inject { |a, b| $r9 = probe; a + b }; $r9 }
drive('9c. each_with_index') { |k| [1].each_with_index { |x, i| $r9 = probe }; $r9 }
drive('9d. tap and then') { |k| 1.tap { $r9 = probe }.then { $r9 } }

# 10. rescue and ensure
def m10(k)
  begin
    raise 'x'
  rescue
    r = probe
  ensure
    r2 = probe
  end
  [r, r2]
end
drive('10. rescue and ensure') { |k| m10(k) }

# 11. threads: a thread's own stack
drive('11. thread', 20) { |k| Thread.new { m1a(k) }.value }

# 12. caller with ranges and past the bottom
def m12
  [caller(0..1), caller(1...2), caller(0, 0), caller(100), caller(100, 1), caller_locations(100), caller(0)[-1] == caller.last,
   caller(1, 1) == [caller(0)[1]], caller_locations(0).size == caller(0).size].map { |x| x.is_a?(Array) ? x.map { |y| strip(y) } : x }
end
drive('12. ranges') { |k| m12 }

# 13. warn(uplevel:) and __dir__
require 'stringio'
def m13w(k)
  old = $stderr
  $stderr = StringIO.new
  warn('x', uplevel: 0)
  warn('y', uplevel: 1)
  $stderr.string
ensure
  $stderr = old
end
def m13d(k) = [__dir__ == DIR, eval('__dir__') == DIR, eval('__dir__', nil, 'nowhere/x.rb')]
drive('13a. warn uplevel') { |k| strip(m13w(k)) }
drive('13b. __dir__') { |k| m13d(k) }

puts "14. eval in a statement over two lines: #{strip(multiline)}"

puts 'end'
