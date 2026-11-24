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

## When to run it: once per branch, after the quick check

The gate needs the whole test suite, a sweep and, for step 4, a quiet machine, so it is not
what each attempt is tried with. An attempt first passes the quick check, which takes
minutes and can run beside a sweep, a build or another session:

1. the candidate builds, and `git diff --stat` shows what was meant (step 1);
2. the differential matrix for the code it touched prints CRuby's output, with the
   change's switch on and off (step 2 lists them), and the IronRuby.Tests named for that
   code pass;
3. `ruby Util/perf/ab.rb -i` on the shapes the change is meant to move and on two or three
   it is not (`/perf-ab`, step 7): instructions per call, which do not need an idle CPU.

An attempt that was meant to take work off a path (a scope not made, a helper not called)
and whose shapes count the same instructions as the baseline is rejected there, with no
gate run for it. One that was meant to save something that is not instructions - a cache
miss, a mispredicted branch, a collection - is not judged by the count: it is timed
(`/perf-ab`, steps 2 to 4) before it goes on. Attempts that pass may be committed one after another on one
branch and gated together: the gate judges the branch against the commit it left. When
such a branch fails a step, `git bisect` between that commit and the tip, with the failing
step as the test, names the commit; fix or drop it and run the gate again. Nothing is
merged on the quick check alone, and every pull request has had the whole gate on the
commit it is opened at.

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
- The candidate's Release build must be of its last commit: build it here (console and
  tests) before measuring or probing anything, whatever its author says was built. In
  round fifteen a worktree's console build still held two steps its author had dropped
  from the branch, and the first measurement and probe were of those.

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

Steps 2 and 3 are CPU-bound, not timing runs: they need no quiet machine, only one gate's
work at a time on it. Build both trees at once (each has its own DLR copy, so their `obj/`
do not collide), then run what follows one piece after another, each piece using every core.

```sh
# in each tree (the candidate with the DLR copy Util/perf/worktree.sh made for it), both at once
set -o pipefail      # a failed build must stop here: tail's success would let the tests run on the binaries of an earlier build
dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -c Release -p:IronRubyTargetFrameworks=net10.0 \
  -p:DlrSourceDir=<dlr copy>/src/core/ -p:CheckDlrPatches=false -v q | tail -3 || exit 1
dotnet build Src/Console/Ruby.Console.csproj -c Release -p:IronRubyTargetFrameworks=net10.0 \
  -p:DlrSourceDir=<dlr copy>/src/core/ -p:CheckDlrPatches=false -v q | tail -3 || exit 1

# then, one tree after the other
IR_CONFIG=Release Util/parallel-tests.sh /abs/path/tests-<tree>; echo "exit $?"
```

`Util/parallel-tests.sh` runs the suite as a queue of processes of twenty tests, one per core,
with the few slow tests first: about three minutes instead of nine (the suite costs some
twenty CPU-minutes, and the old single process already used two cores; `DeepNesting1` alone
is 75 s). Exit 0 is `PASSED`; 1 is a failure that failed again when run alone; 2 is a run that
is not complete - a process that did not end as the driver ends (crashed, killed, timed out:
the OOM killer has taken this suite when other builds ran beside it) or a listed test that
never ran. An incomplete run would compare as clean: run the lists it names again, alone.
A test that failed beside the others and passed alone is in `flaky.txt` and printed
(`BlockLoopNested1` does this about once in sixteen runs under load, on master too).

`diff` the two trees' `failed.txt` and `flaky.txt`: a name only the candidate has fails the
gate - in `flaky.txt` too, until twenty runs of it alone (`./IronRuby.Tests Name`, eight at
a time) show it fails on the baseline as well. Do not go by "one failure, as before" - a
different test failing in place of the known one would look the same.

Then the differential matrices, all of them, in every mode on both trees, against CRuby:

```sh
ruby Util/perf/gate-matrices.rb --all --out /abs/path/matrices $BASE $NEW \
  --mode off=<the change's switch, e.g. IR_LIGHT_REGEX=0>     # one --mode per switch it adds
```

It runs each of the 99 matrices (`inline-matrix.rb` a case per process, `int-matrix.rb`
through its driver) under CRuby twice and under as built, `-X:NoJIT -X:NoOSR`,
`-X:NoJITCalls`, `IR_LIGHT_FRAMES=0`, `IR_PARKED_SCOPES=0` and the ReadyToRun host on both
trees, in empty directories of their own, and fails on a line the candidate prints that is
not CRuby's and that the baseline in the same mode does not print either. A DIFF is run twice
more alone before it is believed; one that then passes is reported `flaky`, and is judged as a
flaky test is. `cruby!` means CRuby itself did not exit 0: the matrix dies before its last
rows (below) and must be fixed before it proves anything. The R2R mode is skipped, and said
so, when a tree has no ReadyToRun host as new as its Release build.

The files to read most closely for the code the diff touches (each header says what "same"
means):

| Touched | Run |
|---|---|
| `Runtime/Jit/`, call sites | `Util/jit-matrix.rb` |
| calls inlined by the method JIT | `Util/perf/inline-matrix.rb` (one case per process, see its header) |
| `Runtime/Osr/`, iterators | `Util/block-loop-matrix.rb`, `Util/osr-array-matrix.rb` |
| block and scope reuse | `Util/block-scope-reuse-matrix.rb`, `Util/block-scope-reuse-blockpass.rb`, `Util/block-scope-reuse-race.rb` |
| instance variables, shapes | `Util/ivar-matrix.rb`, `Util/ivar-stress.rb` |
| Integer/Float arithmetic | `Util/int-matrix.rb` |
| Hash, Struct | `Util/hash-order-matrix.rb`, `Util/hash-struct-matrix.rb` |
| exceptions, backtraces | `Util/raise-matrix.rb`, `Util/full-message-matrix.rb` |
| calls, blocks, scopes, dispatch | `Util/perf/hot-shapes-matrix.rb` |
| light frames (`MethodDefinition.cs`), rules that answer for the frame (`DispatchIntrinsics.cs`) | `Util/block-given-matrix.rb`, `Util/light-rest-matrix.rb`, each also with its switch off and with `IR_LIGHT_FRAMES=0` |
| calls a compiled method binds to another's typed body (on by default) | `Util/jit-calls-matrix.rb`, also with `-X:NoJITCalls` |

A matrix is compared only after it is seen to end: `ruby FILE; echo $?` prints 0 and the
output's last line is the file's last row (gate-matrices.rb checks the first half). A file that dies of the same exception under
CRuby and under every build prints the same four times; one did in round seventeen (a
`p [..]` after `p` had become a local), and its last rows had not run for two commits.
And for a change that lets more bodies compile, compare the `specialized` and `declined`
lines of both builds on the matrices and on the probes as well as on the benchmarks: a
body that was specialized before and is declined now is a fault of the change even when
every answer is right ("no reason recorded" is an exception swallowed).

And always, because an optimization that is only wrong when hot passes everything above:

```sh
ruby Util/perf/awfy.rb 8 "new=env IR_CONFIG=Release <tree>/ir.sh"     # any FAIL fails the gate
<tree>/ir.sh -X:NoJIT -X:NoOSR <a script the change speeds up>         # same output as with them on
```

Three shapes got past this gate once and were found in review, a fourth got past its
author's checks and was caught here, and a fifth had been on master since before the gate
existed (`Util/perf/LEDGER.md`, "Faults the gate missed"). Ask each of them of every change:
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
- Does a fast path decide from a resolved method whether a call may reach it? Make the
  method private or protected in two ways: where it is defined, and with `private :x` in a
  subclass of the class that defines it. The second leaves an entry that only forwards to the
  inherited method; resolution follows it and returns the superclass's method, whose own
  visibility is still public. The visibility is the lookup's
  (`RubyModule.GetMethodLookup(name).Visibility`), not `resolved.Info`'s.

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
  `==` or `nil?`, and look at the CRuby output once to see that it says what you meant;
- threads in a probe use block-local names that occur nowhere else in the file. Ruby
  threads run in parallel here, so three threads appending to an Array the script
  happened to name at top level fail on every tree, the baseline included, while CRuby
  prints the expected answer; and a value that depends on which thread ran first is not
  a difference (a method whose callee another thread redefines may see either one).

## 3. ruby/spec

Sweep all five suites on the candidate, when `spec/` and `mspec/` are checked out (CLAUDE.md
gives the pins), and check the result against the list of known failures:

```sh
cd <tree> && IR_CONFIG=Release JOBS=$(nproc) Util/ci-sweep.sh /abs/path/out && ruby Util/sweep-check.rb /abs/path/out
```

It takes under ten minutes on four cores (32,000 examples), less on more, reruns a crashed or hung job
file by file and a new failure alone, and fails on a failing example the list does not
name or a file with no summary line. A subset is not a substitute: the ten faults in
`Util/perf/LEDGER.md` got past runs of `language` and a hand-picked part of `core`. The
list is kept by the nightly job, which sweeps the default build; if the Release sweep
names something new, run that file on the baseline too before believing it. Not while a
timing run is going, and nothing else heavy while it runs: timing-sensitive examples flip
under load.

For a single directory run by hand (a comparison with the baseline, say), run mspec from
the root of a checkout, one run at a time per directory: `default.mspec` there tells mspec
which implementation it is on (run from anywhere else, the `fixnum_max` helper raises and
five examples of core/string and core/array fail on any build), and two runs in one
directory share `rubyspec_temp`. Compare the names of the failing examples, not only the
summary lines.

## 4. Nothing got slower

Back to back, twice, on a quiet machine (`uptime`), with both trees named by absolute path:
`./ir.sh` is whichever tree the shell happens to be in, and step 2 left it in the candidate.

```sh
BASE=/abs/path/to/baseline NEW=/abs/path/to/candidate
ruby $BASE/Util/perf/micro-table.rb "base=env IR_CONFIG=Release $BASE/ir.sh" "new=env IR_CONFIG=Release $NEW/ir.sh"
ruby $BASE/Util/perf/awfy.rb 12 "base=env IR_CONFIG=Release $BASE/ir.sh" "new=env IR_CONFIG=Release $NEW/ir.sh"
ruby $BASE/Util/perf/awfy.rb 12 "new=env IR_CONFIG=Release $NEW/ir.sh" "base=env IR_CONFIG=Release $BASE/ir.sh"
```

Both runs are of all fourteen AWFY benchmarks, Havlak included: that is awfy.rb's default.
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

The same goes for a micro line. A script that times ten shapes one after another times
the later ones in whatever state the earlier ones left the process in, and that state
differs between two trees when one of them compiles an earlier shape and the other does
not: in round eight three lines were 15% slower in every round of such a script and the
same on both trees once each shape ran in a process of its own. Give a shape that looks
slower its own process (a script that takes the shape as an argument), six or more
alternating rounds, and compare medians and minima; a line that reaches the same minimum
on both trees has a fast and a slow state, not a cost.

A change to *when* a compiled path is entered (a floor, a threshold, a screen) is not
covered by the benchmarks or by `alloc.rb`: measure the shapes the old rule kept out, just
under the old limit and with the lightest body the path accepts, one shape and one length
per process (`Util/perf/micro.rb s_each1 ...` has `each`, `times`, `map` and `any?` over 1,
2 and 4 elements). In round nine a floor lowered from 16 to 1 made NBody 27% faster,
allocated nothing more anywhere, and made `a.each { |x| s += x }` over one or two elements
1.5 to 2 times slower. For a change to the method JIT's screen, compare the whole
`[jit] specialized= rejected= screen-rejected=` line of every benchmark, not only what is
newly specialized: a method that is newly wrapped and then declined is a cost.

A change that adds a way for compiled code to be invalidated (a version, an epoch, a guard
that hands a loop back) is measured with the events that do *not* concern the code: a
second thread, and a second runtime in the process, that extends objects, defines new
constants, singleton methods and classes, and autoloads, beside a hot loop, with
`IR_OSR_VERBOSE=1`'s `stale=` and `IR_JIT_VERBOSE=1`'s `invalidations=` next to the
baseline's. In round fifteen every check that changed something the loop was built on
passed, and a loop beside a thread calling `extend` was 15 times slower.

On a machine that will not be quiet, `ruby Util/perf/ab.rb -i` on the shape files
(`/perf-ab`, step 7) says which small shapes changed their instruction count, and so which
are worth waiting for a quiet minute to time; it does not replace the two timed runs, and
the AWFY benchmarks are not counted.

More than 5% slower on any line in both runs fails the gate. Check start-up too, which
these do not see, on the ReadyToRun host: it is what `ir.sh` runs when `IR_CONFIG` is not
set and what a release ships, a Release build on Linux makes it (`Src/Console/bin/R2R`),
and it starts three times faster than the Release build beside it, so a time taken with
`IR_CONFIG=Release` is not the start-up anybody sees. Ten times each on both trees, best
of each, within 5%:

```sh
IR_CONFIG=R2R $TREE/ir.sh -e 1
IR_CONFIG=R2R $TREE/ir.sh -S gem --version
IR_CONFIG=R2R $TREE/ir.sh -S gem list
IR_CONFIG=R2R $TREE/ir.sh -e 'require "irb"'
echo '1+1' | IR_CONFIG=R2R $TREE/ir.sh -S irb
```

(`ls -l` the two `ir.dll` first: `ir.sh` falls back to nothing here, and a host older than
the Release build is the tree before the change.) A run counts only if it exits 0 and prints
what it should: `1` prints nothing, `gem --version` a version, `gem list` the gems
(`minitest` among them), the `require` nothing on stderr, `irb` a line with `2`. A command
that fails before it does its work can be the fastest of ten, and a current `ir.dll` does not
say it got that far. The command-line tools are what
start-up is for, and they run more Ruby before they answer than `-e 1` does, so a change
to when or what the tiers compile shows on them first. The tiers are on by default and
are meant to stay on; these five are the price that is watched for it. At a7060a27 they
took 0.53, 0.68, 1.65, 1.39 and 2.18 s, of which the tiers were 0-10% (`-X:NoJIT
-X:NoOSR`: 0.54, 0.67, 1.50, 1.32, 2.06). Making start-up itself shorter is gap 26 of
`Util/perf/LEDGER.md`.

## 5. Verdict

One line, PASS or FAIL, then what was run with its result (counts, not "all good"), then
for a FAIL the smallest reproduction and whether the baseline shows it. Do not fix the
change here; the gate reports.
