# Differential matrix for `s + "lit"` and `s << "lit"` in methods the method JIT compiles (-X:JIT,
# JitCompiler.EmitStringLiteralConcat). Run under CRuby and under ./ir.sh in every mode, and diff:
#
#   ruby Util/jit-strop-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-strop-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-strop-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Every method is called past the JIT's thresholds with a String, then probed. Which ones are specialized:
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-strop-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]' | grep so_
# The traps: a receiver that is not a plain String (nil, an Integer, a subclass with its own method, one
# with a singleton method), String#+ or #<< redefined or refined, encodings that do not mix (UTF-16LE),
# `<<` on a frozen or a chilled String (FrozenError; the deprecation warning), `<<` is an effect, so a
# deopt after it must not append again, and `+` makes a new, unfrozen String of the receiver's encoding.

def t(label)
  print label.ljust(52), ' => '
  begin
    v = yield
    puts v.inspect
  rescue Exception => e
    puts e.class
  end
end

HOT = 300

def warm(n = HOT)
  i = 0
  r = nil
  while i < n
    r = yield i
    i += 1
  end
  r
end

class SO
  def initialize; @n = 0; @buf = +""; end
  attr_reader :n, :buf

  def so_plus(s) = s + ":"
  def so_plus2(s, t) = s + "/" + t
  def so_plus_lit = "a" + "b"
  def so_append(s) = s << "!"
  def so_buf(x)
    @buf << "x"
    x + 1
  end
  def so_count(s)
    @n += 1
    s << "-"
  end
  def so_then_ivar(s)
    s << "+"
    @n = s.size
  end
  def so_chilled
    s = "abc"
    s << "d"
  end
end

class SubS < String
  def +(o) = "sub+"
end
class SubPlain < String; end

o = SO.new
t("plus")                       { warm { |i| o.so_plus("k#{i % 3}") } }
t("plus: new, unfrozen")        { s = +"k"; r = o.so_plus(s); [r.equal?(s), r.frozen?, s] }
t("plus: frozen receiver")      { r = o.so_plus("k".freeze); [r, r.frozen?] }
t("plus: utf-8")                { r = o.so_plus("é"); [r, r.encoding] }
t("plus: binary high byte")     { r = o.so_plus("\xff".b); [r, r.encoding] }
t("plus: us-ascii")             { r = o.so_plus("a".encode("US-ASCII")); [r, r.encoding] }
t("plus: utf-16le")             { o.so_plus("a".encode("UTF-16LE")) }
t("plus: nil")                  { o.so_plus(nil) }
t("plus: Integer")              { o.so_plus(1) }
t("plus: subclass with +")      { o.so_plus(SubS.new("q")) }
t("plus: subclass")             { r = o.so_plus(SubPlain.new("q")); [r, r.class] }
t("plus: singleton +")          { s = +"z"; def s.+(o) = "single"; o.so_plus(s) }
t("plus: Array")                { o.so_plus([1]) }
t("plus2")                      { warm { |i| o.so_plus2("a", i.to_s) } }
t("plus of literals")           { warm { o.so_plus_lit } }
t("append")                     { s = +""; warm { o.so_append(s) }; [s.size, o.so_append(s).equal?(s)] }
t("append: frozen")             { o.so_append("f".freeze) }
t("append: utf-16le")           { s = "a".encode("UTF-16LE"); [(o.so_append(s) rescue $!.class), s.bytesize] }
t("append: nil")                { o.so_append(nil) }
t("append: Array")              { o.so_append([1]) }
t("append: binary")             { s = "\xff".b; o.so_append(s); [s, s.encoding] }
t("effect then a deopt")        { p2 = SO.new; warm { |i| p2.so_buf(i) }; r = p2.so_buf(2**62); [r, p2.buf.size] }
t("count then append")          { p2 = SO.new; s = +""; warm { p2.so_count(s) }; p2.so_count("z".freeze) rescue nil; [p2.n, s.size] }
t("append then ivar, frozen")   { p2 = SO.new; s = +""; warm { p2.so_then_ivar(s) }; p2.freeze; [(p2.so_then_ivar(s) rescue $!.class), s.size, p2.n] }

msgs = []
Warning.singleton_class.define_method(:warn) { |m, **| msgs << m.sub(/\A.*?: /, '') }
Warning[:deprecated] = true
t("chilled")                    { r = warm(5) { o.so_chilled }; [r, msgs.size] }
t("chilled, hot")               { warm { o.so_chilled }; [o.so_chilled, msgs.size > 0] }
Warning[:deprecated] = false

module PlusRefinement
  refine String do
    def +(o) = "refined"
  end
end
class SO
  using PlusRefinement
  def so_refined(s) = s + ":"
end
t("refined")                    { warm { |i| o.so_refined("r") } }

class String
  alias_method :__orig_plus, :+
  def +(o) = "redefined"
end
t("String#+ redefined")         { o.so_plus("k") }
class String
  alias_method :+, :__orig_plus
end
t("String#+ restored")          { o.so_plus("k") }
class String
  alias_method :__orig_append, :<<
  def <<(o) = "appended?"
end
t("String#<< redefined")        { o.so_append(+"k") }
class String
  alias_method :<<, :__orig_append
end
t("String#<< restored")         { o.so_append(+"k") }
