# frozen_string_literal: true
# Runs CRuby's own tests (test/ruby/test_*.rb of a ruby/ruby checkout) on IronRuby.
#
#   ./ir.sh Util/cruby-test/runner.rb <cruby> [test-unit options] <cruby>/test/ruby/test_array.rb
#
# It is CRuby's tool/test/init.rb and runner.rb without the checkers that need RubyVM or
# CRuby's GC (iseq_loader_checker, gc_checker, tracepointchecker).
cruby = File.expand_path(ARGV.shift || abort("usage: runner.rb <cruby checkout> [options] files..."))

%w[GEM_HOME GEM_PATH].each do |gem_env|
  ENV["BUNDLED_#{gem_env}"] = ENV[gem_env]
  ENV[gem_env] = ""
end
ENV["GEM_SKIP"] = ""

Warning[:experimental] = false

$LOAD_PATH.unshift File.join(cruby, "tool/lib")
$LOAD_PATH.unshift File.join(cruby, "test/lib")

require 'test/unit'

exit Test::Unit::AutoRunner.run(true, File.join(cruby, "test"))
