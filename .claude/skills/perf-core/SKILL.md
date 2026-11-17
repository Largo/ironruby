---
name: perf-core
description: Make one core method that is written in Ruby (in the prelude, Src/StdLib/ironruby/ruby4.rb) faster without moving it to C# - survey the core methods that are far from CRuby, see why the slow one is slow (almost always how it and its helpers are entered - a rest parameter, a helper that makes a scope, a block or a splat passed on - not its loop), rewrite its entry the way TruffleRuby's Ruby core is written, and keep it only if it is measurably faster and behaves exactly as before. Use when a named core method (inject, sum, zip, strip, each_with_index, dig, to_h, min_by...) is slow, when asked to speed up the prelude or the core library, or when tempted to port a prelude method to C# because it is slow. Prefer it to /perf-step whenever the slow thing is a core method written in Ruby; /loop /perf-core takes one method per run.
---

# A core method in Ruby, made faster where it is

The prelude is where IronRuby is far from CRuby: calls that end in a prelude method run at
about 3x CRuby, calls that end in C# at 1.6x (`Util/bench/truffleruby-lessons.md`, section
3). The reflex is to port the slow one to C#. Measured, the method's own work is rarely the
problem: what costs is being entered. TruffleRuby keeps 2,700 core methods in Ruby by
writing them so that entry is cheap, and the same writing works here.

Argument: a method (`/perf-core Array#zip`), or nothing for the worst one.

## 0. A worktree for the work

This checkout stays the baseline, clean and built (`/perf-step`, step 2, says why). Make the
candidate tree now, before anything is added or changed:

```sh
Util/perf/worktree.sh NAME          # ../wt-NAME on branch perf/NAME, with its own DLR copy
```

It copies the DLR from `../dlr/src/core`; from a checkout that has no such sibling (one
under `.claude/worktrees`, say), export `DLR_SOURCE_DIR=<the DLR checkout>/src/core` first.
Shapes you add to `core-small.rb` go into the worktree and are committed with the change;
throwaway probes go into a scratch file outside the repository.

## 1. Find it

```sh
ruby Util/perf/ab.rb -r 1 -n 200000 Util/bench/core-small.rb cruby=ruby "ir=env IR_CONFIG=Release ./ir.sh"
```

That is a survey: one round at a fifth of the iterations, about two minutes for all 67
calls (the defaults, three rounds of 1,000,000, take about 25). It ranks well enough to
choose; its ratios are not results. One round has no spread to read, and a row under
about 100 ns sits on the 4 ms grain of the timer at this count (its cells come out as
multiples of 20 ns), so a cheap row's ratio can be off by half. Measure the chosen method
properly in step 5.

The row names are the `t("...")` lines at the bottom of `core-small.rb`; each calls
`h.mNN`, whose one-line body (in `class H` at the top) is the call being timed. Then ask
the inventory which side defines it:

```sh
WHERE='Array#dig Enumerable#min_by' ruby Util/bench/core-inventory.rb Array | tail -2
```

`Ruby` and `Ruby (replaces C#)` are this skill's. `C#` is `/perf-step`'s. `?` means the
name is defined on an ancestor: ask again as `Enumerable#take`, `Kernel#String`,
`Comparable#clamp`. Nothing at run time tells Ruby from C# here - prelude methods hide
their `source_location` - which is why the inventory reads the sources.

Take the row with the highest ratio among those that are Ruby, cost 100 ns or more on
IronRuby, and are not already settled in `Util/perf/LEDGER.md` (under "Landed", under
"Rejected", or in a gap's notes - gap 15 says `tap` and `then` are already minimal Ruby,
so skip them). When two are close, take the one with more nanoseconds over CRuby.

In the worktree, make sure every form the method is commonly called in has a row of its
own in `core-small.rb` - with and without a block, with and without the optional argument,
a Symbol in place of a block: the form you did not measure is the one you will make
slower. A row is a method `mNN` on `H` and a `t("name", N) { ... h.mNN ... }` line.

## 2. See where the time goes

Read the method in `ruby4.rb`, and every `__x__` helper it calls: a helper can be a C#
method registered under that name (`grep -rn '"__x__"' Src/Libraries`) or another Ruby
method in `ruby4.rb`, and the difference matters below. Then ask about all of them at
once, on a call that takes the common path:

```sh
IR_LIGHT_FRAMES=why IR_CONFIG=Release ./ir.sh -e '[[1, 2]].dig(0, 1)' 2>&1 |
  grep -a -E ' (dig|__dig_step__|__array_to_int__) '
```

- `light:` - runs without a scope.
- `why not: ... <Node> at line N` - the filter refused it; line N is the first statement
  that is not scope-free (`MethodCall` there is often a call with a splat or a block
  passed on).
- `needs scope:` with `why not: ... body needs its scope after the rewrite` - the filter
  let it in, but something in the compiled body still reads the scope (a global variable,
  `binding`, a call the light-frame rewrite cannot route): it makes one on every call.

What each thing costs, on top of the 16 ns of calling a Ruby method at all (ns, IronRuby /
CRuby; measured on four-element receivers, good to about 30%):

| in the method | costs | cheaper form |
|---|---:|---|
| being passed a block literal | +104 / 0 | nothing to do here |
| a rest parameter `*args` in a method that makes a scope | +110 to +130 / +75 | optional parameters with a marker: +10 |
| a rest parameter in a light frame | +30 to +90 | the same |
| `&block` named | +8 to +42 / +20 | `yield` and `block_given?`, unless the block is passed on |
| `block_given?` in a light frame | +30 to +50 / +16 | it is the cheapest way to ask |
| `defined?(yield)` anywhere but as the opening guard | +75 to +180 | `block_given?` |
| a call with a splatted argument, `x.m(*a)` | about +76, and keeps the caller out of light frames | name the arguments |
| one layer of `inner(*args, &block)` | +200 to +275 / +110 | call `inner` with the arguments named |
| a call of a Ruby `__x__` helper that makes a scope | +124 to +128 | inline it, or make it light |
| one call of a C# method or C# `__x__` helper on self | +8 to +12 | |
| `super`, a block passed on, `rescue`, keywords | keep the method out of light frames | keep them off the common path |
| a body that needs a scope anywhere, even when the common case returns on line 1 | the whole scope, +150 ns (`String("a")`) | answer the common case, call a private method with the rest |
| a string interpolation that calls something, `"#{x.class} ..."` (even on an error path) | the method needs its scope after the rewrite | `x.class.to_s + " ..."` |
| one `while` iteration with a `yield` | ~100 / 50 | leave loops over elements in C# |

Add the parts up for the common path and compare with the row: if they do not come near
it, something on the path is not on your list yet.

Then find the floor - the cheapest code that computes the same answer for the common case:
the C# method a wrapper ends in, called directly (`a.__inject_without_name_check__(0) { }`
next to `a.inject(0) { }`), or, for a method with no C# behind it, the plain expression
(`a[0][1]` next to `a.dig(0, 1)`). Time both in one scratch file with `Util/perf/ab.rb`
(`/perf-ab`, step 5, says how to write one). What a perfect entry would win is the
difference as a share of the method's time on IronRuby; under 15%, pick another method.

`IR_LIGHT_FRAMES=why` names only the first statement it refused, and says nothing more
specific than `body needs its scope after the rewrite`. To find the construct, copy the
method's statements into one-line methods in a scratch file, *call each once* (a method
is compiled, and reported, on its first call), and see which are not `light:`.

Worked example (2026-11-15): `[[1, 2]].dig(0, 1)` took 776 ns against `@n[0][1]`'s 68 and
CRuby's 64. `dig` itself was light; its two helpers, `__array_to_int__` and `__dig_step__`,
are Ruby and made a scope on every call (four calls, about 500 ns), and the rest parameter
and the splat into the next `dig` cost the rest. 91% to win: go on.

## 3. Rewrite the entry, not the method

In the order they usually pay:

1. **Say "not given" with a marker, not with a rest Array.**
   `def first(n = (no_n = true; nil))` instead of `def first(*args)`. Keep a rest
   parameter only where CRuby's error for too many arguments needs the count, and then
   after the optional ones: `def inject(initial = (no_initial = true; nil), name = (no_name
   = true; nil), *extra, &block)`.
2. **A direct path for the forms that need no check.** Most wrappers exist for one rare
   case (a name that is not a Symbol, a count argument). Test for the common forms first
   and call the C# method with the arguments named; send everything else down the path
   that is there now, unchanged.
3. **Do not name the block unless it is passed on**; open with
   `return to_enum(:m) { size } unless block_given?`.
4. **A type test and one C# helper for the plain receiver**
   (`return __list_tally__ if __plain_list__?`), the generic `each` body after it. This is
   TruffleRuby's `Primitive.array_inject(...) if Primitive.is_a?(self, Array)`.
5. `::Symbol === x` rather than `x.is_a?(Symbol)` for a value that may be a BasicObject.

6. **A loop in the prelude counts with `i = i.succ`, not `i += 1`**, as CRuby's own Ruby
   builtins do (`numeric.rb`): a program that redefines Integer#+ must not change which
   elements are visited (`Array#sum`'s first loop skipped every other one).
7. **A rare path that needs a lot of code goes into a private method of its own.** A body
   that grew by a fallback made the common call 5% slower on `Array#sum`; an Enumerator
   made on a hot fallback cost 2 us. Measure the form that did not change, too.

What this skill does not do: move the method to C#, change what it returns or raises, or
write a per-element loop in Ruby (a `while ... yield` runs at 100 ns per element against 9
for a C# iterator; that needs the loop compiler to learn `yield`).

## 4. Prove it behaves the same

Before the rewrite, write the edges down as a script of probes and keep its output from
CRuby and from the unchanged build:

```ruby
def t(l); print l.ljust(34), ' => '; begin; v = yield; puts v.inspect; rescue Exception => e; puts "#{e.class}: #{e.message}"; end; end
t('inject 3 args block') { [1, 2, 3].inject(1, :+, 3) { } }
```

Cover: every argument count including one too many, with and without a block; `nil`, a
BasicObject and an object with only the conversion method as each argument; an empty and a
one-element receiver; Array, Range, Hash and a plain `include Enumerable` class; `break`,
`raise` and `return` from the block; `&proc`, `&:sym`, a lambda; the operator it relies on
redefined; `arity`, `parameters` and `name` of the method; the frame's label in a backtrace.

After the rewrite the output must equal the unchanged build's, line for line. A line where
the unchanged build differed from CRuby may change only to CRuby's. A new difference from
CRuby is a failure, error messages included.

## 5. Measure every form

```sh
ruby Util/perf/ab.rb -r 4 ../wt-NAME/Util/bench/core-small.rb cruby=ruby "base=env IR_CONFIG=Release ./ir.sh" \
     "new=env IR_CONFIG=Release ../wt-NAME/ir.sh" -- inject inject_sym reduce range_inject
```

The worktree's copy of the file, which has the rows you added; both builds run the same
file. Name every row of the method after `--` (a few minutes at four rounds).

`/perf-ab` says how to read it. Keep the change only if the targeted form is 15% faster or
more and no form of the method, and no other method that calls it, is slower beyond the
noise of the run. The first version of `inject` here was 30% faster with a block and 30%
slower with a Symbol; only the table showed it.

## 6. Check, land, record

- `spec/core/<class>` for the receiver classes and for `enumerable`, against the unchanged
  build, failing example names compared (not counts).
- `/perf-gate ../wt-NAME`. IronRuby.Tests load the prelude from `Src/StdLib` when a test
  says `require 'ruby4'`, so they see the change.
- A Release build precompiles the prelude into the R2R host's startup library; run the
  probes once with `IR_CONFIG=R2R` after building.
- Commit: what was wrong (the entry, with its cost), what the method does now, the
  numbers against the unchanged build and CRuby, how it was checked.
- Promote it as `/perf-step` does (step 4 there): `git -C ../wt-NAME status --short` prints
  nothing, `git merge --no-ff perf/NAME` into the working branch, rebuild the baseline
  here, and run the step-5 rows again on the merged build against the commit before the
  merge. If the gain does not show there, revert the merge.
- Then, in `Util/perf/LEDGER.md`, move the method to "Landed" (with the merge commit) or
  add under "Rejected" what was tried and what the floor was, and remove the worktree
  (`Util/perf/worktree.sh -d NAME`). Do not open a pull request unless asked.

## Stop conditions for a loop

No prelude row of `core-small.rb` above 2x CRuby whose floor leaves 15% to win; three
rejections in a row; or what is left is all blocked on the same thing (a block or a splat
passed on) - then the next step is `/perf-frame`, not another method.
