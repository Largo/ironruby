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
        /// Taking spins for a few microseconds before it blocks. A resume / Fiber.yield round trip
        /// whose other side answers quickly - Enumerator#next over a generator, a fiber that yields
        /// in a loop - then costs two cache-line transfers instead of two kernel wake-ups, which is
        /// most of what a switch used to cost. The spin budget adapts per mailbox: it goes back to
        /// the cap whenever the message arrived shortly after the spin gave up, and halves when it
        /// took long, so a fiber that is resumed rarely soon stops burning a core on it. Single-core
        /// machines never spin.
        ///
        /// A blocked take is a Monitor wait, so Thread#kill / Thread#raise (delivered with
        /// Thread.Interrupt) wake it exactly as they wake Queue#pop, which the mailbox used to be.
        /// </summary>
        public sealed class FiberMailbox {
            // How long a take spins before it blocks: between a step and the cap, adapted per mailbox.
            private static readonly long _maxSpinTicks = Stopwatch.Frequency / 25000;      // 40us
            private static readonly long _minSpinTicks = Stopwatch.Frequency / 1000000;    // 1us
            private static readonly bool _canSpin = Environment.ProcessorCount > 1;

            private readonly object/*!*/ _lock = new object();

            // Pending messages: practically always none or one, so the first one has a slot of its own.
            private object _message;
            private Queue<object> _overflow;
            private volatile int _count;

            // The receiver is parked in Monitor.Wait (guarded by _lock).
            private bool _blocked;

            private long _spinTicks = _maxSpinTicks;

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
                long spinStart = 0;
                if (_count == 0 && _canSpin) {
                    spinStart = Stopwatch.GetTimestamp();
                    Spin(spinStart);
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

                            if (spinStart != 0) {
                                // The spin gave up. If the message came soon after, a longer spin would
                                // have caught it; if it took long, the other side does real work (or
                                // waits for something) between switches, and spinning is wasted.
                                long waited = Stopwatch.GetTimestamp() - spinStart;
                                long budget = _spinTicks;
                                _spinTicks = (waited < _maxSpinTicks) ? _maxSpinTicks : Math.Max(_minSpinTicks, budget / 2);
                                spinStart = 0;
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

            private void Spin(long start) {
                long budget = _spinTicks;
                while (true) {
                    for (int i = 0; i < 4; i++) {
                        Thread.SpinWait(2);
                        if (_count != 0) {
                            return;
                        }
                    }
                    if (Stopwatch.GetTimestamp() - start > budget) {
                        return;
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
