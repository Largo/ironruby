/*
 * libironruby-capi: the hand-written half of the shim (see capi.h).
 *
 * Here are the trampolines IronRuby calls C through, the unwinding they make possible, the
 * C API's variadic functions (which cannot be [UnmanagedCallersOnly] methods), rb_protect and
 * the other functions that catch a raise, CRuby's printf extension for VALUEs, and the global
 * VALUE variables (rb_cObject...) that IronRuby fills in when it loads the shim.
 */
#include "capi.h"
#include "ruby/thread.h"
#include "ruby/util.h"
#include <errno.h>
#include <signal.h>
#if defined(_WIN32)
# include <windows.h>
#else
# include <pthread.h>
#endif
#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>

/* ruby.h points snprintf and vsnprintf at ruby_snprintf, which is defined here in terms of them */
#undef snprintf
#undef vsnprintf
#undef strtod
#undef strdup  /* ruby/util.h: ruby_strdup, defined below */

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
    RB_IR_LONGJMP(frame->buf);
}

/* ------------------------------------------------------------------------------------------
 * Trampolines: IronRuby's only way into C. Each returns *raised = 1 when the C code raised
 * (the exception is waiting on the managed side).
 * ------------------------------------------------------------------------------------------ */

typedef VALUE (*rb_ir_f0)(VALUE);
#define A(i) argv[i]

/* Each arity's own function type: in C23 (GCC 16's default) VALUE (*)() takes no arguments. */
static VALUE
rb_ir_dispatch(void *fn, int arity, VALUE self, int argc, const VALUE *argv)
{
    switch (arity) {
      case -2: return ((VALUE (*)(VALUE, VALUE))fn)(self, argv[0]);
      case -1: return ((VALUE (*)(int, const VALUE *, VALUE))fn)(argc, argv, self);
      case 0: return ((VALUE (*)(VALUE))fn)(self);
      case 1: return ((VALUE (*)(VALUE, VALUE))fn)(self, A(0));
      case 2: return ((VALUE (*)(VALUE, VALUE, VALUE))fn)(self, A(0), A(1));
      case 3: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2));
      case 4: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3));
      case 5: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4));
      case 6: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5));
      case 7: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5), A(6));
      case 8: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7));
      case 9: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8));
      case 10: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9));
      case 11: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10));
      case 12: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10), A(11));
      case 13: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10), A(11), A(12));
      case 14: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10), A(11), A(12), A(13));
      case 15: return ((VALUE (*)(VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE, VALUE))fn)(self, A(0), A(1), A(2), A(3), A(4), A(5), A(6), A(7), A(8), A(9), A(10), A(11), A(12), A(13), A(14));
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

/* rb_tracepoint_new's hook: void (*)(VALUE tpval, void *data). */
RB_IR_EXPORT void
rb_ir_call_tracepoint(void *fn, VALUE tpval, void *data, int *raised)
{
    struct rb_ir_frame frame;
    *raised = 0;
    RB_IR_PROTECT(frame, ((void (*)(VALUE, void *))fn)(tpval, data), *raised = 1);
}

/* rb_debug_inspector_open's callback: rb_debug_inspector_func_t. */
RB_IR_EXPORT VALUE
rb_ir_call_inspector(void *fn, const rb_debug_inspector_t *dc, void *data, int *raised)
{
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    *raised = 0;
    RB_IR_PROTECT(frame, result = ((rb_debug_inspector_func_t)fn)(dc, data), *raised = 1);
    return result;
}

/* What a typed object's dsize function says it holds outside its slot (ObjectSpace.memsize_of). */
RB_IR_EXPORT size_t
rb_ir_typed_data_memsize(VALUE obj, int *raised)
{
    struct rb_ir_frame frame;
    size_t result = 0;
    *raised = 0;
    const rb_data_type_t *type = RTYPEDDATA_TYPE(obj);
    void *data = RTYPEDDATA_GET_DATA(obj);
    if (type == NULL || type->function.dsize == NULL || data == NULL) return 0;
    RB_IR_PROTECT(frame, result = type->function.dsize(data), *raised = 1);
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

/* Whether this thread is one Ruby code runs on: IronRuby has entered C on it. */
int
ruby_native_thread_p(void)
{
    return rb_ir_top != NULL;
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
    VALUE result = Qnil, errinfo = rb_errinfo(), current = rb_ir_current_exception();
    int tag = 0;
    RB_IR_PROTECT(frame, result = b_proc(data1), tag = rb_ir_caught());
    /* $! is the exception while the ensure function runs, and what it was before afterwards,
       whether the ensure function returns or raises */
    {
        int tag2 = 0;
        RB_IR_PROTECT(frame, e_proc(data2), tag2 = rb_ir_caught());
        rb_set_errinfo(errinfo);
        rb_ir_set_current_exception(current);
        if (tag2) rb_jump_tag(tag2);
    }
    if (tag) rb_jump_tag(tag);
    return result;
}

static VALUE
rb_ir_rescue(VALUE (*b_proc)(VALUE), VALUE data1, VALUE (*r_proc)(VALUE, VALUE), VALUE data2, int count, const VALUE *classes)
{
    struct rb_ir_frame frame;
    VALUE result = Qnil, errinfo = rb_errinfo(), current = rb_ir_current_exception();
    int tag = 0;
    RB_IR_PROTECT(frame, result = b_proc(data1), tag = rb_ir_caught());
    if (!tag) return result;
    if (!rb_ir_rescue_match(count, classes)) rb_jump_tag(tag);
    /* $! is the exception while the rescue function runs, and what it was before afterwards,
       whether the rescue function returns or raises */
    {
        int tag2 = 0;
        RB_IR_PROTECT(frame, result = r_proc ? r_proc(data2, rb_errinfo()) : Qnil, tag2 = rb_ir_caught());
        rb_set_errinfo(errinfo);
        rb_ir_set_current_exception(current);
        if (tag2) rb_jump_tag(tag2);
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
            else if (p[0] == 'I' && p[1] == '6' && p[2] == '4') { lng = 2; spec[sl++] = 'l'; spec[sl++] = 'l'; p += 3; }
            else if (p[0] == 'I' && p[1] == '3' && p[2] == '2') { lng = 0; p += 3; }
            else if (*p == 'I') { lng = 3; spec[sl++] = 'z'; p++; }
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

/* errno is the C library's, so these read it here rather than in IronRuby
 * (Marshal.GetLastSystemError is GetLastError on Windows). */
void
rb_sys_fail(const char *msg)
{
    rb_syserr_fail(*rb_errno_ptr(), msg);
}

void
rb_sys_fail_str(VALUE msg)
{
    rb_syserr_fail_str(*rb_errno_ptr(), msg);
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

/* UTF-16 and UTF-32, either byte order: be is 1 for big-endian */
static OnigCodePoint u16_at(const OnigUChar *p, int be) { return be ? (p[0] << 8) | p[1] : (p[1] << 8) | p[0]; }
static OnigCodePoint
u32_at(const OnigUChar *p, int be)
{
    return be ? ((OnigCodePoint)p[0] << 24) | (p[1] << 16) | (p[2] << 8) | p[3]
              : ((OnigCodePoint)p[3] << 24) | (p[2] << 16) | (p[1] << 8) | p[0];
}

static int
utf16_len(const OnigUChar *p, const OnigUChar *e, int be)
{
    if (e - p < 2) return ONIGENC_CONSTRUCT_MBCLEN_NEEDMORE(2 - (int)(e - p));
    OnigCodePoint c = u16_at(p, be);
    if (c >= 0xdc00 && c <= 0xdfff) return ONIGENC_CONSTRUCT_MBCLEN_INVALID();
    if (c < 0xd800 || c > 0xdbff) return ONIGENC_CONSTRUCT_MBCLEN_CHARFOUND(2);
    if (e - p < 4) return ONIGENC_CONSTRUCT_MBCLEN_NEEDMORE(4 - (int)(e - p));
    c = u16_at(p + 2, be);
    return c >= 0xdc00 && c <= 0xdfff ? ONIGENC_CONSTRUCT_MBCLEN_CHARFOUND(4) : ONIGENC_CONSTRUCT_MBCLEN_INVALID();
}

static int
utf32_len(const OnigUChar *p, const OnigUChar *e, int be)
{
    if (e - p < 4) return ONIGENC_CONSTRUCT_MBCLEN_NEEDMORE(4 - (int)(e - p));
    OnigCodePoint c = u32_at(p, be);
    return c <= 0x10ffff && !(c >= 0xd800 && c <= 0xdfff) ? ONIGENC_CONSTRUCT_MBCLEN_CHARFOUND(4) : ONIGENC_CONSTRUCT_MBCLEN_INVALID();
}

static OnigCodePoint
utf16_to_code(const OnigUChar *p, const OnigUChar *e, int be)
{
    if (e - p < 2) return *p;
    OnigCodePoint c = u16_at(p, be);
    if (c >= 0xd800 && c <= 0xdbff && e - p >= 4) {
        OnigCodePoint lo = u16_at(p + 2, be);
        if (lo >= 0xdc00 && lo <= 0xdfff) return 0x10000 + ((c - 0xd800) << 10) + (lo - 0xdc00);
    }
    return c;
}

static int
utf16_code_to_mbc(OnigCodePoint c, OnigUChar *buf, int be)
{
    if (c > 0xffff) {
        OnigCodePoint hi = 0xd800 + ((c - 0x10000) >> 10), lo = 0xdc00 + ((c - 0x10000) & 0x3ff);
        utf16_code_to_mbc(hi, buf, be);
        utf16_code_to_mbc(lo, buf + 2, be);
        return 4;
    }
    buf[be ? 0 : 1] = (OnigUChar)(c >> 8);
    buf[be ? 1 : 0] = (OnigUChar)c;
    return 2;
}

static int
utf32_code_to_mbc(OnigCodePoint c, OnigUChar *buf, int be)
{
    for (int i = 0; i < 4; i++) buf[be ? 3 - i : i] = (OnigUChar)(c >> (8 * i));
    return 4;
}

static OnigUChar *
utf16_left_adjust(const OnigUChar *start, const OnigUChar *p, int be)
{
    if (p <= start) return (OnigUChar *)p;
    p -= (p - start) % 2;
    if (p - start >= 2) {
        OnigCodePoint c = u16_at(p, be), prev = u16_at(p - 2, be);
        if (c >= 0xdc00 && c <= 0xdfff && prev >= 0xd800 && prev <= 0xdbff) p -= 2;
    }
    return (OnigUChar *)p;
}

#define RB_IR_WIDE(name, be) \
    static int name##_precise_len(const OnigUChar *p, const OnigUChar *e, const OnigEncodingType *enc) { return name##_len_(p, e, be); } \
    static int name##_newline(const OnigUChar *p, const OnigUChar *e, const OnigEncodingType *enc) { return name##_len_(p, e, be) > 0 && name##_to_code_(p, e, be) == '\n'; } \
    static OnigCodePoint name##_code(const OnigUChar *p, const OnigUChar *e, const OnigEncodingType *enc) { return name##_to_code_(p, e, be); } \
    static int name##_to_mbc(OnigCodePoint c, OnigUChar *buf, const OnigEncodingType *enc) { return name##_code_to_mbc_(c, buf, be); } \
    static OnigUChar *name##_adjust(const OnigUChar *start, const OnigUChar *p, const OnigUChar *e, const OnigEncodingType *enc) { return name##_left_adjust_(start, p, be); }

static int utf16_len_(const OnigUChar *p, const OnigUChar *e, int be) { return utf16_len(p, e, be); }
static OnigCodePoint utf16_to_code_(const OnigUChar *p, const OnigUChar *e, int be) { return utf16_to_code(p, e, be); }
static int utf16_code_to_mbc_(OnigCodePoint c, OnigUChar *buf, int be) { return utf16_code_to_mbc(c, buf, be); }
static OnigUChar *utf16_left_adjust_(const OnigUChar *start, const OnigUChar *p, int be) { return utf16_left_adjust(start, p, be); }
static int utf32_len_(const OnigUChar *p, const OnigUChar *e, int be) { return utf32_len(p, e, be); }
static OnigCodePoint utf32_to_code_(const OnigUChar *p, const OnigUChar *e, int be) { return e - p < 4 ? *p : u32_at(p, be); }
static int utf32_code_to_mbc_(OnigCodePoint c, OnigUChar *buf, int be) { return utf32_code_to_mbc(c, buf, be); }
static OnigUChar *utf32_left_adjust_(const OnigUChar *start, const OnigUChar *p, int be) { return (OnigUChar *)(p - (p - start) % 4); }

#define utf16be_len_ utf16_len_
#define utf16be_to_code_ utf16_to_code_
#define utf16be_code_to_mbc_ utf16_code_to_mbc_
#define utf16be_left_adjust_ utf16_left_adjust_
#define utf16le_len_ utf16_len_
#define utf16le_to_code_ utf16_to_code_
#define utf16le_code_to_mbc_ utf16_code_to_mbc_
#define utf16le_left_adjust_ utf16_left_adjust_
#define utf32be_len_ utf32_len_
#define utf32be_to_code_ utf32_to_code_
#define utf32be_code_to_mbc_ utf32_code_to_mbc_
#define utf32be_left_adjust_ utf32_left_adjust_
#define utf32le_len_ utf32_len_
#define utf32le_to_code_ utf32_to_code_
#define utf32le_code_to_mbc_ utf32_code_to_mbc_
#define utf32le_left_adjust_ utf32_left_adjust_
RB_IR_WIDE(utf16be, 1)
RB_IR_WIDE(utf16le, 0)
RB_IR_WIDE(utf32be, 1)
RB_IR_WIDE(utf32le, 0)

static int utf16_code_len(OnigCodePoint c, const OnigEncodingType *enc) { return c > 0x10ffff ? ONIGERR_TOO_BIG_WIDE_CHAR_VALUE : c > 0xffff ? 4 : 2; }
static int utf32_code_len(OnigCodePoint c, const OnigEncodingType *enc) { return c > 0x10ffff ? ONIGERR_TOO_BIG_WIDE_CHAR_VALUE : 4; }

/* the encodings whose bytes 0xA0..0xFF are Latin-1's letters (rb_ir_make_encoding's kind 6) */
#define RB_IR_LATIN1 (1U << 30)

/* Character classes: exact for ASCII; a non-ASCII code point is a letter (and so alnum, word,
 * graph, print) in a Unicode encoding and nothing in any other, which is close enough for what
 * extensions ask (rb_enc_isalnum, rb_enc_isspace...) until IronRuby answers these itself. */
static int
is_code_ctype(OnigCodePoint code, OnigCtype ctype, const OnigEncodingType *enc)
{
    if (code < 128) {
        int c = (int)code;
        switch (ctype) {
          case ONIGENC_CTYPE_NEWLINE: return c == '\n';
          case ONIGENC_CTYPE_ALPHA: return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
          case ONIGENC_CTYPE_BLANK: return c == ' ' || c == '\t';
          case ONIGENC_CTYPE_CNTRL: return c < 0x20 || c == 0x7f;
          case ONIGENC_CTYPE_DIGIT: return c >= '0' && c <= '9';
          case ONIGENC_CTYPE_GRAPH: return c > 0x20 && c < 0x7f;
          case ONIGENC_CTYPE_LOWER: return c >= 'a' && c <= 'z';
          case ONIGENC_CTYPE_PRINT: return c >= 0x20 && c < 0x7f;
          case ONIGENC_CTYPE_PUNCT: return c > 0x20 && c < 0x7f && !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'));
          case ONIGENC_CTYPE_SPACE: return c == ' ' || (c >= '\t' && c <= '\r');
          case ONIGENC_CTYPE_UPPER: return c >= 'A' && c <= 'Z';
          case ONIGENC_CTYPE_XDIGIT: return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
          case ONIGENC_CTYPE_WORD: return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
          case ONIGENC_CTYPE_ALNUM: return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
          case ONIGENC_CTYPE_ASCII: return 1;
        }
        return 0;
    }
    if (code < 256 && (enc->flags & (ONIGENC_FLAG_UNICODE | RB_IR_LATIN1))) {
        int c = (int)code;
        int upper = c >= 0xc0 && c <= 0xde && c != 0xd7;
        int lower = (c >= 0xdf && c <= 0xff && c != 0xf7) || c == 0xaa || c == 0xb5 || c == 0xba;
        switch (ctype) {
          case ONIGENC_CTYPE_ALPHA: case ONIGENC_CTYPE_ALNUM: case ONIGENC_CTYPE_WORD: return upper || lower;
          case ONIGENC_CTYPE_UPPER: return upper;
          case ONIGENC_CTYPE_LOWER: return lower;
          case ONIGENC_CTYPE_SPACE: return c == 0x85 || c == 0xa0;
          case ONIGENC_CTYPE_BLANK: return c == 0xa0;
          case ONIGENC_CTYPE_CNTRL: return c < 0xa0;
          case ONIGENC_CTYPE_PRINT: return c >= 0xa0;
          case ONIGENC_CTYPE_GRAPH: return c > 0xa0;
          case ONIGENC_CTYPE_PUNCT: return c > 0xa0 && !upper && !lower;
        }
        return 0;
    }
    if (!(enc->flags & ONIGENC_FLAG_UNICODE)) return 0;
    switch (ctype) {
      case ONIGENC_CTYPE_ALPHA: case ONIGENC_CTYPE_GRAPH: case ONIGENC_CTYPE_PRINT:
      case ONIGENC_CTYPE_WORD: case ONIGENC_CTYPE_ALNUM:
        return 1;
    }
    return 0;
}

/* lower case for ASCII, and for Latin-1's letters in a Unicode or Latin-1 encoding */
static OnigCodePoint
fold_code(OnigCodePoint c, const OnigEncodingType *enc)
{
    if (c >= 'A' && c <= 'Z') return c + 32;
    if (c >= 0xc0 && c <= 0xde && c != 0xd7 && (enc->flags & (ONIGENC_FLAG_UNICODE | RB_IR_LATIN1))) return c + 32;
    return c;
}

static OnigCodePoint
upcase_code(OnigCodePoint c, const OnigEncodingType *enc)
{
    if (c >= 'a' && c <= 'z') return c - 32;
    if (c >= 0xe0 && c <= 0xfe && c != 0xf7 && (enc->flags & (ONIGENC_FLAG_UNICODE | RB_IR_LATIN1))) return c - 32;
    return c;
}

static int
mbc_case_fold(OnigCaseFoldType flag, const OnigUChar **pp, const OnigUChar *end, OnigUChar *to, const OnigEncodingType *enc)
{
    const OnigUChar *p = *pp;
    int r = enc->precise_mbc_enc_len(p, end, enc);
    if (!ONIGENC_MBCLEN_CHARFOUND_P(r)) {
        *to = *p;
        (*pp)++;
        return 1;
    }
    *pp += ONIGENC_MBCLEN_CHARFOUND_LEN(r);
    return enc->code_to_mbc(fold_code(enc->mbc_to_code(p, end, enc), enc), to, enc);
}

static int apply_all_case_fold(OnigCaseFoldType flag, OnigApplyAllCaseFoldFunc f, void *arg, const OnigEncodingType *enc) { return 0; }
static int get_case_fold_codes_by_str(OnigCaseFoldType flag, const OnigUChar *p, const OnigUChar *end, OnigCaseFoldCodeItem acs[], const OnigEncodingType *enc) { return 0; }
static int property_name_to_ctype(const OnigEncodingType *enc, const OnigUChar *p, const OnigUChar *end) { return ONIGERR_INVALID_CHAR_PROPERTY_NAME; }
static int get_ctype_code_range(OnigCtype ctype, OnigCodePoint *sb_out, const OnigCodePoint *ranges[], const OnigEncodingType *enc) { return ONIG_NO_SUPPORT_CONFIG; }

static int
case_map(OnigCaseFoldType *flagP, const OnigUChar **pp, const OnigUChar *end, OnigUChar *to, OnigUChar *to_end, const OnigEncodingType *enc)
{
    OnigUChar *start = to;
    while (*pp < end && to_end - to >= enc->max_enc_len) {
        const OnigUChar *p = *pp;
        int r = enc->precise_mbc_enc_len(p, end, enc);
        if (!ONIGENC_MBCLEN_CHARFOUND_P(r)) {
            *to++ = *(*pp)++;
            continue;
        }
        *pp += ONIGENC_MBCLEN_CHARFOUND_LEN(r);
        OnigCodePoint c = enc->mbc_to_code(p, end, enc), m = c;
        if (*flagP & ONIGENC_CASE_UPCASE) m = upcase_code(c, enc);
        else if (*flagP & ONIGENC_CASE_DOWNCASE) m = fold_code(c, enc);
        if (m != c) *flagP |= ONIGENC_CASE_MODIFIED;
        to += enc->code_to_mbc(m, to, enc);
    }
    return (int)(to - start);
}

/*
 * kind: 0 single-byte (or a multibyte encoding treated byte by byte), 1 UTF-8 and its variants,
 * 2 UTF-16BE, 3 UTF-16LE, 4 UTF-32BE, 5 UTF-32LE, 6 single-byte with Latin-1's letters.
 */
RB_IR_EXPORT rb_encoding *
rb_ir_make_encoding(const char *name, int min_len, int max_len, int index, int kind)
{
    int utf8 = kind == 1;
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
    switch (kind) {
#define RB_IR_SET_WIDE(n, len) \
        enc->precise_mbc_enc_len = n##_precise_len; enc->is_mbc_newline = n##_newline; enc->mbc_to_code = n##_code; \
        enc->code_to_mbclen = len; enc->code_to_mbc = n##_to_mbc; enc->left_adjust_char_head = n##_adjust; break;
      case 2: RB_IR_SET_WIDE(utf16be, utf16_code_len)
      case 3: RB_IR_SET_WIDE(utf16le, utf16_code_len)
      case 4: RB_IR_SET_WIDE(utf32be, utf32_code_len)
      case 5: RB_IR_SET_WIDE(utf32le, utf32_code_len)
#undef RB_IR_SET_WIDE
    }
    if (kind >= 1 && kind <= 5) enc->flags = ONIGENC_FLAG_UNICODE;
    if (kind == 6) enc->flags = RB_IR_LATIN1;
    enc->is_code_ctype = is_code_ctype;
    enc->mbc_case_fold = mbc_case_fold;
    enc->apply_all_case_fold = apply_all_case_fold;
    enc->get_case_fold_codes_by_str = get_case_fold_codes_by_str;
    enc->property_name_to_ctype = property_name_to_ctype;
    enc->get_ctype_code_range = get_ctype_code_range;
    enc->case_map = case_map;
    return enc;
}

/* ------------------------------------------------------------------------------------------
 * rb_scan_args, for a format that is not a compile-time constant (the headers inline the
 * constant case). CRuby's class.c: "[lead][opt][*][trail][:][&]". IronRuby does not tell C
 * whether keywords were passed yet, so a trailing Hash is taken as the keywords when the
 * format has ':' and there are more arguments than the mandatory ones.
 * ------------------------------------------------------------------------------------------ */

static int
rb_ir_scan_args(int argc, const VALUE *argv, const char *fmt, va_list ap)
{
    const char *p = fmt;
    int n_lead = 0, n_opt = 0, n_trail = 0, f_var = 0, f_hash = 0, f_block = 0;
    if (*p >= '0' && *p <= '9') {
        n_lead = *p++ - '0';
        if (*p >= '0' && *p <= '9') n_opt = *p++ - '0';
    }
    if (*p == '*') { f_var = 1; p++; }
    if (*p >= '0' && *p <= '9') n_trail = *p++ - '0';
    if (*p == ':') { f_hash = 1; p++; }
    if (*p == '&') { f_block = 1; p++; }
    if (*p != '\0') rb_fatal("bad scan arg format: %s", fmt);

    int n_mand = n_lead + n_trail;
    VALUE hash = Qnil;
    if (f_hash && argc > n_mand && RB_TYPE_P(argv[argc - 1], T_HASH)) {
        hash = rb_hash_dup(argv[argc - 1]);
        argc--;
    }
    if (argc < n_mand || (!f_var && argc > n_mand + n_opt)) {
        rb_error_arity(argc, n_mand, f_var ? -1 : n_mand + n_opt);
    }

    int i = 0;
    for (int k = 0; k < n_lead; k++) {
        VALUE *var = va_arg(ap, VALUE *);
        if (var) *var = argv[i];
        i++;
    }
    for (int k = 0; k < n_opt; k++) {
        VALUE *var = va_arg(ap, VALUE *);
        if (i < argc - n_trail) {
            if (var) *var = argv[i];
            i++;
        } else if (var) {
            *var = Qnil;
        }
    }
    if (f_var) {
        int n = argc - i - n_trail;
        VALUE *var = va_arg(ap, VALUE *);
        if (var) *var = n > 0 ? rb_ary_new_from_values(n, argv + i) : rb_ary_new();
        if (n > 0) i += n;
    }
    for (int k = 0; k < n_trail; k++) {
        VALUE *var = va_arg(ap, VALUE *);
        if (var) *var = argv[i];
        i++;
    }
    if (f_hash) {
        VALUE *var = va_arg(ap, VALUE *);
        if (var) *var = hash;
    }
    if (f_block) {
        VALUE *var = va_arg(ap, VALUE *);
        if (var) *var = rb_block_given_p() ? rb_block_proc() : Qnil;
    }
    return argc;
}

int
(rb_scan_args)(int argc, const VALUE *argv, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int r = rb_ir_scan_args(argc, argv, fmt, ap);
    va_end(ap);
    return r;
}

int
(rb_scan_args_kw)(int kw_flag, int argc, const VALUE *argv, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    int r = rb_ir_scan_args(argc, argv, fmt, ap);
    va_end(ap);
    return r;
}

/* ------------------------------------------------------------------------------------------
 * More variadic and C-only functions.
 * ------------------------------------------------------------------------------------------ */

static VALUE do_eval_string(VALUE str) { return rb_eval_string((const char *)str); }

VALUE
rb_eval_string_protect(const char *str, int *state)
{
    return rb_protect(do_eval_string, (VALUE)str, state);
}

/* error.c's rb_warning_category_enabled_p, asking Warning[] */
static int
ir_warning_category_enabled(rb_warning_category_t category)
{
    const char *name;
    switch (category) {
      case RB_WARN_CATEGORY_DEPRECATED: name = "deprecated"; break;
      case RB_WARN_CATEGORY_EXPERIMENTAL: name = "experimental"; break;
      case RB_WARN_CATEGORY_PERFORMANCE: name = "performance"; break;
      case RB_WARN_CATEGORY_STRICT_UNUSED_BLOCK: name = "strict_unused_block"; break;
      default: return 1;
    }
    return RTEST(rb_funcall(rb_path2class("Warning"), rb_intern("[]"), 1, ID2SYM(rb_intern(name))));
}

void
rb_category_warn(rb_warning_category_t category, const char *fmt, ...)
{
    va_list ap;
    if (!ir_warning_category_enabled(category)) return;
    va_start(ap, fmt);
    VALUE msg = rb_vsprintf(fmt, ap);
    va_end(ap);
    rb_ir_warn(msg, 0);
}

void
rb_category_warning(rb_warning_category_t category, const char *fmt, ...)
{
    va_list ap;
    if (!ir_warning_category_enabled(category)) return;
    va_start(ap, fmt);
    VALUE msg = rb_vsprintf(fmt, ap);
    va_end(ap);
    rb_ir_warn(msg, 1);
}

VALUE
rb_io_printf(int argc, const VALUE *argv, VALUE out)
{
    rb_io_write(out, rb_f_sprintf(argc, argv));
    return Qnil;
}

VALUE
rb_struct_define(const char *name, ...)
{
    VALUE args = rb_ary_new();
    if (name) rb_ary_push(args, rb_str_new_cstr(name));
    va_list ap;
    va_start(ap, name);
    for (;;) {
        const char *member = va_arg(ap, const char *);
        if (!member) break;
        rb_ary_push(args, rb_id2sym(rb_intern(member)));
    }
    va_end(ap);
    return rb_funcallv(rb_cStruct, rb_intern("new"), (int)RARRAY_LEN(args), RARRAY_CONST_PTR(args));
}

/* struct.c's: a Data class with the NULL-terminated member names, under Data or super */
VALUE
rb_data_define(VALUE super, ...)
{
    VALUE args = rb_ary_new();
    va_list ap;
    va_start(ap, super);
    for (;;) {
        const char *member = va_arg(ap, const char *);
        if (!member) break;
        rb_ary_push(args, rb_id2sym(rb_intern(member)));
    }
    va_end(ap);
    if (!super || NIL_P(super)) {
        super = rb_path2class("Data");
    }
    else {
        Check_Type(super, T_CLASS);
    }
    return rb_funcallv(super, rb_intern("define"), (int)RARRAY_LEN(args), RARRAY_CONST_PTR(args));
}

VALUE
rb_struct_define_under(VALUE outer, const char *name, ...)
{
    VALUE args = rb_ary_new();
    va_list ap;
    va_start(ap, name);
    for (;;) {
        const char *member = va_arg(ap, const char *);
        if (!member) break;
        rb_ary_push(args, rb_id2sym(rb_intern(member)));
    }
    va_end(ap);
    VALUE st = rb_funcallv(rb_cStruct, rb_intern("new"), (int)RARRAY_LEN(args), RARRAY_CONST_PTR(args));
    rb_const_set(outer, rb_intern(name), st);
    return st;
}

VALUE
rb_struct_new(VALUE klass, ...)
{
    long n = RARRAY_LEN(rb_struct_s_members(klass));
    VALUE args = rb_ary_new();
    va_list ap;
    va_start(ap, klass);
    for (long i = 0; i < n; i++) rb_ary_push(args, va_arg(ap, VALUE));
    va_end(ap);
    return rb_class_new_instance((int)n, RARRAY_CONST_PTR(args), klass);
}

double
ruby_strtod(const char *str, char **endptr)
{
    return strtod(str, endptr);
}

char *
ruby_strdup(const char *str)
{
    size_t n = strlen(str) + 1;
    char *copy = malloc(n);
    if (!copy) rb_memerror();
    memcpy(copy, str, n);
    return copy;
}

unsigned long
ruby_scan_oct(const char *start, size_t len, size_t *retlen)
{
    const char *p = start;
    unsigned long v = 0;
    while (len-- > 0 && *p >= '0' && *p <= '7') v = (v << 3) | (unsigned long)(*p++ - '0');
    *retlen = (size_t)(p - start);
    return v;
}

unsigned long
ruby_scan_hex(const char *start, size_t len, size_t *retlen)
{
    const char *p = start;
    unsigned long v = 0;
    for (; len-- > 0; p++) {
        int d = (*p >= '0' && *p <= '9') ? *p - '0' : (*p >= 'a' && *p <= 'f') ? *p - 'a' + 10 : (*p >= 'A' && *p <= 'F') ? *p - 'A' + 10 : -1;
        if (d < 0) break;
        v = (v << 4) | (unsigned long)d;
    }
    *retlen = (size_t)(p - start);
    return v;
}

/* ------------------------------------------------------------------------------------------
 * Digest plugins: an extension that brings its own hash function hands digest.so an
 * rb_digest_metadata_t, wrapped by rb_digest_wrap_metadata, which ruby/digest.h finds through
 * rb_ext_resolve_symbol("digest.so", ...). IronRuby's digest is C#: the wrapper is here, and
 * Digest::Base calls the plugin's functions through rb_ir_digest_call (NativeDigest.cs).
 */
typedef struct {
    int api_version;
    size_t digest_len;
    size_t block_len;
    size_t ctx_size;
    int (*init_func)(void *);
    void (*update_func)(void *, unsigned char *, size_t);
    int (*finish_func)(void *, unsigned char *);
} rb_ir_digest_metadata_t;   /* ruby/digest.h's rb_digest_metadata_t */

static const rb_data_type_t rb_ir_digest_metadata_type = {
    "digest/metadata",
    { 0, 0, 0, },
    0, 0,
    RUBY_TYPED_FREE_IMMEDIATELY,
};

static VALUE
rb_ir_digest_wrap_metadata(const rb_ir_digest_metadata_t *meta)
{
    return rb_obj_freeze(TypedData_Wrap_Struct(0, &rb_ir_digest_metadata_type, (void *)meta));
}

/* The metadata a Digest::Base subclass's "metadata" holds, or NULL for any other typed object. */
RB_IR_EXPORT const rb_ir_digest_metadata_t *
rb_ir_digest_metadata(VALUE obj)
{
    return RTYPEDDATA_TYPE(obj) == &rb_ir_digest_metadata_type ? RTYPEDDATA_GET_DATA(obj) : NULL;
}

/* op 0: init_func(ctx); 1: update_func(ctx, ptr, len); 2: finish_func(ctx, ptr) */
RB_IR_EXPORT int
rb_ir_digest_call(const rb_ir_digest_metadata_t *meta, int op, void *ctx, unsigned char *ptr, size_t len, int *raised)
{
    struct rb_ir_frame frame;
    int result = 0;
    *raised = 0;
    switch (op) {
      case 0: RB_IR_PROTECT(frame, result = meta->init_func(ctx), *raised = 1); break;
      case 1: RB_IR_PROTECT(frame, meta->update_func(ctx, ptr, len), *raised = 1); break;
      case 2: RB_IR_PROTECT(frame, result = meta->finish_func(ctx, ptr), *raised = 1); break;
    }
    return result;
}

/* Only digest.so's rb_digest_wrap_metadata, which is all CRuby's own extensions look up with it. */
void *
rb_ext_resolve_symbol(const char *feature, const char *symbol)
{
    if ((strcmp(feature, "digest.so") == 0 || strcmp(feature, "digest") == 0) &&
        strcmp(symbol, "rb_digest_wrap_metadata") == 0) {
        rb_require("digest");
        return (void *)(uintptr_t)rb_ir_digest_wrap_metadata;
    }
    return NULL;
}

int
rb_enc_precise_mbclen(const char *p, const char *e, rb_encoding *enc)
{
    if (p >= e) return ONIGENC_CONSTRUCT_MBCLEN_NEEDMORE(1);
    return enc->precise_mbc_enc_len((const OnigUChar *)p, (const OnigUChar *)e, enc);
}

int
rb_enc_mbclen(const char *p, const char *e, rb_encoding *enc)
{
    int r = rb_enc_precise_mbclen(p, e, enc);
    if (ONIGENC_MBCLEN_CHARFOUND_P(r)) return ONIGENC_MBCLEN_CHARFOUND_LEN(r);
    int min = enc->min_enc_len;
    return min <= e - p ? min : (int)(e - p);
}

unsigned int
rb_enc_codepoint_len(const char *p, const char *e, int *len_p, rb_encoding *enc)
{
    if (e <= p) rb_raise(rb_eArgError, "empty string");
    int r = rb_enc_precise_mbclen(p, e, enc);
    if (!ONIGENC_MBCLEN_CHARFOUND_P(r)) rb_raise(rb_eArgError, "invalid byte sequence in %s", enc->name);
    if (len_p) *len_p = ONIGENC_MBCLEN_CHARFOUND_LEN(r);
    return enc->mbc_to_code((const OnigUChar *)p, (const OnigUChar *)e, enc);
}

char *
rb_enc_nth(const char *p, const char *e, long nth, rb_encoding *enc)
{
    while (p < e && nth-- > 0) p += rb_enc_mbclen(p, e, enc);
    return (char *)(p > e ? e : p);
}

/* CRuby's: the original UTF-8 of up to 6 bytes, beyond U+10FFFF too */
int
rb_uv_to_utf8(char buf[6], unsigned long uv)
{
    if (uv < 0x200000) return utf8_code_to_mbc((OnigCodePoint)uv, (OnigUChar *)buf, NULL);
    if (uv < 0x4000000) {
        buf[0] = (char)(0xf8 | (uv >> 24));
        buf[1] = (char)(0x80 | ((uv >> 18) & 0x3f));
        buf[2] = (char)(0x80 | ((uv >> 12) & 0x3f));
        buf[3] = (char)(0x80 | ((uv >> 6) & 0x3f));
        buf[4] = (char)(0x80 | (uv & 0x3f));
        return 5;
    }
    if (uv < 0x80000000UL) {
        buf[0] = (char)(0xfc | (uv >> 30));
        buf[1] = (char)(0x80 | ((uv >> 24) & 0x3f));
        buf[2] = (char)(0x80 | ((uv >> 18) & 0x3f));
        buf[3] = (char)(0x80 | ((uv >> 12) & 0x3f));
        buf[4] = (char)(0x80 | ((uv >> 6) & 0x3f));
        buf[5] = (char)(0x80 | (uv & 0x3f));
        return 6;
    }
    rb_raise(rb_eRangeError, "pack(U): value out of range");
    return 0;
}

/* string.c's: a fixed-width encoding counts whole and partial characters, any other walks them */
long
rb_enc_strlen(const char *p, const char *e, rb_encoding *enc)
{
    long c;
    if (enc->min_enc_len == enc->max_enc_len) {
        return (e - p + enc->min_enc_len - 1) / enc->min_enc_len;
    }
    for (c = 0; p < e; c++) p += rb_enc_mbclen(p, e, enc);
    return c;
}

int
rb_enc_codelen(int c, rb_encoding *enc)
{
    int n = enc->code_to_mbclen((OnigCodePoint)c, enc);
    if (n <= 0) rb_raise(rb_eArgError, "invalid codepoint 0x%x in %s", (unsigned int)c, enc->name);
    return n;
}

/*
 * vm_method.c's: the function a method stands for when the platform lacks it. ruby.h names it
 * through the pointer rb_f_notimplement_; IronRuby defines a method of it as one respond_to?
 * denies (CApi.DefineMethod).
 */
void
rb_notimplement(void)
{
    rb_raise(rb_eNotImpError, "%"PRIsVALUE"() function is unimplemented on this machine",
             rb_id2str(rb_frame_this_func()));
}

#undef rb_f_notimplement
VALUE
rb_f_notimplement(int argc, const VALUE *argv, VALUE obj, VALUE marker)
{
    rb_notimplement();
    UNREACHABLE_RETURN(Qnil);
}
VALUE (*const rb_f_notimplement_)(int, const VALUE *, VALUE, VALUE marker) = rb_f_notimplement;

/* encoding.c's: a new dummy encoding, as many as fit in its 256 */
int
rb_define_dummy_encoding(const char *name)
{
    int index = rb_ir_define_dummy_encoding(name);
    if (index < 0) rb_raise(rb_eEncodingError, "too many encoding (> 256)");
    return index;
}

OnigUChar *
onigenc_get_left_adjust_char_head(OnigEncoding enc, const OnigUChar *start, const OnigUChar *s, const OnigUChar *end)
{
    if (s <= start) return (OnigUChar *)s;
    return enc->left_adjust_char_head(start, s, end, enc);
}

/* No GVL: IronRuby's threads run in parallel already, so these just call func. What they do
 * keep is the rest of the contract: the thread is "sleep" meanwhile, and Thread#wakeup, #raise,
 * #kill or a signal for the main thread runs ubf(data2) to get func to return. RUBY_UBF_IO
 * interrupts the blocking system call with SIGVTALRM, whose handler is installed without
 * SA_RESTART so that the call fails with EINTR. */
#if defined(_WIN32)
/* Windows: the blocked thread's synchronous I/O is cancelled instead (ERROR_OPERATION_ABORTED). */
RB_IR_EXPORT void
rb_ir_interrupt_thread(void *thread)
{
    HANDLE h = OpenThread(THREAD_TERMINATE, FALSE, (DWORD)(uintptr_t)thread);
    if (h) {
        CancelSynchronousIo(h);
        CloseHandle(h);
    }
}

static void *
rb_ir_current_thread(void)
{
    return (void *)(uintptr_t)GetCurrentThreadId();
}
#else
static void rb_ir_sigvtalrm(int sig) { (void)sig; }
static pthread_once_t rb_ir_sigvtalrm_once = PTHREAD_ONCE_INIT;

static void
rb_ir_install_sigvtalrm(void)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = rb_ir_sigvtalrm;
    sigemptyset(&sa.sa_mask);
    sigaction(SIGVTALRM, &sa, NULL);
}

RB_IR_EXPORT void
rb_ir_interrupt_thread(void *thread)
{
    pthread_kill((pthread_t)thread, SIGVTALRM);
}

static void *
rb_ir_current_thread(void)
{
    pthread_once(&rb_ir_sigvtalrm_once, rb_ir_install_sigvtalrm);
    return (void *)pthread_self();
}
#endif

static void *
rb_ir_blocking_region(void *(*func)(void *), void *data1, rb_unblock_function_t *ubf, void *data2)
{
    int token = rb_ir_blocking_begin((void *)ubf, data2, rb_ir_current_thread());
    void *result = func(data1);
    rb_ir_blocking_end(token);
    return result;
}

void *
rb_thread_call_without_gvl(void *(*func)(void *), void *data1, rb_unblock_function_t *ubf, void *data2)
{
    return rb_ir_blocking_region(func, data1, ubf, data2);
}

void *
rb_thread_call_without_gvl2(void *(*func)(void *), void *data1, rb_unblock_function_t *ubf, void *data2)
{
    return rb_ir_blocking_region(func, data1, ubf, data2);
}

void *
rb_thread_call_with_gvl(void *(*func)(void *), void *data1)
{
    return func(data1);
}

void *
rb_nogvl(void *(*func)(void *), void *data1, rb_unblock_function_t *ubf, void *data2, int flags)
{
    return rb_ir_blocking_region(func, data1, ubf, data2);
}

int
ruby_thread_has_gvl_p(void)
{
    return rb_ir_top != NULL;
}

/* rb_exec_recursive: func(obj, arg, recursive?), recursive when obj is already being processed
 * on this thread. */
#define RB_IR_RECURSION_MAX 256
static RB_IR_THREAD VALUE rb_ir_recursion[RB_IR_RECURSION_MAX];
static RB_IR_THREAD int rb_ir_recursion_depth;

static VALUE
rb_ir_exec_recursive(VALUE (*func)(VALUE, VALUE, int), VALUE obj, VALUE arg)
{
    for (int i = 0; i < rb_ir_recursion_depth; i++) {
        if (rb_ir_recursion[i] == obj) return func(obj, arg, 1);
    }
    if (rb_ir_recursion_depth == RB_IR_RECURSION_MAX) return func(obj, arg, 0);
    int depth = rb_ir_recursion_depth;
    rb_ir_recursion[rb_ir_recursion_depth++] = obj;
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    int tag = 0;
    RB_IR_PROTECT(frame, result = func(obj, arg, 0), tag = rb_ir_caught());
    rb_ir_recursion_depth = depth;
    if (tag) rb_jump_tag(tag);
    return result;
}

VALUE
rb_exec_recursive(VALUE (*func)(VALUE, VALUE, int), VALUE obj, VALUE arg)
{
    return rb_ir_exec_recursive(func, obj, arg);
}

VALUE
rb_exec_recursive_paired(VALUE (*func)(VALUE, VALUE, int), VALUE obj, VALUE paired_obj, VALUE arg)
{
    return rb_ir_exec_recursive(func, obj, arg);
}

/* ------------------------------------------------------------------------------------------
 * Global variables defined from C (rb_define_hooked_variable & co): CRuby's stock getters and
 * setters, which an extension passes by name, and the trampolines IronRuby calls them through.
 * ------------------------------------------------------------------------------------------ */

VALUE rb_gvar_undef_getter(ID id, VALUE *data) { return Qnil; }
VALUE rb_gvar_val_getter(ID id, VALUE *data) { return (VALUE)data; }
VALUE rb_gvar_var_getter(ID id, VALUE *data) { return data ? *data : Qnil; }
void rb_gvar_undef_setter(VALUE val, ID id, VALUE *data) { }
void rb_gvar_val_setter(VALUE val, ID id, VALUE *data) { }
void rb_gvar_var_setter(VALUE val, ID id, VALUE *data) { *data = val; }

void
rb_gvar_readonly_setter(VALUE val, ID id, VALUE *data)
{
    rb_name_error(id, "%"PRIsVALUE" is a read-only variable", rb_id2str(id));
}

RB_IR_EXPORT VALUE
rb_ir_call_gvar_get(void *fn, ID id, VALUE *data, int *raised)
{
    struct rb_ir_frame frame;
    VALUE result = Qnil;
    *raised = 0;
    RB_IR_PROTECT(frame, result = ((rb_gvar_getter_t *)fn)(id, data), *raised = 1);
    return result;
}

RB_IR_EXPORT void
rb_ir_call_gvar_set(void *fn, VALUE val, ID id, VALUE *data, int *raised)
{
    struct rb_ir_frame frame;
    *raised = 0;
    RB_IR_PROTECT(frame, ((rb_gvar_setter_t *)fn)(val, id, data), *raised = 1);
}

/* ------------------------------------------------------------------------------------------
 * T_DATA. IronRuby makes the object (a DataObject) and its handle's slot is laid out as RData /
 * RTypedData, so the fields are written here and read by the header macros directly. The data
 * is never embedded: RTYPEDDATA's type word never has TYPED_DATA_EMBEDDED set.
 * ------------------------------------------------------------------------------------------ */

#undef rb_data_object_wrap
#undef rb_data_object_zalloc
#undef rb_data_typed_object_wrap
#undef rb_data_typed_object_zalloc

void *
ruby_xmalloc(size_t size)
{
    void *p = malloc(size ? size : 1);
    if (!p) rb_memerror();
    return p;
}

void *
ruby_xmalloc2(size_t nelems, size_t elemsiz)
{
    if (elemsiz && nelems > (size_t)-1 / elemsiz) rb_raise(rb_eArgError, "malloc: possible integer overflow");
    return ruby_xmalloc(nelems * elemsiz);
}

void *
ruby_xcalloc(size_t nelems, size_t elemsiz)
{
    void *p = calloc(nelems ? nelems : 1, elemsiz ? elemsiz : 1);
    if (!p) rb_memerror();
    return p;
}

void *
ruby_xrealloc(void *ptr, size_t newsiz)
{
    void *p = realloc(ptr, newsiz ? newsiz : 1);
    if (!p) rb_memerror();
    return p;
}

void *
ruby_xrealloc2(void *ptr, size_t newelems, size_t newsiz)
{
    if (newsiz && newelems > (size_t)-1 / newsiz) rb_raise(rb_eArgError, "realloc: possible integer overflow");
    return ruby_xrealloc(ptr, newelems * newsiz);
}

void
ruby_xfree(void *ptr)
{
    free(ptr);
}

void
rb_gc_adjust_memory_usage(ssize_t diff)
{
}

/* ALLOCV: a temporary buffer whose lifetime CRuby ties to a hidden object; it is freed by
 * rb_free_tmp_buffer here (and leaked if an exception skips that, as handles are). */
void *
rb_alloc_tmp_buffer(volatile VALUE *store, long len)
{
    if (len < 0) rb_raise(rb_eArgError, "negative buffer size (or size too big)");
    void *p = ruby_xmalloc((size_t)len);
    *store = (VALUE)p | 1;   /* not a VALUE anyone reads: odd, so never mistaken for a handle */
    return p;
}

void *
rb_alloc_tmp_buffer_with_count(volatile VALUE *store, size_t len, size_t count)
{
    return rb_alloc_tmp_buffer(store, (long)len);
}

void
rb_free_tmp_buffer(volatile VALUE *store)
{
    if (*store & 1) free((void *)(*store & ~(VALUE)1));
    *store = 0;
}

VALUE
rb_data_object_wrap(VALUE klass, void *datap, RUBY_DATA_FUNC dmark, RUBY_DATA_FUNC dfree)
{
    VALUE obj = rb_ir_data_new(klass, 0);
    RDATA(obj)->dmark = dmark;
    RDATA(obj)->dfree = dfree;
    RDATA(obj)->data = datap;
    return obj;
}

VALUE
rb_data_object_zalloc(VALUE klass, size_t size, RUBY_DATA_FUNC dmark, RUBY_DATA_FUNC dfree)
{
    return rb_data_object_wrap(klass, ruby_xcalloc(1, size), dmark, dfree);
}

VALUE
rb_data_typed_object_wrap(VALUE klass, void *datap, const rb_data_type_t *type)
{
    VALUE obj = rb_ir_data_new(klass, 1);
    *(VALUE *)&RTYPEDDATA(obj)->type = (VALUE)type;
    RTYPEDDATA(obj)->data = datap;
    return obj;
}

VALUE
rb_data_typed_object_zalloc(VALUE klass, size_t size, const rb_data_type_t *type)
{
    return rb_data_typed_object_wrap(klass, ruby_xcalloc(1, size), type);
}

int
rb_typeddata_inherited_p(const rb_data_type_t *child, const rb_data_type_t *parent)
{
    for (; child; child = child->parent) {
        if (child == parent) return 1;
    }
    return 0;
}

int
rb_typeddata_is_kind_of(VALUE obj, const rb_data_type_t *data_type)
{
    if (!RB_TYPE_P(obj, T_DATA) || !RTYPEDDATA_P(obj)) return 0;
    return rb_typeddata_inherited_p(RTYPEDDATA_TYPE(obj), data_type);
}

void *
rb_check_typeddata(VALUE obj, const rb_data_type_t *data_type)
{
    if (!RB_TYPE_P(obj, T_DATA) || !RTYPEDDATA_P(obj)) {
        rb_raise(rb_eTypeError, "wrong argument type %s (expected %s)",
                 RB_SPECIAL_CONST_P(obj) ? rb_obj_classname(obj) : rb_obj_classname(obj), data_type->wrap_struct_name);
    }
    if (!rb_typeddata_inherited_p(RTYPEDDATA_TYPE(obj), data_type)) {
        rb_raise(rb_eTypeError, "wrong argument type %s (expected %s)",
                 RTYPEDDATA_TYPE(obj)->wrap_struct_name, data_type->wrap_struct_name);
    }
    return RTYPEDDATA_GET_DATA(obj);
}

/* What rb_get_alloc_func answers for a class no extension gave an allocator. */
RB_IR_EXPORT VALUE
rb_ir_default_allocator(VALUE klass)
{
    return rb_ir_allocate_builtin(klass);
}

/*
 * struct rb_io's layout as these headers have it, for IronRuby's rb_io_t mirrors
 * (CApi.Io.cs): its size, then the offsets of self, fd, mode and pathv.
 */
/* the fields are deprecated for extensions, which should call rb_io_descriptor() and so on */
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wdeprecated-declarations"
#endif
RB_IR_EXPORT void
rb_ir_io_layout(size_t *out)
{
    out[0] = sizeof(struct rb_io);
    out[1] = offsetof(struct rb_io, self);
    out[2] = offsetof(struct rb_io, fd);
    out[3] = offsetof(struct rb_io, mode);
    out[4] = offsetof(struct rb_io, pathv);
}
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif

/*
 * io.c's: after a call on io failed with error, an interrupted one is retried at once (the
 * events come back), one that would have blocked waits for them (rb_io_wait), and any other
 * error leaves nothing ready (Qnil). Here rather than in C# for the platform's errno values.
 */
VALUE
rb_io_maybe_wait(int error, VALUE io, VALUE events, VALUE timeout)
{
    rb_io_t *fptr;
    RB_IO_POINTER(io, fptr);
    switch (error) {
#if defined(ERESTART)
      case ERESTART:
#endif
      case EINTR:
        rb_thread_check_ints();
        return events;
#if defined(EWOULDBLOCK) && EWOULDBLOCK != EAGAIN
      case EWOULDBLOCK:
#endif
      case EAGAIN:
        return rb_io_wait(io, events, timeout);
      default:
        return Qnil;
    }
}

int
rb_io_maybe_wait_readable(int error, VALUE io, VALUE timeout)
{
    VALUE result = rb_io_maybe_wait(error, io, RB_INT2NUM(RUBY_IO_READABLE), timeout);
    if (RTEST(result)) return RB_NUM2INT(result);
    if (result == Qfalse) rb_raise(rb_eIOTimeoutError, "Timed out waiting for IO to become readable!");
    return 0;
}

int
rb_io_maybe_wait_writable(int error, VALUE io, VALUE timeout)
{
    VALUE result = rb_io_maybe_wait(error, io, RB_INT2NUM(RUBY_IO_WRITABLE), timeout);
    if (RTEST(result)) return RB_NUM2INT(result);
    if (result == Qfalse) rb_raise(rb_eIOTimeoutError, "Timed out waiting for IO to become writable!");
    return 0;
}

#if !defined(_WIN32)
#include <fcntl.h>
#include <sys/param.h>
#include <sys/select.h>
#include <sys/time.h>
#include <unistd.h>

/* thread.c's rb_fd_* (the large-fdset flavour the headers declare): plain C over fd_set. */

void
rb_fd_init(rb_fdset_t *fds)
{
    fds->maxfd = 0;
    fds->fdset = ALLOC(fd_set);
    FD_ZERO(fds->fdset);
}

void
rb_fd_term(rb_fdset_t *fds)
{
    xfree(fds->fdset);
    fds->maxfd = 0;
    fds->fdset = 0;
}

void
rb_fd_zero(rb_fdset_t *fds)
{
    if (fds->fdset)
        MEMZERO(fds->fdset, fd_mask, howmany(fds->maxfd, NFDBITS));
}

static void
ir_fd_resize(int n, rb_fdset_t *fds)
{
    size_t m = howmany(n + 1, NFDBITS) * sizeof(fd_mask);
    size_t o = howmany(fds->maxfd, NFDBITS) * sizeof(fd_mask);

    if (m < sizeof(fd_set)) m = sizeof(fd_set);
    if (o < sizeof(fd_set)) o = sizeof(fd_set);

    if (m > o) {
        fds->fdset = xrealloc(fds->fdset, m);
        memset((char *)fds->fdset + o, 0, m - o);
    }
    if (n >= fds->maxfd) fds->maxfd = n + 1;
}

void
rb_fd_set(int n, rb_fdset_t *fds)
{
    ir_fd_resize(n, fds);
    FD_SET(n, fds->fdset);
}

void
rb_fd_clr(int n, rb_fdset_t *fds)
{
    if (n >= fds->maxfd) return;
    FD_CLR(n, fds->fdset);
}

int
rb_fd_isset(int n, const rb_fdset_t *fds)
{
    if (n >= fds->maxfd) return 0;
    return FD_ISSET(n, fds->fdset) != 0;
}

void
rb_fd_copy(rb_fdset_t *dst, const fd_set *src, int max)
{
    size_t size = howmany(max, NFDBITS) * sizeof(fd_mask);

    if (size < sizeof(fd_set)) size = sizeof(fd_set);
    dst->maxfd = max;
    dst->fdset = xrealloc(dst->fdset, size);
    memcpy(dst->fdset, src, size);
}

void
rb_fd_dup(rb_fdset_t *dst, const rb_fdset_t *src)
{
    size_t size = howmany(rb_fd_max(src), NFDBITS) * sizeof(fd_mask);

    if (size < sizeof(fd_set)) size = sizeof(fd_set);
    dst->maxfd = src->maxfd;
    dst->fdset = xrealloc(dst->fdset, size);
    memcpy(dst->fdset, src->fdset, size);
}

int
rb_fd_select(int n, rb_fdset_t *readfds, rb_fdset_t *writefds, rb_fdset_t *exceptfds, struct timeval *timeout)
{
    fd_set *r = NULL, *w = NULL, *e = NULL;
    if (readfds) {
        ir_fd_resize(n - 1, readfds);
        r = rb_fd_ptr(readfds);
    }
    if (writefds) {
        ir_fd_resize(n - 1, writefds);
        w = rb_fd_ptr(writefds);
    }
    if (exceptfds) {
        ir_fd_resize(n - 1, exceptfds);
        e = rb_fd_ptr(exceptfds);
    }
    return select(n, r, w, e, timeout);
}

/*
 * select(2), waiting at most 100ms at a time and checking for interrupts in between, so that
 * Thread#raise and Thread#kill reach a thread waiting here, as CRuby's do. The sets are
 * restored before each retry; a timeout is the total.
 */
int
rb_thread_fd_select(int max, rb_fdset_t *read, rb_fdset_t *write, rb_fdset_t *except, struct timeval *timeout)
{
    rb_fdset_t orig[3];
    rb_fdset_t *sets[3] = { read, write, except };
    struct timeval end, now;
    int i, result;

    if (!read && !write && !except) {
        if (!timeout) {
            rb_thread_sleep_forever();
            return 0;
        }
        rb_thread_wait_for(*timeout);
        return 0;
    }
    for (i = 0; i < 3; i++) {
        orig[i].maxfd = 0;
        orig[i].fdset = NULL;
        if (sets[i]) {
            ir_fd_resize(max - 1, sets[i]);
            rb_fd_init(&orig[i]);
            rb_fd_dup(&orig[i], sets[i]);
        }
    }
    if (timeout) {
        gettimeofday(&end, NULL);
        timeradd(&end, timeout, &end);
    }
    for (;;) {
        struct timeval slice = { 0, 100000 };
        if (timeout) {
            gettimeofday(&now, NULL);
            if (timercmp(&now, &end, >=)) {
                timerclear(&slice);
            }
            else {
                struct timeval left;
                timersub(&end, &now, &left);
                if (timercmp(&left, &slice, <)) slice = left;
            }
        }
        result = select(max, read ? rb_fd_ptr(read) : NULL, write ? rb_fd_ptr(write) : NULL,
                        except ? rb_fd_ptr(except) : NULL, &slice);
        if (result != 0 && !(result < 0 && errno == EINTR)) {
            break;
        }
        if (timeout && result == 0 && !timerisset(&slice)) {
            break;
        }
        for (i = 0; i < 3; i++) {
            if (sets[i]) rb_fd_dup(sets[i], &orig[i]);
        }
        rb_thread_check_ints();
    }
    for (i = 0; i < 3; i++) {
        if (orig[i].fdset) rb_fd_term(&orig[i]);
    }
    return result;
}

/* thread.c's: wait until fd is readable, or writable, letting interrupts in */
int
rb_thread_wait_fd(int fd)
{
    rb_fdset_t set;
    rb_fd_init(&set);
    rb_fd_set(fd, &set);
    rb_thread_fd_select(fd + 1, &set, NULL, NULL, NULL);
    rb_fd_term(&set);
    return TRUE;
}

int
rb_thread_fd_writable(int fd)
{
    rb_fdset_t set;
    rb_fd_init(&set);
    rb_fd_set(fd, &set);
    rb_thread_fd_select(fd + 1, NULL, &set, NULL, NULL);
    rb_fd_term(&set);
    return TRUE;
}

/* io.c's: a descriptor above stderr is closed in a child process. */
void
rb_maygvl_fd_fix_cloexec(int fd)
{
    int flags = fcntl(fd, F_GETFD), flags2;
    if (flags == -1) {
        rb_bug("rb_maygvl_fd_fix_cloexec: fcntl(%d, F_GETFD) failed: %s", fd, strerror(errno));
    }
    flags2 = fd <= 2 ? flags & ~FD_CLOEXEC : flags | FD_CLOEXEC;
    if (flags != flags2 && fcntl(fd, F_SETFD, flags2) != 0) {
        rb_bug("rb_maygvl_fd_fix_cloexec: fcntl(%d, F_SETFD, %d) failed: %s", fd, flags2, strerror(errno));
    }
}

void
rb_update_max_fd(int fd)
{
    /* CRuby tracks the largest descriptor for its child processes; IronRuby's do not need it */
    (void)fd;
}

void
rb_fd_fix_cloexec(int fd)
{
    /* IO#fileno is IronRuby's own number for the stream, which rb_io_descriptor hands out */
    int native = rb_ir_native_fd(fd);
    if (native >= 0) rb_maygvl_fd_fix_cloexec(native);
    rb_update_max_fd(fd);
}

int
rb_cloexec_open(const char *pathname, int flags, mode_t mode)
{
    int ret;
#ifdef O_CLOEXEC
    flags |= O_CLOEXEC;
#endif
    while ((ret = open(pathname, flags, mode)) == -1 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
    }
    if (ret >= 0) {
        rb_maygvl_fd_fix_cloexec(ret);
    }
    return ret;
}

int
rb_cloexec_fcntl_dupfd(int fd, int minfd)
{
    int ret = fcntl(fd, F_DUPFD_CLOEXEC, minfd);
    if (ret >= 0) {
        rb_maygvl_fd_fix_cloexec(ret);
    }
    return ret;
}

int
rb_cloexec_dup(int oldfd)
{
    /* not one of the standard descriptors 0, 1 and 2 */
    return rb_cloexec_fcntl_dupfd(oldfd, 3);
}

int
rb_cloexec_dup2(int oldfd, int newfd)
{
    int ret;
    if (oldfd == newfd) {
        ret = newfd;
    }
    else {
#if defined(__linux__)
        if (newfd > 2) {
            ret = dup3(oldfd, newfd, O_CLOEXEC);
            if (ret != -1 || errno != ENOSYS) return ret;
        }
#endif
        ret = dup2(oldfd, newfd);
        if (ret < 0) return ret;
    }
    rb_maygvl_fd_fix_cloexec(ret);
    return ret;
}

int
rb_cloexec_pipe(int descriptors[2])
{
#if defined(__linux__)
    int result = pipe2(descriptors, O_CLOEXEC | O_NONBLOCK);
#else
    int result = pipe(descriptors);
    if (result == 0) {
        rb_maygvl_fd_fix_cloexec(descriptors[0]);
        rb_maygvl_fd_fix_cloexec(descriptors[1]);
        fcntl(descriptors[0], F_SETFL, fcntl(descriptors[0], F_GETFL) | O_NONBLOCK);
        fcntl(descriptors[1], F_SETFL, fcntl(descriptors[1], F_GETFL) | O_NONBLOCK);
    }
#endif
    return result;
}
/* io.c's descriptor-only waits, for a call that failed with errno */
int
rb_io_wait_readable(int f)
{
    switch (errno) {
      case EINTR:
#if defined(ERESTART)
      case ERESTART:
#endif
        rb_thread_check_ints();
        return TRUE;
      case EAGAIN:
#if defined(EWOULDBLOCK) && EWOULDBLOCK != EAGAIN
      case EWOULDBLOCK:
#endif
        rb_thread_wait_fd(f);
        return TRUE;
      default:
        return FALSE;
    }
}

int
rb_io_wait_writable(int f)
{
    switch (errno) {
      case EINTR:
#if defined(ERESTART)
      case ERESTART:
#endif
        rb_thread_check_ints();
        return TRUE;
      case EAGAIN:
#if defined(EWOULDBLOCK) && EWOULDBLOCK != EAGAIN
      case EWOULDBLOCK:
#endif
        rb_thread_fd_writable(f);
        return TRUE;
      default:
        return FALSE;
    }
}

/* thread.c's: the RUBY_IO_* events that came ready, 0 after the timeout, -1 on an error */
int
rb_wait_for_single_fd(int fd, int events, struct timeval *tv)
{
    rb_fdset_t sets[3];
    rb_fdset_t *r = NULL, *w = NULL, *e = NULL;
    int i, result, ready = 0;

    for (i = 0; i < 3; i++) rb_fd_init(&sets[i]);
    if (events & RUBY_IO_READABLE) { r = &sets[0]; rb_fd_set(fd, r); }
    if (events & RUBY_IO_WRITABLE) { w = &sets[1]; rb_fd_set(fd, w); }
    if (events & RUBY_IO_PRIORITY) { e = &sets[2]; rb_fd_set(fd, e); }
    result = rb_thread_fd_select(fd + 1, r, w, e, tv);
    if (result > 0) {
        if (r && rb_fd_isset(fd, r)) ready |= RUBY_IO_READABLE;
        if (w && rb_fd_isset(fd, w)) ready |= RUBY_IO_WRITABLE;
        if (e && rb_fd_isset(fd, e)) ready |= RUBY_IO_PRIORITY;
    }
    for (i = 0; i < 3; i++) rb_fd_term(&sets[i]);
    return result < 0 ? -1 : ready;
}
#endif /* !_WIN32 */

/*
 * vm.c's ruby_vm_at_exit: functions run, last registered first, once the runtime is gone -
 * here, at the process's exit (atexit), which is after IronRuby has run its at_exit blocks.
 */
#include "ruby/vm.h"
#define IR_VM_AT_EXIT_MAX 64
static void (*ir_vm_at_exit_funcs[IR_VM_AT_EXIT_MAX])(ruby_vm_t *);
static int ir_vm_at_exit_count;

static void
ir_run_vm_at_exit(void)
{
    while (ir_vm_at_exit_count > 0) {
        ir_vm_at_exit_funcs[--ir_vm_at_exit_count](NULL);
    }
    fflush(stdout);
}

void
ruby_vm_at_exit(void (*func)(ruby_vm_t *))
{
    if (ir_vm_at_exit_count == 0) atexit(ir_run_vm_at_exit);
    if (ir_vm_at_exit_count == IR_VM_AT_EXIT_MAX) rb_raise(rb_eRuntimeError, "too many ruby_vm_at_exit functions");
    ir_vm_at_exit_funcs[ir_vm_at_exit_count++] = func;
}

/* ------------------------------------------------------------------------------------------
 * ruby/ractor.h. A C extension only ever runs in the main Ractor here, so RB_EXT_RACTOR_SAFE has
 * nothing to record, the Ractor's standard streams are the process's ($stdin & co), and a
 * Ractor-local storage key holds one value for the process.
 */
#include "ruby/ractor.h"

void
rb_ext_ractor_safe(bool flag)
{
    (void)flag;
}

VALUE rb_ractor_stdin(void) { return rb_gv_get("$stdin"); }
VALUE rb_ractor_stdout(void) { return rb_gv_get("$stdout"); }
VALUE rb_ractor_stderr(void) { return rb_gv_get("$stderr"); }
void rb_ractor_stdin_set(VALUE io) { rb_gv_set("$stdin", io); }
void rb_ractor_stdout_set(VALUE io) { rb_gv_set("$stdout", io); }
void rb_ractor_stderr_set(VALUE io) { rb_gv_set("$stderr", io); }

VALUE
rb_ractor_make_shareable(VALUE obj)
{
    return rb_funcall(rb_cRactor, rb_intern("make_shareable"), 1, obj);
}

VALUE
rb_ractor_make_shareable_copy(VALUE obj)
{
    VALUE args[2];
    args[0] = obj;
    args[1] = rb_hash_new();
    rb_hash_aset(args[1], ID2SYM(rb_intern("copy")), Qtrue);
    return rb_funcallv_kw(rb_cRactor, rb_intern("make_shareable"), 2, args, RB_PASS_KEYWORDS);
}

/* what rb_ractor_shareable_p (an inline function) asks once the object's own bit says no */
bool
rb_ractor_shareable_p_continue(VALUE obj)
{
    return RTEST(rb_funcall(rb_cRactor, rb_intern("shareable?"), 1, obj));
}

struct rb_ractor_local_key_struct {
    const struct rb_ractor_local_storage_type *type;
    void *ptr;
    VALUE value;
    bool set;
};

const struct rb_ractor_local_storage_type rb_ractor_local_storage_type_free = { NULL, free };

static rb_ractor_local_key_t
ractor_local_newkey(const struct rb_ractor_local_storage_type *type)
{
    rb_ractor_local_key_t key = calloc(1, sizeof(struct rb_ractor_local_key_struct));
    if (key == NULL) rb_memerror();
    key->type = type;
    key->value = Qnil;
    return key;
}

rb_ractor_local_key_t rb_ractor_local_storage_value_newkey(void) { return ractor_local_newkey(NULL); }
rb_ractor_local_key_t rb_ractor_local_storage_ptr_newkey(const struct rb_ractor_local_storage_type *type) { return ractor_local_newkey(type); }

VALUE
rb_ractor_local_storage_value(rb_ractor_local_key_t key)
{
    return key->set ? key->value : Qnil;
}

bool
rb_ractor_local_storage_value_lookup(rb_ractor_local_key_t key, VALUE *val)
{
    if (!key->set) return false;
    *val = key->value;
    return true;
}

void
rb_ractor_local_storage_value_set(rb_ractor_local_key_t key, VALUE val)
{
    key->value = val;
    key->set = true;
}

void *rb_ractor_local_storage_ptr(rb_ractor_local_key_t key) { return key->ptr; }

void
rb_ractor_local_storage_ptr_set(rb_ractor_local_key_t key, void *ptr)
{
    key->ptr = ptr;
    key->set = true;
}

/* RB_OBJ_FREEZE / OBJ_FREEZE: CRuby sets the flag in place; here the object itself is frozen */
void
rb_obj_freeze_inline(VALUE obj)
{
    if (!RB_SPECIAL_CONST_P(obj)) rb_obj_freeze(obj);
}

/* RHASH_SIZE */
size_t
rb_hash_size_num(VALUE hash)
{
    return NUM2SIZET(rb_hash_size(hash));
}

/* object.c's: the exception: keyword of Integer() and friends, which extensions with
 * conversion functions of their own (bigdecimal's BigDecimal()) reuse */
int
rb_opts_exception_p(VALUE opts, int default_value)
{
    ID kwds[1];
    VALUE exception;
    kwds[0] = rb_intern("exception");
    if (!rb_get_kwargs(opts, kwds, 0, 1, &exception)) return default_value;
    if (exception == Qtrue) return 1;
    if (exception == Qfalse) return 0;
    rb_raise(rb_eArgError, "expected true or false as exception: %+"PRIsVALUE, exception);
    return 0;
}

void
rb_must_asciicompat(VALUE str)
{
    rb_encoding *enc = rb_enc_get(str);
    if (!enc) rb_raise(rb_eTypeError, "not encoding capable object");
    if (!rb_enc_asciicompat(enc)) rb_raise(rb_eEncCompatError, "ASCII incompatible encoding: %s", rb_enc_name(enc));
}

st_index_t
rb_memhash(const void *ptr, long len)
{
    return rb_st_hash(ptr, (size_t)len, 0);
}
