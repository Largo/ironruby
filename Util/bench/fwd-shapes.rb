# The calls of app-shapes.rb's `forwardable` shape, one kind of forwarded call per shape, each
# beside the same call written by hand as a control: what a Forwardable delegator costs over it.
$stdout.sync = true
require 'forwardable'
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

class Fwd
  extend Forwardable
  def_delegators :@songs, :size, :first, :map
  def_delegator :@meta, :fetch, :meta
  def_delegator :songs, :last
  attr_reader :songs
  def initialize; @songs = (0...20).to_a; @meta = { public: true, owner: "me" }; end
end
class Hand
  attr_reader :songs
  def initialize; @songs = (0...20).to_a; @meta = { public: true, owner: "me" }; end
  def size = @songs.size
  def first = @songs.first
  def map(&) = @songs.map(&)
  def meta(k) = @meta.fetch(k)
  def last = songs.last
end
# the same as Forwardable's, written out: `...' and the defined? guard
class Gen
  def initialize; @songs = (0...20).to_a; end
  def size(...)
    _ = @songs
    if defined?(_.size)
      _.size(...)
    else
      _.__send__(:size, ...)
    end
  end
  def size_d(...) = @songs.size(...)
  def size_r(*a, &b) = @songs.size(*a, &b)
end
F = Fwd.new; H = Hand.new; G = Gen.new

t("f_noarg", n(400000))  { |n| s = 0; n.times { s += F.size }; s }
t("h_noarg", n(400000))  { |n| s = 0; n.times { s += H.size }; s }
t("f_arg", n(400000))    { |n| s = 0; n.times { s += F.meta(:owner).size }; s }
t("h_arg", n(400000))    { |n| s = 0; n.times { s += H.meta(:owner).size }; s }
t("f_block", n(100000))  { |n| s = 0; n.times { s += F.map { |x| x }.size }; s }
t("h_block", n(100000))  { |n| s = 0; n.times { s += H.map { |x| x }.size }; s }
t("f_method", n(400000)) { |n| s = 0; n.times { s += F.last }; s }
t("h_method", n(400000)) { |n| s = 0; n.times { s += H.last }; s }
t("g_guard", n(400000))  { |n| s = 0; n.times { s += G.size }; s }
t("g_dots", n(400000))   { |n| s = 0; n.times { s += G.size_d }; s }
t("g_rest", n(400000))   { |n| s = 0; n.times { s += G.size_r }; s }
