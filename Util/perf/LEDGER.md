# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-14, commit 832b73f0 (round six merged), Release net10.0, 4 cores, load 1.0-1.5.
CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0 (Oracle GraalVM Native). The machine's speed
differs between sessions by tens of percent (CRuby's own numbers move with it), so compare
ratios, not microseconds.

Are-We-Fast-Yet, all thirteen, steady state, µs per iteration (lower is better). "before"
is the tree without round six (9f4d44f8: round five plus the review fixes), against CRuby
at the start of the same session:

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | before |
|---|---:|---:|---:|---:|---:|
| Richards | 335459 | 423234 | 15680 | 1.26 | 1.33 |
| Permute | 117385 | 146645 | 6292 | 1.25 | 1.49 |
| DeltaBlue | 43436 | 52176 | 1680 | 1.20 | 1.17 |
| Json | 84958 | 98995 | 7424 | 1.17 | 1.17 |
| NBody | 1670115 | 1782565 | 15802 | 1.07 | 1.07 |
| Bounce | 90083 | 87169 | 2321 | 0.97 | 0.97 |
| Towers | 110777 | 104076 | 4790 | 0.94 | 0.99 |
| Queens | 97182 | 90278 | 6500 | 0.93 | 0.98 |
| Sieve | 151085 | 136804 | 7445 | 0.91 | 0.89 |
| CD | 771211 | 588410 | 34310 | 0.76 | 1.17 |
| List | 104352 | 70726 | 7048 | 0.68 | 0.68 |
| Storage | 152215 | 94251 | 16898 | 0.62 | 0.61 |
| Mandelbrot | 1006523 | 138831 | 47925 | 0.14 | 0.14 |

Geomean of the thirteen: IronRuby/CRuby 0.823 (0.872 before), TruffleRuby/CRuby 0.046.
The two columns of ratios come from runs against CRuby hours apart, so a difference of a
few hundredths between them (DeltaBlue, Sieve, Queens, Towers) is not a change: the two
trees measured against each other are under "Landed", round six.

The first five rounds measured only five of these. Their history, IronRuby/CRuby; "r5
here" is the round-five tree in this session, "r5" what was recorded when it landed, in
another session, which is how far the same tree moves between sessions:

| benchmark | now | r5 here | r5 | r4 | r3 | r2 | r1 | start |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 1.20 | 1.17 | 1.23 | 1.41 | 1.56 | 1.64 | 1.95 | 2.23 |
| Richards | 1.26 | 1.33 | 1.16 | 1.19 | 1.42 | 1.48 | 1.61 | 1.94 |
| Json | 1.17 | 1.17 | 1.13 | 1.19 | 1.20 | 1.28 | 1.56 | 1.64 |
| Bounce | 0.97 | 0.97 | 0.91 | 0.89 | 1.17 | 1.18 | 1.40 | 1.65 |
| Towers | 0.94 | 0.99 | 0.99 | 1.02 | 1.27 | 1.28 | 1.57 | 1.90 |
| geomean | 1.10 | 1.12 | 1.08 | 1.13 | 1.32 | 1.36 | 1.61 | 1.86 |

Bytes allocated per iteration (`Util/perf/alloc.rb`; deterministic, unlike the timings),
MB, before round six -> now: DeltaBlue 24.7 -> 24.8, Richards 19.6 -> 19.5, Json 46.3 ->
46.3, CD 515.9 -> 225.8, Bounce 11.0 -> 11.0, List 8.2 -> 8.2, Mandelbrot 94.6 -> 94.7,
NBody 2892 -> 2892, Permute 124.6 -> 124.6, Queens 41.3 -> 41.2, Sieve 98.8 -> 98.7,
Storage 112.6 -> 112.6, Towers 0.17 -> 0.13. Only CD changed: Towers allocates so little
that its figure moves by a quarter between two runs of one tree (0.14 and 0.17 on 9f4d44f8).

Per operation (`Util/perf/micro.rb`), ns, the median of four runs; a line moves by 15-20%
from run to run on this machine, so the ratio is good to about that:

| op | CRuby | IronRuby | ratio | start |
|---|---:|---:|---:|---:|
| empty | 21.3 | 23.2 | 1.09 | 1.23 |
| ivar_rw | 33.7 | 56.8 | 1.69 | 2.72 |
| getter | 23.5 | 31.8 | 1.36 | 3.17 |
| attr | 16.8 | 3.6 | 0.21 | 0.80 |
| cmp | 25.8 | 29.4 | 1.14 | 3.58 |
| abs | 31.4 | 53.1 | 1.69 | 3.07 |
| arr | 38.4 | 43.1 | 1.12 | 1.82 |
| nil? | 23.5 | 35.5 | 1.51 | 3.48 |
| == | 25.2 | 28.7 | 1.14 | 3.35 |
| yield | 37.3 | 55.3 | 1.48 | 2.57 |
| each4 | 171.4 | 223.6 | 1.30 | 1.87 |
| while10 | 187.1 | 40.7 | 0.22 | 0.23 |
| new2 | 92.0 | 74.0 | 0.81 | 1.47 |
| symcase | 31.9 | 27.6 | 0.87 | 1.03 |
| str== | 101.8 | 52.9 | 0.52 | 1.44 |
| poly2 | 54.8 | 68.5 | 1.25 | 2.11 |
| blockeach | 59.7 | 52.4 | 0.88 | 2.33 |
| lit== | 178.0 | 61.6 | 0.35 | 0.94 |
| swap | 55.6 | 59.6 | 1.07 | 1.37 |
| locattr | 49.4 | 42.4 | 0.86 | 1.35 |

The last three are round six's shapes (two runs; their "start" is the tree before round
six): a literal as the receiver of `==`, two element stores in one method, Float attr
reads on locals assigned from attr reads.

Profile (`Util/perf/profile.sh`, self time), the six benchmarks that were slower than
CRuby before round six:

- Richards: `CallSite.Target` 31.4%, Scheduler#schedule 6.4%,
  `jitentry$is_task_holding_or_waiting` 4.8%, TaskControlBlock#run_task 4.6%,
  `jitentry$is_waiting_with_packet` 3.4%, the create_handler block 3.3%, JIT_WriteBarrier
  3.0%, `ProcOps::Call` 2.7%, Scheduler#queue_packet 2.7%, `JitStub0::Call` 2.2%.
- Permute: Permute#permute 17.8%, `CallSite.Target` 16.0%, `jitentry$swap` 10.3%, the
  block in permute 9.3%, JIT_WriteBarrier 6.7%, libcoreclr 6.3%, `Object::GetType` 5.3%,
  `JitRuntime::IndexInt` 2.8%, `RubyMethodScope::.ctor` 1.9%. (`JitUndoLog` and its
  thread-local lookup, 9% before, are gone.)
- DeltaBlue: `CallSite.Target` 22.9%, `jitentry$execute` 6.7%, libcoreclr 5.2%, block in
  Vector#each 4.4%, JIT_WriteBarrier 3.0%, `RubyOps::TakeBfcForBlockYield` 2.9%,
  Vector#append 2.3%, `JitStub0::Call` 2.0%.
- Json: `CallSite.Target` 23.6%, Parser#read 8.9%, libcoreclr 5.1%,
  Parser#read_string_internal 4.8%, `MutableString::Equals` 4.0% (7.2% before),
  `JitStub0::Call` 3.5% (1.3% before), JIT_WriteBarrier 3.1%, `jitentry$is_white_space` 2.3%.
- NBody: `CallSite.Target` 23.0%, the inner block of advance 14.0%,
  `RubyObjectAttributeReaderDispatcherWithScope::Invoke` 13.2%, libcoreclr 11.7%,
  JIT_WriteBarrier 5.8%, the attribute writer dispatcher 3.6%.
- CD: `CallSite.Target` 26.2% (33.0% before), RedBlackTree#put 8.3%, libcoreclr 8.1%,
  `jitentry$is_in_voxel` 6.0% (the method was 12.7% and its sites another 11%),
  RedBlackTree#tree_insert 5.7%, libclrjit 4.0%, Vector2D#compare_to 3.6%,
  CollisionDetector#recurse 3.4%, RedBlackTree#find_node 2.6%.
- Not leads (measured): the DLR miss path, `Proc#call` in Richards, tail calls in rules,
  the light frame's own bookkeeping, the class lookup of identity sites (fixed), the
  attribute dispatchers' own code (round six, Rejected), libclrjit at steady state in a
  profile taken under load (late tiering of the runtime's own code, not rules being
  rebuilt: the count of compiled methods per NBody iteration falls to about a dozen).

## Gaps, worst first

1. **Calls of other methods from hot bodies** (`CallSite.Target` 16-31%; `ivar_rw` 1.69,
   `abs` 1.69, `nil?` 1.51, `yield` 1.48, `getter` 1.36). Richards' `run_task` and
   `schedule`, Towers' `push_disk`/`move_top_disk`, DeltaBlue's `remove_first` and Bounce's
   `bounce` all fail one rule of the method JIT: nothing that can deopt may follow an
   effect, because a deopt re-runs the generic body from the start. A second call, or a
   guard or ivar write after a call, is such a thing. See Rejected (perf/jcall) for the
   version that respects the rule and what it bought. What is needed is a guard failure
   after an effect that *continues in generic code* (a generic call for that one
   operation) instead of deopting; for calls, a method scope made on demand from the JIT
   body (locals into the generic tuple layout, the method's block, which the stub's fast
   path drops today). `IR_JIT_VERBOSE=1` prints "a deopt after an effect (EmitXxx)" per
   declined method. Round six adds two things. Richards' 31% has no single owner:
   `schedule` 5.4%, the create_handler block 5.2%, `run_task` 3.7%, `queue_packet` 3.5%,
   the create_device block 2.1%, `release` 1.3%, all ordinary sites in code the JIT does
   not compile. And a generic call made from a JIT body needs the caller's scope to be the
   call the source makes: without one it sees private and protected methods and no
   refinements (round six, "Caught by the gate"), so "continues in generic code" has to
   carry self's class at least.
2. **CD's red-black tree** (`put`, `tree_insert`, `find_node`, `Vector2D#compare_to`: 20%
   of self time now that `is_in_voxel` is compiled). `compare_to` is screen-rejected over
   its self call of `compare_numbers`, a three-line Float-only method, and `tree_insert`
   and `find_node` over their call of `compare_to`. Untried: inline a small pure self
   call into the caller's specialization.
3. **NBody: 2.9 GB allocated per iteration**, about 80% of it Float boxes (some 35 per
   pair of bodies; the same loops with an empty inner body allocate a fifth - this and
   the figures below are the NBody experiment's, taken on a loaded machine). Mandelbrot,
   whose Floats live in a `while` loop OSR compiles, runs at 0.14 of CRuby; NBody's are in
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
   part of this shows on NBody alone, which is why it was not attempted in round six.
4. **A method the JIT wrapped and then declined keeps its trampoline** (`JitStub.GiveUp`):
   every later call pays one extra indirect hop for the rest of the process. Found when a
   wider screen let Towers' `push_disk` and `move_disks` through to an emitter that
   declined them: 9-11% of Towers. That was fixed by narrowing the screen, but the cost
   applies to every hot method declined at emit time on this build too (run
   `IR_JIT_VERBOSE=1` and count "declined"). Putting the original entry back on give-up
   is less cheap than it looked: rules embed the trampoline's delegate when they are
   built, so they would have to be bound again. In Richards only two methods are declined
   after being wrapped (`RBObject#append`, `Scheduler#hold_self`). The hop that costs is
   the one into bodies that *were* specialized: `JitStub0::Call` is 2-3.5% of Richards,
   DeltaBlue and Json, and rose from 1.3% to 3.5% of Json when round six compiled
   `is_white_space` and `is_digit`. A call site that knows its target is specialized
   could call the entry delegate directly.
5. **Permute: `permute` itself** (17.8% self, and a RubyMethodScope per call: it is not a
   light frame, `IR_LIGHT_FRAMES=why` says why, nobody has read it yet), its `n != 0`
   through `BasicObject#!=` (gap 9), and `Object::GetType` at 5.3%.
6. **Json: a MutableString and its content per character** from `@input[@index]` in
   Parser#read (35% of Json's bytes). A one-object single-character String, or a content
   object without a per-owner back-pointer so that it can be shared, is untried. What is
   left of `MutableString::Equals` (4.0%) is `@current != '"'` and `case @current when`
   in `read`, `read_value` and `read_string_internal`, which the JIT declines for their
   `raise` and their calls: the character comparison of round six only helps where the
   method JIT compiles the comparison.
7. **Richards: the worker block that contains a nested block** (`DATA_SIZE.times { }`
   inside the create_worker block): its scope is never reused and the inner Proc is made
   on every call, which defeats Proc, BlockParam and scope reuse for the inner block -
   about 40% of what Richards still allocates. Needs `MarkCaptured` at block exit when a
   nested Proc escaped, so that the block can otherwise keep its scope and that Proc.
   NBody's inner `.each` pays the same 224 B per outer iteration.
8. **Int32 boxes** (Json ~10% of bytes, Richards and DeltaBlue 3-6%): emitted
   `AstUtils.Convert`/`Box` goes through the DLR's `Int32ToObject`, whose cache ends at
   999; `BlockParam.Yield(int)`, `LibraryDispatcher`, `JitRuntime.NarrowLong`. Route them
   through `Protocols.BoxInt32` (cached to 65535).
9. **`!=` outside the method JIT is two dispatches**: `BasicObject#!=` calls `==` through
   a site shared by every `!=` in the runtime, so that site is polymorphic. 2.9% of
   Richards inclusive (`NO_TASK != @current_task`), 1.2-1.6% self in Json and Permute.
   See Rejected (perf/richards) for a correct fast path that was too small to show.
10. **`TakeBfcForBlockYield` 2.9% of DeltaBlue**: the atomic exchange added in 94dc9169 to
    reuse a BlockParam across yields. Cheaper when the frame is known to be the only user.
11. **Light frames still exclude** `super` (6 hot `initialize` methods), `rescue`,
    keywords, `$~`, regex literals; `block_given?` still forces the scope.
    `IR_LIGHT_FRAMES=why` lists them. Also: a Range per `(a..b).each`, and
    RubyInstanceData + RubyObject as two objects per Ruby object.
12. **The method JIT refuses a constant whose value is nil** ("constant without a value":
    it cannot tell nil from an autoload that has not run). Richards' `NO_WORK = nil` and
    `NO_TASK = nil` keep three methods out. See Rejected for why fixing only that is not
    worth it yet.
13. **One site, four receiver classes, two speeds**: `recv != other` in a loop over four
    plain classes takes about 105 ns or about 280 ns per iteration, the same binary, from
    one process to the next (three of six runs slow on 9f4d44f8). Two and three classes
    do not do it. Unexplained, and outside AWFY, but a factor of 2.6 on a polymorphic
    site is worth an afternoon.

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

Round six: five experiments at once, one agent and one worktree each, on the benchmarks
slower than CRuby when all thirteen were run for the first time (Permute, Richards, CD,
Json with DeltaBlue, NBody). Three were kept. Each was measured alone against 9f4d44f8 on
a quiet machine, all thirteen benchmarks, in one awfy.rb invocation with the others as
further columns; then together, once in each column order: geomean of the thirteen 0.954
and 0.959 (CD 0.660 and 0.694, Permute 0.850 and 0.869, Json 0.981 and 0.985, no
benchmark more than 5% slower in both). The merged tree has 9f4d44f8's results on
IronRuby.Tests in Release on net10.0 (1253 tests, one failure on both trees,
`Exception_CauseRaisedInClause1`; on net11.0, run on the merged tree only, all 1253
pass) and on ruby/spec language, core/string and core/array with
basicobject and comparable (2935, 3963 and 3124 examples, the same failing examples), and
prints what CRuby prints on jit-, hot-shapes- and ivar-matrix with the JIT on and off (630,
281 and 252 lines); ivar-stress passes; start-up is unchanged (`-e 1`, best of 12: 1.19 s
against 1.16, medians 1.29 and 1.28).

- **perf/cd, c8802329 - the method JIT types attr reads and writes on a local assigned
  once from an attr read** (`init = motion.pos_one; fin.x - init.x`). `TrySample` knew
  self, self's instance variables and parameters; CD's hottest method,
  `CollisionDetector#is_in_voxel`, reads attributes of locals and was declined ("call:
  x/0"), so its forty Float operations went through sites and boxed. The local's sample
  is what the sampled owner's instance variable holds; it is a profile, and every read
  still checks the receiver's class and the method version. Alone: CD 0.632 and 0.720,
  geomean 0.973 and 0.976; bytes per CD iteration 516 MB -> 226 MB; locattr 66 -> 42 ns.
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
  0.856, 0.868, 0.841; geomean of the thirteen 1.010 and 0.999; swap 76 -> 60 ns. It is
  used only for an instance-variable or local receiver, while Array#[]= is the public
  builtin, with no refinement active; everything else compiles logged as before. See
  "Caught by the gate" for why the receiver is checked first.
- **perf/json, e4fc38aa - the method JIT compiles `"lit" == v` and `"lit" != v`**, and
  both string intrinsics compare an ASCII literal by characters with a String that holds
  characters in an encoding where ASCII is its own bytes (`MutableString.EqualsAsciiLiteral`).
  Json's `is_white_space` and `is_digit` are chains of `' ' == @current`; with the
  literal as the receiver they were screen-rejected. Per call, three rounds: four
  comparisons 224, 219, 229 ns -> 96, 103, 102; ten 477, 463, 466 -> 159, 163, 164.
  On AWFY it is inside the spread (Json 0.984 and 0.963 alone, geomean 0.999 and 0.996):
  kept on the per-operation rule, like perf/t3. `MutableString::Equals` 7.2% -> 4.0% of
  Json, and `JitStub0::Call` 1.3% -> 3.5%, the price of entering the two bodies (gap 4).
- **Tooling**: the skills run all thirteen benchmarks for every recorded number and
  decision, both column orders, all candidates of a round as columns of one run;
  `micro.rb` has `lit==`, `swap` and `locattr`; perf-gate asks a fourth question and says
  how to tell that a differential case reaches its path.

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

Round six (none of these is in the tree; the branches were local to the session):

- **perf/richards - `BasicObject#!=` answers by identity for nil and plain objects whose
  `==` is still BasicObject's** (gap 9), looked up once per global method version and
  kept beside the `==` site (`BinaryOpStorage`). Correct: 57 cases with `==` defined,
  removed, singleton, included, prepended, extended, on Object, Kernel, NilClass and
  BasicObject, undefined, private, refined, all as CRuby. `ValueNotEquals` 2.9% -> 1.2%
  of Richards inclusive. Richards against 9f4d44f8, five quiet runs: 0.962, 0.948, 0.937,
  0.995, 1.141; the Json branch, which cannot touch Richards, gave 0.942 and 0.958 in the
  same invocations. Geomean of the thirteen 1.008 and 1.023; `!=` itself about 5 ns of 60
  faster in generic code with one to three receiver classes. A gain its own profile puts
  at two percent cannot be shown on this machine, so it is not kept; it belongs with
  whatever closes gap 9 properly (one dispatch, as CRuby's opt_neq).
- **Nil-valued constants in the method JIT** (gap 12; Richards experiment, first
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

### Caught by the gate (round six)

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
- **Three of the gate's own cases tested nothing at first.** Methods written with an
  implicit self (`c = a`) that the JIT declined for that, so the new path never ran; a
  matrix case that reported with `x == true` below this file's `using IDRefine`, where
  Object#== answers :refeq, and so printed the same on every line, CRuby included; and a
  probe that created one Array with a singleton class before warming up, after which the
  JIT declined every Array store in the process. Each printed exactly what CRuby printed.
  A differential case is evidence only once `IR_JIT_VERBOSE=1` shows the shape compiled
  and the probe is seen to reach the code it is about; where one odd receiver invalidates
  the compiled code for the rest of the process, it is one probe per process.
- **Five core/string and core/array examples failed on the candidate only** in one run:
  mspec had been started outside a checkout for it, without `default.mspec`. The baseline
  fails the same five from the same directory.

Not from this work, found on the way and still open: a self-recursive method with a
logged Hash store slows down about 1000x past 65,536 levels of recursion (the shared
`JitUndoLog` fills, the outer entry deopts, and `OnDeopt` never retires it because no
generic call has completed); `NilClass#nil?` removed still answers true; `equal?` is true
for equal Bignums and Floats; `local_variables` lists block-locals before parameters;
backtrace lines of multi-line statements point at the statement's first line;
`method(:binding).call` works where CRuby refuses; a thread dying of SystemStackError
prints its whole backtrace. From round six: `Enumerable#chunk_while` loses elements once
`nil?` answers something truthy for an Array (`class Object; def nil?; :x; end; end;
[[1],[1]].chunk_while { |x, y| x == y }.to_a` is `[[[1]]]`, CRuby `[[[1], [1]]]`): the
prelude's version asks `chunk.nil?`, and `ruby4.rb` asks `.nil?` 278 times, so it will not
be the only method that a redefined `nil?` breaks where CRuby's C does not ask;
`a[2**40] = x` raises RangeError where CRuby raises NoMemoryError;
`Exception_CauseRaisedInClause1` fails in Release on net10.0 and passes on net11.0.
