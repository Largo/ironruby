/* ****************************************************************************
 *
 * The two zlibs Zlib::ZStream can run on.
 *
 * Where there is a system libz (every Unix) the stream is libz's own z_stream,
 * driven through LibZ - byte-for-byte the output CRuby produces, and the whole
 * API: dictionaries, deflateParams, inflateSyncPoint.
 *
 * Windows has no libz to bind to.  What it does have, in every .NET install, is
 * the zlib .NET itself compresses with: System.IO.Compression.Native, whose
 * CompressionNative_* exports wrap deflateInit2/deflate/deflateEnd and
 * inflateInit2/inflate/inflateEnd around a small PAL_ZStream (the pointers and
 * counts of one call; the z_stream stays inside).  That is enough to inflate and
 * deflate anything - gzip, zlib or raw, any level, window, memory level,
 * strategy and flush mode - which is what gem install, net/http, the gzip classes
 * and practically all Ruby code need.  What the PAL does not export is kept here
 * in managed code (the running totals and the adler32/crc32 check value, which
 * libz keeps in z_stream) or refused with NotImplementedError (preset
 * dictionaries, changing the level mid-stream, inflateSyncPoint).  The
 * compressor is the one the runtime ships - zlib-intel in .NET 8, zlib-ng in 9
 * and later - so compressed bytes can differ from CRuby's, as they differ
 * between any two zlib builds; decompression is exact.
 *
 * IRONRUBY_ZLIB=dotnet selects the runtime's zlib on Unix too, which is how this
 * half is tested away from Windows.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;
using IronRuby.Builtins;

namespace IronRuby.StandardLibrary.Zlib {

    /// <summary>One native compression stream, whichever zlib is behind it.</summary>
    internal abstract class ZNative {

        /// <summary>
        /// Whether the zlib inside the .NET runtime is the one in use: on Windows, where there
        /// is no libz, or when IRONRUBY_ZLIB=dotnet asks for it.
        /// </summary>
        internal static readonly bool UsesRuntimeZlib =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            String.Equals(Environment.GetEnvironmentVariable("IRONRUBY_ZLIB"), "dotnet", StringComparison.OrdinalIgnoreCase);

        internal static ZNative/*!*/ Create() {
            return UsesRuntimeZlib ? (ZNative)new RuntimeZNative() : new LibZNative();
        }

        /// <summary>The zlib version to report as ZLIB_VERSION / Zlib.zlib_version.</summary>
        internal static string/*!*/ Version {
            get {
                if (!UsesRuntimeZlib) {
                    return LibZ.Version;
                }
                // The runtime does not export zlibVersion(); these are the versions it builds
                // (zlib-intel's 1.2.13 base in .NET 8, zlib-ng's 1.3.1 compatibility in 9+).
                return Environment.Version.Major >= 9 ? "1.3.1" : "1.2.13";
            }
        }

        internal abstract int InitInflate(int windowBits);
        internal abstract int InitDeflate(int level, int windowBits, int memLevel, int strategy);

        /// <summary>
        /// One inflate() or deflate() over the given buffers. Reports how much of each is left,
        /// as z_stream's avail_in and avail_out would.
        /// </summary>
        internal abstract int Process(bool inflate, IntPtr nextIn, uint availIn, IntPtr nextOut, uint availOut, int flush,
            out uint availInLeft, out uint availOutLeft);

        internal abstract int End(bool inflate);
        internal abstract int Reset(bool inflate);
        internal abstract int SetDictionary(bool inflate, byte[]/*!*/ dictionary);

        /// <summary>deflateParams(), which may flush into the output buffer.</summary>
        internal abstract int Params(int level, int strategy, IntPtr nextOut, uint availOut, out uint availOutLeft);

        internal abstract int SyncPoint();

        /// <summary>Forgets the buffer pointers of the last call; they are unpinned now.</summary>
        internal abstract void ClearPointers();

        /// <summary>Releases what the stream holds besides libz's own state (End releases that).</summary>
        internal abstract void Free();

        internal abstract ulong TotalIn { get; }
        internal abstract ulong TotalOut { get; }
        internal abstract uint Adler { get; }
        internal abstract int DataType { get; }
        internal abstract string Message { get; }
    }

    #region libz

    /// <summary>libz's own z_stream, unmanaged because libz keeps a pointer to it in its state.</summary>
    internal sealed class LibZNative : ZNative {
        private IntPtr _z;

        internal LibZNative() {
            _z = Marshal.AllocHGlobal(LibZ.StreamSize);
            for (int i = 0; i < LibZ.StreamSize; i++) {
                Marshal.WriteByte(_z, i, 0);
            }
        }

        private LibZ.ZStreamRec Rec {
            get { return (LibZ.ZStreamRec)Marshal.PtrToStructure(_z, typeof(LibZ.ZStreamRec)); }
            set { Marshal.StructureToPtr(value, _z, false); }
        }

        internal override int InitInflate(int windowBits) {
            return LibZ.inflateInit2_(_z, windowBits, LibZ.Version, LibZ.StreamSize);
        }

        internal override int InitDeflate(int level, int windowBits, int memLevel, int strategy) {
            return LibZ.deflateInit2_(_z, level, LibZ.Z_DEFLATED, windowBits, memLevel, strategy, LibZ.Version, LibZ.StreamSize);
        }

        internal override int Process(bool inflate, IntPtr nextIn, uint availIn, IntPtr nextOut, uint availOut, int flush,
            out uint availInLeft, out uint availOutLeft) {

            var z = Rec;
            z.next_in = nextIn;
            z.avail_in = availIn;
            z.next_out = nextOut;
            z.avail_out = availOut;
            Rec = z;

            int err = inflate ? LibZ.inflate(_z, flush) : LibZ.deflate(_z, flush);

            z = Rec;
            availInLeft = z.avail_in;
            availOutLeft = z.avail_out;
            return err;
        }

        internal override int End(bool inflate) {
            return inflate ? LibZ.inflateEnd(_z) : LibZ.deflateEnd(_z);
        }

        internal override int Reset(bool inflate) {
            return inflate ? LibZ.inflateReset(_z) : LibZ.deflateReset(_z);
        }

        internal override int SetDictionary(bool inflate, byte[]/*!*/ dictionary) {
            return inflate
                ? LibZ.inflateSetDictionary(_z, dictionary, (uint)dictionary.Length)
                : LibZ.deflateSetDictionary(_z, dictionary, (uint)dictionary.Length);
        }

        internal override int Params(int level, int strategy, IntPtr nextOut, uint availOut, out uint availOutLeft) {
            var z = Rec;
            z.next_in = IntPtr.Zero;
            z.avail_in = 0;
            z.next_out = nextOut;
            z.avail_out = availOut;
            Rec = z;

            int err = LibZ.deflateParams(_z, level, strategy);

            availOutLeft = Rec.avail_out;
            return err;
        }

        internal override int SyncPoint() {
            return LibZ.inflateSyncPoint(_z);
        }

        internal override void ClearPointers() {
            // libz remembers next_in/next_out across calls, and deflateParams() in particular
            // will happily write through a stale next_out.
            var z = Rec;
            z.next_in = IntPtr.Zero;
            z.avail_in = 0;
            z.next_out = IntPtr.Zero;
            z.avail_out = 0;
            Rec = z;
        }

        internal override void Free() {
            if (_z != IntPtr.Zero) {
                Marshal.FreeHGlobal(_z);
                _z = IntPtr.Zero;
            }
        }

        internal override ulong TotalIn { get { return (ulong)Rec.total_in; } }
        internal override ulong TotalOut { get { return (ulong)Rec.total_out; } }
        internal override uint Adler { get { return (uint)Rec.adler; } }
        internal override int DataType { get { return Rec.data_type; } }

        internal override string Message {
            get {
                IntPtr msg = Rec.msg;
                return (msg != IntPtr.Zero) ? Marshal.PtrToStringAnsi(msg) : null;
            }
        }
    }

    #endregion

    #region the runtime's zlib

    /// <summary>
    /// A stream on System.IO.Compression.Native. The PAL keeps the z_stream to itself, so the
    /// totals and the check value z_stream would have are kept here, from what each call
    /// consumed and produced - libz's own bookkeeping, done over the same bytes.
    /// </summary>
    internal sealed class RuntimeZNative : ZNative {
        private const string Lib = "System.IO.Compression.Native";

        // pal_zlib.h's PAL_ZStream; the same struct System.IO.Compression's ZLibNative.ZStream is.
        [StructLayout(LayoutKind.Sequential)]
        private struct PalZStream {
            public IntPtr nextIn;
            public IntPtr nextOut;
            public IntPtr msg;
            public IntPtr internalState;
            public uint availIn;
            public uint availOut;
        }

        [DllImport(Lib, EntryPoint = "CompressionNative_DeflateInit2_")]
        private static extern int DeflateInit2_(ref PalZStream stream, int level, int method, int windowBits, int memLevel, int strategy);

        [DllImport(Lib, EntryPoint = "CompressionNative_Deflate")]
        private static extern int Deflate(ref PalZStream stream, int flush);

        [DllImport(Lib, EntryPoint = "CompressionNative_DeflateEnd")]
        private static extern int DeflateEnd(ref PalZStream stream);

        [DllImport(Lib, EntryPoint = "CompressionNative_InflateInit2_")]
        private static extern int InflateInit2_(ref PalZStream stream, int windowBits);

        [DllImport(Lib, EntryPoint = "CompressionNative_Inflate")]
        private static extern int Inflate(ref PalZStream stream, int flush);

        [DllImport(Lib, EntryPoint = "CompressionNative_InflateEnd")]
        private static extern int InflateEnd(ref PalZStream stream);

        // What kind of check value the stream keeps: none (raw deflate), adler32 (zlib) or
        // crc32 (gzip); Auto is an inflate that decides from the first byte (windowBits 32+).
        private enum Wrap { Raw, Zlib, Gzip, Auto }

        private PalZStream _s;
        private bool _inflate;
        private int _level, _windowBits, _memLevel, _strategy;
        private Wrap _wrap;
        private ulong _totalIn, _totalOut;
        private uint _check;
        // The first bytes of a zlib stream: where the preset dictionary's id is, when inflate()
        // asks for one - libz reports it in adler.
        private readonly byte[] _head = new byte[6];
        private int _headLength;
        private byte[] _copy = new byte[0];

        internal override int InitInflate(int windowBits) {
            _inflate = true;
            _windowBits = windowBits;
            _wrap = (windowBits < 0) ? Wrap.Raw : (windowBits >= 32) ? Wrap.Auto : (windowBits >= 16) ? Wrap.Gzip : Wrap.Zlib;
            ResetBookkeeping();
            return InflateInit2_(ref _s, windowBits);
        }

        internal override int InitDeflate(int level, int windowBits, int memLevel, int strategy) {
            _inflate = false;
            _level = level;
            _windowBits = windowBits;
            _memLevel = memLevel;
            _strategy = strategy;
            _wrap = (windowBits < 0) ? Wrap.Raw : (windowBits >= 16) ? Wrap.Gzip : Wrap.Zlib;
            ResetBookkeeping();
            return DeflateInit2_(ref _s, level, LibZ.Z_DEFLATED, windowBits, memLevel, strategy);
        }

        private void ResetBookkeeping() {
            _totalIn = _totalOut = 0;
            _headLength = 0;
            // What z_stream.adler starts as: deflate's is the empty check value of its wrapper
            // (1 for adler32 and for raw, 0 for crc32); inflate's is state->wrap & 1 - 1 for
            // zlib, 0 for gzip - and a raw inflate's is never set, so the zeroed struct's 0.
            if (_inflate) {
                _check = (_wrap == Wrap.Zlib || _wrap == Wrap.Auto) ? 1u : 0u;
            } else {
                _check = (_wrap == Wrap.Gzip) ? 0u : 1u;
            }
        }

        internal override int Process(bool inflate, IntPtr nextIn, uint availIn, IntPtr nextOut, uint availOut, int flush,
            out uint availInLeft, out uint availOutLeft) {

            _s.nextIn = nextIn;
            _s.availIn = availIn;
            _s.nextOut = nextOut;
            _s.availOut = availOut;

            int err = inflate ? Inflate(ref _s, flush) : Deflate(ref _s, flush);

            availInLeft = _s.availIn;
            availOutLeft = _s.availOut;
            int consumed = (int)(availIn - availInLeft);
            int produced = (int)(availOut - availOutLeft);

            if (inflate) {
                Remember(nextIn, consumed);
                if (_wrap == Wrap.Auto && _headLength > 0) {
                    // inflate's auto-detection: gzip starts 1f 8b, anything else is zlib
                    _wrap = (_head[0] == 0x1f) ? Wrap.Gzip : Wrap.Zlib;
                    _check = (_wrap == Wrap.Gzip) ? 0u : 1u;
                }
                if (err == LibZ.Z_NEED_DICT && _headLength >= 6) {
                    _check = ((uint)_head[2] << 24) | ((uint)_head[3] << 16) | ((uint)_head[4] << 8) | _head[5];
                } else {
                    Check(nextOut, produced);
                }
            } else {
                Check(nextIn, consumed);
            }

            _totalIn += (ulong)consumed;
            _totalOut += (ulong)produced;
            return err;
        }

        private void Remember(IntPtr input, int count) {
            if (_headLength < _head.Length && count > 0) {
                int n = Math.Min(count, _head.Length - _headLength);
                Marshal.Copy(input, _head, _headLength, n);
                _headLength += n;
            }
        }

        private void Check(IntPtr data, int count) {
            if (count <= 0 || _wrap == Wrap.Raw || _wrap == Wrap.Auto) {
                return;
            }
            if (_copy.Length < count) {
                _copy = new byte[Math.Max(count, 16384)];
            }
            Marshal.Copy(data, _copy, 0, count);
            _check = (_wrap == Wrap.Gzip)
                ? ManagedChecksums.Crc32(_check, _copy, 0, count)
                : ManagedChecksums.Adler32(_check, _copy, 0, count);
        }

        internal override int End(bool inflate) {
            return inflate ? InflateEnd(ref _s) : DeflateEnd(ref _s);
        }

        internal override int Reset(bool inflate) {
            // The PAL has no inflateReset/deflateReset (.NET 8 exports neither); a fresh stream
            // with the same parameters is what a reset is.
            End(inflate);
            _s = new PalZStream();
            return inflate ? InitInflate(_windowBits) : InitDeflate(_level, _windowBits, _memLevel, _strategy);
        }

        internal override int SetDictionary(bool inflate, byte[]/*!*/ dictionary) {
            throw Unavailable(inflate ? "Zlib::Inflate#set_dictionary" : "Zlib::Deflate#set_dictionary");
        }

        internal override int Params(int level, int strategy, IntPtr nextOut, uint availOut, out uint availOutLeft) {
            throw Unavailable("Zlib::Deflate#params");
        }

        internal override int SyncPoint() {
            throw Unavailable("Zlib::Inflate#sync_point?");
        }

        private static Exception/*!*/ Unavailable(string/*!*/ what) {
            return new NotImplementedError(what + " is not available with the zlib in the .NET runtime (no libz on this platform)");
        }

        internal override void ClearPointers() {
            _s.nextIn = IntPtr.Zero;
            _s.availIn = 0;
            _s.nextOut = IntPtr.Zero;
            _s.availOut = 0;
        }

        internal override void Free() {
        }

        internal override ulong TotalIn { get { return _totalIn; } }
        internal override ulong TotalOut { get { return _totalOut; } }
        internal override uint Adler { get { return _check; } }

        // Z_UNKNOWN: libz's guess at text or binary is not something the PAL passes on.
        internal override int DataType { get { return 2; } }

        internal override string Message {
            get { return (_s.msg != IntPtr.Zero) ? Marshal.PtrToStringAnsi(_s.msg) : null; }
        }
    }

    #endregion

    #region checksums

    /// <summary>
    /// adler32, crc32 and their combine functions as zlib computes them, for the platforms whose
    /// zlib does not export them (the runtime's exports only a crc32, and nothing else).
    /// </summary>
    internal static class ManagedChecksums {
        private const uint AdlerBase = 65521;
        // the most bytes adler32 can sum before the 32-bit sums could overflow
        private const int AdlerNMax = 5552;

        internal static readonly uint[]/*!*/ CrcTable = MakeCrcTable();

        private static uint[]/*!*/ MakeCrcTable() {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++) {
                uint c = n;
                for (int k = 0; k < 8; k++) {
                    c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        internal static uint Crc32(uint crc, byte[]/*!*/ data, int offset, int count) {
            crc = ~crc;
            for (int i = offset; i < offset + count; i++) {
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }
            return ~crc;
        }

        internal static uint Adler32(uint adler, byte[]/*!*/ data, int offset, int count) {
            uint a = adler & 0xFFFF;
            uint b = adler >> 16;
            while (count > 0) {
                int n = Math.Min(count, AdlerNMax);
                count -= n;
                while (n-- > 0) {
                    a += data[offset++];
                    b += a;
                }
                a %= AdlerBase;
                b %= AdlerBase;
            }
            return (b << 16) | a;
        }

        // crc32_combine: the CRC of the concatenation from the two CRCs, by multiplying the first
        // through len2 zero bytes with GF(2) matrix exponentiation (zlib 1.2's formulation, and
        // zlib 1.3's answer for len2 = 0, which is crc1 ^ crc2).
        internal static uint Crc32Combine(uint crc1, uint crc2, long len2) {
            if (len2 <= 0) {
                return crc1 ^ crc2;
            }

            var even = new uint[32];
            var odd = new uint[32];
            odd[0] = 0xEDB88320u;
            uint row = 1;
            for (int n = 1; n < 32; n++) {
                odd[n] = row;
                row <<= 1;
            }
            Gf2MatrixSquare(even, odd);     // two zero bits
            Gf2MatrixSquare(odd, even);     // four zero bits

            do {
                Gf2MatrixSquare(even, odd);
                if ((len2 & 1) != 0) {
                    crc1 = Gf2MatrixTimes(even, crc1);
                }
                len2 >>= 1;
                if (len2 == 0) {
                    break;
                }
                Gf2MatrixSquare(odd, even);
                if ((len2 & 1) != 0) {
                    crc1 = Gf2MatrixTimes(odd, crc1);
                }
                len2 >>= 1;
            } while (len2 != 0);

            return crc1 ^ crc2;
        }

        private static uint Gf2MatrixTimes(uint[]/*!*/ matrix, uint vector) {
            uint sum = 0;
            for (int i = 0; vector != 0; i++, vector >>= 1) {
                if ((vector & 1) != 0) {
                    sum ^= matrix[i];
                }
            }
            return sum;
        }

        private static void Gf2MatrixSquare(uint[]/*!*/ square, uint[]/*!*/ matrix) {
            for (int n = 0; n < 32; n++) {
                square[n] = Gf2MatrixTimes(matrix, matrix[n]);
            }
        }

        internal static uint Adler32Combine(uint adler1, uint adler2, long len2) {
            if (len2 < 0) {
                return 0xFFFFFFFFu;
            }
            ulong rem = (ulong)(len2 % AdlerBase);
            ulong sum1 = adler1 & 0xFFFF;
            ulong sum2 = (rem * sum1) % AdlerBase;
            sum1 += (adler2 & 0xFFFF) + AdlerBase - 1;
            sum2 += ((adler1 >> 16) & 0xFFFF) + ((adler2 >> 16) & 0xFFFF) + AdlerBase - rem;
            if (sum1 >= AdlerBase) sum1 -= AdlerBase;
            if (sum1 >= AdlerBase) sum1 -= AdlerBase;
            if (sum2 >= ((ulong)AdlerBase << 1)) sum2 -= ((ulong)AdlerBase << 1);
            if (sum2 >= AdlerBase) sum2 -= AdlerBase;
            return (uint)(sum1 | (sum2 << 16));
        }
    }

    #endregion
}
