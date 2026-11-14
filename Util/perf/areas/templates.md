# Perf area: templates and text generation (2026-11-14)

Baseline: PR #216 head b6cd208a (`wt-perfbase`), Release net10.0. Oracle: CRuby 4.0.9.

## How it was measured

The box ran ten agents at once (load 35-50 on 8 cores), so wall clock was useless:
ruby-bench's own per-iteration times swung 3-40x. `templates/cpu-itr.sh` runs a
ruby-bench gem benchmark with the harness's `realtime` replaced by process CPU time
(all threads, so the background JIT and GC count), `WARMUP_ITRS=0`, a fixed number of
iterations. "first" is iteration #1 (cold), "steady" the mean of the second half.
The answers: every one of these benchmarks checks its own output (erubi the rendered
size, liquid-render the 165689 rendered bytes, ...) and all ran to completion.

## Ratio table (baseline, CPU ms per iteration, 4 iterations)

| benchmark      | CRuby steady | IronRuby first | IronRuby steady | ratio steady |
|----------------|-------------:|---------------:|----------------:|-------------:|
| erubi          |          600 |          60306 |           56666 |     **94x**  |
| mail           |          343 |          17462 |            3619 |        10.6x |
| liquid-render  |          540 |           7478 |            3996 |         7.4x |
| liquid-compile |          173 |           5386 |             890 |         5.1x |
| erubi-rails    |         4704 |          27082 |           16681 |         3.5x |
| liquid-il      |         1149 |          15542 |            2473 |         2.2x |
| etanni         |         1624 |          11124 |            3393 |         2.1x |

(The ~3.8x for erubi in pure-gems.md was measured before something on the load path
compared a `' '` literal with a US-ASCII one - see below; today it is 94x.)

## Where erubi's time went

One sentence: every `_buf << 'literal'` into the output buffer allocated about the whole
buffer, because the buffer was byte-backed and `Utils.Append(ref byte[], string, ...)`
handed `Encoding.GetBytes` the entire free tail of the doubled array as the destination
span; `EscapingEncoding` does not override the span overloads, and .NET's base
implementation converts through a temporary `byte[]` as long as the destination.
So each append cost O(buffer): 248 MB allocated and ~45 gen0 GCs per 166 KB render
(perf: 78% of samples in libcoreclr under `BinaryContent.AppendBytes(string)`).

Why the buffer was byte-backed only under the benchmark: `::String.new` is BINARY;
appending a char-backed literal keeps it char-backed. But `uri/common.rb` (loaded by
`Bundler.setup` through the vendored URI) runs, in a `# frozen_string_literal: true` file,
`TBLENCWWWCOMP_[' '] = '+'`, whose key compares the UTF-8 literal `' '` with the US-ASCII
`-(32.chr)`. `StringContent.ReverseOrdinalCompareTo(BinaryContent)` switches the literal
to bytes - and frozen literals are deduplicated, so from then on every `' '` literal in
the program (erubi's template emits `_buf << ' '.freeze`) is byte-backed, and the first
append of one switches the output buffer to bytes for good. Reproduced in 5 lines
(`Util/perf/areas/templates/byte-buffer-append.rb`): without bundler erubi was ~2x, with
it 94x.

## Kept changes

1. `c243ab8b` Utils.Append gives GetBytes exactly the room being written.
   3000 appends of a 10-char literal to a binary buffer: 20.2 MB -> 1.6 MB allocated.
   erubi render after the `' '` switch (5 renders): 1.48 s -> 0.16 s CPU, 225 -> 2 gen0 GCs.
2. `56d91723` EscapingEncoding overrides `GetByteCount(ReadOnlySpan<char>)` and
   `GetBytes(ReadOnlySpan<char>, Span<byte>)` (inner encoding when there are no
   surrogates, as its string/char[] overloads already did): 1.6 MB -> 0.68 MB.

ruby-bench, CPU ms/iteration steady (cold first iteration in brackets):

| benchmark   | baseline        | candidate      | CRuby | ratio now |
|-------------|----------------:|---------------:|------:|----------:|
| erubi       | 56666 (60306)   | 1845 (12470)   |   600 |  3.1x (was 94x) |
| erubi-rails | 16681 (27082)   | 15634 (24350)  |  4704 |  3.3x (noise) |
| mail        | 3619 (17462)    | 3073 (22756)   |   343 |  noise |
| etanni      | 3393            | 3471           |  1624 |  noise |

Correctness: mspec core/string + core/encoding (4596 examples) and library/stringio,
core/symbol, library/erb, library/cgi (1048 examples): identical results on baseline
and candidate (the one String.try_convert error is on both).

liquid A/B, interleaved base/cand/cand/base, 8 iterations, CPU ms steady:
liquid-il 1739, 1129, 1424, 1596 (no regression; the 4-iteration table had looked 40%
worse, which was noise); liquid-compile 568, 537, 489, 631 (no change).

## Tried and not kept / measured only

- `DOTNET_TieredPGO=0` (in-process erubi loop, without the bug above): the first 200
  renders took 2.1 s instead of ~6 s CPU (TieredPGO re-JITs every hot method twice -
  15470 JIT compiles in that warm-up, 4569 "Instrumented Tier0" and 2344 instrumented
  Tier1), steady state ~10% slower (0.23 vs 0.20 s per 50 renders). Process-wide, so it
  would move AWFY too: not changed here, left for the main session to gate
  (`<TieredPGO>false</TieredPGO>` in Ruby.Console.csproj would be the change).
- GC knobs (`DOTNET_gcConcurrent=0`, `DOTNET_GCgen0size=64MB`) on erubi: no change beyond noise.

## What is left, ranked

1. mail 10.6x: the Ragel-generated `Mail::Parsers::*Parser.parse` methods (thousands of
   lines of table-driven integer/array code) dominate - 16% of samples in `CallSite.Target`,
   6% in the body of `ReceivedParser.parse`; the method JIT does not specialize bodies that
   big. Belongs to parsers/compute.
2. liquid-render 7.4x / liquid-compile 5.1x: not profiled this round (budget). Hypothesis:
   Liquid's tokenizer/StringScanner and `instance_variable`-heavy Context lookups.
3. Comparing strings of different representations converts one of them in place
   (`ReverseOrdinalCompareTo` -> `SwitchToBinary`), even for a frozen, deduplicated
   literal, which then changes the cost of everything that later touches that literal.
   Comparing without switching (or never switching a frozen string) is the strings
   area's call; after the fixes above the cost is a transcoding per append, not O(n).
4. `CGI.escapeHTML` is the pure-Ruby fallback (gsub with a hash): 14x CRuby's C
   version in a micro (7.2 s vs 0.52 s for 2M short strings). Used by liquid's `escape`
   filter and Rails' `html_escape`; a C# CGI::EscapeExt / ERB::Escape would be the fix.
5. Warm-up: every benchmark's first iteration is 5-30x its steady state (JIT tiers).
