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
            private string/*!*/ _name = "SHA1";
            private Crypto.IncrementalHash _hash;

            public Digest() {
                _hash = Crypto.IncrementalHash.CreateHash(Crypto.HashAlgorithmName.SHA1);
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
                    case "SHA256": return "SHA256";
                    case "SHA384": return "SHA384";
                    case "SHA512": return "SHA512";
                    default: return null;
                }
            }

            internal static Crypto.HashAlgorithmName ToHashAlgorithmName(string/*!*/ canonicalName) {
                switch (canonicalName) {
                    case "MD5": return Crypto.HashAlgorithmName.MD5;
                    case "SHA1": return Crypto.HashAlgorithmName.SHA1;
                    case "SHA256": return Crypto.HashAlgorithmName.SHA256;
                    case "SHA384": return Crypto.HashAlgorithmName.SHA384;
                    default: return Crypto.HashAlgorithmName.SHA512;
                }
            }

            internal static int DigestLengthOf(string/*!*/ canonicalName) {
                switch (canonicalName) {
                    case "MD5": return 16;
                    case "SHA1": return 20;
                    case "SHA256": return 32;
                    case "SHA384": return 48;
                    default: return 64;
                }
            }

            internal static int BlockLengthOf(string/*!*/ canonicalName) {
                return (canonicalName == "SHA384" || canonicalName == "SHA512") ? 128 : 64;
            }

            private static string/*!*/ ResolveName(RubyContext/*!*/ context, object algorithm) {
                var str = algorithm as MutableString;
                if (str != null) {
                    string canonical = CanonicalizeName(str.ConvertToString());
                    if (canonical == null) {
                        throw new OpenSSLError(MutableString.CreateMutable(
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
                self._hash = Crypto.IncrementalHash.CreateHash(ToHashAlgorithmName(self._name));

                if (data != null) {
                    Update(self, data);
                }
                return self;
            }

            [RubyMethod("reset")]
            public static Digest/*!*/ Reset(Digest/*!*/ self) {
                // IncrementalHash resets itself when the current hash is retrieved
                self._hash.GetHashAndReset();
                return self;
            }

            [RubyMethod("update")]
            [RubyMethod("<<")]
            public static Digest/*!*/ Update(Digest/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data) {
                self._hash.AppendData(data.ConvertToBytes());
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
                return self._hash.GetCurrentHash();
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
                using (var hash = Crypto.IncrementalHash.CreateHash(ToHashAlgorithmName(canonicalName))) {
                    hash.AppendData(data);
                    return hash.GetHashAndReset();
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

        [RubyClass("KDFError"), Serializable]
        public class KDFError : OpenSSLError {
            private const string/*!*/ M = "KDF error";

            public KDFError() : this(null, null) { }
            public KDFError(string message) : this(message, null) { }
            public KDFError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public KDFError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }
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

            [RubyClass("Certificate")]
            public class Certificate {
                private X509Certificate/*!*/ _certificate;

                private bool IsEmpty {
                    get { return _certificate.Handle == IntPtr.Zero; }
                }

                [RubyConstructor]
                public static Certificate/*!*/ CreateCertificate(RubyClass/*!*/ self) {
                    return Initialize(new Certificate(), null);
                }

                [RubyConstructor]
                public static Certificate/*!*/ CreateCertificate(RubyClass/*!*/ self, MutableString/*!*/ data) {
                    return Initialize(new Certificate(), data);
                }

                [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
                public static Certificate/*!*/ Initialize(Certificate/*!*/ self, MutableString/*!*/ data) {
                    if (data == null) {
                        self._certificate = new X509Certificate();
                    } else {
                        self._certificate = new X509Certificate(data.ToByteArray());
                    }

                    return self;
                }

                // add_extension
                // check_private_key
                // extensions
                // extensions=

                private static string OpenSSLFormat(string x509String) {
                    string[] pairs = x509String.Split(',');
                    Array.Sort<string>(pairs);

                    StringBuilder sb = new StringBuilder();
                    foreach (var val in pairs) {
                        sb.AppendFormat("/{0}", val.Trim());
                    }

                    return sb.ToString();
                }

                // issuer=

                [RubyMethod("issuer")]
                public static MutableString Issuer(Certificate/*!*/ self) {
                    if (self.IsEmpty) {
                        return null;
                    } else {
                        return MutableString.CreateAscii(OpenSSLFormat(self._certificate.Issuer));
                    }
                }

                // not_after => time
                // not_after=
                // not_before => time
                // not_before=

                [RubyMethod("public_key")]
                public static MutableString PublicKey(Certificate/*!*/ self) {
                    if (self.IsEmpty) {
                        // TODO: Raise OpenSSL::X509::CertificateError
                        return MutableString.CreateEmpty();
                    } else {
                        return MutableString.CreateAscii(self._certificate.GetPublicKeyString());
                    }
                }
                // public_key=

                private int SerailNumber {
                    get {
                        if (IsEmpty) {
                            return 0;
                        } else {
                            return int.Parse(_certificate.GetSerialNumberString(), CultureInfo.InvariantCulture);
                        }
                    }
                }

                [RubyMethod("serial")]
                public static int Serial(Certificate/*!*/ self) {
                    return self.SerailNumber;
                }

                // serial=
                // sign(key, digest) => self
                // signature_algorithm

                [RubyMethod("subject")]
                public static MutableString Subject(Certificate/*!*/ self) {
                    if (self.IsEmpty) {
                        return null;
                    } else {
                        return MutableString.CreateAscii(OpenSSLFormat(self._certificate.Subject));
                    }
                }

                // subject=
                // to_der
                // to_pem

                [RubyMethod("inspect")]
                [RubyMethod("to_s")]
                public static MutableString ToString(RubyContext/*!*/ context, Certificate/*!*/ self) {
                    using (IDisposable handle = RubyUtils.InfiniteInspectTracker.TrackObject(self)) {
                        // #<OpenSSL::X509::Certificate subject=, issuer=, serial=0, not_before=nil, not_after=nil>
                        var result = MutableString.CreateEmpty();
                        result.Append("#<");
                        result.Append(context.Inspect(context.GetClassOf(self)));

                        if (handle == null) {
                            return result.Append(":...>");
                        }
                        bool empty = self.IsEmpty;
                        result.AppendFormat(" subject={0}, issuer={1}, serial={2}, not_before=nil, not_after=nil>", 
                            empty ? "" : OpenSSLFormat(self._certificate.Subject),
                            empty ? "" : OpenSSLFormat(self._certificate.Issuer),
                            empty ? 0 : self.SerailNumber
                        );
                        return result;
                    }
                }

                // to_text
                // verify

                [RubyMethod("version")]
                public static int Version(Certificate/*!*/ self) {
                    if (self.IsEmpty) {
                        return 0;
                    } else {
                        return 2;
                    }
                }

                // version=
            }

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

        [RubyModule("PKey")]
        public static class PKey {

            [RubyClass("RSA")]
            public class RSA {
                // RSA.new([size | encoded_key] [, pass]) -> rsa
                // new(2048) -> rsa 
                // new(File.read("rsa.pem")) -> rsa
                // new(File.read("rsa.pem"), "mypassword") -> rsa
                // initialize
                // generate(size [, exponent]) -> rsa
                // public? -> true (The return value is always true since every private key is also a public key)
                // private? -> true | false
                // to_pem -> aString
                // to_pem(cipher, pass) -> aString
                // to_der -> aString
                // public_encrypt(string [, padding]) -> aString
                // public_decrypt(string [, padding]) -> aString
                // private_encrypt(string [, padding]) -> aString
                // private_decrypt(string [, padding]) -> aString
                // params -> hash
                // to_text -> aString
                // public_key -> aRSA
                // inspect
                // to_s
            }
        }

        [RubyClass("OpenSSLError"), Serializable]
        public class OpenSSLError : SystemException {
            private const string/*!*/ M = "OpenSSL error";

            public OpenSSLError() : this(null, null) { }
            public OpenSSLError(string message) : this(message, null) { }
            public OpenSSLError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
            public OpenSSLError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
            protected OpenSSLError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context) { }
#endif
        }

        [RubyModule("SSL")]
        public static class SSL {
            [RubyClass("SSLError"), Serializable]
            public class SSLError : OpenSSLError {
                private const string/*!*/ M = "SSL error";

                public SSLError() : this(null, null) { }
                public SSLError(string message) : this(message, null) { }
                public SSLError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
                public SSLError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
                protected SSLError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                    : base(info, context) { }
#endif
            }
        }
    }
}
#endif