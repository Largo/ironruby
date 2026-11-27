# Gap 12 of Util/perf/LEDGER.md: one `recv != other' site whose receivers are instances of k plain classes in
# turn (k = 1, 2, 3, 4, 8, 60), each k a site of its own, in a method's `while' loop; and the same with `==',
# and with a method of the classes' own (`recv.m'). `<shape> <ns> ns <checksum>' per line, for Util/perf/ab.rb;
# `ruby twospeed-shapes.rb NAME...' runs only the shapes named; N is the calls a section makes (ab.rb -n);
# INSTR=1 (ab.rb -i, or Util/perf/callgrind/count.sh) adds instructions. Run it in many processes: the point is
# whether a process lands in one speed or another.
INSTR = ENV['INSTR'] && (require_relative '../perf/instructions'; Instructions.open || warn('no instruction counter (perf_event_open)'))
def cpu
  Process.clock_gettime(Process::CLOCK_THREAD_CPUTIME_ID)
rescue SystemCallError, NameError
  Process.clock_gettime(Process::CLOCK_MONOTONIC)
end
def t(name, n = 1_000_000)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  n = ENV['N'].to_i if ENV['N']
  best = 1e9; r = nil; fewest = nil; counted = !!INSTR
  7.times do
    c0 = INSTR && INSTR.read
    t0 = cpu
    r = yield n
    d = cpu - t0
    c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1
      fewest = c1 - c0 if fewest.nil? || c1 - c0 < fewest
    else
      counted = false
    end
  end
  printf "%-14s %8.1f ns %s%s\n", name, best * 1e9 / n, r, counted ? format('  %.1f instr', fewest.to_f / n) : ''
end
CLASSES = (1..60).map { |i| Class.new { define_method(:m) { i } } }
OTHER = Object.new
# a method per shape, so that each has a site of its own
[1, 2, 3, 4, 8, 60].each do |k|
  objs = CLASSES.first(k).map(&:new)
  Object.class_eval <<~RUBY
    def neq#{k}(objs, n)
      i = 0; c = 0
      while i < n
        c += 1 if objs[i % #{k}] != OTHER
        i += 1
      end
      c
    end
    def eq#{k}(objs, n)
      i = 0; c = 0
      while i < n
        c += 1 if objs[i % #{k}] == OTHER
        i += 1
      end
      c
    end
    def call#{k}(objs, n)
      i = 0; c = 0
      while i < n
        c += objs[i % #{k}].m
        i += 1
      end
      c
    end
  RUBY
  t("neq#{k}") { |n| send(:"neq#{k}", objs, n) }
  t("eq#{k}") { |n| send(:"eq#{k}", objs, n) }
  t("call#{k}") { |n| send(:"call#{k}", objs, n) }
end
