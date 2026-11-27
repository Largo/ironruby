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
   `hash_slot_for`; in DeltaBlue `Dictionary#at` at `match` (compiled since perf/havlak: see
   Rejected, perf/jitbind, for what the calls on other receivers wait for now); in CD `RedBlackTree#get`
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
   out first what compiles once the screen lets it through. Done in perf/deadconst (see Landed): the
   screen lets `schedule` through and nothing compiles - it is called once per iteration, so it is the loop
   tier's, which still stops at `[osr] emitter: call: is_task_holding_or_waiting/0`; the next step for it
   is calls on another receiver in an outlined loop. What that takes, found in perf/deadconst2 by taking
   the obstacles off a copy of `schedule` one at a time (`IR_OSR_VERBOSE=1`): first the predicate
   `is_task_holding_or_waiting` (`@task_holding || (!@packet_pending && @task_waiting)`, a pure method the
   method JIT would inline, but the loop tier's inliner has no profile of the receiver's class); with
   the loop's receiver in a local `t` it still says `call: is_task_holding_or_waiting/0`, and with the
   predicate written out on attribute readers `call: th_p/0` - an attribute read on a local the `while`
   writes is refused there on purpose (round eight: CD's `Node#successor` walks a few links per entry and
   paid the copy's entry each time; only a core iterator's loop takes it). Behind those, `run_task`, which
   no outlined loop can call: it needs a general call made with the loop's scope (visibility and
   refinements as the source's, gap 1's round seven), and since an outlined loop deopts to the top of the
   iteration from its snapshot, nothing after the call in the iteration may deopt - `@current_task = t`
   is an instance variable write on self, which deopts for a frozen self. So three pieces: a class
   profile for receivers in loops (the live value at entry is one, as `_peekLocal` uses), attribute reads
   on a written local in a `while` that is entered once and runs long (the OSR counter knows it, the
   entry does not), and a general call from a loop with a store after it that cannot deopt. The prize is
   the loop's own dispatch (one predicate call, three attribute reads, two instance variable writes an
   iteration), not `run_task`, which stays a call: at most `schedule`'s 7.5% self time, likely half.
   Not attempted. And two that the rounds
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
   Round thirty-eight looked at the 152 MB (see Landed, perf/nbody): 608 bytes per
   `advance`, of which a method scope (160), two Procs (2 x 80) and two BlockParams
   (2 x 48) were made on every call because a block literal with a literal nested in
   it never reused its Proc, so `advance` never parked its scope. That is gone: 152 ->
   48 MB. The 192 bytes per `advance` left (with the harness's `times` block) are
   Integer boxes (about 96), Float boxes (about 72) and the locals tuple of `advance`
   (about 24; a parked scope gets a new one each call), by type only - which sites make
   the boxes was not traced. Likely: the `times` index past the small-integer cache;
   the copy's exits, which box Integer locals with a plain convert
   (`JitLoopCompiler.Box`), not through the cache; and the `0.01` literal, boxed on
   every call of the generic `times` block (the probe's driver showed one Double per
   call). What the parking saves in bytes it pays in bookkeeping:
   callgrind counts `advance` level and a light nested shape 0.8% dearer, so the
   change is worth what the collector does not have to do, which the time on this
   machine could not show.
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
   Round perf/permute (2026-11-26, on 2e9521f) measured what is left and where (see Rejected): the
   block-loop tier never tries this block (runs of 1-6), and would decline it at `call: swap/2` (no
   bound call in a block copy; `leaf/1` alone says the same) and then at `permute(n1)`, a recursive call
   of a method that has no typed body; and since `swap` writes elements, then `permute` is called, then
   `swap` runs again, any deopt after the first `swap` re-runs it - the effect rule declines the block
   even with both calls bound. The method JIT declines `permute` written with a `while` too, at "a
   deopt after an effect (call permute at line 44)". Yet that `while` version runs Permute in 2.39 s
   against 4.10 s as written (CRuby 2.66 s, `harness.rb Permute 20 100`) as generic code: the gap is
   the call with a literal block. Per call, `__send__`-driven, ns: a plain call 30, the same with any
   literal block - `n.downto(0) { }`, `(n + 1).times { }`, `n.tap { }`, a Ruby method that yields once -
   160-170 (CRuby 62 and 94-133); per iteration of `downto` IronRuby is at CRuby (52 against 59 ns with
   a call in the block). It is not the bytes (360 B a call, 288 of them the scope, Proc and BlockParam):
   parking them for `downto` took them away and no time (below). So the next attempt is the entry of a
   call with a literal block - what the rule, the BlockParam and the block's frame do per call, without
   allocating - which is /perf-frame's kind of work, and which every iterator call pays, not only
   Permute's. Not the loop tier.
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
   Round thirty-seven: it allocates 12.8 MB per iteration again (alloc.rb, 2f8c31c), and
   perf/iter's reuse of a block holding a literal takes it to 2.4 MB (see Landed).
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
   nor a class variable, a `defined?` of a constant or a constant qualified by an expression
   (`@@x`, `defined?(A::B)`, `self.class::C`; `IR_LIGHT_LEXICAL=0`, see Landed, perf/lexical);
   nor keywords beside a rest or post parameter (`def warn(*msgs, uplevel: nil)`;
   `IR_LIGHT_KEYWORD_REST=0`) nor a hash passed on as keywords (`g(*a, **o)`;
   `IR_LIGHT_KEYWORD_SPLAT=0`, both see Landed, perf/lexical2);
   Util/block-given-matrix.rb, Util/light-rest-matrix.rb, Util/block-pass-matrix.rb,
   Util/splat-pass-matrix.rb, Util/interpolation-matrix.rb, Util/hash-literal-matrix.rb,
   Util/masgn-matrix.rb, Util/regex-light-matrix.rb, Util/lexical-light-matrix.rb and
   Util/keyword-rest-light-matrix.rb are their matrices.
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
   Global variables went in in round thirty-seven (perf/lglobal, `IR_LIGHT_GLOBALS`,
   `Util/global-light-matrix.rb`, see Landed): every name but `$~` and `$_`; a global in a body
   of more than 24 calls still keeps its scope (the masgn limit, measured again on
   `String#__ir_split_lines__`).

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
    Round of 2026-11-27 (see Landed, perf/twospeed, not gated): found and fixed - an interpreted shared rule
    left ahead of its compiled one in a site's rules, run on every call to fail (DLR patch 0111). `neq2`
    still varies 59-102 ns on both builds, which is something else.
    Round of 2026-11-27, second (perf/twospeed2, a finding, no code): `neq2`'s spread is the CLR's dynamic PGO,
    not IronRuby's or the DLR's. Alone `neq2` is 60-64 ns in 12 of 12 processes; after `eq1` and `call1` (either
    alone does not do it) 61-104. With `DOTNET_TieredPGO=0` it is 67-75 in 8 of 8, with
    `DOTNET_TieredCompilation=0` 66-71: no slow process and no fast one. The site holds two rules, one a class
    (the negated-builtin `!=` rule is per class, not shared), so every other call misses its target and runs
    `System.Dynamic.UpdateDelegates.UpdateAndExecute3` (dotnet-trace: 20-25% of the loop, the rules 13-19%) -
    .NET's own code, tiered and instrumented, which the calls before it trained: guarded devirtualization of the
    rule delegates it invokes then guesses well (62 ns) or badly (100 ns) depending on what it saw. The rules and
    the loop are dynamic methods, not tiered. Left: the lever is IronRuby's - one rule for both classes (a
    version set, as a shared rule has), so that the site never misses; that is a change to how `!=`/`==` on
    plain objects is bound (#215), for its own step. Bounce, 10 processes each (`harness.rb Bounce 20 1500`,
    median of the last 10 iterations, ms): with DLR patch 0111 473-511 (median 495), without 477-507 (498): not
    bimodal on either, and level.
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
    Round thirty-seven (perf/iter, see Landed, not gated) measured the four parts on one
    element against CRuby: a literal inside a block 111 ns and 302 B against 44 (`n.times {
    o.e { |c| s += c } }`), `each1` 131/77 B against 72, a Ruby-level iterator 125/77 B
    against 55, `upto1` 151/389 B against 70, `any1` 149/374 B against 52, `find1` 292/885 B
    against 66, `each_with_index1` 415/1205 B against 134, `(1..k).each` 120/117 B against
    141. It took the first: a block holding a literal now reuses its scope and the literal's
    Proc as a block without one reuses its scope - 302 -> 46 B (what a call without a block
    allocates), 0.51-0.70 of the time. Left: `any?`, `find` and `each_with_index` (Enumerable's
    C# goes through `each` and a SplattingBlock: the bytes are its own, not the parking's) and
    the literal given to a core iterator outside the parked list.
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
    about the floor for one shape there. Tried and rejected (perf/lsite, see Rejected): a
    site that has answered `LightScopeRequired` remembers it and skips the light site from
    then on - the light site is +7 of the +232 instructions a light `gsub` pays; the rest is
    the late scope's arm in the body and the light frame's exit. Also tried and rejected
    (perf/lsite2): a light method that has made its scope late once forwards, at the top of
    its compiled body, to a scoped twin compiled from the same AST - the twin costs what a
    scoped method does, but the forward costs what it saves (the light lambda's prologue,
    which zeroes its locals before the first test, +158, and the delegate hop, +56).
    Untried: calls reach the twin without entering the light lambda - the method's delegate
    replaced and its rules rebound (not through `MethodsUpdated`, which invalidates all
    compiled code), with a policy for a method that needs its scope only sometimes; or the
    prelude gives such a method's rare path a method of its own, so that the common path
    stays without a splat or a scope-reading callee (`/perf-core`).
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
    **Re-measured after perf/lframe** (perf/regexlight, 2026-11-27, on b887bc1 + perf/lexical
    a8feb83; `Util/perf/callgrind/count.sh`, `ab.rb -i -r 1 -n 20000 Util/bench/regex-shapes.rb`,
    default against `IR_LIGHT_REGEX=all`): the regex literals that go to a method storing the
    caller's `$~` still lose light - `gsub` 12,666 -> 12,887 (1.017), `split` 10,836 -> 11,050
    (1.020), `kase` 8,575 -> 8,843 (1.031), `eqt` 5,767 -> 5,998 (1.040), the controls `size`
    1.000 and `matchp` 0.985. 214-268 instructions a call, the +232 perf/lsite measured: the
    cheaper light entry of perf/lframe did not change what the scope made late and the call
    made twice cost. Nothing was built; the about 800 methods of the census's regex lines stay
    out. A step on them has to make the second call or the late scope cheaper (perf/lsite and
    perf/lsite2 tried the second and were rejected), not the light entry.
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
    perf/struct (see Landed) measured it - 8 JIT invalidations for 30 `Struct.new`s
    beside a hot loop - and fixed it for anonymous classes (0); a reader and the first `new` on a
    new struct class take dispatchers instead of rules (4-9 and 10-25 us under IR_BIND_TRACE).
    `Struct.new(:a, :b)` plus a first new and getter 79.5 -> 51 us (CRuby 5.7); `Struct.new` alone
    26-27 -> 20-21 us a class (CRuby: 4.1-4.4). Left: the class itself (`Struct.new` in C# is about
    three quarters of it, `Class.new(Struct)` 4 us), writers' rules, and a site past the
    dispatchers' budget building a `new` rule per class.
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
    entry points are in `Util/bench/daloze-lessons.md`, section 6. Outside AWFY;
    it is what JSON escaping, `strscan` and log parsing spend.
    Round of perf/strsearch (2026-11-26, see Landed): `Util/bench/strsearch-shapes.rb` (120
    shapes) did not reproduce the regexp half - over 10 KB IronRuby is 25 to 160 times
    *faster* than CRuby (`mp_u_n` 366 ns against 59 us, .NET's vectorized `IndexOfAny`),
    and `match?` and `=~` cost the same there; what was slow was `match?` that finds
    something (it built a MatchData), `#byteindex` (a Ruby helper; 1.1 ms on 10 KB of UTF-8)
    and a BINARY needle (wrong answers); all three are dealt with. **Still open:** `s.index('"')` not
    found in 10 KB is 198 ns against 109 (profile: 59% in `PackedSpanHelpers.IndexOf`,
    the search over 20 KB of UTF-16; the rest is the call) - only a receiver held as bytes
    would close it; `#start_with?` 134 / 212 ns against 55 / 90 (done in round two, see
    Landed: perf/strsearch2); `#byteindex` on UTF-8 found late is 1.3 us against 117 ns
    (the UTF-8 length of the prefix, `Encoding.UTF8.GetByteCount`, 1.1 us over 10 KB of
    mixed text), and from a positive offset other than 0 it walks the characters to it, so a
    strscan-style loop of byte offsets over UTF-8 text is quadratic where CRuby's is not
    (fixed in round four, perf/strsearch4: 79-94 -> 2.4 ms over 100 KB, CRuby 0.35).
    Round three (perf/strsearch3, see Landed) found that StringScanner itself, which does not
    call `#byteindex`, was quadratic on UTF-8, and far worse; that is fixed.
22. **A `begin`/`rescue` or `ensure` in a method costs about 80 ns per call when nothing
    is raised**: `def r(x) begin; x + 1; rescue StandardError; 0; end end` 106 ns against
    CRuby's 24, the same with `ensure` 112 against 27 (`Util/bench/zjit-post-shapes.rb`,
    one loaded run: factors). Gap 9 seen from the caller: no light frame, no typed body.
    A .NET `try` costs nothing on the path that does not throw, so the experiment is to
    find what the method does around the handler (the scope, the saving of `$!`, the
    frame) and let a body whose handler is not entered run as light as one without.
    And **a raise that is rescued costs 6.3 us against 0.64**: a .NET exception per use.
    The method JIT (round of 2026-11-26, see Rejected, perf/jitbody): `Body` alone refuses 30
    methods, not the 461 the census used to say, all cold wrappers whose rescued part is a
    call; a handler as a deopt can take only a call-free protected part (1 method). Left: a
    handler emitted in the typed body, worth it only once a run shows hot methods needing it.
23. **A method made by `define_method` is called at 2.3 times CRuby's time** (64 against
    28 ns; a `def` of the same body 31; YJIT 2-5 ns): the method JIT takes no block as a
    method body. Rails-style code defines most of its small methods this way.
    Tried in round thirty-one (see Rejected, perf/dmeth): without the method JIT the call is
    only about 1.15 times a `def`'s, and a pooled BlockParam and a direct rule bought 1.16-1.2;
    what is left is the JIT taking a block body (a `JitStub` over a `BlockDefinition`).
    Round of 2026-11-26 (see Landed, perf/dmethjit): taken; `bm1` 51 -> 26.5 ns against
    CRuby's 24. Left: bodies over captured locals, `block_given?`/`yield` or `super`, and calls with a
    block or a splat, which still take the rule.
    Round of 2026-11-26, second (see Landed, perf/dmethjit2): bodies that read captured
    locals, and setters called as `o.x = v`, taken (`{ @h[name] }` 69.8 -> 53.3 ns, a `def` 49.7).
    Left: bodies that write a captured local (4 of 154 in the stdlib and gems), `yield`/`super`, and
    the `*args, &block` forwarders.
24. **Struct with `keyword_init: true`: creating an object is 3.2 times CRuby** (1162
    against 369 ns; positional creation is level at 142 against 131). And
    `h.each_pair { |k, v| }` / `each_with_index` are 1.6 to 1.9 times CRuby (gap 13 with
    two parameters).
    Round thirty-four (see Landed, perf/inv2): `Data.new`, `#with` and Data readers are direct;
    the invoice shapes are at CRuby's time. Left: initializers taking keywords (`Product.new(sku:
    ...)`) at about 2x CRuby, `Hash#dup`, and Struct `keyword_init` as above. (`Data.singleton_methods`
    listed `:allocate`, which CRuby's does not: fixed in perf/crubydiff, see Landed.)
    perf/struct (see Landed): Struct keyword creation is stored in C#, `kw_new` 1017 ->
    198 ns against CRuby's 299 (`kw_hash` 147 against 196, `sub_kw` 379 against 356). Left from this
    gap: keyword initializers (`kwinit` 236 against 132) and `Hash#dup`.
    perf/struct2 (see Rejected) measured both in a method's loop rather than the shape file's
    block driver, which inflates them: `Hash#dup` of three keys is at CRuby's time (134-137 ns
    against 144) and was dropped from this gap; `P.new(sku:, name:)` is 1.2-1.5 times CRuby there
    (noisy). In the same loop `String#dup` is 2.4 times CRuby (137-142 ns against 56) and
    `Array#dup` 1.15 times (143-146 against 124): the generic Kernel#dup path, worth a gap of its
    own (`Util/bench/dup-shapes.rb`).
    perf/strdup (see Landed): String#dup without the hooks, as CRuby's, is no faster on the clock
    (100-113 against 104-109 ns, CRuby 55); the gap is elsewhere in the call or the copy.
    perf/dupcost (see Landed): it was the instance-data lookups and a wasted content; `S.dup`
    77 ns against 114 (CRuby 56).
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
    `Dictionary#at` compiles since perf/havlak (see Landed): Havlak's declines are
    `Vector#remove_first` (a deopt after an effect) and `split_bucket` (an attribute write
    on a receiver nothing samples - the same kind of local, `current` walking a chain; the
    write would need what the read now has, a name from a class the call was inlined for or
    from the classes that define the writer).
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
    2026-11-26, perf/startup-library-tools: the startup library holds the tools' files
    (irb, reline, prism's Ruby side, RubyGems' commands and what `gem list` requires: 215
    files, from `Util/aot/startup-files.rb`, which lists what the commands load). R2R host,
    against master 206f2b93, instructions (perf stat, all threads, best of 3): `-e 1` 1,524
    -> 1,513 M, `gem --version` 2,109 -> 1,764 M, `gem list` 6,615 -> 3,860 M, irb (`echo
    exit`) 6,996 -> 4,057 M. Wall, best of 7 alternating, load 20 (not quiet): `-e 1` 885 /
    891, `gem --version` 1425 / 1128, `gem list` 2964 / 2373, `echo exit | irb` 3430 / 2347,
    `echo 1+1 | irb` 4276 / 2339 ms. The first `Prism.parse` after `require "prism"` (the
    `Loader#load_nodes` first call) went from ~1.9 s to ~0.15 s at that load: with
    serialize.rb precompiled, most of what perf/big-method-first-call attacks is gone.
    The library grows from 32.9 to 117.6 MB; a single line table for all files made `-e 1`
    11% dearer and is now one per file. Left: the 91 gemspecs (~245 ms of `gem list`), which
    `Gem::Specification.load` evals with its own binding, so the library cannot serve them.
    2026-11-26, perf/rubygems-boot: what RubyGems costs `-e 1` (master 7bc00726, R2R host;
    `-e 1` 478 ms best of 20, `--disable-gems` ~340). Perf with jitdump, 4 runs, main thread
    inside gem_prelude's `require "rubygems"`: 146 ms CPU profiled (~122-139 unprofiled; the
    file-open timeline puts rubygems.rb -> error_highlight.rb at 139 ms median of 7). By whose
    code runs: rubygems.rb's own top level 72 (of which: the default-spec cache 21 -
    `ironruby_load_default_specs` reading and `Marshal.load`ing 57 KB / 1,484 names 10, the
    glob of 62 gemspecs 6, their stamp 2, the cache file name 2, activation 1; stack walks for
    `require_relative` and `__dir__` 19; the require wrapper chain and loader for ~25 requires
    10), specification.rb's class body 20 (Struct.new, the attribute table), requirement.rb 17
    (Gem::Version constants: `compute_sort_key`), rbconfig.rb 11, specification_record.rb 9,
    platform.rb 8 (Gem::Platform constants), the rest 10. By activity: binding call sites ~53
    (545 outermost binds more than `--disable-gems`, phase timer +50 ms), running compiled Ruby
    and the runtime under it (first calls of R2R methods, type loads) ~70, .NET JIT 20, GC 18,
    kernel 18. Nothing is parsed but kernel_require.rb (eval'd, 4.5 KB, ~5 ms) and the defs
    the method JIT re-parses at their first call (12 ms, `def body` phases). Lazy RubyGems
    (TruffleRuby's lazy-rubygems: `autoload :Gem`, a `require` that loads RubyGems on a
    LoadError) is out: `$LOADED_FEATURES`, `Kernel#require`'s owner and the default gems'
    activation would differ from CRuby's, which loads the same 30 files at boot.
    The fix taken: precompiled code notes where it calls `require_relative` and `__dir__` (as
    it does for const_set and autoload), and those two, now library methods handed the
    caller's scope, pass the file to their Ruby halves; others still walk the stack.
    `caller(0, 1)` from compiled code was a CLR walk of the whole stack, 0.1 ms at the top and
    0.6 ms eighty frames down, ~50 times in `-e 1` and 160 times in `require "irb"` (all now
    noted). Stack-walk samples in `-e 1` 93-106 -> 2-6; allocation 27.6 -> 25.4 MB. R2R, best
    of 10 alternating, every output checked, load 2.3: `-e 1` 488 / 488 (20 rounds: 478 ->
    459, CPU 449 -> 438), `gem --version` 583 -> 550, `gem list` 1078 -> 1032, `require
    "irb"` 1020 -> 1044, `echo 1+1 | irb` 1138 -> 1133 ms; instructions (all threads, best):
    1,515 -> 1,486, 1,766 -> 1,735, 3,893 -> 3,783, 3,862 -> 3,899, 4,171 -> 4,185 M. The
    irb ones are +1%: its walks are gone (-8 M), and one gen0 collection there became a gen1
    (+37 M in `gc_heap`), with 3 MB less allocated. With the next commit (a noted call's
    arguments evaluated before its note, so that `autoload :X, File.expand_path("x", __dir__)`
    keeps its const_source_location), best of 10 at load 15: 1,516 -> 1,485, 1,763 -> 1,730,
    3,799 -> 3,765, 3,852 -> 3,830, 4,126 -> 4,122 M. Left, largest first: binding (gap 26's
    "cheaper first binds"); the class bodies' constants; the default-spec cache, whose map
    could be read on the first `find_default_spec` instead of at boot.
    2026-11-26, perf/require-compile-threshold: the start-up compilation threshold (200, which
    only the core prelude had) now covers every file's top level on the thread that runs it -
    a require, load or autoload, and the main script's or `-e`'s own top level. Requires alone
    took a third to a half of what `-X:CompilationThreshold 1000` saves (`gem list` 3,897 ->
    3,482 M instructions, irb 4,121 -> 3,842); with the main script all of it (3,053; 3,173),
    because `gem` and irb run their commands from the script's top level, not from a require.
    The count is fixed when a lambda or rule is made and calls made while loading count towards
    it, so code that turns hot is compiled 170 calls later and reaches the same steady state:
    a loop started inside a require ran its rounds in 7.2-7.6 ms (master 7.2-7.6), its first
    round 15-20 ms dearer; AWFY geomean 1.004 / 0.991 new/base in the two orders. The price is
    warm-up of medium scripts: a small block-taking call run N times from a script, one process
    each, medians of 6 - N=100 66.7 -> 55.6 ms, 300 86 -> 88, 600 101 -> 109, 2000 155 -> 186,
    20000 223 -> 262. It grows with the value: at 2000 calls `-X:CompilationThreshold` 64
    cost 20 ms, 200 40 ms and 500 75 ms, while 500 saved only 3% more instructions on `gem
    list` and irb than 200; so 200. R2R host, instructions (perf stat, all threads, median of 3)
    master -> this: `-e 1` 1,517 -> 1,516 M, `gem --version` 1,767 -> 1,740, `gem list` 3,897
    -> 3,166, `echo exit | irb` 4,118 -> 3,283, `echo 1+1 | irb` 4,174 -> 3,319; CPU 1430 ->
    1058, 1509 -> 1118, 1549 -> 1121 ms. Wall, best of 10 alternating, load 1.2-2.2 (500, same
    shape): `gem list` 1039 -> 985, irb exit 1103 -> 1033, irb 1+1 1191 -> 1069 ms; at 200, load
    7-28: 1113 -> 1040, 1171 -> 1096, 1206 -> 1117, `require "irb"` 1023 -> 957. Why a
    crossing costs so much: past the threshold the shape still ran at ~50-150 us a call for
    hundreds of calls before its compiled code took over (steady state is 1.7 us); that cold
    stretch, not the IL compile, is the next thing to look at.
    2026-11-26, perf/startup (rejected, 98baf5b): lever 1 of perf/first-bind - keep the spent
    dispatcher answering until the hot call's rule, compiled in the background, lands. Measured
    first (R2R host, base 2f8c31c, `IR_BIND_TRACE`): the lever has shrunk since perf/first-bind
    counted it. `gem list` builds 241 rules (80 ms of 2,942 binds' 183 ms inclusive), irb 268
    (90 ms), and only 43 / 51 of them are hot (bound after a dispatcher spent its budget). The
    rest are first rules with no dispatcher in front. Tried: for a hot call,
    `LibraryDispatcher.TryBridge` builds the rule's lambda as `DynamicUtils.LightBind` does,
    starts its compile at once, and hands the site the spent dispatcher's delegate. The
    dispatcher makes the calls with no budget until the compiled rule lands, then declines.
    The bind after a decline gets the compiled rule once (a thread-static, as the DLR's
    `LastInterpretedFailure`: without that once-only guard, a compiled rule whose test fails for
    the call is handed out again forever, and `gem list` hung). Not during the core prelude,
    whose compiles are held back. A bridge bind costs ~170 us against ~330 for a rule bind.
    Main-thread instructions (callgrind, `DOTNET_TieredCompilation=0`, R2R host) base -> new:
    `-e 1` 2,091.4 -> 2,092.8 M (+0.06%, three runs each, spread 0.1 M), `gem --version` 2,271.8
    -> 2,273.6 (+0.08%), `gem list` 3,447-3,459 -> 3,426-3,433 (-0.4..-1.0%), `require "irb"`
    3,482 -> 3,461-3,466 (-0.5%), irb 1+1 3,709 -> 3,700 (-0.2%). All threads: `gem list` -0.9%,
    irb -0.4%, so starting the compiles earlier costs nothing elsewhere. Wall, best of 10
    alternating, load ~1: 360/352, 410/418, 715/701, 724/719, 782/775 ms (all within +-2%).
    Steady state unchanged: AWFY 8 s DeltaBlue/Json/Richards within 2% (a 3 s run showed
    DeltaBlue and Json 36% slower, which was noise), micro-table no line >5% slower in both
    orders except s_map4, which is equal alone (medians 194.5 / 196.8 ns). Matrices
    (shared-rule-light, hot-shapes, jit, jit-calls) in 7 modes, with `IR_HOT_BRIDGE=0` and
    `IR_CONFIG=R2R`: PASS; hot-shapes-matrix makes 923 bridges. Rejected: 0.2-0.8% on three
    commands, a fixed +0.06-0.08% on the two that never bridge (dispatcher and bind paths;
    not the thread-static, which moving off the common bind path did not change), and ~210
    lines of cross-thread hand-off. Learned: what is left of first-bind cost is the ~200 first
    rules per tool, not hot ones, so levers 2 (`CallInstruction` per signature) and 3 (typed
    sites rebinding what untyped ones warmed) should be measured before anything is built.
    Their perf/first-bind sizes (9-16, 6-16 ms) are as stale as lever 1's 18-21 ms was.
    2026-11-26, perf/startup2 (no change; this split is gap 26's new starting point). Base
    79a2c30. Asked: are perf/first-bind's levers 2 and 3 still there, and where do `-e 1` and
    `gem list` spend their time now. Measured with a throwaway build: `IR_BIND_TRACE` marking typed
    and untyped rules, a stopwatch on uncached `CallInstruction.Create`, timestamps with main-thread
    `JitInfo` at the start-up milestones, and `strace -f --seccomp-bpf -e openat` for file times.
    The machine was ~40% slower than in perf/startup: `-e 1` took 505 ms here, 360 ms there, on
    both trees. Only relative numbers carry over.
    - Lever 3 (typed sites rebinding what untyped ones warmed): gone. `gem list` binds 48 typed
      rules and one of them duplicates an untyped one (0.7 ms). Lever 2 (`CallInstruction` per
      signature): 174 / 263 uncached creates in `-e 1` / `gem list`, 66 / 106 of them through
      MakeGenericType and Activator. 11 / 16 ms all in, JIT included: under 2% of `gem list`.
      `CallInstruction.ReadyToRun.cs` already roots the common shapes. Lever 1: perf/startup above.
    - The first rule of a process (`private`, in the prelude) still costs ~12 ms. Typed rules cost
      ~1 ms each in `-e 1` (22 rules, 25 ms), against ~0.45 ms for untyped ones.
    - Split of `-e 1`, R2R host, ms from `Main` (fastest of three; main-thread JIT ms / methods in
      brackets): to `Host.Run` 9; to the RubyContext ctor 23 (8 / 49); LoadBuiltins 40 (9 / 87);
      to LoadCorePrelude 13; registering the startup library 4; the core prelude with RubyGems
      248 (69 / 495); to exit 16. The runtime starts ~40 ms before `Main`. `--disable-gems -e 1`
      takes 358 ms against 511, so RubyGems is ~150 ms. `gem list`: pre-prelude ~129, prelude
      ~380, then the command ~515 ms. Under strace, the command spends ~285 ms requiring what
      `list_command` needs (spec_fetcher -> remote_fetcher -> net/http, socket, openssl, zlib,
      resolv, securerandom: ~60 files; CRuby requires the same ones), then ~200 ms on the 93
      gemspecs (median 1.3 ms each; the 245 ms perf/startup-library-tools gave them includes the
      requires that happen between them).
    - JIT on the main thread (`System.Runtime.JitInfo`, normal tiering): the R2R host spends
      120-143 ms on 749 methods in `-e 1` and 180-190 ms on 1,015 in `gem list`. Of `-e 1`'s, 334
      are plain Tier0 compiles of BCL generics the framework image lacks (Dictionary and
      ConcurrentDictionary over ValueTuple and over IronRuby types, SearchValues...). 75 are
      "Instrumented Tier0" compiles of large methods (93 KB of code). 31 are IronRuby methods
      compiled FullOpts, and 20 are rules or lambdas.
    - The release image (`Util/package-release.sh`: self-contained, composite, startup profile)
      JITs 216 methods in 59 ms for `-e 1` and 376 in 106 ms for `gem list`. Wall, best of 6
      alternating, R2R host / release: `-e 1` 505 / 375, `gem --version` 593 / 457, `gem list`
      992 / 847, `require "irb"` 1041 / 831, irb 1+1 1112 / 935 ms. So the host the gate's
      start-up rule times (perf-gate step 4) pays ~130-210 ms more than what is released, most
      of it in JIT. A change that moves JIT shows on the R2R host far more than in a release.
    - `DOTNET_TieredPGO=0` takes the instrumented compiles away: main-thread JIT -20 ms in `-e 1`
      on both images, -10 to -17 ms in `gem list`. AWFY 8 s geomean 1.188, NBody -26%, Havlak
      -30%. Rejected.
    - `RubyStackTraceBuilder.FindCopyingClasses` (the table of modules that classes copy in)
      reflects over every type in IronRuby.Libraries the first time a backtrace holds a copied
      module's method: 9 ms once per process in `gem list`, `require "irb"` and irb (not in
      `-e 1` or `gem --version`). It also loads AngleSharp.dll and System.Net.Sockets.dll. In
      `gem list` it is optparse's rescued exception in `Gem::Command#handles?`. The source
      generator sees every `[Includes(Copy = true)]` and could emit the table. That is ~1%, a
      small fix for whoever is nearby.
    No lever reaches 3% of `gem list` on its own. What is left is spread over the prelude and
    RubyGems' own Ruby (binds ~100 ms inclusive in `-e 1`, the running of precompiled code), the
    files `gem list` requires, and the JIT, which the release image already halves. A next attempt
    should measure on the release image, or say why the R2R host stands in for it.
    2026-11-27, perf/startup3 (Landed, not gated): measured on the release image as perf/startup2
    asks. FindCopyingClasses' type scan is now generated attributes, and the default-spec cache is
    validated without a glob or a File::Stat per gemspec. Main-thread instructions -2.0% / -2.0% /
    -1.8% on `gem list` / `require "irb"` / irb, and -2.8% on `-e 1` (release image). What is left of
    RubyGems' boot is its own class bodies (specification, requirement, platform: ~40 ms of `-e 1`)
    and rbconfig (~12 ms).
    2026-11-27, perf/startup4 (Landed, not gated): RubyGems' boot files are all in the startup
    library. Their self time is first binds, two `super` rules, specification.rb's string `eval`
    and the R2R host's JIT. rbconfig.rb no longer uses CLR interop: -0.3 to -0.9% main-thread
    instructions on all five commands. Next leads: the 4,989 CLR member lookups of ruby4.rb in `-e 1`
    (5.9 ms), and eval'd strings in the startup library.
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
    Another, found in round perf/deadconst (2026-11-26) and open on master: a typed method body
    whose `while` folds a constant and has nothing else that may deopt goes on with the old value
    after another thread reassigns it (`x = 0.0; while x < n; return :seen if DF; x += 1.0; end;
    :limit`, warmed with DF false, then DF reassigned 0.1 s into `spin(2.0e9)`: CRuby `:seen`,
    IronRuby `:limit`). Its safe point sees the move, but `MethodLoopSafePointSlow` deopts only a body
    that may deopt (`JitLoopGuard.MayDeopt`), and this one has no deopt of its own (an Int counter's
    overflow check gives most loops one). perf/deadconst makes such a body deopt where it left an arm
    out; for a folded value in general the same (`_canDeopt` for a pure body with a loop that folded a
    constant) is untried, and an impure body would have to be declined for it.
    Done in perf/deadconst2 (see Landed): such a body deopts at its loop's safe point when a constant
    it folded was reassigned, and looks its constants up again there (going on) when it was another.
    What is left of this gap's running-loop faults: a nearer constant appearing (rminor's case above),
    and a method body with an effect that only a method redefinition makes stale, which goes on with
    the old method until it returns (`MethodLoopSafePointSlow`'s impure branch).
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
    Round thirty-seven (see Landed, perf/singleton, not gated): that idea, taken further - a
    singleton class with no modules of its own answers every name it has no entry for from its
    superclass's lookup cache, lock-free, and the shared rule's test reads that cache. The
    profile behind it (callgrind with the CLR's perf map, 300 steady-state `os_app` objects,
    672K instructions each): `TryShareVersion` -> `ResolveShared` and `FillMethodLookup` about
    90K and 65K an object - some thirty fills per object, each walking the ancestors and copying
    the singleton class's whole cache table to file one name, because each definition empties
    it. `os_new` 0.77 of its instructions, `os_app` 0.84.
    Left, measured but not taken: (1) a shared rule is one per (method, receiver type, delegate
    type), so two sites calling one method with arguments of different types share it, and the
    second gets a rule of its own built for each object (~200 us, `IR_BIND_TRACE` "shared-new"):
    OpenStruct's `define_singleton_method!(name, ...)` (a Symbol) and `("#{name}=", ...)` (a
    String) do, 220-240 binds in a process's first ~250 OpenStructs, until the interpreted
    `new_ostruct_member!` is recompiled (also with `-X:NoJIT`), about 45 ms of warm-up. Several
    shared rules per key, the next built when every one holds the version and none matched,
    would end it. (2) The four reader binds per object (a dispatcher per new singleton class,
    gap 19's "first call"), about 5% of `os_app`'s instructions. (3) `Ractor.shareable_proc`
    some 5%, `ModuleOps.DefineMethod` some 11%.
33. **Three differences from CRuby in interpolation**, found by the probes of
    `Util/interpolation-matrix.rb` on the build before perf/interp and left as they were
    (the matrix avoids them); not speed gaps. When a part's `to_s` raises, the backtrace
    names the method's first line for that frame where CRuby names the interpolation's own
    (`def m(x)`, `y = 1`, `"a#{x}"` on lines 10-12: IronRuby 10, CRuby 12), with and
    without a scope. A TracePoint `:line` event is reported for the body of an endless
    `def`, CRuby reports none. And with a refinement of `Symbol#to_s` in use, `"#{:s}"` asks
    the refined `to_s`; CRuby answers `"s"` without asking it.
    Round thirty-seven (see Landed, perf/crubydiff): the endless `def` and the Symbol are fixed, and
    the frame's line already matched CRuby on the branch (with and without a scope); the matrix has
    rows for all three now. Left: in a heredoc, a part that is a local variable (`#{x}` on a line
    below the statement) still names the statement's line - the bridge maps the part to the
    variable's shared node, which carries the declaration's span, so the conversion gets no line of
    its own (it would take a node that carries the part's span, and walker support for it). And a
    parenthesized parameter default (`def f(x = (1))`) is a `:line` event to CRuby, none here.
34. **Two differences from CRuby in Hash literals**, found by the probes of
    `Util/hash-literal-matrix.rb` on the build before perf/hashlit and left as they were
    (the matrix leaves them out, with a comment); not speed gaps. A key that is an
    instance of a String subclass is copied and frozen (`k = MyS.new("k"); h = {k => 1}`:
    `h.keys[0].frozen?` is true and it is not `k`); CRuby 4.0 keeps `k` itself, unfrozen,
    and copies only a plain String. The same with `h[k] = 1`. And `defined?({a: 1.nope})`
    is `"expression"` where CRuby says nil, as both say for `defined?([1.nope])`.
    Closed (see Landed, perf/crubydiff): the String subclass key is fixed; `defined?` already said
    nil on the round-37 branch. The matrix has rows for both now.

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
    too.
    The stackless form this entry listed as untried was already there (2026-11-26, perf/extiter
    found it): `Enumerator.cs`'s ExternalCursor walks Array#each / each_index / reverse_each /
    each_with_index, Hash#each / each_pair / each_key / each_value, Integer#times / upto /
    downto, an Integer Range, String#each_byte / each_char, Struct#each and each_slice /
    each_cons over those, while the method is the unredefined one, with no fiber; a `next` there
    is 67-94 ns against CRuby's 226-234 (`Util/bench/extiter-shapes.rb`). Its end was the cost
    left - a StopIteration thrown from C# took the catch-site walk, 22 us against CRuby's 0.8 -
    and perf/extiter (Landed) defers that walk; perf/extiter2 (Landed) gives `map`, `collect`,
    `select`, `filter`, `find_all` and `reject` cursors of their own, and perf/extiter3 (Landed)
    `with_index`, `each_with_index` and `with_object` on an enumerator, and Enumerable#
    each_with_index over a Hash, a Range or a Struct, and perf/extiter5 (Landed) Hash#select,
    #filter and #reject, `filter_map`, `flat_map` / `collect_concat` and `each_with_object`.
    Still on a fiber, 5-16 us of process CPU per `next` against 0.25-0.40: any of these over a
    source with no cursor (an IO, a custom #each, a generator), `Enumerator::Lazy`,
    `Enumerator.produce` (generators of the prelude, which run Ruby code between two values: a
    cursor would call blocks under its lock) and `Enumerator.new`. A raw fiber round trip is 2.3 us of wall clock and 4.7-6 us of CPU (two
    threads spinning), so the prelude's machinery around it is not where the time is.
    **An abandoned fiber-backed enumerator** costs 1.1-1.6 ms of process CPU to make and take one
    value from (starting its thread: 316-482 us of wall clock warm, a new OS thread each, as
    perf/fiberstart found; one that finishes reuses a pooled thread, 13-25 us), and its thread and about 60 KB stay until the handle's
    finalizer unwinds it (FiberRegistration, ThreadOps.Fiber.cs). That needs nothing on the
    fiber's side to reach the handle, and a blocked thread's stack is a GC root: since
    perf/extiter4 (Landed) a lazy made elsewhere, or whose blocks are Symbol#to_procs, is
    reclaimed as an Enumerator's generator is, but a block that runs on the fiber and closes over
    a scope holding the enumerator (`e = xs.lazy.map { |x| ... }; e.next`, the same with
    `Enumerator.new { |y| ... }`) keeps it for the life of the process: 10,000 of them are 10,018
    threads and 640 MB after GC (CRuby: 2 threads, 17 MB; `Util/bench/extiter-abandoned.rb`). Only
    a fiber that is not a thread gets past that. Untried: switching fibers without a thread (a
    continuation of the method JIT's own); a smaller stack for fiber threads (the memory, not the
    thread).
    Two differences from CRuby in `with_index` / `with_object` enumerators, with or without a
    cursor, left as they were (the matrix leaves them out, with a comment): MRI's enumerator calls
    the source's method itself, so a singleton #each on the source enumerator is not called (here
    it is), and it calls #with_index by name, so redefining Enumerator#with_index after the
    enumerator is made changes it (here the generator it made stays).
    A trap found on the way: a local in the prelude's #with_index that held the enumerator it
    returns kept every abandoned one alive - the generator block closes over the method's scope,
    its fiber's thread is a root - and 2,000 of them ran the process out of memory
    (`IR_FAST_NEXT=0`); the enumerator is returned without a local.

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
    Closed (see Landed, perf/crubydiff): both are fixed. Found on the way, left: CRuby's copy of a
    singleton class makes `initialize`, `initialize_copy` and `respond_to_missing?` private in the copy;
    and an explicit `obj.method_missing(:w)` after a private call's error says "private method 'w'
    called" in CRuby (its method_missing_reason is never cleared), "undefined method" here.

38. **A keyword call into a method with a rest parameter, and the keyword prologue, outside
    the method JIT** (perf/lexical3, measured with `Util/perf/callgrind/count.sh`, instructions
    a call, shapes in `prelude-cost.rb`; the method JIT specializes a trivial keyword method,
    so these are the costs of every keyword method it does not take). A rest method given a
    keyword call pays about 1,400 more than given the same Hash positionally:
    a rest method `def r(*a) = a.size + 1` called `(i)` 2,559 (`kr_plain_rest`), and
    `v_hash_into_rest` called `(i, 2)` 2,638, `(i, KH)` 2,762,
    `(i, {k: 2})` 3,416, `(i, k: 2)` 4,815 - and a keyword method with a rest the same
    (`def v_unused(*a, k: 1) = 3` 4,798, `kr_rest` 5,325), so it is the call into the rest,
    not that method's prologue. Without a rest parameter the keyword call costs what the
    literal does (`def v_two(a, b)`, JIT off: positional 1,745, `{k: 2}` 2,404, `k: 2`
    2,435). The first suspect owned it: `RubyOps.NormalizeRestArgument` copied the hash to
    unmark it; perf/kwrest (Landed) unmarks the call's own, 4,814 -> 3,688, about 270 left over
    the literal. A keyword method with a rest never normalized, so `v_unused` and `kr_rest`
    did not move: theirs is the prologue's cost below. Separately, the keyword prologue of
    `def kw(a, k: 1) = a + k` costs about 1,300-1,500 with the JIT off (4,017 against
    `v_two_kw` 2,435) in either lowering. Not the splat lowering: see Rejected, perf/lexical3.
    The prologue's parts (perf/kwprologue, Rejected, JIT off): the separation checks 242, the
    `unless KeywordArgumentsMatch` statement 380-640, each read 160-210 - 850 for
    `def p_opt(a, k: 1) = a` called with the keyword left out, against 1,904 for the same
    method with a plain optional. Folding the checks into one call saved 62. The method JIT
    takes such methods and pays none of it (1,507); what pays is a keyword method it declines
    (`**o`, `p_rest` 3,906). Unexplained: why one runtime call with four or five arguments
    costs 350 or more in the interpreted body. A profiler that names the DLR interpreter's
    frames (perf with jitdump, where perf_event_open is allowed) is the next tool.

39. **A String is not safe under a concurrent writer either** - a correctness gap, like gap 20 for
    Array and Hash, found by perf/strrace (2026-11-27). `Util/string-race-matrix.rb` races 36 readers
    against 7 writers that flip a string between known states (replace, an append and its removal,
    slice! and prepend, setbyte and back, an insertion and its removal, force_encoding there and
    back, encode! there and back); a reader must answer what it answers on one of those states.
    CRuby: every row ok. IronRuby (6956e4e, 20 runs): 101 of 252 rows are not always ok - every
    kind of reader: `end_with?`, `getbyte`, `each_byte` and `unpack1` raise IndexOutOfRangeException;
    `split` and `sum` RangeError; `hash`, `rindex`, `to_sym` and `upcase` ArgumentError; index,
    rindex, `<=>`, hash, chars, count, scan, inspect, upcase, byteslice, size and valid_encoding?
    answer what no state held; and in 30 rows the *writer* raises (force_encoding or encode!:
    "character U+0000 can't be encoded in ASCII-8BIT"), because a reader changed how the string is
    held under it. Three causes, each in every reader, so no reader-by-reader fix is sound:
    (1) readers read the content's array, start and count unheld while a writer shifts the array in
    place (Remove, Insert) or replaces it - the slice copy was the one reader that held it (perf/strcow);
    (2) a reader that switches the representation (SwitchToBinary/SwitchToChars, from getbyte,
    bytes, unpack, index of a byte string...) converts unheld and publishes with SetContent, so a
    write made meanwhile is lost or decoded in the wrong encoding - readers corrupt the string;
    (3) `_encoding` and `_content` are separate fields read separately, so a reader can pair the new
    bytes with the old encoding. The design that would fix it, not started: every content read
    held (ContentState.Enter, one compare-exchange, the cost perf/strcow measured for a slice:
    +1.4% instructions, +5-8% time on a short one), with operations on two strings (==, <=>,
    include?, index of a String) holding one and copying or snapshotting the other - never both,
    or two threads comparing a == b and b == a spin forever; a representation switch made holding
    the old content and published only if it is still the owner's (compare-exchange on
    `_rawContent`), every writer re-checking after it holds that its content is still current; and
    the encoding kept with the content (or read under the same hold). A decision about what
    IronRuby promises, as gap 20 is, before an experiment.
    perf/strswitch (see Landed) took cause 2: no write is lost to a reader's switch and no
    writer fails on one (21 and 25 rows of 252 on master, 0 and 0 now, 20 runs). Causes 1 and 3
    remain: 89 rows of readers that answer wrongly or raise.

## Landed

- **Gate of 2026-11-27 for round 4 of round thirty-seven** (perf/strdup, perf/strsearch2,
  perf/extiter2 and 3, perf/lframe, perf/lexical2 and 3, perf/kwrest, perf/jitscreen and 2,
  perf/deadconst2, perf/permute, perf/havlak, perf/dmethjit2, perf/jitsettle; the rejected
  perf/struct2, perf/lsite2 and perf/startup's hot-rule bridge, ledger and tools only): the branch
  `claude/happy-gauss-95wm2j` at 15285eb against master f81fc19, Release net10.0, four cores: **PASS**.
  IronRuby.Tests (`parallel-tests.sh`): 1,352 tests, every one executed, no failed or flaky name (the
  script's INCOMPLETE is the driver's `Current culture: ` banner, as at round thirty-seven's gate).
  `gate-matrices.rb --all`, 171 matrices in 11 modes (the six default ones, and `IR_FAST_DUP`,
  `IR_JIT_REST`, `IR_JIT_UNSAMPLED`, `IR_KWREST_IN_PLACE`, `IR_LIGHT_ENTRY` off): 1,502 same, 353
  baseline, 26 DIFF, each explained. callable-parameters-matrix (11): `method(:puts).to_proc`'s prelude
  line moved again (CRuby says nil). thread-backtrace-matrix (3): the main thread's line 87 or 88 by
  timing, 88 alone on both trees. caller-matrix's R2R row m8b: the R2R host leaves out Kernel#eval's
  frames on both trees when run alone (master healed by timing in the sweep); a defect of the R2R
  host's own, handed to perf/crubydiff. fiber-reclaim-matrix (11): its native-enumerator probe took
  `each_with_index` over a Hash, which perf/extiter3 gave a cursor, so there was no fiber left to
  reclaim; the probe now takes `combination(2)` (15285eb), and the matrix is then the same as the
  baseline's in all 12 modes. The older switches (`IR_LIGHT_GLOBALS`, `IR_LIGHT_LEXICAL`,
  `IR_LIGHT_KEYWORD_REST`, `IR_LIGHT_KEYWORD_SPLAT`, `IR_JIT_DEADCONST`, `IR_FAST_SEARCH`, `IR_FAST_NEXT`,
  `IR_FAST_STRUCT`, `IR_JIT_DMETH`) over the 49 matrices of the code they touch: 440 same, 50 baseline.
  Probes for the gate's questions (a million deep through the new frames, visibility around
  define_method bodies, captured locals of a making method parked or called again, `$!` around the
  deferred StopIteration) print CRuby's output with the JIT on and off; 2,000 abandoned enumerators
  leave no more threads or memory than on master.
  ruby/spec, all five suites (32,285 examples): no failure the known list does not name.
  Census against master: light frames 91.7% -> 94.1% of methods (658 up, none down), method JIT
  compiles 11.6% -> 11.9% (131 up, none down).
  AWFY, 8 iterations: all fourteen verify. Timing at load about 1: micro-table in both orders, no line
  more than 5% slower in both (`arr` 1.07 / 1.00, `cmp` 1.13 / 1.00, `nil?` 1.14 / 1.01, one order
  each). AWFY 12 in both orders: geomean new/base 0.989 and 1.013, none more than 5% slower in both
  (Richards 0.996 / 1.039; Havlak 0.95 / 1.12, which follows the order). Alone, five rounds in each
  order, medians new/base: NBody 1.023 / 1.005, List 1.004 / 1.000, Bounce 0.996 / 0.993.
  Start-up on the R2R host, best of ten alternating, two runs, base -> new ms: `-e 1` 509 -> 528 and
  534 -> 562 (1.038, 1.052), `gem --version` 579 -> 598 and 632 -> 613, `gem list` 1073 -> 1056 and
  1052 -> 1098, `require "irb"` 986 -> 1036 and 1031 -> 1054 (1.050, 1.021), irb 1+1 1129 -> 1166 and
  1180 -> 1169. None more than 5% slower in both runs, but `-e 1` is 4-5% (20-28 ms) slower in both:
  open, for perf/startup3 and 4 (round 5) to win back, and measured again at round 5's gate.

- **perf/strswitch (2026-11-27, not gated) - a String's representation switch cannot lose a write**
  (gap 39's cause 2; switch `IR_SAFE_SWITCH`). A switch between characters and bytes (a reader's
  getbyte, bytes, index, unpack... or a writer's) now converts holding the content it switches
  from and publishes the new one only by compare-exchange on `_rawContent` against it
  (`MutableString.PublishSwitch`, from `Content.WrapContent`); the callers that stored the result
  unconditionally (`SwitchToBytes`, `SwitchToCharacters`, `Transcode`) no longer do. Every write to
  a char or byte content checks, once it holds it, that it is still the string's
  (`EnterCurrentWrite`, `IsCurrent`) and otherwise makes the write on the current content.
  `ForceEncoding` sets the encoding holding the byte content it switched to, if still current and
  still bytes, or switches again (`Content.TrySetOwnerEncoding`). `Util/string-race-matrix.rb` now
  also reports a lost write (the string a writer leaves is not the state its flips began from).
  20 runs each: master 106 of 252 rows not always ok, 21 where the writer raised and 25 with a
  lost write; this 89, 0 and 0. The 89 are cause 1 and 3 of gap 39 (readers reading unheld): under
  encode! 29, slice! front 19, insert 17, replace 11, force_encoding 7, append 6; IndexOutOfRange
  24, ArgumentError 18, RangeError 7, the rest wrong answers. Single-threaded instructions
  (`callgrind/count.sh`, `Util/bench/strswitch-shapes.rb`): each write +41 (append 2,022 -> 2,063,
  setbyte 1,697 -> 1,739, insert and slice! 3,695 -> 3,765), force_encoding there and back 4,139
  -> 4,318, index after a switch 9,853 -> 10,092, getbyte and bytes that switch +41 and +46; size
  and plain level. Checks: strcow, dup, string-search and hot-shapes matrices in every mode and with
  the switch off: PASS; spec/core/string 3,963 examples, the one known try_convert wording error on
  master, here and switched off; 60 MutableString, String and Encoding IronRuby.Tests: PASSED.
  Left: writes into the array a caller got (`EnsureCapacity`, `GetWritableByteArray`, for buffers
  and native code) are outside this; a writer's multi-step change that sets content and encoding
  unheld (`Replace`, the end of `Transcode`) is safe against a reader's switch only because its
  last store is unconditional.

- **perf/strcow (2026-11-27, not gated) - a String slice copied while the content is held; a
  long tail slice shares the array, as MRI's** (gap 24's dup note). Asked for: copy-on-write content
  for dup, `+` and slices. Found: it is there already - CharArrayContent and BinaryContent share
  their array on Clone and on a long slice and copy on the first write (ContentState, the
  `MutableString_CopyOnWrite*` tests): a dup of 10,000 characters costs what a dup of 10 does (108
  ns), `+` is CRuby's time (97 against 106), a short slice too (95 against 101). What was left:
  (1) a slice that is copied read the array unheld, so `s[1..]` racing `s.slice!(0, 1)` tore (the
  string's last character several times; `Util/strcow-matrix.rb`'s slice! rows differed from CRuby
  in 20 runs of 20). Copies are now made holding the content (`GetSlice` enters it once and decides
  there): a short slice +40 instructions (2,877 -> 2,917), 92 -> 97-100 ns. (2) A tail slice,
  which MRI shares (rb_str_subseq, SHARABLE_SUBSTRING_P) whatever it keeps alive, was copied
  unless it kept half the array: `Utils.IsSharedSlice(length, count, toEnd)` shares a slice of 256
  or more that runs to the end, for String#[] and #slice only (`Content.GetSlice(start, count,
  shareTail)`: #slice! removes the slice next and would then copy the whole array to write it).
  `IR_SHARE_TAIL=0` turns (2) off. `LONG[5000..]` of 10,001 characters 23,772 -> 3,505
  instructions, 1,580-1,613 -> 151-157 ns (CRuby 130); a middle slice is copied as in MRI and stays
  1,550-1,620 (CRuby 487: half the bytes, a char is two) - a per-encoding byte store is the next
  step there, not sharing. Controls level: dup, `+`, size, plain. Checks: `Util/strcow-matrix.rb`
  (new; threads included) prints CRuby's output, 20 runs of 20 (master: 0 of 20); strcow, dup,
  string-search, hot-shapes, jit and jit-calls matrices in every mode and with the switch off:
  PASS; spec/core/string 3,963 examples, the one known try_convert wording error on both; the 43
  MutableString and String IronRuby.Tests: PASSED. Left: other readers of a content (index, each
  char, the byte views) still read it unheld.

- **perf/dupcost (2026-11-26, not gated) - `String#dup` of a bare string: one look for instance
  data, and the copy constructor** (gap 24's dup note). Where the time went, by callgrind with the
  CLR's perf map over one `str_dup` section of `dup-shapes.rb` (instructions per call, the
  shape's driver included; the scratch profiler kept the section's files and named JIT frames
  from /tmp/perf-PID.map): the bare test 553 - `HasInstanceVariables` and `GetImmediateClassOf`
  each looked the string up in the ConditionalWeakTable of instance data, through the general
  `TryGetInstanceData` - and the copy 706: `CreateInstance` 375 (a new string with an empty
  char-array content, zeroed) and `Replace` 331 (`SetEncoding` 120, the content's clone 106),
  which threw that content away. Not the library call. Now `RubyContext.HasNoInstanceVariablesOrSingleton`
  looks once, straight in the table for a String, and the copy is `MutableString.Clone()` (the
  copy constructor: content clone, encoding, character flags - what CreateInstance().Replace left).
  Tried first and dropped: a per-runtime flag "no String has instance data yet" - always set, since
  the Loader puts `@gem_prelude_index` on the load path's strings at start-up.
  Instructions (`callgrind/count.sh`, N=20000): `str_dup` 3,290 -> 2,418 (over `plain`'s 1,266:
  2,024 -> 1,152); `str_clone`, `hash_dup`, `clone`, `obj_dup`, `oi_dup`, `arr_dup` level (one
  `obj_dup` outlier on the base, 6,130, as seen before). Time in a method's loop, three alternating
  runs: `S.dup` 97-113 -> 76-78 ns (CRuby 56); in reverse order 114-117 -> 77. `S.clone` read
  139-192 -> 193-220 when run after `S.dup` in one process, but alone (129-132 on both, three runs
  each) and in reverse order (207-208 against 198-209) it is level: an artefact of the order of
  shapes in one process - which may also be what sank perf/struct2's Hash#dup (see Rejected).
  Checks: dup, hash-dup, string-search, hot-shapes, jit and jit-calls matrices in every mode and
  with `IR_FAST_DUP=0`: PASS. spec/core/string with the Kernel dup/clone specs: 4,010 examples,
  the one known try_convert wording error on both builds. Not measured, since the call was not
  the cost: other no-argument library calls (`s.size`, `a.first`, `h.empty?`). Left: the copy
  allocates two objects (the string and its content) where CRuby shares the buffer; a
  copy-on-write content would take the next ~350.

- **perf/strdup (2026-11-26, gated 2026-11-27 (round 4's gate entry)) - `String#dup` is String's own, MRI's rb_str_dup_m** (gap
  24's dup note). Kept as a CRuby-compatibility fix, not as a speed-up. `Util/dup-matrix.rb` (new: String,
  Object and Array dup and clone - content, encodings, broken bytes, frozen and chilled, ivars,
  singleton classes, subclasses, a refinement, TracePoint, and the hooks redefined in String, Object
  and Kernel or by prepend and include, then restored) showed master differing from CRuby in six
  rows. In CRuby `String#dup` belongs to String: a bare string (class String itself, no singleton
  class, no instance variables) is copied without calling initialize_dup or initialize_copy, even
  redefined; any other goes through rb_obj_dup. `MutableStringOps.Duplicate` does that now, so the
  matrix prints CRuby's output (switch `IR_FAST_DUP=0`: every string through Kernel#dup, as before).
  Instructions (`callgrind/count.sh`, `dup-shapes.rb`): `str_dup` 3,688 -> 3,291; `str_clone`,
  `hash_dup`, `clone`, `obj_dup`, `oi_dup` (an object with ivars), `arr_dup` and `plain` level.
  Time in a method's loop, three alternating runs: `S.dup` 104-109 ns on master, 100-113 here, CRuby
  55 - no gain; controls within the noise. So the hooks were not what String#dup spends: the 50 ns
  over CRuby lie in the call of the library method and the copy (CreateInstance, Replace's content
  clone), still to be found. Checks: dup, hash-dup, struct, hash-struct, jit and hot-shapes matrices
  in every mode and with the switch off: PASS (dup-matrix `same`, `baseline -2` switched off).
  spec/core/string and kernel dup/clone: 4,010 examples, the one known try_convert wording error
  on both builds. Edge for the gate: the bare test is no instance variables now and no singleton
  class, so a string whose last instance variable was removed counts as bare and skips its hooks;
  MRI's FL_EXIVAR may stay set after the removal, which would send it through rb_obj_dup.
- **perf/forward (2026-11-27) - a rest parameter passed on where nothing can see it again is the callee's
  rest parameter; a block-passing call's hoisted splat is fresh** (gap 36 and gap 16's second part;
  `IR_FORWARD_REST=0` turned the first off) - timed, not gated. **The first part is off by default since round 5's
  gate (2026-11-27), behind `IR_FORWARD_REST=1`**: `Util/forward-rest-matrix.rb` (new, from the gate's probes) reads
  the rest parameter again after the call by names ForwardedRest's walk cannot see - `instance_eval("args")`,
  `send(:eval, "args")`, an alias of `eval` (any call may ask for the frame's scope), a TracePoint's binding at
  `:return` - and each saw the callee's `a << :x` (`[1, 2, :x]`, CRuby `[1, 2]`). It is sound only where nothing
  runs in the frame after the call (the method's tail, no ensure) and no TracePoint is on, which it does not check
  yet; the hoisted splat of a block-passing call (the second part) is a copy and stays. Bytes a call
  (`Util/bench/forward-bytes.rb`, new), base -> new: `def w(...) = raw(...)` 212 -> 116,
  `w(...) = rest_t(...)` (a callee with a rest) 309 -> 116, `w(*args, &b) = raw(*args, &b)` 227 -> 132,
  with a rest callee 312 -> 120, `w(*args) = rest_t(*args)` 214 -> 120, `w_splat(1) { }` 420 -> 325;
  `w(*args) = raw(*args)` (one Array, the rest itself) 119 -> 118.
  Two copies went. (1) `f(*a, &b)` - and `n(...)`, which is `n(*?fwd?, &?fwdblk?)` - hoists its arguments
  before the block argument (MRI's order) and expanded the splat into a new Array there that the site
  did not call fresh, so a rest callee copied it again; it is fresh now (`MethodCall`, the hoisted
  `#splat`), and a splat that is fresh already is not copied at all. (2) `ForwardedRest` (new, the
  method's AST before its body is transformed): the one splat of the method's rest parameter that may
  call its Array fresh - the only reference to the parameter (no other read, assignment or
  `defined?`), not in a loop or a block, and nothing that can reach the frame: no block literal (a
  Proc's `#binding`), no `binding`/`eval`/`local_variable_*` call, no zsuper, no `retry`/`redo`.
  `Arguments` sets the site's fresh flag for that splat only. By the same rules (a prism count) it
  takes 25 of the prelude's 137 methods with a rest or `(...)`, and 216 of 744 in `ironruby/` and
  `ruby/4.0`.
  Instructions a call (`count.sh`, `Util/bench/prelude-cost.rb`): `w_splat` (`*args, &block`) 6223 ->
  5742 (0.92), `w_fwd` 6163 -> 5613 (0.91); controls `w_pass`, `w_anon`, `w_opt` move by +-70 between
  runs in both directions (0.986-1.014), the counter's floor here. Time (`ab.rb`, six rounds):
  `w_splat` 0.87, `w_fwd` 0.84, `w_pass` overlapping.
  Checked: `Util/splat-pass-matrix.rb` section 10b (new, 21 rows: a rest callee changing and keeping
  the Array, two calls keeping two Arrays, a chain of forwards, the rest escaping or changed first, a
  binding, a loop, a block literal, a block passed on, `...` with keywords and a block and without,
  ruby2_keywords with keywords and with a Hash positional, send, public_send, `super(*a)`, zsuper,
  method_missing, an empty rest), block-pass, light-rest, keyword-light, block-given: CRuby's output
  and the unchanged build's with the JIT on, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls`, and the unchanged
  build's with `IR_LIGHT_FRAMES=0`, `IR_FORWARD_REST=0` and `IR_SPLAT_REST=0`; hot-shapes the unchanged
  build's in every mode (its known row against CRuby). ruby/spec language/send, method, delegation,
  super: the same on both builds (send's one error is the known `into`/`to` wording).
  For the gate: the callee may keep the caller's rest Array; the analysis is what says the caller
  cannot see it again - read `ForwardedRest`'s list of what can reach a frame. A block-passing call's
  retry (Ruby 1.8's `retry` from the block) would run the call again with the hoisted Array; prism
  refuses `retry` outside a rescue, so it cannot be written.

- **perf/timeout (2026-11-27) - `Timeout.timeout` wakes its watcher thread only when it has to**
  (`Src/StdLib/ironruby/timeout.rb`, an override that loads CRuby's file as it is and redefines two
  methods of `Timeout::State`) - timed, not gated. A raise rescued through `Timeout.timeout` cost
  152-164 us against CRuby's 3.1 (perf/ensure's finding). Taken apart with copies of timeout.rb
  (each with one thing left out; no profiler: this container refuses perf_event_open and the
  sampler could not be installed): without the `ensure request.finished` 21 us, without the
  watcher's signal 57-60, without the handler's `rescue ExitException` 66, without the Proc's
  `return` 62; and by thread (`Util/bench/timeout-shapes.rb`'s measure): with the signal 145 us of
  process CPU of which 100 on the calling thread, without it 57 and 57. So each call's
  `@condvar.signal` wakes the watcher, whose iteration (drain the queue, `min_by`, `reject!`, wait
  again) then runs beside the raise: ~45 us of its own CPU and ~43 more on the caller. Pinned to one
  CPU the difference shrinks (72 against 54): interference between the two threads, not the
  watcher's work alone - not found further without a sampler (no contended Monitor entries:
  `Monitor.LockContentionCount` 0 per call; no gen-0 collections).
  The watcher needs a signal only when it would otherwise sleep past the new request's deadline. It
  now publishes the deadline it waits for (`@watcher_deadline`, under `@queue_mutex`, while in the
  condition variable's wait), and `add_request` signals when the new deadline is earlier, when the
  watcher is not in that wait (in `@queue.pop`, which the push wakes, or between waits, where it
  checks the queue under the mutex), or when the queue has 64 requests. A request not signalled for
  is due no earlier than what the watcher wakes for, and it drains the queue then: every timeout
  fires when CRuby's would. Without a raise the watcher usually finds the request finished, drops it
  and blocks in `@queue.pop`, which every push wakes as before, so that path is unchanged.
  Timing (`ab.rb`, six rounds, the calling thread's CPU, ns): raise 53,328 -> 32,954 (0.62); no raise
  2,765 -> 3,146 and the bare raise control 4,753 -> 4,750, both overlapping. Process CPU of a raise,
  two runs (`Util/bench/timeout-shapes.rb`'s measure, us): 164 and 135 -> 76 and 69. Callgrind gives
  nothing here: valgrind runs one thread at a time, so the interference cannot appear, and the
  baseline's arm failed under it.
  What is left of the raise (57 against a bare 8): `Error.handle_timeout`'s `rescue ExitException =>
  e` (a class list by constant: a catch, a backtrace, then `raise` again; a frame like it alone 29 us
  against 8.6 for a core class) and the Proc's `return yield` inside begin/ensure.
  Checked: `Util/timeout-matrix.rb` (new, 28 rows: firing in sleep, a busy loop, IO and Queue#pop,
  nil/zero/negative, a custom class and message, the backtrace, ExitException not taken by a bare
  rescue, a raise in the block with its cause and `$!`, nested both ways, a short timeout after 2,000
  long ones and inside a long one with 200 queued, two threads, one watcher thread after 10,000
  calls) prints CRuby's output on the unchanged build and on this one (with the JIT on, `-X:NoJIT
  -X:NoOSR`, `-X:NoJITCalls`, `IR_LIGHT_FRAMES=0`); ruby/spec library/timeout (7), core/thread/raise
  (77) and core/kernel/sleep (15) pass on both.
  For the gate: Timeout's own code is CRuby's and is loaded as it is; a timeout gem installed over it
  is not overridden (the override is found first on the load path only for `require "timeout"` of
  the stdlib copy).

- **perf/jitaref (2026-11-26) - `a[i]` in a typed body after some Array has a singleton `[]`**
  - trace only, not gated (the census cannot see it: it loads no library). The side finding of
  perf/jitconst: app-shapes declined `OptionParser.top` (`@stack[-1]`) and `.base`, and rejected
  `Gem::Requirement#none?`, all at "[] on a RubyArray". The typed body takes `recv[i]` with any
  Integer index already (negative from the end, out of range nil: `JitRuntime.IndexInt`); in
  isolation `top` is specialized. What turned it off is CSV: `csv/fields_converter.rb` defines
  `def NO_QUOTED_FIELDS.[](_index)` on one Array, which puts `[]` in Array's
  `ClrSingletonMethods`, and `HasPlainInstances` then says no for the class for the rest of the
  process - every program that requires `csv` lost Array element reads in every typed body whose
  receiver is not a typed Array parameter.
  Now, while `Array#[]` is the public library method and Array has no adopted instances, a class
  that only lost `HasPlainInstances` compiles the read with `IndexIntChecked` / `IndexLongChecked` /
  `IndexObjChecked`: for an exact `RubyArray` receiver they ask whether this object has a singleton
  class - the generic site's own test (`RubyOps.IsClrNonSingletonRuleValid`, an instance-data
  lookup) - and deopt if it has, so the generic body dispatches to its method; any other receiver
  goes to the existing helpers. A singleton defined after the compile moves Array's method version,
  which the body's entry checks. A refinement of `Array#[]`, a redefinition, a subclass's own `[]`
  and an adopted instance are refused or deopt as before.
  Traces: app-shapes specialized 117 -> 119 (`OptionParser.top`, `OptionParser.base`), declined
  after the pre-screen 30 -> 28; Json, Richards, DeltaBlue, CD and Havlak (3 x 10) the same on both
  builds. `census.rb -d` 0 methods changed. `Gem::Requirement#none?` now stops at its second
  obstacle, `@requirements[0] == DefaultRequirement` ("== on a receiver of unknown type"): Array#==
  calls `==` on the elements (Ruby code), and an identity shortcut (true for the same object,
  deopt otherwise) would deopt on every requirement that is not the default; left.
  Instructions a call with CSV loaded (`Util/perf/callgrind/count.sh`,
  `Util/bench/jit-aref-shapes.rb`, new): `@a[-1]` 2336 -> 1778 (0.76), `@a[i]` 2582 -> 2021
  (0.78); `x[0]` on an Array parameter 2116 -> 2116 (the typed parameter path, unaffected), the
  control `@a.size` 1523 -> 1523.
  Checked: `Util/jit-aref-matrix.rb` (new, 24 rows: reads before and after one Array gets a
  singleton `[]`, from bodies compiled before and after it, negative and out-of-range indexes, a
  frozen Array, `dup` (no singleton) and `clone` (singleton kept) of the special Array, a singleton
  `method_missing`, a subclass's own `[]`, Hash, String and nil receivers, an Array given its
  singleton after the body compiled, a refinement, `Array#[]` redefined and restored), and jit,
  jit-calls, jit-scan, osr-array and csv: CRuby's output and the unchanged build's with the JIT on,
  `-X:NoJIT -X:NoOSR` and `-X:NoJITCalls`, and the unchanged build's with `IR_LIGHT_FRAMES=0`;
  hot-shapes the unchanged build's in every mode with its known one-row difference from CRuby.
  For the gate: an instance-data lookup per read for Arrays once the class has a singleton `[]`
  anywhere - the cost the generic site pays in the same state; the other element methods (`[]=`,
  `fetch`, `size`...) still use the class-wide test.
- **perf/twospeed (2026-11-27) - an interpreted rule stuck before its compiled one runs compiled
  (gap 12, DLR patch 0111)** - not gated. What decides the speed: `recv != other` over four or eight plain
  classes is served by IronRuby's shared rule (one rule whose test is a set of class versions, handed to
  every site that calls the method on another class, RubyCallAction). `DynamicUtils.LightBind` makes it
  interpreted; past its count it compiles in the background and then fails its test once compiled, so that
  the site binds again and the binder hands out the compiled one (`LastInterpretedFailure`). The site got
  the compiled rule another way (the shared rule replaced under it), and in some processes the interpreted
  one stayed ahead of it in the site's rules: on every call of three of the four classes it ran in the
  interpreter to fail, and the compiled rule behind it matched. Which order a process lands in depends on
  when the background compile comes back, so on timing: `IR_BIND_TRACE=1` and `IR_PHASE_TIMES=1` made
  every run fast (0 of 32 slow). Found with `dotnet-trace` (sampled, `--show-child-io`): the slow
  process spent 53% of the loop in `UpdateAndExecute3 -> LightLambda.Run4 -> Interpreter.Run`, the fast one
  0.3%; and with counters in a copy of the DLR: 5.25 million failures of a compiled-ready rule in the slow
  processes, 50-800 in the fast ones, the same number of rebinds in both.
  - The change (Build/dlr-patches/0111-interpreted-rule-runs-compiled.patch): the interpreted rule keeps
    its `LightLambda`, and once it has failed for its compiled target 4,096 times more than the site has
    bound again and picked that up, it runs the compiled target itself (`LightLambda.UseCompiled`) and
    matches. 32 was tried first and made a 60-class site 18% slower: its rules, found in the binder's cache
    again and again, switched too, and the cache then held rules behind a `LightLambda` Run; at 4,096 only a
    stuck rule switches.
  - Ten processes each (`Util/bench/twospeed-shapes.rb`, ns, base / new): `neq4` slow
    (221-237) in 5 of 10 on base, 0 of 10 new (78-85); `call4` 3 of 10 / 0 (85-100); `neq8` 7 of 10
    (187-403) / 0 (102-122); `call8` 7 of 10 (178-264) / 0 (109-124). Level: 1, 2 and 3 classes, `==` at
    every count, `neq60` (median 668 / 661) and `call60` (704 / 704). `neq2` varies 59-102 on both builds:
    another effect, left.
  - Instructions (`Util/perf/callgrind/count.sh`, N=20000): `neq1`, `call1`, `neq2`, `neq4` 1.000; `call4`
    1.019 (+53); `neq8` 0.725, `call8` 0.856, `neq60` 0.666, `call60` 0.328 (in so few calls the rules a
    megamorphic site keeps meeting are still the interpreted ones).
  - Matrices, CRuby against base against new, with the JIT on, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls` and
    `IR_LIGHT_FRAMES=0`: shared-rule-light, jit, jit-calls and dmeth-jit identical to CRuby; hot-shapes
    identical to base (its one row that differs from CRuby differs on base). IronRuby.Tests: 240 named
    (rules and sites, Method*, Super*, Alias*, Proc*, Block*, define_method, instance_method, the JIT) pass.
  - For the gate: the patch is the DLR's, so a checkout of `../dlr` needs it applied by hand (`git -C
    ../dlr apply Build/dlr-patches/0111-interpreted-rule-runs-compiled.patch`); every interpreted rule in
    every program is affected. AWFY was not timed; Bounce was once seen bimodal (gap 12's note).

- **perf/jitprobe (2026-11-26) - the stub's generic cost is not the interpreter's** - not gated. A probe
  (`JitStub.Probe`) times the generic body to decide whether an entry that deopts pays its way. It waited
  for the generic body's compile for at most 8 deopt windows, which go by in microseconds when every
  other call deopts, and then timed the interpreter: 4 us a call where the compiled body takes 0.16. Its
  next probe is 100 ms on, and until then that figure kept the entry: `x + 1` with an Integer and a Float
  in turn, in a method of its own, deopted on every other call for about 1,000 windows (some 15,000
  deopts at 2 us). Right after the compile a probe still read 1.75 us (the CLR's first-tier code; 0.12 at
  100 ms).
  - The change: a cost timed before the compile is marked (`_genericCostInterpreted`) and forgotten when
    `GenericCompiled` fires, which lets the next window probe again. And a probe taken while the generic
    body is not compiled, or compiled 100 ms ago or less, takes none of the six probes and lets the next
    come 10 ms on rather than 100 (at most 16 of them): the cost is the least any probe read, so it comes
    down to the warm figure early. A stub with a warm generic body probes as before.
  - Real runs (IR_JIT_VERBOSE=1: app-shapes, `gem list`, all 14 AWFY at `harness.rb B 5 10`): two methods
    were first probed on an interpreted generic body. `Element#length` (AWFY List) deopts on every call
    (16 in 16): the base keeps it for the whole run on a 62 us figure; the new probes twice more, reads
    8.7 us and retires it - an entry that deopts on every call never pays. `Gem::Dependency#runtime?`
    (`gem list`) is retired on both. Otherwise the traces specialize and decline the same (264, 64).
    (Havlak, Mandelbrot and NBody report "incorrect result" at that inner size on both builds and on CRuby:
    they verify only at their own.)
  - Time (`Util/bench/jit-probe-shapes.rb`, a method of its own per section, 100,000 calls; `ab.rb -r 6`,
    ns; base, new): `fresh_mixed` 875 -> 277 (0.32), `fresh_int` (never deopts) 87.0 / 96.7 (overlap);
    `Util/bench/jit-settle-shapes.rb` all level (0.96-1.01, overlap). `warm_int`, a method defined once,
    run after the fresh shapes in the same process, reads 42.7 -> 78.7: alone it is 82.4 / 83.9, and after
    0.8 s of other work first 43-46 / 50-59 - the base spends half a second more in `fresh_mixed`, in
    which the CLR tiers up the code `warm_int` runs too. Not a cost of the change.
  - Matrices, CRuby against base (8fde498) against new, with the JIT on, `-X:NoJIT -X:NoOSR`,
    `-X:NoJITCalls` and `IR_JIT_REST=0`: jit, jit-calls, jit-const, dmeth-jit identical to CRuby;
    hot-shapes, inline and int-matrix identical to base (the rows that differ from CRuby differ on base:
    as in perf/jitsettle). IronRuby.Tests: the 20 named for the JIT pass.
  - For the gate: a stub whose generic body is still interpreted now probes more (up to 16 short
    probes of 32 timed calls each in its first 100 ms after the compile); `GenericCompiled` now takes
    the stub's lock on the light compiler's thread.
- **perf/fiberstart (2026-11-27) - what a fiber's start costs, found; the end of an iteration on a
  fiber raised from C# and its stack walk deferred, as a cursor's is (gap 35)** - not gated. Base
  8d7efda (the round-38 branch with perf/extiter5). Measured first (`Util/bench/fiber-start.rb`,
  new: one kind per process, blocks of 500, wall and process CPU, threads), on this four-core
  container: a raw CLR thread started and joined is 120-150 us whatever its stack (0, 256 KB, 1 MB,
  8 MB; a C# probe), 150-220 us with 0-3,000 threads alive; Ruby's `Thread.new { }.join` 130-260
  us, one left waiting (`thread_park`) 285-412 us - so the 41-58 us of #213 was another machine. A
  Fiber made is 2.3 us; one that finishes runs on a pooled thread (`fiber_complete` 13-25 us,
  2,000 pool hits of 2,000); one left suspended takes a new thread, 316-482 us after warm-up,
  1.2-1.5 times a waiting Ruby thread's, and keeps it: without a GC.start 3,000 of them were three
  GCs and 3,017 threads, and reclaiming them (GC.start every 100 or 250) bounded the threads at
  117-267 but cost more per enumerator (802-838 us against 578: the unwinding), since a reclaimed
  thread beyond the pool's 8 ends. No change there: a thread for a suspended fiber is the price
  while a fiber is a thread. What a drained fiber-backed enumerator cost beyond its fiber
  (`gen_drain` 206 us against a fiber's 24) was mostly its end: __iter_stop__ raised a
  StopIteration Ruby code had made and kept (the cause of later ones) with Kernel#raise, which
  takes the catch-site walk (73 us of it). Now __iter_stop__ calls `Enumerator#__iter_raise_stop__`
  (C#), which makes and throws it as CursorStep does - unkept, the first kept in the enumerator and
  exposed (ExposeCatchSite) when handed out as a cause, forgotten by #rewind - unless
  StopIteration's #new or #initialize is the program's (MakesUnkeptException), when the prelude
  makes it as before, since MRI's rb_exc_new calls them. Both paths now show the StopIteration to
  a TracePoint on :raise (`TracePoint.OnException`, as Hash#fetch's KeyError): the cursor's was
  never shown (base: none of CRuby's three events), the fiber's was by Kernel#raise. Medians of
  4,000, base -> new: a generator's ending `next` 57 -> 28-33 us, a lazy's `loop { e.next }` end
  53-57 -> 25-27 us, the first `next` unchanged (65-75). `ab.rb -r 6`, ns, base / off / new:
  `gen_end` (`next` again after a generator's end) 32803 / 29684 / 7929 (0.27 of off), `lazy_one`
  (a one-element lazy drained) 119422 / 114263 / 70466 (0.62), `gen_one` 1.04 overlapping (its
  start dominates), the cursor shapes level with base. Still open, the cursor path's own
  difference from CRuby: with StopIteration#initialize redefined its cause is another object
  (base too; the fiber path takes the prelude's way then and matches CRuby). `Util/extiter-matrix
  .rb` (329 probes, 17 new: result, causes and their backtraces with the first end dropped in a
  frame that has returned, loop, rewind, dup, an error in the generator, the cause inside a rescue,
  TracePoint on both paths, threads, a redefined #initialize) prints CRuby's output on new; base
  differs in the cursor TracePoint probe (fixed here), off in `flm_to_ary_next` only (perf/extiter5).
  enum-next-, block-loop-, raise-, full-message- and enum-matrix the same on base, new and off.
  spec/core/fiber, enumerator, thread and kernel/loop_spec: 1,026 examples, 0 failures in all
  three; the Enumerator*, Lazy*, Loop*, Fiber*, Thread*, Exception* and Raise* IronRuby.Tests pass
  in five plain runs on base and five on new (no abort, no failure).

- **perf/extiter5 (2026-11-27) - Hash#select / #filter / #reject, `filter_map`, `flat_map` and
  `each_with_object` take `next` on a cursor (gap 35)** - not gated. Base 1fca3c7 (the round-38
  branch at b887bc1 with perf/extiter4). Hash's own #select, #filter and #reject copy the
  receiver when the iteration starts, as IDictionaryOps.Filter and MRI's rb_hash_select do (a
  plain Hash with its compare_by_identity, the entries with their hash codes), walk the copy with
  key and value as the two values of a step, and drop a pair the fed answer does not keep: the
  copy is StopIteration#result. The prelude's Enumerable#filter_map (the true answers),
  #flat_map / #collect_concat (an answer's elements when it is an Array or Array.try_convert
  makes one, MRI's rb_check_array_type; the answer itself otherwise), and #each_with_object (the
  memo beside each step, `IndexCursor`), and Array's own #each_with_object, over any receiver
  whose #each a cursor walks, while they are the methods registered with `__iter_cursor_method__`.
  #flat_map's conversion runs Ruby code, so a cursor whose answer does (`ResumesWithRubyCode`)
  resumes before the cursor's lock is taken, on the owning thread, where MRI's flat_map_i
  converts; an error ends that iteration and the next #next starts again (next_init), and a
  conversion that calls back into the same enumerator raises FiberError "attempt to resume the
  current fiber", as MRI's does (the fiber path answers instead: the base differs from CRuby in
  that one probe). `next`, `peek`, `next_values` and `peek_values` take a second call-site
  storage for the conversion: a storage holds one site. A trap on the way: the handler around
  that Resume, written in CursorStep, made every StopIteration a cursor throws 15% slower
  (`ary_small` 1251 -> 1435, `rescue_end` 3853 -> 4483 ns, eight rounds) - a handler in the
  method an exception is thrown from cost about 600 ns a throw - and in a NoInlining method of
  its own it is level (1.01, 0.99, 1.02). `IR_FAST_NEXT=0` leaves them to the fiber. `ab.rb -r
  6`, ns per `next`, CRuby / base / off / new: a Hash's `select` with `feed` 314 / 14113 / 14310
  / 257, `reject` 279 / 10871 / 9841 / 194, `[1000].filter_map` 240 / 7743 / 5305 / 105,
  `flat_map` 312 / 8820 / 5663 / 132, `each_with_object` 255 / 5419 / 5746 / 155, a Hash's 340
  / 10319 / 9376 / 228; the earlier rounds' shapes level with base (`lazy_next` 0.96 of off;
  `generator` read 2.23 of off after shapes that use a fiber only when switched off, and 0.93 of
  base alone). Abandoned after one `next` (`extiter-abandoned.rb`, 10,000 each): 1.04-1.31 ms
  and a thread each (reclaimed after GC: these were detached Enumerators already), 330-340 MB ->
  8-20 us, no thread, 139-147 MB. `Util/extiter-matrix.rb` (312 probes, 50 new: results with and
  without feed, the result Hash's class, default and compare_by_identity, a frozen receiver, the
  receiver changed during the iteration, peek, next_values, rewind, loop, with_index, to_ary, a
  #to_ary that answers nil, raises, returns an Integer or calls back in, method_missing, a
  subclass's, a module's and a singleton's method, threads, size, inspect) prints CRuby's output
  on new; base and off differ from it in the call-back probe only. enum-next-, block-loop-,
  hash-order-, raise-, full-message-, enum- and enumerable2-matrix the same on base, new and off.
  spec/core/enumerator, hash, enumerable and kernel/loop_spec: 1,541 examples, the same one error
  in all three (Hash.try_convert's `into`/`to`); 117 named IronRuby.Tests pass (three verbose
  runs).

- **perf/extiter4 (2026-11-26) - an abandoned lazy enumerator's fiber is reclaimed: its #next walks
  a copy (gap 35)** - not gated. Base 3bf570d (the round-37 branch at 79a2c30 with
  perf/extiter3). Measured first (`Util/bench/extiter-abandoned.rb`, new: 10,000 enumerators
  abandoned after one `next`, threads and RSS after three `GC.start`s): every lazy kind kept its
  thread for good on the base - 10,018 threads, 634-715 MB - where a generator Enumerator made in
  a method was reclaimed (18 threads). The fiber of an Enumerator gets a detached copy to run
  (`__enum_iteration_source__`), so nothing on the fiber's thread reaches the enumerator, its
  @__fiber__ or the handle, and the handle's finalizer unwinds the thread; a Lazy, an Enumerator
  subclass, got itself, and the thread's `source.each` kept it alive. Now an Enumerator::Lazy that
  is nothing more (no singleton class, no subclass) and whose #each is the prelude's
  (`Enumerator.__iter_detachable_lazy__`, the prelude's Lazy#each registered with
  `__iter_cursor_method__`) gives its fiber a copy made with `Lazy.allocate` and the seven
  instance variables Lazy#each reads. Base -> new, threads and RSS after GC: a lazy made in a
  method 10,018 / 639 MB -> 18 / 325 MB, a lazy of Symbol#to_procs in a local 10,018 / 709 ->
  18 / 344, one made in a method and held in a local 10,018 / 715 -> 18 / 333. Not reached, and
  not reachable while a fiber is a thread: a block that runs on the fiber and closes over the scope
  holding the enumerator (`e = xs.lazy.map { |x| ... }; e.next`; `Enumerator.new { }` alike),
  10,018 threads before and after. `IR_FAST_NEXT=0` gives the lazy itself, as before. Time per
  enumerator unchanged (1.1-1.4 ms, the thread's start); `ab.rb -r 8`, alone, `lazy_next` 0.98 of
  off, `generator` overlapping (1.30 and 1.48 in a run of six shapes, after shapes that use a
  fiber only when switched off). `Util/extiter-matrix.rb` (262 probes, 40 new: next / peek /
  rewind / loop / feed over every Lazy operation, chunk's deferred block, Lazy.new, eager, a
  singleton #each, a subclass, instance variables set after the copy, frozen, dup, threads, an
  exception in the block) prints CRuby's output on base, new and off; enum-next-, block-loop-,
  hash-order-, raise-, full-message-, enum- and enumerable2-matrix the same on all three.
  spec/core/enumerator, hash, enumerable, fiber and kernel/loop_spec: 1,729 examples, the same one
  error in all three (Hash.try_convert's `into`/`to`). 125 named IronRuby.Tests (Enumerator*,
  Enumerable*, Lazy*, Loop*, Fiber*, Thread*, Hash*, Backtrace*, Exception*, Raise*, Rescue*)
  pass in five verbose runs on base and new and four plain runs of new. One more plain run of new
  aborted on an unhandled ThreadInterruptedException in a new thread's start (RubyThreadInfo.Lookup,
  interrupted by Thread#raise's delivery), and one of four plain runs of base failed
  `Fiber_ThreadRaiseInSwitchBookkeeping1`: Thread#raise around thread and fiber starts is flaky on
  both.

- **perf/crubydiff (round thirty-seven, 2026-11-26) - differences from CRuby, not speed** - not
  gated; one commit each, every one with CRuby 4.0's output as a registered IronRuby.Tests case.
  - `Class#new` handed a library `initialize` (Struct's, Array's) the caller's scope, whose
    refinements then reached it: `Util/struct-matrix.rb`'s `UsingRefine.make` (after 200 calls, when
    the rule was compiled) and an Array subclass from the first call. The inner site now takes no scope
    (`CallArguments.GetBoundCallSiteArguments`). struct-matrix: CRuby's output in full. Refinements16.
  - `Data.singleton_methods` listed `:allocate` (gap 24), and so did ArithmeticSequence's and
    Continuation's: the prelude's singleton `allocate` gave way to `Class#__ir_undef_alloc__`, MRI's
    `rb_undef_alloc_func` (`RubyClass.UndefineAllocator`, checked in the allocate rule of that class
    only). Prelude_DataUndefinedAllocator1.
  - `frozen << x` raised Encoding::CompatibilityError (the three lines of hot-shapes-matrix that
    differed from CRuby), `<< -1` / `<< 2**80` RangeError, `prepend(1)` TypeError: CRuby checks
    frozenness first. The checks are on the error paths only (MutableString.MutateAppend when the
    encodings clash, Append(int) before ToChr). hot-shapes-matrix: CRuby's output. Left: `frozen <<
    Object.new` is TypeError (the binder converts first), `tr!` checks frozenness before the
    encodings where CRuby does the opposite. String_AppendFrozenFirst1.
  - A String subclass's instance as a Hash key (gap 34): stored as it is, unfrozen, as CRuby's
    RHASH_STRING_KEY_P has it (SetHashElement and the JIT's HashStoreObj). hash-literal-matrix: two
    new rows, baseline before; a third for `defined?({a: x.nope})`, already same. Hash_StringSubclassKey1.
  - A private or protected method's call reaching BasicObject#method_missing through a user's
    `super` (gap 37): the visibility now travels with the method_missing reason
    (`RubyOps.SetMethodMissingReasonVisibility`, a MethodInfo field, not a new [Emitted] helper), and
    the error says "private method 'x' called". One more thread-static write per call that reaches a
    user's method_missing. SuperAndMethodMissing3.
  - Module#dup / #clone (gap 37): the members are copied in IDuplicable.Duplicate, before
    initialize_copy, as MRI 4.0's init_copy does; Module's and Class's own initialize_copy are gone
    (CRuby's owner is Kernel). ModuleCopyBeforeInitializeCopy1.
  - An endless def's expression is no TracePoint `:line` event (gap 33): the bridge adds it with
    Statements.NoLine unless it holds parenthesized statements; a rescue modifier there keeps no line
    either. interpolation-matrix: new row, baseline before. TracePoint_EndlessDefLine1.
  - `"#{sym}"` under a refinement of Symbol#to_s is the name while Symbol#to_s is the built-in
    (gap 33, vm_objtostring). interpolation-matrix: new row. Refinements17.
  - `__callee__` in a define_method body called through an alias answered the definition's name
    (found by perf/lframe): an alias call runs a copy of the method's lambda that carries the name
    (Proc.CalleeName in its rare state; RubyLambdaMethodInfo.GetLambdaFor), in the rule and the
    first-call dispatcher; such a call skips the method JIT's stub. light-entry-matrix (claude/perf-lsite)
    "define_method body by alias": CRuby's output. DefineMethodCallee1.
  Not fixed: `42.compare_to(o)` in inline-matrix's `tree_int_key` answers Int32#CompareTo's
  "Object must be of type Int32." where CRuby has no such method. That is IronRuby's interop
  (RubyModule.IsUnmangledBuiltin: snake_case CLR names on Integer and Float, `5.to_string("X")`,
  which ClrTests asserts), not a leak; matching CRuby means dropping those names, or mapping them
  only on request. Already matching on the branch when the round began: refinement before prepend on
  Integer#+ (f7679a1), the frame line of a raising interpolation part, `defined?({a: x.nope})`.
  Checked per fix (see each commit): the named IronRuby.Tests and the ruby/spec files around each
  change on both trees, results as before (refine_spec 38/0, prepend_spec 55/0, spec/core/module
  1050 examples with the 4 known errors, struct, data, class, kernel dup/clone, string, hash,
  tracepoint, coverage, basicobject, nomethoderror, language def/method/send/private/protected).
  For the gate: the two changes on dispatch-adjacent paths - Class#new's rule for a library
  initialize (no scope argument now, so a light-scope site's `Struct.new`/`Array.new` rule no longer
  sets ScopeRead) and method_missing's reason - and Module#dup/#clone, which now copies before
  initialize_copy (anything that relied on Module#initialize_copy copying, e.g. a re-run
  initialize_copy, gets Kernel's).
  Second round (2026-11-27, on b887bc1): the causes of the gate's "baseline" rows. All matrices,
  as built, this branch against b887bc1: lines differing from CRuby 14,255 -> 414, baseline
  matrices 28 -> 19 (chr, format-c, format-c-bytes, format, pack, unpack, ungetc, encode,
  string-methods, value-object, hash-literal, interpolation, hot-shapes, light-entry, struct now
  same). One commit per cause, each with CRuby 4.0's output as a registered IronRuby.Tests case:
  - Integer#chr and String#<< of a code above 0xFF in a single byte encoding: "invalid codepoint
    0x100 in US-ASCII" (CRuby 4.0's onigmo), and UTF-32 from 0x80000000 up invalid too; "out of
    char range" stays for << onto BINARY/US-ASCII and argument-less chr. chr-matrix 9,011 lines.
    Integer_ChrSingleByte1 (Integer_ChrMultibyte1's expectation regenerated from 4.0.10).
  - %c of such a code: "invalid character" (BINARY and US-ASCII keep "out of char range").
    format-c and format-c-bytes, 4,682 lines. Sprintf_CharacterSingleByte1.
  - Math.log(x, base) as rb_math_log (pole first, log2 quotient plus the shifted bits, a size_t),
    atan2/hypot converting the second argument first, Math.sqrt of a Complex. math-matrix 97 -> 37.
    Math_LogBaseAndOrder1.
  - A respond_to? that claims to_ary/to_str/to_hash without the method, default method_missing:
    no conversion (rb_check_funcall), in the binder's rule and the prelude's try_convert.
    array2 19 rows. Protocol_RespondToWithoutMethod1.
  - Integer#fdiv of a coerced result as a Float; Integer#remainder (dividend for an infinite
    divisor, Bignum coercing with remainder); Float::INFINITY.divmod's "Infinity"; Rational#coerce's
    class name and Numeric#divmod's modulo first. coerce-matrix 22 -> 4. Four tests.
  - Kernel#Integer: sign-only strings "invalid value" before the radix, message via rb_inspect;
    Rational()/Complex() naming true/false. numeric-conversion 18 -> 2. Two tests.
  - sprintf directives cut short: "malformed format string", after fetching %1$ / %<name>'s
    argument. format-matrix 16 -> 0. Sprintf_MalformedEnd1.
  - Array#pack floats take a Numeric only (rb_to_float); unpack's "@" at the end counts 1;
    String#undump passes \s and \0 through; String#encode's options keywords only; String#tr!
    checks encodings before frozenness (a leftover of the first round). Five tests.
  Left, with the reason: the "from nil to integer" / "gives NilClass" wording ruby/spec pins the
  other way (array2 63, math 37, numeric-conversion 2); `x << 2**64`, a NoMemoryError in CRuby
  (coerce 4); 42.compare_to (deliberate interop); ffi-matrix (CRuby does not exit 0);
  regexp-binary-ctype's ISO-8859-1 column (25 rows: the regexp translator would need Onigmo's
  ISO-8859-1 ctype table beside its ASCII one); emacs-mule (CRuby's own oddities); strftime
  (%:% and friends), strptime, caller and thread-backtrace frames, marshal, converter and
  io-conversion messages, stdre's URI regexp, eval's regexp warning, Enumerator::Lazy's prepended
  helper in its ancestors, int-matrix's (2**100).equal?, the heredoc part's frame line and
  `frozen << Object.new` (the binder converts first) - each a cause of its own, one or a few rows.
  For the gate: callable-parameters-matrix shows a DIFF only because ruby4.rb's lines moved
  (method(:puts).to_proc.source_location; CRuby says nil on both); Kernel#Integer(String) now
  takes the context (one more argument on that fast path); the binder's conversion rule for an
  overridden respond_to? resolves to_xxx and method_missing at build time.

- **perf/strsearch4 (2026-11-27) - the ASCII scan's cost of round three found and mostly removed;
  `#byteindex`/`#byterindex` from an offset other than 0 walk from the last search, through a map
  shared with StringScanner (gap 21, round four)** - not gated. Commits 8d030d1, 103c741 on
  `claude/perf-strsearch`, based on claude/happy-gauss-95wm2j b887bc1 with d3b8844 merged in.
  1. Round three cost an ASCII scan word by word over 10 KB (`ss_words_a`) 2.6 to 3.4% more
     instructions, most of it also with `IR_FAST_SEARCH=0`. Found with a per-function count:
     callgrind with `DOTNET_ReadyToRun=0` (so that the library's code is JIT'd and named in the
     runtime's perf map), the dumps of `count.sh`'s sections kept, their JIT addresses named from
     `/tmp/perf-PID.map`, the least section of the tree before round three (b887bc1) against round
     three's. Of the 6.7 M instructions a section had gained, 4.9 M were the check every scan makes
     four times a token: the map getter (1.8 M), `StaticsHelpers.GetNonGCStaticBase` for the
     switch's static (1.7 M; the switch-off arm paid it too, which is why turning the switch off
     did not take the cost away), `MutableString.IsAscii()` (1.4 M), the encoding; the rest the
     write barrier bucket (1.1 M), `MatchRegex`, `ByteLength`, `ClearMatch`. The getter now tests
     `HasByteCharacters`, one flag that answers for a subject known to be ASCII, and leaves the
     switch, the encoding and IsAscii to the slow path: `ss_words_a` 13.62-13.73 M (before round
     three) / 13.97 M (round three) / 13.78-13.89 M now, +0.4 to 1.2% (the tree before round three
     counts 0.8% apart from one process to the next). What is left is the getter's call (1.1 M a
     section; the JIT does not inline it under `count.sh`'s settings, AggressiveInlining or not) and
     `HasByteCharacters`' (0.37 M; AggressiveInlining on it changed nothing and was not kept).
  2. `#byteindex`/`#byterindex` with a String needle on valid non-ASCII UTF-8 walked the characters
     from the start to the offset on every call and counted the bytes of the answer's prefix: a
     loop `pos = s.byteindex(",", pos + 1)` was quadratic. The scanner's map moves to
     `Builtins/Utf8OffsetMap.cs`; for the searches it is shared by the readers of one snapshot of
     the string's characters, in a ConditionalWeakTable keyed by the snapshot (as MutableString's
     character index tables are); its UTF-8 length is counted only for an offset from the end; an
     offset of 0 or none makes no map. Loop over 10 KB / 100 KB of UTF-8, base -> new (CRuby):
     2.8 / 79-94 ms -> 0.23-0.29 / 2.4 ms (0.034 / 0.35); the ASCII loop 0.28 / 2.64 ms on both.
     Callgrind, instructions a pass over 10 KB, ref (before round three) / base / new:
     `bidx_loop_u` 63,835,104 / 63,835,196 / 3,279,668; `bridx_loop_u` 64,766,966 / 64,768,009 /
     4,333,019; controls `bidx_loop_a` 2,325,945 / 2,308,458 (one other base run 17.0 M, not
     repeated) / 2,310,130, `bidx1_a_n` 9,500 / 9,499 / 9,501, `bidx1_u_e` 4,629 / 4,628 / 4,635,
     `ss_words_u` 2,943,413,146 / 15,023,760 / 15,217,309 (+1.3%: the scanner's map is the shared
     class now, whose length and pair flag are computed lazily), `size` unchanged.
  Checked: `Util/string-search-matrix.rb` (60,164 rows) gains the scanner's subject changed between
  scans (setbyte, `[]=`, insert, replace, force_encoding, a stray byte appended) and
  byteindex/byterindex loops and offset sweeps over long ASCII, UTF-8, astral, BINARY and US-ASCII
  strings with a change midway; it prints exactly what the base prints, switch on and off; the base
  differs from CRuby in 115 rows (the 110 of round three and 5 new ones: the scanner after its
  subject was replaced by a shorter string or edited inside a character). `csv-matrix`,
  `erb-escape-matrix`: CRuby's output on base, new and off. spec/core/string (3,963),
  spec/library/stringscanner (240), spec/library/csv (33): the same on base, new and off, 0
  failures (the one `String.try_convert` error on all three).

- **perf/startup4 (2026-11-27) - RubyGems' boot files are precompiled already; rbconfig asks the host
  instead of CLR interop (gap 26)** - not gated. Base c7cb16e (15285eb with perf/startup3).
  Asked: are specification.rb, requirement.rb, platform.rb, specification_record.rb and rbconfig.rb
  compiled at every boot? No. The startup library (`Util/aot/startup-files.txt`, written by
  `Util/aot/startup-files.rb` from what `-e 1`, the gem commands and irb load; built by
  `Util/aot/build-startup.sh` in the R2R publish and in `package-release.sh`) holds all 214 files a
  boot loads, and `IR_STARTUP_DLL_TRACE=1` reports none stale. What a boot still parses is the method
  JIT's re-parse of 11 `def`s, and specification.rb's string `eval` (line 1937, 906 chars, which
  defines set_nil_attributes_to_nil and set_not_nil_attributes_to_default_values).
  What their self time is (per-file load timer extended with PhaseTimer's bind and CLR-lookup counters,
  R2R host, `-e 1`, ms; self = time in the file's own code, nested loads excluded):
  | file | self | .NET JIT | binds | CLR lookups | rest |
  |---|---|---|---|---|---|
  | requirement | 13.7 | 5.9 | 3.8 (50) | 0.1 (99) | 4.0 |
  | specification | 12.7 | 0.9 | 3.1 (101) | 0.2 (381) | 8.7 |
  | rbconfig | 11.0 | 2.0 | 5.9 (34) | 1.4 (19) | 1.7 |
  | platform | 8.5 | 3.0 | 2.3 (25) | 0.1 (68) | 3.2 |
  | specification_record | 5.5 | 0.4 | 0.7 (17) | 0.0 (48) | 4.4 |
  - Clock marks in specification.rb's class body: the `eval` ~3.7 ms (the process's first eval);
    INITIALIZE_CODE_FOR_DEFAULTS (strings built with `inspect`) ~2.9; `Time.now.utc` ~1; the
    @@attributes partition ~1. The ~15 ms around `@@default_value` is `Gem::Requirement`'s autoload,
    requirement.rb itself.
  - requirement.rb's JIT is BCL generics that the R2R host lacks: ConcurrentDictionary over
    ValueTuple<int, RubyCallSignature> (8 methods), span helpers, and the members of
    `IronRuby.Classes.Object$2`, a CLR type emitted at run time (2-3 such types per command, 10-28 JIT'd
    methods). The release image precompiles the generics.
  - The two dearest binds are `super` rules: Gem::Version.new 1.2 ms, Gem::Platform.new 0.7 ms.
  - rbconfig.rb's binds are CLR interop: Assembly.get_entry_assembly.location 1.0 ms, get_entry_assembly
    0.5, Path.get_directory_name 0.4, Environment.Version / Major / Minor 0.2-0.3 each, plus their
    reflection lookups.
  Taken (f6a75af): rbconfig.rb reads the entry assembly's directory and the runtime's major.minor from
  `IronRuby.__host_config__`, a private singleton method. Its bindir is a UTF-8 string, as
  System::String#to_s gave it (a first try with EncodePath gave US-ASCII). rbconfig's self time
  ~11 -> ~7-7.9 ms; its binds 34 -> 28, 5.9 -> 2.2 ms; its CLR lookups 19 -> 16 at ~0 ms.
  Main-thread instructions (callgrind, TieredCompilation=0, base and new side by side), M:
  | command | release image | R2R host |
  |---|---|---|
  | `-e 1` | 1,162.3 -> 1,151.8 (-0.90%) | 2,012.3 -> 2,001.8 (-0.52%) |
  | `gem --version` | 1,337.2 -> 1,325.3 (-0.88%) | 2,273.7 -> 2,263.5 (-0.45%) |
  | `gem list` | 2,242.6 -> 2,234.1 (-0.38%) | 3,378.4 -> 3,369.2 (-0.27%) |
  | `require "irb"` | 2,358.3 -> 2,348.4 (-0.42%) | 3,424.6 -> 3,411.9 (-0.37%) |
  | irb 1+1 | 2,527.4 -> 2,516.5 (-0.43%) | 3,684.9 -> 3,671.1 (-0.37%) |
  ~10 M fewer on every command, none more.
  Wall, best of 10 alternating, base / new, ms: release image 344 / 345, 399 / 347, 693 / 765,
  766 / 825, 843 / 852; R2R host 483 / 440, 586 / 538, 917 / 1000, 941 / 891, 1040 / 1036. Swings of
  +-8-13% both ways on a machine that had just restarted: noise, far above the effect.
  Checked: every RbConfig::CONFIG value, its encoding and frozenness, and RbConfig.ruby print the same
  as the base on the R2R host and the Release build. So do Gem.loaded_specs, $LOADED_FEATURES and its
  order, and default-gem lookups (perf/startup3's probe). -w prints nothing, and
  `IronRuby.__host_config__` is private.
  AWFY verifying: at 3 s CD was killed at the deadline before one iteration on both trees, on a slow
  machine. At 8 s in both orders CD, NBody, Sieve and Bounce verify, and none is slower in both orders
  (Bounce +16% / 0%, NBody +9% / -1%).
  Left in RubyGems' boot, all of it spread thin: the `super` rules; the first `eval` (~3.7 ms; the
  startup library could hold eval'd strings keyed by their exact text, a general mechanism);
  ruby4.rb's 4,989 CLR member lookups in `-e 1` (5.9 ms, core prelude, not RubyGems: worth finding
  out what asks); and JIT that only the R2R host pays.

- **perf/kwrest (/perf-step, 2026-11-26, on 79a2c30 + perf/lexical3) - a rest parameter that
  takes a call's keyword hash unmarks the call's own instead of storing a copy
  (`IR_KWREST_IN_PLACE`, gap 38's first cost)** - instructions only, gated 2026-11-27 (round 4's gate entry). A keyword
  call into a method whose rest takes the hash (`def r(*a)` called `r(i, k: 2)`; send,
  method_missing, `define_method { |*a| }`, every delegator without `ruby2_keywords`) paid
  about 1,400 instructions over passing the same Hash positionally. Bisected: the rule's
  `RubyOps.NormalizeRestArgument` made `new Hash(hash)` - a table and every entry inserted
  again through the comparer - only to have an unmarked hash in the array. Every hash marked as
  keyword arguments is made for one call (`MakeKeywordArgumentsHash*`, `KeywordSplatArgument`'s
  copy, the copies of `SplatRuby2Keywords` and `RestoreRuby2Keywords`, the C API's;
  `PassOnKeywordRest` hands on one the method stops holding), which `ClearKeywordArguments`
  already relies on to unmark the hash in place for any other parameter. So the mark (and
  `IsOmittedKeywords`) comes off in place; the copy stays for a `ruby2_keywords` method, whose
  copy carries the other mark, for the runtime's shared frozen empty hash, and for anything not
  exactly a Hash. The keyword path went out of line (`NormalizeRestKeywords`, NoInlining) so
  that the test a call without keywords runs stays as small as before. The rule's other
  suspect - `ArgsBuilder` for a rest callee with a keyword signature - owns nothing here: a
  keyword method with a rest never normalizes, and `v_unused` / `kr_rest` did not move; what
  they pay over a plain rest is the keyword prologue, gap 38's second cost, a separate
  experiment.
  - Instructions a call (`Util/perf/callgrind/count.sh`, `ab.rb -i -r 1 -n 20000`, unchanged
    build / switch off / on, shapes in `prelude-cost.rb`): `v_hash_into_rest` 4,814 / 4,838 /
    3,688 (0.76); controls `x_const`, `v_two_pos`, `kr_plain_rest`, `v_prebuilt_hash` and the
    keyword-rest methods `kr_rest`, `v_unused`, `kr_lines` 1.000. `v_hash_literal_arg` read
    1.02 in that run, where the new code does not run; alone in a process it is 3,469 -> 3,400
    (0.98): which shape's section a gen0 collection lands in moved with the allocations the
    earlier shapes no longer make. (The first build had the keyword path inline and the same
    1.02 there; the off arm of the split build equals the unchanged build on every shape.)
  - `Util/kwrest-call-matrix.rb` (new: the hash in the rest passed on splatted, as an argument,
    double-splatted, to `k:`; mutated; a fresh one each call; frozen; `**{}`, `**nil`, a
    positional Hash, String keys; the caller's hash through `**h`, `**frozen` and positionally,
    a kept hash passed again; `ruby2_keywords` - passing on, positional, the flag, mutated,
    applied later, its hash into a plain rest; send, public_send, `__send__`, method_missing,
    zsuper into a rest and with `**kw`, `super(*a)`, `define_method |*a|`, Method#call,
    to_proc, Enumerator replay, a lambda's rest; `**nil`, `**kw` and `k:` given a Hash) prints
    CRuby 4.0's output on the unchanged build, and on the change by default, with
    `IR_KWREST_IN_PLACE=0`, `IR_LIGHT_FRAMES=0`, `-X:NoJIT -X:NoOSR` and `-X:NoJITCalls`; so do
    keyword-rest-light and keyword-light. splat-pass, light-rest and jit-calls print CRuby's
    output in the default mode, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls` and `IR_LIGHT_FRAMES=0`;
    hot-shapes prints the unchanged build's in all five (it differs from CRuby already in one
    String probe, FrozenError against Encoding::CompatibilityError). ruby/spec
    `language/keyword_arguments_spec.rb` 23 examples 0 failures, `method_spec.rb` 168 / 0,
    `send_spec.rb` 78 with 1 error - the known `into`/`to` conversion message - the same on
    both builds. IronRuby.Tests `Method_KeywordHashIntoRest1` (new; passes with the switch
    off too) and 50 named keyword, method, splat, send and super tests pass. Not timed; no
    R2R run.

- **perf/havlak (/perf-step, 2026-11-26, on 819a368) - a call on a local the profile has no sample of
  is inlined for the classes that define the method, under checks of their class (gap 25)** - not
  gated. som.rb's `Dictionary#at` (Havlak's `IdentityDictionary`) declined at `call: match/2`: `e`
  comes from `get_bucket(hash)`, a call, so `TrySample` has nothing for it, and the inliner, which
  checks a class at a call on a parameter, had no class to check. Now `UnsampledCallees` takes the
  classes, constants of Object, that define a method of the name themselves whose typed body is
  inlinable now (`InlinableCallee`): one, or two with the same parameter and return types, each under
  a check of its exact class (the inliner's two-class path, `IsOfClass`/`GuardClass`), and anything
  else deopts - a subclass, a singleton class, a class whose method has no such body,
  method_missing; the entry's method version check covers a redefinition, and a private or
  protected method is not inlinable for an explicit receiver. A candidate still being profiled is
  waited for only when no other is ready. An attribute read of the same local (`e.value`, `e.next`)
  takes the variable's name from the class the call was inlined for and reads through
  `ReadSelfAttr`, which binds to the class it meets and deopts where the name is not that variable's
  reader; its value is an object. Switch: `IR_JIT_UNSAMPLED=0`.
  Census (Src/StdLib + AWFY): 0 methods changed (it has no specialized callees to inline). Havlak
  traced (`harness.rb Havlak 1 3`): `Dictionary/at` specialized (`hash`, `get_bucket` and
  `IdEntry#match` inlined), declines after the pre-screen 3 -> 2, deopts 0, invalidations 0;
  `census -c` 88 agree, 0 differ. The other thirteen benchmarks traced (3 x 1): the same JIT and OSR
  decisions on both builds; all fourteen verify (CD at `3 100`: its inner count 1 is not a valid
  one, on either build or CRuby). `Util/bench/havlak-shapes.rb` (new; som.rb's Dictionary with
  `Entry#match`), instructions a call (`callgrind/count.sh`, new/base): `at_first` 0.611, `at_chain`
  (behind one entry) 0.571, `at_miss` 0.538, the control `chain_len` (the same loop calling nothing on
  the local) 1.000; time on a quiet machine, ns: 142 -> 88, 172 -> 104, 180 -> 100, control 147 ->
  148. Havlak, `harness.rb Havlak 6 1`, three pairs in separate processes, the order turned in the
  second: base 23.55, 22.37, 22.19 s, new 22.34, 21.62, 21.74 (0.965, new faster in each pair, inside
  the benchmark's swing).
  Checked: `Util/unsampled-matrix.rb` (new, 16 cases, one per process: entries of another class with
  a match of its own half way, two classes both inlinable, `match` and `value` redefined, a singleton
  `match` and `value`, method_missing, a subclass, a subclass with `private :value`, `value` and
  `match` made private, `match` protected and removed, the entries frozen, the buckets emptied) prints
  CRuby's output on the unchanged build and on the new one with the JIT on, `-X:NoJIT -X:NoOSR`,
  `-X:NoJITCalls` and `IR_JIT_UNSAMPLED=0`; every case specializes `at` with `match` inlined. jit,
  jit-calls, jit-const, ivar, deadconst matrices print CRuby's in all four modes; hot-shapes and
  inline the baseline's (their known rows: a FrozenError where IronRuby raises
  Encoding::CompatibilityError, and `tree_int_key`, on every build with the JIT on or off).
  IronRuby.Tests Jit*, Loop*, Inline* (21) pass.
  For the gate: the candidates are found by enumerating Object's constants under the hierarchy lock
  at each compile of such a call (compile time only); a class not a constant of Object (nested in a
  module) is not a candidate, so such a call stays uninlined. Look at the attribute read's name being
  taken from the first candidate while the receiver may be of the second (ReadSelfAttr re-binds, and
  deopts if that class's reader is another variable), and at "a fast path that decides from a resolved
  method": the candidate's method is resolved on the candidate class with the lookup's visibility
  (`InlinableCallee`), and a subclass never takes the inlined body (exact class check).

- **perf/jitsettle (2026-11-26) - a retired entry's cool-down costs nothing per call** - gated 2026-11-27 (round 4's gate entry).
  A method whose typed body keeps failing (`x + 1` called with an Integer and a Float in turn) ran
  10-16% slower than with the JIT off, as a `def` and as a define_method method. What it does per call,
  from a trace (`IR_JIT_VERBOSE=1`, a phase-by-phase timing of 12.5 million calls): after the first
  windows of 16 deopts the entry is retired and retried 8 times, cooling down 8, 16, ... 1024 ms - about
  2 s in all before it settles for good - and all that time the fast slot held the cooling stand-in
  (`Cool`): a counter decremented on every call, a random pick, and one call in 8 timed with two
  `Stopwatch` reads, though only the last 16 samples are used, once, when the cool-down ends. Steady
  state: 115 ns a call against 101 with `-X:NoJIT`.
  - The change (`JitStub.StartResting`): a retirement puts the generic body itself in the fast slot,
    as a settled method has it, and a `System.Threading.Timer` ends the cool-down by starting the probe
    the stub already had (`StartCooling(0, ProbeCalls, 0)`: 32 generic calls, each timed), whose
    `CoolDownCheck` puts the entry back. A generation count makes a timer left from an earlier rest do
    nothing; `GenericCompiled` installs the compiled generic body during a rest too. The probes before
    any retirement, the retire rule and the retry schedule are unchanged; a body that deopts rarely is
    never retired, so it never rests. `IR_JIT_REST=0` brings back the counted cool-down.
  - Time (`Util/bench/jit-settle-shapes.rb`, `ab.rb -r 6`, ns; -X:NoJIT / base / new, new over base):
    `mixed_def` 95.9 / 112.7 / 97.5 (0.87), `mixed_dm` 119.8 / 127.6 / 117.9 (0.92), `rare_def` (a Float
    on one call in 1000) 70.0 / 61.9 / 61.9 (1.00), controls `plain_def`, `ivar_def`, `two_def`,
    `plain_dm` 0.96-0.99 (overlap). Phase by phase the `def` goes from 115 to about 100 ns, level with
    `-X:NoJIT`, and the run of 12.5 million calls ends 0.2-0.5 s sooner.
  - Instructions (`Util/perf/callgrind/count.sh`, N=20000): the controls and `rare_def` 1.000;
    `mixed_def` 0.987, `mixed_dm` 1.000 - the cool-down is counted in milliseconds, of which valgrind's
    20,000 calls see little: the clock is this change's measure.
  - AWFY (all 14, `harness.rb B 5 10`, IR_JIT_VERBOSE=1): 138 specialized, 29 declined, 15 kept, no
    retirement and one probe's retry on both builds - none of the benchmarks retires an entry, so the
    change does not reach them.
  - Matrices, CRuby against base (3b28a0c) against new, with the JIT on, `-X:NoJIT -X:NoOSR`,
    `-X:NoJITCalls` and `IR_JIT_REST=0`: jit, jit-calls, jit-const, dmeth-jit identical to CRuby;
    hot-shapes, inline (one case per process) and int-matrix (through int-matrix.py, `.hash` lines left
    out: they are seeded per process) identical to base, each with the rows that differ from CRuby on
    base too (FrozenError for Encoding::CompatibilityError; "Object must be of type Int32";
    `(2**100).equal?(2**100)` and the size of `Integer.instance_methods(false)`). IronRuby.Tests: the 20
    named for the JIT pass.
  - For the gate: a timer thread now takes the stub's lock (EndRest) - a stub retired while its
    runtime shuts down has a timer pending for up to a second; and the retry needs only the timer and
    32 calls, where it needed 1024 calls and the cool-down.

- **perf/dmethjit2 (2026-11-26) - a define_method body that reads captured locals, and setters, in the
  method JIT (gap 23)** - gated 2026-11-27 (round 4's gate entry). Chosen by a survey of the 154 `define_method` blocks in the
  standard library and the bundled gems (Prism, by parameters and what the body does with the
  enclosing scopes' locals): 66 have plain or no parameters and *read* a captured local with nothing
  else in the way (`{ @options[name] }`, `{ params[name] }`, `{ code }`, `{ @major_version >= v }`,
  `{ |v| @h[name] = v }`), against 17 that read none (what round one took); 4 write one; the
  `*args, &block` bodies of delegate.rb (`__send__(mid, *args, &block)`) are calls the typed compiler
  takes in no method, and forwardable generates `def`s with `...`, not define_method.
  - The change: BlockDefinition keeps the tree of a block that *assigns* no enclosing local
    (`AssignedOuterVariableNames`), not only one that uses none. `JitBlockMethod.ResolveCaptured`
    finds each local the block reads in the scope as many levels up from the Proc's `LocalScope` as the
    lexical depths say, checking that no scope between declares the name (`RubyScope.TryGetLocalSlot`);
    the def it hands the JIT is a `BlockMethodDefinition` that knows those slots, and the screen
    (`JitScreen`) and `JitCompiler.EmitLocalRead` take a read of one as a call of
    `JitBlockMethod.ReadCaptured` - the value in the scope's locals at that moment, an object, as the
    block's own closure reads it (ScopeBuilder.CreateScope) - where they bail for any other def. And the
    rule of `RubyLambdaMethodInfo` sends an assignment call (`o.x = v`, `o[k] = v`) to the stub too,
    its value as the last argument (as `Methods.Yield` passes it): a setter never reached the stub.
  - Instructions a call (`Util/perf/callgrind/count.sh`, N=20000; master 2f8c31c, new): `bmcapt`
    (`x + captured`) 1921 -> 1255 (0.65), `bmh` (`{ @h[name] }`) 2592 -> 1892 (0.73; the `def` 1799),
    `bmhset` 2359 -> 1713 (0.73; the `def` 1639), `bmconst` (`{ code }`) 1877 -> 1366 (0.73),
    `bmscaled` (`{ |x| x * k }`, k a parameter of the method that called define_method) 2300 -> 1693
    (0.74); the `def` controls 1.000.
  - Time (`ab.rb -r 6` on a loaded machine, ns; CRuby / master / switch off / new, new over master):
    `bmcapt` 31.8 / 63.5 / 64.7 / 40.0 (0.62), `bmh` 35.7 / 69.8 / 72.6 / 53.3 (0.76; the `def` 49.7),
    `bmhset` 47.3 / 65.1 / 64.8 / 43.9 (0.67; the `def` 43.2), `bmconst` 27.6 / 57.3 / 57.7 / 37.6
    (0.66), `bmscaled` 33.9 / 75.6 / 74.0 / 49.4 (0.65); `def` controls 0.93-0.99 (overlap).
  - Traces: `Util/bench/app-shapes.rb` and AWFY Richards, DeltaBlue, Json, CD specialize and decline
    exactly what master does (113/29, 78/19): none of their hot methods is such a body. In the matrix 15
    more define_method methods are specialized and two more are wrapped and declined (`flag ? x : -x`,
    the compiler has no `-@`; `recv.helper(x)` on a captured receiver).
  - Matrices, CRuby against master against new, with the JIT on, `-X:NoJIT -X:NoOSR`,
    `-X:NoJITCalls` and `IR_JIT_DMETH=0`: `Util/dmeth-jit-matrix.rb` (41 rows now: a name captured per
    iteration with its reader and writer, a captured local of a method's scope, two levels up, a captured
    receiver, a captured flag another method changes, a captured value changed through Binding to a
    Float, a Bignum and a String, block-local shadowing, threads, assignment calls), `dmeth-matrix`,
    `jit-matrix`, `jit-calls-matrix`, `block-given-matrix`: identical to CRuby. `inline-matrix` (one
    case per process): new identical to master (both differ from CRuby in `tree_int_key`, as before).
    IronRuby.Tests: the 141 registered tests that use define_method or instance_method or are named for
    the JIT pass.
  - Also, a commit of its own: two define_method methods from the same Proc (or its dup, or its
    lambda) are `==` with equal hashes, as in CRuby (`DefineMethodEquality1`), which round one found.
  - For the gate: a captured local is read through the scope object found when the stub was made; a
    scope whose locals tuple were replaced after that (a reused or parked scope) would be read where
    the block's closure also reads it, per call, but that equivalence rests on ScopeBuilder's closure
    code. More block literals keep their tree on the dispatcher with the JIT on (any that assigns no
    enclosing local).

- **perf/jitstr (2026-11-26) - `s + "lit"` and `s << "lit"` in a body the method JIT compiles** -
  census only, not gated. Behind the String literal line (101 alone) are call arguments (44, gap 1's
  wall, perf/strlit's rejected first build) and operands of String operators: `+` 12 (about 15 with
  mixed forms), `<<` 11 (16 with `*` or `%` beside it), `"fmt" % x` 8, `"-" * n` 5, comparisons
  a few (`s == "lit"` is perf/t3's intrinsic already). Taken: `+` and `<<` with a String literal
  argument. Not `%` (format runs `to_s`/`inspect`: Ruby code from the typed body) nor `*` (left for
  a step of its own). Different from round fifteen's String `==` and `String#[]` (perf/core2,
  0.92 and 0.91 on their shapes, nothing hot compiled): judged by the methods that then compile,
  and the screen takes the operator only where it lets a method through; and from the rejected
  "`x == "lit"` without allocating" (code outside the JIT, bytes only): this is the typed body.
  `EmitStringLiteralConcat`: for a receiver whose CLR type is exactly `MutableString` (a subclass
  instance is another type) and whose encoding the literal's can join, the library's own code -
  `MutableString.Concat` for `+`, `Append` for `<<` (`JitRuntime.StringConcatLiteral`,
  `StringAppendLiteral`), the literal one frozen copy that neither keeps nor changes. Compiled only
  while `String#+`/`#<<` are the public library methods with no String carrying a singleton method
  of the name (`IsPlainLibraryMethod`, as `s == "lit"`; a redefinition moves the method version the
  entry checks) and no refinement active where the method was written. Anything else deopts before
  anything is done: nil, an Integer, an Array, a subclass, incompatible encodings (UTF-16LE), and for
  `<<` a frozen receiver (FrozenError from the generic body's frame) or a chilled one (the
  deprecation warning, and the String unchilled, by the generic body). `<<` is an effect, after its
  own deopt and not one on self only (`_effectNotOnSelf`), so a deopt after it declines as usual; not
  in an outlined loop. The screen refuses either as an operand of a binary operator (a String, so
  the operator could only deopt): the first build let `URI::Generic#userinfo` (`@user + ':' +
  @password`) through, and app-shapes' trace declined it after the pre-screen (30 -> 31); with that
  refused the trace is the baseline's.
  JIT compiles 3,376 -> 3,384, maybe 2,989 -> 2,997, never 22,011 -> 21,995, light frames unchanged
  (Src/StdLib + AWFY, 28,376 methods; 16 methods moved up - 8 to `compiles`, 8 to `maybe` - none
  down). Moved: `Net::HTTP#debug` (two copies), RDoc's `accept_rule`, `accept_blank_line` and
  `accept_list_end`, `REXML::Declaration#to_s`, `REXML::Text#xpath`, `IPAddr#reverse`,
  `#ip6_arpa`, `#ip6_int`, Thor's `file_collision_help`, `Bundler::UI::RGProxy#say`; none of AWFY
  (Json's are call arguments) or the prelude. Real code barely has it: of the methods app-shapes
  calls, the String literal is the only refusal in none.
  Traces: app-shapes 117 specialized and 30 declined after the pre-screen on both builds; AWFY
  Json, Richards, DeltaBlue, CD and Havlak (3 x 10) the same on both; `census.rb -c` 290 agree, 0
  differ, and app-shapes' known 15.
  Instructions a call (`Util/perf/callgrind/count.sh`, `Util/bench/jit-strop-shapes.rb`, new):
  `s + ":"` 3308 -> 3048 (0.92), `s + ":" + "x"` 4669 -> 3979 (0.85), `buf << "x"` 3910 -> 3116
  (0.80), `@user + ":"` 3462 -> 3137 (0.91), the control `s == "lit"` 1244 -> 1244. The
  concatenation itself is most of the cost; the typed body saves the literal's allocation and the
  dispatch.
  Checked: `Util/jit-strop-matrix.rb` (new, 31 rows: new and unfrozen results, a frozen receiver of
  `+`, UTF-8, BINARY and US-ASCII receivers, UTF-16LE, nil, Integer and Array receivers, a subclass
  with and without its own `+`, a singleton `+`, `<<` on frozen, chilled (with the deprecation
  warning counted), UTF-16LE and BINARY receivers, an effect then a deopt, `<<` then an ivar write
  on a frozen self, a refinement, `String#+` and `#<<` redefined and restored), and jit,
  jit-calls, jit-strlit, jit-scan, jit-interp, jit-const: CRuby's output and the unchanged build's
  with the JIT on, `-X:NoJIT -X:NoOSR` and `-X:NoJITCalls`, and the unchanged build's with
  `IR_LIGHT_FRAMES=0`; hot-shapes and inline-matrix (one case per process) the unchanged build's in
  every mode, with their known one-row differences from CRuby.
  For the gate: no IronRuby.Tests or spec run. A String given a singleton `+` anywhere makes every
  later compile of `+` bail for the life of the process (`HasPlainInstances`, as for `==`).
- **perf/strsearch3 (2026-11-27) - StringScanner on a UTF-8 subject maps bytes to characters
  incrementally, without switching the subject to bytes (gap 21, round three)** - not gated.
  Commits 0c41d0f, 8822aad on `claude/perf-strsearch`, based on claude/happy-gauss-95wm2j
  79a2c30 with a7b9a12 merged in. Chosen by measured size among the three items left under gap
  21: a loop over 10 KB and 100 KB of UTF-8 text (CRuby / base, ms) - StringScanner word by word
  (`skip(/\s+/); scan(/\S+/)`) 0.20 / 55 and 2.1 / 8,400; `scan_until(/,/)` 0.072 / 13.8 and
  0.71 / 2,047; `byteindex(",", pos)` in a loop 0.039 / 2.7 and 0.36 / 93; `index(",", pos)` (a
  character offset) 2.4 / 0.058 and 233 / 0.24 (CRuby's is the quadratic one). The scanner does not
  use `#byteindex`, so its loop was its own: the scan pointer is a byte offset and the regex
  matches CLR characters, and every conversion copied out and counted the whole prefix
  (`CharIndexOf` before the match, `MatchData.ToByteIndex` after), while `eos?`, `matched`,
  `getch`... read the subject as bytes, which switched it from characters to bytes, and the next
  match decoded all of it again (profile: `SwitchToCharacters` 35%, UTF-8 transcoding 80% self).
  Now a scanner keeps a map for a subject that is valid UTF-8 and not all ASCII, made for one
  snapshot of its characters (ConvertToString's string; a changed subject has a new one) with its
  UTF-8 length counted once; offsets are converted by walking the characters from a cursor (the
  last offset converted), so a step costs about a token's length. Match offsets are taken as CLR
  offsets (`MatchData.TryGetGroupClrOffsets`, new) and mapped; ByteLength, ByteSlice, getch's
  width, charpos, bol?, an ASCII literal and Integer groups read the characters. An offset inside a
  character, an ASCII, non-UTF-8 or broken subject, a group read after the subject changed, and
  `IR_FAST_SEARCH=0` take the old paths.
  Callgrind (`count.sh`, N=20000), instructions a pass over 10 KB, base / new: `ss_until_u`
  736,035,168 / 4,818,316 (0.0065); `ss_words_u` (not counted on the base: some 2.9e9) / 15,030,852;
  `ss_words_a` (ASCII, a control) 13,620,867-13,727,541 / 14,080,576 (**+2.6 to +3.4%**; with
  `IR_FAST_SEARCH=0` 13,969,285, so most of it is not the map's check: moving the mapped paths out
  of `MatchRegex` and `ByteSlice` and inlining the early exit did not change it); `sw3_a`, `size`
  unchanged. Time, `ab.rb -r 4`, ns a pass, CRuby / base / off / new: `ss_words_u` 414,855 /
  211,024,746 / 213,010,273 / 1,756,873; `ss_until_u` 150,450 / 52,087,624 / 52,459,357 / 635,316;
  `ss_words_a` 364,739 / 633,832 / 635,709 / 1,439,309 - but that shape is bimodal from process to
  process (0.7 to 2.1 ms on either build, five runs each), and base and off had spent minutes in the
  slow shapes before it in the same process; the count is the number to go by. CSV.parse of
  UTF-8 rows (`Zürich,"Müller, Jörg",42,ñandú`), CRuby / base / new: 10 KB 1.2 / 218 / 79 ms, 100 KB
  11 / 27,651 / 168 ms (the ASCII rows' times swing 17-95 ms from run to run on all three arms).
  Still 4 to 8 times CRuby on UTF-8 scans (the regex call, a MatchData per match, a String per
  token), about where ASCII scans already were.
  Checked: `Util/string-search-matrix.rb` gained a StringScanner section (2,532 rows: 19 operations
  from every byte offset of ASCII, UTF-8, astral, broken UTF-8, BINARY and EUC-JP subjects, word
  loops, concat midway, `string=`, fixed_anchor, terminate/reset; 60,022 rows in all): this build
  prints exactly what the base prints, switch on and off. The base differs from CRuby in 110 of the
  new rows, all pre-existing and untouched: a scan started from a byte offset inside a character
  (UTF-8, astral, EUC-JP: CRuby matches from the stray bytes, IronRuby finds nothing), a non-ASCII
  literal on a BINARY subject (CRuby raises Encoding::CompatibilityError), EUC-JP. `csv-matrix`
  and `erb-escape-matrix`: same as the base and CRuby. spec/core/string (3,963 examples),
  spec/library/stringscanner (240), spec/library/csv, spec/library/erb: the same on base, new and
  off, 0 failures (one error in all three, `String.try_convert`, run as a directory).

- **perf/lconst (/perf-frame, 2026-11-27) - a compiled body's constants without a cast helper**
  (switch `IR_SEALED_CONSTANTS`, on unless `=0`; a DLR patch, `0112-compile-rewriter`) -
  instructions only, not gated. Built on 79a2c30 with claude/perf-lsite merged (980e36c).
  - **Counted first** (callgrind with `DOTNET_ReadyToRun=0`, so that CoreLib's cast helpers have
    names, and a caller walk of the perf map): `CastHelpers.ChkCastClass` and
    `ChkCastClassSpecial` are 9.2% of `fc`'s instructions (frame-cost, `-X:NoJIT`), 11.7% of
    `tap`'s and 6.9% of `inject`'s (core-small). By caller: the Ruby bodies (`F#fc` 7 casts a
    call, `tap`'s caller `H#m56` 24, `H#m3` 19, `Enumerable#inject` 8), the shape file's
    compiled loop (~10, every shape alike), and the rules (`CallSite.Target`, 1-6). A cast
    is ~17 instructions. In a body: `LambdaExpression.Compile` keeps object constants in its
    closure's object[] and loads one with a bounds check and a `castclass`; every dynamic
    call site is one (`CallSite<T>`, not sealed: `DOTNET_JitDisasm` of `F#fc3` shows 14
    helper calls).
  - **Why not a sealed holder.** First cut: each site in a sealed `SiteHolder<T>`, and every
    constant typed as its own class where that is sealed. It counted more (up to 1.028): an
    `ExpressionVisitor` sees a `DynamicExpression` as an extension node (`Accept` calls
    `VisitDynamic` only for a `DynamicExpressionVisitor`), and reducing it gives
    `Constant(site).Target(Constant(site), ...)`, two casts where the compiler emits one.
    Fixed, the holder's cast was still the helper: in Ruby's bodies the JIT calls
    `CORINFO_HELP_CHKCASTCLASS` even for a sealed class, where a probe lambda of the same
    runtime (in its own process and inside `ir`; with a try/finally, a fault, a catch and
    rethrow, 600 locals, 32 casts, `PrecompiledFrames.Current`) gets the one-compare inline
    check. What makes the difference was not found.
  - **What landed.** Just before an interpreted lambda is compiled to IL - the DLR's
    `LightDelegateCreator`, through a hook the patch adds (`LightCompilation.Rewriter`), and
    `-X:NoAdaptiveCompilation`'s compile - `CompiledConstantsRewriter` (a
    `DynamicExpressionVisitor`) reduces the extension nodes, turns each dynamic expression into
    the call the compiler would emit (the site, its Target, then the arguments), and loads
    every constant of a class (not a string, a Type or a MethodBase, which the compiler emits
    inline) as an object, which needs no cast, through `Unsafe.As<T>`, which is no code. Sound:
    a ConstantExpression's value is of its type, and the rewriter makes the sites itself.
    The interpreter keeps the tree it had; the method JIT's typed bodies and the rules are
    compiled elsewhere and keep their casts. `F#fc3`: 14 helper calls -> 5, 622 -> 603 lines.
  - **Numbers** (instructions a call, `Util/perf/callgrind/count.sh`, N=20,000, switch off ->
    on). frame-cost `-X:NoJIT`: `f0` 0.984, `f1` 0.988, `fc` 0.989, `fc3` 0.991, `fy` 0.992,
    `fbg` 0.983, `bytesplice` 0.984; default mode: `f0`/`f1` 1.000 (the method JIT's), the rest
    0.983-0.992. core-small: `each` 0.979, `max_by` 0.979, `inject` 0.982, `sum_blk` 0.985,
    `then` 0.985, `each_with_index` 0.987, `zip` 0.988, `minmax` 0.990, `inject_sym` 0.991,
    `tap` 0.991. regex-shapes: 0.980-0.996, `size` 1.000. Two rows first read high and were
    the process's history (counts move with the shapes run before in the same process):
    `tap` 1.011 in a list of ten, 0.991 alone (two processes each, to the 0.1); `eqt` 0.781
    after `kase`, 0.987 alone. Some callgrind processes of both arms stopped part way under
    valgrind (the .NET process dies there now and then; the same shapes complete natively and
    in a rerun): their rows were measured again. Start-up (wall clock, six alternating runs,
    while a publish ran): `ir -e 1` 1,609-1,779 ms on, 1,678-1,814 off; `-S gem list`
    3,244-3,443 on, 3,271-3,501 off - no worse.
  - Checked: 18 matrices (light-entry, light-rest, block-given, global-light, lexical-light,
    keyword-light, regex-light, rescue-light, super-light, parked-scope, light-remember, caller,
    raise, jit, jit-calls, jit-scan, jit-strlit, hot-shapes) in seven modes (as built,
    `IR_SEALED_CONSTANTS=0`, `IR_LIGHT_FRAMES=0`, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls`,
    `-X:NoAdaptiveCompilation`, and the R2R host with its precompiled startup library, rebuilt
    from this tree): 108 of the six Release modes print what the baseline prints, and CRuby's
    but where the baseline differs too (light-entry's `define_method` alias, caller's library
    frames, raise, hot-shapes' FrozenError). The R2R host: what the Release candidate prints,
    but regex-light's `raise label` (`Integer()` from a light frame is labelled "block in
    <main>": the same with the switch off and with `IR_STARTUP_DLL=none`, the R2R host's own)
    and one line of caller-matrix whose iteration counts move from run to run in both arms (when
    the background compilation lands). `IR_CONFIG=R2R ./ir.sh -e ...` and `-S gem list`
    run with the startup library, switch on and off. IronRuby.Tests (which compile up front):
    71 named (Method_LightFrame*, Backtrace_*, Scenario_RubyBlocks/Eval/Exceptions/Closures/
    MethodMissing/MethodAliases*, JitCore*, Dynamic*): PASSED. `Util/check-dlr-patches.sh`
    passes on a `../dlr` with the patch applied. Not run: the suite, a sweep, AWFY, timing.
  - Left: the method JIT's typed bodies and the rules (`CallSite.Target`, compiled by the
    DLR's binder) could use the same rewrite; why the JIT expands no cast in these bodies.

- **perf/lframe (/perf-frame, 2026-11-26) - a cheaper method entry: the alias name and the
  traced test** (switch `IR_LIGHT_ENTRY`, on unless `=0`) - instructions only, gated 2026-11-27 (round 4's gate entry).
  Built on 2f8c31c. What gap 16's profiles left on every light frame, looked at on its own
  with `Util/bench/frame-cost.rb` (new: a method that does nothing, one, three calls, a yield,
  `block_given?`, and `String#bytesplice`), `-X:NoJIT` so that the generic bodies run.
  - **Not the prologue.** perf/lsite2 put the light lambda's +158 down to zeroing its locals;
    `DOTNET_JitDisasm` of the light `H#gsub` says otherwise: two 64-byte stores. What the
    lambda pays up front is its closure constants (each load a bounds check and a
    `CHKCASTCLASS` helper call), the frame record (`PrecompiledFrames.Enter`) and the
    entry's helpers. So no `SkipLocalsInit` (the runtime compiles with the BCL's
    `LambdaExpression.Compile`, which has no such switch, and the DLR patch set has nothing
    to patch) and no DLR patch.
  - **The alias name.** A call through an alias leaves the name it was called by in a
    thread-static for the method's entry to take (`MarkAliasCall`, `TakePendingCallee`,
    `CreateMethodScope`). Every entry tested a flag first, `_aliasCallSeen`, which start-up
    sets for good (RubyGems and the prelude call aliases), so every entry of every method,
    light or scoped, read the thread-static - a call into the runtime from a DynamicMethod:
    `TakePendingCallee` 26 + `TakePendingCalleeSlow` 34 instructions a call in the `fc`
    profile. The flag is now a count of the threads that have a name pending: raised by the
    alias call when its thread had none, lowered by the entry that takes one, so an entry
    reads the thread-static only while some thread has a name pending. A thread that dies
    with one pending leaves the count up, which only costs the reads it costs today.
  - **The traced test.** A light frame without register locals called
    `CreateMethodScopeIfTraced` (nine arguments) on every entry to learn that no TracePoint
    listens; the test is now inline, as `EnterWithRegisterLocals` has it.
  - Left as they were: `EnterLightMethod` and `LeaveLightMethodFrame` already inline their
    fast paths (the slow exit only for a block or a scope), and the frame record is every
    compiled frame's, tuned before (#168).
  - **Numbers** (instructions a call, `Util/perf/callgrind/count.sh`, N=20,000; switch off ->
    on). frame-cost, light frames: `f0` 1,572 -> 1,435 (0.913), `f1` 1,656 -> 1,591 (0.961),
    `fc` 2,553 -> 2,314 (0.906), `fc3` 4,280 -> 3,693 (0.863), `fy` 2,412 -> 2,347 (0.973),
    `fbg` 1,932 -> 1,795 (0.929), `bytesplice` 27,120 -> 26,907 (0.992); the same with
    `IR_LIGHT_FRAMES=0` (the alias count only): 0.945-0.974, `bytesplice` 0.991. Default mode,
    regex-shapes: 0.977-0.993, `size` 1.000; core-small: `tap` 0.931, `then` 0.945, `minmax`
    0.969, `inject_sym` 0.973, `inject` 0.975, `sum_blk` 0.980, `zip` 0.985, `max_by` 0.984,
    `each`/`each_with_index` 0.991. No shape counts more. No timing run (the counter is the
    measure here; the gate owes the clock).
  - Checked: `Util/light-entry-matrix.rb` (new: `__callee__` and `__method__` through `alias`
    and `alias_method` into light and scoped methods, `define_method`, `send`, `public_send`,
    `Method#call`, `bind_call`, method_missing; an alias call that raises ArgumentError before
    its method is entered and then ordinary calls; alias calls as arguments of alias calls;
    recursion through an alias; four threads calling aliases and names; TracePoint `:call`'s
    `method_id`/`callee_id`; a million-deep recursion through a light frame, and through an
    alias: SystemStackError, and the process goes on) and light-rest, block-given,
    global-light, lexical-light, keyword-light, regex-light, rescue-light, super-light,
    parked-scope, caller, light-remember and hot-shapes: the candidate prints what the
    baseline prints in all 78 runs (each as built, `IR_LIGHT_ENTRY=0`, `IR_LIGHT_FRAMES=0`,
    `IR_LIGHT_REGEX=all`, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls`), and CRuby's but where the
    baseline differs too: caller's library frames, hot-shapes' FrozenError, and
    light-entry's `define_method` body called through an alias, whose `__callee__` is the
    definition's name on both builds (CRuby: the alias's) - a fault of master, not of this.
    IronRuby.Tests: the 11 of AliasTests, `Backtrace_ArityCalleeFrame1` and the ten
    `Method_LightFrame*`: PASSED. The census cannot see this (no method changes verdict).
    Not run: `IR_CONFIG=R2R` start-up, the suite, a sweep, AWFY (owed to the gate).

- **perf/startup3 (2026-11-27) - start-up on the release image: a copied module's backtrace name
  from generated attributes; the default-spec cache validated in one pass over the directory (gap
  26)** - not gated. Base 79a2c30 (with the round's ledger, 973314a). Two commits:
  - 86e85c0: `RubyStackTraceBuilder.FindCopyingClasses` read the `Includes(Copy = true)` attributes of
    every type in IronRuby.Libraries the first time a backtrace named a copied module's method. That
    cost ~9 ms once per process (`gem list`, `require "irb"` and irb, through optparse's rescued
    exception in `Gem::Command#handles?`) and loaded AngleSharp.dll and System.Net.Sockets.dll. The
    initializer generator now emits `[assembly: RubyCopiedModule(typeof(module), typeof(class))]`,
    six pairs in IronRuby.Libraries and none in Libraries.Yaml. An assembly without
    `RubyLibraryAttribute` is still scanned, and `IR_COPIED_TABLE=0` scans as before.
    LibraryCopiedModuleAttributes1 compares the written table with the scan for both assemblies.
  - 7fba338: `rubygems/defaults/ironruby.rb`'s `ironruby_load_default_specs` checks its cache's stamp
    with `IronRuby.__gemspec_stamp__`: name, size and mtime of each `*.gemspec` entry, in one C# pass.
    It used to glob the 62 default gemspecs and make a File::Stat and a Time for each. A boot that
    hits the cache no longer globs. A miss and `IRONRUBY_NO_GEM_CACHE=1` work as before.
    `rubygems/util` is still required at boot, as upstream's glob loads it, so `$LOADED_FEATURES` and
    its order are unchanged. The cache format is version 2, so old caches miss once.
    The helper first used a List of KeyValuePairs, a value-type instantiation that no R2R image
    holds: its first call JIT-compiled 26 methods (~3 ms on the R2R host) and made `gem --version`
    +1.3%. Over a List of FileSystemInfo it compiles 5 (~1 ms).
  Where RubyGems' ~120 ms of `-e 1` goes (per-file load timer, R2R host, startup library):
  rubygems.rb's top level 35-42 ms self, ~31 ms of it the default-spec cache (glob 7, stamp 5.7,
  index file name 2.5, read 5.6, Marshal.load 3.2, activation 3, the rest ~3); specification.rb 13-16
  self, plus requirement 13-16, platform 9-10 and specification_record 6-7 under it; rbconfig 11-14.
  Only the cache is IronRuby's own code. `find_default_spec` cannot be made lazy: the runtime requires
  ironruby/error_highlight, did_you_mean and syntax_suggest through RubyGems' require at every boot.
  Main-thread instructions (callgrind, TieredCompilation=0), base and new run side by side, base ->
  new, M:
  | command | release image | R2R host |
  |---|---|---|
  | `-e 1` | 1,198.3 -> 1,165.0 (-2.77%) | 2,092.4 -> 2,013.4 (-3.78%) |
  | `gem --version` | 1,339.5 -> 1,338.3 (-0.09%) | 2,272.6 -> 2,274.7 (+0.09%) |
  | `gem list` | 2,293.6 -> 2,247.4 (-2.01%) | 3,424.3 -> 3,380.4 (-1.28%) |
  | `require "irb"` | 2,412.4 -> 2,363.9 (-2.01%) | 3,469.6 -> 3,427.1 (-1.23%) |
  | irb 1+1 | 2,577.6 -> 2,531.8 (-1.77%) | 3,725.5 -> 3,678.5 (-1.26%) |
  The counts depend on load: under valgrind, the number of runs sharing the CPUs decides when
  background compiles land, and so how long the main thread interprets. The same command moved
  ~1-2% between batches of 2 and 4 runs. Compare only runs of one batch.
  Split by `IR_COPIED_TABLE=0` (release image, a trio in one batch): `gem list` base 2,300.0, new
  2,280.6, new with the scan 2,303.8; `require "irb"` 2,425.0, 2,378.3, 2,420.5. So in the tools the
  saving is part 1's. Part 2 saves `-e 1`'s 33-79 M (release / R2R host). In `gem --version`, `gem
  list` and irb, RubyGems globs and stats the specification directories later anyway, so the
  first-use cost moves there instead of going away.
  Wall, best of 10 alternating (`startup2.rb`), base / new, ms: release image `-e 1` 331 / 321,
  `gem --version` 388 / 384, `gem list` 721 / 684, `require "irb"` 719 / 715, irb 796 / 774. New
  first: 313 / 310, 371 / 377, 663 / 684, 700 / 704, 756 / 751 (that run lists new first). R2R host:
  423 / 415, 484 / 488, 819 / 825, 864 / 861, 934 / 945. All within the machine's noise, which these
  differences are under.
  Checked: a probe of backtraces through each copied module's methods prints the same on base, new
  and new with `IR_COPIED_TABLE=0`, on the Release build and the R2R host. That includes the three
  lines where IronRuby already differs from CRuby: Integer#div shown as `Integer#/`, Integer#coerce
  as `Numeric#coerce`, IO#printf's frame missing. The 30 backtrace and caller tests and the 18
  require and prelude tests of IronRuby.Tests pass. caller, raise, jit-raise and set-backtrace
  matrices print the same in 7 modes (the gate's five, R2R, `IR_COPIED_TABLE=0`).
  caller-matrix on the R2R host prints its Kernel#eval frames in 1 of 6 runs on either tree, a flake.
  thread-backtrace-matrix's main-thread row is the known 87/88. RubyGems' state is the same on both
  trees, on a hit and with `IRONRUBY_NO_GEM_CACHE=1`, Release and R2R, and with -w:
  `Gem.loaded_specs`, `$LOADED_FEATURES` and its order, `find_default_spec` of ten names, and the
  map's size (1,484). A touched gemspec makes the next boot miss and rewrite the cache.
  Steady state: AWFY verifying, no FAIL. At 3 s the geomean was 1.062, with Permute 2.4x. At 8 s in
  both orders Permute, NBody and Sieve are within 2%, and Havlak is +4% and -8%: noise.

- **Gate of 2026-11-26 for round thirty-seven** (perf/lglobal, perf/lexical, perf/deadconst,
  perf/strsearch, perf/extiter, perf/struct, perf/dmethjit; the rejected perf/lsite's tools and
  perf/jitbody's census fix): the branch `claude/happy-gauss-95wm2j` at 2f8c31c against master
  f81fc19, Release net10.0, four cores: **PASS**.
  IronRuby.Tests (`parallel-tests.sh`): 1,347 tests on the candidate, 1,345 on the baseline, every one
  executed, no failed or flaky name on either. The script says INCOMPLETE and exits 2 on both trees
  because it reads the driver's `Current culture: ` banner (an empty culture name in this container)
  as a test; every listed test had an `Executing` line and every process ended `PASSED`.
  `gate-matrices.rb --all` in 13 modes (the six default ones, and each switch off: `IR_LIGHT_GLOBALS`,
  `IR_LIGHT_LEXICAL`, `IR_JIT_DEADCONST`, `IR_FAST_SEARCH`, `IR_FAST_NEXT`, `IR_FAST_STRUCT`,
  `IR_JIT_DMETH`): 1,699 same, 403 baseline, no DIFF of the change's. callable-parameters-matrix's
  `method(:puts).to_proc.source_location` is a prelude line on both trees (nil in CRuby) and moved from
  ruby4.rb:13514 to 13519 because the merges added five lines above Kernel#puts; thread-backtrace-matrix's
  main-thread row prints line 87 or 88 by timing on both trees (88, CRuby's, in six runs of each alone).
  ffi-matrix was `cruby!` until the ffi gem was installed for CRuby, then baseline in every mode.
  A probe file for the gate's questions - a million deep through a light frame that reads a global, one
  with a class variable, a define_method body and a keyword Struct initialize (SystemStackError);
  private, protected and `private :x` in a subclass around define_method bodies; redefinition, a
  singleton method and remove_method half way; `$!` around the cursor's deferred StopIteration, loop's
  result, the cause and nested loops; keyword Struct new, unknown and String keys; a global read in a
  hot loop and assigned from another thread - prints CRuby's output on the candidate with the JIT on
  and off (master differs in String keys, which perf/struct fixed).
  ruby/spec, all five suites (32,285 examples): no failure the known list does not name. One job
  (spec/core/thread in one process) hung once under the sweep's load; ci-sweep ran its files one at a
  time, all with summaries, and the batch then passed nine times out of nine (seven on the candidate,
  two on master, 17-21 s each).
  AWFY, 8 iterations: all fourteen verify. Timing at load about 1: micro-table in both orders, no line
  more than 5% slower in both (`rangelit` 1.05 / 1.04, `veach1` 1.04 / 1.02; `empty` 1.22, `cmp` 1.19,
  `poly2` 1.10 and `veach100` 1.08 in one order only). AWFY 12 in both orders: geomean new/base 0.988
  and 0.993, none more than 5% slower in both (NBody 1.016 / 1.059; Havlak 0.91 / 1.11).
  Start-up on the R2R host, best of ten alternating, base -> new ms: `-e 1` 451 -> 439, `gem --version`
  472 -> 444, `gem list` 779 -> 874, `require "irb"` 871 -> 857, irb 1+1 1012 -> 999; `gem list` again,
  twenty alternating runs each: best 865 -> 881, median 937 -> 942 (the 779 was one fast run).
  This container refuses perf_event_open, so there is no `ab.rb -i` here; perf/lsite's
  `Util/perf/callgrind/count.sh` counts instructions instead.

- **perf/extiter3 (2026-11-26) - `with_index`, `each_with_index` and `with_object` enumerators
  take `next` on a cursor over their source's (gap 35)** - gated 2026-11-27 (round 4's gate entry). Base 1efd7e6 (the merged
  branch at 2f8c31c with perf/extiter2). The prelude's Enumerator#with_index (and so
  #each_with_index) and #with_object made without a block are generators; they now mark the
  enumerator they return with its source and offset or memo (`__iter_index_source__`), and
  `TryCreateCursor` gives a marked one, while its generator is the one marked and its #each the
  built-in one, an `IndexCursor` over the source's own cursor (any cursor: Array, Hash, Range,
  times, each_slice, map / select, another with_index): [value, index] or [value, memo], what
  the block answers handed on to the source's suspended yield (MRI's with_index_i and
  with_object_i answer it). Enumerable#each_with_index has one too now over a Hash, a Range or a
  Struct (it had only Array's). An offset that is not a Fixnum, a copy (#dup), and a generator
  replaced by #initialize keep the fiber. Two prelude fixes go with it, in every mode: the
  generator of #with_index answered `i += 1` to its source, so `[1, 2].map.with_index` fed `:q`
  collected [2, 3] (CRuby [:q, nil]) and `select.with_index` kept everything; and #with_object's
  StopIteration#result was the source's answer, not the memo. `IR_FAST_NEXT=0` leaves the new
  cursors to the fiber. `ab.rb -r 6`, ns per `next`, CRuby / base / off / new: `[1000].each.
  with_index` 267 / 10798 / 6302 / 325 (0.05 of off), a Hash's `each_with_index` 335 / 12397 /
  8468 / 304, `map.with_index` 283 / 6826 / 6448 / 368, `each.with_object` 250 / 5714 / 6183 /
  204; perf/extiter's and perf/extiter2's shapes level with base. In that run `lazy_next` read
  2.42 and `zip_enum` 1.28 of off; alone over six rounds every control overlapped, and over
  eight `lazy_next` was 0.97 and `zip_enum` 0.99. `Util/extiter-matrix.rb` (222 probes: offsets
  nil, negative, Float, #to_int, Bignum, at the Fixnum edge; every source; results; feed through
  map / select / reject; nesting; each_slice; a generator source; peek and next_values; loop;
  rewind; mutation; dup; #initialize again; a subclass's #each redefined; threads) prints CRuby's
  output on new and off, and the base's on all but the 8 probes the prelude fixes correct;
  enum-next-, block-loop-, hash-order-, raise-, full-message-, enum-, enumerable2- and jit-matrix
  are the same on base, new and off. spec/core/enumerator, kernel/loop_spec, spec/core/hash and
  spec/core/enumerable: 1,541 examples, the same one error in all three (Hash.try_convert's
  known `into`/`to` wording); 117 named IronRuby.Tests (Enumerator*, Enumerable*, Array*, Hash*,
  Loop*, Fiber*, Backtrace*, Exception*, Raise*, Rescue*) pass.

- **perf/extiter2 (2026-11-26) - `map`, `collect`, `select`, `filter`, `find_all` and `reject`
  enumerators take `next` on a cursor, with no fiber (gap 35)** - gated 2026-11-27 (round 4's gate entry). Base: the merged
  branch at 2f8c31c, which has perf/extiter. An enumerator of Array's own #map / #collect /
  #select / #filter / #reject, or of Enumerable's #map / #collect / #select / #filter /
  #find_all / #reject over a receiver whose #each a cursor walks (Range, Hash#map and
  #find_all, Struct, Integer#times' enumerator), runs on a cursor once the methods involved
  are checked to be the built-in ones (`IsBuiltinMethod`, as for the other cursors): the
  element comes from the source's cursor, and what the suspended block would have answered -
  the value fed with #feed, or nil - is kept for StopIteration#result (`ExternalCursor.Resume`,
  `Result`): the answers for #map, the elements answered true for #select (Array's re-read
  after the block, MRI's rb_ary_elt), false for #reject (Array's as yielded). No Ruby code runs
  in the cursor. `IR_FAST_NEXT=0` leaves them to the fiber (and undoes perf/extiter too).
  `Util/bench/extiter-shapes.rb`, `ab.rb -r 6`, ns per `next`, CRuby / base / off / new:
  `[1000].map` 245 / 9412 / 9944 / 92 (0.01 of off), `select` 226 / 5024 / 5123 / 77,
  `(1..1000).map` 259 / 5174 / 9478 / 102, a Hash's `find_all` 365 / 7427 / 7727 / 369 (0.05);
  the cursor shapes of perf/extiter level with base. The fiber controls read 1.1-2.3 times off
  in that run, after shapes that no longer start a fiber; alone (`-- ewi_next lazy_next
  produce generator`, six rounds) 0.95-1.02, all overlapping. `Util/extiter-matrix.rb` (178
  probes now: each method over each source, with and without #feed, fed before the first
  `next` and after the last, `peek`, `next_values`, `loop`, `rewind`, mutation during the
  iteration, a subclass, a singleton's and a redefined method and #each, threads) prints
  CRuby's output on base, new and off; enum-next-, block-loop-, hash-order-, raise-,
  full-message-, enum- and jit-matrix are the same on base, new and off. spec/core/enumerator,
  kernel/loop_spec and the array and enumerable map / collect / select / filter / find_all /
  reject specs (540 examples) pass in all three; 95 named IronRuby.Tests (Enumerator*,
  Enumerable*, Array*, Loop*, Fiber*, Backtrace*, Exception*, Raise*, Rescue*) pass.

- **perf/lexical2 (/perf-step, 2026-11-26, on 2f8c31c) - keywords beside a rest or post
  parameter, and a hash passed on as keywords, in light frames (`IR_LIGHT_KEYWORD_REST`,
  `IR_LIGHT_KEYWORD_SPLAT`, gap 9)** - census only, gated 2026-11-27 (round 4's gate entry). Chosen over the other "not a
  light frame" lines of the census of 2f8c31c because it is the one the prelude has: 24 of its
  101 methods (`String#each_line`, `String#lines`, `IO#initialize`, `File#initialize`,
  `Data#initialize`, `Kernel#open`, `IO.readlines`, `Enumerator.produce`), against 2 of the 71
  LambdaDefinition methods (whose Proc makes the scope anyway) and 4 `defined?` of anything else;
  "body needs its scope after the rewrite" (104) is many causes. perf/lkw had left the line out
  without an attempt ("the general lowering binds the positionals out of one splat with helpers
  `IsScopeFree` does not take").
  - The lowering is `def m(*?args?)` and a prologue of `SplatParameterRead`s
    (PrismAstBridge.LowerGeneralParameters): one runtime call each on the hidden local, none
    taking the scope but `PopKeywordArguments`, which reads the runtime's equality comparer for
    the empty hash of a call without keywords. The rest Array is bound by the RubyMethodInfo in
    either frame, so the prologue empties the same call-owned Array (the matrix checks that the
    caller's splatted Array and double-splatted Hash are left alone). A signature a
    parameterless `super` cannot rebuild by name (an anonymous rest with posts) keeps its copy
    of the splat (`SuperSplat`) and its scope.
  - That alone: +58. 30 more then passed the filter and failed in the rewriter because they pass
    `**opts` on - the shape perf/lkw also named ("a body that passes `**opts` on (52)") and that
    every light-frame candidate had: `KeywordArgumentSplat`'s helpers (`SplatKeywordHash`,
    `SplatKeywordHashIfHash`, `KeywordSplatArgument`, `PassOnKeywordRest`) read only the
    runtime's equality comparer. `IR_LIGHT_KEYWORD_SPLAT` alone +70; both +158. `a: 1, **h`
    (KeywordSplatMerge, 19 methods) is still refused by `IsScopeFree`.
  - Census (Src/StdLib + AWFY, 28,376 methods): light frames 26,535 -> 26,693 (93.5% -> 94.1%),
    158 moved up, none down; JIT compiles / maybe / never unchanged (3,296 / 2,923 / 22,157);
    "body needs its scope after the rewrite" 104 -> 34. No AWFY method moved. `-c` on a
    Richards trace 0 differ.
  - Instructions a call (`Util/perf/callgrind/count.sh` through `ab.rb -i -r 1 -n 20000`, both
    switches off -> on, new shapes in `prelude-cost.rb`): `kr_rest` (`def rest(*a, k: 1)`)
    5,720 -> 5,325 (0.93), `kr_pass` (`pass(*a, **o) = inner(*a, **o)`) 12,272 -> 11,359
    (0.93), `kr_lines` (`"ab".each_line(chomp: true) { }`) 17,670 -> 17,436 (0.99); the
    controls `kr_kw` (`def kw(a, k: 1)`, light before) and `x_const` 1.00. Below perf-frame's
    15% bar for a shape: the scope was not most of what these calls cost. `kr_rest` is still
    3.4 times `kr_kw`; the next step for this shape was thought to be the lowering itself -
    binding the positionals in the calling convention - which perf/lexical3 measured and
    rejected: `kr_kw` was specialized by the method JIT, and the cost is the keyword call into
    a rest and the keyword prologue (gap 38). Not timed.
  - `Util/keyword-rest-light-matrix.rb` (new: the signatures, binding errors, positional Hash,
    `**nil`, defaults, binding, local_variables, block_given?, zsuper with a rest and with
    `**rest`, super_method, send, Method#call, to_proc, arity and parameters, redefinition,
    TracePoint, `**h` with nil, `{}`, to_hash, non-Hashes, the caller's Hash not aliased, and
    String#each_line, #lines, Data#initialize, Array#sample, Kernel#warn) prints CRuby 4.0's
    output on the unchanged build, and on the change by default, with either switch off, with
    `IR_LIGHT_FRAMES=0`, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls` and R2R; the keyword, lexical,
    jit, jit-calls, block-given, light-rest, splat-pass and hash-literal matrices agree with the
    baseline and CRuby. IronRuby.Tests `Method_LightFrameKeywordRest1` (new) and 102 named
    tests pass.
  - Two separate commits fix what perf/lexical found: `defined?(@@x)` and
    `class_variable_defined?` answer true for a class variable an ancestor overtook
    (`RubyModule.IsClassVariableDefined`, MRI's rb_cvar_defined), and a method defined in a
    `refine` block reads the class variables of the module around it (the class-variable walk
    passes over a refinement, as vm_get_cvar_base passes over a cref pushed by an eval). Tests
    `ClassVariables_DefinedOvertaken1` and `ClassVariables_Refinement1` have CRuby's output;
    `Util/lexical-light-matrix.rb` probes both again and prints CRuby's output in every mode.

- **perf/deadconst2 (/perf-step, 2026-11-26, on claude/happy-gauss-95wm2j 2f8c31c) - a typed method body
  whose loop folded a constant sees it reassigned at the loop's next safe point (gap 29)** - gated 2026-11-27 (round 4's gate entry).
  The fault perf/deadconst found: a method body's `while` that folded a constant, with nothing else that
  may deopt (a Float counter: an Int one's overflow check is a deopt), went on with the old value after
  another thread reassigned the constant, until the call returned; CRuby's next read sees the new one.
  The reassignment did move the code epoch (`RubyContext.ConstantReassigned`) and the safe point saw it,
  but `MethodLoopSafePointSlow` deopts only a body that may (`JitLoopGuard.MayDeopt`). Now
  `ResolveConstant` notes a constant folded in or after a loop of the method body's own, and `TryCompile`
  makes such a body one that may deopt (the rule perf/deadconst had for a left-out arm, which this
  replaces); one with an effect is declined, as a loop body that can deopt with an effect always is. One
  that folds a constant after calling a bound callee with a loop is declined (the callee's safe point
  looks at its own constants only). And the safe point, before deopting, looks the body's folded
  constants up again (`JitLoopGuard.ConstantsStillHold`, the entry's `RevalidateConstants` for a running
  loop: the method version unchanged, each constant the same object in the same module, the version read
  before the lookups stored after): a reassignment of another constant lets the loop go on, where before
  a body that could deopt deopted for any reassignment anywhere. rminor's trouble (a name cannot tell
  which new constant concerns a fold) does not arise: a reassignment moves the epoch already, and what
  is compared is the folded constants' own lookups.
  Census (Src/StdLib + AWFY): 0 methods changed, as it must (it compiles no constant). All fourteen AWFY
  benchmarks traced (3 x 1, `IR_JIT_VERBOSE` and `IR_OSR_VERBOSE`): the same methods specialized and
  declined and the same loops specialized and refused on both builds. Richards `census -c` 0 differ.
  Instructions a call (`Util/perf/callgrind/count.sh`, `ab.rb -i -n 20000`, base / new,
  `Util/bench/deadconst-shapes.rb`): `mfloop_const` (a method's 8-iteration Float loop adding a constant,
  the kind this makes deopt-capable) 1575.0 / 1650.8 = 1.048 - the entry's deopt handler and entry count,
  once a call, not per iteration; its control `mfloop_plain` (the step a parameter) 1.002; `mloop_plain`
  (an Int loop) 1.004, `mloop_dead` 1.000, `m_dead` 1.000, `m_plain` 1.000, `loop_dead` and `loop_plain`
  (OSR) 1.001. Ordinary loops count the same.
  Checked: `Util/deadconst-matrix.rb` gains rows 26-29 - a folded step and a folded bound of a method's
  Float `while` reassigned by another thread (both `:limit` on the unchanged build, CRuby's `:seen` now;
  the safe point deopts), the same with an instance variable written in the loop (declined on both
  builds), and another constant reassigned 200 times while a folded loop runs (the right sum; no deopt in
  the trace). The matrix prints CRuby's output on the new build with the JIT on, `-X:NoJIT -X:NoOSR`,
  `-X:NoJITCalls` and `IR_JIT_DEADCONST=0`; jit, jit-calls, jit-const, block-loop, osr-array and dmeth-jit
  matrices print CRuby's on both builds in all four modes, inline-matrix the baseline's (its documented
  `tree_int_key`). IronRuby.Tests Jit* and Loop* (21) pass.
  For the gate: a pure body with a loop that folds a constant now has a deopt-capable entry (+76
  instructions a call on the Float shape); a caller that binds such a body treats it as one that can
  deopt (`code.Deopts`), which could decline a caller with an effect before the call - none of the
  fourteen benchmarks has one. Look at the order in `ConstantsStillHold` and at `Folded` being set
  before `LoopGuard.Set` publishes the epoch.

- **perf/jitscreen2 (2026-11-26) - a multiple assignment of a list in a body the method JIT
  compiles** (`a, b = b, a`, `@a, @b = x, y`) - census only, gated 2026-11-27 (round 4's gate entry). With the census
  counting honestly, the lines after the Hash literal were String literals (101) and multiple
  assignment (75). The String literals left are arguments of calls on self (`add_tag :BOLD,
  "\e[1m"`, Json's `read_char('-')`: taking them was perf/strlit's rejected first build, gap 1's
  wall) and operands of String operators (`text << " @ "`, `@user + ':'`, `'%x' % n`: String
  intrinsics, not a screen step). Multiple assignment had no JIT attempt (perf/masgn was light
  frames). Of the 75: about 47 destructure one value (`a, b = f(x)`, `x, y = pair`), which needs
  `to_ary` - Ruby code run from the typed body, or a deopt after the effect that made the value,
  which the compiler declines (the trap) - and are left; the list form is taken.
  `EmitParallelAssignment` follows the generic body (`CompoundLeftValue.TransformWrite`): every
  right-hand value into a temporary, left to right, then each target written left to right,
  nil for a missing value, an extra one evaluated and dropped. Each write is `EmitAssignment`
  of `target = value` with the value an `EmittedValue` (a node the typed compiler alone makes),
  so locals keep their types and an instance variable write its effect and frozen-self deopt
  exactly as two statements would. Targets: locals of the method and instance variables; no
  `*rest`, no nested target, two or more values, none splatted (`IsListAssignment`). The
  expression's Array is made only where its value is used (not before the last statement of a
  list).
  JIT compiles 11.9% (3,370 -> 3,376), maybe 2,981 -> 2,989, never 22,025 -> 22,011, light
  frames unchanged (Src/StdLib + AWFY, 28,376 methods; 14 methods moved up - 6 to `compiles`, 8
  to `maybe`, all 8 at a call - none down). `ParallelAssignmentExpression` alone 75 -> 60 (the
  destructuring forms). Moved: initializers (`Benchmark::Report`, `RDoc::Markup::Table`,
  `Net::IMAP::ResponseReader`, `IRB::Vec`), `URI::Generic#set_authority` (three copies, at
  `default_port`); none of AWFY or the prelude.
  Traces: app-shapes specialized 116 -> 117 (`Shop::Line#initialize`), declined after the
  pre-screen 30 -> 30; AWFY Json, Richards, DeltaBlue, CD and Havlak (3 x 10) the same on both
  builds; `census.rb -c` 290 agree, 0 differ, and app-shapes' known 15.
  Instructions a call (`Util/perf/callgrind/count.sh`, `Util/bench/jit-masgn-shapes.rb`, new):
  `x, y = y, x` 2634 -> 1492 (0.57), `a, b, c = x, x + 1, x * 2` 3559 -> 1560 (0.44),
  `@a, @b = x, y` 2483 -> 1948 (0.79), the expression's value used 2608 -> 1954 (0.75), the
  control (`a = x; b = x + 1`) 1518 -> 1519. Timed (`ab.rb`, six rounds): 0.44 and 0.41 for
  the first two, the rest overlapping.
  Checked: `Util/jit-masgn-matrix.rb` (new, 22 rows: swap of Ints, Floats and Strings, three
  values and a Bignum, rotation, instance variables and their swap, a local and an instance
  variable together, a missing and an extra value, the value used and dropped, Float and object
  values, overflow, an effect then a deopt, the order of calls on the right, a Fibonacci swap in
  a `while` loop, a frozen self, redefinition; 17 of 19 `ma_*` methods specialized, the others at
  a call of `note` and at an interpolation's deopts), and jit, jit-calls, jit-const, masgn,
  jit-scan, ivar, jit-hash and jit-construct: CRuby's output and the unchanged build's with the
  JIT on, `-X:NoJIT -X:NoOSR` and `-X:NoJITCalls`, and the unchanged build's with
  `IR_LIGHT_FRAMES=0`; hot-shapes and inline-matrix (one case per process) the unchanged build's
  in every mode, with their known one-row differences from CRuby.
  Also: a per-count body (a method with optional parameters) that declined said "no reason
  recorded" (`TrySpecializePerCount` never set the reason); it now records it as `TrySpecialize`
  does. `CSV::FieldsConverter#initialize`'s line says "the compiler bailed out (see the
  `rejected' line)".
  For the gate: no IronRuby.Tests or spec run; `EmittedValue` is a new kind of node only the
  typed compiler makes (its TransformReadCore throws).

- **perf/jitscreen (2026-11-26) - a Hash literal in a body the method JIT compiles** (`{}`,
  `{a: x}`, `{"k" => v}`, `opts = {}`) - census only, gated 2026-11-27 (round 4's gate entry). The screen refused every Hash
  literal, the largest line of "JIT never, one thing only" that is not a call once the census
  counts honestly (first commit below). `EmitHashLiteral` makes the Hash the generic body makes
  (`HashConstructor.TransformReadCore`): keys and values in order, through the same code
  (`JitRuntime.NewHash`, `NewSymbolHash1..3`, the bodies of `RubyOps.MakeHash` and
  `MakeSymbolHash1..3` with the compiled-for runtime's comparer). Only with keys the comparer
  hashes itself (`IsPlainLiteralKeyed`, the generic body's `HasPlainLiteralKeys`: Symbol and
  String literals, nil, true, false, a Fixnum, or one other numeric literal alone): building it
  then runs no Ruby code - no `#hash`, no `#eql?` - so it is no effect a deopt could repeat, no
  way into deep recursion and no call that needs a stack check (fault eleven under "Faults the
  gate missed" was `{self => 0}`). Up to five pairs; not a keyword-arguments hash, not an
  operand of a binary operator.
  JIT compiles 11.6% -> 11.9% (3,296 -> 3,370), maybe 10.3% -> 10.5% (2,923 -> 2,981), never
  78.1% -> 77.6% (22,157 -> 22,025), light frames unchanged (Src/StdLib + AWFY, 28,376 methods;
  132 methods moved up - 74 to `compiles`, 58 to `maybe` - none down) - census only, gated 2026-11-27 (round 4's gate entry).
  `HashConstructor` refused at all 977 -> 331, alone 136 -> 5 (keys that are not plain literals,
  and literals of more than five pairs, stay refused). Moved: mostly `initialize` methods that set `@h = {}`, `to_h` and
  `marshal_dump` methods returning `{key: @ivar, ...}`, and `opts = {}` defaults; in the prelude
  `NilClass#to_h`, `ObjectSpace::WeakKeyMap#initialize`, `GC.latest_compact_info`; none of AWFY.
  Of the 58 to `maybe`: 33 wait for a call, 12 for a constant, 10 for an index on a parameter
  the census types as an Integer, 3 the census already calls "declined once typed".
  **The one exception to the rules, for the gate:** a trace of `Util/bench/app-shapes.rb`
  specializes 114 -> 116 methods (`Date::Format::Bag#initialize`, `Playlist#initialize`), but
  declines 29 -> 30 after the pre-screen: `CSV::FieldsConverter#initialize(options={})`, whose
  `{}` default was all that kept it out, is declined for a call of `need_static_convert?` on
  self after its instance-variable writes - gap 1's wall, which the unchanged build hits the
  same way for the same method with `options=nil` ("a deopt after an effect (call need?)"). It
  pays the declined stub's delegate hop per call. (Its `declined` line says "no reason recorded":
  the per-count path does not record its reason.) AWFY traces (Json, Richards, DeltaBlue, CD,
  3 x 10): 82 specialized and 19 declined on both builds; `census.rb -c` 225 agree, 0 differ;
  on app-shapes the same 15 screen differences as the baseline.
  Instructions a call (`Util/perf/callgrind/count.sh`, `Util/bench/jit-hash-shapes.rb`, new):
  `{}` 2248 -> 1839 (0.82), `{a: x, b: x + 1}` 3317 -> 2666 (0.80), `{"k" => x}` 3452 -> 3104
  (0.90), an initialize storing `{}`, `[]` and an argument 3021 -> 2607 (0.86), `(x, opts = {})`
  called without opts 14878 -> 2027 (0.14), the control `[x, x + 1]` 1961 -> 1960. Timed
  (`ab.rb`, six and eight rounds, spreads of 10-25% beside other experiments): `opts = {}` 0.74
  and 0.80, `{}` 0.87 and 0.92 (overlapping), the rest overlapping (the initialize 1.13 and
  1.06 with overlapping rounds, 0.86 by instructions).
  Checked: `Util/jit-hash-matrix.rb` (new, 31 rows: a new Hash per evaluation, Symbol keys 1, 2,
  3 and 5, String keys copied and frozen, mixed and Float keys, nested literals, an instance
  variable store, `@m ||= {}`, `opts = {}` fresh per call and given, a Bignum and a Float value,
  a literal after an effect and a deopt, 2,000-deep recursion through a literal, redefinition, a
  frozen self; every `hl_*` method but one specialized under `IR_JIT_VERBOSE=1`), and jit,
  jit-calls, jit-const, hash-literal, jit-scan, jit-strlit, ivar, jit-construct, jit-rescue:
  CRuby's output and the unchanged build's with the JIT on, `-X:NoJIT -X:NoOSR`,
  `-X:NoJITCalls`, and the unchanged build's with `IR_LIGHT_FRAMES=0`. hot-shapes and
  inline-matrix (one case per process) print the unchanged build's output in every mode; each
  differs from CRuby in one row on both builds (`FrozenError` for
  `Encoding::CompatibilityError`; "Object must be of type Int32" for a NoMethodError).
  Tried and left: `NewHash*` among the helpers a body with held-back stores may call (the
  trace says "stores not held back ... a call of JitRuntime.NewSymbolHash1"), as round
  strlit's literal makers were; not tried here.
  For the gate: no IronRuby.Tests or spec run; the decline above.

- **perf/strsearch2 (2026-11-26) - `#start_with?` with one String prefix takes no scope; an
  ASCII prefix is compared in place (gap 21, round two)** - gated 2026-11-27 (round 4's gate entry). Commit dc1d136 on
  `claude/perf-strsearch`, based on the merged branch 2f8c31c. `start_with?` had one overload,
  which takes the caller's scope to store `$~` for a Regexp prefix, so a light frame calling it
  with a String made its scope on the call (`RubyOps.LightScopeRequired`) and called again. A
  second overload takes one MutableString and no scope (not switchable: an overload is
  registered or not); a Regexp, a `#to_str` object or several prefixes still take the first. An
  ASCII prefix of two bytes or more on an ASCII-compatible receiver is compared as characters or
  bytes where both strings are, instead of `GetByteCount`/`GetByte` per byte (which also switch
  a non-ASCII receiver held as characters to bytes); `IR_FAST_SEARCH=0` turns that off. The
  census moved nothing (28,376 methods). Callgrind (`Util/perf/callgrind/count.sh`, N=20000),
  instructions a call, base / off / new: sw1_a 3,473 / 2,645 / 2,724, sw3_a 4,704 / 3,862 / 2,850,
  sw1_u 3,434 / 2,625 / 2,685, sw3_u 4,185 / 3,341 / 2,818, sw1_b 3,331 / 2,522 / 2,601, sw3_b
  4,278 / 3,434 / 2,862; controls idx1_a_e, short_idx, size within 0.6%. The in-place compare
  costs the same for any length (about 2,790), so a one-byte prefix keeps the byte loop; the
  check that decides it costs about 80 instructions there (+3% against off, overlapping in
  time). Time, four alternating rounds, ns, CRuby / base / off / new:

      sw1_a  54.4 / 122.4 /  63.8 /  65.3     sw3_a  85.7 / 194.4 / 138.0 /  88.9  (0.64 of off)
      sw1_u  51.9 / 119.8 /  60.0 /  62.8     sw3_u  92.3 / 167.1 / 107.2 /  90.8  (0.85)
      sw1_b  52.0 / 119.9 /  67.6 /  67.5     sw3_b 105.6 / 179.9 / 115.8 / 101.0  (0.87)
      controls overlap: idx1_a_e 67.2 -> 66.9, short_idx 84.5 -> 87.9, size 48.0 -> 47.2

  Checked: `Util/string-search-matrix.rb` with 1,255 more `start_with?` rows (Shift_JIS trail
  bytes, EUC-JP, broken UTF-8 leads and continuations, BINARY, UTF-16, several prefixes, a
  Regexp among them, nil, none; 57,489 rows) prints CRuby 4.0's output on the base and on this
  build in both modes; spec/core/string (3,963 examples) and spec/library/stringscanner (240):
  the same on both builds, 0 failures, one error on both (`String.try_convert ... raises
  TypeError if it's not a kind of String`, run as a directory).

- **perf/iter (round thirty-seven, 2026-11-26) - a block that holds a block literal reuses its scope
  and the literal's Proc (gaps 13 and 6)** - instructions and bytes, not gated. A block whose
  body has a block literal (`a.each { |x| o.e { |c| s += c } }`, Richards' worker block with
  `DATA_SIZE.times { }`, prism's `each_child_node` walks) made a scope, a Proc and a BlockParam on
  every call: the literal's Proc is over the block's scope, so `BlockDefinition` never let such a
  block reuse its scope (`!HasNestedBlock`) nor the literal its Proc across calls (its `#proc`
  variable is a CLR local of the block's frame). Now a literal written in a block's own body
  registers with the block (`BlockScope.ParkProcVariables`) and is made by
  `RubyOps.InstantiateNestedParkedBlock`; a block all of whose literals do so reuses its scope as a
  block without one does. At the normal end of a call whose scope `LeaveReusableBlockScope` kept on
  the BlockParam, `ParkNestedProc` keeps the last Proc of each literal on the scope
  (`RubyBlockScope.ParkedNested`, a Proc or a Proc[], and the thread) if `IsReusableAndInactive`,
  and otherwise `UnparkBlockScope` takes the scope back: a Proc given to anything but a light frame
  or a [BlockNotKept] iterator, or a scope it ran in captured, keeps the scope from being reused. A
  Proc of a literal the same call replaced may be held anywhere and reaches the scope: it marks the
  scope captured (as `InstantiateParkedBlock` does for a method). The next call that takes the
  scope makes the kept Procs again (`Proc.ReuseParked` with its self, dropping the BlockParam
  another thread made). A lambda, a literal that does not reuse its Proc, or one in a module body
  inside the block keeps the old path (`HasUnparkableNestedBlock`). The first call through a
  BlockParam still keeps nothing (`ParkScope`), so a block run once per BlockParam pays only the
  field. `IR_PARKED_BLOCK_SCOPES=0` (or `IR_PARKED_SCOPES=0`) is the old code.
  Bytes per call (`Util/bench/nest-shapes.rb`, the thread's allocated bytes): the literal inside a
  block 302 -> 46 B, the same as a call without a block. Instructions (callgrind, new over the
  switch off): `light` 0.927, `each` 0.910, `two` 0.946, `kids` 0.953, `el3` 0.999, `el2` 1.026
  (two elements: the second call keeps the scope and nothing reuses it), `once` 1.012 (a first run
  of the whole file said 2.509 for it; counted again alone, twice, 1.012), controls `flat` 1.004,
  `noblk` 1.001, `meth` 0.978. Time, four alternating rounds on a host at load 2-20: `light` 110 ->
  66 ns (0.59; CRuby 48), `each` 114 -> 64 (0.51, overlapping; CRuby 64), `two` 168 -> 127 (0.70;
  CRuby 104), `kids` 0.86, `el3` 0.94, `el2` 1.07 and `once` 1.00 overlapping. alloc.rb, all
  fourteen: Richards 12,843,062 -> 2,444,179 B per iteration, Bounce 7,297,784 -> 5,471,207,
  geomean 0.889; up: Sieve +1.3% (+1.6 MB, the same with the switch off: `ParkedNested` makes every
  RubyBlockScope 8 B larger, and Sieve makes some 200,000 a iteration that are not reused),
  DeltaBlue +0.4%, Permute +0.2%, Havlak +0.2%, CD +0.1%; Towers moves by a third between runs of
  the same build (113 and 147 KB). AWFY under callgrind, instructions per benchmark call, new over
  off: Richards 0.999, Bounce 0.998, DeltaBlue 1.000, Queens 0.998, Permute 1.000 - Richards' bytes
  are not its time (its clock on the busy host: medians 4% either way between processes).
  pwalk36: `walk` 0.995, `plain` 0.989, `each_child` 0.989 and `compact` 1.013 with 5-8% between
  processes (prism's walk collects inside the sections). core-small under callgrind, new over
  base: `each`, `inject`, `each_with_index`, `map_w_index`, `any`, `times`, `upto`, `range_each`,
  `hash_each` 1.000, `find` 0.993, `group_by` 1.001.
  Matrices (`Util/perf/gate-matrices.rb`, base 2f8c31c): `Util/block-nest-reuse-matrix.rb` (new:
  &b kept by a method or in an ivar, the literal made three times in one call with the middle one
  kept, proc, lambda, define_method, Binding of either scope, eval, a Hash default proc, an
  Enumerator, lazy, a Thread, break/next/return/raise from the inner and the outer block,
  recursion through the outer Proc and through the iterator, another self by define_method and
  instance_exec, a Fiber suspended in the inner block, Enumerator#next, two literals one kept, a
  literal in a `class << o` body, four threads calling one outer Proc and running one each,
  TracePoint enabled between calls; CRuby's output on the unchanged build first), with
  block-scope-reuse, -blockpass, -race, block-loop, block-given, light-rest, hot-shapes, jit,
  parked-scope and block-pass, in the seven modes (as built, -X:NoJIT -X:NoOSR, -X:NoJITCalls,
  IR_LIGHT_FRAMES=0, IR_PARKED_SCOPES=0, R2R, IR_PARKED_BLOCK_SCOPES=0): PASS, 69 same, 8 as the
  baseline. A WeakRef probe (receiver and argument of a define_method body with a nested literal,
  arguments of Procs called five times whose scope is kept, a method's arguments, instance_exec's
  self): all freed, on both builds and CRuby.
  For the gate: the 8 B on every block scope (the alternative is the Procs in hidden slots of the
  block's locals tuple, which `MakeClearLocals` would skip); the Proc#call path, where a kept scope
  and its Procs move between threads through `CallBlockParam` (the thread check in
  `InstantiateNestedParkedBlockSlow`, and the Proc[] cleared when another thread keeps); and any
  way a literal's Proc reaches Ruby without clearing its reusable flag.

- **perf/nbody b3c20a5 55be4d2 (round thirty-eight, 2026-11-27) - a block literal with a literal
  nested in it reuses its Proc when it can be outlined (gap 2)** - not gated. NBody allocated 152 MB
  per iteration, 608 bytes per `advance`; an in-process GC allocation-tick listener (EventListener on
  GCAllocationTick) gave RubyMethodScope 26%, Proc 26%, BlockParam 16%, Int32 16%, Double 12% of them.
  `advance` is a light frame whose literal blocks could park its scope with their Procs
  (RubyOps.CreateMethodScopeParked), but a literal with another literal nested in it never reused its
  Proc (BlockDefinition: `!HasNestedBlock`), since the nested Proc holds its scope - although, once the
  `each_index` block is outlined, the nested `Range#each` runs in place (perf/nest) and no nested Proc is
  ever made. Now a block the block-loop screen accepts (BlockLoopInfo, read from the AST before the body
  is compiled, `BlockScope.ReusesProcWithNested`) reuses its Proc all the same, and a literal nested in
  it, on the generic calls that do make its Proc, clears the reuse of every Proc of the block scopes it
  is made in (`RubyOps.KeepEnclosingProcs`, [Emitted]): a Proc a nested Proc may reach is never made
  again, so never staged for parking (StageParkedProc), so the method's scope is not parked on that call.
  Nothing else changes for such a block: its scope is still not reused, its BlockParams are handed on as
  before (a first version marked the scope captured, which set KeptFlag and cost Richards' worker
  Proc#call its BlockParam reuse: +9% bytes). Nested literals no longer count against their method's
  parking. The calls before the loop is outlined make the nested Proc, and a slot gives up after 64
  failures; the first run of the copy gives the block's method's slot one more try
  (`BlockLoopInfo.ParkSlot`). A first version let every block with a nested literal reuse its Proc: 2.1%
  and 2.4% more instructions on a nested lambda and an `Array#each` in `Hash#each`, which make the nested
  Proc on every call and gained nothing; hence the screen. Switch: IR_NESTED_PROCS=0.
  Bytes (Util/perf/alloc.rb): NBody 152,025,847 -> 48,032,633 per iteration (0.316); the other thirteen
  level (geomean of the fourteen 0.912). `Util/bench/nested-proc-shapes.rb` (new), bytes per call:
  `advance` 593 -> 177, the five others unchanged. Instructions (Util/perf/callgrind/count.sh, switch off
  -> on in one tree; a process lands in one of two modes some 400 instructions or 4% apart, so pairs are
  compared mode with mode): `advance` 53,326 -> 53,354 or 51,427, `nest` (the same nesting over
  Integers, light body) 41,977 -> 42,321 and 41,578 -> 41,920 (+0.8%: parking's bookkeeping costs a
  little more than the allocations it saves), `always`, `lambda`, `worker`, `flat` level. Time could not
  be resolved on this machine (thirteen experiments at once; ab.rb spreads 20-30%, a control at 0.75):
  NBody 0.924, 0.990, 0.982, 1.007 of the base in four AWFY runs at 10 s, the fourteen 1.022 and 0.991
  in both column orders, no FAIL; NBody prints the same with -X:NoJIT -X:NoOSR. The census does not see
  this (0 methods changed).
  Matrices (Util/perf/gate-matrices.rb, base 2f8c31c): nested-proc (new: kept nested Procs of hot
  parked methods, lambda, proc, Proc.new, binding, define_method and Thread nested, the outer literal made
  twice in one call, break/next/return, recursion, an Enumerator, threads, and an outlined block whose
  nested `Range#each` or `times` is redefined mid-run to keep its block), parked-scope, ivar, int,
  block-loop, osr-array, jit, jit-calls, hot-shapes, block-scope-reuse, block-given, block-pass,
  binding-lvar, eval, in the gate's six modes and IR_NESTED_PROCS=0: 22 baseline, 76 same, no DIFF.
  `Util/ivar-stress.rb` OK three times. With KeepEnclosingProcs made a no-op the new matrix's rows go
  wrong (NullReferenceException, a kept Proc reading another call's locals), so it does probe the rule.

- **perf/singleton (round thirty-seven, 2026-11-26) - lookups on a re-versioned singleton class
  answered from its superclass's cache (gaps 32, 19)** - not gated. Each definition on an object
  that gets methods of its own (OpenStruct's `define_singleton_method!` of every member)
  re-versions its singleton class and so empties the class's method lookup cache; every name asked
  again - the `singleton_method_added` hook check, `respond_to?`, the shared rules' version test -
  then walked the ancestors under the class hierarchy lock and copied the cache's whole table to
  file one entry, about thirty times per OpenStruct. Now a singleton class of a plain object with
  no modules of its own and at most 16 methods keeps a snapshot of its own method names per
  version (`RubyModule.InheritedLookups`) and answers every other name from its superclass's
  cache, which stays warm across all such objects, without the lock and without filing. The
  superclass's fill registered the answer for invalidation, so defining that name re-versions the
  singleton class; defining a name nobody asked for does not, so every change of a module's
  method table or modules drops the snapshot under the lock, and a reader checks after taking the
  superclass's answer that the snapshot is still current (a superclass fill that registered the
  name took the lock after any definition that had not seen the registration). And
  `RubyCallAction.TryShareVersion` takes a new version from that cache when it finds the rule's
  method there, at a visibility the call sees, before resolving under the lock
  (`TryShareVersionFromLookup`; not for method_missing, a refined name, a protected method).
  `IR_SINGLETON_LOOKUP=0` turns both off.
  Instructions per call (`Util/perf/callgrind/count.sh`, N=200; base / off / new):
  `ostr36-shapes.rb` `os_new` 232,692 / 233,695 / 180,601
  (0.77), `os_write` 391,933 / 394,835 / 317,172 (0.80), `os_app` 565,493 / 576,944 / 482,054 (0.84),
  `dsm_blk_call` 73,982 / 89,375 / 70,403 (0.95 of base), `dsm_tbl_call` 78,759 / 78,681 / 74,836 (0.95);
  `app-shapes.rb` `ostruct` 5,808,127 / 5,829,182 / 4,932,345 (0.85); controls level: `delegate`
  9,469,818 / 9,470,041 / 9,473,484, `forwardable` 4,204,623 / 4,163,226 / 4,204,592. Not timed
  (timing on this machine spread more than the change); `sdef-shapes.rb` has no counter.
  `Util/sdef-matrix.rb` section 12 (new: a name defined that nobody had asked for, `def o.x` after
  lookups, undef/remove and `private` in the singleton class, a private forwarder in a subclass,
  protected, `respond_to_missing?` and `method_missing` added to the class later, the
  `singleton_method_added` hook, include and prepend into the singleton class, `extend` of a module
  with `extended`, a refinement, two threads defining and removing while a third asks) prints CRuby
  4.0's output on both builds; with the snapshot drop after a definition taken out, its first two
  rows go wrong. `gate-matrices.rb` on hot-shapes, shared-rule-light, jit, jit-calls, sdef, extend,
  respond-to, refinement, prepend (both), dmeth, struct, delegate and forwardable, in the seven
  modes with `IR_SINGLETON_LOOKUP=0`: PASS (83 same, 15 baseline: struct-matrix and hot-shapes
  differ from CRuby only as the baseline does). ruby/spec: library/openstruct, and
  core/kernel define_singleton_method, extend, singleton_class, singleton_methods, respond_to,
  public_send, method, core/basicobject/singleton_method_added, core/module method_defined,
  prepend, include, refine, private, remove_method, undef_method, language/def, core/marshal/dump:
  no failures on either build.

- **perf/lglobal (round thirty-seven, 2026-11-26) - a global variable in a light frame (gap 9)** -
  census only when merged, gated 2026-11-26 (round thirty-seven's gate entry). `IsScopeFree` had no case for a GlobalVariable, so `$stdout`, `$VERBOSE`,
  `$/`, `$!` or `$x = v` kept 258 methods out of light frames by themselves (Kernel#puts among them,
  on every call of `puts`). Only `$~` and `$_` (and what is derived from `$~`) live in the frame
  (RubyScope.GetInnerMostClosureScope); every other global is the runtime's (SpecialGlobalVariableInfo,
  GlobalVariableInfo). So a light frame now reads and writes any global but those two names through
  the declaring scope: `RubyOps.ReadLightGlobalVariable`, `GetLightGlobalVariable` and
  `SetLightGlobalVariable` take the place of the cached helpers (LightFrameRewriter.VisitGlobalVariableCall),
  and `defined?($x)` hands `IsDefinedGlobalVariable` the declaring scope. A name can still denote `$~` or
  `$_` - an alias, English.rb's `$LAST_MATCH_INFO` - so the helpers decide from the variable the site
  finds (`GlobalVariable.ReadsScope`, true for a kind this assembly does not know) and answer
  `LightScopeRequired`, having done nothing, when it would read the frame; the frame then makes its
  scope and does it again with the cached helper, the value assigned evaluated once. The same while a
  trace_var handler is registered, for a non-main Ractor, and for an assignment that creates the
  global. What is read or assigned is the variable the decision was made for
  (`RubyContext.SetGlobalVariableValue`), not one looked up again, which another thread may have
  aliased in between. A name that already denotes `$~` or `$_` when the body is compiled keeps the
  scope (the rewriter is given the runtime): reading English's alias light made the scope at every
  read, 1.43x. The frame keeps its stack check: `$FILENAME` asks ARGF.filename, `$stdout = o` asks
  `o.respond_to?(:write)`, and a trace_var handler runs Ruby, any of which may call the method again.
  A global in a body of more than 24 calls keeps its scope (the masgn limit): `String#__ir_split_lines__`
  (`$/` and some forty calls) was 17,890 bytes of IL light against 7,652 with its scope, and
  `"a\nb\n".lines` 1.10x slower in six alternating rounds alone; 25 methods are held back by it.
  `IR_LIGHT_GLOBALS=0` turns it off.
  Light frames 91.7% -> 92.6% (26,019 -> 26,257), light blocks unchanged, JIT unchanged (Src/StdLib +
  AWFY, 28,376 methods; 238 methods moved up, none down). Of the prelude: Kernel#puts,
  Exception.to_tty?, Process.last_status, Process::Status.wait, GC::Profiler.report,
  Enumerator::Generator#initialize; none of AWFY's. `census.rb -c` on traces of
  `Util/bench/app-shapes.rb` (920 methods called) and of the matrix (175): light frames 0 differ;
  declined 29 and specialized 115 on both trees.
  Timed (`ab.rb -r 6`, `Util/bench/global-shapes.rb`, pinned, load 0.5-1.8; this container has no
  perf counters, so no `-i`), ns a call, off -> on: `defined?($g)` 91.7 -> 60.4 (0.66), `def m(sep =
  $/)` 94.1 -> 63.9 (0.68), `$stdout.equal?(STDOUT)` 95.6 -> 72.0-83.9 (0.71-0.88), `$VERBOSE` 79.9 ->
  72.0 (0.84-0.90), `x + $g` 80.0 -> 47.9-71.9 (0.56-0.90), `puts` to a null writer alone 358.7 ->
  316.4 (0.88, overlap); `$g` alone and `$g = v` are compiled by the method JIT either way (overlap);
  controls (an ivar, a method that names `$~`) overlap. `lines` and the alias read looked 1.3-1.5x
  slower late in the sequential script and are the same alone (0.97, 1.00) and as a pair (the same
  minimum on both trees): a state of the process, as round eight describes.
  Checked: `Util/global-light-matrix.rb` (new: reads and writes of plain and special globals,
  read-only ones, `$stdout` to a writer and to something without #write, `||=`, `&&=`, op-assign,
  parallel assignment; `$~`, `$_` and aliases of them made before and after the methods were compiled,
  in a block, in another thread, with print; trace_var with a block, a String and a handler that
  assigns its own variable; defined?; send, bind_call, define_method, binding, eval, raise, `$!` in a
  rescue, caller, a million deep, TracePoint) prints CRuby's output on the baseline and on the
  candidate as built, `IR_LIGHT_GLOBALS=0`, `IR_LIGHT_FRAMES=0`, `IR_PARKED_SCOPES=0`, `-X:NoJIT
  -X:NoOSR`, `-X:NoJITCalls` and the R2R host. gate-matrices.rb on 14 matrices (global-light,
  block-given, block-scope-reuse, jit, keyword-light, light-rest, loop-light, masgn, hot-shapes,
  regex-light, rescue-light, shared-rule-light, splat-pass, super-light) in those seven modes: PASS,
  90 same, 8 baseline (hot-shapes in every mode and regex-light under R2R, as on master).
  `Method_LightFrameGlobal1` (new) and 27 named global tests pass.

- **perf/lexical (2026-11-26) - class variables, `defined?` of a constant and `{expr}::C` in
  light frames (`IR_LIGHT_LEXICAL`, gap 9).** The census of master f81fc19 had three lines of
  "not a light frame: the construct" that are lexical lookups: ClassVariable 90 methods,
  `IsDefinedExpression of ConstantVariable` 89, `ConstantVariable with an expression qualifier`
  80. Their helpers (`RubyOps.GetClassVariable`, `TryGetClassVariable`, `IsDefinedClassVariable`,
  `SetClassVariable`; `IsDefinedUnqualifiedConstant`, `IsDefinedQualifiedConstant`,
  `IsDefinedGlobalConstant`, which `ScopeOperandIndex` already listed; `GetExpressionQualifiedConstant`,
  `IsDefinedExpressionQualifiedConstant`) read the runtime, the global scope and the lexical chain
  from the scope: `RubyScope.GetInnerMostModule` and `TryResolveConstantNoLock` walk `Module` up
  the parents, and a method scope's `Module` is null and it is no instance_eval scope, so the walk
  from it is the walk from its parent - the declaring scope the rewriter hands them (the scope
  made on demand has the same parent). A block scope has no module either, so a light block's
  `scope ?? parent` stands in the same way. `IsScopeFree` lets in, under the switch, `@@x`,
  `defined?(@@x)`, `@@x = v` and its op-assigns (operator ones need `IR_LIGHT_CALLS` as for `@x`),
  and `defined?`/a read of any constant whose innermost qualifier is a constant or a scope-free
  expression; `ScopeOperandIndex` hands the five helpers not yet listed the declaring scope; with
  the switch off `IR_LIGHT_FRAMES=why` names it. `x::A = v` stays out. Census (Src/StdLib + AWFY,
  28,376 methods): light frames 26,019 -> 26,293 (91.7% -> 92.7%), 274 methods moved up, none
  down; JIT compiles / maybe / never unchanged (3,296 / 2,921 / 22,159). The prelude's
  `SystemCallError.__coerce_errno__` is among them, no AWFY method (AWFY has none of the three).
  `ab.rb -r 4` on new shapes of `prelude-cost.rb` against the switch off (no perf counters in
  the container, so time, not `-i`; the machine was shared by four other experiments): `x_cv`
  (`@@cv`) 118.2 -> 80.9 ns (0.68), `x_defc` (`defined?(C) ? 1 : 0`) 80.4 -> 40.0 (0.50),
  `x_qual` (`self.class::C`) 70.6 -> 41.8 (0.59); `x_const` (a plain constant, light before)
  36.0 -> 35.7 (0.99 overlap). `Util/lexical-light-matrix.rb` (class bodies, `class << self`,
  module_eval with a String and with a block, define_method, the top level, instance_eval, a
  module function, bind_call, a nested class, an ancestor overtaking a class variable later,
  constants and class variables defined and changed after the site is bound, private constants,
  const_missing, a raising qualifier of `defined?`, binding, caller, TracePoint) prints CRuby
  4.0's output on the unchanged build and on the change by default, with `IR_LIGHT_LEXICAL=0`,
  `IR_LIGHT_FRAMES=0`, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls` and R2R; jit-matrix,
  jit-calls-matrix, block-given-matrix and light-rest-matrix agree with the baseline and CRuby;
  census `-c` on a Richards trace 0 differ; IronRuby.Tests `Method_LightFrameLexical1` (new)
  and 45 named class-variable, constant and light-frame tests pass. Found on the way, not
  fixed here (fixed in perf/lexical2, and the matrix probes both again): `defined?(@@x)` raises the
  "overtaken" RuntimeError where CRuby answers "class variable"; `@@x` in a method of a `refine`
  block resolves to the refinement, CRuby to the module around it. And CRuby 4.0.10 crashes
  reading a class variable removed after its inline cache was warmed (a dangling cache), so the
  matrix does not remove one. - census only when merged, gated 2026-11-26 (round thirty-seven's gate entry)

- **perf/deadconst (/perf-step, 2026-11-26, master f81fc19) - a statement guarded by a constant whose
  value makes it dead is left out of the typed body and of a loop's copy (gap 1)** - census only when merged, gated 2026-11-26 (round thirty-seven's gate entry).
  `trace(x) if TRACING`, `log(x) unless QUIET`, `if C then a else b end` without an elsif, where C is a
  constant the compiler folds (`C`, `::C`, `A::B`): `JitCompiler.TryEmitConstantGuarded` resolves it as
  any folded read (`ResolveConstant`, so the entry's revalidation, gap 29, covers it) and emits only the
  arm its value leaves alive, without the test. The screen asks the same question of the same nodes
  (`JitCompiler.ConstantGuard`): `JitStub.Wrap` hands it the constant's value now, looked up in the
  declaring scope as the compiler will (`JitFoldedConstant.Resolve`), and it walks only the live arm; the
  census and the precompiled library, which have no values, take a guard of one arm for a switch that is
  off. A loop runs on past the entry, so a method body with a loop (its own or an iterator's block) that
  left something out is made one that may deopt (`_canDeopt`): its safe point then deopts when the constant
  is reassigned (`MethodLoopSafePointSlow`), which only a pure body may (otherwise it is declined, as any
  loop in a body that can deopt and has an effect); a body that binds a callee with a loop is declined.
  An outlined loop needs nothing: a reassignment moves the code epoch (`RubyContext.ConstantReassigned`)
  and its safe point hands it back. Switch: `IR_JIT_DEADCONST=0`.
  Census (Src/StdLib + AWFY, 28,376 methods): never 22,159 -> 22,157, maybe 2,921 -> 2,923; 2 methods
  moved up, none down - `Scheduler#schedule` (never at `MethodCall trace (self)` -> maybe at "constant
  without a scope: NO_TASK", the census's usual stop) and RSS `ListenerMixin#tag_end` (`p ... if DEBUG`).
  The census cannot see a constant's value, so it was judged by a run and by shapes. Richards traced
  (3 x 10): the screen no longer rejects `schedule`, and nothing else changed - `schedule` is called once
  per iteration and never reaches the method JIT's threshold, and its `while` is still refused by the
  loop tier at its first call on another receiver: `[osr] emitter: call: is_task_holding_or_waiting/0`,
  `richards.rb:260: not specializable` (behind it `@current_task.link` and `.run_task`; `trace` was never
  the loop tier's first obstacle). `declined ... (passed the pre-screen)` 6 -> 6; `census -c`: 49 agree,
  0 differ, 16 specialized all compiles/maybe, 6 declined all maybe. No AWFY benchmark is expected to move.
  `Util/bench/deadconst-shapes.rb` (new), `ab.rb -r 4` by time (this container has no instruction
  counter: `-i` printed nothing), CRuby / base / new ns: `m_dead` (a call on self under a false
  constant, then `x + @k`) 27.3 / 61.2 / 51.3 = 0.84; `m_dead2` (a nil and a qualified guard) 35.5 / 59.6
  / 50.4 = 0.85; `mloop_dead` (the guard in a method's `while`, 8 iterations a call) 223.6 / 240.0 / 60.3 =
  0.25; `loop_dead` (in a long loop, the loop tier's) 17.9 / 34.8 / 2.8 = 0.08; their controls without
  the guard `m_plain` 1.02, `mloop_plain` 0.99, `loop_plain` 1.03, all overlap; each guarded shape is now
  at its control.
  Checked: `Util/deadconst-matrix.rb` (new, 22 rows): between calls the guard reassigned true or false,
  `unless`, if/else both ways, qualified, nil set to 0, removed (NameError), shadowed lexically and through
  a superclass, set to an equal false again, the `if`'s value assigned, in a bound callee, in a method's
  `while`, in a `times` block, a live guard turned off; and another thread reassigning it while a compiled
  loop runs: a method's `while` (Int and Float counters), a top-level `while` (OSR), a `times` block, a
  method called in a loop. CRuby's output on the unchanged build, and on the new one with the JIT on,
  `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls` and the switch off. With the loop rule taken out, the Float row
  answers `:limit` (it is the row that tests it: an Int counter's overflow makes the body one that may
  deopt anyway). jit, jit-calls, block-loop, osr-array, jit-const matrices and inline-matrix (one case per
  process) print the same on base and new in all four modes, CRuby's but for inline-matrix's
  `tree_int_key`, which differs on every IronRuby build (documented in the matrix). IronRuby.Tests Jit*
  (16) pass.
  For the gate: a method that folded a constant in a `while` and has nothing else that may deopt keeps the
  constant's old value after another thread reassigns it, on master too (gap 29); this change fixes it
  only where an arm is left out. The screen's lookup runs at each `def`'s first call, under the class
  hierarchy lock, once per guard. Look hardest at the loop rule (`TryCompile`, `_droppedArm`) and at a
  dropped arm after a call of a bound callee that runs generic code which reassigns the constant - the
  same question every folded constant already raises, not timed or probed beyond row 13.

- **perf/strsearch (2026-11-26) - `#byteindex`/`#byterindex` with a String needle in C#,
  `Regexp#match?` without a MatchData, and two searches that answered wrong (gap 21)** - gated 2026-11-26
  (round thirty-seven's gate entry). Commits ba4a151, c9a4e7c, 95d5891 on `claude/perf-strsearch`; switch
  `IR_FAST_SEARCH=0` (both speed-ups; the fixes are not switched). The census does not see a
  library method's speed (0 methods moved, 28,376); judged by time, since this container has
  no instruction counter: `Util/perf/ab.rb -r 4 Util/bench/strsearch-shapes.rb`, CRuby / master
  f81fc19 / the candidate with the switch off / on, alternating rounds, ns:

      bidx1_a_e   65.3 / 590.4 / 587.6 / 130.8   0.22     mp_a_e   98.2 / 166.0 / 157.5 / 104.0  0.66
      bidx1_a_l  115.7 / 759.6 / 786.9 / 279.2   0.35     mp_u_e  127.6 / 156.0 / 162.2 / 107.9  0.67
      bidx1_a_n  117.6 / 754.9 / 755.9 / 231.0   0.31     mp3_b_e  91.9 / 171.1 / 175.1 / 116.0  0.66
      bidx1_b_n  119.3 / 663.4 / 638.1 / 199.2   0.31     int_mp  119.9 / 164.4 / 165.6 / 108.0  0.65
      controls, all overlap: idx1_a_n 199.3 -> 198.2, idx1_u_l 196.5 -> 200.0, tilde_a_e 247.8 ->
      248.0, int_tilde 248.0 -> 255.3, mp_u_l 396.7 -> 348.0, size 51.5 -> 47.4, eq 74.7 -> 67.6

  On 10 KB of UTF-8 with non-ASCII characters `#byteindex` was 1,118,000 / 1,130,000 /
  1,146,000 ns (found at byte 5 / 30 bytes from the end / not found; master, N=1000) and is
  146 / 1,316 / 239 (CRuby 75 / 117 / 120). What it was: `#byteindex` and `#byterindex` sent
  every call to ruby4.rb's `__ir_byte_search__`, which builds a table of every character's
  byte offset with `each_char` on every call. Now a String needle with a Fixnum offset (or
  none) is done in C#: a receiver whose characters are its bytes needs no conversion, a valid
  UTF-8 one walks its UTF-16 characters to the offset and counts the UTF-8 length of the
  prefix the answer ends; a Regexp, a `#to_str` needle, a Bignum offset, other multibyte
  encodings and broken strings still go to the helper. `Regexp#match?` asks `Regex.IsMatch`
  (but for a subject with a surrogate pair, which needs the Match to drop one that starts
  between the halves). Fixed on the way, both in the new `Util/string-search-matrix.rb`
  (56,234 rows, which now print CRuby 4.0's output, switch on and off; master differs in
  1,098): a needle held as bytes (`'"'.b`) on a receiver held as characters that are not
  its bytes switched the receiver to bytes and answered byte offsets (`"é\"".index('"'.b)`
  was 2); and a Regexp search from an offset outside the string raised the encoding's
  CompatibilityError (or, for `#index`, the broken receiver's ArgumentError) where CRuby
  answers nil first. Checked: the matrix; `astral-string`, `string-methods`,
  `regexp-binary-ctype`, `stdre`, `regex-light` and `regexp-escaped-byte` matrices print what
  master prints; spec/core/string index, rindex, byteindex, byterindex, match, start_with,
  include, partition, rpartition, slice, element_reference, element_set, split, sub, gsub,
  scan, end_with, spec/core/regexp/match_spec.rb, spec/library/stringscanner and
  stringio/gets_spec.rb: 0 failures; 62 named MutableString/Regex IronRuby.Tests pass.

- **perf/extiter (2026-11-26) - the StopIteration that ends a cursor's external iteration defers
  its stack walk, and `loop` reads its result without exposing it (gap 35)** - gated 2026-11-26 (round thirty-seven's gate entry). A
  `next` over a built-in source (Array, Hash, Range, Integer#times...) runs on a cursor with no
  fiber already, at 67-94 ns against CRuby's 226-234; what each such iteration still paid was
  its end: a StopIteration thrown from C# took the catch-site walk of the whole CLR stack (22-24 us,
  CRuby 0.8), where one Kernel#raise makes defers it until the exception is exposed
  (RubyExceptionData.TryDeferCatchSite, RaisedUnkept). Now the cursor's StopIteration is marked
  unkept (`RubyExceptionData.MarkUnkept`) unless a TracePoint is enabled; the first one, which the
  cursor keeps as the cause of those after it, takes the walk when it is handed out as a cause
  (`RubyExceptionData.ExposeCatchSite`, public now). And Kernel#loop rescues StopIteration
  without `=> stop`, which exposed it, reading the result through `Enumerator.__rescued_stop_result__`
  (`$!` read with `RubyContext.CurrentExceptionNoExpose`, public now), unless #result is not
  the prelude's (registered with `__iter_cursor_method__`), when it calls it as before. Switch:
  `IR_FAST_NEXT=0`. `ruby Util/perf/ab.rb -r 6 Util/bench/extiter-shapes.rb` (new), ns per
  `next`, CRuby / base f81fc19 / off / new, new/off: 4-element Array drained with
  `begin/rescue` 1395 / 6275 / 6243 / 1265 (0.20), the same with `loop { e.next }` 1414 / 7028
  / 7052 / 1730 (0.25), `next` again after the end 766 / 23764 / 23893 / 3882 (0.16), Hash of
  100 271.9 against 474.8 (0.57), Array and Range of 1000 and `peek` 0.77-0.79; the controls,
  which keep their fiber (map, select, with_index, lazy, produce, Enumerator.new; 5-11 us against
  0.22-0.36) and zip, overlap (spreads up to 120% on the fiber rows). `Util/extiter-matrix.rb`
  (new, 131 probes: every cursor source, results, causes and their backtraces with the first end
  dropped in a frame that has returned, `loop` with a subclass's, a singleton's and a redefined
  #result, TracePoint, threads, mutation during the iteration, redefinition before and after the
  enumerator is made) prints CRuby's output on base, new and new with the switch off;
  enum-next-, enum-, enumerable2-, block-loop-, loop-light-, full-message-, raise-, jit-raise-,
  hash-order- and jit-matrix the same on base and new. spec/core/enumerator +
  kernel/loop_spec (443 examples) and spec/core/exception + enumerable + language/rescue_spec
  (846) pass on both; 72 named IronRuby.Tests (Enumerator_*, Loop*, Backtrace*, Exception*,
  Raise*, Rescue*) pass.

- **perf/struct (2026-11-26, 0cfab5c..37eae81, gated 2026-11-26) - Struct keyword arguments stored in
  C#; a new struct class made without moving the global method version, its readers and its first
  constructions without rules (gaps 24 and 19).** Four commits, one switch (`IR_FAST_STRUCT=0`).
  (1) `Struct#initialize` (RubyStructOps.Initialize) stores keywords whose keys all name members
  (Symbol or String, MRI's struct_hash_set_i) when keyword_init is true or nil, a single Hash for
  keyword_init: true, and clears every member for no arguments; it sent all of them through a site
  to `__struct_initialize_slow__` in ruby4.rb before (copy the Hash, `members`, `keyword_init?`,
  `keys - names`, a splat). keyword_init is read as MRI's struct_ivar_get does, so a subclass of a
  keyword_init: true struct is keyword_init too, and String keys name members: both were wrong on
  master. (2) Measured first, as the gap asked: a loop calling a specialized method while it made a
  Struct class every 10,000 iterations counted 8 JIT invalidations for 30 classes (Class.new none),
  and eight settle the method on its generic body for good; `new` and `[]` on the singleton class
  override Class#new, and the prelude's `inspect` overrides Module#inspect. An anonymous class whose
  superclass has the default inherited hook has been seen by no code, so its methods go into the
  tables without PrepareMethodUpdate (`RubyModule.SetMethodOfUnseenModuleNoEventNoLock`), events
  after; `keyword_init?` and `inspect` are made in C# with the others, as MRI's setup_struct does
  (so `singleton_methods` lists `:inspect`, as in CRuby). Now 0 invalidations. (3) A member reader
  at a site with a scope (light ones too) gets a `RubyStructReaderDispatcher`: the rule generators
  are closures per member per class, so no rule was ever shared (IR_BIND_TRACE: 32-577 us -> 4-9 us).
  (4) `LibraryDispatcher.TryCreateConstruction` takes struct classes (`TryGetObjectConstruction(count,
  structs: true)`; the method JIT's constructions still take plain classes only): allocate the
  RubyStruct, call a Ruby initialize as for a plain class or Struct#initialize through a site of the
  construction's own, as the rule does (IR_BIND_TRACE: the first `new` 80-160 us -> 10-25 us).
  `Util/bench/struct-shapes.rb`, six alternating rounds, CRuby / master / new, ns: `kw_new` 299 /
  1017 / 198, `kwnil_new` 305 / 1019 / 189, `kw_hash` 196 / 1051 / 147, `sub_kw` 356 / 1149 / 379,
  `pos_new` 120 / 180 / 115, `new_class_use` (Struct.new plus a first new and getter) 5,678 / 79,510
  / 50,976; `kwinit`, `getter`, `setter`, `obj_new`, `plain` and `hash_dup` overlap (the machine was
  loaded: `getter` read 39 ns in one run and 79-93 in another; `hash_dup`, `setter` and `obj_new`
  each looked slower once and overlapped run alone, 8-10 rounds). `new_class` is GC-bound and its
  least-of-seven swings (spreads 44-143%); in one process, rounds of 1,000 `Struct.new(:a, :b)`,
  three alternating runs: 26-27 us a class on master, 20-21 here, CRuby 4.1-4.4.
  `Util/struct-matrix.rb` (new, 110 rows, each cold, again and warm) prints CRuby's output but for
  `UsingRefine.make`, a refined `initialize` that Class#new sees on every IronRuby build (master
  too; fixed in perf/crubydiff, see Landed); master differs in eight more rows. Matrices through gate-matrices.rb, as built, `-X:NoJIT
  -X:NoOSR`, `-X:NoJITCalls`, `IR_LIGHT_FRAMES=0`, `IR_PARKED_SCOPES=0`, R2R and `IR_FAST_STRUCT=0`:
  struct, hash-struct, hash-order, keyword-light, jit, jit-calls `same` or `baseline -16`,
  hot-shapes `baseline`: PASS. spec/core/struct: 170 examples, 1 error (the known deconstruct_keys
  one) on master, on this and with the switch off. IronRuby.Tests, the 37 named Struct, Keyword and
  Construct tests and ClrRubyImpliedInterfaces3: PASSED. Recursion through a struct's initialize
  and keyword initialize: SystemStackError with and without the JIT. Census: 0 methods changed (it
  sees none of this). For the gate: the unseen-class path (a hook of `inherited` defined in another
  thread between the two checks, ObjectSpace finding the class before its members exist), the
  construction's initialize site made without the caller's scope (as the rule's library-initialize
  call, which keeps the caller's flags, may not be), and that `keyword_init?` reads the hidden
  variable through superclasses for any Struct subclass. Left: writers keep per-class rules; a site
  past the dispatchers' budget builds a `new` rule per class (about 250 classes into a loop);
  `Struct.new` itself is 5 times CRuby; `kwinit` (`def initialize(sku:, name:)`) 1.8x and
  `Hash#dup` are untouched.

- **perf/dmethjit (2026-11-26) - the method JIT takes the body of a method made by `define_method`
  (gap 23)** - gated 2026-11-26 (round thirty-seven's gate entry). `JitBlockMethod` (Runtime/Jit) takes a block as the `def` it would be
  when nothing in it tells the two apart: BlockDefinition keeps its tree on the dispatcher
  (`BlockDispatcher.JitAst`) when the block reads and writes none of the enclosing scope's locals,
  has a def's parameters (plain mandatory, then optional; no rest, post, block or keyword one) and
  was not compiled for eval or tracing; at the first call a rule is built for, its own screen refuses
  what means the defining scope's thing in a block (yield, `block_given?`, `super`, class variables,
  `__method__`, `binding`, `proc`/`lambda`, nested blocks and block-pass, regular expressions and
  globals); then a synthetic `MethodDefinition` over the block's statements goes to `JitStub.Wrap` as a
  def's would, with the Proc's `LocalScope` for its constants and the refinements active there. The
  stub's generic body is the call the rule made (`Proc.BuildCall`) written in C#; the rule of
  `RubyLambdaMethodInfo` calls the stub for plain arguments (no block, splat or assignment value),
  passing omitted optionals as the default sentinel and unmarking a trailing keyword hash.
  `JitStub.SelfCallResolvesHere` takes a self-call of the same define_method method (recursion).
  Switch: `IR_JIT_DMETH=0`.
  - The census counts only `def`s and moves by nothing (28,376 methods, 0 changed). Judged instead
    by a traced run and a timed one. `Util/bench/dmeth-shapes.rb` under IR_JIT_VERBOSE=1: every
    `bm*` shape specialized but `bmcapt` (a captured local: not taken). Traces of
    `Util/bench/app-shapes.rb` and AWFY Richards, DeltaBlue, Json, CD: the same specialized (113, 78)
    and declined-after-the-pre-screen (29, 19) counts as the baseline - none of their methods is an
    eligible define_method body (delegate's and forwardable's take `*args, &block`).
  - Timing (`ab.rb -r 6`, ns a call; CRuby / master / switch off / new, new over off): `bm0` 22.1 /
    50.0 / 51.2 / 27.3 (0.53), `bm1` 24.2 / 51.4 / 51.3 / 26.5 (0.52), `bm2` 30.2 / 60.0 / 60.3 /
    37.5 (0.62), `bmivar` 21.6 / 48.1 / 47.3 / 30.6 (0.65), `bmcall` 41.4 / 69.9 / 74.2 / 39.1
    (0.53); the `def` controls and `bmcapt` level (0.90-1.01, overlap). An omitted optional argument
    (`{ |x, y = 2| }` called with one) 93.9 -> 38.5. A body wrapped and then declined (a `return`, a
    `nil.to_i`) costs what it did (0.99, 1.04 overlap).
  - Slower: a body whose typed version keeps deopting (`{ |x| x + 1 }` called with an Integer and a
    Float in turn) 98.2 -> 107.7 (1.10, alone, -r 8) - the stub's retire-and-retry cycle, which a
    `def` of the same body pays as well (1.12 against -X:NoJIT).
  - Matrices, CRuby against master against new: `Util/dmeth-jit-matrix.rb` (new: 31 rows - every
    shape the JIT takes, captured locals read and written, the defining method's block, implicit and
    explicit super, `__method__` under an alias, binding, instance_exec of the same Proc, define_method
    with a Method/UnboundMethod/lambda, redefinition and remove_method while hot, a subclass override, a
    module in two classes with different ivar types, refinements, Class.new constants, a kept keyword
    hash), `Util/dmeth-matrix.rb`, `jit-matrix`, `jit-calls-matrix`, `block-given-matrix` with the JIT
    on, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls` and the switch off: all identical to CRuby.
    `Util/perf/inline-matrix.rb` (one case per process, the same modes): new identical to master;
    both differ from CRuby in `tree_int_key` ("Object must be of type Int32." where CRuby says
    "undefined method 'compare_to' for an instance of Integer"), as on master. IronRuby.Tests: the 69
    registered tests that use define_method and the 20 named for the JIT pass.
  - Found on master, not fixed: two define_method methods made from the same Proc are `==` as
    UnboundMethods in CRuby and not in IronRuby (`W.instance_method(:n1) == W.instance_method(:n2)`).
  - For the gate: the rule now calls a JIT stub for every eligible define_method method, so a
    wrong typed body shows in any code that defines methods this way; the block's tree is kept on
    its dispatcher for the life of the Proc when the JIT is on (one reference per eligible literal,
    as `LoopInfo` already does); the generic body re-implements Proc.BuildCall's rule in C#
    (`JitBlockMethod.Call0..CallN`): a difference in break/return/LambdaUnwinder handling would show
    there first.

- **perf/first-bind (2026-11-26) - what a first bind costs, split; light-scope sites call library
  methods through a dispatcher for their first calls (gap 26).** The split (master 4bae83f3, R2R
  host with the startup library; `IR_BIND_TRACE`, `IR_PHASE_TIMES`, perf with jitdump classing
  each bind sample by the path it took, and a throwaway instrumented DLR that timed the rule's
  build and compile, counted each interpreted rule's hits and timed `CallInstruction.Create`).
  Binds, `-e 1` / `gem list` / irb: 1,060 / 3,164 / 3,116, 111 / 216 / 221 ms inclusive. A
  precompiled dispatcher serves 927 / 2,371 / 2,270 of them at 59 / 35 / 39 us; a rule is built
  for 133 / 514 / 564 at 430 / 251 / 231 us - 12-18% of the binds, 51-60% of the time. Under
  bind, perf ms: the rule's LightCompile 23 / 58 / 61 (`CallInstruction.SlowCreate`'s
  MakeGenericType, Activator and CreateDelegate for each new method signature 9 / 14 / 16 of
  it), making the rule's lambda and delegate 8 / 13 / 13 (a FullOpts stub JIT'd per new
  delegate type), overload resolution 9 / 21 / 25, the expression tree 6 / 16 / 19, lookup 6 /
  12 / 9; on the dispatcher path the library dispatcher and its call plan 24 / 34 / 39, the
  callee def's own first compile 17 / 24 / 38 (not binding), lookup 12 / 17 / 21. In `-e 1`
  ~45% of it is the runtime running code for the first time (prestub, R2R fixups, type loads);
  the first rule of a process costs ~10 ms. Rule build against compile: 21 / 32, 47 / 72, 46 /
  71 ms. How often a rule runs before the IL replaces it: once 25% / 10% / 13% (23 / 27 / 28
  ms), 3-31 times most of the rest; 189 / 221 of the tools' rules are hot ones (bound after a
  dispatcher spent its budget, threshold 2: 30 / 35 ms) whose LightCompile serves the handful of
  calls until their background compile lands. Finding 3 of #295 at start-up: typed rules whose
  binder also bound an untyped one, 1.3 / 6.4 / 16.3 ms. Rule binds by site: light-scope sites
  66 / 142 / 160 (15 / 27 / 28 ms) - a light site took no library dispatcher, since it has only
  the declaring scope to pass, nor any dispatcher for a call on a module's singleton class.
  Taken: the light site takes the library dispatcher unless the overload takes the scope
  (`LibraryCallPlan.ReadsScope`: block_given?, binding, local_variables, const_get keep the rule
  that hands the call back), and a module's singleton class (File.join, Gem.dir) no longer
  keeps a light site off the dispatchers. Rule binds 133 -> 90, 514 -> 436, 564 -> 483; bind
  inclusive (phase timer, median of 3) `-e 1` 111.7 -> 106.3, `gem list` 214.5 -> 206.4 ms.
  Instructions (all threads, least of 3): 1,336 -> 1,321, 1,508 -> 1,486, 3,213 -> 3,176, 3,705
  -> 3,674 M. Wall, `bench.rb 10`, load 0.9-1.5, median ms: `-e 1` 454 -> 449, `gem --version`
  521 -> 530 (best 505 -> 502, CPU 505 -> 484), `gem list` 965 -> 950, irb exit 1079 -> 1052,
  irb 1+1 1103 -> 1087. AWFY geomean new/base 0.985 and 1.016 (both orders, none slower in both;
  Storage -13% and +0.5%); micro-table: symcase 6%/9% slower in the two orders, the same alone
  (six alternating processes: medians 41.4 / 41.2 ns); `s_each*`, `s_times*`, `veach*` 5-19%
  faster in both (their light sites' hot rules are now compiled at threshold 2). Checked:
  hot-shapes-matrix gains a section (library calls at light sites and module singleton calls,
  hot, with the method redefined, made private on a subclass and on the singleton class, a
  singleton method and a prepended module on the module, a refinement, method_missing, and
  block_given?/binding/local_variables) - CRuby's output on both trees in every mode;
  gate-matrices --all no DIFF but thread-backtrace-matrix's main-thread row, which prints line
  87 or 88 by timing on both trees (88 on both, six runs each, alone); IronRuby.Tests PASSED on
  both; the sweep's six new failures (Set compare_by_identity) fail on master too. Next levers,
  by size: the hot rules' LightCompile (keep the spent dispatcher answering until the
  background-compiled rule lands, 18-21 ms of the tools); `CallInstruction` without a generic
  type per signature (9-16 ms); the typed/untyped duplicate (6-16 ms, and warm-up).

- **perf/big-method-first-call (2026-11-26) - a lambda literal is light-compiled at its first
  call, not with the method it is written in.** irb's first syntax check cost ~300 ms, nearly
  all the first `Prism.parse` (205 ms; the second 1 ms). Not `Serialize::Loader#load_nodes`'
  1,800-line `case`: only CRuby runs that one. IronRuby (`RUBY_ENGINE != "ruby"`) runs
  `define_load_node_lambdas`, an array of 152 lambda literals made by every Loader, and calls
  the few the source needs. Its first call took 160 ms on the ReadyToRun host (`IR_PHASE_TIMES`:
  53 ms transform, 114 ms LightCompile; the second call 0.03 ms), because the light compiler
  compiles every lambda nested in what it compiles. Linear, not superlinear: a synthetic method
  with N `when` branches costs ~70 us a branch to build at its first call (~140 with light frames,
  whose rewrite doubles each call site: `IR_LIGHT_FRAMES=0` halves it), at N = 100 to 1600 alike.
  DLR patch 0110 lets the language name the nested lambdas compiled at the first run of a
  delegate made from one (`LightCompiler.CompileOnFirstCall`): a walk of the lambda's tree finds
  the variables it closes over, which the enclosing lambda boxes and hands over as before, and an
  IL compile of the enclosing lambda light-compiles the pending ones for their try depth (0109).
  IronRuby names lambda literals only (`IR_LAZY_LAMBDAS=0` off, `=all` every nested lambda, for
  testing the walk): 683 of the 700 nested lambdas `require "irb"` compiles are called, and
  deferring them all cost the walk (36 ms) and made start-up ~4% slower. `define_load_node_lambdas`
  160 -> 105 ms. Instructions (least of three, R2R host): `echo exit | iirb.sh` 6.48G -> 6.08G,
  `require "prism"` and one parse 3.84G -> 3.56G, `-e 1` / `require "irb"` / `gem list` the same
  to 0.1%. Wall, best of ten, R2R host, load ~2: `-e 1` 472 -> 467 ms, `gem --version` 632 -> 635,
  `gem list` 1367 -> 1398, `require "irb"` 1193 -> 1212, `echo 1+1 | irb` 1707 -> 1668,
  `echo exit | iirb.sh` 1585 -> 1521, require prism + parse 1014 -> 946. AWFY geomean 1.016 and
  0.994 (new/base, both orders); Bounce looked 20% slower in both and is bimodal on both trees
  (six alternating runs of each: base 62-143k, new 64-150k; 60 iterations in a process of their
  own: 49.8G instructions on both). With the startup library of #291 the Loader is precompiled
  (0.7 ms first call) and this changes nothing there; it is the Release build's, the library-less
  host's and any program's that keeps a table of lambdas. Left (next start-up targets): the
  block bodies' own transform (two rewrites over each body, LightFrameRewriter and
  RegisterLocalsRewriter, are 57% of a block's transform), and an enclosing light frame's
  rewriters walking every nested block body again.

- **perf/compile-cold-stretch (2026-11-26) - the launchers give the youngest generation 128 MB, so
  start-up runs without collections (gap 26).** Asked: why a call costs 50-150 us for hundreds of
  calls after it crosses the compile threshold (PR #294's probe, steady state 1.7 us). Measured,
  per call, the edge probe (`m(a) + k(h, i)`, one process): (1) the 50-150 us was the Release
  build, whose IronRuby and DLR code runs as Tier-0 JIT output: interpreted calls cost 56 us there,
  22 us with `DOTNET_TieredCompilation=0`, and 8-18 us on the R2R host, which is what ships and
  what `ir.sh` runs. Nothing in the threshold logic itself costs per call: past it each call reads
  `_compiled`/`HasCompiled` and goes on interpreting, with no lock or wait. (2) On the R2R host at
  threshold 200 the calls in the window cost ~25 us against ~12-18 before it: the background
  compiles (a profile: 1.6 cores of pool threads for 40 ms, 55% of it RyuJIT optimizing the
  DynamicMethods of lambdas and rules) share the physical cores with the main thread
  (`taskset -c 0-3`, one hyperthread per core: 16-18 us), with run-to-run noise from tiered PGO.
  (3) A compiled lambda's call sites start cold: the interpreter's sites are untyped
  (`Func<CallSite, object, ...>`) and the compiled code's are typed (`RubyScope`, `int`, `Proc`,
  `MutableString` arguments), so the binder's rule cache, keyed by delegate type, never hands the
  compiled code the rules the interpreter warmed up: every site binds again (0.1-0.7 ms each),
  runs the new rule interpreted for another threshold of calls and then compiles a duplicate (a
  trace of the probe at 200: `k`'s five sites rebound at call ~300 and their rules compiled at
  ~520). In `gem list` it is small (4 lambdas compile; 111 of 514 binds are typed). Not fixed;
  the next lever for warm-up. (4) The biggest single part of #294's probe cost on the R2R host
  was the collector: the process's first gen2 collection (~18-20 ms) moved into the timed window
  (with no collection, N=2000 cost +8 ms instead of +20). That led here: the CLR sizes gen0 for
  short-lived garbage (~6 MB on this box), while what IronRuby allocates as it starts lives as
  long as the process, so each collection copies it again. `gem list` (93 MB allocated): 16 gen0,
  5 gen1, 2 gen2, 120 ms paused; irb 27 and 144 ms; `-e 1` 5 and 32 ms. `ironruby` and
  `ironruby.cmd` now set `DOTNET_GCgen0size=0x8000000` unless it is set (the runtimeconfig has
  no knob for it: `System.GC.Gen0Size` is not read on .NET 10.0.15): `gem list` and `-e 1` run
  with no collection, irb with one (a gen2 the large-object heap's budget starts;
  `System.GC.LOHThreshold` = 4 MB in the runtimeconfig removes it too - not taken, left for a
  measurement of its own). Start-up, R2R host, `bench.rb 10`, load 1.2-1.6, median ms base ->
  new: `-e 1` 481 -> 459, `gem --version` 572 -> 539, `gem list` 1046 -> 958, irb exit 1131 ->
  1085, irb 1+1 1140 -> 1093. Peak RSS `-e 1` 109 -> 116 MB, `gem list` 174 -> 228. AWFY 12,
  both orders, load 0.6-1.3: geomean new/base 0.926 and 0.940; Havlak -25%, Storage -9/-19%,
  DeltaBlue -6/-10%, Json -9/-10%, none slower in both orders. micro-table both orders: no line
  slower in both; the four slower in one order (locattr, sqrtw, blkattr, s_times4) timed alone,
  6 alternating rounds: 0.96, 1.00, 0.97, 0.97. Edge probe, R2R, medians of 6, ms, base / new /
  #294 alone (from an earlier run of its own) / #294 on this: N=100 23.7 / 23.5 / 16.3 / 16.9; 300 24.0 / 24.6 / 23.4 / 24.0;
  600 25.1 / 27.1 / 30.5 / 34.2; 2000 33.1 / 33.9 / 55.9 / 39.9; 20000 114.2 / 100.9 / 117.5 /
  108.8. So #294 on top of this still fails the 5% rule at N=600 and 2000 (parts 2 and 3 above).

- **perf/splat-recursion (2026-11-24) - a splat the call site makes binds once for every length,
  and is the callee's rest parameter.** `def f(n, *a) = n == 0 ? a : f(n - 1, *a, n)`,
  `200.times { f(300) }`: 12.0 s against CRuby's 0.037. Cause, from `IR_BIND_TRACE=1`: the rule
  of a splatted call tested the list's exact length (`MetaObjectBuilder.AddSplattedArgumentTest`),
  and a light site splatting gets no precompiled dispatcher, so every level of the recursion
  bound and compiled a rule of its own (about 150 us each, a `?` and a `rule` line per call,
  warm or not). With that gone (one rule, `Count >= k`, when the list's tail all goes into the
  rest parameter: no parameter after the rest, no keywords, no right-hand side, the rest given
  at least one item) the shape was still 3-14x CRuby and worse than quadratic (n = 600 was 7x
  n = 300): a profile put 74% of the thread in libcoreclr, which the symbols (`dotnet-symbol`)
  named as the collector's relocation (`relocate_in_uoh_objects`, `relocate_advance_to_non_sip`),
  and `DOTNET_GCgen0size=0x20000000` cut it 2.6x. Every level allocated four copies of its
  arguments (`[*a, n]` copied `a` into an Array of its length and again into one of twice it to
  add `n`; the rule copied that into the rest parameter), all alive until the recursion
  unwinds: 1.8 collections per `f(600)`, mostly gen1. Now `[*a, x...]` is made the size it ends
  up (`RubyOps.SplatCopy`; `[*a, a.pop]` still copies before the item is evaluated), and a site
  whose splat is such an array (`f(*a, x)`, `f(*a, *b)`; not `f(*a)`) says so in its signature
  (`RubyCallFlags.HasFreshSplat`, `*+` in traces), so that the rule gives the array itself to a
  rest parameter that takes all of it (`RubyOps.TakeFreshSplat`): one copy a level. A block's
  rest parameter from a splat (`yield(*a, x)` into `|*r|`) is made its size too, not grown an
  item at a time. `IR_SPLAT_REST=0` turns off the rule for every length and the taking.
  Bytes: `f(600)` 5.9 MB -> 1.48 MB, 0.24 collections. Quiet machine (load 4-7), `ab.rb`, ns per
  call, cruby / base / off / new: `f(50)` 7,499 / 2,074,855 / 2,363,065 / 10,000;
  `f(300)` 103,425 / 24,707,551 / 24,351,088 / 119,997; `f(600)` 273,084 / 66,622,762 /
  59,587,666 / 594,583. The script above: 0.072 s (cold) against 12.0. Three rounds, load 3-16:
  `g(*a, x)` with 300 items 480 / 2,079 / 1,360 / 628 (new/off 0.46), `yield(*a, 1)` into
  `|*r|` with 300 items 480 / 3,280 / 880 / 880 (the presizing, in `off` too), with five
  items and `f(n - 1, *a)` (one copy a level, as before) overlapping. Left: `f(n - 1, *a)` 300
  deep is 1.75x CRuby and `f(600)` 2.2x, both the collector moving the arrays the recursion
  keeps alive; a block's rest parameter still copies a made splat (`yield` sites carry no
  signature to say it). Checked: Util/splat-pass-matrix.rb gains section 10 (every length
  with leading, optional and post parameters, keywords and a hash after the splat,
  ruby2_keywords, a kept or mutated rest parameter not shared, redefinition half way,
  method_missing, send, super, yield, array literals, private/protected/`private :x` in a
  subclass half way, a refinement, a made splat a million deep: SystemStackError), all CRuby's;
  gate-matrices (splat-pass, block-pass, hot-shapes, jit, light-rest) in seven modes with
  `off=IR_SPLAT_REST=0`: PASS (hot-shapes "baseline" in every mode, as master);
  IronRuby.Tests (parallel-tests.sh, Release): PASSED.
  **Gate of 2026-11-25** (the branch with master e5c6a859 merged, against e5c6a859, Release
  net10.0): **PASS**. IronRuby.Tests 1,340 PASSED on both trees, no failed or flaky name on
  either. `gate-matrices.rb --all` (153 units, seven modes with `off=IR_SPLAT_REST=0`, R2R
  included): PASS once `array2-matrix`'s `[1,2,3].sample(2)` row stopped printing which items
  it chose (random on every engine, CRuby included; three DIFFs it caused on the first run).
  `ReflectionCache.Generated.cs` regenerated with ClassInitGenerator: identical to the commit's.
  ruby/spec, all five suites (32,680 examples): no failure the known list does not name.
  Timing, load under 2: the fourteen benchmarks verify; AWFY 12 both orders, geomean 1.000 and
  1.004 (new/base); Havlak 1.013 and 1.059, and in twelve alternating rounds of its own the new
  tree was the faster in eleven (medians 4.71 against 4.78 s). micro-table both orders: geomean
  0.972 and 0.998, no line more than 5% slower in both. Start-up on the ReadyToRun host, ten
  alternating runs, best ms (base -> new): `-e 1` 517 -> 503, `gem --version` 693 -> 696,
  `gem list` 1569 -> 1572.

- **perf/lrebind (2026-11-24, 0d464ce0) - a shared rule's compiled target goes to its binder's
  cache.** The first-call profile counted about 160 extra binds in 200 light methods, all
  `shared-hit +(L,1) :: Int32`. The cause was not Integer's version (a top-level `def` does not
  bump it). When the shared rule's interpreted copy is compiled, the interpreted rule stays in the
  binder's L2 cache and fails its test for good (`CachedBindingInfo.CheckCompiled`), and
  `BindShared` stored the compiled rule in `shared.Rule` without `CacheTarget`, so every light
  site that had not run the call yet failed in L2 and bound. It now caches the compiled rule too.
  BindDelegate calls for 200 light methods 1438 -> 1272 (scope frames: 1273); `+(L,1)` Int32 binds
  163 -> 1; the new `Util/shared-rule-light-matrix.rb` 409 -> 26 binds of `zz(L,0)`, part of it
  because a stale rule left `LastInterpretedFailure` set and later binds got a wrong compiled rule
  to rebuild. Instructions saved are small: 12K per light method (m200 light minus scope 467K ->
  455K, ReadyToRun host, same tree with and without the patch), `-e 1` unchanged. The rest of the
  light first-call extra (about 460K per method, linear in the method count) is runtime and JIT
  work on the light-frame compile path, not binding: perf by DSO puts it in libcoreclr, libclrjit,
  CoreLib, Microsoft.Dynamic and System.Linq.Expressions.
  Found in passing, not fixed: CRuby calls a refinement of Integer#+ before a module prepended to
  Integer; IronRuby calls the prepended module's method (light frames on and off). (Fixed since by
  f7679a1, "Search a refinement ahead of the modules prepended to what it refines": perf/crubydiff
  found it matching CRuby in every mode, and `Util/shared-rule-light-matrix.rb` covers it.)
  Gate, the branch at 5b464e8c against b76d3a6f, Release net10.0: **PASS**. IronRuby.Tests
  PASSED on both (1,334). 36 matrices in five modes: 180 same, 0 differ. ruby/spec, all five
  suites: no failure the known list does not name. `ab.rb -i` on the rescue, keyword, loop and
  regex shapes: every row 0.95-1.04. Timing not run (load average up to 76 during the gate).

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

- **perf/passensure (2026-11-27) - running a pending ensure clause from the filter of a rescue frame
  above it: designed, found unsound, not built** (stage 1 of the brief; `Docs/rescue-above-ensure.md`).
  The idea (perf/rescuefilter): a rescue above a passed ensure catches and throws again (~20 us for the
  first such frame) only because Ruby evaluates its class list after the ensure runs; run the ensure
  from the rescue's first-pass filter, then decide there, and have the second pass skip the clause.
  Not sound, for the CLR's model rather than the work: (1) the CLR finally blocks between the raise and
  the ensure - a Mutex's unlock and File.open's close in C# library methods, the DLR's `$!` restores,
  every Ruby frame's bookkeeping - run in the second pass, and Ruby wants them before an outer ensure:
  `Util/raise-matrix.rb` "pe: a Mutex unlocked before the outer ensure" prints `[[:locked, false]]` on
  CRuby and today, the early clause would see `true`; (2) a compiled or light frame's clause reads its
  frame's CLR locals and cannot be called from an outer filter without compiling every ensure clause
  as a closure (a cost on the path that does not raise); (3) a clause that raises, jumps (`return`,
  `break`, `next`, `throw`, `retry` above) or is interrupted (Thread#raise, Thread#kill) cannot do so
  from inside a filter, which can only return, and whose exceptions the CLR swallows.
  `Util/raise-matrix.rb` has seventeen new rows, "a rescue above a passed ensure" (the ensure before a
  class list that is a method call, a constant reassigned / `===` defined / the exception extended in
  the ensure, `$!`, cause, each jump, retry, Mutex and File order, the backtrace, Thread#raise and
  Thread#kill during the ensure): CRuby's output on the unchanged build in every mode. What is left
  for the cost: a cheaper catch (a lazier capture, `ExceptionDispatchInfo` for the throw again),
  which needs a sampler.

- **perf/rescuefilter (2026-11-27) - deciding a constant rescue class list in the first-pass filter:
  measured, not built - it is decided there already, except above a passed ensure, where it cannot
  soundly be.** perf/timeout named "`rescue ExitException => e`: 29 us per frame against 8.6 for a core
  class". Wrong attribution: alone, a rescue of a user class named by a constant - top-level,
  qualified, lexical in a module, with or without `=> e`, of an Exception subclass followed by `raise`
  - costs what a core class does (8-16 us per raise against a bare 8,
  `Util/bench/rescue-above-ensure-shapes.rb`, new), because `RubyOps.RescueSiteCannotMatch` reads the
  clause's constant site cache in the filter, for any constant. What turns the filter off is not the
  class but an ensure the exception has already gone through: `RescuePassMayTest` refuses while
  `PendingPassedEnsures` is not zero, for every class list, core classes included - Ruby evaluates the
  class list after those ensures have run, and the CLR's first pass runs before them. Then the frame
  catches, completes the backtrace the ensure began (`TakePassedEnsureCapture`,
  `CaptureExceptionTrace`), finds no match and throws again: the first such frame above an ensure
  costs about 20 us (33 against 14 without the ensure), each further one about 3 (three: 43);
  CRuby 0.8 and 1.2. Timeout's raise has exactly this shape (`handle_timeout`'s rescue above the
  Proc's `ensure request.finished`): with perf/timeout's override, leaving out any one of the
  rescue, the ensure or the Proc takes it from 63-76 us to 25-31.
  Not sound to decide in the filter: the pending ensure runs arbitrary code first (`request.finished`
  calls a method), which can reassign the constant (the site's version would move, but only after
  the filter answered), define `===` on the class, or `extend` the exception with a module of the
  class list. What would take it: run the pending ensure clauses from the filter itself, on top of
  the raise, before the class list is looked at (the order Ruby wants), and have the second pass skip
  them - the pieces are there for ensure-only bodies (`PassEnsure`, `EnterPassedEnsure`, the stack
  room `PassEnsureStackRoom` keeps for a clause run on top of the raise), not for a clause that a
  rescue frame above has to wait for. A larger change than a step; its matrix would need every row of
  raise-matrix about ensure order, `$!`, cause, retry and jumps out of an ensure.

- **perf/ensure (2026-11-26) - the catch-and-rethrow of an `ensure` clause that jumps (#213 item 3):
  measured, not built - real code almost never has such a frame on a raise's path.** Compiled, an
  `ensure` already lets an exception go through a CLR filter (`EnsureGuardExpression`,
  `RubyOps.PassEnsure`); a clause with a jump in it - a call with a block, `yield`, zsuper,
  `return`/`break`/`next`/`retry` (`Body.EnsureJumpFinder`, `GotoFinder`) - is made a catch of
  everything and a throw again by the DLR's FlowControlRewriter, and its guard gets frame depth -1,
  so an exception whose backtrace is still to be taken is caught there.
  Cost (`Util/bench/ensure-jump-shapes.rb`, new, us per raise rescued, IronRuby / CRuby): one frame
  plain 12.5 / 0.79, calling a block 55 / 0.99, yielding 61 / 0.87; twenty frames plain 28 / 2.3,
  block 119 / 4.2, yield 112 / 3.1. So the first such frame costs about 43 us - the backtrace taken
  eagerly where the plain ensure defers it - and each further one about 4.5 us, the rethrow.
  How often: the prelude's 24 `ensure` clauses have no jump; of 276 in `Src/StdLib/ironruby` and
  `ruby/4.0`, 25 do, all in cold code - socket helpers (4), bundler's DSL, CLI and settings (6),
  open3 (2), irb (2), resolv, mkmf, rdoc, rubygems, puma (2), pty, msgpack, bigdecimal. A raise
  through the frames real code puts around it (`Util/bench/raise-through-shapes.rb`, new, us,
  IronRuby / CRuby): bare 8.1 / 0.59, Monitor#synchronize 11.7 / 0.67, Mutex#synchronize 11.8 /
  0.68, File.open with a block 16.3 / 2.1, Array#each 16.0 / 0.69, Integer#times 15.9, Hash#each
  16.2, loop 17.3, tap 14.8 - none carries the ~43 us of a jump-ensure. The rethrow is real but off
  the paths that raise; a DLR patch for it (Build/dlr-patches) was not written.
  Found instead: **`Timeout.timeout` with a raise in its block costs 152-164 us against CRuby's 3.1**
  (9.2 / 2.0 without the raise). Its own ensure (`request.finished`) has no jump; the path has
  `return yield(sec)` from a Proc inside a begin/ensure, and `Error.handle_timeout`'s `rescue
  ExitException => e` with `raise` again (a class list by constant, so a catch and a backtrace, then a
  re-raise). Pieces of it measured alone: a `rescue E2 => e` frame 12.3 us, a Proc with `return
  yield` and an ensure 15-31, the two together 44: they do not add up to 155, so the rest is still to
  be found. That is the next step here, not the ensure rethrow.

- **perf/jitbind (2026-11-26) - binding a call on another receiver (a parameter, an ivar, a local
  whose class the profile knows) to the callee's typed body, with a generic call as the fallback:
  counted, not built - nothing it would take stops there.** The three methods gap 1 names: DeltaBlue's
  and Havlak's `Dictionary#at` is specialized already on 9040b98 (`IdEntry#match`, the call on the
  loop's `e`, `hash` and `get_bucket` are inlined into it: perf/havlak's inlining on an unsampled
  local; the gap's "declines at `match`" was out of date). Richards' `queue_packet` is refused at
  `t.add_input_and_check_priority`, but that callee has no typed body to bind to: it is declined
  itself at `append` (the effect rule). CD's `RedBlackTree#get` waits for `find_node`, which is
  declined at `current.key`, an attribute read on the local its `while` loop reassigns (the
  `key.compare_to` on the parameter is inlined there already). Then every "call:" rejection in
  app-shapes and the fourteen AWFY traces (3 x 10), with whether its callee had a typed body in the
  same run: 24 in app-shapes and 23 in AWFY, of which the callee has none in all but a handful;
  those are calls on self (`is_digit`, `is_white_space`, `set_is_root`, `is_shorter_than`,
  `internal_init`: `-X:JITCalls`' already), the "no typed body yet" rejections of methods
  specialized later in the run (`at`, `add`, `hash_slot_for`), and Towers' `pop_disk_from` at
  `top.next`, an attribute reader on a local read from an Array (the callee "specialized" was
  `Random#next`, a name match). One call on another receiver to a typed callee is left: Bounce's
  `Ball#initialize`, `random.next` on a parameter, a constructor run once per ball, whose callee
  writes `@seed` - so each bound call is an effect before the next one that could fail its class
  check, and it needs the generic-call fallback after an effect this gap describes, for one cold
  method. Not built.
  What the walls are now, then: callees without typed bodies (the effect rule: `append`,
  `read_string_internal`, `move_top_disk`/`push_disk`, `is_input`, `value` at `new`), and
  attribute reads and writes on locals that hold an element or a loop's value (`current.key`,
  `top.next`, `x.right` in `successor`). The latter is gap 3's, and the next step for CD and
  Towers.

- **perf/jitplain (2026-11-26) - the per-receiver test of perf/jitaref for the typed body's other
  core operations: nothing to take.** The other operations (`Array#[]=`, `fetch`, `size`, `first`,
  `last`, `push`/`<<`, `==`, `Hash#[]`/`[]=`/`fetch`/`key?`, `String#size`/`==`/`[]`, the
  Integer and Float operators) still use the class-wide `HasPlainInstances`, so the question was
  which of them a real run turns off. `Util/perf/plain-probe.rb` (new) prints, at exit, every core
  class whose `ClrSingletonMethods` or `HasAdoptedInstances` is set. In app-shapes, `gem list`, a
  run requiring csv, json, set, optparse, uri, net/http, erb and logger, and all fourteen AWFY
  benchmarks: only Array is ever touched, with the names `[]` (CSV's `NO_QUOTED_FIELDS`, which
  perf/jitaref took), `getopts`, `initialize`, `options`, `options=`, `order!`, `parse!`,
  `permute!` (optparse extends `ARGV` with `OptionParser::Arguable`) and `resolve_feature_path`
  (`$LOAD_PATH`'s, in every process); no `method_missing`, no adopted instance, and Hash, String,
  Symbol, Range, Proc, Time, Regexp, MatchData, nil, true, false, Integer and Float untouched. None
  of those names is an operation the typed body takes, so no class-wide test fails for one in these
  runs and a per-receiver test would change nothing they do. Not built. What would make it worth
  building: a library that gives one String or Hash a singleton method of an operator's name, or a
  `method_missing` (DelegateClass and SimpleDelegator do not: they are Ruby objects); the probe
  finds it.

- **perf/jitconst (2026-11-26) - "JIT maybe: constant without a scope" is the census's, not the
  runtime's; nothing to let in.** The census's second "maybe" line (455 methods of Src/StdLib +
  AWFY on 9d2c89a: ENV 19, Array 12, Integer 11, URI 11, AF_INET 8, BN 7) comes from
  `ResolveConstant` bailing when `_constantScope` is null, and the census calls
  `JitCompiler.TryCompile` with no scope at all (it runs no program, so there is no declaring
  scope to look a constant up in). A real compile gets the stub's declaring scope and folds the
  constant, revalidated at entry (perf/constrev). Traces: app-shapes calls 10 of these methods and
  specializes 4 (`ERB#make_compiler`, `ERB::Compiler::Scanner#initialize`,
  `Gem::Specification#platform`, `Shop::Product#tax_rate`); two are declined for something else
  (`OptionParser.top` and `Gem::Requirement#none?`, both at "[] on a RubyArray": `@stack[-1]`,
  `@requirements[0] == ...`), four never turn hot. Fourteen AWFY benchmarks (2 x 5): 118
  specialized, and no rejection or decline anywhere names a constant, autoload or a deprecated
  constant (`JitFoldedConstant.Resolve`'s failures).
  What stands behind the constant, found with a census-only probe (not kept: an unresolved
  constant emitted as an opaque object, and a body that then compiled reported as "constant
  only"): of the 455, 221 still stop at the constant, using it where the value is needed (a class
  for `new`, `when`, `::X`, a receiver such as `ENV[k]`), 183 have nothing else behind it - they
  compile once a run resolves the constant to a value the body can type - and about 50 stop next at
  a call or another profile question. So the line is a census blind spot, not a refusal: a step
  that made the census resolve constants would change its verdicts, not what runs. The runtime
  questions of gap 29 (a later constant shadowing a folded one, autoload in flight,
  `const_missing`, private constants) were not touched.
  Also found: two of app-shapes' methods are declined at "[] on a RubyArray" for an element read
  with a negative index or of an Array instance variable compared with `==` - a typed-body
  question for another step.
- **perf/kwprologue (/perf-step, 2026-11-26, on 78aab00) - a keyword method's prologue checks
  the call in one runtime call** (gap 38's second cost; switch `IR_KW_PROLOGUE`, reverted).
  Measured first by switching the prologue's parts off one at a time in a throwaway build
  (`Util/perf/callgrind/count.sh`, `-X:NoJIT`, instructions a call): in `def p_opt(a, k: 1) =
  a` the separation checks cost 242, the statement guarded by `KeywordArgumentsMatch` 380 (467
  with the keyword given, 640 for a required one), the read 160-210 a keyword - 850 in all
  against a plain optional (`p_plain_opt` 1,904, `p_opt_omitted` 2,765). The helpers are a few
  instructions each (`HasKeywords` returns at once on the empty hash); an untaken `raise ...
  unless a` costs 108 and the next 76, so a statement costs what its test does to execute, not
  by being there; light frames do not change it (885 without them against 860). Tried: one
  `[Emitted]` helper, `KeywordPrologueMatch(last, kw, names, required, rest)`, as the condition
  of the `unless` that guards the prologue, with the separation checks moved into its body ahead
  of the missing/unknown ones - the same errors in the same order, and the shape `JitKeywords`
  recognizes (the JIT still specialized `kw`, `p_opt`, `p_req`, `p_two`; census 0 moved).
  Result: `p_opt_omitted` 2,764 -> 2,702, `p_opt` 3,680 -> 3,618 (-62, 2%) with the JIT off,
  nothing with it on; the controls 1.000. Rejected: 62 instructions do not pay for a new emitted
  helper and a second prologue shape. Learned: the cost is not the number of statements but each
  runtime call in the interpreted body (one call with five arguments cost about 560, where the
  three it replaced cost 622), and the DLR's call instructions are the generic ones up to ten
  arguments, so it is not a reflection fallback either - unexplained, see gap 38. Kept:
  `Util/kw-prologue-matrix.rb` (which error wins when several things are wrong, default order
  and side effects, ruby2_keywords, send, method_missing, super, define_method, Method#call, a
  module function; CRuby's output on the unchanged build in every mode, and on the candidate
  with the switch on and off), `Method_KeywordPrologue1`, and the shapes in `prelude-cost.rb`
  (`p_*`, `d_*`). The parent's other idea - a call site that binds literal keywords without the
  hash, guarded on the callee's signature - is untried; it would save the hash (about 670 at the
  site) and the reads for a callee the JIT declines.

- **perf/lexical3 (/perf-step, 2026-11-26, on 3cabf57) - bind the positionals of `def m(a, *r,
  k: 1)` in the calling convention instead of out of one splat** (the idea perf/lexical2's entry
  named), rejected by measurement before any code. The premise was `kr_rest` 5,325
  instructions against `kr_kw` 1,557. But `kr_kw` is specialized by the method JIT (4,017 with
  `-X:NoJIT`), and the splat lowering costs about nothing net: `def v_unused(*a, k: 1) = 3`
  (the whole lowering and prologue, nothing used) 4,798 against `def v_hash_into_rest(*a)`
  given the same keyword call 4,815, and against a rest method of the same shape called with
  one positional, 2,559. What a keyword call to such a method pays is the call into the rest
  (about 1,400 over a positional Hash) and, in every keyword method outside the JIT, the
  keyword prologue (about 1,300-1,500): gap 38, with the shapes in `prelude-cost.rb`. The
  prelude's methods of this shape (`String#each_line`, `#lines`, `Kernel#warn`, `IO.read`)
  have no positionals before the rest, so there was nothing to bind in the convention
  anyway. Learned: compare keyword shapes with the method JIT off (or check that it declined
  both), and with a control that passes the same Hash positionally.
- **perf/permute (2026-11-26, on 2e9521f) - `[BlockNotKept]` on Integer#upto and #downto, and both off
  `NeverParkedIterators`** (gap 4). Round eight's `[BlockNotKept]` alone changed nothing, since the
  Proc was made again on every call; parked scopes (later) reuse a method's literal-block Proc and
  scope where the iterator is `[BlockNotKept]`, as `times` and `each` are. With both: a `downto` call
  with a literal block allocates 72 B instead of 360, Permute 53.8 MB an iteration instead of 124.5
  (0.43, `alloc.rb`); time unchanged (`n.downto(0) { }` 161 against 164 ns, Permute 3.76-4.06 s
  against 3.81-4.05); instructions (`callgrind/count.sh`, `Util/bench/permute-shapes.rb`, new/base):
  `permute` 1.043, `dt_call` 1.023, controls `permute_while`, `tm_call`, `wh_call` 1.000. Parking
  costs more instructions than the allocation it saves. Reverted; the shapes and two block-loop-matrix
  cases (a `swap` on self redefined half way through a hot `downto`, a singleton `swap` on the
  receiver; CRuby's output on the unchanged build with the JIT on, off and without JIT calls) kept.
  What was learned is under gap 4.

- **perf/lsite2 (/perf-step, 2026-11-26) - a light method that has made its scope late
  enters through a scoped twin from then on** (gap 16; switch `IR_LIGHT_TWIN`, `=verbose`
  names each twin). Built on the merged branch at 2f8c31c. A `LightFrameTwin`, a constant of
  each compiled light body (not one compiled ahead of time, nor under Coverage), is told by
  the late scope's node (`LightFrame.MakeScope`) that the body made its scope; the first time
  that happens in compiled code (an interpreted body has had few calls) it transforms the
  same AST for the same declaring scope and module with `scopedTwin` (everything a body that
  may be light gets, parked scopes included, but the light frame), compiles it to IL and
  publishes it. The light body's lambda becomes `frame == null && twin.Twin != null ?
  Twin(self, block, args...) : <body>`, the test outside its `CompiledFrameExpression`, so a
  backtrace has the twin's frame only. Behaviour and backtraces matched CRuby
  (`light-remember-matrix` and the others below).
  - First cut light-compiled the twin (`RubyScriptCode.CompileLambda`): the forward then went
    through the `LightLambda` stand-in on every call, and every late scope at start-up compiled
    a twin (56 methods by `ir -e` of the shape file: `Gem::Version#canonical_segments`,
    `Enumerable#each_with_index`, `Kernel#__require_relative__`...). Twins are now compiled to
    IL and only from compiled light code: two (`H#gsub`, `H#eqt`) in the same run.
  - **Numbers** (instructions a call, `Util/perf/callgrind/count.sh`, N=20,000; twin off ->
    on): `gsub` all 12,948 -> 12,933 (on 12,715: all/on 1.017), `split` 11,037 -> 11,099
    (1.027), `case when /z/` 8,946 -> 8,863 (1.028), `=~ /d/` 6,061 -> 6,016 (1.026);
    controls `size` and `tilde` unchanged, `match?` 4,047 -> 4,079, `Pathname#+` 99,960 ->
    100,486 (`Pathname#+` got a twin: it needs its scope on some calls only, and now pays a
    scope and the forward on all of them). core-small (`IR_LIGHT_FRAMES=0` / twin off / on):
    `inject_sym` 9,232 / 9,133 / 9,333, `zip` 13,131 / 13,132 / 13,229, `inject` with a block
    10,258 / 10,279 / 10,284, `minmax` 4,757 / 4,504 / 4,531.
  - **Why: the forward costs what it saves.** callgrind with the CLR's perf map, one `gsub`
    section, twin on against `on` (scope at entry): +214 a call, all of it the forward - the
    light lambda +158, a delegate hop +56; the twin's own costs are `on`'s to the instruction
    (`CreateMethodScope`, `LeaveMethodFrame`, the body). The light lambda pays its prologue
    before its first test: the CLR zeroes its locals (each call site's `#a`/`#r` temporaries,
    the tuple, `#rfc`, `#callee`...) whatever path it takes, and that is most of what a light
    frame that makes its scope late loses. A forward placed anywhere in the light body cannot
    win; the calls have to reach the twin without it. The delegate rules call is
    `RubyMethodBody.GetDelegate`'s, baked into call plans (`RubyMethodCallPlan._method`) and
    dispatchers, behind the JIT's stub until it settles; the one way to make every site
    rebind, `RubyModule.MethodsUpdated`, bumps the global method version and invalidates all
    compiled code (`RubyUtils.InvalidateCompiledCode`), too much to pay per twin. Nor is "the
    first late scope" a policy: `Pathname#+` shows a method that needs its scope now and
    then losing its light frame for good.
  - **And it was not right.** light-remember, regex-light, block-given, global-light and
    lexical-light printed CRuby 4.0's output on the baseline and the candidate, each as
    built, with `IR_LIGHT_TWIN=0`, `IR_LIGHT_FRAMES=0`, `IR_LIGHT_REGEX=all`, `-X:NoJIT
    -X:NoOSR` and `-X:NoJITCalls`, and hot-shapes the baseline's (its known
    `Encoding::CompatibilityError`). light-rest did not, wherever the twin was on (as built,
    `IR_LIGHT_REGEX=all`, `-X:NoJIT -X:NoOSR`, `-X:NoJITCalls`): `def frame_locations(*a);
    caller_locations(0, 1)[0].label; end` answered `"Kernel#caller_locations"` as well as
    `"Object#frame_locations"` once the twin took over - a frame counted that should not be
    (the forwarding CLR frame, or the twin's call of `caller_locations` made through another
    site than the light body's late call). A twin would have to answer frame questions as the
    method it stands in for; the matrix caught it, keep it in any next attempt.
    Not run: `IR_CONFIG=R2R` start-up, census (nothing kept).

- **perf/struct2 (/perf-step, 2026-11-26) - `Hash#dup` without its two hook calls** (gap 24;
  switch `IR_FAST_DUP`). `RubyUtils.TryDuplicateObject` made the copy itself (a new Hash, the
  instance variables, then what `Hash#initialize_copy` = `#replace` does) when the receiver's class
  is Hash itself, no c_call is traced, and `initialize_dup` / `initialize_copy` resolve to the
  library's KernelOps.InitializeDuplicate and HashOps.InitializeCopy - checked once per method
  version of Hash, the resolutions marked InvalidateSitesOnOverride so that overriding either in
  Hash, a prepended or included module, Object or Kernel moves the version. `Util/hash-dup-matrix.rb`
  (new: copies' contents, defaults, compare_by_identity, ivars, frozenness, singleton methods, a
  subclass, a refinement, TracePoint, and the hooks redefined and restored, once each with the dup
  warm) printed CRuby's output on master, on the change and with the switch off. Instructions
  (`Util/perf/callgrind/count.sh`, `dup-shapes.rb`, N=20000): `hash_dup` 4,180 -> 3,816, `plain`
  1,266 both - the two dynamic calls were an eighth of the excess. Timed in a method's loop (four
  rounds each, two alternating runs, steady rounds): `H.dup` 134-137 -> 127-129 ns, CRuby 144 -
  already at CRuby's time - while `H.clone`, which the change does not enter, went 174-175 -> 198 in
  both runs (the bigger TryDuplicateObject, presumably no longer inlined or laid out as before).
  Rejected: 6% on a shape already level with CRuby against 13% on one it should not touch. What was
  learned: the "Hash#dup 2x" of gap 24 was the shape file's block driver; in a method `String#dup`
  (2.4x) and `Array#dup` (1.15x) are the dup gaps, and a fast path for them belongs in a method of
  its own beside TryDuplicateObject (NoInlining), not inside it.

- **perf/lsite (/perf-step, 2026-11-26) - a light site that remembers it needed the scope**
  (gap 16; switch `IR_LIGHT_REMEMBER`). In `LightFrameRewriter.MakeLightCall`, each site
  got a box (`StrongBox<object>`, a constant of the body like a regex literal's cache, set
  only to `RubyOps.LightScopeRequired`): `#r = memory ?? light(...)`, and the arm that makes
  the scope stores the marker when the box is empty. A stale read costs an ask or a scope,
  never a wrong answer, since the ordinary site with the scope is what the light one stands
  in for. The AOT compiler had to learn to re-create a set box empty. First version
  (`memory == null ? (r = light(), r == marker ? memory = r : r) : marker`) cost more.
  - **How it was measured.** No `ab.rb -i` here: the container refuses
    `perf_event_open`, and timing (`ab.rb -r 10`) spread 5-15% on 50-500 ns shapes. New:
    `Util/perf/callgrind/count.sh`, callgrind instrumenting only the shape files' sections,
    main thread only, tiered compilation off: steady to 0.01% section to section and process
    to process (two processes of one arm: 8,996.6 / 8,993.3), but larger than the counter's
    numbers and dependent on which shapes ran before in the process, so arms are compared
    with the same shape list. N=20,000.
  - **Regex shapes** (instructions a call; on = scope at entry, all = `IR_LIGHT_REGEX=all`;
    remember off -> on): `gsub` on 12,719 -> 12,751, all 12,954 -> 12,971 (all/on 1.017);
    `split` all 11,115 -> 11,240 (1.029 against on); `case when /z/` 8,908 -> 8,870 (1.029);
    `=~ /d/` 6,014 -> 6,018 (1.028). Controls: `size` 1,538 -> 1,575, `tilde` (a scoped
    method) 6,321 -> 6,357, `match?` 4,968 -> 5,038: +37 on every shape, the cost of the
    one node in the shape file's own driver loop, a light block run by the DLR
    interpreter (the first version: +118).
  - **inject(:+) and zip** (core-small.rb; `IR_LIGHT_FRAMES=0` / remember off / on):
    `inject_sym` 9,268 / 9,132 / 9,345, `zip` 13,168 / 13,133 / 13,256, `inject` with a block
    10,418 / 10,276 / 10,643, `minmax` (control) 4,793 / 4,503 / 4,594. In this measure the
    light frames already did not lose against scoped ones, and remembering made all four
    worse (1.009-1.036): the prelude's light frames run interpreted at first and pay the
    node on every light call.
  - **Why it cannot work: the light site is not the cost.** callgrind with the CLR's perf
    map, one `gsub` section, light (all) against scoped (on), +232 instructions a call: the
    method's own compiled body +165 (the late scope's arm: `MaterializeLocals`,
    `CreateMethodScopeLazy`'s arguments, the test), `CreateMethodScopeLazy` +35 against
    `CreateMethodScope` 57 less, `LeaveLightMethodFrameSlow` +23, `LeaveLightMethodFrame`
    +17, `TakePendingCallee(Slow)` +30, `EnterLightMethod` +12; the light site's rule (the
    dynamic `CallSite.Target`) +7. Skipping the site saves those 7 and adds the node.
  - Kept: `Util/light-remember-matrix.rb` (a site that needed the scope once and then gets a
    receiver whose method does not, and the reverse, alternating; String#tr and String#sub
    redefined at 1,000 calls and put back at 2,000; method_missing; a protected method of the
    same class then a public one; a private method with an explicit receiver; super into
    String#sub and String#gsub with a block; block_given? in a block of the method; binding
    after a scope-needing call; two threads on one site with their own `$~`; a refinement
    in force, and a method defined before `using`). It, regex-light, block-given and
    light-rest print CRuby 4.0's output on the baseline and on the candidate, each as built,
    with `IR_LIGHT_REMEMBER=0`, `IR_LIGHT_FRAMES=0`, `IR_LIGHT_REGEX=all`, `-X:NoJIT
    -X:NoOSR` and `-X:NoJITCalls`; hot-shapes prints the baseline's in every mode (its
    known `Encoding::CompatibilityError` for CRuby's FrozenError). Not run: `IR_CONFIG=R2R`.
  - Learned: what a light frame that makes its scope late pays is the frame, not the
    question - to stop losing, such a method has to enter scoped next time (gap 16's
    "Untried"), not ask less.

- **perf/jitbody (2026-11-26) - a `begin`/`rescue`/`ensure` in the method JIT's typed body**
  (gap 22; `JIT never, one thing only: Body 461`). Not attempted past the census, which
  answered it: the 461 were an error of the census. In its collect-all mode the screen
  (`JitScreen.WalkBody`) noted the refused `Body` and returned without walking what was in
  it, so every call inside a `begin` went uncounted; walked (the change kept, census only,
  every verdict the same: `-d` 0 methods changed, `-c` on Json 59/59 agree), `Body` is
  refused in 736 methods and alone in **30**, not 461 (Src/StdLib + AWFY, 28,376 methods).
  - The forms, over the 753 `never` methods with a `Body` among their refusals (a prism script
    over the census's `-l -a` list, not kept): `rescue` 639
    (with a class list 579, bare 71, `=> e` 186, several clauses 52), `ensure` 125, a
    `begin` inside the method 381, at the method's level 377, `retry` 20, `else` 14, the
    modifier 7. Of the 30 alone: `rescue` 21 (class list 20, bare 1, `=> e` 2), `ensure` 7;
    no `retry`, `else` or modifier. A bare `begin ... end` is no refusal at all (the screen
    and `JitCompiler.Emit` take a `Body` without clauses).
  - Why not the cheapest sound form, the handler as a deopt to the generic body (as round
    nineteen's `raise`): a deopt re-runs the method from the top, so it is sound only if
    nothing observable happened before the exception, and an exception out of a call may
    come after anything the callee did. So the protected part must have no call at all, and
    the 30 are all I/O and protocol wrappers whose protected part is a call (`lstat`,
    `getsockname`, `path`, `@http.finish`, `count || default`, `offsets.fetch`). One method
    has a call-free protected part: `Net::IMAP::SASL::PlainAuthenticator#process`, an
    `ensure` around a `return` of a string. Ceiling of that form: 1 method of 28,376.
    And `Util/bench/rescue-shapes.rb` calls `is_a?` in every shape (refused by the screen on
    purpose, to measure the frames), so no change here moves it; nothing was timed.
  - What a next attempt needs: the handler in the typed body itself - a .NET try/catch whose
    filter is the generic body's (`RescuePassMayTest`, `CanRescue*`, the class list
    evaluated after inner ensures, `$!` set and restored, `retry` a jump), an `ensure` as a
    finally that does not run for a deopt passing through, and a deopt inside the handler or
    the ensure declined as after an effect. It wins at most the 30 (all `maybe` behind
    calls), and they are cold. Not worth it until a run (`census.rb -u` on app-shapes)
    shows hot methods that have nothing else refused.
  - `Util/jit-rescue-matrix.rb` (new): 33 probes, hot past the thresholds and raising on
    some calls - an effect before the raise, `retry`, a class list that is a method call,
    inner ensure before the outer class list, `ensure` with `return` in the body and in the
    clause, `$!` in the handler and restored, a callee's effect then raise, the modifier,
    else+ensure, a deopting `raise` with ensure, `cause`, `throw` through an ensure, a
    rescue that does not match, a frozen self written in ensure, Timeout through a rescue,
    redefinition. It prints CRuby's output on master with the JIT on, `-X:NoJIT -X:NoOSR`,
    `-X:NoJITCalls` and `IR_LIGHT_FRAMES=0`.

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

### Caught by the gate (round thirty-seven, round 5)

- **A rest parameter handed to the callee as its own was read again through the frame's scope** -
  perf/forward's ForwardedRest, before it merged into master. Its walk refused a block literal,
  `binding`, `eval`, `local_variable_*` and a zsuper in the method, and took the splat as the only
  reference to the rest parameter otherwise; the gate's probe read it after the call with
  `instance_eval("args")`, `send(:eval, "args")`, an alias of `eval` and a TracePoint's binding at
  `:return`, and each saw the callee's change. A light frame's scope is asked for at run time by
  whatever the callee turns out to be (`RubyOps.LightScopeRequired`), so a list of names in the source
  cannot say that nothing will read a local again; only "nothing runs in the frame after the call"
  can, and a TracePoint can still look at :return. The experiment's matrix (splat-pass, kwrest-call)
  had rows for what the callee does to the Array and none for a later reader of the caller's frame.
  Off by default (`IR_FORWARD_REST=1` turns it on); `Util/forward-rest-matrix.rb` pins the readers.
  A question for any change that lets two frames share what Ruby copies: list every way the frame can
  be read after the call - eval by any name, binding by any route, TracePoint - before the walk.
