# A call through each of Ruby's delegation patterns to a trivial method: SimpleDelegator (method_missing), a
# DelegateClass (generated `def m(...); __getobj__.m(...); end`), Forwardable's def_delegators, and a plain
# method_missing with public_send; with zero and two arguments and with a block. direct is the control.
#
#   ruby Util/perf/ab.rb -r 6 Util/bench/delegate-shapes.rb "base=env IR_CONFIG=Release ../ironruby/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#   ruby Util/perf/ab.rb -i -r 1 -n 20000 Util/bench/delegate-shapes.rb \
#        "base=env IR_CONFIG=Release Util/perf/callgrind/count.sh ../ironruby" "new=env IR_CONFIG=Release Util/perf/callgrind/count.sh ."
#
# Every line prints the sum of its results next to its time; it must be CRuby's.
$stdout.sync = true
require 'delegate'
require 'forwardable'

class DgTarget
  def zero = 1
  def two(a, b) = a
  def blk = yield
end
class DgMM
  def initialize(t) = @target = t
  def method_missing(name, *args, &block) = @target.public_send(name, *args, &block)
  def respond_to_missing?(name, priv = false) = @target.respond_to?(name, priv) || super
end
class DgFw
  extend Forwardable
  def_delegators :@target, :zero, :two, :blk
  def initialize(t) = @target = t
end
DgDC = DelegateClass(DgTarget)

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
# INSTR=1 (Util/perf/ab.rb -i) adds a column: the instructions of a call, from the least of the
# seven sections as the time is. See Util/perf/instructions.rb.
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
T = DgTarget.new
D = { "direct" => T, "sd" => SimpleDelegator.new(T), "dc" => DgDC.new(T), "fw" => DgFw.new(T), "mm" => DgMM.new(T) }
N = (ENV["N"] || 200_000).to_i
D.each do |k, o|
  t("#{k}_zero", N) { |n| i = 0; r = 0; while i < n; r += o.zero; i += 1; end; r }
  t("#{k}_two", N)  { |n| i = 0; r = 0; while i < n; r += o.two(1, 2); i += 1; end; r }
  t("#{k}_blk", N)  { |n| i = 0; r = 0; while i < n; r += o.blk { 1 }; i += 1; end; r }
end
