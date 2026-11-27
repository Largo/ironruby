# Readers of a String racing a writer on another thread (perf/strrace). There is no GVL here: a
# reader that reads a string's array, start and count while a writer moves them can answer what no
# state of the string ever held, or raise. Each writer below flips the string between a few states,
# each of them consistent (replace, an append and its removal, slice! and prepend, one byte set and
# reset, an insertion and its removal, the encoding forced back and forth, encode! there and back);
# a reader's answer must be one it gives on one of those states, computed beforehand on copies.
# CRuby, whose GVL makes each call atomic, prints "ok" in every row.
#
#   ruby   Util/string-race-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/string-race-matrix.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt
#
# A row is "<reader> / <writer>: ok", "wrong" (an answer of no state), "raises <Class>" or, when the
# writer itself failed (a reader that changes how the string is held can do that to it), "writer
# raises <Class>". A reader with a block (each_char, each_byte) lets the writer run between yields in
# CRuby too, so only its exceptions count. Run it
# many times (the races are rare): a single run proves little. READS sets the reads per row.
# -*- frozen_string_literal: false -*-

READS = (ENV["READS"] || 2000).to_i
N = 300

ASCII = ("abcdefghij" * N).freeze
MB = ("aé日" * (N / 2)).freeze

# writer name => [the string's states, a block that flips the string between them forever]
WRITERS = {
  "replace" => [[ASCII, "XYZ" * 50], ->(s, st) { s.replace(st[1]); s.replace(st[0]) }],
  "append" => [[ASCII, ASCII + "Q" * 40], ->(s, _) { s << "Q" * 40; s.slice!(-40, 40) }],
  "slice!_front" => [[ASCII, ASCII[3..]], ->(s, _) { s.slice!(0, 3); s.prepend("abc") }],
  "setbyte" => [[ASCII, ASCII.dup.tap { |x| x.setbyte(150, 90) }], ->(s, _) { s.setbyte(150, 90); s.setbyte(150, ASCII.getbyte(150)) }],
  "insert" => [[ASCII, ASCII.dup.insert(100, "XYZ")], ->(s, _) { s[100, 0] = "XYZ"; s[100, 3] = "" }],
  "force_encoding" => [[MB, MB.dup.force_encoding("BINARY")], ->(s, _) { s.force_encoding("BINARY"); s.force_encoding("UTF-8") }],
  "encode!" => [[MB, MB.encode("UTF-16LE")], ->(s, _) { s.encode!("UTF-16LE"); s.encode!("UTF-8") }],
}

READERS = {
  "index" => ->(s) { s.index("hij") },
  "index_at" => ->(s) { s.index("a", 200) },
  "rindex" => ->(s) { s.rindex("abc") },
  "index_mb" => ->(s) { s.index("日") },
  "include?" => ->(s) { s.include?("jab") },
  "start_with?" => ->(s) { s.start_with?("abcdefghijab") },
  "end_with?" => ->(s) { s.end_with?("ghij") },
  "==" => ->(s) { s == ASCII },
  "eql?" => ->(s) { s.eql?(MB) },
  "<=>" => ->(s) { s <=> ASCII },
  "hash" => ->(s) { s.hash },
  "size" => ->(s) { s.size },
  "bytesize" => ->(s) { s.bytesize },
  "chars" => ->(s) { s.chars },
  "each_char" => ->(s) { n = 0; s.each_char { |c| n += 1 if c.nil? }; n },
  "bytes" => ->(s) { s.bytes },
  "each_byte" => ->(s) { n = 0; s.each_byte { |b| n += 1 unless b.is_a?(Integer) && b.between?(0, 255) }; n },
  "getbyte" => ->(s) { s.getbyte(150) },
  "byteslice" => ->(s) { s.byteslice(140, 20) },
  "[]_char" => ->(s) { s[150] },
  "[]_range" => ->(s) { s[140, 20] },
  "count" => ->(s) { s.count("a") },
  "sum" => ->(s) { s.sum },
  "to_sym" => ->(s) { s.to_sym },
  "dup" => ->(s) { s.dup },
  "+" => ->(s) { s + "!" },
  "upcase" => ->(s) { s.upcase },
  "reverse" => ->(s) { s.reverse },
  "split" => ->(s) { s.split("j") },
  "=~" => ->(s) { s =~ /hij/ },
  "scan" => ->(s) { s.scan(/a./).size },
  "unpack1" => ->(s) { s.unpack1("C*") },
  "valid_encoding?" => ->(s) { s.valid_encoding? },
  "ascii_only?" => ->(s) { s.ascii_only? },
  "inspect" => ->(s) { s.inspect },
  "encoding" => ->(s) { s.encoding },
}

def answer(reader, s)
  [:value, reader.(s)]
rescue => e
  [:raise, e.class]
end

# comparing a torn answer can raise too (a String of a broken encoding): that is a wrong answer
def allowed?(allowed, a)
  allowed.include?(a)
rescue
  false
end

Thread.report_on_exception = false
ONLY = ENV["ONLY"]
READERS.each do |rname, reader|
  next if ONLY && !rname.match?(ONLY)
  WRITERS.each do |wname, (states, flip)|
    allowed = states.map { |st| answer(reader, st.dup) }
    s = states[0].dup
    stop = false
    writer = Thread.new do
      flip.(s, states) until stop
      nil
    rescue => e
      e.class
    end
    wrong = 0
    raised = []
    READS.times do
      a = answer(reader, s)
      if a[0] == :raise && !allowed?(allowed, a)
        raised << a[1]
      elsif !allowed?(allowed, a)
        wrong += 1
      end
    end
    stop = true
    failed = writer.value
    result = if failed then "writer raises #{failed}"
             elsif !raised.empty? then "raises #{raised.uniq.map(&:to_s).sort.join(', ')}"
             elsif wrong > 0 then "wrong"
             else "ok" end
    puts "#{rname} / #{wname}: #{result}"
  end
end
puts "done"
