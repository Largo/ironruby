# Differential matrix for CSV#read (which parses without an Enumerator on IronRuby) and for
# Kernel#Float's error on a String. Prints the same on CRuby 4.0 and IronRuby, JIT on and off:
#   ruby Util/csv-matrix.rb > a; ./ir.sh Util/csv-matrix.rb > b; ./ir.sh -X:NoJIT -X:NoOSR Util/csv-matrix.rb > c
require 'csv'
def row(label)
  r = begin
    v = yield
    v.is_a?(CSV::Table) ? [:table, v.headers, v.map(&:to_h)] : v
  rescue Exception => e
    [e.class, e.message]
  end
  puts "#{label}: #{r.inspect}"
end
S = "a,b,c\n1,2.5,x\n\"q,1\",\"line\nbreak\",\"\"\"\"\n,,\n"
row("plain") { CSV.parse(S) }
row("headers") { CSV.parse(S, headers: true) }
row("numeric") { CSV.parse(S, converters: :numeric) }
row("all") { CSV.parse(S, headers: true, converters: :all, header_converters: :symbol) }
row("return_headers") { CSV.parse(S, headers: true, return_headers: true).map(&:to_a) }
row("blank skip") { CSV.parse("a\n\nb\n", skip_blanks: true) }
row("skip_lines") { CSV.parse("#c\na\n", skip_lines: /\A#/) }
row("crlf") { CSV.parse("a,b\r\n1,2\r\n") }
row("utf8") { CSV.parse("é,ü\n1,2\n").map { |r| r.map { |f| [f, f.encoding.to_s] } } }
row("sjis") { CSV.parse("a,\x82\xa0\n".dup.force_encoding("Shift_JIS")).map { |r| r.map(&:encoding).map(&:to_s) } }
row("bad utf8") { CSV.parse("a,\xff\n") }
row("unclosed") { CSV.parse("a,\"b\n") }
row("stray quote") { CSV.parse("a,b\"c\n") }
row("field size") { CSV.parse("abcdef\n", max_field_size: 3) }
row("liberal") { CSV.parse("a,b\"c\n", liberal_parsing: true) }
row("conv raises") { CSV.parse("1,2\n", converters: [->(f) { raise ArgumentError, "no #{f}" }]) }
row("conv frozen") { CSV.parse("1,x\n", converters: [->(f) { f.frozen? }]) }
row("conv sees fiber local") { Thread.current[:csvm] = 1; CSV.parse("1\n", converters: [->(f) { Thread.current[:csvm].inspect }]) }
row("conv break") { CSV.parse("1\n2\n", converters: [->(f) { throw :t, f }]) rescue nil; catch(:t) { CSV.parse("1\n2\n", converters: [->(f) { throw :t, f }]) } }
row("hconv sees fiber local") { Thread.current[:csvm] = 2; CSV.parse("h\n1\n", headers: true, header_converters: [->(h) { Thread.current[:csvm].inspect }]).headers }
row("named conv fiber local") { CSV::Converters[:csvm] = ->(f) { Thread.current[:csvm].inspect }; CSV.parse("1\n", converters: :csvm) }
o = Object.new; def o.match(l) = (Thread.current[:csvm] ? nil : l.start_with?("#"))
row("skip_lines object") { CSV.parse("#c\na\n", skip_lines: o) }
row("skip_lines string") { CSV.parse("#c\na\n", skip_lines: "#") }
class MyIO < StringIO; def gets(*) = (Thread.current[:csvm] ? "seen\n" : super); end
row("io subclass") { CSV.new(MyIO.new("a\nb\n")).read }
row("frozen string") { CSV.parse("a,b\n".freeze) }
row("binary") { CSV.parse("a,b\n".b).map { |r| r.map(&:encoding).map(&:to_s) } }
row("col_sep row_sep") { CSV.parse("a;b|c;d|", col_sep: ";", row_sep: "|") }
row("nil/empty value") { CSV.parse("a,,\"\"\n", nil_value: 0, empty_value: 1) }
row("unconverted") { CSV.parse("1,x\n", converters: :integer, unconverted_fields: true).map(&:unconverted_fields) }
row("strip") { CSV.parse(" a , b \n", strip: true) }
row("date converters") { CSV.parse("2026-01-02,2026-01-02 03:04:05,nodate\n", converters: [:date, :date_time]).inspect }
c = CSV.new("h\n1\n2\n3\n", headers: true)
row("shift then read") { [c.shift.to_h, c.read.map(&:to_h)] }
row("after read: shift eof read") { [c.shift, c.eof?, c.read.to_a, c.lineno] }
row("rewind read") { c.rewind; c.read.map(&:to_h) }
c = CSV.new("a\nb\n")
row("read twice") { [c.read, c.read, c.shift, c.eof?, c.lineno, c.line] }
c = CSV.new("a\n\"b\nc\n")
row("malformed read") { c.read }
row("malformed then shift") { [c.shift, c.eof?] }
row("malformed then read") { c.read }
c = CSV.new("a\nb\n")
row("eof? then read") { [c.eof?, c.read] }
c = CSV.new("x\ny\n", headers: true, return_headers: true)
row("return_headers twice") { [c.read.to_a, c.read.to_a, c.shift] }
row("readlines") { CSV.new("1\n2\n").readlines }
row("table") { CSV.parse(S, headers: true).by_col[0] }
row("each block") { a = []; CSV.parse("1\n2\n") { |r| a << r }; a }
row("foreach-ish each break") { c = CSV.new("1\n2\n3\n"); c.each { |r| break }; [c.shift, c.read] }
# Kernel#Float on a String that is not a number, rescued and inspected
["item 1", "2026-02-11", "", " ", "1_", "1e", "0x", "é", "1\xff".b, "abc".encode("UTF-16LE")].each do |s|
  row("Float(#{s.inspect})") { Float(s) }
  row("Float(#{s.inspect}) rescue") { (Float(s) rescue $!.message) }
end
row("Float backtrace") { def flt(s) = Float(s); begin; flt("q"); rescue => e; [e.backtrace.size > 1, e.backtrace_locations[0].label, e.cause] end }
row("Float rescue then $!") { x = (Float("q") rescue 1); [x, $!] }
row("Float exception: false") { [Float("q", exception: false), Float("1.5", exception: false)] }
row("Float in ensure") { r = []; begin; begin; Float("z"); ensure; r << $!.message; end; rescue => e; r << e.message; end; r }
row("Float cause") { begin; begin; raise "outer"; rescue; Float("w"); end; rescue => e; [e.message, e.cause&.message] end }
row("Float kept") { e = (Float("k") rescue $!); [e.message, e.backtrace.is_a?(Array), e.backtrace_locations&.first&.lineno == __LINE__] }
class String; alias_method :__csvm_inspect, :inspect; def inspect = "<#{__csvm_inspect}>"; end
row("Float redefined inspect") { Float("r") }
class String; alias_method :inspect, :__csvm_inspect; end

# Review of round 35: CSV#read takes the fast path only where CRuby's #read -> #to_a -> #each
# could not tell it from the enumerator: a subclass or singleton #each, a Regexp subclass's
# match? and a Warning.warn handler under $VERBOSE (fiber-local state) each see what CRuby shows.
class ReviewCSV < CSV; def each; return to_enum unless block_given?; super { |r| yield ["filtered"] }; end; end
p ReviewCSV.new("raw\nx\n").read
review_csv = CSV.new("a\nb\n"); def review_csv.each(&) = (super { |r| yield r.map(&:upcase) }); p review_csv.read
class ReviewRe < Regexp; def match?(s) = (Thread.current[:review_csvm] ||= :seen; super); end
p CSV.parse("#x\na\n", skip_lines: ReviewRe.new("\\A#")), Thread.current[:review_csvm].inspect
review_old_verbose = $VERBOSE
module Warning; class << self; alias review_orig_warn warn; def warn(m, **) = (Thread.current[:review_warned] = true); end; end
$VERBOSE = true
p CSV.parse("1e400,2\n", converters: :float), Thread.current[:review_warned].inspect
$VERBOSE = review_old_verbose
module Warning; class << self; alias warn review_orig_warn; end; end
