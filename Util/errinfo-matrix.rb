# Differential matrix for $! around begin/rescue/else/ensure when nothing is raised and when something is (Body.
# TransformExceptionHandling; IR_LAZY_ERRINFO leaves $! alone on the runs that raise nothing). Run it under CRuby and
# under ./ir.sh in every mode, and diff:
#
#   ruby Util/errinfo-matrix.rb > mri.txt
#   ./ir.sh Util/errinfo-matrix.rb > on.txt
#   IR_LAZY_ERRINFO=0 ./ir.sh Util/errinfo-matrix.rb > off.txt
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/errinfo-matrix.rb > scoped.txt
#   diff mri.txt on.txt && diff mri.txt off.txt && diff mri.txt scoped.txt
#
# Each probe runs 3000 times (interpreted, then compiled) and prints every distinct answer once: $! in and after a
# rescue, a begin that raises nothing inside a rescue clause, a rescue in a callee, an exception going through a rescue
# that does not match, return/break/next/throw through ensure, an exception raised in ensure and its cause, retry,
# else/ensure order, binding and local_variables in a rescue clause, caller from it, TracePoint :raise/:rescue,
# Timeout and Thread#raise arriving mid-body, and recursion a million deep through an ensure.

N = 3000

def t(label)
  print label.ljust(52), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 70]}"
  end
end

def errinfo = $!&.message

def quiet_rescue(x)
  begin
    x + 1
  rescue StandardError
    0
  end
end

def quiet_ensure(x)
  begin
    x + 1
  ensure
    @seen = errinfo
  end
end

def callee_rescues
  raise TypeError, "inner"
rescue TypeError
  errinfo
end

def passes_through
  begin
    raise ArgumentError, "pass"
  rescue TypeError
    :no
  end
end

def ensure_return
  begin
    return errinfo
  ensure
    @in_ensure = errinfo
  end
end

def ensure_raises
  begin
    raise "first"
  ensure
    raise "second"
  end
end

def retrying
  n = 0
  begin
    n += 1
    raise "r#{n}" if n < 3
    [n, errinfo]
  rescue
    retry
  end
end

def else_ensure(log)
  begin
    log << errinfo
  rescue
    log << :rescue
  else
    log << :else << errinfo
  ensure
    log << :ensure << errinfo
  end
  log
end

def in_clause
  begin
    raise "b"
  rescue => err
    [err.message == binding.local_variable_get(:err).message, local_variables.include?(:err), caller(0, 1)[0][/in .*/]]
  end
end

def deep_ensure(n)
  return 0 if n == 0
  begin
    deep_ensure(n - 1) + 1
  ensure
    @d = n
  end
end

t("nothing raised: $! after rescue, after ensure") { |i| [quiet_rescue(i % 2), quiet_ensure(i % 2), errinfo, @seen] }
t("nothing raised inside a rescue clause") do |i|
  begin
    raise "outer#{i % 2}"
  rescue
    [quiet_rescue(i % 2), quiet_ensure(i % 2), errinfo, @seen]
  end
end
t("$! after the rescue clause ends") { |i| begin; raise "x#{i % 2}"; rescue; errinfo; end; errinfo }
t("a callee's rescue, then $! here") { |i| begin; raise "o"; rescue; [callee_rescues, errinfo]; end }
t("through a rescue that does not match") { |i| r = begin; passes_through; rescue => e; [e.message, errinfo]; end; [r, errinfo] }
t("return through ensure, inside a rescue") { |i| begin; raise "y"; rescue; [ensure_return, @in_ensure, errinfo]; end }
t("an exception raised in ensure and its cause") { |i| begin; ensure_raises; rescue => e; [e.message, e.cause&.message, errinfo]; end }
t("retry") { |i| [retrying, errinfo] }
t("else and ensure order, $! in each") { |i| else_ensure([]) }
t("else and ensure inside a rescue clause") { |i| begin; raise "z"; rescue; else_ensure([]); end }
t("next and break through ensure") { |i| [1, 2, 3].each { |k| begin; next if k == 1; break k * 10; ensure; @last = [k, errinfo]; end } }
t("throw through ensure") { |i| [catch(:t) { begin; throw :t, 5; ensure; @thrown = errinfo; end }, @thrown, errinfo] }
t("binding, local_variables, caller in a rescue clause") { |i| in_clause }

tp_events = []
tp = TracePoint.new(:raise, :rescue) { |tp| tp_events << [tp.event, tp.raised_exception.message] }
t("TracePoint :raise and :rescue") do |i|
  tp_events.clear
  tp.enable { begin; raise "tp"; rescue; end; quiet_rescue(i % 2); quiet_ensure(i % 2) }
  tp_events.dup
end

require 'timeout'
t("Timeout in a body with a rescue that does not match") do |i|
  next nil unless i % 500 == 0
  r = (Timeout.timeout(0.01) { begin; sleep 1; rescue ArgumentError; :no; end } rescue $!.class)
  [r, errinfo]
end
t("Thread#raise in a body with rescue and ensure") do |i|
  next nil unless i % 500 == 0
  th = Thread.new { begin; sleep; rescue => e; [e.message, errinfo]; ensure; @th_ensure = errinfo; end }
  sleep 0.01 until th.status == "sleep"
  th.raise("tr")
  [th.value, errinfo]
end

puts "recursion a million deep through ensure: " + (begin; deep_ensure(1_000_000).to_s; rescue SystemStackError; "SystemStackError"; end)
puts "and $! after: #{errinfo.inspect}"
