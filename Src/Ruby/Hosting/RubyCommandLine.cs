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
using System.Collections.Generic;
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
            // Kernel#chomp, #chop, #sub and #gsub exist only under -n and -p, which is what
            // `Kernel.private_instance_methods(false)` is asked to show; so they are added here,
            // where the options are known, rather than declared with [RubyMethod].
            if (((RubyContext)Language).RubyOptions.LoopOverInput) {
                InputLoopOps.Define((RubyContext)Language);
            }

            // `ruby` with no -e and no script argument reads the program from stdin when
            // stdin is redirected. Only a real terminal gets the interactive loop — and
            // the banner that goes with it.
            if (Options.Command == null && Options.FileName == null && System.Console.IsInputRedirected) {
                // The program is read as bytes: a magic comment may say they are not UTF-8, and
                // decoding them before the parser has seen it mangles them.
                var program = new MemoryStream();
                using (var stdin = System.Console.OpenStandardInput()) {
                    stdin.CopyTo(program);
                }
                return RunFile(Engine.CreateScriptSource(new BinaryContentProvider(program.ToArray()), "-",
                    GetSourceCodeEncoding(), SourceCodeKind.File));
            }

            return base.Run();
        }

        protected override int RunFile(string fileName) {
            // A script that cannot be read is a LoadError in MRI, reported without a backtrace:
            // "ruby: No such file or directory -- x (LoadError)".
            string scriptPath = RubyUtils.CanonicalizePath(fileName);
            if (Directory.Exists(scriptPath) || !File.Exists(scriptPath)) {
                string reason = Directory.Exists(scriptPath) ? "Is a directory" : "No such file or directory";
                Console.Write(String.Format("ruby: {0} -- {1} (LoadError)\n", reason, fileName), Style.Error);
                return 1;
            }

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

            byte[] raw = options.LoopOverInput ? null : GetRawCommandBytes(command);
            if (raw != null) {
                return RunFile(Engine.CreateScriptSource(new BinaryContentProvider(raw), "-e", GetSourceCodeEncoding(), SourceCodeKind.Statements));
            }

            return RunFile(CreateCommandSource(command, SourceCodeKind.Statements, "-e"));
        }

        /// <summary>
        /// The bytes the -e arguments were given as, when .NET could not decode them as UTF-8 and
        /// put U+FFFD in their place: `-e "# encoding: big5..."` holds Big5 bytes, which only the
        /// magic comment can make sense of. On Linux the process's own command line has them.
        /// Null when there is nothing to recover or it cannot be done.
        /// </summary>
        private static byte[] GetRawCommandBytes(string/*!*/ command) {
            if (command.IndexOf('\uFFFD') < 0) {
                return null;
            }
            try {
                const string cmdline = "/proc/self/cmdline";
                if (!File.Exists(cmdline)) {
                    return null;
                }
                byte[] data = File.ReadAllBytes(cmdline);
                var args = new List<byte[]>();
                int start = 0;
                for (int i = 0; i < data.Length; i++) {
                    if (data[i] == 0) {
                        var arg = new byte[i - start];
                        Array.Copy(data, start, arg, 0, arg.Length);
                        args.Add(arg);
                        start = i + 1;
                    }
                }

                var joined = new List<byte>();
                bool any = false;
                for (int i = 0; i < args.Count; i++) {
                    byte[] arg = args[i];
                    byte[] code = null;
                    if (arg.Length == 2 && arg[0] == '-' && arg[1] == 'e' && i + 1 < args.Count) {
                        code = args[++i];
                    } else if (arg.Length > 2 && arg[0] == '-' && arg[1] == 'e') {
                        code = new byte[arg.Length - 2];
                        Array.Copy(arg, 2, code, 0, code.Length);
                    } else if (arg.Length == 2 && arg[0] == '-' && arg[1] == '-') {
                        break;
                    }
                    if (code != null) {
                        if (any) {
                            joined.Add((byte)'\n');
                        }
                        joined.AddRange(code);
                        any = true;
                    }
                }

                byte[] result = joined.ToArray();
                return any && Encoding.UTF8.GetString(result) == command ? result : null;
            } catch (IOException) {
                return null;
            } catch (UnauthorizedAccessException) {
                return null;
            }
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

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "signal")]
        private static extern IntPtr SysSignal(int signal, IntPtr handler);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "raise")]
        private static extern int SysRaise(int signal);

        protected override void UnhandledException(Exception e) {
            // Kernel#at_exit can access $!. So we need to publish the uncaught exception
            ((RubyContext)Language).CurrentException = e;

            // A SignalException that nobody rescued is not a program error: MRI puts the
            // default disposition back and re-raises the signal, so the process dies the way
            // it would have if the signal had never been turned into an exception. Reporting
            // it instead would tell the parent "exited with 1" where it is watching for
            // "killed by SIGTERM", which is what every Process::Status predicate is about.
            // The at_exit handlers still run first, and an Interrupt is reported as well.
            var context = (RubyContext)Language;
            int signal = GetSignalToRaise(e);
            if (signal > 0) {
                try {
                    context.RunShutdownHandlers();
                } catch (Exception) {
                    // the handlers report their own failures
                }
                if (signal == SignalInterrupt) {
                    Console.Write(Engine.GetService<ExceptionOperations>().FormatException(e), Style.Error);
                }
                RaiseSignal(signal);
                return;
            }

            // MRI runs the at_exit handlers first and reports the exception after them - except
            // for a script that does not parse, which is reported as it is parsed.
            if (!(e is SystemExit) && !context.MainScriptFailedToParse) {
                try {
                    context.RunShutdownHandlers();
                } catch (Exception) {
                    // the handlers report their own failures
                }
            }

            // The report already ends in a newline; the base class adds another.
            Console.Write(Engine.GetService<ExceptionOperations>().FormatException(e), Style.Error);
        }

        private const int SignalInterrupt = 2;

        /// <summary>The number of the signal an uncaught SignalException stands for, or 0.</summary>
        private int GetSignalToRaise(Exception/*!*/ e) {
            if (Path.DirectorySeparatorChar != '/') {
                return 0;
            }

            var context = (RubyContext)Language;

            // SignalException and its #signo both live in the library assembly, which this one
            // cannot reference, so both are reached through the object model instead.
            bool isSignal = false;
            for (var cls = context.GetClassOf(e); cls != null; cls = cls.SuperClass) {
                if (cls.Name == "SignalException") {
                    isSignal = true;
                    break;
                }
            }
            if (!isSignal) {
                return 0;
            }

            object number;
            try {
                number = Engine.Operations.InvokeMember(e, "signo");
            } catch (Exception) {
                return 0;
            }
            return (number is int && (int)number > 0) ? (int)number : 0;
        }

        private void RaiseSignal(int signal) {
            var context = (RubyContext)Language;
            Flush(context.StandardOutput);
            Flush(context.StandardErrorOutput);
            SysSignal(signal, IntPtr.Zero);   // SIG_DFL
            SysRaise(signal);
        }

        private void Flush(object io) {
            if (io == null) {
                return;
            }
            try {
                Engine.Operations.InvokeMember(io, "flush");
            } catch (Exception) {
                // nothing useful to do while the process is on its way out
            }
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