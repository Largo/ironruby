# Differential matrix for the dependency lists of modules and classes (RubyModule.DependentClasses,
# WeakList): every class that mixes a module in, and every singleton class of an extended object,
# is recorded in the module's list, and a change to the module reaches them through it. Many
# objects are extended (and dropped, and collected) while the changes happen: a method added to
# the module, a module included into it, a method removed, undefined, made private, aliased,
# prepend, refine, rebinding, method_missing/respond_to?, clone/dup/Marshal, Struct classes, and
# a second thread that extends and defines while this one calls. Call sites are hot (warmed past
# the JIT's thresholds) before each change and checked after it.
#
#   ruby Util/extend-matrix.rb > cruby.txt
#   IR_CONFIG=Release ./ir.sh Util/extend-matrix.rb > ir.txt
#   IR_CONFIG=Release ./ir.sh -X:NoJIT -X:NoOSR Util/extend-matrix.rb > ir-nojit.txt
#
# All three outputs must be identical.

def row(name)
  r = begin
    yield
  rescue => e
    [:error, e.class]
  end
  puts "#{name}: #{r.inspect}"
end

def callfoo(o) o.foo end
def callbar(o) o.bar end
def safe(o, m) o.__send__(m) rescue $!.class end
def churn(mod, n) i = 0; while i < n; Object.new.extend(mod); i += 1; end end
def hot(objs, m) 3_000.times { objs.each { |o| safe(o, m) } }; objs.map { |o| safe(o, m) } end

# 1. a method added to the module later, while thousands of extended objects have been dropped
row("added later") {
  m = Module.new { def foo; 1; end }
  keep = Array.new(20) { Object.new.extend(m) }
  r1 = hot(keep, :foo)
  churn(m, 20_000); GC.start
  m.module_eval { def bar; 2; end; def foo; 11; end }
  [r1.uniq, hot(keep, :foo).uniq, hot(keep, :bar).uniq]
}

# 2. a module included into the module later
row("include into M later") {
  m = Module.new { def foo; 1; end }
  n = Module.new { def bar; :n; end; def foo; :nfoo; end }
  keep = Array.new(10) { Object.new.extend(m) }
  r1 = hot(keep, :bar)
  churn(m, 10_000)
  m.include(n)
  [r1.uniq, hot(keep, :bar).uniq, hot(keep, :foo).uniq, keep[0].singleton_class.ancestors.take(3).map { |a| a == m ? :m : a == n ? :n : :s }]
}

# 3. remove, undef, private, alias
row("remove/undef/private/alias") {
  base = Module.new { def foo; :base; end }
  m = Module.new { include base; def foo; :m; end; def bar; :bar; end; def baz; :baz; end }
  keep = Array.new(10) { Object.new.extend(m) }
  r = [hot(keep, :foo).uniq, hot(keep, :bar).uniq, hot(keep, :baz).uniq]
  churn(m, 5_000)
  m.send(:remove_method, :foo); r << hot(keep, :foo).uniq
  m.send(:undef_method, :bar); r << hot(keep, :bar).uniq
  m.send(:private, :baz); r << hot(keep, :baz).uniq << keep[0].respond_to?(:baz) << keep[0].respond_to?(:baz, true)
  m.send(:alias_method, :qux, :baz); m.send(:public, :qux); r << hot(keep, :qux).uniq
  r
}

# 4. a class including M, its subclass and their singleton classes
row("class/subclass/singleton") {
  m = Module.new { def foo; :m1; end }
  c = Class.new { include m }
  d = Class.new(c)
  objs = [c.new, d.new, c.new.tap { |o| o.singleton_class }, d.new.tap { |o| def o.x; end }]
  r1 = hot(objs, :foo)
  churn(m, 5_000)
  m.module_eval { def foo; :m2; end }
  r2 = hot(objs, :foo)
  m.module_eval { def bar; :b; end }
  [r1, r2, hot(objs, :bar), d.ancestors.index(m)]
}

# 5. prepend into M and into an extended object's singleton
row("prepend") {
  m = Module.new { def foo; :m; end }
  p1 = Module.new { def foo; [:p, super]; end }
  keep = Array.new(5) { Object.new.extend(m) }
  r1 = hot(keep, :foo).uniq
  churn(m, 5_000)
  m.prepend(p1)
  r2 = hot(keep, :foo).uniq
  keep[0].singleton_class.prepend(Module.new { def foo; [:sp, super]; end })
  [r1, r2, hot(keep, :foo)]
}

# 6. a refinement of M
module RefM; def foo; :plain; end; end
module RefR; refine(RefM) { def foo; :refined; end }; end
RefObjs = Array.new(5) { Object.new.extend(RefM) }
row("refine before using") { hot(RefObjs, :foo).uniq }
module UsingRef
  using RefR
  def self.go(objs) objs.map { |o| o.foo } end
end
row("refine with using") { churn(RefM, 3_000); [UsingRef.go(RefObjs).uniq, hot(RefObjs, :foo).uniq] }

# 7. instance_method rebinding, method_missing, respond_to?
row("rebind/method_missing") {
  m = Module.new { def foo; [:foo, self.class]; end; def method_missing(n, *a) n == :ghost ? :mm : super end
                   def respond_to_missing?(n, p = false) n == :ghost || super end }
  o = Object.new.extend(m)
  um = m.instance_method(:foo)
  churn(m, 5_000)
  m.module_eval { def foo; :redefined; end }
  [um.bind(o).call, um.bind_call(Object.new), o.foo, safe(o, :ghost), o.respond_to?(:ghost), o.respond_to?(:zzz), safe(o, :zzz)]
}

# 8. ObjectSpace, clone/dup/Marshal, ancestors
module MarshalM; def foo; :mm; end; end
row("clone/dup/marshal") {
  o = Object.new.extend(MarshalM)
  churn(MarshalM, 5_000); GC.start
  live = 0; ObjectSpace.each_object(Class) { |k| live += 1 if k.singleton_class? && k.include?(MarshalM) }
  c = o.clone; d = o.dup; mo = Marshal.load(Marshal.dump(o))
  [safe(c, :foo), safe(d, :foo), safe(mo, :foo), mo.singleton_class.include?(MarshalM),
   o.singleton_class.ancestors.include?(MarshalM), c.singleton_class.include?(MarshalM), d.singleton_class.include?(MarshalM), live >= 1]
}

# 9. Struct classes and their subclasses
row("struct") {
  r = []
  i = 0; s = 0
  while i < 3_000; c = Struct.new(:a, :b); s += c.new(i, 1).a + c.new(1, 2).b; i += 1; end
  r << s
  sc = Class.new(Struct.new(:x)) { def double; x * 2; end }
  r << sc.new(4).double << sc.superclass.superclass
  Struct.new("ExtendMatrixS", :q) unless defined?(Struct::ExtendMatrixS)
  r << Struct::ExtendMatrixS.new(5).q
  m = Module.new { def foo; :sm; end }
  st = Struct.new(:z) { include m }
  objs = Array.new(4) { st.new(1) }
  r1 = hot(objs, :foo).uniq
  churn(m, 2_000)
  m.module_eval { def foo; :sm2; end }
  r << r1 << hot(objs, :foo).uniq
}

# 10. class churn: Class.new { def }, singleton defs
row("class churn") {
  s = 0; i = 0
  while i < 3_000
    c = Class.new { def x; 3; end }; s += c.new.x
    o = Object.new; def o.x; 5; end; s += o.x
    i += 1
  end
  s
}

# 11. a second thread extends and defines while this one calls
row("threads") {
  m = Module.new { def foo; 1; end }
  keep = Array.new(8) { Object.new.extend(m) }
  stop = false
  q = Queue.new
  t = Thread.new do
    k = 0
    until stop
      Object.new.extend(m); o = Object.new; def o.y; end; Class.new { include m }
      k += 1
      q << 1 if k == 1
    end
    k
  end
  q.pop
  s = 0
  20_000.times { |i| keep.each { |o| s += callfoo(o) } }
  m.module_eval { def foo; 2; end }
  s2 = 0
  2_000.times { keep.each { |o| s2 += callfoo(o) } }
  stop = true
  [s, s2, t.value > 0]
}

# 12. a thread changes M (adds bar, redefines foo) while others extend in parallel
row("threads change M") {
  m = Module.new { def foo; 1; end }
  ts = Array.new(3) { Thread.new { a = []; 3_000.times { a << Object.new.extend(m) }; a } }
  m.module_eval { def bar; :late; end; def foo; 2; end }
  objs = ts.flat_map(&:value)
  [objs.size, objs.map { |o| o.foo }.uniq, objs.map { |o| o.bar }.uniq]
}

# 13. a class or singleton class that is dropped is collected
row("collected") {
  m = Module.new { def foo; 1; end }
  churn(m, 20_000)
  GC.start; GC.start
  live = 0; ObjectSpace.each_object(Class) { |k| live += 1 if k.singleton_class? && k.include?(m) }
  live < 1_000
}
