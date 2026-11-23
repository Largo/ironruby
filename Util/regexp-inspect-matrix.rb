# Differential matrix for Regexp#inspect against the encoding its result is meant
# for (Encoding.default_internal, else default_external).
#
#   ruby    Util/regexp-inspect-matrix.rb > /tmp/reins.mri
#   ./ir.sh Util/regexp-inspect-matrix.rb > /tmp/reins.ir
#   diff /tmp/reins.mri /tmp/reins.ir
#
# MRI's rb_reg_desc hands rb_reg_expr_str the result encoding. The source is
# copied as it is only when it is valid, every ASCII character in it is
# printable and not a '/', and every other character is in that same encoding.
# Otherwise it is written character by character: a non-ASCII one as \uXXXX
# (\u{XXXXX}) in a Unicode regexp and \xNN or \x{NNNN} (its bytes) in any other,
# whatever the result encoding is, an invalid byte as \xNN, a '/' as "\/", an
# unprintable ASCII character that is not a space as \xNN, and a backslash
# together with the character after it as they are.

$VERBOSE = nil

def show(s)
  "#{s.encoding} #{s.b.unpack1('H*')} #{s.b.inspect}"
end

def regexps
  utf8 = [
    "あ", "a/b", "あ/", "あ\\/", "\\あ", "\\あ/", "😀", "é", "\u00A0", "\u200B", "\u3000",
    "あ\t", "あ\n", "\x01あ", "あ\x7F", "a b", "[あ-ん]+", "\\p{Hiragana}あ", "#\{あ}",
  ].map { |s| Regexp.new(s) }
  utf8 += [/あ/i, /あ/mix, /\//, /a\/あ/, /\u3042/, /\あ/, /(?<名前>x)/]

  eucjp = %w[a4a2 8ea1 8fb0a1 a4a22f 5ca4a2 a4a209 61].map { |h| Regexp.new([h].pack("H*").force_encoding("EUC-JP")) }
  sjis = %w[82a0 a1 82a02f].map { |h| Regexp.new([h].pack("H*").force_encoding("Shift_JIS")) }
  latin = %w[e9 a0 e92f].map { |h| Regexp.new([h].pack("H*").force_encoding("Windows-1252")) }
  binary = %w[ff 80 61802f e38182 00ff].map { |h| Regexp.new([h].pack("H*").b) }
  binary += [/\xff/n, Regexp.new("\\xff".encode("US-ASCII")), /a/n, /\xa4\xa2/n]

  utf8 + eucjp + sjis + latin + binary
end

settings = [
  ["UTF-8", nil], ["US-ASCII", nil], ["EUC-JP", nil], ["BINARY", nil], ["Shift_JIS", nil],
  ["Windows-1252", nil], ["US-ASCII", "UTF-8"], ["UTF-8", "EUC-JP"], ["US-ASCII", "Windows-1252"],
]

settings.each do |external, internal|
  Encoding.default_external = external
  Encoding.default_internal = internal
  regexps.each_with_index do |re, i|
    line = begin
      show(re.inspect)
    rescue Exception => e
      "#{e.class}: #{e.message}"
    end
    puts "ext=#{external} int=#{internal.inspect} ##{i} #{re.encoding} #{re.source.b.unpack1('H*')}: #{line}"
  end
ensure
  Encoding.default_internal = nil
  Encoding.default_external = "UTF-8"
end

# #to_s and #source are not for a terminal: they keep the characters as they are.
Encoding.default_external = "US-ASCII"
regexps.each_with_index do |re, i|
  puts "to_s ##{i}: #{show(re.to_s)} | source #{show(re.source)}"
end
