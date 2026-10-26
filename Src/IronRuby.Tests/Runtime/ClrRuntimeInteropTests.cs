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
using System.Linq;

namespace IronRuby.Tests {
    public partial class Tests {
        #region Platform assemblies loaded on demand

        /// <summary>
        /// The class library is split into many assemblies on .NET Core. A CLR namespace member that lives
        /// in a platform assembly nothing has loaded yet is found without load_assembly.
        /// </summary>
        public void ClrPlatformAssemblies1() {
            Assert(!Context.DomainManager.GetLoadedAssemblyList().Contains(typeof(Console).Assembly));

            TestOutput(@"
p System::Console
p System::Linq::Enumerable.range(1, 3).to_a
s = System::Collections::Generic::Stack[Integer].new
s.push 1; s.push 2
p s.pop
d = System::Collections::Generic::SortedDictionary[String, Integer].new
d['b'] = 2; d['a'] = 1
p d.keys.to_a
p System::Collections::Concurrent::ConcurrentDictionary[String, Integer].new.count
p System::Text::RegularExpressions::Regex.new('b+').match('abbc').value
p System::Diagnostics::Process
p System::Xml::XmlDocument
p Microsoft::Win32::Registry
", @"
System::Console
[1, 2, 3]
2
[""a"", ""b""]
0
'bb'
System::Diagnostics::Process
System::Xml::XmlDocument
Microsoft::Win32::Registry
");

            // registered with the runtime like an explicitly loaded assembly:
            Assert(Context.DomainManager.GetLoadedAssemblyList().Contains(typeof(Console).Assembly));
        }

        /// <summary>
        /// defined?, const_defined? and const_get load the assembly the same way, a member that no platform assembly
        /// defines is still missing, and a removed member is not loaded back.
        /// </summary>
        public void ClrPlatformAssemblies2() {
            TestOutput(@"
def d; defined?(System::Console); end
p d, d
p System.const_defined?(:Console)
p System.const_get(:Console)
p defined?(System::NoSuchType), System.const_defined?(:NoSuchType)
2.times { System::NoSuchType rescue p $! }
System::Collections::Generic::NoSuchType rescue p $!
p System.constants.include?(:Console)
System.send(:remove_const, :Console)
p d, System.const_defined?(:Console)
", @"
""constant""
""constant""
true
System::Console
nil
false
#<NameError: uninitialized constant System::NoSuchType>
#<NameError: uninitialized constant System::NoSuchType>
#<NameError: uninitialized constant System::Collections::Generic::NoSuchType>
true
nil
false
");
        }

        /// <summary>
        /// Loading is restricted to CLR namespaces: the top level is an ordinary Ruby lookup, and a namespace
        /// path does not become a top-level constant.
        /// </summary>
        public void ClrPlatformAssemblies3() {
            TestOutput(@"
p defined?(Linq), defined?(Console)
p System::Linq
p defined?(Linq)
Linq rescue p $!
", @"
nil
nil
System::Linq
nil
#<NameError: uninitialized constant Linq>
");
        }

        /// <summary>
        /// An assembly loaded after a missing constant was cached at a defined? site makes the site see the new
        /// member (the load changes the constant version).
        /// </summary>
        public void ClrPlatformAssemblies4() {
            Context.ObjectClass.SetConstant("DummyLinqAssembly", typeof(System.Linq.Dummy).Assembly.FullName);
            TestOutput(@"
def d; defined?(System::Linq::Dummy); end
p d
load_assembly DummyLinqAssembly
p d
", @"
nil
""constant""
");
        }

        #endregion

        #region Default interface methods

        public interface IDefaultGreeter {
            string Name { get; }
            string Greet() => "Hello from " + Name;
            string Greet(string greeting) => greeting + " from " + Name;
            string Title => "Dr. " + Name;
        }

        public interface ILoudGreeter : IDefaultGreeter {
            string Shout() => Greet().ToUpperInvariant() + "!";
        }

        public class ClrDefaultGreeter : IDefaultGreeter {
            public string Name => "C#";
        }

        public class ClrOverridingGreeter : IDefaultGreeter {
            public string Name => "C#";
            public string Greet() => "Hi from C#";
        }

        public class ClrExplicitGreeter : IDefaultGreeter {
            public string Name => "C#";
            string IDefaultGreeter.Greet() => "Explicit hi";
        }

        public static class DefaultGreeterUser {
            public static string Greet(IDefaultGreeter g) => g.Greet();
            public static string Greet(IDefaultGreeter g, string greeting) => g.Greet(greeting);
            public static string Title(IDefaultGreeter g) => g.Title;
            public static string Shout(ILoudGreeter g) => g.Shout();
        }

        private void DefineDefaultGreeterConstants() {
            Context.ObjectClass.SetConstant("IGreeter", Context.GetModule(typeof(IDefaultGreeter)));
            Context.ObjectClass.SetConstant("ILoudGreeter", Context.GetModule(typeof(ILoudGreeter)));
            Context.ObjectClass.SetConstant("ClrGreeter", Context.GetClass(typeof(ClrDefaultGreeter)));
            Context.ObjectClass.SetConstant("ClrOverridingGreeter", Context.GetClass(typeof(ClrOverridingGreeter)));
            Context.ObjectClass.SetConstant("ClrExplicitGreeter", Context.GetClass(typeof(ClrExplicitGreeter)));
            Context.ObjectClass.SetConstant("User", Context.GetClass(typeof(DefaultGreeterUser)));
        }

        /// <summary>
        /// A Ruby class that includes an interface and doesn't implement a member with a default body gets the default
        /// body, called from C# through the interface as well as from Ruby.
        /// </summary>
        public void ClrDefaultInterfaceMethods1() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
class G
  include IGreeter
  def name; 'Ruby'; end
end

g = G.new
puts User.greet(g), User.greet(g, 'Hey'), User.title(g)
puts g.greet, g.Greet, g.greet('Yo'), g.title
p g.respond_to?(:greet), G.instance_methods.map(&:to_s).include?('greet')
p IGreeter.instance_methods(false).map(&:to_s).sort
", @"
Hello from Ruby
Hey from Ruby
Dr. Ruby
Hello from Ruby
Hello from Ruby
Yo from Ruby
Dr. Ruby
true
true
[""greet"", ""title""]
");
        }

        /// <summary>
        /// A Ruby method overrides the default body for C# callers; super reaches the default body.
        /// </summary>
        public void ClrDefaultInterfaceMethods2() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
class G
  include IGreeter
  def name; 'Ruby'; end
  def greet; super + '!'; end
  def title; 'Prof. ' + name; end
end

g = G.new
puts User.greet(g), g.greet, User.title(g)
", @"
Hello from Ruby!
Hello from Ruby!
Prof. Ruby
");
        }

        /// <summary>
        /// Default bodies of the interfaces the included one extends are found too.
        /// </summary>
        public void ClrDefaultInterfaceMethods3() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
class G
  include ILoudGreeter
  def name; 'Ruby'; end
end

puts User.shout(G.new), G.new.shout, G.new.greet
", @"
HELLO FROM RUBY!
HELLO FROM RUBY!
Hello from Ruby
");
        }

        /// <summary>
        /// The default body calls members the Ruby class has to implement.
        /// </summary>
        public void ClrDefaultInterfaceMethods4() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
class G
  include IGreeter
end

User.greet(G.new) rescue p $!.class, $!.name
G.new.greet rescue p $!.class, $!.name
", @"
NoMethodError
:Name
NoMethodError
:Name
");
        }

        /// <summary>
        /// A C# class that doesn't override a default interface method exposes it; an override or an explicit
        /// implementation still wins.
        /// </summary>
        public void ClrDefaultInterfaceMethods5() {
            DefineDefaultGreeterConstants();
            TestOutput(@"
c = ClrGreeter.new
puts c.greet, c.Greet, c.greet('Hey'), c.title
p c.respond_to?(:greet), c.methods.map(&:to_s).include?('greet')
puts ClrOverridingGreeter.new.greet, ClrOverridingGreeter.new.greet('Hey')
puts ClrExplicitGreeter.new.greet
", @"
Hello from C#
Hello from C#
Hey from C#
Dr. C#
true
true
Hi from C#
Hey from C#
Explicit hi
");
        }

        #endregion
    }
}
