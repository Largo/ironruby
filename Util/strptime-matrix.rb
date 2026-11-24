# Differential matrix for Date._strptime and Date._parse (Src/StdLib/ironruby/date/format.rb) and
# what is built on them: Date/DateTime/Time.strptime, Date/Time.parse. One row per directive of
# strptime, widths, literals, mismatches (nil), leftovers, and the hashes _parse returns, each run
# cold and hot (the 200th call). Run under CRuby and IronRuby, JIT on and off, and diff:
#
#   ruby Util/strptime-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/strptime-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/strptime-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# All must end with the row `end' and be identical.

require 'date'
require 'time'
$stdout.sync = true

def row(label)
  first = last = nil
  200.times do |i|
    v = begin
      yield
    rescue => e
      [e.class, e.message]
    end
    first = v if i == 0
    last = v
  end
  puts "#{label}: #{first.inspect}#{first.inspect == last.inspect ? '' : " HOT #{last.inspect}"}"
end

def sp(str, fmt) = row("_strptime #{str.inspect} #{fmt.inspect}") { Date._strptime(str, fmt) }

[["2026", "%Y"], ["-2026", "%Y"], ["+12345", "%Y"], ["20261", "%3Y"], ["20", "%C"], ["2026", "%C%y"],
 ["26", "%y"], ["70", "%y"], ["03", "%m"], ["13", "%m"], ["04", "%d"], [" 4", "%e"], ["32", "%d"],
 ["123", "%j"], ["367", "%j"], ["05", "%H"], [" 5", "%k"], ["25", "%H"], ["11", "%I"], [" 9", "%l"],
 ["06", "%M"], ["60", "%M"], ["07", "%S"], ["60", "%S"], ["123", "%L"], ["123456789", "%N"],
 ["12", "%S%L"], ["5 PM", "%I %p"], ["5 am", "%I %P"], ["5 p.m.", "%I %p"], ["Monday", "%A"], ["mon", "%a"],
 ["Mond", "%a"], ["March", "%B"], ["mar", "%b"], ["Sep", "%h"], ["xyz", "%b"], ["10", "%U"], ["10", "%W"],
 ["3", "%u"], ["0", "%w"], ["8", "%w"], ["2026", "%G"], ["26", "%g"], ["10", "%V"], ["UTC", "%Z"],
 ["EST", "%Z"], ["+0900", "%z"], ["-05:30", "%:z"], ["+09:00:00", "%::z"], ["Pacific Standard Time", "%Z"],
 ["GMT+9", "%Z"], ["1700000000", "%s"], ["-5", "%s"], ["1700000000123", "%Q"], ["a\nb", "a%nb"], ["a\tb", "a%tb"],
 ["%", "%%"], ["Sat Aug 28 02:55:50 2021", "%c"], ["08/28/21", "%x"], ["02:55:50", "%X"], ["08/28/21", "%D"],
 ["2021-08-28", "%F"], ["02:55:50", "%T"], ["02:55", "%R"], ["02:55:50 PM", "%r"], ["28-Aug-2021", "%v"],
 ["Sat Aug 28 02:55:50 UTC 2021", "%+"], ["2026-03-04 05:06:07 UTC", "%Y-%m-%d %H:%M:%S %Z"],
 ["2026-03-04T05:06:07+0900", "%Y-%m-%dT%H:%M:%S%z"], ["2026-03-04", "%Y/%m/%d"], ["2026-03-04 extra", "%Y-%m-%d"],
 ["2026-03-04", "%Y-%m-%d %H"], ["  2026", " %Y"], ["2026", "   %Y"], ["x2026", "x%Y"], ["y2026", "x%Y"],
 ["a.b*c", "a.b*c"], ["2026", "%EY"], ["04", "%Od"], ["5", "%Q"], ["2026 ", "%Y"], ["", "%Y"], ["", ""],
 ["abc", ""], ["2026", "%"], ["2026", "%Y%"], ["2026", "%q"], ["%q", "%q"]].each { |s, f| sp(s, f) }

# odd rows for the C# walk over ASCII-only strings (Src/Libraries/Date/DateStrptime.cs) and the
# cases it leaves to format.rb (another encoding, a number past a long)
[["EST5", "%Z"], ["EST DST", "%Z"], ["est  dst5", "%Z"], ["est dstx", "%Z"], ["Central Daylight Time", "%Z"],
 ["a.b standard  time!", "%Z"], ["x standard timex", "%Z"], ["utc+09:30:15", "%Z"], ["UT-3", "%Z"], ["gmt", "%Z"],
 ["gmtx", "%Z"], ["Z", "%z"], ["xyz", "%Z"], ["+5.5", "%z"], ["+09,30", "%z"], ["+", "%z"], ["UTC_", "%Z"],
 ["9", "%Ez"], ["x", "%E"], ["x", "%O"], [":", "%:"], ["+09:00:00", "%:::z"], ["+0900", "%::::z"],
 ["20260304", "%Y%m%d"], ["2026030405", "%Y%m%d%H"], ["1" * 30, "%Y"], ["-" + "9" * 20, "%s"], ["9" * 25, "%Q"],
 ["12345678901234567", "%Y"], ["-5", "%L"], ["+5", "%N"], ["123456789012", "%N"], ["123456789012", "%N "],
 ["-", "%s"], ["+", "%Y"], ["5 pmx", "%I %p"], ["5 p.m", "%I %p"], ["5 PM", "%H %p"], ["12 am", "%I %p"],
 ["12 pm 3 am", "%I %p %I %p"], ["1926", "%C%y"], ["2619", "%y%C"], ["99 5", "%g %y"], ["a\vb", "a b"],
 ["a \t\n b", "a b"], ["2026-03", "%Y-%m-%d"], ["Wednesday", "%a"], ["wedn", "%A"], ["Septem", "%B"],
 ["2026 xx", "%Y"], ["2026", "%Y %"], ["2026 %", "%Y %%"], ["10 5", "%U %W"], ["2026", "%EC%Ey"],
 ["3 7", "%Ou %OV"], ["2026é", "%Y"], ["é2026", "é%Y"], ["2026", "%Y é"]].each { |s, f| sp(s, f) }
row("_strptime binary leftover") { h = Date._strptime("2026 xx".b, "%Y"); [h, h[:leftover].encoding] }
row("_strptime binary zone") { h = Date._strptime("UTC".b, "%Z"); [h, h[:zone].encoding] }
row("_strptime us-ascii zone") { h = Date._strptime("UTC".encode("US-ASCII"), "%Z"); [h, h[:zone].encoding] }
row("_strptime utf-16") { Date._strptime("2026".encode("UTF-16LE"), "%Y") }
StrptimeSub = Class.new(String)   # one class for all the runs: the row compares their inspect
row("_strptime subclass") { h = Date._strptime(StrptimeSub.new("2026 x"), "%Y"); [h, h[:leftover].class] }
row("_strptime frozen") { Date._strptime("2026".freeze, "%Y".freeze) }
row("_strptime int") { Date._strptime(2026, "%Y") }
row("_strptime nil fmt") { Date._strptime("2026", nil) }
row("_strptime fraction") { Date._strptime("1.5", "%s.%N")[:sec_fraction].class }
row("Time.strptime L") { Time.strptime("2026-03-04 05:06:07.5 +0900", "%Y-%m-%d %H:%M:%S.%L %z").inspect }
row("Time.strptime Q") { Time.strptime("1700000000123", "%Q").utc.inspect }
row("Time.strptime zone name") { Time.strptime("2026-03-04 05:06:07 PST", "%Y-%m-%d %H:%M:%S %Z").utc.inspect }
row("Time.strptime bad zone") { Time.strptime("2026-03-04 05:06:07 xyz", "%Y-%m-%d %H:%M:%S %Z").utc.inspect }
row("Time.strptime bad mday") { Time.strptime("2026-02-31", "%Y-%m-%d").inspect }
row("Time.strptime huge year") { Time.strptime("123456789012345678901234", "%Y").year }

row("_strptime default") { Date._strptime("2026-03-04") }
row("DateTime._strptime default") { DateTime._strptime("2026-03-04T05:06:07+09:00") }
row("Date.strptime") { Date.strptime("2026-03-04", "%Y-%m-%d").to_s }
row("Date.strptime bad") { Date.strptime("2026-13-04", "%Y-%m-%d") }
row("Date.strptime nomatch") { Date.strptime("x", "%Y") }
row("DateTime.strptime") { DateTime.strptime("2026-03-04 05:06:07 +0900", "%Y-%m-%d %H:%M:%S %z").to_s }
row("Time.strptime") { Time.strptime("2026-03-04 05:06:07 UTC", "%Y-%m-%d %H:%M:%S %Z").utc.inspect }
row("Time.strptime z") { t = Time.strptime("2026-03-04 05:06:07 +0530", "%Y-%m-%d %H:%M:%S %z"); [t.inspect, t.utc_offset] }
row("Time.strptime s") { Time.strptime("1700000000", "%s").utc.inspect }
row("Time.strptime N") { Time.strptime("05:06:07.123456789", "%H:%M:%S.%N").nsec }
row("Time.strptime bad") { Time.strptime("x", "%Y") }

["2026-03-04 05:06:07 +0000", "2026-03-04T05:06:07Z", "Sat Aug 28 02:55:50 2021", "Sat, 28 Aug 2021 02:55:50 GMT",
 "28 Aug 2021", "H31.04.30", "2021-W34-6", "2021-240", "10:20:30.5 pm EST", "Aug 28", "3rd Feb 2001 04:05:06 PM",
 "20210828T025550", "nonsense", "12/31/1999", "1999-12-31 bc"].each do |s|
  row("_parse #{s.inspect}") { Date._parse(s) }
  row("_parse nocomp #{s.inspect}") { Date._parse(s, false) }
end
row("Time.parse") { Time.parse("2026-03-04 05:06:07 +0000").utc.httpdate }
row("Date.parse") { Date.parse("Sat Aug 28 2021").to_s }
row("Date.parse bad") { Date.parse("nonsense") }
puts "end"
