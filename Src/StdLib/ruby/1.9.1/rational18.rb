#
#   rational.rb -
#       $Release Version: 0.5 $
#       $Revision: 1.7 $
#       $Date: 1999/08/24 12:49:28 $
#       by Keiju ISHITSUKA(SHL Japan Inc.)
#
# Documentation by Kevin Jackson and Gavin Sinclair.
# 
# When you <tt>require 'rational'</tt>, all interactions between numbers
# potentially return a rational result.  For example:
#
#   1.quo(2)              # -> 0.5
#   require 'rational'
#   1.quo(2)              # -> Rational(1,2)
# 
# See Rational for full documentation.
#


#
# Creates a Rational number (i.e. a fraction).  +a+ and +b+ should be Integers:
# 
#   Rational(1,3)           # -> 1/3
#
# Note: trying to construct a Rational with floating point or real values
# produces errors:
#
#   Rational(1.1, 2.3)      # -> NoMethodError
#
module Kernel
  def Rational(a, b = 1)
    if a.kind_of?(Rational) && b == 1
      a
    else
      Rational.reduce(a, b)
    end
  end
end

#
# Rational implements a rational class for numbers.
#
# <em>A rational number is a number that can be expressed as a fraction p/q
# where p and q are integers and q != 0.  A rational number p/q is said to have
# numerator p and denominator q.  Numbers that are not rational are called
# irrational numbers.</em> (http://mathworld.wolfram.com/RationalNumber.html)
#
# To create a Rational Number:
#   Rational(a,b)             # -> a/b
#   Rational.new!(a,b)        # -> a/b
#
# Examples:
#   Rational(5,6)             # -> 5/6
#   Rational(5)               # -> 5/1
# 
# Rational numbers are reduced to their lowest terms:
#   Rational(6,10)            # -> 3/5
#
# But not if you use the unusual method "new!":
#   Rational.new!(6,10)       # -> 6/10
#
# Division by zero is obviously not allowed:
#   Rational(3,0)             # -> ZeroDivisionError
#
class Rational < Numeric
  @RCS_ID='-$Id: rational.rb,v 1.7 1999/08/24 12:49:28 keiju Exp keiju $-'

  #
  # Reduces the given numerator and denominator to their lowest terms.  Use
  # Rational() instead.
  #
  def Rational.reduce(num, den = 1)
    raise ZeroDivisionError, "denominator is zero" if den == 0

    if den < 0
      num = -num
      den = -den
    end
    gcd = num.gcd(den)
    num = num.div(gcd)
    den = den.div(gcd)
    if den == 1 && defined?(Unify)
      num
    else
      new!(num, den)
    end
  end

  #
  # Implements the constructor.  This method does not reduce to lowest terms or
  # check for division by zero.  Therefore #Rational() should be preferred in
  # normal use.
  #
  def Rational.new!(num, den = 1)
    new(num, den)
  end

  private_class_method :new

  #
  # This method is actually private.
  #
  def initialize(num, den)
    if den < 0
      num = -num
      den = -den
    end
    if num.kind_of?(Integer) and den.kind_of?(Integer)
      @numerator = num
      @denominator = den
    else
      @numerator = num.to_i
      @denominator = den.to_i
    end
  end

  #
  # Returns the addition of this value and +a+.
  #
  # Examples:
  #   r = Rational(3,4)      # -> Rational(3,4)
  #   r + 1                  # -> Rational(7,4)
  #   r + 0.5                # -> 1.25
  #
  # rb_num_coerce_bin: an argument that cannot coerce is a TypeError, not a
  # NoMethodError for #coerce.
  def __ir_coerce_bin__(other, op)
    unless other.respond_to?(:coerce)
      raise TypeError, "#{other.nil? ? 'nil' : other.class} can't be coerced into Rational"
    end
    x, y = other.coerce(self)
    x.__send__(op, y)
  end
  private :__ir_coerce_bin__

  def + (a)
    if a.kind_of?(Rational)
      num = @numerator * a.denominator
      num_a = a.numerator * @denominator
      Rational(num + num_a, @denominator * a.denominator)
    elsif a.kind_of?(Integer)
      self + Rational.new!(a, 1)
    elsif a.kind_of?(Float)
      Float(self) + a
    else
      __ir_coerce_bin__(a, :+)
    end
  end

  #
  # Returns the difference of this value and +a+.
  # subtracted.
  #
  # Examples:
  #   r = Rational(3,4)    # -> Rational(3,4)
  #   r - 1                # -> Rational(-1,4)
  #   r - 0.5              # -> 0.25
  #
  def - (a)
    if a.kind_of?(Rational)
      num = @numerator * a.denominator
      num_a = a.numerator * @denominator
      Rational(num - num_a, @denominator*a.denominator)
    elsif a.kind_of?(Integer)
      self - Rational.new!(a, 1)
    elsif a.kind_of?(Float)
      Float(self) - a
    else
      __ir_coerce_bin__(a, :-)
    end
  end

  #
  # Returns the product of this value and +a+.
  #
  # Examples:
  #   r = Rational(3,4)    # -> Rational(3,4)
  #   r * 2                # -> Rational(3,2)
  #   r * 4                # -> Rational(3,1)
  #   r * 0.5              # -> 0.375
  #   r * Rational(1,2)    # -> Rational(3,8)
  #
  def * (a)
    if a.kind_of?(Rational)
      num = @numerator * a.numerator
      den = @denominator * a.denominator
      Rational(num, den)
    elsif a.kind_of?(Integer)
      self * Rational.new!(a, 1)
    elsif a.kind_of?(Float)
      Float(self) * a
    else
      __ir_coerce_bin__(a, :*)
    end
  end

  #
  # Returns the quotient of this value and +a+.
  #   r = Rational(3,4)    # -> Rational(3,4)
  #   r / 2                # -> Rational(3,8)
  #   r / 2.0              # -> 0.375
  #   r / Rational(1,2)    # -> Rational(3,2)
  #
  def / (a)
    if a.kind_of?(Rational)
      num = @numerator * a.denominator
      den = @denominator * a.numerator
      Rational(num, den)
    elsif a.kind_of?(Integer)
      raise ZeroDivisionError, "division by zero" if a == 0
      self / Rational.new!(a, 1)
    elsif a.kind_of?(Float)
      Float(self) / a
    else
      __ir_coerce_bin__(a, :/)
    end
  end

  #
  # Returns this value raised to the given power.
  #
  # Examples:
  #   r = Rational(3,4)    # -> Rational(3,4)
  #   r ** 2               # -> Rational(9,16)
  #   r ** 2.0             # -> 0.5625
  #   r ** Rational(1,2)   # -> 0.866025403784439
  #
  def ** (other)
    # A whole number spelled as a Rational is still a whole number, and the answer
    # stays exact: Rational(2,3) ** Rational(2,1) is (4/9), not 0.444...
    other = other.numerator if other.kind_of?(Rational) && other.denominator == 1

    if other.kind_of?(Integer)
      if other > 0
	num = @numerator ** other
	den = @denominator ** other
      elsif other < 0
	# Turning the fraction over needs a numerator to divide by.
	raise ZeroDivisionError, "divided by 0" if @numerator == 0
	num = @denominator ** -other
	den = @numerator ** -other
	num, den = -num, -den if den < 0
      else
	num = 1
	den = 1
      end
      Rational.new!(num, den)
    elsif other.kind_of?(Rational) || other.kind_of?(Float)
      if @numerator == 0 && other.kind_of?(Rational)
	# Zero to a negative power is one divided by zero, and says so - but only when
	# the power was asked for exactly. A Float one answers Infinity, as Float
	# arithmetic does everywhere else.
	raise ZeroDivisionError, "divided by 0" if other < 0
	# Zero to a fraction of a power is still exactly zero.
	Rational.new!(0, 1)
      elsif self < 0 && Rational.__complex_power?(other)
	# A negative number raised to a power that is not whole leaves the real line.
	Rational.__negative_power__(self, other)
      else
	Float(self) ** other
      end
    else
      __ir_coerce_bin__(other, :**)
    end
  end

  #
  # Returns the remainder when this value is divided by +other+.
  #
  # Examples:
  #   r = Rational(7,4)    # -> Rational(7,4)
  #   r % Rational(1,2)    # -> Rational(1,4)
  #   r % 1                # -> Rational(3,4)
  #   r % Rational(1,7)    # -> Rational(1,28)
  #   r % 0.26             # -> 0.19
  #
  def % (other)
    # As in #divmod below: the quotient floors rather than truncating, so the remainder
    # takes the sign of the divisor.
    # num_div: a zero divisor, 0.0 included, is a ZeroDivisionError rather than
    # the FloatDomainError flooring Infinity or NaN would give.
    raise ZeroDivisionError, "divided by 0" if other == 0
    value = (self / other).floor
    return self - other * value
  end

  #
  # Returns the quotient _and_ remainder.
  #
  # Examples:
  #   r = Rational(7,4)        # -> Rational(7,4)
  #   r.divmod Rational(1,2)   # -> [3, Rational(1,4)]
  #
  def divmod(other)
    # The quotient floors, which is not what #to_i does - it truncates, so this answered
    # [-3, -1/2] for (-7/2).divmod(1) where MRI answers [-4, 1/2].
    # num_div: a zero divisor, 0.0 included, is a ZeroDivisionError rather than
    # the FloatDomainError flooring Infinity or NaN would give.
    raise ZeroDivisionError, "divided by 0" if other == 0
    value = (self / other).floor
    return value, self - other * value
  end

  #
  # Returns the absolute value.
  #
  def abs
    if @numerator > 0
      Rational.new!(@numerator, @denominator)
    else
      Rational.new!(-@numerator, @denominator)
    end
  end

  #
  # Returns +true+ iff this value is numerically equal to +other+.
  #
  # But beware:
  #   Rational(1,2) == Rational(4,8)          # -> true
  #   Rational(1,2) == Rational.new!(4,8)     # -> false
  #
  # Don't use Rational.new!
  #
  def == (other)
    if other.kind_of?(Rational)
      @numerator == other.numerator and @denominator == other.denominator
    elsif other.kind_of?(Integer)
      self == Rational.new!(other, 1)
    elsif other.kind_of?(Float)
      Float(self) == other
    elsif defined?(BigDecimal) and other.kind_of?(BigDecimal)
      # TODO:
      other == Float(self)
    else
      other == self
    end
  end

  #
  # Standard comparison operator.
  #
  def <=> (other)
    if other.kind_of?(Rational)
      num = @numerator * other.denominator
      num_a = other.numerator * @denominator
      v = num - num_a
      if v > 0
	return 1
      elsif v < 0
	return  -1
      else
	return 0
      end
    elsif other.kind_of?(Integer)
      return self <=> Rational.new!(other, 1)
    elsif other.kind_of?(Float)
      return Float(self) <=> other
    elsif defined? other.coerce
      x, y = other.coerce(self)
      return x <=> y
    else
      return nil
    end
  end

  def coerce(other)
    if other.kind_of?(Float)
      return other, self.to_f
    elsif other.kind_of?(Integer)
      return Rational.new!(other, 1), self
    else
      super
    end
  end

  #
  # Converts the rational to an Integer.  Not the _nearest_ integer, the
  # truncated integer.  Study the following example carefully:
  #   Rational(+7,4).to_i             # -> 1
  #   Rational(-7,4).to_i             # -> -2
  #   (-1.75).to_i                    # -> -1
  #
  # In other words:
  #   Rational(-7,4) == -1.75                 # -> true
  #   Rational(-7,4).to_i == (-1.75).to_i     # false
  #
  def to_i
    Integer(@numerator.div(@denominator))
  end

  #
  # Converts the rational to a Float.
  #
  def to_f
    # Rounding each side to a Float first loses the answer for anything past Float::MAX -
    # 10**400 over 10**399 is Infinity over Infinity - and costs a bit even when it does not.
    @numerator.fdiv(@denominator)
  end

  #
  # Returns a string representation of the rational number.
  #
  # Example:
  #   Rational(3,4).to_s          #  "3/4"
  #   Rational(8).to_s            #  "8"
  #
  def to_s
    if @denominator == 1
      @numerator.to_s
    else
      @numerator.to_s+"/"+@denominator.to_s
    end
  end

  #
  # Returns +self+.
  #
  def to_r
    self
  end

  #
  # Returns a reconstructable string representation:
  #
  #   Rational(5,8).inspect     # -> "Rational(5, 8)"
  #
  def inspect
    sprintf("Rational(%s, %s)", @numerator.inspect, @denominator.inspect)
  end

  #
  # Returns a hash code for the object.
  #
  def hash
    @numerator.hash ^ @denominator.hash
  end

  attr :numerator
  attr :denominator

  private :initialize
end

class Rational
  # Whether a negative number raised to this power leaves the real line. An infinite
  # power does not - it runs off to zero or to infinity along the reals - and a NaN
  # one does, carrying the NaN into both parts.
  def self.__complex_power?(x)
    return false if x.is_a?(Float) && x.infinite?
    !__whole__(x)
  end

  # Whether an exponent names a whole number, however it is spelled.
  def self.__whole__(x)
    case x
    when Integer  then true
    when Rational then x.denominator == 1
    when Float    then x.finite? && x == x.floor
    else false
    end
  end

  # A negative real sits at angle pi, so base ** w is |base| ** w turned through pi*w:
  # cos and sin of that are exact at the whole and half turns, which is why MRI answers
  # (-8) ** 0.5 with a real part of exactly 0.0 rather than 1.7e-16.
  def self.__negative_power__(base, other)
    w = other.to_f
    r = (-base).to_f ** w
    Complex.__raw__(r * __cospi__(w), r * __sinpi__(w))
  end

  def self.__cospi__(w)
    return w if w.nan?
    return (w.to_i.even? ? 1.0 : -1.0) if w == w.floor
    return 0.0 if (w * 2) == (w * 2).floor
    Math.cos(w * Math::PI)
  end

  def self.__sinpi__(w)
    return w if w.nan?
    return 0.0 if w == w.floor
    return ((w - 0.5).to_i.even? ? 1.0 : -1.0) if (w * 2) == (w * 2).floor
    Math.sin(w * Math::PI)
  end
end

class Float
  # Guarded the way Integer#** is below: aliasing a second time would make power!
  # the method that calls power!.
  unless method_defined?(:power!)
    alias power! **

    def ** (other)
      if self < 0 && other.is_a?(Numeric) && Rational.__complex_power?(other)
        # A negative number raised to a power that is not whole leaves the real line.
        return Rational.__negative_power__(self, other)
      end
      power!(other)
    end
  end
end

class Integer
  undef quo
  # If Rational is defined, returns a Rational number instead of an Integer.
  def quo(other)
    Rational.new!(self,1) / other
  end
  alias rdiv quo

  # Returns a Rational number if the result is in fact rational (i.e. +other+ < 0).
  def rpower (other)
    # `other >= 0` on something that is not a number at all reports the comparison
    # failing rather than the coercion failing: 2 ** "x" said "comparison of String
    # with 0 failed" where CRuby says "String can't be coerced into Integer".  Let the
    # builtin ** answer for anything non-numeric, which is where that message lives.
    return power!(other) unless other.is_a?(Numeric)

    # A whole number spelled as a Rational answers as a Rational: 2 ** Rational(2,1)
    # is (4/1), where 2 ** 2 is 4.
    if other.is_a?(Rational) && other.denominator == 1
      return Rational.new!(self, 1) ** other.numerator
    end

    if other.is_a?(Integer)
      return self.power!(other) if other >= 0
      # 0 ** -1 asks for one divided by zero, and says so.
      raise ZeroDivisionError, "divided by 0" if self == 0
      return Rational.new!(self, 1) ** other
    end

    # Anything else is a fraction of a power: a Float, or a Rational that is not whole.
    if self == 0
      raise ZeroDivisionError, "divided by 0" if other.is_a?(Rational) && other < 0
      # Zero to a fraction of a power is exactly zero - and stays a Rational if that
      # is how the power was asked for.
      return Rational.new!(0, 1) if other.is_a?(Rational) && other > 0
    elsif self < 0 && Rational.__complex_power?(other)
      # A negative number raised to a power that is not whole leaves the real line.
      return Rational.__negative_power__(self, other)
    end

    self.power!(other)
  end

  unless defined? 1.power!
    alias power! **
    alias ** rpower
  end
end

