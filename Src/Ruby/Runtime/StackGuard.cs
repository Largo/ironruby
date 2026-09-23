/* ****************************************************************************
 *
 * Deep recursion raises SystemStackError instead of killing the process.
 *
 * The CLR cannot catch a StackOverflowException: it terminates the process, so a Ruby
 * program with a recursion bug - or a legitimately deep recursion over a big input - would
 * take the whole host down with it. The overflow therefore has to be prevented: running
 * Ruby code checks how much stack is left where it can recurse - method and block entry
 * (RubyOps.CreateMethodScope, CreateBlockScope), a JIT-specialized body that calls itself
 * (JitRuntime.EnterSelfCall), the recursion guard the CLR-implemented builtins use for
 * inspect, ==, eql?, hash and <=> (RubyUtils.RecursionTracker.TrackObject), Array#join and
 * Marshal - and raises SystemStackError while there is still room to unwind.
 *
 * The check is one [ThreadStatic] load, one compare against the address of a local and a
 * branch not taken. The limit is the thread's real stack bound (pthread_getattr_np, or
 * GetCurrentThreadStackLimits on Windows) plus a margin: 1/8 of the stack, at least 256K and
 * at most 1M. The margin is what the exception needs to be raised and handled at the bottom
 * of the stack, which is more than in MRI: a CLR catch handler runs on top of the frames it is
 * unwinding, which only go away once it has finished - and IronRuby's rescue clauses, and the
 * catch that captures an exception's backtrace in every method frame, are catch handlers.
 * The first of them to see the error builds its backtrace (see BacktraceLimit) right there.
 *
 * So once the error has been raised the thread is allowed half the margin more, until its
 * stack is back well above the point where it was raised: a rescue or an ensure clause can
 * call Ruby methods without instantly hitting the same limit again. Deeper than that raises
 * again - it never crashes.
 *
 * ***************************************************************************/

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Builtins;

namespace IronRuby.Runtime {
    public static class StackGuard {
        internal const string Message = "stack level too deep";

        // The fast path's limit, stored complemented so that the default 0 of a thread that has
        // not been set up yet (and the "check everything" state) reads as "every address is
        // below the limit": the first check on a thread is always the slow one.
        [ThreadStatic]
        private static nuint _limitComplement;

        private const byte Uninitialized = 0, Normal = 1, Overflowed = 2, Unknown = 3;

        [ThreadStatic]
        private static byte _state;

        // Raise below _softLimit; once raised, below _hardLimit (half the margin further down)
        // until the stack is back above _overflowPoint + _margin.
        [ThreadStatic]
        private static nuint _softLimit;
        [ThreadStatic]
        private static nuint _hardLimit;
        [ThreadStatic]
        private static nuint _overflowPoint;
        [ThreadStatic]
        private static nuint _margin;

        /// <summary>
        /// A SystemStackError's backtrace keeps the innermost this many frames. MRI keeps all of
        /// them, some ten thousand; here there can be twice that, and each one costs - the stack
        /// walk, the frame's name and position, a Thread::Backtrace::Location - so a complete one
        /// took seconds. The innermost frames are the recursion, which is what the trace is for.
        /// </summary>
        internal const int BacktraceLimit = 1000;

        private const int MinMargin = 256 * 1024;
        private const int MaxMargin = 1024 * 1024;

        /// <summary>
        /// Raises SystemStackError if the current thread's stack is nearly exhausted. Cheap
        /// enough for every method and block entry.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Check() {
            byte probe = 0;
            nuint sp = (nuint)Unsafe.ByteOffset(ref Unsafe.NullRef<byte>(), ref probe);
            if (sp < ~_limitComplement) {
                CheckSlow(sp);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CheckSlow(nuint sp) {
            switch (_state) {
                case Uninitialized:
                    Initialize();
                    if (_state == Normal && sp < _softLimit) {
                        Overflow(sp);
                    }
                    return;

                case Normal:
                    if (sp < _softLimit) {
                        Overflow(sp);
                    }
                    return;

                case Overflowed:
                    if (sp > _overflowPoint + _margin) {
                        // unwound: back to the normal limit and the fast path
                        _state = Normal;
                        _limitComplement = ~_softLimit;
                    } else if (sp < _hardLimit) {
                        throw new SystemStackError(Message);
                    }
                    return;

                default:
                    // The stack bounds could not be determined: ask the runtime every time. Its
                    // margin is smaller (64K/128K), but it is better than a crash.
                    if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) {
                        throw new SystemStackError(Message);
                    }
                    return;
            }
        }

        private static void Overflow(nuint sp) {
            _state = Overflowed;
            _overflowPoint = sp;
            _limitComplement = 0;   // every check takes the slow path until the stack unwinds
            throw new SystemStackError(Message);
        }

        private static void Initialize() {
            nuint low, high;
            if (!TryGetStackBounds(out low, out high) || high <= low) {
                _state = Unknown;
                return;
            }

            nuint size = high - low;
            nuint margin = size / 8;
            if (margin < MinMargin) margin = MinMargin;
            if (margin > MaxMargin) margin = MaxMargin;
            if (margin * 2 > size) {
                // a tiny stack: whatever it has, keep a quarter of it for the unwinding
                margin = size / 4;
            }

            _margin = margin;
            _softLimit = low + margin;
            _hardLimit = low + margin / 2;
            _limitComplement = ~_softLimit;
            _state = Normal;
        }

        /// <summary>
        /// Turns on the DLR's Utils.RethrowOutsideCatch before any Ruby code is compiled: an
        /// exception a frame intercepts only to let it go on is rethrown after the CLR catch has
        /// been left, not with a rethrow inside it.
        ///
        /// Every Ruby method and block frame intercepts every exception that passes through it:
        /// its filter (the backtrace capture, the `return' unwinder check, `rescue') is emulated
        /// as catch-test-rethrow, and an interpreted frame catches for its finally blocks. A
        /// rethrow inside a catch is a dispatch nested in the one being handled; on .NET 8 that
        /// made an exception through n frames cost O(n^2) - a raise 4,000 frames down took 50s,
        /// so a SystemStackError from 20,000 down would have taken the best part of an hour -
        /// and on .NET 9+ each nesting keeps the dispatch machinery on the stack, so an exception
        /// through some 1,500 Ruby frames overflowed it and killed the process. A plain throw
        /// restarts the CLR stack trace, which IronRuby does not need: it captures an exception's
        /// backtrace in the first frame that sees it.
        ///
        /// Set by reflection so that this builds and runs against a DLR without the switch, which
        /// simply keeps rethrowing inside the catch.
        /// </summary>
        internal static void ConfigureDlr() {
            if (!_dlrConfigured) {
                _dlrConfigured = true;
                var property = typeof(Microsoft.Scripting.Ast.Utils).GetProperty("RethrowOutsideCatch", BindingFlags.Public | BindingFlags.Static);
                if (property != null && property.CanWrite) {
                    property.SetValue(null, true);
                }
            }
        }

        private static bool _dlrConfigured;

        #region Stack bounds

        private static bool TryGetStackBounds(out nuint low, out nuint high) {
            low = high = 0;
            try {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    GetCurrentThreadStackLimits(out low, out high);
                    return true;
                }
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
                    IntPtr self = pthread_self();
                    high = (nuint)(nint)pthread_get_stackaddr_np(self);
                    low = high - pthread_get_stacksize_np(self);
                    return true;
                }
                return TryGetPosixStackBounds(out low, out high);
            } catch (Exception) {
                // DllNotFoundException, EntryPointNotFoundException: an unusual libc
                low = high = 0;
                return false;
            }
        }

        private static bool TryGetPosixStackBounds(out nuint low, out nuint high) {
            low = high = 0;
            // pthread_attr_t is 56 bytes on x64 glibc, 64 on arm64; musl's is smaller.
            IntPtr attr = Marshal.AllocHGlobal(512);
            try {
                if (pthread_getattr_np(pthread_self(), attr) != 0) {
                    return false;
                }
                try {
                    IntPtr addr;
                    nuint size;
                    if (pthread_attr_getstack(attr, out addr, out size) != 0) {
                        return false;
                    }
                    low = (nuint)(nint)addr;
                    high = low + size;
                    return true;
                } finally {
                    pthread_attr_destroy(attr);
                }
            } finally {
                Marshal.FreeHGlobal(attr);
            }
        }

        [DllImport("kernel32")]
        private static extern void GetCurrentThreadStackLimits(out nuint lowLimit, out nuint highLimit);

        [DllImport("libc")]
        private static extern IntPtr pthread_self();

        [DllImport("libc")]
        private static extern int pthread_getattr_np(IntPtr thread, IntPtr attr);

        [DllImport("libc")]
        private static extern int pthread_attr_getstack(IntPtr attr, out IntPtr stackAddr, out nuint stackSize);

        [DllImport("libc")]
        private static extern int pthread_attr_destroy(IntPtr attr);

        [DllImport("libc")]
        private static extern IntPtr pthread_get_stackaddr_np(IntPtr thread);

        [DllImport("libc")]
        private static extern nuint pthread_get_stacksize_np(IntPtr thread);

        #endregion
    }
}
