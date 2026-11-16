# A deopt that does not restart the body: design

Status: stage 1 is implemented behind `-X:JITResume` (off by default; `Src/Ruby/Runtime/Jit/JitResume.cs`,
`JitCompiler.Resume.cs`, matrix section 11 of `Util/jit-matrix.rb`), narrowed as its section says; the rest
is design only. It answers gap 1 of `Util/perf/LEDGER.md`
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

**Getting to a resume point.** Expression trees forbid a jump into a `TryExpression`, and the
generic body is made of them: the method's statements sit inside the try/filter/finally of
`MethodDefinition` (the `IsMethodUnwinderTargetFrame` filter, `LeaveMethodFrame` or
`LeaveLightMethodFrame` in the finally), and every `while` body inside the `BlockUnwinder` catch
and the `EnterLoop`/`LeaveLoop` try/finally of `WhileLoopExpression`. A single `switch` at the
top cannot reach any of them. So the dispatch is **one per try region, at the start of that
region, jumping only to labels directly inside it**:

- the method's dispatch is the first statement inside the method's try, and jumps to a
  top-level resume label, or to the label just before a `while` statement that contains the
  target;
- a resume point in a branch of an `if`/`unless` in statement position needs no dispatch of
  its own. A conditional is not a try region: the expression compiler and `LightCompiler`
  both give a `ConditionalExpression` a label scope a jump may enter
  (`LabelScopeKind.Statement`; `Try` and `Expression` scopes are the ones that refuse). So the
  dispatch of the region the `if` is in jumps straight to the label in the branch, or to the
  label before a `while` in it, and the test, whose outcome the typed body already acted on,
  is not evaluated again;
- a loop being resumed is entered through its head with a resume index set (`#resume`, a
  local of the resumable body): its condition is `#resume != 0 || cond`, so the condition the
  typed body already evaluated is not evaluated again; the first statement inside the loop
  body's innermost try is the loop's dispatch. When the target label is directly in that body
  (or in an `if` branch of it), the dispatch clears `#resume` and jumps to it. When the target
  is in a nested loop, the dispatch jumps to the label just before that `while` statement and
  leaves `#resume` as it is: the nested loop's condition has to pass on it as well, and the
  nested loop's dispatch needs the index. `#resume` is cleared once, by the dispatch of the
  loop whose body holds the target, and from then on every loop condition, the enclosing
  loops' included, is evaluated for real.

Each jump then stays inside one try region, which is what the compiler allows. That is read
from the two compilers' label rules, not run. Stage 3's first test runs it: whether the DLR
interpreter (`LightCompiler`), which runs the generic body before it is compiled, takes the
jumps into a loop's try, and whether both take the jump into an `if` branch as IronRuby emits
it (a conditional with a value even where the value is dropped; if that is refused, the
resumable body emits a statement-position `if` as a void one).

What it handles: everything A cannot - typed results, a changed world (check versions and
tracing after anything that ran Ruby code, resume when they moved), a scope that has been
materialized (see "Locals" below), loops (resume inside the loop body after the safe point,
which also closes the "caller in flight" window of `Util/bench/jit-methods.md` for impure
bodies, the part it says "is not designed here").

What it costs: a second generic compile of the method (once, and only for methods that resume);
a frame-state map per resume point (which typed variable is which local, in which
representation); the per-region dispatch and the `#resume` test in each resumable loop's
condition (in the resumable body only, which is cold); and a hard rule that the resumable body
and the generic one are the same program.

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
2. self-guarding: its fast path re-checks everything it speculates on - the receiver's class,
   **the global method version** (`VersionOk` reads only `GlobalMethodVersion`), for a folded
   constant the constant version, **and that nothing is being traced** (the entry tests that
   separately, with `NotTracing()`) - and its slow path is the generic operation on the
   evaluated operands, with a result the code after it takes as `Obj`.

Integer and Float arithmetic, typed comparisons and inlined callees after the first effect are
emitted on `Obj` operands with the same per-operation check (static reads and compares) and a
generic slow path, or the body is declined as today. That keeps A free of world changes
without any resume: an operation that cares checks the world itself. The truth of an `Obj`
value in the tail is Ruby's (nil and false are false), never "the value is `true`", and an
operator applied to it is the receiver's (`!(true && @x)` once answered false for an `@x` with
a `#!` of its own).

Tracing is the one world change that is about the *statements* between operations, not the
operations: the generic body tests `TracePoint.ActiveEvents` at each line and at its return
(`Tracing.cs`). A fallback that runs Ruby code can enable a TracePoint:

    class Odd; def task_holding=(v); TracePoint.new(:line, :return) { |t| p t.event }.enable; end; end

`hold_self` falls back on an `Odd`, and CRuby then fires `:line` for `@current_task.link` and
`:return` for `hold_self`. So the statements of a body that come after its first fallback carry
the generic body's hooks, behind the same test of `ActiveEvents` the generic body makes
(a static read), with the scope made on demand for them as a light frame's hooks get it
(`LightFrame.MakeScopeIfTraced`); and the self-guards' tracing test sends every operation from
then on to its generic slow path, which reports its own `c_call`. Where a body cannot carry the
hooks, it is declined (stage 1, 2) or resumes generically once tracing is seen (stage 3 on).

## The state a continuation needs, and how to get it

A fallback that is a call through a site needs the caller's scope for some callees; a resume
needs it always. What a scope holds, and where each piece comes from in a typed body:

| State | Generic body | Typed body today | How to materialize |
|---|---|---|---|
| Locals | the `MutableTuple` of the method's `MethodFrameShape`, or register locals (`ScopeBuilder.TryUseRegisterLocals`) | typed CLR variables, some declared in a branch's own DLR block | box each live typed variable into the tuple layout of the generic body: `Int` boxed, `Lng` through `NarrowLong`, `Dbl` boxed, `Bool` as `true`/`false`; never assigned -> nil. The generic compile records its shape and tuple type for the stub (it has them only inside `MethodDefinition` today). A local the JIT scoped to a branch that has ended is still a Ruby local with its value: such locals are hoisted to function scope in bodies with continuations. |
| Self | the scope's self | `_self` | as is |
| The method's block | the scope's block parameter | **dropped**: the entry's `GenericCall` passes `blk`, the typed call does not | the stub never wraps a method with `&b` or one that uses its block (`ast.UsesBlock`, `JitStub.cs` at the two screens), so the body itself never yields; only something that reads the scope can reach the block - `binding.eval("yield")`, `binding.eval("block_given?")`. The typed body therefore takes `blk` as a parameter when it has continuations, and it goes into the scope through `RubyOps.CreateMethodScopeLazy`, which calls `ClearReusable` on it: whatever asked for the scope may keep the block (fault: `def capture(&b); b; end`). |
| Class for visibility and refinements | the scope's self, its declaring module, the declaring scope's refinement activation | the site the JIT made the call through has none | the parent of the materialized scope is the declaring scope (as `LightFrame.MakeScope` does); a fallback call goes through a site bound with `RubyCallFlags.HasLightScope` and the declaring scope, which returns `RubyOps.LightScopeRequired` for a protected or missing method on a receiver, a refined method, or a library method that takes the scope - and is then made again through the full site with the materialized scope (`LightFrameRewriter.LightScopeCall`, verbatim). Never a site made with `RubyCallSignature.Simple`: a site without a scope sees private and protected methods (fault, round seven). |
| `$~`, `$_` | the method scope | never read: `JitCompiler.OrdinaryGlobal` refuses any global that is not a `GlobalVariableInfo`, which `$~` and `$_` are not | they are written by library methods that take the scope, i.e. only through `LightScopeRequired`. So the scope is materialized **at most once per invocation** and kept in a local (`#scope ?? (#scope = make())`, as `LightFrame.LazyScope`), so that a second callee that reads `$~` sees what the first set, and so that a resume hands the same scope on. |
| Flow-control frame, and leaving the method | `RuntimeFlowControl` of the call (a light frame's `#rfc`); the method's statements inside a try whose filter is `IsMethodUnwinderTargetFrame` and whose finally is `LeaveMethodFrame`/`LeaveLightMethodFrame` (`MethodDefinition.cs`, around line 302) | none | made with the scope, as `CreateMethodScopeLazy` takes it, and a body with continuations is wrapped in the **same try/filter/finally** as a light frame, keyed on its `#rfc` and its `#scope` (null until made). Without the filter `def m; @a = 1; pr = binding.eval("proc { return 7 }"); pr.call; 99; end` does not return 7; without the finally a proc kept after `m` returned does not raise LocalJumpError. It is also what `break` and `return` compare with (fault: `d(1) { 9 }` threw MethodUnwinder because `MethodPropagateReturn` compared the target with the scope object only). |
| Frame and line | the generic body's frame, line markers | the typed body already carries the generic body's frame name (`JitStub.Named`: "file:line:in 'C#m'"), so a frame of it reads right; what it lacks is **line updates** - today only the stack check at its entry raises from it | A: a fallback can raise from inside the typed body, so a body with fallbacks updates its line before each fallback, as the generic body's line markers do. B: the resume leaves the typed body by throwing, and the resumable body is the frame, so each invocation shows exactly one Ruby frame. |
| Stack depth | `CreateMethodScope` checks it | the entry checks only for self-calls (`StackCheckPlacer`) | every path from a typed body into Ruby code checks it: a fallback call is such a path (four of the ten faults were a missing check). |

**Once the scope exists, the locals live in it.** Copying the typed locals into the tuple at
the moment it is made is not enough in either direction:

- the scope may be kept (a Binding, a Proc made in `binding.eval`), so a local the typed body
  assigns *after* it was made must reach the tuple: `def m; @a = 1; x = 1; b = binding; x = 2;
  b.local_variable_get(:x); end` is 2;
- the callee may write locals through it (`local_variable_set`, `eval`), so the typed copies
  are stale after the call.

This is what register locals do in generic code (`ScopeBuilder.TryUseRegisterLocals`: once the
tuple exists every access goes to it), and a typed body cannot afford that test on every typed
local. So the rule is: **the call that makes the scope is the last thing the typed body does
for that invocation.** From stage 3 on, the body resumes generically at the next statement
(B), on the tuple. Before stage 3 there is nothing to resume into, so stages 1 and 2 take only
bodies where the question cannot arise: no parameters and no locals at all (`hold_self`), and a
`binding.eval` that defines one keeps it in the Binding, not in the method. A scope that was
made but provably not kept could copy back behind type guards; not worth it before it shows in
a profile.

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
  a version check after the loop's safe point, resuming at the start of the body of the
  iteration that has just been entered (stage 5).
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
| The materialized scope's flow-control frame is the call's, and the body leaves it as the generic one does. | `d(1) { 9 }` threw MethodUnwinder (round five) | `def m; @a = 1; pr = binding.eval("proc { return 7 }"); pr.call; 99; end` returns 7; the same proc kept and called after `m` returned raises LocalJumpError |
| Locals assigned after the scope was made are the scope's. | none yet | `def m; @a = 1; x = 1; b = binding; x = 2; b.local_variable_get(:x); end` is 2; `local_variable_set` from the callee, then the local read |
| A block put into a materialized scope is no longer reusable. | `def capture(&b); b; end` | a method called with a block (it does not use it, so the stub wraps it) whose fallback takes `binding` and keeps it; `binding.eval("block_given?")` and `binding.eval("yield")` then and after the method returned |
| One scope per invocation. | none yet | two fallback calls, the first sets `$~` (`=~` through `send`), the second reads `Regexp.last_match` |
| After the first effect, every speculative operation re-checks the method version, the constant version and tracing. | none yet (it is new: today nothing speculative follows an effect) | fallback callee that redefines `Integer#+`, the attribute reader, a constant the body folded; each followed by the operation |
| A TracePoint enabled by a fallback sees the rest of the body. | none yet | `Odd#task_holding=` enabling `TracePoint.new(:line, :return, :c_call)`: `:line` for the next statement, `:c_call` for the next attribute call, `:return` for the method, as CRuby prints them |
| The truth of an `Obj` value in the tail is Ruby's, and its operators are the receiver's. | `!(true && @x)` answered false (round four) | `@x` nil, false, 0, an object with its own `!` and `==`, read after a fallback |
| "Last" is per invocation. | final `C.new` in a self-recursive body (38a149b) | fallback inside a self-recursive body |
| A deopt that is not after an effect still restarts the body, with the undo log at the right mark. | the 65,536-level recursion slowdown (open) | logged store, then deopt, before and after a fallback |
| Backtraces: one Ruby frame per invocation, at the line of the operation. | none in the JIT yet (`jcall` needed the named frame) | exception raised by a fallback callee, by a fallback write to a frozen object, at several lines |
| Fallbacks count toward retiring the entry. | round nine's entry measured only where the old rule let it in | a site that sees a second class on every call: time against the generic build |
| The probe reaches the fallback. | round seven's three cases that tested nothing | `IR_JIT_VERBOSE=1` shows the method specialized and a `fallbacks=` counter moving; one odd receiver per process |
| Threads: a fallback site's cache is shared by every thread running the body. | none yet (there is no GVL) | four threads through one body, with receivers of two classes |
| A probe's threads do not share its locals by accident. | round nine's probe whose threads appended to a top-level `r` (it failed on every tree, CRuby passed by taking turns) | thread blocks use block-local names found nowhere else in the file; where two answers are both right under redefinition (one call before, one after), the probe accepts both |

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
  checks, plus the tracing test, answering false instead of throwing. After the first effect
  `EmitMethodAttrWrite` and `EmitMethodAttrRead` emit `if (!Try...) fallback` and do not set
  `_canDeopt`. The read's result is `Obj` there: `EmitMethodAttrRead` today wraps it in
  `GuardInt`/`GuardLong`/`GuardDouble` when the profiled value was a number, which is a deopt,
  so after the first effect it drops that guard.
- The fallback is a call through a site bound with `HasLightScope` and the declaring scope; on
  `LightScopeRequired`, through the full site with a scope made once (`CreateMethodScopeLazy`
  with the shape of the generic body). Stage 1 takes only bodies with no parameters and no
  locals (so the scope never has to be kept current, see "Once the scope exists") and no typed
  operation after the first effect (so the self-guarded-tail rule holds trivially).
  `hold_self` has neither.
- The typed body takes `blk`, is wrapped in the light frame's try/filter/finally, updates its
  line before each fallback, and carries the generic body's `:line`/`:return` hooks after its
  first fallback; the fallback checks the stack.
- `IR_JIT_VERBOSE` prints `fallbacks=`; `OnDeopt` counts them.
- Test: `JitResume1` in IronRuby.Tests runs a `hold_self`-shaped method hot under the switch,
  asserts it is specialized (and not without the switch), then makes the fallbacks run (a
  receiver of another class with its own `task_holding=`, a private one, a frozen one, a
  `method_missing`) and compares output with CRuby 4.0's.

Shown: the deopt after an effect is the only thing keeping `hold_self` out. `IR_JIT_VERBOSE=1`
names the reason now (`declined Scheduler/hold_self arity 0 (passed the pre-screen): a deopt after
an effect (attribute write task_holding= at line 216)`), and with the switch Richards' `hold_self`
is specialized and nothing else in AWFY changes.

As built, stage 1 is narrower than the list above: the fallbacks are on explicit receivers other
than self (`@o.x`, `@o.x = v`), not on the Struct member pair (a Struct operation after an effect
still declines the body), and a body with fallbacks that also has a self-call, a loop, element
writes, a construction or an explicit `return` is declined. After the first fallback only literals,
self, instance variable reads and self-guarded attribute operations may be emitted. The typed body
stays part of its entry; a fallback's call and the hooks run in a small lambda named as the generic
body's frame, which keeps the line in `PrecompiledFrames` while the call runs (a separate typed body
cost ~4 ns a call). The light frame's filter is a catch that rethrows what is not its own.

Expected gain, if it is: `hold_self` is 2.0% of Richards inclusive and 0.9% self (gap 11's table). Compiled,
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

Tried once, in another form, and rejected (`Util/perf/LEDGER.md`, Rejected, perf/resume2): the bodies the
typed compiler declines compiled whole as a self-guarded tail, with no typed prefix. It was correct and
exactly as fast as the generic body, 3% slower over the benchmarks: a self-guarded operation on objects in
a body with a frame is what a call-site rule in the generic body already is. What that leaves of this
stage is the part that keeps a typed, frameless prefix, and it pays only for methods whose time is in
that prefix; for the methods named above the time is in the calls, and the gain has to come from the
call itself (a compiled body entering a compiled body without the site, the boxes or the frame).

**Stage 3 - resume (B).** The resumable generic body, resume points at statement boundaries,
the frame-state map, per-statement undo marks, the per-try-region dispatch and the loop resume
flag. First PR inside the stage: a test that the dispatch reaches a label inside a `while` body
nested in another, through both loops' heads, and a label in a branch of an `if` (at the top
level and in a loop body), under both the expression compiler and the DLR interpreter, with no
loop condition and no `if` test evaluated on the way in, and with both loops' conditions
evaluated again from the next iteration on. Unlocks typed arithmetic after an effect (Towers' `push_disk`/`move_top_disk`, Bounce's
`bounce`), `append`'s loop after its store, and `tree_insert`'s `Node.new` in mid-body (a
construction followed by guards). Also bodies with locals whose scope gets made (they resume at
the next statement), and bodies that see tracing turn on.

**Stage 4 - absorb callee deopts at the call boundary** for inlined and bound callees after an
effect, as above; `JitCode.Deopts` means "before the first effect".

**Stage 5 - the in-flight window** for impure bodies: a version check after each loop safe point.
The typed loop's safe point comes after the iteration's condition and before its body
(`EmitWhile`: test, safe point, body), so a check that fails there is inside an iteration that
has been entered: the resume point is the first statement of the loop's body, reached through
the loop's head with `#resume` set and its dispatch (stage 3), and the condition, which the
typed body has just evaluated as true, is not evaluated again. The other case, a resume that
stands *before* a loop's condition (the statement boundary before the `while`, or a check
that were placed at the back edge), is not a loop resume at all: it jumps to the label before
the `while` statement with `#resume` clear, and the generic code evaluates the condition. Stage
5 has only the first case; a check at the back edge would be the second, and must not be given
the first's index, which would run an iteration whose condition nobody evaluated. Then
`-X:JITResume` on by default, after a full sweep and the benchmark suite, if stages 1-4
measured as faster.
