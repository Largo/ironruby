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
            // the extension lock let go of for the region, taken again by rb_thread_call_with_gvl
            public ReleasedLock Released;
            // The region this one runs in, on the same thread: rb_thread_call_without_gvl from the
            // function of an rb_thread_call_with_gvl that is itself in a region.
            public BlockingRegion Outer;
            // How many rb_thread_call_with_gvl calls in the region are under way: the outermost
            // takes the lock again and lets go of it, nested ones find it held. Only the region's
            // own thread changes it.
            public volatile int WithGvlDepth;
        }

        // CLR thread -> the innermost blocking region it is in (each links to the one around it)
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
            region.Released = ReleaseForRuby();
            var current = Thread.CurrentThread;
            BlockingRegion outer;
            _blockingRegions.TryGetValue(current, out outer);
            region.Outer = outer;
            _blockingRegions[current] = region;
            return 0;
        }

        // the innermost region ends: the one around it, if any, is the thread's again
        internal static void rb_ir_blocking_end(int token) {
            var thread = Thread.CurrentThread;
            BlockingRegion region;
            if (_blockingRegions.TryGetValue(thread, out region)) {
                if (region.Outer != null) {
                    _blockingRegions[thread] = region.Outer;
                } else {
                    _blockingRegions.TryRemove(thread, out region);
                }
                RubyUtils.ExitNativeWait(region.WasBlocked);
                Reacquire(region.Released);
            }
        }

        // rb_thread_call_with_gvl inside a blocking region: the region's lock again, for its
        // function. Outside one the thread has whatever lock its code runs under already, and
        // CRuby allows a call with the GVL held too.
        internal static void rb_ir_with_gvl_begin() {
            BlockingRegion region;
            if (_blockingRegions.TryGetValue(Thread.CurrentThread, out region) && region.WithGvlDepth++ == 0) {
                Reacquire(region.Released);
            }
        }

        internal static void rb_ir_with_gvl_end() {
            BlockingRegion region;
            if (_blockingRegions.TryGetValue(Thread.CurrentThread, out region) && region.WithGvlDepth > 0 && --region.WithGvlDepth == 0) {
                region.Released = ReleaseForRuby();
            }
        }

        private static void Unblock(Thread/*!*/ thread) {
            BlockingRegion region;
            // a region whose rb_thread_call_with_gvl is running has no unblocking function, as in CRuby
            if (!_blockingRegions.TryGetValue(thread, out region) || region.Ubf == IntPtr.Zero || region.WithGvlDepth > 0) {
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
