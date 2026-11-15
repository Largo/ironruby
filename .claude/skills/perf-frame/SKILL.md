---
name: perf-frame
description: Make one more kind of Ruby method cheaper to enter in IronRuby, in the compiler - either let a method shape that still allocates a scope on every call run without one (a method that passes its block or a splat on, uses super, defined?(yield), keywords, rescue), or have the frame answer a call that today forces the scope (as block_given? now is). Use when IR_LIGHT_FRAMES=why says many methods are kept out of light frames by one construct, when /perf-core finds that the methods left are all blocked on the same thing, when wrapper or delegator methods (`*args, &block`) are slow everywhere, or when asked to make method calls, blocks or frames cheaper across the board. Prefer it to /perf-step for anything about the cost of entering a method. One shape per run; C# work that needs a differential matrix before it needs code.
---

# One more shape without a scope

A Ruby method normally allocates a `RubyMethodScope` (about 184 bytes, plus its locals)
per call. A *light frame* is a method whose body provably never needs it and makes none:
that is IronRuby's stand-in for what TruffleRuby gets from escape analysis, and it has to
be earned shape by shape. Each shape let in speeds up every method of that shape, in the
prelude and in programs alike - and this is the code where the perf ledger records most of
its faults, so the order of work here is matrix, then switch, then code.

Read first: `Util/bench/truffleruby-lessons.md` sections 3 and 4, and in
`Src/Ruby/Compiler/Ast/Definitions/MethodDefinition.cs` the comments on
`IsLightFrameCandidate`, `IsScopeFree`, `TransformLightFrame` and
`LightFrameRewriter.LightScopeCall`.

How it fits together:

- `IsLightFrameCandidate` / `IsScopeFree` is the cheap filter over the Ruby AST: which
  parameters and which statements a light frame may have.
- `LightFrameRewriter` then rewrites the transformed body: uses of the scope become the
  declaring scope (for helpers that only need the runtime or the lexical chain) or a scope
  made on demand. If a use is left that it cannot stand in for, the method keeps its scope.
- A call from a light frame goes through a site flagged `HasLightScope` that is given the
  declaring scope. A rule that would read the caller's scope cannot be built for it; the
  site gets a rule that answers `RubyOps.LightScopeRequired`, and the frame then makes its
  scope and calls again through the ordinary site (`RubyMetaBinder.Bind`,
  `CallArguments.ScopeRead`).
- A scope-free block's body goes through the same rewriter (in `BlockDefinition.cs`,
  where `IsLightCallBody()` is tested), with no flow-control frame and no block parameter
  of its own.

## 1. Choose the shape

Count what keeps methods out, over a workload that matters (a benchmark, a test run, a
Rails boot), keeping only the files you care about - start-up alone defines a few hundred
methods (RubyGems, the prelude) and would otherwise dominate the count:

```sh
IR_LIGHT_FRAMES=why IR_CONFIG=Release ./ir.sh script.rb 2>&1 >/dev/null |
  grep -a '^\[light\] why not' | grep -a -E 'ruby4\.rb|script\.rb' |
  sed 's/.*): //; s/ at line.*//' | sort | uniq -c | sort -rn | head
```

The reason is the first statement that is not scope-free, by its node type
(`SimpleAssignmentExpression`, `IfExpression`, `MethodCall`), not by the construct inside
it: leave out the `sed` and what follows to see each method's `file:line`, and read a
dozen of those lines to see which construct of the table below it is. It counts definitions, not calls. To know
which of those methods are hot, count calls under CRuby on the same workload:

```sh
ruby -e 'c = Hash.new(0); tp = TracePoint.new(:call) { |t| c["#{t.defined_class}##{t.method_id}"] += 1 }; tp.enable
         at_exit { tp.disable; c.sort_by { |_, v| -v }.first(30).each { |k, v| puts "#{v} #{k}" } }; load ARGV.shift' script.rb
```

(Prelude methods are C under CRuby and do not show; for those, read which of their callers
are hot.) Open shapes, with what they were measured to cost (`Util/perf/LEDGER.md`, gaps 9
and 16, has the current list):

| shape | today | who has it |
|---|---|---|
| a call that passes the method's block on, `inner(&block)` | +94 to +275 ns per layer | 36 prelude wrappers, every delegator |
| a call with a splatted argument, `inner(*args)` | keeps the method out | `String#strip`, `lstrip`, `rstrip` (+145 ns over the C# method) |
| `defined?(yield)` outside the opening guard | +75 to +180 ns | |
| `__method__`, `__callee__` | force the scope | logging, delegation |
| `super` | keeps the method out | hot `initialize` methods |
| keywords, `rescue`, `$~`, regex literals | keep the method out | |

Take one. State what it costs with a pair of shapes in `Util/bench/prelude-cost.rb` (the
method with the construct and the same method without it), measured with `/perf-ab`. No
pair, no experiment: the gain has to be visible in one line.

## 2. A worktree for the work

This checkout stays the baseline, clean and built; everything below - the matrix, the
compiler change, the test - is written in the candidate tree:

```sh
Util/perf/worktree.sh NAME          # ../wt-NAME on branch perf/NAME, with its own DLR copy; prints its build command
```

From a checkout with no `../dlr` beside it (one under `.claude/worktrees`), export
`DLR_SOURCE_DIR=<the DLR checkout>/src/core` first. Build the worktree with the command it
prints before writing anything, so that the matrix's first run is of the unchanged code.

## 3. The matrix comes first

Write `Util/<shape>-matrix.rb` in the worktree before changing the compiler, modelled on
`Util/block-given-matrix.rb` and `Util/light-rest-matrix.rb`: a `t(label) { }` that runs
each probe 3000 times and prints every distinct answer once, so that the interpreted body,
the compiled one and a site whose rule is already bound all meet the case. It must print
the same under CRuby and under the unchanged build before you start; then every difference
later is yours.

Cases a frame change has to survive:

- **Who is asked.** The method the construct calls defined by the class itself; defined,
  removed, undefined and aliased over *after* the site is bound; a singleton method; a
  module included or prepended later; a `BasicObject` receiver; `method_missing`; a
  private method with an explicit receiver; a refinement in use, and one that exists but
  is not in use here; the Kernel method itself replaced and put back.
- **Other ways in.** `send`, `__send__`, `public_send`, `Method#call`, `to_proc`,
  `bind_call`, `&method`, `define_method`, a singleton method, a module function,
  `method_missing`, `super` into it and out of it, `ruby2_keywords`.
- **Where it is written.** In a block of the method, a nested block, a proc or lambda
  called after the method returned, a `while`, an `eval`, a `binding`.
- **What the frame still does afterwards.** `binding`, `local_variables`,
  `local_variable_set`, a block that reads and one that writes a local, `raise` and the
  label in the backtrace, `return` from a block, `break`, `caller`, recursion, `__method__`.
- **Arguments.** None, one, many, `nil`, a splat (of nothing, of nil, of a non-Array),
  keywords and a double splat, the Array or Proc being the call's own and not the
  caller's, reassigning the parameter.
- **Tracing.** A TracePoint for `:call` and for `:c_call` sees what it saw; the answers
  after it is disabled are the ones before.

## 4. Behind a switch

A `static readonly bool` beside the others in `MethodDefinition.cs`
(`IR_LIGHT_REST`, `IR_LIGHT_BLOCK_GIVEN`): on unless the variable is `0`. The switch is the
control for measuring (`/perf-ab`, the third arm), the first thing to try when something
breaks later, and what lets a release turn the shape off without a revert.

Then the least change that lets the shape in. Two kinds:

**A shape the filter refused** (a rest parameter): often the rewriter and the scope made on
demand already handle it and only `IsLightFrameCandidate` or `IsScopeFree` says no. Remove
the refusal under the switch, run the matrix, and read `IR_LIGHT_FRAMES=why` to confirm the
methods became light. Update `WhyNotLightFrameCandidate` with it.

**A call the frame can answer itself** (`block_given?`): the rule for the site answers a
marker object and the frame turns the marker into the answer from what it holds - its
block parameter, its name. In `DispatchIntrinsics` a `TryBuild...` that returns the marker
when the call resolves to the built-in method, called from `RubyCallAction.BuildCall`; in
`LightFrameRewriter.LightScopeCall` the test for the marker. It stays a call site with the
receiver test every rule has, which is what makes redefinition, BasicObject and
method_missing come out right without any code for them.

What went wrong doing exactly this, so that it does not again:

- **A marker reaches a site that does not know it.** Sites share rules by name and
  signature, a block's frame uses the same rewriter without the method's block parameter,
  and `send(:name)` binds the named call for a site written `send`. Decide which sites may
  get the marker with one predicate used on both sides
  (`DispatchIntrinsics.IsLightBlockGivenSite`), give it only to a site written with the
  name (`siteNamesMethod`), and make every other site that can match treat the marker as
  "make the scope and call".
- **One argument list in two nodes.** `AstExpressions` is a builder that is emptied when
  an expression takes it: build two nodes from one and the second has no arguments
  ("Non-empty collection required" from the visitor, at compile time, in every mode).
- **A marker must be a `public static readonly` field of a public class** (`RubyOps`): the
  compiler that precompiles the prelude maps such objects to their field. Any other object
  constant would be a different object in the precompiled code, and the identity test
  would fail silently.
- **Check `TracePoint.CCallTracing` when binding**, as the dispatchers do: while it is on,
  rules have to report the call. Turning it on discards the rules made before.
- **Statics that remember a `RubyMemberInfo`** are a hint only (`IsKernelMethod(method,
  ref last, ...)`): several runtimes can share the process.
- Many `.cs` files here are CRLF, some with a BOM. Edit by bytes and check
  `git diff --stat` for a file that changed whole.

## 5. Verify in every mode

The matrix must print CRuby's output with: the default; the switch off;
`IR_LIGHT_FRAMES=0`; `-X:NoJIT -X:NoOSR`; `-X:JITCalls`; and `IR_CONFIG=R2R` after a
Release build (the host whose prelude is precompiled). Also `Util/jit-matrix.rb`,
`Util/jit-calls-matrix.rb`, and the two matrices of the shapes already let in.

Then a test in `Src/IronRuby.Tests/Runtime/MiscTests.cs` beside `Method_LightFrameRest1`
(registered in `RubyTests.cs`; expected output from `ruby -w`, which also shows the
warnings the test would fail on), and `/perf-gate ../wt-NAME`.

A frame change reaches every method, so sweep more than the gate's `spec/language`:

```sh
# in the baseline checkout
export IR_CONFIG=Release; OUT="$(cd .. && pwd)"
SUITES="core language" JOBS=5 Util/parallel-sweep.sh "$OUT/sweep-base"
(cd ../wt-NAME && SUITES="core language" JOBS=5 Util/parallel-sweep.sh "$OUT/sweep-new" "$OUT/sweep-base")
```

The baseline first, then the candidate against it: the second run prints the examples
that are new and the ones fixed. Each tree needs `spec` and `mspec` linked in (CLAUDE.md).
About six minutes each at five jobs; do not rebuild either tree while one runs.

Compare failing example names against a sweep of the unchanged build. If a job has no
summary line, look at it before trusting the rest: run as root from a tree whose `spec` is
a symlink of another path depth, `spec/core/dir`'s chroot examples loop forever on any
build - kill that `ir` and run the directory from a tree whose depth matches.

## 6. Measure, decide, record

`/perf-ab` with the switch-off arm, on the pair from step 1 and on the methods that were
supposed to gain. Then the whole of `prelude-cost.rb` once, and `Util/benchsuite/run.sh
--time 3 truffle/classic/ truffle/micro/ awfy/` on both trees, for what moved that nobody
expected: a method that became light but then needs its scope on most calls pays for the
detour.

Keep it if the pair moves by 15% or more, nothing else is slower beyond the run's noise,
and everything in step 5 holds. Otherwise leave the switch off by default or revert, and
say which in the ledger. A change that is kept is promoted as `/perf-step` does (step 4
there): committed on `perf/NAME`, merged into the working branch with `--no-ff`, the
baseline rebuilt and the pair measured again on the merged build; then the worktree is
removed (`Util/perf/worktree.sh -d NAME`).

Record: gap 9 of `Util/perf/LEDGER.md` (what light frames still exclude) brought up to
date with the switch's name and the matrix; the commit says what the shape cost, why
letting it in is sound (what makes the scope made on demand see the same thing), the
numbers and every mode that was checked. Do not open a pull request unless asked.
