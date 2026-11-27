# A rest parameter passed on as the splat of a call - `def w(*args) = n(*args)' - read again after the call by ways that
# do not name it in the method's source: instance_eval and class_eval of a String, send(:eval, ...), an alias of eval,
# Kernel#binding through bind_call or a Method, and a TracePoint's binding at :return. CRuby's caller keeps its own
# Array whatever the callee does to its rest parameter. IronRuby's ForwardedRest (IR_FORWARD_REST=1) handed the callee
# the caller's Array where its walk found no other reference, and each of these saw the callee's change (the gate of
# round 5, 2026-11-27); it is off by default until it checks that nothing can run in the frame after the call.
# (The bind_call and Method rows differ from CRuby on master too: CRuby cannot make a Binding for a non-Ruby caller.)
def n(*a); a << :x; a; end
def row(label)
  r = begin; yield; rescue Exception => e; "#{e.class}: #{e.message[0, 60]}"; end
  puts "#{label.ljust(48)} #{r.inspect}"
end
def w_ieval(*args); n(*args); instance_eval("args"); end
def w_ceval(*args); n(*args); Object.class_eval("1"); instance_eval { 0 }; instance_eval("args.size"); end
def w_send_binding(*args); n(*args); send(:binding).local_variable_get(:args); end
def w_usend_binding(*args); n(*args); __send__(:binding).local_variable_get(:args); end
def w_send_eval(*args); n(*args); send(:eval, "args"); end
def w_bind_call(*args); n(*args); Kernel.instance_method(:binding).bind_call(self).local_variable_get(:args); end
def w_method_binding(*args); n(*args); method(:binding).call.local_variable_get(:args) rescue $!.class; end
alias e_alias eval
def w_alias_eval(*args); n(*args); e_alias("args"); end
def w_lvars(*args); n(*args); send(:local_variables); end
def w_kept(*args) = n(*args)
3.times do
  row('instance_eval(string) after the call') { w_ieval(1, 2) }
  row('instance_eval(string) behind other evals') { w_ceval(1, 2) }
  row('send(:binding)') { w_send_binding(1, 2) }
  row('__send__(:binding)') { w_usend_binding(1, 2) }
  row('send(:eval, "args")') { w_send_eval(1, 2) }
  row('bind_call of Kernel#binding') { w_bind_call(1, 2) }
  row('method(:binding).call') { w_method_binding(1, 2) }
  row('an alias of eval') { w_alias_eval(1, 2) }
  row('the callee keeps the Array') { a = [1, 2]; r = w_kept(*a); [a, r] }
end
$seen = []
tp = TracePoint.new(:return) { |t| $seen << t.binding.local_variable_get(:args) if t.method_id == :w_traced }
def w_traced(*args) = n(*args)
w_traced(0)
tp.enable { w_traced(1, 2) }
def w_traced2(*args) = n(*args)
tp2 = TracePoint.new(:return) { |t| $seen << t.binding.local_variable_get(:args) if t.method_id == :w_traced2 }
tp2.enable { w_traced2(3, 4) }
row('TracePoint :return binding') { $seen }
