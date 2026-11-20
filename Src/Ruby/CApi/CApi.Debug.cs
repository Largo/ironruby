/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// ruby/debug.h: TracePoints with a C hook, the trace argument readers, and the debug
    /// inspector. ObjectSpace.memsize_of's call into a typed object's dsize function too.
    /// </summary>
    internal static unsafe partial class CApi {

        #region TracePoint

        private static delegate* unmanaged[Cdecl]<void*, VALUE, void*, int*, void> _callTracepoint;

        // MRI's RUBY_EVENT_* bits, which TraceEvents shares; the names in bit order, as TracePoint#event
        private static readonly string[] _traceEventNames = {
            "line", "class", "end", "call", "return", "c_call", "c_return", "raise",
            "b_call", "b_return", "thread_begin", "thread_end", "fiber_switch", "script_compiled", "rescue"
        };

        // The hook is func(tpval, data). target_thread is not supported by CRuby either (the
        // parameter is named target_thread_not_supported_yet): it traces every thread.
        internal static VALUE rb_tracepoint_new(VALUE target_thread, uint events, IntPtr func, void* data) {
            if (_callTracepoint == null) {
                _callTracepoint = (delegate* unmanaged[Cdecl]<void*, VALUE, void*, int*, void>)ShimExport("rb_ir_call_tracepoint");
            }
            var handler = Proc.Create(_context, 0, new Func<BlockParam, object, object[], RubyArray, object>((b, self, unused, args) => {
                int raised;
                var depth = EnterNative();
                try {
                    _callTracepoint((void*)func, ToValue(args.Count > 0 ? args[0] : null), data, &raised);
                } finally {
                    LeaveNative(depth);
                }
                if (raised != 0) {
                    Rethrow();
                }
                return null;
            }));
            return ToValue(new TracePoint(_context, (TraceEvents)(events & (uint)TraceEvents.All), handler));
        }

        private static TracePoint/*!*/ ToTracePoint(VALUE tpval) {
            var tp = FromValue(tpval) as TracePoint;
            if (tp == null) {
                throw RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (expected tracepoint)", ClassName(FromValue(tpval))));
            }
            return tp;
        }

        internal static VALUE rb_tracepoint_enable(VALUE tpval) {
            ToTracePoint(tpval).Enable();
            return Qundef;
        }

        internal static VALUE rb_tracepoint_disable(VALUE tpval) {
            ToTracePoint(tpval).Disable();
            return Qundef;
        }

        internal static VALUE rb_tracepoint_enabled_p(VALUE tpval) {
            return ToTracePoint(tpval).IsEnabled ? Qtrue : Qfalse;
        }

        // rb_trace_arg_t is opaque to extensions: IronRuby's is the TracePoint's handle, whose
        // readers answer for the event being delivered, as tracearg's do.
        internal static void* rb_tracearg_from_tracepoint(VALUE tpval) {
            ToTracePoint(tpval);
            return (void*)tpval;
        }

        private static VALUE TraceArg(void* arg, string/*!*/ name) {
            return CallV((VALUE)arg, name);
        }

        internal static uint rb_tracearg_event_flag(void* arg) {
            int i = Array.IndexOf(_traceEventNames, FromValue(TraceArg(arg, "event"))?.ToString());
            return i >= 0 ? 1u << i : 0;
        }

        internal static VALUE rb_tracearg_event(void* arg) { return TraceArg(arg, "event"); }
        internal static VALUE rb_tracearg_lineno(void* arg) { return TraceArg(arg, "lineno"); }
        internal static VALUE rb_tracearg_path(void* arg) { return TraceArg(arg, "path"); }
        internal static VALUE rb_tracearg_parameters(void* arg) { return TraceArg(arg, "parameters"); }
        internal static VALUE rb_tracearg_method_id(void* arg) { return TraceArg(arg, "method_id"); }
        internal static VALUE rb_tracearg_callee_id(void* arg) { return TraceArg(arg, "callee_id"); }
        internal static VALUE rb_tracearg_defined_class(void* arg) { return TraceArg(arg, "defined_class"); }
        internal static VALUE rb_tracearg_binding(void* arg) { return TraceArg(arg, "binding"); }
        internal static VALUE rb_tracearg_self(void* arg) { return TraceArg(arg, "self"); }
        internal static VALUE rb_tracearg_return_value(void* arg) { return TraceArg(arg, "return_value"); }
        internal static VALUE rb_tracearg_raised_exception(void* arg) { return TraceArg(arg, "raised_exception"); }
        internal static VALUE rb_tracearg_eval_script(void* arg) { return TraceArg(arg, "eval_script"); }
        internal static VALUE rb_tracearg_instruction_sequence(void* arg) { return TraceArg(arg, "instruction_sequence"); }

        #endregion

        #region Debug inspector

        private static delegate* unmanaged[Cdecl]<void*, void*, void*, int*, VALUE> _callInspector;

        /// <summary>
        /// One frame rb_debug_inspector_open saw. Only the C method that opened it and the Ruby
        /// code that called that method are known in full: IronRuby keeps no chain of the callers'
        /// scopes, so the frames above them have a location and nothing else.
        /// </summary>
        private sealed class InspectedFrame {
            public object Self, Class, Binding, Location;
        }

        private static InspectedFrame[]/*!*/ InspectFrames() {
            var locations = Send(_context.KernelModule, "caller_locations", null, 0) as RubyArray ?? new RubyArray();
            var frame = _frame;
            int offset = frame != null ? 1 : 0;
            var frames = new InspectedFrame[locations.Count + offset];
            for (int i = 0; i < locations.Count; i++) {
                frames[i + offset] = new InspectedFrame { Location = locations[i] };
            }
            if (frame == null) {
                return frames;
            }

            // a C frame has no binding; its location is its caller's, as a cfunc frame's is in CRuby
            frames[0] = new InspectedFrame {
                Self = frame.Self,
                Class = frame.Method.Owner,
                Location = locations.Count > 0 ? locations[0] : null,
            };
            var scope = frame.Scope;
            if (scope != null && frames.Length > 1) {
                var binding = Binding.Create(scope, scope.SelfObject);
                string path;
                int line;
                if (scope.TryGetCurrentSourceLocation(out path, out line)) {
                    binding.SourcePath = path;
                    binding.SourceLine = line;
                }
                var methodScope = scope.GetInnerMostMethodScope();
                frames[1].Self = scope.SelfObject;
                frames[1].Class = methodScope != null ? (object)methodScope.DeclaringModule : null;
                frames[1].Binding = binding;
            }
            return frames;
        }

        internal static VALUE rb_debug_inspector_open(IntPtr func, void* data) {
            if (_callInspector == null) {
                _callInspector = (delegate* unmanaged[Cdecl]<void*, void*, void*, int*, VALUE>)ShimExport("rb_ir_call_inspector");
            }
            var handle = GCHandle.Alloc(InspectFrames());
            try {
                int raised;
                VALUE result;
                var depth = EnterNative();
                try {
                    result = _callInspector((void*)func, (void*)GCHandle.ToIntPtr(handle), data, &raised);
                } finally {
                    LeaveNative(depth);
                }
                if (raised != 0) {
                    Rethrow();
                }
                return result;
            } finally {
                handle.Free();
            }
        }

        private static InspectedFrame/*!*/ InspectedFrameAt(void* dc, long index) {
            var frames = (InspectedFrame[])GCHandle.FromIntPtr((IntPtr)dc).Target;
            long i = index;
            if (i < 0 || i >= frames.Length) {
                throw RubyExceptions.CreateArgumentError("no such frame");
            }
            return frames[i];
        }

        internal static VALUE rb_debug_inspector_backtrace_locations(void* dc) {
            var frames = (InspectedFrame[])GCHandle.FromIntPtr((IntPtr)dc).Target;
            var result = new RubyArray(frames.Length);
            foreach (var frame in frames) {
                result.Add(frame.Location);
            }
            return ToValue(result);
        }

        internal static VALUE rb_debug_inspector_frame_self_get(void* dc, long index) { return ToValue(InspectedFrameAt(dc, index).Self); }
        internal static VALUE rb_debug_inspector_frame_class_get(void* dc, long index) { return ToValue(InspectedFrameAt(dc, index).Class); }
        internal static VALUE rb_debug_inspector_frame_binding_get(void* dc, long index) { return ToValue(InspectedFrameAt(dc, index).Binding); }

        // IronRuby has no RubyVM::InstructionSequence
        internal static VALUE rb_debug_inspector_frame_iseq_get(void* dc, long index) {
            InspectedFrameAt(dc, index);
            return Qnil;
        }

        // the frames are counted from the outermost, as CRuby's depth is
        internal static VALUE rb_debug_inspector_frame_depth(void* dc, long index) {
            var frames = (InspectedFrame[])GCHandle.FromIntPtr((IntPtr)dc).Target;
            InspectedFrameAt(dc, index);
            return ToValue(frames.Length - 1 - index);
        }

        internal static VALUE rb_debug_inspector_current_depth() {
            return ToValue((long)InspectFrames().Length);
        }

        #endregion

        #region ObjectSpace.memsize_of

        private static delegate* unmanaged[Cdecl]<VALUE, int*, nuint> _typedDataMemsize;

        internal static long TypedDataMemsize(DataObject/*!*/ obj) {
            if (_typedDataMemsize == null) {
                _typedDataMemsize = (delegate* unmanaged[Cdecl]<VALUE, int*, nuint>)ShimExport("rb_ir_typed_data_memsize");
            }
            int raised;
            nuint size;
            var depth = EnterNative();
            try {
                size = _typedDataMemsize(ToValue(obj), &raised);
            } finally {
                LeaveNative(depth);
            }
            if (raised != 0) {
                Rethrow();
            }
            return (long)size;
        }

        #endregion
    }
}
