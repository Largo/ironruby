# frozen_string_literal: true
#
# CRuby's timeout (Src/StdLib/ruby/4.0/timeout.rb), loaded as it is, with one IronRuby adjustment.
#
# Every Timeout.timeout queues a request for the watcher thread and signals its condition variable,
# so the watcher wakes once per call: it drains the queue, finds the closest deadline again and goes
# back to waiting. In CRuby that costs about a microsecond. Here the watcher's wake-up runs beside the
# calling thread and costs it too: a Timeout.timeout whose block raises took 145 us of CPU against
# 57 us with the signal left out, one that returns 28 against 4 (Util/bench/timeout-shapes.rb).
#
# The signal is needed only when the watcher would otherwise sleep past the new request's deadline.
# The watcher publishes the deadline it is waiting for (@watcher_deadline, under @queue_mutex, set
# while it is in the condition variable's wait), and a new request signals it only if its own deadline
# is earlier, if the watcher is not waiting there (blocked in @queue.pop, which the push wakes, or
# between two waits, where it looks at the queue under the mutex before waiting again), or if the
# queue has grown to QUEUE_SIGNAL_LENGTH requests, so that a long-lived wait does not let finished
# requests pile up. A request not signalled for has a deadline no earlier than the one the watcher
# wakes for, and the watcher drains the queue then, before any of them can be due: every timeout
# still fires when CRuby's would.
require "rbconfig"
load File.join(RbConfig::CONFIG["rubylibdir"], "timeout.rb")

module Timeout
  class State
    QUEUE_SIGNAL_LENGTH = 64
    private_constant :QUEUE_SIGNAL_LENGTH

    def create_timeout_thread
      # Threads unexpectedly inherit the interrupt mask: https://github.com/ruby/timeout/issues/41
      # So reset the interrupt mask to the default one for the timeout thread
      Thread.handle_interrupt(Object => :immediate) do
        watcher = Thread.new do
          requests = []
          while true
            until @queue.empty? and !requests.empty? # wait to have at least one request
              req = @queue.pop
              requests << req unless req.done?
            end
            closest_deadline = requests.min_by(&:deadline).deadline

            now = 0.0
            @queue_mutex.synchronize do
              begin
                @watcher_deadline = closest_deadline
                while (now = GET_TIME.call(Process::CLOCK_MONOTONIC)) < closest_deadline and @queue.empty?
                  @condvar.wait(@queue_mutex, closest_deadline - now)
                end
              ensure
                @watcher_deadline = nil
              end
            end

            requests.each do |req|
              req.interrupt if req.expired?(now)
            end
            requests.reject!(&:done?)
          end
        end

        if !watcher.group.enclosed? && (!defined?(Ractor.main?) || Ractor.main?)
          ThreadGroup::Default.add(watcher)
        end

        watcher.name = "Timeout stdlib thread"
        watcher.thread_variable_set(:"\0__detached_thread__", true)
        watcher
      end
    end

    def add_request(request)
      Sync.synchronize @queue_mutex do
        @queue << request
        waiting_for = @watcher_deadline
        if waiting_for.nil? || request.deadline < waiting_for || @queue.size >= QUEUE_SIGNAL_LENGTH
          @condvar.signal
        end
      end
    end
  end
end
