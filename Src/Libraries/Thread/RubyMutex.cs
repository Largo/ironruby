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
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Threading {
    /// <summary>
    /// Ruby's Mutex is not a CLR monitor: it is not recursive (a second lock from the owner is a
    /// ThreadError, not a re-entry), it may only be unlocked by its owner, and ownership belongs to a
    /// *Ruby thread* - which for a fiber means the thread that owns the fiber group, because IronRuby
    /// gives every fiber its own CLR thread.
    /// </summary>
    [RubyClass("Mutex", DefineIn = typeof(ThreadOps))]
    public class RubyMutex {

        /// <summary>MRI's #&lt;Thread::Mutex:0x...&gt;, rather than the CLR type name.</summary>
        [RubyMethod("inspect")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyMutex/*!*/ self) {
            return RubyUtils.ObjectToMutableString(context, self);
        }
        private readonly object/*!*/ _syncRoot = new object();

#pragma warning disable 420 // volatile fields passed by ref to Interlocked
        // The Ruby thread that holds the lock (the fiber group's owner) ... Taking a free lock is one
        // compare-and-swap of this field from null; only a thread that has to wait for it uses _syncRoot.
        private volatile Thread _owner;
        // ... and the CLR thread that actually took it. Ruby reports #owned? per *fiber* even though
        // deadlock detection is per thread, and a fiber is a CLR thread here. Only the holder sets it to
        // itself, so the holder can test it without a lock.
        private volatile Thread _ownerFiber;
        // Threads waiting in LockContended; changed under _syncRoot, read by Release without it.
        private int _waiters;

        // Every locked mutex is registered so that a thread's locks can be released when it dies,
        // which is what MRI does. A lock taken by a thread for itself goes on that thread's own list,
        // which only that thread touches; one taken inside a fiber - whose owning thread may die first,
        // or be the one that releases it - goes into the shared set.
        [ThreadStatic]
        private static List<RubyMutex> _heldByThisThread;
        [ThreadStatic]
        private static int _heldPruneAt;
        private static readonly HashSet<RubyMutex>/*!*/ _sharedLocked = new HashSet<RubyMutex>(ReferenceEqualityComparer<RubyMutex>.Instance);
        private bool _inSharedSet;

        public RubyMutex() {
        }

        private static Thread/*!*/ CurrentOwner {
            get { return RubyUtils.CurrentRubyThread; }
        }

        /// <summary>Whether the calling fiber (CLR thread) holds the lock. Lock-free: see _ownerFiber.</summary>
        internal bool IsHeldByCurrentFiber {
            get { return ReferenceEquals(_ownerFiber, Thread.CurrentThread); }
        }

        internal bool IsLockedInternal {
            get { return _owner != null; }
        }

        [RubyMethod("locked?")]
        public static bool IsLocked(RubyMutex/*!*/ self) {
            return self._owner != null;
        }

        [RubyMethod("owned?")]
        public static bool IsOwned(RubyMutex/*!*/ self) {
            return self.IsHeldByCurrentFiber;
        }

        [RubyMethod("try_lock")]
        public static bool TryLock(RubyMutex/*!*/ self) {
            Thread me = CurrentOwner;
            if (Interlocked.CompareExchange(ref self._owner, me, null) != null) {
                return false;
            }
            self.Acquired(me);
            return true;
        }

        [RubyMethod("lock")]
        public static RubyMutex/*!*/ Lock(RubyMutex/*!*/ self) {
            DoLock(self);
            return self;
        }

        private static void DoLock(RubyMutex/*!*/ self) {
            DoLock(self, true);
        }

        /// <summary>
        /// <paramref name="interruptible"/> false keeps a parked Thread#kill / Thread#raise parked
        /// until the lock has been taken. ConditionVariable#wait needs that: MRI guarantees the
        /// mutex is held again when #wait returns, even when the thread was killed while it was
        /// waiting for it, and code in an ensure clause relies on still owning it.
        /// </summary>
        internal static void DoLock(RubyMutex/*!*/ self, bool interruptible) {
            if (interruptible) {
                RubyUtils.CheckAsyncException();
            }
            Thread me = CurrentOwner;
            if (Interlocked.CompareExchange(ref self._owner, me, null) == null) {
                self.Acquired(me);
                return;
            }
            self.LockContended(me, interruptible);
        }

        private void LockContended(Thread/*!*/ me, bool interruptible) {
            // Not `lock`: Thread.Interrupt breaks a contended Monitor.Enter off, and the bare
            // ThreadInterruptedException reached Ruby code instead of the Thread#raise it came for.
            using (new RubyUtils.UninterruptedLock(_syncRoot)) {
                if (_owner == me) {
                    // A fiber is a CLR thread here. If an asynchronous Thread#raise terminated
                    // the fiber in the narrow hand-off around #synchronize, reclaim its lock;
                    // a suspended (and therefore still alive) sibling fiber must still deadlock.
                    Thread fiber = _ownerFiber;
                    if (fiber != null && !fiber.IsAlive) {
                        Release();
                    } else {
                        throw new ThreadError("deadlock; recursive locking");
                    }
                }
            }

            // A Thread#kill or Thread#raise that comes before the wait has blocked is not lost
            // (RubyUtils.BeginNudgeableWait): NudgeThread interrupts only a thread that is already
            // in WaitSleepJoin, so one that came while this thread was on its way into Monitor.Wait -
            // or between a PulseAll and the next Monitor.Wait, having lost the race for the lock -
            // found nothing to wake, and the thread slept on with the exception parked: a
            // Timeout.timeout around a lock that is never released hung. What is parked is looked
            // at before every Monitor.Wait, and a nudge from then on pulses _syncRoot.
            RubyUtils.BeginNudgeableWait(_syncRoot);
            // Counted before each attempt, and with a full fence: a Release that clears _owner
            // after an attempt failed is then sure to see us waiting and pulse.
            Interlocked.Increment(ref _waiters);
            try {
                while (true) {
                    bool interrupted = false;
                    using (new RubyUtils.UninterruptedLock(_syncRoot)) {
                        if (Interlocked.CompareExchange(ref _owner, me, null) == null) {
                            Acquired(me);
                            return;
                        }
                        if (!RubyUtils.PrepareNudgeableWait()) {
                            interrupted = true;
                        } else {
                            string previous = IronRuby.Builtins.ThreadOps.EnterBlockingMethod("Thread::Mutex#lock");
                            try {
                                using (RubyUtils.BlockingWait.Enter()) {
                                    Monitor.Wait(_syncRoot);
                                }
                            } catch (ThreadInterruptedException) {
                                interrupted = true;
                            } finally {
                                IronRuby.Builtins.ThreadOps.ExitBlockingMethod(previous);
                                RubyUtils.NudgeableWaitDone();
                            }
                        }
                    }

                    // Thread#kill / Thread#raise nudged us: deliver what is parked outside _syncRoot,
                    // since a safe point runs Ruby code (trap handlers, finalizers) that could wait for
                    // the holder or another contender. If nothing is parked (or it is masked, or went
                    // to the running fiber) the wait goes on. A ConditionVariable re-lock keeps it
                    // parked until the lock is taken.
                    if (interrupted && interruptible) {
                        RubyUtils.TranslateThreadInterrupt();
                    }
                }
            } finally {
                Interlocked.Decrement(ref _waiters);
                RubyUtils.EndNudgeableWait();
            }
        }

        // Called by the thread that has just set _owner to <paramref name="owner"/>.
        private void Acquired(Thread/*!*/ owner) {
            Thread current = Thread.CurrentThread;
            _ownerFiber = current;
            if (ReferenceEquals(owner, current)) {
                List<RubyMutex> held = _heldByThisThread;
                if (held == null) {
                    _heldByThisThread = held = new List<RubyMutex>();
                    _heldPruneAt = 16;
                } else if (held.Count >= _heldPruneAt) {
                    PruneHeld(held, current);
                }
                held.Add(this);
            } else {
                _inSharedSet = true;
                using (new RubyUtils.UninterruptedLock(_sharedLocked)) {
                    _sharedLocked.Add(this);
                }
            }
        }

        // Drops the entries of locks this thread no longer holds: a lock is normally taken off the list
        // when it is released, but not when another fiber of the same thread released it.
        private static void PruneHeld(List<RubyMutex>/*!*/ held, Thread/*!*/ current) {
            held.RemoveAll(m => !ReferenceEquals(m._ownerFiber, current));
            _heldPruneAt = Math.Max(16, held.Count * 2);
        }

        // Called by the holder (or, for a dead holder, on its behalf) with the lock held. Every
        // monitor taken here and in Acquired and ReleaseLocksOf is held for moments only, and is an
        // UninterruptedLock: a Thread#raise's nudge that found the thread waiting for one broke the
        // wait off - here before _owner was cleared, which left the mutex locked for good, or before
        // the waiters were pulsed, which slept on; and in the `finally` that ends a Ruby thread
        // (ReleaseLocksOf) the ThreadInterruptedException is unhandled and ends the process.
        // Whatever the nudge came for stays parked for the next safe point.
        private void Release() {
            if (_inSharedSet) {
                _inSharedSet = false;
                using (new RubyUtils.UninterruptedLock(_sharedLocked)) {
                    _sharedLocked.Remove(this);
                }
            } else if (ReferenceEquals(_ownerFiber, Thread.CurrentThread)) {
                List<RubyMutex> held = _heldByThisThread;
                if (held != null) {
                    // locks are mostly released in the reverse order they were taken
                    for (int i = held.Count - 1; i >= 0; i--) {
                        if (ReferenceEquals(held[i], this)) {
                            held.RemoveAt(i);
                            break;
                        }
                    }
                }
            }
            _ownerFiber = null;
            Interlocked.Exchange(ref _owner, null);
            if (Volatile.Read(ref _waiters) != 0) {
                using (new RubyUtils.UninterruptedLock(_syncRoot)) {
                    Monitor.PulseAll(_syncRoot);
                }
            }
        }

        /// <summary>
        /// Called when a Ruby thread terminates: MRI releases every mutex the thread still holds.
        /// Must be called on <paramref name="thread"/> itself.
        /// </summary>
        public static void ReleaseLocksOf(Thread/*!*/ thread) {
            if (ReferenceEquals(thread, Thread.CurrentThread)) {
                List<RubyMutex> held = _heldByThisThread;
                if (held != null && held.Count > 0) {
                    foreach (RubyMutex m in held.ToArray()) {
                        if (m._owner == thread || m._ownerFiber == thread) {
                            m.Release();
                        }
                    }
                    held.Clear();
                }
            }

            RubyMutex[] shared;
            using (new RubyUtils.UninterruptedLock(_sharedLocked)) {
                if (_sharedLocked.Count == 0) {
                    return;
                }
                shared = new RubyMutex[_sharedLocked.Count];
                _sharedLocked.CopyTo(shared);
            }
            foreach (RubyMutex m in shared) {
                if (m._owner == thread || m._ownerFiber == thread) {
                    m.Release();
                }
            }
        }
#pragma warning restore 420

        /// <summary>Releases the lock if the current Ruby thread holds it; false if it does not.</summary>
        private static bool DoUnlock(RubyMutex/*!*/ self) {
            if (self._owner != CurrentOwner) {
                return false;
            }
            self.Release();
            return true;
        }

        [RubyMethod("unlock")]
        public static RubyMutex/*!*/ Unlock(RubyMutex/*!*/ self) {
            Thread owner = self._owner;
            if (owner == null) {
                throw new ThreadError("Attempt to unlock a mutex which is not locked");
            }
            if (owner != CurrentOwner) {
                throw new ThreadError("Attempt to unlock a mutex which is locked by another thread");
            }
            self.Release();
            return self;
        }

        [RubyMethod("synchronize")]
        public static object Synchronize(BlockParam criticalSection, RubyMutex/*!*/ self) {
            if (criticalSection == null) {
                throw new ThreadError("must be called with a block");
            }
            bool ownedBefore = IsOwned(self);
            try {
                DoLock(self);
                object result;
                criticalSection.Yield(out result);
                return result;
            } finally {
                // An asynchronous Thread#raise can arrive just as DoLock completes. Release a
                // lock acquired by this call even if control never reached the block. Do not,
                // however, unlock a recursive lock that was already owned on entry.
                if (!ownedBefore && IsOwned(self)) {
                    DoUnlock(self);
                }
            }
        }

        // MRI: the seconds slept, rounded, when woken; nil when the timeout ran out.
        [RubyMethod("sleep")]
        public static object Sleep(RubyMutex/*!*/ self) {
            return DoSleep(self, Double.NegativeInfinity);
        }

        [RubyMethod("sleep")]
        public static object Sleep(RespondToStorage/*!*/ respondToStorage, BinaryOpStorage/*!*/ divmodStorage,
            BinaryOpStorage/*!*/ multiplyStorage, ConversionStorage<IList>/*!*/ arrayTryCast, ConversionStorage<IntegerValue>/*!*/ integerCast,
            RubyMutex/*!*/ self, object timeout) {
            return DoSleep(self, ToSleepSeconds(respondToStorage, divmodStorage, multiplyStorage, arrayTryCast, integerCast, timeout));
        }

        /// <summary>
        /// The timeout of #sleep in seconds, Double.NegativeInfinity for nil (no timeout). Also used by
        /// ConditionVariable#wait, which in MRI is mutex.sleep(timeout) and so converts the same way.
        /// </summary>
        internal static double ToSleepSeconds(RespondToStorage/*!*/ respondToStorage, BinaryOpStorage/*!*/ divmodStorage,
            BinaryOpStorage/*!*/ multiplyStorage, ConversionStorage<IList>/*!*/ arrayTryCast, ConversionStorage<IntegerValue>/*!*/ integerCast,
            object timeout) {

            double seconds;
            if (timeout == null) {
                return Double.NegativeInfinity;
            } else if (timeout is int) {
                seconds = (int)timeout;
            } else if (timeout is long) {
                seconds = (long)timeout;
            } else if (timeout is double) {
                seconds = (double)timeout;
            } else if (timeout is BigInteger) {
                seconds = ToWholeSeconds(integerCast, timeout);
            } else {
                seconds = ToSecondsByDivmod(respondToStorage, divmodStorage, multiplyStorage, arrayTryCast, integerCast, timeout);
            }
            if (Double.IsNaN(seconds)) {
                throw RubyExceptions.CreateRangeError("NaN out of Time range");
            }
            if (Double.IsPositiveInfinity(seconds)) {
                throw RubyExceptions.CreateRangeError("Inf out of Time range");
            }
            if (seconds < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }
            return seconds;
        }

        /// <summary>
        /// MRI's interval of any other object - a Rational, a BigDecimal, a Numeric of the program's own:
        /// <c>whole, fraction = timeout.divmod(1)</c>, then <c>fraction * 1_000_000_000</c> nanoseconds.
        /// Something without a #divmod that answers an Array is not an interval at all.
        /// </summary>
        private static double ToSecondsByDivmod(RespondToStorage/*!*/ respondToStorage, BinaryOpStorage/*!*/ divmodStorage,
            BinaryOpStorage/*!*/ multiplyStorage, ConversionStorage<IList>/*!*/ arrayTryCast, ConversionStorage<IntegerValue>/*!*/ integerCast,
            object timeout) {

            IList parts = null;
            if (Protocols.RespondTo(respondToStorage, timeout, "divmod")) {
                var divmod = divmodStorage.GetCallSite("divmod");
                parts = Protocols.TryCastToArray(arrayTryCast, divmod.Target(divmod, timeout, ScriptingRuntimeHelpers.Int32ToObject(1)));
            }
            if (parts == null) {
                throw RubyExceptions.CreateTypeError("can't convert {0} into time interval",
                    respondToStorage.Context.GetClassDisplayName(timeout));
            }

            double whole = ToWholeSeconds(integerCast, parts.Count > 0 ? parts[0] : null);
            if (whole < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }
            var multiply = multiplyStorage.GetCallSite("*");
            double nanoseconds = ToWholeSeconds(integerCast,
                multiply.Target(multiply, parts.Count > 1 ? parts[1] : null, ScriptingRuntimeHelpers.Int32ToObject(1000000000)));
            return whole + nanoseconds / 1e9;
        }

        /// <summary>MRI's NUM2TIMET/NUM2LONG: a Float truncated, anything else through #to_int, into a long.</summary>
        private static double ToWholeSeconds(ConversionStorage<IntegerValue>/*!*/ integerCast, object value) {
            if (value is int) {
                return (int)value;
            } else if (value is long) {
                return (long)value;
            } else if (value is double) {
                return Math.Truncate((double)value);
            }
            IntegerValue integer = value is BigInteger ? new IntegerValue((BigInteger)value) : Protocols.CastToInteger(integerCast, value);
            long result;
            if (integer.IsFixnum) {
                return integer.Fixnum;
            } else if (integer.Bignum.AsInt64(out result)) {
                return result;
            }
            throw RubyExceptions.CreateRangeError("bignum too big to convert into 'long'");
        }

        private static object DoSleep(RubyMutex/*!*/ self, double seconds) {
            if (self._owner != CurrentOwner) {
                throw new ThreadError("Attempt to unlock a mutex which is not locked");
            }

            Unlock(self);
            try {
                var info = IronRuby.Builtins.ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (Double.IsNegativeInfinity(seconds)) {
                    info.Sleep();
                } else {
                    double ms = seconds * 1000;
                    if (!info.Sleep(ms > Int32.MaxValue ? Timeout.Infinite : (int)ms)) {
                        return null;
                    }
                }
                return (int)Math.Round(watch.Elapsed.TotalSeconds);
            } finally {
                DoLock(self);
            }
        }
    }
}
