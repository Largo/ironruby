# usage: micro.rb NAME N -- best-of-3 in-process CPU time per op, plus the answer
require 'monitor'
name, n = ARGV[0], (ARGV[1] || 100000).to_i
work = lambda do
r = 0
case name
when "fiber_resume"  then f = Fiber.new { loop { Fiber.yield 1 } }; n.times { r += f.resume }
when "fiber_new"     then n.times { r += Fiber.new { 1 }.resume }
when "enum_next_arr" then a = (1..n).to_a; e = a.each; n.times { r += e.next }
when "enum_next_gen" then e = Enumerator.new { |y| i = 0; loop { y << (i += 1) } }; n.times { r += e.next }
when "enum_next_map" then e = (1..n).each_slice(1); n.times { r += e.next[0] }
when "lazy"          then r = (1..n).lazy.map { |x| x * 2 }.select(&:even?).first(n / 2).sum
when "yielder_to_a"  then (n/1000).times { r += Enumerator.new { |y| 1000.times { |i| y << i } }.to_a.size }
when "thread_new"    then n.times { r += Thread.new { 1 }.value }
when "mutex"         then m = Mutex.new; n.times { m.synchronize { r += 1 } }
when "monitor"       then m = Monitor.new; n.times { m.synchronize { r += 1 } }
when "queue"         then q = Queue.new; n.times { q.push 1; r += q.pop }
when "sized_queue"   then q = SizedQueue.new(10); n.times { q.push 1; r += q.pop }
when "queue_pp"      then q = Queue.new; t = Thread.new { n.times { |i| q << 1 }; q << nil }; while (x = q.pop); r += x; end; t.join
when "tlocal"        then n.times { Thread.current[:a] = 1; r += Thread.current[:a] }
when "fiber_local"   then n.times { Thread.current.thread_variable_set(:a, 1); r += Thread.current.thread_variable_get(:a) }
when "condvar"       then m = Mutex.new; c = ConditionVariable.new; n.times { m.synchronize { c.signal; r += 1 } }
end
r
end
best = nil; ans = nil
3.times { t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); ans = work.call; d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t; best = d if !best || d < best }
puts "#{name} #{ans} #{(best * 1e9 / n).round(1)}ns/op"
