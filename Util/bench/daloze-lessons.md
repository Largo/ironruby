# Lessons from Benoit Daloze's papers, blog and talks

Benoit Daloze ("eregon") leads TruffleRuby. This is what his published work says that IronRuby
can use: for speed, and for the one thing his research is about that IronRuby shares with
TruffleRuby and CRuby does not have - Ruby threads that run in parallel. Read on 2026-11-17 from
https://eregon.me/blog/ (the posts and the papers under `assets/research/`) and the talk
transcripts on speakerdeck.com; section 8 lists what was not read. `truffleruby-lessons.md` is the
companion about TruffleRuby's source; `Util/perf/LEDGER.md` has the gaps these ideas belong to.

A section or figure number refers to the paper named under each idea. **[memory]** marks a statement
that was not found on a page read for this file. Numbers quoted from the sources are theirs, on
their machines; the only measurements made here are the probes of section 3 and these shapes from
the 2025 regexp post, CRuby 4.0 / IronRuby (ea86fa35), one run each beside two builds (load 7.7), so
factors and not figures: `/["\\\x0-\x1f]/.match?(s)` over a 10 KB String with no match 10.7 / 3.1 us, the
same String as binary 9.5 / 0.47 us, `re =~ s` 8.3 / 0.76 us, `s.index('"')` 146 / 748 ns,
`/\A[+-]?\d+/.match("12345 rest")` 452 / 663 ns (`match?` 186 / 273), the six-group time regexp
649 / 859 ns.

Where to start: section 4 has the ranking, section 1 each idea's source, what IronRuby does today
and the experiment it suggests.

The IronRuby files cited were read, not run, unless a line says "measured":
- `Src/Ruby/Runtime/RubyInstanceData.cs`
- `Src/Ruby/Builtins/RubyArray.cs`
- `Src/Ruby/Builtins/Hash.Storage.cs`
- `Src/Ruby/Builtins/RubyRegex.cs`
- `Src/Ruby/Runtime/RubyUtils.cs` (safe points)
- `Src/Ruby/Runtime/Calls/MethodDispatcher.cs`, `RubyMetaBinder.cs` and `RuleVersionSet.cs`

## 0. Four questions the texts answer
- **Unboxed fields under a concurrent type change** (OOPSLA 2016, §4.2):
  - Each pair of field and type gets its own storage location, and a location is never reused. A type change or a removal makes a *new* location; the old one stays, marked "removed".
  - The storage only grows and is allocated exactly for its shape. A reader holding a newer shape than the storage gets an out-of-bounds access, which is handled as "field absent". A reader holding a stale shape reads a value that was really written to that field with that type.
  - Reads need no synchronization at all. Writes, and changes of shape, lock the object, but only once the object is *shared* (§4.3, §5).
  - Without this, §3.2–3.4 and Figures 2–4 show three failures: lost field definitions, lost updates while the storage grows, and out-of-thin-air values.
- **Collection strategies** (OOPSLA 2018):
  - **Arrays** (§4.4, Fig. 2) have five storage strategies: empty, `int[]`, `long[]`, `double[]` and `Object[]`. Storing an element the current strategy cannot hold migrates the array, one way, to a more general strategy.
  - **When an array becomes shared,** a non-empty one moves to SharedFixedStorage: plain loads and stores, zero overhead, and the size and element type may not change. An empty one moves to SharedDynamicStorage, behind the Lightweight Layout Lock: optimistic reads, a write mode shared among writers, and an exclusive mode for layout changes.
  - **Fixed to dynamic** is done inside a guest safepoint (§5.2). Basic operations check for a safepoint before they run, never during. Operations that call back into Ruby re-check the strategy after each callback, which single-threaded code already does.
  - **Hash** (§4.5, §5.4) has three strategies: Empty, PackedArray with up to three pairs (3 because of keyword-argument hashes) and Buckets. When shared it becomes ConcurrentBuckets: Lightweight Layout Lock, lock-free bucket chains, and a lock-free doubly-linked insertion list appended with two CAS operations.
  - **Single-threaded cost:** within measurement error on Are-We-Fast-Yet (§7.1).
  - **Shared-array cost** (§7.2): SharedFixedStorage performs like a local array. Volatile element accesses cost 3–7.3x, and the Lightweight Layout Lock 4–12.6x over SharedFixedStorage.
- **Free safepoint checks** (ICOOOLPS 2015):
  - `poll()` reads a Truffle `Assumption`, which compiles to nothing (§4.2, Fig. 4). Requesting a safepoint invalidates the assumption, which deoptimizes the code that depends on it through the JVM's own safepoint, a read of a polling page. The threads then poll in the interpreter, meet at a barrier, and the assumption is renewed (Fig. 5).
  - Threads blocked in sleeps, locks or I/O are interrupted and their call restarted; native code is a latency problem (§4.4).
  - A volatile-flag `poll()` cost **25% ± 1.5% geomean** on whole benchmarks (§5.1). The thesis, §3.3.1, says `Thread#raise` and `Thread#kill` use the *deferred* form: the exception is thrown at the target's next poll, outside the safepoint, because `ensure` blocks may run arbitrary Ruby code.
- **Are-We-Fast-Yet** (DLS 2016):
  - **What the suite measures (§3.1–3.3):** compiler effectiveness on a "core language": objects with fields, polymorphic methods, closures that read and write outer variables, fixed-size arrays, strings, integers, doubles and GC.
  - **What a fair port must not use:** built-in hash tables, stacks or queues (the suite ships its own collection library); identity hashes; non-local returns except in an `if` or to implement an iterator; `break`/`continue` except to implement iteration.
  - **What a fair port must do:** be as identical as possible across languages; use arrays as fixed-size; iterate with `forEach` and a closure; be idiomatic; be well-typed (numeric fields start at 0, not nil); be deterministic.
  - **Per benchmark** (Table 1, Table 2, Fig. 1–3):
    - Mandelbrot and NBody are floating-point.
    - Json is string-heavy.
    - Storage stresses the GC with a tree of arrays.
    - Sieve, Bounce, Queens, Permute and Storage are dominated by closure calls (Sieve: 60M polymorphic closure applications).
    - Richards: 95.5M monomorphic calls.
    - CD: 23M polymorphic calls.
    - DeltaBlue has the most polymorphic sites (23 method, 4 closure).
    - **Havlak:** 79.6M monomorphic calls, 2.7M polymorphic calls, 17.9M polymorphic closure applications over 5 sites, and a maximum stack depth of **1,717** where the others stay at 24–75. It is the only deeply recursive benchmark (graph traversal for loop recognition).

## 1. Ranked ideas

### 1. Instance variables that hold Floats unboxed, with a location per pair of variable and type
- **Source.** OOPSLA 2016, https://eregon.me/blog/assets/research/thread-safe-objects.pdf:
  - §2.2 and Figure 1: in Truffle's object model a field seen only with `long` values goes in a `primN` or `primExt` slot. Assigning another type moves it to an Object location and changes the shape.
  - §4.2: the rule summarized above that makes this safe without read locks.
  - §7.3: the safe model costs ≤5.1%, within error. Locking every write costs 54% geomean and **2.5x on NBody, "because there is a very large number of object writes"**.
- **IronRuby today.**
  - `RubyInstanceData._ivars` is a single `object[]`: shape in element 0, boxed values after it. It is lock-free for reads, writes and adds, and growing or removing a variable goes through the Move protocol under `lock(this)`, with a process-wide barrier.
  - Removal already moves instead of leaving a hole, which is §4.2's "never reuse a location" by another route. Every Float write allocates a box.
  - The ledger's gap 9 also notes that every Ruby object is two .NET objects, `RubyObject` plus `RubyInstanceData` (`RubyObject.cs:37`).
- **Experiment.**
  - **Hypothesis:** NBody's remaining 602 MB per iteration (gap 2, still about 80% Float boxes; 0.39 of CRuby's time, TruffleRuby 47 times faster) falls by most of that amount if the shape gives a Float variable a `double` location.
  - **Design that fits the existing protocol:** a parallel `long[]` (doubles stored as their bits) hung off the slots array. A variable changing from Float to anything else gets a *new* object slot through Move, and the old raw slot is left "removed", per §4.2. A reader holding the old shape then still reads a real Float value of that variable, never something reinterpreted.
  - **Measure:** `Util/perf/alloc.rb` and AWFY NBody, plus a micro of `@x += 0.1` through `/perf-ab`. CD and Bounce also use Float instance variables.
  - **Size:** about 500–900 lines: `RubyInstanceData`, `InstanceVariableShape`, the instance-variable sites and rules, the method JIT's and the loop tiers' instance-variable emitters, Marshal, `dup`/`clone`, `instance_variable_get` (boxes on read).
  - **What can go wrong:**
    - A raw slot and its shape published out of order. The Busy/Moved CAS protocol must cover the second array.
    - The JIT's typed bodies must deopt when a location generalizes.
    - Memory grows for variables that change type often (§6.2).
    - A Float box today keeps identity (`equal?`) across reads. CRuby's flonums do not, so dropping it is compatible. **[memory]** about flonums; check `@x.equal?(@x)` for a heap-allocated Float on CRuby 4.0.
- **Why first.** It goes straight at the largest measured allocation gap, the ledger has never tried it, and §4.2 gives a rule that fits the lock-free protocol IronRuby already has.

### 2. Profile Havlak, the deep-recursion and polymorphic-block benchmark nobody has looked at
- **Source.** DLS 2016, https://eregon.me/blog/assets/research/cross-language-compiler-benchmarking.pdf: Table 1 (Havlak is the largest benchmark, 421 lines executed and 110 methods) and Table 2, with the figures given above.
- **IronRuby today.** The ledger has Havlak at 3.74–3.90 s per iteration, 25 times TruffleRuby's time, with "nobody has looked at where its time goes". Its declines are calls, and one attribute write, on receivers that were not profiled (gap 1).
- **Experiment.**
  - **Hypothesis:** Havlak's time is in the cost of entering a frame on deep recursion and in block calls at 5 polymorphic sites. It is the only benchmark where light frames and the block-loop tier meet depth.
  - **Measure:** a profile with `IR_JIT_VERBOSE=1` and `IR_LIGHT_FRAMES=why`.
  - **Gap:** new, or part of gaps 1 and 9.
  - **Size:** no code to measure.
  - **Risk:** none. A recursion depth of 1,717 is safe even with Windows' 1 MB thread stacks if a frame stays under about 500 B; check that.
- **Why second.** It is cheap, and the paper's table says it stresses a dimension (depth) that no other benchmark in the table covers.

### 3. Probe `Regexp#match?` for a single character class over a long String
- **Source.** Blog, "Matching Regexps 200 Times Faster" (2025-03-14), https://eregon.me/blog/2025/03/14/matching-regexps-200-times-faster.html:
  - `/["\\\x0-\x1f]/.match?` over a 10 KB String takes 48.3 µs on CRuby 3.4 with YJIT and 213 ns on TruffleRuby, 227 times faster. TRegex compiles to a finite automaton, JIT-compiles it, uses SIMD (`ByteIndexOfCodePointSetNode`, a lookup table plus SIMD), and is split per call site.
  - The same technique makes a pure-Ruby JSON string escape 20 times faster than the C extension, `Time.new(String)` 2.5 times and `StringScanner#scan_integer` 3 times.
- **IronRuby today.**
  - `RubyRegex` turns a regexp into a .NET `Regex` and rebuilds it with `RegexOptions.Compiled` after `CompileThreshold` uses (`RubyRegex.cs:54, 471, 591`).
  - The input is converted with `input.ConvertToString()` (`RubyRegex.cs:236`); whether that copies a 10 KB binary or char-array content on every call is not checked.
  - **[memory]** The .NET 8+ regex engines search for a leading set with vectorized `IndexOfAny`/`SearchValues`, so the matcher itself may already be fast.
- **Experiment.**
  - **Hypothesis:** the blog's three shapes show whether IronRuby's cost is the matcher or the String conversion and the `MatchData`. If it is the conversion, a span over the content makes `match?` allocate nothing.
  - **Measure:** the blog's `bench_regexp_match.rb`, `TIME_REGEXP.match` and `/\A[+-]?\d+/.match` against CRuby, through `/perf-ab`.
  - **Gap:** new, outside Are-We-Fast-Yet; it is what a JSON or strscan-heavy application spends.
  - **Size:** 0 lines for the probe; perhaps 50–150 for a no-copy path.
  - **What can go wrong:** `$~` must still be set by `match`; String encodings; astral characters (`MatchOutsidePairs`).
- **Why third.** Cheap to find out, possibly large, but there is no ledger number behind it yet.

### 4. Storage strategies for `RubyArray`: empty, `long[]`, `double[]`, `object[]`
- **Source.** OOPSLA 2018, https://eregon.me/blog/assets/research/thread-safe-collections.pdf, §2.2 and §4.4 with Fig. 2 (the strategies and their one-way generalization), as summarized above.
- **The cost without a GVL.** Table 1, example 2: an unsafe implementation loses an update when one thread's `array[0] = "s"` migrates `int[]` to `Object[]` while another stores an integer. TruffleRuby avoids that by switching a shared array's strategy only inside a guest safepoint (§5.2, §6.2: "We rely specifically on the synchronization semantics of safepoints"). IronRuby has no stop-the-world safepoint for Ruby threads, so a migration here needs a per-array lock or CAS, or the sharing machinery of idea 5.
- **IronRuby today.** `RubyArray` has `object[] _content`, `_start` and `_count` (`RubyArray.cs:36-41`), documented as "Not thread safe (even when frozen)".
- **Experiment.**
  - **Hypothesis:** Sieve (0.83–0.85 of CRuby; Table 2: 60M closure applications over a boolean array, so `true`/`false` and no boxing), Queens and Permute (1.15–1.23, gap 4) gain little. Their arrays hold booleans or small Integers, which may be cached boxes. What would gain is Float arrays and the `Storage` benchmark's allocation.
  - **First step:** `alloc.rb` broken down by type for those benchmarks. If Integer and Float boxes in arrays are under 10% of their allocation, drop this idea.
  - **Gap:** new.
  - **Size:** large. Every `_content` user in `Src/Libraries`, `RubyArray.Jit.cs` and the block-loop tier.
  - **What can go wrong:** lost updates during migration under threads (above); JIT intrinsics; Marshal.
- **Why fourth.** The paper shows it is the foundation of TruffleRuby's arrays. Here it costs a concurrency design first, and the gain is unmeasured.

### 5. Local and shared objects (the "shared" mark in the shape) as the way to make Array and Hash safe
- **Source.**
  - OOPSLA 2016, §5.1–5.3 and Fig. 5: an object becomes shared when it is written into a globally reachable object (classes, constants, globals; Ruby's initial set is 2,347 objects, §7.4). The mark lives in the shape, so existing shape checks carry it at no cost. A write barrier on shared objects only shares the value and everything it reaches, then writes under the object's monitor. The barrier specializes itself on the structure of the value's object graph, to a bounded depth (§5.3).
  - OOPSLA 2018, §4.2–4.3: a collection changes representation *while it is still local*, just before it becomes shared, so the change needs no synchronization.
- **IronRuby today.**
  - Instance variables are already safe without locks for every object (`RubyInstanceData` header), so objects do not need this.
  - Arrays are not thread-safe, and `Hash.Storage.cs` has no locks. Under parallel threads Table 1's examples 3 and 4 (lost appends, out-of-bounds exceptions, out-of-thin-air hash entries) are possible; see questions A–C in section 3.
- **Experiment.**
  - **Hypothesis:** a shared bit on `RubyArray`/`Hash`, set by a barrier on stores into globals, constants, class variables, instance variables of shared objects and `Thread.new` captures. Shared collections then take a lock or the Lightweight Layout Lock, and nothing changes on Are-We-Fast-Yet (§7.1 of the 2018 paper: within error).
  - **Measure:** the full Are-We-Fast-Yet set unchanged, plus the section 3 probes going from wrong to right.
  - **Size:** medium to large.
  - **What can go wrong:** a missed path that makes an object reachable (block captures, `ObjectSpace`, C# hosting APIs, `Marshal`, `define_method` closures); several runtimes in one process (a per-object bit is fine); 2016's §6.1 cost of sharing large object graphs.
- **Why fifth.** It is a correctness decision for the project, not a speed gain.

### 6. Measure what the safe-point poll costs before trying anything else there
- **Source.** ICOOOLPS 2015, https://eregon.me/blog/assets/research/guest-language-safepoints.pdf, §4.1–4.3 and §5.1 (above): a volatile-flag poll cost 25% geomean on the JVM, and an `Assumption` costs 0%. Thesis §3.3.1: `Thread#raise` is a deferred action delivered at the next poll.
- **IronRuby today.**
  - `RubyUtils.SafePoint()` is a `Volatile.Read` of a static and a branch, called at every loop back edge and every method and block entry (`RubyUtils.cs:2440-2459`). Compiled loops compare one `_safePointWord` that holds the request count and the code epoch together (`RubyUtils.cs:2413`).
  - That is a step further than the paper's volatile flag (one load for interrupts and invalidation both), but it is not free. A volatile load on x64 CLR is a plain load that the JIT may not hoist **[memory]**.
  - The ledger records no measurement of the poll itself. The lessons file §5 already says Truffle's free invalidation does not transfer to .NET.
- **Experiment.**
  - **Hypothesis:** the poll is a few percent at most on `while10` (0.22 of CRuby) and Mandelbrot. Measure it with a build that replaces `SafePoint()` by nothing, as an upper bound and not a change to keep.
  - **Gap:** none yet.
  - **Size:** 1 line for the probe.
  - **Second half, for gap 2:** the ledger refuses NBody's outer blocks that store a global, because a `Thread#raise` at the inner safe point restarts the outer iteration. Following the thesis's deferred delivery, the inner loop's safe point could leave the raise pending until the outer iteration's boundary. CRuby itself checks at back edges, so a delay of one outer iteration is within what CRuby's own interrupt latency allows. **[memory]** about CRuby's check points; check `Thread#raise` against a nested loop on CRuby 4.0.
- **Why sixth.** Probably small, and the probe is cheap.

## 2. Already in the ledger or the TruffleRuby lessons, and what the sources add
- **Two-level inline cache: lookup per class, then one call target per method** (splitting talk, RubyKaigi 2023, https://speakerdeck.com/eregon/splitting-the-crucial-optimization-for-ruby-blocks-rubykaigi, the "Effect of 2-level Inline Cache" slide: about 45% fewer polymorphic calls). IronRuby already has it: `RuleVersionSet` lets a rule be shared by every class that resolves to the same method (`RuleVersionSet.cs:37`), and `MemberDispatcher.Next` chains up to `MaxChainDepth` = 8 per-class dispatchers (`MethodDispatcher.cs:59-70`). Nothing to add. Gap 12 (four classes, two speeds) is not explained by the talk.
- **Splitting** (same talk: 7.7x on `sum_to`, 1.5x on OptCarrot, 2x on RailsBench; it removes all polymorphism and megamorphism on 44 benchmarks, per Kaleba et al., cited in the talk). The lessons file §4 item 7 and §5 already cover it ("a DynamicMethod is compiled once and never collected"). The talk's argument is that the gain comes from inlining the block into the split copy, which IronRuby's block-loop tier does by hand for nine iterators (lessons §4 item 6). Nothing new against the recorded reason.
- **Shapes and lock-free instance variables.** Already `RubyInstanceData`. The 2016 paper's §4.2 adds only the rule needed if typed locations come in (idea 1).
- **Zero-cost invalidation.** The lessons file §5 records that it does not transfer. The 2015 paper confirms it depends on the JVM's polling page and deoptimization (§4.3). It adds the 25% figure for a volatile flag, which is a reason to measure (idea 6), not a way round.
- **Ropes** (ManLang 2018, https://eregon.me/blog/assets/research/specializing-ropes-for-ruby.pdf, §3.3): a single-byte substring is answered with a cached leaf from a per-encoding table, so all such substrings share their bytes. IronRuby's gap 7 already shares the content of `@input[@index]` and pays only the 40 B String object, which a mutable Ruby String needs anyway. Nothing to add.
- **Pre-initializing the core library into the image** (blog, 2019-04-24, "How TruffleRuby's Startup Time Became Faster Than MRI's"): 1,787 ms → 165 ms with native image, → 96 ms with the core library parsed ahead of time, → 25 ms with the core library *evaluated* at image build and a list of things delayed to run time (`$$`, `ARGV`, locale, options). Lessons §4 item 8 has the AOT prelude (about 260 ms of 0.85 s). The post adds the next step, a snapshot of the evaluated prelude, which .NET has no direct way to do. Start-up, not steady state.
- **Are-We-Fast-Yet as the measuring stick.** The ledger already uses it. The paper adds the per-benchmark profile above and a fairness rule to keep in mind: an optimization that only works because a benchmark uses a built-in hash or an identity hash is outside what the suite measures.

## 3. Correctness questions for a runtime without a GVL
Each probe's expected output, in the comment, is CRuby 4.0's. Run on 2026-11-17 on ea86fa35, three times
each: **A and C fail** - A raises "Source array was not long enough" out of the Array's copy in two
runs of three, C raises IndexOutOfRange once and leaves 383005 and 394876 entries with wrong values in
the other two; B, D, E, F, G and H print CRuby's answer in all three.
Each refers to the case in the sources it comes from.

**A. Concurrent appends** (2018, Table 1 example 3: "lost append or index-out-of-bounds"). `RubyArray` says it is not thread-safe.
```ruby
a=[]; 8.times.map{Thread.new{100_000.times{a<<1}}}.each(&:join); p a.size  # 800000
```

**B. Writes to distinct indices while another thread grows the array** (2018 §5.5: "size > storage.length" must never be visible).
```ruby
a=[0]*4; t=Thread.new{200_000.times{a<<1; a.pop}}; 1_000_000.times{a[1]=a[0]}; t.join; p a.size  # 4
```

**C. Concurrent Hash inserts** (2018, Table 1 example 4: lost entries, or out-of-thin-air `{a: 2}` when key and value share one array). `Hash.Storage.cs` has no locks.
```ruby
h={}; 8.times.map{|i|Thread.new{50_000.times{|j|h[[i,j]]=j}}}.each(&:join); p h.size, h.all?{|(i,j),v|v==j}  # 400000, true
```

**D. Concurrent definitions of different instance variables** (2016, Fig. 2: a lost definition), and a definition racing an update while the storage grows (Fig. 3: a lost update).
```ruby
o=Object.new; o.instance_variable_set(:@a,0); ts=8.times.map{|i|Thread.new{o.instance_variable_set(:"@v#{i}",i); 1000.times{|k|o.instance_variable_set(:@a,k)}}}; ts.each(&:join); p o.instance_variables.size, o.instance_variable_get(:@a)  # 9, 999
```

**E. Out-of-thin-air reads after a removal** (2016, §3.4 and §4.2). `remove_instance_variable` racing an add: does a reader of `@b` ever see `@a`'s value?
```ruby
o=Object.new; t=Thread.new{100_000.times{o.instance_variable_set(:@a,:A); o.remove_instance_variable(:@a)}}; bad=0; 100_000.times{o.instance_variable_set(:@b,:B); bad+=1 if o.instance_variable_get(:@b)==:A}; t.join; p bad  # 0
```

**F. `Thread#raise` delivered at a poll while `ensure` runs or a lock is being taken** (thesis §3.3.1: deferred delivery so that `ensure` runs outside the safepoint).
```ruby
m=Mutex.new; t=Thread.new{loop{m.synchronize{}}}; sleep 0.1; 100.times{t.raise "x" rescue nil}; t.join rescue nil; p m.locked?  # false
```

**G. A fiber resumed from two threads** (CRuby raises FiberError: "fiber called across threads").
```ruby
f=Fiber.new{Fiber.yield}; f.resume; Thread.new{begin; f.resume; rescue => e; p e.class; end}.join  # FiberError
```

**H. A strategy or storage changed by a block during iteration** (2018 §5.2: re-check after each callback; iterators must make progress and be weakly consistent).
```ruby
a=[1,2,3]; r=[]; a.each{|x| r<<x; a<<4.5 if x==1; break if r.size>5}; p r  # [1, 2, 3, 4.5]
```

## 4. The ranking after the talks and the remaining posts
This replaces the order of section 1; the ideas' texts there still apply.

1. **Unboxed Float instance variables** (idea 1). Unchanged.
2. **Thread-safe Array and Hash** (was idea 5, with idea 4's array storage taken out of it).
   - Probes A and C fail on IronRuby, measured (section 3): concurrent `Array#<<` raises "Source array was not long enough"; concurrent Hash inserts raise IndexOutOfRange or leave 383k–395k entries with wrong values.
   - The parallel talk (2018, slides 40–43) names this as an incompatibility users hit: "Bugs were reported to Bundler and RubyGems because JRuby and Rubinius do not provide thread-safety for Array and Hash", and "From the user point of view, Array and Hash are thread-safe on CRuby".
   - It is a correctness bug, not a speed gain, but it is the one finding here that is confirmed to be wrong today.
   - The design is in section 5.
3. **Profile Havlak** (idea 2). Unchanged.
4. **`String#index` and the fixed cost of short regexp matches** (idea 3, narrowed).
   - `match?` over 10 KB is already faster than CRuby (3.1 µs against 10.7). The open shapes are:
     - `s.index('"')`: 5 times CRuby.
     - The short anchored match: 663 ns against 452.
     - The time regexp: 859 ns against 649.
     - `match?` taking four times as long as `=~` on a UTF-8 String.
   - See section 6.
5. **Array storage strategies** (idea 4). Unchanged in substance. The 2022 benchmarking post adds that fannkuchredux, an Array-heavy benchmark, is 33.88x CRuby on TruffleRuby, "and TruffleRuby uses storage strategies for Arrays". It is still unmeasured here.
6. **Cost of the safe-point poll** (idea 6). Unchanged.

The talks gave no new speed idea that IronRuby does not already have:
- `send(:sym)` is bound as the call it makes (`DispatchIntrinsics.cs:156`, `LibraryDispatcher.cs:1157`); "Optimizing Ruby with TruffleRuby", 2017, shows send, send with a String and eval at the speed of a direct call on TruffleRuby.
- The two-level inline cache exists (`RuleVersionSet`).
- The Rails talk's "higher splitting limit for Rails" has no counterpart here.

## 5. Thread-safe Array and Hash without a safepoint: the smallest design from the papers

**What the papers' design needs a safepoint for, and what it does not:**
- **Needs one:** the migration of a shared array from SharedFixedStorage (unsynchronized) to SharedDynamicStorage (OOPSLA 2018 §5.2 and §6.2: "We rely specifically on the synchronization semantics of safepoints for migrating between concurrent strategies. In other systems, adding a similar mechanism would possibly add run time overhead").
- **Does not need one:**
  - The Lightweight Layout Lock: §6.2, "only relies on a shared-exclusive lock and atomic variables with CAS and addAndGet ... could be ported to other memory models".
  - ConcurrentBuckets: the LLL plus CAS-based lists, §5.4.
  - Sharing: done while the object is still local, §4.3 of 2018 and §5.2 of 2016.
- **So the smallest design drops SharedFixedStorage.** Every shared Array goes straight to SharedDynamicStorage, which is what the paper already does for an empty array when it is shared (§4.4, "Strategy Transitions"), and every shared Hash to a locked table.
- **What .NET cannot give:**
  - A stop-the-world pause of Ruby threads at a point of the runtime's choosing; IronRuby's safe point can only throw at a back edge. So the zero-cost SharedFixedStorage is out.
  - The JVM's deoptimization of compiled code, which TruffleRuby uses for the safepoint. Not needed for this design.
  - Element reads and writes of an `object[]` are atomic on the CLR, which is what §5.1 needs from Java arrays; a `long[]`/`double[]` element is atomic on 64-bit only **[memory]**.

**The pieces, smallest first:**
1. **A shared bit per collection.** TruffleRuby keeps it in the shape so existing checks carry it (2016 §5.1; 2018 §4.1). `RubyArray` has a `_flags` field already (`RubyArray.cs:41`, holding `IsFrozenFlag`); Hash would need one. Every mutating operation tests the bit: one load and one branch on local collections. The papers measured nothing for that test, since theirs is folded into shape checks.
2. **Shared operations under a lock.**
   - **Simplest:** `lock(this)` around every mutating operation and every read that depends on `_start`/`_count`/`_content` together, with reads taking a snapshot.
   - **Costs from the papers:**
     - Locking every write cost 54% geomean, and 2.5x on NBody, for *objects* when everything is shared (2016 §7.3, Java monitors).
     - On shared Hashes the LLL cost 14% over Local on one thread (2018 §7.4).
     - Prawn's PDF generator, which reads and writes shared Hashes heavily, was 8–33% slower (§7.7).
     - Local collections cost nothing measurable (§7.1).
   - A plain lock does not scale with parallel writers (Fig. 5: ReentrantLock and StampedLock "do not scale" on writes); the LLL does.
3. **The Lightweight Layout Lock** (§5.6, Listing 2), if parallel writers to one collection matter.
   - **Per collection:** an exclusive/shared base lock, a `rescan` flag, and a list of per-thread states (an AtomicInteger per thread per lock). Memory grows with the number of threads that touched the collection (§5.6, "Memory Footprint").
   - **Reader:** reads the data, a load fence, checks its own thread state is 0, and otherwise retries through the slow path.
   - **Writer:** CAS of its own state from 0 to WR, and an atomic subtract of WR at the end; if the CAS fails, the slow path takes the base lock shared.
   - **Layout change:** the base lock exclusive. If `rescan` is set, it adds the LC bit to every thread's state and spins until each writer is done. Consecutive layout changes with no read or write in between skip the scan.
   - **Thread registration:** under the exclusive lock, with the new state starting at LC.
   - Port: `Interlocked` plus `ReaderWriterLockSlim` or a custom base lock, and a per-lock thread-state slot. That slot is the expensive part in .NET (a `ConditionalWeakTable` or a `ThreadLocal` per lock). The paper does not say how TruffleRuby finds a thread's state for a given lock.
4. **The write barrier** (2016 §5.2–5.3): before storing a value into a shared object, share the value and everything it reaches; the traversal specializes on the value's shape to a bounded depth.
   - **The initial set, and where sharing starts, as the papers list them:** classes and their metaclasses, constants (top-level and in modules), global variables, standard streams, encodings, nil; 2,347 objects in JRuby+Truffle (2016 §7.4, Fig. 10).
   - Stores into fields of shared objects (§5.2); elements added to shared collections (2018 §4.2 and Fig. 1, `$global_variable = array` and `array << {b => 42}`); objects handed to external code (2016 §6.3).
   - §6.6: sharing can start only when a second thread is created; the initial set is shared then (23 ms).
   - **Not enumerated in what I read:** the block and the locals a `Thread.new` captures, `Queue#push`, class variables, `ObjectSpace`, `define_method` closures. IronRuby would have to decide each of these itself.
   - Thesis §8.3: a deeply immutable object would not need sharing.
5. **Where IronRuby stores into a reachable place** (to look at, not checked): global variables (`RubyGlobalScope`/the globals table), constants and class variables (`RubyModule`), instance variables of shared objects (`RubyInstanceData`), Array and Hash element stores, `Thread.new` and `Fiber` arguments and captured scopes, `Queue`/`SizedQueue`, the C# hosting API (`IronRuby.create_engine`, ScriptScope variables), and Marshal.

**Smallest useful first step.** Pieces 1, 2 and the barrier at `Thread.new` plus globals and constants, with a lock per shared collection. It fixes probes A and C if the barrier misses nothing.

**Cheaper and coarser alternative from the papers** (2016 §6.6 inverted): one runtime-wide "a second Ruby thread exists" flag, and every collection locked once it is set. It is correct whatever the barrier would miss, costs one static load per operation before the second thread, and then costs every collection a lock. That is the 54% "all shared" case for objects (2016 §7.3), so measure it before choosing. With several runtimes in one process the flag must belong to the `RubyContext`, not be static.

**How to measure:**
- Probes A and C go from wrong to right.
- AWFY, which is all local, must not move, in both column orders.
- A micro of `Array#<<` and `Hash#[]=` on a collection stored in a global, on one thread, gives the cost of a shared collection.

## 6. Regexp and String#index: what the sources say, and what IronRuby does

**What the sources say:**
- The regexp talk (RubyKaigi 2021) and the blog (2025-03-14) give four reasons TruffleRuby is fast: a finite automaton, JIT-compiled regexps, splitting so that each call site inlines its own compiled regexp, and SIMD (`ByteIndexOfCodePointSetNode`).
- Talk conclusion: 24–41x CRuby on `"abc".match?(re)` micro-benchmarks, 2.3–9x on liquid parsing, browser sniffing, regex-redux and syslog.
- On short matches the blog's own numbers say the fixed costs dominate: the time regexp alone is 29.9 ns on TruffleRuby and 810 ns on CRuby, while `Time.new(String)` as a whole is 255 against 627 ns, "spending a lot of time in creating and initializing the Time object".
- Neither source breaks down a short anchored match or `String#index` further.

**What IronRuby does, from reading the code, not profiling it:**
- **`Regexp#match?`:** `RubyRegexOps.IsMatch` (`Src/Libraries/Builtins/RubyRegexOps.cs:531`), then `RubyRegex.MatchFromCharacter(str, start, false)` (`RubyRegex.cs:982`). `ToSearchStart` returns at once for start 0 (`:943`). Then `Match(input, 0, false)` (`:1050`), which calls `Transform` (`ConvertToString` and `HasSurrogatePairs`, `:231-240`), then `regex.Match(str, 0)`, then `MatchData.Create`, which builds a `MatchData` for a successful match although `match?` only needs a bool.
- **`Regexp#=~`:** `RubyRegexOps.MatchIndex` (`:565`), then `RubyRegex.SetCurrentMatchData`, which sets `$~` and calls `regex.Match(str)` (`RubyRegex.cs:1006-1010`). It goes through the same `Transform` and freezes a snapshot of the input on success.
- **So on a String that does not match** (the benchmark's case), the two paths differ only in `Regex.Match(str, 0)` against `Regex.Match(str)`, and in the `[DefaultProtocol]` conversion of the argument. Reading does not explain a factor of four; it needs a profile. Places to look:
  - Whether `Match(str, startat)` takes a different search path in .NET's engine than `Match(str)` **[memory: they should be the same]**.
  - Whether the `match?` call site binds through a conversion rule each time.
  - Tier-up: `_cachedRegexUses` counts both paths (`countUse` is true in both).
- **A `match?` path with no `MatchData` and no `Match` object at all** is cheap to try either way: `Regex.IsMatch(ReadOnlySpan<char>)`, or `EnumerateMatches` as `MatchWindowBounds` already uses (`RubyRegex.cs:1174`).
- **`String#index(String)`** (`MutableStringOps.cs:3869-3884`): `GetCharacterLength(self)` before anything else, even for start 0, then `substring.PrepareForCharacterRead()`, then `self.IndexOf(substring, ToClrIndex(start))`, which is `Content.IndexIn`, then `ToCharacterIndex(result)`.
  - The surrogate flags are cached (`MutableString.HasSurrogates`, `:1076`), so the length is not an O(n) scan once known.
  - For a char-array needle, `IndexIn` calls `str.IndexOf(ToString(), ...)` (`MutableString.CharArrayContent.cs:472`), which allocates the needle as a .NET string on every call.
  - The haystack is searched as UTF-16, twice the bytes CRuby's `memchr`-style search reads over a UTF-8 String.
  - None of this obviously makes 5x. A profile of the 10 KB case should say whether the time is in the search or in a conversion of the content: `PrepareForCharacterRead` on a binary-content String, or `SwitchToBinary`/`SwitchToChars` in `IndexOf(byte[])` (`MutableString.CharArrayContent.cs:463-467`) when the needle is binary content.
  - A one-character needle could go to `ReadOnlySpan<char>.IndexOf(char)` directly.

## 7. How to measure: what the sources do that Util/perf/ does not yet
- **Report a spread with every median.** The 2022 post reports "the median as the bar heights and the median absolute deviation as error bars, for the second half of all iterations". `awfy.rb` takes the median of the second half but prints no spread; `ab.rb` prints max/min of rounds. Adding the MAD per cell to `awfy.rb` would make one-run tables like the ledger's readable without a second run.
- **End warm-up by a spread threshold, not a fixed time.** In the 2022 post the harness "keeps running more iterations until the median absolute deviation threshold is reached"; `awfy.rb` runs SECS seconds and halves.
- **A ratio is an interval** (perfer, 2012: "Execution time ratio as an interval as well"). The ledger gives point ratios, and a "1.02" between two trees should come with its range.
- **Record metadata with each measurement:** git commit, command line, a checksum of the benchmark file (perfer's metadata slide). The ledger records the commit by hand.
- **For start-up and short scripts, report seconds gained or lost, not ratios** (2018 short-scripts post), and give each implementation's maximum deviation across runs.
- **Count allocations to check that each implementation does the same work** before comparing times; lazy work hides in one implementation (2024 parsers post). `alloc.rb` exists for IronRuby; there is no cross-implementation check.
- **Frequency scaling off and the performance governor** (every post). Not possible in this container; it is one more reason for the ledger's alternating runs.

## 8. Not read
- **Speakerdeck talks not read:**
  - "Parallel and Thread-Safe Ruby ... vienna.rb edition" (likely a repeat)
  - "An Introduction to Truffle and Graal"
  - "An Efficient and Thread-Safe Object Representation for JRuby+Truffle" (the 2016 paper's talk)
  - the non-RubyKaigi "Splitting" talk
  - "Making Collections ... Thread-Safe and Efficient" (only skimmed: it follows the 2018 paper)
- **Thesis:** chapters 4–6 beyond what the papers contain; only chapter 8 (future work) and §3.3.1 were read.
- **The 2018 supplement** (thread-safe-collections-supplement.pdf: the LLL's correctness invariants; not downloaded).
- **Few Versatile vs. Many Specialized Collections:** only the first page; it is a design survey with no performance technique there.
- **Blog posts** on ruby/spec, mspec syntax, delegation in Ruby 2.7 and 3, `Thread.report_on_exception` and the TRICK entry: first screens only; none is about speed.
- **Talks found nowhere on the allowed hosts:** the RubyKaigi talks on strings (Kevin Menard's 2016 talk is cited in the parallel talk, on YouTube) and on C extensions.
