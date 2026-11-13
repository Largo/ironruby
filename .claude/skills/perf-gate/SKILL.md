---
name: perf-gate
description: Decide whether a performance change to IronRuby is safe to merge - it must compute what the unchanged build computes (IronRuby.Tests, ruby/spec language, the differential matrices for the code it touched, self-verifying benchmarks) and must not make anything slower. Use before merging any optimization, when /perf-step asks for the gate, or when asked whether a speed-up broke something. Takes the tree to check (default - the current checkout) and can run in a loop over a branch that keeps changing.
---

# The gate for a performance change

An optimization is a claim that the program still does the same thing. The gate checks that
claim against the unchanged build, not against expectations: a test that already fails on
the baseline is not the change's fault, and one that fails only on the candidate is, even
if it "looks unrelated". Argument: the candidate tree (`../wt-NAME`); the baseline is the
checkout the skill runs in, at the commit the candidate branched from.

Run the steps in order and stop at the first failure: the cheap ones catch most breakage.

## 1. The diff is what it says it is

```sh
git -C <tree> diff --stat <base>...HEAD
```

- Whole-file churn means line endings or a BOM were rewritten: fix that first (CLAUDE.md).
- A `*.Generated.cs` in the diff must come from its generator. A `[RubyMethod]`,
  parameter-attribute or `[Emitted]` change without the regenerated file is a silent no-op
  or a stale binder.
- New `static` fields: several runtimes share a process, so a static cache keyed by
  anything of one runtime leaks into the others. New caches: what bounds them, and what
  invalidates them (method redefinition, a singleton class appearing, include/prepend,
  refinements, `remove_method`, constant reassignment, freezing)?
- Shared mutable state on the fast path: there is no GVL. Ask what a second thread sees
  between any two statements of the new code.

## 2. It still computes the same thing

```sh
# in the candidate tree, with the DLR copy Util/perf/worktree.sh made for it
dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -c Release -p:IronRubyTargetFrameworks=net10.0 \
  -p:DlrSourceDir=<dlr copy>/src/core/ -p:CheckDlrPatches=false -v q | tail -3
(cd Src/IronRuby.Tests/bin/Release/net10.0 && ./IronRuby.Tests /verbose > results.txt 2>&1; tail -1 results.txt; grep '^> FAILED' results.txt)
```

Then, whichever apply to the code the diff touches (each file's header says how to run it
and what "same" means; most compare JIT/OSR on against off, or IronRuby against CRuby):

| Touched | Run |
|---|---|
| `Runtime/Jit/`, call sites | `Util/jit-matrix.rb` |
| `Runtime/Osr/`, iterators | `Util/block-loop-matrix.rb`, `Util/osr-array-matrix.rb` |
| block and scope reuse | `Util/block-scope-reuse-matrix.rb`, `-blockpass.rb`, `-race.rb` |
| instance variables, shapes | `Util/ivar-matrix.rb`, `Util/ivar-stress.rb` |
| Integer/Float arithmetic | `Util/int-matrix.rb` |
| Hash, Struct | `Util/hash-order-matrix.rb`, `Util/hash-struct-matrix.rb` |
| exceptions, backtraces | `Util/raise-matrix.rb`, `Util/full-message-matrix.rb` |
| calls, blocks, scopes, dispatch | `Util/perf/hot-shapes-matrix.rb` |

And always, because an optimization that is only wrong when hot passes everything above:

```sh
ruby Util/perf/awfy.rb 8 "new=env IR_CONFIG=Release <tree>/ir.sh"     # any FAIL fails the gate
<tree>/ir.sh -X:NoJIT -X:NoOSR <a script the change speeds up>         # same output as with them on
```

Write one new differential case for the shortcut itself: the shape it accelerates, run hot
(a few thousand iterations, past the JIT thresholds), with the thing that must invalidate
it happening in the middle - the method redefined, a singleton method added to the cached
receiver, the block captured with `&b`, `binding` called, an exception raised through it -
and compare the output with CRuby 4.0's. If the tree has a matrix for the area (`Util/perf/hot-shapes-matrix.rb`
for calls and blocks), add the case there; otherwise add a registered test in `Src/IronRuby.Tests` (CLAUDE.md, "Tests").

## 3. ruby/spec

CI runs `spec/language`; do the same when `spec/` and `mspec/` are checked out (CLAUDE.md
gives the pins), on both trees, and compare the summary lines and the failing example names:

```sh
IR_CONFIG=Release ./ir.sh -Imspec/lib mspec/bin/mspec-run spec/language
```

A change in call dispatch, blocks, scopes or core classes has a wide reach: also sweep
`core` for the classes it touches (`Util/parallel-sweep.sh new base-out`). Not while a
timing run is going.

## 4. Nothing got slower

Back to back, twice, on a quiet machine (`uptime`):

```sh
ruby Util/perf/micro-table.rb "base=env IR_CONFIG=Release ./ir.sh" "new=env IR_CONFIG=Release <tree>/ir.sh"
ruby Util/perf/awfy.rb 12 "base=env IR_CONFIG=Release ./ir.sh" "new=env IR_CONFIG=Release <tree>/ir.sh"
```

More than 5% slower on any line in both runs fails the gate. Check start-up too, which
these do not see: `time` of `ir.sh -e 1` ten times on each, best of each, within 5%.

## 5. Verdict

One line, PASS or FAIL, then what was run with its result (counts, not "all good"), then
for a FAIL the smallest reproduction and whether the baseline shows it. Do not fix the
change here; the gate reports.
