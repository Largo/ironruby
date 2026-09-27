#!/bin/bash
# Windows experiments; runs in the ironruby checkout.
set -x
export RUBY_EXE="$(cygpath -w "$PWD/ir.cmd")"

watch() { # seconds command...
  local secs=$1; shift
  "$@" &
  local pid=$!
  for _ in $(seq 1 $secs); do
    kill -0 $pid 2>/dev/null || { wait $pid; echo "exit=$?"; return; }
    sleep 1
  done
  echo "=== STILL RUNNING after ${secs}s"
  tasklist //v | grep -i -E "ir.exe|cmd.exe|ruby" | head -20
  wmic process where "name='ir.exe' or name='cmd.exe'" get ProcessId,ParentProcessId,CommandLine 2>/dev/null | head -20
  kill $pid 2>/dev/null
  taskkill //F //IM ir.exe >/dev/null 2>&1
}

echo "--- 1. child writing into a closed pipe"
cat > p1.rb <<'RUBY'
STDERR.puts "child start"
begin
  loop { puts "y" * 100 }
rescue Exception => e
  STDERR.puts "child got #{e.class}: #{e.message}"
  raise
end
RUBY
watch 40 bash -c 'cmd //c ir.cmd p1.rb | head -c 10; echo; echo "pipeline status ${PIPESTATUS[*]}"'

echo "--- 2. IO.popen close while the child writes"
cat > p2.rb <<'RUBY'
t = Time.now
cmd = "#{ENV['RUBY_EXE']} p1.rb"
io = IO.popen(cmd, 'r')
STDERR.puts "pid #{io.pid}"
sleep 2
STDERR.puts "closing"
io.close
STDERR.puts "closed: #{$?.inspect} after #{Time.now - t}"
RUBY
watch 60 cmd //c ir.cmd p2.rb

echo "--- 3. the spec itself"
watch 120 cmd //c ir.cmd -Imspec/lib mspec/bin/mspec-run -f s spec/core/io/close_spec.rb
