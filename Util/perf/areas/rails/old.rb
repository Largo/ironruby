# Puts back the baseline's Random.urandom, HMAC#initialize and IronRubyOpenSSL__.bytes (A/B in one build).
class Random
  def self.urandom(count)
    File.open("/dev/urandom", "rb") { |f| f.read(count) }
  end
end
require "openssl"
class OpenSSL::HMAC
  def initialize(key, digest)
    @digest_name = OpenSSL::Digest.new(digest).name
    key = String.try_convert(key) or
      raise TypeError, "no implicit conversion of #{key.class} into String"
    block_length = OpenSSL::Digest.new(@digest_name).block_length
    key = OpenSSL::Digest.digest(@digest_name, key) if key.bytesize > block_length
    key = key.b + ("\x00".b * (block_length - key.bytesize))
    @inner_pad = key.each_byte.map { |b| (b ^ 0x36).chr }.join.b
    @outer_pad = key.each_byte.map { |b| (b ^ 0x5C).chr }.join.b
    reset
  end
end
module IronRubyOpenSSL__
  def self.bytes(array)
    array.to_a.map { |b| b.to_i }.pack("C*")
  end
end
