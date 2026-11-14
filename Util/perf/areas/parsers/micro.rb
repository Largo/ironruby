cpu = -> { Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) }
def t(name, n, cpu)
  3.times { yield }
  best = 1e9
  3.times { s = cpu.(); n.times { yield }; best = [best, cpu.() - s].min }
  printf "%-22s %8.1f ns/op\n", name, best / n * 1e9 / 1000
end
s = "".b
t("str<<int", 2000, cpu) { b = "".b; 1000.times { |i| b << (i & 0x7f) } }
t("str<<str", 2000, cpu) { b = "".b; x = "ab".b; 1000.times { b << x } }
bin = ("\x01\x85\x02" * 400).b
t("getbyte", 2000, cpu) { i = 0; while i < 1000; bin.getbyte(i); i += 1; end }
t("byteslice", 2000, cpu) { i = 0; while i < 1000; bin.byteslice(i, 4); i += 1; end }
t("int ops", 2000, cpu) { i = 0; v = 0; while i < 1000; v = ((i << 7) | (v & 0x7f)) >> 3; i += 1; end }
t("unpack1 E", 2000, cpu) { i = 0; while i < 1000; bin.unpack1("E", offset: i); i += 1; end }
t("force_enc", 2000, cpu) { 1000.times { bin.byteslice(1, 8).force_encoding(Encoding::UTF_8) } }
