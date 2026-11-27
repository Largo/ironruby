# Differential matrix for Array#take, which is Array's own in CRuby (rb_ary_take: the count through
# NUM2LONG, a negative one refused, then rb_ary_subseq) rather than Enumerable's walk with #each.
# Run it under CRuby and under ./ir.sh, and diff:
#
#   ruby Util/array-take-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/array-take-matrix.rb > /tmp/ir.txt 2>&1
#   diff /tmp/mri.txt /tmp/ir.txt

def t(label)
  print label.ljust(46), ' => '
  begin
    puts yield.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message}"
  end
end

A = [1, 2, 3, 4]

class Sub < Array; end
class OwnEach < Array
  def each = raise("each called")
end
class OwnSize < Array
  def size = 99
  def length = 99
  def [](*) = :own
end
class ToInt
  def to_int = 2
end
class BadToInt
  def to_int = "2"
end

t('take(2)') { A.take(2) }
t('take(0)') { A.take(0) }
t('take(4), take(9)') { [A.take(4), A.take(9)] }
t('empty take(3)') { [].take(3) }
t('take(-1)') { A.take(-1) }
t('take(-1) of an empty one') { [].take(-1) }
t('take(2.9)') { A.take(2.9) }
t('take(-0.5)') { A.take(-0.5) }
t('take(nil)') { A.take(nil) }
t('take("2")') { A.take("2") }
t('take(:a)') { A.take(:a) }
t('take(to_int)') { A.take(ToInt.new) }
t('take(bad to_int)') { A.take(BadToInt.new) }
t('take(BasicObject)') { A.take(BasicObject.new) }
t('take(2**40)') { A.take(2**40) }
t('take(2**64)') { A.take(2**64) }
t('take(-2**64)') { A.take(-2**64) }
t('take(Float::INFINITY)') { A.take(Float::INFINITY) }
t('take(Float::NAN)') { A.take(Float::NAN) }
t('take(2r)') { A.take(2r) }
t('take()') { A.take }
t('take(1, 2)') { A.take(1, 2) }
t('take with a block') { A.take(2) { :ignored } }
t('a new Array') { r = A.take(4); [r.equal?(A), r.class] }
t('a change to it stays its own') { r = A.take(2); r << 9; [r, A] }
t('a frozen one') { f = [1, 2, 3].freeze; r = f.take(2); [r, r.frozen?] }
t('a subclass') { r = Sub[1, 2, 3].take(2); [r, r.class] }
t('a subclass with its own #each') { OwnEach[1, 2, 3].take(2) }
t('a subclass with its own #size, #[]') { OwnSize[1, 2, 3].take(2) }
t('nil elements') { [nil, nil, 1].take(2) }
t('owner, arity, parameters') { m = A.method(:take); [m.owner, m.arity, m.parameters] }
t('backtrace label') { A.take(-1) rescue $!.backtrace_locations.first(2).map(&:label) }
t('Enumerable#take of a Range') { (1..9).take(3) }
t('Enumerable#take of a Hash') { { a: 1, b: 2 }.take(1) }
t('a redefined Enumerable#take') do
  module Enumerable; alias __saved_take__ take; def take(n) = :enum; end
  begin
    [A.take(1), (1..3).take(1)]
  ensure
    module Enumerable; alias take __saved_take__; remove_method :__saved_take__; end
  end
end
t('a redefined Array#each') do
  class Array; alias __saved_each__ each; def each = raise("each"); end
  begin
    A.take(2)
  ensure
    class Array; alias each __saved_each__; remove_method :__saved_each__; end
  end
end
