# Work packages: passing more of CRuby's test/ruby

Baseline (`baseline.txt`, CRuby v4.0.10's tests, net10.0, Linux): 142 files, 53 clean, 86 with
failures, 3 without a summary; 10,325 tests counted, 6,342 passing. (The first sweep, of v4.0.9:
48 clean, 11 without a summary, 6,903 counted, 4,939 passing.) Each package is a set of
test files one person (or agent) takes, in a build tree of their own.

## Rules for every package

1. **CRuby 4.0 is the spec** (CLAUDE.md). Reduce a failing test to a few lines, run them with
   `ruby` (4.0.x) and with `./ir.sh`, and make IronRuby print what CRuby prints. Never edit the
   CRuby tests.
2. **Order of work:** first whatever leaves a file without a summary (a hang, a crash, an
   exception out of the runner): find the test at the end of the log, fix it, or if it cannot
   pass on IronRuby (see 5) add it to `excludes/`. Then failures, cheapest and most widely
   shared cause first: one missing method or wrong message often fails several tests.
3. **Where a method lives:** `Src/Libraries/Builtins/*Ops.cs` *and* the prelude
   `Src/StdLib/ironruby/ruby4.rb`; the prelude wins. A `.rb` change needs no rebuild. A
   `[RubyMethod]` signature or attribute change needs only a build (the initializers are
   generated during it).
4. **Check each fix:** the reduced snippet against CRuby, the test file before and after
   (failures must go down and nothing else in the file may break), and the ruby/spec files of
   the methods touched (`./ir.sh -Imspec/lib mspec/bin/mspec-run spec/core/<class>/<method>_spec.rb`)
   when `spec/` is there. A C# change to shared code (binder, call sites, RubyOps, IO, the
   compiler) also runs `IronRuby.Tests`.
5. **Leave alone** what tests CRuby's implementation: RubyVM, iseqs, `ObjectSpace` counts and
   allocation tracing, GC statistics and compaction, memory-leak assertions
   (`assert_no_memory_leak`), `fork`, Ractor, Ruby::Box, YJIT/ZJIT, C-level `-test-`
   extensions, exact `ruby` command-line dumps (`--dump`, `--yydebug`). Do not make such a
   test pass by faking the feature.
6. **Commits:** one per logical fix; the subject says what changed, the body what was wrong,
   what CRuby does and how it was checked (CLAUDE.md "Code and commits"). Keep each file's line
   endings. No client or machine names.
7. **Report** per file: failures+errors before and after, what was fixed, what was excluded
   and why, and what remains with its cause in a line each, so the next round can be cut.

## Packages

| # | Area | Files (baseline: tests / failures+errors) |
|---|---|---|
| 1 | Numbers | test_complex 57/0, test_float 63/1, test_numeric 27/1, test_rational 52/0, test_integer 38/1, test_math 34/0, test_rand 41/0, test_random_formatter 26/0, test_arithmetic_sequence 37/0, test_range 67/2, test_sprintf 35/3, test_sprintf_comb 95/0 |
| 2 | Collections | test_array 493/26, test_hash 298/14, test_enum 90/4, test_enumerator 70/0, test_lazy_enumerator 64/0, test_set 63/0, test_struct 102/2 |
| 3 | Strings | test_pack 56/4, test_string 475/30, test_symbol 59/6, test_stringchar 4/0, test_literal 29/2, test_mixed_unicode_escapes 1/1, test_regexp 167/48 |
| 4 | Encodings | test_econv 85/12, test_transcode 101/21, test_encoding 15/1, test_m17n 127/13, test_m17n_comb 82/3, test_io_m17n 160/22 |
| 5 | Classes and methods | test_class 62/8, test_module 195/17, test_object 67/8, test_method 99/17, test_alias 18/4, test_variable 32/3, test_defined 39/5, test_arity 3/0 |
| 6 | Procs, super, refinements | test_proc 120/13, test_lambda 27/1, test_super 50/6, test_refinement 122/15, test_yield 13/0, test_fiber 35/3, test_pattern_matching 30/0, test_assignment 29/0, test_flip 5/1 |
| 7 | Exceptions, eval, syntax | test_eval 40/5, test_exception 101/20, test_backtrace 31/4, test_frozen_error 5/1, test_syntax 171/11, test_parse 107/21, test_beginendblock 16/5, test_autoload 27/4, test_require 52/18 |
| 8 | IO, files, environment | test_io 252/40, test_argf 73/20, test_dir 39/10, test_env 94/17, test_fnmatch 15/3, test_pipe 12/2, test_readpartial 4/1, test_io_buffer 87/22 |
| 9 | Processes, threads, signals | test_process 142/47, test_thread 98/24, test_thread_cv 13/3, test_thread_queue 40/5, test_threadgroup 3/1, test_signal 21/4, test_system 8/3 |
| 10 | Time, Marshal, runtime | test_time 89/19, test_time_tz 166/15, test_marshal 234/35, test_weakmap 19/1, test_objectspace 37/20, test_gc (hangs at test_interrupt_in_finalizer), test_trace (hangs in test_trace), test_settracefunc 102/52, test_rubyoptions 68/51 |
