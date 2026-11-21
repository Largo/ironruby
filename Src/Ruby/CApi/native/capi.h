/*
 * libironruby-capi: the native half of IronRuby's Ruby C API. Internal to the shim.
 *
 * Every rb_* function an extension calls is exported from here. Most forward through a table
 * of function pointers that IronRuby fills at startup with [UnmanagedCallersOnly] methods
 * (forwarders.generated.c, from Src/Ruby/CApi/functions.txt); the rest - variadic functions,
 * rb_protect and friends, anything that has to unwind - are C, in capi.c.
 *
 * Unwinding. A Ruby exception cannot travel as a CLR exception through C frames (the CLR does
 * not unwind native frames on Unix), so it travels as CRuby's does: IronRuby calls into C only
 * through a trampoline that setjmps (rb_ir_call_*), and when a managed function raises it
 * records the exception, sets rb_ir_pending and returns; the forwarder sees the flag and
 * longjmps to the innermost trampoline, which returns to IronRuby, which rethrows. No managed
 * frame is ever skipped by a longjmp: the managed callee has already returned when it happens.
 */
#ifndef IRONRUBY_CAPI_H
#define IRONRUBY_CAPI_H

#include "ruby.h"
#include "ruby/encoding.h"
#include "ruby/debug.h"
#include "ruby/io.h"
#include <setjmp.h>
#include <string.h>

#if defined(_WIN32)
# define RB_IR_EXPORT __declspec(dllexport)
#else
# define RB_IR_EXPORT __attribute__((visibility("default")))
#endif
#if defined(_MSC_VER)
# define RB_IR_THREAD __declspec(thread)
#else
# define RB_IR_THREAD __thread
#endif

/* On Windows a C library longjmp unwinds the stack the SEH way, which needs every frame it
 * crosses to cooperate; GCC's builtins only restore registers, as CRuby's RUBY_SETJMP does
 * with MinGW. */
#if defined(_WIN32)
typedef void *rb_ir_jmp_buf[5];
# define RB_IR_SETJMP(buf) __builtin_setjmp(buf)
# define RB_IR_LONGJMP(buf) __builtin_longjmp(buf, 1)
#else
typedef jmp_buf rb_ir_jmp_buf;
# define RB_IR_SETJMP(buf) setjmp(buf)
# define RB_IR_LONGJMP(buf) longjmp(buf, 1)
#endif

struct rb_ir_frame {
    rb_ir_jmp_buf buf;
    struct rb_ir_frame *prev;
};

extern RB_IR_THREAD struct rb_ir_frame *rb_ir_top;
extern RB_IR_THREAD int rb_ir_pending;

RBIMPL_ATTR_NORETURN() void rb_ir_throw(void);

#define RB_IR_CHECK() do { if (RB_UNLIKELY(rb_ir_pending)) rb_ir_throw(); } while (0)

/* Runs STMT with a jump target of its own: on a raise, ON_RAISE runs (with the exception still
 * pending on the managed side) instead of unwinding further. */
#define RB_IR_PROTECT(frame, STMT, ON_RAISE) do {   \
    (frame).prev = rb_ir_top;                         \
    rb_ir_top = &(frame);                             \
    if (RB_IR_SETJMP((frame).buf) == 0) {             \
        STMT;                                         \
        rb_ir_top = (frame).prev;                     \
    } else {                                          \
        rb_ir_top = (frame).prev;                     \
        rb_ir_pending = 0;                            \
        ON_RAISE;                                     \
    }                                                 \
} while (0)

/* Managed functions of the shim's own, in functions.txt with the rest. */
int rb_ir_caught(void);
int rb_ir_rescue_match(int count, const VALUE *classes);
VALUE rb_ir_current_exception(void);
int rb_ir_define_dummy_encoding(const char *name);
int rb_ir_native_fd(int fd);
void rb_ir_set_current_exception(VALUE exc);
void rb_ir_warn(VALUE message, int verbose_only);
VALUE rb_ir_str_new_buffer(const char *ptr, long len, int encindex);
void rb_ir_raise_notimp(const char *name);
void rb_ir_bug(const char *message);
VALUE rb_ir_data_new(VALUE klass, int typed);
VALUE rb_ir_allocate_builtin(VALUE klass);
int rb_ir_blocking_begin(void *ubf, void *data2, void *thread);
void rb_ir_blocking_end(int token);
void rb_ir_with_gvl_begin(void);
void rb_ir_with_gvl_end(void);
void rb_ir_ext_thread_safe(int flag);

#endif
