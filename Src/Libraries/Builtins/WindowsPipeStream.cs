/* ****************************************************************************
 *
 * One end of a Windows anonymous pipe (IO.pipe, popen, a spawned child's
 * standard streams), for the one thing a plain FileStream cannot do there.
 *
 * A ReadFile on an anonymous pipe blocks in the kernel until the writer writes or
 * goes away, and nothing a managed thread can do reaches it: closing the handle
 * from another thread does not wake it (the read keeps its own reference to the
 * pipe), and Thread.Interrupt waits for the thread to come back into managed
 * code.  Ruby defines both: IO#close from another thread makes the blocked read
 * raise IOError ("stream closed in another thread"), and Thread#raise / #kill
 * interrupt it.  On Windows neither happened - close_spec's reader thread sat in
 * read(2) for ever, and so did every thread killed while waiting for a child.
 *
 * Unix gets there by waiting in poll(2) in slices (DescriptorStream).  There is
 * nothing to poll an anonymous pipe with, but there is CancelSynchronousIo, which
 * makes a thread's pending synchronous ReadFile/WriteFile fail with
 * ERROR_OPERATION_ABORTED.  So a thread about to read or write registers itself,
 * with a real handle to itself, and a close - or RubyUtils.NativeIoCanceller, which
 * Thread#raise and Thread#kill call - cancels the I/O of whichever registered
 * thread it is after.  The thread then looks at why: the stream closed (IOError),
 * an asynchronous exception parked for it (raised from here), or neither (the
 * cancel was meant for someone else's I/O on the same thread - it cannot be - so it
 * simply reads again).
 *
 * A cancel can arrive between the thread's last look and its ReadFile, and is then
 * lost, since CancelSynchronousIo cancels only I/O that is already pending.  The
 * canceller therefore repeats itself until the thread has left, for a bounded time.
 *
 * ***************************************************************************/
#if FEATURE_PROCESS

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    internal sealed class WindowsPipeStream : FileStream {
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CancelSynchronousIo(IntPtr thread);

        [DllImport("kernel32")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
            out IntPtr target, int access, bool inheritHandle, int options);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const int DUPLICATE_SAME_ACCESS = 0x00000002;
        private const int ERROR_OPERATION_ABORTED = 995;

        /// <summary>
        /// Every thread that is inside a ReadFile or WriteFile on some pipe, with a real handle
        /// to it (GetCurrentThread's pseudo handle means "me" to whoever uses it).
        /// </summary>
        private static readonly Dictionary<Thread, IntPtr> _blocked = new Dictionary<Thread, IntPtr>();

        /// <summary>The threads inside a ReadFile or WriteFile on this pipe.</summary>
        private readonly List<Thread> _users = new List<Thread>();

        private volatile bool _closed;

        static WindowsPipeStream() {
            RubyUtils.NativeIoCanceller += CancelBlockedIo;
        }

        public WindowsPipeStream(SafeFileHandle/*!*/ handle, FileAccess access)
            : base(handle, access, 1, false) {
        }

        // Stream's other entry points - ReadByte, the Span overloads, CopyTo - come back to these
        // two, FileStream's included once it has been derived from.

        public override int Read(byte[] buffer, int offset, int count) {
            if (count == 0) {
                return 0;
            }
            return Blocking(() => base.Read(buffer, offset, count));
        }

        public override void Write(byte[] buffer, int offset, int count) {
            if (count == 0) {
                return;
            }
            Blocking(() => { base.Write(buffer, offset, count); return 0; });
        }

        private T Blocking<T>(Func<T>/*!*/ io) {
            Thread thread = Thread.CurrentThread;
            IntPtr self;
            if (!DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), out self, 0, false, DUPLICATE_SAME_ACCESS)) {
                // Uncancellable, but still a read.
                return io();
            }

            lock (_blocked) {
                _blocked[thread] = self;
                _users.Add(thread);
            }
            bool wasBlocked = RubyUtils.EnterNativeWait();
            try {
                while (true) {
                    if (_closed) {
                        throw RubyExceptions.CreateIOError("stream closed in another thread");
                    }
                    RubyUtils.CheckAsyncException();
                    try {
                        return io();
                    } catch (OperationCanceledException) {
                    } catch (IOException e) when ((e.HResult & 0xFFFF) == ERROR_OPERATION_ABORTED) {
                    } catch (ObjectDisposedException) when (_closed) {
                        // closed between the look above and the call
                    }
                }
            } finally {
                RubyUtils.ExitNativeWait(wasBlocked);
                lock (_blocked) {
                    _blocked.Remove(thread);
                    _users.Remove(thread);
                }
                CloseHandle(self);
            }
        }

        /// <summary>RubyUtils.NativeIoCanceller: Thread#raise or #kill has parked something for <paramref name="thread"/>.</summary>
        private static void CancelBlockedIo(Thread/*!*/ thread) {
            CancelUntilGone(new[] { thread }, null);
        }

        /// <summary>
        /// Cancels the pipe I/O of each of <paramref name="threads"/> until it has left it (or
        /// <paramref name="stream"/>, when given, no longer lists it), for at most about a second.
        /// </summary>
        private static void CancelUntilGone(IList<Thread>/*!*/ threads, WindowsPipeStream stream) {
            for (int attempt = 0; attempt < 200; attempt++) {
                bool any = false;
                lock (_blocked) {
                    foreach (var thread in threads) {
                        IntPtr handle;
                        if ((stream == null || stream._users.Contains(thread)) && _blocked.TryGetValue(thread, out handle)) {
                            any = true;
                            CancelSynchronousIo(handle);
                        }
                    }
                }
                if (!any) {
                    return;
                }
                if (attempt < 20) {
                    Thread.Yield();
                } else {
                    Thread.Sleep(5);
                }
            }
        }

        protected override void Dispose(bool disposing) {
            _closed = true;
            Thread[] users;
            lock (_blocked) {
                users = _users.ToArray();
            }
            if (users.Length > 0) {
                CancelUntilGone(users, this);
            }
            base.Dispose(disposing);
        }
    }
}

#endif
