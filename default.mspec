# MSpec configuration for running ruby/spec against IronRuby.
#
#   ir -X:UsePrism -X:StdLib=<stdlib paths> -Imspec/lib mspec/bin/mspec-run spec/language
#
class MSpecScript
  ir_root = File.expand_path(File.dirname(__FILE__))

  windows = File::ALT_SEPARATOR == "\\"
  exe = windows ? "ir.exe" : "ir"
  bin = File.join(ir_root, "Src", "Console", "bin", "Debug", "net8.0")
  # A tree cross-published for Windows puts the host one level deeper.
  bin = File.join(bin, "win-x64") if windows && !File.exist?(File.join(bin, exe))

  stdlib = %w[ironruby ruby/4.0 ruby/1.9.1].
    map { |d| File.join(ir_root, "Src", "StdLib", d) }.join(File::PATH_SEPARATOR)
  # The separator is ";" on Windows, which is also how cmd.exe separates arguments - and
  # ruby_exe builds a command line the shell parses. Quoting keeps the three directories
  # one argument; RubyOptionsParser.GetPaths strips the quotes back off.
  stdlib = "\"#{stdlib}\"" if windows

  set :target, File.join(bin, exe)
  set :flags, [
    "-X:UsePrism",
    # -X:StdLib rather than -I: the library goes on $LOAD_PATH after the -I paths a spec
    # passes, as MRI's does
    "-X:StdLib=#{stdlib}",
  ]

  set :language, [File.join(ir_root, "spec", "language")]

  set :excludes, [
    # IO.popen("-") is fork-without-exec. The CLR has no fork primitive, and emulating it
    # with a new interpreter would not preserve the running Ruby process's state.
    "IO.popen starts returns a forked process if the command is -",
  ]

end

# mspec's fixnum_max and fixnum_min know MRI's range (from RbConfig::LIMITS), JRuby's,
# TruffleRuby's and a few more, and raise for any other engine - which fails every spec
# at the Fixnum/Bignum boundary in core/integer, core/array and core/string before it
# has tested anything. IronRuby's immediate integers are System.Int32, and its
# RbConfig::LIMITS says so; defining the pair here, after the helper file has been
# loaded, is what a pristine mspec checkout (CI's) needs.
if RUBY_ENGINE == "ironruby"
  require 'mspec/helpers/numeric'
  require 'rbconfig/sizeof'

  def fixnum_max
    RbConfig::LIMITS['FIXNUM_MAX']
  end

  def fixnum_min
    RbConfig::LIMITS['FIXNUM_MIN']
  end
end
