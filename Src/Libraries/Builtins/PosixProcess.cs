/* ****************************************************************************
 *
 * Thin wrappers over the POSIX process calls .NET does not expose.
 *
 * Each one hands back an Integer rather than throwing: a negative result is
 * -errno.  The prelude turns that into the right Errno:: class, which is where
 * it has to happen anyway - most of the Errno family is defined in Ruby there
 * and has no CLR type for C# code to throw.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    public static partial class RubyProcess {

        [StructLayout(LayoutKind.Sequential)]
        private struct RLimit {
            public ulong Current;
            public ulong Maximum;
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "getrlimit")]
        private static extern int SysGetRLimit(int resource, out RLimit limit);

        [DllImport("libc", SetLastError = true, EntryPoint = "setrlimit")]
        private static extern int SysSetRLimit(int resource, ref RLimit limit);

        [DllImport("libc", SetLastError = true, EntryPoint = "getpgid")]
        private static extern int SysGetPgid(int pid);

        [DllImport("libc", SetLastError = true, EntryPoint = "setpgid")]
        private static extern int SysSetPgid(int pid, int pgid);

        [DllImport("libc", SetLastError = true, EntryPoint = "getsid")]
        private static extern int SysGetSid(int pid);

        [DllImport("libc", SetLastError = true, EntryPoint = "setsid")]
        private static extern int SysSetSid();

        [DllImport("libc", SetLastError = true, EntryPoint = "getpriority")]
        private static extern int SysGetPriority(int which, int who);

        [DllImport("libc", SetLastError = true, EntryPoint = "setpriority")]
        private static extern int SysSetPriority(int which, int who, int priority);

        [DllImport("libc", SetLastError = true, EntryPoint = "geteuid")]
        private static extern int SysGetEuid();

        [DllImport("libc", SetLastError = true, EntryPoint = "getegid")]
        private static extern int SysGetEgid();

        [DllImport("libc", SetLastError = true, EntryPoint = "issetugid")]
        private static extern int SysIsSetUgid();

        private static int Failure() {
            int error = Marshal.GetLastWin32Error();
            return -(error == 0 ? 1 : error);
        }

        /// <summary>[soft, hard] for the resource, or a negative errno.</summary>
        [RubyMethod("__getrlimit__", RubyMethodAttributes.PublicSingleton)]
        public static object GetRLimit(RubyModule/*!*/ self, [DefaultProtocol]int resource) {
            RLimit limit;
            if (SysGetRLimit(resource, out limit) != 0) {
                return ScriptingRuntimeHelpers.Int32ToObject(Failure());
            }
            return new RubyArray { Clamp(limit.Current), Clamp(limit.Maximum) };
        }

        // RLIM_INFINITY does not fit a Fixnum; hand it over as the unsigned value so the
        // prelude can compare it against Process::RLIM_INFINITY.
        private static object Clamp(ulong value) {
            return (value <= Int32.MaxValue)
                ? ScriptingRuntimeHelpers.Int32ToObject((int)value)
                : (object)new System.Numerics.BigInteger(value);
        }

        [RubyMethod("__setrlimit__", RubyMethodAttributes.PublicSingleton)]
        public static int SetRLimit(RubyModule/*!*/ self, [DefaultProtocol]int resource,
            [DefaultProtocol]long soft, [DefaultProtocol]long hard) {

            var limit = new RLimit { Current = unchecked((ulong)soft), Maximum = unchecked((ulong)hard) };
            return (SysSetRLimit(resource, ref limit) != 0) ? Failure() : 0;
        }

        [RubyMethod("__getpgid__", RubyMethodAttributes.PublicSingleton)]
        public static int GetPgid(RubyModule/*!*/ self, [DefaultProtocol]int pid) {
            int result = SysGetPgid(pid);
            return (result < 0) ? Failure() : result;
        }

        [RubyMethod("__setpgid__", RubyMethodAttributes.PublicSingleton)]
        public static int SetPgid(RubyModule/*!*/ self, [DefaultProtocol]int pid, [DefaultProtocol]int pgid) {
            return (SysSetPgid(pid, pgid) != 0) ? Failure() : 0;
        }

        [RubyMethod("__getsid__", RubyMethodAttributes.PublicSingleton)]
        public static int GetSid(RubyModule/*!*/ self, [DefaultProtocol]int pid) {
            int result = SysGetSid(pid);
            return (result < 0) ? Failure() : result;
        }

        [RubyMethod("__setsid__", RubyMethodAttributes.PublicSingleton)]
        public static int SetSid(RubyModule/*!*/ self) {
            int result = SysSetSid();
            return (result < 0) ? Failure() : result;
        }

        [RubyMethod("__getpriority__", RubyMethodAttributes.PublicSingleton)]
        public static int GetPriority(RubyModule/*!*/ self, [DefaultProtocol]int which, [DefaultProtocol]int who) {
            // getpriority legitimately returns -1, so errno has to be cleared first; the prelude
            // asks for the value and only treats a result below -20 as an error.
            Marshal.SetLastSystemError(0);
            int result = SysGetPriority(which, who);
            if (result == -1 && Marshal.GetLastWin32Error() != 0) {
                return Failure();
            }
            return result;
        }

        [RubyMethod("__setpriority__", RubyMethodAttributes.PublicSingleton)]
        public static int SetPriority(RubyModule/*!*/ self, [DefaultProtocol]int which,
            [DefaultProtocol]int who, [DefaultProtocol]int priority) {
            return (SysSetPriority(which, who, priority) != 0) ? Failure() : 0;
        }

        [RubyMethod("__geteuid__", RubyMethodAttributes.PublicSingleton)]
        public static int GetEuid(RubyModule/*!*/ self) {
            return SysGetEuid();
        }

        [RubyMethod("__getegid__", RubyMethodAttributes.PublicSingleton)]
        public static int GetEgid(RubyModule/*!*/ self) {
            return SysGetEgid();
        }

        [RubyMethod("__issetugid__", RubyMethodAttributes.PublicSingleton)]
        public static object IsSetUgid(RubyModule/*!*/ self) {
            try {
                return (SysIsSetUgid() != 0);
            } catch (EntryPointNotFoundException) {
                // Linux has no issetugid(2).
                return null;
            }
        }
    }
}
