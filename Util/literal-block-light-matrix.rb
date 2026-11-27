# Differential matrix for calls with a block in a light frame whose body has literal blocks (round thirty-seven,
# perf/literalsite; Docs/light-frame-literal-blocks.md, change A). In such a body a call with a block went through the
# full site with the scope made on demand (LightFrameRewriter.LightScopeCall: `!BlockLiterals'); with
# IR_LIGHT_LITERAL_CALLS on, when every literal of the body is of the narrow shape - it reads and writes no enclosing
# local, has no return, yield, super, nested literal, and calls none of binding, eval, local_variables, __method__ - the
# call takes the light site, and the full one only when its rule answers LightScopeRequired. Run it under CRuby and
# under ./ir.sh with the switch on and off, and diff:
#
#   ruby Util/literal-block-light-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/literal-block-light-matrix.rb > /tmp/on.txt
#   IR_LIGHT_LITERAL_CALLS=0 ./ir.sh Util/literal-block-light-matrix.rb > /tmp/off.txt
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt
#
# Every probe runs N times and reports each distinct answer once, so the interpreted body, the compiled one and a site
# whose rule is already bound all meet the case.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/literal-block-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'

N = 3000

def t(label)
  print label.ljust(58), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 60]}"
  end
end

P0 = proc { :p0 }

class C
  def nb = (block_given? ? yield : :none)
  def nb2(x) = (block_given? ? yield(x) : :none)
  def keep(&b) = (@kept = b; :kept)
  attr_reader :kept
  def run_kept = @kept.call
  def kept_binding_names = @kept.binding.local_variables.sort
  def priv_target = :priv
  private :priv_target
  def m_missing_target; end

  # ---- the narrow shape: literals that read no enclosing local
  def lit(a) = nb { :lit }
  def lit_x(a) = nb2(a) { |x| x * 2 }
  def pass_in_lit_body(a); return nb(&P0) if a; nb { :never }; end
  def plain_in_lit_body(a); return nb if a; nb { :never }; end
  def each_lit(a) = a.each { |x| x }.size
  def map_lit(a) = a.map { |x| x + 1 }
  def times_lit(n); n.times { }; n; end
  def two_lits(a) = [nb { 1 }, nb2(a) { |x| x }]
  def lit_and_pass(a, b) = [nb { :l }, nb(&b)]

  # ---- the literal escapes from the callee
  def lit_kept(a); keep { :from_kept }; run_kept; end
  def lit_kept_later(a); keep { a.class }; :ok; end
  def lit_define(a); C.send(:define_method, :defined_by_lit) { :d }; defined_by_lit; end
  def lit_proc_new(a) = Kernel.proc { :pn }.call
  def lit_lambda(a) = lambda { |x| x }.lambda?
  def lit_kept_binding(a); x = 1; keep { :k }; [x, kept_binding_names]; end

  # ---- break, next, raise from the literal
  def lit_break(a) = nb { break :broke }
  def lit_break_ensure(a)
    r = nb { break :broke_e }
    r
  ensure
    @ensured = true
  end
  attr_reader :ensured
  def lit_next(a) = nb { next :nexted }
  def lit_raise(a) = (nb { raise ArgumentError, "in lit" } rescue $!.message)
  def lit_throw(a) = catch(:t) { nb { throw :t, :thrown } }

  # ---- the callee reaches for the caller's scope
  def lit_to_binding_callee(a) = nb2(binding) { |b| b.local_variables.sort }
  def lit_send_binding(a) = nb { send(:binding).local_variables.sort }
  def lit_local_variables_method(a); y = 5; nb { :x }; local_variables.sort; end
  def lit_method_name(a); nb { :x }; __method__; end
  def lit_match(a); nb { "abc".send(:=~, /b/) }; $~ && $~[0]; end
  def lit_eval_method(a); z = 3; nb { :x }; eval("z + 1"); end
  def lit_private(a) = nb { priv_target }
  def lit_respond(a) = nb { respond_to?(:priv_target) }
  def lit_block_given(a) = [nb { block_given? }, block_given?]

  # ---- re-entrancy and recursion
  def lit_recurse(n) = (n == 0 ? :bottom : nb { lit_recurse(n - 1) })
  def lit_reenter(a) = nb { a ? lit_reenter(false) : :inner }

  # ---- literals that are not of the narrow shape: the body keeps the full site
  def wide_local(a); s = 0; nb { s += 1 }; s; end
  def wide_return(a); nb { return :ret }; :after; end
  def wide_yield(a) = nb { yield }
  def wide_binding(a); q = 1; nb { binding.local_variable_get(:q) }; end
  def wide_nested(a) = nb { nb { :nested } }
  def wide_pass(a); s = 0; nb { s += 1 }; nb(&P0); end
end

c = C.new

puts "# 1. the narrow shape"
t("lit")                       { c.lit(1) }
t("lit_x")                     { |i| c.lit_x(i % 3) }
t("pass_in_lit_body")          { c.pass_in_lit_body(true) }
t("pass_in_lit_body false")    { c.pass_in_lit_body(false) }
t("plain_in_lit_body")         { c.plain_in_lit_body(true) }
t("each_lit")                  { c.each_lit([1, 2, 3]) }
t("map_lit")                   { c.map_lit([1, 2]) }
t("times_lit")                 { c.times_lit(2) }
t("two_lits")                  { c.two_lits(:a) }
t("lit_and_pass")              { c.lit_and_pass(1, proc { :b }) }

puts "# 2. escape"
t("lit_kept")                  { c.lit_kept(1) }
t("lit_kept_later")            { |i| c.lit_kept_later(i.odd? ? 1 : "s"); c.run_kept }
t("lit_define")                { c.lit_define(1) }
t("lit_proc_new")              { c.lit_proc_new(1) }
t("lit_lambda")                { c.lit_lambda(1) }
t("lit_kept_binding")          { c.lit_kept_binding(1) }

puts "# 3. break, next, raise"
t("lit_break")                 { c.lit_break(1) }
t("lit_break_ensure")          { [c.lit_break_ensure(1), c.ensured] }
t("lit_next")                  { c.lit_next(1) }
t("lit_raise")                 { c.lit_raise(1) }
t("lit_throw")                 { c.lit_throw(1) }

puts "# 4. the scope reached for"
t("lit_to_binding_callee")     { c.lit_to_binding_callee(1) }
t("lit_send_binding")          { c.lit_send_binding(1) }
t("lit_local_variables_method"){ c.lit_local_variables_method(1) }
t("lit_method_name")           { c.lit_method_name(1) }
t("lit_match")                 { c.lit_match(1) }
t("lit_eval_method")           { c.lit_eval_method(1) }
t("lit_private")               { c.lit_private(1) }
t("lit_respond")               { c.lit_respond(1) }
t("lit_block_given")           { c.lit_block_given(1) }

puts "# 5. re-entrancy, threads"
t("lit_recurse")               { c.lit_recurse(3) }
t("lit_reenter")               { c.lit_reenter(true) }
t("threads") { |i| i % 300 == 0 ? 4.times.map { |k| Thread.new { lt_r = []; 50.times { lt_r << c.lit_x(k) }; lt_r.uniq } }.map(&:value) : :skip }

puts "# 6. not the narrow shape"
t("wide_local")                { c.wide_local(1) }
t("wide_return")               { c.wide_return(1) }
t("wide_yield")                { c.wide_yield(1) { :y } }
t("wide_binding")              { c.wide_binding(1) }
t("wide_nested")               { c.wide_nested(1) }
t("wide_pass")                 { c.wide_pass(1) }

puts "# 7. tracing"
ev = []
tp = TracePoint.new(:call, :b_call, :c_call) { |x| ev << [x.event, x.method_id] if x.path == __FILE__ && [:lit, :nb, :pass_in_lit_body].include?(x.method_id) }
tp.enable
3.times { c.lit(1); c.pass_in_lit_body(true) }
tp.disable
p ev.uniq
bl = []
tp = TracePoint.new(:b_call) { |x| bl << x.binding.local_variables.sort if x.path == __FILE__ && x.lineno == C.instance_method(:lit_x).source_location[1] }
tp.enable
c.lit_x(4)
tp.disable
p bl
