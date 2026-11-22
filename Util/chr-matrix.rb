# Differential matrix for Integer#chr.
#
#   ruby    Util/chr-matrix.rb > /tmp/chr.mri
#   ./ir.sh Util/chr-matrix.rb > /tmp/chr.ir
#   diff /tmp/chr.mri /tmp/chr.ir
#
# Prints the encoding name, the bytes and the exception class+message for every
# combination, so both the value and the *message* can be diffed - the message
# is what several of the ruby/spec expectations actually match on.

def try(label)
  v = yield
  if v.is_a?(String)
    puts "#{label}\t#{v.encoding.name} #{v.bytes.inspect}"
  else
    puts "#{label}\t#{v.inspect}"
  end
rescue Exception => e
  puts "#{label}\t#{e.class}: #{e.message}"
end

BIG = 2**64

puts "== no argument, default_internal nil =="
Encoding.default_internal = nil
[-BIG, -256, -1, 0, 1, 65, 0x7f, 0x80, 0xa1, 0xff, 0x100, 0x3000, 256,
 2206368128, BIG].each do |n|
  try("#{n}.chr") { n.chr }
end

puts "== no argument, default_internal set =="
%w[UTF-8 SHIFT_JIS US-ASCII BINARY EUC-JP ISO-8859-9 TIS-620].each do |encname|
  begin
    Encoding.default_internal = Encoding.find(encname)
  rescue Exception => e
    puts "default_internal=#{encname}\t#{e.class}: #{e.message}"
    next
  end
  [0, 65, 0x7f, 0x80, 0xa1, 0xff, 0x100, 0x3000, 0x8140, 0xfc4b, 0xa1a0, 620,
   -1, 256].each do |n|
    try("di=#{encname} #{n}.chr") { n.chr }
  end
end
Encoding.default_internal = nil

puts "== explicit encoding argument =="
ENCS = %w[US-ASCII BINARY UTF-8 SHIFT_JIS EUC-JP ISO-8859-9 TIS-620 UTF-16
          UTF-16LE UTF-16BE UTF-32 CESU-8]
CODEPOINTS = [-BIG, -1, 0, 0x41, 0x7f, 0x80, 0xa1, 0xdf, 0xff, 0x100, 0x3000,
              0x8140, 0xfc4b, 0xa1a0, 620, 0xd800, 0xdbff, 0xdc00, 0xdfff,
              0x10400, 0x10ffff, 0x110000, 2206368128, BIG]
ENCS.each do |encname|
  enc = begin
    Encoding.find(encname)
  rescue Exception => e
    puts "Encoding.find(#{encname})\t#{e.class}: #{e.message}"
    next
  end
  CODEPOINTS.each do |n|
    try("#{n}.chr(#{encname})") { n.chr(enc) }
  end
end

puts "== encoding given as a String / case folding =="
['utf-8', 'UTF-8', 'Utf-8', 'euc-jp', 'binary', 'ASCII-8BIT'].each do |name|
  try("7894.chr(#{name.inspect})") { 7894.chr(name) }
  try("0xA4A2.chr(#{name.inspect})") { 0xA4A2.chr(name) }
end

puts "== argument errors =="
try('chr(nil)') { 65.chr(nil) }
try('chr(1)') { 65.chr(1) }
try('chr("no-such-encoding")') { 65.chr("no-such-encoding") }
try('chr(:utf8)') { 65.chr(:utf8) }
try('chr to_str') { o = Object.new; def o.to_str; 'UTF-8'; end; 65.chr(o) }
try('chr 2 args') { 65.chr(Encoding::UTF_8, Encoding::UTF_8) }
try('class of result') { 65.chr.class }
try('frozen?') { 65.chr.frozen? }
try('not equal') { 82.chr.equal?(82.chr) }

# Every encoding that is not a dummy: MRI's rb_enc_uint_chr takes the bytes of the number, as
# many as the encoding's code_to_mbclen says, and accepts them when they are exactly one well
# formed character (rb_enc_precise_mbclen) - "invalid codepoint" when they are not, "out of
# char range" when the number is wider than the encoding's longest character. String#<< and
# #concat with an Integer go the same way.
puts "== every encoding: chr, <<, concat =="
WIDE = [0x41, 0x7f, 0x80, 0x81, 0xa0, 0xa1, 0xdf, 0xe0, 0xfe, 0xff, 0x100, 0x4142, 0x8130, 0x8140, 0x817f, 0x8180, 0x81fe, 0x81ff, 0x8040,
        0x8ea1, 0x8ee0, 0xa1a1, 0xa140, 0xd800, 0xdfff, 0xe000, 0xfe40, 0xfefe, 0xff40, 0xffff,
        0x10000, 0x10ffff, 0x110000, 0x813081, 0x8ea1a1, 0x8fa1a1, 0x8fa1a0, 0xffffff, 0x90a1a1, 0x90a0ff, 0x99ffff, 0x9aa0a1, 0x9ba19f, 0x9ca0a1, 0x9ea0a0,
        0x1000000, 0x7fffffff, 0x80000000, 0x9ca0a1a2, 0x9da0a0a0, 0x9dffff9f, 0x9ae0a0, 0x9befff, 0x9bf0a0, 0x9adfa0, 0x9cf0a0a0, 0x9cf4ffff, 0x9cf5a0a0, 0x9df5a0a0, 0x9dfeffff, 0x9dffa0a0, 0xfffffffd, 0xfffffffe, 0x9ea0a0a0, 0x90a0a0a0, 0x81308130, 0x81308140, 0x81308039, 0x8130fe39, 0x8130ff30, 0x813a8130, 0x80308130, 0x84318130, 0x84328130,
        0x8ea1a1a1, 0x8eb0fefe, 0x8eb1a1a1, 0x8ea0a1a1, 0x8fa1a1a1, 0xa1a1a1a1, 0xd8000000, 0xe3329a35, 0xfe39fe39, 0xff308130, 0xffffffff,
        0x100000000, BIG, -1]
Encoding.list.reject(&:dummy?).sort_by(&:name).each do |enc|
  WIDE.each do |n|
    try("#{n.to_s(16)}.chr(#{enc.name})") { n.chr(enc) }
    try("#{enc.name} << #{n.to_s(16)}") { (+"").force_encoding(enc) << n }
  end
  try("#{enc.name} concat") { (+"").force_encoding(enc).concat(0x41, 0x8ea1a1a1, 0x41) }
  try("#{enc.name} US-ASCII << 0x80") { enc.name == "US-ASCII" ? ((+"").force_encoding(enc) << 0x80) : nil }
end
