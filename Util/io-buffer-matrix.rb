# Fixtures live in a directory of this run's own. They were under a fixed /tmp/rk, which
# every run shared: run side by side (CRuby and each IronRuby mode, as
# Util/perf/gate-matrices.rb does), one run's File.write truncated the file another was
# reading, and that run printed [] or EOFError where CRuby prints the content.
require 'tmpdir'
require 'fileutils'
RK = Dir.mktmpdir('io-buffer-matrix-')
at_exit { FileUtils.rm_rf(RK) }
def t(label)
  puts "#{label}: #{yield.inspect}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}: #{e.message}"
end
File.write("#{RK}/bsrc.txt", "abcdef")
t("and!") { b = IO::Buffer.new(2); b.set_string("ab"); c = IO::Buffer.new(2); c.set_string("\x0f\x0f".b); [b.and!(c).get_string.bytes, b.get_string.bytes] }
t("or!") { b = IO::Buffer.new(1); b.set_string("\xf0".b); c = IO::Buffer.new(1); c.set_string("\x0f".b); b.or!(c).get_string.bytes }
t("xor!") { b = IO::Buffer.new(1); b.set_string("\xff".b); c = IO::Buffer.new(1); c.set_string("\x0f".b); b.xor!(c).get_string.bytes }
t("not!") { b = IO::Buffer.new(1); b.set_string("\x00".b); b.not!.get_string.bytes }
t("and! non-buffer") { IO::Buffer.new(2).and!("xx") }
t("and! size") { IO::Buffer.new(2).and!(IO::Buffer.new(3)) }
t("write") { f = File.open("#{RK}/bw.txt", "w"); n = IO::Buffer.for(+"hello").write(f, 5); f.close; [n, File.read("#{RK}/bw.txt")] }
t("read") { b = IO::Buffer.new(4); f = File.open("#{RK}/bsrc.txt"); n = b.read(f, 3); f.close; [n, b.get_string(0, 3)] }
t("pread") { b = IO::Buffer.new(4); f = File.open("#{RK}/bsrc.txt"); n = b.pread(f, 2, 3); f.close; [n, b.get_string(0, 3)] }
t("copy") { a = IO::Buffer.new(4); s = IO::Buffer.new(2); s.set_string("xy"); a.copy(s, 0); a.get_string(0, 2) }
t("copy non-buffer") { IO::Buffer.new(4).copy("ab", 0) }
t("resize external") { IO::Buffer.for(+"x").resize(4) }
t("resize internal") { b = IO::Buffer.new(2); b.set_string("ab"); b.resize(4); b.size }
