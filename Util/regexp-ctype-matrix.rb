# Differential matrix for regexps matched against strings in every ASCII compatible single byte
# encoding - BINARY, ISO-8859-*, KOI8-*, Windows-125*, TIS-620, the IBM and mac code pages.
#
#   ruby    Util/regexp-ctype-matrix.rb > /tmp/sb.mri
#   ./ir.sh Util/regexp-ctype-matrix.rb > /tmp/sb.ir
#   diff -u /tmp/sb.mri /tmp/sb.ir
#
#   ./ir.sh Util/regexp-ctype-matrix.rb KOI8-R     # one encoding (or several)
#
# MRI compiles a regexp that is not fixed-encoding for the subject's encoding
# (rb_reg_prepare_enc), and Onigmo asks that encoding's own ctype table: its own letters,
# punctuation, spaces and word characters, and its own case folds. IronRuby matches over the
# subject decoded to chars and spells the table out (RegexpCtype, generated from CRuby by
# Util/gen-regexp-ctype.rb). Each row lists the bytes 0x00-0xFF a pattern matches in a string of
# one byte, as hex ranges; the fold rows list, per byte, the other bytes a literal of it
# (or a class of it, or a negated class) matches under /i.
#
# A byte the engine does not read as a character in the encoding - /\A.\z/m does not match it -
# is left out of every row and listed in the encoding's "undecoded" row instead. CRuby reads
# every byte of these encodings as a character; IronRuby does not read some that the encoding
# leaves undefined (TIS-620's 0x80-0xA0, Windows-1252's 0x81), which is a difference in decoding,
# not in regexps.

ENCODINGS = Encoding.list.select do |e|
  e.ascii_compatible? && !e.dummy? && !e.name.start_with?("UTF") && e != Encoding::US_ASCII &&
    (0x80..0xff).all? { |b| s = b.chr.force_encoding(e); s.valid_encoding? && s.length == 1 }
end.sort_by(&:name)

selected = ARGV.empty? ? ENCODINGS : ENCODINGS.select { |e| ARGV.include?(e.name) }

POSIX = %w[alnum alpha ascii blank cntrl digit graph lower print punct space upper word xdigit]

def ranges(cs)
  return "-" if cs.empty?
  cs.slice_when { |a, b| b != a + 1 }.map { |r| r.size == 1 ? "%02x" % r[0] : "%02x-%02x" % [r[0], r[-1]] }.join(",")
end

def char(e, b) = b.chr.force_encoding(e)

$bytes = nil

def row(e, label, src, opt = 0)
  r = begin
    re = Regexp.new(src.encode(e), opt)
    hits = []
    errors = []
    $bytes.each do |b|
      begin
        hits << b if re.match?(char(e, b))
      rescue
        errors << b
      end
    end
    ranges(hits) + (errors.empty? ? "" : " error[#{ranges(errors)}]")
  rescue => x
    "#{x.class}: #{x.message[0, 50]}"
  end
  puts "#{e.name} #{label.ljust(22)} #{r}"
end

# with also: a byte the pattern holds besides the one tried, which the row leaves out
def folds(e, label, also = nil)
  out = []
  $bytes.each do |b|
    next if b == also
    re = begin
      yield(char(e, b))
    rescue
      next
    end
    hits = $bytes.select do |c|
      c != b && c != also && (re.match?(char(e, c)) rescue false)
    end
    out << "%02x>%s" % [b, ranges(hits)] unless hits.empty?
  end
  puts "#{e.name} #{label.ljust(22)} #{out.join(' ')}"
end

selected.each do |e|
  any = /\A.\z/m
  $bytes = (0..0xff).select { |b| (any.match?(char(e, b)) rescue false) }
  puts "#{e.name} #{'undecoded'.ljust(22)} #{ranges((0..0xff).to_a - $bytes)}"
  %w[d a u].each do |m|
    POSIX.each do |n|
      row(e, "(?#{m})[[:#{n}:]]", "\\A(?#{m})[[:#{n}:]]\\z")
      row(e, "(?#{m})[[:^#{n}:]]", "\\A(?#{m})[[:^#{n}:]]\\z")
    end
    %w[w W d D s S h H].each { |x| row(e, "(?#{m})\\#{x}", "\\A(?#{m})\\#{x}\\z") }
    %w[w s d].each { |x| row(e, "(?#{m})[\\#{x}]", "\\A(?#{m})[\\#{x}]\\z") }
    row(e, "(?#{m})\\b after", "\\A.(?#{m})\\b")
    row(e, "(?#{m})\\B after", "\\A.(?#{m})\\B")
  end
  POSIX.each do |n|
    row(e, "\\p{#{n}}", "\\A\\p{#{n}}\\z")
    row(e, "\\P{#{n}}", "\\A\\P{#{n}}\\z")
    row(e, "(?a)\\p{#{n}}", "\\A(?a)\\p{#{n}}\\z")
  end
  row(e, "\\R", "\\A\\R\\z")
  row(e, ".", "\\A.\\z")
  %w[upper lower alpha punct].each do |n|
    row(e, "[[:#{n}:]]/i", "\\A[[:#{n}:]]\\z", Regexp::IGNORECASE)
    row(e, "[[:^#{n}:]]/i", "\\A[[:^#{n}:]]\\z", Regexp::IGNORECASE)
    row(e, "\\p{#{n}}/i", "\\A\\p{#{n}}\\z", Regexp::IGNORECASE)
  end
  row(e, "[a-z]/i", "\\A[a-z]\\z", Regexp::IGNORECASE)
  row(e, "[^a-z]/i", "\\A[^a-z]\\z", Regexp::IGNORECASE)
  row(e, "i/i", "\\Ai\\z", Regexp::IGNORECASE)
  row(e, "I/i", "\\AI\\z", Regexp::IGNORECASE)
  folds(e, "literal /i") { |s| Regexp.new("\\A".encode(e) + Regexp.escape(s) + "\\z".encode(e), Regexp::IGNORECASE) }
  folds(e, "class /i") { |s| Regexp.new("\\A[".encode(e) + Regexp.escape(s) + "]\\z".encode(e), Regexp::IGNORECASE) }
  folds(e, "class /i (2)", 0x01) { |s| Regexp.new("\\A[\\x01".encode(e) + Regexp.escape(s) + "]\\z".encode(e), Regexp::IGNORECASE) }
  folds(e, "negated class /i", 0x01) { |s| Regexp.new("\\A[^\\x01".encode(e) + Regexp.escape(s) + "]\\z".encode(e), Regexp::IGNORECASE) }
  folds(e, "(?i:literal)") { |s| Regexp.new("\\A(?i:".encode(e) + Regexp.escape(s) + ")\\z".encode(e)) }
  folds(e, "(?-i:literal)/i") { |s| Regexp.new("\\A(?-i:".encode(e) + Regexp.escape(s) + ")\\z".encode(e), Regexp::IGNORECASE) }
  folds(e, "(literal)+ /i") { |s| Regexp.new("\\A(".encode(e) + Regexp.escape(s) + "){1,2}\\z".encode(e), Regexp::IGNORECASE) }
end

# The same through the entry points, for a few encodings.
puts
%w[ISO-8859-1 ISO-8859-7 ISO-8859-9 KOI8-R Windows-1251 Windows-1252 IBM437 macRoman].each do |name|
  next unless (e = ENCODINGS.find { |x| x.name == name })
  next unless selected.include?(e)
  s = (0x80..0xff).map(&:chr).join.force_encoding(e)
  probes = {
    "scan [[:alpha:]]+" => -> { s.scan(Regexp.new("[[:alpha:]]+")).map(&:bytesize) },
    "gsub [[:punct:]]" => -> { s.gsub(Regexp.new("[[:punct:]]"), "").bytesize },
    "split \\s" => -> { s.split(Regexp.new("\\s")).map(&:bytesize) },
    "scan \\b" => -> { s.scan(Regexp.new("\\b")).size },
    "index (?u)\\w" => -> { s.index(Regexp.new("(?u)\\w")) },
    "scan /i" => -> { s.scan(Regexp.new(s[0x40, 4], Regexp::IGNORECASE)).size },
    "downcase =~ /i" => -> { (0x80..0xff).count { |b| c = char(e, b); (c.downcase =~ Regexp.new(Regexp.escape(c), Regexp::IGNORECASE)) rescue nil } },
  }
  probes.each do |label, probe|
    r = begin
      probe.call.inspect
    rescue => x
      "#{x.class}: #{x.message[0, 50]}"
    end
    puts "#{name} #{label.ljust(22)} #{r}"
  end
end
