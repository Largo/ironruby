# The parts of one OpenStruct.new + reads + writes (the `ostruct` shape of app-shapes.rb), for
# gap 32: shape names as arguments, N from ENV, `<shape> <ns> ns <checksum>` per line.
$stdout.sync = true
require 'ostruct'
def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
INSTR = ENV["INSTR"] && (require_relative "../perf/instructions"; Instructions.open || warn("no instruction counter (perf_event_open)"))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; fewest = nil; counted = !!INSTR; sum = nil
  7.times do
    c0 = INSTR && INSTR.read; t0 = cpu; sum = yield n; d = cpu - t0; c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end
N = (ENV["N"] || 1000).to_i
def n(c) = [c * N / 1000, 1].max
H = { name: "row", qty: 2, price: 4 }
class Rd
  def initialize = @table = { a: 1 }
end
# The four parts of one ostruct object: making it, the first read of each reader (a new
# method on a new singleton class), a write through method_missing, the other calls.
t("os_new", n(3000))      { |n| s = 0; n.times { s += 1 if OpenStruct.new(H) }; s }
t("os_read1", n(3000))    { |n| s = 0; n.times { o = OpenStruct.new(H); s += o.qty }; s }
t("os_read3", n(3000))    { |n| s = 0; n.times { o = OpenStruct.new(H); s += o.qty + o.price + o.name.size }; s }
t("os_write", n(3000))    { |n| s = 0; n.times { o = OpenStruct.new(H); o.total = 1; s += o.total }; s }
t("os_app", n(3000))      { |n| s = 0; n.times { o = OpenStruct.new(H); o.total = o.qty * o.price
                                  s += o.total + o.name.size + o.to_h.size + (o.respond_to?(:total) ? 1 : 0) + o[:qty] }; s }
# The mechanism alone: a reader made by define_singleton_method from a block, and called once.
t("dsm_blk", n(10000))    { |n| s = 0; pr = proc { 2 }; n.times { o = Object.new; o.define_singleton_method(:x, &pr); s += 1 }; s }
t("dsm_blk_call", n(10000)) { |n| s = 0; pr = proc { 2 }; n.times { o = Object.new; o.define_singleton_method(:x, &pr); s += o.x }; s }
t("dsm_tbl_call", n(10000)) { |n| s = 0; n.times { o = Rd.new; nm = :a; o.define_singleton_method(:x, &Proc.new { @table[nm] }); s += o.x }; s }
t("dsm_tbl_call2", n(10000)) { |n| s = 0; n.times { o = Rd.new; nm = :a; o.define_singleton_method(:x, &Proc.new { @table[nm] }); s += o.x + o.x }; s }
