# Dispatch area (perf-areas round, 2026-11-14)

Baseline: wt-perfbase (b6cd208a) Release net10.0 vs CRuby 4.0.9, `Util/rubybench/run.sh`
(RB_WARMUP=3 RB_ITRS=3). The machine ran at load ~50 on 8 cores, so single numbers swing up to 2x;
the A/B numbers below are interleaved (B N N B).

## Ratios IronRuby/CRuby, worst first

| benchmark | IR ms | CRuby ms | ratio |
|---|---|---|---|
| send_cfunc_block | 2281 | 686 | 3.33 |
| 30k_methods | 8019 | 2416 | 3.32 |
| structaset | 547 | 209 | 2.62 |
| setivar | 660 | 324 | 2.04 |
| getivar-module | 887 | 443 | 2.00 |
| send_bmethod | 550 | 278 | 1.98 |
| send_rubyfunc_inline | 4161 | 2251 | 1.85 |
| structaref | 841 | 479 | 1.76 |
| send_rubyfunc_block | 1345 | 926 | 1.45 |
| 30k_ifelse | 13176 | 9380 | 1.40 |
| block_methods | 14333 | 10681 | 1.34 |
| setivar_object | 245 | 197 | 1.24 |
| struct-new-no-escape | 509 | 429 | 1.19 |
| setivar_young | 244 | 223 | 1.09 |
| respond_to / keyword_args / object-new / cfunc_itself | | | 0.50-0.76 |
| attr_accessor / getivar / 30k_variables | | | 0.24-0.37 |

## Kept: Struct member readers in compiled code

Struct accessors are `RubyCustomMethodInfo` rule generators, so the method JIT and the OSR loop
compiler, which inline attr_reader reads (SelfAttrSite), gave up on them: a loop doing `o.a` on a
Struct was not specializable at all. Now the info carries the member index
(`RubyCustomMethodInfo.StructMember`), and `o.x` on a profiled Struct whose x is the member reader
compiles to `JitRuntime.ReadStructMember` (class + global method version check, else deopt).

- structaref (run.sh ms, B N N B): 523 / 159 / 119 / 492 -> ~3.7x faster, now ~0.3x CRuby (479).
- in-process micro (Util/perf/areas/dispatch-like, least of 40): 45 -> 16.5 ns per read; CRuby 23.5.
- Correctness: `Util/perf/areas/dispatch/struct_check.rb` (singleton reader, other Struct class,
  redefinition, frozen) same as CRuby; run.sh --verify structaref/structaset SAME; mspec core/struct
  + kernel/frozen 179 examples, 0 failures, 1 error (the same 1 error as the baseline's core/struct).

The method JIT's writer path (`o.x = v` in a method compiled without a loop, WriteStructMember)
is kept as well; it deopts on a frozen Struct so the FrozenError comes from the real call.

## Tried and rejected

- Struct member **writes in an outlined loop** (WriteStructMemberLogged, undo-logged like
  WriteSelfAttr): the loop became specializable but structaset got slower (581/325 base vs
  1110/653 with it). The store is dominated by boxing the Integer and logging the old value per
  store; the generic site is no worse. Removed.

## Where the rest goes (hypotheses, worst first)

1. send_cfunc_block (3.3x): `ARR.reverse_each {}` / `ARR.index {}` on an empty array - the cost is
   the block-passing library call (BlockParam/Proc creation per call), not the body. Fix: avoid
   materialising the block for library methods that never yield on an empty receiver / lazy BlockParam.
2. 30k_methods (3.3x): one-shot compile of 30k method definitions - start-up/compile cost, not dispatch.
3. setivar / structaset (2x-2.6x): each store of a fresh Integer boxes it (allocation + write
   barrier); CRuby's Fixnums are immediates. A boxed-int cache beyond the small range or keeping
   the slot typed would be needed. Measured: stores of a loop counter ~40-60 ns vs CRuby ~20-26 ns.
4. getivar-module (2x): ivars on a Module (`@x` in a class-level method) go through the
   non-RubyObject path (RubyModule's instance data, no inline shape cache).
5. send_bmethod (2x): define_method methods called through `send` - the BlockDispatcher + Proc
   call per invocation.

## Not verified

The `Util/perf/*-matrix.rb` differential matrices were not run to completion (my invocation of
hot-shapes-matrix with a `list` argument did not do what I assumed and was stopped). Neither matrix
has Struct cases; the Struct paths are covered only by `dispatch/struct_check.rb` and mspec core/struct.
