# Differential matrix for IO#ungetc on a stream that converts what it reads: one with an internal encoding
# ("r:utf-16le:utf-8") or in text mode ("rt", universal_newline: true, newline: ...). Run it under CRuby and
# under ./ir.sh and diff:
#
#   ruby Util/ungetc-matrix.rb > mri.txt
#   ./ir.sh Util/ungetc-matrix.rb > ir.txt
#   diff mri.txt ir.txt
#
# On such a stream MRI's rb_io_ungetc puts the text in fptr->cbuf, the buffer of characters already converted,
# and not with the bytes of fptr->rbuf: the readers of characters (getc, readchar, each_char, gets and the other
# line readers, read with no length) hand it back as it is, without converting it again, and the readers of
# bytes (getbyte, read(n), readpartial, sysread, each_byte, ungetbyte, copy_stream...) raise IOError while it
# holds something. io_unread (before a seek, #pos, a write or a flush) drops it when it has read-ahead to give
# back, #rewind, #binmode and #set_encoding always do, and it does not grow past its first size (128K at least).
#
# Left out, because the two differ there for reasons that are not #ungetc's:
#   - a byte read after #ungetc and #rewind/#binmode raises in MRI (clear_readconv leaves cbuf.len as it was);
#   - #size, #stat, #truncate and #fsync after a read and an #ungetc (which of them MRI flushes through);
#   - #ungetbyte and #pos around a lone "\r" a universal newline stream has looked past;
#   - #dup after a read and an #ungetc (the two share an offset, and MRI's has read ahead);
#   - line readers and universal newlines on a stream whose external encoding is a wide one (UTF-16, UTF-32).

require 'tempfile'

def t(label, data, mode = "rt", **opts)
  f = Tempfile.new('u'); f.binmode; f.write(data); f.close
  File.open(f.path, mode, **opts) do |io|
    r = begin; yield io; rescue => e; [e.class, e.message]; end
    puts "#{label}: #{r.inspect}"
  end
ensure
  f.unlink
end

def enc(x) = x.is_a?(String) ? [x, x.encoding.to_s] : x

# --- text mode: what is pushed back comes out as it went in, newlines and all -------------------------------

[["a\nb\n", 1], ["\nb\n", 0], ["a\r\nb", 1], ["\r\nb", 0], ["ab", 1], ["", 0], ["a\rb", 0]].each do |data, skip|
  ["\r", "\r\n", "x\r", "\n", "\r\r"].each do |push|
    l = "#{data.inspect} skip#{skip} push#{push.inspect}"
    go = ->(io) { skip.times { io.getc }; io.ungetc(push) }
    t("#{l} getc", data) { |io| go[io]; [io.getc, io.getc, io.getc] }
    t("#{l} gets", data) { |io| go[io]; [io.gets, io.gets, io.lineno] }
    t("#{l} read", data) { |io| go[io]; io.read }
    t("#{l} read(n)", data) { |io| go[io]; io.read(1) }
    t("#{l} getbyte", data) { |io| go[io]; io.getbyte }
    t("#{l} readchar eof?", data) { |io| go[io]; [io.eof?, io.readchar, io.eof?, io.read] }
    t("#{l} each_char", data) { |io| go[io]; io.each_char.to_a }
    t("#{l} readlines", data) { |io| go[io]; io.readlines }
    t("#{l} readpartial", data) { |io| go[io]; io.readpartial(10) }
    t("#{l} gets(nil)", data) { |io| go[io]; io.gets(nil) }
    t("#{l} gets('')", data) { |io| go[io]; io.gets("") }
    t("#{l} gets(limit)", data) { |io| go[io]; [io.gets(1), io.gets(2), io.gets("b")] }
    t("#{l} universal_newline:", data, "r", universal_newline: true) { |io| go[io]; [io.getc, io.read] }
  end
end

D = "a\nb\r\nc\rd"
{
  "ungetc"      => ->(io) { io.ungetc("\r") },
  "getc+ungetc" => ->(io) { io.getc; io.ungetc("X\r") },
}.each do |on, op|
  {
    "readbyte"      => ->(io) { io.readbyte },
    "read(0)"       => ->(io) { io.read(0) },
    "read(nil)"     => ->(io) { io.read(nil) },
    "read(nil, s)"  => ->(io) { b = +"zz"; io.read(nil, b); b },
    "readpartial(0)" => ->(io) { io.readpartial(0) },
    "read_nonblock" => ->(io) { io.read_nonblock(3) },
    "read_nonblock exception: false" => ->(io) { io.read_nonblock(3, exception: false) },
    "sysread"       => ->(io) { io.sysread(3) },
    "sysread(0)"    => ->(io) { io.sysread(0) },
    "pread"         => ->(io) { io.pread(3, 0) },
    "each_byte"     => ->(io) { io.each_byte.first(2) },
    "gets chomp:"   => ->(io) { [io.gets(chomp: true), io.gets(chomp: true)] },
    "each_line"     => ->(io) { io.each_line.to_a },
    "each_line(2)"  => ->(io) { io.each_line(2).first(4) },
    "readlines(1)"  => ->(io) { io.readlines(1).first(4) },
    "readline(sep)" => ->(io) { [io.readline("b"), io.readline(nil)] },
    "gets('\\r')"   => ->(io) { [io.gets("\r"), io.gets("\r")] },
    "each_codepoint" => ->(io) { io.each_codepoint.to_a },
    "lineno"        => ->(io) { [io.gets, io.lineno, io.gets, io.lineno] },
    "pos"           => ->(io) { [io.pos, io.getc] },
    "ungetbyte"     => ->(io) { io.ungetbyte("Q"); [io.getc, io.getc, io.getc] },
    "ungetc again"  => ->(io) { io.ungetc("Z"); [io.getc, io.getc, io.getc] },
    "binmode"       => ->(io) { io.binmode; [io.getc, io.getc, io.read] },
    "sysseek"       => ->(io) { io.sysseek(0) },
    "copy_stream"   => ->(io) { r, w = IO.pipe; IO.copy_stream(io, w); w.close; r.read },
    "select"        => ->(io) { IO.select([io], nil, nil, 0)&.first&.size },
    "close"         => ->(io) { io.close; (io.getc rescue $!.message) },
    "reopen"        => ->(io) { io.reopen(io.path, "rt"); [io.getc, io.getc] },
    "drained"       => ->(io) { 3.times { io.getc }; io.getbyte.class },
  }.each do |an, a|
    t("rt #{on} #{an}", D) { |io| op[io]; a[io] }
  end
end

# --- what drops it -------------------------------------------------------------------------------------------

g = ->(io) { [io.getc, io.getc] }
%w[rt r+t r:utf-16le:utf-8 r+:utf-16le:utf-8].each do |m|
  d = m.include?("16") ? "abcdef".encode("utf-16le").b : "abcdef"
  t("#{m} rewind", d, m) { |io| io.ungetc("Z"); io.rewind; g[io] }
  t("#{m} getc, rewind", d, m) { |io| io.getc; io.ungetc("Z"); io.rewind; g[io] }
  t("#{m} read, rewind", d, m) { |io| io.read; io.ungetc("Z"); io.rewind; g[io] }
  t("#{m} seek", d, m) { |io| io.ungetc("Z"); io.seek(0); g[io] }
  t("#{m} getc, seek", d, m) { |io| io.getc; io.ungetc("Z"); io.seek(0); g[io] }
  t("#{m} getc, seek cur", d, m) { |io| io.getc; io.ungetc("Z"); io.seek(0, IO::SEEK_CUR); g[io] }
  t("#{m} read, seek", d, m) { |io| io.read; io.ungetc("Z"); io.seek(0); g[io] }
  t("#{m} pos", d, m) { |io| io.ungetc("Z"); [io.pos, g[io]] }
  t("#{m} getc, pos", d, m) { |io| io.getc; io.ungetc("Z"); [io.pos, g[io]] }
  t("#{m} read, pos", d, m) { |io| io.read; io.ungetc("Z"); [io.pos, g[io]] }
  t("#{m} getc, pos=", d, m) { |io| io.getc; io.ungetc("Z"); io.pos = 2; g[io] }
  t("#{m} getc, flush", d, m) { |io| io.getc; io.ungetc("Z"); io.flush; g[io] }
  t("#{m} getc, eof?", d, m) { |io| io.getc; io.ungetc("Z"); [io.eof?, g[io]] }
  t("#{m} getc, lineno=", d, m) { |io| io.getc; io.ungetc("Z"); io.lineno = 3; g[io] }
  t("#{m} getc, sync=", d, m) { |io| io.getc; io.ungetc("Z"); io.sync = true; g[io] }
  t("#{m} getc, sysseek", d, m) { |io| io.getc; io.ungetc("Z"); (io.sysseek(0) rescue $!.message) }
  next unless m.include?("+")
  t("#{m} write", d, m) { |io| io.ungetc("Z"); io.write("W"); g[io] }
  t("#{m} write, rewind", d, m) { |io| io.ungetc("Z"); io.write("W"); io.rewind; g[io] }
  t("#{m} getc, write", d, m) { |io| io.getc; io.ungetc("Z"); io.write("W"); [g[io], io.pos] }
  t("#{m} getc, print", d, m) { |io| io.getc; io.ungetc("Z"); io.print("W"); g[io] }
  t("#{m} getc, syswrite", d, m) { |io| io.getc; io.ungetc("Z"); [(io.syswrite("W") rescue $!.class), io.getc] }
end

# what MRI has read ahead: a read into the caller's buffer leaves nothing, and a write gives it back
r = ->(label, &b) { t("r+t #{label}", "abcdefgh", "r+t", &b) }
r.("read(1), pos") { |io| io.read(1); io.ungetc("X"); [io.pos, io.getc] }
r.("readpartial, pos") { |io| io.readpartial(1); io.ungetc("X"); [io.pos, io.getc] }
r.("getbyte, seek cur") { |io| io.getbyte; io.ungetc("X"); io.seek(0, IO::SEEK_CUR); io.getc }
r.("gets(2), pos") { |io| io.gets(2); io.ungetc("X"); [io.pos, io.getc] }
r.("getc, write, ungetc, pos") { |io| io.getc; io.write("W"); io.ungetc("X"); [io.pos, io.getc] }
r.("getc, print, ungetc, pos") { |io| io.getc; io.print("W"); io.ungetc("X"); [io.pos, io.getc] }
r.("set_encoding(Encoding)") { |io| io.ungetc("X"); io.set_encoding(Encoding::UTF_8); io.getc }
r.("set_encoding(Encoding, Encoding)") { |io| io.ungetc("X"); io.set_encoding(Encoding::UTF_8, Encoding::UTF_16LE); io.getc.b }
r.("set_encoding(String)") { |io| io.ungetc("X"); io.set_encoding("utf-8"); io.getc }
r.("read(nil, frozen)") { |io| io.ungetc("X"); [(io.read(nil, "".freeze) rescue $!.class), io.getc] }
r.("read(nil, 1)") { |io| io.ungetc("X"); [(io.read(nil, 1) rescue $!.class), io.getc] }

# --- an internal encoding: the text is in it, a codepoint is one of its characters ---------------------------

U = "é\nü\r\nz".encode("utf-16le").b
[["r:utf-16le:utf-8", {}], ["rb:utf-16le:utf-8", {}], ["r:utf-16le:iso-8859-1", {}]].each do |mode, o|
  {
    "ungetc(str)"   => ->(io) { io.ungetc("ß") },
    "ungetc(0xdf)"  => ->(io) { io.ungetc(0xdf) },
    "ungetc(65)"    => ->(io) { io.ungetc(65) },
    "ungetc(\\xff)" => ->(io) { io.ungetc("\xff".b) },
    "ungetc(getc)"  => ->(io) { c = io.getc; io.ungetc(c) },
  }.each do |on, op|
    {
      "getc"      => ->(io) { [enc(io.getc), enc(io.getc), io.pos] },
      "read"      => ->(io) { enc(io.read) },
      "getbyte"   => ->(io) { io.getbyte },
      "each_char" => ->(io) { io.each_char.map { enc(_1) } },
      "eof?"      => ->(io) { io.eof? },
      "read(2)"   => ->(io) { io.read(2) },
    }.each do |an, a|
      t("#{mode} #{on} #{an}", U, mode, **o) { |io| op[io]; a[io] }
    end
  end
end
t("r:utf-8:utf-16le gets", "a\nb", "r:utf-8:utf-16le") { |io| io.ungetc("z\n".encode("utf-16le")); [io.gets, io.gets, io.gets].map { _1&.b } }
t("r:utf-8:utf-16le each_char", "a", "r:utf-8:utf-16le") { |io| io.ungetc("é".encode("utf-16le")); io.each_char.map { enc(_1) } }
t("r:utf-8:utf-16le half a unit", "a", "r:utf-8:utf-16le") { |io| io.ungetc("x"); io.each_char.map { [_1.b, _1.encoding.to_s] } }
t("r:utf-8:utf-16le ungetc(65)", "a", "r:utf-8:utf-16le") { |io| io.ungetc(65); io.each_char.map { [_1.b, _1.encoding.to_s] } }

# --- a stream that converts nothing: bytes like the rest -----------------------------------------------------

t("r", D, "r") { |io| io.ungetc("\r"); [io.getbyte, io.getc, io.read(1), io.gets] }
t("r:utf-8", D, "r:utf-8") { |io| io.ungetc("é"); [io.getbyte, io.getc, io.read] }
t("r invalid: :replace", D, "r", invalid: :replace) { |io| io.ungetc("\r"); [io.getbyte, io.getc, io.read] }
t("r universal_newline: false", D, "r", universal_newline: false) { |io| io.ungetc("\r"); [io.getbyte, io.getc, io.read] }
t("r newline: :lf", D, "r", newline: :lf) { |io| io.ungetc("\r"); [io.getbyte, io.getc, io.read] }
t("r crlf_newline: true", D, "r", crlf_newline: true) { |io| io.ungetc("\r"); [io.getbyte, io.getc, io.read] }

# --- characters, limits and paragraphs across the buffer and the stream --------------------------------------

t("split character", D, "rt:utf-8") { |io| io.ungetc("é"); [io.getc, io.getc] }
t("half a character, then not its rest", D, "rt:utf-8") { |io| io.ungetc("\xc3"); [io.getc, io.getc] }
t("half a character, then its rest", "\xa9b", "rt:utf-8") { |io| io.ungetc("\xc3"); [io.getc, io.getc] }
t("half a character at the end", "", "rt:utf-8") { |io| io.ungetc("\xe3\x81"); [io.getc, io.getc, io.getc] }
t("binary", "a", "rt:binary") { |io| io.ungetc("é"); io.each_char.map { enc(_1) } }
t("limit inside a character", "é", "rt:utf-8") { |io| io.ungetc("éé"); [io.gets(1), io.gets(3), io.gets] }
t("limits", D) { |io| io.ungetc("\r\n"); [io.gets(1), io.gets(1), io.gets("")] }
t("paragraph", "\n\na\n\n\nb") { |io| io.ungetc("\r\r"); [io.gets(""), io.gets("")] }
t("paragraph across", "a\n\n\nb") { |io| io.getc; io.ungetc("\n\n\r\n"); [io.gets(""), io.gets("")] }
t("gets chomp: \\r\\n", D) { |io| io.ungetc("x\r\n"); [io.gets(chomp: true), io.gets(chomp: true)] }
t("gets chomp: \\r", D) { |io| io.ungetc("x\r"); [io.gets(chomp: true), io.gets(chomp: true)] }
t("at the end", D) { |io| io.read; io.ungetc("q"); [io.eof?, io.getc, io.eof?, io.getc] }
t("readchar at the end", D) { |io| io.read; io.ungetc("z"); [io.readchar, (io.readchar rescue $!.class)] }
t("each_char break", D) { |io| io.ungetc("\rQ"); io.each_char { break }; (io.getbyte rescue $!.message) }

# --- arguments -----------------------------------------------------------------------------------------------

t("ungetc('')", D) { |io| io.ungetc(""); io.getbyte }
t("ungetc(nil)", D) { |io| io.ungetc(nil); io.getbyte }
t("ungetc(13)", D) { |io| io.ungetc(13); [io.getc, io.getc] }
t("ungetc(0x3042) us-ascii", D, "rt:us-ascii") { |io| io.ungetc(0x3042); io.getc }
t("ungetc(0xe9) us-ascii", "a", "rt:us-ascii") { |io| [(io.ungetc(0xe9) rescue $!.class), io.getc] }
t("ungetc(0x3042) utf-8", D, "rt:utf-8") { |io| io.ungetc(0x3042); [io.getc, io.getc] }
t("ungetc(to_str)", D) { |io| o = Object.new; def o.to_str = "\r"; io.ungetc(o); io.getc }
t("ungetc write-only", "x", "w") { |io| io.ungetc("q") }

# --- the size of the buffer ----------------------------------------------------------------------------------

t("100000", D) { |io| io.ungetc("x" * 100000); io.read.size }
t("5000 + 5000", D) { |io| io.ungetc("x" * 5000); io.ungetc("y" * 5000); io.read.size }
t("1 + 200000", D) { |io| io.ungetc("x"); (io.ungetc("y" * 200_000) rescue $!) }
t("131072 + 1", D) { |io| io.ungetc("x" * 131072); [(io.ungetc("y") rescue $!), io.read.size] }
t("131071 + 1 + 1", D) { |io| io.ungetc("x" * 131071); [(io.ungetc("y") rescue $!), (io.ungetc("y") rescue $!)] }
t("300000 first", D) { |io| io.ungetc("x" * 300_000); [(io.ungetc("y") rescue $!), io.getc, (io.ungetc("yy") rescue $!), (io.ungetc("y") rescue $!)] }
t("300000, read, 300000", D) { |io| io.ungetc("x" * 300_000); io.read; [(io.ungetc("y" * 300_000) rescue $!)] }
