# Expression matrix for strings that share their characters or bytes (copy-on-write, perf/strcow):
# a dup, a `+` and slices - short, long, of the middle and of the tail, of a slice - of strings
# held as characters, as bytes and as an immutable CLR string, frozen and chilled; then each side
# changed by <<, []=, setbyte, replace, slice!, force_encoding, encode!, squeeze!, and the other
# side read; Marshal; and threads: two appending to a string and its copy, readers slicing while
# a writer replaces, setbyte and slice! racing a reader. A row must print what CRuby prints;
# threaded rows print only what does not depend on the interleaving. The two rows racing slice!
# tore on IronRuby before perf/strcow (a copied slice read the array unheld).
#
#   ruby   Util/strcow-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/strcow-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# Every line is "<source> => <result>" or "<source> !! <ExceptionClass>: <message>"; the rows of
# EXPRESSIONS run cold, again and 50 times, and a line says so when a later answer differs.
# -*- frozen_string_literal: false -*-

def long(c = "a", n = 1000) = (s = c * n; s << "z"; s)         # held as characters, room to spare
def bin(n = 1000) = (s = ("\xff" * n).b; s << "z".b; s)            # held as bytes
def lit(n = 1000) = "a" * n                                        # an immutable CLR string
def summary(s) = [s.size, s.bytesize, s.encoding.to_s, s[0, 3], s[-3, 3], s.valid_encoding?, s.frozen?]
def uniform?(s) = s.empty? || s.count(s[0]) == s.size

$warnings = []
module Warning
  def self.warn(msg, category: nil) = ($warnings << msg.sub(/\A.*?: /, ""))
end

EXPRESSIONS = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  # --- slices of a long string held as characters: change either side
  (s = long; t = s[500..]; s << "X"; [summary(t), summary(s)])
  (s = long; t = s[500..]; t << "X"; [summary(t), summary(s)])
  (s = long; t = s[1..]; s[700] = "Q"; [t[698, 3], s[699, 3]])
  (s = long; t = s[1..]; t[700] = "Q"; [t[699, 3], s[700, 3]])
  (s = long; t = s[1..]; s.replace("x"); [summary(t), s])
  (s = long; t = s[1..]; t.replace("x"); [t, summary(s)])
  (s = long; t = s[1..]; s.slice!(0, 900); [summary(t), summary(s)])
  (s = long; t = s[1..]; t.slice!(0, 900); [summary(t), summary(s)])
  (s = long; t = s[1..]; s.setbyte(999, 66); [t[-3, 3], s[-3, 3]])
  (s = long; t = s[1..]; t.setbyte(0, 66); [t[0, 2], s[0, 2]])
  (s = long; t = s[1..]; s.force_encoding("BINARY"); s << "\xff".b; [summary(t), summary(s)])
  (s = long; t = s[1..]; t.force_encoding("UTF-16LE"); [t.encoding, summary(s)])
  (s = long; t = s[1..]; s.encode!("UTF-16LE"); [summary(t), s.bytesize])
  (s = long("ab"); t = s[1..]; s.squeeze!; [summary(t), summary(s)])
  (s = long; t = s[1..]; u = t[1..]; s << "1"; t << "2"; [summary(u), u[-3, 3]])
  (s = long; t = s[2, 600]; s << "X"; [summary(t)])
  (s = long; t = s[300, 701]; t << "Y"; [summary(t), summary(s)])
  (s = long; t = s[999..]; [t, t.size])
  (s = long; t = s[700..]; s.clear; [t.size, s])
  # --- slices of a string held as bytes
  (s = bin; t = s.byteslice(1..); s.setbyte(5, 65); [t.bytes.first(6), s.bytes.first(6)])
  (s = bin; t = s[1..]; t << "!".b; [summary(t), summary(s)])
  (s = bin; t = s.byteslice(400, 601); s.force_encoding("UTF-8"); [summary(t), summary(s)])
  # --- multibyte characters
  (s = long("é"); t = s[300..]; s << "ü"; [summary(t), summary(s)])
  (s = long("é"); t = s[300..]; t[0] = "x"; [t[0, 3], s[300, 3]])
  (s = long("日本"); t = s[1..]; s.force_encoding("BINARY"); [summary(t)])
  # --- immutable, frozen and chilled sources
  (s = lit; t = s[1..]; t << "x"; [summary(t), summary(s)])
  (s = lit.freeze; t = s[1..]; t << "x"; [summary(t), summary(s)])
  (s = long.freeze; t = s[1..]; t << "x"; [summary(t), summary(s)])
  (s = long.freeze; t = s[1..]; [t.frozen?, t.equal?(s)])
  (s = long.freeze; d = s.dup; d << "x"; [summary(d), summary(s)])
  (d = "chilled literal".dup; d << "!"; [d, $warnings.size])
  ($warnings.clear; s = "chilled"; s << "!"; [s, $warnings])
  # --- dup and +
  (s = long; d = s.dup; s << "1"; d << "2"; [s[-2, 2], d[-2, 2]])
  (s = long; d = s.dup; d.setbyte(0, 66); [s[0], d[0]])
  (s = long; c = s + "x"; s << "y"; [c[-2, 2], s[-2, 2], c.size])
  (s = long; c = s + s[1..]; s.clear; [c.size, uniform?(c[0, 1000])])
  (s = long; c = +s[1..]; c << "q"; [c[-2, 2], s[-2, 2]])
  # --- Marshal
  (s = long; t = s[1..]; s << "m"; [Marshal.load(Marshal.dump(t)) == t, Marshal.load(Marshal.dump(s)) == s])
  (s = bin; t = s[1..]; Marshal.load(Marshal.dump(t)).bytesize)
RUBY

THREADED = <<~'RUBY'.lines.map(&:strip).reject { |l| l.empty? || l.start_with?("#") }
  # two threads append to a string and its copy (a dup, a tail slice)
  (s = long; d = s.dup; a = Thread.new { 5000.times { s << "s" } }; b = Thread.new { 5000.times { d << "d" } }; a.join; b.join; [s.size, d.size, s.count("d"), d.count("s")])
  (s = long; d = s[1..]; a = Thread.new { 5000.times { s << "s" } }; b = Thread.new { 5000.times { d << "d" } }; a.join; b.join; [s.size, d.size, s.count("d"), d.count("s")])
  # a reader slicing the tail while a writer replaces the whole string, letter by letter
  (s = "a" * 2001; s << "a"; stop = false; w = Thread.new { i = 0; until stop; s.replace((i.even? ? "a" : "b") * 2002); i += 1; end }; bad = 0; 3000.times { t = s[1..]; bad += 1 unless uniform?(t) && t.size == 2001 }; stop = true; w.join; bad)
  # a reader slicing while a writer sets bytes and another slices off the front
  (s = long("a", 4000); stop = false; w = Thread.new { i = 0; until stop; s.setbyte(i % 4000, 98); s.setbyte(i % 4000, 97); i += 1; end }; bad = 0; 3000.times { t = s[1..]; bad += 1 unless t.size == 4000 && t.count("ab") == 4000 }; stop = true; w.join; bad)
  # a tail slice racing slice! off the front (a long tail is shared, taken while the content is held)
  (s = long("a", 4000); t = nil; w = Thread.new { 2000.times { s.slice!(0, 1) } }; r = Thread.new { 2000.times { t = s[1..]; raise "torn" unless uniform?(t[0..-2]) } }; w.join; r.join; [s.size, uniform?(s[0..-2])])
  # a middle slice racing slice!: copied, while the content is held
  (s = long("a", 4000); bad = 0; w = Thread.new { 2000.times { s.slice!(0, 1) } }; r = Thread.new { 2000.times { t = s[1, 100]; bad += 1 unless uniform?(t) } }; w.join; r.join; [s.size, bad])
RUBY

def clean(v) = v.inspect.gsub(/0x\h+/, "XX")
def run(source)
  "=> #{clean(eval(source))}"
rescue Exception => e
  "!! #{e.class}: #{e.message.gsub(/0x\h+/, "XX")}"
end

EXPRESSIONS.each do |source|
  first = run(source)
  second = run(source)
  warm = nil
  50.times { warm = run(source) }
  line = "#{source} #{first}"
  line += "   [second: #{second}]" if second != first
  line += "   [warm: #{warm}]" if warm != first
  puts line
end
THREADED.each do |source|
  results = 5.times.map { run(source) }.uniq
  puts "#{source} #{results.join(' | ')}"
end
puts "done"
