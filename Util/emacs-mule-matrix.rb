# Differential matrix for Emacs-Mule strings, and for stateless-ISO-2022-JP and
# stateless-ISO-2022-JP-KDDI, which MRI makes replicas of it.
#
#   ruby    Util/emacs-mule-matrix.rb > /tmp/mule.mri
#   ./ir.sh Util/emacs-mule-matrix.rb > /tmp/mule.ir
#   diff /tmp/mule.mri /tmp/mule.ir
#
# MRI's enc/emacs_mule.c: a character is ASCII, a lead of 0x81-0x8F and one
# byte, 0x90-0x99 and two, 0x9A-0x9B, a private charset byte (0xE0-0xEF) and
# one, or 0x9C-0x9D, a charset byte (0xF0-0xF4 after 0x9C, 0xF5-0xFE after 0x9D)
# and two; every byte after the lead is 0xA0 or above. Its code point is its
# bytes read as one number, and Onigmo calls every character of more than one
# byte ALPHA, which #succ goes by.

def t(label)
  v = begin
    yield
  rescue Exception => e
    [e.class, e.message]
  end
  puts "#{label}: #{v.inspect}"
end

def hex(s) = s.unpack1("H*")

CASES = %w[
  41 81a0 81ff 8fa0 8fff 90a1a1 92a4a2 99ffff 9ae0a0 9be0ff 9bffa0 9aa0a1
  9cf0a0a0 9cf4ffff 9cf5a0a0 9df4a0a0 9df5a0a0 9dfeffff 9dffa0a0
  80 9e 9f a0 ff 81 819f 92a4 92a4a2a4 9cf0a0 41922420 4192a4a2814281a0
  7a8fff 7a9dfeffff 7a99ffff 7a9bffff 81a07a 81a0392d39 8fff7a 81a0ff 7a7a 39
].freeze

%w[Emacs-Mule stateless-ISO-2022-JP].each do |name|
  CASES.each do |h|
    s = [h].pack("H*").force_encoding(name)
    l = "#{name} #{h}"
    t(l) { [s.valid_encoding?, s.length, s.chars.map { hex(_1) }, s.inspect, s.dump] }
    t("#{l} ord") { [(s.ord rescue $!.class), (s.codepoints rescue $!.class), hex(s.succ)] }
    t("#{l} scrub") { [hex(s.scrub), s.scrub { |b| "<#{hex(b)}>" }] }
    t("#{l} slice") { [hex(s.reverse), hex(s[0] || ""), hex(s[-1] || ""), hex(s[1..] || ""), s.index("A")] }
    t("#{l} misc") { [hex(s.center(s.length + 2)), hex(s.upcase), hex(s.swapcase), s.each_grapheme_cluster.count] } if s.valid_encoding?
  end
end

[0x41, 0x81a0, 0x8fff, 0x92a4a2, 0x9ae0a0, 0x9cf0a0a0, 0x9df5a0a0, 0x80, 0x82, 0x819f, 0xffff, 0x9bffa0].each do |n|
  t("chr #{n.to_s(16)}") { hex(n.chr("Emacs-Mule")) }
  t("<< #{n.to_s(16)}") { hex((+"").force_encoding("Emacs-Mule") << n) }
end

# stateless-ISO-2022-JP has converters to and from EUC-JP
["aあ", "あい", "ｱ", "丂"].each do |u|
  t("to stateless #{u.inspect}") do
    s = u.encode("EUC-JP").encode("stateless-ISO-2022-JP")
    [hex(s), s.length, s.chars.map { hex(_1) }, s.ord, s.encode("UTF-8") == u]
  end
end
%w[92a4a2 4192a4a2 92a4 81a0 9cf0a0a0 92a4a2ff].each do |h|
  t("from stateless #{h}") { hex([h].pack("H*").force_encoding("stateless-ISO-2022-JP").encode("UTF-8")) }
end
