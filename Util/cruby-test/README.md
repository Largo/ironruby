# CRuby's test/ruby on IronRuby

CRuby's own tests (`test/ruby/test_*.rb` of a ruby/ruby checkout at the release IronRuby
follows, v4.0.9 here) run on IronRuby with CRuby's test-unit, next to ruby/spec.

```sh
git clone --depth 1 --branch v4.0.9 https://github.com/ruby/ruby ../cruby
./ir.sh Util/cruby-test/runner.rb ../cruby -v ../cruby/test/ruby/test_array.rb      # one file
./ir.sh Util/cruby-test/runner.rb ../cruby -v -n test_flatten ../cruby/test/ruby/test_array.rb
Util/cruby-test/sweep.sh ../cruby out            # every file, one process each
ruby Util/cruby-test/summary.rb out [baseline_out]
```

- `runner.rb` is CRuby's `tool/test/init.rb` without the checkers that need RubyVM or CRuby's
  GC, and without the leak checker.
- `excludes/TestFoo.rb` holds `exclude(:test_bar, "why")` lines (CRuby's `test/.excludes`
  format) for tests that hang or kill the process and cannot pass on IronRuby, so that the
  rest of their file is counted. It is not a list of known failures: a test that merely fails
  stays in.
- `baseline.txt` is `summary.rb`'s output for the last full sweep.
- `PACKAGES.md` cuts the failing files into work packages.

A file "without a summary" hung (exit 124), crashed (134) or let an exception out of the
runner (1); none of its tests are counted, so fixing or excluding the one test that does it is
worth more than any single failure.

Not goals: `test_ast`, `test_compile_prism`, `test_iseq`, `test_rubyvm`, `test_allocation`,
`test_gc_compact`, `test_optimization`, `test_box`, `test_ractor`, `test_yjit*`, `test_zjit`,
`test_shapes`, `test_string_memory`, `test_stack`, `test_default_gems` test CRuby's VM,
compiler, GC and build tree.
