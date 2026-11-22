# Differential matrix for the %c directive of Kernel#format / String#%.
#
#   ruby    Util/format-c-matrix.rb > /tmp/c.mri
#   ./ir.sh Util/format-c-matrix.rb > /tmp/c.ir
#   diff /tmp/c.mri /tmp/c.ir
#
# MRI's rb_str_format takes a String argument's first character, and anything else as an int
# (NUM2INT: a Float is truncated, a number that does not fit an int is a RangeError, nil a
# TypeError). The number is a character of the format's encoding if rb_enc_codelen has a length
# for it - "invalid character" if it has none, or if the number is negative - and its bytes
# are put (rb_enc_mbcput) without asking whether they are a well formed character, which is
# where %c differs from Integer#chr (Util/chr-matrix.rb): "%c" % 0x100 is "\x01\x00" in EUC-KR.
# A US-ASCII format given 0x80-0xFF becomes a binary result.
#
# Left out: nil. MRI 4.0 says "no implicit conversion from nil to integer" and ruby/spec pins
# "no implicit conversion of nil into Integer" for every other engine (raise_consistent_error
# in core/kernel/shared/sprintf.rb), which is what IronRuby says.

def try(label)
  v = yield
  puts "#{label}\t#{v.is_a?(String) ? "#{v.encoding.name} #{v.bytes.inspect}" : v.inspect}"
rescue Exception => e
  puts "#{label}\t#{e.class}: #{e.message}"
end

NUMBERS = [0, 0x41, 0x7f, 0x80, 0x81, 0x9f, 0xa0, 0xa1, 0xe0, 0xfc, 0xfd, 0xdf, 0xff, 0x100, 0x3042, 0x4142, 0x8100, 0x813f, 0x8140, 0x817f, 0x81fc, 0x81fd, 0x8080, 0x8ea1, 0x8ee0, 0xa1a1, 0xd800, 0xfefe, 0xffff,
           0x10000, 0x10ffff, 0x110000, 0x800000, 0x808080, 0x8ea1a1, 0x8fa1a1, 0xa1a100, 0xffffff, 0x1000000, 0x7fffffff,
           0x80000000, 0x8431a430, 0x8ea1a1a1, 0xffffffff, 2**32, 2**64, -1, -0x80000000, -0x80000001, -2**64]
OTHERS = ["ab", "", 65.9, -0.5, 1e20, :a, [65], Object.new.tap { |o| def o.to_int = 66 }, Object.new.tap { |o| def o.to_str = "zy" },
          Object.new.tap { |o| def o.inspect = "#<o>" }, 65r, Complex(65, 0)]

Encoding.list.reject(&:dummy?).sort_by(&:name).each do |enc|
  next unless enc.ascii_compatible?
  NUMBERS.each do |n|
    try("%c #{enc.name} #{n.to_s(16)}") { "<%c>".dup.force_encoding(enc) % n }
  end
  [0x41, 0xa1, 0x8140, 0x3042].each do |n|
    try("%3c #{enc.name} #{n.to_s(16)}") { "<%3c>".dup.force_encoding(enc) % n }
    try("%-3c #{enc.name} #{n.to_s(16)}") { format("<%-3c>".dup.force_encoding(enc), n) }
    try("%c%c #{enc.name} #{n.to_s(16)}") { format("%c|%c".dup.force_encoding(enc), n, 0x41) }
  end
end

%w[UTF-8 US-ASCII BINARY Shift_JIS EUC-JP].each do |name|
  OTHERS.each do |v|
    try("%c #{name} #{v.class}") { "<%c>".dup.force_encoding(name) % [v] }
  end
  try("%c #{name} other encoding") { "<%c>".dup.force_encoding(name) % "\u3042" }
  try("%c #{name} binary arg") { "<%c>".dup.force_encoding(name) % "\xff\xfe".b }
  try("%c #{name} then %s") { format("%c%s".dup.force_encoding(name), 0xa1, "\u3042") }
  try("%s then %c #{name}") { format("%s%c".dup.force_encoding(name), "\u3042", 0xa1) }
  try("%c then %c #{name}") { format("%c%c%c".dup.force_encoding(name), 0x41, 0xa1, 0xdf) }
  try("%c then binary %s #{name}") { format("%c%s".dup.force_encoding(name), 0xa1, "\xff".b) }
  try("%c then %s of the same #{name}") { format("%c%s".dup.force_encoding(name), 0xa1, "\xa1".dup.force_encoding(name)) }
end
try("%c UTF-16LE") { "%c".encode("UTF-16LE") % 0x3042 }
