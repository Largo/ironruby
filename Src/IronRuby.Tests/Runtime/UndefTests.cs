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

        public void Scenario_MethodUndef1() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  def m1
    puts 'M::m1'
  end
  
  def m2
    puts 'M::m2'
  end
  
  def w
    puts 'M::w'
  end
  
  def u
    undef :w
  end
end

module N
 def m1
    puts 'N::m1'
  end
  
  def m2
    puts 'N::m2'
  end
end


class C
  include M

  def c1
    puts 'c1'
  end
  
  def c2
    puts 'c2'
  end
  
  undef :d2 rescue puts $!
end

class E < C
  undef m1
end

class D < E
  include N
  
  def d1
    puts 'd1'
  end
  
  def d2
    puts 'd2'
  end
  
  def w
    puts 'D::w'
  end
  
  def method_missing name, *a
    puts ""missing: #{name}""
  end
end

c = C.new
d = D.new

c.m1
c.m2
c.c1
c.c2
d.m1
d.m2
d.c1
d.c2
d.d1
d.d2
d.u
d.w
", 1, 0);
            }, @"
undefined method 'd2' for class 'C'
M::m1
M::m2
c1
c2
N::m1
N::m2
c1
c2
d1
d2
D::w
");
        }

        public void Scenario_MethodUndef2() {
            Context.DefineGlobalVariable("o", 0);
            CompilerTest(@"
class C
  def foo
  end

  undef foo rescue $o += 1
  undef foo rescue $o += 10    # blows up here
end
");
            AssertEquals(Context.GetGlobalVariable("o"), 10);
        }

        public void MethodUndefExpression() {
            AssertOutput(delegate() {
                CompilerTest(@"
def u; end
p ((undef u))
", 0, 0); 
            }, "nil");
        }

        /// <summary>
        /// Undef (unlike alias) doesn't look up the method in Object.
        /// </summary>
        public void UndefMethodLookup1() {
            AssertOutput(delegate() {
                CompilerTest(@"
module Kernel; def m_kernel; end; end
class Object;  def m_object; end; end

module N
  def m_n
  end

  module M
    undef m_object rescue puts '!undef m_object'
    undef m_kernel rescue puts '!undef m_kernel'
    undef m_n rescue puts '!undef m_n'
  end
end
");
            }, @"
!undef m_object
!undef m_kernel
!undef m_n
");
        }

        /// <summary>
        /// Undef in class_eval.
        /// </summary>
        public void UndefMethodLookup2() {
            TestOutput(@"
module Kernel; def m_kernel; end; end
class Object;  def m_object; end; end

p method(:m_kernel)
p method(:m_object)

module N
  Object.class_eval do
    undef m_object
    undef m_kernel
  end
end

p method(:m_kernel) rescue p $!
p method(:m_object) rescue p $!
", @"
#<Method: Object(Kernel)#m_kernel() UndefMethodLookup2.rb:2>
#<Method: Object#m_object() UndefMethodLookup2.rb:3>
#<NameError: undefined method 'm_kernel' for class 'Object'>
#<NameError: undefined method 'm_object' for class 'Object'>
");
        }

        /// <summary>
        /// Undef on singletons.
        /// </summary>
        public void UndefMethodLookup3() {
            TestOutput(@"
class D
  def f; end
end

d = D.new

class << d
  def undef_f
    undef f rescue p $!
  end
end

p d.method(:f)
d.undef_f
puts '---'
p D.instance_method(:f)
p d.method(:f) rescue p $!
", @"
#<Method: D#f() UndefMethodLookup3.rb:3>
---
#<UnboundMethod: D#f() UndefMethodLookup3.rb:3>
#<NameError: undefined method 'f' for class 'D'>
");
        }

        /// <summary>
        /// Undef in instance_eval.
        /// </summary>
        public void UndefMethodLookup4() {
            TestOutput(@"
class C
  def f; end
end

class D
  def f; end
  
  C.instance_eval do
    p self
    undef f rescue p $!
  end
end

puts '---'
p C.instance_method(:f) rescue p $!
p D.instance_method(:f) rescue p $!
", @"
C
#<NameError: undefined method 'f' for class 'C'>
---
#<UnboundMethod: C#f() UndefMethodLookup4.rb:3>
#<UnboundMethod: D#f() UndefMethodLookup4.rb:7>
");
        }

        /// <summary>
        /// Undef in define_method.
        /// </summary>
        public void UndefMethodLookup6() {
            TestOutput(@"
class D
end

class E
  D.send(:define_method, :bar) {
    undef f rescue p $!
  }
end

d = D.new
d.bar
", @"
#<NameError: undefined method 'f' for class 'E'>
");
        }

        /// <summary>
        /// MRI warns on undefining, removing or redefining the methods the runtime itself calls: undefining
        /// before it looks for the method; redefining object_id or __send__ (an undef too) when the class has it.
        /// </summary>
        public void UndefCoreMethodWarnings1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class X
  undef object_id
  undef object_id rescue p $!
end
module M
  def __send__(*) = 1
end
class Y
  def initialize = 1
  remove_method :initialize
  alias object_id to_s
end
", 0, 5);
            }, @"
#<NameError: undefined method 'object_id' for class 'X'>
");
        }

        /// <summary>
        /// BasicObject#initialize can be removed, with a warning, and is the only initialize Object has.
        /// </summary>
        public void RemoveBasicObjectInitialize1() {
            AssertOutput(delegate() {
                CompilerTest(@"
p Object.private_instance_methods(false).include?(:initialize)
BasicObject.send(:remove_method, :initialize)
begin
  Object.new
rescue NoMethodError => e
  puts e.message
end
", 0, 1);
            }, @"
false
undefined method 'initialize' for an instance of Object
");
        }

        /// <summary>
        /// A call of method_missing where it has been undefined is answered as BasicObject#method_missing would answer it:
        /// MRI's method_missing() goes to raise_method_missing with the call's own arguments.
        /// </summary>
        public void UndefMethodMissing1() {
            TestOutput(@"
c = Class.new { undef_method(:method_missing) }
o = c.new
begin; o.method_missing; rescue NameError, ArgumentError => e; p [e.class, e.message]; end
begin; o.method_missing(""foo""); rescue NameError, ArgumentError => e; p [e.class, e.message]; end
begin; o.method_missing(:foo, 1); rescue NameError, ArgumentError => e; p [e.class, e.name, e.args]; end
begin; o.bar; rescue NameError, ArgumentError => e; p [e.class, e.name]; end
", @"
[ArgumentError, ""no method name given""]
[ArgumentError, ""method name must be a Symbol but String is given""]
[NoMethodError, :foo, [1]]
[NoMethodError, :bar]
");
        }
    }
}
