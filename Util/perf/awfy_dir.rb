# The directory the Are-We-Fast-Yet benchmarks are run from, for awfy.rb and alloc.rb: a copy of the
# checkout's Ruby directory in which som.rb's `Set' is called `SomSet'.
#
# AWFY's som.rb defines its own Set and IdentitySet < Set. Ruby 4.0 made Set a core class, and there
# a subclass of Set does not run an `initialize' that a reopened Set defines, so CRuby 4.0 fails Havlak,
# the one benchmark that makes a Set ("undefined method 'has_some' for nil"). Under another name the
# class is what the benchmark means it to be on every implementation, and all fourteen benchmarks run
# on all of them from one source. Nothing else is changed; a benchmark still raises on a wrong answer.
require 'tmpdir'
require 'fileutils'

def awfy_dir(root)
  home = ENV['PERF_HOME'] || File.join(File.dirname(root), 'ironruby-perf')
  src = File.join(home, 'awfy/benchmarks/Ruby')
  abort "no #{src}: run Util/perf/setup.sh" unless File.directory?(src)
  dir = Dir.mktmpdir('awfy-')
  at_exit { FileUtils.rm_rf(dir) }
  FileUtils.cp(Dir[File.join(src, '*.rb')], dir)
  rename = lambda do |file, pattern, to, count|
    path = File.join(dir, file)
    text = File.read(path)
    found = text.scan(pattern).size
    # a checkout that has moved on is looked at, not patched by guess
    abort "#{File.join(src, file)}: #{found} of #{pattern.inspect}, expected #{count}: see Util/perf/awfy_dir.rb" unless found == count
    File.write(path, text.gsub(pattern, to))
  end
  rename.('som.rb', /^class Set$/, 'class SomSet', 1)
  rename.('som.rb', /^class IdentitySet < Set$/, 'class IdentitySet < SomSet', 1)
  rename.('havlak.rb', /(?<![A-Za-z_:])Set\.new/, 'SomSet.new', 1)
  dir
end
