# Benchmarks and the experimental compilers

`run.sh` times `bench.rb`'s ~50 micro/medium benchmarks against CRuby and prints the ratio.

    Util/bench/run.sh                 # Debug build (what ir.sh runs by default)
    IR_CONFIG=Release Util/bench/run.sh
    IR=/path/to/other/ir.sh Util/bench/run.sh

Measure in **Release**: the optimized build is ~1.76x faster across the whole suite
(geomean 8.26x -> 4.69x of CRuby 4.0.6), because `Optimize=false` leaves the whole
runtime unoptimized for the .NET JIT. `ir.sh` honours `IR_CONFIG=Release`.

## -X:JIT - method JIT

Type-specializes a hot method body: unboxed int/long/double locals and CLR arithmetic, no
RubyMethodScope, direct self-calls for recursion. Guards on one global method-table
version (covers redefinition, override, singleton, prepend, include, alias); refinements
disable it; overflow out of Int64 and division by zero deopt to the generic body, so a body
that can deopt and has a side effect is never specialized.

An Integer is carried as a CLR `int` or `long` following the runtime's own rule - Int32 if
the value fits in one, otherwise Int64 - and every value that leaves the specialized region
goes back through that funnel, so a value the JIT produced is `eql?`, hashes and Marshals
exactly like the same value produced anywhere else. `int op int` that overflows produces a
long rather than deopting (two Int32s never overflow a 64-bit operation, so that case needs
no check at all); only the step out of Int64 deopts, because a BigInteger result would make
the static type of every arithmetic result `object`, which is no specialization at all.

`fib` 8.7x faster, `ackermann` 8.6x - both at or past CRuby; the call family ~2x.

## -X:OSR - on-stack replacement

Replaces a running `while`/`until` loop with a type-specialized copy of itself, entered
over the same locals tuple (so there is no state to materialize). This is what reaches
loops in a block that is only ever entered once - the shape no invocation-counting JIT
can see.

`while_loop` 0.78x, `cmp_branch` 0.38x, `float_arith` 0.26x, `mandelbrot` 0.44x of CRuby,
and `int_arith` 0.57x once the lattice learned Int64 (see below).

A type-guard failure rebuilds the copy over the types the locals hold now, so a loop whose
accumulator grows out of Int32 is re-specialized rather than abandoned.

Note: outlining a loop *without* specializing it is worth nothing - IronRuby's compiled
tier is no faster than its interpreter here, because the cost is the dynamic call site on
every operator, not interpretation.

## Both are on by default

The full sweep (core library language command_line security) gives identical results with
them on and off, and IronRuby's C# test suite is unchanged, so they are the default. Turn
them off to compare, or if you suspect one of them:

    ./ir.sh -X:NoJIT -X:NoOSR script.rb

A loop whose locals change representation under it - an accumulator crossing 2^31 - deopts
once, and the site then builds a second copy over the types the locals hold *now* rather
than handing the loop back for good. That is what makes `int_arith` move: 3.61x of CRuby
before, 0.57x after.

## Known limits

- `times`/`each`/`upto` loops are blocks, not `while` loops, so -X:OSR does not see them.
- A *method* that deopts is not re-specialized the way a loop is: its entry stays installed
  and every later call whose argument types no longer match falls through to the generic
  body. Only -X:OSR rebuilds.
- `raise_rescue` (~150x) and `fiber_switch` (~237x) are unrelated to both: exception
  backtrace construction, and a real CLR thread per Fiber.
