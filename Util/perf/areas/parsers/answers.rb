# The answers the kept changes could affect, to diff against CRuby:
# protoboeuf-encode's bytes (SHA-256) and RuboCop's autocorrect of ruby-bench's fixture (offences + corrected source).
require "digest"
dir = "/root/workspace/ruby-bench/benchmarks/protoboeuf-encode"
def make_shareable(x) = x
require File.join(dir, "benchmark_pb")
lots = Marshal.load(File.binread(File.join(dir, "encoded_msgs.bin"))).map { |b| ProtoBoeuf::ParkingLot.decode(b) }
3.times { lots.each { |l| ProtoBoeuf::ParkingLot.encode(l) } }   # past the rule/JIT thresholds
enc = lots.map { |l| ProtoBoeuf::ParkingLot.encode(l) }
puts "protoboeuf-encode #{enc.sum(&:bytesize)} bytes #{Digest::SHA256.hexdigest(enc.join)} #{enc.map(&:encoding).uniq}"
if ARGV[0] == "rubocop"
  Dir.chdir("/root/workspace/parsers-scratch/ruby-bench/benchmarks/rubocop")
  require "bundler/setup"; require "stringio"; require "rubocop"
  opts = RuboCop::Options.new.parse(%w[--stderr --force-exclusion --format json --raise-cop-error --autocorrect]).first
  path = File.expand_path("fixture.rb")
  3.times do |i|
    out = StringIO.new; $stdout = out
    opts[:stdin] = File.read(path)
    RuboCop::Runner.new(opts, RuboCop::ConfigStore.new).run([path])
    $stdout = STDOUT
    j = JSON.parse(out.string)
    offs = j["files"].flat_map { |f| f["offenses"].map { |o| [o["cop_name"], o["location"]["line"], o["location"]["column"], o["message"], o["corrected"]] } }
    puts "rubocop run #{i}: #{offs.size} offences #{Digest::SHA256.hexdigest(offs.inspect)[0, 16]} corrected-src #{Digest::SHA256.hexdigest(opts[:stdin].to_s)[0, 16]}"
  end
end
