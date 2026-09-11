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
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Threading {
    /// <summary>
    /// Thread::ConditionVariable. The old implementation used an AutoResetEvent and a sleep-based
    /// broadcast, which lost wakeups; this is a plain Monitor condition, so #signal wakes exactly one
    /// waiter and #broadcast wakes all of them atomically.
    /// </summary>
    [RubyClass("ConditionVariable")]
    public class RubyConditionVariable {
        private readonly object/*!*/ _lock = new object();
        // Every waiter gets a ticket; signal hands out one permit, broadcast hands out one per waiter.
        private int _waiting;
        private int _permits;

        public RubyConditionVariable() {
        }

        [RubyMethod("signal")]
        public static RubyConditionVariable/*!*/ Signal(RubyConditionVariable/*!*/ self) {
            lock (self._lock) {
                if (self._permits < self._waiting) {
                    self._permits++;
                    Monitor.Pulse(self._lock);
                }
            }
            return self;
        }

        [RubyMethod("broadcast")]
        public static RubyConditionVariable/*!*/ Broadcast(RubyConditionVariable/*!*/ self) {
            lock (self._lock) {
                self._permits = self._waiting;
                Monitor.PulseAll(self._lock);
            }
            return self;
        }

        [RubyMethod("wait")]
        public static RubyConditionVariable/*!*/ Wait(RubyConditionVariable/*!*/ self, [NotNull]RubyMutex/*!*/ mutex) {
            return Wait(self, mutex, null);
        }

        [RubyMethod("wait")]
        public static RubyConditionVariable/*!*/ Wait(RubyConditionVariable/*!*/ self, [NotNull]RubyMutex/*!*/ mutex, object timeout) {
            int ms = RubyQueue.GetTimeoutMilliseconds(timeout);

            lock (self._lock) {
                self._waiting++;
            }

            RubyMutex.Unlock(mutex);
            try {
                lock (self._lock) {
                    long deadline = ms < 0 ? 0 : Environment.TickCount64 + ms;
                    while (self._permits == 0) {
                        bool signalled;
                        try {
                            if (ms < 0) {
                                signalled = Monitor.Wait(self._lock);
                            } else {
                                int remaining = (int)(deadline - Environment.TickCount64);
                                signalled = remaining > 0 && Monitor.Wait(self._lock, remaining);
                            }
                        } catch (ThreadInterruptedException) {
                            RubyUtils.TranslateThreadInterrupt();
                            continue;
                        }
                        if (!signalled) {
                            break;
                        }
                    }
                    if (self._permits > 0) {
                        self._permits--;
                    }
                    self._waiting--;
                }
            } finally {
                RubyMutex.Lock(mutex);
            }
            return self;
        }
    }
}
