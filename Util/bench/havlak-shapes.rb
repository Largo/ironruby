# Shapes for Havlak (Util/perf/LEDGER.md gap 25): som.rb's Dictionary#at, whose `e.match(hash, key)' is a call on a
# local nothing samples (JitCompiler.UnsampledCallees, IR_JIT_UNSAMPLED), in the format Util/perf/ab.rb reads
# (`<shape> <ns> ns <checksum>`; N from the environment; INSTR=1 or Util/perf/callgrind/count.sh for instructions).
#
#   ruby Util/perf/ab.rb -i -r 1 -c none -n 20000 Util/bench/havlak-shapes.rb \
#        "base=Util/perf/callgrind/count.sh ." "new=Util/perf/callgrind/count.sh ../wt-NAME"
$stdout.sync = true

class HsEntry
  attr_reader :hash, :key, :value
  attr_accessor :next
  def initialize(hash, key, value, nxt); @hash = hash; @key = key; @value = value; @next = nxt; end
  def match(hash, key); @hash == hash && @key == key; end
end

class HsKey
  def initialize(n); @n = n; end
  def custom_hash; @n; end
end

# som.rb's Dictionary, an identity one, as Havlak uses it
class HsDict
  def initialize(size = 16); @buckets = Array.new(size); @size = 0; end
  def hash(key)
    return 0 unless key
    hash = key.custom_hash
    hash ^ hash >> 16
  end
  def get_bucket_idx(hash); (@buckets.size - 1) & hash; end
  def get_bucket(hash); @buckets[get_bucket_idx(hash)]; end
  def at(key)
    hash = hash(key)
    e = get_bucket(hash)
    while e
      return e.value if e.match(hash, key)
      e = e.next
    end
    nil
  end
  def at_put(key, value)
    hash = hash(key)
    i = get_bucket_idx(hash)
    @buckets[i] = HsEntry.new(hash, key, value, @buckets[i])
  end
  # control: a method of the same shape whose loop calls nothing on the local
  def chain_length(key)
    e = get_bucket(hash(key))
    n = 0
    while e
      n += 1
      e = e.next
    end
    n
  end
end

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
INSTR = ENV["INSTR"] && (require_relative "../perf/instructions"; Instructions.open || warn("no instruction counter (perf_event_open)"))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; fewest = nil; counted = !!INSTR; sum = nil
  7.times do
    c0 = INSTR && INSTR.read; t0 = cpu; sum = yield n; d = cpu - t0; c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end

d = HsDict.new
keys = (0...24).map { |i| HsKey.new(i) }
keys.each_with_index { |k, i| d.at_put(k, i) }
first = keys[23]   # the head of its chain
deep = keys[7]     # behind one other in its chain
missing = HsKey.new(100)
N = (ENV["N"] || 1_000_000).to_i
t("at_first", N) { |n| i = 0; s = 0; while i < n; s += d.at(first); i += 1; end; s }
t("at_chain", N) { |n| i = 0; s = 0; while i < n; s += d.at(deep); i += 1; end; s }
t("at_miss", N) { |n| i = 0; s = 0; while i < n; s += 1 if d.at(missing).nil?; i += 1; end; s }
t("chain_len", N) { |n| i = 0; s = 0; while i < n; s += d.chain_length(deep); i += 1; end; s }
