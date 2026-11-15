# A deopt that does not restart the body: design

Status: design only, nothing here is implemented. It answers gap 1 of `Util/perf/LEDGER.md`
("Calls of other methods from hot bodies"): `CallSite.Target` is 24-32% of every benchmark,
and Richards, Towers, DeltaBlue, Bounce and CD's `tree_insert` all wait on one rule of the
method JIT.

## The rule, and why it costs so much

A typed body (`JitCompiler`) has exactly one way out when a speculation fails: it throws
`JitDeoptException`, the entry (`JitStub`, the `Ast.TryCatch` around the typed call) catches
it, takes logged element writes back (`WithUndoLog`), and calls the generic body **from the
top**. That is invisible only while nothing the typed body did is observable, so the compiler
tracks it:

- `_pure` goes false at the first effect: an instance variable write (`EmitAssignment`), an
  attribute or Struct member write on another object (`EmitMethodAttrWrite`,
  `EmitStructMemberWrite`), a construction, a bound callee that is not pure.
- `_canDeopt` going true while `!_pure` sets `_deoptAfterEffect`, and
  `TryCompile` returns null for `_canDeopt && !_pure && (_deoptAfterEffect || _hasLoop || _selfCall)`.
- `_effectNotOnSelf` makes a later write to self count as a deopt (its frozen check), since it is
  no longer "the first write, before anything observable".

Almost every operation can deopt: an attribute read or write checks the receiver's class and
the method version (`JitRuntime.WriteAttr` -> `WriteAttrUncached` -> `Deopt()`), Integer
arithmetic checks overflow, a value from an ivar or a call is unboxed behind `GuardInt`/
`GuardDouble`. So the rule in practice is "one effect, and it must be the last speculative thing
in the body". Gap 11's table lists what that keeps out of Richards: `hold_self` (an ivar
increment, then an attribute write on another object, then an attribute read), `append` (an
attribute write, then a loop of attribute reads), `queue_packet`, `release`, and with them every
caller of those. Rejected `perf/jcall` was the version of calls from compiled code that kept the
rule: a call is an effect, so nothing speculative may follow it, so it bought little.

The fix the ledger names: a guard that fails **after** an effect must not restart the body. It
must continue - either with that one operation done generically, or with the rest of the body
in generic code from that point.

## Two ways to continue

### A. Per-site generic fallback

Each speculative operation after the first effect carries its own slow path: when its guard
fails, it does what the generic body would do for **that operation only**, with the operands
already evaluated, and the typed body goes on. Nothing is restarted, so nothing has to be
invisible.

    // `@current_task.task_holding = true' after `@hold_count += 1'
    recv = <ivar read @current_task>; val = true
    if (!JitRuntime.TryWriteAttr(recv, val, site))        // class, method version, not frozen
        JitRuntime.FallbackCall(site.Generic, scope0, recv, val)   // the source's call, once
    // continues typed

What it handles: an operation whose result the typed code after it can take whatever the generic
operation returns. In practice: a result typed `Obj`, or one nobody reads (an attribute write's
value in statement position, an ivar write). Attribute and Struct member reads and writes,
general method calls (which return `Obj` anyway), `==`/`!=` on objects, `[]`/`[]=` on an object
whose class the profile saw.

What it cannot handle:

- **A typed result.** `n = @count + 1` with `n` typed `long`: an overflow after an effect has a
  Bignum and nowhere to put it. The fallback would have to change the type of everything
  downstream.
- **The world changing under the rest of the body.** A fallback that runs Ruby code (any call;
  an attribute reader on an unexpected class is an arbitrary method) can redefine methods,
  reassign constants, enable a TracePoint. An operation emitted later that is speculative but
  not self-guarding - an inlined `Integer#+`, a folded constant, an inlined pure callee - would
  then go on with the old meaning. The entry checked the method and constant versions once, on
  the way in.

Cost: a slow path per operation (code size, not time: the fast path is a test and a branch),
and every operation after the first effect must re-check what it speculates on.

### B. Resume in generic code (a materialized frame)

The failing guard builds the frame the generic body would have at that point - its scope with
the locals, self, the block - and generic code runs the rest of the method from there. This is
what YJIT's side exits (to the interpreter at the exact PC, with the VM stack rebuilt) and
Truffle's `transferToInterpreterAndInvalidate` (to the AST interpreter, with the frame
materialized) do. Both have an interpreter that can start anywhere. IronRuby's generic body is
a compiled DLR lambda that can only start at its top, so resuming needs a **resumable generic
body**: the method's AST compiled again in generic mode, with the scope as a parameter instead
of made in its prologue and a `switch` at the top that jumps to a label per resume point. It is
built lazily, the first time a method resumes, and is cold code.

Resume points are statement boundaries (of the method's statement list, and of the statements
of `if`/`unless`/`while` bodies in statement position), never the middle of an expression: the
generic body has no operand stack to rebuild. A guard that fails mid-statement resumes at the
**start of its statement**, which is sound when nothing observable happened in that statement
before the guard: the current rule, applied per statement instead of per body. `JitUndoLog`
then takes a mark per statement, not per entry.

What it handles: everything A cannot - typed results, a changed world (check versions after
anything that ran Ruby code, resume when they moved), loops (resume inside the loop body, after
the safe point, which also closes the "caller in flight" window of `Util/bench/jit-methods.md`
for impure bodies, the part it says "is not designed here").

What it costs: a second generic compile of the method (once, and only for methods that resume);
a frame-state map per resume point (which typed variable is which local, in which
representation); jumps into blocks, which the expression compiler and the DLR interpreter allow
only in statement position (to be checked first in stage 3, with one test in each); and a hard
rule that the resumable body and the generic one are the same program.

### The trade-off, and the choice

A is local: each operation's slow path is the operation, so its correctness is checked one
operation at a time against the generic code, and it needs no second compile. It cannot carry a
typed value across a failure or survive a world change for operations that do not check it. B
is general but adds a second generic body that has to stay equal to the first, and resume
bookkeeping that is the same kind of thing three of the ten faults were in.

**Do A first, for operations whose results are objects, with a rule that keeps it sound; add B
for what A declines.** A alone takes `hold_self` and, with general calls, the start of what
`schedule` and `run_task` need; B is what `append`'s loop, `tree_insert`'s mid-body `Node.new`
and the typed arithmetic of Towers' and Bounce's hot methods need.

The soundness rule for A (the "self-guarded tail"): after the first effect, an operation may be
emitted only if it is

1. not speculative at all (a local read, an ivar read, a literal, a branch on a boolean), or
2. self-guarding: its fast path re-checks everything it speculates on - the receiver's class
   **and the global method version** (and, for a folded constant, the constant version) - and
   its slow path is the generic operation on the evaluated operands, with a result the code
   after it takes as `Obj`.

Integer and Float arithmetic, typed comparisons and inlined callees after the first effect are
emitted on `Obj` operands with a per-operation version check (`VersionOk` is a static read and a
compare) and a generic slow path, or the body is declined as today. That keeps A free of world
changes without any resume: an operation that cares checks the world itself.

## The state a continuation needs, and how to get it

A fallback that is a call through a site needs the caller's scope for some callees; a resume
needs it always. What a scope holds, and where each piece comes from in a typed body:

| State | Generic body | Typed body today | How to materialize |
|---|---|---|---|
| Locals | the `MutableTuple` of the method's `MethodFrameShape`, or register locals (`ScopeBuilder.TryUseRegisterLocals`) | typed CLR variables, some declared in a branch's own DLR block | box each live typed variable into the tuple layout of the generic body: `Int` boxed, `Lng` through `NarrowLong`, `Dbl` boxed, `Bool` as `true`/`false`; never assigned -> nil. The generic compile records its shape and tuple type for the stub (it has them only inside `MethodDefinition` today). A local the JIT scoped to a branch that has ended is still a Ruby local with its value: such locals are hoisted to function scope in bodies with continuations. |
| Self | the scope's self | `_self` | as is |
| The method's block | the scope's block parameter | **dropped**: the entry's `GenericCall` passes `blk`, the typed call does not | the typed body takes `blk` as a parameter when it has continuations. It goes into the scope through `RubyOps.CreateMethodScopeLazy`, which calls `ClearReusable` on it: whatever asked for the scope may keep the block (fault: `def capture(&b); b; end`). |
| Class for visibility and refinements | the scope's self, its declaring module, the declaring scope's refinement activation | the site the JIT made the call through has none | the parent of the materialized scope is the declaring scope (as `LightFrame.MakeScope` does); a fallback call goes through a site bound with `RubyCallFlags.HasLightScope` and the declaring scope, which returns `RubyOps.LightScopeRequired` for a protected or missing method on a receiver, a refined method, or a library method that takes the scope - and is then made again through the full site with the materialized scope (`LightFrameRewriter.LightScopeCall`, verbatim). Never a site made with `RubyCallSignature.Simple`: a site without a scope sees private and protected methods (fault, round seven). |
| `$~`, `$_` | the method scope | they keep a method out of light frames (gap 9); a body with continuations is declined if it reads them (to be checked: whether the JIT has any path that does today) | they are written by library methods that take the scope, i.e. only through `LightScopeRequired`. So the scope is materialized **at most once per invocation** and kept in a local (`#scope ?? (#scope = make())`, as `LightFrame.LazyScope`), so that a second callee that reads `$~` sees what the first set, and so that a resume hands the same scope on. |
| Flow-control frame | `RuntimeFlowControl` of the call (a light frame's `#rfc`) | none | made with the scope, as `CreateMethodScopeLazy` takes it. It is what `break` from the method's block and `return` from a block compare with (fault: `d(1) { 9 }` threw MethodUnwinder because `MethodPropagateReturn` compared the target with the scope object only). |
| Frame and line | the generic body's frame, line markers | the entry is hidden, `jit$name` is not a Ruby frame | A: a fallback can raise from inside the typed body, so a body with fallbacks is compiled as a named frame (`CompiledFrameExpression`, as perf/jcall did) and updates its line before each fallback. B: the resume leaves the typed body by throwing, and the resumable body is the frame, so each invocation shows exactly one Ruby frame. |
| Stack depth | `CreateMethodScope` checks it | the entry checks only for self-calls (`StackCheckPlacer`) | every path from a typed body into Ruby code checks it: a fallback call is such a path (four of the ten faults were a missing check). |

Write-back: after a fallback call that materialized the scope, a callee may have changed locals
through it (`binding.local_variable_set`, `eval` on a captured Binding) and may keep it. The
typed copies are then stale. Stage 1 takes this only where no local is read after the call; in
general, a call that materialized the scope is followed by a resume (B) at the next statement,
and the rest of the invocation runs on the tuple. A scope that was made but provably not
captured could copy back behind type guards; not worth it before it shows in a profile.

## The inliner and -X:JITCalls

- **Inlined pure callees** (`JitCompiler.Inline.cs`). An inlined callee's guards are the
  caller's. After an effect in the caller, a failure in an inlined pure callee falls back to the
  generic call of the callee with its evaluated arguments: restarting a pure callee is
  invisible, so the deopt is absorbed at the callee's boundary. The callee's guards become one
  test (`#ok`) and a branch to that call, not an exception: an exception handler in the middle
  of an expression makes the expression compiler spill, and keeps the .NET JIT from keeping
  locals in registers across it.
- **Bound callees** (`-X:JITCalls`, `EmitDirectCall`). Today a callee's `JitDeoptException`
  leaves through the caller to the caller's entry, and the caller takes over the callee's
  facts, so a callee that deopts is a deopt after any effect the caller made first. With
  continuations the same absorption applies: a direct call after an effect is wrapped so that
  the callee's deopt (which, by the callee's own rule, only happens before the callee's first
  effect) becomes a generic call of the callee from the caller. `JitCode.Deopts` of a callee
  with continuations covers only its pre-effect deopts; the callee's fallbacks and resumes are
  its own business and never reach the caller. A resume inside a bound callee runs the callee's
  resumable body and hands its result back as `Obj`; the caller goes on if it takes `Obj` there,
  and resumes itself otherwise.
- **The "caller in flight"** window (`jit-methods.md`, Part 2) closes for impure bodies with B:
  a version check after the loop's safe point, resuming at the loop's next iteration.
- **`OnDeopt` accounting.** The entry retires a specialization whose deopt rate is high. A
  fallback is cheaper than a deopt but can still be taken on every call (a site that sees a
  second class every time). Fallbacks and resumes count as deopts for that purpose, or a body
  that falls back on every call stays in place and is slower than the generic one for good
  (round nine's lesson: measure where the old rule kept the path out).

## What the gate must check

Each invariant, the past fault it comes from, and the shape that tests it. The (effect, deopt,
operand order) matrix being added to `Util/jit-matrix.rb` by another session is the planned
gate; these are the rows it needs for continuations.

| Invariant | Fault it would have caught | Test shape |
|---|---|---|
| A fallback does the operation exactly once, on operands evaluated once, before the guard. | `o.value = o.value + 1; @x = 1` wrote twice (round four); `h.fetch(k, @n += 1)` evaluated the default twice | effect in the operand of the falling-back operation; counter in the operand |
| The effect set includes the operands of the operation itself. | `h.fetch(k, @n += 1)` | as above, for each kind of operation with a fallback |
| A fallback call sees the source's visibility and refinements. | `@w[j] = x` called a private `[]=` (round seven) | receiver with private, protected and `method_missing` writer/reader/method, unrelated class calling protected, `using` refinement of the attribute |
| Every new path into Ruby code checks the stack. | `def f; f { }; end`; `const_missing` recursion; `C.new(n + 1)` in `initialize` | a fallback call that recurses into the same method: SystemStackError, not a crash |
| The materialized scope's flow-control frame is the call's. | `d(1) { 9 }` threw MethodUnwinder (round five) | method with a block whose fallback callee yields to it via a Proc; `break` and `return` from it |
| A block put into a materialized scope is no longer reusable. | `def capture(&b); b; end` | fallback callee that keeps `binding` and later reads the block |
| One scope per invocation. | none yet | two fallback calls, the first sets `$~` (`=~` through `send`), the second reads `Regexp.last_match` |
| After the first effect, every speculative operation re-checks the method version. | none yet (it is new: today nothing speculative follows an effect) | fallback callee that redefines `Integer#+`, the attribute reader, a constant the body folded, and enables a TracePoint; each followed by the operation |
| "Last" is per invocation. | final `C.new` in a self-recursive body (38a149b) | fallback inside a self-recursive body |
| A deopt that is not after an effect still restarts the body, with the undo log at the right mark. | the 65,536-level recursion slowdown (open) | logged store, then deopt, before and after a fallback |
| Backtraces: one Ruby frame per invocation, at the line of the operation. | none in the JIT yet (`jcall` needed the named frame) | exception raised by a fallback callee, by a fallback write to a frozen object, at several lines |
| Fallbacks count toward retiring the entry. | round nine's entry measured only where the old rule let it in | a site that sees a second class on every call: time against the generic build |
| The probe reaches the fallback. | round seven's three cases that tested nothing | `IR_JIT_VERBOSE=1` shows the method specialized and a `fallbacks=` counter moving; one odd receiver per process |
| Threads: a fallback site's cache is shared by every thread running the body. | none yet (there is no GVL) | four threads through one body, with receivers of two classes |

## Staged plan

Each stage is one PR, behind `-X:JITResume` (off by default; stage *n* adds a value), with its
rows in the matrix, IronRuby.Tests, the language suite and a sweep, and is measured as perf-gate
says.

**Stage 0 - the gate** (in progress in another session): the (effect, deopt, operand order)
matrix in `Util/jit-matrix.rb`. Nothing below is merged before its rows exist.

**Stage 1 - self-guarded attribute operations after an effect; unlocks `Scheduler#hold_self`.**

    def hold_self
      @hold_count += 1                    # effect (overflow deopts before it: fine)
      @current_task.task_holding = true   # attr write on another object: deopt after effect today
      @current_task.link                  # attr read: deopt after effect today
    end

- `JitRuntime.TryWriteAttr`/`TryReadAttr` (and the Struct member pair): `WriteAttr`/`ReadSelfAttr`'s
  checks answering false instead of throwing. After the first effect `EmitMethodAttrWrite` and
  the attribute read emit `if (!Try...) fallback`, and do not set `_canDeopt`.
- The fallback is a call through a site bound with `HasLightScope` and the declaring scope; on
  `LightScopeRequired`, through the full site with a scope made once (`CreateMethodScopeLazy`
  with the shape of the generic body). Stage 1 takes only bodies with no local read after a
  fallback (so no write-back) and no typed operation after the first effect (so the
  self-guarded-tail rule holds trivially). `hold_self` has no locals.
- The typed body takes `blk` and is a named frame with line markers, as above; the fallback
  checks the stack.
- `IR_JIT_VERBOSE` prints `fallbacks=`; `OnDeopt` counts them.
- Test: `JitResume1` in IronRuby.Tests runs a `hold_self`-shaped method hot under the switch,
  asserts it is specialized (and not without the switch), then makes the fallbacks run (a
  receiver of another class with its own `task_holding=`, a private one, a frozen one, a
  `method_missing`) and compares output with CRuby 4.0's.

Expected gain: `hold_self` is 2.0% of Richards inclusive and 0.9% self (gap 11's table). Compiled,
its two attribute calls become inline ivar accesses, so most of the 2.0% goes: about 1-1.5% of
Richards, below the noise of one awfy run; show it with a micro of the method and the profile
share. Its callers (`run_task`'s handler blocks) are still generic. The stage is worth it for the
mechanism, not the number.

**Stage 2 - general calls and an `Obj`-typed tail.** A call of another method (on self or on an
object) from a typed body goes through the light-scope site with materialization on demand (stage
1's fallback, now as the primary path), and counts as an effect. After the first effect the
compiler emits the self-guarded tail: operations on `Obj`, each with a version check and a
generic slow path; typed locals that are assigned after it become `Obj`. Unlocks `queue_packet`
and `release` (given gap 11's nil-constant fix, which is built and on the Rejected list),
`DeltaBlue`'s `remove_first`, and the start of `schedule` and `run_task`. This is perf/jcall's
change with its restriction gone; measured against its numbers.

**Stage 3 - resume (B).** The resumable generic body, resume points at statement boundaries,
the frame-state map, per-statement undo marks. First PR inside the stage: a test that a goto
into a `while` body and into an `if` branch in statement position compiles and runs under both
the expression compiler and the DLR interpreter; if not, resume points are top-level statements
only. Unlocks typed arithmetic after an effect (Towers' `push_disk`/`move_top_disk`, Bounce's
`bounce`), `append`'s loop after its store, and `tree_insert`'s `Node.new` in mid-body (a
construction followed by guards). Also a write-back after a call that materialized the scope.

**Stage 4 - absorb callee deopts at the call boundary** for inlined and bound callees after an
effect, as above; `JitCode.Deopts` means "before the first effect".

**Stage 5 - the in-flight window** for impure bodies: a version check after each loop safe point,
resuming at the next iteration. Then `-X:JITResume` on by default, after a full sweep and the
benchmark suite, if stages 1-4 measured as faster.
