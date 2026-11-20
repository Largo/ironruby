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
    /// One Digest::Base instance whose hash function a C extension brings (ruby/digest.h): the
    /// class's "metadata" wraps the extension's rb_digest_metadata_t, and the hash state is a
    /// context of its ctx_size, which init_func, update_func and finish_func work on, as in
    /// digest.so's Digest::Base.
    /// </summary>
    public sealed unsafe class NativeDigest {
        [StructLayout(LayoutKind.Sequential)]
        private struct Metadata {
            public int ApiVersion;
            public nuint DigestLength, BlockLength, ContextSize;
            public IntPtr Init, Update, Finish;
        }

        private const int ApiVersion = 3;   // RUBY_DIGEST_API_VERSION
        private const int OpInit = 0, OpUpdate = 1, OpFinish = 2;

        private static delegate* unmanaged[Cdecl]<VALUE, Metadata*> _metadataOf;
        private static delegate* unmanaged[Cdecl]<Metadata*, int, void*, byte*, nuint, int*, int> _call;

        private readonly Metadata* _meta;
        private readonly void* _context;

        private NativeDigest(Metadata* meta) {
            _meta = meta;
            _context = NativeMemory.AllocZeroed(Math.Max(meta->ContextSize, 1));
        }

        ~NativeDigest() {
            NativeMemory.Free(_context);
        }

        public int DigestLength { get { return checked((int)_meta->DigestLength); } }
        public int BlockLength { get { return checked((int)_meta->BlockLength); } }

        /// <summary>
        /// The digest for an object of a class whose "metadata" is <paramref name="metadata"/>,
        /// initialized; null when that is not plugin metadata at all.
        /// </summary>
        public static NativeDigest TryCreate(object metadata) {
            var data = metadata as DataObject;
            if (data == null || !data.Typed) {
                return null;
            }
            if (_metadataOf == null) {
                _call = (delegate* unmanaged[Cdecl]<Metadata*, int, void*, byte*, nuint, int*, int>)CApi.ShimExport("rb_ir_digest_call");
                _metadataOf = (delegate* unmanaged[Cdecl]<VALUE, Metadata*>)CApi.ShimExport("rb_ir_digest_metadata");
            }
            Metadata* meta = _metadataOf(CApi.ToValue(data));
            if (meta == null) {
                return null;
            }
            if (meta->ApiVersion != ApiVersion) {
                throw RubyExceptions.CreateRuntimeError("Incompatible digest API version");
            }
            var result = new NativeDigest(meta);
            result.Reset();
            return result;
        }

        private int Call(int op, byte* ptr, nuint length) {
            int raised, result;
            var depth = CApi.EnterNative();
            try {
                result = _call(_meta, op, _context, ptr, length, &raised);
            } finally {
                CApi.LeaveNative(depth);
            }
            GC.KeepAlive(this);
            if (raised != 0) {
                CApi.Rethrow();
            }
            return result;
        }

        public void Reset() {
            if (Call(OpInit, null, 0) != 1) {
                throw RubyExceptions.CreateRuntimeError("Digest initialization failed.");
            }
        }

        public void Update(byte[]/*!*/ data) {
            fixed (byte* p = data) {
                Call(OpUpdate, p, (nuint)data.Length);
            }
        }

        /// <summary>The digest, after which the context starts over, as digest.so's #finish leaves it.</summary>
        public byte[]/*!*/ Finish() {
            var result = new byte[DigestLength];
            fixed (byte* p = result) {
                Call(OpFinish, p, 0);
            }
            Reset();
            return result;
        }

        public void CopyFrom(NativeDigest/*!*/ other) {
            if (other._meta != _meta) {
                throw RubyExceptions.CreateTypeError("different algorithms");
            }
            Buffer.MemoryCopy(other._context, _context, _meta->ContextSize, _meta->ContextSize);
            GC.KeepAlive(other);
        }
    }
}
