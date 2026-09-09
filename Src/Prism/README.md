# IronRuby.Prism — CRuby's parser as an IronRuby front end

Binding of [ruby/prism](https://github.com/ruby/prism), the parser CRuby
uses, via P/Invoke to `libprism.so`. Replaces the hand-ported 1.9-era
`Tokenizer.cs` + gppg `Parser.y` front end with the same parser CRuby,
JRuby and TruffleRuby use, targeting current (4.x-level) Ruby syntax.

Architecture (the JRuby approach):

1. `generate.rb` reads prism's `config.yml` and emits
   `Generated/PrismNodes.Generated.cs` (152 typed node classes + flag
   consts) and `Generated/PrismLoader.Generated.cs` (the per-node switch
   of the binary deserializer). Re-run it when upgrading prism; the
   loader validates the serialization version at runtime.
2. `PrismLoader.cs` implements the primitives of prism's binary
   serialization (varuint/varsint, constant pool, integers, locations)
   per prism's `docs/serialization.md`.
3. `PrismParser.cs` calls `pm_serialize_parse`, passing filepath, line
   and outer-scope locals (for `eval`) through the serialized
   `pm_options_t` data blob.
4. `PrismAstBridge.cs` maps the typed nodes onto `IronRuby.Compiler.Ast`.
   Modern syntax with no 1.9 AST equivalent is lowered: safe navigation
   becomes a nil-guarded temp, keyword arguments become a trailing
   optional hash plus a prologue (missing-keyword checks, defaults,
   `**rest` extraction), `it`/numbered params become explicit block
   params, `**` splats in literals/calls become `Hash#merge` chains,
   `2r`/`2i` become `Rational`/`Complex` calls.
5. Syntax errors from prism are reported through the DLR `ErrorSink`
   like the legacy parser's.

## Status

- **`ir -X:UsePrism file.rb` works**: the console flag swaps the front end
  for everything, including `require`d stdlib and `eval` (outer eval
  locals are passed to prism as a scope via the options blob).
- **Modern syntax runs**: keyword arguments (required/optional/`**rest`,
  correct missing-keyword errors), safe navigation, endless methods,
  hash shorthand `{x:}`, `it` and numbered block params, lambdas
  (`->`, `.()`), `**` splats — verified byte-identical against CRuby 3.3
  output (and `it` runs here even though CRuby 3.3 rejects it).
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

## Known limits / next steps

- Pattern matching is lowered to ===/deconstruct tests with bindings:
  case/in with guards, captures, array/hash/const patterns, pins,
  alternations, standalone `=>` and `in`. Not yet: find patterns with
  two splats (`[*, x, *]`), `{**nil}` exact-match patterns. The
  NoMatchingPatternError message is shorter than MRI's.
- `...` argument forwarding is lowered to `*?fwd?, &?fwdblk?` (keywords
  ride along as the trailing hash, consistent with the kwargs lowering).
- Keyword-argument lowering is restricted to signatures without optional
  positionals or `*rest` (where "trailing hash" and "keywords"
  coincide); unknown-keyword errors are not raised (permissive).
- String literals round-trip through UTF-16; binary string literals with
  invalid UTF-8 may lose bytes (needs byte[]-based MutableString
  literals).
- Switchover plan: run the IronRuby.Tests parser suites under
  `-X:UsePrism`, then flip the default and keep the legacy parser as
  fallback.

Line/column info: prism gives byte offsets + a line-offset table
(`pm_parser_line_offsets`); IronRuby's `SourceSpan`s are built from the
same data, so source maps carry over directly.
