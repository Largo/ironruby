# io/nonblock - the O_NONBLOCK flag on a stream.
#
# There is no fcntl underneath here to set the flag on, so this records what it
# was told and answers it back. That is enough for code that sets the flag and
# asks about it, which is what the flag is mostly used for; what it cannot do is
# actually make a read return instead of blocking. Said plainly rather than
# pretended otherwise.

class IO
  def nonblock?
    defined?(@__nonblock__) ? !!@__nonblock__ : false
  end

  def nonblock=(value)
    @__nonblock__ = !!value
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
