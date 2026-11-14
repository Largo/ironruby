# Perf area: parsers and tool workloads (2026-11-14)

Branch `perf/parsers`, from #216's head b6cd208a. Machine shared by ten agents: every time
below is CPU (user+sys) or ruby-bench's own wall average under the same load for both
engines; read ratios, not absolute numbers (CRuby's own hexapdf went 2.9 s -> 10.3 s
against the quiet-machine table in `Util/rubybench/pure-gems.md`).

## Ratio table (baseline build, `Util/rubybench/pure-gems-run.sh`, ruby-bench copy)

| benchmark | IronRuby | CRuby | ratio | note |
|---|---|---|---|---|
| rubocop | 55282 ms | 927 ms | **60x** | iterations 150 s, 62 s, 45 s, 59 s: never warms up (below) |
| protoboeuf-encode | 5171 ms | 535 ms | 9.7x | |
| protoboeuf (decode) | 4345 ms | 426 ms | 10.2x | steady state ~8x; the rest is .NET JIT of 2000-line methods |
| graphql | 1297 ms | 355 ms | 3.7x | |
| tinygql | 7442 ms | 2061 ms | 3.6x | StringScanner is no longer the problem (was 121x) |
| ruby-json | 6658 ms | 2324 ms | 2.9x | |
| hexapdf | 29547 ms | 10322 ms | 2.9x | |
| ruby-lsp | - | - | N/A | this tree has no librbs (`Util/build-librbs.sh` not run here) |
| psych-load | - | 14111 ms | N/A | Gemfile wants psych ~> 4.0.0 (as pure-gems.md says) |

TruffleRuby's asciidoctor was not run (budget went to rubocop).

## Where rubocop's time goes

In-process loop `Util/perf/areas/parsers/rubocop_loop.rb` (one autocorrect of the fixture
per run, CPU per run): CRuby 468 ms; IronRuby ~40 s per run at runs 3-15, with no trend
down. perf at run 2-3: 49% libclrjit, 22% libcoreclr, the Ruby code itself a few percent.
The JIT samples are on thread-pool threads compiling `LightLambda`s
(`LightDelegateCreator.Compile`): call-site rules reaching their compile threshold.
`IR_BIND_TRACE=1` counts the binds: **~128,000 binds per run, every run** (load + run 0:
203k). The rubocop sites that call `self.class.badge`, `cop.class`, `cop_name`,
`relevant_file?`, `gem_requirements`, `respond_to?`, ... see ~600 cop classes each, so a
site's rules thrash and each new rule is interpreted, then compiled and JITted.

RubyCallAction already shares one rule between receiver classes that resolve the call to
the same method (`BindShared`/`RuleVersionSet`), but not at a site with a light scope
(`HasLightScope`, added later by c9a17e28) - and since methods run without a scope
until they need one, that is nearly every call inside a method body.

## Kept

1. **Rules are shared at light-scope sites too** (RubyCallAction.BindShared). The flag is
   added to `SharableCallFlags`; the method is resolved the way `Resolve` resolves it at such a
   site (an explicit-receiver call looks for a public method only; when there is none nothing is
   shared and the ordinary bind reads the scope). Binds per steady run 128k -> 50k.
   rubocop CPU per run, A/B at the same time (6 warm runs, mean of the next 3):
   baseline 37068 ms, this 17660 ms (**2.1x**); warm-up runs 125/61/51/40/50/44 s vs
   70/29/24/23/24/19 s. Second A/B on the final build: 33475 -> 15108 ms (2.2x). Still ~32x CRuby (468 ms).
2. **`String#<<` Integer appends in place** (MutableStringOps.Append(self, int)): a byte onto a
   BINARY string, a code point < 0x80 onto an ASCII-compatible one, without building a
   one-character String through Integer#chr. `micro.rb` str<<int 313-340 -> 84-88 ns (CRuby ~90);
   protoboeuf-encode steady state (`pb_encode.rb`) 1.13-1.29 -> 0.71 s/pass (CRuby 0.29), whole
   process 35-41 -> 30-32 s CPU.

Verified: protoboeuf-encode 4648275 bytes, same SHA-256 as CRuby; rubocop (`answers.rb`) the same
40 offences / corrected source digest as CRuby over three runs; `append_check.rb` (encodings x
code points x frozen) identical to the baseline (both differ from CRuby only in the pre-existing
"bignum out of char range" wording); `shared_light_check.rb` (a site in a method body meeting 40
classes: private/protected/method_missing/redefinition/refinements) identical to CRuby;
hot-shapes-matrix 0 differing lines of 1264; mspec core/string 3963 examples, language 2935,
core/kernel 2230, library/stringscanner 240: same failures as the baseline (1, 1, 4, 0
pre-existing errors); core/module 1050 and core/basicobject 172 examples: same 4 and 3 errors as the baseline.

## Tried and not kept

- Boxing `int?` library results (StringScanner#skip, #match?...) through the small-Integer
  cache instead of `Box_Nullable`: correct, but tinygql 21.6-24.2 vs 24.0-25.3 ms/parse and a skip
  micro within noise. Not measurable on this machine; reverted.
- `DOTNET_TieredPGO=0` for the protoboeuf JIT warm-up: 1.25 vs 1.04 s/pass, no help.

## What is left (ranked)

1. **rubocop still ~38x**: 50k binds per run remain. `respond_to?(:sym)` rules (6.7k/run) are
   specialized on the receiver class (DispatchIntrinsics, up to 32 per class) and never shared -
   at a megamorphic site the general rule should be shared instead; `Class#new` (3k/run) and
   attr_reader calls (`gem_requirements`, 3k/run, RubyAttributeReaderInfo is not a shareable kind)
   bind per class; 19k "pre" precompiled-dispatcher binds per run past the chain limit of 8.
   Every bind also costs a later background compile, which is where the JIT time is.
2. **protoboeuf decode/encode**: steady state is ~2.5-8x, but the harness's 20 iterations
   mostly measure the .NET JIT of the generated 2000-line `decode_from`/`_encode` bodies
   (35% libclrjit 12 s in, still 30% at 30 s). A cheaper tier for huge methods is a JIT-area item.
3. **tinygql/graphql 3.6x**: 32% libcoreclr (allocation/GC) and 12% `CallSite.Target` in the lexer
   and node constructors; the per-token allocations, not StringScanner, are the cost now.
4. `IO.for_fd(2)` without a mode opens fd 2 read-only (CRuby takes the mode from the fd):
   `syswrite` raises IOError. Found while instrumenting; not fixed (not perf).
