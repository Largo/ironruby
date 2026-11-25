# When buffered standard output reaches a reader, compared with CRuby 4.0. Each case runs the
# interpreter that runs this file (RbConfig.ruby) as a child whose standard output and standard
# error are one pipe this process reads, so the order in which what it prints, what it writes to
# STDERR and what its own children print arrive is what is compared - with the child's exit
# status. CRuby's rules, which these show:
#
#   - STDOUT is buffered when it is not a terminal (sync is false either way; a terminal is
#     written through), STDERR.sync is true. The buffer is 8192 bytes: a write that fills it or
#     does not fit goes out at once, together with what the buffer held.
#   - It is written out by #flush, by Kernel#p (with or without arguments; not by pp), by
#     #close, #dup and #reopen, before a child starts (system, spawn, `, IO.popen: prefork()
#     flushes $stdout and $stderr - not STDOUT when $stdout is something else), and at exit -
#     after the at_exit handlers and after the report of an uncaught exception (abort's message
#     too), and on a signal Ruby handles (TERM).
#   - It is not written out by STDOUT.sync = true (the next write is), by reading STDIN, by
#     exit!, by exec (no prefork there), or when the process is killed.
#   - #syswrite goes past it, and warns when the buffer holds something - not after a write
#     too large for it, which left it empty.
#   - #reopen(io) takes the other IO's sync with its descriptor (STDOUT onto STDERR writes
#     through); #reopen(path) keeps the IO's own.
#   - Every writer of descriptor 1 in the process goes through the one buffer: on IronRuby a
#     second runtime (IronRuby.create_engine) and .NET's Console.Out, whose lines stay where
#     they were written among Ruby's (Console.Out writes each line out at once, so what Ruby
#     buffered before it goes first). CRuby has neither, so those cases do there what the same
#     code in plain Ruby would - another runtime's code is eval'd, and a Console line is a
#     write and a flush.
#
# Linux only (the cases run sh commands); deterministic output only.
require 'rbconfig'
require 'tmpdir'
require 'fileutils'
require 'shellwords'

RUBY = RbConfig.ruby
DIR = Dir.mktmpdir('stdout-buffer-matrix-')
at_exit { FileUtils.rm_rf(DIR) }

# Long runs of one character as "a*8191", so that the buffer-size cases stay readable.
def squeeze(s)
  s.gsub(/(.)\1{9,}/m) { "#{$1}*#{$&.size}" }
end

def status(st)
  st.signaled? ? "signal #{Signal.signame(st.termsig)}" : "exit #{st.exitstatus}"
end

def run(label, code, stdin: File::NULL)
  r, w = IO.pipe
  pid = Process.spawn(RUBY, "-e", code, out: w, err: w, in: stdin)
  w.close
  out = r.read
  r.close
  Process.wait(pid)
  puts "#{label}: #{squeeze(out).inspect} #{status($?)}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}: #{e.message}"
end

# sync and where writes go
run "sync at a pipe", 'STDERR.print [STDOUT.sync, STDERR.sync, STDIN.sync].inspect'
run "stdout then stderr", 'print "a"; STDERR.print "E"; puts "b"'
run "puts lines", 'puts "a"; puts "b"; STDERR.print "E"; puts "c"'
run "write several", 'print "a"; $stdout.write("b", "c"); STDERR.print "E"'
run "putc printf display", 'print "a"; putc "b"; printf "c"; 1.display; STDERR.print "E"'
run "<<", 'STDOUT << "a" << "b"; STDERR.print "E"'
run "warn", 'print "a"; warn "W"; puts "b"'

# the buffer's size
run "8191 bytes", 'print "a" * 8191; STDERR.print "E"; print "b"; STDERR.print "F"; print "c"'
run "8191 + 2", 'print "a" * 8191; STDERR.print "E"; print "bc"; STDERR.print "F"; print "d"'
run "8192 bytes", 'print "a" * 8192; STDERR.print "E"; print "b"; STDERR.print "F"'
run "9000 bytes", 'print "a" * 9000; STDERR.print "E"; print "b"'
run "4000 + 5000", 'print "a" * 4000; STDERR.print "E"; print "b" * 5000; STDERR.print "F"; print "d"'
run "4000 + 4192", 'print "a" * 4000; STDERR.print "E"; print "b" * 4192; STDERR.print "F"; print "d"'
run "100 + 20000", 'print "a" * 100; STDERR.print "E"; print "b" * 20000; STDERR.print "F"; print "d"'
run "many lines", '2000.times { |i| puts i }; STDERR.print "E"'

# what writes it out
run "flush", 'print "a"; STDOUT.flush; STDERR.print "E"; puts "b"'
run "$stdout.flush", 'print "a"; $stdout.flush; STDERR.print "E"'
run "sync = true", 'print "a"; STDOUT.sync = true; STDERR.print "E"; print "b"; STDERR.print "F"'
run "sync = true first", 'STDOUT.sync = true; print "a"; STDERR.print "E"; puts "b"'
run "sync = false", 'STDOUT.sync = true; STDOUT.sync = false; print "a"; STDERR.print "E"'
run "p", 'print "a"; p 1; STDERR.print "E"'
run "p no args", 'print "a"; p; STDERR.print "E"'
run "p inspect", 'o = Object.new; def o.inspect = "O"; print "a"; p o; STDERR.print "E"'
run "pp", 'print "a"; pp 1; STDERR.print "E"'
run "close", 'print "a"; STDOUT.close; STDERR.print "E"'
# (the copy's own buffering is not compared: IronRuby writes a dup of STDOUT through)
run "dup", 'print "a"; f = STDOUT.dup; STDERR.print "E"; f.print "b"; f.close; STDERR.print "F"'
run "reopen file", "print 'a'; $stdout.reopen(#{File.join(DIR, 'reopen.txt').inspect}); print 'b'; $stdout.flush; STDERR.print File.read(#{File.join(DIR, 'reopen.txt').inspect})"
run "reopen stderr", 'STDOUT.reopen(STDERR); STDERR.print STDOUT.sync; print "a"; STDERR.print "E"; print "b"; STDERR.print "F"'
run "reopen pipe", 'r, w = IO.pipe; STDOUT.reopen(w); puts "x"; STDERR.print r.read_nonblock(10).inspect'
run "reopen file io sync", "f = File.open(#{File.join(DIR, 'reopen2.txt').inspect}, 'w'); STDOUT.reopen(f); STDERR.print STDOUT.sync"
run "reopen path sync", "STDOUT.sync = true; STDOUT.reopen(#{File.join(DIR, 'reopen3.txt').inspect}); STDERR.print STDOUT.sync"
run "pos on a pipe", 'print "a"; begin; STDOUT.pos; rescue => e; STDERR.print e.class; end; STDERR.print "E"'

# children
run "system", 'print "a"; system("echo", "x"); puts "b"'
run "system shell", 'print "a"; system "echo x 1>&2"; puts "b"'
run "spawn", 'print "a"; pid = spawn("echo", "x"); Process.wait pid; puts "b"'
run "spawn no wait", 'print "a"; pid = spawn("true"); STDERR.print "E"; Process.wait pid'
run "backquote", 'print "a"; `echo x`; STDERR.print "E"'
run "IO.popen", 'print "a"; IO.popen(["echo", "x"]) { |i| STDERR.print i.read }; puts "b"'
run "exec", 'print "a"; exec "echo", "x"'
run "$stdout StringIO", 'require "stringio"; print "a"; $stdout = StringIO.new; print "b"; STDERR.print "E"; s = $stdout.string; $stdout = STDOUT; print s'
run "$stdout StringIO system", 'require "stringio"; print "a"; $stdout = StringIO.new; system("echo", "x"); STDERR.print "E"; $stdout = STDOUT'

# other writers of descriptor 1 in the process (an engine starts without the prelude: ruby4 is
# Kernel#system's)
ENGINE = 'def inner(code) = defined?(IronRuby.create_engine) ? (($e ||= IronRuby.create_engine.tap { |e| e.execute(%q(require "ruby4")) }).execute(code); nil) : eval(code); '
run "engine", ENGINE + 'inner %q{puts "inner"}; puts "outer"'
run "engine system", ENGINE + 'inner %q{puts "inner"}; puts "outer"; STDOUT.syswrite "S\n"; system("true"); STDERR.print "E"'
run "engine spawn from inner", ENGINE + 'print "a"; inner %q{print "b"; system("echo", "x")}; puts "c"'
run "engine at exit", ENGINE + 'puts "outer"; inner %q{print "inner"}'
run "engine flush", ENGINE + 'print "a"; inner %q{print "b"; $stdout.flush}; STDERR.print "E"; puts "c"'
CONSOLE = 'def clr(s) = defined?(System::Console) ? System::Console.WriteLine(s) : (STDOUT.write("#{s}\n"); STDOUT.flush); '
run "Console.WriteLine", CONSOLE + 'puts "ruby1"; clr "clr"; puts "ruby2"'
run "Console.WriteLine stderr", CONSOLE + 'print "a"; clr "clr"; STDERR.print "E"; puts "b"'
run "Console.WriteLine exit!", CONSOLE + 'print "a"; clr "clr"; print "b"; exit!'

# reading
run "gets", 'print "a"; STDIN.gets; STDERR.print "E"'
run "read", 'print "a"; STDIN.read; STDERR.print "E"'
run "gets data", 'print "a"; STDERR.print STDIN.gets; puts "b"', stdin: __FILE__

# syswrite
run "syswrite", 'print "a"; STDOUT.syswrite "S"; puts "b"'
run "syswrite after overflow", 'print "a" * 9000; STDOUT.syswrite "S"; puts "b"'
run "syswrite empty buffer", 'STDOUT.syswrite "S"; puts "b"'
run "puts CR at the boundary", 'print "a" * 8190; STDERR.print "E"; puts "x\r"; STDERR.print "F"; exit!'

# the end
run "end", 'print "a"'
run "exit", 'print "a"; exit 3'
run "exit!", 'print "a"; exit!'
run "exit! 0", 'print "b"; STDERR.print "E"; exit!(0)'
run "exit! after at_exit", 'at_exit { print "b" }; print "a"; exit!'
run "abort", 'print "a"; abort "AB"'
run "raise", 'print "a"; raise "boom"'
run "at_exit stderr", 'at_exit { STDERR.print "X" }; print "a"'
run "at_exit stdout", 'at_exit { print "b" }; print "a"; STDERR.print "E"'
run "at_exit exit!", 'at_exit { STDERR.print "X"; exit! }; print "a"'
run "TERM", 'print "a"; Process.kill(:TERM, $$); sleep 5'
run "TERM trapped", 'print "a"; trap(:TERM) { STDERR.print "T"; exit }; Process.kill(:TERM, $$); sleep 5'
run "KILL", 'print "a"; Process.kill(:KILL, $$); sleep 5'

# threads: every line whole, none lost
r, w = IO.pipe
pid = Process.spawn(RUBY, "-e", '4.times.map { |i| Thread.new { 2000.times { puts "line #{i}" } } }.each(&:join)', out: w)
w.close
lines = r.read.lines
r.close
Process.wait(pid)
puts "threads lines: #{lines.size} #{lines.tally.sort.inspect} #{status($?)}"

# a file
path = File.join(DIR, "out.txt")
pid = Process.spawn(RUBY, "-e", 'STDERR.print STDOUT.sync; print "a"; STDERR.print File.size(ARGV[0]); puts "b"', path, out: path, err: File.join(DIR, "err.txt"))
Process.wait(pid)
puts "file: #{File.read(path).inspect} #{File.read(File.join(DIR, 'err.txt')).inspect} #{status($?)}"

# a terminal (script(1) gives the child one): written through, sync still false
if system("command -v script >/dev/null 2>&1")
  code = 'STDERR.print [STDOUT.sync, STDOUT.tty?].inspect; print "a"; STDERR.print "E"; print "b"'
  out = IO.popen(["script", "-qec", "#{Shellwords.escape(RUBY)} -e #{Shellwords.escape(code)}", "/dev/null"], &:read)
  puts "terminal: #{out.inspect} #{status($?)}"
else
  puts "terminal: no script(1)"
end
