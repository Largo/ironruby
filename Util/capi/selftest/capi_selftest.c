/*
 * The C API functions real gems use that ruby/spec's capi suite does not call, built and
 * run by Util/capi/selftest.rb on IronRuby and on CRuby alike.
 */
#include <ruby.h>
#include <ruby/encoding.h>
#include <ruby/ractor.h>
#include <stdlib.h>

/* not in the public headers: extensions declare it themselves, as bigdecimal does */
int rb_opts_exception_p(VALUE opts, int default_value);

static rb_ractor_local_key_t value_key, ptr_key;

static VALUE
opts_exception(int argc, VALUE *argv, VALUE self)
{
    VALUE opts;
    rb_scan_args(argc, argv, ":", &opts);
    return INT2FIX(rb_opts_exception_p(opts, 7));
}

static VALUE
must_asciicompat(VALUE self, VALUE str)
{
    rb_must_asciicompat(str);
    return Qtrue;
}

static VALUE hash_size(VALUE self, VALUE hash) { return SIZET2NUM(RHASH_SIZE(hash)); }

static VALUE
freeze(VALUE self, VALUE obj)
{
    OBJ_FREEZE(obj);
    return obj;
}

static VALUE
memhash(VALUE self, VALUE str)
{
    StringValue(str);
    return ULL2NUM((unsigned long long)rb_memhash(RSTRING_PTR(str), RSTRING_LEN(str)));
}

static VALUE local_value(VALUE self) { return rb_ractor_local_storage_value(value_key); }

static VALUE
local_value_lookup(VALUE self)
{
    VALUE v;
    if (!rb_ractor_local_storage_value_lookup(value_key, &v)) return Qfalse;
    return rb_ary_new_from_args(1, v);
}

static VALUE
local_value_set(VALUE self, VALUE v)
{
    rb_ractor_local_storage_value_set(value_key, v);
    return v;
}

static VALUE
local_ptr(VALUE self)
{
    long *p = rb_ractor_local_storage_ptr(ptr_key);
    return p ? LONG2NUM(*p) : Qnil;
}

static VALUE
local_ptr_set(VALUE self, VALUE n)
{
    long *p = malloc(sizeof(long));
    *p = NUM2LONG(n);
    rb_ractor_local_storage_ptr_set(ptr_key, p);  /* leaked when replaced, as on CRuby */
    return n;
}

static VALUE make_shareable(VALUE self, VALUE obj) { return rb_ractor_make_shareable(obj); }
static VALUE make_shareable_copy(VALUE self, VALUE obj) { return rb_ractor_make_shareable_copy(obj); }
static VALUE shareable_p(VALUE self, VALUE obj) { return rb_ractor_shareable_p(obj) ? Qtrue : Qfalse; }

static VALUE
std_streams(VALUE self)
{
    return rb_ary_new_from_args(3, rb_ractor_stdin(), rb_ractor_stdout(), rb_ractor_stderr());
}

void
Init_capi_selftest(void)
{
    RB_EXT_RACTOR_SAFE(true);
    value_key = rb_ractor_local_storage_value_newkey();
    ptr_key = rb_ractor_local_storage_ptr_newkey(NULL);

    VALUE m = rb_define_module("CApiSelftest");
    rb_define_module_function(m, "opts_exception", opts_exception, -1);
    rb_define_module_function(m, "must_asciicompat", must_asciicompat, 1);
    rb_define_module_function(m, "hash_size", hash_size, 1);
    rb_define_module_function(m, "freeze", freeze, 1);
    rb_define_module_function(m, "memhash", memhash, 1);
    rb_define_module_function(m, "local_value", local_value, 0);
    rb_define_module_function(m, "local_value_lookup", local_value_lookup, 0);
    rb_define_module_function(m, "local_value_set", local_value_set, 1);
    rb_define_module_function(m, "local_ptr", local_ptr, 0);
    rb_define_module_function(m, "local_ptr_set", local_ptr_set, 1);
    rb_define_module_function(m, "make_shareable", make_shareable, 1);
    rb_define_module_function(m, "make_shareable_copy", make_shareable_copy, 1);
    rb_define_module_function(m, "shareable?", shareable_p, 1);
    rb_define_module_function(m, "std_streams", std_streams, 0);

    /* as racc's cparse does: allowed, and silent from C */
    VALUE no_init = rb_define_class_under(m, "NoInit", rb_cObject);
    rb_undef_method(no_init, "initialize");
}
