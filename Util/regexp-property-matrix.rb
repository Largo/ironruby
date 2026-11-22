# Differential matrix for the encoding of a regexp that uses a character
# property (\p{...}, \P{...}), and for the property names each encoding knows.
#
#   ruby    Util/regexp-property-matrix.rb > /tmp/rp.mri
#   ./ir.sh Util/regexp-property-matrix.rb > /tmp/rp.ir
#   diff /tmp/rp.mri /tmp/rp.ir
#
# MRI's rb_reg_preprocess: a property pins the regexp to the encoding of its
# source even when that source is ASCII only, so /\p{Alpha}/ in a UTF-8 file
# is a UTF-8 regexp with FIXEDENCODING, and matches no string of another
# encoding that is not ASCII only. A property in a comment does not count.
# Onigmo knows only the POSIX names, Word and ASCII in an encoding without
# Unicode's tables (and six scripts in the Japanese ones): \p{L} in US-ASCII
# is a RegexpError.
#
# Left out, and still different: what a property or POSIX class matches in a
# BINARY or US-ASCII string ("\xFF".b =~ /\p{Alpha}/n is nil in MRI, as only
# ASCII letters are letters there; IronRuby reads the byte as U+00FF).

def t(label)
  v = begin
    yield
  rescue Exception => e
    [e.class, e.message]
  end
  puts "#{label}: #{v.inspect}"
end

def d(r) = [r.source, r.options, r.encoding, r.fixed_encoding?]

LITERALS = [/x/, /\p{L}/, /\\p{L}/, /\\\p{L}/, /[\p{L}]/i, /\P{Alpha}/, /\p{^Alpha}/, /\xc2\xa1/n, /x/n,
            /\p{Alpha}/n, /あ/, /x/u, /\p{L}/u, /\p{Alpha}/e, /(?#\p{L})x/, /x # \p{L}
/x, /[#]\p{L}/x, /\p{L}/m, /\h/, /\X/, /\w/, /[[:alpha:]]/, /\p{Any}/, /\p{Hiragana}/]
LITERALS.each_with_index do |r, i|
  t("literal #{i}") { d(r) + [r == r.dup, r.hash == r.dup.hash, r.to_s.encoding, r.inspect.encoding] }
end

a = /\p{L}/
b = /x/
t("union re re") { d(Regexp.union(b, a)) }
t("union re str") { d(Regexp.union(a, "x")) }
t("union str re") { d(Regexp.union("あ", a)) }
t("union n re") { d(Regexp.union(/\p{Alpha}/n, /x/)) }
t("interpolate re") { d(/#{a}y/) }
t("interpolate prop") { d(/#{b}\p{N}/) }
t("interpolate plain") { d(/#{b}y/) }
t("new re") { d(Regexp.new(a)) }
t("new source") { d(Regexp.new(a.source)) }
t("new binary") { d(Regexp.new('\p{Alpha}'.b)) }
t("new ascii alpha") { d(Regexp.new('\p{Alpha}'.encode('US-ASCII'))) }
t("marshal") { d(Marshal.load(Marshal.dump(/\p{L}/))) }
t("marshal n") { d(Marshal.load(Marshal.dump(/\p{Alpha}/n))) }
t("string interpolation") { "#{/\p{L}/}".encoding }

%w[UTF-8 UTF8-MAC UTF8-DoCoMo CESU-8 EUC-JP Shift_JIS US-ASCII BINARY UTF-16LE].each do |n|
  s = begin; "axあ".encode(n); rescue; "ax".encode(n); end
  t("#{n} =~ ascii re") { s =~ /x/ }
  t("#{n} =~ prop re") { s =~ /\p{Alpha}/ }
  t("#{n} =~ prop re, ascii only") { "ax".encode(n) =~ /\p{Alpha}/ }
  t("#{n} =~ utf-8 re") { s =~ Regexp.new("xあ") }
  t("#{n} gsub prop") { s.gsub(/\p{Alpha}/, "-") }
end

names = %w[Alpha alpha ALPHA Alnum Blank Cntrl Digit Graph Lower Print Punct Space Upper XDigit Word ASCII
           Any Assigned L Lu Letter Hiragana Katakana Han Latin Greek Cyrillic Hangul Arabic Emoji
           In_Basic_Latin Age=6.0 X_Digit Hex_Digit Newline]
%w[UTF-8 US-ASCII BINARY EUC-JP Shift_JIS Windows-31J eucJP-ms ISO-8859-1 Windows-1252 GBK Big5 EUC-KR
   CESU-8 UTF8-MAC UTF8-KDDI UTF-16LE KOI8-R].each do |e|
  known = names.select do |n|
    Regexp.new("\\p{#{n}}".encode(e))
    true
  rescue RegexpError
    false
  end
  puts "#{e} names: #{known.join(' ')}"
end
# which properties count: one in a comment does not, as MRI's unescape_nonascii reads them
COMMENTED = [
  ['(?ax)a # \p{L}', 0], ['(?ux)a # \p{L}', 0], ['(?-x:a)# \p{L}', Regexp::EXTENDED], ['(?x:a)# \p{L}', 0],
  ['a(?#\)\p{L})b', 0], ["(a(?x) # \\p{L}\n)# \\p{L}", 0], ["(a(?-x) # \\p{L}\n)# \\p{N}", Regexp::EXTENDED],
  ["a # [\\p{L}\nb", Regexp::EXTENDED], ['(?=a)# \p{L}', Regexp::EXTENDED], ['(?<n>a)# \p{L}', Regexp::EXTENDED],
  ['\((?x)# \p{L}', 0], ["(?:a(?x) # \\p{L}\n)# \\p{L}", 0], ['(?i:(?x)a)# \p{L}', 0], ['(?x:(?-x)a)# \p{L}', 0],
  ["(?x:(?-x:a)# \\p{L}\n)", 0], ["(?x:a # \\p{L}\n)b", 0],
]
COMMENTED.each do |source, options|
  t("commented #{source.inspect} #{options}") { d(Regexp.new(source.encode("US-ASCII"), options)) }
  t("commented utf-8 #{source.inspect} #{options}") { d(Regexp.new(source, options)) }
end

t("error L") { Regexp.new('\p{L}'.encode('US-ASCII')) }
t("error Hangul") { Regexp.new('a\P{Hangul}b'.encode('EUC-JP')) }
t("error ^L") { Regexp.new('[\p{^L}]'.encode('ISO-8859-1')) }
t("error options") { Regexp.new('\p{L}'.encode('US-ASCII'), Regexp::IGNORECASE | Regexp::EXTENDED) }
t("error Foo") { Regexp.new('\p{Foo}') }
