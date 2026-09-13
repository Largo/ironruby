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
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Shell;
using Microsoft.Scripting.Runtime;
using System.Reflection;
using System.Threading;
using System.IO;
using System.Globalization;
using System.Text;

namespace IronRuby.Hosting {
   
    /// <summary>
    /// A simple Ruby command-line should mimic the standard irb.exe
    /// </summary>
    public class RubyCommandLine : CommandLine {
        public RubyCommandLine() {
        }

        internal new RubyConsoleOptions Options {
            get { return (RubyConsoleOptions)base.Options; }
        }

        protected override string Logo {
            get { return GetLogo(); }
        }

        public static string GetLogo() {
            return String.Format(CultureInfo.InvariantCulture,
                "IronRuby {1} on {2}{0}Copyright (c) Microsoft Corporation. All rights reserved.{0}{0}",
                Environment.NewLine, IronRuby.CurrentVersion.DisplayVersion, RubyContext.MakeRuntimeDesriptionString());
        }

        protected override int? TryInteractiveAction() {
            try {
                return base.TryInteractiveAction();
            } catch (ThreadAbortException e) {
                Exception visibleException = RubyUtils.GetVisibleException(e);
                if (visibleException == e || visibleException == null) {
                    throw;
                } else {
                    throw visibleException;
                }
            } catch (SystemExit e) {
                return e.Status;
            }
        }

        // overridden to set the default encoding to -KX
        protected override int Run() {
            // `ruby` with no -e and no script argument reads the program from stdin when
            // stdin is redirected. Only a real terminal gets the interactive loop — and
            // the banner that goes with it.
            if (Options.Command == null && Options.FileName == null && System.Console.IsInputRedirected) {
                return RunFile(CreateCommandSource(System.Console.In.ReadToEnd(), SourceCodeKind.File, "-"));
            }

            return base.Run();
        }

        protected override int RunFile(string fileName) {
            var options = ((RubyContext)Language).RubyOptions;
            if (options.LoopOverInput) {
                // The program has to be read rather than handed to the compiler as a path, so
                // that the -n loop can be wrapped around it.
                string path = RubyUtils.CanonicalizePath(fileName);
                string source = File.ReadAllText(path, GetSourceCodeEncoding());
                return RunFile(Engine.CreateScriptSource(
                    new BinaryContentProvider(GetSourceCodeEncoding().GetBytes(WrapInInputLoop(source, options))),
                    path, GetSourceCodeEncoding(), SourceCodeKind.File
                ));
            }

            return RunFile(Engine.CreateScriptSourceFromFile(RubyUtils.CanonicalizePath(fileName), GetSourceCodeEncoding()));
        }

        /// <summary>
        /// -n and -p run the program once per input line, as `while gets; ...; end`; -a splits the
        /// line into $F and -l chomps it first.
        ///
        /// The loop header sits on the same line as the first line of the program rather than on a
        /// line of its own, so that every line of the program keeps the number it has in the file
        /// and backtraces still point at the right place.
        ///
        /// A BEGIN or END block in the program is rejected by the parser as "permitted only at
        /// toplevel", because after the wrapping it no longer is one. MRI wraps the parsed program
        /// instead, where the two have already been hoisted out; doing the same here needs the
        /// same hoisting in the prism bridge.
        /// </summary>
        private static string/*!*/ WrapInInputLoop(string/*!*/ source, RubyOptions/*!*/ options) {
            var header = new StringBuilder("while gets;");
            if (options.ChopLines) {
                header.Append("$_.chomp!($/);");
            }
            if (options.AutoSplit) {
                header.Append("$F=$_.split;");
            }

            var footer = new StringBuilder();
            if (options.PrintEachLine) {
                footer.Append(";print $_");
            }
            footer.Append(";end");

            return header.ToString() + source + footer.ToString();
        }

        protected override ScriptCodeParseResult GetCommandProperties(string code) {
            return CreateCommandSource(code, SourceCodeKind.InteractiveCode, "(ir)").GetCodeProperties(Engine.GetCompilerOptions(ScriptScope));
        }

        protected override void ExecuteCommand(string/*!*/ command) {
            ExecuteCommand(CreateCommandSource(command, SourceCodeKind.InteractiveCode, "(ir)"));
        }

        protected override int RunCommand(string/*!*/ command) {
            var options = ((RubyContext)Language).RubyOptions;
            if (options.LoopOverInput) {
                command = WrapInInputLoop(command, options);
            }

            return RunFile(CreateCommandSource(command, SourceCodeKind.Statements, "-e"));
        }

        private ScriptSource/*!*/ CreateCommandSource(string/*!*/ command, SourceCodeKind kind, string/*!*/ sourceUnitId) {
            var encoding = GetSourceCodeEncoding();
            return Engine.CreateScriptSource(new BinaryContentProvider(encoding.GetBytes(command)), sourceUnitId, encoding, kind);
        }

        private Encoding/*!*/ GetSourceCodeEncoding() {
            // UTF-8, not US-ASCII: the command is already a decoded .NET string, and turning it
            // back into bytes through US-ASCII replaced every non-ASCII character with a question
            // mark. `ir -e 'p "αΣ".bytes'` printed [63, 63] while the same script in a file
            // printed the four UTF-8 bytes. UTF-8 is also what the source unit's encoding resolves
            // to further down when no magic comment says otherwise, so this only stops -e from
            // being the one input path that loses bytes before the parser ever sees them.
            // (Ruby 2.0 made UTF-8 the default script encoding; only -K overrides it.)
            return (((RubyContext)Language).RubyOptions.DefaultEncoding ?? RubyEncoding.UTF8).Encoding;
        }
        
        protected override Scope/*!*/ CreateScope() {
            Scope scope = base.CreateScope();
            RubyOps.ScopeSetMember(scope, "iron_ruby", Engine);
            return scope;
        }

        protected override void UnhandledException(Exception e) {
            // Kernel#at_exit can access $!. So we need to publish the uncaught exception
            ((RubyContext)Language).CurrentException = e;

            base.UnhandledException(e);
        }

        protected override void Shutdown() {
            try {
                Engine.Runtime.Shutdown();
            } catch (SystemExit e) {
                // Kernel#at_exit runs during shutdown, and it can set the exitcode by calling exit
                ExitCode = e.Status;
            }
        }
    }
}
#endif