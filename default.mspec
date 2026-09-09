# MSpec configuration for running ruby/spec against IronRuby.
#
#   ir -X:UsePrism -I<stdlib paths> -Imspec/lib mspec/bin/mspec-run spec/language
#
class MSpecScript
  ir_root = File.expand_path(File.dirname(__FILE__))

  set :target, File.join(ir_root, "Src", "Console", "bin", "Debug", "net8.0", "ir")
  set :flags, [
    "-X:UsePrism",
    "-I#{File.join(ir_root, "Src", "StdLib", "ironruby")}",
    "-I#{File.join(ir_root, "Src", "StdLib", "ruby", "1.9.1")}",
  ]

  set :language, [File.join(ir_root, "spec", "language")]
end
