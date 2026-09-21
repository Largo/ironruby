# Benchmarks and the experimental compilers

`run.sh` times `bench.rb`'s ~50 micro/medium benchmarks against CRuby and prints the ratio.

    Util/bench/run.sh                 # Debug build (what ir.sh runs by default)
    IR_CONFIG=Release Util/bench/run.sh
    IR=/path/to/other/ir.sh Util/bench/run.sh

Measure in **Release**: the optimized build is ~1.76x faster across the whole suite
(geomean 8.26x -> 4.69x of CRuby 4.0.6), because `Optimize=false` leaves the whole
runtime unoptimized for the .NET JIT. `ir.sh` honours `IR_CONFIG=Release`.

## -X:JIT - method JIT

Type-specializes a hot method body: unboxed int/double locals and CLR arithmetic, no
RubyMethodScope, direct self-calls for recursion. Guards on one global method-table
version (covers redefinition, override, singleton, prepend, include, alias); refinements
disable it; Fixnum overflow and division by zero deopt to the generic body, so a body that
can deopt and has a side effect is never specialized.

`fib` 8.7x faster, `ackermann` 8.6x - both at or past CRuby; the call family ~2x.

## -X:OSR - on-stack replacement

Replaces a running `while`/`until` loop with a type-specialized copy of itself, entered
over the same locals tuple (so there is no state to materialize). This is what reaches
loops in a block that is only ever entered once - the shape no invocation-counting JIT
can see.

`while_loop` 0.78x, `cmp_branch` 0.38x, `float_arith` 0.26x, `mandelbrot` 0.44x of CRuby.

Note: outlining a loop *without* specializing it is worth nothing - IronRuby's compiled
tier is no faster than its interpreter here, because the cost is the dynamic call site on
every operator, not interpretation.

## Both are off by default

The full sweep (core library language command_line security) passes with both enabled and
shows no new failures, but they are young. Enable explicitly:

    ./ir.sh -X:JIT -X:OSR script.rb

## Known limits

- `int_arith` does not move under either: IronRuby's Fixnum is `System.Int32`, so a sum
  past 2^31 becomes a BigInteger, where CRuby's fixnum is 63-bit.
- `times`/`each`/`upto` loops are blocks, not `while` loops, so -X:OSR does not see them.
- `raise_rescue` (~150x) and `fiber_switch` (~237x) are unrelated to both: exception
  backtrace construction, and a real CLR thread per Fiber.
