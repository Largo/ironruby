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

  # --- methods the 1.8-era core never grew -------------------------------
  #
  # All of these hand back a plain Array, never the receiver's subclass, which
  # is what MRI does and what ruby/spec asserts.

  def rotate(count = 1)
    count = __array_to_int__(count)
    return [].concat(self) if empty?
    count %= size
    [].concat(self[count..-1]).concat(self[0, count])
  end unless method_defined?(:rotate)

  def rotate!(count = 1)
    __array_check_frozen__
    replace(rotate(count))
  end unless method_defined?(:rotate!)

  # The deletions have to be visible if the block raises part way through, so
  # this compacts in place rather than building a new array and swapping it in.
  def select!(&block)
    return to_enum(:select!) { size } unless block
    __array_check_frozen__
    original = size
    kept = 0
    index = 0
    begin
      while index < size
        element = self[index]
        if block.call(element)
          self[kept] = element
          kept += 1
        end
        index += 1
      end
    ensure
      # Only the examined-and-rejected span goes; anything the block never saw
      # (because it raised) stays where it is.
      slice!(kept, index - kept) if index > kept
    end
    original == size ? nil : self
  end unless method_defined?(:select!)

  def keep_if(&block)
    return to_enum(:keep_if) { size } unless block
    select!(&block)
    self
  end unless method_defined?(:keep_if)

  def sort_by!(&block)
    return to_enum(:sort_by!) { size } unless block
    __array_check_frozen__
    replace(sort_by(&block))
  end unless method_defined?(:sort_by!)

  # Two modes, as in MRI: find-minimum when the block answers true/false/nil,
  # find-any when it answers a number (negative = look left, 0 = found).
  def bsearch_index(&block)
    return to_enum(:bsearch_index) unless block
    low = 0
    high = size
    satisfied = nil
    while low < high
      middle = low + (high - low) / 2
      answer = block.call(self[middle])
      case answer
      when true
        satisfied = middle
        high = middle
      when false, nil
        low = middle + 1
      when Numeric
        return middle if answer == 0
        if answer < 0
          high = middle
        else
          low = middle + 1
        end
      else
        raise TypeError, "wrong argument type #{answer.class} (must be numeric, true, false or nil)"
      end
    end
    satisfied
  end unless method_defined?(:bsearch_index)

  def bsearch(&block)
    return to_enum(:bsearch) unless block
    index = bsearch_index(&block)
    index && self[index]
  end unless method_defined?(:bsearch)

  def rfind(ifnone = nil, &block)
    return to_enum(:rfind, ifnone) unless block
    index = size - 1
    while index >= 0
      # The array may shrink under us; MRI re-reads the size every step.
      index = size - 1 if index >= size
      break if index < 0
      element = self[index]
      return element if block.call(element)
      index -= 1
    end
    ifnone && ifnone.call
  end unless method_defined?(:rfind)

  def difference(*others)
    others.inject([].concat(self)) { |result, other| result - other }
  end unless method_defined?(:difference)

  # #union uniques the receiver even with no arguments; #intersection does not.
  def union(*others)
    others.inject([].concat(self).uniq) { |result, other| result | other }
  end unless method_defined?(:union)

  def intersection(*others)
    others.inject([].concat(self)) { |result, other| result & other }
  end unless method_defined?(:intersection)

  def repeated_permutation(count, &block)
    count = __array_to_int__(count)
    unless block
      return to_enum(:repeated_permutation, count) {
        count < 0 ? 0 : size ** count
      }
    end
    __array_repeat__(count, false, &block)
  end unless method_defined?(:repeated_permutation)

  def repeated_combination(count, &block)
    count = __array_to_int__(count)
    unless block
      return to_enum(:repeated_combination, count) {
        if count < 0
          0
        elsif count == 0
          1
        else
          __array_binomial__(size + count - 1, count)
        end
      }
    end
    __array_repeat__(count, true, &block)
  end unless method_defined?(:repeated_combination)

  def fetch_values(*indexes, &block)
    indexes.map { |index| block ? fetch(index, &block) : fetch(index) }
  end unless method_defined?(:fetch_values)

  # The core #shuffle takes no arguments; MRI's takes a :random generator and
  # must hand back a plain Array.  Defined unconditionally for that reason.
  def shuffle(random: Random)
    result = [].concat(self)
    index = result.size - 1
    while index > 0
      swap = __array_rand_index__(random, index + 1)
      result[index], result[swap] = result[swap], result[index]
      index -= 1
    end
    result
  end

  def shuffle!(random: Random)
    __array_check_frozen__
    replace(shuffle(random: random))
  end

  def sample(count = nil, random: Random)
    if count.nil?
      return nil if empty?
      return self[__array_rand_index__(random, size)]
    end

    count = __array_to_int__(count)
    raise ArgumentError, "negative sample number" if count < 0
    total = size
    count = total if count > total

    pool = [].concat(self)
    result = []
    index = 0
    while index < count
      swap = index + __array_rand_index__(random, total - index)
      pool[index], pool[swap] = pool[swap], pool[index]
      result << pool[index]
      index += 1
    end
    result
  end unless method_defined?(:sample)

  # --- helpers -----------------------------------------------------------

  private

  def __array_check_frozen__
    raise FrozenError.new("can't modify frozen #{self.class}: #{inspect}", receiver: self) if frozen?
  end

  def __array_to_int__(value)
    return value if value.is_a?(Integer)
    unless value.respond_to?(:to_int)
      raise TypeError, "no implicit conversion of #{value.nil? ? 'nil' : value.class} into Integer"
    end
    converted = value.to_int
    unless converted.is_a?(Integer)
      raise TypeError, "can't convert #{value.class} to Integer (#{value.class}#to_int gives #{converted.class})"
    end
    converted
  end

  # MRI's rb_random_ulong_limited: ask the generator, coerce with #to_int, and
  # insist the answer addresses an existing element.
  def __array_rand_index__(random, limit)
    value = random.rand(limit)
    value = value.to_int unless value.is_a?(Integer)
    raise RangeError, "random number too big #{value}" if value < 0 || value >= limit
    value
  end

  def __array_binomial__(n, k)
    return 0 if k > n
    result = 1
    (1..k).each { |i| result = result * (n - k + i) / i }
    result
  end

  # MRI iterates a snapshot, so mutating the receiver from the block does not
  # change the elements that are yielded.
  def __array_repeat__(count, sorted, &block)
    return self if count < 0
    if count == 0
      block.call([])
      return self
    end
    return self if empty?

    snapshot = [].concat(self)
    indexes = Array.new(count, 0)
    total = snapshot.size
    loop do
      block.call(indexes.map { |i| snapshot[i] })

      position = count - 1
      position -= 1 while position >= 0 && indexes[position] == total - 1
      return self if position < 0
      indexes[position] += 1
      if sorted
        (position + 1...count).each { |i| indexes[i] = indexes[position] }
      else
        (position + 1...count).each { |i| indexes[i] = 0 }
      end
    end
  end

  public

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

class IO
  # autoclose is tracked but not acted on: IronRuby closes descriptors it owns
  # through the CLR stream, and never closes one handed to it from outside.
  def autoclose?
    defined?(@__autoclose) ? @__autoclose : true
  end unless method_defined?(:autoclose?)

  def autoclose=(value)
    @__autoclose = !!value
  end unless method_defined?(:autoclose=)
end

class File
  # True when the path is absolute without consulting the filesystem. "~/x" is
  # not absolute: MRI does no tilde expansion here.
  def self.absolute_path?(path)
    path = ::Kernel.String(path) unless path.is_a?(::String)
    !!(path =~ %r{\A(?:[A-Za-z]:)?[/\\]})
  end unless respond_to?(:absolute_path?)

  # File.size, File.size? and File.directory? take an IO, or anything that
  # converts to one with #to_io, as well as a path; the C# implementations only
  # know about paths.
  class << self
    [:size, :size?, :directory?].each do |name|
      path_only = instance_method(name)
      define_method(name) do |target|
        unless target.is_a?(String) || target.respond_to?(:to_path)
          io = target.respond_to?(:to_io) ? target.to_io : (target.is_a?(IO) ? target : nil)
          if io
            stat = io.stat
            case name
            when :directory? then return stat.directory?
            when :size?      then return stat.size == 0 ? nil : stat.size
            else                  return stat.size
            end
          end
        end
        path_only.bind(self).call(target)
      end
    end
  end

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
    # A constant cannot start with a lower case letter, so CRuby's set_encoding_const()
    # capitalises the first character before it gives up on the name: "macCyrillic" becomes
    # Encoding::MacCyrillic as well as Encoding::MACCYRILLIC, and "eucJP" becomes EucJP.
    __custom__ = __custom__.sub(/\A[a-z]/) { |__ch__| __ch__.upcase }
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

  # Kernel#Integer and #Float: the C# side parses the string grammar (and, since
  # this branch, an explicit radix); the arity, the exception: keyword and the
  # coercion protocol are easier to get exactly right here.
  #
  # MRI's order for Integer(): an Integer or Float is converted directly, a
  # String is parsed, and anything else is asked for #to_int, then #to_i, with
  # #to_str consulted first when the object offers one.
  if private_method_defined?(:Integer) || method_defined?(:Integer)
    alias_method :__ir_Integer__, :Integer
    private :__ir_Integer__

    def Integer(arg, base = nil, exception: true)
      __numeric_conversion__(exception) do
        if arg.nil?
          raise ::TypeError, "can't convert nil into Integer"
        end

        if base && !arg.is_a?(::String) && !arg.respond_to?(:to_str)
          raise ::ArgumentError, "base specified for non string value"
        end

        if arg.is_a?(::String)
          next base ? __ir_Integer__(arg, base) : __ir_Integer__(arg)
        end

        if arg.is_a?(::Integer)
          next arg
        end

        if arg.is_a?(::Float)
          if arg.nan? || arg.infinite?
            raise ::FloatDomainError, arg.to_s
          end
          next arg.to_i
        end

        if arg.respond_to?(:to_str)
          text = arg.to_str
          unless text.is_a?(::String)
            raise ::TypeError, "can't convert #{arg.class} into Integer"
          end
          next base ? __ir_Integer__(text, base) : __ir_Integer__(text)
        end

        # #to_int first, then #to_i; a non-Integer from #to_int is not an error
        # by itself, it just falls through to #to_i.
        if arg.respond_to?(:to_int)
          value = arg.to_int
          next value if value.is_a?(::Integer)
        end

        unless arg.respond_to?(:to_i)
          raise ::TypeError, "can't convert #{__conversion_class_name__(arg)} into Integer"
        end

        value = arg.to_i
        unless value.is_a?(::Integer)
          raise ::TypeError,
                "can't convert #{__conversion_class_name__(arg)} to Integer " \
                "(#{arg.class}#to_i gives #{value.class})"
        end
        value
      end
    end
    module_function :Integer
  end

  if private_method_defined?(:Float) || method_defined?(:Float)
    alias_method :__ir_Float__, :Float
    private :__ir_Float__

    def Float(arg, exception: true)
      __numeric_conversion__(exception) do
        raise ::TypeError, "can't convert nil into Float" if arg.nil?
        __ir_Float__(arg)
      end
    end
    module_function :Float
  end

  # exception: false suppresses a conversion *failure* only - an exception the
  # object's own #to_int or #to_f raises still comes through.
  def __numeric_conversion__(exception)
    yield
  rescue ::TypeError, ::ArgumentError, ::FloatDomainError, ::RangeError
    raise if exception
    nil
  end
  private :__numeric_conversion__
  module_function :__numeric_conversion__

  def __conversion_class_name__(object)
    object.nil? ? "nil" : object.class.to_s
  end
  private :__conversion_class_name__
  module_function :__conversion_class_name__

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


  # ------------------------------------------------------------------
  # Byte-offset searching.
  #
  # A byte offset is not a character offset, and MRI insists that the one you
  # hand it lands on a character boundary.  The search itself has to run in the
  # receiver's real encoding - forcing a binary copy, which is what #byteindex
  # used to do, makes a UTF-8 Regexp needle raise Encoding::CompatibilityError -
  # so the offset is converted to a character offset, the ordinary search runs,
  # and the answer is converted back.
  # ------------------------------------------------------------------

  # Byte offset of the start of every character, plus one final entry for the
  # end of the string.  The Nth entry is the byte offset of character N.
  def __ir_char_starts__
    starts = [0]
    at = 0
    each_char { |ch| at += ch.bytesize; starts << at }
    starts
  end
  private :__ir_char_starts__

  def __ir_byte_search__(reverse, needle, offset)
    starts = __ir_char_starts__
    size = starts.last

    offset += size if offset < 0
    return nil if offset < 0
    if offset > size
      # #index gives up past the end; #rindex clamps to it.
      return nil unless reverse
      offset = size
    end

    char_offset = starts.index(offset)
    raise IndexError, "offset #{offset} does not land on character boundary" if char_offset.nil?

    found = reverse ? rindex(needle, char_offset) : index(needle, char_offset)
    found.nil? ? nil : starts[found]
  end
  private :__ir_byte_search__

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
    # Three shapes: (index, length, str), (range, str) and the five argument
    # (index, length, str, str_index, str_length), which splices only part of the
    # replacement. Four arguments is not one of them, even with a Range first.
    case args.size
    when 2
      index_args, str, sub_args = args[0, 1], args[1], nil
    when 3
      # (range, str, str_range) when the first argument is a Range, otherwise
      # (index, length, str).
      if args[0].is_a?(::Range)
        index_args, str, sub_args = args[0, 1], args[1], args[2, 1]
      else
        index_args, str, sub_args = args[0, 2], args[2], nil
      end
    when 5
      index_args, str, sub_args = args[0, 2], args[2], args[3, 2]
    when 4
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 4, expected 2, 3, or 5)")
    else
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 2..5)")
    end

    unless str.is_a?(::String)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{str.nil? ? 'nil' : str.class} into String")
    end

    if index_args.size == 1
      range = index_args[0]
      unless range.is_a?(::Range)
        ::Kernel.raise(::TypeError, "wrong argument type #{range.nil? ? 'nil' : range.class} (expected Range)")
      end
      index, length = __byte_range__(range)
    else
      index = ::Kernel.Integer(index_args[0])
      length = ::Kernel.Integer(index_args[1])
      # A negative length is rejected before the index is even bounds checked.
      ::Kernel.raise(::IndexError, "negative length #{length}") if length < 0
      index += bytesize if index < 0
      ::Kernel.raise(::IndexError, "index #{index_args[0]} out of string") if index < 0 || index > bytesize
    end

    if sub_args && sub_args.size == 1
      sub_range = sub_args[0]
      unless sub_range.is_a?(::Range)
        ::Kernel.raise(::TypeError, "wrong argument type #{sub_range.nil? ? 'nil' : sub_range.class} (expected Range)")
      end
      sub_index, sub_length = str.__send__(:__byte_range__, sub_range)
      str = str.byteslice(sub_index, sub_length) || str[0, 0]
    elsif sub_args
      sub_index = ::Kernel.Integer(sub_args[0])
      sub_length = ::Kernel.Integer(sub_args[1])
      ::Kernel.raise(::IndexError, "negative length #{sub_length}") if sub_length < 0
      sub_index += str.bytesize if sub_index < 0
      if sub_index < 0 || sub_index > str.bytesize
        ::Kernel.raise(::IndexError, "index #{sub_args[0]} out of string")
      end
      str = str.byteslice(sub_index, sub_length) || str[0, 0]
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

  # MRI takes a String or a Regexp here and nothing else - Kernel.String would happily
  # accept anything with a #to_s - and the empty pieces carry the receiver's encoding.
  def __ir_separator__(pattern)
    return pattern if pattern.is_a?(::Regexp) || pattern.is_a?(::String)
    if pattern.respond_to?(:to_str)
      converted = pattern.to_str
      return converted if converted.is_a?(::String)
    end
    ::Kernel.raise(::TypeError,
      "wrong argument type #{pattern.nil? ? 'nil' : pattern.class} (expected Regexp)")
  end
  private :__ir_separator__

  def partition(pattern)
    pattern = __ir_separator__(pattern)
    empty = self[0, 0]
    if pattern.is_a?(::Regexp)
      m = pattern.match(self)
      return [self[0..-1], empty, empty] unless m
      [m.pre_match, m[0], m.post_match]
    else
      i = index(pattern)
      return [self[0..-1], empty, empty] unless i
      [self[0, i], pattern.dup, self[(i + pattern.length)..-1]]
    end
  end unless method_defined?(:partition)

  def rpartition(pattern)
    pattern = __ir_separator__(pattern)
    empty = self[0, 0]
    if pattern.is_a?(::Regexp)
      start = nil
      pos = 0
      # Regexp#match takes no start offset here, so walk forward keeping the
      # last match that begins at or after each position.
      while pos <= length && (i = index(pattern, pos))
        start = i
        pos = i + 1
      end
      return [empty, empty, self[0..-1]] unless start
      m = pattern.match(self[start..-1])
      [self[0, start], m[0], self[(start + m[0].length)..-1]]
    else
      i = rindex(pattern)
      return [empty, empty, self[0..-1]] unless i
      [self[0, i], pattern.dup, self[(i + pattern.length)..-1]]
    end
  end unless method_defined?(:rpartition)

  def prepend(*others)
    others = others.map do |o|
      # #to_str only: MRI will not call #to_s to find something to prepend.
      converted = o.is_a?(::String) ? o : ::String.try_convert(o)
      converted || ::Kernel.raise(::TypeError,
        "no implicit conversion of #{o.nil? ? 'nil' : o.class} into String")
    end
    replace(others.join + self)
  end unless method_defined?(:prepend)

  def casecmp?(other)
    return nil unless other.is_a?(::String)
    # Two strings in encodings that cannot be compared are not unequal, they are
    # incomparable, and #casecmp? answers nil for them just as #casecmp does.
    return nil if ::Encoding.compatible?(self, other).nil?
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
    # String, not the receiver's class: since Ruby 3.0 only #dup, #clone and #+@ carry a
    # String subclass over to the result.
    return __ir_plain_copy__ if valid_encoding?
    default = encoding == ::Encoding::UTF_8 ? "�" : "?"
    if replacement && !replacement.valid_encoding?
      ::Kernel.raise(::ArgumentError, "replacement must be valid byte sequence '#{replacement.inspect}'")
    end
    out = +""
    out.force_encoding(encoding) if out.respond_to?(:force_encoding)
    # A run of bad bytes that is the start of a character which simply ran out of input -
    # a truncated one at the end of the string - is one replacement, not one per byte, so
    # the bad characters are collected before being handed over.
    pending = nil
    flush = lambda do
      next if pending.nil?
      out << (block ? block.call(pending).to_s : (replacement || default))
      pending = nil
    end
    each_char do |ch|
      if ch.valid_encoding?
        flush.call
        out << ch
      elsif pending && __ir_starts_character__(pending)
        pending += ch
      else
        flush.call
        pending = ch
      end
    end
    flush.call
    out
  end unless method_defined?(:scrub)

  # True when the bytes could be the beginning of a character in this encoding that ran
  # out of input, as opposed to something that can never begin one.
  def __ir_starts_character__(bytes)
    return false if bytes.empty?
    lead = bytes.getbyte(0)
    case encoding
    when ::Encoding::UTF_8
      expected = if lead >= 0xF0 then 4 elsif lead >= 0xE0 then 3 elsif lead >= 0xC2 then 2 else 0 end
      return false if expected == 0 || bytes.bytesize >= expected
      (1...bytes.bytesize).all? { |i| (bytes.getbyte(i) & 0xC0) == 0x80 }
    when ::Encoding::UTF_16LE, ::Encoding::UTF_16BE, ::Encoding::UTF_32LE, ::Encoding::UTF_32BE
      bytes.bytesize < 4
    else
      false
    end
  end
  private :__ir_starts_character__

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

# Numeric#step: MRI's num_step, which the builtin only ever implemented as the
# two-positional-argument block form. Missing were the keyword form
# (`1.step(by: 2, to: 7)`), the endless form (`1.step`), the
# Enumerator::ArithmeticSequence return value, and the float/infinity corners.
class Numeric
  # MRI num_step_extract_args: positional (to, step) merged with the `to:` and
  # `by:` keywords, which collide rather than override.
  def __step_extract_args__(args, kw)
    if args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..2)")
    end
    to = args.size >= 1 ? args[0] : nil
    by = args.size >= 2 ? args[1] : nil
    unless kw.nil? || kw.empty?
      unknown = kw.keys - [:to, :by]
      unless unknown.empty?
        ::Kernel.raise(::ArgumentError, "unknown keyword#{unknown.size > 1 ? 's' : ''}: #{unknown.map(&:inspect).join(', ')}")
      end
      if kw.key?(:to)
        ::Kernel.raise(::ArgumentError, "to is given twice") if args.size > 0
        to = kw[:to]
      end
      if kw.key?(:by)
        ::Kernel.raise(::ArgumentError, "step is given twice") if args.size > 1
        by = kw[:by]
      end
    end
    [to, by]
  end
  private :__step_extract_args__

  # MRI num_step_scan_args. The `unit < 0` is not incidental: it is what turns
  # a String step into ArgumentError("comparison of String with 0 failed"),
  # which is the error the specs ask for.
  def __step_scan_args__(args, kw)
    to, unit = __step_extract_args__(args, kw)
    unit = 1 if unit.nil?
    ::Kernel.raise(::ArgumentError, "step can't be 0") if unit == 0
    unit < 0
    [to, unit]
  end
  private :__step_scan_args__

  def step(*args, **kw, &block)
    unless block
      to, unit = __step_extract_args__(args, kw)
      unit = 1 if unit.nil?
      ::Kernel.raise(::ArgumentError, "step can't be 0") if unit == 0
      if (to.nil? || to.is_a?(::Numeric)) && unit.is_a?(::Numeric)
        shown = args.empty? && (kw.nil? || kw.empty?) ? "" : "(#{(args.map(&:inspect) + (kw || {}).map { |k, v| "#{k}: #{v.inspect}" }).join(', ')})"
        return ::Enumerator::ArithmeticSequence.__build__(
          self, to, unit, false, self, "(#{inspect}.step#{shown})")
      end
      recv = self
      e = ::Enumerator.new { |y| recv.step(*args, **kw) { |v| y << v } }
      e.__set_size__(lambda do
        t, u = recv.__send__(:__step_scan_args__, args, kw)
        t = (u < 0 ? -::Float::INFINITY : ::Float::INFINITY) if t.nil?
        ::Enumerator::ArithmeticSequence.__interval_step_size__(recv, t, u, false)
      end)
      return e
    end

    to, unit = __step_scan_args__(args, kw)
    ::Enumerator::ArithmeticSequence.__step_each__(self, to, unit, false, &block)
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

# The rest of Complex, on top of 1.8's complex.rb.
#
# complex.rb predates Ruby 1.9's Complex by a decade and differs from it
# everywhere that matters: Kernel#Complex(a, b) built the result as
# `Complex.new(a.real - b.imag, a.imag + b.real)`, so every argument had to
# answer #real and #imag (which is why the specs' mock numerics all died on an
# undefined #-); Complex.rect and Complex.rectangular did not exist; #<=>
# compared magnitudes rather than answering nil; #to_f/#to_i/#to_r,
# #rationalize, #fdiv, #finite? and #infinite? were missing; and Numeric's
# comparison operators were inherited rather than undefined.
#
# Everything below follows MRI's complex.c.
class Complex
  # nucomp_real_check: Integer, Float and Rational pass; a Complex passes when
  # its imaginary part is zero; any other Numeric passes when #real? is true.
  def self.__real_check__(n)
    return if n.is_a?(::Integer) || n.is_a?(::Float) || n.is_a?(::Rational)
    if n.is_a?(::Complex)
      return if n.imag == 0
    elsif n.is_a?(::Numeric) && n.real?
      return
    end
    ::Kernel.raise(::TypeError, "not a real")
  end

  # nucomp_s_new_internal: no canonicalisation, no checks.
  def self.__raw__(real, imag)
    c = allocate
    c.__send__(:__set_parts__, real, imag)
    c
  end

  def __set_parts__(real, imag)
    @real = real
    @image = imag
  end
  private :__set_parts__

  # nucomp_s_canonicalize_internal: a Complex in either position folds in.
  def self.__canon__(real, imag)
    cr = real.is_a?(::Complex)
    ci = imag.is_a?(::Complex)
    if !cr && !ci
      __raw__(real, imag)
    elsif !cr
      __raw__(real - imag.imag, 0 + imag.real)
    elsif !ci
      __raw__(real.real, real.imag + imag)
    else
      __raw__(real.real - imag.imag, real.imag + imag.real)
    end
  end

  def self.rectangular(real, imag = 0)
    __real_check__(real)
    __real_check__(imag)
    __canon__(real, imag)
  end
  class << self
    alias_method :rect, :rectangular
  end

  def self.polar(abs, arg = 0)
    __real_check__(abs)
    __real_check__(arg)
    return __raw__(abs, 0.0) if abs == 0 || arg == 0
    __canon__(abs * ::Math.cos(arg), abs * ::Math.sin(arg))
  end

  I = __raw__(0, 1) unless const_defined?(:I, false) && I == __raw__(0, 1)

  # k_exact_zero_p: an exact zero, so 0.0 does not count.
  def __exact_zero_imag__
    i = imag
    !i.is_a?(::Float) && i == 0
  end
  private :__exact_zero_imag__

  # rb_num_coerce_bin, with Complex's wording.
  def __coerce_bin__(other, op)
    unless other.respond_to?(:coerce)
      ::Kernel.raise(::TypeError,
        "#{other.nil? ? 'nil' : other.class} can't be coerced into #{self.class}")
    end
    a, b = other.coerce(self)
    a.__send__(op, b)
  end
  private :__coerce_bin__

  def __real_operand__(other)
    other.is_a?(::Numeric) && other.real?
  end
  private :__real_operand__

  def +(other)
    if other.is_a?(::Complex)
      ::Complex.__raw__(real + other.real, imag + other.imag)
    elsif __real_operand__(other)
      ::Complex.__raw__(real + other, imag)
    else
      __coerce_bin__(other, :+)
    end
  end

  def -(other)
    if other.is_a?(::Complex)
      ::Complex.__raw__(real - other.real, imag - other.imag)
    elsif __real_operand__(other)
      ::Complex.__raw__(real - other, imag)
    else
      __coerce_bin__(other, :-)
    end
  end

  def *(other)
    if other.is_a?(::Complex)
      ::Complex.__raw__(real * other.real - imag * other.imag,
                        real * other.imag + imag * other.real)
    elsif __real_operand__(other)
      ::Complex.__raw__(real * other, imag * other)
    else
      __coerce_bin__(other, :*)
    end
  end

  def /(other)
    if other.is_a?(::Complex)
      d = other.abs2
      ::Complex.__raw__((real * other.real + imag * other.imag).quo(d),
                        (imag * other.real - real * other.imag).quo(d))
    elsif __real_operand__(other)
      ::Complex.__raw__(real.quo(other), imag.quo(other))
    else
      __coerce_bin__(other, :/)
    end
  end
  alias_method :quo, :/

  def fdiv(other)
    if other.is_a?(::Complex)
      d = other.abs2.to_f
      ::Complex.__raw__((real * other.real + imag * other.imag).fdiv(d),
                        (imag * other.real - real * other.imag).fdiv(d))
    elsif __real_operand__(other)
      ::Complex.__raw__(real.fdiv(other), imag.fdiv(other))
    else
      __coerce_bin__(other, :fdiv)
    end
  end

  def **(other)
    return ::Complex.__raw__(1, 0) if other.is_a?(::Numeric) && !other.is_a?(::Float) && other == 0
    other = other.numerator if other.is_a?(::Rational) && other.denominator == 1
    if other.is_a?(::Complex)
      if other.imag == 0 && !other.imag.is_a?(::Float)
        other = other.real
      else
        r, theta = polar
        return ::Complex.polar(r ** other, theta * other) rescue nil
      end
    end
    if other.is_a?(::Integer)
      if other > 0
        # Repeated squaring, so that an exact Complex stays exact.
        x = self
        z = x
        n = other - 1
        while n != 0
          while true
            q, rem = n.divmod(2)
            break if rem != 0
            x = ::Complex.__raw__(x.real * x.real - x.imag * x.imag,
                                  2 * x.real * x.imag)
            n = q
          end
          z = z * x
          n -= 1
        end
        return z
      end
      return (::Complex.__raw__(1, 0) / self) ** (-other)
    end
    if __real_operand__(other)
      r, theta = polar
      return ::Complex.polar(r ** other, theta * other)
    end
    __coerce_bin__(other, :**)
  end

  def -@
    ::Complex.__raw__(-real, -imag)
  end

  def +@
    self
  end

  def abs
    r = real
    i = imag
    if r.is_a?(::Float) || i.is_a?(::Float)
      ::Math.hypot(r, i)
    elsif r == 0
      i.abs
    elsif i == 0
      r.abs
    else
      ::Math.hypot(r, i)
    end
  end
  alias_method :magnitude, :abs

  def abs2
    real * real + imag * imag
  end

  def arg
    ::Math.atan2(imag, real)
  end
  alias_method :angle, :arg
  alias_method :phase, :arg

  def polar
    [abs, arg]
  end

  def conjugate
    ::Complex.__raw__(real, -imag)
  end
  alias_method :conj, :conjugate

  def ==(other)
    if other.is_a?(::Complex)
      real == other.real && imag == other.imag
    elsif __real_operand__(other)
      real == other && imag == 0
    else
      other == self
    end
  end

  def eql?(other)
    return false unless other.is_a?(::Complex)
    real.class == other.real.class && imag.class == other.imag.class && self == other
  end

  def <=>(other)
    return nil unless imag == 0
    if other.is_a?(::Complex)
      return other.imag == 0 ? (real <=> other.real) : nil
    end
    return real <=> other if __real_operand__(other)
    return nil unless other.is_a?(::Numeric)
    nil
  end

  def coerce(other)
    return [::Complex.__raw__(other, 0), self] if __real_operand__(other)
    return [other, self] if other.is_a?(::Complex)
    ::Kernel.raise(::TypeError, "#{other.class} can't be coerced into #{self.class}")
  end

  def denominator
    real.denominator.lcm(imag.denominator)
  end

  def numerator
    cd = denominator
    ::Complex.__raw__(real.numerator * (cd / real.denominator),
                      imag.numerator * (cd / imag.denominator))
  end

  def to_c
    self
  end

  def to_f
    unless __exact_zero_imag__
      ::Kernel.raise(::RangeError, "can't convert #{self} into Float")
    end
    real.to_f
  end

  def to_i
    unless __exact_zero_imag__
      ::Kernel.raise(::RangeError, "can't convert #{self} into Integer")
    end
    real.to_i
  end

  def to_r
    unless __exact_zero_imag__ || imag == 0
      ::Kernel.raise(::RangeError, "can't convert #{self} into Rational")
    end
    real.to_r
  end

  def rationalize(*args)
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    unless __exact_zero_imag__
      ::Kernel.raise(::RangeError, "can't convert #{self} into Rational")
    end
    real.rationalize(*args)
  end

  def hash
    [real, imag].hash
  end

  def zero?
    real == 0 && imag == 0
  end

  def nonzero?
    zero? ? nil : self
  end

  # MRI removes the real-number protocol from Complex rather than inheriting
  # Numeric's, so that `Complex(1) < 2` is a NoMethodError and not an attempt
  # to compare magnitudes.
  %i[< <= > >= between? clamp positive? negative? % modulo div divmod
     remainder floor ceil round truncate step i integer? divmod].each do |m|
    undef_method(m) if method_defined?(m) || private_method_defined?(m)
  end

  def integer?
    false
  end
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
    each_with_index do |pair, index|
      pair = yield(pair) if block_given?
      array = pair.respond_to?(:to_ary) ? pair.to_ary : nil
      unless array.is_a?(Array)
        raise TypeError, "wrong element type #{pair.class} at #{index} (expected array)"
      end
      unless array.size == 2
        raise ArgumentError, "wrong array length at #{index} (expected 2, was #{array.size})"
      end
      result[array[0]] = array[1]
    end
    result
  end

  # #uniq and #uniq! compare the block's result, not the element itself; the
  # core versions ignore the block entirely, so they are kept for the no-block
  # case and wrapped here.
  alias_method :__uniq_without_block__, :uniq
  alias_method :__uniq_in_place_without_block__, :uniq!

  def uniq(&block)
    return __uniq_without_block__ unless block
    seen = {}
    result = []
    each do |element|
      key = block.call(element)
      next if seen.key?(key)
      seen[key] = true
      result << element
    end
    result
  end

  def uniq!(&block)
    return __uniq_in_place_without_block__ unless block
    result = uniq(&block)
    return nil if result.size == size
    replace(result)
  end

  private :__uniq_without_block__, :__uniq_in_place_without_block__

  # #zip takes anything that responds to #each, not just arrays: an Enumerator
  # (including an infinite one, which is why this reads lazily and stops at the
  # receiver's size) or any object with #each.
  alias_method :__zip_arrays__, :zip

  def zip(*others, &block)
    length = size
    converted = others.map do |other|
      if other.is_a?(Array)
        other
      elsif other.respond_to?(:to_ary)
        other.to_ary
      elsif other.respond_to?(:each)
        collected = []
        other.each do |element|
          break if collected.size >= length
          collected << element
        end
        collected
      else
        raise TypeError, "wrong argument type #{other.class} (must respond to :each)"
      end
    end
    __zip_arrays__(*converted, &block)
  end

  private :__zip_arrays__
end

class String
  # A copy of the receiver as a plain String. Since Ruby 3.0 a method that derives a new
  # string answers with String even when the receiver is a subclass of it; only #dup, #clone
  # and #+@ carry the class over, and String.new here takes no encoding: keyword.
  def __ir_plain_copy__
    out = +""
    out.force_encoding(encoding) if out.respond_to?(:force_encoding)
    out << self
    out
  end
  private :__ir_plain_copy__

  def b
    __ir_plain_copy__.force_encoding(Encoding::BINARY)
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
  # Every Enumerable gets #to_set, not just Array: Hash, Struct, Range and
  # Enumerator are all asked for one by the specs.
  def to_set(*args, &block)
    require 'set'
    ::Set.new(self, *args, &block)
  end unless method_defined?(:to_set)

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

class Enumerator
  # 2.6's arithmetic sequence: what Range#step and Range#% answer, and what
  # Numeric#step answers. It is an Enumerator that also remembers the three
  # numbers it was built from.
  #
  # Iteration and #size follow MRI's numeric.c: ruby_float_step,
  # ruby_float_step_size and ruby_num_interval_step_size. Reimplementing them
  # rather than "value += step until past the end" matters because MRI
  # multiplies rather than accumulates for floats (so 1.0.step(12.7, 1.3) ends
  # at exactly 12.7), and because the infinity and NaN corners have answers a
  # naive loop does not reach.
  class ArithmeticSequence < ::Enumerator
    attr_reader :begin, :end, :step

    # floor that leaves NaN and Infinity alone, the way C's floor() does.
    # Float#floor raises FloatDomainError for them.
    def self.__safe_floor__(x)
      return x if x.is_a?(::Float) && (x.nan? || x.infinite?)
      x.floor
    end

    # MRI ruby_float_step_size: the number of values yielded, as a Float so
    # that Infinity and NaN survive.
    def self.__float_step_size__(beg, fin, unit, excl)
      n = (fin - beg) / unit
      err = (beg.abs + fin.abs + (fin - beg).abs) / unit.abs * ::Float::EPSILON
      if unit.infinite?
        return (unit > 0 ? beg <= fin : beg >= fin) ? 1.0 : 0.0
      end
      return ::Float::INFINITY if unit == 0
      err = 0.5 if err > 0.5
      if excl
        return 0.0 if n <= 0
        n = n < 1 ? 0.0 : __safe_floor__(n - err).to_f
        d = (n + 1) * unit + beg
        if beg < fin
          n += 1 if d < fin
        elsif beg > fin
          n += 1 if d > fin
        end
      else
        return 0.0 if n < 0
        n = __safe_floor__(n + err).to_f
        d = (n + 1) * unit + beg
        if beg < fin
          n += 1 if d <= fin
        elsif beg > fin
          n += 1 if d >= fin
        end
      end
      n + 1
    end

    # MRI ruby_num_interval_step_size.
    def self.__interval_step_size__(from, to, unit, excl)
      if from.is_a?(::Integer) && to.is_a?(::Integer) && unit.is_a?(::Integer)
        return ::Float::INFINITY if unit == 0
        delta = to - from
        diff = unit
        if diff < 0
          diff = -diff
          delta = -delta
        end
        delta -= 1 if excl
        return 0 if delta < 0
        delta / diff + 1
      elsif from.is_a?(::Float) || to.is_a?(::Float) || unit.is_a?(::Float)
        n = __float_step_size__(from.to_f, to.to_f, unit.to_f, excl)
        return n if n.infinite?
        # MRI converts the double back to an Integer here; NaN raises
        # FloatDomainError out of rb_dbl2big, and so does Float#floor.
        n.floor
      else
        neg = unit < 0
        return ::Float::INFINITY if !neg && !(unit > 0)
        cmp = neg ? :< : :>
        return 0 if from.__send__(cmp, to)
        n = (to - from).div(unit)
        n += 1 unless excl && from + n * unit == to
        n < 0 ? 0 : n
      end
    end

    # MRI ruby_float_step / num_step / range_step iteration, shared.
    def self.__step_each__(from, to, unit, excl, &block)
      desc = unit < 0
      if to.nil?
        inf = true
      elsif to.is_a?(::Float) && to.infinite?
        inf = (to < 0) ? desc : !desc
      else
        inf = false
      end
      inf = true if unit == 0

      if from.is_a?(::Integer) && unit.is_a?(::Integer) && (inf || to.is_a?(::Integer))
        i = from
        if inf
          loop { block.call(i); i += unit }
        elsif desc
          while i >= to
            block.call(i)
            i += unit
          end
        else
          while i <= to
            block.call(i)
            i += unit
          end
        end
        return
      end

      fin = to
      fin = (desc ? -::Float::INFINITY : ::Float::INFINITY) if fin.nil?

      if from.is_a?(::Float) || fin.is_a?(::Float) || unit.is_a?(::Float)
        beg = from.to_f
        stop = fin.to_f
        step = unit.to_f
        n = __float_step_size__(beg, stop, step, excl)
        if step.infinite?
          block.call(beg) if n > 0
        elsif step == 0
          loop { block.call(beg) }
        else
          i = 0
          # n may be NaN (Infinity.step(Infinity, 1)); 0 < NaN is false, so
          # nothing is yielded, which is what MRI's for(;i<n;) loop does.
          while i < n
            d = i * step + beg
            d = stop if step >= 0 ? stop < d : d < stop
            block.call(d)
            i += 1
          end
        end
        return
      end

      i = from
      cmp = desc ? :< : :>
      loop do
        break if excl ? (i == fin || i.__send__(cmp, fin)) : i.__send__(cmp, fin)
        block.call(i)
        i += unit
      end
    end

    def self.__build__(from, to, by, exclude_end, source, inspect_str = nil)
      seq = allocate
      seq.__send__(:__arith_init__, from, to, by, exclude_end, source, inspect_str)
      seq
    end

    def __arith_init__(from, to, by, exclude_end, source, inspect_str = nil)
      @begin = from
      @end = to
      @step = by
      @exclude_end = exclude_end
      @source = source
      @inspect_str = inspect_str
      initialize(nil) do |y|
        __arith_each__ { |v| y << v }
      end
    end
    private :__arith_init__

    def exclude_end?
      @exclude_end
    end

    def first(n = nil)
      return __arith_each__ { |v| return v } if n.nil?
      return [] if n <= 0
      result = []
      __arith_each__ do |v|
        result << v
        break if result.size >= n
      end
      result
    end

    def each(&block)
      return self unless block
      __arith_each__(&block)
      self
    end

    def __arith_each__(&block)
      ArithmeticSequence.__step_each__(@begin, @end, @step, @exclude_end, &block)
      self
    end
    private :__arith_each__

    def to_a
      result = []
      __arith_each__ { |v| result << v }
      result
    end
    alias_method :entries, :to_a
    alias_method :force, :to_a

    def size
      return ::Float::INFINITY if @end.nil? && @step != 0
      ArithmeticSequence.__interval_step_size__(@begin, @end, @step, @exclude_end)
    end

    def last(n = nil)
      values = to_a
      n.nil? ? values.last : values.last(n)
    end

    def ==(other)
      other.is_a?(ArithmeticSequence) &&
        self.begin == other.begin && self.end == other.end &&
        step == other.step && exclude_end? == other.exclude_end?
    end
    alias_method :eql?, :==

    def hash
      [self.begin, self.end, step, exclude_end?].hash
    end

    def inspect
      return @inspect_str if @inspect_str
      "((#{@source.inspect}).%(#{@step.inspect}))"
    end
    alias_method :to_s, :inspect
  end
end

# 3.2's Data: immutable value objects. The constant did not exist, so every
# file in spec/core/data failed to load.
class Data
  class << self
    def define(*members, &block)
      members = members.map do |m|
        unless m.is_a?(::Symbol) || m.is_a?(::String)
          ::Kernel.raise(::TypeError, "#{m.inspect} is not a symbol nor a string")
        end
        m.to_sym
      end
      duplicate = members.group_by { |m| m }.select { |_, v| v.size > 1 }.keys.first
      ::Kernel.raise(::ArgumentError, "duplicate member: #{duplicate}") if duplicate

      klass = ::Class.new(self) do
        @__members__ = members

        members.each do |name|
          define_method(name) { instance_variable_get("@#{name}") }
        end

        class << self
          def members
            @__members__.dup
          end

          def [](*args, **kwargs)
            new(*args, **kwargs)
          end

          def new(*args, **kwargs)
            instance = allocate
            instance.__send__(:__data_init__, @__members__, args, kwargs)
            instance
          end
        end
      end
      klass.class_eval(&block) if block
      klass
    end

    def members
      @__members__ ? @__members__.dup : []
    end
  end

  def __data_init__(names, args, kwargs)
    if !args.empty? && !kwargs.empty?
      ::Kernel.raise(::ArgumentError, "wrong number of arguments")
    end
    if args.empty? && kwargs.empty? && !names.empty?
      ::Kernel.raise(::ArgumentError, "missing keyword#{names.size > 1 ? 's' : ''}: #{names.map(&:inspect).join(', ')}")
    end
    if !args.empty?
      if args.size > names.size
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..#{names.size})")
      end
      if args.size < names.size
        missing = names[args.size..-1]
        ::Kernel.raise(::ArgumentError, "missing keyword#{missing.size > 1 ? 's' : ''}: #{missing.map(&:inspect).join(', ')}")
      end
      names.each_with_index { |n, i| instance_variable_set("@#{n}", args[i]) }
    else
      missing = names - kwargs.keys
      unless missing.empty?
        ::Kernel.raise(::ArgumentError, "missing keyword#{missing.size > 1 ? 's' : ''}: #{missing.map(&:inspect).join(', ')}")
      end
      unknown = kwargs.keys - names
      unless unknown.empty?
        ::Kernel.raise(::ArgumentError, "unknown keyword#{unknown.size > 1 ? 's' : ''}: #{unknown.map(&:inspect).join(', ')}")
      end
      names.each { |n| instance_variable_set("@#{n}", kwargs[n]) }
    end
    freeze
  end
  private :__data_init__

  def members
    self.class.members
  end

  def to_h(&block)
    result = {}
    members.each { |n| result[n] = __send__(n) }
    return result unless block
    out = {}
    result.each { |k, v| pair = block.call(k, v); out[pair[0]] = pair[1] }
    out
  end

  def deconstruct
    members.map { |n| __send__(n) }
  end

  def deconstruct_keys(keys)
    return to_h if keys.nil?
    all = members
    result = {}
    keys.each do |k|
      return result unless all.include?(k)
      result[k] = __send__(k)
    end
    result
  end

  def with(**kwargs)
    return self if kwargs.empty?
    unknown = kwargs.keys - members
    unless unknown.empty?
      ::Kernel.raise(::ArgumentError, "unknown keyword#{unknown.size > 1 ? 's' : ''}: #{unknown.map(&:inspect).join(', ')}")
    end
    self.class.new(**to_h.merge(kwargs))
  end

  def ==(other)
    other.class == self.class && other.deconstruct == deconstruct
  end

  def eql?(other)
    other.class == self.class && members.all? { |n| __send__(n).eql?(other.__send__(n)) }
  end

  def hash
    ([self.class] + deconstruct).hash
  end

  def inspect
    name = self.class.name
    body = members.map { |n| "#{n}=#{__send__(n).inspect}" }.join(", ")
    "#<data #{name ? "#{name} " : ''}#{body}>"
  end
  alias_method :to_s, :inspect
end unless defined?(Data)

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

# --- Math ------------------------------------------------------------------
# Math had two independent problems in the same place.
#
# The C# builtins signalled every out-of-domain result as Errno::EDOM (MRI
# raises Math::DomainError), did so for NaN input as well (MRI answers NaN),
# converted arguments with the DefaultProtocol instead of Float()'s rules (MRI
# raises TypeError for anything that is not a Numeric, including a String that
# looks like a number), had no second argument to #log, no pair from #lgamma
# and a #cbrt that was pow(x, 1/3.0) and so NaN for every negative argument.
#
# On top of that, the Complex implementation IronRuby loads is Ruby 1.8's
# complex.rb, which replaces sqrt/exp/log/cos/sin/tan/... with CMath versions
# that answer a Complex. This block runs after complex.rb and takes the module
# back; Complex arithmetic keeps working because complex.rb calls the `!`
# aliases it took before redefining.
#
# Everything is module_function, as in MRI: `include Math; atanh(2)` has to
# raise the same Math::DomainError as `Math.atanh(2)`.
module Math
  class DomainError < StandardError; end unless const_defined?(:DomainError, false)

  alias_method :__ir_gamma__, :gamma
  alias_method :__ir_lgamma__, :lgamma
  alias_method :__ir_erf__, :erf
  alias_method :__ir_erfc__, :erfc
  module_function :__ir_gamma__, :__ir_lgamma__, :__ir_erf__, :__ir_erfc__

  # MRI's rb_to_float: Numeric only. An object that merely answers #to_f is a
  # TypeError, and so is a String - Float("1") would work but Math.sqrt("1")
  # does not.
  def __flt__(x)
    return x if x.is_a?(::Float)
    if x.is_a?(::Numeric)
      v = x.to_f
      return v if v.is_a?(::Float)
    end
    ::Kernel.raise(::TypeError, "can't convert #{x.nil? ? 'nil' : x.class} into Float")
  end

  def __dom__(name)
    ::Kernel.raise(::Math::DomainError, "Numerical argument is out of domain - #{name}")
  end

  module_function :__flt__, :__dom__
  private_class_method :__flt__, :__dom__

  def acos(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("acos") if x < -1.0 || x > 1.0
    ::System::Math.Acos(x)
  end

  def asin(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("asin") if x < -1.0 || x > 1.0
    ::System::Math.Asin(x)
  end

  def atan(x)
    ::System::Math.Atan(__flt__(x))
  end

  def atan2(y, x)
    y = __flt__(y)
    x = __flt__(x)
    return y if y.nan?
    return x if x.nan?
    ::System::Math.Atan2(y, x)
  end

  def acosh(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("acosh") if x < 1.0
    ::System::Math.Acosh(x)
  end

  def asinh(x)
    ::System::Math.Asinh(__flt__(x))
  end

  def atanh(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("atanh") if x < -1.0 || x > 1.0
    return ::Float::INFINITY if x == 1.0
    return -::Float::INFINITY if x == -1.0
    ::System::Math.Atanh(x)
  end

  def cos(x)
    ::System::Math.Cos(__flt__(x))
  end

  def sin(x)
    ::System::Math.Sin(__flt__(x))
  end

  def tan(x)
    ::System::Math.Tan(__flt__(x))
  end

  def cosh(x)
    ::System::Math.Cosh(__flt__(x))
  end

  def sinh(x)
    ::System::Math.Sinh(__flt__(x))
  end

  def tanh(x)
    ::System::Math.Tanh(__flt__(x))
  end

  def exp(x)
    ::System::Math.Exp(__flt__(x))
  end

  # pow(x, 1/3.0) is NaN for every negative x; cbrt is defined there.
  def cbrt(x)
    ::System::Math.Cbrt(__flt__(x))
  end

  def sqrt(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("sqrt") if x < 0.0
    # sqrt(-0.0) is -0.0 in IEEE but 0.0 in MRI.
    return 0.0 if x == 0.0
    ::System::Math.Sqrt(x)
  end

  def __log__(x, name)
    return x if x.nan?
    __dom__(name) if x < 0.0
    nil
  end
  module_function :__log__
  private_class_method :__log__

  # MRI's math_log_split. Math.log2(2**10001) has to be 10001.0, but the
  # argument does not survive the trip through a double: shift the exponent
  # out first and add it back afterwards.
  def __log_split__(x)
    if x.is_a?(::Integer) && x > 0
      bits = x.bit_length
      if bits > 1024
        n = bits - 53
        return [(x >> n).to_f, n]
      end
    end
    [__flt__(x), 0]
  end
  module_function :__log_split__
  private_class_method :__log_split__

  # Math.log(x) and Math.log(x, nil) are different calls - the second is a
  # TypeError - so the base cannot be an optional parameter defaulting to nil.
  def log(x, *rest)
    if rest.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{rest.size + 1}, expected 1..2)")
    end
    x, bits = __log_split__(x)
    r = __log__(x, "log")
    return r if r
    v = ::System::Math.Log(x) + bits * ::System::Math.Log(2.0)
    return v if rest.empty?
    b, bbits = __log_split__(rest[0])
    r = __log__(b, "log")
    return r if r
    v / (::System::Math.Log(b) + bbits * ::System::Math.Log(2.0))
  end

  def log2(x)
    x, bits = __log_split__(x)
    r = __log__(x, "log2")
    return r if r
    ::System::Math.Log2(x) + bits
  end

  def log10(x)
    x, bits = __log_split__(x)
    r = __log__(x, "log10")
    return r if r
    ::System::Math.Log10(x) + bits * ::System::Math.Log10(2.0)
  end

  # exp(x)-1 and log(1+x), accurate near zero, which is the whole point of
  # having them separately from exp and log.
  def expm1(x)
    x = __flt__(x)
    # exp(1e-16) - 1.0 is exactly 0; the identity (u-1)*x/log(u) keeps the
    # significant digits that the subtraction cancels away.
    u = ::System::Math.Exp(x)
    return x if u == 1.0
    return u - 1.0 if u - 1.0 == -1.0 || u.infinite? || u.nan?
    (u - 1.0) * x / ::System::Math.Log(u)
  end

  def log1p(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("log1p") if x < -1.0
    return -::Float::INFINITY if x == -1.0
    # log(1+x) loses every significant digit for tiny x; the identity
    # x*log1p(u)/u with u = (1+x)-1 keeps them.
    u = 1.0 + x
    return x if u == 1.0
    ::System::Math.Log(u) * x / (u - 1.0)
  end

  def erf(x)
    __ir_erf__(__flt__(x))
  end

  def erfc(x)
    __ir_erfc__(__flt__(x))
  end

  def hypot(x, y)
    x = __flt__(x).abs
    y = __flt__(y).abs
    # An infinity wins over a NaN, which is what C's hypot() promises.
    return ::Float::INFINITY if x.infinite? || y.infinite?
    return ::Float::NAN if x.nan? || y.nan?
    x, y = y, x if x < y
    return x if y == 0.0
    # Scaling by the larger operand keeps x*x from overflowing for x ~ 1e300.
    r = y / x
    x * ::System::Math.Sqrt(1.0 + r * r)
  end

  # MRI's NUM2INT: #to_int, never String parsing, and a Float that is not
  # finite is a RangeError rather than a FloatDomainError.
  def __int__(n)
    return n if n.is_a?(::Integer)
    if n.is_a?(::Float)
      if n.nan?
        ::Kernel.raise(::RangeError, "float NaN out of range of integer")
      elsif n.infinite?
        ::Kernel.raise(::RangeError, "float #{n > 0 ? 'Inf' : '-Inf'} out of range of integer")
      end
      return n.to_i
    end
    ::Kernel.raise(::TypeError, "no implicit conversion from nil to integer") if n.nil?
    unless n.respond_to?(:to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{n.class} into Integer")
    end
    n.to_int
  end
  module_function :__int__
  private_class_method :__int__

  def ldexp(x, n)
    n = __int__(n)
    x = __flt__(x)
    return x if x == 0.0 || x.nan? || x.infinite?
    return 0.0 * x if n < -2098
    return (x < 0 ? -::Float::INFINITY : ::Float::INFINITY) if n > 2098
    ::System::Math.ScaleB(x, n)
  end

  # MRI answers +infinity at the poles rather than raising, and raises only for
  # -infinity. MathUtils.Gamma already handles the negative non-integers.
  def gamma(x)
    x = __flt__(x)
    return x if x.nan?
    if x == 0.0
      return (1.0 / x) < 0 ? -::Float::INFINITY : ::Float::INFINITY
    end
    if x < 0.0
      __dom__("gamma") if x.infinite? || x == x.floor
    end
    __ir_gamma__(x)
  end

  # lgamma answers the pair [log(|gamma(x)|), sign of gamma(x)].
  def lgamma(x)
    x = __flt__(x)
    return [x, 1] if x.nan?
    __dom__("lgamma") if x.infinite? && x < 0
    return [::Float::INFINITY, 1] if x.infinite?
    if x == 0.0
      return [::Float::INFINITY, (1.0 / x) < 0 ? -1 : 1]
    end
    if x < 0.0
      return [::Float::INFINITY, 1] if x == x.floor
      # Reflection: |gamma(x)| = pi / (|sin(pi*x)| * gamma(1-x)), and the sign
      # of gamma(x) is the sign of sin(pi*x) because gamma(1-x) > 0 there.
      s = ::System::Math.Sin(::Math::PI * x)
      v = ::System::Math.Log(::Math::PI) - ::System::Math.Log(s.abs) - __ir_lgamma__(1.0 - x)
      return [v, s < 0 ? -1 : 1]
    end
    # MathUtils.LogGamma(1.0) is 4.4e-16 rather than 0.
    return [0.0, 1] if x == 1.0 || x == 2.0
    [__ir_lgamma__(x), 1]
  end

  def frexp(x)
    x = __flt__(x)
    return [x, 0] if x == 0.0 || x.nan? || x.infinite?
    e = ::System::Math.ILogB(x) + 1
    [::System::Math.ScaleB(x, -e), e]
  end

  module_function :frexp
  module_function :acos, :acosh, :asin, :asinh, :atan, :atan2, :atanh, :cbrt,
                  :cos, :cosh, :erf, :erfc, :exp, :expm1, :gamma, :hypot,
                  :ldexp, :lgamma, :log, :log10, :log1p, :log2, :sin, :sinh,
                  :sqrt, :tan, :tanh
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

  # The rest of the clocks this platform names. They are symbols like the three above
  # because clock_gettime below dispatches on identity, not on a number.
  %i[
    CLOCK_MONOTONIC_RAW CLOCK_MONOTONIC_COARSE CLOCK_REALTIME_COARSE
    CLOCK_THREAD_CPUTIME_ID CLOCK_BOOTTIME
  ].each { |c| const_set(c, c) unless const_defined?(c) }

  # Linux wait flags, priority classes and resource limits. ruby/spec only asks that
  # they exist and are Integers, but the numbers are the real ones so that anything
  # passing them to a syscall later gets the right value.
  {
    WNOHANG: 1, WUNTRACED: 2,
    PRIO_PROCESS: 0, PRIO_PGRP: 1, PRIO_USER: 2,
    RLIMIT_CPU: 0, RLIMIT_FSIZE: 1, RLIMIT_DATA: 2, RLIMIT_STACK: 3,
    RLIMIT_CORE: 4, RLIMIT_RSS: 5, RLIMIT_NPROC: 6, RLIMIT_NOFILE: 7,
    RLIMIT_MEMLOCK: 8, RLIMIT_AS: 9, RLIMIT_LOCKS: 10, RLIMIT_SIGPENDING: 11,
    RLIMIT_MSGQUEUE: 12, RLIMIT_NICE: 13, RLIMIT_RTPRIO: 14, RLIMIT_RTTIME: 15,
    RLIM_INFINITY: 2**64 - 1, RLIM_SAVED_CUR: 2**64 - 1, RLIM_SAVED_MAX: 2**64 - 1,
  }.each { |name, value| const_set(name, value) unless const_defined?(name) }

  # Process.times returns this; MRI names the struct under Process as well as Struct.
  Tms = Struct::Tms unless const_defined?(:Tms)

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

  # NB SignalException#signo / #signm are defined just below this module.
  #
  # Process.exit / .exit! / .abort are module functions in MRI and behave exactly
  # like their Kernel namesakes.  Without them Process.exit raised NoMethodError,
  # and in spec/core/process/exit_spec.rb that NoMethodError escaped a thread whose
  # main thread was waiting in a bare sleep for the SystemExit - so the file hung.
  unless respond_to?(:exit)
    def exit(*args)
      ::Kernel.exit(*args)
    end
    module_function :exit

    def exit!(*args)
      ::Kernel.exit!(*args)
    end
    module_function :exit!

    def abort(*args)
      ::Kernel.abort(*args)
    end
    module_function :abort
  end

  unless respond_to?(:clock_getres)
    # We have no way to ask the platform, so report the resolution the source we
    # actually use has: Stopwatch for the monotonic clocks, and Time for the rest.
    def clock_getres(clock_id = CLOCK_MONOTONIC, unit = :float_second)
      seconds = 1.0 / System::Diagnostics::Stopwatch.frequency.to_f
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
    module_function :clock_getres
  end

  unless respond_to?(:argv0)
    # MRI hands back the $0 the program started with, and keeps handing it back
    # after $0 is assigned to. setproctitle is what assignment goes through.
    ORIGINAL_ARGV0 = ($0 && $0.dup.freeze) unless const_defined?(:ORIGINAL_ARGV0)

    def argv0
      ORIGINAL_ARGV0
    end
    module_function :argv0

    def setproctitle(title)
      # There is no portable way to rewrite the process title from .NET; MRI returns
      # the string it was given either way.
      title.to_s
    end
    module_function :setproctitle
  end

  unless respond_to?(:maxgroups)
    def maxgroups
      @maxgroups ||= 65536
    end
    module_function :maxgroups

    def maxgroups=(value)
      @maxgroups = value.to_int
    end
    module_function :maxgroups=
  end

  unless respond_to?(:detach)
    # A thread that reaps the child and whose #value is the exit status, plus the
    # #pid reader MRI puts on it.
    def detach(pid)
      pid = pid.to_int
      thread = Thread.new(pid) { |p| Process.wait2(p)[1] }
      thread.define_singleton_method(:pid) { pid }
      thread
    end
    module_function :detach
  end

  unless respond_to?(:warmup)
    def warmup
      true
    end
    module_function :warmup
  end

  # --- spawn and the wait family ------------------------------------------------
  #
  # Process.__spawn__ starts "/bin/sh -c <script>" and hands back a pid without
  # waiting; everything else - picking the command apart, converting and checking
  # the arguments, and folding :chdir/:umask/redirections into the script - is here,
  # because it is all protocol work that reads far better in Ruby.

  SPAWN_OPTION_KEYS = [
    :unsetenv_others, :close_others, :pgroup, :new_pgroup, :chdir, :umask,
    :in, :out, :err, :rlimit_core, :rlimit_cpu, :rlimit_fsize, :exception,
  ].freeze

  def self.__check_spawn_string__(value, what)
    unless value.is_a?(String)
      unless value.respond_to?(:to_str)
        raise TypeError, "no implicit conversion of #{value.class} into String"
      end
      value = value.to_str
    end
    raise ArgumentError, "#{what} contains null byte" if value.include?("\0")
    value
  end

  # Single-quote for /bin/sh: everything inside '' is literal, and a quote itself
  # has to leave and re-enter the quoting.
  def self.__shell_quote__(s)
    "'" + s.to_s.gsub("'", "'\\\\''") + "'"
  end

  def self.__spawn_redirect__(key, value)
    fd = case key
         when :in then 0
         when :out then 1
         when :err then 2
         when Integer then key
         when IO then key.fileno
         when Array then nil
         else return nil
         end
    return nil if fd.nil?

    case value
    when :close       then "#{fd}>&-"
    when Integer      then "#{fd}>&#{value}"
    when IO           then "#{fd}>&#{value.fileno}"
    when Array
      if value[0] == :child
        "#{fd}>&#{value[1] == :out ? 1 : (value[1] == :err ? 2 : value[1])}"
      else
        name = __check_spawn_string__(value[0], "path name")
        mode = value[1].to_s
        op = mode.include?("a") ? ">>" : (mode.start_with?("r") ? "<" : ">")
        "#{fd}#{op}#{__shell_quote__(name)}"
      end
    when String, Symbol
      name = __check_spawn_string__(value.to_s, "path name")
      fd == 0 ? "#{fd}<#{__shell_quote__(name)}" : "#{fd}>#{__shell_quote__(name)}"
    else
      nil
    end
  end

  def self.__resolve_executable__(name)
    if name.include?(File::SEPARATOR) || (File::ALT_SEPARATOR && name.include?(File::ALT_SEPARATOR))
      candidates = [name]
    else
      path = ENV["PATH"].to_s
      candidates = path.split(File::PATH_SEPARATOR).map { |dir| File.join(dir.empty? ? "." : dir, name) }
    end

    candidates.each do |candidate|
      next unless File.exist?(candidate)
      raise Errno::EACCES, candidate if File.directory?(candidate)
      raise Errno::EACCES, candidate unless File.executable?(candidate)
      return candidate
    end
    raise Errno::ENOENT, name
  end

  def self.__parse_spawn_args__(args)
    args = args.dup
    env = nil
    options = {}

    if !args.empty? && args.first.respond_to?(:to_hash) && !args.first.is_a?(String)
      env = args.shift.to_hash
    end
    if args.size > 1 && args.last.respond_to?(:to_hash) && !args.last.is_a?(String)
      options = args.pop.to_hash
    end
    raise ArgumentError, "wrong number of arguments (given 0, expected 1+)" if args.empty?

    if env
      env = env.each_with_object({}) do |(k, v), h|
        key = __check_spawn_string__(k, "environment name")
        raise ArgumentError, "environment name contains a equal : #{key}" if key.include?("=")
        h[key] = v.nil? ? nil : __check_spawn_string__(v, "environment value")
      end
    end

    options.each_key do |key|
      next if key.is_a?(Integer) || key.is_a?(IO) || key.is_a?(Array)
      raise ArgumentError, "wrong exec option: #{key.inspect}" if key.is_a?(String)
      unless SPAWN_OPTION_KEYS.include?(key)
        raise ArgumentError, "wrong exec option symbol: #{key.inspect}"
      end
    end

    [env, args, options]
  end

  def self.__build_spawn_script__(args, options)
    if args.size == 1 && !args.first.is_a?(Array)
      # One string: the shell gets it verbatim, so expansion and whitespace splitting
      # both behave the way MRI's shell form does.
      command = __check_spawn_string__(args.first, "command")
      raise Errno::ENOENT, "" if command.strip.empty?
      body = command
    else
      first = args.first
      if first.is_a?(Array) || (!first.is_a?(String) && first.respond_to?(:to_ary))
        pair = first.to_ary
        unless pair.size == 2
          raise ArgumentError, "wrong first argument"
        end
        name = __check_spawn_string__(pair[0], "command")
        argv0 = __check_spawn_string__(pair[1], "command")
      else
        name = __check_spawn_string__(first, "command")
        argv0 = name
      end
      rest = args[1..-1].map { |a| __check_spawn_string__(a, "string") }
      # Resolve before running so that a missing or unusable command is ENOENT/EACCES
      # from Process.spawn itself, not a 127 from the shell.
      resolved = __resolve_executable__(name)
      body = ([__shell_quote__(resolved)] + rest.map { |a| __shell_quote__(a) }).join(" ")
      body = "exec -a #{__shell_quote__(argv0)} #{body}" if argv0 != name
    end

    prefix = []
    if (dir = options[:chdir])
      dir = dir.respond_to?(:to_path) ? dir.to_path : dir
      dir = __check_spawn_string__(dir, "path name")
      raise Errno::ENOENT, dir unless File.directory?(dir)
      prefix << "cd #{__shell_quote__(dir)}"
    end
    if (mask = options[:umask])
      prefix << format("umask %o", mask.to_int)
    end

    redirects = options.map { |k, v| __spawn_redirect__(k, v) }.compact
    script = body
    script = "#{script} #{redirects.join(' ')}" unless redirects.empty?
    script = "exec #{script}" unless script.start_with?("exec ")
    script = (prefix + [script]).join(" && ") unless prefix.empty?
    script
  end

  def self.spawn(*args)
    env, command, options = __parse_spawn_args__(args)

    [:unsetenv_others, :close_others, :new_pgroup].each do |key|
      next unless options.key?(key)
      value = options[key]
      unless value.nil? || value == true || value == false
        raise ArgumentError, "wrong exec option: #{key.inspect}"
      end
    end
    if options.key?(:pgroup)
      pgroup = options[:pgroup]
      raise TypeError, "wrong exec option" if pgroup.is_a?(Symbol)
      raise ArgumentError, "negative process group ID : #{pgroup}" if pgroup.is_a?(Integer) && pgroup < 0
    end

    script = __build_spawn_script__(command, options)
    __spawn__(script, env, options[:unsetenv_others] ? true : false)
  end

  # --- POSIX calls that come back as -errno ---------------------------------------
  #
  # Most of the Errno family is defined in Ruby further up this file, so there is no
  # CLR class for the runtime to throw. Mapping the number here instead gives every
  # one of them - ESRCH, EPERM, EINVAL and the rest - without any C# counterpart.

  def self.__errno_class__(errno)
    @errno_classes ||= Errno.constants.each_with_object({}) do |name, h|
      klass = Errno.const_get(name)
      number = (klass.const_get(:Errno) rescue nil) if klass.is_a?(Class)
      h[number] ||= klass if number.is_a?(Integer)
    end
    @errno_classes[errno] || SystemCallError
  end

  def self.__check__(result, message = nil)
    raise __errno_class__(-result), message if result.is_a?(Integer) && result < 0
    result
  end

  # The CLR half of kill hands back -errno rather than throwing, for the same reason.
  class << self
    unless method_defined?(:__clr_kill__) || private_method_defined?(:__clr_kill__)
      alias_method :__clr_kill__, :kill

      def kill(signal, *pids)
        Process.__check__(__clr_kill__(signal, *pids))
      end
    end
  end

  unless respond_to?(:getrlimit)
    def getrlimit(resource)
      resource = __rlimit_resource__(resource)
      result = __getrlimit__(resource)
      __check__(result) if result.is_a?(Integer)
      result
    end
    module_function :getrlimit

    def setrlimit(resource, soft, hard = nil)
      resource = __rlimit_resource__(resource)
      soft = __rlimit_value__(soft)
      hard = hard.nil? ? soft : __rlimit_value__(hard)
      __check__(__setrlimit__(resource, soft, hard))
      nil
    end
    module_function :setrlimit

    # MRI takes the number, or the constant's name with or without the RLIMIT_ prefix.
    def __rlimit_resource__(resource)
      case resource
      when Integer then resource
      when Symbol, String
        name = resource.to_s
        name = "RLIMIT_#{name}" unless name.start_with?("RLIMIT_")
        unless const_defined?(name)
          raise ArgumentError, "invalid resource name: #{resource}"
        end
        const_get(name)
      else
        unless resource.respond_to?(:to_int)
          raise TypeError, "no implicit conversion of #{resource.class} into Integer"
        end
        value = resource.to_int
        unless value.is_a?(Integer)
          raise TypeError, "can't convert #{resource.class} to Integer"
        end
        value
      end
    end
    module_function :__rlimit_resource__

    def __rlimit_value__(value)
      return value if value.is_a?(Integer)
      unless value.respond_to?(:to_int)
        raise TypeError, "no implicit conversion of #{value.class} into Integer"
      end
      value.to_int
    end
    module_function :__rlimit_value__
  end

  unless respond_to?(:getpgid)
    def getpgid(pid)
      __check__(__getpgid__(pid.to_int))
    end
    module_function :getpgid

    def setpgid(pid, pgid)
      __check__(__setpgid__(pid.to_int, pgid.to_int))
      0
    end
    module_function :setpgid

    def getsid(pid = 0)
      __check__(__getsid__(pid.to_int))
    end
    module_function :getsid

    def setsid
      __check__(__setsid__)
    end
    module_function :setsid
  end

  unless respond_to?(:getpriority)
    def getpriority(which, who)
      __check__(__getpriority__(which.to_int, who.to_int))
    end
    module_function :getpriority

    def setpriority(which, who, priority)
      __check__(__setpriority__(which.to_int, who.to_int, priority.to_int))
      0
    end
    module_function :setpriority
  end

  unless const_defined?(:Sys)
    module Sys
      def getuid;   Process.uid;  end
      def geteuid;  Process.__geteuid__; end
      def getgid;   Process.gid;  end
      def getegid;  Process.__getegid__; end
      def issetugid; Process.__issetugid__; end
      module_function :getuid, :geteuid, :getgid, :getegid, :issetugid
    end
  end

  unless const_defined?(:UID)
    module UID
      def rid;  Process.uid; end
      def eid;  Process.euid; end
      def sid_available?; false; end
      module_function :rid, :eid, :sid_available?
    end
  end

  unless const_defined?(:GID)
    module GID
      def rid;  Process.gid; end
      def eid;  Process.egid; end
      def sid_available?; false; end
      module_function :rid, :eid, :sid_available?
    end
  end

  def self.waitpid2(pid = -1, flags = 0)
    __waitpid__(pid.to_int, flags.to_int)
  end

  def self.waitpid(pid = -1, flags = 0)
    result = waitpid2(pid, flags)
    result && result[0]
  end

  def self.wait(pid = -1, flags = 0)
    waitpid(pid, flags)
  end

  def self.wait2(pid = -1, flags = 0)
    waitpid2(pid, flags)
  end

  def self.waitall
    results = []
    loop do
      begin
        pair = __waitpid__(-1, 0)
      rescue SystemCallError
        break
      end
      break if pair.nil?
      results << pair
    end
    results
  end
end

class SignalException
  # The CLR-backed class carries only the message, but MRI names the signal both
  # ways round: #signm is "SIGTERM" and #signo is 15.
  def signm
    message
  end unless method_defined?(:signm)

  def signo
    ::Signal.list[message.to_s.sub(/\ASIG/, "")]
  end unless method_defined?(:signo)
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
    def each_key; @table.each_value { |(k, _)| yield k }; self; end
    def each_value; @table.each_value { |(_, v)| yield v }; self; end
    def each_pair(&block); each(&block); end
  end unless const_defined?(:WeakMap)

  # 3.2's map with weakly-held keys compared by equality rather than identity.
  # Same caveat as WeakMap: the references here are strong, so entries outlive
  # what MRI would collect, which is safe but not weak.
  class WeakKeyMap
    def initialize
      @table = {}
    end

    def [](key)
      @table[key]
    end

    def []=(key, value)
      # MRI refuses a key it could not hold weakly.
      case key
      when ::Integer, ::Float, ::Symbol, ::TrueClass, ::FalseClass, ::NilClass
        ::Kernel.raise(::ArgumentError, "WeakKeyMap keys must be garbage collectable")
      end
      @table[key] = value
    end

    def delete(key)
      if @table.key?(key)
        @table.delete(key)
      elsif block_given?
        yield key
      end
    end

    # The key already in the map that is equal to the one given.
    def getkey(key)
      @table.each_key { |k| return k if k == key }
      nil
    end

    def key?(key)
      @table.key?(key)
    end
    alias_method :member?, :key?
    alias_method :include?, :key?

    def clear
      @table.clear
      self
    end


    def inspect
      "#<ObjectSpace::WeakKeyMap:0x#{(object_id << 1).to_s(16).rjust(16, '0')} size=#{@table.size}>"
    end
  end unless const_defined?(:WeakKeyMap)
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

# Binding had nothing but the CLR accessor for its receiver, so all of
# spec/core/binding errored. Kernel#eval already accepts a binding, and every
# one of these can be asked of the binding through it.
class Binding
  def receiver
    self.SelfObject
  end unless method_defined?(:receiver)

  def eval(code, file = nil, line = nil)
    if file
      ::Kernel.eval(code, self, file, line || 1)
    else
      ::Kernel.eval(code, self)
    end
  end unless method_defined?(:eval)

  def local_variables
    eval("local_variables")
  end unless method_defined?(:local_variables)

  def local_variable_defined?(name)
    eval("defined?(#{__check_lvar_name__(name)}) == 'local-variable'")
  end unless method_defined?(:local_variable_defined?)

  def local_variable_get(name)
    name = __check_lvar_name__(name)
    unless local_variable_defined?(name)
      ::Kernel.raise(::NameError, "local variable '#{name}' is not defined for #{inspect}")
    end
    eval(name)
  end unless method_defined?(:local_variable_get)

  # The value cannot be written into the eval'd source, so it is parked in a
  # thread-local and read back out from inside the binding.
  def local_variable_set(name, value)
    name = __check_lvar_name__(name)
    ::Thread.current[:__ir_binding_value__] = value
    eval("#{name} = ::Thread.current[:__ir_binding_value__]")
    value
  end unless method_defined?(:local_variable_set)

  # Only a plain identifier names a local; a global, an instance variable or a
  # special variable such as $~ is a NameError rather than a way in.
  def __check_lvar_name__(name)
    name = __ir_variable_name__(name)
    unless name =~ /\A[_\p{Alpha}][_\p{Alnum}]*\z/
      ::Kernel.raise(::NameError, "wrong local variable name '#{name}' for #{inspect}")
    end
    name
  end
  private :__check_lvar_name__
end

class IO
  def self.try_convert(obj)
    obj.respond_to?(:to_io) ? obj.to_io : nil
  end unless respond_to?(:try_convert)

  # 3.1's IO::Buffer, backed by a String rather than by mapped memory: there is
  # no zero-copy to be had here, so an "external" or "mapped" buffer is the
  # internal kind wearing a different flag. 160 of spec/core/io's errors were
  # this constant not existing at all.
  class Buffer
    include ::Comparable

    PAGE_SIZE = 4096
    DEFAULT_SIZE = 65536
    EXTERNAL = 1
    INTERNAL = 2
    MAPPED = 4
    SHARED = 8
    LOCKED = 32
    PRIVATE = 64
    READONLY = 128
    LITTLE_ENDIAN = 4
    BIG_ENDIAN = 8
    HOST_ENDIAN = LITTLE_ENDIAN
    NETWORK_ENDIAN = BIG_ENDIAN

    class AllocationError < ::RuntimeError; end
    class AccessError < ::RuntimeError; end
    class InvalidatedError < ::RuntimeError; end
    class LockedError < ::RuntimeError; end
    class MaskError < ::ArgumentError; end

    def self.for(string)
      buffer = allocate
      flags = EXTERNAL | (string.frozen? ? READONLY : 0)
      buffer.__take_over__(string, 0, string.bytesize, flags)
      if block_given?
        begin
          return yield(buffer)
        ensure
          buffer.free
        end
      end
      buffer
    end

    # Yields a buffer of the given size and answers what was written into it.
    def self.string(length)
      buffer = new(length)
      yield buffer
      buffer.get_string
    end

    def self.map(file, size = nil, offset = 0, flags = 0)
      data = file.pread(size || (file.size - offset), offset)
      buffer = allocate
      buffer.__take_over__(data.dup, 0, data.bytesize, MAPPED | flags)
      buffer
    end

    def initialize(size = DEFAULT_SIZE, flags = INTERNAL)
      size = ::Kernel.Integer(size)
      ::Kernel.raise(::ArgumentError, "Size can't be negative!") if size < 0
      @flags = flags | ((flags & (EXTERNAL | MAPPED)) != 0 ? 0 : INTERNAL)
      @data = "\0".b * size
      @offset = 0
      @size = size
      @freed = false
    end

    def __set_parent__(parent)
      @parent = parent
    end

    def __take_over__(data, offset, size, flags)
      @data = data
      @offset = offset
      @size = size
      @flags = flags
      @freed = false
    end

    def __check__
    end
    private :__check__

    def size
      @freed ? 0 : @size
    end

    def empty?
      size == 0
    end

    # Freeing a buffer leaves it valid-but-null; what makes a buffer invalid is
    # the storage underneath going away, which is what a slice of a transferred
    # or freed buffer is looking at.
    def valid?
      @parent.nil? || !@parent.__storage_dead__
    end

    def __storage_dead__
      defined?(@storage_dead) ? @storage_dead : false
    end
    protected :__storage_dead__

    def null?
      @freed || @size == 0
    end

    def external?
      (@flags & EXTERNAL) != 0
    end

    def internal?
      !null? && (@flags & INTERNAL) != 0
    end

    def mapped?
      (@flags & MAPPED) != 0
    end

    def shared?
      (@flags & SHARED) != 0
    end

    def private?
      (@flags & PRIVATE) != 0
    end

    def readonly?
      (@flags & READONLY) != 0
    end

    def locked?
      (@flags & LOCKED) != 0
    end

    def __check_writable__
      __check__
      ::Kernel.raise(AccessError, "Buffer is not writable!") if readonly?
      ::Kernel.raise(LockedError, "Buffer already locked!") if locked?
    end
    private :__check_writable__

    def locked
      __check__
      ::Kernel.raise(LockedError, "Buffer already locked!") if locked?
      @flags |= LOCKED
      begin
        yield self
      ensure
        @flags &= ~LOCKED
      end
    end

    def free
      @freed = true
      @storage_dead = true
      @data = "".b
      @offset = 0
      @size = 0
      self
    end

    def transfer
      __check__
      other = self.class.allocate
      other.__take_over__(@data, @offset, @size, @flags)
      @freed = true
      @storage_dead = true
      @data = "".b
      @offset = 0
      @size = 0
      other
    end

    def resize(new_size)
      __check_writable__
      if external? || mapped?
        ::Kernel.raise(AccessError, "Cannot resize external buffer!")
      end
      new_size = ::Kernel.Integer(new_size)
      ::Kernel.raise(::ArgumentError, "Size can't be negative!") if new_size < 0
      current = get_string
      grown = current.byteslice(0, new_size).to_s
      grown = grown + ("\0".b * (new_size - grown.bytesize)) if grown.bytesize < new_size
      @data = grown
      @offset = 0
      @size = new_size
      self
    end

    def slice(offset = 0, length = nil)
      __check__
      offset = ::Kernel.Integer(offset)
      ::Kernel.raise(::ArgumentError, "Offset can't be negative!") if offset < 0
      length = @size - offset if length.nil?
      length = ::Kernel.Integer(length)
      ::Kernel.raise(::ArgumentError, "Length can't be negative!") if length < 0
      if offset + length > @size
        ::Kernel.raise(::ArgumentError, "Specified offset+length is bigger than the buffer size!")
      end
      other = self.class.allocate
      other.__take_over__(@data, @offset + offset, length, @flags)
      other.__set_parent__(self)
      other
    end

    def get_string(offset = 0, length = nil, encoding = ::Encoding::BINARY)
      __check__
      offset = ::Kernel.Integer(offset)
      length = @size - offset if length.nil?
      length = ::Kernel.Integer(length)
      if offset < 0 || length < 0 || offset + length > @size
        ::Kernel.raise(::ArgumentError, "Specified offset+length is bigger than the buffer size!")
      end
      result = @data.byteslice(@offset + offset, length).to_s
      result.force_encoding(encoding) if result.respond_to?(:force_encoding)
      result
    end
    alias_method :to_str, :get_string

    def set_string(string, offset = 0, length = nil, source_offset = 0)
      __check_writable__
      offset = ::Kernel.Integer(offset)
      source = string.byteslice(source_offset, length || (string.bytesize - source_offset)).to_s
      if offset + source.bytesize > @size
        ::Kernel.raise(::ArgumentError, "Specified offset+length is bigger than the buffer size!")
      end
      binary = @data.dup
      binary.force_encoding(::Encoding::BINARY) if binary.respond_to?(:force_encoding)
      piece = source.dup
      piece.force_encoding(::Encoding::BINARY) if piece.respond_to?(:force_encoding)
      at = @offset + offset
      @data = binary.byteslice(0, at).to_s + piece +
              binary.byteslice(at + piece.bytesize, binary.bytesize).to_s
      source.bytesize
    end

    def clear(value = 0, offset = 0, length = nil)
      __check_writable__
      length = @size - offset if length.nil?
      set_string([value & 0xff].pack("C") * length, offset)
      self
    end

    def __value_size__(type)
      case type
      when :U8, :S8 then 1
      when :U16, :S16 then 2
      when :U32, :S32, :f32 then 4
      when :U64, :S64, :f64 then 8
      else ::Kernel.raise(::ArgumentError, "Invalid type name!")
      end
    end
    private :__value_size__

    def __directive__(type)
      case type
      when :U8 then "C"
      when :S8 then "c"
      when :U16 then "S>"
      when :S16 then "s>"
      when :U32 then "L>"
      when :S32 then "l>"
      when :U64 then "Q>"
      when :S64 then "q>"
      when :f32 then "g"
      when :f64 then "G"
      else ::Kernel.raise(::ArgumentError, "Invalid type name!")
      end
    end
    private :__directive__

    def get_value(type, offset)
      get_string(offset, __value_size__(type)).unpack(__directive__(type))[0]
    end

    def set_value(type, offset, value)
      set_string([value].pack(__directive__(type)), offset)
    end

    def get_values(types, offset = 0)
      at = offset
      types.map do |type|
        v = get_value(type, at)
        at += __value_size__(type)
        v
      end
    end

    def each(type = :U8, offset = 0, count = nil)
      return ::Enumerator.new { |y| each(type, offset, count) { |i, v| y << [i, v] } } unless block_given?
      step = __value_size__(type)
      at = offset
      n = count || ((@size - offset) / step)
      n.times do
        yield at, get_value(type, at)
        at += step
      end
      self
    end

    def each_byte(offset = 0, count = nil)
      return ::Enumerator.new { |y| each_byte(offset, count) { |i, v| y << [i, v] } } unless block_given?
      each(:U8, offset, count) { |i, v| yield i, v }
      self
    end

    def values(type = :U8, offset = 0, count = nil)
      result = []
      each(type, offset, count) { |_, v| result << v }
      result
    end

    # Offset, sixteen bytes in hex padded out to a fixed width, then the same
    # bytes as text with anything unprintable shown as a dot - MRI's layout.
    def hexdump
      get_string.bytes.each_slice(16).each_with_index.map { |row, i|
        hex = row.map { |b| format("%02x", b) }.join(" ")
        text = row.map { |b| (b >= 0x20 && b < 0x7f) ? b.chr : "." }.join
        format("0x%08x  %-47s %s", i * 16, hex, text)
      }.join("\n")
    end

    def to_s
      parts = []
      parts << "EXTERNAL" if external?
      parts << "INTERNAL" if internal? && !null?
      parts << "MAPPED" if mapped?
      parts << "SHARED" if shared?
      parts << "LOCKED" if locked?
      parts << "PRIVATE" if private?
      parts << "READONLY" if readonly?
      parts << "NULL" if null?
      "#<IO::Buffer 0x#{(object_id << 1).to_s(16).rjust(16, '0')}+#{@size} #{parts.join(' ')}>"
    end
    # MRI's inspect is the header with the hexdump under it; to_s is the
    # header on its own.
    def inspect
      return to_s if null? || size == 0
      "#{to_s}#{nl_}#{hexdump}"
    end

    def nl_
      "\n"
    end
    private :nl_

    def <=>(other)
      return nil unless other.is_a?(::IO::Buffer)
      get_string <=> other.get_string
    end

    def ==(other)
      other.is_a?(::IO::Buffer) && get_string == other.get_string
    end

    # The copying operators also want a Buffer, and also work over the bytes
    # the two have in common - the result is the size of the receiver.
    def __binary_op__(other, op)
      __require_buffer__(other)
      a = get_string
      b = other.get_string
      n = [a.bytesize, b.bytesize].min
      bytes = a.bytes
      n.times { |i| bytes[i] = bytes[i].__send__(op, b.getbyte(i)) & 0xff }
      result = ::IO::Buffer.new(bytes.size)
      result.set_string(bytes.pack("C*"))
      result
    end
    private :__binary_op__

    # The in-place forms. MRI insists on a Buffer for the right-hand side of
    # these, where the copying forms are happy with anything string-shaped, and
    # it works over as many bytes as the two have in common rather than
    # refusing a mismatch.
    def __require_buffer__(other)
      unless other.is_a?(::IO::Buffer)
        ::Kernel.raise(::TypeError, "wrong argument type #{other.class} (expected IO::Buffer)")
      end
      other
    end
    private :__require_buffer__

    def __in_place_op__(other, op)
      __check_writable__
      __require_buffer__(other)
      a = get_string
      b = other.get_string
      n = [a.bytesize, b.bytesize].min
      bytes = a.bytes
      n.times { |i| bytes[i] = bytes[i].__send__(op, b.getbyte(i)) & 0xff }
      set_string(bytes.pack("C*"))
      self
    end
    private :__in_place_op__

    def and!(other)
      __in_place_op__(other, :&)
    end

    def or!(other)
      __in_place_op__(other, :|)
    end

    def xor!(other)
      __in_place_op__(other, :^)
    end

    def not!
      __check_writable__
      set_string(get_string.bytes.map { |x| (~x) & 0xff }.pack("C*"))
      self
    end

    # Moving bytes between the buffer and an IO. There is no scatter/gather
    # underneath, so these are a plain read or write of the slice in question.
    # MRI reads and writes as much as the buffer holds - the length argument is
    # a minimum, not a cap - and answers how many bytes moved.
    def read(io, length = nil, offset = 0)
      want = size - offset
      data = io.read(want)
      return nil if data.nil?
      set_string(data, offset)
      data.bytesize
    end

    def write(io, length = nil, offset = 0)
      io.write(get_string(offset, size - offset))
    end

    def pread(io, from, length = nil, offset = 0)
      data = io.pread(size - offset, from)
      set_string(data, offset)
      data.bytesize
    end

    def pwrite(io, from, length = nil, offset = 0)
      io.pwrite(get_string(offset, size - offset), from)
    end

    def copy(source, offset = 0, length = nil, source_offset = 0)
      __require_buffer__(source)
      set_string(source.get_string, offset, length, source_offset)
    end

    def &(other)
      __binary_op__(other, :&)
    end

    def |(other)
      __binary_op__(other, :|)
    end

    def ^(other)
      __binary_op__(other, :^)
    end

    def ~
      bytes = get_string.bytes.map { |x| (~x) & 0xff }
      result = ::IO::Buffer.new(bytes.size)
      result.set_string(bytes.pack("C*"))
      result
    end
  end

  # Same for IO.read: the text form is tagged with the encoding asked for,
  # the length form is bytes. IO.binread stays binary, which is its whole job.
  class << self
    alias_method :__ir_class_read__, :read
    private :__ir_class_read__

    def read(name, *args)
      options = args.last.is_a?(::Hash) ? args.pop : nil
      result = args.empty? ? __ir_class_read__(name) : __ir_class_read__(name, *args)
      return result if result.nil? || !result.respond_to?(:force_encoding)
      return result unless args.empty? || args[0].nil?
      enc = nil
      internal = nil
      if options
        enc = options[:encoding] || options["encoding"]
        if enc.is_a?(::String) && enc.include?(":")
          # "external:internal" asks for a conversion, same as the mode string.
          external, internal = enc.split(":", 2)
          enc = external
        end
        enc = ::Encoding.find(enc) if enc
        internal = ::Encoding.find(internal) if internal
      end
      enc ||= ::Encoding.default_external
      result.force_encoding(enc) if enc
      if internal && enc != internal && enc != ::Encoding::BINARY
        result = result.encode(internal)
      end
      result
    end
  end

  # read with no length answers text in the stream's external encoding; read
  # with a length answers bytes, and is ASCII-8BIT. The C# read always handed
  # back ASCII-8BIT, so File.read(path, encoding: "utf-8") came out binary.
  alias_method :__ir_read__, :read
  private :__ir_read__

  def read(*args)
    result = __ir_read__(*args)
    return result if result.nil?
    return result unless args.empty? || args[0].nil?
    return result unless result.respond_to?(:force_encoding)
    enc = (external_encoding rescue nil) || ::Encoding.default_external
    result.force_encoding(enc) if enc
    __transcode__(result)
  end

  # set_encoding takes "external:internal" in one string as well as the two
  # separately; the C# one only understood a single encoding name and answered
  # "unknown encoding name - utf-8:ISO-8859-1".
  alias_method :__ir_set_encoding__, :set_encoding

  def set_encoding(*args)
    if args.size >= 1 && args[0].is_a?(::String) && args[0].include?(":")
      external, internal = args[0].split(":", 2)
      rest = args[1..-1] || []
      return __ir_set_encoding__(external, internal, *rest)
    end
    __ir_set_encoding__(*args)
  end

  # binmode is implemented for a File and throws for everything else - a pipe,
  # a socket, the standard streams. On this platform it has nothing to do but
  # say "these are bytes", so record that and set the external encoding, rather
  # than throwing a CLR exception at anything that is not a File.
  alias_method :__ir_binmode__, :binmode

  def binmode
    @__binmode__ = true
    begin
      __ir_binmode__
    rescue ::Exception
      begin
        set_encoding(::Encoding::BINARY)
      rescue ::Exception
      end
    end
    self
  end

  # A stream opened "r:external:internal" is asking for the bytes to be read as
  # the external encoding and handed back as the internal one. Nothing did that,
  # so the text came back tagged external and untranslated. #internal_encoding
  # already answers nil unless there is really a conversion to do - the external
  # side binary, or the two the same, both mean no - so its answer is the whole
  # condition here.
  def __transcode__(text)
    return text if text.nil?
    target = internal_encoding
    return text if target.nil?
    return text unless text.respond_to?(:encode)
    text.encode(target)
  end
  private :__transcode__

  # The line readers take a chomp: option, which the built-ins do not know
  # about - the options hash landed in the separator or limit parameter and came
  # back as "no implicit conversion of Hash into Integer". The option is split
  # off here and the newline taken off each line afterwards.
  def __take_chomp__(args)
    return [args, false] unless !args.empty? && args.last.is_a?(::Hash)
    options = args.last
    return [args, false] unless options.key?(:chomp) || options.empty?
    args = args[0...-1]
    [args, !!options[:chomp]]
  end
  private :__take_chomp__

  def __chomp_line__(line, separator)
    return line if line.nil?
    sep = separator.is_a?(::String) ? separator : $/
    return line if sep.nil? || sep.empty?
    line.end_with?(sep) ? line[0...(line.length - sep.length)] : line
  end
  private :__chomp_line__

  alias_method :__ir_gets__, :gets

  def gets(*args)
    args, chomp = __take_chomp__(args)
    line = __transcode__(__ir_gets__(*args))
    chomp ? __chomp_line__(line, args[0]) : line
  end

  alias_method :__ir_readline__, :readline

  def readline(*args)
    args, chomp = __take_chomp__(args)
    line = __transcode__(__ir_readline__(*args))
    chomp ? __chomp_line__(line, args[0]) : line
  end

  alias_method :__ir_readlines__, :readlines

  def readlines(*args)
    args, chomp = __take_chomp__(args)
    lines = __ir_readlines__(*args).map { |l| __transcode__(l) }
    chomp ? lines.map { |l| __chomp_line__(l, args[0]) } : lines
  end

  alias_method :__ir_each_line__, :each_line

  def each_line(*args, &block)
    args, chomp = __take_chomp__(args)
    unless block
      return ::Enumerator.new { |y| each_line(*args, chomp: chomp) { |l| y << l } }
    end
    __ir_each_line__(*args) do |line|
      line = __transcode__(line)
      block.call(chomp ? __chomp_line__(line, args[0]) : line)
    end
  end

  if method_defined?(:each)
    alias_method :__ir_each__, :each
    def each(*args, &block)
      each_line(*args, &block)
    end
  end

  class << self
    alias_method :__ir_class_readlines__, :readlines

    def readlines(name, *args)
      options = args.last.is_a?(::Hash) ? args.pop : nil
      chomp = options && options[:chomp]
      lines = __ir_class_readlines__(name, *args)
      return lines unless chomp
      sep = args[0].is_a?(::String) ? args[0] : $/
      lines.map { |l| (sep && !sep.empty? && l.end_with?(sep)) ? l[0...(l.length - sep.length)] : l }
    end

    alias_method :__ir_foreach__, :foreach

    def foreach(name, *args, &block)
      options = args.last.is_a?(::Hash) ? args.pop : nil
      chomp = options && options[:chomp]
      unless block
        return ::Enumerator.new { |y| foreach(name, *args, chomp: chomp) { |l| y << l } }
      end
      sep = args[0].is_a?(::String) ? args[0] : $/
      __ir_foreach__(name, *args) do |line|
        if chomp && sep && !sep.empty? && line.end_with?(sep)
          line = line[0...(line.length - sep.length)]
        end
        block.call(line)
      end
    end
  end

  # readpartial reads what is there, up to maxlen bytes, and only blocks when
  # nothing is there at all. Nothing here has a non-blocking read underneath, so
  # on a stream that is already open this is a read of at most maxlen bytes -
  # which is what MRI does on a regular file too. The result is bytes, so
  # ASCII-8BIT, and end of file is an EOFError rather than nil.
  def readpartial(maxlen, outbuf = nil)
    maxlen = ::Kernel.Integer(maxlen)
    ::Kernel.raise(::ArgumentError, "negative length #{maxlen} given") if maxlen < 0
    if maxlen == 0
      result = "".b
      return outbuf ? outbuf.replace(result) : result
    end
    data = __ir_read__(maxlen)
    if data.nil? || data.empty?
      ::Kernel.raise(::EOFError, "end of file reached")
    end
    data.force_encoding(::Encoding::BINARY) if data.respond_to?(:force_encoding)
    outbuf ? outbuf.replace(data) : data
  end unless method_defined?(:readpartial)

  class << self
    # IO.pipe takes the encodings for the read end, and its block form yields the
    # pair, closes both afterwards and answers what the block answered. The
    # built-in took no arguments at all and gave the pair back from the block
    # form instead of the block's value.
    alias_method :__ir_pipe__, :pipe

    def pipe(*args)
      options = args.last.is_a?(::Hash) ? args.pop : nil
      external, internal = args
      if external.is_a?(::String) && external.include?(":")
        external, internal = external.split(":", 2)
      end
      if options
        external ||= options[:external_encoding]
        internal ||= options[:internal_encoding]
        if (enc = options[:encoding]) && external.nil?
          external, internal = enc.is_a?(::String) && enc.include?(":") ? enc.split(":", 2) : [enc, internal]
        end
      end

      read_end, write_end = __ir_pipe__
      if external
        internal ? read_end.set_encoding(external, internal) : read_end.set_encoding(external)
      end

      return [read_end, write_end] unless block_given?
      begin
        yield(read_end, write_end)
      ensure
        read_end.close unless read_end.closed?
        write_end.close unless write_end.closed?
      end
    end

    # Multiplexing through poll(2), for the streams that have an operating
    # system descriptor to poll. Not all of them do: IronRuby's IO.pipe is not
    # backed by a FileStream, so IO#GetNativeDescriptor answers -1 for a pipe
    # and there is nothing to ask the kernel about. Those streams are reported
    # ready - the same guess as before, but now confined to the cases where no
    # better answer exists, and never allowed to turn into an indefinite wait.
    POLLIN__ = 0x001
    POLLOUT__ = 0x004
    POLLERR__ = 0x008
    POLLHUP__ = 0x010
    POLLNVAL__ = 0x020

    def select(reads = nil, writes = nil, errors = nil, timeout = nil)
      [reads, writes, errors].each do |list|
        next if list.nil? || list.respond_to?(:to_ary)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{list.class} into Array")
      end
      reads = (reads || []).to_a
      writes = (writes || []).to_a
      errors = (errors || []).to_a
      return nil if reads.empty? && writes.empty? && errors.empty?

      pollable = []
      unpollable = []
      [[reads, POLLIN__, :read], [writes, POLLOUT__, :write], [errors, 0, :error]].each do |list, event, kind|
        list.each do |io|
          fd = ::IO.GetNativeDescriptor(io) rescue -1
          (fd >= 0 ? pollable : unpollable) << [io, event, kind, fd]
        end
      end

      readable = []
      writable = []
      failing = []

      unless unpollable.empty?
        unpollable.each do |io, _, kind, _|
          next if io.closed?
          case kind
          when :read then readable << io
          when :write then writable << io
          end
        end
      end

      unless pollable.empty?
        # If anything was answered without polling there is already a result, so
        # the poll must not wait; otherwise honour the caller's timeout.
        millis =
          if !readable.empty? || !writable.empty?
            0
          elsif timeout.nil?
            -1
          else
            (timeout.to_f * 1000).round
          end
        revents = ::IO.Poll(pollable.map { |_, _, _, fd| fd }, pollable.map { |_, e, _, _| e }, millis)
        if revents.nil?
          pollable.each do |io, _, kind, _|
            next if io.closed?
            case kind
            when :read then readable << io
            when :write then writable << io
            end
          end
        else
          pollable.each_with_index do |(io, _, kind, _), i|
            got = revents[i]
            case kind
            when :read
              readable << io if (got & (POLLIN__ | POLLHUP__ | POLLERR__ | POLLNVAL__)) != 0
            when :write
              writable << io if (got & (POLLOUT__ | POLLERR__ | POLLNVAL__)) != 0
            else
              failing << io if (got & (POLLERR__ | POLLNVAL__)) != 0
            end
          end
        end
      end

      return nil if readable.empty? && writable.empty? && failing.empty?
      [readable, writable, failing]
    end
  end

  # Reads a byte-order mark, and if there is one, adopts the encoding it names
  # and leaves the stream positioned after it. Answers nil when there is none.
  BOMS__ = [
    ["\xEF\xBB\xBF".b, "UTF-8"],
    ["\x00\x00\xFE\xFF".b, "UTF-32BE"],
    ["\xFF\xFE\x00\x00".b, "UTF-32LE"],
    ["\xFE\xFF".b, "UTF-16BE"],
    ["\xFF\xFE".b, "UTF-16LE"],
  ]

  def set_encoding_by_bom
    unless binmode?
      ::Kernel.raise(::ArgumentError, "ASCII incompatible encoding needs binmode")
    end
    start = pos
    head = __ir_read__(4).to_s
    head.force_encoding(::Encoding::BINARY) if head.respond_to?(:force_encoding)
    match = BOMS__.find { |bytes, _| head.start_with?(bytes) }
    unless match
      seek(start)
      return nil
    end
    seek(start + match[0].bytesize)
    enc = ::Encoding.find(match[1])
    set_encoding(enc)
    enc
  end unless method_defined?(:set_encoding_by_bom)

  # ---- the byte and character side of IO ---------------------------------

  # getc answered a byte as an Integer, which is the 1.8 meaning; MRI has
  # answered a one-character String since 1.9, reading as many bytes as the
  # character needs.
  if instance_method(:getc).arity == 0
    alias_method :__ir_getc__, :getc
    private :__ir_getc__

    def getc
      first = __ir_getc__
      return nil if first.nil?
      return first if first.is_a?(::String)
      enc = (external_encoding rescue nil) || ::Encoding.default_external
      bytes = [first]
      char = nil
      4.times do
        char = bytes.pack("C*")
        char.force_encoding(enc) if char.respond_to?(:force_encoding)
        break if char.valid_encoding?
        nxt = __ir_getc__
        break if nxt.nil?
        bytes << nxt
      end
      __transcode__(char)
    end
  end

  def getbyte
    s = read(1)
    return nil if s.nil? || s.empty?
    s.getbyte(0)
  end unless method_defined?(:getbyte)

  def readbyte
    b = getbyte
    ::Kernel.raise(::EOFError, "end of file reached") if b.nil?
    b
  end unless method_defined?(:readbyte)

  def ungetbyte(byte)
    return nil if byte.nil?
    if byte.is_a?(::Integer)
      ungetc(byte & 0xff)
    else
      ::Kernel.String(byte).bytes.reverse_each { |b| ungetc(b) }
    end
    nil
  end unless method_defined?(:ungetbyte)

  def each_char
    return ::Enumerator.new { |y| each_char { |c| y << c } } unless block_given?
    while (c = getc)
      yield c
    end
    self
  end unless method_defined?(:each_char)

  def each_codepoint
    return ::Enumerator.new { |y| each_codepoint { |c| y << c } } unless block_given?
    each_char { |c| yield c.ord }
    self
  end unless method_defined?(:each_codepoint)

  alias_method :codepoints, :each_codepoint unless method_defined?(:codepoints)

  # ---- descriptor flags ---------------------------------------------------
  #
  # There is no exec here to leak a descriptor into, and no way to unset
  # binmode once set, so these record what they are told and answer it back.

  def close_on_exec=(value)
    @__close_on_exec__ = !!value
  end unless method_defined?(:close_on_exec=)

  def close_on_exec?
    defined?(@__close_on_exec__) && !@__close_on_exec__ ? false : true
  end unless method_defined?(:close_on_exec?)

  # The built-in answers from the stream mode, which a pipe never gets set
  # because the built-in binmode throws on one. Fall back to the flag that
  # the binmode above records.
  if method_defined?(:binmode?)
    alias_method :__ir_binmode_p__, :binmode?
    private :__ir_binmode_p__
  end

  def binmode?
    return true if defined?(@__binmode__) && @__binmode__
    respond_to?(:__ir_binmode_p__, true) ? __ir_binmode_p__ : false
  end

  # A hint to the kernel about the access pattern; there is nothing to pass it
  # to here, but MRI still validates the arguments and answers nil.
  def advise(advice, offset = 0, len = 0)
    unless %i[normal sequential random willneed dontneed noreuse].include?(advice)
      ::Kernel.raise(::NotImplementedError, "Unsupported advice: #{advice.inspect}")
    end
    ::Kernel.Integer(offset)
    ::Kernel.Integer(len)
    nil
  end unless method_defined?(:advise)

  def fdatasync
    fsync
    0
  end unless method_defined?(:fdatasync)

  def to_path
    respond_to?(:path) ? path : nil
  end unless method_defined?(:to_path)

  # Positional read/write, done by saving and restoring the file position since
  # there is no pread/pwrite underneath.
  def pread(maxlen, offset, buffer = nil)
    ::Kernel.raise(::ArgumentError, "negative string size") if maxlen < 0
    saved = pos
    begin
      seek(offset)
      result = read(maxlen)
      ::Kernel.raise(::EOFError, "end of file reached") if result.nil?
      buffer ? buffer.replace(result) : result
    ensure
      seek(saved)
    end
  end unless method_defined?(:pread)

  def pwrite(string, offset)
    saved = pos
    begin
      seek(offset)
      write(string)
    ensure
      seek(saved)
    end
  end unless method_defined?(:pwrite)

  # Nothing here blocks on a descriptor the way a real event loop would, so a
  # readable stream is one that is not at end of file.
  def wait_readable(timeout = nil)
    eof? ? nil : self
  rescue ::IOError
    nil
  end unless method_defined?(:wait_readable)

  def wait_writable(timeout = nil)
    closed? ? nil : self
  end unless method_defined?(:wait_writable)
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
  # The built-in handles an Integer range; a Float one it refuses outright,
  # and MRI searches those too - bisecting the interval rather than the
  # integers in it.
  if method_defined?(:bsearch)
    alias_method :__ir_bsearch__, :bsearch
    private :__ir_bsearch__
  end

  def bsearch(&block)
    return ::Enumerator.new { |y| each { |x| y << x } } unless block
    b = self.begin
    e = self.end
    if (b.nil? || b.is_a?(::Integer)) && (e.nil? || e.is_a?(::Integer))
      respond_to?(:__ir_bsearch__, true) ? __ir_bsearch__(&block) : __bsearch_int__(block)
    elsif (b.nil? || b.is_a?(::Numeric)) && (e.nil? || e.is_a?(::Numeric))
      __bsearch_float__(block)
    else
      ::Kernel.raise(::TypeError, "can't do binary search for #{(b || e).class}")
    end
  end

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
    ::Enumerator::ArithmeticSequence.__build__(self.begin, self.end, n, exclude_end?, self)
  end

  alias_method :__ir_step__, :step

  # Without a block, MRI answers an arithmetic sequence rather than a plain
  # Enumerator, and the specs check the class.
  def step(n = 1, &block)
    return __ir_step__(n, &block) if block
    if self.begin.is_a?(::Numeric) && (self.end.nil? || self.end.is_a?(::Numeric))
      ::Enumerator::ArithmeticSequence.__build__(self.begin, self.end, n, exclude_end?, self)
    else
      __ir_step__(n)
    end
  end

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

# Marshal support for the numeric tower. MRI dumps Rational and Complex through
# #marshal_dump/#marshal_load (a two element array), not as plain objects with
# instance variables, and the loaded value is frozen like every other numeric.
class Rational
  def marshal_dump
    [numerator, denominator]
  end

  def marshal_load(pair)
    instance_variable_set(:@numerator, pair[0])
    instance_variable_set(:@denominator, pair[1])
    freeze
    self
  end
end

class Complex
  def marshal_dump
    [real, imaginary]
  end

  def marshal_load(pair)
    instance_variable_set(:@real, pair[0])
    instance_variable_set(:@image, pair[1])
    freeze
    self
  end
end
