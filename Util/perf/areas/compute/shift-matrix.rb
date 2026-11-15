# Differential check for the JIT's Integer#<< / #>>: run under CRuby and IronRuby, diff the output.
VALS = [0, 1, -1, 5, -5, 255, 0x7fffffff, -0x80000000, 0xffffffff, 2**40, -(2**40), 2**62, 2**63 - 1, -(2**63)]
WS = [0, 1, 3, 8, 24, 30, 31, 32, 33, 62, 63, 64, 65, 100, -1, -8, -31, -32, -63, -64, -100]
def shl(a, b); a << b; end
def shr(a, b); a >> b; end
def loop_shl(a, b); i = 0; r = 0; while i < 3; r = a << b; i += 1; end; r; end
def loop_shr(a, b); i = 0; r = 0; while i < 3; r = a >> b; i += 1; end; r; end
def mix(n); i = 0; s = 0; while i < n; s = ((s << 5) ^ i ^ (s >> 3)) & 0xffffffff; i += 1; end; s; end
def grow(n); i = 0; s = 1; while i < n; s = s << 7; i += 1; end; s; end
def neg(n); i = 0; s = 0; while i < n; s += (i >> -2) + (i << -1); i += 1; end; s; end
50.times do
  VALS.each { |a| WS.each { |b| x = [shl(a, b), shr(a, b), loop_shl(a, b), loop_shr(a, b)]; puts "#{a} #{b} #{x.inspect}" if $last } }
end
$last = true
VALS.each { |a| WS.each { |b| puts "#{a} #{b} #{[shl(a, b), shr(a, b), loop_shl(a, b), loop_shr(a, b)].inspect}" } }
p 30.times.map { |k| mix(1000 * k) }
p 30.times.map { |k| grow(k * 2) }.map(&:bit_length)
p 30.times.map { |k| neg(1000 * k) }
p (1 << 2.0 rescue $!.class), (8 >> 1.5 rescue $!.class)
