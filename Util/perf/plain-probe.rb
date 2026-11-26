# Which core classes no longer answer for their instances in the method JIT's class-wide test
# (JitCompiler.HasPlainInstances): a singleton method of some name, or method_missing, on any one instance of a
# CLR-typed class (Array, Hash, String...) is recorded in the class's ClrSingletonMethods, and an adopted instance in
# HasAdoptedInstances; either turns that operation (or all of them) off in every typed body for the rest of the process.
# Load it into a run; it prints what was recorded when the process ends:
#
#   IR_CONFIG=Release ./ir.sh -r Util/perf/plain-probe.rb Util/bench/app-shapes.rb 2>&1 >/dev/null | grep '\[plain\]'
#   IR_CONFIG=Release ./ir.sh -r Util/perf/plain-probe.rb -S gem list 2>&1 >/dev/null | grep '\[plain\]'
#
# IronRuby only (it reads internal properties through CLR reflection).
at_exit do
  f = System::Reflection::BindingFlags
  flags = f.NonPublic | f.Instance | f.Public
  t = Array.GetType
  sm = t.GetProperty("ClrSingletonMethods", flags)
  ad = t.GetProperty("HasAdoptedInstances", flags)
  out = [Array, Hash, String, Symbol, Range, Proc, Time, Regexp, MatchData, NilClass, TrueClass, FalseClass, Integer, Float].map do |c|
    keys = (v = sm.GetValue(c)) ? v.Keys.to_a.map(&:to_s).sort : []
    keys.empty? && !ad.GetValue(c) ? nil : "#{c}: singleton names #{keys.inspect}#{ad.GetValue(c) ? ' ADOPTED' : ''}"
  end.compact
  $stderr.puts "[plain] " + (out.empty? ? "all plain" : out.join("; "))
end
