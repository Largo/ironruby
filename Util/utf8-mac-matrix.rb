# Differential matrix for MRI's UTF8-MAC converters (enc/trans/utf8_mac.c), and
# for the default replacement of an invalid or undefined character.
#
#   ruby    Util/utf8-mac-matrix.rb > /tmp/mac.mri
#   ./ir.sh Util/utf8-mac-matrix.rb > /tmp/mac.ir
#   diff /tmp/mac.mri /tmp/mac.ir
#
# A UTF8-MAC string is UTF-8; converting to it writes each character
# decomposed, as HFS+ names files (Apple's table, not NFD: some characters stay
# whole and marks are not reordered), and converting from it composes a
# character with the one after it where the pair names one. The tables are
# generated from CRuby by Util/gen-utf8-mac.rb. Every code point is checked
# both ways at the end, as counts and the first few that differ.
#
# Left out, and still different: in a conversion from UTF8-MAC that goes on
# through UTF-8 to a third encoding, MRI writes an invalid byte's replacement
# before the character its first converter still holds ("e\xFF" to UTF-16BE
# is FFFD 0065); IronRuby writes it after, as MRI's single converter does.

def t(label)
  v = begin
    yield
  rescue Exception => e
    [e.class, e.message]
  end
  puts "#{label}: #{v.inspect}"
end

def h(s) = s.unpack1("H*")

SAMPLES = ["é", "é", "ガ", "ガ", "가", "각", "가", "각",
           "각", "Å", "Å", "Å", "ẛ̣", "ﬁ", "Ω", "̈́", "ཱི",
           "\u{1d15e}", "á̂", "̀", "é́", "é́", "ÿ", "Ǖ", "Ǖ",
           " ", "豈", "⫝̸", "\u{2f800}", "क़", "क़", "ば゙", "ΐ", "ΐ",
           "ά", "ᬆ", "x\u{1f600}́", "\u{fffff}"]
SAMPLES.each do |u|
  t("to #{h(u)}") { h(u.encode("UTF8-MAC")) }
  t("to via UTF-16 #{h(u)}") { h(u.encode("UTF-16LE").encode("UTF8-MAC")) }
  t("from #{h(u)}") { h(u.dup.force_encoding("UTF8-MAC").encode("UTF-8")) }
  t("from to UTF-16 #{h(u)}") { h(u.dup.force_encoding("UTF8-MAC").encode("UTF-16BE")) }
  t("round trip #{h(u)}") { h(u.encode("UTF8-MAC").encode("UTF-8")) }
end

t("undefined") { "\u{100000}".encode("UTF8-MAC") }
t("undefined replace") { h("a\u{10ffff}".encode("UTF8-MAC", undef: :replace)) }
t("invalid from") { "\xff".dup.force_encoding("UTF8-MAC").encode("UTF-8") }
t("invalid to") { "\xff".dup.force_encoding("UTF-8").encode("UTF8-MAC") }
t("invalid from replace") { h("é\xff".dup.force_encoding("UTF8-MAC").encode("UTF-8", invalid: :replace)) }
t("from EUC-JP") { h("ば".encode("EUC-JP").encode("UTF8-MAC")) }
t("to EUC-JP") { h("バ".dup.force_encoding("UTF8-MAC").encode("EUC-JP")) }
t("path to") { Encoding::Converter.search_convpath("UTF-8", "UTF8-MAC") }
t("path from") { Encoding::Converter.search_convpath("UTF8-MAC", "EUC-JP") }

t("converter from") do
  ec = Encoding::Converter.new("UTF8-MAC", "UTF-8")
  [h(ec.convert("e")), h(ec.convert("́")), h(ec.convert("xカ")), h(ec.convert("゙")), h(ec.finish)]
end
t("converter to") do
  ec = Encoding::Converter.new("UTF-8", "UTF8-MAC")
  [h(ec.convert("é")), h(ec.convert("ガ")), h(ec.finish)]
end
t("primitive_convert") do
  ec = Encoding::Converter.new("UTF8-MAC", "UTF-8")
  src = "éé".b
  dst = +""
  [ec.primitive_convert(src, dst, nil, 2), h(dst), ec.primitive_convert(src, dst), h(dst)]
end
t("converter to EUC-JP") do
  ec = Encoding::Converter.new("UTF8-MAC", "EUC-JP")
  h(ec.convert("バ") + ec.finish)
end
t("converter invalid") do
  ec = Encoding::Converter.new("UTF8-MAC", "UTF-8")
  [ec.primitive_convert("e\xff".b, dst = +""), h(dst), ec.primitive_errinfo[0..1]]
end

require "tmpdir"
path = File.join(Dir.tmpdir, "ironruby-utf8-mac-#{$$}.txt")
begin
  File.binwrite(path, "éガ\n")
  t("IO read") { h(File.read(path, encoding: "UTF8-MAC:UTF-8")) }
  t("IO gets") { File.open(path, "r:UTF8-MAC:UTF-8") { |f| h(f.gets) } }
  File.open(path, "w:UTF8-MAC") { |f| f.write("éガ") }
  t("IO write") { h(File.binread(path)) }
ensure
  File.delete(path) if File.exist?(path)
end

# Encoding::Converter with a small destination: what the composer holds and lets go of,
# and a replacement after it, are not lost when the destination fills
["\u00e9\u{1f600}", "e\u0301\u{1f600}x", "\xc3\xa9\xff", "\xc3\xa9\xe3", "a\u30ab\u3099b", "e\u0301\xffe\u0301",
 "\u1100\u1161\u11a8\xff", "\xffe\u0301", "e\u0301\xe3\x81"].each do |src|
  [["UTF-8", nil], ["UTF-8", { invalid: :replace }], ["UTF-16BE", nil], ["EUC-JP", nil]].each do |to, opts|
    results = (1..7).map do |limit|
      ec = Encoding::Converter.new("UTF8-MAC", to, **(opts || {}))
      dst = +""
      s = src.b
      statuses = []
      25.times do
        r = ec.primitive_convert(s, dst, nil, limit)
        statuses << r
        break unless r == :destination_buffer_full
      end
      "#{limit}=#{statuses.uniq.join('/')}:#{h(dst)}"
    end
    puts "small destination #{h(src)} #{to} #{opts.inspect}: #{results.join(' ')}"
  end
end

# the default replacement: U+FFFD where the last converter writes UTF-8
pairs = [%w[UTF8-MAC UTF-8], %w[UTF-8 UTF8-MAC], %w[EUC-JP UTF8-MAC], %w[UTF8-MAC EUC-JP], %w[UTF8-MAC UTF-16BE],
         %w[CESU-8 UTF-8], %w[UTF-8 CESU-8], %w[EUC-JP CESU-8], %w[CESU-8 EUC-JP], %w[UTF8-KDDI UTF-8], %w[UTF-8 UTF-16LE],
         %w[EUC-JP UTF-8], %w[UTF-8 EUC-JP], %w[Shift_JIS EUC-JP], %w[UTF-16LE UTF-8], %w[UTF-16LE UTF-32BE],
         %w[UTF-8 ISO-2022-JP], %w[UTF-8 UTF-16], %w[EUC-JP UTF-32], %w[UTF-8 IBM037], %w[ISO-2022-JP UTF-8], %w[UTF-8 CP50221]]
pairs.each do |f, to|
  t("replacement #{f} #{to}") { Encoding::Converter.new(f, to).replacement.dump }
  next if f == "UTF8-MAC" && to != "UTF-8" # the ordering the header leaves out
  t("invalid replace #{f} #{to}") { h("e\xff".dup.force_encoding(f).encode(to, invalid: :replace)) } if Encoding.find(f).ascii_compatible?
end

# every code point: how many decompose, and checksums of both directions
decomposed = (0..0xFFFFF).count { |cp| !(0xD800..0xDFFF).cover?(cp) && [cp].pack("U").encode("UTF8-MAC").unpack("U*") != [cp] }
puts "decomposed code points: #{decomposed}"
sum = 0
(0..0xFFFFF).each do |cp|
  next if (0xD800..0xDFFF).cover?(cp)
  sum = (sum * 31 + [cp].pack("U").encode("UTF8-MAC").unpack("U*").sum { _1 * 7 + 1 }) % 1_000_000_007
end
puts "decomposition checksum: #{sum}"
recomposed = (0..0xFFFF).sum do |cp|
  next 0 if (0xD800..0xDFFF).cover?(cp)
  [cp].pack("U").encode("UTF8-MAC").force_encoding("UTF8-MAC").encode("UTF-8").unpack1("U")
end
puts "recomposition checksum: #{recomposed}"
