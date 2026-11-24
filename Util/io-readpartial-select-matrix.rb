# Fixtures live in a directory of this run's own. They were under a fixed /tmp/rk, which
# every run shared: run side by side (CRuby and each IronRuby mode, as
# Util/perf/gate-matrices.rb does), one run's File.write truncated the file another was
# reading, and that run printed [] or EOFError where CRuby prints the content.
require 'tmpdir'
require 'fileutils'
RK = Dir.mktmpdir('io-readpartial-select-matrix-')
at_exit { FileUtils.rm_rf(RK) }
def t(label)
  puts "#{label}: #{yield.inspect}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}"
end
File.write("#{RK}/rp.txt", "hello")
t("readpartial") { f = File.open("#{RK}/rp.txt"); r = [f.readpartial(3), f.readpartial(3)]; f.close; r }
t("readpartial eof") { f = File.open("#{RK}/rp.txt"); f.read; begin; f.readpartial(3); ensure; f.close; end }
t("readpartial buf") { g = File.open("#{RK}/rp.txt"); b = +"xx"; r = [g.readpartial(2, b), b]; g.close; r }
t("readpartial buf identity") { g = File.open("#{RK}/rp.txt"); b = +"xx"; r = b.equal?(g.readpartial(2, b)); g.close; r }
t("readpartial 0") { g = File.open("#{RK}/rp.txt"); r = g.readpartial(0); g.close; r }
t("readpartial neg") { g = File.open("#{RK}/rp.txt"); begin; g.readpartial(-1); ensure; g.close; end }
t("readpartial enc") { File.open("#{RK}/rp.txt") { |h| h.readpartial(3).encoding } }
t("select read") { f = File.open("#{RK}/rp.txt"); r = IO.select([f]); f.close; r.class }
t("select timeout") { f = File.open("#{RK}/rp.txt"); r = IO.select([f], [], [], 0); f.close; r[0].size }
t("select write") { IO.select(nil, [STDOUT])[1].size }
t("select none") { IO.select([], [], [], 0) }
t("select bad") { IO.select("x") }
