/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Threading;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// A loaded C extension and its lock. IronRuby has no GVL, so without a lock two threads would
    /// run an extension's C code at once, which CRuby never does: most extensions keep state - a
    /// cache, a static buffer, a struct inside a shared object - that assumes they cannot. So an
    /// extension's C functions run under its lock unless it says they need none, with
    /// rb_ext_thread_safe(true) (TruffleRuby's) or rb_ext_ractor_safe(true) (CRuby's, for code
    /// that can run in several Ractors at once, so on several threads too).
    /// </summary>
    internal sealed class CExtension {
        internal readonly string/*!*/ Path;
        internal readonly object/*!*/ Lock = new object();

        // what rb_ext_thread_safe / rb_ext_ractor_safe last said, for the functions defined after it
        internal volatile bool ThreadSafe;

        internal readonly CCode/*!*/ LockedCode, FreeCode;

        // its Init_, which runs under the lock as it does under CRuby's GVL: a method Init_ has
        // defined already can be called from another thread before Init_ returns
        internal readonly CCode/*!*/ InitCode;

        internal CExtension(string/*!*/ path, bool locks) {
            Path = path;
            LockedCode = new CCode(this, true, false);
            FreeCode = new CCode(this, false, false);
            InitCode = new CCode(this, locks, true);
        }
    }

    /// <summary>
    /// A C function an extension handed to IronRuby - a method, an allocator, a block, a hook:
    /// the extension it belongs to and whether it runs under that extension's lock.
    /// </summary>
    internal sealed class CCode {
        internal readonly CExtension Extension;
        internal readonly bool Locked;
        // the extension's Init_ (CApi.CurrentCode)
        internal readonly bool Init;

        // not an extension's, or with IRONRUBY_CEXT_LOCK=0
        internal static readonly CCode/*!*/ Free = new CCode(null, false, false);

        internal CCode(CExtension extension, bool locked, bool init) {
            Extension = extension;
            Locked = locked;
            Init = init;
        }
    }

    /// <summary>What a call into C changes on its thread, put back when it returns (CApi.EnterNative).</summary>
    internal struct NativeScope {
        internal int Depth;
        internal CExtension Extension, Held;
        internal int HeldCount;
        internal bool InInit;
    }

    /// <summary>An extension lock let go while C runs Ruby code (CApi.ReleaseForRuby).</summary>
    internal struct ReleasedLock {
        internal CExtension Held;
        internal int Count;
    }

    internal static unsafe partial class CApi {
        // IRONRUBY_CEXT_LOCK=0: every extension's code runs on any number of threads at once, as
        // TruffleRuby's --cexts-lock=false (for one known to be safe that does not say so)
        private static readonly bool _extensionLocks = Environment.GetEnvironmentVariable("IRONRUBY_CEXT_LOCK") != "0";

        // the extension whose C code this thread is running, and whether that is its Init_
        [ThreadStatic] private static CExtension _extension;
        [ThreadStatic] private static bool _inInit;

        // The extension lock this thread holds, and how many calls into that extension's locked
        // code are under way on it. A thread holds one extension's lock at most: calling into
        // another extension lets go of the first, so two extensions calling each other on two
        // threads cannot deadlock.
        [ThreadStatic] private static CExtension _held;
        [ThreadStatic] private static int _heldCount;

        /// <summary>
        /// What a C function the running extension hands IronRuby now is: locked unless the
        /// extension has said it is thread-safe - or, outside its Init_, unless the code handing
        /// it over is itself not locked, as TruffleRuby decides.
        /// </summary>
        internal static CCode/*!*/ CurrentCode() {
            var ext = _extension;
            if (ext == null || !_extensionLocks) {
                return CCode.Free;
            }
            bool locked = !ext.ThreadSafe || !_inInit && _held == ext;
            return locked ? ext.LockedCode : ext.FreeCode;
        }

        // rb_ext_thread_safe, rb_ext_ractor_safe (native/capi.c)
        internal static void rb_ir_ext_thread_safe(int flag) {
            var ext = _extension;
            if (ext != null) {
                ext.ThreadSafe = flag != 0;
            }
        }

        /// <summary>
        /// Before calling <paramref name="code"/>: its extension becomes this thread's, and its
        /// lock is taken if it needs it. Null is a callback C asks for while it runs (rb_hash_foreach's,
        /// rb_protect's): it runs as the C code that asked.
        /// </summary>
        private static void EnterCode(CCode code, ref NativeScope scope) {
            scope.Extension = _extension;
            scope.InInit = _inInit;
            scope.Held = _held;
            scope.HeldCount = _heldCount;
            if (code == null) {
                return;
            }
            _extension = code.Extension;
            _inInit = code.Init;
            if (code.Locked) {
                var ext = code.Extension;
                if (_held == ext) {
                    _heldCount++;
                } else {
                    if (_held != null) {
                        Monitor.Exit(_held.Lock);
                        _held = null;
                    }
                    EnterLock(ext);
                    _held = ext;
                    _heldCount = 1;
                }
            }
        }

        private static void LeaveCode(ref NativeScope scope) {
            if (_held != scope.Held) {
                if (_held != null) {
                    Monitor.Exit(_held.Lock);
                    _held = null;
                }
                if (scope.Held != null) {
                    EnterLock(scope.Held);
                }
                _held = scope.Held;
            }
            _heldCount = scope.HeldCount;
            _extension = scope.Extension;
            _inInit = scope.InInit;
        }

        /// <summary>
        /// Lets go of the extension lock while C runs Ruby code (rb_funcall, rb_yield...) or
        /// blocks (rb_thread_call_without_gvl): CRuby lets other threads run there too, and Ruby
        /// code that waits for another thread would otherwise wait for one that wants this lock.
        /// </summary>
        internal static ReleasedLock ReleaseForRuby() {
            var released = new ReleasedLock { Held = _held, Count = _heldCount };
            if (_held != null) {
                Monitor.Exit(_held.Lock);
                _held = null;
                _heldCount = 0;
            }
            return released;
        }

        internal static void Reacquire(ReleasedLock released) {
            if (released.Held == null || _held == released.Held) {
                return;
            }
            if (_held != null) {
                Monitor.Exit(_held.Lock);
                _held = null;
            }
            EnterLock(released.Held);
            _held = released.Held;
            _heldCount = released.Count;
        }

        /// <summary>
        /// Takes an extension's lock, however long another thread keeps it, as a thread waits for
        /// CRuby's GVL. A nudge (Thread#raise, Thread#kill: RubyUtils.NudgeThread) breaks a
        /// contended Monitor.Enter, and then the thread would go on without the lock its C code
        /// assumes - into an rb_ensure's cleanup, or back into the C function that called Ruby. So
        /// the wait goes on, as RubyUtils.UninterruptedLock's does, and what the nudge was for stays
        /// parked until the thread's next safe point.
        /// </summary>
        private static void EnterLock(CExtension/*!*/ ext) {
            bool taken = false;
            while (true) {
                try {
                    Monitor.Enter(ext.Lock, ref taken);
                    return;
                } catch (ThreadInterruptedException) {
                    if (taken) {
                        return;
                    }
                }
            }
        }

        /// <summary>Calls an extension's Init_, under its lock: its code from here on is that extension's.</summary>
        private static void CallInit(string/*!*/ path, IntPtr init) {
            var ext = new CExtension(path, _extensionLocks);
            CallVoid(init, null, ext.InitCode);
        }
    }
}
