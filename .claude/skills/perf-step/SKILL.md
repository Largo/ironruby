---
name: perf-step
description: One iteration of making IronRuby faster - take the worst open gap from Util/perf/LEDGER.md or the top refusal of the census (Util/perf/census.rb), try one optimization for it (usually a trick TruffleRuby or YJIT uses) in a separate worktree, and merge it only if the census shows the method JIT or the light frames covering more methods than the unchanged build; otherwise record why not. A step takes seconds to judge - it runs no test suite, no spec sweep and no timing benchmark; those are /perf-gate's and /perf-measure's, owed once to the branch. Use when asked to make IronRuby faster in general, to try a performance idea, or to keep improving speed, and the target is not more specific: a slow core method written in Ruby goes to /perf-core, the cost of entering methods (frames, scopes, wrappers) to /perf-frame, and "is this build faster on X" to /perf-ab. Made to run in a loop (`/loop /perf-step`): each run does one experiment and leaves the ledger ready for the next.
---

# One step faster

Each run is one experiment with a recorded outcome. The ledger (`Util/perf/LEDGER.md`) is
the loop's memory: a run that neither merges something nor writes down why not has wasted
its turn, because the next run will try the same thing.

A step is judged by the census, not by the clock. `Util/perf/census.rb` parses a set of
files and takes every `def` through the decisions its first call would take it through,
without running anything: is it a light frame, and is it `never`, `maybe` or `compiles` to
the method JIT. That takes ten seconds for the whole standard library, gives the same
answer on a busy machine, and names each method whose verdict a change moved. "Faster"
here means "covers more": more methods without a scope, more with a typed body.

So a step does **not** run IronRuby.Tests, a ruby/spec sweep, the AWFY suite or
`/perf-gate`. They take ten minutes to an hour, and they are what the branch owes once
(step 5), not what each change is tried with.

Arguments, if given, name the target (`/perf-step yield`) or the number of experiments to
run side by side (`/perf-step 3`). Default: the worst open gap, one experiment.

## 1. Choose

1. Read `Util/perf/LEDGER.md` whole, and take the census of the unchanged build (step 2
   saves it). Its three tables are the candidates, most frequent first: what refused a
   method for good (`JIT never`), where a typed body stopped for want of a profile (`JIT
   maybe`), and why a method keeps its scope (`not a light frame`). The names after a line
   say which call or construct it is (`MethodCall (screen)  new 936, is_a? 666, each 577`).
2. Take the first gap under "Gaps, worst first" that has no experiment in progress and
   whose rejected attempts leave something untried, and that a line of the census is
   about; or, if none is, the top line of the census that has no rejected attempt in the
   ledger. A gap with three rejected attempts is skipped until someone writes a new idea
   under it.
3. The census counts methods, not calls: a refusal in a method nothing calls costs nothing.
   Before taking a line, see that methods which matter have it - the benchmarks' own
   (`census.rb -l "$AWFY" | grep 'MethodCall each'`), or the prelude's
   (`Src/StdLib/ironruby/ruby4.rb`), which every program calls.
4. State the hypothesis in one sentence, with the count that supports it:
   *"577 methods of the standard library and 10 of the benchmarks' are `never` because
   they call `each` with a block: the screen refuses any call with a block."* If you
   cannot say which decision refuses them, the experiment is to find out (step 2), not to
   change code on a guess.

A gap the census cannot see - how fast code runs that is already covered: an inline cache,
a storage strategy, a core method's own loop - is not judged by it. Judge such a step by
instructions per call (`/perf-ab`, step 7: `ruby Util/perf/ab.rb -i`), which is as quick
and as indifferent to the machine's load, and read "counts fewer instructions on the shapes
it is about, and no more on two or three it is not" wherever the steps below say "covers
more".

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
before proposing something they already cover. `Util/bench/truffleruby-lessons.md` has
what TruffleRuby keeps in Ruby and in Java, what a core method written in Ruby costs here
part by part, and a ranked list of what transfers.

Two kinds of gap have a skill of their own, with the traps of that kind of work; read it
for how to make the change, and come back here to judge and to record:

- a core method that is slow and is written in Ruby (the prelude): `/perf-core`;
- a kind of method that still allocates a scope on every call, or a call that forces one
  (a block or a splat passed on, `super`, `defined?(yield)`): `/perf-frame`.

Their own sweeps, timing runs and gate are not part of a step: stop before them. What a
step keeps of them is the differential matrix for the code it touches, which takes seconds
(step 2).

## 2. Experiment, away from the baseline

The build in this checkout is the baseline. Build it now, before anything is compared
with it (`dotnet build Src/Console/Ruby.Console.csproj -c Release
-p:IronRubyTargetFrameworks=net10.0`, with `set -o pipefail` if its output is piped): the
binaries do not say which sources they came from, and after a pull or a branch switch they
are another revision's. `git status --short --untracked-files=no` must print nothing first,
for the whole checkout and not only `Src` (`Directory.Build.props` and the build scripts
shape the build too): the experiment's worktree starts from the committed branch, so an
uncommitted edit here would be in the baseline and not in the candidate, and be counted
as the experiment's effect. Then do not rebuild it while an experiment is being judged.

Save the baseline's census, once per baseline build. The corpus is the standard library
with the prelude (breadth: 28,000 methods) and the AWFY benchmarks (the methods the
ledger's numbers are made of), given as paths from the checkout so that a worktree's own
copies are compared with them:

```sh
export IR_CONFIG=Release
AWFY="${PERF_HOME:-../ironruby-perf}/awfy/benchmarks/Ruby"
BASE="$(cd .. && pwd)/census-base.tsv"
./ir.sh Util/perf/census.rb -o "$BASE" Src/StdLib "$AWFY"
```

```sh
Util/perf/worktree.sh NAME      # ../wt-NAME on branch perf/NAME, with its own DLR copy; prints the build command
```

Then do the work there, yourself or through a subagent (Agent tool). One agent per
experiment; several experiments may run side by side, one worktree each. A brief for a
subagent must contain: the hypothesis and the counts behind it, the worktree path and the
build command `worktree.sh` printed, the commands below, what the other running
experiments cover (so two do not fix the same thing), the rules from CLAUDE.md that bite
here (CRuby 4.0 behaviour is the spec; no GVL; several runtimes per process; line endings;
generated files), "commit on the branch, do not push", "do not run the test suite, a sweep
or a benchmark", and "revert what does not move the census". Ask it to report the decision
it changed, the diff's shape, and the census's lines.

After each build of the worktree, three things, all of them seconds:

```sh
WT="$(cd ../wt-NAME && pwd)"

# 1. what moved: the counts then and now, and every method whose verdict changed
(cd "$WT" && ./ir.sh Util/perf/census.rb -d "$BASE" Src/StdLib "$AWFY")

# 2. the same answers: the matrix for the code touched, on both builds and on CRuby
./ir.sh Util/jit-matrix.rb > /tmp/matrix.base 2>&1
"$WT/ir.sh" Util/jit-matrix.rb > /tmp/matrix.new 2>&1
ruby Util/jit-matrix.rb > /tmp/matrix.ruby 2>&1
cmp /tmp/matrix.base /tmp/matrix.new && cmp /tmp/matrix.ruby /tmp/matrix.new    # no output: all three agree

# 3. a run's word for it: trace the benchmarks whose methods moved, and check the census against the trace
(cd "$AWFY" && IR_JIT_VERBOSE=1 IR_LIGHT_FRAMES=why "$WT/ir.sh" harness.rb Richards 3 10) 2> /tmp/trace.new >/dev/null
(cd "$WT" && ./ir.sh Util/perf/census.rb -c /tmp/trace.new "$AWFY")
```

1. `-d` is the result. A change to the compiler shows as methods of unchanged files moving;
   a change to the prelude shows as `ruby4.rb`'s own methods moving. Nothing moved: the
   change did not do what the hypothesis said, whatever else it did.
2. Which matrix: `Util/jit-matrix.rb` and `Util/jit-calls-matrix.rb` for `Runtime/Jit`,
   `Util/block-given-matrix.rb`, `Util/light-rest-matrix.rb` and the like for the frames
   (`ls Util/*matrix*.rb`; `/perf-gate` step 2 lists which goes with which code). A shape
   the change lets in that no matrix has yet gets its probes added first, and they must
   print the same under CRuby and the unchanged build before the change is made. This is
   the only thing in a step that says the code still computes the same, so it is not
   skipped; it is about ten seconds a matrix.
3. `-c` compares the census with what a run did, for the methods the run called: every
   line of it must say `0 differ`, and no method the run specialized may be `never`. It is
   also the answer to the one way the census can be fooled (step 3): a method that moved
   from `never` to `maybe` has to show in the trace as `[jit] specialized`, not as
   `[jit] declined ... (passed the pre-screen)`. Three iterations of the benchmarks
   concerned are enough; inner counts are in `Util/perf/alloc.rb`.

No whole-suite run, no `micro-table.rb`, no `awfy.rb` timing: none of them decides a step.

## 3. Decide

Keep the change only if all of these hold, on the last build of the worktree:

- **It covers more.** The census has methods that moved up - from a scope to a light
  frame, from `never` to `maybe` or `compiles`, from `maybe` to `compiles` - and the
  methods the hypothesis named are among them.
- **Nothing moved down.** A method that lost its light frame or went from `compiles` or
  `maybe` to a lower verdict is a regression, however many moved up; zero is the rule, and
  an exception is one you can name the reason for and write in the ledger.
- **`never` to `maybe` is backed by a run.** Loosening the screen alone moves methods to
  `maybe` and makes them slower: a method that is wrapped, profiled and then declined pays
  for the stub on every call for the life of the process (the comment on `JitScreen`).
  Such a move counts only if a traced run shows methods of that kind specialized, and the
  trace's count of `declined ... (passed the pre-screen)` did not grow against the same
  trace of the baseline.
- **The matrices print what the baseline and CRuby print**, and the census agrees with the
  trace (`-c`: `0 differ`).

Otherwise it is rejected, however elegant.

What the census does not say, and a step therefore does not know: that a typed body
computes what the generic one does beyond what the matrix probes; that a covered method is
faster (one that deopts on most calls is slower); how often any of them is called. That is
what the gate and the timing runs are for, and why the ledger marks a step's result as the
census's (step 4) until they have been run (step 5).

## 4. Land or record

Whoever did the experiment - a subagent or you - its changes are committed on `perf/NAME`
and `git -C ../wt-NAME status --short` prints nothing before the last census is taken: the
census judges the tree, the merge takes the branch, and they must be the same thing.

**Kept:** merge the branch into the working branch with a merge commit
(`git merge --no-ff perf/NAME`), rebuild the baseline here (it is now the new baseline)
and save its census again (step 2): the next step is compared with this one's result, and
the saved census of the merged build must show the same counts the worktree's did. If it
does not, revert the merge and record it as rejected. Move the gap's entry to "Landed"
with the census's counts before and after, the corpus, the commit, and the words
**census only, not gated**:

    light frames 64.2% -> 64.9%, JIT compiles 10.1% -> 10.4%, never 81.7% -> 81.2%
    (Src/StdLib + AWFY, 28,335 methods; 212 methods moved up, none down) - census only, not gated

**Rejected:** add to the gap's entry what was tried, what the census showed (which
methods did not move, or which moved down), and what was learned about which decision
refuses them - that sentence is what makes the next attempt different.

Either way remove the worktree (`Util/perf/worktree.sh -d NAME`) and commit the ledger.
Do not push, and do not open a pull request: what a step merged has not been tested.

## 5. What the branch still owes

Everything a step no longer runs is run once, for all the steps since the last gated
commit together - when the user asks for it, and in any case before the branch is pushed
to master or a pull request is opened from it:

- `/perf-gate` on the branch against the last gated commit: IronRuby.Tests, ruby/spec's
  language suite, the matrices in every mode, the self-verifying benchmarks. When it fails,
  `git bisect` over the steps' merge commits with the failing step as the test names the
  one to fix or revert.
- `/perf-measure`, for the ledger's "Current numbers" and for whether covering more was in
  fact faster: a step whose methods gained nothing, or lost, on the clock is reverted
  there and its entry moved to the rejected ones with the numbers.

Then the entries lose their "not gated", and the branch may be pushed.

## 6. Stop conditions for a loop

End the loop (and say why) when: the last three steps were all rejected; every line at the
top of the census's tables has three rejected attempts in the ledger; the census of the
unchanged build disagrees with a trace of it (`-c` on the baseline: the census is then
wrong, and that is the thing to fix); ten steps have been merged without a gate (step 5 is
due: say so and stop); or the disk is nearly full (each worktree and DLR copy is about
1.5 GB - remove stale ones first).
