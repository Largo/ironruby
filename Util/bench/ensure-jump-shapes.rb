# Raise and rescue through 1 and 20 frames whose `ensure` is plain, calls a block, or yields: an ensure clause with a jump
# (a block call, yield, zsuper, return/break/next/retry) is rewritten by the DLR into a catch and a throw again, and keeps the
# backtrace from being deferred (Body.EnsureJumpFinder, GotoFinder; frameDepth -1). us per raise, best of five.
#   IR_CONFIG=Release ./ir.sh Util/bench/ensure-jump-shapes.rb; ruby Util/bench/ensure-jump-shapes.rb
$stdout.sync = true
N = (ENV['N'] || 2000).to_i
def plain(d) = d == 0 ? raise("x") : (begin; plain(d - 1); ensure; @z = 1; end)
def blk(d) = d == 0 ? raise("x") : (begin; blk(d - 1); ensure; [1].each { |v| @z = v }; end)
def yl(d, &b) = d == 0 ? raise("x") : (begin; yl(d - 1, &b); ensure; yield; end)
def none(d) = d == 0 ? raise("x") : none(d - 1)
def run(name, depth)
  best = nil
  5.times do
    t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    N.times { begin; yield depth; rescue; end }
    d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    best = d if best.nil? || d < best
  end
  printf "%-12s depth %2d %8.2f us\n", name, depth, best * 1e6 / N
end
[1, 20].each do |dp|
  run("none", dp) { |d| none(d) }
  run("plain", dp) { |d| plain(d) }
  run("blk", dp) { |d| blk(d) }
  run("yield", dp) { |d| yl(d) { @z = 2 } }
end
