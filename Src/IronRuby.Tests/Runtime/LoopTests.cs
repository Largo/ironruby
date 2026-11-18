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
        public void RubyForLoop1() {
            AssertOutput(delegate() {
                CompilerTest(@"
for a in [1,2,3]
    x = 'ok'
    print a
end

print x         # x visible here, for-loop doesn't define a scope
");
            }, "123ok");
        }

        public void WhileLoop1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 0
x = while i < 4 do 
  puts i
  i = i + 1
end
puts x
");
            }, @"
0
1
2
3

");
        }

        /// <summary>
        /// Break in a while loop.
        /// </summary>
        public void LoopBreak1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 3
x = while i > 0 do 
  puts i
  if i == 2 then
    break
  end
  i = i - 1
end
puts x
");
            }, @"
3
2

");
        }

        /// <summary>
        /// Break with a value in a while loop.
        /// </summary>
        public void LoopBreak2() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 3
x = while i > 0 do 
  puts i
  if i == 2 then
    break 'foo'
  end
  i = i - 1
end
puts x
");
            }, @"
3
2
foo
");
        }

        /// <summary>
        /// Loop break from within class declaration.
        /// </summary>
        public void LoopBreak3() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 0
while i < 5 do
  puts i

  class C
    puts 'in C'
    break
  end
  
  i = i + 1
end
");
            }, @"
0
in C
");
        }

        /// <summary>
        /// Redo in a while loop.
        /// </summary>
        public void LoopRedo1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 3
j = 2
x = while i > 0 do 
  puts i
  if i == 2 and j > 0 then
    j = j - 1
    redo
  end
  i = i - 1
end
puts x
");
            }, @"
3
2
2
2
1

");
        }

        /// <summary>
        /// Next in a while loop.
        /// </summary>
        public void LoopNext1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 3
j = 2
x = while i > 0 do 
  puts i
  if i == 2 and j > 0 then
    j = j - 1
    next 'foo'
  end
  i = i - 1
end
puts x
");
            }, @"
3
2
2
2
1

");
        }

        public void Scenario_RubyUntilLoop1() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 1
until i > 3 do
  puts i
  i = i + 1
end
");
            }, @"
1
2
3");
        }

        public void Scenario_WhileLoopCondition1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def w x
  while x; p x; break; end
end

w []
w 0
w 1
w nil
w true
w false
");
            }, @"
[]
0
1
true
");
        }

        public void PostTestWhile1() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  puts 1
end while (puts 2)
");
            }, @"
1
2
");
        }

        public void PostTestUntil1() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  puts 1
end until (puts(2); true)
");
            }, @"
1
2
");
        }

        public void WhileModifier1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 1 while (puts 2)
");
            }, @"
2
");
        }

        public void UntilModifier1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 1 until (puts(2); true)
");
            }, @"
2
");
        }

        public void WhileModifier2() {
            AssertOutput(delegate() {
                CompilerTest(@"
$i = false
def c
  puts 'c'
  $i = !$i 
end

def x
  puts 'x'
end

(x) while c
");
            }, @"
c
x
c
");
        }

        public void UntilModifier2() {
            AssertOutput(delegate() {
                CompilerTest(@"
$i = true
def c
  puts 'c'
  $i = !$i 
end

def x
  puts 'x'
end

(x) until c
");
            }, @"
c
x
c
");
        }

        /// <summary>
        /// Array#each_index outlined with its block by -X:OSR (BlockLoopKind.EachIndex): the length is read
        /// afresh every step, as rb_ary_each_index does, so a pop or a push under the loop ends it where CRuby
        /// does; an element of another type, break, next, a raise and its backtrace frame, and a subclass's own
        /// each_index. The output is CRuby 4.0's.
        /// </summary>
        public void BlockLoopEachIndex1() {
            TestOutput(@"
require 'ruby4'
def sum(a); s = 0; a.each_index { |i| s += i * 3 + a[i] }; s; end
a = (0...5000).to_a
p sum(a), sum([1, 2, 3])
a[4000] = 0.5
p sum(a)
a[4000] = nil
p((sum(a) rescue $!.class))
b = (0...5000).to_a
s = 0
b.each_index { |i| s += i; b.pop if i == 3000 }
p s, b.size
s = 0
b.each_index { |i| s += i; b.push(1) if i == 4000 }
p s, b.size
p(b.each_index { |i| break i * 2 if i == 4500 })
c = 0
b.each_index { |i| next if i.odd?; c += 1 }
p c
def raiser(a); s = 0; a.each_index { |i| s += 10 / (a.size - 1 - i) }; end
begin
  raiser(a)
rescue ZeroDivisionError => e
  p e.backtrace_locations[0, 3].map(&:label)
end
k = Class.new(Array) { def each_index; yield :sub; self; end }
r = []
k.new(a).each_index { |i| r << i }
p r
", @"
49990000
15
49986000.5
TypeError
12492501
4999
12497500
5000
9000
2500
[""Integer#/"", ""block in Object#raiser"", ""Array#each_index""]
[:sub]
");
        }

        /// <summary>
        /// A global variable read or assigned in a specialized method or an outlined loop (JitCompiler.EmitGlobalRead,
        /// EmitGlobalWrite): the value it holds now, whatever it is, through an alias too; what an assignment stores
        /// is what a read sees, and trace_var still sees every assignment. A special variable, an undefined one, or
        /// one the loop both reads and assigns keeps the loop generic. The output is CRuby 4.0's.
        /// </summary>
        public void JitGlobals1() {
            TestOutput(@"
require 'ruby4'
$g = 3
def gm(n) = $g + n
def gl(m)
  i = 0; s = 0
  while i < m
    s = $g
    i += 1
  end
  s
end
p 100.times.map { |i| gm(i) }.sum
p gl(10_000)
$g = 7
p gm(1), gl(100)
$g = 2**70
p gm(1), gl(100)
$g = ""str""
p gl(100)
alias $h $g
$h = :via_alias
p gl(100)
p((gm(""x"") rescue $!.class))
def gs(m)
  i = 0; s = nil
  while i < m
    s = $stdout
    i += 1
  end
  s.equal?(STDOUT)
end
p gs(10_000)
def gu(m)
  i = 0; s = 1
  while i < m
    s = $undefined_here
    i += 1
  end
  s
end
$VERBOSE = nil
p gu(10_000)
$t = 0
th = Thread.new { 1000.times { |k| $t = k } }
th.join
p gl(10), $t
def gw(m)
  i = 0
  while i < m
    $w = i * 1_000_000
    i += 1
  end
  $w
end
p gw(10_000)
$cnt = 0
def grw(m)
  i = 0
  while i < m
    $cnt = $cnt + 1
    i += 1
  end
  $cnt
end
p grw(10_000), grw(10_000)
traced = []
trace_var(:$w) { |v| traced << v }
p gw(20_000), traced.size, traced.last
untrace_var(:$w)
p gw(5), traced.size
def mw(v) = ($w2 = v)
p 100.times.map { |i| mw(i) }.sum, $w2
", @"
5250
3
8
7
1180591620717411303425
1180591620717411303424
""str""
:via_alias
NoMethodError
true
nil
:via_alias
999
9999000000
10000
20000
19999000000
20000
19999000000
4000000
20000
4950
99
");
        }

        /// <summary>
        /// -X:JITCalls (a prototype, off by default): a method the JIT compiles calls the typed bodies of the
        /// other methods it calls on self directly. What the callee's entry would have guarded falls to the
        /// caller's: a callee redefined, overridden in a subclass, deopting, or passed a type it was not
        /// compiled for still gives CRuby 4.0's output.
        /// </summary>
        public void JitCalls1() {
            int bound = IronRuby.Runtime.Jit.JitRuntime.DirectCallBodies;
            var engine = IronRuby.Ruby.CreateEngine((setup) => { setup.Options["JITCalls"] = true; });
            AreEqual(engine.CreateScriptSourceFromString(JitCallsScript, "jitcalls.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                JitCallsExpected);
            // the output is the same when nothing is bound: tot's call of acc, a callee with a loop, is one
            // the inliner never takes, and Shape#area's of side is another
            Assert(IronRuby.Runtime.Jit.JitRuntime.DirectCallBodies >= bound + 2);
            bound = IronRuby.Runtime.Jit.JitRuntime.DirectCallBodies;
            AreEqual(Engine.CreateScriptSourceFromString(JitCallsScript, "jitcalls.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                JitCallsExpected);
            // and without the switch nothing is
            Assert(IronRuby.Runtime.Jit.JitRuntime.DirectCallBodies == bound);
        }

        /// <summary>
        /// -X:JITMethods (a prototype, off by default): the method JIT's typed bodies as static methods of a runtime
        /// assembly, in each of its modes, alone and with -X:JITCalls, whose direct calls are then calls of those
        /// methods. The tree compiler is IronRuby.JitEmit.dll, which this project references so that it sits next
        /// to IronRuby.dll; without it the switch would compile as before, so the test checks that bodies were emitted.
        /// </summary>
        public void JitMethods1() {
            var modes = new[] { "run", "run+", "run,tiered", "run,tiered+", "collect+", "dynamic+", "run,entry,readonly+" };
            foreach (string mode in modes) {
                int emitted = IronRuby.Runtime.Jit.JitMethodEmitter.Emitted;
                int failed = IronRuby.Runtime.Jit.JitMethodEmitter.Failed;
                var engine = IronRuby.Ruby.CreateEngine((setup) => {
                    setup.Options["JITMethods"] = mode.TrimEnd('+');
                    // with -X:JITCalls
                    if (mode.EndsWith("+")) { setup.Options["JITCalls"] = true; }
                });
                AreEqual(engine.CreateScriptSourceFromString(JitCallsScript, "jitmethods.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                    JitCallsExpected);
                Assert(IronRuby.Runtime.Jit.JitMethodEmitter.Emitted > emitted);
                Assert(IronRuby.Runtime.Jit.JitMethodEmitter.Failed == failed);
            }
        }

        /// <summary>
        /// -X:JITResume (a prototype, off by default; Docs/jit-resume-deopt.md, stage 1): a body whose attribute
        /// operations come after an effect - Richards' Scheduler#hold_self - is compiled, each of them falling back
        /// to the call it stands for when the receiver is not what the profile saw: another class with a writer of
        /// its own, a private writer, a frozen receiver, method_missing. The effect before them runs once per call
        /// whatever they do, and the output is CRuby 4.0's with the switch and without.
        /// </summary>
        public void JitResume1() {
            int fallbacks = IronRuby.Runtime.Jit.JitRuntime.Fallbacks;
            var engine = IronRuby.Ruby.CreateEngine((setup) => { setup.Options["JITResume"] = true; });
            AreEqual(engine.CreateScriptSourceFromString(JitResumeScript, "jitresume.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                JitResumeExpected);
            // only a body compiled with fallbacks makes one, and each of the four odd receivers is one at least:
            // the output is the same when hold_self stays generic
            Assert(IronRuby.Runtime.Jit.JitRuntime.Fallbacks >= fallbacks + 4);
            fallbacks = IronRuby.Runtime.Jit.JitRuntime.Fallbacks;
            AreEqual(Engine.CreateScriptSourceFromString(JitResumeScript, "jitresume.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                JitResumeExpected);
            // and without the switch it does stay generic
            Assert(IronRuby.Runtime.Jit.JitRuntime.Fallbacks == fallbacks);
        }

        // hold_self made hot with a plain task, then called with each odd one, after the same calls of a cold
        // copy of it; the output is CRuby 4.0's.
        private const string JitResumeScript = @"
class Task; attr_accessor :task_holding, :link; end
class Sched
  attr_accessor :hold_count, :current_task
  def initialize(t); @hold_count = 0; @current_task = t; end
  def hold_self
    @hold_count += 1
    @current_task.task_holding = true
    @current_task.link
  end
end
class Cold < Sched
  def hold_self
    @hold_count += 1
    @current_task.task_holding = true
    @current_task.link
  end
end
class Other
  attr_reader :seen
  def task_holding=(v); @seen = [:w, v]; end
  def link; :other; end
end
class Hidden; attr_accessor :task_holding, :link; private :task_holding=; end
class Missing; def method_missing(n, *a); [n, a]; end; def respond_to_missing?(*) = true; end
probe = lambda do |s, plain|
  r = [s.hold_count]
  other = Other.new
  s.current_task = other
  r << s.hold_self << other.seen.inspect << s.hold_count
  s.current_task = Hidden.new
  r << (begin; s.hold_self; rescue NoMethodError => e; e.message; end) << s.hold_count
  s.current_task = Task.new.freeze
  r << (begin; s.hold_self; rescue FrozenError => e; e.class; end) << s.hold_count
  s.current_task = Missing.new
  r << s.hold_self.inspect << s.hold_count
  s.current_task = plain
  r << s.hold_self << plain.task_holding << s.hold_count
end
plain = Task.new; plain.link = :plain
# First on a class whose hold_self stays cold: what the probes define on their first use is then
# defined before Sched#hold_self is compiled. A method defined after that fails the body's version
# check at its entry, and the probes would run the generic body.
cold = probe.call(Cold.new(plain), plain)
s = Sched.new(plain)
r = []
120.times { r << s.hold_self }
r = r.uniq + probe.call(s, plain) + cold
r.join(',')
";
        private const string JitResumeExpected = "plain,120,other,[:w, true],121,private method 'task_holding=' called for an instance of Hidden,122,FrozenError,123,[:link, []],124,plain,true,125,0,other,[:w, true],1,private method 'task_holding=' called for an instance of Hidden,2,FrozenError,3,[:link, []],4,plain,true,5";

        // Callers and callees made hot, then probed where a direct call has to give way; the output is CRuby 4.0's.
        private const string JitCallsScript = @"
def fib(k) = k < 2 ? k : fib(k - 1) + fib(k - 2)
def sq(x) = x * x
def norm2(x, y) = sq(x) + sq(y)
def sum(n)
  s = 0; i = 0
  while i < n
    s += norm2(i, 7)
    i += 1
  end
  s
end
def small?(k) = k < 2
def fibh(k) = small?(k) ? k : fibh(k - 1) + fibh(k - 2)
def quot(a, b) = a / b
def ratio(a, b) = quot(a, b) + 1
def acc(n)
  s = 0; i = 0
  while i < n
    s += sq(i)
    i += 1
  end
  s
end
def tot(n) = acc(n) + 1
class Shape
  def scale = 2
  def side(x) = x * scale
  def area(x) = side(x) * side(x)
end
class Wide < Shape
  def scale = 3
end
r = []
100.times { r << sum(50) << fibh(15) << ratio(10, 3) << Shape.new.area(4) << tot(50) }
r = r.uniq
r << norm2(4_000_000_000, 4_000_000_000) << norm2(1.5, 2)
r << (ratio(1, 0) rescue $!.class)
r << Wide.new.area(4)
Object.send(:remove_method, :sq)
def sq(x) = x + 1
r << sum(50) << norm2(1, 2) << tot(50)
Object.send(:remove_method, :small?)
def small?(k) = k < 3
r << fibh(15) << fib(20)
r.join(',')
";
        private const string JitCallsExpected = "42875,610,4,64,40426,32000000000000000000,6.25,ZeroDivisionError,144,1675,5,1276,987,6765";
    }
}
