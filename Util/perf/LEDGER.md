# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-12, commit 4768b16 (the four merges below), Release net10.0, 4 cores, load 1.2-2.5.
CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0 (Oracle GraalVM Native). The machine was about
12% slower than for the first measurement (CRuby's own numbers rose by that much), so
compare ratios, not microseconds.

Are-We-Fast-Yet, steady state, µs per iteration (lower is better):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | before (22e3f91) |
|---|---:|---:|---:|---:|---:|
| DeltaBlue | 46965 | 91807 | 6358 | 1.95 | 2.23 |
| Richards | 360171 | 579224 | 17223 | 1.61 | 1.94 |
| Json | 92803 | 144937 | 7822 | 1.56 | 1.64 |
| Bounce | 99725 | 139396 | 2464 | 1.40 | 1.65 |
| Towers | 121879 | 191885 | 5283 | 1.57 | 1.90 |

Geomean: IronRuby/CRuby 1.61 (before: 1.86), TruffleRuby/CRuby 0.057.

Per operation (`Util/perf/micro.rb`), ns:

| op | CRuby | IronRuby | ratio | before |
|---|---:|---:|---:|---:|
| empty | 23.8 | 28.8 | 1.21 | 1.23 |
| ivar_rw | 39.6 | 78.1 | 1.97 | 2.72 |
| getter | 27.8 | 46.3 | 1.67 | 3.17 |
| attr | 20.6 | 15.1 | 0.73 | 0.80 |
| cmp | 29.8 | 57.4 | 1.93 | 3.58 |
| abs | 40.1 | 73.9 | 1.84 | 3.07 |
| arr | 44.9 | 55.8 | 1.24 | 1.82 |
| nil? | 27.1 | 55.1 | 2.03 | 3.48 |
| == | 30.6 | 59.5 | 1.94 | 3.35 |
| yield | 41.0 | 70.1 | 1.71 | 2.57 |
| each4 | 204.1 | 348.4 | 1.71 | 1.78 |
| while10 | 212.0 | 46.9 | 0.22 | 0.23 |
| new2 | 101.1 | 85.0 | 0.84 | 1.47 |
| symcase | 33.9 | 38.3 | 1.13 | 1.03 |
| str== | 112.6 | 66.9 | 0.59 | 1.44 |
| poly2 | 61.7 | 84.7 | 1.37 | 2.11 |
| blockeach | 67.1 | 107.5 | 1.60 | 2.33 |

## Gaps, worst first

1. **A method with a body is still ~25-45 ns dearer than an empty one** (`nil?` 2.03,
   `ivar_rw` 1.97, `==` 1.94, `cmp` 1.93, `abs` 1.84, `getter` 1.67). Every AWFY benchmark.
   The scope is gone for these bodies (Landed, perf/t1); what is left is the nested dynamic
   call site itself and the light frame's entry. Untried:
   - the method JIT (`Runtime/Jit`) declines any body with an ordinary method call
     (screen-rejected `MethodCall`); a specialized body costs ~25 ns. Teaching it calls
     other than self-recursion is the largest single thing left, and needs a deopt story
     for a body that has already had a side effect.
   - light frames still exclude optional parameters, rescue/ensure, loops, blocks, string
     interpolation and global variables.
   - a light-scope site skips LibraryDispatcher and the shared rules (they refuse the
     flag), so core-method calls from light methods go through ordinary DLR rules.
   - `block_given?` forces the scope: `o.bg { }` 92 ns against 50 without it.
2. **Blocks given to C# iterators** (`each4` 1.71, `blockeach` 1.60) and what is left of
   `yield` (1.71). Bounce, Towers, Json. Learned (perf/t2): the block body is compiled, not
   interpreted; a block that needs a scope (makes a call, or reads a closed-over local)
   costs 25-30 ns more per yield than one that does not; the Proc and BlockParam made per
   call cost ~20 ns each. Untried: reusing the Proc for library iterators known not to
   keep their block (needs a whitelist); scope-free blocks that make calls, as perf/t1 did
   for methods; lowering `BlockLoopSite.MinRun` for short loops; `yield` in the method JIT.
3. **Polymorphic sites that resolve to library methods** (`poly2` 1.37 is the Ruby-method
   case, now chained). Richards, DeltaBlue, Json have almost no class-polymorphic misses on
   Ruby methods: Richards' polymorphism is `Proc#call` over different blocks, and `==`,
   `to_s` and the like on Integer/nil/String go through DLR rules, which still pay the full
   `UpdateAndExecute` miss. Untried: chaining those rules (a DLR patch, or IronRuby calling
   the rules through its own matchmaker).
4. **First instance variable of a new object**: 35-45 ns (instance data, slot array, a
   compare-and-swap). Left alone because the compare-and-swap is what makes it safe
   without a GVL; any change needs that argument made first.

## Landed

All four measured alone against 22e3f91 on a quiet machine, then together; the merged tree
gives the same results as 22e3f91 on IronRuby.Tests (one failure, `Exception_CauseRaisedInClause1`,
which fails in Release on 22e3f91 too), ruby/spec language (2935 examples) and core for
the classes touched (12419 examples), and matches CRuby on `Util/perf/hot-shapes-matrix.rb`
with the JIT on and off. Start-up is unchanged (1.44 s for `-e 1`, best of 12, both).

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

## Rejected

Nothing yet.

## Regressions

None.
