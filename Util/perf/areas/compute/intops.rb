# Integer shapes past 2**31 (Int64 tier) vs Fixnum, steady state.
def t(name)
  yield; c0 = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
  r = yield
  printf("%-14s %7.1f ms  %s\n", name, (Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)-c0)*1000, r)
end
N = 2_000_000
t("small-arith") { i=0; s=0; while i<N; s = (s + i*3) & 0xffff; i+=1; end; s }
t("crc32-mask")  { i=0; s=0; while i<N; s = ((s << 5) ^ i ^ (s >> 3)) & 0xffffffff; i+=1; end; s }
t("rgba-pack")   { i=0; s=0; while i<N; r=i&255; s ^= (r << 24) | (r << 16) | (r << 8) | 0xff; i+=1; end; s }
t("rgba-unpack") { i=0; s=0; c=0xff8040ff; while i<N; s += ((c >> 24) & 0xff) + ((c >> 16) & 0xff); i+=1; end; s }
t("i64-mul")     { i=0; s=1; while i<N; s = (s * 31 + i) % 1_000_000_007_000; i+=1; end; s }
t("i64-cmp")     { i=0; s=0; b=5_000_000_000; while i<N; s+=1 if b+i > b; i+=1; end; s }
t("float-mix")   { i=0; s=0.0; while i<N; s += (i * 0.5).floor; i+=1; end; s }
