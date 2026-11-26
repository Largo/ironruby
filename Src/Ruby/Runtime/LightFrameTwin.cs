/* ****************************************************************************
 *
 * A light frame's scoped twin (gap 16 of Util/perf/LEDGER.md; IR_LIGHT_TWIN).
 *
 * A light frame that makes its scope late - a call whose light site answered
 * RubyOps.LightScopeRequired, `binding', a `return' from a block - pays for the light
 * frame and for the scope: the late scope's arm in the body, CreateMethodScopeLazy, the
 * light frame's exit, about 230 instructions a call more than the same method with its
 * scope made at entry (`s.gsub(/b/, "x")'). The first time a compiled light body makes its
 * scope late, it compiles the same method with its scope (MethodDefinition.TransformBody
 * with scopedTwin), and from then on its compiled code calls that twin at its entry and
 * returns what it returns. The forward is outside the light body's frame record
 * (CompiledFrameExpression), so a backtrace has the twin's frame only; an interpreted light
 * body does not forward, since every interpreted frame is in a backtrace.
 *
 * The twin is one per compiled body, which is per runtime (its declaring scope is baked in).
 * Twin is written once, by the thread that compiled it, and read without a lock: a call
 * that reads it null runs the light body, which is right, only slower.
 *
 * ***************************************************************************/

using System;
using System.Threading;

namespace IronRuby.Runtime {
    public sealed class LightFrameTwin {
        /// <summary>The method compiled with its scope, of the light body's delegate type; null until the first late scope.</summary>
        public Delegate Twin;

        private Func<Delegate> _compile;
        private int _state;
        private readonly string/*!*/ _name;

        /// <summary>IR_LIGHT_TWIN=verbose names each twin compiled on stderr.</summary>
        private static readonly bool Verbose = Environment.GetEnvironmentVariable("IR_LIGHT_TWIN") == "verbose";

        /// <summary>The light body's delegate type, set once its lambda is made (MethodDefinition.ForwardToTwin).</summary>
        internal Type DelegateType;

        internal LightFrameTwin(string/*!*/ name, Func<Delegate>/*!*/ compile) {
            _name = name;
            _compile = compile;
        }

        /// <summary>The light body does not forward (it needs its scope after all, or takes its parameters in an array).</summary>
        internal void Disable() {
            _state = 1;
            _compile = null;
        }

        /// <summary>
        /// The light body is making its scope late: compile the twin, once, if the body is compiled (it has had calls enough for
        /// the light compiler to compile it). A compilation that fails, or gives another delegate type, leaves the method light.
        /// </summary>
        public void ScopeMadeLate(Microsoft.Scripting.Interpreter.InterpretedFrame interpretedFrame) {
            // only a compiled light body forwards: one that is still interpreted has had few calls, and may not have many
            if (interpretedFrame != null || _state != 0 || Interlocked.CompareExchange(ref _state, 1, 0) != 0) {
                return;
            }
            var compile = _compile;
            _compile = null;
            Delegate twin;
            try {
                twin = compile();
            } catch (Exception e) {
                if (Verbose) {
                    Console.Error.WriteLine("[twin] {0}: {1}", _name, e.Message);
                }
                return;
            }
            bool published = twin != null && twin.GetType() == DelegateType;
            if (published) {
                Volatile.Write(ref Twin, twin);
            }
            if (Verbose) {
                Console.Error.WriteLine("[twin] {0}: {1}", _name, published ? "scoped from now on" : "not published");
            }
        }
    }
}
