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
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Threading {
    /// <summary>
    /// Thread::ConditionVariable. Built on the same per-thread sleep signal as Thread.stop, exactly like
    /// MRI: that is what makes Thread#run and Thread#wakeup end a #wait (a Monitor condition could not
    /// be woken that way), and it gives #signal the FIFO order the specs require. The old implementation
    /// used an AutoResetEvent plus a 1ms sleep per waiter in #broadcast and lost wakeups.
    /// </summary>
    [RubyClass("ConditionVariable")]
    public class RubyConditionVariable {
        private readonly object/*!*/ _lock = new object();
        private readonly LinkedList<IronRuby.Builtins.ThreadOps.RubyThreadInfo>/*!*/ _waiters = new LinkedList<IronRuby.Builtins.ThreadOps.RubyThreadInfo>();

        public RubyConditionVariable() {
        }

        [RubyMethod("signal")]
        public static RubyConditionVariable/*!*/ Signal(RubyConditionVariable/*!*/ self) {
            IronRuby.Builtins.ThreadOps.RubyThreadInfo first = null;
            lock (self._lock) {
                if (self._waiters.Count > 0) {
                    first = self._waiters.First.Value;
                    self._waiters.RemoveFirst();
                }
            }
            if (first != null) {
                first.Wake();
            }
            return self;
        }

        [RubyMethod("broadcast")]
        public static RubyConditionVariable/*!*/ Broadcast(RubyConditionVariable/*!*/ self) {
            IronRuby.Builtins.ThreadOps.RubyThreadInfo[] all;
            lock (self._lock) {
                all = new IronRuby.Builtins.ThreadOps.RubyThreadInfo[self._waiters.Count];
                self._waiters.CopyTo(all, 0);
                self._waiters.Clear();
            }
            foreach (var waiter in all) {
                waiter.Wake();
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

            var info = IronRuby.Builtins.ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
            LinkedListNode<IronRuby.Builtins.ThreadOps.RubyThreadInfo> node;
            lock (self._lock) {
                node = self._waiters.AddLast(info);
            }

            RubyMutex.Unlock(mutex);
            try {
                if (ms < 0) {
                    info.Sleep();
                } else {
                    info.Sleep(ms);
                }
            } finally {
                lock (self._lock) {
                    if (node.List != null) {
                        self._waiters.Remove(node);
                    }
                }
                RubyMutex.Lock(mutex);
            }
            return self;
        }
    }
}
