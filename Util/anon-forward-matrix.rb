# What can reach the values a method forwards with `...' or an anonymous `*', `**' or `&' (Ruby 3.2+), after the call
# that forwarded them - what a forward that hands the callee the caller's own Array (no copy) would have to keep right.
# CRuby 4.0: nothing names them (local_variables, binding.local_variables and a TracePoint binding see none,
# binding.local_variable_get(:"...") raises), but the method's own body can forward them again - a second `raw(...)',
# eval("raw(...)"), binding.eval, instance_eval, send(:eval), eval through an alias, and a TracePoint :return hook's
# binding.eval - and must then see what the caller was given, whatever an earlier callee did to its rest or kwrest.
#
#   ruby Util/anon-forward-matrix.rb > mri.txt && ./ir.sh Util/anon-forward-matrix.rb > ir.txt && diff mri.txt ir.txt
#
# (perf/dotsfwd, Util/perf/LEDGER.md: why `...' forwarding cannot share the caller's Array.)
def raw(*a, **k, &b) = [a, k, b&.call]
def try_(label)
  r = begin; yield; rescue Exception => e; "#{e.class}: #{e.message.lines.first.strip[0, 80].gsub(/0x[0-9a-f]+/, "0x")}"; end
  puts "#{label.ljust(44)} => #{r.inspect}"
end
def d_eval(...) = eval("raw(...)")
def d_eval_star(*) = eval("raw(*)")
def d_eval_dstar(**) = eval("raw(**)")
def d_eval_amp(&) = eval("raw(&)")
def d_lvars(...) = local_variables
def d_lvars_anon(*, **, &) = local_variables
def d_binding_lvars(...) = binding.local_variables
def d_binding_get(...) = binding.local_variable_get(:"...")
def d_binding_get_star(*) = binding.local_variable_get(:*)
def d_binding_eval(...) = binding.eval("raw(...)")
def d_instance_eval(...) = instance_eval("raw(...)")
def d_send_eval(...) = send(:eval, "raw(...)")
def d_eval_star_in_dots(...) = eval("raw(*)")
try_("eval('raw(...)') in def(...)") { d_eval(1, 2, k: 3) { :blk } }
try_("eval('raw(*)') in def(*)") { d_eval_star(1, 2) }
try_("eval('raw(**)') in def(**)") { d_eval_dstar(k: 1) }
try_("eval('raw(&)') in def(&)") { d_eval_amp { :blk } }
try_("local_variables in def(...)") { d_lvars(1, 2) }
try_("local_variables in def(*, **, &)") { d_lvars_anon(1, k: 2) { } }
try_("binding.local_variables in def(...)") { d_binding_lvars(1) }
try_("binding.local_variable_get(:'...')") { d_binding_get(1) }
try_("binding.local_variable_get(:*)") { d_binding_get_star(1) }
try_("binding.eval('raw(...)')") { d_binding_eval(1, 2) }
try_("instance_eval('raw(...)')") { d_instance_eval(1, 2) }
try_("send(:eval, 'raw(...)')") { d_send_eval(1, 2) }
try_("eval('raw(*)') in def(...)") { d_eval_star_in_dots(1, 2) }
seen = []
tp = TracePoint.new(:return) { |t| seen << (t.binding.local_variables rescue $!.class) if t.method_id == :fwd_tp }
def fwd_tp(...) = raw(...)
tp.enable { fwd_tp(1, 2) }
try_("TracePoint :return binding.local_variables") { seen }
def keeps(*a) = (a << :mutated; a)
def fwd_keep(...) = keeps(...)
try_("callee mutates its rest; caller's view") { arr = [1, 2]; [fwd_keep(*arr), arr] }
def keeps2(*a) = (a << :m; a.size)
def twice(...) = [keeps2(...), raw(...)]
try_("callee mutates rest, then raw(...) again") { twice(1, 2) }
def twice_eval(...) = [keeps2(...), eval("raw(...)")]
try_("callee mutates rest, then eval('raw(...)')") { twice_eval(1, 2) }
def twice_anon(*) = [keeps2(*), raw(*)]
try_("callee mutates rest, then raw(*) again") { twice_anon(1, 2) }
def kwkeep(**k) = (k[:x] = 1; k.size)
def twice_kw(**) = [kwkeep(**), raw(**)]
try_("callee mutates kwrest, then raw(**) again") { twice_kw(a: 1) }
def fwd_tp2(...) = keeps2(...)
seen = []
tp = TracePoint.new(:return) { |t| seen << t.binding.eval("raw(...)") if t.method_id == :fwd_tp2 }
tp.enable { fwd_tp2(1, 2) }
try_("TracePoint :return binding.eval('raw(...)')") { seen }
class Object; alias_method :e2, :eval; end
def via_alias(...) = [keeps2(...), e2("raw(...)")]
try_("eval through an alias after a mutating callee") { via_alias(1, 2) }
