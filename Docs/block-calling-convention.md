# A block that is not a Proc until it escapes: design

Status: design (round thirty-seven, perf/bcc); stage 0, the attribution, done in perf/stage0 - see
"Stage 0: measured" below, which replaces the arithmetic bounds of section 4. It follows `Docs/light-frame-literal-blocks.md`,
whose three experiments removed the candidates one by one: the full call site costs nothing
(perf/literalsite), publishing the frame's scope costs nothing (perf/procscope), and allocating the
scope and the Proc costs what reusing them does (`IR_PARKED_SCOPES=0` leaves every time as it is). What
is left is the work of the block protocol itself. This document says what that work is, what CRuby does
instead, a representation that could skip it, and stages that can each be built and kept on their own.
**Stage 0 is a measurement**, because the gain of every later stage depends on which steps cost what,
which this host cannot show (no `perf`; callgrind does not name JIT code).

## 1. The cost model

### Measured (round thirty-seven, this host, Release net10.0, ns and bytes per call of the caller)

The caller is a light method; `ny` is `def ny; yield; end`, `keep` is `def keep(&b) = b`, `pass` is
`def pass(&b) = ny(&b)`. Bytes are the thread's allocated bytes; 22 B of each line is the driver's
(`nb0`, a call without a block, has them too).

| shape | CRuby ns | IronRuby ns | bytes | parking off: ns, bytes | light frames off: ns, bytes |
|---|---|---|---|---|---|
| `nb0`: `nb` | 36 | 59 | 22 | 37, 22 | 30, 22 |
| `ny1`: `ny { 1 }` | 50-53 | 160-161 | 70 | 169, 358 | 185, 518 |
| `nys`: `s = 0; ny { s += 1 }; s` | 63 | 158 | 54 | 174, 342 | 184, 502 |
| `e1`: `a.each { \|x\| }` over [1] | 73-75 | 142-145 | 70 | 161, 358 | 136, 70 |
| `e1s`: `s = 0; a.each { \|x\| s += x }; s` | 81 | 147 | 54 | 161, 342 | 142, 54 |
| `pon`: `pass { 1 }` (passed on) | 85 | 195 | 358 | 194, 358 | 254, 702 |
| `cv`: `keep { 1 }.call` (converted) | 239 | 172 | 358 | 170, 358 | 205, 542 |

What it says:

- **A literal block costs IronRuby about 100 ns per call against CRuby's 15-40.** `ny { 1 }` is 160
  against 50; the yield itself is about 11 ns (perf/yieldloop), as CRuby's is.
- **The bytes are not the time.** With parking, a call allocates 48 bytes beyond the driver; without it
  336, and the time moves by 10 ns or less. Light frames off (a scope at entry) adds 160 bytes and
  ~25 ns to the Ruby-callee shapes. The ~290 bytes a literal used to cost, by the classes' fields (not
  measured one by one): the method scope (~140), the Proc (~80), its BlockParam (~48), the locals tuple
  (~24).
- **Passing a block on is never parked** (`pon`: 358 bytes and 195 ns, parking on or off): the callee
  that names its block (`&b`) converts the Proc as a method's (`ConvertAsMethodFrame`), which is not
  reusable.
- **Converting is already cheaper than CRuby** (`cv` 172 against 239): CRuby makes its Proc and moves
  the frame's environment to the heap then (`vm_make_env_object`); IronRuby already has both.

### What IronRuby does per call of `ny { 1 }` from a light method (read from the code)

| step | where | what |
|---|---|---|
| 1 | caller | the scope made on demand: `TakeParkedScope` (an exchange) and `ReuseParkedScopeLazy` (a dozen fields), or `CreateMethodScopeLazy`; the locals tuple made at entry (`MakeLocalsStorage`: a body with a literal keeps its locals in a heap tuple) |
| 2 | caller | the Proc: `InstantiateParkedBlock` (the parked Proc taken, `ReuseParked`, `TryReuse`), or `new Proc` |
| 3 | caller | the call through the light site, the Proc its block argument (perf/literalsite) |
| 4 | callee | `CreateRfcForMethod(proc)`: the Proc becomes the callee's flow-control frame (`ConvertAsFrame`: kind, the reusable flag, `_activeFlowControlScope`) - it is what a `break` in the block targets |
| 5 | callee | `yield`: `ReuseBfcForYield` (the Proc's `YieldBlockParam` reset), `RubyOps.Yield0`/`Yield1` -> the dispatcher -> the block's compiled lambda |
| 6 | block | entry: `EnterScopeFreeBlock` or `TakeReusableBlockScope`/`CreateReusableBlockScope` (a block that reads an enclosing local reaches it through `Proc.LocalScope`) |
| 7 | callee | after the yield: `LightMethodYield` (the BlockParam's return reason: break?) |
| 8 | callee | exit: `LeaveLightMethodFrame(rfc, scope)`: the Proc is an active rfc, so the slow path, `LeaveMethodFrame` |
| 9 | caller | exit: `BeginPark` / `StageParkedProc` / `EndPark` (an exchange, `ClearForPark`, `ClearCallState`) |

### What CRuby does

The caller's control frame holds a captured block, `rb_captured_block { self, ep, code.iseq }`, written by
`send` with no allocation; the callee receives a *block handler*, that pointer tagged as an iseq block
(`VM_BH_FROM_ISEQ_BLOCK`). `yield` (`vm_invoke_iseq_block`) pushes a frame for the block whose `ep` is the
captured one, so the block reads the caller's locals in the caller's own stack frame. `break` finds its
target by the captured `ep`. Nothing is on the heap until something asks for a Proc (`&b`, `proc`,
`binding`): `rb_vm_make_proc` then moves the environment to the heap (`vm_make_env_object`) and every
frame that shares it follows. Steps 1, 2, 4, 8 and 9 above have no counterpart; 5-7 are a frame push.

## 2. The representation

A **block descriptor**: what a callee needs to yield to a literal, and nothing it needs only when the
block escapes.

    sealed class BlockDescriptor : RuntimeFlowControl   // it is the callee's flow-control frame, as the Proc is now
        object Self
        BlockDispatcher Dispatcher                       // the literal's code
        MutableTuple Locals                              // the caller frame's tuple (or null: a narrow literal)
        FrameHeader Frame                                // the caller's rfc, declaring scope, module, shape, block, callee name
        Proc Materialized                                // the Proc made from it, once something asked

- **One per (literal, frame), reused across calls of the frame**, as the parked Proc is now, but taken
  with no exchange: a light frame owns its descriptors in CLR locals for the call (the caller is the
  only one that runs the literal), and a recursive or concurrent call of the same method has its own
  frame and its own descriptors. So the per-call work of steps 1, 2 and 9 becomes: fill four fields.
- **The scope is made from it on demand** - `binding`, `eval`, `local_variables`, a TracePoint, a callee
  that answers LightScopeRequired, `$~` - from `Frame` and `Locals`, exactly as the light frame's lazy
  scope is made today, and kept on the descriptor (one scope per invocation: the frame's own lazy scope
  takes the descriptor's if it exists).
- **The Proc is made from it on demand** (`Materialize`): when a callee converts the block (`&b` in a
  Ruby callee, `Proc.new`, `define_method`, `proc`, `lambda`, a C# method whose block parameter is a
  `Proc` and not a `BlockParam`), or stores it. The Proc's scope is the descriptor's scope, made then;
  from then on the descriptor answers with that Proc and is not reused by the frame's next call (it
  escaped). That is CRuby's `rb_vm_make_proc`.

What each kind of callee does with it:

| callee | today | with a descriptor |
|---|---|---|
| a library `[BlockNotKept]` iterator (Array#each, Integer#times...) | `CreateBfcForBlockNotKept(proc)`: the Proc's reused BlockParam | a BlockParam over the descriptor (its `Proc` property materializes); the yield goes to `Dispatcher` with `Self` and the descriptor as the block's parent frame |
| a Ruby method that only yields (`def ny; yield; end`, a light frame) | step 4, 5, 7, 8 | the descriptor is its rfc; `yield` calls `Dispatcher` directly; `LightMethodYield` reads the descriptor's flow state |
| a Ruby method that passes it on (`def pass(&b) = ny(&b)`) | converted as a method's: never reused, 358 bytes | stage (c): a `&b` that is only passed on passes the descriptor (the light frame's block parameter is a descriptor until something reads `b` otherwise) |
| a method that converts it (`&b` read, `Proc.new`, `define_method`) | the Proc | `Materialize` |

## 3. Every way it can escape or be observed

| how | what must happen |
|---|---|
| `&b` read as an object, stored, returned, `b.call` | `Materialize`: the Proc, over the scope made from the frame now; the descriptor is spent |
| `Proc.new`/`proc`/`lambda` with no block of their own, `define_method`, `instance_exec(&b)` | the same, through the BlockParam's `Proc` |
| `binding` in the block, `eval`, `local_variables`, `__method__` | the block's scope is made with the descriptor's scope as its parent, made now from `Frame` and `Locals` |
| `binding`/`eval` in the caller after the call | the caller's lazy scope is the descriptor's if one was made (one scope per invocation) |
| a TracePoint `:b_call`/`:line` in the block, `binding.local_variables` | as `binding` in the block |
| `$~`, `$_` set by a callee of the block | through LightScopeRequired: the block's scope, with the descriptor's scope as parent |
| `break` | the descriptor is the callee's rfc, as the Proc is now (`SetFlowControl(Break, converter)`); after the frame returned, `LocalJumpError` as today (the frame header says the method has left) |
| `return` in the block | not in the first stages (the narrow shape has none) |
| `next`, `redo`, raise, throw | the block's own; nothing reaches the descriptor |
| a Fiber suspended in the block, `Enumerator#next` | the descriptor stays in use until the callee returns; the frame does not reuse it while its call is in flight (it owns it for the call) |
| another thread running the block (a callee that hands it to `Thread.new`) | that is a conversion (`Thread.new` takes a Proc): materialized |
| re-entrancy: the block calls the caller's method again | a new frame, its own descriptors |
| `yield` after the callee returned (a Proc kept and called) | only a materialized Proc can be kept; its scope's frame header says the method left |
| `block_given?` in the callee | true: the descriptor is a block |
| `Proc#==`, `object_id` of the block | only on a materialized Proc |

## Stage 0: measured (perf/stage0)

`Src/IronRuby.Tests/Perf/BlockCostHarness.cs`, run as `IronRuby.Tests /blockcost [N]` (not a test: the
driver runs it only for that switch). It hosts a runtime, takes real objects from Ruby code - a literal's
compiled block dispatcher, a method scope, its declaring scope and module - and times the runtime calls the
compiled code makes, each alone and then together, Stopwatch over 3,000,000 iterations, best of five after
a warm-up, Release net10.0; then the same calls end to end through Ruby. Two runs agree to a few ns.

| step (ns per call) | alone |
|---|---|
| the locals tuple (`MakeLocalsStorage`) | 6 |
| a scope made (`CreateMethodScopeLazy`, allocation and its collection) | 45-55 |
| a Proc made (`new Proc`) | 16-17 |
| **the parked cycle**: take the scope, reinitialize it, make the Proc again, stage, park | **45-48** |
| - take and park with nothing between (`TakeParkedScope`, `BeginPark`, `EndPark`) | 23-25 |
| - plus the scope reinitialized (`ReuseParkedScopeLazy`) | +6 |
| - plus the Proc made again and staged (`InstantiateParkedBlock`, `StageParkedProc`) | +8 |
| - the scope's fields cleared and set again, alone (`ClearCallState`, `Reinitialize`) | 7 |
| - one `Interlocked.CompareExchange`, against a `Volatile.Write` | 10.4 against 2.5 |
| the light callee's entry and exit (`EnterLightMethod`, `LeaveLightMethodFrame`) | 3 |
| a yield to `{ 1 }` (`ReuseBfcForYield`, `Yield0`, the block, `LightMethodYield`) | 15-17 |
| a `[BlockNotKept]` yield to `{ \|x\| }` (`CreateBfcForBlockNotKept`, `Yield`, `LeaveProcConverter`) | 15-17 |
| `nyl`, all its steps together (parked on every call) | 68-73 |
| `e1`, all its steps together | 52-53 |

End to end, ns per call with the caller's loop: `nb0` (a call without a block) 42-53, `nyp` (`ny(&PY)`, a stored
Proc) 98-101, `nyl` (`ny { 1 }`) 148-155; `e0` (no call) 24-26, `ep` (`a.each(&PE)`) 86-92, `e1` (`a.each { |x| }`)
133-145. So:

- **The literal itself costs 50-54 ns** (`nyl` - `nyp`, `e1` - `ep`), and the parked cycle plus the locals
  tuple is 52: **the literal's cost is the parked cycle, entirely.** Of it, about 16 ns are its two atomics
  (the take's and the park's), 6 the scope's reinitialization, 8 the Proc made again, 6 the tuple, and the
  rest the cycle's checks and clearing.
- **Passing any block and yielding to it costs another 55-63 ns** (`nyp` - `nb0`, `ep` - `e0`): the yield 16,
  the callee's conversion 3, and 35-45 ns in the compiled code around them - the call site with a block, the
  library rule's flow-control frame, the light frames' prologues and epilogues - which no runtime call
  stands for and the harness cannot split further. CRuby's whole `ny { 1 }` is 14 ns over its `nb`.
- **The sums hold**: `nyl`'s steps (68-73) are its end to end minus `nb0` (about 100) less those 35 ns; `e1`'s
  (52) are its end to end minus `e0` less the 60 of calling `each` with a block at all.

### The stages, by these numbers

| stage | what it removes | measured bound per call | of |
|---|---|---|---|
| A: `[BlockNotKept]` iterators, narrow literals | the parked cycle and the tuple for those calls | up to ~50 ns (`e1` 133-145 -> ~85-90, `ep`'s) | CRuby 73 |
| B: Ruby methods that only yield | the parked cycle, and the conversion | up to ~55 ns (`nyl` 148-155 -> ~95-100, `nyp`'s) | CRuby 50 |
| C: pass-on | the 336 bytes and the unparked Proc of every pass-on | not measured here | |
| D: literals that read locals | as A and B for those | as A and B | |
| (none) the call with a block, the yield | 35-63 ns of compiled-code work around a block call | not reached by the convention: gap 1's call sites | |

**Which pays most for one round's work: A.** It needs the descriptor only on the library side (a
`CreateBfcForBlockNotKept` overload, the narrow shape's instantiation in the caller) and takes up to the
same ~50 ns as B, which needs the light callee's entry, yield and `block_given?` to take a descriptor.

**A step cheap to cut by itself, without the new convention: the park's atomic.** `EndPark` publishes the
parked scope with `Interlocked.CompareExchange`; a `Volatile.Write` does the same job (a call that parked in
the meantime loses its scope to the garbage; a taker's exchange still makes what it takes its own) and is
8 ns cheaper per call of every method with a parked literal - about 15% of the literal's cost. perf/iter3
tried exactly this and saw nothing end to end, where the host's noise was larger than 8 ns; the harness
shows it. It is a round of its own (the take must stay atomic: two threads may reach for the same parked
scope).

### Stage A, simulated before it was built (perf/stagea)

The harness times `e1`'s call as stage A would make it (the rows "e1: all steps (descriptor, ...)"): the
Proc taken from its slot by the thread that owns it, with no atomic, its scope never made, a frame header
filled for the scope to be made from on demand, the yield, and the Proc given back. ns per call, two runs:

| `e1`'s steps | ns | bytes |
|---|---|---|
| parked, as now (after perf/endpark) | 40-44 | 24 (the tuple) |
| descriptor, the whole header stored per call | 57-62 | 24 |
| descriptor, only the fields that change per call stored (the tuple, the rfc) | 41-47 | 24 |
| descriptor, no header (not sound: a scope made on demand could not be made) | 29-33 | 0 |

**Stage A does not pay.** Filling the header is a set of reference stores into a heap object, each a GC
write barrier, which is what the parked scope's reinitialization costs too; the stage-0 bound took the fill
as free. The tuple cannot be dropped (`Locals = null` above): a `send(:binding)` in a narrow literal shows the
method's locals, in CRuby too. And the end to end does not sit in the runtime steps alone: `ept` (`ep` with a
narrow literal in a branch never taken) is 100-102 ns against `ep`'s 90-92 and `e1`'s 145-151, so having a
literal costs the frame 10 ns and evaluating it 45-49, of which the steps a descriptor would replace are about
29. Stages B and D carry the same fill. What would pay is the block reaching the caller's state in place, as
CRuby's captured `ep` does, which objects passed through call sites cannot do.

## 4. Stages

Each behind a switch of its own, with its matrix before its code, keepable alone.

**Stage 0 - attribution (no runtime change).** Where do the ~100 ns of `ny { 1 }` and the ~70 of
`a.each { |x| }` go among steps 1-9? A `perf` profile on a host that has it (`Util/perf/profile.sh` with
a shape driver), or the steps timed one by one in C# (a test harness that runs the emitted sequence with
each step replaced by a no-op that keeps its result). The table of steps above is the checklist. Expected
outcome: an upper bound for each stage below; build only the stages whose steps carry the time.

**Stage A - `[BlockNotKept]` library iterators, narrow literals, light callers.** The literal of the
narrow shape (`MethodDefinition.HasOnlyNarrowLiterals`) given to a `[BlockNotKept]` library method is
passed as a descriptor with `Locals = null` (the literal reads no local) and `Frame` the light frame's
header; `CreateBfcForBlockNotKept` gains an overload taking it. Removes steps 1, 2 and 9 for those calls
(the scope is never made unless escaped). Upper bound: `e1` 142 -> about `nb0` + the iterator + the
yield, by the table near 90 (CRuby 73). Matrix: `Util/literal-block-light-matrix.rb` sections 1-5 for
Array#each, #map, Integer#times, Hash#each; the escape rows of section 3 above (a `binding` through
`send` in the block, `$~` set through `send`, a TracePoint `:b_call` binding, `break` with an ensure,
Enumerator#next suspended in the block, re-entrancy, four threads); `IR_LIGHT_FRAMES=0`,
`IR_PARKED_SCOPES=0`, the switch off.

**Stage B - Ruby methods that only yield.** A light callee whose block is a descriptor uses it as its rfc
and yields to `Dispatcher` directly (steps 4, 5, 8 lose their Proc work). Needs the light frame's yield
(`YieldCall` in `LightFrameRewriter`) and `LightMethodYield` to take a descriptor, and `block_given?`,
`defined?(yield)` and `super` (which passes the block on) to see one. Upper bound: `ny1` 160 -> about
`nb0` + one yield, near 75 (CRuby 50). Matrix: block-given, block-pass, block-scope-reuse (all three),
block-nest-reuse, light-entry, light-rest, parked-scope, parked-iterator, plus a new row set for a Ruby
callee that yields, passes on, converts, and calls `block_given?`, each with a descriptor.

**Stage C - pass-on.** A light frame whose `&b` is only passed on (`def pass(&b) = ny(&b)`,
`IsScopeFree`'s block pass) keeps a descriptor as a descriptor; any other read of `b` materializes.
Upper bound: `pon` 195 -> near `ny1` after stage B, and its 336 bytes to the driver's. Matrix:
block-pass-matrix, and a pass-on chain of three with a conversion at each depth.

**Stage D - literals that read the frame's locals.** `Locals` the frame's tuple; the block's compiled
code reads its parent tuples from the descriptor instead of `Proc.LocalScope` (BlockDefinition's
`CreateScopeWithParent`). This is the general case (`e1s`, `nys`) and the one where CRuby's frame-pointer
design matters most; it needs the block's lambda to take a parent that is not a scope, which touches
every closure access. Upper bound: `e1s`, `nys` to stage A's and B's numbers.

## 5. What the method JIT and the light frames must change

- **Light frames** (`MethodDefinition.TransformLightFrame`, `LightFrameRewriter`): the literal's
  instantiation becomes the descriptor's fill (stage A on); the lazy scope takes the descriptor's scope
  (one per invocation); the exit drops `BeginPark`/`EndPark` for bodies whose literals are all
  descriptors; a light callee's entry takes a descriptor as its rfc (stage B); its block parameter may be
  a descriptor (stage C), with every read of it that is not a pass-on materializing.
- **Call sites** (`RubyCallAction`, `MethodDispatcher`, the library binder): the block argument's static
  type is `object` (a Proc or a descriptor); rules that bind a `Proc` parameter materialize; rules that
  bind a `BlockParam` take either. `[BlockNotKept]` is what says a library method takes a descriptor.
- **The method JIT** (`Runtime/Jit`): it never wraps a method that uses its block (the screen), so stages A
  and C do not reach it; a typed body that *calls* an iterator with a literal block (stage A's caller)
  bails today at the call with a block (`EmitCall`: "call with a block") unless it is a nested block loop
  in an outlined core iterator (`JitCompiler.Nest.cs`), so nothing changes there either. Stage B makes a
  yielding method's entry take a descriptor, which the JIT's entry stubs (`JitStub`) must pass through
  untouched (they forward `blk`).
- **The block-loop tier** (`BlockLoopSite`, OSR): it outlines a block into a C# iterator's loop from the
  BlockParam's Proc; with a descriptor it reads `Dispatcher`/`Self` from it, and a block-loop copy that
  needs the block's scope asks the descriptor.
- **BlockParam** keeps one shape: it gains a descriptor field and a `Proc` property that materializes;
  `IsLibProcConverter`, `Break`, `ReturnReason`, `MethodYield` read the descriptor where they read the Proc's
  frame.
