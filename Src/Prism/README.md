# IronRuby.Prism — CRuby's parser as an IronRuby front end

Prototype binding of [ruby/prism](https://github.com/ruby/prism), the parser
CRuby 3.4+ uses, via P/Invoke to `libprism.so`. Goal: retire the hand-ported
1.9-era `Tokenizer.cs` + gppg `Parser.y` (~13k lines) and get modern Ruby
syntax handling from the same parser CRuby, JRuby and TruffleRuby use.

## Status

- `PrismParser.ParseSerialized(source)` — prism's compact binary AST
  (the format JRuby/TruffleRuby load; `docs/serialization.md` in prism).
- `PrismParser.ParseToJson(source)` — full AST as JSON, easy to consume.
- `dotnet run [file.rb]` dumps both for a quick look.

Build the native library first:

```sh
git clone https://github.com/ruby/prism ../../../prism
cd ../../../prism && ruby templates/template.rb && make shared
```

## Integration plan

1. **Loader generation.** Prism describes every node type in `config.yml`
   and generates its Java/JS bindings from ERB templates. Write a
   `templates/csharp/` set (mirroring `templates/java/api/`) that emits a
   C# deserializer for the binary format — no JSON hop, no per-node
   hand-maintenance, auto-tracks prism releases.
2. **AST bridge.** A visitor that maps prism nodes to the existing
   `IronRuby.Compiler.Ast` node set consumed by `AstGenerator`. Ruby 1.9
   constructs map 1:1; newer syntax (pattern matching, endless methods,
   safe navigation) either lowers to existing nodes or reports a clean
   "not supported yet" error — still a strict improvement over a parse
   error from the 1.9 grammar.
3. **Switchover.** `-X:UsePrism` flag selects the front end; the legacy
   parser stays until the bridge passes IronRuby.Tests parser suites,
   then becomes the fallback and eventually goes away.

Line/column info: prism gives byte offsets + a line-offset table
(`pm_parser_line_offsets`); IronRuby's `SourceSpan`s are built from the
same data, so source maps carry over directly.
