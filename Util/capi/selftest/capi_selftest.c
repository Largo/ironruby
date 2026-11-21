/*
 * The C API functions real gems use that ruby/spec's capi suite does not call, built and
 * run by Util/capi/selftest.rb on IronRuby and on CRuby alike.
 */
#include <ruby.h>
#include <ruby/encoding.h>
#include <ruby/ractor.h>
#include <ruby/thread.h>
#include <stdlib.h>
#include <time.h>

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

/* how many threads are inside at once: never more than one for code under the GVL (CRuby) or
 * its extension's lock (IronRuby, for code the extension has not declared thread-safe) */
static volatile int inside, overlapped;

static VALUE
exclusive(VALUE self, VALUE n)
{
    long i, m = NUM2LONG(n);
    volatile unsigned long x = 0;
    if (__atomic_add_fetch(&inside, 1, __ATOMIC_SEQ_CST) > 1) overlapped = 1;
    for (i = 0; i < m; i++) x += i;
    __atomic_sub_fetch(&inside, 1, __ATOMIC_SEQ_CST);
    return overlapped ? Qtrue : Qfalse;
}

static VALUE yield_nil(VALUE self) { return rb_yield(Qnil); }

/* the same count, in code that runs with the GVL (CRuby) or the extension's lock (IronRuby) again
 * after a nested rb_thread_call_with_gvl has returned: the inner call must not let go of it */
static volatile int nested_inside, nested_overlapped;
static void *noop(void *data) { return data; }

static void *
nested_exclusive_with_gvl(void *data)
{
    long i, m = *(long *)data;
    volatile unsigned long x = 0;
    rb_thread_call_with_gvl(noop, NULL);
    if (__atomic_add_fetch(&nested_inside, 1, __ATOMIC_SEQ_CST) > 1) nested_overlapped = 1;
    for (i = 0; i < m; i++) x += i;
    __atomic_sub_fetch(&nested_inside, 1, __ATOMIC_SEQ_CST);
    return NULL;
}

static void *
nested_exclusive_without_gvl(void *data)
{
    return rb_thread_call_with_gvl(nested_exclusive_with_gvl, data);
}

static VALUE
nested_exclusive(VALUE self, VALUE n)
{
    long m = NUM2LONG(n);
    rb_thread_call_without_gvl(nested_exclusive_without_gvl, &m, RUBY_UBF_IO, NULL);
    return nested_overlapped ? Qtrue : Qfalse;
}

/* a blocking region that runs another one inside an rb_thread_call_with_gvl, then waits for
 * another thread to call feed: the outer region must have let go of the GVL or lock again */
static volatile int fed;
static VALUE feed(VALUE self) { fed = 1; return Qnil; }

static void *
region_in_with_gvl(void *data)
{
    rb_thread_call_without_gvl(noop, NULL, RUBY_UBF_IO, NULL);
    return NULL;
}

static void *
wait_for_feed(void *data)
{
    long waited, limit = *(long *)data;
    struct timespec ms = { 0, 1000000 };
    rb_thread_call_with_gvl(region_in_with_gvl, NULL);
    for (waited = 0; !fed && waited < limit; waited++) nanosleep(&ms, NULL);
    return (void *)(fed ? 1 : 0);
}

static VALUE
wait_in_nested_region(VALUE self, VALUE ms)
{
    long limit = NUM2LONG(ms);
    fed = 0;
    return rb_thread_call_without_gvl(wait_for_feed, &limit, RUBY_UBF_IO, NULL) ? Qtrue : Qfalse;
}

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

    /* defined after taking back RB_EXT_RACTOR_SAFE: not thread-safe, so one thread at a time */
    RB_EXT_RACTOR_SAFE(false);
    VALUE unsafe = rb_define_module_under(m, "Unsafe");
    rb_define_module_function(unsafe, "exclusive", exclusive, 1);
    rb_define_module_function(unsafe, "yield_nil", yield_nil, 0);
    rb_define_module_function(unsafe, "nested_exclusive", nested_exclusive, 1);
    rb_define_module_function(unsafe, "wait_in_nested_region", wait_in_nested_region, 1);
    rb_define_module_function(unsafe, "feed", feed, 0);

    /* as racc's cparse does: allowed, and silent from C */
    VALUE no_init = rb_define_class_under(m, "NoInit", rb_cObject);
    rb_undef_method(no_init, "initialize");
}
