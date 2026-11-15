# Pieces of a lazy step: splat blocks, Yielder#yield / #<<, Enumerator#each with a generator.
n = 300_000
def m(label, n)
  best = nil; r = nil
  8.times { c = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); r = yield; d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - c; best = d if !best || d < best }
  puts "#{label}: #{(best*1e9/n).round}ns #{r}"
end
r = (1..n)
m("range.each plain", n) { s = 0; r.each { |x| s += x }; s }
m("range.each splat", n) { s = 0; r.each { |*v| s += v[0] }; s }
pr = proc { |x| x }
m("proc.call(*v)", n) { s = 0; r.each { |*v| s += pr.call(*v) }; s }
m("proc.call(x)", n) { s = 0; r.each { |x| s += pr.call(x) }; s }
m("enum_new y<<", n) { s = 0; Enumerator.new { |y| r.each { |x| y << x } }.each { |x| s += x }; s }
m("enum_new y.yield", n) { s = 0; Enumerator.new { |y| r.each { |x| y.yield x } }.each { |x| s += x }; s }
m("enum_new y.yield(*v)", n) { s = 0; Enumerator.new { |y| r.each { |*v| y.yield(*v) } }.each { |x| s += x }; s }
m("lazy each", n) { s = 0; r.lazy.each { |x| s += x }; s }
