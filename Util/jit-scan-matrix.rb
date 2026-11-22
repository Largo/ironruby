# Differential matrix for the method JIT (-X:JIT, Src/Ruby/Runtime/Jit/), section 20 of Util/jit-matrix.rb:
# Json's scanner core, whose instance variable writes are held back (JitCompiler.Sink.cs). A file of its
# own because the sections of jit-matrix.rb leave String's methods aliased, which the JIT does not compile.
# Run under CRuby and under ./ir.sh - with the JIT on and off - and diff:
#
#   ruby Util/jit-scan-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-scan-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT Util/jit-scan-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# All three must be identical. The JIT is only allowed to be faster, never different.
#
# Every method below is called well past the JIT's hotness threshold (40 calls) with
# the argument types it is meant to be specialized for, and THEN probed - with values
# that cross the edges the specialized code has guards for: Int32 -> Int64 growth,
# Int64 -> Bignum overflow (a deopt), a zero divisor (a deopt), an argument of a type
# it was not specialized for, the method redefined, a subclass overriding a method it
# calls on self, a singleton method, a refinement, and Integer#+ itself redefined.
#
# Which of these actually get specialized is not observable from Ruby, so it is not
# part of the output. To see it:
#
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-scan-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]'
#
# The methods named sp_* are shapes the JIT is expected to SPECIALIZE; those named
# no_* are shapes it is expected to REJECT (the output must still be right). A sp_*
# method showing up as rejected there means a construct regressed, not a wrong answer.

def t(label)
  print label.ljust(56), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    # The class, not the message: which exception a specialized body raises is the JIT's
    # business, and IronRuby's wording of a few messages (ZeroDivisionError's, the quote
    # style of NoMethodError's) differs from CRuby's with the JIT off just the same.
    puts e.class
  end
end

HOT = 120

# Calls the block HOT times so that the method it calls turns hot.
def warm
  i = 0
  while i < HOT
    yield i
    i += 1
  end
end

# ---------------------------------------------------------------- 20. instance variable writes held back
#
# Json's Parser#read writes @line, @column and @index, and then reads @input.length and
# @input[@index], which deopt when @input is not a plain String: its typed body keeps the writes
# in locals and stores them where it ends (JitCompiler.Sink.cs), so that each deopt still finds
# nothing done. Each row gets a class of its own (a fresh method to turn hot), made hot on an ASCII
# String, and then, in the middle of the run, has something happen that its guards are for; the
# row prints what the calls returned and the parser's variables after them. `jd_char' and
# `jd_digit' are Json's callers, which bind `read' (-X:JITCalls); `@current != ch' is String#==.

puts '--- 20. instance variable writes held back ---'

JD_SRC = <<~RUBY
  attr_accessor :input, :index, :line, :column, :current
  def initialize(s) = (@input = s; @index = -1; @line = 1; @column = 0; @current = nil; read)
  def read
    if "\\n" == @current
      @line  += 1
      @column = 0
    end
    @index += 1
    if @index < @input.length
      @current = @input[@index]
    else
      @current = nil
    end
  end
  def jd_char(ch)
    return false if @current != ch
    read
    true
  end
  def jd_is_digit = '0' == @current || '1' == @current || '2' == @current || '3' == @current
  def jd_digit
    return false unless jd_is_digit
    read
    true
  end
  def state = [@index, @line, @column, @current]
RUBY
JD_TEXT = "ab\ncd\n12" * 4
$jd_n = 0
def jd_class
  k = Class.new
  Object.const_set("JdParser#{$jd_n += 1}", k)
  k.class_eval(JD_SRC)
  k
end
# runs `read' HOT times on a parser over JD_TEXT (rewound at the end), the block called at 60
# with the parser; what each call from the 58th returned (or the exception's class), then the state
def jd_run(p, &mid)
  seen = []
  warm do |i|
    mid.call(p) if i == 60 && mid
    if p.current.nil? && i != 60 && p.input.is_a?(String) && p.index.is_a?(Integer) && p.index >= p.input.size
      p.index = -1
    end
    v = (p.read rescue $!.class)
    seen << (v.is_a?(String) ? v.dump[1..-2] : "<#{v.inspect}>") if i >= 58
  end
  [seen.join(' '), p.state]
end
def jd_row(label, input = JD_TEXT.dup, &mid)
  k = jd_class
  p = k.new(input)
  t("jd: #{label}") { jd_run(p, &mid) }
  p
end

jd_row('plain')
jd_row('@input nil') { |p| p.input = nil }
jd_row('@input an Array') { |p| p.input = %w[x y z] }
class JdLenString < String; def length = 3; end
class JdIdxString < String; def [](i) = "<#{i}>"; end
class JdPlainString < String; end
jd_row('@input a String subclass with its own length') { |p| p.input = JdLenString.new(JD_TEXT) }
jd_row('@input a String subclass with its own []') { |p| p.input = JdIdxString.new(JD_TEXT) }
jd_row('@input a plain String subclass') { |p| p.input = JdPlainString.new(JD_TEXT) }
jd_row('@input frozen') { |p| p.input = JD_TEXT.dup.freeze }
jd_row('@input multibyte') { |p| p.input = "hé\nあ\u{1F600}x" * 3 }
jd_row('@input invalid bytes') { |p| p.input = "a\xff\xfe\nb\xe3\x81".dup.force_encoding('UTF-8') * 3 }
jd_row('@input binary') { |p| p.input = "a\xff\n\xe3\x81\x82".b * 3 }
jd_row('@input UTF-16LE') { |p| p.input = "aé\nb".encode('UTF-16LE') * 3 }
jd_row('@index nil') { |p| p.index = nil }
jd_row('@index a Float') { |p| p.index = 1.5 }
jd_row('@index at the 62-bit limit') { |p| p.index = 4_611_686_018_427_387_902 }  # 2**62 - 2
jd_row('@index at the 63-bit limit') { |p| p.index = 9_223_372_036_854_775_806 }  # 2**63 - 2
jd_row('@index at 2**63 - 1') { |p| p.index = 9_223_372_036_854_775_807 }
jd_row('@index a Bignum') { |p| p.index = 2**64 }
jd_row('@index past the end') { |p| p.index = 1000 }
jd_row('@index negative') { |p| p.index = -5 }
jd_row('@index -3') { |p| p.index = -3 }
jd_row('@current a Symbol') { |p| p.current = :"\n" }
jd_row('@current an Integer') { |p| p.current = 10 }
class JdStrLike
  def to_str = "\n"
  def ==(o) = (o == "\n")
end
jd_row('@current with to_str and ==') { |p| p.current = JdStrLike.new }
class JdSneaky
  attr_accessor :parser
  def to_str = "x"
  def ==(o) = (parser.index = 3; parser.line = 100; true)
end
jd_row('@current whose == writes the parser') { |p| s = JdSneaky.new; s.parser = p; p.current = s }
jd_row('@current a String subclass') { |p| p.current = JdPlainString.new("\n") }
jd_row('a singleton method on the parser') { |p| def p.state = super + [:single] }
jd_row('a singleton read on the parser') { |p| def p.read = (@index += 2; :single_read) }

# the callers, which bind read
k = jd_class
p = k.new("0123,\n01" * 3)
t('jd: callers') do
  r = []
  warm do |i|
    p.index = -1 if p.current.nil?
    r << [p.jd_digit, p.jd_char(','), p.jd_char("\n")] if i % 7 == 0
    p.read unless p.jd_digit || p.jd_char(',') || p.jd_char("\n")
    p.input = nil if i == 100
  end rescue r << $!.class
  [r.size, r.last(4), p.state]
end
k = jd_class
p = k.new("0123,\n01" * 3)
t('jd: jd_char with a non-String') do
  warm { |i| p.index = -1 if p.current.nil?; p.jd_char(','); p.read }
  [p.jd_char(nil), p.jd_char(:x), p.jd_char(JdPlainString.new(p.current.to_s)), p.state]
end

# a second thread storing @input and @index while the loop runs: whatever ran first, every
# @current is nil or one character of one of the two texts, and nothing is raised
JD_INDEXES = [-1, 0, 1, 2, 3, 4, 5]
def jd_threads(jp)
  alt = JD_TEXT.tr('a-d', 'w-z')
  stop = false
  bad = []
  th = Thread.new do
    j = 0
    until stop
      jp.input = j.even? ? alt : JD_TEXT.dup
      jp.index = JD_INDEXES[j % 7]
      j += 1
    end
  end
  begin
    50_000.times do
      c = jp.read
      bad << c unless c.nil? || (c.size == 1 && (JD_TEXT + alt).include?(c))
      jp.index = -1 if c.nil?
    end
  ensure
    # also when read raised: the writer must not run on through the rows after this one
    stop = true
    th.join
  end
  bad.uniq.first(3)
end
jp = jd_class.new(JD_TEXT.dup)
warm { jp.read; jp.index = -1 if jp.current.nil? }
t('jd: another thread stores @input and @index') { jd_threads(jp) }

# frozen: which write raises, with which message, and what was stored before it
[nil, "\n", 'a'].each do |cur|
  k = jd_class
  p = k.new(JD_TEXT.dup)
  warm { p.read; p.index = -1 if p.current.nil? }
  p.index = 1
  p.current = cur
  p.freeze
  t("jd: frozen parser, @current #{cur.inspect}") do
    e = (p.read rescue $!)
    [e.class, e.message.sub(/0x\h+/, '').sub(/ @input=.*/m, ''), p.state]
  end
end

# methods redefined in the middle of the run: only the first reaches a typed body, since a method
# put back with alias_method is not the library method to the compiler any more
# (each one changes the answer only while $jd_on, set in the middle of the run: warm's own
# `i < HOT' and `i += 1' must go on working, and it passes 7 only before then)
jd_redefined = lambda do |cls, name, body|
  saved = :"jd_saved_#{name.to_s.tr('<+[]=', 'ltpie')}"
  cls.class_eval { alias_method saved, name }
  wrapped = proc { |*a| $jd_on ? instance_exec(saved, *a, &body) : __send__(saved, *a) }
  jd_row("#{cls}##{name} redefined") do
    cls.class_eval { define_method(name, &wrapped) }
    $jd_on = true
  end
  $jd_on = false
  cls.class_eval { alias_method name, saved; remove_method saved }
end
jd_redefined.call(String, :[], proc { |_, i| "#{i}!" })
jd_redefined.call(String, :length, proc { |_| 4 })
jd_redefined.call(Integer, :<, proc { |s, o| self == 7 ? false : __send__(s, o) })
jd_redefined.call(Integer, :+, proc { |s, o| r = __send__(s, o); r == 7 ? 70 : r })
jd_redefined.call(String, :==, proc { |_, o| true })
jd_row('String#length private') { String.send(:private, :length) }
String.send(:public, :length)
jd_row('String#[] private') { String.send(:private, :[]) }
String.send(:public, :[])
module JdRefine
  refine String do
    def length = 2
    def [](i) = 'R'
  end
end
k = Class.new
Object.const_set(:JdRefined, k)
k.class_eval("using JdRefine\n" + JD_SRC)
t('jd: refined where read is defined') { jd_run(k.new(JD_TEXT.dup)) }

# a body whose value is its last write (Bounce's Random#next): the box it stores is the value it answers
class JdSeed
  attr_accessor :seed
  def initialize = (@seed = 74_755)
  def jd_next
    @seed = ((@seed * 1_309) + 13_849) & 65_535
  end
  def jd_two
    @a = @seed + 1
    @seed = @a * 4_611_686_018_427_387_903
  end
end
jds = JdSeed.new
t('jd: value of the last write') do
  r = []
  warm { |i| r << (jds.jd_next rescue $!.class); jds.seed = nil if i == 100 }
  [r.sum { |x| x.is_a?(Integer) ? x : 0 }, r.last(2), (jds.jd_next rescue $!.class)]
end
jds = JdSeed.new
t('jd: value of the last write, overflow to a Bignum') do
  r = []
  warm { |i| jds.seed = i < 60 ? 1 : 3; r << jds.jd_two }
  [r.first, r.last, jds.instance_variable_get(:@a), jds.seed]
end
jds = JdSeed.new
warm { jds.jd_next }
jds.freeze
t('jd: value of the last write, frozen') { [(jds.jd_next rescue $!.class), jds.seed] }

# the order in which held-back writes add variables: instance_variables shows the order they were
# first assigned at run time, which a branch can make other than the source's
class JdOrd
  def initialize(c) = (@c = c)
  def jd_ord
    if @c > 0
      @b = 1
    else
      @a = 1
      @b = 2
    end
    nil
  end
  def jd_ord2
    @a = 1 if @c > 0
    @b = 2
    @a = 3
    nil
  end
  def jd_ord3
    @b = 1 if @c > 0
    @a = 2
    nil
  end
end
t('jd: order of new variables, a branch') do
  r = []
  200.times { |i| o = JdOrd.new(i < 100 ? 1 : 0); o.jd_ord; r << o.instance_variables if i == 0 || i >= 150 }
  r.uniq
end
t('jd: order of new variables, a conditional first write') do
  r = []
  200.times { |i| o = JdOrd.new(i % 2); o.jd_ord2; r << o.instance_variables if i >= 150 }
  r.uniq
end
t('jd: order, a variable removed between calls') do
  o = JdOrd.new(1)
  r = []
  200.times do |i|
    o.instance_variable_set(:@c, i % 2)
    o.remove_instance_variable(:@b) if i >= 150 && o.instance_variable_defined?(:@b)
    o.remove_instance_variable(:@a) if i % 3 == 0 && o.instance_variable_defined?(:@a)
    o.jd_ord3
    r << o.instance_variables if i >= 150
  end
  r.uniq
end
t('jd: order, an object with many variables') do
  r = []
  200.times do |i|
    o = JdOrd.new(i % 2)
    40.times { |k| o.instance_variable_set(:"@v#{k}", k) }
    o.jd_ord2
    r << o.instance_variables.last(3) if i >= 150
  end
  r.uniq
end
class JdOrdSub < JdOrd
  def initialize(c) = (@b = 0; @a = 0; @c = c)
end
t('jd: order, a subclass that made the variables in another order') do
  r = []
  200.times { |i| o = (i.odd? ? JdOrdSub : JdOrd).new(i % 2); o.jd_ord2; r << [o.class, o.instance_variables] if i >= 150 }
  r.uniq
end
class JdOrdClass
  @c = 0
  class << self
    def jd_cls
      @b = 1 if @c > 0
      @a = 2
      nil
    end
    attr_accessor :c
  end
end
t('jd: order, a class-level method') do
  r = []
  200.times do |i|
    JdOrdClass.c = i % 2
    JdOrdClass.remove_instance_variable(:@a) if JdOrdClass.instance_variable_defined?(:@a)
    JdOrdClass.remove_instance_variable(:@b) if JdOrdClass.instance_variable_defined?(:@b)
    JdOrdClass.jd_cls
    r << JdOrdClass.instance_variables if i >= 150
  end
  r.uniq
end

@jd_mf = 0
def jd_main
  @jd_mb = 1 if @jd_mf > 0
  @jd_ma = 2
  nil
end
t('jd: order, main') do
  r = []
  200.times do |i|
    @jd_mf = i % 2
    remove_instance_variable(:@jd_ma) if instance_variable_defined?(:@jd_ma)
    remove_instance_variable(:@jd_mb) if instance_variable_defined?(:@jd_mb)
    jd_main
    r << instance_variables.grep(/jd_m/) if i >= 150
  end
  r.uniq
end

# ---- review of round seventeen: what the allow-list cannot see, types, a caller with an effect
# 1. a caller bound to a body with held writes, the caller having an effect first
class JdRd
  attr_accessor :input, :index, :current, :log, :other, :arr
  def initialize(s) = (@input = s; @index = -1; @line = 1; @column = 0; @current = nil; @log = 0; @other = JdBox.new; @arr = [0]; read)
  def read
    if "\n" == @current
      @line  += 1
      @column = 0
    end
    @index += 1
    if @index < @input.length
      @current = @input[@index]
    else
      @current = nil
    end
  end
  def c_ivar
    @log += 1
    read
  end
  def c_attr
    @other.n = @other.n + 1
    read
  end
  def c_arr
    @arr[0] = @arr[0] + 1
    read
  end
  def state = [@index, @log, @other.n, @arr[0], @current]
end
class JdBox; attr_accessor :n; def initialize = (@n = 0); end
%i[c_ivar c_attr c_arr].each do |m|
  r = JdRd.new("ab\ncd" * 4)
  200.times { |i| r.index = -1 if r.current.nil?; r.input = nil if i == 150; (r.send(m) rescue nil) }
  Kernel.p [m, r.state]
  r = JdRd.new("ab\ncd" * 4)
  100.times { r.index = -1 if r.current.nil?; r.send(m) }
  r.freeze
  Kernel.p [m, :frozen, (r.send(m) rescue $!.class), r.state]
end

# 2. reads and writes of held variables in forms the allow-list does not see directly
class JdF
  attr_reader :x, :y
  def initialize(v) = (@v = v; @x = 0; @y = 0)
  def f_defined = (@x = @v + 1; defined?(@x))
  def f_interp = (@x = @v + 1; "#{@x}")
  def f_oror = (@x = nil if @v > 5; @x ||= @v; @x)
  def f_andand = (@x = @v; @x &&= @v + 1; @x)
  def f_masgn = (@x, @y = @v, @v + 1; @x)
  def f_chain = (@x = @y = @v + 1; @x)
  def f_twice = (@x = @v; @x += 1; @x += 1; @x)
  def f_case
    case @v
    when 1 then @x = 10
    else @x = 20
    end
    @x
  end
  def f_ternary = (@v > 2 ? (@x = 1) : (@y = 2); [@x, @y].size)
  def f_operand = (1 + (@x = @v + 2))
  def f_float = (@x = @v * 1.5)
  def f_big = (@x = @v * 4_000_000_000)
  def f_before = (t = @x; @x = @v; t)
  def f_self_attr = (@x = @v + 3; self.x)
  def f_ivget = (@x = @v + 4; instance_variable_get(:@x))
end
o = JdF.new(1)
ms = %i[f_defined f_interp f_oror f_andand f_masgn f_chain f_twice f_case f_ternary f_operand f_float f_big f_before f_self_attr f_ivget]
ms.each do |m|
  r = nil
  200.times { |i| o.instance_variable_set(:@v, i % 7); r = o.send(m) }
  v = o.send(m)
  Kernel.p [m, r, v, o.x, o.y]
end
# a Float and a large Integer the method returned: the object the variable holds
[:f_float, :f_big].each do |m|
  200.times { |i| o.instance_variable_set(:@v, i + 1); o.send(m) }
  v = o.send(m)
  Kernel.p [m, v.equal?(o.x), v == o.x]
end

# 3. a held variable whose type changes between calls
class JdT
  attr_accessor :n, :m, :k
  def initialize = (@n = 0; @m = 0; @k = 1)
  def bump
    @m = @k + 1
    @n += 1
    nil
  end
  def arms
    if @k > 0
      @m = @k + 1
    else
      @m = 0.5
    end
    @n = @m
    nil
  end
end
o = JdT.new
200.times { o.bump }
r = []
[1.5, nil, 4_611_686_018_427_387_903, 9_223_372_036_854_775_807, 2**70, "s"].each do |v|
  o.n = v
  o.m = :before
  r << [(o.bump rescue $!.class), o.n, o.m]
end
Kernel.p r
o = JdT.new
200.times { |i| o.k = i % 3 - 1; o.arms }
Kernel.p [o.n, o.m]
o.k = 5; o.arms; Kernel.p [o.n, o.m]
o.k = -1; o.arms; Kernel.p [o.n, o.m]

# 4. keyword and optional parameters: no writes are held back there (their prologue and defaults are
# emitted outside the held values' block), and the methods are specialized as they were before
class JdK
  def kw(k: 1); @a = k; @b = k + 1; nil; end
  def kw2(x, k: 2); @b = x; @a = x + k; @b; end
  def opt(x = (@a = 5)); @b = x; nil; end
  def opt2(x, y = x + 1); @b = y; @a = x; @c = @a + @b; nil; end
end
r = []
300.times do |i|
  o = JdK.new
  o.kw(k: i); a = [o.instance_variables, o.instance_variable_get(:@a), o.instance_variable_get(:@b)]
  o = JdK.new
  v = o.kw2(i, k: 3); b = [o.instance_variables, v, o.instance_variable_get(:@a)]
  o = JdK.new
  (i.odd? ? o.opt : o.opt(i)); c = [o.instance_variables, o.instance_variable_get(:@a), o.instance_variable_get(:@b)]
  o = JdK.new
  (i % 3 == 0 ? o.opt2(i) : o.opt2(i, 0.5)); d = [o.instance_variables, o.instance_variable_get(:@c)]
  r << [a, b, c, d] if i % 60 == 7 || i % 60 == 12
end
Kernel.p r
o = JdK.new.freeze
Kernel.p [(o.kw(k: 1) rescue $!.class), (o.kw2(1) rescue $!.class), (o.opt rescue $!.class), (o.opt2(1) rescue $!.class), o.instance_variables]

# 5. one variable held back while it is written as an Int32 and as an Int64 (a long local, stored through
# NarrowLong): Ball#bounce of Bounce and the shapes around it. Each method is hot at call 40 and has its
# event at call 60 and later; what the variable holds afterwards is shown by its class, equal? and
# object_id against the same Integer made again, eql?, hash and Marshal.dump.
def jw_box(v)
  w = Integer(v.to_s)
  # (identity only where IronRuby's Integer is an Int32 box: a larger one is a CRuby Fixnum to 2**62, which
  # IronRuby's equal? and object_id do not follow, with the JIT on or off)
  same = (v >= -2**31 && v < 2**31) ? [v.equal?(w), v.object_id == w.object_id] : :wide
  [v, v.class, same, v.eql?(w), v.hash == w.hash, Marshal.dump(v) == Marshal.dump(w)]
end
JW_EDGES = [2**30, 2**31 - 1, 2**31, 2**62 - 1, 2**62, 2**63 - 1].flat_map { |e| [e, -e] }.flat_map { |e| [e - 1, e, e + 1] }
class JwA
  attr_accessor :x, :y
  def initialize = (@x = 0; @y = 0)
  # Lng (the sum) then Int (the limit)
  def jw_clamp(d); @x += d; if @x > 500; @x = 500; end; nil; end
  # Int then Lng, the method's value the held write
  def jw_last(d); @x = 7; @x += d; end
  # in different arms
  def jw_arms(c, d); if c; @x = 5; else; @x = @y + d; end; nil; end
  # conditionally, both orders
  def jw_cond(d); @x = d + d if d > 3; @x = 1 if d == 0; nil; end
  # a variable the body may create, beside the widened one, in both arms
  def jw_fresh(c, d); if c; @b = 1; @a = d + d; else; @a = 2; @b = d + 1; end; @a = 0 if d == 0; nil; end
  # a deopt (a Bignum sum) after another held write
  def jw_over(d); @y = @y + 1; @x += d; if @x < 0; @x = 0; end; nil; end
end
o = JwA.new
r = []
120.times { |i| o.x = (i < 60 ? i : JW_EDGES[i % JW_EDGES.size]); o.jw_clamp(i < 60 ? 3 : 0); r << jw_box(o.x) if i >= 60 && i < 60 + JW_EDGES.size }
Kernel.p [:jw_clamp, r]
r = []
120.times { |i| v = o.jw_last(i < 60 ? i : JW_EDGES[i % JW_EDGES.size] - 7); r << [jw_box(v), v.equal?(o.x)] if i >= 60 && i < 60 + JW_EDGES.size }
Kernel.p [:jw_last, r]
r = []
120.times { |i| o.y = (i < 60 ? i : JW_EDGES[i % JW_EDGES.size]); o.jw_arms(i.odd?, i < 60 ? 2 : 1); r << jw_box(o.x) if i >= 60 }
Kernel.p [:jw_arms, r]
r = []
120.times { |i| o.x = :none; o.jw_cond(i < 60 ? i % 6 : JW_EDGES[i % JW_EDGES.size]); r << (o.x == :none ? :none : jw_box(o.x)) if i >= 58 }
Kernel.p [:jw_cond, r]
r = []
120.times { |i| f = JwA.new; f.jw_fresh(i.odd?, i < 60 ? i : JW_EDGES[i % JW_EDGES.size]); r << [f.instance_variables, f.instance_variable_get(:@a)] if i >= 58 || i % 13 == 0 }
Kernel.p [:jw_fresh, r]
r = []
120.times do |i|
  o.y = 0; o.x = (i < 60 ? i : 2**63 - 3)
  v = (o.jw_over(i < 60 ? 1 : i - 58) rescue $!.class)
  r << [v, jw_box(o.x), o.y] if i >= 58
end
Kernel.p [:jw_over, r]
o.freeze
Kernel.p [:jw_frozen, (o.jw_clamp(1) rescue $!.class), (o.jw_last(1) rescue $!.class), o.x]

# the same variable with a Float, nil or a String in one arm: not widened (CRuby's answer either way)
JW_WIDE = 2**40
JW_S = "s"
class JwB
  attr_accessor :x
  def initialize = (@x = 0)
  def jw_float(c, d); if c; @x = d + 1; else; @x = 1.5; end; @x = d + JW_WIDE if d > 100; nil; end
  def jw_nil(c, d); if c; @x = d + d; else; @x = nil; end; @x = JW_WIDE if d > 100; nil; end
  def jw_str(c, d); if c; @x = d + 1; else; @x = JW_S; end; @x = JW_WIDE if d > 100; nil; end
end
%i[jw_float jw_nil jw_str].each do |m|
  o = JwB.new; r = []
  120.times { |i| o.send(m, i % 3 != 0, i < 60 ? i : 200); r << [o.x, o.x.class] if i >= 58 && i % 5 == 0 }
  Kernel.p [m, r]
end

# Bounce's Ball#bounce, as the benchmark has it
class JwBall
  attr_accessor :x, :y, :x_vel, :y_vel
  def initialize(x, y, xv, yv) = (@x = x; @y = y; @x_vel = xv; @y_vel = yv)
  def bounce
    x_limit = y_limit = 500
    bounced = false
    @x += @x_vel
    @y += @y_vel
    if @x > x_limit
      @x = x_limit
      @x_vel = 0 - @x_vel.abs
      bounced = true
    end
    if @x < 0
      @x = 0
      @x_vel = @x_vel.abs
      bounced = true
    end
    if @y > y_limit
      @y = y_limit
      @y_vel = 0 - @y_vel.abs
      bounced = true
    end
    if @y < 0
      @y = 0
      @y_vel = @y_vel.abs
      bounced = true
    end
    bounced
  end
  def state = [@x, @y, @x_vel, @y_vel].map { |v| [v, v.class] }
end
def jw_balls(label, n = 200, &mid)
  b = JwBall.new(250, 10, 37, -45)
  out = []
  n.times do |i|
    mid.call(b, i) if mid && (i == 60 || i == 150)
    out << (b.bounce rescue $!.class)
  end
  Kernel.p [label, out.count(true), out.count(false), out.reject { |v| v == true || v == false }.uniq, b.state, jw_box(b.x), jw_box(b.y)]
end
jw_balls('ball')
jw_balls('ball at the Int32 edge') { |b, i| b.x = 2**31 - 2; b.x_vel = i == 60 ? 1 : -(2**31) }
jw_balls('ball at the Int64 edge') { |b, i| b.y = i == 60 ? -(2**63) + 2 : 2**63 - 1; b.y_vel = i == 60 ? -1 : 1 }
jw_balls('x_vel a Float') { |b, i| b.x_vel = i == 60 ? 12.5 : -3 }
jw_balls('x_vel a Bignum') { |b, i| b.x_vel = i == 60 ? 2**70 : 4 }
jw_balls('y_vel nil') { |b, i| b.y_vel = i == 60 ? nil : 5 }
b = JwBall.new(1, 1, 3, 4); 100.times { b.bounce }; b.freeze
Kernel.p [:jw_ball_frozen, (b.bounce rescue $!.class), b.state]
b = JwBall.new(495, 3, 7, -9); 100.times { b.bounce }; b.instance_variable_set(:@x, 498); b.x_vel = 4
Kernel.p [:jw_ball_value, b.bounce, jw_box(b.x), b.x_vel, b.bounce]

# Integer's methods redefined part way through, and restored
JW_MAGIC = 13
%i[abs + - > <].each do |op|
  saved = :"__jw_#{op.object_id}"
  Integer.alias_method saved, op
  body = case op
         when :abs then proc { self == -JW_MAGIC || self == JW_MAGIC ? 1000 : __send__(saved) }
         else proc { |o| o == JW_MAGIC ? (op == :+ || op == :- ? 999 : true) : __send__(saved, o) } end
  jw_balls("Integer##{op} redefined") do |b, i|
    if i == 60
      Integer.define_method(op, &body)
      b.x_vel = JW_MAGIC; b.x = 400
    else
      Integer.alias_method op, saved
    end
  end
end

# Integer#abs refined where bounce is defined
module JwRefine
  refine(Integer) { def abs = 3 }
end
class JwRBall < JwBall
  using JwRefine
  def bounce
    @x += @x_vel
    if @x > 500
      @x = 500
      @x_vel = 0 - @x_vel.abs
    end
    if @x < 0
      @x = 0
      @x_vel = @x_vel.abs
    end
    nil
  end
end
b = JwRBall.new(250, 0, 41, 0); r = []
200.times { |i| b.bounce; r << b.x_vel if i > 150 }
Kernel.p [:jw_refined, b.state, r.uniq.sort]

# a second thread reading x and y while the hot loop runs: every value is one the source could store
b = JwBall.new(250, 250, 47, -53)
done = false
started = false
reader = Thread.new do
  started = true
  bad = []
  until done
    x = b.x; y = b.y; xv = b.x_vel
    bad << [x, y, xv] unless x.is_a?(Integer) && y.is_a?(Integer) && x >= -47 && x <= 547 && y >= -53 && y <= 553 && (xv == 47 || xv == -47)
  end
  bad.first(3)
end
Thread.pass until started
300_000.times { b.bounce }
done = true
Kernel.p [:jw_thread, reader.value, b.state]

# `@a.size' in a body whose writes are held back (JitRuntime.SizeOf): an Array's or a Hash's count,
# anything else a deopt before any store, so @n is bumped once whatever @a holds; and the shape of
# som.rb's Vector#remove_all, `@a = Array.new(@a.size)', which reads @a before it writes it.
class JzSize
  attr_accessor :a
  attr_reader :n, :k
  def initialize(a) = (@a = a; @n = 0; @k = nil)
  def sp_size
    @n += 1
    @k = @a.size + 1
  end
  def sp_resize
    @n += 1
    @a = Array.new(@a.size)
  end
end
class JzSized
  def size = 7
end
def jz_warm(z, v)
  warm { z.a = v.dup; z.sp_size; z.a = v.dup; z.sp_resize }
end
def jz_row(label, warm_with, v)
  z = JzSize.new(warm_with)
  jz_warm(z, warm_with)
  z.a = v
  n = z.n
  r = (z.sp_size rescue $!.class)
  t(label + ': @a.size') { [r, z.n - n, z.k] }
  jz_warm(z, warm_with)
  z.a = v
  n = z.n
  r = (z.sp_resize rescue $!.class)
  t(label + ': @a = Array.new(@a.size)') { [r, z.n - n, z.a] }
end
jz_row('held writes, an Array', [1, 2, 3], [4, 5])
jz_row('held writes, a Hash', { a: 1 }, { b: 2, c: 3 })
jz_row('held writes, a String after an Array', [1, 2, 3], 'four')
jz_row('held writes, nil after an Array', [1, 2, 3], nil)
jz_row('held writes, an object with size after an Array', [1, 2, 3], JzSized.new)
# An opening run of writes (`@x = 0; @y = 0', written at once on a fresh object) and then an operation
# that may deopt: the writes held back instead, so the deopt comes before them. The variables a fresh
# object gets must come out in the source's order, once.
class JzRun
  attr_accessor :a
  def initialize(a) = (@a = a)
  def sp_run
    @x = 0
    @y = 1
    @k = @a.size
  end
  def sp_run_if
    @y = 2
    @x = 3
    if @a
      @a = Array.new(@a.size)
    end
  end
end
def jz_run_row(label, v)
  warm { JzRun.new([1, 2]).sp_run; JzRun.new([1, 2]).sp_run_if; JzRun.new(nil).sp_run_if }
  z = JzRun.new(v)
  r1 = (z.sp_run rescue $!.class)
  z2 = JzRun.new(v)
  r2 = (z2.sp_run_if rescue $!.class)
  t('opening run then a deopt: ' + label) { [r1, z.instance_variables, z.k = nil, r2, z2.instance_variables, z2.a] }
end
class JzRun; attr_accessor :k; end
jz_run_row('an Array', [1, 2, 3])
jz_run_row('a Hash', { a: 1 })
jz_run_row('a String', 'four')
jz_run_row('nil', nil)
jz_run_row('an object with size', JzSized.new)

# `@x ||= v', `@x &&= v' and the same on a local (JitCompiler.EmitOrAssignment): the variable read once, v
# evaluated only when the store is made, the store held back like a write inside an `if'. The counter @n is
# v's visible effect: it must move exactly when CRuby evaluates v, and once only across a deopt.
class JoMemo
  attr_reader :n
  def initialize(*x)
    @n = 0
    @x = x[0] unless x.empty?
  end
  def sp_or = (@x ||= (@n += 1))
  def sp_and = (@x &&= (@n += 1))
  def sp_or_v(v) = (@x ||= v)
  def sp_and_v(v) = (@x &&= v)
  def sp_or_twice(a, b)
    @x ||= a
    @x ||= b
    @x
  end
  def sp_or_then(k)
    @x ||= (@n += 1)
    k + 1
  end
  def sp_and_then(k)
    @x &&= (@n += 1)
    k + 1
  end
  def sp_ab
    @a ||= 1
    @b ||= 2
  end
  def sp_memo = (@memo ||= [@n, :memo])
  def x = @x
end
def jo_state(o) = [o.x, o.n, o.instance_variables]
warm { |i| JoMemo.new.sp_or; JoMemo.new(i).sp_or; JoMemo.new.sp_and; JoMemo.new(i).sp_and
           JoMemo.new.sp_or_v(i); JoMemo.new(i).sp_and_v(i); JoMemo.new.sp_or_twice(i, 2)
           JoMemo.new.sp_or_then(i); JoMemo.new(1).sp_and_then(i); JoMemo.new.sp_ab; JoMemo.new.sp_memo }
[[], [nil], [false], [0], [""], [Object]].each do |init|
  t("||= on #{init.empty? ? 'unset' : init[0].inspect}") { o = JoMemo.new(*init); [o.sp_or, jo_state(o)] }
  t("&&= on #{init.empty? ? 'unset' : init[0].inspect}") { o = JoMemo.new(*init); [o.sp_and, jo_state(o)] }
  t("||= v on #{init.empty? ? 'unset' : init[0].inspect}") { o = JoMemo.new(*init); [o.sp_or_v(:v), jo_state(o)] }
  t("&&= v on #{init.empty? ? 'unset' : init[0].inspect}") { o = JoMemo.new(*init); [o.sp_and_v(2.5), jo_state(o)] }
  t("||= frozen on #{init.empty? ? 'unset' : init[0].inspect}") { o = JoMemo.new(*init).freeze; [(o.sp_or rescue $!.class), jo_state(o)] }
  t("&&= frozen on #{init.empty? ? 'unset' : init[0].inspect}") { o = JoMemo.new(*init).freeze; [(o.sp_and rescue $!.class), jo_state(o)] }
end
t('||= twice') { o = JoMemo.new; [o.sp_or_twice(nil, 3), o.sp_or_twice(4, 5), jo_state(o)] }
t('||= twice, false then a String') { o = JoMemo.new; [o.sp_or_twice(false, "s"), jo_state(o)] }
[2**62, 2**63 - 1, 1.5, "s"].each do |k|
  t("||= then k + 1, k = #{k.inspect}") { o = JoMemo.new; [(o.sp_or_then(k) rescue $!.class), jo_state(o)] }
  t("&&= then k + 1, k = #{k.inspect}") { o = JoMemo.new(1); [(o.sp_and_then(k) rescue $!.class), jo_state(o)] }
end
t('@a ||= 1; @b ||= 2 on a fresh object') { o = JoMemo.new; [o.sp_ab, o.instance_variables] }
t('@a ||= 1; @b ||= 2 with @b first') { o = JoMemo.new; o.instance_variable_set(:@b, 7); [o.sp_ab, o.instance_variables, o.instance_variable_get(:@b)] }
t('memo 5000 times on one object') { o = JoMemo.new; s = 0; 5000.times { s += o.sp_memo[0] }; [s, o.sp_memo.equal?(o.sp_memo)] }
t('memo once on each of 5000 objects') { s = 0; 5000.times { |i| o = JoMemo.new(i); s += o.sp_memo.size }; s }

class JoLocal
  def sp_l0 = (x ||= 5; x)
  def sp_lx(k)
    x = (k == 0 ? nil : k == 1 ? false : k == 2 ? 0 : :s)
    x ||= 5
    x
  end
  def sp_lx_and(k)
    x = (k == 0 ? nil : k == 1 ? false : k == 2 ? 0 : :s)
    x &&= 6
    x
  end
  def sp_lp(a = nil)
    a ||= 3
    a
  end
  def sp_land(a)
    a &&= a + 1
    a
  end
  def sp_li(a)
    a ||= 3
    a + 1
  end
  # a branch that assigned the local has no binding left after it ends: the local may hold its value
  def no_lb(c)
    if c; x = 1; end
    x ||= 2
  end
  def no_lb_and(c)
    x = 5 if c
    x &&= x + 1
    x
  end
end
jl = JoLocal.new
warm { |i| jl.sp_l0; jl.sp_lx(i % 4); jl.sp_lx_and(i % 4); jl.sp_lp; jl.sp_lp(i); jl.sp_land(nil); jl.sp_land(i); jl.sp_li(i) }
t('local ||= before any assignment') { jl.sp_l0 }
t('local ||= after nil, false, 0, :s') { (0..3).map { |k| jl.sp_lx(k) } }
t('local &&= after nil, false, 0, :s') { (0..3).map { |k| jl.sp_lx_and(k) } }
t('parameter ||= 3') { [jl.sp_lp, jl.sp_lp(nil), jl.sp_lp(false), jl.sp_lp(7), jl.sp_lp(2**64), jl.sp_lp("s")] }
t('parameter &&= a + 1') { [jl.sp_land(nil), jl.sp_land(false), jl.sp_land(1), jl.sp_land(2**63 - 1), jl.sp_land(1.5), (jl.sp_land("s") rescue $!.class)] }
warm { |i| jl.no_lb(i.even?); jl.no_lb_and(i.even?) }
t('local ||= after a branch that may have assigned it') { [jl.no_lb(true), jl.no_lb(false), jl.no_lb(1), jl.no_lb(nil)] }
t('local &&= after a branch that may have assigned it') { [jl.no_lb_and(true), jl.no_lb_and(false)] }
t('Integer parameter ||= then + 1') { [jl.sp_li(1), jl.sp_li(2**63 - 1), (jl.sp_li(nil) rescue $!.class), (jl.sp_li(false) rescue $!.class), jl.sp_li(1.5)] }

sa = [1, 2]
def sa.size = 9
jz_row('held writes, an Array with a singleton size', [1, 2, 3], sa)

# last: once any String has had a singleton length, no body compiles String#length
jd_row('@input a singleton length') { |p| s = JD_TEXT.dup; def s.length = 2; p.input = s }
