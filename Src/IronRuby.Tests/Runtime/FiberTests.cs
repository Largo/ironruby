using System;
using System.Threading;

namespace IronRuby.Tests {
    // A CLR boundary can catch an unwind signal or replace it from finally. The
    // runtime must keep reclaiming until it reaches the fiber worker boundary.
    public static class FiberReclaimInterop {
        private static int _cleanups;
        public static int Cleanups { get { return Volatile.Read(ref _cleanups); } }
        public static void Reset() { _cleanups = 0; }

        public static void Run(Action body, Action after, bool replaceSignal) {
            try {
                try {
                    body();
                } catch (Exception) {
                }
                after();
            } finally {
                Interlocked.Increment(ref _cleanups);
                if (replaceSignal) {
                    throw new InvalidOperationException("CLR cleanup replaced the reclaim signal");
                }
            }
        }
    }

    public partial class Tests {
        public void Fiber_ReclamationClrBoundary1() {
            FiberReclaimInterop.Reset();
            Context.ObjectClass.SetConstant("FiberReclaimInterop", Context.GetClass(typeof(FiberReclaimInterop)));
            TestOutput(@"
require 'ruby4'
require 'weakref'
$reclaim_continued = $reclaim_rescued = $reclaim_ensured = 0

def make_clr_reclaim_fiber(replace)
  Fiber.new do
    begin
      FiberReclaimInterop.run(-> { Fiber.yield :ready }, -> { $reclaim_continued += 1 }, replace)
    rescue Exception
      $reclaim_rescued += 1
    ensure
      $reclaim_ensured += 1
    end
  end
end

def discard_clr_reclaim_fiber(replace)
  fiber = make_clr_reclaim_fiber(replace)
  raise 'not ready' unless fiber.resume == :ready
  [WeakRef.new(fiber), fiber.__state__.__core__]
end

cases = [discard_clr_reclaim_fiber(false), discard_clr_reclaim_fiber(true)]
deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) + 10
loop do
  GC.start
  break if cases.all? { |weak, core| !weak.weakref_alive? && core.status == 'terminated' }
  raise 'CLR boundary retained discarded fiber' if Process.clock_gettime(Process::CLOCK_MONOTONIC) > deadline
  sleep 0.01
end
p [$reclaim_continued, $reclaim_rescued, $reclaim_ensured, FiberReclaimInterop.cleanups]
", "[0, 0, 0, 2]");
        }
    }
}
