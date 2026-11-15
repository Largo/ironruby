/*
 * libironruby-capi: the hand-written half of the shim (see capi.h).
 *
 * Here are the trampolines IronRuby calls C through, the unwinding they make possible, the
 * C API's variadic functions (which cannot be [UnmanagedCallersOnly] methods), rb_protect and
 * the other functions that catch a raise, CRuby's printf extension for VALUEs, and the global
 * VALUE variables (rb_cObject...) that IronRuby fills in when it loads the shim.
 */
#include "capi.h"
#include <errno.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

/* ruby.h points snprintf and vsnprintf at ruby_snprintf, which is defined here in terms of them */
#undef snprintf
#undef vsnprintf

RB_IR_THREAD struct rb_ir_frame *rb_ir_top;
RB_IR_THREAD int rb_ir_pending;

RB_IR_EXPORT int *
rb_ir_pending_address(void)
{
    return &rb_ir_pending;
}

void
rb_ir_throw(void)
{
    struct rb_ir_frame *frame = rb_ir_top;
    if (frame == NULL) {
        /* A C function called with no trampoline below it: a thread the extension started
         * itself, calling the C API without a Ruby frame. CRuby would crash here too. */
        fputs("[IronRuby C API] an exception was raised with no Ruby frame to unwind to\n", stderr);
        abort();
    }
    longjmp(frame->buf, 1);
}

/* ------------------------------------------------------------------------------------------
 * Trampolines: IronRuby's only way into C. Each returns *raised = 1 when the C code raised
 * (the exception is waiting on the managed side).
 * ------------------------------------------------------------------------------------------ */

typedef VALUE (*rb_ir_f0)(VALUE);
#define A(i) argv[i]
#define CALL_N(n, ...) return ((VALUE (*)())fn)(self, ##__VA_ARGS__)

static VALUE
rb_ir_dispatch(void *fn, int arity, VALUE self, int argc, const VALUE *argv)
{
    switch (arity) {
      case -2: return ((VALUE (*)(VALUE, VALUE))fn)(self, argv[0]);
      case -1: return ((VALUE (*)(int, const VALUE *, VALUE))fn)(argc, argv, self);
      case 0:  CALL_N(0);
      case 1:  CALL_N(1, A(0));
      case 2:  CALL_N(2, A(0), A(1));
      case 3:  CALL_N(3, A(0), A(1), A(2));
      case 4:  CALL_N(4, A(0), A(1), A(2), A(3));
      case 5:  CALL_N(5, A(0), A(1), A(2), A(3), A(4));
      case 6:  CALL_N(6, A(0), A(1), A(2), A(3), A(4), A(5));
      case 7:  CALL_N(7, A(0), A(1), A(2), A(3), A(4), A(5), A(6));
      case 8:  CALL_N(8, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7));
      case 9:  CALL_N(9, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8));
      case 10: CALL_N(10, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9));
      case 11: CALL_N(11, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10));
      case 12: CALL_N(12, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10), A(11));
      case 13: CALL_N(13, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10), A(11), A(12));
      case 14: CALL_N(14, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10), A(11), A(12), A(13));
      case 15: CALL_N(15, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10), A(11), A(12), A(13), A(14));
    }
    return Qnil;
}

/* A method defined by rb_define_method & co. */
RB_IR_EXPORT VALUE
rb_ir_call_method(void *fn, int arity, VALUE self, int argc, const VALUE *argv, int *raised)
{
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    *raised = 0;
    RB_IR_PROTECT(frame, result = rb_ir_dispatch(fn, arity, self, argc, argv), *raised = 1);
    return result;
}

/* Init_<name>, a dfree function, anything void(void *). */
RB_IR_EXPORT void
rb_ir_call_void(void *fn, void *arg, int *raised)
{
    struct rb_ir_frame frame;
    *raised = 0;
    RB_IR_PROTECT(frame, ((void (*)(void *))fn)(arg), *raised = 1);
}

/* VALUE (*)(VALUE): an allocator, rb_iterate's iterator, rb_protect's body... */
RB_IR_EXPORT VALUE
rb_ir_call_func1(void *fn, VALUE arg, int *raised)
{
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    *raised = 0;
    RB_IR_PROTECT(frame, result = ((rb_ir_f0)fn)(arg), *raised = 1);
    return result;
}

/* A block written in C: rb_block_call_func. */
RB_IR_EXPORT VALUE
rb_ir_call_block(void *fn, VALUE yielded_arg, VALUE callback_arg, int argc, const VALUE *argv, VALUE blockarg, int *raised)
{
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    *raised = 0;
    RB_IR_PROTECT(frame, result = ((rb_block_call_func_t)fn)(yielded_arg, callback_arg, argc, argv, blockarg), *raised = 1);
    return result;
}

/* rb_hash_foreach's and st_foreach's callback: int (*)(VALUE key, VALUE value, VALUE arg). */
RB_IR_EXPORT int
rb_ir_call_foreach(void *fn, VALUE key, VALUE value, VALUE arg, int *raised)
{
    struct rb_ir_frame frame;
    int result = 0;
    *raised = 0;
    RB_IR_PROTECT(frame, result = ((int (*)(VALUE, VALUE, VALUE))fn)(key, value, arg), *raised = 1);
    return result;
}

/* A function the shim does not implement yet (stubs.generated.c). */
void
rb_ir_unimplemented(const char *name)
{
    rb_ir_raise_notimp(name);
    /* rb_ir_raise_notimp's forwarder has unwound; not reached */
    abort();
}

/* ------------------------------------------------------------------------------------------
 * Global VALUEs. IronRuby resolves each path and stores the object's VALUE when it loads the
 * shim; a class IronRuby does not have is left nil.
 * ------------------------------------------------------------------------------------------ */

#define RB_IR_GLOBALS(X) \
    X(rb_mKernel, "Kernel") X(rb_mComparable, "Comparable") X(rb_mEnumerable, "Enumerable") \
    X(rb_mErrno, "Errno") X(rb_mFileTest, "FileTest") X(rb_mGC, "GC") X(rb_mMath, "Math") \
    X(rb_mProcess, "Process") X(rb_mWaitReadable, "IO::WaitReadable") X(rb_mWaitWritable, "IO::WaitWritable") \
    X(rb_cBasicObject, "BasicObject") X(rb_cObject, "Object") X(rb_cArray, "Array") \
    X(rb_cBinding, "Binding") X(rb_cClass, "Class") X(rb_cDir, "Dir") X(rb_cEncoding, "Encoding") \
    X(rb_cEnumerator, "Enumerator") X(rb_cFalseClass, "FalseClass") X(rb_cFile, "File") \
    X(rb_cComplex, "Complex") X(rb_cFloat, "Float") X(rb_cHash, "Hash") X(rb_cIO, "IO") \
    X(rb_cMatch, "MatchData") X(rb_cMethod, "Method") X(rb_cModule, "Module") \
    X(rb_cRefinement, "Refinement") X(rb_cNameErrorMesg, "NameError::message") \
    X(rb_cNilClass, "NilClass") X(rb_cNumeric, "Numeric") X(rb_cProc, "Proc") X(rb_cRandom, "Random") \
    X(rb_cRange, "Range") X(rb_cRational, "Rational") X(rb_cRegexp, "Regexp") X(rb_cStat, "File::Stat") \
    X(rb_cString, "String") X(rb_cStruct, "Struct") X(rb_cSymbol, "Symbol") X(rb_cThread, "Thread") \
    X(rb_cTime, "Time") X(rb_cTrueClass, "TrueClass") X(rb_cUnboundMethod, "UnboundMethod") \
    X(rb_cInteger, "Integer") X(rb_cSet, "Set") X(rb_cRactor, "Ractor") X(rb_cIOBuffer, "IO::Buffer") \
    X(rb_cBox, "Ruby::Box") \
    X(rb_eException, "Exception") X(rb_eStandardError, "StandardError") X(rb_eSystemExit, "SystemExit") \
    X(rb_eInterrupt, "Interrupt") X(rb_eSignal, "SignalException") X(rb_eFatal, "fatal") \
    X(rb_eArgError, "ArgumentError") X(rb_eEOFError, "EOFError") X(rb_eIndexError, "IndexError") \
    X(rb_eStopIteration, "StopIteration") X(rb_eKeyError, "KeyError") X(rb_eRangeError, "RangeError") \
    X(rb_eIOError, "IOError") X(rb_eRuntimeError, "RuntimeError") X(rb_eFrozenError, "FrozenError") \
    X(rb_eSecurityError, "SecurityError") X(rb_eSystemCallError, "SystemCallError") \
    X(rb_eThreadError, "ThreadError") X(rb_eTypeError, "TypeError") X(rb_eZeroDivError, "ZeroDivisionError") \
    X(rb_eNotImpError, "NotImplementedError") X(rb_eNoMemError, "NoMemoryError") \
    X(rb_eNoMethodError, "NoMethodError") X(rb_eFloatDomainError, "FloatDomainError") \
    X(rb_eLocalJumpError, "LocalJumpError") X(rb_eSysStackError, "SystemStackError") \
    X(rb_eRegexpError, "RegexpError") X(rb_eEncodingError, "EncodingError") \
    X(rb_eEncCompatError, "Encoding::CompatibilityError") X(rb_eNoMatchingPatternError, "NoMatchingPatternError") \
    X(rb_eNoMatchingPatternKeyError, "NoMatchingPatternKeyError") X(rb_eScriptError, "ScriptError") \
    X(rb_eNameError, "NameError") X(rb_eSyntaxError, "SyntaxError") X(rb_eLoadError, "LoadError") \
    X(rb_eMathDomainError, "Math::DomainError") X(rb_eIOTimeoutError, "IO::TimeoutError") \
    X(rb_stdin, "$stdin") X(rb_stdout, "$stdout") X(rb_stderr, "$stderr") \
    X(rb_fs, "$;") X(rb_output_fs, "$,") X(rb_rs, "$/") X(rb_default_rs, "\n") X(rb_output_rs, "$\\") \
    X(rb_argv0, "$0")

#define RB_IR_DEFINE_GLOBAL(name, path) RB_IR_EXPORT VALUE name;
RB_IR_GLOBALS(RB_IR_DEFINE_GLOBAL)

struct rb_ir_global { const char *path; VALUE *address; };
#define RB_IR_GLOBAL_ENTRY(name, path) { path, &name },
static const struct rb_ir_global rb_ir_globals[] = { RB_IR_GLOBALS(RB_IR_GLOBAL_ENTRY) { NULL, NULL } };

RB_IR_EXPORT const void *
rb_ir_global_table(int *count)
{
    *count = (int)(sizeof(rb_ir_globals) / sizeof(rb_ir_globals[0])) - 1;
    return rb_ir_globals;
}

/* ------------------------------------------------------------------------------------------
 * Catching: rb_protect, rb_rescue2, rb_ensure. rb_ir_caught moves the pending exception to the
 * thread's errinfo and answers CRuby's tag for it (TAG_RAISE, TAG_THROW...).
 * ------------------------------------------------------------------------------------------ */

VALUE
rb_protect(VALUE (*func)(VALUE), VALUE args, int *state)
{
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    int tag = 0;
    RB_IR_PROTECT(frame, result = func(args), (tag = rb_ir_caught(), result = Qnil));
    if (state) *state = tag;
    return result;
}

VALUE
rb_ensure(VALUE (*b_proc)(VALUE), VALUE data1, VALUE (*e_proc)(VALUE), VALUE data2)
{
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    int tag = 0;
    RB_IR_PROTECT(frame, result = b_proc(data1), tag = rb_ir_caught());
    e_proc(data2);
    if (tag) rb_jump_tag(tag);
    return result;
}

static VALUE
rb_ir_rescue(VALUE (*b_proc)(VALUE), VALUE data1, VALUE (*r_proc)(VALUE, VALUE), VALUE data2, int count, const VALUE *classes)
{
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    int tag = 0;
    RB_IR_PROTECT(frame, result = b_proc(data1), tag = rb_ir_caught());
    if (!tag) return result;
    if (!rb_ir_rescue_match(count, classes)) rb_jump_tag(tag);
    {
        VALUE exc = rb_errinfo();
        result = r_proc ? r_proc(data2, exc) : Qnil;
        rb_set_errinfo(Qnil);
    }
    return result;
}

VALUE
rb_rescue2(VALUE (*b_proc)(VALUE), VALUE data1, VALUE (*r_proc)(VALUE, VALUE), VALUE data2, ...)
{
    VALUE classes[32];
    int count = 0;
    va_list ap;
    va_start(ap, data2);
    for (;;) {
        VALUE c = va_arg(ap, VALUE);
        if (!c || count == 32) break;
        classes[count++] = c;
    }
    va_end(ap);
    return rb_ir_rescue(b_proc, data1, r_proc, data2, count, classes);
}

VALUE
rb_rescue(VALUE (*b_proc)(VALUE), VALUE data1, VALUE (*r_proc)(VALUE, VALUE), VALUE data2)
{
    VALUE standard = rb_eStandardError;
    return rb_ir_rescue(b_proc, data1, r_proc, data2, 1, &standard);
}

/* ------------------------------------------------------------------------------------------
 * printf with CRuby's extension: "%"PRIsVALUE prints a VALUE's to_s, "%+"PRIsVALUE its inspect.
 * PRIsVALUE is "li\v" here: a conversion 'i' with an 'l' and a vertical tab after it.
 * ------------------------------------------------------------------------------------------ */

struct rb_ir_buf {
    char *ptr;
    size_t len, capa;
    int encindex;   /* -1 until a VALUE gives the result an encoding */
};

static void
buf_put(struct rb_ir_buf *b, const char *p, size_t n)
{
    if (b->len + n + 1 > b->capa) {
        size_t capa = b->capa ? b->capa * 2 : 128;
        while (capa < b->len + n + 1) capa *= 2;
        char *q = realloc(b->ptr, capa);
        if (!q) abort();
        b->ptr = q;
        b->capa = capa;
    }
    memcpy(b->ptr + b->len, p, n);
    b->len += n;
    b->ptr[b->len] = '\0';
}

/* The VALUE's string is copied out before anything else can call back into Ruby. */
static void
buf_put_value(struct rb_ir_buf *b, VALUE v, int inspect, int width, int left, int precision)
{
    VALUE s = inspect ? rb_inspect(v) : rb_obj_as_string(v);
    long n = RSTRING_LEN(s);
    if (precision >= 0 && precision < n) {
        /* whole characters: CRuby's precision counts them */
        long len = precision;
        if (rb_str_subpos(s, 0, &len)) n = len;
    }
    if (b->encindex < 0) b->encindex = rb_enc_get_index(s);
    if (!left) for (long i = n; i < width; i++) buf_put(b, " ", 1);
    buf_put(b, RSTRING_PTR(s), (size_t)n);
    if (left) for (long i = n; i < width; i++) buf_put(b, " ", 1);
}

static void
rb_ir_vformat(struct rb_ir_buf *b, const char *fmt, va_list ap)
{
    const char *p = fmt;
    while (*p) {
        const char *pct = strchr(p, '%');
        if (!pct) { buf_put(b, p, strlen(p)); break; }
        buf_put(b, p, (size_t)(pct - p));
        p = pct + 1;
        if (*p == '%') { buf_put(b, "%", 1); p++; continue; }

        char spec[64];
        size_t sl = 0;
        spec[sl++] = '%';
        int plus = 0, left = 0, width = -1, precision = -1;
        while (*p && strchr("-+ #0'", *p)) {
            if (*p == '+') plus = 1;
            if (*p == '-') left = 1;
            if (sl < 40) spec[sl++] = *p;
            p++;
        }
        if (*p == '*') {
            width = va_arg(ap, int);
            sl += (size_t)snprintf(spec + sl, sizeof(spec) - sl - 8, "%d", width);
            p++;
        } else {
            if (*p >= '0' && *p <= '9') width = atoi(p);
            while (*p >= '0' && *p <= '9') { if (sl < 40) spec[sl++] = *p; p++; }
        }
        if (*p == '.') {
            spec[sl++] = *p++;
            if (*p == '*') {
                precision = va_arg(ap, int);
                sl += (size_t)snprintf(spec + sl, sizeof(spec) - sl - 8, "%d", precision);
                p++;
            } else {
                precision = atoi(p);
                while (*p >= '0' && *p <= '9') { if (sl < 48) spec[sl++] = *p; p++; }
            }
        }
        /* length */
        int lng = 0;   /* 0 int, 1 long, 2 long long, 3 size_t, 4 ptrdiff, 5 intmax, -1 short, -2 char, 9 long double */
        for (;;) {
            if (*p == 'l') { lng = lng == 1 ? 2 : 1; spec[sl++] = *p++; }
            else if (*p == 'h') { lng = lng == -1 ? -2 : -1; spec[sl++] = *p++; }
            else if (*p == 'z') { lng = 3; spec[sl++] = *p++; }
            else if (*p == 't') { lng = 4; spec[sl++] = *p++; }
            else if (*p == 'j') { lng = 5; spec[sl++] = *p++; }
            else if (*p == 'q') { lng = 2; spec[sl++] = 'l'; spec[sl++] = 'l'; p++; }
            else if (*p == 'L') { lng = 9; spec[sl++] = *p++; }
            else break;
        }
        char conv = *p;
        if (!conv) break;
        p++;
        spec[sl++] = conv;
        spec[sl] = '\0';

        if (conv == 'i' && *p == '\v') {
            /* PRIsVALUE */
            p++;
            buf_put_value(b, va_arg(ap, VALUE), plus, width, left, precision);
            continue;
        }

        char small[512];
        char *out = small;
        int n = 0;
        switch (conv) {
          case 'd': case 'i':
            switch (lng) {
              case 1: n = snprintf(small, sizeof(small), spec, va_arg(ap, long)); break;
              case 2: n = snprintf(small, sizeof(small), spec, va_arg(ap, long long)); break;
              case 3: n = snprintf(small, sizeof(small), spec, va_arg(ap, ssize_t)); break;
              case 4: n = snprintf(small, sizeof(small), spec, va_arg(ap, ptrdiff_t)); break;
              case 5: n = snprintf(small, sizeof(small), spec, va_arg(ap, intmax_t)); break;
              default: n = snprintf(small, sizeof(small), spec, va_arg(ap, int)); break;
            }
            break;
          case 'u': case 'o': case 'x': case 'X': case 'b': case 'B':
            if (conv == 'b' || conv == 'B') spec[sl - 1] = 'x';   /* not C; rarely used */
            switch (lng) {
              case 1: n = snprintf(small, sizeof(small), spec, va_arg(ap, unsigned long)); break;
              case 2: n = snprintf(small, sizeof(small), spec, va_arg(ap, unsigned long long)); break;
              case 3: n = snprintf(small, sizeof(small), spec, va_arg(ap, size_t)); break;
              case 4: n = snprintf(small, sizeof(small), spec, va_arg(ap, ptrdiff_t)); break;
              case 5: n = snprintf(small, sizeof(small), spec, va_arg(ap, uintmax_t)); break;
              default: n = snprintf(small, sizeof(small), spec, va_arg(ap, unsigned int)); break;
            }
            break;
          case 'c':
            n = snprintf(small, sizeof(small), spec, va_arg(ap, int));
            break;
          case 'p':
            n = snprintf(small, sizeof(small), spec, va_arg(ap, void *));
            break;
          case 'f': case 'F': case 'e': case 'E': case 'g': case 'G': case 'a': case 'A':
            if (lng == 9) n = snprintf(small, sizeof(small), spec, va_arg(ap, long double));
            else n = snprintf(small, sizeof(small), spec, va_arg(ap, double));
            break;
          case 's': {
            const char *s = va_arg(ap, const char *);
            n = snprintf(small, sizeof(small), spec, s);
            if (n >= (int)sizeof(small)) {
                out = malloc((size_t)n + 1);
                snprintf(out, (size_t)n + 1, spec, s);
            }
            break;
          }
          case 'n':
            (void)va_arg(ap, void *);
            n = 0;
            break;
          default:
            /* unknown conversion: print it as written */
            buf_put(b, spec, sl);
            continue;
        }
        if (n > 0) buf_put(b, out, (size_t)n);
        if (out != small) free(out);
    }
}

static VALUE
rb_ir_format_to_string(int encindex, const char *fmt, va_list ap)
{
    struct rb_ir_buf b = { NULL, 0, 0, encindex };
    buf_put(&b, "", 0);
    rb_ir_vformat(&b, fmt, ap);
    VALUE str;
    /* rb_ir_str_new_buffer may raise; the buffer must not leak past it */
    struct rb_ir_frame frame;
    int tag = 0;
    RB_IR_PROTECT(frame, str = rb_ir_str_new_buffer(b.ptr, (long)b.len, b.encindex), tag = rb_ir_caught());
    free(b.ptr);
    if (tag) rb_jump_tag(tag);
    return str;
}

VALUE
rb_vsprintf(const char *fmt, va_list ap)
{
    return rb_ir_format_to_string(-1, fmt, ap);
}

VALUE
rb_sprintf(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE r = rb_vsprintf(fmt, ap);
    va_end(ap);
    return r;
}

VALUE
rb_enc_vsprintf(rb_encoding *enc, const char *fmt, va_list ap)
{
    return rb_ir_format_to_string(enc ? rb_enc_to_index(enc) : -1, fmt, ap);
}

VALUE
rb_enc_sprintf(rb_encoding *enc, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE r = rb_enc_vsprintf(enc, fmt, ap);
    va_end(ap);
    return r;
}

VALUE
rb_str_vcatf(VALUE dst, const char *fmt, va_list ap)
{
    VALUE s = rb_ir_format_to_string(rb_enc_get_index(dst), fmt, ap);
    rb_str_buf_append(dst, s);
    return dst;
}

VALUE
rb_str_catf(VALUE dst, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    rb_str_vcatf(dst, fmt, ap);
    va_end(ap);
    return dst;
}

#undef rb_str_buf_cat
VALUE
rb_str_buf_cat(VALUE str, const char *ptr, long len)
{
    return rb_str_cat(str, ptr, len);
}

int
ruby_vsnprintf(char *str, size_t n, char const *fmt, va_list ap)
{
    struct rb_ir_buf b = { NULL, 0, 0, -1 };
    buf_put(&b, "", 0);
    rb_ir_vformat(&b, fmt, ap);
    if (n > 0) {
        size_t m = b.len < n - 1 ? b.len : n - 1;
        memcpy(str, b.ptr, m);
        str[m] = '\0';
    }
    int len = (int)b.len;
    free(b.ptr);
    return len;
}

int
ruby_snprintf(char *str, size_t n, char const *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int r = ruby_vsnprintf(str, n, fmt, ap);
    va_end(ap);
    return r;
}

/* ------------------------------------------------------------------------------------------
 * Raising.
 * ------------------------------------------------------------------------------------------ */

void
rb_raise(VALUE exc, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE msg = rb_vsprintf(fmt, ap);
    va_end(ap);
    rb_exc_raise(rb_exc_new_str(exc, msg));
}

void
rb_enc_raise(rb_encoding *enc, VALUE exc, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE msg = rb_enc_vsprintf(enc, fmt, ap);
    va_end(ap);
    rb_exc_raise(rb_exc_new_str(exc, msg));
}

void
rb_loaderror(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE msg = rb_vsprintf(fmt, ap);
    va_end(ap);
    rb_exc_raise(rb_exc_new_str(rb_eLoadError, msg));
}

void
rb_name_error(ID name, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE msg = rb_vsprintf(fmt, ap);
    va_end(ap);
    VALUE args[2] = { msg, rb_id2sym(name) };
    rb_exc_raise(rb_class_new_instance(2, args, rb_eNameError));
}

void
rb_frozen_error_raise(VALUE recv, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE msg = rb_vsprintf(fmt, ap);
    va_end(ap);
    VALUE exc = rb_exc_new_str(rb_eFrozenError, msg);
    rb_ivar_set(exc, rb_intern("@receiver"), recv);
    rb_exc_raise(exc);
}

void
rb_fatal(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE msg = rb_vsprintf(fmt, ap);
    va_end(ap);
    rb_exc_raise(rb_exc_new_str(rb_eFatal, msg));
}

void
rb_bug(const char *fmt, ...)
{
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    rb_ir_bug(buf);
    abort();
}

void
rb_warn(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE msg = rb_vsprintf(fmt, ap);
    va_end(ap);
    rb_ir_warn(msg, 0);
}

void
rb_warning(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    VALUE msg = rb_vsprintf(fmt, ap);
    va_end(ap);
    rb_ir_warn(msg, 1);
}

/* ruby.h defines errno as (*rb_errno_ptr()); this is the C library's own. */
int *
rb_errno_ptr(void)
{
#if defined(__APPLE__)
    return __error();
#elif defined(_WIN32)
    return _errno();
#else
    return __errno_location();
#endif
}

/* ------------------------------------------------------------------------------------------
 * The rest of the variadic API.
 * ------------------------------------------------------------------------------------------ */

#define RB_IR_COLLECT(n, argv, last) \
    VALUE argv##_small[16]; \
    VALUE *argv = (n) <= 16 ? argv##_small : malloc(sizeof(VALUE) * (size_t)(n)); \
    { va_list ap; va_start(ap, last); for (long i = 0; i < (n); i++) argv[i] = va_arg(ap, VALUE); va_end(ap); }

/* malloc'd argument lists are freed even when the call raises */
static VALUE
rb_ir_free_after(VALUE (*call)(void *), void *data, VALUE *argv, VALUE *small)
{
    if (argv == small) return call(data);
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    int tag = 0;
    RB_IR_PROTECT(frame, result = call(data), tag = rb_ir_caught());
    free(argv);
    if (tag) rb_jump_tag(tag);
    return result;
}

struct funcall_args { VALUE recv; ID mid; int argc; const VALUE *argv; };
static VALUE do_funcall(void *p) { struct funcall_args *a = p; return rb_funcallv(a->recv, a->mid, a->argc, a->argv); }

VALUE
(rb_funcall)(VALUE recv, ID mid, int n, ...)
{
    RB_IR_COLLECT(n, argv, n);
    struct funcall_args a = { recv, mid, n, argv };
    return rb_ir_free_after(do_funcall, &a, argv, argv_small);
}

struct values_args { long n; const VALUE *argv; };
static VALUE do_ary_new(void *p) { struct values_args *a = p; return rb_ary_new_from_values(a->n, a->argv); }
static VALUE do_yield(void *p) { struct values_args *a = p; return rb_yield_values2((int)a->n, a->argv); }

VALUE
rb_ary_new_from_args(long n, ...)
{
    RB_IR_COLLECT(n, argv, n);
    struct values_args a = { n, argv };
    return rb_ir_free_after(do_ary_new, &a, argv, argv_small);
}

VALUE
(rb_yield_values)(int n, ...)
{
    RB_IR_COLLECT(n, argv, n);
    struct values_args a = { n, argv };
    return rb_ir_free_after(do_yield, &a, argv, argv_small);
}

/* ------------------------------------------------------------------------------------------
 * Encodings. An rb_encoding is Onigmo's OnigEncodingType, a struct extensions read directly
 * (rb_enc_name, rb_enc_mbmaxlen, rb_enc_to_index are macros over its fields) and whose
 * function pointers macros call (rb_enc_mbclen...). IronRuby makes one per encoding it hands
 * out. The character functions are exact for UTF-8 and for single-byte encodings; any other
 * multibyte encoding gets the single-byte ones for now.
 * ------------------------------------------------------------------------------------------ */

static int
utf8_len(const OnigUChar *p, const OnigUChar *e)
{
    int c = *p, n;
    if (c < 0x80) return 1;
    if (c < 0xc2) return ONIGENC_CONSTRUCT_MBCLEN_INVALID();
    n = c < 0xe0 ? 2 : c < 0xf0 ? 3 : c < 0xf5 ? 4 : 0;
    if (n == 0) return ONIGENC_CONSTRUCT_MBCLEN_INVALID();
    if (e - p < n) return ONIGENC_CONSTRUCT_MBCLEN_NEEDMORE(n - (int)(e - p));
    for (int i = 1; i < n; i++) if ((p[i] & 0xc0) != 0x80) return ONIGENC_CONSTRUCT_MBCLEN_INVALID();
    return ONIGENC_CONSTRUCT_MBCLEN_CHARFOUND(n);
}

static int utf8_precise_len(const OnigUChar *p, const OnigUChar *e, const OnigEncodingType *enc) { return utf8_len(p, e); }
static int sb_precise_len(const OnigUChar *p, const OnigUChar *e, const OnigEncodingType *enc) { return ONIGENC_CONSTRUCT_MBCLEN_CHARFOUND(1); }
static int is_newline(const OnigUChar *p, const OnigUChar *end, const OnigEncodingType *enc) { return p < end && *p == '\n'; }

static OnigCodePoint
utf8_to_code(const OnigUChar *p, const OnigUChar *end, const OnigEncodingType *enc)
{
    int len = utf8_len(p, end);
    if (!ONIGENC_MBCLEN_CHARFOUND_P(len)) return *p;
    len = ONIGENC_MBCLEN_CHARFOUND_LEN(len);
    if (len == 1) return *p;
    OnigCodePoint c = *p & (0x7f >> len);
    for (int i = 1; i < len; i++) c = (c << 6) | (p[i] & 0x3f);
    return c;
}
static OnigCodePoint sb_to_code(const OnigUChar *p, const OnigUChar *end, const OnigEncodingType *enc) { return *p; }
static int utf8_code_len(OnigCodePoint c, const OnigEncodingType *enc) { return c < 0x80 ? 1 : c < 0x800 ? 2 : c < 0x10000 ? 3 : c < 0x110000 ? 4 : ONIGERR_TOO_BIG_WIDE_CHAR_VALUE; }
static int sb_code_len(OnigCodePoint c, const OnigEncodingType *enc) { return c < 0x100 ? 1 : ONIGERR_INVALID_CODE_POINT_VALUE; }

static int
utf8_code_to_mbc(OnigCodePoint c, OnigUChar *buf, const OnigEncodingType *enc)
{
    if (c < 0x80) { buf[0] = (OnigUChar)c; return 1; }
    if (c < 0x800) { buf[0] = 0xc0 | (c >> 6); buf[1] = 0x80 | (c & 0x3f); return 2; }
    if (c < 0x10000) { buf[0] = 0xe0 | (c >> 12); buf[1] = 0x80 | ((c >> 6) & 0x3f); buf[2] = 0x80 | (c & 0x3f); return 3; }
    buf[0] = 0xf0 | (c >> 18); buf[1] = 0x80 | ((c >> 12) & 0x3f); buf[2] = 0x80 | ((c >> 6) & 0x3f); buf[3] = 0x80 | (c & 0x3f);
    return 4;
}
static int sb_code_to_mbc(OnigCodePoint c, OnigUChar *buf, const OnigEncodingType *enc) { buf[0] = (OnigUChar)c; return 1; }

static OnigUChar *
utf8_left_adjust(const OnigUChar *start, const OnigUChar *p, const OnigUChar *end, const OnigEncodingType *enc)
{
    while (p > start && (*p & 0xc0) == 0x80) p--;
    return (OnigUChar *)p;
}
static OnigUChar *sb_left_adjust(const OnigUChar *start, const OnigUChar *p, const OnigUChar *end, const OnigEncodingType *enc) { return (OnigUChar *)p; }
static int always_true(const OnigUChar *p, const OnigUChar *end, const OnigEncodingType *enc) { return 1; }

RB_IR_EXPORT rb_encoding *
rb_ir_make_encoding(const char *name, int min_len, int max_len, int index, int utf8)
{
    OnigEncodingType *enc = calloc(1, sizeof(OnigEncodingType));
    if (!enc) abort();
    enc->name = strdup(name);
    enc->min_enc_len = min_len;
    enc->max_enc_len = max_len;
    enc->ruby_encoding_index = index;
    enc->flags = utf8 ? ONIGENC_FLAG_UNICODE : 0;
    enc->precise_mbc_enc_len = utf8 ? utf8_precise_len : sb_precise_len;
    enc->is_mbc_newline = is_newline;
    enc->mbc_to_code = utf8 ? utf8_to_code : sb_to_code;
    enc->code_to_mbclen = utf8 ? utf8_code_len : sb_code_len;
    enc->code_to_mbc = utf8 ? utf8_code_to_mbc : sb_code_to_mbc;
    enc->left_adjust_char_head = utf8 ? utf8_left_adjust : sb_left_adjust;
    enc->is_allowed_reverse_match = always_true;
    return enc;
}
