# Differential matrix for the encoding Regexp.new gives a pattern whose
# non-ASCII bytes stand right after a backslash.
#
#   ruby    Util/regexp-escaped-byte-matrix.rb > /tmp/reb.mri
#   ./ir.sh Util/regexp-escaped-byte-matrix.rb > /tmp/reb.ir
#   diff /tmp/reb.mri /tmp/reb.ir
#
# MRI's unescape_nonascii pins the regexp to its source's encoding when it
# meets a non-ASCII character. A character that follows a backslash is only
# counted when it is longer than one byte (the backslash goes with it); a
# single non-ASCII byte there - possible in BINARY and the other single byte
# encodings, where every byte is a character - falls through to "escape as
# is" and leaves the encoding unset, so rb_reg_initialize makes the regexp
# US-ASCII and not fixed: Regexp.new("\\\xFF".b) is a US-ASCII regexp whose
# source holds the byte 0xFF. In a multibyte encoding the bytes after the
# backslash are either a character (EUC-JP "\\\xA4\xA2" pins EUC-JP) or
# invalid ("invalid multibyte character").
#
# Such a US-ASCII regexp is not fixed, so MRI recompiles it in the encoding
# of a non-ASCII string it is matched against (rb_reg_prepare_re): in BINARY
# or ISO-8859-1 the byte is a character again and the match goes ahead; in
# UTF-8 the backslash and the byte are "regexp preprocess failed: invalid
# multibyte character". Its source is a US-ASCII string holding a byte that
# is not US-ASCII, so Regexp.new(r.source) raises.
#
# A comment - (?#...), or # to the end of the line in extended mode - pins
# the encoding only in UTF-8, and the bytes of a # comment are not checked.
# unescape_nonascii stops at the first thing wrong, so "\\x\xFF" is an
# "invalid hex escape" in UTF-8 too. Error messages quote the pattern as
# rb_reg_error_desc does: bytes, in the pattern's encoding, written for the
# default external encoding (so run both with the same LANG).
#
# Left out, and still different: Regexp#inspect of a regexp whose source holds
# characters outside ASCII and is not in the default external encoding: MRI
# escapes them (rb_reg_desc, "/\\\xA4\\xA2/" for BINARY "\\\xA4\xA2"), IronRuby
# does not. That is Regexp#inspect's own difference, not this one's.

$stdout.sync = true

def t(label)
  v = begin
    yield
  rescue Exception => e
    [e.class, e.message]
  end
  puts "#{label}: #{v.inspect}"
end

# (#inspect only where it writes the source as it is; see above)
def d(r) = [r.encoding, r.source.encoding, r.fixed_encoding?, r.source.b,
            r.encoding == Encoding::US_ASCII || r.source.ascii_only? ? r.inspect.b : "-", r.inspect.encoding,
            r.to_s.b, r.options]

def m(r, s)
  r =~ s
rescue Exception => e
  [e.class, e.message]
end

BYTES = ["\\\xFF", "\\\xA4\xA2", "a\\\xFF", "\\\xFFa", "\xFF", "\\\\\xFF", "[\\\xFF]", "\\\xFF\\\xFE",
         "\\\xFF\xFE", "(?#\\\xFF)a", "(?#\xFF)a", "(?#\xC3\xA9)a", "\\\xC3\xBF", "\\\xE3\x81\x82", "\\\xB1",
         "\\\x80", "\\x\xFF", "\\xg\xFF", "\\u\xFF", "\\c\xFF", "\\M-\xFF", "a/\\\xFF\t", "a # \xFF", "a # \xC3\xA9",
         "(?x)a # \xFF", "(?x:a # \xFF\n)\xFE", "(?x:a # \xFF\n)b", "[#\xFF]", "a # \\\xFF\n\xFE"]
ENCODINGS = ["BINARY", "UTF-8", "EUC-JP", "Shift_JIS", "ISO-8859-1", "Windows-1252", "KOI8-R", "US-ASCII"]
SUBJECTS = ["\xFF".b, "a\xFF".b, "\xA4\xA2".b, "\u00FF", "\xFF".dup.force_encoding("UTF-8"),
            "\xA4\xA2".dup.force_encoding("EUC-JP"), "\xFF".dup.force_encoding("ISO-8859-1"),
            "\x80".dup.force_encoding("Windows-1252"), "\u20AC", "\xFF".dup.force_encoding("KOI8-R"), "a"]

UNION_PARTS = ["\u00FF", "\xFE".b, "\xFE".dup.force_encoding("ISO-8859-1"), /\u00FF/, /\xfe/n,
               Regexp.new("\xFE".b), Regexp.new("a\\\xFE".b), "\u00E9".encode("UTF-16LE")]

BYTES.each do |bytes|
  ENCODINGS.each do |enc|
    src = bytes.dup.force_encoding(enc)
    [0, Regexp::NOENCODING, Regexp::FIXEDENCODING, Regexp::IGNORECASE, Regexp::EXTENDED].each do |opts|
      label = "#{bytes.b.inspect} #{enc} #{opts}"
      r = nil
      t(label) { r = Regexp.new(src, opts); d(r) }
      next unless r
      # A fresh regexp for each subject: MRI recompiles a regexp that is not fixed in the
      # encoding of the string it is matched against and keeps the recompiled one, so a
      # match can depend on the strings the same regexp object was matched against before.
      t("#{label} match") { SUBJECTS.map { |s| m(Regexp.new(src, opts), s) } }
      t("#{label} eql") { [r == Regexp.new(r.source, r.options), r.hash == Regexp.new(r.source, r.options).hash] }
      t("#{label} union") { u = Regexp.union(r, "a"); [u.encoding, u.fixed_encoding?] }
      t("#{label} unions") do
        UNION_PARTS.map do |x|
          [[r, x], [x, r]].map do |parts|
            u = Regexp.union(*parts)
            [u.encoding, u.fixed_encoding?]
          rescue => e
            [e.class, e.message]
          end
        end
      end
      t("#{label} interp") { u = /#{r}b/; [u.encoding, u.fixed_encoding?, u.source.b] }
      t("#{label} interp str") { u = /#{src}b/; [u.encoding, u.fixed_encoding?, u.source.b] }
      t("#{label} marshal") { u = Marshal.load(Marshal.dump(r)); [u.encoding, u.fixed_encoding?, u == r] }
    end
  end
end
