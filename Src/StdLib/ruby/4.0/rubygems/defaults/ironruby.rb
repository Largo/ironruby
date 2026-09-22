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
  # A binstub RubyGems writes gets `#!/usr/bin/env <ruby_install_name>` by
  # default, and RbConfig's ruby_install_name here is "ir" - the apphost, which
  # is not on PATH and which would start without the arguments that make it a
  # Ruby 4 interpreter anyway.  Turn the env shebang off for every install, so
  # the line becomes `#!<Gem.ruby>`, the ir.sh wrapper.  That also makes
  # Bundler's CLI::Exec#ruby_shebang? recognise the binstub - it matches
  # "#!#{Gem.ruby}" - so `bundle exec <gem executable>` loads it in this process
  # instead of exec'ing it into whatever `ruby` is on PATH.
  #
  # This is a pre_install hook rather than a reopened Gem::Installer because
  # rubygems/installer.rb is loaded long after this file and would overwrite the
  # method.  A hook that returns false aborts the install, so it returns true.
  pre_install do |installer|
    if installer.respond_to?(:instance_variable_set) && !Gem.configuration[:custom_shebang]
      installer.instance_variable_set(:@env_shebang, false)
    end
    true
  end

  ##
  # Gems whose C extensions IronRuby cannot load, collected while scanning the
  # gem index.  See BasicSpecification#ignored? below.

  def self.ironruby_ignored_gems # :nodoc:
    @ironruby_ignored_gems ||= []
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
