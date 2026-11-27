# A rescue above a passed ensure: design, and why stage 2 was not built

A `rescue` whose clauses do not match costs nothing extra when its class list can be decided in
the CLR's first pass: the filter `RescuePassUnwinder || RescuePassMayTest && CannotMatch...`
(`Body.TransformExceptionHandling`, `RubyOps`) lets the exception go through without a catch. It
cannot when the exception has already gone through a compiled ensure clause that has not run yet
(`RubyExceptionData.PendingPassedEnsures`): the frame catches the exception, completes its
backtrace, matches the clauses, and throws it again. The first such frame above an ensure costs
about 20 us, each further one about 3; CRuby about 1 (`Util/bench/rescue-above-ensure-shapes.rb`).
`Timeout.timeout`'s raise has this shape (`Error.handle_timeout`'s rescue above the Proc's
`ensure request.finished`) and pays 40 of its 63-76 us for it.

This note asks whether the pending ensure clauses could be run from the filter itself - in the
first pass, before the class list is looked at, which is the order Ruby wants - and the second
pass made to skip them. The answer is no, for reasons that are about the CLR's model, not about
the work: stage 2 was not built. The matrix rows are in `Util/raise-matrix.rb` ("a rescue above
a passed ensure") and print CRuby's output on the unchanged build.

## What exists

- **`EnsureGuardExpression`** guards the statements of a body that has an ensure clause and no
  rescue clause. Compiled, it is a try with a filter, `RubyOps.PassEnsure`, and a fault block,
  `RubyOps.EnterPassedEnsure`, inside the try/finally whose finally is the ensure clause.
- **`PassEnsure`** (first pass) lets the exception through when catching it would only have
  taken note of it: the exception is marked handled, gets `$!` as its cause, and
  `PendingPassedEnsures` is counted up. It refuses (the frame catches) for a stack overflow, a
  stack more than `PassEnsureMaxDepth` compiled frames deep, or less than `PassEnsureStackRoom`
  (512 KB) of stack left - room for the ensure clause, which in the second pass runs *on top of
  the raise*: CoreCLR runs a finally funclet with the frames between the throw and the catch
  still on the stack.
- **`EnterPassedEnsure`** (second pass, the fault block) sets `$!` for the clause, counts
  `PendingPassedEnsures` down, and hands the frame's part of the backtrace to the exception
  (`RubyExceptionData.EnterPassedEnsure`), so that a backtrace taken later still has the frame.
- **`RescuePassMayTest`** refuses while `PendingPassedEnsures` is not zero: the clauses of the
  pending ensures will run Ruby code before Ruby would match this frame's class list.

So "a clause run on top of the raise" already exists, and it is the CLR's second pass doing it in
its own order. What stage 2 needed is something else: the clause run during the *first* pass.

## What stage 2 would have been

The rescue frame's filter, finding `PendingPassedEnsures > 0`, would run each pending clause
itself, innermost first, then evaluate the class list as now, and mark the clauses as run so that
their finally blocks do nothing when the second pass reaches them (a flag on the exception's data
per pending clause, read by the clause's guard). The point was to keep this frame from catching
when nothing matches.

## Why it is not sound

1. **CLR finally blocks between the raise and the ensure run in the second pass, and Ruby wants
   them first.** A clause of an outer frame runs, in Ruby, after everything inside it has been
   cleaned up. Inside it there are C# `finally` blocks of library methods - `Mutex#synchronize`
   unlocks in one (`RubyMutex.cs`), `File.open` with a block closes the file in one - the DLR's own
   finally blocks (a Ruby frame restoring `$!`, popping its frame record), and the light frames'
   bookkeeping. Run from a first-pass filter, the ensure clause would run before all of them.
   `Util/raise-matrix.rb` "pe: a Mutex unlocked before the outer ensure" prints
   `[[:locked, false]]` on CRuby and on IronRuby today; the early clause would see `true`, and
   one that locked the mutex again would raise "deadlock; recursive locking". "pe: a File closed
   before the outer ensure" is the same with `closed?`. Running those CLR blocks early too is not
   possible: a filter cannot run another frame's finally, and the second pass runs it regardless.
   Knowing that no such block lies between - no C# frame with a finally, no Ruby frame with
   bookkeeping - would make the case too rare to matter: every Ruby frame has one.

2. **The clause cannot be called from outside its frame.** A compiled ensure clause is code of its
   frame's method, reading the frame's locals; a light frame keeps them in CLR locals, which no
   other code can reach. Running it from an outer frame's filter needs it compiled as a closure
   over a heap environment, for every ensure clause, raise or not - a cost on the path that does
   not raise, to make the path that raises and does not match cheaper.

3. **What the clause does may not stay inside a filter.** The CLR runs a filter and takes any
   exception leaving it as "false"; a filter cannot transfer control.
   - **The clause raises** ("pe: the ensure raises": the new exception replaces the old, with the
     old as its cause). From the filter, the new exception would have to propagate from the
     clause's frame, through the frames between it and this one, whose filters have already
     answered for the old exception. There is no way to restart the first pass at an inner frame
     with another exception.
   - **The clause jumps** - `return` ("pe: return from the ensure": `:returned`), `break`, `next`,
     `throw` (each its row), or `retry` of a rescue between. Each ends the exception and moves
     control to a point inside the frames the first pass is walking. A filter can only return.
   - **Thread#raise or Thread#kill while the clause runs** ("pe: Thread#raise during the ensure",
     "pe: Thread#kill during the ensure"): delivered at a safe point inside the clause, the
     exception would be thrown inside the filter and swallowed by the CLR; parking it again
     would deliver it at the wrong place (after the old exception's handling) or lose it.
   Each of these could make the filter say "catch" for the rest of the dispatch, but by then the
   clause has run once, and the second pass would run it again (or skip it, and the effect of the
   jump would be lost).

4. **`$!`, `cause` and the backtrace** would be the smaller problem: `$!` in the clause is the
   propagating exception (set as `EnterPassedEnsure` sets it), `cause` is decided by `PassEnsure`
   already, and the backtrace part the clause's frame hands over could be handed over from the
   filter as well. They do not rescue the design from 1-3.

## What the second pass would have had to skip, and how

Each pending clause run from a filter: the guard's finally would test a per-clause "already run"
mark on the exception's data (the frame's record index, which `EnterPassedEnsure` already has) and
do nothing - `$!` restore, the clause - while the frame's own bookkeeping still ran. This part is
easy; it is the order of everything else that is not.

## What would be sound, and what it would win

- **Cheaper catches**, not fewer: the first catch above a passed ensure completes the backtrace
  the ensure began (`TakePassedEnsureCapture`, `CaptureExceptionTrace`) and throws again. Making
  the throw again a `ExceptionDispatchInfo.Throw` of a captured dispatch, and the capture lazier,
  would lower the 20 us without changing any order. That is ordinary profiling work, and needs a
  sampler this environment does not have (`perf_event_open` is refused).
- **Fewer rescue frames on the path**: for `Timeout`, `handle_timeout`'s rescue exists to turn its
  own `ExitException` into `Timeout::Error`. An IronRuby override could test `exc.equal?(e)` in a
  way that does not need the rescue - it cannot: the rescue is how it learns that the exception
  passed through. Left as it is.

## Rows

`Util/raise-matrix.rb`, section "a rescue above a passed ensure", seventeen rows: the ensure before
the class list (a class list that is a method call), a non-matching rescue with the ensure run
once, the constant of the class list reassigned in the ensure, `===` defined in it, the exception
extended with a module of the class list in it, `$!` in the ensure and after, the ensure raising
(cause), `return`, `break`, `next` and `throw` from the ensure, `retry` above it, a Mutex and a
File cleaned up before the outer ensure, the backtrace keeping the inner frame, and Thread#raise
and Thread#kill while the ensure runs. CRuby's output on the unchanged build with the JIT on,
`-X:NoJIT -X:NoOSR`, `-X:NoJITCalls` and `IR_LIGHT_FRAMES=0` (the matrix's one row that prints an
anonymous class's address differs from run to run, as before).
