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
", 0, 1);
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

        /// <summary>
        /// A method defined before anything was refined, and first called after, does not see a `using' that comes
        /// later in the scope it was defined in.
        /// </summary>
        public void Refinements7() {
            TestOutput(@"
def before(s)
  s.r7
rescue NoMethodError
  :plain
end
module R7
  refine(String) { def r7; :refined; end }
end
p before('x')
using R7
p before('x')
def after(s) = s.r7
p after('x')
", @"
:plain
:plain
:refined
");
        }

        /// <summary>
        /// An OSR loop entered under other refinements than its compiled copy's gets a copy for those, however
        /// often that happens: it does not use up the rebuilds kept for locals whose types widen, and retire.
        /// </summary>
        public void Refinements8() {
            int specialized = IronRuby.Runtime.Osr.OsrLoopSite.Specialized;
            int retired = IronRuby.Runtime.Osr.OsrLoopSite.Retired;
            TestOutput(@"
module R8a; refine(String) { def r8a; 1; end }; end
module R8b; refine(String) { def r8b; 2; end }; end
module R8c; refine(String) { def r8c; 3; end }; end
module R8d; refine(String) { def r8d; 4; end }; end
module R8e; refine(String) { def r8e; 5; end }; end
work = lambda { i = 0; while i < 100_000; i += 1; end; i }
p work.call
using R8a
p work.call
using R8b
p work.call
using R8c
p work.call
using R8d
p work.call
using R8e
p work.call
", @"
100000
100000
100000
100000
100000
100000
");
            int retiredHere = IronRuby.Runtime.Osr.OsrLoopSite.Retired - retired;
            int specializedHere = IronRuby.Runtime.Osr.OsrLoopSite.Specialized - specialized;
            Assert(retiredHere == 0 && specializedHere == 6,
                System.String.Format("OSR sites retired: {0} (expected 0), copies specialized: {1} (expected 6)", retiredHere, specializedHere));
        }

        /// <summary>
        /// A refined call in a method body, or in a block in it, tests the body's refinement record (RubyCallAction.Resolve).
        /// The same `def' run again where other refinements are active changes the record, and the calls bound against
        /// the old one bind again - in the calls of the method that start afterwards and in one that is still running,
        /// whether or not its scope has evaluated a string. (Before the record was tested, a running call that had
        /// evaluated a string kept the old refinements, while every other one saw the new ones.)
        /// </summary>
        public void Refinements9() {
            TestOutput(@"
module R1; refine(String) { def r = :r1 }; end
module R2; refine(String) { def r = :r2 }; end
module R3; refine(String) { def r = :r3 }; end
class C; end
using R1
definer = proc do
  C.class_eval do
    def m(s) = [s.r, [1].map { s.r }.first]
    def n(s, again) = [s.r, again.call, s.r, [1].map { s.r }.first]
    def e(s, again) = [eval('s.r'), again.call, s.r, eval('s.r')]
  end
end
definer.call
p (1..100).map { [C.new.m('x'), C.new.n('x', -> { :none }), C.new.e('x', -> { :none })] }.uniq
using R2
p C.new.m('x')
p C.new.e('x', -> { definer.call; :again })
p (1..100).map { [C.new.m('x'), C.new.n('x', -> { :none }), C.new.e('x', -> { :none })] }.uniq
using R3
p C.new.n('x', -> { definer.call; :again })
p (1..100).map { [C.new.m('x'), C.new.n('x', -> { :none }), C.new.e('x', -> { :none })] }.uniq
", @"
[[[:r1, :r1], [:r1, :none, :r1, :r1], [:r1, :none, :r1, :r1]]]
[:r1, :r1]
[:r1, :again, :r2, :r2]
[[[:r2, :r2], [:r2, :none, :r2, :r2], [:r2, :none, :r2, :r2]]]
[:r2, :again, :r3, :r3]
[[[:r3, :r3], [:r3, :none, :r3, :r3], [:r3, :none, :r3, :r3]]]
");
        }

        /// <summary>
        /// A block written in a method body, and one inside it, that Module#refine runs see the refinements of the module
        /// being refined, though their calls were bound against the method's record before: a rule that reads the record
        /// for a block passes only while no refine block runs. (CRuby 4.0 answers :outer_q and :outer_n on the last line,
        /// from the inline caches the earlier calls filled; with no earlier call it answers :inner_q and :inner_n.)
        /// </summary>
        public void Refinements10() {
            TestOutput(@"
module Outer
  refine(String) { def o = :outer; def q = :outer_q; def n = :outer_n }
end
using Outer
class FakeRefiner
  def refine(_) = yield
end
def mk(target, out) = target.send(:refine, Integer) { out << ['x'.o, 'x'.q, [1].map { 'x'.n }.first] }
holder = Module.new { refine(String) { def q = :inner_q; def n = :inner_n } }
out = []
fake = FakeRefiner.new
100.times { mk(fake, out) }
p out.uniq
mk(holder, out)
p out.last
", @"
[[:outer, :outer_q, :outer_n]]
[:outer, :inner_q, :inner_n]
");
        }

        /// <summary>
        /// A refined call on a Ruby object, made in a method or in a block written in one, gets a precompiled dispatcher that
        /// tests the method's record (MethodDispatcher.CreateRefinedRubyObjectDispatcher): with no arguments, with two and
        /// with a block. It binds again when the `def' runs again under other refinements, and when Module#refine runs the
        /// block. (CRuby 4.0 prints :r1 on the third line, its block keeping the refinements it was made under - see
        /// Refinements6 - and :r2 on the fifth, from the inline caches the earlier calls filled.)
        /// </summary>
        public void Refinements11() {
            TestOutput(@"
class P; end
module R1
  refine(P) { def r = :r1; def r2(a, b) = [:r1, a, b]; def rb = [:r1, yield] }
end
module R2
  refine(P) { def r = :r2; def r2(a, b) = [:r2, a, b]; def rb = [:r2, yield] }
end
using R1
definer = proc do
  def m(o) = [o.r, o.r2(1, 2), o.rb { 3 }, [1].map { [o.r, o.r2(4, 5), o.rb { 6 }] }.first]
end
definer.call
o = P.new
p (1..100).map { m(o) }.uniq
using R2
p m(o)
definer.call
p (1..100).map { m(o) }.uniq
class FakeRefiner
  def refine(_) = yield
end
def mk(target, out, o) = target.send(:refine, Integer) { out << [o.r, o.r2(1, 2), o.rb { 3 }] }
holder = Module.new { refine(P) { def r = :inner; def r2(a, b) = [:inner, a, b]; def rb = [:inner, yield] } }
out = []
fake = FakeRefiner.new
100.times { mk(fake, out, o) }
p out.uniq
mk(holder, out, o)
p out.last
p (1..100).map { mk(fake, out, o); out.last }.uniq
", @"
[[:r1, [:r1, 1, 2], [:r1, 3], [:r1, [:r1, 4, 5], [:r1, 6]]]]
[:r1, [:r1, 1, 2], [:r1, 3], [:r1, [:r1, 4, 5], [:r1, 6]]]
[[:r2, [:r2, 1, 2], [:r2, 3], [:r2, [:r2, 4, 5], [:r2, 6]]]]
[[:r2, [:r2, 1, 2], [:r2, 3]]]
[:inner, [:inner, 1, 2], [:inner, 3]]
[[:r2, [:r2, 1, 2], [:r2, 3]]]
");
        }

        /// <summary>
        /// A refinement of a class or module is searched ahead of the modules prepended to it, not only ahead of it: CRuby
        /// keeps the refined method entry in the module's own method table, before its prepended modules. `super' from a
        /// class's refinement continues at its first prepended module, from a module's refinement at the module itself.
        /// Every expected line is CRuby 4.0's.
        /// </summary>
        public void Refinements12() {
            TestOutput(@"
# 1 refine + prepend (prepend before using)
module P1; def +(o) = [:P1, super]; end
module R1; refine Integer do; def +(o) = [:refined, o]; end; end
Integer.prepend(P1)
module T1; using R1; p(3 + 1); p(3.send(:+, 1)); p(3.public_send(:+, 1)); p(3.method(:+).call(1)); p 3.respond_to?(:+); end

# 2 refinement calls super with prepend: where does super go?
class C2; def m = [:C2]; end
module P2; def m = [:P2, *super]; end
module R2; refine C2 do; def m = [:R2, *super]; end; end
C2.prepend(P2)
module T2; using R2; p C2.new.m; p C2.new.send(:m); p C2.new.method(:m).call; end

# 3 prepend after using
class C3; def m = [:C3]; end
module R3; refine C3 do; def m = [:R3, *super]; end; end
module P3; def m = [:P3, *super]; end
module T3; using R3; C3.prepend(P3); p C3.new.m; end

# 4 include
class C4; def m = [:C4]; end
module I4; def m = [:I4, *super]; end
module R4; refine C4 do; def m = [:R4, *super]; end; end
C4.include(I4)
module T4; using R4; p C4.new.m; end

# 5 subclass receiver, prepend on superclass
class C5; def m = [:C5]; end
class D5 < C5; end
module P5; def m = [:P5, *super]; end
module R5; refine C5 do; def m = [:R5, *super]; end; end
C5.prepend(P5)
module T5; using R5; p D5.new.m; end

# 6 subclass receiver, prepend on subclass, refine superclass
class C6; def m = [:C6]; end
class D6 < C6; end
module P6; def m = [:P6, *super]; end
module R6; refine C6 do; def m = [:R6, *super]; end; end
D6.prepend(P6)
module T6; using R6; p D6.new.m; end

# 8 refine subclass, prepend on subclass
class C8; def m = [:C8]; end
class D8 < C8; def m = [:D8, *super]; end
module P8; def m = [:P8, *super]; end
module R8; refine D8 do; def m = [:R8, *super]; end; end
D8.prepend(P8)
module T8; using R8; p D8.new.m; p C8.new.m; end

# 9 Integer + with super from refinement
module P9; def -(o) = [:P9, super]; end
module R9; refine Integer do; def -(o) = [:R9, super]; end; end
Integer.prepend(P9)
module T9; using R9; p(5 - 1); x = 5; p(x - 2); end

# 10 without using: prepended still applies
p(3 + 1)
p(5 - 1)
# 11 loop (hot path)
module T11; using R1; s = nil; 1000.times { |i| s = i + 1 }; p s; def self.f(a) = a + 1; 200.times { f(2) }; p f(2); end
# 12 refine an included module that has a module prepended to it
module M10; def m = [:M10]; end
module Q10; def m = [:Q10, *super]; end
M10.prepend Q10
class C10; include M10; end
module R10; refine M10 do; def m = [:R10, *super]; end; end
module T10; using R10; p C10.new.m; end

# 13 refine a module prepended to a class, which has a module prepended to it
class C11; def m = [:C11]; end
module P11; def m = [:P11, *super]; end
module Q11; def m = [:Q11, *super]; end
P11.prepend Q11
C11.prepend P11
module R11; refine P11 do; def m = :R11; end; end
module T11; using R11; p C11.new.m; end
p C11.new.m
", @"
[:refined, 1]
[:refined, 1]
[:refined, 1]
[:refined, 1]
true
[:R2, :P2, :C2]
[:R2, :P2, :C2]
[:R2, :P2, :C2]
[:R3, :P3, :C3]
[:R4, :C4]
[:R5, :P5, :C5]
[:P6, :C6]
[:R8, :P8, :D8, :C8]
[:C8]
[:R9, [:P9, 4]]
[:R9, [:P9, 3]]
[:P1, 4]
[:P9, 4]
[:refined, 1]
[:refined, 1]
[:R10, :M10]
:R11
[:Q11, :P11, :C11]
");
        }

        /// <summary>
        /// super from a method written where a refinement is active resolves under it, as a call does: CRuby's
        /// search_refined_method reads the cref of the frame that calls super. A refinement of a superclass, of one
        /// behind an included module, of a class with a prepended module and of an ancestor module; super from
        /// define_method's block; a method the refinement adds; defined?(super), which does not see refinements; and a
        /// method written where nothing is used.
        /// </summary>
        public void Refinements13() {
            TestOutput(@"
class C1; def m(a = 0) = [:C1, a]; end
module R1; refine C1 do; def m(a = 0) = [:R1, *super]; end; end
module P2; def m = [:P2, *super]; end
class C2; def m = [:C2]; prepend P2; end
module R2; refine C2 do; def m = [:R2, *super]; end; end
module A3; def m = [:A3]; end
class C3; include A3; end
module R3; refine A3 do; def m = [:R3, *super]; def q = :q; end; end
class C4; def method_missing(n, *) = [:mm, n]; end
module R4; refine C4 do; def q = [:R4]; end; end
class D0 < C1; def m(a = 0) = [:D0, *super]; end
module T
  using R1; using R2; using R3; using R4
  class D1 < C1
    def m(a = 0) = [:D1, *super]
  end
  module M1; def m(a = 0) = [:M1, *super]; end
  class E1 < C1; include M1; def m(a = 0) = [:E1, *super]; end
  class F1 < C1; def m(a = 0) = [:F1, *super(a + 1) { }]; end
  class D2 < C2; def m = [:D2, *super]; end
  class D3 < C3; define_method(:m) { [:D3, *super()] }; def z = defined?(super); end
  class D4 < C4; def q = [:D4, *super]; def z = [:D4z, *super]; end
  p D1.new.m(1), F1.new.m(1), E1.new.m, D2.new.m, D3.new.m, D3.new.z, D4.new.q, D4.new.z
  s = 0
  d = D1.new
  3000.times { |i| s += d.m(i).size }
  p s
end
p D0.new.m
", @"
[:D1, :R1, :C1, 1]
[:F1, :R1, :C1, 2]
[:E1, :M1, :R1, :C1, 0]
[:D2, :R2, :P2, :C2]
[:D3, :R3, :A3]
nil
[:D4, :R4]
[:D4z, :mm, :z]
12000
[:D0, :C1, 0]
");
        }

        /// <summary>
        /// super from a module prepended to an included module that is refined: the refinement is searched ahead of
        /// the prepended module (the refined entry stays in the module's own table), so super from that prepended
        /// module goes on to the module and does not come back to the refinement.
        /// </summary>
        public void Refinements14() {
            TestOutput(@"
module P; def m = [:P]; end
module Q; end
P.prepend(Q)
class C; include P; end
module R; refine P do; def m = [:R, *super]; end; end
using R
module Q; def m = [:Q, *super]; end
p C.new.m
p Q.instance_method(:m).bind_call(C.new)
", @"
[:R, :P]
[:Q, :P]
");
        }

        /// <summary>
        /// `using' at the top level refines the rest of that frame, blocks included: a block or lambda written earlier in the
        /// same frame and called afterwards sees the refinement (MRI replaces the frame's cref, and blocks read it from the
        /// frame), as does a define_method body made from one. A method defined before the `using' does not, and neither does
        /// a block or define_method body written in a class body or a string class_eval that had ended by then, nor a class
        /// body reopened after it: such a body's cref points at the one current when it was entered.
        /// </summary>
        public void Refinements15() {
            TestOutput(@"
class C; def m = [:C]; def n = [:Cn]; end
module R; refine C do; def m = [:R]; end; end
module R2; refine C do; def n = [:R2]; end; end
pr = proc { C.new.m }
p pr.call
la = lambda { [C.new.m, C.new.send(:m), C.new.method(:m).call] }
Object.send(:define_method, :dm) { C.new.m }
def meth = C.new.m
class D < C; define_method(:m) { [:D, *super()] }; define_method(:k) { C.new.m }; $dp = proc { C.new.m }; end
module M; class N; $np = proc { C.new.m }; end; end
C.class_eval('$ep = proc { C.new.m }')
using R
between = proc { [C.new.m, C.new.n] }
using R2
p pr.call, la.call, dm, meth
p between.call
p [D.new.m, D.new.k, $dp.call, $np.call, $ep.call]
class D; p [D.new.m, C.new.m]; end
r = nil
3000.times { r = [pr.call, $dp.call, D.new.k, D.new.m] }
p r
", @"
[:C]
[:R]
[[:R], [:R], [:R]]
[:R]
[:C]
[[:R], [:R2]]
[[:D, :C], [:C], [:C], [:C], [:C]]
[[:D, :C], [:R]]
[[:R], [:C], [:C], [:D, :C]]
");
        }

        /// <summary>
        /// Class#new calls initialize as MRI's rb_obj_call_init does, with no refinement active: a refinement of initialize
        /// active where new is called is not seen, whether the class's initialize is Ruby's or a library's (Struct#initialize,
        /// Array#initialize) and whether the rule of new is interpreted or compiled (a Struct's was, after 200 calls).
        /// </summary>
        public void Refinements16() {
            TestOutput(@"
S = Struct.new(:a, :b)
class A < Array; end
class C; def initialize(*a) = (@a = a); attr_reader :a; end
module R
  refine(S) { def initialize(*) = super(:refined, :init) }
  refine(A) { def initialize(*) = super(2, :r) }
  refine(C) { def initialize(*) = super(:r) }
end
module U
  using R
  def self.make = [S.new(1, 2).to_a, A.new(1, 0).to_a, C.new(1).a]
end
r = nil
300.times { |i| x = U.make; (p [i, x]; r = x) if x != r }
", @"
[0, [[1, 2], [0], [1]]]
");
        }
    }
}
