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
using System.Collections;
using System.Collections.Generic;

namespace IronRuby.Tests {
    /// <summary>
    /// Ruby protocols on CLR objects: delegates as callables, tuples and pairs as arrays, hash patterns
    /// over CLR members, IComparable&lt;T&gt; as Comparable, tasks and async streams.
    /// </summary>
    public partial class Tests {

        #region Delegates

        public static class ProtocolDelegates1 {
            public delegate string Formatter(string text, int width = 10, params object[] extra);
            public delegate bool Parser(string text, out int value);

            public static Func<int, int> Doubler() { return x => x * 2; }
            public static Func<int, int, int> Adder() { return (a, b) => a + b; }
            public static Func<string> Constant() { return () => "const"; }
            public static Comparison<int> Descending() { return (a, b) => b.CompareTo(a); }
            public static Predicate<string> IsEmpty() { return s => s.Length == 0; }
            public static Action<string> Recorder(List<string> log) { return s => log.Add(s); }
            public static Formatter MakeFormatter() { return (text, width, extra) => text.PadLeft(width) + extra.Length; }
            public static Parser MakeParser() { return (string text, out int value) => Int32.TryParse(text, out value); }

            public static int Apply(Func<int, int> f, int x) { return f(x); }
        }

        /// <summary>
        /// A delegate answers the calls a Proc answers, and they all go through Invoke, converting the arguments.
        /// </summary>
        public void ClrDelegateCall1() {
            Context.ObjectClass.SetConstant("D", Context.GetClass(typeof(ProtocolDelegates1)));
            TestOutput(@"
dbl = D.doubler
p dbl.invoke(5), dbl.call(5), dbl.(5), dbl[5], dbl.yield(5), dbl === 5
p D.constant.call, D.constant[]

log = System::Collections::Generic::List[System::String].new
D.recorder(log).call('x')
p log.to_a

case ''
when D.is_empty then puts 'empty'
end
p %w[a b].grep(D.is_empty)

p((D.adder.call(1) rescue $!))
p((D.adder.call(1, 2, 3) rescue $!))
p((dbl.call('x') rescue $!.class))
p dbl.respond_to?(:call), dbl.method(:call).call(4)
", @"
10
10
10
10
10
10
'const'
'const'
['x']
empty
[]
#<ArgumentError: wrong number of arguments (given 1, expected 2)>
#<ArgumentError: wrong number of arguments (given 3, expected 2)>
TypeError
true
8
");
        }

        /// <summary>
        /// Arity and parameters describe Invoke the way Method#arity and Method#parameters describe a method.
        /// </summary>
        public void ClrDelegateSignature1() {
            Context.ObjectClass.SetConstant("D", Context.GetClass(typeof(ProtocolDelegates1)));
            TestOutput(@"
[D.doubler, D.adder, D.constant, D.descending, D.is_empty, D.make_formatter, D.make_parser].each do |d|
  p [d.arity, d.parameters, d.lambda?]
end
p D.make_formatter.call('ab', 4)
", @"
[1, [[:req, :arg]], true]
[2, [[:req, :arg1], [:req, :arg2]], true]
[0, [], true]
[2, [[:req, :x], [:req, :y]], true]
[1, [[:req, :obj]], true]
[-2, [[:req, :text], [:opt, :width], [:rest, :extra]], true]
[1, [[:req, :text]], true]
'  ab0'
");
        }

        /// <summary>
        /// to_proc makes a lambda that invokes the delegate, which is what &amp;delegate passes as a block.
        /// </summary>
        public void ClrDelegateToProc1() {
            Context.ObjectClass.SetConstant("D", Context.GetClass(typeof(ProtocolDelegates1)));
            TestOutput(@"
dbl = D.doubler
pr = dbl.to_proc
p pr.class, pr.lambda?, pr.arity, pr.parameters, pr.call(3)
p((pr.call(1, 2) rescue $!))
p((D.constant.to_proc.call(1) rescue $!))

p [1, 2, 3].map(&dbl)
def m; yield 5; end
p m(&dbl)
p [3, 1, 2].sort(&D.descending)
p [3, 1, 2].max(&D.descending)
p D.apply(dbl, 4), D.apply(dbl.to_proc, 4)

define_method(:twice, &dbl)
p twice(8)

# a delegate made from a Ruby block goes back to being a Ruby callable
f = System::Func[Integer, Integer].new { |x| x + 100 }
p f.invoke(1), f.call(1), f.to_proc.call(1), [1, 2].map(&f), f.arity
p D.apply(f.to_proc, 1)
", @"
Proc
true
1
[[:req, :arg]]
6
#<ArgumentError: wrong number of arguments (given 2, expected 1)>
#<ArgumentError: wrong number of arguments (given 1, expected 0)>
[2, 4, 6]
10
[3, 2, 1]
1
8
8
16
101
101
101
[101, 102]
1
101
");
        }

        /// <summary>
        /// curry, &gt;&gt; and &lt;&lt; are Proc's applied to the delegate's #to_proc, as Method's are.
        /// </summary>
        public void ClrDelegateCompose1() {
            Context.ObjectClass.SetConstant("D", Context.GetClass(typeof(ProtocolDelegates1)));
            TestOutput(@"
dbl = D.doubler
inc = ->(x) { x + 1 }
p((dbl >> inc).call(5), (dbl << inc).call(5), (inc >> dbl).call(5), (inc << dbl).call(5))
p((dbl >> dbl).call(1))
p((dbl >> inc).lambda?, (dbl << inc).lambda?, (dbl << proc { |x| x }).lambda?)
p((dbl >> 5 rescue $!))
p((dbl << nil rescue $!))

add = D.adder
p add.curry[1][2], add.curry(2)[3][4], add.curry.lambda?
p((add.curry(3) rescue $!))
p dbl.curry[21]
", @"
11
12
12
11
4
true
true
false
#<TypeError: callable object is expected>
#<TypeError: callable object is expected>
3
7
true
#<ArgumentError: wrong number of arguments (given 3, expected 2)>
42
");
        }

        #endregion

        #region Type groups

        public class ProtocolGroupHost1 {
            public class Box {
                public static int Total;
                public static string Label { get; set; }
                public static int Twice(int x) { return x * 2; }
                public static int Apply(Func<int, int> f, int x) { return f(x); }
            }

            public class Box<T> {
                public static string Kind() { return typeof(T).Name; }
            }

            public class Pair<T> { }
            public class Pair<T, U> { }
        }

        /// <summary>
        /// A constant naming both a type and its generic namesakes is a TypeGroup; calls the group does not
        /// define itself go on to the non-generic type.
        /// </summary>
        public void ClrTypeGroupForwarding1() {
            Context.ObjectClass.SetConstant("H", Context.GetClass(typeof(ProtocolGroupHost1)));
            TestOutput(@"
B = H::Box
p B.class
p B.twice(21), B.Twice(1), B.apply(System::Func[Integer, Integer].new { |x| x + 1 }, 1)
p B.respond_to?(:twice), B.respond_to?(:Twice), B.respond_to?(:nope)
p B.method(:twice).call(4)

B.total = 5
B.label = 'x'
p B.total, B.label

NB = B[0]
def NB.with_block
  yield 3
end
p B.with_block { |x| x * 2 }

# the group's own methods are not forwarded
p B.count, B[Integer].kind

p((B.nope rescue $!))
p((B.nope(1, 2) rescue [$!.class, $!.name, $!.args]))
p((H::Pair.nope rescue $!))
p H::Pair.respond_to?(:nope)

p System::Tuple.create(1, 2).item2
p System::Threading::Tasks::Task.from_result(5).result
", @"
Microsoft::Scripting::Actions::TypeGroup
42
2
2
true
true
false
8
5
'x'
6
2
'Int32'
#<NoMethodError: undefined method `nope' for an instance of Microsoft::Scripting::Actions::TypeGroup>
[NoMethodError, :nope, [1, 2]]
#<NoMethodError: undefined method `nope' for an instance of Microsoft::Scripting::Actions::TypeGroup>
false
2
5
");
        }

        #endregion

        #region Tuples and pairs

        public static class ProtocolTuples1 {
            public static (int Min, int Max) MinMax(int[] xs) {
                return (System.Linq.Enumerable.Min(xs), System.Linq.Enumerable.Max(xs));
            }

            public static (string, int, bool) Triple() { return ("a", 1, true); }
            public static Tuple<string, int> RefPair() { return Tuple.Create("b", 2); }
            public static (int, int, int, int, int, int, int, int, int) Nine() { return (1, 2, 3, 4, 5, 6, 7, 8, 9); }

            public static Dictionary<string, int> Dict() {
                return new Dictionary<string, int> { { "a", 1 }, { "b", 2 } };
            }

            public static Hashtable Table() {
                var result = new Hashtable();
                result["x"] = 10;
                return result;
            }
        }

        /// <summary>
        /// Every ValueTuple/Tuple converts to an Array (to_a, to_ary), so multiple assignment and a block's |a, b| take
        /// it apart.
        /// </summary>
        public void ClrTupleDestructuring1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTuples1)));
            TestOutput(@"
mm = T.min_max(System::Array[Integer].new([3, 1, 2]))
min, max = mm
p [min, max]
p mm.to_a, mm.to_ary, mm.size, mm.length
p mm[0], mm[1], mm[-1], mm[-2], mm[2], mm[-3]
p [*mm], Array(mm)
[mm].each { |a, b| p a + b }
p [mm, mm].map { |a, b| a * b }

t = T.triple
p t.to_a, t.size
s, i, b = t
p s, i, b

p T.ref_pair.to_a
x, y = T.ref_pair
p x, y

nine = T.nine
p nine.size, nine.to_a, nine[8], nine[-9]
", @"
[1, 3]
[1, 3]
[1, 3]
2
2
1
3
3
1
nil
nil
[1, 3]
[1, 3]
4
[3, 3]
['a', 1, true]
3
'a'
1
true
['b', 2]
'b'
2
9
[1, 2, 3, 4, 5, 6, 7, 8, 9]
9
1
");
        }

        /// <summary>
        /// Array patterns go through #deconstruct, which hands CLR strings over as Ruby strings so that String matches them.
        /// </summary>
        [Options(Prism = true)]
        public void ClrTuplePatterns1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTuples1)));
            TestOutput(@"
mm = T.min_max(System::Array[Integer].new([3, 1, 2]))
case mm
in [a, b] then p [a, b]
end
p mm.deconstruct, T.triple.deconstruct

case T.triple
in [String => s, Integer => i, true] then p [s, i]
end

p((T.triple in [String, Integer]))
p((T.triple in [*, true]))

case T.nine
in [1, *rest, 9] then p rest
end

T.ref_pair => [name, count]
p [name, count]
", @"
[1, 3]
[1, 3]
[""a"", 1, true]
[""a"", 1]
false
true
[2, 3, 4, 5, 6, 7, 8]
[""b"", 2]
");
        }

        /// <summary>
        /// A dictionary enumerates KeyValuePairs (or DictionaryEntries), which convert to [key, value], so a block
        /// with |k, v| gets the key and the value.
        /// </summary>
        public void ClrKeyValuePair1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTuples1)));
            TestOutput(@"
d = T.dict
r = []
d.each { |k, v| r << [k, v] }
p r
p d.map { |k, v| k.to_s + v.to_s }
p d.inject(0) { |sum, (k, v)| sum + v }
p d.sort_by { |k, v| -v }.map { |k, v| k.to_s }

pair = d.first
k, v = pair
p [k, v], pair.to_a, pair.to_ary

h = T.table
h.each { |k, v| p [k, v] }
e = h.first
p e.class, e.to_a
k, v = e
p [k, v]
", @"
[['a', 1], ['b', 2]]
[""a1"", ""b2""]
3
[""b"", ""a""]
['a', 1]
['a', 1]
['a', 1]
['x', 10]
System::Collections::DictionaryEntry
['x', 10]
['x', 10]
");
        }

        /// <summary>
        /// deconstruct_keys of a pair: {key:, value:}, with the keys asked for (the CLR names Key/Value included), or
        /// all of them for nil.
        /// </summary>
        [Options(Prism = true)]
        public void ClrKeyValuePairPatterns1() {
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(ProtocolTuples1)));
            TestOutput(@"
require 'ruby4'
p T.dict.to_h

pair = T.dict.first
p pair.deconstruct_keys(nil), pair.deconstruct_keys([:key]), pair.deconstruct_keys([:Value, :nope, 1])
p pair.deconstruct_keys([]), pair.deconstruct_keys(['key'])
p((pair.deconstruct_keys(1) rescue $!))

case pair
in {key: String => key, value: Integer => value} then p [key, value]
end
case pair
in {key: 'a', **rest} then p rest
end
p((pair in {key: 'b'}))

e = T.table.first
p e.deconstruct, e.deconstruct_keys(nil), e.deconstruct_keys([:Key])
case e
in [String => k, v] then p [k, v]
end
case e
in {key:, value:} then p [key, value]
end
", @"
{'a' => 1, 'b' => 2}
{key: ""a"", value: 1}
{key: ""a""}
{Value: 1}
{}
{""key"" => ""a""}
#<TypeError: wrong argument type Integer (expected Array or nil)>
[""a"", 1]
{value: 1}
false
[""x"", 10]
{key: ""x"", value: 10}
{Key: ""x""}
[""x"", 10]
[""x"", 10]
");
        }

        #endregion
    }
}
