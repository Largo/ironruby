# Differential matrix for character types in a regexp matched against a BINARY or US-ASCII
# subject, against ISO-8859-1 and UTF-8 subjects as controls.
#
#   ruby    Util/regexp-binary-ctype-matrix.rb > /tmp/ctype.mri
#   ./ir.sh Util/regexp-binary-ctype-matrix.rb > /tmp/ctype.ir
#   diff -u /tmp/ctype.mri /tmp/ctype.ir
#
# MRI compiles a regexp that is not fixed-encoding against the subject's encoding
# (rb_reg_prepare_enc), and Onigmo's ASCII-8BIT and US-ASCII ctype tables have no letters,
# digits, spaces or punctuation above 0x7F, nor any case pairs there. So against a binary
# string \p{Alpha}, [[:alpha:]], (?u)\w, \b and /i see the bytes 0x80-0xFF as nothing at all,
# while ISO-8859-1 keeps its own letters ("\xE9" is e-acute) and UTF-8 all of Unicode's.
# IronRuby matches one CLR regex over the subject decoded to chars, where a binary byte
# 0x80-0xFF is U+0080-U+00FF and .NET would call most of them letters.
#
# A row lists, for each subject encoding, which of the characters 0x80-0xFF (and 0x00-0x7F,
# as a check that ASCII is unchanged) the pattern matches, as hex ranges.
#
# The BINARY, US-ASCII and ISO-8859-1 columns and the entry points agree with CRuby 4.0; an
# ISO-8859-1 subject is matched with Onigmo's ISO-8859-1 ctype table (its own punctuation,
# U+00A0 as its one space above ASCII, the superscripts and fractions as word characters, and
# (?a) restricting only [[:graph:]] [[:print:]] [[:word:]] and \b). Rows that still differ, in
# the UTF-8 column and older than this matrix: UTF-8's \b and \B do not count U+00B2 U+00B3
# U+00B9 U+00BC-U+00BE as word characters, and UTF-8's [[:upper:]]/i misses U+00B5. Neither
# encoding folds "\u00DF" onto "ss" under /i, as Onigmo does.

require "strscan"

ENCODINGS = [Encoding::BINARY, Encoding::ISO_8859_1, Encoding::UTF_8]

def subject(code, enc)
  if enc == Encoding::UTF_8
    code.chr(Encoding::UTF_8)
  else
    code.chr.force_encoding(enc)
  end
end

def ranges(codes)
  return "-" if codes.empty?
  out = []
  start = prev = codes.first
  codes.drop(1).each do |c|
    if c == prev + 1
      prev = c
      next
    end
    out << (start == prev ? "%02x" % start : "%02x-%02x" % [start, prev])
    start = prev = c
  end
  out << (start == prev ? "%02x" % start : "%02x-%02x" % [start, prev])
  out.join(",")
end

def attempt
  yield
rescue Exception => e
  "#{e.class}: #{e.message[0, 70]}"
end

# Which single characters of each encoding the regexp matches, as a whole-string match.
def row(label, re)
  print label.ljust(34), " ", "#{re.encoding.name}#{re.fixed_encoding? ? '!' : ''}".ljust(11)
  cols = ENCODINGS.map do |enc|
    attempt do
      lo = (0..0x7f).select { |c| re.match?(subject(c, enc)) }
      hi = (0x80..0xff).select { |c| re.match?(subject(c, enc)) }
      "#{enc.name}[#{ranges(lo)} | #{ranges(hi)}]"
    end
  end
  # US-ASCII: only ASCII strings are valid, so the ASCII half is all there is to compare
  cols << attempt do
    lo = (0..0x7f).select { |c| re.match?(c.chr.force_encoding(Encoding::US_ASCII)) }
    "US-ASCII[#{ranges(lo)}]"
  end
  puts cols.join("  ")
end

def build(src, opts = 0)
  attempt { Regexp.new(src, opts) }
end

# Each pattern is made from a BINARY source and from a UTF-8 one. A property escape makes the
# regexp fixed-encoding (BINARY from a BINARY source, US-ASCII from a UTF-8 one, which then
# refuses a non-ASCII subject of any other encoding); anything else gives a US-ASCII regexp that
# is not fixed and is compiled for the subject's encoding. The column after the pattern is the
# regexp's encoding, with ! when it is fixed.
PATTERNS = [
  '\A\p{Alpha}\z', '\A\p{^Alpha}\z', '\A\P{Alpha}\z', '\A\p{Word}\z', '\A\p{Lower}\z',
  '\A\p{Upper}\z', '\A\p{Alnum}\z', '\A\p{Punct}\z', '\A\p{Space}\z', '\A\p{Print}\z',
  '\A\p{Graph}\z', '\A\p{Cntrl}\z', '\A\p{Digit}\z', '\A\p{XDigit}\z', '\A\p{Blank}\z',
  '\A\p{ASCII}\z', '\A\p{Any}\z',
  '\A[[:alpha:]]\z', '\A[[:^alpha:]]\z', '\A[^[:alpha:]]\z', '\A[[:punct:]]\z', '\A[[:lower:]]\z',
  '\A[[:upper:]]\z', '\A[[:alnum:]]\z', '\A[[:space:]]\z', '\A[[:print:]]\z', '\A[[:graph:]]\z',
  '\A[[:word:]]\z', '\A[[:cntrl:]]\z', '\A[[:xdigit:]]\z', '\A[[:ascii:]]\z',
  '\A[[:alpha:]&&[^a-z]]\z', '\A[\p{Alpha}0-9]\z', '\A[^\p{Alpha}]\z', '\A[\w\x80]\z',
  '\A\w\z', '\A\W\z', '\A\d\z', '\A\s\z', '\A\h\z', '\A\H\z',
  '\A(?u)\w\z', '\A(?u)\W\z', '\A(?u)\s\z', '\A(?u)\d\z', '\A(?u:[\w])\z',
  '\A(?a)[[:alpha:]]\z', '\A(?a)\p{Alpha}\z',
  '\b', '\B', '\A.\b', '(?u)\b', '\A\R\z', '\A\X\z', '\A.\z',
  '\A\p{Greek}\z', '\A\p{L}\z', '\A\p{Latin}\z',
]

PATTERNS.each do |src|
  [[src.b, ""], [src, " (utf8 src)"]].each do |source, tag|
    re = build(source)
    if re.is_a?(String)
      puts "#{(src + tag).ljust(34)} #{re}"
    else
      row(src + tag, re)
    end
  end
end

puts
puts "# case-insensitive"
PATTERNS_I = [
  '\A\p{Lower}\z', '\A\p{Upper}\z', '\A[[:lower:]]\z', '\A[[:upper:]]\z', '\A[a-z]\z', '\A\w\z',
  '\A[^a-z]\z', '\A(?u)\w\z',
]
PATTERNS_I.each do |src|
  re = build(src.b, Regexp::IGNORECASE)
  if re.is_a?(String)
    puts "#{(src + '/i').ljust(34)} #{re}"
  else
    row(src + "/i", re)
  end
end

# A pattern that spells a non-ASCII character: /i must fold it in ISO-8859-1 and UTF-8 only.
puts
puts "# non-ASCII literals, /i"
[[0xe9, 0xc9], [0xc9, 0xe9], [0xff, nil], [0xe0, 0xc0], [0xb5, nil], [0xdf, nil], [0xe9, 0xe9]].each do |pat, other|
  ENCODINGS.each do |enc|
    pattern = subject(pat, enc)
    [0, Regexp::IGNORECASE].each do |opt|
      re = attempt { Regexp.new(pattern, opt) }
      results = (0x80..0xff).select { |c| attempt { re.match?(subject(c, enc)) } == true }
      puts "lit %02x %-10s %-2s => %s" % [pat, enc.name, opt == 0 ? "" : "i", ranges(results)]
    end
    # a class range with a non-ASCII bound
    lo = [pat, other || pat].min
    hi = [pat, other || pat].max
    cls = "[".encode(enc) + subject(lo, enc) + "-".encode(enc) + subject(hi, enc) + "]".encode(enc)
    re = attempt { Regexp.new(cls, Regexp::IGNORECASE) }
    results = (0x80..0xff).select { |c| attempt { re.match?(subject(c, enc)) } == true }
    puts "cls %02x-%02x %-10s i  => %s" % [lo, hi, enc.name, ranges(results)]
    re = attempt { Regexp.new("[^".encode(enc) + subject(pat, enc) + "]".encode(enc), Regexp::IGNORECASE) }
    results = (0x80..0xff).select { |c| attempt { re.match?(subject(c, enc)) } == true }
    puts "neg %02x %-10s i     => %s" % [pat, enc.name, ranges(results)]
  end
end

# The /n literals: an ASCII-only /n regexp is US-ASCII and not fixed; one with a byte escape
# above 0x7F is fixed BINARY.
puts
puts "# /n literals"
NREGEXPS = {
  '/\p{Alpha}/n' => /\p{Alpha}/n, '/[[:alpha:]]/n' => /[[:alpha:]]/n, '/\w/n' => /\w/n,
  '/(?u)\w/n' => /(?u)\w/n, '/\xff|\p{Alpha}/n' => /\xff|\p{Alpha}/n,
  '/\xff|[[:alpha:]]/n' => /\xff|[[:alpha:]]/n, '/\xff|\b/n' => /\xff|\b/n,
  '/\xe9/in' => /\xe9/in, '/[\xe0-\xef]/in' => /[\xe0-\xef]/in, '/\p{Lower}/in' => /\p{Lower}/in,
  '/\p{Alpha}/u' => /\p{Alpha}/u, '/[[:alpha:]]/' => /[[:alpha:]]/,
}
NREGEXPS.each do |label, re|
  print label.ljust(34), " enc=#{re.encoding} fixed=#{re.fixed_encoding?} "
  hi = (0x80..0xff).map { |c| [c, attempt { (c.chr.b =~ re) }] }
  matched = hi.select { |_, r| r.is_a?(Integer) }.map(&:first)
  errors = hi.map(&:last).grep(String).uniq
  puts "BINARY[#{ranges(matched)}]#{errors.empty? ? '' : ' ' + errors.join(' ')}"
end

# The same question through every matching entry point, on a mixed binary string.
puts
puts "# entry points"
s = "a\xE9b\xFF c\xC9".b
re = Regexp.new('\p{Alpha}+')
ri = Regexp.new('[a-z]+', Regexp::IGNORECASE)
rb = Regexp.new('\b')
[
  ["=~", -> { s =~ Regexp.new('[[:alpha:]]{2}') }],
  ["match", -> { s.match(re)&.to_a }],
  ["match?", -> { s.match?(Regexp.new('\p{Alpha}\p{Alpha}')) }],
  ["scan", -> { s.scan(re) }],
  ["scan /i", -> { s.scan(ri) }],
  ["scan \\b", -> { s.scan(Regexp.new('\b.')) }],
  ["gsub", -> { s.gsub(re, "<\\0>") }],
  ["sub", -> { s.sub(Regexp.new('[[:alpha:]](?=[[:alpha:]])'), "#") }],
  ["split", -> { s.split(Regexp.new('[[:^alpha:]]')) }],
  ["split \\b", -> { s.split(rb) }],
  ["index", -> { s.index(Regexp.new('\p{Alpha}'), 1) }],
  ["rindex", -> { s.rindex(Regexp.new('\p{Alpha}')) }],
  ["[]", -> { s[Regexp.new('\p{Alpha}{2,}')] }],
  ["slice!", -> { t = s.dup; [t.slice!(Regexp.new('[[:alpha:]]+\z')), t] }],
  ["start_with?", -> { s.start_with?(Regexp.new('\p{Alpha}{2}')) }],
  ["partition", -> { s.partition(Regexp.new('[[:alpha:]]{2}')) }],
  ["count words", -> { s.scan(Regexp.new('\p{Word}+')).size }],
  ["tr-like gsub", -> { s.gsub(Regexp.new('[^[:alpha:]]'), ".") }],
  ["StringScanner#scan", -> { ss = StringScanner.new(s); [ss.scan(re), ss.scan(re), ss.pos] }],
  ["StringScanner#scan_until", -> { ss = StringScanner.new(s); [ss.scan_until(Regexp.new('\p{Alpha}{2}')), ss.pos] }],
  ["StringScanner#check", -> { ss = StringScanner.new(s); ss.pos = 1; ss.check(Regexp.new('[[:alpha:]]')) }],
  ["Regexp#match pos", -> { re.match(s, 2)&.begin(0) }],
  ["Regexp.union", -> { s.scan(Regexp.union(re, /\d/)) }],
  ["same re, UTF-8 then BINARY", -> { [re.match?("é"), re.match?("\xE9".b), re.match?("é")] }],
  ["same re, ISO-8859-1, BINARY", -> { [re.match?("\xE9".dup.force_encoding("ISO-8859-1")), re.match?("\xE9".b)] }],
  ["astral UTF-8 then BINARY", -> { [re.match?("\u{1D4B3}é"), re.match?("\xE9".b)] }],
].each do |label, probe|
  puts "#{label.ljust(30)} => #{attempt { probe.call }.inspect}"
end

# ISO-8859-1: the same through the entry points, and the (?a) quirks.
puts
puts "# ISO-8859-1"
l = "a\xE9b\xB2 c\xA0d\xAD\xA7".dup.force_encoding(Encoding::ISO_8859_1)
def latin1(src) = Regexp.new(src.dup.force_encoding(Encoding::ISO_8859_1))
[
  ["scan [[:alpha:]]+", -> { l.scan(/[[:alpha:]]+/) }],
  ["scan \\w (?u)", -> { l.scan(/(?u)\w+/) }],
  ["split \\s", -> { l.split(/\s/) }],
  ["split \\b", -> { l.split(/\b/) }],
  ["gsub [[:punct:]]", -> { l.gsub(/[[:punct:]]/, "!") }],
  ["index \\p{Space}", -> { l.index(latin1('\p{Space}')) }],
  ["(?a) posix", -> { %w[alpha punct space graph print word].map { |n| l.scan(Regexp.new("(?a)[[:#{n}:]]")).size } }],
  ["(?a)\\b", -> { l.scan(/(?a)\b./) }],
  ["\\R", -> { "\r\n\x85\n".dup.force_encoding(Encoding::ISO_8859_1).scan(/\R/) }],
  ["upper /i", -> { "\xDF\xFF\xC9\xE9".dup.force_encoding(Encoding::ISO_8859_1).scan(/[[:upper:]]/i) }],
  ["StringScanner", -> { ss = StringScanner.new(l); [ss.scan(/[[:alpha:]]+/), ss.scan(/[[:alpha:]\s]+/), ss.pos] }],
  ["latin1 regexp, ASCII subject", -> { ["$" =~ latin1('\p{Punct}'), "$" =~ /\p{Punct}/u] }],
  ["one regexp, three encodings", -> { re = /\s/; [re.match?("\u00a0"), re.match?("\xA0".b), re.match?("\xA0".dup.force_encoding(Encoding::ISO_8859_1))] }],
  ["(?a)\\b in UTF-8", -> { ["\u00e9" =~ /(?a)\b/, "\u00e9a" =~ /(?a)\Ba/, "a\u00e9" =~ /(?a)a\b/] }],
].each do |label, probe|
  puts "#{label.ljust(30)} => #{attempt { probe.call }.inspect}"
end
