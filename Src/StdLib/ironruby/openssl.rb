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

    # The settings an SSLSocket is made with.  Only #verify_mode is acted on
    # (see openssl/ssl.rb): the TLS behind SSLSocket is .NET's SslStream, which
    # takes its trust anchors, protocol versions and cipher list from the
    # platform rather than from a context object.
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
    class DigestError < OpenSSLError; end unless const_defined?(:DigestError, false)

    # RIPEMD160, BLAKE2 and the SHA512/t truncations are deliberately absent:
    # .NET has no implementation and none of them is a few lines of Ruby.
    # SHA3-224 is absent because .NET bound only the other three SHA-3 sizes.
    # Whether the platform's crypto backend has SHA-3 is a runtime question, so
    # the constants are those the C# half actually accepts.
    names = %w[MD5 SHA1 SHA224 SHA256 SHA384 SHA512 SHA3-256 SHA3-384 SHA3-512].select do |name|
      begin
        Digest.new(name)
        true
      rescue OpenSSLError
        false
      end
    end

    names.each do |algorithm|
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

      # OpenSSL spells the SHA-3 algorithms with a hyphen; a constant cannot have
      # one, so the class is OpenSSL::Digest::SHA3_256 and #name is "SHA3-256".
      const_set(algorithm.tr("-", "_"), subclass)
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

  # HMAC (RFC 2104) over OpenSSL::Digest.  The construction is written out rather
  # than handed to IncrementalHash.CreateHMAC so that every digest OpenSSL::Digest
  # offers has an HMAC -- .NET's HMAC classes stop at SHA-2, and SHA-224 has no
  # .NET hash at all.
  class HMAC
    class HMACError < OpenSSLError; end

    def initialize(key, digest)
      @digest_name = Digest.new(digest).name
      key = String.try_convert(key) or
        raise TypeError, "no implicit conversion of #{key.class} into String"
      block_length = Digest.new(@digest_name).block_length
      key = Digest.digest(@digest_name, key) if key.bytesize > block_length
      key = key.b + ("\x00".b * (block_length - key.bytesize))
      @inner_pad = key.each_byte.map { |b| (b ^ 0x36).chr }.join.b
      @outer_pad = key.each_byte.map { |b| (b ^ 0x5C).chr }.join.b
      reset
    end

    def update(data)
      @inner.update(String.try_convert(data) ||
                    (raise TypeError, "no implicit conversion of #{data.class} into String"))
      self
    end
    alias_method :<<, :update

    def reset
      @inner = Digest.new(@digest_name)
      @inner.update(@inner_pad)
      self
    end

    def digest
      outer = Digest.new(@digest_name)
      outer.update(@outer_pad)
      outer.update(@inner.digest)
      outer.digest
    end

    def hexdigest
      digest.unpack1("H*")
    end
    alias_method :to_s, :hexdigest
    alias_method :inspect, :hexdigest

    def base64digest
      [digest].pack("m0")
    end

    def ==(other)
      other.is_a?(HMAC) && OpenSSL.fixed_length_secure_compare(digest, other.digest)
    end

    class << self
      def digest(digest, key, data)
        new(key, digest).update(data).digest
      end

      def hexdigest(digest, key, data)
        new(key, digest).update(data).hexdigest
      end

      def base64digest(digest, key, data)
        new(key, digest).update(data).base64digest
      end
    end
  end

  # The ASN.1 universal tag numbers X509::Name uses for its entry types.
  module ASN1
    EOC = 0
    BOOLEAN = 1
    INTEGER = 2
    BIT_STRING = 3
    OCTET_STRING = 4
    NULL = 5
    OBJECT = 6
    OBJECT_DESCRIPTOR = 7
    EXTERNAL = 8
    REAL = 9
    ENUMERATED = 10
    EMBEDDED_PDV = 11
    UTF8STRING = 12
    RELATIVE_OID = 13
    SEQUENCE = 16
    SET = 17
    NUMERICSTRING = 18
    PRINTABLESTRING = 19
    T61STRING = 20
    VIDEOTEXSTRING = 21
    IA5STRING = 22
    UTCTIME = 23
    GENERALIZEDTIME = 24
    GRAPHICSTRING = 25
    ISO64STRING = 26
    GENERALSTRING = 27
    UNIVERSALSTRING = 28
    CHARACTER_STRING = 29
    BMPSTRING = 30
  end unless defined?(ASN1)

  module X509
    class NameError < OpenSSLError; end

    # A distinguished name as a list of [short name, value, ASN.1 type]
    # entries, in Ruby; the C# half defines only the class. MRI keeps an
    # X509_NAME and lets OpenSSL resolve and print the attribute names; the
    # table below covers the attributes OpenSSL knows by name that turn up
    # in certificates, and dotted OIDs are accepted as they are.
    # DER (Name.new(der), #to_der) is not supported.
    class Name
      include Comparable

      COMPAT = 0
      RFC2253 = 17892119
      ONELINE = 8520479
      MULTILINE = 44302342

      DEFAULT_OBJECT_TYPE = ASN1::UTF8STRING
      OBJECT_TYPE_TEMPLATE = Hash.new(DEFAULT_OBJECT_TYPE).update(
        "C" => ASN1::PRINTABLESTRING,
        "countryName" => ASN1::PRINTABLESTRING,
        "serialNumber" => ASN1::PRINTABLESTRING,
        "dnQualifier" => ASN1::PRINTABLESTRING,
        "DC" => ASN1::IA5STRING,
        "domainComponent" => ASN1::IA5STRING,
        "emailAddress" => ASN1::IA5STRING
      )

      # [short name, long name, OID]
      ATTRIBUTES = [ # :nodoc:
        ["C", "countryName", "2.5.4.6"],
        ["ST", "stateOrProvinceName", "2.5.4.8"],
        ["L", "localityName", "2.5.4.7"],
        ["O", "organizationName", "2.5.4.10"],
        ["OU", "organizationalUnitName", "2.5.4.11"],
        ["CN", "commonName", "2.5.4.3"],
        ["SN", "surname", "2.5.4.4"],
        ["GN", "givenName", "2.5.4.42"],
        ["initials", "initials", "2.5.4.43"],
        ["title", "title", "2.5.4.12"],
        ["serialNumber", "serialNumber", "2.5.4.5"],
        ["street", "streetAddress", "2.5.4.9"],
        ["postalCode", "postalCode", "2.5.4.17"],
        ["postOfficeBox", "postOfficeBox", "2.5.4.18"],
        ["telephoneNumber", "telephoneNumber", "2.5.4.20"],
        ["dnQualifier", "dnQualifier", "2.5.4.46"],
        ["pseudonym", "pseudonym", "2.5.4.65"],
        ["generationQualifier", "generationQualifier", "2.5.4.44"],
        ["name", "name", "2.5.4.41"],
        ["description", "description", "2.5.4.13"],
        ["businessCategory", "businessCategory", "2.5.4.15"],
        ["organizationIdentifier", "organizationIdentifier", "2.5.4.97"],
        ["DC", "domainComponent", "0.9.2342.19200300.100.1.25"],
        ["UID", "userId", "0.9.2342.19200300.100.1.1"],
        ["emailAddress", "emailAddress", "1.2.840.113549.1.9.1"],
        ["unstructuredName", "unstructuredName", "1.2.840.113549.1.9.2"],
        ["jurisdictionL", "jurisdictionLocalityName", "1.3.6.1.4.1.311.60.2.1.1"],
        ["jurisdictionST", "jurisdictionStateOrProvinceName", "1.3.6.1.4.1.311.60.2.1.2"],
        ["jurisdictionC", "jurisdictionCountryName", "1.3.6.1.4.1.311.60.2.1.3"],
      ].freeze

      def self.__attribute(oid) # :nodoc:
        ATTRIBUTES.find { |attr| attr.include?(oid) } ||
          (oid.match?(/\A[0-2](\.\d+)+\z/) ? [oid, oid, oid] : nil)
      end

      def self.__string(value) # :nodoc:
        String.try_convert(value) or
          raise TypeError, "no implicit conversion of #{value.nil? ? 'nil' : value.class} into String"
      end

      class << self
        def parse_openssl(str, template = OBJECT_TYPE_TEMPLATE)
          if str.start_with?("/")
            # /A=B/C=D format
            ary = str[1..-1].split("/").map { |i| i.split("=", 2) }
          else
            # Comma-separated
            ary = str.split(",").map { |i| i.strip.split("=", 2) }
          end
          new(ary, template)
        end

        alias parse parse_openssl
      end

      def initialize(name = nil, template = OBJECT_TYPE_TEMPLATE)
        @entries = []
        return if name.nil?
        if name.is_a?(String)
          raise NotImplementedError, "OpenSSL::X509::Name.new(der) is not supported"
        end
        name.to_ary.each do |entry|
          unless entry.is_a?(Array)
            raise TypeError, "wrong argument type #{entry.class} (expected Array)"
          end
          oid, value, type = entry
          add_entry(oid, value, type || template[oid])
        end
      end

      def add_entry(oid, value, type = nil, loc: -1, set: 0)
        oid = Name.__string(oid)
        value = Name.__string(value)
        attr = Name.__attribute(oid) or
          raise NameError, "X509_NAME_add_entry_by_txt: invalid field name (name=#{oid})"
        type ||= OBJECT_TYPE_TEMPLATE[oid]
        entry = [attr, value.b.freeze, Integer(type)]
        if loc < 0
          @entries << entry
        else
          @entries.insert(loc, entry)
        end
        self
      end

      def to_a
        @entries.map { |attr, value, type| [attr[0], value.dup, type] }
      end

      def to_s(format = nil)
        case format
        when nil
          @entries.map { |attr, value| "/#{attr[0]}=#{__oneline_escape(value)}" }.join
        when COMPAT
          @entries.map { |attr, value| "#{attr[0]}=#{__oneline_escape(value)}" }.join(", ")
        when ONELINE
          @entries.map { |attr, value| "#{attr[0]} = #{__quote(value)}" }.join(", ")
        when MULTILINE
          @entries.map { |attr, value|
            label = attr[0] == attr[2] ? "#{attr[1]} " : attr[1].ljust(26)
            "#{label}= #{__multiline_escape(value)}"
          }.join("\n")
        else
          @entries.reverse.map { |attr, value| "#{attr[0]}=#{__rfc2253_escape(value, true)}" }.join(",")
        end
      end

      def to_utf8
        @entries.reverse.map { |attr, value| "#{attr[0]}=#{__rfc2253_escape(value, false)}" }.join(",").force_encoding(Encoding::UTF_8)
      end

      def inspect
        "#<#{self.class} #{to_s(RFC2253)}>"
      end

      def cmp(other)
        __compare_key <=> other.__compare_key
      end

      def <=>(other)
        other.is_a?(Name) ? cmp(other) : nil
      end

      def eql?(other)
        other.is_a?(Name) && cmp(other) == 0
      end

      def hash
        __compare_key.hash
      end

      def __compare_key # :nodoc:
        @entries.map { |attr, value, _| [attr[2], value] }
      end
      protected :__compare_key

      private

      def __oneline_escape(value)
        value.each_byte.map { |b|
          if b == 0x2B || b == 0x2F # + and /
            "\\" + b.chr
          elsif b < 0x20 || b >= 0x7F
            "\\x%02X" % b
          else
            b.chr
          end
        }.join
      end

      def __rfc2253_escape(value, escape_msb)
        bytes = value.bytes
        out = bytes.each_with_index.map { |b, i|
          c = b.chr
          if ",+\"\\<>;".include?(c) || (i == 0 && (c == "#" || c == " ")) || (i == bytes.size - 1 && c == " ")
            "\\" + c
          elsif b < 0x20 || b == 0x7F || (escape_msb && b >= 0x80)
            "\\%02X" % b
          else
            c
          end
        }
        out.join.b
      end

      def __multiline_escape(value)
        utf8 = value.dup.force_encoding(Encoding::UTF_8)
        chars = utf8.valid_encoding? ? utf8.each_char.map(&:ord) : value.bytes
        chars.map { |c|
          if c == 0x5C
            "\\\\"
          elsif c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0xFF)
            "\\%02X" % c
          elsif c > 0xFFFF
            "\\W%08X" % c
          elsif c > 0xFF
            "\\U%04X" % c
          else
            c.chr
          end
        }.join
      end

      def __quote(value)
        needs_quotes = value.match?(/[,+"\\<>;]/n) || value.start_with?("#", " ") || value.end_with?(" ")
        body = value.each_byte.map { |b|
          c = b.chr
          if needs_quotes && (c == "\"" || c == "\\")
            "\\" + c
          elsif !needs_quotes && ",+\"\\<>;".include?(c)
            "\\" + c
          elsif b < 0x20 || b >= 0x7F
            "\\%02X" % b
          else
            c
          end
        }.join
        needs_quotes ? "\"#{body}\"" : body
      end
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

# The certificate half of OpenSSL, on System.Security.Cryptography.X509Certificates:
# CertificateRequest builds and signs, X509Chain verifies.  The C# library defines
# only the error classes and X509::Name's placeholder; everything that needs the
# .NET certificate API is here, where the CLR types can be driven directly.
module IronRubyOpenSSL__ # :nodoc: all
  Kernel.load_assembly 'System.Security.Cryptography'

  X509 = ::System::Security::Cryptography::X509Certificates
  Crypto = ::System::Security::Cryptography
  EPOCH = ::System::DateTime.new(1970, 1, 1, 0, 0, 0, ::System::DateTimeKind.Utc)

  # A CLR byte[] as a binary Ruby String.
  def self.bytes(array)
    array.to_a.map { |b| b.to_i }.pack("C*")
  end

  # A Ruby String as a CLR byte[].  A Ruby String converts to both byte[] and
  # String, so an overload set that has one of each -- X509Certificate2's
  # constructor, say -- is ambiguous until the array is built explicitly.
  def self.clr_bytes(string)
    bytes = string.bytes
    array = ::System::Array.of(::System::Byte).new(bytes.size)
    bytes.each_with_index { |byte, index| array[index] = byte }
    array
  end

  # A CLR String as a Ruby String.  The .NET PEM writers hand back one of these
  # and it has to be a real Ruby String before anything indexes or matches it.
  def self.string(value)
    value.to_s
  end

  # An OpenSSL::X509::Name as an X500DistinguishedName.  Name#to_s(RFC2253) is
  # already the escaped, most-significant-last form the CLR parser reads; the
  # explicit to_clr_string picks the String overload of a constructor that also
  # takes a byte[], which a Ruby String converts to as well.
  def self.dn(name)
    unless name.kind_of?(::OpenSSL::X509::Name)
      raise TypeError, "wrong argument type #{name.class} (expected OpenSSL::X509::Name)"
    end
    X509::X500DistinguishedName.new(name.to_s(::OpenSSL::X509::Name::RFC2253).to_clr_string)
  end

  def self.hash_algorithm(digest)
    name = digest.respond_to?(:name) ? digest.name : digest.to_s
    case name.upcase
    when "SHA1" then Crypto::HashAlgorithmName.SHA1
    when "SHA256" then Crypto::HashAlgorithmName.SHA256
    when "SHA384" then Crypto::HashAlgorithmName.SHA384
    when "SHA512" then Crypto::HashAlgorithmName.SHA512
    else
      raise ::OpenSSL::X509::CertificateError, "unsupported signature digest: #{name}"
    end
  end

  def self.time(value, what)
    raise ::OpenSSL::X509::CertificateError, "#{what} is not set" if value.nil?
    seconds = value.kind_of?(::Time) ? value.to_f : Kernel.Float(value)
    ::System::DateTimeOffset.new(EPOCH.AddSeconds(seconds), ::System::TimeSpan.Zero)
  end

  # A serial number as the big-endian, unsigned byte string CertificateRequest#Create
  # wants.  MRI takes any non-negative Integer; zero still needs one byte.
  def self.serial_bytes(serial)
    value = Kernel.Integer(serial)
    raise ::OpenSSL::X509::CertificateError, "negative serial number" if value < 0
    hex = value.to_s(16)
    hex = "0" + hex if hex.length.odd?
    [hex].pack("H*")
  end
end

module OpenSSL
  module X509
    class ExtensionError < OpenSSLError; end unless const_defined?(:ExtensionError, false)
    class StoreError < OpenSSLError; end unless const_defined?(:StoreError, false)

    # A certificate extension.  The value is kept as the OpenSSL configuration
    # string it was created from; the DER lives in the CLR extension object.
    class Extension
      def initialize(clr, oid, value, critical) # :nodoc:
        @clr = clr
        @oid = oid
        @value = value
        @critical = critical
      end

      attr_reader :oid, :value

      def __clr # :nodoc:
        @clr
      end

      def critical?
        @critical
      end

      def to_s
        @value
      end

      def inspect
        "#<#{self.class} oid=#{@oid.inspect}, value=#{@value.inspect}, critical=#{@critical}>"
      end
    end

    # MRI's ExtensionFactory turns an "oid = value" pair from an OpenSSL config
    # file into DER, resolving "hash" and "keyid" against the two certificates it
    # was handed.  The CLR has a class per extension rather than a config parser,
    # so the handful of names certificates actually use are mapped by hand and
    # anything else is refused rather than silently encoded wrong.
    class ExtensionFactory
      attr_accessor :issuer_certificate, :subject_certificate,
                    :subject_request, :crl, :config

      def initialize(issuer_certificate = nil, subject_certificate = nil,
                     subject_request = nil, crl = nil)
        @issuer_certificate = issuer_certificate
        @subject_certificate = subject_certificate
        @subject_request = subject_request
        @crl = crl
      end

      def create_extension(*args)
        if args.size == 1 && args[0].kind_of?(Array)
          oid, value, critical = args[0]
        elsif args.size == 1
          oid, value = args[0].to_str.split("=", 2)
          oid = oid.strip
          value = value.to_s.strip
          if value.start_with?("critical,")
            critical = true
            value = value[9..-1].strip
          end
        else
          oid, value, critical = args
        end
        critical = !!critical
        Extension.new(__clr_extension(oid.to_s, value.to_s, critical), oid.to_s, value.to_s, critical)
      end

      def create_ext(*args)
        create_extension(*args)
      end

      KEY_USAGE = { # :nodoc:
        "digitalsignature" => "DigitalSignature",
        "nonrepudiation" => "NonRepudiation",
        "keyencipherment" => "KeyEncipherment",
        "dataencipherment" => "DataEncipherment",
        "keyagreement" => "KeyAgreement",
        "keycertsign" => "KeyCertSign",
        "crlsign" => "CrlSign",
        "encipheronly" => "EncipherOnly",
        "decipheronly" => "DecipherOnly",
      }.freeze

      private

      def __public_key_of(certificate, what)
        unless certificate.respond_to?(:public_key) && certificate.public_key
          raise ExtensionError, "a #{what} certificate with a public key is required"
        end
        IronRubyOpenSSL__::X509::PublicKey.new(certificate.public_key.__clr_key)
      end

      def __subject_key_identifier(certificate, what, critical)
        IronRubyOpenSSL__::X509::X509SubjectKeyIdentifierExtension.new(
          __public_key_of(certificate, what), critical)
      end

      def __clr_extension(oid, value, critical)
        case oid
        when "basicConstraints"
          ca = false
          path_length = 0
          has_path_length = false
          value.split(",").each do |part|
            key, val = part.strip.split(":", 2)
            case key
            when "CA" then ca = val.to_s.upcase == "TRUE"
            when "pathlen" then path_length = val.to_i; has_path_length = true
            end
          end
          IronRubyOpenSSL__::X509::X509BasicConstraintsExtension.new(ca, has_path_length, path_length, critical)
        when "keyUsage"
          flags = IronRubyOpenSSL__::X509::X509KeyUsageFlags.None
          value.split(",").each do |name|
            member = KEY_USAGE[name.strip.downcase] or
              raise ExtensionError, "unknown key usage: #{name.strip}"
            flags |= IronRubyOpenSSL__::X509::X509KeyUsageFlags.send(member)
          end
          IronRubyOpenSSL__::X509::X509KeyUsageExtension.new(flags, critical)
        when "subjectKeyIdentifier"
          unless value == "hash"
            raise ExtensionError, "only subjectKeyIdentifier=hash is supported"
          end
          __subject_key_identifier(@subject_certificate, "subject", critical)
        when "authorityKeyIdentifier"
          unless value.split(",").all? { |part| part.strip.start_with?("keyid") }
            raise ExtensionError, "only authorityKeyIdentifier=keyid is supported"
          end
          ski = __subject_key_identifier(@issuer_certificate, "issuer", false)
          IronRubyOpenSSL__::X509::X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(ski)
        else
          raise ExtensionError, "unsupported certificate extension: #{oid}"
        end
      end
    end

    # A certificate, built field by field the way MRI's is and turned into DER by
    # CertificateRequest at #sign time.  Everything before the signature is held
    # in Ruby; afterwards the signed X509Certificate2 is what #to_der, #to_pem and
    # Store#verify work from.
    class Certificate
      def initialize(data = nil)
        @version = 0
        @serial = 0
        @subject = Name.new
        @issuer = Name.new
        @public_key = nil
        @not_before = nil
        @not_after = nil
        @extensions = []
        @clr = nil
        __load(data) unless data.nil?
      end

      attr_accessor :version, :serial, :subject, :issuer, :public_key,
                    :not_before, :not_after

      def __clr # :nodoc:
        @clr
      end

      def extensions
        @extensions.dup
      end

      def extensions=(list)
        @extensions = list.to_a.dup
      end

      def add_extension(extension)
        @extensions << extension
        extension
      end

      def signed?
        !@clr.nil?
      end

      # CertificateRequest signs with the issuer's key: the subject's public key
      # goes into the request, the issuer's name and a signature generator over
      # the signing key into Create.
      def sign(key, digest)
        hash = IronRubyOpenSSL__.hash_algorithm(digest)
        case key
        when OpenSSL::PKey::RSA
          request = IronRubyOpenSSL__::X509::CertificateRequest.new(
            IronRubyOpenSSL__.dn(@subject),
            (@public_key || key.public_key).__clr_key,
            hash,
            IronRubyOpenSSL__::Crypto::RSASignaturePadding.Pkcs1)
          generator = IronRubyOpenSSL__::X509::X509SignatureGenerator.CreateForRSA(
            key.__clr_key, IronRubyOpenSSL__::Crypto::RSASignaturePadding.Pkcs1)
        when OpenSSL::PKey::EC
          # The subject's public key is the ECDsa object itself: CertificateRequest
          # takes a key, not a key pair, and only reads the public half of it.
          request = IronRubyOpenSSL__::X509::CertificateRequest.new(
            IronRubyOpenSSL__.dn(@subject),
            (@public_key || key).__clr_key,
            hash)
          generator = IronRubyOpenSSL__::X509::X509SignatureGenerator.CreateForECDsa(key.__clr_key)
        else
          raise CertificateError,
                "only OpenSSL::PKey::RSA and OpenSSL::PKey::EC keys can sign here"
        end
        @extensions.each { |extension| request.CertificateExtensions.Add(extension.__clr) }
        @clr = request.Create(IronRubyOpenSSL__.dn(@issuer), generator,
                              IronRubyOpenSSL__.time(@not_before, "not_before"),
                              IronRubyOpenSSL__.time(@not_after, "not_after"),
                              IronRubyOpenSSL__.serial_bytes(@serial))
        self
      end

      def to_der
        raise CertificateError, "certificate is not signed" if @clr.nil?
        IronRubyOpenSSL__.bytes(@clr.RawData)
      end

      def to_pem
        body = [to_der].pack("m0").scan(/.{1,64}/).join("\n")
        "-----BEGIN CERTIFICATE-----\n#{body}\n-----END CERTIFICATE-----\n"
      end
      alias_method :to_s, :to_pem

      def inspect
        "#<#{self.class} subject=#{@subject}, issuer=#{@issuer}, serial=#{@serial}, " \
          "not_before=#{@not_before.inspect}, not_after=#{@not_after.inspect}>"
      end

      private

      def __load(data)
        der = data.to_str
        if der.include?("-----BEGIN CERTIFICATE-----")
          body = der[/-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----/m, 1]
          der = body.to_s.unpack1("m")
        end
        @clr = IronRubyOpenSSL__::X509::X509Certificate2.new(IronRubyOpenSSL__.clr_bytes(der))
        @serial = @clr.SerialNumber.to_s.to_i(16)
        @version = @clr.Version - 1
        @subject = Name.parse_openssl(@clr.Subject.to_s)
        @issuer = Name.parse_openssl(@clr.Issuer.to_s)
        # UTC, as MRI answers.  This used Time.parse, which only exists once "time" is
        # required - and the `rescue nil` after it turned that NoMethodError into a
        # certificate with no validity period at all.
        @not_before = ::Time.at(::System::DateTimeOffset.new(@clr.NotBefore.ToUniversalTime).ToUnixTimeSeconds).utc
        @not_after = ::Time.at(::System::DateTimeOffset.new(@clr.NotAfter.ToUniversalTime).ToUnixTimeSeconds).utc
        self
      rescue ::System::Security::Cryptography::CryptographicException => error
        raise CertificateError, error.message
      end
    end

    # X509_STORE: a bag of certificates to build and check a chain against.
    # X509Chain does the building; the trust anchors are the self-signed
    # certificates in the bag, which is how OpenSSL's store behaves for the
    # chains these are used for -- a certificate whose issuer is also in the
    # store is verified through that issuer, not trusted on its own, so an
    # expired issuer still fails the certificate it signed.
    class Store
      attr_accessor :verify_callback, :time, :flags, :purpose, :trust
      attr_reader :error, :error_string, :chain

      def initialize
        @certificates = []
        @error = nil
        @error_string = nil
        @chain = nil
      end

      def add_cert(certificate)
        unless certificate.kind_of?(Certificate)
          raise TypeError, "wrong argument type #{certificate.class} (expected OpenSSL::X509::Certificate)"
        end
        @certificates << certificate
        self
      end

      def add_file(path)
        ::File.read(path).scan(/-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----/m).each do |pem|
          add_cert(Certificate.new(pem))
        end
        self
      end

      def add_path(path)
        ::Dir[::File.join(path, "*")].each { |file| add_file(file) if ::File.file?(file) }
        self
      end

      def set_default_paths
        self
      end

      def verify(certificate, chain = nil)
        clr = certificate.__clr
        if clr.nil?
          @chain = nil
          @error = 20
          @error_string = "unable to get local issuer certificate"
          return false
        end

        builder = IronRubyOpenSSL__::X509::X509Chain.new
        policy = builder.ChainPolicy
        policy.RevocationMode = IronRubyOpenSSL__::X509::X509RevocationMode.NoCheck
        policy.TrustMode = IronRubyOpenSSL__::X509::X509ChainTrustMode.CustomRootTrust
        policy.VerificationTime = IronRubyOpenSSL__.time(@time, "time") unless @time.nil?
        (@certificates + Array(chain)).each do |other|
          next if other.__clr.nil?
          policy.ExtraStore.Add(other.__clr)
          policy.CustomTrustStore.Add(other.__clr) if other.subject.eql?(other.issuer)
        end

        result = builder.Build(clr)
        @chain = result ? [certificate] : nil
        if result
          @error = 0
          @error_string = "ok"
        else
          @error, @error_string = __first_error(builder)
        end
        result
      end

      private

      # X509Chain reports a set of flags; MRI reports the X509_V_ERR_* code of the
      # first problem OpenSSL hit.  Only the codes these flags correspond to are
      # mapped, in roughly the order OpenSSL would notice them.
      def __first_error(builder)
        builder.ChainStatus.to_a.each do |status|
          case status.Status.to_s
          when "NotTimeValid", "CtlNotTimeValid"
            return [10, "certificate has expired"]
          when "NotSignatureValid"
            return [7, "certificate signature failure"]
          when "UntrustedRoot", "ExplicitDistrust"
            return [19, "self signed certificate in certificate chain"]
          when "InvalidBasicConstraints"
            return [24, "invalid CA certificate"]
          end
        end
        [20, "unable to get local issuer certificate"]
      end
    end
  end
end

module OpenSSL
  # MRI's name for HMAC's error class is OpenSSL::HMACError.
  HMACError = HMAC::HMACError unless const_defined?(:HMACError, false)

  # PKCS5 is the older, positional spelling of what OpenSSL::KDF answers with
  # keywords; openssl still ships it and code still calls it - ActiveSupport's
  # KeyGenerator, for one.  Both go to the same PBKDF2.
  module PKCS5
    def self.pbkdf2_hmac(pass, salt, iter, keylen, digest)
      digest = digest.name if digest.kind_of?(OpenSSL::Digest)
      OpenSSL::KDF.pbkdf2_hmac(pass.to_s, salt: salt.to_s, iterations: Integer(iter),
                               length: Integer(keylen), hash: digest)
    end

    def self.pbkdf2_hmac_sha1(pass, salt, iter, keylen)
      pbkdf2_hmac(pass, salt, iter, keylen, "SHA1")
    end
  end
end

# The rest of OpenSSL, each part on the piece of System.Security.Cryptography
# that corresponds to it.  They are separate files because each is a self
# contained translation of one of libcrypto's object models.
require 'openssl/bn'
require 'openssl/asn1'
require 'openssl/cipher'
require 'openssl/pkey'
require 'openssl/ssl'
