# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-13, commit be96b9bb (round five merged), Release net10.0, 4 cores, load 1.1-1.5.
CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0 (Oracle GraalVM Native). The machine's speed
differs between sessions by tens of percent (CRuby's own numbers move with it), so compare
ratios, not microseconds.

Are-We-Fast-Yet, steady state, µs per iteration (lower is better):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | r4 | r3 | r2 | r1 | start |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 33593 | 41339 | (1320) | 1.23 | 1.41 | 1.56 | 1.64 | 1.95 | 2.23 |
| Richards | 250368 | 290033 | 12314 | 1.16 | 1.19 | 1.42 | 1.48 | 1.61 | 1.94 |
| Json | 66218 | 74582 | 6296 | 1.13 | 1.19 | 1.20 | 1.28 | 1.56 | 1.64 |
| Bounce | 72446 | 65864 | 1822 | 0.91 | 0.89 | 1.17 | 1.18 | 1.40 | 1.65 |
| Towers | 89168 | 88004 | 3986 | 0.99 | 1.02 | 1.27 | 1.28 | 1.57 | 1.90 |

Geomean: IronRuby/CRuby 1.08 (1.13, 1.32, 1.36, 1.61, 1.86 before). TruffleRuby/CRuby
about 0.05 on the four it ran normally; its DeltaBlue took 1.1 s per iteration in this run
(1.3-12 ms in earlier ones - it had not reached compiled code within 12 s), so the figure
in brackets is round three's.

Bytes allocated per iteration (`Util/perf/alloc.rb`; deterministic, unlike the timings):
DeltaBlue 24.7 MB (40.8 after round four, 53.5 before it), Richards 19.6 (76.6, 168.9),
Json 46.3 (55.9, 73.4), Bounce 11.0 (11.0, 71.1), Towers 0.16 (0.17, 89.3).

Per operation (`Util/perf/micro.rb`), ns:

| op | CRuby | IronRuby | ratio | start |
|---|---:|---:|---:|---:|
| empty | 18.2 | 21.8 | 1.20 | 1.23 |
| ivar_rw | 28.4 | 46.8 | 1.65 | 2.72 |
| getter | 21.2 | 29.0 | 1.37 | 3.17 |
| attr | 15.6 | 3.2 | 0.21 | 0.80 |
| cmp | 22.8 | 28.5 | 1.25 | 3.58 |
| abs | 27.0 | 45.2 | 1.67 | 3.07 |
| arr | 32.3 | 36.7 | 1.14 | 1.82 |
| nil? | 20.5 | 30.1 | 1.47 | 3.48 |
| == | 22.1 | 27.7 | 1.25 | 3.35 |
| yield | 32.3 | 45.9 | 1.42 | 2.57 |
| each4 | 149.6 | 192.9 | 1.29 | 1.87 |
| while10 | 154.7 | 34.3 | 0.22 | 0.23 |
| new2 | 73.9 | 64.7 | 0.88 | 1.47 |
| symcase | 24.8 | 25.0 | 1.01 | 1.03 |
| str== | 81.1 | 45.8 | 0.56 | 1.44 |
| poly2 | 45.4 | 50.2 | 1.11 | 2.11 |
| blockeach | 48.4 | 41.9 | 0.87 | 2.33 |

Profile (`Util/perf/profile.sh`, self time):

- DeltaBlue: `CallSite.Target` 22.2%, `jitentry$execute` 6.7%, libcoreclr 5.3%, block in
  Vector#each 4.5%, JIT_WriteBarrier 3.4%, `RubyOps::TakeBfcForBlockYield` 2.6%,
  Vector#append 2.2%, `jitentry$is_satisfied` 2.0%, Vector#each 2.0%,
  `JitRuntime::IsIdentical` 2.0%, `JitStub0::Call` 1.8%.
- Richards: `CallSite.Target` 31.5%, Scheduler#schedule 6.5%,
  `jitentry$is_task_holding_or_waiting` 4.8%, TaskControlBlock#run_task 4.7%, the
  create_handler block 3.4%, JIT_WriteBarrier 3.4%, `jitentry$is_waiting_with_packet` 3.1%,
  `JitStub0::Call` 2.9%.
- Json: `CallSite.Target` 26.0%, Parser#read 9.9%, MutableString::Equals 7.5%, libcoreclr
  5.3%, Parser#read_string_internal 4.0%, JIT_WriteBarrier 2.8%.
- Not leads (measured): the DLR miss path, `Proc#call` in Richards, tail calls in rules,
  the light frame's own bookkeeping, the class lookup of identity sites (fixed).

## Gaps, worst first

1. **A method the JIT wrapped and then declined keeps its trampoline** (`JitStub.GiveUp`):
   every later call pays one extra indirect hop for the rest of the process. Found when a
   wider screen let Towers' `push_disk` and `move_disks` through to an emitter that
   declined them: 9-11% of Towers. That was fixed by narrowing the screen, but the cost
   applies to every hot method declined at emit time on this build too (run
   `IR_JIT_VERBOSE=1` and count "declined"). Untried and cheap: on give-up, put the
   original entry back (or have call sites bypass a retired stub). `JitStub0::Call` at
   2-3% of Richards and DeltaBlue for *specialized* methods is the same hop: a call site
   that knows its target is specialized could call the entry delegate directly.
2. **Calls of other methods from hot bodies** (`CallSite.Target` 22-32%; `abs` 1.67,
   `ivar_rw` 1.65, `nil?` 1.47, `yield` 1.42, `getter` 1.37). Richards' `run_task` and
   `schedule` (11%), Towers' `push_disk`/`move_top_disk`, DeltaBlue's `remove_first` and
   Bounce's `bounce` all fail one rule of the method JIT: nothing that can deopt may
   follow an effect, because a deopt re-runs the generic body from the start. A second
   call, or a guard or ivar write after a call, is such a thing. See Rejected (perf/jcall)
   for the version that respects the rule and what it bought. What is needed is a guard
   failure after an effect that *continues in generic code* (a generic call for that one
   operation) instead of deopting; for calls, a method scope made on demand from the JIT
   body (locals into the generic tuple layout, the method's block, which the stub's fast
   path drops today). `IR_JIT_VERBOSE=1` prints "a deopt after an effect (EmitXxx)" per
   declined method.
3. **Json: a MutableString and its content per character** from `@input[@index]` in
   Parser#read (35% of Json's bytes), compared with one-character literals
   (`MutableString::Equals` 7.5% of its time). A one-object single-character String, or a
   content object without a per-owner back-pointer so that it can be shared, is untried.
4. **Richards: the worker block that contains a nested block** (`DATA_SIZE.times { }`
   inside the create_worker block): its scope is never reused and the inner Proc is made
   on every call, which defeats Proc, BlockParam and scope reuse for the inner block -
   about 40% of what Richards still allocates. Needs `MarkCaptured` at block exit when a
   nested Proc escaped, so that the block can otherwise keep its scope and that Proc.
5. **Int32 boxes** (Json ~10% of bytes, Richards and DeltaBlue 3-6%): emitted
   `AstUtils.Convert`/`Box` goes through the DLR's `Int32ToObject`, whose cache ends at
   999; `BlockParam.Yield(int)`, `LibraryDispatcher`, `JitRuntime.NarrowLong`. Route them
   through `Protocols.BoxInt32` (cached to 65535).
6. **`TakeBfcForBlockYield` 2.6% of DeltaBlue**: the atomic exchange added in 94dc9169 to
   reuse a BlockParam across yields. Cheaper when the frame is known to be the only user.
7. **Light frames still exclude** `super` (6 hot `initialize` methods), `rescue`,
   keywords, `$~`, regex literals; `block_given?` still forces the scope.
   `IR_LIGHT_FRAMES=why` lists them. Also: a Range per `(a..b).each`, and
   RubyInstanceData + RubyObject as two objects per Ruby object.

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
- **`x == "lit"` without allocating the literal, in code the method JIT does not
  compile**: 80 B less per comparison in a micro test, no change in AWFY bytes (Json's
  comparisons already go through the JIT). Reverted.

## Regressions

None in speed.

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
