# Differential matrix for the method JIT (-X:JIT, Src/Ruby/Runtime/Jit/): a String literal in a
# body the JIT compiles (JitCompiler.EmitStringLiteral). A literal is the object the generic body
# makes: in a file that says nothing, a new chilled String on every evaluation; under
# `frozen_string_literal: true' one frozen String, the same object every time and the one `-"lit"'
# answers; with `false', a new plain String. Run under CRuby and under ./ir.sh - with the JIT on
# and off - and diff:
#
#   ruby Util/jit-strlit-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/jit-strlit-matrix.rb > /tmp/jit.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/jit-strlit-matrix.rb > /tmp/nojit.txt
#   diff /tmp/mri.txt /tmp/jit.txt && diff /tmp/mri.txt /tmp/nojit.txt
#
# Every method is called well past the JIT's hotness threshold and then probed: each row prints
# what the last calls answered, whether two calls answered the same object, the String's
# encoding, frozen state and validity, what mutating it does (a chilled String warns), and what a
# deopt after the literal leaves. Which methods were specialized is not part of the output:
#
#   IR_JIT_VERBOSE=1 ./ir.sh Util/jit-strlit-matrix.rb 2>&1 >/dev/null | grep -a 'jit\]'
#
# The methods named sl_* are shapes the JIT is expected to specialize.

def t(label)
  print label.ljust(56), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts e.class
  end
end

HOT = 120

def warm
  i = 0
  while i < HOT
    yield i
    i += 1
  end
end

# A chilled String's first mutation warns (category :deprecated); the warnings are collected here.
$warned = []
module Warning
  def self.warn(msg, category: nil, **)
    $warned << [category, msg.sub(/\A.*?warning: /, '').chomp]
  end
end
Warning[:deprecated] = true

# What a String answers about itself.
def desc(s)
  return s unless s.is_a?(String)
  [s, s.encoding.to_s, s.frozen?, s.valid_encoding?, s.bytesize]
end

# Calls the method HOT times, keeps the last two answers, and says what they are: the last one
# described, whether the two are one object, and what `<<' on the last one does.
def probe(obj, name, *args)
  a = b = nil
  warm { |_| a, b = b, obj.send(name, *args) }
  $warned.clear
  mutated = begin
    b << '!' if b.is_a?(String)
    b
  rescue => e
    e.class
  end
  [desc(a), a.equal?(b), mutated, $warned.dup]
end

puts '--- a literal in a file that says nothing (chilled) ---'

class SlChilled
  def initialize = (@s = nil; @h = { 'k' => 1 })
  def sl_ascii = 'abc'
  def sl_empty = ''
  def sl_utf8 = 'hé あ'
  def sl_astral = "\u{1F600}"
  def sl_escaped = "a\tb\n"
  def sl_invalid = "a\xffb"
  def sl_binary_escape = "\xE3\x81\x82"
  def sl_store = (@s = 'stored'; @s)
  def sl_store_then = (@s = 'x'; 1)
  def sl_key = @h['k']
  def sl_cond(x) = x > 0 ? 'pos' : 'neg'
  def sl_case(x)
    case x
    when 1 then 'one'
    when 2 then 'two'
    else 'many'
    end
  end
  def sl_local(x)
    s = 'loc'
    x + 1
    s
  end
  def sl_arg = ident('passed')
  def ident(v) = v
  def sl_array = ['a', 'b']
  def sl_or_memo = (@m ||= 'memo')
  def sl_compare(x) = x == 'abc'
  attr_reader :s
end

o = SlChilled.new
t('ascii') { probe(o, :sl_ascii) }
t('empty') { probe(o, :sl_empty) }
t('utf8') { probe(o, :sl_utf8) }
t('astral') { probe(o, :sl_astral) }
t('escaped') { probe(o, :sl_escaped) }
t('invalid byte') { probe(o, :sl_invalid) }
t('binary escape') { probe(o, :sl_binary_escape) }
t('stored in an ivar') { probe(o, :sl_store) }
t('stored, then something else answered') { [probe(o, :sl_store_then), desc(o.s), o.sl_store_then && o.s.equal?(o.s)] }
t('a Hash key') { probe(o, :sl_key) }
t('?: on an Integer') { [probe(o, :sl_cond, 1), probe(o, :sl_cond, -1)] }
t('case/when') { [probe(o, :sl_case, 1), probe(o, :sl_case, 2), probe(o, :sl_case, 3)] }
t('a local, then arithmetic') { probe(o, :sl_local, 5) }
t('a local, then overflow') { probe(o, :sl_local, 2**62) }
t('a local, then a String argument') { probe(o, :sl_local, 'x') }
t('a local, deopt in the middle') do
  r = []
  warm { |i| r << o.sl_local(i == 100 ? 2**64 : i) }
  [desc(r[99]), desc(r[100]), r[99].equal?(r[100]), r.map(&:object_id).uniq.size]
end
t('passed to a call on self') { probe(o, :sl_arg) }
t('in an Array literal') { probe(o, :sl_array) }
t('@m ||= literal') { probe(SlChilled.new, :sl_or_memo) }
t('== literal') { [probe(o, :sl_compare, 'abc'), probe(o, :sl_compare, 'x'), probe(o, :sl_compare, 1)] }
t('distinct objects over a hot run') { r = []; warm { |_| r << o.sl_ascii }; r.map(&:object_id).uniq.size }

class SlChilled
  def sl_ascii = 'redefined'
end
t('redefined') { probe(o, :sl_ascii) }

puts '--- a literal under frozen_string_literal: true ---'

eval(<<~'RUBY', nil, 'sl_frozen.rb', 1)
  # frozen_string_literal: true
  class SlFrozen
    def sl_ascii = 'abc'
    def sl_utf8 = 'hé あ'
    def sl_invalid = "a\xffb"
    def sl_store = (@s = 'stored'; @s)
    def sl_local(x)
      s = 'loc'
      x + 1
      s
    end
    def sl_interned = 'interned-lit'
  end
RUBY
f = SlFrozen.new
t('frozen ascii') { probe(f, :sl_ascii) }
t('frozen utf8') { probe(f, :sl_utf8) }
t('frozen invalid byte') { probe(f, :sl_invalid) }
t('frozen stored') { probe(f, :sl_store) }
t('frozen local, overflow') { probe(f, :sl_local, 2**62) }
t('frozen, the same object as -"lit"') { probe(f, :sl_interned); f.sl_interned.equal?(-'interned-lit'.dup) }

puts '--- a literal under frozen_string_literal: false ---'

eval(<<~'RUBY', nil, 'sl_mutable.rb', 1)
  # frozen_string_literal: false
  class SlMutable
    def sl_ascii = 'abc'
    def sl_local(x)
      s = 'loc'
      x + 1
      s
    end
  end
RUBY
m = SlMutable.new
t('mutable ascii') { probe(m, :sl_ascii) }
t('mutable local, overflow') { probe(m, :sl_local, 2**62) }

puts '--- a literal in a while loop ---'

class SlLoop
  def sl_loop(n)
    a = Array.new(n)
    i = 0
    while i < n
      a[i] = 'z'
      i += 1
    end
    a
  end
end
l = SlLoop.new
t('a loop of literals') { r = nil; warm { |_| r = l.sl_loop(5) }; [r, r.map(&:object_id).uniq.size, r.map(&:frozen?).uniq] }
t('a long loop of literals') { r = l.sl_loop(3000); [r.size, r.map(&:object_id).uniq.size, r.last] }
