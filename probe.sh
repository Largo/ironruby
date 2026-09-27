#!/bin/bash
# Windows experiments; runs in the ironruby checkout.
export RUBY_EXE="$(cygpath -w "$PWD/ir.cmd")"

cat > cl.rb <<'RUBY'
# What command line does a spawn with [cmd, argv0] build? python prints its own.
py = 'import ctypes; k = ctypes.windll.kernel32; k.GetCommandLineW.restype = ctypes.c_wchar_p; print(repr(k.GetCommandLineW()))'
pid = Process.spawn(["python.exe", "ARGV0"], "-c", py)
Process.wait pid
pid = Process.spawn(["python.exe", "/C"], "-c", py)
Process.wait pid
pid = Process.spawn("python.exe", "-c", py)
Process.wait pid
pid = Process.spawn(["cmd.exe", "/C"], "/C", "echo", "argv_zero")
Process.wait pid
p $?
RUBY

echo "--- CRuby"
ruby -v
ruby cl.rb
echo "--- IronRuby"
cmd //c ir.cmd cl.rb
