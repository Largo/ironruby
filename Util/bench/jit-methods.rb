# Benchmarks for -X:JITMethods (Src/Ruby/Runtime/Jit/JitMethodEmitter.cs): methods the method
# JIT specializes. Each runs its timed section several times in one process and prints the
# value it computed (to check against CRuby's), the seconds the first run took - which has
# the method's compilation in it - and the seconds the fastest later run took. The seconds are
# CPU time of the main thread on Linux (see `now' below).
#
#   ruby Util/bench/jit-methods.rb <name> [runs]
#   ./ir.sh -X:JITMethods Util/bench/jit-methods.rb <name>
#   ./ir.sh Util/bench/jit-methods.rb --list
#
# Util/bench/jit-methods-run.rb runs them under each mode of the switch and makes the tables.
#
# The first group calls itself, which is the call the switch turns from a delegate invocation
# into a `call'. The second group does not: its typed bodies are part of their entries, so only
# -X:JITMethods=...,entry touches them.
#
# The methods are defined once, up here: a `def' in a timed section would redefine its method
# on every run, and each run would then compile it again.

BENCH = {}
def bench(name, &b) = BENCH[name] = b

# ---- methods that call themselves ------------------------------------------------------

def fib(k) = k < 2 ? k : fib(k - 1) + fib(k - 2)

bench("fib35") { fib(35) }

def ack(mm, k)
  if mm == 0 then k + 1
  elsif k == 0 then ack(mm - 1, 1)
  else ack(mm - 1, ack(mm, k - 1))
  end
end

bench("ackermann") do
  s = 0
  48.times { s += ack(3, 7) }
  s
end

def tak(x, y, z) = y < x ? tak(tak(x - 1, y, z), tak(y - 1, z, x), tak(z - 1, x, y)) : z

bench("tak") do
  s = 0
  16.times { s += tak(24, 16, 8) }
  s
end

# a Float accumulator down the recursion
def horner(x, k, acc) = k == 0 ? acc : horner(x, k - 1, acc * x + k)

bench("float_rec") do
  s = 0.0; i = 0
  while i < 400_000
    s += horner(0.5, 40, 1.0)
    i += 1
  end
  s
end

# the typed body holds live objects next to the self-call: the Array class its parameter is
# checked against, on every call
def asum(a, i) = i == a.size ? 0 : a[i] + asum(a, i + 1)

bench("array_rec") do
  a = (1..200).to_a
  s = 0; i = 0
  while i < 40_000
    s += asum(a, 0)
    i += 1
  end
  s
end

# a Symbol and a String, compared with on every call
def weigh(k, x)
  return 0 if k == 0
  v = case x when :a then 1 when "b" then 2 else 3 end
  v + weigh(k - 1, x)
end

bench("case_rec") do
  s = 0; i = 0
  while i < 40_000
    s += weigh(100, :a) + weigh(100, "b")
    i += 1
  end
  s
end

# ---- methods that do not ---------------------------------------------------------------

def norm2(x, y) = x * x + y * y

bench("call_arith") do
  s = 0; i = 0
  while i < 6_000_000
    s += norm2(i & 1023, 7)
    i += 1
  end
  s
end

class Vec
  def initialize(x, y); @x = x; @y = y; end
  def pick(k) = k == 0 ? @x : @y
  def bump(v); @x = v; end
end

bench("call_ivar_get") do
  v = Vec.new(3, 4)
  s = 0; i = 0
  while i < 6_000_000
    s += v.pick(i & 1)
    i += 1
  end
  s
end

bench("call_ivar_set") do
  v = Vec.new(3, 4)
  i = 0
  while i < 6_000_000
    v.bump(i)
    i += 1
  end
  v.pick(0)
end

name = ARGV[0]
if name == "--list"
  puts BENCH.keys
  exit
end
unless BENCH.key?(name)
  warn "unknown bench: #{name}"
  exit 1
end
# The clock: the nanoseconds this thread has spent on a CPU, where Linux tells (the timed
# sections are single-threaded, and a busy machine then stretches the wall time and not the
# measurement), else the wall clock. BENCH_CLOCK=wall asks for the wall clock. The kernel
# counts that time in scheduler ticks (4 ms here), which is why no timed section is short.
SCHEDSTAT = "/proc/thread-self/schedstat"
CPU_CLOCK = ENV["BENCH_CLOCK"] != "wall" && File.readable?(SCHEDSTAT)
def now = CPU_CLOCK ? File.read(SCHEDSTAT).to_i / 1e9 : Process.clock_gettime(Process::CLOCK_MONOTONIC)

runs = (ARGV[1] || 5).to_i
values = []
times = []
runs.times do
  t = now
  values << BENCH[name].call
  times << now - t
end
# every run computes the same thing
puts values.uniq.size == 1 ? values[0].inspect : "runs differ: #{values.inspect}"
printf("%.6f\n", times[0])
printf("%.6f\n", (times[1..] || []).min || times[0])
