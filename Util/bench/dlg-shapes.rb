# The parts of app-shapes.rb's `delegate` shape, one shape each.
$stdout.sync = true
require 'delegate'
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
class LoudString < SimpleDelegator
  def upcase_size = __getobj__.upcase.size
end
class Proxy < BasicObject
  def initialize(o) = @o = o
  def method_missing(m, *a, &b) = @o.__send__(m, *a, &b)
end
class Proxy2 < BasicObject
  def initialize(o) = @o = o
  def method_missing(m, *a) = @o.size
end
class Plain
  def initialize(o) = @o = o
  def size = @o.size
  def fwd(*a, &b) = @o.__send__(:index, *a, &b)
end
S = "delegated text"
D = LoudString.new(S)
P = Proxy.new(S); P2 = Proxy2.new(S); PL = Plain.new(S)
t("plain", n(400000))     { |n| s = 0; n.times { s += PL.size }; s }
t("splat_send", n(400000)) { |n| s = 0; n.times { s += PL.fwd("t") }; s }
t("mm_bare", n(200000))   { |n| s = 0; n.times { s += P2.size }; s }
t("mm_send", n(200000))   { |n| s = 0; n.times { s += P.size }; s }
t("getobj", n(400000))    { |n| s = 0; n.times { s += D.__getobj__.size }; s }
t("resp", n(200000))      { |n| s = 0; n.times { s += S.respond_to?(:size, false) ? 1 : 0 }; s }
t("d_size", n(100000))    { |n| s = 0; n.times { s += D.size }; s }
t("d_index", n(100000))   { |n| s = 0; n.times { s += D.index("t") }; s }
t("d_upcase_size", n(200000)) { |n| s = 0; n.times { s += D.upcase_size }; s }
