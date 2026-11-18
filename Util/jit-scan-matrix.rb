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
  50_000.times do
    c = jp.read
    bad << c unless c.nil? || (c.size == 1 && (JD_TEXT + alt).include?(c))
    jp.index = -1 if c.nil?
  end
  stop = true
  th.join
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

# last: once any String has had a singleton length, no body compiles String#length
jd_row('@input a singleton length') { |p| s = JD_TEXT.dup; def s.length = 2; p.input = s }
