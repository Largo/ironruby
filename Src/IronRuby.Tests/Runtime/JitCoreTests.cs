/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

namespace IronRuby.Tests {
    public partial class Tests {
        // Core methods the method JIT's typed bodies compute themselves (Src/Ruby/Runtime/Jit):
        // each shape is made hot on the kind of receiver it is compiled for, then given the others,
        // and the core method is redefined in the middle of a run. Util/jit-matrix.rb has more.

        /// <summary>String#length and #size: characters in the String's encoding, not bytes or UTF-16 units.</summary>
        public void JitCore_StringLength1() {
            int specialized = IronRuby.Runtime.Jit.JitRuntime.Specialized;
            TestOutput(@"
class SlBox
  def initialize(s) = (@input = s; @index = 0)
  def more = @index < @input.length
  def len(s) = s.length
  def hash_of(s) = s.size * 1_402_589
  def slot(s) = hash_of(s) & 15
end
class SlString < String
  def length = 99
end
b = SlBox.new('abc')
200.times { |i| b.len('abcd'); b.hash_of('ab' * (i % 4)); b.slot('xyz'); b.more }
p [""héllo"", ""\xff\xfe"".dup.force_encoding('UTF-8'), ""\xff"".b, ""abé"".encode('UTF-16LE'), ""\u{1F600}x"",
   SlString.new('abc'), [1, 2]].map { |s| [b.len(s), b.slot(s)] }
p((b.len(nil) rescue $!.class))
seen = []
200.times do |i|
  class String; remove_method :length; def length = -1; end if i == 100
  seen << b.len('abcd')
end
p seen.uniq
", @"
[[5, 1], [2, 10], [1, 13], [3, 7], [2, 10], [99, 7], [2, 10]]
NoMethodError
[4, -1]
");
            int specializedHere = IronRuby.Runtime.Jit.JitRuntime.Specialized - specialized;
            Assert(specializedHere >= 3, System.String.Format("methods specialized: {0} (expected at least 3)", specializedHere));
        }
    }
}
