# frozen_string_literal: true
# :markup: markdown
#
# set.rb - defines the Set class
#
# Copyright (c) 2002-2023 Akinori MUSHA <knu@iDaemons.org>
#
# Documentation by Akinori MUSHA and Gavin Sinclair.
#
# All rights reserved.  You can redistribute and/or modify it under the same
# terms as Ruby.


##
# This library provides the Set class, which implements a collection
# of unordered values with no duplicates. It is a hybrid of Array's
# intuitive inter-operation facilities and Hash's fast lookup.
#
# The method `to_set` is added to Enumerable for convenience.
#
# Set is easy to use with Enumerable objects (implementing `each`).
# Most of the initializer methods and binary operators accept generic
# Enumerable objects besides sets and arrays.  An Enumerable object
# can be converted to Set using the `to_set` method.
#
# Set uses Hash as storage, so you must note the following points:
#
# * Equality of elements is determined according to Object#eql? and
#   Object#hash.  Use Set#compare_by_identity to make a set compare
#   its elements by their identity.
# * Set assumes that the identity of each element does not change
#   while it is stored.  Modifying an element of a set will render the
#   set to an unreliable state.
# * When a string is to be stored, a frozen copy of the string is
#   stored instead unless the original string is already frozen.
#
# ## Comparison
#
# The comparison operators `<`, `>`, `<=`, and `>=` are implemented as
# shorthand for the {proper_,}{subset?,superset?} methods.  The `<=>`
# operator reflects this order, or return `nil` for sets that both
# have distinct elements (`{x, y}` vs. `{x, z}` for example).
#
# ## Example
#
# ```ruby
# require 'set'
# s1 = Set[1, 2]                        #=> #<Set: {1, 2}>
# s2 = [1, 2].to_set                    #=> #<Set: {1, 2}>
# s1 == s2                              #=> true
# s1.add("foo")                         #=> #<Set: {1, 2, "foo"}>
# s1.merge([2, 6])                      #=> #<Set: {1, 2, "foo", 6}>
# s1.subset?(s2)                        #=> false
# s2.subset?(s1)                        #=> true
# ```
#
# ## Contact
#
# - Akinori MUSHA <<knu@iDaemons.org>> (current maintainer)
#
# ## What's Here
#
#  First, what's elsewhere. \Class \Set:
#
# - Inherits from {class Object}[rdoc-ref:Object@What-27s+Here].
# - Includes {module Enumerable}[rdoc-ref:Enumerable@What-27s+Here],
#   which provides dozens of additional methods.
#
# In particular, class \Set does not have many methods of its own
# for fetching or for iterating.
# Instead, it relies on those in \Enumerable.
#
# Here, class \Set provides methods that are useful for:
#
# - [Creating a Set](#class-Set-label-Methods+for+Creating+a+Set)
# - [Set Operations](#class-Set-label-Methods+for+Set+Operations)
# - [Comparing](#class-Set-label-Methods+for+Comparing)
# - [Querying](#class-Set-label-Methods+for+Querying)
# - [Assigning](#class-Set-label-Methods+for+Assigning)
# - [Deleting](#class-Set-label-Methods+for+Deleting)
# - [Converting](#class-Set-label-Methods+for+Converting)
# - [Iterating](#class-Set-label-Methods+for+Iterating)
# - [And more....](#class-Set-label-Other+Methods)
#
# ### Methods for Creating a \Set
#
# - ::[]:
#   Returns a new set containing the given objects.
# - ::new:
#   Returns a new set containing either the given objects
#   (if no block given) or the return values from the called block
#   (if a block given).
#
# ### Methods for \Set Operations
#
# - [|](#method-i-7C) (aliased as #union and #+):
#   Returns a new set containing all elements from +self+
#   and all elements from a given enumerable (no duplicates).
# - [&](#method-i-26) (aliased as #intersection):
#   Returns a new set containing all elements common to +self+
#   and a given enumerable.
# - [-](#method-i-2D) (aliased as #difference):
#   Returns a copy of +self+ with all elements
#   in a given enumerable removed.
# - [\^](#method-i-5E):
#   Returns a new set containing all elements from +self+
#   and a given enumerable except those common to both.
#
# ### Methods for Comparing
#
# - [<=>](#method-i-3C-3D-3E):
#   Returns -1, 0, or 1 as +self+ is less than, equal to,
#   or greater than a given object.
# - [==](#method-i-3D-3D):
#   Returns whether +self+ and a given enumerable are equal,
#   as determined by Object#eql?.
# - \#compare_by_identity?:
#   Returns whether the set considers only identity
#   when comparing elements.
#
# ### Methods for Querying
#
# - \#length (aliased as #size):
#   Returns the count of elements.
# - \#empty?:
#   Returns whether the set has no elements.
# - \#include? (aliased as #member? and #===):
#   Returns whether a given object is an element in the set.
# - \#subset? (aliased as [<=](#method-i-3C-3D)):
#   Returns whether a given object is a subset of the set.
# - \#proper_subset? (aliased as [<](#method-i-3C)):
#   Returns whether a given enumerable is a proper subset of the set.
# - \#superset? (aliased as [>=](#method-i-3E-3D])):
#   Returns whether a given enumerable is a superset of the set.
# - \#proper_superset? (aliased as [>](#method-i-3E)):
#   Returns whether a given enumerable is a proper superset of the set.
# - \#disjoint?:
#   Returns +true+ if the set and a given enumerable
#   have no common elements, +false+ otherwise.
# - \#intersect?:
#   Returns +true+ if the set and a given enumerable:
#   have any common elements, +false+ otherwise.
# - \#compare_by_identity?:
#   Returns whether the set considers only identity
#   when comparing elements.
#
# ### Methods for Assigning
#
# - \#add (aliased as #<<):
#   Adds a given object to the set; returns +self+.
# - \#add?:
#   If the given object is not an element in the set,
#   adds it and returns +self+; otherwise, returns +nil+.
# - \#merge:
#   Merges the elements of each given enumerable object to the set; returns +self+.
# - \#replace:
#   Replaces the contents of the set with the contents
#   of a given enumerable.
#
# ### Methods for Deleting
#
# - \#clear:
#   Removes all elements in the set; returns +self+.
# - \#delete:
#   Removes a given object from the set; returns +self+.
# - \#delete?:
#   If the given object is an element in the set,
#   removes it and returns +self+; otherwise, returns +nil+.
# - \#subtract:
#   Removes each given object from the set; returns +self+.
# - \#delete_if - Removes elements specified by a given block.
# - \#select! (aliased as #filter!):
#   Removes elements not specified by a given block.
# - \#keep_if:
#   Removes elements not specified by a given block.
# - \#reject!
#   Removes elements specified by a given block.
#
# ### Methods for Converting
#
# - \#classify:
#   Returns a hash that classifies the elements,
#   as determined by the given block.
# - \#collect! (aliased as #map!):
#   Replaces each element with a block return-value.
# - \#divide:
#   Returns a hash that classifies the elements,
#   as determined by the given block;
#   differs from #classify in that the block may accept
#   either one or two arguments.
# - \#flatten:
#   Returns a new set that is a recursive flattening of +self+.
#  \#flatten!:
#   Replaces each nested set in +self+ with the elements from that set.
# - \#inspect (aliased as #to_s):
#   Returns a string displaying the elements.
# - \#join:
#   Returns a string containing all elements, converted to strings
#   as needed, and joined by the given record separator.
# - \#to_a:
#   Returns an array containing all set elements.
# - \#to_set:
#   Returns +self+ if given no arguments and no block;
#   with a block given, returns a new set consisting of block
#   return values.
#
# ### Methods for Iterating
#
# - \#each:
#   Calls the block with each successive element; returns +self+.
#
# ### Other Methods
#
# - \#reset:
#   Resets the internal state; useful if an object
#   has been modified while an element in the set.
#
class Set
  include Enumerable

  # Creates a new set containing the given objects.
  #
  #     Set[1, 2]                   # => #<Set: {1, 2}>
  #     Set[1, 2, 1]                # => #<Set: {1, 2}>
  #     Set[1, 'c', :s]             # => #<Set: {1, "c", :s}>
  def self.[](*ary)
    new(ary)
  end

  # Creates a new set containing the elements of the given enumerable
  # object.
  #
  # If a block is given, the elements of enum are preprocessed by the
  # given block.
  #
  #     Set.new([1, 2])                       #=> #<Set: {1, 2}>
  #     Set.new([1, 2, 1])                    #=> #<Set: {1, 2}>
  #     Set.new([1, 'c', :s])                 #=> #<Set: {1, "c", :s}>
  #     Set.new(1..5)                         #=> #<Set: {1, 2, 3, 4, 5}>
  #     Set.new([1, 2, 3]) { |x| x * x }      #=> #<Set: {1, 4, 9}>
  def initialize(enum = nil, &block) # :yields: o
    @__hash__ ||= Hash.new(false)

    enum.nil? and return

    if block
      if __core_set__?
        __init_block__(enum, block)
      else
        __do_with_enum__(enum) { |o| add(block[o]) }
      end
    else
      __init_enum__(enum)
    end
  end

  # set_i_initialize: an Array's elements are stored as they are, anything else's come
  # from its each_entry (or #each), a Set's too. A Set whose #each is Set's own is copied
  # by its table, which nothing can tell apart. A subclass with Set::SubclassCompatible
  # merges.
  def __init_enum__(enum) # :nodoc:
    return merge(enum) unless __core_set__? && !frozen? && !@__iter_lev__
    if ::Array === enum
      __merge_array__(enum)
    elsif ::Set === enum && enum.method(:each).owner.equal?(::Set) &&
        enum.method(:each_entry).owner.equal?(::Enumerable)
      @__hash__.update(enum.__send__(:__ir_hidden_ivar__, :@__hash__))
    else
      __merge_each__(enum)
    end
  end
  private :__init_enum__

  # set_initialize_with_block: what the block answers is stored without #add.
  def __init_block__(enum, block) # :nodoc:
    if ::Array === enum
      i = 0
      while i < enum.__send__(:__ary_size__)
        __store__(block[enum.__send__(:__ary_at__, i)])
        i = i.succ
      end
    else
      __do_with_enum__(enum) { |o| __store__(block[o]) }
    end
  end
  private :__init_block__

  # Makes the set compare its elements by their identity and returns
  # self.  This method may not be supported by all subclasses of Set.
  def compare_by_identity
    if @__hash__.respond_to?(:compare_by_identity)
      return self if @__hash__.compare_by_identity?
      __check_frozen__
      raise RuntimeError, "compare_by_identity during iteration" if @__iter_lev__
      @__hash__.compare_by_identity
      self
    else
      raise NotImplementedError, "#{self.class.name}\##{__method__} is not implemented"
    end
  end

  # Returns true if the set will compare its elements by their
  # identity.  Also see Set#compare_by_identity.
  def compare_by_identity?
    @__hash__.respond_to?(:compare_by_identity?) && @__hash__.compare_by_identity?
  end

  def __do_with_enum__(enum, &block) # :nodoc:
    if enum.respond_to?(:each_entry)
      enum.each_entry(&block) if block
    elsif enum.respond_to?(:each)
      enum.each(&block) if block
    else
      raise ArgumentError, "value must be enumerable"
    end
  end
  private :__do_with_enum__

  # An empty set of the receiver's class for the results of &, flatten, collect! and
  # classify. Core Set's (set_s_alloc) is allocated, not made with #new, so that an
  # #initialize that wants arguments is not called, and compares the way the receiver
  # does. A subclass with Set::SubclassCompatible makes it with self.class.new, which
  # compares the ordinary way.
  def __new_like__ # :nodoc:
    __core_set__? ? __alloc_like__ : self.class.new
  end
  private :__new_like__

  # set_s_alloc and set_i_compare_by_identity, which core Set uses for every result it
  # makes, a subclass's included (^ and divide).
  def __alloc_like__ # :nodoc:
    self.class.allocate.__send__(:__core_init__, @__hash__.compare_by_identity?)
  end
  private :__alloc_like__

  def __core_init__(identity) # :nodoc:
    @__hash__ = Hash.new(false)
    @__hash__.compare_by_identity if identity
    self
  end
  private :__core_init__

  # Ruby 4.0's core Set names itself, not its table, when frozen:
  # "can't modify frozen Set: Set[1]".
  def __check_frozen__ # :nodoc:
    raise FrozenError.new("can't modify frozen #{self.class}: #{inspect}", receiver: self) if frozen?
  end
  private :__check_frozen__

  # Dup internal hash.
  def initialize_copy(orig)
    super
    @__hash__ = orig.__send__(:__ir_hidden_ivar__, :@__hash__).dup
    # a copy taken inside #each is not itself being iterated
    @__iter_lev__ = nil if @__iter_lev__
  end

  # Clone internal hash.
  def initialize_clone(orig, **options)
    super
    @__hash__ = orig.__send__(:__ir_hidden_ivar__, :@__hash__).clone(**options)
    @__iter_lev__ = nil if @__iter_lev__ && !frozen?
  end

  def freeze    # :nodoc:
    @__hash__.freeze
    super
  end

  # Returns the number of elements.
  def size
    @__hash__.size
  end
  alias length size

  # Returns true if the set contains no elements.
  def empty?
    @__hash__.empty?
  end

  # Removes all elements and returns self.
  #
  #     set = Set[1, 'c', :s]             #=> #<Set: {1, "c", :s}>
  #     set.clear                         #=> #<Set: {}>
  #     set                               #=> #<Set: {}>
  def clear
    __check_frozen__
    @__hash__.clear
    self
  end

  # Replaces the contents of the set with the contents of the given
  # enumerable object and returns self.
  #
  #     set = Set[1, 'c', :s]             #=> #<Set: {1, "c", :s}>
  #     set.replace([1, 2])               #=> #<Set: {1, 2}>
  #     set                               #=> #<Set: {1, 2}>
  def replace(enum)
    __check_frozen__
    raise RuntimeError, "cannot replace set during iteration" if @__iter_lev__
    if enum.instance_of?(self.class)
      @__hash__.replace(enum.__send__(:__ir_hidden_ivar__, :@__hash__))
      self
    else
      __do_with_enum__(enum)  # make sure enum is enumerable before calling clear
      clear
      merge(enum)
    end
  end

  # Converts the set to an array.  The order of elements is uncertain.
  #
  #     Set[1, 2].to_a                    #=> [1, 2]
  #     Set[1, 'c', :s].to_a              #=> [1, "c", :s]
  def to_a
    @__hash__.keys
  end

  # Returns self if no arguments are given.  Otherwise, converts the
  # set to another with `klass.new(self, *args, &block)`.
  #
  # In subclasses, returns `klass.new(self, *args, &block)` unless
  # overridden.
  def to_set(klass = Set, *args, &block)
    return self if instance_of?(Set) && klass == Set && block.nil? && args.empty?
    klass.new(self, *args, &block)
  end

  def __flatten_merge__(set, seen = Set.new) # :nodoc:
    set.each { |e|
      if e.is_a?(Set)
        if seen.include?(e_id = e.object_id)
          raise ArgumentError, "tried to flatten recursive Set"
        end

        seen.add(e_id)
        __flatten_merge__(e, seen)
        seen.delete(e_id)
      else
        add(e)
      end
    }

    self
  end
  protected :__flatten_merge__

  # Returns a new set that is a copy of the set, flattening each
  # containing set recursively.
  def flatten
    __new_like__.__flatten_merge__(self)
  end

  # Equivalent to Set#flatten, but replaces the receiver with the
  # result in place.  Returns nil if no modifications were made.
  def flatten!
    replace(flatten()) if any?(Set)
  end

  # Returns true if the set contains the given object.
  #
  # Note that <code>include?</code> and <code>member?</code> do not test member
  # equality using <code>==</code> as do other Enumerables.
  #
  # See also Enumerable#include?
  def include?(o)
    @__hash__[o]
  end
  alias member? include?

  # Returns true if the set is a superset of the given set.
  def superset?(set)
    case
    when ::Set === set && __core_set__?
      other = set.__send__(:__ir_hidden_ivar__, :@__hash__)
      @__hash__.size >= other.size && __table_in__(other, @__hash__)
    when set.instance_of?(self.class) && @__hash__.respond_to?(:>=)
      @__hash__ >= set.__send__(:__ir_hidden_ivar__, :@__hash__)
    when set.is_a?(Set)
      size >= set.size && set.all?(self)
    else
      raise ArgumentError, "value must be a set"
    end
  end
  alias >= superset?

  # Returns true if the set is a proper superset of the given set.
  def proper_superset?(set)
    case
    when ::Set === set && __core_set__?
      other = set.__send__(:__ir_hidden_ivar__, :@__hash__)
      @__hash__.size > other.size && __table_in__(other, @__hash__)
    when set.instance_of?(self.class) && @__hash__.respond_to?(:>)
      @__hash__ > set.__send__(:__ir_hidden_ivar__, :@__hash__)
    when set.is_a?(Set)
      size > set.size && set.all?(self)
    else
      raise ArgumentError, "value must be a set"
    end
  end
  alias > proper_superset?

  # Returns true if the set is a subset of the given set.
  def subset?(set)
    case
    when ::Set === set && __core_set__?
      other = set.__send__(:__ir_hidden_ivar__, :@__hash__)
      @__hash__.size <= other.size && __table_in__(@__hash__, other)
    when set.instance_of?(self.class) && @__hash__.respond_to?(:<=)
      @__hash__ <= set.__send__(:__ir_hidden_ivar__, :@__hash__)
    when set.is_a?(Set)
      size <= set.size && all?(set)
    else
      raise ArgumentError, "value must be a set"
    end
  end
  alias <= subset?

  # Returns true if the set is a proper subset of the given set.
  def proper_subset?(set)
    case
    when ::Set === set && __core_set__?
      other = set.__send__(:__ir_hidden_ivar__, :@__hash__)
      @__hash__.size < other.size && __table_in__(@__hash__, other)
    when set.instance_of?(self.class) && @__hash__.respond_to?(:<)
      @__hash__ < set.__send__(:__ir_hidden_ivar__, :@__hash__)
    when set.is_a?(Set)
      size < set.size && all?(set)
    else
      raise ArgumentError, "value must be a set"
    end
  end
  alias < proper_subset?

  # Whether every key of +a+ is one of +b+ (set_le_i): the tables, not #each or #include?,
  # as core Set compares with another Set.
  def __table_in__(a, b) # :nodoc:
    a.each_key { |o| return false unless b.key?(o) }
    true
  end
  private :__table_in__

  # Whether +a+ and +b+ share a key (set_i_intersect's walk of the smaller table).
  def __tables_meet__(a, b) # :nodoc:
    a, b = b, a if a.size > b.size
    a.each_key { |o| return true if b.key?(o) }
    false
  end
  private :__tables_meet__

  # Returns 0 if the set are equal,
  # -1 / +1 if the set is a proper subset / superset of the given set,
  # or nil if they both have unique elements.
  def <=>(set)
    return unless set.is_a?(Set)

    case size <=> set.size
    when -1 then -1 if proper_subset?(set)
    when +1 then +1 if proper_superset?(set)
    else 0 if self.==(set)
    end
  end

  # Returns true if the set and the given enumerable have at least one
  # element in common.
  #
  #     Set[1, 2, 3].intersect? Set[4, 5]   #=> false
  #     Set[1, 2, 3].intersect? Set[3, 4]   #=> true
  #     Set[1, 2, 3].intersect? 4..5        #=> false
  #     Set[1, 2, 3].intersect? [3, 4]      #=> true
  def intersect?(set)
    case set
    when Set
      return __tables_meet__(@__hash__, set.__send__(:__ir_hidden_ivar__, :@__hash__)) if __core_set__?
      if size < set.size
        any?(set)
      else
        set.any?(self)
      end
    when Enumerable
      set.any?(self)
    else
      raise ArgumentError, "value must be enumerable"
    end
  end

  # Returns true if the set and the given enumerable have
  # no element in common.  This method is the opposite of `intersect?`.
  #
  #     Set[1, 2, 3].disjoint? Set[3, 4]   #=> false
  #     Set[1, 2, 3].disjoint? Set[4, 5]   #=> true
  #     Set[1, 2, 3].disjoint? [3, 4]      #=> false
  #     Set[1, 2, 3].disjoint? 4..5        #=> true
  def disjoint?(set)
    !intersect?(set)
  end

  # Calls the given block once for each element in the set, passing
  # the element as parameter.  Returns an enumerator if no block is
  # given.
  #
  # Like MRI, the set counts the iterations running over it (@__iter_lev__) so
  # that adding a new element, merging or replacing meanwhile raises.
  def each(&block)
    block_given? or return enum_for(__method__) { size }
    return @__hash__.each_key(&block) && self if frozen?

    @__iter_lev__ = (@__iter_lev__ || 0) + 1
    begin
      @__hash__.each_key(&block)
    ensure
      # a set frozen inside the block keeps the count; it cannot change anyway
      unless frozen?
        if @__iter_lev__ > 1
          @__iter_lev__ -= 1
        else
          @__iter_lev__ = nil
        end
      end
    end
    self
  end

  # Adds the given object to the set and returns self.  Use `merge` to
  # add many elements at once.
  #
  #     Set[1, 2].add(3)                    #=> #<Set: {1, 2, 3}>
  #     Set[1, 2].add([3, 4])               #=> #<Set: {1, 2, [3, 4]}>
  #     Set[1, 2].add(2)                    #=> #<Set: {1, 2}>
  def add(o)
    __check_frozen__
    if @__iter_lev__ && !@__hash__.key?(o)
      raise RuntimeError, "can't add a new item into set during iteration"
    end
    @__hash__[o] = true
    self
  end
  alias << add

  # Adds the given object to the set and returns self.  If the
  # object is already in the set, returns nil.
  #
  #     Set[1, 2].add?(3)                    #=> #<Set: {1, 2, 3}>
  #     Set[1, 2].add?([3, 4])               #=> #<Set: {1, 2, [3, 4]}>
  #     Set[1, 2].add?(2)                    #=> nil
  def add?(o)
    add(o) unless include?(o)
  end

  # Deletes the given object from the set and returns self.  Use
  # `subtract` to delete many items at once.
  def delete(o)
    __check_frozen__
    @__hash__.delete(o)
    self
  end

  # Deletes the given object from the set and returns self.  If the
  # object is not in the set, returns nil.
  def delete?(o)
    delete(o) if include?(o)
  end

  # Deletes every element of the set for which block evaluates to
  # true, and returns self. Returns an enumerator if no block is
  # given.
  def delete_if
    block_given? or return enum_for(__method__) { size }
    __check_frozen__
    # @__hash__.delete_if should be faster, but using it breaks the order
    # of enumeration in subclasses.
    select { |o| yield o }.each { |o| @__hash__.delete(o) }
    self
  end

  # Deletes every element of the set for which block evaluates to
  # false, and returns self. Returns an enumerator if no block is
  # given.
  def keep_if
    block_given? or return enum_for(__method__) { size }
    __check_frozen__
    # @__hash__.keep_if should be faster, but using it breaks the order of
    # enumeration in subclasses.
    reject { |o| yield o }.each { |o| @__hash__.delete(o) }
    self
  end

  # Replaces the elements with ones returned by `collect()`.
  # Returns an enumerator if no block is given.
  def collect!
    block_given? or return enum_for(__method__) { size }
    __check_frozen__
    set = __new_like__
    each { |o| set << yield(o) }
    replace(set)
  end
  alias map! collect!

  # Equivalent to Set#delete_if, but returns nil if no changes were
  # made. Returns an enumerator if no block is given.
  def reject!(&block)
    block_given? or return enum_for(__method__) { size }
    n = size
    delete_if(&block)
    self if size != n
  end

  # Equivalent to Set#keep_if, but returns nil if no changes were
  # made. Returns an enumerator if no block is given.
  def select!(&block)
    block_given? or return enum_for(__method__) { size }
    n = size
    keep_if(&block)
    self if size != n
  end

  # Equivalent to Set#select!
  alias filter! select!

  # Merges the elements of the given enumerable objects to the set and
  # returns self.
  def merge(*enums, **nil)
    __check_frozen__
    raise RuntimeError, "cannot add to set during iteration" if @__iter_lev__
    enums.each { |enum| __merge_enum__(enum) }
    self
  end

  # set_merge_enum_into: core Set fills its table from another Set or an Array without
  # calling #each or #add, and from anything else with each_entry (or #each), still without
  # #add. A subclass with Set::SubclassCompatible adds each element with #add.
  def __merge_enum__(enum) # :nodoc:
    if enum.instance_of?(self.class)
      @__hash__.update(enum.__send__(:__ir_hidden_ivar__, :@__hash__))
    elsif !__core_set__?
      __do_with_enum__(enum) { |o| add(o) }
    elsif ::Array === enum
      __merge_array__(enum)
    elsif ::Set === enum
      @__hash__.update(enum.__send__(:__ir_hidden_ivar__, :@__hash__))
    else
      __merge_each__(enum)
    end
    self
  end
  private :__merge_enum__

  # The Array's storage, not its #each, #size or #[]: a subclass's own are not called.
  def __merge_array__(ary) # :nodoc:
    h = @__hash__
    if ary.instance_of?(::Array)
      ary.each { |o| h[o] = true }
    else
      i = 0
      while i < ary.__send__(:__ary_size__)
        h[ary.__send__(:__ary_at__, i)] = true
        i = i.succ
      end
    end
  end
  private :__merge_array__

  # set_merge_enum_into's rb_block_call of each_entry (or #each), whose block stores the
  # element without #add. Enumerable#each_entry is inlined when it is the receiver's: it
  # packs what #each yields (no value is nil, several an Array) as enum_yield does. The
  # block raises as #add did when the enumerable froze or began iterating the set (MRI
  # stores the element regardless; the table here, a Hash, would raise its own error).
  def __merge_each__(enum) # :nodoc:
    if enum.respond_to?(:each_entry)
      if enum.method(:each_entry).owner.equal?(::Enumerable)
        __merge_one_each__(enum) and return
        enum.each { |*v| __store__(v.size == 1 ? v[0] : (v.empty? ? nil : v)) }
      else
        enum.each_entry { |o| __store__(o) }
      end
    elsif enum.respond_to?(:each)
      enum.each { |o| __store__(o) }
    else
      raise ArgumentError, "value must be enumerable"
    end
  end
  private :__merge_each__

  # #add without the call of a subclass's own.
  def __store__(o) # :nodoc:
    h = @__hash__
    h.frozen? and __check_frozen__
    if @__iter_lev__ && !h.key?(o)
      raise RuntimeError, "can't add a new item into set during iteration"
    end
    h[o] = true
  end
  private :__store__

  # A Range's or an arithmetic sequence's own #each yields one value at a time, so the
  # block takes it as it is. Answers nil for any other #each.
  def __merge_one_each__(enum) # :nodoc:
    c = enum.class
    return unless (c.equal?(::Range) || c.equal?(::Enumerator::ArithmeticSequence)) &&
      enum.method(:each).owner.equal?(c)
    h = @__hash__
    enum.each { |o| h[o] = true }
    true
  end
  private :__merge_one_each__

  # Deletes every element that appears in the given enumerable object
  # and returns self.
  def subtract(enum)
    __check_frozen__
    __do_with_enum__(enum) { |o| delete(o) }
    self
  end

  # Returns a new set built by merging the set and the elements of the
  # given enumerable object.
  #
  #     Set[1, 2, 3] | Set[2, 4, 5]         #=> #<Set: {1, 2, 3, 4, 5}>
  #     Set[1, 5, 'z'] | (1..6)             #=> #<Set: {1, 5, "z", 2, 3, 4, 6}>
  def |(enum)
    dup.merge(enum)
  end
  alias + |
  alias union |

  # Returns a new set built by duplicating the set, removing every
  # element that appears in the given enumerable object.
  #
  #     Set[1, 3, 5] - Set[1, 5]                #=> #<Set: {3}>
  #     Set['a', 'b', 'z'] - ['a', 'c']         #=> #<Set: {"b", "z"}>
  def -(enum)
    return dup.subtract(enum) unless __core_set__?
    n = dup
    h = n.__send__(:__ir_hidden_ivar__, :@__hash__)
    if ::Set === enum
      enum.__send__(:__ir_hidden_ivar__, :@__hash__).each_key { |o| h.delete(o) }
    else
      __do_with_enum__(enum) { |o| h.delete(o) }
    end
    n
  end
  alias difference -

  # Returns a new set containing elements common to the set and the
  # given enumerable object.
  #
  #     Set[1, 3, 5] & Set[3, 2, 1]             #=> #<Set: {3, 1}>
  #     Set['a', 'b', 'z'] & ['a', 'b', 'c']    #=> #<Set: {"a", "b"}>
  def &(enum)
    n = __new_like__
    if ::Set === enum && __core_set__?
      # set_i_intersection: the smaller table walked, the larger one looked up
      h = n.__send__(:__ir_hidden_ivar__, :@__hash__)
      mine = @__hash__
      other = enum.__send__(:__ir_hidden_ivar__, :@__hash__)
      if other.size > mine.size
        mine.each_key { |o| h[o] = true if other.key?(o) }
      else
        other.each_key { |o| h[o] = true if mine.key?(o) }
      end
    elsif enum.is_a?(Set)
      if enum.size > size
        each { |o| n.add(o) if enum.include?(o) }
      else
        enum.each { |o| n.add(o) if include?(o) }
      end
    elsif __core_set__?
      # the block returns the element, as core Set's set_intersection_block does: an
      # enumerable's #each may look at what its block answers. Kept and called after
      # #& has returned, it still adds to the result but answers nil, as MRI's does
      # (test_set.rb's test_and).
      h = n.__send__(:__ir_hidden_ivar__, :@__hash__)
      mine = @__hash__
      done = false
      begin
        __do_with_enum__(enum) { |o| h[o] = true if mine.key?(o); done ? nil : o }
      ensure
        done = true
      end
    else
      # Set::SubclassCompatible's block answers what its last expression does
      __do_with_enum__(enum) { |o| n.add(o) if include?(o) }
    end
    n
  end
  alias intersection &

  # Returns a new set containing elements exclusive between the set
  # and the given enumerable object.  `(set ^ enum)` is equivalent to
  # `((set | enum) - (set & enum))`.
  #
  #     Set[1, 2] ^ Set[2, 3]                   #=> #<Set: {3, 1}>
  #     Set[1, 'b', 'c'] ^ ['b', 'd']           #=> #<Set: {"d", 1, "c"}>
  #
  # Ruby 4.0 starts from a copy of the receiver, so the result keeps its class
  # and compare_by_identity; the argument is deduplicated first (into a set that is
  # allocated, not made with #new), the way the receiver compares. A subclass with
  # Set::SubclassCompatible starts from self.class.new(enum) instead and adds the
  # receiver's elements to it.
  def ^(enum)
    unless __core_set__?
      n = self.class.new(enum)
      each { |o| n.add(o) unless n.delete?(o) }
      return n
    end
    n = dup
    h = n.__send__(:__ir_hidden_ivar__, :@__hash__)
    enum = __alloc_like__.merge(enum) unless ::Set === enum
    enum.__send__(:__ir_hidden_ivar__, :@__hash__).each_key do |o|
      if h.key?(o)
        h.delete(o)
      else
        h[o] = true
      end
    end
    n
  end

  # Returns true if two sets are equal.  The equality of each couple
  # of elements is defined according to Object#eql?.
  #
  #     Set[1, 2] == Set[2, 1]                       #=> true
  #     Set[1, 3, 5] == Set[1, 5]                    #=> false
  #     Set['a', 'b', 'c'] == Set['a', 'c', 'b']     #=> true
  #     Set['a', 'b', 'c'] == ['a', 'c', 'b']        #=> false
  def ==(other)
    if self.equal?(other)
      true
    elsif other.instance_of?(self.class)
      @__hash__ == other.__send__(:__ir_hidden_ivar__, :@__hash__)
    elsif other.is_a?(Set) && self.size == other.size
      other.all? { |o| @__hash__.include?(o) }
    else
      false
    end
  end

  def hash      # :nodoc:
    @__hash__.hash
  end

  # Ruby 4.0 made #eql? the same method as #==, not merely one that agrees with it.
  alias eql? ==

  # Resets the internal state after modification to existing elements
  # and returns self.
  #
  # Elements will be reindexed and deduplicated.
  def reset
    __check_frozen__
    raise RuntimeError, "reset during iteration" if @__iter_lev__
    if @__hash__.respond_to?(:rehash)
      @__hash__.rehash # This should perform frozenness check.
    else
      raise FrozenError.new("can't modify frozen #{self.class}: #{inspect}", receiver: self) if frozen?
    end
    self
  end

  # Returns true if the given object is a member of the set,
  # and false otherwise.
  #
  # Used in case statements:
  #
  #     require 'set'
  #
  #     case :apple
  #     when Set[:potato, :carrot]
  #       "vegetable"
  #     when Set[:apple, :banana]
  #       "fruit"
  #     end
  #     # => "fruit"
  #
  # Or by itself:
  #
  #     Set[1, 2, 3] === 2   #=> true
  #     Set[1, 2, 3] === 4   #=> false
  #
  alias === include?

  # Classifies the set by the return value of the given block and
  # returns a hash of {value => set of elements} pairs.  The block is
  # called once for each element of the set, passing the element as
  # parameter.
  #
  #     require 'set'
  #     files = Set.new(Dir.glob("*.rb"))
  #     hash = files.classify { |f| File.mtime(f).year }
  #     hash       #=> {2000=>#<Set: {"a.rb", "b.rb"}>,
  #                #    2001=>#<Set: {"c.rb", "d.rb", "e.rb"}>,
  #                #    2002=>#<Set: {"f.rb"}>}
  #
  # Returns an enumerator if no block is given.
  def classify(&block) # :yields: o
    block_given? or return enum_for(__method__) { size }
    __classify__(__core_set__?, &block)
  end

  # set_i_classify's loop; #divide uses core Set's (allocated subsets) whatever the
  # receiver's class.
  def __classify__(core) # :nodoc:
    h = {}

    each { |i|
      (h[yield(i)] ||= core ? __alloc_like__ : self.class.new).add(i)
    }

    h
  end
  private :__classify__

  # Divides the set into a set of subsets according to the commonality
  # defined by the given block.
  #
  # If the arity of the block is 2, elements o1 and o2 are in common
  # if both block.call(o1, o2) and block.call(o2, o1) are true.  Otherwise,
  # elements o1 and o2 are in common if block.call(o1) == block.call(o2).
  #
  #     require 'set'
  #     numbers = Set[1, 3, 4, 6, 9, 10, 11]
  #     set = numbers.divide { |i,j| (i - j).abs == 1 }
  #     set        #=> Set[Set[1], Set[3, 4], Set[6], Set[9, 10, 11]]
  #
  # Returns an enumerator if no block is given.
  #
  # #divide stays core Set's in a subclass with Set::SubclassCompatible too: the subsets
  # are allocated (no #initialize) and compare the way the receiver does, and the result
  # is a plain Set.
  def divide(&func)
    func or return enum_for(__method__) { size }

    if func.arity == 2
      # set_divide_arity2: a union-find over the positions of the elements (not the
      # elements themselves, which an identity set may hold several equal ones of),
      # joining i and j when the block relates them both ways. Ruby 4.0 no longer asks
      # the block to relate an element to itself.
      items = to_a.freeze
      parents = Array.new(items.size) { |i| i }
      root = lambda do |i|
        i = parents[i] while parents[i] != i
        i
      end
      (0...items.size - 1).each do |i|
        (i + 1...items.size).each do |j|
          if func.call(items[i], items[j]) && func.call(items[j], items[i])
            ri = root.(i)
            rj = root.(j)
            parents[rj] = ri if ri != rj
          end
        end
      end

      set = Set.new()
      subsets = {}
      items.each_with_index do |v, i|
        unless (subset = subsets[root.(i)])
          subset = subsets[root.(i)] = __alloc_like__
          set.add(subset)
        end
        subset.add(v)
      end
      set
    else
      Set.new(__classify__(true, &func).values)
    end
  end

  # Returns a string created by converting each element of the set to a string
  # See also: Array#join
  def join(separator=nil)
    to_a.join(separator)
  end

  InspectKey = :__inspect_key__         # :nodoc:
  __hide_constant__ :InspectKey

  # Ruby 4.0 made Set a core class and gave it the literal-looking form
  # "Set[1, 2]"; a subclass keeps the older "#<MySet: {1, 2}>", unless it is one of
  # Set::CoreSet, which has the core behaviour ("MySet[1, 2]").
  def inspect
    ids = (Thread.current[InspectKey] ||= [])
    plain = __core_set__?

    if ids.include?(object_id)
      return __ir_binary_if_ascii__(plain ? "#{self.class}[...]" : sprintf('#<%s: {...}>', self.class.name))
    end

    ids << object_id
    begin
      body = to_a.inspect[1..-2]
      return __ir_binary_if_ascii__(plain ? "#{self.class}[#{body}]" : sprintf('#<%s: {%s}>', self.class, body))
    ensure
      ids.pop
    end
  end

  alias to_s inspect

  # Set itself pretty-prints in the Ruby 4.0 "Set[...]" form, subclasses in
  # the older one, as #inspect does.
  def pretty_print(pp)  # :nodoc:
    if __core_set__?
      return pp.group(1, "#{self.class}[", ']') {
        pp.seplist(self) { |o|
          pp.pp o
        }
      }
    end

    pp.group(1, sprintf('#<%s:', self.class.name), '>') {
      pp.breakable
      pp.group(1, '{', '}') {
        pp.seplist(self) { |o|
          pp.pp o
        }
      }
    }
  end

  def pretty_print_cycle(pp)    # :nodoc:
    return pp.text(empty? ? "#{self.class}[]" : "#{self.class}[...]") if __core_set__?
    pp.text sprintf('#<%s: {%s}>', self.class.name, empty? ? '' : '...')
  end

  def __core_set__? # :nodoc:
    !is_a?(SubclassCompatible)
  end
  private :__core_set__?

  # In Ruby 4.0 a direct subclass of the core Set is given Set::SubclassCompatible, the
  # behaviour of the pure-Ruby Set before it, while a subclass of CoreSet keeps the core
  # behaviour. IronRuby's Set is that pure-Ruby Set, so the module holds no methods: it
  # marks the subclasses whose results (&, ^, flatten, collect!, classify) are made with
  # self.class.new and #inspect is "#<MySet: {1, 2}>", and it shows in their ancestors.
  class CoreSet < Set; end

  module SubclassCompatible; end
  private_constant :SubclassCompatible

  # set_s_inherited, defined after CoreSet so that CoreSet does not get the module
  def self.inherited(subclass)
    super
    subclass.include(SubclassCompatible) if equal?(::Set)
  end
  private_class_method :inherited
end

module Enumerable
  # Makes a set from the enumerable object with given arguments.
  # Needs to `require "set"` to use this method.
  def to_set(klass = Set, *args, &block)
    klass.new(self, *args, &block)
  end unless method_defined?(:to_set)
end
