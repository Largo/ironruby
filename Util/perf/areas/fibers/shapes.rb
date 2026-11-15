# Narrowing a fiber round trip: Ruby Fiber vs two Threads ping-ponging over Queues.
n = (ARGV[0] || 50000).to_i
def m(label, n)
  best = nil; bw = nil
  3.times do
    c = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); w = Process.clock_gettime(Process::CLOCK_MONOTONIC)
    yield
    d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - c; e = Process.clock_gettime(Process::CLOCK_MONOTONIC) - w
    best = d if !best || d < best; bw = e if !bw || e < bw
  end
  puts "#{label}: cpu #{(best*1e9/n).round}ns wall #{(bw*1e9/n).round}ns"
end
f = Fiber.new { loop { Fiber.yield 1 } }
m("fiber", n) { n.times { f.resume } }
a = Queue.new; b = Queue.new
t = Thread.new { loop { b << a.pop } }
m("queue pingpong", n) { n.times { a << 1; b.pop } }
