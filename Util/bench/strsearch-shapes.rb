# Searching a String (ledger gap 21): index, rindex, include?, start_with? and byteindex with a
# one- and a three-character needle, and Regexp#match?, =~ and #match, on 10 KB receivers that are
# ASCII (a), UTF-8 with non-ASCII characters (u) and BINARY with high bytes (b), the needle found
# early (e, byte 5), late (l, 30 bytes from the end) or not at all (n). ns per call of a method
# on H, the caller a `while` loop; the sum of the results is the checksum.
#
#   ruby Util/perf/ab.rb -i Util/bench/strsearch-shapes.rb "base=env IR_CONFIG=Release ./ir.sh" \
#        "new=env IR_CONFIG=Release ../wt-strsearch/ir.sh" -- idx1_a_n idx1_u_n mp_u_n
#   ruby Util/bench/strsearch-shapes.rb --list
#
# The methods were generated once from a table and are written out, one per shape (a method made by
# define_method costs more than what is measured). Controls: size, eq, short_idx.
$stdout.sync = true

FILL_A = "abcdefghij" * 1000
FILL_U = "abcdéfghîj" * 910            # 11 bytes, 10 characters
FILL_B = ("abcd\xE9fghij" * 1000).b
def place(fill, at, s) = (f = fill.dup; f[at, s.size] = s; f)
def lateat(fill) = fill.size - 30
R = {}
%i[a u b].each do |k|
  fill = { a: FILL_A, u: FILL_U, b: FILL_B }[k]
  q = k == :b ? 'q"z'.b : 'q"z'
  R[[k, :e]] = place(fill, 5, q)
  R[[k, :l]] = place(fill, lateat(fill), q)
  R[[k, :n]] = fill.dup
end
Q1 = '"'
Q3 = 'q"z'
Q1B = '"'.b
Q3B = 'q"z'.b
RE = /[<>"]/
RE3 = /q"z/
RE_INT = /\A[+-]?\d+/
SHORT = "12345 rest"

class H
  def initialize
    @ae = R[[:a, :e]]
    @al = R[[:a, :l]]
    @an = R[[:a, :n]]
    @ue = R[[:u, :e]]
    @ul = R[[:u, :l]]
    @un = R[[:u, :n]]
    @be = R[[:b, :e]]
    @bl = R[[:b, :l]]
    @bn = R[[:b, :n]]
    @short = SHORT.dup
    @other = FILL_A.dup
  end
  def idx1_a_e = (@ae.index(Q1) || -1)
  def idx3_a_e = (@ae.index(Q3) || -1)
  def inc1_a_e = (@ae.include?(Q1) ? 1 : 0)
  def inc3_a_e = (@ae.include?(Q3) ? 1 : 0)
  def bidx1_a_e = (@ae.byteindex(Q1) || -1)
  def ridx1_a_e = (@ae.rindex(Q1) || -1)
  def ridx3_a_e = (@ae.rindex(Q3) || -1)
  def mp_a_e = (RE.match?(@ae) ? 1 : 0)
  def tilde_a_e = (RE =~ @ae || -1)
  def match_a_e = ((m = RE.match(@ae)) ? m.begin(0) : -1)
  def mp3_a_e = (RE3.match?(@ae) ? 1 : 0)
  def idxre_a_e = (@ae.index(RE) || -1)
  def idx1_a_l = (@al.index(Q1) || -1)
  def idx3_a_l = (@al.index(Q3) || -1)
  def inc1_a_l = (@al.include?(Q1) ? 1 : 0)
  def inc3_a_l = (@al.include?(Q3) ? 1 : 0)
  def bidx1_a_l = (@al.byteindex(Q1) || -1)
  def ridx1_a_l = (@al.rindex(Q1) || -1)
  def ridx3_a_l = (@al.rindex(Q3) || -1)
  def mp_a_l = (RE.match?(@al) ? 1 : 0)
  def tilde_a_l = (RE =~ @al || -1)
  def match_a_l = ((m = RE.match(@al)) ? m.begin(0) : -1)
  def mp3_a_l = (RE3.match?(@al) ? 1 : 0)
  def idxre_a_l = (@al.index(RE) || -1)
  def idx1_a_n = (@an.index(Q1) || -1)
  def idx3_a_n = (@an.index(Q3) || -1)
  def inc1_a_n = (@an.include?(Q1) ? 1 : 0)
  def inc3_a_n = (@an.include?(Q3) ? 1 : 0)
  def bidx1_a_n = (@an.byteindex(Q1) || -1)
  def ridx1_a_n = (@an.rindex(Q1) || -1)
  def ridx3_a_n = (@an.rindex(Q3) || -1)
  def mp_a_n = (RE.match?(@an) ? 1 : 0)
  def tilde_a_n = (RE =~ @an || -1)
  def match_a_n = ((m = RE.match(@an)) ? m.begin(0) : -1)
  def mp3_a_n = (RE3.match?(@an) ? 1 : 0)
  def idxre_a_n = (@an.index(RE) || -1)
  def sw1_a = (@ae.start_with?(Q1) ? 1 : 0)
  def sw3_a = (@ae.start_with?("abcde") ? 1 : 0)
  def idx1_u_e = (@ue.index(Q1) || -1)
  def idx3_u_e = (@ue.index(Q3) || -1)
  def inc1_u_e = (@ue.include?(Q1) ? 1 : 0)
  def inc3_u_e = (@ue.include?(Q3) ? 1 : 0)
  def bidx1_u_e = (@ue.byteindex(Q1) || -1)
  def ridx1_u_e = (@ue.rindex(Q1) || -1)
  def ridx3_u_e = (@ue.rindex(Q3) || -1)
  def mp_u_e = (RE.match?(@ue) ? 1 : 0)
  def tilde_u_e = (RE =~ @ue || -1)
  def match_u_e = ((m = RE.match(@ue)) ? m.begin(0) : -1)
  def mp3_u_e = (RE3.match?(@ue) ? 1 : 0)
  def idxre_u_e = (@ue.index(RE) || -1)
  def idx1_u_l = (@ul.index(Q1) || -1)
  def idx3_u_l = (@ul.index(Q3) || -1)
  def inc1_u_l = (@ul.include?(Q1) ? 1 : 0)
  def inc3_u_l = (@ul.include?(Q3) ? 1 : 0)
  def bidx1_u_l = (@ul.byteindex(Q1) || -1)
  def ridx1_u_l = (@ul.rindex(Q1) || -1)
  def ridx3_u_l = (@ul.rindex(Q3) || -1)
  def mp_u_l = (RE.match?(@ul) ? 1 : 0)
  def tilde_u_l = (RE =~ @ul || -1)
  def match_u_l = ((m = RE.match(@ul)) ? m.begin(0) : -1)
  def mp3_u_l = (RE3.match?(@ul) ? 1 : 0)
  def idxre_u_l = (@ul.index(RE) || -1)
  def idx1_u_n = (@un.index(Q1) || -1)
  def idx3_u_n = (@un.index(Q3) || -1)
  def inc1_u_n = (@un.include?(Q1) ? 1 : 0)
  def inc3_u_n = (@un.include?(Q3) ? 1 : 0)
  def bidx1_u_n = (@un.byteindex(Q1) || -1)
  def ridx1_u_n = (@un.rindex(Q1) || -1)
  def ridx3_u_n = (@un.rindex(Q3) || -1)
  def mp_u_n = (RE.match?(@un) ? 1 : 0)
  def tilde_u_n = (RE =~ @un || -1)
  def match_u_n = ((m = RE.match(@un)) ? m.begin(0) : -1)
  def mp3_u_n = (RE3.match?(@un) ? 1 : 0)
  def idxre_u_n = (@un.index(RE) || -1)
  def sw1_u = (@ue.start_with?(Q1) ? 1 : 0)
  def sw3_u = (@ue.start_with?("abcde") ? 1 : 0)
  def idx1_b_e = (@be.index(Q1B) || -1)
  def idx3_b_e = (@be.index(Q3B) || -1)
  def inc1_b_e = (@be.include?(Q1B) ? 1 : 0)
  def inc3_b_e = (@be.include?(Q3B) ? 1 : 0)
  def bidx1_b_e = (@be.byteindex(Q1B) || -1)
  def ridx1_b_e = (@be.rindex(Q1B) || -1)
  def ridx3_b_e = (@be.rindex(Q3B) || -1)
  def mp_b_e = (RE.match?(@be) ? 1 : 0)
  def tilde_b_e = (RE =~ @be || -1)
  def match_b_e = ((m = RE.match(@be)) ? m.begin(0) : -1)
  def mp3_b_e = (RE3.match?(@be) ? 1 : 0)
  def idxre_b_e = (@be.index(RE) || -1)
  def idx1_b_l = (@bl.index(Q1B) || -1)
  def idx3_b_l = (@bl.index(Q3B) || -1)
  def inc1_b_l = (@bl.include?(Q1B) ? 1 : 0)
  def inc3_b_l = (@bl.include?(Q3B) ? 1 : 0)
  def bidx1_b_l = (@bl.byteindex(Q1B) || -1)
  def ridx1_b_l = (@bl.rindex(Q1B) || -1)
  def ridx3_b_l = (@bl.rindex(Q3B) || -1)
  def mp_b_l = (RE.match?(@bl) ? 1 : 0)
  def tilde_b_l = (RE =~ @bl || -1)
  def match_b_l = ((m = RE.match(@bl)) ? m.begin(0) : -1)
  def mp3_b_l = (RE3.match?(@bl) ? 1 : 0)
  def idxre_b_l = (@bl.index(RE) || -1)
  def idx1_b_n = (@bn.index(Q1B) || -1)
  def idx3_b_n = (@bn.index(Q3B) || -1)
  def inc1_b_n = (@bn.include?(Q1B) ? 1 : 0)
  def inc3_b_n = (@bn.include?(Q3B) ? 1 : 0)
  def bidx1_b_n = (@bn.byteindex(Q1B) || -1)
  def ridx1_b_n = (@bn.rindex(Q1B) || -1)
  def ridx3_b_n = (@bn.rindex(Q3B) || -1)
  def mp_b_n = (RE.match?(@bn) ? 1 : 0)
  def tilde_b_n = (RE =~ @bn || -1)
  def match_b_n = ((m = RE.match(@bn)) ? m.begin(0) : -1)
  def mp3_b_n = (RE3.match?(@bn) ? 1 : 0)
  def idxre_b_n = (@bn.index(RE) || -1)
  def sw1_b = (@be.start_with?(Q1B) ? 1 : 0)
  def sw3_b = (@be.start_with?("abcde") ? 1 : 0)
  def int_match = ((m = RE_INT.match(@short)) ? m.end(0) : -1)
  def int_mp = (RE_INT.match?(@short) ? 1 : 0)
  def int_tilde = (RE_INT =~ @short || -1)
  def short_idx = (@short.index(" ") || -1)
  def size = @other.size
  def eq = (@other == @short ? 1 : 0)
end

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
# INSTR=1 (Util/perf/ab.rb -i) adds a column: the instructions of a call, from the least of the
# seven sections as the time is. See Util/perf/instructions.rb.
INSTR = ENV["INSTR"] && (require_relative "../perf/instructions"; Instructions.open || warn("no instruction counter (perf_event_open)"))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; fewest = nil; counted = !!INSTR; sum = nil
  7.times do
    c0 = INSTR && INSTR.read; t0 = cpu; sum = yield n; d = cpu - t0; c1 = INSTR && INSTR.read
    best = d if d < best
    if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end
h = H.new
# a shape over a whole 10 KB receiver takes a microsecond or more; one that stops early or is
# short, about a hundred nanoseconds
LONG = (ENV["N"] || 100_000).to_i
SHORTN = LONG * 10
# CRuby takes 9 to 60 us for a character class over the whole of a 10 KB String, IronRuby under one:
# the count is the long one so that IronRuby's sections are long enough to time.
REGEX = LONG

t("idx1_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.idx1_a_e; i += 1; end; s }
t("idx3_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.idx3_a_e; i += 1; end; s }
t("inc1_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.inc1_a_e; i += 1; end; s }
t("inc3_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.inc3_a_e; i += 1; end; s }
t("bidx1_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.bidx1_a_e; i += 1; end; s }
t("ridx1_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.ridx1_a_e; i += 1; end; s }
t("ridx3_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.ridx3_a_e; i += 1; end; s }
t("mp_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.mp_a_e; i += 1; end; s }
t("tilde_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.tilde_a_e; i += 1; end; s }
t("match_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.match_a_e; i += 1; end; s }
t("mp3_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.mp3_a_e; i += 1; end; s }
t("idxre_a_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.idxre_a_e; i += 1; end; s }
t("idx1_a_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx1_a_l; i += 1; end; s }
t("idx3_a_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx3_a_l; i += 1; end; s }
t("inc1_a_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc1_a_l; i += 1; end; s }
t("inc3_a_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc3_a_l; i += 1; end; s }
t("bidx1_a_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.bidx1_a_l; i += 1; end; s }
t("ridx1_a_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx1_a_l; i += 1; end; s }
t("ridx3_a_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx3_a_l; i += 1; end; s }
t("mp_a_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp_a_l; i += 1; end; s }
t("tilde_a_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.tilde_a_l; i += 1; end; s }
t("match_a_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.match_a_l; i += 1; end; s }
t("mp3_a_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp3_a_l; i += 1; end; s }
t("idxre_a_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.idxre_a_l; i += 1; end; s }
t("idx1_a_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx1_a_n; i += 1; end; s }
t("idx3_a_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx3_a_n; i += 1; end; s }
t("inc1_a_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc1_a_n; i += 1; end; s }
t("inc3_a_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc3_a_n; i += 1; end; s }
t("bidx1_a_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.bidx1_a_n; i += 1; end; s }
t("ridx1_a_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx1_a_n; i += 1; end; s }
t("ridx3_a_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx3_a_n; i += 1; end; s }
t("mp_a_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp_a_n; i += 1; end; s }
t("tilde_a_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.tilde_a_n; i += 1; end; s }
t("match_a_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.match_a_n; i += 1; end; s }
t("mp3_a_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp3_a_n; i += 1; end; s }
t("idxre_a_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.idxre_a_n; i += 1; end; s }
t("sw1_a", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.sw1_a; i += 1; end; s }
t("sw3_a", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.sw3_a; i += 1; end; s }
t("idx1_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.idx1_u_e; i += 1; end; s }
t("idx3_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.idx3_u_e; i += 1; end; s }
t("inc1_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.inc1_u_e; i += 1; end; s }
t("inc3_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.inc3_u_e; i += 1; end; s }
t("bidx1_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.bidx1_u_e; i += 1; end; s }
t("ridx1_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.ridx1_u_e; i += 1; end; s }
t("ridx3_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.ridx3_u_e; i += 1; end; s }
t("mp_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.mp_u_e; i += 1; end; s }
t("tilde_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.tilde_u_e; i += 1; end; s }
t("match_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.match_u_e; i += 1; end; s }
t("mp3_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.mp3_u_e; i += 1; end; s }
t("idxre_u_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.idxre_u_e; i += 1; end; s }
t("idx1_u_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx1_u_l; i += 1; end; s }
t("idx3_u_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx3_u_l; i += 1; end; s }
t("inc1_u_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc1_u_l; i += 1; end; s }
t("inc3_u_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc3_u_l; i += 1; end; s }
t("bidx1_u_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.bidx1_u_l; i += 1; end; s }
t("ridx1_u_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx1_u_l; i += 1; end; s }
t("ridx3_u_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx3_u_l; i += 1; end; s }
t("mp_u_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp_u_l; i += 1; end; s }
t("tilde_u_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.tilde_u_l; i += 1; end; s }
t("match_u_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.match_u_l; i += 1; end; s }
t("mp3_u_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp3_u_l; i += 1; end; s }
t("idxre_u_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.idxre_u_l; i += 1; end; s }
t("idx1_u_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx1_u_n; i += 1; end; s }
t("idx3_u_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx3_u_n; i += 1; end; s }
t("inc1_u_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc1_u_n; i += 1; end; s }
t("inc3_u_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc3_u_n; i += 1; end; s }
t("bidx1_u_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.bidx1_u_n; i += 1; end; s }
t("ridx1_u_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx1_u_n; i += 1; end; s }
t("ridx3_u_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx3_u_n; i += 1; end; s }
t("mp_u_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp_u_n; i += 1; end; s }
t("tilde_u_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.tilde_u_n; i += 1; end; s }
t("match_u_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.match_u_n; i += 1; end; s }
t("mp3_u_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp3_u_n; i += 1; end; s }
t("idxre_u_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.idxre_u_n; i += 1; end; s }
t("sw1_u", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.sw1_u; i += 1; end; s }
t("sw3_u", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.sw3_u; i += 1; end; s }
t("idx1_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.idx1_b_e; i += 1; end; s }
t("idx3_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.idx3_b_e; i += 1; end; s }
t("inc1_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.inc1_b_e; i += 1; end; s }
t("inc3_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.inc3_b_e; i += 1; end; s }
t("bidx1_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.bidx1_b_e; i += 1; end; s }
t("ridx1_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.ridx1_b_e; i += 1; end; s }
t("ridx3_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.ridx3_b_e; i += 1; end; s }
t("mp_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.mp_b_e; i += 1; end; s }
t("tilde_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.tilde_b_e; i += 1; end; s }
t("match_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.match_b_e; i += 1; end; s }
t("mp3_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.mp3_b_e; i += 1; end; s }
t("idxre_b_e", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.idxre_b_e; i += 1; end; s }
t("idx1_b_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx1_b_l; i += 1; end; s }
t("idx3_b_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx3_b_l; i += 1; end; s }
t("inc1_b_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc1_b_l; i += 1; end; s }
t("inc3_b_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc3_b_l; i += 1; end; s }
t("bidx1_b_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.bidx1_b_l; i += 1; end; s }
t("ridx1_b_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx1_b_l; i += 1; end; s }
t("ridx3_b_l", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx3_b_l; i += 1; end; s }
t("mp_b_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp_b_l; i += 1; end; s }
t("tilde_b_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.tilde_b_l; i += 1; end; s }
t("match_b_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.match_b_l; i += 1; end; s }
t("mp3_b_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp3_b_l; i += 1; end; s }
t("idxre_b_l", REGEX) { |n| i = 0; s = 0; while i < n; s += h.idxre_b_l; i += 1; end; s }
t("idx1_b_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx1_b_n; i += 1; end; s }
t("idx3_b_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.idx3_b_n; i += 1; end; s }
t("inc1_b_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc1_b_n; i += 1; end; s }
t("inc3_b_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.inc3_b_n; i += 1; end; s }
t("bidx1_b_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.bidx1_b_n; i += 1; end; s }
t("ridx1_b_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx1_b_n; i += 1; end; s }
t("ridx3_b_n", LONG) { |n| i = 0; s = 0; while i < n; s += h.ridx3_b_n; i += 1; end; s }
t("mp_b_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp_b_n; i += 1; end; s }
t("tilde_b_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.tilde_b_n; i += 1; end; s }
t("match_b_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.match_b_n; i += 1; end; s }
t("mp3_b_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.mp3_b_n; i += 1; end; s }
t("idxre_b_n", REGEX) { |n| i = 0; s = 0; while i < n; s += h.idxre_b_n; i += 1; end; s }
t("sw1_b", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.sw1_b; i += 1; end; s }
t("sw3_b", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.sw3_b; i += 1; end; s }
t("int_match", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.int_match; i += 1; end; s }
t("int_mp", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.int_mp; i += 1; end; s }
t("int_tilde", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.int_tilde; i += 1; end; s }
t("short_idx", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.short_idx; i += 1; end; s }
t("size", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.size; i += 1; end; s }
t("eq", SHORTN) { |n| i = 0; s = 0; while i < n; s += h.eq; i += 1; end; s }
