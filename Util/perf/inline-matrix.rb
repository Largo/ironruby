# Differential cases for the method JIT's inlining of small pure methods (JitCompiler.Inline.cs):
# a method calling a pure Float/Integer method on self, or on an object whose class the profile saw,
# run hot, with what must invalidate the inlined call happening in the middle.
#
# One case per process, since one case's invalidation would change what the next one compiles:
#   for c in $(ruby Util/perf/inline-matrix.rb list); do echo "== $c"; ruby Util/perf/inline-matrix.rb $c 2>&1; done > cruby.txt
#   (the same with IR_CONFIG=Release ./ir.sh, and with ./ir.sh -X:NoJIT -X:NoOSR) and compare: "same" is identical output.
# IR_JIT_VERBOSE=1 shows `inlined P#cmp into go' (or `into go_on') for each case: the path is reached.

CASES = %w[redefine subclass singleton private protected refine prepend remove_method undef_method
           argtypes raise caller method recursion explicit explicit_subclass
           explicit_private explicit_protected explicit_missing]

if ARGV[0] == 'list'
  puts CASES
  exit
end
c = ARGV[0]

class P
  def initialize(x); @x = x; end

  def cmp(a, b)
    return 0 if a == b
    return -1 if a < b
    1
  end

  def go(o)
    r = cmp(@x, o)
    r == 0 ? cmp(o, 1.5) : r
  end

  # an explicit receiver, of a class the profile saw
  def go_on(q, o)
    q.cmp(o, 2.5)
  end
end

class Q < P
end

def run(n, mid)
  p = P.new(2.5)
  sum = 0
  n.times do |i|
    mid.call(i) if i == n / 2 && mid
    v = (i % 3 == 0) ? 1.0 : (i % 3 == 1 ? 2.5 : 4.0)
    p = yield(p, i) if block_given?
    sum += p.go(v)
  end
  sum
end

N = 4000

case c
when 'redefine'
  puts run(N, ->(i) { P.class_eval { def cmp(a, b); 10; end } })
when 'subclass'
  Q.class_eval { def cmp(a, b); 7; end }
  puts run(N, nil) { |p, i| i >= N / 2 ? Q.new(2.5) : p }
when 'singleton'
  obj = nil
  puts run(N, nil) { |p, i|
    if i == N / 2
      obj = P.new(2.5)
      def obj.cmp(a, b); 100; end
      obj
    else
      p
    end
  }
when 'private'
  puts run(N, ->(i) { P.send(:private, :cmp) })
  begin
    P.new(1.0).cmp(1.0, 2.0)
  rescue NoMethodError => e
    puts e.message
  end
when 'protected'
  puts run(N, ->(i) { P.send(:protected, :cmp) })
  begin
    P.new(1.0).cmp(1.0, 2.0)
  rescue NoMethodError => e
    puts e.message
  end
  # the explicit-receiver caller, inside the class, may still call it
  q = P.new(1.0)
  s = 0
  N.times { |i| s += q.go_on(q, i.to_f) }
  puts s
when 'refine'
  module R
    refine P do
      def cmp(a, b); 1000; end
    end
  end
  puts run(N, nil)
  using R
  puts P.new(1.0).cmp(1.0, 1.0)
  puts run(N, nil)
when 'prepend'
  m = Module.new { def cmp(a, b); super * 3; end }
  puts run(N, ->(i) { P.prepend(m) })
when 'remove_method'
  P.class_eval { def method_missing(n, *a); n == :cmp ? 50 : super; end }
  puts run(N, ->(i) { P.send(:remove_method, :cmp) })
when 'undef_method'
  P.class_eval { def method_missing(n, *a); n == :cmp ? 60 : super; end }
  puts run(N, ->(i) { P.send(:undef_method, :cmp) })
when 'argtypes'
  vals = [1.0, 2.5, 3, 2**70, nil]
  p = P.new(2.5)
  sum = 0
  N.times do |i|
    k = (i < N / 2) ? i % 2 : 2 + (i % 3)
    begin
      sum += p.go(vals[k])
    rescue NoMethodError, ArgumentError => e
      sum += 1
      puts e.message if i > N - 4
    end
  end
  puts sum
when 'raise'
  class P
    def div(a, b); a / b; end
    def go_div(b); div(10, b) + 1; end
  end
  p = P.new(1.0)
  s = 0
  N.times { |i| s += p.go_div(i < N / 2 ? 3 : 0) rescue (puts $!.class, $!.backtrace.first(3).map { |l| l.sub(/^.*\//, '') }; break) }
  puts s
when 'caller'
  class P
    def where(a); (a > 1.0 && caller(0, 2).size == 2) ? 1 : 0; end
    def go_where(a); where(a) + 1; end
  end
  p = P.new(1.0)
  s = 0
  N.times { |i| s += p.go_where(i.to_f) }
  puts s
  puts p.go_where(2.0)
when 'method'
  class P
    def named(a); __method__ == :named ? a : -a; end
    def go_named(a); named(a) + 1.0; end
  end
  p = P.new(1.0)
  s = 0.0
  N.times { |i| s += p.go_named(i.to_f) }
  puts s
when 'recursion'
  class P
    def down(a); a < 0.0 ? 0.0 : down(a - 1.0) + 1.0; end
    def go_down(a); down(a) + 1.0; end
  end
  p = P.new(1.0)
  s = 0.0
  N.times { |i| s += p.go_down((i % 5).to_f) }
  puts s
  begin
    p.go_down(1.0e9)
  rescue SystemStackError => e
    puts e.class
  end
when 'explicit'
  q = P.new(1.0)
  s = 0
  N.times { |i|
    P.class_eval { def cmp(a, b); 9; end } if i == N / 2
    s += q.go_on(q, i.to_f)
  }
  puts s
when 'explicit_subclass'
  Q.class_eval { def cmp(a, b); 8; end }
  q = P.new(1.0)
  s = 0
  N.times { |i| s += q.go_on(i < N / 2 ? q : Q.new(1.0), i.to_f) }
  puts s
when 'explicit_private', 'explicit_protected', 'explicit_missing'
  q = P.new(1.0)
  s = 0
  N.times { |i|
    if i == N / 2
      case c
      when 'explicit_private' then P.send(:private, :cmp)
      when 'explicit_protected' then P.send(:protected, :cmp)
      else
        P.class_eval { def method_missing(n, *a); n == :cmp ? 70 : super; end }
        P.send(:remove_method, :cmp)
      end
    end
    begin
      s += q.go_on(q, i.to_f)
    rescue NoMethodError => e
      s += 1000
      puts e.message if i == N - 1
    end
  }
  puts s
end
