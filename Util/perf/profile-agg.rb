# Self and inclusive time per frame from `perf script -F ip,sym,dso` (Util/perf/profile.sh).
#
#   ruby Util/perf/profile-agg.rb X.stacks [top=25]            # top frames
#   ruby Util/perf/profile-agg.rb X.stacks 10 'GetInstanceVar' # who calls the frames matching the regexp
#   ruby Util/perf/profile-agg.rb X.stacks 10 'self:CallSite.Target' # ... only samples whose leaf matches (self time)
#   ruby Util/perf/profile-agg.rb X.stacks 10 'Scheduler#wait'  # Ruby methods by label too
#
# The input is blocks of "address symbol (dso)" lines, leaf first, separated by blank lines.
file, top = ARGV[0], (ARGV[1] || 25).to_i
leaf_only = ARGV[2]&.start_with?('self:')
callers = ARGV[2] && Regexp.new(ARGV[2].delete_prefix('self:'))
# A compiled Ruby body is "IR|label|line|id|file(args)" (RubyStackTraceBuilder.EncodeMethodName):
# shown as "label (file:line)", one entry for all of a method's compiled instances and tiers.
# %XXXX is a UTF-16 code unit, so a character outside the BMP is two of them: decode the run
# together (a surrogate alone is not a character, and chr would raise on it).
unescape = lambda do |t|
  t.gsub(/(?:%\h{4})+/) do |run|
    run.scan(/%(\h{4})/).map { |h| h[0].hex }.pack('S<*').force_encoding(Encoding::UTF_16LE)
       .encode(Encoding::UTF_8, invalid: :replace, undef: :replace)
  end
end
name = lambda do |s|
  # profile.sh asks perf for the DSO too: an unresolved native frame is shown by its library
  # (libcoreclr.so is stripped: the GC and the allocation helpers).
  if (m = s.match(/\A\[unknown\] \((.*)\)\z/))
    next "[unknown] #{File.basename(m[1])}"
  end
  if (m = s.match(/IR\|([^|]*)\|(\d+)\|\d+\|([^(]*)/))
    label = unescape.(m[1])
    file = File.basename(unescape.(m[3]))
    next "#{label} (#{file}:#{m[2]})"[0, 110]
  end
  s = s.gsub(/\[(OptimizedTier1|Optimized|Tier1|Tier0|QuickJitted|PreJIT|InstrumentedTier\w*)\]/, '').sub(/\(.*/, '').strip
  # "instance object [IronRuby] IronRuby.Runtime.RubyOps::GetInstanceVariable" -> "RubyOps::GetInstanceVariable";
  # a DLR rule is "CallSite.Target" (all rules share the name: see who calls it with the third argument).
  s = s.sub(/\A.*?\[[^\]]+\] (?:dynamicClass::)?/, '').sub(/\A[\w.`]*\.(?=[\w`]+(\[[^\]]*\])?::)/, '')
  # The runtime names the patched write barrier code after the first stub it maps.
  s = 'JIT_WriteBarrier' if s.include?('JIT_CopiedWriteBarriers')
  s[0, 110]
end
selfc = Hash.new(0); incl = Hash.new(0); chains = Hash.new(0); n = 0
File.read(file, encoding: 'utf-8', invalid: :replace).split(/\n\s*\n/).each do |block|
  st = block.lines.map { |l| name.(l.strip.split(nil, 2)[1] || '?') }
  next if st.empty?
  n += 1
  selfc[st[0]] += 1
  st.uniq.each { |f| incl[f] += 1 }
  if callers && (i = leaf_only ? (st[0] =~ callers ? 0 : nil) : st.index { |f| f =~ callers })
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
