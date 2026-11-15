using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;

namespace IronRuby.Tests {
    public partial class Tests {
        /// <summary>
        /// The library initializers are written by a source generator (Src/InitializerGenerator), which works out
        /// from the compiler's symbols what LibraryOverload.EncodeCustomAttributes reads from a built method: which
        /// parameters are [DefaultProtocol] or [NotNull], and whether the last one is a params array. The binder
        /// trusts what the initializer passes, so a generator that disagrees with the runtime changes how every
        /// library method converts its arguments, and nothing else would say so.
        /// </summary>
        public void LibraryInitializerOverloadAttributes1() {
            var mismatches = new List<string>();
            int count = 0;

            Func<RubyModule, string, RubyMemberInfo, bool> check = (module, name, member) => {
                var method = member as RubyLibraryMethodInfo;
                if (method != null) {
                    foreach (LibraryOverload overload in method.Overloads) {
                        LibraryOverload reflected = LibraryOverload.Reflect(overload.Delegate);
                        bool same = overload.IsVariadic == reflected.IsVariadic;
                        for (int i = 0; i < overload.Parameters.Count; i++) {
                            same &= overload.HasDefaultProtocol(i) == reflected.HasDefaultProtocol(i)
                                && overload.ProhibitsNull(i) == reflected.ProhibitsNull(i);
                        }
                        if (!same) {
                            mismatches.Add(module.Name + "#" + name + " (" + overload.ReflectionInfo + ")");
                        }
                        count++;
                    }
                }
                return false;
            };

            Action checkAll = () => {
                using (Context.ClassHierarchyLocker()) {
                    var seen = new HashSet<RubyModule>();
                    var queue = new Queue<RubyModule>();
                    queue.Enqueue(Context.ObjectClass);
                    queue.Enqueue(Context.BasicObjectClass);
                    queue.Enqueue(Context.KernelModule);
                    while (queue.Count > 0) {
                        RubyModule module = queue.Dequeue();
                        if (!seen.Add(module)) {
                            continue;
                        }

                        module.EnumerateMethods(check);
                        if (module.ImmediateClass.IsSingletonClass && seen.Add(module.ImmediateClass)) {
                            module.ImmediateClass.EnumerateMethods(check);
                        }

                        module.EnumerateConstants((_, constantName, value) => {
                            var nested = value as RubyModule;
                            if (nested != null) {
                                queue.Enqueue(nested);
                            }
                            return false;
                        });
                    }
                }
            };

            // the builtins, before any Ruby code depends on them being right ...
            checkAll();
            int builtins = count;

            // ... and some of the other libraries; they need the prelude
            CompilerTest(@"
require 'ruby4'
require 'stringio'
require 'strscan'
require 'zlib'
require 'bigdecimal'
require 'digest'
");
            checkAll();

            Assert(mismatches.Count == 0, mismatches.Count + " overloads whose parameter attributes differ from the method's, among them: "
                + String.Join(", ", mismatches.GetRange(0, Math.Min(mismatches.Count, 10))));
            // far fewer than the builtins have means the walk found nothing to compare
            Assert(builtins > 2000, "only " + builtins + " overloads of the builtins were checked");
            // the second walk sees the builtins again, and the libraries
            Assert(count - builtins > builtins, "no overload of a library loaded by require was checked");
        }
    }
}
