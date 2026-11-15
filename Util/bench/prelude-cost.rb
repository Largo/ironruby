# What a core method costs when it is written in Ruby (as the prelude's are) against the same
# method in C#: ns per call of the method, the caller a `while` loop in a method. See
# truffleruby-lessons.md for what the numbers say.
#
#   IR_CONFIG=Release ./ir.sh Util/bench/prelude-cost.rb               # JIT and OSR on (the default)
#   IR_CONFIG=Release ./ir.sh -X:NoJIT -X:NoOSR Util/bench/prelude-cost.rb
#   ruby Util/bench/prelude-cost.rb                                    # CRuby, for the built-in column
#   ... prelude-cost.rb each4 rb_each4                                 # only these (one process each is steadiest)
#   ... prelude-cost.rb --list                                         # the shapes' names
#   ruby Util/perf/ab.rb Util/bench/prelude-cost.rb cruby=ruby "base=..." "new=..." -- g_bg   # two builds, taking turns
#
# Four groups: a core method in C# against the same method in Ruby (each4 / rb_each4 ...); the parts a Ruby
# iterator is made of (e0, e_y1 ...: the method is called with a block and does only the part named);
# the openings a Ruby iterator has (g_*: `return to_enum(...) unless block_given?' and its variants);
# and the ways a wrapper takes and passes on its arguments (n_*, w_*).
#
# Every line prints a checksum next to its time: a pair (`each4`, `rb_each4`) must print the
# same one, and the same one as CRuby.
$stdout.sync = true

# The Ruby versions, written the way a prelude method is written when nothing but the
# language is available to it: a `while` over the receiver and `yield`.
class Array
  def rb_each
    return to_enum(:rb_each) { size } unless block_given?
    i = 0
    while i < size
      yield self[i]
      i += 1
    end
    self
  end

  def rb_map
    return to_enum(:rb_map) { size } unless block_given?
    result = ::Array.new(size)
    i = 0
    while i < size
      result[i] = yield self[i]
      i += 1
    end
    result
  end

  def rb_select
    return to_enum(:rb_select) { size } unless block_given?
    result = []
    i = 0
    while i < size
      e = self[i]
      result << e if yield e
      i += 1
    end
    result
  end

  def rb_inject(acc)
    i = 0
    while i < size
      acc = yield acc, self[i]
      i += 1
    end
    acc
  end

  # the shapes the prelude's compatibility wrappers have
  def rb_each_blockarg(&block)
    return to_enum(:rb_each_blockarg) { size } unless block
    i = 0
    while i < size
      block.call(self[i])
      i += 1
    end
    self
  end

  def wrap_each(*args, &block)
    each(*args, &block)
  end

  # a coercing method: Array#first(n), which takes anything with #to_int
  def rb_first(n = (none = true; nil))
    return self[0] if none
    n = n.to_int unless ::Integer === n
    ::Kernel.raise ::ArgumentError, "negative array size" if n < 0
    self[0, n]
  end

  def rb_empty?
    size == 0
  end
end

class Integer
  def rb_times
    return to_enum(:rb_times) { self } unless block_given?
    i = 0
    while i < self
      yield i
      i += 1
    end
    self
  end

  def rb_zero?
    self == 0
  end

  def rb_between?(min, max)
    self >= min && self <= max
  end
end

module Kernel
  def rb_nil?
    false
  end

  def rb_tap
    yield self
    self
  end

  def rb_itself
    self
  end
end

class H
  def initialize; @a4 = [1, 2, 3, 4]; @a1k = Array.new(1000) { |i| i }; @a = @a4; @k = @a1k; end

  # iterators over 4 elements: the price of calling the iterator at all
  def each4;        s = 0; @a4.each { |x| s += x }; s; end
  def rb_each4;     s = 0; @a4.rb_each { |x| s += x }; s; end
  def rb_eachb4;    s = 0; @a4.rb_each_blockarg { |x| s += x }; s; end
  def wrap_each4;   s = 0; @a4.wrap_each { |x| s += x }; s; end
  def map4;         @a4.map { |x| x + 1 }.size; end
  def rb_map4;      @a4.rb_map { |x| x + 1 }.size; end
  def select4;      @a4.select { |x| x > 2 }.size; end
  def rb_select4;   @a4.rb_select { |x| x > 2 }.size; end
  def times4;       s = 0; 4.times { |i| s += i }; s; end
  def rb_times4;    s = 0; 4.rb_times { |i| s += i }; s; end
  def inject4;      @a4.inject(0) { |s, x| s + x }; end
  def rb_inject4;   @a4.rb_inject(0) { |s, x| s + x }; end

  # over 1000 elements: the price of one iteration
  def each1k;       s = 0; @a1k.each { |x| s += x }; s; end
  def rb_each1k;    s = 0; @a1k.rb_each { |x| s += x }; s; end
  def rb_eachb1k;   s = 0; @a1k.rb_each_blockarg { |x| s += x }; s; end
  def map1k;        @a1k.map { |x| x + 1 }.size; end
  def rb_map1k;     @a1k.rb_map { |x| x + 1 }.size; end
  def select1k;     @a1k.select { |x| x > 500 }.size; end
  def rb_select1k;  @a1k.rb_select { |x| x > 500 }.size; end
  def times1k;      s = 0; 1000.times { |i| s += i }; s; end
  def rb_times1k;   s = 0; 1000.rb_times { |i| s += i }; s; end
  def inject1k;     @a1k.inject(0) { |s, x| s + x }; end
  def rb_inject1k;  @a1k.rb_inject(0) { |s, x| s + x }; end

  # no block
  def first2;       @a4.first(2).size; end
  def rb_first2;    @a4.rb_first(2).size; end
  def first0;       @a4.first; end
  def rb_first0;    @a4.rb_first; end
  def empty;        @a4.empty? ? 1 : 0; end
  def rb_empty;     @a4.rb_empty? ? 1 : 0; end
  def nilq(o);      o.nil? ? 1 : 0; end
  def rb_nilq(o);   o.rb_nil? ? 1 : 0; end
  def zero(i);      i.zero? ? 1 : 0; end
  def rb_zero(i);   i.rb_zero? ? 1 : 0; end
  def between(i);   i.between?(10, 20) ? 1 : 0; end
  def rb_between(i); i.rb_between?(10, 20) ? 1 : 0; end
  def itself_(o);   o.itself.size; end
  def rb_itself(o); o.rb_itself.size; end
  def tap_(o);      o.tap { |x| x }.size; end
  def rb_tap(o);    o.rb_tap { |x| x }.size; end
end

# ---- the parts of a Ruby iterator
class Array
  def e0; end                         # call with a block, nothing done
  def e_bg; block_given?; end
  def e_def; defined?(yield); end
  def e_y1; yield 1; end
  def e_y4; yield 1; yield 2; yield 3; yield 4; end
  def e_size4; size; size; size; size; end
  def e_idx4; self[0]; self[1]; self[2]; self[3]; end
  def e_while4; i = 0; while i < 4; i += 1; end; self; end
  def e_whilesize; i = 0; while i < size; i += 1; end; self; end
  def e_whileidx; i = 0; n = size; while i < n; self[i]; i += 1; end; self; end
  def e_whiley; i = 0; while i < 4; yield i; i += 1; end; self; end
  def e_full; i = 0; while i < size; yield self[i]; i += 1; end; self; end
  def e_blk(&b); end
  def e_blkcall(&b); b.call(1); end
  def e_splat(*a); end
  def e_splatblk(*a, &b); end
  def e_fwd(*a, &b); e_y1(*a, &b); end
  def e_enum; return to_enum(:e_enum) { size } unless block_given?; self; end
end

# ---- openings
class Array
  def g0; self; end
  def g_bg; return to_enum(:g_bg) unless block_given?; self; end
  def g_bgb; return to_enum(:g_bgb) { size } unless block_given?; self; end
  def g_def; return to_enum(:g_def) unless defined?(yield); self; end
  def g_defb; return to_enum(:g_defb) { size } unless defined?(yield); self; end
  def g_blk(&block); return to_enum(:g_blk) unless block; self; end
  def g_blkb(&block); return to_enum(:g_blkb) { size } unless block; self; end
  def g_bgy; return to_enum(:g_bgy) { size } unless block_given?; yield 1; self; end
  def g_defy; return to_enum(:g_defy) { size } unless defined?(yield); yield 1; self; end
  def g_y; yield 1; self; end
end

# ---- wrappers
class Array
  def raw(a = 0); yield a; end
  def w_splat(*args, &block); raw(*args, &block); end
  def w_splat_sel(*args, &block); args.empty? ? raw(&block) : raw(args[0], &block); end
  def w_opt(a = (no_a = true; nil), &block); no_a ? raw(&block) : raw(a, &block); end
  def w_optnil(a = nil, &block); a.nil? ? raw(&block) : raw(a, &block); end
  def w_anon(a = (no_a = true; nil), &); no_a ? raw(&) : raw(a, &); end
  def w_fwd(...); raw(...); end
  def w_yield(a = (no_a = true; nil)); no_a ? raw { |x| yield x } : raw(a) { |x| yield x }; end
  def n_splat(*args); args.empty? ? size : args[0]; end
  def n_opt(a = (no_a = true; nil)); no_a ? size : a; end
  def n_optnil(a = nil); a.nil? ? size : a; end
  def n_none; size; end
end

class H
  def e0; s = 0; @a.e0 { |x| s += x }; s; end
  def k_e0; s = 0; @k.e0 { |x| s += x }; s; end
  def e_bg; s = 0; @a.e_bg { |x| s += x }; s; end
  def k_e_bg; s = 0; @k.e_bg { |x| s += x }; s; end
  def e_def; s = 0; @a.e_def { |x| s += x }; s; end
  def k_e_def; s = 0; @k.e_def { |x| s += x }; s; end
  def e_y1; s = 0; @a.e_y1 { |x| s += x }; s; end
  def k_e_y1; s = 0; @k.e_y1 { |x| s += x }; s; end
  def e_y4; s = 0; @a.e_y4 { |x| s += x }; s; end
  def k_e_y4; s = 0; @k.e_y4 { |x| s += x }; s; end
  def e_size4; s = 0; @a.e_size4 { |x| s += x }; s; end
  def k_e_size4; s = 0; @k.e_size4 { |x| s += x }; s; end
  def e_idx4; s = 0; @a.e_idx4 { |x| s += x }; s; end
  def k_e_idx4; s = 0; @k.e_idx4 { |x| s += x }; s; end
  def e_while4; s = 0; @a.e_while4 { |x| s += x }; s; end
  def k_e_while4; s = 0; @k.e_while4 { |x| s += x }; s; end
  def e_whilesize; s = 0; @a.e_whilesize { |x| s += x }; s; end
  def k_e_whilesize; s = 0; @k.e_whilesize { |x| s += x }; s; end
  def e_whileidx; s = 0; @a.e_whileidx { |x| s += x }; s; end
  def k_e_whileidx; s = 0; @k.e_whileidx { |x| s += x }; s; end
  def e_whiley; s = 0; @a.e_whiley { |x| s += x }; s; end
  def k_e_whiley; s = 0; @k.e_whiley { |x| s += x }; s; end
  def e_full; s = 0; @a.e_full { |x| s += x }; s; end
  def k_e_full; s = 0; @k.e_full { |x| s += x }; s; end
  def e_blk; s = 0; @a.e_blk { |x| s += x }; s; end
  def k_e_blk; s = 0; @k.e_blk { |x| s += x }; s; end
  def e_blkcall; s = 0; @a.e_blkcall { |x| s += x }; s; end
  def k_e_blkcall; s = 0; @k.e_blkcall { |x| s += x }; s; end
  def e_splat; s = 0; @a.e_splat { |x| s += x }; s; end
  def k_e_splat; s = 0; @k.e_splat { |x| s += x }; s; end
  def e_splatblk; s = 0; @a.e_splatblk { |x| s += x }; s; end
  def k_e_splatblk; s = 0; @k.e_splatblk { |x| s += x }; s; end
  def e_fwd; s = 0; @a.e_fwd { |x| s += x }; s; end
  def k_e_fwd; s = 0; @k.e_fwd { |x| s += x }; s; end
  def e_enum; s = 0; @a.e_enum { |x| s += x }; s; end
  def k_e_enum; s = 0; @k.e_enum { |x| s += x }; s; end
  def nb_e0; s = 0; @a.e0; s; end
  def nc_e0; s = 0; @a.e0 { |x| x }; s; end
  def nc_y1; s = 0; @a.e_y1 { |x| x }; s; end
  def nc_each; s = 0; @a.each { |x| x }; s; end
  def none; s = 0; @a.size; s; end
  def lit_each; s = 0; @a.each { |x| s += x }; s; end
  def g0; @a.g0 { |x| x }.size; end
  def g_bg; @a.g_bg { |x| x }.size; end
  def g_bgb; @a.g_bgb { |x| x }.size; end
  def g_def; @a.g_def { |x| x }.size; end
  def g_defb; @a.g_defb { |x| x }.size; end
  def g_blk; @a.g_blk { |x| x }.size; end
  def g_blkb; @a.g_blkb { |x| x }.size; end
  def g_bgy; @a.g_bgy { |x| x }.size; end
  def g_defy; @a.g_defy { |x| x }.size; end
  def g_y; @a.g_y { |x| x }.size; end
  def w_raw; @a.raw(1) { |x| x }; end
  def w_splat; @a.w_splat(1) { |x| x }; end
  def w_splat_sel; @a.w_splat_sel(1) { |x| x }; end
  def w_opt; @a.w_opt(1) { |x| x }; end
  def w_optnil; @a.w_optnil(1) { |x| x }; end
  def w_anon; @a.w_anon(1) { |x| x }; end
  def w_fwd; @a.w_fwd(1) { |x| x }; end
  def w_yield; @a.w_yield(1) { |x| x }; end
  def w_raw0; @a.raw { |x| x + 1 }; end
  def w_splat0; @a.w_splat { |x| x + 1 }; end
  def w_opt0; @a.w_opt { |x| x + 1 }; end
  def w_anon0; @a.w_anon { |x| x + 1 }; end
  def n_splat; @a.n_splat(1); end
  def n_opt; @a.n_opt(1); end
  def n_optnil; @a.n_optnil(1); end
  def n_splat0; @a.n_splat; end
  def n_opt0; @a.n_opt; end
  def n_optnil0; @a.n_optnil; end
  def n_none; @a.n_none; end
end

def cpu
  File.read("/proc/thread-self/schedstat").to_i / 1e9
rescue SystemCallError
  Process.clock_gettime(Process::CLOCK_MONOTONIC)
end

def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9
  sum = nil
  7.times do
    t0 = cpu
    sum = yield n
    d = cpu - t0
    best = d if d < best
  end
  printf "%-12s %9.1f ns  %d\n", name, best * 1e9 / n, sum
end

h = H.new
a4 = [1, 2, 3, 4]
N = (ENV['N'] || 2_000_000).to_i
K = N / 200   # the 1000-element shapes: as many iterations in all as N/5 calls over 4 elements would make


t("each4", N)       { |n| i = 0; s = 0; while i < n; s += h.each4; i += 1; end; s }
t("rb_each4", N)    { |n| i = 0; s = 0; while i < n; s += h.rb_each4; i += 1; end; s }
t("rb_eachb4", N)   { |n| i = 0; s = 0; while i < n; s += h.rb_eachb4; i += 1; end; s }
t("wrap_each4", N)  { |n| i = 0; s = 0; while i < n; s += h.wrap_each4; i += 1; end; s }
t("map4", N)        { |n| i = 0; s = 0; while i < n; s += h.map4; i += 1; end; s }
t("rb_map4", N)     { |n| i = 0; s = 0; while i < n; s += h.rb_map4; i += 1; end; s }
t("select4", N)     { |n| i = 0; s = 0; while i < n; s += h.select4; i += 1; end; s }
t("rb_select4", N)  { |n| i = 0; s = 0; while i < n; s += h.rb_select4; i += 1; end; s }
t("times4", N)      { |n| i = 0; s = 0; while i < n; s += h.times4; i += 1; end; s }
t("rb_times4", N)   { |n| i = 0; s = 0; while i < n; s += h.rb_times4; i += 1; end; s }
t("inject4", N)     { |n| i = 0; s = 0; while i < n; s += h.inject4; i += 1; end; s }
t("rb_inject4", N)  { |n| i = 0; s = 0; while i < n; s += h.rb_inject4; i += 1; end; s }

t("each1k", K)      { |n| i = 0; s = 0; while i < n; s += h.each1k; i += 1; end; s }
t("rb_each1k", K)   { |n| i = 0; s = 0; while i < n; s += h.rb_each1k; i += 1; end; s }
t("rb_eachb1k", K)  { |n| i = 0; s = 0; while i < n; s += h.rb_eachb1k; i += 1; end; s }
t("map1k", K)       { |n| i = 0; s = 0; while i < n; s += h.map1k; i += 1; end; s }
t("rb_map1k", K)    { |n| i = 0; s = 0; while i < n; s += h.rb_map1k; i += 1; end; s }
t("select1k", K)    { |n| i = 0; s = 0; while i < n; s += h.select1k; i += 1; end; s }
t("rb_select1k", K) { |n| i = 0; s = 0; while i < n; s += h.rb_select1k; i += 1; end; s }
t("times1k", K)     { |n| i = 0; s = 0; while i < n; s += h.times1k; i += 1; end; s }
t("rb_times1k", K)  { |n| i = 0; s = 0; while i < n; s += h.rb_times1k; i += 1; end; s }
t("inject1k", K)    { |n| i = 0; s = 0; while i < n; s += h.inject1k; i += 1; end; s }
t("rb_inject1k", K) { |n| i = 0; s = 0; while i < n; s += h.rb_inject1k; i += 1; end; s }

t("first2", N)      { |n| i = 0; s = 0; while i < n; s += h.first2; i += 1; end; s }
t("rb_first2", N)   { |n| i = 0; s = 0; while i < n; s += h.rb_first2; i += 1; end; s }
t("first0", N)      { |n| i = 0; s = 0; while i < n; s += h.first0; i += 1; end; s }
t("rb_first0", N)   { |n| i = 0; s = 0; while i < n; s += h.rb_first0; i += 1; end; s }
t("empty", N)       { |n| i = 0; s = 0; while i < n; s += h.empty; i += 1; end; s }
t("rb_empty", N)    { |n| i = 0; s = 0; while i < n; s += h.rb_empty; i += 1; end; s }
t("nil?", N)        { |n| i = 0; s = 0; while i < n; s += h.nilq(a4); i += 1; end; s }
t("rb_nil?", N)     { |n| i = 0; s = 0; while i < n; s += h.rb_nilq(a4); i += 1; end; s }
t("zero?", N)       { |n| i = 0; s = 0; while i < n; s += h.zero(i); i += 1; end; s }
t("rb_zero?", N)    { |n| i = 0; s = 0; while i < n; s += h.rb_zero(i); i += 1; end; s }
t("between?", N)    { |n| i = 0; s = 0; while i < n; s += h.between(i); i += 1; end; s }
t("rb_between?", N) { |n| i = 0; s = 0; while i < n; s += h.rb_between(i); i += 1; end; s }
t("itself", N)      { |n| i = 0; s = 0; while i < n; s += h.itself_(a4); i += 1; end; s }
t("rb_itself", N)   { |n| i = 0; s = 0; while i < n; s += h.rb_itself(a4); i += 1; end; s }
t("tap", N)         { |n| i = 0; s = 0; while i < n; s += h.tap_(a4); i += 1; end; s }
t("rb_tap", N)      { |n| i = 0; s = 0; while i < n; s += h.rb_tap(a4); i += 1; end; s }

# the parts of a Ruby iterator, each called with a block over four elements
t("none", N) { |n| i = 0; s = 0; while i < n; s += h.none; i += 1; end; s }
t("nb_e0", N) { |n| i = 0; s = 0; while i < n; s += h.nb_e0; i += 1; end; s }
t("nc_e0", N) { |n| i = 0; s = 0; while i < n; s += h.nc_e0; i += 1; end; s }
t("nc_y1", N) { |n| i = 0; s = 0; while i < n; s += h.nc_y1; i += 1; end; s }
t("nc_each", N) { |n| i = 0; s = 0; while i < n; s += h.nc_each; i += 1; end; s }
t("lit_each", N) { |n| i = 0; s = 0; while i < n; s += h.lit_each; i += 1; end; s }
t("e0", N) { |n| i = 0; s = 0; while i < n; s += h.e0; i += 1; end; s }
t("e_bg", N) { |n| i = 0; s = 0; while i < n; s += h.e_bg; i += 1; end; s }
t("e_def", N) { |n| i = 0; s = 0; while i < n; s += h.e_def; i += 1; end; s }
t("e_y1", N) { |n| i = 0; s = 0; while i < n; s += h.e_y1; i += 1; end; s }
t("e_y4", N) { |n| i = 0; s = 0; while i < n; s += h.e_y4; i += 1; end; s }
t("e_size4", N) { |n| i = 0; s = 0; while i < n; s += h.e_size4; i += 1; end; s }
t("e_idx4", N) { |n| i = 0; s = 0; while i < n; s += h.e_idx4; i += 1; end; s }
t("e_while4", N) { |n| i = 0; s = 0; while i < n; s += h.e_while4; i += 1; end; s }
t("e_whilesize", N) { |n| i = 0; s = 0; while i < n; s += h.e_whilesize; i += 1; end; s }
t("e_whileidx", N) { |n| i = 0; s = 0; while i < n; s += h.e_whileidx; i += 1; end; s }
t("e_whiley", N) { |n| i = 0; s = 0; while i < n; s += h.e_whiley; i += 1; end; s }
t("e_full", N) { |n| i = 0; s = 0; while i < n; s += h.e_full; i += 1; end; s }
t("e_blk", N) { |n| i = 0; s = 0; while i < n; s += h.e_blk; i += 1; end; s }
t("e_blkcall", N) { |n| i = 0; s = 0; while i < n; s += h.e_blkcall; i += 1; end; s }
t("e_splat", N) { |n| i = 0; s = 0; while i < n; s += h.e_splat; i += 1; end; s }
t("e_splatblk", N) { |n| i = 0; s = 0; while i < n; s += h.e_splatblk; i += 1; end; s }
t("e_fwd", N) { |n| i = 0; s = 0; while i < n; s += h.e_fwd; i += 1; end; s }
t("e_enum", N) { |n| i = 0; s = 0; while i < n; s += h.e_enum; i += 1; end; s }
t("k_e_whilesize", N/200) { |n| i = 0; s = 0; while i < n; s += h.k_e_whilesize; i += 1; end; s }
t("k_e_whileidx", N/200) { |n| i = 0; s = 0; while i < n; s += h.k_e_whileidx; i += 1; end; s }
t("k_e_full", N/200) { |n| i = 0; s = 0; while i < n; s += h.k_e_full; i += 1; end; s }

# openings
t("g0", N) { |n| i = 0; s = 0; while i < n; s += h.g0; i += 1; end; s }
t("g_bg", N) { |n| i = 0; s = 0; while i < n; s += h.g_bg; i += 1; end; s }
t("g_bgb", N) { |n| i = 0; s = 0; while i < n; s += h.g_bgb; i += 1; end; s }
t("g_def", N) { |n| i = 0; s = 0; while i < n; s += h.g_def; i += 1; end; s }
t("g_defb", N) { |n| i = 0; s = 0; while i < n; s += h.g_defb; i += 1; end; s }
t("g_blk", N) { |n| i = 0; s = 0; while i < n; s += h.g_blk; i += 1; end; s }
t("g_blkb", N) { |n| i = 0; s = 0; while i < n; s += h.g_blkb; i += 1; end; s }
t("g_y", N) { |n| i = 0; s = 0; while i < n; s += h.g_y; i += 1; end; s }
t("g_bgy", N) { |n| i = 0; s = 0; while i < n; s += h.g_bgy; i += 1; end; s }
t("g_defy", N) { |n| i = 0; s = 0; while i < n; s += h.g_defy; i += 1; end; s }

# wrappers
t("w_raw", N) { |n| i = 0; s = 0; while i < n; s += h.w_raw; i += 1; end; s }
t("w_splat", N) { |n| i = 0; s = 0; while i < n; s += h.w_splat; i += 1; end; s }
t("w_splat_sel", N) { |n| i = 0; s = 0; while i < n; s += h.w_splat_sel; i += 1; end; s }
t("w_opt", N) { |n| i = 0; s = 0; while i < n; s += h.w_opt; i += 1; end; s }
t("w_optnil", N) { |n| i = 0; s = 0; while i < n; s += h.w_optnil; i += 1; end; s }
t("w_anon", N) { |n| i = 0; s = 0; while i < n; s += h.w_anon; i += 1; end; s }
t("w_fwd", N) { |n| i = 0; s = 0; while i < n; s += h.w_fwd; i += 1; end; s }
t("w_yield", N) { |n| i = 0; s = 0; while i < n; s += h.w_yield; i += 1; end; s }
t("w_raw0", N) { |n| i = 0; s = 0; while i < n; s += h.w_raw0; i += 1; end; s }
t("w_splat0", N) { |n| i = 0; s = 0; while i < n; s += h.w_splat0; i += 1; end; s }
t("w_opt0", N) { |n| i = 0; s = 0; while i < n; s += h.w_opt0; i += 1; end; s }
t("w_anon0", N) { |n| i = 0; s = 0; while i < n; s += h.w_anon0; i += 1; end; s }
t("n_none", N) { |n| i = 0; s = 0; while i < n; s += h.n_none; i += 1; end; s }
t("n_splat", N) { |n| i = 0; s = 0; while i < n; s += h.n_splat; i += 1; end; s }
t("n_opt", N) { |n| i = 0; s = 0; while i < n; s += h.n_opt; i += 1; end; s }
t("n_optnil", N) { |n| i = 0; s = 0; while i < n; s += h.n_optnil; i += 1; end; s }
t("n_splat0", N) { |n| i = 0; s = 0; while i < n; s += h.n_splat0; i += 1; end; s }
t("n_opt0", N) { |n| i = 0; s = 0; while i < n; s += h.n_opt0; i += 1; end; s }
t("n_optnil0", N) { |n| i = 0; s = 0; while i < n; s += h.n_optnil0; i += 1; end; s }
