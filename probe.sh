#!/bin/bash
# Windows experiments; runs in the ironruby checkout.
export RUBY_EXE="$(cygpath -w "$PWD/ir.cmd")"
dotnet tool install -g dotnet-stack >/dev/null 2>&1
export PATH="$PATH:$HOME/.dotnet/tools:/c/Users/runneradmin/.dotnet/tools"

stacks() {
  for p in $(tasklist //FI "IMAGENAME eq ir.exe" //FO CSV //NH | cut -d, -f2 | tr -d '"'); do
    echo "=== stacks of ir.exe $p"
    timeout 60 dotnet-stack report -p "$p" 2>&1 | head -80
  done
}

watch() { # seconds command...
  local secs=$1; shift
  "$@" &
  local pid=$!
  for _ in $(seq 1 $secs); do
    kill -0 $pid 2>/dev/null || { wait $pid; echo "exit=$?"; return; }
    sleep 1
  done
  echo "=== STILL RUNNING after ${secs}s"
  tasklist //v | grep -i -E "ir.exe|cmd.exe" | head -20
  stacks
  kill $pid 2>/dev/null
  taskkill //F //IM ir.exe >/dev/null 2>&1
}

echo "--- 1. the spec's child: rescue EPIPE, then exit"
cat > p3.rb <<'RUBY'
t = Time.now
io = IO.popen("#{ENV['RUBY_EXE']} -e \"r = loop{puts %q(y); 0} rescue 1; STDERR.puts %q(rescued); exit r\"", 'r')
STDERR.puts "pid #{io.pid}"
sleep 3
STDERR.puts "closing"
io.close
STDERR.puts "closed: #{$?.inspect} after #{Time.now - t}"
RUBY
watch 40 cmd //c ir.cmd p3.rb

echo "--- 2. spawn with [cmd.exe, /C]"
cat > p4.rb <<'RUBY'
p Process.__spawn_command__([["cmd.exe", "/C"], "/C", "echo", "argv_zero"], nil)
pid = Process.spawn(["cmd.exe", "/C"], "/C", "echo", "argv_zero")
Process.wait pid
p $?
system("cmd.exe", "/C", "echo", "three")
p $?
RUBY
watch 40 cmd //c ir.cmd p4.rb
