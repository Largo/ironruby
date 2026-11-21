# Builds Util/capi/selftest (a C extension calling the C API functions real gems use that
# ruby/spec's capi suite does not) with mkmf and checks what it gives, on the Ruby running it:
#
#   ./ir.sh Util/capi/selftest.rb      # IronRuby, with libironruby-capi built (Util/build-capi.sh)
#   ruby Util/capi/selftest.rb         # CRuby 4.0, whose results the expectations below are
#
# On IronRuby it also checks that IRONRUBY_NATIVE_GEMS hands a feature IronRuby implements
# itself to a gem's compiled extension (Gem.ironruby_native_extension_for). Exits 1 on a
# difference.

require "rbconfig"
require "tmpdir"
require "fileutils"
require "stringio"

fixture = File.expand_path("selftest", __dir__)
ruby = RbConfig.ruby
failures = []

check = lambda do |what, actual, expected|
  failures << "#{what}: expected #{expected.inspect}, got #{actual.inspect}" unless actual == expected
end

Dir.mktmpdir("capi-selftest") do |dir|
  FileUtils.cp(Dir[File.join(fixture, "*")], dir)
  log = File.join(dir, "build.log")
  built = system(ruby, "extconf.rb", chdir: dir, out: log, err: [:child, :out]) &&
    system(ENV["MAKE"] || "make", chdir: dir, out: [log, "a"], err: [:child, :out])
  abort "building the extension failed:\n#{File.read(log)}" unless built

  $LOAD_PATH.unshift(dir)
  captured, $stderr = $stderr, StringIO.new
  begin
    require "capi_selftest"
  ensure
    warnings, $stderr = $stderr.string, captured
  end
  m = CApiSelftest

  # rb_undef_method(klass, "initialize") is allowed and silent; new then ends in method_missing
  check.("rb_undef_method warnings", warnings, "")
  check.("new without initialize",
    (begin; m::NoInit.new; rescue NoMethodError => e; e.message; end),
    "undefined method 'initialize' for an instance of CApiSelftest::NoInit")

  # rb_opts_exception_p
  check.("exception: absent", m.opts_exception, 7)
  check.("exception: true", m.opts_exception(exception: true), 1)
  check.("exception: false", m.opts_exception(exception: false), 0)
  check.("exception: 1",
    (begin; m.opts_exception(exception: 1); rescue ArgumentError => e; e.message; end),
    "expected true or false as exception: 1")
  check.("unknown keyword",
    (begin; m.opts_exception(foo: 1); rescue ArgumentError => e; e.message; end),
    "unknown keyword: :foo")

  # rb_must_asciicompat
  check.("ASCII-compatible", m.must_asciicompat("a".encode("UTF-8")), true)
  check.("UTF-16LE",
    (begin; m.must_asciicompat("a".encode("UTF-16LE")); rescue Encoding::CompatibilityError => e; e.message; end),
    "ASCII incompatible encoding: UTF-16LE")

  # RHASH_SIZE, OBJ_FREEZE
  check.("RHASH_SIZE", [m.hash_size({}), m.hash_size({a: 1, b: 2})], [0, 2])
  s = +"x"
  check.("OBJ_FREEZE", [m.freeze(s).equal?(s), s.frozen?, m.freeze(1)], [true, true, 1])

  # rb_memhash: the same bytes the same hash in a process, another in another process
  check.("rb_memhash in a process", m.memhash("abc") == m.memhash(+"abc"), true)
  check.("rb_memhash bytes", m.memhash("abc") == m.memhash("abd"), false)
  other = IO.popen([ruby, "-I", dir, "-rcapi_selftest", "-e", 'print CApiSelftest.memhash("abc")'], &:read)
  check.("rb_memhash per process", other.empty? || other.to_i == m.memhash("abc"), false)

  # Ractor-local storage
  check.("value, unset", [m.local_value, m.local_value_lookup], [nil, false])
  m.local_value_set(:v)
  check.("value, set", [m.local_value, m.local_value_lookup], [:v, [:v]])
  m.local_value_set(nil)
  check.("value, set to nil", m.local_value_lookup, [nil])
  check.("ptr, unset", m.local_ptr, nil)
  m.local_ptr_set(42)
  check.("ptr, set", m.local_ptr, 42)
  # set and read from several threads at once: every read sees a value one of them set
  seen = 8.times.map do |i|
    Thread.new { 2000.times.map { m.local_value_set(i); m.local_value } }
  end.flat_map(&:value).uniq.sort
  check.("value from threads", seen - (0...8).to_a, [])

  # Ractor sharing and the standard streams
  a = [+"s"]
  check.("make_shareable", [m.make_shareable(a).equal?(a), a.frozen?, a[0].frozen?, m.shareable?(a)], [true, true, true, true])
  b = [+"t"]
  c = m.make_shareable_copy(b)
  check.("make_shareable_copy", [c.equal?(b), b.frozen?, c.frozen?, c], [false, false, true, ["t"]])
  check.("shareable? of a mutable string", m.shareable?(+"u"), false)
  check.("std_streams", m.std_streams.map(&:object_id), [$stdin, $stdout, $stderr].map(&:object_id))

  # Code an extension has not declared thread-safe runs on one thread at a time (CRuby's GVL,
  # IronRuby's extension lock), and Ruby code it calls - here waiting for another thread that
  # calls the same extension - does not hold it
  check.("not thread-safe: one thread at a time",
    4.times.map { Thread.new { m::Unsafe.exclusive(20_000_000) } }.map(&:value).uniq, [false])
  q = Queue.new
  waiter = Thread.new { m::Unsafe.yield_nil { q.pop } }
  sleep 0.1
  Thread.new { m::Unsafe.yield_nil { q << :fed } }.join
  check.("lock let go while C runs Ruby", waiter.join(10) && waiter.value, :fed)

  # IRONRUBY_NATIVE_GEMS: a gem's compiled extension in place of IronRuby's own
  if defined?(Gem.ironruby_native_extension_for)
    gem_home = File.join(dir, "gems-home")
    lib = File.join(gem_home, "gems", "capi_selftest-1.0", "lib")
    FileUtils.mkdir_p([lib, File.join(gem_home, "specifications")])
    so = "capi_selftest.#{RbConfig::CONFIG["DLEXT"]}"
    FileUtils.cp(File.join(dir, so), lib)
    probe = <<~RUBY
      spec = Gem::Specification.new {|s| s.name = "capi_selftest"; s.version = "1.0" }
      spec.loaded_from = File.join(#{gem_home.dump}, "specifications", "capi_selftest-1.0.gemspec")
      Gem.loaded_specs["capi_selftest"] = spec
      $LOAD_PATH.unshift(#{lib.dump})
      found = Gem.ironruby_native_extension_for("capi_selftest")
      print [found, Gem.ironruby_require_native("capi_selftest") && CApiSelftest.hash_size({a: 1})].inspect
    RUBY
    run = lambda do |env|
      IO.popen([env, ruby, "-e", probe], &:read)
    end
    check.("IRONRUBY_NATIVE_GEMS names the gem", run.({"IRONRUBY_NATIVE_GEMS" => "capi_selftest"}),
      [File.join(lib, so), 1].inspect)
    check.("IRONRUBY_NATIVE_GEMS unset", run.({"IRONRUBY_NATIVE_GEMS" => nil}), [nil, false].inspect)
  end
end

if failures.empty?
  puts "capi selftest: passed (#{RUBY_ENGINE})"
else
  puts "capi selftest: #{failures.size} failed (#{RUBY_ENGINE})", failures
  exit 1
end
