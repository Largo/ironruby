# MSpec configuration for running ruby/spec against IronRuby.
#
#   ir -X:UsePrism -X:StdLib=<stdlib paths> -Imspec/lib mspec/bin/mspec-run spec/language
#
class MSpecScript
  ir_root = File.expand_path(File.dirname(__FILE__))

  set :target, File.join(ir_root, "Src", "Console", "bin", "Debug", "net8.0", "ir")
  set :flags, [
    "-X:UsePrism",
    # -X:StdLib rather than -I: the library goes on $LOAD_PATH after the -I paths a spec
    # passes, as MRI's does
    "-X:StdLib=#{%w[ironruby ruby/4.0 ruby/1.9.1].map { |d| File.join(ir_root, "Src", "StdLib", d) }.join(File::PATH_SEPARATOR)}",
  ]

  set :language, [File.join(ir_root, "spec", "language")]

  set :excludes, [
    # IO.popen("-") is fork-without-exec. The CLR has no fork primitive, and emulating it
    # with a new interpreter would not preserve the running Ruby process's state.
    "IO.popen starts returns a forked process if the command is -",
  ]

end
