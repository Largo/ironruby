# The instructions the calling thread has executed in user mode, read from the CPU's counter
# (perf_event_open(2)), for the shape files' `t` (Util/bench/core-small.rb, prelude-cost.rb)
# when Util/perf/ab.rb -i asks for them:
#
#   counter = Instructions.open        # nil where there is no counter to open
#   c0 = counter.read; ...; c1 = counter.read      # nil if the counter was lost meanwhile
#
# Why count at all: on a machine other people are using, the time of the same binary on the
# same shape moves by 10% and more from one process to the next, and what a change to a method's
# entry saves is of that size. The instructions of a timed section do not depend on what else
# is running, on the CPU's frequency or on who has the other hyperthread: the least of seven
# sections came out within 0.3% from process to process under IronRuby and within 0.01% under
# CRuby, with the machine at a load of 3 to 8 on eight CPUs.
#
# Why here and not `perf stat` around the process: a process's count has its start-up, its
# compilation and its warm-up sections in it, and those differ from run to run by more than
# the change does (a shape counted that way, as the difference of a run at N and one at 2N,
# was 4% to 14% apart on one binary). The counter is the thread's, so the JIT's and the
# collector's background threads are not in it, and it is read around each section, so the
# least section can be taken, as it is for the time.
#
# What reading it costs: a read is one read(2) through Fiddle, 3 to 4 microseconds, and the two
# reads of a section with the two readings of the clock between them are 29,000 instructions
# under CRuby and 89,000 under IronRuby - under a tenth of an instruction per call at a
# million calls, where a call is some thousands. It needs no helper in C.
#
# Instructions are not time: a cache miss, a mispredicted branch and a longer dependency chain
# cost time and no instructions, and one engine's count says nothing about another's.
#
# Linux on x86-64 and arm64, through Fiddle, which both CRuby and IronRuby have. The kernel
# refuses the counter to a user who is not root where kernel.perf_event_paranoid is above 2,
# and in most containers and virtual machines; .open is then nil.
module Instructions
  SYS_PERF_EVENT_OPEN = { 'x86_64' => 298, 'aarch64' => 241, 'arm64' => 241 }[RUBY_PLATFORM[/\A[^-]+/]]

  # The counter of the thread that calls this, running from now on.
  def self.open
    return nil unless SYS_PERF_EVENT_OPEN && RUBY_PLATFORM.include?('linux')
    require 'fiddle'
    libc = Fiddle::Handle::DEFAULT
    syscall = Fiddle::Function.new(libc['syscall'],
      [Fiddle::TYPE_LONG, Fiddle::TYPE_VOIDP, Fiddle::TYPE_INT, Fiddle::TYPE_INT, Fiddle::TYPE_INT, Fiddle::TYPE_LONG], Fiddle::TYPE_LONG)
    @read = Fiddle::Function.new(libc['read'], [Fiddle::TYPE_INT, Fiddle::TYPE_VOIDP, Fiddle::TYPE_SIZE_T], Fiddle::TYPE_SSIZE_T)
    # struct perf_event_attr in its first version (64 bytes): type PERF_TYPE_HARDWARE (0), size,
    # config PERF_COUNT_HW_INSTRUCTIONS (1), and of the flags pinned, exclude_kernel and
    # exclude_hv. Pinned, because an event that is not shares the CPU's counters by turns with
    # whatever else is counting (another perf, a profiler) and then holds the instructions of
    # part of a section with nothing to say so. A pinned event that cannot have its counter
    # stops instead, and every read of it from then on is an end of file: .read is nil.
    attr = [0, 64, 1, 0, 0, 0, 0x64, 0, 0, 0].pack('LLQQQQQLLQ')
    # pid 0 and cpu -1: this thread, on whichever CPU it runs
    @fd = syscall.call(SYS_PERF_EVENT_OPEN, attr, 0, -1, -1, 0)
    return nil if @fd < 0
    @buffer = Fiddle::Pointer.malloc(8)
    read && self
  rescue StandardError, LoadError, NotImplementedError
    nil
  end

  # The count so far, or nil once the kernel has taken the counter away (see `pinned` above).
  def self.read
    return nil unless @read.call(@fd, @buffer, 8) == 8
    @buffer[0, 8].unpack1('Q')
  end
end
