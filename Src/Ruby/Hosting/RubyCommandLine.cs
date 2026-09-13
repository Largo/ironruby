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
            return RunFile(Engine.CreateScriptSourceFromFile(RubyUtils.CanonicalizePath(fileName), GetSourceCodeEncoding()));
        }

        protected override ScriptCodeParseResult GetCommandProperties(string code) {
            return CreateCommandSource(code, SourceCodeKind.InteractiveCode, "(ir)").GetCodeProperties(Engine.GetCompilerOptions(ScriptScope));
        }

        protected override void ExecuteCommand(string/*!*/ command) {
            ExecuteCommand(CreateCommandSource(command, SourceCodeKind.InteractiveCode, "(ir)"));
        }

        protected override int RunCommand(string/*!*/ command) {
            return RunFile(CreateCommandSource(command, SourceCodeKind.Statements, "-e"));
        }

        private ScriptSource/*!*/ CreateCommandSource(string/*!*/ command, SourceCodeKind kind, string/*!*/ sourceUnitId) {
            var encoding = GetSourceCodeEncoding();
            return Engine.CreateScriptSource(new BinaryContentProvider(encoding.GetBytes(command)), sourceUnitId, encoding, kind);
        }

        private Encoding/*!*/ GetSourceCodeEncoding() {
            return (((RubyContext)Language).RubyOptions.DefaultEncoding ?? RubyEncoding.Ascii).Encoding;
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
            if (RaiseAsSignal(e)) {
                return;
            }

            base.UnhandledException(e);
        }

        private bool RaiseAsSignal(Exception/*!*/ e) {
            if (Path.DirectorySeparatorChar != '/') {
                return false;
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
                return false;
            }

            object number;
            try {
                number = Engine.Operations.InvokeMember(e, "signo");
            } catch (Exception) {
                return false;
            }
            if (!(number is int) || (int)number <= 0) {
                return false;
            }

            Flush(context.StandardOutput);
            Flush(context.StandardErrorOutput);
            SysSignal((int)number, IntPtr.Zero);   // SIG_DFL
            SysRaise((int)number);
            return true;
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