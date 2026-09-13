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
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Warning's category switches.  They live here rather than in the Ruby prelude because the
    /// runtime has to read them too: the chilled string literal warning is raised from inside
    /// MutableString's mutation guard, and -W:category has to be able to set them before any Ruby
    /// code runs.  Warning.warn itself stays in Ruby, so overriding it still works.
    /// </summary>
    [RubyModule("Warning")]
    public static class WarningOps {

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("[]", RubyMethodAttributes.PublicInstance)]
        public static bool GetCategory(RubyContext/*!*/ context, object self, [NotNull]RubySymbol/*!*/ category) {
            return context.IsWarningEnabled(CheckCategory(category));
        }

        [RubyMethod("[]=", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("[]=", RubyMethodAttributes.PublicInstance)]
        public static object SetCategory(RubyContext/*!*/ context, object self, [NotNull]RubySymbol/*!*/ category, object flag) {
            context.SetWarningEnabled(CheckCategory(category), RubyOps.IsTrue(flag));
            return flag;
        }

        [RubyMethod("categories", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("categories", RubyMethodAttributes.PublicInstance)]
        public static RubyArray/*!*/ GetCategories(RubyContext/*!*/ context, object self) {
            var result = new RubyArray(RubyContext.WarningCategories.Length);
            foreach (string category in RubyContext.WarningCategories) {
                result.Add(context.CreateAsciiSymbol(category));
            }
            return result;
        }

        private static string/*!*/ CheckCategory(RubySymbol/*!*/ category) {
            string name = category.ToString();
            if (Array.IndexOf(RubyContext.WarningCategories, name) < 0) {
                throw RubyExceptions.CreateArgumentError("unknown category: {0}", name);
            }
            return name;
        }
    }
}
