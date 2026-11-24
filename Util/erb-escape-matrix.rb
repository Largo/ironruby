# Differential matrix for ERB::Escape.html_escape (Src/Libraries/Erb/ErbEscape.cs, escape.c's erb_escape_html):
# what it escapes and hands back, and how it converts a non-String with #to_s - rb_convert_type, whose
# rb_check_funcall asks a respond_to? of the object's own, then any #to_s, then respond_to_missing? and a
# method_missing of the object's own, and turns a NoMethodError out of method_missing into the TypeError
# unless respond_to? or respond_to_missing? said yes.
#
#   ruby Util/erb-escape-matrix.rb > cruby.txt
#   IR_CONFIG=Release ./ir.sh Util/erb-escape-matrix.rb > ir.txt
#   IR_CONFIG=Release ./ir.sh -X:NoJIT -X:NoOSR Util/erb-escape-matrix.rb > ir-nojit.txt
#
# All three outputs must be identical.
require 'erb/escape'
def t(label)
  r = yield
  puts "#{label}: #{r.inspect}"
rescue Exception => e
  puts "#{label}: #{e.class}: #{e.message}"
end
class P1 < BasicObject   # no respond_to?, no respond_to_missing?
  def method_missing(m, *a) = m == :to_s ? "<p1>" : super
end
class P2 < BasicObject   # respond_to_missing? only, BasicObject has no respond_to?
  def respond_to_missing?(m, priv) = m == :to_s
  def method_missing(m, *a) = m == :to_s ? "<p2>" : super
end
class P3                 # to_s advertised only for private lookup
  undef_method :to_s
  def respond_to_missing?(m, priv) = m == :to_s && priv
  def method_missing(m, *a) = m == :to_s ? "<p3>" : super
end
class P4                 # advertised publicly
  undef_method :to_s
  def respond_to_missing?(m, priv) = m == :to_s
  def method_missing(m, *a) = m == :to_s ? "<p4>" : super
end
class P5                 # method_missing without respond_to_missing?
  undef_method :to_s
  def method_missing(m, *a) = m == :to_s ? "<p5>" : super
end
class P6 < BasicObject   # nothing at all
end
class P7                 # respond_to? redefined to say yes
  undef_method :to_s
  def respond_to?(m, priv = false) = m == :to_s || super
  def method_missing(m, *a) = m == :to_s ? "<p7>" : super
end
class P8                 # private to_s
  private def to_s = "<p8>"
end
class P9                 # to_s returns non-String
  def to_s = 42
end
[P1, P2, P3, P4, P5, P6, P7, P8, P9].each { |c| t(c.name) { ERB::Escape.html_escape(c.new) } }
class Q1                 # method_missing that gives up (super)
  undef_method :to_s
  def method_missing(m, *a) = super
end
class Q2                 # gives up although respond_to_missing? said yes
  undef_method :to_s
  def respond_to_missing?(m, priv) = m == :to_s
  def method_missing(m, *a) = super
end
class Q3                 # raises a NoMethodError about something else
  undef_method :to_s
  def method_missing(m, *a) = m == :to_s ? nil.zork : super
end
class Q4                 # respond_to? says no although to_s exists
  def respond_to?(m, priv = false) = m == :to_s ? false : super
end
class Q5                 # respond_to? of one parameter
  undef_method :to_s
  def respond_to?(m) = m == :to_s
  def method_missing(m, *a) = m == :to_s ? "<q5>" : super
end
class Q6 < BasicObject   # respond_to_missing? says yes only privately, method_missing gives up
  def respond_to_missing?(m, priv) = m == :to_s && priv
  def method_missing(m, *a) = ::Kernel.raise(::NoMethodError.new("nope", m, receiver: self))
end
$log = []
class Q7                 # what respond_to_missing? is asked
  undef_method :to_s
  def respond_to_missing?(m, priv) = ($log << [m, priv]; true)
  def method_missing(m, *a) = m == :to_s ? "<q7>" : super
end
[Q1, Q2, Q3, Q4, Q5, Q6, Q7].each { |c| t(c.name) { ERB::Escape.html_escape(c.new) } }
class Q8 < BasicObject   # respond_to? defines the to_s it says yes to: found on the first call
  def respond_to?(m, priv = false)
    return false unless m == :to_s
    (class << self; self; end).send(:define_method, :to_s) { "<q8>" }
    true
  end
end
t("Q8") { ERB::Escape.html_escape(Q8.new) }
p $log
class SubS < String; end
plain = "no entities"
sub = SubS.new("plain sub")
[["plain", "a<b>&\"c'd"], ["empty", ""], ["utf8", "é<ü>"], ["binary", "<x>".b], ["sjis", "<x>".encode("Shift_JIS")]].each do |label, s|
  t(label) { r = ERB::Escape.html_escape(s); [r, r.encoding.name] }
end
t("same object") { [ERB::Escape.html_escape(plain).equal?(plain), ERB::Escape.html_escape(sub).equal?(sub), ERB::Escape.html_escape(sub).class] }
t("utf16") { require "cgi/escape"; ERB::Escape.html_escape("<a>".encode("UTF-16LE")).encode("UTF-8") }
t("symbol") { ERB::Escape.html_escape(:"<s>") }
t("integer") { ERB::Escape.html_escape(42) }
t("nil") { ERB::Escape.html_escape(nil) }
t("hot") { r = nil; 5000.times { |i| r = ERB::Escape.html_escape(i.even? ? "<#{i}>" : P4.new) }; r }
t("util") { require "erb"; [ERB::Util.html_escape("<&>"), ERB::Util.h(P5.new)] }
