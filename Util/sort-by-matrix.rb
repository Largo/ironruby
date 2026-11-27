# Differential matrix for Enumerable#sort_by and the count forms of min_by and max_by on a receiver
# that is not a plain Array (a Hash, a Range, a Struct, an Enumerator, a class of its own that
# includes Enumerable), where the keys come from a walk with #each and the elements are sorted by
# them. Run it under CRuby and under ./ir.sh, and diff:
#
#   ruby Util/sort-by-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/sort-by-matrix.rb > /tmp/ir.txt 2>&1
#   diff /tmp/mri.txt /tmp/ir.txt

def t(label)
  print label.ljust(46), ' => '
  begin
    puts yield.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message.sub(/0x\h+/, '0x')}"
  end
end

H = { a: 3, b: 1, c: 2, d: 1 }

class Multi
  include Enumerable
  def each
    yield 1, :x
    yield 3
    yield
    yield [2, :y]
    yield 0, :z, :w
  end
end

class Pairs
  include Enumerable
  def initialize(*a) = @a = a
  def each(&b) = @a.each(&b)
end

class OddKey
  attr_reader :v
  def initialize(v) = @v = v
  def <=>(o) = (v <=> o.v).to_f
  def inspect = "K#{v}"
end

class BigCmp
  attr_reader :v
  def initialize(v) = @v = v
  def <=>(o) = (v <=> o.v) * 2**70
end

class StrCmp
  def <=>(_) = "x"
end

S = Struct.new(:a, :b, :c)

# ---------------------------------------------------------------- 1. the forms
t('hash |k, v|') { H.sort_by { |k, v| -v } }
t('hash |pair|') { H.sort_by { |pair| pair[1] } }
t('hash |k|') { H.sort_by { |k| k.to_s } }
t('hash ties keep order') { H.sort_by { |_k, v| v } }
t('hash lambda (k, v)') { H.sort_by(&->(k, v) { v }) }
t('hash lambda (pair)') { H.sort_by(&->(pair) { pair[1] }) }
t('hash &:last') { H.sort_by(&:last) }
t('hash proc (*a)') { H.sort_by(&proc { |*a| a.inspect }) }
t('hash empty') { {}.sort_by { |k, v| v } }
t('hash one') { { z: 1 }.sort_by { |k, v| v } }
t('hash frozen') { H.dup.freeze.sort_by { |k, v| v } }
t('hash no block') { e = H.sort_by; [e.class, e.size, e.each { |k, v| v }] }
t('range') { (1..6).sort_by { |x| [x % 3, -x] } }
t('range endless, break') { (1..).sort_by { |x| break x if x > 3 } }
t('struct') { S.new(3, 1, 2).sort_by { |x| x } }
t('enumerator with index') { %w[b c a].each_with_index.sort_by { |s, i| s } }
t('enumerator with_index |pair|') { %w[b c a].each_with_index.sort_by { |pair| -pair[1] } }
t('several values per yield |*a|') { Multi.new.sort_by { |*a| a.size } }
t('several values per yield |a|') { Multi.new.sort_by { |a| a.inspect } }
t('several values per yield |a, b|') { Multi.new.sort_by { |a, b| b.to_s } }
t('own Enumerable') { Pairs.new(3, 1, 2).sort_by { |x| -x } }
t('own Enumerable, 40 elements') { Pairs.new(*(1..40).to_a.shuffle(random: Random.new(1))).sort_by { |x| x % 7 } }
t('a Set') { require 'set'; Set[3, 1, 2].sort_by { |x| x } }
t('the result is a new Array') { r = H.sort_by { |k, v| v }; [r.class, r.frozen?] }

# ---------------------------------------------------------------- 2. the keys
t('keys that do not compare') { Pairs.new(1, 2).sort_by { |x| x == 1 ? 1 : "a" } }
t('keys that do not compare, 3') { Pairs.new(1, 2, 3).sort_by { |x| x == 2 ? :s : x } }
t('nil keys') { Pairs.new(1, 2).sort_by { nil } }
t('one nil key') { Pairs.new(1).sort_by { nil } }
t('Float keys, NaN') { Pairs.new(1, 2, 3).sort_by { |x| x == 2 ? Float::NAN : x.to_f } }
t('<=> answering a Float') { Pairs.new(3, 1, 2).sort_by { |x| OddKey.new(x) } }
t('<=> answering a Bignum') { Pairs.new(3, 1, 2).sort_by { |x| BigCmp.new(x) }.map(&:itself) }
t('<=> answering a String') { Pairs.new(3, 1).sort_by { |x| StrCmp.new } }
t('Array keys') { H.sort_by { |k, v| [v, k] } }
t('String keys') { H.sort_by { |k, v| k.to_s * v } }
t('mixed Integer and Float keys') { Pairs.new(1, 2, 3, 4).sort_by { |x| x.odd? ? x : x - 0.5 } }
t('Bignum keys') { Pairs.new(1, 2, 3).sort_by { |x| -x * 2**80 } }

# ---------------------------------------------------------------- 3. the block
t('break') { H.sort_by { |k, v| break :out if v == 1 } }
t('next') { H.sort_by { |k, v| next -v } }
t('raise') { H.sort_by { |k, v| raise "boom #{k}" } }
t('throw') { catch(:t) { H.sort_by { |k, v| throw :t, k } } }
t('the block sees the walk in order') { seen = []; Pairs.new(3, 1, 2).sort_by { |x| seen << x; x }; seen }
t('the hash grows under the block') { h = { a: 1 }; begin; h.sort_by { |k, v| h[:"x#{v}"] = v + 1; v }; rescue => e; e.class; end }
t('backtrace frames') do
  H.sort_by { |k, v| raise "x" }
rescue => e
  e.backtrace_locations.first(4).map(&:label)
end
t('backtrace frames, own Enumerable') do
  Pairs.new(1, 2).sort_by { raise "x" }
rescue => e
  e.backtrace_locations.first(4).map(&:label)
end
t('a redefined Array#sort_by does not count') do
  class Array; alias __saved_sort_by__ sort_by; def sort_by = :redefined; end
  begin
    [H.sort_by { |k, v| v }, Pairs.new(2, 1).sort_by { |x| x }, H.min_by(2) { |k, v| v }]
  ensure
    class Array; alias sort_by __saved_sort_by__; remove_method :__saved_sort_by__; end
  end
end
t('a redefined Array#<< does not count') do
  class Array; alias __saved_push__ <<; def <<(x) = raise("no"); end
  begin
    H.sort_by { |k, v| v }
  ensure
    class Array; alias << __saved_push__; remove_method :__saved_push__; end
  end
end
t('a redefined Integer#<=>') do
  class Integer; alias __saved_cmp__ <=>; def <=>(o) = -(__saved_cmp__(o) || 0); end
  begin
    [H.sort_by { |k, v| v }, Pairs.new(1, 3, 2).sort_by { |x| x }]
  ensure
    class Integer; alias <=> __saved_cmp__; remove_method :__saved_cmp__; end
  end
end

# ---------------------------------------------------------------- 4. min_by(n), max_by(n)
t('hash min_by(2)') { H.min_by(2) { |k, v| v } }
t('hash max_by(2)') { H.max_by(2) { |k, v| v } }
t('hash min_by(0), max_by(9)') { [H.min_by(0) { |k, v| v }, H.max_by(9) { |k, v| v }] }
t('range min_by(2)') { (1..5).min_by(2) { |x| -x } }
t('own max_by(2), ties') { Pairs.new(1, 2, 3, 4).max_by(2) { |x| x % 2 } }
t('min_by(-1)') { H.min_by(-1) { |k, v| v } }
t('max_by(2) keys that do not compare') { Pairs.new(1, 2).max_by(2) { |x| x == 1 ? 1 : "a" } }

# ---------------------------------------------------------------- 5. the method
m = Enumerable.instance_method(:sort_by)
t('arity, parameters, owner') { [m.arity, m.parameters, m.owner, H.method(:sort_by).owner] }
t('min_by arity, parameters') { [Enumerable.instance_method(:min_by).arity, Enumerable.instance_method(:min_by).parameters] }
