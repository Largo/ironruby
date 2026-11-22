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
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;

namespace IronRuby.StandardLibrary.Sockets {
    internal class SocketStream : Stream, IDescriptorStream, INonBlockingWriteStream {
        internal readonly Socket/*!*/ _socket;

        internal Socket/*!*/ Socket {
            get { return _socket; }
        }

        public SocketStream(Socket/*!*/ s) {
            Assert.NotNull(s);
            _socket = s;
        }

        /// <summary>
        /// The socket's own descriptor. Without it IO.select had nothing to hand poll(2) and
        /// fell back to "no descriptor, so assume ready", which reported every socket as
        /// readable whether or not anything had arrived.
        /// </summary>
        public int Descriptor {
            get { return (int)_socket.Handle; }
        }

        public override bool CanRead {
            get { return true; }
        }

        public override bool CanSeek {
            get { return false; }
        }

        public override bool CanWrite {
            get { return true; }
        }

        public override void Close() {
            base.Close();
            _socket.Close();
        }

        public override void Flush() {
        }

        private int _readTimeout = Timeout.Infinite;
        private int _writeTimeout = Timeout.Infinite;

        public override bool CanTimeout {
            get { return true; }
        }

        // Reads wait for readability under IO#timeout before they start (RubyBufferedStream), so
        // only the write needs to know it.
        public override int ReadTimeout {
            get { return _readTimeout; }
            set { _readTimeout = value; }
        }

        public override int WriteTimeout {
            get { return _writeTimeout; }
            set { _writeTimeout = value; }
        }

        public override long Length {
            get {
                throw new InvalidOperationException();
            }
        }

        public override long Position {
            get {
                throw new InvalidOperationException();
            }
            set {
                throw new InvalidOperationException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) {
            return RubyBasicSocket.Blocking(_socket, SelectMode.SelectRead, () => _socket.Receive(buffer, offset, count, SocketFlags.None));
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new InvalidOperationException();
        }

        public override void SetLength(long value) {
            throw new InvalidOperationException();
        }

        // IO#write writes all of it, as MRI's io_binwrite does. On a non-blocking socket (IO#nonblock=,
        // which servers and IO.copy_stream's callers set) send(2) takes what fits in the kernel buffer and
        // says how much, or EAGAIN when nothing fits, and MRI then waits for the socket to be writable
        // (rb_io_maybe_wait_writable) and goes on. A single Send here returned a short count that was
        // dropped: the rest of the String never went out, and an EAGAIN came out as a SocketError.
        //
        // IO#timeout bounds the whole write (RubyIO.ApplyTimeout sets WriteTimeout here), and
        // running out of it is a TimeoutException, which the buffered stream reports as
        // IO::TimeoutError.
        public override void Write(byte[] buffer, int offset, int count) {
            if (_socket.Blocking) {
                if (_writeTimeout == Timeout.Infinite) {
                    RubyBasicSocket.Blocking<int>(() => _socket.Send(buffer, offset, count, SocketFlags.None));
                    return;
                }
                int saved = _socket.SendTimeout;
                _socket.SendTimeout = Math.Max(_writeTimeout, 1);
                try {
                    RubyBasicSocket.Blocking<int>(() => _socket.Send(buffer, offset, count, SocketFlags.None));
                } catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) {
                    throw new TimeoutException();
                } finally {
                    _socket.SendTimeout = saved;
                }
                return;
            }
            var clock = (_writeTimeout == Timeout.Infinite) ? null : Stopwatch.StartNew();
            while (count > 0) {
                SocketError error;
                int sent = _socket.Send(buffer, offset, count, SocketFlags.None, out error);
                if (error == SocketError.WouldBlock) {
                    // a safe point every slice, as the blocking waits are (RubyBasicSocket.BlockingCore)
                    RubyBasicSocket.Blocking<int>(() => {
                        while (!_socket.Poll(RubyBasicSocket.PollSliceMicroseconds, SelectMode.SelectWrite)) {
                            IronRuby.Runtime.RubyUtils.CheckAsyncException();
                            if (clock != null && clock.ElapsedMilliseconds >= _writeTimeout) {
                                throw new TimeoutException();
                            }
                        }
                        return 0;
                    });
                    continue;
                }
                if (error != SocketError.Success) {
                    throw new SocketException((int)error);
                }
                offset += sent;
                count -= sent;
            }
        }

        // On a non-blocking socket Send takes what fits in the kernel buffer and says how much;
        // #write_nonblock answers that count, where Write above goes on until all of it is sent.
        public int WriteNonBlocking(byte[]/*!*/ buffer, int offset, int count) {
            return _socket.Send(buffer, offset, count, SocketFlags.None);
        }
    }
}
