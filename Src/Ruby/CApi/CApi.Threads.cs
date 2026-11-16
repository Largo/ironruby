/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// rb_thread_call_without_gvl's blocking regions (native/capi.c): while C code runs one, its
    /// thread is "sleep", and an interrupt runs the unblocking function the extension gave
    /// (RubyUtils.NativeUnblockHook).
    /// </summary>
    internal static unsafe partial class CApi {
        private sealed class BlockingRegion {
            public IntPtr Ubf, Data, NativeThread;
            public bool WasBlocked;
        }

        private static readonly ConcurrentDictionary<Thread, BlockingRegion> _blockingRegions = new ConcurrentDictionary<Thread, BlockingRegion>();
        private static delegate* unmanaged[Cdecl]<void*, void> _interruptThread;
        private static readonly IntPtr RubyUbfIo = new IntPtr(-1);

        private static void InitializeThreads() {
            _interruptThread = (delegate* unmanaged[Cdecl]<void*, void>)ShimExport("rb_ir_interrupt_thread");
            RubyUtils.NativeUnblockHook = Unblock;
        }

        internal static int rb_ir_blocking_begin(void* ubf, void* data2, void* thread) {
            var region = new BlockingRegion { Ubf = (IntPtr)ubf, Data = (IntPtr)data2, NativeThread = (IntPtr)thread };
            region.WasBlocked = RubyUtils.EnterNativeWait();
            _blockingRegions[Thread.CurrentThread] = region;
            return 0;
        }

        internal static void rb_ir_blocking_end(int token) {
            BlockingRegion region;
            if (_blockingRegions.TryRemove(Thread.CurrentThread, out region)) {
                RubyUtils.ExitNativeWait(region.WasBlocked);
            }
        }

        private static void Unblock(Thread/*!*/ thread) {
            BlockingRegion region;
            if (!_blockingRegions.TryGetValue(thread, out region) || region.Ubf == IntPtr.Zero) {
                return;
            }
            if (region.Ubf == RubyUbfIo) {
                _interruptThread((void*)region.NativeThread);
            } else {
                CallVoid(region.Ubf, (void*)region.Data);
            }
        }
    }
}
