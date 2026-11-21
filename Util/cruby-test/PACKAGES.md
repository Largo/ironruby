# Work packages: passing more of CRuby's test/ruby

Baseline (`baseline.txt`, CRuby v4.0.9's tests, net10.0, Linux): 142 files, 48 clean, 83 with
failures, 11 without a summary; 6,903 tests counted, 4,939 passing. Each package is a set of
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
   `[RubyMethod]` signature or attribute change needs `./regen-initializers.sh`; check that the
   generated diff holds only your change.
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
| 1 | Numbers | test_complex 57/10, test_float 63/7, test_numeric 27/5, test_rational 52/3, test_integer 38/1, test_math 34/2, test_rand 41/7, test_random_formatter 26/2, test_arithmetic_sequence 37/2, test_range 67/15, test_sprintf 33/5, test_sprintf_comb 95/6 |
| 2 | Collections | test_array 477/48, test_hash 306/17, test_enum 90/21, test_enumerator 70/6, test_lazy_enumerator 64/6, test_set 63/4, test_struct 102/2 |
| 3 | Strings | test_pack (crashes at test_pack_unpack_u), test_string 442/30, test_symbol 59/2, test_stringchar 4/2, test_literal 29/4, test_mixed_unicode_escapes 1/1, test_regexp (hangs at test_cache_opcodes_initialize) |
| 4 | Encodings | test_econv 84/29, test_transcode 98/22, test_encoding 15/2, test_m17n 127/20, test_m17n_comb 82/12, test_io_m17n 159/48 |
| 5 | Classes and methods | test_class 62/10, test_module 195/22, test_object 67/15, test_method 98/16, test_alias 18/2, test_variable 32/3, test_defined 39/9, test_arity 3/1 |
| 6 | Procs, super, refinements | test_proc 118/16, test_lambda 27/5, test_super 50/6, test_refinement 122/15, test_yield 13/2, test_fiber 35/3, test_pattern_matching 30/2, test_assignment 29/1, test_flip 5/1 |
| 7 | Exceptions, eval, syntax | test_eval (crashes at test_fixnum_instance_eval_cvar), test_exception 98/16, test_backtrace 31/4, test_frozen_error 5/1, test_syntax 170/11, test_parse 102/19, test_beginendblock 16/4, test_autoload 27/2, test_require (exception out of the runner in test_loading_fifo_fd_leak) |
| 8 | IO, files, environment | test_io (hangs after test_copy_stream_socket6), test_argf (hangs at test_readlines_limit_0), test_dir 38/10, test_env 94/30, test_fnmatch 15/3, test_pipe 12/2, test_readpartial 4/1, test_io_buffer 80/19 |
| 9 | Processes, threads, signals | test_process (hangs at test_execopts_redirect_open_fifo_interrupt_raise), test_thread (hangs at test_thread_status_raise_after_kill), test_thread_cv 13/3, test_thread_queue 40/5, test_threadgroup 3/1, test_signal 21/4, test_system 8/3 |
| 10 | Time, Marshal, runtime | test_time 88/17, test_time_tz 155/4, test_marshal 231/32, test_weakmap 19/1, test_objectspace 37/19, test_gc (hangs at test_latest_gc_info_need_major_by), test_trace (hangs in test_trace), test_settracefunc 102/47, test_rubyoptions 68/52 |
