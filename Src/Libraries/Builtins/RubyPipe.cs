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
using System.Diagnostics;
using System.IO;
using System.Threading;
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    /// <summary>
    /// Pipe for intra-process producer-consumer style message passing
    /// </summary>
    internal class RubyPipe : Stream {
        private readonly EventWaitHandle _dataAvailableEvent;
        private readonly EventWaitHandle _writerClosedEvent;
        private readonly EventWaitHandle _readerClosedEvent;
        private readonly WaitHandle[] _eventArray;
        private readonly Queue<byte> _queue;

        private const int WriterClosedEventIndex = 1;
        private const int ReaderClosedEventIndex = 2;

        private RubyPipe() {
            _dataAvailableEvent = new AutoResetEvent(false);
            _writerClosedEvent = new ManualResetEvent(false);
            _readerClosedEvent = new ManualResetEvent(false);
            _eventArray = new WaitHandle[3];
            _queue = new Queue<byte>();

            _eventArray[0] = _dataAvailableEvent;
            _eventArray[1] = _writerClosedEvent;
            _eventArray[2] = _readerClosedEvent;
            Debug.Assert(_eventArray[WriterClosedEventIndex] == _writerClosedEvent);
            Debug.Assert(_eventArray[ReaderClosedEventIndex] == _readerClosedEvent);
        }

        private RubyPipe(RubyPipe pipe) {
            _dataAvailableEvent = pipe._dataAvailableEvent;
            _writerClosedEvent = pipe._writerClosedEvent;
            _readerClosedEvent = pipe._readerClosedEvent;
            _eventArray = pipe._eventArray;
            _queue = pipe._queue;
        }

        internal void CloseWriter() {
            _writerClosedEvent.Set();
        }

        internal void CloseReader() {
            // Wakes up a thread parked in Read so that closing the read end of a pipe from another
            // thread terminates the blocked read, the way CRuby does.
            _readerClosedEvent.Set();
        }

        /// <summary>
        /// Called when this end of the pipe is closed. The reader and the writer are distinct objects
        /// so that each end can signal the other; see PipeWriter.
        /// </summary>
        protected virtual void OnClose() {
            CloseReader();
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                OnClose();
            }
            base.Dispose(disposing);
        }

        public static void CreatePipe(out Stream reader, out Stream writer) {
            RubyPipe pipe = new RubyPipe();
            reader = pipe;
            writer = new PipeWriter(pipe);
        }

        public override bool CanRead {
            get { return true; }
        }

        public override bool CanSeek {
            get { throw new NotImplementedException(); }
        }

        public override bool CanWrite {
            get { return true; }
        }

        public override void Flush() {
            throw new NotImplementedException();
        }

        public override long Length {
            get { throw new NotImplementedException(); }
        }

        public override long Position {
            get {
                throw new NotImplementedException();
            }
            set {
                throw new NotImplementedException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) {
            if (count == 0) {
                return 0;
            }

            while (true) {
                lock (((ICollection)_queue).SyncRoot) {
                    if (_queue.Count > 0) {
                        int read = Math.Min(count, _queue.Count);
                        for (int i = 0; i < read; i++) {
                            buffer[offset + i] = _queue.Dequeue();
                        }
                        if (_queue.Count > 0) {
                            // _dataAvailableEvent is an AutoResetEvent, so re-arm it for the bytes we left behind.
                            _dataAvailableEvent.Set();
                        }
                        return read;
                    }

                    if (_writerClosedEvent.WaitOne(0)) {
                        // Writer is gone and the queue is drained: end of file.
                        return 0;
                    }
                }

                // Wait until data is available, the writer closes the pipe, or this end is closed
                // from another thread.
                if (WaitHandle.WaitAny(_eventArray) == ReaderClosedEventIndex) {
                    throw RubyExceptions.CreateIOError("stream closed in another thread");
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotImplementedException();
        }

        public override void SetLength(long value) {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            lock (((ICollection)_queue).SyncRoot) {
                for (int idx = 0; idx < count; idx++) {
                    _queue.Enqueue(buffer[offset + idx]);
                }
                _dataAvailableEvent.Set();
            }
        }

        /// <summary>
        /// PipeWriter instance always exists as a sibling of a RubyPipe. Two objects are needed
        /// so that we can detect whether Close is being called on the reader end of a pipe,
        /// or on the writer end of a pipe.
        /// </summary>
        internal class PipeWriter : RubyPipe {
            internal PipeWriter(RubyPipe pipe)
                : base(pipe) {
            }

            protected override void OnClose() {
                CloseWriter();
            }
        }
    }
}
