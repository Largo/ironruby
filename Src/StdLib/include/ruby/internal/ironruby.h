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
 * lacks it, which is what a CRuby-built extension, reading object memory directly, would.
 * On Windows the loader checks that the DLL imports libironruby-capi instead. */
#if (defined(__GNUC__) || defined(__clang__)) && !defined(_WIN32)
/* C++ gives a namespace-scope const internal linkage, which a weak symbol cannot have
 * ("weak declaration must be public"), even inside an extern "C" block: it needs extern
 * there. C warns about an initialized extern, and needs none. */
#ifdef __cplusplus
extern
#endif
__attribute__((weak, visibility("default"))) const int rb_ir_abi_version = 1;
#endif
/* CRuby's own, declared here too because encoding.h's inline functions now call them */
int rb_enc_get_index(VALUE obj);
void rb_enc_set_index(VALUE obj, int encindex);
#ifdef __cplusplus
}
#endif
#endif /* RBIMPL_IRONRUBY_H */
