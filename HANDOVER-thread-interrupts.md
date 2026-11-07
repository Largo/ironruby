# Handover: Thread#raise / Thread#kill delivery across fibers (after PR #125)

Status as of 2026-11-07. Repository `Largo/ironruby`.

## Where things stand

| What | State |
|---|---|
| PR #125, "Deliver Thread#raise to the fiber a thread is running" | **Merged** into `master` as `8c4d50dc` (3 commits: `643c2a55`, `ae8874f1`, `c8f16760`) |
| Follow-up fix for a CodeRabbit finding that was missed before the merge | Commit `49d42059` on branch **`ccr-e6923615-mq5tnx`**, pushed, **no PR yet** |
| Issue #126: `RubyThreadInfo` keyed by `ManagedThreadId` and never freed | Open, not started |

### First steps for the next agent
1. Check out `ccr-e6923615-mq5tnx` (it is `master` + `49d42059`).
2. Build and run the checks below. Note: one net8.0 `core/mutex` run had a single failure I could not
   capture (see "Open items").
3. Open a PR for `49d42059` when the user asks for one. Reference PR #125 and the CodeRabbit finding.

## What the follow-up commit `49d42059` fixes

CodeRabbit posted a **Major** finding on #125 as an *outside-diff* comment in the body of its review of
`c8f16760` (pullrequestreview-5812543839, about `RubyUtils.cs` CheckAsyncException). It never became an
inline review thread, so the pre-merge check, which only looked at threads, missed it.
**Lesson: before merging, read the review bodies and the "Merge Risk" line in CodeRabbit's summary
comment, not only the review threads.**

The bug: `RubyUtils.ExitThread` (Thread#kill) did not park its kill signal when a Thread#raise was
already parked, assuming the raise would end the thread. If the thread rescued the exception, or a
`Thread.handle_interrupt(RuntimeError => :never)` mask held it back, the kill was simply lost. Since
#125, a fiber waiting for control could also forward the exception to the running fiber, which might
rescue it. CRuby queues both and still kills the thread.

The fix (all in `Src/Ruby/Runtime/RubyUtils.cs` unless noted):
- `_killsBehindRaise` (a `HashSet<int>` guarded by the `_pendingAsyncExceptions` lock) records a kill that
  arrived behind a parked raise. `UnparkLocked` turns it into a parked `ThreadExitSignal` once the raise
  is taken (`GetPendingAsyncException`, `RetractAsyncException`).
- `CheckAsyncException` delivers that kill (`PromoteKillBehindRaise`) when the raise cannot be delivered
  right now: in a fiber-switch wait (`killOnly`), or when a mask holds back the raise but not the kill.
- `MoveAsyncException` does not move a raise away from a thread that has a kill waiting, and never
  overwrites a kill already parked on the target thread.
- `DropPendingAsyncExceptions` drops both. It is used at thread end, in the fiber pool
  (`ThreadOps.Fiber.cs` ParkFiberThread), and at the start of `RubyThreadStart`'s catch
  (`ThreadOps.cs`). That last spot matters: a kill delivered while the "terminated with exception" report
  is being written to a Ruby `$stderr` (a method call, so a safe point) cut the report short and broke
  ruby/spec `core/thread/report_on_exception_spec.rb` "prints the backtrace even if the thread was killed
  just after Thread#raise".
- New test `Thread_KillBehindRaise1` (`Src/IronRuby.Tests/Runtime/FiberTests.cs`, registered in
  `RubyTests.cs`). CRuby 3.3.6 prints `[:killed, :killed, true]` for its script; `master` printed
  `[:still_running, :still_running, false]`.

Verified on `49d42059`: full IronRuby.Tests suite PASSED; ruby/spec `core/fiber`, `core/thread`,
`core/enumerator` 0 failures on net8.0 and net10.0; `core/mutex` 0 failures on net10.0 and in 5 of 6
net8.0 runs (see "Open items").

## What PR #125 changed (background)

Every Fiber runs on a CLR thread of its own (`ThreadOps.Fiber.cs`: `FiberCore`, `FiberMailbox`, a fiber
worker pool). Thread#raise is delivered cooperatively: it parks the exception in
`RubyUtils._pendingAsyncExceptions` (keyed by `ManagedThreadId`), and `NudgeThread` wakes a blocked
target with `Thread.Interrupt`. Safe points (method entry, block entry, loop back edges, blocking calls)
call `CheckAsyncException`.

- `643c2a55` (by an earlier session): Thread#raise goes to the fiber the thread is *running*
  (`RubyThreadInfo.ActiveFiberThread`, `FiberSwitchLock`, `FiberCore.MakeRunning`), a fiber waiting in its
  mailbox forwards a raise rather than raising it there, and `handle_interrupt` masks look classes up in
  the right runtime.
- `ae8874f1`:
  - **Compiler:** when a block's whole body is `begin … rescue … end`, its entry safe point now runs
    inside the rescued statements (`RubyOps.CreateBlockScopeSafePointInBody`,
    `TakeReusableBlockScopeSafePointInBody`, `Body.SafePointFirst`, `BlockDefinition.Transform`).
    `Integer#times` loops in C#, so its only interrupt check was the block entry, which used to come
    before the rescue. This was most of ruby/spec `core/mutex/lock_spec` "does not raise deadlock if a
    fiber's attempt to lock was interrupted".
  - **Stale interrupts:** `NudgeThread` interrupts only while something is still parked and counts the
    nudges it has under way (`_nudgesInFlight`). A thread that takes away its own parked exception calls
    `DropStaleNudge`, which waits for those nudges and clears a latched interrupt with `Thread.Sleep(0)`.
  - **Uninterruptible locks:** `RubyUtils.UninterruptedLock` is a `lock` whose `Monitor.Enter` ignores
    `ThreadInterruptedException`. It is used for `_pendingAsyncExceptions`, `FiberSwitchLock`, the fiber
    mailbox `_lock` and `_idleFiberWorkers`. `Thread.Interrupt` also breaks a contended `Monitor.Enter`,
    and that used to kill fiber worker threads, hanging their resumers.
- `c8f16760`: 10-second deadline per raise in `Thread_RaiseBetweenBlockCallsMeetsRescue1`.

## Open items / known gaps

1. **Intermittent net8.0 `core/mutex` failure.** One run of the whole directory on `49d42059` reported
   `1 failure`. Five immediate reruns were clean and I did not capture which example it was. Run
   `core/mutex` (and `core/mutex/lock_spec.rb` alone) in a loop under load and capture the failure text
   before opening the PR.
2. **Issue #126** (`RubyThreadInfo._mapping` keyed by `ManagedThreadId`). Today it never frees an entry, so
   ids are never reused (2,000 IronRuby threads → 2,000 distinct ids) but every thread leaks. Plain .NET
   reuses ids (2,000 threads → 201 distinct ids), so a naive fix would let new threads inherit dead
   threads' state. `_pendingAsyncExceptions`, `_nudgesInFlight` and `_killsBehindRaise` make the same
   id-as-identity assumption. The issue has the details and a suggested direction (key by the `Thread`
   object, e.g. `ConditionalWeakTable`).
3. **Not covered by the block-entry fix:** with `-X:OSR` (off by default) a hot loop is outlined and
   checks between iterations, outside the block's rescue; and `def … rescue … end` methods still check at
   entry, before the rescue.
4. **Lost nudge (by design):** `DropStaleNudge` can swallow the nudge of a raise that parks something new
   for the thread at the same moment. That raise stays parked and is raised at the next safe point, but
   a thread already blocked in a wait that does not check parked exceptions first (such as
   `FiberMailbox.Take`) would sit until the wait ends.
5. Cosmetic: `Thread#inspect` sometimes shows the wrong source line for where a thread was created.

## How to build and check (new host has Ruby 4 and .NET)

```console
# DLR and prism checkouts are siblings of the repo; see README.md "Building" for the pinned revisions
dotnet build Src/Console/Ruby.Console.csproj                    # both net8.0 and net10.0
Util/run-tests.sh                                                # IronRuby.Tests (edit DOTNET_ROOT/DLR path if needed)

# ruby/spec + mspec at CI's pinned revisions (.github/workflows/ci.yml: SPEC_REF, MSPEC_REF)
git clone https://github.com/ruby/spec ../spec && git -C ../spec checkout dda28d049100033a8b7050eacbb35957a2eb0c00
git clone https://github.com/ruby/mspec ../mspec && git -C ../mspec checkout b79c65ed28b00f0869ad82b0e3dceef0d943cd58
for d in mutex fiber thread enumerator; do
  RUBY_EXE="$PWD/ir.sh" ./ir.sh -I../mspec/lib ../mspec/bin/mspec-run ../spec/core/$d | tail -1
done
IR_TFM=net10.0 ...                                               # same, on the .NET 10 build
```

Single IronRuby.Tests cases: `Src/IronRuby.Tests/bin/Debug/net8.0/IronRuby.Tests <TestName> ...`, e.g.
`Thread_KillBehindRaise1 Thread_RaiseBetweenBlockCallsMeetsRescue1 Fiber_ThreadRaiseReachesRunningFiber1
Thread_HandleInterruptOtherRuntime1`.

Stress repro for the lock_spec race: run the body of the lock_spec example 150 times per process in 6
parallel processes (before #125's follow-ups: about 6 failures per 900 runs; after: 0 in 5,400).
On a hang, `dotnet-stack report -p <pid>` (`dotnet tool install -g dotnet-stack`) shows where each CLR
thread waits.

## Gotchas

- Many C# files use **CRLF**, and some (`RubyUtils.cs`, `ThreadOps.cs`) have mixed line endings, even
  within a single function. Edit with tools that keep each line's ending; check `git diff --stat` for
  suspiciously large counts.
- `Kernel#loop` in IronRuby is Ruby code (`yield while true` in `Src/StdLib/ironruby/ruby4.rb`), so its back
  edge is a safe point outside the block, as in CRuby. Tests about block-entry delivery must use a C#
  loop such as `Integer#times`.
- The bare IronRuby.Tests runtime does not load `ruby4.rb` stdlib extras like `Queue` unless the test
  does `require 'ruby4'`.
