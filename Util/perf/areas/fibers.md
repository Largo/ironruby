# Perf area: fibers, external enumeration, concurrency primitives (2026-11-14)

Baseline: wt-perfbase (b6cd208a), Release net10.0. CRuby 4.0.9. Measured on a machine with a
load average of 25-58 (ten agents building), so everything is CPU time of a fixed amount of work,
in-process, best of N (`Util/perf/areas/fibers/micro.rb`, `run.sh`), answers compared with CRuby.

## Ratio table (IronRuby / CRuby, CPU ns per op, baseline)

| shape | CRuby | IronRuby | ratio |
|---|---:|---:|---:|
| Fiber#resume / Fiber.yield round trip | 247 | 17692 | **71.7** |
| Enumerator.new { \|y\| ... } #next | 555 | 21141 | **38.1** |
| (1..n).each_slice(1) #next | 629 | 20403 | **32.4** |
| Fiber.new { }.resume | 4734 | 33684 | 7.1 |
| Thread.new { }.value | 80007 | 354228 | 4.4 |
| (1..n).lazy.map.select.first | 297 | 1193 | 4.0 |
| Enumerator.new + 1000x y<< .to_a (per element) | 205 | 547 | 2.7 |
| Queue ping-pong between two threads (producer/consumer) | 264 | 657 | 2.5 |
| Array#each #next (cursor already) | 469 | 951 | 2.0 |
| ConditionVariable#signal in synchronize | 242 | 441 | 1.8 |
| Mutex#synchronize | 243 | 417 | 1.7 |
| Monitor#synchronize | 248 | 387 | 1.6 |
| Queue push+pop, one thread | 253 | 355 | 1.4 |
| Thread.current[:a] set+get | 226 | 318 | 1.4 |
| SizedQueue push+pop | 362 | 418 | 1.2 |
| thread_variable_set/get | 300 | 357 | 1.2 |
| ruby-bench gvl_release_acquire loop (read 10 + write) | 2026-2136 | 655-786 | 0.3-0.4 |

## Where the fiber time goes

A fiber is a CLR thread; a switch posts to the target's mailbox and takes from its own (spin
100 us, then Monitor.Wait). `shapes.rb` puts a Fiber round trip next to two Ruby Threads
ping-ponging over two Queues: on IronRuby 18.7 us CPU / 46 us wall vs 28 us / 35 us, and
CRuby's own thread ping-pong costs 17 us. So the 72x is the OS thread handoff itself - the
Ruby-level prelude around resume/yield is noise next to it - and wall time per switch went up
to 300-600 us when the machine was at load 25 (the spin misses, every take is a kernel
wake-up behind 25 runnable threads). Making that cheaper needs fibers that are not threads
(a stackful coroutine the CLR does not offer, or compiling fiber bodies to state machines);
no mailbox primitive tweak can get below the ~17 us thread round trip. Not attempted.

The way around it that is cheap is not to use a fiber: Enumerator#next already has
fiber-free cursors for Array/Hash/Range/Struct/String #each & co.

## Kept

1. **each_slice / each_cons #next take a cursor** (ad0824a7). `SliceCursor` cuts slices out of
   the receiver's #each cursor when Enumerable#each_slice/#each_cons are the prelude's over the
   built-in ones and #each is one a cursor can do (Array, Hash, Integer Range, Struct).
   `(1..n).each_slice(1)` #next: 32117 -> 891 ns (CRuby 633, 52x -> 1.4x). Answers equal
   CRuby on `slice_check.rb` (peek/next/StopIteration#result and cause, mutation during
   iteration, rewind, endless range, redefined each_slice, subclass with its own #each, lazy).
   core/enumerator + core/enumerable: 968 examples, 0 failures, same as the baseline.
2. **A plain #lazy's #each hands the block to the receiver's #each** (see commit). MRI's
   lazy_init_block does the same; IronRuby went generator -> Yielder -> two splatting blocks
   per element. A B B A, CPU per element, best of 8 (`lazy_shapes.rb`, `yielder_shapes.rb`):
   `(1..n).lazy.each {}` 491/490/515/574 -> 86-150 ns (CRuby 79-82); `lazy.map.select.first`
   2145/2235 -> 1620/1665 ns (CRuby 575); `lazy.map.to_a` 1569-1592 -> 1068-1413 ns. Answers equal
   CRuby on `lazy_check.rb` (return values, |k, v| / |*a| / lambda blocks, a custom #each with
   arguments and two values, next/size/eager/zip/with_index); core/enumerator + core/enumerable
   968 examples, 0 failures.

## Tried and rejected

- (none beyond the measurement of the fiber handoff above)

## What is left, ranked

1. Fiber switch / generator #next (38-72x): threads as fibers; only a non-thread fiber fixes it.
2. Thread.new (4.4x): a CLR thread start each time; Ruby Thread identity is the CLR Thread, so
   pooling threads is not a contained change.
3. Lazy chains (~2.8x after the plain-lazy shortcut): every stage is `source.each { |*values| ...
   block.call(*values) }` + Yielder#<<; a splatting block costs 435 ns vs CRuby 216
   (`yielder_shapes.rb`) - the splat block and Proc#call(*args) belong to the dispatch area.
4. Queue ping-pong between threads (2.5x), Mutex/Monitor/ConditionVariable (1.6-1.8x): small,
   the CRuby side is also a lock + GVL.
