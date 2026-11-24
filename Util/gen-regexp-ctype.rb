#!/usr/bin/env ruby
# Generates Src/Ruby/Builtins/RegexpCtype.Generated.cs: the Onigmo ctype table of every ASCII
# compatible single byte encoding, as CRuby matches a regexp against a string in it.
#
# MRI compiles a regexp that is not fixed-encoding for the subject's encoding
# (rb_reg_prepare_enc), and Onigmo then asks that encoding's own ctype table, which is not
# Unicode's: ISO-8859-1's punctuation is U+00AD but not U+00A7, its U+0085 is no space, the IBM
# and mac code pages (Onigmo makes them replicas of ASCII-8BIT) have no letters, digits or
# punctuation above 0x7F at all, and each case-folds only what its own table pairs up. IronRuby
# runs a CLR regex over the subject decoded to chars, so RegexpTransformer spells these tables
# out (RegexpCtype). They are asked of CRuby, byte by byte:
#
# - the 14 POSIX classes, [[:alpha:]] ... [[:xdigit:]], as bit masks over the 256 bytes;
# - \p{Alpha} ... where it is not the POSIX class of the same name (Windows-1251's \p{Print}
#   takes in "\t");
# - the case folds: the bytes a literal byte matches under /i, but for A-Z/a-z, and the byte
#   itself where its literal does not match it (Windows-1252's /\xAF/i matches "\xBF" but not
#   "\xAF"); and, where they differ, the bytes a class of one byte matches ([i]/i does not take
#   in ISO-8859-9's dotted capital I, which i/i does).
#
# Encodings with the same table share an entry. The generator also checks the rules
# RegexpTransformer applies on top of a table, the same for every encoding, and stops if a
# CRuby breaks one: \s is the table's space in every mode; \w and \b are its word characters
# under (?u) and (?d) - \w only under (?u) - and ASCII ones under (?a); \d is ASCII but under
# (?u); (?a) restricts [[:graph:]], [[:print:]] and [[:word:]] to ASCII and leaves the other
# POSIX classes and \p{...} as they are; \R is "\r\n" or one of \n \v \f \r.
#
#   ruby Util/gen-regexp-ctype.rb [output.cs]

abort "run this with CRuby (it asks CRuby's own regexps)" if RUBY_ENGINE != "ruby"

out = ARGV[0] || File.expand_path("../Src/Ruby/Builtins/RegexpCtype.Generated.cs", __dir__)
$VERBOSE = nil

POSIX = %w[alnum alpha ascii blank cntrl digit graph lower print punct space upper word xdigit]
ASCII = (0..0x7f).to_a

encodings = Encoding.list.select do |e|
  e.ascii_compatible? && !e.dummy? && !e.name.start_with?("UTF") && e != Encoding::US_ASCII &&
    (0x80..0xff).all? { |b| s = b.chr.force_encoding(e); s.valid_encoding? && s.length == 1 }
end
abort "no ASCII-8BIT" unless encodings.include?(Encoding::BINARY)

def char(e, b) = b.chr.force_encoding(e)

def members(e, src, opt = 0)
  re = Regexp.new(src.encode(e), opt)
  (0..0xff).select { |b| re.match?(char(e, b)) }
end

def check(e, what, actual, expected)
  return if actual == expected
  abort "#{e.name}: #{what} is #{actual.map { |b| '%02x' % b }.join(' ')}, the generator expected " \
        "#{expected.map { |b| '%02x' % b }.join(' ')} - see the rules at the top of this file"
end

def table(e)
  posix = POSIX.to_h { |n| [n, members(e, "\\A[[:#{n}:]]\\z")] }
  properties = POSIX.to_h { |n| [n, members(e, "\\A\\p{#{n}}\\z")] }.reject { |n, m| m == posix[n] }

  check(e, "[[:ascii:]]", posix["ascii"], ASCII)
  %w[d a u].each { |m| check(e, "(?#{m})\\s", members(e, "\\A(?#{m})\\s\\z"), posix["space"]) }
  check(e, "(?u)\\w", members(e, "\\A(?u)\\w\\z"), posix["word"])
  %w[d a].each { |m| check(e, "(?#{m})\\w", members(e, "\\A(?#{m})\\w\\z"), posix["word"] & ASCII) }
  check(e, "(?u)\\d", members(e, "\\A(?u)\\d\\z"), posix["digit"])
  %w[d a].each { |m| check(e, "(?#{m})\\d", members(e, "\\A(?#{m})\\d\\z"), posix["digit"] & ASCII) }
  %w[d u].each { |m| check(e, "(?#{m})\\b", members(e, "\\A.(?#{m})\\b"), posix["word"]) }
  check(e, "(?a)\\b", members(e, "\\A.(?a)\\b"), posix["word"] & ASCII)
  check(e, "\\R", members(e, "\\A\\R\\z"), [0x0a, 0x0b, 0x0c, 0x0d])
  POSIX.each do |n|
    restricted = %w[graph print word].include?(n)
    check(e, "(?a)[[:#{n}:]]", members(e, "\\A(?a)[[:#{n}:]]\\z"), restricted ? posix[n] & ASCII : posix[n])
    check(e, "(?a)\\p{#{n}}", members(e, "\\A(?a)\\p{#{n}}\\z"), properties[n] || posix[n])
  end

  folds = fold_pairs(e) { |s| Regexp.escape(s) }
  # (with a second member: Onigmo makes a class of one character a literal of it)
  class_folds = fold_pairs(e, 0x01) { |s| "[\\x01" + Regexp.escape(s) + "]" }
  class_folds = nil if class_folds == folds
  ASCII.select { |c| c.chr =~ /[A-Za-z]/ }.each do |c|
    other = c.chr.swapcase.ord
    abort "#{e.name}: #{c.chr} does not fold onto #{other.chr}" unless Regexp.new(c.chr, Regexp::IGNORECASE).match?(char(e, other))
  end

  [posix, properties, folds, class_folds]
end

# (a << 8) | b for each byte b the pattern made of byte a matches under /i, but A-Z/a-z; and
# (a << 8) | a where it does not match a itself
def fold_pairs(e, also = nil)
  pairs = []
  (0..0xff).each do |a|
    next if a == also
    re = Regexp.new("\\A".encode(e) + yield(char(e, a)) + "\\z".encode(e), Regexp::IGNORECASE)
    pairs << ((a << 8) | a) unless re.match?(char(e, a))
    (0..0xff).each do |b|
      next if a == b || b == also || !re.match?(char(e, b))
      ascii_pair = a < 0x80 && b < 0x80 && a.chr.downcase == b.chr.downcase
      pairs << ((a << 8) | b) unless ascii_pair
    end
  end
  pairs
end

def masks(bytes) = (0..3).map { |q| bytes.select { |b| b / 64 == q }.sum { |b| 1 << (b % 64) } }
def hex(values) = values.map { |v| format("0x%016XUL", v) }.join(", ")

entries = {}
encodings.sort_by(&:name).each do |e|
  t = table(e)
  (entries[t] ||= []) << e.name
end
# US-ASCII's characters are ASCII-8BIT's ASCII ones, with the same table (a US-ASCII regexp with
# a property matches ASCII text with it)
entries.each_value { |names| names << "US-ASCII" if names.include?("ASCII-8BIT") }

lines = []
lines << "// <auto-generated> generated by Util/gen-regexp-ctype.rb from CRuby #{RUBY_VERSION} - do not edit </auto-generated>"
lines << "namespace IronRuby.Builtins {"
lines << "    internal sealed partial class RegexpCtype {"
lines << "        // Per table: the encodings that have it; the POSIX classes (in PosixNames' order) as"
lines << "        // four bit masks over the bytes each; the \\p{...} that differ from them, name then"
lines << "        // masks; the case folds, (byte << 8) | the byte a literal byte also matches under /i (a byte"
lines << "        // with itself where its literal does not match it);"
lines << "        // and the same for a class of one byte, where it is not the same (else null)."
lines << "        private static readonly Data[] _data = new Data[] {"
entries.sort_by { |_, names| names.first }.each do |(posix, properties, folds, class_folds), names|
  lines << "            new Data("
  lines << "                new string[] { #{names.map { |n| "\"#{n}\"" }.join(', ')} },"
  lines << "                new ulong[] {"
  POSIX.each { |n| lines << "                    #{hex(masks(posix[n]))}, // #{n}" }
  lines << "                },"
  lines << "                new object[] { #{properties.map { |n, m| "\"#{n}\", new ulong[] { #{hex(masks(m))} }" }.join(', ')} },"
  [folds, class_folds].each_with_index do |pairs, i|
    last = i == 1
    if pairs.nil?
      lines << "                null),"
      next
    end
    text = pairs.map { |f| format("0x%04X", f) }.each_slice(12).map { |s| "                    " + s.join(", ") + "," }
    if text.empty?
      lines << "                new ushort[] { }#{last ? '),' : ','}"
    else
      lines << "                new ushort[] {"
      lines.concat(text)
      lines << "                }#{last ? '),' : ','}"
    end
  end
end
lines << "        };"
lines << "    }"
lines << "}"
File.write(out, lines.join("\r\n") + "\r\n")
puts "#{out}: #{entries.size} tables for #{entries.values.sum(&:size)} encodings"
