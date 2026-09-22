# IronRuby, modernized

[![CI](https://github.com/Largo/ironruby/actions/workflows/ci.yml/badge.svg?branch=modernize)](https://github.com/Largo/ironruby/actions/workflows/ci.yml)

A fork of [IronRuby](https://github.com/IronLanguages/ironruby) — Ruby on the .NET CLR —
brought back to life: it **builds and runs on .NET 8**, and it parses Ruby with
**[prism](https://github.com/ruby/prism), CRuby's own parser**, instead of the hand-ported
Ruby 1.9 grammar it shipped with in 2011.

```console
$ ./irb.sh
irb(main):001> RUBY_DESCRIPTION
=> "IronRuby 1.2.0-dev (4.0.0) on .NET 8.0.31 [x86_64-linux]"
irb(main):002> def fib(n) = n < 2 ? n : fib(n - 1) + fib(n - 2)
=> :fib
irb(main):003> (1..10).map { fib(_1) }
=> [1, 1, 2, 3, 5, 8, 13, 21, 34, 55]
irb(main):004> require "io/console"; IO.console.winsize
=> [44, 80]
irb(main):005> Point = Struct.new(:x, :y)
=> Point
irb(main):006> case Point.new(3, 4)
irb(main):007*   in [Integer => x, Integer => y]
irb(main):008*     Math.hypot(x, y)
irb(main):009*   end
=> 5.0
irb(main):010> 1 / 0
(irb):10:in 'Integer#/': Attempted to divide by zero. (ZeroDivisionError)
        from (irb):10:in '<main>'
```

That is **irb 1.16.0 and reline 0.6.3**, the same ones CRuby 4.0 ships — with syntax
highlighting, auto-indent, Tab completion, history and `ls`/`show_source` — running on
IronRuby. `io/console` is a C extension in CRuby, so IronRuby implements it over termios.

Scripts run the same way, and gems install with real TLS:

```console
$ ./ir.sh script.rb          # prism front end, vendored stdlib, JIT and OSR on
$ ./igem.sh install rack     # or: ./ir.sh -S gem install rack
$ ./ir.sh -S bundle install
```

`ir` / `igem` / `iirb` are the i-prefixed names IronRuby has used since 1.x, so they can
sit on `PATH` beside CRuby's `ruby` and `gem` — the same reason JRuby ships `jruby`,
`jgem` and `jirb`. The unprefixed `gem`, `irb`, `bundle` and `bundler` live in
`Src/StdLib/bin` and are what `ir -S <name>` finds.

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
| REPL | irb from 2011 | **irb 1.16.0 + reline**, on a termios `io/console` |
| Gems | RubyGems 1.3.7 (2010) | **RubyGems 4.0.16 + Bundler**, installing over real TLS; `gem install rails` resolves without compiling C |

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
| `spec/library` | 6398 | **0** |
| `spec/core` | 23136 | **21** — 20 of them `ObjectSpace.each_object`, which needs `-X:ObjectSpace` |
| IronRuby's own C# test suite | ~1470 | 19 known |

CRuby 4.0.6 is the oracle: where a spec and IronRuby disagree, the behaviour is checked
against `ruby` and IronRuby is changed, not the spec.

The same tree **runs on Windows**, where `spec/language` and `spec/security` are also at
zero; see [Windows](#windows) for how to build it and what is still missing there.

The **Ruby 4.0 standard library is vendored** in `Src/StdLib/ruby/4.0` and comes first on the
load path; the 1.9 tree sits behind it for the libraries 4.0 gemified or implements as C
extensions. A compatibility prelude
([`Src/StdLib/ironruby/ruby4.rb`](Src/StdLib/ironruby/ruby4.rb)) supplies what MRI provides
natively — `Process.clock_gettime`, `Random`, `ObjectSpace::WeakMap`, `ruby2_keywords`,
pattern-matching support classes and core methods from Ruby 2.x-4.x. `Ripper` is implemented
on prism, the way CRuby 4.0 implements it, and `io/console` over termios, so the current
**irb** and **reline** run unmodified.

What is left is mostly what .NET cannot do: `fork`, a controlling TTY, `setproctitle`,
C-extension APIs (`fiddle`, `mkmf` compiling), and heap walking (`ObjectSpace.each_object`
is opt-in behind `-X:ObjectSpace`, as JRuby's is behind `-X+O`).

### Default gems

The libraries IronRuby implements in C# - `bigdecimal`, `json`, `psych`, `openssl`,
`zlib`, `stringio`, `strscan`, `digest`, `date`, `etc`, `fcntl`, `io/console`, `prism` -
are advertised to RubyGems as **default gems**, with gemspecs in
`Src/StdLib/ruby/gems/4.0.0/specifications/default` generated by
[`Util/gen-default-gemspecs.rb`](Util/gen-default-gemspecs.rb).  Without them the resolver
does not know the library is already there: `gem install activesupport` used to download
`bigdecimal`'s C sources and try to compile them.  This is what JRuby does for the same
reason.  The versions in the table are checked against the running interpreter's own
`VERSION` constants when the gemspecs are generated, so they cannot drift.

A C extension is never built: there is no `ruby.h` to compile against, and the gem whose
extension is missing is often in a read-only host CRuby tree.  Such a gem is left alone -
its `.rb` files load, a real `require` of the `.so` raises `LoadError`, and the pure-Ruby
fallback most of them carry is used instead.

### Rails

A Rails 8.1 application boots and serves HTTP requests through Rack:

```
RAILS GET /hello       -> 200 "Hello world from Rails 8.1.3.1 on ironruby 4.0.0"
RAILS GET /hello/Iron  -> 200 "Hello IronRuby from Rails 8.1.3.1 on ironruby 4.0.0"
```

ActiveSupport, ActiveModel, ActionDispatch, ActionController, ActionView (ERB templates
through the real resolver), ActiveJob, ActiveRecord and railties all install and load.
Two things stop a *complete* Rails app, and neither is a Ruby-level bug:

- **nokogiri** is a C extension (libxml2 + gumbo) and a hard dependency of
  `rails-html-sanitizer` -> `loofah` -> `actionview`, and of `rails-dom-testing` ->
  `actionpack`.  Everything above was measured with a load-only stub in its place; the
  sanitize helpers, the dom assertions and the HTML error page need a real implementation.
- **ActiveRecord has no adapter**: `sqlite3`, `pg`, `mysql2` and `trilogy` are all native.

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

## Platforms

Linux is the primary target and the one the numbers above are measured on. **Windows works**:
`ir.cmd` and `irb.cmd` run, and `spec/language` (2920 examples) and `spec/security` pass with
**0 failures**. `spec/core` and `spec/library` are rougher there — symlinks/chmod/umask in
`core/file`, fork and signals in `core/process`, and `IO.select`/`IO#wait` on pipes (which is
`poll(2)`, so `core/io` and the socket suites hang rather than fail). `zlib`, Win32OLE and
readline are not implemented on Windows; `IO.console` is nil, so irb falls back to its ANSI
input path. macOS is untested.

## Building

Requires the .NET SDK, plus Ruby and a C compiler to build prism.

```console
$ git clone https://github.com/IronLanguages/dlr ../dlr          # modern DLR, referenced as source
$ git clone https://github.com/ruby/prism ../prism
$ (cd ../prism && ruby templates/template.rb && make shared)     # libprism.so
$ (cd Src/Prism && ruby generate.rb)                             # C# nodes + loader
$ dotnet build Src/Console/Ruby.Console.csproj
```

The prism revision is pinned: `Generated/` is produced from a particular `config.yml`, and
a `libprism` built from a different one deserializes into the wrong fields. See
[`Src/Prism/README.md`](Src/Prism/README.md) for the revision and how to check a checkout.

Running the conformance suite:

```console
$ git clone https://github.com/ruby/spec && git clone https://github.com/ruby/mspec
$ RUBY_EXE=./ir.sh ./ir.sh -Imspec/lib mspec/bin/mspec-run spec/language
$ Util/parallel-sweep.sh out            # all five suites, 8 at a time, ~8 minutes
$ Util/run-tests.sh                     # IronRuby's own C# tests
$ Util/bench/run.sh                     # benchmarks against CRuby
$ Util/gems/run.sh                      # default/bundled gems, each with its own suite
```

### Windows

Windows needs `prism.dll` instead of `libprism.so`, and nothing else: `ir.exe` is an
ordinary framework-dependent .NET 8 app, so it can be published from Linux and run on a box
that has only the **runtime** installed. Build prism with the RubyInstaller DevKit's MinGW
(`make shared` names its output after RbConfig's `SOEXT`, so there it is `libprism.dll`):

```
C:\> git clone https://github.com/ruby/prism C:\prism
C:\> cd C:\prism && git checkout 531cd5e~1
C:\prism> ruby templates\template.rb
C:\prism> ridk exec make shared -j4          :: -> C:\prism\build\libprism.dll
```

Copy `libprism.dll` next to `libprism.so` in `../prism/build` and build or publish for
Windows; `IronRuby.Prism.csproj` ships whichever shared libraries are there and
`PrismNative` loads the one for the platform it wakes up on:

```console
$ dotnet build Src/Console/Ruby.Console.csproj -c Release -r win-x64 --self-contained false
```

Copy `Src/Console/bin/Release/net8.0/win-x64/`, `Src/StdLib/`, `ir.cmd` and `irb.cmd` to the
Windows machine, keeping the same relative layout (`ir.cmd` finds the binaries under
`Src\Console\bin\%IR_CONFIG%\net8.0\`, with or without a `win-x64` level). Then:

```
C:\ir> set IR_CONFIG=Release
C:\ir> ir.cmd -e "puts RUBY_DESCRIPTION"
IronRuby 1.2.0-dev (4.0.0) on .NET 8.0.31 [x64-mswin64]
C:\ir> irb.cmd
C:\ir> set RUBY_EXE=C:\ir\ir.cmd
C:\ir> ir.cmd -Imspec/lib mspec/bin/mspec-run spec/language
```

`ir.cmd` is the twin of `ir.sh`; note that `-X:StdLib` is split on the platform's path
separator, so its argument is `;`-separated on Windows (a `:`-separated list would be cut
apart at every drive letter).

Conformance on Windows 11 x64 (.NET 8.0.31), same ruby/spec revision as the Linux table
above. Some suites are run a directory at a time there because three of them hang (see
below), which the whole-suite numbers on Linux do not need:

| suite | examples | Windows | Linux |
|---|---|---|---|
| `spec/language` | 2920 | **0 failures, 0 errors** | 0 |
| `spec/security` | 32 | **0** | 0 |
| `spec/command_line` | 167 | 13 failures, all of them the *spec's* Unix shell syntax — `-e 'code'` (cmd.exe does not treat `'` as a quote) and `2> /dev/null` | 0 |
| `spec/library` | 3902 | 1 failure, 193 errors; `io-wait`, `net-http` and `socket` hang | 0 |
| `spec/core` | 20999 | 74 failures, 172 errors | 21 errors, all `ObjectSpace` |

Where the Windows errors are: `core/file` (113) and `core/filetest` (8) are symlinks,
`chmod`/`umask` and the other Unix file metadata; `core/process` (36 failures, 15 errors) is
`fork`, process groups, uid/gid and signals; `library/win32ole` (114) and `library/readline`
(25) are libraries IronRuby does not implement and whose specs only run on Windows;
`library/zlib` (42) is the `LoadError` below. `core/objectspace` needs `-X:ObjectSpace`, as
on Linux.

What Windows does not have yet: **`zlib`** (`require "zlib"` is a clean `LoadError` — there
is no libz to bind to, and the `z_stream` binding is laid out for an LP64 C compiler, so a
stray `zlib1.dll` would be read through the wrong struct), **`io/console`** (`IO.console` is
`nil`, the termios binding being Unix-only by construction; irb and reline fall back to
their ANSI path and start fine — `Src/Libraries/Termios` would need a Win32 console-API
twin), **`IO#wait`/`IO.select` on a pipe** (they are `poll(2)`, which is why `io-wait`,
`socket` and `core/io` hang rather than fail), redirection of **descriptors above 2** in
`Process.spawn`/`exec` options (Windows hands a child three handles, not a descriptor
table, so such a redirection is refused with `EINVAL`), and the Unix-only half of
`Src/Libraries/Builtins/PosixProcess.cs` (`setsid`, `getpriority`, `setrlimit`, …).

## License

Apache License 2.0, as the original. See `Public/License.html`.
The original content description is preserved in [`README.txt`](README.txt).
