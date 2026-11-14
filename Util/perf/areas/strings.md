# Strings, Symbol, Regexp, file I/O (perf-areas round, 2026-11-14)

Baseline: `wt-perfbase` (b6cd208a, Release net10.0). CRuby 4.0.9. Shapes:
`Util/perf/areas/strings/shapes.rb` (40 in-process shapes, thread CPU, least of 5);
answers: `Util/perf/areas/strings/check.rb` (diff against `ruby check.rb`).

Timing caveat found on the way: a shape that runs for less than a few seconds measures
IronRuby's tiering, not the method. The first pass (warm-up of n/4 calls) put `bytes` at 4-5x,
`File.readlines`/`foreach`/`StringIO#gets` at 2.3x; with 2 s of warm-up they are 0.9-1.0x.
Every number below is after >= 2 s of warm-up; ten agents shared the box, so the runs are
interleaved base/cand/cand/base and both baseline runs are shown where it matters.

## Ratio table (IronRuby baseline / CRuby, steady state, worst first)

| shape | CRuby | IronRuby | ratio |
|---|---|---|---|
| `lines(chomp: true)`, 2000 lines | 302 us | 1983 / 1869 us | 6.4 |
| `String#encode("UTF-16LE")`, 420 chars | 8.2 us | 26.6 / 28.3 us | 3.4 |
| `encode("UTF-8", "ISO-8859-1")` | 8.0 us | 36 us | 4.5 |
| `String#index` 1M chars, needle at the end, non-ASCII receiver | 24 us | 120 us | 5.0 |
| `String#index` 1M chars, ASCII receiver | 24 us | 45 us | 1.9 |
| `each_line { }`, 2000 lines | 404 us | 710 / 736 us | 1.8 |
| `match?(/\d+/)` | 0.40 us | 0.69 / 0.67 us | 1.7 |
| `lines`, 2000 lines | 296 us | 582 / 533 us | 1.9 |
| `Array#sum` of 480 Integers (arrays area) | 1.5 us | 3.0 us | 2.0 |
| `File.write` 10 KB | 66 us | 87 us | 1.3 |
| gsub/sub/scan/split/=~/match/format/%/interp/upcase/each_char/chars/pack/unpack/to_sym/Symbol#to_s/String#==/substring/inspect/<</File.read/readlines/foreach/StringIO | | | 0.3-1.1 |

## Kept

1. **`String#encode` takes the text whole when every character surely fits** (ASCII into an
   ASCII-reading encoding, or surrogate-free text into UTF-8/16/32; UTF-7 excluded). Before,
   `Transcode` built one CLR string per character and asked the encoder about it (try/catch).
   420 chars to UTF-16LE 26.6/28.3 -> 2.7/2.9 us; ASCII 30.3 -> 2.3 us; Latin-1 -> UTF-8
   36 -> 2.8 us (CRuby 8 us). Text that may not fit (non-ASCII into Latin-1, Shift_JIS...)
   keeps the per-character path: still ~26 us, ~3.5x CRuby.
2. **`String#lines` / `#each_line` collect lines in C#** (`__ir_lines__`), not with a Ruby
   block per line: lines 582/533 -> 370/367 us, each_line 710/736 -> 543/543 us.
3. **`lines(chomp: true)` with the default separator chomps in C#**: 1983/1869 -> 317/236 us.
   Also fixes a difference from CRuby: a `"\r"` with no `"\n"` after it is kept
   (`"x\r".lines(chomp: true) == ["x\r"]`; the Ruby path stripped it).
4. **`String#match?` in C#** (was a ruby4.rb method calling `Regexp#match?`):
   0.69/0.67 -> 0.39/0.54 us (CRuby 0.40); String pattern 1.04 -> 0.61 us (CRuby 1.93).

Verified: `check.rb` output identical to the baseline's except the two chomp lines that now
match CRuby (the other 28 differing lines vs CRuby are pre-existing: xml:/replace on broken
binary input, UTF-16 lines). mspec core/string + core/regexp + core/symbol: 4489 examples, 0 failures, 4 errors on both
trees, the same 4.

## Tried / measured and not changed

- **`String#index` on a big string** (the old ~80x is gone): the search is a vectorized
  `Span.IndexOf`; the remaining gap is the representation. An ASCII string held as bytes is
  1.9x (45 vs 24 us per MB); a non-ASCII one is held as UTF-16, so 2 MB are scanned with
  `PackedSpanHelpers.IndexOf` (98% of the profile) - 120 us. Not fixable without a UTF-8
  string representation.
- A process-CPU harness blamed `String#size` (5 us on a 1M string): that was the tiering
  thread's CPU, not the call (0.17 us with thread CPU).

## What is left (ranked)

1. `encode` into non-Unicode targets with non-ASCII text: per-character probe with an
   exception per refusal; an `EncoderFallback` that records instead of throwing would let it
   go whole too.
2. `String#index`/`include?` on large non-ASCII strings: UTF-16 doubles the bytes scanned.
3. `each_line { }` still 1.3x: ruby4.rb's wrapper builds the whole array then calls the block
   via `block.call` per line.
4. `Array#sum` 2x (arrays area).
