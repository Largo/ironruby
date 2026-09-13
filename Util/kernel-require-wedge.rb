# Why `mspec-run spec/core/kernel` never finishes.
#
#   ./ir.sh Util/kernel-require-wedge.rb
#
# The directory wedges on one example, spec/core/kernel/require_spec.rb ->
# spec/core/kernel/shared/require.rb:667, "blocks a second thread from
# returning while the 1st is still requiring".  It is a deadlock, not
# slowness: a busy-wait with no timeout cannot be waited out, so raising
# mspec's cap does not help and never will.
#
# This script is that example with the two busy-waits bounded and instrumented,
# so it reports which thread is stuck and on which term instead of hanging.
# Run against this tree it prints:
#
#   events            : [:con_pre, :set_thread_local,
#                        [:t1_gave_up, {other_backtrace: "nil", other_stop?: true}],
#                        :con_post]
#   t2 saw t1's thread-local : true
#   t1_res=true t2_res=false
#
# Read that as:
#
#   * :set_thread_local fired, and t2 saw it - so Thread.current[:key] IS
#     visible across threads.  That is the natural first guess and it is wrong.
#   * t1_res=true, t2_res=false, other_stop?=true - so require's cross-thread
#     lock works exactly as CRuby's: t2 really did block inside require and
#     really did get false.  Also not the cause.
#   * The thread that spins forever is t1, not t2, and it spins inside the
#     fixture spec/fixtures/code/concurrent.rb on this line:
#
#         Thread.pass until t.backtrace && t.backtrace.any? { |c| c.include? 'require' } && t.stop?
#
#     Every term is satisfied except the first: Thread#backtrace answers nil
#     for any thread other than the caller.
#
# Is that fixable?  Not without changing how IronRuby represents a call stack.
# Backtraces here are built lazily by walking the CLR stack when one is asked
# for (RubyExceptionData.CreateBacktrace); there is no per-thread list of Ruby
# frames to read from another thread, and .NET Core removed the only APIs that
# could capture another thread's managed stack (Thread.Suspend,
# StackTrace(Thread)).  Supporting it would mean pushing and popping a frame
# record on every Ruby method entry and exit - the per-call cost the lazy
# design exists to avoid.  The Thread reopening in Src/StdLib/ironruby/ruby4.rb
# returns nil deliberately for that reason.
#
# So: spec/core/kernel has to be swept per file (Util/spec-sweep.sh), and this
# is exactly why.
#
require 'thread'
$stdout.sync = true

FIX = "/tmp/kn/concurrent_probe.rb"
File.write(FIX, <<'FIXTURE')
$probe << :con_pre
Thread.current[:in_concurrent_rb] = true
$probe << :set_thread_local

if t = Thread.current[:wait_for]
  spins = 0
  until (t.backtrace && t.backtrace.any? { |c| c.include?('require') } && t.stop?)
    spins += 1
    if spins > 300_000
      $probe << [:t1_gave_up, :other_backtrace => t.backtrace.inspect, :other_stop? => t.stop?]
      break
    end
    Thread.pass
  end
end
$probe << :con_post
FIXTURE

$probe = Queue.new
fin = false
t1_res = t2_res = nil
t2 = nil

t1 = Thread.new do
  Thread.pass until t2
  Thread.current[:wait_for] = t2
  t1_res = require(FIX)
  n = 0
  until fin
    break if (n += 1) > 300_000
    Thread.pass
  end
end

t2_saw_local = false
t2 = Thread.new do
  n = 0
  until t1[:in_concurrent_rb]
    if (n += 1) > 300_000
      $probe << :t2_never_saw_thread_local
      break
    end
    Thread.pass
  end
  t2_saw_local = !!t1[:in_concurrent_rb]
  begin
    t2_res = require(FIX)
  ensure
    fin = true
  end
end

t1.join(60) or puts "t1 DID NOT FINISH"
t2.join(60) or puts "t2 DID NOT FINISH"

events = []
events << $probe.pop until $probe.empty?
puts "events            : #{events.inspect}"
puts "t2 saw t1's thread-local : #{t2_saw_local}"
puts "t1_res=#{t1_res.inspect} t2_res=#{t2_res.inspect}"
