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

  # There is no cipher implementation behind this: every algorithm is
  # unsupported, which is what MRI reports for a name OpenSSL does not know.
  # The class exists so that code which names OpenSSL::Cipher::CipherError
  # (in a rescue clause, say) loads.
  class Cipher
    class CipherError < OpenSSLError; end

    def self.ciphers
      []
    end

    def initialize(name)
      raise CipherError, "unsupported cipher algorithm: #{name}"
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
