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
        /// <summary>
        /// A method that arrives in an active refinement after a call of its name has been bound - defined,
        /// redefined or removed outside the refine block - is what the call makes next.
        /// </summary>
        public void Refinements1() {
            TestOutput(@"
module M
  R = refine(String) { def a; :a; end }
end
using M
def call_shout(s)
  s.shout
rescue NoMethodError
  :no_method
end
p call_shout('x')
M::R.send(:define_method, :shout) { :refined }
p call_shout('x')
M::R.send(:define_method, :shout) { :redefined }
p call_shout('x')
M::R.send(:remove_method, :shout)
p call_shout('x')
", @"
:no_method
:refined
:redefined
:no_method
");
        }

        /// <summary>
        /// An operator added to an active refinement after the method using it has been called many times.
        /// </summary>
        public void Refinements2() {
            TestOutput(@"
module Later
  R = refine(Integer) { def zzz; 0; end }
end
module Calc
  using Later
  def self.mul(a, b)
    a * b
  end
end
200.times { Calc.mul(3, 4) }
p Calc.mul(3, 4)
Later::R.send(:define_method, :*) { |o| :refined_mul }
p Calc.mul(3, 4)
p 3 * 4
", @"
12
:refined_mul
12
");
        }

        /// <summary>
        /// Whether a call can be refined depends on its name and the receiver's class: a refinement of
        /// String#shout leaves String#upcase and every Integer call alone, a refinement of a module reaches
        /// the classes that include it, and a CLR class can be refined.
        /// </summary>
        public void Refinements3() {
            TestOutput(@"
module Shout
  refine(String) { def shout; upcase + '!'; end }
end
module CmpName
  refine(Comparable) { def cmp_name; :refined_cmp; end }
end
module SbShout
  refine(System::Text::StringBuilder) { def shout; :sb; end }
end
module User
  using Shout
  using CmpName
  using SbShout
  def self.go(s, i)
    [s.shout, s.upcase, i + 1, i.cmp_name, s.cmp_name, System::Text::StringBuilder.new.shout]
  end
end
3.times { User.go('a', 1) }
p User.go('a', 1)
p [('a'.shout rescue :none), (1.cmp_name rescue :none)]
", @"
[""A!"", ""A"", 2, :refined_cmp, :refined_cmp, :sb]
[:none, :none]
");
        }

        /// <summary>
        /// Kernel#respond_to? sees the refinements active where it is called; the folded respond_to?(:sym)
        /// of a Ruby object must not answer for a scope that sees another refinement.
        /// </summary>
        public void Refinements4() {
            TestOutput(@"
class Foo; end
module RT
  refine(Foo) { def bar; 1; end }
end
def rt1(o) = o.respond_to?(:bar)
module UsesRT
  using RT
  def self.rt2(o) = o.respond_to?(:bar)
end
f = Foo.new
3.times { rt1(f); UsesRT.rt2(f) }
p [rt1(f), UsesRT.rt2(f), f.respond_to?(:bar)]
", @"
[false, true, false]
");
        }

        /// <summary>
        /// ""#{x}"" and &amp;:sym honour the refinements active where they are written, and only there.
        /// </summary>
        public void Refinements5() {
            TestOutput(@"
class Pt; def to_s; 'plain'; end; end
module TS
  refine(Pt) { def to_s; 'refined'; end }
end
module SY
  refine(Integer) { def double; self * 2; end }
end
module Uses
  using TS
  using SY
  def self.show(o) = ""#{o}""
  def self.dbl(a) = a.map(&:double)
  def self.lens(a) = a.map(&:size)
end
p [""#{Pt.new}"", Uses.show(Pt.new), Uses.dbl([1, 2]), Uses.lens(['ab']), ([1].map(&:double) rescue :no_double)]
", @"
[""plain"", ""refined"", [2, 4], [2], :no_double]
");
        }

        /// <summary>
        /// A method defined where nothing is `using'd sees no refinement however hot it runs; the same `def'
        /// run again where one is active does.
        /// </summary>
        public void Refinements6() {
            TestOutput(@"
module R6
  refine(String) { def r6; :refined; end }
end
definer = proc do
  def m6(s)
    s.r6
  rescue NoMethodError
    :plain
  end
end
definer.call
100.times { m6('x') }
p m6('x')
using R6
p m6('x')
definer.call
p m6('x')
", @"
:plain
:plain
:refined
");
        }
    }
}
