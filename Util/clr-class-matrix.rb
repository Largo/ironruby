# Differential matrix for the class of a String, a Hash and other objects of CLR types that keep their instance data
# in the runtime's table (RubyContext._referenceTypeInstanceData): their singleton classes, instance variables, object
# ids and frozen state, as compiled code and call sites find them, while other objects of the same class have singleton
# classes. An object the table was never given data for is answered its class without the table lookup
# (RubyContext.MayHaveTableInstanceData, IR_INSTANCE_DATA_FILTER). Run it under CRuby and under ./ir.sh, and diff:
#
#   ruby Util/clr-class-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/clr-class-matrix.rb > /tmp/ir.txt 2>&1
#   diff /tmp/mri.txt /tmp/ir.txt

def t(label)
  print label.ljust(52), ' => '
  begin
    puts yield.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message.gsub(/0x\h+/, '0x').gsub(/#<Proc:0x [^>]*>/, '#<Proc>')}"
  end
end

class Probe
  def initialize(o) = @o = o
  def nil_p = @o.nil?
  def cls = @o.class
  def kind = [@o.is_a?(@o.class), @o.class === @o, @o.instance_of?(@o.class)]
  def hello = @o.respond_to?(:hello) ? @o.hello : :none
  def sm = @o.singleton_methods.sort
end

def hot(p, n = 3000) = n.times { p.nil_p; p.cls; p.kind; p.hello }

MAKERS = {
  'String' => -> { +"s" },
  'Hash' => -> { { k: 1 } },
  'Range' => -> { (1..2).dup },
  'Proc' => -> { proc { 1 } },
  'Regexp' => -> { Regexp.new("a") },
  'Struct' => -> { Struct.new(:a).new(1) },
}

MAKERS.each do |name, make|
  x = make.()
  px = Probe.new(x)
  hot(px)
  t("#{name}: plain") { [px.nil_p, px.cls == x.class, px.kind, px.hello] }
  def x.hello = :singleton
  t("#{name}: after def x.hello") { [px.hello, px.cls == x.class, px.kind, px.sm] }
  hot(px)
  y = make.()
  py = Probe.new(y)
  hot(py)
  t("#{name}: another one, unaffected") { [py.nil_p, py.hello, py.sm] }
  def y.nil? = :mine
  t("#{name}: a singleton nil? on it") { [py.nil_p, y.nil?] }
  z = make.()
  z.instance_variable_set(:@iv, 5)
  pz = Probe.new(z)
  hot(pz)
  t("#{name}: an instance variable") { [z.instance_variable_get(:@iv), pz.nil_p, pz.hello] }
  w = make.()
  id = w.object_id
  t("#{name}: object_id stays") { w.object_id == id }
  v = make.()
  v.freeze
  t("#{name}: frozen") { [v.frozen?, Probe.new(v).nil_p, (def v.q = 1 rescue $!.class)] }
  u = make.()
  def u.hello = :u
  u.instance_variable_set(:@v, 1)
  t("#{name}: clone and dup") { [Probe.new(u.clone).hello, Probe.new(u.dup).hello, u.clone.instance_variable_get(:@v)] }
  s = make.()
  s.extend(Module.new { def hello = :extended })
  t("#{name}: extend") { Probe.new(s).hello }
end

t('a frozen literal cannot have one') { s = "lit".freeze; def s.x = 1 }
t('a Symbol cannot have one') { s = :sym; def s.x = 1 }
t('a String subclass') { k = Class.new(String); x = k.new("a"); def x.hello = :sub; [Probe.new(x).hello, Probe.new(x).cls == k] }
t('a Hash subclass') { k = Class.new(Hash); x = k.new; def x.hello = :sub; [Probe.new(x).hello, Probe.new(x).cls == k] }

# ---------------------------------------------------------------- many objects with data (the filter fills)
t('70000 Strings with instance variables, then lookups') do
  many = Array.new(70_000) { |i| s = +"m#{i}"; s.instance_variable_set(:@i, i); s }
  plain = Array.new(1000) { |i| +"p#{i}" }
  one = many[12_345]
  def one.hello = :one
  [many.sum { |s| s.instance_variable_get(:@i) }, plain.count { |s| Probe.new(s).hello == :none },
   Probe.new(one).hello, many.count { |s| s.singleton_methods.empty? }]
end
t('70000 object_ids, then singleton classes on a few') do
  many = Array.new(70_000) { |i| h = { i: i }; h.object_id; h }
  few = many.each_slice(10_000).map(&:first)
  few.each_with_index { |h, i| h.define_singleton_method(:hello) { i } }
  [few.map { |h| Probe.new(h).hello }, Probe.new(many[1]).hello, Probe.new({}).hello]
end

# ---------------------------------------------------------------- threads
t('threads give Strings singleton classes') do
  strs = Array.new(8) { +"t#{_1}" }
  probes = strs.map { Probe.new(_1) }
  ths = strs.each_with_index.map { |x, i| Thread.new { 200.times { probes[i].hello }; x.define_singleton_method(:hello) { i }; probes[i].hello } }
  ths.map(&:value)
end
t('a singleton defined while another thread calls') do
  x = { a: 1 }
  px = Probe.new(x)
  th = Thread.new { r = nil; 20000.times { r = px.hello }; r }
  def x.hello = :late
  th.join
  px.hello
end
