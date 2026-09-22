# irubyc -- IronRuby's ahead-of-time compiler, the analogue of JRuby's jrubyc.
#
# WHAT IT DOES, PLAINLY
#
#   irubyc packages Ruby sources into a self-contained .NET application: a
#   generated C# host is compiled (by the .NET SDK) into an assembly that
#   carries the Ruby files as *embedded managed resources*, next to IronRuby's
#   own assemblies, libprism and a copy of the standard library.  The result
#   runs without the source tree -- no .rb file is written to the output.
#
#   It does NOT emit IL for the Ruby code.  The Ruby is still parsed and
#   compiled by IronRuby at startup, exactly as `ir app.rb` would.  Emitting IL
#   to disk needs LambdaExpression.CompileToMethod, which .NET Core removed and
#   .NET 10 still does not have (PersistedAssemblyBuilder can save an assembly,
#   but nothing can turn a DLR expression tree into a MethodBuilder any more).
#   So this is a deployment compiler, not a code generator; see README.md.
#
#   The embedded tree behaves like a read-only directory at <app>/src: require,
#   require_relative, load, File.read, Dir.glob and Dir.entries all see it,
#   because those go through the DLR's PlatformAdaptationLayer.  File.exist?
#   and the rest of FileTest do not -- they stat the real file system.
#
# jrubyc's option names are kept where they mean the same thing (-t/--target,
# -d/--dir, -p/--prefix, --verbose); the rest are IronRuby's own.

require "optparse"
require "fileutils"
require "rbconfig"

module IronRuby
  module Compiler
    VERSION = "0.1"

    class Options
      attr_accessor :target, :basedir, :prefix, :main, :name, :app_host,
                    :self_contained, :rid, :stdlib, :verbose, :keep, :configuration
      attr_reader :sources

      def initialize
        @target = "."
        @basedir = nil
        @prefix = nil
        @main = nil
        @name = nil
        @app_host = true          # --dll turns this off
        @self_contained = false
        @rid = nil
        @stdlib = true
        @verbose = false
        @keep = false
        @configuration = "Release"
        @sources = []
      end
    end

    class CompileError < StandardError; end

    module_function

    def compile_argv(argv)
      options = parse_options(argv)
      return 0 if options == :done      # --help / --version
      return 1 if options.nil?
      compile(options)
      0
    rescue CompileError => e
      warn "irubyc: #{e.message}"
      1
    end

    def parse_options(argv)
      options = Options.new
      parser = OptionParser.new do |opts|
        opts.banner = <<~BANNER
          Usage: irubyc [options] <script.rb> [more.rb|dir ...]

          Packages Ruby sources into a runnable .NET application.  The sources are
          embedded in the output assembly as resources and are compiled by IronRuby
          at startup -- irubyc does not emit IL for the Ruby code itself.

          The first file given is the main script unless --main says otherwise; the
          rest (and every .rb under any directory given) are embedded too and can be
          required by name.  Needs the .NET SDK on the machine that runs irubyc.
        BANNER

        opts.on("-t", "--target DIR", "Directory to write the application into (default: .)") { |d| options.target = d }
        opts.on("-d", "--dir BASEDIR", "Base directory the embedded paths are relative to") { |d| options.basedir = d }
        opts.on("-p", "--prefix PREFIX", "Prefix prepended to every embedded path") { |p| options.prefix = p }
        opts.on("--main FILE", "Script to run at startup (default: the first file)") { |f| options.main = f }
        opts.on("-o", "--output NAME", "Name of the application (default: the main script's basename)") { |n| options.name = n }
        opts.on("--exe", "Produce a native launcher next to the assembly (default)") { options.app_host = true }
        opts.on("--dll", "Produce only the assembly; run it with `dotnet <name>.dll`") { options.app_host = false }
        opts.on("--self-contained [RID]", "Bundle the .NET runtime too (needs the runtime packs)") do |rid|
          options.self_contained = true
          options.rid = rid
        end
        opts.on("--no-stdlib", "Leave out Lib/ruby (the MRI library and gems); keeps the prelude") { options.stdlib = false }
        opts.on("--debug", "Compile the host with the Debug configuration") { options.configuration = "Debug" }
        opts.on("--keep", "Keep the generated C# project and say where it is") { options.keep = true }
        opts.on("--verbose", "Print each step, including the dotnet command line") { options.verbose = true }
        opts.on("--version", "Print the irubyc version and exit") do
          puts "irubyc #{VERSION} (#{RUBY_DESCRIPTION})"
          return :done
        end
        opts.on("-h", "--help", "Show this message") do
          puts opts
          return :done
        end
      end

      rest = parser.parse(argv)
      if rest.empty?
        warn parser.help
        return nil
      end
      options.sources.concat(rest)
      options
    end

    # --- source collection -------------------------------------------------

    def collect_sources(options)
      files = []
      options.sources.each do |arg|
        if File.directory?(arg)
          found = Dir.glob(File.join(arg, "**", "*.rb")).sort
          raise CompileError, "no .rb files under #{arg}" if found.empty?
          files.concat(found)
        elsif File.file?(arg)
          files << arg
        else
          raise CompileError, "no such file or directory -- #{arg}"
        end
      end
      files = files.map { |f| File.expand_path(f) }.uniq
      raise CompileError, "nothing to compile" if files.empty?
      files
    end

    def main_file(options, files)
      main = options.main ? File.expand_path(options.main) : files.first
      unless files.include?(main)
        raise CompileError, "--main #{options.main} is not among the files being compiled"
      end
      main
    end

    # The base directory embedded paths are relative to: -d if given, otherwise
    # the deepest directory that contains every source.
    def base_dir(options, files)
      return File.expand_path(options.basedir) if options.basedir
      dirs = files.map { |f| File.dirname(f).split(File::SEPARATOR) }
      common = dirs.first.each_with_index.take_while { |part, i| dirs.all? { |d| d[i] == part } }.map(&:first)
      common.empty? ? File::SEPARATOR : common.join(File::SEPARATOR)
    end

    def embedded_path(file, base, prefix)
      rel = file.sub(/\A#{Regexp.escape(base)}#{Regexp.escape(File::SEPARATOR)}?/, "")
      rel = rel.tr(File::ALT_SEPARATOR || "\\", "/")
      prefix && !prefix.empty? ? File.join(prefix, rel) : rel
    end

    # --- the .NET side -----------------------------------------------------

    def bin_dir
      dir = RbConfig::CONFIG["bindir"]
      unless File.file?(File.join(dir, "IronRuby.dll"))
        raise CompileError, "cannot find IronRuby.dll next to the running ir (looked in #{dir})"
      end
      dir
    end

    def lib_dir
      RbConfig::CONFIG["libdir"]
    end

    def dotnet
      candidates = []
      candidates << ENV["DOTNET"] if ENV["DOTNET"]
      candidates << File.join(ENV["DOTNET_ROOT"], "dotnet" + RbConfig::CONFIG["EXEEXT"]) if ENV["DOTNET_ROOT"]
      candidates << "dotnet#{RbConfig::CONFIG["EXEEXT"]}"
      candidates.each do |c|
        return c if c.include?(File::SEPARATOR) ? File.file?(c) : which(c)
      end
      raise CompileError, "the .NET SDK is required to compile; `dotnet` was not found " \
                          "(set DOTNET or DOTNET_ROOT)"
    end

    def which(cmd)
      (ENV["PATH"] || "").split(File::PATH_SEPARATOR).any? { |d| File.executable?(File.join(d, cmd)) }
    end

    def runtime_identifier
      System::Runtime::InteropServices::RuntimeInformation.runtime_identifier.to_s
    rescue StandardError, NameError
      raise CompileError, "--self-contained needs a runtime identifier; pass one, e.g. --self-contained=linux-x64"
    end

    REFERENCES = %w[
      IronRuby IronRuby.Libraries IronRuby.Prism
      Microsoft.Scripting Microsoft.Dynamic System.CodeDom
    ].freeze

    # Resource names are prefixed so that a compiled app's own resources cannot
    # collide with anything IronRuby's assemblies carry.
    RESOURCE_PREFIX = "irubyc.src/"

    def csproj(name, options, resources, bin)
      refs = REFERENCES.map { |r|
        %(    <Reference Include="#{r}"><HintPath>#{File.join(bin, r + ".dll")}</HintPath></Reference>)
      }.join("\n")
      res = resources.map { |file, logical|
        %(    <EmbeddedResource Include="#{file}" LogicalName="#{logical}" />)
      }.join("\n")

      <<~XML
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <OutputType>Exe</OutputType>
            <AssemblyName>#{name}</AssemblyName>
            <RootNamespace>IronRuby.Compiled</RootNamespace>
            <Nullable>disable</Nullable>
            <UseAppHost>#{options.app_host}</UseAppHost>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>
            <GenerateDocumentationFile>false</GenerateDocumentationFile>
            <InvariantGlobalization>false</InvariantGlobalization>
          </PropertyGroup>
          <ItemGroup>
            <!-- Ruby exposes UTF-7, and .NET's flock(2)-based FileShare emulation collides
                 with File#flock; ir's own host sets both, so a compiled app must too. -->
            <RuntimeHostConfigurationOption Include="System.Text.Encoding.EnableUnsafeUTF7Encoding" Value="true" />
            <RuntimeHostConfigurationOption Include="System.IO.DisableFileLocking" Value="true" />
          </ItemGroup>
          <ItemGroup>
            <Compile Include="apphost.cs" />
          </ItemGroup>
          <ItemGroup>
        #{res}
          </ItemGroup>
          <ItemGroup>
        #{refs}
          </ItemGroup>
        </Project>
      XML
    end

    def apphost_source(main_path, resources)
      template = File.read(File.expand_path("compiler/apphost.cs", __dir__))
      # Path.Combine understands '/' on both platforms, so keep the portable form.
      manifest = resources.map { |_file, logical|
        embedded = logical.sub(%r{\A#{Regexp.escape(RESOURCE_PREFIX)}}, "")
        %(            { @"#{embedded}", @"#{logical}" },)
      }
      template
        .sub("@@MAIN_PATH@@", main_path)
        .sub("@@MANIFEST@@", manifest.join("\n"))
    end

    # --- the compile itself ------------------------------------------------

    def compile(options)
      files = collect_sources(options)
      main = main_file(options, files)
      base = base_dir(options, files)
      bin = bin_dir

      resources = files.map { |f|
        rel = embedded_path(f, base, options.prefix)
        [f, RESOURCE_PREFIX + rel]
      }
      main_path = embedded_path(main, base, options.prefix)
      name = options.name || File.basename(main, ".rb")

      out = File.expand_path(File.join(options.target, name))
      work = File.expand_path("#{name}.irubyc", options.keep ? options.target : tmp_root)

      say options, "main script: #{main_path}"
      resources.each { |f, logical| say options, "embedding:   #{logical.sub(RESOURCE_PREFIX, "")} (#{f})" }

      check_syntax(options, files)

      FileUtils.rm_rf(work)
      FileUtils.mkdir_p(work)
      File.write(File.join(work, "apphost.cs"), apphost_source(main_path, resources))
      File.write(File.join(work, "#{name}.csproj"), csproj(name, options, resources, bin))

      run_dotnet(options, work, name, out)

      copy_runtime(options, bin, out)
      copy_stdlib(options, out)

      FileUtils.rm_rf(work) unless options.keep

      launcher = options.app_host ? File.join(out, name) : "dotnet #{File.join(out, name + ".dll")}"
      puts "irubyc: wrote #{out}"
      puts "irubyc: run it with #{launcher}"
      if options.keep
        puts "irubyc: generated project kept in #{work}"
      end
    end

    # The Ruby is not compiled here, so nothing would otherwise notice a broken file
    # until the application ran. Ripper is prism, the same parser IronRuby uses, so a
    # file that parses here parses there. Ripper cannot be subclassed in IronRuby
    # (RipperOps is sealed), so this gets the fact of an error but not its line.
    def check_syntax(options, files)
      require "ripper"
      bad = files.reject { |f| Ripper.sexp(File.read(f), f) }
      unless bad.empty?
        raise CompileError, "syntax error in #{bad.join(", ")} " \
                            "(run `ir #{bad.first}` for the location)"
      end
      say options, "syntax:      #{files.size} file(s) parse"
    rescue LoadError
      say options, "syntax:      ripper unavailable, skipping the check"
    end

    def tmp_root
      ENV["TMPDIR"] || ENV["TMP"] || ENV["TEMP"] || "/tmp"
    end

    def run_dotnet(options, work, name, out)
      project = File.join(work, "#{name}.csproj")
      cmd = [dotnet]
      if options.self_contained
        rid = options.rid || runtime_identifier
        cmd += ["publish", project, "-c", options.configuration, "-r", rid, "--self-contained", "true"]
      else
        cmd += ["build", project, "-c", options.configuration]
      end
      cmd += ["-o", out, "--nologo"]

      say options, "running: #{cmd.join(" ")}"
      if options.verbose
        ok = system(*cmd)
      else
        # Quiet on success, but the SDK's diagnostics are the only useful thing
        # to show when the generated host does not compile, so keep them back
        # rather than throw them away.
        output = IO.popen(cmd + ["-v", "quiet"], err: [:child, :out], &:read)
        ok = $?.success?
        print output unless ok
      end
      raise CompileError, "the .NET SDK failed to compile the generated host" unless ok
    end

    # Everything the runtime needs that the SDK did not already copy: the DLLs the
    # host does not reference directly (the YAML library is load_assembly'd from
    # yaml.rb) and libprism, which is a native library.
    def copy_runtime(options, bin, out)
      Dir.glob(File.join(bin, "*")).each do |src|
        next unless File.file?(src)
        base = File.basename(src)
        next unless base =~ /\.(dll|so|dylib)\z/
        next if base.start_with?("ir.")
        dest = File.join(out, base)
        next if File.file?(dest)
        say options, "copying:     #{base}"
        FileUtils.cp(src, dest)
      end
      copy_native_assets(options, bin, out)
    end

    # A NuGet package's native part does not sit next to the assemblies: it is under
    # runtimes/<rid>/native, and the app resolves it through its own deps.json.  The
    # generated host has a deps.json of its own that knows nothing about those
    # packages, so the files are flattened next to the executable instead, where
    # DllImport's default probing finds them.  This is what makes `require "sqlite3"`
    # work in a packaged application: libe_sqlite3.so comes from
    # Microsoft.Data.Sqlite's SQLitePCLRaw dependency.
    def copy_native_assets(options, bin, out)
      rid = begin
        System::Runtime::InteropServices::RuntimeInformation.runtime_identifier.to_s
      rescue StandardError, NameError
        nil
      end
      return if rid.nil? || rid.empty?

      # linux-x64 also matches what a package built for the portable RID ships.
      dirs = [File.join(bin, "runtimes", rid, "native")]
      dirs << File.join(bin, "runtimes", rid.sub(/-.*/, "") + "-x64", "native") if rid !~ /-/
      dirs.each do |dir|
        next unless File.directory?(dir)
        Dir.glob(File.join(dir, "*")).each do |src|
          next unless File.file?(src)
          dest = File.join(out, File.basename(src))
          next if File.file?(dest)
          say options, "copying:     #{File.basename(src)} (native, #{rid})"
          FileUtils.cp(src, dest)
        end
      end
    end

    # rbconfig.rb works out prefix/libdir from its own location, so a copy named
    # "Lib" next to the executable makes the bundle look like an installed IronRuby.
    #
    # Some of the library is not optional: the runtime loads ironruby/ruby4.rb as its
    # prelude and pulls ruby/1.9.1 files (rational18.rb and friends) in while booting.
    # --no-stdlib therefore drops the vendored Ruby 4.0 tree and the installed gems --
    # the 12 MB of it -- and keeps what IronRuby itself needs to start.
    ALWAYS_COPIED = ["ironruby", File.join("ruby", "1.9.1")].freeze
    STDLIB_COPIED = [File.join("ruby", "4.0"), File.join("ruby", "gems"),
                     File.join("ruby", "site_ruby")].freeze

    def copy_stdlib(options, out)
      lib = lib_dir
      dest = File.join(out, "Lib")
      subs = ALWAYS_COPIED + (options.stdlib ? STDLIB_COPIED : [])
      say options, "copying:     #{subs.join(", ")} from #{lib}"
      FileUtils.rm_rf(dest)
      subs.each do |sub|
        source = File.join(lib, sub)
        next unless File.directory?(source)
        target = File.join(dest, sub)
        FileUtils.mkdir_p(File.dirname(target))
        FileUtils.cp_r(source, target)
      end
    end

    def say(options, message)
      puts "irubyc: #{message}" if options.verbose
    end
  end
end
