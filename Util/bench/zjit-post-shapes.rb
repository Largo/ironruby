# The shapes the ZJIT post of 2026-10-09 names as what a method JIT for application code works on
# (Util/bench/zjit-lessons.md): Struct objects, a method made by define_method, Hash#each_pair,
# rest and keyword parameters, begin/rescue and ensure in a method that does not raise, a raise.
# `<shape> <ns> ns <checksum>' per line, for Util/perf/ab.rb; `ruby zjit-post-shapes.rb NAME...'
# runs only the shapes named.
def t(name, n = 2_000_000)
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; r = nil
  5.times { t0 = Process.clock_gettime(Process::CLOCK_MONOTONIC); r = yield n; d = Process.clock_gettime(Process::CLOCK_MONOTONIC) - t0; best = d if d < best }
  printf "%-14s %8.1f ns %s\n", name, best * 1e9 / n, r
end
S = Struct.new(:a, :b)
SK = Struct.new(:a, :b, keyword_init: true)
class P; attr_reader :a, :b; def initialize(a, b); @a = a; @b = b; end; end
class B
  define_method(:bm) { |x| x + 1 }
  define_method(:bm0) { 1 }
  def dm(x) x + 1 end
  def rest(*a) a.size end
  def pass(*a) rest(*a) end
  def kw(a:, b: 2) a + b end
  def resc(x) begin; x + 1; rescue StandardError; 0; end end
  def ens(x) begin; x + 1; ensure; @e = x; end end
  def opt(a, b = 2) a + b end
  def blk(&b) b end
  def yld; yield 1; end
end
o = B.new
H8 = { a: 1, b: 2, c: 3, d: 4, e: 5, f: 6, g: 7, h: 8 }
A8 = [1, 2, 3, 4, 5, 6, 7, 8]
t("struct_new")  { |n| i = 0; c = 0; while i < n; s = S.new(i, 2); c += s.a + s.b; i += 1; end; c }
t("struct_kw", 500_000) { |n| i = 0; c = 0; while i < n; s = SK.new(a: i, b: 2); c += s.a + s.b; i += 1; end; c }
t("struct_set")  { |n| i = 0; s = S.new(0, 2); while i < n; s.a = s.a + s.b; i += 1; end; s.a }
t("obj_new")     { |n| i = 0; c = 0; while i < n; s = P.new(i, 2); c += s.a + s.b; i += 1; end; c }
t("def_call")    { |n| i = 0; c = 0; while i < n; c = o.dm(c); i += 1; end; c }
t("bmethod")     { |n| i = 0; c = 0; while i < n; c = o.bm(c); i += 1; end; c }
t("bmethod0")    { |n| i = 0; c = 0; while i < n; c += o.bm0; i += 1; end; c }
t("each_pair8", 300_000) { |n| i = 0; c = 0; while i < n; H8.each_pair { |k, v| c += v }; i += 1; end; c }
t("hash_each8", 300_000) { |n| i = 0; c = 0; while i < n; H8.each { |k, v| c += v }; i += 1; end; c }
t("each_wi8", 300_000)   { |n| i = 0; c = 0; while i < n; A8.each_with_index { |x, j| c += x + j }; i += 1; end; c }
t("rest3")       { |n| i = 0; c = 0; while i < n; c += o.rest(i, 2, 3); i += 1; end; c }
t("pass3")       { |n| i = 0; c = 0; while i < n; c += o.pass(i, 2, 3); i += 1; end; c }
t("kwargs")      { |n| i = 0; c = 0; while i < n; c += o.kw(a: 1); i += 1; end; c }
t("optarg")      { |n| i = 0; c = 0; while i < n; c += o.opt(1); i += 1; end; c }
t("rescue")      { |n| i = 0; c = 0; while i < n; c = o.resc(c); i += 1; end; c }
t("ensure")      { |n| i = 0; c = 0; while i < n; c = o.ens(c); i += 1; end; c }
t("yield")       { |n| i = 0; c = 0; while i < n; c += o.yld { |x| x }; i += 1; end; c }
t("blockarg")    { |n| i = 0; c = 0; while i < n; c += 1 if o.blk { 1 }; i += 1; end; c }
t("raise", 200_000) { |n| i = 0; c = 0; while i < n; begin; raise ArgumentError; rescue ArgumentError; c += 1; end; i += 1; end; c }
