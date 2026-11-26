# Util/perf/instructions.rb's Instructions, for a run under Util/perf/callgrind/count.sh: the first read of a section
# starts callgrind's instrumentation, the second dumps it and answers the main thread's count. Loaded first (-r), it
# takes the place of the real one, which the shape files then require in vain: the tree's, and the one beside the
# shape file being run (a shape file of another tree).
$LOADED_FEATURES << File.expand_path(ENV["CG_REAL"]) if ENV["CG_REAL"]
$LOADED_FEATURES << File.expand_path("../perf/instructions.rb", File.dirname(File.expand_path($0)))
require 'fiddle'
module Instructions
  def self.open
    lib = Fiddle.dlopen(ENV['CG_LIB'])
    @start = Fiddle::Function.new(lib['cg_start'], [], Fiddle::TYPE_VOID)
    @stop = Fiddle::Function.new(lib['cg_stop'], [], Fiddle::TYPE_VOID)
    @dir = ENV['CG_DIR']
    @on = false
    self
  rescue StandardError, LoadError
    nil
  end

  def self.read
    if @on
      @stop.call
      @on = false
      # --separate-threads=yes: the main thread's file of this dump is cg.PID.N-01
      files = Dir[File.join(@dir, "cg.#{Process.pid}.*-*")]
      main = files.find { |f| f.end_with?('-01') }
      count = main && File.foreach(main).grep(/^(totals|summary):/).first&.split&.at(1)&.to_i
      files.each { |f| File.delete(f) }
      count
    else
      @on = true
      @start.call
      0
    end
  end
end
