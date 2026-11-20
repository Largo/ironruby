# The Ruby C API

What lets a C extension compiled against `ruby.h` be `require`d by IronRuby, on Linux x64 and
Windows x64 (MinGW gcc, as RubyInstaller's CRuby). Three parts:

- **Headers**, `Src/StdLib/include`: CRuby 4.0's, with the macros that read object memory
  turned into calls (`Util/capi/import-headers.rb`, `Src/StdLib/include/README.md`).
- **A native shim**, `libironruby-capi.so` / `libironruby-capi.dll` plus its import library
  `libironruby-capi.dll.a` (`native/`, built by `Util/build-capi.sh` - on Windows from an MSYS2
  UCRT64 or Git Bash shell with MinGW gcc - and shipped next to `IronRuby.dll`). It exports the `rb_*` functions. Most forward through a
  table of function pointers to the `[UnmanagedCallersOnly]` entry points in
  `CApi.Generated.cs`, which call the `CApi.rb_*` methods here; the variadic ones and those
  that catch a raise are C (`native/capi.c`); every other function the headers declare is a
  stub that raises `NotImplementedError`, so an extension loads even if it uses something not
  implemented yet.
- **The runtime side**, this directory: the loader dlopens an ELF `.so`, or LoadLibrary's a PE
  DLL without CLR metadata (`Loader.cs`), refuses one built for CRuby - which reads object
  memory directly - by the `rb_ir_abi_version` marker the headers put in every extension (on
  Windows: by whether it imports `libironruby-capi.dll`), and calls `Init_<name>`.

## Gems

With the C API, `gem install` and Bundler build a gem's C extension as CRuby does (mkmf links it
against the shim, `RbConfig::CONFIG["LIBRUBYARG"]`); one that fails to build is installed with its
Ruby files only, as before, with a warning. The libraries IronRuby implements itself (sqlite3,
nokogiri, json...) keep their built-in version unless `IRONRUBY_NATIVE_GEMS` names them
(comma-separated, or `*`):

    IRONRUBY_NATIVE_GEMS=sqlite3 igem install sqlite3 --platform ruby -- --enable-system-libraries

installs and builds the real sqlite3 gem, and with the variable set `require "sqlite3"` loads it
(`rubygems/defaults/ironruby.rb`). `--platform ruby` because the precompiled platform gems are
built for CRuby. Where IronRuby's own implementation sits under the extension's name
(`msgpack/msgpack.rb`, `oj/oj.rb`, `bcrypt_ext.rb` in `Src/StdLib/ironruby`), that file loads the
native gem's extension instead (`Gem.ironruby_require_native`): a `.rb` anywhere on the load
path would otherwise win over the gem's `.so`.

Gems whose extensions build and give CRuby's results (Linux x64, checked against CRuby 4.0 with
a script exercising each): fast_blank, redcarpet, jaro_winkler, escape_utils, racc (cparse),
digest-murmurhash, unf_ext (C++), and with `IRONRUBY_NATIVE_GEMS` bigdecimal, msgpack, bcrypt
and oj.

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

A function ruby/spec's capi suite does not call gets a check in `Util/capi/selftest`, a small
extension that `ruby Util/capi/selftest.rb` builds and runs on CRuby (the expected results) and
`./ir.sh Util/capi/selftest.rb` on IronRuby; release.yml runs it on the Linux archive.

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

**Windows.** C's `long` is 32-bit there, so the entry points take `CLong`/`CULong`, the C
`long` ranges are 32-bit and a Fixnum has 31 bits (`FIXNUM_MAX` is `LONG_MAX / 2`), as on CRuby
for Windows. Unwinding uses GCC's `__builtin_setjmp`/`__builtin_longjmp`, as CRuby's MinGW build
does: the C runtime's `longjmp` unwinds the SEH way. `RUBY_PLATFORM` stays `x64-mswin64`, so
`Src/StdLib/ironruby/mkmf.rb` turns mkmf's MSVC conventions off and RubyGems is pointed at `make`.

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
- A `T_DATA` object's `dfree` never runs (its handle keeps it alive).
- Not yet: threads created by C calling Ruby, keyword arguments (`rb_keyword_given_p` is always
  false), `rb_io_t` and the IO wait functions, `$_`/`$~` from C (`rb_lastline_*`,
  `rb_backref_set`), st tables, macOS, an MSVC-built extension.
