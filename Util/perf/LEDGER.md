# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-13, commit 98d4a29 (round four merged), Release net10.0, 4 cores, load 1.2-2.7.
CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0 (Oracle GraalVM Native). The machine's speed
differs between sessions by tens of percent (CRuby's own numbers move with it), so compare
ratios, not microseconds.

Are-We-Fast-Yet, steady state, µs per iteration (lower is better):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | round 3 | round 2 | round 1 | start |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 34412 | 48545 | 12199 | 1.41 | 1.56 | 1.64 | 1.95 | 2.23 |
| Richards | 254200 | 301914 | 12282 | 1.19 | 1.42 | 1.48 | 1.61 | 1.94 |
| Json | 70429 | 83670 | 7638 | 1.19 | 1.20 | 1.28 | 1.56 | 1.64 |
| Bounce | 77692 | 68913 | 1882 | 0.89 | 1.17 | 1.18 | 1.40 | 1.65 |
| Towers | 90170 | 91877 | 3881 | 1.02 | 1.27 | 1.28 | 1.57 | 1.90 |

Geomean: IronRuby/CRuby 1.13 (1.32, 1.36, 1.61, 1.86 before). TruffleRuby/CRuby 0.046-0.072
(its DeltaBlue sometimes has not warmed up in 12 s: 1320 µs in the round-three run).

Bytes allocated per iteration (`Util/perf/alloc.rb`; deterministic, unlike the timings):
DeltaBlue 40.8 MB (53.5 before round four), Richards 76.6 (168.9), Json 55.9 (73.4),
Bounce 11.0 (71.1), Towers 0.17 (89.3).

Per operation (`Util/perf/micro.rb`), ns:

| op | CRuby | IronRuby | ratio | start |
|---|---:|---:|---:|---:|
| empty | 18.3 | 20.2 | 1.10 | 1.23 |
| ivar_rw | 28.8 | 46.3 | 1.61 | 2.72 |
| getter | 21.0 | 27.5 | 1.31 | 3.17 |
| attr | 15.6 | 4.1 | 0.26 | 0.80 |
| cmp | 22.7 | 26.7 | 1.18 | 3.58 |
| abs | 26.8 | 47.0 | 1.75 | 3.07 |
| arr | 32.1 | 38.0 | 1.18 | 1.82 |
| nil? | 20.7 | 31.1 | 1.50 | 3.48 |
| == | 22.2 | 26.0 | 1.17 | 3.35 |
| yield | 32.2 | 45.4 | 1.41 | 2.57 |
| each4 | 151.1 | 180.4 | 1.19 | 1.87 |
| while10 | 155.6 | 35.0 | 0.22 | 0.23 |
| new2 | 74.3 | 66.6 | 0.90 | 1.47 |
| symcase | 24.8 | 27.3 | 1.10 | 1.03 |
| str== | 80.9 | 47.9 | 0.59 | 1.44 |
| poly2 | 45.5 | 55.2 | 1.21 | 2.11 |
| blockeach | 49.0 | 42.6 | 0.87 | 2.33 |

Profile (`Util/perf/profile.sh`, self time):

- DeltaBlue: `CallSite.Target` 18.8%, libcoreclr 9.2%,
  `RubyContext::GetImmediateClassOfNonRubyObject` 4.5%, `jitentry$execute` 4.4%, block in
  Vector#each 4.0%, JIT_WriteBarrier 3.3%, `Object::GetType` 2.3%, Vector#append 2.1%,
  Yield1 1.8%, `JitRuntime::IsIdentical` 1.6%.
- Richards: `CallSite.Target` 30.4%, Scheduler#schedule 5.8%,
  `jitentry$is_task_holding_or_waiting` 4.8%, TaskControlBlock#run_task 4.4%, the
  create_handler/create_device blocks 4.0% and 3.3%, `jitentry$is_waiting_with_packet` 3.7%,
  JIT_WriteBarrier 3.3%, `JitStub0::Call` 2.6%.
- Json: `CallSite.Target` 25.0%, Parser#read 9.3%, MutableString::Equals 6.9%, libcoreclr
  5.8%, Parser#read_string_internal 3.5%, JIT_WriteBarrier 3.2%, Parser#is_white_space 2.9%.
- Not leads (measured): the DLR miss path, `Proc#call` in Richards, tail calls in rules,
  the light frame's own bookkeeping (all of it switched off together: 2-3 ns; a
  light-frame call allocates nothing).

## Gaps, worst first

1. **New in the profile after round four, and cheap to look at: the method JIT's identity
   compare on a receiver that is not a RubyObject.** DeltaBlue spends 4.5% in
   `RubyContext::GetImmediateClassOfNonRubyObject`, 2.3% in `Object::GetType` and 1.6% in
   `JitRuntime::IsIdentical`: `IdentityCallSite` (bf354cf) looks up the class of a nil or
   Symbol receiver on every call (`@direction == :forward`, `@x != nil`). nil, true, false
   and Symbol have fixed classes; test for them before the lookup, or cache per exact CLR type.
2. **Ordinary calls inside a method-JIT body** (`CallSite.Target` 19-30%; `abs` 1.75,
   `ivar_rw` 1.61, `nil?` 1.50, `yield` 1.41, `getter` 1.31). The JIT now takes ivar
   operands and writes, identity compares and attr calls, but a self call or a call on an
   object still screen-rejects the method: Richards' `run_task` and `schedule` (10% of it),
   all of Towers' hot methods, Json's Parser methods, DeltaBlue's `recalculate`
   (`weakest`), `Vector#remove_first` (`empty?`), Bounce's `Ball#bounce` (`.abs`, and its
   guards come after writes). A generic call site in a JIT body, placed so that nothing
   after it can deopt - or guard failures that fall back to generic code for the rest of
   the body instead of re-running it - is the largest single thing left. `IR_JIT_VERBOSE=1`
   prints `screen rejected Class/name: reason` per method.
3. **A specialized body that is no faster**: EqualityConstraint#execute compiles
   (`jitentry$execute` 4.4%) but each step is a helper call (`GetIVar`, `ReadSelfAttr`,
   `SetIVar`). Emit the cached shape check and slot load inline, as 1ee9cdc did for DLR code.
4. **What is still allocated** (tables by type and cause came from an EventPipe
   AllocationTick trace; `Util/perf/alloc.rb` gives the totals):
   - RubyMethodScope (DeltaBlue 34%, Json 18%): Vector#append has a block literal
     (`each_index`, run only when the storage grows) and so is never light; Vector#initialize
     has an optional parameter; Vector#each yields from a block. A scope made only on the
     path that reaches the block literal is untried.
   - BlockParam per `Proc#call` (Richards 35%): `ProcOps.Call` allocates one for every
     `@function.call`.
   - RubyBlockScope for Richards' task blocks (27%): their bodies fail `IsScopeFree`
     (op-assign on an attribute, `data.count -= 1`).
   - Json `@input[@index]`: a MutableString and its content per character; a compare of
     `s[i]` with a one-character literal that never materializes the String would remove
     both, and `MutableString::Equals` (6.9%) with them.
   - Int32 boxes from `ScriptingRuntimeHelpers.Int32ToObject` in emitted code and from
     library methods returning `int`: route through `Protocols.BoxInt32` (cached to 65535).
   - Range objects for `(a..b).each` in Vector#each; RubyInstanceData + RubyObject, two
     objects per Ruby object.
5. **Light frames still exclude** `super` (6 hot `initialize` methods), optional
   parameters, `rescue`, `$~`, regex literals, and any method with a block literal;
   `block_given?` still forces the scope. `IR_LIGHT_FRAMES=why` lists them.

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

## Rejected

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
  the JIT refuses in method bodies. Worth redoing after gap 1(b), with the str== case fixed.

## Regressions

None.
