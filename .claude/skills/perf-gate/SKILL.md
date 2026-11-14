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
git -C <tree> status --short          # must print nothing: the gate judges commits
git status --short -- Src Util        # nothing here either: the baseline is built from this checkout
git -C <tree> diff --stat <base>...HEAD
```

- Uncommitted changes in the candidate tree are built and tested but are in neither this
  diff nor the merge that follows: commit them (or stash and rebuild) before going on.
- Uncommitted changes in the baseline checkout make the baseline something other than the
  commit the candidate branched from, and every comparison below is against it: commit or
  stash them, and rebuild the baseline, first.

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
set -o pipefail      # a failed build must stop here: tail's success would let the tests run on the binaries of an earlier build
dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -c Release -p:IronRubyTargetFrameworks=net10.0 \
  -p:DlrSourceDir=<dlr copy>/src/core/ -p:CheckDlrPatches=false -v q | tail -3 || exit 1
(cd Src/IronRuby.Tests/bin/Release/net10.0 && ./IronRuby.Tests /verbose > results.txt 2>&1; echo "exit $?"; tail -1 results.txt; grep '^> FAILED' results.txt | sort > failed.txt)
```

The run is complete only if it ends as the driver ends it: `PASSED` (exit 0), or the
numbered list of failures followed by a `Repro: ... IronRuby.Tests.dll /verbose Name...`
line (exit 1). A run that crashed or was killed - the OOM killer has taken this suite
when other builds ran beside it - ends anywhere, with another exit status, may have
printed no failure name at all, and would compare as clean: run it again, alone.

Do the same in the baseline checkout (its own Release test build) and `diff` the two
`failed.txt`: a name only the candidate has fails the gate. Do not go by "one failure, as
before" - a different test failing in place of the known one would look the same.

Then, whichever apply to the code the diff touches (each file's header says how to run it
and what "same" means; most compare JIT/OSR on against off, or IronRuby against CRuby):

| Touched | Run |
|---|---|
| `Runtime/Jit/`, call sites | `Util/jit-matrix.rb` |
| `Runtime/Osr/`, iterators | `Util/block-loop-matrix.rb`, `Util/osr-array-matrix.rb` |
| block and scope reuse | `Util/block-scope-reuse-matrix.rb`, `Util/block-scope-reuse-blockpass.rb`, `Util/block-scope-reuse-race.rb` |
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

Three shapes got past this gate once and were found in review, and a fourth got past its
author's checks and was caught here (`Util/perf/LEDGER.md`, "Faults the gate missed").
Ask each of them of every change:
- Can a frame that this makes cheaper recurse - directly, through a block it passes, through
  a callee - without passing a stack check? Run it a million deep: it must raise
  SystemStackError, not end the process.
- Does a compiled path use something *about* a value (its truth, its type at one moment) in
  place of the value an operation is defined on?
- If a fast path can fail part way (a deopt, a fallback, a retry), what has already been
  done when it does, and is that done twice? Look at every operation that can fail *after*
  each new effect, including ones that used to be safe because nothing came before them.
- Does a fast path make a call of its own in place of one the source makes (a fallback
  site, a helper that dispatches)? A site made without the caller's scope sees private and
  protected methods and no refinements (`RubyCallAction.GetVisibilityContext`), so it is
  the source's call only for a receiver whose method is public and unrefined. Give it a
  receiver with a private method of that name, one with a protected one called from an
  unrelated class, one with `method_missing`, and a refinement active where the caller is
  defined.

Write one new differential case for the shortcut itself: the shape it accelerates, run hot
(a few thousand iterations, past the JIT thresholds), with the thing that must invalidate
it happening in the middle - the method redefined, a singleton method added to the cached
receiver, the block captured with `&b`, `binding` called, an exception raised through it -
and compare the output with CRuby 4.0's. If the tree has a matrix for the area (`Util/perf/hot-shapes-matrix.rb`
for calls and blocks), add the case there; otherwise add a registered test in `Src/IronRuby.Tests` (CLAUDE.md, "Tests").

A case proves something only if it runs the path it is about. Three cases written for
this gate did not at first: a method the JIT declined for another reason (an attribute
read with an implicit self), a store that deopted before it reached the fallback it was
meant to test, and a report built on `==` in a file where an earlier `using` had refined
Object#==. So for every new case:

- run it with `IR_JIT_VERBOSE=1` and see the method named as specialized (and, for a
  fallback or a guard, that it did not simply deopt first);
- make the probe reach the new code and nothing before it: a receiver that fails an
  earlier check tests that check;
- in `hot-shapes-matrix.rb`, everything after the `IDRefine` case runs with Object#== and
  #nil? refined and a module prepended to Kernel: report values with `inspect`, not with
  `==` or `nil?`, and look at the CRuby output once to see that it says what you meant.

## 3. ruby/spec

CI runs `spec/language`; do the same when `spec/` and `mspec/` are checked out (CLAUDE.md
gives the pins), on both trees, and compare the summary lines and the failing example names:

```sh
IR_CONFIG=Release ./ir.sh -Imspec/lib mspec/bin/mspec-run spec/language
```

Run mspec from the root of a checkout, one run at a time per directory: `default.mspec`
there tells mspec which implementation it is on (run from anywhere else, the `fixnum_max`
helper raises and five examples of core/string and core/array fail on any build), and two
runs in one directory share `rubyspec_temp`. Compare the names of the failing examples,
not only the summary lines.

A change in call dispatch, blocks, scopes or core classes has a wide reach: also sweep
`core` for the classes it touches (`Util/parallel-sweep.sh new base-out`). Not while a
timing run is going.

## 4. Nothing got slower

Back to back, twice, on a quiet machine (`uptime`), with both trees named by absolute path:
`./ir.sh` is whichever tree the shell happens to be in, and step 2 left it in the candidate.

```sh
BASE=/abs/path/to/baseline NEW=/abs/path/to/candidate
ruby $BASE/Util/perf/micro-table.rb "base=env IR_CONFIG=Release $BASE/ir.sh" "new=env IR_CONFIG=Release $NEW/ir.sh"
ruby $BASE/Util/perf/awfy.rb 12 "base=env IR_CONFIG=Release $BASE/ir.sh" "new=env IR_CONFIG=Release $NEW/ir.sh"
ruby $BASE/Util/perf/awfy.rb 12 "new=env IR_CONFIG=Release $NEW/ir.sh" "base=env IR_CONFIG=Release $BASE/ir.sh"
```

The second run has the columns the other way round on purpose. Which column runs first
moves single benchmarks by more than the 5% this step allows: in four runs of round six
Towers was 7-10% slower in whichever tree ran second and NBody 1-6% faster. A benchmark
is slower only if it is slower in both orders. A cell of a tree that does not touch that
benchmark's code still moves by about 5% from run to run (NBody by more), so before
blaming a change for a slower line, say which of its code that benchmark runs.

A line that is slower in both orders is looked at before it is believed or dismissed:
compare the JIT's decisions on the two trees (`IR_JIT_VERBOSE=1`, sorted, times stripped),
the bytes (`alloc.rb`) and the profile; then measure it in separate processes of a fixed
length, the two trees taken alternately, eight of each, and compare the medians
(`harness.rb NAME 120 INNER`, the median of the last sixty iterations of each process).
Storage failed this step in four of seven awfy.rb runs in round seven and was the same
on both trees measured that way.

More than 5% slower on any line in both runs fails the gate. Check start-up too, which
these do not see: `time` of `ir.sh -e 1` ten times on each, best of each, within 5%.

## 5. Verdict

One line, PASS or FAIL, then what was run with its result (counts, not "all good"), then
for a FAIL the smallest reproduction and whether the baseline shows it. Do not fix the
change here; the gate reports.
