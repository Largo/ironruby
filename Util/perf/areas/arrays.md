# Arrays area (perf-areas round, 2026-11-14)

Shapes: `Util/perf/areas/arrays/iter.rb` (ns per call, least of 5, from a method, 200k calls;
mostly 1-element receivers or 8-element literals). Machine heavily loaded (10 agents), so
CRuby numbers are noisy; ratios below 1.5x are within noise.

## Ratio table on the baseline (b6cd208a), IronRuby / CRuby

| shape | CRuby ns | IR ns | ratio |
|---|---|---|---|
| `b.rotate!` (8 elems) | 61 | 1472 | 24x |
| `[7].any? { }` | 155 | 1814 | 11.7x |
| `[7].all? { }` | 162 | 1150 | 7.1x |
| `[7].sum` | 74 | 521 | 7.0x |
| `min_by { }` (8) | 1327 | 3906 | 2.9x |
| `max` (8) | 58 | 139 | 2.4x |
| `inject(0) { }` | 465 | 1098 | 2.4x |
| `each { }` (1) | 152 | 354 | 2.3x |
| `zip` (4) | 612 | 1322 | 2.2x |
| `each_with_index` | 381 | 710 | 1.9x |
| `map` | 238 | 413 | 1.7x |
| pushpop / each_slice | | | 1.3-1.4x |
| sort_by, unshift, pack, sort, product, flatten, each_with_object | | | <= 1.0x |

Where the time went: every one of the worst is a **Ruby prelude wrapper** (`ruby4.rb`) in
front of a C# method: `any?/all?/none?/one?(*args, &block)` (splat array, a kept Proc for
`&block`, a second call) only to support the rare pattern argument; `rotate!` was
`replace(rotate(count))` with `rotate` built from two slices and two concats;
`Array#sum(*args, &block)` wraps `__numeric_sum__`.

## Kept

1. **any?/all?/none?/one? without the wrapper.** Enumerable.cs's block forms are called
   directly; a new one-argument overload of each hands a pattern to the prelude's private
   `__<name>_with_pattern__` (the old wrapper bodies). Arity errors and the "given block not
   used" warning location unchanged. Top level, 1M calls, steady state (alternating processes):
   `[7].any? { |x| x > 1000 }` 713-766 -> 218-295 ns (CRuby 152-159);
   8 elements 1071-1149 -> 616-659 ns (CRuby 707-766).
2. **Array#rotate / rotate! in C#** (ArrayOps). `rotate!` over 8 elements 1301-1475 -> 215-237
   ns (CRuby 67-73); over 1 element 1188-1268 -> 62-79 (CRuby 47-52).

Verified: answers equal CRuby on a hand check (rotate with negative/large/Float/nil counts,
subclass, frozen; patterns, arity error, warning, early return from a block, Hash/Range,
multi-value each); mspec core/array + core/enumerable 3434 examples, 1 error, same single
error (try_convert wording) as the baseline; any/all/none/one/rotate/hash any/range: 721
examples, 0 failures both trees. Only wording difference: `rotate(nil)` says "of nil into
Integer" (IronRuby's chosen wording, as before).

## Left, ranked

1. `Array#sum` (7x): same wrapper shape (`sum(*args, &block)` -> `__numeric_sum__`); a C#
   `sum()`/`sum(init)` with a fallback to Enumerable#sum via a private alias should get ~70 ns.
2. `min_by` (2.9x): prelude `__extreme_by__` with a Ruby block per element; a C# loop.
3. `inject` (2.4x): prelude `__enum_inject__(args, block)` wrapper again.
4. `each`/`map` on one element (2x): fixed Proc/BlockParam entry cost (LEDGER gap 13).
5. `rotate!` still ~3x CRuby: overload/DefaultProtocol dispatch plus a temp array.
Not measured (time): optcarrot / rubyboy macro runs.
