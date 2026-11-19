/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// rb_io_t. Extensions reach an IO's fields through RFILE(io)->fptr - GetOpenFile and
    /// RB_IO_POINTER read it after calling rb_io_taint_check - so an IO's handle slot points at a
    /// native struct rb_io (the second word after RBasic, where struct RFile keeps it), which
    /// rb_io_taint_check fills in from the RubyIO: self, fd, mode (FMODE_* flags) and pathv.
    /// The functions that take an rb_io_t * find the RubyIO again through fptr->self. The
    /// struct's layout comes from the shim (rb_ir_io_layout), which compiles it from the headers.
    /// </summary>
    internal static unsafe partial class CApi {
        private const int FMODE_READABLE = 0x1, FMODE_WRITABLE = 0x2, FMODE_BINMODE = 0x4, FMODE_SYNC = 0x8,
            FMODE_APPEND = 0x40, FMODE_CREATE = 0x80, FMODE_EXCL = 0x400, FMODE_TRUNC = 0x800;
        private const int RUBY_IO_READABLE = 1, RUBY_IO_PRIORITY = 2, RUBY_IO_WRITABLE = 4;
        private const int RFileFptrWord = 2;

        private static nuint[] _ioLayout; // size, then the offsets of self, fd, mode, pathv

        private static nuint[]/*!*/ IoLayout() {
            if (_ioLayout == null) {
                var layout = new nuint[5];
                fixed (nuint* p = layout) {
                    ((delegate* unmanaged[Cdecl]<nuint*, void>)ShimExport("rb_ir_io_layout"))(p);
                }
                _ioLayout = layout;
            }
            return _ioLayout;
        }

        private static int FileMode(RubyIO/*!*/ io) {
            IOMode mode = io.Mode;
            if (io.Closed) {
                return 0;
            }
            int result;
            switch (mode & IOMode.ReadWriteMask) {
                case IOMode.WriteOnly: result = FMODE_WRITABLE; break;
                case IOMode.ReadWrite: result = FMODE_READABLE | FMODE_WRITABLE; break;
                default: result = FMODE_READABLE; break;
            }
            if ((mode & IOMode.WriteAppends) != 0) result |= FMODE_APPEND;
            if ((mode & IOMode.CreateIfNotExists) != 0) result |= FMODE_CREATE;
            if ((mode & IOMode.ErrorIfExists) != 0) result |= FMODE_EXCL;
            if ((mode & IOMode.Truncate) != 0) result |= FMODE_TRUNC;
            if (io.IsBinmode) result |= FMODE_BINMODE;
            if (io.AutoFlush) result |= FMODE_SYNC;
            return result;
        }

        /// <summary>
        /// Points the IO's slot at its rb_io_t, brought up to date - or at nothing while the IO is
        /// not initialized, which rb_io_check_closed reports as CRuby does for a NULL fptr.
        /// </summary>
        private static void RefreshFptr(VALUE handle, RubyIO/*!*/ io) {
            VALUE* slot = (VALUE*)handle;
            if (!io.Initialized) {
                slot[RFileFptrWord] = 0;
                return;
            }
            nuint[] layout = IoLayout();
            byte* fptr = (byte*)slot[RFileFptrWord];
            if (fptr == null) {
                fptr = (byte*)NativeMemory.AllocZeroed(layout[0]);
                slot[RFileFptrWord] = (VALUE)fptr;
            }
            object path = null;
            if (!io.Closed && io is RubyFile) {
                try {
                    path = Call(io, "path");
                } catch (Exception) {
                    // no path to give
                }
            }
            *(VALUE*)(fptr + layout[1]) = handle;
            *(int*)(fptr + layout[2]) = io.Closed ? -1 : io.GetFileDescriptor();
            *(int*)(fptr + layout[3]) = FileMode(io);
            *(VALUE*)(fptr + layout[4]) = ToValue(path);
        }

        private static RubyIO/*!*/ IoOf(void* fptr) {
            if (fptr == null) {
                throw RubyExceptions.CreateIOError("uninitialized stream");
            }
            var io = FromValue(*(VALUE*)((byte*)fptr + IoLayout()[1])) as RubyIO;
            if (io == null) {
                throw RubyExceptions.CreateTypeError("not an IO");
            }
            return io;
        }

        private static RubyIO/*!*/ CheckOpen(RubyIO/*!*/ io) {
            if (!io.Initialized) {
                throw RubyExceptions.CreateIOError("uninitialized stream");
            }
            if (io.Closed) {
                throw RubyExceptions.CreateIOError("closed stream");
            }
            return io;
        }

        private static RubyIO/*!*/ OpenIo(VALUE io) {
            var rio = FromValue(io) as RubyIO;
            if (rio == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type " + _context.GetClassDisplayName(FromValue(io)) + " (expected IO)");
            }
            return CheckOpen(rio);
        }

        internal static VALUE rb_io_taint_check(VALUE obj) {
            object o = FromValue(obj);
            if (_context.IsObjectFrozen(o)) {
                throw RubyExceptions.CreateObjectFrozenError(_context, o);
            }
            if (o is RubyIO io) {
                RefreshFptr(obj, io);
            }
            return obj;
        }

        internal static void rb_io_check_initialized(void* fptr) {
            IoOf(fptr);
        }

        internal static void rb_io_check_closed(void* fptr) {
            CheckOpen(IoOf(fptr));
        }

        private static void CheckMode(void* fptr, int flag, string/*!*/ message) {
            RubyIO io = CheckOpen(IoOf(fptr));
            if ((FileMode(io) & flag) == 0) {
                throw RubyExceptions.CreateIOError(message);
            }
        }

        internal static void rb_io_check_readable(void* fptr) { CheckMode(fptr, FMODE_READABLE, "not opened for reading"); }
        internal static void rb_io_check_char_readable(void* fptr) { CheckMode(fptr, FMODE_READABLE, "not opened for reading"); }
        internal static void rb_io_check_byte_readable(void* fptr) { CheckMode(fptr, FMODE_READABLE, "not opened for reading"); }
        internal static void rb_io_check_writable(void* fptr) { CheckMode(fptr, FMODE_WRITABLE, "not opened for writing"); }

        internal static int rb_io_mode(VALUE io) {
            return FileMode(OpenIo(io));
        }

        internal static void rb_io_set_nonblock(void* fptr) {
            RubyIO io = CheckOpen(IoOf(fptr));
            Send(_context.KernelModule, "require", null, MutableString.CreateAscii("io/nonblock"));
            Call(io, "nonblock=", true);
        }

        /// <summary>
        /// io.c's: waits for the events (RUBY_IO_READABLE, _PRIORITY, _WRITABLE) with IO.select;
        /// the events that came ready, or Qfalse after the timeout - IO#timeout when nil. The
        /// errno-driven rb_io_maybe_wait* around it are native/capi.c's.
        /// </summary>
        internal static VALUE rb_io_wait(VALUE io, VALUE events, VALUE timeout) {
            RubyIO rio = OpenIo(io);
            int wanted = Convert.ToInt32(FromValue(events));
            object limit = FromValue(timeout) ?? rio.Timeout;
            var select = Send(Constant("IO"), "select", null,
                (wanted & RUBY_IO_READABLE) != 0 ? RubyArray.Create(rio) : null,
                (wanted & RUBY_IO_WRITABLE) != 0 ? RubyArray.Create(rio) : null,
                (wanted & RUBY_IO_PRIORITY) != 0 ? RubyArray.Create(rio) : null,
                limit) as RubyArray;
            CheckOpen(rio);
            if (select == null) {
                return Qfalse;
            }
            int ready = 0;
            if (select[0] is RubyArray r && r.Count > 0) ready |= RUBY_IO_READABLE;
            if (select[1] is RubyArray w && w.Count > 0) ready |= RUBY_IO_WRITABLE;
            if (select[2] is RubyArray e && e.Count > 0) ready |= RUBY_IO_PRIORITY;
            return ready != 0 ? ToValue(ready) : Qfalse;
        }

        #region Thread waits (native/capi.c's select loop and rb_io_maybe_wait call these)

        internal static void rb_thread_check_ints() {
            RubyUtils.CheckAsyncException(true);
        }

        internal static void rb_thread_sleep_forever() {
            Send(_context.KernelModule, "sleep", null);
        }

        internal static void rb_thread_wait_for(TimeVal time) {
            Send(_context.KernelModule, "sleep", null, time.tv_sec + time.tv_usec / 1e6);
        }

        #endregion
    }
}
