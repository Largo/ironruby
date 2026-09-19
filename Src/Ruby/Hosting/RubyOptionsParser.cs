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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Shell;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Hosting {
    public sealed class RubyConsoleOptions : ConsoleOptions {
        public string ChangeDirectory;
        public bool DisplayVersion;
    }

    public sealed class RubyOptionsParser : OptionsParser<RubyConsoleOptions> {
        private readonly List<string>/*!*/ _loadPaths = new List<string>();
        private readonly List<string>/*!*/ _requiredPaths = new List<string>();
        private RubyEncoding _defaultEncoding;
        private string _externalEncodingName;
        private string _internalEncodingName;
        private bool _disableRubyGems;
        private readonly List<string>/*!*/ _warningCategoryFlags = new List<string>();

#if DEBUG
        private ConsoleTraceListener _debugListener;

        private sealed class CustomTraceFilter : TraceFilter {
            public readonly Dictionary<string, bool>/*!*/ Categories = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            public bool EnableAll { get; set; }

            public override bool ShouldTrace(TraceEventCache cache, string source, TraceEventType eventType, int id, string category, object[] args, object data1, object[] data) {
                string message = data1 as string;
                if (message == null) return true;

                bool enabled;
                if (Categories.TryGetValue(category, out enabled)) {
                    return enabled;
                } else {
                    return EnableAll;
                }
            }
        }

        private void SetTraceFilter(string/*!*/ arg, bool enable) {
            string[] categories = arg.Split(new[] { ';', ','}, StringSplitOptions.RemoveEmptyEntries);

            if (categories.Length == 0 && !enable) {
                Trace.Listeners.Clear();
                return;
            }

            if (_debugListener == null) {
                _debugListener = new ConsoleTraceListener { IndentSize = 4, Filter = new CustomTraceFilter { EnableAll = categories.Length == 0 } };
                Trace.Listeners.Add(_debugListener);
            } 
         
            foreach (var category in categories) {
                ((CustomTraceFilter)_debugListener.Filter).Categories[category] = enable;
            }
        }
#endif

        /// <summary>
        /// One feature named by --enable / --disable. MRI silently accepts the names of features
        /// it has compiled out, so an unrecognised one here is not an error either - it would turn
        /// a working command line into a startup failure on an implementation detail.
        /// </summary>
        private void SetFeature(string/*!*/ feature, bool enable) {
            switch (feature) {
                case "gems":
                case "gem":
                    _disableRubyGems = !enable;
                    break;

                case "frozen-string-literal":
                case "frozen_string_literal":
                    // prism's numbering, which is where this ends up.
                    LanguageSetup.Options["FrozenStringLiteral"] = enable ? 1 : -1;
                    break;

                case "all":
                    _disableRubyGems = !enable;
                    LanguageSetup.Options["FrozenStringLiteral"] = enable ? 1 : -1;
                    break;

                default:
                    // did_you_mean, error_highlight, syntax_suggest, jit, yjit, rubyopt: nothing
                    // here implements them yet, and refusing the option would be worse than
                    // ignoring it.
                    break;
            }
        }

        private static string[] GetPaths(string input) {
            string[] paths = StringUtils.Split(input, new char[] { Path.PathSeparator }, Int32.MaxValue, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < paths.Length; i++) {
                // Trim any occurrances of "
                string[] parts = StringUtils.Split(paths[i], new char[] { '"' }, Int32.MaxValue, StringSplitOptions.RemoveEmptyEntries);
                paths[i] = String.Concat(parts);
            }
            return paths;
        }

        /// <exception cref="Exception">On error.</exception>
        protected override void ParseArgument(string arg) {
            ContractUtils.RequiresNotNull(arg, "arg");

            string mainFileFromPath = null;

            // The flags that take no argument cluster with whatever follows them, so -ne 'code'
            // is -n -e 'code'. Peeling one off at a time also covers -np, -nal and so on.
            if (arg.Length > 2 && arg[0] == '-' && "nplad".IndexOf(arg[1]) >= 0) {
                ParseArgument(arg.Substring(0, 2));
                ParseArgument("-" + arg.Substring(2));
                return;
            }

            if (arg.StartsWith("-e", StringComparison.Ordinal)) {
                string command;
                if (arg == "-e") {
                    command = PopNextArg();
                } else {
                    command = arg.Substring(2);
                }

                LanguageSetup.Options["MainFile"] = "-e";
                if (CommonConsoleOptions.Command == null) {
                    CommonConsoleOptions.Command = String.Empty;
                } else {
                    CommonConsoleOptions.Command += "\n";
                }
                CommonConsoleOptions.Command += command;
                return;
            }

            // --backtrace-limit=N caps how many frames an uncaught exception prints; Ruby code
            // reads the setting back through Thread::Backtrace.limit.
            if (arg.StartsWith("--backtrace-limit", StringComparison.Ordinal)) {
                string value = (arg == "--backtrace-limit") ? PopNextArg() : arg.Substring("--backtrace-limit".Length).TrimStart('=');
                int limit;
                if (!Int32.TryParse(value, out limit)) {
                    throw new InvalidOptionException(String.Format("invalid argument for --backtrace-limit: {0}", value));
                }
                LanguageSetup.Options["BacktraceLimit"] = limit;
                return;
            }

            if (arg.StartsWith("-S", StringComparison.Ordinal)) {
                mainFileFromPath = arg == "-S" ? PopNextArg() : arg.Substring(2);
            }

            if (arg.StartsWith("-I", StringComparison.Ordinal)) {
                string includePaths;
                if (arg == "-I") {
                    includePaths = PopNextArg();
                } else {
                    includePaths = arg.Substring(2);
                }

                _loadPaths.AddRange(GetPaths(includePaths));
                return;
            }

            if (arg.StartsWith("-K", StringComparison.Ordinal)) {
                _defaultEncoding = arg.Length >= 3 ? RubyEncoding.GetEncodingByNameInitial(arg[2]) : null;
                return;
            }

            // -Eext, -E ext, -Eext:int and the long spellings. Unlike -K this says nothing about
            // how the source is read: it sets Encoding.default_external (and default_internal).
            if (arg.StartsWith("-E", StringComparison.Ordinal) || arg.StartsWith("--encoding", StringComparison.Ordinal)) {
                string value;
                if (arg == "-E" || arg == "--encoding") {
                    value = PopNextArg();
                } else if (arg.StartsWith("--encoding=", StringComparison.Ordinal)) {
                    value = arg.Substring("--encoding=".Length);
                } else if (arg.StartsWith("-E", StringComparison.Ordinal)) {
                    value = arg.Substring(2);
                } else {
                    throw new InvalidOptionException(String.Format("Option `{0}' not supported", arg));
                }

                int separator = value.IndexOf(':');
                if (separator >= 0) {
                    _externalEncodingName = value.Substring(0, separator);
                    _internalEncodingName = value.Substring(separator + 1);
                } else {
                    _externalEncodingName = value;
                }
                return;
            }

            if (arg.StartsWith("--external-encoding", StringComparison.Ordinal)) {
                _externalEncodingName = (arg == "--external-encoding")
                    ? PopNextArg() : arg.Substring("--external-encoding=".Length);
                return;
            }

            if (arg.StartsWith("--internal-encoding", StringComparison.Ordinal)) {
                _internalEncodingName = (arg == "--internal-encoding")
                    ? PopNextArg() : arg.Substring("--internal-encoding=".Length);
                return;
            }

            // -U is -E's internal half on its own: default_internal becomes UTF-8 and the external
            // encoding is left alone.
            if (arg == "-U") {
                _internalEncodingName = "UTF-8";
                return;
            }

            // --enable=a,b / --disable=a,b and the hyphenated --enable-a / --disable-a spellings.
            // MRI accepts a comma separated list in the '=' form.
            if (arg.StartsWith("--enable", StringComparison.Ordinal) || arg.StartsWith("--disable", StringComparison.Ordinal)) {
                bool enable = arg.StartsWith("--enable", StringComparison.Ordinal);
                string prefix = enable ? "--enable" : "--disable";
                string features;
                if (arg == prefix) {
                    features = PopNextArg();
                } else if (arg[prefix.Length] == '=' || arg[prefix.Length] == '-') {
                    features = arg.Substring(prefix.Length + 1);
                } else {
                    throw new InvalidOptionException(String.Format("Option `{0}' not supported", arg));
                }

                foreach (var feature in features.Split(',')) {
                    SetFeature(feature.Trim(), enable);
                }
                return;
            }

            if (arg.StartsWith("-r", StringComparison.Ordinal)) {
                _requiredPaths.Add((arg == "-r") ? PopNextArg() : arg.Substring(2));
                return;
            }

            if (arg.StartsWith("-C", StringComparison.Ordinal)) {
                // -Cdir and -C dir are both valid, as for -r above
                ConsoleOptions.ChangeDirectory = (arg == "-C") ? PopNextArg() : arg.Substring(2);
                return;
            }

            // -0 alone means paragraph-less "\0", -0<octal> names the byte, as in -072 for ':'.
            if (arg.StartsWith("-0", StringComparison.Ordinal)) {
                int separator = 0;
                for (int i = 2; i < arg.Length; i++) {
                    if (arg[i] < '0' || arg[i] > '7') {
                        throw new InvalidOptionException(String.Format("Option `{0}' not supported", arg));
                    }
                    separator = separator * 8 + (arg[i] - '0');
                }
                LanguageSetup.Options["InputRecordSeparator"] = ((char)separator).ToString();
                return;
            }

            if (arg.StartsWith("-i", StringComparison.Ordinal)) {
                // -i[extension] edits in place; an extension keeps the original as a backup and no
                // extension throws it away. The empty string is a real answer here, so the option
                // has to carry it rather than being absent.
                LanguageSetup.Options["InplaceMode"] = arg.Substring(2);
                return;
            }

            if (arg.StartsWith("-C", StringComparison.Ordinal) ||
                arg.StartsWith("-F", StringComparison.Ordinal) ||
                arg.StartsWith("-T", StringComparison.Ordinal) ||
                arg.StartsWith("-x", StringComparison.Ordinal)) {
                throw new InvalidOptionException(String.Format("Option `{0}' not supported", arg));
            }

            int colon = arg.IndexOf(':');
            string optionName, optionValue;
            if (colon >= 0) {
                optionName = arg.Substring(0, colon);
                optionValue = arg.Substring(colon + 1);
            } else {
                optionName = arg;
                optionValue = null;
            }

            switch (optionName) {
                #region Ruby options

                case "-c":
                case "--copyright":
                case "-s":
                    throw new InvalidOptionException(String.Format("Option `{0}' not supported", optionName));

                case "-n":
                    LanguageSetup.Options["LoopOverInput"] = true;
                    break;

                case "-p":
                    LanguageSetup.Options["LoopOverInput"] = true;
                    LanguageSetup.Options["PrintEachLine"] = true;
                    break;

                case "-a":
                    LanguageSetup.Options["AutoSplit"] = true;
                    break;

                case "-l":
                    LanguageSetup.Options["ChopLines"] = true;
                    break;

                case "-d":
                case "--debug":
                    LanguageSetup.Options["DebugVariable"] = true; // $DEBUG = true
                    // --debug turns on every debugging aid MRI has, which includes naming the
                    // place a string literal was written.
                    LanguageSetup.Options["DebugFrozenStringLiteral"] = true;
                    break;

                case "--debug-frozen-string-literal":
                    LanguageSetup.Options["DebugFrozenStringLiteral"] = true;
                    break;

                case "--version":
                    ConsoleOptions.PrintVersion = true;
                    ConsoleOptions.Exit = true;
                    break;

                case "-v":
                    ConsoleOptions.DisplayVersion = true;
                    goto case "-W2";

                // -W:deprecated, -W:no-deprecated and the other category switches. The parser has
                // already split the colon off, so optionValue is the category.
                case "-W":
                    if (optionValue == null) {
                        goto case "-W2";
                    }
                    _warningCategoryFlags.Add(optionValue);
                    break;

                // The -W numeric levels also move the category bits, exactly as MRI's
                // proc_W_option does over RB_WARN_CATEGORY_DEFAULT_BITS (deprecated|experimental).
                // They share the ordered list with -W:category so that the later flag wins per bit.
                case "-W0":
                    LanguageSetup.Options["Verbosity"] = 0; // $VERBOSE = nil
                    _warningCategoryFlags.Add("no-deprecated");
                    _warningCategoryFlags.Add("no-experimental");
                    break;

                case "-W1":
                    LanguageSetup.Options["Verbosity"] = 1; // $VERBOSE = false
                    _warningCategoryFlags.Add("no-deprecated");
                    break;

                case "-w":
                case "-W2":
                    LanguageSetup.Options["Verbosity"] = 2; // $VERBOSE = true
                    _warningCategoryFlags.Add("deprecated");
                    _warningCategoryFlags.Add("experimental");
                    break;

                #endregion

#if DEBUG
                case "-DT*":
                    SetTraceFilter(String.Empty, false);
                    break;

                case "-DT":
                    SetTraceFilter(PopNextArg(), false);
                    break;

                case "-ET*":
                    SetTraceFilter(String.Empty, true);
                    break;

                case "-ET":
                    SetTraceFilter(PopNextArg(), true);
                    break;

                case "-ER":
                    RubyOptions.ShowRules = true;
                    break;

                case "-save":
                    LanguageSetup.Options["SavePath"] = optionValue ?? AppDomain.CurrentDomain.BaseDirectory;
                    break;

                case "-load":
                    LanguageSetup.Options["LoadFromDisk"] = ScriptingRuntimeHelpers.True;
                    break;

                case "-useThreadAbortForSyncRaise":
                    RubyOptions.UseThreadAbortForSyncRaise = true;
                    break;

                case "-compileRegexps":
                    RubyOptions.CompileRegexps = true;
                    break;
#endif
                case "-trace":
                    LanguageSetup.Options["EnableTracing"] = ScriptingRuntimeHelpers.True;
                    break;

                case "-profile":
                    LanguageSetup.Options["Profile"] = ScriptingRuntimeHelpers.True;
                    break;

                case "-1.8.6":
                case "-1.8.7":
                case "-1.9":
                case "-2.0":
                    throw new InvalidOptionException(String.Format("Option `{0}' is no longer supported. The compatible Ruby version is 1.9.", optionName));

                case "-X":
                    switch (optionValue) {
                        case "AutoIndent":
                        case "TabCompletion":
                        case "ColorfulConsole":
                            throw new InvalidOptionException(String.Format("Option `{0}' not supported", optionName));

                        case "ObjectSpace":
                            LanguageSetup.Options["ObjectSpace"] = ScriptingRuntimeHelpers.True;
                            return;
                    }
                    goto default;
                    
               default:
                    base.ParseArgument(arg);

                    if (ConsoleOptions.FileName != null) {
                        if (mainFileFromPath != null) {
                            ConsoleOptions.FileName = FindMainFileFromPath(mainFileFromPath);
                        }

                        if (ConsoleOptions.Command == null) {
                            SetupOptionsForMainFile();
                        } else {
                            SetupOptionsForCommand();
                        }
                    } 
                    break;
            }
        }

        private void SetupOptionsForMainFile() {
            LanguageSetup.Options["MainFile"] = RubyUtils.CanonicalizePath(ConsoleOptions.FileName);
            LanguageSetup.Options["Arguments"] = PopRemainingArgs();;
        }

        private void SetupOptionsForCommand() {
            string firstArg = ConsoleOptions.FileName;
            ConsoleOptions.FileName = null;

            List<string> args = new List<string>(new string[] { firstArg });
            args.AddRange(PopRemainingArgs());

            LanguageSetup.Options["MainFile"] = "-e";
            LanguageSetup.Options["Arguments"] = args.ToArray();
        }

        private string FindMainFileFromPath(string mainFileFromPath) {
            string path = Platform.GetEnvironmentVariable("PATH");
            foreach (string p in path.Split(';')) {
                string fullPath = RubyUtils.CombinePaths(p, mainFileFromPath);
                if (Platform.FileExists(fullPath)) {
                    return fullPath;
                }
            }
            return mainFileFromPath;
        }

        protected override void AfterParse() {
            var existingSearchPaths =
                LanguageOptions.GetSearchPathsOption(LanguageSetup.Options) ??
                LanguageOptions.GetSearchPathsOption(RuntimeSetup.Options);

            if (existingSearchPaths != null) {
                _loadPaths.InsertRange(0, existingSearchPaths);
            }

            try {
                string rubylib = Environment.GetEnvironmentVariable("RUBYLIB");
                if (rubylib != null) {
                    _loadPaths.AddRange(GetPaths(rubylib));
                }
            } catch (SecurityException) {
                // nop
            }
            LanguageSetup.Options["SearchPaths"] = _loadPaths;

            if (!_disableRubyGems) {
                _requiredPaths.Insert(0, "gem_prelude.rb");
            } else {
                // gem_prelude.rb ends by requiring ruby4.rb, the Ruby half of the core library
                // (Set, pattern matching, ...). --disable-gems only turns off RubyGems in MRI,
                // so the core layer is still loaded.
                _requiredPaths.Insert(0, "ruby4.rb");
            }

            LanguageSetup.Options["RequiredPaths"] = _requiredPaths;

            LanguageSetup.Options["DefaultEncoding"] = _defaultEncoding;
            LanguageSetup.Options["WarningCategoryFlags"] = _warningCategoryFlags;
            LanguageSetup.Options["ExternalEncoding"] = _externalEncodingName;
            LanguageSetup.Options["InternalEncoding"] = _internalEncodingName;
            LanguageSetup.Options["LocaleEncoding"] = _defaultEncoding ??
                RubyEncoding.GetRubyEncoding(Console.InputEncoding);

#if DEBUG
            // Can be set to nl-BE, ja-JP, etc
            string culture = Environment.GetEnvironmentVariable("IR_CULTURE");
            if (culture != null) {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(culture, false);
            }
#endif
            if (ConsoleOptions.DisplayVersion && ConsoleOptions.Command == null && ConsoleOptions.FileName == null) {
                ConsoleOptions.PrintVersion = true;
                ConsoleOptions.Exit = true;
            }
        }

        public override void GetHelp(out string commandLine, out string[,] options, out string[,] environmentVariables, out string comments) {
            commandLine = "[options] [file] [arguments]";
            environmentVariables = null;
            comments = null;

            options = new string[,] {
             // { "-0[octal]",                   "specify record separator (\0, if no argument)" },
             // { "-a",                          "autosplit mode with -n or -p (splits $_ into $F)" },
             // { "-c",                          "check syntax only" },
                { "-Cdirectory",                 "cd to directory, before executing your script" },
                { "-d",                          "set debugging flags (set $DEBUG to true)" },
                { "-D",                          "emit debugging information (PDBs) for Visual Studio debugger" },
                { "-e 'command'",                "one line of script. Several -e's allowed. Omit [file]" },
             // { "-Fpattern",                   "split() pattern for autosplit (-a)" },
                { "-h[elp]",                     "Display usage" },
             // { "-i[extension]",               "edit ARGV files in place (make backup if extension supplied)" },
                { "-Idirectory",                 "specify $LOAD_PATH directory (may be used more than once)" },
                { "-Kkcode",                     "specifies KANJI (Japanese) code-set" },
             // { "-l",                          "enable line ending processing" },
             // { "-n",                          "assume 'while gets(); ... end' loop around your script" },
             // { "-p",                          "assume loop like -n but print line also like sed" },
                { "-rlibrary",                   "require the library, before executing your script" },
             // { "-s",                          "enable some switch parsing for switches after script name" },
                { "-S",                          "look for the script using PATH environment variable" },
             // { "-T[level]",                   "turn on tainting checks" },
                { "-v",                          "print version number, then turn on verbose mode" },
                { "-w",                          "turn warnings on for your script" },
                { "-W[level]",                   "set warning level; 0=silence, 1=medium (default), 2=verbose" },
             // { "-x[directory]",               "strip off text before #!ruby line and perhaps cd to directory" },
             // { "--copyright",                 "print the copyright" },
                { "--version",                   "print the version" },

                { "-trace",                      "enable support for set_trace_func" },
                { "-profile",                    "enable support for 'pi = IronRuby::Clr.profile { block_to_profile }'" },
                
                { "-X:ExceptionDetail",          "enable ExceptionDetail mode" },
                { "-X:ObjectSpace",              "let ObjectSpace.each_object find objects, not just modules (slows down Object#new)" },
                { "-X:NoAdaptiveCompilation",    "disable adaptive compilation - all code will be compiled" },
                { "-X:CompilationThreshold",     "the number of iterations before the interpreter starts compiling" },
                { "-X:PassExceptions",           "do not catch exceptions that are unhandled by script code" },
                { "-X:PrivateBinding",           "enable binding to private members" },
                { "-X:ShowClrExceptions",        "display CLS Exception information" },
                { "-X:RemoteRuntimeChannel",     "remote console channel" }, 
             // { "-X:AutoIndent",               "Enable auto-indenting in the REPL loop" },
             // { "-X:TabCompletion",            "Enable TabCompletion mode" },
             // { "-X:ColorfulConsole",          "Enable ColorfulConsole" },

#if DEBUG
                { "-DT",                         "disables tracing of specified events [debug only]" },
                { "-DT*",                        "disables tracing of all events [debug only]" },
                { "-ET",                         "enables tracing of specified events [debug only]" },
                { "-ET*",                        "enables tracing of all events [debug only]" },
                { "-save [path]",                "save generated code to given path [debug only]" },
                { "-load",                       "load pre-compiled code [debug only]" },
                { "-useThreadAbortForSyncRaise", "for testing purposes [debug only]" },
                { "-compileRegexps",             "faster throughput, slower startup [debug only]" },
                { "-X:AssembliesDir <dir>",      "set the directory for saving generated assemblies [debug only]" },
                { "-X:SaveAssemblies",           "save generated assemblies [debug only]" },
                { "-X:TrackPerformance",         "track performance sensitive areas [debug only]" },
                { "-X:PerfStats",                "print performance stats when the process exists [debug only]" },
#endif
            };
        }
    }
}
