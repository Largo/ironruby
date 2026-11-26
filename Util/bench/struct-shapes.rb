# What creating a Struct object and a Struct class costs (LEDGER gaps 24 and 19): ns per operation, the caller a
# `while' loop. The switch is the other arm:
#
#   ruby Util/perf/ab.rb -r 6 Util/bench/struct-shapes.rb "off=env IR_FAST_STRUCT=0 IR_CONFIG=Release ./ir.sh" \
#        "on=env IR_CONFIG=Release ./ir.sh"
#
# kw_new is Struct.new(..., keyword_init: true)'s new with keywords; kwnil_new the same keywords to a struct made
# without keyword_init: (nil: it takes both); kw_hash a keyword_init struct given one Hash argument; pos_new a
# positional new; sub_kw a subclass of a keyword_init struct whose initialize calls super with keywords. kwinit is
# a class whose initialize takes two required keywords, hash_dup a Hash#dup of three keys. new_class makes a Struct
# class (Struct.new(:a)); new_class_use makes one and calls its new and its getter once (a first call on a new
# class); both run N/40 times. getter, setter, obj_new and plain are controls.
$stdout.sync = true

KW = Struct.new(:a, :b, keyword_init: true)
KN = Struct.new(:a, :b)
P = Struct.new(:a, :b)
class SubKW < KW
  def initialize(a:, b: 2)
    super(a: a, b: b)
  end
end
class Product
  attr_reader :sku, :name
  def initialize(sku:, name:)
    @sku = sku
    @name = name
  end
end
class Obj
  attr_reader :a
  def initialize(a, b)
    @a = a
    @b = b
  end
end
def plain(a) = a

def cpu
  File.read("/proc/thread-self/schedstat").to_i / 1e9
rescue SystemCallError
  Process.clock_gettime(Process::CLOCK_MONOTONIC)
end

INSTR = ENV['INSTR'] && (require_relative '../perf/instructions'; Instructions.open || warn('no instruction counter (perf_event_open)'))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9
  fewest = nil
  counted = !!INSTR
  sum = nil
  7.times do
    c0 = INSTR && INSTR.read
    t0 = cpu
    sum = yield n
    d = cpu - t0
    c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1
      c = c1 - c0
      fewest = c if fewest.nil? || c < fewest
    else
      counted = false
    end
  end
  printf "%-14s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format('  %.1f instr', fewest.to_f / n) : ''
end
N = (ENV['N'] || 200_000).to_i
NC = [N / 40, 20].max
H3 = { a: 1, b: 2, c: 3 }
PT = P.new(1, 2)

t("kw_new", N) { |n| i = 0; c = 0; while i < n; c += KW.new(a: i, b: 2).a; i += 1; end; c }
t("kwnil_new", N) { |n| i = 0; c = 0; while i < n; c += KN.new(a: i, b: 2).a; i += 1; end; c }
t("kw_hash", N) { |n| i = 0; c = 0; h = { a: 1, b: 2 }; while i < n; c += KW.new(h).b + i; i += 1; end; c }
t("sub_kw", N) { |n| i = 0; c = 0; while i < n; c += SubKW.new(a: i).b; i += 1; end; c }
t("pos_new", N) { |n| i = 0; c = 0; while i < n; c += P.new(i, 2).a; i += 1; end; c }
t("kwinit", N) { |n| i = 0; c = 0; while i < n; c += Product.new(sku: i, name: "x").sku; i += 1; end; c }
t("hash_dup", N) { |n| i = 0; c = 0; while i < n; c += H3.dup.size + i; i += 1; end; c }
t("new_class", NC) { |n| i = 0; c = 0; while i < n; c += Struct.new(:a).members.size; i += 1; end; c }
t("new_class_use", NC) { |n| i = 0; c = 0; while i < n; k = Struct.new(:a, :b); c += k.new(i, 1).a; i += 1; end; c }
t("getter", N) { |n| i = 0; c = 0; while i < n; c += PT.a + i; i += 1; end; c }
t("setter", N) { |n| i = 0; c = 0; s = P.new(0, 0); while i < n; s.b = i; c += s.b; i += 1; end; c }
t("obj_new", N) { |n| i = 0; c = 0; while i < n; c += Obj.new(i, 2).a; i += 1; end; c }
t("plain", N) { |n| i = 0; c = 0; while i < n; c += plain(i); i += 1; end; c }
