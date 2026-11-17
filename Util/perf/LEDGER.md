# Performance ledger

What `/perf-measure`, `/perf-step` and `/perf-gate` (`.claude/skills/`) read and write: the
current numbers, the gaps worth closing, and what was tried for each. Newest facts replace
old ones; the history is in git.

## Current numbers

2026-11-16, f37be3b1 (master after #216 to #227: round ten, the perf areas, the
source-generated initializers, `-X:JITCalls`, `each_index`), Release net10.0, 4 cores (Xeon,
2.10 GHz), `git status` clean. Load average 2.96 when the AWFY run began (the build had
just ended), 1.05 when it ended, 1.0 through the micro table. The container was restarted
again before this measurement and is a different machine from round ten's: CRuby's NBody
1.93 s then, 1.56 s now, and CRuby's short calls gained more than IronRuby's did (below).
CRuby 4.0.9 (no YJIT), TruffleRuby 40.0.0 (Oracle GraalVM Native). The machine's speed
differs between sessions by tens of percent, so compare ratios, not microseconds, and
ratios across sessions only with a build of the older tree beside them.

Are-We-Fast-Yet, all thirteen, steady state, µs per iteration (lower is better), one run;
the last column is the ratio recorded for round ten (a6b1c629, another machine):

| benchmark | CRuby | IronRuby | TruffleRuby | IronRuby/CRuby | round ten |
|---|---:|---:|---:|---:|---:|
| DeltaBlue | 40257 | 49579 | 1821 | 1.23 | 1.18 |
| Permute | 113028 | 130404 | 6289 | 1.15 | 1.16 |
| Richards | 334877 | 340521 | 14810 | 1.02 | 1.20 |
| Json | 80796 | 76531 | 7334 | 0.95 | 0.90 |
| Queens | 91887 | 82354 | 6239 | 0.90 | 0.90 |
| Bounce | 93258 | 82912 | 2214 | 0.89 | 0.92 |
| Towers | 111587 | 97759 | 4746 | 0.88 | 0.96 |
| Sieve | 148448 | 122726 | 7002 | 0.83 | 0.84 |
| NBody | 1556841 | 1260939 | 15645 | 0.81 | 0.80 |
| CD | 719615 | 501951 | 38816 | 0.70 | 0.70 |
| Storage | 139119 | 85812 | 16751 | 0.62 | 0.59 |
| List | 98972 | 38077 | 6749 | 0.38 | 0.37 |
| Mandelbrot | 803319 | 48764 | 41664 | 0.06 | 0.14 |

Geomean of the thirteen: IronRuby/CRuby 0.678 (0.730, 0.750 and 0.779 in round ten's three
runs); TruffleRuby/CRuby 0.048. What moved is Mandelbrot: with a build of a6b1c629 beside
master on this machine it is 122.8 -> 48.8 ms (DeltaBlue 51.6 -> 49.8, Richards 332.8 ->
330.0 in the same invocation), from something merged since round ten that was not looked
for; `Integer#<<` and `#>>` specialized in the JIT (9997c5bc) is the likely one, since its
inner loop shifts. Without Mandelbrot the other twelve have a geomean of 0.829 against
round ten's 0.839: level. TruffleRuby's DeltaBlue was 1.8 ms in this run, 12.7-12.9 since
round nine and 3.6 in round seven's. On the tree of round nine the geomean was 0.785,
0.800 and 0.768; on the tree of round seven 0.804 and 0.787 (NBody
1.21 and 1.08, CD 0.70 and 0.66), and on the
tree of round five, at the start of the session, it was 0.872 (Permute 1.49, Richards
1.33, CD, Json and DeltaBlue 1.17, NBody 1.07): the first time the eight benchmarks
beyond the original five were run at all.

The five measured from the start, IronRuby/CRuby; "now" is the run above, the other
columns are what was recorded when each round landed, in other sessions:

| benchmark | now | r10 | r9 | r7 | r6 | r5 | r4 | r3 | r2 | r1 | start |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| DeltaBlue | 1.23 | 1.18, 1.22, 1.16 | 1.18, 1.22, 1.25 | 1.33, 1.26 | 1.28 | 1.23 | 1.41 | 1.56 | 1.64 | 1.95 | 2.23 |
| Richards | 1.02 | 1.20, 1.08, 1.29 | 1.11, 1.29, 1.21 | 1.18, 1.19 | 1.15 | 1.16 | 1.19 | 1.42 | 1.48 | 1.61 | 1.94 |
| Json | 0.95 | 0.90, 1.04, 0.97 | 1.02, 0.87, 0.74 | 0.94, 0.87 | 1.02 | 1.13 | 1.19 | 1.20 | 1.28 | 1.56 | 1.64 |
| Bounce | 0.89 | 0.92, 1.06, 1.73 | 0.91, 0.90, 0.92 | 0.73, 0.90 | 0.92 | 0.91 | 0.89 | 1.17 | 1.18 | 1.40 | 1.65 |
| Towers | 0.88 | 0.96, 1.04, 0.94 | 0.99, 0.97, 0.95 | 1.00, 0.93 | 0.94 | 0.99 | 1.02 | 1.27 | 1.28 | 1.57 | 1.90 |
| geomean | 0.99 | 1.02, 1.09, 1.19 | 1.04, 1.04, 1.00 | 1.02, 1.02 | 1.05 | 1.08 | 1.13 | 1.32 | 1.36 | 1.61 | 1.86 |

Rounds seven to ten changed none of these five on purpose (Json's allocation fell in
round nine); what they did is in the direct comparisons of the trees under "Landed" (CD
and Permute in round seven, NBody, CD and Json in round nine, List in round ten).

Round thirteen (2026-11-16, fd13114a, after a restart of the container; load 0.9-1.1):
direct calls between compiled methods are on by default, and of the thirteen only List
moves. One run of all thirteen, CRuby / IronRuby / IronRuby with `-X:NoJITCalls`, us:
DeltaBlue 41713 / 52406 / 54565, Richards 312449 / 367417 / 339484 (the same in ten
alternating processes of each, see Landed), Json 87081 / 77069 / 74644, CD 729060 /
465810 / 491252, Bounce 88910 / 81601 / 82789, List 98116 / 27677 / 38776, Mandelbrot
801788 / 49336 / 50309, NBody 1581301 / 1249594 / 1260251, Permute 109624 / 143146 /
137417, Queens 95590 / 85373 / 81990, Sieve 157016 / 124852 / 122915, Storage 139218 /
86894 / 89555, Towers 107800 / 101096 / 99371. Geomean IronRuby/CRuby 0.672, with
`-X:NoJITCalls` 0.687; List 0.28 of CRuby, from 0.38-0.40.

Round fourteen (2026-11-17, 80813950; another host again, slower: CRuby's NBody 2.11 s;
load 1.2-1.9): nested block loops and three core operations in typed bodies. One run of
all fourteen (`awfy.rb 12 --whole`), CRuby / IronRuby / TruffleRuby, us, and
IronRuby/CRuby: DeltaBlue 46689 / 54633 / 9425, 1.17; Richards 337929 / 411619 / 17035,
1.22; Json 93255 / 85816 / 7648, 0.92; CD 807667 / 544757 / 35712, 0.67; Bounce 98770 /
94401 / 2482, 0.96; List 114517 / 30928 / 7647, 0.27; Mandelbrot 1110815 / 59279 / 53393,
0.05; NBody 2109390 / 828860 / 17734, 0.39; Permute 128164 / 157725 / 6831, 1.23; Queens
105279 / 97781 / 7119, 0.93; Sieve 175409 / 149600 / 8061, 0.85; Storage 175170 / 100244
/ 17728, 0.57; Towers 127484 / 115948 / 5321, 0.91; Havlak FAIL (CRuby 4.0 cannot run
it) / 3741379 / 151728. Geomean of the thirteen CRuby runs: IronRuby/CRuby 0.629 (0.672
in round thirteen), TruffleRuby/CRuby 0.051. NBody is at 0.39 of CRuby, from 0.79.
Havlak, measured for the first time, is 25 times TruffleRuby's time, in the same band
as the others (5 for DeltaBlue's cold run to 47 for NBody); nobody has looked at where
its time goes.

Round fifteen (2026-11-17, ea86fa35; load 1.0-1.2): a constant whose value is nil in the
method JIT, and loops that see a redefinition while they run. Neither is aimed at a benchmark and none moved: all fourteen
(`awfy.rb 12 --whole`, the tree before / after, both column orders) 0.994 and 1.007, so
the table above stands. IronRuby alone, us, before / after in the first run: DeltaBlue
48841 / 49797, Richards 346862 / 352748, Json 74782 / 72318, CD 472757 / 503289 (1.021
in eight alternating processes), Bounce 81700 / 85563 (0.997 in eight), List 27535 /
27094, Mandelbrot 48955 / 49113, NBody 688502 / 679864, Permute 135807 / 133501, Queens
82300 / 83109, Sieve 136331 / 129789, Storage 92000 / 88190, Towers 98740 / 91177, Havlak
3878371 / 3902921.

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

Per operation (`Util/perf/micro.rb`), ns, one run of `micro-table.rb` on f37be3b1; a line
moves by 15-20% from run to run on this machine, so a ratio is good to about that. Against
round ten's table IronRuby's nanoseconds fell on every line and several ratios still rose
by 10-19% (`ivar_rw` 1.47 -> 1.63, `getter` 1.20 -> 1.37, `each4` 1.04 -> 1.22, `short4`
1.06 -> 1.26, in a second run too): that is this machine, not the tree. A build of
a6b1c629 beside master (`ab.rb`, four rounds, twelve lines) has the same ratios to CRuby
(`ivar_rw` 1.43, `getter` 1.25, `each4` 1.25) and master at 0.95-1.07 of it, every row
marked `overlap`; seven of the twelve lean 5-7% slower on master, which four rounds do
not show to be a difference and which the next measurement should look at again:

| op | CRuby | IronRuby | ratio | start |
|---|---:|---:|---:|---:|
| empty | 19.9 | 22.3 | 1.12 | 1.23 |
| ivar_rw | 33.0 | 53.8 | 1.63 | 2.72 |
| getter | 23.7 | 32.4 | 1.37 | 3.17 |
| attr | 17.1 | 3.9 | 0.23 | 0.80 |
| cmp | 24.6 | 30.3 | 1.23 | 3.58 |
| abs | 29.8 | 52.6 | 1.77 | 3.07 |
| arr | 35.0 | 42.0 | 1.20 | 1.82 |
| nil? | 23.5 | 34.0 | 1.45 | 3.48 |
| == | 24.6 | 27.8 | 1.13 | 3.35 |
| yield | 44.7 | 54.5 | 1.22 | 2.57 |
| each4 | 189.6 | 231.8 | 1.22 | 1.87 |
| while10 | 187.7 | 40.7 | 0.22 | 0.23 |
| new2 | 91.7 | 74.1 | 0.81 | 1.47 |
| symcase | 29.5 | 28.9 | 0.98 | 1.03 |
| str== | 100.0 | 49.4 | 0.49 | 1.44 |
| poly2 | 54.5 | 36.1 | 0.66 | 2.11 |
| blockeach | 57.8 | 50.6 | 0.88 | 2.33 |
| lit== | 201.3 | 59.1 | 0.29 | 0.94 |
| swap | 62.2 | 58.4 | 0.94 | 1.35 |
| locattr | 61.0 | 42.7 | 0.70 | 1.38 |
| sqrtw | 44.7 | 2.5 | 0.06 | 1.34 |
| rangeeach | 46.3 | 1.8 | 0.04 | 0.87 |
| blkattr | 77.2 | 61.9 | 0.80 | 1.12 |
| selfcall | 89.4 | 31.7 | 0.35 | 0.99 |
| veach1 | 181.3 | 317.9 | 1.75 | 1.59 |
| veach2 | 229.7 | 362.5 | 1.58 | 1.37 |
| veach4 | 367.7 | 509.3 | 1.39 | 1.38 |
| veach100 | 5135.0 | 5609.6 | 1.09 | 1.04 |
| rangelit | 158.4 | 88.4 | 0.56 | 0.87 |
| short4 | 285.4 | 359.4 | 1.26 | 1.41 |
| s_each1 | 76.6 | 169.0 | 2.21 | 2.11 |
| s_times1 | 91.7 | 194.5 | 2.12 | 2.07 |
| s_map1 | 107.4 | 180.5 | 1.68 | 1.93 |
| s_any1 | 69.0 | 164.3 | 2.38 | 6.15 |
| s_each2 | 112.7 | 194.7 | 1.73 | 1.70 |
| s_times2 | 125.9 | 204.6 | 1.63 | 1.57 |
| s_map2 | 140.8 | 211.1 | 1.50 | 1.29 |
| s_any2 | 98.5 | 180.4 | 1.83 | 4.54 |
| s_each4 | 186.5 | 234.0 | 1.25 | 1.25 |
| s_times4 | 187.7 | 244.2 | 1.30 | 1.27 |
| s_map4 | 242.5 | 245.1 | 1.01 | 1.05 |
| s_any4 | 160.7 | 216.1 | 1.34 | 3.09 |
| listwalk | 141.6 | 67.7 | 0.48 | 0.71 |
| treefind | 1244.7 | 501.1 | 0.40 | 0.60 |

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
   benchmark; `abs` 1.77 and 1.35 in two runs, `ivar_rw` 1.63, `nil?` 1.45, `getter` 1.37,
   `yield` 1.22 and 1.40). Richards' `run_task`
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
   `hash_slot_for`; in DeltaBlue `Dictionary#at` at `match`; in CD `RedBlackTree#get`
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
   Untried, and the largest thing left here (`Util/bench/daloze-lessons.md`, idea 1): an
   instance variable that holds a Float kept unboxed. Every Float written to `@x` is a
   box today (`RubyInstanceData._ivars` is one `object[]`), and NBody's remaining 602 MB
   per iteration is mostly those. The paper on thread-safe objects gives the rule that
   makes it safe without locking reads - a storage location belongs to one pair of
   variable and type and is never reused, a type change takes a new location - which
   fits the lock-free move protocol the instance variables already have.
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
9. **Light frames still exclude** `super` (6 hot `initialize` methods), `rescue`,
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
    Round fifteen landed it after all (perf/core2, see Landed), for its own shape and not
    for Richards: `NIL_CONST == x` in a small method is 0.63-0.66 of before. Closed.
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
    `Array#dig(0, 1)` 15.1, `sum { }` 6.5, `Kernel#String` 6.2, `Array#sum` 5.3, `to_h { }`
    4.5, `(1..4).sum` 4.0, `Kernel#tap` 3.9, `min_by` 3.6, `Hash#sum { }` 3.6, `then` 3.2,
    `take_while` 3.0, `uniq` 2.9, `flat_map` 2.7, `filter_map` 2.4, `tally` 2.1; and
    `String#strip`/`lstrip`/`rstrip` cost 145 ns over the C# method they wrap. `inject`
    and `reduce` are done (2.0 -> 1.3, `inject(:+)` 6.7 -> 4.0). One method per run of
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
19. **`Object.new.extend(M)` is 200 to 300 times CRuby's time and grows with the number
    of objects extended**: 20,000 in a `while` loop take 4.5 s, 40,000 11.0 s, 200,000
    72 s, with and without the JIT and OSR; CRuby takes 0.23 s for 200,000 (1.1 us each).
    `Struct.new(:a)` is 105-115 us each against 3.6 (300,000: 32-35 s against 1.1 s),
    `Class.new { def x; 3; end }` 15 us against 0.7. Found in round fifteen by a probe
    written for something else (a loop whose own body changes method tables); nobody has
    looked. First guess, unverified: the module's list of dependent classes takes a weak
    reference per singleton class ever made and is scanned on each update. Outside
    AWFY, but DCI-style code and anything that extends per request pays it.
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
    entry points are in `Util/bench/daloze-lessons.md`, section 6. Reading the code did
    not explain the first two; the experiment starts with finding out (a one-character
    needle straight to a span search, `match?` without a Match object). Outside AWFY;
    it is what JSON escaping, `strscan` and log parsing spend.

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
