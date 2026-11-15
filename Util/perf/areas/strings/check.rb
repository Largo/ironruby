# Answers for the shapes the strings round changed; diff the output against CRuby's.
strs = ["", "abc", "Hello Ünïcödé", "a\u{1F600}b", "x\r\ny\n\nz", "line\n" * 3, "a,b,,c,", "\xff\xfe".b, "日本語"]
%w[UTF-8 UTF-16LE UTF-16BE UTF-16 UTF-32 ISO-8859-1 US-ASCII Windows-1252 Shift_JIS EUC-JP BINARY].each do |enc|
  strs.each do |s|
    begin
      r = s.encode(enc)
      puts "#{enc} #{s.inspect} => #{r.bytes.inspect} #{r.encoding}"
    rescue => e
      puts "#{enc} #{s.inspect} => #{e.class}: #{e.message}"
    end
    r = (s.encode(enc, undef: :replace, invalid: :replace) rescue $!.class)
    puts "  replace => #{r.is_a?(String) ? r.bytes.inspect : r}"
    r = (s.encode(enc, xml: :text) rescue $!.class)
    puts "  xml => #{r.is_a?(String) ? r.bytes.inspect : r}"
  end
end
puts "a<b&c>".encode("UTF-16LE", xml: :attr).bytes.inspect
puts "x\ny".encode("UTF-16LE", crlf_newline: true).bytes.inspect
puts "é".b.encode("UTF-8").inspect rescue puts $!.class
puts "é".encode("BINARY").inspect rescue puts $!.class
strs.each do |s|
  next unless s.valid_encoding?
  p [s.lines, s.lines("\n"), s.lines(","), s.lines(""), s.lines(chomp: true), s.lines(nil), s.each_line.to_a, s.lines("ab")]
end
class MyS < String; end
p MyS.new("a\nb").lines.map(&:class)
p "a\nb".force_encoding("UTF-16LE").lines rescue p $!.class
p ["abc".match?(/b/), "abc".match?(/b/, 2), "abc".match?("b"), "abc".match?("b", 1), "abc".match?(/\Ab/, 1), "a\u{1F600}b".match?(/b/, 2)]
[nil, 1, :a, Object.new.tap { |o| def o.inspect = "o" }].each { |x| p(("a".match?(x) rescue [$!.class, $!.message.sub(/Object.*/, 'Object')])) }
$~ = nil; "abc".match?(/b/); p $~
p(("abc".match?(/b/, "x") rescue $!.class))
["x\r", "a\r\nb\r\r\nc\n\r", "a\n\nb", "\r\n", "\n", "a\nb\n", "é\r\nü"].each { |s| p [s.lines(chomp: true), s.each_line(chomp: true).to_a, s.lines("\n", chomp: true)] }
p MyS.new("a\nb\n").lines(chomp: true).map(&:class)
p "a\r\nb".encode("UTF-16LE").lines(chomp: true).map { |l| l.encode("UTF-8") }
