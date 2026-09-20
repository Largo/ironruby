# frozen_string_literal: true
#
# IronRuby implementation of the Fiddle library core.
#
# Dynamic library loading is implemented on top of
# System.Runtime.InteropServices.NativeLibrary, which performs a real
# dlopen(3)/dlsym(3)/dlclose(3) on POSIX systems (LoadLibrary/GetProcAddress
# on Windows).  Only the handle-related part of Fiddle is provided;
# Fiddle::Function/Closure (foreign function calls) are not implemented.

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
end
