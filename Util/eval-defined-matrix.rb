# Differential matrix for `defined?' in an eval'd text whose compiled code is kept and shared by every eval of the
# same text (EvalCodeCache; IR_EVAL_SHARE_DEFINED lets such a text in). Each probe evaluates one text many times
# while what its answer depends on changes between the evals: a method defined, made private, removed; a constant
# defined and removed; another self; a binding with the local and one without; a global, an instance and a class
# variable set and unset. And what the key has to tell apart: __FILE__ and __LINE__, a method's frame and the top
# level, threads. Run it under CRuby and under ./ir.sh, and diff:
#
#   ruby Util/eval-defined-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/eval-defined-matrix.rb > /tmp/on.txt 2>&1
#   IR_EVAL_SHARE_DEFINED=0 ./ir.sh Util/eval-defined-matrix.rb > /tmp/off.txt 2>&1
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

def b_with(**h)
  b = binding
  h.each { |k, v| b.local_variable_set(k, v) }
  b
end

# ---------------------------------------------------------------- 1. what the answer depends on changes

class Probe
  def initialize(n) = (@iv = n if n)
  def b = binding
end
t('defined?(local) with and without the local') { |i| eval("defined?(cur) ? cur : :none", i.even? ? b_with(cur: i % 3) : b_with) }
t('defined?(method), defined and removed') do |i|
  Object.send(:define_method, :dm_probe) { 1 } if i == 1000
  Object.send(:remove_method, :dm_probe) if i == 2000
  eval("defined?(dm_probe)", b_with)
end
class Vis; def pub = 1; end
t('defined?(o.m), made private and public') do |i|
  Vis.send(:private, :pub) if i == 1000
  Vis.send(:public, :pub) if i == 2000
  eval("defined?(o.pub)", b_with(o: Vis.new))
end
t('defined?(Const), defined and removed') do |i|
  Object.const_set(:DC_PROBE, 1) if i == 1000
  Object.send(:remove_const, :DC_PROBE) if i == 2000
  eval("defined?(DC_PROBE)", b_with)
end
t('defined?(A::B)') do |i|
  Object.const_set(:DCA, Module.new) if i == 500
  DCA.const_set(:B, 1) if i == 1500
  DCA.send(:remove_const, :B) if i == 2500
  eval("defined?(DCA::B)", b_with)
end
t('defined?(@iv) with another self') { |i| eval("defined?(@iv)", Probe.new(i.even? ? i : nil).b) }
t('defined?($g) set and unset') do |i|
  $dg_probe = 1 if i == 1000
  $dg_probe = nil if i == 2000
  eval("defined?($dg_probe)", b_with)
end
class CV; @@cv = 1; def self.b = binding; end
t('defined?(@@cv) in a class\'s frame') { |i| eval("defined?(@@cv)", CV.b) }
t('defined?(String), defined?(::Kernel)') { |i| eval("[defined?(String), defined?(::Kernel)]", b_with) }
t('defined?(x = 1), defined?(1 + 1), defined?(nil)') { |i| eval("[defined?(x = 1), defined?(1 + 1), defined?(nil)]", b_with) }
t('defined?(puts), defined?(self)') { |i| eval("[defined?(puts), defined?(self)]", b_with) }

# ---------------------------------------------------------------- 2. what stays refused, and what the key tells apart

def with_block = eval("defined?(yield) ? yield : :noblock", binding)
t('defined?(yield), with and without a block') { |i| i.even? ? with_block { i % 2 } : with_block }
class SupBase; def m = :base; end
class SupSub < SupBase; def m = eval("defined?(super) ? super : :nosuper", binding); def n = eval("defined?(super) ? 1 : :nosuper", binding); end
t('defined?(super)') { [SupSub.new.m, SupSub.new.n] }
t('__FILE__ and __LINE__ with the file and line given') { |i| eval("[defined?(x), __FILE__, __LINE__]", b_with, "f#{i % 2}.rb", i % 3) }
def in_method(cur) = eval("defined?(cur) ? cur : :none", binding)
t('in a method and at the top level') { |i| i.even? ? in_method(i % 3) : eval("defined?(cur) ? cur : :none", b_with) }
t('a cached text in four threads') do |i|
  next nil unless i % 500 == 0
  4.times.map { |k| Thread.new { 100.times.map { eval("defined?(cur) ? cur : :none", k.even? ? b_with(cur: k) : b_with) }.uniq } }.map(&:value)
end

# ---------------------------------------------------------------- 3. templates

require 'erb'
T = ERB.new("<%= defined?(current) ? current : 'none' %>:<%= defined?(klass) ? 1 : 0 %>")
t('ERB with defined?') { |i| i.even? ? T.result_with_hash(current: i % 3) : T.result_with_hash(klass: 1) }
