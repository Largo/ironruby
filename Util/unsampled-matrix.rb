# Differential cases for a call on a local the method JIT has no sample of (JitCompiler.UnsampledCallees,
# IR_JIT_UNSAMPLED): `e = get_bucket(hash)' before a `while e' that calls `e.match(hash, key)' and reads `e.value' and
# `e.next', as som.rb's Dictionary#at (AWFY Havlak). The call is inlined for the classes that define an inlinable
# `match' themselves, each under a check of its exact class, and the reads go through a site that checks the class.
# Each case warms `at' past the JIT's threshold, then does what must change the answer half way through a long run of
# lookups, and prints what the lookups answered.
#
# One case per process, since one case's event would change what the next compiles:
#   for c in $(ruby Util/unsampled-matrix.rb list); do echo "== $c"; ruby Util/unsampled-matrix.rb $c 2>&1; done > cruby.txt
#   (the same with IR_CONFIG=Release ./ir.sh, with -X:NoJIT -X:NoOSR, with -X:NoJITCalls and with IR_JIT_UNSAMPLED=0)
# IR_JIT_VERBOSE=1 shows `inlined Ent#match into at' and `specialized Dict/at': the path is reached.

CASES = %w[plain class_change two_classes redefine redefine_value singleton singleton_value method_missing
           subclass subclass_private private_value private_match protected_match remove_match frozen nil_entry]

if ARGV[0] == 'list'
  puts CASES
  exit
end
c = ARGV[0]

class Ent
  attr_reader :hash, :key, :value, :next
  attr_writer :next
  def initialize(hash, key, value, nxt); @hash = hash; @key = key; @value = value; @next = nxt; end
  def match(hash, key); @hash == hash && @key == key; end
end

class IdEnt
  attr_reader :hash, :key, :value, :next
  def initialize(hash, key, value, nxt); @hash = hash; @key = key; @value = value; @next = nxt; end
  def match(hash, key); @hash == hash && (@key.equal? key); end
end

class Dict
  def initialize; @buckets = Array.new(16); end
  def hash_of(key); key * 7; end
  def get_bucket(hash); @buckets[hash & 15]; end
  def put(key, value, cls = Ent)
    h = hash_of(key)
    @buckets[h & 15] = cls.new(h, key, value, @buckets[h & 15])
  end

  def at(key)
    hash = hash_of(key)
    e = get_bucket(hash)
    while e
      return e.value if e.match(hash, key)
      e = e.next
    end
    nil
  end
end

def answer
  yield
rescue NoMethodError, NameError => e
  [e.class, e.message[/\A[^(]*/].strip]
end

d = Dict.new
40.times { |k| d.put(k, k * 10) }
N = 4000

# warm, then the event at the half, then on; the sum of what was found, the count of nils, the first odd answer
def run(d, n, event)
  sum = 0
  nils = 0
  odd = nil
  n.times do |i|
    event.call if i == n / 2
    v = answer { d.at(i % 48) }
    if v.is_a?(Integer)
      sum += v
    elsif v.nil?
      nils += 1
    else
      odd ||= v
    end
  end
  [sum, nils, odd]
end

3000.times { |i| d.at(i % 40) }

case c
when 'plain'
  p run(d, N, -> {})
when 'class_change'
  # entries of another class with a match of its own (any key with the same hash)
  p run(d, N, -> {
    k2 = Class.new do
      attr_reader :value, :next
      def initialize(hash, key, value, nxt); @hash = hash; @value = value; @next = nxt; end
      def match(hash, key); @hash == hash; end
    end
    40.times { |k| d.put(k, -k, k2) }
  })
when 'two_classes'
  idd = Dict.new
  40.times { |k| idd.put(k, k, IdEnt) }
  3000.times { |i| idd.at(i % 40) }
  p run(d, N, -> { 20.times { |k| d.put(k, k + 1, IdEnt) } })
when 'redefine'
  p run(d, N, -> { Ent.class_eval { def match(hash, key); false; end } })
when 'redefine_value'
  p run(d, N, -> { Ent.class_eval { def value; -1; end } })
when 'singleton'
  # the entry found for key 5 answers false from now on: the lookup goes on down its chain
  p run(d, N, -> {
    e = d.get_bucket(d.hash_of(5))
    e = e.next until e.key == 5
    def e.match(hash, key); false; end
  })
when 'singleton_value'
  p run(d, N, -> {
    e = d.get_bucket(d.hash_of(7))
    e = e.next until e.key == 7
    def e.value; 7000; end
  })
when 'method_missing'
  ghost = Class.new do
    def initialize(nxt); @nxt = nxt; end
    def method_missing(name, *args)
      case name
      when :match then args[0] == 3 * 7 && args[1] == 3
      when :value then :ghost
      when :next then @nxt
      else super
      end
    end
    def respond_to_missing?(name, priv = false); [:match, :value, :next].include?(name) || super; end
  end
  p run(d, N, -> {
    h = d.hash_of(3)
    d.instance_variable_get(:@buckets)[h & 15] = ghost.new(d.get_bucket(h))
  })
when 'subclass'
  sub = Class.new(Ent)
  p run(d, N, -> { 40.times { |k| d.put(k, k * 100, sub) } })
when 'subclass_private'
  Object.const_set(:SubPrivate, Class.new(Ent) { private :value })
  p run(d, N, -> { d.put(9, 1, SubPrivate) })
when 'private_value'
  p run(d, N, -> { Ent.send(:private, :value) })
when 'private_match'
  p run(d, N, -> { Ent.send(:private, :match) })
when 'protected_match'
  p run(d, N, -> { Ent.send(:protected, :match) })
when 'remove_match'
  p run(d, N, -> { Ent.send(:remove_method, :match) })
when 'frozen'
  p run(d, N, -> { d.instance_variable_get(:@buckets).each { |e| while e; e.freeze; e = e.next; end } })
when 'nil_entry'
  p run(d, N, -> { d.instance_variable_get(:@buckets).map! { nil } })
end
