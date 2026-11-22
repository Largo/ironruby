# frozen_string_literal: true
# ext/-test-/integer in Ruby, which tool/lib/-test-/integer.rb requires as -test-/integer.so.
# CRuby keeps an Integer in the fixnum range as a Fixnum, but its C API can make a Bignum of the
# same value (to_bignum), so test_bignum checks that the two agree. IronRuby has one
# representation per value, so to_bignum answers the Integer itself and the tests check that
# IronRuby's arithmetic gives the same answers. fixnum? and bignum? answer by RbConfig::LIMITS,
# which is what CRuby's FIXNUM_P amounts to for a normalized Integer.
require "rbconfig/sizeof"

module Bug
  class Integer
    def self.fixnum?(i) = (RbConfig::LIMITS["FIXNUM_MIN"]..RbConfig::LIMITS["FIXNUM_MAX"]).cover?(i)
    def self.bignum?(i) = !fixnum?(i)
    def self.to_bignum(i) = i
    def self.positive_pow(x, y) = x ** y

    class MyInteger < ::Integer
      def self.new = raise(NotImplementedError, "Bug::Integer::MyInteger allocates an Integer through CRuby's C API")
    end
  end
end
