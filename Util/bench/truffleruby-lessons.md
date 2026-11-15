# What TruffleRuby keeps in Ruby, what in Java, and what IronRuby can take from it

The question: TruffleRuby writes most of its core library in Ruby and is fast. IronRuby
writes most of its core in C#, and the usual fix for a slow prelude method has been to move
it to C#. Can the core get faster across the board without doing that?

Short answer, with the numbers below:

- TruffleRuby's split is not "everything in Ruby". The per-element loops (`Array#each`,
  `map`, `select`, `Hash#each`, `Range#each`), the operators and element access are Java.
  What is Ruby is argument handling, enumerator creation, error paths and the generic
  algorithms, and each of those calls into Java through `Primitive.x`, which is not a
  method call.
- Its Ruby core is fast because of five mechanisms, and only some of them need Graal.
  IronRuby already has an equivalent of three (inline caches per call site, block loops
  compiled together with their iterator, frames without a heap scope). It lacks the other
  two: calls the compiler answers itself (`Primitive`, always-inlined methods) and a
  `yield` the loop compiler can compile.
- Measured on master, a core method written in Ruby costs 2x the same method in C# per
  call and 5-20x per element, and calls that end in a prelude method are at 3.1x CRuby
  where calls that end in C# are at 1.6x. For short receivers, which most are, that is not
  the loop: it is what a Ruby method pays to be entered (a block, `block_given?`, a rest
  parameter, a block passed on).
- Three things were implemented here, all in the entry path, none of them moving code to
  C#: `block_given?` answered from the frame (a guarded call that does nothing else takes
  0.80 of the time), methods with a rest parameter running without a scope (0.67-0.70), and
  `inject` and `reduce` taking their arguments without a rest Array (0.61-0.73). Numbers,
  checks and what is still open are in section 6.

Everything here was measured on Release builds of net10.0, one process per run, modes
interleaved, on-CPU time of the benchmark thread (`/proc/thread-self/schedstat`), pinned
to two quiet cores of a shared eight-core machine. A figure is the best of 7 runs in each
of two to four processes and is good to about 15%; every benchmark prints a checksum that
must be CRuby's.

    ruby Util/bench/core-inventory.rb                      # the IronRuby inventory below
    IR_CONFIG=Release ./ir.sh Util/bench/prelude-cost.rb   # Ruby against C#, the parts, openings, wrappers
    IR_CONFIG=Release ./ir.sh Util/bench/core-small.rb     # 67 core methods on four elements
    ruby Util/block-given-matrix.rb; ruby Util/light-rest-matrix.rb   # the differential matrices

## 1. TruffleRuby's inventory

`oracle/truffleruby` at `ea04582e` (2026-11-14). Ruby core is
`src/main/ruby/truffleruby/core/` (118 files, 38,269 lines), Java core is
`src/main/java/org/truffleruby/core/`.

Whole tree: **2,762** `def`s in Ruby core, **931** `@CoreMethod` annotations (1,042 method
names; 558 annotations under `core/`, the rest is interop, C extensions and debugging)
and **518** `@Primitive` annotations. Ruby core calls **379** distinct `Primitive.x` names
at 3,150 places; the most used are `is_a?` 443, `nil?` 319, `undefined?` 171,
`convert_with_to_int` 148, `class` 141, `convert_with_to_str` 108, `always_split` 66,
`equal?` 53, `single_block_arg` 50, `rb_num2int` 45.

| class | `def` in Ruby | `@CoreMethod` / `@Primitive` in Java |
|---|---:|---:|
| Array | 87 (+9 of Enumerable's copied in by alias) | 29 / 17 |
| Hash | 54 | 17 / 7 |
| String | 87 (+36 in `Truffle::StringOperations`, `Splitter`) | 40 / 46 |
| Integer | 36 | 25 / 11 |
| Float | 13 | 24 / 5 |
| Enumerable | 60 | none |
| Kernel | 56 | 43 / 28 |
| Range | 22 | 9 / 3 |
| Struct | 29 | none |
| Comparable | 8 | none |
| Symbol | 19 | 7 / 0 |
| Proc | 8 | 11 / 7 |
| Module | 6 | 67 / 5 |
| Numeric | 45 | none |
| Enumerator | 69 | none |

Where the methods one would guess are:

| | Ruby | Java |
|---|---|---|
| Array | `each_with_index`, `inject`, `any?`, `all?`, `find`, `each_slice`, `sort_by`, `zip`, `flatten`, `uniq`, `sum`, `min`, `max`, `first`, `last`, `<=>`, `==`, `hash` | `each`, `map`, `map!`, `select`, `reject`, `include?`, `[]`, `[]=`, `<<` |
| Integer | `times`, `upto`, `downto`, `step` (Numeric) | `+ - * / < ==` ..., `to_s` |
| Range | `map` (primitive for Integer ranges, else `super`) | `each`, `step` |
| Hash | `map`, `select`, `merge`, `transform_values`, `dig`, `fetch`, `each_with_object` | `each`, `[]`, `[]=` |
| Enumerable | all of it | - |
| Kernel | `loop`, `tap`, `then`, `Integer()`, `Array()` | `respond_to?`, `send`, `is_a?`, `nil?`, `freeze`, `dup`, `instance_variable_get`, `block_given?` |
| Comparable | all of it | - |
| String | `split`, `sub`, `gsub`, `start_with?`, `index`, `to_i`, `to_sym`, `strip`, `center` | `+`, `<<`, `==`, `each_char` |

Many of the Ruby ones are a type test and one primitive: `Array#inject` is
`Enumerable#inject`, which starts with
`return Primitive.array_inject(self, initial, sym, block) if Primitive.is_a?(self, ::Array)`;
`Array#==` asks `Primitive.array_equal?` and falls back to Ruby when that answers
`undefined`; `Hash#fetch` is `Primitive.hash_get_or_undefined` plus the error path.
`Array#find`, `Integer#times`, `Kernel#loop`, `Kernel#tap` and `Comparable` are plain
Ruby with `while` and `yield`.

CRuby does the same on a smaller scale since 3.3/3.4: `Integer#times`, `Array#first`,
`Kernel#tap`, `then` and `loop` are Ruby (`numeric.rb`, `array.rb`, `kernel.rb`, with
`Primitive.attr! :inline_block`, `Primitive.cexpr!`), and `Array#each`, `map` and `select`
are Ruby only while a JIT is on (`with_jit do` in `array.rb`).

### Their rule

`doc/contributor/how-to-guide.md`, "How to choose between Ruby and Java":

> Prefer Ruby if possible (easier to maintain & read), Java can make sense in some cases
> if it can gain much in terms of performance (e.g., extra inline cache or so which is not
> possible in Ruby).

and: "performance critical code paths are implemented in Java and complex but less
critical paths are implemented in Ruby as fallback specializations" (the example is
`Float#==`, whose Java node calls Ruby's `equal_fallback`). `doc/contributor/primitives.md`:
"Primitives ... do not involve a method call. The Primitive Truffle AST node is added
directly in the Truffle AST and so they always execute inline ... they are also the most
efficient way to call some Java logic from some Ruby code." And a constraint that matters
when copying it (`CoreMethodAssumptions.java`): operations inlined at the call site "may
only target core methods defined in Java ... never core methods defined in Ruby".

### The mechanisms

1. **`Primitive.x(args)` is replaced when the file is parsed**, by the node the name is
   registered for (`YARPTranslator`, `PrimitiveNodeConstructor`): no lookup, no argument
   array, no call, no way to redefine it, arity checked at parse time. It is only accepted
   in core files (paths starting `<internal:core>`). The annotation can ask for work on the
   arguments (`lowerFixnum`, `raiseIfFrozen`). `undefined` in a core file is a literal of a
   "not provided" object and `Primitive.undefined?` an identity compare, which is how core
   methods take optional arguments without a rest Array: `def first(n = undefined)`.
2. **Methods that are always inlined into their caller** (`@CoreMethod(alwaysInlined =
   true)`, 41 of them): `block_given?`, `binding`, `respond_to?`, `__send__`, `dup`,
   `frozen?`, `Proc#call`, `Class#new`, `Integer#==`, `String#==`. They get the caller's
   frame and have none of their own.
3. **Inline caches, and a copy of the callee per call site** (splitting). Every call site
   caches up to 8 targets. A Java core method that takes a block is cloned per call site
   by default (`needsBlock ? Split.ALWAYS : Split.HEURISTIC`), and 66 Ruby core methods ask
   for it with `Primitive.always_split` - those that call a block in a loop (`times`, `upto`,
   `step`, `Enumerable#map`, `each_with_index`, `inject`), and those whose regexp or format
   argument is usually a literal (`gsub`, `split`, `=~`, `printf`, `pack`). A copy sees one
   block and one receiver type, so its caches are monomorphic. The documented price is
   memory and warm-up: "Should only be used when the trade-off is worth it".
4. **The block is inlined into the copy.** `yield` and `Proc#call` cache on the block's
   call target and, alone among calls, force inlining (`callNode.forceInlining()`). With the
   split copy that puts the block's body inside `Integer#times`' `while`. Partial evaluation
   then removes the frame, the `Proc` and the boxed loop counter.
5. **Operators in core Ruby are not sends.** `i < self` and `i += 1` are parsed into
   guarded nodes for the built-in operation, each under a "not redefined" assumption per
   class and method that invalidates compiled code when broken. Branches (`if`, `unless`,
   `&&`) carry profiles, per copy.
6. **Coercions are one node**: `Primitive.rb_num2int(n)` returns an `int` argument as it
   is and only for anything else calls a Ruby method (`Truffle::Type.rb_to_int_fallback`)
   that sends `to_int` and checks the result.
7. **Loading**: the 118 files are parsed and run at start, in a native image at build time
   (pre-initialization); method bodies are translated on first call
   (`lazy-translation-core`).
8. **Backtraces** do not hide Ruby core frames; they are labelled `<internal:core>` as
   CRuby's are. Always-inlined methods have no frame at all, and an exception from one is
   re-raised through a real call to get one.

## 2. IronRuby's inventory

`ruby Util/bench/core-inventory.rb` on master (`19f56f95`): `[RubyMethod]` names in
`Src/Libraries/{Builtins,Extensions}` against `def`s in the prelude
(`Src/StdLib/ironruby/ruby4.rb`, 12,559 lines).

| class | C# only | Ruby | of which replace a C# method | Ruby and take a block |
|---|---:|---:|---:|---:|
| Array | 83 | 25 | 5 | 16 |
| Hash | 61 | 17 | 0 | 5 |
| String | 119 | 25 | 7 | 7 |
| Integer | 61 | 1 | 0 | 0 |
| Float | 33 | 5 | 0 | 0 |
| Enumerable | 31 | 35 | 11 | 31 |
| Kernel | 158 | 24 | 9 | 5 |
| Range | 21 | 8 | 1 | 6 |
| Struct | 18 | 6 | 3 | 3 |
| Comparable | 7 | 0 | 0 | 0 |
| Symbol | 30 | 3 | 0 | 0 |
| Proc | 15 | 0 | 0 | 0 |
| Module | 88 | 1 | 0 | 0 |
| Numeric | 15 | 16 | 4 | 0 |
| Enumerator | 15 | 11 | 2 | 6 |
| these fifteen | 755 | 177 | 42 | 79 |

Whole tree: 2,235 `[RubyMethod]` names, 620 prelude `def`s of core methods and 294 prelude
helpers named `__x__`. So IronRuby is about 80% C# where TruffleRuby is about 75% Ruby.

The prelude methods that replace a C# one are mostly wrappers: they take `*args, &block`,
check or convert something the 1.9-era C# method does not, and call it under an alias.
`Enumerable#inject`, `reduce`, `each_with_index`, `min`, `max`, `zip`, `each_slice`,
`each_cons`, `sort_by`, `take_while`, `drop_while`; `Array#zip`, `uniq`, `shuffle`;
`String#strip`, `lstrip`, `rstrip`, `each_line`; `Kernel#loop`, `String`, `puts`, `sleep`,
`rand`. (`Hash#dig` and `transform_values` went the other way in the last perf round: the
prelude's definitions are still there, guarded by `unless method_defined?`, and C# has them.)
36 prelude methods have the signature `(*args, &block)`, 96 take a rest parameter.

What IronRuby has in place of `Primitive` is a naming convention: about 230 C# methods
registered as private methods named `__x__` (`__numeric_sum__`, `__list_tally__`,
`__ir_responds_to__`), which the prelude calls 1,928 times as ordinary methods on self -
through a call site, bound by a rule, visible to `Kernel.private_instance_methods`.

What it already has of the mechanisms above:

| TruffleRuby | IronRuby on master |
|---|---|
| inline caches per call site | DLR call sites; the inner sites of a C# method (`CallSiteStorage`) are made per bound rule, so a C# core method already has its caches per caller. A *Ruby* core method's inner sites are shared by all its callers. |
| split copy + inlined block | for nine C# iterators only: `BlockLoopSite` compiles `times`, `upto`, `downto`, `Array#each`, `map`, `select`, `any?`, `all?`, `none?` together with a block literal as one typed `while` (Util/bench/README.md) |
| frames without heap objects | light frames: a method whose body never needs its `RubyMethodScope` makes none (`MethodDefinition.TransformLightFrame`) |
| always-inlined methods | `DispatchIntrinsics`: `respond_to?(:sym)`, `send(:sym)`, `!=` are answered by the rule itself |
| not-redefined assumptions | `RubyModule.GlobalMethodVersion` at a JIT body's entry; class versions in every rule |
| core frames out of backtraces | `RubyStackTraceBuilder._InternalFiles` |
| lazy translation | method bodies are transformed and compiled on first call |
| `Primitive` | no |
| `yield` in compiled loops | no: the typed loop compiler gives up on a `yield`, so a Ruby `while ... yield` is all call sites |

## 3. What a core method in Ruby costs today

Master, ns per call of the method from a method in a `while` loop (lower is better).
`rb_x` is the method written the way TruffleRuby and CRuby write it - `return to_enum(..)
unless block_given?`, a `while` over `self[i]`, `yield` - defined in
`Util/bench/prelude-cost.rb`.

| method | CRuby | C#, no JIT | C# | Ruby, no JIT | Ruby | Ruby / C# | Ruby / CRuby's |
|---|---:|---:|---:|---:|---:|---:|---:|
| `each`, four elements | 260 | 376 | 370 | 783 | 684 | 1.8 | 2.2 |
| `map`, four elements | 322 | 457 | 436 | 918 | 852 | 2.0 | 1.7 |
| `select`, four elements | 317 | 408 | 420 | 860 | 888 | 2.1 | 2.3 |
| `times`, four elements | 255 | 408 | 360 | 691 | 640 | 1.8 | 2.3 |
| `inject`, four elements | 479 | 940 | 992 | 652 | 632 | 0.6 | 2.3 |
| `each`, 1000 elements | 47181 | 44798 | 6250 | 97600 | 96760 | 15.5 | 2.0 |
| `map`, 1000 elements | 49600 | 38375 | 19996 | 102400 | 104000 | 5.2 | 1.8 |
| `select`, 1000 elements | 46394 | 38378 | 19200 | 100804 | 100000 | 5.2 | 1.7 |
| `times`, 1000 elements | 39199 | 43037 | 3200 | 69590 | 64774 | 20.2 | 1.5 |
| `inject`, 1000 elements | 62200 | 40800 | 37596 | 94398 | 99167 | 2.6 | 2.1 |
| `first(2)` | 104 | 136 | 140 | 240 | 236 | 1.7 | 1.6 |
| `first` | 40 | 72 | 76 | 136 | 132 | 1.7 | 1.9 |
| `empty?` | 36 | 84 | 56 | 112 | 112 | 2.0 | 2.0 |
| `nil?` | 40 | 80 | 88 | 108 | 76 | 0.9 | 1.5 |
| `zero?` | 40 | 80 | 72 | 112 | 88 | 1.2 | 1.6 |
| `between?` | 116 | 108 | 112 | 124 | 120 | 1.1 | 1.9 |
| `itself` | 52 | 100 | 80 | 100 | 88 | 1.1 | 1.8 |
| `tap { }` | 76 | 272 | 276 | 299 | 284 | 1.0 | 3.7 |

"No JIT" is `-X:NoJIT -X:NoOSR`; the last column is the Ruby version on IronRuby against the
same Ruby source on CRuby.

- **Per call** a Ruby iterator costs about twice the C# one (four elements), with or
  without the JIT: the JIT does not compile a method that yields.
- **Per element** it is 2.2-2.7x without the JIT and 5-20x with it, because the JIT compiles
  the C# iterator's loop together with the block (`each` 6 ns per element, `times` 3)
  and cannot compile the Ruby one at all. (`inject` is not one of the iterators it
  compiles that way: 2.6x.)
- **The same Ruby source runs at about half CRuby's speed** (last column: 1.5-2.3x), in the
  loop as much as at entry.
- **A wrapper is worse than either**: `inject`, the prelude's `(*args, &block)` wrapper
  around the C# method, costs half again as much as `rb_inject`, a complete fold in Ruby.
- **Without a block** Ruby and C# are close: `rb_zero?`, `rb_nil?`, `rb_itself`,
  `rb_between?` are within noise of the C# method, since such a method is inlined by the
  JIT or runs as a light frame; `rb_first` and `rb_empty?` are 1.7-2x (one more call
  inside).

Where the time goes, for a Ruby method called with a block over four elements
(`e_*` in `prelude-cost.rb`; each line is the method doing only what it says):

| the method's body | CRuby | IronRuby |
|---|---:|---:|
| a C# method called (`size`), for scale | 40 | 56 |
| `def e0; end`, no block | 56 | 72 |
| the same, given `{ \|x\| x }` | 56 | 188 |
| the same, given `{ \|x\| s += x }` | 56 | 196 |
| `block_given?` | 72 | 280 |
| `return to_enum(:m) { size } unless block_given?; self` | 76 | 264 |
| `yield 1` | 84 | 252 |
| four `yield`s | 184 | 412 |
| `size` four times | 76 | 252 |
| `self[0]` ... `self[3]` | 84 | 208 |
| `while i < 4; i += 1; end` | 124 | 180 |
| `while i < size; i += 1; end` | 140 | 380 |
| `while i < 4; yield i; i += 1; end` | 264 | 524 |
| `while i < size; yield self[i]; i += 1; end` | 280 | 620 |
| `def m(&b); end` | 76 | 204 |
| `def m(&b); b.call(1); end` | 108 | 288 |
| `def m(*a); end` | 146 | 264 |
| `def m(*a, &b); e_y1(*a, &b); end` | 194 | 464 |
| `while i < size` over 1000, per element | 15.2 | 32.8 |
| `while i < n; self[i]` over 1000, per element (compiled) | 21.6 | 7.2 |
| the whole loop over 1000, per element | 49.6 | 94.4 |

Read as differences (this run, and the range over three more runs where it moved):

| part | IronRuby | CRuby |
|---|---:|---:|
| calling a Ruby method without a block (over a C# one) | +16 ns | +16 |
| passing a block literal to it | **+104 to +116** | 0 |
| `block_given?` in it | **+56 to +84** | +16 |
| the opening `return to_enum(:m) { size } unless block_given?` | **+68 to +98** | +20 |
| one `yield` | +53 to +78 | +28 to +37 |
| one call of a C# method on self (`size`, `self[i]`) | +3 to +14 | +5 |
| one `while` iteration of `i < size; i += 1` (not compiled: it has a call) | 33 to 40 | 15 |
| one iteration with a `yield` | 94 to 104 | 50 |
| a rest parameter `*args` | **+68 to +130** | +75 to +90 |
| naming the block `&block` | +8 to +42 | +20 |
| one layer of `inner(*args, &block)` | **+200 to +275** | +110 |

So of the 310 ns between `rb_each` and `each` over four elements, about 190 are entry
cost (the opening, and what `yield` costs over a C# iterator's block call), paid once per
call whatever the receiver's size - and short receivers are most receivers. The loop is
the smaller part until the receiver is long, and then it is all of it.

And the prelude is where IronRuby is far from CRuby. `Util/bench/core-small.rb`, 67 calls
of core methods on a four-element receiver, ns per call, worst ratio to CRuby 4.0.9 first;
the side is from `WHERE=... ruby Util/bench/core-inventory.rb`. The last two columns are
this branch (section 6); only the `inject`, `reduce` and `sum` lines were meant to move,
and the rest show what two runs of unchanged code differ by.

| call | defined in | CRuby | master | master / CRuby | branch | branch / master |
|---|---|---:|---:|---:|---:|---:|
| `Array#dig(0, 1)` | Ruby | 64 | 968 | 15.12 | 868 | 0.90 |
| `inject(:+)` | Ruby (replaces C#) | 121 | 807 | 6.67 | 492 | 0.61 |
| `sum { }` | Ruby | 252 | 1640 | 6.51 | 1516 | 0.92 |
| `Kernel#String` | Ruby (replaces C#) | 52 | 324 | 6.23 | 300 | 0.93 |
| `Array#sum` | Ruby | 60 | 316 | 5.27 | 300 | 0.95 |
| `Enumerable#take` | C# | 85 | 396 | 4.69 | 360 | 0.91 |
| `to_h { }` | Ruby | 593 | 2680 | 4.52 | 2752 | 1.03 |
| `(1..4).sum` | Ruby | 84 | 332 | 3.95 | 328 | 0.99 |
| `Kernel#tap` | Ruby | 76 | 300 | 3.95 | 276 | 0.92 |
| `Enumerable#min_by` | Ruby | 491 | 1752 | 3.57 | 1540 | 0.88 |
| `Hash#sum { }` | Ruby | 528 | 1872 | 3.55 | 1756 | 0.94 |
| `Hash#any?` | C# | 276 | 971 | 3.52 | 1024 | 1.05 |
| `Kernel#then` | Ruby | 83 | 264 | 3.16 | 248 | 0.94 |
| `String#center` | C# | 137 | 432 | 3.15 | 420 | 0.97 |
| `Enumerable#find` | C# | 204 | 620 | 3.04 | 648 | 1.04 |
| `Enumerable#take_while` | Ruby (replaces C#) | 240 | 724 | 3.02 | 728 | 1.01 |
| `Array#uniq` | Ruby (replaces C#) | 205 | 592 | 2.89 | 532 | 0.90 |
| `Enumerable#flat_map` | Ruby | 840 | 2223 | 2.65 | 2300 | 1.03 |
| `Array#max` | C# | 52 | 132 | 2.54 | 140 | 1.06 |
| `Array#min` | C# | 52 | 128 | 2.46 | 124 | 0.97 |
| `each_with_index.map` | - | 1016 | 2476 | 2.44 | 2255 | 0.91 |
| `Enumerable#filter_map` | Ruby | 484 | 1164 | 2.40 | 1148 | 0.99 |
| `String#start_with?` | C# | 105 | 252 | 2.39 | 264 | 1.05 |
| `(1..4).inject` | Ruby (replaces C#) | 451 | 1064 | 2.36 | 780 | 0.73 |
| `Array#include?` | C# | 60 | 132 | 2.20 | 128 | 0.97 |
| `Hash#each_pair` | C# | 298 | 640 | 2.15 | 648 | 1.01 |
| `Enumerable#tally` | Ruby | 318 | 656 | 2.06 | 636 | 0.97 |
| `Enumerable#each_cons` | Ruby (replaces C#) | 700 | 1376 | 1.97 | 1260 | 0.92 |
| `Hash#each` | C# | 300 | 588 | 1.96 | 604 | 1.03 |
| `Kernel#Array` | C# | 52 | 100 | 1.92 | 92 | 0.92 |
| `Enumerable#reduce` | Ruby (replaces C#) | 480 | 916 | 1.91 | 660 | 0.72 |
| `Array#zip` | Ruby (replaces C#) | 427 | 808 | 1.89 | 764 | 0.95 |
| `Array#minmax` | C# | 117 | 220 | 1.89 | 212 | 0.96 |
| `Enumerable#inject` | Ruby (replaces C#) | 477 | 896 | 1.88 | 620 | 0.69 |
| `Enumerable#each_slice` | Ruby (replaces C#) | 599 | 1094 | 1.83 | 1052 | 0.96 |
| `Kernel#loop` | Ruby (replaces C#) | 306 | 548 | 1.79 | 548 | 1.00 |
| `Hash#transform_values` | C# | 328 | 560 | 1.71 | 600 | 1.07 |
| `Hash#dig` | C# | 52 | 88 | 1.69 | 96 | 1.09 |
| `Enumerable#each_with_index` | Ruby (replaces C#) | 494 | 825 | 1.67 | 820 | 0.99 |
| `Integer#upto` | C# | 260 | 428 | 1.65 | 400 | 0.93 |
| `Struct class .new` | - | 159 | 256 | 1.61 | 268 | 1.05 |
| `min { }` | C# | 256 | 400 | 1.56 | 396 | 0.99 |
| `Array#reverse_each` | C# | 268 | 415 | 1.55 | 384 | 0.92 |
| `Enumerable#all?` | C# | 248 | 384 | 1.55 | 404 | 1.05 |
| `Kernel#Integer` | C# | 68 | 104 | 1.53 | 100 | 0.96 |
| `Integer#times` | C# | 256 | 388 | 1.52 | 416 | 1.07 |
| `Enumerable#partition` | C# | 584 | 872 | 1.49 | 804 | 0.92 |
| `Enumerable#any?` | C# | 244 | 360 | 1.48 | 388 | 1.08 |
| `Array#each_index` | C# | 260 | 380 | 1.46 | 380 | 1.00 |
| `Range#each` | C# | 256 | 372 | 1.45 | 388 | 1.04 |
| `Array#find_index` | C# | 208 | 300 | 1.44 | 304 | 1.01 |
| `count { }` | C# | 252 | 360 | 1.43 | 340 | 0.94 |
| `Hash#select` | C# | 371 | 528 | 1.42 | 532 | 1.01 |
| `Array#each` | C# | 264 | 368 | 1.39 | 375 | 1.02 |
| `first(2)` | C# | 91 | 124 | 1.37 | 128 | 1.03 |
| `between? (via <=>)` | C# | 224 | 304 | 1.36 | 288 | 0.95 |
| `Numeric#step` | C# | 304 | 399 | 1.31 | 396 | 0.99 |
| `(1..4).map` | C# | 590 | 772 | 1.31 | 760 | 0.98 |
| `Hash#fetch` | C# | 60 | 76 | 1.27 | 84 | 1.11 |
| `Hash#map` | C# | 632 | 800 | 1.27 | 788 | 0.99 |
| `Enumerable#group_by` | Ruby | 666 | 840 | 1.26 | 880 | 1.05 |
| `Hash#to_a` | C# | 283 | 348 | 1.23 | 332 | 0.95 |
| `map(&:to_s)` | C# | 573 | 700 | 1.22 | 636 | 0.91 |
| `Array#sort_by` | C# | 726 | 816 | 1.12 | 856 | 1.05 |
| `String#each_char` | C# | 592 | 632 | 1.07 | 576 | 0.91 |
| `Array#each_with_object` | C# | 629 | 444 | 0.71 | 432 | 0.97 |
| `Comparable#clamp` | C# | 163 | 112 | 0.69 | 100 | 0.89 |

The 25 calls that end in a prelude method are at 3.07x CRuby (geomean; median 2.9), the
40 that end in C# at 1.63x (median 1.5). `Array#each_with_object` and `Comparable#clamp`
beat CRuby; `Enumerable#take`, `Hash#any?`, `Enumerable#find` and `String#center` are the
C# methods out of line.

## 4. What transfers, ranked

By what it gains for what it costs, with what was measured.

1. **Calls the compiler answers itself, for what a frame knows** (TruffleRuby's
   always-inlined `block_given?`). *Done here for `block_given?`*, see section 6. Next of
   the same kind, each a rule in `DispatchIntrinsics` and a branch in the light-frame
   rewriter: `defined?(yield)` anywhere in a method (today +75 to +180 ns unless it is the
   first statement's guard), and `__method__` and `__callee__`, which read the frame's name
   the way `block_given?` read its block. Needs: nothing new. Risk: each must stay the
   method's own call when the method is redefined, missing or refined - the matrix for
   `block_given?` has the cases.
2. **Frames without a scope for the shapes core methods have.** *Done here for a rest
   parameter.* Still excluded and measured: a method that passes its block or a splat on
   (`inner(&block)`, `inner(*args)`, `inner(*args, &block)`: +94 to +275 ns per layer, and
   the shape of 36 prelude wrappers and of every delegator in user code), `super` (hot
   `initialize` methods), keywords, `rescue`. TruffleRuby has no such list because partial
   evaluation removes the frame whatever is in it; here each shape has to be let in by hand,
   and the block-passing one is the next that pays. Risk: this is the code where the perf
   ledger records most of its faults; each shape needs its own matrix.
3. **Arguments by marker, not by rest Array** (`def first(n = undefined)`).
   *Done here for `inject`, `reduce`, `Array#sum`.* A prelude method that takes
   `(*args, &block)` only to count the arguments pays +110 ns for the Array and the scope,
   and +200 for splatting it again. An optional parameter with a "not given" marker
   (`n = (no_n = true; nil)`) costs +10 ns and keeps the method a light frame. Left:
   `each_with_index`, `min`, `max`, `min_by`, `max_by`, `zip`, `reverse_each`,
   `String#strip`/`lstrip`/`rstrip`, `Array#to_h`, `Hash#transform_keys`, `count`. Needs
   only Ruby. Risk: the arity error a rest parameter raised by hand becomes the automatic
   one, so each needs its messages compared with CRuby's (`inject` keeps a rest parameter
   for exactly that).
4. **`Primitive.x` proper**: a name the compiler turns into a direct call of a static C#
   method, accepted only in the prelude. Not built. Needs: the name resolved in
   `MethodCall.Transform` when the file is the core prelude (`Loader.IsLoadingCorePrelude`,
   or the path, for the other files under `Src/StdLib/ironruby`), a table from name to
   `MethodInfo` filled from an attribute by the initializer generator, and the prelude's
   `__x__` calls renamed. Gains: about 8-12 ns per call (the measured cost of a bound call
   site of a C# method on self), no rule to bind for the site (0.2-0.4 ms each on first
   use; start-up binds about 760 rules), 230 private methods gone from `Kernel` and the
   core classes, and calls the method JIT can type because their target is known. The
   first primitives worth having are the ones TruffleRuby uses most - `is_a?`, `nil?`,
   `undefined?`, `class`, `equal?`, `rb_num2int`, `convert_with_to_str` - because the
   prelude asks those questions with real sends today (`x.is_a?(Integer)`, `::Hash === x`,
   `__array_to_int__(key)`). Risk: low, it is confined to the prelude; a primitive is not a
   method, so nothing may `send` it.
5. **The loop compiler compiles `yield`.** A Ruby `while ... yield` costs ~100 ns per
   element against 9 for a C# iterator compiled with its block, and this is the one thing
   that decides whether an iterator can live in Ruby. Needs, per the ledger's own
   assessment: the `BlockParam` protocol (break, next, redo, return) in the emitter, a deopt
   after an effect that continues in generic code instead of re-running the body, and a
   frame for the block that backtraces still show. Large. Until it exists the per-element
   loops belong in C#, which is also where TruffleRuby has them.
6. **More iterators in the block-loop tier.** The cheap form of always-split: `BlockLoopSite`
   takes nine iterators today. `each_with_index`, `inject`, `each_with_object`, `reject`,
   `find`, `count`, `sum` with a block, `Hash#each` and `Range#each` over Integers have the
   same shape. Gains 3-10x per element on long receivers of those; nothing on short ones.
7. **Copies of a Ruby core method per call site.** A prelude method's inner sites are
   shared by every caller, so `each` inside `Enumerable#each_entry` sees every class that
   includes Enumerable. A copy per call site (or per receiver class) would make them
   monomorphic. Needs a place to hang the copy (the rule already is per site) and a compile
   per copy; .NET never tiers a `DynamicMethod`, so each copy is compiled once at full
   optimization. Worth it only for the few methods TruffleRuby marks, and only after 5.
8. **The prelude compiled ahead of time.** Parsing and transforming it is about 260 ms of
   a 0.85 s start; `Util/aot` already saves it as a DLL. It moves the first run, not the
   steady state.

Moving C# iterators into Ruby "once Ruby-to-Ruby calls are cheap" is not on the list: it
needs 5 first, and even then TruffleRuby's own answer is to keep `Array#each`, `map`,
`select`, `Hash#each` and `Range#each` native and write what sits around them in Ruby.

### A rule for IronRuby

TruffleRuby's, adjusted to what was measured:

- **C#**: operators, element access, the loop that touches every element, and anything
  that needs the caller's frame. A C# method that loops over a block joins the block-loop
  tier if it is one of the common ones.
- **Ruby**: argument handling, enumerator creation, error paths, the generic `Enumerable`
  algorithm, and everything that is not hot. Written so that it stays a light frame:
  optional parameters with a marker rather than `*args`; `block_given?` or
  `defined?(yield)` rather than `&block` unless the block is passed on; a type test and one
  call of a C# helper for the common receiver, then the generic body.
- A slow prelude method is first a method with an expensive entry. Measure
  (`IR_LIGHT_FRAMES=why` says why it makes a scope) before moving it to C#.

The skills in `.claude/skills` are these as repeatable steps: `/perf-core` takes one slow
prelude method (item 3), `/perf-frame` one more frame shape or one more call the frame
answers (items 1 and 2), and `/perf-ab` is how either is measured (`Util/perf/ab.rb`).

## 5. What does not transfer

- **Partial evaluation.** Truffle specializes the interpreter itself to the program, so a
  split copy with an inlined block becomes one compilation unit whatever the code looks
  like. .NET compiles methods; what comes closest is what IronRuby does by hand, shape by
  shape: rules as inline caches, the method JIT's typed bodies, `BlockLoopSite`'s merged
  loop, and (Largo/ironruby#219) bodies emitted as methods of a runtime assembly so that
  the .NET JIT tiers them and inlines one into another.
- **Graal's escape analysis.** It is what makes TruffleRuby's frames, `Proc`s, splat
  Arrays and boxed numbers free. .NET's JIT stack-allocates only objects it can prove do
  not escape within one method, and a `RubyMethodScope` or a `Proc` handed to a delegate
  through a call site always escapes. So here an allocation has to be avoided by
  construction: light frames, `[BlockNotKept]` reuse of `Proc` and `BlockParam`, unboxed
  locals in typed bodies. That is why entry cost, not the loop, is where IronRuby's Ruby
  core loses, and why items 1-3 are about not making objects.
- **Invalidating compiled code.** An assumption in Truffle costs nothing while it holds,
  because breaking it throws compiled code away at a safepoint. .NET cannot leave a method
  in the middle, so a guard is a compare that runs: one global version at a JIT body's
  entry, a class version in each rule, an OSR loop left at an iteration boundary.
- **`core-always-clone`**, a copy of every core method per call site: it trades memory and
  warm-up for speed in a system that recompiles in the background and discards copies.
  Here each copy is a `DynamicMethod` that is compiled once and not collected while its
  site lives.
- **Profiles on every branch** of core code feed partial evaluation; without it they
  would only be overhead. The method JIT's argument samples are the equivalent where it
  matters.

## 6. What was implemented

Three changes, each its own commit. None moves a method to C#.

Master is `19f56f95`; "branch" is this branch. CRuby, master and branch ran alternately,
three rounds, best of 7 in each (`prelude-cost.rb`) or two rounds (`core-small.rb`). A
shape nobody touched moves by up to 15% between two such runs, so a ratio between 0.87
and 1.15 is noise.

### `block_given?` is answered from the frame

What was wrong: a method that runs without a scope hands its call sites the scope it was
defined in, and a rule that would read the caller's scope sends the call back to be made
again with the scope made for it. `Kernel#block_given?` reads the caller's scope, so every
`block_given?` in such a method allocated the `RubyMethodScope` the method was written to
avoid and made the call twice. The perf ledger listed it as open ("`block_given?` still
forces the scope").

What it does now: the rule for a `block_given?` site of a light frame, when the name
resolves to `Kernel#block_given?`, answers a marker (`RubyOps.LightBlockGiven`,
`DispatchIntrinsics.TryBuildBlockGiven`), and the frame turns the marker into "is my block
parameter null" (`LightFrameRewriter.LightScopeCall`). It is still a call site with the
receiver test every rule has, so a `block_given?` the class defines, one defined, removed
or aliased over later, a `BasicObject`, `method_missing`, a refinement and a `:c_call`
TracePoint all get the call they got before. `send(:block_given?)` and a `block_given?`
inside a block keep the old path. `IR_LIGHT_BLOCK_GIVEN=0` turns it off.

| shape | CRuby | master | branch | branch / master |
|---|---:|---:|---:|---:|
| `def m; block_given?; end`, with a block | 72 | 280 | 220 | 0.79 |
| `return to_enum(:m) unless block_given?; self` | 72 | 300 | 240 | 0.80 |
| `return to_enum(:m) { size } unless block_given?; self` | 72 | 256 | 228 | 0.89 |
| the same, then `yield 1` | 96 | 336 | 272 | 0.81 |
| the `defined?(yield)` guard, for comparison | 64 | 212 | 212 | 1.00 |
| `rb_each` over four elements | 316 | 684 | 668 | 0.98 |
| `rb_map` over four | 504 | 852 | 768 | 0.90 |
| `rb_select` over four | 394 | 888 | 708 | 0.80 |
| `4.rb_times` | 280 | 640 | 536 | 0.84 |

`rb_each`, `rb_map`, `rb_select` and `rb_times` are the iterators of section 3, which open
with that guard. `Util/block-given-matrix.rb` (73 cases, each run 3000 times so that the
interpreted body, the compiled one and an already-bound site meet it) prints what CRuby
prints with the shortcut on, off, with light frames off, with the JIT off and on the R2R
host with its precompiled prelude. It caught two faults on the way: the marker reaching
`send(:block_given?)`, whose site does not know it, and an argument list shared by two
call nodes.

### A method with a rest parameter runs without a scope

What was wrong: `def log(*messages)` was never a light frame, whatever its body, so 96
prelude methods and every `*args` method in a program made a scope per call on top of the
Array.

What it does now: `IsLightFrameCandidate` lets a rest parameter in. Nothing else was
needed: the caller's rule already makes the Array and passes it as a parameter, and a
scope made on demand is given the same parameter flags as one made at entry. Parameters
after the rest, keywords, `super`, a call with a splatted argument (`inner(*args)`) and a
call that passes the block on still keep a method out, so the prelude's forwarding
wrappers are not light yet. `IR_LIGHT_REST=0` turns it off.

| shape | CRuby | master | branch | branch / master |
|---|---:|---:|---:|---:|
| `def m; size; end`, for scale | 52 | 108 | 100 | 0.93 |
| `def m(*args); args.empty? ? size : args[0]; end`, one argument | 127 | 240 | 168 | 0.70 |
| the same, no argument | 120 | 216 | 144 | 0.67 |
| `def m(a = (no_a = true; nil))`, for comparison | 56 | 112 | 108 | 0.96 |
| `def m(*a); end`, given a block | 146 | 264 | 232 | 0.88 |
| `def m(*a, &b); end`, given a block | 113 | 296 | 220 | 0.74 |

In benchsuite, `micro/dispatch/dispatch-mono-splat-rest` went from 0.291 to 0.239 ms
(three alternating runs each: 0.312, 0.280, 0.291 against 0.252, 0.238, 0.239).
`Util/light-rest-matrix.rb` (89 cases) covers what the Array holds for every way of
passing arguments, that it is the call's own, what `binding`, `local_variables`, `eval`, a
block and a TracePoint see of it, `ruby2_keywords`, arity errors and frames in backtraces.

### `inject`, `reduce` and `Array#sum` take their arguments without a rest Array

What was wrong: `Enumerable#inject` and `reduce` were `(*args, &block)` wrappers that
passed the Array to a checking helper, which splatted it into the C# method;
`Array#sum(*args, &block)` counted its arguments by hand. The wrapper cost more than the
fold.

What they do now: `inject(initial = (no_initial = true; nil), name = (no_name = true;
nil), *extra, &block)` sends the forms that need no check - a block with or without an
initial value, a Symbol with or without one - straight to the C# method, and everything
else to the helper as before (the rest is only there so that three arguments raise CRuby's
error). `Array#sum(init = 0)` is the signature CRuby's has. On the way: `inject` with three
arguments and a block now says "expected 0..2" as CRuby does, where it said "1..2".

| call, four elements | CRuby | master | branch | branch / master |
|---|---:|---:|---:|---:|
| `a.inject(0) { \|s, x\| s + x }` | 477 | 896 | 620 | 0.69 |
| `a.reduce(0) { \|s, x\| s + x }` | 480 | 916 | 660 | 0.72 |
| `a.inject(:+)` | 121 | 807 | 492 | 0.61 |
| `(1..4).inject(0) { \|s, x\| s + x }` | 451 | 1064 | 780 | 0.73 |
| `a.sum` | 60 | 316 | 300 | 0.95 |
| `a.sum { \|x\| x + 1 }` | 252 | 1640 | 1516 | 0.92 |

(Four alternating rounds.) `sum` gains little: its `super` keeps it out of light frames,
and with a block it is `Enumerable#sum`'s generic loop that costs.

The first version of this lost 30% on `inject(:+)`, which had no direct path, and was
caught by `core-small.rb`: a marker parameter is cheap, but two of them plus a rest plus a
helper call are not cheaper than one rest. 86 cases of both methods' edges
(argument counts, names that are not Symbols, a block with a name, empty receivers,
`break`, a redefined `Integer#+`) give master's output; they differ from CRuby in two
places master differs in too (`[1, 2].sum` after `Integer#+` is redefined, and the
NoMethodError for a `BasicObject` given as the name).

### Checks

- **IronRuby.Tests**: PASSED on net10.0 and on net11.0, 1,264 tests, no FAILED line, with
  two new tests (`Method_LightFrameBlockGiven1`, `Method_LightFrameRest1`).
- **ruby/spec** at CI's pins, Release net10.0, `Util/parallel-sweep.sh` against a master
  build: `core` (59 directories, 23,132 examples) and `language` (2,935) have exactly
  master's failing examples, 44 and 1. (`core/dir` was rerun from the master tree's path:
  as root, with `spec` a symlink of another depth, its chroot examples loop forever on any
  build.) After the last
  change to the prelude, `core/enumerable`, `array`, `range`, `hash`, `kernel` and `string`
  were run again: master's results, example for example.
- **The three matrices** above, against CRuby 4.0.9.
- **Util/benchsuite** `truffle/classic`, `truffle/micro` and `awfy`, 3 s per benchmark,
  master then branch: 71 truffle benchmarks at 0.990 of master's time (geomean), 13 AWFY
  at 1.004; CRuby's own figures moved by 0.998 and 1.003 between the two runs. IronRuby
  against CRuby: truffle 1.97 -> 1.95, AWFY 0.90 -> 0.90. The five that moved most were run
  three more times each way: `dispatch-mono-splat-rest` is the gain above;
  `monitor/synchronize` and `hash/buckets-reassign` overlap; `core-read-kilobyte`
  (0.86-1.02 against 0.92-0.97 us) is within its own spread; `core-array-pack-little-C` was
  8% slower all three times (0.376 against 0.403-0.429 us), but `pack('C*')` timed
  directly is 320-345 ns on master and 298-321 on the branch, so it is the harness's loop
  or noise, not `pack`. These benchmarks spend their time in loops and operators, which
  this work does not touch.
- **Util/bench/bench.rb** (67 benchmarks, CRuby, master and branch alternately, best of
  2): geomean 0.986 of master, IronRuby
  against CRuby 2.03 -> 1.99. Most of these take 30-100 ms of wall time, so single lines
  scatter from 0.70 to 1.43 at best of 2; the nine at either end were rerun five times
  alternately. `method_missing` (a `*args` method) is 0.82-0.86 of master; the others
  overlap, `call_opt_sym` in eight more runs too (0.19-0.32 s on both).
- **First run**: `ir -e 1` takes the same time (Release: medians 1.56 s and 1.56 s over 8
  alternating starts; the R2R host 0.59 and 0.585 s). The first 200 iterations of a mix of
  the changed shapes, compile included, take 40-95 ms on master and 55-75 on the branch.

### Not done, and known

- A `block_given?` inside a block of a light method still makes the scope; so does
  `defined?(yield)` anywhere but in the opening guard.
- The 34 other `(*args, &block)` wrappers of the prelude (section 4, item 3), and the
  frame for a method that passes its block or a splat on (item 2), which is what most of
  them need: `String#strip`, `lstrip` and `rstrip`, for one, are `(*selectors)` wrappers
  that cost 145 ns over the C# method they call and stay out of light frames because of
  the `lstrip(*selectors)` further down.
- `Util/aot/check-startup.sh` fails on master and on this branch alike: other threads'
  backtraces through the precompiled prelude lack the `Kernel#loop`, `Set#each` and
  `Kernel#then` frames, and `Time.parse` reports line 202 for 203. Not looked into here.
- `Array#sum` uses a redefined `Integer#+` where CRuby does not (master too).
