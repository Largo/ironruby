# -X:JITMethods: the method JIT's code as real methods (a prototype, and its verdict)

`-X:JIT` builds a typed body as a `System.Linq.Expressions` lambda and calls `Compile()`, which
makes a `DynamicMethod` behind a delegate. The body's call to itself (`fib(n - 1)` in `def fib`,
the only Ruby-to-Ruby call the JIT binds) is a delegate invocation through a cell, and the
.NET JIT can never inline one compiled Ruby method into another.

`-X:JITMethods`, off by default, emits the same tree into a static method of a type in a runtime
`AssemblyBuilder`, and the self-call becomes an IL `call` of that method. It exists to answer
one question: how much of what a C# microbenchmark promised for this survives inside IronRuby.

    ./ir.sh -X:JITMethods script.rb            # one AssemblyBuilderAccess.Run assembly
    ./ir.sh -X:JITMethods=collect script.rb    # a RunAndCollect assembly per body
    ./ir.sh -X:JITMethods=dynamic script.rb    # control: still a DynamicMethod, but it calls itself by `call`
    ./ir.sh -X:JITMethods=run,entry script.rb  # the entry (guards, unboxing) is emitted too

Further options, for experiments: `tiered` (leave the methods to tiered compilation; by default
they are `AggressiveOptimization`, compiled fully optimized at once as a DynamicMethod is) and
`readonly` (the lifted constants are `static readonly`, assigned by a type initializer).

It needs `IronRuby.JitEmit.dll` next to `IronRuby.dll`:

    dotnet build Src/Console/Ruby.Console.csproj -c Release -p:IronRubyJitEmit=true ...

Without that file the switch warns once and compiles as before.

## How it is built

- **The tree compiler.** `System.Linq.Expressions` on .NET has no `CompileToMethod`. The port of
  the .NET Framework `LambdaCompiler` in `Util/aot/IronRuby.Aot/ExpressionCompiler` has. It is
  reached through an optional assembly and not moved into `IronRuby.dll`: `Src/JitEmit` compiles
  the same source files (linked, one copy) into `IronRuby.JitEmit.dll`, and
  `JitMethodEmitter` loads that from `IronRuby.dll`'s directory the first time the switch needs
  it and binds one public method, `ExpressionCompiler.CompileToMethod(lambda, methodBuilder,
  substitutions)`. Why: with the switch off `IronRuby.dll` gains nothing but the 410-line
  emitter, so nothing that ships or is tested changes; the AOT tool and the JIT share one
  compiler; and no `InternalsVisibleTo` is needed. The price is that the port is itself
  JIT-compiled cold at its first use (measured below), which a product would not accept: it
  would move the port into `IronRuby.dll` or precompile it with the R2R host.
- **The self-call.** A tree cannot name a `MethodBuilder` (`Expression.Call` asks it for its
  parameters, which it cannot answer before its type is created). The emitter rewrites the
  self-call to a call of a generic stand-in closed over the body's signature
  (`JitMethodEmitter.SelfCallN`), and the port emits a call to the `MethodBuilder` where the tree
  names the stand-in. `dynamic` uses the port's DynamicMethod path with one more hook
  (`CompileDynamic`): a call of the stand-in is a call of the DynamicMethod being compiled, its
  closure passed along. About 60 lines added to the port, nothing changed for the AOT tool.
- **Live constants.** IL cannot hold an object. Every constant that is not an IL literal - the
  `RubyContext`, instance-variable and attribute sites, the pinned class, the Array class, a
  Symbol, a boxed literal, the `Specialization`, the generic body's delegate - becomes a public
  static field of the emitted type, assigned by reflection after `CreateType` and before the
  delegate is handed out (`Lifter`).
- **Access.** The emitted code calls `JitRuntime`'s internal helpers. A DynamicMethod skips
  visibility checks; the emitted assembly carries `IgnoresAccessChecksTo("IronRuby")` instead.
- **Backtraces.** The method keeps the lambda's encoded name, so a frame of an emitted body is
  the same Ruby frame in a backtrace; an emitted entry is hidden as a DynamicMethod entry is.

## What still holds, and what is not covered

Every guard is where it was, in the entry: the global method version, the constant version, the
receiver class pin for a body with a self-call, the argument type checks, the tracing check, and
the `JitDeoptException` handler with its deopt-rate accounting. The emitted body is the same
tree; only how it is called from itself changed. Nothing known is unsound.

`Util/jit-matrix.rb` (redefinition, a subclass override, singleton methods, refinements,
Integer#+ redefined, Int32/Int64/Bignum growth, division by zero) prints the same 542 lines as
CRuby 4.0.9 with the switch off and in every mode; 7 bodies are emitted (182 methods with
`entry`), none falls back. A `SystemStackError` out of an emitted body and a `ZeroDivisionError`
through one have the backtraces they have with the switch off.

IronRuby.Tests (1,252 tests, Debug, net10.0) passes with the switch off, which is all it
runs: none of its tests uses the switch.

Not covered: the entry that `JitKeywordCall` calls (`BuildDirectEntry`) and `-X:OSR`'s loops are
still DynamicMethods, and a body with no self-call is part of its entry, so only `entry` emits
it.

## Results

Release build, .NET 10.0.15, Xeon E3-1275 v5 (4 cores, 8 threads), a machine other work was
running on (load average 3 to 5 during these runs). `Util/bench/jit-methods-run.rb`: every run a
fresh process pinned with `taskset -c 5,6`, the modes taking turns, `dotnet build-server
shutdown` first. The seconds are **CPU time of the benchmark's thread**, not wall time: an
earlier pass on the wall clock was ruined when the load went to 18. A cell is the best of the
runs (5 unless a table says 7), the median in parentheses, and the best as a multiple of
"off". Every run's value was compared with CRuby 4.0.9's; all match.

### Methods that call themselves, once compiled

The fastest of the four later runs of the timed section in one process
(`Util/bench/jit-methods.rb`), so the code and nothing of its compilation.

| benchmark = value | CRuby | off | dynamic | run | collect | run,entry | collect,entry | run,tiered | run,readonly |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| fib35 = 9227465 | 1.036 | 0.170 (0.211) | 0.120 (0.193) **0.71x** | 0.096 (0.120) **0.56x** | 0.106 (0.115) **0.62x** | 0.090 (0.123) **0.53x** | 0.104 (0.127) **0.61x** | 0.069 (0.088) **0.41x** | 0.104 (0.134) **0.61x** |
| ackermann = 49008 | 1.176 | 0.191 (0.196) | 0.121 (0.127) **0.64x** | 0.120 (0.132) **0.63x** | 0.124 (0.133) **0.65x** | 0.120 (0.132) **0.63x** | 0.122 (0.132) **0.64x** | 0.088 (0.100) **0.46x** | 0.121 (0.128) **0.64x** |
| tak = 144 | 1.396 | 0.188 (0.192) | 0.136 (0.152) **0.72x** | 0.124 (0.128) **0.66x** | 0.128 (0.128) **0.68x** | 0.124 (0.128) **0.66x** | 0.136 (0.136) **0.72x** | 0.112 (0.116) **0.60x** | 0.120 (0.124) **0.64x** |
| float_rec = 1599999.9999756534 | 1.036 | 0.136 (0.144) | 0.120 (0.124) **0.88x** | 0.112 (0.120) **0.82x** | 0.116 (0.120) **0.85x** | 0.115 (0.122) **0.85x** | 0.124 (0.132) **0.91x** | 0.101 (0.112) **0.74x** | 0.116 (0.123) **0.85x** |
| array_rec = 804000000 | 0.316 | 0.260 (0.268) | 0.248 (0.260) **0.96x** | 0.242 (0.248) **0.93x** | 0.256 (0.272) **0.98x** | 0.236 (0.252) **0.91x** | 0.252 (0.260) **0.97x** | 0.210 (0.224) **0.81x** | 0.250 (0.256) **0.96x** |
| case_rec = 12000000 | 0.492 | 0.125 (0.136) | 0.119 (0.124) **0.95x** | 0.115 (0.116) **0.92x** | 0.121 (0.128) **0.97x** | 0.116 (0.119) **0.93x** | 0.116 (0.128) **0.93x** | 0.088 (0.088) **0.70x** | 0.110 (0.112) **0.88x** |

The kernel counts a thread's CPU time in 4 ms ticks, so a difference of one tick (3% of 0.12)
is not a difference. In another pass of seven runs fib35 was 0.152 off and 0.085 run.

.NET 11.0.0 says the same (same harness, best of 5):

| benchmark = value | CRuby | off | dynamic | run | collect | run,tiered |
|---|---:|---:|---:|---:|---:|---:|
| fib35 = 9227465 | 1.012 | 0.152 (0.163) | 0.104 (0.114) **0.69x** | 0.088 (0.092) **0.58x** | 0.104 (0.108) **0.68x** | 0.068 (0.072) **0.45x** |
| ackermann = 49008 | 1.236 | 0.184 (0.196) | 0.128 (0.129) **0.70x** | 0.120 (0.132) **0.65x** | 0.120 (0.128) **0.65x** | 0.088 (0.096) **0.48x** |
| tak = 144 | 1.403 | 0.193 (0.200) | 0.152 (0.160) **0.79x** | 0.124 (0.124) **0.64x** | 0.124 (0.129) **0.64x** | 0.112 (0.114) **0.58x** |
| case_rec = 12000000 | 0.492 | 0.124 (0.127) | 0.112 (0.115) **0.90x** | 0.107 (0.112) **0.86x** | 0.119 (0.120) **0.96x** | 0.079 (0.088) **0.64x** |

What the columns say:

- **The direct call is worth 1.5x to 1.8x on a method that is mostly its own call** (fib,
  ackermann, tak), and 5 to 20% on one that does something else per call as well.
- **Most of that is the call, not the assembly.** `dynamic` - still a DynamicMethod, calling
  itself by `call` - gets all of it on ackermann and about two thirds of it on fib and tak.
- **`collect` is `dynamic`, more or less; `run` is a little better.** A collectible assembly's
  methods are compiled fully optimized at once and never tiered, exactly like a
  DynamicMethod's (the JIT's listing says `FullOpts`).
- **What only a non-collectible assembly gets is tiered compilation, and that is the larger
  half.** `run,tiered` reaches 0.41x on fib and 0.46x on ackermann, and moves the benchmarks the
  call alone barely touches (case_rec 0.70x, array_rec 0.81x). At Tier 1 the .NET JIT has a
  profile, and with it **it inlines `fib` into itself**: the Tier-1 code of the emitted `fib` has
  four calls of `fib` and three stack checks in it, where the fully optimized one has two calls
  and one check. This is the first time a compiled Ruby method has been inlined into a compiled
  Ruby method here.
- `entry` and `readonly` change nothing measurable for these.

### The same, first run in the process

The first run of the timed section: it has the method's profiling, its compilation and, with
`tiered`, the time its code spends unoptimized.

| benchmark = value | CRuby | off | dynamic | run | collect | run,entry | collect,entry | run,tiered | run,readonly |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| fib35 = 9227465 | 1.020 | 0.221 (0.296) | 0.183 (0.259) **0.83x** | 0.163 (0.235) **0.74x** | 0.179 (0.230) **0.81x** | 0.168 (0.230) **0.76x** | 0.185 (0.207) **0.84x** | 0.266 (0.290) **1.20x** | 0.167 (0.226) **0.76x** |
| ackermann = 49008 | 1.224 | 0.224 (0.231) | 0.196 (0.199) **0.87x** | 0.192 (0.207) **0.86x** | 0.195 (0.242) **0.87x** | 0.206 (0.211) **0.92x** | 0.211 (0.223) **0.94x** | 0.258 (0.272) **1.15x** | 0.184 (0.199) **0.82x** |
| tak = 144 | 1.452 | 0.218 (0.219) | 0.199 (0.205) **0.91x** | 0.171 (0.177) **0.78x** | 0.187 (0.198) **0.86x** | 0.180 (0.187) **0.82x** | 0.190 (0.196) **0.87x** | 0.284 (0.295) **1.30x** | 0.171 (0.184) **0.78x** |
| float_rec = 1599999.9999756534 | 1.016 | 0.231 (0.250) | 0.240 (0.250) **1.04x** | 0.219 (0.265) **0.95x** | 0.235 (0.240) **1.02x** | 0.229 (0.259) **0.99x** | 0.247 (0.259) **1.07x** | 0.262 (0.281) **1.13x** | 0.228 (0.241) **0.99x** |
| array_rec = 804000000 | 0.332 | 0.483 (0.541) | 0.460 (0.486) **0.95x** | 0.485 (0.513) **1.00x** | 0.460 (0.500) **0.95x** | 0.488 (0.510) **1.01x** | 0.464 (0.514) **0.96x** | 0.439 (0.474) **0.91x** | 0.488 (0.513) **1.01x** |
| case_rec = 12000000 | 0.560 | 0.311 (0.318) | 0.306 (0.333) **0.98x** | 0.283 (0.347) **0.91x** | 0.304 (0.364) **0.98x** | 0.336 (0.359) **1.08x** | 0.324 (0.366) **1.04x** | 0.292 (0.312) **0.94x** | 0.289 (0.307) **0.93x** |

Two costs show here:

- **The first method compiled with the switch costs about 26 ms more** (`IR_JIT_VERBOSE=1`
  reports 17-18 ms of compilation for ackermann without the switch and 43-46 ms with it, 47-54
  with `entry`). That is the port being JIT-compiled itself, the assembly load and
  Reflection.Emit warming up - once per process. After it a method costs what it costs without
  the switch: 300 hot methods take 2.0 ms each off, 2.0 `run`, 2.3 `collect`, 1.5 `run,entry`.
- **`tiered` starts unoptimized.** The method runs Tier-0 code until the runtime's call-counting
  delay (100 ms) and a background compile have passed, which makes a 0.2 s first run 15-30%
  slower than without the switch. (`DOTNET_TC_CallCountingDelayMs=0` is not the way out: it made
  every later run five times slower.)

### Util/bench/bench.rb

Its benchmarks run once per process, so this is a first run, and its `fib` and `ackermann` take
20-30 ms in all: the one-time 26 ms doubles them. The `call_*` and `ivar_set` loops return nil
(in CRuby too); their methods do not call themselves, so only `entry` emits anything for them.
Best of 7.

| benchmark = value | CRuby | off | dynamic | run | collect | run,entry | collect,entry |
|---|---:|---:|---:|---:|---:|---:|---:|
| bench:fib = 514229 | 0.048 | 0.029 (0.034) | 0.052 (0.055) **1.77x** | 0.052 (0.058) **1.76x** | 0.052 (0.055) **1.78x** | 0.057 (0.063) **1.95x** | 0.056 (0.060) **1.91x** |
| bench:ackermann = 1021 | 0.020 | 0.023 (0.027) | 0.048 (0.052) **2.07x** | 0.048 (0.053) **2.06x** | 0.048 (0.053) **2.08x** | 0.053 (0.059) **2.30x** | 0.054 (0.059) **2.33x** |
| bench:call_1arg = nil | 0.060 | 0.184 (0.205) | | | | 0.229 (0.255) **1.25x** | 0.199 (0.251) **1.08x** |
| bench:call_3arg = nil | 0.072 | 0.188 (0.240) | | | | 0.238 (0.263) **1.27x** | 0.245 (0.256) **1.31x** |
| bench:ivar_set = nil | 0.080 | 0.168 (0.209) | | | | 0.216 (0.236) **1.29x** | 0.204 (0.234) **1.22x** |

### Methods that do not call themselves

Steady state again (`jit-methods.rb`, best of 7). Emitting the entry buys nothing: these are
6 million calls from generic code, and the 80 ns each takes are the call site and the
trampoline, not the entry.

| benchmark = value | CRuby | off | run,entry | collect,entry |
|---|---:|---:|---:|---:|
| call_arith = 2094259779520 | 0.336 | 0.490 (0.536) | 0.536 (0.564) **1.09x** | 0.543 (0.562) **1.11x** |
| call_ivar_get = 21000000 | 0.280 | 0.453 (0.498) | 0.468 (0.489) **1.03x** | 0.463 (0.502) **1.02x** |
| call_ivar_set = 5999999 | 0.228 | 0.199 (0.215) | 0.188 (0.208) **0.95x** | 0.192 (0.196) **0.97x** |

### Against the microbenchmark

The C# microbenchmark this follows, run again on this machine (.NET 10, fib(35), best of 7):
the same code in C# 35 ms; an expression tree calling itself through a cell, as today, 118 ms
(3.4x); a DynamicMethod calling itself 68 ms (1.9x); a method in a RunAndCollect assembly 59 ms
(1.7x); in a Run assembly 36 ms (1.0x).

| fib(35), ms | microbenchmark | IronRuby |
|---|---:|---:|
| C# | 35 | |
| tree, self-call through the cell (off) | 118 | 152-170 |
| DynamicMethod calling itself (`dynamic`) | 68 | 116-120 |
| RunAndCollect assembly (`collect`) | 59 | 106-108 |
| Run assembly (`run`) | 36 | 85-96 |
| Run assembly, tiered (`run,tiered`) | | 69 |

(IronRuby: the two passes on .NET 10.) So the saving survives in milliseconds - 36-50 of the
microbenchmark's 50 for a DynamicMethod, 44-64 of 59 for a collectible assembly, 67-74 of 82 for
a Run assembly, and about 100 once tiering is let in - and the order of the variants is the
same. As a ratio it is smaller (1.8x where the
microbenchmark has 3.3x, 2.5x tiered), because IronRuby's `fib` does more per call than C#'s:
a stack check, Int32 arguments widened and range-checked, an overflow-checked add that the JIT
does not inline without a profile. The best mode is still 2.0x the C#.

The microbenchmark's other number - a loop calling a tiny `add` 200 million times, 5-8x of C#
as a tree and 1x in either kind of assembly because the callee is inlined - has no
counterpart to measure: the JIT binds no call from one Ruby method to another.

## Unloading and leaks

3,000 redefinitions of a hot recursive method (`eval "def walk..."`, then 60 calls), growth
per redefinition after forced collections:

| | managed heap | private bytes | emitted code still loaded |
|---|---:|---:|---|
| off | 1.2 KB | 17.2 KB | |
| `dynamic` | 1.3 KB | 18.0 KB | |
| `run` | 3.0 KB | 23.5 KB | 3,000 types, for good |
| `collect` | 1.2 KB | 18.8 KB | 143 of 3,050 assemblies (192 before the forced collections) |
| `run,entry` | 28.7 KB | 48.4 KB | 6,000 types, for good |

- **`run`** A method in a Run assembly is never unloaded, so every body the JIT emits stays:
  about 6 KB each, beyond what a redefinition costs without the switch. A body is emitted per
  specialization - a method's first, up to 8 more after method-table changes
  (`MaxRespecializations`), one per number of optional arguments passed - so a program with a
  fixed set of methods leaks a bounded amount, and one that keeps defining methods (code
  reloading, `eval` in a loop) leaks without bound. Worse than the code are the **static
  fields: they root their constants for the life of the process**. With `entry` those include
  the `Specialization`, so the whole superseded definition stays (stub, syntax tree, generic
  body: 31 KB a method); in every mode they include the `RubyContext`, so a runtime that a host
  has shut down is never collected once one of its methods was emitted.
- **`collect`** An assembly goes when nothing refers to it, and that works: of 3,050, 192 were
  loaded at the end and 143 after forced collections. Those 143 are what the DLR's rule cache
  still held of old definitions; their DynamicMethods are held the same way without the
  switch. The price is the larger half of the gain (no tiering, so no profile and no
  inlining), an assembly per body (0.3 ms more to make, and about 50 KB each while alive: the
  93 more of them loaded at the end were 4.8 MB of private memory), and slower access to the
  lifted constants.
- **`dynamic`** has nothing to unload.

## Verdict

**As it stands, no.** What the JIT can turn into a direct call today is a method's call to
itself. That is worth 1.5-1.8x on recursive microbenchmarks, nothing anywhere else, 26 ms at
the first compile, and with `run` a leak. If that gain were wanted on its own, `dynamic` is the
way to have it: most of the gain, nothing to unload, and the same compiler dependency.

**As the base of the next step, yes - the Run assembly, tiered, and not the collectible one.**
The measurement that matters is not the call but what came with it: in a non-collectible
assembly the .NET JIT tiers the method, profiles it and inlines one compiled Ruby method into
another, which no DynamicMethod and no collectible assembly gets. That is what took `fib` from
0.56x to 0.41x and moved the benchmarks the call alone did not. A collectible assembly buys
unloading at the price of exactly that, so it is the wrong half to keep.

The next step is the one the inlining would pay for: **binding a call to another compiled Ruby
method directly.**

1. In `JitCompiler.EmitCall`, a call on self to a method whose body the JIT can type for the
   argument types at hand becomes a `call` of that body (compiled on demand, emitted into the
   same assembly), with a typed result - an Integer or Float crosses the call unboxed, which
   may be worth as much as the inlining. The guards are the ones the self-call has: the entry
   pins the receiver class and the global method version, so the name cannot have come to
   mean anything else.
2. Deopt and purity cross the call: a callee that can deopt makes its caller one that can, a
   callee with a side effect makes its caller impure, and the pair is then rejected as a body
   with both is today. A callee needs the stack check a self-calling body has.
3. Emit as a second tier, not instead of the first. A method is specialized as today, as a
   DynamicMethod, and promoted to the Run assembly once it has stayed specialized and hot:
   that bounds the leak to methods that have shown they last (with a byte budget after which
   nothing is promoted), and keeps the Tier-0 phase and the 26 ms away from short programs.
   Entries stay DynamicMethods; emitting them gains nothing and pins the most.
4. The constants need an owner that dies with the runtime: fields the runtime clears at
   shutdown, or one indirection through a per-runtime pool, so that an emitted method does not
   keep a `RubyContext` alive.
5. The port has to be precompiled (in `IronRuby.dll` with the R2R host, or its own R2R image),
   or the first promotion costs 26 ms.
6. A backtrace through an inlined callee has no frame for it. A typed body raises only by
   deopting (which re-runs the generic body) or from the stack check, so this is small, but it
   has to be decided before the .NET JIT is allowed to inline bodies that can raise.

Then measure the shape the microbenchmark could and this prototype could not: a loop calling a
small method.

## Seen on the way

- Without the switch, redefining a hot method 3,000 times grows the process by 17 KB of
  private memory a time (1.2 KB of it managed heap).
- `JitRuntime.AddLong` stays a call in `fib`'s typed body, with or without the switch.
