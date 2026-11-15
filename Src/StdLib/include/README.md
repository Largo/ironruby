# Ruby C API headers

The headers a C extension compiles against on IronRuby (`RbConfig::CONFIG["rubyhdrdir"]`).
They are CRuby 4.0's own, under Ruby's licence (`COPYING`, which offers the 2-clause BSD
licence in `BSDL`), with one kind of change: the inline functions and macros that read an
object's memory directly - `RSTRING_PTR`, `RARRAY_LEN`, `RBASIC(obj)->flags`,
`RB_BUILTIN_TYPE`, the encoding index in the flags - call into IronRuby instead, through the
helpers in `ruby/internal/ironruby.h`. Every changed line is marked `/* IronRuby */`.

Do not edit them by hand: `ruby Util/capi/import-headers.rb [CRUBY_INCLUDE_DIR]` copies a
CRuby 4.0's headers and applies the changes, and fails loudly when one no longer applies.

`x86_64-linux/ruby/config.h` is the platform header CRuby's build generates; it is the only
platform shipped so far. See `Src/Ruby/CApi/README.md` for how the C API works.
