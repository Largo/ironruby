# Differential matrix for CESU-8 strings, and for the code points of UTF-8's
# carrier variants (UTF8-DoCoMo, UTF8-KDDI, UTF8-SoftBank).
#
#   ruby    Util/cesu8-matrix.rb > /tmp/cesu8.mri
#   ./ir.sh Util/cesu8-matrix.rb > /tmp/cesu8.ir
#   diff /tmp/cesu8.mri /tmp/cesu8.ir
#
# CESU-8 (MRI's enc/cesu_8.c) is UTF-8 with no four byte form: a character
# outside the BMP is a surrogate pair, each half in three bytes. A lone
# surrogate is not a character, so "\xED\xA0\x80" is a pair cut short and
# "\xED\xB2\x80" invalid at its second byte. Its code points, like those of the
# carrier variants, are Unicode's: "\xE3\x81\x82".ord is 0x3042 and #inspect
# spells it "あ".
#
# Left out, and still different: a UTF-8 regexp matched against a string in
# one of these encodings (MRI raises Encoding::CompatibilityError), and
# UTF8-MAC's decomposition when it is transcoded to. Also left out: the
# grapheme clusters of an invalid string (MRI's stop at a character cut short
# and drop the rest), and an invalid sequence followed by a non-ASCII byte on
# its way to another encoding: MRI's CESU-8 transcoder loses the byte it should
# read again, and passes the rest of that character through as raw bytes
# ("\xED\xA0\x80\xE3\x81\x82" converts to "\x81\x82" after the error).

def t(label)
  v = begin
    yield
  rescue Exception => e
    [e.class, e.message]
  end
  puts "#{label}: #{v.inspect}"
end

def hex(s) = s.unpack1("H*")

CASES = [
  "", "a", "\x7f", "\xc2\x80", "\xdf\xbf", "\xe0\xa0\x80", "\xe3\x81\x82",
  "\xed\x9f\xbf", "\xee\x80\x80", "\xef\xbf\xbf",
  # surrogate pairs: U+10000, U+1F600, U+10FFFF
  "\xed\xa0\x80\xed\xb0\x80", "\xed\xa0\xbd\xed\xb8\x80", "\xed\xaf\xbf\xed\xbf\xbf",
  # lone halves, pairs the wrong way round or cut short
  "\xed\xb2\x80", "\xed\xbf\xbf", "\xed\xa0\x80", "\xed\xa0", "\xed\xa0\xbd\xed",
  "\xed\xa0\xbd\xed\xb8", "\xed\xb8\x80\xed\xa0\xbd", "\xed\xa0\x80a",
  "a\xed\xa0\x80\xed\xa0\x80", "\xed\xa0\x80\xe3\x81\x82",
  # UTF-8's four byte form, overlong forms, stray bytes
  "\xf0\x9f\x98\x80", "\xc0\x80", "\xc1\xbf", "\xe0\x80\x80", "\x80", "\xff", "a\xffb",
  "\xe3\x81", "\xe3\x81a", "x\xed\xa0\xbd\xed\xb8\x80y\xe3\x81\x82z",
].freeze

CASES.each do |raw|
  s = raw.b.force_encoding("CESU-8")
  h = hex(raw)
  t("#{h} valid") { [s.valid_encoding?, s.ascii_only?, s.length, s.bytesize] }
  t("#{h} chars") { s.chars.map { hex(_1) } }
  t("#{h} codepoints") { s.codepoints }
  t("#{h} ord") { s.ord }
  t("#{h} inspect") { [s.inspect, s.dump] }
  t("#{h} scrub") { [hex(s.scrub), hex(s.scrub("?")), s.scrub { |b| "<#{hex(b)}>" }] }
  t("#{h} reverse") { [hex(s.reverse), s[0] && hex(s[0]), s[-1] && hex(s[-1]), hex(s.slice(1, 2) || "")] }
  t("#{h} succ") { hex(s.succ) }
  t("#{h} encode") { hex(s.encode("UTF-8")) }
  t("#{h} encode replace") { hex(s.encode("UTF-16BE", invalid: :replace)) } unless raw.b.include?("\xed\xa0\x80\xe3".b)
  t("#{h} each_grapheme") { s.each_grapheme_cluster.map { hex(_1) } } if s.valid_encoding?
end

# from Unicode text to CESU-8 and back
["a", "é", "あ", "\u{10000}", "\u{1f600}", "\u{10ffff}", "a\u{1f600}b"].each do |u|
  t("U #{u.inspect} encode") do
    c = u.encode("CESU-8")
    [hex(c), c.length, c.codepoints, c.encode("UTF-8") == u]
  end
end
[0, 0x41, 0x80, 0x3042, 0xd7ff, 0xd800, 0xdfff, 0xe000, 0xffff, 0x10000, 0x1f600, 0x10ffff, 0x110000].each do |n|
  t("#{n.to_s(16)}.chr") { hex(n.chr("CESU-8")) }
  t("%c #{n.to_s(16)}") { hex("%c".encode("CESU-8") % n) }
end

%w[UTF8-DoCoMo UTF8-KDDI UTF8-SoftBank].each do |name|
  ["\xe3\x81\x82", "\xf0\x9f\x98\x80", "\xc3\xa9z", "\xff\xe3\x81\x82", "\xef\xbf\xbf"].each do |raw|
    s = raw.b.force_encoding(name)
    t("#{name} #{hex(raw)}") { [s.valid_encoding?, s.length, (s.ord rescue $!.class), s.codepoints, s.inspect] }
    t("#{name} #{hex(raw)} succ") { hex(s.succ) }
  end
end
