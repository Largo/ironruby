# Ruby 4 compatibility layer for IronRuby's 1.9-era core library.
# Pure Ruby, loaded from gem_prelude. Pattern-matching support classes plus
# widely-used core methods added between Ruby 2.0 and 4.0.

class NoMatchingPatternError < StandardError; end
class NoMatchingPatternKeyError < NoMatchingPatternError; end
class FrozenError < RuntimeError; end unless defined?(FrozenError)

class Object
  def then(&block)
    return to_enum(:then) { 1 } unless block
    yield self
  end unless method_defined?(:then)
  alias_method :yield_self, :then unless method_defined?(:yield_self)

  def itself
    self
  end unless method_defined?(:itself)

  # The Method object for a method defined only on this object. Kernel#method
  # would happily answer one inherited from the class.
  def singleton_method(name)
    name = name.to_sym if name.respond_to?(:to_sym)
    unless name.is_a?(::Symbol)
      ::Kernel.raise(::TypeError, "#{name.inspect} is not a symbol nor a string")
    end
    klass = (singleton_class rescue nil)
    defined = klass &&
      (klass.instance_methods(false).include?(name) ||
       klass.private_instance_methods(false).include?(name) ||
       klass.protected_instance_methods(false).include?(name))
    unless defined
      ::Kernel.raise(::NameError, "undefined singleton method `#{name}\' for #{inspect}")
    end
    method(name)
  end unless method_defined?(:singleton_method)
end

module Kernel
  private

  def require_relative(path)
    caller_path = caller.first.split(/:\d/, 2).first
    require File.expand_path(path, File.dirname(caller_path))
  end unless private_method_defined?(:require_relative)
end

module Enumerable
  def filter_map(&block)
    return to_enum(:filter_map) { size if respond_to?(:size) } unless block
    result = []
    # Like #map, the yielded values reach the block as they were yielded:
    # `yield 1, 2` is 1 to a one-parameter block, not [1, 2].
    each { |*values| v = block.call(*values); result << v if v }
    result
  end unless method_defined?(:filter_map)

  # The optional Hash counts into it instead of into a fresh one, which also
  # means the frozen check happens before the first element is looked at.
  def tally(counts = nil)
    if counts.nil?
      result = {}
      each { |*values| item = __enum_item__(values); result[item] = (result[item] || 0) + 1 }
      return result
    end

    hash = counts.is_a?(Hash) ? counts : Hash.try_convert(counts)
    raise TypeError, "no implicit conversion of #{counts.class} into Hash" if hash.nil?
    raise FrozenError.new("can't modify frozen Hash: #{hash.inspect}") if hash.frozen?
    each do |*values|
      item = __enum_item__(values)
      # #fetch rather than #[] so that a default value or default proc on the
      # given Hash cannot be mistaken for a count that is already there.
      n = hash.fetch(item, 0)
      unless n.is_a?(Integer)
        raise TypeError, "wrong argument type #{n.class} (expected Integer)"
      end
      hash[item] = n + 1
    end
    hash
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

  # Extra arguments are passed straight through to #each, which is how a
  # receiver whose #each takes parameters gets to see them.
  def each_entry(*args, &block)
    return to_enum(:each_entry, *args) unless block
    each(*args) { |*values| block.call(__enum_item__(values)) }
    self
  end unless method_defined?(:each_entry)

  unless method_defined?(:each_with_index_without_args)
    alias_method :each_with_index_without_args, :each_with_index

    def each_with_index(*args, &block)
      return to_enum(:each_with_index, *args) { size if respond_to?(:size) } unless block
      return each_with_index_without_args(&block) if args.empty?
      index = 0
      each(*args) do |*values|
        block.call(__enum_item__(values), index)
        index += 1
      end
      self
    end
  end

  # Ruby 2.5 gave the predicates an optional pattern, matched with #===.
  # The built-ins only know the block form, and because they are defined on
  # Enumerable they shadow rather than extend, so wrap them.
  unless method_defined?(:all_without_pattern?)
    alias_method :all_without_pattern?, :all?
    alias_method :any_without_pattern?, :any?
    alias_method :none_without_pattern?, :none?
    alias_method :one_without_pattern?, :one?

    def __check_pattern_args__(args, block = nil)
      if args.size > 1
        raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
      end
      # The pattern wins and the block is never called, which is worth saying out loud.
      if block && !args.empty? && !$VERBOSE.nil?
        warn "warning: given block not used"
      end
    end
    private :__check_pattern_args__

    def all?(*args, &block)
      __check_pattern_args__(args, block)
      return all_without_pattern?(&block) if args.empty?
      pattern = args[0]
      each { |*values| return false unless pattern === __enum_item__(values) }
      true
    end

    def any?(*args, &block)
      __check_pattern_args__(args, block)
      return any_without_pattern?(&block) if args.empty?
      pattern = args[0]
      each { |*values| return true if pattern === __enum_item__(values) }
      false
    end

    def none?(*args, &block)
      __check_pattern_args__(args, block)
      return none_without_pattern?(&block) if args.empty?
      pattern = args[0]
      each { |*values| return false if pattern === __enum_item__(values) }
      true
    end

    def one?(*args, &block)
      __check_pattern_args__(args, block)
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

  # The method-name form of #inject reports a bad name the way MRI does - by
  # inspecting the object rather than by naming its class - and takes at most
  # two arguments. A block alongside a method name is never called.
  unless method_defined?(:inject_without_name_check)
    alias_method :inject_without_name_check, :inject

    def inject(*args, &block)
      if args.size > 2
        raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)"
      end
      if args.size == 2 || (args.size == 1 && !block)
        name = args.last
        unless name.is_a?(Symbol) || name.is_a?(String)
          # A name that is neither is still given the chance to be a String.
          name = name.respond_to?(:to_str) ? name.to_str : nil
          unless name.is_a?(String)
            raise TypeError, "#{args.last.inspect} is not a symbol nor a string"
          end
          args = args.dup
          args[-1] = name
        end
        if block && !$VERBOSE.nil?
          warn "warning: given block not used"
        end
      end
      inject_without_name_check(*args, &block)
    end
    alias_method :reduce, :inject
  end

  # min/max also grew an `n` form.
  unless method_defined?(:min_without_count)
    alias_method :min_without_count, :min
    alias_method :max_without_count, :max

    def min(*args, &block)
      n = args.empty? ? nil : __count_arg__(args[0])
      return min_without_count(&block) if n.nil?
      sorted = block ? to_a.sort(&block) : to_a.sort
      sorted.first(n)
    end

    def max(*args, &block)
      n = args.empty? ? nil : __count_arg__(args[0])
      return max_without_count(&block) if n.nil?
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
    return to_enum(:min_by, *args) { size if respond_to?(:size) } unless block
    args = [] if args.size == 1 && args[0].nil?
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
    return to_enum(:max_by, *args) { size if respond_to?(:size) } unless block
    args = [] if args.size == 1 && args[0].nil?
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

  # nil is not a count: `max(nil)` is the plain no-argument form, not an error.
  def __count_arg__(n)
    return nil if n.nil?
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
    return to_enum(:minmax_by) { size if respond_to?(:size) } unless block
    [min_by(&block), max_by(&block)]
  end unless method_defined?(:minmax_by)

  def flat_map
    return to_enum(:flat_map) { size if respond_to?(:size) } unless block_given?
    result = []
    each do |*values|
      # Like #map, #flat_map hands the yielded values straight to the block
      # rather than the packed item: `yield 1, 2` reaches `{ |a| }` as 1.
      mapped = yield(*values)
      array = if mapped.is_a?(Array)
                mapped
              elsif mapped.respond_to?(:to_ary)
                converted = mapped.to_ary
                # An element that answers #to_ary has promised an Array; nil
                # means "not one after all", anything else is a broken promise.
                unless converted.nil? || converted.is_a?(Array)
                  raise TypeError, "can't convert #{mapped.class} to Array " \
                                   "(#{mapped.class}#to_ary gives #{converted.class})"
                end
                converted
              end
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
    each(*args) do |*values|
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

  # The whole slicing family answers a lazy Enumerator, never an Array: the
  # receiver may be endless, and code that chains .lazy or .first onto one of
  # these must not force the source. Each of them therefore does its work
  # inside an Enumerator block and yields groups as they are completed. None of
  # them can say how many groups there will be without running, so the
  # Enumerator has no size.
  def chunk_while(&block)
    # MRI builds a Proc out of the block up front, so the missing-block error
    # is the one #to_proc gives rather than a "no block given" LocalJumpError.
    raise ArgumentError, "tried to create Proc object without a block" unless block
    source = self
    Enumerator.new do |yielder|
      chunk = nil
      previous = nil
      source.each do |*values|
        item = __enum_item__(values)
        if chunk.nil?
          chunk = [item]
        elsif block.call(previous, item)
          chunk << item
        else
          yielder.yield(chunk)
          chunk = [item]
        end
        previous = item
      end
      yielder.yield(chunk) if chunk
    end
  end unless method_defined?(:chunk_while)

  def slice_when(&block)
    raise ArgumentError, "tried to create Proc object without a block" unless block
    chunk_while { |a, b| !block.call(a, b) }
  end unless method_defined?(:slice_when)

  # Exactly one of a pattern and a block, and MRI reports a wrong count against
  # whichever form was chosen: with a block the method takes no arguments.
  def __slice_args__(both_message, args, block)
    if block
      unless args.empty?
        raise ArgumentError, both_message || "wrong number of arguments (given #{args.size}, expected 0)"
      end
    elsif args.size != 1
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1)"
    end
  end
  private :__slice_args__

  # A new group begins at every element the pattern or block accepts; the first
  # element always starts one, however it answers.
  def slice_before(*args, &block)
    __slice_args__(nil, args, block)
    pattern = args[0]
    source = self
    Enumerator.new do |yielder|
      group = nil
      source.each do |*values|
        item = __enum_item__(values)
        starts = block ? block.call(item) : (pattern === item)
        if group.nil?
          group = [item]
        elsif starts
          yielder.yield(group)
          group = [item]
        else
          group << item
        end
      end
      yielder.yield(group) if group
    end
  end unless method_defined?(:slice_before)

  def slice_after(*args, &block)
    # MRI words the both-given case differently in #slice_after than it does
    # in #slice_before.
    __slice_args__("both pattern and block are given", args, block)
    pattern = args[0]
    source = self
    Enumerator.new do |yielder|
      group = []
      source.each do |*values|
        item = __enum_item__(values)
        group << item
        if block ? block.call(item) : (pattern === item)
          yielder.yield(group)
          group = []
        end
      end
      yielder.yield(group) unless group.empty?
    end
  end unless method_defined?(:slice_after)

  def reverse_each(&block)
    return to_enum(:reverse_each) { size if respond_to?(:size) } unless block
    to_a.reverse_each(&block)
    self
  end unless method_defined?(:reverse_each)

  # The block's value groups adjacent elements, but three of its answers are
  # not keys at all: nil and :_separator drop the element and end the run,
  # :_alone puts the element in a group of its own, and every other Symbol
  # starting with an underscore is reserved and rejected.
  def chunk(&block)
    return to_enum(:chunk) { size if respond_to?(:size) } unless block
    source = self
    Enumerator.new do |yielder|
      key = nil
      chunk = nil
      source.each do |*values|
        item = __enum_item__(values)
        k = block.call(item)
        if k.nil? || k == :_separator
          yielder.yield([key, chunk]) if chunk
          key = nil
          chunk = nil
        elsif k == :_alone
          yielder.yield([key, chunk]) if chunk
          yielder.yield([:_alone, [item]])
          key = nil
          chunk = nil
        elsif k.is_a?(Symbol) && k.to_s.start_with?("_")
          raise RuntimeError, "symbols beginning with an underscore are reserved"
        elsif chunk && k == key
          chunk << item
        else
          yielder.yield([key, chunk]) if chunk
          key = k
          chunk = [item]
        end
      end
      yielder.yield([key, chunk]) if chunk
    end
  end unless method_defined?(:chunk)

  # #sort_by has to walk the receiver exactly once - the block may be expensive
  # or have side effects - which the decorate/sort/undecorate helper does.
  unless method_defined?(:sort_by_without_enumerator)
    alias_method :sort_by_without_enumerator, :sort_by

    def sort_by(&block)
      return to_enum(:sort_by) { size if respond_to?(:size) } unless block
      __sort_by_key__(block)
    end
  end

  # Neither of these can know how long the answer will be without running the
  # block, so their Enumerators report no size even when the receiver has one.
  unless method_defined?(:take_while_without_enumerator)
    alias_method :take_while_without_enumerator, :take_while
    alias_method :drop_while_without_enumerator, :drop_while

    def take_while(&block)
      return to_enum(:take_while) { nil } unless block
      take_while_without_enumerator(&block)
    end

    def drop_while(&block)
      return to_enum(:drop_while) { nil } unless block
      drop_while_without_enumerator(&block)
    end
  end

  # Both return the receiver when they are given a block (they returned nil),
  # and both reject a slice size that cannot produce any slice at all.
  unless method_defined?(:each_cons_without_self)
    alias_method :each_cons_without_self, :each_cons
    alias_method :each_slice_without_self, :each_slice

    def __slice_size__(n, message)
      size = n
      size = size.to_int if !size.is_a?(Integer) && size.respond_to?(:to_int)
      raise ArgumentError, message if size.is_a?(Integer) && size <= 0
      n
    end
    private :__slice_size__

    def each_cons(n, &block)
      n = __slice_size__(n, "invalid size")
      return each_cons_without_self(n) unless block
      each_cons_without_self(n, &block)
      self
    end

    def each_slice(n, &block)
      n = __slice_size__(n, "invalid slice size")
      return each_slice_without_self(n) unless block
      each_slice_without_self(n, &block)
      self
    end
  end

  # An argument that is not an Array is taken by #to_ary if it has one and
  # otherwise iterated with #each - a Range or a lazy Enumerator zips fine.
  # Only something that can do neither is an error.
  unless method_defined?(:zip_without_conversion)
    alias_method :zip_without_conversion, :zip

    def __zip_source__(other)
      return other if other.is_a?(Array)
      if other.respond_to?(:to_ary)
        converted = other.to_ary
        return converted if converted.is_a?(Array)
      end
      unless other.respond_to?(:each)
        raise TypeError, "wrong argument type #{other.class} (must respond to :each)"
      end
      other.to_enum(:each)
    end
    private :__zip_source__

    def zip(*others, &block)
      sources = others.map { |other| __zip_source__(other) }
      result = block ? nil : []
      index = 0
      each do |*values|
        row = [__enum_item__(values)]
        sources.each do |source|
          row << if source.is_a?(Array)
                   source[index]
                 else
                   begin
                     source.next
                   rescue StopIteration
                     nil
                   end
                 end
        end
        index += 1
        block ? block.call(row) : result << row
      end
      result
    end
  end
end

class Range
  # The number of elements the range iterates. MRI 3.4 only counts a range that
  # begins at an Integer; a Float beginning cannot be walked at all (1.0..2.0
  # has no elements to count), and a beginning that has #succ can be walked but
  # not counted in advance, so it answers nil rather than guessing.
  def size
    from = self.begin
    to = self.end
    if from.is_a?(::Integer)
      if to.is_a?(::Numeric)
        return ::Enumerator::ArithmeticSequence.__interval_step_size__(from, to, 1, exclude_end?)
      end
      return ::Float::INFINITY if to.nil?
    elsif from.nil?
      raise TypeError, "can't iterate from NilClass"
    elsif !from.respond_to?(:succ)
      raise TypeError, "can't iterate from #{from.class}"
    end
    nil
  end

  # Walking a range backwards needs a finite end to start from. An Integer end
  # can be counted down from forever, which is how a beginless range works;
  # anything else has to be materialised first.
  def reverse_each(&block)
    unless block
      range = self
      enum = ::Enumerator.new { |y| range.reverse_each { |x| y << x } }
      enum.__set_size__(lambda { range.__send__(:__reverse_each_size__) })
      return enum
    end
    to = self.end
    raise TypeError, "can't iterate from NilClass" if to.nil?
    from = self.begin
    if from.nil?
      raise TypeError, "can't iterate from NilClass" unless to.is_a?(::Integer)
      i = exclude_end? ? to - 1 : to
      loop do
        block.call(i)
        i -= 1
      end
      return self
    end
    to_a.reverse_each(&block)
    self
  end

  # Matches MRI's range_reverse_each_size, quirks included: the class it names
  # when it refuses is the end's unless the beginning is the unwalkable one.
  def __reverse_each_size__
    from = self.begin
    to = self.end
    raise TypeError, "can't iterate from NilClass" if to.nil?
    if from.is_a?(::Integer)
      size
    elsif from.nil?
      return ::Float::INFINITY if to.is_a?(::Integer)
      raise TypeError, "can't iterate from #{to.class}"
    elsif to.is_a?(::Integer)
      raise TypeError, "can't iterate from Integer"
    elsif from.respond_to?(:succ)
      nil
    else
      raise TypeError, "can't iterate from #{from.class}"
    end
  end
  private :__reverse_each_size__

  # The built-in #first/#last only answer the no-argument form, and because they
  # are defined on Range they hide Enumerable#first(n) rather than falling
  # through to it - so `(0..Float::INFINITY).first(3)` was an ArgumentError.
  unless method_defined?(:first_without_count)
    alias_method :first_without_count, :first
    alias_method :last_without_count, :last

    def first(*args)
      if args.empty?
        raise RangeError, "cannot get the first element of beginless range" if self.begin.nil?
        return first_without_count
      end
      n = __to_count__(args[0])
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
      if args.empty?
        raise RangeError, "cannot get the last element of endless range" if self.end.nil?
        return last_without_count
      end
      n = __to_count__(args[0])
      raise ArgumentError, "negative array size (or size too big)" if n < 0
      to_a.last(n)
    end

    # #first and #last take a count, not an arbitrary object: anything that is
    # not an Integer has to offer #to_int and have it answer one.
    def __to_count__(n)
      return n if n.is_a?(::Integer)
      unless n.respond_to?(:to_int)
        raise TypeError, "no implicit conversion of #{n.nil? ? "nil" : n.class} into Integer"
      end
      converted = n.to_int
      unless converted.is_a?(::Integer)
        raise TypeError, "can't convert #{n.class} to Integer (#{n.class}#to_int gives #{converted.class})"
      end
      converted
    end
    private :__to_count__
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
  # #dig indexes with an Integer and nothing else: unlike #[] it does not take a
  # Range or an arithmetic sequence, because there would be no way to keep
  # digging into the several elements that would answer.
  def dig(key, *rest)
    __dig_step__(self[__array_to_int__(key)], rest)
  end unless method_defined?(:dig)

  # No Array#sum here on purpose: Enumerable#sum already does the compensated
  # summation MRI does, and this was a second, naive copy that Array never
  # reached anyway - method_defined? saw the included Enumerable#sum and skipped it.

  # a[(0..).step(2)]: the sequence describes a stretch of the array and a stride
  # through it. The stretch is normalized like a Range, except that a stride of
  # more than one element makes the normalization strict - a stretch that does
  # not fit is a RangeError rather than a silent clamp - and a negative stride
  # reads the same stretch from its far end.
  def __arithmetic_slice__(sequence)
    unless sequence.is_a?(::Enumerator::ArithmeticSequence)
      raise TypeError, "no implicit conversion of #{sequence.class} into Integer"
    end

    step = sequence.step
    step = 1 if step.nil?
    step = step.to_int unless step.is_a?(::Integer)
    raise ArgumentError, "step can't be 0" if step == 0

    first = sequence.begin
    last = sequence.end
    exclude_end = sequence.exclude_end?
    if step < 0
      # Reading backwards swaps the ends, and the exclusion travels with the
      # element it excluded: it was the last one, it is now the first. The
      # arithmetic is done on the index as written, before it is normalized, so
      # that (0...-1) starts at 0 rather than at the element before the last.
      first, last = last, first
      if exclude_end && !first.nil?
        first += 1
        exclude_end = false
      end
    end

    count = size
    strict = step > 1 || step < -1

    begin_index = first.nil? ? 0 : first.to_int
    begin_index += count if begin_index < 0
    if begin_index < 0 || begin_index > count
      raise RangeError, "#{sequence.inspect} out of range" if strict
      return nil
    end

    if last.nil?
      end_index = count
    else
      end_index = last.to_int
      end_index += count if end_index < 0
      end_index += 1 unless exclude_end
      end_index = count if end_index > count && !strict
    end

    length = end_index - begin_index
    length = 0 if length < 0
    raise RangeError, "#{sequence.inspect} out of range" if strict && length > count
    # Whatever the stretch says, only elements that are really there come out.
    length = count - begin_index if begin_index + length > count

    result = []
    if step > 0
      i = begin_index
      while i < begin_index + length
        result << self[i]
        i += step
      end
    else
      i = begin_index + length - 1
      while i >= begin_index
        result << self[i]
        i += step
      end
    end
    result
  end

  # Array has its own #max, #min and #sum in MRI rather than inheriting Enumerable's,
  # and code in the wild checks which one it gets. The bodies are Enumerable's.
  def max(*args, &block) = super
  def min(*args, &block) = super
  def sum(*args, &block) = super

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
      if value.nil?
        raise TypeError, "no implicit conversion from nil to integer"
      end
      raise TypeError, "no implicit conversion of #{value.class} into Integer"
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
  # Both take a String or something with #to_str, and both answer a copy when
  # there is nothing to remove - including for an empty affix, where
  # "self[0...-0]" would otherwise answer "".
  def __affix__(affix)
    return affix if affix.is_a?(::String)
    converted = affix.respond_to?(:to_str) ? affix.to_str : nil
    unless converted.is_a?(::String)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(affix)} into String")
    end
    converted
  end
  private :__affix__

  def delete_prefix(prefix)
    prefix = __affix__(prefix)
    (!prefix.empty? && start_with?(prefix)) ? self[prefix.length..-1] : dup
  end unless method_defined?(:delete_prefix)

  def delete_suffix(suffix)
    suffix = __affix__(suffix)
    (!suffix.empty? && end_with?(suffix)) ? self[0...-suffix.length] : dup
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
RUBY_COPYRIGHT = "ironruby - Apache License, Version 2.0".freeze unless defined?(RUBY_COPYRIGHT)
RUBY_DESCRIPTION = "ironruby #{RUBY_VERSION} (.NET)".freeze unless defined?(RUBY_DESCRIPTION)

# Ruby 4.0 collects the RUBY_* constants into a module. Every member must be the
# very same object as its RUBY_* counterpart -- ruby/spec asserts identity with
# #equal?, not equality -- so these alias rather than re-create.
module Ruby
  VERSION = RUBY_VERSION
  PATCHLEVEL = RUBY_PATCHLEVEL
  COPYRIGHT = RUBY_COPYRIGHT
  DESCRIPTION = RUBY_DESCRIPTION
  ENGINE = RUBY_ENGINE
  ENGINE_VERSION = RUBY_ENGINE_VERSION
  PLATFORM = RUBY_PLATFORM
  RELEASE_DATE = RUBY_RELEASE_DATE
  REVISION = RUBY_REVISION
end unless defined?(Ruby)

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
  end unless private_method_defined?(:private_constant)

  def public_constant(*names)
    names
  end unless private_method_defined?(:public_constant)
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

class IO
  # What IO.popen adds to the IO it hands back: the child's pid, and a #close that reaps
  # the child so that $? describes it. A module rather than singleton methods, because a
  # singleton `def io.close` has no `super` to reach the library's IO#close with.
  module PopenChild
    def pid
      @__popen_pid__
    end

    def close
      result = super
      begin
        Process.waitpid(@__popen_pid__)
      rescue SystemCallError
      end
      result
    end
  end
end

class << IO
  # IO.pipe used to be a queue between two threads of this process, whose "descriptors"
  # were indices into IronRuby's own table. Nothing outside the process could be handed
  # one, which is the whole point of a pipe as soon as there is a child to talk to, so
  # it is pipe(2) now. The block form is here too; the method it replaces took no block
  # and quietly ignored one.
  def pipe(*args)
    read, write = Process.__os_pipe__
    external, internal = args.reject { |a| a.respond_to?(:to_hash) }
    read.set_encoding(external, internal) if external

    return [read, write] unless block_given?
    begin
      yield(read, write)
    ensure
      write.close unless write.closed?
      read.close unless read.closed?
    end
  end

  # IO.popen on top of Process.spawn and a real pipe. The method it replaces started the
  # child through System.Diagnostics.Process, which meant the redirection options had to
  # be lowered onto the shell command line, there was no way to hand the child anything
  # but the three standard streams, #pid did not exist, and $? was filled in when the
  # child *started* - so every caller that looked at the exit status of a popen child,
  # ruby_exe in mspec above all, was reading a status of zero no matter what happened.
  def popen(*args, &block)
    env = nil
    if !args.empty? && !args.first.is_a?(String) && !args.first.is_a?(Array) &&
       args.first.respond_to?(:to_hash)
      env = args.shift
    end

    options = {}
    if args.size > 1 && !args.last.is_a?(String) && args.last.respond_to?(:to_hash)
      options = args.pop.to_hash.dup
    end

    command = args.shift
    mode = args.shift
    mode = "r" if mode.nil?
    mode = mode.to_str if !mode.is_a?(String) && mode.respond_to?(:to_str)
    mode = mode.to_s.sub(/:.*\z/, "")

    if command == "-" || (command.is_a?(Array) && command.first == "-")
      raise NotImplementedError, "fork() function is unimplemented on this machine"
    end

    readable = mode.include?("r") || mode.include?("+")
    writable = mode.include?("w") || mode.include?("a") || mode.include?("+")

    parent_read = child_write = parent_write = child_read = nil
    if readable
      parent_read, child_write = Process.__os_pipe__
      options[:out] = child_write
    end
    if writable
      child_read, parent_write = Process.__os_pipe__
      options[:in] = child_read
    end

    spawn_args = []
    spawn_args << env if env
    command.is_a?(Array) ? spawn_args.concat(command) : spawn_args << command
    spawn_args << options

    begin
      pid = Process.spawn(*spawn_args)
    rescue Exception
      [parent_read, child_write, parent_write, child_read].each { |io| io.close if io }
      raise
    end

    # The child owns its ends now; holding them open here would keep a read from ever
    # seeing end-of-file.
    child_write.close if child_write
    child_read.close if child_read

    io = if readable && writable
           Process.__duplex_io__(parent_read, parent_write)
         else
           readable ? parent_read : parent_write
         end

    io.instance_variable_set(:@__popen_pid__, pid)
    io.extend(IO::PopenChild)

    return io unless block

    begin
      block.call(io)
    ensure
      io.close unless io.closed?
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

    def shift(*args)
      if args.size > 1
        raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
      end
      return shift_without_count if args.empty?

      # nil is an argument here, not the absence of one, and an object that is
      # not an Integer is asked for one exactly once - #self[0, count] would ask
      # again - before the count is checked for being negative.
      count = args[0]
      unless count.is_a?(Integer)
        unless count.respond_to?(:to_int)
          raise TypeError, "no implicit conversion of #{count.nil? ? 'nil' : count.class} into Integer"
        end
        count = count.to_int
        unless count.is_a?(Integer)
          raise TypeError, "can't convert #{args[0].class} to Integer (#{args[0].class}#to_int gives #{count.class})"
        end
      end
      raise ArgumentError, "negative array size" if count < 0

      __array_check_frozen__
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

  # Warning[] and Warning[]= are implemented in C# (WarningOps): the runtime reads
  # the categories itself - the chilled string literal warning is raised from
  # inside MutableString's mutation guard - and -W:category has to be able to set
  # them before any Ruby code runs.

  # Warning#warn is implemented in C# (WarningOps) too: it is the single hook every
  # warning - Kernel#warn, runtime warnings and parser warnings alike - funnels
  # through, and the first of those are emitted while this very file is being parsed.
  # `extend self' is what makes Warning.warn find it while keeping Method#owner
  # == Warning, as CRuby has it.

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

    # rational18.rb's rounding methods take no precision at all. MRI's rule is the
    # same for all four: 0 and negative precisions answer an Integer, a positive
    # precision answers a Rational scaled back down.
    [:round, :ceil, :floor, :truncate].each do |name|
      alias_method :"__ir_#{name}__", name

      define_method(name) do |ndigits = 0|
        n = ndigits.to_int
        bare = :"__ir_#{name}__"
        next __send__(bare) if n == 0
        if n > 0
          s = 10**n
          Rational((self * s).__send__(bare), s)
        else
          s = 10**(-n)
          (self / s).__send__(bare) * s
        end
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
      # The category gate runs before the $VERBOSE gate and before the message check,
      # matching warning.rb's Kernel#warn: it is the Ruby half of rb_warn_m.
      category = options[:category]
      unless category.nil?
        unless ::Symbol === category
          unless category.respond_to?(:to_sym)
            raise ::TypeError, "no implicit conversion of #{category.class} into Symbol"
          end
          category = category.to_sym
        end
        return nil unless ::Warning[category]
      end
      return nil if messages.empty?
      return nil if $VERBOSE.nil?

      uplevel = options[:uplevel]
      unless uplevel.nil?
        unless ::Integer === uplevel
          unless uplevel.respond_to?(:to_int)
            raise ::TypeError, "no implicit conversion of #{uplevel.class} into Integer"
          end
          uplevel = uplevel.to_int
        end
        raise ::ArgumentError, "negative level (#{uplevel})" if uplevel < 0
        # IronRuby's Kernel#caller only takes the start argument, and its entries carry a
        # trailing ":in `method'" that MRI's uplevel prefix does not.
        location = (caller(uplevel + 1) || [])[0]
        location = location.sub(/:in [`'].*\z/, '') if location
        # MRI prefixes "warning: " even when the level is past the end of the backtrace.
        prefix = location ? "#{location}: warning: " : "warning: "
      end

      # Array arguments are expanded, one element per line (MRI joins with the record
      # separator). Recurse over real Arrays only - probing #to_ary would disturb mocks.
      flattened = []
      expand = ->(list) { list.each { |m| ::Array === m ? expand.call(m) : flattened << m } }
      expand.call(messages)
      text = flattened.map { |m| s = m.to_s; s.end_with?("\n") ? s : s + "\n" }.join
      return nil if text.empty?
      text = "#{prefix}#{text}" if prefix

      # MRI's `exc == rb_mWarning' escape hatch: a Warning#warn override that calls super
      # lands back here, and routing on would recurse forever.
      if equal?(::Warning)
        # The built-in always appends a newline of its own, so drop the last one.
        # By bytes, not by characters: a message is allowed to hold bytes that are
        # invalid in its encoding, and slicing it as characters raises on those.
        raw = text.end_with?("\n") ? text.byteslice(0, text.bytesize - 1) : text
        __ir_warn__(raw)
        return nil
      end

      # rb_warning_warn_arity: an override that takes exactly one argument gets no keywords.
      if ::Warning.method(:warn).arity == 1
        ::Warning.warn(text)
      else
        ::Warning.warn(text, category: category)
      end
      nil
    end
    module_function :warn
  end
end

class String
  # The name MRI puts in a "no implicit conversion of X into Y" message: the
  # three singletons by value, everything else by class - with IronRuby's
  # Fixnum/Bignum split hidden, because MRI only knows Integer.
  def __ir_type_name__(value)
    case value
    when nil then "nil"
    when true then "true"
    when false then "false"
    else
      name = value.class.to_s
      (name == "Fixnum" || name == "Bignum") ? "Integer" : name
    end
  end
  private :__ir_type_name__

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
    # When every character is one byte wide the byte offsets and the character
    # offsets coincide, so the (linear) character table is not needed.
    size = bytesize
    simple = (size == length)
    starts = simple ? nil : __ir_char_starts__

    offset += size if offset < 0
    return nil if offset < 0
    if offset > size
      # #index gives up past the end; #rindex clamps to it.
      return nil unless reverse
      offset = size
    end

    if simple
      char_offset = offset
    else
      char_offset = starts.index(offset)
      raise ::IndexError, "offset #{offset} does not land on character boundary" if char_offset.nil?
    end

    found = reverse ? rindex(needle, char_offset) : index(needle, char_offset)
    return nil if found.nil?
    simple ? found : starts[found]
  end
  private :__ir_byte_search__

  # A byte search needle is a Regexp or something String-convertible; unlike
  # #index it never accepts an Integer codepoint.
  def __ir_byte_needle__(needle)
    return needle if needle.is_a?(::Regexp) || needle.is_a?(::String)
    if needle.respond_to?(:to_str)
      converted = needle.to_str
      return converted if converted.is_a?(::String)
    end
    ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(needle)} into String")
  end
  private :__ir_byte_needle__

  def __ir_byte_offset__(value)
    return value if value.is_a?(::Integer)
    unless value.respond_to?(:to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion from #{__ir_type_name__(value)} to integer")
    end
    converted = value.to_int
    unless converted.is_a?(::Integer)
      ::Kernel.raise(::TypeError, "can't convert #{__ir_type_name__(value)} to Integer")
    end
    converted
  end
  private :__ir_byte_offset__

  def byteindex(needle, offset = 0)
    __ir_byte_search__(false, __ir_byte_needle__(needle), __ir_byte_offset__(offset))
  end unless method_defined?(:byteindex)

  # MRI's default offset is the end of the string, not -1: "hello".byterindex("")
  # answers 5. An explicitly passed nil is still a TypeError.
  def byterindex(needle, *rest)
    if rest.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{rest.size + 1}, expected 1..2)")
    end
    needle = __ir_byte_needle__(needle)
    offset = rest.empty? ? bytesize : __ir_byte_offset__(rest[0])
    __ir_byte_search__(true, needle, offset)
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

  # The frozen check happens before the affix is even looked at: MRI raises
  # FrozenError even when nothing would have been removed.
  def delete_prefix!(prefix)
    prefix = __affix__(prefix)
    __ir_require_unfrozen__
    result = delete_prefix(prefix)
    result == self ? nil : replace(result)
  end unless method_defined?(:delete_prefix!)

  def delete_suffix!(suffix)
    suffix = __affix__(suffix)
    __ir_require_unfrozen__
    result = delete_suffix(suffix)
    result == self ? nil : replace(result)
  end unless method_defined?(:delete_suffix!)

  def __ir_require_unfrozen__
    return unless frozen?
    ::Kernel.raise(::FrozenError.new("can't modify frozen String: #{inspect}", receiver: self))
  end
  private :__ir_require_unfrozen__

  # 3.4's name for -@. Spelled out rather than aliased: String#-@ is itself
  # defined further down this file.
  def dedup
    frozen? ? self : dup.freeze
  end unless method_defined?(:dedup)

  # Parses as much of a complex number as it can and answers (0+0i) for the
  # rest, the way Kernel#Complex(str, exception: false) does. The grammar is
  # MRI's: [real][sign imaginary"i"], or "real@angle" for polar form, with the
  # real and imaginary parts each an integer, a float or a rational.
  # A single underscore may sit between two digits and nowhere else: "12_3" is
  # 123 but "12__3" stops after the 12.
  DIGITS__ = '\d(?:_?\d)*'
  NUMBER__ = "[+-]?(?:#{DIGITS__})?(?:\\.#{DIGITS__})?(?:[eE][+-]?#{DIGITS__})?(?:/#{DIGITS__})?"
  # MRI accepts i, I, j and J as the imaginary unit.
  UNIT__ = '[iIjJ]'

  def to_c
    unless encoding.ascii_compatible?
      ::Kernel.raise(::Encoding::CompatibilityError, "ASCII incompatible encoding: #{encoding}")
    end
    s = strip
    if (m = /\A(#{NUMBER__})@(#{NUMBER__})/o.match(s)) && !m[1].empty? && !m[2].empty?
      return ::Complex.polar(__to_num__(m[1]), __to_num__(m[2]))
    end
    if (m = /\A(#{NUMBER__})?([+-](?:#{DIGITS__})?(?:\.#{DIGITS__})?(?:[eE][+-]?#{DIGITS__})?(?:\/#{DIGITS__})?)#{UNIT__}/o.match(s))
      real = m[1].nil? || m[1].empty? ? 0 : __to_num__(m[1])
      imag = m[2] == "+" ? 1 : (m[2] == "-" ? -1 : __to_num__(m[2]))
      return ::Complex.new(real, imag)
    end
    if (m = /\A(#{NUMBER__})#{UNIT__}/o.match(s)) && !m[1].empty? && m[1] != "+" && m[1] != "-"
      return ::Complex.new(0, __to_num__(m[1]))
    end
    if (m = /\A(#{NUMBER__})/o.match(s)) && !m[1].empty?
      return ::Complex.new(__to_num__(m[1]), 0)
    end
    # A bare unit is an imaginary 1 - and so is any string starting with one,
    # which is why "Infinity".to_c is (0+1i).
    if (m = /\A([+-]?)#{UNIT__}/o.match(s))
      return ::Complex.new(0, m[1] == "-" ? -1 : 1)
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

  # #count is asked once per character, so the selectors are converted up front:
  # MRI calls #to_str on each argument exactly once however long the receiver is.
  def __strip_selectors__(selectors)
    selectors.map do |selector|
      next selector if selector.is_a?(::String)
      converted = selector.respond_to?(:to_str) ? selector.to_str : nil
      unless converted.is_a?(::String)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(selector)} into String")
      end
      converted
    end
  end
  private :__strip_selectors__

  def lstrip(*selectors)
    return __ir_lstrip__ if selectors.empty?
    selectors = __strip_selectors__(selectors)
    # An invalid selector is an error even when the receiver is empty.
    "".count(*selectors) if empty?
    i = 0
    i += 1 while i < length && __selected__(self[i, 1], selectors)
    self[i..-1] || self[0, 0]
  end

  def rstrip(*selectors)
    return __ir_rstrip__ if selectors.empty?
    selectors = __strip_selectors__(selectors)
    "".count(*selectors) if empty?
    i = length
    i -= 1 while i > 0 && __selected__(self[i - 1, 1], selectors)
    self[0, i]
  end

  def strip(*selectors)
    return __ir_strip__ if selectors.empty?
    selectors = __strip_selectors__(selectors)
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
      # The frozen check comes before the work: MRI raises even when the
      # selectors would leave the string alone.
      if frozen?
        ::Kernel.raise(::FrozenError.new("can't modify frozen String: #{inspect}", receiver: self))
      end
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

  def each_grapheme_cluster(&block)
    clusters = __ir_grapheme_clusters__
    return ::Enumerator.new(clusters.size) { |y| clusters.each { |g| y << g } } unless block
    clusters.each { |g| block.call(g) }
    self
  end unless method_defined?(:each_grapheme_cluster)

  # MRI still yields when a block is passed, answering self rather than the array.
  def grapheme_clusters(&block)
    return __ir_grapheme_clusters__ unless block
    __ir_grapheme_clusters__.each { |g| block.call(g) }
    self
  end unless method_defined?(:grapheme_clusters)

  UNICODE_ENCODINGS__ = [
    ::Encoding::UTF_8, ::Encoding::US_ASCII, ::Encoding::UTF_16LE, ::Encoding::UTF_16BE,
    ::Encoding::UTF_32LE, ::Encoding::UTF_32BE
  ]

  # Segmentation is the CLR's, which speaks UTF-16. Anything that is not a
  # Unicode encoding - BINARY, the dummy encodings, the legacy code pages -
  # has no clusters to find, so MRI's answer there is one element per
  # character.
  def __ir_grapheme_clusters__
    return chars unless valid_encoding? && UNICODE_ENCODINGS__.include?(encoding)
    source = (encoding == ::Encoding::UTF_8 || encoding == ::Encoding::US_ASCII) ? self : encode(::Encoding::UTF_8)
    result = []
    e = ::System::Globalization::StringInfo.GetTextElementEnumerator(source.to_clr_string)
    while e.MoveNext
      piece = e.GetTextElement.to_s
      piece.force_encoding(::Encoding::UTF_8) if piece.respond_to?(:force_encoding)
      piece = piece.encode(encoding) unless source.equal?(self)
      result << piece
    end
    result
  end
  private :__ir_grapheme_clusters__

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
        # z**w == exp(w * log z). Going through `r ** other` instead would hand a
        # Complex exponent to Float#**, which coerces straight back to here and
        # recurses until the stack overflows - which is not rescuable.
        r, theta = polar
        a = other.real.to_f
        b = other.imag.to_f
        log_r = ::Math.log(r)
        return ::Complex.polar(::Math.exp(a * log_r - b * theta), b * log_r + a * theta)
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

  # MRI's Dir.each_child / Dir.foreach enumerators do not know their size.
  def each_child(path, *args, &block)
    return to_enum(:each_child, path, *args) { nil } unless block
    children(path, *args).each(&block)
    nil
  end

  unless respond_to?(:__clr_foreach__, true)
    alias_method :__clr_foreach__, :foreach
    private :__clr_foreach__

    def foreach(path, *args, &block)
      return to_enum(:foreach, path, *args) { nil } unless block
      __clr_foreach__(path, *args, &block)
      nil
    end
  end

  def empty?(path)
    # File.stat rather than File.directory? so that a missing path is an ENOENT
    return false unless File.stat(path).directory?
    entries(path).size <= 2
  end unless respond_to?(:empty?)
end

# The instance side of Dir is a subset of the singleton side; MRI gives it #children,
# #each_child and #chdir, and an Enumerator from #each when no block is given.
class Dir
  unless method_defined?(:__clr_each__)
    alias_method :__clr_each__, :each
    private :__clr_each__

    def each(&block)
      return to_enum(:each) { nil } unless block
      __clr_each__(&block)
      self
    end
  end

  def children
    entries - %w[. ..]
  end unless method_defined?(:children)

  def each_child(&block)
    return to_enum(:each_child) { nil } unless block
    children.each(&block)
    self
  end unless method_defined?(:each_child)

  def entries
    result = []
    rewind
    while (entry = read)
      result << entry
    end
    rewind
    result
  end unless method_defined?(:entries)

  def chdir(&block)
    Dir.chdir(path, &block)
  end unless method_defined?(:chdir)
end

module Errno
  # Linux errno numbers and the messages strerror(3) gives for them. Three jobs:
  # bind the names the 1.9 snapshot never bound, give the C#-registered classes
  # their Errno constant -- those are CLR-backed and carry no number, so
  # Errno::ENOENT::Errno used to resolve up to the Errno module itself instead of
  # 2 -- and supply the default message for SystemCallError#initialize below,
  # since the CLR-backed classes spell several of them differently from MRI
  # ("Invalid Argument" for EINVAL).
  ERRNO_TABLE = {
    0 => ["NOERROR", "Success"], 1 => ["EPERM", "Operation not permitted"],
    2 => ["ENOENT", "No such file or directory"], 3 => ["ESRCH", "No such process"],
    4 => ["EINTR", "Interrupted system call"], 5 => ["EIO", "Input/output error"],
    6 => ["ENXIO", "No such device or address"], 7 => ["E2BIG", "Argument list too long"],
    8 => ["ENOEXEC", "Exec format error"], 9 => ["EBADF", "Bad file descriptor"],
    10 => ["ECHILD", "No child processes"], 11 => ["EAGAIN", "Resource temporarily unavailable"],
    12 => ["ENOMEM", "Cannot allocate memory"], 13 => ["EACCES", "Permission denied"],
    14 => ["EFAULT", "Bad address"], 15 => ["ENOTBLK", "Block device required"],
    16 => ["EBUSY", "Device or resource busy"], 17 => ["EEXIST", "File exists"],
    18 => ["EXDEV", "Invalid cross-device link"], 19 => ["ENODEV", "No such device"],
    20 => ["ENOTDIR", "Not a directory"], 21 => ["EISDIR", "Is a directory"],
    22 => ["EINVAL", "Invalid argument"], 23 => ["ENFILE", "Too many open files in system"],
    24 => ["EMFILE", "Too many open files"], 25 => ["ENOTTY", "Inappropriate ioctl for device"],
    26 => ["ETXTBSY", "Text file busy"], 27 => ["EFBIG", "File too large"],
    28 => ["ENOSPC", "No space left on device"], 29 => ["ESPIPE", "Illegal seek"],
    30 => ["EROFS", "Read-only file system"], 31 => ["EMLINK", "Too many links"],
    32 => ["EPIPE", "Broken pipe"], 33 => ["EDOM", "Numerical argument out of domain"],
    34 => ["ERANGE", "Numerical result out of range"], 35 => ["EDEADLK", "Resource deadlock avoided"],
    36 => ["ENAMETOOLONG", "File name too long"], 37 => ["ENOLCK", "No locks available"],
    38 => ["ENOSYS", "Function not implemented"], 39 => ["ENOTEMPTY", "Directory not empty"],
    40 => ["ELOOP", "Too many levels of symbolic links"], 42 => ["ENOMSG", "No message of desired type"],
    43 => ["EIDRM", "Identifier removed"], 44 => ["ECHRNG", "Channel number out of range"],
    45 => ["EL2NSYNC", "Level 2 not synchronized"], 46 => ["EL3HLT", "Level 3 halted"],
    47 => ["EL3RST", "Level 3 reset"], 48 => ["ELNRNG", "Link number out of range"],
    49 => ["EUNATCH", "Protocol driver not attached"], 50 => ["ENOCSI", "No CSI structure available"],
    51 => ["EL2HLT", "Level 2 halted"], 52 => ["EBADE", "Invalid exchange"],
    53 => ["EBADR", "Invalid request descriptor"], 54 => ["EXFULL", "Exchange full"],
    55 => ["ENOANO", "No anode"], 56 => ["EBADRQC", "Invalid request code"],
    57 => ["EBADSLT", "Invalid slot"], 59 => ["EBFONT", "Bad font file format"],
    60 => ["ENOSTR", "Device not a stream"], 61 => ["ENODATA", "No data available"],
    62 => ["ETIME", "Timer expired"], 63 => ["ENOSR", "Out of streams resources"],
    64 => ["ENONET", "Machine is not on the network"], 65 => ["ENOPKG", "Package not installed"],
    66 => ["EREMOTE", "Object is remote"], 67 => ["ENOLINK", "Link has been severed"],
    68 => ["EADV", "Advertise error"], 69 => ["ESRMNT", "Srmount error"],
    70 => ["ECOMM", "Communication error on send"], 71 => ["EPROTO", "Protocol error"],
    72 => ["EMULTIHOP", "Multihop attempted"], 73 => ["EDOTDOT", "RFS specific error"],
    74 => ["EBADMSG", "Bad message"], 75 => ["EOVERFLOW", "Value too large for defined data type"],
    76 => ["ENOTUNIQ", "Name not unique on network"], 77 => ["EBADFD", "File descriptor in bad state"],
    78 => ["EREMCHG", "Remote address changed"], 79 => ["ELIBACC", "Can not access a needed shared library"],
    80 => ["ELIBBAD", "Accessing a corrupted shared library"], 81 => ["ELIBSCN", ".lib section in a.out corrupted"],
    82 => ["ELIBMAX", "Attempting to link in too many shared libraries"], 83 => ["ELIBEXEC", "Cannot exec a shared library directly"],
    84 => ["EILSEQ", "Invalid or incomplete multibyte or wide character"], 85 => ["ERESTART", "Interrupted system call should be restarted"],
    86 => ["ESTRPIPE", "Streams pipe error"], 87 => ["EUSERS", "Too many users"],
    88 => ["ENOTSOCK", "Socket operation on non-socket"], 89 => ["EDESTADDRREQ", "Destination address required"],
    90 => ["EMSGSIZE", "Message too long"], 91 => ["EPROTOTYPE", "Protocol wrong type for socket"],
    92 => ["ENOPROTOOPT", "Protocol not available"], 93 => ["EPROTONOSUPPORT", "Protocol not supported"],
    94 => ["ESOCKTNOSUPPORT", "Socket type not supported"], 95 => ["ENOTSUP", "Operation not supported"],
    96 => ["EPFNOSUPPORT", "Protocol family not supported"], 97 => ["EAFNOSUPPORT", "Address family not supported by protocol"],
    98 => ["EADDRINUSE", "Address already in use"], 99 => ["EADDRNOTAVAIL", "Cannot assign requested address"],
    100 => ["ENETDOWN", "Network is down"], 101 => ["ENETUNREACH", "Network is unreachable"],
    102 => ["ENETRESET", "Network dropped connection on reset"], 103 => ["ECONNABORTED", "Software caused connection abort"],
    104 => ["ECONNRESET", "Connection reset by peer"], 105 => ["ENOBUFS", "No buffer space available"],
    106 => ["EISCONN", "Transport endpoint is already connected"], 107 => ["ENOTCONN", "Transport endpoint is not connected"],
    108 => ["ESHUTDOWN", "Cannot send after transport endpoint shutdown"], 109 => ["ETOOMANYREFS", "Too many references: cannot splice"],
    110 => ["ETIMEDOUT", "Connection timed out"], 111 => ["ECONNREFUSED", "Connection refused"],
    112 => ["EHOSTDOWN", "Host is down"], 113 => ["EHOSTUNREACH", "No route to host"],
    114 => ["EALREADY", "Operation already in progress"], 115 => ["EINPROGRESS", "Operation now in progress"],
    116 => ["ESTALE", "Stale file handle"], 117 => ["EUCLEAN", "Structure needs cleaning"],
    118 => ["ENOTNAM", "Not a XENIX named type file"], 119 => ["ENAVAIL", "No XENIX semaphores available"],
    120 => ["EISNAM", "Is a named type file"], 121 => ["EREMOTEIO", "Remote I/O error"],
    122 => ["EDQUOT", "Disk quota exceeded"], 123 => ["ENOMEDIUM", "No medium found"],
    124 => ["EMEDIUMTYPE", "Wrong medium type"], 125 => ["ECANCELED", "Operation canceled"],
    126 => ["ENOKEY", "Required key not available"], 127 => ["EKEYEXPIRED", "Key has expired"],
    128 => ["EKEYREVOKED", "Key has been revoked"], 129 => ["EKEYREJECTED", "Key was rejected by service"],
    130 => ["EOWNERDEAD", "Owner died"], 131 => ["ENOTRECOVERABLE", "State not recoverable"],
    132 => ["ERFKILL", "Operation not possible due to RF-kill"], 133 => ["EHWPOISON", "Memory page has hardware error"],
  }.freeze

  ERRNO_ALIASES = { "EDEADLOCK" => "EDEADLK", "EOPNOTSUPP" => "ENOTSUP", "EWOULDBLOCK" => "EAGAIN" }.freeze

  ERRNO_CLASSES = {}

  ERRNO_TABLE.each do |errno, (name, message)|
    if const_defined?(name, false)
      klass = const_get(name)
    else
      klass = Class.new(SystemCallError)
      const_set(name, klass)
    end
    # const_defined? without the second argument would find the enclosing Errno
    # module through the lexical scope, so restrict the lookup to the class.
    klass.const_set(:Errno, errno) unless klass.const_defined?(:Errno, false)
    klass.const_set(:Message, message) unless klass.const_defined?(:Message, false)
    ERRNO_CLASSES[errno] ||= klass
  end

  # MRI makes EWOULDBLOCK *the same class* as EAGAIN where the numbers agree.
  ERRNO_ALIASES.each do |name, target|
    next unless const_defined?(target, false)
    remove_const(name) if const_defined?(name, false)
    const_set(name, const_get(target))
  end

  ERRNO_CLASSES.freeze
end

# SystemCallError's constructor understood exactly one argument: either a message
# or an errno, never both, never a location, and it never picked the matching
# Errno subclass. MRI's shape is
#
#   SystemCallError.new(errno)
#   SystemCallError.new(message, errno)
#   SystemCallError.new(message, errno, location)
#   Errno::EINVAL.new(message = nil, location = nil)
#
# and the message is "<strerror> @ <location> - <message>", with each part dropped
# when absent.
class SystemCallError
  def self.__errno_of__(klass)
    klass.const_defined?(:Errno, false) ? klass.const_get(:Errno) : nil
  end

  def self.__default_message__(klass, errno)
    return klass.const_get(:Message) if klass.const_defined?(:Message, false)
    errno.nil? ? "unknown error" : "Unknown error #{errno}"
  end

  # MRI converts the errno with Integer(), so a Float or an exactly-real Complex
  # is truncated and a String is a TypeError.
  def self.__coerce_errno__(errno)
    return nil if errno.nil?
    return errno if errno.is_a?(::Integer)
    if errno.is_a?(::Float)
      ::Kernel.raise(::FloatDomainError, errno.to_s) if errno.nan? || errno.infinite?
      return errno.truncate
    end
    if defined?(::Complex) && errno.is_a?(::Complex)
      unless errno.imaginary == 0
        ::Kernel.raise(::RangeError, "can't convert #{errno} into Integer")
      end
      return __coerce_errno__(errno.real)
    end
    unless errno.respond_to?(:to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{errno.class} into Integer")
    end
    errno.to_int
  end

  def self.__coerce_message__(message)
    return nil if message.nil?
    return message if message.is_a?(::String)
    unless message.respond_to?(:to_str)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{message.class} into String")
    end
    message.to_str
  end

  def self.__build_message__(default, message, location)
    text = default.dup
    text << " @ " << location.to_s unless location.nil?
    text << " - " << message unless message.nil?
    text
  end

  def self.new(*args)
    if equal?(::SystemCallError) && !args.empty?
      if args.size == 1 && !args[0].is_a?(::String) && !args[0].respond_to?(:to_str)
        message, errno = nil, __coerce_errno__(args[0])
      else
        message, errno = __coerce_message__(args[0]), __coerce_errno__(args[1])
      end
      # A known errno names a subclass; an unknown one stays a plain
      # SystemCallError that remembers the number for #errno.
      klass = ::Errno::ERRNO_CLASSES[errno]
      return klass.new(message, args[2]) if klass
      return super(message, errno, args[2])
    end
    super
  end

  def initialize(*args)
    klass = self.class
    if klass.equal?(::SystemCallError)
      if args.empty?
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 0, expected 1..3)")
      end
      if args.size == 1 && !args[0].is_a?(::String) && !args[0].respond_to?(:to_str)
        message, location, errno = nil, nil, ::SystemCallError.__coerce_errno__(args[0])
      else
        message = ::SystemCallError.__coerce_message__(args[0])
        location = args[2]
        errno = ::SystemCallError.__coerce_errno__(args[1])
      end
    else
      message = ::SystemCallError.__coerce_message__(args[0])
      location = args[1]
      errno = ::SystemCallError.__errno_of__(klass)
    end

    @__errno__ = errno
    default = ::SystemCallError.__default_message__(klass, errno)
    super(::SystemCallError.__build_message__(default, message, location))
  end

  def errno
    defined?(@__errno__) ? @__errno__ : super
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

  # The first argument is the Set class to build, not a Set constructor
  # argument: `to_set(MySet)` has to answer a MySet.
  def to_set(klass = nil, *args, &block)
    require 'set'
    (klass || ::Set).new(self, *args, &block)
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

# The CLR collector answers most of what MRI's GC module reports; the rest are
# knobs that have no .NET equivalent and are kept as settings that round-trip.
module GC
  def self.count
    total = 0
    0.upto(::System::GC.max_generation) { |g| total += ::System::GC.collection_count(g) }
    total
  end

  def self.major_count
    ::System::GC.collection_count(::System::GC.max_generation)
  end

  STAT_KEYS = [
    :count, :time, :marking_time, :sweeping_time, :minor_gc_count, :major_gc_count,
    :heap_allocated_pages, :heap_live_slots, :heap_free_slots, :total_allocated_objects,
    :total_freed_objects, :malloc_increase_bytes, :compact_count
  ].freeze

  def self.__stat_values__
    live = ::System::GC.get_total_memory(false).to_i
    major = major_count
    {
      count: count,
      time: total_time / 1_000_000,
      marking_time: 0,
      sweeping_time: 0,
      minor_gc_count: count - major,
      major_gc_count: major,
      heap_allocated_pages: (live / 16_384) + 1,
      heap_live_slots: live / 40,
      heap_free_slots: 0,
      total_allocated_objects: live / 40,
      total_freed_objects: 0,
      malloc_increase_bytes: 0,
      compact_count: 0
    }
  end

  def self.stat(key_or_hash = nil)
    values = __stat_values__
    case key_or_hash
    when nil then values
    when ::Symbol
      unless values.key?(key_or_hash)
        ::Kernel.raise(::ArgumentError, "unknown key: #{key_or_hash}")
      end
      values[key_or_hash]
    when ::Hash
      values.each { |k, v| key_or_hash[k] = v }
      key_or_hash
    else
      ::Kernel.raise(::TypeError, "non-hash or symbol given")
    end
  end

  # MRI returns whether the collector *was* disabled before the call.
  def self.enable
    was = defined?(@disabled) && @disabled
    @disabled = false
    !!was
  end

  def self.disable
    was = defined?(@disabled) && @disabled
    @disabled = true
    !!was
  end

  def self.start(full_mark: true, immediate_mark: true, immediate_sweep: true)
    ::System::GC.collect
    nil
  end

  class << self
    alias_method :garbage_collect, :start
  end

  def self.total_time
    ::System::GC.get_total_pause_duration.total_milliseconds.to_i * 1_000_000
  rescue ::Exception
    0
  end

  def self.auto_compact
    defined?(@auto_compact) ? @auto_compact : false
  end

  def self.auto_compact=(value)
    @auto_compact = value
  end

  def self.measure_total_time
    defined?(@measure_total_time) ? @measure_total_time : true
  end

  def self.measure_total_time=(value)
    @measure_total_time = value
  end

  def self.stress
    defined?(@stress) ? @stress : false
  end

  def self.stress=(value)
    @stress = value
  end

  def self.compact
    ::System::GC.collect
    nil
  end

  # 3.4's pluggable-GC configuration. :implementation is read-only and global;
  # everything else is a per-implementation setting, and IronRuby has exactly one.
  def self.config(options = nil)
    @config ||= { rgengc_allow_full_mark: true }
    case options
    when nil then { implementation: "default" }.merge(@config)
    when ::Hash
      options.each do |key, value|
        if key.to_s.downcase == "implementation"
          ::Kernel.raise(::ArgumentError, 'Attempting to set read-only key "Implementation"')
        end
      end
      options.each { |key, value| @config[key] = value if @config.key?(key) }
      { implementation: "default" }.merge(@config)
    else
      ::Kernel.raise(::ArgumentError, "expecting a Hash, got #{options.class}")
    end
  end

  module Profiler
    def self.enabled?
      defined?(@enabled) ? @enabled : false
    end

    def self.enable
      @enabled = true
      nil
    end

    def self.disable
      @enabled = false
      nil
    end

    def self.clear
      nil
    end

    def self.total_time
      ::GC.total_time / 1_000_000_000.0
    end

    def self.result
      "GC #{::GC.count} invokes.\n"
    end

    def self.report(out = $stdout)
      out.write(result)
      nil
    end
  end
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

    # Unlike #yield, #<< answers the yielder so that it can be chained.
    def <<(value)
      @block.call(value)
      self
    end

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
        # Ruby 3.0 removed Enumerator.new(obj, meth, *args); only the block form
        # is left. __enum_init__ is still how the prelude builds one internally.
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
      end
      @__initialized__ = true
      self
    end

    # Ruby 1.9: extra arguments are appended to the ones the enumerator was built
    # with and handed to the underlying method; without a block that produces a
    # new enumerator rather than iterating.
    def each(*args, &block)
      unless args.empty?
        target = __enum_target__
        if target
          receiver, method, initial = target
          arguments = initial + args
          return receiver.to_enum(method, *arguments) unless block
          return receiver.__send__(method, *arguments, &block)
        end
      end

      return self unless block
      if @generator
        @generator.call(Yielder.new(&block))
        self
      else
        each_without_generator(&block)
      end
    end
  end

  # nil means "no offset", and anything else has to answer #to_int - a Float
  # offset is truncated rather than counted up in halves.
  def __index_offset__(offset)
    return 0 if offset.nil?
    return offset if offset.is_a?(::Integer)
    unless offset.respond_to?(:to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{offset.class} into Integer")
    end
    converted = offset.to_int
    unless converted.is_a?(::Integer)
      ::Kernel.raise(::TypeError, "can't convert #{offset.class} to Integer")
    end
    converted
  end
  private :__index_offset__

  def with_index(offset = 0)
    offset = __index_offset__(offset)
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

  # Enumerable's #each_with_index would win here and it answers self; MRI's
  # Enumerator#each_with_index is #with_index(0), which answers whatever the
  # underlying #each answered, and it takes no arguments at all.
  def each_with_index(*args, &block)
    unless args.empty?
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0)")
    end
    with_index(0, &block)
  end

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
  # Same story as #each_with_index: Enumerable's version would shadow this one,
  # and the specs check that the two really are the same method.
  alias_method :each_with_object, :with_object

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
    return n.call if !n.is_a?(::Numeric) && n.respond_to?(:call)
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

  # n * (n-1) * ... * (n-k+1), which is 1 when k is 0.
  def __enum_falling_factorial__(n, k)
    result = 1
    k.times { |i| result *= (n - i) }
    result
  end
  private :__enum_falling_factorial__

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
    when :combination, :permutation
      # Arithmetic, not iteration: C(n, k) and n!/(n-k)!, and zero when k elements
      # cannot be taken out of n at all.
      n = __source_count__(source)
      if n
        k = arg.nil? ? n : arg
        if k < 0 || k > n
          0
        elsif op == :combination
          __enum_falling_factorial__(n, k) / __enum_falling_factorial__(k, k)
        else
          __enum_falling_factorial__(n, k)
        end
      end
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
    # Enumerator.allocate never got a receiver.
    return "#<#{self.class}: uninitialized>" if recv.nil? && meth == :each && args.empty?
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
      # Only the block form falls back to the eager Enumerable#zip. Everything
      # else stays lazy, including arguments that are not Arrays: those are
      # iterated with #each, which is what lets an endless receiver be zipped
      # with a Range and cut short by #first.
      return eager.zip(*others, &block) if block

      sources = others.map do |other|
        if other.is_a?(::Array)
          other
        elsif other.respond_to?(:to_ary) && (converted = other.to_ary).is_a?(::Array)
          converted
        elsif other.respond_to?(:each)
          other.to_enum(:each)
        else
          ::Kernel.raise(::TypeError, "wrong argument type #{other.class} (must respond to :each)")
        end
      end

      source = self
      __chain__(size) do |y|
        index = 0
        source.each do |*values|
          row = [values.size <= 1 ? values[0] : values]
          sources.each do |other|
            row << if other.is_a?(::Array)
                     other[index]
                   else
                     begin
                       other.next
                     rescue ::StopIteration
                       nil
                     end
                   end
          end
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
    # MRI accepts anything here -- the arguments are only required to answer
    # #each_entry at the point #each actually walks them, which is what lets
    # Product wrap a plain Object that defines each_entry and nothing else.
    def initialize(*enums)
      __check_frozen__
      @__enums__ = enums
      self
    end

    def initialize_copy(other)
      return self if other.equal?(self)
      __check_frozen__
      unless other.instance_of?(self.class)
        ::Kernel.raise(::TypeError, "initialize_copy should take same class object")
      end
      enums = other.__enums_or_nil__
      ::Kernel.raise(::ArgumentError, "uninitialized product") if enums.nil?
      @__enums__ = enums
      self
    end

    def each(&block)
      return to_enum(:each) { size } unless block
      __product__(0, [], block)
      self
    end

    # Ruby 3.2 specifies #each_entry, not #each: an argument that only answers
    # each_entry has to work, and one that answers neither has to raise NoMethodError
    # rather than a TypeError from the constructor.
    def __product__(index, prefix, block)
      if index == @__enums__.size
        block.call(prefix.dup)
        return
      end
      @__enums__[index].each_entry do |*values|
        prefix.push(values.size <= 1 ? values[0] : values)
        __product__(index + 1, prefix, block)
        prefix.pop
      end
    end
    private :__product__

    protected def __enums_or_nil__
      defined?(@__enums__) ? @__enums__ : nil
    end

    # nil for any size MRI cannot use (missing, non-Integer, NaN); zero short-circuits
    # even when another enumerable has an unknown size, and an infinite size carries.
    def size
      total = 1
      unknown = false
      @__enums__.each do |enum|
        n = enum.respond_to?(:size) ? enum.size : nil
        if n.is_a?(::Integer)
          return 0 if n == 0
          total *= n
        elsif n.is_a?(::Float) && n.infinite?
          total *= n
        else
          unknown = true
        end
      end
      unknown ? nil : total
    end

    def rewind
      @__enums__.each { |enum| enum.rewind if enum.respond_to?(:rewind) }
      self
    end

    def inspect
      enums = __enums_or_nil__
      return "#<#{self.class}: uninitialized>" if enums.nil?

      seen = ::Thread.current[:__enumerator_product_inspect__] ||= []
      return "#<#{self.class}: ...>" if seen.any? { |o| o.equal?(self) }

      seen.push(self)
      begin
        "#<#{self.class}: #{enums.inspect}>"
      ensure
        seen.pop
        ::Thread.current[:__enumerator_product_inspect__] = nil if seen.empty?
      end
    end
    alias_method :to_s, :inspect

    def __check_frozen__
      return unless frozen?
      ::Kernel.raise(::FrozenError, "can't modify frozen #{self.class}: #{inspect}")
    end
    private :__check_frozen__
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
  # The first argument is the Set class to build, not a Set constructor
  # argument: `to_set(MySet)` has to answer a MySet.
  def to_set(klass = nil, *args, &block)
    require 'set'
    (klass || ::Set).new(self, *args, &block)
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
    # Not Enumerator.new: Ruby 3.0 removed its (obj, meth, *args) form, and the
    # class-level initializer is what to_enum used to reach through.
    enum = ::Enumerator.allocate
    enum.__send__(:__enum_init__, self, method, args)
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

# String#each_line / String#lines.
#
# The C# implementation is frozen at Ruby 1.8: it has no `chomp:` keyword, its
# paragraph mode (separator "") keeps every newline of the run instead of the
# first two, it accepts separators that are not String-convertible, and it
# raises "string modified" when the block mutates the receiver (checked in 1.8,
# not checked at all from 1.9 on). Everything here is layered on top of the C#
# splitter so encodings and the String-instances-for-subclasses rule keep
# working; only the grouping and the trimming are redone.
class String
  alias_method :__ir_each_line__, :each_line

  def each_line(*args, chomp: false, &block)
    lines = __ir_split_lines__(args, chomp)
    return ::Enumerator.new { |y| lines.each { |l| y << l } } unless block
    lines.each { |line| block.call(line) }
    self
  end

  # MRI's #lines still behaves exactly like #each_line when a block is passed:
  # it yields and answers self rather than the array.
  def lines(*args, chomp: false, &block)
    return each_line(*args, chomp: chomp, &block) if block
    __ir_split_lines__(args, chomp)
  end

  private

  def __ir_split_lines__(args, chomp)
    if args.size > 1
      raise ::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
    end
    separator = args.empty? ? $/ : args[0]
    unless separator.nil? || separator.is_a?(::String)
      unless separator.respond_to?(:to_str)
        raise ::TypeError, "no implicit conversion of #{__ir_type_name__(separator)} into String"
      end
      separator = separator.to_str
      unless separator.is_a?(::String)
        raise ::TypeError, "can't convert #{separator.class} to String"
      end
    end

    # A non-ASCII-compatible encoding (UTF-16, UTF-7, ...) is never split: MRI
    # has to transcode the separator first, which either fails outright or
    # cannot match. The transcode is still attempted so that an encoding with
    # no converter raises Encoding::ConverterNotFoundError.
    unless encoding.ascii_compatible?
      separator.encode(encoding) if separator
      return [] if empty?
      return [__ir_each_line_all__(nil)]
    end

    return empty? ? [] : [__ir_chomp_line__(__ir_each_line_all__(nil), nil, chomp)] if separator.nil?

    if separator.empty?
      lines = __ir_paragraphs__(__ir_each_line_all__("\n"))
    else
      lines = __ir_each_line_all__(separator)
    end
    return lines unless chomp
    lines.map { |line| __ir_chomp_line__(line, separator, chomp) }
  end

  # Collects the physical lines without letting the 1.8 "string modified"
  # check fire: the block below never touches the receiver.
  def __ir_each_line_all__(separator)
    return dup if separator.nil?
    lines = []
    __ir_each_line__(separator) { |line| lines << line }
    lines
  end

  # Paragraph mode: a paragraph ends at the first blank line after some
  # content, that blank line is kept, and every further blank line of the run
  # is dropped.
  def __ir_paragraphs__(physical)
    result = []
    buffer = nil
    skipping = false
    physical.each do |line|
      blank = (line == "\n" || line == "\r\n")
      if skipping
        next if blank
        skipping = false
      end
      if blank && buffer
        result << (buffer + line)
        buffer = nil
        skipping = true
      elsif buffer
        buffer = buffer + line
      else
        buffer = line.dup
      end
    end
    result << buffer if buffer
    result
  end

  def __ir_chomp_line__(line, separator, chomp)
    return line unless chomp
    if separator.nil? || separator == "\n"
      line = line[0...-1] if line.end_with?("\n")
      line = line[0...-1] if line.end_with?("\r")
      line
    elsif separator.empty?
      line = line[0...-1] while line.end_with?("\n") || line.end_with?("\r")
      line
    elsif line.end_with?(separator)
      line[0, line.length - separator.length]
    else
      line
    end
  end

  public
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
    # `false`: Object is an ancestor of Thread, so an inherited lookup finds the
    # top-level name every time and the constant never gets re-homed.
    next if const_defined?(name, false)
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

  # 1.9 let #begin/#end/#offset name a group. The C# signatures only take an
  # Integer, so a name was a TypeError; __group_bounds__ already knows how to
  # resolve both forms.
  unless method_defined?(:__ir_begin__)
    alias_method :__ir_begin__, :begin
    alias_method :__ir_end__, :end
    alias_method :__ir_offset__, :offset

    def begin(n)
      return __ir_begin__(n) if n.is_a?(::Integer) || n.respond_to?(:to_int)
      bounds = __group_bounds__(n)
      bounds && bounds[0]
    end

    def end(n)
      return __ir_end__(n) if n.is_a?(::Integer) || n.respond_to?(:to_int)
      bounds = __group_bounds__(n)
      bounds && bounds[1]
    end

    def offset(n)
      return __ir_offset__(n) if n.is_a?(::Integer) || n.respond_to?(:to_int)
      __group_bounds__(n) || [nil, nil]
    end
  end
end

# Integer#[] grew (index, length) and Range forms in 2.7. The builtin only ever
# took a single bit index, so both were a TypeError or an ArgumentError. All of
# them reduce to (self >> i) & ((1 << len) - 1), with a negative index shifting
# the other way and a non-positive length meaning "no mask".
[Fixnum, Bignum].each do |klass|
  next unless klass.instance_method(:[]).arity == 1

  klass.class_eval do
    alias_method :__ir_bit__, :[]

    def __bit_position__(value)
      return value if value.is_a?(::Integer)
      if value.is_a?(::Float)
        if value.nan? || value.infinite?
          ::Kernel.raise(::FloatDomainError, value.to_s)
        end
        return value.truncate
      end
      unless value.respond_to?(:to_int)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{value.class} into Integer")
      end
      converted = value.to_int
      unless converted.is_a?(::Integer)
        ::Kernel.raise(::TypeError, "can't convert #{value.class} to Integer")
      end
      converted
    end
    private :__bit_position__

    def __bits_from__(first, length)
      shifted = first >= 0 ? (self >> first) : (self << -first)
      return shifted if length.nil? || length <= 0
      shifted & ((1 << length) - 1)
    end
    private :__bits_from__

    def [](index, length = nil)
      unless index.is_a?(::Range)
        return __ir_bit__(index) if length.nil?
        return __bits_from__(__bit_position__(index), __bit_position__(length))
      end

      unless length.nil?
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 2, expected 1)")
      end

      first = index.begin.nil? ? nil : __bit_position__(index.begin)
      last = index.end.nil? ? nil : __bit_position__(index.end)

      if first.nil?
        # A beginless range asks for every bit below `last`; that is only a finite
        # answer when they are all zero.
        if last.nil? || (self & ((1 << (last + 1)) - 1)) != 0
          ::Kernel.raise(::ArgumentError, "The beginless range for Integer#[] results in infinity")
        end
        return 0
      end

      return __bits_from__(first, nil) if last.nil?
      span = last - first + 1
      span -= 1 if index.exclude_end?
      __bits_from__(first, span)
    end
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
          while excl ? i > to : i >= to
            block.call(i)
            i += unit
          end
        else
          while excl ? i < to : i <= to
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
#
# The shape follows struct.c: Data.define builds a subclass that owns the
# member list, its .new normalises whatever it was handed into a keyword hash
# and then calls #initialize, and Data#initialize is the one place that
# validates the keys and freezes the object. Doing the validation inside .new
# instead - which is what the first version of this file did - meant a
# user-written #initialize was never reached, so neither the `super` idiom nor
# psych's Data.instance_method(:initialize).bind_call trick worked.
class Data
  class << self
    def define(*names, &block)
      names = names.map do |m|
        unless m.is_a?(::Symbol) || m.is_a?(::String)
          ::Kernel.raise(::TypeError, "#{m.inspect} is not a symbol nor a string")
        end
        m.to_sym
      end
      duplicate = names.group_by { |m| m }.select { |_, v| v.size > 1 }.keys.first
      ::Kernel.raise(::ArgumentError, "duplicate member: #{duplicate}") if duplicate
      names.freeze

      klass = ::Class.new(self) do
        names.each do |name|
          define_method(name) { instance_variable_get("@#{name}") }
        end

        class << self
          def members
            __data_members__.dup
          end

          def new(*args, **kwargs, &block)
            unless args.empty?
              unless kwargs.empty?
                ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size + 1}, expected 0)")
              end
              members = __data_members__
              if args.size > members.size
                ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..#{members.size})")
              end
              args.each_with_index { |value, i| kwargs[members[i]] = value }
            end
            instance = allocate
            instance.__send__(:initialize, **kwargs, &block)
            instance
          end

          alias_method :[], :new

          def inspect
            ::Module.instance_method(:to_s).bind(self).call
          end
        end
      end
      klass.instance_variable_set(:@__data_members__, names)
      klass.class_eval(&block) if block
      klass
    end

    private

    # The member list lives in an ivar on the class Data.define created. A
    # plain `class Foo < Data` never gets one, and neither does Data itself,
    # which is why .members is defined on the generated class rather than here
    # - `Data.respond_to?(:members)` has to stay false.
    def __data_members__
      klass = self
      while klass
        names = klass.instance_variable_get(:@__data_members__)
        return names if names
        klass = klass.superclass
      end
      ::Kernel.raise(::NoMethodError, "undefined method `members' for #{self}")
    end
  end

  def initialize(**kwargs)
    names = __data_members__
    given = {}
    unknown = []
    kwargs.each do |key, value|
      key = __data_key__(key)
      if names.include?(key.to_sym)
        given[key.to_sym] = value
      else
        unknown << key.inspect
      end
    end
    missing = names.reject { |name| given.key?(name) }
    unless missing.empty?
      ::Kernel.raise(::ArgumentError,
        "missing keyword#{missing.size > 1 ? 's' : ''}: #{missing.map { |n| n.inspect }.join(', ')}")
    end
    given.each { |name, value| instance_variable_set("@#{name}", value) }
    freeze
    unless unknown.empty?
      ::Kernel.raise(::ArgumentError,
        "unknown keyword#{unknown.size > 1 ? 's' : ''}: #{unknown.join(', ')}")
    end
    nil
  end

  def members
    __data_members__.dup
  end

  def to_h(&block)
    result = {}
    __data_members__.each do |name|
      key, value = name, __send__(name)
      if block
        pair = block.call(key, value)
        unless ::Array === pair
          converted = pair.respond_to?(:to_ary) ? pair.to_ary : nil
          unless ::Array === converted
            ::Kernel.raise(::TypeError, "wrong element type #{pair.class} (expected array)")
          end
          pair = converted
        end
        unless pair.size == 2
          ::Kernel.raise(::ArgumentError, "element has wrong array length (expected 2, was #{pair.size})")
        end
        key, value = pair[0], pair[1]
      end
      result[key] = value
    end
    result
  end

  def deconstruct
    __data_members__.map { |name| __send__(name) }
  end

  def deconstruct_keys(keys)
    return to_h if keys.nil?
    unless ::Array === keys
      ::Kernel.raise(::TypeError, "wrong argument type #{keys.class} (expected Array or nil)")
    end
    names = __data_members__
    return {} if keys.size > names.size
    result = {}
    keys.each do |key|
      key = __data_key__(key)
      return result unless names.include?(key.to_sym)
      result[key] = __send__(key.to_sym)
    end
    result
  end

  def with(**kwargs)
    return self if kwargs.empty?
    names = __data_members__
    updates = {}
    unknown = []
    kwargs.each do |key, value|
      key = __data_key__(key)
      if names.include?(key.to_sym)
        updates[key.to_sym] = value
      else
        unknown << key.inspect
      end
    end
    unless unknown.empty?
      ::Kernel.raise(::ArgumentError,
        "unknown keyword#{unknown.size > 1 ? 's' : ''}: #{unknown.join(', ')}")
    end
    copy = self.class.allocate
    copy.__send__(:initialize, **to_h.merge(updates))
    copy
  end

  def ==(other)
    return true if equal?(other)
    return false unless other.class == self.class
    __data_paired__(:==, other) do
      __data_members__.all? { |name| __send__(name) == other.__send__(name) }
    end
  end

  def eql?(other)
    return true if equal?(other)
    return false unless other.class == self.class
    __data_paired__(:eql?, other) do
      __data_members__.all? { |name| __send__(name).eql?(other.__send__(name)) }
    end
  end

  def hash
    stack = (::Thread.current[:__data_hash__] ||= [])
    return self.class.hash if stack.any? { |seen| seen.equal?(self) }
    stack.push(self)
    begin
      ([self.class] + deconstruct).hash
    ensure
      stack.pop
    end
  end

  # struct.c prints the class path, not #name: a Data class whose name method
  # has been redefined still inspects under its real path, and a class whose
  # path starts with '#' - anonymous, or nested inside something anonymous -
  # prints no name at all unless it is the recursive placeholder.
  def to_s
    path = ::Module.instance_method(:to_s).bind(self.class).call
    named = !path.start_with?("#")
    stack = (::Thread.current[:__data_inspect__] ||= [])
    return "#<data #{path}:...>" if stack.any? { |seen| seen.equal?(self) }
    stack.push(self)
    begin
      out = "#<data "
      out << path if named
      __data_members__.each_with_index do |name, i|
        out << (i > 0 ? ", " : (named ? " " : ""))
        out << "#{name}=#{__send__(name).inspect}"
      end
      out << ">"
    ensure
      stack.pop
    end
  end
  alias_method :inspect, :to_s

  private

  def __data_members__
    self.class.__send__(:__data_members__)
  end

  # Data accepts a String wherever it accepts a Symbol, and asks anything else
  # for #to_str before giving up. The value returned here is the key as the
  # caller wrote it - a String stays a String so that #deconstruct_keys can key
  # its result by it, and so that an unknown-keyword message quotes it.
  def __data_key__(key)
    return key if ::Symbol === key
    return key if ::String === key
    unless key.respond_to?(:to_str)
      ::Kernel.raise(::TypeError, "#{key.inspect} is not a symbol nor a string")
    end
    converted = key.to_str
    unless ::String === converted
      ::Kernel.raise(::TypeError,
        "can't convert #{key.class} into String (#{key.class}#to_str gives #{converted.class})")
    end
    converted
  end

  def __data_paired__(tag, other)
    stack = (::Thread.current[:__data_paired__] ||= [])
    pair = [tag, object_id, other.object_id]
    return true if stack.include?(pair)
    stack.push(pair)
    begin
      yield
    ensure
      stack.pop
    end
  end
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
  # Names and values go through #to_str, not #to_s: ENV is as strict as Hash about
  # implicit conversion and reports the failure the same way.
  def __env_str__(object)
    return object if object.is_a?(::String)
    unless object.respond_to?(:to_str)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{object.class} into String")
    end
    converted = object.to_str
    unless converted.is_a?(::String)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{object.class} into String")
    end
    converted
  end
  private :__env_str__

  # setenv(3) rejects a name that is empty or contains '='. The CLR reports that as its
  # own ArgumentException; MRI reports Errno::EINVAL naming the call.
  def __env_check_name__(name)
    if name.empty? || name.include?("=")
      ::Kernel.raise(::Errno::EINVAL, "setenv(#{name})")
    end
    name
  end
  private :__env_check_name__

  unless respond_to?(:__clr_store__, true)
    alias_method :__clr_store__, :[]=
    alias_method :__clr_fetch__, :[]
    private :__clr_store__
    private :__clr_fetch__

    def []=(name, value)
      name = __env_str__(name)
      if value.nil?
        # Deleting a name setenv(3) would have rejected is a no-op, not an error.
        __clr_store__(name, nil) unless name.empty? || name.include?("=")
        return nil
      end
      value = __env_str__(value)
      __env_check_name__(name)
      __clr_store__(name, value)
      value
    end

    alias_method :store, :[]=
  end

  def [](name)
    __clr_fetch__(__env_str__(name))
  end

  def fetch(name, *default)
    if default.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{default.size + 1}, expected 1..2)")
    end
    name = __env_str__(name)
    if block_given? && !default.empty?
      ::Kernel.warn("warning: block supersedes default value argument")
    end

    value = __clr_fetch__(name)
    return value unless value.nil?
    return yield(name) if block_given?
    return default[0] unless default.empty?

    ::Kernel.raise(::KeyError.new("key not found: #{name.inspect}", receiver: self, key: name))
  end

  def delete(name)
    name = __env_str__(name)
    value = __clr_fetch__(name)
    if value.nil?
      return block_given? ? yield(name) : nil
    end
    __clr_store__(name, nil)
    value
  end

  def clone(freeze: nil)
    unless freeze.nil? || freeze == true || freeze == false
      ::Kernel.raise(::ArgumentError, "unexpected value for freeze: #{freeze.class}")
    end
    ::Kernel.raise(::TypeError, "Cannot clone ENV, use ENV.to_h to get a copy of ENV as a hash")
  end

  def dup
    ::Kernel.raise(::TypeError, "Cannot dup ENV, use ENV.to_h to get a copy of ENV as a hash")
  end

  # MRI hands back an Enumerator rather than complaining about the missing block,
  # and the Enumerator knows how many pairs it will walk.
  def each(&block)
    return to_enum(:each) { size } unless block
    to_hash.each(&block)
    self
  end
  alias_method :each_pair, :each

  def each_key
    return to_enum(:each_key) { size } unless block_given?
    to_hash.each_key { |k| yield k }
    self
  end

  def each_value
    return to_enum(:each_value) { size } unless block_given?
    to_hash.each_value { |v| yield v }
    self
  end

  def delete_if
    return to_enum(:delete_if) { size } unless block_given?
    to_hash.each { |k, v| __clr_store__(k, nil) if yield(k, v) }
    self
  end

  def reject!
    return to_enum(:reject!) { size } unless block_given?
    changed = false
    to_hash.each do |k, v|
      next unless yield(k, v)
      __clr_store__(k, nil)
      changed = true
    end
    changed ? self : nil
  end

  def reject(&block)
    return to_enum(:reject) { size } unless block
    to_hash.reject(&block)
  end

  def select(&block)
    return to_enum(:select) { size } unless block
    to_hash.select(&block)
  end
  alias_method :filter, :select

  alias_method :member?, :include?

  # Pairs are applied one at a time and in order, so a bad pair leaves everything
  # before it in place -- which is what MRI's own specs pin down.
  def merge!(*others)
    others.each do |other|
      other.each do |key, value|
        key = __env_str__(key)
        if block_given? && !__clr_fetch__(key).nil?
          value = yield(key, __clr_fetch__(key), value)
        end
        self[key] = value
      end
    end
    self
  end

  alias_method :update, :merge!

  # replace is all-or-nothing: everything is converted and checked before the first
  # variable is touched, so a TypeError halfway through leaves ENV untouched.
  def replace(other)
    unless other.is_a?(::Hash)
      unless other.respond_to?(:to_hash)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{other.class} into Hash")
      end
      other = other.to_hash
    end

    pairs = other.map do |key, value|
      key = __env_str__(key)
      value = __env_str__(value)
      __env_check_name__(key)
      [key, value]
    end

    clear
    pairs.each { |key, value| __clr_store__(key, value) }
    self
  end

  # The C# implementation built a CLR dictionary and threw on a duplicate value.
  def invert
    to_hash.invert
  end

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

  # MRI keeps the objects it was handed as the result keys, not their #to_str.
  def slice(*keys)
    result = {}
    keys.each do |key|
      name = __env_str__(key)
      value = __clr_fetch__(name)
      result[key] = value unless value.nil?
    end
    result
  end

  def except(*keys)
    keys = keys.map { |key| __env_str__(key) }
    to_hash.reject { |k, _| keys.include?(k) }
  end

  def assoc(key)
    key = __env_str__(key)
    value = __clr_fetch__(key)
    value.nil? ? nil : [key, value]
  end

  # rassoc and value? answer nil for an argument they cannot convert, where assoc
  # and the key-taking methods raise. That asymmetry is MRI's.
  def rassoc(value)
    return nil unless value.is_a?(::String) || value.respond_to?(:to_str)
    value = __env_str__(value)
    to_hash.each { |k, v| return [k, v] if v == value }
    nil
  end

  def value?(value)
    return nil unless value.is_a?(::String) || value.respond_to?(:to_str)
    value = __env_str__(value)
    to_hash.each_value { |v| return true if v == value }
    false
  end
  alias_method :has_value?, :value?

  def to_h(&block)
    to_hash.to_h(&block)
  end

  def key(value)
    value = __env_str__(value)
    to_hash.each { |k, v| return k if v == value }
    nil
  end

  def to_set(*args, &block)
    require 'set'
    ::Set.new(to_hash.to_a, *args, &block)
  end unless respond_to?(:to_set)
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

  def curry(*arity)
    to_proc.curry(*arity)
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
      seconds = CLOCK_RESOLUTIONS__[clock_id.to_s] || 1.0 / System::Diagnostics::Stopwatch.frequency.to_f
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

  # MRI's conversion of anything that claims to be a pid or an id, including the two
  # different TypeErrors it reports for "no #to_int" and "#to_int gave something else".
  def self.__integer_value__(value)
    return value if value.is_a?(Integer)
    unless value.respond_to?(:to_int)
      raise TypeError, "no implicit conversion of #{value.class} into Integer"
    end
    result = value.to_int
    unless result.is_a?(Integer)
      raise TypeError, "can't convert #{value.class} into Integer (#{value.class}#to_int gives #{result.class})"
    end
    result
  end

  # A user or group name is allowed wherever an id is, and is looked up the way MRI
  # looks it up.
  def self.__user_id__(value)
    return __integer_value__(value) unless value.is_a?(String)
    require "etc"
    entry = begin
              Etc.getpwnam(value)
            rescue StandardError
              nil
            end
    raise ArgumentError, "can't find user for #{value}" unless entry
    entry.uid
  end

  def self.__group_id__(value)
    return __integer_value__(value) unless value.is_a?(String)
    require "etc"
    entry = begin
              Etc.getgrnam(value)
            rescue StandardError
              nil
            end
    raise ArgumentError, "can't find group for #{value}" unless entry
    entry.gid
  end

  unless respond_to?(:detach)
    # A thread that reaps the child and whose #value is the exit status, plus the #pid
    # reader and the :pid thread-local MRI puts on it. A pid that is not ours is not an
    # error here: MRI's detach thread just ends with no status.
    def detach(pid)
      pid = __integer_value__(pid)
      thread = Thread.new(pid) do |p|
        begin
          Process.wait2(p)[1]
        rescue SystemCallError
          nil
        end
      end
      thread[:pid] = pid
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
  # Process.__spawn__ is posix_spawn(3): an executable, an argv, an environment, a
  # list of file actions and a process group, in exchange for a pid. Everything
  # above that - picking the command apart, converting and checking the arguments,
  # and turning the redirection options into file actions - is here, because it is
  # protocol work that reads far better in Ruby than in C#.
  #
  # Redirections used to be lowered onto a /bin/sh command line. That could never be
  # right: a descriptor a Ruby program hands us is close-on-exec, so `1>&7` in the
  # shell finds nothing there, and a path with a quote in it had to survive being
  # re-quoted. A file action is applied by posix_spawn in the child between fork and
  # exec, which is exactly where MRI does the same work.

  SPAWN_OPTION_KEYS = [
    :unsetenv_others, :close_others, :pgroup, :new_pgroup, :chdir, :umask,
    :in, :out, :err, :rlimit_core, :rlimit_cpu, :rlimit_fsize, :exception,
  ].freeze

  # File action opcodes, shared with the C# side.
  SPAWN_DUP2  = 0
  SPAWN_CLOSE = 1
  SPAWN_OPEN  = 2
  SPAWN_CHDIR = 3

  # open(2) flags; Linux spells them the same on every architecture we run on.
  SPAWN_O_RDONLY = 0
  SPAWN_O_WRONLY = 1
  SPAWN_O_RDWR   = 2
  SPAWN_O_CREAT  = 0o100
  SPAWN_O_TRUNC  = 0o1000
  SPAWN_O_APPEND = 0o2000

  # A one-string command goes to the shell only if it contains one of these, exactly
  # as in MRI's rb_exec_fillarg. Otherwise MRI splits the string itself and execs the
  # result, which is what makes Process.spawn("no-such-command") raise Errno::ENOENT
  # rather than quietly collect the shell's exit status 127.
  SPAWN_SHELL_META = /[*?{}\[\]<>()~&|\\$;'"`\n#]/

  # ...and the same goes for a command whose first word is a shell built-in, which
  # there is no file to exec. `system("exit 29")` has to reach a shell to mean
  # anything at all.
  SPAWN_SHELL_BUILTINS = %w[
    ! . : break case continue do done elif else esac eval exec exit export fi for
    if in readonly return set shift then times trap unset until while
  ].freeze

  def self.__check_spawn_string__(value, what)
    unless value.is_a?(String)
      unless value.respond_to?(:to_str)
        raise TypeError, "no implicit conversion of #{value.class} into String"
      end
      value = value.to_str
    end
    raise ArgumentError, "string contains null byte" if value.include?("\0")
    value
  end

  def self.__spawn_to_io__(value)
    return value if value.is_a?(IO)
    if value.respond_to?(:to_io)
      io = value.to_io
      return io if io.is_a?(IO)
    end
    nil
  end

  # The descriptors a redirection key names. MRI lets the key be a symbol, a
  # descriptor, an IO, or an array of any of those so that one value redirects
  # several descriptors at once.
  def self.__spawn_key_fds__(key)
    case key
    when :in  then [0]
    when :out then [1]
    when :err then [2]
    when Integer then [key]
    when Array then key.map { |part| __spawn_key_fds__(part) }.flatten
    else
      io = __spawn_to_io__(key)
      raise ArgumentError, "wrong exec redirect: #{key.inspect}" if io.nil?
      [io.fileno]
    end
  end

  def self.__spawn_redirect_key?(key)
    case key
    when :in, :out, :err, Integer, Array then true
    else !__spawn_to_io__(key).nil?
    end
  end

  def self.__spawn_open_flags__(mode, fd)
    return mode if mode.is_a?(Integer)
    if mode.nil?
      return fd == 0 ? SPAWN_O_RDONLY : (SPAWN_O_WRONLY | SPAWN_O_CREAT | SPAWN_O_TRUNC)
    end
    mode = mode.to_str if !mode.is_a?(String) && mode.respond_to?(:to_str)
    text = mode.to_s.sub(/:.*\z/, "")
    plus = text.include?("+")
    case text[0]
    when "a" then (plus ? SPAWN_O_RDWR : SPAWN_O_WRONLY) | SPAWN_O_CREAT | SPAWN_O_APPEND
    when "w" then (plus ? SPAWN_O_RDWR : SPAWN_O_WRONLY) | SPAWN_O_CREAT | SPAWN_O_TRUNC
    else          (plus ? SPAWN_O_RDWR : SPAWN_O_RDONLY)
    end
  end

  # Returns the file actions for one redirection. [:child, fd] cannot be resolved
  # here - it names a descriptor of the child as it will be *after* the other
  # redirections - so it is handed back separately to be appended last.
  def self.__spawn_redirect__(fd, value, deferred)
    case value
    when :close
      [[SPAWN_CLOSE, fd]]
    when Integer
      [[SPAWN_DUP2, fd, __spawn_native_fd__(value)]]
    when String, Symbol
      path = __check_spawn_string__(value.to_s, "path name")
      [[SPAWN_OPEN, fd, path, __spawn_open_flags__(nil, fd), 0o644]]
    when Array
      if value[0] == :child
        deferred << [SPAWN_DUP2, fd, __spawn_key_fds__(value[1]).first]
        []
      else
        path = value[0]
        path = path.to_path if !path.is_a?(String) && path.respond_to?(:to_path)
        path = __check_spawn_string__(path, "path name")
        [[SPAWN_OPEN, fd, path, __spawn_open_flags__(value[1], fd), value[2] || 0o644]]
      end
    else
      io = __spawn_to_io__(value)
      raise ArgumentError, "wrong exec redirect: #{value.inspect}" if io.nil?
      [[SPAWN_DUP2, fd, __spawn_native_fd__(io.fileno)]]
    end
  end

  def self.__spawn_native_fd__(fd)
    native = __native_fd__(fd)
    raise ArgumentError, "wrong exec redirect: #{fd.inspect}" if native < 0
    native
  end

  def self.__spawn_actions__(options)
    actions = []
    deferred = []

    if (dir = options[:chdir])
      dir = dir.to_path if !dir.is_a?(String) && dir.respond_to?(:to_path)
      dir = __check_spawn_string__(dir, "path name")
      raise Errno::ENOENT, dir unless File.directory?(dir)
      actions << [SPAWN_CHDIR, dir]
    end

    options.each do |key, value|
      next unless __spawn_redirect_key?(key)
      first = nil
      __spawn_key_fds__(key).each do |fd|
        if first.nil?
          actions.concat(__spawn_redirect__(fd, value, deferred))
          first = fd
        else
          # [:out, :err] => "file" opens the file once and points both descriptors at
          # it; opening it twice would give them separate offsets, so whichever wrote
          # second would overwrite the other.
          actions << [SPAWN_DUP2, fd, first]
        end
      end
    end

    actions.concat(deferred)
    actions
  end

  def self.__spawn_env__(env, unset_others)
    result = unset_others ? {} : ENV.to_hash
    if env
      env.each do |key, value|
        value.nil? ? result.delete(key) : result[key] = value
      end
    end
    result.map { |key, value| "#{key}=#{value}" }
  end

  def self.__resolve_executable__(name, search_path = nil)
    raise Errno::ENOENT, name if name.empty?

    named = name.include?(File::SEPARATOR) || (File::ALT_SEPARATOR && name.include?(File::ALT_SEPARATOR))
    if named
      candidates = [name]
    else
      path = (search_path || ENV["PATH"]).to_s
      candidates = path.split(File::PATH_SEPARATOR).map { |dir| File.join(dir.empty? ? "." : dir, name) }
    end

    candidates.each do |candidate|
      next unless File.exist?(candidate)
      # A file that cannot be run is a reason to refuse only when the caller named it. A
      # PATH search that turns one up simply keeps looking, and ends in ENOENT if nothing
      # runnable is there - naming the command, not the last unusable file that matched.
      unless File.executable?(candidate) && !File.directory?(candidate)
        next unless named
        raise Errno::EACCES, candidate
      end
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
    # The options hash is only recognised behind a command, except that an env hash has
    # already been taken off the front - so `spawn({}, {})` is env plus options and no
    # command, not env plus a command that happens to be a Hash.
    if args.size > 1 || (env && args.size == 1)
      if !args.empty? && args.last.respond_to?(:to_hash) && !args.last.is_a?(String)
        options = args.pop.to_hash
      end
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
      next if __spawn_redirect_key?(key)
      raise ArgumentError, "wrong exec option" if key.is_a?(String)
      unless SPAWN_OPTION_KEYS.include?(key)
        raise ArgumentError, "wrong exec option symbol: #{key}"
      end
    end

    [env, args, options]
  end

  # [executable, argv] for the command part of a spawn call.
  def self.__spawn_command__(args, env)
    search_path = env && env["PATH"]
    first = args.first
    array_form = first.is_a?(Array) || (!first.is_a?(String) && first.respond_to?(:to_ary))

    if args.size == 1 && !array_form
      command = __check_spawn_string__(first, "command")
      if command =~ SPAWN_SHELL_META
        return ["/bin/sh", ["sh", "-c", command]]
      end
      words = command.split(" ")
      raise Errno::ENOENT, command if words.empty?
      if SPAWN_SHELL_BUILTINS.include?(words.first)
        return ["/bin/sh", ["sh", "-c", command]]
      end
      return [__resolve_executable__(words.first, search_path), words]
    end

    if array_form
      pair = first.to_ary
      raise ArgumentError, "wrong first argument" unless pair.size == 2
      name  = __check_spawn_string__(pair[0], "command")
      argv0 = __check_spawn_string__(pair[1], "command")
    else
      name = __check_spawn_string__(first, "command")
      argv0 = name
    end

    rest = args[1..-1].map { |a| __check_spawn_string__(a, "string") }
    [__resolve_executable__(name, search_path), [argv0] + rest]
  end

  # Everything posix_spawn needs, plus the options the caller still has to act on.
  def self.__spawn_setup__(args)
    env, command, options = __parse_spawn_args__(args)

    [:unsetenv_others, :close_others, :new_pgroup].each do |key|
      next unless options.key?(key)
      value = options[key]
      unless value.nil? || value == true || value == false
        raise ArgumentError, "expected true or false as #{key}: #{value}"
      end
    end

    pgroup = nil
    if options.key?(:pgroup)
      value = options[:pgroup]
      case value
      when true
        pgroup = 0
      when false, nil
        pgroup = nil
      else
        unless value.is_a?(Integer)
          unless value.respond_to?(:to_int) && !value.is_a?(Symbol)
            raise TypeError, "no implicit conversion of #{value.class} into Integer"
          end
          value = value.to_int
        end
        raise ArgumentError, "negative process group ID : #{value}" if value < 0
        pgroup = value
      end
    end

    file, argv = __spawn_command__(command, env)
    [file, argv, __spawn_env__(env, options[:unsetenv_others]), __spawn_actions__(options), pgroup, options]
  end

  # :umask has no posix_spawn equivalent, and the value is inherited, so set it here
  # and put it back afterwards.
  def self.__with_umask__(mask)
    return yield if mask.nil?
    previous = File.umask(mask.to_int)
    begin
      yield
    ensure
      File.umask(previous)
    end
  end

  def self.spawn(*args)
    file, argv, envp, actions, pgroup, options = __spawn_setup__(args)
    __with_umask__(options[:umask]) do
      __check__(__spawn__(file, argv, envp, actions, pgroup, options[:close_others] ? true : false), file)
    end
  rescue SystemCallError
    # MRI forks first and only then discovers that the command cannot be run, so the child
    # it already has exits with 127 and $? says so even though spawn itself raises.
    __set_last_status__(__make_status__(-1, 127 << 8))
    raise
  end

  # Process.exec really does replace this process: execve(2) only returns on failure.
  # Anything Ruby still has buffered would be lost with the address space, so it is
  # flushed first.
  def self.exec(*args)
    # MRI takes close-on-exec off every descriptor the options name before it so much as
    # looks at the command, so that one named in a redirection is still usable if the exec
    # turns out to be impossible.
    args.each do |argument|
      next if argument.is_a?(String) || argument.is_a?(Array) || !argument.respond_to?(:to_hash)
      argument.to_hash.each_key do |key|
        next unless __spawn_redirect_key?(key)
        __spawn_key_fds__(key).each { |fd| __set_cloexec__(fd, false) }
      end
    end

    file, argv, envp, actions, _pgroup, options = __spawn_setup__(args)
    File.umask(options[:umask].to_int) if options[:umask]
    [$stdout, $stderr].each do |io|
      begin
        io.flush
      rescue StandardError
      end
    end
    __check__(__exec__(file, argv, envp, actions), file)
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
    if result.is_a?(Integer) && result < 0
      klass = __errno_class__(-result)
      # `raise klass, nil` is not the same call as `raise klass`: the two-argument form
      # goes looking for Exception.exception(message) and the runtime cannot pick an
      # overload for a nil argument.
      raise klass, message if message
      raise klass
    end
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
    # Anything else is asked for #to_str first and only then for #to_int, which is the order
    # MRI's rlimit_resource_type uses.
    def __rlimit_resource__(resource)
      case resource
      when Integer then resource
      when Symbol, String then __rlimit_by_name__(resource.to_s, resource)
      else
        if resource.respond_to?(:to_str)
          name = resource.to_str
          return __rlimit_by_name__(name, resource) if name.is_a?(String)
        end
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

    def __rlimit_by_name__(name, original)
      name = "RLIMIT_#{name}" unless name.start_with?("RLIMIT_")
      unless const_defined?(name)
        raise ArgumentError, "invalid resource name: #{original}"
      end
      const_get(name)
    end
    module_function :__rlimit_by_name__

    # rlim_t is unsigned, so RLIM_INFINITY is 2**64-1 and getrlimit hands it back that way.
    # __setrlimit__ takes it as a signed long and reinterprets the bits, so anything above
    # Integer::MAX has to be folded into the negative half first or the conversion overflows.
    def __rlimit_value__(value)
      unless value.is_a?(Integer)
        unless value.respond_to?(:to_int)
          raise TypeError, "no implicit conversion of #{value.class} into Integer"
        end
        value = value.to_int
        unless value.is_a?(Integer)
          raise TypeError, "can't convert to Integer"
        end
      end
      # Named inline rather than as constants: Process.constants is part of the API and
      # ruby/spec walks everything matching /\ARLIMIT_/ through getrlimit.
      value > 0x7fff_ffff_ffff_ffff ? value - 0x1_0000_0000_0000_0000 : value
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

  unless respond_to?(:getpgrp)
    def getpgrp
      __check__(__getpgid__(0))
    end
    module_function :getpgrp

    def setpgrp
      __check__(__setpgid__(0, 0))
      0
    end
    module_function :setpgrp
  end

  # The real and effective ids. MRI takes a name as well as a number for the ones that
  # name a user or a group, and the C# side hands back -errno so that EPERM comes out as
  # Errno::EPERM rather than as a bare failure.
  def self.uid=(value)
    __check__(__setuid__(__user_id__(value)))
    value
  end

  def self.euid=(value)
    __check__(__seteuid__(__user_id__(value)))
    value
  end

  def self.gid=(value)
    __check__(__setgid__(__group_id__(value)))
    value
  end

  def self.egid=(value)
    __check__(__setegid__(__group_id__(value)))
    value
  end

  def self.groups=(list)
    gids = list.to_ary.map { |gid| __group_id__(gid) }
    __check__(__setgroups__(gids))
    list
  end

  def self.initgroups(user, group)
    __check__(__initgroups__(user.to_s, __group_id__(group)))
    groups
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

  # waitpid(2) itself, so that a child that was killed is reported as killed rather
  # than as having exited with 128 plus the signal number.
  def self.__wait2__(pid, flags)
    result = __waitpid__(__integer_value__(pid), __integer_value__(flags))
    return nil if result.nil?
    __check__(result)
    result
  end

  def self.wait(pid = -1, flags = 0)
    result = __wait2__(pid, flags)
    result && result[0]
  end

  def self.wait2(pid = -1, flags = 0)
    __wait2__(pid, flags)
  end

  # Not four methods that call one another: MRI defines two and aliases them, and a spec
  # compares Process.method(:waitpid) with Process.method(:wait).
  class << self
    alias_method :waitpid, :wait
    alias_method :waitpid2, :wait2
  end

  def self.waitall
    results = []
    loop do
      begin
        pair = __wait2__(-1, 0)
      rescue SystemCallError
        break
      end
      break if pair.nil?
      results << pair
    end
    results
  end

  # Daemonising means forking, and there is no fork on the CLR. MRI defines the method
  # on platforms that cannot do it too, as the function that raises NotImplementedError,
  # and that function is exactly the case respond_to? answers false for - so portable
  # code asks whether the feature is there rather than whether the name is.
  NOT_IMPLEMENTED_METHODS = [:daemon, :fork, :_fork].freeze unless const_defined?(:NOT_IMPLEMENTED_METHODS)

  unless respond_to?(:daemon)
    def daemon(nochdir = nil, noclose = nil)
      raise NotImplementedError, "daemon() function is unimplemented on this machine"
    end
    module_function :daemon
  end

  # Process.fork and Process._fork exist for the same reason daemon does: MRI defines
  # them everywhere and lets respond_to? be the portable answer. Kernel#fork is private
  # and so was never reachable as Process.fork.
  unless singleton_class.method_defined?(:fork)
    def fork(&block)
      raise NotImplementedError, "fork() function is unimplemented on this machine"
    end
    module_function :fork

    def _fork
      raise NotImplementedError, "fork() function is unimplemented on this machine"
    end
    module_function :_fork
  end

  # The resolution of the clock we would actually read, per clock. The two named after
  # the calls MRI emulates them with have the resolution of those calls, whatever the
  # platform timer manages.
  CLOCK_RESOLUTIONS__ = {
    "GETTIMEOFDAY_BASED_CLOCK_REALTIME" => 1.0e-6,
    "TIME_BASED_CLOCK_REALTIME" => 1.0,
    "GETRUSAGE_BASED_CLOCK_PROCESS_CPUTIME_ID" => 1.0e-6,
    "CLOCK_BASED_CLOCK_PROCESS_CPUTIME_ID" => 1.0e-6,
    "TIMES_BASED_CLOCK_PROCESS_CPUTIME_ID" => 1.0e-2,
    "TIMES_BASED_CLOCK_MONOTONIC" => 1.0e-2,
    "MACH_ABSOLUTE_TIME_BASED_CLOCK_MONOTONIC" => 1.0e-9,
  }.freeze unless const_defined?(:CLOCK_RESOLUTIONS__)

  def self.respond_to?(name, include_all = false)
    return false if NOT_IMPLEMENTED_METHODS.include?(name.to_sym)
    Module.instance_method(:respond_to?).bind(self).call(name, include_all)
  end
end

class Process::Status
  # Process::Status.wait is Process.wait2 without the side effect on $?; it is what a
  # library uses when it must not disturb the status the program is looking at. Having
  # no children at all is not an error here - it answers with a status whose pid is -1.
  def self.wait(pid = -1, flags = 0)
    previous = $?
    begin
      pair = Process.waitpid2(pid, flags)
      pair && pair[1]
    rescue Errno::ECHILD
      Process.__make_status__(-1, 0)
    ensure
      Process.__set_last_status__(previous)
    end
  end unless respond_to?(:wait)
end

module Kernel
  # system, exec, spawn and the backquotes are all Process.spawn underneath; they
  # differ only in what they do with the child once it is running. Doing the argument
  # handling once, in Process, is what gives all four the env hash, the
  # [command, argv0] form and the options hash - the implementations they replace
  # understood only "a string, optionally followed by more strings", so every one of
  # them raised or silently mis-ran anything else.

  def spawn(*args)
    Process.spawn(*args)
  end
  module_function :spawn

  def exec(*args)
    Process.exec(*args)
  end
  module_function :exec

  def system(*args)
    options = (args.size > 1 && args.last.respond_to?(:to_hash) && !args.last.is_a?(String)) ? args.last.to_hash : {}
    exception = options[:exception]

    begin
      pid = Process.spawn(*args)
    rescue SystemCallError
      # MRI reports "could not even start it" as nil rather than as false.
      raise if exception
      return nil
    end

    Process.waitpid(pid)
    status = $?
    return true if status.success?
    if exception
      if status.exitstatus
        raise RuntimeError, "Command failed with status (#{status.exitstatus}): #{args.first}"
      else
        raise RuntimeError, "Command failed with signal #{status.termsig}: #{args.first}"
      end
    end
    false
  end
  module_function :system

  def `(command)
    file, argv, envp, actions, = Process.__spawn_setup__([command])
    Process.__backquote__(file, argv, envp, actions)
  end
  module_function :`
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
      when ::Range then __rand_in_range__(limit)
      when ::Float
        unless limit > 0
          ::Kernel.raise(::ArgumentError, "invalid argument - #{limit}")
        end
        @native.next_double * limit
      else
        n = limit.to_int
        unless n > 0
          ::Kernel.raise(::ArgumentError, "invalid argument - #{limit}")
        end
        __rand_below__(n)
      end
    end

    # An Integer uniformly in 0...n. System::Random.next only covers Int32, so a
    # wider bound is filled from random bytes and rejection-sampled.
    def __rand_below__(n)
      return @native.next(n) if n <= 2147483647

      bits = n.bit_length
      bytes = (bits + 7) / 8
      buffer = System::Array[System::Byte].new(bytes)
      loop do
        @native.next_bytes(buffer)
        value = 0
        buffer.to_a.each { |b| value = (value << 8) | b }
        value >>= (bytes * 8 - bits)
        return value if value < n
      end
    end
    private :__rand_below__

    # MRI drives this entirely off `end - begin`, which is what makes it work for
    # Time and for any object that answers #-, #+ and #to_int or #to_f. nil comes
    # back for a range that cannot produce a value.
    def __rand_in_range__(range)
      first = range.begin
      last = range.end
      return nil if first.nil? || last.nil?

      span = last - first
      if !span.is_a?(::Float) && span.respond_to?(:to_int)
        n = span.to_int
        n += 1 unless range.exclude_end?
        return nil if n <= 0
        first + __rand_below__(n)
      else
        width = span.to_f
        return nil if width < 0 || (width == 0 && range.exclude_end?)
        first + @native.next_double * width
      end
    end
    private :__rand_in_range__

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

module Kernel
  # Kernel#rand is more forgiving than Random#rand: it takes a Range (1.9.3), it
  # ignores the sign of a numeric limit, and a limit that truncates to 0 -- which
  # includes every Float below 1 -- means "give me a Float" rather than an error.
  # The builtin only ever understood a positive Integer.
  unless private_method_defined?(:__ir_rand__)
    alias_method :__ir_rand__, :rand
    private :__ir_rand__

    def rand(limit = nil)
      return __ir_rand__ if limit.nil?
      return ::Random.rand(limit) if limit.is_a?(::Range)

      unless limit.respond_to?(:to_int)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{limit.class} into Integer")
      end
      n = limit.to_int.abs
      n == 0 ? __ir_rand__ : __ir_rand__(n)
    end
    module_function :rand
  end
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
    return obj if obj.is_a?(::String)
    return nil unless obj.respond_to?(:to_str)
    converted = obj.to_str
    return converted if converted.nil? || converted.is_a?(::String)
    ::Kernel.raise(::TypeError,
      "can't convert #{obj.class} to String (#{obj.class}#to_str gives #{converted.class})")
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
    converted = __transcode__(result)
    return converted unless args.size >= 2 && args[1].equal?(result) && !converted.equal?(result)
    # A caller-supplied buffer is filled in place, so the transcoded text has to
    # go back into it rather than being handed back beside it.
    result.force_encoding(converted.encoding)
    result.replace(converted)
    result
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

  # The conversion options the stream was opened with, reduced to the ones
  # String#encode understands. The hash also carries :mode, :binmode and the
  # rest of IO.open's own options, which #encode would reject.
  DECORATOR_OPTIONS__ = [:universal_newline, :cr_newline, :crlf_newline, :newline].freeze
  ENCODE_OPTIONS__ = ([:invalid, :undef, :replace, :fallback, :xml] + DECORATOR_OPTIONS__).freeze

  def __encode_options__
    given = (__conversion_options__ rescue nil)
    return {} unless given.is_a?(::Hash)
    options = {}
    ENCODE_OPTIONS__.each { |key| options[key] = given[key] if given.key?(key) }
    # Binary mode is the absence of conversion, and that includes the newline
    # decorators the stream was opened with.
    DECORATOR_OPTIONS__.each { |key| options.delete(key) } if binmode?
    options
  end
  private :__encode_options__

  # The newline decorators work on the bytes, not on the characters: MRI runs
  # them beside the conversion rather than through it, so they apply to text
  # whose bytes are not valid characters of its own encoding. They are a
  # conversion in their own right, so they happen even when the two encodings
  # are the same; and :universal_newline is a decorator for the way in only.
  def __decorate_newlines__(text, options, reading)
    newline = options[:newline]
    if reading && (options[:universal_newline] || newline == :universal || newline == :lf)
      return text.gsub("\r\n", "\n").gsub("\r", "\n")
    end
    return text.gsub("\n", "\r\n") if options[:crlf_newline] || newline == :crlf
    return text.gsub("\n", "\r") if options[:cr_newline] || newline == :cr
    text
  end
  private :__decorate_newlines__

  def __decorated__(options, reading)
    newline = options[:newline]
    return true if options[:crlf_newline] || options[:cr_newline] || newline == :crlf || newline == :cr
    reading && (options[:universal_newline] || newline == :universal || newline == :lf) ? true : false
  end
  private :__decorated__

  # A stream opened "r:external:internal" is asking for the bytes to be read as
  # the external encoding and handed back as the internal one. #internal_encoding
  # already answers nil unless there is really a conversion to do - the external
  # side binary, or the two the same, both mean no - so its answer is the whole
  # condition here, once the decorators have had their turn.
  def __transcode__(text)
    return text if text.nil?
    return text unless text.respond_to?(:encode)
    options = __encode_options__
    text = __decorate_newlines__(text, options, true) if __decorated__(options, true)
    target = internal_encoding
    return text if target.nil?
    text.encode(target, **options.reject { |key, _| DECORATOR_OPTIONS__.include?(key) })
  end
  private :__transcode__

  # The mirror of __transcode__ for the way out. A stream opened "w:external"
  # is asking for what is written to it to be converted into that encoding, and
  # nothing did it, so the bytes of whatever string was handed over went
  # straight to the file whatever its encoding said.
  #
  # Only a stream that was actually told an external encoding converts: MRI
  # leaves an ordinary stream alone even when Encoding.default_external would
  # have something to say, which is what keeps a binary string writable to
  # $stdout.  A byte-string destination converts nothing either, and text that
  # is already in the target encoding, or is ASCII and going somewhere that
  # keeps ASCII where it is, needs no work.
  def __encode_for_write__(text)
    return text unless ::String === text
    options = __encode_options__
    text = __decorate_newlines__(text, options, false) if __decorated__(options, false)
    target = external_encoding
    return text if target.nil? || target == ::Encoding::BINARY || text.encoding == target
    return text if text.ascii_only? && target.ascii_compatible?
    text.encode(target, **options.reject { |key, _| DECORATOR_OPTIONS__.include?(key) })
  end
  private :__encode_for_write__

  alias_method :__ir_write__, :write

  def write(*args)
    total = 0
    args.each do |arg|
      # Anything that is not already a String is left to the built-in write to
      # convert, which is the only thing that knows how MRI complains about an
      # object that cannot become one. String === arg rather than
      # arg.is_a?(String) because the argument may be a BasicObject, which has
      # no #is_a? to call.
      total += __ir_write__(::String === arg ? __encode_for_write__(arg) : arg)
    end
    total
  end

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

  # Real FD_CLOEXEC, not a remembered flag: whether a child sees the descriptor is the
  # kernel's business, and a spawned child was the only thing that ever asked.
  def close_on_exec=(value)
    flag = value ? true : false
    Process.__set_cloexec__(fileno, flag)
    @__close_on_exec__ = flag
    value
  end

  def close_on_exec?
    actual = Process.__get_cloexec__(fileno)
    return actual unless actual.nil?
    defined?(@__close_on_exec__) && !@__close_on_exec__ ? false : true
  end

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

class Thread
  # Validates the mask and runs the block, but does NOT defer anything: masking an
  # asynchronous interrupt and delivering it later needs Thread.Abort, which .NET Core
  # does not have. Code that only uses handle_interrupt to scope a section behaves
  # correctly; code that depends on a Thread#raise actually being held back does not.
  #
  # It has to exist even so. Without it the method call raises NoMethodError inside a
  # worker thread, and anything waiting on that thread to reach a queue - which is how
  # the thread specs synchronise - blocks forever. One missing method deadlocked the
  # whole of spec/core/thread.
  def self.handle_interrupt(mask)
    raise ArgumentError, "block is needed." unless block_given?

    mask.each_value do |timing|
      unless [:immediate, :on_blocking, :never].include?(timing)
        raise ArgumentError, "unknown mask signature"
      end
    end

    yield
  end unless respond_to?(:handle_interrupt)
end

# caller_locations (2.0) and the Location objects it yields. The runtime only
# offers caller strings, so parse those: "path:lineno:in `label'".
class Thread
  # A thread's own stack is reachable through Kernel#caller.  Another thread's
  # comes from __native_backtrace__, which reads the frame list that thread
  # already keeps for its own backtraces instead of trying to walk its stack
  # from outside - which .NET Core does not allow.  A thread that has not
  # started, has finished, or has never run Ruby code still answers nil.
  def backtrace(*args)
    frames = (self == ::Thread.current) ? ::Kernel.send(:caller, 1) : __native_backtrace__
    return nil if frames.nil?
    __slice_stack__(frames, args)
  end unless method_defined?(:backtrace)

  def backtrace_locations(*args)
    if self == ::Thread.current
      frames = ::Kernel.send(:caller_locations, 1)
    else
      entries = __native_backtrace__
      return nil if entries.nil?
      frames = entries.map { |e| ::Thread::Backtrace::Location.__parse__(e) }
    end
    __slice_stack__(frames, args)
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

      # "path:lineno:in `label'" -> a Location.  Both Kernel#caller_locations and
      # Thread#backtrace_locations have only the string form to work from.
      def self.__parse__(entry)
        if (m = /\A(.*):(\d+)(?::in [`'](.*)')?\z/.match(entry))
          new(m[1], m[2].to_i, m[3])
        else
          new(entry, 0, nil)
        end
      end

      def to_s
        @label ? "#{@path}:#{@lineno}:in `#{@label}'" : "#{@path}:#{@lineno}"
      end

      def inspect; to_s.inspect; end
    end unless const_defined?(:Location)
  end unless const_defined?(:Backtrace)
end

module Kernel
  private

  # Takes everything Kernel#caller takes, shifted by one frame so that the count starts
  # at the caller rather than here. A negative range end is left alone: caller resolves
  # it against the real stack depth, which this frame is not part of.
  def caller_locations(start = 1, length = nil)
    if start.is_a?(Range)
      first = start.begin ? start.begin.to_int : 0
      last = start.end&.to_int
      raise ArgumentError, "negative level (#{first})" if first < 0
      entries = caller(Range.new(first + 1, (last && last >= 0) ? last + 1 : last, start.exclude_end?))
    else
      first = start.to_int
      raise ArgumentError, "negative level (#{first})" if first < 0
      if length.nil?
        entries = caller(first + 1)
      else
        length = length.to_int
        raise ArgumentError, "negative size (#{length})" if length < 0
        entries = caller(first + 1, length)
      end
    end

    return nil if entries.nil?
    entries.map { |entry| Thread::Backtrace::Location.__parse__(entry) }
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

    # Since 3.2 a struct built without keyword_init: takes keywords as well as
    # positional values. Keywords reach this method as a trailing Hash, so the
    # only thing that distinguishes them from a Hash meant as a member value is
    # that every key names a member.
    if keyword_init.nil? && args.size == 1 && args[0].is_a?(Hash) &&
       !args[0].empty? && (args[0].keys - names).empty?
      keyword_init = true
    end

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
    if se.nil?
      # Two ranges that both run to infinity end at the same place; only the
      # exclusive/inclusive disagreement below can still separate them.
      cmp = oe.nil? ? 0 : 1
    else
      cmp = (se <=> oe)
      return false if cmp.nil?
    end
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

  # Range#include? is not Range#cover?. For a range of numbers or times - or
  # when the value asked about is itself a number - the two agree and the
  # comparison is enough. A range of Strings answers whether the value turns up
  # in the #succ walk from begin to end, which is why ("a".."z") does not
  # include "cc". Anything else has to be walked, and a range with no beginning
  # or no end cannot be walked at all.
  def include?(value)
    b = self.begin
    e = self.end
    if __linear__(value) || __linear__(b) || __linear__(e) ||
       __integerish__(b) || __integerish__(e)
      return cover?(value)
    end
    return __string_include__(b, e, value) if b.is_a?(::String) && e.is_a?(::String)
    if b.nil? || e.nil?
      ::Kernel.raise(::TypeError, "cannot determine inclusion in beginless/endless ranges")
    end
    each { |x| return true if x == value }
    false
  end
  alias_method :member?, :include?

  def __linear__(x)
    x.is_a?(::Numeric) || x.is_a?(::Time)
  end
  private :__linear__

  # MRI's rb_check_to_integer: an endpoint that converts to an Integer is
  # treated as a point on a line even if its class is something else entirely.
  def __integerish__(x)
    return false unless x.respond_to?(:to_int)
    (x.to_int rescue nil).is_a?(::Integer)
  end
  private :__integerish__

  # MRI's rb_str_include_range_p: single ASCII characters compare directly,
  # everything else walks #succ and looks for an equal string.
  def __string_include__(b, e, value)
    unless value.is_a?(::String)
      return false unless value.respond_to?(:to_str)
      value = value.to_str
      unless value.is_a?(::String)
        ::Kernel.raise(::TypeError, "can't convert #{value.class} to String")
      end
    end
    if b.bytesize == 1 && e.bytesize == 1 && b.ascii_only? && e.ascii_only? && value.ascii_only?
      return false unless value.bytesize == 1
      return true if b <= value && value < e
      return !exclude_end? && value == e
    end
    found = false
    b.upto(e) do |s|
      next if exclude_end? && s == e
      if s == value
        found = true
        break
      end
    end
    found
  end
  private :__string_include__

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
    b = self.begin
    e = self.end
    integral = (b.nil? || b.is_a?(::Integer)) && (e.nil? || e.is_a?(::Integer))
    numeric = (b.nil? || b.is_a?(::Numeric)) && (e.nil? || e.is_a?(::Numeric))
    unless numeric
      # MRI refuses before it would hand back an enumerator.
      ::Kernel.raise(::TypeError, "can't do binary search for #{(b || e).class}")
    end
    return ::Enumerator.new { |y| each { |x| y << x } } unless block
    if integral
      respond_to?(:__ir_bsearch__, true) ? __ir_bsearch__(&block) : __bsearch_int__(block)
    else
      __bsearch_float__(block)
    end
  end

  # Answers :found, true (go left, remember) or false (go right). MRI accepts any
  # Numeric from the block, not just an Integer, so a block that answers a Float
  # difference - or +/-Float::INFINITY - steers the search rather than raising.
  def __bsearch_test__(block, value)
    r = block.call(value)
    case r
    when true then :satisfied
    when false, nil then :greater
    when ::Integer then r == 0 ? :found : (r < 0 ? :smaller : :greater)
    when ::Numeric
      c = (r <=> 0)
      ::Kernel.raise(::ArgumentError, "comparison of #{r.class} with 0 failed") if c.nil?
      c == 0 ? :found : (c < 0 ? :smaller : :greater)
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
        while __bsearch_test__(block, high) == :greater
          span *= 2
          high = low + span
        end
      else
        low = high - span
        while __bsearch_test__(block, low) != :greater
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
      when :satisfied then result = mid; high = mid - 1
      when :smaller then high = mid - 1
      else low = mid + 1
      end
    end
    result
  end
  private :__bsearch_int__

  # MRI bisects a Float range over the IEEE bit patterns of the doubles rather
  # than over the interval, so the search lands exactly on a representable value
  # instead of converging near it - the difference between answering -0.2 and
  # answering -0.19999999999999998 - and reaches infinity in 64 steps.
  DOUBLE_MIN_INT64 = -9223372036854775808
  private_constant :DOUBLE_MIN_INT64 rescue nil

  def __double_as_int64__(d)
    i = ::System::BitConverter.DoubleToInt64Bits(d)
    i < 0 ? (-9223372036854775808 - i) : i
  end
  private :__double_as_int64__

  def __int64_as_double__(i)
    i = -9223372036854775808 - i if i < 0
    ::System::BitConverter.Int64BitsToDouble(i)
  end
  private :__int64_as_double__

  def __bsearch_float__(block)
    b = self.begin
    e = self.end
    low = __double_as_int64__(b.nil? ? -::Float::INFINITY : b.to_f)
    high = __double_as_int64__(e.nil? ? ::Float::INFINITY : e.to_f)
    high += 1 unless exclude_end?
    satisfied = nil
    while low < high
      mid = if (high < 0) == (low < 0)
              low + ((high - low) / 2)
            elsif low < -high
              -((-1 - low - high) / 2 + 1)
            else
              (low + high) / 2
            end
      value = __int64_as_double__(mid)
      case __bsearch_test__(block, value)
      when :found then return value
      when :satisfied then satisfied = value; high = mid
      when :smaller then high = mid
      else low = mid + 1
      end
    end
    # find-minimum mode answers the smallest element the block accepted; find-any
    # mode has already returned, so reaching here means it never found its zero.
    satisfied
  end
  private :__bsearch_float__

  # Range#step as MRI 3.4 rewrote it. A numeric range steps arithmetically,
  # sharing ruby_float_step with Numeric#step so that a Float range lands on
  # its end point rather than drifting; a range whose elements have #succ still
  # walks them when the step is an Integer, which is what keeps ("A".."G")
  # .step(2) answering letters; and anything else - a Time, a String step, an
  # object that only knows #+ and #<=> - advances by asking the current element
  # for `element + step`. A beginless range has nowhere to start.
  #
  # The old version just handed every stepping job to the 1.9 built-in, which
  # knows only #succ, so a Float step, a negative step, a String step and a
  # step given as an object answering #coerce were all wrong or raised.
  def step(n = nil, &block)
    b = self.begin
    e = self.end
    unit = n.nil? ? 1 : n
    numeric = b.is_a?(::Numeric) && (e.nil? || e.is_a?(::Numeric)) && unit.is_a?(::Numeric)

    if b.nil?
      unless e.is_a?(::Numeric) && unit.is_a?(::Numeric)
        ::Kernel.raise(::ArgumentError, "#step for non-numeric beginless ranges is meaningless")
      end
      ::Kernel.raise(::ArgumentError, "step can't be 0") if unit == 0
      if block
        ::Kernel.raise(::ArgumentError, "#step iteration for beginless ranges is meaningless")
      end
      return ::Enumerator::ArithmeticSequence.__build__(b, e, unit, exclude_end?, self)
    end

    ::Kernel.raise(::ArgumentError, "step can't be 0") if numeric && unit == 0

    unless block
      return ::Enumerator::ArithmeticSequence.__build__(b, e, unit, exclude_end?, self) if numeric
      range = self
      return ::Enumerator.new { |y| range.step(n) { |x| y << x } }
    end

    if numeric
      ::Enumerator::ArithmeticSequence.__step_each__(b, e, unit, exclude_end?, &block)
    elsif unit.is_a?(::Integer) && b.respond_to?(:succ)
      if unit > 0
        i = 0
        each do |x|
          block.call(x) if i % unit == 0
          i += 1
        end
      end
    else
      __step_by_plus__(b, e, unit, &block)
    end
    self
  end

  def %(n)
    step(n)
  end

  # The generic walk: decide which way the range runs, check that adding the
  # step moves that way, then advance with #+ until #<=> says the end has been
  # passed. A step that does not move, or moves against the range, yields
  # nothing instead of looping forever.
  def __step_by_plus__(b, e, unit, &block)
    if e.nil?
      v = b
      loop do
        block.call(v)
        v = v + unit
      end
      return
    end
    dir = (b <=> e)
    return if dir.nil?
    c = (b <=> e)
    return if c.nil? || __step_past_end__(c, dir)
    sdir = (b <=> (b + unit))
    return if sdir.nil? || sdir == 0
    return unless dir == 0 || (dir < 0) == (sdir < 0)
    v = b
    loop do
      block.call(v)
      break if c == 0
      v = v + unit
      c = (v <=> e)
      break if c.nil? || __step_past_end__(c, dir)
    end
  end
  private :__step_by_plus__

  def __step_past_end__(c, dir)
    if dir < 0
      exclude_end? ? c >= 0 : c > 0
    elsif dir > 0
      exclude_end? ? c <= 0 : c < 0
    else
      exclude_end?
    end
  end
  private :__step_past_end__

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

# --- a batch of small post-1.9 additions ------------------------------------

module Kernel
  # Kernel#Hash (1.9). Only nil and [] are special-cased; everything else must
  # answer #to_hash with a Hash.
  def Hash(object)
    return {} if object.nil? || object == []
    return object if object.instance_of?(::Hash)
    unless object.respond_to?(:to_hash)
      ::Kernel.raise(::TypeError, "can't convert #{object.class} into Hash")
    end
    converted = object.to_hash
    unless converted.is_a?(::Hash)
      ::Kernel.raise(::TypeError, "can't convert #{object.class} into Hash (#{object.class}#to_hash gives #{converted.class})")
    end
    converted
  end
  module_function :Hash
  private :Hash

  # MRI has these as private instance methods on Kernel as well as public
  # singletons; IronRuby declared several of them singleton-only, and #loop was
  # not private at all.
  private :loop if public_method_defined?(:loop)
end

class LoadError
  # MRI 2.0 records the path that could not be loaded. IronRuby does not thread
  # it through the raise sites yet, so the reader exists and answers nil unless
  # something set it.
  def path
    defined?(@path) ? @path : nil
  end unless method_defined?(:path)
end

class SyntaxError
  def path
    defined?(@path) ? @path : nil
  end unless method_defined?(:path)
end

# Deliberately not defined: Regexp.timeout / Regexp.timeout= (Ruby 3.2).
# An accessor that only stores the value is worse than no accessor at all. The
# point of the setting is to bound catastrophic backtracking, and ruby/spec tests
# it by running /^(a*)*$/ against a million characters -- which hangs the .NET
# matcher outright once Regexp.timeout= stops being a NoMethodError.
# Implementing it means passing System.Text.RegularExpressions' matchTimeout into
# RubyRegex.TransformPattern (invalidating the cached Regex when the global
# changes) and mapping RegexMatchTimeoutException onto a real Regexp::TimeoutError.

class Random
  # 2.0's Random#random_number: rand's behaviour, but a bare call always answers
  # a Float and an out-of-range argument is an ArgumentError rather than nil.
  def random_number(limit = nil)
    limit.nil? ? rand : rand(limit)
  end unless method_defined?(:random_number)

  def self.random_number(limit = nil)
    limit.nil? ? rand : rand(limit)
  end unless respond_to?(:random_number)
end
