# An mspec formatter for CI: the usual dots and failure report, plus a record of
# which spec file every failing example belongs to, written as it happens.
#
#   SPEC_RECORD=results/core-io.txt ir -Imspec/lib mspec/bin/mspec-run \
#     -f Util/ci/record_formatter.rb spec/core/io
#
# The record has one line per event, tab-separated:
#
#   LOAD   <file>                  a spec file is about to run
#   FAIL   <file>  <description>   an example failed (SpecExpectationNotMetError)
#   ERROR  <file>  <description>   an example raised anything else
#   DONE   <tally>                 the run reached the end ("12 files, 340 examples, ...")
#
# Lines are flushed one at a time, so a run that hangs or takes the process down
# still says which file it was in, and a missing DONE says it never finished.
# Util/ci/spec-baseline.rb compares records against a checked-in baseline.
# <file> is relative to the working directory, always with forward slashes.
require 'mspec/runner/formatters/dotted'

class RecordFormatter < DottedFormatter
  def initialize(out = nil)
    super(out)
    path = ENV["SPEC_RECORD"] or abort "RecordFormatter: set SPEC_RECORD to the record file"
    dir = File.dirname(path)
    Dir.mkdir(dir) unless File.directory?(dir)
    @record = File.open(path, "w")
    @root = normalize(Dir.pwd) + "/"
    @seen = {}
  end

  def register
    super
    MSpec.register :load, self
  end

  def load
    @file = relative(MSpec.file)
    write "LOAD", @file
  end

  def exception(exception)
    super
    description = exception.description.gsub(/\s*\n\s*/, " / ")
    description = description.gsub(@root, "").gsub(@root.tr("/", "\\"), "")
    key = [@file, description]
    return if @seen[key]
    @seen[key] = true
    write exception.failure? ? "FAIL" : "ERROR", @file, description
  end

  def finish
    super
    write "DONE", @tally.format
    @record.close
  end

  private

  def write(*fields)
    @record.puts fields.join("\t")
    @record.flush
  end

  def normalize(path)
    File.expand_path(path).tr("\\", "/")
  end

  def relative(path)
    path = normalize(path)
    path.downcase.start_with?(@root.downcase) ? path[@root.size..-1] : path
  end
end

CUSTOM_MSPEC_FORMATTER = RecordFormatter
