# frozen_string_literal: true
#
# IronRuby implementation of the Fiddle library core.
#
# Dynamic library loading is implemented on top of
# System.Runtime.InteropServices.NativeLibrary, which performs a real
# dlopen(3)/dlsym(3)/dlclose(3) on POSIX systems (LoadLibrary/GetProcAddress
# on Windows).  Foreign calls (Fiddle::Function) go through Fiddle::Native,
# in Src/Libraries/Fiddle/Fiddle.cs, which emits a `calli` stub per signature.
# Fiddle::Closure (native -> Ruby callbacks) is still not implemented.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Fiddle'

module Fiddle
  NativeLibrary = System::Runtime::InteropServices::NativeLibrary
  private_constant :NativeLibrary

  class Error < StandardError; end
  class DLError < Error; end
  class ClearedReferenceError < Error; end

  if System::Runtime::InteropServices::RuntimeInformation.IsOSPlatform(
       System::Runtime::InteropServices::OSPlatform.Windows)
    WINDOWS = true
  else
    WINDOWS = false
  end

  # dlopen(3) mode flags (glibc values; informational on non-glibc since
  # NativeLibrary does not expose mode selection).
  RTLD_LAZY   = 0x00001
  RTLD_NOW    = 0x00002
  RTLD_GLOBAL = 0x00100

  ALIGN_VOIDP    = 8
  ALIGN_CHAR     = 1
  ALIGN_SHORT    = 2
  ALIGN_INT      = 4
  ALIGN_LONG     = 8
  ALIGN_LONG_LONG = 8
  ALIGN_FLOAT    = 4
  ALIGN_DOUBLE   = 8

  SIZEOF_VOIDP    = System::IntPtr.Size
  SIZEOF_CHAR     = 1
  SIZEOF_SHORT    = 2
  SIZEOF_INT      = 4
  SIZEOF_LONG     = SIZEOF_VOIDP == 8 && !WINDOWS ? 8 : 4
  SIZEOF_LONG_LONG = 8
  SIZEOF_FLOAT    = 4
  SIZEOF_DOUBLE   = 8

  TYPE_VOID      = 0
  TYPE_VOIDP     = 1
  TYPE_CHAR      = 2
  TYPE_UCHAR     = -TYPE_CHAR
  TYPE_SHORT     = 3
  TYPE_USHORT    = -TYPE_SHORT
  TYPE_INT       = 4
  TYPE_UINT      = -TYPE_INT
  TYPE_LONG      = 5
  TYPE_ULONG     = -TYPE_LONG
  TYPE_LONG_LONG = 6
  TYPE_ULONG_LONG = -TYPE_LONG_LONG
  TYPE_FLOAT     = 7
  TYPE_DOUBLE    = 8
  TYPE_VARIADIC  = 9
  TYPE_CONST_STRING = 10
  TYPE_BOOL      = 11

  # Typedefs, in terms of the basic types above.  size_t and friends are
  # pointer-sized, which is C's long everywhere except Windows (LLP64).
  TYPE_SSIZE_T   = SIZEOF_VOIDP == SIZEOF_LONG ? TYPE_LONG : TYPE_LONG_LONG
  TYPE_SIZE_T    = -TYPE_SSIZE_T
  TYPE_PTRDIFF_T = TYPE_SSIZE_T
  TYPE_INTPTR_T  = TYPE_SSIZE_T
  TYPE_UINTPTR_T = -TYPE_SSIZE_T

  SIZEOF_SIZE_T    = SIZEOF_VOIDP
  SIZEOF_SSIZE_T   = SIZEOF_VOIDP
  SIZEOF_PTRDIFF_T = SIZEOF_VOIDP
  SIZEOF_INTPTR_T  = SIZEOF_VOIDP
  SIZEOF_UINTPTR_T = SIZEOF_VOIDP
  SIZEOF_CONST_STRING = SIZEOF_VOIDP

  ALIGN_SIZE_T    = SIZEOF_VOIDP
  ALIGN_SSIZE_T   = SIZEOF_VOIDP
  ALIGN_PTRDIFF_T = SIZEOF_VOIDP
  ALIGN_INTPTR_T  = SIZEOF_VOIDP
  ALIGN_UINTPTR_T = SIZEOF_VOIDP
  ALIGN_CONST_STRING = SIZEOF_VOIDP

  def self.last_error
    Thread.current[:__FIDDLE_LAST_ERROR__]
  end

  def self.last_error=(error)
    Thread.current[:__FIDDLE_LAST_ERROR__] = error
  end

  if WINDOWS
    def self.win32_last_error
      Thread.current[:__FIDDLE_WIN32_LAST_ERROR__]
    end

    def self.win32_last_error=(error)
      Thread.current[:__FIDDLE_WIN32_LAST_ERROR__] = error
    end
  end

  class Handle
    # Open a dynamic library.
    #
    #   Handle.new            -- the main program (dlopen(NULL))
    #   Handle.new(nil)       -- ditto
    #   Handle.new(lib)       -- dlopen(lib)
    #
    # +flags+ is accepted for compatibility; NativeLibrary always resolves
    # lazily and locally, so the flags only affect the recorded value.
    def initialize(lib = nil, flags = RTLD_LAZY | RTLD_GLOBAL)
      @lib = lib.nil? ? nil : lib.to_str
      @flags = flags
      @open = true
      @enable_close = false
      if @lib.nil?
        @ptr = NativeLibrary.GetMainProgramHandle
      else
        begin
          @ptr = NativeLibrary.Load(@lib)
        rescue System::DllNotFoundException, System::BadImageFormatException,
               System::ArgumentException => e
          # On glibc the CLR appends the dlerror(3) text as the last line of
          # the exception message; prefer it, as CRuby reports dlerror.
          msg = e.Message.to_s.lines.map(&:chomp).reject(&:empty?)
          raise DLError, (msg.last || "could not open library #{@lib}")
        end
      end
      if block_given?
        begin
          yield self
        ensure
          close if @open
        end
      end
    end

    # dlsym(RTLD_DEFAULT)-style lookup: searches the global symbol scope of
    # the running process (via the main program handle, which on POSIX has
    # global search semantics).
    DEFAULT = new

    # RTLD_NEXT cannot be expressed through NativeLibrary; expose the
    # pseudo-handle object so that code referring to Handle::NEXT loads,
    # but symbol lookup through it raises DLError.
    NEXT = allocate
    NEXT.instance_eval do
      @lib = nil
      @flags = 0
      @open = true
      @enable_close = false
      @ptr = nil
    end

    class << self
      # Address of the function +name+ in the global symbol scope.
      def sym(name)
        DEFAULT.sym(name)
      end
      alias [] sym

      def sym_defined?(name)
        DEFAULT.sym_defined?(name)
      end
    end

    def to_i
      ptr = @ptr
      ptr.nil? ? -1 : ptr.ToInt64
    end
    alias to_int to_i

    def file_name
      @lib
    end

    def sym(name)
      addr = try_sym(name.to_str)
      raise DLError, "unknown symbol \"#{name}\"" unless addr
      addr
    end
    alias [] sym

    def sym_defined?(name)
      !try_sym(name.to_str).nil?
    end

    def close
      raise DLError, "closed handle" unless @open
      @open = false
      if @ptr && @lib
        NativeLibrary.Free(@ptr)
      end
      0
    end

    def close_enabled?
      @enable_close
    end

    def enable_close
      @enable_close = true
    end

    def disable_close
      @enable_close = false
    end

    def disable_closed_handle_check
      # IronRuby always checks; provided for API compatibility.
      nil
    end

    private

    def try_sym(name)
      raise DLError, "closed handle" unless @open
      if @ptr.nil?
        # Handle::NEXT: RTLD_NEXT semantics are not available.
        raise DLError, "RTLD_NEXT is not supported on this platform"
      end
      begin
        NativeLibrary.GetExport(@ptr, name).ToInt64
      rescue System::EntryPointNotFoundException, System::ArgumentException
        nil
      end
    end
  end

  # Creates a new handler that opens +library+, and returns an instance of
  # Fiddle::Handle.  If +nil+ is given for the +library+, Fiddle::Handle::DEFAULT
  # is used, which is the equivalent to RTLD_DEFAULT.
  def dlopen(library)
    if library.nil?
      Handle::DEFAULT
    else
      Handle.new(library)
    end
  end
  module_function :dlopen

  # malloc(3)/realloc(3)/free(3) on the process heap, as Fiddle exposes them.
  # The addresses are plain Integers; Fiddle::Pointer wraps them.

  def malloc(size)
    Native.malloc(size)
  end

  def realloc(address, size)
    Native.realloc(address, size)
  end

  def free(address)
    Native.free(address)
    nil
  end

  module_function :malloc, :realloc, :free

  ##
  # A pointer into unmanaged memory.
  #
  # Only the parts that do not need a C struct description are here: taking an
  # address, reading and writing bytes through it, and owning a malloc'd block.

  class Pointer
    attr_reader :size
    attr_accessor :free

    def self.malloc(size, freefunc = nil)
      raise ArgumentError, "invalid size: #{size}" if size < 0

      ptr = new(Fiddle.malloc(size), size, freefunc)
      if block_given?
        begin
          yield ptr
        ensure
          ptr.call_free
        end
      else
        ptr
      end
    end

    # Fiddle::Pointer[obj] - the address of +obj+'s bytes.  For a String that
    # means a copy in unmanaged memory: a Ruby string's bytes move with the GC,
    # so its address is not a thing that can be handed out and kept.
    def self.[](value)
      to_ptr(value)
    end

    def self.to_ptr(value)
      case value
      when Pointer then value
      when Integer then new(value)
      when String
        bytes = value.b
        ptr = malloc(bytes.bytesize + 1)
        Native.write(ptr.to_i, bytes)
        ptr
      when nil then new(0)
      else
        if value.respond_to?(:to_ptr)
          result = value.to_ptr
          result.is_a?(Pointer) ? result : new(Integer(result))
        else
          new(Integer(value))
        end
      end
    end

    def initialize(address, size = 0, freefunc = nil)
      @address = address.is_a?(Pointer) ? address.to_i : Integer(address)
      @size = size
      @free = freefunc
      if block_given?
        begin
          yield self
        ensure
          call_free
        end
      end
    end

    def to_i
      @address
    end
    alias to_int to_i

    def size=(size)
      @size = size
    end

    def null?
      @address == 0
    end

    def call_free
      return if @free.nil? || @address == 0

      if @free.respond_to?(:call)
        @free.call(@address)
      else
        Fiddle.free(@address)
      end
      @address = 0
      @freed = true
      nil
    end

    def freed?
      !!@freed
    end

    def +(delta)
      Pointer.new(@address + delta, @size - delta)
    end

    def -(delta)
      Pointer.new(@address - delta, @size + delta)
    end

    # The pointer *stored at* this address, and the address of this pointer.
    # #ref cannot be done for a Ruby-side address, so it is not provided.
    def ptr
      Pointer.new(Native.read(@address, SIZEOF_VOIDP).unpack1("J"))
    end
    alias +@ ptr

    def [](offset, length = nil)
      raise DLError, "NULL pointer dereference" if @address == 0

      if length.nil?
        Native.read(@address + offset, 1).getbyte(0)
      else
        Native.read(@address + offset, length)
      end
    end

    def []=(*args)
      raise DLError, "NULL pointer dereference" if @address == 0

      if args.size == 2
        offset, value = args
        if value.is_a?(String)
          Native.write(@address + offset, value.b)
        else
          Native.write(@address + offset, (Integer(value) & 0xff).chr)
        end
      else
        offset, length, value = args
        bytes = value.is_a?(String) ? value.b : Pointer.to_ptr(value).to_s(length)
        bytes = bytes.byteslice(0, length).to_s
        bytes += "\0".b * (length - bytes.bytesize) if bytes.bytesize < length
        Native.write(@address + offset, bytes)
      end
      value
    end

    def to_s(length = nil)
      if length
        Native.read(@address, length)
      else
        Native.read(@address, Native.strlen(@address))
      end
    end

    def to_str(length = nil)
      to_s(length || @size)
    end

    def to_value
      raise NotImplementedError, "Fiddle::Pointer#to_value is not supported on IronRuby"
    end

    def ==(other)
      other.is_a?(Pointer) && other.to_i == @address
    end
    alias eql? ==

    def <=>(other)
      return nil unless other.is_a?(Pointer)

      @address <=> other.to_i
    end

    def hash
      @address.hash
    end

    def inspect
      "#<#{self.class.name} ptr=#{format("%#x", @address)} size=#{@size} free=#{@free.inspect}>"
    end
  end

  ##
  # A callable foreign function.
  #
  #   libc = Fiddle.dlopen(nil)
  #   atoi = Fiddle::Function.new(libc["atoi"], [Fiddle::TYPE_VOIDP], Fiddle::TYPE_INT)
  #   atoi.call("42")   # => 42

  class Function
    DEFAULT = 0
    STDCALL = 1

    attr_reader :ptr, :args, :return_type, :abi, :name

    def initialize(ptr, args, ret_type, abi = DEFAULT, name: nil, need_gvl: false)
      @ptr = ptr.is_a?(Pointer) ? ptr.to_i : Integer(ptr)
      @args = args.map {|type| Integer(type) }
      @return_type = Integer(ret_type)
      @abi = abi
      @name = name
      @need_gvl = need_gvl

      # The call is made with the basic type the typedef stands for; only the
      # result needs to know it was TYPE_CONST_STRING or TYPE_BOOL.
      @call_args = @args.map {|type| basic_type(type) }
      @call_return = basic_type(@return_type)
    end

    def call(*args, &block)
      if args.size != @args.size
        raise ArgumentError, "wrong number of arguments (given #{args.size}, expected #{@args.size})"
      end

      converted = args.each_with_index.map {|arg, i| convert_argument(@args[i], arg) }
      result = Native.invoke(@ptr, @call_args, @call_return, converted)
      result = convert_result(@return_type, result)
      block ? block.call(result) : result
    end

    def to_i
      @ptr
    end

    def to_proc
      method(:call).to_proc
    end

    private

    def basic_type(type)
      case type
      when TYPE_CONST_STRING then TYPE_VOIDP
      when TYPE_BOOL then TYPE_INT
      else type
      end
    end

    def convert_argument(type, arg)
      case type
      when TYPE_VOIDP, TYPE_CONST_STRING
        case arg
        when nil then 0
        when String then arg
        when Integer then arg
        when Pointer then arg.to_i
        else
          arg.respond_to?(:to_ptr) ? Pointer.to_ptr(arg).to_i : Integer(arg)
        end
      when TYPE_FLOAT, TYPE_DOUBLE
        Float(arg)
      when TYPE_BOOL
        arg && arg != 0 ? 1 : 0
      else
        case arg
        when true then 1
        when false, nil then 0
        when Pointer then arg.to_i
        else Integer(arg)
        end
      end
    end

    def convert_result(type, value)
      case type
      when TYPE_VOIDP then Pointer.new(value)
      when TYPE_CONST_STRING
        value == 0 ? nil : Native.read(value, Native.strlen(value))
      when TYPE_BOOL then value != 0
      else value
      end
    end
  end
end
