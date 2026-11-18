# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-14, the code of a6b1c629 (round ten on top of round nine and of #215 through
9b2936fa), Release net10.0, 4 cores, load 1.0. The container was restarted between rounds
nine and ten and has been slower since (CRuby's NBody 1.61 s before, 1.93 s now), so
this table's microseconds do not compare with round nine's; the ratios do. CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0 (Oracle GraalVM
Native). The machine's speed differs between sessions by tens of percent (CRuby's own
numbers move with it), so compare ratios, not microseconds; and in this session one
tree's figure for one benchmark moved by 10%, sometimes 20%, from one run to the next
(the last column of ratios below is two more runs of the same binaries an hour before).

Are-We-Fast-Yet, all thirteen, steady state, µs per iteration (lower is better); the
microseconds are the last run's:

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | two runs before |
|---|---:|---:|---:|---:|---:|
| Richards | 341414 | 409318 | 17496 | 1.20 | 1.08, 1.29 |
| DeltaBlue | 47759 | 56291 | 12896 | 1.18 | 1.22, 1.16 |
| Permute | 133041 | 154267 | 7039 | 1.16 | 1.07, 1.22 |
| Towers | 128759 | 123816 | 5352 | 0.96 | 1.04, 0.94 |
| Bounce | 102599 | 93906 | 2528 | 0.92 | 1.06, 1.73 |
| Json | 95510 | 86375 | 7709 | 0.90 | 1.04, 0.97 |
| Queens | 107845 | 97480 | 7330 | 0.90 | 0.92, 0.86 |
| Sieve | 170274 | 142517 | 8240 | 0.84 | 0.82, 0.86 |
| NBody | 1928277 | 1551819 | 18804 | 0.80 | 0.68, 0.81 |
| CD | 852830 | 601192 | 36126 | 0.70 | 0.65, 0.71 |
| Storage | 179481 | 105347 | 18668 | 0.59 | 0.86, 0.58 |
| List | 119618 | 44220 | 7720 | 0.37 | 0.40, 0.39 |
| Mandelbrot | 1124512 | 154915 | 54940 | 0.14 | 0.13, 0.14 |

Geomean of the thirteen: IronRuby/CRuby 0.730, and 0.750 and 0.779 in the two runs
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

The five measured from the start, IronRuby/CRuby; "now" is the three runs above, the
other columns are what was recorded when each round landed, in other sessions:

| benchmark | now | r9 | r7 | r6 | r5 | r4 | r3 | r2 | r1 | start |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 1.18, 1.22, 1.16 | 1.18, 1.22, 1.25 | 1.33, 1.26 | 1.28 | 1.23 | 1.41 | 1.56 | 1.64 | 1.95 | 2.23 |
| Richards | 1.20, 1.08, 1.29 | 1.11, 1.29, 1.21 | 1.18, 1.19 | 1.15 | 1.16 | 1.19 | 1.42 | 1.48 | 1.61 | 1.94 |
| Json | 0.90, 1.04, 0.97 | 1.02, 0.87, 0.74 | 0.94, 0.87 | 1.02 | 1.13 | 1.19 | 1.20 | 1.28 | 1.56 | 1.64 |
| Bounce | 0.92, 1.06, 1.73 | 0.91, 0.90, 0.92 | 0.73, 0.90 | 0.92 | 0.91 | 0.89 | 1.17 | 1.18 | 1.40 | 1.65 |
| Towers | 0.96, 1.04, 0.94 | 0.99, 0.97, 0.95 | 1.00, 0.93 | 0.94 | 0.99 | 1.02 | 1.27 | 1.28 | 1.57 | 1.90 |
| geomean | 1.02, 1.09, 1.19 | 1.04, 1.04, 1.00 | 1.02, 1.02 | 1.05 | 1.08 | 1.13 | 1.32 | 1.36 | 1.61 | 1.86 |

Rounds seven to ten changed none of these five on purpose (Json's allocation fell in
round nine); what they did is in the direct comparisons of the trees under "Landed" (CD
and Permute in round seven, NBody, CD and Json in round nine, List in round ten).

Bytes allocated per iteration (`Util/perf/alloc.rb`; it moves by about 0.3% between
invocations, unlike the timings), MB, round six -> round seven -> now: DeltaBlue 24.9 ->
24.2 -> 24.6, Richards 19.6 -> 19.3 -> 13.9, Json 35.8 -> 35.9 -> 30.7, CD 516.2 -> 226.2 ->
226.1, Bounce 11.0 -> 11.0 -> 11.0, List 8.2 -> 8.2 -> 8.2, Mandelbrot 94.7 -> 94.5 -> 94.5,
NBody 2892 -> 2892 -> 1364, Permute 124.6 -> 124.5 -> 124.6, Queens 41.2 -> 41.3 -> 41.3,
Sieve 98.8 -> 98.8 -> 98.8, Storage 112.6 -> 112.6 -> 112.6, Towers 0.13 -> 0.13 -> 0.13
(Towers reads anything from 0.07 to 0.26 from one invocation to the next, on every tree).
Round seven's change is CD's; round nine's are NBody's and Richards' (short runs in the
compiled copy, where Floats and the block's frame are not allocated) and Json's (a Range
literal of two Integers without the `<=>` call and its boxes). Since then: Richards 13.9
-> 3.2 with #215's `!=` binding merged in (measured on the tree before round ten and on
this one; the tree before that merge gave 13.9), and List 8.2 -> 0.6 in round ten
(`is_shorter_than` compiled: its two cursors are no longer read through call sites).
The other eleven are where round nine left them.

Per operation (`Util/perf/micro.rb`), ns, one run of `micro-table.rb` on the tree of
round ten; a line moves by 15-20% from run to run on this machine, so a ratio is good to
about that:

| op | CRuby | IronRuby | ratio | start |
|---|---:|---:|---:|---:|
| empty | 24.5 | 25.2 | 1.03 | 1.23 |
| ivar_rw | 39.7 | 58.3 | 1.47 | 2.72 |
| getter | 28.7 | 34.4 | 1.20 | 3.17 |
| attr | 20.3 | 4.3 | 0.21 | 0.80 |
| cmp | 29.5 | 34.4 | 1.17 | 3.58 |
| abs | 35.7 | 58.4 | 1.64 | 3.07 |
| arr | 43.9 | 46.6 | 1.06 | 1.82 |
| nil? | 26.7 | 38.4 | 1.44 | 3.48 |
| == | 29.6 | 32.6 | 1.10 | 3.35 |
| yield | 42.4 | 65.3 | 1.54 | 2.57 |
| each4 | 264.5 | 275.5 | 1.04 | 1.87 |
| while10 | 212.8 | 43.6 | 0.20 | 0.23 |
| new2 | 104.5 | 86.3 | 0.83 | 1.47 |
| symcase | 36.3 | 34.3 | 0.94 | 1.03 |
| str== | 121.2 | 59.1 | 0.49 | 1.44 |
| poly2 | 62.1 | 43.7 | 0.70 | 2.11 |
| blockeach | 78.2 | 59.4 | 0.76 | 2.33 |
| lit== | 244.8 | 67.7 | 0.28 | 0.94 |
| swap | 70.3 | 67.5 | 0.96 | 1.35 |
| locattr | 73.8 | 47.6 | 0.64 | 1.38 |
| sqrtw | 52.1 | 2.8 | 0.05 | 1.34 |
| rangeeach | 72.9 | 2.1 | 0.03 | 0.87 |
| blkattr | 88.7 | 76.8 | 0.87 | 1.12 |
| selfcall | 114.6 | 35.1 | 0.31 | 0.99 |
| veach1 | 235.2 | 379.6 | 1.61 | 1.59 |
| veach2 | 327.0 | 448.6 | 1.37 | 1.37 |
| veach4 | 504.7 | 592.2 | 1.17 | 1.38 |
| veach100 | 8026.1 | 7087.8 | 0.88 | 1.04 |
| rangelit | 190.2 | 103.0 | 0.54 | 0.87 |
| short4 | 376.1 | 400.3 | 1.06 | 1.41 |
| s_each1 | 87.8 | 193.8 | 2.21 | 2.11 |
| s_times1 | 107.8 | 197.3 | 1.83 | 2.07 |
| s_map1 | 124.1 | 207.3 | 1.67 | 1.93 |
| s_any1 | 79.1 | 187.4 | 2.37 | 6.15 |
| s_each2 | 127.1 | 222.2 | 1.75 | 1.70 |
| s_times2 | 145.7 | 222.0 | 1.52 | 1.57 |
| s_map2 | 169.3 | 239.6 | 1.42 | 1.29 |
| s_any2 | 113.8 | 214.9 | 1.89 | 4.54 |
| s_each4 | 209.5 | 265.1 | 1.27 | 1.25 |
| s_times4 | 216.8 | 272.0 | 1.25 | 1.27 |
| s_map4 | 269.7 | 291.2 | 1.08 | 1.05 |
| s_any4 | 183.2 | 259.9 | 1.42 | 3.09 |
| listwalk | 155.2 | 69.5 | 0.45 | 0.71 |
| treefind | 1501.0 | 571.3 | 0.38 | 0.60 |

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
   compiles `hold_self`. Not implemented.
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
   an empty Hash literal `{}` (found on `Array#to_h`;
   `::Hash.new` is scope-free but calls a redefined Hash.new), `super` (6 hot `initialize`
   methods; also `Array#sum` without a block, 5.3x CRuby, whose rare path is `super`), `rescue`,
   keywords, `$~`, regex literals, and a call that passes the method's block on
   (`inner(&block)`: 94 to 275 ns per wrapper layer, Util/bench/truffleruby-lessons.md).
   `IR_LIGHT_FRAMES=why` lists them. Also a Range per `(a..b).each`, and
   RubyInstanceData + RubyObject as two objects per Ruby object.
   `block_given?` no longer forces the scope (it is answered from the frame's block
   parameter, `IR_LIGHT_BLOCK_GIVEN=0` for the old call) and a rest parameter no longer
   keeps a method out (`IR_LIGHT_REST=0`); Util/block-given-matrix.rb and
   Util/light-rest-matrix.rb are their matrices.

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
12. **One site, four receiver classes, two speeds**: `recv != other` in a loop over four
    plain classes takes about 105 ns or about 280 ns per iteration, the same binary, from
    one process to the next (three of six runs slow on 9f4d44f8). Two and three classes
    do not do it. Unexplained, and outside AWFY, but a factor of 2.6 on a polymorphic
    site is worth an afternoon. Bounce may be the same thing: one tree's steady state
    was 64 ms in one run and 81-83 ms in three others of the same session.
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
    `uniq` 2.9, `filter_map` 2.4, `tally` 2.1; and
    `String#strip`/`lstrip`/`rstrip` cost 145 ns over the C# method they wrap. `inject`
    and `reduce` are done (2.0 -> 1.3, `inject(:+)` 6.7 -> 4.0), and so is `Array#dig`
    (15.1 -> 6.1, see Landed), and `Array#sum` with a block (6.5 -> 3.3), and `String("a")`
    (5.2 -> 2.4). Next: `String(5)`, `String(nil)` - about 1.6 us each against CRuby's 180,
    in `Kernel#__ir_string__`, which asks whether #respond_to? is Kernel's
    (`method(:respond_to?).owner`) and calls #to_s through `bind_call`; CRuby decides that in
    C (rb_check_funcall). Probably needs a C# helper, which is /perf-step's, not /perf-core's.
    `Array#to_h { }` is done too (4.7 -> 2.3), and `min_by`/`max_by` (3.1 -> 2.2), and their count form
    (`min_by(2)` 3.8 -> 1.9). Open beside speed: `min_by(n)`'s tie order is input order,
    MRI's nmin selection orders ties its own way. `take_while`/`drop_while` done (2.6 -> 2.2,
    now Array's own as in MRI), and `flat_map` (2.2 -> 1.7). `uniq` is not this skill's:
    its C# method alone costs what the call does (500 ns against CRuby's 230). One method per run of
    `/perf-core`. `tap` and `then` are already minimal Ruby: what they pay is gap 13.
16. **Wrappers stay out of light frames** because they pass a block or a splat on
    (`inner(&block)`, `inner(*args)`): 36 prelude methods are `(*args, &block)` wrappers, and
    a layer costs 94 to 275 ns. Until that shape is let in, `/perf-core` can only route
    around it. `/perf-frame`; see gap 9 for the other shapes.
17. **The prelude calls its C# helpers as methods**: about 230 private `__x__` methods,
    called 1,928 times in `ruby4.rb` through call sites (8-12 ns each, a rule to bind per
    site at start-up, 0.2-0.4 ms). TruffleRuby's `Primitive.x` is replaced at parse time.
    Untried: a name the compiler turns into a direct static call in prelude files only
    (lessons, section 4, item 4).
18. **A `while ... yield` in Ruby runs at ~100 ns per element** against 9 for a C#
    iterator compiled with its block, and at twice CRuby's time for the same source: the
    loop compiler gives up on `yield` (see the Rejected entry "A block whose body is
    `yield <expr>`"). This is what keeps per-element loops in C#.
19. **`send` with a String name leaves a `Kernel#send` frame in backtraces** that CRuby
    does not show (`[1].send("sum") { raise }`: IronRuby "Array#sum < Kernel#send", CRuby
    "Array#sum < <caller>"); with a Symbol it is right. Found by /perf-core's probes, not a
    speed gap.

## Landed

All four measured alone against 22e3f91 on a quiet machine, then together; the merged tree
gives the same results as 22e3f91 on IronRuby.Tests (one failure, `Exception_CauseRaisedInClause1`,
which fails in Release on 22e3f91 too), ruby/spec language (2935 examples) and core for
the classes touched (12419 examples), and matches CRuby on `Util/perf/hot-shapes-matrix.rb`
with the JIT on and off. Start-up is unchanged (1.44 s for `-e 1`, best of 12, both).

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
- **perf/minby (/perf-core) - `min_by` and `max_by` without a rest Array or a `|*values|`
  block**: `[1, 2, 3, 4].min_by { }` 1339 -> 1032 ns on the merged build (CRuby 448),
  `max_by` 1300 -> 969. Still #each underneath, because CRuby's backtraces show Array#each
  under an Enumerable method; an index loop that dropped that frame was caught by
  Backtrace_LibraryFrames1.
- **perf/toh (/perf-core) - `Array#to_h` as MRI's `rb_ary_to_h`**: an index loop,
  `block_given?` once, each pair through `Array.try_convert`. `[1, 2, 3, 4].to_h { }` 2120 ->
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

How far one benchmark moves by itself, measured in round eight: Bounce's iteration time
wanders between 63 and 88 ms inside one process, in stretches of 25 to 50 iterations,
on every tree; awfy.rb's figure is the median of the second half of about 150
iterations, so it lands on 64 or on 80 depending on where a fast stretch falls (the
baseline was at 64 in both runs of one comparison and at 79-82 in six processes of 120
iterations each). Storage, Towers and DeltaBlue show the same kind of swing. A
difference between two trees on such a benchmark is settled by separate processes of a
fixed length taken alternately (perf-gate, step 4), not by more awfy.rb runs.
