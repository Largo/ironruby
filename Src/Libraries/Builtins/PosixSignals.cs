/* ****************************************************************************
 *
 * Signal numbers, name parsing and the process-wide handler table shared by
 * Signal.trap and Process.kill.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Signals were stubbed out in IronRuby: Signal.trap quietly did nothing except for "INT" and
    /// Process.kill raised NotImplementedError. That left ruby/spec waiting forever for a child
    /// process that only a signal can end, so both are implemented here on top of libc's kill(2)
    /// and .NET's PosixSignalRegistration.
    /// </summary>
    internal static class PosixSignals {
        // Linux signal numbers. They are what Signal.list has always reported and what the
        // platform we run ruby/spec on uses.
        private static readonly Dictionary<string, int> _numbers = new Dictionary<string, int>(StringComparer.Ordinal) {
            { "HUP", 1 }, { "INT", 2 }, { "QUIT", 3 }, { "ILL", 4 }, { "TRAP", 5 },
            { "ABRT", 6 }, { "IOT", 6 }, { "BUS", 7 }, { "FPE", 8 }, { "KILL", 9 },
            { "USR1", 10 }, { "SEGV", 11 }, { "USR2", 12 }, { "PIPE", 13 }, { "ALRM", 14 },
            { "TERM", 15 }, { "STKFLT", 16 }, { "CHLD", 17 }, { "CLD", 17 }, { "CONT", 18 },
            { "STOP", 19 }, { "TSTP", 20 }, { "TTIN", 21 }, { "TTOU", 22 }, { "URG", 23 },
            { "XCPU", 24 }, { "XFSZ", 25 }, { "VTALRM", 26 }, { "PROF", 27 }, { "WINCH", 28 },
            { "IO", 29 }, { "POLL", 29 }, { "PWR", 30 }, { "SYS", 31 },
        };

        internal static IEnumerable<KeyValuePair<string, int>> Numbers {
            get { return _numbers; }
        }

        /// <summary>
        /// "TERM", "SIGTERM", :SIGTERM and 15 all name signal 15. EXIT (0) is accepted as a name
        /// the way MRI accepts it, and anything else is an ArgumentError - deliberately without
        /// asking the object for #to_int, which ruby/spec checks for.
        /// </summary>
        internal static int ToNumber(object signalId) {
            if (signalId is int) {
                return (int)signalId;
            }

            string name = null;
            var str = signalId as MutableString;
            if (str != null) {
                name = str.ConvertToString();
            } else {
                var symbol = signalId as RubySymbol;
                if (symbol != null) {
                    name = symbol.ToString();
                }
            }

            if (name == null) {
                throw RubyExceptions.CreateArgumentError("bad signal type {0}",
                    signalId == null ? "NilClass" : signalId.GetType().Name);
            }

            string bare = name.StartsWith("SIG", StringComparison.Ordinal) ? name.Substring(3) : name;
            if (bare == "EXIT") {
                return 0;
            }

            int number;
            if (!_numbers.TryGetValue(bare, out number)) {
                throw RubyExceptions.CreateArgumentError("unsupported signal `{0}'",
                    name.StartsWith("SIG", StringComparison.Ordinal) ? name : "SIG" + name);
            }
            return number;
        }

        internal static string ToName(int number) {
            foreach (var entry in _numbers) {
                if (entry.Value == number) {
                    return entry.Key;
                }
            }
            return null;
        }

        #region kill

        [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
        private static extern int SysKill(int pid, int signal);

        private const int ESRCH = 3;
        private const int EPERM = 1;
        private const int EINVAL = 22;

        /// <summary>
        /// Signals whose default disposition is to end the process. MRI installs a handler for each of
        /// these and turns them into a SignalException instead of letting the process die, so sending
        /// one to yourself is something a Ruby program can rescue. CHLD, CONT, URG and WINCH are
        /// ignored by default, and KILL and STOP cannot be caught at all.
        /// </summary>
        internal static bool TerminatesByDefault(int signal) {
            switch (signal) {
                case 9:   // KILL
                case 17:  // CHLD
                case 18:  // CONT
                case 19:  // STOP
                case 20:  // TSTP
                case 21:  // TTIN
                case 22:  // TTOU
                case 23:  // URG
                case 28:  // WINCH
                    return false;
                default:
                    return signal > 0;
            }
        }

        internal static bool HasHandler(int signal) {
            lock (_handlers) {
                return _handlers.ContainsKey(signal);
            }
        }

        /// <summary>
        /// Sends the signal and returns 0, or -errno. Errno::ESRCH and most of its siblings are
        /// defined in Ruby by the prelude and have no CLR type to throw, so the number goes back
        /// to the prelude, which knows how to turn it into the right class.
        /// </summary>
        internal static int Kill(int pid, int signal) {
            if (SysKill(pid, signal) == 0) {
                return 0;
            }

            int error = Marshal.GetLastWin32Error();
            if (error == EINVAL) {
                throw RubyExceptions.CreateArgumentError("invalid signal number ({0})", signal);
            }
            return -(error == 0 ? 1 : error);
        }

        #endregion

        #region trap

        private sealed class Handler {
            public object Command;                       // Proc, or a MutableString such as "DEFAULT"
            public PosixSignalRegistration Registration;
        }

        private static readonly Dictionary<int, Handler> _handlers = new Dictionary<int, Handler>();

        /// <summary>
        /// Installs a handler for the signal and returns whatever was installed before, using MRI's
        /// spelling of "nothing was installed" ("DEFAULT"). <paramref name="invoke"/> runs the Ruby
        /// side of the handler; it is null for "IGNORE", which only cancels the default disposition.
        /// </summary>
        internal static object Trap(int signal, object command, Action<int> invoke) {
            lock (_handlers) {
                Handler handler;
                object previous = MutableString.CreateAscii("DEFAULT");
                if (_handlers.TryGetValue(signal, out handler)) {
                    previous = handler.Command;
                    if (handler.Registration != null) {
                        handler.Registration.Dispose();
                    }
                    _handlers.Remove(signal);
                }

                if (IsDefault(command)) {
                    // Nothing left to run: let the platform do whatever it does by default.
                    return previous;
                }

                var installed = new Handler { Command = command };
                installed.Registration = TryRegister(signal, IsIgnore(command) ? null : invoke);
                _handlers[signal] = installed;
                return previous;
            }
        }

        private static bool IsDefault(object command) {
            var str = command as MutableString;
            if (str == null) {
                return command == null;
            }
            string s = str.ConvertToString();
            return s == "DEFAULT" || s == "SIG_DFL";
        }

        private static bool IsIgnore(object command) {
            var str = command as MutableString;
            if (str == null) {
                return false;
            }
            string s = str.ConvertToString();
            return s == "IGNORE" || s == "SIG_IGN";
        }

        private static PosixSignalRegistration TryRegister(int signal, Action<int> invoke) {
            try {
                return PosixSignalRegistration.Create((PosixSignal)signal, context => {
                    // Whatever the signal would normally do to the process, a Ruby handler replaces it.
                    context.Cancel = true;
                    if (invoke != null) {
                        try {
                            invoke(signal);
                        } catch (Exception) {
                            // A raise out of a signal handler has nowhere to go on this thread.
                        }
                    }
                });
            } catch (Exception) {
                // Unsupported signal number, or a platform without POSIX signals: the handler is
                // still recorded so that #trap reports it back, it just never fires.
                return null;
            }
        }

        #endregion
    }
}
