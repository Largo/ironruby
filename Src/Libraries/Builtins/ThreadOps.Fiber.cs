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
#if FEATURE_THREAD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    // The primitives the Ruby-level Fiber (Src/StdLib/ironruby/ruby4.rb) switches with.
    public static partial class ThreadOps {

        /// <summary>
        /// The mailbox a fiber waits on while it is not running. Every fiber is a CLR thread of its
        /// own and exactly one fiber of a group runs at a time, so a switch is "post to the target's
        /// mailbox, then take from my own".
        ///
        /// Taking spins for a while before it blocks. A resume / Fiber.yield round trip whose other
        /// side answers quickly - Enumerator#next over a generator, a fiber that yields in a loop -
        /// then costs two cache-line transfers instead of two kernel wake-ups, which is most of what
        /// a switch used to cost. A wake-up costs tens of microseconds on a busy machine, so the spin
        /// may run about as long before it gives up. Two misses in a row pause spinning for that
        /// mailbox - a fiber that is resumed rarely, or threads that cannot run side by side, would
        /// only burn a core on it - and every 32nd take tries again. The spin yields the CPU now and
        /// then, in case the thread it waits for is queued behind it. Single-core machines never
        /// spin.
        ///
        /// A blocked take is a Monitor wait, so Thread#kill / Thread#raise (delivered with
        /// Thread.Interrupt) wake it exactly as they wake Queue#pop, which the mailbox used to be.
        /// </summary>
        public sealed class FiberMailbox {
            // How long a take spins before it blocks, and how spinning is paused and probed.
            private static readonly long _spinTicks = Stopwatch.Frequency / 10000;          // 100us
            private const int SpinMissesToPause = 2;
            private const int PausedTakesPerProbe = 32;
            private static readonly bool _canSpin = Environment.ProcessorCount > 1;

            private readonly object/*!*/ _lock = new object();

            // Pending messages: practically always none or one, so the first one has a slot of its own.
            private object _message;
            private Queue<object> _overflow;
            private volatile int _count;

            // The receiver is parked in Monitor.Wait (guarded by _lock).
            private bool _blocked;

            // Spin misses in a row; takes since spinning was paused (spinning is paused while
            // _misses has reached SpinMissesToPause).
            private int _misses;
            private int _pausedTakes;

            // The fiber group's owner, and its RubyThreadInfo, cached for FiberTake.
            private Thread _owner;
            private RubyThreadInfo _ownerInfo;

            public void Post(object message) {
                lock (_lock) {
                    int count = _count;
                    if (count == 0) {
                        _message = message;
                    } else {
                        if (_overflow == null) {
                            _overflow = new Queue<object>();
                        }
                        _overflow.Enqueue(message);
                    }
                    _count = count + 1;
                    if (_blocked) {
                        Monitor.Pulse(_lock);
                    }
                }
            }

            public object Take() {
                if (_count == 0 && _canSpin) {
                    if (_misses < SpinMissesToPause || ++_pausedTakes >= PausedTakesPerProbe) {
                        _pausedTakes = 0;
                        if (Spin()) {
                            _misses = 0;
                        } else if (_misses < SpinMissesToPause) {
                            _misses++;
                        }
                    }
                }

                while (true) {
                    bool interrupted = false;
                    lock (_lock) {
                        if (_count == 0) {
                            _blocked = true;
                            try {
                                Monitor.Wait(_lock);
                            } catch (ThreadInterruptedException) {
                                interrupted = true;
                            } finally {
                                _blocked = false;
                            }
                        }

                        if (!interrupted && _count != 0) {
                            object message = _message;
                            int count = _count - 1;
                            _message = (count > 0) ? _overflow.Dequeue() : null;
                            _count = count;
                            return message;
                        }
                    }

                    if (interrupted) {
                        // Thread#kill / Thread#raise nudged us: deliver the parked exception (outside the
                        // lock, it runs Ruby code). If nothing is parked the wait simply goes on.
                        RubyUtils.TranslateThreadInterrupt();
                    }
                }
            }

            private bool Spin() {
                long start = Stopwatch.GetTimestamp();
                int round = 0;
                while (true) {
                    for (int i = 0; i < 4; i++) {
                        Thread.SpinWait(2);
                        if (_count != 0) {
                            return true;
                        }
                    }
                    if ((++round & 7) == 0) {
                        Thread.Yield();
                        if (_count != 0) {
                            return true;
                        }
                    }
                    if (Stopwatch.GetTimestamp() - start > _spinTicks) {
                        return false;
                    }
                }
            }

            internal RubyThreadInfo/*!*/ GetOwnerInfo(Thread/*!*/ owner) {
                RubyThreadInfo info = _ownerInfo;
                if (info == null || !ReferenceEquals(_owner, owner)) {
                    info = RubyThreadInfo.FromThread(owner);
                    _owner = owner;
                    _ownerInfo = info;
                }
                return info;
            }
        }

        // Fiber.current of this CLR thread (a fiber never leaves the thread it runs on), for the
        // context it belongs to. The same thing is kept in the thread's locals as :__ir_fiber_current__
        // for the C# code that looks there; this copy is what makes Fiber.current cheap.
        [ThreadStatic]
        private static object _currentFiber;
        [ThreadStatic]
        private static RubyContext _currentFiberContext;

        [RubyMethod("__fiber_mailbox__", RubyMethodAttributes.PublicSingleton)]
        public static FiberMailbox/*!*/ CreateFiberMailbox(object self) {
            return new FiberMailbox();
        }

        [RubyMethod("__fiber_current__", RubyMethodAttributes.PublicSingleton)]
        public static object GetCurrentFiber(RubyContext/*!*/ context, object self) {
            return ReferenceEquals(_currentFiberContext, context) ? _currentFiber : null;
        }

        [RubyMethod("__fiber_current_set__", RubyMethodAttributes.PublicSingleton)]
        public static object SetCurrentFiber(RubyContext/*!*/ context, object self, object fiber) {
            RubyThreadInfo.FromThread(Thread.CurrentThread)[context.CreateAsciiSymbol("__ir_fiber_current__")] = fiber;
            _currentFiber = fiber;
            _currentFiberContext = context;
            return fiber;
        }

        [RubyMethod("__fiber_post__", RubyMethodAttributes.PublicSingleton)]
        public static object FiberPost(object self, [NotNull]FiberMailbox/*!*/ mailbox, object message) {
            mailbox.Post(message);
            return null;
        }

        /// <summary>
        /// Waits for the next message to the fiber running on this thread, then makes this thread
        /// the one that runs on behalf of <paramref name="owner"/> (what __set_fiber_owner__ does).
        /// </summary>
        [RubyMethod("__fiber_take__", RubyMethodAttributes.PublicSingleton)]
        public static object FiberTake(object self, [NotNull]FiberMailbox/*!*/ mailbox, [NotNull]Thread/*!*/ owner) {
            object message = mailbox.Take();
            RubyUtils.SetFiberOwnerThread(owner);
            mailbox.GetOwnerInfo(owner).ActiveFiberThread = Thread.CurrentThread;
            return message;
        }
    }
}
#endif
