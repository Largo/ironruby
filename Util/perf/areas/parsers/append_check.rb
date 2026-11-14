# String#<< Integer and StringScanner int? results: values, encodings and errors, to diff against CRuby.
out = []
[Encoding::BINARY, Encoding::UTF_8, Encoding::US_ASCII, Encoding::EUC_JP, Encoding::UTF_16LE, Encoding::Shift_JIS].each do |e|
  [0, 0x41, 0x7f, 0x80, 0xff, 0x100, 0x3042, -1, 2**40].each do |c|
    s = "ab".encode(e) rescue "ab".dup.force_encoding(e)
    begin
      s << c
      out << [e, c, s.encoding, s.bytes, s.valid_encoding?, s.ascii_only?].inspect
    rescue => ex
      out << [e, c, ex.class, ex.message].inspect
    end
  end
end
s = "héllo".dup; s << 0x41; out << [s, s.ascii_only?, s.length].inspect
s = "abc".dup; s.ascii_only?; s << 0x41; out << [s, s.ascii_only?].inspect
s = "abc".dup; s.ascii_only?; s << 0xe9; out << [s, s.ascii_only?, s.length].inspect
s = "x".b; s.ascii_only?; s << 0xe9; s << 0x41; out << [s, s.ascii_only?, s.length, s.bytesize].inspect
begin; "a".freeze << 0x41; rescue => ex; out << ex.class.inspect; end
begin; "a".b.freeze << 0x41; rescue => ex; out << ex.class.inspect; end
require "strscan"
ss = StringScanner.new("abc  def" * 300)
r = []; until ss.eos?; r << ss.skip(/\w+/) << ss.skip(/\s+/) << ss.match?(/x/); end
out << r.inspect << r.map(&:class).uniq.inspect
out << "abc".index("c").inspect << "abc".index("z").inspect << ss.skip(/z/).inspect
puts out
