# Enumerator::Lazy over a plain #lazy: compare the output with CRuby's.
p (1..3).lazy.each { }
p [1, 2].lazy.each { }
p({a: 1}.lazy.each { })
r = []; {a: 1, b: 2}.lazy.each { |k, v| r << [k, v] }; p r
r = []; {a: 1}.lazy.each { |*a| r << a }; p r
r = []; [[1, 2], [3]].lazy.each { |a, b| r << [a, b] }; p r
r = []; [[1, 2]].lazy.each(&->(a) { r << a }); p r
begin; r = []; [[1, 2]].lazy.each(&->(a, b) { r << [a, b] }); p r; rescue => e; p e.class; end
o = Object.new; def o.each; yield 1, 2; yield 3; :ret; end; o.extend(Enumerable)
r = []; p(o.lazy.each { |*a| r << a }); p r
r = []; o.lazy.each { |a| r << a }; p r
p o.lazy.map { |a, b| [a, b] }.to_a
p o.lazy.to_a, o.lazy.force, o.lazy.first(2)
p (1..Float::INFINITY).lazy.map { _1 * 2 }.select(&:even?).first(3)
p [3, 1, 2].lazy.sort, [1, 2, 3].lazy.each_slice(2).to_a
x = (1..3).lazy; p x.each.class, x.each { break 7 }
p [1, 2, 3].lazy.zip([4, 5, 6]).to_a, (1..4).lazy.with_index.map { |a, i| a * i }.to_a
e = (1..3).lazy; p e.next, e.next, e.size
p (1..3).lazy.eager.map { _1 + 1 }
def o.each(*a); yield a; :r; end
p o.lazy.each(5, 6) { |*z| p z }
