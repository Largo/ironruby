# Backtraces of library-raised exceptions rescued in various ways; prints the same on CRuby and IronRuby.
def bt(e) = e.backtrace.first(3).map { |l| l.sub(/^.*\//, "").sub(/:in .*/, "") }.inspect
def a1; Integer("x"); end
def a2; a1; end
def f1; Float("y"); end
def k1; {a: 1}.fetch(:b); end
def n1; nil.zork; end
def v1; zork_undefined; end
H = {}
[:a2, :f1, :k1, :n1, :v1].each do |m|
  begin; send(m); rescue => e; puts "#{m} #{e.class} #{bt(e)}"; end
  x = (send(m) rescue $!); puts "#{m} $! #{bt(x)}"
  y = nil
  3.times { begin; send(m); rescue; y = $!; end }
  puts "#{m} loop #{bt(y)}"
  z = begin; send(m); rescue ArgumentError, KeyError, NameError; $!; end
  puts "#{m} later #{bt(z)} #{z.message[0, 30]}"
  H[m] = lambda { begin; send(m); rescue; end; begin; send(m); rescue => q; q; end }.call
  puts "#{m} lambda #{bt(H[m])}"
end
p Integer("x", exception: false), (Integer("12") rescue 0), ({}.fetch(:q) rescue :d)
e = {a: 1}.fetch(:zz) rescue $!; p [e.key, e.receiver]
# CRuby's Hash#fetch does not call KeyError#initialize; where it is called the kept exception's backtrace must be whole
def kk; {}.fetch(:w); end
($kept = nil; (kk rescue nil); puts "kept #{$kept ? bt($kept) : "-"}")
class KeyError; def initialize(*a, **k); $kept = self; super; end; end
($kept = nil; (kk rescue nil); puts "kept #{$kept ? bt($kept) : "-"}")
