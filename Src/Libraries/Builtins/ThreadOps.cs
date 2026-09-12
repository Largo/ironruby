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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    using Debug = System.Diagnostics.Debug;

    /// <summary>
    /// Ruby threads are represented by CLR thread objects (System.Threading.Thread).
    /// Ruby 1.8.N has green threads where the language does the thread scheduling. We map the green threads 
    /// directly to CLR threads.
    /// 
    /// Ruby supports asynchronously manipulating of an arbitrary thread with methods like Thread#raise, Thread#exit, etc.
    /// For such methods, we use Thread.Abort which is unsafe. Howevever, Ruby 1.9 may not support green threads,
    /// and this will not be an issue then.
    /// </summary>
    [RubyClass("Thread", Extends = typeof(Thread), Inherits = typeof(object), BuildConfig = "FEATURE_THREAD")]
    public static class ThreadOps {
        static bool _globalAbortOnException;
        static bool _globalReportOnException = true;

        /// <summary>
        /// The ThreadState enumeration is a flag, and multiple values could be set simultaneously. Also,
        /// there is other state that IronRuby tracks. RubyThreadStatus flattens out the different states
        /// into non-overlapping values.
        /// </summary>
        private enum RubyThreadStatus {
            /// <summary>
            /// Ruby does not expose such a state. However, since IronRuby uses CLR threads, this state can exist for
            /// threads that are not created directly from Ruby code
            /// </summary>
            Unstarted,

            Running,

            Sleeping,

            Completed,

            /// <summary>
            /// If Thread#kill has been called, and the thread is not sleeping
            /// </summary>
            Aborting,

            /// <summary>
            /// An unhandled exception was thrown by the thread
            /// </summary>
            Aborted
        }

        public class RubyThreadInfo {
            private static readonly Dictionary<int, RubyThreadInfo> _mapping = new Dictionary<int, RubyThreadInfo>();
            private readonly Dictionary<RubySymbol, object> _threadLocalStorage;
            private ThreadGroup _group;
            private readonly Thread _thread;
            private bool _blocked;
            private bool _abortOnException;
            private AutoResetEvent _runSignal = new AutoResetEvent(false);
            private bool _isSleeping;

            private RubyThreadInfo(Thread thread) {
                _threadLocalStorage = new Dictionary<RubySymbol, object>();
                _group = ThreadGroup.Default;
                _thread = thread;
            }

            internal static RubyThreadInfo FromThread(Thread t) {
                RubyThreadInfo result;
                lock (_mapping) {
                    int key = t.ManagedThreadId;
                    if (!_mapping.TryGetValue(key, out result)) {
                        result = new RubyThreadInfo(t);
                        _mapping[key] = result;
                    }
                }
                return result;
            }

            internal static void RegisterThread(Thread t) {
                FromThread(t);
            }

            internal object this[RubySymbol/*!*/ key] {
                get {
                    lock (_threadLocalStorage) {
                        object result;
                        if (!_threadLocalStorage.TryGetValue(key, out result)) {
                            result = null;
                        }
                        return result;
                    }
                }
                set {
                    lock (_threadLocalStorage) {
                        if (value == null) {
                            _threadLocalStorage.Remove(key);
                        } else {
                            _threadLocalStorage[key] = value;
                        }
                    }
                }
            }

            internal bool HasKey(RubySymbol/*!*/ key) {
                lock (_threadLocalStorage) {
                    return _threadLocalStorage.ContainsKey(key);
                }
            }

            internal RubyArray GetKeys() {
                lock (_threadLocalStorage) {
                    RubyArray result = new RubyArray(_threadLocalStorage.Count);
                    foreach (RubySymbol key in _threadLocalStorage.Keys) {
                        result.Add(key);
                    }
                    return result;
                }
            }

            internal ThreadGroup Group {
                get {
                    return _group;
                }
                set {
                    Interlocked.Exchange(ref _group, value);
                }
            }

            internal Thread Thread {
                get {
                    return _thread;
                }
            }

            internal Exception Exception { get; set; }
            internal object Result { get; set; }
            internal bool CreatedFromRuby { get; set; }
            internal bool ExitRequested { get; set; }
            internal MutableString Name { get; set; }
            internal bool ReportOnException { get; set; }

            // Thread#[] is fiber-local in MRI (and, since every fiber is its own CLR thread here, the
            // dictionary above already is); Thread#thread_variable_get is thread-local, so it needs
            // storage of its own.
            private readonly Dictionary<RubySymbol, object> _threadVariables = new Dictionary<RubySymbol, object>();

            internal object GetThreadVariable(RubySymbol/*!*/ key) {
                lock (_threadVariables) {
                    object result;
                    return _threadVariables.TryGetValue(key, out result) ? result : null;
                }
            }

            internal void SetThreadVariable(RubySymbol/*!*/ key, object value) {
                lock (_threadVariables) {
                    if (value == null) {
                        _threadVariables.Remove(key);
                    } else {
                        _threadVariables[key] = value;
                    }
                }
            }

            internal bool HasThreadVariable(RubySymbol/*!*/ key) {
                lock (_threadVariables) {
                    return _threadVariables.ContainsKey(key);
                }
            }

            internal RubyArray/*!*/ GetThreadVariableKeys() {
                lock (_threadVariables) {
                    RubyArray result = new RubyArray(_threadVariables.Count);
                    foreach (RubySymbol key in _threadVariables.Keys) {
                        result.Add(key);
                    }
                    return result;
                }
            }
            
            internal bool Blocked {
                get {
                    return _blocked;
                }
                set {
                    System.Diagnostics.Debug.Assert(Thread.CurrentThread == _thread);
                    _blocked = value;
                }
            }

            internal bool AbortOnException {
                get {
                    return _abortOnException;
                }
                set {
                    _abortOnException = value;
                }
            }

            internal static RubyThreadInfo[] Threads {
                get {
                    lock (_mapping) {
                        List<RubyThreadInfo> result = new List<RubyThreadInfo>(_mapping.Count);
                        foreach (KeyValuePair<int, RubyThreadInfo> entry in _mapping) {
                            if (entry.Value.Thread.IsAlive) {
                                result.Add(entry.Value);
                            }
                        }
                        return result.ToArray();
                    }
                }
            }

            /// <summary>
            /// We do not use Thread.Sleep here as another thread can call Thread#wakeup/Thread#run. Instead, we use our own
            /// lock which can be signalled from another thread.
            /// </summary>
            internal void Sleep() {
                try {
                    _isSleeping = true;
                    try {
                        _runSignal.WaitOne();
                    } catch (ThreadInterruptedException) {
                        // Thread#kill / Thread#raise nudged us: deliver the parked exception. If there is none
                        // the interrupt was spurious and the sleep simply ends (MRI's sleep is also allowed to
                        // return early).
                        RubyUtils.TranslateThreadInterrupt();
                    }
                } finally {
                    _isSleeping = false;
                }
            }

            /// <summary>
            /// Same as Sleep() but with a timeout; returns true if the full timeout elapsed.
            /// </summary>
            internal bool Sleep(int milliseconds) {
                try {
                    _isSleeping = true;
                    try {
                        return _runSignal.WaitOne(milliseconds);
                    } catch (ThreadInterruptedException) {
                        RubyUtils.TranslateThreadInterrupt();
                        return false;
                    }
                } finally {
                    _isSleeping = false;
                }
            }

            internal void Run() {
                if (_isSleeping) {
                    _runSignal.Set();
                }
            }

            /// <summary>
            /// Like Run(), but signals even if the thread has not reached its wait yet. ConditionVariable
            /// registers a waiter before it releases the mutex, so a #signal can arrive first.
            /// </summary>
            internal void Wake() {
                _runSignal.Set();
            }
        }

        //  declared private instance methods:
        //    initialize
        //  declared protected instance methods:
        //  declared public instance methods:

        private static Exception MakeKeyTypeException(RubyContext/*!*/ context, object key) {
            return RubyExceptions.CreateTypeError("{0} is not a symbol nor a string", context.Inspect(key).ToString());
        }

        /// <summary>
        /// Thread-local keys are Symbols; a String is interned and anything else is given a chance to
        /// convert itself with #to_str (but never #to_sym, which MRI is explicit about).
        /// </summary>
        private static RubySymbol/*!*/ ToKey(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, object key) {
            RubySymbol symbol = key as RubySymbol;
            if (symbol != null) {
                return symbol;
            }

            MutableString str = key as MutableString;
            if (str == null && key != null) {
                str = Protocols.TryCastToString(toStr, key);
            }

            if (str == null) {
                throw MakeKeyTypeException(context, key);
            }

            return context.CreateSymbol(str);
        }

        [RubyMethod("[]")]
        public static object GetElement(Thread/*!*/ self, [NotNull]RubySymbol/*!*/ key) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            return info[key];
        }

        [RubyMethod("[]")]
        public static object GetElement(RubyContext/*!*/ context, Thread/*!*/ self, [NotNull]MutableString/*!*/ key) {
            return GetElement(self, context.CreateSymbol(key));
        }

        [RubyMethod("[]")]
        public static object GetElement(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key) {
            return GetElement(self, ToKey(toStr, context, key));
        }

        [RubyMethod("[]=")]
        public static object SetElement(Thread/*!*/ self, [NotNull]RubySymbol/*!*/ key, object value) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            info[key] = value;
            return value;
        }

        [RubyMethod("[]=")]
        public static object SetElement(RubyContext/*!*/ context, Thread/*!*/ self, [NotNull]MutableString/*!*/ key, object value) {
            return SetElement(self, context.CreateSymbol(key), value);
        }

        [RubyMethod("[]=")]
        public static object SetElement(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key, object value) {
            return SetElement(self, ToKey(toStr, context, key), value);
        }

        [RubyMethod("abort_on_exception")]
        public static object AbortOnException(Thread/*!*/ self) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            return info.AbortOnException;
        }

        [RubyMethod("abort_on_exception=")]
        public static object AbortOnException(Thread/*!*/ self, bool value) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            info.AbortOnException = value;
            return value;
        }

        [RubyMethod("alive?")]
        public static bool IsAlive(Thread/*!*/ self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            return self.IsAlive;
        }

        [RubyMethod("group")]
        public static ThreadGroup Group(Thread/*!*/ self) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            return info.Group;
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, Thread/*!*/ self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);

            MutableString result = MutableString.CreateMutable(context.GetIdentifierEncoding());
            result.Append("#<");
            result.Append(context.GetClassDisplayName(self));
            result.Append(':');
            RubyUtils.AppendFormatHexObjectId(result, RubyUtils.GetObjectId(context, self));

            MutableString name = RubyThreadInfo.FromThread(self).Name;
            if (name != null) {
                result.Append('@');
                result.Append(name);
            }

            result.Append(' ');

            RubyThreadStatus status = GetStatus(self);
            switch (status) {
                case RubyThreadStatus.Unstarted:
                    result.Append("unstarted");
                    break;
                case RubyThreadStatus.Running:
                    result.Append("run");
                    break;
                case RubyThreadStatus.Sleeping:
                    result.Append("sleep");
                    break;
                case RubyThreadStatus.Aborting:
                    result.Append("aborting");
                    break;
                case RubyThreadStatus.Completed:
                case RubyThreadStatus.Aborted:
                    result.Append("dead");
                    break;
            }

            result.Append('>');
            return result;
        }

        [RubyMethod("join")]
        public static Thread/*!*/ Join(Thread/*!*/ self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);

            while (true) {
                try {
                    self.Join();
                    break;
                } catch (ThreadInterruptedException) {
                    RubyUtils.TranslateThreadInterrupt();
                }
            }

            Exception threadException = RubyThreadInfo.FromThread(self).Exception;
            if (threadException != null) {
                throw threadException;
            }

            return self;
        }

        [RubyMethod("join")]
        public static Thread/*!*/ Join(Thread/*!*/ self, double seconds) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);

            if (!(self.ThreadState == ThreadState.AbortRequested || self.ThreadState == ThreadState.Aborted)) {
                double ms = seconds * 1000;
                int timeout = (ms < Int32.MinValue || ms > Int32.MaxValue) ? Timeout.Infinite : (int)ms;
                bool joined;
                while (true) {
                    try {
                        joined = self.Join(timeout);
                        break;
                    } catch (ThreadInterruptedException) {
                        RubyUtils.TranslateThreadInterrupt();
                    }
                }
                if (!joined) {
                    return null;
                }
            }

            Exception threadException = RubyThreadInfo.FromThread(self).Exception;
            if (threadException != null) {
                throw threadException;
            }

            return self;
        }

        [RubyMethod("kill")]
        [RubyMethod("exit")]
        [RubyMethod("terminate")]
        public static Thread Kill(Thread/*!*/ self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);

            if (!self.IsAlive) {
                return self;
            }

            if (GetStatus(self) == RubyThreadStatus.Sleeping && info.ExitRequested) {
                // Thread must be sleeping in an ensure clause. Wake up the thread and allow ensure clause to complete
                info.Run();
                return self;
            }

            info.ExitRequested = true;
            if (self == Thread.CurrentThread) {
                // No need for the asynchronous machinery - just start unwinding.
                throw new RubyUtils.ThreadExitSignal();
            }

            RubyUtils.ExitThread(self);
            return self;
        }

        [RubyMethod("key?")]
        public static object HasKey(Thread/*!*/ self, [NotNull]RubySymbol/*!*/ key) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            return info.HasKey(key);
        }

        [RubyMethod("key?")]
        public static object HasKey(RubyContext/*!*/ context, Thread/*!*/ self, [NotNull]MutableString/*!*/ key) {
            return HasKey(self, context.CreateSymbol(key));
        }

        [RubyMethod("key?")]
        public static object HasKey(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key) {
            return HasKey(self, ToKey(toStr, context, key));
        }

        [RubyMethod("keys")]
        public static object Keys(RubyContext/*!*/ context, Thread/*!*/ self) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            return info.GetKeys();
        }

        #region priority, priority=
        [RubyMethod("priority")]
        public static object Priority(Thread/*!*/ self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            switch (self.Priority) {
                case ThreadPriority.Lowest:
                    return -2;
                case ThreadPriority.BelowNormal:
                    return -1;
                case ThreadPriority.Normal:
                    return 0;
                case ThreadPriority.AboveNormal:
                    return 1;
                case ThreadPriority.Highest:
                    return 2;
                default:
                    return 0;
            }
        }

        [RubyMethod("priority=")]
        public static Thread Priority(Thread/*!*/ self, int priority) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            if (priority <= -2)
                self.Priority = ThreadPriority.Lowest;
            else if (priority == -1)
                self.Priority = ThreadPriority.BelowNormal;
            else if (priority == 0)
                self.Priority = ThreadPriority.Normal;
            else if (priority == 1)
                self.Priority = ThreadPriority.AboveNormal;
            else
                self.Priority = ThreadPriority.Highest;

            return self;
        }
        #endregion
        #region raise, fail

#if FEATURE_EXCEPTION_STATE
        private static void RaiseAsyncException(Thread thread, Exception exception) {
            RubyThreadStatus status = GetStatus(thread);

            // rethrow semantics, preserves the backtrace associated with the exception:
            RubyUtils.RaiseAsyncException(thread, exception);

            if (status == RubyThreadStatus.Sleeping) {
                // Thread.Abort can interrupt a thread with ThreadState.WaitSleepJoin. However, Thread.Abort 
                // is deferred while the thread is in a catch block. If there is a Kernel.sleep in a catch block,
                // then that sleep will not be interrupted. 
                // TODO: We should call Run to nudge the thread if its CurrentException is not-null, and 
                // ThreadOps.Stop should have a checkpoint to see whether an async exception needs to be thrown

                // Run(thread);
            }
        }
#endif

        /// <summary>
        /// Thread#raise takes exactly the arguments Kernel#raise does, including `cause:`, and is
        /// built from the same helper so the two cannot drift apart.
        ///
        /// The cause is resolved in the *calling* thread's context, which is what MRI 4.0 does:
        /// the exception carries the cause of whoever raised it, not of the thread it lands in.
        /// </summary>
        [RubyMethod("raise")]
        [RubyStackTraceHidden]
        public static void RaiseException(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, Thread/*!*/ self, params object[]/*!*/ args) {

            if (self == Thread.CurrentThread) {
                KernelOps.RaiseException(respondToStorage, storage0, storage1, setBackTraceStorage, context, self, args);
                return;
            }

#if FEATURE_EXCEPTION_STATE
            // A bare `thread.raise` on *another* thread is a plain RuntimeError; it does not
            // re-raise the caller's $! (MRI can't see the target thread's $! either).
            Exception e = KernelOps.CreateExceptionToRaise(respondToStorage, storage0, storage1, setBackTraceStorage, context, args, false);
            RaiseAsyncException(self, e);
#else
            throw new NotImplementedError("Thread#raise not supported on this platform");
#endif
        }

        #endregion

        //    safe_level

        // TODO: these two methods interrupt a sleeping thread via the Thread.Interrupt API.
        // Unfortunately, this API interrupts the sleeping thread by throwing a ThreadInterruptedException.
        // In many Ruby programs (eg the specs) this causes the thread to terminate, which is NOT the
        // expected behavior. This is tracked by Rubyforge bug # 21157

        [RubyMethod("run")]
        [RubyMethod("wakeup")]
        public static Thread Run(Thread/*!*/ self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            info.Run();
            return self;
        }

        private static RubyThreadStatus GetStatus(Thread thread) {
            ThreadState state = thread.ThreadState;
            RubyThreadInfo info = RubyThreadInfo.FromThread(thread);

            if ((state & ThreadState.Unstarted) == ThreadState.Unstarted) {
                if (info.CreatedFromRuby) {
                    // Ruby threads do not have an unstarted status. We must be in the tiny window when ThreadOps.CreateThread
                    // created the thread, but has not called Thread.Start on it yet.
                    return RubyThreadStatus.Running;
                } else {
                    // This is a thread created from outside Ruby. In such a case, we do not know when Thread.Start
                    // will be called on it. So we report it as unstarted.
                    return RubyThreadStatus.Unstarted;
                }
            }

            if ((state & (ThreadState.Stopped|ThreadState.Aborted)) != 0) {
                if (RubyThreadInfo.FromThread(thread).Exception == null) {
                    return RubyThreadStatus.Completed;
                } else {
                    return RubyThreadStatus.Aborted;
                }
            }

            if ((state & ThreadState.WaitSleepJoin) == ThreadState.WaitSleepJoin) {
                // We will report a thread to be sleeping more often than in CRuby. This is because any "lock" statement
                // can potentially cause ThreadState.WaitSleepJoin. Also, "Thread.pass" does System.Threading.Thread.Sleep(0)
                // which also briefly changes the state to ThreadState.WaitSleepJoin
                return RubyThreadStatus.Sleeping;
            }

            if ((state & ThreadState.AbortRequested) != 0) {
                return RubyThreadStatus.Aborting;
            }

            if ((state & ThreadState.Running) == ThreadState.Running) {
                if (info.Blocked) {
                    return RubyThreadStatus.Sleeping;
                } else {
                    return RubyThreadStatus.Running;
                }
            }

#pragma warning disable 162 // msc: unreachable code
            throw new ArgumentException("unknown thread status: " + state);
#pragma warning restore 162
        }

        [RubyMethod("status")]
        public static object Status(Thread/*!*/ self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            switch (GetStatus(self)) {
                case RubyThreadStatus.Unstarted:
                    return MutableString.CreateAscii("unstarted");
                case RubyThreadStatus.Running:
                    return MutableString.CreateAscii("run");
                case RubyThreadStatus.Sleeping:
                    return MutableString.CreateAscii("sleep");
                case RubyThreadStatus.Aborting:
                    return MutableString.CreateAscii("aborting");
                case RubyThreadStatus.Completed:
                    return false;
                case RubyThreadStatus.Aborted:
                    return null;
                default:
                    throw new ArgumentException("unknown thread status");
            }
        }

        [RubyMethod("value")]
        public static object Value(Thread/*!*/ self) {
            Join(self);
            return RubyThreadInfo.FromThread(self).Result;
        }

        //    stop?

        //  declared singleton methods

        [RubyMethod("abort_on_exception", RubyMethodAttributes.PublicSingleton)]
        public static object GlobalAbortOnException(object self) {
            return _globalAbortOnException;
        }

        [RubyMethod("abort_on_exception=", RubyMethodAttributes.PublicSingleton)]
        public static object GlobalAbortOnException(object self, bool value) {
            _globalAbortOnException = value;
            return value;
        }

        private static void SetCritical(RubyContext/*!*/ context, bool value) {
            // Debug.Assert(context.RubyOptions.Compatibility < RubyCompatibility.Ruby19);
            if (value) {
                bool lockTaken = false;
                try {
                    MonitorUtils.Enter(context.CriticalMonitor, ref lockTaken);
                } finally {
                    // thread could have been aborted just before/after Monitor.Enter acquired the lock:
                    if (lockTaken) {
                        context.CriticalThread = Thread.CurrentThread;
                    }
                }
            } else {
                Monitor.Exit(context.CriticalMonitor);
                context.CriticalThread = null;
            }
        }

        [RubyMethod("critical", RubyMethodAttributes.PublicSingleton)] // Compatibility <= RubyCompatibility.Ruby18
        public static bool Critical(RubyContext/*!*/ context, object self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            return context.CriticalThread != null;
        }

        [RubyMethod("critical=", RubyMethodAttributes.PublicSingleton)]
        public static void Critical(RubyContext/*!*/ context, object self, bool value) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            SetCritical(context, value);
        }

        [RubyMethod("current", RubyMethodAttributes.PublicSingleton)]
        public static Thread/*!*/ Current(object self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            return Thread.CurrentThread;
        }

        //    exclusive
        //    fork
        [RubyMethod("list", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ List(object self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);

            RubyThreadInfo[] threads = RubyThreadInfo.Threads;
            RubyArray result = new RubyArray(threads.Length);
            foreach (RubyThreadInfo threadInfo in threads) {
                Thread thread = threadInfo.Thread;
                if (thread != null) {
                    result.Add(thread);
                }
            }

            return result;
        }

        [RubyMethod("main", RubyMethodAttributes.PublicSingleton)]
        public static Thread/*!*/ GetMainThread(RubyContext/*!*/ context, RubyClass self) {
            return context.MainThread;
        }

        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("start", RubyMethodAttributes.PublicSingleton)]
        public static Thread/*!*/ CreateThread(RubyContext/*!*/ context, BlockParam startRoutine, object self, params object[]/*!*/ args) {
            if (startRoutine == null) {
                throw new ThreadError("must be called with a block");
            }
            ThreadGroup group = Group(Thread.CurrentThread);
            Thread result = new Thread(new ThreadStart(() => RubyThreadStart(context, startRoutine, args, group)));

            // Ruby exits when the main thread exits. So all other threads need to be marked as background threads
            result.IsBackground = true;

            result.Start();
            return result;
        }

        private static void RubyThreadStart(RubyContext/*!*/ context, BlockParam/*!*/ startRoutine, object[]/*!*/ args, ThreadGroup group) {
            RubyThreadInfo info = RubyThreadInfo.FromThread(Thread.CurrentThread);
            info.CreatedFromRuby = true;

            info.Group = group;

            try {
                // Thread#kill / Thread#raise may have been called before the thread got a chance to run.
                RubyUtils.CheckAsyncException();

                object threadResult;
                // TODO: break/returns might throw LocalJumpError if the RFC that was created for startRoutine is not active anymore:
                if (startRoutine.Yield(args, out threadResult) && startRoutine.Returning(threadResult, out threadResult)) {
                    info.Exception = new ThreadError("return can't jump across threads");
                }
                info.Result = threadResult;
            } catch (MethodUnwinder) {
                info.Exception = new ThreadError("return can't jump across threads");
            } catch (Exception e) {
                if (e is ThreadInterruptedException) {
                    // We nudge a thread with Thread.Interrupt to deliver Thread#kill / Thread#raise. If the
                    // interrupt reached a wait we do not wrap, translate it here.
                    Exception pending = RubyUtils.GetPendingAsyncException(Thread.CurrentThread);
                    if (pending != null) {
                        e = pending;
                    }
                }

                if (RubyUtils.IsRubyThreadExit(e) || info.ExitRequested) {
                    // Note that "e" may not be the exit signal at this point: if an exception was raised from a
                    // finally block, we get that here instead.
                    Utils.Log(String.Format("Thread {0} exited.", info.Thread.ManagedThreadId), "THREAD");
                    info.Result = null;
                    if (!RubyUtils.IsRubyThreadExit(e) && !(e is ThreadInterruptedException)) {
                        info.Exception = RubyUtils.GetVisibleException(e);
                    }
                } else {
                    e = RubyUtils.GetVisibleException(e);
                    RubyExceptionData.ActiveExceptionHandled(e);
                    info.Exception = e;

                    StringBuilder trace = new StringBuilder();
                    trace.Append(e.Message);
                    trace.AppendLine();
                    trace.AppendLine();
                    trace.Append(e.StackTrace);
                    trace.AppendLine();
                    trace.AppendLine();
                    RubyExceptionData data = RubyExceptionData.GetInstance(e);
                    if (data.Backtrace != null) {
                        foreach (var frame in data.Backtrace) {
                            trace.Append(frame.ToString());
                        }
                    }

                    Utils.Log(trace.ToString(), "THREAD");

                    if (_globalAbortOnException || info.AbortOnException) {
                        // MRI re-raises the exception on the main thread. Never rethrow it here:
                        // this is a background thread, and an unhandled exception on one takes the
                        // whole process down. Park it for the main thread instead - it is delivered
                        // at the main thread's next blocking point, and #join still re-raises it
                        // because info.Exception is set.
                        Console.Error.WriteLine("#<Thread:0x{0:x8}> terminated with exception:",
                            info.Thread.ManagedThreadId);
                        Console.Error.WriteLine(e.Message);

                        Thread mainThread = context.MainThread;
                        if (mainThread != null && mainThread != Thread.CurrentThread) {
                            RubyUtils.RaiseAsyncException(mainThread, e);
                        }
                    }
                }
            } finally {
                // MRI releases every mutex a thread still holds when it dies.
                IronRuby.StandardLibrary.Threading.RubyMutex.ReleaseLocksOf(Thread.CurrentThread);

                // Its not a good idea to terminate a thread which has set Thread.critical=true, but its hard to predict
                // which thread will be scheduled next, even with green threads. However, ConditionVariable.create_timer 
                // in monitor.rb explicitly does "Thread.critical=true; other_thread.raise" before exiting, and expects
                // other_thread to be scheduled immediately.
                // To deal with such code, we release the critical monitor here if the current thread is holding it
                if (context.RubyOptions.Compatibility < RubyCompatibility.Ruby19 && context.CriticalThread == Thread.CurrentThread) {
                    SetCritical(context, false);
                }
            }
        }

        [RubyMethod("pass", RubyMethodAttributes.PublicSingleton)]
        public static void Yield(object self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            // Thread.pass is a safe point for a pending Thread#kill / Thread#raise.
            RubyUtils.CheckAsyncException();
            Thread.Sleep(0);
        }

        [RubyMethod("stop", RubyMethodAttributes.PublicSingleton)]
        public static void Stop(RubyContext/*!*/ context, object self) {
            if (context.CriticalThread == Thread.CurrentThread) {
                SetCritical(context, false);
            }
            DoSleep();
        }

        internal static void DoSleep() {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            // TODO: MRI throws an exception if you try to stop the main thread
            RubyThreadInfo info = RubyThreadInfo.FromThread(Thread.CurrentThread);
            info.Sleep();
        }

        /// <summary>
        /// Kernel#sleep(n). Uses the same signal as Thread.stop so that Thread#run/#wakeup ends the sleep early
        /// (MRI behavior) and so that Thread#kill/#raise can interrupt it. Returns the number of seconds slept.
        /// </summary>
        internal static int DoSleep(int milliseconds) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            RubyThreadInfo info = RubyThreadInfo.FromThread(Thread.CurrentThread);
            long start = Environment.TickCount64;
            info.Sleep(milliseconds);
            return (int)Math.Round((Environment.TickCount64 - start) / 1000.0);
        }

        /// <summary>Kernel#sleep(n) exposed to the threading library (Mutex#sleep).</summary>
        public static int SleepForLibrary(int milliseconds) {
            return DoSleep(milliseconds);
        }

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(RubyContext/*!*/ context, Thread/*!*/ self) {
            return Inspect(context, self);
        }

        #region name, name=

        [RubyMethod("name")]
        public static MutableString GetName(Thread/*!*/ self) {
            return RubyThreadInfo.FromThread(self).Name;
        }

        [RubyMethod("name=")]
        public static object SetName(Thread/*!*/ self, [DefaultProtocol]MutableString name) {
            if (name != null && name.IndexOf((byte)0) >= 0) {
                throw RubyExceptions.CreateArgumentError("string contains null byte");
            }
            RubyThreadInfo.FromThread(self).Name = name;
            if (name != null) {
                try {
                    self.Name = name.ToString();
                } catch (InvalidOperationException) {
                    // the CLR only lets a thread be named once
                }
            }
            return name;
        }

        #endregion

        #region thread variables

        [RubyMethod("thread_variable_get")]
        public static object GetThreadVariable(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key) {
            return RubyThreadInfo.FromThread(self).GetThreadVariable(ToKey(toStr, context, key));
        }

        [RubyMethod("thread_variable_set")]
        public static object SetThreadVariable(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key, object value) {
            RubyThreadInfo.FromThread(self).SetThreadVariable(ToKey(toStr, context, key), value);
            return value;
        }

        [RubyMethod("thread_variable?")]
        public static bool HasThreadVariable(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, Thread/*!*/ self, object key) {
            return RubyThreadInfo.FromThread(self).HasThreadVariable(ToKey(toStr, context, key));
        }

        [RubyMethod("thread_variables")]
        public static RubyArray/*!*/ GetThreadVariables(Thread/*!*/ self) {
            return RubyThreadInfo.FromThread(self).GetThreadVariableKeys();
        }

        #endregion

        #region fetch

        [RubyMethod("fetch")]
        public static object Fetch(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, BlockParam block, Thread/*!*/ self, object key) {
            return FetchInternal(toStr, context, block, self, key, true, null);
        }

        [RubyMethod("fetch")]
        public static object Fetch(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context, BlockParam block, Thread/*!*/ self, object key, object defaultValue) {
            return FetchInternal(toStr, context, block, self, key, false, defaultValue);
        }

        private static object FetchInternal(ConversionStorage<MutableString>/*!*/ toStr, RubyContext/*!*/ context,
            BlockParam block, Thread/*!*/ self, object key, bool noDefault, object defaultValue) {

            RubySymbol symbol = ToKey(toStr, context, key);
            RubyThreadInfo info = RubyThreadInfo.FromThread(self);
            if (info.HasKey(symbol)) {
                return info[symbol];
            }

            if (block != null) {
                object result;
                block.Yield(key, out result);
                return result;
            }

            if (!noDefault) {
                return defaultValue;
            }

            throw RubyExceptions.CreateIndexError("key not found: {0}", context.Inspect(key).ToString());
        }

        #endregion

        #region report_on_exception

        [RubyMethod("report_on_exception")]
        public static object ReportOnException(Thread/*!*/ self) {
            return RubyThreadInfo.FromThread(self).ReportOnException;
        }

        [RubyMethod("report_on_exception=")]
        public static object ReportOnException(Thread/*!*/ self, object value) {
            RubyThreadInfo.FromThread(self).ReportOnException = RubyOps.IsTrue(value);
            return value;
        }

        [RubyMethod("report_on_exception", RubyMethodAttributes.PublicSingleton)]
        public static object GlobalReportOnException(object self) {
            return _globalReportOnException;
        }

        [RubyMethod("report_on_exception=", RubyMethodAttributes.PublicSingleton)]
        public static object GlobalReportOnException(object self, object value) {
            _globalReportOnException = RubyOps.IsTrue(value);
            return value;
        }

        #endregion

        [RubyMethod("exit", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("kill", RubyMethodAttributes.PublicSingleton)]
        public static Thread/*!*/ KillThread(object self, [NotNull]Thread/*!*/ thread) {
            return Kill(thread);
        }

        /// <summary>
        /// Called by the Fiber implementation: a fiber runs on its own CLR thread but has to count as the
        /// thread that owns its fiber group wherever Ruby ownership semantics apply (Mutex, mostly).
        /// </summary>
        [RubyMethod("__set_fiber_owner__", RubyMethodAttributes.PublicSingleton)]
        public static object SetFiberOwner(object self, [NotNull]Thread/*!*/ owner) {
            RubyUtils.SetFiberOwnerThread(owner);
            return owner;
        }

        [RubyMethod("stop?", RubyMethodAttributes.PublicInstance)]
        public static bool IsStopped(Thread self) {
            RubyThreadInfo.RegisterThread(Thread.CurrentThread);
            RubyThreadStatus status = GetStatus(self);
            return status == RubyThreadStatus.Sleeping || status == RubyThreadStatus.Completed || status == RubyThreadStatus.Aborted;
        }
    }
}
#endif