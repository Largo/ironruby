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
        /// A block loop whose block holds another core iterator with a literal block runs the inner loop in place
        /// (JitCompiler.Nest.cs): a Float crossing the inner block, a bound of another type on a late outer
        /// iteration, and the backtrace of an error raised in the inner block.
        /// </summary>
        public void BlockLoopNested1() {
            TestOutput(@"
require 'ruby4'
class P; attr_accessor :x, :v; def initialize(x); @x = x; @v = 0.5; end; end
def pairs(ps); e = 0.0; ps.each_index { |i| a = ps[i]; ((i + 1)...ps.size).each { |j| b = ps[j]; d = a.x - b.x; e += d * d * a.v * b.v } }; e; end
def tt(n, m); s = 0; n.times { |i| m.times { |j| s += i * j } }; s; end
def sum(a, hi); s = 0; a.each_index { |i| t = i * 2; (i...hi).each { |j| s += j + t } }; s; end
ps = Array.new(8) { |i| P.new(i + 0.5) }
e = 0.0
3000.times { e += pairs(ps) }
p e
r = 0
3000.times { |k| r += tt(8, k == 2500 ? 2.5 : 3) rescue r += 1000 }
p r
r = 0
3000.times { |k| r += sum((0...8).to_a, k == 2900 ? 8.5 : 8) rescue r += 1000 }
p r
def rz(a, z); s = 0; a.each_index { |i| (0...3).each { |j| s += 10 / (z - j) } }; s
rescue ZeroDivisionError => e
  e.backtrace_locations[0, 5].map(&:label)
end
3000.times { rz((0...8).to_a, 9) }
p rz((0...8).to_a, 2)
def gw(n, m); c = 0; begin; n.times { |i| m.times { |j| c += 1; $gw = c } }; rescue RuntimeError; end; c - $gw; end
$gw = 0
300.times { gw(20, 20) }
p (1..3).map {
  q = Queue.new
  th = Thread.new { q << 1; gw(100_000, 50_000) }
  q.pop; sleep 0.05; th.raise(""stop"")
  d = th.value
  d == 0 || d == 1
}
def fl(flags, acc, n); n.times { |i| f = flags[0]; f.times { |j| acc[0] = acc[0] + j + 1 } }; end
5.times { fl([2], [0], 2000) }
flags = [0]; acc = [0]
th = Thread.new { sleep 0.03; Integer.class_eval { remove_method :times; def times; yield 98; self; end }; flags[0] = 1 }
fl(flags, acc, 30_000_000)
th.join
p [acc[0] % 99 == 0, acc[0] > 0]
", @"
252000.0
462916
1008120
[""Integer#/"", ""block (2 levels) in Object#rz"", ""Range#each"", ""block in Object#rz"", ""Array#each_index""]
[true, true, true]
[true, true]
");
        }

        /// <summary>
        /// A compiled loop that is running when another thread redefines a method it was built on - an outlined
        /// `while' inlining Array#size, a block loop over `times', a typed method body inlining a callee - calls the
        /// new method from its next iteration on (JitRuntime.OsrHandBack, MethodLoopSafePointSlow); it checked the
        /// method version only on entry before. The thread redefines, then sets the flag, and the loop counts only
        /// iterations after the one that saw the flag, so the output is CRuby 4.0's every run.
        /// </summary>
        public void JitRunningLoopRedefined1() {
            TestOutput(@"
# the redefinitions warn under -w, as in CRuby (""method redefined; discarding old size""); this is about the loops
$VERBOSE = false
def after_a_while(flag)
  Thread.new { |; t0|
    t0 = Process.clock_gettime(Process::CLOCK_MONOTONIC)
    Thread.pass while Process.clock_gettime(Process::CLOCK_MONOTONIC) - t0 < 0.05
    yield
    flag[0] = 1
  }
end
def w(a, flag)
  c = 0; seen = 0; after = 0
  while after < 1000
    if seen == 1
      c += a.size
      after += 1
    end
    seen = 1 if flag[0] == 1
  end
  c
end
def bt(a, flag)
  c = 0; seen = 0; after = 0
  while after < 1000
    100_000.times { |i| if seen == 1 && after < 1000; c += a.size; after += 1; end; seen = 1 if flag[0] == 1 }
  end
  c
end
class K
  def k(x) = x * 2
  def m(flag, n)
    c = 0; seen = 0; after = 0
    while after < n
      if seen == 1
        c += k(3)
        after += 1
      end
      seen = 1 if flag[0] == 1
    end
    c
  end
end
flag = [0]
th = after_a_while(flag) { Array.class_eval { alias_method :__size, :size; def size = 0 } }
p w([1, 2], flag); th.join
Array.class_eval { alias_method :size, :__size; remove_method :__size }
flag = [0]
th = after_a_while(flag) { Array.class_eval { alias_method :__size, :size; def size = 0 } }
p bt([1, 2], flag); th.join
Array.class_eval { alias_method :size, :__size; remove_method :__size }
o = K.new
3000.times { o.k(1); o.m([1], 10) }
flag = [0]
th = after_a_while(flag) { K.class_eval { def k(x) = 0 } }
p o.m(flag, 1000); th.join
p w([1, 2], [1])
", @"
0
0
0
2000
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
        /// Direct calls (on by default, -X:NoJITCalls): a method the JIT compiles calls the typed bodies of the
        /// other methods it calls on self directly. What the callee's entry would have guarded falls to the
        /// caller's: a callee redefined, overridden in a subclass, deopting, or passed a type it was not
        /// compiled for still gives CRuby 4.0's output.
        /// </summary>
        public void JitCalls1() {
            int bound = IronRuby.Runtime.Jit.JitRuntime.DirectCallBodies;
            AreEqual(Engine.CreateScriptSourceFromString(JitCallsScript, "jitcalls.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                JitCallsExpected);
            // the output is the same when nothing is bound: tot's call of acc, a callee with a loop, is one
            // the inliner never takes, and Shape#area's of side is another
            Assert(IronRuby.Runtime.Jit.JitRuntime.DirectCallBodies >= bound + 2);
            bound = IronRuby.Runtime.Jit.JitRuntime.DirectCallBodies;
            var engine = IronRuby.Ruby.CreateEngine((setup) => { setup.Options["JITCalls"] = false; });
            AreEqual(engine.CreateScriptSourceFromString(JitCallsScript, "jitcalls.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                JitCallsExpected);
            // and with -X:NoJITCalls nothing is
            Assert(IronRuby.Runtime.Jit.JitRuntime.DirectCallBodies == bound);
        }

        /// <summary>
        /// `private :x' or `protected :x' in a subclass leaves an entry there that forwards to the inherited x, and the
        /// method the resolution arrives at is the superclass's public one. A compiled `o.x', `o.x = v' or Struct member
        /// access asks the lookup for the visibility (JitRuntime.SelfAttrSite.Resolve) and makes the call, which raises
        /// CRuby 4.0's NoMethodError; asking the method, it read and wrote the variable. With -X:JITResume the
        /// attribute operations after an effect fall back to the same call.
        /// </summary>
        public void JitAttrVisibility1() {
            // (this file's line ends are CRLF, the script's "\n")
            string expected = JitAttrVisibilityExpected.Replace("\r", "");
            var engine = IronRuby.Ruby.CreateEngine((setup) => { setup.Options["JITResume"] = true; });
            AreEqual(engine.CreateScriptSourceFromString(JitAttrVisibilityScript, "jitattrvisibility.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                expected);
            AreEqual(Engine.CreateScriptSourceFromString(JitAttrVisibilityScript, "jitattrvisibility.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                expected);
        }

        private const string JitAttrVisibilityScript = @"
class VNode
  attr_accessor :val, :other, :n
  def initialize
    @val = 0
    @other = nil
    @n = 0
  end
  def peek
    @other.val
  end
  def poke
    @other.val = :w
  end
  def hold
    @n += 1
    @other.val = :h
    @other.val
  end
end
class VProtected < VNode
  protected :val, :val=
end
class VPrivate < VNode
  private :val, :val=
end
class VPublicAgain < VPrivate
  public :val, :val=
end
VStruct = Struct.new(:m)
class VStructPrivate < VStruct
  private :m, :m=
end
class VHolder
  attr_accessor :s
  def get
    @s.m
  end
  def set
    @s.m = 5
  end
end

$out = []
def t
  $out << yield.inspect
rescue NoMethodError => e
  $out << e.message
end

node = VNode.new
node.other = VNode.new
holder = VHolder.new
holder.s = VStruct.new(1)
# an error message's first use defines methods, which sends hot bodies back to profiling: once before the warm-up
t { nil.val }
i = 0
while i < 400
  node.peek
  node.poke
  node.hold
  holder.get
  holder.set
  i += 1
end
[VProtected, VPrivate, VPublicAgain].each do |c|
  node.other = c.new
  t { node.peek }
  t { node.poke }
  t { node.hold }
end
t { node.n }
holder.s = VStructPrivate.new(1)
t { holder.get }
t { holder.set }
$out.join(""\n"")
";

        private const string JitAttrVisibilityExpected = @"undefined method 'val' for nil
protected method 'val' called for an instance of VProtected
protected method 'val=' called for an instance of VProtected
protected method 'val=' called for an instance of VProtected
private method 'val' called for an instance of VPrivate
private method 'val=' called for an instance of VPrivate
private method 'val=' called for an instance of VPrivate
0
:w
:h
403
private method 'm' called for an instance of VStructPrivate
private method 'm=' called for an instance of VStructPrivate";

        /// <summary>
        /// -X:JITResume (Docs/jit-resume-deopt.md, stage 1, off by default): Richards' Scheduler#hold_self - an ivar
        /// increment, then an attribute write and an attribute read on another object - is specialized only under the
        /// switch, and then falls back to the call of each attribute operation whose guard fails after the first
        /// effect, without running the body again: a receiver of another class, a private writer, a frozen receiver
        /// and method_missing give CRuby 4.0's output, and the body goes on running typed afterwards.
        /// </summary>
        public void JitResume1() {
            int bodies = IronRuby.Runtime.Jit.JitRuntime.ResumeBodies;
            int fallbacks = IronRuby.Runtime.Jit.JitRuntime.FallbackCount;
            var engine = IronRuby.Ruby.CreateEngine((setup) => { setup.Options["JITResume"] = true; });
            AreEqual(engine.CreateScriptSourceFromString(JitResumeScript, "jitresume.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                JitResumeExpected);
            Assert(IronRuby.Runtime.Jit.JitRuntime.ResumeBodies == bodies + 1);
            // ROdd's writer and reader, RMissing's, RPrivate's writer, the frozen receiver's
            Assert(IronRuby.Runtime.Jit.JitRuntime.FallbackCount >= fallbacks + 6);
            // without the switch hold_self is declined (a deopt after an effect) and nothing falls back
            bodies = IronRuby.Runtime.Jit.JitRuntime.ResumeBodies;
            fallbacks = IronRuby.Runtime.Jit.JitRuntime.FallbackCount;
            AreEqual(Engine.CreateScriptSourceFromString(JitResumeScript, "jitresume.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                JitResumeExpected);
            Assert(IronRuby.Runtime.Jit.JitRuntime.ResumeBodies == bodies);
            Assert(IronRuby.Runtime.Jit.JitRuntime.FallbackCount == fallbacks);
        }

        private const string JitResumeScript = @"
class RTask
  attr_accessor :task_holding
  attr_reader :link
  def initialize(link)
    @link = link
    @task_holding = false
  end
end
class ROdd
  def task_holding=(v)
    $log << [:odd, v]
  end
  def link = :odd_link
end
class RPrivate
  attr_reader :link
  private
  def task_holding=(v)
    $log << [:private, v]
  end
end
class RMissing
  def method_missing(name, *args)
    $log << [name, args]
    :missing
  end
  def respond_to_missing?(*) = true
end
class RSched
  attr_accessor :hold_count, :current_task
  def initialize(task)
    @hold_count = 0
    @current_task = task
  end
  def hold_self
    @hold_count += 1
    @current_task.task_holding = true
    @current_task.link
  end
end
def probe(s, probes, frozen)
  r = []
  probes.each do |task|
    s.current_task = task
    r << (s.hold_self rescue $!.class)
    r << s.hold_count
  end
  r << frozen.task_holding << $log.inspect
  s.current_task = RTask.new(:again)
  r << s.hold_self << s.hold_count
end
frozen = RTask.new(:frozen_link).freeze
# the ones that raise last: an error message made defines methods, which sends the rest to the generic
# body (the entry's method version)
probes = [ROdd.new, RMissing.new, RPrivate.new, frozen]
# everything the probes run, once before anything is hot: a library class first used defines its methods
$log = []
probe(RSched.new(RTask.new(:first)), probes, frozen)
$log = []
s = RSched.new(RTask.new(:plain))
100.times { s.hold_self }
([s.hold_count, s.current_task.task_holding] + probe(s, probes, frozen)).join(',')
";
        private const string JitResumeExpected = "100,true,odd_link,101,missing,102,NoMethodError,103,FrozenError,104,false,[[:odd, true], [:task_holding=, [true]], [:link, []]],again,105";

        /// <summary>
        /// -X:JITMethods (a prototype, off by default): the method JIT's typed bodies as static methods of a runtime
        /// assembly, in each of its modes, alone and with direct calls, which are then calls of those
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
                    // with direct calls, and without them (-X:NoJITCalls)
                    setup.Options["JITCalls"] = mode.EndsWith("+");
                });
                AreEqual(engine.CreateScriptSourceFromString(JitCallsScript, "jitmethods.rb", Microsoft.Scripting.SourceCodeKind.File).Execute().ToString(),
                    JitCallsExpected);
                Assert(IronRuby.Runtime.Jit.JitMethodEmitter.Emitted > emitted);
                Assert(IronRuby.Runtime.Jit.JitMethodEmitter.Failed == failed);
            }
        }

        /// <summary>
        /// The census (IronRuby.jit_census, JitCensus; Util/perf/census.rb): what the light frames and the method JIT make
        /// of each method of a file that is parsed and not run. The verdicts are the ones a method's first call gets
        /// (MethodDefinition.TransformBody, JitStub.Wrap), so one that changes here has changed for programs as well:
        /// `never' is the parameter list's or the screen's refusal, `compiles' a typed body that needs no profile,
        /// `maybe' a body the screen passes and the profile decides.
        /// </summary>
        public void JitCensus1() {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ir-census-" + System.Guid.NewGuid().ToString("N") + ".rb");
            System.IO.File.WriteAllText(path, @"
$census_ran = true
class Point
  def add(a, b) = a + b
  def each; yield @x; end
  def log(*messages) = messages
  def safe
    @x.fetch
  rescue StandardError
    nil
  end
  def dist(o) = @x - o.x
  def self.zero = 0
end
def top = puts(1)
");
            try {
                TestOutput(@"
IronRuby.jit_census(['" + path.Replace('\\', '/') + @"']).each do |m|
  p [""#{m[:module]}#{m[:singleton] ? '.' : '#'}#{m[:name]}"", m[:line], m[:light], m[:light_why], m[:jit], m[:jit_stage], m[:jit_why], m[:jit_types]]
end
p defined?($census_ran)
p IronRuby.jit_census(['/no/such/census.rb']).map { |m| m.key?(:error) }
", @"
[""Point#add"", 4, true, nil, ""compiles"", nil, nil, ""Int""]
[""Point#each"", 5, true, nil, ""never"", ""parameters"", ""uses its block"", nil]
[""Point#log"", 6, true, nil, ""never"", ""parameters"", ""rest parameter"", nil]
[""Point#safe"", 7, true, nil, ""never"", ""screen"", ""Body"", nil]
[""Point#dist"", 12, true, nil, ""maybe"", ""emit"", ""call: x/0"", nil]
[""Point.zero"", 13, true, nil, ""compiles"", nil, nil, """"]
[""Object#top"", 15, true, nil, ""never"", ""screen"", ""MethodCall puts (self)"", nil]
nil
[true]
");
            } finally {
                System.IO.File.Delete(path);
            }
        }

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
