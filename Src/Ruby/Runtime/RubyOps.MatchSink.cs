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
    /// MethodDefinition.IsMatchSink): a scope of its own that holds nothing but $~ - its sink - which the frame takes at its
    /// first such call from the thread's free sinks (TakeMatchSink), gives those calls as their scope, and gives back when it
    /// returns (ReturnMatchSink, which clears it). The library method stores $~ in it; a scope the frame makes later starts with
    /// it (SeedLightScopeMatch) - for a TracePoint's binding, say. So $~ is the frame's, as in CRuby; nothing shared holds a
    /// match; and a call costs no more than the light call it is, the frame two reads of a thread static.
    /// </summary>
    public static partial class RubyOps {
        private const int MaxFreeMatchSinks = 8;

        // A thread's sinks not taken by a frame. A list, not a stack: a frame that never returns (a Fiber left suspended) only
        // takes one out of it.
        private sealed class MatchSinkPool {
            internal readonly RubyTopLevelScope[] Free = new RubyTopLevelScope[MaxFreeMatchSinks];
            internal int Count;
        }

        [ThreadStatic]
        private static MatchSinkPool t_matchSinks;

        /// <summary>
        /// A sink for a light frame of the runtime of <paramref name="declaringScope"/>, the frame's declaring scope.
        /// </summary>
        public static RubyScope/*!*/ TakeMatchSink(RubyScope/*!*/ declaringScope) {
            var context = declaringScope.RubyContext;
            var pool = t_matchSinks;
            if (pool != null && pool.Count > 0) {
                int i = --pool.Count;
                var sink = pool.Free[i];
                pool.Free[i] = null;
                // several runtimes may run on a thread: a sink is the runtime's whose methods it is given to
                if (sink.RubyContext == context) {
                    return sink;
                }
            }
            return new RubyTopLevelScope(context);
        }

        /// <summary>
        /// Called as the frame that took <paramref name="sink"/> returns: its $~ is no one's any more.
        /// </summary>
        public static void ReturnMatchSink(RubyScope/*!*/ sink) {
            var top = (RubyTopLevelScope)sink;
            top.CurrentMatch = null;
            var pool = t_matchSinks ?? (t_matchSinks = new MatchSinkPool());
            if (pool.Count < MaxFreeMatchSinks) {
                pool.Free[pool.Count++] = top;
            }
        }

        /// <summary>
        /// The scope a light frame that is a match sink makes on demand, given the $~ of its sink (null if it has taken none).
        /// </summary>
        public static RubyMethodScope/*!*/ SeedLightScopeMatch(RubyMethodScope/*!*/ scope, RubyScope sink) {
            if (sink != null) {
                scope.CurrentMatch = ((RubyTopLevelScope)sink).CurrentMatch;
            }
            return scope;
        }
    }
}
