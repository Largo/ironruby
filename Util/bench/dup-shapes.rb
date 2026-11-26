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
H3 = { a: 1, b: 2, c: 3 }
X = {}
O = Object.new
HI = { a: 1 }.tap { |h| h.instance_variable_set(:@x, 1) }
A3 = [1, 2, 3]
S3 = "abc"
t("hash_dup", N) { |n| i = 0; c = 0; while i < n; c += H3.dup.size + i; i += 1; end; c }
t("replace", N) { |n| i = 0; c = 0; while i < n; c += X.replace(H3).size + i; i += 1; end; c }
t("hash_new", N) { |n| i = 0; c = 0; while i < n; c += Hash.new.size + i; i += 1; end; c }
t("lit3", N) { |n| i = 0; c = 0; while i < n; c += { a: 1, b: 2, c: i }.size; i += 1; end; c }
t("to_h", N) { |n| i = 0; c = 0; while i < n; c += H3.to_h.size + i; i += 1; end; c }
t("merge0", N) { |n| i = 0; c = 0; while i < n; c += H3.merge.size + i; i += 1; end; c }
t("obj_dup", N) { |n| i = 0; c = 0; while i < n; O.dup; c += i; i += 1; end; c }
t("arr_dup", N) { |n| i = 0; c = 0; while i < n; c += A3.dup.size + i; i += 1; end; c }
t("str_dup", N) { |n| i = 0; c = 0; while i < n; c += S3.dup.size + i; i += 1; end; c }
t("plain", N) { |n| i = 0; c = 0; while i < n; c += plain(i); i += 1; end; c }
t("size", N) { |n| i = 0; c = 0; while i < n; c += H3.size + i; i += 1; end; c }
t("clone", N) { |n| i = 0; c = 0; while i < n; c += H3.clone.size + i; i += 1; end; c }
t("dup_ivar", N) { |n| i = 0; c = 0; while i < n; c += HI.dup.size + i; i += 1; end; c }
