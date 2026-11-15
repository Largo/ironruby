# Enumerator#next / #peek over each_slice / each_cons: compare the output with CRuby's.
def drain(e)
  out = []
  loop { out << e.peek; out << e.next }
  out << :stop
  begin; e.next; rescue StopIteration => x; out << (x.result.is_a?(Struct) ? :struct : x.result.class) << x.cause.class; end
  out
end
p drain([1, 2, 3, 4, 5].each_slice(2))
p drain([1, 2, 3, 4, 5].each_cons(2))
p drain([1, 2].each_cons(3))
p drain([].each_slice(3))
p drain((1..7).each_slice(3))
p drain((1...7).each_cons(3))
p drain({a: 1, b: 2, c: 3}.each_slice(2))
p drain(Struct.new(:x, :y, :z).new(1, 2, 3).each_cons(2))
e = [1, 2, 3, 4].each_slice(2); a = e.next; a << :mut; p e.next, a
e = [1, 2, 3].each_cons(2); x = e.next; x << 9; p e.next
arr = [1, 2, 3]; e = arr.each_slice(2); e.next; arr << 4 << 5; p drain(e)
e = [1, 2, 3].each_slice(2); e.next; e.rewind; p e.next
p (1..).each_slice(2).next
p (1..Float::INFINITY).each_slice(2).first(2)
class Array; def each_slice(n); return to_enum(:each_slice, n) unless block_given?; yield :redef; self; end; end
p [1, 2].each_slice(1).next
class Array; remove_method :each_slice; end
class Foo < Array; def each; yield 42; end; end
p Foo[1, 2].each_slice(1).next
e = [1, 2, 3].each_slice(2); p e.next; p e.size
p [1, 2, 3].lazy.each_slice(2).next
p [1, 2, 3].each_slice(2).with_index.next
