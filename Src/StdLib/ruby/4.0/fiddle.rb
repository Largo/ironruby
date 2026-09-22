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
  TYPE_VARIADIC  = 9
  TYPE_CONST_STRING = 10
  TYPE_BOOL      = 11

  # The C99 typedefs CRuby's fiddle exposes as their own type numbers; each is
  # an alias for whichever fixed-width type has the same size and signedness.
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

  TYPE_SSIZE_T   = SIZEOF_SIZE_T == SIZEOF_LONG ? TYPE_LONG :
                   SIZEOF_SIZE_T == SIZEOF_LONG_LONG ? TYPE_LONG_LONG : TYPE_INT
  TYPE_SIZE_T    = -TYPE_SSIZE_T
  TYPE_PTRDIFF_T = TYPE_SSIZE_T
  TYPE_INTPTR_T  = TYPE_SSIZE_T
  TYPE_UINTPTR_T = -TYPE_SSIZE_T

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

  # A block of unmanaged memory, addressed by an integer.
  #
  # Only the part of CRuby's Fiddle::Pointer that does not need a C extension
  # is here: an address, a size, reading and writing bytes through it, and
  # malloc/free over Marshal.AllocHGlobal.  Pointer.[] on a String copies the
  # string's bytes into freshly allocated memory, which is what an argument
  # marshalled for TYPE_VOIDP needs.
  class Pointer
    attr_accessor :size

    def self.malloc(size, free = nil)
      ptr = new(Marshal_.AllocHGlobal(System::IntPtr.new(size)).ToInt64, size, free)
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

    def self.to_ptr(value)
      case value
      when Pointer then value
      when String then self[value]
      when Integer then new(value)
      when nil then new(0)
      else
        return to_ptr(value.to_ptr) if value.respond_to?(:to_ptr)
        new(Integer(value))
      end
    end

    # Fiddle::Pointer[str] - unmanaged memory holding a copy of str's bytes.
    def self.[](value)
      return to_ptr(value) unless value.is_a?(String)

      bytes = value.b
      ptr = malloc(bytes.bytesize + 1)
      ptr[0, bytes.bytesize] = bytes
      ptr.size = bytes.bytesize
      ptr
    end

    def initialize(address, size = 0, free = nil)
      @address = Integer(address)
      @size = size
      @free = free
      @freed = false
    end

    def to_i
      @address
    end
    alias to_int to_i

    def null?
      @address.zero?
    end

    def +(offset) = Pointer.new(@address + offset, @size - offset)
    def -(offset) = Pointer.new(@address - offset, @size + offset)
    def ==(other) = other.is_a?(Pointer) && other.to_i == @address
    alias eql? ==
    def hash = @address.hash
    def <=>(other) = other.is_a?(Pointer) ? @address <=> other.to_i : nil

    def inspect
      "#<#{self.class.name} ptr=0x%016x size=%d free=%s>" % [@address, @size, @free.inspect]
    end

    # to_s()     - the bytes up to the first NUL
    # to_s(len)  - the first +len+ bytes
    def to_s(len = nil)
      return read_bytes(len) if len
      out = +"".b
      i = 0
      loop do
        byte = Marshal_.ReadByte(System::IntPtr.new(@address), i)
        break if byte.zero?
        out << byte
        i += 1
      end
      out
    end

    def to_str(len = nil)
      len ? read_bytes(len) : read_bytes(@size)
    end

    def [](offset, len = nil)
      return Marshal_.ReadByte(System::IntPtr.new(@address), offset) & 0xff unless len
      read_bytes(len, offset)
    end

    def []=(offset, len_or_value, value = nil)
      if value.nil?
        Marshal_.WriteByte(System::IntPtr.new(@address), offset,
                           System::Byte.new(Integer(len_or_value) & 0xff))
        return len_or_value
      end
      bytes = value.b
      len = len_or_value
      len.times do |i|
        byte = i < bytes.bytesize ? bytes.getbyte(i) : 0
        Marshal_.WriteByte(System::IntPtr.new(@address), offset + i, System::Byte.new(byte))
      end
      value
    end

    def call_free
      return if @freed || @address.zero?
      @freed = true
      if @free
        @free.respond_to?(:call) ? @free.call(self) : Marshal_.FreeHGlobal(System::IntPtr.new(@address))
      else
        Marshal_.FreeHGlobal(System::IntPtr.new(@address))
      end
      nil
    end

    def freed? = @freed

    private

    def read_bytes(len, offset = 0)
      out = +"".b
      len.times {|i| out << (Marshal_.ReadByte(System::IntPtr.new(@address), offset + i) & 0xff) }
      out
    end
  end

  NULL = Pointer.new(0)

  ##
  # A foreign function: an address, the types of its arguments and the type of
  # its result, callable with #call.
  #
  # CRuby builds the call frame with libffi.  There is no libffi here, so the
  # call is emitted as IL instead: a DynamicMethod whose body is a single
  # `calli` against the unmanaged calling convention, which is exactly what the
  # CLR's own P/Invoke marshaller emits for a blittable signature.  One method
  # is emitted per distinct signature and cached, so a Function called in a loop
  # pays for the emit once.
  #
  # Arguments are converted the way CRuby's Fiddle does: an Integer or a Pointer
  # for TYPE_VOIDP becomes an address, and a String becomes a pointer to a copy
  # of its bytes which is copied back into the String after the call - so a C
  # function that fills a buffer fills the Ruby String, as it does on CRuby.
  class Function
    DEFAULT = 0
    STDCALL = 1
    CDECL = 0

    attr_reader :ptr, :args, :return_type, :abi, :name

    def initialize(ptr, args, ret_type, abi = DEFAULT, name: nil, need_gvl: false)
      @ptr = ptr.respond_to?(:to_i) ? ptr.to_i : Integer(ptr)
      @args = args.map {|t| Integer(t) }
      @return_type = Integer(ret_type)
      @abi = abi
      @name = name
      @need_gvl = need_gvl
      @caller = Function.emitter(@return_type, @args, @abi)
    end

    def to_i = @ptr
    def to_proc = method(:call).to_proc

    def call(*argv, &block)
      raise ArgumentError, "wrong number of arguments (given #{argv.size}, expected #{@args.size})" if argv.size != @args.size

      pinned = []
      clr = []
      @args.each_with_index do |type, i|
        clr << Function.marshal_in(type, argv[i], pinned)
      end
      begin
        result = @caller.call(clr, @ptr)
      ensure
        pinned.each(&:call)
      end
      block ? block.call(result) : result
    end

    # --- IL emission ------------------------------------------------------

    Emit_ = System::Reflection::Emit
    Marshal_ = System::Runtime::InteropServices::Marshal
    private_constant :Emit_, :Marshal_

    # System::Int64 and friends are the *Ruby* classes those CLR types map to -
    # System::Int64 is Integer, whose to_clr_type is System.Int32 - so the
    # types the signature is built from have to be looked up by name.
    def self.clr(name)
      System::Type.GetType(name) or raise TypeError, "no CLR type #{name}"
    end

    CLR_TYPES = {
      TYPE_VOID => clr("System.Void"),
      TYPE_CHAR => clr("System.SByte"),
      TYPE_UCHAR => clr("System.Byte"),
      TYPE_SHORT => clr("System.Int16"),
      TYPE_USHORT => clr("System.UInt16"),
      TYPE_INT => clr("System.Int32"),
      TYPE_UINT => clr("System.UInt32"),
      TYPE_LONG => clr(SIZEOF_LONG == 8 ? "System.Int64" : "System.Int32"),
      TYPE_ULONG => clr(SIZEOF_LONG == 8 ? "System.UInt64" : "System.UInt32"),
      TYPE_LONG_LONG => clr("System.Int64"),
      TYPE_ULONG_LONG => clr("System.UInt64"),
      TYPE_FLOAT => clr("System.Single"),
      TYPE_DOUBLE => clr("System.Double"),
      TYPE_VOIDP => clr("System.IntPtr"),
      TYPE_CONST_STRING => clr("System.IntPtr"),
      TYPE_BOOL => clr("System.Byte"),
    }.freeze
    private_constant :CLR_TYPES

    def self.clr_type(type)
      CLR_TYPES[type] or raise TypeError, "unknown fiddle type #{type.inspect}"
    end

    # The IL conversion that turns the uniform Int64/Double the emitted method
    # takes into the type the native signature declares, and back again.
    NARROW_IN = {
      TYPE_CHAR => :Conv_I1, TYPE_UCHAR => :Conv_U1,
      TYPE_SHORT => :Conv_I2, TYPE_USHORT => :Conv_U2,
      TYPE_INT => :Conv_I4, TYPE_UINT => :Conv_U4,
      TYPE_LONG_LONG => :Conv_I8, TYPE_ULONG_LONG => :Conv_U8,
      TYPE_VOIDP => :Conv_I, TYPE_CONST_STRING => :Conv_I, TYPE_BOOL => :Conv_U1,
      TYPE_FLOAT => :Conv_R4, TYPE_DOUBLE => nil,
      TYPE_LONG => (SIZEOF_LONG == 8 ? :Conv_I8 : :Conv_I4),
      TYPE_ULONG => (SIZEOF_LONG == 8 ? :Conv_U8 : :Conv_U4),
    }.freeze
    WIDEN_OUT = {
      TYPE_VOID => nil,
      TYPE_CHAR => :Conv_I8, TYPE_UCHAR => :Conv_U8,
      TYPE_SHORT => :Conv_I8, TYPE_USHORT => :Conv_U8,
      TYPE_INT => :Conv_I8, TYPE_UINT => :Conv_U8,
      TYPE_LONG_LONG => nil, TYPE_ULONG_LONG => nil,
      TYPE_VOIDP => :Conv_I8, TYPE_CONST_STRING => :Conv_I8, TYPE_BOOL => :Conv_U8,
      TYPE_FLOAT => :Conv_R8, TYPE_DOUBLE => nil,
      TYPE_LONG => (SIZEOF_LONG == 8 ? nil : :Conv_I8),
      TYPE_ULONG => (SIZEOF_LONG == 8 ? nil : :Conv_U8),
    }.freeze
    private_constant :NARROW_IN, :WIDEN_OUT

    @emitters = {}
    @emitters_mutex = Mutex.new

    # Returns an object answering #call(args, function_pointer).
    def self.emitter(ret_type, arg_types, _abi)
      key = [ret_type, *arg_types]
      @emitters_mutex.synchronize do
        @emitters[key] ||= build_emitter(ret_type, arg_types)
      end
    end

    # The emitted method's own signature is deliberately not the native one:
    # every integer and pointer argument arrives as Int64 and every floating
    # point one as Double, and the result comes back the same way.  Reflection's
    # Invoke would have to be handed a box of the exact native type otherwise -
    # a System::UInt16, say - and a Ruby Integer cannot produce one.  Widening
    # Int32 to Int64 is a conversion the default binder does perform, so this
    # shape is reachable from Ruby, and the narrowing back to the declared type
    # happens in IL, where it is free.
    def self.build_emitter(ret_type, arg_types)
      float = ->(t) { t == TYPE_FLOAT || t == TYPE_DOUBLE }
      i8 = clr("System.Int64")
      r8 = clr("System.Double")

      native_sig = System::Array[System::Type].new(arg_types.map {|t| clr_type(t) })
      method_sig = System::Array[System::Type].new(
        arg_types.map {|t| float.(t) ? r8 : i8 } + [i8])
      shim_ret = ret_type == TYPE_VOID ? clr_type(TYPE_VOID) : (float.(ret_type) ? r8 : i8)

      dm = Emit_::DynamicMethod.new("fiddle_calli", shim_ret, method_sig)
      il = dm.GetILGenerator
      arg_types.each_with_index do |type, i|
        # Ldarg's operand is a 2-byte index: pass a short so ILGenerator picks
        # Emit(OpCode, short) rather than the 4-byte Emit(OpCode, int) overload.
        il.Emit(Emit_::OpCodes.Ldarg, System::Int16.new(i))
        op = NARROW_IN.fetch(type) { raise TypeError, "unknown fiddle type #{type.inspect}" }
        il.Emit(Emit_::OpCodes.send(op)) if op
      end
      il.Emit(Emit_::OpCodes.Ldarg, System::Int16.new(arg_types.length))
      il.Emit(Emit_::OpCodes.Conv_I)
      il.EmitCalli(Emit_::OpCodes.Calli,
                   System::Runtime::InteropServices::CallingConvention.Cdecl,
                   clr_type(ret_type), native_sig)
      out = WIDEN_OUT.fetch(ret_type) { raise TypeError, "unknown fiddle type #{ret_type.inspect}" }
      il.Emit(Emit_::OpCodes.send(out)) if out
      il.Emit(Emit_::OpCodes.Ret)

      Emitter.new(dm, ret_type, arg_types.length)
    end
    private_class_method :build_emitter

    class Emitter # :nodoc:
      def initialize(dynamic_method, ret_type, arity)
        @dm = dynamic_method
        @ret_type = ret_type
        @arity = arity
      end

      def call(args, fn_ptr)
        boxed = System::Array[System::Object].new(args + [fn_ptr])
        Function.marshal_out(@ret_type, @dm.Invoke(nil, boxed))
      end
    end
    private_constant :Emitter

    # --- argument and result conversion -----------------------------------

    # Every argument leaves here as a Ruby Integer that fits in Int64, or a
    # Float; the emitted method narrows it to the declared type.
    def self.marshal_in(type, value, pinned)
      case type
      when TYPE_VOIDP, TYPE_CONST_STRING then pointer_argument(value, pinned)
      when TYPE_BOOL then value ? 1 : 0
      when TYPE_FLOAT, TYPE_DOUBLE then Float(value)
      when TYPE_CHAR then int_of(value, 8, true)
      when TYPE_UCHAR then int_of(value, 8, false)
      when TYPE_SHORT then int_of(value, 16, true)
      when TYPE_USHORT then int_of(value, 16, false)
      when TYPE_INT then int_of(value, 32, true)
      when TYPE_UINT then int_of(value, 32, false)
      when TYPE_LONG, TYPE_ULONG
        int_of(value, SIZEOF_LONG * 8, SIZEOF_LONG == 8 || type == TYPE_LONG)
      when TYPE_LONG_LONG, TYPE_ULONG_LONG
        # Both halves of the 64-bit range map onto the same Int64 bits.
        int_of(value, 64, true)
      else
        raise TypeError, "unsupported fiddle argument type #{type.inspect}"
      end
    end

    def self.int_of(value, bits, signed)
      i = value.respond_to?(:to_int) ? value.to_int : Integer(value)
      i &= (1 << bits) - 1
      i -= (1 << bits) if signed && i >= (1 << (bits - 1))
      i
    end
    private_class_method :int_of

    # A String argument is passed as a pointer to a pinned copy of its bytes,
    # and the copy is written back afterwards so that an out parameter reaches
    # Ruby - which is what CRuby's Fiddle does with the string's own buffer.
    def self.pointer_argument(value, pinned)
      case value
      when nil then 0
      when String
        bytes = System::Array[System::Byte].new(value.bytesize)
        value.bytesize.times {|i| bytes[i] = value.getbyte(i) }
        handle = System::Runtime::InteropServices::GCHandle.Alloc(
          bytes, System::Runtime::InteropServices::GCHandleType.Pinned)
        target = value
        pinned << lambda do
          unless target.frozen?
            target.bytesize.times {|i| target.setbyte(i, bytes[i] & 0xff) }
          end
          handle.Free
        end
        handle.AddrOfPinnedObject.ToInt64
      when Pointer then value.to_i
      when Integer then value
      else
        return pointer_argument(value.to_ptr, pinned) if value.respond_to?(:to_ptr)
        Integer(value)
      end
    end
    private_class_method :pointer_argument

    def self.marshal_out(type, result)
      case type
      when TYPE_VOID then nil
      when TYPE_BOOL then Integer(result) != 0
      when TYPE_CONST_STRING
        i = Integer(result)
        i.zero? ? nil : Pointer.new(i.negative? ? i + (1 << 64) : i).to_s
      when TYPE_FLOAT, TYPE_DOUBLE then Float(result)
      when TYPE_UCHAR, TYPE_USHORT, TYPE_UINT then Integer(result)
      when TYPE_ULONG, TYPE_ULONG_LONG, TYPE_VOIDP
        # conv.u8 of a 64-bit result is a no-op, so an address or an unsigned
        # long comes back with its sign bit set; put it back in range.
        i = Integer(result)
        i.negative? ? i + (1 << 64) : i
      else Integer(result)
      end
    end
  end

  Marshal_ = System::Runtime::InteropServices::Marshal
  private_constant :Marshal_

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
