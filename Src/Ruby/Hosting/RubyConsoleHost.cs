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
#if FEATURE_FULL_CONSOLE
using System;
using System.IO;
using System.Threading;
using IronRuby;
using IronRuby.Builtins;
using IronRuby.Hosting;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using Microsoft.Scripting.Hosting.Shell;
using Microsoft.Scripting.Utils;

namespace IronRuby.Hosting {
    public abstract class RubyConsoleHost : ConsoleHost {
        protected RubyConsoleHost() {
            SetHomeEnvironmentVariable();
        }

        protected override Type Provider {
            get { return typeof(RubyContext); }
        }

        protected override CommandLine/*!*/ CreateCommandLine() {
            return new RubyCommandLine();
        }

        protected override OptionsParser/*!*/ CreateOptionsParser() {
            return new RubyOptionsParser();
        }

        protected override LanguageSetup CreateLanguageSetup() {
            return Ruby.CreateRubySetup();
        }

#if NETCOREAPP
        /// <summary>
        /// The DLR's default reads the runtime setup from the .NET Framework configuration system
        /// (a &lt;microsoft.scripting&gt; section in ir.exe.config). On .NET that needs
        /// System.Configuration.ConfigurationManager, which ir does not ship, so the probe could
        /// only fail - an assembly search and a FileNotFoundException, ~25 ms of every startup -
        /// and always ended in this same empty setup.
        /// </summary>
        protected override ScriptRuntimeSetup CreateRuntimeSetup() {
            var setup = new ScriptRuntimeSetup();
            setup.LanguageSetups.Add(CreateLanguageSetup());
            return setup;
        }
#endif

        protected override ConsoleOptions ParseOptions(string[] args, ScriptRuntimeSetup runtimeSetup, LanguageSetup languageSetup) {
            languageSetup.Options["ApplicationBase"] = AppDomain.CurrentDomain.BaseDirectory;
            runtimeSetup.HostType = typeof(RubyScriptHost);
            return base.ParseOptions(args, runtimeSetup, languageSetup);
        }

        /// <summary>
        /// The host the runtime asks for its PlatformAdaptationLayer, so that source files
        /// are opened the way MRI opens a script: readable by anyone and deletable while
        /// open. The DLR's default opens them FileShare.Read, which on Windows makes the
        /// file undeletable until the stream is collected - and a Ruby program that writes a
        /// script, loads it and then removes it (every ruby/spec code-loading example does)
        /// gets Errno::EACCES for its trouble. Unix never noticed because unlink(2) there
        /// does not care who has the file open.
        /// </summary>
        private sealed class RubyScriptHost : ScriptHost {
            public override PlatformAdaptationLayer PlatformAdaptationLayer {
                get { return SharedPlatform; }
            }
        }

        private static readonly PlatformAdaptationLayer/*!*/ SharedPlatform = new RubyPlatformAdaptationLayer();

        private sealed class RubyPlatformAdaptationLayer : PlatformAdaptationLayer {
            public override Stream OpenInputFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) {
                return base.OpenInputFileStream(path, mode, access, share | FileShare.ReadWrite | FileShare.Delete, bufferSize);
            }

            /// <summary>
            /// MRI's Dir.pwd is getcwd(): the directory as it was set. .NET's GetCurrentDirectory
            /// expands an 8.3 short name ("C:\Users\ABCDEF~1") to the long one, so after
            /// Dir.chdir(Dir.tmpdir) Dir.pwd was not the directory just changed into, and a path
            /// built from the one did not start with the other - rubocop, for one, then cannot
            /// show a file relative to the current directory.
            /// </summary>
            public override string CurrentDirectory {
                get {
                    if (Path.DirectorySeparatorChar == '\\') {
                        var buffer = new System.Text.StringBuilder(260);
                        uint length = GetCurrentDirectoryW((uint)buffer.Capacity, buffer);
                        if (length > buffer.Capacity) {
                            buffer = new System.Text.StringBuilder((int)length);
                            length = GetCurrentDirectoryW(length, buffer);
                        }
                        if (length > 0 && length <= buffer.Capacity) {
                            return buffer.ToString();
                        }
                    }
                    return base.CurrentDirectory;
                }
                set {
                    // .NET sets it through Path.GetFullPath, which expands short names too; MRI's
                    // SetCurrentDirectory takes the path as given. Set once - a relative path set
                    // twice would be resolved twice - and leave a failure to .NET, whose
                    // exceptions are what Dir.chdir turns into Errno.
                    if (Path.DirectorySeparatorChar == '\\' && SetCurrentDirectoryW(value)) {
                        return;
                    }
                    base.CurrentDirectory = value;
                }
            }

            [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            private static extern uint GetCurrentDirectoryW(uint bufferLength, System.Text.StringBuilder buffer);

            [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            private static extern bool SetCurrentDirectoryW(string path);
        }

        private static void SetHomeEnvironmentVariable() {
            try {
                PlatformAdaptationLayer platform = PlatformAdaptationLayer.Default;
                string homeDir = RubyUtils.GetHomeDirectory(platform);
                platform.SetEnvironmentVariable("HOME", homeDir);
            } catch (System.Security.SecurityException) {
                // Ignore EnvironmentPermission exception
            }
        }
    }
}
#endif