# frozen_string_literal: true
#
# RubyGems defaults for IronRuby.  rubygems.rb requires
# "rubygems/defaults/#{RUBY_ENGINE}" after rubygems/defaults.rb, which is the
# hook upstream reserves for an alternative implementation - see the comment at
# the top of rubygems.rb.  Everything IronRuby-specific about gem locations,
# the Ruby command RubyGems re-executes, and the platform a gem is built for
# lives here rather than in a patched copy of the vendored files.

module Gem
  ##
  # IronRuby ships the Ruby 4.0 standard library, so its gems live under
  # <libdir>/ruby/gems/4.0.0 - not under RbConfig's "ruby_version", which is
  # still the 1.9.1 the load path and the C# Loader use for the legacy tree.

  def self.default_dir
    @default_dir ||= File.join(RbConfig::CONFIG["rubylibprefix"], "gems", ruby_api_version)
  end

  ##
  # The API version gems are installed under.  RbConfig::CONFIG["ruby_version"]
  # is "1.9.1" for the legacy standard library directory; the gem tree follows
  # RUBY_VERSION instead, as it does on CRuby.

  def self.ruby_api_version
    @ruby_api_version ||= RUBY_VERSION.split(".").first(2).push(0).join(".")
  end

  ##
  # Path for gems in the user's home directory.  Same layout as CRuby, but
  # keyed by engine ("ironruby") and the 4.0.0 API version, so IronRuby's
  # user gems never collide with the host Ruby's.

  def self.user_dir
    gem_dir = File.join(Gem.user_home, ".gem")
    gem_dir = File.join(Gem.data_home, "gem") unless File.exist?(gem_dir)
    File.join(gem_dir, ruby_engine, ruby_api_version)
  end

  ##
  # Gem trees belonging to a matching host CRuby installation.
  #
  # IronRuby is a drop-in for the CRuby release whose standard library it
  # vendors, and pure-Ruby gems installed for that CRuby load unchanged here.
  # Rather than force every user to set GEM_PATH, look for a `ruby` on PATH and
  # offer its <prefix>/lib/ruby/gems/<api version> read-only, when the API
  # version matches ours.  Set IRONRUBY_NO_HOST_GEMS=1 to opt out.

  def self.host_ruby_dirs # :nodoc:
    return @host_ruby_dirs if defined?(@host_ruby_dirs) && @host_ruby_dirs

    @host_ruby_dirs = []
    return @host_ruby_dirs if ENV["IRONRUBY_NO_HOST_GEMS"]

    seen = {}
    (ENV["PATH"] || "").split(File::PATH_SEPARATOR).each do |dir|
      next if dir.empty?

      exe = File.join(dir, "ruby#{RbConfig::CONFIG["EXEEXT"]}")
      next unless File.file?(exe)

      prefix = File.dirname(File.dirname(exe))
      gems = File.join(prefix, "lib", "ruby", "gems", ruby_api_version)
      next unless File.directory?(gems)
      next if seen[gems]

      seen[gems] = true
      @host_ruby_dirs << gems
    end
    @host_ruby_dirs
  rescue StandardError
    @host_ruby_dirs = []
  end

  ##
  # Where the default gems' specifications live.
  #
  # IronRuby's standard library is not gemified, so its own gem tree has no
  # specifications/default.  Fall back to the host CRuby's, so that gems which
  # depend on a gemified stdlib library - parser depends on racc, rspec-core on
  # did_you_mean, ... - resolve instead of raising Gem::MissingSpecError.  A
  # default gem never adds itself to $LOAD_PATH (Specification#add_self_to_load_path
  # returns early for one), so this cannot pull CRuby's library directory in
  # ahead of IronRuby's own.

  def self.default_specifications_dir
    @default_specifications_dir ||= begin
      own = File.join(default_dir, "specifications", "default")
      if Dir.exist?(own) && !Dir.glob(File.join(own, "*.gemspec")).empty?
        own
      else
        host = host_ruby_dirs.map {|d| File.join(d, "specifications", "default") }.find {|d| Dir.exist?(d) }
        host || own
      end
    end
  end

  def self.default_path
    path = []
    path << user_dir if user_home && File.exist?(user_home)
    path << default_dir
    path << vendor_dir if vendor_dir && File.directory?(vendor_dir)
    path.concat(host_ruby_dirs)
    path
  end

  ##
  # The host trees stay on Gem.path even when something narrows GEM_HOME and
  # GEM_PATH - which Bundler does on every command, pinning both to its
  # bundle_path.  On CRuby that loses nothing, because bundle_path defaults to
  # the very directory the gems are in; here it would hide them all.  They are
  # read-only extra sources, like a vendor directory, so appending them is
  # always safe.

  def self.path
    paths.path | host_ruby_dirs
  end

  ##
  # RbConfig.ruby names the `ir` apphost, which starts without the -X:UsePrism
  # and -X:StdLib arguments that make it a Ruby 4 interpreter.  RubyGems and
  # Bundler shell out to Gem.ruby to run binstubs and extconf.rb, so point it
  # at the ir.sh (ir.cmd on Windows) wrapper next to the source tree when there
  # is one.

  def self.ruby
    @ruby ||= begin
      wrapper = nil
      begin
        # <root>/Src/StdLib/ruby/4.0/rubygems/defaults/ironruby.rb -> <root>
        root = File.expand_path("../../../../../..", __dir__)
        name = Gem.win_platform? ? "ir.cmd" : "ir.sh"
        candidate = File.join(root, name)
        wrapper = candidate if File.file?(candidate)
      rescue StandardError
        wrapper = nil
      end

      ruby = wrapper || RbConfig.ruby
      ruby = "\"#{ruby}\"" if /\s/.match?(ruby)
      ruby
    end
  end

  ##
  # Gems whose C extensions IronRuby cannot load, collected while scanning the
  # gem index.  See BasicSpecification#ignored? below.

  def self.ironruby_ignored_gems # :nodoc:
    @ironruby_ignored_gems ||= []
  end

  ##
  # Lazy loading of the host CRuby's default gemspecs.
  #
  # Upstream's Gem::Specification.load_defaults evaluates every gemspec in
  # Gem.default_specifications_dir at boot, purely to feed
  # Gem.register_default_spec - which only wants, per gem, the list of paths
  # that `require` should map to it.  On CRuby that directory holds a handful
  # of specs and the eval is compiled C; here it is the host's 46 default
  # gemspecs, and evaluating them is by far the largest single cost of
  # `require "rubygems"`.
  #
  # Nothing at boot needs the Gem::Specification objects themselves, so the
  # require-name -> gemspec-file mapping is computed once and cached on disk.
  # A later boot restores the mapping from the cache and stores the gemspec's
  # *path* in Gem's map; Gem.find_default_spec - the only reader of that map -
  # evaluates the gemspec the first time a require actually hits it, and then
  # replaces the placeholders through the ordinary register_default_spec, so
  # the map ends up holding exactly what upstream would have put there.
  #
  # Set IRONRUBY_NO_GEM_CACHE=1 to fall back to upstream's eager scan.

  IRONRUBY_DEFAULT_SPEC_INDEX_VERSION = 1 # :nodoc:

  ##
  # The standard library this process is running out of.  A source tree can be
  # checked out many times over - every worktree on this box shares one home
  # directory and one host CRuby - so it is part of the cache's identity: the
  # recorded feature paths point into *this* tree, and requiring another
  # worktree's copy of rubygems/dependency.rb would be a real bug.

  def self.ironruby_lib_dir # :nodoc:
    @ironruby_lib_dir ||= File.expand_path("../..", __dir__)
  end

  def self.ironruby_default_spec_index_file # :nodoc:
    # A plain deterministic checksum: String#hash is salted per process, and
    # requiring digest at boot would cost more than this whole cache saves.
    tag = ironruby_lib_dir.each_byte.inject(17) {|a, b| (a * 31 + b) & 0xffffffff }
    File.join(Gem.cache_home, "ironruby",
              "default-specs-#{ruby_api_version}-#{tag.to_s(36)}.cache")
  end

  ##
  # Cheap fingerprint of the default specifications directory: every gemspec's
  # name, size and mtime.  Installing, removing or updating a default gem
  # changes it, which is exactly when the mapping has to be rebuilt.

  def self.ironruby_default_spec_stamp(files) # :nodoc:
    files.sort.map do |file|
      stat = File.stat(file)
      [File.basename(file), stat.size, stat.mtime.to_i]
    end
  end

  ##
  # Evaluate +file+, a default gemspec, and let upstream's
  # register_default_spec put the real Gem::Specification into the map under
  # every name it owns.  Returns the spec, or nil when the gemspec is bad.

  def self.ironruby_register_default_spec_file(file) # :nodoc:
    spec = Gem::Specification.load(file)
    return nil unless spec

    register_default_spec spec
    spec
  end

  def self.find_default_spec(path) # :nodoc:
    spec = @path_to_default_spec_map[path]
    return spec unless spec.is_a?(String)

    # A placeholder: the gemspec has not been evaluated yet.  Doing so rewrites
    # every entry this gemspec owns, this one included.
    ironruby_register_default_spec_file(spec)
    spec = @path_to_default_spec_map[path]
    spec.is_a?(String) ? nil : spec
  end

  ##
  # Replacement for Gem::Specification.load_defaults.  Uses the on-disk
  # mapping when it is still valid, and otherwise does what upstream does
  # while recording the mapping for next time.

  def self.ironruby_load_default_specs # :nodoc:
    dir = Gem.default_specifications_dir
    files = Gem::Util.glob_files_in_dir("*.gemspec", dir)
    return if files.empty?

    stamp = nil
    cached = nil
    features = nil
    unless ENV["IRONRUBY_NO_GEM_CACHE"]
      begin
        stamp = ironruby_default_spec_stamp(files)
        data = File.open(ironruby_default_spec_index_file, "rb", &:read)
        data = Marshal.load(data)
        if data.is_a?(Hash) &&
           data[:version] == IRONRUBY_DEFAULT_SPEC_INDEX_VERSION &&
           data[:dir] == dir && data[:lib] == ironruby_lib_dir &&
           data[:stamp] == stamp
          cached = data[:map]
          features = data[:features]
        end
      rescue StandardError, NotImplementedError
        cached = nil
      end
    end

    if cached
      # Evaluating the gemspecs pulls a few of RubyGems' own autoloaded files
      # in - a gemspec with a dependency loads rubygems/dependency.rb.  Load
      # them here so that $LOADED_FEATURES is the same either way.
      Array(features).each {|f| require f if File.file?(f) }
      @path_to_default_spec_map.update(cached)
      ironruby_activate_already_loaded_defaults(cached)
      return
    end

    loaded_before = $LOADED_FEATURES.dup

    # Cache miss: do exactly what upstream does, and note which gemspec each
    # registered name came from so the next boot can skip the evaluation.
    origin = {}
    files.each do |file|
      spec = Gem::Specification.load(file)
      next unless spec

      origin[spec.object_id] = file
      register_default_spec spec
    end

    return unless stamp

    map = {}
    @path_to_default_spec_map.each do |name, spec|
      file = origin[spec.object_id]
      map[name] = file if file
    end
    ironruby_write_default_spec_index(dir, stamp, map, $LOADED_FEATURES - loaded_before)
  end

  def self.ironruby_write_default_spec_index(dir, stamp, map, features) # :nodoc:
    return if ENV["IRONRUBY_NO_GEM_CACHE"]

    file = ironruby_default_spec_index_file
    require "fileutils"
    FileUtils.mkdir_p File.dirname(file)
    tmp = "#{file}.#{Process.pid}"
    File.open(tmp, "wb") do |io|
      io.write Marshal.dump(version: IRONRUBY_DEFAULT_SPEC_INDEX_VERSION,
                            dir: dir, lib: ironruby_lib_dir,
                            stamp: stamp, map: map, features: features)
    end
    File.rename tmp, file
  rescue StandardError, NotImplementedError
    begin
      File.delete tmp if tmp && File.exist?(tmp)
    rescue StandardError
      nil
    end
  end

  ##
  # register_default_spec activates a default gem whose file the interpreter
  # has already loaded.  With placeholders in the map that check has to run the
  # other way round - over $LOADED_FEATURES rather than over every name - so
  # that the same gems end up activated without evaluating the other 45
  # gemspecs.

  def self.ironruby_activate_already_loaded_defaults(map) # :nodoc:
    return if $LOADED_FEATURES.empty?

    prefixes = default_gem_load_paths.map {|lp| "#{lp}/" }
    pending = nil
    $LOADED_FEATURES.each do |feature|
      prefixes.each do |prefix|
        next unless feature.start_with?(prefix)

        file = map[feature[prefix.length..-1]]
        next unless file.is_a?(String)

        (pending ||= []) << file
      end
    end
    return unless pending

    pending.uniq.each {|file| ironruby_register_default_spec_file(file) }
  end

  class Specification
    def self.load_defaults # :nodoc:
      Gem.ironruby_load_default_specs
    end
  end

  class BasicSpecification
    ##
    # Upstream marks a gem "ignored" when its extensions are not built for the
    # running Ruby, and Gem::Dependency#matching_specs then rejects it.  On
    # IronRuby a C extension is never built, so that rule would make every gem
    # with an extension - and every gem that merely depends on one - impossible
    # to activate: parser depends on racc, and racc's .so exists only for the
    # host CRuby.  Most such gems carry a pure-Ruby fallback that the extension
    # merely accelerates (racc/parser.rb rescues the LoadError of racc/cparse),
    # and the ones that do not fail at require time with a plain LoadError for
    # the extension file, which is a better answer than refusing to resolve.
    #
    # The names are recorded so `gem env` and -w can report them.

    def ignored?
      if @ignored.nil?
        @ignored = false
        if missing_extensions? && !Gem.ironruby_ignored_gems.include?(full_name)
          Gem.ironruby_ignored_gems << full_name
        end
      end

      @ignored
    end

  end
end
