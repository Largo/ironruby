# Differential cases for a call on a local the method JIT has no sample of (JitCompiler.UnsampledCallees,
# IR_JIT_UNSAMPLED): `e = get_bucket(hash)' before a `while e' that calls `e.match(hash, key)' and reads `e.value' and
# `e.next', as som.rb's Dictionary#at (AWFY Havlak). The call is inlined for the classes that define an inlinable
# `match' themselves, each under a check of its exact class, and the reads go through a site that checks the class.
# Each case warms `at' past the JIT's threshold, then does what must change the answer half way through a long run of
# lookups, and prints what the lookups answered.
#
# One case per process, since one case's event would change what the next compiles:
#   for c in $(ruby Util/unsampled-matrix.rb list); do echo "== $c"; ruby Util/unsampled-matrix.rb $c 2>&1; done > cruby.txt
#   (the same with IR_CONFIG=Release ./ir.sh, with -X:NoJIT -X:NoOSR, with -X:NoJITCalls, with IR_JIT_UNSAMPLED=0 and with
#   IR_JIT_NESTED=0). The nested_ cases put the classes in a module (JitStub.ClassesDefining); the write_ cases write an
#   attribute of the local in a loop (declined by the effect rule: a read after the write can deopt), the pop_ cases in
#   AWFY Towers' straight-line pop_disk_from (compiled: the write is the last effect).
# IR_JIT_VERBOSE=1 shows `inlined Ent#match into at' and `specialized Dict/at': the path is reached.

CASES = %w[plain class_change two_classes redefine redefine_value singleton singleton_value method_missing
           subclass subclass_private private_value private_match protected_match remove_match frozen nil_entry
           nested nested_redefine nested_other nested_singleton
           write write_frozen write_private write_subclass_private write_missing write_thread
           pop pop_frozen pop_private pop_subclass_private pop_missing pop_thread]

if ARGV[0] == 'list'
  puts CASES
  exit
end
c = ARGV[0]

class Ent
  attr_reader :hash, :key, :value, :next
  attr_writer :next
  attr_accessor :mark
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

  # an attribute write on the same kind of local (split_bucket's `lo_tail.next = current')
  def touch(key, m)
    hash = hash_of(key)
    e = get_bucket(hash)
    while e
      e.mark = m if e.match(hash, key)
      e = e.next
    end
    m
  end
end

# AWFY Towers' pop_disk_from and push_disk: a disk read from an Array, an element write, then an attribute write on
# the disk - a local nothing samples, and a straight-line body the JIT compiles (the write is its last effect)
class Disk
  attr_accessor :next
  attr_reader :size
  def initialize(size); @size = size; @next = nil; end
end

class Piles
  def initialize; @piles = Array.new(3); end
  def pop(pile)
    top = @piles[pile]
    raise 'empty pile' unless top
    @piles[pile] = top.next
    top.next = nil
    top
  end
  def push(disk, pile)
    disk.next = @piles[pile]
    @piles[pile] = disk
  end
  def sizes(pile); a = []; d = @piles[pile]; while d; a << d.size; d = d.next; end; a; end
end

# moves `n' disks one at a time from pile i % 3 to pile (i + 2) % 3, so that every pile's top moves in turn, the event
# at the half; then the sum of the sizes moved, each pile's sizes, and the first odd answer
def run_pop(pl, n, event)
  odd = nil
  moved = 0
  n.times do |i|
    event.call if i == n / 2
    v = answer { d = pl.pop(i % 3); pl.push(d, (i + 2) % 3); d.size }
    v.is_a?(Integer) ? moved += v : odd ||= v
  end
  [moved, pl.sizes(0), pl.sizes(1), pl.sizes(2), odd]
end

# the same in a module: classes nested in one are candidates too (JitStub.ClassesDefining)
module Ns
  class Ent
    attr_reader :hash, :key, :value, :next
    def initialize(hash, key, value, nxt); @hash = hash; @key = key; @value = value; @next = nxt; end
    def match(hash, key); @hash == hash && @key == key; end
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
end

def answer
  yield
rescue NoMethodError, NameError => e
  [e.class, e.message[/\A[^(]*/].strip]
rescue FrozenError => e
  [e.class, e.message[/\A[^:]*/]]
rescue RuntimeError => e
  [e.class, e.message]
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
nd = Ns::Dict.new
40.times { |k| nd.put(k, k * 10) }
3000.times { |i| nd.at(i % 40) }
3000.times { |i| d.touch(i % 40, 0) }
pl = Piles.new
[5, 4, 3, 2, 1].each { |sz| pl.push(Disk.new(sz), 0) }
[15, 14, 13, 12, 11].each { |sz| pl.push(Disk.new(sz), 1) }
[25, 24, 23, 22, 21].each { |sz| pl.push(Disk.new(sz), 2) }
3000.times { |i| pl.push(pl.pop(i % 3), (i + 2) % 3) }

# writes: touch every key, with the event at the half; what the entries' marks are then, or the first error
def run_touch(d, n, event)
  odd = nil
  n.times do |i|
    event.call if i == n / 2
    v = answer { d.touch(i % 40, i) }
    odd ||= v unless v.is_a?(Integer)
  end
  marks = (0...40).map { |k| e = d.get_bucket(d.hash_of(k)); e = e.next until e.nil? || e.key == k; e && e.mark }
  [marks.compact.sum, marks.count(nil), odd]
end

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
when 'nested'
  p run(nd, N, -> {})
when 'nested_redefine'
  p run(nd, N, -> { Ns::Ent.class_eval { def match(hash, key); @key == key + 1; end } })
when 'nested_other'
  # a second nested class with a match of its own, at the head of each chain
  p run(nd, N, -> {
    Ns.const_set(:Other, Class.new do
      attr_reader :value, :next
      def initialize(hash, key, value, nxt); @hash = hash; @value = value; @next = nxt; end
      def match(hash, key); @hash == hash; end
    end)
    40.times { |k| nd.put(k, -k, Ns::Other) }
  })
when 'nested_singleton'
  p run(nd, N, -> {
    e = nd.get_bucket(nd.hash_of(5))
    e = e.next until e.key == 5
    def e.match(hash, key); false; end
  })
when 'write'
  p run_touch(d, N, -> {})
when 'write_frozen'
  p run_touch(d, N, -> { e = d.get_bucket(d.hash_of(6)); e = e.next until e.key == 6; e.freeze })
when 'write_private'
  p run_touch(d, N, -> { Ent.send(:private, :mark=) })
when 'write_subclass_private'
  Object.const_set(:SubPrivateWriter, Class.new(Ent) { private :mark= })
  p run_touch(d, N, -> { d.put(11, 1, SubPrivateWriter) })
when 'write_missing'
  Object.const_set(:GhostWriter, Class.new do
    attr_reader :hash, :key, :value, :next
    def initialize(hash, key, value, nxt); @hash = hash; @key = key; @value = value; @next = nxt; @marks = []; end
    def match(hash, key); @hash == hash && @key == key; end
    def mark; @marks.size; end
    def method_missing(name, *args)
      name == :mark= ? (@marks << args[0]; args[0]) : super
    end
    def respond_to_missing?(name, priv = false); name == :mark= || super; end
  end)
  p run_touch(d, N, -> { d.put(12, 1, GhostWriter) })
when 'pop'
  p run_pop(pl, N, -> {})
when 'pop_frozen'
  p run_pop(pl, N, -> { pl.instance_variable_get(:@piles).each { |top| top.freeze if top } })
when 'pop_private'
  p run_pop(pl, N, -> { Disk.send(:private, :next=) })
when 'pop_subclass_private'
  Object.const_set(:PrivateDisk, Class.new(Disk) { private :next= })
  p run_pop(pl, N, -> {
    pd = PrivateDisk.new(99)
    piles = pl.instance_variable_get(:@piles)
    pd.instance_variable_set(:@next, piles[2])
    piles[2] = pd
  })
when 'pop_missing'
  Object.const_set(:GhostDisk, Class.new do
    attr_reader :size
    def initialize(size); @size = size; @link = nil; end
    def method_missing(name, *args)
      case name
      when :next then @link
      when :next= then @link = args[0]
      else super
      end
    end
    def respond_to_missing?(name, priv = false); name == :next || name == :next= || super; end
  end)
  p run_pop(pl, N, -> { pl.push(GhostDisk.new(77), 0) })
when 'pop_thread'
  # another thread reads each pile's top disk's next while the moves go on: it must only see nil or a Disk
  stop_pt = false
  bad_pt = 0
  piles_pt = pl.instance_variable_get(:@piles)
  reader_pt = Thread.new do
    until stop_pt
      piles_pt.each { |top_pt| n_pt = top_pt && top_pt.next; bad_pt += 1 unless n_pt.nil? || n_pt.is_a?(Disk) }
    end
  end
  r_pt = run_pop(pl, N, -> {})
  stop_pt = true
  reader_pt.join
  p [r_pt, bad_pt]
when 'write_thread'
  # another thread reads the mark of key 3 while the writes go on; it must only ever see a value some touch wrote
  e3 = d.get_bucket(d.hash_of(3))
  e3 = e3.next until e3.key == 3
  stop_wt = false
  bad_wt = 0
  reader_wt = Thread.new do
    until stop_wt
      m_wt = e3.mark
      bad_wt += 1 unless m_wt.is_a?(Integer) && m_wt >= 0 && m_wt < N && (m_wt == 0 || m_wt % 40 == 3)
    end
  end
  r_wt = run_touch(d, N, -> {})
  stop_wt = true
  reader_wt.join
  p [r_wt, bad_wt]
end
