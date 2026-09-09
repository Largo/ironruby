# IronRuby.Prism — CRuby's parser as an IronRuby front end

Prototype binding of [ruby/prism](https://github.com/ruby/prism), the parser
CRuby 3.4+ uses, via P/Invoke to `libprism.so`. Goal: retire the hand-ported
1.9-era `Tokenizer.cs` + gppg `Parser.y` (~13k lines) and get modern Ruby
syntax handling from the same parser CRuby, JRuby and TruffleRuby use.

## Status

- **`ir -X:UsePrism file.rb` works**: the console flag swaps the front end
  for everything, including `require`d stdlib and `eval` (outer eval
  locals are threaded via `RubyCompilerOptions.LocalNames` +
  prism's VARIABLE_CALL flag).
- **100% of the bundled 1.9 stdlib maps through the bridge**
  (571/571 files in `ruby/1.9.1`, 39/39 in `ironruby/`; measure with
  `IronRuby.Prism --sweep <dir>`). Coverage includes rescue/ensure/retry,
  case/when, regexps (incl. named-capture writes), multiple/attribute/
  index assignment and op-assigns, for loops, alias/undef, singleton
  classes, BEGIN-less END blocks, string/symbol/xstring interpolation.
- Stress programs (classes, super, blocks, splat, yield, ERB templating
  via eval, OpenStruct, Time.parse, StringIO) produce byte-identical
  output vs the legacy parser. Unmapped node types (pattern matching,
  safe navigation, keyword args) raise a clean NotSupportedException
  naming the prism node.
- `PrismParser.ParseSerialized(source)` — prism's compact binary AST
  (the format JRuby/TruffleRuby load; `docs/serialization.md` in prism).
- `PrismParser.ParseToJson(source)` — full AST as JSON (what the bridge
  currently consumes).
- `dotnet run -- --json file.rb` dumps the JSON AST.

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
