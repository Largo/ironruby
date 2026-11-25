# Buffered STDOUT at a pipe, as CI runs it on Linux and Windows: the IronRuby.Tests suite gives
# every runtime a stream of its own (SharedIO), so it never reaches the buffer ConsoleStream keeps
# for the process's descriptor 1, nor the Windows code under it (GetFileType, the two-byte "\r\n"
# a #puts appends in text mode, the exit of a Windows exec). Each case runs the interpreter that
# runs this file (RbConfig.ruby) as a child whose standard output and standard error are one
# pipe, and compares what arrives, in that order, and the exit status with what CRuby 4.0 gives
# (this file passes under CRuby too). Util/stdout-buffer-matrix.rb has the full set of cases,
# Linux only; these are the ones that need a platform, a second runtime or .NET to show.
# Prints each line, and exits 1 if any differs.
require "rbconfig"

RUBY = RbConfig.ruby
WINDOWS = RUBY_PLATFORM =~ /mswin|mingw|cygwin/ || (defined?(System::Environment) && System::Environment.OSVersion.Platform.to_s == "Win32NT")
$failed = false

# Long runs of one character as "a*8191", so that the buffer-size cases stay readable.
def squeeze(s)
  s.gsub(/(.)\1{9,}/m) { "#{$1}*#{$&.size}" }
end

def check(name, code, expected, status = 0)
  r, w = IO.pipe
  pid = Process.spawn(RUBY, "-e", code, out: w, err: w, in: File::NULL)
  w.close
  out = r.read
  r.close
  Process.wait(pid)
  # a #puts line ends in "\r\n" on Windows, in IronRuby as in CRuby (text mode)
  actual = [squeeze(out.delete("\r")), $?.exitstatus]
  ok = actual == [expected, status]
  $failed = true unless ok
  puts "#{ok ? "ok  " : "FAIL"} #{name}: #{actual.inspect}#{ok ? "" : " (expected #{[expected, status].inspect})"}"
rescue Exception => e
  $failed = true
  puts "FAIL #{name}: #{e.class}: #{e.message}"
end

CHILD = 'require "rbconfig"; def child(s) = system(RbConfig.ruby, "-e", "print :#{s}"); '
# IronRuby.create_engine: a second runtime in the process, without the prelude (ruby4 is
# Kernel#system's). Under CRuby, the same code in this one.
ENGINE = 'def inner(code) = defined?(IronRuby.create_engine) ? (($e ||= IronRuby.create_engine.tap { |e| e.execute(%q(require "ruby4")) }).execute(code); nil) : eval(code); '
# System::Console.WriteLine writes each line out at once; under CRuby, a write and a flush.
CONSOLE = 'def clr(s) = defined?(System::Console) ? System::Console.WriteLine(s) : (STDOUT.write("#{s}\n"); STDOUT.flush); '

check "sync", 'STDERR.print [STDOUT.sync, STDERR.sync].inspect', "[false, true]"
check "stdout then stderr", 'print "a"; STDERR.print "E"; puts "b"', "Eab\n"
check "flush", 'print "a"; STDOUT.flush; STDERR.print "E"; puts "b"', "aEb\n"
check "9000 bytes", 'print "a" * 9000; STDERR.print "E"; print "b"', "a*9000Eb"
check "p", 'print "a"; p; STDERR.print "E"', "aE"
check "syswrite", 'print "a"; STDOUT.syswrite "S"; puts "b"', "-e:1: warning: syswrite for buffered IO\nSab\n"
check "syswrite after overflow", 'print "a" * 9000; STDOUT.syswrite "S"; puts "b"', "a*9000Sb\n"
check "reopen stderr", 'STDOUT.reopen(STDERR); STDERR.print STDOUT.sync; print "a"; STDERR.print "E"; print "b"; STDERR.print "F"', "trueaEbF"
check "reopen pipe", 'r, w = IO.pipe; STDOUT.reopen(w); puts "x"; STDERR.print r.read_nonblock(10).inspect', "\"x\\n\""

check "system", CHILD + 'print "a"; child "x"; puts "b"', "axb\n"
check "exec", 'print "a"; exec RbConfig.ruby, "-e", "print %q(b)"', "b"

check "end", 'print "a"', "a"
check "exit", 'print "a"; exit 3', "a", 3
check "exit!", 'print "a"; STDERR.print "E"; exit!', "E", 1
check "raise", 'print "a"; raise "boom"', "-e:1:in '<main>': boom (RuntimeError)\na", 1
check "at_exit", 'at_exit { print "b" }; print "a"; STDERR.print "E"', "Eab"

# A #puts line and its end are two pieces of the buffer; the end is "\r\n" on Windows, so there
# it fills the buffer where "\n" leaves one byte free, and exit! then has nothing to lose.
check "puts at the boundary", 'print "a" * 8189; STDERR.print "E"; puts "x"; STDERR.print "F"; exit!',
  WINDOWS ? "Ea*8189x\nF" : "EF", 1

check "engine", ENGINE + 'inner %q{puts "inner"}; puts "outer"', "inner\nouter\n"
check "engine system", CHILD + ENGINE + 'inner %q{puts "inner"}; puts "outer"; STDOUT.syswrite "S\n"; child "x"; STDERR.print "E"',
  "-e:1: warning: syswrite for buffered IO\nS\ninner\nouter\nxE"
check "engine at exit", ENGINE + 'puts "outer"; inner %q{print "inner"}', "outer\ninner"
check "Console.WriteLine", CONSOLE + 'puts "ruby1"; clr "clr"; puts "ruby2"', "ruby1\nclr\nruby2\n"
check "Console.WriteLine exit!", CONSOLE + 'print "a"; clr "clr"; print "b"; exit!', "aclr\n", 1

exit 1 if $failed
