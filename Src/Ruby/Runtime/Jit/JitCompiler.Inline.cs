/* ****************************************************************************
 *
 * Calls an outlined loop makes to Ruby methods, compiled into the loop: the callee's body, typed by
 * the arguments it is given at this call site, instead of a call.
 *
 *   m(a, b), self.m(a, b)    self's class (the loop's self, or the receiver of the callee being
 *                            inlined) answers m with a Ruby method; guarded once, on entry
 *   o.m(a, b)                o a local whose value the loop can see, a RubyObject whose class answers
 *                            m with a public Ruby method; guarded at the call
 *
 * The method is resolved when the loop is compiled, under the global method version the copy's
 * prologue checks (OsrLoopSite), so a redefinition, an override, an include or a singleton method
 * makes the copy stale rather than wrong. A callee takes plain mandatory parameters only, as many as
 * there are arguments; it may not be one already being inlined here (no recursion), and inlining nests
 * MaxInlineDepth deep at most.
 *
 * Why this is sound is why the loop itself is: the callee's body is emitted by the same rules, so it
 * calls no Ruby code, every write it makes is logged and taken back by a deopt (JitUndoLog), and a
 * deopt in it re-runs the enclosing iteration generically - where the call is a call, with its frame,
 * its backtrace and its TracePoint events. Nothing raises out of typed code (an exception is a deopt
 * first), and an iteration that completed did exactly what the calls would have done.
 *
 * The callee's parameters and locals are typed variables of the inlined frame, keyed apart from the
 * caller's (_framePrefix). Its `return' leaves the frame; the call's value has the type every return and
 * the fall-off value unify to, which a second emission uses when the first, boxed one found a narrower
 * one. A parameter given a local of the caller's whose value the loop can see has that value as its
 * profile (_provenance): an Array is the caller's load-once Array, a Struct's members are read directly.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;
using Range = IronRuby.Builtins.Range;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    internal sealed partial class JitCompiler {
        internal const int MaxInlineDepth = 4;

        private sealed class InlineFrame {
            internal MSA.LabelTarget/*!*/ Label;
            internal JT Expected;
            internal JT Union = JT.None;
            internal int Returns;
        }

        // the callee being emitted, and how deep the inlining is
        private InlineFrame _frame;
        private int _inlineDepth;
        private string/*!*/ _framePrefix = "";
        private int _inlineFrames;
        private List<MethodDeclaration> _inlineStack;

        // the class self has in the code being emitted: the loop's self's (guarded on entry), or the
        // receiver's of the callee being inlined (guarded at the call); null if unknown
        private RubyClass _inlineSelfClass;
        private bool _selfClassGuarded;

        // the value of a callee's parameter, by Key, when its argument was a caller's local the loop can see
        private Dictionary<string, object> _provenance;

        // what self is now (the loop's, or the receiver of the callee being inlined), for its instance variables'
        // values; and the callee's locals it assigns exactly once (EmitArrayLocalAssignment)
        private object _liveSelfOverride;
        private bool _hasLiveSelfOverride;
        private HashSet<string> _singleAssigned;

        private object _liveSelf {
            get { return _hasLiveSelfOverride ? _liveSelfOverride : (_hints != null ? _hints.SelfValue : null); }
        }

        private RubyClass SelfClass {
            get { return (_inlineDepth > 0) ? _inlineSelfClass : (_hints != null ? _hints.SelfClass : null); }
        }

        /// <summary>
        /// A local's tuple slot and what it holds now (see _peekLocal) - or, for a parameter of a callee being
        /// inlined, its variable and the value its argument had; null for any other local of the callee.
        /// </summary>
        private KeyValuePair<MSA.Expression, object>? PeekLocal(LocalVariable/*!*/ local) {
            if (_peekLocal == null) { return null; }
            if (_inlineDepth > 0 || IsInlinedLocal(local)) {
                object live;
                MSA.ParameterExpression p;
                JT t;
                if (_provenance != null && _provenance.TryGetValue(Key(local), out live) &&
                    TryLookup(Key(local), out p, out t) && t == JT.Obj) {
                    return new KeyValuePair<MSA.Expression, object>(p, live);
                }
                return null;
            }
            return _peekLocal(local);
        }

        /// <summary>A callee's parameter with a profile from its argument (see PeekLocal).</summary>
        private bool HasProvenance(LocalVariable/*!*/ local) {
            return _provenance != null && (_inlineDepth > 0 || IsInlinedLocal(local)) && _provenance.ContainsKey(Key(local));
        }

        /// <summary>
        /// `m(args)', `self.m(args)', `o.m(args)' compiled as the body of the Ruby method they call, when the
        /// call can be (see the file comment); false to leave the call to whoever bails on it.
        /// </summary>
        private bool TryEmitInlinedCall(MethodCall/*!*/ node, out MSA.Expression result, out JT type) {
            result = null;
            type = JT.None;
            if (_loop == null || _peekLocal == null || !_refinements.IsEmpty || node.Block != null || _inlineDepth >= MaxInlineDepth) {
                return false;
            }
            var args = node.Arguments;
            int argc = (args == null) ? 0 : args.Expressions.Length;
            for (int i = 0; i < argc; i++) {
                if (args.Expressions[i] is SplattedArgument) { return false; }
            }

            // the receiver and its class
            RubyClass cls;
            bool isSelf = (node.Target == null || node.Target is SelfReference);
            object liveReceiver = null;
            if (isSelf) {
                if (_ivarProfile == null) { return false; }
                cls = SelfClass;
                if (cls == null) { return false; }
            } else {
                var local = node.Target as LocalVariable;
                if (local == null) { return false; }
                var peek = PeekLocal(local);
                liveReceiver = peek.HasValue ? peek.Value.Value as RubyObject : null;
                if (liveReceiver == null) { return false; }
                cls = ((RubyObject)liveReceiver).ImmediateClass;
            }

            // the method
            var resolved = cls.ResolveMethod(node.MethodName, VisibilityContext.AllVisible);
            var info = resolved.Found ? resolved.Info as RubyMethodInfo : null;
            if (info == null || (!isSelf && !info.IsPublic)) { return false; }
            var ast = info.GetSyntaxTree();
            if (ast == null || ast.Body == null) { return false; }
            if (_inlineStack != null && _inlineStack.Contains(ast)) { return false; }
            var parameters = ast.Parameters;
            if (parameters.Optional.Length != 0 || parameters.Unsplat != null || parameters.Block != null ||
                parameters.SuperSplat != null || parameters.Mandatory.Length != argc) {
                return false;
            }
            var signature = parameters.Signature;
            if (signature != null && (signature.TakesKeywords || signature.RefusesKeywords || signature.MaxPositionalCount != argc)) {
                return false;
            }
            var paramLocals = new LocalVariable[argc];
            for (int i = 0; i < argc; i++) {
                paramLocals[i] = parameters.Mandatory[i] as LocalVariable;
                if (paramLocals[i] == null) { return false; }
            }
            // The callee's locals are nil on every call, its variables here are not: none may be read before
            // it is assigned (its parameters are, by the call).
            int calleeDepth = ast.DefinedScope.Depth;
            if (!Osr.DefiniteAssignment.Check(new RExpr[] { ast.Body }, local => local.DefinitionLexicalDepth == calleeDepth, paramLocals)) {
                return false;
            }

            // From here on a failure is a bailout: the call would have been one anyway.
            var temps = new List<MSA.ParameterExpression>();
            var setup = new List<MSA.Expression>();

            MSA.Expression receiverValue = null;
            if (!isSelf) {
                JT rt;
                var recv = Emit(node.Target, out rt);
                if (rt != JT.Obj) { throw Bail("inlined call on a " + rt); }
                var recvTemp = Ast.Variable(typeof(object), "#receiver");
                temps.Add(recvTemp);
                setup.Add(Ast.Assign(recvTemp, recv));
                // what o's class was when the loop was compiled, or a deopt
                _canDeopt = true;
                setup.Add(Ast.Call(JitRuntime.M("GuardImmediateClass"), recvTemp, Ast.Constant(cls)));
                receiverValue = recvTemp;
            } else if (_inlineDepth == 0) {
                // the loop's self is the receiver, and its class is checked once, on entry (JitLoopFacts.SelfClass)
                _selfClassGuarded = true;
            }

            // the arguments, in the caller's frame, left to right
            var argValues = new MSA.ParameterExpression[argc];
            var argTypes = new JT[argc];
            var argLive = new object[argc];
            var argArrays = new KeyValuePair<MSA.ParameterExpression, JT>?[argc];
            for (int i = 0; i < argc; i++) {
                var argNode = args.Expressions[i];
                var e = Emit(argNode, out argTypes[i]);
                e = NumericIVar(argNode, e, ref argTypes[i]);
                if (argTypes[i] == JT.Never || argTypes[i] == JT.None) { throw Bail("inlined call with an argument that jumps away"); }
                argValues[i] = Ast.Variable(ClrType(argTypes[i]), "#arg" + i);
                temps.Add(argValues[i]);
                setup.Add(Ast.Assign(argValues[i], e));
                var argLocal = argNode as LocalVariable;
                if (argLocal != null && argTypes[i] == JT.Obj) {
                    var peek = PeekLocal(argLocal);
                    if (peek.HasValue) { argLive[i] = peek.Value.Value; }
                    if (argLive[i] is RubyArray) { argArrays[i] = ArrayLocal(argLocal); }
                }
            }
            var assigned = AssignedLocals(ast.Body);

            // the callee's frame
            var savedFrame = _frame;
            var savedPrefix = _framePrefix;
            var savedSelf = _self;
            var savedSelfClass = _inlineSelfClass;
            var savedIvarProfile = _ivarProfile;
            var savedAttrProfile = _attrProfile;
            var savedConstantScope = _constantScope;
            var savedBreak = _breakLabel;
            var savedContinue = _continueLabel;
            var savedInlinedBlocks = _inlinedBlocks;
            var savedScopeIndex = _localScopeIndex;
            var savedLiveSelf = _liveSelfOverride;
            var savedHasLiveSelf = _hasLiveSelfOverride;
            var savedSingleAssigned = _singleAssigned;
            var singleAssigned = SingleAssignedLocals(ast.Body);
            var savedSelfClassForFrame = SelfClass;
            string prefix = "#f" + (++_inlineFrames) + ":";
            if (_inlineStack == null) { _inlineStack = new List<MethodDeclaration>(); }

            JT expected = JT.Obj;
            for (int pass = 0; ; pass++) {
                var frame = new InlineFrame { Expected = expected, Label = Ast.Label(ClrType(expected), "#inline-return") };
                MSA.Expression body;
                JT bodyType;
                List<MSA.ParameterExpression> declared;
                var binds = new List<MSA.Expression>();
                _inlineStack.Add(ast);
                _inlineDepth++;
                try {
                    _frame = frame;
                    _framePrefix = prefix;
                    _inlineSelfClass = isSelf ? savedSelfClassForFrame : cls;
                    if (!isSelf) {
                        _self = (MSA.ParameterExpression)receiverValue;
                        var obj = liveReceiver;
                        var objClass = cls;
                        _ivarProfile = name => {
                            object v;
                            if (!_context.TryGetInstanceVariable(obj, name, out v)) { return JT.Obj; }
                            return (v is int) ? JT.Int : (v is long) ? JT.Lng : (v is double) ? JT.Dbl : JT.Obj;
                        };
                        _attrProfile = name => name.Length > 1 && name[name.Length - 1] == '='
                            ? JitRuntime.SelfAttrSite.WriterVariable(objClass, name, false)
                            : JitRuntime.SelfAttrSite.ReaderVariable(objClass, name, false);
                    }
                    _constantScope = info.DeclaringScope;
                    _singleAssigned = singleAssigned;
                    if (!isSelf) {
                        _liveSelfOverride = liveReceiver;
                        _hasLiveSelfOverride = true;
                    }
                    _breakLabel = null;
                    _continueLabel = null;
                    _inlinedBlocks = 0;

                    PushScope();
                    _localScopeIndex = _vars.Count - 1;
                    for (int i = 0; i < argc; i++) {
                        string key = Key(paramLocals[i]);
                        var p = Declare(key, argTypes[i]);
                        binds.Add(Ast.Assign(p, argValues[i]));
                        // a parameter the callee never assigns has its argument's value as its profile
                        if (argLive[i] != null && !assigned.Contains(paramLocals[i].Name)) {
                            if (_provenance == null) { _provenance = new Dictionary<string, object>(); }
                            _provenance[key] = argLive[i];
                            if (argArrays[i].HasValue) {
                                if (_arrayLocals == null) {
                                    _arrayLocals = new Dictionary<string, KeyValuePair<MSA.ParameterExpression, JT>>();
                                    _arrayLoads = new List<KeyValuePair<MSA.ParameterExpression, MSA.Expression>>();
                                    _arrayPeeked = new List<LocalVariable>();
                                }
                                _arrayLocals[key] = argArrays[i].Value;
                            }
                        }
                    }
                    body = Emit(ast.Body, out bodyType);
                    // `def width = @w': the value an instance variable holds now types it, as an operand's would
                    var last = LastStatement(ast.Body);
                    if (last != null) { body = NumericIVar(last, body, ref bodyType); }
                    declared = PopScope();
                } finally {
                    _inlineDepth--;
                    _inlineStack.RemoveAt(_inlineStack.Count - 1);
                    _frame = savedFrame;
                    _framePrefix = savedPrefix;
                    _self = savedSelf;
                    _inlineSelfClass = savedSelfClass;
                    _ivarProfile = savedIvarProfile;
                    _attrProfile = savedAttrProfile;
                    _constantScope = savedConstantScope;
                    _breakLabel = savedBreak;
                    _continueLabel = savedContinue;
                    _inlinedBlocks = savedInlinedBlocks;
                    _localScopeIndex = savedScopeIndex;
                    _liveSelfOverride = savedLiveSelf;
                    _hasLiveSelfOverride = savedHasLiveSelf;
                    _singleAssigned = savedSingleAssigned;
                }

                JT union = (frame.Returns == 0) ? bodyType : Merge(bodyType, frame.Union);
                if (union == JT.Never || union == JT.None) { union = JT.Obj; }
                if (frame.Returns > 0 && union != expected) {
                    // a narrower type than the boxed first emission assumed: emit again under it
                    if (pass > 0) { throw Bail("an inlined method's returns do not settle"); }
                    expected = union;
                    continue;
                }

                var frameBody = new List<MSA.Expression>(binds);
                if (frame.Returns > 0) {
                    frameBody.Add(Ast.Label(frame.Label, Coerce(body, bodyType, expected)));
                    type = expected;
                } else {
                    frameBody.Add(body);
                    type = bodyType;
                }
                var inner = Ast.Block(ClrType(type), declared, frameBody);
                setup.Add(inner);
                result = Ast.Block(ClrType(type), temps, setup);
                return true;
            }
        }

        /// <summary>`return' in a callee being inlined: the value leaves the inlined frame (TryEmitInlinedCall).</summary>
        private MSA.Expression/*!*/ EmitInlineReturn(ReturnStatement/*!*/ node, out JT type) {
            var args = node.Arguments;
            JT vt;
            MSA.Expression value;
            if (args == null || args.Expressions.Length == 0) {
                vt = JT.Obj;
                value = Ast.Constant(null, typeof(object));
            } else if (args.Expressions.Length == 1 && !(args.Expressions[0] is SplattedArgument)) {
                value = Emit(args.Expressions[0], out vt);
                value = NumericIVar(args.Expressions[0], value, ref vt);
            } else {
                throw Bail("return of several values");
            }
            if (vt == JT.Never) {
                type = JT.Never;
                return value;
            }
            var frame = _frame;
            frame.Returns++;
            frame.Union = (frame.Union == JT.None) ? vt : Merge(frame.Union, vt);
            if (Merge(vt, frame.Expected) != frame.Expected) {
                // only in the second emission, which the first one's union typed: it cannot widen
                if (frame.Expected != JT.Obj) { throw Bail("an inlined method's returns do not settle"); }
            }
            type = JT.Never;
            return Ast.Return(frame.Label, Coerce(value, vt, frame.Expected), typeof(object));
        }

        /// <summary>The statement whose value a method body falls off with, if it is a plain one.</summary>
        private static RExpr LastStatement(RExpr/*!*/ body) {
            var b = body as Body;
            if (b != null) {
                if (b.RescueClauses != null || b.ElseStatements != null || b.EnsureStatements != null || b.Statements == null ||
                    b.Statements.Count == 0) {
                    return null;
                }
                return b.Statements.Last;
            }
            return body;
        }

        /// <summary>The names of the locals a method body assigns anywhere (its parameters among them).</summary>
        private static HashSet<string>/*!*/ AssignedLocals(RExpr/*!*/ body) {
            var walker = new AssignedLocalsWalker();
            body.Walk(walker);
            return walker.Names;
        }

        /// <summary>The names of the locals a method body assigns exactly once, by a plain `x = v'.</summary>
        private static HashSet<string>/*!*/ SingleAssignedLocals(RExpr/*!*/ body) {
            var walker = new AssignedLocalsWalker();
            body.Walk(walker);
            var result = new HashSet<string>();
            foreach (var entry in walker.Counts) {
                if (entry.Value == 1 && !walker.Other.Contains(entry.Key)) { result.Add(entry.Key); }
            }
            return result;
        }

        private sealed class AssignedLocalsWalker : Walker {
            internal readonly HashSet<string>/*!*/ Names = new HashSet<string>();
            // plain `x = v' assignments by name, and the names op-assigned or assigned any other way
            internal readonly Dictionary<string, int>/*!*/ Counts = new Dictionary<string, int>();
            internal readonly HashSet<string>/*!*/ Other = new HashSet<string>();

            public override bool Enter(SimpleAssignmentExpression/*!*/ node) {
                var local = node.Left as LocalVariable;
                if (local != null) {
                    Names.Add(local.Name);
                    if (node.Operation == null) {
                        int n;
                        Counts.TryGetValue(local.Name, out n);
                        Counts[local.Name] = n + 1;
                    } else {
                        Other.Add(local.Name);
                    }
                }
                return true;
            }

            public override bool Enter(ParallelAssignmentExpression/*!*/ node) {
                // whatever it assigns: be safe and count every local it names
                foreach (var lhs in node.Left.LeftValues) {
                    var local = lhs as LocalVariable;
                    if (local != null) { Names.Add(local.Name); Other.Add(local.Name); }
                }
                return true;
            }

            // a block of the body, run inline or not, may assign the method's locals too
            public override bool Enter(MethodCall/*!*/ node) {
                var block = node.Block as BlockDefinition;
                if (block != null) {
                    foreach (var statement in block.Body) { statement.Walk(this); }
                }
                return true;
            }
        }
    }

    public static partial class JitRuntime {
        /// <summary>The value of `(a..b).each { }' run inline (JitCompiler.EmitInlinedIterator); a deopt past Int32.</summary>
        public static object MakeIntegerRange(long first, long last, bool exclusive) {
            if (first < Int32.MinValue || first > Int32.MaxValue || last < Int32.MinValue || last > Int32.MaxValue) { throw Deopt(); }
            return new Range((int)first, (int)last, exclusive);
        }

        /// <summary>A receiver of an inlined call (JitCompiler.TryEmitInlinedCall) whose class is no longer the one it was inlined for.</summary>
        public static void GuardImmediateClass(object value, RubyClass/*!*/ cls) {
            var obj = value as RubyObject;
            if (obj == null || !ReferenceEquals(obj.ImmediateClass, cls)) { throw Deopt(); }
        }

        /// <summary>The entry check of an outlined loop with calls on self inlined: self's class is the one they were resolved in.</summary>
        public static bool SelfClassIs(RubyContext/*!*/ context, object self, RubyClass/*!*/ cls) {
            var obj = self as RubyObject;
            return ReferenceEquals(obj != null ? obj.ImmediateClass : context.GetImmediateClassOf(self), cls);
        }
    }
}
