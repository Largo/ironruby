# Differential matrix for the line readers (and universal newlines) of a stream that converts from a wide
# encoding: "rb:utf-16le:utf-8", "r:utf-32le:utf-8", universal_newline: true. Run it under CRuby and under
# ./ir.sh and diff:
#
#   ruby Util/wide-lines-matrix.rb > mri.txt
#   ./ir.sh Util/wide-lines-matrix.rb > ir.txt
#   diff mri.txt ir.txt
#
# The byte of a "\n" does not end a line of UTF-16: it is half of the character, or part of another one
# (U+010A, U+0A00). MRI looks for the separator, counts the limit and folds the newlines in the text it has
# converted, and so do the line readers here (ReadLineFromPendingChars in IoOps.cs): a character at a time,
# through the conversion.
#
# Left out: the encodings that read a byte order mark (UTF-16, UTF-32 with no LE or BE), and separators,
# limits and chomp: when the internal encoding is a wide one too.

require 'tempfile'
def t(label, data, mode, **opts)
  f = Tempfile.new('u'); f.binmode; f.write(data); f.close
  File.open(f.path, mode, **opts) do |io|
    r = begin; yield io; rescue => e; [e.class, e.message]; end
    puts "#{label}: #{r.inspect}"
  end
ensure
  f.unlink
end
def enc(x) = x.is_a?(String) ? [x.b, x.encoding.to_s] : x.is_a?(Array) ? x.map { enc(_1) } : x
T = "ab\ncd\r\n\r\n\n\u00e9f\rg\u3042\u0a00\u010a\n\n\nh"
[["utf-16le", "utf-8"], ["utf-16be", "utf-8"], ["utf-32le", "utf-8"], ["utf-16le", "iso-8859-1"], ["utf-16le", "utf-32be"]].each do |ext, int|
  text = int == "iso-8859-1" ? T.delete("\u3042\u0a00\u010a") : T
  data = text.encode(ext).b
  [["rb:#{ext}:#{int}", {}], ["r:#{ext}:#{int}", {}], ["r:#{ext}:#{int}", {universal_newline: true}]].each do |mode, o|
    l = "#{mode} #{o.keys.join}"
    {
      "gets"         => ->(io) { [io.gets, io.gets, io.gets, io.lineno] },
      "gets chomp:"  => ->(io) { Array.new(4) { io.gets(chomp: true) } },
      "gets(nil)"    => ->(io) { io.gets(nil) },
      "gets('')"     => ->(io) { [io.gets(""), io.gets(""), io.gets("")] },
      "gets(3)"      => ->(io) { Array.new(5) { io.gets(3) } },
      "readlines"    => ->(io) { io.readlines },
      "readlines chomp:" => ->(io) { io.readlines(chomp: true) },
      "each_line"    => ->(io) { io.each_line.to_a },
      "read"         => ->(io) { io.read },
      "each_char"    => ->(io) { io.each_char.to_a.join },
      "getc pos"     => ->(io) { [io.getc, io.getc, io.getc, io.pos] },
      "gets, read"   => ->(io) { [io.gets, io.read] },
      "gets, getc, gets" => ->(io) { [io.gets, io.getc, io.gets, io.lineno] },
      "gets, eof?"   => ->(io) { io.gets; [io.eof?, io.readlines.size, io.eof?, io.gets] },
      "gets, pos"    => ->(io) { io.gets; io.pos },
      "gets, rewind" => ->(io) { io.gets; io.rewind; [io.gets, io.lineno] },
    }.each do |an, a|
      next if int == "utf-32be" && an =~ /gets\(|chomp|sep/   # separators in a wide internal encoding: left out
      t("#{l} #{an}", data, mode, **o) { |io| enc(a[io]) }
    end
  end
end
