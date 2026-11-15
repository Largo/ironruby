# Perf area: exceptions and non-local control flow (2026-11-14)

Measured with `Util/perf/areas/exceptions/micro.rb` (per-op CPU ns, best of 5, in-process, steady state)
on the baseline (#216 head b6cd208a, Release net10.0) against CRuby 4.0.9. The machine was shared by ten
agents; absolute numbers drift up to 3x between runs, so only interleaved A/B comparisons count.

## Ratio table (baseline, worst first)

| shape | CRuby ns | IronRuby ns | ratio |
|---|---:|---:|---:|
| caller_locations(1,1) | 644 | 109066 | 169x |
| raise "x"; rescue => e | 1731 | 70853 | 41x |
| 1/0 rescue nil | 2016 | 68430 | 34x |
| {}.fetch(:b) rescue nil | 2556 | 67392 | 26x |
| nil.zork rescue nil | 2400 | 61949 | 26x |
| catch(:t) { throw :t } | 412 | 8808 | 21x |
| Integer("x") rescue nil | 3188 | 64043 | 20x |
| Float("x") rescue nil | 3107 | 60475 | 19x |
| catch { \|t\| throw t } | 501 | 8997 | 18x |
| raise E (pre-made); rescue | 745 | 7339 | 10x |
| raise through 10 frames | 2896 | 26187 | 9x |
| loop { e.next } (StopIteration) | 10456 | 77274 | 7x |
| e.next past end, rescue StopIteration | 9647 | 67018 | 7x |
| proc { return } from a method | 964 | 6602 | 7x |
| e.backtrace | 14306 | 87985 | 6x |
| caller | 10627 | 63516 | 6x |
| ensure, raise through | 1899 | 11068 | 6x |
| raise MyErr; rescue MyErr | 1701 | 9235 | 5x |
| retry once | 2077 | 9521 | 5x |
| block next | 204 | 593 | 3x |
| ensure, no raise | 108 | 204 | 2x |
| catch, no throw | 242 | 391 | 1.6x |
| block return (ruby-bench throw.rb shape) | 386 | 507 | 1.3x |
| block break | 500 | 598 | 1.2x |
| fetch with default | 172 | 192 | 1.1x |
| Integer("x", exception: false) | 374 | 279 | 0.7x |
| lambda return | 915 | 449 | 0.5x |

## Where the time goes

Two costs, in this order:

1. **The catch-site CLR stack walk** (`new System.Diagnostics.StackTrace(false)`, ~2.5 us per frame
   here: 27 frames 69 us, 125 frames 212 us, measured from Ruby) - 65-70% of every shape above 15x.
   perf: `RubyStackTraceBuilder.GetClrStackTrace` -> `StackTrace..ctor` -> `StackFrameHelper.InitializeSourceInfo`
   (the native walk, `libcoreclr`, no symbols) plus `RuntimeType.GetMethodBase` ~10%. The runtime already
   defers this walk (RubyExceptionData.TryDeferCatchSite) for exceptions Kernel#raise made, until the
   exception is exposed. It is taken when:
   - the exception is exposed: `rescue => e`, `$!`, `e.backtrace` (raise "x"; rescue => e: 41x);
   - the exception was thrown by C# library code - those were never deferred, since C# code may keep an
     exception and hand it to Ruby later (Encoding::Converter#last_error). That is why `Integer('x')`,
     `Float('x')`, `Hash#fetch`, `nil.foo` and `1/0` rescued and dropped cost 20-34x while `raise MyErr` is 5x;
   - `caller`/`caller_locations` (always a whole-stack walk, even for `caller_locations(1,1)`, then a regexp
     parse per entry in ruby4.rb).
2. **The .NET exception dispatch itself**, ~6-7 us on this machine (raise of a pre-made exception, no walk:
   7.3 us; catch/throw: 8.8 us; proc return: 6.6 us). Two-pass SEH over the frames between throw and catch.

## Kept: library throws that keep nothing defer the walk (commit on perf/exceptions)

`RubyExceptionData.MarkUnkept(e)` sets `RaisedUnkept` (what Kernel#raise sets) on an exception a library
method makes and throws at once; the existing deferral then applies. Marked sites:
`Integer()`'s and `Float()`'s invalid-value ArgumentError, the NoMethodError/NameError of a call with no
method (`RubyOps.MakeMissingMethodError`, `MakeUndefinedLocalOrMethodError`, BasicObject#method_missing), and
Hash#fetch's KeyError when `KeyError.new` runs only the prelude's #initialize and built-in code
(`MakesUnkeptPreludeException`; a program that redefines KeyError#initialize can keep the exception, so it is
not marked then). Exposure still takes the walk, from the live frame, so backtraces are unchanged.

Interleaved A B B A (ns/op; the machine was loaded, raise_rescue as the control was ~33 us on both):

| shape | base | new | new | base |
|---|---:|---:|---:|---:|
| Integer("x") rescue nil | 139382 | 28597 | 31554 | 239124 |
| Float("x") rescue nil | 71293 | 16527 | 21416 | 76560 |
| {}.fetch(:b) rescue nil | 111695 | 13484 | 21663 | 112477 |
| nil.zork rescue nil | 79138 | 8021 | 8341 | 75251 |
| raise MyErr; rescue (control) | 33220 | 32996 | 33307 | 34468 |

So 4-10x faster for these shapes (they now cost about what `raise MyErr` costs).

Verified: `Util/perf/areas/exceptions/backtrace-check.rb` (backtraces of all four rescued as `=> e`, `$!`,
in a loop, read after the frame, from a lambda; a redefined KeyError#initialize keeping the exception) prints
the same on the new tree as on the baseline, and the same as CRuby except the known difference that CRuby's
Hash#fetch does not call KeyError#initialize. mspec, same counts and same failing examples on both trees:
core/kernel/Integer_spec (244 ex, 3 errors both), Float_spec (101), core/hash/fetch_spec (18), core/exception
(250), language/rescue_spec (60), core/basicobject/method_missing_spec (30), language/send_spec (78, 1 error
both - "into" vs "to" wording), core/kernel/caller_spec (20).

## Not done / rejected, with reasons

- **catch/throw, proc return, break without a .NET exception.** The remaining 6-9 us is the CLR's
  two-pass exception dispatch. Unwinding without it would need every compiled call site (and every C#
  library method that yields) to test a pending-unwind flag after each call and return early, running
  ensure clauses itself - a compiler-wide change with a per-call cost on the non-throwing path, which
  is the common one (block return/break are already 1.2-1.3x because the compiler turns the known-target
  cases into plain returns). ruby-bench `throw.rb` is that already-optimised shape (1.3x). Not feasible in
  this round.
- **1/0 rescue nil** (34x): the DivideByZeroException comes from the CLR's own integer division in compiled
  code or from several `throw new DivideByZeroException` sites; marking those is the same fix, left because the
  JIT path's exception is not made by library code at a single site.
- **rescue => e / $! / e.backtrace** (41x): exposure must take the walk while the catching frame lives
  (the frames below it vanish afterwards). Making it cheaper means building the catch-site frames without a
  CLR walk (from PrecompiledFrames records and the interpreted-frame chain), which misses C# library frames
  (`Integer#times` etc.) that Ruby backtraces show. Biggest remaining lever, large change.

## Remaining gaps, ranked

1. caller_locations(1,1) 169x / caller 6x: a full CLR walk for any depth, then regexp parsing in ruby4.rb.
   A bounded walk (stop after N Ruby frames) is not offered by System.Diagnostics.StackTrace; building
   Location objects in C# instead of parsing strings would take ~12% off.
2. rescue => e / $! (41x): the exposure walk (above).
3. 1/0, other C# throw sites (34x): mark more sites unkept (each needs a check that nothing keeps the exception).
4. catch/throw (21x), proc return (7x): CLR dispatch cost (above).
5. StopIteration via Enumerator#next (7x): the fiber switch plus the walk; belongs with the fibers area.
