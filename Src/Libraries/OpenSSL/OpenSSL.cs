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
#if FEATURE_CRYPTOGRAPHY

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using IronRuby.StandardLibrary.Sockets;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using Crypto = System.Security.Cryptography;

namespace IronRuby.StandardLibrary.OpenSsl {

    [RubyModule("OpenSSL", BuildConfig = "FEATURE_CRYPTOGRAPHY")]
    public static class OpenSsl {
        // TODO: constants
        // Config,HMACError,PKCS12,Random,OPENSSL_VERSION,PKCS7,BN,ConfigError,PKey,Engine,BNError,Netscape,OCSP
        // OpenSSLError,CipherError,SSL,VERSION,X509,ASN1,OPENSSL_VERSION_NUMBER,Cipher

        // The algorithms below come from System.Security.Cryptography, not from
        // libcrypto, but ruby/spec (and real code) gates features on these two
        // constants, so they report the feature level actually implemented.
        [RubyConstant]
        public const string OPENSSL_VERSION = "OpenSSL 3.0.0 (IronRuby, System.Security.Cryptography)";

        [RubyConstant]
        public const int OPENSSL_VERSION_NUMBER = 0x30000000;

        [RubyConstant]
        public const string VERSION = "3.2.0";

        /// <summary>
        /// OpenSSL::Digest wraps one of the message digests OpenSSL's EVP layer exposes.
        /// The .NET equivalent is IncrementalHash, which supports the same
        /// update/finish/reset lifecycle.
        /// </summary>
        [RubyClass("Digest")]
        public class Digest {
            // MRI's class for "this is not a digest OpenSSL knows"; code rescues it
            // by name, so it has to be this class and not its OpenSSLError parent.
            [RubyException("DigestError"), Serializable]
            public class DigestError : OpenSSLError {
                public DigestError() : this(null, null) { }
                public DigestError(string message) : this(message, null) { }
                public DigestError(string message, Exception inner) : base(message ?? "DigestError", inner) { }
                public DigestError(MutableString message) : base(message.ConvertToString()) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
                protected DigestError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                    : base(info, context) { }
#endif
            }

            private string/*!*/ _name = "SHA1";
            private IncrementalDigest _hash;

            public Digest() {
                _hash = CreateIncremental("SHA1");
            }

            internal string/*!*/ AlgorithmName {
                get { return _name; }
            }

            /// <summary>
            /// OpenSSL accepts any of its own spellings of a digest name ("sha1", "SHA-1", ...)
            /// and reports back the canonical one.
            /// </summary>
            internal static string CanonicalizeName(string/*!*/ name) {
                switch (name.ToUpperInvariant().Replace("-", "")) {
                    case "MD5": return "MD5";
                    case "SHA":
                    case "SHA1": return "SHA1";
                    case "SHA224": return "SHA224";
                    case "SHA256": return "SHA256";
                    case "SHA384": return "SHA384";
                    case "SHA512": return "SHA512";
                    // SHA-3 arrived in .NET 8; it is only there when the platform's
                    // crypto backend has it (OpenSSL 3 does, Windows CNG before 25H2
                    // does not), so an unsupported build reports the name as unknown.
                    // .NET has no SHA3-224: it only bound the three sizes that pair
                    // with its signature and HMAC algorithms.
                    case "SHA3256": return Crypto.SHA3_256.IsSupported ? "SHA3-256" : null;
                    case "SHA3384": return Crypto.SHA3_256.IsSupported ? "SHA3-384" : null;
                    case "SHA3512": return Crypto.SHA3_256.IsSupported ? "SHA3-512" : null;
                    default: return null;
                }
            }

            internal static Crypto.HashAlgorithmName ToHashAlgorithmName(string/*!*/ canonicalName) {
                switch (canonicalName) {
                    case "MD5": return Crypto.HashAlgorithmName.MD5;
                    case "SHA1": return Crypto.HashAlgorithmName.SHA1;
                    case "SHA256": return Crypto.HashAlgorithmName.SHA256;
                    case "SHA384": return Crypto.HashAlgorithmName.SHA384;
                    case "SHA3-256": return Crypto.HashAlgorithmName.SHA3_256;
                    case "SHA3-384": return Crypto.HashAlgorithmName.SHA3_384;
                    case "SHA3-512": return Crypto.HashAlgorithmName.SHA3_512;
                    default: return Crypto.HashAlgorithmName.SHA512;
                }
            }

            internal static int DigestLengthOf(string/*!*/ canonicalName) {
                switch (canonicalName) {
                    case "MD5": return 16;
                    case "SHA1": return 20;
                    case "SHA224": return 28;
                    case "SHA256": return 32;
                    case "SHA384": return 48;
                    case "SHA3-256": return 32;
                    case "SHA3-384": return 48;
                    default: return 64;
                }
            }

            internal static int BlockLengthOf(string/*!*/ canonicalName) {
                switch (canonicalName) {
                    case "SHA384":
                    case "SHA512": return 128;
                    // Keccak's rate: 1600 bits of state less twice the capacity.
                    case "SHA3-256": return 136;
                    case "SHA3-384": return 104;
                    case "SHA3-512": return 72;
                    default: return 64;
                }
            }

            /// <summary>
            /// A fresh running digest for the algorithm.  .NET has no SHA-224 and no way
            /// to hand SHA-256 a different initial state, which is all SHA-224 is, so
            /// that one is computed here (Sha224.cs) instead of mapped onto .NET.
            /// </summary>
            internal static IncrementalDigest/*!*/ CreateIncremental(string/*!*/ canonicalName) {
                if (canonicalName == "SHA224") {
                    return new Sha224();
                }
                return new NetDigest(Crypto.IncrementalHash.CreateHash(ToHashAlgorithmName(canonicalName)));
            }

            private static string/*!*/ ResolveName(RubyContext/*!*/ context, object algorithm) {
                var str = algorithm as MutableString;
                if (str != null) {
                    string canonical = CanonicalizeName(str.ConvertToString());
                    if (canonical == null) {
                        throw new DigestError(MutableString.CreateMutable(
                            "Unsupported digest algorithm (" + str.ConvertToString() + ").", RubyEncoding.UTF8
                        ));
                    }
                    return canonical;
                }

                var digest = algorithm as Digest;
                if (digest != null) {
                    // the state of the argument is deliberately not copied, matching OpenSSL
                    return digest._name;
                }

                throw RubyExceptions.CreateTypeConversionError(context.GetClassDisplayName(algorithm), "String");
            }

            [RubyConstructor]
            public static Digest/*!*/ CreateDigest(RubyContext/*!*/ context, RubyClass/*!*/ self, object algorithm,
                [DefaultProtocol, Optional]MutableString data) {
                return Initialize(context, new Digest(), algorithm, data);
            }

            // Reinitialization. Not called when a factory/non-default ctor is called.
            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static Digest/*!*/ Initialize(RubyContext/*!*/ context, Digest/*!*/ self, object algorithm,
                [DefaultProtocol, Optional]MutableString data) {
                self._name = ResolveName(context, algorithm);
                self._hash = CreateIncremental(self._name);

                if (data != null) {
                    Update(self, data);
                }
                return self;
            }

            [RubyMethod("reset")]
            public static Digest/*!*/ Reset(Digest/*!*/ self) {
                // IncrementalHash resets itself when the current hash is retrieved
                self._hash.Reset();
                return self;
            }

            [RubyMethod("update")]
            [RubyMethod("<<")]
            public static Digest/*!*/ Update(Digest/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data) {
                self._hash.Append(data.ConvertToBytes());
                return self;
            }

            [RubyMethod("name")]
            public static MutableString/*!*/ Name(Digest/*!*/ self) {
                return MutableString.CreateAscii(self._name);
            }

            [RubyMethod("digest_length")]
            [RubyMethod("digest_size")]
            public static int DigestLength(Digest/*!*/ self) {
                return DigestLengthOf(self._name);
            }

            [RubyMethod("block_length")]
            public static int BlockLength(Digest/*!*/ self) {
                return BlockLengthOf(self._name);
            }

            /// <summary>
            /// Finishes the running digest without losing it: IncrementalHash can only
            /// finish-and-reset, so the accumulated bytes are replayed afterwards.
            /// </summary>
            internal static byte[]/*!*/ Finish(Digest/*!*/ self) {
                return self._hash.Peek();
            }

            [RubyMethod("digest")]
            public static MutableString/*!*/ GetDigest(Digest/*!*/ self) {
                return MutableString.CreateBinary(Finish(self));
            }

            [RubyMethod("digest")]
            public static MutableString/*!*/ GetDigest(RubyContext/*!*/ context, Digest/*!*/ self, [NotNull]MutableString/*!*/ data) {
                Reset(self);
                Update(self, data);
                var result = MutableString.CreateBinary(Finish(self));
                Reset(self);
                return result;
            }

            [RubyMethod("hexdigest")]
            public static MutableString/*!*/ HexDigest(Digest/*!*/ self) {
                return MutableString.CreateAscii(ToHex(Finish(self)));
            }

            [RubyMethod("hexdigest")]
            public static MutableString/*!*/ HexDigest(RubyContext/*!*/ context, Digest/*!*/ self, [NotNull]MutableString/*!*/ data) {
                return MutableString.CreateAscii(ToHex(GetDigest(context, self, data).ConvertToBytes()));
            }

            [RubyMethod("base64digest")]
            public static MutableString/*!*/ Base64Digest(Digest/*!*/ self) {
                return MutableString.CreateAscii(Convert.ToBase64String(Finish(self)));
            }

            [RubyMethod("base64digest")]
            public static MutableString/*!*/ Base64Digest(RubyContext/*!*/ context, Digest/*!*/ self, [NotNull]MutableString/*!*/ data) {
                return MutableString.CreateAscii(Convert.ToBase64String(GetDigest(context, self, data).ConvertToBytes()));
            }

            [RubyMethod("==")]
            public static bool Equal(RubyContext/*!*/ context, Digest/*!*/ self, [NotNull]Digest/*!*/ other) {
                return self._name == other._name && ToHex(Finish(self)) == ToHex(Finish(other));
            }

            internal static byte[]/*!*/ ComputeHash(string/*!*/ canonicalName, byte[]/*!*/ data) {
                using (var hash = CreateIncremental(canonicalName)) {
                    hash.Append(data);
                    return hash.Peek();
                }
            }

            internal static string/*!*/ ToHex(byte[]/*!*/ bytes) {
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) {
                    sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }

            #region singleton digest/hexdigest/base64digest

            [RubyMethod("digest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Digest_(RubyContext/*!*/ context, RubyClass/*!*/ self,
                object algorithm, [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateBinary(ComputeHash(ResolveName(context, algorithm), data.ConvertToBytes()));
            }

            [RubyMethod("hexdigest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ HexDigest_(RubyContext/*!*/ context, RubyClass/*!*/ self,
                object algorithm, [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateAscii(ToHex(ComputeHash(ResolveName(context, algorithm), data.ConvertToBytes())));
            }

            [RubyMethod("base64digest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Base64Digest_(RubyContext/*!*/ context, RubyClass/*!*/ self,
                object algorithm, [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateAscii(Convert.ToBase64String(ComputeHash(ResolveName(context, algorithm), data.ConvertToBytes())));
            }

            #endregion
        }

        [RubyClass("HMAC")]
        public class HMAC {

            internal static byte[]/*!*/ Compute(Digest/*!*/ digest, MutableString/*!*/ key, MutableString/*!*/ data) {
                byte[] keyBytes = key.ConvertToBytes();
                using (var hmac = Crypto.IncrementalHash.CreateHMAC(Digest.ToHashAlgorithmName(digest.AlgorithmName), keyBytes)) {
                    hmac.AppendData(data.ConvertToBytes());
                    return hmac.GetHashAndReset();
                }
            }

            [RubyMethod("hexdigest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ HexDigest(RubyClass/*!*/ self,
                [NotNull]Digest/*!*/ digest,
                [DefaultProtocol, NotNull]MutableString/*!*/ key,
                [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateAscii(Digest.ToHex(Compute(digest, key, data)));
            }

            [RubyMethod("digest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Digest_(RubyClass/*!*/ self,
                [NotNull]Digest/*!*/ digest,
                [DefaultProtocol, NotNull]MutableString/*!*/ key,
                [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateBinary(Compute(digest, key, data));
            }

            // HMAC.new(key, digest) -> hmac
            // update(string) -> self
            // digest -> aString
            // hexdigest -> aString
            // reset -> self
        }

        /// <summary>
        /// OpenSSL::KDF. The keyword handling and argument coercion live in the Ruby
        /// half (Src/StdLib/ironruby/openssl.rb); these are the raw primitives.
        /// </summary>
        [RubyModule("KDF")]
        public static class KDF {

            [RubyMethod("__pbkdf2_hmac__", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Pbkdf2Hmac(RubyModule/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ pass,
                [DefaultProtocol, NotNull]MutableString/*!*/ salt,
                [DefaultProtocol]int iterations,
                [DefaultProtocol]int length,
                [DefaultProtocol, NotNull]MutableString/*!*/ hash) {

                // MRI allocates the result string before anything else is checked
                if (length < 0) {
                    throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
                }

                string canonical = Digest.CanonicalizeName(hash.ConvertToString());
                if (canonical == null) {
                    throw new OpenSSLError(MutableString.CreateMutable(
                        "Unsupported digest algorithm (" + hash.ConvertToString() + ").", RubyEncoding.UTF8
                    ));
                }
                if (length == 0) {
                    return MutableString.CreateBinary(new byte[0]);
                }
                if (iterations <= 0) {
                    throw new KDFError(MutableString.CreateAscii("PKCS5_PBKDF2_HMAC: invalid iteration count"));
                }

                byte[] derived = Crypto.Rfc2898DeriveBytes.Pbkdf2(
                    pass.ConvertToBytes(), salt.ConvertToBytes(), iterations, Digest.ToHashAlgorithmName(canonical), length
                );
                return MutableString.CreateBinary(derived);
            }

            [RubyMethod("__scrypt__", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Scrypt(RubyModule/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ pass,
                [DefaultProtocol, NotNull]MutableString/*!*/ salt,
                [DefaultProtocol]int N,
                [DefaultProtocol]int r,
                [DefaultProtocol]int p,
                [DefaultProtocol]int length) {

                // MRI allocates the result string before anything else is checked
                if (length < 0) {
                    throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
                }
                if (length == 0) {
                    return MutableString.CreateBinary(new byte[0]);
                }
                if (N < 2 || (N & (N - 1)) != 0) {
                    throw new KDFError(MutableString.CreateAscii("EVP_PBE_scrypt: Invalid N parameter"));
                }
                if (r < 1 || p < 1) {
                    throw new KDFError(MutableString.CreateAscii("EVP_PBE_scrypt: Invalid parameters"));
                }
                return MutableString.CreateBinary(
                    ScryptImpl.DeriveKey(pass.ConvertToBytes(), salt.ConvertToBytes(), N, r, p, length)
                );
            }
        }

        [RubyException("KDFError"), Serializable]
        public class KDFError : OpenSSLError {
            public KDFError() : this(null, null) { }
            public KDFError(string message) : this(message, null) { }
            public KDFError(string message, Exception inner) : base(message ?? "KDFError", inner) { }
            public KDFError(MutableString message) : base(message.ConvertToString()) { RubyExceptionData.InitializeException(this, message); }
        }

        #region secure_compare

        [RubyMethod("fixed_length_secure_compare", RubyMethodAttributes.PublicSingleton)]
        public static bool FixedLengthSecureCompare(RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ a, [DefaultProtocol, NotNull]MutableString/*!*/ b) {

            byte[] x = a.ConvertToBytes();
            byte[] y = b.ConvertToBytes();
            if (x.Length != y.Length) {
                throw RubyExceptions.CreateArgumentError("inputs must be of equal length");
            }
            return Crypto.CryptographicOperations.FixedTimeEquals(x, y);
        }

        #endregion

        [RubyModule("Random")]
        public static class RandomModule {

            // This is a no-op method since our random number generator uses the .NET crypto random number generator
            // that gets its seed values from the OS

            [RubyMethod("seed", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Seed(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ seed) {
                return seed;
            }

            [RubyMethod("pseudo_bytes", RubyMethodAttributes.PublicSingleton)]
            [RubyMethod("random_bytes", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ RandomBytes(RubyModule/*!*/ self, [DefaultProtocol]int length) {
                if (length < 0) {
                    throw RubyExceptions.CreateArgumentError("negative string size");
                }

                if (length == 0) {
                    return MutableString.CreateEmpty();
                }

                byte[] data = new byte[length];
                var generator = new Crypto.RNGCryptoServiceProvider();
                generator.GetBytes(data);

                return MutableString.CreateBinary(data);
            }

            // add(str, entropy) -> self
            // load_random_file(filename) -> true
        }

        [RubyClass("BN")]
        public class BN {

            // new => aBN
            // new(bn) => aBN
            // new(string) => aBN
            // new(string, 0 | 2 | 10 | 16) => aBN

            [RubyMethod("rand", RubyMethodAttributes.PublicSingleton)]
            public static BigInteger/*!*/ Rand(RubyClass/*!*/ self, [DefaultProtocol]int bits, [DefaultProtocol, Optional]int someFlag, [Optional]bool otherFlag) { // TODO: figure out someFlag and otherFlag
                byte[] data = new byte[bits >> 3];
                var generator = new Crypto.RNGCryptoServiceProvider();
                generator.GetBytes(data);

                uint[] transformed = new uint[data.Length >> 2];
                int j = 0;
                for (int i = 0; i < transformed.Length; ++i) {
                    transformed[i] = data[j] + (uint)(data[j + 1] << 8) + (uint)(data[j + 2] << 16) + (uint)(data[j + 3] << 24);
                    j += 4;
                }

                return BigIntegerCompat.Create(1, transformed);
            }
        }

        [RubyModule("X509")]
        public static class X509 {

            [RubyClass("CertificateError", Extends = typeof(CryptographicException), Inherits = typeof(ExternalException))]
            public class CryptographicExceptionOps {
                [RubyConstructor]
                public static CryptographicException/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                    CryptographicException result = new CryptographicException(RubyExceptions.MakeMessage(ref message, "Not enought data."));
                    RubyExceptionData.InitializeException(result, message);
                    return result;
                }
            }

            // TODO: Constants

            // OpenSSL::X509::Certificate, ExtensionFactory and Store are written in Ruby
            // (Src/StdLib/ironruby/openssl.rb): building and verifying a certificate is
            // System.Security.Cryptography.X509Certificates work, and driving CertificateRequest
            // and X509Chain from Ruby keeps the whole of X509 in one place.

            [RubyClass("Name")]
            public class Name {
                // new => name
                // new(string) => name
                // new(dn) => name
                // new(dn, template) => name
                // add_entry(oid, value [, type]) => self
                // to_s => string
                // to_s(integer) => string
                // to_a => [[name, data, type], ...]
                // hash => integer
                // to_der => string
                // parse(string) => name
            }
        }

        // OpenSSL::PKey is written in Ruby (Src/StdLib/ironruby/openssl/pkey.rb): the
        // keys are System.Security.Cryptography's RSA, DSA and ECDsa, and the classes
        // there define #initialize, which a C# stub of the same name would take over.

        // MRI's OpenSSL exceptions are plain StandardErrors: the message is exactly what was given,
        // not the "<base> - <message>" form of SystemCallError.
        [RubyException("OpenSSLError"), Serializable]
        public class OpenSSLError : SystemException {
            public OpenSSLError() : this(null, null) { }
            public OpenSSLError(string message) : this(message, null) { }
            public OpenSSLError(string message, Exception inner) : base(message ?? "OpenSSLError", inner) { }
            public OpenSSLError(MutableString message) : base(message.ConvertToString()) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
            protected OpenSSLError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
#endif
        }

        [RubyModule("SSL")]
        public static class SSL {
#if FEATURE_SYNC_SOCKETS
        /// <summary>
        /// One TLS connection.  Not an MRI class: OpenSSL::SSL::SSLSocket is the
        /// Ruby class in openssl/ssl.rb and this is what it does its reading and
        /// writing through.
        /// </summary>
        [RubyClass("Transport__")]
        public class Transport {
            private Socket _socket;
            private SslStream _ssl;
            private bool _verify;
            private string _hostname;

            [RubyConstructor]
            public static Transport/*!*/ Create(RubyClass/*!*/ self, [NotNull]object/*!*/ io) {
                var socket = io as RubyBasicSocket;
                if (socket == null) {
                    throw RubyExceptions.CreateTypeError("SSLSocket needs a socket to run on");
                }
                var result = new Transport();
                result._socket = socket.Socket;
                // The Ruby socket layer leaves Socket.Blocking false and does its own
                // polling; SslStream drives the socket itself and needs it blocking.
                result._socket.Blocking = true;
                return result;
            }

            private bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) {
                return !_verify || errors == SslPolicyErrors.None;
            }

            /// <summary>
            /// The client handshake.  hostname is the SNI name and the name the
            /// certificate is checked against; verify false accepts any certificate,
            /// which is what OpenSSL::SSL::VERIFY_NONE means.
            /// </summary>
            [RubyMethod("connect")]
            public static Transport/*!*/ Connect(Transport/*!*/ self,
                [DefaultProtocol]MutableString hostname, bool verify) {

                self._verify = verify;
                self._hostname = hostname == null ? "" : hostname.ConvertToString();
                try {
                    self._ssl = new SslStream(new NetworkStream(self._socket, false), false,
                        new RemoteCertificateValidationCallback(self.Validate));
                    self._ssl.AuthenticateAsClient(self._hostname);
                } catch (Exception error) {
                    throw new SSLError(Unwrap(error).Message);
                }
                return self;
            }

            // An SslStream failure arrives wrapped in an AuthenticationException whose
            // inner exception says what actually went wrong.
            private static Exception/*!*/ Unwrap(Exception/*!*/ error) {
                return (error is AuthenticationException || error is IOException) && error.InnerException != null
                    ? error.InnerException : error;
            }

            private SslStream/*!*/ Stream {
                get {
                    if (_ssl == null) {
                        throw new SSLError("SSL session is not started yet");
                    }
                    return _ssl;
                }
            }

            /// <summary>Up to length bytes, or nil at end of stream.</summary>
            [RubyMethod("read")]
            public static MutableString Read(Transport/*!*/ self, [DefaultProtocol]int length) {
                byte[] buffer = new byte[length];
                int read;
                try {
                    read = self.Stream.Read(buffer, 0, length);
                } catch (IOException error) {
                    throw new SSLError(Unwrap(error).Message);
                }
                if (read == 0) {
                    return null;
                }
                Array.Resize(ref buffer, read);
                return MutableString.CreateBinary(buffer);
            }

            [RubyMethod("write")]
            public static int Write(Transport/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data) {
                byte[] bytes = data.ConvertToBytes();
                try {
                    self.Stream.Write(bytes, 0, bytes.Length);
                    self.Stream.Flush();
                } catch (IOException error) {
                    throw new SSLError(Unwrap(error).Message);
                }
                return bytes.Length;
            }

            [RubyMethod("close")]
            public static void Close(Transport/*!*/ self) {
                if (self._ssl != null) {
                    try {
                        self._ssl.Dispose();
                    } catch (Exception) {
                        // closing a connection the peer already dropped is not an error
                    }
                    self._ssl = null;
                }
            }

            /// <summary>The peer's certificate as DER, or nil before the handshake.</summary>
            [RubyMethod("peer_cert")]
            public static MutableString PeerCertificate(Transport/*!*/ self) {
                if (self._ssl == null || self._ssl.RemoteCertificate == null) {
                    return null;
                }
                return MutableString.CreateBinary(self._ssl.RemoteCertificate.Export(X509ContentType.Cert));
            }

            /// <summary>"TLSv1.3", the spelling OpenSSL::SSL::SSLSocket#ssl_version uses.</summary>
            [RubyMethod("protocol")]
            public static MutableString/*!*/ Protocol(Transport/*!*/ self) {
                if (self._ssl == null) {
                    return null;
                }
                switch (self._ssl.SslProtocol) {
                    case SslProtocols.Tls: return MutableString.CreateAscii("TLSv1");
                    case SslProtocols.Tls11: return MutableString.CreateAscii("TLSv1.1");
                    case SslProtocols.Tls12: return MutableString.CreateAscii("TLSv1.2");
                    case SslProtocols.Tls13: return MutableString.CreateAscii("TLSv1.3");
                    default: return MutableString.CreateAscii(self._ssl.SslProtocol.ToString());
                }
            }

            /// <summary>The negotiated cipher suite, e.g. "TLS_AES_256_GCM_SHA384".</summary>
            [RubyMethod("cipher")]
            public static MutableString Cipher(Transport/*!*/ self) {
                if (self._ssl == null) {
                    return null;
                }
                return MutableString.CreateAscii(self._ssl.NegotiatedCipherSuite.ToString());
            }

            /// <summary>Bytes already decrypted and waiting, which IO.select cannot see.</summary>
            [RubyMethod("pending")]
            public static int Pending(Transport/*!*/ self) {
                return self._ssl == null ? 0 : (self._ssl.CanRead && self._socket.Available > 0 ? self._socket.Available : 0);
            }
        }
#endif

            [RubyException("SSLError"), Serializable]
            public class SSLError : OpenSSLError {
                public SSLError() : this(null, null) { }
                public SSLError(string message) : this(message, null) { }
                public SSLError(string message, Exception inner) : base(message ?? "SSLError", inner) { }
                public SSLError(MutableString message) : base(message.ConvertToString()) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
                protected SSLError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                    : base(info, context) { }
#endif
            }
        }
    }
}
#endif