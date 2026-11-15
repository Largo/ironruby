# Per-op cost (ns) of non-local control flow shapes. Usage: ruby micro.rb [filter]
def foo; yield; end
def bar; foo { return 1 }; end
def brk; [1].each { break 2 }; end
def nxt; foo { next 3 }; end
def lam; l = -> { return 4 }; l.call; end
def prc_ret; pr = proc { return 5 }; pr.call; 0; end
class MyErr < StandardError; end
def deep(n); n == 0 ? raise(MyErr) : deep(n - 1); end
SHAPES = {
  "raise_rescue"      => -> { begin; raise MyErr; rescue MyErr; end },
  "raise_msg"         => -> { begin; raise "x"; rescue => e; end },
  "raise_obj"         => (E = MyErr.new("p"); -> { begin; raise E; rescue MyErr; end }),
  "raise_deep10"      => -> { begin; deep(10); rescue MyErr; end },
  "retry"             => -> { n = 0; begin; n += 1; raise MyErr if n < 2; rescue MyErr; retry; end },
  "ensure_noraise"    => -> { begin; 1; ensure; 2; end },
  "ensure_raise"      => -> { begin; begin; raise MyErr; ensure; 2; end; rescue MyErr; end },
  "catch_throw"       => -> { catch(:t) { throw :t, 1 } },
  "catch_nothrow"     => -> { catch(:t) { 1 } },
  "catch_obj_throw"   => -> { catch { |t| throw t } },
  "block_return"      => -> { bar },
  "block_break"       => -> { brk },
  "block_next"        => -> { nxt },
  "lambda_return"     => -> { lam },
  "proc_return"       => -> { prc_ret },
  "stop_iteration"    => -> { e = [1].each; e.next; begin; e.next; rescue StopIteration; end },
  "loop_stopiter"     => -> { e = [1, 2].each; loop { e.next } },
  "Integer_bad"       => -> { Integer("x") rescue nil },
  "Integer_exc_false" => -> { Integer("x", exception: false) },
  "Float_bad"         => -> { Float("x") rescue nil },
  "fetch_keyerror"    => (H = {a: 1}; -> { H.fetch(:b) rescue nil }),
  "fetch_default"     => -> { H.fetch(:b, 0) },
  "backtrace"         => -> { begin; raise MyErr; rescue => e; e.backtrace; end },
  "caller"            => -> { caller },
  "caller_locations1" => -> { caller_locations(1, 1) },
  "nomethod_rescue"   => -> { nil.zork rescue nil },
  "zerodiv_rescue"    => -> { 1 / 0 rescue nil },
}
filt = ARGV[0] && Regexp.new(ARGV[0])
SHAPES.each do |name, pr|
  next if filt && name !~ filt
  n = 2000
  loop { t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); n.times { pr.call }; d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t; break if d > 0.2; n *= 2 }
  best = 5.times.map { t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); n.times { pr.call }; Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t }.min
  printf "%-18s %10.1f\n", name, best * 1e9 / n
end
