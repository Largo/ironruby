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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Zlib {

    /// <summary>
    /// The parts of libz this library is built on.
    ///
    /// Compressed output has to be byte-for-byte what CRuby produces - the specs compare exact
    /// bytes - so the compressor itself is the system's, not a reimplementation.
    /// </summary>
    internal static class LibZ {
        private const string Lib = "libz";

        internal const int Z_NO_FLUSH = 0;
        internal const int Z_SYNC_FLUSH = 2;
        internal const int Z_FINISH = 4;

        internal const int Z_OK = 0;
        internal const int Z_STREAM_END = 1;
        internal const int Z_NEED_DICT = 2;
        internal const int Z_ERRNO = -1;
        internal const int Z_STREAM_ERROR = -2;
        internal const int Z_DATA_ERROR = -3;
        internal const int Z_MEM_ERROR = -4;
        internal const int Z_BUF_ERROR = -5;
        internal const int Z_VERSION_ERROR = -6;

        internal const int Z_DEFLATED = 8;

        /// <summary>
        /// z_stream, laid out the way libz declares it. The uLong fields are C's `unsigned long`,
        /// which is pointer-sized on the LP64 and ILP32 platforms this runs on.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ZStreamRec {
            public IntPtr next_in;
            public uint avail_in;
            public UIntPtr total_in;
            public IntPtr next_out;
            public uint avail_out;
            public UIntPtr total_out;
            public IntPtr msg;
            public IntPtr state;
            public IntPtr zalloc;
            public IntPtr zfree;
            public IntPtr opaque;
            public int data_type;
            public UIntPtr adler;
            public UIntPtr reserved;
        }

        [DllImport(Lib, EntryPoint = "zlibVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sys_zlibVersion();

        private static string _version;

        /// <summary>
        /// The version libz reports at run time, which is also what has to be handed to the
        /// init functions - they refuse to initialize a stream against a mismatched header.
        /// </summary>
        internal static string Version {
            get {
                if (_version == null) {
                    _version = Marshal.PtrToStringAnsi(sys_zlibVersion()) ?? "1.2.11";
                }
                return _version;
            }
        }

        internal static readonly int StreamSize = Marshal.SizeOf(typeof(ZStreamRec));

        [DllImport(Lib, EntryPoint = "deflateInit2_", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int deflateInit2_(IntPtr strm, int level, int method, int windowBits,
            int memLevel, int strategy, string/*!*/ version, int stream_size);

        [DllImport(Lib, EntryPoint = "deflate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int deflate(IntPtr strm, int flush);

        [DllImport(Lib, EntryPoint = "deflateEnd", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int deflateEnd(IntPtr strm);

        [DllImport(Lib, EntryPoint = "deflateReset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int deflateReset(IntPtr strm);

        [DllImport(Lib, EntryPoint = "deflateParams", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int deflateParams(IntPtr strm, int level, int strategy);

        [DllImport(Lib, EntryPoint = "deflateSetDictionary", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int deflateSetDictionary(IntPtr strm, byte[]/*!*/ dictionary, uint dictLength);

        [DllImport(Lib, EntryPoint = "inflateInit2_", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int inflateInit2_(IntPtr strm, int windowBits, string/*!*/ version, int stream_size);

        [DllImport(Lib, EntryPoint = "inflate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int inflate(IntPtr strm, int flush);

        [DllImport(Lib, EntryPoint = "inflateEnd", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int inflateEnd(IntPtr strm);

        [DllImport(Lib, EntryPoint = "inflateReset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int inflateReset(IntPtr strm);

        [DllImport(Lib, EntryPoint = "inflateSetDictionary", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int inflateSetDictionary(IntPtr strm, byte[]/*!*/ dictionary, uint dictLength);

        [DllImport(Lib, EntryPoint = "inflateSyncPoint", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int inflateSyncPoint(IntPtr strm);

        [DllImport(Lib, EntryPoint = "adler32", CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr adler32(UIntPtr adler, byte[] buf, uint len);

        [DllImport(Lib, EntryPoint = "crc32", CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr crc32(UIntPtr crc, byte[] buf, uint len);

        [DllImport(Lib, EntryPoint = "adler32_combine", CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr adler32_combine(UIntPtr adler1, UIntPtr adler2, long len2);

        [DllImport(Lib, EntryPoint = "crc32_combine", CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr crc32_combine(UIntPtr crc1, UIntPtr crc2, long len2);

        [DllImport(Lib, EntryPoint = "get_crc_table", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr get_crc_table();
    }

    [RubyModule("Zlib")]
    public static class Zlib {

        #region Constants

        [RubyConstant("NO_FLUSH")]
        public const int NO_FLUSH = 0;

        [RubyConstant("SYNC_FLUSH")]
        public const int SYNC_FLUSH = 2;

        [RubyConstant("FULL_FLUSH")]
        public const int FULL_FLUSH = 3;

        [RubyConstant("FINISH")]
        public const int FINISH = 4;

        [RubyConstant("ZLIB_VERSION")]
        public static string ZLIB_VERSION = ZNative.Version;

        // The version of the `zlib` gem whose API this implements, not libz's own version.
        [RubyConstant("VERSION")]
        public static string VERSION = "3.2.3";

        [RubyConstant("MAXBITS")]
        public const int MAXBITS = 15;

        [RubyConstant("MAXLCODES")]
        public const int MAXLCODES = 286;

        [RubyConstant("MAXDCODES")]
        public const int MAXDCODES = 30;

        [RubyConstant("MAXCODES")]
        public const int MAXCODES = (MAXLCODES + MAXDCODES);

        [RubyConstant("FIXLCODES")]
        public const int FIXLCODES = 288;

        [RubyConstant("MAX_WBITS")]
        public const int MAX_WBITS = 15;

        [RubyConstant("MAX_MEM_LEVEL")]
        public const int MAX_MEM_LEVEL = 9;

        [RubyConstant("DEF_MEM_LEVEL")]
        public const int DEF_MEM_LEVEL = 8;

        [RubyConstant("Z_DEFLATED")]
        public const int Z_DEFLATED = 8;

        [RubyConstant("BINARY")]
        public const int BINARY = 0;

        [RubyConstant("ASCII")]
        public const int ASCII = 1;

        [RubyConstant("TEXT")]
        public const int TEXT = 1;

        [RubyConstant("UNKNOWN")]
        public const int UNKNOWN = 2;

        [RubyConstant("NO_COMPRESSION")]
        public const int NO_COMPRESSION = 0;

        [RubyConstant("BEST_SPEED")]
        public const int BEST_SPEED = 1;

        [RubyConstant("BEST_COMPRESSION")]
        public const int BEST_COMPRESSION = 9;

        [RubyConstant("DEFAULT_COMPRESSION")]
        public const int DEFAULT_COMPRESSION = -1;

        [RubyConstant("FILTERED")]
        public const int FILTERED = 1;

        [RubyConstant("HUFFMAN_ONLY")]
        public const int HUFFMAN_ONLY = 2;

        [RubyConstant("RLE")]
        public const int RLE = 3;

        [RubyConstant("FIXED")]
        public const int FIXED = 4;

        [RubyConstant("DEFAULT_STRATEGY")]
        public const int DEFAULT_STRATEGY = 0;

        [RubyConstant("OS_MSDOS")]
        public const int OS_MSDOS = 0x00;

        [RubyConstant("OS_AMIGA")]
        public const int OS_AMIGA = 0x01;

        [RubyConstant("OS_VMS")]
        public const int OS_VMS = 0x02;

        [RubyConstant("OS_UNIX")]
        public const int OS_UNIX = 0x03;

        [RubyConstant("OS_VMCMS")]
        public const int OS_VMCMS = 0x04;

        [RubyConstant("OS_ATARI")]
        public const int OS_ATARI = 0x05;

        [RubyConstant("OS_OS2")]
        public const int OS_OS2 = 0x06;

        [RubyConstant("OS_MACOS")]
        public const int OS_MACOS = 0x07;

        [RubyConstant("OS_ZSYSTEM")]
        public const int OS_ZSYSTEM = 0x08;

        [RubyConstant("OS_CPM")]
        public const int OS_CPM = 0x09;

        [RubyConstant("OS_TOPS20")]
        public const int OS_TOPS20 = 0x0a;

        [RubyConstant("OS_WIN32")]
        public const int OS_WIN32 = 0x0b;

        [RubyConstant("OS_QDOS")]
        public const int OS_QDOS = 0x0c;

        [RubyConstant("OS_RISCOS")]
        public const int OS_RISCOS = 0x0d;

        [RubyConstant("OS_UNKNOWN")]
        public const int OS_UNKNOWN = 0xff;

        [RubyConstant("OS_CODE")]
        public const int OS_CODE = OS_UNIX;

        #endregion

        #region Module functions

        // module_function in MRI: private instance methods as well as singleton ones.

        [RubyMethod("zlib_version", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("zlib_version", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ ZlibVersion(object self) {
            return MutableString.CreateAscii(ZNative.Version);
        }

        [RubyMethod("crc32", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("crc32", RubyMethodAttributes.PrivateInstance)]
        public static object GetCrc(ConversionStorage<IntegerValue>/*!*/ integerConversion, object self,
            [Optional, DefaultProtocol]MutableString str, [Optional]object initialCrc) {

            return Checksum(integerConversion, str, initialCrc, false);
        }

        [RubyMethod("adler32", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("adler32", RubyMethodAttributes.PrivateInstance)]
        public static object GetAdler(ConversionStorage<IntegerValue>/*!*/ integerConversion, object self,
            [Optional, DefaultProtocol]MutableString str, [Optional]object initialAdler) {

            return Checksum(integerConversion, str, initialAdler, true);
        }

        private static object Checksum(ConversionStorage<IntegerValue>/*!*/ integerConversion, MutableString str, object initial, bool adler) {
            uint seed;
            if (initial == null || initial is Missing) {
                // With no seed, libz's own initial value is the answer: 1 for adler32, 0 for crc32.
                seed = adler ? 1u : 0u;
            } else {
                seed = Protocols.CastToUInt32Unchecked(integerConversion, initial);
            }

            byte[] bytes = (str != null) ? str.ToByteArray() : null;
            return Protocols.Normalize(adler ? Adler32(seed, bytes) : Crc32(seed, bytes));
        }

        /// <summary>
        /// adler32(seed, bytes) from whichever zlib is in use; with no bytes it is the initial
        /// value whatever the seed, as libz answers for a null buffer.
        /// </summary>
        internal static uint Adler32(uint seed, byte[] bytes) {
            if (ZNative.UsesRuntimeZlib) {
                return (bytes == null) ? 1u : ManagedChecksums.Adler32(seed, bytes, 0, bytes.Length);
            }
            return (uint)LibZ.adler32((UIntPtr)seed, bytes, (bytes != null) ? (uint)bytes.Length : 0);
        }

        internal static uint Crc32(uint seed, byte[] bytes) {
            if (ZNative.UsesRuntimeZlib) {
                return (bytes == null) ? 0u : ManagedChecksums.Crc32(seed, bytes, 0, bytes.Length);
            }
            return (uint)LibZ.crc32((UIntPtr)seed, bytes, (bytes != null) ? (uint)bytes.Length : 0);
        }

        [RubyMethod("crc32_combine", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("crc32_combine", RubyMethodAttributes.PrivateInstance)]
        public static object CrcCombine(ConversionStorage<IntegerValue>/*!*/ integerConversion, object self,
            object crc1, object crc2, [DefaultProtocol]int length) {

            uint a = Protocols.CastToUInt32Unchecked(integerConversion, crc1);
            uint b = Protocols.CastToUInt32Unchecked(integerConversion, crc2);
            return Protocols.Normalize(ZNative.UsesRuntimeZlib
                ? ManagedChecksums.Crc32Combine(a, b, length)
                : (uint)LibZ.crc32_combine((UIntPtr)a, (UIntPtr)b, length));
        }

        [RubyMethod("adler32_combine", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("adler32_combine", RubyMethodAttributes.PrivateInstance)]
        public static object AdlerCombine(ConversionStorage<IntegerValue>/*!*/ integerConversion, object self,
            object adler1, object adler2, [DefaultProtocol]int length) {

            uint a = Protocols.CastToUInt32Unchecked(integerConversion, adler1);
            uint b = Protocols.CastToUInt32Unchecked(integerConversion, adler2);
            return Protocols.Normalize(ZNative.UsesRuntimeZlib
                ? ManagedChecksums.Adler32Combine(a, b, length)
                : (uint)LibZ.adler32_combine((UIntPtr)a, (UIntPtr)b, length));
        }

        [RubyMethod("crc_table", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("crc_table", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ CrcTable(object self) {
            // get_crc_table() hands back 256 z_crc_t (uint32 since zlib 1.2.7) entries.
            var result = new RubyArray(256);
            var entries = new int[256];
            if (ZNative.UsesRuntimeZlib) {
                for (int i = 0; i < entries.Length; i++) {
                    entries[i] = unchecked((int)ManagedChecksums.CrcTable[i]);
                }
            } else {
                Marshal.Copy(LibZ.get_crc_table(), entries, 0, 256);
            }
            for (int i = 0; i < entries.Length; i++) {
                result.Add(Protocols.Normalize(unchecked((uint)entries[i])));
            }
            return result;
        }

        [RubyMethod("deflate", RubyMethodAttributes.PublicSingleton)]
        public static object DeflateString(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ str, [Optional]object level) {

            return Deflate.DeflateString(fixnumCast, block, null, str, level);
        }

        [RubyMethod("inflate", RubyMethodAttributes.PublicSingleton)]
        public static object InflateString(BlockParam block, RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            return Inflate.InflateString(block, null, str);
        }

        [RubyMethod("gzip", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Gzip(ConversionStorage<int>/*!*/ fixnumCast, RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ str, [Optional]object options) {

            RubyContext context = fixnumCast.Context;

            int level = DEFAULT_COMPRESSION;
            int strategy = DEFAULT_STRATEGY;
            var hash = options as IDictionary<object, object>;
            if (hash != null) {
                object value;
                if (hash.TryGetValue(context.CreateAsciiSymbol("level"), out value) && value != null) {
                    level = Protocols.CastToFixnum(fixnumCast, value);
                }
                if (hash.TryGetValue(context.CreateAsciiSymbol("strategy"), out value) && value != null) {
                    strategy = Protocols.CastToFixnum(fixnumCast, value);
                }
            }

            // +16 asks libz to write a gzip wrapper rather than a zlib one.
            using (var z = new Deflate(level, MAX_WBITS + 16, DEF_MEM_LEVEL, strategy)) {
                z.Run(str.ToByteArray(), LibZ.Z_FINISH, null);
                return z.TakeBuffer();
            }
        }

        [RubyMethod("gunzip", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Gunzip(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            using (var z = new Inflate(MAX_WBITS + 16)) {
                z.Run(str.ToByteArray(), LibZ.Z_FINISH, null);
                return z.TakeBuffer();
            }
        }

        #endregion

        #region ZStream class

        [RubyClass("ZStream")]
        public class ZStream : IDisposable {
            /// <summary>
            /// MRI's ZSTREAM_AVAIL_OUT_STEP_MAX: with a block, output is handed over in chunks
            /// of exactly this size, and a shorter tail is held back until the stream ends.
            /// </summary>
            internal const int ChunkSize = 16384;

            /// <summary>The native stream - libz's z_stream, or the .NET runtime's (ZlibBackends.cs).</summary>
            private ZNative _z;

            internal readonly bool _isInflate;
            private bool _ended;
            private bool _finished;
            private int _availOut;

            /// <summary>Input libz has not consumed yet - MRI's z->input.</summary>
            private readonly List<byte>/*!*/ _input = new List<byte>();

            /// <summary>Output produced but not handed to Ruby yet - MRI's z->buf.</summary>
            private readonly List<byte>/*!*/ _output = new List<byte>();

            /// <summary>
            /// Serializes every use of the native z_stream. MRI runs a stream under the GVL, so two
            /// Ruby threads never touch one z_stream at once there; IronRuby threads really are
            /// concurrent, and two of them in deflate() on one stream corrupt libz's state and the
            /// native heap (test_multithread_deflate killed the process with SIGSEGV), or one
            /// thread's run points next_in at a buffer the other has already unpinned.
            /// Reentrant, because a block yielded to mid-run may call back into the stream; the
            /// in-progress flag turns that into MRI's InProgressError instead.
            /// </summary>
            private readonly object/*!*/ _lock = new object();
            private bool _inProgress;

            /// <summary>MRI wraps #deflate and #inflate in a per-stream Mutex; this is its owner.</summary>
            private readonly object/*!*/ _methodLock = new object();
            private Thread _methodOwner;

            internal ZStream(bool isInflate) {
                _isInflate = isInflate;
                _z = ZNative.Create();
            }

            ~ZStream() {
                Dispose(false);
            }

            public void Dispose() {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing) {
                lock (_lock) {
                    DisposeNoLock();
                }
            }

            private void DisposeNoLock() {
                if (_z != null) {
                    if (!_ended) {
                        _z.End(_isInflate);
                        _ended = true;
                    }
                    _z.Free();
                    _z = null;
                }
            }

            /// <summary>
            /// The native stream for reading what z_stream records (totals, adler, msg); it stays
            /// readable after #end, as the struct did, until the stream is disposed.
            /// </summary>
            internal ZNative Rec {
                get {
                    lock (_lock) {
                        if (_z == null) {
                            throw new Error("stream is not ready");
                        }
                        return _z;
                    }
                }
            }

            /// <summary>
            /// Runs a native call on the stream with the stream lock held, so that it cannot race
            /// a run, an #end or a #reset on another thread.
            /// </summary>
            internal int Native(Func<ZNative, int>/*!*/ call) {
                lock (_lock) {
                    return call(Handle);
                }
            }

            /// <summary>
            /// MRI's rb_mutex_synchronize(z->mutex, ...) around #deflate and #inflate: another
            /// thread waits its turn, and the owning thread coming back in - from the block it was
            /// given - is a recursive lock, which MRI reports as a ThreadError.
            /// </summary>
            internal T Synchronize<T>(Func<T>/*!*/ body) {
                if (_methodOwner == Thread.CurrentThread) {
                    throw new ThreadError("deadlock; recursive locking");
                }
                lock (_methodLock) {
                    _methodOwner = Thread.CurrentThread;
                    try {
                        return body();
                    } finally {
                        _methodOwner = null;
                    }
                }
            }

            internal ZNative Handle {
                get {
                    if (_z == null || _ended) {
                        throw new Error("stream is not ready");
                    }
                    return _z;
                }
            }

            internal bool IsFinished {
                get { return _finished; }
                set { _finished = value; }
            }

            internal bool IsEnded {
                get { return _ended; }
            }

            internal List<byte>/*!*/ Output {
                get { return _output; }
            }

            internal int PendingInput {
                get { return _input.Count; }
            }

            internal void AppendInput(byte[]/*!*/ bytes) {
                _input.AddRange(bytes);
            }

            #region running libz

            /// <summary>
            /// MRI's zstream_run. Feeds <paramref name="data"/> plus anything libz left over last
            /// time through deflate()/inflate(), yielding whole <see cref="ChunkSize"/> chunks of
            /// output to the block as they fill.
            ///
            /// Returns the result the block broke with, or null. <paramref name="broke"/> says which.
            /// </summary>
            internal object Run(byte[]/*!*/ data, int flush, BlockParam block, out bool broke) {
                lock (_lock) {
                    if (_inProgress) {
                        throw new InProgressError("zlib stream is in progress");
                    }
                    _inProgress = true;
                    try {
                        return RunNoLock(data, flush, block, out broke);
                    } finally {
                        _inProgress = false;
                    }
                }
            }

            private object RunNoLock(byte[]/*!*/ data, int flush, BlockParam block, out bool broke) {
                broke = false;
                object blockResult = null;

                byte[] pending;
                if (_input.Count > 0) {
                    pending = new byte[_input.Count + data.Length];
                    _input.CopyTo(pending, 0);
                    Array.Copy(data, 0, pending, _input.Count, data.Length);
                    _input.Clear();
                } else {
                    pending = data;
                }

                var scratch = new byte[ChunkSize];
                GCHandle inHandle = GCHandle.Alloc(pending, GCHandleType.Pinned);
                GCHandle outHandle = GCHandle.Alloc(scratch, GCHandleType.Pinned);
                int err = LibZ.Z_OK;
                int consumed = 0;
                try {
                    IntPtr inBase = inHandle.AddrOfPinnedObject();
                    IntPtr outBase = outHandle.AddrOfPinnedObject();

                    while (true) {
                        // Fetched on every pass: the block yielded to below may have closed
                        // the stream, and libz must not be handed an ended z_stream.
                        ZNative handle = Handle;
                        uint availIn, availOut;
                        err = handle.Process(_isInflate, inBase + consumed, (uint)(pending.Length - consumed),
                            outBase, (uint)ChunkSize, flush, out availIn, out availOut);

                        int produced = ChunkSize - (int)availOut;
                        for (int i = 0; i < produced; i++) {
                            _output.Add(scratch[i]);
                        }
                        consumed = pending.Length - (int)availIn;
                        _availOut = (int)availOut;

                        if (block != null && YieldChunks(block, ref blockResult)) {
                            broke = true;
                            break;
                        }

                        if (err == LibZ.Z_STREAM_END) {
                            _finished = true;
                            break;
                        }
                        if (err != LibZ.Z_OK && err != LibZ.Z_BUF_ERROR) {
                            break;
                        }
                        if (availOut > 0) {
                            break;
                        }
                        if (availIn == 0 && _isInflate) {
                            // inflate() answers Z_BUF_ERROR once it is out of input; deflate()
                            // can still have output pending in its own state, so only inflate
                            // gets to stop here.
                            break;
                        }
                    }
                } finally {
                    if (_z != null) {
                        _z.ClearPointers();
                    }
                    inHandle.Free();
                    outHandle.Free();
                }

                for (int i = consumed; i < pending.Length; i++) {
                    _input.Add(pending[i]);
                }

                if (broke) {
                    return blockResult;
                }

                if (err != LibZ.Z_OK && err != LibZ.Z_STREAM_END) {
                    throw MakeError(err, Rec.Message);
                }

                return null;
            }

            /// <summary>Convenience for the callers that have no block and want exceptions only.</summary>
            internal void Run(byte[]/*!*/ data, int flush, BlockParam block) {
                bool broke;
                Run(data, flush, block, out broke);
            }

            /// <summary>
            /// MRI's zstream_passthrough_input: once the stream has ended, whatever input libz
            /// did not take is not compressed data any more, so it becomes output verbatim.
            /// </summary>
            internal void PassthroughInput() {
                _output.AddRange(_input);
                _input.Clear();
            }

            /// <summary>
            /// deflateParams(), which takes no input but may flush what the old level buffered.
            /// </summary>
            internal int RunParams(int level, int strategy) {
                lock (_lock) {
                    if (_inProgress) {
                        throw new InProgressError("zlib stream is in progress");
                    }
                    return RunParamsNoLock(level, strategy);
                }
            }

            private int RunParamsNoLock(int level, int strategy) {
                var scratch = new byte[ChunkSize];
                GCHandle outHandle = GCHandle.Alloc(scratch, GCHandleType.Pinned);
                ZNative handle = Handle;
                int err;
                try {
                    IntPtr outBase = outHandle.AddrOfPinnedObject();
                    while (true) {
                        uint availOut;
                        err = handle.Params(level, strategy, outBase, (uint)ChunkSize, out availOut);

                        int produced = ChunkSize - (int)availOut;
                        for (int i = 0; i < produced; i++) {
                            _output.Add(scratch[i]);
                        }
                        if (err != LibZ.Z_BUF_ERROR) {
                            break;
                        }
                    }
                } finally {
                    handle.ClearPointers();
                    outHandle.Free();
                }
                return err;
            }

            private bool YieldChunks(BlockParam/*!*/ block, ref object blockResult) {
                while (_output.Count >= ChunkSize) {
                    var chunk = MutableString.CreateBinary(_output.GetRange(0, ChunkSize), RubyEncoding.Binary);
                    _output.RemoveRange(0, ChunkSize);
                    if (block.Yield(chunk, out blockResult)) {
                        return true;
                    }
                }
                return false;
            }

            internal MutableString/*!*/ TakeBuffer() {
                var result = MutableString.CreateBinary(_output, RubyEncoding.Binary);
                _output.Clear();
                _availOut = 0;
                return result;
            }

            internal void AppendToBuffer(byte[]/*!*/ bytes) {
                _output.AddRange(bytes);
            }

            /// <summary>
            /// MRI's zstream_detach_buffer. With a block, a partial buffer is deliberately held
            /// back ("prevent tiny yields mid-stream") until the stream has ended.
            /// </summary>
            internal object DetachBuffer(BlockParam block, out bool broke) {
                broke = false;
                if (block != null && !_finished) {
                    return null;
                }

                var str = TakeBuffer();
                if (block == null) {
                    return str;
                }

                object blockResult;
                if (block.Yield(str, out blockResult)) {
                    broke = true;
                    return blockResult;
                }
                return null;
            }

            internal void End() {
                lock (_lock) {
                    if (_z == null || _ended) {
                        return;
                    }
                    _z.End(_isInflate);
                    _ended = true;
                }
            }

            internal void ResetStream() {
                lock (_lock) {
                    if (_inProgress) {
                        throw new InProgressError("zlib stream is in progress");
                    }
                    ResetStreamNoLock();
                }
            }

            private void ResetStreamNoLock() {
                int err = Handle.Reset(_isInflate);
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, Rec.Message);
                }
                _input.Clear();
                _output.Clear();
                _finished = false;
                _availOut = 0;
            }

            internal MutableString/*!*/ TakeInput() {
                var result = MutableString.CreateBinary(_input, RubyEncoding.Binary);
                _input.Clear();
                return result;
            }

            #endregion

            #region instance methods

            [RubyMethod("adler")]
            public static object Adler(ZStream/*!*/ self) {
                return Protocols.Normalize(self.Rec.Adler);
            }

            [RubyMethod("avail_in")]
            public static int AvailIn(ZStream/*!*/ self) {
                return self._input.Count;
            }

            [RubyMethod("avail_out")]
            public static int GetAvailOut(ZStream/*!*/ self) {
                return self._availOut;
            }

            [RubyMethod("avail_out=")]
            public static object SetAvailOut(ZStream/*!*/ self, [DefaultProtocol]int size) {
                self._availOut = size;
                return size;
            }

            [RubyMethod("finish")]
            public static object Finish(BlockParam block, ZStream/*!*/ self) {
                if (!self._finished) {
                    bool broke;
                    object result = self.Run(Utils.EmptyBytes, LibZ.Z_FINISH, block, out broke);
                    if (broke) {
                        return result;
                    }
                }

                bool detachBroke;
                return self.DetachBuffer(block, out detachBroke);
            }

            [RubyMethod("close")]
            [RubyMethod("end")]
            public static object Close(ZStream/*!*/ self) {
                self.End();
                return null;
            }

            [RubyMethod("stream_end?")]
            [RubyMethod("finished?")]
            public static bool IsFinishedP(ZStream/*!*/ self) {
                return self._finished;
            }

            [RubyMethod("closed?")]
            [RubyMethod("ended?")]
            public static bool IsClosed(ZStream/*!*/ self) {
                return self._ended;
            }

            [RubyMethod("data_type")]
            public static int DataType(ZStream/*!*/ self) {
                return self.Rec.DataType;
            }

            [RubyMethod("flush_next_in")]
            public static MutableString/*!*/ FlushNextIn(ZStream/*!*/ self) {
                return self.TakeInput();
            }

            [RubyMethod("flush_next_out")]
            public static object FlushNextOut(BlockParam block, ZStream/*!*/ self) {
                bool broke;
                return self.DetachBuffer(block, out broke);
            }

            [RubyMethod("reset")]
            public static object Reset(ZStream/*!*/ self) {
                self.ResetStream();
                return null;
            }

            [RubyMethod("total_in")]
            public static object TotalIn(ZStream/*!*/ self) {
                return Protocols.Normalize(self.Rec.TotalIn);
            }

            [RubyMethod("total_out")]
            public static object TotalOut(ZStream/*!*/ self) {
                return Protocols.Normalize(self.Rec.TotalOut);
            }

            #endregion
        }

        #endregion

        #region argument helpers

        internal static int OptionalInt(ConversionStorage<int>/*!*/ fixnumCast, object value, int defaultValue) {
            if (value == null || value is Missing) {
                return defaultValue;
            }
            return Protocols.CastToFixnum(fixnumCast, value);
        }

        internal static byte[]/*!*/ Bytes(MutableString str) {
            return (str != null) ? str.ToByteArray() : Utils.EmptyBytes;
        }

        internal static Exception/*!*/ MakeError(int err, string message) {
            switch (err) {
                case LibZ.Z_STREAM_END: return new StreamEnd(message ?? "stream end");
                case LibZ.Z_NEED_DICT: return new NeedDict(message ?? "need dictionary");
                case LibZ.Z_STREAM_ERROR: return new StreamError(message ?? "stream error");
                case LibZ.Z_DATA_ERROR: return new DataError(message ?? "data error");
                case LibZ.Z_BUF_ERROR: return new BufError(message ?? "buffer error");
                case LibZ.Z_VERSION_ERROR: return new VersionError(message ?? "incompatible version");
                case LibZ.Z_MEM_ERROR: return new MemError(message ?? "insufficient memory");
                default: return new Error(message ?? ("unknown zlib error " + err));
            }
        }

        #endregion

        #region Inflate class

        [RubyClass("Inflate")]
        public class Inflate : ZStream {
            /// <summary>Dictionaries registered with #add_dictionary, keyed by their adler32.</summary>
            private Dictionary<uint, byte[]> _dictionaries;

            internal Inflate(int windowBits)
                : base(true) {

                int err = Handle.InitInflate(windowBits);
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, Rec.Message);
                }
            }

            [RubyConstructor]
            public static Inflate/*!*/ Create(ConversionStorage<int>/*!*/ fixnumCast, RubyClass/*!*/ self, [Optional]object windowBits) {
                return new Inflate(OptionalInt(fixnumCast, windowBits, MAX_WBITS));
            }

            /// <summary>MRI's do_inflate: nil means "no more input", and empty input is skipped
            /// so that inflate() cannot answer Z_BUF_ERROR for it.</summary>
            private object DoInflate(MutableString src, BlockParam block, out bool broke) {
                broke = false;
                if (src == null) {
                    return RunGuarded(Utils.EmptyBytes, LibZ.Z_FINISH, block, out broke);
                }

                byte[] bytes = src.ToByteArray();
                if (bytes.Length > 0 || PendingInput > 0) {
                    return RunGuarded(bytes, LibZ.Z_SYNC_FLUSH, block, out broke);
                }
                return null;
            }

            /// <summary>
            /// Runs, and on Z_NEED_DICT retries once with a dictionary registered by #add_dictionary,
            /// as MRI does before it gives up and raises.
            /// </summary>
            private object RunGuarded(byte[]/*!*/ data, int flush, BlockParam block, out bool broke) {
                try {
                    return Run(data, flush, block, out broke);
                } catch (NeedDict) {
                    byte[] dictionary;
                    if (_dictionaries == null || !_dictionaries.TryGetValue(Rec.Adler, out dictionary)) {
                        throw;
                    }
                    SetDictionary(dictionary);
                    return Run(Utils.EmptyBytes, flush, block, out broke);
                }
            }

            internal void SetDictionary(byte[]/*!*/ dictionary) {
                int err = Native(h => h.SetDictionary(true, dictionary));
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, Rec.Message);
                }
            }

            [RubyMethod("inflate")]
            public static object InflateMethod(BlockParam block, Inflate/*!*/ self, [DefaultProtocol]MutableString src) {
                return self.Synchronize(() => self.InflateBody(block, src));
            }

            private object InflateBody(BlockParam block, MutableString src) {
                Inflate self = this;
                bool broke;

                if (self.IsFinished) {
                    // Past the end of the stream everything is passed straight through.
                    if (src == null) {
                        return self.DetachBuffer(block, out broke);
                    }
                    self.AppendToBuffer(src.ToByteArray());
                    return MutableString.CreateBinary();
                }

                object result = self.DoInflate(src, block, out broke);
                if (broke) {
                    return result;
                }
                object dst = self.DetachBuffer(block, out broke);
                if (self.IsFinished) {
                    self.PassthroughInput();
                }
                return dst;
            }

            [RubyMethod("<<")]
            public static object Append(BlockParam block, Inflate/*!*/ self, [DefaultProtocol]MutableString src) {
                if (self.IsFinished) {
                    if (src != null) {
                        self.AppendToBuffer(src.ToByteArray());
                    }
                    return self;
                }

                bool broke;
                object result = self.DoInflate(src, block, out broke);
                if (broke) {
                    return result;
                }
                if (self.IsFinished) {
                    self.PassthroughInput();
                }
                return self;
            }

            [RubyMethod("set_dictionary")]
            public static MutableString/*!*/ SetDictionary(Inflate/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ dictionary) {
                self.SetDictionary(dictionary.ToByteArray());
                return dictionary;
            }

            [RubyMethod("add_dictionary")]
            public static MutableString/*!*/ AddDictionary(Inflate/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ dictionary) {
                byte[] bytes = dictionary.ToByteArray();
                if (self._dictionaries == null) {
                    self._dictionaries = new Dictionary<uint, byte[]>();
                }
                self._dictionaries[Adler32(Adler32(0, null), bytes)] = bytes;
                return dictionary;
            }

            [RubyMethod("sync_point?")]
            public static bool IsSyncPoint(Inflate/*!*/ self) {
                int err = self.Native(h => h.SyncPoint());
                if (err == 1) {
                    return true;
                }
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, self.Rec.Message);
                }
                return false;
            }

            [RubyMethod("sync")]
            public static bool Sync(Inflate/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ src) {
                // MRI hunts for the next full-flush point by feeding the data in; without
                // inflateSync's partial-input contract the honest answer is "not found".
                self.AppendInput(src.ToByteArray());
                return false;
            }

            [RubyMethod("inflate", RubyMethodAttributes.PublicSingleton)]
            public static object InflateString(BlockParam block, RubyClass self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
                using (var z = new Inflate(MAX_WBITS)) {
                    bool broke;
                    object result = z.Run(str.ToByteArray(), LibZ.Z_SYNC_FLUSH, block, out broke);
                    if (broke) {
                        return result;
                    }
                    return z.DetachBuffer(block, out broke);
                }
            }
        }

        #endregion

        #region Deflate class

        [RubyClass("Deflate")]
        public class Deflate : ZStream {

            internal Deflate(int level, int windowBits, int memLevel, int strategy)
                : base(false) {

                int err = Handle.InitDeflate(level, windowBits, memLevel, strategy);
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, Rec.Message);
                }
            }

            [RubyConstructor]
            public static Deflate/*!*/ Create(ConversionStorage<int>/*!*/ fixnumCast, RubyClass/*!*/ self,
                [Optional]object level, [Optional]object windowBits, [Optional]object memLevel, [Optional]object strategy) {

                return new Deflate(
                    OptionalInt(fixnumCast, level, DEFAULT_COMPRESSION),
                    OptionalInt(fixnumCast, windowBits, MAX_WBITS),
                    OptionalInt(fixnumCast, memLevel, DEF_MEM_LEVEL),
                    OptionalInt(fixnumCast, strategy, DEFAULT_STRATEGY)
                );
            }

            /// <summary>MRI's do_deflate: nil finishes the stream, and an empty Z_NO_FLUSH run is
            /// skipped so that deflate() cannot answer Z_BUF_ERROR for it.</summary>
            private object DoDeflate(MutableString src, int flush, BlockParam block, out bool broke) {
                broke = false;
                if (src == null) {
                    return Run(Utils.EmptyBytes, LibZ.Z_FINISH, block, out broke);
                }

                byte[] bytes = src.ToByteArray();
                if (flush != LibZ.Z_NO_FLUSH || bytes.Length > 0) {
                    return Run(bytes, flush, block, out broke);
                }
                return null;
            }

            [RubyMethod("deflate")]
            public static object DeflateMethod(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, Deflate/*!*/ self,
                [DefaultProtocol]MutableString src, [Optional]object flush) {

                int f = OptionalInt(fixnumCast, flush, LibZ.Z_NO_FLUSH);
                return self.Synchronize(() => {
                    bool broke;
                    object result = self.DoDeflate(src, f, block, out broke);
                    if (broke) {
                        return result;
                    }
                    return self.DetachBuffer(block, out broke);
                });
            }

            [RubyMethod("<<")]
            public static object Append(BlockParam block, Deflate/*!*/ self, [DefaultProtocol]MutableString src) {
                bool broke;
                object result = self.DoDeflate(src, LibZ.Z_NO_FLUSH, block, out broke);
                return broke ? result : (object)self;
            }

            [RubyMethod("flush")]
            public static object Flush(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, Deflate/*!*/ self, [Optional]object flush) {
                int f = OptionalInt(fixnumCast, flush, SYNC_FLUSH);
                bool broke;
                if (f != LibZ.Z_NO_FLUSH) {
                    object result = self.Run(Utils.EmptyBytes, f, block, out broke);
                    if (broke) {
                        return result;
                    }
                }
                return self.DetachBuffer(block, out broke);
            }

            [RubyMethod("params")]
            public static object Params(ConversionStorage<int>/*!*/ fixnumCast, Deflate/*!*/ self, object level, object strategy) {
                int newLevel = OptionalInt(fixnumCast, level, DEFAULT_COMPRESSION);
                int newStrategy = OptionalInt(fixnumCast, strategy, DEFAULT_STRATEGY);

                // Changing the level mid-stream makes libz flush what it has buffered, so this
                // needs an output buffer just as much as deflate() does.
                int err = self.RunParams(newLevel, newStrategy);

                if (err != LibZ.Z_OK) {
                    throw MakeError(err, self.Rec.Message);
                }
                return null;
            }

            [RubyMethod("set_dictionary")]
            public static MutableString/*!*/ SetDictionary(Deflate/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ dictionary) {
                byte[] bytes = dictionary.ToByteArray();
                int err = self.Native(h => h.SetDictionary(false, bytes));
                if (err != LibZ.Z_OK) {
                    throw MakeError(err, self.Rec.Message);
                }
                return dictionary;
            }

            [RubyMethod("deflate", RubyMethodAttributes.PublicSingleton)]
            public static object DeflateString(ConversionStorage<int>/*!*/ fixnumCast, BlockParam block, RubyClass self,
                [DefaultProtocol, NotNull]MutableString/*!*/ str, [Optional]object level) {

                using (var z = new Deflate(OptionalInt(fixnumCast, level, DEFAULT_COMPRESSION), MAX_WBITS, DEF_MEM_LEVEL, DEFAULT_STRATEGY)) {
                    bool broke;
                    object result = z.Run(str.ToByteArray(), LibZ.Z_FINISH, block, out broke);
                    if (broke) {
                        return result;
                    }
                    return z.DetachBuffer(block, out broke);
                }
            }
        }

        #endregion

        #region Exceptions

        [RubyException("Error"), Serializable]
        public class Error : SystemException {
            public Error() : this(null, null) { }
            public Error(string message) : this(message, null) { }
            public Error(string message, Exception inner) : base(message ?? "Error", inner) { }

            protected Error(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
        }

        [RubyException("StreamEnd"), Serializable]
        public class StreamEnd : Error {
            public StreamEnd() : this(null, null) { }
            public StreamEnd(string message) : this(message, null) { }
            public StreamEnd(string message, Exception inner) : base(message ?? "StreamEnd", inner) { }

            protected StreamEnd(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
        }

        [RubyException("NeedDict"), Serializable]
        public class NeedDict : Error {
            public NeedDict() : this(null, null) { }
            public NeedDict(string message) : this(message, null) { }
            public NeedDict(string message, Exception inner) : base(message ?? "NeedDict", inner) { }

            protected NeedDict(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
        }

        [RubyException("DataError"), Serializable]
        public class DataError : Error {
            public DataError() : this(null, null) { }
            public DataError(string message) : this(message, null) { }
            public DataError(string message, Exception inner) : base(message ?? "DataError", inner) { }

            protected DataError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
        }

        [RubyException("BufError"), Serializable]
        public class BufError : Error {
            public BufError() : this(null, null) { }
            public BufError(string message) : this(message, null) { }
            public BufError(string message, Exception inner) : base(message ?? "BufError", inner) { }

            protected BufError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
        }

        [RubyException("MemError"), Serializable]
        public class MemError : Error {
            public MemError() : this(null, null) { }
            public MemError(string message) : this(message, null) { }
            public MemError(string message, Exception inner) : base(message ?? "MemError", inner) { }

            protected MemError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
        }

        [RubyException("VersionError"), Serializable]
        public class VersionError : Error {
            public VersionError() : this(null, null) { }
            public VersionError(string message) : this(message, null) { }
            public VersionError(string message, Exception inner) : base(message ?? "VersionError", inner) { }

            protected VersionError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
        }

        [RubyException("InProgressError"), Serializable]
        public class InProgressError : Error {
            public InProgressError() : this(null, null) { }
            public InProgressError(string message) : this(message, null) { }
            public InProgressError(string message, Exception inner) : base(message ?? "InProgressError", inner) { }

            protected InProgressError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
        }

        [RubyException("StreamError"), Serializable]
        public class StreamError : Error {
            public StreamError() : this(null, null) { }
            public StreamError(string message) : this(message, null) { }
            public StreamError(string message, Exception inner) : base(message ?? "StreamError", inner) { }

            protected StreamError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
        }

        #endregion

    }
}
