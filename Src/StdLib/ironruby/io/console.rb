# io/console is a C extension in MRI (termios/ioctl, or the Win32 console API).
# This is the part of it .NET's System.Console can honestly provide: the window
# size, and reading a key or a password without echo. Terminal modes (#raw,
# #noecho, #echo=, #console_mode, ...) are not implemented.

load_assembly 'System.Console'
require 'stringio'

class IO
  # [rows, columns] of the terminal this IO is connected to.
  def winsize
    __console_check__
    [System::Console.window_height, System::Console.window_width]
  rescue System::IO::IOException, System::PlatformNotSupportedException
    raise Errno::ENOTTY, *path
  end

  # Reads one character from the terminal without echoing it and without
  # waiting for a newline.
  def getch(min: nil, time: nil, intr: nil)
    __console_check__
    __read_key__
  end

  # Prompts on the terminal (on $stderr when reading STDIN, as MRI does) and
  # reads a line without echoing it; the line is returned without its newline.
  def getpass(prompt = nil)
    __console_check__
    wio = equal?(STDIN) ? $stderr : self
    wio.write(prompt) if prompt
    wio.flush
    line = +""
    while (c = __read_key__)
      break if c == "\r" || c == "\n"
      line << c
    end
    wio.puts
    line
  end

  private

  def __console_check__
    raise Errno::ENOTTY, *path unless tty?
  end

  def __read_key__
    System::Console.read_key(true).key_char.to_s
  rescue System::InvalidOperationException
    raise Errno::ENOTTY, *path
  end
end

# MRI adds these to IO::generic_readable, which StringIO includes: the
# console-free meanings of #getch (#getc) and #getpass (a prompted #gets).
class StringIO
  def getch(*args)
    getc(*args)
  end

  def getpass(prompt = nil)
    write(prompt) if prompt
    flush
    str = gets
    puts
    str&.chomp
  end
end
