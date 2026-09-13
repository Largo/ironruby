/* ****************************************************************************
 *
 * A Stream over a raw file descriptor, for the two things FileStream cannot do.
 *
 * FileStream tracks a position of its own and writes with pwrite(2) whenever it
 * believes the handle is seekable. Two of them over descriptors that share one
 * open file description - which is exactly what a child's standard output and
 * standard error are once both have been redirected to the same file - then each
 * write at their own offset and overwrite each other, where CRuby's sequential
 * writes append.
 *
 * And a read FileStream has started cannot be interrupted: closing the IO from
 * another thread, which Ruby defines as making the blocked read raise IOError,
 * leaves the reader parked in read(2) for ever. Waiting in poll(2) with a short
 * timeout and re-checking a flag gives the reader somewhere to notice from.
 *
 * ***************************************************************************/

using System;
using System.IO;
using System.Runtime.InteropServices;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>A stream that knows the descriptor it is reading and writing.</summary>
    public interface IDescriptorStream {
        int Descriptor { get; }
    }

    public sealed class DescriptorStream : Stream, IDescriptorStream {
        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd {
            public int fd;
            public short events;
            public short revents;
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "poll")]
        private static extern int sys_poll([In, Out] PollFd[] fds, uint count, int timeoutMilliseconds);

        [DllImport("libc", SetLastError = true, EntryPoint = "read")]
        private static extern IntPtr sys_read(int fd, byte[] buffer, IntPtr count);

        [DllImport("libc", SetLastError = true, EntryPoint = "write")]
        private static extern IntPtr sys_write(int fd, byte[] buffer, IntPtr count);

        [DllImport("libc", SetLastError = true, EntryPoint = "close")]
        private static extern int sys_close(int fd);

        private const short POLLIN = 0x001;
        private const int EINTR = 4;

        private readonly System.Threading.ManualResetEvent _closing = new System.Threading.ManualResetEvent(false);
        private int _descriptor;
        private volatile bool _closed;
        private readonly bool _readable;
        private readonly bool _writable;
        private readonly bool _ownsDescriptor;

        public DescriptorStream(int descriptor, bool readable, bool writable, bool ownsDescriptor) {
            _descriptor = descriptor;
            _readable = readable;
            _writable = writable;
            _ownsDescriptor = ownsDescriptor;
        }

        public int Descriptor {
            get { return _descriptor; }
        }

        public override bool CanRead { get { return _readable && !_closed; } }
        public override bool CanWrite { get { return _writable && !_closed; } }
        public override bool CanSeek { get { return false; } }

        public override long Length {
            get { throw new NotSupportedException(); }
        }

        public override long Position {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        public override void Flush() {
            // write(2) has already handed everything to the kernel.
        }

        public override int Read(byte[] buffer, int offset, int count) {
            if (count == 0) {
                return 0;
            }
            byte[] chunk = new byte[count];
            while (true) {
                if (_closed) {
                    throw RubyExceptions.CreateIOError("stream closed in another thread");
                }

                // poll(2) with no timeout at all, and then a managed wait: a thread parked in
                // a P/Invoke is still Running as far as the CLR is concerned, and Ruby code
                // that waits for a reader to block - Thread#stop? - would spin for ever. The
                // event is also what a close from another thread sets, so the reader wakes at
                // once rather than after the tick.
                var fds = new PollFd[1];
                fds[0].fd = _descriptor;
                fds[0].events = POLLIN;
                int ready = sys_poll(fds, 1, 0);
                if (ready < 0) {
                    if (Marshal.GetLastWin32Error() == EINTR) {
                        continue;
                    }
                    throw new IOException("poll failed");
                }
                if (ready == 0) {
                    _closing.WaitOne(5);
                    continue;
                }

                // The close may have come in while this thread was in poll; the descriptor is
                // gone by now, so read(2) would report a bad descriptor rather than the
                // IOError Ruby defines for a stream closed underneath a blocked read.
                if (_closed) {
                    throw RubyExceptions.CreateIOError("stream closed in another thread");
                }

                int read = (int)sys_read(_descriptor, chunk, (IntPtr)count);
                if (read < 0) {
                    if (Marshal.GetLastWin32Error() == EINTR) {
                        continue;
                    }
                    throw new IOException("read failed");
                }
                Buffer.BlockCopy(chunk, 0, buffer, offset, read);
                return read;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) {
            int written = 0;
            byte[] chunk = new byte[count];
            Buffer.BlockCopy(buffer, offset, chunk, 0, count);
            while (written < count) {
                byte[] rest;
                if (written == 0) {
                    rest = chunk;
                } else {
                    rest = new byte[count - written];
                    Buffer.BlockCopy(chunk, written, rest, 0, count - written);
                }
                int n = (int)sys_write(_descriptor, rest, (IntPtr)(count - written));
                if (n < 0) {
                    if (Marshal.GetLastWin32Error() == EINTR) {
                        continue;
                    }
                    throw new IOException("write failed");
                }
                written += n;
            }
        }

        protected override void Dispose(bool disposing) {
            _closed = true;
            _closing.Set();
            int descriptor = _descriptor;
            if (_ownsDescriptor && descriptor >= 0) {
                _descriptor = -1;
                sys_close(descriptor);
            }
            base.Dispose(disposing);
        }
    }
}
