# Struct member reads/writes in hot methods, including the cases that must deopt.
S = Struct.new(:a, :b)
T = Struct.new(:b, :a)
def rd(o) = o.a
def wr(o, v) = (o.a = v)
def loop_it(o)
  i = 0; s = 0
  while i < 200_000
    o.a = i; s += o.a; i += 1
  end
  s
end
s = S.new(1, 2)
p loop_it(s), loop_it(s)
p loop_it(T.new(0, 0))           # other class, other index
x = S.new(0, 0); def x.a = 42
p rd(x)                           # singleton reader
200_000.times { wr(s, 1) }
f = S.new(0, 0).freeze
begin; wr(f, 5); rescue => e; p e.class; end
class S; def a = :redef; end
p rd(s), s.b
p wr(s, 7), s.to_a
p loop_it(s) rescue p $!.class
