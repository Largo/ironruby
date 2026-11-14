# Narrowing Enumerator::Lazy: CPU ns per source element, best of 8.
n = 300_000
def m(label, n)
  best = nil; r = nil
  8.times { c = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); r = yield; d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - c; best = d if !best || d < best }
  puts "#{label}: #{(best*1e9/n).round}ns #{r}"
end
m("eager map.select.sum", n) { (1..n).map { |x| x * 2 }.select(&:even?).sum }
m("lazy map.to_a", n) { (1..n).lazy.map { |x| x * 2 }.to_a.size }
m("lazy map.select.to_a", n) { (1..n).lazy.map { |x| x * 2 }.select(&:even?).to_a.size }
m("lazy map.select.first", n) { (1..n).lazy.map { |x| x * 2 }.select(&:even?).first(n).size }
m("lazy each", n) { s = 0; (1..n).lazy.each { |x| s += x }; s }
m("enum_new to_a", n) { Enumerator.new { |y| n.times { |i| y << i } }.to_a.size }
m("enum_new yield", n) { Enumerator.new { |y| n.times { |i| y.yield i } }.to_a.size }
