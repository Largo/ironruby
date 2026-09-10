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

  def sum(init = 0)
    if block_given?
      inject(init) { |acc, x| acc + yield(x) }
    else
      inject(init) { |acc, x| acc + x }
    end
  end unless method_defined?(:sum)

  alias_method :filter, :select unless method_defined?(:filter)
end

class Array
  def dig(key, *rest)
    value = self[key]
    return value if rest.empty? || value.nil?
    value.dig(*rest)
  end unless method_defined?(:dig)

  def sum(init = 0)
    inject(init) { |acc, x| block_given? ? acc + yield(x) : acc + x }
  end unless method_defined?(:sum)

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
    value = self[key]
    return value if rest.empty? || value.nil?
    value.dig(*rest)
  end unless method_defined?(:dig)

  def transform_values
    result = {}
    each { |k, v| result[k] = yield(v) }
    result
  end unless method_defined?(:transform_values)

  def transform_keys
    result = {}
    each { |k, v| result[yield(k)] = v }
    result
  end unless method_defined?(:transform_keys)

  def slice(*keys)
    result = {}
    keys.each { |k| result[k] = self[k] if key?(k) }
    result
  end unless method_defined?(:slice)

  def except(*keys)
    result = dup
    keys.each { |k| result.delete(k) }
    result
  end unless method_defined?(:except)

  def deconstruct_keys(keys)
    self
  end unless method_defined?(:deconstruct_keys)

  def compact
    reject { |_, v| v.nil? }
  end unless method_defined?(:compact)

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

class Integer
  def digits(base = 10)
    raise Math::DomainError, "out of domain" if negative?
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

  def clamp(min, max = nil)
    min, max = min.first, min.last if max.nil?
    self < min ? min : (self > max ? max : self)
  end unless method_defined?(:clamp)
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
end

module Kernel
  private

  def __dir__
    File.dirname(File.expand_path(caller.first.split(/:\d/, 2).first))
  end unless private_method_defined?(:__dir__)
end

class Hash
  # approximation: default Object#eql? is identity, which covers typical uses
  # (mspec keys caches by exception instances)
  def compare_by_identity
    self
  end unless method_defined?(:compare_by_identity)

  def compare_by_identity?
    false
  end unless method_defined?(:compare_by_identity?)
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
