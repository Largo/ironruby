# Compute area (perf-areas round, 2026-11-14)

Baseline: PR #216 head b6cd208a, Release net10.0, vs CRuby 4.0.9. ruby-bench micro runner
(`Util/rubybench/run.sh`, warmup 2, itrs 3; 8 shared cores, so wall times are noisy).

## Ratios on the baseline (IR/CRuby, worst first)

| benchmark | IR ms | CRuby ms | ratio | where the time goes |
|---|---|---|---|---|
| str_concat | 1944 | 267 | 7.28 | String#<< / allocation (strings area) |
| splay | 1350 | 265 | 5.09 | JIT declines SplayTree methods (`call: right/0` attr readers) - dispatch area |
| ruby-xor | 1286 | 365 | 3.52 | JIT screen rejects `String#setbyte`; whole method runs through dynamic sites |
| nqueens | 840 | 495 | 1.70 | **`<<`/`>>` not in the JIT: any method using a shift ran generic** (fixed) |
| nbody | 694 | 399 | 1.74 | Float ivar math, not investigated |
| fannkuchredux | 2467 | 1470 | 1.68 | Array element swaps, not investigated |
| blurhash | 1161 | 835 | 1.39 | |
| gcbench | 10744 | 9050 | 1.19 | |
| loops-times, binarytrees, matmul, sudoku | | | 0.79-0.94 | |
| fib | 121 | 647 | 0.19 | |
| lee | - | - | - | needs the victor gem |

chunky-png / rubykon are gem benchmarks (pure-gems.md: 2.2x / 2.9x); not re-run.

## The Int32 / Bignum premise

Integers past 2**31 are NOT BigIntegers: `ClrInteger.Narrow` hands out a boxed Int64 for the whole
long range, and the JIT types them `JT.Lng`. Measured (`compute/intops.rb`, CPU ms, 2M iterations):
Int64 `+`, `*`, `%`, `&`, `|`, `^`, compare are as fast as Int32 (e.g. i64-mul 20 ms vs CRuby 111).
What *was* slow past and below 2**31 alike was shifting.

## Kept: Integer#<< and #>> in the method JIT

`JitCompiler.EmitShift` + `JitRuntime.ShlLong/ShrLong/ShrInt`: int/long receiver, int/long width;
a right shift of an int stays an int; a result past a long, or a negative width that leaves the int
range, deopts. Before, the screen rejected every method containing `<<`/`>>` on integers.

| shape (CPU ms, `intops.rb`/`ops.rb`) | before | after | CRuby |
|---|---|---|---|
| `i << 8` x2M | 191-207 | 5.3 | |
| crc32-style `((s<<5)^i^(s>>3)) & 0xffffffff` | 1106-1196 | 14.7 | 293 |
| rgba pack | 926-1032 | 50 | 370 |
| rgba unpack | 526-548 | 28 | 216 |
| ruby-bench nqueens (per itr, 2 runs each, A B B A) | 1831 / 1643 | 407 / 434 | 495 |

Others in the A/B (blurhash, nbody, fannkuchredux) moved within wall-clock noise (they do not shift
in hot code). Answers: `run.sh --verify` SAME for nqueens, blurhash, fannkuchredux, nbody, fib,
ruby-xor, sudoku. `compute/shift-matrix.rb` (299 lines, every value x width incl. overflow/negative
widths/deopt+retire paths) identical to CRuby. hot-shapes-matrix (1264 lines) and inline-matrix
(51 lines) identical. mspec core/integer: 598 examples, 0 failures, 2 errors - same as baseline.

## Left, ranked

1. ruby-xor 3.5x / 5.4x CPU: `String#getbyte/setbyte/bytesize` are not JIT intrinsics; setbyte
   would need JitUndoLog entries (a deopt re-runs from the top, and xor is not idempotent).
2. splay 5.1x: method JIT declines on attr_reader calls on other objects (`call: right/0`).
3. str_concat 7.3x: strings area.
4. nbody / fannkuchredux ~1.7x: not profiled.
