# frozen_string_literal: true
#
# Regenerates the default gemspecs IronRuby ships for the libraries it
# provides itself:
#
#     Src/StdLib/ruby/gems/4.0.0/specifications/default/<name>-<version>.gemspec
#
# Run it with the built interpreter, from the root of the source tree:
#
#     ./ir.sh Util/gen-default-gemspecs.rb
#
# Why this exists
# ---------------
# RubyGems only knows a library is already present when a *gemspec* says so.
# Without these, `gem install activesupport` resolves `bigdecimal` and `json`
# against rubygems.org, downloads the C sources and tries to compile an
# extension - for libraries IronRuby already implements in C#.  CRuby avoids
# that by gemifying its standard library; JRuby does the same for what it
# implements natively.  This is IronRuby's copy of that list.
#
# The versions below are the versions IronRuby *provides*, not the versions
# CRuby happens to ship:
#
#   * a library vendored verbatim from the CRuby release whose standard
#     library lives in Src/StdLib/ruby/4.0 carries that release's version -
#     it is literally that code;
#   * a library IronRuby implements in C# carries the version its own
#     VERSION constant reports.  The generator re-reads those constants and
#     refuses to write a gemspec that disagrees with the running interpreter,
#     so the table cannot drift away from the implementation.
#
# A library IronRuby does *not* implement must not be listed here.  Leaving it
# out means RubyGems installs the real gem from rubygems.org, which is the
# right answer for anything pure Ruby (logger, csv, base64, racc, ...).

require "fileutils"

ROOT = File.expand_path("..", __dir__)
STDLIB = File.join(ROOT, "Src", "StdLib")

# Search order matches ir.sh's -X:StdLib: IronRuby's own library first.
ROOTS = [
  File.join(STDLIB, "ironruby"),
  File.join(STDLIB, "ruby", "4.0"),
  File.join(STDLIB, "ruby", "1.9.1"),
].freeze

OUT_DIR = File.join(STDLIB, "ruby", "gems", "4.0.0", "specifications", "default")

# name => [version, summary, entries, options]
#
# An entry is a require name.  "json" means the file json.rb; a trailing "/"
# means the whole subtree, "json/" => json/**/*.rb.  An entry the interpreter
# answers without a file on disk (ruby2_keywords is built into the loader)
# is written as [name, :builtin].
#
# :check is the constant whose value must equal the version.  :require is what
# to require before reading it, when that is not the gem's own name.
GEMS = {
  # --- implemented in C# (Src/Libraries) -----------------------------------
  # These are the entries that matter: without them RubyGems compiles C.
  "bigdecimal" => ["4.0.1", "Arbitrary-precision decimal arithmetic",
                   ["bigdecimal", "bigdecimal/"], check: "BigDecimal::VERSION"],
  "date" => ["3.3.4", "Date and DateTime",
             ["date", "date/"], check: "Date::VERSION"],
  "digest" => ["3.2.1", "Message digest libraries",
               ["digest", "digest/"], check: "Digest::VERSION"],
  "etc" => ["1.4.6", "Access to information from the passwd and group files",
            ["etc"], check: "Etc::VERSION"],
  "fcntl" => ["1.3.0", "Constants for fcntl(2) and open(2)",
              ["fcntl"], check: "Fcntl::VERSION"],
  "io-console" => ["0.8.2", "Console size and raw mode",
                   ["io/console", "io/console/"], require: "io/console",
                   check: "IO::ConsoleMode::VERSION"],
  "io-nonblock" => ["0.3.2", "IO#nonblock", ["io/nonblock"], require: "io/nonblock"],
  "io-wait" => ["0.4.0", "IO#wait and friends", ["io/wait"], require: "io/wait"],
  "json" => ["2.18.0", "JSON parsing and generation",
             ["json", "json/"], check: "JSON::VERSION"],
  "openssl" => ["3.2.0", "OpenSSL bindings, on .NET's cryptography stack",
                ["openssl", "openssl/"], check: "OpenSSL::VERSION"],
  "psych" => ["5.3.1", "YAML parser and emitter",
              ["psych", "psych/"], check: "Psych::VERSION"],
  "stringio" => ["3.2.0", "IO on strings",
                 ["stringio"], check: "StringIO::VERSION"],
  "strscan" => ["3.1.6", "Lexical scanning of strings",
                ["strscan"], check: "StringScanner::Version"],
  "zlib" => ["3.2.3", "Deflate/inflate, on System.IO.Compression",
             ["zlib"], check: "Zlib::VERSION"],
  "prism" => ["1.9.0", "The Prism Ruby parser - IronRuby's own front end",
              ["prism", "prism/"], check: "Prism::VERSION"],
  "sqlite3" => ["2.9.6", "SQLite3, on the native library Microsoft.Data.Sqlite carries",
                ["sqlite3", "sqlite3/"], check: "SQLite3::VERSION"],
  # Not a default gem anywhere else: nokogiri is a C extension over libxml2 and
  # gumbo, and neither compiles here.  It is listed because it is a hard
  # dependency of rails-html-sanitizer -> loofah -> actionview and of
  # rails-dom-testing -> actionpack, so without a gemspec saying it is present
  # `gem install actionview` stops at a C compiler.  The version is the nokogiri
  # API level implemented, not a nokogiri release.
  "nokogiri" => ["1.18.0", "HTML5, HTML4 and XML parsing, on AngleSharp",
                 ["nokogiri", "nokogiri/"], check: "Nokogiri::VERSION"],

  # --- vendored from the CRuby release in Src/StdLib/ruby/4.0 --------------
  "bundler" => ["4.0.16", "The best way to manage a Ruby application's gems",
                ["bundler", "bundler/"], require: false, bin: ["bundle", "bundler"]],
  "delegate" => ["0.6.1", "Delegation of method calls", ["delegate"]],
  "did_you_mean" => ["2.0.0", "Did you mean? experience",
                     ["did_you_mean", "did_you_mean/"]],
  "english" => ["0.8.1", "Readable aliases for the special variables", ["English"]],
  "erb" => ["6.0.1.1", "An easy-to-use but powerful templating system",
            ["erb", "erb/"], check: "ERB::VERSION"],
  # IronRuby answers the error_highlight API, but ErrorHighlight.spot always
  # returns nil - see Src/StdLib/ironruby/error_highlight.rb for why.
  "error_highlight" => ["0.7.2", "The error_highlight API; spot always answers nil here",
                        ["error_highlight", "error_highlight/"]],
  "fileutils" => ["1.8.0", "Several file utility methods for copying, moving, removing, etc.",
                  ["fileutils"], check: "FileUtils::VERSION"],
  "find" => ["0.2.0", "Traverse a file tree", ["find"]],
  "forwardable" => ["1.4.0", "Provides delegation of specified methods to a designated object",
                    ["forwardable", "forwardable/"]],
  "ipaddr" => ["1.2.8", "A class to manipulate an IP address", ["ipaddr"]],
  # railties depends on irb, and irb on reline.  Without these two, installing
  # railties resolves irb against rubygems.org, which drags in rdoc 8 and then
  # rbs - a C extension - for a library IronRuby already ships and already runs
  # as `ir -S irb`.
  "irb" => ["1.16.0", "Interactive Ruby", ["irb", "irb/"], require: false, bin: ["irb"]],
  "reline" => ["0.6.3", "GNU Readline and Editline, in pure Ruby",
               ["reline", "reline/"], require: false],
  "net-http" => ["0.9.1", "HTTP client api for Ruby", ["net/http", "net/https", "net/http/"],
                 require: "net/http"],
  "net-protocol" => ["0.2.2", "The abstract interface for net-* client",
                     ["net/protocol"], require: "net/protocol"],
  "open3" => ["0.2.1", "Popen, but with stderr, too", ["open3"]],
  "open-uri" => ["0.5.0", "An easy-to-use wrapper for Net::HTTP and Net::FTP",
                 ["open-uri"], require: "open-uri"],
  "optparse" => ["0.8.1", "OptionParser is a class for command-line option analysis",
                 ["optparse", "optparse/", "optionparser"]],
  "pp" => ["0.6.3", "Pretty-prints Ruby objects", ["pp"]],
  "prettyprint" => ["0.2.0", "Implements a pretty printing algorithm for readable structure",
                    ["prettyprint"]],
  "resolv" => ["0.7.0", "Thread-aware DNS resolver library", ["resolv", "resolv-replace"]],
  "ruby2_keywords" => ["0.0.5", "Shim library for Module#ruby2_keywords",
                       [["ruby2_keywords", :builtin]], require: false],
  "securerandom" => ["0.4.1", "Interface for secure random number generator", ["securerandom"]],
  "shellwords" => ["0.2.2", "Manipulates strings with word parsing rules of UNIX Bourne shell",
                   ["shellwords"]],
  "singleton" => ["0.3.0", "The Singleton module implements the Singleton pattern",
                  ["singleton"]],
  "syntax_suggest" => ["2.0.3", "Find syntax errors in your source in a snap",
                       ["syntax_suggest", "syntax_suggest/"], require: false],
  "tempfile" => ["0.3.1", "A utility class for managing temporary files",
                 ["tempfile"], check: "Tempfile::VERSION"],
  "time" => ["0.4.2", "Extends the Time class with methods for parsing and conversion",
             ["time"]],
  "timeout" => ["0.6.0", "Auto-terminate potentially long-running operations",
                ["timeout"], check: "Timeout::VERSION"],
  "tmpdir" => ["0.3.1", "Extends the Dir class to manage the OS temporary file path",
               ["tmpdir"]],
  "tsort" => ["0.2.0", "Topological sorting using Tarjan's algorithm", ["tsort"]],
  "un" => ["0.3.0", "Utilities to replace common UNIX commands", ["un"]],
  "uri" => ["1.1.1", "URI is a module providing classes to handle Uniform Resource Identifiers",
            ["uri", "uri/"], check: "URI::VERSION"],
  "weakref" => ["0.1.4", "Allows a referenced object to be garbage-collected", ["weakref"]],
  "yaml" => ["0.4.0", "YAML Ain't Markup Language", ["yaml", "yaml/"]],
}.freeze

def entry_files(entry)
  name, kind = entry.is_a?(Array) ? entry : [entry, nil]
  return ["lib/#{name}.rb"] if kind == :builtin

  found = []
  ROOTS.each do |root|
    if name.end_with?("/")
      Dir.glob(File.join(root, name, "**", "*.rb")).sort.each do |path|
        found << "lib/" + path[(root.length + 1)..-1]
      end
    else
      path = File.join(root, "#{name}.rb")
      found << "lib/#{name}.rb" if File.file?(path)
    end
  end
  found
end

# A default gem's executables live in <gem_dir>/<bindir>, which on CRuby is
# lib/ruby/gems/<api>/gems/<name>-<version>/exe.  Gem.bin_path("bundler",
# "bundle") is what `rails new` runs to install an application's gems, so that
# directory has to exist and hold a runnable file.
#
# IronRuby keeps the executables it ships in one place instead - Src/StdLib/bin,
# the directory ir.sh puts on RUBYPATH, so that `ir -S bundle` finds them - so
# what goes in the gem's exe/ is a one-line loader of the real binstub.  Both
# are written here, next to the gemspec that names them, so a version bump
# cannot leave a stale directory behind.
BIN_DIR = "exe"
GEMS_DIR = File.join(STDLIB, "ruby", "gems", "4.0.0", "gems")
SHARED_BIN_DIR = File.join(STDLIB, "bin")

def write_executables(name, version, executables)
  dir = File.join(GEMS_DIR, "#{name}-#{version}", BIN_DIR)
  FileUtils.mkdir_p dir

  executables.each do |exe|
    target = File.join(SHARED_BIN_DIR, exe)
    warn "gen-default-gemspecs: #{target} does not exist" unless File.file?(target)

    path = File.join(dir, exe)
    File.write(path, <<~RUBY)
      #!/usr/bin/env ruby
      # frozen_string_literal: true
      #
      # Generated by Util/gen-default-gemspecs.rb.  Do not edit by hand.
      #
      # Gem.bin_path("#{name}", "#{exe}") answers this file, because that is where
      # RubyGems looks for a default gem's executables.  The binstub itself is
      # Src/StdLib/bin/#{exe}, the one `ir -S #{exe}` runs, and this loads it so
      # that there is only ever one copy to keep working.
      load File.expand_path("../../../../../../bin/#{exe}", __dir__)
    RUBY
    File.chmod(0o755, path)
  end
end

def gemspec_source(name, version, summary, files, executables)
  lines = []
  lines << "# -*- encoding: utf-8 -*-"
  lines << "# stub: #{name} #{version} ruby lib"
  lines << "#"
  lines << "# Generated by Util/gen-default-gemspecs.rb.  Do not edit by hand."
  lines << ""
  lines << "Gem::Specification.new do |s|"
  lines << "  s.name = #{name.dump}.freeze"
  lines << "  s.version = #{version.dump}"
  lines << ""
  lines << "  s.required_rubygems_version = Gem::Requirement.new(\">= 0\".freeze)"
  lines << "  s.require_paths = [\"lib\".freeze]"
  lines << "  s.authors = [\"IronRuby\".freeze]"
  lines << "  s.description = #{"#{summary}.  Provided by IronRuby itself.".dump}.freeze"
  lines << "  s.summary = #{summary.dump}.freeze"
  lines << "  s.homepage = \"https://github.com/IronLanguages/ironruby\".freeze"
  lines << "  s.licenses = [\"Ruby\".freeze, \"BSD-2-Clause\".freeze]"
  unless executables.empty?
    lines << "  s.bindir = #{BIN_DIR.dump}.freeze"
    lines << "  s.executables = [#{executables.map {|e| "#{e.dump}.freeze" }.join(", ")}]"
  end
  lines << "  s.files = ["
  files.each {|f| lines << "    #{f.dump}.freeze," }
  lines << "  ]"
  lines << "end"
  lines.join("\n") + "\n"
end

# Cross-check the table against the running interpreter.
mismatches = []
GEMS.each do |name, (version, _summary, _entries, opts)|
  opts ||= {}
  const = opts[:check]
  next unless const

  feature = opts.key?(:require) ? opts[:require] : name
  begin
    require feature if feature
    actual = const.split("::").inject(Object) {|mod, part| mod.const_get(part) }.to_s
  rescue StandardError, LoadError, NameError => e
    mismatches << "#{name}: cannot read #{const} (#{e.class}: #{e.message})"
    next
  end
  mismatches << "#{name}: #{const} is #{actual}, table says #{version}" unless actual == version
end

unless mismatches.empty?
  warn "gen-default-gemspecs: the table disagrees with this interpreter:"
  mismatches.each {|m| warn "  #{m}" }
  exit 1
end

FileUtils.mkdir_p OUT_DIR
Dir.glob(File.join(OUT_DIR, "*.gemspec")).each {|f| File.delete(f) }

GEMS.each do |name, (version, summary, entries, opts)|
  opts ||= {}
  executables = Array(opts[:bin])
  files = entries.flat_map {|e| entry_files(e) }.uniq
  if files.empty?
    warn "gen-default-gemspecs: #{name} matched no files - skipping"
    next
  end

  write_executables(name, version, executables) unless executables.empty?

  path = File.join(OUT_DIR, "#{name}-#{version}.gemspec")
  File.write(path, gemspec_source(name, version, summary, files, executables))
  puts "#{name}-#{version} (#{files.length} files)"
end
