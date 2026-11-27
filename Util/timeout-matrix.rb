# Differential matrix for Timeout (Src/StdLib/ruby/4.0/timeout.rb, with IronRuby's adjustment in
# Src/StdLib/ironruby/timeout.rb: the watcher thread is woken for a new request only when it has to be).
# Run under CRuby and under ./ir.sh, and diff:
#
#   ruby Util/timeout-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/timeout-matrix.rb > /tmp/ir.txt
#   diff /tmp/mri.txt /tmp/ir.txt
#
# Times are not printed, only whether a timeout fired within a bound and what it raised. The traps: a short timeout
# after many long ones (the watcher must be woken for an earlier deadline), a long timeout around a short one and the
# other way round, many requests queued while the watcher waits for a later deadline, firing while blocked in sleep,
# in IO and in a busy loop, a custom error class and message, the backtrace, Timeout::ExitException not caught by a
# bare rescue, and the watcher thread not multiplied by many calls.
require 'timeout'
$stdout.sync = true

def t(label)
  print label.ljust(52), ' => '
  begin
    v = yield
    puts v.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message}"
  end
end

def mono = Process.clock_gettime(Process::CLOCK_MONOTONIC)

def fires_within(limit)
  t0 = mono
  begin
    yield
    :no_timeout
  rescue Timeout::Error => e
    el = mono - t0
    el < limit ? [e.class, e.message] : [:late, e.class]
  end
end

t("no timeout, value")                  { Timeout.timeout(1) { :v } }
t("nil and zero mean none")             { [Timeout.timeout(nil) { 1 }, Timeout.timeout(0) { 2 }] }
t("negative")                           { Timeout.timeout(-1) { 1 } }
t("fires in sleep")                     { fires_within(2) { Timeout.timeout(0.1) { sleep 5 } } }
t("fires in a busy loop")               { fires_within(2) { Timeout.timeout(0.1) { loop { 1 + 1 } } } }
t("fires in IO")                        { r, w = IO.pipe; fires_within(2) { Timeout.timeout(0.1) { r.read(1) } } }
t("fires in Queue#pop")                 { q = Queue.new; fires_within(2) { Timeout.timeout(0.1) { q.pop } } }
t("custom class")                       { begin; Timeout.timeout(0.05, ArgumentError) { sleep 5 }; rescue ArgumentError => e; [e.class, e.message]; end }
t("custom class and message")           { begin; Timeout.timeout(0.05, ArgumentError, "late!") { sleep 5 }; rescue ArgumentError => e; [e.class, e.message]; end }
t("custom message")                     { begin; Timeout.timeout(0.05, nil, "slow") { sleep 5 }; rescue Timeout::Error => e; e.message; end }
t("backtrace has the block's line")     { begin; Timeout.timeout(0.05) { sleep 5 }; rescue Timeout::Error => e; e.backtrace.any? { |l| l.include?("timeout-matrix.rb") }; end }
t("bare rescue does not take it")       { fires_within(2) { Timeout.timeout(0.1) { begin; sleep 5; rescue => e; :rescued; end } } }
t("rescue Exception does")              { Timeout.timeout(0.1) { begin; sleep 5; rescue Exception => e; e.class.name; end } }
t("raise in the block")                 { begin; Timeout.timeout(5) { raise "boom" }; rescue => e; [e.class, e.message]; end }
t("raise in the block, cause")          { begin; Timeout.timeout(5) { raise "boom" }; rescue => e; e.cause; end }
t("$! after")                           { begin; Timeout.timeout(5) { raise "boom" }; rescue; end; $! }
t("nested, inner shorter fires")        { fires_within(2) { Timeout.timeout(5) { Timeout.timeout(0.1) { sleep 5 } } } }
t("nested, outer shorter fires")        { fires_within(2) { Timeout.timeout(0.1) { Timeout.timeout(5) { sleep 5 } } } }
t("nested, both long")                  { Timeout.timeout(5) { Timeout.timeout(6) { :ok } } }
t("short after 2000 long ones")         { 2000.times { Timeout.timeout(30) { 1 } }; fires_within(2) { Timeout.timeout(0.1) { sleep 5 } } }
t("short after 2000 long raising")      { 2000.times { begin; Timeout.timeout(30) { raise "x" }; rescue; end }; fires_within(2) { Timeout.timeout(0.1) { sleep 5 } } }
t("short inside a long, many queued")   { Timeout.timeout(30) { 200.times { Timeout.timeout(20) { 1 } }; fires_within(2) { Timeout.timeout(0.1) { sleep 5 } } } }
t("long ones keep working after")       { 500.times { Timeout.timeout(30) { 1 } }; Timeout.timeout(30) { :ok } }
t("another thread's timeout")           { th = Thread.new { fires_within(2) { Timeout.timeout(0.1) { sleep 5 } } }; th.value }
t("two threads at once")                { a = Thread.new { fires_within(2) { Timeout.timeout(0.2) { sleep 5 } } }; b = Thread.new { Timeout.timeout(5) { sleep 0.05; :b } }; [a.value, b.value] }
t("watcher threads after 10,000 calls") { 10_000.times { Timeout.timeout(30) { 1 } }; Thread.list.count { |th| th.name == "Timeout stdlib thread" } }
t("fires after 10,000 calls")           { fires_within(2) { Timeout.timeout(0.1) { sleep 5 } } }
t("timeout method on an object")        { o = Object.new; o.extend(Timeout); o.send(:timeout, 1) { :m } }
