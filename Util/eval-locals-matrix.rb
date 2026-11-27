# Differential matrix for the locals an eval'd text reads and writes by name - the binding's, and the ones an
# earlier eval defined on it (RubyOps.GetLocalVariable / SetLocalVariable, RubyScope's dynamic locals). The
# compiled code of a text is kept and shared by every eval of the same text (EvalCodeCache), so one site meets
# many bindings: the probes alternate bindings, define locals between two reads, change a name from the
# method's own to one an eval defined, copy bindings, and run evals from other threads. Run it under CRuby and
# under ./ir.sh, and diff:
#
#   ruby Util/eval-locals-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/eval-locals-matrix.rb > /tmp/on.txt 2>&1
#   IR_EVAL_LOCAL_CACHE=0 ./ir.sh Util/eval-locals-matrix.rb > /tmp/off.txt 2>&1
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt

N = 3000

def t(label)
  print label.ljust(52), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 70]}"
  end
end

def err
  yield
rescue Exception => e
  "#{e.class}: #{e.message[0, 70]}"
end

# ---------------------------------------------------------------- 1. one binding

def b_with(**h)
  b = binding
  h.each { |k, v| b.local_variable_set(k, v) }
  b
end

t('eval defines, next eval reads') { |i| b = binding; eval("x = #{i % 3}", b); eval("x", b) }
t('defined by an eval that does not assign') { |i| b = binding; eval("x = 1 if false", b); [b.local_variable_defined?(:x), eval("x", b)] }
t('local_variable_set, then eval reads') { |i| b = b_with(v: i % 4); eval("v + 1", b) }
t('eval writes, local_variable_get reads') { |i| b = b_with(v: 0); eval("v = #{i % 3}", b); b.local_variable_get(:v) }
t('eval writes, the same eval reads') { |i| b = b_with(v: i % 2); eval("v += 10; v * 2", b) }
t('a loop of reads and writes') { |i| b = b_with(v: i % 3); eval("s = 0; j = 0; while j < 5; s += v; j += 1; end; s", b) }
t('a block in the text reads and writes') { |i| b = b_with(v: 1); eval("[1, 2].each { |e| v += e }; v", b) }
t('several evals, one binding') do |i|
  b = binding
  eval("a = #{i % 2}", b); eval("b2 = a + 1", b); eval("a = b2 * 10", b)
  [eval("a", b), eval("b2", b), b.local_variables.sort]
end
t('a new local between two reads') do |i|
  b = b_with(v: i % 3)
  r1 = eval("v", b)
  eval("w#{i % 5} = 1", b)
  b.local_variable_set(:v, r1 + 1)
  [r1, eval("v", b)]
end

# ---------------------------------------------------------------- 2. the same text, many bindings

TEXT = "v * 2 + (defined?(w) ? 1 : 0)"
t('alternating bindings') { |i| bs = [b_with(v: 1), b_with(v: 2), b_with(v: 3, w: 0)]; bs.map { |b| eval(TEXT, b) } }
held = [b_with(v: 5), b_with(v: 6)]
t('two kept bindings, alternating') { |i| b = held[i % 2]; eval("v += 1; v", b) % 4 }
def method_local(v) = eval("v * 2", binding)
def method_dynamic = (b = binding; b.local_variable_set(:v, 7); eval("v * 2", b))
t('the name the method\'s, then an eval\'s') { |i| i.even? ? method_local(i % 3) : method_dynamic }
def shadow(v)
  [1].map { |v| eval("v", binding) } + [eval("v", binding)]
end
t('a block parameter shadows the method\'s') { |i| shadow(i % 3) }
t('a block\'s own local, then the method\'s') { |i| x = i % 2; [[0].map { |e| y = e + 5; eval("y + x", binding) }, eval("x", binding)] }

# ---------------------------------------------------------------- 3. light frames, copies, the top level

def light(a) = eval("a + 1", binding)
def light_write(a) = (eval("a = 50", binding); a)
def light_define(a) = (b = binding; eval("z = a", b); b.local_variable_get(:z))
t('a binding taken in a light frame') { |i| [light(i % 3), light_write(i % 2), light_define(i % 4)] }
t('Binding#dup shares old, keeps new') do |i|
  b = b_with(v: 1)
  c = b.dup
  eval("v = #{i % 3}", c); eval("n = 9", c)
  [eval("v", b), b.local_variable_defined?(:n), eval("n", c)]
end
t('TOPLEVEL_BINDING.dup') { |i| b = TOPLEVEL_BINDING.dup; b.local_variable_set(:tv, i % 2); eval("tv + 1", b) }
t('instance_eval of a String reads the caller\'s local') { |i| loc = i % 3; Object.new.instance_eval("loc * 3") }
t('a missing name is a method call') { |i| b = b_with(v: 1); err { eval("vv", b) } }
t('local_variable_set after a read') { |i| b = b_with(v: 1); r = eval("v", b); b.local_variable_set(:v, i % 2); [r, eval("v", b)] }

# ---------------------------------------------------------------- 4. threads and ERB

require 'erb'
t('a local defined in another thread') do |i|
  b = binding
  Thread.new { eval("tt = #{i % 3}", b) }.join
  eval("tt", b)
end
shared = b_with(c: 0)
t('evals on one binding from four threads') do |i|
  next nil unless i % 500 == 0
  shared.local_variable_set(:c, 0)
  4.times.map { Thread.new { 200.times { eval("c += 1", shared) } } }.each(&:join)
  eval("c", shared) > 0
end
ERBT = ERB.new("<%= title %>:<% items.each do |it| %><%= it %>,<% end %>")
t('ERB#result_with_hash') { |i| ERBT.result_with_hash(title: "T#{i % 2}", items: [i % 3, 1]) }
t('ERB#result on bindings') { |i| title = "B"; items = [i % 2]; ERBT.result(binding) }
