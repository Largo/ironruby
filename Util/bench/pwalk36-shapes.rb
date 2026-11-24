# The parts of app-shapes.rb's `prism_walk` shape, one shape each: the walk of a Prism::Visitor subclass
# over a parsed tree, a plain Visitor, and the node methods it is made of.
$stdout.sync = true
require 'prism'
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
class Counter < Prism::Visitor
  attr_reader :counts
  def initialize; @counts = Hash.new(0); super(); end
  def visit_call_node(node); @counts[:call] += 1; @counts[:block] += 1 if node.block; super; end
  def visit_def_node(node); @counts[:def] += 1; super; end
  def visit_local_variable_read_node(node); @counts[:lvar] += 1; super; end
  def visit_string_node(node); @counts[:string] += node.unescaped.size; super; end
  def visit_integer_node(node); @counts[:int] += node.value; super; end
  def visit_if_node(node); @counts[:if] += 1; super; end
end
SRC = File.read(File.expand_path("app-shapes.rb", __dir__), encoding: "UTF-8").lines
        .then { |l| l[0..l.index { |x| x.start_with?("end") }] }.grep_v(/\A\s*#/).join
TREE = Prism.parse(SRC).value
NODES = []; (q = [TREE]; until q.empty?; x = q.shift; NODES << x; q.concat(x.compact_child_nodes); end)
t("walk", n(400))      { |n| s = 0; n.times { c = Counter.new; TREE.accept(c); s += c.counts.sum { |k, v| v } }; s }
t("plain", n(400))     { |n| s = 0; n.times { TREE.accept(Prism::Visitor.new); s += 1 }; s }
t("each_child", n(400)) { |n| s = 0; n.times { NODES.each { |x| x.each_child_node { |c| s += 1 } } }; s }
t("compact", n(400))   { |n| s = 0; n.times { NODES.each { |x| s += x.compact_child_nodes.size } }; s }
t("type", n(400))      { |n| s = 0; n.times { NODES.each { |x| s += x.type.size } }; s }
t("parse", n(20))      { |n| s = 0; n.times { s += Prism.parse(SRC).value.statements.body.size }; s }
