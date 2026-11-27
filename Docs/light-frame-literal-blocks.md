# A literal block in a light frame: what it costs, and a Proc without a method scope

Status: measured and designed in round thirty-seven (perf/lazyproc), not built. It is the remainder of
gap 13 of `Util/perf/LEDGER.md` (the fixed cost of calling an iterator) and a case of gap 16 (a light
frame that ends up making its scope).

## What it costs

`Util/bench/iter-shapes.rb`, ns per call of a light method, the callee `def nb; 1; end` ignoring its
block, alternating rounds against CRuby 4.0 (round thirty-seven, this host):

| shape | IronRuby | CRuby | what it is |
|---|---|---|---|
| `nb0`: `nb` | 39-43 | 32-36 | a light frame calling a light frame |
| `nbp`: `nb(&P)` | 52-62 | 36-44 | a stored Proc passed on through the light site (`LightBlockPassEnabled`) |
| `nby`: `return nb if a; nb { }` | 56 | 36 | a body with a literal; the hot path calls without a block |
| `nbx`: `return nb(&P) if a; nb { }` | 108 | 48 | the same, the hot path passing a stored Proc |
| `nbl`: `nb { }` | 116-123 | 35-40 | the literal itself |
| `eachp` / `eachl`: `a.each(&P)` / `a.each { }` | 84 / 120 | 68 / 64 | the same through Array#each |

So the literal's ~60 ns over a stored Proc is in two parts:

- **About 15 ns is the Proc** (`nbl` against `nbx`): made, or made again from the parked scope
  (`InstantiateParkedBlock`), and converted by the callee.
- **About 45 ns is the call with a block in a body that has a literal** (`nbx` against `nbp`): the same
  `nb(&P)` costs 108 in such a body and 62 in one without. In a light body with literal blocks
  (`LightFrameRewriter.BlockLiterals`) a call with a block does not take the light site
  (`blockPass = ... && !BlockLiterals`): it goes through the full site with the scope made on demand
  (`light.LazyScope`: `TakeParkedScope` + `ReuseParkedScopeLazy`, or `CreateMethodScopeLazy`), and the
  method then leaves through `LeaveLightMethodFrameSlow` and parks (`BeginPark`/`EndPark`). A call without
  a block in the same body keeps the light site and costs 56 (`nby`).

How the 45 ns divides between the scope, the parking around it, the frame's exit with a scope and the
full site is **not measured**: Linux perf cannot run where this was done, and callgrind could not name the
JIT's code. The pieces read cheap (the scope's constructor is a dozen field writes; publishing the parked
scope with a release write instead of a compare-exchange moved nothing, perf/iter3; allocating instead of
parking, `IR_PARKED_SCOPES=0`, costs the same). The first job of an attempt is that attribution - a
profile on a host with perf, or the sequence the body emits timed piece by piece in C# - because it
decides which of the two steps below is worth building.

## How a literal reaches its method today

- **The method's locals.** A light body's locals are in the tuple the scope made on demand wraps
  (`scope.LocalsTupleVariable`) whenever a block reads them through that scope; a body that can keep them in
  CLR registers (`ScopeBuilder.TryUseRegisterLocals`) materializes the tuple when the scope is made
  (`MaterializeLocals`). Which of the two a body with a literal of the narrow shape gets is to be read from
  `TransformLightFrame` before B is built: B needs the tuple to be the frame's own storage. A block reaches an enclosing local through its Proc:
  `Proc.LocalScope` is the method's scope, and the block's lambda takes the parent tuples from it
  (`BlockDefinition`: `CreateScopeWithParent(..., bfc.Proc.LocalScope, ...)`, and the closure variables
  `#closure<n>` of `ScopeBuilder`). A scope-free block body (`IsLightCallBody`) runs without a scope of its
  own and reads the enclosing locals from the same parent.
- **The literal's Proc** is `new Proc(ProcKind.Block, self, scope, dispatcher)` with `scope` the method's
  scope (`InstantiateBlock`/`InstantiateReusableBlock`/`InstantiateParkedBlock` all take
  `gen.CurrentScopeVariable`, which the rewriter turns into `light.LazyScope`): the literal forces the
  scope whether or not its body reads anything of it.
- **Callees.** A call from a light frame without a literal block goes through a site flagged
  `HasLightScope`, given the declaring scope; a rule that needs the caller's scope answers
  `RubyOps.LightScopeRequired`, and the frame makes its scope and calls again through the full site
  (`LightFrameRewriter.LightScopeCall`). In a body with literals, calls with a block are left on the full
  site, so the literal forces the scope a second time over, at the call.

## What needs a real method scope behind a literal's Proc

Everything that reads `Proc.LocalScope`, or reaches the method's frame through it:

| use | how it reaches the scope | detectable |
|---|---|---|
| the block reads or writes an enclosing local | the parent tuples from `Proc.LocalScope` | at compile time (the block's outer variables, `BlockDispatcher.OuterVariableNames`) |
| `return` in the block | the MethodUnwinder's target is the method's frame | at compile time |
| `binding`, `eval`, `local_variables`, `__method__` in the block, a nested literal | the block's scope, made with the method's as its parent | written in the block: at compile time; through `send`, `method`, `instance_exec`: only at run time, as LightScopeRequired from the block's own call sites |
| `$~`, `$_` set or read by the block or a method it calls | `GetInnerMostClosureScope` walks from the block's scope to the method's | at run time (a callee that takes the scope answers LightScopeRequired) |
| a TracePoint `:b_call`/`:line` in the block | the block's scope made for the hook, its parent the method's; `binding.local_variables` lists the method's locals | at run time (`TracePoint.ActiveEvents`) |
| the Proc escapes: the callee names it (`&b`, `Proc.new`, `define_method`), keeps it, calls `#binding` on it | the Proc object outlives the call and carries `LocalScope` | at run time: the callee's conversion (`ConvertAsMethodFrame`, not `ConvertAsLightFrame`/[BlockNotKept]) |
| `break` from the block | the Proc's converter (the callee's frame), and `LocalScope.IsEmpty` in `BlockParam.Break` | at run time, but needs only something non-empty |
| constants and `def` in the block | the lexical chain from `Proc.LocalScope`, which passes through the method's scope to the declaring scope | the declaring scope gives the same lexical answers |

## The design

The narrowest shape that can be sound: a literal whose body **reads and writes no enclosing local, has no
`return`, no nested literal, and none of `binding`, `eval`, `local_variables`, `__method__` written in it**,
in a light body whose literals are all of that kind (`nb { }`, `a.each { |x| f(x) }`). Its Proc needs the
method's scope for nothing it does itself; only the run-time reaches of the table remain. Two changes
follow from the numbers, each worth building only if the attribution says so:

**A. The call with such a literal through the light site.** In a body with literals, a call with a block
takes the full site with the scope made on demand (`blockPass = ... && !BlockLiterals`). For a literal of
the narrow shape the call needs the caller's scope no more than `nb(&P)` does: it can take the
`HasLightScope` site, with LightScopeRequired (then the full site with the scope, as today) when its rule
reads the caller's scope. What it saves is the full site's share of the 45 ns; on its own it still makes
the scope, because the Proc does.

**B. The Proc's scope made, but not as the frame's.** The light frame makes the scope object the Proc
needs (a dozen field writes) without making it the frame's published scope: `#scope` stays null, so the
frame leaves through the inlined `LeaveLightMethodFrame` fast path, does not park, and its own calls keep
the light site. The Proc holds a complete method scope (the frame's tuple, self, block, rfc, declaring
scope and module), so every run-time reach of the table finds a real scope: a block that calls `binding`
through `send` gets a block scope whose parent is it; `$~` set by a callee lands in it; a Proc kept by the
callee and asked for `#binding` after the method returned sees the method's locals, because the tuple is
the frame's own. Two things change against today and are what its matrix must pin:

- **One scope per invocation.** If the frame later needs a scope of its own (a call that answered
  LightScopeRequired, `__method__`, a TracePoint), it must take the one the Proc holds, not make a second:
  `$~` set in the block and read by the method after, `local_variable_set` through one binding and read
  through the other. So the frame keeps the made scope in a variable of its own (`#proc-scope`), and
  `light.LazyScope` becomes `#scope ?? (#scope = #proc-scope ?? make())`, publishing it then.
- **Leaving.** A scope the frame never published still had a Proc over it, which a callee may have kept:
  its flow-control frame must say the method is gone (`LeaveMethod`) when the method returns, so that a
  `break` or a `binding.eval("return")` through it after that raises LocalJumpError as today. That is the
  slow path of `LeaveLightMethodFrame`; B keeps it only when `#proc-scope` is non-null and the Proc is no
  longer reusable (it escaped), and otherwise resets the scope for the next call as parking does.

**Which first.** If the 45 ns is mostly the full site, A alone takes it. If it is mostly the scope's
publication (the frame's exit with a scope, the parking around it), B takes it and A adds the rest. If it
is the object itself, neither does much, and the literal's cost is what a heap Proc over a heap scope is
in .NET: CRuby's literal is a stack frame field, and only escape analysis of the kind TruffleRuby does
would remove it.

## The matrix it needs first

`Util/literal-block-light-matrix.rb`, before any code, printing CRuby's output on the unchanged build:
the narrow literal passed to a light Ruby method, Array#each and Integer#times, called hot; the callee
naming the block (`&b`) and keeping it, calling `b.binding.local_variables` after the method returned, and
`define_method(:x, &b)`; the block calling `binding` and `local_variables` through `send`; the block
calling `"a" =~ /a/` through `send` and the method reading `$~` after; a TracePoint `:b_call` reading
`binding.local_variables` in the block; `break` with a value and the method's `ensure`; an exception
through the block; a re-entrant call of the method from its block; four threads; `IR_LIGHT_BLOCKS=0`,
`IR_PARKED_SCOPES=0` and the change's switch.
