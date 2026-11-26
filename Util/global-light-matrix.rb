# Differential matrix for global variables in a method that runs without a scope (a light frame,
# MethodDefinition.TransformLightFrame). An ordinary global, and every special one but the regex
# match and the last input line, is the runtime's: `$x', `$x = v', `$x ||= v' and `defined?($x)' are
# read and written through the declaring scope there (LightFrameRewriter.VisitGlobalVariableCall).
# A name that turns out to denote `$~' or `$_' - an alias, English.rb's `$LAST_MATCH_INFO' - makes
# the frame's scope on demand and is read again with it, and so is any global while a trace_var
# handler is registered. Run it under CRuby and under ./ir.sh with the shortcut on and off, and diff:
#
#   ruby Util/global-light-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/global-light-matrix.rb > /tmp/on.txt 2>&1
#   IR_LIGHT_GLOBALS=0 ./ir.sh Util/global-light-matrix.rb > /tmp/off.txt 2>&1
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/global-light-matrix.rb > /tmp/noframes.txt 2>&1
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt && diff /tmp/mri.txt /tmp/noframes.txt
#
# Every probe runs N times and reports each distinct answer once, in the order they first appear, so
# the interpreted body, the compiled one and a site whose rule is already bound all meet the case.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/global-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

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

# ---------------------------------------------------------------- 1. reads of the runtime's globals

$g_plain = 10
def r_plain; $g_plain; end
def r_plain_plus(x); $g_plain + x; end
def r_stdout; $stdout.equal?(STDOUT); end
def r_stderr; $stderr.equal?(STDERR); end
def r_verbose; $VERBOSE; end
def r_debug; $DEBUG; end
def r_rs; $/; end
def r_fs; $;; end
def r_ofs; $,; end
def r_program; $0 == $PROGRAM_NAME; end
def r_load_path; [$LOAD_PATH.equal?($:), $LOAD_PATH.class]; end
def r_loaded; $LOADED_FEATURES.equal?($"); end
def r_pid; $$ == Process.pid; end
def r_bang; $!; end
def r_at; $@; end
def r_status; $?.class; end
def r_lineno; $.; end
def r_undefined; $g_never_assigned_anywhere; end
def r_two; [$g_plain, $VERBOSE]; end
def r_in_while; i = 0; s = 0; while i < 3; s += $g_plain; i += 1; end; s; end
def r_in_if(x); x > 0 ? $g_plain : -$g_plain; end
def r_minus_w; $-w; end
def r_star; $*.equal?(ARGV); end

t('a plain global') { r_plain }
t('a plain global in an expression') { |i| r_plain_plus(i % 2) }
t('$stdout') { r_stdout }
t('$stderr') { r_stderr }
t('$VERBOSE') { r_verbose }
t('$DEBUG') { r_debug }
t('$/') { r_rs }
t('$;') { r_fs }
t('$,') { r_ofs }
t('$0 == $PROGRAM_NAME') { r_program }
t('$LOAD_PATH') { r_load_path }
t('$LOADED_FEATURES') { r_loaded }
t('$$') { r_pid }
t('$! outside a rescue') { r_bang }
t('$@ outside a rescue') { r_at }
t('$?') { system('true'); r_status }
t('$.') { r_lineno.class }
t('a global never assigned') { r_undefined }
t('two globals') { r_two }
t('in a while') { r_in_while }
t('in a conditional') { |i| r_in_if(i % 2 == 0 ? 1 : -1) }
t('$-w') { r_minus_w }
t('$* is ARGV') { r_star }

# ---------------------------------------------------------------- 2. writes

def w_plain(v); $g_w = v; end
def w_plain_read(v); $g_w = v; $g_w + 1; end
def w_op(x); $g_op += x; end
def w_or(v); $g_or ||= v; end
def w_and(v); $g_and &&= v; end
def w_two(a, b); $g_a = a; $g_b = b; [$g_a, $g_b]; end
def w_swap; $g_a, $g_b = $g_b, $g_a; [$g_a, $g_b]; end
def w_value(v); ($g_val = v).to_s; end
def w_stdout(o); $stdout = o; end
def w_verbose(v); $VERBOSE = v; end
def w_readonly_bang; $! = 1; end
def w_readonly_load_path; $LOAD_PATH = []; end
def w_lineno(v); $. = v; $.; end
def w_stdout_bad; $stdout = 5; end
def w_new_global(i); $g_new_global = i; end

t('assign a plain global') { |i| w_plain(i % 3); $g_w }
t('assign and read back') { |i| w_plain_read(i % 3) }
$g_op = 0
t('op-assign') { |i| w_op(1); $g_op % 7 }
t('||= unset, then set') { |i| $g_or = nil if i % 2 == 0; w_or(i % 3) }
t('&&= set, then nil') { |i| $g_and = (i % 2 == 0) ? 1 : nil; w_and(:x) }
t('two writes, two reads') { |i| w_two(i % 2, :b) }
$g_a = 1; $g_b = 2
t('parallel assignment of two globals') { w_swap }
t('the value of an assignment') { |i| w_value(i % 2) }
t('$stdout to another writer and back') do |i|
  out = Object.new
  def out.write(*a) = a.sum(&:size)
  w_stdout(out)
  r = $stdout.equal?(out)
  w_stdout(STDOUT)
  [r, $stdout.equal?(STDOUT)]
end
t('$VERBOSE to nil and back') { |i| old = $VERBOSE; w_verbose(nil); r = $VERBOSE; w_verbose(old); [r, $VERBOSE == old] }
t('$! is read-only') { w_readonly_bang }
t('$LOAD_PATH is read-only') { w_readonly_load_path }
t('$. is an Integer') { |i| w_lineno(i % 2) }
t('$stdout to something without write') { w_stdout_bad }
t('a global made by the method') { |i| w_new_global(i % 2); $g_new_global }

# ---------------------------------------------------------------- 3. the frame's own: $~, $_ and their aliases

alias $g_match $~
alias $g_line $_

def m_tilde; 'ab' =~ /b/; $~ && $~[0]; end
def m_alias_after_match; 'xy' =~ /y/; $g_match && $g_match[0]; end
def m_alias_no_match; $g_match; end
def m_alias_set_line(v); $g_line = v; $_; end
def m_line_alias_read; $_ = 'own'; $g_line; end
def m_alias_line_unset; $g_line; end
def m_alias_print(v); $g_line = v; print; end
def m_alias_then_call; $g_line = 'L'; [1].map { $_ }; end
def m_alias_both_ways; 'q' =~ /q/; a = $g_match[0]; $g_match = nil; [a, $~]; end

'top' =~ /top/
$_ = 'top line'
t('$~ after a match of the method') { m_tilde }
t('alias of $~ after a match of the method') { m_alias_after_match }
t('alias of $~, no match of its own') { m_alias_no_match }
t('the caller still has its own $~') { m_alias_no_match; $~[0] }
t('alias of $_ assigned, $_ read') { |i| m_alias_set_line(i % 2 == 0 ? 'a' : 'b') }
t('$_ assigned, alias read') { m_line_alias_read }
t('alias of $_, nothing assigned in the method') { m_alias_line_unset }
t('the caller keeps its $_') { m_alias_set_line('callee'); $_ }
$stdout.sync = true
t('print with no argument prints the alias-assigned $_') { |i| m_alias_print(i % 1000 == 0 ? "printed\n" : ''); :ok }
t('a block reads the $_ the alias assigned') { m_alias_then_call }
t('alias of $~ read and assigned') { m_alias_both_ways }

# the same with the Regexp in a constant, which keeps the method a light frame: the match is stored through the scope the
# call makes on demand, and the alias then reads that scope
RE_Y = /y/
RE_Q = /q/
RE_B = /b/
RE_T = /t/
def m_light_alias_after_match; 'xy' =~ RE_Y; $g_match && $g_match[0]; end
def m_light_alias_match_method; RE_Y.match('zy'); $g_match.pre_match; end
def m_light_alias_both_ways; 'q' =~ RE_Q; a = $g_match[0]; $g_match = nil; [a, $g_match]; end
def m_light_alias_no_match_after_call; [1].size; $g_match; end
def m_light_threads; 'tt' =~ RE_T; [Thread.new { $g_match }.value, $g_match[0]]; end
def m_light_block_alias; 'bb' =~ RE_B; [1].map { $g_match[0] }; end
def m_light_line_alias_write_then_print(v); $g_line = v; print; $g_line; end

t('light: alias of $~ after =~ with a constant') { m_light_alias_after_match }
t('light: alias of $~ after Regexp#match') { m_light_alias_match_method }
t('light: alias of $~ read and assigned') { m_light_alias_both_ways }
t('light: alias of $~ after an unrelated call') { m_light_alias_no_match_after_call }
t('light: the caller\'s $~ after those') { $~[0] }
t('light: another thread, then the method\'s own') { m_light_threads }
t('light: a block reads the alias of the method\'s $~') { m_light_block_alias }
t('light: alias of $_ written, print, read') { |i| m_light_line_alias_write_then_print(i % 1000 == 0 ? "p\n" : '') }
t('light: the caller\'s $_ after that') { $_ }

# The same once more, with the methods compiled before the names became aliases: a name that already denotes $~ or $_
# when a method is first called keeps that method's scope (LightFrameRewriter.VisitGlobalVariableCall), so these are the
# ones that find out at the read or the write and make the scope then.
def l_after_match; 'xy' =~ RE_Y; $g_late_match && $g_late_match[0]; end
def l_match_method; RE_Y.match('zy'); $g_late_match.is_a?(MatchData) ? $g_late_match.pre_match : $g_late_match; end
def l_both_ways; 'q' =~ RE_Q; a = $g_late_match && $g_late_match[0]; $g_late_match = nil; [a, $g_late_match]; end
def l_no_match_after_call; [1].size; $g_late_match; end
def l_threads; 'tt' =~ RE_T; [Thread.new { $g_late_match }.value, $g_late_match && $g_late_match[0]]; end
def l_block; 'bb' =~ RE_B; [1].map { $g_late_match && $g_late_match[0] }; end
def l_line_write_then_print(v); $g_late_line = v; print; $g_late_line; end
def l_line_read; $g_late_line; end
$g_late_match = :plain
$g_late_line = ''
before = [l_after_match, l_match_method, l_both_ways, l_no_match_after_call, l_threads, l_block, l_line_write_then_print(''), l_line_read]
p before
alias $g_late_match $~
alias $g_late_line $_
t('late: alias of $~ after =~ with a constant') { l_after_match }
t('late: alias of $~ after Regexp#match') { l_match_method }
t('late: alias of $~ read and assigned') { l_both_ways }
t('late: alias of $~ after an unrelated call') { l_no_match_after_call }
t('late: the caller\'s $~ after those') { $~[0] }
t('late: another thread, then the method\'s own') { l_threads }
t('late: a block reads the alias of the method\'s $~') { l_block }
t('late: alias of $_ written, print, read') { |i| l_line_write_then_print(i % 1000 == 0 ? "lp\n" : '') }
t('late: alias of $_ read, nothing assigned') { l_line_read }
t('late: the caller\'s $_ after that') { $_ }

def m_half; $g_half; end
$g_half = :plain
t('a global aliased to $~ half way') do |i|
  eval('alias $g_half $~') if i == N / 2
  'zz' =~ /z/
  r = m_half
  r.is_a?(MatchData) ? :match_of_the_method_or_nil : r
end
eval('alias $g_half $g_plain')
t('and aliased to a plain global after') { m_half }

def m_threads; 'tt' =~ /t/; Thread.new { $g_match }.value; end
t('another thread has no $~ of the method') { m_threads }

# ---------------------------------------------------------------- 4. trace_var

$g_traced_log = []
def tr_assign(v); $g_traced = v; end
def tr_read; $g_traced; end
trace_var(:$g_traced) { |v| $g_traced_log << v if $g_traced_log.size < 5 }
t('assigning a traced global fires the handler') { |i| tr_assign(i % 4); tr_read }
p $g_traced_log
untrace_var(:$g_traced)
t('and not after untrace_var') { |i| tr_assign(:untraced); $g_traced_log.size }

$g_cmd_log = []
trace_var(:$g_traced2, '$g_cmd_log << $g_traced2 if $g_cmd_log.size < 3')
def tr_assign2(v); $g_traced2 = v; end
t('a String handler') { |i| tr_assign2(i % 3); $g_cmd_log.size }
p $g_cmd_log
untrace_var(:$g_traced2)

def tr_recursive(v); $g_rec = v; end
trace_var(:$g_rec) { |v| tr_recursive(v * 2) if v < 100 }
t('a handler that assigns the variable it traces') { |i| tr_recursive(1); $g_rec }
untrace_var(:$g_rec)

# ---------------------------------------------------------------- 5. defined?

def d_plain; defined?($g_plain); end
def d_never; defined?($g_defined_never); end
def d_stdout; defined?($stdout); end
def d_tilde_alias; defined?($g_match); end
t('defined?($g) of a set global') { d_plain }
t('defined?($g) of a global never set') { d_never }
t('defined?($stdout)') { d_stdout }
t('defined? of an alias of $~') { d_tilde_alias }

# ---------------------------------------------------------------- 6. other ways in, and what the frame still does

class GHost
  def ivar_and_global; @x = $g_plain; [@x, $VERBOSE]; end
  def self.singleton; $g_plain * 2; end
  define_method(:defined_by_block) { $g_plain + 1 }
end
module GFun
  module_function
  def fun; $g_plain - 1; end
end

def o_binding; v = $g_plain; b = binding; b.local_variable_get(:v); end
def o_locals; v = $g_plain; local_variables; end
def o_eval; v = $g_plain; eval('v + $g_plain'); end
def o_block; [1, 2].map { |x| x + $g_plain }; end
def o_proc; pr = proc { $g_plain }; pr; end
def o_raise; raise ArgumentError, "g=#{$g_plain}"; end
def o_rescue; begin; raise 'x'; rescue; $!.message; end; end
def o_rescue_at; begin; raise 'y'; rescue; $@.class; end; end
def o_caller; $g_plain; caller(0, 1)[0][/'.*'/]; end
def o_method_name; $g_plain; __method__; end
def o_deep(n); $g_depth = n; o_deep(n + 1); end
def o_puts(v); $stdout.write("#{v}\n"); end

t('in a method of a class') { GHost.new.ivar_and_global }
t('a singleton method') { GHost.singleton }
t('define_method') { GHost.new.defined_by_block }
t('module_function') { GFun.fun }
t('send') { send(:r_plain) }
t('__send__') { __send__(:r_plain) }
t('public_send of a public method') { GHost.new.public_send(:ivar_and_global) }
t('Method#call') { method(:r_plain).call }
t('bind_call') { Object.instance_method(:r_plain).bind_call(self) }
t('binding after a read') { o_binding }
t('local_variables after a read') { o_locals }
t('eval after a read') { o_eval }
t('a block reading a global') { o_block }
t('a proc called after the method returned') { o_proc.call }
t('raise with a global in the message') { begin; o_raise; rescue => e; e.message; end }
t('$! in a rescue of the method') { o_rescue }
t('$@ in a rescue of the method') { o_rescue_at }
t('caller') { o_caller }
t('__method__') { o_method_name }
t('a million deep') { |i| i == 0 ? (begin; o_deep(0); rescue SystemStackError; :stack_error; end) : :skipped }
t('$stdout.write and Kernel#puts to a writer') do |i|
  buf = []
  out = Object.new
  out.define_singleton_method(:write) { |*a| buf.concat(a); a.sum(&:size) }
  old = $stdout
  $stdout = out
  begin
    o_puts(i % 2)
    puts 'via Kernel#puts'
  ensure
    $stdout = old
  end
  buf
end

# ---------------------------------------------------------------- 7. a light block

def lb_each; s = 0; [1, 2, 3].each { |x| s += x * $g_plain }; s; end
def lb_write; [1, 2].each { |x| $g_lb = x }; $g_lb; end
def lb_alias; 'bb' =~ /b/; [1].map { $g_match[0] }; end
t('a block reading a global') { lb_each }
t('a block writing a global') { lb_write }
t('a block reading the alias of the method\'s $~') { lb_alias }

# ---------------------------------------------------------------- 8. tracing

def tp_target; $g_plain; end
calls = []
tp = TracePoint.new(:call) { |x| calls << x.method_id if x.method_id == :tp_target && calls.size < 2 }
tp.enable
t('a TracePoint for :call') { tp_target }
tp.disable
p calls
t('after the TracePoint is disabled') { tp_target }
