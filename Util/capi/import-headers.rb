# Copies CRuby's C API headers into Src/StdLib/include and makes them IronRuby's:
#
#   ruby Util/capi/import-headers.rb [CRUBY_INCLUDE_DIR]    # default: RbConfig's rubyhdrdir
#
# Run it with a CRuby 4.0 whose headers are the ones to ship (`ruby -v` 4.0.x). The headers
# are CRuby's own (Ruby's licence, BSD-2-Clause - see Src/StdLib/include/README.md), with one
# kind of change: the inline functions and macros that read an object's memory directly -
# RSTRING_PTR, RARRAY_LEN, RBASIC(obj)->flags, RB_BUILTIN_TYPE... - call a function instead,
# because an IronRuby object is a CLR object and a VALUE that is not an immediate is a handle
# (Src/Ruby/CApi/README.md). Each edit below names the text it replaces and fails loudly when
# that text has moved, so a new CRuby release is a re-run plus a look at what no longer applies.
#
# config.h is the build's own (x86_64-linux for now): mkmf puts rubyarchhdrdir on the include
# path, as for CRuby.
require 'fileutils'
require 'rbconfig'

src = ARGV[0] || RbConfig::CONFIG['rubyhdrdir']
arch = ARGV[1] || RbConfig::CONFIG['arch']
root = File.expand_path('../..', __dir__)
dst = File.join(root, 'Src/StdLib/include')

abort "#{src}/ruby.h not found" unless File.file?(File.join(src, 'ruby.h'))
version = File.read(File.join(src, 'ruby/version.h'))[/RUBY_API_VERSION_MAJOR (\d+)/, 1] + '.' +
          File.read(File.join(src, 'ruby/version.h'))[/RUBY_API_VERSION_MINOR (\d+)/, 1]
abort "headers are Ruby #{version}, not 4.0" unless version == '4.0'

FileUtils.rm_rf(File.join(dst, 'ruby'))
FileUtils.rm_f(File.join(dst, 'ruby.h'))
Dir.glob(File.join(dst, '*-*')).each { |d| FileUtils.rm_rf(d) if File.directory?(d) }
FileUtils.mkdir_p(dst)
FileUtils.cp(File.join(src, 'ruby.h'), dst)
FileUtils.cp_r(File.join(src, 'ruby'), dst)
archdir = File.join(dst, arch, 'ruby')
FileUtils.mkdir_p(archdir)
FileUtils.cp(File.join(RbConfig::CONFIG['rubyarchhdrdir'] || "#{src}/#{arch}", 'ruby/config.h'), archdir)

def edit(dst, file, old, new)
  path = File.join(dst, file)
  text = File.binread(path)
  count = text.scan(old).size
  abort "#{file}: expected exactly one #{old.inspect}, found #{count}" unless count == 1
  File.binwrite(path, text.sub(old) { new })
end

# The helpers the edited headers call. value.h is included by every header that needs VALUE,
# and before any of the ones edited here.
edit dst, 'ruby/internal/value.h', "#endif /* RBIMPL_VALUE_H */",
  "#include \"ruby/internal/ironruby.h\"\n#endif /* RBIMPL_VALUE_H */"
File.write(File.join(dst, 'ruby/internal/ironruby.h'), <<~'C')
  #ifndef RBIMPL_IRONRUBY_H
  #define RBIMPL_IRONRUBY_H
  /**
   * IronRuby: the functions the C API headers call where CRuby's read an object's memory.
   *
   * An IronRuby object lives in the CLR's heap and moves; the VALUE an extension holds for one
   * is a handle, not a pointer to an RBasic. So RBASIC(obj)->flags, RSTRING(str)->len and the
   * like are calls into the runtime here (libironruby-capi, Src/Ruby/CApi). Immediates -
   * Fixnums, Flonums, static Symbols, nil, true, false, undef - are encoded as CRuby encodes
   * them, so the macros that only look at a VALUE's bits stay inline.
   *
   * These are not API: an extension uses the CRuby macros, which work on IronRuby as written.
   */
  #define IRONRUBY 1

  #ifdef __cplusplus
  extern "C" {
  #endif
  VALUE rb_ir_builtin_type(VALUE obj);
  VALUE rb_ir_rbasic_class(VALUE obj);
  VALUE rb_ir_fl_test(VALUE obj, VALUE flags);
  void rb_ir_fl_set(VALUE obj, VALUE flags);
  void rb_ir_fl_unset(VALUE obj, VALUE flags);
  void rb_ir_fl_reverse(VALUE obj, VALUE flags);
  long rb_ir_rstring_len(VALUE str);
  char *rb_ir_rstring_ptr(VALUE str);
  long rb_ir_rarray_len(VALUE ary);
  VALUE *rb_ir_rarray_ptr(VALUE ary);
  VALUE rb_ir_rarray_aref(VALUE ary, long i);
  void rb_ir_rarray_aset(VALUE ary, long i, VALUE v);
  /* Marks an extension as compiled against these headers: IronRuby refuses to load one that
   * lacks it, which is what a CRuby-built extension, reading object memory directly, would. */
  #if defined(__GNUC__) || defined(__clang__)
  __attribute__((weak, visibility("default"))) const int rb_ir_abi_version = 1;
  #endif
  /* CRuby's own, declared here too because encoding.h's inline functions now call them */
  int rb_enc_get_index(VALUE obj);
  void rb_enc_set_index(VALUE obj, int encindex);
  #ifdef __cplusplus
  }
  #endif
  #endif /* RBIMPL_IRONRUBY_H */
C

edit dst, 'ruby/internal/core/rbasic.h',
  "    return RBASIC(obj)->klass;\n",
  "    return rb_ir_rbasic_class(obj); /* IronRuby */\n"

edit dst, 'ruby/internal/value_type.h',
  "    VALUE ret = RBASIC(obj)->flags & RUBY_T_MASK;\n",
  "    VALUE ret = rb_ir_builtin_type(obj); /* IronRuby */\n"

edit dst, 'ruby/internal/fl_type.h',
  "    return RBASIC(obj)->flags & flags;\n",
  "    return rb_ir_fl_test(obj, flags); /* IronRuby */\n"
edit dst, 'ruby/internal/fl_type.h',
  "    rbimpl_fl_set_raw_raw(RBASIC(obj), flags);\n",
  "    rb_ir_fl_set(obj, flags); /* IronRuby */\n"
edit dst, 'ruby/internal/fl_type.h',
  "    rbimpl_fl_unset_raw_raw(RBASIC(obj), flags);\n",
  "    rb_ir_fl_unset(obj, flags); /* IronRuby */\n"
edit dst, 'ruby/internal/fl_type.h',
  "    rbimpl_fl_reverse_raw_raw(RBASIC(obj), flags);\n",
  "    rb_ir_fl_reverse(obj, flags); /* IronRuby */\n"

# An encoding index is not a flag here: a string's encoding is a property of the string.
edit dst, 'ruby/internal/encoding/encoding.h',
  "    VALUE f = /* upcast */ RBIMPL_CAST((VALUE)encindex);\n\n    f <<= RUBY_ENCODING_SHIFT;\n    RB_FL_UNSET_RAW(obj, RUBY_ENCODING_MASK);\n    RB_FL_SET_RAW(obj, f);\n",
  "    rb_enc_set_index(obj, encindex); /* IronRuby */\n"
edit dst, 'ruby/internal/encoding/encoding.h',
  "    VALUE ret = RB_FL_TEST_RAW(obj, RUBY_ENCODING_MASK) >> RUBY_ENCODING_SHIFT;\n\n    return RBIMPL_CAST((int)ret);\n",
  "    return rb_enc_get_index(obj); /* IronRuby */\n"

# Strings: the bytes live in a MutableString; RSTRING_PTR hands out a native copy that the
# runtime keeps in step with it (Src/Ruby/CApi/CApi.Strings.cs).
edit dst, 'ruby/internal/core/rstring.h',
  "    return RSTRING(str)->len;\n",
  "    return rb_ir_rstring_len(str); /* IronRuby */\n"
edit dst, 'ruby/internal/core/rstring.h',
  "    char *ptr = RB_FL_TEST_RAW(str, RSTRING_NOEMBED) ?\n        RSTRING(str)->as.heap.ptr :\n        RSTRING(str)->as.embed.ary;\n\n    if (RUBY_DEBUG && RB_UNLIKELY(! ptr)) {",
  "    char *ptr = rb_ir_rstring_ptr(str); /* IronRuby */\n\n    if (RUBY_DEBUG && RB_UNLIKELY(! ptr)) {"
edit dst, 'ruby/internal/core/rstring.h',
  "    char *ptr = RB_FL_TEST_RAW(str, RSTRING_NOEMBED) ?\n        RSTRING(str)->as.heap.ptr :\n        RSTRING(str)->as.embed.ary;\n    long len = RSTRING_LEN(str);\n",
  "    char *ptr = rb_ir_rstring_ptr(str); /* IronRuby */\n    long len = RSTRING_LEN(str);\n"

# Arrays: likewise, RARRAY_PTR is a native copy of the elements.
edit dst, 'ruby/internal/core/rarray.h',
  "    VALUE f = RBASIC(ary)->flags;\n    f &= RARRAY_EMBED_LEN_MASK;\n    f >>= RARRAY_EMBED_LEN_SHIFT;\n    return RBIMPL_CAST((long)f);\n",
  "    return rb_ir_rarray_len(ary); /* IronRuby */\n"
edit dst, 'ruby/internal/core/rarray.h',
  "    if (RB_FL_ANY_RAW(a, RARRAY_EMBED_FLAG)) {\n        return RARRAY_EMBED_LEN(a);\n    }\n    else {\n        return RARRAY(a)->as.heap.len;\n    }\n",
  "    return rb_ir_rarray_len(a); /* IronRuby */\n"
edit dst, 'ruby/internal/core/rarray.h',
  "    if (RB_FL_ANY_RAW(a, RARRAY_EMBED_FLAG)) {\n        return FIX_CONST_VALUE_PTR(RARRAY(a)->as.ary);\n    }\n    else {\n        return FIX_CONST_VALUE_PTR(RARRAY(a)->as.heap.ptr);\n    }\n",
  "    return FIX_CONST_VALUE_PTR(rb_ir_rarray_ptr(a)); /* IronRuby */\n"
edit dst, 'ruby/internal/core/rarray.h',
  "    VALUE tmp = RB_OBJ_WB_UNPROTECT_FOR(ARRAY, ary);\n    return RBIMPL_CAST((VALUE *)RARRAY_CONST_PTR(tmp));\n",
  "    return rb_ir_rarray_ptr(ary); /* IronRuby */\n"
edit dst, 'ruby/internal/core/rarray.h',
  "    RARRAY_PTR_USE(ary, ptr,\n        RB_OBJ_WRITE(ary, &ptr[i], v));\n",
  "    rb_ir_rarray_aset(ary, i, v); /* IronRuby */\n"
edit dst, 'ruby/internal/core/rarray.h',
  "#define RARRAY_AREF(a, i) RARRAY_CONST_PTR(a)[i]\n",
  "#define RARRAY_AREF(a, i) rb_ir_rarray_aref(a, i) /* IronRuby */\n"

puts "imported #{src} (#{arch}) into #{dst}"
