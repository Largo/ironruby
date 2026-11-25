# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-19 evening, c99c2ef6 (rounds twenty-two, twenty-four and twenty-five merged: `Array.new`
in typed bodies, `@x ||= v`, constants looked up again), Release net10.0, the same host as
below, load 0.6-1.1 throughout, `git status` clean. This is the timing half those three
rounds owed; the tables further down are 2baa0099's and were not taken again.

Are-We-Fast-Yet, all fourteen, us per iteration, one run of the three (`awfy.rb 12`);
"second run" is IronRuby/CRuby in a run of those two with IronRuby first. The last two
columns compare this build with 2baa0099, the build before the three merges: the time in
alternating processes (six rounds, medians), and the bytes per iteration (`alloc.rb`):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | second run | new/2baa0099 (alternating) | bytes new/2baa0099 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Havlak | 2111468 | 3283095 | 168618 | 1.55 | 1.61 | 0.919 | 0.980 |
| Permute | 89031 | 125597 | 6430 | 1.41 | 1.27 | 0.965 | 1.000 |
| Towers | 83662 | 98698 | 4672 | 1.18 | 0.95 | 1.086 | 0.741 |
| DeltaBlue | 39405 | 45621 | 1522 | 1.16 | 1.05 | 1.045 | 0.971 |
| Sieve | 116917 | 127634 | 7021 | 1.09 | 0.82 | 0.972 | 1.000 |
| Richards | 295190 | 321413 | 14595 | 1.09 | 1.16 | 0.956 | 1.005 |
| Queens | 93916 | 85915 | 6200 | 0.91 | 1.02 | 0.916 | 1.001 |
| Json | 72268 | 60015 | 7329 | 0.83 | 0.74 | 0.989 | 0.993 |
| Storage | 105853 | 80876 | 16976 | 0.76 | 0.63 | 0.931 | 1.000 |
| CD | 699294 | 454064 | 36253 | 0.65 | 0.64 | 0.994 | 0.994 |
| Bounce | 86719 | 44327 | 2148 | 0.51 | 0.53 | 0.992 | 1.000 |
| NBody | 1587580 | 530033 | 15643 | 0.33 | 0.34 | 0.955 | 1.000 |
| List | 100265 | 26069 | 6689 | 0.26 | 0.25 | 0.935 | 1.003 |
| Mandelbrot | 791486 | 48924 | 41198 | 0.06 | 0.06 | 1.001 | 0.994 |

Geomean IronRuby/CRuby 0.670 and 0.631 (0.635 and 0.643 at 2baa0099), IronRuby/TruffleRuby
12.4. CRuby's own column moves by a fifth between the two runs here as it did before
(Sieve 117 and 148 ms, Towers 84 and 107), so the ratio to CRuby does not say what the
three merges did; the comparison with 2baa0099 does:

- **Time: no difference that this host can show.** Alternating processes: geomean 0.974,
  from 0.916 (Queens) and 0.919 (Havlak) to 1.045 (DeltaBlue) and 1.086 (Towers), with
  ranges that overlap in every row. `awfy.rb 12` of the two builds: 1.012 with the old
  build first, 0.985 with the new one first. Towers was the one row slower all three
  times (1.09, 1.18, 1.04), and in ten more alternating rounds it was 1.037 with the
  ranges inside each other (76-106 ms and 88-103), DeltaBlue 0.972, Storage 1.008. Its
  JIT decisions are the same on both builds and it allocates a quarter less.
- **Bytes: geomean 0.975.** Towers 167 -> 124 KB, DeltaBlue 16.2 -> 15.7 MB, Havlak 1.91 ->
  1.87 GB; the rest within 0.7%.
- **The micro table** (both orders): faster in both `abs`, `new2`, `swap`, `s_map2`,
  `s_each4` (7-20%); slower in both `yield`, `lit==`, `veach4`, `treefind` (9-26%). Each
  of the four in a process of its own, eight alternating rounds in each order: `yield`
  0.87 and 0.98, `veach4` 0.93 and 0.92, `treefind` 0.99 and 0.94; `lit==` 1.15 and 1.18,
  and in twelve more rounds 0.95 and 0.96 - the same minimum on both builds, so a fast
  and a slow state and not a cost. The JIT's decisions for it are the same.
- **Start-up on the ReadyToRun host**, least of ten, 2baa0099 -> now: `-e 1` 475 -> 480 ms,
  `gem --version` 637 -> 619, `require "irb"` 1235 -> 1248, `irb` 1864 -> 1829, `gem
  list` 1316 -> 1426 - and `gem list` again, twenty alternating runs: least 1362 -> 1330,
  median 1442 -> 1425.

So the three rounds cover more (the census), allocate a little less, and are not
measurably faster or slower on the benchmarks - which have neither a `||=` nor a constant
set while they run, and whose `Array.new` sites are in constructors. What they are for is
ordinary Ruby, which this table does not have; a benchmark of that kind (rubygems loading
and resolving, a parser, an ERB render) is the gap in the measuring.

Master's own table, merged in with master at 7fb0cbcb (taken on another machine; it does not
compare with the tables of this branch above and below):

2026-11-19, the code of 434e3dcc (perf/interp on top of master at 0e78b9fd, #233),
Release net10.0, 8 cores, load 1.1 to 1.4 (other sessions' work). CRuby 4.0.9 (no YJIT).
"master" is 0e78b9fd built beside it and run in the same invocation. TruffleRuby is not
installed on this machine and was not measured: its last figures are 2026-11-14's
(TruffleRuby/CRuby 0.053 over the thirteen, 40.0.0, Oracle GraalVM Native).

**These ratios do not compare with the tables before them.** The table of 2026-11-14
(a6b1c629, a container of 4 cores) had IronRuby/CRuby at 0.730 over the thirteen, with
DeltaBlue 1.18 and Richards 1.20. On this machine the Release build of 2026-11-14 itself
(b6cd208a, the tree before round ten) reads DeltaBlue 1.50, Richards 1.51, Json 1.23,
Bounce 1.13, Storage 0.92 today, in one invocation with CRuby and with master, and
master is under it or within 1% of it on each of them (1.44, 1.45, 1.21, 1.14, 0.91;
List 0.86 -> 0.50 is round ten). So the higher ratios are this machine's, not a
regression of the code: against the table of 2026-11-14 CRuby's microseconds are the
same or up to 23% lower here, and IronRuby's are 11-28% higher on nine of the thirteen
(master; the old build was run on six, and is as high on those). Why IronRuby loses
where CRuby gains (eight cores shared with other sessions) is not known. The machine's speed
differs between sessions by tens of percent, so compare ratios within one table, not
microseconds, and not across tables.

Are-We-Fast-Yet, all thirteen, steady state, µs per iteration (lower is better), one
run of `awfy.rb 12`:

| benchmark | CRuby | master | IronRuby | IronRuby/CRuby | master/CRuby |
|---|---:|---:|---:|---:|---:|
| Richards | 334983 | 498135 | 541272 | 1.62 | 1.49 |
| DeltaBlue | 49355 | 71984 | 71890 | 1.46 | 1.46 |
| Permute | 134603 | 176916 | 170787 | 1.27 | 1.31 |
| Towers | 108721 | 138013 | 135836 | 1.25 | 1.27 |
| Json | 87430 | 105719 | 106933 | 1.22 | 1.21 |
| Bounce | 100006 | 115384 | 113929 | 1.14 | 1.15 |
| Queens | 92249 | 101608 | 103837 | 1.13 | 1.10 |
| Sieve | 158689 | 166912 | 163819 | 1.03 | 1.05 |
| Storage | 140626 | 128215 | 129070 | 0.92 | 0.91 |
| CD | 814398 | 667110 | 666172 | 0.82 | 0.82 |
| NBody | 1966248 | 1529525 | 1485338 | 0.76 | 0.78 |
| List | 100347 | 50419 | 51735 | 0.52 | 0.50 |
| Mandelbrot | 862111 | 62981 | 62134 | 0.07 | 0.07 |

Geomean of the thirteen: IronRuby/CRuby 0.853, master/CRuby 0.852; IronRuby over master
1.002, and 1.010 in a second run with the columns the other way round (Landed,
perf/interp, has both). Richards' 1.62 against master's 1.49 is one run's: in six more
alternating runs of the two it was 1.08, 0.95, 1.07, 1.01, 1.00 and 1.00 of master, and
its JIT decisions are master's and its light frames differ in seven methods outside the
benchmark's loop (four of `run.rb`'s reporting, three at start-up).

What follows is the history up to 2026-11-14, in that table's ratios. Geomean of the
thirteen then: IronRuby/CRuby 0.730, and 0.750 and 0.779 in the two runs
before (the second has Bounce at 1.73, a cell that was 1.00 of the tree before in six
alternating processes); TruffleRuby/CRuby 0.053. On the tree of round nine it was 0.785,
0.800 and 0.768, and 0.785 again with #215's `!=` binding merged in (Permute 1.28 ->
1.17); TruffleRuby's DeltaBlue has been 12.7-12.9 ms since round nine and was 3.6 in
round seven's run. CRuby's own figures move too: Json was 81, 93 and 106 ms in the three runs, which
is all of Json's 1.02, 0.87, 0.74. On the tree of round seven it was 0.804 and 0.787 (NBody
1.21 and 1.08, CD 0.70 and 0.66), and on the
tree of round five, at the start of the session, it was 0.872 (Permute 1.49, Richards
1.33, CD, Json and DeltaBlue 1.17, NBody 1.07): the first time the eight benchmarks
beyond the original five were run at all.

The five measured from the start, IronRuby/CRuby; "r10" is the three runs of 2026-11-14,
the other columns are what was recorded when each round landed, in other sessions. On
this machine on 2026-11-19 they read 1.46, 1.62, 1.22, 1.14 and 1.25 (geomean 1.33),
which is the machine (above) and no row of this table:

| benchmark | r10 | r9 | r7 | r6 | r5 | r4 | r3 | r2 | r1 | start |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 1.18, 1.22, 1.16 | 1.18, 1.22, 1.25 | 1.33, 1.26 | 1.28 | 1.23 | 1.41 | 1.56 | 1.64 | 1.95 | 2.23 |
| Richards | 1.20, 1.08, 1.29 | 1.11, 1.29, 1.21 | 1.18, 1.19 | 1.15 | 1.16 | 1.19 | 1.42 | 1.48 | 1.61 | 1.94 |
| Json | 0.90, 1.04, 0.97 | 1.02, 0.87, 0.74 | 0.94, 0.87 | 1.02 | 1.13 | 1.19 | 1.20 | 1.28 | 1.56 | 1.64 |
| Bounce | 0.92, 1.06, 1.73 | 0.91, 0.90, 0.92 | 0.73, 0.90 | 0.92 | 0.91 | 0.89 | 1.17 | 1.18 | 1.40 | 1.65 |
| Towers | 0.96, 1.04, 0.94 | 0.99, 0.97, 0.95 | 1.00, 0.93 | 0.94 | 0.99 | 1.02 | 1.27 | 1.28 | 1.57 | 1.90 |
| geomean | 1.02, 1.09, 1.19 | 1.04, 1.04, 1.00 | 1.02, 1.02 | 1.05 | 1.08 | 1.13 | 1.32 | 1.36 | 1.61 | 1.86 |

2026-11-19, 2baa0099 (master's #228 to #233 merged in, then round twenty-one's
`perf/park`), Release net10.0, 4 cores (Xeon, 2.10 GHz), `git status` clean. Load average
0.47 when the AWFY run began, 1.0-1.1 through the rest. The same host as the table before
(3081699d, twelve hours earlier): the one on which CRuby's own numbers move by a fifth
between two runs (NBody 1.54 and 1.24 s in the two runs below, Towers 93 and 106 ms), so
the two columns of ratios disagree by more than a round is worth, and what a round did is
under "Landed", from alternating processes. CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0
(Oracle GraalVM Native). The machine's speed differs between sessions by tens of percent,
so compare ratios, not microseconds, and ratios across sessions only with a build of the
older tree beside them.

Are-We-Fast-Yet, all fourteen, steady state, us per iteration (lower is better), one run
of the three (`awfy.rb 12`); "second run" is IronRuby/CRuby in a run of those two with
IronRuby first; the last column is the ratio of the table before (3081699d):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | second run | 3081699d |
|---|---:|---:|---:|---:|---:|---:|
| Havlak | 2099129 | 3103907 | 163840 | 1.48 | 1.60 | 1.83 |
| DeltaBlue | 34877 | 46491 | 1500 | 1.33 | 1.16 | 1.23 |
| Permute | 98742 | 110303 | 6224 | 1.12 | 1.23 | 1.03 |
| Richards | 301100 | 313872 | 14742 | 1.04 | 0.99 | 1.41 |
| Towers | 92973 | 94134 | 4604 | 1.01 | 0.71 | 1.08 |
| Queens | 86075 | 83871 | 5058 | 0.97 | 0.95 | 0.98 |
| Sieve | 147454 | 112580 | 6164 | 0.76 | 0.89 | 0.84 |
| Json | 79431 | 60089 | 7163 | 0.76 | 0.72 | 0.71 |
| CD | 696421 | 471430 | 36701 | 0.68 | 0.71 | 0.71 |
| Storage | 122077 | 80844 | 16263 | 0.66 | 0.69 | 0.72 |
| Bounce | 88213 | 46760 | 2096 | 0.53 | 0.58 | 0.49 |
| NBody | 1542019 | 541277 | 15344 | 0.35 | 0.45 | 0.34 |
| List | 102529 | 26007 | 6431 | 0.25 | 0.28 | 0.25 |
| Mandelbrot | 775369 | 48310 | 37913 | 0.06 | 0.06 | 0.08 |

Geomean of the fourteen: IronRuby/CRuby 0.635 and 0.643 (0.667 and 0.646 at 3081699d,
0.641 and 0.639 at 981d2704, 0.681 and 0.693 at a7060a27); TruffleRuby/CRuby 0.050;
IronRuby is 12.7 times TruffleRuby's time (Mandelbrot 1.3, List 4, Storage 5, Json 8, CD
13, Queens 17, Permute and Sieve 18, Havlak 19, Towers 20, Richards 21, Bounce 22,
DeltaBlue 31, NBody 35). Of the thirteen without Havlak: 0.595 and 0.600. TruffleRuby's
DeltaBlue was killed in the table's run (exit 137 after no iteration: it has been 22 ms,
1161 ms and 1.5 ms in the last three tables) and ran in 1.52 and 1.48 ms twice alone
right after; the table and the geomeans have 1.50.

What is in this table that was not in the last: master's six pull requests (a block and a
splat passed on by a light frame, the core loops of the prelude, `sum`'s Float seed;
their own entries under "Landed" say what each did, none was measured against this
branch's tree here) and round twenty-one's parked scopes, which moved no benchmark's time
in alternating processes (0.95-1.03, all overlapping) and took a quarter to a third of
the bytes of DeltaBlue, Queens and Havlak. By this table three benchmarks are slower than
CRuby in both runs - Havlak, DeltaBlue, Permute - and Richards and Towers are at it.

The five measured from the start, IronRuby/CRuby; "now" is the two runs above, the other
columns are what was recorded when each round landed, in other sessions:

| benchmark | now | 3081699d | 981d2704 | d064caf6 | a7060a27 | f37be3b1 | r10 | r9 | r7 | r6 | r5 | r4 | r3 | r2 | r1 | start |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 1.33, 1.16 | 1.23, 1.01 | 1.30, 1.19 | 1.17, 1.15 | 1.24, 1.23 | 1.23 | 1.18, 1.22, 1.16 | 1.18, 1.22, 1.25 | 1.33, 1.26 | 1.28 | 1.23 | 1.41 | 1.56 | 1.64 | 1.95 | 2.23 |
| Richards | 1.04, 0.99 | 1.41, 1.17 | 1.21, 1.16 | 1.18, 1.20 | 1.11, 1.16 | 1.02 | 1.20, 1.08, 1.29 | 1.11, 1.29, 1.21 | 1.18, 1.19 | 1.15 | 1.16 | 1.19 | 1.42 | 1.48 | 1.61 | 1.94 |
| Json | 0.76, 0.72 | 0.71, 0.89 | 0.74, 0.81 | 0.73, 0.80 | 0.93, 0.94 | 0.95 | 0.90, 1.04, 0.97 | 1.02, 0.87, 0.74 | 0.94, 0.87 | 1.02 | 1.13 | 1.19 | 1.20 | 1.28 | 1.56 | 1.64 |
| Bounce | 0.53, 0.58 | 0.49, 0.49 | 0.53, 0.54 | 0.99, 0.97 | 0.94, 0.90 | 0.89 | 0.92, 1.06, 1.73 | 0.91, 0.90, 0.92 | 0.73, 0.90 | 0.92 | 0.91 | 0.89 | 1.17 | 1.18 | 1.40 | 1.65 |
| Towers | 1.01, 0.71 | 1.08, 0.95 | 0.95, 1.00 | 0.92, 0.96 | 0.99, 1.04 | 0.88 | 0.96, 1.04, 0.94 | 0.99, 0.97, 0.95 | 1.00, 0.93 | 0.94 | 0.99 | 1.02 | 1.27 | 1.28 | 1.57 | 1.90 |
| geomean | 0.89, 0.81 | 0.92, 0.87 | 0.90, 0.90 | 0.98, 1.01 | 1.04, 1.05 | 0.99 | 1.02, 1.09, 1.19 | 1.04, 1.04, 1.00 | 1.02, 1.02 | 1.05 | 1.08 | 1.13 | 1.32 | 1.36 | 1.61 | 1.86 |

Rounds seven to sixteen changed none of these five on purpose (Json's allocation fell in
round nine); round seventeen changed Json and round eighteen Bounce. What the rounds did
is in the direct comparisons of the trees under "Landed" (CD and Permute in round seven,
NBody, CD and Json in round nine, List in rounds ten and thirteen, NBody in rounds
fourteen and sixteen, Json in round seventeen, Bounce in round eighteen).

Bytes allocated per iteration (`Util/perf/alloc.rb`; it moves by about 0.3% between
invocations, unlike the timings), MB, at 2baa0099: DeltaBlue 15.9, Richards 14.1, Json
28.5, CD 225.5, Bounce 7.3, List 0.6, Mandelbrot 3.2, NBody 152.1, Permute 124.5, Queens
30.5, Sieve 98.7, Storage 112.6, Towers 0.15 (Towers reads anything from 0.07 to 0.26
from one invocation to the next, on every tree), Havlak 1909. Where they came from, round
six -> now: CD 516 -> 226 (round seven); NBody 2892 -> 1364 (round nine) -> 602 (round
fourteen) -> 152 (round sixteen: the boxes of its Float instance variables); Json 35.8 ->
30.6 (round nine) -> 28.5 (round seventeen: the scanner's Integers stay unboxed inside
`read`); Bounce 11.0 -> 7.3 (round eighteen: the ball's sums inside `bounce`); List 8.2 ->
0.6 (round ten); Mandelbrot 94.5 -> 3.2 (not recorded when it happened; `Integer#<<` and
`#>>` in the JIT, 9997c5bc, is the likely one, as for its time); DeltaBlue 24.7 -> 15.9,
Queens 41.3 -> 30.5 and Havlak 2726 -> 2557 (master's merges) -> 1909 (round twenty-one:
the scope, Proc and BlockParam of a method holding a literal block); Permute, Sieve and
Storage where they were. Richards is under "Regressions": 14.1 now, and this file said
3.2.

Per operation (`Util/perf/micro.rb`), ns, one run of `micro-table.rb` on 2baa0099; a line
moves by 15-20% from run to run on this machine, so a ratio is good to about that.
"3081699d" is the ratio of the last table, on the same host. The lines that call an
iterator with a block from a method are round twenty-one's: `s_each1` 2.21 -> 1.62,
`s_times1` 1.92 -> 1.34, `s_map1` 1.92 -> 1.47, `s_each2` 1.65 -> 1.44, `veach1` 1.84 ->
1.54 (0.71-0.83 of the tree before in alternating rounds). `empty` (21.1 -> 26.1 ns) and
`yield` (45.2 -> 54.8) read worse in this run and are not a change: in eight alternating
rounds against the tree before, `yield` was 43.5 against 43.1 ns and `blockeach` 40.5
against 40.2, and `empty` is the table's first line, timed while the process warms up:

| op | CRuby | IronRuby | ratio | 3081699d | start |
|---|---:|---:|---:|---:|---:|
| empty | 19.3 | 26.1 | 1.35 | 1.06 | 1.23 |
| ivar_rw | 31.0 | 33.4 | 1.08 | 1.09 | 2.72 |
| getter | 22.5 | 26.1 | 1.16 | 1.27 | 3.17 |
| attr | 16.2 | 3.4 | 0.21 | 0.23 | 0.80 |
| cmp | 25.9 | 24.5 | 0.95 | 0.89 | 3.58 |
| abs | 30.3 | 32.9 | 1.09 | 0.85 | 3.07 |
| arr | 35.9 | 33.4 | 0.93 | 0.86 | 1.82 |
| nil? | 23.3 | 36.2 | 1.55 | 1.41 | 3.48 |
| == | 24.8 | 29.1 | 1.17 | 1.09 | 3.35 |
| yield | 29.9 | 54.8 | 1.83 | 1.42 | 2.57 |
| each4 | 168.2 | 189.1 | 1.12 | 1.26 | 1.87 |
| while10 | 153.5 | 25.8 | 0.17 | 0.17 | 0.23 |
| new2 | 73.4 | 68.1 | 0.93 | 0.95 | 1.47 |
| symcase | 29.2 | 28.4 | 0.97 | 1.06 | 1.03 |
| str== | 98.8 | 45.2 | 0.46 | 0.51 | 1.44 |
| poly2 | 48.1 | 30.7 | 0.64 | 0.74 | 2.11 |
| blockeach | 53.8 | 41.6 | 0.77 | 0.93 | 2.33 |
| lit== | 186.5 | 50.4 | 0.27 | 0.37 | 0.94 |
| swap | 61.6 | 54.2 | 0.88 | 0.75 | 1.35 |
| locattr | 59.3 | 44.0 | 0.74 | 0.70 | 1.38 |
| sqrtw | 34.9 | 2.1 | 0.06 | 0.06 | 1.34 |
| rangeeach | 38.0 | 0.6 | 0.02 | 0.02 | 0.87 |
| blkattr | 71.2 | 38.1 | 0.54 | 0.63 | 1.12 |
| selfcall | 91.0 | 32.0 | 0.35 | 0.43 | 0.99 |
| veach1 | 189.3 | 290.9 | 1.54 | 1.84 | 1.59 |
| veach2 | 243.7 | 315.8 | 1.30 | 1.61 | 1.37 |
| veach4 | 378.5 | 456.1 | 1.21 | 1.28 | 1.38 |
| veach100 | 4703.8 | 5591.3 | 1.19 | 1.01 | 1.04 |
| rangelit | 150.9 | 94.0 | 0.62 | 0.71 | 0.87 |
| short4 | 255.9 | 334.2 | 1.31 | 1.29 | 1.41 |
| s_each1 | 77.3 | 125.2 | 1.62 | 2.21 | 2.11 |
| s_times1 | 91.1 | 122.0 | 1.34 | 1.92 | 2.07 |
| s_map1 | 83.7 | 123.0 | 1.47 | 1.92 | 1.93 |
| s_any1 | 54.6 | 149.4 | 2.74 | 2.48 | 6.15 |
| s_each2 | 91.3 | 131.9 | 1.44 | 1.65 | 1.70 |
| s_times2 | 125.5 | 130.5 | 1.04 | 1.37 | 1.57 |
| s_map2 | 128.3 | 155.6 | 1.21 | 1.42 | 1.29 |
| s_any2 | 82.2 | 168.2 | 2.05 | 1.98 | 4.54 |
| s_each4 | 152.3 | 193.5 | 1.27 | 1.54 | 1.25 |
| s_times4 | 161.1 | 199.8 | 1.24 | 1.48 | 1.27 |
| s_map4 | 209.1 | 209.3 | 1.00 | 1.21 | 1.05 |
| s_any4 | 133.2 | 179.8 | 1.35 | 1.35 | 3.09 |
| listwalk | 112.1 | 59.8 | 0.53 | 0.53 | 0.71 |
| treefind | 1132.6 | 444.8 | 0.39 | 0.46 | 0.60 |

Master's micro table (434e3dcc, the other machine):

Per operation (`Util/perf/micro.rb`), ns, one run of `micro-table.rb` on 434e3dcc on
2026-11-19 (the "start" column is history, in the ratios of the machines of the time); a
line moves by 15-20% from run to run, so a ratio is good to about that. Above 1.3, worst
first: s_times1 2.04, s_each1 1.98, s_any1 1.97, s_map1 1.87, s_times2 1.63, veach1 1.60,
s_any2 1.58, yield 1.57, ivar_rw 1.55, s_each2 1.53, abs 1.51, veach2 1.46, veach4 1.39,
nil? 1.38, s_times4 1.33, s_map2 1.32 - gap 13 (calling an iterator at all) and gap 1
(calls from hot bodies), as before:

| op | CRuby | IronRuby | ratio | start |
|---|---:|---:|---:|---:|
| empty | 24.6 | 27.0 | 1.10 | 1.23 |
| ivar_rw | 43.2 | 66.9 | 1.55 | 2.72 |
| getter | 34.1 | 38.1 | 1.12 | 3.17 |
| attr | 22.2 | 5.2 | 0.23 | 0.80 |
| cmp | 31.4 | 36.7 | 1.17 | 3.58 |
| abs | 41.7 | 62.9 | 1.51 | 3.07 |
| arr | 43.6 | 50.4 | 1.16 | 1.82 |
| nil? | 29.6 | 40.7 | 1.38 | 3.48 |
| == | 34.2 | 38.1 | 1.11 | 3.35 |
| yield | 46.9 | 73.5 | 1.57 | 2.57 |
| each4 | 228.0 | 296.1 | 1.30 | 1.87 |
| while10 | 179.6 | 57.1 | 0.32 | 0.23 |
| new2 | 104.8 | 85.1 | 0.81 | 1.47 |
| symcase | 38.9 | 37.6 | 0.97 | 1.03 |
| str== | 117.8 | 57.6 | 0.49 | 1.44 |
| poly2 | 76.4 | 46.4 | 0.61 | 2.11 |
| blockeach | 70.3 | 66.2 | 0.94 | 2.33 |
| lit== | 249.7 | 77.3 | 0.31 | 0.94 |
| swap | 66.5 | 78.5 | 1.18 | 1.35 |
| locattr | 78.0 | 49.4 | 0.63 | 1.38 |
| sqrtw | 56.0 | 3.6 | 0.06 | 1.34 |
| rangeeach | 64.6 | 2.6 | 0.04 | 0.87 |
| blkattr | 95.4 | 75.3 | 0.79 | 1.12 |
| selfcall | 119.1 | 39.6 | 0.33 | 0.99 |
| veach1 | 224.4 | 358.4 | 1.60 | 1.59 |
| veach2 | 297.2 | 434.1 | 1.46 | 1.37 |
| veach4 | 440.0 | 611.3 | 1.39 | 1.38 |
| veach100 | 6665.0 | 7686.0 | 1.15 | 1.04 |
| rangelit | 169.3 | 113.3 | 0.67 | 0.87 |
| short4 | 348.5 | 420.3 | 1.21 | 1.41 |
| s_each1 | 94.2 | 186.2 | 1.98 | 2.11 |
| s_times1 | 100.9 | 205.4 | 2.04 | 2.07 |
| s_map1 | 127.5 | 238.0 | 1.87 | 1.93 |
| s_any1 | 89.4 | 176.3 | 1.97 | 6.15 |
| s_each2 | 136.6 | 208.5 | 1.53 | 1.70 |
| s_times2 | 136.1 | 221.8 | 1.63 | 1.57 |
| s_map2 | 171.6 | 226.0 | 1.32 | 1.29 |
| s_any2 | 127.9 | 201.6 | 1.58 | 4.54 |
| s_each4 | 229.7 | 283.4 | 1.23 | 1.25 |
| s_times4 | 222.7 | 297.2 | 1.33 | 1.27 |
| s_map4 | 276.7 | 301.0 | 1.09 | 1.05 |
| s_any4 | 216.8 | 262.7 | 1.21 | 3.09 |
| listwalk | 144.3 | 74.3 | 0.51 | 0.71 |
| treefind | 1470.4 | 619.9 | 0.42 | 0.60 |

`lit==`, `swap` and `locattr` are round seven's shapes, and their "start" is round six: a
literal as the receiver of `==`, two element stores in one method, Float attr reads on
locals assigned from attr reads. The last three are round eight's, each a loop of its
own (ns per iteration), measured on the round-eight tree with round seven as "start":
`Math.sqrt` in a `while` loop, a 1000-element `Range#each`, attr op-assigns on a local
that a 20-iteration `times` block assigns. From `selfcall` on they are round nine's, and
their "start" is the round-eight tree against the CRuby figure of this table: a method
calling a small pure Float method on self twice; SOM's `Vector#each` (a Ruby `each` that
yields an ivar Array's elements from a `Range#each` block) summed over 4 and over 100
elements; a method making a Range of two Integers to slice with; an `Array#each` over
four objects with Float arithmetic; and `each`, `times`, `map` and `any?` with a
one-operation block over 1, 2 and 4 elements, called from a method (ns per call of the
iterator); their "start" is round nine's ratio. Those twelve are the cost of calling an
iterator at all (gap 13), of which round ten took `any?`'s share. `veach1`, `veach2`,
`listwalk` and `treefind` are round ten's, with the tree of round nine as "start": SOM's
`Vector#each` over one and two elements, a cursor walking a linked list, and a lookup in
a binary tree of 300 keys whose `compare_to` calls a leaf on self.

Profile (`Util/perf/profile.sh`, self time):

- DeltaBlue: `CallSite.Target` 23.9%, `jitentry$execute` 6.9%, libcoreclr 5.6%, block in
  Vector#each 4.3%, JIT_WriteBarrier 3.1%, Vector#append 2.4%,
  `RubyOps::TakeBfcForBlockYield` 2.3%, `jitentry$is_satisfied` 2.2%.
- Richards: `CallSite.Target` 34.5%, Scheduler#schedule 6.7%,
  `jitentry$is_task_holding_or_waiting` 4.5%, TaskControlBlock#run_task 4.3%,
  `jitentry$is_waiting_with_packet` 3.4%, JIT_WriteBarrier 3.3%, the create_handler block
  3.3%, Scheduler#queue_packet 2.7%, `ProcOps::Call` 2.1%.
- Permute: `CallSite.Target` 18.1%, Permute#permute 15.1%, `jitentry$swap` 10.6%, the
  block in permute 8.1%, libcoreclr 7.8%, JIT_WriteBarrier 5.9%, `Object::GetType` 4.9%,
  `JitRuntime::IndexInt` 3.0%, `BasicObjectOps::ValueNotEquals` 2.6%,
  `RubyMethodScope::.ctor` 2.4%. (`JitUndoLog` and its thread-local lookup, 9% before
  round seven, are gone.)
- NBody: `CallSite.Target` 20.9%, the inner block of advance 14.6%, libcoreclr 14.0%,
  `RubyObjectAttributeReaderDispatcherWithScope::Invoke` 13.0%, JIT_WriteBarrier 5.4%, the
  attribute writer dispatcher 4.0%.
- Json: `CallSite.Target` 24.4%, Parser#read 10.9%, libcoreclr 5.6%,
  Parser#read_string_internal 4.5%, `MutableString::Equals` 3.3%, JIT_WriteBarrier 3.0%,
  Parser#read_char 2.9%, `MutableString::EqualsAsciiLiteral` 2.2%,
  `BasicObjectOps::ValueNotEquals` 2.1%.
- CD: `CallSite.Target` 27.6% (33.0% before round seven), libcoreclr 8.5%,
  RedBlackTree#put 6.9%, `jitentry$is_in_voxel` 6.3% (the method was 12.7% and its sites
  another 11%), RedBlackTree#tree_insert 5.2%, libclrjit 4.3%, Vector2D#compare_to 3.5%,
  CollisionDetector#recurse 3.3%, RedBlackTree#find_node 2.9%.
- Not leads (measured): the DLR miss path, tail calls in rules, the light frame's own
  bookkeeping, the class lookup of identity sites (fixed), the JIT's trampoline (gone),
  the attribute dispatchers' own code (round seven, Rejected), libclrjit at steady state
  in a profile taken under load (late tiering of the runtime's own code, not rules being
  rebuilt: the NBody experiment counted compiled methods per iteration falling to about
  a dozen).

### Ordinary Ruby: `Util/bench/app-shapes.rb` (2026-11-19, 550d838f)

The fourteen benchmarks above are kernels; this is the first table of the other kind: 26
self-checking units of work (the standard library's pure Ruby, and four small programs
written the way applications are; every checksum the same on CRuby 4.0 and IronRuby with
the JIT on, off and on the ReadyToRun host). us per unit, the least of five alternating
rounds on a quiet machine (`ab.rb -r 5`; spreads 10-60%, so two digits at most):

| shape | CRuby | IronRuby | IronRuby/CRuby | JIT and OSR off |
|---|---:|---:|---:|---:|
| ostruct | 102 | 5272 | 51.51 | 5229 |
| erb_result | 149 | 6940 | 46.56 | 6082 |
| uri | 91 | 500 | 5.49 | 500 |
| invoice_build | 141 | 727 | 5.16 | 718 |
| time | 120 | 573 | 4.76 | 537 |
| invoice_render | 136 | 572 | 4.22 | 591 |
| set | 50 | 178 | 3.53 | 192 |
| forwardable | 66 | 158 | 2.38 | 158 |
| prism_walk | 155 | 363 | 2.34 | 388 |
| erb_compile | 178 | 363 | 2.04 | 442 |
| csv_generate | 220 | 430 | 1.95 | 442 |
| delegate | 243 | 469 | 1.93 | 444 |
| optparse | 198 | 371 | 1.87 | 376 |
| rules | 194 | 338 | 1.74 | 352 |
| csv_parse | 331 | 575 | 1.74 | 815 |
| gem_requirement | 120 | 198 | 1.65 | 196 |
| pp | 232 | 374 | 1.61 | 374 |
| json_generate | 93 | 145 | 1.56 | 143 |
| gem_version | 170 | 257 | 1.51 | 261 |
| json_parse | 83 | 111 | 1.34 | 109 |
| gem_spec | 133 | 176 | 1.32 | 179 |
| query_aggregate | 145 | 181 | 1.24 | 185 |
| strscan | 432 | 508 | 1.18 | 515 |
| query_lazy | 285 | 325 | 1.14 | 343 |
| shellwords | 111 | 103 | 0.93 | 111 |
| render | 156 | 143 | 0.92 | 147 |

Geomean IronRuby/CRuby 2.48 (1.94 without the two at the top; median 1.87): on ordinary
Ruby IronRuby is about twice CRuby's time, where on the kernels it is 0.65. And the tiers
do nothing for it: the geomean of the JIT and OSR on against off is 0.97, every row but
one overlapping (`csv_parse` 0.71). The census of the 961 methods this run called
(`census.rb -u`): 11.4% `compiles`, 5.7% `maybe`, 82.8% `never` (229 at their parameters:
a block parameter 87, a rest parameter 61, a block used 52; 567 at the screen); the run
specialized 90 of them and declined 30. Refused in most methods: `new` 53, `is_a?` 49, a
String literal 49, `each_child_node` 34 (Prism's visitor, each alone), `respond_to?` 31,
`each` 30, a Hash literal 26, `@x ||= v` 24 (23 alone: those the screen still refuses for
their right-hand side).
What the two at the top are made of, us per call, CRuby -> IronRuby (a probe, each line a
loop of its own): `b.local_variable_set(:x, 1)` 0.1 -> 436, `erb.result_with_hash(a: 1)`
26 -> 6,158, `erb.result(binding)` 12 -> 609, `eval('1 + 1')` 4.4 -> 219,
`instance_eval('1')` 4.2 -> 177, `OpenStruct.new(h)` of three keys 15 -> 2,615,
`define_singleton_method` 1.2 -> 46, `Object.new.singleton_class` 0.3 -> 17, `def o.x`
0.6 -> 18, `Class.new` 0.5 -> 12, `Class.new { def a; end }` 0.9 -> 37. Gaps 30 to 32.

## Gaps, worst first

1. **Calls of other methods from hot bodies** (`CallSite.Target` 24-32% of every
   benchmark; `abs` 1.77 and 1.35 in two runs, `ivar_rw` 1.63, `nil?` 1.45, `getter` 1.37,
   `yield` 1.22 and 1.40 on this branch's host; `yield` 1.57, `ivar_rw` 1.55, `abs` 1.51,
   `nil?` 1.38 in master's table). Richards' `run_task`
   and `schedule` (11.6%), Towers' `push_disk`/`move_top_disk`, DeltaBlue's `remove_first`
   and Bounce's `bounce` all fail one rule of the method JIT: nothing that can deopt may
   follow an effect, because a deopt re-runs the generic body from the start. A second
   call, or a guard or ivar write after a call, is such a thing. See Rejected (perf/jcall)
   for the version that respects the rule and what it bought. What is needed is a guard
   failure after an effect that *continues in generic code* (a generic call for that one
   operation) instead of deopting; for calls, a method scope made on demand from the JIT
   body (locals into the generic tuple layout, the method's block, which the stub's fast
   path drops today). `IR_JIT_VERBOSE=1` prints "a deopt after an effect (EmitXxx)" per
   declined method. Three of the ten faults below were in this rule's bookkeeping: any
   work here needs the (effect, deopt, operand order) matrix extended first. Round seven
   adds two things. Richards' 34.5% has no single owner: `schedule` 6.5%, `run_task`
   5.6%, the create_handler block 5.3%, `queue_packet` 3.2%, the create_device block
   2.8%, `release` 1.0%, all ordinary sites in code the JIT does not compile. And a
   generic call made from a JIT body needs the caller's scope to be the call the source
   makes: without one it sees private and protected methods and no refinements ("Caught
   by the gate", round seven), so "continues in generic code" has to carry self's class
   at least.
   The design is `Docs/jit-resume-deopt.md`: a per-operation generic fallback for
   operations with object results after the first effect, every later speculative
   operation re-checking the method version itself, and a resumable generic body for
   what that cannot take (typed results, loops); the state a materialized scope needs;
   the gate's rows mapped to the faults below; and a staged plan whose first stage
   compiles `hold_self`. Stage 1 is in behind `-X:JITResume`, off by default (round
   eleven, see Landed): it compiles `hold_self` and is worth 13-14% on that shape and
   nothing measurable on a benchmark, as the design expected. With the switch
   `hold_self` is at 1.2-1.3 of CRuby and `bump`, the same method without its two
   attribute operations, at 1.4-1.6: what is left is the cost of entering the method,
   not of what it does.
   Stage 2 was tried in round twelve as a second, untyped compile of the bodies the
   typed compiler declines ("tail mode", perf/resume2, see Rejected): correct, and no
   faster than the generic body on any of four shapes, 3% slower over the benchmarks.
   It showed what this gap is not: the generic body with its call-site rules is already
   what a compiled body of self-guarded object operations with a frame is. The time
   moved from `CallSite.Target` into the bodies and stayed the same. Stage 1 gained
   because its typed body has no frame and no boxes until a fallback. So an attempt here
   must take away something the generic body cannot: the frame (a body that makes a
   general call needs one for the callee's backtrace on every call, so this means calls
   whose callee is a compiled body known not to look, `-X:JITCalls`' kind), or the
   boxes and the dispatch between two compiled bodies (a typed body calling a typed
   body's entry directly, arguments unboxed, with the caller's frame made only when the
   callee falls back). Recompiling the same operations a second way is not it; do not
   try stage 2 again as written. `Util/bench/jit-resume2-shapes.rb` has the four shapes
   (Richards' `find_task`, `queue_packet`, `add_input_and_check_priority`, `schedule`) at
   1.6, 1.1, 0.9 and 1.1 of CRuby for the next attempt to move.
   Round thirteen took the part of that which was already built: `-X:JITCalls`, a typed
   body calling the typed body of another method on self, is on by default (see Landed).
   It reaches what has a typed callee: List's `tail` (28% of List), CD's `magnitude`. It
   does not reach this gap's methods, because their callees have no typed body: in
   Richards `device_in_add`, `work_in_add` and `add_input_and_check_priority` are now
   offered to the compiler and declined at `append` (no typed body: a deopt after an
   effect) or `NO_WORK` (gap 11); in Json `HashIndexTable#add` and `#get` at
   `hash_slot_for`; in DeltaBlue `Dictionary#at` at `match`; in CD `RedBlackTree#get`
   at `node.value` and `Node#successor` at `x.right`, attribute reads on a local (the
   result of the bound `find_node` in `get`, which has no profile; a local that holds
   self in `successor`): `get` binds `find_node` and fails only there, a small thing to
   try for gap 3. So what is left here is what keeps the callees
   out, the effect rule, and for calls on another receiver the binder's reach (it binds
   calls on self only; the inliner already checks a class at a call on a parameter).
   Round fourteen (a workflow of three experiments, see Landed and Rejected) took the
   single core operations that declined whole methods: `String#length`/`size`, `abs`
   and `== nil` on an Array or Hash are operations of the typed body now, and Json
   compiles five more methods (`string_hash`, `hash_slot_for`, `HashIndexTable#add` and
   `#get` through bound calls), DeltaBlue and CD `Vector#at` and `#capacity`. No
   benchmark moved for it. What declines the hot ones now: Json's `read_char` and
   `read` at `!=` on a String with a non-literal operand, and behind them `read_digit`,
   `skip_white_space` and `read_string`; Richards' `append`, `add_input_and_check_priority`
   and two `initialize` at the nil constant (gap 11; both were started in perf/core and
   left as unapplied diffs, not kept); Bounce's `bounce`, past `abs` now, at a deopt
   after an effect; DeltaBlue's `recalculate` at `is_input` and `value` at `new`; List's
   `Element#length` at `length` on nil. And the round closed the second cheap way round
   the effect rule (perf/undo: logged stores are slower than the generic body), so
   `bounce`, `hold_self` and `remove_first` wait for a body that is cheaper to enter,
   not for a rule.
   Round fifteen (perf/core2) went down the two chains that were left. The nil constant
   is compiled now (gap 11), and Richards' two `initialize` with it; `append` then
   declines at "a deopt after an effect (call link at line 54)". `==`/`!=` between two
   Strings with a non-literal operand and `String#[]` with an Integer were built and
   dropped (0.92 and 0.91 on their shapes): with them Json's `read` declines at a deopt
   after `@index += 1`. So both chains end at the effect rule again. A survey of what
   declines in all fourteen found no core operation left that repeats: List's,
   DeltaBlue's and CD's hot declines are calls of user methods on another receiver,
   Havlak's are calls and an attribute write on a receiver that was not profiled.
   Untried: Json's `read` has no parameters and no locals, so the stage-1 resume of
   `-X:JITResume` (a fallback call in place of a deopt) could take `String#length` and
   `String#[]` after the store; it is the one hot method found so far that stage 1 fits.
   Round seventeen compiled it a third way (see Landed, perf/jread): a typed body whose
   only effects are writes to self's instance variables holds them back to its end, so
   every deopt comes before every store. `read`, `read_char` and `read_digit` are
   compiled, Json is at 0.78-0.84 of its time. That is the first way round the effect
   rule that pays (perf/jcall respected it, perf/undo logged the stores and was slower
   than the generic body): the fast path is locals, and nothing is undone.
   What still declines on d064caf6, from `IR_JIT_VERBOSE=1` on all fourteen (the files of
   the survey: every final decline of a method that passed the screen):
   - Bounce's `Ball#bounce`, which is the benchmark: "stores not held back in bounce: an
     effect before a held-back write", then "a deopt after an effect". It writes `@x`
     first as `@x += @x_vel` and then as `@x = x_limit`; the two writes have different
     types (a Long sum, an Int local), a second type is not held but stored where it
     stands, and that store is the effect. A probe says the same in the compiler's
     words: `lim = 500; @x += @v; @x = lim if @x > lim` gives "writes of @x as Lng and
     Int", and with one type (`@x = @x - 500`) "a call of JitRuntime.NarrowLong", a
     helper the allow-list does not have. Held values of one variable in two numeric
     types need one local wide enough for both, and the narrowing admitted if it only
     deopts. Done in round eighteen (see Landed, perf/bounce): `bounce` is compiled,
     Bounce is at 0.56-0.57 of its time and 0.53 of CRuby.
   - DeltaBlue's and Havlak's `Vector#remove_first` ("an invoked body": an inlined or
     bound callee inside a body with held writes; the allow-list cannot see into it),
     Havlak's `set_nesting_level` (a call of `set_is_root`), DeltaBlue's `choose_method`
     (`ReadSelfAttr`, an attribute reader on self in a body with held writes: a reader
     of a variable that is not held would be safe to admit).
   - Richards' `hold_self` (an attribute write on another object beside the held
     writes: "an effect other than the held-back writes") and `append` ("a deopt after an
     effect (call link)"): writes to other objects, which holding back does not cover; a
     thread could see them, so they would need the same argument made per object.
   - Json's `skip_white_space` ("a loop in a body that can deopt and has an effect":
     `read while is_white_space`; holding back does not cross a loop's safe point) and
     `read_string_internal` at `pause_capture` (a Range literal and a String append).
   Everything else that is hot and not compiled is refused by the screen before the
   compiler sees it: over the fourteen, beyond what every process refuses at start-up,
   78 methods for a `new` in the middle of the body, 28 for `first`, 27 for `times`.
   After round eighteen four benchmarks are slower than CRuby (Havlak 1.54-1.61,
   DeltaBlue 1.19-1.30, Permute 1.21, Richards 1.16-1.21). Profiles of three of them on
   981d2704 (`Util/perf/profile.sh`, self time) beside the JIT's decisions, to choose by
   where the time is and not by what the list above happens to name:
   - DeltaBlue: `CallSite.Target` 23.2%, `jitentry$execute` 6.7%, block in `Vector#each`
     4.8% (gap 14), `Vector#append` 2.5% (screen: `new`), block in `Plan#execute` 2.1%,
     `Planner#add_propagate` 1.5% (screen: `with`), `AbstractConstraint#satisfy` 1.5%
     (screen: `raise`). `remove_first` and `choose_method`, which the list above names,
     are not among the first sixteen (under 1.4% each): making them hold their writes
     would buy nothing that can be measured. Not worth a round.
   - Richards: `CallSite.Target` 31.4%, `Scheduler#schedule` 7.5% (screen: the call in
     `trace(...) if TRACING`, a constant that is false; behind it calls on another
     receiver in a `while`), `run_task` 4.7% (screen: a Proc's `call`), the
     create_handler block 4.1%, `queue_packet` 3.0% (screen: it calls `find_task` on
     self, which is refused for `raise 'find_task failed' unless t`), `find_task` 1.8%,
     `add_input_and_check_priority` 2.7% and `append` 2.1% (the effect rule, writes to
     other objects).
   - Permute: `Permute#permute` 18.0% and its `downto` block 9.7% (gap 4: the screen
     refuses the method for `downto`, the loop tier the block for `swap/2`, a call),
     `jitentry$swap` 11.8%, `CallSite.Target` 15.7%, `Object::GetType` 5.2%.
   One reason repeats across them and across ordinary Ruby: a `raise` in a cold branch
   keeps the whole method from the compiler, and with it every caller that would bind
   it (`find_task` -> `queue_packet`, `release`; DeltaBlue's `satisfy`; Json's
   `read_required_char`, `read_value`, `read_name`, `parse` and the three `read_true`,
   `read_false`, `read_null` behind them). A `raise` can be a deopt where a deopt is
   allowed: the generic body runs again from the top and raises, with its own frame for
   the backtrace. Done in round nineteen (see Landed, perf/raise): the methods compile,
   shapes like them are a third faster, and no benchmark moves, because each caller
   stops at its next reason - `queue_packet` at a call on another receiver
   (`add_input_and_check_priority`), `release` at an attribute write on an unprofiled
   receiver, Json's `read_required_char` at "a deopt after an effect (call raise)"
   since the `read_char` it binds writes through a direct call, which is not held back,
   DeltaBlue's `satisfy` at `inputs_do` on self.
   Next by the same data: a statement guarded by a constant that is false
   (`trace(...) if TRACING`) dropped under the constant's version, for Richards'
   `schedule` (7.5% self, the largest generic body of the benchmark); behind it the loop
   tier still refuses `schedule`'s `while` for its calls on another receiver, so find
   out first what compiles once the screen lets it through. And two that the rounds
   above keep running into: held-back writes that reach through a bound callee (a
   caller whose only effects are its callee's writes to the same self: `read_required_char`
   -> `read_char` -> `read`, `set_nesting_level` -> `set_is_root`), and attribute writes
   on a receiver the profile has not seen (`release`, Havlak's `split_bucket`).
2. **NBody: 2.9 GB allocated per iteration**, about 80% of it Float boxes (some 35 per
   pair of bodies; the same loops with an empty inner body allocate a fifth - this and the
   figures below are the NBody experiment's, taken on a loaded machine). Mandelbrot,
   whose Floats live in a `while` loop OSR compiles, runs at about 0.15 of CRuby; NBody's are in
   `(a...b).each { }` inside `each_index { }`, and the block-loop tier declines the inner
   block four ways: `Math.sqrt(x)` ("operator sqrt on Obj, Dbl": the Math constant taken
   as an operand), attribute op-assign `o.vx -= v` (MemberAssignmentExpression), an attr
   read on a local assigned inside the loop (`j_body = bodies[j]`; the loop's attr read
   wants a receiver the loop does not write), and `Range#each`, which is not a
   BlockLoopKind, with runs of 0-4 iterations under `BlockLoopSite.MinRun` = 16. With the
   first three written around, a `times` block of attr reads and writes on parameter
   objects does specialize: 310 ms against CRuby's 700. Each run entered allocates an
   args array and a tuple, so MinRun = 1 on runs of four bought little (475-555 ms
   against 565-795, noisy): short runs need a cheaper entry as well as a lower floor. No
   part of this shows on NBody alone, which is why round seven did not attempt it.
   Round eight took the first three and the kind (see Landed), and NBody still does not
   move: what is left is the entry. With attr reads and writes on a loop-assigned local
   allowed in `while` loops too, `advance` rewritten with a `while` inner loop ran in
   1051-1054 ms against 1410-1636 on the tree before and CRuby's 1265 (the experiment's
   figures); but that also compiled CD's `Node#successor`, a `while` entered 721k times
   per iteration for a few iterations each, which then paid an entry allocation every
   time, so they are limited to core-iterator blocks until an entry costs nothing: the
   OSR tier counts iterations, not entries, and so cannot tell a loop whose runs are
   short. A cheap entry for short runs (args array and tuple per run today) is the one
   thing between this and NBody at about 0.8 of CRuby; then `each_index` as a kind.
   Round nine made the entry allocate nothing and gave each block a floor by the weight
   of its body (see Landed): NBody is at 0.78-0.81 of CRuby, 1364 MB per iteration. What
   is left: an entry still costs about 45 ns more than a yield (the copy's Array check
   asks `GetImmediateClassOf`, a weak-table lookup, about 12 ns; the copy's prologue and
   the delegate about 20), which is why a light block needs five elements; the OSR
   tier's entry for `while` loops builds its argument array in generated code and still
   allocates it, so attr reads and writes on a loop-assigned local stay limited to
   core-iterator blocks; and `each_with_index` and `Array#each` with a `yield` in the
   block are not kinds. `each_index` is one now (see Landed), and NBody does not move with
   it: both of its `each_index` blocks hold the inner `Range#each` block, and the screen
   rejects a block holding a call with a block (the loop emitter cannot take one), so
   they are never offered to the loop compiler. What NBody needs next is a block loop
   whose body may hold another outlined iterator, the inner block's loop emitted in
   place; `each_with_index` needs two parameters, which the screen does not take either.
   Round fourteen built that (see Landed, perf/nest): the inner `Range#each`, `times`,
   `upto` or `downto` block runs in place inside the outer block's loop. NBody 0.59 of
   its time, 1364 -> 602 MB per iteration, 0.39 of CRuby. What is left of NBody: `energy`'s
   loops are entered too rarely to be outlined; an inner `each_index` or `Array#each` is
   not a kind that runs in place; an outer block that stores a global beside the inner
   iterator is refused (a Thread#raise at the inner safe point restarts the outer
   iteration, which takes locals back and cannot take a global back); and TruffleRuby is
   still 47 times faster on it.
   Round sixteen built the largest thing that was left here (`Util/bench/daloze-lessons.md`,
   idea 1; see Landed, perf/unboxed): a Float instance variable that typed code updates
   is stored raw, in a location of its own in a slot array whose shape says so. NBody
   0.76-0.80 of its time, 602 -> 152 MB per iteration, 0.35 of CRuby; TruffleRuby is
   still 35 times faster on it. What the 152 MB are has not been looked at. Not done,
   and each a way on: Integer instance variables are boxed as before (`@x += 1` on a
   value past the small-integer cache allocates 24 bytes; `ivar-float-shapes.rb` has
   the rows); a Float variable is only raw once typed code has overwritten it, so one
   that is written once in `initialize` and read from typed code stays a box; and the
   generic read of a raw object is a call (about 3 ns) because putting it into the
   inlined read cost every program 2-4% (see "Caught by the measurement (round
   sixteen)").
3. **CD's red-black tree** (`put`, `tree_insert`, `find_node`, `Vector2D#compare_to`:
   18.5% of self time now that `is_in_voxel` is compiled). `compare_to` is
   screen-rejected over its self call of `compare_numbers`, a three-line Float-only
   method, and `tree_insert` and `find_node` over their call of `compare_to`. Round nine
   inlines a small pure callee (see Landed), so `compare_to` and
   `Vector3D#squared_magnitude` are compiled: CD about 5% faster. The three tree methods
   are still out, for other reasons now: `tree_insert` over `Node.new` in the middle of
   the body (only a final construction compiles), `find_node` over `current.key`, an
   attr read on a local its `while` loop writes (method mode has no such read), and
   `put` over its `color` calls.
   Round ten compiled `find_node` (see Landed): a micro lookup is 0.6 of before, the
   share of `RedBlackTree#get` in CD's profile fell from 8.8% to 4.5% and
   `CallSite.Target` from 25.3% to 21.6%, and CD's time moved by less than its noise
   (598 against 598 ms in eight alternating pairs by its author, 611 against 578 in six
   of the gate's). `tree_insert` is now rejected only at `Node.new`: a deopt after a
   construction in the middle of a body would run `initialize` twice, so it needs the
   allocation's effects logged, or the deopt that resumes mid-body of gap 1.
   Round eleven looked behind `Node.new`. On master `IR_JIT_VERBOSE=1` shows only the first
   refusal ("screen rejected RedBlackTree/tree_insert: MethodCall new"); the rest is from a
   patch that was not kept (an environment switch that let any plain `C.new` through the
   screen and `EmitConstruction`), so none of it can be seen on master. Under that patch
   the next refusal printed was `z.parent = y`, "attribute assignment parent= on an
   unprofiled receiver" (a construction's result has no profile and is typed Obj). The two
   after it were not observed, since the emitter stopped at that one: they are read from
   `TryCompile`'s conditions. The loop's `x.value = value` is an effect, and an impure body
   with a loop may not deopt anywhere, though that branch returns at once; and the inlined
   `compare_to` after `z.parent = y` is a deopt after an effect. So the narrow version is,
   on that reading, four changes, not
   one: (1) a mid-body construction of a class whose `initialize` only writes its own
   instance variables from its arguments and literals, with the site deopting *before*
   the construction (not taking the generic `new` the tail construction falls back to)
   whenever the construction is not current or `:c_call` is traced, and never under
   `-X:ObjectSpace`, where `each_object(Node)` would count the instance a restarted body
   threw away; (2) the result typed as that class, so its attribute writers bind without
   a profile; (3) writes to that object before it escapes (stored, passed, returned) not
   counted as effects; (4) an effect followed by `return` in a loop not counted against
   the loop's other deopts. What it could buy: `tree_insert` is called 209,547 times per
   CD iteration (about 600 ms); on a 64-key tree of CD's `Vector2D` keys it costs 859 ns
   generic, called with `send` (CRuby 1259), a `get` of the same keys, whose `find_node`
   is compiled, 312 ns (CRuby 937), so at most some 450 ns a call, and much less on CD's
   own small per-frame trees: its 5.2% self share (round seven's profile; `perf` could
   not sample on this round's machine) puts the gain at 2-4% of CD, the size that round
   ten's `find_node` could not show against CD's noise. `Util/jit-construct-
   matrix.rb` pins construct-then-deopt (Integer overflow, a key of a class the site has
   not seen, a frozen node, `initialize` redefined to count, to publish self and to raise,
   the class reopened, the `tree_insert` shape with `compare_to` and a result constructed in
   the loop) and passes on CRuby 4.0 and IronRuby with and without the JIT; any of the four
   changes has to keep it passing. Its probes pass the warmed types, so they reach the
   deopt after the construction rather than an entry rejection, and it checks itself
   through the JIT's state: a probe that meets an invalidated method, or meets a compiled
   one and does not deopt, fails (exit status 1). Today every such probe reports "not
   compiled, semantics only" on stderr. See Rejected.
4. **Permute: the `downto` block in `permute`** (the method is 15% self). `permute` is a
   light frame (`IR_LIGHT_FRAMES=why` says so; an earlier version of this entry said it
   was not, from the `RubyMethodScope::.ctor` line of the profile alone), and the method
   JIT rejects it at the `downto` call. Its block reads the outer local `n1`, so every
   non-leaf call makes a RubyMethodScope, a Proc and a BlockParam, about 350 B, for a run
   of one to six iterations. Run length is not what keeps it out of the block-loop tier:
   the same block over `20.downto(0)` is screened in and then declined by the loop
   emitter ("call: swap/2", "operator perm on Obj, Int", "emitter gave up"), which has
   no general method call, only intrinsics. So lowering `MinRun` would buy Permute a
   failed compile per site; what it needs is gap 1's call from compiled code, or the
   `downto` compiled as a counted loop in the caller, which has to keep the block's
   backtrace lines and the `Integer#downto` frame, the stack check, `break`/`next`/
   `redo`/`return`, `&b`, `binding`, fresh block-locals per iteration, and fall back for
   a Bignum, a Float bound or a redefined `downto`. Round eight's experiment, on a
   loaded machine: with the block written as a `while` loop the method
   takes 834-1243 us per `permute(6)` against CRuby's 835-1010, and 1155-2286 against
   1121 as written, so the block is the whole gap; a larger gen0 does not help, so it is
   the yield machinery and not the garbage. `[BlockNotKept]` on `Integer#downto` changes
   nothing (149.73 B per call before and after): the Proc is new on every call, since
   each call evaluates the literal once and the frame's slot is empty. `n != 0` goes
   through `BasicObject#!=` (gap 10), and `Object::GetType` at 4.9% is `swap`'s type
   guards, by that experiment's reading of the profile.
5. **Rules bake the interpreted LightLambda of a method the JIT never wraps**
   (`LightLambda::Run*`, ~1% of Richards' `schedule`): a rule-level read of the body's
   current delegate, as b78ec3f5 did for stubs, would remove the hop once the body is
   compiled. `ObjectConstruction` could read a stub's `Target` too.
   Re-measured in round ten: `LightLambda::Run*` is about 0.8% of Richards' self time in
   total. Too small for the bar; closed.
   It was also built once, in the session of #215 after its round six (perf/hop, 102bcaa1
   and fca7d08e, never pushed): rules and `ObjectConstruction` read a cell per method body
   that takes the compiled delegate when it arrives. `LightLambda::Run*` went from 3.7% of
   Towers, 2.3% of Richards, 1.4% of Json and 1.1% of DeltaBlue to nothing, the full sweep
   and the matrices passed - and the least CPU time of a fixed run of the five benchmarks
   (`Util/perf/cputime.rb`, five rounds, the baseline twice) was 1.004 of the baseline's,
   whose own second column was 0.996. The frames go; the time does not. That confirms the
   close: a frame's share of a profile is not what removing it saves when all it did was
   forward.
6. **Richards: the worker block that contains a nested block** (`DATA_SIZE.times { }`
   inside the create_worker block): its scope is never reused and the inner Proc is made
   on every call - about 40% of what Richards still allocates. Needs reuse decided at
   block exit (nothing called `MarkCaptured`), with every escape route of the nested Proc
   checked first; `def capture(&b); b; end` (below) is what an unchecked one looks like.
   Closed in round ten: with `DATA_SIZE.times { }` rewritten as a `while` loop Richards
   allocates 3,204,249 bytes per iteration against 3,198,021 as written, so the inner
   block costs nothing measurable any more (round nine runs it in the compiled copy).
7. **Json**: the 40 B String per character read is what is left of `@input[@index]`
   (its content is shared now); two Integer boxes per `read` (`@input.length`,
   `@index += 1` - see Rejected, perf/box, for what caching them buys and costs);
   `@input[a..b]` is 160 B with a Range per call; `MutableString::Equals` 3.3% and
   `EqualsAsciiLiteral` 2.2% (5.7% in Equals before round seven): what is left is
   `@current != '"'` and `case @current when` in `read`, `read_value` and
   `read_string_internal`, which the JIT declines for their `raise` and their calls, so
   the compiled comparison of round seven does not reach them.
8. **`TakeBfcForBlockYield` 2.3% of DeltaBlue** is not the atomic exchange of 94dc9169:
   with a plain read and write in its place (an upper bound, not a correct change),
   SOM's `Vector#each` over 4 elements ran at 0.98-1.05 of before and over 100 at
   1.00-1.15 (round nine). Closed. What DeltaBlue pays is in gap 14.
9. **Light frames still exclude** a `while` with a `break` in it (`Array#take_while`),
   `super` (6 hot `initialize`
   methods; also `Array#sum` without a block, 5.3x CRuby, whose rare path is `super`), `rescue`,
   keywords, `$~` and regex literals.
   `IR_LIGHT_FRAMES=why` lists them. Also a Range per `(a..b).each`, and
   RubyInstanceData + RubyObject as two objects per Ruby object.
   `block_given?` no longer forces the scope (it is answered from the frame's block
   parameter, `IR_LIGHT_BLOCK_GIVEN=0` for the old call) and a rest parameter no longer
   keeps a method out (`IR_LIGHT_REST=0`), nor does a call that passes a block on
   (`inner(&block)`, `map(&:sym)`; `IR_LIGHT_BLOCK_PASS=0`, see Landed, perf/blockpass), nor
   one that splats a value (`inner(*args)`, `[*a]`, `yield(*a)`; `IR_LIGHT_SPLAT=0`, see
   Landed, perf/splat), nor an interpolation (`"a#{x}"`; `IR_LIGHT_INTERPOLATION=0`, see
   Landed, perf/interp; a block that interpolates still makes its scope), nor a Hash
   literal or the keywords of a call (`{a: x}`, `{}`, `f(a: x)`; `IR_LIGHT_HASH=0`,
   `IR_LIGHT_KEYWORD_HASH=0`, see Landed, perf/hashlit; `f(**h)` is still out);
   nor a multiple assignment (`a, b = b, a`; `IR_LIGHT_MASGN=0`, see Landed, perf/masgn);
   nor a regex literal whose method leaves `$~` alone (`s.match?(/x/)`, `/a#{x}/`; `IR_LIGHT_REGEX=0`,
   see Landed, perf/lightre: one passed to `gsub`, `=~`, `split`, `when` and the like stays out);
   Util/block-given-matrix.rb, Util/light-rest-matrix.rb, Util/block-pass-matrix.rb,
   Util/splat-pass-matrix.rb, Util/interpolation-matrix.rb, Util/hash-literal-matrix.rb,
   Util/masgn-matrix.rb and Util/regex-light-matrix.rb are their matrices.
   What the census names next (2026-11-19 after perf/hashlit, 28,335 methods, 6,542 of
   them not light). By its first table: keyword parameters 1,257 (37 of them the
   prelude's, none AWFY's), `rescue`/`ensure` 478, 185 candidates that still need their
   scope after the rewrite (in the prelude `Enumerable#zip`, `Enumerator::Lazy#zip`,
   `Enumerator#__size_from_target__`, `Process#detach`, `Process.waitall`,
   `__errno_class__`; nine of them were looked at in perf/hashlit and are a `rescue`
   inside a literal block, `a.map { |x| begin; x; rescue; end }`, by the subagent's probe), and other optional,
   splat or post parameters 176.
   The rest are refused by a statement of the body, and the census's second table says
   for which construct inside it: `super` 1,054, a regex literal 1,008, a parallel
   assignment 661, a `begin ... end` block 523 (the table calls it Body), `break` 268
   (258 of them in a `while`), `defined?` 190, a global variable 158, a Range literal in a
   body with literal blocks 138, a class variable 86, a qualified constant 76, a lambda
   literal 47, a `rescue` modifier 43. (The lines grew when Hash literals left: a method
   is counted under the first construct that refuses it.)
   Where it matters: the prelude's 191 are keyword parameters 37, a `begin` block 27, a
   parallel assignment 24, `defined?` 23, `super` 16, a regex 12, a `rescue` modifier 10;
   AWFY's 21 are a Range
   literal that receives `each` with a block 12 (`Vector#each`, `Sieve#sieve`; the scope
   is made for the block anyway, so letting the Range in buys nothing by itself), `super`
   6 (five DeltaBlue constraints' `initialize`, Richards' TaskControlBlock), a
   `begin ... end while` 2 (Json's `read_object`, `read_array`) and a regex 1. A regex literal was
   refused by itself although `$~` is written by the call that matches (perf/lightre took the part
   whose call does not); `defined?(@x)` is refused.
   A method with both a literal block and a passed one still makes its scope
   for the passed one's call (LightScopeCall leaves calls with a block to BlockLiterals).

10. **`!=` outside the method JIT is two dispatches**: `BasicObject#!=` calls `==`
    through a site shared by every `!=` in the runtime, so that site is polymorphic. 2.9%
    of Richards inclusive (`NO_TASK != @current_task`), 2.1-2.6% self in Json and Permute.
    See Rejected (perf/richards) for a correct fast path that was too small to show.
    #215 has since bound `a != b` as the negated builtin `==` for nil, booleans, plain
    objects, two Integers and two Strings (05a31f0a, merged into this branch after round
    nine): the cases this session's perf/neq1 found faster, without the Integer-against-
    Float one it found slower.
11. **The method JIT refuses a constant whose value is nil** ("constant without a value":
    it cannot tell nil from an autoload that has not run). Richards' `NO_WORK = nil` and
    `NO_TASK = nil` keep three methods out. See Rejected for why fixing only that is not
    worth it yet.
    Round ten built the fix and did not keep it (see Rejected): a nil constant is not
    what keeps Richards' hot methods out. Its table of them, by `IR_JIT_VERBOSE` with the
    first obstacle patched to see the second: `schedule` (94.9% inclusive, 7.4% self) a
    general self-call (`trace`), then `run_task`; `run_task` (76.0 / 4.5) `@function.call`;
    `queue_packet` (16.3 / 2.9) and `release` `find_task`, which raises;
    `add_input_and_check_priority` (5.8 / 2.5), `device_in_add`, `work_in_add` `append`,
    which has a loop; `append` (3.1 / 2.0) the nil constant, then a receiver of unknown
    type, then - with those patched - a deopt after an effect (it starts with the store
    `packet.link = NO_WORK` and then reads in a loop); `hold_self` (2.0 / 0.9) the same
    (`@hold_count += 1`, then an attr write on another object); and five blocks
    (`create_handler`, `create_device`, idler, worker: 23.2, 13.6, 9.8 and 7.0% inclusive),
    which the method JIT does not take at all. So Richards waits on gap 1: general calls
    from compiled code, and a deopt that does not restart the body.
    Round fifteen landed it after all (perf/core2, see Landed), for its own shape and not
    for Richards: `NIL_CONST == x` in a small method is 0.63-0.66 of before. Closed.
12. **One site, four receiver classes, two speeds**: `recv != other` in a loop over four
    plain classes takes about 105 ns or about 280 ns per iteration, the same binary, from
    one process to the next (three of six runs slow on 9f4d44f8). Two and three classes
    do not do it. Unexplained, and outside AWFY, but a factor of 2.6 on a polymorphic
    site is worth an afternoon. Bounce may be the same thing: one tree's steady state
    was 64 ms in one run and 81-83 ms in three others of the same session.
    Round thirty-three (see Landed, perf/pwalk): a site in a method without a scope
    (`node.accept(self)` in a visitor) built and compiled a rule per receiver class, and past
    the DLR's rule caches kept building them again: 52 us a call over 60 classes. Such sites
    now take a precompiled dispatcher per class (127 ns). Left: `prism_walk` spends about 12%
    in the dispatcher chain and its cache lookup; a table from class to dispatcher per site is
    the next idea. A light caller's call of a JIT-typed method with a Float argument (`selfcall`
    of micro-table) is 3% slower by median through the dispatcher than through the rule, with
    the same bytes.
13. **Calling an iterator at all costs twice what it does in CRuby**: `a.each { |x| s +=
    x }` over one element from a method is 166 ns against 79, `n.times` 182 against 88,
    `a.map` 194 against 101, and `a.any? { |x| x > 1000 }` 414 against 67, 438 against
    97 over two and 476 against 154 over four (the `s_*` lines of the table). The
    per-element cost is near CRuby's by four elements; the fixed part is the Proc and
    BlockParam, the method scope where the block reads an outer local, and for `any?`
    something of its own that nobody has looked at (a factor of six on one element).
    Short collections are most collections: DeltaBlue's `Vector#each` runs over one or
    two elements in most of its 20,592 calls per iteration.
    Round ten: `any?` was the prelude's doing (see Landed) and is at 2.4 of CRuby over
    one element, from 6.2. For `each`, `times` and `map` the fixed part was measured and
    left: a method whose body has a literal block allocates 335 B per call against 23.5
    without one - about 184 B for the method's scope, which it makes on every call once
    it contains a block literal (50318082; the outer local the block writes adds 8 B),
    and about 128 B per iterator call for a Proc and its BlockParam. `[BlockNotKept]`
    reuse cannot help across calls: the previous Proc is kept in a hidden variable of the
    frame that makes it, and each call is a new frame. In the profile of `s_each1`
    allocating and filling scope, Proc and BlockParam is 30-35% of the 190 ns
    (`JIT_WriteBarrier` 11%, allocation 12.6%, `RubyMethodScope..ctor` 4.7%, memset 4.2%).
    Untried: the dispatcher keeps the last Proc with its BlockParam, taken with an atomic
    exchange as `TakeBfcForProcCall` does and given back only after a normal return when
    nothing captured it; then the light method's scope parked the same way. That is the
    "parked scopes" idea of the Rejected list in a narrower form, in the area where five
    of the ten faults were, so every escape route of the Proc is a case before it is a
    change.
    Round twenty-one takes this up for a reason round six did not have (perf/lfb, the
    parked scopes, was rejected because none of the five benchmarks measured then moved):
    Havlak, measured since round fourteen, has 30% of its samples in the runtime and
    makes about 3.4 million calls per iteration of som.rb's `Vector#each` and `has_some`
    (`(a..b).each { |i| yield @storage[i] }`). On 3081699d that shape over one element is
    336 ns and 685 B a call against CRuby's 175 ns, and the same loop written as a
    `while` 232 ns and 357 B against 104: the inner layer (the method's scope, the Proc,
    the BlockParam, the Range) is 328 B and 105 ns, the layer around it - the caller's
    literal block that writes a local - the rest. Done in round twenty-one (see Landed,
    perf/park): the scope and the Procs are kept for the body's next call, the 288 B are
    gone from both layers, `s_each1` is at 0.71-0.74 of its time and 1.6 of CRuby's from
    2.2. Havlak's time did not move: its 0.79 upper bound was the block invocation per
    element (gap 14), not these bytes. What is left of the fixed part: the Range of
    `(a..b).each` (40 B), a literal given to an iterator that is not known to only
    yield (`upto`, `any?`, `inject`, every Ruby-level one: no slot, the old price), and
    the half of the time that is not allocation.
    Round thirty-six (perf/pwalk36, nothing kept but `Util/bench/pwalk36-shapes.rb`):
    `prism_walk` is this gap - a call with a literal block from inside a block on every node
    (`o.e { |c| s += c }` in a block 146 ns, CRuby 45), plus about 50 ns where one site sees
    sixty receiver classes (`node.accept(self)`). A dispatcher chain of 64 instead of 8 made
    the walk about 12% faster and a megamorphic call without a block 2.2 times slower:
    reverted. A per-site table from class to dispatcher is worth at most about 10% of the
    walk. `Prism.parse` itself (not in the shape) is 8 times CRuby's.
14. **DeltaBlue: `Vector#each`** (`(@first_idx..(@last_idx - 1)).each { |i| yield
    @storage[i] }`) is about 20% of its samples by nearest Ruby frame (8.9% the method,
    11.3% its block), and no call site owns more than 1.2% of the 24% in
    `CallSite.Target`. Per element it is two block invocations: on a 100-element run
    `TakeBfcForBlockYield` 10.4%, `Yield1` 9.6%, `EnterScopeFreeBlock` 4.1%,
    `GetInnerMostMethodScope` 3.1%, about 54 ns against CRuby's 43; per call about 670 B
    (method scope, Proc, BlockParam, Range), which cannot fall while the yield inside
    the block finds the method's block through the method scope
    (`GetMethodBlockParameter` and `GetMethodBlockParameterSelf`, two walks per yield).
    The block-loop tier does not take the block: its body is a `yield`. Untried: a
    block-loop kind whose body is `yield <expr>`, or the method's Proc resolved once per
    iterator call.
    Round ten tried the second and assessed the first (see Rejected): neither is where
    DeltaBlue's time is. In a fresh profile of DeltaBlue itself the yield machinery is
    6-7% of self time (`block in Vector#each` 4.3%, `TakeBfcForBlockYield` 2.8%, `Yield1`
    2.2%, `Vector#each` 1.9%, `BlockDispatcher1::Invoke` 1.5%); the 10% shares above were
    of a 100-element micro. What is left for DeltaBlue is gap 13 (the fixed cost of its
    20,592 `Range#each` calls per iteration, most over one or two elements) and gap 1
    (`CallSite.Target` 23% in `add_propagate`, `satisfy`, `Plan#execute`'s block).
15. **Core methods written in Ruby are at 3.1x CRuby, those in C# at 1.6x**
    (`Util/bench/core-small.rb`, 67 calls on four elements; `Util/bench/truffleruby-lessons.md`
    section 3). The cause is the entry, not the method: a rest parameter, a named block,
    one layer of `inner(*args, &block)`. Worst prelude rows on 2026-11-15, master / CRuby:
    `Array#sum` 5.3, `(1..4).sum` 4.0, `Kernel#tap` 3.9, `Hash#sum { }` 3.6, `then` 3.2,
    `uniq` 2.9, `tally` 2.1; and
    `String#strip`/`lstrip`/`rstrip` cost 145 ns over the C# method they wrap. `inject`
    and `reduce` are done (2.0 -> 1.3, `inject(:+)` 6.7 -> 4.0), and so is `Array#dig`
    (15.1 -> 6.1, see Landed), and `Array#sum` with a block (6.5 -> 3.3), and `String("a")`
    (5.2 -> 2.4). Next: `String(5)`, `String(nil)` - about 1.6 us each against CRuby's 180,
    in `Kernel#__ir_string__`, which asks whether #respond_to? is Kernel's
    (`method(:respond_to?).owner`) and calls #to_s through `bind_call`; CRuby decides that in
    C (rb_check_funcall). Probably needs a C# helper, which is /perf-step's, not /perf-core's.
    `Array#to_h { }` is done too (4.7 -> 2.3), and `min_by`/`max_by` (3.1 -> 2.2), and their count form
    (`min_by(2)` 3.8 -> 2.9 after the review fix below). Open beside speed: `min_by(n)`'s tie order is input order,
    MRI's nmin selection orders ties its own way. `take_while`/`drop_while` done (2.6 -> 2.2,
    now Array's own as in MRI), and `flat_map` (2.2 -> 1.7), `filter_map` (2.1 -> 1.6).
    /perf-core loop stopped 2026-11-18: what is left above 2x (`Array#sum` and `Range#sum`
    without a block, `Hash#sum { }`, `Array#dig` at 6x) is blocked on frame shapes -
    `super`, `{}`, a `while` with `break`, a block passed on - which is /perf-frame's. `uniq` is not this skill's:
    its C# method alone costs what the call does (500 ns against CRuby's 230). One method per run of
    `/perf-core`. `tap` and `then` are already minimal Ruby: what they pay is gap 13.
    Round thirty-four (see Landed, perf/time2): ASCII `Date._strptime` is C#, `strptime` 0.2-0.3
    of its time. Left: `Date._parse` and `Time.parse` about 3x CRuby (Ruby regexps and Hash
    building in `format.rb`), a "UT-3" style offset, UTF-16 subjects' errors, and a subclass of
    String taking the Ruby path.
16. **A light frame whose usual callee wants the caller's scope costs more than a frame
    with a scope**, and **the splat itself is what a forwarding wrapper still pays**. Both
    are what is left of "wrappers that splat stay out of light frames", which is done (see
    Landed, perf/splat, 2026-11-19).
    The first: `inject(:+)` and `Array#zip` became light frames with the splat and gained
    nothing. They allocate what they did (408 and 1314 B a call, switch on and off) and
    count 4.9% and 2.9% more instructions (`ab.rb -i`, four rounds, against the same binary
    with `IR_LIGHT_SPLAT=0`). Inferred from the unchanged bytes, not traced: the call they
    make answers `LightScopeRequired`, the scope is made late and the call is made again,
    which costs about 150-200 instructions more than a scope made at entry. `inject` with a
    block, in the same method, lost its scope (658 -> 498 B, 3% fewer instructions). A
    C#-only shape the change cannot reach (`minmax`) read 4.6% more in that table, so 5% is
    about the floor for one shape there. Untried: a site that has answered
    `LightScopeRequired` remembers it, and the frame makes its scope before that call the
    next time without asking; or the prelude gives such a method's rare path a method of its
    own, so that the common path stays without a splat or a scope-reading callee
    (`/perf-core`).
    The second: `w_splat` is 366 ns against 188 for the bare call (CRuby 194 and 71), and
    still about 185 B a call more than the block-passing wrapper: the rest Array the
    caller's rule builds and its unpacking into the callee. Untried: a rule for a splat
    site whose Array is the caller's own rest parameter, passing the elements on without
    the copy (TruffleRuby's splat of a rest Array is the same Array when it does not
    escape).
    The third (found 2026-11-21, perf/masgnfix): **a large light frame compiles to two or
    three times the IL of the same body with a scope**, and past the CLR's limits it is
    compiled without optimization. Each call becomes the light site, a test of its answer
    and the call again with the scope made on demand, and that copies every register local
    into a new tuple (`ScopeBuilder.MaterializeLocals`, once per call site); each access of
    a register local becomes a test of the tuple. `String#bytesplice`'s body, light: 49 KB
    of IL against 15 KB, and `DOTNET_JitDisasmSummary=1` says `Tier-0 switched MinOpts` (the
    scoped one `FullOpts`). No scope is made late (a counter in `CreateMethodScopeLazy`
    read 0 after warm-up), and the light frame allocates less (656 against 960 B a call).
    Not only a multiple assignment: the same body with its multiple assignments written as
    single ones (a light frame before perf/masgn too) counts 13,295 instructions a call light
    against 11,486 with `IR_LIGHT_FRAMES=0`, and 11,171 light without register locals (a
    temporary switch; FullOpts at 29 KB). Straight-line bodies with ten locals: the gain
    shrinks with the number of calls (8 calls 0.93, 16 0.95, 32 0.97, 48 1.05). perf/masgnfix
    only keeps a body with a multiple assignment and more than 24 calls out. Untried: register
    locals only for a small body (the numbers above say that alone recovers most of it),
    and one copy of the scope-making code per frame instead of one per call site.
    **perf/lightsize** did the first (at most 40 calls; that copy of `bytesplice` 0.82, and
    0.97 of its scoped count) and found the second cannot be built in an expression tree (a
    site is inside an expression, which control cannot enter). Left: the arms still double a
    large light frame's IL without register locals (m48 11.7 KB against 5.7), and a body with
    a multiple assignment and more than 24 calls loses 2-4% light for a reason not yet found.
17. **The prelude calls its C# helpers as methods**: about 230 private `__x__` methods,
    called 1,928 times in `ruby4.rb` through call sites (8-12 ns each, a rule to bind per
    site at start-up, 0.2-0.4 ms). TruffleRuby's `Primitive.x` is replaced at parse time.
    Untried: a name the compiler turns into a direct static call in prelude files only
    (lessons, section 4, item 4).
18. **A `while ... yield` in Ruby runs at ~100 ns per element** against 9 for a C#
    iterator compiled with its block, and at twice CRuby's time for the same source: the
    loop compiler gives up on `yield` (see the Rejected entry "A block whose body is
    `yield <expr>`"). This is what keeps per-element loops in C#.
19. **`Object.new.extend(M)` is 200 to 300 times CRuby's time and grows with the number
    of objects extended**: 20,000 in a `while` loop take 4.5 s, 40,000 11.0 s, 200,000
    72 s, with and without the JIT and OSR; CRuby takes 0.23 s for 200,000 (1.1 us each).
    `Struct.new(:a)` is 105-115 us each against 3.6 (300,000: 32-35 s against 1.1 s),
    `Class.new { def x; 3; end }` 15 us against 0.7. Found in round fifteen by a probe
    written for something else (a loop whose own body changes method tables); nobody has
    looked. First guess, unverified: the module's list of dependent classes takes a weak
    reference per singleton class ever made and is scanned on each update. Outside
    AWFY, but DCI-style code and anything that extends per request pays it.
    Round sixteen fixed the growth (perf/extend, see Landed): the guess was right, and
    an extend is now 11-25 us whatever came before, ten times less at 20,000 and thirty
    at 200,000. What is left, from its author's `IR_BIND_TRACE=1` runs, is the first
    call on each brand-new class, still 20 times CRuby for `extend`: 2-7 us to bind a
    precompiled dispatcher for `o.foo` on a new singleton class; 70-120 us for the first
    call of a method defined in `Class.new { def x }` or by `def o.x`, because
    `RubyMethodBody.GetDelegate` compiles the body again for each declaring module (the
    module is baked into the code; making the body independent of it has to keep
    `super` and refinements right); and for a new Struct class full rules instead of
    precompiled dispatchers, about 113 us for `new` and 51 us for a getter, since the
    getter's and setter's rule generators are closures made per class and never match
    a shared rule. Also unexplained by measurement: a `Struct.new` moves the global
    method version (reading the code: `new` and `[]` added to the struct class's
    singleton class override `Class#new`, which invalidates sites on override), so it
    throws away every running loop's copy and every typed body's entry.
20. **Array and Hash are not safe under concurrent mutation** - a correctness gap, listed
    here because the fix has a speed price and the papers say how to keep it at zero for
    code that does not share. Eight threads appending to one Array raise "Source array
    was not long enough" out of the Array's copy (two runs of three); eight threads
    inserting into one Hash raise IndexOutOfRange or lose entries (383005 and 394876 of
    400000, with wrong values). CRuby prints the right answer, and so do TruffleRuby's
    collections by design. `Util/bench/daloze-lessons.md`, sections 3 and 5: a shared
    bit per collection, set when the collection becomes reachable from a global, a
    constant or another thread, and a lock only on shared ones; their numbers are no
    measurable cost for local collections and 14% for a shared Hash on one thread. The
    stop-the-world safepoint their design uses for one transition does not exist here,
    and the design without it is written out there. Not started: it is a decision about
    what IronRuby promises, not an experiment of this loop.
21. **Searching a String**: `s.index('"')` over 10 KB is 5 times CRuby's time (748
    against 146 ns); `re.match?(s)` on a UTF-8 String takes four times what `re =~ s`
    takes (3.1 against 0.76 us for a character class not found in 10 KB; on a binary
    String `match?` takes 0.47 us), although `match?` has less to do; a short anchored
    match is 1.3 to 1.5 times CRuby (`/\A[+-]?\d+/.match("12345 rest")` 663 against 452
    ns). One run beside two builds, so factors only; shapes and a reading of the three
    entry points are in `Util/bench/daloze-lessons.md`, section 6. Reading the code did
    not explain the first two; the experiment starts with finding out (a one-character
    needle straight to a span search, `match?` without a Match object). Outside AWFY;
    it is what JSON escaping, `strscan` and log parsing spend.
22. **A `begin`/`rescue` or `ensure` in a method costs about 80 ns per call when nothing
    is raised**: `def r(x) begin; x + 1; rescue StandardError; 0; end end` 106 ns against
    CRuby's 24, the same with `ensure` 112 against 27 (`Util/bench/zjit-post-shapes.rb`,
    one loaded run: factors). Gap 9 seen from the caller: no light frame, no typed body.
    A .NET `try` costs nothing on the path that does not throw, so the experiment is to
    find what the method does around the handler (the scope, the saving of `$!`, the
    frame) and let a body whose handler is not entered run as light as one without.
    And **a raise that is rescued costs 6.3 us against 0.64**: a .NET exception per use.
23. **A method made by `define_method` is called at 2.3 times CRuby's time** (64 against
    28 ns; a `def` of the same body 31; YJIT 2-5 ns): the method JIT takes no block as a
    method body. Rails-style code defines most of its small methods this way.
    Tried in round thirty-one (see Rejected, perf/dmeth): without the method JIT the call is
    only about 1.15 times a `def`'s, and a pooled BlockParam and a direct rule bought 1.16-1.2;
    what is left is the JIT taking a block body (a `JitStub` over a `BlockDefinition`).
24. **Struct with `keyword_init: true`: creating an object is 3.2 times CRuby** (1162
    against 369 ns; positional creation is level at 142 against 131). And
    `h.each_pair { |k, v| }` / `each_with_index` are 1.6 to 1.9 times CRuby (gap 13 with
    two parameters).
    Round thirty-four (see Landed, perf/inv2): `Data.new`, `#with` and Data readers are direct;
    the invoice shapes are at CRuby's time. Left: initializers taking keywords (`Product.new(sku:
    ...)`) at about 2x CRuby, `Hash#dup`, and Struct `keyword_init` as above. And
    `Data.singleton_methods` lists `:allocate`, which CRuby's does not (as on the baseline).
25. **Havlak: 1.4 times CRuby, 25 times TruffleRuby, and no single place** (round
    sixteen's breakdown; its author's numbers, taken beside two other experiments at load
    2-14, so ratios). CRuby runs a copy with som.rb's `Set` renamed to `SomSet`: 2.85-3.3
    s per iteration against IronRuby's 3.8-4.8. By phase, IronRuby/CRuby: allocating the
    per-node objects 0.64, the DFS 1.87, `identify_edges` 1.18, the main `downto` loop
    1.49. It allocates 2.72 GB per iteration (66 `find_loops` of about 53 MB), and about
    half of that is the `Range#each` inside som.rb's `Vector#each` and `has_some`: 52,000
    calls per `find_loops` at about 500 B each (the Range, the Proc, the BlockParam and
    the method's scope) - gaps 13 and 14, whose two obvious steps are under Rejected.
    Other bytes per operation: `IdentitySet#add` 987, `find_set` over a chain of three
    971, `at_put` of an existing key 693, `loop { return }` 536, `Dictionary#at` 71.
    Per operation in ns, CRuby / IronRuby / TruffleRuby: `Dictionary#at` 225 / 164 / 23,
    `at_put` 500 / 384 / 52, `Vector#each` over 1-3 elements 363 / 457 / 25,
    `IdentitySet#add` 496 / 839 / 35, a new node 163 / 125 / 1.6, `find_set` 886 / 969 /
    54, `has_some` 473 / 530 / 51, `loop { return }` 110 / 288, the DFS per node 1705 /
    2976: the single operations are at CRuby's speed, the composite ones (a block passed
    on through two layers of `&block`, `loop`) are not. The method JIT specializes 21
    methods, declines 3 and screens out 109: `Dictionary#at` at a call of `match` on a
    local assigned in its loop, `Vector#remove_first` at a deopt after an effect,
    `split_bucket` at an attribute write on a receiver that was not profiled, and
    `find_loops`, `do_dfs`, the steps, `SomSet#add`/`contains`, `find_set` and `append` at
    `new`, `each` and `has_some`; five `while` loops are retired at calls. Recursion
    depth is not it: the DFS goes 424 deep, and recursion through `Vector#each` costs
    475 ns a level at depth 10 and 552 at depth 1000. Narrow things to try: let
    `Dictionary#at` compile (type a call on a local assigned in the loop); the per-call
    cost of `(a..b).each { |i| yield ... }` (gap 14); `loop` through gap 22.
26. **Start-up: `gem` and `irb` answer in 0.7 to 2.2 s, CRuby in 0.1 to 0.2** (the one
    thing that has to stay fast while the tiers stay on by default, and it is not fast).
    2026-11-18, a7060a27, the ReadyToRun host (`IR_CONFIG=R2R`, what `ir.sh` runs when
    `IR_CONFIG` is not set and what a release ships), best of five, ms, load 2.0 (an
    experiment was building beside it): IronRuby / with `-X:NoJIT -X:NoOSR` / CRuby /
    TruffleRuby native -
    `-e 1` 526 / 538 / 54 / 18; `gem --version` 683 / 665 / 118 / 392; `gem list` 1651 /
    1496 / 217 / 836; `-e 'require "irb"'` 1389 / 1316 / 153 / 663; `echo 1+1 | irb` 2184
    / 2060 / 154 / not taken. (CRuby's and TruffleRuby's were taken at load 1.1 a few
    minutes earlier.) The Release build without ReadyToRun, which is what this ledger's
    "start-up" lines and the gate measured until now: 1644, 1912, 3475, 3214.
    What that says: the tiers are 0-10% of it (most on `gem list`, where much Ruby runs
    once), so switching them off buys nothing; RubyGems is about 165 ms of `-e 1`
    (`--disable-gems`: 361); and the tools pay most after the runtime is up - `gem list`
    1.0 s beyond `gem --version`, an irb session 1.7 s beyond `-e 1` - which is Ruby code
    being parsed, compiled and run for the first time. Nobody has split that into
    parsing, building expression trees, the .NET JIT on them, call-site rules being
    made, and the cold code itself; that split is the first experiment here.
    What exists already, outside this ledger: the ReadyToRun host with a start-up profile
    (`Util/publish-r2r.sh`, `Util/make-startup-mibc.sh`; its header gives 1.37 -> 0.93 s
    for `-e ''`); a start-up library built by `Util/aot/build-startup.sh` and checked by
    `Util/aot/check-startup.sh` (`Util/aot/README.md`: the prelude compiled to IL in a
    saved assembly, so that a process starts without parsing it); and a warm-start
    prototype (`Util/warm/README.md`: a pool of processes that have already started,
    `IR_SERVER=1`, by its own figures `-e 1` in 15 ms, 70 MB per idle worker). None of
    these three was measured here.
    How the others do it: CRuby compiles nothing at start and has its core in C;
    TruffleRuby's native image has the core library loaded in the image (18 ms) and then
    runs `gem` and `irb` cold in its interpreter, 3 to 4 times CRuby's time - the same
    shape as here, at a third of the size.
    Ways on, none tried in this loop: the split above; RubyGems and irb's own files in
    the saved assembly as the prelude is; loading RubyGems lazily (TruffleRuby does);
    what a cold call site costs to bind the first time, since a tool that runs each
    site once pays only that; and whether the warm pool can be the default for
    `gem` and `irb`. The gate times these commands now (perf-gate, step 4), so the
    work on the tiers cannot make them worse unseen.
27. **`send` with a String name leaves a `Kernel#send` frame in backtraces** that CRuby
    does not show (`[1].send("sum") { raise }`: IronRuby "Array#sum < Kernel#send", CRuby
    "Array#sum < <caller>"); with a Symbol it is right. Found by /perf-core's probes, not a
    speed gap.
28. **`-w` warns of an unused block where CRuby does not**: `def given; block_given?; end`
    called as `given(&b)` from a method that received `b` prints "the block passed to
    'Object#given' ... may be ignored" under `ir -w` on master too; CRuby 4.0 `-w` printed it
    for a three-line file but not inside a longer one. Found while writing
    `Method_LightFrameBlockPass1`, not a speed gap; not looked into.
29. **A typed body that reads a constant is thrown away by every `extend` and every new
    constant, and after eight of them for good** (found in round twenty-four; on the
    unchanged build too). `def top; TOP; end` and `def qual; G::P::RUBY; end`, called 300
    times in each of 20 rounds: specialized once when nothing else happens, and nine
    times when each round also does `Object.new.extend(Comparable)` or
    `Object.const_set("K#{k}", k)` - after which the stub settles on the generic body
    (`MaxRespecializations`) and pays its hop for the life of the process. A method
    without a constant (`x + @a`) is specialized once in all four cases, and a `def` or a
    singleton method in each round disturbs none of the three. So the guard is the
    constant's: one global version that anything defining a constant or making a singleton
    class moves, where a program does both all the time while it loads (every `require`
    after warm-up, every `extend`). The benchmarks do neither once they run, so no number
    here shows it; a program that keeps loading loses every typed body with a constant in
    it (how many of the census's methods that is has not been counted: its `maybe` table
    has 399 that stop at "constant without a scope", which are methods of that kind, and
    the ones that compile with their constant are not told apart).
    Done for method bodies in round twenty-five (see Landed, perf/constrev): the entry
    looks the folded constants up again and keeps the body. What is left: outlined loops
    (`JitLoopCompiler`) still guard on a version baked into them, so a loop compiled in a
    method that reads a constant hands itself back at its next entry after any constant
    is set; a lookup made while an autoload is in flight still throws the body away, and
    counts against the eight; and what one revalidation costs on a program that moves
    the version on every iteration (`Object.new.extend(M)` in a loop, gap 19) has not
    been timed - each call of each such method then takes the class hierarchy lock once
    per lookup, as each generic constant site does.
    A fault of the same guard, found in round thirty-one and still open: a compiled loop
    that is running and has folded a constant goes on answering it after a nearer constant
    of that name appears (another thread defines `M::SHL10` lexically nearer than the folded
    `Object::SHL10`, a superclass gets `SHS10`, an included module brings `SHI10`); CRuby's
    next read answers the new one. A new constant moves `ConstantAccessVersion`, which the
    loop's safe point does not compare. The fix that moved the code epoch for any new
    constant whose name had been folded was taken back (see Rejected, rminor's epoch move):
    the name cannot tell a constant that shadows the folded one from any other of that name.
    The next attempt keys on where the constant was found (the modules of the folded lookup's
    path), and is measured on a loop beside a thread that defines the same name elsewhere.
30. **`Binding#local_variable_get`, `_set`, `_defined?` and `local_variables` are an
    `eval` of source text each** (the prelude, `class Binding`): 436 us for a set against
    0.1 on CRuby. `ERB#result_with_hash` sets one per key (237 times CRuby), irb sets `_`
    on every line, rdoc's generator sets its page's locals. The scope has the access in
    C# (`RubyScope.ResolveLocalVariable`, `ResolveAndSetLocalVariable`). Done in round
    twenty-eight (see Landed, perf/bindlv): 469 -> 5.5 us, `erb_result` 3-4 times faster;
    what is left of ERB's time is the `eval` of its source (gap 31).
31. **`eval` of a String costs fifty times CRuby's** (219 us for `eval('1 + 1')`, 177 for
    `instance_eval('1')`, 741 for a three-line ERB source): each call parses, bridges,
    transforms and compiles its text anew. Untried: where the 219 us go (the native
    parse and the bridge, the transformation, the light compiler's first run), and a
    cache of the compiled code by source text, file, line and the scope's layout, which
    is what a template evaluated per request needs.
    Measured and tried in round thirty-one (see Rejected, perf/evalc): of about 250 us for
    `eval('1 + 1')`, the native parse is 20, prism's load 10, the bridge 23, the
    transformation 60, the light compile and the default file name the rest; with no file
    given, the name `(eval at f:l)` takes a CLR stack walk of 30-90 us (`caller(0)` costs
    15-20 us, CRuby's 2.9). A cache of the compiled code made a hit cost 1 us with a file
    and 33 with none, and `erb_result` 0.41 of its time; it was rejected because compiled
    code keeps per-site state in boxes of its own that a second eval must not share.
    Round thirty-two (see Landed, perf/caller): the default file name is read from the frame
    records, without the CLR walk: `eval` without a file at 5 and 50 frames deep 0.60 and
    0.46 of its time, `instance_eval` and `binding.eval` about half. Left: the parse, bridge,
    transformation and compilation of every text (about 15 us at the least, CRuby's 2), and
    `caller(0)`/`caller_locations` paying the whole CLR walk.
    Round thirty-three (see Landed, perf/erbr): the blocks of eval'd text are compiled after
    1000 runs instead of 32, and `ERB::Escape.html_escape` is C#: `erb_result` 0.68-0.81 of its
    time. Left: an eval's own cost is about 1 ms on a template (light compile, transformation,
    bridge), and the template's body run as a method is about 8 times CRuby's.
    Round thirty-four (see Landed, perf/erb2): the parsed tree of an evaluated text is kept, so a
    template's parse and bridge cost 5 us instead of 72; `erb_eval` 0.77 of its time. Left: the
    transformation (~50 us) and light compile (~80 us) of every eval.
    Round thirty-six (see Landed, perf/erb36): the compiled code of an eval'd text is kept
    when a walk of its whole tree, blocks included, finds nothing that keeps state of one
    eval (no regexp, range, flip-flop, END, `defined?`, `super`, `yield`, `def`, `class`,
    `module`) - the screen perf/evalc lacked. `erb_result` 0.31-0.33 of its time, `eval_small`
    0.40. Left: the binding's locals read by name (about 300 `GetLocalVariable` per render,
    each under a lock), and an eval of a text the screen refuses costs what it did.
32. **A singleton class, and a method defined at run time, cost 25 to 60 times CRuby's**
    (`Object.new.singleton_class` 17 us, `def o.x` 18, `define_singleton_method` 46,
    `Class.new` 12, with a `def` 37), and `OpenStruct.new(h)` of three keys, which
    defines a reader and a writer on each object, 2.6 ms - far more than six definitions
    add up to, so most of it is what a definition invalidates (the method version every
    call site and typed body is keyed on) and not the definition. Gap 19
    (`Object.new.extend(M)` at 200 to 300 times) is the same family. Untried: what one
    `def o.x` invalidates, counted (rules rebuilt, typed bodies respecialized), on
    `ostruct` of `app-shapes.rb`.
    Counted in round thirty-one (see Landed, perf/sdef): of `OpenStruct.new`'s time most was
    the rule of `respond_to?(name, true)` built and compiled anew for each object's singleton
    class (0.1-0.2 ms each); sharing that rule made it 663 -> 59 us (CRuby 8.7). `def o.x`
    (27 us), `singleton_class` and `Class.new` are unchanged.
    Round thirty-two (see Landed, perf/sclass): from the third module on, a `def` takes a
    delegate from one compiled factory: `def o.x` with its first call 0.24-0.27 of its time.
    Left: `singleton_class` (1.4 us here, mostly the class itself), `define_singleton_method`
    (4-9 us, which the factory does not cover: its body is a block), `extend` (gap 19), and
    the scope per call of a factory-made body.
    Round thirty-three (see Landed, perf/ostr): readers at light sites, `respond_to?(:x)` and
    the first setter through `method_missing` no longer build a rule per object's singleton
    class: `ostruct` of `app-shapes.rb` 0.56 of its time, `ostruct3` of `sdef-shapes.rb` 0.14-0.16.
    Left: `OpenStruct.new` is about 120 us against CRuby's 10, some fifteen binds per object.
    Round thirty-four (see Landed, perf/ostr2): a shared rule's test takes in a re-versioned
    singleton class that resolves the same: binds per OpenStruct 17 -> 4, `ostruct` 0.30-0.33 of
    its time. Left: the four binds of per-object readers (a different method on each object);
    the resolution in a polymorphic rule's test, which remembers one rejected version only; and
    the lock that resolution takes inside a rule test.

    Round thirty-six (perf/ostr36, rejected, shape file kept as `Util/bench/ostr36-shapes.rb`):
    per OpenStruct, 23 `RuleVersionSet.IsValidFor` -> `TryShareVersion` (each
    `define_singleton_method!` re-versions the singleton class, so every self call resolves
    again under the hierarchy lock and the set's lock; about 0.5 us each, 11 us an object),
    4 binds (1.3 us, not the problem), 12-20 KB allocated. Moving `Ractor.shareable_proc`'s
    two ConditionalWeakTable entries onto the Proc measured nothing. Next idea: a lock-free
    test for a singleton class with no mixins that does not define the name itself (it
    resolves as its superclass), at most about 10%.
33. **Three differences from CRuby in interpolation**, found by the probes of
    `Util/interpolation-matrix.rb` on the build before perf/interp and left as they were
    (the matrix avoids them); not speed gaps. When a part's `to_s` raises, the backtrace
    names the method's first line for that frame where CRuby names the interpolation's own
    (`def m(x)`, `y = 1`, `"a#{x}"` on lines 10-12: IronRuby 10, CRuby 12), with and
    without a scope. A TracePoint `:line` event is reported for the body of an endless
    `def`, CRuby reports none. And with a refinement of `Symbol#to_s` in use, `"#{:s}"` asks
    the refined `to_s`; CRuby answers `"s"` without asking it.
34. **Two differences from CRuby in Hash literals**, found by the probes of
    `Util/hash-literal-matrix.rb` on the build before perf/hashlit and left as they were
    (the matrix leaves them out, with a comment); not speed gaps. A key that is an
    instance of a String subclass is copied and frozen (`k = MyS.new("k"); h = {k => 1}`:
    `h.keys[0].frozen?` is true and it is not `k`); CRuby 4.0 keeps `k` itself, unfrozen,
    and copies only a plain String. The same with `h[k] = 1`. And `defined?({a: 1.nope})`
    is `"expression"` where CRuby says nil, as both say for `defined?([1.nope])`.

35. **`Enumerator#next` and every other fiber switch cost 40 to 80 times CRuby's**: a fiber is
    a thread here, and a switch is a handoff between two threads (about 20-40 us against 0.5).
    Any library that takes items with `next` pays it twice per item: CSV#each did (round
    thirty-five, perf/csv35, which reads without the enumerator where nothing can tell), and
    `Enumerator::Lazy`, `Enumerator#peek` and external iteration in general still do. Also a
    measuring trap: `app-shapes.rb` and `time-shapes.rb` time with the main thread's CPU
    (`/proc/thread-self/schedstat`), so the work a fiber does is not counted there, and a change
    that moves it back onto the main thread looks slower (`csv_parse` 1.37 and 0.94 of its time
    in the round-35 gate, while process CPU fell to 0.18-0.37). `invoice-parts.rb` and
    `csv-shapes.rb` use `CLOCK_PROCESS_CPUTIME_ID`, which counts the JIT's background threads
    too. Untried: switching fibers without a thread (a continuation of the method JIT's own,
    or a stackless form for the enumerators the prelude makes).
36. **Left from round thirty-five**, each smaller than one gap of its own: `...` passed on is
    still a splat of an Array and a block argument (`def m(...) = n(...)` allocates the rest
    per call; the frame is light now); `URI::Generic#query=` and `merge` still run the
    regexp-heavy checks per call (`merge` 1.1 times the round's base at N=1000, 0.87 at
    5000: warm-up); the `const_get` cache of perf/dlg35 holds the value of a constant
    removed after the lookup until the module's next `const_get` (a reference only: the
    version check keeps it from being answered).
    Round thirty-six (see Landed, perf/uri36): at a steady state (N=10000) `uri`'s parts are at
    CRuby's but `to_s` (1.6x) and `decode` (2.2x, now 1.5x); at N=1000 most of the 2.7x is
    warm-up - .NET's tier-1 recompiles run on a background thread that starves under load
    (`DOTNET_TieredCompilation=0`: `uri` about half, start-up 1.6 -> 4.4 s; rejected). Read
    `uri` at N=5000 or not at all. Left in `decode`: `partition` 2.3x, `each_line` with a
    block 2.2x.
37. **Two differences from CRuby found in round thirty-six**, not speed gaps. A private
    method called from outside on an object whose class's `method_missing` calls `super`
    raises "undefined method" (CRuby: "private method 'x' called"; OpenStruct's
    `new_ostruct_member!` shows it): the reason does not reach BasicObject#method_missing
    through `super`. And `Module#dup`/`clone` copies the constants in `super` of
    `initialize_copy`, where CRuby has copied them before it (a `const_get` in an
    `initialize_copy` before `super` sees the copy's constants there, the outer ones here).

## Landed

- **Gate of 2026-11-24 for round thirty-six** (perf/erb36, perf/uri36; the shape files of
  perf/pwalk36 and perf/ostr36): the branch at 0aaeec12 against c17dbfe9, Release net10.0:
  **PASS**. IronRuby.Tests PASSED on both trees, no failure name on either. 18 matrices
  (eval, uri, binding-lvar, caller, erb-escape, refinement, refinement-binding, block-given,
  light-rest, jit-const, jit, string-methods, sdef, raise, full-message, encode, stdre,
  hot-shapes) in four modes print what the baseline prints, but for the trees' paths;
  `eval-matrix` and `uri-matrix` differ from CRuby only where the baseline does (a regexp
  warning, `undump`). Probes of the eval cache against CRuby, cold and 3000 times hot: string,
  frozen-string, range, regexp, rational and lambda literals one object per eval or not as
  CRuby has them, in block parameters' defaults too; `__method__`, `return`/`break`/`next`,
  bindings, new locals, constants by lexical module, a refinement activated in the same class
  between two evals of the same text, a method made private and `Integer#+` redefined in the
  middle, four threads. ruby/spec, all five suites (32,285 examples): no failure the known
  list does not name (one `Socket.tcp ... :open_timeout` example failed under the sweep's load
  and passed alone). The fourteen benchmarks verify. Timing: AWFY, 12 iterations, both orders:
  geomean 0.996 and 1.008, no benchmark more than 5% slower in both. micro-table: `blkattr`
  1.10 and 1.05, in eight alternating rounds of its own 1.00. `app-shapes.rb`: `erb_result`
  0.31-0.33, the rest overlapping or in one order only (`csv_parse` 1.24 in the order that
  started at load 6). Shape files: `erb_eval_top` 0.32, `erb_eval` 0.60, `eval_small` 0.40;
  `chomp!` 0.30, `b` 0.73, `decode` 0.87. Bytes per iteration: geomean 0.998. Start-up (`-e
  1`), 24 alternating runs each: best 1252 -> 1239 ms, median 1301 -> 1292.

- **196bf522 (round thirty-five's review, after the gate) - CSV#read's fast path narrowed, and
  `const_get`'s cache cleared on a module copy.** CodeRabbit found three ways to tell the
  fiber-free `CSV#read` from CRuby's (a subclass's or a singleton `#each`, which CRuby's
  `#read` reaches through `#to_a`; a Regexp subclass as `skip_lines`, whose `match?` sees the
  enumerator fiber's locals; a `Warning.warn` handler under `$VERBOSE`, reached by the `:float`
  converter for "1e400"), and a `const_get` cache that `Module#initialize_copy` replacing the
  table did not clear. All four checked against CRuby 4.0 and fixed; `Util/csv-matrix.rb`
  has the three CSV cases.

- **perf/erb36 (round thirty-six, 2026-11-24, 8b6d119b, merged in 1d7c6151) - the compiled
  code of an eval'd text is kept** (`EvalCodeCache`, gap 31), the idea perf/evalc was rejected
  for, with the screen its rejection asked for: a walk of the whole tree, call blocks and the
  right of a parallel assignment included, refuses any text with a regexp, range,
  flip-flop, `END`, `defined?`, `super`, `yield`, `def`, `class` or `module`. Per runtime, 64
  entries LRU under a lock; perf/evalc's key; not for a module eval, a keyword frame, an
  isolated proc, Coverage, or a compile that warned. Each render of a template had transformed
  (70 us) and light-compiled (130 us) the text afresh, and its blocks never reached the
  compiler. `erb_result` of `app-shapes.rb` 0.31-0.33 of its time (CRuby 0.20 ms against 0.37),
  192 -> 55 KB per render. `Util/eval-matrix.rb` rows 32-35.

- **perf/uri36 (round thirty-six, 2026-11-24, 680aeca0, merged in b7745600) - `String#chomp!`
  with an ASCII separator in place, and `String#b` in C#.** `URI.decode_www_form` chomps "&"
  off every pair: the general path built the chomped copy and compared it with self (714 ns,
  CRuby 149); an ASCII separator other than "\n" on an ASCII receiver is now checked for
  frozen and removed in place (0.30 of its time). `String#b` made a copy and two
  `force_encoding` calls in the prelude (0.73). `decode` 0.87. `Util/uri-matrix.rb` gains
  `chomp`, `chomp!` and `b` over 18 receivers and 17 separators, hot and invalidated;
  `Util/bench/uri36-shapes.rb`.

- **Gate of 2026-11-24 for round thirty-five** (perf/uri35, perf/csv35, perf/fwd35,
  perf/dlg35): the branch at 66f10f69 against 4ed96a37, Release net10.0: **PASS**.
  IronRuby.Tests PASSED on both trees, no failure name on either. The four new matrices
  (`uri`, `csv`, `forwardable`, `delegate`) print CRuby's output with the JIT, with
  `-X:NoJIT -X:NoOSR`, with `IR_LIGHT_FRAMES=0` and with `-X:NoJITCalls`; 34 existing
  matrices (hot-shapes, jit, jit-calls, jit-const, block-given, light-rest, keyword-light,
  super-light, rescue-light, refinement, sdef, respond-to, eval, value-object, strptime,
  erb-escape...) in the same four modes print what the baseline prints, but for the trees'
  paths in warnings. `defined?(o.m)` with `m` only refined, made private, and answered by
  `respond_to_missing?`, run hot: CRuby's answers. Recursion a million deep through `def
  m(n, ...)`, `defined?(self.m)`, `defined?(@x)`, `const_get` and `respond_to?`:
  SystemStackError, as CRuby. ruby/spec, all five suites (32,285 examples): no failure the
  known list does not name. The fourteen benchmarks verify. Timing: AWFY, 12 iterations,
  both orders: geomean 0.997 and 1.006 (new/base), no benchmark more than 5% slower in both.
  micro-table: `new2` (1.09, 1.05) and `s_any1` (1.07, 1.06) slower in both orders; in eight
  alternating rounds of their own 1.01 and 1.03, overlapping. `app-shapes.rb`, four rounds
  each order: `forwardable` 0.82-0.84 of its time, `delegate` 0.66-0.69, `uri` 0.75 and 1.00
  with spreads of 200% (0.82 in eight rounds alone), `csv_parse` 1.37 and 0.94 (gap 35: the
  timer counts the main thread only); `csv-shapes.rb` (process CPU) 0.18-0.28 of the base's
  time. `uri-shapes.rb` at N=5000: `join` 0.86, `merge` 0.87, `decode` 0.93, `split` 0.56 at
  N=1000. Bytes per iteration: geomean 1.003. Start-up (`-e 1`, Release, best of ten): 1.236
  -> 1.253 s.

- **perf/uri35 (round thirty-five, 2026-11-24, b7b8e063, merged in 97230205) -
  `MatchData#[]` with a group name in C#, and `String#each_line` with one separator straight
  to `__ir_lines__`.** The prelude's `MatchData#[](*args)` stood in front of the C# methods to
  take names and Ranges, and its rest entry cost more than the lookup, which went through the
  name table eight or nine times; `URI.parse` makes a dozen of those calls. `MatchDataOps` has
  `[]` for a String and a Symbol (one table lookup, `MatchData.TryGetNamedGroupValue`) and a
  Range overload; the prelude method is gone. `uri` of `app-shapes.rb` 0.78-0.83 of its time in
  the round's comparison, `split` 0.56. `Util/uri-matrix.rb` (429 rows), `Util/bench/uri-shapes.rb`.

- **perf/csv35 (round thirty-five, 2026-11-24, 44992656, merged in 8ad12723) - `CSV#read`
  without a fiber, and Float()'s String error unkept.** `CSV#read` took every row through
  `parser_enumerator.next`, two thread switches a row (gap 35); it now has the parser yield
  into the Array when the input is a StringIO, the converters are CSV's own and `skip_lines`
  is nil, a Regexp or a String, which nothing in the parse can tell from the fiber. A patch to
  the installed csv gem, marked `IronRuby:` and listed in `Util/install-bundled-gems.rb`
  (66f10f69), since a reinstall overwrites it. `Float(s) rescue s`, the `:float` converter,
  walked the CLR stack for each String it refused; the error is `MarkUnkept` now (20 -> 3.5
  us). `csv-shapes.rb`, process CPU: `full` 3.1-3.2 ms -> 0.66 (CRuby 0.29), `plain` 0.74-1.0
  -> 0.25-0.29. `Util/csv-matrix.rb`, `Util/bench/csv-shapes.rb`.

- **perf/fwd35 (round thirty-five, 2026-11-24, a7a80cda, merged in 8209d51d) - `def m(...)`
  and `defined?(obj.m)` in light frames** (Forwardable's delegators are both). `...` lowers to
  a rest and a block with no keyword prologue, but its signature said `**`, so it was refused
  as keywords with a rest (`Parameters.ForwardsArguments`); `defined?(recv.m)` gets a light
  resolve-only site that answers boxed, or `LightScopeRequired` for anything that is not a
  public method, which is then asked again with the scope made on demand
  (`LightFrameRewriter.LightIsDefined`). `forwardable` of `app-shapes.rb` 0.81-0.85 of its
  time; a delegator call 149 -> 121 ns. `Util/forwardable-matrix.rb`, `Util/bench/fwd-shapes.rb`.

- **perf/dlg35 (round thirty-five, 2026-11-24, 27b60c3c, merged in c69eac32) - a delegated
  call: `const_get` cached, `respond_to?` and `const_get` callable from light frames,
  `defined?(@x)` in light frames.** Delegator's `target_respond_to?` names `Object` inside a
  BasicObject subclass, so every forwarded call ran `const_missing` and `::Object.const_get`
  (155 ns, CRuby 30). `RubyUtils.GetConstant` keeps the last constant found per module while
  `ConstantAccessVersion` holds (not for a deprecated constant, after Ractors start, or during
  an autoload). A `[DeclaringScope]` parameter takes from the caller's scope only what it
  shares with its declaring scope, so `Kernel#respond_to?` and `Module#const_get` no longer make
  a light frame's scope; a light block hands its calls the scope made on demand once there is
  one (refine_spec: a `using` inside `Module.new { }`). `delegate` of `app-shapes.rb` 0.66-0.70
  of its time. `Util/delegate-matrix.rb`, `Util/bench/dlg-shapes.rb`.

- **perf/lfirst (2026-11-24, 32e7e2ad and 97c5019b) - a light frame's first call costs less.**
  Light frames made start-up 2% slower than v4.0.6 (`ir -e 1`, ReadyToRun host, instructions),
  because a light method costs more at its first call than one with a scope: 0.62M instructions
  more for a plain body, 1.63M more for a body with `rescue` (200 such methods, one call each).
  A profile put the largest part in `ScopeBuilder.TryUseRegisterLocals`: the scope a light frame
  makes on demand is one node used at every rescue helper, every call made again with the scope
  and the frame's filter, and the register rewrite rebuilt the nine-argument
  `CreateMethodScopeLazy` call and the locals tuple at each use. `LightFrame.MakeScope` is now one
  node, and the rewriter rewrites each `??` and scope-making call once (memoized by reference,
  outside OSR arguments, its only context). Second, `HandlingScopes` was a
  `Dictionary<string, enum>`, whose code the ReadyToRun image cannot hold: the first light frame
  of every process JIT-compiled ten dictionary methods. It is a switch now. The generated code is
  unchanged: a DebugView dump of all 2,165 method lambdas of the 200-method scripts, the light
  matrices and the shape files is byte-identical; the census is unchanged (25,856 of 28,369 light).
  Light frames on minus off, ReadyToRun host, `DOTNET_TC_CallCounting=0`, five runs:
  plain method 0.617M -> 0.489M, rescue method 1.630M -> 1.312M, `-e 1` 13.1M -> 6.7M.
  What is left is two full-tree rewrites (about 42%), compiling the larger tree (40%) and binding
  (19%: light sites probe the shared rule first, and its version test fails for an Integer
  receiver after each top-level `def`, so they rebind; not changed here).
  Gate, the branch at 0968a9ab against 4ed96a37, Release net10.0: **PASS**. IronRuby.Tests
  PASSED on both (1,311). 35 matrices in five modes: 175 same, 0 differ. ruby/spec, all five
  suites (32,680 examples): no failure the known list does not name. Instructions per call
  (`ab.rb -i`): rescue shapes 0.93-1.00, keyword 0.96-1.02, loop 0.98-1.02, regex 0.99-1.02
  (`kase` 1.060 in two rounds, 1.003 in four). Timing not run: load average 25-45 for most of
  the gate. Light frames on minus off on the gate's trees: `-e 1` 12.8M -> 6.3M, 200 plain
  methods 120.8M -> 98.3M, 200 rescue methods 282.7M -> 255.8M.

- **Gate of 2026-11-24 for round thirty-four** (perf/time2, perf/inv2, perf/erb2, perf/ostr2):
  the branch at 77f841b9 against 592c75ab, Release net10.0: **PASS** with 7289271b (below).
  IronRuby.Tests PASSED on both trees, no failure name on either. 37 matrices in seven modes:
  CRuby's output but for the differences the baseline has too; `strptime-matrix` differs from
  CRuby in 20 lines against the baseline's 32 and `value-object-matrix` in 2 against 12.
  `caller-matrix` and `block-loop-matrix` differed on the ReadyToRun host only (the known
  `Kernel#eval` and nested `Range#each` frames). Recursion a million deep: SystemStackError in
  both modes. ruby/spec, all five suites (32,285 examples): no failure the known list does not
  name. The fourteen benchmarks verify; `app-shapes.rb` prints CRuby's checksums, JIT on and off.
  Timing: AWFY, 12 iterations, both orders: geomean 0.967 and 1.015 (new/base), no benchmark
  more than 5% slower in both. micro-table: `ivar_rw`, `abs`, `arr`, `yield`, `each4`, `poly2`,
  `blockeach` and `selfcall` were slower in both orders; in six alternating rounds of their own
  they are 0.96-1.08, all overlapping, and `poly2` (the shape perf/ostr2's rule test could
  touch) 1.01 in twelve. Of the shape files, `iso_in`, `parse`, `format` and `eval_distinct`
  looked slower in one order or both; in alternating rounds of their own, at a steady-state N,
  they are 0.83-1.03, overlapping. `app-shapes.rb` four rounds each order: `ostruct` 0.30-0.33
  of its time, `invoice_build` 0.41-0.46, `invoice_render` 0.40-0.43, the rest overlapping.
  Bytes per iteration: geomean 1.007, none more than 1% but Towers (149 -> 163 KB). Start-up on
  the ReadyToRun host, twelve alternating runs, best ms (base -> new): `-e 1` 425 -> 449 (medians
  521 and 528), `gem --version` 585 -> 600 (medians 685 and 660), and of the gate's own runs
  `gem list` 1436 -> 1430, `require "irb"` 1292 -> 1286, `irb` 1866 -> 1840.

- **7289271b (round thirty-four, after the merge) - an attribute reader may read a hidden
  variable.** `RubyAttributeReaderInfo` asserted a name starting with `@`; perf/inv2's Data
  readers read the hidden `<ir>name`, so every Debug build (CI's) failed the assertion in
  `Prelude_DataStructErrors1`. The assertion takes the hidden prefix too.

- **perf/time2 (round thirty-four, 2026-11-23, ec0f1c98, merged in 1587bcd3) - ASCII-only
  `Date._strptime` in C#** (`Src/Libraries/Date/DateStrptime.cs`). The Ruby port of 1.9's
  `_strptime_i` cost about 18 us a call against CRuby's 1.1: a String per format token, a case
  over sixty literals, a `sub!` with a MatchData per field. The C# walk takes a subject and a
  format that are both ASCII-only plain Strings and answers false for anything else, which
  `format.rb` then parses as before. Fixed on both paths: `%L`/`%N` with a sign, `%p` before a
  letter, an unknown zone's `offset: nil` (in `Date._parse` too). `d_strptime` 0.09-0.31 of its
  time, `strptime` 0.20-0.30, `time` of `app-shapes.rb` 0.52. `Util/strptime-matrix.rb` gains
  69 rows; `Util/bench/time-shapes.rb` splits `time` into its parts.

- **perf/inv2 (round thirty-four, 2026-11-23, 7a2d25f4, merged in ef88aec2) - `Data.new`,
  `Data#with`, Data readers and `Array#sum`'s plain fold.** `Data.new` built a keyword Hash out
  of positional values and splatted it into `initialize` through three `__send__`s; when the
  class's `initialize` is Data's own and the arguments name every member, `KernelOps.DataNew`
  makes the frozen instance directly (as MRI, calling neither `allocate` nor `freeze`), and
  `DataWith` likewise. Readers are attribute readers of hidden variables, not `define_method`
  blocks. `Array#sum` past a non-number is a plain `+` loop, as rb_ary_sum. `Money.new` 1083 ->
  307 ns, `with` 1168 -> 478, `<=>` 143 -> 55; `invoice_build` 0.41-0.46 of its time,
  `invoice_render` 0.40-0.43, near CRuby's at steady state (149 and 128 us against 128 and 126). `Util/bench/invoice-parts.rb`.

- **perf/erb2 (round thirty-four, 2026-11-23, 66e5dec6, merged in acc9f9fc) - the prism trees
  of evaluated strings are kept** (`EvalAstCache`, gap 31). Per runtime, the last 64 texts up
  to 64 KB, keyed by text, file, line, outer locals, encoding and whether the eval is outside a
  method; only the AST, so every eval still transforms and compiles afresh (literal identity,
  `/o`, `END {}` and call sites stay per eval, which is what sank the compiled-code cache of
  perf/evalc). A parse that reported anything is not kept. Parse and bridge of a template 72 ->
  5 us; `erb_eval` 0.77-0.78 of its time, `erb_full` 0.71-0.73, `erb_result` 0.68.
  `Util/eval-matrix.rb` rows 26-31, `Util/bench/erb-shapes.rb`.

- **perf/ostr2 (round thirty-four, 2026-11-23, 39fd30bf, merged in 77f841b9) - a shared rule's
  test takes in a new class version that resolves to its method** (gap 32). Every
  `define_singleton_method` re-versions the object's singleton class, so each self call inside
  `ostruct.rb` bound again (about 22 binds per `OpenStruct.new`). `RuleVersionSet.IsValidFor`
  now resolves the call for a version it does not hold (`RubyCallAction.TryShareVersion`, as
  `BindShared` would) and adds it when it lands on the rule's method; a call whose visibility
  comes from the caller's scope only for a public method; one rejected version is remembered.
  `BindShared` no longer hands out the interpreted rule after the compiled one exists, and a
  receiver with a singleton class of its own tries the shared rule before a dispatcher at any
  site. Binds per OpenStruct 17 -> 4; `ostruct` of `app-shapes.rb` 0.30-0.33 of its time,
  `ostruct3_new` and `ostruct_app` 0.21-0.32. `Util/sdef-matrix.rb` section 11.

- **Gate of 2026-11-23 for round thirty-three** (perf/setx, perf/erbr, perf/ostr, perf/pwalk):
  the branch at 4e349c7f against 02aa8db1, Release net10.0: **PASS** with 5f9f11ef (below).
  IronRuby.Tests PASSED on both trees, no failure name on either. 36 matrices in seven modes
  (JIT on, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls`, `IR_LIGHT_FRAMES=0`, `IR_PARKED_SCOPES=0`,
  ReadyToRun, and the baseline): CRuby's output but for the differences the baseline has too,
  and the new `set-matrix` matches CRuby where the baseline differs in 360 lines.
  `block-loop-matrix` differed on the baseline and the ReadyToRun host only (the known nested
  `Range#each` frame). Recursion a million deep: SystemStackError in both modes. ruby/spec, all
  five suites (32,285 examples): no failure the known list does not name. The fourteen
  benchmarks verify; `app-shapes.rb` prints CRuby's checksum for all 26 shapes, JIT on and off.
  Timing: AWFY, 12 iterations, both orders: geomean 0.964 and 0.984 (new/base), Richards 0.86-0.89,
  no benchmark more than 5% slower in both. micro-table: `symcase`, `selfcall` and `str==` were
  slower in both orders; in eight alternating rounds of their own they overlap (`selfcall` again
  in ten processes per tree: median 28.9 against 28.1 ns, minimum 27.7 against 26.2).
  `app-shapes.rb` five rounds each order: `set` 0.48-0.50 of its time, `ostruct` 0.56, `prism_walk`
  0.87-0.90, `erb_result` 0.78-0.81 (overlapping), the rest overlapping. `set-shapes.rb` and
  `eval_1p1`, slower in one order, overlap in eight alternating rounds of their own.
  **Bytes per iteration: Richards 14.0 -> 30.3 MB**, the one finding: perf/pwalk gave light sites
  the attribute writer's dispatcher, which boxed a typed Boolean per store. Fixed in 5f9f11ef
  (geomean of bytes 0.993, Richards 12.8 MB; Richards, DeltaBlue, Towers and Havlak timed again
  in both orders: 0.963 and 0.937). Start-up on the ReadyToRun host, ten alternating runs, best
  ms (base -> new): `-e 1` 372 -> 380, `gem --version` 485 -> 491, `gem list` 1094 -> 1061,
  `require "irb"` 926 -> 929, `irb` 1259 -> 1270.

- **5f9f11ef (round thirty-three, after the gate) - an attribute writer's dispatcher boxes with
  the shared boxes.** `RubyObjectAttributeWriterDispatcherWithScope` stored `(object)arg0`, a new
  box per call when the method JIT had typed the site's argument as a Boolean or Integer; it now
  converts with `MethodDispatcher.Box` (true and false the shared objects, small Integers from the
  cache), as the method dispatchers do. A case of hot writers from light methods in
  `Util/perf/hot-shapes-matrix.rb`.

- **perf/setx (round thirty-three, 2026-11-23, a8c5b84c and e8de5370, merged in 8903a912) -
  a core Set's table filled directly** (`Src/StdLib/ironruby/set.rb`). `Set.new`, `merge`, `&`,
  `^`, `-`, `subset?` and their kin went through `add` and `include?` per element, which a
  subclass may redefine and CRuby's set.c does not call; they now fill and read the Hash.
  New `Util/set-matrix.rb` (408 lines; the baseline differs from CRuby in 360) and
  `Util/bench/set-shapes.rb`. `set` of `app-shapes.rb` 0.48-0.50 of its time; `Set.new` of a
  Range or Array 0.15-0.27, `subset?` 0.14-0.54, `&` 0.39, `[]` 0.42. Left: `^` and `|` cost dup
  and Hash inserts; the method JIT refuses the `i.succ` and `frozen?` loops. Differences from
  CRuby left as they were: a Set frozen or iterated during a merge, CRuby's interning of String
  keys, one more `hash`/`eql?` call in `Hash#delete`, redefined Hash methods being seen.

- **perf/erbr (round thirty-three, 2026-11-23, aaa9f836 and c9c171d0, merged in e08dc551) -
  eval'd blocks compiled later, and `ERB::Escape.html_escape` in C#** (gap 31). The blocks of
  an eval'd text were compiled after 32 runs, so a template evaluated per request compiled its
  blocks on the background JIT again and again; `RubyContext.EvalCompilationThreshold` (1000)
  is the threshold for eval'd code, and `Util/eval-matrix.rb` row 25 runs one past it. And
  `erb/escape` was a `gsub` in Ruby; `Src/Libraries/Erb/ErbEscape.cs` ports escape.c.
  `erb_result` 0.68-0.81 of its time; process CPU per call 9-11 -> 2.7-3.5 ms.

- **perf/ostr (round thirty-three, 2026-11-23, 22468d63, merged in ce1ac3ab) - calls on
  per-object singleton classes without a rule each** (gap 32). `BindShared` shares the rule of
  a call that lands in a Ruby `method_missing` (`IsMissing`, `ResolveMethodMissingForSite`);
  `DispatchIntrinsics.MaySpecialize` no longer folds `respond_to?` on a singleton class of a
  plain object; a light site's first calls of a `define_method` block method go through a
  LibraryDispatcher. `ostruct` of `app-shapes.rb` 0.56 of its time, `ostruct3` of
  `sdef-shapes.rb` 0.14-0.16, `extend` 0.36-0.58.

- **perf/pwalk (round thirty-three, 2026-11-23, 9e29c85b, merged in 4e349c7f) - precompiled
  dispatchers at light sites** (gap 12). `BindPrecompiled` refused sites in methods without a
  scope, so each receiver class got a rule; it now gives them the dispatchers of Ruby methods
  and attribute accessors, resolving an explicit receiver's public method only, never for a
  splat, a refined name or a singleton class (except a `define_method` block method), and
  `RubyMetaBinder` tries the shared rule first. A site over 60 classes: 52 us -> 127 ns a call;
  `prism_walk` 0.87-0.90 of its time, Richards 0.83-0.89. The writer's boxing it exposed is
  fixed in 5f9f11ef.

- **Gate of 2026-11-23 for round thirty-two** (perf/caller, perf/stdre, perf/invoice,
  perf/sclass): the branch at dbe0a3b3 against b7b71c89, Release net10.0: **PASS**.
  IronRuby.Tests PASSED on both trees, no failure name on either. 35 matrices in seven modes
  (JIT on, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls`, `IR_LIGHT_FRAMES=0`, `IR_PARKED_SCOPES=0`,
  ReadyToRun, and the baseline): CRuby's output but for the differences the baseline has too,
  and `value-object-matrix` two lines closer to it (the `Data` rows the round fixed). Two that
  looked new were not: `block-loop-matrix`'s nested `Range#each` frame, missing from the
  backtrace and from `caller` of a raise in the inner block, is missing in 2 of 20 runs of each
  tree with the JIT on (and was a ReadyToRun-only difference in round thirty-one's gate); and
  on the ReadyToRun host `caller-matrix` row 8 sometimes loses the `Kernel#eval` frame in the
  first iterations, which the baseline does too with `DOTNET_TieredPGO=0` (3 of 5 runs).
  Recursion a million deep: SystemStackError on both modes. ruby/spec, all five suites
  (32,285 examples): no failure the known list does not name. The fourteen benchmarks verify;
  `app-shapes.rb` prints CRuby's checksum for all 26 shapes, JIT on and off.
  Timing: AWFY, 12 iterations, both orders: geomean 0.995 and 0.997 (new/base), no benchmark
  more than 5% slower in both. micro-table: no line more than 5% slower in both orders.
  `app-shapes.rb` five rounds each order: `uri` 0.38 and 0.40 of its time, `time` 0.63 and
  0.65, `invoice_build` 0.64 and 0.65, `invoice_render` 0.69 and 0.71, the rest overlapping.
  One order of `caller-shapes.rb`, `sdef-shapes.rb` and perf/evalc's eval shapes put `dsm` at 1.99,
  `eval_file_5` 1.45, `eval_file_50` 1.40 and `caller0_5` 1.13; each in processes of its own,
  eight of each tree taken alternately, medians (minima) new/base: 0.82 (1.09), 1.00 (0.99),
  0.91 (0.89), 0.97 (1.12) - the state an earlier shape left behind, not a cost. Bytes per
  iteration 1.012 (Towers 156 -> 175 kB, DeltaBlue 14.4 -> 14.8 MB; the rest within 0.5%).
  Start-up on the ReadyToRun host, ten alternating runs, best ms (base -> new): `-e 1` 403 ->
  394, `gem --version` 508 -> 503, `gem list` 1088 -> 1093, `require "irb"` 993 -> 982, `irb`
  1306 -> 1312.

- **perf/caller (round thirty-two, 2026-11-23, 902a62ac, merged in 28a71259) - an eval's
  default file name from the frame records, and `caller(n, length)` makes `length` frames**
  (gap 31). `eval(str)` without a file names its code `(eval at f:l)`, which took a CLR stack
  walk of the whole Ruby backtrace (30-90 us); `RubyUtils.DefaultEvalFileName` now reads the
  location from the interpreted frames and the compiled frames' records
  (`RubyExceptionData.GetRecordedFrames`, at most 8 frames) and walks the stack only when they
  name no file. `Kernel#caller(n, length)` builds `length` frames, and `require_relative` and
  `__dir__` ask `caller(0, 1)`. New `Util/caller-matrix.rb` (probes of caller, caller_locations
  and the eval file name from methods, blocks, evals, class bodies, define_method, threads) and
  `Util/bench/caller-shapes.rb`. In the gate (one order): `eval_5` 0.60, `eval_50` 0.46,
  `caller1_50` 0.62, `loc1_50` 0.78 of its time; perf/evalc's eval shapes: `eval_bind` 0.58,
  `instance_eval` 0.51, `eval_distinct` 0.51, `erb_src_eval` 0.70. Left: `caller(0)` and
  `caller_locations` still pay the CLR walk, which is most of what they cost (.NET has no walk
  of part of the stack); the records lack the library methods' frames, so they cannot replace
  it for a backtrace.

- **perf/stdre (round thirty-two, 2026-11-23, 128bf39f and 2d8741b0, merged in 0a9b4be6) -
  MatchData's named groups from a table per CLR regex, and `Date._strptime` without
  `method_missing`.** `MatchData#[]` with a name, `named_captures` and `names` built the map of
  Ruby names to CLR group numbers anew for every match (`RegexpTransformer.GetRubyGroupName`
  over every group); `MatchData.NamedGroupTable` is made once per CLR regex
  (a `ConditionalWeakTable`) and shared by its matches. `URI::RFC3986_Parser#split` reads
  eleven named groups per URI. And `date/format.rb`'s `_strptime` (a port of 1.9's) tokenized
  the format with a regexp per directive, matched each literal with a new Regexp, and set the
  results through `Format::Bag#method_missing`; it now tokenizes with a loop, matches a literal
  with `start_with?` (a literal in an incompatible encoding answers nil, as CRuby's does:
  4d15a1c2), and the bag has accessors. New `Util/stdre-matrix.rb` and
  `Util/strptime-matrix.rb` (133 rows). `uri` of `app-shapes.rb` 0.38-0.40 of its time, `time`
  0.63-0.65. Left in `time`: the `case` over directives and the adaptive compilation of
  `_strptime_i`'s large body.

- **perf/invoice (round thirty-two, 2026-11-23, 453a8844, merged in 13d6eef4) - a `Data` value
  built without the bookkeeping when every member is given.** `Data#initialize` checked
  missing and unknown keywords and built its error lists on every call; when the keywords are
  exactly the members (as many, each a member) it stores them at once and freezes. `Data#with`
  reads the values without `to_h`, and `Data.new` with positional values builds the keywords
  with a loop. 14 new rows in `Util/value-object-matrix.rb`. `invoice_build` 0.64-0.65,
  `invoice_render` 0.69-0.71. Left: `Data.new` with positional values is about 4 us, CRuby's
  0.3.

- **perf/sclass (round thirty-two, 2026-11-23, 98e19d37, 2523d7df and 35fac274, merged in
  dbe0a3b3) - one compiled factory for a `def` run for many modules** (gap 32). A `def` run
  for one module after another (`def o.x` per object, `Class.new { def a; end }` in a loop)
  transformed and compiled its body for each module, most of the 24 us of a `def o.x` and its
  first call. From the third module on (`IR_DEF_FACTORY`, 0 turns it off) the body is compiled
  once more as a factory taking the declaring scope and module, compiled to IL (the light
  compiler's closure rewrite of the nested body recursed without end when a factory-made body
  ran hot), and each further module gets a delegate from it. Kept out: a body with `super`
  (one shared site would collect a rule per class), with a Regexp or Range literal (which
  object, or which `/o` value, a module's method answers would depend on how many modules came
  first: `AstGenerator.HasLiteralState`), with refinements or coverage. A factory-made body is
  not a light frame, so it costs a scope per call. New `Util/sdef-matrix.rb` section 10 and
  `Util/bench/sdef-shapes.rb`. `def_o_x` 0.24-0.27 of its time, `Class.new { def a = 1
  }.new.a` 0.35; hot calls of a factory-made body unchanged. Left: the IL compile of the
  factory (once per body and label) and the scope per call of the bodies it makes.

- **Gate of 2026-11-23 for round thirty-one** (perf/sdef, perf/rminor, perf/dmeth's matrix; the
  eval cache and the epoch move taken back): the branch at aa7474c1 against 58c73a6a, Release
  net10.0: **PASS**. IronRuby.Tests PASSED on both trees (`Exception_CauseRaisedInClause1`
  passed too), no failure name on either. 30 matrices in seven modes (JIT on, `-X:NoJIT
  -X:NoOSR`, `-X:NoJITCalls`, `IR_LIGHT_FRAMES=0`, `IR_PARKED_SCOPES=0`, ReadyToRun, and the
  baseline): CRuby's output but for the differences the baseline has too (`eval-matrix` row 8,
  the regexp warning IronRuby never gives in eval; `int-matrix` 16 lines; `raise-matrix` and
  `refinement-matrix`'s anonymous class addresses; `block-loop-matrix` on the ReadyToRun
  host), and `jit-matrix`'s `is_a?` rows, which the baseline fails. Recursion a million deep:
  SystemStackError on every mode. ruby/spec, all five suites (32,285 examples): no failure the
  known list does not name. The fourteen benchmarks verify; `app-shapes.rb` prints CRuby's
  checksum for all 26 shapes, JIT on and off.
  Timing: AWFY, 12 iterations, both orders: geomean 1.005 and 0.996 (new/base). Towers was
  slower in both (1.07, 1.06) and Storage in one (1.08); in separate processes, eight of each
  taken alternately, Towers' median 97.9 against 95.9 ms and its minimum 91.9 against 93.4,
  Storage 83.5 against 84.0: the same. micro-table: `selfcall` and `veach1` slower in both
  orders; `ab.rb`, six rounds each order, every line overlapping (0.97-1.09). `app-shapes.rb`
  five rounds each order: `ostruct` 0.57 and 0.51, the rest overlapping but `forwardable`
  (0.87 in one order, 1.00 in the other) and `json_generate` (0.91, 1.02). Bytes per
  iteration 1.002. Start-up on the ReadyToRun host, ten alternating runs, best and median ms
  (base -> new): `-e 1` 421/443 -> 405/435, `gem --version` 532/565 -> 514/558, `gem list`
  1126/1185 -> 1132/1193, `require "irb"` 1018/1057 -> 1041/1097, `irb` 1463/1557 ->
  1546/1584 (the best 6% apart, the medians 2%; nothing in the round runs at start-up but
  `respond_to?` sites binding).

- **perf/sdef (round thirty-one, 2026-11-22, 2bcbf532, merged in 4398562f) - the rule of a
  `respond_to?` call that cannot be folded is shared across classes** (gap 32).
  `RubyCallAction.BindShared` refused every call resolving to `Kernel#respond_to?`, because
  `TryBuildRespondTo` folds `respond_to?(:name)` to a constant for the receiver's class. But
  it folds only a call with one plain argument and no block, and every other `respond_to?`
  site got a rule built and compiled for each class it saw: `respond_to?(name, true)` in
  `OpenStruct#is_method_protected!`, which sees a new singleton class with every OpenStruct
  (0.1-0.2 ms a rule, `IR_BIND_TRACE=1`). `DispatchIntrinsics.MaySpecialize` now takes the
  call signature and keeps only a foldable site out of sharing.
  `OpenStruct.new` of three keys 663 -> 59 us (CRuby 8.7). In the round's gate (`ab.rb`, both
  orders): `ostruct` of `app-shapes.rb` 0.57 and 0.51 of its time; `sdef-shapes.rb`
  `ostruct3_new` 0.33, `ostruct_app` 0.39, `ostruct3` 0.75; `def o.x`, `singleton_class`,
  `define_singleton_method`, `Class.new` unchanged. `Util/sdef-matrix.rb` (new, 43 rows: hot
  sites across singleton classes and run-time definitions) prints CRuby's output in every
  mode.

- **perf/rminor (round thirty-one, 2026-11-22, merged in a7b4e435) - `is_a?` with a non-module
  raises CRuby's TypeError, and the review's minor findings on the tools.**
  `is_a?`/`kind_of?`/`instance_of?` with nil raised a CLR `ArgumentNullException` and with
  another non-module the binder's conversion error; CRuby 4.0 raises `TypeError: class or
  module required` for both (the "Regressions" entry). A CLR type group is still taken
  (495a9aa2). New test `Kernel_IsAModuleRequired1`. And: the census names a method's own
  refusal, not that of a callee screened on the way (`JitScreen._rejectedDetail`); `census.rb`
  matches trace records by expanded path, line and method; `alloc.rb` keeps each benchmark's
  two iteration counts apart; jit-raise, jit-scan, jit-construct, osr-array and parked-scope
  matrices run the rows they meant to; `LoopTests.BlockLoopNested1` redefines
  `Integer#times` only after its loop has progressed; `prism_walk` of `app-shapes.rb` parses a
  whole module. Its third change, the epoch move for a shadowing constant, was taken back
  (see Rejected).

- **Gate of 2026-11-22 for perf/lrescue**: the branch at df493d8c against master at dbf4a4f4,
  Release net10.0: **PASS**, on the second run. The first stopped at IronRuby.Tests: `JitCensus1`
  pinned the old refusal of a method with a rescue clause, which is now a light frame (its JIT
  verdict unchanged); the expectation was updated (df493d8c). Then IronRuby.Tests 1,297 executed
  and PASSED on both trees. 35 matrices in five modes (the switches-off mode with
  `IR_LIGHT_RESCUE=0` too): 175 same, 0 differ. ruby/spec, all five suites (32,680 examples): no
  failure the known list does not name.
  Timing (`ab.rb`, six rounds, the order turned each round, ns a call, baseline -> candidate,
  `Util/bench/rescue-shapes.rb`): rescue not entered 117 -> 77 (0.66), `ensure` 136 -> 96
  (0.71), `else` 136 -> 104 (0.76), the modifier 112 -> 88 (0.79), rescue entered once in a
  hundred calls 239 -> 160 (0.67), `loop` left by a break 520 -> 440 (0.85); `=> e` 0.71 with
  overlapping rounds (by instructions 0.81); `loop` ended by StopIteration 1.01 and the three
  controls 0.92-1.00, overlapping. Instructions a call 0.78-0.92 on the same shapes. The regex,
  step, loop and super shapes overlap. micro-table: `s_any1` slower in both orders (1.19 and
  1.19); alone, eight rounds, 173 -> 176 ns, overlapping. AWFY, 12 iterations, both orders:
  geomean 0.991 and 1.009 (new/base), every benchmark verified; Queens 1.08 in one order and 0.96
  in the other, Storage 1.03 and 1.02. Start-up 1,203 and 1,228 ms.

- **perf/lrescue (/perf-step, 2026-11-22) - a method with `rescue`, `else` or `ensure` in a light
  frame; the scope is made only when a handler is entered** (`IR_LIGHT_RESCUE=0` turns it off) -
  census only when merged, gated on 2026-11-22 (first entry). Gap 22. Two commits.
  The refusal assumed the handler needed the scope; it needs far less. `$!` is saved, set and
  restored through the runtime (`GetCurrentException`, `SetCurrentException`, the `=> e` target),
  and the CLR first-pass filters (`RescuePassMayTest`, `RescueSiteCannotMatch`,
  `RescueDefaultCannotMatch`, `RescuePassCommit`, `SetCauseLeavingEnsure`) read only the
  runtime too: they get the declaring scope. What reads the frame's own scope runs only once an
  exception is being matched or caught (`CanRescue*` capture the trace from its interpreted
  frame, `CompareException` reports to TracePoint and asks about refinements, `EnterRescue` sets
  `InRescue` for `EvalRetry`, the ensure guard reads `FlowControlScope`): they get the scope made
  on demand. `RescuePassUnwinder` gets the scope if one was made and is now null-safe;
  `LeaveRescue` runs only if one was. `retry` is a jump to a label before the try. The rewriter
  maps the helpers by name (`LightFrameRewriter.VisitHandlingCall`) and now looks into the
  filter, ensure-guard and finally-flow nodes, which also lets in methods with a literal block
  whose body has a `begin`/`rescue` (the block keeps its own scope and its helpers).
  - Let in: `rescue` with no class, one, several, `*CONST`, a class in a local; `=> local` and
    `=> @ivar`; `else`; `ensure`; `retry`; the rescue modifier; the same inside a `begin`. Still
    refused: `rescue =>` another target (none in the corpus), a clause holding a construct refused
    elsewhere (`$!` 9, `defined?` 4...; reported as "rescue, else or ensure clause: ..."), and a
    `begin`/`rescue` as a statement of a block's own body (blocks keep their scope).
  - Light frames 87.4% -> 91.1% (24,791 -> 25,846), light blocks and JIT unchanged (Src/StdLib +
    AWFY, 28,337 methods; 1,055 moved up, none down): 862 refused first for the handler, 193
    with a literal block that holds one. `Kernel#loop`, `JSON.dump`, `Pathname#initialize`,
    `Pathname#unlink`, `Etc.passwd`, `Rational.__convert__`, `Complex#to_r`.
  - Measured (`ab.rb -i`, instructions a call, switch off -> on, `Util/bench/rescue-shapes.rb`):
    rescue not entered 1,279 -> 1,023 (0.80), `ensure` 0.86, `=> e` 0.83, `else` 0.84, a
    `begin`/`rescue` in the body 0.83, the modifier 0.81, rescue entered once in a hundred calls
    0.90, `loop` left by a break 0.94, `loop` ended by StopIteration 0.98 (the raise is the
    cost); three controls 0.98-1.01. Not timed.
  - Checked: `Util/rescue-light-matrix.rb` (new, about 75 probes) prints CRuby 4.0's output on
    the baseline and on the candidate as built, with the switch off, with `IR_LIGHT_FRAMES=0`,
    with `-X:NoJIT -X:NoOSR` and with `-X:NoJITCalls`; twenty older matrices the same on both
    builds. A second probe written for the merge (an inner ensure that writes a local before the
    outer handler reads it through `binding`; `local_variable_set` and `eval` assigning in a
    handler and the local read after; a binding taken before the raise; retry; nested rescue and
    `cause`; next, return and throw through ensure; `=> @ivar`; yield in a handler; backtrace and
    caller labels; a redefinition half way; a caller that is itself in a rescue; a million deep;
    four threads; Thread#raise into `loop`) prints CRuby's as built, with the switch off, with
    light frames off, with parked scopes off and with the JIT off; fourteen of its nineteen
    methods run light.
  - Not covered: an ensure-only body's first-pass filter makes the scope inside the filter, so a
    raise through it allocates one (and with a parking slot takes a parked scope it does not
    return). Found on the baseline, not fixed: TracePoint's `:rescue` reports the raise's line
    where CRuby reports the rescue clause's.

- **Gate of 2026-11-22 for perf/lkw**: the branch at its ledger commit against master at
  d7730f1b, Release net10.0: **PASS**. IronRuby.Tests 1,294 executed and PASSED on both trees.
  34 matrices in five modes (the switches-off mode with `IR_LIGHT_KEYWORDS=0` too): 170 same, 0
  differ. ruby/spec, all five suites (32,680 examples): no failure the known list does not name.
  Timing (`ab.rb`, six rounds, the order turned each round, ns a call, baseline -> candidate,
  `Util/bench/keyword-shapes.rb`): a required keyword 176 -> 144 (0.82), an optional one given
  184 -> 152 (0.83) and left out 144 -> 109 (0.76), two keywords 208 -> 176 (0.85), a keyword
  after an optional 264 -> 208 (0.79), a small body 248 -> 216 (0.87), a default that reads a
  parameter 168 -> 112 (0.67), `**rest` 272 -> 200 (0.74); a keyword after a positional 0.86
  with overlapping rounds, alone 2,167 -> 2,022 instructions (0.93). The JIT-specialized keyword
  method and the three controls 1.00, overlapping. Instructions a call 0.78-0.92 on the same
  shapes. The regex, step, loop and super shapes overlap. micro-table: `str==` slower in both
  orders again (1.14 and 1.12), and alone, eight rounds, 53.9 -> 53.6 ns: that line of the table
  follows the shapes run before it, as in the last gate. AWFY, 12 iterations, both orders:
  geomean 1.004 and 0.994 (new/base), every benchmark verified; the benchmarks define no method
  with keywords. Start-up 1,195 and 1,209 ms.

- **perf/lkw (/perf-step, 2026-11-22) - a method with keyword parameters in a light frame**
  (`IR_LIGHT_KEYWORDS=0` turns it off) - census only when merged, gated on 2026-11-22 (first entry). Three commits.
  `IsLightFrameCandidate` refused any method with keywords, a `**rest` or `**nil` outright, the
  largest line of the census (1,257 methods). Nothing in their lowering needs the scope: the
  keywords arrive in a hidden optional parameter that holds the call's keyword hash, and a
  prologue at the head of the body checks it and binds each keyword to its local, the defaults
  evaluated there in order (`PrismAstBridge.LowerKeywords`): `KeywordArgumentsTest` and
  `KeywordParameterRead`, runtime calls that take no scope. Only the hidden parameter's own
  default read the scope, for its RubyContext (`GetOmittedKeywordArguments`,
  `MakeOmittedKeywordArguments`); a light frame hands it the declaring scope. `eval("super")`
  finds the keyword signature by the scope's local names, which a light frame now records too.
  - The prologue's error message was `list.inspect[1..-2]`, and a Range literal keeps a method
    with literal blocks out: it is `text[1, text.size - 2]` now. The step first used
    `delete_prefix`/`delete_suffix`, which the prelude defines in Ruby; a runtime without the
    prelude then raised NoMethodError for an unknown keyword, found by a probe that redefines
    `String#delete_prefix` and fixed before the merge.
  - Not taken, each with a census reason of its own: keywords beside a rest or post parameter
    (146: `Kernel#warn`, `IO#read`, `String#each_line`, `Kernel#open`; the general lowering binds
    the positionals out of one splat with helpers `IsScopeFree` does not take), a body that passes
    `**opts` on (52), and bodies kept out by something else (rescue 27, a large multiple
    assignment 24, about 60 scattered).
  - Light frames 83.8% -> 87.4% (23,758 -> 24,777), JIT unchanged (Src/StdLib + AWFY, 28,337
    methods; 1,019 moved up, none down): `Psych.load`, `Prism.parse`, `Pathname#rmtree`,
    `Exception#detailed_message`, `Array#shuffle!`, `GC#start`, `Kernel#BigDecimal`.
  - Measured (`ab.rb -i`, instructions a call, switch off -> on, `Util/bench/keyword-shapes.rb`,
    bodies the method JIT refuses, since it specializes a trivial keyword method and the frame
    then never runs): a required keyword 1,797 -> 1,625 (0.90), an optional one given 0.88 and
    left out 1,556 -> 1,330 (0.86), a default that reads a parameter 0.83, two keywords 0.91, a
    keyword after a positional 0.94, after an optional 0.93, `**rest` 0.91; a JIT-specialized
    keyword method 1.00 and three controls without keywords 0.99-1.00. Not timed.
  - Checked: `Util/keyword-light-matrix.rb` (new) prints CRuby 4.0's output on the baseline and
    on the candidate as built, with the switch off, with `IR_LIGHT_FRAMES=0`, with
    `-X:NoJIT -X:NoOSR` and `-X:NoJITCalls`; nine older matrices the same on both builds. A
    second probe written for the merge (defaults that call `binding`, `local_variables`,
    `block_given?`, `__method__`; a `**rest` mutated; a keyword named `if`; zsuper, `eval("super")`
    and eval reading a keyword; a Hash for an optional before keywords; `**nil`; ten keywords;
    a frozen Hash splatted; the messages for `:"]"` and a String key; a redefinition half way; a
    million deep; four threads) prints CRuby's in the same four modes.
  - Found on the baseline, not fixed: `local_variables` lists keyword parameters in declaration
    order where CRuby lists the required ones first (`def m(a, k: 1, j:)`: `[:a, :j, :k]`); and
    an exception raised with an interpolated message reports the `def` line in `backtrace[0]`.

- **The whole gate for #239 (rounds eleven to twenty-eight, the review fixes, master at
  7fb0cbcb merged in), 2026-11-22, on 9de71602 against 7fb0cbcb: PASS.** Run after the
  merge, because the review fixes had had the tests and the matrices but no sweep and no
  timing. IronRuby.Tests on both trees: the same one failure
  (`Exception_CauseRaisedInClause1`), no other. Twenty-seven matrices under CRuby and
  seven ways of running the branch (JIT on, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls`,
  `IR_LIGHT_FRAMES=0`, `IR_PARKED_SCOPES=0`, the ReadyToRun host): CRuby's output but for
  the known differences that master has too (`int-matrix`'s Integer hashes, addresses in
  `raise-` and `refinement-matrix`, the ReadyToRun host's missing `Range#each` frame in
  `block-loop-matrix`); master at 7fb0cbcb differs from CRuby in `binding-lvar` (46 lines),
  `jit` (26), `osr-array` (10) and `block-loop` (2), which the branch fixed. A million-deep
  recursion through four of the cheaper frames is SystemStackError with the JIT on and
  off; `ivar-stress.rb` three times OK; the fourteen benchmarks verify; `app-shapes.rb`
  prints CRuby's checksums with the JIT on and off; `Util/ci-sweep.sh` of all five suites,
  32,285 examples, nothing the known-failures list does not name.
  Timing, against 7fb0cbcb: AWFY geomean 0.864 and 0.848 in the two orders (NBody 0.46,
  Bounce 0.59, List 0.71-0.73, Havlak 0.86-0.87, Json 0.78-0.85, Richards 0.85-0.92), no
  benchmark more than 5% slower in both orders (Towers 1.04 and 1.01, Queens 1.02 and
  1.01); bytes geomean 0.764 (NBody 1.36 GB -> 152 MB, Havlak 2.59 -> 1.87 GB, DeltaBlue
  24.5 -> 15.7 MB, Queens 41 -> 30 MB, Bounce 11.0 -> 7.3 MB). The micro table: fourteen
  lines faster in both orders (`rangeeach` 0.39, `abs` 0.67-0.78, `blkattr` 0.67-0.70,
  `s_each1`, `s_times1`, `s_map1`/`2`/`4`, `veach1`/`2`, `ivar_rw`, `while10`), none
  slower in both. `app-shapes.rb`, five rounds each way: `erb_result` 0.28 and 0.30,
  `ostruct` 0.89-0.91; `gem_requirement` 1.13 and 1.11, and in eight more rounds each way
  alone 0.88 and 1.03 with the ranges inside each other and 14 methods specialized
  against 12. A loop and a method folding a constant beside a thread that extends objects
  and defines constants, singleton methods and classes: 936-1163 ms against 1163-1381,
  invalidations 0 against 8. Start-up on the ReadyToRun host, ten runs: `-e 1`, `gem list`,
  `irb` within 4% on minima and medians; `gem --version` 1.13 and `require "irb"` 1.00 on
  minima at first, then thirty alternating runs each: medians 0.975 and 0.996, the first
  quartiles 1.02 and 1.00, the minima single samples.

- **Gate of 2026-11-21 for perf/jbreak**: the branch at 9bc85a93 against master at 8251113c,
  Release net10.0: **PASS**. IronRuby.Tests 1,292 executed and PASSED on both trees. 34 matrices
  in five modes: 165 same; `int-matrix.rb` prints sixteen `Integer#hash` values, which are seeded
  per process, and is the same on both trees without those lines in every mode. ruby/spec, all
  five suites (32,680 examples): no failure the known list does not name.
  Timing (`ab.rb`, five rounds, the order turned each round, ns a call, baseline -> candidate,
  `Util/bench/loop-shapes.rb`): a loop of one iteration with a break 98 -> 56 (0.57), of four
  109 -> 46 (0.43), `next` in four 135 -> 56 (0.41); of a hundred 131 -> 80 (0.61) with
  overlapping rounds (spreads of 61% and 100%), so by time it has not shown a difference, and by
  instructions it is 2,445 -> 1,578 (0.645). The loop of 10,000 iterations printed no time on
  either tree; by instructions 131,821 -> 102,253 (0.776). The other loop shapes 0.97-1.00, all
  overlapping; `sum` with a block and the scan loop count 0.6% and 1.3% more instructions.
  The regex, step and super shapes overlap, but for `respond_to?` through super (1.08 in three
  rounds); alone, eight rounds, it is 124 -> 121 ns and 1,360 instructions on both. micro-table:
  `str==` was slower in both orders (1.09 and 1.11); alone, eight rounds, 53.5 -> 54.1 ns,
  overlapping. AWFY, 12 iterations, both orders: geomean 1.000 and 1.002 (new/base), every
  benchmark verified: no AWFY method has such a loop. Start-up 1,193 and 1,205 ms.

- **perf/jbreak (/perf-step, 2026-11-21) - the method JIT compiles a `while`/`until` loop with
  `break`, `break v` or `next`** - census only when merged, gated on 2026-11-21 (first entry).
  Two places refused them. The screen (`JitScreen.Walk`) had no case for a BreakStatement or a
  NextStatement; and Emit took them only in an outlined OSR loop, and `break v` not even there.
  The OSR copy already made each a jump to the loop's labels. Emit now takes them in any loop it
  emits (no block body is emitted in a typed body, so each one met is the loop's), and in a
  method's body `break v` stores v, boxed, in the loop's result (`EmitBreak`), which is nil again
  each time the loop is entered. The screen counts the loops around a node. Still refused: `redo`,
  `next v`, a break of several values or a splat, and `break v` in an outlined loop.
  - Census: JIT maybe 2,911 -> 2,923, never 22,133 -> 22,121, compiles and light frames unchanged
    (Src/StdLib + AWFY, 28,337 methods; 12 moved up, none down): `Rational.__rationalize_internal__`
    and eleven rules of RDoc::Markdown (`_Space`, `_Endline`, `_Sp`...). BreakStatement was the
    first refusal of 45 methods and the only one of 12; most have a call the screen refuses too.
  - **Kept against the rule for never -> maybe, and why.** In a traced run of Markdown parsing and
    `rationalize`, every one of the moved methods that ran was declined after the pre-screen, for
    a call on self and not for the break (`_Spacechar/0`, `get_byte/0`, `ceil/0`): declined 3 ->
    14, specialized 30 on both. The rule is there because a declined method keeps its stub. What
    that costs here was measured: parsing a Markdown document 1,800 times counts 421.5e9
    instructions on the baseline and 421.0e9 with the change (two alternating runs of each, user
    mode), so nothing that shows. What the step is for is not in the corpus: a numeric loop with a
    break, in which the break was the only refusal.
  - Measured (`ab.rb -i`, instructions a call, `Util/bench/loop-shapes.rb`): a loop of one
    iteration with a break 1,292 -> 697 (0.54), of four 1,206 -> 638 (0.53; one without a jump is
    630), of a hundred 2,457 -> 1,566 (0.64), of 10,000 132,887 -> 101,282 (0.76), `next` in four
    1,579 -> 743 (0.47); `take_while`, `drop_while`, `rfind`, `sum` with a block, the scan loop and
    the controls 0.96-1.02. Not timed.
  - Checked: section 10 of `Util/loop-light-matrix.rb` (new; twelve probes, each seen as
    `[jit] specialized` under `IR_JIT_VERBOSE=1`) and the rest of the file print CRuby 4.0's
    output on the baseline and on the candidate, as built, with `IR_LIGHT_FRAMES=0` and with
    `-X:NoOSR`; `jit-matrix.rb`, `osr-array-matrix.rb`, `block-loop-matrix.rb` the same on both.
    A second probe written for the merge (a local retyped before a break and after a `next`, a
    break of a Float, a String, a Bignum and an element as the loop's value, a break in the
    condition, nested loops, a callee redefined half way, a million deep, four threads) prints
    CRuby's as built, with `IR_LIGHT_FRAMES=0` and with `-X:NoJIT -X:NoOSR`; a loop that retypes
    a local around the jump is declined, not miscompiled.
  - Next, by the trace: a call on self with no arguments to a method that is neither a leaf nor
    an attribute reader passes the screen and is declined at the first compile. Refusing it in
    the screen would take the eleven declines above, and the older ones like it, off the stub.
  - `ab.rb` now turns the order of the builds round after each round (asked for in the review of
    the last gate's numbers): a cell's least is then taken over rounds in both orders.

- **Gate of 2026-11-21 for perf/lbreak**: the branch at 86fcbc2e against master at 831508a0,
  Release net10.0: **PASS**. IronRuby.Tests 1,292 executed and PASSED on both trees. 33 matrices
  in five modes (as built; the six light-frame switches off; `IR_LIGHT_FRAMES=0`;
  `IR_PARKED_SCOPES=0`; `-X:NoJIT -X:NoOSR`): 165 same, 0 differ. ruby/spec, all five suites
  (32,680 examples): no failure the known list does not name.
  Timing (`ab.rb`, five rounds, ns a call, baseline -> candidate, `Util/bench/loop-shapes.rb`): a
  loop of one iteration with a break 136 -> 104 (0.76), of four 152 -> 112 (0.74), of a hundred
  240 -> 154 (0.64), `next` in four 160 -> 144 (0.90), `take_while` and `drop_while` 328 -> 272
  (0.83), `rfind` 0.91, the scan loop 0.90, `@m ||= begin ... end` 96 -> 56 (0.58); `sum` with a
  block, a plain `begin`, a loop of four without a jump and the control 0.99-1.11, all
  overlapping. The loop of 10,000 iterations was not timed on the baseline (the run printed no
  number for it); by instructions it is 0.998. Instructions a call agree with the step's own
  (0.72-0.91 on the shapes above), and two shapes count more: the plain `begin` 658 -> 682
  (1.036, spread 5%) and the control 931 -> 958 (1.029), neither slower on the clock.
  `ab.rb` runs the baseline first in every round, so these ratios carry whatever the order is
  worth; the instruction counts do not, and the two tables that are run in both orders follow.
  The regex, step and super shapes 0.91-1.10, all overlapping. micro-table in both orders: no
  line slower in both. AWFY, 12 iterations, both orders: geomean 0.991 and 0.990 (new/base),
  every benchmark verified; start-up 1,188 and 1,209 ms.
  The prelude's `bytesplice` counts 11,568 -> 11,657 instructions a call here (1.008), where the
  last two gates had 1.06.

- **perf/lbreak (/perf-step, 2026-11-21) - `break`, `next` and `redo` in a `while`/`until` loop, a
  `begin ... end` without clauses, and a light frame's OSR site without a scope** (`IR_LIGHT_BREAK=0`,
  `IR_LIGHT_BEGIN=0`, `IR_LIGHT_OSR=0`) - census only when merged, gated on 2026-11-21 (first entry). Three commits.
  - **break/next/redo.** In a loop each is a jump to one of the loop's labels, a break's value
    stored in the loop's result (`TransformJump`); the scope is read only in an eval, which is
    never light, or to leave a class body. `IsScopeFree` walks no block's body and the parser
    refuses one outside a loop or block, so every one it meets is a loop's; a break in a block is
    as before. Nothing in the code says why they were left out.
  - **The OSR site.** Alone, the break step lost: `ab.rb -i`, off -> on, a loop of one iteration
    with a break 1.07, of four 1.07, `next` in four 1.21, where `take_while` gained (0.86). The
    method JIT's screen refuses BreakStatement, so these loops get fast only through OSR; once the
    loop is specialized every call enters the copy at its first back edge, and a light frame made
    its scope on demand for that site: a RubyMethodScope a call (`perf` showed its constructor
    only in the light arm). The copy reads the scope for two things, folded constants and
    refinements (`JitLoopCompiler`). A method's light frame now hands the site its declaring
    scope, in which the constants resolve as in the method's (its child), and the site is marked
    `OsrLoopSite.InLightFrame`, for which the copy is built with no refinements and no test of
    them: a light frame is compiled only where none is active. Light blocks keep the old path.
    By itself, with `-X:NoJIT`: a light loop of four iterations without a jump 1,572 -> 1,188
    instructions a call (0.76), one of 10,000 iterations 1.000.
  - **begin.** A Body without rescue, else or ensure compiles to its statements in place
    (`Body.TransformRead`), so it is as scope-free as they are. One with clauses now says so in
    the census (460 methods).
  - Light frames 82.6% -> 83.8% (23,418 -> 23,758), JIT unchanged (Src/StdLib + AWFY, 28,337
    methods; 209 moved up by the jumps, 131 by begin, none down). By the jumps: `Array#take_while`,
    `#drop_while`, `#rfind`, the prelude's sum helpers, `Pathname#ascend`, Zlib::GzipReader's
    readers, RDoc::Markdown's rules; by begin: the `@x ||= begin ... end` memoizers of RubyGems
    and Bundler. 73 methods with a break are kept out by something else.
  - Measured (`ab.rb -i`, instructions a call, baseline -> the three together,
    `Util/bench/loop-shapes.rb`): a loop of one iteration with a break 1,417 -> 1,292 (0.91), of
    four 1,477 -> 1,205 (0.82), of a hundred 2,708 -> 2,459 (0.91), of 10,000 133,094 -> 132,797
    (1.00), `next` in four 1,685 -> 1,501 (0.89), `take_while` 3,492 -> 2,975 (0.85),
    `drop_while` 0.87, `rfind` 0.94, `sum` with a block 0.98, a loop of four without a jump
    (already light) 655 -> 632 (0.97), `@m ||= begin [1, 2].sum end` 0.82.
  - Checked: `Util/loop-light-matrix.rb` (new; every jump in every kind of loop, nested, as a
    value, after a call that makes the scope, through OSR, with threads, a million deep; an OSR'd
    loop in a light frame whose folded constant is redefined, and one in a method defined before
    a `using` that refines `Integer#+`) prints CRuby 4.0's output on the baseline and on the
    candidate as built, with each of the three switches off, with `IR_LIGHT_FRAMES=0`, with
    `-X:NoJIT -X:NoOSR` and with `-X:NoJITCalls`. A second probe (loops of 30,000 iterations in
    light frames that call `local_variables`, `binding`, `block_given?`, a protected and a
    private method, `eval`, `method_missing`, `caller_locations`, `__method__`, raise; a constant
    redefined; four threads; a million deep) prints CRuby's as built, with `IR_LIGHT_OSR=0` and
    with `IR_LIGHT_FRAMES=0`. `census.rb -c`: 0 differ.
  - Not done, and larger than this step by the counts: the method JIT's screen refuses
    BreakStatement, so a loop with a break runs at 1,200-1,500 instructions where one without
    runs typed at about 630. `census.rb -c LOG` with one file matches no method ("called 0 of
    46"); with a second file it works.

- **Gate of 2026-11-21 for perf/lsuper and the call counter fixes**: the branch at 4f66f1ae against
  master at 951a0ddb, Release net10.0: **PASS**. IronRuby.Tests 1,291 executed and PASSED on both
  trees. 31 matrices in five modes (as built; `IR_LIGHT_REGEX=0 IR_LIGHT_MASGN=0 IR_LIGHT_SUPER=0`;
  `IR_LIGHT_FRAMES=0`; `IR_PARKED_SCOPES=0`; `-X:NoJIT -X:NoOSR`): 155 same, 0 differ. ruby/spec,
  all five suites (32,680 examples): no failure the known list does not name.
  Timing (`ab.rb`, five rounds, ns a call, baseline -> candidate): `initialize` calling `super(a)`
  197 -> 162 (0.82), `super()` into Object 196 -> 130 (0.66), `super` with two implicit arguments
  115 -> 76 (0.66), super passing the block 232 -> 178 (0.77), super into `Array#size` 120 -> 68
  (0.57), super reaching a Ruby `method_missing` 178 -> 126 (0.71); `respond_to?` through super,
  which keeps its scope, 0.96 and the three controls 1.00-1.02, all overlapping. The regex shapes
  and the step-shapes 0.95-1.09, all overlapping. micro-table in both orders: no line slower in
  both. AWFY, 12 iterations, both orders: geomean 0.990 and 0.987 (new/base), every benchmark
  verified; start-up 1,182 and 1,209 ms.
  The prelude's `bytesplice` counts 10,901 -> 11,544 instructions a call (1.059) here too, with a
  baseline that already has the changes the last gate measured it on, and `bs_a` (the same body
  in the script) 1.002: the difference follows the tree the build runs from and not a change.
  Which property of the tree is not found.

- **perf/lsuper (/perf-step, 2026-11-21) - a method that calls `super` in a light frame, unless
  super reads the caller's scope** (`IR_LIGHT_SUPER=0` turns it off) - census only when merged, gated on 2026-11-21 (first entry).
  `IsScopeFree` had no case for a SuperCall, which kept about 1,050 methods out. A super site read
  three things from the scope: the module the method was defined in and the name it was defined
  under (`RubyScope.GetSuperCallTarget`), for the lookup, and the method's self. A light frame's
  body is compiled per declaring module (`RubyMethodBody.GetDelegate`), so the module and the name
  are constants of the compilation and self is the site's own operand. The rewriter turns the
  site into a `SuperCallAction` of its own (`MakeLight`) that carries the two and is bound with a
  light scope; a rule that reads the scope answers `LightScopeRequired`, and the frame makes its
  scope and calls the ordinary site (`MakeLightCall`, the code `LightScopeCall` had). A `super`
  without arguments reads the parameters, which are locals. Not let in: `super` with a literal
  block (its Proc needs the scope as its parent), and `super` inside a block of the method, which
  goes through the block's scope as before.
  - A fault found by the matrix and fixed in the step: super hands the method's block on, and a
    light frame reuses the Proc of a literal block after the call, so a parent that kept the block
    (`def keep(&b) = b`, child `def keep = super`) raised NullReferenceException. A method with a
    super now enters as one that names its block (`LightFrame.NamesBlock`).
  - What loses stays out: `def respond_to?(m, p = false) = m == :zz || super` counts 1,489
    instructions a call as a light frame against 1,360 with its scope, because
    `Kernel#respond_to?` asks for the caller's scope on every call (gap 16). So a method named like
    a library method that takes its caller's scope (`SuperScopeMethods`, from the `[RubyMethod]`s
    with a RubyScope parameter) keeps its scope when it calls super. The name decides only which
    frame is made; the rule still decides what the call needs. `method_missing` is not on the
    list: super into a Ruby one gains 21%.
  - Light frames 79.1% -> 82.6% (22,410 -> 23,419), JIT unchanged (Src/StdLib + AWFY, 28,337
    methods; 1,009 moved up, none down). 387 of them are `initialize`; of AWFY's, DeltaBlue's
    `Plan`, `BinaryConstraint`, `UnaryConstraint`, `EqualityConstraint`, `ScaleConstraint` and
    Richards's `TaskControlBlock`, all `initialize`.
  - Measured (`ab.rb -i`, instructions a call, `IR_LIGHT_SUPER=0` -> on): `initialize` calling
    `super(a)` 2,078 -> 1,723 (0.83), `super()` into Object 1,823 -> 1,384 (0.76), `super` with two
    implicit arguments 1,257 -> 951 (0.76), super passing the method's block 2,281 -> 2,087
    (0.92), super into `Array#size` 1,191 -> 876 (0.74), an Exception subclass's `initialize`
    3,283 -> 2,908 (0.89), super reaching a Ruby `method_missing` 1,801 -> 1,414 (0.79). Controls
    (an `initialize` without super, a plain call, a yield) 1.002-1.007.
  - Checked: `Util/super-light-matrix.rb` (new) prints CRuby 4.0's output on the baseline and on
    the candidate as built, with `IR_LIGHT_SUPER=0`, `IR_LIGHT_FRAMES=0`, `IR_LIGHT_BLOCK_PASS=0`,
    `-X:NoJIT -X:NoOSR` and `-X:NoJITCalls`; block-given, light-rest, masgn, regex-light,
    binding-lvar, hot-shapes, jit and jit-calls print the baseline's. A second probe (a class
    made by `dup` and by `clone`, a module in two classes, `module_function`, a singleton method
    and its clone, `bind_call`, `super_method`, prepend and extend half way, four threads,
    `BasicObject`, `defined?(super)`) prints CRuby's with the switch on and off. `census.rb -c`
    on traces of the matrix, the shapes and DeltaBlue: 0 differ.
  - Two faults of the baseline the matrix is written around, not fixed here: `super(n, 0)` from
    an Array subclass's `initialize` raises NullReferenceException in `Array#initialize`, and a
    TracePoint sees no `:c_call` for a super into a C# method.
  - With it, three findings of the review of the regex and register-local steps: the call counter
    counts the operator of `x += v`, both calls of `a[i] += v` and `o.x += v`, and the defaults of
    optional parameters; `s[/x/] = v` (String#[]=, which stores `$~`) keeps the scope. One method
    moves down by the count, `Ripper.add_spaces` (light frames 23,418).

- **Gate of 2026-11-21 for perf/lightsize, perf/lightre and the block-pass count**: the branch at
  0db76bbc against master at 45a1eeff, Release net10.0: **PASS**. IronRuby.Tests 1,291 executed
  and PASSED on both trees, no failing name on either. 30 matrices in five modes (as built,
  `IR_LIGHT_REGEX=0`, `IR_LIGHT_FRAMES=0`, `IR_PARKED_SCOPES=0`, `-X:NoJIT -X:NoOSR`): 150 same,
  0 differ. ruby/spec, all five suites (32,680 examples): no failure the known list does not name.
  Timing (`ab.rb`, five rounds, ns a call, baseline -> candidate): `s.match?(/b/)` 202 -> 171
  (0.85), `/b/.match?(s)` 224 -> 184 (0.82), a literal as a value 128 -> 96 (0.75); the shapes
  kept out (`gsub`, `split`, `case`, `=~`) 0.91-1.03, all overlapping; the step-shapes 0.97-1.08,
  all overlapping. micro-table in both orders: no line slower in both (the lines 5-14% slower
  in one order are as much faster in the other). AWFY, 12 iterations, both orders: geomean
  0.996 and 1.000, every benchmark verified; start-up 1,183 and 1,199 ms.
  Not explained: the prelude's `bytesplice` counts 10,903 -> 11,529 instructions a call (1.057,
  spread 0.1%) while its time is 1,568 -> 1,536 ns (overlapping) and `bs_a`, the same body
  defined in the script, counts 0.998. It keeps its scope on both trees (the masgn limit), and
  with `IR_LIGHT_REGEX=0` the candidate still counts 11,436 in most processes and 10,914 in
  some, so it is a state of the process and not the regex step.

- **perf/lightre (/perf-step, 2026-11-21) - a regex literal in a light frame, unless it goes to a
  method that stores `$~`** (`s.match?(/x/)`, `/x/.match?(s)`, `/a#{x}/o`, a literal as a value or
  passed to a method of the program; `IR_LIGHT_REGEX=0` turns it off, `IR_LIGHT_REGEX=all` lets the
  rest in too, for measuring) - census only when merged, gated on 2026-11-21 (first entry). `IsScopeFree` had no case for a
  RegularExpression, so it kept 1,066 methods out (census of 2026-11-21). The literal reads no scope:
  its Regexp is made once into a box of the site (`RubyOps.CreateRegex*`), and the parts of one with
  interpolation go through to_s sites as a String's do. What reads the scope is the callee: the C#
  methods that store the caller's `$~` take a RubyScope, so from a light frame their rule answers
  `LightScopeRequired`, the frame makes its scope and calls again - correct, and already what
  `s.gsub(r, "")` with a non-literal `r` did, but gap 16's cost on every call. So a literal passed
  to (or the receiver of) a call named `=~ !~ === match gsub(!) sub(!) scan split index rindex
  byteindex byterindex [] []= slice(!) start_with? partition rpartition grep grep_v`, or a `when`
  comparison, still keeps its method out; the name decides only which frame is worth making light,
  the call's rule still decides what the call needs. `if /x/` (a RegularExpressionCondition, `$_`)
  and named captures (a MatchExpression) are other nodes and stay out.
  The 1,066 by use (classified with prism, first that applies, outside blocks): the method reads
  `$~`/`$1`/`Regexp.last_match` 213; the literal goes to a `$~`-setting call 502 (gsub 264, sub 94,
  split 67, scan 60, match 43, sub! 33, gsub! 28, [] 22), `=~`/`!~` 129, `when` 50; only a value or
  passed to a method that leaves `$~` alone 129 (match? 87, grep 11, a method of the program) and
  37 more with interpolation; 6 not found by line.
  Light frames 78.5% -> 79.1% (22,255 -> 22,410), light blocks +21, JIT unchanged (Src/StdLib +
  AWFY, 28,337 methods; 155 methods moved up, none down). With `IR_LIGHT_REGEX=all` 650 would move.
  Moved: `Pathname#chop_basename`, `URI.encode_www_form_component` and its three siblings,
  `Reline::Unicode.space_character?`, `Net::HTTPHeader#connection_close?`, ERB's compiler, Gem's
  suffix regexps, Psych's `visit_String`, OpenSSL and Puma helpers; none of the prelude's (its 12
  all pass the literal to a `$~`-setting call) and none of AWFY's.
  Measured (`ab.rb -i`, three rounds, `Util/bench/regex-shapes.rb`, instructions a call, off -> on
  -> all): `s.match?(/b/)` 2,126 -> 1,869 (0.88), `/b/.match?(s)` 2,152 -> 1,890 (0.88), a literal as
  a value 1,370 -> 1,100 (0.80), `space_character?` 2,490 -> 2,149 (0.86),
  `URI.encode_www_form_component` 12,246 -> 12,072 (0.99), `Pathname#+` 37,299 -> 35,848 (0.96).
  The shapes kept out lose when let in (all/on): `gsub(/b/, "x")` 1.028, `split(/,/)` 1.039, `case
  when /z/ ... when /b/` 1.067, `=~ /d/` 1.030 - gap 16, measured again, and the reason for the
  restriction. Controls (a light frame without a regex, a method that reads `$~`): 0.991 and 0.997
  on/off in six rounds, both two-state shapes.
  Checked: `Util/regex-light-matrix.rb` (new: `$~`, `$1`, `$&`, `` $` ``, `Regexp.last_match` after
  every kind of call, in the method, in its blocks, through binding and eval, in the caller and in
  another thread (which keep their own), nested callees, a scope made late, case/when, named
  captures, `if /x/`, `match?` leaving `$~` alone, gsub with a block and a hash, scan with a block,
  `String#[]`, `=~` on nil, Symbol, send/public_send/Method#call, method_missing, a private method
  with an explicit receiver, `String#gsub`/`match?` and `Regexp#===` redefined after binding and put
  back, TracePoint `:call`/`:c_call`, one frozen object per literal, `o`, a refinement of `=~` and
  `match?`, recursion a million deep with match?, gsub, split and interpolation: SystemStackError)
  prints CRuby 4.0's output on the baseline and on the candidate as built, with `IR_LIGHT_REGEX=0`,
  with `IR_LIGHT_REGEX=all`, with `IR_LIGHT_FRAMES=0` and with `-X:NoJIT -X:NoOSR`; block-given,
  light-rest, masgn, binding-lvar, parked-scope and hot-shapes print the baseline's in every one of
  those modes (hot-shapes differs from CRuby on the baseline too: an Encoding::CompatibilityError
  where CRuby raises FrozenError). `census.rb -c` on traces of the matrix and of the shapes: 0
  differ, but for the matrix's method defined after `using` (`ref_matchp`), which the census calls light and
  the run gives a scope for the refinement - as it does on the baseline for any method after
  `using`. No IronRuby.Tests, sweep or AWFY run (owed to the gate).

- **perf/lightsize (/perf-step, 2026-11-21) - register locals only in a light frame of at most
  40 calls** (`MethodDefinition.MaxRegisterLocalsCalls`) - gated on 2026-11-21 (first entry). A larger light frame keeps
  its locals in their tuple, made at entry, as one with more than 16 locals already did.
  Asked first: could the scope-making arm of `LightFrameRewriter.LightScopeCall` be one copy
  per frame instead of one per call site (gap 16's third)? **No, not in an expression tree.**
  Each site's arm is `LightDynamic(action, #scope ?? (#scope = LightScopeInLoop(
  CreateMethodScopeLazy(shape, ..., MaterializeLocals(), self, block, ..., #rfc, #callee),
  #in-loop)), args...)`, and `MaterializeLocals` is `#locals ?? {new tuple; one store per
  register; }`. A shared block the sites jump to and come back from cannot be had: a site is
  inside an expression (an argument, a condition), and the DLR refuses a jump into one
  ("Control cannot enter an expression", `LabelInfo.ValidateJump`; System.Linq.Expressions
  has the same rule), nor is a branch with values on the IL stack valid. A nested lambda would
  close over the register locals and hoist them into a closure for the whole body; a helper
  taking them by ref makes them address-exposed, so the JIT keeps them in memory; a helper
  taking them by value still loads each one at every site. What is left per site is the
  site, the test and the second call: without register locals the arms alone still double a
  straight-line body (m48: 11.7 KB of IL against 5.7 KB with its scope; m24 6.1 against 3.1).
  - Measured (`ab.rb -i`, instructions a call): a copy of `bytesplice`'s body written with
    single assignments (81-100 calls; `bs_g`) 13,394 -> 10,993 (0.82; with
    `IR_LIGHT_FRAMES=0` 11,380), IL 51,959 `Tier-0 switched MinOpts` -> 30,036 `FullOpts`;
    allocated 626 -> 769 B a call (the tuple; 1,297 with its scope). Straight-line bodies
    with ten locals and a multiple assignment, the masgn limit lifted (calls counted where the
    register switch flips them): with register locals against without, m8 (~10 calls)
    3,188/3,267, m16 4,693/4,855, m24 6,367/6,687, m32 (31-36) 7,811/8,016, m48 (45-50)
    11,774/11,550, so the limit sits between m32 and m48. The step-shapes (`swap`, `ctl`,
    `ctl2`, `lit_sz`, `itp_sz`, `iswap`, `attr2`, `arr2`) 0.978-1.005 against the baseline,
    all overlapping (their IL is unchanged); `bytesplice` and `bs_a`-`bs_f` (they keep their
    scope by the masgn limit) 0.998-1.013. Tried and dropped: comparing with a static field
    for `RubyOps.LightScopeRequired` instead of a closure constant (m48 within 0.3%).
  - The masgn limit (`MaxMultipleAssignmentCalls`, 24) stays: with this change, the `bs_*`
    copies with the limit lifted count 1.019-1.037 of `IR_LIGHT_MASGN=0` (bytesplice 1.012),
    so a large body with a multiple assignment still loses for a reason that is not the
    register locals; at 40 instead of 24, m32 gains 1%, inside the noise. Census unchanged
    (light frames 22,255: it says which methods are light, not how they keep their locals).
  - Checked: `Util/binding-lvar-matrix.rb` has new rows (a light body of more than 40 calls
    whose scope is made at its first, second and third call, in a loop, after a literal block
    and an eval wrote a local, `local_variables`; the same with rescue and ensure, which keeps
    its scope; a million-deep recursion through it, SystemStackError) that print CRuby 4.0's
    output on the baseline and the candidate in every mode; block-given, light-rest, masgn,
    hash-literal, parked-scope, binding-lvar, block-pass, splat-pass and hot-shapes print on
    the candidate what they print on the baseline, as built, with `IR_LIGHT_FRAMES=0`, with
    `IR_PARKED_SCOPES=0` and with `-X:NoJIT -X:NoOSR` (36 of 36 identical). No IronRuby.Tests,
    sweep or AWFY run (owed to the gate).

- **Gate of 2026-11-21 for perf/strlit, perf/jinterp, perf/masgn and its narrowing**: the
  branch at c2b83fc9 against master at e83e9af7, Release net10.0: **PASS**, the second
  time. The first run failed step 4 on one shape, `String#bytesplice`, 1,485 -> 1,744 ns
  alone: perf/masgn had made it a light frame, whose lambda was 2.9 times the IL and, for
  a copy of its body, compiled without optimization (the entry below and gap 16).
  - IronRuby.Tests: `PASSED`, 1,291 on both trees.
  - 30 matrices (the 21 of the gate before, jit-strlit, jit-interp, masgn, jit-scan,
    jit-construct, jit-raise, jit-const, ivar, int), each as built, with `IR_LIGHT_MASGN=0`,
    with `IR_LIGHT_FRAMES=0` and with `-X:NoJIT -X:NoOSR`: 120 runs print what master
    prints, but for `Integer#hash` in int-matrix, which differs from process to process,
    and once for two lines of block-loop-matrix without the JIT, whose backtraces had no
    `Range#each`: master loses that frame too, in 1 run of 12 (the branch in 0 of 12). A
    library frame missing from a backtrace now and then is master's, and is the same kind
    of failure as `Backtrace_LibraryFrames1` in the gate before. Not explained.
  - ruby/spec, all five suites: 32,680 examples, 45 failing, every one listed, none new.
  - Time (`ab.rb -r 5`, `IR_LIGHT_MASGN=0` -> on; CRuby 4.0.10): `a, b = b, a` 174 -> 131
    (CRuby 46), `@a, @b = @b, @a` 186 -> 142 (50), `a, b = [x, 2]` 192 -> 138 (40),
    `a, *b = x, 1, 2` 206 -> 184 (124), `self.p, self.q = x, 1` 198 -> 142 (68). A method
    returning a String literal 148 -> 135 against master (95), `"a#{x}b"` 244 -> 238 (238);
    `bytesplice` and its six cut-down copies count the instructions they count on master
    (0.989 to 1.005).
  - AWFY on the branch before the narrowing, both orders, no FAIL: geomean 0.996 and 0.994;
    not run again after it (no method of the benchmarks moved with perf/masgn). The micro
    table in both orders after it: no line more than 5% slower in both. Start-up 1,180 ->
    1,201 ms.
  - A precompiled library kept no `frozen_string_literal` for the JIT's parse of a def, so
    perf/strlit made unfrozen literals there; found in review of perf/jinterp, reproduced,
    fixed (the perf/strlit entry).

- **perf/masgn (/perf-step, 2026-11-21) - a multiple assignment in a light frame** (`a, b = b, a`,
  `x, *rest = list`, `@a, (b, c) = v`, `a[i], a[j] = a[j], a[i]`, `self.x, self.y = 1, 2`;
  `IR_LIGHT_MASGN=0` turns it off) - gated 2026-11-21, see the note above perf/masgn. `IsScopeFree` had no case for a
  ParallelAssignmentExpression, so it kept 661 methods out of light frames (476 at it first; 24
  of the prelude's). Nothing it compiles to takes the scope: the right side is an Array made by
  `RubyOps.MakeArrayN` or one value through an ImplicitSplatAction site (`#to_ary`, given no
  scope, as ExplicitSplatAction's `*x` is), the left values' receivers go to hidden variables,
  and each is written by an Array helper and the write a simple assignment to it is (a local,
  an instance variable, a call of `x=` or `[]=` through LightScopeCall). The rewriter now lets
  the ImplicitSplatAction site through as it does the other conversion sites; any such site
  sets MakesCalls, so a frame that recurses through a `#to_ary` written in Ruby keeps its stack
  check. Left values that are globals, class variables, constants or `x&.y` still refuse.
  Light frames 76.9% -> 78.7% (21,793 -> 22,308), light blocks 67.1% -> 67.6% (7,725 ->
  7,783), JIT unchanged (Src/StdLib + AWFY, 28,337 methods; 515 methods moved up, none down;
  50 methods have more light blocks, none fewer) - gated 2026-11-21, see the note above perf/masgn.
  Moved: 18 of the prelude's (`__ir_each_impl__`, `__ir_step_slow__`, `__step_scan_args__`,
  `bytesplice`, `__ir_byteindex__`, `__arithmetic_slice__`, `remainder`, `__ir_log__`,
  `__ir_hypot__`, `marshal_load`, `inspect`, ...), Rational/Complex/BigDecimal/Date/Pathname
  helpers, none of AWFY's (its methods have no multiple assignment in a body that is otherwise
  light). 15 more pass the filter and still need their scope after the rewrite
  (`__io_each_line__`, `DelegateClass`, `Resolv#get_rr`, irb commands): unchanged, a scope as
  before; not looked into.
  Checked: `Util/masgn-matrix.rb` (new: every kind of right side and `#to_ary`, the value of the
  assignment, evaluation order, attribute and element writes private, protected, made private
  in a subclass, refined, through `method_missing` and redefined after binding, what a scope
  made on demand sees, blocks, TracePoint, and recursion a million deep through `#to_ary` and
  on the right side, which raises SystemStackError) prints CRuby 4.0's output on the baseline,
  the candidate, `IR_LIGHT_MASGN=0`, `IR_LIGHT_FRAMES=0` and `-X:NoJIT -X:NoOSR`; the
  light-rest, block-given, block-pass, splat-pass, jit and jit-calls matrices print the
  baseline's; `census.rb -c` on traces of the matrix and of Json: 0 differ. Not measured on
  the clock: the shape is entered no faster unless it is called hot, and none of AWFY's is.
  Gated afterwards (`ab.rb -i`, switch off against on): small bodies gain (`a, b = b, a`
  0.82, `@a, @b = @b, @a` 0.86, `a, b = [x, 2]` and `a, *b = x, 1, 2` 0.86), but
  `String#bytesplice` lost (10,940 -> 11,500 instructions, +17% on the clock), and copies of
  it 7-26%. The mechanism is gap 16's third: the light lambda is 2-3x the IL, and past the
  CLR's limits it is compiled MinOpts. **Narrowed (perf/masgnfix)**: a body with a multiple
  assignment and more than 24 calls (`MethodCall`, `[]`, attribute, `super` and `yield`
  nodes; a literal block's body not counted) keeps its scope (`IsLargeWithMultipleAssignment`;
  `IR_LIGHT_FRAMES=why` says "multiple assignment in a body of more than 24 calls"). The
  limit comes from straight-line bodies with ten locals: 16 calls 0.95, 24-32 0.97, 48 1.05.
  Light frames 21,793 -> 22,265 (472 methods moved up instead of 515; of the prelude's,
  `bytesplice` and `__arithmetic_slice__` keep their scope). `bytesplice` 10,899 (off) /
  11,445 (merged) / 10,901 (narrowed); the copies of it 0.79-0.94 of the merged count, each
  within 0.3% of switch-off; swap, iswap, arr2, rest and attr2 unchanged against merged
  (0.999-1.001). `Util/masgn-matrix.rb` prints CRuby 4.0's output in all four modes.

- **perf/jinterp (/perf-step, 2026-11-21, 2220ed2d) - an interpolated String in a body the method
  JIT compiles** (`"#{@path}:#{@lineno}"`, `"v#{x}"`, `%W[a#{x}]`, a heredoc) - census only, not
  gated. The screen refused every StringConstructor: 1,191 methods refused for one at all, 432 for
  nothing else, 610 at it first. `EmitInterpolation` makes the String the generic body makes
  (`StringConstructor.TransformWithoutScope`, in the source encoding the parser now records on
  the node), each part converted by `JitRuntime.InterpolatedPart`, which runs no Ruby code: a
  String as it is, an Integer, Float, Symbol, nil, true or false through the generic body's own
  conversion site while that class's `to_s` is the library one (resolved when compiling, kept so
  by the entry's method version guard), and anything else deopts before its `to_s` could run.
  So a typed body never calls a `to_s` written in Ruby: visibility, `method_missing`, a `to_s`
  answering a non-String, one with side effects or one that recurses are the generic body's.
  Not where a refinement is active, not in an outlined loop, not `:"a#{x}"` or a command.
  JIT compiles 11.5% -> 11.6% (3,268 -> 3,293), maybe 10.2% -> 10.3% (2,890 -> 2,911), never
  78.3% -> 78.1% (22,179 -> 22,133), light frames unchanged (Src/StdLib + AWFY, 28,337 methods;
  46 methods moved up, none down) - gated 2026-11-21, see the note above perf/masgn.
  `StringConstructor` refused at all 1,191 -> 434, alone 432 -> 61; the parts it now refuses
  are counted as their calls (`MethodCall (screen)` 14,461 -> 14,921).
  Moved: 25 to `compiles` (the prelude's `Thread::Backtrace::Location#to_s`,
  `Gem::Specification#to_s`, `Gem::NameTuple#inspect`, `Racc::Rule#to_s`, ...), 21 to `maybe`
  (mostly a constant or a call elsewhere in the body). None of AWFY's: its methods do not
  interpolate.
  What the screen still refuses, and why:
  - a part that is a call other than a binary operator (`#{self.class}`, `#{name}`,
    `#{x.inspect}`). The first build took them: 273 up (248 to `maybe`, 77 at `call: class/0`),
    and app-shapes declined three more methods after the screen than the baseline (30 -> 33:
    `Gem::BasicSpecification#full_name` at `==` on an unknown receiver,
    `CSV::Parser#validate_field_size` at `<=` on nil, `ERB#set_eoutvar`) and specialized none
    more. The call is the wall (gap 1), not the interpolation; and the object of a call on
    another receiver is mostly one whose `to_s` the conversion would deopt on anyway.
  - an interpolation after a write that is not to a local of the method, the part's own write
    included (`"#{@n += 1}"`): its conversion can deopt, which after an effect declines the
    method. With calls refused and this taken, app-shapes still declined `ERB#set_eoutvar`
    (`compiler.put_cmd = "#{eoutvar}.<<"` twice: the second conversion is a deopt after the
    attribute write). A conversion of a local already converted on every path before it cannot
    deopt; that is a flow fact the compiler does not keep, and is what to try next here.
  - an interpolation in an operand or as an argument of a call on self or a variable, as for a
    String literal (perf/strlit).
  Checked: `Util/jit-interp-matrix.rb` (new, 74 rows: every kind of part, typed parts, deopts
  mid-run, encodings, effects before and after a part, `to_s` of NilClass, Symbol and Integer
  redefined and restored after the bodies were compiled, a private `TrueClass#to_s`, a prepended
  `Float#to_s`, a million-deep self-recursion and a million-deep nested `to_s`, threads,
  `frozen_string_literal: true`; 22 `si_*` methods specialized under `IR_JIT_VERBOSE=1`) and the
  interpolation, jit, jit-strlit, jit-scan, jit-calls, jit-construct, jit-raise, jit-const, ivar
  and hash-literal matrices: CRuby's output on the baseline, the candidate and the candidate with
  `-X:NoJIT -X:NoOSR` (jit-construct's stderr notes differ from CRuby's in order, as on the
  baseline). Traces of Json, Richards, DeltaBlue, CD and Havlak (3 x 10): the same specialized
  and declined counts as the baseline; `census.rb -c` 290 methods, 0 differ. app-shapes: the
  same checksums, the same 106 specialized and 30 declined as the baseline; `-c` 0 differ on
  light frames and the same 15 screen differences the baseline's trace has.
  For the gate: no timing taken; IronRuby.Tests and the spec suites not run.

- **perf/strlit (/perf-step, 2026-11-21, 2f7e4647) - a String literal in a body the method
  JIT compiles** (`def name = "document"`, `@buf = ''`, `h['k']`, `['a', 'b']`) - census
  only, not gated. The screen refused every String literal: 2,208 methods refused for one
  at all, 498 for nothing else, the most frequent refusal after `new`. `EmitStringLiteral`
  makes the String the generic body makes (`StringLiteral.TransformWithoutSite`): a new
  chilled or plain one on each evaluation, or the interned frozen one under
  `frozen_string_literal: true`, which a box of the typed body's own finds again. Neither is
  an effect a deopt could repeat. Under `--debug-frozen-string-literal` the compiler
  declines (only the generic body knows the site).
  JIT compiles 10.5% -> 11.5% (2,979 -> 3,268), maybe 9.8% -> 10.2% (2,771 -> 2,890), never
  79.7% -> 78.3% (22,587 -> 22,179), light frames unchanged (Src/StdLib + AWFY, 28,337
  methods; 408 methods moved up, none down) - gated 2026-11-21, see the note above perf/masgn.
  `StringLiteral (screen)` 1,046 -> 259; refused at all 2,208 -> 860, alone 498 -> 100.
  Moved: 202 of the vendored stdlib, 161 of the gems, 44 of `ironruby/` (the prelude's
  `IO::Buffer#nl_` compiles, `IO#path` and `Process.__spawn_shell__` are `maybe`), and
  Json's `Parser#initialize` (compiles).
  What the screen still refuses, and why: a literal anywhere inside an operand of a binary
  operator (`suffixes - [".rb"]`, `@buf += "\b"`: the operator only deopts; refusing the
  Array literal operand itself instead moved 12 methods down), and a literal argument of a
  call on self or on a variable. The first build took the arguments: 458 up, and Json's
  trace declined 12 methods after the screen instead of 6 - `read_null`, `read_true`,
  `read_false`, `read_number`, `read_fraction`, `read_exponent`, all at their calls of
  `read_char('.')`/`read_required_char('u')` (no typed body; a deopt after an effect, gap
  1). That is the wall again, not the literal: those 100 left alone wait for the call.
  Tried and reverted: the literal makers (`RubyOps.Create*MutableString*`) among the helpers
  a body with held-back writes may call: 0 up, 2 down (rdoc's `set_string`, `compiles` ->
  `maybe` at `size on a Int`).
  Checked: `Util/jit-strlit-matrix.rb` (new, 36 rows: chilled, frozen and mutable literals,
  encodings, an invalid byte, identity across calls, `-"lit"`, the chilled warning, a deopt
  after a literal, a literal in a loop; every `sl_*` method specialized under
  `IR_JIT_VERBOSE=1`) and the jit, jit-scan, jit-calls, jit-construct, jit-raise, jit-const
  and ivar matrices: CRuby's output on the baseline, the candidate and the candidate with
  `-X:NoJIT -X:NoOSR` (jit-construct's stdout; its stderr notes differ in order only).
  Traces of Json, Richards, DeltaBlue, CD and Havlak (3 x 10): the same specialized and
  declined counts as the baseline; `census.rb -c` 290 methods, 0 differ. app-shapes:
  specialized 103 -> 104 (`CSV::InputRecordSeparator.value`, `"\n"`), the same 30 declined.
  For the gate: no timing taken; IronRuby.Tests and the spec suites not run.
  Fixed after (perf/prefrozen): a startup-library def's JIT source had no magic comments, so
  under `frozen_string_literal: true` (34 of the 48 startup files) its typed body made chilled
  Strings. The library now keeps the file's frozen comment before the def; census unchanged.

- **perf/hashlit (/perf-step, 2026-11-19) - a method with a Hash literal, or one that
  passes keywords, runs without a scope** (`{a: x}`, `{"k" => v}`, `{}`, `f(a: x)`;
  `IR_LIGHT_HASH=0` and `IR_LIGHT_KEYWORD_HASH=0` turn each off). `IsScopeFree` had no
  case for a HashConstructor, so one anywhere in a body sent the method to the final
  refusal, while an Array literal was taken element by element. The helpers a literal
  and a call's keyword hash compile to (`RubyOps.MakeHash`, `MakeSymbolHash1..3`,
  `MakeKeywordArgumentsHash`, `MakeKeywordArgumentsHash0..3`) take the scope only for
  the runtime's equality comparer, so the rewriter now passes them the declaring scope,
  as it does for instance variables and constants (`ScopeOperandIndex`), and the filter
  takes a Hash whose keys and values are scope-free. `**h` in a literal already was a
  call of `Hash#merge`; `f(**h)` (KeywordSplatMerge) and the defaults of keyword
  parameters are not taken. Two commits, the keywords second, so either reverts alone.
  light frames 71.7% -> 76.9% (20,318 -> 21,793), light blocks 7,389 -> 7,724, JIT
  compiles, maybe and never unchanged (Src/StdLib + AWFY, 28,335 methods; 1,475 methods
  moved up, none down: 807 for the literal, 148 of those a `h = {}` default, and 668 for
  the keywords). 42 of them are the prelude's, among them
  `group_by`, `uniq`, `tally`, `uniq!`, six `to_h`, `transform_keys`, `slice`, `fetch`,
  `to_set`, `transform_values!`, `__check_frozen__`; none is AWFY's, whose methods
  write no Hash literal, so the AWFY table is not expected to move.
  131 of the 789 the literal kept out did not move with the first commit: another
  construct keeps them (parallel assignment, a regex, `super`, a global variable).
  Checked, by the subagent and again by me on its build: `Util/hash-literal-matrix.rb`
  (new, 111 probes) prints CRuby 4.0's output on the build before, the new one, with
  each switch off and both, with `-X:NoAdaptiveCompilation` and with `IR_LIGHT_FRAMES=0`;
  the interpolation, splat-pass, block-pass, light-rest and block-given matrices print
  CRuby's on both builds; `census.rb -c` against traced runs of Richards and Json says
  0 differ; the merged build's census is the worktree's row for row.
  Instructions per call (`ab.rb -i`, the subagent's run, not repeated): `{a: x}` 1,663
  -> 1,360, `{a: x, b: x}` 1,761 -> 1,449, `{}` filled with two keys 2,220 -> 1,964,
  `{"k" => x}` 2,010 -> 1,797, `f(a: x)` to a keyword 1,161 -> 893, two keywords to
  `**o` 2,692 -> 2,466, a control without a Hash 910 -> 872 (within its spread).
  Found on the way: gap 34.

  Gated on 2026-11-20 together with perf/why, the branch at 9f8bc1a7 against master at
  084b09cf, both Release net10.0, 8 cores, load 1.1 to 1.7 (`/perf-gate`): **PASS**.
  - IronRuby.Tests: the same one failure on both trees (`Exception_CauseRaisedInClause1`,
    master's, Release only).
  - Seventeen matrices (hash-literal, interpolation, block-given, light-rest, block-pass,
    splat-pass, hot-shapes, hash-order, hash-struct, callable-parameters, raise,
    full-message, refinement, refinement-binding, jit, jit-calls, set-backtrace), each as
    built, with `IR_LIGHT_HASH=0 IR_LIGHT_KEYWORD_HASH=0`, with `IR_LIGHT_FRAMES=0` and
    with `-X:NoJIT -X:NoOSR`: the branch prints what master prints in all 68 runs (the
    one line of callable-parameters that differs is a `source_location` with the tree's
    own path in it), and CRuby's output wherever master does.
  - The four questions. A million deep through a literal's value, its String key, a key's
    `#hash`, a keyword call to a required keyword, to `**o`, from a block and inside a
    literal: SystemStackError each time and the process alive, as on master. Visibility,
    a callee that reads the caller's scope, a callee and a key's `#hash` changing half
    way, and a refinement in use where the caller is defined are sections 7 to 11 of
    `Util/hash-literal-matrix.rb` (31 probes, 148 lines in all): CRuby's output on both
    trees and in every mode above and with `-X:NoAdaptiveCompilation`; `IR_LIGHT_FRAMES=why`
    names 113 of the file's methods as light frames on the branch and 21 on master, the
    new callers among them. The first version of these probes tested less than it
    said: a `using` in the middle of the file kept every later method out of light
    frames, and the methods that recursed through a keyword call had keyword parameters
    of their own. The refinement section is last for that reason.
  - ruby/spec, all five suites (`Util/ci-sweep.sh`, then `sweep-check.rb`): 32,680
    examples, 45 failing, every one listed, none new, no file without a summary.
  - Time on the shapes it is about (`ab.rb -r 5`, on-CPU ns per call, the branch with
    both switches off -> on; CRuby 4.0.9 in the same rounds): `{a: x}` 162 -> 118 (CRuby
    92), `{a: x, b: x}` 213 -> 140 (108), `{}` filled with two keys 214 -> 172 (133),
    `{"k" => x}` 244 -> 175 (97), `f(a: x)` to a keyword 104 -> 72 (60), two keywords to
    `**o` 288 -> 242 (155), `to_h` with a block 528 -> 480 (123). `tally`, `group_by`,
    `uniq` with a block and the two controls overlap: the prelude's methods that moved
    spend their time elsewhere. Instructions per call agree (`ab.rb -i`: 0.82, 0.74,
    0.92, 0.78, 0.79, 0.86 on the first six; master counts what the switches-off build
    counts).
  - AWFY, `awfy.rb 12` in both column orders, no FAIL: geomean branch/master 1.002 and
    1.014; nothing is more than 5% slower in both orders (Richards and NBody are 5.5%
    and 5.9% slower in the order where the branch ran first and 1.8% and 0.2% faster in
    the other). As expected: none of AWFY's methods moved.
  - `micro-table.rb` three times, once with the columns the other way round: `empty` was
    6 to 8% slower on the branch in all three, `while10` and `selfcall` in two. Each in
    a process of its own (`ab.rb ... -- NAME`): the same instructions per call on both
    trees to two parts in a thousand (342.4 and 341.8, 791.2 and 790.9, 596.2 and 595.8)
    and the same time over eight alternating rounds (27.5 and 26.7 ns, 68.0 and 68.0,
    46.3 and 46.4). So it is the state the table's earlier shapes leave the process in,
    as in round eight, and not a cost of the change, which decides only what is
    compiled.
  - Start-up, `-e 1`, best of ten: 1,148 ms on master, 1,177 on the branch (+2.5%).
  Not measured: TruffleRuby (not installed here), net11.0, Debug.

  Gated again the same day, after master's parked scopes (perf/park) were merged into the
  branch: the branch at 2a516b4d against master at 1522386b, both Release net10.0, load
  1.0 to 1.6 during the timing: **PASS**. The two changes meet in `MethodDefinition.cs`: a
  method that is a light frame only because of this change may now also park its scope.
  - IronRuby.Tests: `PASSED` on both trees, 1,290 tests each, with
    `MutableString_CopyOnWrite3` left out on both. Its reader starves against the writer
    on a Release build on this machine (past 100 s in 8 of 8 runs alone, four on each
    tree, and on Release builds of two days before): master's, and none of this code.
  - Twenty-one matrices, the seventeen above and parked-scope, block-scope-reuse,
    block-loop and binding-lvar, each as built, with `IR_LIGHT_HASH=0
    IR_LIGHT_KEYWORD_HASH=0`, with `IR_LIGHT_FRAMES=0`, with `IR_PARKED_SCOPES=0` and with
    `-X:NoJIT -X:NoOSR`: the branch prints what master prints in all 105 runs.
  - A literal in a method whose scope is parked is section 11 of
    `Util/hash-literal-matrix.rb` (15 probes): CRuby's output on both trees and in the
    five modes. `IR_PARKED_SCOPES=verbose` on them: 11 methods park as light frames and 1
    with a scope on the branch, 4 and 8 on master, so the probes run the path they are
    about. The first version of them did not: `(0..2).each` in a body with literal blocks
    keeps the method out of light frames, so they use `3.times`. The million-deep probes
    and the 31 probes of visibility, scope readers and callees changing half way print
    what master prints in the same modes.
  - Census on the merged trees, 28,337 methods: light frames 20,318 -> 21,793 (+1,475),
    JIT compiles 2,979, maybe 2,771 and never 22,587 on both.
  - ruby/spec, all five suites: 32,680 examples, 45 failing, every one listed, none new,
    no file without a summary.
  - Time on the shapes (`ab.rb -r 5`, switches off -> on; CRuby 4.0.10): `{a: x}` 160 ->
    120 (CRuby 92), `{a: x, b: x}` 214 -> 142 (108), `{}` filled 218 -> 175 (134),
    `{"k" => x}` 240 -> 174 (96), `f(a: x)` 104 -> 70 (60), two keywords to `**o` 278 ->
    244 (154), `tally` 368 -> 336 (250), `uniq` with a block 768 -> 656 (320); `to_h`,
    `group_by` and the two controls overlap. Instructions per call: 0.82, 0.75, 0.93,
    0.80, 0.77, 0.89 on the first six, 0.89 on `tally`.
  - AWFY, `awfy.rb 12` in both column orders, no FAIL: geomean branch/master 1.005 and
    0.995. The largest single difference is Bounce, 3.6% slower in one order and 0.6% in
    the other.
  - `micro-table.rb` three times, once reversed: no line is more than 5% slower in all
    three; `nil?` is in two (14% and 9%). In a process of its own it counts 502.1 and
    501.8 instructions per call and takes 41.3 and 40.0 ns over eight alternating rounds.
  - Start-up, best of ten: 1,167 ms on master, 1,204 on the branch (+3.2%).

  Gated a third time, after the stack check came back for a literal whose keys may run
  Ruby code (the eleventh fault below): the branch at 0347de88 against master at 1522386b,
  Release net10.0, load 1.0 to 1.3 during the timing: **PASS**.
  - IronRuby.Tests: `PASSED` on both trees, 1,290 each, `MutableString_CopyOnWrite3` left
    out as before (its lock has since been fixed on master). A first run, made while two
    other builds ran on the machine, failed `Backtrace_LibraryFrames1` on the branch only:
    the backtrace of `[].each.next` had no `Enumerator#next` line. It passes alone 25 times
    of 25 on both trees, and that line was right 3,000 times of 3,000 in three processes
    per tree, also with `IR_LIGHT_HASH=0` and with `IR_LIGHT_FRAMES=0`; the second whole
    run passed. Not reproduced, so not explained.
  - The 21 matrices in the five modes: 105 runs print what master prints.
    `Util/hash-literal-matrix.rb` now has section 12, a million deep through a key's
    `#hash`, `#eql?` and a splat's `#to_hash`: SystemStackError as on CRuby.
  - ruby/spec, all five suites: 32,680 examples, 45 failing, every one listed, none new.
  - Time on the shapes, switches off -> on: `{a: x}` 162 -> 120, `{a: x, b: x}` 212 -> 140,
    `{}` filled 212 -> 172, `{"k" => x}` 244 -> 178, `f(a: x)` 104 -> 68, two keywords to
    `**o` 284 -> 248. `group_by` counts 1.7% more instructions than on master in a process
    of its own (5,016 -> 5,102, three runs), with the two Hash switches off as well and
    not with `IR_LIGHT_FRAMES=0`; its time overlaps. Which step costs it is not known.
  - AWFY in both orders, no FAIL: geomean branch/master 1.001 and 0.989. `micro-table.rb`
    three times, once reversed: no line slower in all three. Start-up 1,171 -> 1,193 ms.

- **perf/why (/perf-step, 2026-11-19) - a find-out step: the census names the construct
  that keeps a method out of light frames.** `IR_LIGHT_FRAMES=why` and the census said
  only which statement of a body was not scope-free (`IfExpression at line 12`), so its
  three largest lines after the parameters were statement names, and nobody could say
  which decision of `IsScopeFree` kept 5,704 methods out. `IsScopeFree` now takes a box
  for the reason, null on the normal path, and a refusal records what it refused
  (`IfExpression at line 12: GlobalVariable`); the reason's text is built only when asked
  for. `census.rb` has a second table, "not a light frame: the construct". Nothing was
  meant to move and nothing did: of the 28,335 rows of the census 0 differ outside
  `light_why`, and the 5,704 that had a line number are the old text plus the construct
  (my own comparison of the two TSVs; the merged build's census is the worktree's byte
  for byte). The interpolation, block-pass and splat-pass matrices print CRuby's output
  on the merged build. Gated on 2026-11-20 with perf/hashlit (above). Not checked: the
  texts that only appear with a switch off. What it found is under gap 9.
- **perf/bindlv (round twenty-eight, 2026-11-19, 3820abf2, merged in 508f543d) - `Binding#local_variable_get`,
  `_set`, `_defined?` and `local_variables` without `eval`** (gap 30). The prelude's four
  methods were each one or two `eval`s of source text; the slot access is now
  `BindingOps`' private `__ir_local_defined__`, `__ir_local_get__`, `__ir_local_set__`
  (on `RubyScope.TryResolveBindingLocal` / `SetBindingLocal`, the walk the eval's compiled
  assignment made, stopping at an isolated Proc's block) and a C# `local_variables`; the
  name check stays in Ruby. On the way it fixed what the eval versions got wrong against
  CRuby: a capitalized name is a NameError, `self`/`nil`/keyword-named locals can be set,
  `local_variables` lists newest first and one eval's names in source order,
  `TOPLEVEL_BINDING`'s new variables stay out of the main script's binding, an isolated
  Proc's binding does not see the outer local, and the thread-local the old `_set` left
  behind is gone. `Binding#dup` of a binding whose own scope is still empty makes a
  sibling instead of nesting one level deeper (every `ERB#result_with_hash` dups
  `TOPLEVEL_BINDING`).
  us per call, CRuby / before / after: `local_variable_set` 0.1 / 469 / 5.5,
  `local_variable_get` - / 397 / 4-5.5, `local_variables` - / 246 / 2.3,
  `erb.result_with_hash` 25.6 / 5,795 / 1,954. `app-shapes.rb` in five alternating rounds,
  both orders: `erb_result` 0.26 and 0.33 of its time; `erb_compile`, `ostruct`,
  `gem_spec`, `invoice_build`, `optparse` overlapping in both orders (0.81-1.17, no
  direction). Start-up on the ReadyToRun host: `-e 1` median 499 -> 498 ms over fifty
  alternating runs, `gem --version`, `require "irb"`, `irb` within 0.5%.
  Gated (on its branch, 3820abf2 on 550d838f): IronRuby.Tests with only
  `Exception_CauseRaisedInClause1`; the sweep of all five suites, nothing unlisted;
  `Util/binding-lvar-matrix.rb` (new, 162 rows; the unchanged build differed from CRuby
  in 22) and eighteen other matrices in five modes, CRuby's output but for the two known
  differences (`raise-matrix`'s anonymous class address, `block-loop-matrix` on the
  ReadyToRun host); the benchmarks verify; irb answers.
  What is left of `erb_result` (13 times CRuby) is the `eval` of the template's source,
  gap 31. Not done: the name check's two regular expressions are most of the 5 us left;
  a Proc made by `b.eval("proc { binding }")` sees variables `b` defines afterwards, where
  CRuby's does not (old build too, left out of the matrix).

- **The gate's correctness half for rounds twenty-two, twenty-four and twenty-five**
  (2026-11-19, on d1a738cf, the three census-only merges together; run because the third
  one changes the entry of every typed body that reads a constant). IronRuby.Tests in
  Release: the one failure the tree had before (`Exception_CauseRaisedInClause1`), and no
  other. `Util/ci-sweep.sh` of all five suites, 32,285 examples: no failing example that
  `Util/spec-known-failures.txt` does not list, none listed that now passes, no file
  without a summary. Twenty matrices (hot-shapes, block-loop, osr-array,
  block-scope-reuse, block-given, light-rest, block-pass, splat-pass, parked-scope, int,
  raise, full-message, hash-struct, jit, jit-const, jit-scan, jit-construct, jit-calls,
  jit-raise, ivar), each under CRuby, the Release build, `-X:NoJIT -X:NoOSR`,
  `IR_LIGHT_FRAMES=0` and the ReadyToRun host: CRuby's output everywhere but in three,
  none of them this branch's - `int-matrix` (16 lines: `Integer#hash` values) and
  `raise-matrix` (2 lines: an anonymous class's address) in every mode alike, and
  `block-loop-matrix` on the ReadyToRun host only (4 lines, see Regressions).
  `ivar-stress.rb` three times OK; the fourteen benchmarks verify (no FAIL).
  The timing half followed the same evening (see "Current numbers", c99c2ef6): nothing
  slower on the fourteen benchmarks, the micro table or start-up once each suspect line
  had its own process; no gain in time that the host can show; bytes 0.975. What the
  entries list "for the gate" beyond that (no `:c_call` from the `Array.new` helpers,
  per-count bodies with constants) is still open.

- **perf/constrev (/perf-step, round twenty-five, 2026-11-19, 093c7e19) - a typed body
  looks its constants up again when the constant version moves** (gap 29) - not gated when merged; gated and measured since (see the first entry).
  The census does not see this gap (0 methods changed, as it must be); it was judged by a
  probe and a matrix. A body that folded a constant was guarded by the runtime's one
  constant version; when that had moved the stub threw the body away, profiled again, and
  after eight times settled on the generic body. Now, when the method version is
  unchanged, `JitStub.RevalidateConstants` resolves every constant the bodies folded
  again by the code the compiler resolved it with (`JitFoldedConstant.Resolve`: the old
  `ResolveConstant`, returning its bail reason) and, if each is the same object found in
  the same module, stores the version it read before the lookups as the snapshot; the
  entry reads the snapshot from the specialization (`JitRuntime.ConstantsCurrent`, two
  volatile reads and a cast where there was one read against an immediate). A caller that
  binds a body takes its folded constants over. Anything else - a constant that changed,
  an autoload in flight, a deprecated constant - goes the old way.
  The probe (three methods, 300 calls in each of 20 rounds, and per round an `extend` or
  a `const_set` of something else): `qual` (`G::P::RUBY`) and `top` (`TOP`) specialized 9
  times each before, once after, invalidations 8 -> 0; with a `def` or a singleton method
  per round, or nothing, once before and after. The rubygems driver of round twenty-four:
  `Gem::Specification#platform` specialized 9 times -> once, invalidations 13 -> 5, 31
  methods specialized and 12 declined on both builds, CRuby's output. AWFY's traces are
  the same, each with `invalidations=0`.
  `Util/jit-const-matrix.rb` is new (52 rows, CRuby's output on the unchanged build with
  the JIT on and off): events that must not change the answer (an unrelated constant, an
  `extend`, an `include`, a `def`, a `require`), the constant removed and set again, set
  to an equal object, removed for good, shadowed by a nearer lexical scope and through
  the ancestors (`include`, a superclass, `prepend`), qualified reads with a module on
  the path replaced, `private_constant`, `deprecate_constant`, an autoload registered in
  a nearer scope, `const_missing`, `when C`, a lexical `Array` under `Array.new`, a bound
  callee that reads the constant, and two threads (one setting 2,000 constants or
  alternating the constant itself, one calling: no wrong answer). It and the jit,
  jit-construct, jit-scan, jit-calls, jit-raise and ivar matrices print the same on
  CRuby, the unchanged build and the new one with the JIT on, with `-X:NoJIT -X:NoOSR`
  and with `-X:NoJITCalls`; the new one six times running.
  The order that makes it safe without a lock of its own: every change to a constant moves
  the version under the class hierarchy lock, which each lookup takes; the version is
  read before the first lookup and stored after the last, so a change in between has
  moved the version past what is stored and the next entry looks again; a version read
  after a lookup is never stored; two threads may both look and both store. A successful
  look also moves the constant version of the bodies' own loop guards (`JitLoopGuard`),
  which the experiment's author added beyond the brief: without it a body with a `while`
  would hand itself back at its next safe point after any reassignment anywhere.
  For the gate: the entry of every constant-reading typed body is a few instructions
  longer and has not been timed; `TrySpecializePerCount` with constants has no probe of
  its own; and CRuby warns once for a deprecated constant read three times in one place
  where IronRuby warns three times, with or without the JIT (the matrix prints only that
  a warning came).

- **perf/orasgn (/perf-step, round twenty-four, 2026-11-19, 92bb385c) - `@x ||= v` and
  `@x &&= v` in a body the method JIT compiles** - census only when merged; gated and measured since (see the first entry). The screen
  refused every `||=` and `&&=`; the memoizing getter is the only thing it refuses in 443
  methods (an upper bound: it had not looked at the right-hand side). `EmitOrAssignment`
  builds `@x || (@x = v)` from `EmitAndOr` and `EmitAssignment`, on an instance variable
  and on a local of the method: the variable read once, `v` evaluated only when the store
  is made, the store a write "made on some paths" for the held-back writes
  (`TrySinkIVarWrite(conditional)`), never one of the opening run. Method bodies only; an
  element, class variable, attribute, global or constant target stays refused.
  JIT compiles 10.4% -> 10.5% (2,949 -> 2,979), maybe 9.7% -> 9.8% (2,756 -> 2,771), never
  79.9% -> 79.7% (22,630 -> 22,585), light frames unchanged (Src/StdLib + AWFY, 28,335
  methods; 45 methods moved up, none down) - census only; gated and measured together with the rounds after it (see the entry above).
  The 30 that compile whatever the receiver is are getters of rubygems
  (`Gem::Specification#authors`, `#dependencies`, `#executables`, `#extensions`,
  `#licenses`, `#requirements`, `Gem::Dependency#type`, `Gem::Command.build_args`), Thor,
  RDoc, Minitest, Racc, RBS, and `Process#maxgroups` of the prelude. A driver of twenty
  memoizing methods of rubygems, rake, csv, rdoc and test-unit, each called once on a
  fresh object and thousands of times on one (CRuby's checksum on both builds): 16 -> 31
  methods specialized, 12 -> 12 declined after the screen, the same twelve. AWFY's traces
  are unchanged (no benchmark has a `||=`). jit-scan (120 -> 176 rows: unset, nil, false,
  0, "", a frozen receiver, a counted right-hand side, `instance_variables` order, a
  deopt after the store, locals and parameters), ivar, jit, jit-construct, jit-calls and
  jit-raise matrices: CRuby's and the unchanged build's output with the JIT on, with
  `-X:NoJIT -X:NoOSR` and with `-X:NoJITCalls`.
  The find-out build is why the screen is narrower than the idiom: with every `||=` let
  through, 86 methods left `never` (37 to `compiles`) and the driver declined ten more -
  eight at `call X/0`, the memo's work being a call on self (`@blocks ||= compute`), which
  is round twenty-three's and `kraise`'s wall. So the screen takes the right-hand side
  only if it is no call but a binary operator, and not a memo of nil or false (`@skip ||=
  false` is an effect before whatever follows: `RDoc::Markup::Verbatim#ruby?` declined at
  "a deopt after an effect"). That costs 36 `maybe` and 7 `compiles`; the 398 other
  methods of the 443 wait for the call.
  For the gate: no timing has been taken; and see gap 29, which the driver ran into
  (`Gem::Specification#platform`, `@new_platform ||= Gem::Platform::RUBY`, specialized
  nine times in one run and then settled on the generic body).

- **The census names everything the screen refuses in a method, not only the first thing**
  (round twenty-four, 2026-11-19; a tool, no change to what runs). After round
  twenty-three, which took the second line of `JIT never` and moved nothing to `compiles`:
  the screen stops at its first refusal, so "674 methods at `is_a?`" said nothing of what
  stands behind it. For the census the screen now goes on after each refusal
  (`JitScreen.CollectAll`; the verdicts and the first refusal are unchanged: `-d` against
  the census before, 0 methods changed) and `census.rb` prints three more tables. Of the
  18,456 methods the screen refuses, 10,676 have one thing in the way, 3,312 two, 1,770
  three, 2,698 four or more. One thing only, over Src/StdLib + AWFY: `new` 557, `each`
  498, a String literal 492, `@x ||= v` 443, a `rescue`/`ensure` body 442, an interpolated
  String 389, `map` 293, `enter` 233, `is_a?` 144 (of 926 methods it is refused in at
  all), a Hash literal 128, `join` 123. In the prelude: `raise` 26 alone and in 120
  methods at all - written `::Kernel.raise(...)`, which `IsRaiseCall` does not take
  (it takes a bare `raise`) -, `__check__` on self 13, `is_a?` 8 alone of 109, and
  `respond_to?` never alone (76). In the benchmarks: `new` 16, `each` 8, a String literal
  8, `times` 6, `append` 5.
  What the walk does not enter, it does not count: a call with a block is one refusal,
  whatever its block holds, so "alone" is an upper bound for `each` and `map`.
  An assignment's refusal now says which (`||= InstanceVariable`), in the census and in
  `IR_JIT_VERBOSE`'s "screen rejected" lines.

- **perf/arrnew (/perf-step, round twenty-two, 2026-11-19, c5b5b96f) - `Array.new` anywhere
  in a body the method JIT compiles** - census only when merged; gated and measured since (see the first entry). The screen refused every
  `new` that is not a method's last statement (`TailConstruction`), the census's most
  frequent refusal: 1,025 methods of 28,335, 65 of the benchmarks' 391. `Array.new`,
  `Array.new(n)` and `Array.new(n, v)` (no block; `Array` or `::Array`, resolving to the
  Array class itself; `new` the built-in one, public and unrefined; `Array#initialize` the
  built-in one) run no Ruby code, so `EmitArrayConstruction` makes them an operation that
  may stand anywhere and run again after a deopt (`JitRuntime.NewArray*`; a size out of
  [0, Int32.MaxValue / 8], an Int64 beyond it or an object that is not a boxed Int32 deopts
  and the generic body raises or converts). Three more things came with it, because a
  method let through the screen and then declined pays for its stub on every call:
  `SizeOf` is among the helpers a body with held-back writes may call; a nil or false the
  profile saw for a receiver is not believed inside the branch that receiver's own truth
  selects (`if @storage ... @storage.size`, `?:`, `unless ... else`, `&&`: `_truthGuards`,
  a hint only); and a body whose opening run of instance variable writes
  (`EmitInitialIVars`, an effect that was never held back) is followed by a deopt is
  compiled once more with the run held back, where it used to be declined - on the
  unchanged build too (`@a = 0; @b = 0; @k = @s.size`).
  JIT compiles 10.4% -> 10.4% (2,947 -> 2,949), maybe 9.7% -> 9.7% (2,747 -> 2,756), never
  79.9% -> 79.9% (22,641 -> 22,630), light frames unchanged (Src/StdLib + AWFY, 28,335
  methods; 13 methods moved up, none down) - census only; gated and measured together with the rounds after it (see the entry above).
  Seven of the eleven that left `never` are the benchmarks': `Vector#initialize` and
  `#remove_all`, `Dictionary#initialize` and `#remove_all`, `HashIndexTable#initialize`,
  richards' `Packet#initialize` and `Scheduler#initialize`. Traces of eleven benchmarks
  (three iterations each): 91 methods specialized against 84 (`Vector#initialize` in CD,
  DeltaBlue, Json and Havlak; `HashIndexTable#initialize`; `Packet#initialize`; Havlak's
  two `Dictionary` methods and `Vector#remove_all`; List's `Element#length`, declined
  before, through the guarded nil), declines after the screen 27 -> 26 in total and in no
  benchmark more than before; `census.rb -c`: light frame and screen 327 agree, 0 differ.
  jit-construct (21 -> 69 rows), jit, jit-scan (103 -> 120 rows), jit-calls, jit-raise and
  ivar matrices: CRuby's and the unchanged build's output with the JIT on, with
  `-X:NoJIT -X:NoOSR` and with `-X:NoJITCalls`.
  The hypothesis's count was wrong by a factor of four: about fifty methods have an
  `Array.new` as the `new` that refused them, and eleven moved, because behind it stands a
  second refusal - a call on self of a method that is neither a leaf nor bound
  (`Queens#queens` at `place_queen`, `Permute#benchmark`, `Sieve#benchmark`,
  `Towers#benchmark`, `Dictionary#resize`), a block (`Vector#at_put`, `#remove`,
  `#append`), a block parameter. The `new` line is user classes: 993 methods are left.
  And the first build was sent back once: AWFY's `Vector.new` has a nil `@storage`, so
  `remove_all` passed the screen and was declined at "size on a nil" - right about the
  profile, wrong about the branch.
  For the gate, which this step has not had: the helpers do not report a `:c_call` to a
  TracePoint, as `StringLength` and the other intrinsics do not (`Construct*` falls back
  while tracing, the generic `Array.new` reports `new` and `initialize`); the retry round
  is one more compile of a body that would otherwise be declined, its cost not measured;
  no timing has been taken, and `Vector#initialize` is the one of these that is hot.
- **The census answers for a call on self as `-X:JITCalls` does** (a169b274, with round
  twenty-two): `census.rb -c` on the unchanged build said "JIT screen 315 agree, 12
  differ" - twelve benchmark methods `never` that the run let through the screen, two of
  them specialized (`HashIndexTable#add`, `List#tail`). The run's screen takes a call on
  self whose callee may be inlined or, with bound calls, compiled (`IsCallableMethod`);
  the census knew only the first. 101 methods of the corpus went from `never` to `maybe`
  (100) or `compiles` (1) by the correction alone, so the census of 2baa0099 (never 80.3%)
  and the ones after it (79.9%) are not the same measure. The traces need the real path
  of the benchmarks (`/home/user/awfy/...`, not the link under `ironruby-perf`), or `-c`
  finds none of the methods and reports nothing to differ.

- **perf/interp (/perf-step, 2026-11-19) - a method that interpolates runs without a
  scope** (`"a#{x}"`, `raise ArgumentError, "negative size (#{n})"`;
  `IR_LIGHT_INTERPOLATION=0` turns it off). The filter (`IsScopeFree`) has taken an
  interpolation since perf/lf, but since the refinements work of 2026-09-19 each part is
  converted by `TryConvertToSWithRefinements(scope, value)` before the `ConvertToSAction`
  site, and the rewriter refused both: the raw scope operand, and a dynamic site that is
  not a call. So every such method was a candidate that "needs its scope after the
  rewrite", the largest line of the census's light-frame table. In a method's light frame
  the helper would answer null (a method defined where a refinement is active is never a
  light frame, and a method scope without that record answers no refinements), so the
  rewriter puts null in its place, as it does for `&x`, and lets the conversion site
  through. A block's frame keeps the helper: a block can be written where a refinement is
  active.

      light frames 66.3% -> 71.7% (18,790 -> 20,318), light blocks 7,389 and the JIT's
      verdicts unchanged; "body needs its scope after the rewrite" 1,695 -> 167
      (Src/StdLib + AWFY, 28,335 methods; 1,528 methods moved up, none down)

  Gated and measured on 2026-11-19, 434e3dcc against master at 0e78b9fd, both Release
  net10.0 (below).

  103 of them are the prelude's, mostly helpers that raise with an interpolated message
  (`__count_arg__`, `__to_count__`, `__dig_step__`, `__affix__`, `__array_to_int__`,
  `__slice_args__`, `__zip_source__`, `Range#__size__`, `Random#rand`, `Kernel#rand`,
  `MatchData#[]`, `Enumerator#initialize`...), and four are AWFY's, all in `run.rb`'s
  reporting. The census of the merged build is the worktree's, byte for byte.
  Util/interpolation-matrix.rb (new, 95 lines: `to_s` refined in twelve places, answering a
  non-String, raising, private, missing, redefined; parts that read the frame, yield,
  splat; encodings; TracePoint; threads; a million-deep recursion) prints the same under
  CRuby 4.0, the build before, and this one with the switch on and off; with
  `IR_LIGHT_FRAMES=0`, `-X:NoJIT -X:NoOSR` and `-X:JITCalls` by the experiment's own runs,
  which also had the splat-pass, block-pass, block-given, light-rest, refinement,
  refinement-binding, raise and error-message matrices agree. `census.rb -c` on a run of
  Json: 58 methods called, 0 differ.
  What the census cannot see here: a `using` that has run before a `def`. On a traced run
  of the matrix it calls four such methods light that the run keeps a scope for
  ("refinements"), which is the rule the change rests on. Five files of the standard
  library have a `using`, with 7 of the 1,528 methods in them, so the count is at most
  that much too high. (A fifth disagreement there is `-c` taking a `def` nested on its
  parent's line for the parent.)
  The gate (`/perf-gate`): IronRuby.Tests in Release fails `Exception_CauseRaisedInClause1`
  on both trees and nothing else, and the new `Method_LightFrameInterpolation1` passes;
  thirteen matrices (interpolation, block-given, light-rest, block-pass, splat-pass,
  hot-shapes, raise, full-message, refinement, refinement-binding, jit, jit-calls,
  set-backtrace), each as built, with `IR_LIGHT_INTERPOLATION=0`, with `IR_LIGHT_FRAMES=0`
  and with `-X:NoJIT -X:NoOSR`, print on this tree what they print on master in all 52
  runs, and CRuby's output in 40 of them (hot-shapes, raise and refinement differ from
  CRuby by 6, 2 and 4 lines on both trees); a million-deep recursion through an
  interpolating method, through its block, through an interpolated part and through
  `to_s` raises SystemStackError on CRuby and on both trees; AWFY has no FAIL with the JIT
  on and off; ruby/spec, all five suites on this tree (32,680 examples), has no failure
  that `Util/spec-known-failures.txt` does not list.
  Instructions per call (`ab.rb -i`, three rounds; the switch off on this build against
  the switch on): a method that interpolates an Integer 2190 -> 1879 (0.86), two parts
  2838 -> 2298 (0.81), an instance variable 2224 -> 1993 (0.90), an object with `to_s`
  2348 -> 2136 (0.91), a method that only raises with an interpolated message and does
  not raise 1530 -> 1197 (0.78), one that interpolates on one branch 3073 -> 2538 (0.83).
  Timed (`ab.rb -r 5`, ns): 208 -> 167 (0.81), 307 -> 215 (0.70), 205 -> 174 (0.85),
  224 -> 196 (0.87), 144 -> 92 (0.64), 308 -> 216 (0.70); CRuby has 151, 189, 165, 182,
  50 and 201 on them. Three shapes that do not interpolate (a one-line method, a `+` of
  two Strings, an `each` with a block) have rounds that overlap on the two trees once each
  has a process of its own (650.8 and 650.7; 1756, with rounds 2.8% apart, and 1800,
  which is also this build's count with the switch off; 2984 and 2989), and overlap when
  timed. In one process with the other shapes the first two read 1.6% and 5% up: the
  state the earlier shapes leave, as the gate's step 4 says.
  Nothing slower: AWFY, geomean of the thirteen 1.002 with master first and 1.010 with
  this tree first, no line over 5% in both (Richards 1.09 and 1.03, then six more
  alternating runs around 1.01; Sieve 0.98 and 1.06); the micro table in both orders has
  `blockeach` and `sqrtw` 5% and 6% up in both, and in eight alternating processes of its
  own each reaches master's minimum (61.7 against 61.9 ns; 2.8 ns in all sixteen);
  start-up 1198 against 1194 ms, best of ten.
  So what the step bought is the cost of entering a method that interpolates, 13-36% on
  such a call, and no AWFY benchmark has one on its path. Gap 16's cost applies to a
  method whose usual callee wants the caller's scope; most of these reach such a callee
  (`raise`) only on their error path, which was not timed. Not run: net11.0, Windows, R2R.
- **perf/splat (/perf-frame, 2026-11-19) - a method that passes a splat on runs without a
  scope** (`inner(*args)`, `inner(*args, &block)`, `[*a]`, `yield(*a)`; `IR_LIGHT_SPLAT=0`
  turns it off). `IsScopeFree` takes a `SplattedArgument` of a scope-free expression, and
  the rewriter lets the `ExplicitSplatAction` site through: it is given the value only and
  calls `#to_a` through a conversion site, the helpers that join several splats take no
  scope, and `LightScopeCall` already bound a signature with a splatted argument. When the
  rule answers `LightScopeRequired` the call is made again with the same temporaries, so
  `#to_a` runs once (a probe of the matrix). `when *a` and `**h` still keep a method out
  (interpolation did until perf/interp, above).
  Census (Src/StdLib + AWFY, 28,335 methods), on the tree with #232 merged in (273d6299,
  as are the instruction counts below): light frames
  18,318 -> 18,790 (64.6% -> 66.3%), light blocks 7,317 -> 7,389, the JIT's verdicts
  unchanged; 472 methods moved up, none down. 38 are the prelude's (`strip`, `lstrip`,
  `rstrip`, `inject`, `reduce`, `zip`, `each_with_index`, `reverse_each`, `each_entry`,
  `to_set`, `raise`, `curry`...), none AWFY's.
  Against the same binary with the switch off, `ab.rb`: `w_splat` (`*args, &block` passed
  on) 429 -> 366 ns (0.85; eight rounds at load 1, spreads 3-7%, the bare call `w_raw`
  0.99), `strip` 0.84 and 0.85 in two runs, `lstrip` 0.70, `rstrip` 0.86. Instructions per
  call on the merged build: `w_splat` 0.911, `w_splat0` 0.911, `strip` 0.896, `lstrip`
  0.833, `rstrip` 0.890, `w_raw` 1.000. Bytes per call: `w_splat` 666 -> 474, `strip` 562
  -> 378, `inject { }` 658 -> 498. Slower: `inject(:+)` and `zip`, see gap 16.
  Gated on the tree before #232 was merged in (6eb8fa69 and 32698c1d against 80699c25):
  Util/splat-pass-matrix.rb (154 probes, every answer CRuby's) and the block-given,
  light-rest, block-pass, jit and jit-calls matrices, each with the change on, off,
  `IR_LIGHT_FRAMES=0`, `-X:NoJIT -X:NoOSR`, `-X:JITCalls` and R2R; IronRuby.Tests in Release
  on both trees (the same one failure, `Exception_CauseRaisedInClause1`) and in Debug
  (passes, new test `Method_LightFrameSplat1`); a million-deep recursion through a splat
  wrapper is SystemStackError with the JIT on and off; `Util/ci-sweep.sh` of all five
  suites, 32,680 examples, no new failure; AWFY, no FAIL, geomean 0.997 and 1.002 in the
  two orders (Bounce 2.6% and 4.5% slower; over a run of it the two builds' light-frame
  decisions differ in four prelude methods and in none of its own); the micro table, no line more than 5% slower in both orders; start-up 1.20 s on
  both. After merging #232: the census, the splat and block-pass matrices against CRuby
  and the unchanged build, `census.rb -c` on a run of core-small (59 methods called, 0
  differ), the instruction counts above, and IronRuby.Tests in Release (the same one
  failure) - the sweep and AWFY were not run again on the merged tree.
  Learned: the time fell twice as far as the instructions (8% of `w_splat`'s), because
  what went is an allocation; a method becoming a light frame is a gain only if the call
  it usually makes does not want the scope (gap 16); and `ab.rb -i` of one binary with a
  switch on and off moves shapes the switch cannot reach by up to 5%, even with four
  rounds - different methods compiled at start-up, different code for what they share.

- **perf/blockpass (/perf-frame) - a method that passes its block on runs without a scope**
  (`def w(&b) = inner(&b)`, `map(&:to_s)`, `&nil`, `&@ivar`, a local Proc, Method or Symbol;
  `IR_LIGHT_BLOCK_PASS=0` turns it off). `IsScopeFree` takes a call whose block is a
  `BlockReference` to a scope-free expression; the rewriter lets its `ConvertToProcAction`
  site through (it takes no scope), drops `TryConvertBlockWithRefinements` (a light frame
  has no definition refinements, so its scope would answer none and the helper null), gives
  `MethodRetry`/`MethodPropagateReturn` the scope made on demand (only after a callee answered
  a BlockReturnResult), and binds the call with a light scope as any other, the Proc as its
  block argument. One thing the matrix caught: a light frame that names its block stands for
  itself by that Proc, which `BlockParam.IsLiteralBlock` took for a library call's literal
  block, so `def w(&b) = lambda(&b)` made a lambda where CRuby raises; such a Proc is now
  marked (`Proc.IsMethodConverted`), and
  `IsLiteralBlock` asks the converter, so a `dup` or `clone` of it is not literal either (review;
  `x.then { lambda(&b.dup) }` was already wrong on master). `prelude-cost.rb`, ten alternating rounds against
  master at load 2.5: `w_pass` (one layer) 362 -> 288 ns (0.80 of the switch-off build),
  `w_pass2` (two layers) 374 -> 268 (0.72), the bare call `w_raw` unchanged (CRuby 72, 112,
  152). Over spec/core of ten classes, 266 prelude methods are light frames against 238
  (`min_by`, `max_by`, `minmax_by`, `flat_map`, `each_cons`, `each_slice`, `each_with_index`,
  `reverse_each`, `sort_by!`, `bsearch`, Enumerable's `take_while`/`drop_while`...); in them the
  entry is small beside the loop, 2-4% fewer instructions per call (`ab.rb -i`). Checked:
  Util/block-pass-matrix.rb (86 probes, every answer CRuby's), block-given, light-rest, jit and
  jit-calls matrices, each with the change on, off, `IR_LIGHT_FRAMES=0`, `-X:NoJIT -X:NoOSR`,
  `-X:JITCalls` and R2R; block-scope-reuse (both), hot-shapes and raise matrices with the JIT on
  and off; a million-deep recursion through `&b` is SystemStackError; IronRuby.Tests in Release
  on both trees (the same one failure, `Exception_CauseRaisedInClause1`); `Util/ci-sweep.sh` of
  all five suites, 32,675 examples, no new failure (`core/dir/chroot_spec.rb` loops under root
  through the `spec` symlink, and passes on both builds from the main checkout). AWFY at load
  6: geomean 0.955 and 0.972 in the two orders; Json, NBody and Storage slower in both, but
  their light-frame decisions are identical on the two builds (as are the micro table's), so
  the change does not reach them. Learned: `IR_LIGHT_FRAMES=why` over a spec run, with the refused
  statement classified by what it holds, is the census that picks the shape; and a light frame that names its
  block hands that Proc to everything it calls - anything that asks a Proc where it came
  from has to know.

All four measured alone against 22e3f91 on a quiet machine, then together; the merged tree
gives the same results as 22e3f91 on IronRuby.Tests (one failure, `Exception_CauseRaisedInClause1`,
which fails in Release on 22e3f91 too), ruby/spec language (2935 examples) and core for
the classes touched (12419 examples), and matches CRuby on `Util/perf/hot-shapes-matrix.rb`
with the JIT on and off. Start-up is unchanged (1.44 s for `-e 1`, best of 12, both).

- **perf/fmap (/perf-core) - `filter_map` of an Array with a one-value block**: 959 -> 775
  ns (CRuby 472).
- **perf/uniq (/perf-core, correctness with a small gain) - `Array#uniq { }` as MRI's
  `rb_ary_uniq`**: no block call for one element or none, an index walk, five probes now
  CRuby's; 885 -> 763 ns (CRuby 350), short of the 15% a speed change needs - the Hash of
  keys is most of what is left.
- **perf/fm (/perf-core) - `flat_map` of an Array**: one value per element and
  `Array.try_convert` for the block's answer, as MRI's `flat_map_i`: 1820 -> 1406 ns
  (CRuby 835), scalar answers 1348 -> 1043 (777).
- **perf/tw (/perf-core) - `Array#take_while` and `#drop_while` are Array's own, as in MRI**:
  620 -> 522 and 676 -> 522 ns (CRuby 236), and eight cases now match CRuby (owner,
  backtrace frames, a subclass's own #each, an Array shrinking under the block).
- **perf/minbyn (/perf-core) - `min_by(n)`, `max_by(n)` of an Array through the C#
  `sort_by`** (a stable merge sort, under a private alias): 2676 -> 1308 ns on the merged
  build (CRuby 698). Keys that do not compare now raise the key's error, as MRI. Learned:
  sorting four Arrays by `Array#<=>` cost 950 ns - prefer a C# method the prelude already
  has, taken under an alias at load so a redefinition cannot reach it.
  Review fix (PR 228): the C# sort_by called the block itself, so a backtrace from it showed
  Array#sort_by where MRI's shows Array#each, and a count of 0 or a negative one ran the
  block before answering [] or raising. The keys now come from a walk with #each and are
  handed to the C# sort by position, and the count is checked first: 1266 -> 2040 ns
  (`max_by(2)` 1295 -> 2116), still under the 2676 before. Next lever: a C# helper that
  sorts the elements by keys it is given, leaving out the second block call per element.
- **perf/minby (/perf-core) - `min_by` and `max_by` without a rest Array or a `|*values|`
  block**: `[1, 2, 3, 4].min_by { }` 1339 -> 1032 ns on the merged build (CRuby 448),
  `max_by` 1300 -> 969. Still #each underneath, because CRuby's backtraces show Array#each
  under an Enumerable method; an index loop that dropped that frame was caught by
  Backtrace_LibraryFrames1.
- **perf/toh (/perf-core) - `Array#to_h` as MRI's `rb_ary_to_h`**: an index loop,
  `block_given?` once, each pair through `Array.try_convert`. `[1, 2, 3, 4].to_h { |x| [x, x] }` 2120 ->
  1276 ns on the merged build (CRuby 548), `[[1, 2]].to_h` 796 -> 376 (114); three cases now
  give CRuby's answer (an Array subclass with its own #to_ary, a bad #to_ary, a BasicObject).
- **perf/string, a52c7501 (/perf-core) - `Kernel#String` answers a String without a
  scope**: `String("a")` 238 -> 118 ns on the merged build (CRuby 50). Learned: a method
  that answers the common case on its first line still pays for the scope the rest of its
  body needs (a `rescue`, a singleton class): split the rest into a private method.
- **perf/sum, ccc3608d + its follow-up (/perf-core) - `Array#sum` with a block adds its
  values itself**, as MRI's `ary_sum` does: Integers, then Floats with the compensated
  sum, and anything else handed to Enumerable#sum's fold from where it got to.
  `a.sum { |x| x + 1 }` 1421 -> 868 ns (CRuby 236), over Floats 2175 -> 1539 (312); the
  call without a block unchanged. Learned: a prelude loop must count with `i = i.succ`, not
  `i += 1` (a redefined Integer#+ made it skip elements); an Enumerator is too dear for a
  hot fallback; and a body grown by a rarely used path made the common call 5% slower until
  the path became a method of its own. `Hash#sum { }` (3.5) is still Enumerable#sum's.
- **perf/dig, b0bbefed (/perf-core) - `Array#dig` steps into the next level without two
  Ruby helpers that make a scope**: `[[1, 2]].dig(0, 1)` 682 -> 356 ns on the merged build
  against the commit before (CRuby 60), `dig(0)` 328 -> 158, three keys into a Hash 784 ->
  652; four rounds, no overlap. What is left (about 6x CRuby): the call into a method with
  a rest parameter, `__ir_responds_to__`, and the second `dig` call. Learned: an
  interpolated call, even on an error path, keeps a method out of light frames.
- **perf/t1, c9a17e2 - methods that make calls run without a scope until a call needs one.**
  Every call site took the caller's RubyMethodScope, so only call-free bodies were "light";
  any other allocated a 160-byte scope per call. Now a call in a light frame goes through a
  site bound with `RubyCallFlags.HasLightScope`; a rule that would read the scope returns
  `RubyOps.LightScopeRequired` and the method makes its scope and repeats the call.
  getter 69 -> 40, cmp 85 -> 52, nil? 85 -> 47, ivar_rw 87 -> 61 ns; AWFY geomean 0.906.
  `IR_LIGHT_CALLS=0` turns it off.
- **perf/t3, ad47bb7 38a149b 2d29947 - the JIT compiles `s == "lit"`, a final `C.new(...)`
  and a final element store.** str== 139 -> 63, new2 125 -> 78, arr 66 -> 50 ns. AWFY
  unchanged: its hot methods make general calls, which the JIT declines.
- **perf/t4, fc2f579 - precompiled dispatchers chain into a polymorphic inline cache**
  (up to 8 links, then `site.Update`). poly2 121 -> 102 ns alone, 85 with perf/t1.
  AWFY unchanged, for the reason under gap 3.
- **perf/t2, fd94bce - a light-frame yielder's block literal reuses its Proc and
  BlockParam.** yield 87 -> 62 ns. AWFY unchanged. Its safety rests on every way of reaching
  a block's scope or Proc clearing the reuse mark (`RubyScope.MarkCaptured`,
  `CreateMethodScopeLazy`, `InitializeBlock`): a new path that keeps a block scope must
  clear it too.

Together: AWFY 10-16% faster than 22e3f91 in back-to-back runs (geomean 0.84 and 0.91).

Round two, each measured alone against 4c78e82 on a quiet machine (two AWFY runs), then
together; the merged tree gives 4c78e82's results on IronRuby.Tests, ruby/spec language
and nineteen core directories (17123 examples; the expectation count differs only in
core/thread, where it varies from run to run on the baseline too), and matches CRuby on
`Util/perf/hot-shapes-matrix.rb` with the JIT on and off.

- **perf/lf, 0b3043d - light frames take attribute and element assignment, string and
  symbol literals, interpolation, case/when and while loops.** These, not anything deep,
  were why most hot benchmark methods still made a scope: the cheap filter
  (`IsScopeFree`) refused them before the rewriter looked. With perf/blk: AWFY 0.871 and
  0.862 (perf/blk alone 0.975), so about 11% from this.
- **perf/blk, baaa807 96881ad - Proc and BlockParam reuse for library iterators that only
  yield (`[BlockNotKept]`), and scope-free blocks that make calls or use outer locals.**
  each4 281 -> 203 ns, blockeach 81 -> 70; AWFY 0.975, DeltaBlue 10% faster. `[BlockNotKept]`
  is a promise: a marked method that hands `block.Proc` to anything lets a reused Proc escape.
- **perf/prof, 1ee9cdc - the cached instance variable read and write, inline** in compiled
  code, the method JIT's `GetIVar`/`SetIVar` and the attr dispatchers. A method doing many
  `@x` reads ~40% faster; ivar_rw 51 -> 46 ns; AWFY 0.982 in both runs.

Round three, measured against 3237c08 on a quiet machine, three AWFY runs: geomean
0.956, 0.953, 0.967. Same gate as round two (17025 examples, 78747 expectations, identical
on both trees), plus raise-, full-message- and error-message-matrix identical to the baseline.

- **perf/sym, 88cc224 - compiled Ruby bodies get ASCII names** (`IR|label|line|id|file`,
  `RubyStackTraceBuilder.EncodeMethodName`/`TryParseRubyMethodName`). The runtime's
  perf-map writer damaged every non-ASCII name; that was the 45% `[unknown]`.
- **3c938e1 - `RespondToStorage` caches its symbol**: `Array#==` interned `:to_ary` per call.
- **7c7ec90 - `IsTrue`, `IsFalse`, `SetAttribute` inlined; attr rules' helper out of tail
  position.** The DLR compiles every rule with explicit tail calls, and the JIT never
  inlines a tail-call target. CPU time of a fixed run 3-4.5% lower.
- **f04fe5a - `IR_LIGHT_FRAMES=why`** prints why a method is not a light frame.

Round four, each measured alone against e888778 on a quiet machine, three AWFY runs,
then together. The merged tree prints what CRuby and e888778 print on hot-shapes-, jit-,
ivar-, block-loop- and block-scope-reuse-matrix with the JIT on and off, passes
ivar-stress, and has e888778's results on IronRuby.Tests and on ruby/spec language plus
22 core directories (17339 examples, 79716 expectations on both).

- **perf/alloc, f4936ac - register locals in light frames and scope-free blocks**: AWFY
  0.869, 0.869, 0.901 with the two below. `ScopeBuilder.RegisterLocalsRewriter` gave up at
  every `LightDynamicExpression`, so since c9a17e2 nearly every light method and block
  allocated a MutableTuple for its locals per call; it now visits the site's children.
  Found by counting allocation by type, not by timing. **0cbb11d** caches Integer boxes
  -1024..65535 (`Protocols.BoxInt32`); **4a5b605** shares the CLR string of a
  one-character ASCII slice; **112af68** adds `Util/perf/alloc.rb`.
- **perf/jiv, d14eddf 9ca993d 6347585 bf354cf d88766c - a wider method JIT**: AWFY 0.951,
  0.974, 1.003; cmp 48 -> 30 ns, `==` 45 -> 28, attr 10.9 -> 4.3, Richards 7-10% faster.
  Instance variables as operands (typed from a sample of the receiver at the threshold
  call), `!x`, ivar writes when every deopt comes before the first write (no loop, no
  self-call), identity compares (086e90d redone, `s == "lit"` kept ahead of it), attr
  reader/writer calls on self and other receivers; if/case arms emitted in the order they
  run, which the deopt-before-effect rule needs. DeltaBlue's specialized methods 6 -> 22
  without getting faster (gap 3).
- **perf/frame, 06e0c2b - `EnterLightMethod`, `TakePendingCallee`, `LeaveLightMethodFrame`
  inline their common path**: AWFY 0.980, 0.976, 0.979.

Round five, measured against 9bdb5da9 on a quiet machine in two sessions, three AWFY
runs each: 0.973, 0.935, 0.953 and 0.955, 0.952, 0.976; DeltaBlue 9-15% faster. The merged
tree prints what CRuby and 9bdb5da9 print on hot-shapes-, jit-, ivar-, block-loop-,
block-scope-reuse- (and -blockpass) matrix with the JIT on and off, passes ivar-stress and
block-scope-reuse-race, and has 9bdb5da9's results on IronRuby.Tests and on ruby/spec
language plus 24 core directories (17983 examples, 80877 expectations on both).

- **perf/alloc2 (alone 0.985, 0.959, 0.966; bytes per iteration 0.65 of before):**
  **aaa2f995** `Proc#call` reuses the BlockParam of the proc's last call (kept on the
  Proc, taken with an atomic exchange, handed back only after a normal return of a block
  whose scope was not captured; `RubyScope.MarkCaptured` marks it kept); **94dc9169** a
  `yield` inside a block does the same; **4de5fc96** register locals survive a `while`
  loop's OSR site (the tuple is built from the registers on demand); **50318082** a
  method containing a call with a literal block can be a light frame, its scope made
  right before the Proc (`IR_LIGHT_BLOCKS=0` turns it off) - what the rejected parked
  scopes were after; **76058948** light frames take optional parameters with scope-free
  defaults (`IR_LIGHT_OPTIONALS=0`).
- **perf/jfast: 481ee803** the method JIT's identity sites take the class of nil, true,
  false and Symbols from a field, not `GetImmediateClassOfNonRubyObject` (4% of
  DeltaBlue); **70fdd571** `ReadSelfAttr`, `WriteAttr` and the type guards inline their
  fast paths. Alone it measured flat overall (DeltaBlue 3-10% faster, Richards 2-4%
  slower); with perf/alloc2 the pair beat perf/alloc2 alone in every run.

Round six, each measured alone against the working branch on a quiet machine, three AWFY
runs, then together. Gate: 14 matrices with the JIT on and off against CRuby and the tree
before, ivar-stress and block-scope-reuse-race, the reproductions of the faults below,
IronRuby.Tests by failing name, ruby/spec language plus 32 directories (23065 examples,
92368 expectations, identical on both trees).

- **perf/str, 965aac8a - one-character ASCII slices share their content**: AWFY 0.979,
  0.976, 0.982; Json 5-10% faster and 46.3 -> 35.9 MB. `s[i]` is a new MutableString on
  one of 128 static, owner-less StringContents; `_content` became a property over
  `_rawContent` that swaps in a content of the string's own (compare-and-swap) before any
  other read or any mutation. New code must not read `_rawContent` directly: a shared
  content has no owner. 72 B -> 40 B per slice.
- **perf/tramp, b78ec3f5 - a stub's callers read its current target**: AWFY 0.986, 0.978,
  0.988. `JitStubN.Target` holds the specialized entry, or the generic body once the
  method is declined or retired; rules read it and make one call
  (`JitStub.CallCurrent`), dispatchers are re-pointed by `Install`, and a settled body's
  delegate becomes the generic one. `JitStub*::Call` was 3.8% of Richards and 2.2% of
  DeltaBlue; 2-7 hot methods per benchmark had been behind a dead trampoline.

Round seven: five experiments at once, one agent and one worktree each, on the benchmarks
slower than CRuby when all thirteen were run for the first time (Permute 1.49, Richards
1.33, CD, Json and DeltaBlue 1.17, NBody 1.07, on the tree of round five). Three were
kept. Each was measured alone against 9f4d44f8 (round five plus its review fixes) on a
quiet machine, all thirteen benchmarks, in one awfy.rb invocation with the others as
further columns, and together in both column orders (0.954 and 0.959). Round six landed
meanwhile; merged with it and measured against 72c29b4c, the geomean of the thirteen was
0.944 and 0.942 in one run of each column order and 0.972 in a third with both trees
beside CRuby: CD 0.647, 0.654, 0.624; Permute 0.900, 0.916, 0.895; Json 0.967, 0.984,
1.075, and 0.870, 0.961, 0.969, 0.953 in four runs more, so Json is level or a few
percent faster and round seven's share of it is a per-operation gain. Two benchmarks
looked slower and were not. Towers: 7.6% and 10.5% slower in one pair of runs, then
1.10/1.05, 0.93/0.94 and 0.98/0.99; none of its hot methods is compiled. Storage: more
than 5% slower in four of seven runs, by up to 30%; then, in eight processes of 120
iterations per tree, taken alternately, a steady state of 86.2 ms on 72c29b4c and 87.1
here (means 88.1 and 86.4). For both, the JIT's decisions are the same line for line on
the two trees, and for Storage the bytes and the profile too. awfy.rb's twelve seconds
are not enough for a benchmark whose iterations range from 69 to 177 ms in one process.
Gate, merged tree against 72c29b4c:
IronRuby.Tests in Release on net10.0 (1253 tests, one failure on both trees,
`Exception_CauseRaisedInClause1`; on net11.0, run on the merged tree only, all 1253 pass);
ruby/spec language, core/string and core/array with basicobject and comparable (2935,
3963 and 3124 examples, the same failing examples); jit-, hot-shapes- and ivar-matrix
print what CRuby prints with the JIT on and off (642, 742 and 252 lines); ivar-stress
passes; 21 odd receivers of a store, one process each, as CRuby; start-up is unchanged
(`-e 1`, best of 12: 1.21 s on both, medians 1.34 and 1.37).

- **perf/cd, c8802329 - the method JIT types attr reads and writes on a local assigned
  once from an attr read** (`init = motion.pos_one; fin.x - init.x`). `TrySample` knew
  self, self's instance variables and parameters; CD's hottest method,
  `CollisionDetector#is_in_voxel`, reads attributes of locals and was declined ("call:
  x/0"), so its forty Float operations went through sites and boxed. The local's sample
  is what the sampled owner's instance variable holds; it is a profile, and every read
  still checks the receiver's class and the method version. Alone: CD 0.632 and 0.720,
  geomean 0.973 and 0.976; bytes per CD iteration 516 MB -> 226 MB; locattr 66 -> 40 ns.
  The first iteration is not slower (1.5 s against 2.1 s). The same profile reaches
  `EmitMethodAttrWrite`, so `c = self.a; c.v = x` is compiled too, which the commit does
  not say: jit-matrix has the cases (frozen leaf, a writer of its own, redefined,
  singleton, a failing call or ivar write after the store).
- **perf/permute, d027dbe6 ff744878 - an element store before a method's last one goes
  unlogged** when nothing between it and the last store can deopt or run Ruby code. In
  `tmp = @v[i]; @v[i] = @v[j]; @v[j] = tmp` the first store went through the thread's
  JitUndoLog only so that a deopt in the second could take it back: 9% of Permute. The
  last store's receiver is now read and checked to be an Array itself before the first
  store, and the last store hands what its fast path cannot do (growth, an index that
  raises, a frozen Array) to a call of `[]=`. Alone, five runs: Permute 0.862, 0.812,
  0.856, 0.868, 0.841; geomean of the thirteen 1.010 and 0.999; swap 74 -> 54 ns. It is
  used only for an instance-variable or local receiver, while Array#[]= is the public
  builtin, with no refinement active; everything else compiles logged as before. See
  "Caught by the gate" for why the receiver is checked first.
- **perf/json, e4fc38aa 90219459 - the method JIT compiles `"lit" == v` and `"lit" != v`**,
  and both string intrinsics compare an ASCII literal by characters with a String that
  holds characters in an encoding where ASCII is its own bytes
  (`MutableString.EqualsAsciiLiteral`). Json's `is_white_space` and `is_digit` are chains
  of `' ' == @current`; with the literal as the receiver they were screen-rejected. Per
  call, against 9f4d44f8, three rounds: four comparisons 224, 219, 229 ns -> 96, 103,
  102; ten 477, 463, 466 -> 159, 163, 164; against 72c29b4c lit== 167 -> 58 ns. On AWFY
  it is inside the spread (Json 0.984 and 0.963 alone): kept on the per-operation rule,
  like perf/t3. Merged with round six's shared contents it needed one change: read through
  `_content`, a one-character slice got a content of its own in every comparison, 39.5 MB
  per Json iteration against 35.8; it asks `SharedAsciiChar` first now, as Equals does
  (35.9).
- **Tooling**: the skills run all thirteen benchmarks for every recorded number and
  decision, both column orders, all candidates of a round as columns of one run;
  `micro.rb` has `lit==`, `swap` and `locattr`; perf-gate asks a fourth question and says
  how to tell that a differential case reaches its path.

Round eight: three experiments on the tree of round seven (NBody's inner loop, Permute's
frame, `!=` as one dispatch); the last two are under Rejected. One landed, and it moves
no AWFY benchmark: geomean of the thirteen against 6c932eb3's code, both column orders,
1.004 and 0.998; NBody and CD the same in separate processes taken alternately; start-up
not slower. It is kept on the per-operation rule. Gate against the tree before:
IronRuby.Tests in Release (net10.0: 1253 tests, the one known failure on both; net11.0,
this tree only: all pass); ruby/spec language, core/range, core/math, core/integer,
core/array and core/float (2935, 606, 243, 598, 2898 and 260 examples, the same failing
examples); block-loop-, osr-array-, jit-, hot-shapes- and ivar-matrix as CRuby with the
tiers on and off, int-matrix as the tree before; ivar-stress; 124 lines of odd arguments
and odd elements met on hot sites, shown by IR_OSR_VERBOSE to reach the compiled loops.

- **perf/nbloop, 587fb944 097600ad cdc12734 5183dd88 - `Math.sqrt`, attr reads and writes
  on a loop-assigned local, and `Range#each`, in compiled loops** (gap 2). `Math.sqrt(x)`
  on a number is the square root with no call, in the method JIT and the loop compiler,
  while the constant is the Math module and its singleton `sqrt` the library's (a
  negative argument deopts, so the call raises Math::DomainError): 56 -> 2.5 ns in a
  `while` loop, a method computing `Math.sqrt(@x*@x + @y*@y)` 88 -> 53. An attr read, an
  attr write and `o.x op= v` through a local the loop assigns (`b = bs[i]; b.v -= b.x`)
  compile in a core iterator's block, the local's class sampled from the Array it is
  assigned from and checked at every read and write; the store logs what it replaces,
  as stores in loops did: 83 -> 62 ns for two op-assigns in a 20-iteration `times` block.
  `Range#each` over Integers is `Integer#upto`'s outlined loop for a run above the floor
  (39 -> 2.2 ns per element of a 1000-element run); a run at or under it takes the loop
  it took before. Two things were taken back after the first version: the attr changes
  in `while` loops (CD's bytes +3.0%, gap 2), and a rewritten `Range#each` loop for
  short runs, which was not shown to cost anything but had no reason to differ.
  `locattr` (round seven's shape) had a median of 38.0 ns on the tree before and 41.1 on
  this one over twelve alternating processes, with the same minimum (33.7 and 33.9) and
  the same JIT decisions: both trees have a fast and a slow state for it and this build
  was in the slow one more often. Not explained.

Round nine: three experiments on the tree of round eight (the entry of a compiled block
loop, a small pure callee inlined, DeltaBlue's `Vector#each`), all three kept, two of them
after going back to their authors once. The three together against round eight's tree,
all thirteen, both column orders on a quiet machine: geomean 0.970 and 0.941; NBody 0.67
and 0.66, CD 0.96 and 0.89, Json 0.96 and 0.95, nothing slower in both. What one run
showed slower was taken again in separate processes alternately, medians of six, before
and after: List (1.28 in one run, 1.00 in the other) 68.0 and 67.5 ms, Bounce (1.04 in
both) 83.7 and 83.4, Richards 388 and 375, Storage 90.0 and 89.2, Sieve (1.05 and 0.99)
127.4 and 132.8, then 135.0 and 129.7 over eight more. Gate against the tree before:
IronRuby.Tests in Release (net10.0: 1253 tests, the one known failure; net11.0: all
pass); ruby/spec language, core/range, integer, float, array, enumerable, module, method,
kernel, tracepoint and basicobject (2935, 606, 598, 260, 2898, 536, 1050, 199, 2230, 76
and 172 examples, the same summaries and the same failing examples); jit-, hot-shapes-,
ivar-, block-loop-, osr-array-, block-scope-reuse- and the new inline-matrix as CRuby
with the tiers on and off, int-matrix as the tree before; ivar-stress and
block-scope-reuse-race; four probe files of the gate's own (32, 23, 26 and 18 lines),
shown by IR_JIT_VERBOSE and IR_OSR_VERBOSE to reach the new paths; eight benchmarks
verify with the tiers off; start-up CPU time level (1.25-1.34 s on all four trees).
After #215's branch was merged in again (through 9b2936fa, cd089543): IronRuby.Tests the
same on both frameworks, the matrices as CRuby with #215's new cases in them
(hot-shapes-matrix 1264 lines, jit-matrix 676), and the full five-suite sweep
(`Util/ci-sweep.sh`, Release net10.0, four jobs): core 23130 examples, library 6011,
language 2935, command_line 175, security 34; no failing example that
`Util/spec-known-failures.txt` does not list, none of its entries newly passing, no file
without a summary line.

- **perf/entry, 8160d9c2 374f856a 389aa0d4 - an entry of a compiled block loop allocates
  nothing, and each block has a floor by the weight of its body** (gap 2). A site keeps
  one argument array with the block's tuple in it; an entry takes it with an atomic
  exchange (a second thread, or a re-entry, allocates its own), and puts it back
  cleared in a `finally`. The tuple is all `object` slots to a power of two, set and
  read through its properties instead of `MutableTuple.SetValue` (a virtual call, a
  switch and a cast per slot: about 40 ns of an entry). With that an entry costs about
  45 ns more than a yield, and a call the copy types saves 4 or 5, so the floor is
  counted from the block's body: 1 for twelve calls or more (operators and attribute
  reads included; NBody's inner block has about 47), 2 for six to eleven, 5 below that,
  and `map`, `select`, `any?`, `all?`, `none?` keep 16. NBody 1880-1970 -> 1280-1410 ms in
  alternating processes (CRuby 1610), 2892 -> 1364 MB per iteration; Richards 19.6 ->
  13.9 MB with no change in time. Per call of the iterator, three alternating
  processes, before and after: a ten-call attribute body over 2 elements 288/292/260 ->
  222/208/184 ns, over 3 339/389/344 -> 188/201/200, over 8 875 -> 265; an eight-call
  Integer body over 3 223 -> 178, over 8 551 -> 224; `s += x` over 8 251-265 -> 173-180;
  everything under its floor level.
- **perf/cdtree, 80745184 2d174fc5 - the method JIT inlines a call of a small pure
  method** (gap 3): `compare_numbers(@x, other.x)`, `self.m(a)`, or `o.m(a)` on a local
  whose class the profile sampled, where m is a specialized Ruby method whose typed body
  has no effect, no loop, no constant and no call. The caller invokes that body's lambda,
  which the expression compiler inlines. The guards are the call site's: self's class is
  pinned at the entry, an explicit receiver's is checked where the call is, the global
  method version covers redefinition, override, singleton, prepend, removal and
  visibility; an explicit receiver reaches only public methods, and nothing is inlined
  where a refinement is active. The body can only deopt, never raise, so an exception
  comes from the real callee with its own frame. A caller that turns hot before its
  callee profiles on, up to three times. `selfcall` 80-90 -> 33-34 ns; newly compiled:
  CD's `Vector2D#compare_to` and `Vector3D#squared_magnitude`, DeltaBlue's
  `Dictionary#get_bucket` and `Strength#weakest`, and `Vector#empty?` inside
  `remove_first`. CD about 5% faster in alternating processes (552 -> 526 ms median of
  five, and 0.96 and 0.89 above), DeltaBlue 1-3%.
- **perf/dblue, fc16b146 - a Range literal needs no scope, and two Fixnums make a Range
  without `<=>`**. `Range#initialize` called `<=>` on any two bounds; MRI's `range_init`
  does not ask two Fixnums, so `(1..n)` ran a redefined `Integer#<=>` where CRuby does
  not (a difference, fixed: the count of `<=>` calls now matches at 62 bits both ways).
  The literal takes its context from its comparison storage, not the scope, so a method
  whose body makes a Range can be a light frame - except in a body with a literal
  block, where the scope is made before the Proc anyway and SOM's `Vector#each` measured
  5-7% slower as a light frame. `Range#initialize` checks the stack before the `<=>` it
  still makes, since the frame that calls it may have no entry check. `rangelit` 140-143
  -> 93-94 ns; Json 86.9 -> 80.1 ms median of five alternating pairs, faster in each, and
  35.9 -> 30.7 MB. DeltaBlue, its target, does not move (see gap 14 for where its time
  is).

Round ten: four experiments on the tree of round nine with #215 merged through 9b2936fa
(the fixed cost of an iterator call, DeltaBlue's `Vector#each`, Richards, CD's tree
lookup). Three changes kept, one of them a side find; `Vector#each` kept nothing. All
thirteen against the tree before, both column orders: List 0.59 and 0.57; the other
twelve inside the noise of a machine that was restarted between the rounds and has been
uneven since (geomean 0.976 and 1.006, with Bounce at 1.10 and 1.92 and Json at 1.11
and 1.08, slower in both orders). In separate processes taken alternately, medians of
six, before and after: Json 91.4 and 89.7 ms, Bounce 98.7 and 98.8, Storage 105.8 and
103.0, Queens 108.2 and 104.0, CD 611 and 578, NBody 1555 and 1598, Towers 114.6 and
119.8 and then 125.3 and 123.2 over eight more; the JIT's decisions on those benchmarks
are identical on the two trees. Gate against the tree before: IronRuby.Tests in Release
(1254 tests; net10.0 the one known failure, net11.0 all pass); the full five-suite
sweep (core 23130 examples, library 6011, language 2935, command_line 175, security 34:
nothing that `Util/spec-known-failures.txt` does not list); jit- (684 lines),
hot-shapes- (1264), ivar-, block-loop-, osr-array- and block-scope-reuse-matrix as CRuby
with the tiers on and off, inline-matrix (50 cases) as CRuby but for one case that
differs on every tree (`42.compare_to(x)` reaches `Int32.CompareTo`, see below), enum- and
enumerable2-matrix as the tree before; ivar-stress and block-scope-reuse-race; four
probe files of the gate's own (170, 19, 15 and 4 lines) shown by IR_JIT_VERBOSE to reach
the new paths; six benchmarks verify with the tiers off; start-up CPU time level
(1.31-1.38 s on both trees), and a file defining 15,000 methods in 3,000 classes loads
in 2.2 s on both.

- **perf/rich, f2ab0a9e - the method JIT samples a list cursor through the local it
  starts from** (a side find of the Richards experiment). A local assigned more than
  once had no sample, so `xt = x; while yt; ...; xt = xt.nxt` read `xt.nxt` through a
  call site. Its first assignment in source order, when that is from another local,
  now names where the sample comes from; it is a profile only, and every read checks
  the receiver's class and the method version. `List#is_shorter_than` and CD's
  `tree_minimum` compile: List 74 -> 44 ms (0.59 and 0.57 above; 0.37-0.40 of CRuby),
  8.2 -> 0.6 MB per iteration; `listwalk` 108-121 -> 71-72 ns.
- **perf/cdt2, 9a653abb - an inlined call on a parameter of up to two classes, one level
  deep** (gap 3). Three things kept `RedBlackTree#find_node` generic: the screen
  resolved `compare_to` on the tree's own class and took only leaf callees
  (`Vector2D#compare_to` calls `compare_numbers`); `current`, assigned in the loop, had
  no sample; and `key` has two classes in CD (CallSign and Vector2D), so a body compiled
  for the first deopted on every call with the second and was retired. Now a call on
  another object is admitted when some top-level class defines an inlinable method of
  that name, such a callee may itself call one leaf on self, a first assignment from an
  instance variable gives a cursor its sample (`current = @root`), the profile records
  the first two Ruby classes of each argument and a call on a parameter with two
  inlines both behind a class test (a third class deopts), and a body that inlined on
  one class and deopts too often profiles again, twice at most, instead of being
  retired. `treefind` 889-989 -> 529-548 ns (CRuby 1508); CD's time does not move
  measurably (gap 3).
- **perf/iter, 9bbc9157 6b4e5960 - the pattern forms of `all?`, `any?`, `none?` and `one?`
  are C# overloads, not prelude wrappers** (gap 13). The C# `Array#any?` walked the
  Array directly all along; the prelude redefined the four methods as `(*args, &block)`
  wrappers for the pattern argument, so every block form paid a splat Array, a Proc, a
  call of the argument check and a second call with `&block`. `a.any? { }` over 1, 2
  and 4 elements 415-429, 453 and 490-513 ns -> 185-188, 212-214 and 258-265 (CRuby 79,
  113, 183). Two differences from CRuby went with the wrappers: a private `===` on the
  pattern is called (CRuby calls it with rb_funcall; the wrapper raised NoMethodError),
  and an Array subclass's own `#each` is not (the wrapper called it; so did `one?` with
  a block or without an argument, which now walks the Array as `rb_ary_one_p` does).

Gap 2's `each_index` (one experiment on 6e146fa3, measured against a Release build of that
commit, taken alternately on a loaded machine: other sessions were benchmarking). The
measured build is 8a70f030 (the change; the commit after it touches only this file and a
test), on 2026-11-15. Its core count and load average were not written down, which is why
the entry below carries a second measurement that has them. Kept on
the per-operation rule; it moves no AWFY benchmark. Gate against the tree before:
IronRuby.Tests in Release on net10.0 (the one known failure, `Exception_CauseRaisedInClause1`;
the new `BlockLoopEachIndex1` passes); ruby/spec language and core/array (2935 and 2898
examples, the same one error on both trees); block-loop- and osr-array-matrix as CRuby
4.0.9 with OSR on and off; the thirteen AWFY benchmarks verify; IR_OSR_VERBOSE shows the
matrix's `each_index` blocks specialized.

- **`Array#each_index` is a block-loop kind** (gap 2). `BlockLoopKind.EachIndex` is
  Each's loop yielding the counter (`while #i < #a.size; i = #i; body; #i += 1; end`),
  entered from `IListOps.EachIndex` for a plain RubyArray with Each's floor and
  countdown; the size is read every step, as `rb_ary_each_index` does, and a redefined
  or private `Array#size` does not change the run. `a.each_index { |i| s += a[i] * 2 + i }`
  per element, three alternating runs: over 1000 58-61 -> 5.7-6.2 ns, over 16 65-74 ->
  31-33, over 5 100-106 -> 81-87 (CRuby, same load: 71, 145, 187); a 12-operation Float
  body over 1000 196-202 -> 5.7-9.2. NBody: 1.80-2.22 s per 250,000 against 1.75-2.27,
  the same, since neither of its `each_index` blocks reaches the loop compiler (gap 2).
  Measured again on 2026-11-16 with the conditions recorded: Release net10.0 builds of
  6e146fa3 and of the branch at 22d353ab (the change is 8a70f030), each from a worktree
  whose `git status --short` printed nothing, built one after the other and then measured
  with nothing else running; 4 cores, 1-minute load average 2.32 at the start (the builds
  had just ended) and 1.44 at the end. The script is not one of `Util/perf`'s: the same
  block inside `reps.times`, 4,000,000 elements per batch, the least of seven batches
  after two for warm-up, three rounds taking turns between the builds. Per element,
  6e146fa3 -> the branch: over 1000 39.9-43.0 -> 6.4-7.2 ns, over 16 46.3-47.4 ->
  18.0-19.1, over 5 63.4-64.6 -> 47.4-50.4 (CRuby 4.0.9, once, after the rounds: 37.9,
  45.4, 51.7); a Float body of 13 operations over 1000 (not the one above) 138.7-144.1
  -> 3.9-4.4 (CRuby 127.1). Every run printed the same sums. That is about 6x over 1000
  elements (the first measurement had 10x), 2.5x over 16 (2.2x) and 1.3x over 5 (1.2x),
  where entering the loop is most of the cost.

Round eleven: one experiment on master f37be3b1 (2026-11-16, 4 cores, load 0.3-1.4 while
measuring), stage 1 of `Docs/jit-resume-deopt.md`. **It does not meet the step's rule
and is in the tree all the same**, because the design lands each stage behind the switch
and says of this one that it is worth it for the mechanism, not the number. With the
switch off nothing is compiled differently, so "Current numbers" stand.

- **perf/resume1, 4375167a 01df6f28 (merged in d809920a) - `-X:JITResume`: an attribute
  operation after a typed body's first effect falls back to its own call** (gap 1).
  `IR_JIT_VERBOSE=1` now says why a method that passed the pre-screen was declined
  (`a deopt after an effect (attribute write task_holding= at line 216)`), and for
  Richards' `Scheduler#hold_self` and the three shapes of
  `Util/bench/jit-resume-shapes.rb` that was the only reason. With the switch `o.x` and
  `o.x = v` on another object after the first effect check the receiver's class, the
  method version, frozen-ness and tracing themselves (`JitRuntime.TryReadAttr`,
  `TryWriteAttr`) and, when a check fails, make the source's call once on the operands
  already evaluated: through a site with a light scope and the declaring scope, then
  through the full site with a scope made once per invocation, inside a light frame's
  try and finally, with the line kept for a backtrace and a stack check. Only bodies
  with no parameters and no locals; after the first fallback only literals, self,
  instance variable reads and other self-guarded attribute operations; no loop,
  self-call, element write, construction or explicit `return` in such a body; not the
  Struct member pair. Per call, `Util/perf/ab.rb -r 7`, two runs with the columns in
  both orders (CRuby / without the switch / with it): `hold_self` 39.8-43.6 / 60.8-61.2
  / 52.4-52.6 (0.87, 0.86), `hold_w` 33.7-35.7 / 53.9-55.4 / 46.3-49.6 (0.92, 0.84),
  `hold_ww` 49.1-50.0 / 69.1-69.3 / 60.6-62.0 (0.87, 0.90); the two controls that were
  compiled before, `bump` and `peek`, within their spread. All thirteen AWFY, two runs,
  both orders: with the switch 1.009 and 1.013 of f37be3b1, without it 1.018 and 0.996,
  nothing slower by 5% in both; Richards, the one benchmark with such a method (it is
  the only method in all thirteen whose `declined` line becomes `specialized`), 1.009
  and 1.061. The micro table, two runs, both orders: no line slower by 5% in both.
  Start-up the same (1.48-1.62 s). The gate: IronRuby.Tests with the one failure
  f37be3b1 has; `Util/jit-matrix.rb` (section 11 is new: 26 rows and a threads row, each
  shown by `RS_CHECK=1` to be specialized and to fall back) the same under CRuby, the
  JIT, `-X:NoJIT`, `-X:JITResume` and `-X:JITResume -X:NoJIT -X:NoOSR`; hot-shapes,
  jit-calls, ivar, raise, hash-struct and block-given matrices the same as f37be3b1 with
  the switch off, on, and on with `-X:JITCalls`; a sweep of all five ruby/spec suites
  with the switch on (32,285 examples) with nothing the list of known failures does not
  name, and one of the merged tree without it.
  The merged tree (with d4a97243, below) against the branch before it: the three shapes
  0.90, 0.83 and 0.90 with the switch; all thirteen 1.019 and 1.003 in the two orders.
  Towers was 8% slower in both (109.8 against 101.2 ms, 106.1 against 98.6) and is the
  same in eight alternating processes of each build (medians 102.5 and 101.8 ms, the
  two ranges 98.6-108.0 and 96.3-105.1): d4a97243 changes what a site does when it
  binds, which Towers does once.
  What it says about the gap: compiling the body takes 8-9 ns off `hold_self`'s 61, and
  the method is still 9-13 ns slower than CRuby's with both attribute operations inline.
  A body of this size is dominated by its entry (`bump`, one ivar increment, is 37-43 ns
  against CRuby's 26-28), so compiling more leaf bodies buys little; the calls *between*
  compiled bodies are where stage 2 has to pay.

Round thirteen: one experiment on fa69240d (2026-11-16, 4 cores, a container restarted
during the round; load 0.5-1.3 while measuring). No new code in the JIT: the round's
first step was to measure what was already in the tree and off.

- **perf/calls aaba4635 (merged in fd13114a) - direct calls between compiled methods on
  by default** (gap 1; `-X:NoJITCalls` turns them off, `-X:JITCalls` is still accepted).
  The prototype of `Util/bench/jit-methods.md`, Part 2: a typed body calls the typed
  body of another method it calls on self, where the inliner declines the callee (a
  loop, an effect, a self-call). It had been measured on its own shapes and never on
  the benchmarks. Six alternating processes of a fixed length per build and benchmark,
  median of the per-process medians, without / with the switch on fa69240d: List 39241
  (38315-40579) / 28271 (27492-28575) us, 0.720; the other twelve between 0.978 (NBody)
  and 1.034 (Richards), each with overlapping ranges; geomean 0.976. awfy.rb, both
  orders, four runs with the switch on the base build (two of them of four benchmarks)
  and two with the candidate build: List 0.71, 0.70, 0.71, 0.75, 0.74 and 0.71; geomean 0.986 and 0.984 for the candidate;
  no benchmark slower by 5% in both orders (CD 1.15 in one run and 0.98 in the other,
  Towers 1.00 and 1.12, Queens 1.02 and 1.08). The geomean alone is under the step's 3%:
  it is kept for List, a whole benchmark 28% faster, and because nothing pays for it.
  What changes in the JIT's decisions, all thirteen compared line by line: List's `tail`
  binds `is_shorter_than` (a callee with a `while`), CD's `Vector3D#magnitude` binds
  `squared_magnitude`; eight methods in Richards, Json, DeltaBlue and CD pass the wider
  screen and are then declined (gap 1 lists them), which costs each 40 to 120 profiled
  calls and nothing after: Richards, with three of them, is 344854 (332415-353663)
  without and 343535 (328345-365110) with direct calls in ten alternating processes on
  the merged build, though single awfy.rb runs of it ranged from 0.92 to 1.14. The
  micro table, two runs, both orders: no line slower by 5% in both. Start-up 1.49 and
  1.47 s, best of ten.
  The gate: IronRuby.Tests fails `Exception_CauseRaisedInClause1` on both trees and
  nothing else (`JitCalls1` now checks that the default binds and that an engine with
  the option off does not, `JitMethods1` sets it both ways); jit-calls, jit, ivar,
  raise, int, hash-struct, block-given, light-rest, hash-order, block-loop, osr-array,
  hot-shapes, the two block-scope-reuse matrices, ivar-stress and the inline matrix
  (one case per process) print what fa69240d prints, with direct calls on and with
  `-X:NoJITCalls` (raise and inline differ from CRuby in 2 lines each on both trees,
  int only in `hash` values); a sweep of all five ruby/spec suites, 32,285 examples,
  with nothing the list of known failures does not name; and a probe of 33 lines, eight
  of them shown by `IR_JIT_VERBOSE=1` to run a bound call: a bound callee recursing a
  million deep (SystemStackError, and the same answers afterwards), a callee that writes
  an instance variable and then meets an overflow, a Float, a String or nil (each write
  done once; those callers are declined, as a deopt after an effect), a bound callee's
  nil, false, 0 and found element tested for truth, a callee overridden, made private
  or protected, or overridden privately in a subclass, undefined with `method_missing`,
  given a singleton method, refined elsewhere, and four threads making one caller hot:
  CRuby's output on both trees.
  A difference from CRuby that the probes for this round found on master, and that
  direct calls widen from inlined callees to bound ones: a caller inside its typed
  body's loop goes on calling the callee it was compiled with when another thread
  redefines that callee, until the caller returns (`until done?(i, limit)` over an
  inlined `done?` never ends after `done?` is redefined to answer true; CRuby and
  `-X:NoJIT` see the new method at the next call). `jit-methods.md` calls it the caller
  in flight and gives the fix for a pure body, a version check after the loop's safe
  point with a deopt there; an impure one needs gap 1's resume. In practice a caller
  with a loop and a callee with effects is declined anyway (a loop in a body that can
  deopt and has an effect), so what direct calls add is callees with loops of their own.
  Not fixed in this round.

Round fourteen: three experiments on 45708767, side by side, as a workflow ("get
TruffleRuby's tricks with the JIT to work here too"): each built by a subagent in its own
worktree, then given to an independent reviewer whose only job was to find an input on
which it differs from CRuby where the baseline does not, then repaired once. The
container was restarted five times during it (other hosts; nothing on disk was lost, each
restart cost the running agents' work in progress). Two landed, one is under Rejected
(perf/undo). Measured by me on 2026-11-17 on a quiet machine (load 0.7-1.3), gated on
the tree with both merged.

- **perf/nest 7503ea01 47d030be (merged in c256bd77) - a core iterator's nested literal
  block runs in place inside a block loop** (gap 2). The block-loop screen took only
  innermost blocks; an outer block may now hold `(a...b).each`, `n.times`, `a.upto(b)`
  or `a.downto(b)` with a literal block that itself passes the screen, and the loop
  emitter (`Jit/JitCompiler.Nest.cs`) writes the inner loop inside the outer one: bounds
  evaluated once per outer iteration in source order, Int32 bounds or a deopt, the
  inner block's parameter and locals as CLR locals, a safe point of its own, the
  library's own iterator checked when compiling and again (the method version) each
  time an outer iteration reaches it. NBody, five alternating processes per build:
  1447104 (1428887-1493588) -> 858503 (836480-876876) us, 0.593; in two runs of all
  fourteen with the columns both ways 0.577 and 0.600, and 0.587 and 0.605 on the
  merged tree; 1364 -> 602 MB per iteration (its author's figure). Its author's shapes,
  an `each_index` block over an inner `Range#each` or `times` of 1, 2, 4 and 100
  elements: 0.34, 0.24, 0.14 and 0.42 of before, so short inner runs gain most and
  nothing pays an entry. Richards and Sieve were 4-6% slower than the baseline in both
  of those runs and are 1.004 and 1.014 in six alternating processes, ranges
  overlapping; no JIT decision of any benchmark but NBody's changes.
  The review (23 probe files, 85 shapes of locals and shadowing among them) found two faults
  that need a second thread, both fixed by narrowing: a Thread#raise caught at the
  inner safe point restarts the outer iteration, whose locals are taken back while a
  global it stored is not, so an outer block that stores a global beside the in-place
  iterator is refused (16 to 21 of 30 runs showed it before); and an iterator
  redefined by another thread went on running in place, hence the per-iteration
  version check. It also found that eight of the author's matrix rows never ran the
  nested copy (their outer runs were under the block's floor of five); they do now.
- **perf/core 122581ca 079d660f 04c9d48e 4ce34feb 7a0a1948 (merged in 80813950) -
  `String#length`/`#size`, `Integer#abs`/`Float#abs` and `v == nil`/`v != nil` on an
  Array or a Hash as operations of a typed body** (gap 1; TruffleRuby's core methods
  as specialized nodes). Each used to decline the whole method. `Util/bench/jit-core-
  shapes.rb`, ab.rb with five rounds, both column orders, CRuby / before / after in ns:
  `str_size` 60.0 / 117.7 / 57.6 (0.49, 0.48), `str_length` 41.4 / 70.7 / 54.1 (0.77,
  0.75), `abs` 42.8 / 75.9 / 54.1 (0.71, 0.73), `fabs` 65.4 / 77.9 / 58.1 (0.75, 0.76),
  `vec_at` 107.8 / 114.8 / 68.4 (0.60, 0.61), `vec_capacity` 95.5 / 84.2 / 53.2 (0.63,
  0.62). It is kept for those: the fourteen benchmarks are 0.993 and 0.992 of the
  baseline with it alone (Json 0.975 and 0.953, and 86228 (84309-86342) against 87304
  (86731-88603) in six alternating processes: about 1%). Gap 1 says which methods it
  compiles and what declines the rest.
  The review (120 probe files) found `a != nil` answering true where `!=` had been made
  private or protected - the guard read the visibility of the method a forwarder
  resolves to, the fault of round eleven in a new place and the reason for the gate's
  fifth question; the fix reads the lookup's visibility in `IsPublic` and
  `IdentityCallSite.Resolves`, which corrects the same test on the String and identity
  comparisons master already compiled. And `v.abs` through an `attr_reader` named abs
  was no longer compiled; it is again.
- Both together against 45708767+ledger (c2747ab2): all fourteen 0.960, the thirteen in
  the other order 0.962; NBody 0.587 and 0.605; nothing else outside 5% in both. The
  micro table, two runs, both orders: `abs` 0.70 and 0.65, `rangeeach` 0.52 and 0.50,
  `blkattr` 0.78 and 0.80, no line slower by 5% in both. Start-up 1.50 and 1.49 s.
  The gate, on the tree with both: IronRuby.Tests with the one failure the baseline
  has; jit (1162 lines with the new sections 13-16), block-loop (193), jit-calls, ivar,
  osr-array, hash-struct, block-given, light-rest, hot-shapes, the two
  block-scope-reuse matrices and the inline matrix the same as the baseline run on the
  new matrix files, and CRuby's where the baseline's is (raise and inline differ from
  CRuby in two lines each on both); all five ruby/spec suites, 32,285 examples, with
  nothing the list of known failures lacks; and the reviewers' 143 probes run again on
  CRuby, the baseline and the merged tree: 124 the same on all three, one that the
  baseline fails and the merged tree passes (a private `!=`), 18 that differ from CRuby
  on both, of which two are worse on the merged tree - see "Caught by the review".

Round fifteen: two experiments on 87bb0b5f, side by side, each by a subagent in its own
worktree; the larger one then went to an independent reviewer, and back to its author
once. Measured by me on 2026-11-17 on a quiet machine (load 1.0-1.2), gated on the tree
with both merged (perf/safept with perf/core2 merged into it, d338750c).

- **perf/core2 d03fe1e7 (merged in 29aed06d) - a constant whose value is nil in the
  method JIT** (gaps 1 and 11). `ResolveConstant` declined every constant without a
  value because an autoload that has not run has none either; the constant's storage
  tells them apart (`ConstantSiteCache.WeakNull`). A nil constant is now a value like
  another under the constant version guard, `NIL_CONST == x` and `!= x` are `x is nil`
  while `NilClass#==` and `BasicObject#!=` are the public library methods and no
  refinement is active, and a constant receiver's observed type is its value's type.
  `Util/bench/jit-core-shapes.rb` `nil_const`, ab.rb with five rounds, CRuby / before /
  after in ns: 33.3 / 62.3 / 39.4 and 33.9 / 60.5 / 39.7 with the columns reversed
  (0.63, 0.66), the other six shapes overlapping. Kept for that shape: all fourteen
  benchmarks are 0.972 and 0.999 of the baseline with it alone, Richards 1.003 and
  1.026 - it compiles two more `initialize` there and nothing hot (gap 1). My probe of
  139 lines (nil constants compared, tested, assigned, used as receivers; constants
  holding an Array, a String, numbers, a Hash, a Struct and an object with readers; the
  constant reassigned to another type and to nil, `NilClass#==` redefined, removed and
  made private, `BasicObject#!=` redefined, singleton methods on the constant's object)
  prints the same on CRuby, the baseline, the build and the build with the tiers off.
  One thing went wrong on the way: the Release console build in the worktree was older
  than the last commit and still held the two steps its author had dropped (the DLL
  was 1.5 KB larger than one built from the commit), so my first measurement and probe
  were of something else. Both were repeated on a build made from the commit.
- **perf/safept 3e8a7bc3 3a52d160 59828ec0 826c652c 2b7faaf5 a8d443c8 (merged in
  ea86fa35) - a compiled loop that is running sees a method another thread redefines**
  (the first open item of "Caught by the review (round fourteen)"; TruffleRuby's
  safepoints, which see an interrupt and an invalidation with one check). The count of
  waiting safe-point work became the high half of a 64-bit word whose low half is a
  code epoch; a compiled loop compares the whole word with the epoch it was compiled
  at, an immediate, where it compared the count with zero - still one compare per
  iteration (`cmp qword [word], imm32`; an epoch kept in a register local made `each`
  1.13-1.34, one in the high half made `times` 1.14, so the layout is the result of
  measuring). The epoch moves when the global method version moved in an update, and
  when an existing constant is assigned again or removed. An outlined loop, a block
  loop and an in-place iterator hand themselves back by a throw (a slow path that
  returned made the CLR JIT spill the loop counter: `each` 15-30% slower), and
  `OsrLoopSite.Run` builds a copy under the current versions without counting a failed
  specialization - the entry guard's version failure no longer uses up one of three
  retypes either - no sooner than eight times the last compilation's time after it. A
  typed method body's loop takes a slow path (`JitRuntime.MethodLoopSafePointSlow`)
  that runs what is waiting, checks the versions, and deopts when the body may.
  What it fixes, on rows that run under CRuby, the build and the build with the tiers
  off (`Util/osr-array-matrix.rb` section 10, the last section of
  `Util/block-loop-matrix.rb`, `Util/jit-matrix.rb` section 17; the other thread
  redefines and then sets a flag, and the loop counts from the iteration after it saw
  the flag): Array#size, String#length, Integer#abs, Integer#+ and a folded constant in
  an outlined loop, by `def`, alias, `define_method` and `prepend`, from a trap handler
  and from a rescue of Thread#raise; `times`, `each` and nested `times`; a typed body
  with a core operation, an inlined callee and a bound callee. The baseline gets eight
  of the outlined rows, four of the typed-body rows and two of the block rows wrong.
  What it costs, against the unchanged build: the lightest loops (`r15` shapes: empty
  `while`, nested `while`, `times`, `each`, nested `times`), ab.rb with seven rounds,
  0.98 / 0.95 / 0.96 / 1.03 / 0.89 and with the columns reversed 1.00 / 1.04 / 1.01 /
  1.14 / 0.99, every row overlapping; a typed body with a short loop is faster, its safe
  point being an inline compare where it was a call (micro `while10` 40.0 -> 31.7 and
  42.5 -> 33.4 ns, 0.79 in both orders); the micro table has no other line outside 5%
  in both orders but `s_each2` (1.06, 1.06), which is 180.2 against 179.7 ns in a
  process of its own, as are `s_each1`, `s_each4`, `s_times4`, `s_map1`, `empty` and
  `poly2` (0.97-1.02, all overlapping); the benchmarks are under "Current numbers";
  start-up 1.55 and 1.47 s.
  What is still exposed: a typed body with an effect and a loop that holds nothing
  that can deopt (an instance variable store and Float arithmetic) keeps the old method
  until it returns; a redefinition that lands between an iteration's check and its use
  is seen one iteration late; the method version is global, so a redefinition anywhere
  hands back every running loop (10,000 of them beside one loop: 127-136 rebuilds,
  about 11% of the loop's time compiling, 8.1 s against 7.5 s).
- The gate, on the tree with both: IronRuby.Tests with the one failure the baseline
  has (the two branches alone and together); jit (1214 lines with sections 17 and 18),
  osr-array, block-loop, jit-calls, hot-shapes, ivar, hash-struct, block-given and
  light-rest as CRuby's, with the tiers off and with `-X:NoJITCalls` too (int and raise
  differ from CRuby in the lines the baseline differs in); the reviewer's probes again
  on the final build; all five ruby/spec suites, 32,285 examples, nothing the list of
  known failures lacks; no FAIL in two runs of all fourteen.

Round sixteen: three experiments on cd0d906c, each by a subagent in its own worktree,
beside one another (so the authors' timings are at loads of 4 to 14, and mine for the
first were taken while the third was still running). `havlak` ended as a breakdown (gap
25, and `Kernel#loop` in C# under Rejected).

- **perf/extend f4b38652 d5685f40 (merged in 8e844238) - a module's dependent classes
  are looked up in constant time** (gap 19). `extend` and `include` add the class to
  the module's weak list of dependents after searching the list for it, and the list
  kept every singleton class ever made for the module; `WeakList` now has a set beside
  the list, `Contains` replaces the two searches, and Add prunes dead entries each time
  the list has doubled. Three alternating runs of 20,000, before -> after in ms, load
  5-7: `Object.new.extend(M)` with a call 5180 / 5352 / 4847 -> 527 / 491 / 451 (0.09),
  `Class.new.include(M)` 5218 / 6032 / 5749 -> 947 / 1010 / 991 (0.17); `Class.new` with
  a `def` 2336-2767 -> 2300-2344, a singleton `def`, `define_method` and `const_set`
  level, `Struct.new` 6.8-12.3 s against 7.2-9.8 s. Its author's: 200,000 extends 72 ->
  2.2 s, RSS flat at 269-279 MB over 2,000,000. My probe of 60,000 extends among
  classes, subclasses and modules that include M, followed by every kind of change to
  M, prints CRuby's output on both trees and takes 4.4 s where it took 41 (CRuby 0.17).
  The second commit is mine: an enumeration of the list goes by index, and the walks
  of dependents add to other modules' lists as they go, so a prune from Add under an
  enumeration of the same list would make the walk skip classes. Reading the walks
  found no case that does it; the list now counts its enumerations and prunes at none.
  The gate: IronRuby.Tests with the baseline's one failure; `Util/extend-matrix.rb`
  (new, 14 rows) and the hot-shapes, jit, jit-calls, ivar, hash-struct, block-loop and
  osr-array matrices as CRuby's, tiers on and off; all five ruby/spec suites, 32,285
  examples, nothing new; the fourteen benchmarks 1.043 and 1.005 of the baseline at
  load 3-9 (Storage 1.36 and CD 1.21 in the first run, 0.99 and 0.98 in the second),
  and what was slower in both orders level in alternating processes: Queens 1.014,
  Towers 1.002, Richards 0.915, micro `getter` 1.03 and `==` 1.01 overlapping;
  start-up 1510 against 1520 ms, best of fifteen.
- **perf/unboxed 1d918afe .. 0a8c380d (merged in a7060a27) - a Float instance variable
  that typed code updates is stored raw** (gap 2; `Util/bench/daloze-lessons.md`, idea 1).
  Every Float written to `@x` was a box, and 450 of NBody's 602 MB per iteration were
  those (75 stores per `advance`, 24 bytes each; measured by its author with the stores
  taken out). A variable that the method JIT or a loop tier overwrites with a double is
  now a `FloatLocation` in the object's slot array, and the update writes into it.
  Rawness belongs to the shape: every shape has a raw twin, a location is only ever in
  an array whose element 0 is a raw shape, and an object becomes raw by a Move under
  the lock (`RubyInstanceData.TryPromote`), so nothing that loaded a plain shape sees a
  location and no read takes a lock. A typed store promotes only when it overwrites a
  slot that already holds a Float; the first promotion marks the class
  (`RubyClass.FloatsRaw`), whose later instances are born raw. A generic read of a raw
  object is a call out of line, where a Float is boxed (the same box again while the
  value is unchanged, so `@x.equal?(@x)` and `object_id` say what they said); the
  inlined cached read is the unchanged build's. Beside it: a Float's `object_id` is
  CRuby's flonum id where CRuby has a flonum (`1.5.object_id == 1.5.object_id` was
  false).
  Against c791ce6e, all fourteen in both column orders, load 1.0-1.5: NBody 709.8 ->
  542.6 and 724.3 -> 559.3 ms (0.764, 0.772), 0.804 in six alternating processes
  (718.7 -> 578.1), 602 -> 152 MB per iteration; geomean of the fourteen 0.980 and
  0.986; nothing else allocates differently. CD was 1.075 and 1.072 in the two tables
  and 1.004 in alternating processes (478.5 against 480.5 ms, ranges 462-487 and
  459-500); there Richards 0.945, Sieve 0.983, DeltaBlue 0.998, Json 1.001, Bounce
  1.005. micro.rb: `blkattr` (a Float and an Integer attribute updated in a block)
  0.89; `treefind` 1.05 and 1.10 in the tables and 1.04 with overlapping ranges in
  eight alternating rounds, `short4` 1.05 and 1.06 and 1.05 overlapping: both are
  probably 4% dearer and neither is shown to be more. `ivar-float-shapes.rb` (new):
  `attr_f` (`b.vx -= d * c.m` on Floats) 44.2 -> 32.9 ns, 0.74 and 0.78; `read_o`, an
  object attribute of a born-raw object read generically, 1.17 and 1.18. Start-up 1416
  against 1418 ms, best of twelve.
  What it costs, and where: a generic read of an object whose class has raw Floats is
  about 3 ns dearer, its Integer and object variables included (`read_i` after
  `attr_f`: 8.6 -> 14.3 ns for two reads in a typed loop); a class once marked stays
  marked; raw twins count against the process-wide limit on shapes; a location is 32
  bytes where a box was 24, and a raw variable that is also read generically keeps its
  box beside it (56 bytes); a generic `@x` site that sees plain and raw objects of one
  class in turn swaps its entry.
  The gate, on 0a8c380d: IronRuby.Tests with the baseline's one failure; ivar (267
  lines), jit (1224), block-loop, osr-array, hot-shapes, jit-calls and hash-struct as
  CRuby's with the tiers on, off and with `-X:NoJITCalls` (int differs in the
  baseline's 16 lines); `Util/ivar-stress.rb`, which now flips Floats between raw and
  boxed and moves them between threads, five times; all five ruby/spec suites, 32,285
  examples, nothing new; no FAIL in two runs of the fourteen. Two reviews by
  subagents that had not seen the code (eleven probe files, threads among them) found
  no wrong result in the final design; one difference from CRuby in the first
  (`o.set(3.0).object_id == o.x.object_id`) is gone, and two rows the baseline gets
  wrong are right. Not probed, read only: a rollback after a promotion in the same loop
  iteration (the loop was not specialized in the probe written for it), and several
  runtimes in one process (the shape trees are static and shared, as before;
  `FloatsRaw` is per class).
  It took four acceptance runs; see "Caught by the measurement (round sixteen)".

Round seventeen: one experiment on 67313c86, by a subagent in its own worktree, alone on
the machine; two independent reviews; three acceptance runs.

- **perf/jread 67d633be .. e321b21d (merged in d064caf6) - a typed body holds its writes
  to self's instance variables back to its end** (gap 1). Json's `Parser#read` stores
  `@line`, `@column` and `@index` and then calls `@input.length` and `@input[@index]`, and
  the method JIT declines a body in which something that can deopt follows an effect;
  `read_char`, `read_digit` and `skip_white_space` only waited for it ("waiting for a
  callee"). The ledger's two ideas (stage 1's fallback, the late guards hoisted) were
  not what was built: a body whose only effects are writes to self's instance variables
  keeps them in locals (a value and a `written` flag each) and stores them where it
  ends (`JitCompiler.Sink.cs`), so every deopt comes before every store and a restart
  finds nothing done. Between the writes it may hold only what an allow-list checked on
  the emitted tree admits (helpers that run no Ruby code: guards, checked Integer
  arithmetic, the String operations, `GetIVar`); anything else - a call, an invoked
  callee, a try, a jump, a fallback, another effect - compiles the body again as before.
  A read of a held variable is answered from the local; a frozen self deopts at the
  first store; a body whose value is its last held write answers the box it stores; new
  variables are created in the order they were first assigned at run time (source
  order where every write of each variable precedes every write of the next, a counter
  per variable where not). Not tried for a body with a `while` (its safe point) or with
  keyword or optional parameters. Beside it, `String#[]` with an Integer and `==`/`!=`
  between two Strings are typed operations (round fifteen had built and dropped both:
  they bought nothing while `read` still declined).
  The JIT on Json: `read`, `read_char` and `read_digit` specialized (the two callers with
  `read` bound as a direct call), 8 -> 11 bodies; `skip_white_space` still declined ("a
  loop in a body that can deopt and has an effect"), `read_string_internal` at
  `pause_capture`.
  Against 67313c86, all fourteen in both column orders, three acceptance runs at load
  1.0-1.2: Json 0.776 and 0.795, 0.787 and 0.822, 0.838 and 0.790; in six alternating
  processes 0.809, 0.818 and 0.816 (84.8 -> 69.3 ms); 30.5 -> 28.5 MB per iteration.
  Geomean of the fourteen 0.991 / 0.989, 0.967 / 0.982, 0.995 / 0.971. Nothing more
  than 5% slower in both orders in any run; in alternating processes Bounce 0.984-1.016,
  CD 0.972-0.983, Richards 1.004-1.021, DeltaBlue 1.004-1.005, Towers 0.966-1.007.
  `Util/bench/jit-scan-shapes.rb` (new), ns, CRuby / before / after: `read` 205 / 161 /
  96 (0.60), a scanner loop over `read_char`, `read_digit` and `skip_white_space` 1432 /
  788 / 567 (0.72), `read` with `@input` swapped between two Strings 218 / 174 / 109
  (0.63), `read` on a String subclass for good (deopts, retired) 215 / 162 / 165,
  overlapping. Bodies that only write (my `writes.rb`, one shape per process, six
  rounds): two writes after a read 71.6 -> 62.5 (0.87), `@x += @vx; @y += @vy` 75.4 ->
  60.1 (0.80), writes in both arms of an `if` 67.0 -> 69.6 and 67.5 -> 68.9
  (overlapping; a reviewer's top-level loop over the same body read 5-11% slower in two
  rounds of eleven), one or two unconditional writes level (compiled before as well).
  micro.rb: seven small lines were 5-10% slower in both orders of the second run's
  table (`arr`, `nil?`, `==`, `cmp`, `selfcall`, `veach2`, `s_map1`) and five of them,
  each in a process of its own, 0.99-1.01 with overlapping ranges: the table's state
  again (perf-gate, step 4). Start-up on the ReadyToRun host, five commands, ten runs
  each, three times: within 4% everywhere (`-e 1` 480 / 463, `gem --version` 610 / 603,
  `gem list` 1231 / 1272, `require "irb"` 1159 / 1137, an irb session 1735 / 1781 ms in
  the last); the first attempt's second compile shows on Json only (compile= 60 -> 112
  ms, three more bodies and their callers), not on `gem list` or irb (no body there
  holds a write and then fails to keep it).
  The gate, on e321b21d: IronRuby.Tests with the baseline's one failure;
  `Util/jit-scan-matrix.rb` (new, 78 lines: what `@input`, `@index` and `@current` can be,
  the parser frozen or with singleton methods, the String and Integer methods redefined,
  made private and refined, the callers, a second thread, the order of new variables,
  the forms the allow-list does not see, a variable's type changing between calls,
  keyword and optional parameters) and the jit, jit-calls, hot-shapes, ivar, block-loop,
  osr-array and hash-struct matrices as CRuby's with the tiers on, off and with
  `-X:NoJITCalls`; ivar-stress three times; all five ruby/spec suites, nothing new; no
  FAIL in six runs of the fourteen.
  What rests on reading only: that no admitted helper throws anything but a deopt, and
  that a Thread#raise cannot land inside a body with held writes (no call and no loop in
  it; a probe of 300 raises under `handle_interrupt` and one of 50 raises and 50 kills
  found no state the source cannot leave, on either build). Known and not new: a second
  thread that freezes self between two of the final stores leaves the first one made,
  as two consecutive writes of a compiled body could before.
  See "Caught by the review (round seventeen)" for what the two reviews found.

Round eighteen: one experiment on ef5f0197, by a subagent in its own worktree, alone on
the machine; one independent review; one acceptance run.

- **perf/bounce 8d092e71 011e476b c3cb833f (merged in 981d2704) - a held-back instance
  variable may be written as an Int and as a Long** (gap 1). `Ball#bounce`, which is the
  Bounce benchmark, was the first decline left after round seventeen: `@x += @x_vel` is a
  Long sum and `@x = x_limit` an Int, a held variable took the type of its first write
  and refused a second, the refused write was stored where it stood, and that store was
  the effect ("an effect before a held-back write"; the verbose line showed the last
  obstacle, not the first, which is now kept). Such a variable is held in one long:
  when a round meets the two types, `TryCompile` runs another in which the Int writes of
  that name are longs (`[jit] held as a long in bounce: @x, written as an Int and as a
  Lng`), and the store goes through `NarrowLong`, which makes the box the generic body
  makes (the cached Int32 box for a value that fits, a boxed long otherwise). Int or
  Long with a Float, nil or an object stays refused: one typed local cannot say which
  class the variable holds. `NarrowLong`, `BooleanToObject` and `RubyOps.IsTrue` are
  new on the allow-list, by method (bounce answers a Boolean local).
  Against ef5f0197, all fourteen in both column orders, load 1.0-1.2: Bounce 90.7 -> 51.5
  and 87.0 -> 48.7 ms (0.568, 0.560), 0.566 in six alternating processes (91.0 -> 51.5),
  11.0 -> 7.3 MB per iteration; geomean of the fourteen 0.962 and 0.947; in alternating
  processes Json 0.956, Towers 0.976, Sieve 0.998, Richards 1.004, CD 1.008.
  `Util/bench/jit-bounce-shapes.rb` (new), ns, CRuby / before / after: `bounce` 89 / 124 /
  88 (0.71, 0.69 the other way round), one variable in two types 43 / 53 / 46 (0.87,
  0.82), with the narrowing 36 / 51 / 40 (0.80, 0.75); the write-only bodies of round
  seventeen level. micro.rb: `ivar_rw` 59.3 -> 41.7 and 57.5 -> 40.0 ns (it writes `@x`
  as a sum and as 0: 0.70 in both orders); `s_map1` and `s_map4` 5-8% slower in both
  orders of the table and 1.02 and 1.03 with overlapping ranges each in a process of
  its own (the table's state again: an earlier line compiles now). Start-up on the
  ReadyToRun host, five commands, ten runs each: level (`-e 1` 469 / 470, `gem
  --version` 583 / 598, `gem list` 1287 / 1285, `require "irb"` 1195 / 1179, an irb
  session 1776 / 1778 ms); no body of `gem list` or irb is widened or loses its held
  writes.
  The JIT on the fourteen: only Bounce's decisions change (`bounce` specialized). What
  it costs: a compile round per widened variable, not one in all (the first refused
  write makes the body impure and hides the later conflicts; `bounce` takes two, Bounce's
  compile= 23 -> 45 ms), and a wasted round where the body cannot hold its writes for
  another reason. Collecting every conflict in one round is a way on, for compile time
  only.
  The gate, on 011e476b: IronRuby.Tests with the baseline's one failure; the jit,
  jit-calls, hot-shapes, ivar, osr-array and hash-struct matrices as CRuby's with the
  tiers on, off and with `-X:NoJITCalls`; `jit-scan-matrix.rb` (section 5, 103 lines
  now: the box at 2**30, 2**31, 2**62, 2**63 and one step round each - class, `equal?`,
  `object_id`, `eql?`, `hash`, Marshal - the method's value, the order of new variables,
  a frozen ball, a velocity that turns Float, Bignum or nil, Integer's methods redefined
  and refined, a second thread) the same four ways once its thread row stopped printing
  how many reads it got in (c3cb833f); ivar-stress three times; all five ruby/spec
  suites, nothing new; no FAIL in two runs of the fourteen. One review by a subagent
  that had not seen the code, eight probe files after the acceptance run: no wrong
  result, no method specialized before and declined now (a widened read that then needs
  an Int - an Array index, a bound callee's argument - brings in a helper that is not
  admitted, and the body compiles as before); it found the comment on the rounds false,
  which c3cb833f corrects. Resting on reading only: that the three helpers run no Ruby
  code and throw nothing. Not reached: a Thread#raise landing inside the hot loop
  itself (its probe delivers between batches of calls).
  Seen once and not from this change: in the acceptance run, `block-loop-matrix.rb` with
  `-X:NoJIT -X:NoOSR` printed two backtraces of its nested-block rows without their
  `Range#each` frame (and with one frame more at the end); four reruns on both builds
  had the frame, and the run with the tiers off does not use the compiler this change
  is in. Under "Regressions", to check.

Round nineteen: one experiment on d9fd2817, by a subagent in its own worktree, alone on
the machine; one independent review; one acceptance run. Chosen from profiles and not
from the gap's list (see gap 1).

- **perf/raise 0f4f0cfd 51646893 (merged in 3081699d) - a `raise` in a typed body is a
  deopt** (gap 1). The screen took `raise 'x'` for a call of a method on self that is no
  inlinable leaf and refused the whole method ("MethodCall raise (self)"), and every
  caller that would have bound it with it. Now a call of `raise` or `fail` with no
  receiver and no literal block passes the screen with its arguments unwalked and is
  emitted as `JitRuntime.RaiseDeopt()`: the typed body throws its deopt, the generic
  body runs again from the top and raises - class, message, backtrace, `cause`, `$!` and
  the TracePoint events are the generic body's, because it does all of it. The existing
  rule says where: after held-back writes of self's instance variables it is allowed
  (the generic body makes the stores and then raises), after any other effect the method
  is declined as before. Declined too: a body in which every path raises, a `raise` in
  an outlined loop, and a self whose `raise`/`fail` resolves (singleton class and the
  definition site's refinements included) to anything but Kernel's - which is about
  cost, not soundness, since a deopt runs the source whatever `raise` is.
  The second commit is what makes it safe to take: a body that deopted on every call
  was never retired, because the retire rule compares the typed body with the cost of
  generic calls that returned, and none did (a lookup that always raises: 12.2 us per
  call against the baseline's 7.6). A generic call that raises is timed now, by an
  exception filter that never catches, and those times count while no call has ever
  returned; the body retires after one window and backs off as any other.
  Against d9fd2817, `Util/bench/jit-raise-shapes.rb` (new) in both orders, ns, CRuby /
  before / after: a caller that binds a lookup with a raise 60 / 86 / 58 (0.67, 0.67),
  an argument check before arithmetic 42 / 74 / 49 (0.66, 0.68), the lookup itself 38 /
  62 / 55 (0.89, 0.85). Raising on every call 6.9 us, on every second call 3.5 us and in
  bursts: 0.99, 1.01 and 1.02 one way and 1.01, 0.99 and 0.97 the other, ten alternating
  rounds each, every range overlapping; one in a thousand 1.05 and 0.97, overlapping.
  (The reviewer's own script read the raising shapes 1-8% slower at the median in five
  runs with overlapping spreads; ten rounds each way do not show it.) CRuby raises in
  0.7 us: an exception here costs ten times CRuby's, before and after.
  No benchmark moves: the fourteen 0.991 and 0.980; in six alternating processes
  Richards 0.996, Json 1.017, DeltaBlue 1.020, Towers 1.007, Bounce 0.972, CD 0.990.
  What compiles now and where the callers stop: Richards' `find_task` is specialized,
  `release` binds it and declines at "attribute assignment task_holding= on an
  unprofiled receiver", `queue_packet` is refused for its call of
  `add_input_and_check_priority` on another receiver; Json's `read_required_char`
  declines at "a deopt after an effect (call raise)" because the `read_char` it binds
  writes through a direct call, which cannot be held back, and `read_true`,
  `read_false`, `read_null` are refused for a String literal; DeltaBlue's `satisfy` is
  refused for `inputs_do` on self; Towers' `pop_disk_from`, `push_disk`, `move_top_disk`
  and `move_disks` pass the screen now and are declined behind it. Start-up on the
  ReadyToRun host, five commands, ten runs each: `-e 1` 469 / 466, `gem --version` 579 /
  604 (623 / 635 at the median), `gem list` 1244 / 1207, `require "irb"` 1173 / 1151, an
  irb session 1759 / 1743 ms; the reviewer counted what the JIT does there on both
  builds and it is the same (`gem list` 10 specialized, 5 rejected; irb 6 and 5).
  The gate, on 51646893: IronRuby.Tests with the baseline's one failure;
  `Util/jit-raise-matrix.rb` (new: what is raised, in eleven forms; arguments with
  effects; the raise under every kind of condition; after held-back writes and after an
  effect; `ensure`, `rescue`, `retry` and `catch` in the caller; five kinds of a `raise`
  that is not Kernel's; TracePoint; a raise in a thread; a bound caller's two frames)
  and the raise, full-message, jit-scan, jit, jit-calls, hot-shapes, ivar, block-loop,
  osr-array and hash-struct matrices the same on both builds with the tiers on, off and
  with `-X:NoJITCalls`, CRuby's but for an address in raise-matrix and int's 16 lines;
  ivar-stress three times; all five ruby/spec suites, nothing new; no FAIL in two runs
  of the fourteen. One review by a subagent that had not seen the code, eleven probe
  files: the exception through every way of calling the method (`send`, `Method#call`,
  a Proc, `define_method`, `super`, an alias, a bound caller), no frame of the method
  twice and no stub frame; effects the rerun could make twice (a redefined `==`,
  `method_missing`, `coerce`, a write inside the raise's arguments) counted once;
  TracePoint's `:call`, `:line`, `:raise` and `:c_call` counts as CRuby's whether the
  trace was enabled before or after the method was hot; `throw`, `Thread#kill`,
  `Thread#raise` and `exit` through the new filter. No wrong result, no method
  specialized before and declined now. Not reached: `ObjectSpace` counts, a finalizer,
  `Thread#backtrace` from another thread during the rerun.
  A cost that is new and small: a method declined while `Kernel#raise` was wrapped stays
  declined after it is put back.

Rounds twenty and twenty-one. Round twenty measured the collector's settings and built
nothing (see Rejected). Round twenty-one: one experiment on fd9a8c27, by a subagent in its
own worktree, sent back once; three independent reviews; two acceptance runs, the second
after master (#228 to #233) had been merged into both trees.

- **perf/park 271c3730 ac8a79cd 55485f92 (merged in 2baa0099) - a method keeps its scope
  and the Procs of its literal blocks for its next call** (gap 13; the idea of the
  rejected perf/lfb of round six, on the scope a light frame makes on demand). A method
  that holds a literal block made a scope, a Proc and a BlockParam on every call: 288 B,
  measured part by part (som.rb's `Vector#each` 347 B a call, of which the Range is 40;
  `@storage.each { }` 307; a light caller with `{ |x| s += x }` 339). A body gets a
  slot (`ParkedScopeSlot`, a constant of the compiled body) when every literal in it can
  reuse its Proc and it has no `super`, no `&block` parameter, no refinements, is not
  eval'd, and none of its literals goes to a core iterator measured never to give the
  Proc back (`MethodDefinition.IsNeverParkedIterator`, by name: `new`, `upto`, `downto`,
  `any?`, `inject`, `find`, `sort_by` and others). At the normal end of a call, if
  nothing captured the scope and every Proc made over it is reusable and inactive, the
  scope and the Procs are cleared of the call - locals, self and its singleton class,
  the block, `$~`, the BlockParam's target frame - and published together as one
  `ParkedProcs` object; the next call takes that object with one exchange and looks at
  it only afterwards. A call that finds the slot empty (a recursion, a second thread),
  or was left by an exception, a `throw` or a kill, runs the unchanged path and
  allocates what the unchanged build allocates, to the byte; so does every body with
  `IR_PARKED_SCOPES=0`. No class of the unchanged build grew.
  Against 74c188e5 (this branch with master), load 1.0-1.3 on a host whose cells move by
  a tenth, so by alternating rounds: `micro.rb`, eight rounds, both orders - `s_each1`
  146 -> 103 ns (0.71, 0.74), `s_times1` 0.72 and 0.74, `s_map1` 0.75 and 0.75, `s_each4`
  0.82 and 0.83, `veach1` 0.83 and 0.81, `each4` 0.84 and 0.88 overlapping; `s_any1`,
  `blockeach`, `yield` level. Bytes per iteration (`alloc.rb`, all fourteen): DeltaBlue
  24.7 -> 15.9 MB, Queens 41.3 -> 30.5, Havlak 2557 -> 1908; none above the baseline.
  Eight alternating processes: Havlak 0.953 (3302 ms, 3162-3500, against 3148,
  2976-3211), DeltaBlue 0.964, CD 0.963, Permute 0.976, Towers 0.982, Storage 0.994, Json
  0.998, Bounce 1.000, Richards 1.021, Queens 1.032 (81.5, 76.6-84.2, against 84.1,
  80.8-87.0; it was 1.14 and 1.11 in the two tables), every pair of ranges overlapping.
  Start-up on the ReadyToRun host, five commands, ten runs each: within 3% at best and
  at the median. Three micro lines without a block in them (`arr`, `str==`, `poly2`)
  were slower in both orders of the table; each in a process of its own 1.01, 1.01
  (twelve rounds, with the switch off as a third column: 41.4 / 43.6 / 41.8 ns) and 1.00.
  **What it did not do** is what it was built for: Havlak loses a quarter of its bytes
  and none of its time that this host can show. The upper bound its author measured
  first - som.rb's `each` and `has_some` written as `while` loops: Havlak 0.79 of its
  time, 1850 MB - is the block invocation per element that the rewrite removes, not the
  bytes per call. So gap 25's next step is the element, not the call: gap 14.
  The gate, on 8eb61cda: IronRuby.Tests with the baseline's one failure;
  `Util/parked-scope-matrix.rb` (new) with block-pass, splat-pass, the three
  block-scope-reuse files, block-given, light-rest, block-loop, hot-shapes, jit,
  jit-calls, jit-scan, jit-raise, ivar, osr-array, hash-struct, raise and full-message
  as CRuby's or the baseline's with the tiers on, off, with `-X:NoJITCalls`, with
  `IR_PARKED_SCOPES=0` and with `IR_LIGHT_FRAMES=0`, the three with thread rows five
  times more; ivar-stress three times; all five ruby/spec suites, nothing new; no FAIL.
  Three reviews by subagents that had not seen the code, some fifty probe files. Of the
  merged version: 80 runs of 2, 4 and 8 threads in one body (each element checking that
  self, locals, block locals and the caller's block are its thread's own), with a
  thread recursing through the body, raising out of it, and raising and killing into
  the others, light frames on and off, tiers off - no wrong value, exception or hang;
  WeakRefs to fifteen things a call touches, all freed; Procs kept by a callee keep
  their self and locals; a second runtime in the process; a method redefined under
  four threads; fibers suspended in the block and enumerators driven by `next`. Read,
  not run: the exchange protocol line by line. Not possible: counting Procs through
  `ObjectSpace` (unsupported on both builds).
  See "Caught by the review and by the measurement (round twenty-one)".

## Rejected

- **perf/evalc da03f675 and its second pass (round thirty-one, 2026-11-22) - a cache of the
  compiled code of eval'd strings** (gap 31). Merged, then taken out (f133bcfa) after an
  independent review; the second pass that fixed the review's points was rejected too.
  - The cache: per RubyContext, 256 entries least recently used first, under a lock; the key
    the bytes and encoding, the file (or the default name) and line, module eval or not,
    `$VERBOSE`, the frame label and block depth, the method's name and parameters, the
    visible locals and the modules constants are looked up in; not kept for texts over
    64 KB, keyword frames, isolated procs, Coverage, or a compile that warned.
  - Timing (`ab.rb`, ns, unchanged -> cached; CRuby): `eval('1 + 1')` 63,202 -> 22,457
    (4,182), `eval` with a binding 34,003 -> 18,474, `instance_eval` 34,010 -> 18,284, an ERB
    template's `result` 77,228 -> 7,492 (18,151), `erb_result` of `app-shapes.rb` 0.41. A hit
    with a file costs 1.05 us; one without a file 33, nearly all of it the stack walk that
    names `(eval at f:l)` (`TryGetCurrentSourceLocation` builds a whole CLR `StackTrace`).
  - **Why not kept: compiled code is not only code.** It keeps per-site state in boxes of its
    own, and a second eval of the same text must get fresh ones. The review found a `/.../o`
    regexp evaluated once per entry instead of once per eval (`eval('/#{x}/o')` for three
    `x`: `/v0/` three times), an `END {}` registered once, regexp and range literals the same
    object across evals, an entry whose module was collected never evicted (2,048 entries
    in an LRU of 256 after 20,000 `Object.new.instance_eval`), and a miss 1.25-1.4 times an
    uncached eval. The second pass fixed all of those for the text's own body, and the
    same faults remained one level down: a `def` or a block in the text is compiled at its
    first call, after the cache has looked, so `eval("def evo(x) = /\#{x}/o")` three times
    still gave `/v0/` for all three methods, and a literal in such a method was one object
    for both evals (`Util/eval-matrix.rb` row 24 now).
  - Learned: (1) the 250 us of an eval are parse 20, load 10, bridge 23, transformation 60,
    light compile and file name the rest; (2) without a file, the stack walk alone bounds a
    cached eval at 33 us - the next attempt needs a cheap caller line first (`caller(0)` is
    15-20 us against CRuby's 2.9: the same walk); (3) a cache must either scan the whole
    tree, nested `def` and block bodies included, for anything that keeps state per site
    (`/o`, regexp, range and other folded literals, `END`, `BEGIN`, `__method__`-keyed
    caches, `defined?` caches) and refuse those texts, or cache the transformed tree and
    compile per eval - which keeps parse, load, bridge and transformation (113 of 250 us)
    and loses the rest; (4) eval'd code that runs hot reaches the IL compiler, which inlined
    a class body's lambda and lost `<class:Foo>` from backtraces (the unchanged build does
    the same under `-X:NoAdaptiveCompilation`).

- **perf/rminor's epoch move 413b629c (round thirty-one, 2026-11-22) - a new constant whose name
  some compiled code folded moves the code epoch.** Merged, taken back in aa7474c1. It fixed a
  real fault (gap 29: a running compiled loop does not see a nearer constant of the name it
  folded appear), by recording the names the JIT folds and treating a new constant of such a
  name, or a mixin bringing one, as a reassignment.
  - **Why not kept: it moved every guard for events that do not concern them.** An independent
    review: a loop reading `Cfg::LIMIT` beside a thread that defines `LIMIT` on fresh anonymous
    classes went from 0.26 s to 8.1 s, since a name cannot tell a constant that shadows the
    folded one from one in an unrelated class. And the qualifier of `A::B` was never recorded,
    so a new `B` nearer to `A`'s lookup was missed.
  - Learned: the invalidation has to know the lookup's path - the lexical scopes and the
    ancestors the folded constant was searched through - and test a new constant's owner
    against it; and every such change gets the probe of the gate's rule: the same event in an
    unrelated place, timed.

- **perf/dmeth (round thirty-one, 2026-11-22) - a `define_method` method called through a
  pooled BlockParam and a direct rule** (gap 23). Reverted by its author; only
  `Util/dmeth-matrix.rb` (CRuby's output on every build and mode) was kept. The diff is not in
  the repository.
  - Where the time goes (perf samples, 60 million calls of `define_method(:bm) { |x| x + 1 }`
    against `def bm(x) = x + 1`): mostly the method JIT, which compiles the `def` to an add and
    never takes a block body (`JitStub` and `JitCompiler` are built on `MethodDefinition`).
    The rest: the rule of `Proc.BuildCall` allocates a BlockParam per call and wraps the
    call in a try/filter/finally, `RubyOps.Yield1` checks the lambda's arity again (4.5% of
    samples) after the rule already did, a virtual `BlockDispatcher1.InvokeNoAutoSplat`, and
    write barriers (3.9%). The body is already scope-free.
  - The change: `RubyLambdaMethodInfo.TryBuildDirectCall` for 0-4 plain arguments calling a
    static `Invoke0`..`Invoke4` that does the rule's work in C# without the second arity check,
    and one pooled BlockParam per method, taken with `Interlocked.Exchange` and given back
    only after a normal return when it was not kept.
  - Timing (`ab.rb`, ns a call; CRuby, unchanged, changed): `bmethod` 29.2 / 63.5 / 53.1
    (1.20 times faster), `bmethod0` 24.7 / 61.6 / 52.9 (1.16); `forwardable` and `delegate` of
    `app-shapes.rb` unchanged (their methods take `*args, &block`, which the rule did not
    cover). Under the 1.5 the brief asked for.
  - Learned: without the JIT the define_method call is only about 1.15 times a `def`'s; what
    gap 23 measures is the JIT's, and the next attempt is a `JitStub` over a block body.

- **perf/jscreen 41ef00da (/perf-step, 2026-11-21) - the screen refuses `x`, a call on self with no
  arguments, unless x is a leaf, a bindable method, an attribute reader or undefined.** Not merged.
  The screen took every such call as a possible attr_reader read; a method that calls a Ruby
  method of its own that way is wrapped, profiled, and declined at the first compile
  (`rejected ... call: _Spacechar/0`), and keeps its stub.
  - Counted first, over sixteen traced runs (the fourteen AWFY benchmarks, RDoc::Markdown with
    `rationalize`, a load of eight libraries): 48 methods declined after the pre-screen. 35 for a
    call (17 of them a call on self with no arguments, the rest calls with arguments, calls on a
    local, `<<`, `downcase`), 6 for a deopt after an effect, 2 for `[]` on a RubyArray, 2 for an
    attribute assignment on an unprofiled receiver, 3 others.
  - With the change: declined 48 -> 31, specialized 168 -> 168 with the same names, the matrices
    and a probe of thirteen kinds of callee the same on both builds and on CRuby. Census: maybe
    2,923 -> 2,393, never 22,121 -> 22,651, all 530 from maybe to never.
  - **Why not kept: it buys nothing that can be measured.** Instructions a call of a Markdown
    rule 2,570 -> 2,611 (1.016, inside what the controls move by); the main thread's
    instructions per parse 49-52e6 on both builds; 1,500 parses 68.6 and 70.0 s against 68.6 and
    72.2 s. A declined method's stub has settled and costs nothing a call, which the jbreak
    step's measurement had already said. What the change risks is 530 verdicts decided by the
    declaring module at the first call (a subclass whose attr_reader shadows the method is then
    never tried), for no gain.
  - Learned: (1) Whole-process `perf stat` does not measure a JIT change here: about 60% of a
    Markdown run's instructions are libclrjit on thread-pool workers and vary by 15e9 between
    runs. Count the main thread, or use `ab.rb -i`. (2) Compiling these callers is stage 2 of
    `Docs/jit-resume-deopt.md` (a generic call in a typed body needs a light-scope site, a frame
    for the backtrace, the tracing hooks, and something to do about a deopt after it); the
    "tail mode" attempt above was as fast as the generic body. For the Markdown rules the time is
    in the generic callee anyway. (3) `census.rb -c` differs on a file that defines a method
    twice under a condition (`markdown.rb`'s `get_byte`): the census keeps the last def, the run
    the first. (4) `Util/perf/hot-shapes-matrix.rb` differs from CRuby on the baseline in three
    lines: Encoding::CompatibilityError where CRuby raises FrozenError. Not looked into.

- **`Kernel.raise(...)` and `::Kernel.raise(...)` as the deopt a bare `raise` is**
  (/perf-step, round twenty-four, 2026-11-19, perf/kraise, not merged; census only). The
  prelude writes its 322 raises with the receiver, the screen refuses that in 120 of its
  methods and in 26 as the only thing. Built in 27 lines (`IsRaiseCall` takes a
  `ConstantVariable` named `Kernel` without a qualifier; `EmitRaise` bails unless it is
  the Kernel module and its singleton `raise` the built-in one - for speed, the deopt
  being right either way). All six JIT matrices printed CRuby's output, the 18 probe
  methods compiled. Census: 54 methods `never` -> `maybe`, none to `compiles`, none down.
  A driver of the prelude methods that moved, called hot on the path that does not raise:
  specialized 11 -> 19, declined after the screen 6 -> 16. Eight gained (five of them
  `Prism::DSL` flag helpers), ten lost: `String#__ir_require_unfrozen__` and
  `Enumerator::Product#__check_frozen__` at `call: frozen?/0`, `IO#autoclose=` and
  `IO::PopenChild#pid` at `closed?/0`, `IO::Buffer#__check__` at `valid?/0`, `#free` at
  `locked?/0`, `StringIO#readchar` at `getc/0`, `IO::Buffer#__check_writable__` at
  `__check__/0`, two more at an operator. Not kept.
  It is round twenty-three's wall again, and it is one thing: **a call without arguments
  on self (or on a local) passes the screen on the hope of an attribute reader or a leaf,
  and where it is a method written in C# - `frozen?`, `closed?`, `valid?`, `nan?` - or a
  Ruby method that is neither, the compiler has no call to make and declines the body**.
  Whatever else is taught to the screen, the methods it then lets through meet that call.
  About twelve of the 54 also raise on every path (`allocate`, `marshal_dump`, the
  `Ruby::Box` stubs), which the compiler declines as "every path raises" - a bare `raise`
  has the same hole in the screen today. And `census.rb -c` differed from the run in four
  `Process` methods made with `module_function` (the census says `maybe`, the run's screen
  refuses their call on self of `__check__`): the census does not know the copy on the
  singleton class resolves the callee differently.
  What a decline costs was measured for this entry (`ab.rb`, eight alternating rounds, the
  method JIT on against `-X:NoJIT`, minima; another build was running, spreads 21-42%, so
  the sizes are rough): a method that passes the screen and is declined 1.07, 1.08 and
  1.00 of the same method never wrapped (40 -> 43 ns, 52 -> 56, 40 -> 40), a method that
  compiles 0.82 (46 -> 38), one the screen refuses 0.98. So a decline is three or four
  nanoseconds a call and a trivial body's typed version gains eight: ten declines for
  eight gains is a loss, and the rule that rejected rounds twenty-three and twenty-four
  is the right size.
  Two ways on, neither a census step: the typed body makes the call (a library method on
  self whose answer runs no Ruby code needs only the check that the name resolves to it,
  as `IdentityCallSite` does for `nil?`; `frozen?` is the one that came up four times), or
  a decline costs nothing (the stub taken out of the call sites' rules when it settles),
  after which a hopeful screen is free. The probes are kept (`Util/jit-raise-matrix.rb`,
  section 10; CRuby's output on the unchanged build, JIT on and off).

- **`v.is_a?(C)`, `kind_of?`, `instance_of?` and `C === v` as tests of a typed body**
  (/perf-step, round twenty-three, 2026-11-19, perf/isa, not merged; census only). The
  screen refuses 674 methods at `is_a?` (76 in the prelude), 166 at `===`, 91 at `kind_of?`
  and 14 at `instance_of?`, the second line of the census after `new`, and the parts were
  there: `EmitModuleTest` (`when C`) and `IdentityCallSite` (the run-time check that a name
  is the library's, as for `v.nil?`). Built in 141 lines: one shared predicate
  (`IsKindTest`), the fold for a typed receiver, the site for an object, a run-time module
  test with a deopt for `v.is_a?(klass)`. Every probe shape compiled and the five JIT
  matrices printed CRuby's output.
  Census: maybe 2,756 -> 2,962, never 22,630 -> 22,424, compiles unchanged - 206 methods
  from `never` to `maybe` (8 of the prelude's), none to `compiles`, none down; the screen
  alone gives the same numbers as the screen with the emitter. A driver that calls the
  moved prelude and library methods hot (same checksum on CRuby and both builds):
  specialized 6 -> 7 (`Kernel#Pathname`), declined after the screen 1 -> 5. That is the
  case the rule is for - a method wrapped, profiled and declined pays for its stub on
  every call - so it was not kept. AWFY's traces were unchanged (no benchmark calls these).
  What refuses the methods is not the test. Of the 945, 705 stay `never` at what comes
  next in the body: `new` 194, `===` on a receiver that is no constant 121 (`pattern ===
  v`), `respond_to?` 68, `all?` 20, `raise` in a form `IsRaiseCall` does not take 16; the
  prelude's argument checks are all among them (`__array_to_int__` and `rassoc` at
  `respond_to?`, `__flt__` and `__int__` at `raise`, `match?` at `new`, `Range#%` at
  `__send__`). The 206 that pass stop at a call on another object: the four new declines
  were `other.begin`, `value.call`, `other.path` (a call without arguments on a local,
  which the screen takes on the hope of an attribute reader) and `@data.equal?(source)` on
  a String. So a next attempt takes the kind test together with what stands behind it, in
  this order of count: `respond_to?` with a Symbol literal (the same site idea: the
  library `respond_to?` and no `respond_to_missing?`), the `raise` forms left, and calls
  on another receiver (gap 1) - or it is not worth a screen entry. The probes are kept
  (`Util/jit-matrix.rb`, section 7a, 40 rows that agree with CRuby on the unchanged
  build): receivers of every kind, an extended object, `BasicObject`, a class with its own
  `is_a?`, a private one, `Kernel#is_a?` redefined and put back, `Integer.include` after
  warm-up, `Const === v` for constants that hold no module, refinements.

- **`Enumerable#sum` asking `block_given?` once instead of per element** (/perf-core,
  2026-11-18, for `Hash#sum { }` at 3.5x CRuby): 1532 -> 1524 ns, overlapping. The block
  writes the method's locals (`acc`, `compensation`), so it makes its scope whatever it asks.
  What `Hash#sum { }` pays is the generic fold's per-element block; a Hash path of its own
  would need Hash#each's one-or-two-value yield semantics checked first.
- **A same-encoding fast path in `MutableString.Equals`** (gap: Json's `'0' == @current`
  chains, 6% of Json). Its share fell to 4.7%, which no timing could show. Reverted.
- **`[AggressiveInlining]` on `RubyOps.IsTrue`** removed its 0.9% from the profile with no
  measurable wall-clock gain. Not committed.

- **Light-scope sites through the precompiled dispatchers, LibraryDispatcher and shared
  rules** (round three; patch kept nowhere in the tree: add `HasLightScope` to the two
  flag masks in `RubyCallAction.BindPrecompiled`/`BindShared`, public-only visibility,
  decline overloads taking a RubyScope). Correct, all gates passed, but `CallSite.Target`
  20.9% -> 11.4% just moved into the attr and method dispatchers (6.9% + 3.8%); no
  benchmark faster in all of five runs. Reverted.
- **Unboxed bools in rules, and rules' library calls out of tail position**: nothing
  measurable on hot `@x < o`, `@v + o`, `absm` (see gap 1 for why).
- **perf/lfb 22f83a0 - parked method scopes**: a method with block literals keeps its
  scope and Procs in a per-body slot for the next call (CompareExchange, reinitialized).
  280 B less per call, each4 217 -> 178 ns, all gates passed; AWFY 0.992, 1.050, 1.002.
  Not merged: no benchmark moves, and a reused scope is only safe while everything that
  can keep a method scope calls `MarkCaptured`. The branch's finding stands: the Proc of
  a block passed to a non-light Ruby iterator is never reusable, which is why DeltaBlue
  did not move.
- **perf/disp 086e90d - `nil?`, `==`, `!=`, `equal?` on object operands in the method
  JIT** (`JitRuntime.IdentityCallSite`). `==` 50 -> 27 ns, `nil?` 43 -> 35, but `str==`
  52 -> 97 (the new screen case gets in the way of the `s == "lit"` intrinsic), AWFY
  1.010, 1.007, 1.000: the benchmarks' hot `==`/`nil?` are on instance variables, which
  the JIT refused in method bodies at the time. Redone in round four, once it took them
  (d14eddf), as bf354cf, with `s == "lit"` kept ahead of the new screen case.

- **perf/jcall 7ecff1c2 fec30b9e - calls of other methods from a method-JIT body**
  (round five; new files `Jit/JitCalls.cs`, `Jit/JitCompiler.Calls.cs`). A call goes from
  the frameless body through a light-scope site handed the declaring scope; if the callee
  needs the caller's scope the body deopts, which is sound only while nothing observable
  has happened, so a call counts as an effect and nothing that can deopt may follow it -
  not a second call, not an ivar write (the callee may have frozen self). A body that
  makes calls is compiled as its own named delegate inside a CompiledFrameExpression so
  that it appears in the callee's backtrace. All gates passed. Json's `read_value`,
  `read_string`, `read_name`, `JsonArray#add` and DeltaBlue's `weakest`, `add_constraint`
  specialized; the hottest methods did not (gap 2). Alone 0.983, 0.971, 0.996; on top of
  round five's other two branches the tree measured the same overall (0.965, 0.953, 0.974
  against 0.955, 0.952, 0.976) and DeltaBlue 3-10% slower in all three runs. Not merged.
  Its first version made Towers 9-11% slower, which is how gap 1 was found.
- **perf/resume2 d239871a..c8ad27de - "tail mode": bodies the typed compiler declines,
  compiled a second way** (round twelve, gap 1; stage 2 of `Docs/jit-resume-deopt.md`
  behind `-X:JITResume`; new file `Jit/JitTail.cs`, 1,230 lines, 2,220 with its matrix
  section and test). Not the design's stage 2 to the letter: instead of a typed prefix
  with an `Obj` tail after the first effect, the whole body from its first statement as
  operations that speculate on nothing or guard themselves on every run (Int32
  arithmetic and comparison, `==`/`!=`, `nil?`, `!`, `a[i]`, `a[i] = v`, attribute
  read and write, each re-checking the method version and the lookup's visibility),
  every value an object, parameters and locals in object variables written through to
  the scope once a call has made it, general calls through a light-scope site and then
  the full site, the body in the generic frame's place (same name, line markers,
  try/catch/finally, stack check, trace hooks), nothing that can deopt. Correct on
  everything its author ran: jit-matrix section 13 the same on CRuby, default, `-X:NoJIT`,
  the switch and a stress switch that makes every guard miss; the other matrices; the
  full test suite; ruby/spec language with the switch; all thirteen benchmarks without
  FAIL. With the switch Richards goes from 15 specialized and 4 rejected to 34 and 0,
  Json from 11/9 to 39/0, CD from 25/2 to 39/0, Towers from 2 to 7 (its author's
  counts, some from a build before the last two fixes).
  And it buys nothing. The shapes, ns per call, ab.rb with 7 rounds, measured twice with
  the columns reversed (CRuby / base / switch off / on): `find_task` 32.9 / 52.1 / 52.8 /
  49.7 and 51.9 / 51.6 / 51.8; `queue_packet` 161.2 / 181.6 / 181.1 / 173.8 and 181.1 /
  175.6 / 176.3; `add_input` 65.0 / 61.4 / 61.7 / 61.9 and 56.8 / 58.0 / 61.6; `schedule`
  172.1 / 195.5 / 194.2 / 194.9 and 191.7 / 185.2 / 181.1: every on/off pair inside the
  spread. All thirteen benchmarks, 12 s, both orders: with the switch 1.028 and 1.028 of
  the base (Json 13% and 9% slower, Towers 10% and 10%, Bounce 7% and 7%; DeltaBlue
  and CD level, Richards level in one run and 6% slower in the other). Without the switch the branch measured 1.016 and 1.020 of
  the base, Richards 6-8% and NBody 4-8% slower in both orders, although nothing should
  compile differently with it off; not looked into, since the branch is not merged, but
  a merge would have had to explain it.
  Where the time goes (its author's perf of Towers): `CallSite.Target` 38% -> 11%, the
  method bodies 1.5-2 times their own time each, the sum unchanged or worse. A call-site
  rule for `a + b` or an attribute is a class-version compare and the operation; an
  inline guard and a helper call is the same work in another place, and the frame is
  the generic frame. Two things made it worse before they were found: a guard that kept
  one receiver class re-bound with a reflection lookup each time `!x.equal?(y)` saw true
  after false (CD 35% slower; now four classes, and plain calls after 64 binds), and an
  Array fast path that tested `HasInstancesWithOwnClass`, which is true for Array in
  every process from start-up, so it never hit. Not merged; the branch is not pushed.
  Kept from it: the shapes (`Util/bench/jit-resume2-shapes.rb`, 6197f6ab).
- **perf/undo a06d9dc7, reverted by its author in 9b4c2fdd - logged instance variable,
  attribute and Struct member stores, taken back on a deopt** (round fourteen, gap 1; the
  first of three experiments of a workflow on TruffleRuby's JIT techniques). The other
  way to let a guard fail after an effect: instead of continuing, make the restart
  invisible, as the typed bodies already do for Array and Hash element writes
  (`JitUndoLog`). A store of an existing instance variable of self, an `attr_writer`
  store on another object and a Struct member store log (object, slot, old, new) and
  no longer count as effects; a rollback puts the old value back only where the slot
  still holds what the body stored (a compare-exchange, so another thread's store in
  between is kept); a frozen receiver, a variable that would have to be added, a full
  log and a receiver that is not a RubyObject deopt before anything is written. Used
  only on a second compile of a body the first would decline for a deopt after an
  effect; no loops; such a body is not inlined or bound. `Scheduler#hold_self` and
  DeltaBlue's `Vector#remove_first` went from declined to specialized, and
  `Util/jit-matrix.rb` was CRuby's (section 10 under `ED_CHECK=1`: 94 specialized, 36
  deopted after their effect, against 50 and 14).
  And the compiled bodies were slower than the generic ones they replace. Its author's
  numbers, taken at a load of 2.4 to 4.6 and not measured again by anyone: `hold_self`
  66.7 -> 76.2 ns, `hold_w` 57.5 -> 75.3, `hold_ww` 73.8 -> 93.6, two `@x += 1` in a
  row 60.9 -> 79.8; an attribute write then a read, and an increment then arithmetic,
  within the spread; Richards 1.022 and DeltaBlue 0.974 in six alternating processes,
  ranges overlapping. Where it goes: the entry's try/finally around the log, taking
  and releasing the thread's log, and a checked out-of-line store that writes five
  fields of a log entry; with the try/finally removed (unsafe, to see) the bodies came
  down to the generic body's speed and no further, because the generic code already
  does these stores through inline caches in about 27 ns.
  With round twelve this closes both cheap ways around the rule: neither continuing in
  untyped compiled code nor undoing pays, for the same reason - what a small body of
  stores costs is its entry and its bracket, not its stores. It also says that for
  Richards' and DeltaBlue's declined methods the rule is no longer the first obstacle:
  after it come the nil constant, `abs`, `== nil` on an Array and calls whose callee has
  no typed body (perf/core, the workflow's second experiment, takes those).
- **`x == "lit"` without allocating the literal, in code the method JIT does not
  compile**: 80 B less per comparison in a micro test, no change in AWFY bytes (Json's
  comparisons already go through the JIT). Reverted.

- **perf/box 6fe39e6a bb4f0846 - int results of library methods through
  `Protocols.BoxInt32`** (round six: rule results via `MetaObjectBuilder.ConvertResult`,
  the block path of `RubyMethodGroupBase`, `BlockParam.Yield(int)`). Bytes per iteration:
  Json 35.7 -> 29.6 MB, DeltaBlue 24.8 -> 23.8. AWFY time 0.995, 1.008, 1.009: nothing.
  Its first version also changed `LibraryDispatcher.Box` and made Util/bench's
  `str_interp` 40% slower on its one cold pass (and faster warm), which was not
  explained; that edit was reverted. Not merged: it saves memory, not time. Worth taking
  if allocation itself becomes the goal.

Round seven (none of these is in the tree; the branches were local to the session):

- **perf/richards - `BasicObject#!=` answers by identity for nil and plain objects whose
  `==` is still BasicObject's** (gap 10), looked up once per global method version and
  kept beside the `==` site (`BinaryOpStorage`). Correct: 57 cases with `==` defined,
  removed, singleton, included, prepended, extended, on Object, Kernel, NilClass and
  BasicObject, undefined, private, refined, all as CRuby. `ValueNotEquals` 2.9% -> 1.2%
  of Richards inclusive. Richards against 9f4d44f8, five quiet runs: 0.962, 0.948, 0.937,
  0.995, 1.141; the Json branch, which cannot touch Richards, gave 0.942 and 0.958 in the
  same invocations. Geomean of the thirteen 1.008 and 1.023; `!=` itself about 5 ns of 60
  faster in generic code with one to three receiver classes. A gain its own profile puts
  at two percent cannot be shown on this machine, so it is not kept; it belongs with
  whatever closes gap 10 properly (one dispatch, as CRuby's opt_neq).
- **Nil-valued constants in the method JIT** (gap 11; Richards experiment, first
  attempt): `ResolveConstant` took a null whose `ConstantStorage.WeakValue` is
  `ConstantSiteCache.WeakNull` as a real nil, and typed a constant receiver by its folded
  value. `HandlerTaskDataRecord#initialize` and `DeviceTaskDataRecord#initialize`
  specialized, both cold; the hot one, `RBObject#append`, then declines at "call:
  link/0", an attr read inside its `while` loop. Richards 0.97, 1.08, 1.07 under load,
  bytes unchanged. Worth redoing together with attr reads of a local inside a loop.
- **Non-generic entry points on the attribute dispatchers** (NBody experiment, first
  attempt; `RubyObjectAttributeReaderDispatcherWithScope::Invoke` is 13% of NBody): the
  miss path moved into a NoInlining method so that the hit path is not shared-generic
  code. The reader's share 12.4% -> 10.9%, no difference in time that could be separated.
  The reader's cost is the delegate call through the site plus about ten dependent
  loads it already had; edits inside the dispatcher cannot remove much.
- **An inline cache for `recv.attr` in generic code** (NBody experiment, second attempt;
  new `AttributeReadSite`, emitted by `MethodCall.TransformRead` for a call with an
  explicit receiver and no arguments, falling back to the usual site). Three versions.
  The first made a static call that takes the scope, which stops `LightScopeCall` from
  making the call with a light scope: NBody 5-28% and CD about 35% faster under load, but
  Bounce 26-52% and Richards 6-20% slower, their frames no longer light. The third,
  learning inside one `Read` call, on a quieter machine, five runs: NBody -6.7, -11.2,
  +3.5, -10.8, -10.9%; CD +0.7, +16, +4.3, +4.0, +5.3%. Every zero-argument call with an
  explicit receiver that is *not* an attribute read pays the cache's load and test, about
  1.5 ns (`o.foo` 17.4 -> 18.9 ns). All its cases matched CRuby. Two lessons for anything
  emitted into generic code: whatever passes the scope variable outside a LightDynamic
  call disables light frames, and a closure constant costs a load at every site it is
  attached to, the ones it cannot help included. A cache placed by a per-site profile, or
  in the binder's rule for a monomorphic attr site, would not have the second cost. The
  first version's CD figure is unexplained and may be worth one quiet run by itself.

Round eight (the NBody experiment landed, see above):

- **perf/neq1 - `a != b` that reaches `BasicObject#!=`, bound as the receiver's `==`
  negated** (gap 10): one dispatch, as CRuby's opt_neq, made in `DispatchIntrinsics`
  where `send` and `respond_to?` are rewritten the same way. Its first version took the
  shortcut for any public library `==`, and on a hot site lost the `BasicObject#!=`
  frame from backtraces and `caller_locations` whenever that `==` runs Ruby code (Array,
  Hash, Struct, Range, Comparable, String with a `to_str` argument, Integer and Float
  with an object argument), while the first, cold executions of the same line kept it.
  Its own backtrace case used a Ruby-defined `==`, where the shortcut is off. The second
  version limited it to what opt_neq limits itself to: identity (`BasicObject#==` on nil,
  true, false, plain objects), Symbol, and Integer, Float or String with an argument they
  compare without calling out, tested in the rule. Correct on everything tried: 57
  semantic cases, the frames seen from a hot site, c_call tracing switched on at a hot
  site, a 45-line matrix. On a quiet machine, ns per `!=` in generic code, medians of
  five alternating rounds: nil 24.9 -> 16.8, Symbol 26.2 -> 19.4, String 29.3 -> 23.7,
  plain object 25.5 -> 22.7, Integer 24.9 -> 24.1 (faster in two rounds, slower in two);
  Integer against a Float 31.5 -> 34.3, slower in all five; a site whose argument type
  alternates 5-9% slower. AWFY geomean of the thirteen 1.019 and 1.008. No line held 15%
  in every round, the one Permute needs showed nothing, and one got slower: not kept.
  What is left of the idea is the shortcut for identity, Symbol and String alone, the
  three lines that were never slower; its cost, that `!=` rules are then not shared
  across classes, was not measured on wide sites.
- **Permute's block** (gap 4): `[BlockNotKept]` on `Integer#downto` changes nothing, and
  lowering `MinRun` cannot help, because the loop emitter declines the block at its
  calls. Nothing was built beyond that.

Round nine (all three experiments landed; these are the versions of them that did not):

- **`BlockLoopSite.MinRun` = 1 for every block** (perf/entry, first version): NBody 27%
  faster and nothing allocated, but an entry cost a fixed 100-170 ns, so `a.each { |x| s
  += x }` over one element went from 85-117 ns per call to 158-182, over two from 99-130
  to 183-194, `times` and `Range#each` the same, `a.map` slower up to eight elements
  (114 -> 277 at one, 252-283 -> 301-360 at eight) and `any?` up to four. `alloc.rb`
  showed it level or better everywhere: the cost was time, not bytes.
- **One floor of 5 for every block**: costs nothing and gains nothing on NBody
  (1817-1940 against 1789-1944), whose inner `Range#each` runs 4, 3, 2, 1.
- **Two floors, 1 for six calls or more**: an eight-call Integer body over one element
  140-161 ns in the copy against 96-107. Hence the middle floor of 2.
- **Skipping the copy's Array check while no Array has a singleton class**
  (`HasInstancesWithOwnClass`): some Array always has one from start-up on, so it never
  applies.
- **The inlining screen admitting every call, then every call on an instance variable**
  (perf/cdtree, first two versions): a caller is wrapped, profiled and declined when
  its callee turns out not to be inlinable. Towers' bytes rose 16x with the first;
  with the second Richards wrapped `work_in_add`, `device_in_add` and
  `add_input_and_check_priority` (their `append` has a loop, a constant and an attr
  write) and was about 4% slower in two sets of five alternating processes, DeltaBlue
  declined eight methods. The screen now asks of the callee, by syntax, what
  `JitCode.Inlinable` will ask. The instance-variable rule bought one DeltaBlue method
  (`UnaryConstraint#choose_method`) and no time (51.5 against 52.1 ms).
- **Range literals as scope-free in bodies with literal blocks**: makes `Vector#each` a
  light frame, 5-7% slower per call (`RubyMethodScope::.ctor` 2.5% -> 4.8%,
  `InstantiateReusableBlock` 0.8% -> 1.8%).

Round ten:

- **A `yield` inside a block reading the method's Proc once** (perf/veach, gap 14): the
  yield kept the Proc in a hidden local and took its self from it, in place of the
  second scope walk (`GetMethodBlockParameterSelf`). `Vector#each` over 1, 2, 4 and 100
  elements and DeltaBlue each faster in about half the runs and slower in the other
  half (DeltaBlue 62.4/59.5, 61.5/58.9, 64.7/68.8, 60.5/66.5, 57.9/57.1, 56.3/59.4 ms):
  the walk is cheap. Reverted.
- **A block whose body is `yield <expr>` run by the block-loop tier's copy**: not built.
  The loop compiler cannot compile a `yield` at all (a plain `while ... yield(i)` loop
  is "node: YieldCall, emitter gave up"), so it would first need the BlockParam and the
  break/next/redo/return protocol in the emitter and a deopt after a yield that had
  effects, and then a frame for `block in each`, which CRuby shows in backtraces and
  `caller` and the copy removes. At most it takes one block invocation per element off
  the 132 long runs.
- **A nil-valued constant read in the method JIT** (perf/rich, gap 11): a constant whose
  storage holds `ConstantSiteCache.WeakNull` is a real nil, while an autoload that has
  not run resolves to an empty storage, and a constant receiver was typed by its folded
  value. Richards went from 15 specialized and 4 rejected to 17 and 2: the two cold
  `initialize` methods. `append` still fails on a deopt after an effect, so it gains
  nothing; not committed.
- **Nesting the inlined call for self-calls too** (perf/cdt2): wrapped Json's
  `HashIndexTable#add` and `#get`, which were then declined. Nested only for a call on
  another object. **Inlining only the sampled class on a parameter**: retired for good
  in CD, whose keys have two classes; hence the two-class dispatch.
- **CD's `tree_insert` through a mid-body `Node.new`** (round eleven, gap 3): not built.
  The construction is the first of four rules that decline the body (the second seen
  under a patch that was not kept, the last two read from `TryCompile`), each in the
  effect/deopt bookkeeping that three of the ten faults the gate missed were in, for an
  estimated 2-4% of CD, below what CD's noise lets a round show. Gap 3 lists the four,
  the safety conditions (a pure `initialize`, a deopt before a construction that is not
  current, never under `-X:ObjectSpace`) and the matrix that pins the semantics.
- **`==`/`!=` between two Strings with a non-literal operand, and `String#[]` with an
  Integer, as operations of a typed body** (round fifteen, perf/core2, gap 1; built and
  dropped by their author, the diffs are not kept on the branch). 0.92 and 0.91 on
  their shapes, under the 15% a micro line needs, and they compile nothing hot: with
  both, Json's `read` gets as far as a deopt after `@index += 1` and is declined there.
  Numbers are the author's, taken beside another experiment. What it says: a core
  operation is worth adding to the typed body when the method around it then compiles,
  and for Json's readers that needs the effect rule first.
- **`Kernel#loop` in C#** (round sixteen, perf/havlak, gap 25; built and reverted by its
  author, nothing committed). `loop` is in the prelude with a `begin`/`rescue
  StopIteration`, which keeps it out of light frames; in C# (a yield loop, a catch, the
  exception's `result` through a call site, the blockless form left to the prelude) a
  matrix of 26 cases and the backtraces were the same as CRuby's. `loop { return }` 536 ->
  352 B per call, Havlak 2724 -> 2682 MB per iteration; the shape at 0.72-0.90 of before
  and Havlak at 0.954 and 0.983 in six alternating processes, every one of them
  overlapping at load 4-14. Not shown to reach the bar, and not re-measured on a quiet
  machine: what `loop` pays is what every method with a `rescue` pays (gap 22: about 80
  ns per call when nothing is raised), so the step to take is that one, in the compiler,
  and `loop` stays in Ruby.
- **Garbage collector settings** (round twenty; measured, nothing built). Havlak, the
  benchmark furthest behind CRuby, has 30.1% of its samples in libcoreclr itself
  (`Util/perf/profile.sh`, 3081699d; then `CallSite.Target` 13.3%, `JIT_WriteBarrier`
  3.0%, and no Ruby frame above 2.3%) and allocates 2.73 GB per iteration, so the
  collector looked like a lever that needs no code. All fourteen with
  `DOTNET_GCgen0size` at 64, 128 and 256 MB and with `DOTNET_gcServer=1`, both column
  orders, on a host whose cells move by a tenth (Mandelbrot, which allocates nothing,
  read 38 to 49 ms across the columns): geomean against the default 0.989 / 0.969 for
  64 MB, 0.946 / 0.934 for 128, 1.000 / 0.941 for 256, 1.024 / 0.995 for the server
  collector. Then six alternating processes each, default against 128 MB: Mandelbrot
  0.932 (the control), Storage 0.891, Sieve 0.892, Permute 0.900, Havlak 0.962, Json
  0.983, CD 1.040, every pair of ranges overlapping widely (CD 367-449 against 361-471
  ms) - it leans the way the control leans, and shows nothing. Default against the
  server collector: Havlak 0.863 with the ranges apart (3.49-3.65 s against 2.93-3.19),
  and Storage 1.174, Mandelbrot 1.106, Json 1.083, CD 1.040; `gem list` starts in 1351
  ms against 1195. Peak memory, Havlak / Json / Bounce, MB: default 290 / 266 / 243, 64
  MB 225 / 195 / 179, 128 MB 298 / 264 / 240, 256 MB 425 / 396 / 348, server 350 / 204
  / 179; `-e 1` 117 everywhere.
  What it says: no setting is better for the suite; the server collector is better
  for Havlak alone and worse for the rest and for start-up, so it is not a default.
  That Havlak answers to the collector that marks on four cores and not to a larger
  youngest generation says its time in the runtime is the marking of what survives
  (the graph it builds), not the number of collections: it falls with fewer bytes that
  live long enough to be marked, which is gap 13's allocation per iterator call (about
  half of Havlak's bytes), not with a knob. And the default youngest generation follows
  the L3 cache the host reports (260 MiB on this one: the default's peak memory is the
  128 MB setting's), which is one way a change of host changes IronRuby's numbers and
  not CRuby's.

## Regressions

None in speed.

To check, not bisected: Richards allocates 14.0 MB per iteration at a7060a27, and 13.9
at c791ce6e in every run of round sixteen's acceptance (so not from that round's
merges), where this file recorded "13.9 -> 3.2 with #215's `!=` binding merged in" at
f37be3b1. Either something between f37be3b1 and c791ce6e took that back
(`git log --oneline f37be3b1..c791ce6e`), or the 3.2 was never Richards' (Mandelbrot
reads 3.2 today). `ruby Util/perf/alloc.rb "ir=..." -- Richards` on a build of f37be3b1
says which. Its time has not moved (340 ms in both tables).

To check, seen once: `Util/block-loop-matrix.rb` under `-X:NoJIT -X:NoOSR` printed the
backtraces of its two nested-block rows ("nested: raise in the inner block", "nested:
caller from the inner block") without their `Range#each` frame and with the frame after
the last one in its place, in round eighteen's acceptance run of perf/bounce (a build
whose change is in the method JIT, which that run does not use). Four reruns on that
build and on the baseline had the frame, as CRuby has. A backtrace that depends on
something that varies from run to run with the tiers off - whether the DLR has compiled
a frame's rule or still interprets it would be one - has not been looked for.

To fix, not performance: differences from CRuby 4.0 that the reviews of rounds seventeen
to nineteen met on the way. Each is the same on the build before those rounds and with
the tiers off, so none is the JIT's; each wants a test with CRuby's output.
- `raise "x #{i}"` (an interpolated String as the argument) gets the previous
  statement's line in its backtrace; with the message built by `+` the line is right.
- `raise 42` (TypeError, "exception class/object expected") has no `'Kernel#raise'`
  frame at the top of its backtrace; CRuby has one.
- The frames of `Exception.exception`, `Exception#set_backtrace` and `Method#call` are
  missing from backtraces where CRuby shows them.
- A frozen exception instance that is raised gets a backtrace (CRuby leaves it nil), and
  `backtrace_locations` on a frozen exception raises FrozenError (CRuby: nil).
- TracePoint fires no `:return` when a method is left by an exception; CRuby fires it
  after `:raise`.
- An Integer between 2**31 and 2**62 is not identical to an equal one computed again
  (`equal?` false, different `object_id`); in CRuby every Integer up to 2**62-1 is a
  Fixnum. Inside the Int32 range IronRuby agrees.
- `ObjectSpace._id2ref` cannot find the id of a Float that CRuby holds as a flonum
  (RangeError), found in round sixteen.
And in the tools: `Util/jit-matrix.rb` replaces `Integer#-` at about line 2510 and never
restores it, and restores `String#length` and others through `alias_method`, after which
the JIT no longer takes them for the built-in ones; the two thousand lines behind run
with that on every build alike (see "Caught by the review (round seventeen)").

- Seen on the unchanged build by round twenty-two's probes, not touched: `Array.new(2**64, 1)`
  raises ArgumentError where CRuby raises RangeError; `Array.new(nil)` says "no implicit
  conversion of nil into Integer" where CRuby 4.0 says "no implicit conversion from nil to
  integer" (the `into` wording CLAUDE.md keeps; the rest of the sentence differs too); a refinement of `Array.singleton_class#new` is not applied in a method defined
  late in a long file after `using` (it is in a small file); and the built-in
  `Array#initialize` called with two arguments through an alias or through `super` from a
  module prepended to Array's singleton class raises a CLR `System::NullReferenceException`.
- Seen by round twenty-three's probes, not touched: `1.is_a?(nil)`, `kind_of?(nil)` and
  `instance_of?(nil)` raise a CLR `System::ArgumentNullException` ("Value cannot be null.
  (Parameter 'other')") where CRuby raises TypeError "class or module required"; and with
  an argument that is no module (`1.is_a?(3)`, `"s".is_a?("String")`) the TypeError says
  "no implicit conversion of Integer into Module" where CRuby says "class or module
  required". Fixed in round thirty-one (perf/rminor, see Landed).

- **On the ReadyToRun host a backtrace through a block nested in a block lacks the
  `Range#each` frame** (found by the gate of round twenty-five; on master 0e78b9fd too,
  so not this branch's). `Util/block-loop-matrix.rb`, rows "nested: raise in the inner
  block" and "nested: caller from the inner block": CRuby and the Release (IL) build
  print `block (2 levels) in ...`, `Range#each`, `block in ...`, `Array#each_index`; with
  `IR_CONFIG=R2R` - what `ir.sh` runs by default and what a release ships - the
  `Range#each` line is missing, four runs of four, with `-X:NoJIT`, `-X:NoOSR`, both, and
  `IR_PARKED_SCOPES=0` alike. So it is no tier's doing: the precompiled image loses (or
  does not name) the library method's frame. An earlier round saw the same line missing
  once with the tiers off and could not repeat it; it was probably run on that host.

## Faults the gate missed

Found by review of #215 (Greptile), all three real, all fixed with cases added to the
matrices. Each came from a shape nobody had put in a matrix, so the lesson is in what the
gate asks for (`.claude/skills/perf-gate`), not in running it more often:

- **`def f; f { }; end` killed the process** instead of raising SystemStackError (round
  five, 50318082). A light frame checked the stack only if `LightScopeCall` had rewritten
  one of its calls, and it does not rewrite a call with a block. Every frame shape that is
  newly light needs a recursion case: the entry check is what stands between deep
  recursion and the end of the process.
- **`!(true && @x)` answered false** for an `@x` with a `#!` of its own (round four,
  9ca993d). The truth of a condition is not the value an operator is called on.
- **`o.value = o.value + 1; @x = 1` on a frozen self wrote `o.value` twice** (round four,
  d88766c with 6347585). "A write to self deopts only as the first observable thing" had
  been true until attribute writes on other objects became possible. Every new kind of
  effect has to be checked against every operation that can deopt, including those that
  were sound only because nothing came before them.

A hunt for more of the same followed (three agents, one per area: light frames, the
method JIT, block and Proc reuse), each comparing CRuby, IronRuby and IronRuby with the
JIT off on hundreds of small programs:

- **`def d(i); [i].each { |v| return 5 }; end; d(1) { 9 }` threw MethodUnwinder** (round
  five, 50318082), on the first call, no warm-up. The scope a light frame makes on demand
  takes the call's flow-control frame - the caller's literal block - and
  `MethodPropagateReturn` compared the return's target with the scope object only. Found
  by the hunt through block reuse, in code that hunt was not about: the commonest shapes
  (`return` from a block, in a method given a block) were in no matrix.
- **`const_missing` recursing through a constant read, and `#inspect` recursing through a
  qualified constant read, ended the process** (light frames since c9a17e2). A constant
  read is not a dynamic site, so the frame had no entry check, but the helper behind it
  runs Ruby code. The slow paths (`RubyModule.ConstantMissing`,
  `RubyOps.GetQualifiedConstant`) now check the stack. The general form of the question:
  which runtime helpers reachable from a light frame can run Ruby code without going
  through a call site?
- The method JIT's widening survived about 420 programs, among them every pairing of
  eight effects with nine deopts in five shapes; 150 of those ran specialized. Block,
  Proc and BlockParam reuse survived every keeping path, `[BlockNotKept]` path and
  reentrancy shape tried (the lists are in the commits' merge messages and the hunt
  reports). What the hunts did not cover: Windows, net11.0, several runtimes in one
  process, and the full spec sweep.

A second review (CodeRabbit) then found four more, all real, the first of them in the
one area the hunt had called sound:

- **`def capture(&b); b; end` handed out the same Proc on every evaluation of a literal**
  (fd94bce, once light frames took bodies that read a parameter). "A light frame can only
  yield to its block" was the premise of marking the block reusable, and a block
  parameter breaks it. The hunt had tried `&b` stored and forwarded, in methods that were
  not light. A premise stated in a comment is a thing to test when the set of bodies it
  speaks about is widened.
- **`h.fetch(k, @n += 1)` evaluated the default twice** (the hash intrinsics, with
  6347585): the emitter marked its deopt before emitting the arguments, so an effect in
  an argument did not count as preceding it. The hunt's 400 effect/deopt pairs put the
  effect before or after an operation, never inside its operands.
- **A final `C.new` in a self-recursive body** left the outer invocations with operators
  an inner `initialize` had redefined (38a149b): "last statement" is last for one
  invocation only.
- **`def initialize(n); C.new(n + 1); end` ended the process** (38a149b): the direct
  construction has no call site and no scope, hence no stack check. The fourth recursion
  without a check; the question to ask of any new direct path into Ruby code.

Ten faults in all after the gate had passed: seven found by review, three by the hunt.

A review of this branch's Hash-literal change found an eleventh:

- **`def hash; {self => 0}; 0; end` ended the process** (460cac05). The literal's helper
  (`RubyOps.MakeHash`) is no call site, so the light frame dropped its stack check, but
  it calls the key's `#hash` and `#eql?`. The gate's million-deep probe recursed through a
  helper method that made a call, and so kept its check; the review found the method whose
  only call is the literal's helper. Section 12 of the Hash-literal matrix has the case.

The full five-suite sweep (`Util/ci-sweep.sh`, then `Util/sweep-check.rb`) was run on
commit 72c29b4c, Release net10.0, four jobs, after all of the above: core 23130 examples,
library 6011, language 2935, command_line 175, security 34; no failing example that
`Util/spec-known-failures.txt` does not list, none of its entries newly passing, no file
without a summary line. It takes under eight minutes on four cores, so it belongs in the
gate of every round, not only at the end: perf-gate's step 3 now says so. CI (Ubuntu and
Windows on net11.0, ruby/spec language, the wasm host) passes on the same commit.

### Caught by the gate (round seven)

- **`@v[i] = x; @w[j] = x` called a private `[]=`** (perf/permute, d027dbe6, before it
  was merged). The last store, which may not deopt once an earlier store went unlogged,
  handed what it could not do to a call of `[]=` through a site of its own, made with
  `RubyCallSignature.Simple`. A site without a scope sees every method
  (`RubyCallAction.GetVisibilityContext`): a @w with a private `[]=` got it called where
  CRuby raises NoMethodError, got it called instead of `method_missing`, and a protected
  one was called from an unrelated class. The author's cases and the gate's first 63 all
  passed: they used a swap, whose right-hand side reads the receiver first and deopts
  before any store, so none of them reached the fallback. Fixed in ff744878 by checking,
  before the first store, that the last store's receiver is an Array itself; on that, with
  Array#[]= the public builtin and no refinement, a call that sees everything is the call
  the source makes. The general form: a fast path that makes a call in place of one the
  source makes owes it the source's visibility and refinements, and a scope-less site
  has neither (`ConstructionSite` is right only because `new` is public).
- **A merge that compiled and passed everything allocated 10% more.** e4fc38aa and
  965aac8a were each right; together, the first read a String's content through the
  property the second had put in the way of every reader but Equals. No test could show
  it: `alloc.rb` on the benchmark the other change was about did (Json 39.5 MB against
  35.8). After a merge with work that landed meanwhile, measure what that work gained,
  not only what this round did.
- **Three of the gate's own cases tested nothing at first.** Methods written with an
  implicit self (`c = a`) that the JIT declined for that, so the new path never ran; a
  matrix case that reported with `x == true` below `using IDRefine`, where Object#==
  answers :refeq, and so printed the same on every line, CRuby included; and a probe that
  created one Array with a singleton class before warming up, after which the JIT
  declined every Array store in the process. Each printed exactly what CRuby printed.
  A differential case is evidence only once `IR_JIT_VERBOSE=1` shows the shape compiled
  and the probe is seen to reach the code it is about; where one odd receiver invalidates
  the compiled code for the rest of the process, it is one probe per process.
- **Five core/string and core/array examples failed on the candidate only** in one run:
  mspec had been started outside a checkout for it, without `default.mspec`. The baseline
  fails the same five from the same directory.

### Caught by the gate (round nine)

- **Entering the compiled copy for every run made short runs of light blocks 1.5 to 2
  times slower** (perf/entry, first version, before it was merged). Its author measured
  NBody (27% faster), bytes for all thirteen (level or lower) and one four-element
  shape with a heavy body (7-12% faster). The gate's table - `each`, `times`,
  `Range#each`, `map` and `any?` with a one-operation block over 1, 2, 4 and 8 elements,
  one shape per process - showed a fixed 100-170 ns per entry. No AWFY benchmark has
  that shape hot enough to show it, and bytes cannot. A change to *when* a compiled
  path is entered is measured where the old rule kept it out: at the lengths just under
  the old floor, with the lightest body that path accepts.
- **The reworked version still lost on one cell the author had not measured**: a body of
  six to eleven calls over a single element (140-161 ns against 96-107). The author's
  report said so ("I measured no heavy shape at k=1-4 other than NBody"). The threshold
  it had chosen was a count, not a measurement; the gate measured both sides of it.
- **A screen that admits more wraps methods that are then declined** (perf/cdtree, first
  version). Its author compared which methods were newly specialized, and Towers' JIT
  decisions; the gate compared the whole `[jit] specialized= rejected= screen-rejected=`
  line of every benchmark, found eleven more rejected in DeltaBlue and three in
  Richards, and Richards 4% slower in alternating processes. After a change to the
  screen, a new `declined` line is a cost until shown otherwise.
- **One of the gate's probes failed on every tree, the baseline included, and the fault
  was the probe's**: three threads appended to `r`, which the script had used as a
  top-level local forty lines earlier, so the blocks shared one Array ("comparison of
  Integer with nil" from `sort`, an ArgumentError from the Array's copy). CRuby printed
  the expected answer because its threads take turns. A probe's threads use
  block-local names that occur nowhere else in the file. The same probe expected only 5
  or 23 from a method while another thread redefined its callee, where 14 (one call
  before the redefinition, one after) is as right; CRuby can print it too.

### Caught by the gate (round eleven)

- **A compiled `o.x` read through an accessor a subclass had made private** - on master,
  not from the change being gated (fixed in d4a97243). `class Hidden < Node; private
  :val, :val=; end`, and `o.val` in a hot body answered with the variable where CRuby
  raises NoMethodError; the same for `protected`, for the writer, and for a Struct
  member. `private :x` in a subclass leaves an entry there that only forwards to the
  inherited method, resolution follows it, and the method it returns is the
  superclass's public one: the JIT asked that method for its visibility
  (`resolved.Info.IsPublic`) where the lookup has it (`GetMethodLookup(name).Visibility`).
  The experiment's own matrix had a private row and a protected row and passed both:
  their classes *defined* the accessors private. The gate's fourth question now names
  the third kind of receiver (`.claude/skills/perf-gate`). A row for "private" tests one
  of the ways a method gets to be private.
- **The probe that found it failed first for a reason of its own**: it expected a
  subclass's protected writer to be callable from an instance of the superclass, which
  CRuby refuses too, and the script died on that line before reaching anything else;
  and its second version compared an `inspect` that prints the object's address. Print
  the exception's class and message per row and go on (as the matrices' `t` does), and
  no addresses.

Not from this work, found on the way and still open: a self-recursive method with a
logged Hash store slows down about 1000x past 65,536 levels of recursion (the shared
`JitUndoLog` fills, the outer entry deopts, and `OnDeopt` never retires it because no
generic call has completed); `NilClass#nil?` removed still answers true; `equal?` is true
for equal Bignums and Floats; `local_variables` lists block-locals before parameters;
backtrace lines of multi-line statements point at the statement's first line;
`method(:binding).call` works where CRuby refuses; a thread dying of SystemStackError
prints its whole backtrace. From round seven: `Enumerable#chunk_while` loses elements
once `nil?` answers something truthy for an Array (`class Object; def nil?; :x; end; end;
[[1],[1]].chunk_while { |x, y| x == y }.to_a` is `[[[1]]]`, CRuby `[[[1], [1]]]`): the
prelude's version asks `chunk.nil?`, and `ruby4.rb` asks `.nil?` 278 times, so it will not
be the only method that a redefined `nil?` breaks where CRuby's C does not ask;
`a[2**40] = x` raises RangeError where CRuby raises NoMemoryError; a String with a stray
byte in UTF-16LE inspects as `"\uDC20"`, CRuby's is `"\x20"`;
`Exception_CauseRaisedInClause1` fails in Release on net10.0 and passes on net11.0.
From round eight: where `Integer#==` or `Float#==` calls the argument's `==`, a
backtrace has no `Integer#==` frame (CRuby's has one), and `[[x]] == [[y]]` has one
`Array#==` frame where CRuby has two; the frame of `Struct#==` is labelled `Array#==` in
about one run in three of the same script; c_call tracing reports an `==` for each
Integer element `Array#==` compares, which CRuby does not; `Math.sqrt(Complex(4, 0))` is
2.0 (CRuby: (2.0+0.0i)), and the TypeError for `Math.sqrt(true)` names TrueClass where
CRuby says true. From round nine: `a.any? { }` over one element takes six times what it
does in CRuby (gap 13). From round ten: `42.compare_to(1)` is 1 and
`42.respond_to?(:compare_to)` true (so for Float; String is right): .NET's
`Int32.CompareTo` is reachable on Integer under its Ruby-style name, where CRuby raises
NoMethodError; `%w[apple banana].any?(/(an)+/)` leaves `$~` nil where CRuby sets it in
the caller; `[].method(:any?).owner` is Enumerable (CRuby: Array), and
`Enumerable.instance_method(:any?).bind(arr).call(pattern)` walks an Array subclass
without calling its `each`, which CRuby's Enumerable#any? does call.

### Caught by the review (round fourteen)

The first round whose experiments went to a reviewer who had not built them, with one
instruction: find an input where the candidate differs from CRuby and the baseline does
not. Two reviews, 143 probe files, each run on CRuby, the baseline, the candidate and the
candidate with `-X:NoJIT -X:NoOSR`, and counted only where `IR_JIT_VERBOSE=1` or
`IR_OSR_VERBOSE=1` showed the new path compiled. Both verdicts were "fail", with three
candidate-only wrong results between them (above, under Landed), all fixed before the
gate. The authors' own matrices and the tests had passed. What the authors' checks
missed is what they have missed before: a visibility read from the resolved method, a
second thread at the right moment, and rows that do not reach the path they are about.

Left open, and on master before the round:

- **A compiled loop that is running does not see a method another thread redefines.**
  (Closed in round fifteen by perf/safept, except for a typed body with an effect; see
  Landed. What follows is the entry as it was written.)
  A loop copy checks the method version when it is entered, not while it runs. Master
  shows it for `Array#size` in an outlined `while`, for an inlined `Integer#to_i`, and
  for an inlined or bound callee in a typed method body (round thirteen); perf/core adds
  `String#length`, `Integer#abs` and `Array#==` to it, the one difference the gate let
  through because the class was already there (two of the reviewers' probes print
  CRuby's answer on the baseline and not on the merged tree for that reason). The
  outlined loop has a safe point at the top of every iteration that already hands the
  loop back at an iteration boundary: checking the version there closes it for outlined
  loops and block loops at one compare per iteration (perf/nest does exactly that for
  its inner iterator), and for a pure typed method body the same check may deopt. Not
  counted as a failed specialization, or a thread that loads a library beside a running
  loop retires the loop. The next thing to build; it needs the loop benchmarks beside it.
- A Thread#raise into an outlined loop whose inner `while` stores a global leaves the
  global ahead of the local it was stored from (the reviewers' `p06w.rb`; the fault
  perf/nest was narrowed to avoid, in the shape master already compiles).
- `module Enumerable; private :!=; end` followed by `public :!=` there leaves Array
  without `!=` (NoMethodError; CRuby answers). JIT off as well: it is in the visibility
  forwarders, not in a fast path.
- From earlier rounds, still open: `remove_method :nil?` on NilClass (round twelve);
  no `:c_call` for attribute accessors (round eleven).
- Reported by a reviewer and not reproduced by me in three lines: a warmed
  `3.times { }` site after `private :times` raising an internal ArgumentError.

How far one benchmark moves by itself, measured in round eight: Bounce's iteration time
wanders between 63 and 88 ms inside one process, in stretches of 25 to 50 iterations,
on every tree; awfy.rb's figure is the median of the second half of about 150
iterations, so it lands on 64 or on 80 depending on where a fast stretch falls (the
baseline was at 64 in both runs of one comparison and at 79-82 in six processes of 120
iterations each). Storage, Towers and DeltaBlue show the same kind of swing. A
difference between two trees on such a benchmark is settled by separate processes of a
fixed length taken alternately (perf-gate, step 4), not by more awfy.rb runs.

### Caught by the review (round fifteen)

perf/safept went to a reviewer with the same instruction as in round fourteen, plus one:
say where the candidate is clearly slower than the baseline. Seventeen probe files. No
wrong result; three probes that the baseline fails and the candidate passes; and one cost
that every check of the author's had passed, because all of them changed something the
loop was built on:

- **A change to something unrelated made every running loop slow.** The epoch moved on
  every bump of the constant version (a new constant, `include`, `extend`, an autoload
  starting and ending) and on every method table update whether or not the global
  method version moved. A `while` loop over `a.size` beside a thread looping
  `Object.new.extend(M)` took 2.2-2.8 s in place of 0.13-0.18 s (CRuby 1.32 s), beside
  one defining new constants 1.5 s in place of 0.07 s, and a thread in a second runtime
  of the process did the same to the first: the copy was handed back, found not stale,
  thrown away all the same and built again after the spacing. Fixed by moving the epoch
  only where a compiled copy can be stale (2b7faaf5). The author's numbers after it:
  every probe within noise of the baseline, no stale copy except beside `Struct.new`,
  which moves the method version on the baseline too.
- **The same thing once more, found by a probe of mine after that fix:** the file an
  autoload loads assigns a name the module already holds (the autoload's placeholder),
  which counted as a reassignment. A loop beside a thread that autoloads 300 constants:
  1.3 s in place of 0.25 s, about 30 copies rebuilt; 0.27-0.31 s after a8d443c8.

What to take from it for the gate: a change that adds a way for compiled code to be
invalidated is measured with the invalidating events that do NOT concern the code, from
another thread and from another runtime, and with the verbose counters (`stale=`,
`invalidations=`) beside the baseline's. And a candidate's build is checked against its
last commit before anything is measured on it (see perf/core2 under Landed).

### Caught by the measurement (round sixteen)

perf/unboxed computed the right thing from its first version on (two reviews, the
matrices, the sweep). What it got wrong, five times, was what it cost code that has no
Float in an instance variable, and each time the tests had nothing to say:

- **The first version put a location in any slot array** and had every generic read
  test the slot's type. All fourteen: 1.037 of the baseline (CD 1.27, Richards 1.12,
  Json 1.07) for NBody's 0.81. Sent back; the redesign ties rawness to the shape, so
  that a plain object's read is the compare it was.
- **The redesign made the site's entry eight bytes larger and gave the typed reads a
  flag to load.** `treefind` (five shapes at a two-entry site, an entry allocated on
  every miss) was 8% slower in alternating processes, ranges apart, and level once
  every node had one shape. Fixed in 128306e7.
- **That fix removed a check it took for a duplicate**, and every generic read of a
  raw object went one call further: `blkattr` from 0.94 to 1.06-1.08, `short4` to
  1.07-1.12, Integer attributes of a born-raw class from 1.20 to 1.43-1.51. Only the
  experiment's own shape file and a second acceptance run showed it. Fixed in 36c58562.
- **Reading the raw object inside the inlined cached read** (63f19fc7; its author had
  tried the same in 676de2d6 and taken it back in 6304f815) was the cheapest read of a
  raw object - `read_i` 1.12 where it had been 1.67, `treefind` 1.01 - and passed the
  rule as written: no benchmark more than 5% slower in both orders. But that read is
  compiled into every `@x` and every attribute read, and six alternating processes of
  four benchmarks without a Float had Bounce 1.040, DeltaBlue 1.027, Sieve 1.024 and
  Richards 1.022; the fourteen together were level (1.001, 0.995). Taken back in
  0a8c380d: the cost stays with the classes that have the gain.
- **One row of the shape file measured nothing**: `attr_i` printed 31 ns on one build
  and 48 on the next, and its six repetitions range from 28 to 105 ns on the baseline
  itself (it allocates an Integer per iteration, and the least of six is whichever
  repetition the collector left alone).

What to take from it:

- A change to a helper that is inlined into every compiled body (`RubyOps.TryReadCached`,
  `GetInstanceVariable`, the typed code's `GetIVar` and `ReadSelfAttr`) is judged on
  benchmarks that do not use the feature, in alternating processes, before anything
  else: 2-4% on each of them is under the table's noise and under the 5% rule, and is
  the whole geomean.
- A fix found by one slow line is measured again on the lines the experiment was
  about, not only on that line.
- Before believing a row that prints the least of a few repetitions, print the
  repetitions.

### Caught by the review (round seventeen)

perf/jread passed IronRuby.Tests, nine matrices, ivar-stress and all five ruby/spec suites
with a wrong result in it, and measured well. Two reviewers who had not seen the code, one
after the other, and the rerun of the author's own matrix found:

- **A body that added variables created them in the order of the source text**, not in
  the order they were first assigned: `if c then @b = 1 else @a = 1; @b = 2 end` on a
  fresh object left `[:@b, :@a]` where CRuby, the baseline and the build with the tiers
  off leave `[:@a, :@b]`. I had asked the reviewer for exactly this after reading the
  author's "the stores are made in source order"; the author's 46 rows had none about
  order, and nothing in the specs runs such a method forty times on fresh objects. Sent
  back once; fixed in 42882e01 and f1ef1ac6.
- **Methods the baseline specialized were declined, silently.** With keyword parameters,
  or a default that writes (`def m(x = (@a = 5))`), the held values' locals were used
  outside the block that declares them; the tree failed to compile, the stub's
  `catch (Exception)` made that a decline, and the verbose line said "no reason
  recorded". Right answers, by the grace of that catch. Found by the second reviewer
  comparing `specialized` lines of both builds on its probes; fixed in e321b21d.
- **The author's matrix had stopped half way.** After the rework two of its lines said
  `p [o.n, o.m]` where `p` was a local of the file by then: a NoMethodError, on CRuby
  and on every build alike, so the four outputs were equal and the gate's "0
  differences" was true. The last row had not run since. Found when I added rows behind
  it and they did not print.

What to take from it for the gate:

- A matrix is compared only after it is seen to end: CRuby's exit status is 0 and the
  last line is the file's last row. Equal outputs prove nothing about a file that died
  the same way everywhere.
- For a change that lets more bodies compile, compare the `specialized` and `declined`
  lines of both builds on the matrices and probes, not only on the benchmarks: a body
  that was specialized before and is declined now is a fault of the change even when
  every answer is right, and "no reason recorded" is an exception swallowed.
- A claim in an author's report that is stated without a row ("in source order") is
  where to send the reviewer first.
- Not from this change, found on the way: `Util/jit-matrix.rb` replaces `Integer#-` at
  about line 2510 and never restores it, and restores `String#length` and others through
  `alias_method`, after which the JIT no longer takes them for the built-in ones. The
  two thousand lines behind run with that, on every build alike; some of their rows
  cannot be testing what their labels say. To be gone through.

### Caught by the review and by the measurement (round twenty-one)

perf/park is in the part of the runtime where five of the first ten faults were, and its
first version passed IronRuby.Tests, eighteen matrices, ivar-stress and all five ruby/spec
suites. What was wrong with it:

- **A leak, found by a reviewer's WeakRef probe.** The scope was cleared at park and the
  Proc parked with it was not: its self, the BlockParam cached on it with a parked block
  scope, and the scope's singleton-class field kept the receiver of a method's last call
  alive for as long as the method. No test, matrix or spec asks whether an object is
  freed. The fix has one now (`Method_ParkedScopeLeak1`: WeakRefs to the receiver, an
  argument, a method local and the caller's self; all four must be freed with the switch
  on and off).
- **It cost the programs it did not help, found by `alloc.rb` over all fourteen** (its
  author had run it on the two benchmarks the change was for): Permute +13% bytes and
  1.10 of its time, Storage +12% bytes, Json +4%, `s_any1` 5-11% slower. Three causes, by
  its author's dissection afterwards. `ScopeBuilder`'s `RegisterLocalsRewriter` knew
  `CreateMethodScopeLazy` by name and not the parked variant, so a light frame with a
  slot lost its register locals and allocated a tuple per call - found by making the new
  runtime helpers do nothing and removing the emitted code piece by piece until the
  bytes fell. A body that could never park (a recursion that finds the slot taken, a
  literal given to a Ruby-level iterator) made the larger, parkable kind of scope on
  every call. And the scope class had been unsealed and extended for everyone.
- **A race in the redesign, found by a matrix row with light frames off**: the take read
  the parked object's scope before the exchange that makes the object its own; another
  call could take it, fail to park and empty it in between (a NullReferenceException with
  four threads, once in several runs). Taken first and read after, now, with a row of
  its own.
- **The hypothesis was wrong, found by timing what the bytes bought.** A quarter of
  Havlak's allocation is gone and its time is where it was (0.95 with overlapping
  ranges). Round twenty had read Havlak's answer to the server collector as "its time in
  the runtime falls with fewer bytes"; it does not fall with these bytes. What the
  `while` rewrite of `Vector#each` gains (0.79) is a block invocation per element.

What to take from it:

- A change that reuses an object across calls is asked what the object still reaches
  when it is put away: a WeakRef to everything a call touches, a collection, and the
  same with the switch off, is part of its matrix.
- `alloc.rb` is run on all fourteen for every change, not on the benchmarks it is for: it
  is exact, it takes four minutes, and bytes that went up somewhere are a cost with a
  cause even when the time is inside the noise.
- A helper the compiler recognises by name (`RegisterLocalsRewriter`, the screens'
  lists, `ReflectionCache`) has to learn every new variant of it: grep for the old
  name's uses before adding a sibling.
- An upper bound measured by rewriting the benchmark says what the rewrite is worth, not
  what the mechanism is: before building for "the bytes", take the bytes away alone (a
  rewrite that keeps the block and drops the allocation) and time that.
