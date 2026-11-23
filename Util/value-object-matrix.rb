# Expression matrix for the value-object surface: Data, Struct, Range, Set,
# Comparable and Object's own protocol (dup/clone/freeze/instance_variable_*).
#
# Run the same file through CRuby and through IronRuby and diff the output:
#
#   ruby    Util/value-object-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/value-object-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt | grep -c '^<'
#
# Every line is "<source> => <result>" or "<source> !! <ExceptionClass>: <message>",
# so a diff points straight at the expression that behaves differently.

require "set"

PRELUDE = <<~'RUBY'
  M = Data.define(:amount, :unit)
  S1 = Data.define(:value)
  E0 = Data.define
  class MNamed < M
    def self.name; "A"; end
  end
  SUB = Class.new(M) { def initialize(amount:, unit:); super; end }
  OVER = Data.define(:amount, :unit) do
    def initialize(*rest, **kw); super; $trace = [:initialize, rest, kw]; end
  end
  AREA = Data.define(:w, :h, :area) do
    def initialize(w:, h:); super(w: w, h: h, area: w * h); end
  end
  STR = Struct.new(:a, :b)
  KWSTR = Struct.new(:a, :b, keyword_init: true)
RUBY

EXPRESSIONS = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  # --- Data.define
  Data.define.members
  Data.define(:a, :b).members
  Data.define("a", :b, "c").members
  (Data.define(:a, :a) rescue "#{$!.class}: #{$!.message}")
  (Data.define(1) rescue "#{$!.class}: #{$!.message}")
  Data.respond_to?(:members)
  Class.new(Data).respond_to?(:members)
  M.respond_to?(:members)
  Class.new(M).members
  Data.define(:a).superclass
  Data.define(:a).new(1).class.ancestors.include?(Data)
  Data.instance_methods(false).sort
  Data.singleton_methods(false).sort
  Data.define(:a).instance_methods(false).sort
  Data.define(:a).singleton_methods(false).sort

  # --- Data.new argument handling
  M.new(1, "m").to_h
  M.new(amount: 1, unit: "m").to_h
  M[1, "m"].to_h
  M[amount: 1, unit: "m"].to_h
  M.new("amount" => 1, "unit" => "m").to_h
  S1.new("value" => -1, value: 42).value
  S1.new(42, **{}).value
  M.new(1, "m", **{}).to_h
  E0.new.frozen?
  M.new(1, "m").frozen?
  (M.new rescue "#{$!.class}: #{$!.message}")
  (M.new(unit: "m") rescue "#{$!.class}: #{$!.message}")
  (M.new(unit: "km", "unit" => "km") rescue "#{$!.class}: #{$!.message}")
  (M.new(amount: 1, unit: "m", system: "x") rescue "#{$!.class}: #{$!.message}")
  (M.new(1 => 2) rescue "#{$!.class}: #{$!.message}")
  (M.new(1, 2, 3) rescue "#{$!.class}: #{$!.message}")
  (M.new([] => 1) rescue "#{$!.class}: #{$!.message}")
  SUB.new(amount: 1, unit: "km").to_h
  AREA.new(w: 2, h: 3).area
  ($trace = nil; OVER.new(42, "m"); $trace)
  ($trace = nil; OVER.new(amount: 42, unit: "m"); $trace)
  ($trace = nil; OVER[42, "m"]; $trace)
  ($trace = nil; OVER.with_defaults rescue "#{$!.class}")
  (k = Data.define(:amount) { def initialize(amount:, &b); super(amount: b.call(amount)); end }; k.new(1) { |a| a * 2 }.amount)
  (k = Data.define(:amount) { def initialize(amount:, &b); super(amount: b.call(amount)); end }; k[amount: 1] { |a| a * 2 }.amount)
  (d = AREA.allocate; Data.instance_method(:initialize).bind_call(d, w: 2, h: 3, area: 6); d.to_h)
  ($p = []; c = Data.define(:a) { def initialize(*, **); super; $p << :init; end }; c.new(1); $p)
  M.new(1, "m").instance_variables.empty?
  M.new(amount: 1, "amount" => 2, unit: "m").to_h
  M.new(unit: "m", amount: 1).to_h
  (M.new(1, "m").__send__(:initialize, amount: 2, unit: "x") rescue "#{$!.class}: #{$!.message}")
  (M.new(amount: 1, unit: "m", amount2: 3, x: 4) rescue "#{$!.class}: #{$!.message}")
  (M.new(unit: "m", zz: 1) rescue "#{$!.class}: #{$!.message}")
  (c = Data.define(:a, :b) { def a = 99 }; c.new(1, 2).with(b: 3).to_h)
  (c = Data.define(:a, :b) { def a = 99 }; c.new(1, 2).with(b: 3).a)
  M.new(1, "m").with(amount: 1, "amount" => 2).to_h
  M.new(1, "m").with(unit: "x", amount: 5).to_h
  (M.new(1, "m").with(amount: 2, zz: 1, yy: 2) rescue "#{$!.class}: #{$!.message}")
  (d = M.new(1, "m").with(amount: 3); [d.frozen?, d.class, d.instance_variables])
  ($trace = nil; OVER.new(1, "m").with(unit: "x"); $trace)
  ($trace = nil; OVER.new(1, "m").with("unit" => "x"); $trace)
  (AREA.new(w: 2, h: 3).with(w: 5) rescue "#{$!.class}: #{$!.message}")

  # --- Data instance protocol
  M.new(1, "m").members
  M.new(1, "m").to_h
  M.new(1, "m").deconstruct
  M.new(1, "m").deconstruct_keys([:amount])
  M.new(1, "m").deconstruct_keys([:amount, :unit])
  M.new(1, "m").deconstruct_keys(nil)
  M.new(1, "m").deconstruct_keys([])
  M.new(1, "m").deconstruct_keys(["amount", "unit"])
  M.new(1, "m").deconstruct_keys([:amount, :unit, :amount])
  M.new(1, "m").deconstruct_keys([:zz, :amount])
  M.new(1, "m").deconstruct_keys([:amount, :zz])
  (M.new(1, "m").deconstruct_keys([0, 1]) rescue "#{$!.class}: #{$!.message}")
  (M.new(1, "m").deconstruct_keys([0, []]) rescue "#{$!.class}: #{$!.message}")
  (M.new(1, "m").deconstruct_keys("x") rescue "#{$!.class}: #{$!.message}")
  (M.new(1, "m").deconstruct_keys(1) rescue "#{$!.class}: #{$!.message}")
  (M.new(1, "m").deconstruct_keys({}) rescue "#{$!.class}: #{$!.message}")
  (M.new(1, "m").deconstruct_keys rescue "#{$!.class}")
  M.new(1, "m").with.equal?(nil)
  (d = M.new(1, "m"); d.with.equal?(d))
  M.new(1, "m").with(amount: 4).to_h
  M.new(1, "m").with("amount" => 4, "unit" => "x").to_h
  (M.new(1, "m").with(4, "m") rescue "#{$!.class}: #{$!.message}")
  (M.new(1, "m").with(zz: 1) rescue "#{$!.class}: #{$!.message}")
  ($trace = nil; OVER.new(42, "m").with(amount: 0); $trace)
  (s = Class.new(M); d = s.new(amount: 1, unit: "k"); def s.new(*); raise "no"; end; d.with(unit: "m").to_h)
  M.new(1, "m") == M.new(1, "m")
  M.new(1, "m") == M.new(1, "x")
  M.new(1, "m").eql?(M.new(1, "m"))
  M.new(1, "m").eql?(M.new(1.0, "m"))
  M.new(1, "m").hash == M.new(1, "m").hash
  M.new(1, "m").hash.instance_of?(Integer)
  Data.define(:x).new(1).hash == Data.define(:x).new(1).hash
  M.new(1, "m").to_s
  M.new(1, "m").inspect
  Data.define(:a).new("").to_s
  (c = Class.new; c.class_eval("Foo = Data.define(:a)"); c::Foo.new("").to_s)
  (m = Module.new; m.class_eval("Foo = Data.define(:a)"); m::Foo.new("").to_s)
  MNamed.new(42, "km").to_s
  (k = Class.new(M) { def self.name; "A"; end }; k.new(42, "km").to_s)
  (a = M.allocate; a.send(:initialize, amount: 42, unit: a); a.to_s)
  (a = M.allocate; a.send(:initialize, amount: 42, unit: a); b = M.allocate; b.send(:initialize, amount: 42, unit: b); a == b)
  (a = M.allocate; a.send(:initialize, amount: a, unit: "km"); b = M.allocate; b.send(:initialize, amount: b, unit: "mi"); a == b)
  (a = M.allocate; a.send(:initialize, amount: a, unit: "km"); b = M.allocate; b.send(:initialize, amount: b, unit: "mi"); a.eql?(b))
  M.new(1, "m").to_h { |k, v| [v, k] }
  (M.new(1, "m").to_h { |k, v| [k, v, 1] } rescue "#{$!.class}: #{$!.message}")
  (M.new(1, "m").to_h { |k, v| [k] } rescue "#{$!.class}: #{$!.message}")
  (M.new(1, "m").to_h { |k, v| "x" } rescue "#{$!.class}: #{$!.message}")
  (M.new(1, "m").to_h { |k, v| 1 } rescue "#{$!.class}: #{$!.message}")
  Data.instance_method(:inspect) == Data.instance_method(:to_s)

  # --- Struct
  STR.new(1, 2).to_a
  STR.new(1).to_a
  (STR.new(1, 2, 3) rescue "#{$!.class}: #{$!.message}")
  STR.members
  STR.new(1, 2).members
  STR.new(1, 2).to_h
  STR.new(1, 2).to_h { |k, v| [v, k] }
  (STR.new(1, 2).to_h { |k, v| [k] } rescue "#{$!.class}: #{$!.message}")
  (STR.new(1, 2).to_h { |k, v| "x" } rescue "#{$!.class}: #{$!.message}")
  STR.new(1, 2).deconstruct_keys([:a])
  STR.new(1, 2).deconstruct_keys(nil)
  STR.new(1, 2).deconstruct_keys([:a, :b, :c])
  (STR.new(1, 2).deconstruct_keys("x") rescue "#{$!.class}: #{$!.message}")
  STR.new(1, 2).deconstruct_keys([0, 1])
  STR.new(1, 2).inspect
  (s = STR.new(1, nil); s.b = s; s.inspect)
  STR.new(1, 2).dig(:a)
  STR.new(1, 2).values_at(0, 1)
  STR.new(1, 2).values_at(0..1)
  (STR.new(1, 2).values_at(5) rescue "#{$!.class}")
  STR.new(1, 2).select { |x| x > 1 }
  STR.new(1, 2).size
  STR.new(1, 2) == STR.new(1, 2)
  STR.new(1, 2).eql?(STR.new(1, 2))
  STR.new(1, 2).hash == STR.new(1, 2).hash
  STR.new(1, 2).hash.instance_of?(Integer)
  KWSTR.new(a: 1, b: 2).to_a
  (KWSTR.new(1, 2) rescue "#{$!.class}")
  (Struct.new(:a) { def initialize(*); super; end }.new(1).a)
  Struct.new("Foo", :a).name
  Struct.new(:a).new(1).frozen?
  Struct.new(:a, :b).new(1, 2).each_pair.to_a
  Struct.new(:a).instance_method(:a).arity
  (Struct.new(:a, :a) rescue "#{$!.class}")
  (Struct.new(:a).new(1).a = 2) rescue "#{$!.class}"
  Struct.new(:a).ancestors.include?(Enumerable)
  STR.new(1, 2).filter_map { |x| x if x > 1 }
  (s = STR.new(1, 2); s.each_pair { |k, v| }; s.to_a)

  # --- Range basics
  (1..5).to_a
  (1...5).to_a
  ("a".."e").to_a
  (1..5).size
  (1...5).size
  (1.0..5.0).size
  (1..).size
  (1..5).count
  (1..5).sum
  (1..5).min
  (1..5).max
  (1...5).max
  (1..5).minmax
  (1..5).first(2)
  (1..5).last(2)
  (1..5).step(2).to_a
  (1..5) % 2
  ((1..5) % 2).to_a
  (1.0..2.0).step(0.5).to_a
  (1..5).each_slice(2).to_a
  (1..5).cover?(2)
  (1..5).cover?(2..3)
  (1..5).cover?(2...6)
  (1..5) === 3
  (1..5).include?(3)
  ("a".."z").include?("cc")
  ("a".."z").cover?("cc")
  (1..5).to_s
  (1..5).inspect
  (nil..nil).inspect
  (1..).inspect
  (..5).inspect
  (1..5).hash == (1..5).hash
  (1..5).eql?(1..5)
  (1..5) == (1..5)
  (1..5).frozen?
  Range.new(1, 5).frozen?
  (1..5).entries
  (1..5).reverse_each.to_a
  (1..5).each_entry.to_a
  (1..5).begin
  (1..5).end
  (1..5).exclude_end?
  (5..1).to_a
  (1..5).find_index(3)
  ((1..5).step(0) rescue "#{$!.class}")
  ((1..5).step(-1).to_a rescue "#{$!.class}")
  (Range.new(1, "a") rescue "#{$!.class}")
  (Range.new(nil, nil).to_a rescue "#{$!.class}")
  ((1..5).each.to_a)
  (("a".."e").step(2).to_a)
  ((1..10).bsearch { |x| x >= 4 })
  ((1.0..10.0).bsearch { |x| x >= 4 })
  (1..5).max(2)
  (1..5).min(2)
  (1..5).minmax_by { |x| -x }
  (1..0).size
  (1..5).percent_placeholder rescue "#{$!.class}"
  ((1..5).to_a.frozen?)
  ((1..5).dup.frozen?)
  ((1..5).clone.frozen?)
  (Range.instance_method(:inspect) == Range.instance_method(:to_s))

  # --- Comparable
  1.clamp(2, 3)
  5.clamp(1..3)
  (Class.new { include Comparable; def <=>(o); 0; end }.new == 1)
  (Class.new { include Comparable; def <=>(o); nil; end }.new < 1 rescue "#{$!.class}")
  Comparable.instance_methods.sort

  # --- Set
  Set[1, 2, 3].to_a.sort
  Set.new([1, 2]).inspect
  Set.new([1, 2]).to_s
  (Set[1, 2] | Set[3]).to_a.sort
  (Set[1, 2] & Set[2]).to_a
  (Set[1, 2] - Set[2]).to_a
  (Set[1, 2] ^ Set[2, 3]).to_a.sort
  Set[1, 2].subset?(Set[1, 2, 3])
  Set[1, 2].superset?(Set[1])
  Set[1, 2] <= Set[1, 2]
  Set[1, 2] < Set[1, 2]
  Set[1, 2].proper_subset?(Set[1, 2, 3])
  Set[1, 2].disjoint?(Set[3])
  Set[1, 2].intersect?(Set[2])
  Set[1, 2] == Set[2, 1]
  Set[1, 2].hash == Set[2, 1].hash
  Set[1, 2].frozen?
  Set[1, 2].freeze.frozen?
  Set[1, 2].map { |x| x * 2 }.sort
  Set[1, 2].to_set.equal?(nil)
  Set[1, 2].add?(1)
  Set[1, 2].add?(3).to_a.sort
  Set[1, 2].delete?(5)
  Set[1, 2].each.to_a.sort
  Set[Set[1]].flatten.to_a
  Set[1, 2].divide { |a, b| (a - b).abs == 1 }.size
  Set[1, 2].classify { |x| x.even? }.keys.sort_by(&:to_s)
  Set[1, 2].join(",")
  Set.new([1, 2]).compare_by_identity.size
  Set[1, 2] === 1
  Set.instance_method(:filter!) == Set.instance_method(:select!)
  Set.new([1, 2]).sum
  Set[1, 2].to_a.class
  Set.ancestors.include?(Enumerable)
  (Set[1, 2].merge([3]).to_a.sort)
  (Set[1, 2].subtract([2]).to_a)
  (Set[1] <=> Set[1, 2])
  (Set[1] <=> 1)

  # --- Object protocol
  (o = Object.new; o.instance_variable_set(:@a, 1); o.instance_variable_get(:@a))
  (o = Object.new; o.instance_variable_set(:@a, 1); o.instance_variables)
  (o = Object.new; o.instance_variable_defined?(:@a))
  (o = Object.new; o.instance_variable_get("a") rescue "#{$!.class}: #{$!.message}")
  (o = Object.new; o.instance_variable_get(:a) rescue "#{$!.class}: #{$!.message}")
  (o = Object.new; o.instance_variable_set(:a, 1) rescue "#{$!.class}: #{$!.message}")
  (o = Object.new; o.instance_variable_get(1) rescue "#{$!.class}")
  (o = Object.new; o.remove_instance_variable(:@a) rescue "#{$!.class}: #{$!.message}")
  (o = Object.new; o.instance_variable_set(:@a, 1); o.remove_instance_variable(:@a))
  1.itself
  1.then { |x| x + 1 }
  1.yield_self { |x| x + 1 }
  1.tap { |x| x + 1 }
  (1.then.class)
  Object.new.frozen?
  1.frozen?
  :a.frozen?
  "a".frozen?
  nil.frozen?
  (o = Object.new.freeze; o.instance_variable_set(:@a, 1) rescue "#{$!.class}")
  (o = Object.new; o.freeze; o.dup.frozen?)
  (o = Object.new; o.freeze; o.clone.frozen?)
  (o = Object.new; o.freeze; o.clone(freeze: false).frozen?)
  (o = Object.new; o.freeze; o.clone(freeze: true).frozen?)
  (o = Object.new; o.clone(freeze: 1) rescue "#{$!.class}")
  (o = Object.new; def o.foo; 1; end; o.dup.respond_to?(:foo))
  (o = Object.new; def o.foo; 1; end; o.clone.respond_to?(:foo))
  (o = Object.new; def o.foo; 1; end; o.singleton_methods)
  (o = Object.new; def o.foo; 1; end; o.singleton_method(:foo).arity)
  (o = Object.new; o.singleton_method(:foo) rescue "#{$!.class}")
  (o = Object.new; o.singleton_class.equal?(o.singleton_class))
  1.singleton_class rescue "#{$!.class}"
  nil.singleton_class
  (o = Object.new; o.define_singleton_method(:x) { 5 }; o.x)
  (o = Struct.new(:a).new(1); o.dup.a)
  (o = "x"; o.instance_variable_set(:@a, 1); o.dup.instance_variable_get(:@a))
  (o = "x"; o.instance_variable_set(:@a, 1); o.clone.instance_variable_get(:@a))
  (o = Object.new; o.extend(Module.new { def zz; 1; end }); o.zz)
  Object.new.methods.include?(:itself)
  Object.new.public_methods(false).class
  (o = Object.new; o.freeze; o.frozen?)
  (M.new(1, "m").dup.frozen?)
  (M.new(1, "m").clone.frozen?)
  (M.new(1, "m").clone(freeze: false).frozen?)
  ((1..2).clone(freeze: false).frozen?)

  # --- ObjectSpace
  ObjectSpace.respond_to?(:each_object)
  ObjectSpace.respond_to?(:garbage_collect)
  ObjectSpace.respond_to?(:define_finalizer)
  ObjectSpace.respond_to?(:undefine_finalizer)
  ObjectSpace.respond_to?(:count_objects)
  ObjectSpace::WeakMap.instance_methods(false).sort
  (ObjectSpace::WeakMap.new.size rescue "#{$!.class}")
  (w = ObjectSpace::WeakMap.new; k = Object.new; w[k] = "v"; w[k])
  (w = ObjectSpace::WeakMap.new; k = Object.new; w[k] = "v"; w.key?(k))
  (w = ObjectSpace::WeakMap.new; k = Object.new; w[k] = "v"; w.length)
  (defined?(ObjectSpace::WeakKeyMap) ? ObjectSpace::WeakKeyMap.instance_methods(false).sort : "missing")
  (ObjectSpace.count_objects.class rescue "#{$!.class}")
  (ObjectSpace.define_finalizer(Object.new, proc { }).class rescue "#{$!.class}")
  (ObjectSpace.define_finalizer(Object.new, 1) rescue "#{$!.class}")
  (ObjectSpace.undefine_finalizer(Object.new).class rescue "#{$!.class}")
  (ObjectSpace.each_object(Class).class rescue "#{$!.class}")
  (ObjectSpace.garbage_collect.class rescue "#{$!.class}")
  # --- Data.new / #with made directly (KernelOps.DataNew, DataWith) and when they are not
  M.new(1, "kg")
  M.new(amount: 1, unit: "kg").frozen?
  M.new("amount" => 1, "unit" => "kg")
  (M.new(1) rescue "#{$!.class}: #{$!.message}")
  (M.new(amount: 1) rescue "#{$!.class}: #{$!.message}")
  (M.new(amount: 1, unit: 2, x: 3) rescue "#{$!.class}: #{$!.message}")
  (M.new(1, 2, 3) rescue "#{$!.class}: #{$!.message}")
  (M.new(1, unit: 2) rescue "#{$!.class}: #{$!.message}")
  M.new(*[1, 2], **{})
  E0.new.frozen?
  E0.new.instance_variables
  M.new(1, 2).instance_variables
  SUB.new(1, 2)
  ($trace = nil; OVER.new(1, 2); $trace)
  ($trace = nil; OVER.new(1, 2).with(unit: 3); $trace)
  AREA.new(w: 2, h: 3)
  AREA.new(w: 2, h: 3).with(w: 4)
  (AREA.new(w: 2, h: 3).with(area: 1) rescue "#{$!.class}: #{$!.message}")
  M.new(1, 2).with(unit: 3)
  M.new(1, 2).with
  M.new(1, 2).with("unit" => 4)
  (M.new(1, 2).with(foo: 4) rescue "#{$!.class}: #{$!.message}")
  (M.new(1, 2).with(1) rescue "#{$!.class}: #{$!.message}")
  (M.new(1, 2).with({unit: 4}) rescue "#{$!.class}: #{$!.message}")
  (m = M.new(1, 2); m.with(unit: 3).equal?(m))
  (m = M.new(1, 2); m.with(amount: 1, unit: 2).equal?(m))
  Class.new(M).new(1, 2).class.superclass
  (c = Class.new(M); c.new(1, 2).with(unit: 5).class == c)
  (m = M.new(1, 2); m.singleton_class; m.with(unit: 3).singleton_methods)
  (o = Data.define(:a) { def self.allocate = raise("no") }; o.new(1).a)
  (o = Data.define(:a) { def freeze = raise("no") }; o.new(1).frozen?)
  (o = Data.define(:a); o.new(1).method(:a).arity)
  (o = Data.define(:a); o.instance_method(:a).parameters)
  (o = Data.define(:a); [o.public_method_defined?(:a), o.instance_method(:a).owner == o])
  (o = Data.define(:a) { def a = super * 2 }; o.new(4).a rescue $!.class)
  (o = Data.define(:a); m = o.new(5); m.instance_variable_get(:@a))
  # hot loops with the class changed half way: initialize, the reader, a prepended initialize
  (o = Data.define(:a); (1..300).map { |i| o.class_eval { def initialize(a:) = super(a: a * 10) } if i == 150; o.new(i).a }.sum)
  (o = Data.define(:a); (1..300).map { |i| o.class_eval { def initialize(**kw) = super(a: 0) } if i == 150; o.new(a: i).with(a: 1).a }.sum)
  (o = Data.define(:a); (1..300).map { |i| o.class_eval { def a = 7 } if i == 150; o.new(i).a }.sum)
  (o = Data.define(:a); (1..300).map { |i| o.prepend(Module.new { def initialize(a:) = super(a: -a) }) if i == 150; o.new(i).a }.sum)
  (o = Data.define(:a); (1..300).map { |i| o.class_eval { remove_method :a } if i == 150; (o.new(i).a rescue 1) }.sum)
  # --- Array#sum folding something that is not a number (Array#__sum_tail__)
  [M.new(1, 2), M.new(3, 4)].sum([])
  %w[a b c].sum("")
  [[1], [2]].sum([0])
  [1, 2].sum(Rational(1, 2)) { |x| x * 0.5 }
  ([1, "a"].sum(0) rescue "#{$!.class}: #{$!.message}")
  ([0.1] * 10).sum
  ([0.1] * 10).sum(0) { |x| x }
  (t = Struct.new(:v) { def +(o) = v + (o.respond_to?(:v) ? o.v : o) }; [t.new(1), 1e100, 1.0, -1e100].sum(0) rescue $!.class)
  (t = Struct.new(:v) { def +(o) = v + (o.respond_to?(:v) ? o.v : o) }; [t.new(1), 1e100, 1.0, -1e100].sum(0) { |x| x } rescue $!.class)
  (t = Struct.new(:v) { def +(o) = v + (o.respond_to?(:v) ? o.v : o) }; [3, t.new(1), 0.1, t.new(2), 0.2, 0.3].sum(0) rescue $!.class)
  (t = Struct.new(:v) { def +(o) = self.class.new(v + (o.respond_to?(:v) ? o.v : o)) }; [1, 2, 0.5].sum(t.new(0)).v)
  (t = Struct.new(:v) { def +(o) = self.class.new(v + (o.respond_to?(:v) ? o.v : o)) }; (1..300).map { |i| t.class_eval { def +(o) = 1 } if i == 150; [1, 2].sum(t.new(0)) { |x| x }.then { |r| r.is_a?(t) ? r.v : r } }.sum)
  (["a", 1.5].sum(0.0) rescue "#{$!.class}: #{$!.message}")
  ([1, 1.5, "a"].sum(M.new(0, 0)) { |x| x } rescue "#{$!.class}: #{$!.message}")
  (t = Struct.new(:v) { def +(o) = v + o }; [2, 0.1, 0.2, 0.3].sum(t.new(1)) { |x| x })
  (t = Struct.new(:v) { def +(o) = v + o }; [2, 1e100, 1.0, -1e100].sum(t.new(1)))
  (t = Struct.new(:v) { def +(o) = v + o }; [1e100, 1.0, -1e100, 3].sum(t.new(1.5)) { |x| x })
  (t = Struct.new(:v) { def +(o) = v + o }; [1r / 3, 0.1, 2].sum(t.new(1)))
RUBY

eval(PRELUDE, TOPLEVEL_BINDING)

EXPRESSIONS.each do |source|
  begin
    value = eval(source, TOPLEVEL_BINDING)
    puts "#{source} => #{value.inspect}"
  rescue Exception => e
    puts "#{source} !! #{e.class}: #{e.message}"
  end
end
