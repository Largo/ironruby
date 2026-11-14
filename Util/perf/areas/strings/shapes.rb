# String/Symbol/Regexp/IO micro shapes. Usage: ruby shapes.rb [filter]
# Prints name, CPU ms per run (least of 5 after warmup), and a checksum.
require 'stringio'
DIR = ENV['STR_SCRATCH'] || '/root/workspace/strings-scratch'
FILE = File.join(DIR, "shapes-#{$$}.txt")
File.write(FILE, (1..2000).map { |i| "line #{i} some text here, value=#{i * 7}\n" }.join)
BIG = 'a' * 1_000_000 + 'b'
WORDS = (1..200).map { |i| "word#{i}" }.join(' ')
CSV = (1..200).map { |i| "f#{i}" }.join(',')
MIXED = 'Hello World Ünïcödé ' * 20
SYMS = %w[a bb ccc dddd eeeee].map(&:to_sym)
ARR = (1..100).map(&:to_s)
SHAPES = {
  'regex =~'          => [20000, ->{ c = 0; i = 0; while i < 100; c += 1 if "abc123def" =~ /\d+/; i += 1; end; c }],
  'match?'            => [20000, ->{ c = 0; i = 0; while i < 100; c += 1 if "abc123def".match?(/\d+/); i += 1; end; c }],
  'match'             => [20000, ->{ m = nil; i = 0; while i < 100; m = /(\d+)/.match("abc123def"); i += 1; end; m[1] }],
  'dyn regex'         => [2000,  ->{ c = 0; i = 0; while i < 100; c += 1 if "abc#{i}" =~ /c#{i}/; i += 1; end; c }],
  'gsub str'          => [5000,  ->{ s = nil; 100.times { s = 'white chocolate'.gsub('white', 'dark') }; s }],
  'gsub regex'        => [5000,  ->{ s = nil; 100.times { s = 'a1b2c3'.gsub(/\d/, '2') }; s }],
  'gsub block'        => [5000,  ->{ s = nil; 100.times { s = 'dog cat dog'.gsub(/\w+/) { |a| a == 'dog' ? 'cat' : 'dog' } }; s }],
  'sub'               => [5000,  ->{ s = nil; 100.times { s = 'hello world'.sub(/o/, '0') }; s }],
  'scan'              => [500,   ->{ WORDS.scan(/\w+/).size }],
  'split str'         => [2000,  ->{ CSV.split(',').size }],
  'split regex'       => [1000,  ->{ WORDS.split(/\s+/).size }],
  'index big off'     => [200,   ->{ BIG.index('b', 500_000) }],
  'index big'         => [200,   ->{ BIG.index('b') }],
  'start_with?'       => [20000, ->{ c = 0; i = 0; while i < 100; c += 1 if 'hello world'.start_with?('hell'); i += 1; end; c }],
  'include?'          => [20000, ->{ c = 0; i = 0; while i < 100; c += 1 if 'hello world'.include?('wor'); i += 1; end; c }],
  'format'            => [3000,  ->{ s = nil; 100.times { |i| s = format('%05d %s %.2f', i, 'x', 1.5) }; s }],
  '% op'              => [3000,  ->{ s = nil; 100.times { |i| s = '%d-%s' % [i, 'y'] }; s }],
  'interp'            => [5000,  ->{ s = nil; 100.times { |i| s = "a#{i}b#{:c}d" }; s }],
  'upcase/downcase'   => [5000,  ->{ s = nil; 100.times { s = 'Hello World'.upcase.downcase }; s }],
  'each_char'         => [500,   ->{ c = 0; MIXED.each_char { |ch| c += 1 }; c }],
  'chars'             => [500,   ->{ MIXED.chars.size }],
  'bytes'             => [500,   ->{ MIXED.bytes.sum }],
  'pack'              => [3000,  ->{ ARR.map(&:to_i).pack('N*').size }],
  'unpack'            => [3000,  ->{ ("\x00\x01\x02\x03" * 100).unpack('N*').sum }],
  'encode'            => [2000,  ->{ s = nil; 100.times { s = MIXED.encode('UTF-16LE') }; s.bytesize }],
  'force_encoding'    => [20000, ->{ s = nil; 100.times { s = 'abc'.dup.force_encoding('BINARY') }; s }],
  'Symbol#to_proc'    => [3000,  ->{ ARR.map(&:size).sum }],
  'to_sym'            => [5000,  ->{ s = nil; ARR.each { |x| s = x.to_sym }; s }],
  'sym to_s'          => [20000, ->{ s = nil; 100.times { s = SYMS[2].to_s }; s }],
  'String#==' => [20000, ->{ c = 0; i = 0; while i < 100; c += 1 if 'hello world' == 'hello world'; i += 1; end; c }],
  'substring'         => [20000, ->{ s = nil; i = 0; while i < 100; s = 'hello world'[2, 5]; i += 1; end; s }],
  'inspect'           => [3000,  ->{ s = nil; 100.times { s = MIXED[0, 30].inspect }; s }],
  'concat <<'         => [500,   ->{ s = +''; 1000.times { s << 'abc' }; s.size }],
  'File.read'         => [300,   ->{ File.read(FILE).size }],
  'File.readlines'    => [100,   ->{ File.readlines(FILE).size }],
  'File.foreach'      => [100,   ->{ c = 0; File.foreach(FILE) { c += 1 }; c }],
  'File.write'        => [300,   ->{ File.write(FILE + '.w', 'x' * 10000) }],
  'StringIO gets'     => [100,   ->{ io = StringIO.new(File.read(FILE)); c = 0; c += 1 while io.gets; c }],
  'StringIO write'    => [500,   ->{ io = StringIO.new; 1000.times { io.write('abc') }; io.string.size }],
}
filter = ARGV[0] ? Regexp.new(ARGV[0]) : nil
def cpu = Process.clock_gettime(Process::CLOCK_THREAD_CPUTIME_ID)
SHAPES.each do |name, (n, blk)|
  next if filter && name !~ filter
  r = nil
  t0 = cpu; r = blk.call while cpu - t0 < 3.0 # warm up 3 s: past the JIT tiers
  best = 1e9
  5.times { t = cpu; n.times { r = blk.call }; d = cpu - t; best = d if d < best }
  printf "%-18s %9.2f ms  %s\n", name, best * 1000, r.inspect[0, 30]
end
File.delete(FILE) rescue nil
File.delete(FILE + '.w') rescue nil
