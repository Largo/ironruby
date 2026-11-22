# Differential matrix for String#inspect when the string is in the encoding the
# result is in (Encoding.default_internal, else default_external), for the ASCII
# compatible encodings without Unicode's tables.
#
#   ruby    Util/inspect-own-encoding-matrix.rb > /tmp/ins.mri
#   ./ir.sh Util/inspect-own-encoding-matrix.rb > /tmp/ins.ir
#   diff /tmp/ins.mri /tmp/ins.ir
#
# MRI's rb_str_inspect writes such a character as itself when rb_enc_isprint
# says it is printable: every valid character of more than one byte, and the
# single bytes above 0x7F Onigmo's ctype table for the encoding marks
# (Util/gen-inspect-printable.rb reads them off CRuby). The rest are escaped.

$VERBOSE = nil

def inspect_in(encoding, s, external: false)
  if external
    Encoding.default_external = encoding
  else
    Encoding.default_internal = encoding
  end
  i = s.inspect
  "#{i.encoding} #{i.b.unpack1('H*')}"
rescue Exception => e
  "#{e.class}: #{e.message}"
ensure
  Encoding.default_internal = nil
  Encoding.default_external = "UTF-8"
end

encodings = Encoding.list.reject(&:dummy?).select(&:ascii_compatible?)
encodings.reject! { |e| %w[ASCII-8BIT US-ASCII CESU-8].include?(e.name) || e.name.start_with?("UTF") }

encodings.sort_by(&:name).each do |e|
  printable = (0x80..0xff).select do |b|
    s = [b].pack("C").force_encoding(e)
    inspect_in(e, s).end_with?("22#{b.to_s(16)}22")
  end
  puts "#{e.name} printable: #{printable.map { _1.to_s(16) }.join(',')}"
  mixed = "a\"\#{x}\#$y\#@z#\\\n\t\e\x7f\x00".b + [0x80, 0xa1, 0xb0, 0xff].pack("C*")
  puts "#{e.name} mixed: #{inspect_in(e, mixed.force_encoding(e))}"
end

{
  "EUC-JP" => %w[a4a2 a1a1 8ea1 8fb0a1 adf4 f9a1 ffff a4 8e a1a2a3 8fb0],
  "Shift_JIS" => %w[82a0 8140 a1 df 815f eaa4 ff 82 81ad 8740 a0],
  "Windows-31J" => %w[82a0 8740 ed40 fa40 a1],
  "GBK" => %w[b0a1 a1a1 8140 80 ff a1],
  "GB18030" => %w[b0a1 81308130 a1a1 8130],
  "Big5" => %w[a440 a140 c6a1 f9fe ff],
  "EUC-KR" => %w[b0a1 a1a1 a4a1 ff],
  "CP949" => %w[8141 b0a1 80],
  "EUC-TW" => %w[c4a1 8ea2a1a1 8ea2],
  "Emacs-Mule" => %w[92a4a2 81a0 9cf0a0a0 92a4],
}.each do |name, list|
  list.each do |h|
    s = [h].pack("H*").force_encoding(name)
    puts "#{name} #{h}: #{inspect_in(name, s)} | external #{inspect_in(name, s, external: true)}"
  end
  s = ["61" + list[0] + "0a" + list[0] + "7f2322"].pack("H*").force_encoding(name)
  puts "#{name} mixed: #{inspect_in(name, s)}"
end

# a string in another encoding than the result's is not this case
puts "foreign: #{inspect_in('EUC-JP', "あ".encode('Shift_JIS'))}"
puts "foreign utf-8: #{inspect_in('EUC-JP', "あ")}"
puts "ascii only: #{inspect_in('EUC-JP', "a\x7f".dup.force_encoding('EUC-JP'))}"
