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
    }
}
