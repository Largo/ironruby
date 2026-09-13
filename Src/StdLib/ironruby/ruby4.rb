# Ruby 4 compatibility layer for IronRuby's 1.9-era core library.
# Pure Ruby, loaded from gem_prelude. Pattern-matching support classes plus
# widely-used core methods added between Ruby 2.0 and 4.0.

class NoMatchingPatternError < StandardError; end
class NoMatchingPatternKeyError < NoMatchingPatternError; end
class FrozenError < RuntimeError; end unless defined?(FrozenError)

class Object
  def then
    yield self
  end unless method_defined?(:then)
  alias_method :yield_self, :then unless method_defined?(:yield_self)

  def itself
    self
  end unless method_defined?(:itself)
end

module Kernel
  private

  def require_relative(path)
    caller_path = caller.first.split(/:\d/, 2).first
    require File.expand_path(path, File.dirname(caller_path))
  end unless private_method_defined?(:require_relative)
end

module Enumerable
  def filter_map
    result = []
    each { |x| v = yield(x); result << v if v }
    result
  end unless method_defined?(:filter_map)

  def tally
    result = Hash.new(0)
    each { |x| result[x] += 1 }
    result.default = nil
    result
  end unless method_defined?(:tally)

  # MRI does not simply fold with +: once a Float turns up it switches to
  # Kahan-Babuska compensated summation, which is why ([0.1] * 10).sum is
  # exactly 1.0 there and 0.9999999999999999 under a naive inject. Non-numeric
  # elements (String, Array) keep the plain fold.
  def sum(init = 0)
    acc = init
    compensation = 0.0
    compensating = false
    each do |*values|
      x = __enum_item__(values)
      x = yield(x) if block_given?
      unless compensating
        unless x.is_a?(Float) && (acc.is_a?(Integer) || acc.is_a?(Float) || acc.is_a?(Rational))
          acc = acc + x
          next
        end
        compensating = true
        acc = acc.to_f
      end
      unless x.is_a?(Integer) || x.is_a?(Float) || x.is_a?(Rational)
        # back out of float mode: settle the compensation before leaving it behind
        acc += compensation
        compensation = 0.0
        compensating = false
        acc = acc + x
        next
      end
      x = x.to_f
      t = acc + x
      compensation += acc.abs >= x.abs ? (acc - t) + x : (x - t) + acc
      acc = t
    end
    compensating ? acc + compensation : acc
  end unless method_defined?(:sum)

  alias_method :filter, :select unless method_defined?(:filter)

  # A `yield` with several values is one element, packed into an Array; a yield
  # with none is nil. Everything below goes through this so that
  # `def each; yield 1, 2; end` behaves the way the C# side now does.
  def __enum_item__(values)
    case values.size
    when 0 then nil
    when 1 then values[0]
    else values
    end
  end
  private :__enum_item__

  def each_entry(&block)
    return to_enum(:each_entry) unless block
    each { |*values| block.call(__enum_item__(values)) }
    self
  end unless method_defined?(:each_entry)

  # Ruby 2.5 gave the predicates an optional pattern, matched with #===.
  # The built-ins only know the block form, and because they are defined on
  # Enumerable they shadow rather than extend, so wrap them.
  unless method_defined?(:all_without_pattern?)
    alias_method :all_without_pattern?, :all?
    alias_method :any_without_pattern?, :any?
    alias_method :none_without_pattern?, :none?
    alias_method :one_without_pattern?, :one?

    def __check_pattern_args__(args)
      return if args.size <= 1
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
    end
    private :__check_pattern_args__

    def all?(*args, &block)
      __check_pattern_args__(args)
      return all_without_pattern?(&block) if args.empty?
      pattern = args[0]
      each { |*values| return false unless pattern === __enum_item__(values) }
      true
    end

    def any?(*args, &block)
      __check_pattern_args__(args)
      return any_without_pattern?(&block) if args.empty?
      pattern = args[0]
      each { |*values| return true if pattern === __enum_item__(values) }
      false
    end

    def none?(*args, &block)
      __check_pattern_args__(args)
      return none_without_pattern?(&block) if args.empty?
      pattern = args[0]
      each { |*values| return false if pattern === __enum_item__(values) }
      true
    end

    def one?(*args, &block)
      __check_pattern_args__(args)
      return one_without_pattern?(&block) if args.empty?
      pattern = args[0]
      found = false
      each do |*values|
        next unless pattern === __enum_item__(values)
        return false if found
        found = true
      end
      found
    end
  end

  # min/max also grew an `n` form.
  unless method_defined?(:min_without_count)
    alias_method :min_without_count, :min
    alias_method :max_without_count, :max

    def min(*args, &block)
      return min_without_count(&block) if args.empty?
      n = __count_arg__(args[0])
      sorted = block ? to_a.sort(&block) : to_a.sort
      sorted.first(n)
    end

    def max(*args, &block)
      return max_without_count(&block) if args.empty?
      n = __count_arg__(args[0])
      sorted = block ? to_a.sort(&block) : to_a.sort
      sorted.reverse.first(n)
    end
  end

  def group_by
    return to_enum(:group_by) unless block_given?
    result = {}
    each { |*values| item = __enum_item__(values); (result[yield(item)] ||= []) << item }
    result
  end unless method_defined?(:group_by)

  def each_with_object(memo)
    return to_enum(:each_with_object, memo) unless block_given?
    each { |*values| yield(__enum_item__(values), memo) }
    memo
  end unless method_defined?(:each_with_object)

  # Ruby 2.2 added the `n` form: the n smallest/largest, as an Array.
  def min_by(*args, &block)
    return to_enum(:min_by, *args) unless block
    if args.empty?
      best = nil
      best_key = nil
      each do |*values|
        item = __enum_item__(values)
        key = block.call(item)
        if best_key.nil? || (key <=> best_key) < 0
          best_key = key
          best = item
        end
      end
      best
    else
      __sort_by_key__(block).first(__count_arg__(args[0]))
    end
  end unless method_defined?(:min_by)

  def max_by(*args, &block)
    return to_enum(:max_by, *args) unless block
    if args.empty?
      best = nil
      best_key = nil
      each do |*values|
        item = __enum_item__(values)
        key = block.call(item)
        if best_key.nil? || (key <=> best_key) > 0
          best_key = key
          best = item
        end
      end
      best
    else
      __sort_by_key__(block).reverse.first(__count_arg__(args[0]))
    end
  end unless method_defined?(:max_by)

  def __count_arg__(n)
    n = n.to_int unless n.is_a?(Integer)
    raise ArgumentError, "negative size (#{n})" if n < 0
    n
  end
  private :__count_arg__

  # Sorts by the block's key, keeping input order for equal keys so that
  # `min_by(n)` and `max_by(n)` agree with MRI on ties.
  def __sort_by_key__(block)
    decorated = []
    index = 0
    each do |*values|
      item = __enum_item__(values)
      decorated << [block.call(item), index, item]
      index += 1
    end
    decorated.sort { |a, b| c = (a[0] <=> b[0]); c == 0 ? (a[1] <=> b[1]) : c }.map { |triple| triple[2] }
  end
  private :__sort_by_key__

  def minmax_by(&block)
    return to_enum(:minmax_by) unless block
    [min_by(&block), max_by(&block)]
  end unless method_defined?(:minmax_by)

  def flat_map
    return to_enum(:flat_map) unless block_given?
    result = []
    each do |*values|
      # Like #map, #flat_map hands the yielded values straight to the block
      # rather than the packed item: `yield 1, 2` reaches `{ |a| }` as 1.
      mapped = yield(*values)
      array = mapped.is_a?(Array) ? mapped : (mapped.respond_to?(:to_ary) ? mapped.to_ary : nil)
      array.is_a?(Array) ? result.concat(array) : result << mapped
    end
    result
  end unless method_defined?(:flat_map)
  alias_method :collect_concat, :flat_map unless method_defined?(:collect_concat)

  def uniq
    seen = {}
    result = []
    each do |*values|
      item = __enum_item__(values)
      key = block_given? ? yield(item) : item
      next if seen.key?(key)
      seen[key] = true
      result << item
    end
    result
  end unless method_defined?(:uniq)

  def compact
    result = []
    each { |*values| item = __enum_item__(values); result << item unless item.nil? }
    result
  end unless method_defined?(:compact)

  def to_h(*args)
    result = {}
    each do |*values|
      pair = block_given? ? yield(*values) : __enum_item__(values)
      array = pair.respond_to?(:to_ary) ? pair.to_ary : pair
      unless array.is_a?(Array)
        raise TypeError, "wrong element type #{pair.class} (expected array)"
      end
      unless array.size == 2
        raise ArgumentError, "element has wrong array length (expected 2, was #{array.size})"
      end
      result[array[0]] = array[1]
    end
    result
  end unless method_defined?(:to_h)

  def grep_v(pattern)
    result = []
    each do |*values|
      item = __enum_item__(values)
      next if pattern === item
      result << (block_given? ? yield(item) : item)
    end
    result
  end unless method_defined?(:grep_v)

  def chunk_while
    return to_enum(:chunk_while) unless block_given?
    result = []
    chunk = nil
    previous = nil
    each do |*values|
      item = __enum_item__(values)
      if chunk.nil?
        chunk = [item]
      elsif yield(previous, item)
        chunk << item
      else
        result << chunk
        chunk = [item]
      end
      previous = item
    end
    result << chunk if chunk
    result
  end unless method_defined?(:chunk_while)

  def slice_when(&block)
    return to_enum(:slice_when) unless block
    chunk_while { |a, b| !block.call(a, b) }
  end unless method_defined?(:slice_when)

  # A new group begins at every element the pattern or block accepts; the first
  # element always starts one, however it answers.
  def slice_before(*args, &block)
    if args.empty? == block.nil?
      raise ArgumentError, "both pattern and block are given" if block
      raise ArgumentError, "wrong number of arguments (given 0, expected 1)"
    end
    pattern = args[0]
    result = []
    group = nil
    each do |*values|
      item = __enum_item__(values)
      starts = block ? block.call(item) : (pattern === item)
      if group.nil?
        group = [item]
      elsif starts
        result << group
        group = [item]
      else
        group << item
      end
    end
    result << group if group
    result
  end unless method_defined?(:slice_before)

  def slice_after(*args, &block)
    if args.empty? == block.nil?
      raise ArgumentError, "both pattern and block are given" if block
      raise ArgumentError, "wrong number of arguments (given 0, expected 1)"
    end
    pattern = args[0]
    result = []
    group = []
    each do |*values|
      item = __enum_item__(values)
      group << item
      if block ? block.call(item) : (pattern === item)
        result << group
        group = []
      end
    end
    result << group unless group.empty?
    result
  end unless method_defined?(:slice_after)

  def reverse_each(&block)
    return to_enum(:reverse_each) { size if respond_to?(:size) } unless block
    to_a.reverse_each(&block)
    self
  end unless method_defined?(:reverse_each)

  def chunk
    return to_enum(:chunk) unless block_given?
    result = []
    key = nil
    chunk = nil
    each do |*values|
      item = __enum_item__(values)
      k = yield(item)
      if chunk && k == key
        chunk << item
      else
        result << [key, chunk] if chunk
        key = k
        chunk = [item]
      end
    end
    result << [key, chunk] if chunk
    result
  end unless method_defined?(:chunk)
end

class Range
  # The number of elements a numeric range iterates. Non-numeric beginnings
  # cannot be counted without walking the range, which MRI refuses to do.
  def size
    from = self.begin
    to = self.end
    raise TypeError, "can't iterate from #{from.class}" unless from.is_a?(Numeric)
    return Float::INFINITY if to.nil?
    return Float::INFINITY if to.is_a?(Float) && to.infinite? == 1
    span = to - from
    return 0 if span < 0
    # Half-open ranges lose the last element only when it lands exactly on the
    # end, so 1...3 has two elements but 1.0...3.5 still has three.
    if exclude_end? && span == span.floor
      span.to_i
    else
      span.floor.to_i + 1
    end
  end unless method_defined?(:size)

  # The built-in #first/#last only answer the no-argument form, and because they
  # are defined on Range they hide Enumerable#first(n) rather than falling
  # through to it - so `(0..Float::INFINITY).first(3)` was an ArgumentError.
  unless method_defined?(:first_without_count)
    alias_method :first_without_count, :first
    alias_method :last_without_count, :last

    def first(*args)
      return first_without_count if args.empty?
      n = args[0]
      n = n.to_int unless n.is_a?(Integer)
      raise ArgumentError, "negative array size (or size too big)" if n < 0
      result = []
      return result if n == 0
      each do |item|
        result << item
        break if result.size == n
      end
      result
    end

    def last(*args)
      return last_without_count if args.empty?
      n = args[0]
      n = n.to_int unless n.is_a?(Integer)
      raise ArgumentError, "negative array size (or size too big)" if n < 0
      to_a.last(n)
    end
  end
end

module Kernel
  private

  # shared tail of Array#dig / Hash#dig / Struct#dig: CRuby raises TypeError,
  # not NoMethodError, when an intermediate element has no #dig
  def __dig_step__(value, rest)
    return value if rest.empty? || value.nil?
    unless value.respond_to?(:dig)
      raise TypeError, "#{value.class} does not have #dig method"
    end
    value.dig(*rest)
  end

  # shared by Hash#to_h and Struct#to_h: validate the [key, value] pair a block
  # returned. Only #to_ary is honoured, never #to_a.
  def __to_h_pair__(pair)
    unless pair.is_a?(Array)
      pair = pair.to_ary if pair.respond_to?(:to_ary)
    end
    unless pair.is_a?(Array)
      raise TypeError, "wrong element type #{pair.class} (expected array)"
    end
    unless pair.size == 2
      raise ArgumentError, "element has wrong array length (expected 2, was #{pair.size})"
    end
    pair
  end
end

class Array
  def dig(key, *rest)
    __dig_step__(self[key], rest)
  end unless method_defined?(:dig)

  # No Array#sum here on purpose: Enumerable#sum already does the compensated
  # summation MRI does, and this was a second, naive copy that Array never
  # reached anyway - method_defined? saw the included Enumerable#sum and skipped it.

  def intersect?(other)
    !(self & other).empty?
  end unless method_defined?(:intersect?)

  def deconstruct
    self
  end unless method_defined?(:deconstruct)

  alias_method :filter, :select unless method_defined?(:filter)
  alias_method :filter!, :select! if method_defined?(:select!) && !method_defined?(:filter!)
  alias_method :append, :push unless method_defined?(:append)
  alias_method :prepend, :unshift unless method_defined?(:prepend)
end

class Hash
  def dig(key, *rest)
    __dig_step__(self[key], rest)
  end unless method_defined?(:dig)

  def transform_values(&block)
    return to_enum(:transform_values) unless block
    result = __result_hash__
    each { |k, v| result[k] = block.call(v) }
    result
  end unless method_defined?(:transform_values)

  def transform_values!(&block)
    return to_enum(:transform_values!) unless block
    raise FrozenError, "can't modify frozen Hash: #{inspect}" if frozen?
    keys.each { |k| self[k] = block.call(self[k]) }
    self
  end unless method_defined?(:transform_values!)

  # transform_keys(hash = nil) { |key| ... } - the hash argument (3.0) wins over
  # the block for the keys it contains.
  def transform_keys(*args, &block)
    mapping = __key_mapping__(args)
    return to_enum(:transform_keys) if mapping.nil? && block.nil?
    # CRuby drops the compare_by_identity flag here (but keeps it in #transform_values)
    result = {}
    each do |k, v|
      nk = if mapping && mapping.key?(k)
             mapping[k]
           elsif block
             block.call(k)
           else
             k
           end
      result[nk] = v
    end
    result
  end unless method_defined?(:transform_keys)

  def transform_keys!(*args, &block)
    mapping = __key_mapping__(args)
    return to_enum(:transform_keys!) if mapping.nil? && block.nil?
    raise FrozenError, "can't modify frozen Hash: #{inspect}" if frozen?
    # CRuby semantics: walk a snapshot of the pairs, deleting the old key only
    # if it has not already been produced as a new key (so `break` leaves the
    # already-processed prefix rewritten and the rest untouched).
    new_keys = {}
    to_a.each do |k, v|
      nk = if mapping && mapping.key?(k)
             mapping[k]
           elsif block
             block.call(k)
           else
             k
           end
      delete(k) unless new_keys.key?(k)
      self[nk] = v
      new_keys[nk] = nil
    end
    self
  end unless method_defined?(:transform_keys!)

  private def __key_mapping__(args)
    if args.size > 1
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
    end
    return nil if args.empty?
    mapping = args[0]
    return mapping if mapping.is_a?(Hash)
    mapping = mapping.to_hash if mapping.respond_to?(:to_hash)
    return mapping if mapping.is_a?(Hash)
    raise TypeError, "no implicit conversion of #{args[0].nil? ? 'nil' : args[0].class} into Hash"
  end

  def slice(*keys)
    result = {}
    keys.each { |k| result[k] = self[k] if key?(k) }
    result
  end unless method_defined?(:slice)

  # #except builds a plain Hash: CRuby does not carry the default value or the
  # default proc over to the result.
  def except(*keys)
    result = __result_hash__
    each { |k, v| result[k] = v }
    keys.each { |k| result.delete(k) }
    result
  end unless method_defined?(:except)

  def deconstruct_keys(keys)
    self
  end unless method_defined?(:deconstruct_keys)

  # unlike #reject, #compact keeps the default value and the default proc
  def compact
    result = dup
    result.delete_if { |_, v| v.nil? }
    result
    # instance_methods(false) rather than method_defined?: Hash includes Enumerable,
    # whose #compact is defined by this point and returns an Array of pairs, so
    # method_defined? is true and Hash would be left returning the wrong class.
  end unless instance_methods(false).include?(:compact)

  alias_method :filter, :select unless method_defined?(:filter)
end

class String
  def delete_prefix(prefix)
    start_with?(prefix) ? self[prefix.length..-1] : dup
  end unless method_defined?(:delete_prefix)

  def delete_suffix(suffix)
    end_with?(suffix) ? self[0...-suffix.length] : dup
  end unless method_defined?(:delete_suffix)

  alias_method :+@, :dup unless method_defined?(:+@)
end

module Comparable
  # Comparable#clamp (2.4; range form 2.4, beginless/endless 2.7).
  def clamp(*args)
    case args.size
    when 1
      range = args[0]
      unless range.is_a?(Range)
        raise TypeError, "wrong argument type #{range.class} (expected Range)"
      end
      min, max = range.begin, range.end
      if range.exclude_end? && !max.nil?
        raise ArgumentError, "cannot clamp with an exclusive range"
      end
    when 2
      min, max = args
    else
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)"
    end

    unless min.nil? || max.nil?
      c = (min <=> max)
      if c.nil? || c > 0
        raise ArgumentError, "min argument must be less than or equal to max argument"
      end
    end

    unless min.nil?
      c = (self <=> min)
      raise ArgumentError, "comparison of #{self.class} with #{min.inspect} failed" if c.nil?
      return min if c < 0
    end
    unless max.nil?
      c = (self <=> max)
      raise ArgumentError, "comparison of #{self.class} with #{max.inspect} failed" if c.nil?
      return max if c > 0
    end
    self
  end unless method_defined?(:clamp)
end

module Math
  # MRI raises Math::DomainError out of Math.sqrt/log/... and Integer#digits.
  class DomainError < StandardError; end unless const_defined?(:DomainError)

  class << self
    alias_method :__ir_frexp__, :frexp

    # frexp answered the magnitude for a negative argument - Math.frexp(-0.5)
    # came back [0.5, 0] where MRI says [-0.5, 0] - and 0.0 came back with the
    # smallest exponent instead of [0.0, 0].
    def frexp(value)
      value = ::Kernel.Float(value)
      return [value, 0] if value == 0.0 || value.nan? || value.infinite?
      fraction, exponent = __ir_frexp__(value.abs)
      [value < 0 ? -fraction : fraction, exponent]
    end
  end
end

class Integer
  # pow(n) is **, but pow(n, m) is modular exponentiation, which has to be done
  # by squaring rather than by computing the full power and then taking it mod m.
  def pow(other, modulo = nil)
    return self**other if modulo.nil?
    unless modulo.is_a?(::Integer) && other.is_a?(::Integer)
      ::Kernel.raise(::TypeError, "Integer#pow() 2nd argument not allowed unless all arguments are integers")
    end
    ::Kernel.raise(::RangeError, "Integer#pow() 1st argument cannot be negative when 2nd argument specified") if other < 0
    ::Kernel.raise(::ZeroDivisionError, "divided by 0") if modulo == 0
    negative = modulo < 0
    m = modulo.abs
    result = 1
    base = self % m
    exponent = other
    while exponent > 0
      result = (result * base) % m if exponent.odd?
      base = (base * base) % m
      exponent >>= 1
    end
    result = result - m if negative && result != 0
    result
  end unless method_defined?(:pow)

  def ceildiv(other)
    -(-self / other)
  end unless method_defined?(:ceildiv)

  # Newton's method on integers: the largest i with i*i <= n.
  def self.sqrt(n)
    unless n.is_a?(::Integer)
      unless n.respond_to?(:to_int)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{n.class} into Integer")
      end
      n = n.to_int
    end
    ::Kernel.raise(::Math::DomainError, 'Numerical argument is out of domain - "isqrt"') if n < 0
    return n if n < 2
    guess = 1 << ((n.bit_length + 1) / 2)
    loop do
      better = (guess + n / guess) / 2
      break if better >= guess
      guess = better
    end
    guess
  end unless respond_to?(:sqrt)

  def digits(base = 10)
    unless base.is_a?(Integer)
      unless base.respond_to?(:to_int)
        raise TypeError, "no implicit conversion of #{base.class} into Integer"
      end
      base = base.to_int
    end
    # MRI checks the receiver before the radix
    raise Math::DomainError, "out of domain" if negative?
    raise ArgumentError, "negative radix" if base < 0
    raise ArgumentError, "invalid radix #{base}" if base < 2
    return [0] if zero?
    result = []
    n = self
    while n > 0
      result << n % base
      n /= base
    end
    result
  end unless method_defined?(:digits)

  def positive?
    self > 0
  end unless method_defined?(:positive?)

  def negative?
    self < 0
  end unless method_defined?(:negative?)

end

class Struct
  def deconstruct
    to_a
  end unless method_defined?(:deconstruct)

  def deconstruct_keys(keys)
    result = {}
    members.each { |m| result[m] = self[m] }
    result
  end unless method_defined?(:deconstruct_keys)
end

module Kernel
  private

  # support for find patterns (`in [*pre, x, *post]`): returns the first index
  # at which the probe block matches, or nil
  def __pm_find_index__(arr, count)
    max = arr.length - count
    i = 0
    while i <= max
      return i if yield(i)
      i += 1
    end
    nil
  end
end

RUBY_ENGINE_VERSION = RUBY_VERSION unless defined?(RUBY_ENGINE_VERSION)
RUBY_COPYRIGHT = "ironruby - Apache License, Version 2.0" unless defined?(RUBY_COPYRIGHT)
RUBY_DESCRIPTION = "ironruby #{RUBY_VERSION} (.NET)" unless defined?(RUBY_DESCRIPTION)

class File
  def self.realpath(path, dir = nil)
    expand_path(path, dir)
  end unless respond_to?(:realpath)

  def self.realdirpath(path, dir = nil)
    expand_path(path, dir)
  end unless respond_to?(:realdirpath)

  # IO.write/File.write and friends arrived in 1.9; the C# IO library only has
  # the instance-level operations.  offset nil means truncate, an integer seeks
  # first and leaves the rest of the file intact, matching IO.write.
  def self.write(name, string, offset = nil, **opts)
    # "rb+" not "r+b": IronRuby's mode parser only accepts the letter before the
    # plus, though CRuby takes either order.
    mode = offset ? "rb+" : (opts[:mode] || "w")
    open(name, mode) do |io|
      io.seek(offset) if offset
      io.write(string)
    end
  end unless respond_to?(:write)

  def self.binwrite(name, string, offset = nil)
    write(name, string, offset, mode: "wb")
  end unless respond_to?(:binwrite)

  def self.binread(name, length = nil, offset = 0)
    open(name, "rb") do |io|
      io.seek(offset) if offset && offset > 0
      length ? io.read(length) : io.read
    end
  end unless respond_to?(:binread)

  def self.empty?(name)
    size(name) == 0
  end unless respond_to?(:empty?)
end

class IO
  def self.write(name, string, offset = nil, **opts)
    File.write(name, string, offset, **opts)
  end unless respond_to?(:write)

  def self.binwrite(name, string, offset = nil)
    File.binwrite(name, string, offset)
  end unless respond_to?(:binwrite)

  def self.binread(name, length = nil, offset = 0)
    File.binread(name, length, offset)
  end unless respond_to?(:binread)

  # Ruby 2.3 gave the non-blocking IO primitives an `exception: false`
  # keyword: instead of raising IO::WaitReadable / IO::WaitWritable / EOFError
  # they return :wait_readable / :wait_writable / nil.  net/protocol drives its
  # read buffer through this form.
  unless method_defined?(:__read_nonblock_raising__)
    alias_method :__read_nonblock_raising__, :read_nonblock

    def read_nonblock(len, buf = nil, exception: true)
      begin
        result = buf.nil? ? __read_nonblock_raising__(len) : __read_nonblock_raising__(len, buf)
      rescue IO::WaitReadable
        raise if exception
        return :wait_readable
      rescue EOFError
        raise if exception
        return nil
      end
      result
    end
  end

  unless method_defined?(:__write_nonblock_raising__)
    alias_method :__write_nonblock_raising__, :write_nonblock

    def write_nonblock(buf, exception: true)
      begin
        result = __write_nonblock_raising__(buf)
      rescue IO::WaitWritable
        raise if exception
        return :wait_writable
      end
      result
    end
  end
end

module Kernel
  private

  def __dir__
    File.dirname(File.expand_path(caller.first.split(/:\d/, 2).first))
  end unless private_method_defined?(:__dir__)
end

class Hash
  def compare_by_identity
    self
  end unless method_defined?(:compare_by_identity)

  def compare_by_identity?
    false
  end unless method_defined?(:compare_by_identity?)

  # a fresh result hash for the non-mutating combinators, carrying the
  # compare_by_identity flag over the way CRuby does
  private def __result_hash__
    compare_by_identity? ? {}.compare_by_identity : {}
  end
end

class Module
  def deprecate_constant(*names)
    names
  end unless method_defined?(:deprecate_constant)

  def private_constant(*names)
    names
  end unless method_defined?(:private_constant)

  def public_constant(*names)
    names
  end unless method_defined?(:public_constant)
end

class Object
  def singleton_class
    class << self
      self
    end
  end unless method_defined?(:singleton_class)
end

class Module
  begin
    public :remove_class_variable
  rescue NameError
  end
end

module Process
  def self.last_status
    $?
  end unless respond_to?(:last_status)
end

class Process::Status
  def signaled?
    false
  end unless method_defined?(:signaled?)

  def termsig
    nil
  end unless method_defined?(:termsig)

  def stopsig
    nil
  end unless method_defined?(:stopsig)

  def stopped?
    false
  end unless method_defined?(:stopped?)
end

class << IO
  # IO.popen(cmd, [mode,] opt) — the core method predates spawn options, so lower
  # the redirection options we support onto the command line (the shell handles them).
  alias_method :popen_without_options, :popen unless method_defined?(:popen_without_options)

  def popen(command, mode = nil, options = nil, &block)
    if mode.is_a?(Hash)
      options = mode
      mode = nil
    end

    if command.is_a?(Array)
      # IO.popen(["cmd", "arg", ...]) -- no shell is involved in MRI, so quote every
      # word before handing it to the shell the core popen does use.
      command = command.map { |word| "'" + word.to_s.gsub("'", %q{'\\\\''}) + "'" }.join(' ')
    end

    if options
      # brace-group so the redirect applies to the whole child, not just its last
      # command (MRI redirects the process's fd, not a single command's)
      if options[:err] == [:child, :out]
        command = "{ #{command}\n} 2>&1"
      end
      if options[:out] == [:child, :err]
        command = "{ #{command}\n} 1>&2"
      end
    end

    if mode
      popen_without_options(command, mode, &block)
    else
      popen_without_options(command, &block)
    end
  end
end

class File::Stat
  def world_writable?
    mode & 0002 == 0002 ? mode : nil
  end unless method_defined?(:world_writable?)

  def world_readable?
    mode & 0004 == 0004 ? mode : nil
  end unless method_defined?(:world_readable?)
end

class Array
  # 1.9 added the count form of shift; the bundled core only has the no-arg one
  unless (begin; [1].shift(1); true; rescue ArgumentError; false; end)
    alias_method :shift_without_count, :shift

    def shift(count = nil)
      return shift_without_count if count.nil?
      taken = self[0, count] || []
      self[0, count] = []
      taken
    end
  end
end

# --------------------------------------------------------------------------
# Module reflection: the `inherit` flag added in 1.9
# --------------------------------------------------------------------------

class Module
  # `mod.method_defined?(name, inherit = true)`. The bundled core only accepts
  # one argument, so wrap it: inherit == true keeps the old behaviour, and
  # inherit == false restricts the lookup to methods defined directly on `mod`
  # (instance_methods(false) returns the public *and* protected ones, which is
  # exactly the set method_defined? reports).
  unless (begin; Module.method_defined?(:name, true); true; rescue ArgumentError; false; end)
    alias_method :method_defined_without_inherit?, :method_defined?

    def method_defined?(name, inherit = true)
      return method_defined_without_inherit?(name) if inherit
      instance_methods(false).include?(name.to_sym)
    end
  end

  unless (begin; Module.public_method_defined?(:name, true); true; rescue ArgumentError; false; end)
    alias_method :public_method_defined_without_inherit?, :public_method_defined?

    def public_method_defined?(name, inherit = true)
      return public_method_defined_without_inherit?(name) if inherit
      public_instance_methods(false).include?(name.to_sym)
    end
  end

  unless (begin; Module.private_method_defined?(:name, true); true; rescue ArgumentError; false; end)
    alias_method :private_method_defined_without_inherit?, :private_method_defined?

    def private_method_defined?(name, inherit = true)
      return private_method_defined_without_inherit?(name) if inherit
      private_instance_methods(false).include?(name.to_sym)
    end
  end

  unless (begin; Module.protected_method_defined?(:name, true); true; rescue ArgumentError; false; end)
    alias_method :protected_method_defined_without_inherit?, :protected_method_defined?

    def protected_method_defined?(name, inherit = true)
      return protected_method_defined_without_inherit?(name) if inherit
      protected_instance_methods(false).include?(name.to_sym)
    end
  end

  unless (begin; Module.const_defined?(:Module, true); true; rescue ArgumentError; false; end)
    alias_method :const_defined_without_inherit?, :const_defined?

    def const_defined?(name, inherit = true)
      return const_defined_without_inherit?(name) if inherit
      constants(false).include?(name.to_s.to_sym)
    end
  end

  unless (begin; Module.const_get(:Module, true); true; rescue ArgumentError; false; end)
    alias_method :const_get_without_inherit, :const_get

    def const_get(name, inherit = true)
      return const_get_without_inherit(name) if inherit
      sym = name.to_s.to_sym
      unless constants(false).include?(sym)
        raise NameError, "uninitialized constant #{self}::#{sym}"
      end
      const_get_without_inherit(name)
    end
  end
end

# --------------------------------------------------------------------------
# Object#remove_instance_variable became public in 1.9
# --------------------------------------------------------------------------

class Object
  begin
    public :remove_instance_variable
  rescue NameError
  end
end

# --------------------------------------------------------------------------
# TRUE / FALSE / NIL were removed in Ruby 3.0
# --------------------------------------------------------------------------

[:TRUE, :FALSE, :NIL].each do |__name__|
  begin
    Object.send(:remove_const, __name__) if Object.const_defined?(__name__)
  rescue NameError
  end
end

# --------------------------------------------------------------------------
# The Warning module (2.0 for #warn, 2.7 for the category switches)
# --------------------------------------------------------------------------

module Warning
  CATEGORIES = [
    :deprecated, :experimental, :performance, :strict_unused_block
  ] unless defined?(CATEGORIES)

  @categories = { :deprecated => false, :experimental => true,
                  :performance => false, :strict_unused_block => false }

  # NB: Module#[] already exists in IronRuby (CLR generic instantiation), so the
  # guard has to look at Warning's own singleton methods, not respond_to?.
  unless singleton_methods(false).include?(:[])
    def self.[](category)
      unless CATEGORIES.include?(category)
        raise ArgumentError, "unknown category: #{category}"
      end
      @categories[category] ? true : false
    end
  end

  unless singleton_methods(false).include?(:[]=)
    def self.[]=(category, flag)
      unless CATEGORIES.include?(category)
        raise ArgumentError, "unknown category: #{category}"
      end
      @categories[category] = flag
      flag
    end
  end

  # Warning.warn is the single hook every Kernel#warn call funnels through in
  # CRuby; user code overrides it to filter or redirect warnings.
  def warn(message, *rest)
    $stderr.write(message)
    nil
  end unless method_defined?(:warn)

  extend self
end

# --------------------------------------------------------------------------
# Encoding constants
#
# CRuby defines a constant for every encoding name and alias; the bundled core
# only defines a handful. Reproduce CRuby's set_encoding_const() naming rules
# over the names the runtime actually knows about.
# --------------------------------------------------------------------------

class Encoding
  __seen__ = {}
  constants(false).each { |__c__| __seen__[__c__.to_s] = true }

  __add__ = lambda do |__name__|
    next if __name__ =~ /\A[0-9]/
    __enc__ = begin
                Encoding.find(__name__)
              rescue StandardError
                nil
              end
    next unless __enc__.is_a?(Encoding)

    __consts__ = []
    __consts__ << __name__ if __name__ =~ /\A[A-Z][A-Za-z0-9_]*\z/
    __custom__ = __name__.gsub(/[^A-Za-z0-9]/, '_')
    if __consts__.empty? || __custom__ =~ /[a-z]/
      __consts__ << __custom__ if __custom__ =~ /[A-Z]/
      __consts__ << __custom__.upcase if __custom__ =~ /[a-z]/
    end

    __consts__.each do |__c__|
      next unless __c__ =~ /\A[A-Z][A-Za-z0-9_]*\z/
      next if __seen__[__c__]
      __seen__[__c__] = true
      begin
        const_set(__c__, __enc__)
      rescue StandardError
      end
    end
  end

  begin
    Encoding.name_list.each { |__n__| __add__.call(__n__) }
  rescue StandardError
  end

  # Names CRuby knows that are missing from (or spelled differently in) the
  # runtime's own name list. Unsupported ones are silently skipped.
  %w[
    ISO-8859-1 ISO-8859-2 ISO-8859-3 ISO-8859-4 ISO-8859-5 ISO-8859-6
    ISO-8859-7 ISO-8859-8 ISO-8859-9 ISO-8859-10 ISO-8859-11 ISO-8859-13
    ISO-8859-14 ISO-8859-15 ISO-8859-16
    Windows-31J Windows-874
    Windows-1250 Windows-1251 Windows-1252 Windows-1253 Windows-1254
    Windows-1255 Windows-1256 Windows-1257 Windows-1258
    CP437 CP737 CP775 CP850 CP852 CP855 CP857 CP860 CP861 CP862 CP863
    CP864 CP865 CP866 CP869 CP932 CP936 CP949 CP950 CP1252
    IBM437 IBM737 IBM775 IBM852 IBM857 IBM861 IBM862 IBM866 IBM869
    KOI8-R KOI8-U GB18030 GBK Big5 EUC-JP EUC-KR EUC-CN
    macRoman macCentEuro macCroatian macCyrillic macGreek macIceland
    macRomania macThai macTurkish macUkraine
    ASCII-8BIT BINARY US-ASCII UTF-8 UTF-16 UTF-32 UTF-16BE UTF-16LE
    UTF-32BE UTF-32LE Shift_JIS SJIS
  ].each { |__n__| __add__.call(__n__) }
end

# --------------------------------------------------------------------------
# $LOAD_PATH.resolve_feature_path (2.5)
# --------------------------------------------------------------------------

if defined?($LOAD_PATH) && $LOAD_PATH.is_a?(Array) &&
   !$LOAD_PATH.respond_to?(:resolve_feature_path)
  def $LOAD_PATH.resolve_feature_path(feature)
    feature = feature.to_str if !feature.is_a?(String) && feature.respond_to?(:to_str)
    raise TypeError, "no implicit conversion into String" unless feature.is_a?(String)

    rb_exts = ['.rb']
    so_exts = ['.so', '.dll', '.dylib', '.bundle']

    check = lambda do |path|
      so_exts.each do |ext|
        return [:so, path] if path.end_with?(ext) && File.file?(path)
      end
      return [:rb, path] if path.end_with?('.rb') && File.file?(path)
      rb_exts.each do |ext|
        return [:rb, path + ext] if File.file?(path + ext)
      end
      so_exts.each do |ext|
        return [:so, path + ext] if File.file?(path + ext)
      end
      nil
    end

    if feature =~ /\A(?:[\/~]|\.\.?\/|[a-zA-Z]:[\/\\])/
      return check.call(File.expand_path(feature))
    end

    each do |dir|
      next unless dir.is_a?(String)
      found = check.call(File.expand_path(feature, dir))
      return found if found
    end
    nil
  end
end

# `.` has not been on the default load path since 1.9.2.
$LOAD_PATH.delete('.') if defined?($LOAD_PATH) && $LOAD_PATH.is_a?(Array)

# --------------------------------------------------------------------------
# Object#clone(freeze:) (2.4)
# --------------------------------------------------------------------------

class Object
  unless (begin; Object.new.clone(:freeze => nil); true; rescue ArgumentError; false; end)
    alias_method :clone_without_options, :clone

    # `clone(freeze: nil)` is the default (copy the frozen state), `freeze: true`
    # always freezes, `freeze: false` never does. The runtime's #clone always
    # copies the frozen flag and there is no way to thaw an object from Ruby, so
    # `freeze: false` on an already-frozen receiver falls back to #dup, which
    # differs from #clone only in that it does not carry over the singleton
    # class.
    def clone(opts = nil)
      freeze_opt = nil
      if opts.is_a?(Hash)
        extra = opts.keys - [:freeze]
        unless extra.empty?
          raise ArgumentError, "unknown keyword: #{extra.first.inspect}"
        end
        freeze_opt = opts[:freeze]
        unless freeze_opt.nil? || freeze_opt == true || freeze_opt == false
          raise ArgumentError,
                "unexpected value for freeze: #{freeze_opt.class}"
        end
      elsif !opts.nil?
        raise TypeError, "no implicit conversion of #{opts.class} into Hash"
      end

      if freeze_opt == false && frozen?
        dup
      elsif freeze_opt == true
        copy = clone_without_options
        copy.freeze
        copy
      else
        clone_without_options
      end
    end
  end
end

# --------------------------------------------------------------------------
# Fiber
#
# .NET has no coroutines, so every Fiber is backed by a dedicated thread plus a
# one-slot mailbox. Thread.new marks CLR threads IsBackground, so an abandoned
# fiber can never keep the process alive. Exactly one fiber of a group ever
# runs: switching means "post to the target's mailbox, then block on my own",
# which is the SemaphoreSlim handshake spelled with Queue (Queue is a
# Monitor-based blocking queue here, so the wait is a real kernel wait).
#
# resume / Fiber.yield / transfer / kill / raise / storage / scheduler follow
# MRI, including the rule that a fiber which was entered by #transfer returns,
# when it terminates, to the deepest fiber of the root's resume chain.
#
# A killed fiber unwinds with Kernel#throw rather than an exception, so ensure
# clauses run but "rescue Exception" does not see it - what MRI guarantees.
#
# Known gap: a fiber that is suspended and then dropped keeps one *background*
# thread parked on its mailbox until the process exits. MRI reclaims the fiber
# stack at GC; we cannot, because the running thread must hold the Fiber object
# strongly for Fiber.current. Nothing leaks past process exit and nothing
# deadlocks, but long-running programs that abandon many fibers pay a thread.
# --------------------------------------------------------------------------

class FiberError < StandardError; end unless defined?(FiberError)

unless defined?(Fiber)
  class Fiber
    # Kernel#throw tag used to unwind a killed fiber.
    KILL_TAG = :__ironruby_fiber_kill__

    SCHEDULER_METHODS = [:block, :unblock, :kernel_sleep, :io_wait]

    # ---- helpers ---------------------------------------------------------

    def self.__err__(cls, msg)
      ::Kernel.raise(cls, msg)
    end

    def self.__mailbox__
      require 'thread' unless defined?(::Queue)
      ::Queue.new
    end

    # Storage keys are Symbols; anything with #to_str (but not #to_sym) is
    # converted, which is what MRI does.
    def self.__check_key__(key)
      return key if key.is_a?(::Symbol)
      if key.respond_to?(:to_str)
        str = key.to_str
        return str.to_sym if str.is_a?(::String)
      end
      __err__(::TypeError, "wrong argument type #{key.class} (expected Symbol)")
    end

    # Exactly Kernel#raise's argument handling (including cause:) but returning the
    # exception instead of throwing it, so that it can be handed to the target fiber.
    # The cause is resolved here, i.e. in the calling fiber's context, which is what
    # MRI 4.0 does.
    def self.__make_exception__(args)
      __build_exception__(*args)
    end

    def self.__init_storage__(storage, parent)
      case storage
      when true
        inherited = parent.__storage_raw__
        inherited ? inherited.dup : nil
      when nil
        nil
      when ::Hash
        __err__(::FrozenError, "can't modify frozen Hash") if storage.frozen?
        copy = {}
        storage.each { |k, v| copy[__check_key__(k)] = v }
        copy
      else
        __err__(::TypeError, "storage must be a hash")
      end
    end

    # ---- the root fiber of a thread --------------------------------------

    def self.current
      Thread.current[:__ir_fiber_current__] || __root__
    end

    def self.__root__
      t = Thread.current
      f = t[:__ir_fiber_root__]
      unless f
        f = allocate
        f.__init_root__
        t[:__ir_fiber_root__] = f
        t[:__ir_fiber_current__] = f
      end
      f
    end

    def __init_root__
      @mailbox     = Fiber.__mailbox__
      @status      = :resumed
      @alive       = true
      @root        = true
      @blocking    = true
      @prev        = nil
      @resuming    = nil
      @transferred = false
      @killing     = false
      @yielded     = false
      @storage     = nil
      @location    = nil
      @scheduler   = nil
      @thread      = Thread.current
      @root_fiber  = self
      self
    end

    # ---- construction ----------------------------------------------------

    def initialize(blocking: false, storage: true, &block)
      Fiber.__err__(::ArgumentError, "tried to create Proc object without a block") unless block
      cur          = Fiber.current
      @block       = block
      @blocking    = blocking ? true : false
      @storage     = Fiber.__init_storage__(storage, cur)
      @mailbox     = Fiber.__mailbox__
      @status      = :created
      @alive       = true
      @root        = false
      @prev        = nil
      @resuming    = nil
      @transferred = false
      @killing     = false
      @yielded     = false
      @thread      = nil
      @scheduler   = nil
      @root_fiber  = cur.__root_fiber__
      loc          = (block.source_location rescue nil)
      @location    = loc ? "#{loc[0]}:#{loc[1]}" : nil
    end

    # ---- internal accessors (used across fibers of the same group) --------

    def __root_fiber__; @root_fiber; end
    def __thread__; @thread; end
    def __storage_raw__; @storage; end
    def __resuming__; @resuming; end
    def __set_resuming__(f); @resuming = f; end
    def __set_status__(s); @status = s; end
    def __set_blocking__(b); @blocking = b; end
    def __set_transferred__; @transferred = true; end
    def __post__(msg); @mailbox.push(msg); end
    def __scheduler_get__; @scheduler; end
    def __scheduler_set__(s); @scheduler = s; end

    def __storage_store__(key, value)
      # MRI deletes the entry rather than storing a nil
      if value.nil?
        @storage.delete(key) if @storage
      else
        @storage ||= {}
        @storage[key] = value
      end
      value
    end

    # ---- public API ------------------------------------------------------

    def alive?
      @alive
    end

    def blocking?
      @blocking
    end

    def inspect
      state = case @status
              when :created    then "created"
              when :suspended  then "suspended"
              when :terminated then "terminated"
              else                  "resumed"
              end
      id = "%016x" % (object_id.abs << 1)
      @location ? "#<Fiber:0x#{id} #{@location} (#{state})>" : "#<Fiber:0x#{id} (#{state})>"
    end

    alias_method :to_s, :inspect

    def storage
      unless Fiber.current.equal?(self)
        Fiber.__err__(::ArgumentError, "Fiber storage can only be accessed from the Fiber it belongs to")
      end
      (@storage || {}).dup
    end

    def storage=(hash)
      unless Fiber.current.equal?(self)
        Fiber.__err__(::ArgumentError, "Fiber storage can only be accessed from the Fiber it belongs to")
      end
      @storage = Fiber.__init_storage__(hash, self)
      hash
    end

    # A fiber may only be driven from the thread that owns its group; otherwise
    # the handoff would post to a mailbox nobody is waiting on and deadlock.
    def __check_thread__(cur)
      unless cur.__root_fiber__.equal?(@root_fiber)
        Fiber.__err__(::FiberError, "fiber called across threads")
      end
    end

    def resume(*args)
      cur = Fiber.current
      Fiber.__err__(::FiberError, "attempt to resume the current fiber") if cur.equal?(self)
      __check_thread__(cur)
      Fiber.__err__(::FiberError, "attempt to resume a terminated fiber") unless @alive
      Fiber.__err__(::FiberError, "double resume") if @prev
      Fiber.__err__(::FiberError, "attempt to resume a resuming fiber") if @resuming
      @prev = cur
      cur.__set_resuming__(self)
      Fiber.__switch__(cur, self, [:resume, args])
    end

    def transfer(*args)
      cur = Fiber.current
      return nil if cur.equal?(self)
      __check_thread__(cur)
      Fiber.__err__(::FiberError, "attempt to transfer to a resuming fiber") if @resuming || @yielded
      Fiber.__err__(::FiberError, "dead fiber called") unless @alive
      cur.__set_transferred__
      Fiber.__switch__(cur, self, [:transfer, args])
    end

    def kill
      cur = Fiber.current
      return self unless @alive
      @killing = true

      if cur.equal?(self)
        throw(KILL_TAG) unless @root
        return self
      end

      __check_thread__(cur)

      unless @thread
        # never entered: there is no stack to unwind and no ensure to run
        @alive = false
        @status = :terminated
        return self
      end

      # An ancestor that is busy resuming somebody cannot be unwound now; the
      # throw happens as soon as control comes back to it (MRI does the same).
      return self if @resuming

      __adopt__(cur)
      Fiber.__switch__(cur, self, [:kill])
      self
    end

    # Makes `cur` the fiber control returns to when we finish - unless somebody
    # is already waiting for us, in which case that fiber keeps the claim and
    # `cur` is the one that gets suspended for good.
    def __adopt__(cur)
      if @prev.nil?
        @prev = cur
        cur.__set_resuming__(self)
      end
    end

    def raise(*args)
      cur = Fiber.current
      exc = Fiber.__make_exception__(args)
      ::Kernel.raise(exc) if cur.equal?(self)
      __check_thread__(cur)
      Fiber.__err__(::FiberError, "attempt to resume a terminated fiber") unless @alive
      Fiber.__err__(::FiberError, "cannot raise exception on unborn fiber") unless @thread
      __adopt__(cur)
      Fiber.__switch__(cur, self, [:raise, exc])
    end

    # ---- class methods ---------------------------------------------------

    def self.yield(*args)
      current.__yield__(args)
    end

    def self.blocking?
      current.blocking? ? 1 : false
    end

    def self.blocking
      f = current
      was = f.blocking?
      f.__set_blocking__(true)
      begin
        yield f
      ensure
        f.__set_blocking__(was)
      end
    end

    def self.[](key)
      k = __check_key__(key)
      s = current.__storage_raw__
      s ? s[k] : nil
    end

    def self.[]=(key, value)
      current.__storage_store__(__check_key__(key), value)
      value
    end

    def self.scheduler
      current.__root_fiber__.__scheduler_get__
    end

    def self.set_scheduler(scheduler)
      unless scheduler.nil?
        SCHEDULER_METHODS.each do |m|
          unless scheduler.respond_to?(m)
            __err__(::ArgumentError, "Scheduler must implement ##{m}")
          end
        end
      end
      current.__root_fiber__.__scheduler_set__(scheduler)
      scheduler
    end

    def self.current_scheduler
      current.blocking? ? nil : scheduler
    end

    def self.schedule(*args, &block)
      s = scheduler
      __err__(::RuntimeError, "No scheduler is available!") unless s
      s.fiber(*args, &block)
    end

    # ---- the switch ------------------------------------------------------

    # Hands control from `cur` to `target` and blocks until control comes back.
    def self.__switch__(cur, target, msg)
      cur.__set_status__(:suspended)
      target.__start__
      target.__post__(msg)
      cur.__act__(cur.__await__)
    end

    def __start__
      return self if @thread
      fiber = self
      owner = @root_fiber.__thread__
      @thread = Thread.new do
        Thread.current[:__ir_fiber_current__] = fiber
        # Ruby ownership (Mutex, deadlock detection) is per thread, not per fiber
        Thread.__set_fiber_owner__(owner) if owner
        fiber.__run__
      end
      self
    end

    def __await__
      msg = @mailbox.pop
      @status = :resumed
      msg
    end

    # Acts on a message that just arrived for the fiber we are running on.
    def __act__(msg)
      throw(KILL_TAG) if @killing && !@root
      case msg[0]
      when :kill
        @killing = true
        throw(KILL_TAG) unless @root
        nil
      when :raise, :error
        ::Kernel.raise(msg[1])
      when :resume, :transfer
        args = msg[1]
        args.size <= 1 ? args.first : args
      else
        msg[1]
      end
    end

    def __yield__(args)
      throw(KILL_TAG) if @killing
      target = @prev
      if @root || target.nil?
        Fiber.__err__(::FiberError, "attempt to yield on a not resumed fiber")
      end
      @prev = nil
      target.__set_resuming__(nil)
      @status = :suspended
      @yielded = true
      target.__post__([:yield, args.size <= 1 ? args.first : args])
      msg = __await__
      @yielded = false
      __act__(msg)
    end

    # Runs on the fiber's own thread.
    def __run__
      msg = __await__
      result = nil
      error = nil
      handed_back = false
      begin
        catch(KILL_TAG) do
          begin
            case msg[0]
            when :resume, :transfer
              result = @block.call(*msg[1])
            else
              __act__(msg)
            end
          rescue ::Exception => e
            error = e
          end
        end
        __finish__(error ? [:error, error] : [:return, result])
        handed_back = true
      ensure
        unless handed_back
          # `return` or `break` out of the fiber block unwinds with something
          # `rescue Exception` cannot see. Hand control back anyway - dropping
          # it here would park the resuming fiber forever.
          begin
            __finish__([:error, ::LocalJumpError.new("unexpected return")])
          rescue ::Exception
          end
        end
      end
    end

    # Runs on the fiber's own thread, as the last thing it does.
    def __finish__(msg)
      @alive = false
      @status = :terminated
      target = @prev
      if target
        @prev = nil
        target.__set_resuming__(nil)
      else
        # Entered by #transfer: MRI returns to the deepest fiber of the root
        # fiber's resume chain, not to the root itself.
        target = @root_fiber
        while (nested = target.__resuming__)
          target = nested
        end
      end
      target.__post__(msg)
    end
  end
end

# Kernel#Complex and Kernel#Rational never learned to read a String - they went
# straight for #real and #numerator on it - so every `Complex("1+2i")` in the
# specs was a NoMethodError. Parsing is String#to_c / String#to_r; what these
# add is MRI's strictness, because Complex("abc") is an ArgumentError where
# "abc".to_c is (0+0i).
module Kernel
  NUMERIC_STRING__ = /\A[+-]?[0-9][0-9_]*(?:\.[0-9][0-9_]*)?(?:[eE][+-]?[0-9][0-9_]*)?(?:\/[0-9][0-9_]*)?\z/
  COMPLEX_STRING__ = /\A[0-9+\-._eE\/i@]+\z/

  def __convert_error__(value)
    ::Kernel.raise(::ArgumentError, "invalid value for convert(): #{value.inspect}")
  end
  private :__convert_error__

  def __string_to_c__(str)
    t = str.strip
    __convert_error__(str) if t.empty? || t !~ COMPLEX_STRING__ || t !~ /[0-9]/
    t.to_c
  end
  private :__string_to_c__

  def __string_to_r__(str)
    t = str.strip
    __convert_error__(str) unless t =~ NUMERIC_STRING__
    t.to_r
  end
  private :__string_to_r__

  # The built-in Complex/Rational are stubs that load complex18.rb and then
  # redefine themselves, so they have to be run once before they can be wrapped
  # - otherwise the first call through the wrapper replaces the wrapper.
  begin
    require 'complex18'
    require 'rational18'
    Complex(0, 0)
    Rational(0, 1)
  rescue ::Exception
  end

  if private_method_defined?(:Complex) || method_defined?(:Complex)
    alias_method :__ir_Complex__, :Complex
    private :__ir_Complex__

    def Complex(real, imaginary = nil, exception: true)
      if real.is_a?(::String) || imaginary.is_a?(::String)
        begin
          r = real.is_a?(::String) ? __string_to_c__(real) : real
          return r if imaginary.nil?
          i = imaginary.is_a?(::String) ? __string_to_c__(imaginary) : imaginary
        rescue ::ArgumentError
          raise if exception
          return nil
        end
        return r + i * ::Complex.new(0, 1)
      end
      begin
        imaginary.nil? ? __ir_Complex__(real) : __ir_Complex__(real, imaginary)
      rescue ::ArgumentError, ::TypeError
        raise if exception
        nil
      end
    end
    module_function :Complex
  end

  if private_method_defined?(:Rational) || method_defined?(:Rational)
    alias_method :__ir_Rational__, :Rational
    private :__ir_Rational__

    def Rational(numerator, denominator = nil, exception: true)
      if numerator.is_a?(::String) || denominator.is_a?(::String)
        begin
          n = numerator.is_a?(::String) ? __string_to_r__(numerator) : numerator
          return n if denominator.nil?
          d = denominator.is_a?(::String) ? __string_to_r__(denominator) : denominator
        rescue ::ArgumentError
          raise if exception
          return nil
        end
        return n / d
      end
      begin
        denominator.nil? ? __ir_Rational__(numerator) : __ir_Rational__(numerator, denominator)
      rescue ::ArgumentError, ::TypeError
        raise if exception
        nil
      end
    end
    module_function :Rational
  end
end

# A non-blocking fiber does not sleep on the thread: it hands the wait to the
# fiber scheduler, which is free to run something else in the meantime. Without
# this, `sleep` with no duration inside a non-blocking fiber blocks the whole
# process for ever - spec/core/kernel/sleep_spec.rb stops dead there.
module Kernel
  if private_method_defined?(:sleep) || method_defined?(:sleep)
    alias_method :__ir_sleep__, :sleep
    private :__ir_sleep__

    def sleep(*args)
      scheduler = ::Fiber.current_scheduler
      if scheduler && scheduler.respond_to?(:kernel_sleep)
        return scheduler.kernel_sleep(*args)
      end
      __ir_sleep__(*args)
    end
    module_function :sleep
  end
end

# --- constants and core methods the 1.9 snapshot predates -------------------

class Float
  INFINITY = 1.0 / 0.0 unless const_defined?(:INFINITY)
  NAN = 0.0 / 0.0 unless const_defined?(:NAN)
  EPSILON = 2.220446049250313e-16 unless const_defined?(:EPSILON)
  DIG = 15 unless const_defined?(:DIG)
  MANT_DIG = 53 unless const_defined?(:MANT_DIG)
end

class Integer
  MAX = 2**62 - 1 unless const_defined?(:MAX)
end

# Float#round / Integer#round only take zero arguments in the 1.9 snapshot.
# The ndigits form is what Matrix#round, Rational#round and a great deal of
# ordinary code use.
class Float
  unless instance_method(:round).arity == -1
    alias_method :__ir_round__, :round

    def round(ndigits = 0)
      n = ndigits.to_int
      return __ir_round__ if n == 0
      return self unless finite?
      if n > 0
        return self if n > 17
        s = 10.0**n
        f = (self * s).__ir_round__.to_f
        # CRuby corrects for the binary representation error here (float.c,
        # round_half_up), which is why 2.675.round(2) is 2.68 and not 2.67.
        if self > 0
          f += 1 if (f + 0.5) / s <= self
        elsif self < 0
          f -= 1 if (f - 0.5) / s >= self
        end
        f / s
      else
        s = 10.0**(-n)
        f = (self / s).__ir_round__.to_f
        if self > 0
          f += 1 if (f + 0.5) * s <= self
        elsif self < 0
          f -= 1 if (f - 0.5) * s >= self
        end
        (f * s).to_i
      end
    end
  end

  def truncate(ndigits = 0)
    n = ndigits.to_int
    return to_i if n == 0
    if n > 0
      s = 10.0**n
      (self * s).to_i / s
    else
      s = 10**(-n)
      (to_i / s) * s
    end
  end unless instance_method(:truncate).arity == -1
end

class Integer
  unless instance_method(:round).arity == -1
    alias_method :__ir_round__, :round

    def round(ndigits = 0)
      n = ndigits.to_int
      return self if n >= 0
      s = 10**(-n)
      half = s / 2
      q, r = abs.divmod(s)
      q += 1 if r >= half
      self < 0 ? -(q * s) : q * s
    end
  end

  def truncate(ndigits = 0)
    n = ndigits.to_int
    return self if n >= 0
    s = 10**(-n)
    (self / s) * s
  end unless instance_method(:truncate).arity == -1

  def floor(ndigits = 0)
    n = ndigits.to_int
    return self if n >= 0
    s = 10**(-n)
    (to_f / s).floor * s
  end unless instance_method(:floor).arity == -1

  def ceil(ndigits = 0)
    n = ndigits.to_int
    return self if n >= 0
    s = 10**(-n)
    (to_f / s).ceil * s
  end unless instance_method(:ceil).arity == -1
end

if defined?(Rational) && Rational.instance_method(:round).arity == 0
  class Rational
    # Rational still comes from rational18.rb, which prints the 1.8 forms:
    # inspect as "Rational(5, 8)" and to_s dropping a denominator of 1. 1.9
    # changed both - "(5/8)" and "2/1" - and every spec that prints a Rational
    # compares against those.
    def to_s
      "#{numerator}/#{denominator}"
    end

    def inspect
      "(#{to_s})"
    end

    alias_method :__ir_round__, :round

    def round(ndigits = 0)
      n = ndigits.to_int
      return __ir_round__ if n == 0
      if n > 0
        s = 10**n
        Rational((self * s).__ir_round__, s)
      else
        s = 10**(-n)
        (self / s).__ir_round__ * s
      end
    end
  end
end

module Kernel
  # The built-in warn takes exactly one message and no keywords; 2.5 added
  # multiple messages plus uplevel:, and 3.0 added category:.
  if private_method_defined?(:warn) && instance_method(:warn).arity == 1
    alias_method :__ir_warn__, :warn
    private :__ir_warn__

    def warn(*messages, **options)
      return nil if messages.empty?
      uplevel = options[:uplevel]
      if uplevel
        # IronRuby's Kernel#caller only takes the start argument.
        location = (caller(uplevel.to_i + 1) || [])[0]
        prefix = location ? "#{location}: warning: " : nil
      end
      text = messages.map { |m| s = m.to_s; s.end_with?("\n") ? s : s + "\n" }.join
      text = "#{prefix}#{text}" if prefix
      # The built-in always appends a newline of its own, so drop the last one.
      # By bytes, not by characters: a message is allowed to hold bytes that are
      # invalid in its encoding, and slicing it as characters raises on those.
      text = text.byteslice(0, text.bytesize - 1) if text.end_with?("\n")
      __ir_warn__(text)
      nil
    end
    module_function :warn
  end
end

class String
  # Byte-oriented slicing.  Done over a binary copy so that the indices really
  # are byte indices, then tagged back with the receiver's encoding the way
  # rb_str_byteslice does.
  def byteslice(*args)
    binary = dup
    binary.force_encoding(Encoding::BINARY) if binary.respond_to?(:force_encoding)
    result = binary[*args]
    return nil if result.nil?
    result.force_encoding(encoding) if result.respond_to?(:force_encoding)
    result
  end unless method_defined?(:byteslice)

  def byteindex(needle, offset = 0)
    binary = dup
    binary.force_encoding(Encoding::BINARY) if binary.respond_to?(:force_encoding)
    needle = needle.dup
    needle.force_encoding(Encoding::BINARY) if needle.respond_to?(:force_encoding)
    binary.index(needle, offset)
  end unless method_defined?(:byteindex)

  def byterindex(needle, offset = -1)
    binary = dup
    binary.force_encoding(Encoding::BINARY) if binary.respond_to?(:force_encoding)
    needle = needle.dup
    needle.force_encoding(Encoding::BINARY) if needle.respond_to?(:force_encoding)
    binary.rindex(needle, offset)
  end unless method_defined?(:byterindex)

  # Replaces a byte range in place. Every index here is a byte index, so the
  # work is done on a binary copy and tagged back afterwards, like bytesplice.
  def bytesplice(*args)
    str = args.pop
    unless str.is_a?(::String)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{str.class} into String")
    end
    case args.size
    when 1
      range = args[0]
      unless range.is_a?(::Range)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{range.class} into Integer")
      end
      index, length = __byte_range__(range)
    when 2
      index = ::Kernel.Integer(args[0])
      length = ::Kernel.Integer(args[1])
      index += bytesize if index < 0
      ::Kernel.raise(::IndexError, "index #{args[0]} out of string") if index < 0 || index > bytesize
      ::Kernel.raise(::IndexError, "negative length #{length}") if length < 0
    else
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size + 1}, expected 2..5)")
    end
    length = bytesize - index if index + length > bytesize

    binary = dup
    binary.force_encoding(::Encoding::BINARY) if binary.respond_to?(:force_encoding)
    piece = str.dup
    piece.force_encoding(::Encoding::BINARY) if piece.respond_to?(:force_encoding)
    result = binary[0, index] + piece + binary[(index + length)..-1].to_s
    result.force_encoding(encoding) if result.respond_to?(:force_encoding)
    replace(result)
  end unless method_defined?(:bytesplice)

  def __byte_range__(range)
    size = bytesize
    first = range.begin
    first = 0 if first.nil?
    first = ::Kernel.Integer(first)
    first += size if first < 0
    ::Kernel.raise(::RangeError, "#{range} out of range") if first < 0 || first > size
    last = range.end
    if last.nil?
      length = size - first
    else
      last = ::Kernel.Integer(last)
      last += size if last < 0
      last -= 1 if range.exclude_end?
      length = last - first + 1
      length = 0 if length < 0
    end
    [first, length]
  end
  private :__byte_range__

  # Appends the bytes of each argument without any encoding negotiation: an
  # Integer contributes one byte, a String contributes its bytes as they are.
  def append_as_bytes(*objects)
    objects.each do |o|
      bytes =
        case o
        when ::Integer then [o & 0xff].pack("C")
        when ::String then o
        else ::Kernel.raise(::TypeError, "wrong argument type #{o.class} (expected String or Integer)")
        end
      piece = bytes.dup
      piece.force_encoding(::Encoding::BINARY) if piece.respond_to?(:force_encoding)
      binary = dup
      binary.force_encoding(::Encoding::BINARY) if binary.respond_to?(:force_encoding)
      binary << piece
      binary.force_encoding(encoding) if binary.respond_to?(:force_encoding)
      replace(binary)
    end
    self
  end unless method_defined?(:append_as_bytes)

  def partition(pattern)
    if pattern.is_a?(::Regexp)
      m = pattern.match(self)
      return [dup, "", ""] unless m
      [m.pre_match, m[0], m.post_match]
    else
      pattern = ::Kernel.String(pattern) unless pattern.is_a?(::String)
      i = index(pattern)
      return [dup, "", ""] unless i
      [self[0, i], pattern.dup, self[(i + pattern.length)..-1]]
    end
  end unless method_defined?(:partition)

  def rpartition(pattern)
    if pattern.is_a?(::Regexp)
      start = nil
      pos = 0
      # Regexp#match takes no start offset here, so walk forward keeping the
      # last match that begins at or after each position.
      while pos <= length && (i = index(pattern, pos))
        start = i
        pos = i + 1
      end
      return ["", "", dup] unless start
      m = pattern.match(self[start..-1])
      [self[0, start], m[0], self[(start + m[0].length)..-1]]
    else
      pattern = ::Kernel.String(pattern) unless pattern.is_a?(::String)
      i = rindex(pattern)
      return ["", "", dup] unless i
      [self[0, i], pattern.dup, self[(i + pattern.length)..-1]]
    end
  end unless method_defined?(:rpartition)

  def prepend(*others)
    others = others.map { |o| o.is_a?(::String) ? o : ::Kernel.String(o) }
    replace(others.join + self)
  end unless method_defined?(:prepend)

  def casecmp?(other)
    return nil unless other.is_a?(::String)
    c = casecmp(other)
    c.nil? ? nil : c == 0
  end unless method_defined?(:casecmp?)

  def delete_prefix!(prefix)
    result = delete_prefix(prefix)
    result == self ? nil : replace(result)
  end unless method_defined?(:delete_prefix!)

  def delete_suffix!(suffix)
    result = delete_suffix(suffix)
    result == self ? nil : replace(result)
  end unless method_defined?(:delete_suffix!)

  # 3.4's name for -@. Spelled out rather than aliased: String#-@ is itself
  # defined further down this file.
  def dedup
    frozen? ? self : dup.freeze
  end unless method_defined?(:dedup)

  # Parses as much of a complex number as it can and answers (0+0i) for the
  # rest, the way Kernel#Complex(str, exception: false) does. The grammar is
  # MRI's: [real][sign imaginary"i"], or "real@angle" for polar form, with the
  # real and imaginary parts each an integer, a float or a rational.
  NUMBER__ = '[+-]?(?:\d[\d_]*)?(?:\.\d[\d_]*)?(?:[eE][+-]?\d[\d_]*)?(?:\/\d[\d_]*)?'

  def to_c
    s = strip
    if (m = /\A(#{NUMBER__})@(#{NUMBER__})/o.match(s)) && !m[1].empty? && !m[2].empty?
      return ::Complex.polar(__to_num__(m[1]), __to_num__(m[2]))
    end
    if (m = /\A(#{NUMBER__})?([+-](?:\d[\d_]*)?(?:\.\d[\d_]*)?(?:[eE][+-]?\d[\d_]*)?(?:\/\d[\d_]*)?)i/o.match(s))
      real = m[1].nil? || m[1].empty? ? 0 : __to_num__(m[1])
      imag = m[2] == "+" ? 1 : (m[2] == "-" ? -1 : __to_num__(m[2]))
      return ::Complex.new(real, imag)
    end
    if (m = /\A(#{NUMBER__})i/o.match(s)) && !m[1].empty? && m[1] != "+" && m[1] != "-"
      return ::Complex.new(0, __to_num__(m[1]))
    end
    if (m = /\A(#{NUMBER__})/o.match(s)) && !m[1].empty?
      return ::Complex.new(__to_num__(m[1]), 0)
    end
    ::Complex.new(0, 0)
  end unless method_defined?(:to_c)

  def __to_num__(text)
    text = text.delete("_")
    if text.include?("/")
      text.to_r
    elsif text.include?(".") || text.include?("e") || text.include?("E")
      text.to_f
    else
      text.to_i
    end
  end
  private :__to_num__

  # The inverse of #dump. Anything that is not something #dump could have
  # produced is a RuntimeError, which is what MRI raises here.
  def undump
    s = self
    forced = nil
    if (m = /\A(".*")\.force_encoding\("([^"]+)"\)\z/m.match(s))
      s = m[1]
      forced = m[2]
    end
    unless s.start_with?('"') && s.end_with?('"') && s.length >= 2
      ::Kernel.raise(::RuntimeError, "invalid dumped string; not wrapped with '\"' nor '\"...\".force_encoding(\"...\")' form")
    end
    body = s[1...-1]
    ::Kernel.raise(::RuntimeError, "invalid dumped string") if body.nil?
    out = +""
    forced = nil
    i = 0
    while i < body.length
      c = body[i]
      if c == '"'
        ::Kernel.raise(::RuntimeError, "invalid dumped string")
      elsif c == "\\"
        i += 1
        e = body[i]
        ::Kernel.raise(::RuntimeError, "invalid dumped string") if e.nil?
        case e
        when "n" then out << "\n"
        when "t" then out << "\t"
        when "r" then out << "\r"
        when "f" then out << "\f"
        when "v" then out << "\v"
        when "b" then out << "\b"
        when "a" then out << "\a"
        when "e" then out << "\e"
        when "s" then out << " "
        when "\\" then out << "\\"
        when '"' then out << '"'
        when "#" then out << "#"
        when "0" then out << "\0"
        when "x"
          hex = body[(i + 1), 2]
          ::Kernel.raise(::RuntimeError, "invalid hex escape") unless hex =~ /\A[0-9a-fA-F]{2}\z/
          out << hex.to_i(16).chr
          i += 2
        when "u"
          if body[i + 1] == "{"
            close = body.index("}", i + 1)
            ::Kernel.raise(::RuntimeError, "unterminated Unicode escape") unless close
            body[(i + 2)...close].split(" ").each { |cp| out << __undump_cp__(cp) }
            i = close
          else
            cp = body[(i + 1), 4]
            ::Kernel.raise(::RuntimeError, "invalid Unicode escape") unless cp =~ /\A[0-9a-fA-F]{4}\z/
            out << __undump_cp__(cp)
            i += 4
          end
        else
          # MRI passes an escape it does not recognise through untouched.
          out << "\\" << e
        end
      else
        out << c
      end
      i += 1
    end
    out.force_encoding(forced) if forced && out.respond_to?(:force_encoding)
    out
  end unless method_defined?(:undump)

  def __undump_cp__(hex)
    ::Kernel.raise(::RuntimeError, "invalid Unicode escape") unless hex =~ /\A[0-9a-fA-F]+\z/
    cp = hex.to_i(16)
    ::Kernel.raise(::RuntimeError, "invalid Unicode codepoint") if cp > 0x10ffff
    # pack("U") hands back an ASCII-8BIT string here; the bytes are UTF-8.
    ch = [cp].pack("U")
    ch.force_encoding(::Encoding::UTF_8) if ch.respond_to?(:force_encoding)
    ch
  end
  private :__undump_cp__

  # Replaces every byte that is not part of a valid character with the given
  # replacement (the encoding's own replacement character by default).
  def scrub(replacement = nil, &block)
    return dup if valid_encoding?
    default = encoding == ::Encoding::UTF_8 ? "�" : "?"
    out = +""
    out.force_encoding(encoding) if out.respond_to?(:force_encoding)
    each_char do |ch|
      if ch.valid_encoding?
        out << ch
      elsif block
        out << block.call(ch).to_s
      else
        out << (replacement || default)
      end
    end
    out
  end unless method_defined?(:scrub)

  def scrub!(replacement = nil, &block)
    replace(scrub(replacement, &block))
  end unless method_defined?(:scrub!)

  # ---- case mapping options (2.4) and strip selectors (4.0) ---------------
  #
  # The built-ins take no arguments, so every `upcase(:ascii)` and
  # `strip("a-c")` in the specs came back as a wrong-number-of-arguments error.

  CASE_OPTIONS__ = [:ascii, :turkic, :lithuanian, :fold]

  def __case_options__(options, folding_allowed)
    ::Kernel.raise(::ArgumentError, "too many options") if options.size > 2
    options.each do |o|
      ::Kernel.raise(::ArgumentError, "invalid option") unless CASE_OPTIONS__.include?(o)
      if o == :fold && !folding_allowed
        ::Kernel.raise(::ArgumentError, "option :fold only allowed for downcasing")
      end
    end
    # :turkic and :lithuanian are the only pair MRI accepts together.
    if options.size == 2 && !(options.include?(:turkic) && options.include?(:lithuanian))
      ::Kernel.raise(::ArgumentError, "too many options")
    end
    options
  end
  private :__case_options__

  # Turkic keeps the dot: I/ı and İ/i are separate letters.
  def __turkic__(up)
    if up
      gsub("i", "İ")
    else
      gsub("I", "ı")
    end
  end
  private :__turkic__

  [[:upcase, true], [:downcase, false], [:capitalize, true], [:swapcase, true]].each do |name, upward|
    plain = :"__ir_#{name}__"
    alias_method plain, name
    private plain

    define_method(name) do |*options|
      __case_options__(options, name == :downcase)
      return __send__(plain) if options.empty?
      if options.include?(:ascii)
        # Only a-z/A-Z move; everything else is left alone.
        case name
        when :upcase then gsub(/[a-z]/) { |c| c.__send__(plain) }
        when :downcase then gsub(/[A-Z]/) { |c| c.__send__(plain) }
        when :swapcase then gsub(/[a-zA-Z]/) { |c| c.__send__(plain) }
        else
          rest = self[1..-1].to_s
          self[0, 1].to_s.gsub(/[a-z]/) { |c| c.__send__(:__ir_upcase__) } +
            rest.gsub(/[A-Z]/) { |c| c.__send__(:__ir_downcase__) }
        end
      elsif options.include?(:turkic)
        __turkic__(upward).__send__(plain)
      else
        # :lithuanian and :fold: MRI currently does plain full case mapping.
        __send__(plain)
      end
    end

    bang = :"#{name}!"
    if method_defined?(bang)
      plain_bang = :"__ir_#{name}_bang__"
      alias_method plain_bang, bang
      private plain_bang
      define_method(bang) do |*options|
        return __send__(plain_bang) if options.empty?
        result = __send__(name, *options)
        result == self ? nil : replace(result)
      end
    end
  end

  # A character is stripped when it is in every one of the given sets, which is
  # exactly what String#count answers for a one-character string.
  def __selected__(ch, selectors)
    ch.count(*selectors) > 0
  end
  private :__selected__

  alias_method :__ir_strip__, :strip
  alias_method :__ir_lstrip__, :lstrip
  alias_method :__ir_rstrip__, :rstrip
  private :__ir_strip__, :__ir_lstrip__, :__ir_rstrip__

  def lstrip(*selectors)
    return __ir_lstrip__ if selectors.empty?
    i = 0
    i += 1 while i < length && __selected__(self[i, 1], selectors)
    self[i..-1] || self[0, 0]
  end

  def rstrip(*selectors)
    return __ir_rstrip__ if selectors.empty?
    i = length
    i -= 1 while i > 0 && __selected__(self[i - 1, 1], selectors)
    self[0, i]
  end

  def strip(*selectors)
    return __ir_strip__ if selectors.empty?
    lstrip(*selectors).rstrip(*selectors)
  end

  [:strip, :lstrip, :rstrip].each do |name|
    bang = :"#{name}!"
    next unless method_defined?(bang)
    plain_bang = :"__ir_#{name}_bang__"
    alias_method plain_bang, bang
    private plain_bang
    define_method(bang) do |*selectors|
      return __send__(plain_bang) if selectors.empty?
      result = __send__(name, *selectors)
      result == self ? nil : replace(result)
    end
  end

  # ---- Unicode normalisation and grapheme clusters ------------------------
  #
  # Both are handed to the CLR, which has the Unicode tables: normalisation to
  # System.String#Normalize and segmentation to StringInfo's text elements,
  # which are grapheme clusters by another name.

  NORMALIZATION_FORMS__ = {
    nfc: :FormC, nfd: :FormD, nfkc: :FormKC, nfkd: :FormKD
  }

  def __normalization_form__(form)
    name = NORMALIZATION_FORMS__[form]
    ::Kernel.raise(::ArgumentError, "Invalid normalization form #{form}.") unless name
    ::System::Text::NormalizationForm.__send__(name)
  end
  private :__normalization_form__

  def __require_unicode__
    unless [::Encoding::UTF_8, ::Encoding::US_ASCII].include?(encoding)
      ::Kernel.raise(::Encoding::CompatibilityError, "Unicode Normalization not appropriate for #{encoding}")
    end
    unless valid_encoding?
      ::Kernel.raise(::ArgumentError, "invalid byte sequence in #{encoding}")
    end
  end
  private :__require_unicode__

  def unicode_normalize(form = :nfc)
    __require_unicode__
    result = to_clr_string.Normalize(__normalization_form__(form)).to_s
    result.force_encoding(encoding) if result.respond_to?(:force_encoding)
    result
  end unless method_defined?(:unicode_normalize)

  def unicode_normalize!(form = :nfc)
    replace(unicode_normalize(form))
  end unless method_defined?(:unicode_normalize!)

  def unicode_normalized?(form = :nfc)
    __require_unicode__
    to_clr_string.IsNormalized(__normalization_form__(form))
  end unless method_defined?(:unicode_normalized?)

  def each_grapheme_cluster
    return ::Enumerator.new(grapheme_clusters.size) { |y| grapheme_clusters.each { |g| y << g } } unless block_given?
    grapheme_clusters.each { |g| yield g }
    self
  end unless method_defined?(:each_grapheme_cluster)

  def grapheme_clusters
    return chars unless valid_encoding?
    result = []
    e = ::System::Globalization::StringInfo.GetTextElementEnumerator(to_clr_string)
    while e.MoveNext
      piece = e.GetTextElement.to_s
      piece.force_encoding(encoding) if piece.respond_to?(:force_encoding)
      result << piece
    end
    result
  end unless method_defined?(:grapheme_clusters)

  # `str =~ x` is `x =~ str` for a Regexp and a TypeError for anything that is
  # not, which is how MRI stops the common `"a" =~ "b"` mistake.
  def =~(other)
    if other.is_a?(::Regexp)
      other =~ self
    elsif other.respond_to?(:=~)
      other =~ self
    else
      ::Kernel.raise(::TypeError, "type mismatch: #{other.class} given")
    end
  end unless method_defined?(:=~)
end

# Numeric#step never learned the keyword form - `1.step(by: 2, to: 7)` handed
# the options hash to the positional parameter and came back with "can't convert
# Hash into Float", which was 70 of spec/core/numeric's 123 errors. The
# positional form still goes to the built-in.
class Numeric
  alias_method :__ir_step__, :step

  def step(*args, &block)
    kw = nil
    if !args.empty? && args.last.is_a?(::Hash)
      last = args.last
      unless last.empty?
        unknown = last.keys - [:to, :by]
        unless unknown.empty?
          ::Kernel.raise(::ArgumentError, "unknown keyword: #{unknown[0].inspect}")
        end
        kw = args.pop
      end
    end
    if kw.nil?
      # The built-in only has the block form.
      return ::Enumerator.new { |y| __ir_step__(*args) { |v| y << v } } unless block
      return __ir_step__(*args, &block)
    end
    unless args.empty?
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size + 1}, expected 0..2)")
    end

    limit = kw[:to]
    increment = kw.key?(:by) ? kw[:by] : 1
    unless block
      return ::Enumerator.new { |y| step(**kw) { |v| y << v } }
    end
    ::Kernel.raise(::ArgumentError, "step can't be 0") if increment == 0

    if limit.nil?
      value = self
      loop do
        block.call(value)
        value += increment
      end
      return self
    end

    if is_a?(::Float) || limit.is_a?(::Float) || increment.is_a?(::Float)
      # MRI multiplies rather than accumulates so the rounding does not drift.
      base = to_f
      stop = limit.to_f
      unit = increment.to_f
      n = (stop - base) / unit
      err = ((base.abs + stop.abs + (stop - base).abs) / unit.abs) * ::Float::EPSILON
      err = 0.5 if err.nan? || err > 0.5
      n = (n + err).floor
      i = 0
      while i <= n
        block.call(base + i * unit)
        i += 1
      end
    else
      value = self
      if increment > 0
        while value <= limit
          block.call(value)
          value += increment
        end
      else
        while value >= limit
          block.call(value)
          value += increment
        end
      end
    end
    self
  end
end

class Numeric
  def i
    ::Complex.new(0, self)
  end unless method_defined?(:i)

  def to_c
    ::Complex.new(self, 0)
  end unless method_defined?(:to_c)

  def numerator
    to_r.numerator
  end unless method_defined?(:numerator)

  def denominator
    to_r.denominator
  end unless method_defined?(:denominator)

  def fdiv(other)
    to_f / other
  end unless method_defined?(:fdiv)

  alias_method :magnitude, :abs unless method_defined?(:magnitude)
end

class Float
  # The neighbouring representable Floats. The CLR walks the IEEE bit pattern
  # for us; MRI's next_float of +Infinity is +Infinity, where BitIncrement
  # answers NaN, so the ends are special-cased.
  def next_float
    return ::Float::NAN if nan?
    return self if self == ::Float::INFINITY
    ::System::Math.BitIncrement(self)
  end unless method_defined?(:next_float)

  def prev_float
    return ::Float::NAN if nan?
    return self if self == -::Float::INFINITY
    ::System::Math.BitDecrement(self)
  end unless method_defined?(:prev_float)

  # The exact value of the Float, which is always a dyadic rational.
  def to_r
    ::Kernel.raise(::FloatDomainError, to_s) if nan? || infinite?
    fraction, exponent = ::Math.frexp(abs)
    numerator = ::Math.ldexp(fraction, 53).to_i
    numerator = -numerator if self < 0
    exponent -= 53
    if exponent >= 0
      ::Kernel.Rational(numerator * (2**exponent), 1)
    else
      ::Kernel.Rational(numerator, 2**(-exponent))
    end
  end unless method_defined?(:to_r)

  def rationalize(eps = nil)
    return to_r if eps.nil?
    eps = eps.abs
    parts = __rationalize_within__((self - eps).to_r, (self + eps).to_r)
    ::Kernel.Rational(parts[0], parts[1])
  end unless method_defined?(:rationalize)

  # Stern-Brocot search for the simplest fraction inside [low, high].
  def __rationalize_within__(low, high)
    return [low.numerator, low.denominator] if low == high
    negative = low < 0
    if negative
      low, high = -high, -low
    end
    n = low.numerator / low.denominator
    n += 1 while ::Kernel.Rational(n, 1) < low
    if ::Kernel.Rational(n, 1) <= high
      return negative ? [-n, 1] : [n, 1]
    end
    whole = low.numerator / low.denominator
    one = ::Kernel.Rational(1, 1)
    num, den = __rationalize_within__(one / (high - whole), one / (low - whole))
    num, den = whole * num + den, num
    negative ? [-num, den] : [num, den]
  end
  private :__rationalize_within__
end

class Symbol
  include ::Comparable unless ancestors.include?(::Comparable)

  def name
    to_s.freeze
  end unless method_defined?(:name)

  def casecmp?(other)
    return nil unless other.is_a?(::Symbol)
    to_s.casecmp?(other.to_s)
  end unless method_defined?(:casecmp?)

  def =~(other)
    to_s =~ other
  end unless method_defined?(:=~)
end

# The complex-number half of Numeric.  IronRuby's Complex has these, but the
# real numerics never got them, so anything written against the Numeric
# protocol (Matrix, Vector, rationalisation code) breaks on a plain Integer.
class Numeric
  def real?
    true
  end unless method_defined?(:real?)

  def real
    self
  end unless method_defined?(:real)

  def imaginary
    0
  end unless method_defined?(:imaginary)
  alias_method :imag, :imaginary unless method_defined?(:imag)

  def conjugate
    self
  end unless method_defined?(:conjugate)
  alias_method :conj, :conjugate unless method_defined?(:conj)

  def abs2
    self * self
  end unless method_defined?(:abs2)

  def rectangular
    [self, 0]
  end unless method_defined?(:rectangular)
  alias_method :rect, :rectangular unless method_defined?(:rect)

  def arg
    self < 0 ? Math::PI : 0
  end unless method_defined?(:arg)
  alias_method :angle, :arg unless method_defined?(:angle)
  alias_method :phase, :arg unless method_defined?(:phase)

  def polar
    [abs, arg]
  end unless method_defined?(:polar)

  def finite?
    true
  end unless method_defined?(:finite?)

  def infinite?
    nil
  end unless method_defined?(:infinite?)

  def positive?
    self > 0
  end unless method_defined?(:positive?)

  def negative?
    self < 0
  end unless method_defined?(:negative?)

  def clamp(min, max = nil)
    if max.nil? && min.kind_of?(Range)
      lo, hi = min.begin, min.end
    else
      lo, hi = min, max
    end
    return lo if lo && self < lo
    return hi if hi && self > hi
    self
  end unless method_defined?(:clamp)
end

# Defined unconditionally: Complex inherits the Numeric versions just added
# above, so method_defined? would report them as already present.
class Complex
  def real?
    false
  end

  # MRI builds both forms the same way: the real part, the imaginary part's
  # sign, the imaginary part's magnitude, then "i" - with a "*" in front of the
  # "i" when the magnitude does not end in a digit, so that "(3/4)*i" and
  # "Infinity*i" stay readable. #to_s renders the parts with #to_s and #inspect
  # renders them with #inspect and wraps the lot in parentheses.
  def __format__(inspecting)
    r = real
    i = imag
    negative = (i.respond_to?(:negative?) ? i.negative? : i < 0) rescue false
    negative ||= (i.is_a?(::Float) && i == 0.0 && (1.0 / i) < 0)
    magnitude = negative ? -i : i
    rs = inspecting ? r.inspect : r.to_s
    is = inspecting ? magnitude.inspect : magnitude.to_s
    star = is =~ /\d\z/ ? "" : "*"
    "#{rs}#{negative ? '-' : '+'}#{is}#{star}i"
  end
  private :__format__

  def to_s
    __format__(false)
  end

  def inspect
    "(#{__format__(true)})"
  end

  def imaginary
    imag
  end unless instance_methods(false).include?(:imaginary)

  def finite?
    real.finite? && imag.finite?
  end

  def infinite?
    (real.infinite? || imag.infinite?) ? 1 : nil
  end

  def rectangular
    [real, imag]
  end
  alias_method :rect, :rectangular
end

# CRuby has KeyError < IndexError and StopIteration < IndexError, but IndexError
# maps to the sealed System::IndexOutOfRangeException here, so it cannot be
# subclassed. StandardError is the closest base that actually instantiates;
# the cost is that `rescue IndexError` will not catch these.
unless defined?(KeyError)
  class KeyError < StandardError
    def initialize(message = nil, receiver: nil, key: nil)
      # The C# core (Kernel#format's named references, Hash#fetch) sets @receiver/@key
      # directly after constructing with just a message.
      @receiver = receiver
      @key = key
      super(message)
    end

    attr_reader :receiver, :key
  end
end
class StopIteration < StandardError; end unless defined?(StopIteration)
class UncaughtThrowError < ArgumentError; end unless defined?(UncaughtThrowError)
class ClosedQueueError < StopIteration; end unless defined?(ClosedQueueError)

class IO
  # Ruby defines NULL on IO; File inherits it. Defining it on File alone left
  # IO::NULL undefined, which several specs and helpers reference.
  NULL = "/dev/null" unless const_defined?(:NULL, false)
end

class << Dir
  # Dir.home / Dir.children / Dir.each_child / Dir.empty? postdate the 1.9 core.
  def home(user = nil)
    if user.nil?
      dir = ENV['HOME']
      unless dir
        require 'etc'
        pw = (Etc.getpwuid(Process.uid) rescue nil)
        dir = pw && pw.dir
      end
      raise ArgumentError, "couldn't find HOME environment -- expanding `~'" unless dir
      return dir.dup
    end

    raise TypeError, "no implicit conversion of #{user.class} into String" unless user.is_a?(String)
    require 'etc'
    pw = (Etc.getpwnam(user) rescue nil)
    raise ArgumentError, "user #{user} doesn't exist" unless pw
    pw.dir.dup
  end unless respond_to?(:home)

  def children(path, *args)
    entries(path, *args) - %w[. ..]
  end unless respond_to?(:children)

  def each_child(path, *args, &block)
    return children(path, *args).each unless block
    children(path, *args).each(&block)
    nil
  end unless respond_to?(:each_child)

  def empty?(path)
    # File.stat rather than File.directory? so that a missing path is an ENOENT
    return false unless File.stat(path).directory?
    entries(path).size <= 2
  end unless respond_to?(:empty?)
end

module Errno
  # Linux errno numbers. Two jobs here: bind the names the 1.9 snapshot never
  # bound, and give the C#-registered classes their Errno constant -- those are
  # CLR-backed and carry no number, so Errno::ENOENT::Errno used to resolve up
  # to the Errno module itself instead of 2.
  {
    "EPERM" => 1, "ENOENT" => 2, "ESRCH" => 3, "EINTR" => 4, "EIO" => 5,
    "ENXIO" => 6, "E2BIG" => 7, "ENOEXEC" => 8, "EBADF" => 9, "ECHILD" => 10,
    "EAGAIN" => 11, "EWOULDBLOCK" => 11, "ENOMEM" => 12, "EACCES" => 13,
    "EFAULT" => 14, "EBUSY" => 16, "EEXIST" => 17, "EXDEV" => 18,
    "ENODEV" => 19, "ENOTDIR" => 20, "EISDIR" => 21, "EINVAL" => 22,
    "ENFILE" => 23, "EMFILE" => 24, "ENOTTY" => 25, "EFBIG" => 27,
    "ENOSPC" => 28, "ESPIPE" => 29, "EROFS" => 30, "EMLINK" => 31,
    "EPIPE" => 32, "EDOM" => 33, "ERANGE" => 34, "ENAMETOOLONG" => 36,
    "ENOTEMPTY" => 39, "ELOOP" => 40, "EADDRINUSE" => 98, "ECONNABORTED" => 103,
    "ECONNRESET" => 104, "ENOTCONN" => 107, "ECONNREFUSED" => 111,
    "EHOSTDOWN" => 112, "EINPROGRESS" => 115,
  }.each do |name, errno|
    if const_defined?(name)
      klass = const_get(name)
      # const_defined? without the second argument would find the enclosing
      # Errno module through the lexical scope, so restrict it to this class.
      klass.const_set(:Errno, errno) unless klass.const_defined?(:Errno, false)
    else
      klass = Class.new(SystemCallError) do
        define_method(:initialize) { |msg = nil| super(msg ? "#{name}: #{msg}" : name) }
      end
      klass.const_set(:Errno, errno)
      const_set(name, klass)
    end
  end
end

class Array
  # ---- the sixteen Array methods spec/core/array gets NoMethodError for ----

  def sample(n = nil, random: ::Kernel)
    rng = random
    if n.nil?
      return nil if empty?
      return self[__sample_index__(rng, size)]
    end
    n = ::Kernel.Integer(n)
    ::Kernel.raise(::ArgumentError, "negative sample number") if n < 0
    n = size if n > size
    pool = dup
    result = []
    n.times do
      i = __sample_index__(rng, pool.size)
      result << pool.delete_at(i)
    end
    result
  end unless method_defined?(:sample)

  def __sample_index__(rng, limit)
    value = rng.rand(limit)
    value = value.to_int if value.respond_to?(:to_int) && !value.is_a?(::Integer)
    unless value.is_a?(::Integer)
      ::Kernel.raise(::NoMethodError, "undefined method `to_int' for #{value.inspect}")
    end
    ::Kernel.raise(::RangeError, "random number too big #{value}") if value < 0 || value >= limit
    value
  end
  private :__sample_index__

  def rotate(n = 1)
    n = ::Kernel.Integer(n)
    return dup if empty?
    n %= size
    self[n..-1] + self[0, n]
  end unless method_defined?(:rotate)

  def rotate!(n = 1)
    replace(rotate(n))
  end unless method_defined?(:rotate!)

  def union(*others)
    result = dup
    others.each { |o| result |= ::Kernel.Array(o) }
    result | []
  end unless method_defined?(:union)

  def difference(*others)
    result = dup
    others.each { |o| result -= ::Kernel.Array(o) }
    result
  end unless method_defined?(:difference)

  def intersection(*others)
    result = dup
    others.each { |o| result &= ::Kernel.Array(o) }
    result & []  == [] ? result : result
  end unless method_defined?(:intersection)

  def fetch_values(*keys, &block)
    keys.map { |k| block ? (fetch(k) { |i| block.call(i) }) : fetch(k) }
  end unless method_defined?(:fetch_values)

  def rfind(&block)
    return ::Enumerator.new { |y| reverse_each { |x| y << x } } unless block
    reverse_each { |x| return x if block.call(x) }
    nil
  end unless method_defined?(:rfind)

  def bsearch(&block)
    i = bsearch_index(&block)
    i.nil? ? nil : self[i]
  end unless method_defined?(:bsearch)

  def bsearch_index(&block)
    return ::Enumerator.new { |y| each_index { |i| y << i } } unless block
    low = 0
    high = size - 1
    result = nil
    while low <= high
      mid = low + (high - low) / 2
      r = block.call(self[mid])
      case r
      when true then result = mid; high = mid - 1
      when false, nil then low = mid + 1
      when ::Integer
        return mid if r == 0
        if r < 0 then high = mid - 1 else low = mid + 1 end
      else
        ::Kernel.raise(::TypeError, "wrong argument type #{r.class} (must be numeric, true, false or nil)")
      end
    end
    result
  end unless method_defined?(:bsearch_index)

  def repeated_permutation(n)
    n = ::Kernel.Integer(n)
    unless block_given?
      count = n < 0 ? 0 : size**n
      return ::Enumerator.new(count) { |y| repeated_permutation(n) { |p| y << p } }
    end
    return self if n < 0
    __repeat__(n, false) { |combo| yield combo }
    self
  end unless method_defined?(:repeated_permutation)

  def repeated_combination(n)
    n = ::Kernel.Integer(n)
    unless block_given?
      return ::Enumerator.new { |y| repeated_combination(n) { |c| y << c } }
    end
    return self if n < 0
    __repeat__(n, true) { |combo| yield combo }
    self
  end unless method_defined?(:repeated_combination)

  # Walks the n-fold product of the receiver's indices, optionally keeping only
  # the non-decreasing tuples, which is exactly repeated_combination.
  def __repeat__(n, sorted)
    if n == 0
      yield []
      return
    end
    return if empty?
    indices = ::Array.new(n, 0)
    loop do
      yield indices.map { |i| self[i] }
      k = n - 1
      k -= 1 while k >= 0 && indices[k] == size - 1
      return if k < 0
      indices[k] += 1
      ((k + 1)...n).each { |j| indices[j] = sorted ? indices[k] : 0 }
    end
  end
  private :__repeat__

  def select!(&block)
    return ::Enumerator.new { |y| each { |x| y << x } } unless block
    before = size
    keep_if(&block)
    size == before ? nil : self
  end unless method_defined?(:select!)

  alias_method :filter!, :select! unless method_defined?(:filter!)

  def keep_if(&block)
    return ::Enumerator.new { |y| each { |x| y << x } } unless block
    replace(select { |x| block.call(x) })
    self
  end unless method_defined?(:keep_if)

  def sort_by!(&block)
    return ::Enumerator.new { |y| each { |x| y << x } } unless block
    replace(sort_by { |x| block.call(x) })
    self
  end unless method_defined?(:sort_by!)

  def to_set(*args, &block)
    require 'set'
    ::Set.new(self, *args, &block)
  end unless method_defined?(:to_set)

  def to_h
    result = {}
    each do |pair|
      pair = yield(pair) if block_given?
      unless pair.respond_to?(:to_ary) && pair.to_ary.size == 2
        raise TypeError, "wrong element type #{pair.class} (expected array)"
      end
      k, v = pair.to_ary
      result[k] = v
    end
    result
  end unless method_defined?(:to_h)
end

class String
  def b
    dup.force_encoding("ASCII-8BIT")
  end unless method_defined?(:b)

  def match?(pattern, pos = 0)
    # the bundled String#match takes no position argument
    target = pos.zero? ? self : self[pos..-1]
    return false if target.nil?
    !target.match(pattern).nil?
  end unless method_defined?(:match?)

  def start_with?(*prefixes)
    prefixes.any? { |p| p.is_a?(Regexp) ? !!(self =~ /\A(?:#{p.source})/) : self[0, p.length] == p }
  end unless method_defined?(:start_with?)
end

class Hash
  def key(value)
    each { |k, v| return k if v == value }
    nil
  end unless method_defined?(:key)

  def assoc(key)
    key?(key) ? [key, self[key]] : nil
  end unless method_defined?(:assoc)

  def rassoc(value)
    each { |k, v| return [k, v] if v == value }
    nil
  end unless method_defined?(:rassoc)

  def to_h(&block)
    unless block
      # CRuby returns self for a plain Hash, and for a subclass a plain Hash that
      # keeps the default value / default proc / compare_by_identity flag.
      return self if instance_of?(Hash)
      result = __result_hash__
      each { |k, v| result[k] = v }
      if default_proc
        result.default_proc = default_proc
      else
        result.default = default
      end
      return result
    end
    result = {}
    each { |k, v| pair = __to_h_pair__(block.call(k, v)); result[pair[0]] = pair[1] }
    result
  end unless method_defined?(:to_h)

  def keep_if
    delete_if { |k, v| !yield(k, v) }
  end unless method_defined?(:keep_if)

  def fetch_values(*keys)
    keys.map { |k| block_given? ? (key?(k) ? self[k] : yield(k)) : fetch(k) }
  end unless method_defined?(:fetch_values)
end

module GC
  def self.count
    0
  end unless respond_to?(:count)

  def self.stat(key = nil)
    stats = { count: 0, heap_allocated_pages: 0, total_allocated_objects: 0 }
    key ? stats[key] : stats
  end unless respond_to?(:stat)
end

# Ruby 3.2 autoloads Set; the 1.9 snapshot requires an explicit require.
autoload :Set, "set" unless defined?(Set)

# --- Enumerator: the block form and the methods 1.9 never had --------------
# The core class only implements #each. Instances the runtime creates itself
# (`[1,2].each` with no block) must keep working, so #initialize and #each
# only divert when a generator block was supplied.
class Enumerator
  class Yielder
    def initialize(&block)
      @block = block
    end

    def yield(*args)
      @block.call(*args)
    end
    alias_method :<<, :yield

    def call(*args)
      @block.call(*args)
    end

    # Lets a yielder stand in for a block: `Enumerator.new { |y| ary.each(&y) }`.
    def to_proc
      yielder = self
      ::Kernel.proc { |*args| yielder.yield(*args) }
    end
  end unless const_defined?(:Yielder)

  unless method_defined?(:each_without_generator)
    alias_method :each_without_generator, :each
    # The built-in #initialize lives on Enumerator itself, so redefining it here
    # hides it from `super`, which would find Object#initialize and silently
    # leave the enumerator with no target. __enum_init__ is the way back in.
    def initialize(*args, &block)
      if block
        # Enumerator.new(size = nil) { |yielder| ... }
        @generator = block
        @__size__ = args[0] unless args.empty?
      else
        if args.empty?
          ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 0, expected 1+)")
        end
        target = args[0]
        method = args.size > 1 ? args[1] : :each
        __enum_init__(target, method, args[2..-1] || [])
      end
    end

    def each(&block)
      return self unless block
      if @generator
        @generator.call(Yielder.new(&block))
        self
      else
        each_without_generator(&block)
      end
    end
  end

  def with_index(offset = 0)
    unless block_given?
      # yield [value, index] pairs, lazily, via the generator form above
      source = self
      n = source.size
      return Enumerator.new(n) { |y|
        i = offset
        source.each { |*a| y << [a.size <= 1 ? a.first : a, i]; i += 1 }
      }
    end
    i = offset
    each do |*args|
      value = args.size <= 1 ? args.first : args
      result = yield(value, i)
      i += 1
      result
    end
  end unless method_defined?(:with_index)

  def each_with_index(&block)
    with_index(0, &block)
  end unless method_defined?(:each_with_index)

  def with_object(memo)
    unless block_given?
      source = self
      n = source.size
      return Enumerator.new(n) { |y| source.each { |*a| y << [a.size <= 1 ? a.first : a, memo] } }
    end
    each do |*args|
      yield(args.size <= 1 ? args.first : args, memo)
    end
    memo
  end unless method_defined?(:with_object)
  alias_method :each_with_object, :with_object unless method_defined?(:each_with_object)

  # --- #size -----------------------------------------------------------
  # MRI gives every enumerator a size function: an Integer, a Proc, or nil when
  # the length cannot be known without iterating. Three things supply one here -
  # `Enumerator.new(size) { }`, the block form of `to_enum`, and the descriptor
  # the C# builtins record - and everything else is honestly nil.
  def __set_size__(value)
    @__size__ = value
    self
  end

  def size
    n = @__size__
    return n.call if n.is_a?(::Proc) || n.is_a?(::Method)
    return n unless n.nil?
    info = __enum_size_info__
    return __size_from_info__(info[0], info[1], info[2]) if info
    target = __enum_target__
    return __size_from_target__(target[0], target[1], target[2]) if target && !@generator
    nil
  end

  # Number of items `source` yields, when that is knowable without iterating.
  def __source_count__(source)
    return nil if source.nil?
    if source.is_a?(::Range) || source.respond_to?(:size)
      n = source.size
      return n if n.is_a?(::Numeric)
      nil
    elsif source.respond_to?(:length)
      n = source.length
      n.is_a?(::Numeric) ? n : nil
    end
  end
  private :__source_count__

  def __size_from_info__(source, op, arg)
    case op
    when :same
      __source_count__(source)
    when :self
      source
    when :slice
      n = __source_count__(source)
      n && arg > 0 ? (n + arg - 1) / arg : nil
    when :cons
      n = __source_count__(source)
      n ? (n - arg + 1 < 0 ? 0 : n - arg + 1) : nil
    when :cycle
      __cycle_size__(__source_count__(source), arg)
    when :upto
      arg < source ? 0 : arg - source + 1
    when :downto
      source < arg ? 0 : source - arg + 1
    end
  end
  private :__size_from_info__

  # `cycle` repeats forever unless a count is given, and an empty source never
  # yields at all, so its size is 0 rather than infinite.
  def __cycle_size__(count, times)
    return nil if count.nil?
    return 0 if count == 0
    return ::Float::INFINITY if times.nil?
    times <= 0 ? 0 : count * times
  end
  private :__cycle_size__

  # `to_enum(:each_slice, 2)` and friends: the descriptor is the method name.
  SIZE_SAME_METHODS = [
    :each, :each_entry, :each_pair, :each_key, :each_value, :each_index,
    :reverse_each, :map, :collect, :map!, :collect!, :flat_map, :collect_concat,
    :select, :filter, :find_all, :select!, :filter!, :reject, :reject!,
    :keep_if, :delete_if, :sort_by, :min_by, :max_by, :group_by, :partition,
    :each_with_index, :each_with_object, :each_char, :each_byte, :each_codepoint,
    :each_line, :find_index, :detect, :find, :take_while, :drop_while,
    :filter_map
  ].freeze

  def __size_from_target__(source, meth, args)
    case meth
    when :each_slice
      n = __source_count__(source)
      n && args[0].to_i > 0 ? (n + args[0].to_i - 1) / args[0].to_i : nil
    when :each_cons
      n = __source_count__(source)
      n ? [n - args[0].to_i + 1, 0].max : nil
    when :times
      source
    when :upto
      args[0] < source ? 0 : args[0] - source + 1
    when :downto
      source < args[0] ? 0 : source - args[0] + 1
    when :cycle
      __cycle_size__(__source_count__(source), args[0])
    else
      SIZE_SAME_METHODS.include?(meth) ? __source_count__(source) : nil
    end
  end
  private :__size_from_target__

  def inspect
    target = (@generator ? nil : __enum_target__)
    return "#<#{self.class}: #{@generator ? 'generator' : '...'}>" unless target
    recv, meth, args = target
    detail = "#{recv.inspect}:#{meth}"
    detail += "(#{args.map { |a| a.inspect }.join(', ')})" unless args.empty?
    "#<#{self.class}: #{detail}>"
  end
  alias_method :to_s, :inspect

  # --- external iteration ------------------------------------------------
  # #next has to suspend `each` half way through, which needs a coroutine.
  # Fiber is a background thread plus a mailbox here, so an enumerator that is
  # stepped and then abandoned parks one thread until the process exits; that is
  # the price of external iteration and MRI's own docs warn about the cost too.
  def __iter_fiber__
    @__fiber__ ||= begin
      source = self
      @__iter_done__ = false
      ::Fiber.new do
        result = source.each { |*args| ::Fiber.yield([:y, args]) }
        [:done, result]
      end
    end
  end
  private :__iter_fiber__

  def __iter_stop__
    error = ::StopIteration.new("iteration reached an end")
    error.__set_result__(@__iter_result__)
    ::Kernel.raise(error)
  end
  private :__iter_stop__

  # Pulls one more set of yielded values out of the fiber, unless one is already
  # waiting because of a #peek.
  def __iter_advance__
    return if @__peeked__
    __iter_stop__ if @__iter_done__
    fed = @__feed__
    @__feed__ = nil
    @__has_feed__ = false
    tag, payload = __iter_fiber__.resume(fed)
    if tag == :y
      @__peek__ = payload
      @__peeked__ = true
    else
      @__iter_done__ = true
      @__iter_result__ = payload
      @__fiber__ = nil
      __iter_stop__
    end
  end
  private :__iter_advance__

  def next_values
    __iter_advance__
    values = @__peek__
    @__peeked__ = false
    @__peek__ = nil
    values
  end

  def peek_values
    __iter_advance__
    @__peek__.dup
  end

  def next
    values = next_values
    values.size <= 1 ? values[0] : values
  end

  def peek
    values = peek_values
    values.size <= 1 ? values[0] : values
  end

  # The value the *suspended* `yield` inside the source method will return.
  def feed(value)
    ::Kernel.raise(::TypeError, "feed value already set") if @__has_feed__
    @__has_feed__ = true
    @__feed__ = value
    nil
  end

  def rewind
    fiber = @__fiber__
    @__fiber__ = nil
    @__iter_done__ = false
    @__iter_result__ = nil
    @__peeked__ = false
    @__peek__ = nil
    @__feed__ = nil
    @__has_feed__ = false
    # Unwind the abandoned fiber so its ensure blocks run and its thread exits.
    fiber.kill if fiber && fiber.alive?
    target = (@generator ? nil : __enum_target__)
    receiver = target && target[0]
    receiver.rewind if receiver && receiver.respond_to?(:rewind)
    self
  end

  # --- Enumerator::Lazy --------------------------------------------------
  # Every lazy operation is a new Lazy whose generator pulls from the previous
  # one, so nothing runs until the chain is forced and an infinite source is
  # fine. The per-iteration state (counters, seen-sets) lives *inside* the
  # generator block, not in the closure that builds it, so forcing the same
  # lazy twice starts over instead of resuming where the last force stopped.
  class Lazy < Enumerator
    # `Enumerator::Lazy.new(obj, size = nil) { |yielder, *values| ... }`
    def initialize(obj, size = nil, &block)
      unless block
        ::Kernel.raise(::ArgumentError, "tried to call lazy new without a block")
      end
      @generator = lambda { |y| obj.each { |*values| block.call(y, *values) } }
      @__size__ = size
      self
    end

    # Builds a Lazy straight from a generator, bypassing #initialize.
    def self.__raw__(size = nil, &generator)
      lazy = allocate
      lazy.__lazy_init__(size, &generator)
      lazy
    end

    def __lazy_init__(size, &generator)
      @generator = generator
      @__size__ = size
      self
    end

    def __chain__(size = nil, &generator)
      Lazy.__raw__(size, &generator)
    end
    private :__chain__

    def __need_block__(name, block)
      return if block
      ::Kernel.raise(::ArgumentError, "tried to call lazy #{name} without a block")
    end
    private :__need_block__

    def lazy
      self
    end

    # Back to an ordinary Enumerator over the same elements.
    def eager
      source = self
      ::Enumerator.new(@__size__) { |y| source.each { |*values| y.yield(*values) } }
    end

    def size
      value = @__size__
      return value.call if value.is_a?(::Proc) || value.is_a?(::Method)
      value
    end

    def inspect
      "#<#{self.class}: ...>"
    end
    alias_method :to_s, :inspect

    def map(&block)
      __need_block__("map", block)
      source = self
      __chain__(size) { |y| source.each { |*values| y << block.call(*values) } }
    end
    alias_method :collect, :map

    def flat_map(&block)
      __need_block__("flat_map", block)
      source = self
      __chain__ do |y|
        source.each do |*values|
          result = block.call(*values)
          # MRI splices an Array (or anything with #to_ary) and yields anything
          # else whole, so `flat_map { |x| x }` over strings is not flattened.
          array = result.is_a?(::Array) ? result : (result.respond_to?(:to_ary) ? result.to_ary : nil)
          if array.is_a?(::Array)
            array.each { |item| y << item }
          else
            y << result
          end
        end
      end
    end
    alias_method :collect_concat, :flat_map

    def select(&block)
      __need_block__("select", block)
      source = self
      __chain__ { |y| source.each { |*values| item = __value__(values); y << item if block.call(item) } }
    end
    alias_method :filter, :select
    alias_method :find_all, :select

    def filter_map(&block)
      __need_block__("filter_map", block)
      source = self
      __chain__ do |y|
        source.each do |*values|
          result = block.call(*values)
          y << result if result
        end
      end
    end

    def reject(&block)
      __need_block__("reject", block)
      source = self
      __chain__ { |y| source.each { |*values| item = __value__(values); y << item unless block.call(item) } }
    end

    def grep(pattern, &block)
      source = self
      __chain__ do |y|
        source.each do |*values|
          value = values.size <= 1 ? values[0] : values
          next unless pattern === value
          y << (block ? block.call(value) : value)
        end
      end
    end

    def grep_v(pattern, &block)
      source = self
      __chain__ do |y|
        source.each do |*values|
          value = values.size <= 1 ? values[0] : values
          next if pattern === value
          y << (block ? block.call(value) : value)
        end
      end
    end

    def compact
      source = self
      __chain__ do |y|
        source.each do |*values|
          value = values.size <= 1 ? values[0] : values
          y << value unless value.nil?
        end
      end
    end

    def uniq(&block)
      source = self
      __chain__ do |y|
        seen = {}
        source.each do |*values|
          value = values.size <= 1 ? values[0] : values
          key = block ? block.call(value) : value
          next if seen.key?(key)
          seen[key] = true
          y << value
        end
      end
    end

    def take(n)
      n = __to_int__(n)
      ::Kernel.raise(::ArgumentError, "attempt to take negative size") if n < 0
      current = size
      new_size = current.nil? ? n : (current < n ? current : n)
      return __chain__(0) { |y| } if n == 0
      source = self
      __chain__(new_size) do |y|
        taken = 0
        tag = ::Object.new
        catch(tag) do
          source.each do |*values|
            y << __value__(values)
            taken += 1
            throw(tag) if taken >= n
          end
        end
      end
    end

    def take_while(&block)
      __need_block__("take_while", block)
      source = self
      __chain__ do |y|
        tag = ::Object.new
        catch(tag) do
          source.each do |*values|
            throw(tag) unless block.call(*values)
            y << __value__(values)
          end
        end
      end
    end

    def drop(n)
      n = __to_int__(n)
      ::Kernel.raise(::ArgumentError, "attempt to drop negative size") if n < 0
      current = size
      new_size = current.nil? ? nil : (current < n ? 0 : current - n)
      source = self
      __chain__(new_size) do |y|
        dropped = 0
        source.each do |*values|
          if dropped < n
            dropped += 1
          else
            y << __value__(values)
          end
        end
      end
    end

    def drop_while(&block)
      __need_block__("drop_while", block)
      source = self
      __chain__ do |y|
        dropping = true
        source.each do |*values|
          dropping = false if dropping && !block.call(*values)
          y << __value__(values) unless dropping
        end
      end
    end

    def with_index(offset = 0, &block)
      offset = __to_int__(offset)
      source = self
      if block
        __chain__(size) do |y|
          i = offset
          source.each do |*values|
            y << block.call(values.size <= 1 ? values[0] : values, i)
            i += 1
          end
        end
      else
        __chain__(size) do |y|
          i = offset
          source.each do |*values|
            y << [values.size <= 1 ? values[0] : values, i]
            i += 1
          end
        end
      end
    end

    def each_with_index(&block)
      with_index(0, &block)
    end

    def with_object(memo)
      source = self
      __chain__(size) do |y|
        source.each { |*values| y << [values.size <= 1 ? values[0] : values, memo] }
      end
    end
    alias_method :each_with_object, :with_object

    def zip(*others, &block)
      # MRI only stays lazy when every argument is a plain Array; anything else
      # (and the block form) falls back to the eager Enumerable#zip.
      if block || others.any? { |other| !other.is_a?(::Array) }
        return eager.zip(*others, &block)
      end
      source = self
      __chain__(size) do |y|
        index = 0
        source.each do |*values|
          row = [values.size <= 1 ? values[0] : values]
          others.each { |other| row << other[index] }
          index += 1
          y << row
        end
      end
    end

    def __value__(values)
      values.size <= 1 ? values[0] : values
    end
    private :__value__

    # The grouping operations have to stay lazy too: inheriting the eager
    # Enumerable versions makes `(1..Float::INFINITY).lazy.chunk_while { }`
    # iterate forever instead of emitting each group as it closes.
    def chunk(&block)
      __need_block__("chunk", block)
      source = self
      __chain__ do |y|
        key = nil
        group = nil
        source.each do |*values|
          item = __value__(values)
          k = block.call(item)
          if group && k == key
            group << item
          else
            y << [key, group] if group
            key = k
            group = [item]
          end
        end
        y << [key, group] if group
      end
    end

    def chunk_while(&block)
      __need_block__("chunk_while", block)
      source = self
      __chain__ do |y|
        group = nil
        previous = nil
        source.each do |*values|
          item = __value__(values)
          if group.nil?
            group = [item]
          elsif block.call(previous, item)
            group << item
          else
            y << group
            group = [item]
          end
          previous = item
        end
        y << group if group
      end
    end

    def slice_when(&block)
      __need_block__("slice_when", block)
      chunk_while { |a, b| !block.call(a, b) }
    end

    def slice_before(*args, &block)
      has_pattern = !args.empty?
      pattern = args[0]
      source = self
      __chain__ do |y|
        group = nil
        source.each do |*values|
          item = __value__(values)
          starts = has_pattern ? (pattern === item) : block.call(item)
          if group.nil?
            group = [item]
          elsif starts
            y << group
            group = [item]
          else
            group << item
          end
        end
        y << group if group
      end
    end

    def slice_after(*args, &block)
      has_pattern = !args.empty?
      pattern = args[0]
      source = self
      __chain__ do |y|
        group = []
        source.each do |*values|
          item = __value__(values)
          group << item
          if has_pattern ? (pattern === item) : block.call(item)
            y << group
            group = []
          end
        end
        y << group unless group.empty?
      end
    end

    # Staying lazy across to_enum is what keeps `lazy.to_enum(:each)` usable on
    # an infinite source.
    def to_enum(method = :each, *args, &size_block)
      source = self
      Lazy.__raw__(size_block) do |y|
        source.send(method, *args) { |*values| y.yield(*values) }
      end
    end
    alias_method :enum_for, :to_enum

    def first(n = nil)
      if n.nil?
        result = nil
        tag = ::Object.new
        catch(tag) do
          each do |*values|
            result = values.size <= 1 ? values[0] : values
            throw(tag)
          end
        end
        return result
      end
      n = __to_int__(n)
      ::Kernel.raise(::ArgumentError, "attempt to take negative size") if n < 0
      result = []
      return result if n == 0
      tag = ::Object.new
      catch(tag) do
        each do |*values|
          result << (values.size <= 1 ? values[0] : values)
          throw(tag) if result.size >= n
        end
      end
      result
    end

    def force(*args)
      args.empty? ? to_a : to_a(*args)
    end

    def to_a
      result = []
      each { |*values| result << (values.size <= 1 ? values[0] : values) }
      result
    end

    def __to_int__(value)
      return value if value.is_a?(::Integer)
      unless value.respond_to?(:to_int)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{value.class} into Integer")
      end
      value.to_int
    end
    private :__to_int__
  end

  # --- Enumerator::Chain -------------------------------------------------
  class Chain < Enumerator
    def initialize(*enums)
      @__enums__ = enums
      self
    end

    def each(&block)
      return to_enum(:each) { size } unless block
      @__enums__.each { |enum| enum.each { |*values| block.call(*values) } }
      self
    end

    def size
      total = 0
      @__enums__.each do |enum|
        n = enum.respond_to?(:size) ? enum.size : nil
        return nil unless n.is_a?(::Numeric)
        return n if n == ::Float::INFINITY
        total += n
      end
      total
    end

    def rewind
      @__enums__.reverse_each { |enum| enum.rewind if enum.respond_to?(:rewind) }
      self
    end

    def inspect
      "#<Enumerator::Chain: #{@__enums__.inspect}>"
    end
    alias_method :to_s, :inspect

    def +(other)
      Chain.new(self, other)
    end
  end

  # --- Enumerator::Product -----------------------------------------------
  # The cartesian product of its arguments, leftmost varying slowest.
  class Product < Enumerator
    def initialize(*enums, **options)
      enums.each do |enum|
        unless enum.respond_to?(:each)
          ::Kernel.raise(::TypeError, "wrong argument type #{enum.class} (must respond to :each)")
        end
      end
      @__enums__ = enums
      self
    end

    def each(&block)
      return to_enum(:each) { size } unless block
      __product__(0, [], block)
      self
    end

    def __product__(index, prefix, block)
      if index == @__enums__.size
        block.call(prefix.dup)
        return
      end
      @__enums__[index].each do |*values|
        prefix.push(values.size <= 1 ? values[0] : values)
        __product__(index + 1, prefix, block)
        prefix.pop
      end
    end
    private :__product__

    def size
      total = 1
      @__enums__.each do |enum|
        n = enum.respond_to?(:size) ? enum.size : nil
        return nil unless n.is_a?(::Numeric)
        total *= n
      end
      total
    end

    def rewind
      @__enums__.reverse_each { |enum| enum.rewind if enum.respond_to?(:rewind) }
      self
    end

    def inspect
      "#<Enumerator::Product: #{@__enums__.inspect}>"
    end
    alias_method :to_s, :inspect
  end

  def +(other)
    Chain.new(self, other)
  end unless method_defined?(:+)

  def self.product(*enums, &block)
    product = Product.new(*enums)
    return product unless block
    product.each(&block)
    nil
  end unless respond_to?(:product)

  # Enumerator.produce(initial = nil) { |previous| ... } - an endless sequence
  # built by feeding each value back into the block. StopIteration ends it.
  def self.produce(*args, &block)
    ::Kernel.raise(::ArgumentError, "no block given") unless block
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    has_initial = !args.empty?
    initial = args[0]
    Enumerator.new(::Float::INFINITY) do |y|
      value = has_initial ? initial : block.call(nil)
      loop do
        y << value
        value = block.call(value)
      end
    end
  end unless respond_to?(:produce)
end

module Enumerable
  def chain(*others)
    ::Enumerator::Chain.new(self, *others)
  end unless method_defined?(:chain)
end

module Enumerable
  def lazy
    source = self
    n = (size if respond_to?(:size))
    n = nil unless n.is_a?(::Numeric)
    ::Enumerator::Lazy.__raw__(n) { |y| source.each { |*values| y.yield(*values) } }
  end unless method_defined?(:lazy)
end

class StopIteration
  def __set_result__(value)
    @result = value
    self
  end

  def result
    @result
  end
end

module Kernel
  # MRI defaults the method to :each and takes an optional block returning the
  # enumerator's #size.
  def to_enum(method = :each, *args, &size_block)
    enum = ::Enumerator.new(self, method, *args)
    enum.__set_size__(size_block) if size_block
    enum
  end
  alias_method :enum_for, :to_enum

  # The C# Kernel#loop does not know about StopIteration, which is what makes
  # `loop { e.next }` terminate instead of blowing up.
  def loop
    return to_enum(:loop) { ::Float::INFINITY } unless block_given?
    begin
      yield while true
    rescue ::StopIteration => stop
      stop.result
    end
  end
end

# Comparable#== calls <=>, and the default Kernel#<=> is defined in terms of
# ==, so two objects that define neither recurse forever and overflow the
# stack. CRuby breaks the cycle with rb_exec_recursive_paired; do the same.
module Comparable
  def ==(other)
    return true if equal?(other)

    stack = (Thread.current[:__comparable_eq__] ||= [])
    pair = [object_id, other.object_id]
    return false if stack.include?(pair)

    stack.push(pair)
    begin
      # Ruby 2.3 stopped rescuing StandardError here: whatever <=> raises goes
      # through, and a result that is not a number raises ArgumentError.
      result = (self <=> other)
      return false if result.nil?
      unless result.is_a?(Numeric)
        raise ArgumentError, "comparison of #{self.class} with #{other.class} failed"
      end
      result == 0
    ensure
      stack.pop
    end
  end
end

# 1.9 returned Enumerators from these; Ruby 1.9.3+ returns Arrays.
class String
  # Ruby 2.4: the first element of #unpack, without building the whole array.
  def unpack1(format, offset: 0)
    # self[offset..] would be neater, but String#[] here rejects an endless range
    (offset > 0 ? self[offset, bytesize - offset] : self).unpack(format).first
  end unless method_defined?(:unpack1)

  unless "a".lines.is_a?(Array)
    alias_method :lines_enumerator, :lines
    alias_method :chars_enumerator, :chars
    alias_method :bytes_enumerator, :bytes

    def lines(*args, &block)
      return lines_enumerator(*args, &block) if block
      lines_enumerator(*args).to_a
    end

    def chars(&block)
      return chars_enumerator(&block) if block
      chars_enumerator.to_a
    end

    def bytes(&block)
      return bytes_enumerator(&block) if block
      bytes_enumerator.to_a
    end
  end
end

class Hash
  # the core raises IndexError; Ruby 1.9 introduced KeyError for this
  unless (begin; {}.fetch(:missing); rescue KeyError; true; rescue IndexError; false; end)
    alias_method :fetch_raising_index_error, :fetch

    def fetch(key, *default, &block)
      return fetch_raising_index_error(key, *default, &block) if !default.empty? || block
      return self[key] if key?(key)
      raise KeyError.new("key not found: #{key.inspect}", receiver: self, key: key)
    end
  end
end

# Ruby 3.4 changed Hash#inspect from {:a=>1} to {a: 1}, quoting symbol keys
# that are not simple identifiers, and spacing the rocket for other keys.
class Hash
  def inspect
    return "{}" if empty?
    stack = (Thread.current[:__hash_inspect__] ||= [])
    return "{...}" if stack.any? { |seen| seen.equal?(self) }
    stack.push(self)
    begin
      body = map { |k, v|
        if k.is_a?(Symbol)
          name = k.to_s
          # `a=` is not a valid label, so it has to be quoted
          key = name =~ /\A[A-Za-z_][A-Za-z0-9_]*[?!]?\z/ ? name : name.inspect
          "#{key}: #{v.inspect}"
        else
          "#{k.inspect} => #{v.inspect}"
        end
      }
      "{" + body.join(", ") + "}"
    ensure
      stack.pop
    end
  end
  alias_method :to_s, :inspect
end

# Mutex/Queue/SizedQueue/ConditionVariable needed `require "thread"` in 1.9; they
# have been built in since 2.0. Ruby 2.x also re-homed them under Thread.
require "thread" unless Object.const_defined?(:Mutex)

class Thread
  %i[Mutex Queue SizedQueue ConditionVariable].each do |name|
    next if const_defined?(name)
    const_set(name, Object.const_get(name)) if Object.const_defined?(name)
  end
end

class Regexp
  def match?(str, pos = 0)
    return false if str.nil?
    !match(pos.zero? ? str : str.to_s[pos..-1].to_s).nil?
  end unless method_defined?(:match?)
end

# MatchData knew nothing about named groups - m[:name] was a TypeError about
# converting a Symbol into an Integer - and had none of the byte-offset or
# pattern-matching methods. The group names come from the CLR Match through the
# three plain CLR methods added to MatchData.cs; everything else is built on
# top of them and of #string, which is the subject the offsets refer to.
class MatchData
  alias_method :__ir_index__, :[]

  def [](*args)
    if args.size == 1
      key = args[0]
      if key.is_a?(::Symbol) || key.is_a?(::String)
        name = key.to_s
        unless HasNamedGroup(name)
          ::Kernel.raise(::IndexError, "undefined group name reference: #{name}")
        end
        return nil unless NamedGroupSuccess(name)
        start = GetNamedGroupStart(name)
        return string[start, GetNamedGroupLength(name)]
      end
    end
    __ir_index__(*args)
  end

  def names
    GetGroupNames().map { |n| n.to_s }
  end unless method_defined?(:names)

  def named_captures(symbolize_names: false)
    result = {}
    names.each do |n|
      result[symbolize_names ? n.to_sym : n] = self[n]
    end
    result
  end unless method_defined?(:named_captures)

  def deconstruct
    captures
  end unless method_defined?(:deconstruct)

  def deconstruct_keys(keys)
    all = names
    result = {}
    if keys.nil?
      all.each { |n| result[n.to_sym] = self[n] }
      return result
    end
    keys.each do |k|
      k = k.to_sym
      return result unless all.include?(k.to_s)
      result[k] = self[k.to_s]
    end
    result
  end unless method_defined?(:deconstruct_keys)

  def match(n)
    self[n.is_a?(::Integer) ? n : n.to_s]
  end unless method_defined?(:match)

  def match_length(n)
    m = match(n)
    m && m.length
  end unless method_defined?(:match_length)

  def __group_bounds__(n)
    if n.is_a?(::Integer)
      s = self.begin(n)
      return nil if s.nil?
      [s, self.end(n)]
    else
      name = n.to_s
      unless HasNamedGroup(name)
        ::Kernel.raise(::IndexError, "undefined group name reference: #{name}")
      end
      return nil unless NamedGroupSuccess(name)
      s = GetNamedGroupStart(name)
      [s, s + GetNamedGroupLength(name)]
    end
  end
  private :__group_bounds__

  # Character offsets in, byte offsets out: the subject's own bytes decide.
  def byteoffset(n)
    bounds = __group_bounds__(n)
    return [nil, nil] if bounds.nil?
    subject = string
    [subject[0, bounds[0]].bytesize, subject[0, bounds[1]].bytesize]
  end unless method_defined?(:byteoffset)

  def bytebegin(n)
    byteoffset(n)[0]
  end unless method_defined?(:bytebegin)

  def byteend(n)
    byteoffset(n)[1]
  end unless method_defined?(:byteend)

  alias_method :__ir_values_at__, :values_at

  def values_at(*indexes)
    indexes.map { |i| self[i] }
  end
end

class Symbol
  def match?(pattern)
    !to_s.match(pattern).nil?
  end unless method_defined?(:match?)

  def start_with?(*prefixes)
    to_s.start_with?(*prefixes)
  end unless method_defined?(:start_with?)

  def end_with?(*suffixes)
    to_s.end_with?(*suffixes)
  end unless method_defined?(:end_with?)
end

class String
  # Ruby 2.3: -"str" returns a frozen (deduplicated) string, +"str" an unfrozen one.
  def -@
    frozen? ? self : dup.freeze
  end unless method_defined?(:-@)
end

# --- pieces the Ruby 4.0 standard library expects --------------------------

# ruby2_keywords (2.7) flags a method/proc so a trailing Hash keeps its
# "these were keywords" marking when delegated. Keywords are lowered onto a
# trailing Hash here anyway, so recording the flag is all that is needed.
class Module
  def ruby2_keywords(*names)
    names
  end unless private_method_defined?(:ruby2_keywords) || method_defined?(:ruby2_keywords)
  private :ruby2_keywords rescue nil
end

# ARGF was a plain Object carrying singleton methods, so it had no class to
# speak of: `ARGF.class` answered Object and `ARGF.class.new("a", "b")` - which
# is how mspec's argf helper builds an instance it can close afterwards - made
# a bare Object. The helper then died on #file, never reached the `@argf = nil`
# in its ensure, and every later example in the file refused to run with
# "Cannot nest calls to the argf helper": 103 of spec/core/argf's 115 errors
# came from that one missing class.
#
# This is a real implementation over a list of filenames, with "-" meaning
# standard input, and ARGF is rebound to an instance of it reading ARGV.
class ARGFClass
  include ::Enumerable

  attr_accessor :lineno

  def initialize(*argv)
    @argv = argv.flatten
    @current = nil
    @current_path = nil
    @lineno = 0
    @finished = false
    @binmode = false
  end

  def argv
    @argv
  end

  # Opens the next file in the list, or returns false when there are none left.
  def __advance__
    return false if @finished
    if @argv.empty?
      return false if @current
      @current = ::STDIN
      @current_path = "-"
      return true
    end
    name = @argv.shift
    @current_path = name
    @current = name == "-" ? ::STDIN : ::File.open(name, @binmode ? "rb" : "r")
    true
  end
  private :__advance__

  def __stream__
    @current = nil if @current && @current != ::STDIN && @current.closed?
    __advance__ unless @current
    @current
  end
  private :__stream__

  def file
    __stream__
    @current || ::STDIN
  end

  def filename
    __stream__
    @current_path || "-"
  end
  alias_method :path, :filename

  def to_io
    file
  end

  def to_s
    "ARGF"
  end
  alias_method :inspect, :to_s

  def fileno
    file.fileno
  end
  alias_method :to_i, :fileno

  def binmode
    @binmode = true
    @current.binmode if @current.respond_to?(:binmode)
    self
  end

  def binmode?
    @binmode
  end

  def closed?
    s = @current
    s.nil? ? true : s.closed?
  end

  def close
    s = file
    ::Kernel.raise(::IOError, "closed stream") if s.closed?
    s.close unless s == ::STDIN
    @current = nil
    @lineno = 0
    self
  end

  def eof?
    s = __stream__
    return true if s.nil?
    return false unless s.eof?
    # The stream is done, but another file may follow.
    while s && s.eof?
      break if @argv.empty?
      s.close unless s == ::STDIN
      @current = nil
      s = __stream__
    end
    s.nil? || s.eof?
  end
  alias_method :eof, :eof?

  def gets(*args)
    loop do
      s = __stream__
      return nil if s.nil?
      line = s.gets(*args)
      if line
        @lineno += 1
        return line
      end
      s.close unless s == ::STDIN
      @current = nil
      if @argv.empty?
        @finished = true
        return nil
      end
    end
  end

  def readline(*args)
    line = gets(*args)
    ::Kernel.raise(::EOFError, "end of file reached") if line.nil?
    line
  end

  def each_line(*args)
    return ::Enumerator.new { |y| each_line(*args) { |l| y << l } } unless block_given?
    while (line = gets(*args))
      yield line
    end
    self
  end
  alias_method :each, :each_line

  def readlines(*args)
    result = []
    while (line = gets(*args))
      result << line
    end
    result
  end
  alias_method :to_a, :readlines

  def read(length = nil, buffer = nil)
    result = +""
    loop do
      s = __stream__
      break if s.nil?
      want = length.nil? ? nil : length - result.bytesize
      break if want && want <= 0
      piece = s.read(want)
      result << piece if piece && !piece.empty?
      break if length && result.bytesize >= length
      s.close unless s == ::STDIN
      @current = nil
      if @argv.empty?
        @finished = true
        break
      end
    end
    if length
      return nil if result.empty?
    end
    buffer ? buffer.replace(result) : result
  end

  def getc
    loop do
      s = __stream__
      return nil if s.nil?
      c = s.getc
      return c if c
      s.close unless s == ::STDIN
      @current = nil
      if @argv.empty?
        @finished = true
        return nil
      end
    end
  end

  def readchar
    c = getc
    ::Kernel.raise(::EOFError, "end of file reached") if c.nil?
    c
  end

  def each_char
    return ::Enumerator.new { |y| each_char { |c| y << c } } unless block_given?
    while (c = getc)
      yield c
    end
    self
  end
  alias_method :chars, :each_char

  def each_byte
    return ::Enumerator.new { |y| each_byte { |b| y << b } } unless block_given?
    each_char { |c| c.each_byte { |b| yield b } }
    self
  end
  alias_method :bytes, :each_byte

  def pos
    file.pos
  end
  alias_method :tell, :pos

  def pos=(value)
    file.pos = value
  end

  def seek(*args)
    file.seek(*args)
  end

  def rewind
    s = file
    ::Kernel.raise(::ArgumentError, "no stream to rewind") if s.nil?
    s.rewind
    @lineno = 0
    0
  end

  def skip
    if @current && @current != ::STDIN
      @current.close
    end
    @current = nil
    self
  end

  def external_encoding
    file.external_encoding
  end

  def internal_encoding
    file.internal_encoding
  end
end

# ENV is Hash-shaped but was missing a third of the shape. Everything here is
# written in terms of the accessors it does have, so it stays in step with the
# real environment rather than a snapshot of it.
class << ENV
  def merge!(*others)
    others.each do |other|
      other.each do |key, value|
        if block_given? && key?(key.to_s)
          value = yield(key.to_s, self[key.to_s], value)
        end
        self[key.to_s] = value.nil? ? nil : value.to_s
      end
    end
    self
  end unless respond_to?(:merge!)

  alias_method :update, :merge! unless respond_to?(:update)

  def keep_if
    return to_enum(:keep_if) unless block_given?
    to_hash.each { |k, v| self[k] = nil unless yield(k, v) }
    self
  end unless respond_to?(:keep_if)

  def select!
    return to_enum(:select!) unless block_given?
    changed = false
    to_hash.each do |k, v|
      unless yield(k, v)
        self[k] = nil
        changed = true
      end
    end
    changed ? self : nil
  end unless respond_to?(:select!)

  alias_method :filter!, :select! unless respond_to?(:filter!)

  def slice(*keys)
    result = {}
    keys.each do |k|
      k = k.to_s
      result[k] = self[k] if key?(k)
    end
    result
  end unless respond_to?(:slice)

  def except(*keys)
    keys = keys.map { |k| k.to_s }
    to_hash.reject { |k, _| keys.include?(k) }
  end unless respond_to?(:except)

  def assoc(key)
    key = key.to_s
    key?(key) ? [key, self[key]] : nil
  end unless respond_to?(:assoc)

  def rassoc(value)
    value = value.to_s
    to_hash.each { |k, v| return [k, v] if v == value }
    nil
  end unless respond_to?(:rassoc)

  def key(value)
    unless value.is_a?(::String)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{value.class} into String")
    end
    to_hash.each { |k, v| return k if v == value }
    nil
  end unless respond_to?(:key)

  def to_set(*args, &block)
    require 'set'
    ::Set.new(to_hash.to_a, *args, &block)
  end unless respond_to?(:to_set)
end

class Proc
  def ruby2_keywords
    self
  end unless method_defined?(:ruby2_keywords)

  # Composition: (f >> g).call(x) is g(f(x)), (f << g).call(x) is f(g(x)).
  def >>(other)
    unless other.respond_to?(:call)
      ::Kernel.raise(::TypeError, "callable object is expected")
    end
    this = self
    ::Proc.new { |*args, &blk| other.call(this.call(*args, &blk)) }
  end unless method_defined?(:>>)

  def <<(other)
    unless other.respond_to?(:call)
      ::Kernel.raise(::TypeError, "callable object is expected")
    end
    this = self
    ::Proc.new { |*args, &blk| this.call(other.call(*args, &blk)) }
  end unless method_defined?(:<<)

  # Collects arguments until there are enough, then calls. A lambda's arity is
  # binding; a plain proc's is not, so curry on one needs the arity spelled out.
  def curry(arity = nil)
    n = arity.nil? ? self.arity : ::Kernel.Integer(arity)
    if lambda?
      a = self.arity
      if arity.nil?
        n = a < 0 ? -a - 1 : a
      elsif a >= 0 && n != a
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{n}, expected #{a})")
      elsif a < 0 && n < -a - 1
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{n}, expected #{-a - 1}+)")
      end
    else
      n = n < 0 ? -n - 1 : n if arity.nil?
    end
    __curry__(self, n, [])
  end unless method_defined?(:curry)

  def __curry__(target, arity, collected)
    ::Kernel.lambda do |*args|
      all = collected + args
      if all.size >= arity
        target.call(*all)
      else
        target.__curry__(target, arity, all)
      end
    end
  end
  protected :__curry__
end

class Method
  def name
    self.Name.to_s.to_sym
  end unless method_defined?(:name)

  def original_name
    name
  end unless method_defined?(:original_name)

  def receiver
    self.Target
  end unless method_defined?(:receiver)

  def owner
    self.GetTargetClass
  end unless method_defined?(:owner)

  def curry(arity = nil)
    to_proc.curry(arity)
  end unless method_defined?(:curry)

  def >>(other)
    to_proc >> other
  end unless method_defined?(:>>)

  def <<(other)
    to_proc << other
  end unless method_defined?(:<<)
end

class UnboundMethod
  def bind_call(receiver, *args, &block)
    bind(receiver).call(*args, &block)
  end unless method_defined?(:bind_call)
end

module Math
  class << self
    # exp(x)-1 and log(1+x), accurate near zero, which is the whole point of
    # having them separately from exp and log.
    def expm1(x)
      ::System::Math.Exp(::Kernel.Float(x)) - 1.0
    end unless respond_to?(:expm1)

    def log1p(x)
      x = ::Kernel.Float(x)
      ::Kernel.raise(::Math::DomainError, 'Numerical argument is out of domain - log1p') if x < -1.0
      ::System::Math.Log(1.0 + x)
    end unless respond_to?(:log1p)
  end
end

module Kernel
  private

  def ruby2_keywords(*names)
    names
  end unless private_method_defined?(:ruby2_keywords)
end

module Process
  CLOCK_REALTIME = :CLOCK_REALTIME unless const_defined?(:CLOCK_REALTIME)
  CLOCK_MONOTONIC = :CLOCK_MONOTONIC unless const_defined?(:CLOCK_MONOTONIC)
  CLOCK_PROCESS_CPUTIME_ID = :CLOCK_PROCESS_CPUTIME_ID unless const_defined?(:CLOCK_PROCESS_CPUTIME_ID)

  unless respond_to?(:clock_gettime)
    # .NET's Stopwatch is the monotonic source; Time.now covers the wall clock.
    def self.clock_gettime(clock_id = CLOCK_MONOTONIC, unit = :float_second)
      seconds =
        case clock_id
        when CLOCK_REALTIME then Time.now.to_f
        else System::Diagnostics::Stopwatch.get_timestamp.to_f / System::Diagnostics::Stopwatch.frequency.to_f
        end

      case unit
      when :float_second then seconds
      when :float_millisecond then seconds * 1_000.0
      when :float_microsecond then seconds * 1_000_000.0
      when :second then seconds.to_i
      when :millisecond then (seconds * 1_000).to_i
      when :microsecond then (seconds * 1_000_000).to_i
      when :nanosecond then (seconds * 1_000_000_000).to_i
      else seconds
      end
    end
  end
end

# Random (1.9.2) — the runtime only exposes Kernel#rand/srand.
unless defined?(Random)
  class Random
    def initialize(seed = Random.new_seed)
      @seed = seed
      @native = System::Random.new(seed.hash & 0x7fffffff)
    end

    attr_reader :seed

    def rand(limit = nil)
      case limit
      when nil then @native.next_double
      when Range
        span = limit.end - limit.begin
        span = span.to_i + (limit.exclude_end? ? 0 : 1)
        limit.begin + @native.next(span)
      when Float then @native.next_double * limit
      else @native.next(limit.to_i)
      end
    end

    def bytes(count)
      buffer = System::Array[System::Byte].new(count)
      @native.next_bytes(buffer)
      buffer.to_a.pack("C*")
    end

    def self.new_seed
      Time.now.to_f.hash ^ object_id
    end

    def self.rand(limit = nil)
      (@default ||= new).rand(limit)
    end

    def self.bytes(count)
      (@default ||= new).bytes(count)
    end

    def self.srand(number = new_seed)
      previous = @seed_value
      @seed_value = number
      @default = new(number)
      previous || 0
    end
  end
end

class Random
  # Real entropy from the OS. /dev/urandom is the same source MRI uses on Unix;
  # the System.Security.Cryptography assembly is not loadable from here.
  def self.urandom(count)
    File.open("/dev/urandom", "rb") { |f| f.read(count) }
  end unless respond_to?(:urandom)
end

module ObjectSpace
  # A real weak map needs runtime support; this keeps strong references, which
  # is safe (entries merely outlive what MRI would collect) but not weak.
  class WeakMap
    include Enumerable

    def initialize
      @table = {}
    end

    def [](key); @table[key.object_id] && @table[key.object_id][1]; end
    def []=(key, value); @table[key.object_id] = [key, value]; end
    def key?(key); @table.key?(key.object_id); end
    alias_method :member?, :key?
    alias_method :include?, :key?
    def each; @table.each_value { |(k, v)| yield(k, v) }; self; end
    def keys; @table.values.map { |(k, _)| k }; end
    def values; @table.values.map { |(_, v)| v }; end
    def size; @table.size; end
    alias_method :length, :size
    def delete(key); entry = @table.delete(key.object_id); entry && entry[1]; end
  end unless const_defined?(:WeakMap)
end

# Class.try_convert (1.9): the conversion protocol, returning nil instead of raising.
class String
  def self.try_convert(obj)
    obj.respond_to?(:to_str) ? obj.to_str : nil
  end unless respond_to?(:try_convert)
end

class Array
  def self.try_convert(obj)
    obj.respond_to?(:to_ary) ? obj.to_ary : nil
  end unless respond_to?(:try_convert)
end

class Hash
  def self.try_convert(obj)
    obj.respond_to?(:to_hash) ? obj.to_hash : nil
  end unless respond_to?(:try_convert)
end

class Integer
  def self.try_convert(obj)
    obj.respond_to?(:to_int) ? obj.to_int : nil
  end unless respond_to?(:try_convert)
end

class IO
  def self.try_convert(obj)
    obj.respond_to?(:to_io) ? obj.to_io : nil
  end unless respond_to?(:try_convert)
end

# caller_locations (2.0) and the Location objects it yields. The runtime only
# offers caller strings, so parse those: "path:lineno:in `label'".
class Thread
  # A thread's own stack is reachable through Kernel#caller; another thread's is
  # not, and IronRuby has no way to walk it, so those answer the same thing MRI
  # answers for a thread that has already finished.
  def backtrace(*args)
    return nil unless self == ::Thread.current
    __slice_stack__(::Kernel.send(:caller, 1), args)
  end unless method_defined?(:backtrace)

  def backtrace_locations(*args)
    return nil unless self == ::Thread.current
    __slice_stack__(::Kernel.send(:caller_locations, 1), args)
  end unless method_defined?(:backtrace_locations)

  # Both take (start, length) or a Range, like Kernel#caller.
  def __slice_stack__(frames, args)
    return frames if args.empty?
    if args[0].is_a?(::Range)
      return frames[args[0]]
    end
    start = ::Kernel.Integer(args[0])
    return nil if start > frames.size
    frames = frames[start..-1] || []
    args.size > 1 && !args[1].nil? ? frames.first(::Kernel.Integer(args[1])) : frames
  end
  private :__slice_stack__

  # There is no asynchronous-interrupt queue here, so nothing is ever pending.
  def pending_interrupt?(error = nil)
    false
  end unless method_defined?(:pending_interrupt?)

  def self.pending_interrupt?(error = nil)
    false
  end unless respond_to?(:pending_interrupt?)

  def self.each_caller_location(&block)
    return ::Kernel.send(:caller_locations, 1).each unless block
    ::Kernel.send(:caller_locations, 1).each { |l| block.call(l) }
    nil
  end unless respond_to?(:each_caller_location)

  def native_thread_id
    return nil unless alive?
    __native_thread_id__
  end unless method_defined?(:native_thread_id)

  def __native_thread_id__
    if self == ::Thread.current
      ::System::Environment.CurrentManagedThreadId
    else
      object_id
    end
  end
  private :__native_thread_id__

  class Backtrace
    class Location
      attr_reader :path, :lineno, :label

      def initialize(path, lineno, label)
        @path = path
        @lineno = lineno
        @label = label
      end

      def absolute_path
        return nil if @path.nil? || @path.start_with?("(")
        File.expand_path(@path) rescue @path
      end

      def base_label; @label; end

      def to_s
        @label ? "#{@path}:#{@lineno}:in `#{@label}'" : "#{@path}:#{@lineno}"
      end

      def inspect; to_s.inspect; end
    end unless const_defined?(:Location)
  end unless const_defined?(:Backtrace)
end

module Kernel
  private

  def caller_locations(start = 1, length = nil)
    entries = caller(start + 1)
    return nil if entries.nil?
    entries = entries.first(length) if length
    entries.map do |entry|
      if (m = /\A(.*):(\d+)(?::in [`'](.*)')?\z/.match(entry))
        Thread::Backtrace::Location.new(m[1], m[2].to_i, m[3])
      else
        Thread::Backtrace::Location.new(entry, 0, nil)
      end
    end
  end unless private_method_defined?(:caller_locations)
end

# --- Hash: pieces of the 2.x-4.0 surface the 1.9 core never had ------------

class Hash
  private def __hash_operand__(other)
    return other if other.is_a?(Hash)
    if other.respond_to?(:to_hash)
      converted = other.to_hash
      return converted if converted.is_a?(Hash)
    end
    raise TypeError, "no implicit conversion of #{other.nil? ? 'nil' : other.class} into Hash"
  end

  # true if every pair of `sub` is present in `sup` (values compared with ==)
  private def __hash_subset__(sub, sup)
    sub.each { |k, v| return false unless sup.key?(k) && sup[k] == v }
    true
  end

  def <(other)
    other = __hash_operand__(other)
    size < other.size && __hash_subset__(self, other)
  end unless method_defined?(:<)

  def <=(other)
    other = __hash_operand__(other)
    size <= other.size && __hash_subset__(self, other)
  end unless method_defined?(:<=)

  def >(other)
    other = __hash_operand__(other)
    size > other.size && __hash_subset__(other, self)
  end unless method_defined?(:>)

  def >=(other)
    other = __hash_operand__(other)
    size >= other.size && __hash_subset__(other, self)
  end unless method_defined?(:>=)

  # Hash#to_proc (2.3) is a lambda, so its arity is enforced.
  def to_proc
    hash = self
    ->(key) { hash[key] }
  end unless method_defined?(:to_proc)

  def compact!
    reject! { |_, v| v.nil? }
  end unless method_defined?(:compact!)

  def select!(&block)
    return to_enum(:select!) unless block
    raise FrozenError, "can't modify frozen Hash: #{inspect}" if frozen?
    before = size
    keep_if(&block)
    size == before ? nil : self
  end unless method_defined?(:select!)

  def default_proc=(proc)
    raise FrozenError, "can't modify frozen Hash: #{inspect}" if frozen?
    self.DefaultValue = nil
    if proc.nil?
      self.DefaultProc = nil
      return nil
    end
    unless proc.is_a?(Proc)
      converted = proc.respond_to?(:to_proc) ? proc.to_proc : nil
      unless converted.is_a?(Proc)
        raise TypeError, "no implicit conversion of #{proc.class} into Proc"
      end
      proc = converted
    end
    if proc.lambda? && proc.arity != 2 && proc.arity >= 0
      raise TypeError, "default_proc takes two arguments (2 for #{proc.arity})"
    end
    self.DefaultProc = proc
    proc
  end unless method_defined?(:default_proc=)

  # Hash#merge/#merge! take any number of hashes since 2.6 (zero included).
  alias_method :__merge_one__, :merge
  alias_method :__update_one__, :merge!

  def merge(*others, &block)
    result = dup
    others.each { |other| result.__update_one__(other, &block) }
    result
  end

  def merge!(*others, &block)
    raise FrozenError, "can't modify frozen Hash: #{inspect}" if frozen?
    others.each { |other| __update_one__(other, &block) }
    self
  end
  alias_method :update, :merge!

  # CRuby's #filter/#filter! are genuine aliases of #select/#select!, and
  # Hash#select is not Enumerable#select, so re-alias them here.
  alias_method :filter, :select
  alias_method :filter!, :select!

  # #delete_if / #keep_if / #reject / #reject! return an Enumerator when called
  # without a block; the 1.9 core raised LocalJumpError instead.
  alias_method :__delete_if_block__, :delete_if
  def delete_if(&block)
    raise FrozenError, "can't modify frozen Hash: #{inspect}" if frozen? && block
    return to_enum(:delete_if) unless block
    __delete_if_block__(&block)
  end

  def keep_if(&block)
    raise FrozenError, "can't modify frozen Hash: #{inspect}" if frozen? && block
    return to_enum(:keep_if) unless block
    delete_if { |k, v| !block.call(k, v) }
  end

  alias_method :__reject_bang_block__, :reject!
  def reject!(&block)
    return to_enum(:reject!) unless block
    raise FrozenError, "can't modify frozen Hash: #{inspect}" if frozen?
    __reject_bang_block__(&block)
  end

  # CRuby's #reject returns a plain Hash: neither the default value nor the
  # default proc carries over.
  def reject(&block)
    return to_enum(:reject) unless block
    result = __result_hash__
    each { |k, v| result[k] = v unless block.call(k, v) }
    result
  end

  # Hash#shift returns nil on an empty hash since 3.0 - it no longer consults
  # the default value or the default proc.
  def shift
    raise FrozenError, "can't modify frozen Hash: #{inspect}" if frozen?
    return nil if empty?
    key = keys.first
    [key, delete(key)]
  end

  # #assoc / #rassoc compare with ==, and stop at the first match (which matters
  # for an identity hash holding several equal-but-distinct keys).
  def assoc(key)
    each { |k, v| return [k, v] if k == key }
    nil
  end

  def rassoc(value)
    each { |k, v| return [k, v] if v == value }
    nil
  end

  # #slice must not go through an overridden #[].
  def slice(*wanted)
    snapshot = __result_hash__
    each { |k, v| snapshot[k] = v }
    result = __result_hash__
    wanted.each { |k| result[k] = snapshot[k] if snapshot.key?(k) }
    result
  end

  # Hash#eql? (and the value comparison Hash#== performs) - compares sizes,
  # then looks each key up with eql?/hash semantics and compares values with
  # #eql?. Pairs already on the comparison stack count as equal so that
  # mutually recursive hashes terminate.
  def eql?(other)
    return true if equal?(other)
    return false unless other.is_a?(Hash)
    return false if size != other.size
    pairs = (Thread.current[:__hash_eql_pairs__] ||= [])
    entry = [object_id, other.object_id]
    return true if pairs.include?(entry)
    pairs.push(entry)
    begin
      each do |k, v|
        return false unless other.key?(k)
        return false unless v.eql?(other[k])
      end
      true
    ensure
      pairs.pop
    end
  end

  # Order-independent #hash. Like CRuby, a hash that (directly or indirectly)
  # contains itself hashes to one fixed value, which is what keeps
  # `h.hash == {x: h}.hash` true for `h = {}; h[:x] = h`.
  private def __recursive_hash_value__
    0x48415348
  end

  # 32-bit avalanche, so that XOR-ing the per-pair hashes together (which is what
  # makes #hash order-independent) cannot cancel out equal values.
  private def __mix32__(value)
    value &= 0xFFFFFFFF
    value = ((value ^ (value >> 16)) * 0x45D9F3B) & 0xFFFFFFFF
    value = ((value ^ (value >> 16)) * 0x45D9F3B) & 0xFFFFFFFF
    value ^ (value >> 16)
  end

  private def __hash_digest__
    result = __mix32__(size)
    each { |k, v| result ^= __mix32__([k, v].hash) }
    result
  end

  def hash
    stack = Thread.current[:__hash_hash_stack__]
    if stack
      throw stack if stack.any? { |o| o.equal?(self) }
      stack.push(self)
      begin
        __hash_digest__
      ensure
        stack.pop
      end
    else
      stack = [self]
      Thread.current[:__hash_hash_stack__] = stack
      begin
        boxed = catch(stack) { [__hash_digest__] }
        boxed.is_a?(Array) ? boxed[0] : __recursive_hash_value__
      ensure
        Thread.current[:__hash_hash_stack__] = nil
      end
    end
  end
end

# Hash.ruby2_keywords_hash / .ruby2_keywords_hash? (2.7). Keyword arguments are
# lowered onto a trailing Hash in this implementation, so the flag is only ever
# informational; it is stored as a hidden instance variable.
class << Hash
  # the core's try_convert reports a different error and does not accept a nil
  # result from #to_hash
  def try_convert(object)
    return object if object.is_a?(Hash)
    return nil unless object.respond_to?(:to_hash)
    converted = object.to_hash
    return nil if converted.nil?
    unless converted.is_a?(Hash)
      raise TypeError, "can't convert #{object.class} into Hash (#{object.class}#to_hash gives #{converted.class})"
    end
    converted
  end

  def ruby2_keywords_hash?(hash)
    unless hash.is_a?(Hash)
      raise TypeError, "wrong argument type #{hash.class} (expected Hash)"
    end
    hash.instance_variable_defined?(:@__ruby2_keywords__) &&
      !!hash.instance_variable_get(:@__ruby2_keywords__)
  end unless respond_to?(:ruby2_keywords_hash?)

  def ruby2_keywords_hash(hash)
    unless hash.is_a?(Hash)
      raise TypeError, "wrong argument type #{hash.class} (expected Hash)"
    end
    copy = hash.dup
    copy.instance_variable_set(:@__ruby2_keywords__, true)
    copy
  end unless respond_to?(:ruby2_keywords_hash)
end

# KeyError gained #receiver and #key in 2.5; Hash#fetch and friends set them.
class KeyError
  def initialize(message = nil, receiver: nil, key: nil)
    @__receiver = receiver
    @__key = key
    super(message)
  end

  def receiver
    raise ArgumentError, "no receiver is available" if @__receiver.nil?
    @__receiver
  end

  def key
    raise ArgumentError, "no key is available" if @__key.nil?
    @__key
  end
end

class Hash
  def fetch_values(*keys, &block)
    keys.map do |k|
      if key?(k)
        self[k]
      elsif block
        block.call(k)
      else
        raise KeyError.new("key not found: #{k.inspect}", receiver: self, key: k)
      end
    end
  end
end

# --- Struct: the modern surface -------------------------------------------

class Struct
  def to_h(&block)
    result = {}
    if block
      each_pair { |k, v| pair = __to_h_pair__(block.call(k, v)); result[pair[0]] = pair[1] }
    else
      members.each_with_index { |m, i| result[m] = self[i] }
    end
    result
    # instance_methods(false) rather than method_defined?: Struct includes Enumerable,
    # whose #to_h is already defined by this point, so method_defined? is true and this
    # definition would be skipped - leaving Struct with a #to_h that walks each (the
    # values) instead of each_pair.
  end unless instance_methods(false).include?(:to_h)

  def dig(key, *rest)
    value = begin
      self[key]
    rescue NameError, IndexError
      nil
    end
    __dig_step__(value, rest)
  end unless method_defined?(:dig)

  # index of `name` in members, or nil; mirrors CRuby's struct_pos
  private def __struct_pos__(name)
    if name.is_a?(Symbol) || name.is_a?(String)
      members.index(name.to_sym)
    else
      unless name.respond_to?(:to_int)
        raise TypeError, "no implicit conversion of #{name.class} into Integer"
      end
      i = name.to_int
      unless i.is_a?(Integer)
        raise TypeError, "can't convert #{name.class} into Integer (#{name.class}#to_int gives #{i.class})"
      end
      i += size if i < 0
      (i >= 0 && i < size) ? i : nil
    end
  end

  def deconstruct_keys(keys)
    return to_h if keys.nil?
    unless keys.is_a?(Array)
      raise TypeError, "wrong argument type #{keys.class} (expected Array or nil)"
    end
    return {} if size < keys.size
    result = {}
    keys.each do |key|
      index = __struct_pos__(key)
      return result if index.nil?
      result[key] = self[index]
    end
    result
  end

  def values_at(*args)
    values = to_a
    count = values.size
    result = []
    args.each do |arg|
      if arg.is_a?(Range)
        first = arg.begin
        first = first.nil? ? 0 : first.to_int
        first += count if first < 0
        raise RangeError, "#{arg} out of range" if first < 0
        last = arg.end
        if last.nil?
          last = count - 1
        else
          last = last.to_int
          last += count if last < 0
          last -= 1 if arg.exclude_end?
        end
        i = first
        while i <= last
          result << (i < count ? values[i] : nil)
          i += 1
        end
      else
        unless arg.respond_to?(:to_int)
          raise TypeError, "no implicit conversion of #{arg.class} into Integer"
        end
        index = arg.to_int
        normalized = index < 0 ? index + count : index
        if normalized < 0
          raise IndexError, "offset #{index} too small for struct(size:#{count})"
        end
        if normalized >= count
          raise IndexError, "offset #{index} too large for struct(size:#{count})"
        end
        result << values[normalized]
      end
    end
    result
  end

  # #each / #each_pair / #select return an Enumerator when no block is given.
  alias_method :__struct_each__, :each
  def each(&block)
    return to_enum(:each) unless block
    __struct_each__(&block)
  end

  # each_pair yields one [name, value] array, so that a single-parameter block
  # receives the pair rather than just the name
  def each_pair(&block)
    return to_enum(:each_pair) unless block
    names = self.class.members
    names.each_with_index { |name, i| block.call([name, self[i]]) }
    self
  end

  alias_method :deconstruct, :to_a
  alias_method :filter, :select
end

# Struct.new(..., keyword_init: true) (2.5) and StructClass#keyword_init? (3.1).
class << Struct
  alias_method :__struct_new__, :new

  def new(*args, &block)
    keyword_init = nil
    if args.last.is_a?(Hash)
      options = args.pop
      unless (options.keys - [:keyword_init]).empty?
        # CRuby would go on to use the Hash as a member name
        raise TypeError, "no implicit conversion of Hash into Symbol"
      end
      value = options[:keyword_init]
      keyword_init = value.nil? ? value : !!value
    end

    names = args.reject { |a| a.is_a?(String) && a =~ /\A[A-Z]/ }
    seen = {}
    names.each do |name|
      key = name.respond_to?(:to_sym) ? name.to_sym : name
      raise ArgumentError, "duplicate member: #{key}" if seen.key?(key)
      seen[key] = true
    end

    klass = args.empty? ? __struct_new__(nil) : __struct_new__(*args)
    klass.instance_variable_set(:@__keyword_init__, keyword_init)
    # only classes created by Struct.new answer #keyword_init?, not every Struct subclass
    klass.define_singleton_method(:keyword_init?) { @__keyword_init__ }
    klass.module_eval(&block) if block
    klass
  end
end

class Struct
  alias_method :__struct_initialize__, :initialize

  def initialize(*args)
    # self.class.members, not members: a struct may have a member called "members"
    names = self.class.members
    keyword_init = self.class.respond_to?(:keyword_init?) ? self.class.keyword_init? : nil
    args.pop if args.size > 0 && args.last.is_a?(Hash) && args.last.empty? && !keyword_init

    if keyword_init
      unless args.size <= 1 && (args.empty? || args[0].is_a?(Hash))
        raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0)"
      end
      given = args[0] || {}
      unknown = given.keys - names
      unless unknown.empty?
        raise ArgumentError, "unknown keywords: #{unknown.join(', ')}"
      end
      __struct_initialize__(*names.map { |name| given[name] })
    else
      # pad with nil so that a partial re-initialize clears the remaining members
      values = args.dup
      values.concat([nil] * (names.size - values.size)) if values.size < names.size
      __struct_initialize__(*values)
    end
  end
  private :initialize
end

# --------------------------------------------------------------------------
# Exception#detailed_message / #full_message (Ruby 3.2+)
# --------------------------------------------------------------------------
#
# This is the text `ruby` prints for an uncaught exception, exposed as methods
# so that libraries can reuse and override it. Written in Ruby rather than C#
# because it is pure string assembly over #message, #backtrace and #cause, all
# of which already exist.
#
# The escape sequences are MRI's: \e[1m = bold (the message), \e[1;4m = bold
# plus underline (the class name), \e[m = reset. MRI leaves the bold
# unterminated when the class is anonymous and ruby/spec pins that, so the
# missing \e[m below is deliberate.

class Exception
  # MRI answers nil when the exception carries no captured locations, which is
  # every exception here: the backtrace is kept as strings, not as Location
  # objects. Rebuilding Locations from the strings would be guesswork, so this
  # gives the honest answer rather than a fabricated one.
  def backtrace_locations
    nil
  end unless method_defined?(:backtrace_locations)

  # Whether an uncaught exception would be printed to a terminal. Decides the
  # default for the `highlight:` option.
  def self.to_tty?
    $stderr.tty?
  rescue StandardError, NotImplementedError
    false
  end

  def detailed_message(highlight: false, **)
    Exception.send(:__check_highlight__, highlight)

    text = begin
      s = to_s
      s.nil? ? "" : s.to_s
    rescue NoMethodError, TypeError
      ""
    end

    # nil for an anonymous class, in which case MRI leaves the class out
    name = self.class.name

    if text.empty?
      # MRI singles out RuntimeError: it is what a bare `raise "..."` produces,
      # so an empty one means nobody ever gave a reason.
      plain = self.class.equal?(::RuntimeError) ? "unhandled exception" : (name || self.class.to_s)
      return highlight ? "\e[1;4m#{plain}\e[m" : plain
    end

    # a multi-line message carries the class on its first line only
    lines = text.split("\n", -1)
    first = lines.shift
    if highlight
      first = name ? "\e[1m#{first} (\e[1;4m#{name}\e[m\e[1m)\e[m" : "\e[1m#{first}"
      lines = lines.map { |line| "\e[1m#{line}\e[m" }
    elsif name
      first = "#{first} (#{name})"
    end
    lines.unshift(first).join("\n")
  end

  def full_message(highlight: Exception.to_tty?, order: :top, **options)
    Exception.send(:__check_highlight__, highlight)
    unless order == :top || order == :bottom
      raise ArgumentError, "expected :top or :bottom as order: #{order.inspect}"
    end

    result = +""
    if order == :bottom
      result << (highlight ? "\e[1mTraceback\e[m (most recent call last):\n" : "Traceback (most recent call last):\n")
    end
    # `order` is ours; every other keyword (plus the resolved highlight) is
    # handed on to #detailed_message, which the user may have overridden.
    Exception.send(:__append_full_message__, result, self, highlight, order, options, caller)
    result
  end

  def self.__check_highlight__(highlight)
    unless highlight == true || highlight == false
      raise ArgumentError, "expected true or false as highlight: #{highlight.inspect}"
    end
  end
  private_class_method :__check_highlight__

  def self.__detailed__(exc, highlight, options)
    detailed = exc.detailed_message(**options, highlight: highlight) if exc.respond_to?(:detailed_message)
    detailed = detailed.to_str if detailed && !detailed.is_a?(::String) && detailed.respond_to?(:to_str)
    unless detailed.is_a?(::String)
      name = exc.class.name || exc.class.to_s
      detailed = highlight ? "\e[1;4m#{name}\e[m" : name
    end
    detailed
  end
  private_class_method :__detailed__

  # `fallback` is the caller of #full_message, which MRI shows when the
  # exception was never raised and so has no backtrace of its own.
  def self.__append_full_message__(out, exc, highlight, order, options, fallback)
    backtrace = begin
      exc.backtrace
    rescue StandardError
      nil
    end
    backtrace = fallback if backtrace.nil? || backtrace.empty?
    backtrace = [] if backtrace.nil?
    head = backtrace[0]
    rest = backtrace[1..-1] || []
    detailed = __detailed__(exc, highlight, options)
    cause = begin
      exc.cause
    rescue StandardError, NoMethodError
      nil
    end

    if order == :top
      out << (head ? "#{head}: #{detailed}\n" : "#{detailed}\n")
      rest.each { |line| out << "\tfrom #{line}\n" }
      __append_full_message__(out, cause, highlight, order, options, nil) if cause
    else
      __append_full_message__(out, cause, highlight, order, options, nil) if cause
      (rest.size - 1).downto(0) { |i| out << "\t#{i + 1}: from #{rest[i]}\n" }
      out << (head ? "#{head}: #{detailed}\n" : "#{detailed}\n")
    end
  end
  private_class_method :__append_full_message__
end

class Range
  # ---- cover?, overlap?, bsearch, % ---------------------------------------
  #
  # 109 of spec/core/range's errors were #cover? alone and 71 were #bsearch;
  # neither existed.

  def cover?(value)
    return __cover_range__(value) if value.is_a?(::Range)
    b = self.begin
    e = self.end
    unless b.nil?
      c = (b <=> value)
      return false if c.nil? || c > 0
    end
    unless e.nil?
      c = (value <=> e)
      return false if c.nil?
      return false if exclude_end? ? c >= 0 : c > 0
    end
    true
  end unless method_defined?(:cover?)

  def __cover_range__(other)
    ob = other.begin
    oe = other.end
    # An empty range is covered by nothing.
    if !ob.nil? && !oe.nil?
      c = (ob <=> oe)
      return false if c.nil?
      return false if c > 0 || (c == 0 && other.exclude_end?)
    end
    return false if ob.nil? && !self.begin.nil?
    return false if oe.nil? && !self.end.nil?
    return false unless ob.nil? || cover?(ob)
    se = self.end
    return true if se.nil?
    cmp = (se <=> oe)
    return false if cmp.nil?
    # MRI's r_cover_range_p: when the two ranges agree about their end being
    # exclusive the comparison is enough, and when they disagree the inclusive
    # one has to be measured against the other's last element instead.
    if exclude_end? == other.exclude_end?
      cmp >= 0
    elsif exclude_end?
      cmp > 0
    elsif cmp >= 0
      true
    else
      vmax = (other.max rescue nil)
      return false if vmax.nil?
      c = (se <=> vmax)
      !c.nil? && c >= 0
    end
  end
  private :__cover_range__

  def overlap?(other)
    unless other.is_a?(::Range)
      ::Kernel.raise(::TypeError, "wrong argument type #{other.class} (expected Range)")
    end
    return false if __empty_range__(self) || __empty_range__(other)
    sb, se = self.begin, self.end
    ob, oe = other.begin, other.end
    unless se.nil? || ob.nil?
      c = (ob <=> se)
      return false if c.nil?
      return false if exclude_end? ? c >= 0 : c > 0
    end
    unless oe.nil? || sb.nil?
      c = (sb <=> oe)
      return false if c.nil?
      return false if other.exclude_end? ? c >= 0 : c > 0
    end
    true
  end unless method_defined?(:overlap?)

  def __empty_range__(r)
    b, e = r.begin, r.end
    return false if b.nil? || e.nil?
    c = (b <=> e)
    c.nil? || c > 0 || (c == 0 && r.exclude_end?)
  end
  private :__empty_range__

  # Binary search over a numeric range, in MRI's two modes: a block answering
  # true/false finds the smallest element it says true for, a block answering
  # an Integer finds the element it answers 0 for.
  def bsearch(&block)
    return ::Enumerator.new { |y| each { |x| y << x } } unless block
    b = self.begin
    e = self.end
    if (b.nil? || b.is_a?(::Integer)) && (e.nil? || e.is_a?(::Integer))
      __bsearch_int__(block)
    elsif (b.nil? || b.is_a?(::Numeric)) && (e.nil? || e.is_a?(::Numeric))
      __bsearch_float__(block)
    else
      ::Kernel.raise(::TypeError, "can't do binary search for #{(b || e).class}")
    end
  end unless method_defined?(:bsearch)

  # Answers :found, true (go left, remember) or false (go right).
  def __bsearch_test__(block, value)
    r = block.call(value)
    case r
    when true then true
    when false, nil then false
    when ::Integer then r == 0 ? :found : r < 0
    else
      ::Kernel.raise(::TypeError, "wrong argument type #{r.class} (must be numeric, true, false or nil)")
    end
  end
  private :__bsearch_test__

  def __bsearch_int__(block)
    low = self.begin
    high = self.end
    high -= 1 if !high.nil? && exclude_end?
    # An endless or beginless range is widened until the answer is bracketed.
    if low.nil? || high.nil?
      span = 1
      if high.nil?
        high = low + span
        while __bsearch_test__(block, high) == false
          span *= 2
          high = low + span
        end
      else
        low = high - span
        while __bsearch_test__(block, low) != false
          span *= 2
          low = high - span
        end
      end
    end
    result = nil
    while low <= high
      mid = low + (high - low) / 2
      case __bsearch_test__(block, mid)
      when :found then return mid
      when true then result = mid; high = mid - 1
      else low = mid + 1
      end
    end
    result
  end
  private :__bsearch_int__

  def __bsearch_float__(block)
    low = (self.begin || -::Float::MAX).to_f
    high = (self.end || ::Float::MAX).to_f
    result = nil
    64.times do
      mid = low + (high - low) / 2
      break if mid == low || mid == high
      case __bsearch_test__(block, mid)
      when :found then return mid
      when true then result = mid; high = mid
      else low = mid
      end
    end
    result
  end
  private :__bsearch_float__

  def %(n)
    step(n)
  end unless method_defined?(:%)

  # Range#min/#max/#minmax are specialised in MRI: without a block they answer from the
  # endpoints instead of enumerating. IronRuby inherited Enumerable's versions, so
  # (0...2**64).max walked eighteen quintillion integers and never came back.
  #
  # The shape follows MRI's range_max/range_min: only a Numeric begin takes the
  # endpoint shortcut for an exclusive range, which is why ('a'...'f').max is still "e"
  # (enumerated) while (303.20...908.1111).max is a TypeError.

  def __range_count__(n)
    unless n.is_a?(Integer)
      unless n.respond_to?(:to_int)
        raise TypeError, "no implicit conversion of #{n.class} into Integer"
      end
      n = n.to_int
      unless n.is_a?(Integer)
        raise TypeError, "can't convert #{n.class} to Integer"
      end
    end
    raise ArgumentError, "negative size (#{n})" if n < 0
    n
  end
  private :__range_count__

  def max(n = nil, &block)
    e = self.end
    raise RangeError, "cannot get the maximum of endless range" if e.nil?
    b = self.begin

    if n
      n = __range_count__(n)
      # An Integer range can answer without walking: (0...2**64).max(2) must not enumerate.
      if block.nil? && e.is_a?(Integer) && (b.nil? || b.is_a?(Integer))
        last = exclude_end? ? e - 1 : e
        return [] if b && b > last
        count = b.nil? ? n : [n, last - b + 1].min
        return Array.new(count) { |i| last - i }
      end
      # Otherwise enumerate, which is also what makes a Float/Array/Time range a TypeError.
      return (block ? entries.sort(&block) : entries.sort).last(n).reverse
    end

    if b.nil?
      if block
        raise RangeError, "cannot get the maximum of beginless range with custom comparison method"
      end
      if exclude_end?
        raise TypeError, "cannot exclude non Integer end value" unless e.is_a?(Integer)
        return e - 1
      end
      return e
    end

    return super(&block) if block || (exclude_end? && !b.is_a?(Numeric))

    c = (b <=> e)
    return nil if c.nil? || c > 0

    if exclude_end?
      raise TypeError, "cannot exclude non Integer end value" unless e.is_a?(Integer)
      return nil if c == 0
      return e - 1
    end
    e
  end

  def min(n = nil, &block)
    b = self.begin
    raise RangeError, "cannot get the minimum of beginless range" if b.nil?
    if n
      n = __range_count__(n)
      return (block ? entries.sort(&block) : entries.sort).first(n) if block
      return take(n)
    end
    if block
      if self.end.nil?
        raise RangeError, "cannot get the minimum of endless range with custom comparison method"
      end
      return super(&block)
    end

    e = self.end
    c = e.nil? ? -1 : (b <=> e)
    return nil if c.nil? || c > 0 || (c == 0 && exclude_end?)
    b
  end

  def minmax(&block)
    return super(&block) if block
    [min, max]
  end

  # Enumerable#count would walk an endless range forever.
  def count(*args, &block)
    return super if block || !args.empty?
    return Float::INFINITY if self.begin.nil? || self.end.nil?
    super
  end

  def to_set(*args, &block)
    raise RangeError, "cannot convert endless range to a set" if self.end.nil?
    super
  end

  def to_a
    raise RangeError, "cannot convert endless range to an array" if self.end.nil?
    super
  end
  alias_method :entries, :to_a
end

# Rebind ARGF to a real instance so ARGF.class is a class. Done at the very end
# of the prelude so that the class above and File are both in place.
begin
  argf_instance = ARGFClass.new(*ARGV)
  Object.send(:remove_const, :ARGF) if Object.const_defined?(:ARGF)
  Object.const_set(:ARGF, argf_instance)
rescue ::Exception
end
