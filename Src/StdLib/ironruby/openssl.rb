# ****************************************************************************
#
# Copyright (c) Microsoft Corporation. 
#
# This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
# copy of the license can be found in the License.html file at the root of this distribution. If 
# you cannot locate the  Apache License, Version 2.0, please send an email to 
# ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
# by the terms of the Apache License, Version 2.0.
#
# You must not remove this notice, or any other, from this software.
#
#
# ****************************************************************************

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.OpenSsl'

module OpenSSL
  module SSL
    VERIFY_NONE = 0
    VERIFY_PEER = 1
    VERIFY_FAIL_IF_NO_PEER_CERT = 2
    VERIFY_CLIENT_ONCE = 4

    # There is no TLS implementation behind this; the class exists so that code
    # which only configures a context (net/ftp, net/http) loads and runs.
    class SSLContext
      SESSION_CACHE_OFF = 0x0000
      SESSION_CACHE_CLIENT = 0x0001
      SESSION_CACHE_SERVER = 0x0002
      SESSION_CACHE_BOTH = 0x0003
      SESSION_CACHE_NO_AUTO_CLEAR = 0x0080
      SESSION_CACHE_NO_INTERNAL_LOOKUP = 0x0100
      SESSION_CACHE_NO_INTERNAL_STORE = 0x0200
      SESSION_CACHE_NO_INTERNAL = 0x0300

      DEFAULT_PARAMS = {
        :verify_mode => VERIFY_PEER,
        :verify_hostname => true
      }.freeze

      attr_accessor :verify_mode, :verify_hostname, :cert_store, :ca_file,
                    :ca_path, :cert, :key, :options, :ssl_version,
                    :min_version, :max_version, :ciphers, :session_cache_mode,
                    :session_id_context, :timeout, :verify_callback,
                    :verify_depth, :servername_cb, :session_new_cb,
                    :alpn_protocols, :npn_protocols, :extra_chain_cert

      def set_params(params = {})
        DEFAULT_PARAMS.each do |name, value|
          send("#{name}=", value) unless params.key?(name)
        end
        params.each do |name, value|
          send("#{name}=", value) if respond_to?("#{name}=")
        end
        params
      end

      def setup
        nil
      end

      def session_cache_stats
        {}
      end

      def flush_sessions(time = nil)
        self
      end
    end
  end

  class Digest
    # CRuby defines OpenSSL::Digest::SHA256 and friends in C (ossl_digest.c);
    # each is a subclass that pins the algorithm name.
    class DigestError < OpenSSLError; end

    # SHA224 and RIPEMD160 are deliberately absent: .NET has no implementation.
    %w[MD5 SHA1 SHA256 SHA384 SHA512].each do |algorithm|
      subclass = Class.new(Digest) do
        define_method(:initialize) do |data = nil|
          if data.nil?
            super(algorithm)
          else
            super(algorithm, data)
          end
        end
      end

      subclass.singleton_class.send(:define_method, :digest) do |data|
        Digest.digest(algorithm, data)
      end
      subclass.singleton_class.send(:define_method, :hexdigest) do |data|
        Digest.hexdigest(algorithm, data)
      end
      subclass.singleton_class.send(:define_method, :base64digest) do |data|
        Digest.base64digest(algorithm, data)
      end

      const_set(algorithm, subclass)
    end
  end

  # OpenSSL::KDF's public API is all keyword arguments; the C# half only exposes
  # the positional primitives.
  module KDF
    # The C# half registers the class as OpenSSL::KDFError (nested classes of a
    # static class cannot be nested inside the KDF module there); CRuby's name
    # for it is OpenSSL::KDF::KDFError.
    KDFError = OpenSSL::KDFError

    # :N: cannot be written as a formal keyword argument in Ruby, so the
    # keywords are taken as a hash and checked by hand, the way the C code does.
    def self.__kw(opts, *names) # :nodoc:
      missing = names.reject { |n| opts.key?(n) }
      unless missing.empty?
        if missing.size == 1
          raise ArgumentError, "missing keyword: :#{missing[0]}"
        else
          raise ArgumentError, "missing keywords: #{missing.map { |n| ":#{n}" }.join(', ')}"
        end
      end
      names.map { |n| opts[n] }
    end
    private_class_method :__kw

    def self.pbkdf2_hmac(pass, **opts)
      salt, iterations, length, hash = send(:__kw, opts, :salt, :iterations, :length, :hash)
      hash = hash.name if hash.kind_of?(OpenSSL::Digest)
      __pbkdf2_hmac__(pass, salt, iterations, length, hash)
    end

    def self.scrypt(pass, **opts)
      salt, n, r, p, length = send(:__kw, opts, :salt, :N, :r, :p, :length)
      __scrypt__(pass, salt, n, r, p, length)
    end

    def self.hkdf(ikm, **opts)
      salt, info, length, hash = send(:__kw, opts, :salt, :info, :length, :hash)
      hash = hash.name if hash.kind_of?(OpenSSL::Digest)
      prk = OpenSSL::HMAC.digest(OpenSSL::Digest.new(hash), salt.to_s, ikm.to_s)
      hash_len = OpenSSL::Digest.new(hash).digest_length
      raise OpenSSL::KDF::KDFError, "length too large" if length > 255 * hash_len
      okm = "".b
      previous = "".b
      counter = 1
      while okm.bytesize < length
        previous = OpenSSL::HMAC.digest(OpenSSL::Digest.new(hash), prk,
                                        previous + info.to_s + counter.chr)
        okm << previous
        counter += 1
      end
      okm[0, length]
    end
  end

  # OpenSSL.secure_compare hashes first so that unequal lengths do not leak, and
  # then re-checks the originals for equality (ossl.c).
  def self.secure_compare(a, b)
    hashed_a = OpenSSL::Digest.digest("SHA256", a)
    hashed_b = OpenSSL::Digest.digest("SHA256", b)
    OpenSSL.fixed_length_secure_compare(hashed_a, hashed_b) && a == b
  end
end
