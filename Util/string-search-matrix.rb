# Differential matrix for searching a String: #index, #rindex, #byteindex, #byterindex,
# #include?, #start_with?, #end_with? with a String or a Regexp needle, and Regexp#match?, #=~,
# #match and String#match?, #=~, #match, with what each leaves in $~.
#
#   ruby    Util/string-search-matrix.rb > /tmp/strsearch.mri
#   ./ir.sh Util/string-search-matrix.rb > /tmp/strsearch.ir
#   diff -u /tmp/strsearch.mri /tmp/strsearch.ir
#
# The receivers are ASCII, UTF-8 with two- and three-byte characters, UTF-8 with a character above
# U+FFFF (a surrogate pair in IronRuby's UTF-16), BINARY with high bytes, broken UTF-8, US-ASCII,
# EUC-JP and UTF-16LE, each in the content kinds a String can be in here (a literal, one built by
# <<, one sliced, one force_encoded, one that has been indexed by character already), and a
# subclass. The needles are one and three characters, empty, broken, of another encoding, an
# object with #to_str, and a Regexp; the offsets are positive, negative, past the end and at it.
# A row prints the answer and $~ (its begin, or nil, or "kept" when the call must leave it alone),
# or the exception's class and message.

def show(v)
  case v
  when MatchData then "#<MatchData #{v[0].inspect} #{v.begin(0)}>"
  else v.inspect
  end
end

# $~ belongs to the frame of the code that matched, so each block sets it to a sentinel match
# itself and hands back the answer, $~ before and $~ after.
SENTINEL = /sentinel/
def try(label)
  v, before, after = yield
  m = after.equal?(before) ? "kept" : (after ? after.begin(0).inspect : "nil")
  puts "#{label}\t#{show(v)}\t$~=#{m}"
rescue Exception => e
  puts "#{label}\t#{e.class}: #{e.message}"
end

class SubStr < String; end
class ToStr; def initialize(s) = @s = s; def to_str = @s; end

def kinds(base)
  out = {}
  out[:lit] = base.dup
  b = String.new(encoding: base.encoding); base.each_char { |c| b << c }; out[:built] = b
  out[:slice] = ("_".encode(base.encoding) + base)[1..]
  out[:forced] = base.b.force_encoding(base.encoding)
  ix = base.dup; ix[0] if ix.size > 0; ix.size; out[:indexed] = ix
  out[:sub] = SubStr.new(base)
  out
end

RECEIVERS = {
  ascii:  'ab"cd ab"cd q"z end',
  utf8:   'éa"日cd é"b q"z fin',
  astral: "😀a\"𝒳c q\"z 😀\"",
  binary: "ab\"\xE9\xFFcd \xE9\"q\"z".b,
  broken: "ab\"\xE9cd q\"z \xFF\"".dup.force_encoding("UTF-8"),
  usascii: 'ab"cd q"z'.dup.force_encoding("US-ASCII"),
  eucjp:  "a\"\xA4\xA2\"q\"z".dup.force_encoding("EUC-JP"),
  utf16:  'a"b q"z'.encode("UTF-16LE"),
  empty:  "",
}

NEEDLES = {
  q1: '"', q3: 'q"z', none: '#', empty: "",
  nonascii: "é", astral: "😀", bin1: '"'.b, binhi: "\xE9".b,
  broken: "\xE9".dup.force_encoding("UTF-8"), eucjp: "\xA4\xA2".dup.force_encoding("EUC-JP"),
  utf16: '"'.encode("UTF-16LE"), tostr: ToStr.new('"'), sub: SubStr.new('q"z'),
}
OFFSETS = [nil, 0, 1, 3, -1, -3, 100, -100]

RECEIVERS.each do |rname, base|
  kinds(base).each do |kname, s|
    puts "== #{rname}/#{kname} #{s.encoding} valid=#{s.valid_encoding?} size=#{s.size} bytesize=#{s.bytesize}"
    NEEDLES.each do |nname, n|
      OFFSETS.each do |off|
        a = off.nil? ? "" : ", #{off}"
        try("index(#{nname}#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? s.index(n) : s.index(n, off)), b, $~] }
        try("rindex(#{nname}#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? s.rindex(n) : s.rindex(n, off)), b, $~] }
        try("byteindex(#{nname}#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? s.byteindex(n) : s.byteindex(n, off)), b, $~] }
        try("byterindex(#{nname}#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? s.byterindex(n) : s.byterindex(n, off)), b, $~] }
      end
      try("include?(#{nname})") { "sentinel" =~ SENTINEL; b = $~; [(s.include?(n)), b, $~] }
      try("start_with?(#{nname})") { "sentinel" =~ SENTINEL; b = $~; [(s.start_with?(n)), b, $~] }
      try("end_with?(#{nname})") { "sentinel" =~ SENTINEL; b = $~; [(s.end_with?(n)), b, $~] }
    end
    [/"/, /q"z/, /[<>"]/, /\A[+-]?\d+/, /\Aa/, /z/, /é/, /😀/, /./, /\b/, /(?<x>")/].each do |re|
      OFFSETS.each do |off|
        a = off.nil? ? "" : ", #{off}"
        try("#{re.inspect}.match?(s#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? re.match?(s) : re.match?(s, off)), b, $~] }
        try("#{re.inspect}.match(s#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? re.match(s) : re.match(s, off)), b, $~] }
        try("s.match?(#{re.inspect}#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? s.match?(re) : s.match?(re, off)), b, $~] }
        try("s.index(#{re.inspect}#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? s.index(re) : s.index(re, off)), b, $~] }
        try("s.rindex(#{re.inspect}#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? s.rindex(re) : s.rindex(re, off)), b, $~] }
        try("s.byteindex(#{re.inspect}#{a})") { "sentinel" =~ SENTINEL; b = $~; [(off.nil? ? s.byteindex(re) : s.byteindex(re, off)), b, $~] }
      end
      try("#{re.inspect} =~ s") { "sentinel" =~ SENTINEL; b = $~; [(re =~ s), b, $~] }
      try("s =~ #{re.inspect}") { "sentinel" =~ SENTINEL; b = $~; [(s =~ re), b, $~] }
      try("s.match(#{re.inspect})") { "sentinel" =~ SENTINEL; b = $~; [(s.match(re)), b, $~] }
      try("s.start_with?(#{re.inspect})") { "sentinel" =~ SENTINEL; b = $~; [(s.start_with?(re)), b, $~] }
      try("#{re.inspect} === s") { "sentinel" =~ SENTINEL; b = $~; [(re === s), b, $~] }
    end
    try("s.match?('q\"z')") { "sentinel" =~ SENTINEL; b = $~; [(s.match?('q"z')), b, $~] }
    try("s.match('q\"z')") { "sentinel" =~ SENTINEL; b = $~; [(s.match('q"z')), b, $~] }
  end
end

puts "== odd operands"
try("/a/.match?(nil)") { "sentinel" =~ SENTINEL; b = $~; [(/a/.match?(nil)), b, $~] }
try("/a/ =~ nil") { "sentinel" =~ SENTINEL; b = $~; [(/a/ =~ nil), b, $~] }
try("/a/.match(nil)") { "sentinel" =~ SENTINEL; b = $~; [(/a/.match(nil)), b, $~] }
try("/a/.match?(:bab)") { "sentinel" =~ SENTINEL; b = $~; [(/a/.match?(:bab)), b, $~] }
try("/a/ =~ :bab") { "sentinel" =~ SENTINEL; b = $~; [(/a/ =~ :bab), b, $~] }
try("/a/.match?(1)") { "sentinel" =~ SENTINEL; b = $~; [(/a/.match?(1)), b, $~] }
try("/a/.match?(ToStr)") { "sentinel" =~ SENTINEL; b = $~; [(/a/.match?(ToStr.new("xa"))), b, $~] }
try("'ab'.index(1)") { "sentinel" =~ SENTINEL; b = $~; [("ab".index(1)), b, $~] }
try("'ab'.index(nil)") { "sentinel" =~ SENTINEL; b = $~; [("ab".index(nil)), b, $~] }
try("'ab'.index('a', 'x')") { "sentinel" =~ SENTINEL; b = $~; [("ab".index("a", "x")), b, $~] }
try("'ab'.index('b', 1.9)") { "sentinel" =~ SENTINEL; b = $~; [("ab".index("b", 1.9)), b, $~] }
try("'ab'.include?(nil)") { "sentinel" =~ SENTINEL; b = $~; [("ab".include?(nil)), b, $~] }
try("'ab'.include?(ToStr)") { "sentinel" =~ SENTINEL; b = $~; [("ab".include?(ToStr.new("b"))), b, $~] }
try("/\\xff/n.match?(utf8)") { "sentinel" =~ SENTINEL; b = $~; [(Regexp.new("\xff".b).match?("é")), b, $~] }
try("/é/.match?(binary)") { "sentinel" =~ SENTINEL; b = $~; [(/é/.match?("\xC3\xA9".b)), b, $~] }
try("/é/.match?(eucjp)") { "sentinel" =~ SENTINEL; b = $~; [(/é/.match?("\xA4\xA2".dup.force_encoding("EUC-JP"))), b, $~] }
s = "abc\"def"
try("index after <<") { "sentinel" =~ SENTINEL; b = $~; [(s << "\"x"; s.index('"', 4)), b, $~] }
try("index after setbyte") { "sentinel" =~ SENTINEL; b = $~; [(t = +"abc"; t.setbyte(1, 0x22); t.index('"')), b, $~] }
try("frozen literal index") { "sentinel" =~ SENTINEL; b = $~; [('x"y'.freeze.index('"')), b, $~] }
