# Runs one gem: the load check, then whatever test suite is on disk.
# Invoked once per gem by Util/gems/run.sh, which reads the ##GEM## line and the
# framework's own "N tests, N assertions, ..." summary out of the log.
#
#   ir.sh -X:ObjectSpace Util/gems/runner.rb <gem-name>
#   ir.sh Util/gems/runner.rb --load-path <gem-name>   # the -I arguments to use

require_relative 'catalog'

if ARGV[0] == '--load-path'
  entry = GemCatalog::ENTRIES[ARGV[1]] or abort "unknown gem: #{ARGV[1]}"
  puts GemCatalog.load_path(ARGV[1], entry).map { |d| "-I#{d}" }.join(' ')
  exit
end

name = ARGV.shift or abort 'usage: runner.rb <gem-name>'
entry = GemCatalog::ENTRIES[name] or abort "unknown gem: #{name}"

GemCatalog.load_path(name, entry).reverse_each { |d| $LOAD_PATH.unshift(d) }

# --- load check ------------------------------------------------------------
load_state = 'ok'
note = entry[:note]
if entry[:require]
  begin
    require entry[:require]
  rescue Exception => e
    load_state = 'FAIL'
    note = "#{e.class}: #{e.message.to_s.split("\n").first}"
  end
end

files = load_state == 'ok' ? GemCatalog.test_files(entry) : []

# run.sh reads the suite kind: "unit" means a summary line follows, "spec" means
# it has to drive mspec itself, and "none" means there is nothing to run.
kind = if !files.empty?
         'unit'
       elsif load_state == 'ok' && entry[:specs]
         'spec'
       else
         'none'
       end

puts "##GEM##\t#{name}\t#{load_state}\t#{kind}\t#{entry[:specs]}\t#{note}"
$stdout.flush
exit!(load_state == 'ok' ? 0 : 1) if files.empty?

# --- run the suite ---------------------------------------------------------
if entry[:framework] == :minitest
  require 'minitest/autorun'
  $LOAD_PATH.unshift(File.join(File.dirname(File.dirname(files.first)), 'test'))
  files.each do |f|
    begin
      require File.expand_path(f)
    rescue Exception => e
      warn "load #{f}: #{e.class}: #{e.message}"
    end
  end
else
  $LOAD_PATH.unshift(File.join(GemCatalog::RUBY_SRC, 'tool', 'lib'))
  $LOAD_PATH.unshift(File.join(GemCatalog::RUBY_SRC, 'test', 'lib'))
  require 'test/unit'

  # LeakChecker walks the heap with ObjectSpace.each_object(IO) / (File).  The CLR
  # has no heap walk, so IronRuby cannot answer that - and a leak check is not what
  # these suites are here to measure.
  class LeakChecker
    def initialize(*); end
    def check(*); end
  end

  Dir.chdir(GemCatalog::RUBY_SRC)
  files.each do |f|
    begin
      require File.expand_path(f)
    rescue Exception => e
      warn "load #{f}: #{e.class}: #{e.message}"
    end
  end
end
# Both frameworks install an at_exit hook that runs the tests and prints the
# summary line run.sh parses.
