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

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.BigDecimal'

module Kernel
  # Kernel#BigDecimal takes a good deal more than a String. Integer and BigDecimal convert
  # exactly; Float and Rational have to be rounded to a number of significant digits, and a
  # Rational insists on being told how many (its expansion may never end). `exception: false`
  # turns every conversion failure into nil instead.
  #
  # The precision means different things per type, which is easy to get wrong by reasoning:
  # for a String it is only a lower bound on the storage and the digits are all kept, for a
  # BigDecimal it is ignored outright, and only for Float and Rational does it round.
  def BigDecimal(value, precision = 0, exception: true)
    precision = 0 if precision.nil?
    precision = Integer(precision) unless precision.kind_of?(Integer)
    raise ArgumentError, "negative precision" if precision < 0

    case value
    when BigDecimal
      value
    when Integer
      BigDecimal.new(value.to_s)
    when Float
      # MRI converts through the shortest round-tripping decimal form of the Float - not
      # through its exact binary value - which is why BigDecimal(1.235, 3) is 1.24 and not
      # the 1.23 that correctly rounding 1.2349999999999999866... would give.
      raise ArgumentError, "precision too large." if precision > Float::DIG + 1
      __bigdecimal_round(BigDecimal.new(value.to_s), precision)
    when Rational
      raise ArgumentError, "can't omit precision for a Rational." if precision.zero?
      BigDecimal.new(value.numerator.to_s).div(value.denominator, precision)
    when String
      BigDecimal.new(value)
    when nil
      raise TypeError, "can't convert nil into BigDecimal"
    else
      if value.respond_to?(:to_str)
        BigDecimal.new(value.to_str)
      else
        raise TypeError, "can't convert #{value.class} into BigDecimal"
      end
    end
  rescue ArgumentError, TypeError
    raise if exception
    nil
  end
  module_function :BigDecimal

  # Rounds to +precision+ significant digits. Zero, NaN and the infinities have no digits to
  # round, and #mult would lose the sign of a negative zero.
  def __bigdecimal_round(value, precision)
    return value if precision.zero? || !value.finite? || value.zero?
    value.mult(1, precision)
  end
  private :__bigdecimal_round
end

class BigDecimal
  # The generation of the bigdecimal API this implements, i.e. the one that ships with the
  # Ruby we target. It is not a version of IronRuby's own code: callers (ruby/spec among
  # them) read it to decide which semantics to expect, so it has to name the semantics we
  # actually follow - 4.0's Integer-returning #div and #divmod, ZeroDivisionError from #%
  # and #remainder, and the sign-dependent answers for an infinite divisor.
  VERSION = "4.0.1"

  NAN = BigDecimal("NaN")
  INFINITY = BigDecimal("Infinity")

  # A BigDecimal is immutable, so there is nothing for #dup to copy. MRI defines the two as
  # one method, which is what the spec checks with instance_method(:clone) == instance_method(:dup).
  def dup
    self
  end
  alias_method :clone, :dup

  # Every BigDecimal is a terminating decimal, so the exact Rational is just the digits over
  # the right power of ten. NaN and the infinities have no rational value at all and raise,
  # rather than answering something like (0/0).
  def to_r
    raise FloatDomainError, "Computation results in 'NaN' (Not a Number)" if nan?
    if (i = infinite?)
      raise FloatDomainError, "Computation results in '#{i > 0 ? '' : '-'}Infinity'"
    end

    sign, digits, _base, exponent = split
    numerator = sign * digits.to_i
    scale = exponent - digits.size
    scale >= 0 ? Rational(numerator * 10**scale, 1) : Rational(numerator, 10**(-scale))
  end

  # A Rational operand does not go through Rational#coerce: that would answer a pair of
  # Floats and quietly throw the extra digits away (and MRI's Rational#coerce refuses a
  # BigDecimal outright). MRI expands the Rational into a BigDecimal itself, so
  # BigDecimal op Rational stays a BigDecimal.
  #
  # A Rational may not have a terminating expansion, so a digit count has to be chosen:
  # enough to cover self, plus a Float's worth of guard digits, doubled.
  def __bigdecimal_expand(other)
    BigDecimal(other, [precision, BigDecimal.double_fig].max * 2)
  end
  private :__bigdecimal_expand

  # These have to be written with def rather than define_method: an alias of a
  # define_method'd method does not compare equal to it here, and #modulo has to stay
  # indistinguishable from #%.
  { :+ => :add, :- => :sub, :* => :mul, :/ => :quo, :% => :mod }.each do |operator, name|
    alias_method :"__#{name}_without_rationals", operator
    private :"__#{name}_without_rationals"
    class_eval <<-RUBY, __FILE__, __LINE__ + 1
      def #{operator}(other)
        other = __bigdecimal_expand(other) if Rational === other
        __#{name}_without_rationals(other)
      end
    RUBY
  end

  # #modulo is #%, not a copy of it - redefining #% above left #modulo pointing at the old
  # method, and BigDecimal.instance_method(:modulo) == BigDecimal.instance_method(:%) checks.
  alias_method :modulo, :%
end

# BigMath.exp and BigMath.log live in the bigdecimal extension itself, not in
# bigdecimal/math (which adds PI, E, sqrt and the trigonometric functions), so plain
# `require "bigdecimal"` has to provide them - and they take any Numeric, not only a
# BigDecimal. The series below are the ones bigdecimal/math has always used; what is new
# here is the argument conversion and the special values, which the 1.9 library got wrong
# (it raised for log(0) instead of answering -Infinity, and refused a Rational outright).
module BigMath
  module_function

  # Returns e raised to the power of +x+, to +prec+ digits of precision.
  def exp(x, prec)
    raise ArgumentError, "Zero or negative precision for exp" if prec <= 0
    x = __coerce_for_bigmath(x, prec)
    return BigDecimal("NaN") if x.nan?
    if (side = x.infinite?)
      return side > 0 ? BigDecimal("Infinity") : BigDecimal("0")
    end

    n = prec + BigDecimal.double_fig
    one = BigDecimal("1")
    x = -x if neg = x < 0
    x1 = one
    y = one
    d = y
    z = one
    i = 0
    while d.nonzero? && ((m = n - (y.exponent - d.exponent).abs) > 0)
      m = BigDecimal.double_fig if m < BigDecimal.double_fig
      x1 = x1.mult(x, n)
      i += 1
      z *= i
      d = x1.div(z, m)
      y += d
    end
    neg ? one.div(y, prec) : y.round(prec - y.exponent)
  end

  # Returns the natural logarithm of +x+, to +prec+ digits of precision.
  def log(x, prec)
    raise ArgumentError, "Zero or negative precision for log" if prec <= 0
    x = __coerce_for_bigmath(x, prec)
    return x if x.nan?
    raise Math::DomainError, "Negative argument for log" if x < 0
    return x if x.infinite?
    return BigDecimal("-Infinity") if x.zero?

    one = BigDecimal("1")
    two = BigDecimal("2")
    n = prec + BigDecimal.double_fig
    if (expo = x.exponent) < 0 || expo >= 3
      x = x.mult(BigDecimal("1E#{-expo}"), n)
    else
      expo = nil
    end
    x = (x - one).div(x + one, n)
    x2 = x.mult(x, n)
    y = x
    d = y
    i = one
    while d.nonzero? && ((m = n - (y.exponent - d.exponent).abs) > 0)
      m = BigDecimal.double_fig if m < BigDecimal.double_fig
      x = x2.mult(x, n)
      i += two
      d = x.div(i, m)
      y += d
    end
    y *= two
    y += log(BigDecimal("10"), prec) * BigDecimal(expo.to_s) if expo
    # The series is run with double_fig guard digits; the answer is only promised to prec.
    y.round(prec - y.exponent)
  end

  # A Float goes through its own shortest decimal form (asking for +prec+ digits of a Float
  # would be rejected as "precision too large"), while a Rational has to be told how many
  # digits of its expansion to produce.
  def __coerce_for_bigmath(x, prec)
    case x
    when BigDecimal then x
    when Integer then BigDecimal(x)
    when Float then BigDecimal(x, 0)
    when Rational then BigDecimal(x, prec)
    else raise ArgumentError, "#{x.inspect} can't be coerced into BigDecimal"
    end
  end

  class << self
    private :__coerce_for_bigmath
  end
end
