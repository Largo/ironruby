# Where ERB#result's time goes (perf/erb2): the parts of app-shapes.rb's erb_result, one template
# render per unit. erb_full is result_with_hash; erb_eval evals the template's src in a fresh
# binding (parse, bridge, transform, compile and run); erb_def runs the same src as a method
# defined once (the body alone); erb_toplevel is new_toplevel + local_variable_set (the binding);
# erb_new is ERB.new(t).src (the template compiler); eval_small is eval('1 + 1', fresh binding).
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
def fresh(title) = (b = binding; b.local_variable_set(:title, title); b.local_variable_set(:items, ITEMS); b)

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; sum = nil
  7.times { t0 = cpu; sum = yield n; d = cpu - t0; best = d if d < best }
  printf "%-16s %9.1f ns  %d\n", name, best * 1e9 / n, sum
end
N = (ENV["N"] || 1000).to_i
def n(count) = [count * N / 1000, 1].max

t("erb_full", n(300))     { |n| s = 0; n.times { |i| s += crc(ERBT.result_with_hash(title: "T#{i & 3}", items: ITEMS)) }; s }
t("erb_eval", n(300))     { |n| s = 0; n.times { |i| s += crc(eval(SRC, fresh("T#{i & 3}"), '(erb)', 0)) }; s }
t("erb_def", n(3000))     { |n| s = 0; n.times { |i| s += crc(Body.run("T#{i & 3}", ITEMS)) }; s }
t("erb_toplevel", n(3000)) { |n| s = 0; n.times { |i| b = ERBT.send(:new_toplevel, [:title, :items]); b.local_variable_set(:title, i); s += b.local_variable_get(:title) }; s }
t("erb_new", n(1000))     { |n| s = 0; n.times { s += crc(ERB.new(PAGE, trim_mode: '-').src) }; s }
t("eval_small", n(10000)) { |n| s = 0; n.times { s += eval('1 + 1', binding) }; s }
