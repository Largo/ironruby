# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-12, commit 22e3f91, Release net10.0, 4 cores, load < 1. CRuby 4.0.9 (no YJIT),
TruffleRuby 40.0.0 (Oracle GraalVM Native).

Are-We-Fast-Yet, steady state, µs per iteration (lower is better):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby |
|---|---:|---:|---:|---:|
| DeltaBlue | 40646 | 90625 | 1535 | 2.23 |
| Richards | 305010 | 590883 | 14730 | 1.94 |
| Json | 84084 | 137492 | 7544 | 1.64 |
| Bounce | 88576 | 145966 | 2270 | 1.65 |
| Towers | 112001 | 212791 | 4707 | 1.90 |

Geomean: IronRuby/CRuby 1.86, TruffleRuby/CRuby 0.045.

Per operation (`Util/perf/micro.rb`), ns:

| op | CRuby | IronRuby | ratio |
|---|---:|---:|---:|
| empty | 21.0 | 25.8 | 1.23 |
| ivar_rw | 34.9 | 94.8 | 2.72 |
| getter | 23.6 | 74.9 | 3.17 |
| attr | 17.2 | 13.7 | 0.80 |
| cmp | 24.8 | 88.9 | 3.58 |
| abs | 30.3 | 92.9 | 3.07 |
| arr | 38.4 | 69.7 | 1.82 |
| nil? | 25.0 | 86.9 | 3.48 |
| == | 26.8 | 89.7 | 3.35 |
| yield | 34.4 | 88.4 | 2.57 |
| each4 | 175.8 | 313.8 | 1.78 |
| while10 | 186.6 | 42.4 | 0.23 |
| new2 | 86.8 | 127.8 | 1.47 |
| symcase | 29.9 | 30.8 | 1.03 |
| str== | 91.9 | 132.3 | 1.44 |
| poly2 | 54.5 | 114.9 | 2.11 |
| blockeach | 57.7 | 134.3 | 2.33 |

## Gaps, worst first

1. **A method with any body costs ~60 ns more than an empty one** (`cmp` 3.58, `nil?` 3.48,
   `==` 3.35, `getter` 3.17, `abs` 3.07, `ivar_rw` 2.72). Every AWFY benchmark.
   In progress: frame/scope elision (perf/t1).
2. **Blocks** (`yield` 2.57, `blockeach` 2.33, `each4` 1.78). Bounce, Towers, Json.
   In progress: no Proc/scope for a block that does not escape (perf/t2).
3. **Polymorphic call sites** (`poly2` 2.11). Richards, DeltaBlue, Json.
   In progress: cheaper 2-8 class inline caches (perf/t4).
4. **Core-method calls** (`arr` 1.82) and **allocation** (`new2` 1.47, `str==` 1.44).
   Json, DeltaBlue. In progress: `new` as allocate + cached `initialize` (perf/t3).

## Landed

Nothing yet.

## Rejected

Nothing yet.

## Regressions

None.
