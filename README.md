# IronRuby, modernized

[![CI](https://github.com/Largo/ironruby/actions/workflows/ci.yml/badge.svg?branch=modernize)](https://github.com/Largo/ironruby/actions/workflows/ci.yml)

A fork of [IronRuby](https://github.com/IronLanguages/ironruby) — Ruby on the .NET CLR —
brought back to life: it **builds and runs on .NET 8**, and it parses Ruby with
**[prism](https://github.com/ruby/prism), CRuby's own parser**, instead of the hand-ported
Ruby 1.9 grammar it shipped with in 2011.

```console
$ ir -X:UsePrism -ISrc/StdLib/ironruby -ISrc/StdLib/ruby/1.9.1 script.rb
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

| suite | result |
|---|---|
| [ruby/spec](https://github.com/ruby/spec) `spec/language` (via mspec) | **2117 / 2682 pass (78.9%)** |
| IronRuby's own C# test suite | ~1470 pass, 22 known failures |
| Parsing the bundled standard libraries | **154 / 154** (Ruby 4.0) and **571 / 571** (1.9) |
| Loading the Ruby 4.0 libraries | **24 / 24** |

`spec/core` and `spec/library` have been measured for the first time and are in
much rougher shape than the syntax suite — roughly 540 of 3417 core examples
passing across the 22 directories measured so far. That is where the work is now.

The **Ruby 4.0 standard library is vendored** in `Src/StdLib/ruby/4.0` and comes first on the
load path; the 1.9 tree sits behind it for the libraries 4.0 gemified or implements as C
extensions. A compatibility prelude
([`Src/StdLib/ironruby/ruby4.rb`](Src/StdLib/ironruby/ruby4.rb)) supplies what MRI provides
natively — `Process.clock_gettime`, `Random`, `ObjectSpace::WeakMap`, `ruby2_keywords`,
pattern-matching support classes and core methods from Ruby 2.x-4.x.
The syntax is current; the runtime and library are where the remaining work is. The largest
single gap: the block dispatcher has no notion of **optional block parameters**, so
`->(x = 1) {}` reaches its body with `x` unset rather than defaulted (arity is right, and a
supplied argument binds correctly). After that: `defined?` edge cases, predefined globals,
and magic-comment encodings.

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
```

## License

Apache License 2.0, as the original. See `Public/License.html`.
The original content description is preserved in [`README.txt`](README.txt).
