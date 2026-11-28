# What a prelude helper call costs (LEDGER gap 17): a method that calls a C# method on self through a
# call site, against the same method answering a constant. Prints ns per call (process CPU).
#
#   ./ir.sh Util/bench/helper-call-cost.rb
#
# And which of the prelude's `__x__` calls a "primitive" could replace at all - the C# ones - against
# the ones that call a Ruby method the prelude defines itself:
#
#   ruby Util/bench/helper-call-cost.rb --census
if ARGV[0] == "--census"
  root = File.expand_path("../..", __dir__)
  src = File.read(File.join(root, "Src/StdLib/ironruby/ruby4.rb"), encoding: "UTF-8")
  calls = Hash.new(0)
  src.scan(/(?<![\w@$:])(__[a-z0-9_]+__[?!]?)/) { calls[$1] += 1 }
  ruby = Dir[File.join(root, "Src/StdLib/ironruby/**/*.rb")].flat_map { |f| File.read(f, encoding: "UTF-8").scan(/def\s+(?:self\.)?(__[a-z0-9_]+__[?!]?)/).flatten }.uniq
  cs = Dir[File.join(root, "Src/{Libraries,Ruby}/**/*.cs")].flat_map { |f| File.read(f, encoding: "UTF-8").scan(/RubyMethod\("(__[a-z0-9_]+__[?!]?)"/).flatten }.uniq
  ruby_only = calls.select { |n, _| ruby.include?(n) }
  cs_only = calls.select { |n, _| cs.include?(n) && !ruby.include?(n) && !%w[__send__ __id__ __method__ __dir__ __callee__].include?(n) }
  printf "ruby4.rb: %d `__x__` calls of %d names\n", calls.values.sum, calls.size
  printf "  to a C#-only helper: %d calls of %d names (a primitive could take these)\n", cs_only.values.sum, cs_only.size
  printf "  to a Ruby method of the prelude's: %d calls of %d names\n", ruby_only.values.sum, ruby_only.size
  exit
end
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
class Array
  def __t_const = true
  def __t_size = size
  def __t_frozen = frozen?
  def __t_plain = __plain_list__?
end
def m(name, n)
  yield 200_000
  best = 1e9
  5.times { t0 = cpu; yield n; d = cpu - t0; best = d if d < best }
  printf "%-22s %6.1f ns\n", name, best * 1e9 / n
end
A = [1, 2]
N = 3_000_000
m("method, constant", N)        { |k| i = 0; while i < k; A.__t_const; i += 1; end }
m("method, size (C#)", N)       { |k| i = 0; while i < k; A.__t_size; i += 1; end }
m("method, frozen? (C#)", N)    { |k| i = 0; while i < k; A.__t_frozen; i += 1; end }
m("method, __plain_list__?", N) { |k| i = 0; while i < k; A.__t_plain; i += 1; end }
m("Thread.__fiber_current__", N) { |k| i = 0; while i < k; Thread.__fiber_current__; i += 1; end }
