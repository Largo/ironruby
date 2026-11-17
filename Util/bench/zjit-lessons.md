# What the ZJIT post says, and where IronRuby stands on it

"ZJIT is now as fast as YJIT" (Rails at Scale, 2026-10-09,
https://railsatscale.com/2026-10-09-zjit-is-now-as-fast-as-yjit/) reports CRuby's new SSA-based
method JIT serving a production storefront at YJIT's response time or slightly under it, with
101 MiB of JIT memory against 125. It is a status report, not a description of techniques, but
it says what its authors found worth optimizing for application code and what they plan next.
Read on 2026-11-17. The linked paper on local variables (doi 10.1145/3840562.3844969) was not
read. `daloze-lessons.md` and `truffleruby-lessons.md` are the companions.

## What it says

- **The benchmark that matters to them is "calls, data movement, I/O"**: a request renderer,
  with the lobste.rs benchmark as its proxy. Numerics are explicitly not it: ZJIT's backend
  has no floating point support yet. Every number in `Util/perf/LEDGER.md` is from
  Are-We-Fast-Yet, which is the other kind (objects, closures, arrays, Floats).
- **Optimizing Struct allocation and initialization** gave the optimizer more to work with
  (load-store elimination then removed more).
- **A method made by `define_method`** is inlined into its caller; their benchmark of the
  call path no longer has a call in it.
- **Less memory than YJIT** because type information lives in small interpreter profiles
  and the compiler's own types live for the milliseconds of one compilation.
- **What they do not do well yet**: complex calling conventions, exception handlers
  (`begin`/`rescue` and `break`/`next` in a block go to the interpreter), floating point.
- **Next**: when to profile, compile, exit and recompile ("function lifecycle"); load-store
  elimination across a whole method; collecting compiled code that is no longer called;
  inlining blocks (their `Array#each` is inlined, its block is not yet); rewriting C
  built-ins such as `Range#each` in Ruby so that the JIT can see through them; cheaper
  inlined frames; removing allocations whose object does not escape (`Hash#each_pair`'s
  pair Arrays, `*args`); exception handlers; more precise alias analysis.

## Where IronRuby stands, shape by shape

`Util/bench/zjit-post-shapes.rb`, ns per operation, one run on 2026-11-17 on 31612b1f beside
three builds (load 6.5), so read the factors, not the figures. CRuby is 4.0.9, whose ZJIT is
older than the post's.

| shape | CRuby | YJIT | ZJIT | IronRuby | /CRuby | /ZJIT |
|---|---:|---:|---:|---:|---:|---:|
| `S.new(i, 2)`, two member reads | 130.8 | 128.3 | 157.0 | 142.3 | 1.09 | 0.91 |
| `SK.new(a: i, b: 2)` (`keyword_init`) | 368.7 | 284.6 | 349.7 | 1162.5 | 3.15 | 3.32 |
| `s.a = s.a + s.b` | 41.1 | 47.3 | 44.6 | 52.6 | 1.28 | 1.18 |
| `P.new(i, 2)`, two readers | 101.9 | 79.3 | 86.4 | 137.9 | 1.35 | 1.60 |
| `o.dm(c)`, a `def` | 25.2 | 20.8 | 30.4 | 30.6 | 1.21 | 1.01 |
| `o.bm(c)`, `define_method(:bm) { \|x\| x + 1 }` | 28.6 | 4.9 | 36.4 | 64.3 | 2.25 | 1.77 |
| `o.bm0`, `define_method(:bm0) { 1 }` | 27.3 | 2.2 | 27.5 | 64.1 | 2.35 | 2.33 |
| `h.each_pair { \|k, v\| }`, 8 pairs | 355.2 | 261.3 | 286.2 | 582.8 | 1.64 | 2.04 |
| `h.each { \|k, v\| }`, 8 pairs | 366.4 | 262.3 | 288.4 | 583.9 | 1.59 | 2.02 |
| `a.each_with_index { \|x, j\| }`, 8 | 521.3 | 410.8 | 447.0 | 995.9 | 1.91 | 2.23 |
| `def rest(*a)` called with three | 71.6 | 68.1 | 68.3 | 78.7 | 1.10 | 1.15 |
| `def pass(*a) rest(*a) end` | 194.4 | 106.1 | 144.6 | 210.3 | 1.08 | 1.45 |
| `o.kw(a: 1)` | 46.0 | 39.6 | 43.1 | 44.8 | 0.97 | 1.04 |
| `o.opt(1)`, one optional | 33.1 | 26.6 | 26.5 | 37.3 | 1.13 | 1.41 |
| `begin; x + 1; rescue StandardError; 0; end` | 24.1 | 22.3 | 23.6 | 106.2 | 4.41 | 4.50 |
| `begin; x + 1; ensure; @e = x; end` | 27.3 | 22.9 | 26.1 | 112.1 | 4.11 | 4.30 |
| `o.yld { \|x\| x }` | 41.7 | 29.0 | 44.0 | 69.7 | 1.67 | 1.58 |
| `o.blk { 1 }`, `&b` returned | 125.2 | 147.3 | 127.1 | 76.4 | 0.61 | 0.60 |
| `raise ArgumentError` and its rescue | 639.8 | 612.8 | 669.5 | 6316.3 | 9.87 | 9.43 |

What the table says:

- **A `begin`/`rescue` or `ensure` in a method costs about 80 ns when nothing is raised**:
  four times CRuby, where the handler is free until used. This is the one thing the post
  calls unsolved for ZJIT that IronRuby already pays for on every call. It is the ledger's
  gap 9 seen from the caller: such a method gets no light frame and no typed body. .NET's
  own `try` regions cost nothing on the path that does not throw, so the cost is what the
  method does around the handler, not the handler.
- **A raise that is rescued costs 6 us**, ten times CRuby: a .NET exception with a stack
  trace. Code that uses exceptions for control flow (`Integer("x") rescue nil`, `loop` with
  `StopIteration`, `catch`/`throw` if it goes the same way) pays it per use.
- **A method made by `define_method` is called at 2.3 times CRuby's time** and twice a
  `def`'s: the method JIT does not take blocks as method bodies at all (the ledger notes
  the same for Richards' five blocks). YJIT runs it in 2-5 ns. DSL-heavy code (Rails
  attribute methods, delegators) is full of these.
- **`Struct.new(keyword_init: true)` objects are created at three times CRuby's time**;
  positional Struct creation is level.
- **Hash iteration with two block parameters and `each_with_index` are 1.6 to 1.9 times
  CRuby**: the ledger's gap 13 (calling an iterator costs twice) with the pair on top.
  Whether IronRuby allocates the `[k, v]` Array the post wants to remove is not measured.
- Level or ahead: positional Struct creation, rest and keyword parameters, a `def` call,
  a block passed on as `&b`.

## What to take from it

1. **Measure something that is not Are-We-Fast-Yet.** Their own conclusion is that a
   web-shaped benchmark steered the work better than micro-benchmarks. The four shapes
   above that are far from CRuby (`rescue`/`ensure`, a rescued raise, `define_method`
   methods, keyword Structs) are invisible in the fourteen AWFY benchmarks because the
   suite's rules exclude them (`daloze-lessons.md`, section 0: no exceptions for control
   flow, no built-in collections). The ledger has them as gaps 22 to 24.
2. **Their plan and IronRuby's state**, item by item: blocks inlined into iterators - done
   here for nine iterators by the block-loop tier, and for nested ones since round
   fourteen; built-ins in Ruby so the JIT sees through them - IronRuby's prelude has them
   in Ruby and pays for it (ledger gap 15: 3.1 times CRuby against 1.6 for C#), because
   its JIT does not inline them, so the lesson is the inliner, not the language; cheaper
   inlined frames - light frames; allocation removal - not started, the unboxed Float
   instance variable (gap 2) is the first step of that kind; exception handlers - gap 22;
   load-store elimination - a typed body reads `@x` again each time it is named, and
   nobody has measured what that costs; code GC - compiled bodies are never collected
   (`truffleruby-lessons.md`, section 5).
3. **Keep the profile small and the compiler's types short-lived.** Not measured here: how
   much memory the method JIT's profiles, stubs and compiled bodies hold per method.
