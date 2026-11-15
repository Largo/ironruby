# The Ruby C API

What lets a C extension compiled against `ruby.h` be `require`d by IronRuby. Linux x64 only so
far. Three parts:

- **Headers**, `Src/StdLib/include`: CRuby 4.0's, with the macros that read object memory
  turned into calls (`Util/capi/import-headers.rb`, `Src/StdLib/include/README.md`).
- **A native shim**, `libironruby-capi.so` (`native/`, built by `Util/build-capi.sh` and
  shipped next to `IronRuby.dll`). It exports the `rb_*` functions. Most forward through a
  table of function pointers to the `[UnmanagedCallersOnly]` entry points in
  `CApi.Generated.cs`, which call the `CApi.rb_*` methods here; the variadic ones and those
  that catch a raise are C (`native/capi.c`); every other function the headers declare is a
  stub that raises `NotImplementedError`, so an extension loads even if it uses something not
  implemented yet.
- **The runtime side**, this directory: the loader dlopens an ELF `.so` (`Loader.cs`), checks
  the `rb_ir_abi_version` marker the headers put in every extension (a CRuby-built `.so` reads
  object memory directly and gets a `LoadError` instead), and calls `Init_<name>`.

## Adding a function

1. `ruby Util/capi/prototypes.rb rb_foo` prints its prototype as the headers declare it;
   add that line to `functions.txt`.
2. Write `internal static ... rb_foo(...)` in one of the `CApi.*.cs` files, with the C# types
   `Util/capi/generate.rb` maps the C ones to (`VALUE`/`ID` are `ulong`, pointers are pointers,
   function pointers `IntPtr`).
3. `Util/build-capi.sh` (it regenerates `native/forwarders.generated.c` and
   `CApi.Generated.cs`), then build IronRuby. The function count is checked at load, so a shim
   and an IronRuby from different `functions.txt` refuse to work together.

A variadic function, or one that must catch a raise (`rb_protect` & co), goes in
`native/capi.c` instead.

## Design

**VALUE.** Immediates are encoded as CRuby encodes them on 64-bit - Fixnums, Flonums, static
Symbols (`ID << 8 | 0x0c`), `Qfalse` 0, `Qnil` 4, `Qtrue` 0x14, `Qundef` 0x24 - so the header
macros that only test bits stay inline. Any other object is a handle: an index into a table,
with the low three bits clear and a non-canonical base address, so a macro that still
dereferences one faults at once. An object always gets the same handle (C compares VALUEs
with `==`). IDs are indices into a symbol table, except one-character operators, which are the
character (`rb_funcall(x, '+', 1, y)`).

**Unwinding.** A CLR exception cannot cross C frames on Unix, so IronRuby enters C only through
a trampoline in the shim that `setjmp`s (`rb_ir_call_method`...). A managed entry point that
throws records the exception and sets a thread-local flag; its C forwarder sees the flag and
`longjmp`s to the innermost trampoline, which returns to IronRuby, which rethrows. No managed
frame is skipped by a `longjmp`: the managed callee has already returned. `rb_protect`,
`rb_rescue2` and `rb_ensure` push a jump target of their own. `break` out of a block that C
yielded to and `throw` travel the same way (they are CLR exceptions in IronRuby too).

**Methods.** `rb_define_method` adds a `RubyCustomMethodInfo` whose rule calls
`CMethod.Invoke` with the normalized arguments, so arity errors are IronRuby's own; the block
is kept in a per-thread frame for `rb_yield`, `rb_block_given_p` and `rb_block_proc`.

**Strings and arrays.** `RSTRING_PTR` and `RARRAY_PTR` hand out native copies (mirrors) kept in
step with the object (`CApi.Mirrors.cs`): written back when C calls into Ruby or returns,
refreshed when Ruby changed the object. A mirror stays allocated for the object's lifetime, so
a pointer C kept stays valid memory. `rb_str_new_static` adopts the extension's own buffer.

## Limits of this first version

- **Handles are never freed**: every object C has seen stays alive for the process. Next:
  a handle scope per call into C, plus `rb_gc_register_address`/mark functions for what C
  keeps.
- **Mirrors cost a byte compare per transition** for every string C asked a pointer for in the
  current call. Native storage inside `MutableString` would remove it.
- One runtime per process can use the C API (the shim's `rb_cObject` & co are process-wide).
- Not yet: `rb_define_alloc_func` and `T_DATA`/`TypedData` objects, `rb_scan_args`'s
  non-inline form, global variable hooks, threads created by C, keyword arguments
  (`rb_keyword_given_p` is always false), gem extensions at `gem install` time
  (`rubygems/defaults/ironruby.rb` still skips them), Windows and macOS.
