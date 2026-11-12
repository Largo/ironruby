---
name: perf-step
description: One iteration of making IronRuby faster - take the worst open gap from Util/perf/LEDGER.md, try one optimization for it (usually a trick TruffleRuby or YJIT uses) in a separate worktree, measure it against the unchanged build, and merge it only if it is measurably faster and passes /perf-gate; otherwise record why not. Use when asked to make IronRuby faster, to try a performance idea, or to keep improving speed. Made to run in a loop (`/loop /perf-step`): each run does one experiment and leaves the ledger ready for the next.
---

# One step faster

Each run is one experiment with a recorded outcome. The ledger (`Util/perf/LEDGER.md`) is
the loop's memory: a run that neither merges something nor writes down why not has wasted
its turn, because the next run will try the same thing.

Arguments, if given, name the target (`/perf-step yield`) or the number of experiments to
run side by side (`/perf-step 3`). Default: the worst open gap, one experiment.

## 1. Choose

1. Read `Util/perf/LEDGER.md` whole. If "Current numbers" is missing, or older than the last
   merge into this branch, run `/perf-measure` first.
2. Take the first gap under "Gaps, worst first" that has no experiment in progress and
   whose rejected attempts leave something untried. A gap with three rejected attempts is
   skipped until someone writes a new idea under it.
3. State the hypothesis in one sentence, with the number that supports it:
   *"`yield` costs 88 ns against 34: the method allocates a Proc and a BlockParam per call
   even when the block is only yielded to."* If you cannot say where the time goes,
   the experiment is to find out (step 2), not to change code on a guess.

Where the ideas come from:

| TruffleRuby / YJIT does | Look in IronRuby at |
|---|---|
| virtual frames: a call allocates nothing unless the frame is captured | `Runtime/RubyScope.cs`, `Calls/RubyMethodInfo.cs`, `Runtime/Jit/` |
| blocks inlined into `yield`; no Proc unless it escapes | `Calls/BlockDispatcher*.cs`, `Runtime/Osr/BlockLoopSite.cs`, `RubyOps.Yield*` |
| polymorphic inline caches, a few compares per extra class | `Calls/RubyCallAction.cs`, `MethodDispatcher*.cs`, DLR rule caches |
| `new` = allocate + inlined `initialize`; object shapes | `Builtins/RubyClass*.cs`, `Runtime/RubyInstanceData.cs`, `Jit/JitCompiler.Objects.cs` |
| core methods as specialized nodes (`Array#[]`, `Integer#abs`, `String#==`) | `Jit/JitIntrinsics.cs`, `Src/Libraries/Builtins/*Ops.cs`, the prelude `ruby4.rb` |
| array/hash storage strategies, ropes, lazy backtraces, constant caches | `Builtins/RubyArray.cs`, `Hash`, `MutableString`, `RubyExceptionData.cs` |

`Util/bench/README.md` says what the JIT and OSR tiers already do and decline; read it
before proposing something they already cover.

## 2. Experiment, away from the baseline

The build in this checkout is the baseline. Do not rebuild it while an experiment is being
measured.

```sh
Util/perf/worktree.sh NAME      # ../wt-NAME on branch perf/NAME, with its own DLR copy; prints the build command
```

Then do the work there, yourself or through a subagent (Agent tool). One agent per
experiment; several experiments may run side by side, one worktree each. A brief for a
subagent must contain: the hypothesis and the numbers behind it, the worktree path and the
build command `worktree.sh` printed, the measuring commands below, what the other running
experiments cover (so two do not fix the same thing), the rules from CLAUDE.md that bite
here (CRuby 4.0 behaviour is the spec; no GVL; several runtimes per process; line endings;
generated files), "commit on the branch, do not push", and "revert what does not measurably
help". Ask it to report the cause it found, the diff's shape, numbers and test results.

No profiler is installed. Locate cost by experiment: `-X:NoJIT`, `-X:NoOSR`, variants of the
shape in a copy of `Util/perf/micro.rb`, a Stopwatch or counter in the suspected path.

Measure base and candidate back to back, same command line, more than once:

```sh
ruby Util/perf/micro-table.rb "base=env IR_CONFIG=Release ./ir.sh" "new=env IR_CONFIG=Release ../wt-NAME/ir.sh"
ruby Util/perf/awfy.rb 12 "base=env IR_CONFIG=Release ./ir.sh" "new=env IR_CONFIG=Release ../wt-NAME/ir.sh" \
     -- Bounce Richards Json DeltaBlue Towers
```

## 3. Decide

Numbers an agent took while other builds were running are a hint. Before deciding, measure
again yourself with nothing else running (`uptime`), twice.

Keep the change only if all of these hold:

- the targeted micro line improves by 15% or more, or the AWFY geomean by 3% or more, in
  both runs;
- no micro line and no AWFY benchmark gets more than 5% slower in both runs;
- `/perf-gate ../wt-NAME` passes.

Otherwise it is rejected, however elegant. A change that is faster but fails the gate goes
back to its author once with the failing test; if it fails again, reject it.

## 4. Land or record

**Kept:** merge the branch into the working branch with a merge commit
(`git merge --no-ff perf/NAME`), rebuild the baseline here (it is now the new baseline),
run `/perf-measure`, and move the gap's entry to "Landed" with the before and after numbers
and the commit. If `/perf-measure` does not show the gain on the merged build, revert the
merge and record it as rejected.

**Rejected:** add to the gap's entry what was tried, the numbers, and what was learned
about where the time goes - that sentence is what makes the next attempt different.

Either way remove the worktree (`Util/perf/worktree.sh -d NAME`), commit the ledger, and
push the working branch. Do not open a pull request unless asked.

## 5. Stop conditions for a loop

End the loop (and say why) when: the last three steps were all rejected; every gap is
below 1.3 or skipped; the gate fails on the unchanged baseline; or the disk is nearly
full (each worktree and DLR copy is about 1.5 GB - remove stale ones first).
