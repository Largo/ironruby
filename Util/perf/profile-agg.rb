# Self and inclusive time per frame from `perf script -F ip,sym` (Util/perf/profile.sh).
#
#   ruby Util/perf/profile-agg.rb X.stacks [top=25]            # top frames
#   ruby Util/perf/profile-agg.rb X.stacks 10 'GetInstanceVar' # who calls the frames matching the regexp
#
# The input is blocks of "address symbol" lines, leaf first, separated by blank lines.
file, top, callers = ARGV[0], (ARGV[1] || 25).to_i, ARGV[2] && Regexp.new(ARGV[2])
name = lambda do |s|
  s.gsub(/\[(OptimizedTier1|Optimized|Tier1|Tier0|QuickJitted|PreJIT)\]/, '').sub(/\(.*/, '').strip[0, 110]
end
selfc = Hash.new(0); incl = Hash.new(0); chains = Hash.new(0); n = 0
File.read(file, encoding: 'utf-8', invalid: :replace).split(/\n\s*\n/).each do |block|
  st = block.lines.map { |l| name.(l.strip.split(nil, 2)[1] || '?') }
  next if st.empty?
  n += 1
  selfc[st[0]] += 1
  st.uniq.each { |f| incl[f] += 1 }
  if callers && (i = st.index { |f| f =~ callers })
    chains[st[i, 4].join(' <- ')] += 1
  end
end
show = ->(h, k) { h.sort_by { |_, v| -v }.first(k).each { |f, v| printf "%5.1f%% %s\n", 100.0 * v / n, f } }
puts "samples #{n}"
if callers
  show.(chains, top)
else
  puts 'SELF'; show.(selfc, top)
  puts 'INCLUSIVE'; show.(incl, top + 15)
end
