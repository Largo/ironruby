/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using IronRuby.Builtins;

namespace IronRuby.Runtime {

    /// <summary>
    /// The $~ of a light frame that makes no scope for its String, Symbol and Regexp calls (RubyCallFlags.HasMatchSink,
    /// MethodDefinition.IsMatchSink). The rule of such a call gives the library method a scope of the thread's own to store
    /// $~ in (EnterMatchSink), and hands what it stored to the frame when it returns (LeaveMatchSink); the frame keeps it in a
    /// CLR local (TakeMatchSink) and puts it into its scope if one is made later (SeedLightScopeMatch) - for a TracePoint's
    /// binding, say. So $~ is the frame's, as in CRuby, and nothing shared holds a match: the sink scopes are cleared as they
    /// are left. A stack, because a library method may run Ruby code after it has stored $~ (`sub(re, hash)' calls the
    /// hash's default proc), and that code's own calls must not take the match of the call that is still running.
    /// </summary>
    public static partial class RubyOps {
        // stored in a sink scope when it is entered: a store by the library method replaces it
        private static readonly MatchData MatchSinkUnset = new MatchData();

        // A thread's sink scopes, one per call in progress, and what the last call left stored until the frame that made it
        // takes it (MatchSinkUnset if nothing, or taken). One object, so that each helper reads one thread static.
        private sealed class MatchSinkState {
            internal RubyTopLevelScope[] Sinks = new RubyTopLevelScope[4];
            internal int Depth;
            internal MatchData Taken = MatchSinkUnset;
        }

        [ThreadStatic]
        private static MatchSinkState t_matchSink;

        public static RubyScope/*!*/ EnterMatchSink(RubyContext/*!*/ context) {
            var state = t_matchSink ?? (t_matchSink = new MatchSinkState());
            int depth = state.Depth;
            var sinks = state.Sinks;
            if (depth == sinks.Length) {
                Array.Resize(ref sinks, depth * 2);
                state.Sinks = sinks;
            }
            var sink = sinks[depth];
            // several runtimes may run on a thread: a sink scope is the runtime's whose methods it is given to
            if (sink == null || sink.RubyContext != context) {
                sinks[depth] = sink = new RubyTopLevelScope(context);
            }
            sink.CurrentMatch = MatchSinkUnset;
            state.Depth = depth + 1;
            return sink;
        }

        public static void LeaveMatchSink() {
            var state = t_matchSink;
            var sink = state.Sinks[--state.Depth];
            state.Taken = sink.CurrentMatch;
            sink.CurrentMatch = null;
        }

        // the call raised: what it stored is no frame's
        public static void AbortMatchSink() {
            var state = t_matchSink;
            state.Sinks[--state.Depth].CurrentMatch = null;
            state.Taken = MatchSinkUnset;
        }

        /// <summary>
        /// The frame's $~ after a call of its light site with RubyCallFlags.HasMatchSink: what the call stored, or
        /// <paramref name="previous"/> if it stored nothing (a method of the program's own, a library method that did not
        /// match: CRuby leaves the caller's $~ then too).
        /// </summary>
        public static MatchData TakeMatchSink(MatchData previous) {
            var state = t_matchSink;
            if (state == null) {
                // the thread has entered no sink: nothing was stored
                return previous;
            }
            var match = state.Taken;
            if (ReferenceEquals(match, MatchSinkUnset)) {
                return previous;
            }
            state.Taken = MatchSinkUnset;
            return match;
        }

        /// <summary>
        /// The scope a light frame that is a match sink makes on demand, given the frame's $~.
        /// </summary>
        public static RubyMethodScope/*!*/ SeedLightScopeMatch(RubyMethodScope/*!*/ scope, MatchData match) {
            if (match != null) {
                scope.CurrentMatch = match;
            }
            return scope;
        }
    }
}
