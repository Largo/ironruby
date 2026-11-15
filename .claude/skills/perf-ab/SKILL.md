---
name: perf-ab
description: Tell whether one IronRuby build is faster than another on small shapes (a core method call, a kind of method entry, a guard) where the difference is 10-30% and the machine is shared - alternating runs, pinned to idle CPUs, on-CPU time, a checksum per shape, and a spread that says when a difference is only noise. Use whenever a perf change needs a before/after number, when a benchmark line "looks slower" or "looks faster" and you are about to believe it, when asked "is this actually faster than master?", and from /perf-core and /perf-frame for their measurements. Not for the AWFY table or the ledger's numbers (/perf-measure) or for whether a change is safe (/perf-gate).
---

# Is it faster? A/B on small shapes

A change to how a method is entered moves a call by 50-100 ns out of 300. On this kind of
machine the same binary measures 10-15% apart from one process to the next, so a single
before/after pair proves nothing either way. `Util/perf/ab.rb` is the tool; this skill is how
to use it and how to read it.

Arguments: the two trees and the shapes. In `/perf-step`, `/perf-core` and `/perf-frame`
the baseline is this checkout and the candidate `../wt-NAME`. For "is my branch faster
than master?" it is the other way round: this checkout is the candidate, and master is a
worktree you make for the purpose (`../wt-base`, below).

## 1. Two builds whose sources you know

Both Release, both built from a clean tree just now (`git status --short` prints nothing;
`set -o pipefail` if the build's output is piped). The binaries do not say which commit
they are. A Debug build is 1.8x slower and inlines nothing: never measure one.

A baseline other than this checkout is a worktree at the commit to compare with:

```sh
git worktree add --detach ../wt-base <commit>            # then build it like any tree
ln -s "$PWD/spec" ../wt-base/spec; ln -s "$PWD/mspec" ../wt-base/mspec   # only if specs will run there
```

Build the two one after the other, not at once, unless each has its own DLR copy
(`Util/perf/worktree.sh` makes one): trees sharing a DLR checkout share its `obj/`. Do not
run `dotnet build-server shutdown`: it ends other sessions' builds.

## 2. Run

```sh
ruby Util/perf/ab.rb -r 4 Util/bench/prelude-cost.rb cruby=ruby \
     "base=env IR_CONFIG=Release ./ir.sh" "new=env IR_CONFIG=Release ../wt-NAME/ir.sh" -- g_bg n_splat
```

- The shape files: `Util/bench/prelude-cost.rb` (a core method in C# against the same in
  Ruby, the parts of a Ruby iterator, openings, wrappers) and `Util/bench/core-small.rb`
  (67 core methods on four elements). `ruby FILE --list` prints a file's shape names.
  `Util/perf/micro.rb` (calls, ivars, yields, loops) is read too, but it times wall
  clock, prints no checksum and has no `N`: use it for a hint, not a decision.
- Name the shapes after `--`; the rows come in that order. A shape costs about 5 s per
  interpreter per round at the default count, so three shapes, three interpreters and
  four rounds are about three minutes. A whole file (67-96 shapes) is about 6-8 minutes
  per interpreter per round: for finding what moved that nobody expected, once, at the
  end, with `-r 2`.
- Rounds: `-r 1` is a survey (no spread is shown, because there is none to show), `-r 4`
  for a decision, `-r 6` for a shape whose cells overlap.
- CRuby first: it is the scale, and its checksum is the right answer.
- Put the candidate last and the build to compare it with just before it: the last
  column is last over the one before.

## 3. Read it

Master against the change that made `block_given?` and rest parameters cheaper, four
rounds (`-- g_bg n_splat e0`, 2026-11-15):

```
ns (spread %)             cruby           base            new  new/base
g_bg                   76.0 (4)     272.0 (14)     207.6 (15)  0.76
n_splat               133.6 (3)     259.2 (12)     182.2 (11)  0.70
e0                     56.7 (6)     171.3 (22)      169.9 (9)  0.99 overlap
```

- A cell is the least of its rounds; the number in brackets is how far the greatest was
  above it. The last column is the two least values' ratio, and `overlap` when some round
  of the slower build beat some round of the faster one.
- `g_bg`: the slowest round of `new` (207.6 x 1.15 = 239) was faster than the fastest of
  `base` (272): shown. `e0`, which the change does not touch, overlaps, as it should: it
  is the run's own noise, here up to 22% on one build. A row the change was meant to move
  that says `overlap` has **not** shown it, whatever its ratio. Run it alone with `-r 6`,
  and with the third arm (step 4).
- Some shapes are bimodal: a process lands on 300 ns or on 380, whichever build it is. It
  looks like a regression in every table it appears in. The test is the same: alone, six
  rounds, and see whether the two sets of numbers overlap.
- `VALUE DIFFERS` is a wrong answer. Stop measuring and find it.
- A shape that was not supposed to move and did, both ways, in the same table: that is the
  size of the noise in this run. Quote it next to the result.

## 4. The third arm

When the change has a switch (`IR_LIGHT_REST=0`, `-X:NoJIT`), run the candidate with the
switch off as one more interpreter:

```sh
ruby Util/perf/ab.rb -r 6 FILE "base=..." "off=env IR_LIGHT_REST=0 IR_CONFIG=Release ../wt-NAME/ir.sh" \
     "new=env IR_CONFIG=Release ../wt-NAME/ir.sh" -- shape
```

`off` and `new` are one binary, so code layout and everything else the change did not
intend are the same in both. `new` against `off` isolates the switched change. `off`
against `base` shows every other difference the candidate build has: noise, and anything
else the branch changed, intended or not. A regression that `off` shows too is not the
switched change's - but it is still the candidate's, and is reported as one until a rerun
shows it is noise.

## 5. A shape of your own

Add it to the file it belongs in, in the candidate's worktree, and commit it with the
change, so that the next run has it; the baseline checkout stays clean. A probe you will
not keep goes into a scratch file outside the repository, written the same way:

- a method on `H` that makes the call and returns something to sum, and a `t("name", N) {
  |n| i = 0; s = 0; while i < n; s += h.name; i += 1; end; s }` line. The sum is the
  checksum: make it depend on what the call returns.
- Write the methods out. A method defined with `eval` or `define_method` costs 100-240 ns
  more per call than the same `def`, which is more than what is being measured.
- Each timed section should take 100 ms or more: on-CPU time
  (`/proc/thread-self/schedstat`) has a grain of 4 ms, so a 50 ns shape needs a count of
  two million (`-n` sets it for every shape of the file; count x ns >= 100,000,000). A
  short count shows as cells that are multiples of `4 ms / count` (20 ns at 200,000).
- A pair is `x` and `rb_x` (C# and Ruby), or the same shape with and without the one thing
  in question (`g0` and `g_bg`): a difference between two lines of one process is steadier
  than either line.

## 6. Report

The rows, with CRuby's column, the rounds, and the noise seen in this run: "0.76 of master
on `g_bg`, four rounds, no overlap; `e0`, untouched, 0.99 with spreads up to 22%" - not
"24% faster".
