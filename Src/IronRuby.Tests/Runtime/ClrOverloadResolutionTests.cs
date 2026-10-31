/* ****************************************************************************
 *
 * How a Ruby call chooses between CLR overloads that fit its arguments
 * equally: C#'s tie-break for generic methods.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;

namespace IronRuby.Tests {
    public partial class Tests {
        // Shaped like Enumerable.Max/Min: selectors returning Int32, Int64 or any TResult.
        public static class TieBreakSelectors {
            public static string Pick<TSource>(IEnumerable<TSource> source, Func<TSource, int> selector) { return "Pick(Func<T, Int32>)"; }
            public static string Pick<TSource>(IEnumerable<TSource> source, Func<TSource, long> selector) { return "Pick(Func<T, Int64>)"; }
            public static string Pick<TSource, TResult>(IEnumerable<TSource> source, Func<TSource, TResult> selector) { return "Pick(Func<T, TResult>)"; }
            public static Func<int, long> Int64Selector() { return x => -x; }

            // Shaped like Enumerable.Sum: no selector returns a type parameter.
            public static string Total<TSource>(IEnumerable<TSource> source, Func<TSource, int> selector) { return "Total(Func<T, Int32>)"; }
            public static string Total<TSource>(IEnumerable<TSource> source, Func<TSource, double> selector) { return "Total(Func<T, Double>)"; }

            // Each more specific in one parameter: neither wins.
            public static string Cross<T>(T a, int b) { return "Cross(T, Int32)"; }
            public static string Cross<T>(int a, T b) { return "Cross(Int32, T)"; }

            // More specific deeper down: an array of Int32 and a list of Int32.
            public static string Items<T>(T[] items, T item) { return "Items(T[], T)"; }
            public static string Items<T>(int[] items, T item) { return "Items(Int32[], T)"; }
            public static string Lists<T, U>(Func<T, IList<U>> f, T arg) { return "Lists(Func<T, IList<U>>)"; }
            public static string Lists<T>(Func<T, IList<int>> f, T arg) { return "Lists(Func<T, IList<Int32>>)"; }

            // A non-generic method wins over a generic one, as before.
            public static string Plain<T>(IEnumerable<T> source, Func<T, int> selector) { return "Plain<T>"; }
            public static string Plain(IEnumerable<int> source, Func<int, int> selector) { return "Plain"; }
        }

        // A type parameter of the declaring type is less specific too.
        public class TieBreakBox<T> {
            public string Put(T item) { return "Put(T)"; }
            public string Put(int item) { return "Put(Int32)"; }
        }

        /// <summary>
        /// Overloads that take the same parameter types once instantiated are told apart as C# does: the one whose declared
        /// parameter types are more specific wins, a type parameter being less specific than any other type.
        /// Max&lt;TSource&gt;(…, Func&lt;TSource, int&gt;) wins over Max&lt;TSource, TResult&gt;(…, Func&lt;TSource, TResult&gt;).
        /// </summary>
        public void ClrOverloadGenericTieBreak1() {
            Context.ObjectClass.SetConstant("S", Context.GetClass(typeof(TieBreakSelectors)));
            Context.ObjectClass.SetConstant("IntBox", Context.GetClass(typeof(TieBreakBox<int>)));
            Context.ObjectClass.SetConstant("StringBox", Context.GetClass(typeof(TieBreakBox<string>)));
            TestOutput(@"
list = System::Collections::Generic::List[Integer].new([3, 1, 4])
puts S.pick(list, System::Func[Integer, Integer].new { -it })
puts S.pick(list, S.int64_selector)
puts S.pick(list, System::Func[Integer, Float].new { -it })
puts S.pick(list) { -it }
puts S.total(list, System::Func[Integer, Integer].new { -it })
puts S.total(list, System::Func[Integer, Float].new { -it })
puts S.items(System::Array[Integer].new([1]), 2)
puts S.lists(System::Func[Integer, System::Collections::Generic::IList[Integer]].new { [it] }, 1)
puts S.plain(list, System::Func[Integer, Integer].new { -it })
puts IntBox.new.put(1)
puts StringBox.new.put('x')
begin
  S.cross(1, 2)
rescue System::Reflection::AmbiguousMatchException
  puts 'ambiguous'
end
", @"
Pick(Func<T, Int32>)
Pick(Func<T, Int64>)
Pick(Func<T, TResult>)
Pick(Func<T, TResult>)
Total(Func<T, Int32>)
Total(Func<T, Double>)
Items(Int32[], T)
Lists(Func<T, IList<Int32>>)
Plain
Put(Int32)
Put(T)
ambiguous
");
        }

        /// <summary>
        /// LINQ's Max and Min take an explicit selector delegate; a block that fits several selector types is still
        /// reported, with the hint to pass the delegate.
        /// </summary>
        public void ClrOverloadGenericTieBreak2() {
            Context.ObjectClass.SetConstant("SystemCoreAssembly", typeof(System.Linq.Enumerable).Assembly.FullName);
            TestOutput(@"
load_assembly SystemCoreAssembly
using System::Linq
list = System::Collections::Generic::List[Integer].new([3, 1, 4])
p list.max(System::Func[Integer, Integer].new { -it })
p list.min(System::Func[Integer, Integer].new { -it })
p list.min(System::Func[Integer, Float].new { it * 1.5 })
p list.max(System::Func[Integer, System::String].new { it.to_s })
p list.sum(System::Func[Integer, Integer].new { -it })
p list.average(System::Func[Integer, Float].new { it * 3.0 })
p list.max { -it }
begin
  list.sum { it * 2 }
rescue System::Reflection::AmbiguousMatchException => e
  puts e.message.end_with?('pass the delegate itself, e.g. System::Func[...].new { ... }')
end
", @"
-1
-4
1.5
'4'
-8
8.0
-1
true
");
        }
    }
}
