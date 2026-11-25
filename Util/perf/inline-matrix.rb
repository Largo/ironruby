# Differential cases for the method JIT's inlining of small pure methods (JitCompiler.Inline.cs):
# a method calling a pure Float/Integer method on self, or on an object whose class the profile saw,
# run hot, with what must invalidate the inlined call happening in the middle.
#
# One case per process, since one case's invalidation would change what the next one compiles:
#   for c in $(ruby Util/perf/inline-matrix.rb list); do echo "== $c"; ruby Util/perf/inline-matrix.rb $c 2>&1; done > cruby.txt
#   (the same with IR_CONFIG=Release ./ir.sh, and with ./ir.sh -X:NoJIT -X:NoOSR) and compare: "same" is identical output.
# IR_JIT_VERBOSE=1 shows `inlined P#cmp into go' (or `into go_on') for each case: the path is reached.
# The tree_ cases are CD's red-black tree lookup: `key.compare_to(current.key)' on a parameter (V#compare_to,
# which inlines V#compare_numbers itself) and attribute reads of a local the `while' loop walks; IR_JIT_VERBOSE=1
# shows `inlined V#compare_to into find_node' and `specialized T/find_node'.

CASES = %w[redefine subclass singleton private protected refine prepend remove_method undef_method
           argtypes raise caller method recursion explicit explicit_subclass
           explicit_private explicit_protected explicit_missing
           tree tree_redefine tree_redefine_inner tree_subclass_key tree_singleton_key tree_private tree_protected
           tree_missing tree_refine tree_other_key tree_nil_key tree_int_key tree_sym_key tree_poly tree_poly3
           tree_attr_redefine tree_attr_subclass tree_attr_singleton tree_struct_node tree_nil_node_key tree_cycle
           tree_root_nil tree_thread tree_ret_nil tree_ret_float tree_ret_bignum tree_raise tree_caller tree_frozen
           tree_deep tree_trace]

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
when /\Atree/
  class V
    attr_reader :x, :y
    def initialize(x, y); @x = x; @y = y; end
    def compare_to(other)
      result = compare_numbers(@x, other.x)
      return result unless result == 0
      compare_numbers(@y, other.y)
    end
    def compare_numbers(a, b)
      return  0 if a == b
      return -1 if a < b
      return  1 if a > b
      return 1 if a == a
      -1
    end
  end
  class VSub < V
  end
  # a key of another class, as CD's CallSign
  class K
    attr_reader :value
    def initialize(v); @value = v; end
    def compare_to(other)
      value == other.value ? 0 : (value < other.value ? -1 : 1)
    end
  end
  class TNode
    attr_reader :key
    attr_accessor :left, :right, :value
    def initialize(k, v); @key = k; @value = v; @left = nil; @right = nil; end
  end
  class TNodeSub < TNode
    def left; @right; end
  end
  class T
    attr_accessor :root
    def initialize; @root = nil; end
    def insert(k, v)
      n = TNode.new(k, v)
      return @root = n unless @root
      c = @root
      while true
        if k.compare_to(c.key) < 0
          return c.left = n unless c.left
          c = c.left
        else
          return c.right = n unless c.right
          c = c.right
        end
      end
    end
    def find_node(key)
      current = @root
      while current
        comparison_result = key.compare_to(current.key)
        return current if comparison_result == 0

        if comparison_result < 0
          current = current.left
        else
          current = current.right
        end
      end
      nil
    end
  end
  def tree_of(keys)
    t = T.new
    keys.each_with_index { |k, i| t.insert(k, i) }
    t
  end
  def tree_keys(n, cls = V)
    (0...n).map { |i| cls.new(((i * 7919) % 1009).to_f, (i % 7).to_f) }
  end
  # n lookups, half present, mid.call(i) once in the middle; the sum of the values found (-1 for none)
  def tree_run(t, keys, n, mid = nil)
    s = 0
    n.times do |i|
      mid.call(i) if mid && i == n / 2
      k = keys[i % keys.size]
      k = V.new(k.x + 0.5, k.y) if i % 4 == 3 && k.is_a?(V)
      begin
        r = t.find_node(k)
        s += r ? r.value : -1
      rescue NoMethodError, ArgumentError, TypeError => e
        s += 1000
        puts e.message.sub(/ for (an instance of )?#<.*>\z/, '') if i == n - 1
      end
    end
    s
  end
  keys = tree_keys(200)
  t = tree_of(keys)
  probe = keys.map { |k| V.new(k.x, k.y) }
  case c
  when 'tree'
    puts tree_run(t, probe, N)
  when 'tree_redefine'
    puts tree_run(t, probe, N, ->(i) { V.class_eval { def compare_to(o); 0; end } })
  when 'tree_redefine_inner'
    puts tree_run(t, probe, N, ->(i) { V.class_eval { def compare_numbers(a, b); b <=> a; end } })
  when 'tree_subclass_key'
    VSub.class_eval { def compare_to(o); -1; end }
    sub = probe.map { |k| VSub.new(k.x, k.y) }
    puts tree_run(t, probe, N / 2) + tree_run(t, sub, N / 2)
  when 'tree_singleton_key'
    puts tree_run(t, probe, N, ->(i) { def (probe[8]).compare_to(o); 1; end })
  when 'tree_private'
    puts tree_run(t, probe, N, ->(i) { V.send(:private, :compare_to) })
  when 'tree_protected'
    puts tree_run(t, probe, N, ->(i) { V.send(:protected, :compare_to) })
  when 'tree_missing'
    V.class_eval { def method_missing(n, *a); n == :compare_to ? 1 : super; end }
    puts tree_run(t, probe, N, ->(i) { V.send(:remove_method, :compare_to) })
  when 'tree_refine'
    module TR
      refine V do
        def compare_to(o); 0; end
      end
    end
    puts tree_run(t, probe, N)
    using TR
    puts V.new(1.0, 1.0).compare_to(V.new(2.0, 2.0))
    puts tree_run(t, probe, N)
  when 'tree_other_key'
    kt = tree_of((0...200).map { |i| K.new((i * 7919) % 1009) })
    kp = (0...200).map { |i| K.new((i * 7919) % 1009) }
    puts tree_run(t, probe, N / 2) + tree_run(kt, kp, N / 2) + tree_run(t, probe, N / 2)
  when 'tree_nil_key', 'tree_int_key', 'tree_sym_key'
    # (an Integer key differs from CRuby on every tree and without the JIT: IronRuby's .NET interop answers
    # `42.compare_to(o)' with Int32#CompareTo, an ArgumentError, where CRuby has no such method)
    odd = { 'tree_nil_key' => nil, 'tree_int_key' => 42, 'tree_sym_key' => :k }[c]
    puts tree_run(t, probe, N, ->(i) { probe[(N - 1) % probe.size] = odd })
  when 'tree_poly', 'tree_poly3'
    kt = tree_of((0...200).map { |i| K.new((i * 7919) % 1009) })
    kp = (0...200).map { |i| K.new((i * 7919) % 1009) }
    s = 0
    8.times { |j| s += tree_run(t, probe, 300) + tree_run(kt, kp, 300) }
    if c == 'tree_poly3'
      # a third class, whose compare_to says equal
      third = Class.new { def compare_to(o); 0; end }
      s += tree_run(t, [third.new], 500)
    end
    s += tree_run(t, probe, 600) + tree_run(kt, kp, 600)
    puts s
  when 'tree_attr_redefine'
    puts tree_run(t, probe, N, ->(i) { TNode.class_eval { def left; @right; end } })
  when 'tree_attr_subclass'
    puts tree_run(t, probe, N, ->(i) {
      mid = t.root.right.left.right
      sub = TNodeSub.new(mid.key, mid.value)
      sub.left, sub.right = mid.left, mid.right
      t.root.right.left.right = sub
    })
  when 'tree_attr_singleton'
    puts tree_run(t, probe, N, ->(i) { def (t.root.right.left).right; nil; end })
  when 'tree_struct_node'
    sn = Struct.new(:key, :value, :left, :right)
    puts tree_run(t, probe, N, ->(i) {
      mid = t.root.right
      t.root.right = sn.new(mid.key, mid.value, mid.left, mid.right)
    })
  when 'tree_nil_node_key'
    puts tree_run(t, probe, N, ->(i) { t.root.right.left.instance_variable_set(:@key, nil) })
  when 'tree_cycle'
    class T
      def find_bounded(key)
        current = @root
        steps = 0
        while current
          steps += 1
          break if steps > 1000
          comparison_result = key.compare_to(current.key)
          return current if comparison_result == 0
          if comparison_result < 0
            current = current.left
          else
            current = current.right
          end
        end
        steps
      end
    end
    s = 0
    N.times do |i|
      t.root.right.left.left = t.root if i == N / 2
      r = t.find_bounded(V.new(0.5, 0.0))
      s += r.is_a?(Integer) ? r : r.value
    end
    puts s
  when 'tree_root_nil'
    puts tree_run(t, probe, N, ->(i) { t.root = nil })
  when 'tree_thread'
    other_root = tree_of(tree_keys(50)).root
    full_root = t.root
    flipper_done = false
    flipper = Thread.new do
      flip_count = 0
      until flipper_done
        t.root = flip_count.even? ? other_root : full_root
        flip_count += 1
        Thread.pass
      end
    end
    s = tree_run(t, probe, N)
    flipper_done = true
    flipper.join
    t.root = full_root
    # the sum depends on the interleaving; what must hold is that every lookup found a node of either tree or none
    puts s.is_a?(Integer), tree_run(t, probe, 200)
  when 'tree_ret_nil', 'tree_ret_float', 'tree_ret_bignum'
    ret = { 'tree_ret_nil' => 'nil', 'tree_ret_float' => '-0.5', 'tree_ret_bignum' => '-(2**70)' }[c]
    puts tree_run(t, probe, N, ->(i) { V.class_eval "def compare_to(o); x == o.x ? compare_numbers(@y, o.y) : #{ret}; end" })
  when 'tree_raise'
    s = 0
    N.times do |i|
      V.class_eval { def compare_numbers(a, b); raise ArgumentError, "boom" if a == 3.0; a <=> b; end } if i == N / 2
      begin
        r = t.find_node(probe[i % probe.size])
        s += r ? r.value : -1
      rescue ArgumentError => e
        puts e.message, e.backtrace.first(4).map { |l| l.sub(/^.*\//, '') } if s >= 0
        s = -s - 1
      end
    end
    puts s
  when 'tree_caller'
    V.class_eval { def compare_to(o); $depth = caller.size; x <=> o.x; end }
    puts tree_run(t, probe, N), caller.size, $depth - caller.size
  when 'tree_frozen'
    puts tree_run(t, probe, N, ->(i) {
      t.freeze
      stack = [t.root]
      while (n = stack.pop)
        n.freeze
        stack << n.left if n.left
        stack << n.right if n.right
      end
    })
  when 'tree_deep'
    deep = T.new
    chain = (0...20000).map { |i| TNode.new(V.new(i.to_f, 0.0), i) }
    chain.each_cons(2) { |a, b| a.right = b }
    deep.root = chain.first
    s = 0
    200.times { |i| s += deep.find_node(V.new((19999 - i).to_f, 0.0)).value }
    puts s
  when 'tree_trace'
    events = 0
    tp = TracePoint.new(:call, :return) { |e| events += 1 if e.method_id == :compare_to || e.method_id == :find_node }
    s = tree_run(t, probe, N, ->(i) { tp.enable })
    tp.disable
    puts s, events
  end
end
