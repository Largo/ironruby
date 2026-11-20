# Shapes for Json's scanner core (Parser#read and the methods that call it) in the method JIT's
# typed bodies (Src/Ruby/Runtime/Jit, JitCompiler.Sink.cs), in the format Util/perf/ab.rb reads:
# `<shape> <ns> ns <checksum>` per shape; shape names as arguments, N from the environment.
#
#   ruby Util/perf/ab.rb -r 5 Util/bench/jit-scan-shapes.rb cruby=ruby "base=env IR_CONFIG=Release ../base/ir.sh" \
#        "new=env IR_CONFIG=Release ./ir.sh"
#
#   read        Json's `read' on a 10 KB String, called N times (the parser rewinds at the end)
#   scan        a scanner loop of `read_char'-, `read_digit'- and `skip_white_space'-like methods
#   read_swap   `read' where @input is replaced by another String (same contents) every 1000 reads
#   read_slow   `read' made hot on a plain String, then given a subclass of String for good: the typed
#               body's guard fails on every call (what the slow path costs against the generic body)
$stdout.sync = true
N = (ENV['N'] || 2_000_000).to_i

class ScParser
  attr_reader :line

  def initialize(input)
    @input = input
    @index = -1
    @line = 1
    @column = 0
    @current = nil
    read
  end

  def rewind
    @index = -1
    @current = nil
    read
  end

  def swap(input)
    @input = input
  end

  def read
    if "\n" == @current
      @line  += 1
      @column = 0
    end
    @index += 1
    if @index < @input.length
      @current = @input[@index]
    else
      @current = nil
    end
  end

  def read_char(ch)
    return false if @current != ch
    read
    true
  end

  def is_digit
    '0' == @current || '1' == @current || '2' == @current || '3' == @current ||
      '4' == @current || '5' == @current || '6' == @current ||
      '7' == @current || '8' == @current || '9' == @current
  end

  def read_digit
    return false unless is_digit
    read
    true
  end

  def is_white_space
    ' ' == @current || "\t" == @current || "\n" == @current || "\r" == @current
  end

  def skip_white_space
    read while is_white_space
  end

  def current = @current

  # one step of a scanner: what Json's parser does between values
  def step
    skip_white_space
    return 1 if read_char(',')
    return 2 if read_char('[')
    return 3 if read_char(']')
    n = 0
    n += 1 while read_digit
    return 4 + n if n > 0
    read
    0
  end
end

class ScString < String; end

SHAPES = %w[read scan read_swap read_slow]
TEXT = ("[12, 345,\n 6789, [0, 1]]\t" * 400)[0, 10_000]

def run_shape(name)
  input = TEXT.dup
  other = TEXT.dup
  sub = ScString.new(TEXT)
  r = 0
  best = nil
  6.times do
    parser = ScParser.new(input)
    if name == 'read_slow'
      # hot (and compiled) on a plain String first, then the guard fails on every call
      5000.times { parser.read; parser.rewind if parser.current.nil? }
      parser.swap(sub)
    end
    t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
    i = 0
    case name
    when 'read', 'read_slow'
      while i < N
        parser.read
        parser.rewind if parser.current.nil?
        i += 1
      end
    when 'read_swap'
      while i < N
        parser.read
        parser.rewind if parser.current.nil?
        parser.swap((i / 1000).odd? ? other : input) if i % 1000 == 0
        i += 1
      end
    when 'scan'
      while i < N
        r += parser.step
        parser.rewind if parser.current.nil?
        i += 1
      end
    end
    d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t
    r += parser.line
    best = d if best.nil? || d < best
  end
  puts format('%s %.1f ns %s', name, best * 1e9 / N, r)
end

(ARGV.empty? ? SHAPES : ARGV).each { |s| run_shape(s) }
