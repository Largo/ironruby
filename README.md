# IronRuby, modernized

[![CI](https://github.com/Largo/ironruby/actions/workflows/ci.yml/badge.svg?branch=modernize)](https://github.com/Largo/ironruby/actions/workflows/ci.yml)

A fork of [IronRuby](https://github.com/IronLanguages/ironruby) — Ruby on the .NET CLR —
brought back to life: it **builds and runs on .NET 8**, and it parses Ruby with
**[prism](https://github.com/ruby/prism), CRuby's own parser**, instead of the hand-ported
Ruby 1.9 grammar it shipped with in 2011.

```console
$ ./ir.sh script.rb          # prism front end, vendored stdlib, JIT and OSR on
```

```ruby
# all of this runs today
case config
in {db: {host: String => host, port: Integer => port}} then "#{host}:#{port}"
in {db: {socket:}} then socket
end

def connect(host:, port: 5432, **opts) = Client.new(host, port, **opts)

users&.filter_map { it.name if it.active? }
```

## What changed

| | before | after |
|---|---|---|
| Runtime | .NET Framework 4 | **.NET 8** (Linux, macOS, Windows) |
| Build | legacy msbuild, 12 configurations | SDK-style `dotnet build` |
| Parser | hand-ported 1.9 grammar (~13k lines) | **prism** — the parser CRuby uses |
| `RUBY_VERSION` | `1.9.2` | `4.0.0` |
| Big integers | `Microsoft.Scripting.Math` | `System.Numerics` |
| Integer | Int32, then BigInteger | Int32 → **Int64** → BigInteger |
| Execution | DLR interpreter, then IL | + a **method JIT and OSR** that specialize on observed types |

## The prism front end

Ruby source is parsed by `libprism.so` through P/Invoke, decoded from prism's binary
serialization by a **generated** C# loader, and mapped onto IronRuby's existing AST — so the
whole AstGenerator/DLR compilation pipeline works unchanged. This is the same architecture
JRuby uses for its Java loader:

```
source ──▶ libprism ──▶ binary AST ──▶ PrismLoader ──▶ PrismAstBridge ──▶ IronRuby AST ──▶ DLR
                     (pm_serialize_parse)  (generated)     (lowering)
```

`Src/Prism/generate.rb` reads prism's `config.yml` and emits 152 typed node classes plus the
deserializer, so upgrading prism is: rebuild the native library, re-run the generator, fix
what the compiler flags. The loader version-checks the serialization format at runtime.

Modern syntax with no equivalent in the 1.9-era AST is **lowered** rather than rejected:

- **pattern matching** → `===` / `deconstruct` / `deconstruct_keys` tests with capture bindings
  (guards, find patterns, `**nil`, pins, alternations, `=>` and `in`)
- **keyword arguments** → trailing optional hash + a prologue (missing-keyword `ArgumentError`,
  defaults, `**rest`)
- **safe navigation** → nil-guarded temporary
- **`it` / `_1`** → explicit block parameters, **`...`** → `*rest, &block` forwarding

See [`Src/Prism/README.md`](Src/Prism/README.md) for the details and the known gaps.

## Status

Measured with [ruby/spec](https://github.com/ruby/spec) at CRuby 4.0.6, `Util/parallel-sweep.sh`:

| suite | examples | failing |
|---|---|---|
| `spec/language` | 2934 | **0** |
| `spec/command_line` | 175 | **0** |
| `spec/security` | 34 | **0** |
| `spec/library` | 6398 | **1** (`Binding#irb`) |
| `spec/core` | 23136 | **21** — 20 of them `ObjectSpace.each_object`, which needs `-X:ObjectSpace` |
| IronRuby's own C# test suite | ~1470 | 19 known |

CRuby 4.0.6 is the oracle: where a spec and IronRuby disagree, the behaviour is checked
against `ruby` and IronRuby is changed, not the spec.

The **Ruby 4.0 standard library is vendored** in `Src/StdLib/ruby/4.0` and comes first on the
load path; the 1.9 tree sits behind it for the libraries 4.0 gemified or implements as C
extensions. A compatibility prelude
([`Src/StdLib/ironruby/ruby4.rb`](Src/StdLib/ironruby/ruby4.rb)) supplies what MRI provides
natively — `Process.clock_gettime`, `Random`, `ObjectSpace::WeakMap`, `ruby2_keywords`,
pattern-matching support classes and core methods from Ruby 2.x-4.x. `Ripper` is implemented
on prism, the way CRuby 4.0 implements it.

What is left is mostly what .NET cannot do: `fork`, a controlling TTY, `setproctitle`,
C-extension APIs (`fiddle`, `mkmf` compiling), and heap walking (`ObjectSpace.each_object`
is opt-in behind `-X:ObjectSpace`, as JRuby's is behind `-X+O`).

## Performance

Two compilers specialize on the types a program actually uses. Both are **on by default**
(`-X:NoJIT`, `-X:NoOSR` turn them off), and both fall back to the generic path rather than
guess:

- **Method JIT** — a hot method body is replaced by a copy with unboxed `int`/`long`/`double`
  locals, CLR arithmetic, no per-call scope, and direct calls for self-recursion. One integer
  compare of a global method-table version guards redefinition, override, singleton methods,
  `prepend`, `include` and `alias`; refinements switch it off; overflow and division by zero
  deopt to the generic body.
- **OSR** — a loop that has taken enough back edges is replaced *while it runs* by a
  type-specialized copy over the same locals tuple, so there is no state to materialize. This
  is what reaches a hot loop inside a block that is only ever entered once, which no
  invocation-counting JIT can see. If the types change, the copy is rebuilt.

`Util/bench/run.sh` (50 benchmarks, Release, ratio to CRuby 4.0.6 — lower is better):

| | ratio |
|---|---|
| `float_arith`, `cmp_branch`, `int_arith`, `mandelbrot`, `fib`, `while_loop` | **0.27 – 0.74** (faster than CRuby) |
| calls, ivars, string and array work | 2 – 4 |
| geomean over all 50 | **3.25** |
| `raise_rescue`, `fiber_switch` | 150+ (backtrace construction; a CLR thread per Fiber) |

Build with `-c Release` and run with `IR_CONFIG=Release ./ir.sh`: the optimized build is
~1.8x faster than the default Debug build across the whole suite. See
[`Util/bench/README.md`](Util/bench/README.md).

## Building

Requires the .NET SDK, plus Ruby and a C compiler to build prism.

```console
$ git clone https://github.com/IronLanguages/dlr ../dlr          # modern DLR, referenced as source
$ git clone https://github.com/ruby/prism ../prism
$ (cd ../prism && ruby templates/template.rb && make shared)     # libprism.so
$ (cd Src/Prism && ruby generate.rb)                             # C# nodes + loader
$ dotnet build Src/Console/Ruby.Console.csproj
```

Running the conformance suite:

```console
$ git clone https://github.com/ruby/spec && git clone https://github.com/ruby/mspec
$ RUBY_EXE=./ir.sh ./ir.sh -Imspec/lib mspec/bin/mspec-run spec/language
$ Util/parallel-sweep.sh out            # all five suites, 8 at a time, ~6 minutes
$ Util/run-tests.sh                     # IronRuby's own C# tests
$ Util/bench/run.sh                     # benchmarks against CRuby
```

## License

Apache License 2.0, as the original. See `Public/License.html`.
The original content description is preserved in [`README.txt`](README.txt).
