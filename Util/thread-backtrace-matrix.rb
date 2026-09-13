# What Thread#backtrace answers for a thread that is not the caller.
#
#   ./ir.sh Util/thread-backtrace-matrix.rb
#
# MRI lets any thread read any other thread's stack.  IronRuby built backtraces
# by walking the CLR stack of whoever asked, which answers only for the calling
# thread, so Thread#backtrace was hard-wired to nil for everybody else:
#
#   sleeping thread      : nil
#   thread blocked on a lock : nil
#   thread in a nested call  : nil
#
# Anything that watches another thread's progress through its stack therefore
# never makes progress.  Util/kernel-require-wedge.rb is one such spec, and
# `Thread.pass until t.backtrace ...` is a shape that appears throughout
# rubyspec, so a nil here does not fail an example, it hangs the file.
#
# Expected after the fix: each case prints a real array whose top frame is
# inside the method named in the case.  The frame list is a snapshot of a
# running thread, so the exact depth can vary by a frame; what must not vary is
# that it is an array rather than nil.

$stdout.sync = true

def show(label, thread)
  bt = thread.backtrace
  puts "#{label}: #{bt.inspect}"
end

# 1. A thread parked in sleep, several Ruby frames deep.
q = Queue.new
def deep3(q); q << :ready; sleep 30; end
def deep2(q); deep3(q); end
def deep1(q); deep2(q); end
t = Thread.new { deep1(q) }
q.pop
sleep 0.2
show "nested + sleeping  ", t
locs = t.backtrace_locations
puts "  locations         : #{(locs && locs.map(&:to_s)).inspect}"
t.kill

# 2. A thread blocked trying to take a lock somebody else holds.
m = Mutex.new
m.lock
q2 = Queue.new
t2 = Thread.new { q2 << :ready; m.lock }
q2.pop
sleep 0.2
show "blocked on a mutex ", t2
m.unlock

# 3. The main thread, read from a child.
q3 = Queue.new
main = Thread.current
t3 = Thread.new { q3 << main.backtrace }
puts "main, seen by child: #{q3.pop.inspect}"
t3.join

# 4. A thread that has finished has no stack, in MRI too.
t4 = Thread.new { 1 }
t4.join
show "finished thread    ", t4

# 5. Reading your own is Kernel#caller and always worked.
show "self               ", Thread.current
