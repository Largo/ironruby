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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using IronRuby.Hosting;
using IronRuby.Runtime;

[assembly: AssemblyTitle("Ruby")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Microsoft")]
[assembly: AssemblyProduct("Ruby")]
[assembly: AssemblyCopyright("© Microsoft Corporation.  All rights reserved.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: CLSCompliant(true)]
[assembly: Guid("ca75230d-3011-485d-b1db-dfe924b6c434")]

//#if !SILVERLIGHT
//[assembly: AssemblyVersion(RubyContext.IronRubyVersionString)]
//[assembly: AssemblyFileVersion(RubyContext.IronRubyVersionString)]
//#endif

#if !SILVERLIGHT && !WP75
[assembly: AllowPartiallyTrustedCallers]
#endif

#if SILVERLIGHT
[assembly: InternalsVisibleTo("IronRuby.Tests")]
#else
[assembly: InternalsVisibleTo("IronRuby.Tests")]
[assembly: InternalsVisibleTo("ClassInitGenerator")]
#endif
[assembly: InternalsVisibleTo("IronRuby.Prism")]


[assembly: SecurityTransparent]
#if !CLR2 && !SILVERLIGHT && !WIN8 && !ANDROID && !WP75
[assembly: SecurityRules(SecurityRuleSet.Level1)]
#endif
