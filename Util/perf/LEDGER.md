# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-13, commit 6940016 (round three merged), Release net10.0, 4 cores, load 1.3-3.
CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0 (Oracle GraalVM Native). The machine's speed
differs between sessions by tens of percent (CRuby's own numbers move with it), so compare
ratios, not microseconds.

Are-We-Fast-Yet, steady state, µs per iteration (lower is better):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | round two | round one | start |
|---|---:|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 33709 | 52674 | 1320 | 1.56 | 1.64 | 1.95 | 2.23 |
| Richards | 246107 | 348616 | 12301 | 1.42 | 1.48 | 1.61 | 1.94 |
| Json | 69779 | 83989 | 6941 | 1.20 | 1.28 | 1.56 | 1.64 |
| Bounce | 74499 | 87186 | 1842 | 1.17 | 1.18 | 1.40 | 1.65 |
| Towers | 89147 | 113134 | 3972 | 1.27 | 1.28 | 1.57 | 1.90 |

Geomean: IronRuby/CRuby 1.32 (round two 1.36, round one 1.61, start 1.86),
TruffleRuby/CRuby 0.046.

Per operation (`Util/perf/micro.rb`), ns:

| op | CRuby | IronRuby | ratio | start |
|---|---:|---:|---:|---:|
| empty | 17.7 | 21.8 | 1.23 | 1.23 |
| ivar_rw | 28.4 | 49.5 | 1.74 | 2.72 |
| getter | 21.2 | 30.4 | 1.43 | 3.17 |
| attr | 15.5 | 10.5 | 0.68 | 0.80 |
| cmp | 22.6 | 41.8 | 1.85 | 3.58 |
| abs | 27.3 | 49.2 | 1.80 | 3.07 |
| arr | 32.3 | 37.3 | 1.15 | 1.82 |
| nil? | 20.5 | 40.3 | 1.97 | 3.48 |
| == | 22.3 | 42.8 | 1.92 | 3.35 |
| yield | 32.3 | 45.8 | 1.42 | 2.57 |
| each4 | 151.4 | 206.8 | 1.37 | 1.87 |
| while10 | 155.2 | 34.6 | 0.22 | 0.23 |
| new2 | 74.3 | 68.6 | 0.92 | 1.47 |
| symcase | 24.8 | 26.4 | 1.06 | 1.03 |
| str== | 81.6 | 47.9 | 0.59 | 1.44 |
| poly2 | 45.8 | 61.1 | 1.33 | 2.11 |
| blockeach | 48.7 | 65.3 | 1.34 | 2.33 |

Profile (`Util/perf/profile.sh`, self time, Ruby bodies now named):

- DeltaBlue: `CallSite.Target` (DLR rule delegates) 25.8%, libcoreclr (GC, allocation
  helpers) 8.8%, block in Vector#each (som.rb:100) 4.7%, EqualityConstraint#execute 4.0%,
  JIT_WriteBarrier 4.0%, block in Plan#execute 2.6%, Vector#append 2.2%, memset 2.2%,
  Yield1 1.8%, Vector#each 1.7%, BlockDispatcher1 1.7%.
- Richards: `CallSite.Target` 32.9%, Scheduler#schedule 6.2%, TaskControlBlock#run_task
  4.8%, JIT_WriteBarrier 4.5%, TaskState#is_task_holding_or_waiting 3.7%,
  #is_waiting_with_packet 3.5%, the create_handler/create_device blocks 3.4% and 3.1%.
- Json: `CallSite.Target` 22.4%, libcoreclr 9.5%, Parser#read 9.2%, MutableString::Equals
  5.6%, Parser#read_string_internal 3.9%, JIT_WriteBarrier 3.4%.
- `ruby Util/perf/profile-agg.rb X.stacks 10 'self:CallSite.Target'` says whose sites the
  rule time is: in DeltaBlue it is spread thin - EqualityConstraint#execute 2.2%,
  Planner#add_propagate 1.9%, block in Vector#each 1.7%, ScaleConstraint#execute 1.6% -
  no one site to fix.
- Not leads (measured): the DLR miss path (~0.25%, sites settle), `Proc#call` in Richards
  (~1%), `Object.GetType` (0.1-0.6%, spread out).

## Gaps, worst first

1. **A method that makes a call pays for its light frame, and a rule per call**
   (`nil?` 1.97, `==` 1.92, `cmp` 1.85, `abs` 1.80, `ivar_rw` 1.74; `CallSite.Target`
   22-33% of every benchmark). What three experiments established:
   - the rule itself is cheap: for `o.nil?` it is a type test, a version compare and the
     call, ~2 ns of a 42 ns call. Most of the ~18 ns over an empty method (which the
     method JIT compiles, frameless) is the light frame: `EnterLightMethod`/the
     RuntimeFlowControl it makes, `TakePendingCallee`, `LeaveMethodFrame`, argument
     temporaries, the `LightScopeRequired` compare, `IsTrue` on the result;
   - hot calls of core methods are rule-bound from any site (LibraryDispatcher only
     serves the first calls), so `HasLightScope` is not what keeps them off a fast path;
   - taking rules' library calls out of tail position so the JIT can inline them
     measured nothing while the frame is there.
   Untried, in the order the evidence suggests: (a) make the light frame cheaper - what
   of `EnterLightMethod`/RFC/`TakePendingCallee`/`LeaveMethodFrame` a body that cannot
   raise through a block or be the target of `return` from a block actually needs;
   (b) let the method JIT (frameless) take instance-variable operands in method bodies
   with guards, as `NumericIVar` does in loops - `WalkOperand` refuses `InstanceVariable`,
   which is what screen-rejects `@x < o`, `@v.abs` and much of DeltaBlue; (c) calls of
   Ruby methods and attr readers from a JIT body, which the screen also rejects.
2. **Allocation and GC**: JIT_WriteBarrier 3.4-4.7%, libcoreclr 9%, memset 2%, plus ~10%
   GC pause; DeltaBlue allocates ~900 MB/s. Count with
   `System::GC.GetAllocatedBytesForCurrentThread` per call of the hot methods the profile
   names (Vector#append, the Vector#each block, EqualityConstraint#execute) to find what
   is allocated: boxed Integers, RuntimeFlowControl, BlockParam, argument arrays.
3. **Blocks**: Yield1 + BlockDispatcher1 + LightLambda::Run2 ~5% of DeltaBlue. A method
   containing a block literal still makes its scope (18 of the benchmarks' 37 non-light
   hot methods stop there; `IR_LIGHT_FRAMES=why` lists them); DeltaBlue's `execute` passes
   its block to Vector#each, a Ruby method that is not light, so the Proc is never
   reusable. Marking a literal reusable when the callee is a Ruby method that only yields
   is untried. See Rejected for parked scopes.
4. **Json string compares**: MutableString::Equals 5.6% from `c == " "` chains in
   is_white_space/is_digit/read_value, which go through `ValueNotEquals` and a rule. A
   one-character-literal fast path is untried (a general Equals fast path was, see Rejected).
5. **Light frames still exclude** `super` (6 hot `initialize` methods), optional
   parameters, `rescue`, `$~`, regex literals; `block_given?` still forces the scope.
6. **Richards**: `WeakReference::get_Target` 1.3% on a hot path - find the reader.

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
