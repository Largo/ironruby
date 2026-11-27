/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/


namespace IronRuby.Tests {
    public partial class Tests {
        public void Clone1() {
            AssertOutput(delegate() {
                CompilerTest(@"
objects = [{}, [], '', Regexp.new('foo'), Object.new, Module.new, Class.new]

objects.each do |x| 
  puts x.class.name
  
  class << x
    CONST = 1
  
    def foo
      3
    end    
    
    instance_variable_set(:@iv_singleton_x, 2);
  end  

  x.instance_variable_set(:@iv_x, 4);
  y = x.clone

  class << y
    raise '1' unless CONST == 1                                     # singleton constants copied
    raise '2' unless instance_variables == [:@iv_singleton_x]       # singleton instance variables copied (CRuby 4.0)
  end
  
  raise '3' unless y.foo == 3                                       # singleton methods copied
  raise '4' unless y.instance_variable_get(:@iv_x) == 4             # instance variables copied
end
");
            }, @"
Hash
Array
String
Regexp
Object
Module
Class
");
        }
        
        public void Dup1() {
            AssertOutput(delegate() {
                CompilerTest(@"
objects = [{}, [], '', Regexp.new('foo'), Object.new]

objects.each do |x| 
  puts x.class.name
  
  class << x
    CONST = 1
  
    def foo
      3
    end    
    
    instance_variable_set(:@iv_singleton_x, 2);
  end  

  x.instance_variable_set(:@iv_x, 4);
  y = x.dup
  
  class << y
    raise unless ((CONST;false) rescue true)          # constants NOT copied
    raise unless ((foo;false) rescue true)            # singleton class methods not copied
    raise unless instance_variables.size == 0         # instance variables on singleton not copied
  end
  
  raise unless ((y.foo;false) rescue true)            # methods NOT copied
  raise unless y.instance_variable_get(:@iv_x) == 4   # instance variables copied
end
", 1, 0);
            }, @"
Hash
Array
String
Regexp
Object
");
        }
        
        public void StructDup1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class St < Struct
end

p X = St.dup
p Y = X.new(:a,:b)
p Z = Y.dup
p U = Z[1,2]
p V = U.dup
p V.members
");
            }, @"
X
Y
Z
#<struct Z a=1, b=2>
#<struct Z a=1, b=2>
[:a, :b]
");
        }

        /// <summary>
        /// A clone keeps the instance variables of the original's singleton class, as rb_singleton_class_clone copies
        /// them (#135) - for any object, and for a module or class on #dup too, whose singleton class #dup also copies.
        /// The copy is the clone's own. They were left behind. The expected output is CRuby 4.0's.
        /// </summary>
        public void SingletonIvarsClone1() {
            TestOutput(@"
x = Object.new
class << x; @s = 2; end
x.instance_variable_set(:@i, 4)
y = x.clone
p y.singleton_class.instance_variables, y.singleton_class.instance_variable_get(:@s), y.instance_variables
y.singleton_class.instance_variable_set(:@s, 9)
p x.singleton_class.instance_variable_get(:@s), x.dup.singleton_class.instance_variables
[Module.new, Class.new, [1]].each do |o|
  class << o; @k = 1; end
  p o.clone.singleton_class.instance_variables
end
m = Module.new; class << m; @k = 1; end
c = Class.new; class << c; @k = 2; end
p m.dup.singleton_class.instance_variables, c.dup.singleton_class.instance_variable_get(:@k), Class.new.dup.singleton_class.instance_variables
", @"
[:@s]
2
[:@i]
2
[]
[:@k]
[:@k]
[:@k]
[:@k]
2
[]
");
        }

        /// <summary>
        /// A method of a cloned class or module looks its constants up in the clone, which CRuby's clone
        /// arranges by rewriting the method's cref ([Bug #15877]).
        /// </summary>
        public void CloneConstantLookup1() {
            TestOutput(@"
class C
  def foo; TEST; end
  def bar; defined?(TEST) ? TEST : :none; end
end
C1 = C.clone
C2 = C.clone
class C1; TEST = :c1; end
class C2; TEST = :c2; end
p C1.new.foo, C2.new.foo, C1.new.bar, C.new.bar
module M
  def foo; TEST; end
end
M1 = M.dup
M1.const_set(:TEST, :m1)
o = Object.new.extend(M1)
p o.foo
", @"
:c1
:c2
:c1
:none
:m1
");
        }

        /// <summary>
        /// #dup and #clone of a module or class copy its constants, methods and class variables before they call
        /// initialize_copy, as MRI 4.0's init_copy does: an initialize_copy reading a constant before super reads the copy's,
        /// one that never calls super still gets them. Module and Class have no initialize_copy of their own (Kernel's),
        /// which leaves a module that already exists as it is.
        /// </summary>
        public void ModuleCopyBeforeInitializeCopy1() {
            TestOutput(@"
K = :outer
module M
  K = :inner
  def self.initialize_copy(o)
    p [const_get(:K), constants, instance_methods(false)]
    super
  end
  def im = 1
end
M.dup; M.clone
module N; X = 1; @@v = 2; def self.initialize_copy(o) = nil; def a = 1; end
n = N.dup; p n.constants, n.instance_methods(false), n.class_variables
class Q; Y = 1; def self.initialize_copy(o) = nil; def q = 1; end
q = Q.clone; p q.constants, q.instance_methods(false), q.superclass, q.name
p Module.instance_method(:initialize_copy).owner, Class.instance_method(:initialize_copy).owner
e = Module.new { def z = 1 }; c = Module.new
p c.send(:initialize_copy, e).equal?(c), c.instance_methods(false), (Module.new.send(:initialize_copy, Class.new) rescue $!)
", @"
[:inner, [:K], [:im]]
[:inner, [:K], [:im]]
[:X]
[:a]
[:@@v]
[:Y]
[:q]
Object
nil
Kernel
Kernel
true
[]
#<TypeError: initialize_copy should take same class object>
");
        }
    }
}
