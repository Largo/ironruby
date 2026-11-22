# Differential matrix for the %c directive of Kernel#format / String#%, by bytes: the wider
# companion of format-c-matrix.rb (every byte value and trail byte, dummy encodings, the
# argument as a C int).
#
#   ruby    Util/format-c-bytes-matrix.rb > /tmp/fcb.mri
#   ./ir.sh Util/format-c-bytes-matrix.rb > /tmp/fcb.ir
#   diff /tmp/fcb.mri /tmp/fcb.ir
#
# MRI's rb_str_format writes an Integer with rb_enc_codelen + rb_enc_mbcput: the bytes of the
# number, in the encoding of the format, with no check that they form a character (which is
# where %c differs from Integer#chr, see chr-matrix.rb). So the matrix runs every non-dummy
# encoding against numbers that are characters, numbers that have a character's width but
# not its shape, and numbers that are too wide, and prints the encoding name, the bytes and
# the exception class+message of each.
#
# Three messages are compared in the wording ruby/spec's raise_consistent_error asks of every
# engine but CRuby < 4.1 ("into", and no account of what #to_int gave), which is IronRuby's:
# see canonical.

def canonical(message)
  message.sub(/\Ano implicit conversion from nil to integer\z/, "no implicit conversion of nil into Integer")
         .sub(/\Acan't convert (\S+) to (Integer|String) \(.*\)\z/, "can't convert \\1 into \\2")
end

def try(label)
  v = yield
  if v.is_a?(String)
    puts "#{label}\t#{v.encoding.name} #{v.bytes.inspect}"
  else
    puts "#{label}\t#{v.inspect}"
  end
rescue Exception => e
  puts "#{label}\t#{e.class}: #{canonical(e.message)}"
end

# One line for a run of numbers: the bytes of each in hex, or the first letter of its error.
def sweep(label, enc, numbers)
  cells = numbers.map do |n|
    begin
      ("%c".dup.force_encoding(enc) % n).unpack1("H*")
    rescue Exception => e
      e.class.name[0]
    end
  end
  puts "#{label}\t#{cells.join(' ')}"
end

def hex(n)
  n.is_a?(Integer) ? (n < 0 ? "-0x%x" % -n : "0x%x" % n) : n.inspect
end

ENCODINGS = Encoding.list.reject(&:dummy?).sort_by(&:name)

NUMBERS = [0, 0x41, 0x7f, 0x80, 0x8e, 0x8f, 0xa0, 0xa1, 0xdf, 0xff, 0x100, 0x3042, 0x8140,
           0x8ea1, 0xa1a1, 0xa4a2, 0xd800, 0xdfff, 0xfc4b, 0xfffe, 0xffff, 0x10000, 0x8fa1a1,
           0x10ffff, 0x110000, 0xa1a1a1, 0xffffff, 0x1000000, 0x7fffffff, 0x80000000,
           0x8431a430, 0x8ea1a1a1, 0xffffffff, 2**32, 2**64, -1, -0x80000000, -0x80000001,
           -2**64]

to_int = Object.new
def to_int.to_int; 0x8140; end
def to_int.inspect; "to_int"; end
to_str = Object.new
def to_str.to_str; "xyz"; end
def to_str.inspect; "to_str"; end
bad_to_int = Object.new
def bad_to_int.to_int; "a"; end
def bad_to_int.inspect; "bad_to_int"; end
big_to_int = Object.new
def big_to_int.to_int; 2**40; end
def big_to_int.inspect; "big_to_int"; end
huge_to_int = Object.new
def huge_to_int.to_int; -2**70; end
def huge_to_int.inspect; "huge_to_int"; end
bad_to_str = Object.new
def bad_to_str.to_str; 1; end
def bad_to_str.inspect; "bad_to_str"; end

OTHERS = ["ab", "", 65.9, 0x80.to_r, 1e20, -1.5, nil, true, false, :a, [65], to_int, to_str,
          big_to_int, huge_to_int, bad_to_int, bad_to_str, Object]

puts "== the argument as a C int =="
[2.0**31, 2.0**31 - 1, -2.0**31, -2.0**31 - 1, 2.0**40, 2.0**62, 2.0**63, -2.0**63, -2.0**64,
 9.3e18, -9.3e18, 1.5e19, 123456789012345678.0, 1e-5, -0.5, -1.0, Float::NAN,
 Float::INFINITY, -Float::INFINITY, Rational(2**31), Rational(10**20), Rational(-3, 2),
 2**31 - 1, 2**31, -2**31, -2**31 - 1, 2**63 - 1, 2**63, -2**63, -2**63 - 1,
 Complex(65, 0), Complex(65, 1)].each do |v|
  try("%c #{v.inspect}") { "%c" % v }
end

puts "== every non-dummy encoding =="
ENCODINGS.each do |enc|
  (NUMBERS + OTHERS).each do |v|
    try("#{enc.name} encode #{hex(v)}") { "%c".encode(enc) % v }
    try("#{enc.name} force #{hex(v)}") { format("<%c>".dup.force_encoding(enc), v) }
  end
end

puts "== every byte, alone and after a lead byte =="
ENCODINGS.each do |enc|
  sweep("#{enc.name} 0x00..0xff", enc, 0..0xff)
  [0x81, 0x8e, 0x8f, 0xa1, 0xfe].each do |lead|
    sweep("#{enc.name} 0x#{lead.to_s(16)}00..0x#{lead.to_s(16)}ff", enc, (0..0xff).map { |b| lead << 8 | b })
  end
  sweep("#{enc.name} 0x8f00a1..0x8fffa1", enc, (0..0xff).map { |b| 0x8f00a1 | b << 8 })
  sweep("#{enc.name} 0x00a1a1..0xffa1a1", enc, (0..0xff).map { |b| 0xa1a1 | b << 16 })
  sweep("#{enc.name} 0x00a1a1a1..0x7fa1a1a1", enc, (0..0x7f).map { |b| 0xa1a1a1 | b << 24 })
end

puts "== dummy encodings =="
Encoding.list.select(&:dummy?).sort_by(&:name).each do |enc|
  [0x41, 0x80, 0x100, "a"].each do |v|
    try("#{enc.name} #{hex(v)}") { "%c".dup.force_encoding(enc) % v }
  end
end

puts "== width and flags =="
%w[UTF-8 US-ASCII BINARY ISO-8859-1 Shift_JIS EUC-JP EUC-KR GB18030 Big5].each do |name|
  enc = Encoding.find(name)
  [0x41, 0x80, 0xff, 0x3042, 0x8140, 0xa1a1, 0x8fa1a1, 0x110000, -1, "ab", ""].each do |v|
    ["%3c|", "%-3c|", "%03c|", "%1c|", "%0c|", "%.0c|", "%.3c|", "%*c|", "%+c|", "% c|",
     "%#c|", "%<a>c|", "%1$c%1$c|"].each do |f|
      args = f.include?("*") ? [5, v] : f.include?("<") ? [{ a: v }] : [v]
      try("#{name} #{f} #{hex(v)}") { format(f.dup.force_encoding(enc), *args) }
    end
  end
end

puts "== the character and what surrounds it =="
sjis = "\x82\xa0".dup.force_encoding("Shift_JIS")
utf8 = "\u3042"
binary = "\xff".b
[
  ["%c%s", [0x3042, utf8]],
  ["%c%s", [0x3042, sjis]],
  ["%c%s", [0x3042, binary]],
  ["%s%c", [utf8, 0x3042]],
  ["%s%c", [sjis, 0x8140]],
  ["%s%c", [sjis, 0x80]],
  ["%s%c", [binary, 0xff]],
  ["%s%c", [binary, 0x100]],
  ["%c%c", [0x80, 0x3042]],
  ["%c%c", [0x3042, 0x80]],
  ["%c%s", [0x80, binary]],
  ["%c%s", [0x80, utf8]],
  ["%c%s", [0x41, sjis]],
  ["%c%c", [sjis, 0x8140]],
  ["%c%c", [0x8140, sjis]],
  ["%c%c", [utf8, sjis]],
  ["%c%c", [binary, 0x80]],
  ["%c%c", ["\xff".dup.force_encoding("UTF-8"), 0x3042]],
  ["%c%c", ["\xe3\x81".dup.force_encoding("UTF-8"), 0x3042]],
  ["%c", ["\xe3\x81\x82\xe3\x81\x84".dup.force_encoding("UTF-8")]],
  ["%c", ["\x82\xa0\x82\xa2".dup.force_encoding("Shift_JIS")]],
  ["%c", ["\x82".dup.force_encoding("Shift_JIS")]],
  ["%c", ["\u{1f600}\u{1f600}"]],
].each do |f, args|
  %w[UTF-8 US-ASCII BINARY Shift_JIS].each do |name|
    label = "#{name} #{f} #{args.map { |a| a.is_a?(String) ? "#{a.encoding.name}:#{a.bytes.inspect}" : hex(a) }.join(' ')}"
    try(label) { format(f.dup.force_encoding(name), *args) }
  end
end
try("non-ASCII format, UTF-8") { "\u3042%c" % 0x3044 }
try("non-ASCII format, UTF-8, surrogate") { "\u3042%c" % 0xd800 }
try("non-ASCII format, Shift_JIS") { "\x82\xa0%c".dup.force_encoding("Shift_JIS") % 0x80 }
try("non-ASCII format, EUC-JP") { "\xa4\xa2%c|".dup.force_encoding("EUC-JP") % 0x8fa1a1 }
try("broken format, UTF-8") { "\xff%c".dup.force_encoding("UTF-8") % 0x3042 }
try("valid_encoding? 0x80 Shift_JIS") { ("%c".encode("Shift_JIS") % 0x80).valid_encoding? }
try("valid_encoding? 0xd800 UTF-8") { ("%c" % 0xd800).valid_encoding? }
try("valid_encoding? 0x8140 Shift_JIS") { ("%c".encode("Shift_JIS") % 0x8140).valid_encoding? }
try("frozen?") { ("%c" % 0x41).frozen? }

puts "== sprintf through other entry points =="
try("Kernel#sprintf") { sprintf("%c".encode("EUC-KR"), 0x80) }
try("String#% Array") { "%c%c".encode("Big5") % [0x80, 0xa140] }
