# Where ERB#result's time goes (perf/erb2): the parts of app-shapes.rb's erb_result, one template
# render per unit. erb_full is result_with_hash; erb_eval evals the template's src in a fresh
# binding (parse, bridge, transform, compile and run); erb_def runs the same src as a method
# defined once (the body alone); erb_toplevel is new_toplevel + local_variable_set (the binding);
# erb_new is ERB.new(t).src (the template compiler); eval_small is eval('1 + 1', fresh binding);
# erb_eval_top evals the src in a copy of TOPLEVEL_BINDING, as #result does (perf/erb36: the
# compiled code of the text is kept, EvalCodeCache).
#
#   IR_CONFIG=Release ./ir.sh Util/bench/erb-shapes.rb [shape ...]
#   ruby Util/bench/erb-shapes.rb
$stdout.sync = true
require 'erb'
require 'zlib'
def crc(s) = Zlib.crc32(s)

PAGE = <<~'EOS'
  <h1><%= title %></h1>
  <ul>
  <% items.each do |item| -%>
    <li class="<%= item[:done] ? 'done' : 'open' %>"><%= ERB::Util.html_escape(item[:text]) %> (<%= item[:n] %>)</li>
  <% end -%>
  </ul>
  <p>Total: <%= items.sum { |i| i[:n] } %></p>
EOS
ITEMS = (1..40).map { |i| { text: "Task <#{i}> & more", n: i * 3, done: i.even? } }.freeze
ERBT = ERB.new(PAGE, trim_mode: '-')
SRC = ERBT.src
class Body; end
Body.class_eval("def self.run(title, items)\n#{SRC}\nend")
def fresh_top(title) = (b = TOPLEVEL_BINDING.dup; b.local_variable_set(:title, title); b.local_variable_set(:items, ITEMS); b)
def fresh(title) = (b = binding; b.local_variable_set(:title, title); b.local_variable_set(:items, ITEMS); b)

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
# INSTR=1 (Util/perf/ab.rb -i) adds a column: the instructions of a unit, from the least of the seven sections.
INSTR = ENV['INSTR'] && (require_relative '../perf/instructions'; Instructions.open || warn('no instruction counter (perf_event_open)'))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; sum = nil; fewest = nil; counted = !!INSTR
  7.times do
    c0 = INSTR && INSTR.read
    t0 = cpu; sum = yield n; d = cpu - t0
    c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1
      c = c1 - c0
      fewest = c if fewest.nil? || c < fewest
    else
      counted = false
    end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format('  %.1f instr', fewest.to_f / n) : ''
end
N = (ENV["N"] || 1000).to_i
def n(count) = [count * N / 1000, 1].max

t("erb_full", n(300))     { |n| s = 0; n.times { |i| s += crc(ERBT.result_with_hash(title: "T#{i & 3}", items: ITEMS)) }; s }
t("erb_eval", n(300))     { |n| s = 0; n.times { |i| s += crc(eval(SRC, fresh("T#{i & 3}"), '(erb)', 0)) }; s }
t("erb_eval_top", n(300)) { |n| s = 0; n.times { |i| s += crc(eval(SRC, fresh_top("T#{i & 3}"), '(erb)', 0)) }; s }
t("erb_def", n(3000))     { |n| s = 0; n.times { |i| s += crc(Body.run("T#{i & 3}", ITEMS)) }; s }
t("erb_toplevel", n(3000)) { |n| s = 0; n.times { |i| b = ERBT.send(:new_toplevel, [:title, :items]); b.local_variable_set(:title, i); s += b.local_variable_get(:title) }; s }
t("erb_new", n(1000))     { |n| s = 0; n.times { s += crc(ERB.new(PAGE, trim_mode: '-').src) }; s }
t("eval_small", n(10000)) { |n| s = 0; n.times { s += eval('1 + 1', binding) }; s }
# a read of a local the binding holds, not the text (RubyOps.GetLocalVariable): 1000 reads of v a unit, and the loop's
# own locals, which the eval defines on the binding too
LOOP = "i = 0; s = 0; while i < 1000; s += v; i += 1; end; s"
def fresh_v = (b = binding; b.local_variable_set(:v, 1); b)
t("eval_reads", n(30)) { |n| s = 0; n.times { s += eval(LOOP, fresh_v, '(reads)', 0) }; s }
# the same loop with its own locals static (a lambda's), reading v from the binding, and reading only its own
LOOP_V = "-> { i = 0; s = 0; while i < 1000; s += v; i += 1; end; s }.()"
LOOP_S = "-> { i = 0; s = 0; w = v; while i < 1000; s += w; i += 1; end; s }.()"
t("eval_reads_v", n(30)) { |n| s = 0; n.times { s += eval(LOOP_V, fresh_v, '(reads)', 0) }; s }
t("eval_reads_static", n(30)) { |n| s = 0; n.times { s += eval(LOOP_S, fresh_v, '(reads)', 0) }; s }
# the template with what perf/erb36's screen refuses (EvalCodeCache.IsShareable): a regexp literal, a defined? of a local
SRC_RE = ERB.new(PAGE.sub("<%= title %>", "<%= title.sub(/T/, 't') %>"), trim_mode: '-').src
SRC_DEF = ERB.new(PAGE.sub("<%= title %>", "<%= defined?(title) ? title : '' %>"), trim_mode: '-').src
t("erb_eval_regexp", n(300)) { |n| s = 0; n.times { |i| s += crc(eval(SRC_RE, fresh_top("T#{i & 3}"), '(erb)', 0)) }; s }
t("erb_eval_defined", n(300)) { |n| s = 0; n.times { |i| s += crc(eval(SRC_DEF, fresh_top("T#{i & 3}"), '(erb)', 0)) }; s }
