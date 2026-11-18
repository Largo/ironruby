/* ****************************************************************************
 *
 * -X:JITResume - an attribute operation after a typed body's first effect that does not
 * restart the body (Docs/jit-resume-deopt.md, choice A, stage 1).
 *
 * A typed body may deopt only while nothing it has done is observable: the entry answers a
 * deopt by running the generic body from the top. So `@n += 1; @o.x = v; @o.y' was declined:
 * the attribute operations after the instance variable write check the receiver's class and
 * the method version, and a failed check had nowhere to go. Here such an operation is
 * self-guarded: its fast path checks everything it relies on - the class, the method version,
 * that the receiver is not frozen and that no TracePoint listens for what a typed body leaves
 * out - and where the check fails it makes the call the source makes, for that one operation,
 * and the body goes on (ResumeFallback).
 *
 * The call is the generic body's call: through a site bound with a light scope and the scope
 * the method was defined in, as a light frame's calls are (MethodDefinition.LightFrameRewriter.
 * LightScopeCall), so that it sees private and protected methods as the source's call does;
 * when the callee would read the caller's scope - a library method that takes it, a protected
 * or missing method on a receiver, a refined method - the scope is made, once per invocation,
 * and the call goes through the full site. A body with such operations is wrapped in the
 * method frame's exception filter and leaves the frame as a light frame does.
 *
 * ***************************************************************************/

using System;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime.Jit {
    public static partial class JitRuntime {
        /// <summary>The fallbacks typed bodies made (ResumeFallback), for IR_JIT_VERBOSE=1 and the matrix.</summary>
        internal static int Fallbacks;

        /// <summary>
        /// What the fallbacks of one compilation of a method share: where the method was defined, the
        /// frame its scope would be made for (no parameters and no locals: stage 1 takes no other
        /// body), and its name in backtraces.
        /// </summary>
        public sealed class ResumeBody {
            internal readonly RubyContext/*!*/ Context;
            internal readonly RubyScope/*!*/ DeclaringScope;
            internal readonly RubyModule/*!*/ DeclaringModule;
            internal readonly MethodFrameShape/*!*/ Shape;
            internal readonly string/*!*/ Name;
            // The generic body's frame name ("file:line:in 'C#m'", encoded): a fallback runs under a frame
            // of that name (Trampoline), whose line is the operation's.
            internal readonly string/*!*/ FrameName;
            // What the generic body's :line and :return hooks report.
            internal readonly string/*!*/ SourcePath;
            internal readonly int EndLine;
            // (site, self, block, frame, receiver, value) => ResumeFallback(...), compiled under FrameName
            // when the compiler first emits a fallback (JitStub.ResumeTrampoline).
            private readonly Func<Func<ResumeSite, object, Proc, ResumeFrame, object, object, object>>/*!*/ _makeTrampoline;
            private Func<ResumeSite, object, Proc, ResumeFrame, object, object, object> _trampoline;
            internal Func<ResumeSite, object, Proc, ResumeFrame, object, object, object>/*!*/ Trampoline {
                get { return _trampoline ?? (_trampoline = _makeTrampoline()); }
            }
            // The specialization the body belongs to, once there is one: its fallbacks count towards
            // retiring its entry (JitStub.OnDeopt).
            internal volatile JitStub.Specialization Specialization;

            internal ResumeBody(RubyContext/*!*/ context, RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule, string/*!*/ name,
                string/*!*/ frameName, string/*!*/ sourcePath, int endLine,
                Func<Func<ResumeSite, object, Proc, ResumeFrame, object, object, object>>/*!*/ makeTrampoline) {
                Context = context;
                DeclaringScope = declaringScope;
                DeclaringModule = declaringModule;
                Shape = new MethodFrameShape(null, 0, name);
                Name = name;
                FrameName = frameName;
                SourcePath = sourcePath;
                EndLine = endLine;
                _makeTrampoline = makeTrampoline;
            }
        }

        /// <summary>
        /// One self-guarded operation of a typed body: the call it falls back to, as the two sites a
        /// light frame makes the same call through. Shared by every thread that runs the body.
        /// </summary>
        public sealed class ResumeSite {
            internal readonly ResumeBody/*!*/ Body;
            internal readonly int Line;
            internal readonly bool Writer;
            private readonly CallSite<Func<CallSite, RubyScope, object, object>> _lightRead, _fullRead;
            private readonly CallSite<Func<CallSite, RubyScope, object, object, object>> _lightWrite, _fullWrite;

            /// <param name="flags">The flags of the source's call (CallSiteBuilder.MakeCallAction).</param>
            internal ResumeSite(ResumeBody/*!*/ body, string/*!*/ name, RubyCallFlags flags, bool writer, int line) {
                Body = body;
                Line = line;
                Writer = writer;
                // `o.x = v' is a call of x= with no argument but the right side (RubyCallFlags.HasRhsArgument)
                var full = RubyCallAction.Make(body.Context, name, new RubyCallSignature(0, flags));
                var light = RubyCallAction.Make(body.Context, name, new RubyCallSignature(0, flags | RubyCallFlags.HasLightScope));
                if (writer) {
                    _lightWrite = CallSite<Func<CallSite, RubyScope, object, object, object>>.Create(light);
                    _fullWrite = CallSite<Func<CallSite, RubyScope, object, object, object>>.Create(full);
                } else {
                    _lightRead = CallSite<Func<CallSite, RubyScope, object, object>>.Create(light);
                    _fullRead = CallSite<Func<CallSite, RubyScope, object, object>>.Create(full);
                }
            }

            internal object Light(RubyScope/*!*/ declaringScope, object receiver, object value) {
                return Writer ? _lightWrite.Target(_lightWrite, declaringScope, receiver, value) : _lightRead.Target(_lightRead, declaringScope, receiver);
            }

            internal object Full(RubyScope/*!*/ scope, object receiver, object value) {
                return Writer ? _fullWrite.Target(_fullWrite, scope, receiver, value) : _fullRead.Target(_fullRead, scope, receiver);
            }
        }

        /// <summary>
        /// What an invocation of a typed body has once it has fallen back: the alias it was called by,
        /// and its scope and flow-control frame if a callee asked for them. Made at the first fallback,
        /// and the scope at most once, so that what one callee leaves in it ($~) the next one finds.
        /// </summary>
        public sealed class ResumeFrame {
            internal RubyMethodScope Scope;
            internal RuntimeFlowControl Rfc;
            internal string Callee;
            private bool _entered;

            // Before anything of the invocation runs Ruby code: the name the call site left for the
            // method's scope (RubyOps.MarkAliasCall), which a light frame takes at its entry.
            internal void Enter(ResumeBody/*!*/ body) {
                if (!_entered) {
                    _entered = true;
                    Callee = RubyOps.TakePendingCallee(body.Name);
                }
            }

            // The scope the generic body would have here, as a light frame makes it (MethodDefinition.
            // TransformLightFrame): no locals, self, the method's block - converted now, as the frame's
            // entry would have converted it - and the scope the method was defined in for a parent.
            internal RubyMethodScope/*!*/ MakeScope(ResumeBody/*!*/ body, object self, Proc block) {
                if (Scope == null) {
                    Enter(body);
                    Rfc = RubyOps.EnterLightMethod(block);
                    Scope = RubyOps.CreateMethodScopeLazy(body.Shape, body.DeclaringScope, body.DeclaringModule, null, self, block, null, Rfc, Callee);
                }
                return Scope;
            }
        }

        // ---- the self-guarded operations ---------------------------------------------------

        // No TracePoint listens for an event a typed body leaves out (JitStub.TypedBodyEvents): the entry
        // tested it on the way in, and a fallback may have enabled one since.
        private static bool NotTracing {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (TracePoint.ActiveEvents & JitStub.TypedBodyEvents) == 0; }
        }

        /// <summary>
        /// ReadSelfAttr that answers false where it would deopt, and while a TracePoint listens: the
        /// caller then makes the call (ResumeFallback).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadAttr(object self, SelfAttrSite/*!*/ site, out object value) {
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass) && NotTracing && RubyOps.TryReadCached(obj._instanceData, site.Variable, out value)) {
                return true;
            }
            return TryReadAttrUncached(self, site, out value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadAttrUncached(object self, SelfAttrSite/*!*/ site, out object value) {
            value = null;
            var obj = self as RubyObject;
            if (obj == null || !NotTracing || (!site.IsBoundTo(obj.ImmediateClass) && !site.TryBind(obj.ImmediateClass))) {
                return false;
            }
            var data = obj._instanceData;
            value = (data != null) ? data.GetInstanceVariable(site.Variable) : null;
            return true;
        }

        /// <summary>
        /// WriteAttr that answers false where it would deopt - another class, a frozen receiver, whose
        /// FrozenError the call raises - and while a TracePoint listens. Nothing is written then.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteAttr(RubyContext/*!*/ context, object self, object value, SelfAttrSite/*!*/ site) {
            if (self is RubyObject obj && site.IsBoundTo(obj.ImmediateClass) && NotTracing && RubyOps.TryWriteCached(obj._instanceData, value, site.Variable)) {
                return true;
            }
            return TryWriteAttrUncached(context, self, value, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryWriteAttrUncached(RubyContext/*!*/ context, object self, object value, SelfAttrSite/*!*/ site) {
            var obj = self as RubyObject;
            if (obj == null || !NotTracing || (!site.IsBoundTo(obj.ImmediateClass) && !site.TryBind(obj.ImmediateClass))) {
                return false;
            }
            var data = obj._instanceData ?? RubyOps.GetInstanceData(ref obj._instanceData);
            if (data._frozen) {
                return false;
            }
            // Not SetIVar: that deopts when another thread has frozen the object since, and this body
            // may not. The store then raises as the attr_writer's would, a call already past its lookup.
            if (!data.TrySetCached(site.Variable.Cache, value, obj)) {
                context.SetInstanceVariable(self, value, site.Variable);
            }
            return true;
        }

        /// <summary>ReadStructMember that answers false where it would deopt, and while a TracePoint listens.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadStructMember(object self, StructMemberSite/*!*/ site, out object value) {
            if (self is RubyStruct s && site.IsBoundTo(s.ImmediateClass) && NotTracing) {
                value = RubyStruct.GetValue(s, site.Index);
                return true;
            }
            return TryReadStructMemberUncached(self, site, out value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadStructMemberUncached(object self, StructMemberSite/*!*/ site, out object value) {
            value = null;
            var s = self as RubyStruct;
            if (s == null || !NotTracing || (!site.IsBoundTo(s.ImmediateClass) && !site.TryBind(s.ImmediateClass))) {
                return false;
            }
            value = RubyStruct.GetValue(s, site.Index);
            return true;
        }

        /// <summary>WriteStructMember that answers false where it would deopt, and while a TracePoint listens.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteStructMember(object self, object value, StructMemberSite/*!*/ site) {
            if (self is RubyStruct s && site.IsBoundTo(s.ImmediateClass) && NotTracing && !s.IsFrozen) {
                RubyStruct.SetValue(s, site.Index, value);
                return true;
            }
            return TryWriteStructMemberUncached(self, value, site);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryWriteStructMemberUncached(object self, object value, StructMemberSite/*!*/ site) {
            var s = self as RubyStruct;
            if (s == null || !NotTracing || s.IsFrozen || (!site.IsBoundTo(s.ImmediateClass) && !site.TryBind(s.ImmediateClass))) {
                return false;
            }
            RubyStruct.SetValue(s, site.Index, value);
            return true;
        }

        // ---- the fallback ------------------------------------------------------------------

        /// <summary>
        /// The call a self-guarded operation of a typed body makes when its guard fails: `receiver.x'
        /// or `receiver.x = value' as the generic body makes it. Called through the body's trampoline,
        /// whose frame is the method's frame in a backtrace taken underneath; its line comes from the
        /// record entered here, as a compiled generic body's does (CompiledFrameExpression).
        /// </summary>
        public static object ResumeFallback(ResumeSite/*!*/ site, object self, Proc block, ResumeFrame/*!*/ frame, object receiver, object value) {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var body = site.Body;
            Fallbacks++;
            frame.Enter(body);

            var frames = Precompiled.PrecompiledFrames.Current;
            int depth = frames.Enter(body.FrameName, site.Line);
            bool returned = false;
            long called = 0, back = 0;
            object result;
            try {
                // a fallback is a path from a typed body into Ruby code, which may call the body again
                RubyOps.RequireSufficientStack();
                called = System.Diagnostics.Stopwatch.GetTimestamp();
                result = site.Light(body.DeclaringScope, receiver, value);
                if (result == RubyOps.LightScopeRequired || result == RubyOps.LightBlockGiven) {
                    // the callee reads the caller's scope: the call again, with the invocation's scope
                    result = site.Full(frame.MakeScope(body, self, block), receiver, value);
                }
                back = System.Diagnostics.Stopwatch.GetTimestamp();
                returned = true;
            } finally {
                // an exception leaves the frame with its line, for the backtrace taken where it is caught
                if (returned) { frames.Return(depth); } else { frames.Leave(depth); }
            }

            // What the fallback cost over the call itself counts as a deopt of the entry, which is
            // retired when it falls back too often to pay (JitStub.OnDeopt).
            var spec = body.Specialization;
            if (spec != null) {
                spec.Stub.OnDeopt(spec, (called - start) + (System.Diagnostics.Stopwatch.GetTimestamp() - back));
            }
            return result;
        }

        /// <summary>
        /// The scope of an invocation for a TracePoint hook of the statements after its first
        /// self-guarded operation, made on demand as a light frame's hooks get theirs.
        /// </summary>
        public static RubyScope/*!*/ ResumeScope(ResumeBody/*!*/ body, object self, Proc block, ResumeFrame/*!*/ frame) {
            return frame.MakeScope(body, self, block);
        }

        /// <summary>The method frame's exception filter (RubyOps.IsMethodUnwinderTargetFrame): a `return' from a proc made in the invocation's scope.</summary>
        public static bool ResumeIsTarget(ResumeFrame frame, MethodUnwinder/*!*/ unwinder) {
            return frame != null && frame.Scope != null && unwinder.TargetFrame == frame.Scope.FlowControlScope;
        }

        /// <summary>Leaves the method frame an invocation made (RubyOps.LeaveLightMethodFrame).</summary>
        public static void ResumeLeave(ResumeFrame/*!*/ frame) {
            if (frame.Scope != null) {
                RubyOps.LeaveLightMethodFrame(frame.Rfc, frame.Scope);
            }
        }
    }
}
