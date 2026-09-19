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

  # These crash the process rather than failing an expectation, which would abort
  # the whole run, so they are skipped until the underlying bugs are fixed.
  set :excludes, [
    # super through the same anonymous module included twice recurses forever
    # (pre-existing: the legacy parser behaves identically)
    "The super keyword invokes methods from a chain of anonymous modules",
  ]
end
