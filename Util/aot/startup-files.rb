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
#
# It also writes startup-evals.txt: the same commands run again with IR_EVAL_RECORD, which records every plain string eval
# compiled without a warning (the source text and everything EvalCodeCache keys on), and the library compiles each one for
# the first eval of exactly that text in exactly that context (PrecompiledCode.TryClaimEval). --check compares them too.
require "open3"

ROOT = File.expand_path("../..", __dir__)
IR_SH = File.join(ENV["IR_TREE"] || ROOT, "ir.sh")
LIST = File.join(__dir__, "startup-files.txt")
EVALS = File.join(__dir__, "startup-evals.txt")
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

# The string evals the commands run, recorded by the runtime (IR_EVAL_RECORD: PrecompiledCode.RecordEval), with the tree's
# root as ${ROOT}: the library compiles each for the first eval of exactly that text in exactly that context. A second pass
# after the files', so that RubyGems' caches are written and only what a usual run evaluates is recorded.
require "json"
require "tmpdir"
def recorded_evals(args, stdin)
  tree = File.dirname(IR_SH)
  Dir.mktmpdir do |dir|
    record = File.join(dir, "evals")
    env = { "IR_STARTUP_DLL" => "none", "IR_EVAL_RECORD" => record, "TERM" => "xterm" }
    cmd = [IR_SH, *args]
    _, out, status = Open3.capture3(env, *cmd, stdin_data: stdin || "")
    abort "startup-files.rb: #{cmd.join(" ")} failed:\n#{out}" unless status.success?
    next [] unless File.exist?(record)
    File.readlines(record, chomp: true).map do |l|
      e = JSON.parse(l)
      e["path"] = e["path"].gsub(tree + "/", "${ROOT}/")
      JSON.generate(e)
    end
  end
end

sections = []
seen = {}
COMMANDS.each do |title, args, stdin|
  files = loaded_files(args, stdin).reject { |f| seen[f] || EXCLUDED.key?(f) }
  files.each { |f| seen[f] = true }
  sections << [title, files]
end
evals = []
COMMANDS.each { |_, args, stdin| evals.concat(recorded_evals(args, stdin)) }
evals.uniq!
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
  listed_evals = File.exist?(EVALS) ? File.readlines(EVALS, chomp: true).reject { |l| l.empty? || l.start_with?("#") } : []
  describe = ->(l) { e = JSON.parse(l); "#{e["path"]}:#{e["line"]} (#{e["code"].unpack1("m").bytesize} bytes)" }
  (listed_evals - evals).each { |l| puts "startup-evals.txt has an eval none of the commands runs: #{describe[l]}" }
  (evals - listed_evals).each { |l| puts "startup-evals.txt lacks an eval the commands run: #{describe[l]}" }
  if missing.empty? && extra.empty? && listed_evals.sort == evals.sort
    puts "ok: startup-files.txt lists the #{listed.size} files the commands load, startup-evals.txt their #{evals.size} evals"
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
File.open(EVALS, "w") do |io|
  io.puts "# Written by startup-files.rb with startup-files.txt: the string evals those commands run, one JSON object a line"
  io.puts "# (PrecompiledCode.RecordEval), compiled into IronRuby.Startup.dll for the first eval of exactly that text in exactly"
  io.puts "# that context. ${ROOT} is the IronRuby tree."
  evals.each { |l| io.puts l }
end
puts "wrote #{EVALS}: #{evals.size} evals"
