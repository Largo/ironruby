# io/nonblock - the O_NONBLOCK descriptor flag.
#
# Where the stream has an operating system descriptor this asks the kernel, so
# it gives the right answer for a descriptor nobody here opened. Not every
# stream has one: IronRuby's IO.pipe is not backed by a FileStream, so
# IO#GetNativeDescriptor answers -1 for a pipe and there is no flag to read. For
# those the value is remembered instead, which is a weaker answer.
#
# A pipe is remembered too, even with a descriptor: MRI opens its pipes
# non-blocking, so a pipe reports true until told otherwise, but the
# descriptor itself stays blocking - the .NET stream reading it expects that,
# and IO's own reads and writes wait either way.

class IO
  # Linux values, the same ones IO#fcntl uses.
  NONBLOCK_GET__ = 3   # F_GETFL
  NONBLOCK_SET__ = 4   # F_SETFL
  NONBLOCK_FLAG__ = 0x800 # O_NONBLOCK

  def __nonblock_native__?
    (::IO.GetNativeDescriptor(self) rescue -1) >= 0
  end
  private :__nonblock_native__?

  def __nonblock_pipe__?
    stat.pipe? rescue false
  end
  private :__nonblock_pipe__?

  def nonblock?
    if __nonblock_pipe__?
      defined?(@__nonblock__) ? !!@__nonblock__ : true
    elsif __nonblock_native__?
      (fcntl(NONBLOCK_GET__, 0) & NONBLOCK_FLAG__) != 0
    else
      defined?(@__nonblock__) ? !!@__nonblock__ : false
    end
  end

  def nonblock=(value)
    if !__nonblock_pipe__? && __nonblock_native__?
      flags = fcntl(NONBLOCK_GET__, 0)
      flags = value ? (flags | NONBLOCK_FLAG__) : (flags & ~NONBLOCK_FLAG__)
      fcntl(NONBLOCK_SET__, flags)
    end
    @__nonblock__ = !!value
    value
  end

  # With a block, sets the flag for the duration and puts it back afterwards.
  def nonblock(value = true)
    return (self.nonblock = value) unless block_given?
    previous = nonblock?
    self.nonblock = value
    begin
      yield
    ensure
      self.nonblock = previous
    end
  end
end
