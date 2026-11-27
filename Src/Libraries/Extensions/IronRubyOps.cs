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

using IronRuby.Compiler.Generation;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System;
using System.IO;

namespace IronRuby.Builtins {
    [RubyModule("IronRuby", Extends = typeof(Ruby), Restrictions = ModuleRestrictions.NotPublished)]
    public static class IronRubyOps {

        [RubyMethod("configuration", RubyMethodAttributes.PublicSingleton)]
        public static DlrConfiguration/*!*/ GetConfiguration(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            return context.DomainManager.Configuration;
        }

        [RubyMethod("globals", RubyMethodAttributes.PublicSingleton)]
        public static Scope/*!*/ GetGlobalScope(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            return context.DomainManager.Globals;
        }

        /// <summary>
        /// What the default-spec cache of rubygems/defaults/ironruby.rb is valid for: [name, size, mtime] of each entry of the
        /// directory that `Dir.glob("*.gemspec")` would match (not a dot file), in ordinal order of the names; mtime in 100 ns
        /// ticks. Empty if the directory does not exist. A boot that finds the cache valid then needs neither the glob nor a
        /// File::Stat per gemspec, which took ~13 ms of `ir -e 1'.
        /// </summary>
        [RubyMethod("__gemspec_stamp__", RubyMethodAttributes.PrivateSingleton)]
        public static RubyArray/*!*/ GetGemspecStamp(RubyContext/*!*/ context, RubyModule/*!*/ self, [NotNull]MutableString/*!*/ dir) {
            // (collections of reference types only: their shared code is precompiled, where a List of KeyValuePairs was
            // JIT-compiled at the first call - 26 methods, ~2 ms)
            var entries = new List<FileSystemInfo>();
            var directory = new DirectoryInfo(context.DecodePath(dir));
            if (directory.Exists) {
                foreach (FileSystemInfo entry in directory.GetFileSystemInfos()) {
                    string name = entry.Name;
                    if (name.EndsWith(".gemspec", StringComparison.Ordinal) && !name.StartsWith(".", StringComparison.Ordinal)) {
                        entries.Add(entry);
                    }
                }
            }
            entries.Sort((x, y) => String.CompareOrdinal(x.Name, y.Name));

            var result = new RubyArray(entries.Count);
            foreach (FileSystemInfo entry in entries) {
                var file = entry as FileInfo;
                long size = (file != null && file.Exists) ? file.Length : 0;
                long mtime = entry.Exists ? entry.LastWriteTimeUtc.Ticks : 0;
                result.Add(new RubyArray(new object[] { context.EncodePath(entry.Name), Protocols.Normalize(size), Protocols.Normalize(mtime) }));
            }
            return result;
        }

        [RubyMethod("loaded_assemblies", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetLoadedAssemblies(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            return new RubyArray(context.DomainManager.GetLoadedAssemblyList());
        }

        /// <summary>
        /// Gets a live read-only and thread-safe dictionary that maps full paths of the loaded scripts to their scopes.
        /// </summary>
        [RubyMethod("loaded_scripts", RubyMethodAttributes.PublicSingleton)]
        public static IDictionary<string, Scope>/*!*/ GetLoadedScripts(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            return new ReadOnlyDictionary<string, Scope>(context.Loader.LoadedScripts);
        }

        /// <summary>
        /// The same as Kernel#require except for that it returns the loaded Assembly or Scope (even if already loaded).
        /// </summary>
        [RubyMethod("require", RubyMethodAttributes.PublicSingleton)]
        public static object/*!*/ Require(RubyScope/*!*/ scope, RubyModule/*!*/ self, MutableString/*!*/ libraryName) {
            object loaded;
            
            scope.RubyContext.Loader.LoadFile(
                null, self, libraryName, LoadFlags.LoadOnce | LoadFlags.AppendExtensions | LoadFlags.ResolveLoaded | LoadFlags.AnyLanguage, out loaded
            );

            Debug.Assert(loaded != null);
            return loaded;
        }

        /// <summary>
        /// The same as Kernel#require except for that it returns the loaded Assembly or Scope.
        /// </summary>
        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        public static object/*!*/ Load(RubyScope/*!*/ scope, RubyModule/*!*/ self, MutableString/*!*/ libraryName) {
            object loaded;
            scope.RubyContext.Loader.LoadFile(null, self, libraryName, LoadFlags.ResolveLoaded | LoadFlags.AnyLanguage, out loaded);
            Debug.Assert(loaded != null);
            return loaded;
        }

        /// <summary>
        /// What the method JIT and the light frames make of every method the files define, without running the files:
        /// an Array of Hashes, one for each method (IronRuby.Runtime.Jit.JitCensus). Util/perf/census.rb sums it up.
        /// </summary>
        [RubyMethod("jit_census", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ JitCensus(RubyContext/*!*/ context, RubyModule/*!*/ self, [NotNull]RubyArray/*!*/ paths) {
            var files = new List<string>(paths.Count);
            foreach (object path in paths) {
                var str = path as MutableString;
                if (str == null) {
                    throw RubyExceptions.CreateTypeError("jit_census takes an Array of paths");
                }
                files.Add(str.ConvertToString());
            }
            return IronRuby.Runtime.Jit.JitCensus.Run(context, files);
        }

        [RubyModule("Clr", Restrictions = ModuleRestrictions.NoUnderlyingType)]
        public static class Clr {
            [RubyMethod("profile", RubyMethodAttributes.PublicSingleton)]
            public static Hash/*!*/ GetProfile(RubyContext/*!*/ context, object self) {
                if (!context.RubyOptions.Profile) {
                    throw RubyExceptions.CreateSystemCallError("You must enable profiling to use Clr.profile");
                }

                Hash result = new Hash(context);
                foreach (var entry in Profiler.Instance.GetProfile()) {
                    result[entry.Id] = Utils.DateTimeTicksFromStopwatch(entry.Ticks);
                }
                return result;
            }

            [RubyMethod("profile", RubyMethodAttributes.PublicSingleton)]
            public static object GetProfile(RubyContext/*!*/ context, BlockParam/*!*/ block, object self) {
                if (!context.RubyOptions.Profile) {
                    throw RubyExceptions.CreateSystemCallError("You must enable profiling to use Clr.profile");
                }

                var start = Profiler.Instance.GetProfile();
                object blockResult;
                if (block.Yield(out blockResult)) {
                    return blockResult;
                }

                var startDict = new Dictionary<string, long>();
                foreach (var counter in start) {
                    startDict[counter.Id] = counter.Ticks;
                }

                Hash result = new Hash(context);
                foreach (var entry in Profiler.Instance.GetProfile()) {
                    long startTime;
                    if (!startDict.TryGetValue(entry.Id, out startTime)) {
                        startTime = 0;
                    }
                    long elapsed = entry.Ticks - startTime;
                    if (elapsed > 0) {
                        result[entry.Id] = Utils.DateTimeTicksFromStopwatch(elapsed);
                    }
                }
                return result;
            }
        }
    }
}
