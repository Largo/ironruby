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

    def self.__make_exception__(args)
      if args.empty?
        cur = $!
        return cur if cur
        return ::RuntimeError.new("")
      end
      first = args[0]
      if first.is_a?(::String)
        ::RuntimeError.new(first)
      elsif args.size >= 2
        exc = first.exception(args[1])
        exc.set_backtrace(args[2]) if args.size >= 3 && args[2]
        exc
      else
        first.exception
      end
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
      @thread = Thread.new do
        Thread.current[:__ir_fiber_current__] = fiber
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
      text = text[0...-1] if text.end_with?("\n")
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
class KeyError < StandardError; end unless defined?(KeyError)
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

  def to_h
    return dup unless block_given?
    result = {}
    each { |k, v| nk, nv = yield(k, v); result[nk] = nv }
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
  end unless const_defined?(:Yielder)

  unless method_defined?(:each_without_generator)
    alias_method :each_without_generator, :each

    def initialize(*args, &block)
      if block
        @generator = block
      else
        super
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
      return Enumerator.new { |y|
        n = offset
        source.each { |*a| y << [a.size <= 1 ? a.first : a, n]; n += 1 }
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
      return Enumerator.new { |y| source.each { |*a| y << [a.size <= 1 ? a.first : a, memo] } }
    end
    each do |*args|
      yield(args.size <= 1 ? args.first : args, memo)
    end
    memo
  end unless method_defined?(:with_object)
  alias_method :each_with_object, :with_object unless method_defined?(:each_with_object)

  def size
    nil
  end unless method_defined?(:size)
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
      (self <=> other) == 0
    rescue StandardError
      false
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
      raise KeyError, "key not found: #{key.inspect}"
    end
  end
end

# Ruby 3.4 changed Hash#inspect from {:a=>1} to {a: 1}, quoting symbol keys
# that are not simple identifiers, and spacing the rocket for other keys.
class Hash
  def inspect
    return "{}" if empty?
    body = map { |k, v|
      if k.is_a?(Symbol)
        name = k.to_s
        key = name =~ /\A[A-Za-z_][A-Za-z0-9_]*[?!=]?\z/ ? name : name.inspect
        "#{key}: #{v.inspect}"
      else
        "#{k.inspect} => #{v.inspect}"
      end
    }
    "{" + body.join(", ") + "}"
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

class Proc
  def ruby2_keywords
    self
  end unless method_defined?(:ruby2_keywords)
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
