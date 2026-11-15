# ruby-bench gvl_release_acquire's loop, CPU per read+write pair, best of 5
output = File.open("/dev/null", "wb"); input = File.open("/dev/zero", "rb")
n = 100_000; best = nil
5.times { c = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); n.times { output.write(input.read(10)) }; d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - c; best = d if !best || d < best }
puts "gvl #{(best*1e9/n).round}ns/iter"
