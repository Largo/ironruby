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

using System;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Builtins;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Generation;
using System.Collections.Generic;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Tests {

    public partial class Tests {
        public void Scenario_Globals1() {
            Context.DefineGlobalVariable("x", 123);
            CompilerTest(@"$z = $x");
            object z = Context.GetGlobalVariable("z");
            AssertEquals(z, 123);
        }

        public void Scenario_RubyLocals1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo()
  x = 1
  y = 2
  puts x + y
end

foo
");
            }, "3");
        }

        public void Scenario_UninitializedVars1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
  x = 1 if false
  puts x
end

foo
");
            }, @"");
        }

        public void Scenario_UninitializedVars2() {
            AssertExceptionThrown<MemberAccessException>(delegate() {
                CompilerTest(@"
def foo
  puts x
end

foo
");
            });
        }

        public void Scenario_RubyParams1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo(a,b)
    puts a + b
end

puts foo(1,2)
");
            },
            @"
3
");
        }

        public void Scenario_RubyParams2() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo(a, b, c = a + b, d = c + 1)
  puts a,b,c,d
end

foo(1,2) 
");
            },
            @"
1
2
3
4
");
        }

        public void Scenario_RubyMethodMissing1() {
            AssertOutput(delegate {
                CompilerTest(@"
class C
  def method_missing(name, *a)
    puts name, a
  end
end

C.new.foo 1,2,3
");
            }, @"
foo
1
2
3
");

        }
        
        public void Scenario_RubyMethodMissing2() {
            AssertExceptionThrown<MissingMethodException>(delegate {
                CompilerTest(@"unknown_method");
            }, delegate(MissingMethodException e) {
                return e.Message.StartsWith("undefined method");
            });
        }

        public void Scenario_RubySingletonConstants1() {
            AssertOutput(delegate {
                CompilerTest(@"
puts nil.to_s
puts true.to_s
puts false.to_s

puts nil | false
puts true & []
puts false ^ []
puts nil.nil?
puts nil.to_i
puts nil.to_f
");
            }, @"

true
false
false
true
true
true
0
0.0
");

        }

        public void Scenario_RubyMath1() {
            AssertOutput(delegate {
                CompilerTest(@"
puts Math.acos(1.0)
puts Math.acos(1)
");
            }, @"
0.0
0.0");

        }

        public void Scenario_RubyScopeParsing() {
            LoggingErrorSink log = new LoggingErrorSink();

            SourceUnitTree p;
            SourceUnit unit;

            unit = Context.CreateSnippet(@"
                class c << G
                end
            ", SourceCodeKind.File);

            p = new Parser().Parse(unit, new RubyCompilerOptions(), log);
            Assert(p == null && log.FatalErrorCount == 1);
            log.ClearCounters();

            unit = Context.CreateSnippet(@"
                def goo(&b)
                    end

                    class C
                        def foo()
                           x.goo() { 
                                goo() {
                                    class << M
                                        def bar()
                                            goo() {
                                            }
                                        end
                                    end
                                }
                           } 
                        end
                    end

                BEGIN { goo1() { } }
                END { goo2() { } } 
            ", SourceCodeKind.File);

            p = new Parser().Parse(unit, new RubyCompilerOptions(), ErrorSink.Null);
            Assert(p != null && !log.AnyError);
            log.ClearCounters();

            unit = Context.CreateSnippet(@"
                for x in array do
                    goo()
                end
            ", SourceCodeKind.File);

            p = new Parser().Parse(unit, new RubyCompilerOptions(), ErrorSink.Null);
            Assert(p != null && !log.AnyError);
            log.ClearCounters();
        }

        /// <summary>
        /// Tests that class lexical scopes are applied to method definitions.
        /// </summary>
        public void Scenario_RubyScopes1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class A; def foo; print 'A'; end; end
class B; def foo; print 'B'; end; end
A.new.foo
B.new.foo
");
            }, @"AB");
        }

        /// <summary>
        /// Uninitialized local variables.
        /// </summary>
        public void Scenario_RubyScopes2A() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
  x = 1 if false
  p x
end

foo
");
            }, @"nil");
        }

        /// <summary>
        /// Uninitialized local variables in closure.
        /// </summary>
        public void Scenario_RubyScopes2B() {
            TestOutput(@"
def foo
  x = 1 if false
  1.times { p x }
end

foo
",
@"nil"
);
        }

        /// <summary>
        /// Tests that variables defined in module/class locals scope are not visible outside.
        /// </summary>
        public void Scenario_RubyScopes3() {
            AssertOutput(delegate() {
                CompilerTest(@"
y = 'var'
class C
    x = 'var'
end

def x; 'method'; end
def y; 'method'; end

puts x
puts y
");
            }, @"
method
var");
        }

        public void Scenario_RubyScopes4() {
            AssertOutput(delegate() {
                CompilerTest(@"
module Bar
    class C
      module M
        puts self
      end
    end
end
");
            }, @"
Bar::C::M
");
        }

        public void Scenario_RubyScopes5() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  module Z
    E = self
  end
end

include M::Z

puts E
");
            }, @"
M::Z
");
        }

        /// <summary>
        /// Nested module scopes - they don't have DLR tuples and therefore need to be skipped when looking up a storage from closure.
        /// </summary>
        public void Scenario_RubyScopes6() {
            CompilerTest(@"
1.times {
  module M
    module N
      z = 1
      1.times {
        x = z
      }
    end
  end
}
");
        }

        public void NumericLiterals1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts(1)
puts(-1)
puts(+1)
puts(1.1)
puts(-1.1)
puts(+1.1)

x = 2.0
puts x
puts 2.0
puts 2.1560
");
            }, @"
1
-1
1
1.1
-1.1
1.1
2.0
2.0
2.156
");
        }

        public void NumericOps1() {
            // Overflow tests. What overflows an Int32 is now an Int64, not a BigInteger - only a
            // value too wide for a long reaches BigInteger.
            Assert((long)ClrInteger.Minus(Int32.MinValue) == -(long)Int32.MinValue);
            Assert((long)ClrInteger.Abs(Int32.MinValue) == -(long)Int32.MinValue);

            Assert((long)ClrInteger.Divide(Int32.MinValue, -1) == -(long)Int32.MinValue);
            Assert(ClrInteger.Modulo(Int32.MinValue, -1) == 0);

            var dm = ClrInteger.DivMod(Int32.MinValue, -1);
            Assert((long)dm[0] == -(long)Int32.MinValue);
            Assert((int)dm[1] == 0);

            Assert((int)ClrInteger.LeftShift(1, Int32.MinValue) == 0);
            AssertExceptionThrown<ArgumentOutOfRangeException>(() => ClrInteger.RightShift(1, Int32.MinValue));
        }

        public void Scenario_RubyInclusions1() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  def foo
    puts 'foo'
  end
end

class C
  include M
end

C.new.foo
");
            }, "foo");
        }

        public void Scenario_RubyClassVersions1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def m
    puts 'v1'
  end
end

C.new.m

class C
  def m
    puts 'v2'
  end
end

C.new.m
");
            },
@"
v1
v2
");
        }

        public void Scenario_RubyClassVersions2() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  def m; puts 'v1'; end
end

module N
  def m; puts 'v2'; end
end

class C
  include M
end

class D < C 
end

D.new.m

class D
  include N
end

D.new.m
");
            },
@"
v1
v2
");
        }

        public void InvokeMemberCache1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def try_call
  B.m rescue puts 'error' 
end

class B; end

try_call       # m not defined

class Class
  def m; puts 'ok' end
end

try_call       # m defined on Class
");
            },
@"
error
ok
");
        }

        public void Scenario_RubyBlockExpressions1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts class Foo
  'A'
end

puts def bar
  'B'
end

puts module M
  'C'
end

puts begin
  'D'
end

x = ('E'; 'F')
puts x
");
            }, @"
A
bar
C
D
F");
        }

        public void Scenario_ClassVariables1() {
            TestOutput(@"
module M
  @@m = 1
  
  def goo
    @@n = 2
  end
end

class C
  include M

  def foo
    @@a = 1
  end
  
  @@b = 2
  @@c = 1
end

class D < C
  def bar
    @@c = 3
  end
  
  @@d = 4
end

C.new.foo
D.new.bar
C.new.goo

p C.class_variables.sort
p D.class_variables.sort
p M.class_variables.sort
", @"
[:@@a, :@@b, :@@c, :@@m, :@@n]
[:@@a, :@@b, :@@c, :@@d, :@@m, :@@n]
[:@@m, :@@n]
");  // class_variables includes inherited ones by default (as CRuby does)
        }

        public void Scenario_ClassVariables2() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  @@x = 1
end

class D < C
  remove_class_variable :@@x rescue puts 'Error'
  @@y = 'foo'
  puts remove_class_variable(:@@y)
end
");
            }, @"
Error
foo
");
        }



        public void Return1() {
            TestOutput(@"
def foo()
  return 123
end

p foo
", @"
123
");
        }

        public void Return2() {
            TestOutput(@"
def f1
  return *[1,2],*[3,4],5,*[6],*[],*7,*nil
end

def f2
  return 1,2,3, a: 2, b: 3
end

def f3
  return :a => 2, :b => 3
end

def f4
  a = [2,3]
  return 1, *a, :a => 2, :b => 3
end

p f1, f2, f3, f4
", @"
[1, 2, 3, 4, 5, 6, 7]
[1, 2, 3, {:a=>2, :b=>3}]
{:a=>2, :b=>3}
[1, 2, 3, {:a=>2, :b=>3}]
");
        }
        
        public void Return3() {
            TestOutput(@"
x = while true do
  break 1,2,3
end
puts x
", @"
1
2
3
");
        }

        public void Return4() {
            TestOutput(@"
x = while true do
  break 1 => 2, 3 => 4
end
puts x
", @"
{1=>2, 3=>4}
");
        }

        public void Return5() {
            TestOutput(@"
x = while true do
  break 'a', 'b', 1 => 2, 3 => 4
end
puts x
", @"
a
b
{1=>2, 3=>4}
");
        }

        public void Return6() {
            TestOutput(@"
x = while true do
  break 'a', 'b', *['A', 'B'], 1 => 2, 3 => 4
end
puts x
", @"
a
b
A
B
{1=>2, 3=>4}
");
        }

        public void Return7() {
            TestOutput(@"
def foo
  return 1,2,3, *[6,7], 4 => 5
end
puts foo
", @"
1
2
3
6
7
{4=>5}
");
        }

        public void Return8() {
            TestOutput(@"
def foo
  return *$x = [1,2]
end
$y = foo

puts $x.object_id == $y.object_id
puts $x.inspect
", @"
true
[1, 2]
");
        }

        public void Scenario_RubyClosures1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
    y = 10
    3.times { |x| puts x + y }
end

foo
");
            }, @"
10
11
12");
        }
        
        public void ClassVariables1() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  @@m = 3
  def moo
    puts @@m
    puts @@a rescue puts '!a'
  end
end

class A
  @@a = 1

  include M  

  class B
    @@b = 2
    def foo
      puts @@b
    end
  end  

  def foo
    puts @@a
  end
end

x = A.new
y = A::B.new
x.foo
x.moo
y.foo
");
            }, @"
1
3
!a
2
");
        }

        public void Scenario_RubyThreads1() { 
            AssertOutput(delegate() {
                CompilerTest(@"
t = Thread.new 1,2,3 do |a,b,c| 
    puts a,b,c 
end
t.join
puts 4
");
            }, @"
1
2
3
4
");
        }

        public void Scenario_YieldCodeGen() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
    puts 1, yield
end
foo { 2 }
");
            }, @"
1
2
");
        }

        public void Scenario_MainSingleton() { 
            AssertOutput(delegate() {
                CompilerTest(@"
require 'mscorlib'
include System::Collections
puts ArrayList
");
            }, @"System::Collections::ArrayList");
        }

        /// <summary>
        /// Time._load hands a dumped zone name to the class's find_timezone. As in MRI, a StandardError from the
        /// lookup leaves the name and anything else goes through; only bytes that are not a time are a TypeError.
        /// </summary>
        public void Time_LoadZone1() {
            TestOutput(@"
class Zone
  attr_reader :name
  def initialize(name); @name = name; end
  def utc_to_local(t); t + 3600; end
  def local_to_utc(t); t - 3600; end
end

class ZTime < Time
  def self.find_timezone(name)
    $lookup.call(name)
  end
end

class ZoneMissing < StandardError; end
class ZoneFailure < Exception; end

xst = Zone.new('XST')
dump = Marshal.dump(ZTime.new(2000, 1, 1, 12, 0, 0, xst))

$lookup = lambda { |name| xst }
t = Marshal.load(dump)
p t, t.zone.equal?(xst)

$lookup = lambda { |name| raise ArgumentError, 'unknown zone' }
t = Marshal.load(dump)
p t, t.zone, t.zone.frozen?

$lookup = lambda { |name| raise ZoneMissing }
p Marshal.load(dump).zone

$lookup = lambda { |name| throw :zone, name }
p catch(:zone) { Marshal.load(dump) }

$lookup = lambda { |name| raise ZoneFailure }
begin
  Marshal.load(dump)
rescue ZoneFailure => e
  p e.class
end

# 2000-16-01: the month is out of range
bad = [0x80000000 | (100 << 14) | (15 << 10) | (1 << 5), 0].pack('VV')
begin
  Time.send(:_load, bad)
rescue TypeError => e
  p e.message
end
", @"
2000-01-01 12:00:00 +0100
true
2000-01-01 12:00:00 +0100
""XST""
true
""XST""
""XST""
ZoneFailure
""marshaled time format differ""
");
        }

        /// <summary>
        /// A time loaded from a dump keeps the zone name as a String when no find_timezone makes an object of it. As in
        /// MRI, dumping it again writes the name as it is, and strftime's %Z prints it; the fixed offset has no name.
        /// </summary>
        public void Time_DumpLoadedZone1() {
            TestOutput(@"
class Zone
  attr_reader :name
  def initialize(name); @name = name; end
  def utc_to_local(t); t + 3600; end
  def local_to_utc(t); t - 3600; end
end

class NoZoneTime < Time
  def self.find_timezone(name); nil; end
end

xst = Zone.new('XST')
t = Time.new(2000, 1, 1, 12, 0, 0, xst)

twice = Marshal.load(Marshal.dump(Marshal.load(Marshal.dump(t))))
p twice, twice.zone, twice.utc_offset, twice == t
p twice.strftime('%H:%M %z %Z'), (twice + 60).strftime('%H:%M %z %Z')

t = Marshal.load(Marshal.dump(Marshal.load(Marshal.dump(NoZoneTime.new(2000, 1, 1, 12, 0, 0, xst)))))
p t.zone, t.strftime('%Z')
", @"
2000-01-01 12:00:00 +0100
""XST""
3600
true
""12:00 +0100 XST""
""12:01 +0100 XST""
""XST""
""XST""
");
        }
    
        /// <summary>
        /// MRI's dtoa settles a rounding it cannot tell from a tie in double arithmetic by the
        /// parity of the last digit, whatever the exact binary value: "%.2f" % 2.675 is 2.68
        /// although the double is below 2.675, and "%.1f" % 99.45 is 99.4 although it is above.
        /// "%g" then keeps the trailing zero dtoa leaves on such a value. The exponent of a value
        /// a few ulps below a power of ten is exact, not Math.Log10's rounding of it.
        /// </summary>
        public void Sprintf_MriTieRounding1() {
            TestOutput(@"
puts '%.2f' % 2.675, '%.1f' % 99.45, '%.1f' % 0.05, '%.0f' % 2.5, '%.0f' % 1.5
puts '%.2e' % 2.675, '%.0e' % 0.95, '%.1g' % 0.095
puts '%.5g' % 0.0676905, '%.5g' % 0.0676895, '%.2g' % 205.0, '%.2g' % 204.0
puts '%.15e' % 999.9999999999999, '%.16e' % 1.0e-07
puts '%.3f' % 1.0005, '%.2f' % 1.005, '%.1f' % 0.25, '%.17g' % 0.1, '%g' % 1234567.0, '%#.3g' % 1.0
", @"
2.68
99.4
0.1
2
2
2.68e+00
1e+00
0.1
0.067690
0.06769
2.0e+02
2e+02
9.999999999999999e+02
9.9999999999999995e-08
1.000
1.00
0.2
0.10000000000000001
1.23457e+06
1.00
");
        }

        /// <summary>Float#-@ negates the sign bit; Numeric#-@ (0 - x) lost it for 0.0.</summary>
        public void Float_NegativeZero1() {
            TestOutput(@"
f = 0.0
p(-f, 1.0 / -f, (-f).to_s, -(-f))
", @"
-0.0
-Infinity
""-0.0""
0.0
");
        }

        /// <summary>
        /// Math.cbrt is the C library's cbrt with MRI's correction step, and Math.gamma of a
        /// whole number comes from MRI's factorial table.
        /// </summary>
        public void Math_CLibrary1() {
            TestOutput(@"
p Math.cbrt(-27.0), Math.cbrt(27.0), Math.gamma(3.0), Math.gamma(23.0), Math.erf(0.0), Math.erfc(0.0)
", @"
-3.0
3.0
2.0
1.1240007277776077e+21
0.0
1.0
");
        }
    
        /// <summary>
        /// An Errno message built with the error's own text in it is not prefixed with that text
        /// again: "No such file or directory - path", not "No such file or directory - No such
        /// file or directory - path".
        /// </summary>
        public void Errno_MessageNotDoubled1() {
            TestOutput(@"
puts Errno::ENOENT.new.message
puts Errno::ENOENT.new('x').message
puts Errno::ENOENT.new('No such file or directory - x').message
puts Errno::ENOENT.new('No such file or directory @ rb_sysopen - x').message
", @"
No such file or directory
No such file or directory - x
No such file or directory - x
No such file or directory @ rb_sysopen - x
");
        }

        public void Numeric_BignumFloatOverflow1() {
            TestOutput(@"
x = 10**400
p x * 1.0, x + 1.0, x - 1.0, x / 10.0, x.fdiv(10**399), x.fdiv(10**401) <= 1e309, (-x) * 1.0
p x <=> 1e308, x > 1e308, x == 1.0 / 0
", @"
Infinity
Infinity
Infinity
Infinity
10.0
true
-Infinity
1
true
false
");
        }

        public void Numeric_BignumExponent1() {
            TestOutput(@"
p 1 ** (2**70), (-1) ** (2**70), (-1) ** (2**70 + 1), 0 ** (2**70)
begin; 2 ** (2**70); rescue ArgumentError => e; p e.message; end
begin; (2**70) ** (2**70); rescue ArgumentError => e; p e.message; end
begin; 5 / 0; rescue ZeroDivisionError => e; p e.message; end
begin; (2**70) % 0; rescue ZeroDivisionError => e; p e.message; end
", @"
1
1
-1
0
""exponent is too large""
""exponent is too large""
""divided by 0""
""divided by 0""
");
        }

        public void Float_Eql1() {
            TestOutput(@"
nan = 0.0 / 0.0
p nan.eql?(nan), nan == nan, 1.0.eql?(1.0), 1.0.eql?(1), 1.0.eql?(1.5), 1.0.eql?(nil)
", @"
false
false
true
false
false
false
");
        }

        public void ComparisonError_AnonymousClass1() {
            TestOutput(@"
c = Class.new
x = c.new
begin; 1 < x; rescue ArgumentError => e; puts e.message.sub(/0x[0-9a-f]+/, 'ID'); end
begin; 'a' < x; rescue ArgumentError => e; puts e.message.sub(/0x[0-9a-f]+/, 'ID'); end
begin; 'a' < 1; rescue ArgumentError => e; puts e.message; end
begin; 1 < 'a'; rescue ArgumentError => e; puts e.message; end
", @"
comparison of Integer with #<Class:ID> failed
comparison of String with #<Class:ID> failed
comparison of String with 1 failed
comparison of Integer with String failed
");
        }

        public void Array_FlattenNil1() {
            TestOutput(@"
p [1, [2, [3]]].flatten(nil), [1, [2, [3]]].flatten(1.5), [1, [2]].flatten!(nil), [1].flatten!(nil)
begin; ""\xFF"".tr('', ''); rescue ArgumentError => e; p e.message; end
begin; ""\xFF"".tr('a', 'b'); rescue ArgumentError => e; p e.message; end
p ""\xFF"".force_encoding('BINARY').tr('a', 'b').bytes
", @"
[1, 2, 3]
[1, 2, [3]]
[1, 2]
nil
""invalid byte sequence in UTF-8""
""invalid byte sequence in UTF-8""
[255]
");
        }

        public void Hash_ValuesAtDefault1() {
            TestOutput(@"
p Hash.new(5).values_at(:x, :y), Hash.new { |h, k| k.to_s }.values_at(:a), {a: 1}.values_at(:a, :b)
h = {a: 1}
begin; h.each { h.rehash }; rescue RuntimeError => e; p e.message; end
begin; Hash.new(&lambda { |k| k }); rescue TypeError => e; p e.message; end
p Hash.new(&lambda { |h, k| k })[:z], Hash.new(&lambda { |*a| a.size })[:z]
", @"
[5, 5]
[""a""]
[1, nil]
""rehash during iteration""
""default_proc takes two arguments (2 for 1)""
:z
2
");
        }

        public void Lambda_ArityMessages1() {
            TestOutput(@"
begin; ->(a, *b) { }.call; rescue ArgumentError => e; p e.message; end
begin; ->(a) { }.call(1, 2); rescue ArgumentError => e; p e.message; end
begin; :to_s.to_proc.call; rescue ArgumentError => e; p e.message; end
p :to_s.to_proc.call(1)
", @"
""wrong number of arguments (given 0, expected 1+)""
""wrong number of arguments (given 2, expected 1)""
""no receiver given""
""1""
");
        }

        public void ErrorMessages_Receivers1() {
            TestOutput(@"
o = Object.new
def o.inspect; 'INSPECTED'; end
begin; o.foo; rescue NoMethodError => e; puts e.message.sub(/0x[0-9a-f]+/, 'ID'); end
begin; self.foo; rescue NoMethodError => e; puts e.message; end
c = Class.new { def self.inspect; 'INS'; end }
begin; c.new.foo; rescue NoMethodError => e; puts e.message; end
begin; Class.new { def m; end }.instance_method(:m).bind(1); rescue TypeError => e; puts e.message.sub(/0x[0-9a-f]+/, 'ID'); end
begin; Object.const_get('Foo::'); rescue NameError => e; puts e.message; end
begin; Object.const_get('Math::PI::X'); rescue TypeError => e; puts e.message; end
begin; Object.send(:remove_const, :foo); rescue NameError => e; puts e.message; end
begin; Object.new.singleton_class::Foo; rescue NameError => e; puts e.message; end
begin; Math::Foo; rescue NameError => e; puts e.message; end
", @"
undefined method `foo' for #<Object:ID>
undefined method `foo' for main
undefined method `foo' for an instance of INS
bind argument must be an instance of #<Class:ID>
wrong constant name Foo::
Math::PI::X does not refer to class/module
'foo' is not allowed as a constant variable name
uninitialized constant Foo
uninitialized constant Math::Foo
");
        }

        public void Time_StringParsing1() {
            TestOutput(@"
p Time.new('2024-01-02 03:04:05 UTC').utc?, Time.new('2024-01-02 03:04:05.5 Z').usec, Time.new('2024-01-02T03:04:05+09:00').utc_offset
['2024-01-02 03:04', '2024-01-02 03', '2024-01-02 03:04:05.', '24-01-02', '2024-1-2', '2024-01-02 03:04:05 +09:00 x', ""2024-01-02 03:04:05\n"", 'x'].each do |s|
  begin; Time.new(s); rescue ArgumentError => e; puts e.message; end
end
begin; Time.new(2024, 1, 1, 24, 1); rescue ArgumentError => e; puts e.message; end
begin; Time.new(2024, 0, 1); rescue ArgumentError => e; puts e.message; end
begin; Time.new(2024, 1, 0); rescue ArgumentError => e; puts e.message; end
begin; Time.new(2024, 1, 1, 1, 1, '1.5'); rescue ArgumentError => e; puts e.message; end
begin; Time.at(1.5).round(-1); rescue ArgumentError => e; puts e.message; end
begin; Time.at(2**62); rescue RangeError => e; puts e.class; end
begin; Time.at(0) + (0.0 / 0.0); rescue FloatDomainError => e; puts e.message; end
p Time.at(-1.5).round.to_i, Time.at(2.5).round.to_i
", @"
true
500000
32400
missing sec part: 03:04
missing min part: 03
subsecond expected after dot: 03:04:05.
year must be 4 or more digits: 24
two digits mon is expected after '-': -1-2
can't parse at: x
can't parse: ""2024-01-02 03:04:05\n""
can't parse: ""x""
min out of range
mon out of range
mday out of range
invalid value for Integer(): ""1.5""
negative ndigits given
RangeError
NaN
-1
3
");
        }

        public void Struct_IndexMessages1() {
            TestOutput(@"
s = Struct.new(:a).new(1)
begin; s[1]; rescue IndexError => e; puts e.message; end
begin; s[-2]; rescue IndexError => e; puts e.message; end
", @"
offset 1 too large for struct(size:1)
offset -2 too small for struct(size:1)
");
        }

        public void Backtrace_RaiseInRescue1() {
            // an exception raised inside a rescue clause keeps the frames of the rescuing method and its callers
            TestOutput(@"
def m
  begin; raise 'inner'; rescue; raise 'outer'; end
end
def n
  m
end
begin
  n
rescue => e
  puts e.backtrace.first(2).map { |l| l[/in [`'](.*)'/, 1] }
  puts e.cause.message
end
begin
  begin; raise 'a'; rescue; raise 'b'; end
rescue => e
  puts(e.backtrace[0] =~ /Compiler|Test/ ? 'host' : 'ruby')
end
begin; raise ArgumentError, 'x', 1; rescue TypeError => e; puts e.message; end
e = RuntimeError.new('u')
begin; e.set_backtrace(1); rescue TypeError => t; puts t.message; end
puts UncaughtThrowError.new(:t, 1, 'tag').tag.inspect
begin; throw :zz; rescue UncaughtThrowError => u; puts u.message, u.tag.inspect; end
", @"
m
n
inner
ruby
backtrace must be Array of String
backtrace must be Array of String
:t
uncaught throw :zz
:zz
");
        }

        public void Thread_JoinSelf1() {
            TestOutput(@"
t = Thread.new { Thread.current.join rescue $!.message }; puts t.value
t = Thread.new { Thread.main.join(0) rescue $!.message }; puts t.value
t = Thread.new { Thread.current.value rescue $!.message }; puts t.value
begin; Thread.current.join; rescue ThreadError => e; puts e.message; end
puts ThreadGroup.new.inspect.sub(/0x\h+/, 'X')
p Thread.respond_to?(:critical)
", @"
Target thread must not be current thread
Target thread must not be main thread
Target thread must not be current thread
Target thread must not be current thread
#<ThreadGroup:X>
false
");
        }

        public void Hash_SelfKey1() {
            TestOutput(@"
h = { b: 1 }; h[h] = 2
h.each { |k, v| puts ""#{k.equal?(h)} #{v}"" }
h.each_key { |k| puts k.equal?(h) }
h.each_value { |v| puts v }
h2 = { a: 1, b: 2 }
h2.each { |k, v| h2.delete(:b) if k == :a; puts k }
p({}.respond_to?(:index))
begin; (lambda { |x, y| [x, y] } << proc { |x| [x, x] }).call(3); rescue ArgumentError => e; puts e.message; end
p((proc { |x, y| [x, y] } << proc { |x| [x, x] }).call(3))
p :upcase.to_proc.equal?(:upcase.to_proc), :upcase.to_proc.call('a')
", @"
false 1
true 2
false
true
1
2
a
false
wrong number of arguments (given 1, expected 2)
[3, 3]
true
""A""
");
        }

        public void Marshal_Misc1() {
            TestOutput(@"
o = Object.new; o.instance_variable_set(:@a, o)
x = Marshal.load(Marshal.dump(o)); p x.instance_variable_get(:@a).equal?(x)
S = Struct.new(:a)
p Marshal.dump(S).bytes
begin; Marshal.dump(Class.new.new); rescue TypeError => e; puts e.message.sub(/0x\h+/, 'X'); end
begin; Marshal.dump(Class.new(String).new('a')); rescue TypeError => e; puts e.message.sub(/0x\h+/, 'X'); end
class Foo; def marshal_dump; self; end; end
begin; Marshal.dump(Foo.new); rescue RuntimeError => e; puts e.message; end
class Bar; def respond_to?(m, p = false); m == :marshal_dump ? true : super; end; def method_missing(m, *a); m == :marshal_dump ? 1 : super; end; end
p Marshal.dump(Bar.new).bytes
p Marshal.load(""\x04\x08f\x071e""), Marshal.load(""\x04\x08f\x06a""), Marshal.load(""\x04\x08/\x06ai\x00"").options
begin; Marshal.load(""\x04\x08[\xffi\x06""); rescue ArgumentError => e; puts e.message; end
p Marshal.load(Marshal.dump(/a/n)).options, Marshal.dump(/a/n).bytes[6]
cb = Object.new; def cb.call(x); x.is_a?(String) ? x.upcase : x; end
p Marshal.load(Marshal.dump([1, 'a']), cb)
src = Object.new; def src.to_str; ""\x04\x08i\x06""; end
p Marshal.load(src)
rd = Object.new; def rd.read(n); ''; end
begin; Marshal.load(rd); rescue TypeError => e; puts e.message; end
begin; Marshal.load(""\x04\x08u:\x0bString\x06a""); rescue TypeError => e; puts e.message; end
begin; Marshal.load(""\x04\x08c\x0fComparable""); rescue ArgumentError => e; puts e.message; end
p [0.0 / 0].pack('e').bytes, [1].pack('C', buffer: nil).bytes
begin; [1].pack('C', bogus: 1); rescue ArgumentError => e; puts e.message; end
", @"
true
[4, 8, 99, 6, 83]
can't dump anonymous class #<Class:X>
can't dump anonymous class #<Class:X>
Foo#marshal_dump returned same class instance
[4, 8, 85, 58, 8, 66, 97, 114, 105, 6]
1.0
0.0
33
negative array size (or size too big)
32
32
[1, ""A""]
1
instance of IO needed
class String needs to have method `_load'
Comparable does not refer to class
[0, 0, 192, 127]
[1]
unknown keyword: :bogus
");
        }

        public void Encoding_Misc1() {
            TestOutput(@"
s = ""a\xffb"".reverse
p s.bytes, s.inspect, s.dump
p ""\x81\x40"".force_encoding(""Shift_JIS"").ord, 0x80.chr(""Windows-1252"").ord, ""\xe9"".force_encoding(""ISO-8859-1"").ord
p ""a"".casecmp(""A"".encode(""UTF-16LE"")), """".casecmp("""".encode(""UTF-16LE"")), ""a"".casecmp?(""A"")
begin; ""a\xffb"".upcase; rescue ArgumentError => e; puts e.message; end
p ""a\xffb"".upcase(:ascii).bytes
begin; Encoding.find(nil); rescue TypeError => e; puts e.message; end
p Encoding::UTF_8.frozen?, """".encode(""UTF-16"").bytes, ""a"".encode(""UTF-16"").bytes, ""\u00e9"".encode(""UTF-8"", nil)
begin; Time.now.strftime(""%Y"".encode(""UTF-16LE"")); rescue ArgumentError => e; puts e.message; end
", @"
[98, 255, 97]
""\""b\\xFFa\""""
""\""b\\xFFa\""""
33088
128
233
nil
0
true
input string invalid
[65, 255, 66]
no implicit conversion of nil into String
true
[]
[254, 255, 0, 97]
""é""
format should have ASCII compatible encoding
");
        }

        public void Io_Messages1() {
            TestOutput(@"
r, w = IO.pipe
w.write(""ab\ncd\n\nef""); w.close
p r.gets("""", nil), r.gets(nil, nil)
begin; r.seek(0, 9); rescue SystemCallError => e; puts e.class; end
begin; ""a"" * nil; rescue TypeError => e; puts e.message; end
begin; Array.new(nil); rescue TypeError => e; puts e.message; end
begin; File.open(""/nonexistent/x"", ""wb"", textmode: true); rescue ArgumentError => e; puts e.message; end
begin; File.open(""/nonexistent/x"", ""rt"", textmode: true); rescue ArgumentError => e; puts e.message; end
begin; File.open(""/nonexistent/x"", ""r"", invalid: :bogus); rescue ArgumentError => e; puts e.message; end
begin; File.open(""/nonexistent/x"", ""r"", newline: :bogus); rescue ArgumentError => e; puts e.message; end
print ""a"", nil, 1; puts
$stdout.putc(""ab""); $stdout.putc(""""); puts
begin; $stdout.putc(nil); rescue TypeError => e; puts e.message; end
", @"
""ab\ncd\n\n""
""ef""
Errno::EINVAL
no implicit conversion of nil into Integer
no implicit conversion of nil into Integer
both textmode and binmode specified
textmode specified twice
unknown value for invalid character option
unexpected value for newline option: bogus
a1
a
no implicit conversion of nil into Integer
");
        }

        public void Enumerator_Copy1() {
            TestOutput(@"
e = [1, 2].each
p e.dup.to_a, e.clone.to_a, [1, 2, 3].each_slice(2).dup.to_a
p Float.ancestors.include?(Precision), Float.ancestors[0, 3].map(&:to_s)
$VERBOSE = nil; p $-W
$VERBOSE = false; p $-W
$VERBOSE = true; p $-W
begin; $-W = 1; rescue NameError => x; puts x.message; end
", @"
[1, 2]
[1, 2]
[[1, 2], [3]]
false
[""Float"", ""Numeric"", ""Comparable""]
0
1
2
$-W is a read-only variable
");
        }
    }
}
