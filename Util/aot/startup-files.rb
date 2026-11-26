# Writes startup-files.txt, the list of Ruby files compiled into the startup library (IronRuby.Startup.dll,
# README.md part D), from what a set of commands actually loads, or checks that it is current:
#
#   ruby Util/aot/startup-files.rb            # run the commands, rewrite startup-files.txt
#   ruby Util/aot/startup-files.rb --check    # exit 1 and name the files the commands load that the list lacks
#
# Each command runs with the library off (IR_STARTUP_DLL=none) and IR_STARTUP_DLL_TRACE=1, which names every Ruby file
# the loader compiles from source; the list is the files of this tree's Src/StdLib that any command compiles, in the
# order they were first loaded, minus EXCLUDED and the files the compiler would leave out for what they use (refinements:
# `IronRuby.Aot unsupported`, AotCompiler.UnsupportedFinder; it needs the compiler built, as build-startup.sh does). A file
# is listed under the first command that loads it. The commands run with this tree's ir.sh, or IR_TREE's (IR_CONFIG as
# set, else what ir.sh picks: the R2R host and the Release build load the same files).
require "open3"

ROOT = File.expand_path("../..", __dir__)
IR_SH = File.join(ENV["IR_TREE"] || ROOT, "ir.sh")
LIST = File.join(__dir__, "startup-files.txt")
STDLIB = File.join(ROOT, "Src/StdLib/")

# [title, ir arguments, stdin]. TERM is set (xterm), so irb's reline loads its ANSI terminal code, as at a terminal.
COMMANDS = [
  ["ir -e 1", %w[-e 1]],
  ["commonly required standard library",
   ["-e", %w[json set stringio strscan io/console date optparse pp tempfile delegate time].map { |l| "require #{l.inspect}" }.join("; ")]],
  ["gem --version", %w[-S gem --version]],
  ["gem list", %w[-S gem list]],
  ["irb (echo exit | iirb)", %w[-S irb], "exit\n"],
]

# Files a command loads that stay out, and why
EXCLUDED = {
  "ruby/4.0/fileutils.rb" => "an exception re-raised through a rescue clause (FileUtils.mkdir_p) loses a frame in compiled code",
  "ruby/4.0/tmpdir.rb" => "an exception re-raised through a rescue clause (Dir::Tmpname.create) loses a frame in compiled code",
}

# {file => why} for the files the compiler leaves out of a library for what they use
def unsupported(files)
  aot = File.join(ROOT, "Util/aot/IronRuby.Aot/bin/Release/net10.0/IronRuby.Aot")
  abort "startup-files.rb: no #{aot}; build it (Util/aot/build-startup.sh)" unless File.executable?(aot)
  env = {}
  env["DOTNET_ROOT"] = "/usr/local/dotnet" if !ENV["DOTNET_ROOT"] && File.executable?("/usr/local/dotnet/dotnet")
  out, err, status = Open3.capture3(env, aot, "unsupported", *files.map { |f| File.join(STDLIB, f) })
  abort "startup-files.rb: #{aot} unsupported failed:\n#{err}" unless status.success?
  out.lines(chomp: true).to_h { |l| l.split(": ", 2) }.transform_keys { |f| f.delete_prefix(STDLIB) }
end

def loaded_files(args, stdin)
  env = { "IR_STARTUP_DLL" => "none", "IR_STARTUP_DLL_TRACE" => "1", "TERM" => "xterm" }
  cmd = [IR_SH, *args]
  _, out, status = Open3.capture3(env, *cmd, stdin_data: stdin || "")
  abort "startup-files.rb: #{cmd.join(" ")} failed:\n#{out}" unless status.success?
  files = out.scan(/^IronRuby\.Startup: compiling (.*)$/).flatten
  abort "startup-files.rb: #{cmd.join(" ")} compiled nothing from source; is IR_STARTUP_DLL_TRACE supported?" if files.empty?
  stdlib = File.join(File.dirname(IR_SH), "Src/StdLib/")
  files.filter_map { |f| f.delete_prefix(stdlib) if f.start_with?(stdlib) }
end

sections = []
seen = {}
COMMANDS.each do |title, args, stdin|
  files = loaded_files(args, stdin).reject { |f| seen[f] || EXCLUDED.key?(f) }
  files.each { |f| seen[f] = true }
  sections << [title, files]
end
LEFT_OUT = unsupported(seen.keys)
LEFT_OUT.each_key { |f| seen.delete(f) }
sections.each { |_, files| files.reject! { |f| LEFT_OUT.key?(f) } }

if ARGV.include?("--check")
  listed = File.readlines(LIST, chomp: true).reject { |l| l.strip.empty? || l.start_with?("#") }
                .map { |l| File.expand_path(l, __dir__).delete_prefix(STDLIB) }
  missing = seen.keys - listed
  extra = listed - seen.keys
  extra.each { |f| puts "startup-files.txt lists #{f}, which none of the commands loads" }
  missing.each { |f| puts "startup-files.txt lacks #{f}, which the commands load" }
  if missing.empty? && extra.empty?
    puts "ok: startup-files.txt lists the #{listed.size} files the commands load"
    exit 0
  end
  puts "rewrite it with: ruby Util/aot/startup-files.rb"
  exit 1
end

File.open(LIST, "w") do |io|
  io.puts "# Written by startup-files.rb (ruby Util/aot/startup-files.rb rewrites it, --check checks it): the Ruby files these"
  io.puts "# commands load, in the order they first load them, compiled into IronRuby.Startup.dll by IronRuby.Aot startup."
  io.puts "# Paths are relative to this file. Left out:"
  EXCLUDED.merge(LEFT_OUT).each { |f, why| io.puts "#   #{f}: #{why}" }
  sections.each do |title, files|
    io.puts "# #{title}"
    files.each { |f| io.puts "../../Src/StdLib/#{f}" }
  end
end
puts "wrote #{LIST}: #{seen.size} files"
