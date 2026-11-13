# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-13, commit 7b0d0b1 (round two merged), Release net10.0, 4 cores, load 1.1-2.5.
CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0 (Oracle GraalVM Native). The machine's speed
differs between sessions by tens of percent (CRuby's own numbers move with it), so compare
ratios, not microseconds.

Are-We-Fast-Yet, steady state, µs per iteration (lower is better):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | round one (4768b16) | start (22e3f91) |
|---|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 33046 | 54136 | 10488 | 1.64 | 1.95 | 2.23 |
| Richards | 244562 | 362743 | 12382 | 1.48 | 1.61 | 1.94 |
| Json | 66726 | 85181 | 6669 | 1.28 | 1.56 | 1.64 |
| Bounce | 74754 | 88381 | 1830 | 1.18 | 1.40 | 1.65 |
| Towers | 89014 | 114014 | 4014 | 1.28 | 1.57 | 1.90 |

Geomean: IronRuby/CRuby 1.36 (round one 1.61, start 1.86), TruffleRuby/CRuby 0.071
(TruffleRuby's DeltaBlue had not finished warming up in 12 s; 0.045-0.057 in earlier runs).

Per operation (`Util/perf/micro.rb`), ns:

| op | CRuby | IronRuby | ratio | round one | start |
|---|---:|---:|---:|---:|---:|
| empty | 17.8 | 29.2 | 1.64 | 1.21 | 1.23 |
| ivar_rw | 29.4 | 53.1 | 1.81 | 1.97 | 2.72 |
| getter | 22.2 | 32.0 | 1.44 | 1.67 | 3.17 |
| attr | 15.7 | 11.1 | 0.71 | 0.73 | 0.80 |
| cmp | 22.5 | 48.1 | 2.14 | 1.93 | 3.58 |
| abs | 27.5 | 49.7 | 1.81 | 1.84 | 3.07 |
| arr | 32.5 | 37.7 | 1.16 | 1.24 | 1.82 |
| nil? | 20.6 | 45.4 | 2.20 | 2.03 | 3.48 |
| == | 22.0 | 45.9 | 2.09 | 1.94 | 3.35 |
| yield | 32.7 | 46.5 | 1.42 | 1.71 | 2.57 |
| each4 | 154.7 | 197.2 | 1.27 | 1.71 | 1.87 |
| while10 | 160.3 | 36.1 | 0.23 | 0.22 | 0.23 |
| new2 | 77.4 | 74.8 | 0.97 | 0.84 | 1.47 |
| symcase | 24.8 | 27.1 | 1.09 | 1.13 | 1.03 |
| str== | 82.2 | 52.5 | 0.64 | 0.59 | 1.44 |
| poly2 | 45.7 | 63.7 | 1.39 | 1.37 | 2.11 |
| blockeach | 49.4 | 75.4 | 1.53 | 1.60 | 2.33 |

(`empty` at 1.64 is one noisy reading: 20-29 ns across this session's runs, CRuby 18-24.)

Where DeltaBlue's time is now (`Util/perf/profile.sh DeltaBlue`, self time): compiled Ruby
bodies 45% (not attributable per method, see the script's header), DLR rule delegates
(`CallSite.Target`) 20%, GC write barriers and allocation 8%, GetInstanceVariable 2.8%,
Yield1 2.1%, IsTrue 1.5%, BlockDispatcher1 1.3%, SetAttribute 1.1%, CreateMethodScope 1.1%,
RubyContext.CreateSymbol 1.0%. Before round two (Richards/DeltaBlue/Json): Ruby bodies
41-42%, rule delegates 9-14%, ivar get/set ~10%, allocation ~10%, Ruby and attr
dispatchers 4-12%, IsTrue/GetType/`!=` 4-5%, blocks 0.2-3%, method scopes 0.6-2%. The DLR
miss path is ~0.25% (sites settle) and `Proc#call` in Richards 0.8%: neither is a lead.

## Gaps, worst first

1. **Calls of core methods from compiled code go through generic DLR rules**
   (`CallSite.Target` 20% of DeltaBlue; `nil?` 2.20, `cmp` 2.14, `==` 2.09). The rule share
   doubled when light frames spread, because a light-scope site skips LibraryDispatcher
   and the shared rules (both refuse `RubyCallFlags.HasLightScope`) and the precompiled
   Ruby-method dispatchers. Untried, and the most promising next step: let those fast
   paths accept the flag when the target does not take the scope. Also untried: which
   sites the rule time belongs to (needs the perf map fixed, below).
2. **The method JIT declines any body with an ordinary method call** (screen-rejected
   `MethodCall`); a specialized body costs ~25 ns where a light one costs ~45. Needs a
   deopt story for a body that has already had a side effect.
3. **Tooling: Ruby method bodies are `[unknown]` in the profile**, 45% of samples. Their
   perf-map lines come out damaged, probably because of the non-ASCII characters in the
   DynamicMethod names. ASCII names when `DOTNET_PerfMapEnabled` is set would attribute
   that time per Ruby method, and every other gap would then be chosen from data.
4. **Blocks**: what is left is the method scope a method containing a block literal must
   make (the Proc needs it as its parent), reuse that only works within one frame
   (`#proc` is a frame variable), and loops of 16+ iterations, whose block is compiled
   inside `BlockLoopSite`, not `BlockDefinition`. `block_given?` still forces the scope.
5. **Light frames still exclude**: methods containing a block literal (about 20 of the
   benchmarks' hot methods), optional parameters (`optionalsInitialization` is outside
   the rewritten statements), `super`, `$~`, regex literals, `rescue`. A per-statement
   reason printer behind `IR_LIGHT_FRAMES` found these in minutes; worth making permanent.
6. **attr_reader/attr_writer calls** cost ~20 ns of dispatcher call whatever the ivar
   code does; inlining them at the call site (as `ReadSelfAttr` does in JIT loops) is
   where that gain is. `Object.GetType` (1%) comes from `TypeEqual` tests the expression
   compiler emits. `RubyContext.CreateSymbol` at 1% of DeltaBlue's steady state should
   not be there at all: something interns a symbol per call.
7. **Allocation** (~10%, plus ~10% GC pause; DeltaBlue allocates ~900 MB/s). perf cannot
   say where from; allocation counts (`System::GC.GetAllocatedBytesForCurrentThread`) can,
   and separate real effects from noise much better than timings on a shared machine.

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

## Rejected

- **A same-encoding fast path in `MutableString.Equals`** (gap: Json's `'0' == @current`
  chains, 6% of Json). Its share fell to 4.7%, which no timing could show. Reverted.
- **`[AggressiveInlining]` on `RubyOps.IsTrue`** removed its 0.9% from the profile with no
  measurable wall-clock gain. Not committed.

## Regressions

None.
