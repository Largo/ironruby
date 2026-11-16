/* ****************************************************************************
 *
 * -X:JITResume - stage 1 of Docs/jit-resume-deopt.md: a guard that fails after a typed body's
 * first effect does not restart the body.
 *
 * A typed body's one way out of a failed speculation is JitDeoptException, which re-runs the
 * generic body from the top, so JitCompiler declines a body in which anything that can deopt
 * follows an effect (_deoptAfterEffect). With the switch, an attribute read or write on another
 * object after the first effect is self-guarding instead (TryReadAttr, TryWriteAttr): the fast
 * path re-checks the receiver's class, the global method version, frozen-ness for a write and
 * that nothing is traced, and answers "missed" rather than throwing; the slow path makes the
 * source's call, once, on the operands already evaluated (FallbackRead, FallbackWrite), and the
 * typed body goes on with its result as an object.
 *
 * What makes the call the source's call:
 *   - it goes through a site bound with a light scope (RubyCallFlags.HasLightScope) and the
 *     declaring scope, and, when that answers RubyOps.LightScopeRequired (a protected or missing
 *     method, a refined one, a library method that reads the scope), through the full site with
 *     the method's scope, made once per invocation, as a light frame's (JitFrame.Scope). Never a
 *     site without a scope, which would see private and protected methods.
 *   - the frame: a body with fallbacks takes the block and is wrapped in a light frame's
 *     try/filter/finally (JitCompiler.WrapResumeBody), so a `return' from a proc made in the scope
 *     returns from the method and a proc kept after it raises LocalJumpError.
 *   - the backtrace: the typed body is part of its entry, whose frame a backtrace leaves out, so
 *     a fallback's call is made from a small lambda named as the generic body's frame
 *     (JitResumeInfo.Slow), which keeps a record of PrecompiledFrames with the line of its
 *     statement for as long as the call runs, as a compiled frame's line markers would: the
 *     method has one frame in a backtrace taken in the call, at the line of the operation. The
 *     fast path pays nothing for it (a typed body of its own, invoked by the entry, cost ~4 ns
 *     a call on hold_self).
 *   - the stack: the call checks it, as CreateMethodScope does at a method's entry.
 *   - tracing: the statements after the first fallback carry the generic body's :line hooks and
 *     its :return hook (ResumeLine, ResumeReturn), and the self-guards send every attribute
 *     operation to its call while anything is traced, which reports its own :c_call.
 *
 * Stage 1 takes only bodies with no parameters and no locals, in which nothing speculative but
 * self-guarded attribute operations follows the first fallback (JitCompiler._resumeTail): see
 * the design for why.
 *
 * ***************************************************************************/

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Precompiled;
using Expression = System.Linq.Expressions.Expression;

namespace IronRuby.Runtime.Jit {

    /// <summary>
    /// What a typed body with fallbacks needs to make the frame its generic body would have: one per
    /// compiled body, a constant in it.
    /// </summary>
    public sealed class JitResumeInfo {
        internal readonly string/*!*/ FrameName;
        internal readonly string/*!*/ Path;
        internal readonly bool Traceable;
        internal readonly int EndLine;
        internal readonly MethodFrameShape/*!*/ Shape;
        internal readonly RubyScope/*!*/ DeclaringScope;
        internal readonly RubyModule/*!*/ DeclaringModule;
        internal readonly string/*!*/ Label;

        // The specialization whose entry runs the body (JitStub.TrySpecialize): fallbacks count as its deopts.
        internal JitStub.Specialization Specialization;

        // IR_JIT_VERBOSE=1: how many times the body fell back. Not interlocked: a lost count only blurs it.
        internal int Fallbacks;

        // What a fallback or a hook runs in (JitRuntime.ResumeSlow): a lambda named FrameName, the method's frame in a
        // backtrace taken there. Compiled the first time one runs; two threads compiling it at once make two that do the same.
        private Func<int, JitFrame, object, object, object>/*!*/ _slow;

        internal Func<int, JitFrame, object, object, object>/*!*/ Slow {
            get {
                var slow = Volatile.Read(ref _slow);
                if (slow == null) {
                    var kind = Expression.Parameter(typeof(int), "kind");
                    var frame = Expression.Parameter(typeof(JitFrame), "frame");
                    var site = Expression.Parameter(typeof(object), "site");
                    var a = Expression.Parameter(typeof(object), "a");
                    var result = Expression.Variable(typeof(object), "#result");
                    // GC.KeepAlive after the call keeps it out of tail position, where RyuJIT would make it a jump and the
                    // frame would be gone from the stack (JitCompiler.KeepOutOfTailPosition)
                    slow = Expression.Lambda<Func<int, JitFrame, object, object, object>>(
                        Expression.Block(typeof(object), new[] { result },
                            Expression.Assign(result, Expression.Call(typeof(JitRuntime).GetMethod("ResumeSlowCall"), kind, frame, site, a)),
                            Expression.Call(typeof(GC).GetMethod("KeepAlive"), frame),
                            result
                        ), FrameName,
                        new[] { kind, frame, site, a }
                    ).Compile();
                    Volatile.Write(ref _slow, slow);
                }
                return slow;
            }
        }

        internal JitResumeInfo(string/*!*/ frameName, string/*!*/ path, int endLine, string/*!*/ name, RubyScope/*!*/ declaringScope,
            RubyModule/*!*/ declaringModule) {
            FrameName = frameName;
            Path = path;
            Traceable = !TracePoint.IsCoreLibraryPath(path);
            EndLine = endLine;
            // stage 1: no parameters, no locals, no block parameter (JitCompiler.ResumeCandidate)
            Shape = new MethodFrameShape(null, 0, name);
            DeclaringScope = declaringScope;
            DeclaringModule = declaringModule;
            Label = (declaringModule.Name ?? "main") + "/" + name;
        }

        /// <summary>The path a frame name carries (RubyStackTraceBuilder.EncodeMethodName), or null.</summary>
        internal static string PathOf(string/*!*/ frameName) {
            string name = frameName, file = null;
            int line = 0;
            return RubyStackTraceBuilder.TryParseRubyMethodName(ref name, ref file, ref line) ? file : null;
        }
    }

    /// <summary>
    /// One invocation's frame, made the first time the invocation falls back or a hook fires (ResumeFrame):
    /// the call's flow-control frame, and the method's scope once something asks for it - what a light frame
    /// keeps in its #rfc and #scope.
    /// </summary>
    public sealed class JitFrame {
        internal readonly JitResumeInfo/*!*/ Info;
        private readonly object _self;
        private readonly Proc _block;
        private readonly RuntimeFlowControl/*!*/ _rfc;
        private readonly string _callee;
        private RubyMethodScope _scope;

        internal JitFrame(JitResumeInfo/*!*/ info, object self, Proc block) {
            Info = info;
            _self = self;
            _block = block;
            // What a light frame's entry does (MethodDefinition.LightFrame.Enter), late: until now the typed body has
            // not run Ruby code, so nothing has seen the block or taken the pending callee name.
            _rfc = RubyOps.EnterLightMethod(block);
            _callee = RubyOps.TakePendingCallee(info.Shape.DefinitionName);
        }

        /// <summary>The method's scope, made once per invocation (LightFrame.LazyScope).</summary>
        internal RubyMethodScope/*!*/ Scope {
            get {
                return _scope ??= RubyOps.CreateMethodScopeLazy(Info.Shape, Info.DeclaringScope, Info.DeclaringModule, null, _self, _block,
                    null, _rfc, _callee);
            }
        }

        // the light frame's filter (MethodDefinition.TransformBody)
        internal bool IsTarget(Exception/*!*/ exception) {
            return RubyOps.IsMethodUnwinderTargetFrame(Scope, exception);
        }

        // the light frame's finally
        internal void Leave() {
            RubyOps.LeaveLightMethodFrame(_rfc, _scope);
        }
    }

    /// <summary>
    /// An attribute operation's call, as the generic body makes it: `o.x' (0 arguments) or `o.x = v' (the right-hand
    /// side), each through a site with a light scope and, when that asks for the scope, through the full one.
    /// </summary>
    public sealed class JitFallbackSite {
        internal readonly string/*!*/ Name;
        internal readonly int Line;
        internal readonly CallSite<Func<CallSite, RubyScope, object, object>> LightRead, FullRead;
        internal readonly CallSite<Func<CallSite, RubyScope, object, object, object>> LightWrite, FullWrite;

        internal JitFallbackSite(RubyContext/*!*/ context, string/*!*/ name, bool writer, int line) {
            Name = name;
            Line = line;
            // `o.x = v': MethodCall.TransformRead with the right-hand side (no arguments, HasRhsArgument); `o.x': none.
            // An explicit receiver other than self: not HasImplicitSelf, so a private method is not found.
            var flags = RubyCallFlags.HasScope | (writer ? RubyCallFlags.HasRhsArgument : RubyCallFlags.None);
            var full = RubyCallAction.Make(context, name, new RubyCallSignature(0, flags));
            var light = RubyCallAction.Make(context, name, new RubyCallSignature(0, flags | RubyCallFlags.HasLightScope));
            if (writer) {
                LightWrite = CallSite<Func<CallSite, RubyScope, object, object, object>>.Create(light);
                FullWrite = CallSite<Func<CallSite, RubyScope, object, object, object>>.Create(full);
            } else {
                LightRead = CallSite<Func<CallSite, RubyScope, object, object>>.Create(light);
                FullRead = CallSite<Func<CallSite, RubyScope, object, object>>.Create(full);
            }
        }
    }

    public static partial class JitRuntime {
        /// <summary>
        /// -X:JITResume: the specializations whose bodies have fallbacks, and how many times one fell back to an
        /// operation's call; for IR_JIT_VERBOSE=1 and the tests. Not interlocked, like the other statistics.
        /// </summary>
        internal static int ResumeBodies, FallbackCount;

        /// <summary>What TryReadAttr answers when the read is not one it can make.</summary>
        public static readonly object Missed = new object();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Traced() {
            return (TracePoint.ActiveEvents & JitStub.TypedBodyEvents) != 0;
        }

        /// <summary>
        /// `o.x' after a typed body's first effect: ReadSelfAttr's read, under the same checks and while nothing is
        /// traced, or Missed - then the body makes the call (FallbackRead).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object TryReadAttr(object self, SelfAttrSite/*!*/ site) {
            object value;
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass) && !Traced() && RubyOps.TryReadCached(obj._instanceData, site.Variable, out value)) {
                return value;
            }
            return TryReadAttrUncached(self, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object TryReadAttrUncached(object self, SelfAttrSite/*!*/ site) {
            // (ReadSelfAttr reads the variable of an object that is not a RubyObject too; here that is the call's job)
            var obj = self as RubyObject;
            if (obj == null || (!site.IsBoundTo(obj.ImmediateClass) && !site.Bind(obj.ImmediateClass)) || Traced()) {
                return Missed;
            }
            var data = obj._instanceData;
            return (data != null) ? data.GetInstanceVariable(site.Variable) : null;
        }

        /// <summary>
        /// `o.x = v' after a typed body's first effect: WriteAttr's store, under the same checks and while nothing is
        /// traced, or false - a frozen o included, whose FrozenError the call raises (FallbackWrite). Another thread
        /// may freeze o or redefine x= after the check: the store is then as if made just before (WriteAttr).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteAttr(RubyContext/*!*/ context, object self, object value, SelfAttrSite/*!*/ site) {
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass) && !Traced() && RubyOps.TryWriteCached(obj._instanceData, value, site.Variable)) {
                return true;
            }
            return TryWriteAttrUncached(context, self, value, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryWriteAttrUncached(RubyContext/*!*/ context, object self, object value, SelfAttrSite/*!*/ site) {
            var obj = self as RubyObject;
            if (obj == null || (!site.IsBoundTo(obj.ImmediateClass) && !site.Bind(obj.ImmediateClass)) || Traced()) {
                return false;
            }
            // SetIVar's store (SetIVarUncached) without its deopt: a frozen object is the call's, to raise its FrozenError
            var data = obj._instanceData ?? RubyOps.GetInstanceData(ref obj._instanceData);
            if (data._frozen) {
                return false;
            }
            if (!data.TrySetCached(site.Variable.Cache, value, obj)) {
                context.SetInstanceVariable(self, value, site.Variable);
            }
            return true;
        }

        // What ResumeSlow is asked to do.
        internal const int SlowRead = 0, SlowWrite = 1, SlowLine = 2, SlowReturn = 3;

        /// <summary>
        /// A fallback's call or a hook: <paramref name="kind"/> says which, <paramref name="site"/> is the JitFallbackSite
        /// (the boxed line of a :line hook), <paramref name="a"/> the receiver (the value, of a :return hook) and
        /// <paramref name="b"/> the value of a write. Through the lambda named as the method's frame (JitResumeInfo.Slow).
        /// </summary>
        public static object ResumeSlow(int kind, JitFrame/*!*/ frame, object site, object a, object b) {
            if (kind == SlowWrite) {
                // the value goes with the receiver: the lambda takes one operand
                return frame.Info.Slow(kind, frame, site, new object[] { a, b });
            }
            return frame.Info.Slow(kind, frame, site, a);
        }

        /// <summary>What the lambda named as the method's frame calls (JitResumeInfo.Slow).</summary>
        public static object ResumeSlowCall(int kind, JitFrame/*!*/ frame, object site, object a) {
            switch (kind) {
                case SlowRead: return FallbackRead(frame, (JitFallbackSite)site, a);
                case SlowWrite: var operands = (object[])a; FallbackWrite(frame, (JitFallbackSite)site, operands[0], operands[1]); return null;
                case SlowLine: ResumeLine(frame, (int)site); return null;
                default: ResumeReturn(frame, a); return null;
            }
        }

        /// <summary>The invocation's frame (JitFrame), made the first time it is asked for.</summary>
        public static JitFrame/*!*/ ResumeFrame(JitFrame frame, JitResumeInfo/*!*/ info, object self, Proc block) {
            return frame ?? new JitFrame(info, self, block);
        }

        /// <summary>The call `o.x' that TryReadAttr could not answer for.</summary>
        private static object FallbackRead(JitFrame/*!*/ frame, JitFallbackSite/*!*/ site, object receiver) {
            long start = Fallen(frame, site, receiver);
            var frames = PrecompiledFrames.Current;
            int depth = frames.Enter(frame.Info.FrameName, site.Line);
            bool returned = false;
            try {
                RubyOps.RequireSufficientStack();
                var light = site.LightRead;
                object result = light.Target(light, frame.Info.DeclaringScope, receiver);
                if (result == RubyOps.LightScopeRequired) {
                    var full = site.FullRead;
                    result = full.Target(full, frame.Scope, receiver);
                }
                returned = true;
                return result;
            } finally {
                Return(frames, depth, returned);
                Counted(frame, start);
            }
        }

        /// <summary>The call `o.x = v' that TryWriteAttr could not make the store of. Its value is not the expression's.</summary>
        private static void FallbackWrite(JitFrame/*!*/ frame, JitFallbackSite/*!*/ site, object receiver, object value) {
            long start = Fallen(frame, site, receiver);
            var frames = PrecompiledFrames.Current;
            int depth = frames.Enter(frame.Info.FrameName, site.Line);
            bool returned = false;
            try {
                RubyOps.RequireSufficientStack();
                var light = site.LightWrite;
                object result = light.Target(light, frame.Info.DeclaringScope, receiver, value);
                if (result == RubyOps.LightScopeRequired) {
                    var full = site.FullWrite;
                    full.Target(full, frame.Scope, receiver, value);
                }
                returned = true;
            } finally {
                Return(frames, depth, returned);
                Counted(frame, start);
            }
        }

        // A frame an exception left keeps its line for the exception's backtrace (CompiledFrameExpression).
        private static void Return(PrecompiledFrames/*!*/ frames, int depth, bool returned) {
            if (returned) {
                frames.Return(depth);
            } else {
                frames.Leave(depth);
            }
        }

        private static long Fallen(JitFrame/*!*/ frame, JitFallbackSite/*!*/ site, object receiver) {
            FallbackCount++;
            var info = frame.Info;
            int n = ++info.Fallbacks;
            if (Verbose && (n & (n - 1)) == 0) {
                Console.Error.WriteLine("[jit] fallback in {0}: {1} on {2} at line {3} (fallbacks={4})", info.Label, site.Name,
                    ClassOf(info.DeclaringModule.Context, receiver).Name ?? "an anonymous class", site.Line, n);
            }
            return Stopwatch.GetTimestamp();
        }

        // A fallback is a deopt as far as retiring the entry goes (JitStub.OnDeopt), at what the call cost: a body that
        // falls back on most calls is retired as one that deopts on them would be.
        private static void Counted(JitFrame/*!*/ frame, long start) {
            var spec = frame.Info.Specialization;
            if (spec != null) {
                spec.Stub.OnDeopt(spec, Math.Max(1, Stopwatch.GetTimestamp() - start));
            }
        }

        /// <summary>
        /// The :line hook of a statement after the first fallback, run while a TracePoint listens for :line (the
        /// test is inline): as the generic body's (TraceLineExpression), on the scope made on demand.
        /// </summary>
        private static void ResumeLine(JitFrame/*!*/ frame, int line) {
            var info = frame.Info;
            var frames = PrecompiledFrames.Current;
            int depth = frames.Enter(info.FrameName, line);
            bool returned = false;
            try {
                // the hook runs the TracePoint's block: Ruby code, as a fallback's call is
                RubyOps.RequireSufficientStack();
                RubyOps.TraceLineEvent(frame.Scope, info.Path, line);
                returned = true;
            } finally {
                Return(frames, depth, returned);
            }
        }

        /// <summary>
        /// The :return hook of a body with fallbacks, falling off its end while a TracePoint listens for :return:
        /// as the generic body's (TraceReturnExpression), at the line of its `end'.
        /// </summary>
        private static void ResumeReturn(JitFrame/*!*/ frame, object value) {
            var info = frame.Info;
            var frames = PrecompiledFrames.Current;
            int depth = frames.Enter(info.FrameName, info.EndLine);
            bool returned = false;
            try {
                RubyOps.RequireSufficientStack();
                RubyOps.TraceReturnEvent(frame.Scope, value, info.Path, info.EndLine);
                returned = true;
            } finally {
                Return(frames, depth, returned);
            }
        }

        /// <summary>The light frame's filter: a MethodUnwinder whose target is this invocation's frame.</summary>
        public static bool ResumeIsTarget(JitFrame frame, Exception/*!*/ exception) {
            return frame != null && frame.IsTarget(exception);
        }

        public static object ResumeReturnValue(Exception/*!*/ exception) {
            return RubyOps.GetMethodUnwinderReturnValue(exception);
        }

        /// <summary>The light frame's finally.</summary>
        public static void ResumeLeave(JitFrame frame) {
            if (frame != null) {
                frame.Leave();
            }
        }
    }
}
