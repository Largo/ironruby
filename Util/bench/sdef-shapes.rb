$stdout.sync = true
require 'ostruct'
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; sum = nil
  7.times { t0 = cpu; sum = yield n; d = cpu - t0; best = d if d < best }
  printf "%-16s %9.1f ns  %d\n", name, best * 1e9 / n, sum
end
N = (ENV["N"] || 1000).to_i
def n(c) = [c * N / 1000, 1].max
H = { a: 1, b: 2, c: 3 }
t("sclass", n(20000))   { |n| s = 0; n.times { s += 1 if Object.new.singleton_class }; s }
t("def_o_x", n(20000))  { |n| s = 0; n.times { o = Object.new; def o.x = 1; s += o.x }; s }
t("dsm", n(10000))      { |n| s = 0; n.times { o = Object.new; o.define_singleton_method(:x) { 2 }; s += o.x }; s }
t("class_new", n(20000)) { |n| s = 0; n.times { s += 1 if Class.new }; s }
t("class_new_def", n(10000)) { |n| s = 0; n.times { s += 1 if Class.new { def a; end } }; s }
t("ostruct3", n(3000))  { |n| s = 0; n.times { s += OpenStruct.new(H).c }; s }
t("extend", n(10000))   { |n| s = 0; n.times { s += 1 if Object.new.extend(Comparable) }; s }
t("ostruct3_new", n(3000))  { |n| s = 0; n.times { s += 1 if OpenStruct.new(H) }; s }
t("ostruct_app", n(3000))  { |n| s = 0; n.times { o = OpenStruct.new(H); s += o[:c] + o.to_h.size }; s }
