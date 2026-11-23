# What the method JIT and the light frames make of every method in a set of files, found
# without running any of them: each file is parsed and each `def' goes through the decisions
# its first call would take it through (IronRuby.jit_census, Src/Ruby/Runtime/Jit/JitCensus.cs).
#
#   ./ir.sh Util/perf/census.rb [options] FILE|DIR...
#   ./ir.sh Util/perf/census.rb ../ironruby-perf/awfy/benchmarks/Ruby
#   ./ir.sh Util/perf/census.rb -o before.tsv Src/StdLib/ironruby/ruby4.rb      # on master
#   ./ir.sh Util/perf/census.rb -d before.tsv Src/StdLib/ironruby/ruby4.rb      # on the branch
#
#     -o FILE   save the verdicts, one method per line, tab-separated
#     -d FILE   compare with a saved census: the methods whose verdict changed. Files below the
#               current directory are saved by their path from it, so a census saved in one
#               checkout compares with one taken from the same directory of another
#     -c LOG    check the census against a real run of the same files, LOG being the stderr of
#               IR_JIT_VERBOSE=1 IR_LIGHT_FRAMES=why ./ir.sh ...: only the methods that run called
#     -u LOG    the census of the methods a run used: everything above and below (the counts, the
#               tables, -l, -o, -d) only for the methods the run in LOG (as for -c) called, and one
#               table more, from the run: of those methods, how many it specialized, declined (with
#               the reasons) and rejected at the screen. Util/bench/app-shapes.rb is such a run:
#                 IR_JIT_VERBOSE=1 IR_LIGHT_FRAMES=why ./ir.sh Util/bench/app-shapes.rb 2> trace.log >/dev/null
#                 ./ir.sh Util/perf/census.rb -u trace.log Src/StdLib Util/bench/app-shapes.rb
#     -n N      rows per table (default 12)
#     -l        list every method (with -a: and everything the screen refuses in it)
#     -a        with -l, every refusal of the screen, not only the first
#
# A method is, to the JIT: `never' (its parameters or the static screen refuse it, whatever it
# is called with), `compiles' (a typed body exists for parameters that are all Integers, all
# Floats or all objects, the receiver unknown) or `maybe' (it passes the screen, and the profile
# decides: the receiver's class and instance variables, constants, callees). The tables give
# the reasons, most frequent first - the top line is what to teach the JIT or the frames next -
# and -d is the effect of having done it. It takes seconds and does not depend on the machine's
# load, but it counts methods, not calls: a refusal in a method nothing calls costs nothing (-u counts the
# methods a run called).
# It does not see the class a method ends up in (JitCensus says what it assumes); -c says how
# far that is from a run.
abort 'census.rb runs on IronRuby: ./ir.sh Util/perf/census.rb ...' unless defined?(IronRuby) && IronRuby.respond_to?(:jit_census)

FIELDS = %i[file line lines module name singleton light light_why blocks light_blocks jit jit_stage jit_kind jit_why jit_types jit_all]
save = diff = check = used = nil
rows = 12
list = false
all_refusals = false
paths = []
args = ARGV.dup
until args.empty?
  case (a = args.shift)
  when '-o' then save = args.shift
  when '-d' then diff = args.shift
  when '-c' then check = args.shift
  when '-u' then used = args.shift
  when '-n' then rows = Integer(args.shift)
  when '-l' then list = true
  when '-a' then all_refusals = true
  when /\A-/ then abort "census.rb: unknown option #{a}"
  else paths << a
  end
end
abort 'usage: ./ir.sh Util/perf/census.rb [-o FILE] [-d FILE] [-c LOG] [-u LOG] [-n N] [-l] FILE|DIR...' if paths.empty?

files = paths.flat_map { |p| File.directory?(p) ? Dir.glob(File.join(p, '**', '*.rb')).sort : [p] }.map { |p| File.expand_path(p) }.uniq
started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
result = IronRuby.jit_census(files)
elapsed = Process.clock_gettime(Process::CLOCK_MONOTONIC) - started
failed, methods = result.partition { |h| h.key?(:error) }

# The run's own account, one method at a time as each is first called: the body's transformation prints a [light]
# line with the file and line, and the screen's refusal, if it refuses, is the [jit] line that follows it. A typed
# body is named by class and method, not by line; a decline's reason is on its own line, or, when the compiler bailed
# out, on the `rejected NAME arity N' line just before it.
# A method of the trace and of the census is known by file, line and name: two methods can share a line
# (`def hot; 1; end; def cold; 2; end'), and the name tells them apart. The runtime prints the path as it was
# given (a relative main file or load path), the census an expanded one: a relative one is taken from the current
# directory, so run the census from the directory the traced run started in.
key = ->(file, line, name) { "#{File.expand_path(file)}:#{line}:#{name}" }
mkey = ->(m) { key.(m[:file], m[:line], m[:name]) }

read_trace = lambda do |log|
  run = {}
  last = nil
  specialized, declined = Hash.new(0), Hash.new(0)
  reasons = Hash.new { |h, k| h[k] = [] }
  bailed = {}
  File.foreach(log, chomp: true) do |line|
    case line
    when /\A\[light\] (light|needs scope|why not): (.+) \((.+):(\d+)\)(?:: (.*))?\z/
      at = key.($3, $4, $2)
      last = run[at] ||= { name: $2, light: false, screen: nil }
      last[:light] = true if $1 == 'light'
      last[:light_why] = $5 if $5
    when %r{\A\[jit\] screen rejected (.+)/([^/ ]+|/): (.*)\z}
      last[:screen] = $3 if last && last[:name] == $2
    when /\A\[jit\] rejected (\S+) arity (\d+): (.*)\z/ then bailed[[$1, $2]] = $3
    when %r{\A\[jit\] specialized (.+)/([^/ ]+|/) arity} then specialized[[$1, $2]] += 1
    when %r{\A\[jit\] declined (.+)/([^/ ]+|/) arity (\d+) \(passed the pre-screen\): (.*)\z}
      declined[[$1, $2]] += 1
      reasons[[$1, $2]] << ($4.include?("see the `rejected' line") ? bailed.delete([$2, $3]) || $4 : $4)
    when %r{\A\[jit\] declined (.+)/([^/ ]+|/) arity} then declined[[$1, $2]] += 1
    end
  end
  [run, specialized, declined, reasons]
end

if used
  trace = read_trace.(used)
  of = methods.size
  # matched as -c matches: by file, line and name
  methods = methods.select { |m| trace[0].key?(mkey.(m)) }
end

label = ->(m) { "#{m[:module]}#{m[:singleton] ? '.' : '#'}#{m[:name]}" }
# A file below the current directory is known by its path from there, on the screen and in a saved census: a census
# saved in one checkout is then compared with one taken in another (a worktree) of the files at the same places.
rel = ->(f) { f.start_with?(Dir.pwd + '/') ? f[Dir.pwd.size + 1..] : f }
where = ->(m) { "#{rel.(m[:file])}:#{m[:line]}" }
pct = ->(n, of) { of.zero? ? '-' : format('%5.1f%%', 100.0 * n / of) }
jit_text = ->(m) { m[:jit] == 'compiles' ? "compiles#{m[:jit_types].to_s.empty? ? '' : " (#{m[:jit_types]})"}" : "#{m[:jit]} (#{m[:jit_why]})" }
light_text = ->(m) { m[:light] ? 'light' : "scope (#{m[:light_why]})" }
# "MethodCall at line 12: GlobalVariable" is the statement (MethodCall), where it is, and the construct inside it that
# IsScopeFree refused (GlobalVariable): the first table counts statements, the second constructs, whatever statement
# holds them - the statement alone did not say what to teach the frames next
light_kind = ->(m) { m[:light_why].to_s.sub(/ at line \d+(: .*)?\z/m, '') }
light_construct = ->(m) { m[:light_why].to_s[/ at line \d+: (.*)\z/m, 1] }
jit_detail = lambda do |m|
  why, kind = m[:jit_why].to_s, m[:jit_kind].to_s
  why.start_with?(kind) ? why[kind.size..].sub(/\A[: ]+/, '') : why
end

# a table of reasons, most frequent first, each with its most frequent details
table = lambda do |title, group, kind, detail = nil|
  next if group.empty?
  puts '', title
  group.group_by(&kind).sort_by { |k, ms| [-ms.size, k] }.first(rows).each do |k, ms|
    details = detail ? ms.map(&detail).reject(&:empty?).tally.sort_by { |d, n| [-n, d] }.first(6).map { |d, n| "#{d} #{n}" }.join(', ') : ''
    puts format('  %5d  %-34s %s', ms.size, k, details)
  end
end

total = methods.size
puts "census: #{total} methods in #{files.size - failed.size} files, #{format('%.1f', elapsed)} s" +
     (failed.empty? ? '' : " (#{failed.size} files not parsed)")
puts "  only the #{total} the run in #{used} called, of #{of}" if used
failed.first(rows).each { |f| puts "  not parsed: #{f[:file]}: #{f[:error]}" }
exit 1 if total.zero?

light = methods.count { |m| m[:light] }
blocks = methods.sum { |m| m[:blocks] }
light_blocks = methods.sum { |m| m[:light_blocks] }
by_jit = methods.group_by { |m| m[:jit] }
never = by_jit.fetch('never', [])
puts '', format('light frames   %6d  %s of methods run without a scope', light, pct.(light, total))
puts format('light blocks   %6d  %s of the %d blocks in them', light_blocks, pct.(light_blocks, blocks), blocks)
puts format('JIT compiles   %6d  %s', by_jit.fetch('compiles', []).size, pct.(by_jit.fetch('compiles', []).size, total))
puts format('JIT maybe      %6d  %s', by_jit.fetch('maybe', []).size, pct.(by_jit.fetch('maybe', []).size, total))
puts format('JIT never      %6d  %s  (parameters %d, screen %d)', never.size, pct.(never.size, total),
            never.count { |m| m[:jit_stage] == 'parameters' }, never.count { |m| m[:jit_stage] == 'screen' })

table.('JIT never: what refused the method', never, ->(m) { "#{m[:jit_kind]} (#{m[:jit_stage]})" }, jit_detail)
# The screen stops at the first thing it refuses, and the table above counts that one. A method with five such
# things gains nothing from the JIT learning the first: what a step can win is the methods one thing keeps out
# (two things, for a step that takes both). jit_all has every refusal of the body, each once.
all_of = ->(m) { m[:jit_all].to_s.split('; ') }
screened = never.select { |m| m[:jit_stage] == 'screen' && !all_of.(m).empty? }
unless screened.empty?
  by_count = screened.group_by { |m| [all_of.(m).size, 4].min }
  puts '', 'JIT never at the screen: how many different things in the body it refuses'
  puts '  ' + (1..4).map { |n| format('%s: %d', n == 4 ? '4 or more' : n.to_s, by_count.fetch(n, []).size) }.join('   ')
  sole_kind = ->(m) { all_of.(m).first.split(' ', 2).first }
  sole_detail = ->(m) { all_of.(m).first.split(' ', 2)[1].to_s }
  table.('JIT never, one thing only: taking it lets the method through the screen', by_count.fetch(1, []), sole_kind, sole_detail)
  pairs = by_count.fetch(2, []).group_by { |m| all_of.(m).sort.join(' + ') }.sort_by { |k, ms| [-ms.size, k] }.first(rows)
  unless pairs.empty?
    puts '', 'JIT never, two things: the pairs'
    pairs.each { |k, ms| puts format('  %5d  %s', ms.size, k) }
  end
  # how often a thing is among a method's refusals at all, whatever else is: the ceiling of teaching it
  among = screened.flat_map { |m| all_of.(m) }.tally.sort_by { |k, n| [-n, k] }.first(rows)
  puts '', 'JIT never: in how many methods a thing is refused at all (alone / with others)'
  alone = by_count.fetch(1, []).map { |m| all_of.(m).first }.tally
  among.each { |k, n| puts format('  %5d  %-40s alone %d', n, k, alone.fetch(k, 0)) }
end

table.('JIT maybe: where the typed body stopped without a profile', by_jit.fetch('maybe', []), ->(m) { m[:jit_kind].to_s }, jit_detail)
table.('not a light frame: why', methods.reject { |m| m[:light] }, light_kind)
table.('not a light frame: the construct', methods.reject { |m| m[:light] || light_construct.(m).nil? }, light_construct, light_kind)

if list
  puts '', 'methods'
  methods.each do |m|
    more = all_refusals && m[:jit_all].to_s.include?('; ') ? "  [#{m[:jit_all]}]" : ''
    puts format('  %-40s %-28s %-30s %s%s', label.(m), where.(m), light_text.(m), jit_text.(m), more)
  end
end

# A method is known across two censuses by its file, class and name, not its line, which moves; the second
# definition of a name in a class body is told from the first by its place among them.
keyed = lambda do |ms|
  seen = Hash.new(0)
  ms.to_h { |m| k = "#{rel.(m[:file])}\t#{label.(m)}"; ["#{k}\t#{seen[k] += 1}", m] }
end

if save
  File.open(save, 'w') do |f|
    f.puts FIELDS.join("\t")
    methods.each { |m| f.puts FIELDS.map { |k| (k == :file ? rel.(m[k]) : m[k]).to_s.tr("\t\n", '  ') }.join("\t") }
  end
  puts '', "saved to #{save}"
end

if diff
  lines = File.readlines(diff, chomp: true)
  names = lines.shift.split("\t").map(&:to_sym)
  old = lines.map do |l|
    m = names.zip(l.split("\t", -1)).to_h
    %i[line lines blocks light_blocks].each { |k| m[k] = m[k].to_i }
    %i[light singleton].each { |k| m[k] = m[k] == 'true' }
    m
  end
  before, after = keyed.(old), keyed.(methods)
  both = before.keys & after.keys
  puts '', "against #{diff}: #{old.size} methods then, #{total} now (#{(after.keys - before.keys).size} new, #{(before.keys - after.keys).size} gone)"
  count = ->(ms, &test) { ms.count(&test) }
  [['light frames', ->(m) { m[:light] }], ['JIT compiles', ->(m) { m[:jit] == 'compiles' }],
   ['JIT maybe', ->(m) { m[:jit] == 'maybe' }], ['JIT never', ->(m) { m[:jit] == 'never' }]].each do |name, test|
    was, is = old.count(&test), methods.count(&test)
    puts format('  %-13s %6d -> %6d  %+d  (%s -> %s)', name, was, is, is - was, pct.(was, old.size).strip, pct.(is, total).strip)
  end
  changed = both.select { |k| before[k][:light] != after[k][:light] || before[k][:jit] != after[k][:jit] }
  puts "  #{changed.size} methods changed" + (changed.size > rows * 4 ? ", the first #{rows * 4}:" : changed.empty? ? '' : ':')
  changed.first(rows * 4).each do |k|
    b, a = before[k], after[k]
    parts = []
    parts << "#{light_text.(b)} -> #{light_text.(a)}" if b[:light] != a[:light]
    parts << "#{jit_text.(b)} -> #{jit_text.(a)}" if b[:jit] != a[:jit]
    puts format('    %-40s %-28s %s', label.(a), where.(a), parts.join('; '))
  end
end

# The run names a typed body by class and method and not by line: "main" is a class without a name (a singleton
# class, mostly). A class the census knows by another name than the run's is not found.
find_in = lambda do |ms, (mod, name)|
  named = ms.select { |m| m[:name] == name }
  if mod == 'main'
    named.select { |m| m[:singleton] || m[:module] == 'Object' }
  else
    named.select { |m| !m[:singleton] && (m[:module] == mod || m[:module].end_with?("::#{mod}") || mod.end_with?("::#{m[:module]}")) }
  end
end

# What the run made of the methods it called. A method is counted once, by the first of: specialized (a typed body
# for some arity), declined, rejected at the screen; the rest the run did neither to.
if used
  run, specialized, declined, reasons = trace
  at = ->(m) { run[mkey.(m)] }
  spec = specialized.keys.flat_map { |k| find_in.(methods, k) }.uniq
  decl = declined.keys.flat_map { |k| find_in.(methods, k) }.uniq - spec
  screen = methods.select { |m| at.(m)[:screen] } - spec - decl
  rest = methods.size - spec.size - decl.size - screen.size
  puts '', "the run in #{used}: what it made of the #{methods.size} methods it called"
  puts format('  specialized    %6d  %s', spec.size, pct.(spec.size, methods.size))
  puts format('  declined       %6d  %s  (passed the screen; the compiler or the profile said no)', decl.size, pct.(decl.size, methods.size))
  puts format('  screen         %6d  %s  (rejected at the screen at the first call)', screen.size, pct.(screen.size, methods.size))
  puts format('  neither        %6d  %s  (called fewer times than the JIT waits for, or offered and not', rest, pct.(rest, methods.size))
  puts '                               specialized: the trace does not say which)'
  unmatched = (specialized.keys + declined.keys).uniq.count { |k| find_in.(methods, k).empty? }
  puts format('  %d typed bodies or declines of the run name no method of these files', unmatched) unless unmatched.zero?
  # a reason is "call: to_s/0" or "== on a receiver of unknown type": the kind before the colon, the detail after it
  why = decl.to_h { |m| [m, declined.keys.select { |k| find_in.(methods, k).include?(m) }.flat_map { |k| reasons[k] }.compact.first.to_s] }.compare_by_identity
  table.('declined: why (the compiler\'s reason, or the decline\'s own)', decl, ->(m) { why[m].split(': ', 2).first },
         ->(m) { why[m].split(': ', 2)[1].to_s })
end

if check
  run, specialized, declined = read_trace.(check)

  called = methods.select { |m| run.key?(mkey.(m)) }
  puts '', "against the run in #{check}: it called #{called.size} of the #{total} methods"
  report = lambda do |name, differ|
    puts format('  %-12s %d agree, %d differ', name, called.size - differ.size, differ.size)
    differ.first(rows).each { |m, text| puts format('    %-40s %-28s %s', label.(m), where.(m), text) }
  end
  report.('light frame', called.filter_map { |m|
    r = run[mkey.(m)]
    [m, "census: #{light_text.(m)}; run: #{r[:light] ? 'light' : "scope (#{r[:light_why]})"}"] if r[:light] != m[:light]
  })
  report.('JIT screen', called.filter_map { |m|
    r = run[mkey.(m)]
    [m, "census: #{jit_text.(m)}; run: #{r[:screen] ? "screen rejected (#{r[:screen]})" : 'not rejected by the screen'}"] if (m[:jit_stage] == 'screen') != !r[:screen].nil?
  })
  find = ->(k) { find_in.(called, k) }
  # a typed body the run made of a method the census calls `never' is the census being wrong; one the run declined
  # of a method it calls `compiles' is the profile having said something the three typings do not
  [['specialized', specialized, 'never'], ['declined', declined, 'compiles']].each do |what, set, odd|
    found = set.keys.flat_map { |k| find.(k) }.uniq
    verdicts = found.map { |m| m[:jit] }.tally
    puts format('  %-12s %d in the run; the census has %s', what, set.size,
                verdicts.empty? ? 'none of them (not in these files)' : "#{found.size} of them, as " + verdicts.sort.map { |v, n| "#{v} #{n}" }.join(', '))
    found.select { |m| m[:jit] == odd }.first(rows).each do |m|
      puts format('    %-40s %-28s census: %s', label.(m), where.(m), jit_text.(m))
    end
  end
end
