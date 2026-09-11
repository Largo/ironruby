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
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.StandardLibrary.Threading {
    /// <summary>
    /// Ruby's Mutex is not a CLR monitor: it is not recursive (a second lock from the owner is a
    /// ThreadError, not a re-entry), it may only be unlocked by its owner, and ownership belongs to a
    /// *Ruby thread* - which for a fiber means the thread that owns the fiber group, because IronRuby
    /// gives every fiber its own CLR thread.
    /// </summary>
    [RubyClass("Mutex")]
    public class RubyMutex {
        private readonly object/*!*/ _syncRoot = new object();
        private Thread _owner;

        public RubyMutex() {
        }

        internal object Mutex { get { return _syncRoot; } }

        private static Thread/*!*/ CurrentOwner {
            get { return RubyUtils.CurrentRubyThread; }
        }

        [RubyMethod("locked?")]
        public static bool IsLocked(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                return self._owner != null;
            }
        }

        [RubyMethod("owned?")]
        public static bool IsOwned(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                return self._owner == CurrentOwner;
            }
        }

        [RubyMethod("try_lock")]
        public static bool TryLock(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                if (self._owner != null) {
                    return false;
                }
                self._owner = CurrentOwner;
                return true;
            }
        }

        [RubyMethod("lock")]
        public static RubyMutex/*!*/ Lock(RubyMutex/*!*/ self) {
            DoLock(self);
            return self;
        }

        private static void DoLock(RubyMutex/*!*/ self) {
            Thread me = CurrentOwner;
            lock (self._syncRoot) {
                if (self._owner == me) {
                    throw new ThreadError("deadlock; recursive locking");
                }
                while (self._owner != null) {
                    try {
                        Monitor.Wait(self._syncRoot);
                    } catch (ThreadInterruptedException) {
                        // Thread#kill / Thread#raise nudged us; if nothing is parked the wait restarts.
                        RubyUtils.TranslateThreadInterrupt();
                    }
                }
                self._owner = me;
            }
        }

        /// <summary>Releases the lock if the current Ruby thread holds it; false if it does not.</summary>
        private static bool DoUnlock(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                if (self._owner != CurrentOwner) {
                    return false;
                }
                self._owner = null;
                Monitor.PulseAll(self._syncRoot);
                return true;
            }
        }

        [RubyMethod("unlock")]
        public static RubyMutex/*!*/ Unlock(RubyMutex/*!*/ self) {
            lock (self._syncRoot) {
                if (self._owner == null) {
                    throw new ThreadError("Attempt to unlock a mutex which is not locked");
                }
                if (self._owner != CurrentOwner) {
                    throw new ThreadError("Attempt to unlock a mutex which is locked by another thread");
                }
                self._owner = null;
                Monitor.PulseAll(self._syncRoot);
            }
            return self;
        }

        [RubyMethod("synchronize")]
        public static object Synchronize(BlockParam criticalSection, RubyMutex/*!*/ self) {
            if (criticalSection == null) {
                throw new ThreadError("must be called with a block");
            }
            DoLock(self);
            try {
                object result;
                criticalSection.Yield(out result);
                return result;
            } finally {
                // the block is allowed to unlock the mutex itself
                DoUnlock(self);
            }
        }

        [RubyMethod("sleep")]
        public static int Sleep(RubyMutex/*!*/ self) {
            return DoSleep(self, Double.NegativeInfinity);
        }

        [RubyMethod("sleep")]
        public static int Sleep(RubyMutex/*!*/ self, object timeout) {
            double seconds;
            if (timeout == null) {
                seconds = Double.NegativeInfinity;
            } else if (timeout is int) {
                seconds = (int)timeout;
            } else if (timeout is double) {
                seconds = (double)timeout;
            } else {
                throw RubyExceptions.CreateTypeError("can't convert {0} into time interval", timeout.GetType().Name);
            }
            return DoSleep(self, seconds);
        }

        private static int DoSleep(RubyMutex/*!*/ self, double seconds) {
            if (seconds < 0 && !Double.IsNegativeInfinity(seconds)) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }
            lock (self._syncRoot) {
                if (self._owner != CurrentOwner) {
                    throw new ThreadError("Attempt to unlock a mutex which is not locked");
                }
            }

            Unlock(self);
            try {
                if (Double.IsNegativeInfinity(seconds)) {
                    return IronRuby.Builtins.ThreadOps.SleepForLibrary(Timeout.Infinite);
                }
                double ms = seconds * 1000;
                return IronRuby.Builtins.ThreadOps.SleepForLibrary(ms > Int32.MaxValue ? Timeout.Infinite : (int)ms);
            } finally {
                DoLock(self);
            }
        }

        [RubyMethod("exclusive_unlock")]
        public static object ExclusiveUnlock(BlockParam criticalSection, RubyMutex/*!*/ self) {
            Unlock(self);
            try {
                object result;
                criticalSection.Yield(out result);
                return result;
            } finally {
                DoLock(self);
            }
        }
    }
}
