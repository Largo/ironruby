# frozen_string_literal: false
#
# Ruby 4.0's monitor.rb, with the Monitor class that MRI implements in C
# (ext/monitor/monitor.c) written out in Ruby on top of Thread::Mutex. The
# rest - MonitorMixin and its ConditionVariable - is MRI's lib/monitor.rb as
# it stands, doc comments aside.
#
# Copyright (C) 2001  Shugo Maeda <shugo@ruby-lang.org>
#
# This library is distributed under the terms of the Ruby license.
# You can freely distribute/modify this library.
#

class Monitor
  def initialize
    @mon_mutex = Thread::Mutex.new
    @mon_owner = nil
    @mon_count = 0
  end

  # Enters exclusive section, if it is available; answers whether it did.
  def try_enter
    if @mon_owner != Thread.current
      return false unless @mon_mutex.try_lock
      @mon_owner = Thread.current
      @mon_count = 0
    end
    @mon_count += 1
    true
  end

  # Enters exclusive section.
  def enter
    if @mon_owner != Thread.current
      @mon_mutex.lock
      @mon_owner = Thread.current
      @mon_count = 0
    end
    @mon_count += 1
    nil
  end

  # Leaves exclusive section.
  def exit
    mon_check_owner
    if @mon_count <= 0
      raise ThreadError, "monitor_exit: count:#{@mon_count}"
    end
    @mon_count -= 1
    if @mon_count == 0
      @mon_owner = nil
      @mon_mutex.unlock
    end
    nil
  end

  # Enters exclusive section and executes the block. Leaves the exclusive
  # section automatically when the block exits.
  def synchronize
    enter
    begin
      yield
    ensure
      exit
    end
  end

  # Returns true if this monitor is locked by any thread
  def mon_locked?
    @mon_mutex.locked?
  end

  # Returns true if this monitor is locked by current thread.
  def mon_owned?
    @mon_mutex.locked? && @mon_owner == Thread.current
  end

  def mon_check_owner
    unless @mon_owner == Thread.current
      raise ThreadError, "current fiber not owner"
    end
    nil
  end

  # Releases the monitor for the duration of cond.wait, and takes it back -
  # at the depth it was held - once the wait is over.
  def wait_for_cond(cond, timeout)
    count = @mon_count
    @mon_owner = nil
    @mon_count = 0
    begin
      cond.wait(@mon_mutex, timeout)
      true
    ensure
      @mon_owner = Thread.current
      @mon_count = count
    end
  end
end

module MonitorMixin
  class ConditionVariable
    def wait(timeout = nil)
      @monitor.mon_check_owner
      @monitor.wait_for_cond(@cond, timeout)
    end

    def wait_while
      while yield
        wait
      end
    end

    def wait_until
      until yield
        wait
      end
    end

    def signal
      @monitor.mon_check_owner
      @cond.signal
    end

    def broadcast
      @monitor.mon_check_owner
      @cond.broadcast
    end

    private

    def initialize(monitor) # :nodoc:
      @monitor = monitor
      @cond = Thread::ConditionVariable.new
    end
  end

  def self.extend_object(obj) # :nodoc:
    super(obj)
    obj.__send__(:mon_initialize)
  end

  def mon_try_enter
    @mon_data.try_enter
  end
  # For backward compatibility
  alias try_mon_enter mon_try_enter

  def mon_enter
    @mon_data.enter
  end

  def mon_exit
    mon_check_owner
    @mon_data.exit
  end

  def mon_locked?
    @mon_data.mon_locked?
  end

  def mon_owned?
    @mon_data.mon_owned?
  end

  def mon_synchronize(&b)
    @mon_data.synchronize(&b)
  end
  alias synchronize mon_synchronize

  def new_cond
    unless defined?(@mon_data)
      mon_initialize
      @mon_initialized_by_new_cond = true
    end
    return ConditionVariable.new(@mon_data)
  end

  private

  # Use <tt>extend MonitorMixin</tt> or <tt>include MonitorMixin</tt> instead
  # of this constructor.  Have look at the examples above to understand how to
  # use this module.
  def initialize(...)
    super
    mon_initialize
  end

  # Initializes the MonitorMixin after being included in a class or when an
  # object has been extended with the MonitorMixin
  def mon_initialize
    if defined?(@mon_data)
      if defined?(@mon_initialized_by_new_cond)
        return # already initialized.
      elsif @mon_data_owner_object_id == self.object_id
        raise ThreadError, "already initialized"
      end
    end
    @mon_data = ::Monitor.new
    @mon_data_owner_object_id = self.object_id
  end

  def mon_check_owner
    @mon_data.mon_check_owner
  end
end

class Monitor
  def new_cond
    ::MonitorMixin::ConditionVariable.new(self)
  end

  # for compatibility
  alias try_mon_enter try_enter
  alias mon_try_enter try_enter
  alias mon_enter enter
  alias mon_exit exit
  alias mon_synchronize synchronize
end
