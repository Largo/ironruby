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
#     -n N      rows per table (default 12)
#     -l        list every method
#
# A method is, to the JIT: `never' (its parameters or the static screen refuse it, whatever it
# is called with), `compiles' (a typed body exists for parameters that are all Integers, all
# Floats or all objects, the receiver unknown) or `maybe' (it passes the screen, and the profile
# decides: the receiver's class and instance variables, constants, callees). The tables give
# the reasons, most frequent first - the top line is what to teach the JIT or the frames next -
# and -d is the effect of having done it. It takes seconds and does not depend on the machine's
# load, but it counts methods, not calls: a refusal in a method nothing calls costs nothing.
# It does not see the class a method ends up in (JitCensus says what it assumes); -c says how
# far that is from a run.
abort 'census.rb runs on IronRuby: ./ir.sh Util/perf/census.rb ...' unless defined?(IronRuby) && IronRuby.respond_to?(:jit_census)

FIELDS = %i[file line lines module name singleton light light_why blocks light_blocks jit jit_stage jit_kind jit_why jit_types]
save = diff = check = nil
rows = 12
list = false
paths = []
args = ARGV.dup
until args.empty?
  case (a = args.shift)
  when '-o' then save = args.shift
  when '-d' then diff = args.shift
  when '-c' then check = args.shift
  when '-n' then rows = Integer(args.shift)
  when '-l' then list = true
  when /\A-/ then abort "census.rb: unknown option #{a}"
  else paths << a
  end
end
abort 'usage: ./ir.sh Util/perf/census.rb [-o FILE] [-d FILE] [-c LOG] [-n N] [-l] FILE|DIR...' if paths.empty?

files = paths.flat_map { |p| File.directory?(p) ? Dir.glob(File.join(p, '**', '*.rb')).sort : [p] }.map { |p| File.expand_path(p) }.uniq
started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
result = IronRuby.jit_census(files)
elapsed = Process.clock_gettime(Process::CLOCK_MONOTONIC) - started
failed, methods = result.partition { |h| h.key?(:error) }

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
table.('JIT maybe: where the typed body stopped without a profile', by_jit.fetch('maybe', []), ->(m) { m[:jit_kind].to_s }, jit_detail)
table.('not a light frame: why', methods.reject { |m| m[:light] }, light_kind)
table.('not a light frame: the construct', methods.reject { |m| m[:light] || light_construct.(m).nil? }, light_construct, light_kind)

if list
  puts '', 'methods'
  methods.each { |m| puts format('  %-40s %-28s %-30s %s', label.(m), where.(m), light_text.(m), jit_text.(m)) }
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

# The run's own account, one method at a time as each is first called: the body's transformation prints a [light]
# line with the file and line, and the screen's refusal, if it refuses, is the [jit] line that follows it.
if check
  run = {}
  last = nil
  specialized, declined = Hash.new(0), Hash.new(0)
  File.foreach(check, chomp: true) do |line|
    case line
    when /\A\[light\] (light|needs scope|why not): (.+) \((.+):(\d+)\)(?:: (.*))?\z/
      at = "#{$3}:#{$4}"
      last = run[at] ||= { name: $2, light: false, screen: nil }
      last[:light] = true if $1 == 'light'
      last[:light_why] = $5 if $5
    when %r{\A\[jit\] screen rejected (.+)/([^/ ]+|/): (.*)\z}
      last[:screen] = $3 if last && last[:name] == $2
    when %r{\A\[jit\] specialized (.+)/([^/ ]+|/) arity} then specialized[[$1, $2]] += 1
    when %r{\A\[jit\] declined (.+)/([^/ ]+|/) arity} then declined[[$1, $2]] += 1
    end
  end

  called = methods.select { |m| run.key?("#{m[:file]}:#{m[:line]}") }
  puts '', "against the run in #{check}: it called #{called.size} of the #{total} methods"
  report = lambda do |name, differ|
    puts format('  %-12s %d agree, %d differ', name, called.size - differ.size, differ.size)
    differ.first(rows).each { |m, text| puts format('    %-40s %-28s %s', label.(m), where.(m), text) }
  end
  report.('light frame', called.filter_map { |m|
    r = run["#{m[:file]}:#{m[:line]}"]
    [m, "census: #{light_text.(m)}; run: #{r[:light] ? 'light' : "scope (#{r[:light_why]})"}"] if r[:light] != m[:light]
  })
  report.('JIT screen', called.filter_map { |m|
    r = run["#{m[:file]}:#{m[:line]}"]
    [m, "census: #{jit_text.(m)}; run: #{r[:screen] ? "screen rejected (#{r[:screen]})" : 'not rejected by the screen'}"] if (m[:jit_stage] == 'screen') != !r[:screen].nil?
  })
  # The run names a typed body by class and method and not by line: "main" is a class without a name (a singleton
  # class, mostly). A class the census knows by another name than the run's is not found.
  find = lambda do |(mod, name)|
    named = called.select { |m| m[:name] == name }
    if mod == 'main'
      named.select { |m| m[:singleton] || m[:module] == 'Object' }
    else
      named.select { |m| !m[:singleton] && (m[:module] == mod || m[:module].end_with?("::#{mod}") || mod.end_with?("::#{m[:module]}")) }
    end
  end
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
