/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;

namespace IronRuby.Compiler.Ast {

    /// <summary>
    /// A method that passes its rest parameter on as the splat of a call - `def w(*args, &amp;b) = n(*args, &amp;b)' - copies it when
    /// the callee has a rest parameter of its own that takes the whole list: the callee must not share an Array the caller still
    /// holds. Where the caller cannot see the Array again, there is nothing to share it with, and the call site may say so as a
    /// site that made its splat itself does (RubyCallFlags.HasFreshSplat): the callee's rest parameter is the caller's Array
    /// (RubyOps.TakeFreshSplat), one Array a call fewer. TruffleRuby does the same for a rest Array that does not escape.
    ///
    /// <see cref="Find"/> answers the one splat of a method body that may say so, or null. The caller cannot see the Array again
    /// when the splat is the only thing in the method that names the rest parameter (no other read, no assignment, no `defined?'),
    /// and nothing can name it for the method: no block literal in the method (a Proc's #binding reaches the frame), no binding,
    /// eval or local_variable_* call, no zsuper (it passes the parameters on); and when it runs once per call of the method: not
    /// in a loop or a block, and the method has no retry or redo. The callee may keep or change the Array as its own: in Ruby it
    /// is a new one, and here the only other reference is a variable no code reads again.
    /// </summary>
    internal sealed class ForwardedRest : Walker {
        // Off unless IR_FORWARD_REST=1. The gate of round 5 (2026-11-27) reached the rest Array after the call by names this
        // walk cannot see: instance_eval and class_eval of a String, send(:eval, ...), an alias of eval - any call may ask
        // for the frame's scope (RubyOps.LightScopeRequired) - and a TracePoint's binding at :return; each saw what the callee
        // did to its rest parameter, where CRuby's caller keeps its own Array. Sound only where nothing runs in the frame after
        // the call (the method's tail, no ensure) and no TracePoint is on, which this does not check yet.
        private static readonly bool _enabled = Environment.GetEnvironmentVariable("IR_FORWARD_REST") == "1";

        private readonly string/*!*/ _name;
        private readonly int _depth;
        private int _references;
        private int _loops;
        private int _blocks;
        private bool _refused;
        private SplattedArgument _splat;

        private ForwardedRest(LocalVariable/*!*/ rest) {
            _name = rest.Name;
            _depth = rest.DefinitionLexicalDepth;
        }

        internal static SplattedArgument Find(Parameters parameters, Body body) {
            if (!_enabled || parameters == null || body == null) {
                return null;
            }
            var rest = parameters.Unsplat as LocalVariable;
            if (rest == null) {
                return null;
            }
            var finder = new ForwardedRest(rest);
            // the defaults of optional parameters are part of the method too
            foreach (var optional in parameters.Optional) {
                optional.Walk(finder);
            }
            body.Walk(finder);
            return (finder._refused || finder._references != 1) ? null : finder._splat;
        }

        private bool IsRest(LocalVariable/*!*/ node) {
            return node.Name == _name && node.DefinitionLexicalDepth == _depth;
        }

        public override bool Enter(LocalVariable/*!*/ node) {
            if (IsRest(node)) { _references++; }
            return true;
        }

        public override bool Enter(SplattedArgument/*!*/ node) {
            var local = node.Argument as LocalVariable;
            if (local != null && IsRest(local)) {
                if (_loops > 0 || _blocks > 0 || _splat != null) {
                    _refused = true;
                } else {
                    _splat = node;
                }
            }
            return true;
        }

        public override bool Enter(MethodCall/*!*/ node) {
            switch (node.MethodName) {
                case "binding":
                case "eval":
                case "local_variables":
                case "local_variable_get":
                case "local_variable_set":
                case "local_variable_defined?":
                    _refused = true;
                    break;
            }
            return true;
        }

        public override bool Enter(SuperCall/*!*/ node) {
            if (node.HasImplicitArguments) { _refused = true; }
            return true;
        }

        public override bool Enter(RetryStatement/*!*/ node) { _refused = true; return true; }
        public override bool Enter(RedoStatement/*!*/ node) { _refused = true; return true; }

        // a block literal's Proc has #binding, which reaches this frame
        public override bool Enter(BlockDefinition/*!*/ node) { _refused = true; _blocks++; return true; }
        public override void Exit(BlockDefinition/*!*/ node) { _blocks--; }
        public override bool Enter(LambdaDefinition/*!*/ node) { _refused = true; _blocks++; return true; }
        public override void Exit(LambdaDefinition/*!*/ node) { _blocks--; }

        public override bool Enter(WhileLoopExpression/*!*/ node) { _loops++; return true; }
        public override void Exit(WhileLoopExpression/*!*/ node) { _loops--; }
        public override bool Enter(ForLoopExpression/*!*/ node) { _loops++; return true; }
        public override void Exit(ForLoopExpression/*!*/ node) { _loops--; }

        // a method defined inside has variables of its own
        public override bool Enter(MethodDefinition/*!*/ node) { return false; }
    }
}
