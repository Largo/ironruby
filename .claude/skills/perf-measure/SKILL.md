---
name: perf-measure
description: Measure IronRuby's speed against CRuby 4.0 and TruffleRuby (Are-We-Fast-Yet benchmarks at steady state, plus per-operation costs) and refresh the numbers and the ranked gap list in Util/perf/LEDGER.md. Use when asked how fast IronRuby is, to compare it with TruffleRuby or CRuby, to find what is slow, or before and after performance work. Safe to run in a loop (`/loop 1d /perf-measure`): it only measures and records.
---

# Measuring IronRuby against CRuby and TruffleRuby

The output is two tables and a ranked list of gaps in `Util/perf/LEDGER.md`, which
`/perf-step` picks its next target from. Nothing else in the tree changes.

## 1. Set up (once per machine)

```sh
Util/perf/setup.sh          # TruffleRuby and AWFY into ../ironruby-perf, Release build if missing
```

Build before measuring, every time: the binaries under `Src/Console/bin/Release` say
nothing about which sources they came from (another branch may have been built there since,
or the tree may have uncommitted changes), and an incremental build of an up-to-date tree
takes seconds. A stale build measures some other commit and records it under `HEAD`.

```sh
set -o pipefail
dotnet build Src/Console/Ruby.Console.csproj -c Release -p:IronRubyTargetFrameworks=net10.0 -v q | tail -3 || exit 1
```

Never measure a Debug build: it is about 1.8x slower across the board.

## 2. Measure

Check `uptime` first. With a load average above 1 on a small machine the numbers move by
tens of percent: wait, or note the load next to the numbers.

```sh
P=${PERF_HOME:-../ironruby-perf}
ruby Util/perf/awfy.rb 12 cruby=ruby "ir=env IR_CONFIG=Release ./ir.sh" \
     "truffle=$P/truffleruby/bin/truffleruby"
ruby Util/perf/micro-table.rb cruby=ruby "ir=env IR_CONFIG=Release ./ir.sh"
```

- That is all thirteen AWFY benchmarks (about 8 minutes at 12 s each on three
  interpreters), and it is what goes into the ledger: the first five rounds measured only
  the five object-oriented ones (`-- Bounce Richards Json DeltaBlue Towers`), which left
  the worst one (Permute, 1.49 of CRuby) unseen and would have hidden a regression in
  arrays, floats or closures. A subset is for iterating on one experiment
  (`/perf-step`), never for the recorded numbers.
- `FAIL` in the IronRuby column is a wrong answer or a crash, not a slow run. Run that
  benchmark alone, find the cause, and report it before anything else: correctness first.
- TruffleRuby's micro column is not worth taking: it deletes these loops. Its AWFY column
  is the honest comparison.
- For the wider picture (146 TruffleRuby benchmarks, ruby-bench) use `Util/benchsuite/run.sh`
  and `Util/bench/run.sh`; they take much longer and are not part of the loop.

## 3. Record

In `Util/perf/LEDGER.md`:

- Replace the "Current numbers" tables with the new ones, with the date, the commit
  (`git rev-parse --short HEAD`), the machine's core count and the load average.
- Rebuild "Gaps, worst first" from the micro table: every operation where IronRuby/CRuby is
  above 1.3, sorted by that ratio, one line each, with the AWFY benchmarks it plausibly
  dominates. Keep the notes already written on a gap (what was tried, what was learned).
- If a number got more than 10% worse than the previous entry at similar load, measure it
  again alone; if it holds, add it under "Regressions" with the range of commits
  (`git log --oneline <old>..HEAD`) and say so in the report. Do not bisect here.

Commit only the ledger (`Util/perf/LEDGER.md`), with a subject like
`perf ledger: numbers at <commit>`.

## 4. Report

The AWFY table, the geomeans (IronRuby/CRuby and TruffleRuby/CRuby), the three worst gaps,
and any regression or FAIL. Numbers, not adjectives.
