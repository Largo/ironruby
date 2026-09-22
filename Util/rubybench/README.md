# ruby-bench under IronRuby

[ruby-bench](https://github.com/ruby/ruby-bench) is CRuby's own benchmark suite
(what used to be yjit-bench). This directory runs it against IronRuby and
against CRuby 4.0.6 side by side, so a change to IronRuby can be judged on
whether it made real Ruby code faster, and so that a benchmark that stops
working is noticed.

    Util/rubybench/run.sh                     # every micro benchmark
    Util/rubybench/run.sh fib nbody           # just these
    Util/rubybench/run.sh --group all         # micro + macro
    Util/rubybench/run.sh --verify            # compare the answer, not the time
    Util/rubybench/run.sh --nojit             # extra column: IronRuby -X:NoJIT
    IR_CONFIG=Release Util/rubybench/run.sh   # measure the optimized build

ruby-bench is not vendored. Point the runner at a checkout with `RB_DIR`
(default `/root/workspace/ruby-bench`); the runner never writes inside it,
beyond what a bundled benchmark does to its own `Gemfile.lock`. Results go to
stdout and the per-run logs to a temp directory - no CSV is written into this
repository.

`catalog.rb` is the list of benchmarks: name, group (`:micro` or `:macro`), and
what is known about running it here. `--group` selects from it, so the macro
benchmarks can be added to the catalog without touching the runner.

## How a number is produced

Each benchmark runs in a fresh process per interpreter. ruby-bench's own
harness prints one `#N: NNNms` line per iteration; the runner drops the warmup
iterations (`RB_WARMUP`, default 5) and reports the fastest of the rest
(`RB_ITRS`, default 5). Minimum rather than mean: the noise on a timed loop is
all one-sided, so the best iteration is the honest "how fast can this go", and
it is the statistic `Util/bench/run.sh` already uses.

`RATIO` is IronRuby divided by CRuby, so 1.00 is parity and 5.00 means five
times slower.

Statuses are meant literally:

| Status  | Meaning |
|---------|---------|
| OK      | ran, and produced iteration times |
| FAIL    | raised, or produced no times |
| TIMEOUT | did not finish within `RB_TIMEOUT` (default 600s) |
| N/A     | cannot run here; the note says why, in one line |

N/A is for something IronRuby genuinely cannot do (`Process.fork`), never for a
bug. A benchmark that fails because IronRuby is wrong is a FAIL to be fixed.

## Checking the answer, not just the exit code

A fast wrong answer is the worst outcome, and almost none of these benchmarks
print anything, so exiting 0 proves very little. `--verify` runs each benchmark
once under both interpreters with a replacement harness - generated into the log
directory, so ruby-bench itself is untouched - that calls the benchmark's block
exactly once and prints the value it returned. The two are then compared:

    BENCHMARK                GROUP  VERDICT   NOTE
    fib                      micro  SAME      Integer len=7 2178309
    nqueens                  micro  SAME      Integer len=2 10
    blurhash                 micro  SAME      String len=30 "LFE.@D9F01_2%L%MIVD*9Goe-;WB"

Object addresses are normalized out. Four benchmarks have no comparable value
(they return a live thread, a freshly built tree, a splay tree whose shape
embeds object ids, or an SVG document); they are listed as SKIP with the reason
in `catalog.rb`'s `UNSTABLE_VALUE`, and three of the four raise on a wrong
answer by themselves.

## RSS is missing, and that is not a failure

ruby-bench's `harness-common.rb#get_maxrss` calls `Fiddle::Function`, which
IronRuby does not have yet. `return_results` therefore raises a `NameError` at
the very end of every IronRuby run - *after* the iterations have been timed and
printed. The runner recognizes exactly that one error, keeps the timings, and
says once at the bottom of the table that the memory figures are unavailable.
Nothing in ruby-bench is patched to work around it: when `Fiddle::Function`
lands, the runs simply stop raising.

## Status

All 41 microbenchmarks run and every one of them that has a value to compare
produces CRuby's answer (`--verify`). One is N/A.

Measured with `IR_CONFIG=Release`, `RB_WARMUP=3 RB_ITRS=4`, against CRuby 4.0.6
on a shared box, so the absolute numbers move by tens of percent between runs
and only the shape of the table is worth reading. Times are milliseconds for
the fastest timed iteration; `RATIO` is IronRuby / CRuby.

```
BENCHMARK                GROUP     IR(ms) NOJIT(ms) CRUBY(ms)   RATIO STATUS   NOTE
30k_ifelse               micro       6285      6687      1703    3.69 OK
30k_methods              micro       5984      5462      1927    3.11 OK       NoJIT is faster
30k_variables            micro       9165      9823      1449    6.33 OK
attr_accessor            micro       1016      1086       262    3.88 OK
block_methods            micro      25140     26822      6993    3.60 OK
cfunc_itself             micro        249       728       160    1.56 OK
fib                      micro       1909      1529       373    5.12 OK       NoJIT is faster
gcbench                  micro      19939     16722      3431    5.81 OK       NoJIT is faster
getivar                  micro        559       536        86    6.50 OK
getivar-module           micro       1082      1131       229    4.72 OK
keyword_args             micro       2766      2297       225   12.29 OK       NoJIT is faster
loops-times              micro       1776      1822      1065    1.67 OK
matmul                   micro        719       718       435    1.65 OK
nqueens                  micro        364       273       183    1.99 OK       NoJIT is faster
object-new               micro         46        51        73    0.63 OK
object-new-initialize    micro        658       713       138    4.77 OK
object-new-no-escape     micro        913      1071       263    3.47 OK
respond_to               micro       2641      2529       237   11.14 OK
ruby-xor                 micro        115       134       114    1.01 OK
send_bmethod             micro        745       712       243    3.07 OK
send_cfunc_block         micro        962       985       295    3.26 OK
send_rubyfunc_block      micro        557       568       172    3.24 OK
send_rubyfunc_inline     micro       1962      1995       653    3.00 OK
setivar                  micro        568       552        90    6.31 OK
setivar_object           micro        534       558        92    5.80 OK
setivar_young            micro        553       567        92    6.01 OK
splay                    micro        772       764       154    5.01 OK
str_concat               micro        132       299        63    2.10 OK
string_malloc_pressure   micro         54        71       495    0.11 OK
structaref               micro        255       251       125    2.04 OK
structaset               micro        165       206       113    1.46 OK
struct-new-no-escape     micro       1276      1256       165    7.73 OK
sudoku                   micro        664       625       404    1.64 OK       NoJIT is faster
throw                    micro         48        47        23    2.09 OK
binarytrees              micro       1281      1149       399    3.21 OK       NoJIT is faster
fannkuchredux            micro        668       670       356    1.88 OK
nbody                    micro        476       462       108    4.41 OK
gvl_release_acquire      micro        196       217       133    1.47 OK
ruby-json                micro       1349      1348       336    4.01 OK
blurhash                 micro        966       928       314    3.08 OK
lee                      micro       4726      4391      1197    3.95 OK       needs the victor gem
knucleotide              micro          -         -         -       - N/A      uses Process.fork
```

The middle of the table is where IronRuby lives: most of these land between two
and four times CRuby. Two benchmarks are faster than CRuby -
`string_malloc_pressure`, because `String.new(capacity:)` is a hint IronRuby
does not have to honour with a real allocation, and `object-new`, which is a
plain allocation loop.

What the outliers say:

* `respond_to` (11x) and `keyword_args` (12x) are the two call-shape
  benchmarks still well out of line. `keyword_args` was 23x before the
  keyword-check change; what is left is the per-call binding itself.
* `getivar`, `setivar*` (6x) and `struct-new-no-escape` (8x) say instance
  variable and Struct access are not as direct as CRuby's slot access.
* `30k_variables` (6x) is dominated by compiling 300 methods of 100 locals,
  not by running them.
* `ruby-json` was **103x** before the StringScanner work in this round.

`--nojit` is worth running when a change touches the compiler: a benchmark
where `-X:NoJIT` wins is a benchmark the method JIT is costing. Eight do here,
mostly by a few percent, and the JIT clearly pays for itself on `cfunc_itself`
(249ms against 728ms without it) and `str_concat`.

## Environment

| Variable | Meaning |
|----------|---------|
| `RB_DIR` | ruby-bench checkout (default `/root/workspace/ruby-bench`) |
| `IR_CONFIG` | `Debug` (default) or `Release` - measure in Release |
| `IR` / `RUBY` | the two interpreters (default `./ir.sh` and `ruby`) |
| `RB_TIMEOUT` | seconds per run (default 600) |
| `RB_WARMUP` / `RB_ITRS` | warmup and timed iterations (default 5 and 5) |
| `RB_MIN_TIME` | minimum seconds of timed work per run (default 0) |
| `RB_LOG_DIR` | where the per-run logs go |
| `GEM_HOME` | gem home for the benchmarks that bundle (default `<repo>/.gems`) |
