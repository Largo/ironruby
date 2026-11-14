# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-13, commit b0585dbe (round six merged), Release net10.0, 4 cores, load 0.9-1.2.
CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0 (Oracle GraalVM Native). The machine's speed
differs between sessions by tens of percent (CRuby's own numbers move with it), so compare
ratios, not microseconds.

Are-We-Fast-Yet, steady state, µs per iteration (lower is better):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | r5 | r4 | r3 | r2 | r1 | start |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 33418 | 42696 | 1329 | 1.28 | 1.23 | 1.41 | 1.56 | 1.64 | 1.95 | 2.23 |
| Richards | 247807 | 284750 | 12280 | 1.15 | 1.16 | 1.19 | 1.42 | 1.48 | 1.61 | 1.94 |
| Json | 66040 | 67392 | 6709 | 1.02 | 1.13 | 1.19 | 1.20 | 1.28 | 1.56 | 1.64 |
| Bounce | 72676 | 66679 | 1817 | 0.92 | 0.91 | 0.89 | 1.17 | 1.18 | 1.40 | 1.65 |
| Towers | 89810 | 84382 | 3879 | 0.94 | 0.99 | 1.02 | 1.27 | 1.28 | 1.57 | 1.90 |

Geomean: IronRuby/CRuby 1.05 (1.08, 1.13, 1.32, 1.36, 1.61, 1.86 before; the fixes for
the ten faults below cost nothing measurable). TruffleRuby/CRuby 0.046. DeltaBlue's ratio
moves between 1.23 and 1.29 from run to run.

Bytes allocated per iteration (`Util/perf/alloc.rb`; deterministic, unlike the timings):
DeltaBlue 24.7 MB (53.5 at the start of round four), Richards 19.6 (168.9), Json 35.8
(73.4), Bounce 11.0 (71.1), Towers 0.14 (89.3).

Per operation (`Util/perf/micro.rb`), ns:

| op | CRuby | IronRuby | ratio | start |
|---|---:|---:|---:|---:|
| empty | 17.6 | 21.3 | 1.21 | 1.23 |
| ivar_rw | 28.5 | 40.4 | 1.42 | 2.72 |
| getter | 22.2 | 25.6 | 1.15 | 3.17 |
| attr | 16.9 | 3.2 | 0.19 | 0.80 |
| cmp | 23.1 | 25.2 | 1.09 | 3.58 |
| abs | 27.1 | 40.0 | 1.48 | 3.07 |
| arr | 32.1 | 34.4 | 1.07 | 1.82 |
| nil? | 20.9 | 28.5 | 1.36 | 3.48 |
| == | 21.9 | 24.5 | 1.12 | 3.35 |
| yield | 32.3 | 45.0 | 1.39 | 2.57 |
| each4 | 167.8 | 187.0 | 1.11 | 1.87 |
| while10 | 156.0 | 32.5 | 0.21 | 0.23 |
| new2 | 75.2 | 61.5 | 0.82 | 1.47 |
| symcase | 25.0 | 23.7 | 0.95 | 1.03 |
| str== | 81.1 | 45.1 | 0.56 | 1.44 |
| poly2 | 45.5 | 46.8 | 1.03 | 2.11 |
| blockeach | 48.7 | 40.4 | 0.83 | 2.33 |

Profile (`Util/perf/profile.sh`, self time):

- DeltaBlue: `CallSite.Target` 24.5%, `jitentry$execute` 7.2%, libcoreclr 6.0%, block in
  Vector#each 4.3%, JIT_WriteBarrier 3.0%, Vector#append 2.4%,
  `RubyOps::TakeBfcForBlockYield` 2.3%, block in Plan#execute 2.0%.
- Richards: `CallSite.Target` 32.0%, Scheduler#schedule 6.5%, TaskControlBlock#run_task
  5.1%, `jitentry$is_task_holding_or_waiting` 4.9%, `jitentry$is_waiting_with_packet` 4.0%,
  the create_handler block 3.5%, JIT_WriteBarrier 3.5%, `ProcOps::Call` 2.3%.
- Json: `CallSite.Target` 25.7%, Parser#read 10.1%, MutableString::Equals 5.7%, libcoreclr
  5.4%, Parser#read_string_internal 4.7%, JIT_WriteBarrier 3.0%.
- Not leads (measured): the DLR miss path, tail calls in rules, the light frame's own
  bookkeeping, the class lookup of identity sites (fixed), the JIT's trampoline (gone).

## Gaps, worst first

1. **Calls of other methods from hot bodies** (`CallSite.Target` 24-32% of every
   benchmark; `abs` 1.48, `ivar_rw` 1.42, `yield` 1.39, `nil?` 1.36). Richards' `run_task`
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
   work here needs the (effect, deopt, operand order) matrix extended first.
2. **Rules bake the interpreted LightLambda of a method the JIT never wraps**
   (`LightLambda::Run*`, ~1% of Richards' `schedule`): a rule-level read of the body's
   current delegate, as b78ec3f5 did for stubs, would remove the hop once the body is
   compiled. `ObjectConstruction` could read a stub's `Target` too.
3. **Richards: the worker block that contains a nested block** (`DATA_SIZE.times { }`
   inside the create_worker block): its scope is never reused and the inner Proc is made
   on every call - about 40% of what Richards still allocates. Needs reuse decided at
   block exit (nothing called `MarkCaptured`), with every escape route of the nested Proc
   checked first; `def capture(&b); b; end` (below) is what an unchecked one looks like.
4. **Json**: the 40 B String per character read is what is left of `@input[@index]`
   (its content is shared now); two Integer boxes per `read` (`@input.length`,
   `@index += 1` - see Rejected, perf/box, for what caching them buys and costs);
   `@input[a..b]` is 160 B with a Range per call; `MutableString::Equals` 5.7%.
5. **`TakeBfcForBlockYield` 2.3% of DeltaBlue**: the atomic exchange of 94dc9169. Cheaper
   only with a proof that no second thread or reentrant call can reach a frame-local Proc.
6. **Light frames still exclude** `super` (6 hot `initialize` methods), `rescue`,
   keywords, `$~`, regex literals; `block_given?` still forces the scope.
   `IR_LIGHT_FRAMES=why` lists them. Also a Range per `(a..b).each`, and
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

The full five-suite sweep (`Util/ci-sweep.sh`, then `Util/sweep-check.rb`) was run on
commit 72c29b4c, Release net10.0, four jobs, after all of the above: core 23130 examples,
library 6011, language 2935, command_line 175, security 34; no failing example that
`Util/spec-known-failures.txt` does not list, none of its entries newly passing, no file
without a summary line. It takes under eight minutes on four cores, so it belongs in the
gate of every round, not only at the end: perf-gate's step 3 now says so. CI (Ubuntu and
Windows on net11.0, ruby/spec language, the wasm host) passes on the same commit.

Not from this work, found on the way and still open: a self-recursive method with a
logged Hash store slows down about 1000x past 65,536 levels of recursion (the shared
`JitUndoLog` fills, the outer entry deopts, and `OnDeopt` never retires it because no
generic call has completed); `NilClass#nil?` removed still answers true; `equal?` is true
for equal Bignums and Floats; `local_variables` lists block-locals before parameters;
backtrace lines of multi-line statements point at the statement's first line;
`method(:binding).call` works where CRuby refuses; a thread dying of SystemStackError
prints its whole backtrace.
