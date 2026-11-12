# Runs Util/perf/micro.rb on several interpreters and prints one row per operation.
#
#   ruby Util/perf/micro-table.rb NAME=CMD...
#   ruby Util/perf/micro-table.rb cruby=ruby "base=env IR_CONFIG=Release ./ir.sh" \
#        "new=env IR_CONFIG=Release ../wt-x/ir.sh"
#
# The last column is the second interpreter over the first, the gap the perf-step skill
# picks its next target from.
interps = ARGV.map { |s| s.split('=', 2) }
micro = File.join(__dir__, 'micro.rb')
res = interps.map do |_, cmd|
  IO.popen("LANG=C.UTF-8 #{cmd} #{micro} 2>&1", &:read).scan(/^(\S+)\s+([\d.]+) ns\/op/).to_h
end
printf "%-10s" + " %9s" * interps.size + "  %s\n", 'ns/op', *interps.map(&:first), interps.size > 1 ? "#{interps[1][0]}/#{interps[0][0]}" : ''
res[0].each_key do |op|
  v = res.map { |r| r[op] }
  ratio = v[1] && v[0].to_f > 0 ? format('%.2f', v[1].to_f / v[0].to_f) : ''
  printf "%-10s" + " %9s" * interps.size + "  %s\n", op, *v.map { |x| x || '-' }, ratio
end
