# Shapes for core operations in the method JIT's typed bodies (Src/Ruby/Runtime/Jit), in the
# format Util/perf/ab.rb reads: `<shape> <ns> ns <checksum>` per shape; shape names as arguments,
# N from the environment. Each is a small method that the JIT declined at one core operation
# before it knew it (the AWFY method it comes from in the comment).
#
#   ruby Util/perf/ab.rb -r 5 Util/bench/jit-core-shapes.rb cruby=ruby "base=env IR_CONFIG=Release ../base/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
$stdout.sync = true
N = (ENV['N'] || 3_000_000).to_i

class CsTable
  def initialize; @hash_table = Array.new(32, 0); end
  # Json's HashIndexTable#string_hash and #hash_slot_for: String#size, through a call on self
  def string_hash(s) = s.size * 1_402_589
  def hash_slot_for(element) = string_hash(element) & @hash_table.length - 1
end

class CsParser
  def initialize(input); @input = input; @index = 0; end
  # Json's Parser#read, the bound test: String#length of an instance variable
  def more = @index < @input.length
end

SHAPES = %w[str_size str_length]

def run_shape(name)
  table = CsTable.new
  parser = CsParser.new('{"a": [1, 2, 3], "b": "xyz"}')
  key = 'abcdef'
  r = 0
  best = nil
  6.times do
    t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    i = 0
    case name
    when 'str_size'   then while i < N; r += table.hash_slot_for(key); i += 1; end
    when 'str_length' then while i < N; r += 1 if parser.more; i += 1; end
    end
    d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    best = d if best.nil? || d < best
  end
  puts format('%s %.1f ns %s', name, best * 1e9 / N, r)
end

(ARGV.empty? ? SHAPES : ARGV).each { |s| run_shape(s) }
