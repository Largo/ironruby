# Fixtures live in a directory of this run's own. They were under a fixed /tmp/rk, which
# every run shared: run side by side (CRuby and each IronRuby mode, as
# Util/perf/gate-matrices.rb does), one run's File.write truncated the file another was
# reading, and that run printed [] or EOFError where CRuby prints the content.
require 'tmpdir'
require 'fileutils'
RK = Dir.mktmpdir('io-chomp-matrix-')
at_exit { FileUtils.rm_rf(RK) }
def t(label)
  puts "#{label}: #{yield.inspect}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}: #{e.message}"
end
File.write("#{RK}/l.txt", "a\nb\n")
t("readlines chomp") { File.readlines("#{RK}/l.txt", chomp: true) }
t("readlines plain") { File.readlines("#{RK}/l.txt") }
t("gets chomp") { File.open("#{RK}/l.txt") { |g| g.gets(chomp: true) } }
t("gets plain") { File.open("#{RK}/l.txt") { |g| g.gets } }
t("gets sep") { File.open("#{RK}/l.txt") { |g| g.gets("\n") } }
t("readline chomp") { File.open("#{RK}/l.txt") { |g| g.readline(chomp: true) } }
t("io readlines chomp") { File.open("#{RK}/l.txt") { |g| g.readlines(chomp: true) } }
t("io readlines plain") { File.open("#{RK}/l.txt") { |g| g.readlines } }
t("each_line chomp") { File.open("#{RK}/l.txt") { |g| g.each_line(chomp: true).to_a } }
t("each_line block") { r = []; File.open("#{RK}/l.txt") { |g| g.each_line(chomp: true) { |l| r << l } }; r }
t("each_line plain") { File.open("#{RK}/l.txt") { |g| g.each_line.to_a } }
t("each chomp") { File.open("#{RK}/l.txt") { |g| g.each(chomp: true).to_a } }
t("foreach chomp") { IO.foreach("#{RK}/l.txt", chomp: true).to_a }
t("foreach plain") { IO.foreach("#{RK}/l.txt").to_a }
t("foreach block") { r = []; IO.foreach("#{RK}/l.txt", chomp: true) { |l| r << l }; r }
