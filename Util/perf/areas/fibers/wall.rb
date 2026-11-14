n = (ARGV[0] || 100000).to_i
f = Fiber.new { loop { Fiber.yield 1 } }
3.times do
  c = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); w = Process.clock_gettime(Process::CLOCK_MONOTONIC)
  r = 0; n.times { r += f.resume }
  puts "wall #{((Process.clock_gettime(Process::CLOCK_MONOTONIC)-w)*1e9/n).round}ns cpu #{((Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)-c)*1e9/n).round}ns"
end
