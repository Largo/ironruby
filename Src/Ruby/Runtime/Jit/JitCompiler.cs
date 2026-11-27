/* ****************************************************************************
 *
 * -X:JIT - an experimental ZJIT-style method JIT for IronRuby.
 *
 * Takes a Ruby method AST plus the types the profiling tier observed for its
 * parameters, and builds a *typed* LambdaExpression: Integer and Float values live in
 * CLR int/long/double locals rather than boxed objects, no RubyMethodScope or MutableTuple
 * is allocated, a call to the method itself becomes a direct call to the specialized
 * entry, and there are no dynamic call sites at all.
 *
 * Everything the compiler cannot prove makes it bail out (JitBailout), in which case
 * the method keeps running on the generic body.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using Ast = System.Linq.Expressions.Expression;
    using RExpr = IronRuby.Compiler.Ast.Expression;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;
    // the AST node, not the runtime's variable (IronRuby.Runtime.GlobalVariable)
    using AstGlobalVariable = IronRuby.Compiler.Ast.GlobalVariable;

    /// <summary>The type lattice. Deliberately tiny: this is a prototype.</summary>
    internal enum JT {
        None = 0,   // no value yet
        Never,      // bottom: the expression never produces a value (it jumped away). Unifies
                    // with anything, and is coerced by evaluating it and then falling into an
                    // unreachable default of whatever type the context wanted.
        Int,        // CLR int, an Integer that fits in an Int32
        Lng,        // CLR long, an Integer that fits in an Int64 but not an Int32 - and, inside
                    // the specialized region, any integer at all: integer arithmetic happens in
                    // long, and a result narrows back to Int only where it has to.
        Dbl,        // CLR double, a Float
        Bool,       // CLR bool, TrueClass/FalseClass
        Obj,        // boxed - anything, moved around but never operated on
    }

    internal sealed class JitBailout : Exception {
        internal static readonly JitBailout Instance = new JitBailout();
        private JitBailout() { }
    }

    /// <summary>The result of a successful specialization.</summary>
    internal sealed class JitCode {
        internal MSA.LambdaExpression/*!*/ Lambda;
        internal StrongBox<Delegate>/*!*/ SelfCell;
        internal JT ReturnType;
        internal JT[]/*!*/ ParameterTypes;
        internal bool CanDeopt;
        internal bool EmittedSelfCall;
        // The body folded constants (EmitConstant): the entry guards on the constant version as well.
        internal bool UsesConstants;
        // What it folded, with what the bodies it calls directly folded: what its entry looks up again when the
        // constant version has moved (JitStub.RevalidateConstants). Null if it folded none.
        internal JitFoldedConstant[] FoldedConstants;
        // The body writes Hash elements (EmitIndexWrite): the entry brackets the call with the undo log.
        internal bool UsesUndoLog;
        // The body may become part of a caller's body (JitCompiler.TryInlineCall): it has no effect, no call
        // that runs Ruby code, no loop, no self-call, and takes exactly its mandatory parameters.
        internal bool Inlinable;
        // The class of self that a call this body inlined was resolved on: the entry pins self's class to it.
        internal RubyClass InlinedSelfClass;
        // A call inlined on a parameter checks the one class the profile saw the parameter hold (TryInlineCall).
        internal bool InlinedOnArgumentClass;

        // -X:JITCalls. What a body that calls this one directly takes over from it (JitCompiler.EmitDirectCall):
        // Deopts and IvarWrite are the two halves of CanDeopt, Pure and EffectNotOnSelf are the compiler's
        // _pure and _effectNotOnSelf, and each includes what the bodies this one calls brought.
        internal bool Deopts, IvarWrite, Pure, EffectNotOnSelf;
        // The body, or one it calls, has a `while' loop, and so a safe point that runs Ruby code
        // (JitRuntime.MethodLoopSafePointSlow).
        internal bool HasLoop;
        // What the body's loops' safe points check: the stub fills in the versions it compiled the body under.
        internal JitLoopGuard LoopGuard;
        // The body writes Hash or Array elements, logged or not, or ends in a construction, which runs an
        // initialize: neither is called directly (JitStub.BindCallee).
        internal bool WritesElements, Construction;
        // The ordinary globals the body reads and the ones it assigns, with those of the bodies it calls
        // directly; null when there are none. A caller's are checked against them (JitCompiler.TakeGlobals).
        internal HashSet<GlobalVariable> GlobalsRead, GlobalsWritten;
        // The body calls the typed bodies of other methods: the entry pins the receiver class they
        // were resolved from.
        internal bool DirectCalls;
        // -X:JITResume: the body falls back to an operation's call at so many sites after its first effect, takes the
        // block after its parameters and makes its frame (JitResume.cs); null when it has none.
        internal JitResumeInfo Resume;
        internal int FallbackSites;
        // The body was compiled for a call without a block: its block parameter is nil, which `&k' passes on as no block
        // (JitStub.BlockParameterOnlyPassed), or it answered `block_given?' with false. The entry takes it only when the
        // block is null.
        internal bool AssumesNoBlock;
    }

    /// <summary>
    /// -X:JITCalls: another method of the receiver as a typed body calls it (JitStub.BindCallee).
    /// </summary>
    internal sealed class JitCallee {
        internal JitCode/*!*/ Code;
        // What the caller invokes with (self, typed arguments): the typed body's delegate, as a
        // constant. -X:JITMethods emits a call of the method behind it (JitMethodEmitter.Lifter).
        internal MSA.Expression/*!*/ Target;
    }

    internal sealed partial class JitCompiler {
        internal const int MaxNodes = 2000;

        private readonly MethodDeclaration _ast;
        private readonly RubyContext/*!*/ _context;

        // The method's block parameter, when it only passes it on (JitStub.BlockParameterOnlyPassed): the body is compiled
        // for a call without a block, where `&k' passes none (JitScreen.PassesNoBlock). Null otherwise.
        private readonly LocalVariable _passedBlock;
        // The body answered `block_given?' with false, for a call without a block (EmitBlockGiven).
        private bool _blockGivenFalse;
        private readonly int _scopeDepth;

        // The refinements active where the code being compiled was written: the calls it binds itself
        // (operators, the self-call) are resolved as its call sites would resolve them.
        private RefinementActivation/*!*/ _refinements = RefinementActivation.Empty;

        // Locals, in nested scopes. Ruby has no block scoping, but a local first assigned
        // inside a conditionally executed region is nil on the path that skipped it, which a
        // typed int/double slot cannot represent. Declaring such a local in the region's own
        // DLR block makes any read from outside fail the lookup and bail out, which is exactly
        // the condition under which the two scopings are indistinguishable.
        private readonly List<Dictionary<string, MSA.ParameterExpression>>/*!*/ _vars =
            new List<Dictionary<string, MSA.ParameterExpression>>();
        private readonly List<Dictionary<string, JT>>/*!*/ _types = new List<Dictionary<string, JT>>();
        private readonly List<List<MSA.ParameterExpression>>/*!*/ _declared = new List<List<MSA.ParameterExpression>>();

        private readonly MSA.ParameterExpression/*!*/ _self;
        private readonly JT[]/*!*/ _paramTypes;
        private readonly string/*!*/[]/*!*/ _paramNames;

        // Self-recursion goes through this cell, filled in once the lambda is compiled.
        private readonly StrongBox<Delegate>/*!*/ _selfCell = new StrongBox<Delegate>();
        private JT _selfReturn;
        private Type _selfDelegateType;

        // _pure: nothing in the body is observable part way through, so a deopt may restart it.
        // _canDeopt: the body contains an operation that can signal a deopt.
        private bool _pure = true;
        private bool _canDeoptValue;
        private bool _canDeopt {
            get { return _canDeoptValue; }
            set {
                _canDeoptValue = value;
                if (value && !_pure) {
                    if (!_deoptAfterEffect && JitRuntime.Verbose) { _firstDeoptAfterEffect = _emitting ?? "?"; }
                    _deoptAfterEffect = true;
                }
                if (value && _unloggedStoreBefore) { _deoptAfterUnloggedStore = true; }
            }
        }
        // A deopting operation was emitted after something observable. Emission follows evaluation
        // order in a body with no loop and no self-call (a branch's effect emitted before the other
        // branch's deopt only errs towards declining), so in such a body a deopt that is not after
        // an effect still finds nothing done and may restart it. A loop's next iteration and a
        // nested self-call run deopts emitted before effects that have already happened.
        private bool _deoptAfterEffect;
        // IR_JIT_VERBOSE=1: the node being emitted when _deoptAfterEffect was first set, for the `declined' line.
        private string _firstDeoptAfterEffect;
        private string _emitting;
        private bool _hasLoop;
        // The body writes an instance variable. A write to a frozen object deopts (JitRuntime.SetIVar)
        // so that the generic body raises the FrozenError from a real frame; it is always the first
        // write that does, before anything observable, so the restart is safe whatever _pure says.
        private bool _ivarWrite;
        // Something observable has happened that was not a write to self: an attribute written on another
        // object, an object constructed. After it a write to self's instance variables is no longer "the
        // first write, before anything observable" (see _ivarWrite), so its deopt on a frozen self would
        // restart a body that has already done something: such a write counts as a deopt after an effect.
        private bool _effectNotOnSelf;
        // The body ends in a construction (EmitConstruction), which runs an initialize: arbitrary Ruby code.
        private bool _construction;
        private bool _selfCall;
        // The body has a `raise' (EmitRaise).
        private bool _raises;
        // How many operands of conditions were values tested for truth rather than booleans (EmitCondition);
        // EmitNot compares it across an `&&'/`||' operand.
        private int _conditionValueLeaves;
        private readonly HashSet<MSA.Expression>/*!*/ _selfCalls = new HashSet<MSA.Expression>();
        private int _nodes;

        // -X:JITCalls: finds the typed body of the method a name resolves to on the receiver, for a
        // call with so many arguments; null where a body binds no such call (no switch, a loop, a
        // receiver class the profile could not pin). See EmitDirectCall.
        private Func<string, int, JitCallee> _callees;
        private bool _directCalls;
        // a body called directly has a loop (JitCode.HasLoop)
        private bool _calleeLoop;

        // Explicit `return'. The label is created on the first one met, typed by the return type
        // the round is compiling under (_selfReturn); _returnUnion is what the values actually
        // seen so far unify to, which is how the round after this one is typed. _retype is set
        // when a return value cannot flow into the current guess at all, so the round is
        // abandoned and re-run against the wider type rather than rejected.
        private MSA.LabelTarget _returnLabel;
        private JT _returnUnion = JT.None;
        private JT _retype = JT.None;
        private bool _firstRound;

        /// <summary>The reason the last bailout gave, for IR_JIT_VERBOSE=1 and the census (JitCensus). Not on a fast path.</summary>
        private string _bailReason;

        private JitBailout/*!*/ Bail(string/*!*/ reason) {
            if ((JitRuntime.Verbose || Osr.OsrLoopSite.Verbose || JitCensus.Current != null) && _bailReason == null) { _bailReason = reason; }
            return JitBailout.Instance;
        }

        // A method with keyword parameters: its keyword locals are typed parameters too, after the
        // positional ones, and the body starts after the prologue that binds them (JitKeywords).
        private readonly JitKeywords _keywords;

        // A body for calls that pass exactly this many of the optional parameters (JitStub's per-count
        // bodies): those are typed parameters, and the body starts by evaluating the defaults of the
        // rest, in order, as assignments to their locals. -1: every optional parameter is a typed
        // parameter, the entry having filled in the omitted ones' literal defaults.
        private readonly int _supplied = -1;

        private JitCompiler(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes, JitKeywords keywords, int supplied,
            RubyScope constantScope) {
            _ast = ast;
            _context = context;
            _constantScope = constantScope;
            if (JitStub.BlockParameterOnlyPassed(ast)) { _passedBlock = ast.Parameters.Block; }
            _paramTypes = paramTypes;
            _keywords = keywords;
            _supplied = (keywords == null) ? supplied : -1;
            _scopeDepth = ast.DefinedScope.Depth;
            _self = Ast.Parameter(typeof(object), "#self");
            PushScope();

            // The typed body's parameters are the generic delegate's: every mandatory one, then
            // the optional ones, whose defaults the entry has already filled in (JitStub).
            var mandatory = ast.Parameters.Mandatory;
            // The typed body's parameters are the generic delegate's positional ones - every mandatory
            // one, then the optional ones, whose defaults the entry has already filled in (JitStub) -
            // or, for a method with keywords, the mandatory ones and then its keyword locals.
            var optional = ast.Parameters.Optional;
            // the positionals of a keyword method: the mandatory ones, then any optional ones before the keywords
            int positional = mandatory.Length + ((keywords != null) ? keywords.OptionalPositionals : 0);
            int expected = (keywords != null) ? positional + keywords.Count : mandatory.Length + ((_supplied >= 0) ? _supplied : optional.Length);
            _paramNames = new string[paramTypes.Length];
            _paramExprs = new MSA.ParameterExpression[paramTypes.Length];
            _resolvedTypes = (JT[])paramTypes.Clone();
            if (paramTypes.Length != expected) { throw JitBailout.Instance; }
            for (int i = 0; i < paramTypes.Length; i++) {
                string name;
                if (i < positional || keywords == null) {
                    var lv = (i < mandatory.Length) ? mandatory[i] as LocalVariable : optional[i - mandatory.Length].Left as LocalVariable;
                    if (lv == null) { throw JitBailout.Instance; }
                    name = lv.Name;
                } else {
                    name = keywords.LocalNames[i - positional];
                }
                if (_vars[0].ContainsKey(name)) { throw JitBailout.Instance; }
                _paramNames[i] = name;
                if (keywords != null && i >= positional && keywords.HasDefaultExpr(i - positional)) {
                    // the boxed value or RubyOps.DefaultArgument; the local is bound by EmitKeywordDefaults
                    _paramExprs[i] = Ast.Parameter(typeof(object), "?" + name);
                    continue;
                }
                var p = Ast.Parameter(ClrType(paramTypes[i]), name);
                _paramExprs[i] = p;
                _vars[0].Add(name, p);
                _types[0].Add(name, paramTypes[i]);
            }
        }

        // The typed body's lambda parameters, and the types its entry unboxes and checks them to: a
        // keyword whose default the body evaluates is a boxed parameter, its type the local's (which
        // EmitKeywordDefaults decides when the profile has none).
        private readonly MSA.ParameterExpression[] _paramExprs;
        private JT[] _resolvedTypes;

        // ---- loop mode -------------------------------------------------------------------
        // -X:OSR points the same emitter at a `while' loop rather than a method body. The
        // difference is where the locals live: in a method they are parameters and CLR locals,
        // in a loop they are slots of the enclosing scope's MutableTuple, bound on demand as
        // the emitter meets them and loaded into typed CLR locals by the entry JitLoopCompiler
        // builds around this. Everything between those two edges is the same code.
        private readonly WhileLoopExpression _loop;
        private readonly Func<LocalVariable, JT, JitLoopSlot> _bindSlot;
        private readonly List<JitLoopSlot> _slots;
        private MSA.LabelTarget _breakLabel, _continueLabel;
        // The innermost loop's result, made by its first `break v' (EmitBreak); null while it has none.
        private MSA.ParameterExpression _breakValue;

        // Loop mode: what an instance variable of the loop's self holds right now, as a type
        // (see NumericIVar). Null when the loop was entered without its self, which is then
        // unbound and the loop may not touch self or an instance variable.
        // Method mode: the same, of the receiver of the call that turned the method hot (ReceiverHints.SelfSample).
        private Func<string, JT> _ivarProfile;

        // Loop mode: the instance variable a method name reads on the loop's self if that method
        // is an attr_reader there, else null (see EmitCall). Null when the loop has no self.
        private readonly Func<string, string> _attrProfile;

        // The attribute reads EmitCall turned into instance variable reads, and the variable each
        // reads, so that NumericIVar can type them the way it types the variable itself.
        private Dictionary<MethodCall, string> _attrReads;

        // Loop mode: a local's tuple slot - the expression that reads it, and what it holds right
        // now - without binding it to a typed slot (see EmitCall). The locals read this way, which
        // the loop then must not write: a write would go to a typed slot this read does not see.
        private readonly Func<LocalVariable, KeyValuePair<MSA.Expression, object>?> _peekLocal;
        private List<LocalVariable> _peeked;

        private JitCompiler(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context, int scopeDepth,
            Func<LocalVariable, JT, JitLoopSlot>/*!*/ bindSlot, MSA.ParameterExpression self, Func<string, JT> ivarProfile,
            Func<string, string> attrProfile, Func<LocalVariable, KeyValuePair<MSA.Expression, object>?> peekLocal,
            RubyScope constantScope) {
            _ast = null;
            _loop = loop;
            _context = context;
            _constantScope = constantScope;
            _scopeDepth = scopeDepth;
            _bindSlot = bindSlot;
            _slots = new List<JitLoopSlot>();
            _paramTypes = new JT[0];
            _paramNames = new string[0];
            _self = self ?? Ast.Parameter(typeof(object), "#self");
            _ivarProfile = (self != null) ? ivarProfile : null;
            _attrProfile = (self != null) ? attrProfile : null;
            _peekLocal = peekLocal;
            PushScope();
        }

        /// <summary>
        /// Emits a typed body for one `while' loop. The slots it bound - the scope locals it
        /// touched, and the type each of them held when the loop turned hot - come back in
        /// <paramref name="slots"/>; the caller loads and stores them. <paramref name="self"/> is
        /// the variable the caller loads the loop's self into (null if it has none), and
        /// <paramref name="ivarProfile"/> what self's instance variables hold right now.
        /// </summary>
        internal static MSA.Expression TryCompileLoop(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context,
            int scopeDepth, Func<LocalVariable, JT, JitLoopSlot>/*!*/ bindSlot, out List<JitLoopSlot> slots, out bool canDeopt) {
            JitLoopFacts facts;
            return TryCompileLoop(loop, context, scopeDepth, bindSlot, null, null, null, null, null, null, RefinementActivation.Empty,
                out slots, out canDeopt, out facts);
        }

        /// <summary>What JitLoopCompiler builds around an outlined loop besides its slots.</summary>
        internal struct JitLoopFacts {
            // the body folded constants: the entry guards on the constant version
            internal bool UsesConstants;
            // what it folded, for the entry to look up again when the constant version has moved
            // (JitRuntime.LoopConstantsCurrent); null if it folded none, or some it did not record
            internal JitFoldedConstant[] FoldedConstants;
            // the body writes Hash elements: the iteration's writes go into the undo log, which the
            // body commits at the top of each iteration (UndoLog) from the mark the entry sets (UndoMark)
            internal MSA.ParameterExpression UndoLog;
            internal MSA.ParameterExpression UndoMark;
            // the Arrays the body indexes directly (ArrayLocal), each a variable the entry loads by the
            // expression paired with it - null when the local does not hold a plain Array; or null
            internal List<KeyValuePair<MSA.ParameterExpression, MSA.Expression>> Arrays;
            // what the loop's safe points check the versions against (EmitLoopSafePoint)
            internal JitLoopGuard Guard;
        }

        internal static MSA.Expression TryCompileLoop(WhileLoopExpression/*!*/ loop, RubyContext/*!*/ context,
            int scopeDepth, Func<LocalVariable, JT, JitLoopSlot>/*!*/ bindSlot, MSA.ParameterExpression self,
            Func<string, JT> ivarProfile, Func<string, string> attrProfile,
            Func<LocalVariable, KeyValuePair<MSA.Expression, object>?> peekLocal, RubyScope constantScope, ReceiverHints hints,
            RefinementActivation/*!*/ refinements, out List<JitLoopSlot> slots, out bool canDeopt, out JitLoopFacts facts, long epoch = 0) {

            slots = null;
            canDeopt = false;
            facts = default(JitLoopFacts);
            JitCompiler c;
            MSA.Expression body;
            for (bool arrayLocals = true; ; arrayLocals = false) {
                c = new JitCompiler(loop, context, scopeDepth, bindSlot, self, ivarProfile, attrProfile, peekLocal, constantScope);
                c._compileEpoch = epoch;
                c._hints = hints;
                c._refinements = refinements;
                c._noArrayLocals = !arrayLocals;
                try {
                    JT type;
                    body = c.EmitWhile(loop, out type);
                } catch (JitBailout) {
                    if (Osr.OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   emitter: {0}", c._bailReason ?? "unsupported construct"); }
                    return null;
                }
                // An Array loaded once on entry (ArrayLocal) is read from its tuple slot, so the loop must
                // not also bind it to a slot, which it may write: if it does, the Arrays go through the
                // intrinsics instead.
                bool conflict = false;
                if (c._arrayPeeked != null) {
                    foreach (var local in c._arrayPeeked) {
                        if (c._vars[0].ContainsKey(c.Key(local))) { conflict = true; break; }
                    }
                }
                if (!conflict) { break; }
                if (Osr.OsrLoopSite.Verbose) { Console.Error.WriteLine("[osr]   emitter: an indexed local is also a slot; again without load-once Arrays"); }
            }
            // A deopt restores the locals to what they were at the top of the iteration and
            // hands the loop back, so the iteration is re-run: it must not have been observable.
            if (c._canDeopt && !c._pure) { return null; }
            // An attribute receiver read straight from its tuple slot must not also be a typed
            // slot the loop writes (EmitCall).
            if (c._peeked != null) {
                foreach (var local in c._peeked) {
                    if (c._vars[0].ContainsKey(c.Key(local))) { return null; }
                }
            }
            slots = c._slots;
            facts.UsesConstants = c._usesConstants;
            facts.FoldedConstants = (c._usesConstants && c._foldedConstants != null && !c._foldedIncomplete) ? c._foldedConstants.ToArray() : null;
            facts.UndoLog = c._undoLog;
            facts.UndoMark = c._undoMark;
            facts.Arrays = c._arrayLoads;
            facts.Guard = c._loopGuard;
            // the back-edge safe point hands the loop back through the deopt
            // catch, so the catch is always needed - but it fires at the top of an iteration,
            // before anything observable, so it does not make an impure body unspecializable.
            canDeopt = true;
            return body;
        }

        /// <summary>The key a local is looked up under: names repeat across lexical depths.</summary>
        private string/*!*/ Key(LocalVariable/*!*/ local) {
            return (_loop == null) ? local.Name : local.Name + "@" + local.DefinitionLexicalDepth;
        }

        /// <summary>
        /// In loop mode a local the emitter has not met yet is bound to its tuple slot, typed
        /// by the value it holds right now - which is the only profile a loop that is already
        /// running needs.
        /// </summary>
        private bool BindLoopLocal(LocalVariable/*!*/ local, JT hint, out MSA.ParameterExpression p, out JT type) {
            var slot = _bindSlot(local, hint);
            if (slot == null) { p = null; type = JT.None; throw JitBailout.Instance; }
            _slots.Add(slot);
            _slotByVariable.Add(slot.Variable, slot);
            _vars[0].Add(Key(local), slot.Variable);
            _types[0].Add(Key(local), slot.Type);
            p = slot.Variable;
            type = slot.Type;
            return true;
        }

        private readonly Dictionary<MSA.ParameterExpression, JitLoopSlot> _slotByVariable =
            new Dictionary<MSA.ParameterExpression, JitLoopSlot>();

        /// <summary>
        /// The load of a slot the loop writes before it reads - a temporary of its own. What
        /// the slot held when the loop turned hot says nothing about what it holds on a later
        /// entry (the scope may be brand new, and the slot nil), so such a slot has no type
        /// guard; instead a flag says whether this run has written it. Reading it before that
        /// means the Ruby code is reading nil, which the typed body has no value for: deopt,
        /// and let the loop itself raise whatever it raises.
        /// </summary>
        private MSA.Expression/*!*/ LoadLoopSlot(MSA.ParameterExpression/*!*/ p) {
            JitLoopSlot slot;
            if (_loop != null && _slotByVariable.TryGetValue(p, out slot) && slot.Assigned != null) {
                _canDeopt = true;
                return Ast.Block(p.Type,
                    Ast.IfThen(Ast.Not(slot.Assigned), Ast.Throw(Ast.Call(JitRuntime.M("OsrUnsetSlot")))),
                    p
                );
            }
            return p;
        }

        /// <summary>The store into such a slot, which is what puts its flag up.</summary>
        private MSA.Expression/*!*/ StoreLoopSlot(MSA.ParameterExpression/*!*/ p, MSA.Expression/*!*/ rhs) {
            JitLoopSlot slot;
            if (_slotByVariable.TryGetValue(p, out slot) && slot.Assigned != null) {
                // The flag goes up after the right hand side has run, not before: `x += 1'
                // reads x, and that read has to still see the slot as unwritten.
                return Ast.Block(p.Type, Ast.Assign(p, rhs), Ast.Assign(slot.Assigned, Ast.Constant(true)), p);
            }
            return Ast.Assign(p, rhs);
        }

        private void PushScope() {
            _vars.Add(new Dictionary<string, MSA.ParameterExpression>());
            _types.Add(new Dictionary<string, JT>());
            _declared.Add(new List<MSA.ParameterExpression>());
        }

        private List<MSA.ParameterExpression>/*!*/ PopScope() {
            int last = _vars.Count - 1;
            var declared = _declared[last];
            _vars.RemoveAt(last);
            _types.RemoveAt(last);
            _declared.RemoveAt(last);
            return declared;
        }

        private bool TryLookup(string/*!*/ name, out MSA.ParameterExpression p, out JT type) {
            for (int i = _vars.Count - 1; i >= 0; i--) {
                if (_vars[i].TryGetValue(name, out p)) { type = _types[i][name]; return true; }
            }
            p = null;
            type = JT.None;
            return false;
        }

        private MSA.ParameterExpression/*!*/ Declare(string/*!*/ name, JT type) {
            var p = Ast.Parameter(ClrType(type), name);
            int last = _vars.Count - 1;
            _vars[last].Add(name, p);
            _types[last].Add(name, type);
            _declared[last].Add(p);
            return p;
        }

        /// <summary>Emits a conditionally executed statement list in its own local scope.</summary>
        private MSA.Expression/*!*/ EmitRegion(Statements statements, out JT type) {
            PushScope();
            var e = EmitStatements(statements, out type);
            var declared = PopScope();
            return declared.Count > 0 ? Ast.Block(ClrType(type), declared, e) : e;
        }

        private MSA.Expression/*!*/ EmitRegion(RExpr/*!*/ node, out JT type) {
            PushScope();
            var e = Emit(node, out type);
            var declared = PopScope();
            return declared.Count > 0 ? Ast.Block(ClrType(type), declared, e) : e;
        }

        private static Type/*!*/ ClrType(JT t) {
            switch (t) {
                case JT.Int: return typeof(int);
                case JT.Lng: return typeof(long);
                case JT.Dbl: return typeof(double);
                case JT.Bool: return typeof(bool);
                default: return typeof(object);
            }
        }

        /// <summary>
        /// Compiles a typed body for the given parameter types. Returns null when the method
        /// cannot be specialized.
        /// </summary>
        internal static JitCode TryCompile(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes) {
            return TryCompile(ast, context, paramTypes, null, RefinementActivation.Empty);
        }

        internal static JitCode TryCompile(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes, JitKeywords keywords,
            RefinementActivation/*!*/ refinements) {
            return TryCompile(ast, context, paramTypes, keywords, refinements, -1);
        }

        /// <summary>
        /// <paramref name="supplied"/>: compile the body for calls that pass this many of the optional
        /// parameters, evaluating the other defaults itself; -1 for the body that takes them all.
        /// </summary>
        internal static JitCode TryCompile(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes, JitKeywords keywords,
            RefinementActivation/*!*/ refinements, int supplied) {
            return TryCompile(ast, context, paramTypes, keywords, refinements, supplied, null, null);
        }

        /// <summary>
        /// <paramref name="constantScope"/>: the scope the method's constants resolve in (its declaring
        /// scope); null if the body may not fold constants.
        /// </summary>
        internal static JitCode TryCompile(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes, JitKeywords keywords,
            RefinementActivation/*!*/ refinements, int supplied, RubyScope constantScope, ReceiverHints hints) {
            return TryCompile(ast, context, paramTypes, keywords, refinements, supplied, constantScope, hints, null);
        }

        /// <summary>
        /// <paramref name="callees"/>: -X:JITCalls, the typed bodies of the receiver's other methods
        /// (JitStub.BindCallee); null if the body may not call one.
        /// </summary>
        internal static JitCode TryCompile(MethodDeclaration/*!*/ ast, RubyContext/*!*/ context, JT[]/*!*/ paramTypes, JitKeywords keywords,
            RefinementActivation/*!*/ refinements, int supplied, RubyScope constantScope, ReceiverHints hints,
            Func<string, int, JitCallee> callees, long epoch = 0) {
            // The self-recursive call needs the method's return type before the body has been
            // typed. Guess, compile, and re-run once if the guess was wrong.
            // Rounds: each retry widens the guess (Int -> Lng -> Dbl/Obj), so the lattice bounds
            // how many there can be; a guess that does not settle is rejected.
            if (JitRuntime.Verbose) { LastDecline = null; }
            JT guess = JT.None;
            bool arrayParameters = true;
            bool unloggedWrites = true;
            bool unloggedEarlyWrites = true;
            // a loop has a safe point, which a body holding writes back cannot have (JitCompiler.Sink.cs): not tried.
            // Nor with keyword or optional parameters: their prologue and their defaults are emitted outside the
            // block that declares the held values (EmitBody), and a default may write (`def m(x = (@a = 5))'), so
            // such a body failed to compile and was declined where it used to be specialized.
            bool sinkStores = !HasWhileLoop(ast) && keywords == null && ast.Parameters.Optional.Length == 0;
            HashSet<string> sinkWide = null;
            bool initialIVarsOff = false;
            for (int round = 0; round < 5; round++) {
                var c = new JitCompiler(ast, context, paramTypes, keywords, supplied, constantScope);
                c._compileEpoch = epoch;
                c._hints = hints;
                c._refinements = refinements;
                c._callees = callees;
                c._resume = (hints != null && keywords == null && supplied < 0 && paramTypes.Length == 0) ? hints.Resume : null;
                c._noArrayLocals = !arrayParameters;
                if (hints != null && hints.SelfSample != null) { c._ivarProfile = IVarProfile(context, hints.SelfSample); }
                c._noUnloggedWrites = !unloggedWrites;
                c._noUnloggedEarlyWrites = !unloggedEarlyWrites;
                c._sinkStores = sinkStores;
                c._sinkWide = sinkWide;
                c._noInitialIVars = initialIVarsOff;
                c._firstRound = (guess == JT.None);
                c._selfReturn = c._firstRound ? JT.Int : guess;
                c._selfDelegateType = DelegateType(LambdaParameterTypes(paramTypes, keywords, ast), c._selfReturn);
                if (c._selfDelegateType == null) { return Declined("too many parameters"); }

                JT actual;
                MSA.Expression body;
                try {
                    body = c.EmitBody(out actual);
                } catch (JitBailout) {
                    // a body that held writes back and bailed is tried again storing them where they are
                    if (c._sunk != null && sinkStores) {
                        if (JitRuntime.Verbose) { Console.Error.WriteLine("[jit] stores not held back in {0}: it bailed out ({1})", ast.Name, c._bailReason ?? "unsupported construct"); }
                        sinkStores = false; round--; continue;
                    }
                    // A `return' whose value does not fit the type this round assumed is not a
                    // rejection, it is a re-run against the type that does fit.
                    if (c._retype != JT.None && c._retype != c._selfReturn) { guess = c._retype; continue; }
                    if (JitRuntime.Verbose) {
                        Console.Error.WriteLine("[jit] rejected {0} arity {1}: {2}", ast.Name, paramTypes.Length,
                            c._bailReason ?? "unsupported construct");
                    }
                    var census = JitCensus.Current;
                    if (census != null) { census.BailReason = c._bailReason ?? "unsupported construct"; }
                    return null;
                }

                // A folded constant - a value, or an arm left out under one - holds for as long as the entry finds it
                // unchanged. A loop runs on past the entry, and CRuby's next read sees a reassignment another thread
                // made meanwhile: a constant folded in a loop of the body's own, or after one, is looked up again at the
                // loop's safe point when the code epoch moves (MethodLoopSafePointSlow), and if it changed the body
                // deopts there. So the body is one that may deopt, which only a pure body may be (below: one with an
                // effect is declined, as a loop that can deopt in a body with an effect always is). A callee's loop
                // looks only at the callee's constants, so a body that folds one after calling such a callee stays
                // generic. (Gap 29: such a body that had nothing else to deopt for went on with the old value.)
                if (c._foldedAfterCalleeLoop) { return Declined("a constant folded after a call of a callee with a loop"); }
                if (c._foldedInLoop) { c._canDeopt = true; }

                // Writes held back that the body cannot keep (JitCompiler.Sink.cs): compile it storing them where they are.
                if (c._sunk != null && c._sinkObstacle != null) {
                    // an Integer variable written in both representations: another round holding it as a long
                    if (c._sinkWiden != null) {
                        if (JitRuntime.Verbose) { Console.Error.WriteLine("[jit] held as a long in {0}: {1}, written as an Int and as a Lng", ast.Name, String.Join(" ", c._sinkWiden)); }
                        if (sinkWide == null) { sinkWide = new HashSet<string>(); }
                        sinkWide.UnionWith(c._sinkWiden);
                        round--;
                        continue;
                    }
                    if (JitRuntime.Verbose) { Console.Error.WriteLine("[jit] stores not held back in {0}: {1}", ast.Name, c._sinkObstacle); }
                    sinkStores = false;
                    round--;
                    continue;
                }

                // An Array parameter loaded once (ArrayParameter) is one the body does not assign, and
                // the load deopts, which only a pure body may: if not, compile it without.
                if (c._arrayParameters != null && ((c._canDeopt && !c._pure) || Assigns(body, c._arrayParameters))) {
                    arrayParameters = false;
                    round--;
                    continue;
                }

                // A self-call runs the body nested in this one, under this one's undo log: a deopt here
                // after it returns re-runs it, so the store it ends with has to be logged after all.
                if (c._unloggedWrite && c._selfCall) {
                    unloggedWrites = false;
                    round--;
                    continue;
                }

                // An element store before the last statement went unlogged on the promise that nothing after it
                // deopts (EmitIndexWrite): if something does, a loop could run a deopt emitted before it again, or an
                // instance variable write could deopt on a frozen self (SetIVar),
                // compile it logged.
                if (c._unloggedStoreBefore && (c._deoptAfterUnloggedStore || c._hasLoop || c._ivarWrite)) {
                    unloggedEarlyWrites = false;
                    round--;
                    continue;
                }

                // The method's value is the value it falls off with, unified with every value an
                // explicit `return' hands back.
                JT result = (c._returnUnion == JT.None) ? actual : Merge(actual, c._returnUnion);
                // Every path ends in a `raise' (EmitRaise): the typed body could only ever deopt.
                if (result == JT.Never && c._raises) { return Declined("every path raises"); }
                if (result != c._selfReturn) {
                    if (result == guess) { return Declined("the return type did not settle"); }
                    guess = result;
                    continue;
                }

                // The return label carries every explicit `return'; the body falls into it.
                if (actual != result || c._returnLabel != null) {
                    body = c.Coerce(body, actual, result);
                }
                if (c._returnLabel != null) {
                    body = Ast.Label(c._returnLabel, body);
                }
                actual = result;

                // A body that can deopt mid-flight is only safe to restart if nothing it has
                // already done is observable (see _deoptAfterEffect).
                if (c._canDeopt && !c._pure && (c._deoptAfterEffect || c._hasLoop || c._selfCall)) {
                    // the opening run of writes made at once is an effect the deopt comes after: try holding them back
                    if (c._initialIVars && sinkStores && !initialIVarsOff) {
                        initialIVarsOff = true;
                        round--;
                        continue;
                    }
                    return Declined(c._deoptAfterEffect ? "a deopt after an effect (" + c._firstDeoptAfterEffect + ")" :
                        c._hasLoop ? "a loop in a body that can deopt and has an effect" : "a self-call in a body that can deopt and has an effect");
                }
                // The construction is the last thing its own invocation does, but not the last thing the body does
                // when the body calls itself: the outer invocation goes on after the inner one's initialize has run,
                // which may have redefined an operator the body has inlined. The self-call does not check the
                // method version again, so such a body stays generic.
                if (c._construction && c._selfCall) { return Declined("a construction in a self-recursive body"); }

                // A loop of a body called directly has a safe point that runs what is waiting where it
                // is; this body's own deopt instead once it writes logged elements (MethodLoopSafePointSlow).
                if (c._hashWrite && c._calleeLoop) { return Declined("logged element writes and a callee with a loop"); }

                // -X:JITResume: a body with fallbacks is a light frame (WrapResumeBody), whose value is an object
                if (c._fallbackSites > 0) {
                    string obstacle = c.ResumeObstacle();
                    if (obstacle != null) { return Declined("fallbacks after an effect in a body with " + obstacle); }
                    body = c.WrapResumeBody(body, actual);
                    actual = JT.Obj;
                }

                // the stack check a method's scope would make (#128), where the method calls itself
                if (c._selfCall) {
                    body = new StackCheckPlacer(c._selfCalls).Visit(body);
                    body = KeepOutOfTailPosition(body, c._self);
                }

                var parameters = new List<MSA.ParameterExpression>(paramTypes.Length + 1);
                parameters.Add(c._self);
                var functionScope = new List<MSA.ParameterExpression>(c._declared[0]);
                for (int i = 0; i < c._paramNames.Length; i++) {
                    var p = c._paramExprs[i];
                    parameters.Add(p);
                    functionScope.Remove(p);
                }
                // the block, last (JitStub.BuildEntry)
                if (c._fallbackSites > 0) { parameters.Add(c._blockVariable); }
                if (c._undoLog != null) {
                    // the element writes are logged where the entry's mark says (JitStub.WithUndoLog)
                    c._safePointDeopts.Value = true;
                    functionScope.Add(c._undoLog);
                    body = Ast.Block(body.Type, Ast.Assign(c._undoLog, Ast.Property(null, typeof(JitUndoLog).GetProperty("Current"))), body);
                }
                if (c._arrayLoads != null) {
                    // the Array parameters, loaded once
                    var loads = new List<MSA.Expression>();
                    foreach (var load in c._arrayLoads) {
                        functionScope.Add(load.Key);
                        loads.Add(Ast.Assign(load.Key, load.Value));
                    }
                    loads.Add(body);
                    body = Ast.Block(body.Type, loads);
                }
                if (functionScope.Count > 0) {
                    body = Ast.Block(functionScope, body);
                }
                // A body that can deopt and has a loop is pure (or it was declined above), and one that logs its
                // element writes takes them back: either may deopt at its loop's safe point when a method it was
                // compiled under is redefined while it runs, and the generic body restarts the call. Not one that
                // cannot deopt otherwise: its entry has no handler, nor do the callers that call it directly.
                c._loopGuard.MayDeopt = c._canDeopt || c._safePointDeopts.Value;
                return new JitCode {
                    Lambda = Ast.Lambda((c._fallbackSites > 0) ? typeof(Func<object, Proc, object>) : c._selfDelegateType, body, "jit$" + ast.Name, parameters),
                    SelfCell = c._selfCell,
                    ReturnType = actual,
                    ParameterTypes = c._resolvedTypes,
                    // (a body with fallbacks counts its entries for OnDeopt, which they are counted by)
                    CanDeopt = c._canDeopt || c._ivarWrite || c._fallbackSites > 0,
                    EmittedSelfCall = c._selfCall,
                    UsesConstants = c._usesConstants,
                    FoldedConstants = (c._usesConstants && c._foldedConstants != null) ? c._foldedConstants.ToArray() : null,
                    UsesUndoLog = c._hashWrite,
                    Deopts = c._canDeopt,
                    IvarWrite = c._ivarWrite,
                    Pure = c._pure,
                    LoopGuard = c._loopGuard,
                    EffectNotOnSelf = c._effectNotOnSelf,
                    HasLoop = c._hasLoop || c._calleeLoop,
                    WritesElements = c._hashWrite || c._unloggedWrite,
                    Construction = c._construction,
                    GlobalsRead = c._globalsRead,
                    GlobalsWritten = c._globalsWritten,
                    DirectCalls = c._directCalls,
                    Inlinable = c._pure && !c._selfCall && !c._directCalls && !c._hasLoop && !c._construction && !c._ivarWrite && !c._usesConstants &&
                        !c._hashWrite && c._undoLog == null && c._arrayLoads == null && keywords == null && supplied < 0 &&
                        ast.Parameters.Optional.Length == 0 && ast.Parameters.Unsplat == null && ast.Parameters.Block == null &&
                        ast.Parameters.Mandatory.Length == paramTypes.Length,
                    InlinedSelfClass = c._inlinedSelfClass,
                    InlinedOnArgumentClass = c._inlinedOnArgumentClass,
                    Resume = (c._fallbackSites > 0) ? c._resume : null,
                    FallbackSites = c._fallbackSites,
                    AssumesNoBlock = c._passedBlock != null || c._blockGivenFalse,
                };
            }
            return Declined("the return type did not settle");
        }

        /// <summary>
        /// Why the last TryCompile on this thread returned null without a bailout, for the `declined' line of
        /// IR_JIT_VERBOSE=1 (JitStub.GiveUp); null when it did not. Only written under the switch, and only
        /// read by the stub that called TryCompile, on the same thread, right after.
        /// </summary>
        [ThreadStatic]
        internal static string LastDecline;

        private static JitCode Declined(string/*!*/ reason) {
            if (JitRuntime.Verbose) { LastDecline = reason; }
            return null;
        }

        /// <summary>The CLR shape of the typed body's parameters: a keyword whose default it evaluates is boxed.</summary>
        private static JT[]/*!*/ LambdaParameterTypes(JT[]/*!*/ paramTypes, JitKeywords keywords, MethodDeclaration/*!*/ ast) {
            if (keywords == null || keywords.DefaultExprs == null) { return paramTypes; }
            var result = (JT[])paramTypes.Clone();
            int first = ast.Parameters.Mandatory.Length + keywords.OptionalPositionals;
            for (int j = 0; j < keywords.Count && first + j < result.Length; j++) {
                if (keywords.HasDefaultExpr(j)) { result[first + j] = JT.Obj; }
            }
            return result;
        }

        /// <summary>Func&lt;object self, T1..Tn, TRet&gt;.</summary>
        private static Type DelegateType(JT[]/*!*/ ps, JT ret) {
            var types = new Type[ps.Length + 2];
            types[0] = typeof(object);
            for (int i = 0; i < ps.Length; i++) { types[i + 1] = ClrType(ps[i]); }
            types[ps.Length + 1] = ClrType(ret);
            switch (ps.Length) {
                case 0: return typeof(Func<,>).MakeGenericType(types);
                case 1: return typeof(Func<,,>).MakeGenericType(types);
                case 2: return typeof(Func<,,,>).MakeGenericType(types);
                case 3: return typeof(Func<,,,,>).MakeGenericType(types);
                case 4: return typeof(Func<,,,,,>).MakeGenericType(types);
                case 5: return typeof(Func<,,,,,,>).MakeGenericType(types);
                case 6: return typeof(Func<,,,,,,,>).MakeGenericType(types);
                // positionals plus keywords (JitStub.MaxArity - 1 + JitKeywords.MaxKeywords)
                default: return (ps.Length <= 14) ? Ast.GetFuncType(types) : null;
            }
        }

        // ---- emission ----------------------------------------------------------------------

        private MSA.Expression/*!*/ EmitBody(out JT type) {
            var body = _ast.Body;
            if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                throw JitBailout.Instance;
            }
            if (_keywords != null) {
                // the keyword prologue is what the entry did; the parameters hold its result
                var bind = EmitKeywordDefaults();
                var statements = EmitStatements(body.Statements, _keywords.PrologueLength, out type);
                if (bind != null) {
                    bind.Add(statements);
                    statements = Ast.Block(ClrType(type), bind);
                }
                return statements;
            }
            var defaults = EmitOmittedDefaults();
            var result = EmitInitialIVars(body.Statements, out type) ?? EmitStatements(body.Statements, out type);
            result = FinishSunkStores(result, ref type);
            if (defaults != null) {
                defaults.Add(result);
                result = Ast.Block(ClrType(type), defaults);
            }
            return result;
        }

        /// <summary>
        /// A per-count body (_supplied): the defaults of the optional parameters the call left out,
        /// left to right, each an assignment to its local - which is what the generic prologue does,
        /// except that it first sets the local to nil, which only a default reading its own or a later
        /// parameter could see; such a read finds no local here and bails.
        /// </summary>
        private List<MSA.Expression> EmitOmittedDefaults() {
            if (_supplied < 0) { return null; }
            var optional = _ast.Parameters.Optional;
            if (_supplied >= optional.Length) { return null; }
            var parts = new List<MSA.Expression>(optional.Length - _supplied + 1);
            for (int i = _supplied; i < optional.Length; i++) {
                JT ignored;
                parts.Add(EmitAssignment(optional[i], out ignored));
            }
            return parts;
        }

        // Set while a keyword default is emitted: it may not assign a local (one bound after it, say).
        private bool _inKeywordDefault;

        /// <summary>
        /// The keywords whose default the body evaluates, in declaration order: each local is the value
        /// the call passed, or - RubyOps.DefaultArgument - its default. Its type is what the profile saw
        /// passed (which the entry checks), or the default's own type if nothing was ever passed.
        /// </summary>
        private List<MSA.Expression> EmitKeywordDefaults() {
            if (_keywords.DefaultExprs == null) { return null; }
            int first = _ast.Parameters.Mandatory.Length + _keywords.OptionalPositionals;
            var parts = new List<MSA.Expression>();
            var defaultArgument = Ast.Field(null, typeof(RubyOps).GetField("DefaultArgument"));
            for (int j = 0; j < _keywords.Count; j++) {
                if (!_keywords.HasDefaultExpr(j)) { continue; }
                int i = first + j;
                _inKeywordDefault = true;
                JT dt;
                var d = Emit(_keywords.DefaultExprs[j], out dt);
                _inKeywordDefault = false;
                if (dt == JT.None || dt == JT.Never) { throw Bail("keyword default without a value"); }

                JT pt = _paramTypes[i];
                JT lt = (pt == JT.None) ? dt : (pt == dt) ? pt : (IsInteger(pt) && IsInteger(dt)) ? JT.Lng : JT.Obj;
                if (lt == JT.Bool || lt == JT.None) { lt = JT.Obj; }
                _resolvedTypes[i] = lt;

                var p = _paramExprs[i];
                MSA.Expression passed;
                switch (lt) {
                    case JT.Int: passed = Ast.Unbox(p, typeof(int)); break;
                    case JT.Dbl: passed = Ast.Unbox(p, typeof(double)); break;
                    case JT.Lng: passed = Ast.Call(JitRuntime.M("ToLong"), p); break;
                    default: passed = p; break;
                }
                var local = Declare(_paramNames[i], lt);
                parts.Add(Ast.Assign(local, Ast.Condition(Ast.ReferenceEqual(p, defaultArgument), Coerce(d, dt, lt), passed, ClrType(lt))));
            }
            return parts;
        }

        /// <summary>An omitted optional parameter of a per-count body: a local its default was assigned to.</summary>
        private bool IsOmittedOptional(string/*!*/ name) {
            if (_supplied < 0) { return false; }
            var optional = _ast.Parameters.Optional;
            for (int i = _supplied; i < optional.Length; i++) {
                if (((LocalVariable)optional[i].Left).Name == name) { return true; }
            }
            return false;
        }

        // ---- an initializer's opening run of `@x = v' --------------------------------------
        // `def initialize(a, b) @a = a; @b = b; @n = 0 ...': a fresh object gets every one of these
        // variables at once - one slot array, already in its final shape, published with one CAS -
        // rather than one shape transition (and interlocked operation) per variable. Only for a run
        // of writes of distinct variables whose values have no effect and read no instance variable
        // (a parameter, a literal, self), so that doing all the stores together is the same as doing
        // them one by one. Any object that is not a fresh RubyObject takes the writes one by one.

        private const int MinInitialIVars = 2;

        // The run was written at once (_initialIVars); a round without it (_noInitialIVars) holds the writes back
        // instead, when sinking is tried: som.rb's Vector#remove_all, `@first_idx = 0; @last_idx = 0' and then a
        // `@storage.size' that may deopt, was declined as a deopt after an effect, since the run is an effect.
        private bool _initialIVars, _noInitialIVars;

        private MSA.Expression EmitInitialIVars(Statements statements, out JT type) {
            type = JT.None;
            if (_loop != null || statements == null || _noInitialIVars) { return null; }

            var run = new List<SimpleAssignmentExpression>();
            var names = new List<string>();
            foreach (var statement in statements) {
                var assign = statement as SimpleAssignmentExpression;
                var ivar = (assign != null && assign.Operation == null) ? assign.Left as InstanceVariable : null;
                if (ivar == null || names.Contains(ivar.Name) || !IsInitialIVarValue(assign.Right)) { break; }
                run.Add(assign);
                names.Add(ivar.Name);
            }
            if (run.Count < MinInitialIVars) { return null; }

            var site = JitRuntime.InitialIVarsSite.TryCreate(names.ToArray());
            if (site == null) { return null; }
            _initialIVars = true;

            var temps = new MSA.ParameterExpression[run.Count];
            var parts = new List<MSA.Expression>();
            var stores = new List<MSA.Expression>();
            var oneByOne = new List<MSA.Expression>();
            var slots = Ast.Variable(typeof(object[]), "#slots");
            for (int i = 0; i < run.Count; i++) {
                ++_nodes;
                JT vt;
                var v = Emit(run[i].Right, out vt);
                temps[i] = Ast.Variable(typeof(object), "#iv" + i);
                parts.Add(Ast.Assign(temps[i], Coerce(v, vt, JT.Obj)));
                stores.Add(Ast.Assign(Ast.ArrayAccess(slots, Ast.Constant(i + 1)), temps[i]));
                oneByOne.Add(Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, temps[i], Ast.Constant(new InstanceVariableSite(names[i]))));
            }
            if (_effectNotOnSelf) { _canDeopt = true; }
            _pure = false;
            _ivarWrite = true;

            stores.Add(Ast.Call(JitRuntime.M("CommitInitialIVars"), _self, slots));
            parts.Add(Ast.Assign(slots, Ast.Call(JitRuntime.M("BeginInitialIVars"), _self, Ast.Constant(site))));
            parts.Add(Ast.IfThen(
                Ast.Not(Ast.AndAlso(Ast.NotEqual(slots, Ast.Constant(null, typeof(object[]))), Ast.Block(typeof(bool), stores))),
                Ast.Block(typeof(void), oneByOne)
            ));

            int rest = statements.Count - run.Count;
            if (rest == 0) {
                // the value of the last assignment is the method's
                type = JT.Obj;
                parts.Add(temps[run.Count - 1]);
            } else {
                JT t = JT.Obj;
                for (int k = run.Count; k < statements.Count; k++) {
                    parts.Add(EmitTopStatement(statements, k, out t));
                }
                type = t;
            }

            var variables = new List<MSA.ParameterExpression>(temps);
            variables.Add(slots);
            return Ast.Block(ClrType(type), variables, parts);
        }

        /// <summary>A value an opening run of instance variable writes may have: see EmitInitialIVars.</summary>
        private bool IsInitialIVarValue(RExpr/*!*/ value) {
            if (value is SelfReference) { return true; }
            var literal = value as Literal;
            if (literal != null) {
                object v = literal.Value;
                return v == null || v is bool || v is int || v is long || v is double;
            }
            // Nothing before the run assigns a local but the omitted optionals' defaults, so a parameter
            // still holds its argument, and an omitted optional its default.
            var local = value as LocalVariable;
            return local != null && local.DefinitionLexicalDepth == _scopeDepth &&
                (Array.IndexOf(_paramNames, local.Name) >= 0 || IsOmittedOptional(local.Name));
        }

        private MSA.Expression/*!*/ EmitStatements(Statements/*!*/ statements, int start, out JT type) {
            int count = statements.Count - start;
            if (count <= 0) {
                type = JT.Obj;
                return Ast.Constant(null, typeof(object));
            }
            var parts = new List<MSA.Expression>(count);
            type = JT.None;
            for (int i = start; i < statements.Count; i++) {
                parts.Add(EmitTopStatement(statements, i, out type));
            }
            return (parts.Count == 1) ? parts[0] : Ast.Block(ClrType(type), parts);
        }

        private MSA.Expression/*!*/ EmitStatements(Statements statements, out JT type) {
            if (statements == null || statements.Count == 0) {
                type = JT.Obj;
                return Ast.Constant(null, typeof(object));
            }
            if (_resume != null && _ast != null && statements == _ast.Body.Statements) {
                // the method's own statements, which may carry :line hooks (EmitTopStatement)
                return EmitStatements(statements, 0, out type);
            }
            if (statements.Count == 1) {
                return Emit(statements.First, out type);
            }
            var parts = new List<MSA.Expression>();
            foreach (var s in statements.AllButLast) {
                JT ignored;
                parts.Add(Emit(s, out ignored));
            }
            parts.Add(Emit(statements.Last, out type));
            return Ast.Block(ClrType(type), parts);
        }

        /// <summary>
        /// The node dispatch. ADDING A CASE HERE MEANS ADDING ONE TO <see cref="JitScreen"/> at
        /// the bottom of this file: the screen decides, before a method is ever wrapped, whether
        /// this method could accept its body, and a node kind it has not been told about is a
        /// method the JIT quietly stops speeding up. IR_JIT_VERBOSE=1 prints the histogram of
        /// node kinds the screen rejected, so the omission shows up there.
        /// </summary>
        private MSA.Expression/*!*/ Emit(RExpr/*!*/ node, out JT type) {
            MSA.Expression result;
            if (!JitRuntime.Verbose) {
                result = EmitNode(node, out type);
            } else {
                // IR_JIT_VERBOSE=1: the node a deopt after an effect is emitted for (_firstDeoptAfterEffect)
                string outer = _emitting;
                _emitting = Describe(node);
                try {
                    result = EmitNode(node, out type);
                } finally {
                    _emitting = outer;
                }
            }
            // -X:JITResume: a node whose code comes after a fallback speculates on nothing or guards itself (JitCompiler.Resume.cs)
            if (_fallbackSites > 0 && !IsTailNode(node)) { throw Bail("after a fallback: " + Describe(node)); }
            return result;
        }

        private MSA.Expression/*!*/ EmitNode(RExpr/*!*/ node, out JT type) {
            if (++_nodes > MaxNodes) { throw Bail("too many nodes"); }
            // Deep enough to run this thread out of stack: the method stays interpreted (see StackGuard).
            if (!StackGuard.IsSufficient) { throw Bail("nested too deeply for the stack"); }

            var literal = node as Literal;
            if (literal != null) { return EmitLiteral(literal, out type); }

            // `:name': the Symbol the generic body would load, the same object on every evaluation
            var symbol = node as SymbolLiteral;
            if (symbol != null) {
                var name = symbol.Value as string;
                if (name == null) { throw Bail("symbol literal"); }
                type = JT.Obj;
                return Ast.Constant(_context.CreateSymbol(name, symbol.Encoding), typeof(object));
            }

            var str = node as StringLiteral;
            if (str != null) { return EmitStringLiteral(str, out type); }

            var interpolation = node as StringConstructor;
            if (interpolation != null) { return EmitInterpolation(interpolation, out type); }

            var local = node as LocalVariable;
            if (local != null) { return EmitLocalRead(local, out type); }

            var assign = node as SimpleAssignmentExpression;
            if (assign != null) { return EmitAssignment(assign, out type); }

            var call = node as MethodCall;
            if (call != null) { return EmitCall(call, out type); }

            var cond = node as ConditionalExpression;
            if (cond != null) { return EmitIfLike(cond.Condition, cond.TrueExpression, cond.FalseExpression, out type); }

            var ifExpr = node as IfExpression;
            if (ifExpr != null) { return EmitIf(ifExpr, out type); }

            var unless = node as UnlessExpression;
            if (unless != null) { return EmitUnless(unless, out type); }

            var not = node as NotExpression;
            if (not != null) { return EmitNot(not.Expression, out type); }

            var and = node as AndExpression;
            if (and != null) { return EmitAndOr(and.Left, and.Right, true, out type); }

            var or = node as OrExpression;
            if (or != null) { return EmitAndOr(or.Left, or.Right, false, out type); }

            var ivar = node as InstanceVariable;
            if (ivar != null) {
                if (_loop != null && _ivarProfile == null) { throw Bail("instance variable in a loop entered without self"); }
                var sunkRead = TrySunkIVarRead(ivar.Name, out type);
                if (sunkRead != null) { return sunkRead; }
                type = JT.Obj;
                return Ast.Call(JitRuntime.M("GetIVar"), Ast.Constant(_context), _self, Ast.Constant(new InstanceVariableSite(ivar.Name)));
            }

            var gvar = node as AstGlobalVariable;
            if (gvar != null) { return EmitGlobalRead(gvar, out type); }

            var self = node as SelfReference;
            if (self != null) {
                if (_loop != null && _ivarProfile == null) { throw Bail("self in a loop entered without it"); }
                type = JT.Obj;
                return _self;
            }

            var loop = node as WhileLoopExpression;
            if (loop != null) { return EmitWhile(loop, out type); }

            // A jump of the innermost `while' being emitted (EmitWhile). No block body is emitted in a method's typed body
            // (EmitCall bails on a call with one), so every break or next met here is the loop's.
            if (_breakLabel != null) {
                var brk = node as BreakStatement;
                if (brk != null) { return EmitBreak(brk, out type); }
                var nxt = node as NextStatement;
                if (nxt != null) {
                    if (nxt.Arguments != null) { throw JitBailout.Instance; }
                    type = JT.Obj;
                    return Ast.Block(typeof(object), Ast.Continue(_continueLabel), Ast.Constant(null, typeof(object)));
                }
            }

            var ret = node as ReturnStatement;
            if (ret != null) { return EmitReturn(ret, out type); }

            var arrayLiteral = node as ArrayConstructor;
            if (arrayLiteral != null) { return EmitArrayLiteral(arrayLiteral, out type); }

            var hashLiteral = node as HashConstructor;
            if (hashLiteral != null) { return EmitHashLiteral(hashLiteral, out type); }

            var parallel = node as ParallelAssignmentExpression;
            if (parallel != null) { return EmitParallelAssignment(parallel, out type); }

            // a value EmitParallelAssignment has already evaluated, standing for its right-hand side
            var emitted = node as EmittedValue;
            if (emitted != null) { type = emitted.Type; return emitted.Value; }

            var body = node as Body;
            if (body != null) {
                if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                    throw JitBailout.Instance;
                }
                return EmitStatements(body.Statements, out type);
            }

            var constant = node as ConstantVariable;
            if (constant != null) { return EmitConstant(constant, out type); }

            var caseExpr = node as CaseExpression;
            if (caseExpr != null) { return EmitCase(caseExpr, out type); }

            // `(a; b)', and what `h[k] op= v' is lowered to when k is not a literal
            var sequence = node as BlockExpression;
            if (sequence != null) { return EmitStatements(sequence.Statements, out type); }

            // `o.x op= v' in an outlined loop (JitCompiler.Objects.cs)
            var member = node as MemberAssignmentExpression;
            if (member != null && _loop != null) { return EmitMemberOpAssign(member, out type); }

            throw Bail("node: " + node.GetType().Name);
        }

        // IR_JIT_VERBOSE=1: a node as the `declined' line names it.
        private static string/*!*/ Describe(RExpr/*!*/ node) {
            var call = node as MethodCall;
            if (call != null) { return "call " + call.MethodName + " at line " + node.Location.Start.Line; }
            var assign = node as SimpleAssignmentExpression;
            if (assign != null && assign.Left is AttributeAccess) {
                return "attribute write " + ((AttributeAccess)assign.Left).Name + " at line " + node.Location.Start.Line;
            }
            return node.GetType().Name + " at line " + node.Location.Start.Line;
        }

        /// <summary>
        /// `$x': an ordinary global variable - one defined, and neither special ($~, $_ and the like are
        /// the scope's, others have a getter of their own) nor aliased to one - read through a cached
        /// site of its own, as the generic body reads it but without the scope (JitRuntime.GetGlobal).
        /// Reading it does nothing observable; a read that finds the name denoting another variable than the
        /// one it was compiled for (an alias made since) deopts.
        /// </summary>
        private MSA.Expression/*!*/ EmitGlobalRead(AstGlobalVariable/*!*/ node, out JT type) {
            var variable = OrdinaryGlobal(node);
            if (_globalsWritten != null && _globalsWritten.Contains(variable)) { throw Bail("global variable $" + node.Name + " read and written"); }
            if (_globalsRead == null) { _globalsRead = new HashSet<GlobalVariable>(); }
            _globalsRead.Add(variable);
            _canDeopt = true;
            type = JT.Obj;
            return Ast.Call(JitRuntime.M("GetGlobal"), Ast.Constant(_context), Ast.Constant(new GlobalVariableSite(node.Name)),
                Ast.Constant(variable, typeof(object)));
        }

        /// <summary>
        /// `$x = v' of an ordinary global (see EmitGlobalRead), which the body does not also read: a deopt that
        /// restarts the body (or the iteration) then only stores the same value again - the value the restarted
        /// code computes from the same state - so the store leaves the body restartable, as a read does. The
        /// helper never raises: a variable that is no longer ordinary, one trace_var watches, or a non-main
        /// Ractor deopts before storing, and the generic code assigns it as it always does.
        /// </summary>
        private MSA.Expression/*!*/ EmitGlobalWrite(AstGlobalVariable/*!*/ node, RExpr/*!*/ right, out JT type) {
            var variable = OrdinaryGlobal(node);
            if (_globalsRead != null && _globalsRead.Contains(variable)) { throw Bail("global variable $" + node.Name + " read and written"); }
            JT vt;
            var v = Emit(right, out vt);
            if (_globalsRead != null && _globalsRead.Contains(variable)) { throw Bail("global variable $" + node.Name + " read and written"); }
            CheckGlobalStoreBesideNest("global variable $" + node.Name + " stored");
            if (_globalsWritten == null) { _globalsWritten = new HashSet<GlobalVariable>(); }
            _globalsWritten.Add(variable);
            _canDeopt = true;
            type = JT.Obj;
            return Ast.Call(JitRuntime.M("SetGlobal"), Ast.Constant(_context), Ast.Constant(new GlobalVariableSite(node.Name)),
                Ast.Constant(variable, typeof(object)), Coerce(v, vt, JT.Obj));
        }

        // The ordinary global the name denotes now; any other bails. The helpers deopt once the name denotes
        // another variable (an alias made since), so two names cannot become one unseen.
        private GlobalVariable/*!*/ OrdinaryGlobal(AstGlobalVariable/*!*/ node) {
            GlobalVariable variable;
            if (!_context.TryGetGlobalVariable(node.Name, out variable) || !(variable is GlobalVariableInfo)) {
                throw Bail("global variable $" + node.Name);
            }
            return variable;
        }

        private HashSet<GlobalVariable> _globalsRead, _globalsWritten;

        /// <summary>
        /// -X:JITCalls: the globals of a body this one calls directly are this body's, for the rule of
        /// EmitGlobalRead and EmitGlobalWrite: a deopt after the call restarts this body, and a global one
        /// of the two reads and the other assigns - or one callee reads and another assigns - would then
        /// be read as the first run left it and assigned from that (`store($g + 1)' counting twice).
        /// </summary>
        private void TakeGlobals(JitCode/*!*/ code, string/*!*/ name) {
            if (code.GlobalsRead != null) {
                foreach (var variable in code.GlobalsRead) {
                    if (_globalsWritten != null && _globalsWritten.Contains(variable)) {
                        throw Bail("call: " + name + " reads a global variable written here");
                    }
                }
            }
            if (code.GlobalsWritten != null) {
                foreach (var variable in code.GlobalsWritten) {
                    if (_globalsRead != null && _globalsRead.Contains(variable)) {
                        throw Bail("call: " + name + " writes a global variable read here");
                    }
                }
            }
            if (code.GlobalsRead != null) {
                if (_globalsRead == null) { _globalsRead = new HashSet<GlobalVariable>(); }
                _globalsRead.UnionWith(code.GlobalsRead);
            }
            if (code.GlobalsWritten != null) {
                if (code.GlobalsWritten.Count > 0) { CheckGlobalStoreBesideNest("call: " + name + " stores a global variable"); }
                if (_globalsWritten == null) { _globalsWritten = new HashSet<GlobalVariable>(); }
                _globalsWritten.UnionWith(code.GlobalsWritten);
            }
        }

        /// <summary>
        /// `[a, b]': a new Array of boxed values, made by the helper the generic body calls. Building
        /// one has no effect anything could observe, so it is no obstacle to a deopt. A splat (or
        /// anything else Emit does not know) among the elements bails as usual.
        /// </summary>
        internal const int MaxArrayLiteral = 5;

        private MSA.Expression/*!*/ EmitArrayLiteral(ArrayConstructor/*!*/ node, out JT type) {
            var items = node.Arguments.Expressions;
            if (items.Length > MaxArrayLiteral) { throw Bail("array literal of " + items.Length); }
            var values = new List<MSA.Expression>(items.Length);
            foreach (var item in items) {
                JT t;
                var e = Emit(item, out t);
                values.Add(Coerce(e, t, JT.Obj));
            }
            type = JT.Obj;
            return IronRuby.Compiler.Methods.MakeArrayOpCall(values);
        }

        /// <summary>
        /// `{}', `{a: x}', `{"k" => v, 1 => w}': the Hash the generic body makes (HashConstructor.TransformReadCore), keys and
        /// values evaluated in order, built by the same code (JitRuntime.NewHash, NewSymbolHash1..3). Only with keys the
        /// runtime's comparer hashes itself (IsPlainLiteralKeyed): then building it runs no Ruby code - no #hash, no #eql? -
        /// and has no effect anything could observe, so it is no obstacle to a deopt, no way into deep recursion and no
        /// call that needs a stack check. A String key is copied and frozen by the helper, as the generic body's is.
        /// </summary>
        private MSA.Expression/*!*/ EmitHashLiteral(HashConstructor/*!*/ node, out JT type) {
            var maplets = node.Maplets;
            if (node.IsKeywordArguments || maplets.Length > MaxArrayLiteral || !IsPlainLiteralKeyed(node)) {
                throw Bail("hash literal");
            }
            var values = new List<MSA.Expression>(1 + 2 * maplets.Length);
            values.Add(Ast.Constant(_context));
            bool symbols = maplets.Length > 0 && maplets.Length <= 3;
            foreach (var maplet in maplets) {
                if (!(maplet.Key is SymbolLiteral)) { symbols = false; }
                JT t;
                var key = Emit(maplet.Key, out t);
                values.Add(Coerce(key, t, JT.Obj));
                var value = Emit(maplet.Value, out t);
                values.Add(Coerce(value, t, JT.Obj));
            }
            type = JT.Obj;
            if (symbols) {
                return Ast.Call(JitRuntime.M("NewSymbolHash" + maplets.Length), values);
            }
            values.RemoveAt(0);
            return Ast.Call(JitRuntime.M("NewHash"), Ast.Constant(_context), Ast.NewArrayInit(typeof(object), values));
        }

        /// <summary>
        /// `a, b = x, y', `@a, @b = b, a': the generic body's order (CompoundLeftValue.TransformWrite) - every right-hand
        /// value first, left to right, into a temporary, then each target written left to right, a missing value nil and
        /// a value with no target dropped. Each write is the one `target = value' makes (EmitAssignment), the value an
        /// EmittedValue, so a local keeps its type and an instance variable write its effect and its deopt on a frozen
        /// self, exactly as the same writes spelled as statements. Only locals of the method and instance variables as
        /// targets, no `*rest' and no nested target, and two or more values, none splatted (IsListAssignment): a single
        /// value is destructured by to_ary, which would be Ruby code run from the typed body, or a deopt after the effect
        /// that computed it. The value of the expression, an Array of the right-hand values, is made only where it is used.
        /// </summary>
        private MSA.Expression/*!*/ EmitParallelAssignment(ParallelAssignmentExpression/*!*/ node, out JT type) {
            if (!IsListAssignment(node, _scopeDepth)) { throw Bail("parallel assignment"); }
            var lefts = node.Left.LeftValues;
            var rights = node.Right;
            var parts = new List<MSA.Expression>();
            var temps = new List<MSA.ParameterExpression>();
            var values = new EmittedValue[rights.Length];
            for (int i = 0; i < rights.Length; i++) {
                JT t;
                var v = Emit(rights[i], out t);
                if (t == JT.None || t == JT.Never) { throw Bail("parallel assignment of a value that jumps away"); }
                var temp = Ast.Variable(ClrType(t), "#masgn" + i);
                temps.Add(temp);
                parts.Add(Ast.Assign(temp, v));
                values[i] = new EmittedValue(temp, t);
            }
            for (int i = 0; i < lefts.Length; i++) {
                RExpr value = (i < values.Length) ? values[i] : new EmittedValue(Ast.Constant(null, typeof(object)), JT.Obj);
                JT ignored;
                parts.Add(Emit(new SimpleAssignmentExpression(lefts[i], value, null, node.Location), out ignored));
            }
            if (IsDiscardedStatement(node)) {
                parts.Add(Ast.Constant(null, typeof(object)));
            } else {
                var boxed = new List<MSA.Expression>(values.Length);
                foreach (var v in values) { boxed.Add(Coerce(v.Value, v.Type, JT.Obj)); }
                parts.Add(IronRuby.Compiler.Methods.MakeArrayOpCall(boxed));
            }
            type = JT.Obj;
            return Ast.Block(typeof(object), temps, parts);
        }

        /// <summary>The parallel assignments EmitParallelAssignment takes (JitScreen walks the same).</summary>
        internal static bool IsListAssignment(ParallelAssignmentExpression/*!*/ node, int scopeDepth) {
            var left = node.Left;
            if (left.HasUnsplattedValue || node.Right.Length < 2) { return false; }
            foreach (var target in left.LeftValues) {
                if (target is InstanceVariable) { continue; }
                var local = target as LocalVariable;
                if (local == null || local.DefinitionLexicalDepth != scopeDepth) { return false; }
            }
            foreach (var value in node.Right) {
                if (value is SplattedArgument) { return false; }
            }
            return true;
        }

        private HashSet<RExpr> _discardedStatements;

        /// <summary>
        /// A statement before the last of its list: EmitStatements drops its value. Anything else (a last statement, an
        /// operand) may be used, which only costs the value being made.
        /// </summary>
        private bool IsDiscardedStatement(RExpr/*!*/ node) {
            if (_discardedStatements == null) {
                var finder = new DiscardedStatementFinder();
                if (_ast != null && _ast.Body != null) { finder.Walk(_ast.Body); }
                _discardedStatements = finder.Found;
            }
            return _discardedStatements.Contains(node);
        }

        private sealed class DiscardedStatementFinder : Walker {
            internal readonly HashSet<RExpr>/*!*/ Found = new HashSet<RExpr>();

            protected internal override void VisitStatements(Statements/*!*/ statements) {
                foreach (var s in statements.AllButLast) { Found.Add(s); }
            }
        }

        /// <summary>
        /// HashConstructor.HasPlainLiteralKeys: a Symbol or String literal, or nil, true, false or a Fixnum literal - or,
        /// alone, any other numeric literal (two Float or Bignum literals of one value are told apart by #eql?).
        /// </summary>
        internal static bool IsPlainLiteralKeyed(HashConstructor/*!*/ node) {
            foreach (var maplet in node.Maplets) {
                if (maplet.Key is SymbolLiteral || maplet.Key is StringLiteral) { continue; }
                var literal = maplet.Key as Literal;
                if (literal == null) { return false; }
                object value = literal.Value;
                if (!(value == null || value is bool || value is int) && node.Maplets.Length > 1) { return false; }
            }
            return true;
        }

        private MSA.Expression/*!*/ EmitLiteral(Literal/*!*/ literal, out JT type) {
            object v = literal.Value;
            if (v is int) { type = JT.Int; return Ast.Constant((int)v, typeof(int)); }
            if (v is long) { type = JT.Lng; return Ast.Constant((long)v, typeof(long)); }
            if (v is double) { type = JT.Dbl; return Ast.Constant((double)v, typeof(double)); }
            if (v is bool) { type = JT.Bool; return Ast.Constant((bool)v, typeof(bool)); }
            if (v == null) { type = JT.Obj; return Ast.Constant(null, typeof(object)); }
            throw Bail("literal: " + (v == null ? "nil" : v.GetType().Name));
        }

        /// <summary>
        /// `"lit"': the String the generic body would make (StringLiteral.TransformWithoutSite) - a new one on
        /// each evaluation, chilled or plain as the file says, or under `frozen_string_literal: true' the one
        /// interned frozen literal. Making it changes nothing a deopt could see done twice: a String nothing has
        /// seen yet is dropped, and the interned one is found again. Under --debug-frozen-string-literal a
        /// literal carries the place it was written, which only the generic body knows.
        /// </summary>
        private MSA.Expression/*!*/ EmitStringLiteral(StringLiteral/*!*/ node, out JT type) {
            if (_context.RubyOptions.DebugFrozenStringLiteral) { throw Bail("string literal under --debug-frozen-string-literal"); }
            type = JT.Obj;
            return Ast.Convert(node.TransformWithoutSite(), typeof(object));
        }

        /// <summary>
        /// `"a#{x}b"': the String the generic body makes (StringConstructor.TransformWithoutScope), each part converted by
        /// JitRuntime.InterpolatedPart, which runs no Ruby code: a String is taken as it is, an Integer, Float, Symbol, nil,
        /// true or false goes through the conversion site the generic body converts with while its class's to_s is the
        /// library one (as it was when the body was compiled: the entry's method version guard), and anything else deopts
        /// before its to_s could run - the generic body calls that from its own frame. So a deopt follows each part, after
        /// whatever the part did (_canDeopt). Not where a refinement is active, which the generic body asks about the to_s
        /// it calls (RubyOps.TryConvertToSWithRefinements), nor in an outlined loop.
        /// </summary>
        private MSA.Expression/*!*/ EmitInterpolation(StringConstructor/*!*/ node, out JT type) {
            if (_loop != null) { throw Bail("interpolation in a loop"); }
            if (!_refinements.IsEmpty) { throw Bail("interpolation where a refinement is active"); }
            if (_interpolationSite == null) {
                _interpolationSite = new JitRuntime.InterpolationSite(_context,
                    IsBuiltinMethod(_context.IntegerClass, "to_s"), IsBuiltinMethod(_context.GetClass(typeof(double)), "to_s"),
                    IsBuiltinMethod(_context.GetClass(typeof(RubySymbol)), "to_s"), IsBuiltinMethod(_context.NilClass, "to_s"),
                    IsBuiltinMethod(_context.TrueClass, "to_s"), IsBuiltinMethod(_context.FalseClass, "to_s"));
            }
            var result = node.TransformWithoutScope(part => {
                JT partType;
                var value = Emit(part, out partType);
                var converted = Ast.Call(JitRuntime.M("InterpolatedPart"), Ast.Constant(_interpolationSite), BoxForStore(value, partType));
                _canDeopt = true;
                return converted;
            });
            if (result == null) { throw Bail("interpolation: " + node.Kind + " or literals of incompatible encodings"); }
            type = JT.Obj;
            return Ast.Convert(result, typeof(object));
        }

        // what the body's interpolations convert their parts with (EmitInterpolation)
        private JitRuntime.InterpolationSite _interpolationSite;

        private MSA.Expression/*!*/ EmitLocalRead(LocalVariable/*!*/ local, out JT type) {
            if (_loop == null && local.DefinitionLexicalDepth != _scopeDepth) {
                // a local a define_method body reads from an enclosing scope: read from there, each time (JitBlockMethod)
                var blockMethod = _ast as JitBlockMethod.BlockMethodDefinition;
                var captured = (blockMethod != null) ? blockMethod.GetCaptured(local) : null;
                if (captured == null) { throw JitBailout.Instance; }
                type = JT.Obj;
                return Ast.Call(JitBlockMethod.ReadCapturedMethod, Ast.Constant(captured));
            }
            MSA.ParameterExpression p;
            if (!TryLookup(Key(local), out p, out type)) {
                KeyValuePair<MSA.ParameterExpression, JT> loaded;
                if (_arrayLocals != null && _arrayLocals.TryGetValue(Key(local), out loaded)) {
                    // an Array the entry loaded once (ArrayLocal), which the loop does not write
                    type = JT.Obj;
                    return Ast.Convert(loaded.Key, typeof(object));
                }
                if (_loop != null) {
                    // a local only stored is an object slot, holding the box it holds (IsStoredOnly)
                    BindLoopLocal(local, IsStoredOnly(local) ? JT.Obj : JT.None, out p, out type);
                    return LoadLoopSlot(p);
                }
                // A local read before any assignment is nil in Ruby; not modelled.
                throw Bail("local read before assignment: " + local.Name);
            }
            return (_loop != null) ? LoadLoopSlot(p) : p;
        }

        private MSA.Expression/*!*/ EmitAssignment(SimpleAssignmentExpression/*!*/ node, out JT type) {
            string op = node.Operation;
            if (op == "&&" || op == "||") { return EmitOrAssignment(node, out type); }
            return EmitAssignment(node, op, false, out type);
        }

        /// <summary>
        /// `@x ||= v', `x ||= v' (and `&&='): `@x || (@x = v)', built from EmitAndOr's parts. The variable is read
        /// once, v is evaluated only when the store is made, and the value is the variable's or v's, joined as
        /// `a || b' joins them. The store happens on some paths only, so a held-back write of it is not a definite one
        /// (TrySinkIVarWrite). Kept to method bodies: an outlined loop does not see these (the screen walks them, but
        /// an instance variable write there bails anyway, and a local one is left to the generic body).
        /// </summary>
        private MSA.Expression/*!*/ EmitOrAssignment(SimpleAssignmentExpression/*!*/ node, out JT type) {
            if (_loop != null) { throw Bail(node.Operation + "= in an outlined loop"); }
            if (!(node.Left is InstanceVariable || node.Left is LocalVariable)) { throw Bail(node.Operation + "= of " + node.Left.GetType().Name); }
            var local = node.Left as LocalVariable;
            MSA.ParameterExpression p;
            JT lt;
            if (local != null && !TryLookup(Key(local), out p, out lt)) {
                // No binding does not mean no assignment: a branch that assigned the local has dropped its binding when
                // it ended (EmitRegion), so in `if c; x = 1; end; x ||= 2' the local may hold 1 here. Taking it for nil
                // made that `x = 2' on every path; a flow property the screen says yes to, so it is the compiler's no.
                throw Bail(node.Operation + "= of a local with no binding here: " + local.Name);
            }
            return EmitAndOr(node.Left, node, node.Operation == "&&", out type);
        }

        private MSA.Expression/*!*/ EmitAssignment(SimpleAssignmentExpression/*!*/ node, string op, bool conditional, out JT type) {

            var element = node.Left as ArrayItemAccess;
            if (element != null) { return EmitIndexWrite(element, op, node.Right, IsLastStatement(node), IsEarlyTopStatement(node), out type); }

            var ivarTarget = node.Left as InstanceVariable;
            if (ivarTarget != null) {
                // A write can raise (FrozenError), and an exception leaving an outlined loop
                // part way through would leave the scope's locals as they were on entry.
                if (_loop != null) { throw Bail("instance variable write in an outlined loop"); }
                JT vt;
                MSA.Expression v;
                if (op != null) {
                    // `@x op= v'  ->  `@x = @x op v': the read and the operator are emitted (and may
                    // deopt) before the write, as for `@x = @x op v' written out
                    JT ct;
                    var current = NumericIVar(ivarTarget, Emit(ivarTarget, out ct), ref ct);
                    var operand = Emit(node.Right, out vt);
                    operand = NumericIVar(node.Right, operand, ref vt);
                    if (IsNumeric(ct) && IsNumeric(vt)) {
                        v = EmitBinary(op, current, ct, operand, vt, out vt);
                    } else if ((ct == JT.Obj && IsNumeric(vt)) || (IsNumeric(ct) && vt == JT.Obj)) {
                        v = EmitSpeculatedBinary(op, current, ct, ivarTarget, operand, vt, node.Right, out vt);
                    } else {
                        throw Bail("instance variable op-assign on " + ct + ", " + vt);
                    }
                } else {
                    v = Emit(node.Right, out vt);
                }
                var sunk = TrySinkIVarWrite(node, ivarTarget.Name, v, vt, conditional, out type);
                if (sunk != null) { return sunk; }
                if (_effectNotOnSelf) { _canDeopt = true; }     // SetIVar deopts on a frozen self, here after an effect elsewhere
                _pure = false;      // an ivar write is observable: nothing emitted after it may deopt (_deoptAfterEffect)
                _ivarWrite = true;
                var ivarSite = Ast.Constant(new InstanceVariableSite(ivarTarget.Name));
                if (vt == JT.Dbl) {
                    // a Float stored raw (RubyInstanceData, "Float locations"); the value stays a double
                    var raw = Ast.Variable(typeof(double), "#value");
                    type = JT.Dbl;
                    return Ast.Block(typeof(double), new[] { raw },
                        Ast.Assign(raw, v),
                        Ast.Call(JitRuntime.M("SetIVarDouble"), Ast.Constant(_context), _self, raw, ivarSite),
                        raw);
                }
                type = JT.Obj;
                return Ast.Call(JitRuntime.M("SetIVar"), Ast.Constant(_context), _self, Coerce(v, vt, JT.Obj), ivarSite);
            }

            var gvarTarget = node.Left as AstGlobalVariable;
            if (gvarTarget != null) {
                if (op != null) { throw Bail("global variable op-assign"); }
                return EmitGlobalWrite(gvarTarget, node.Right, out type);
            }

            var attribute = node.Left as AttributeAccess;
            if (attribute != null) { return EmitAttributeWrite(attribute, op, node.Right, node, out type); }

            var target = node.Left as LocalVariable;
            if (target == null || (_loop == null && target.DefinitionLexicalDepth != _scopeDepth)) { throw JitBailout.Instance; }
            if (_inKeywordDefault) { throw Bail("keyword default assigns a local"); }
            if (op == null) { NoteLoopSample(target, node.Right); }

            JT rhsType;
            var rhs = Emit(node.Right, out rhsType);

            MSA.ParameterExpression p;
            JT lt;
            bool known = TryLookup(Key(target), out p, out lt);
            bool storedOnly = (op == null) && IsNumeric(rhsType) && IsStoredOnly(target);
            if (!known && IsNestedLocal(target)) {
                // a local of an inner block run in place, first assigned here: a CLR local of the copy (JitCompiler.Nest.cs)
                if (op != null) { throw Bail("op-assign of an unassigned local: " + target.Name); }
                if (rhsType == JT.None || rhsType == JT.Never) { throw Bail("a block local assigned a value that jumps away"); }
                if (storedOnly) {
                    rhs = BoxForStore(rhs, rhsType);
                    rhsType = JT.Obj;
                }
                p = Declare(Key(target), rhsType);
                type = rhsType;
                return Ast.Assign(p, rhs);
            }
            if (!known && _loop != null) {
                // The slot exists in the tuple whether or not the loop has read it yet, and it
                // is written back on the way out, so it has to be bound rather than declared.
                // `x op= v' reads x first, so an object v says nothing about what x holds
                known = BindLoopLocal(target, storedOnly ? JT.Obj : (op != null && rhsType == JT.Obj) ? JT.None : rhsType, out p, out lt);
            }
            if (!known && storedOnly) {
                // read only to be stored: carried boxed, boxed once here (IsStoredOnly)
                rhs = BoxForStore(rhs, rhsType);
                rhsType = JT.Obj;
            }

            if (op != null) {
                // `x op= v'  ->  `x = x op v'
                if (!known) { throw JitBailout.Instance; }
                rhs = NumericIVar(node.Right, rhs, ref rhsType);
                MSA.Expression current = (_loop != null) ? LoadLoopSlot(p) : p;
                if (IsNumeric(lt) && IsNumeric(rhsType)) {
                    rhs = EmitBinary(op, current, lt, rhs, rhsType, out rhsType);
                } else if ((lt == JT.Obj && IsNumeric(rhsType)) || (IsNumeric(lt) && rhsType == JT.Obj)) {
                    // `s += a[i]', `x += 1' with x an object local: the object is speculated to be a number
                    rhs = EmitSpeculatedBinary(op, current, lt, target, rhs, rhsType, node.Right, out rhsType);
                } else {
                    throw JitBailout.Instance;
                }
            }

            if (known) {
                if (lt == JT.Obj && rhsType != JT.Obj) {
                    // an object local holds any value, boxed
                    rhs = BoxForStore(rhs, rhsType);
                    rhsType = JT.Obj;
                }
                if (lt != rhsType) {
                    // Int and Lng are two representations of one Ruby class, so a store across
                    // them is a widening or a narrowing, not a type change. Anything else would
                    // change the value's class, which one typed slot cannot express.
                    if (!IsInteger(lt) || !IsInteger(rhsType)) { throw JitBailout.Instance; }
                    rhs = Coerce(rhs, rhsType, lt);
                    rhsType = lt;
                }
            } else {
                p = Declare(target.Name, rhsType);
            }
            type = rhsType;
            return (_loop != null) ? StoreLoopSlot(p, rhs) : (MSA.Expression)Ast.Assign(p, rhs);
        }

        // Branches are emitted in the order they run - conditions in turn, each arm from the purity
        // the conditions before it left - and the purity after the construct is that of all its
        // arms together, so _deoptAfterEffect sees each path as it runs, not one arm's write as
        // coming before the other arm's deopt.
        private MSA.Expression/*!*/ EmitIf(IfExpression/*!*/ node, out JT type) {
            MSA.Expression guarded;
            if (TryEmitConstantGuarded(node, out guarded, out type)) { return guarded; }
            var clauses = node.ElseIfClauses;
            int n = (clauses != null) ? clauses.Count : 0;
            bool hasElse = n > 0 && clauses[n - 1].Condition == null;
            int tests = 1 + (hasElse ? n - 1 : n);
            var conds = new MSA.Expression[tests];
            var arms = new MSA.Expression[tests];
            var armTypes = new JT[tests];
            bool pureAfterArms = true;
            for (int i = 0; i < tests; i++) {
                if (i > 0 && clauses[i - 1].Condition == null) { throw JitBailout.Instance; }
                var condition = (i == 0) ? node.Condition : clauses[i - 1].Condition;
                conds[i] = EmitCondition(condition);
                bool pureAfterTest = _pure;
                int guard = PushTruthGuard(condition);
                try {
                    arms[i] = EmitRegion(i == 0 ? node.Body : clauses[i - 1].Statements, out armTypes[i]);
                } finally {
                    PopTruthGuard(guard);
                }
                pureAfterArms &= _pure;
                _pure = pureAfterTest;
            }
            MSA.Expression elseExpr = Ast.Constant(null, typeof(object));
            JT elseType = JT.Obj;
            if (hasElse) {
                elseExpr = EmitRegion(clauses[n - 1].Statements, out elseType);
            }
            _pure &= pureAfterArms;
            for (int i = tests - 1; i >= 0; i--) {
                elseExpr = Join(conds[i], arms[i], armTypes[i], elseExpr, elseType, out elseType);
            }
            type = elseType;
            return elseExpr;
        }

        private MSA.Expression/*!*/ EmitUnless(UnlessExpression/*!*/ node, out JT type) {
            MSA.Expression guarded;
            if (TryEmitConstantGuarded(node, out guarded, out type)) { return guarded; }
            var test = Ast.Not(EmitCondition(node.Condition));
            bool pureAfterTest = _pure;
            JT thenType;
            var thenExpr = EmitRegion(node.Statements, out thenType);
            bool pureAfterThen = _pure;
            _pure = pureAfterTest;
            MSA.Expression elseExpr = Ast.Constant(null, typeof(object));
            JT elseType = JT.Obj;
            if (node.ElseClause != null) {
                if (node.ElseClause.Condition != null) { throw JitBailout.Instance; }
                // the branch `unless x' leaves to x being true (ObservedType)
                int guard = PushTruthGuard(node.Condition);
                try {
                    elseExpr = EmitRegion(node.ElseClause.Statements, out elseType);
                } finally {
                    PopTruthGuard(guard);
                }
            }
            _pure &= pureAfterThen;
            return Join(test, thenExpr, thenType, elseExpr, elseType, out type);
        }

        /// <summary>
        /// `x if C', `x unless C', `if C then x else y end' (IR_JIT_DEADCONST): the arm the constant's value makes dead
        /// is not emitted at all, and neither is the test. The constant is folded as any other read of it is
        /// (ResolveConstant), so the entry finds out that it changed (JitStub.RevalidateConstants) and the arm runs
        /// again from the next call; an outlined loop hands itself back at its next safe point when it is reassigned
        /// (RubyContext.ConstantReassigned), and a method body's loop deopts there (TryCompile: _foldedInLoop). False when the node is not such a guard, or the value is one a non-main Ractor may not
        /// read, which EmitConstant checks where it is read.
        /// </summary>
        private bool TryEmitConstantGuarded(RExpr/*!*/ node, out MSA.Expression result, out JT type) {
            result = null;
            type = JT.None;
            ConstantVariable guard;
            Statements whenTrue, whenFalse;
            if (!JitRuntime.DeadConstants || !ConstantGuard(node, out guard, out whenTrue, out whenFalse)) { return false; }
            object value = ResolveConstant(guard);
            if (!RactorIsolation.IsTriviallyShareable(value)) { return false; }
            var live = RubyOps.IsTrue(value) ? whenTrue : whenFalse;
            if (live == null || live.Count == 0) {
                type = JT.Obj;
                result = Ast.Constant(null, typeof(object));
            } else {
                result = EmitRegion(live, out type);
            }
            return true;
        }

        // A constant was folded in or after a loop of the body's own (ResolveConstant), or after a call of a callee with
        // one: what TryCompile makes the body deopt for, or declines.
        private bool _foldedInLoop;
        private bool _foldedAfterCalleeLoop;

        /// <summary>
        /// `if C ... [else ...] end' or `unless C ... [else ...] end', without an elsif, whose condition is a constant the
        /// compiler folds (`C', `::C', `A::B'): <paramref name="whenTrue"/> is what runs when the constant is truthy and
        /// <paramref name="whenFalse"/> what runs when it is not (either may be null: nothing). The screen (JitScreen)
        /// and the compiler ask it of the same nodes.
        /// </summary>
        internal static bool ConstantGuard(RExpr/*!*/ node, out ConstantVariable guard, out Statements whenTrue, out Statements whenFalse) {
            guard = null;
            whenTrue = whenFalse = null;
            var ifExpr = node as IfExpression;
            if (ifExpr != null) {
                var clauses = ifExpr.ElseIfClauses;
                if (clauses != null && clauses.Count > 0 && (clauses.Count != 1 || clauses[0].Condition != null)) { return false; }
                guard = ifExpr.Condition as ConstantVariable;
                whenTrue = ifExpr.Body;
                whenFalse = (clauses != null && clauses.Count == 1) ? clauses[0].Statements : null;
            } else {
                var unless = node as UnlessExpression;
                if (unless == null) { return false; }
                if (unless.ElseClause != null && unless.ElseClause.Condition != null) { return false; }
                guard = unless.Condition as ConstantVariable;
                whenTrue = (unless.ElseClause != null) ? unless.ElseClause.Statements : null;
                whenFalse = unless.Statements;
            }
            return guard != null && JitScreen.IsFoldableConstant(guard);
        }

        /// <summary>
        /// `while c; body; end' / `until c; body; end'. A `break' jumps to the exit and a `next' to the test
        /// (EmitBreak); a `redo' makes Emit bail. The value is nil, or what a `break v' stored in the loop's result.
        /// </summary>
        private MSA.Expression/*!*/ EmitWhile(WhileLoopExpression/*!*/ node, out JT type) {
            if (node.IsPostTest) { throw JitBailout.Instance; }
            _hasLoop = true;
            var exit = Ast.Label("#while-exit");
            var cont = Ast.Label("#while-continue");
            var savedBreak = _breakLabel;
            var savedContinue = _continueLabel;
            var savedValue = _breakValue;
            _breakLabel = exit;
            _continueLabel = cont;
            _breakValue = null;
            var test = EmitCondition(node.Condition);
            if (!node.IsWhileLoop) { test = Ast.Not(test); }
            JT ignored;
            var body = EmitRegion(node.Statements, out ignored);
            var result = _breakValue;
            _breakLabel = savedBreak;
            _continueLabel = savedContinue;
            _breakValue = savedValue;
            type = JT.Obj;

            // The outlined loop keeps a copy of its locals as they were at the top of the
            // iteration. A deopt part way through one restores them, so the loop is handed back
            // at an iteration boundary and the generic copy re-runs the iteration from there.
            MSA.Expression snapshot = (node == _loop) ? EmitSnapshot() : null;

            // The interrupt check (Thread#raise, Thread#kill, a trap handler), after the snapshot and the (pure) test, before the body.
            // An inner loop of an outlined one deopts to the *outer* iteration's top, which
            // re-runs part of that iteration: only allowed for a pure body, as for any deopt.
            if (_loop != null && node != _loop) { _canDeopt = true; }
            // The same check sees a method or a constant the code was built on changed by another thread
            // while the loop runs (EmitLoopSafePoint). In a method a safe point runs what is
            // waiting (a trap handler, say) where it is, and the body goes on - unless the body writes
            // elements, or the version moved and the body may deopt, which it only knows once it has
            // been emitted.
            var epoch = NewLoopEpoch();
            MSA.Expression safePoint = EmitLoopSafePoint(epoch);

            var iteration = (snapshot != null)
                ? Ast.Block(snapshot, Ast.IfThen(Ast.Not(test), Ast.Break(exit)), safePoint, body)
                : Ast.Block(Ast.IfThen(Ast.Not(test), Ast.Break(exit)), safePoint, body);

            var variables = new List<MSA.ParameterExpression>();
            if (epoch != null) { variables.Add(epoch); }
            if (result == null) {
                return Ast.Block(typeof(object), variables,
                    StartLoopEpoch(epoch),
                    Ast.Loop(iteration, exit, cont),
                    Ast.Constant(null, typeof(object))
                );
            }
            // An inner loop is entered again on each iteration of the outer one: its result starts as nil every time.
            variables.Add(result);
            return Ast.Block(typeof(object), variables,
                Ast.Assign(result, Ast.Constant(null, typeof(object))),
                StartLoopEpoch(epoch),
                Ast.Loop(iteration, exit, cont),
                result
            );
        }

        /// <summary>
        /// `break' / `break v' in the innermost loop being emitted: v, boxed, goes into the loop's result, and the jump
        /// to its exit follows. An outlined loop (-X:OSR) takes only a bare `break', as it always has: its site hands
        /// back the locals, not a value.
        /// </summary>
        private MSA.Expression/*!*/ EmitBreak(BreakStatement/*!*/ node, out JT type) {
            type = JT.Obj;
            var args = node.Arguments;
            if (args == null || args.Expressions.Length == 0) {
                return Ast.Block(typeof(object), Ast.Break(_breakLabel), Ast.Constant(null, typeof(object)));
            }
            if (_loop != null) { throw Bail("break with a value in an outlined loop"); }
            if (args.Expressions.Length != 1 || args.Expressions[0] is SplattedArgument) { throw Bail("break of several values"); }
            JT vt;
            var value = Emit(args.Expressions[0], out vt);
            if (_breakValue == null) { _breakValue = Ast.Variable(typeof(object), "#break"); }
            return Ast.Block(typeof(object),
                Ast.Assign(_breakValue, Coerce(value, vt, JT.Obj)),
                Ast.Break(_breakLabel),
                Ast.Constant(null, typeof(object))
            );
        }

        /// <summary>
        /// `snap_x = x' for every slot bound so far. Emitted lazily, after the body, because
        /// the body is what binds them; the DLR block it produces runs before the test.
        /// </summary>
        // Set once a method body is known to write Hash or Array elements: its safe points then deopt
        // rather than run Ruby code in the middle of writes a later deopt would take back.
        private readonly StrongBox<bool>/*!*/ _safePointDeopts = new StrongBox<bool>();

        // What the code is compiled under, for its loops' safe points to check (EmitLoopSafePoint);
        // whoever compiles it fills it in (JitLoopCompiler, JitStub).
        private readonly JitLoopGuard/*!*/ _loopGuard = new JitLoopGuard();

        // The code epoch read before the code was compiled (JitLoopGuard.Set): its loops' safe points compare the
        // safe-point word with it, as an immediate.
        private long _compileEpoch;

        /// <summary>
        /// A typed method body's loop keeps an epoch local, which starts from the one the body last found
        /// current; null in an outlined loop, which has none (see EmitLoopSafePoint).
        /// </summary>
        private MSA.ParameterExpression NewLoopEpoch() {
            return (_loop != null) ? null : Ast.Variable(typeof(long), "#epoch");
        }

        private MSA.Expression/*!*/ StartLoopEpoch(MSA.ParameterExpression epoch) {
            return (epoch != null)
                ? (MSA.Expression)Ast.Assign(epoch, Ast.Field(Ast.Constant(_loopGuard), JitLoopGuard.EpochField))
                : AstUtils.Empty();
        }

        /// <summary>
        /// The safe point at the top of an iteration: the safe-point word compared with the code epoch the code
        /// was compiled at, an immediate, so that the loop keeps no register for it.
        ///
        /// In an outlined loop a word that has moved hands the loop back, always: what is not taken in the loop
        /// (a waiting interrupt, a method or constant changed, or a constant the copy did not fold) the site
        /// sorts out (OsrLoopSite.Run), so the path out of the loop is a throw, and the CLR JIT keeps nothing
        /// live across it. (A slow path that returned and let the loop go on made it spill the loop's counter:
        /// an `each' over an Array ran 15-30% slower.)
        ///
        /// A typed method body's safe point may run a trap handler in place and go on (as MethodSafePoint did), so
        /// its slow path returns: when the word has moved, the loop's epoch local says whether the move has been
        /// looked at already, and the slow path brings the local up to date when it lets the body go on. After a
        /// move that left the body current (a constant it did not fold) its loops pay a second compare.
        /// </summary>
        private MSA.Expression/*!*/ EmitLoopSafePoint(MSA.ParameterExpression epoch) {
            var word = Ast.Property(null, typeof(RubyUtils).GetProperty("SafePointWord"));
            var moved = Ast.NotEqual(word, Ast.Constant(_compileEpoch));
            if (epoch == null) {
                return Ast.IfThen(moved, Ast.Throw(Ast.Call(JitRuntime.M("OsrHandBack"))));
            }
            return Ast.IfThen(moved,
                Ast.IfThen(
                    Ast.NotEqual(word, epoch),
                    Ast.Assign(epoch, Ast.Call(JitRuntime.M("MethodLoopSafePointSlow"), Ast.Constant(_loopGuard), Ast.Constant(_safePointDeopts)))
                )
            );
        }

        private MSA.Expression/*!*/ EmitSnapshot() {
            if (_slots.Count == 0 && _undoLog == null) { return AstUtils.Empty(); }
            var copies = new List<MSA.Expression>(_slots.Count + 1);
            if (_undoLog != null) {
                // the Hash writes of the iterations before this one stand
                copies.Add(Ast.Call(_undoLog, typeof(JitUndoLog).GetMethod("Commit"), _undoMark));
            }
            foreach (var slot in _slots) {
                copies.Add(Ast.Assign(slot.Snapshot, slot.Variable));
                if (slot.Assigned != null) {
                    copies.Add(Ast.Assign(slot.AssignedSnapshot, slot.Assigned));
                }
            }
            return Ast.Block(copies);
        }

        /// <summary>
        /// `return v' / `return'. The value leaves through a DLR return label typed by the
        /// return type this round is compiling under, which is the same type the value the body
        /// falls off with is given - so an early `return n' and a trailing `return f(n)' unify
        /// exactly the way the two branches of an `if' do. A round whose guess turns out to be
        /// too narrow for a value some `return' produces is abandoned and re-run wider.
        ///
        /// The value is JT.Never: control does not continue here, so whatever the context wanted
        /// this expression to be, it may be.
        /// </summary>
        private MSA.Expression/*!*/ EmitReturn(ReturnStatement/*!*/ node, out JT type) {
            // In loop mode the body being emitted is one loop outlined out of its method, entered
            // and left by the OSR site. A `return' there returns from the *method*, which the
            // outlined copy has no way to do.
            if (_loop != null) { throw Bail("return inside an outlined loop"); }

            var args = node.Arguments;
            JT vt;
            MSA.Expression value;
            if (args == null || args.Expressions.Length == 0) {
                vt = JT.Obj;
                value = Ast.Constant(null, typeof(object));
            } else if (args.Expressions.Length == 1 && !(args.Expressions[0] is SplattedArgument)) {
                value = Emit(args.Expressions[0], out vt);
            } else {
                throw Bail("return of several values");
            }

            if (vt == JT.Never) {
                // `return (return v)' - only the inner jump ever happens.
                type = JT.Never;
                return value;
            }

            _returnUnion = (_returnUnion == JT.None) ? vt : Merge(_returnUnion, vt);
            if (Merge(vt, _selfReturn) != _selfReturn) {
                // The first round's Int is an assumption, not an observation, so it does not
                // take part in the merge: a Float `return x' makes the next round Float, not Obj.
                _retype = _firstRound ? vt : Merge(_selfReturn, vt);
                throw Bail("return value wider than this round's return type");
            }

            if (_returnLabel == null) { _returnLabel = Ast.Label(ClrType(_selfReturn), "#return"); }
            type = JT.Never;
            return Ast.Return(_returnLabel, Coerce(value, vt, _selfReturn), typeof(object));
        }

        private MSA.Expression/*!*/ EmitIfLike(RExpr/*!*/ c, RExpr/*!*/ t, RExpr/*!*/ f, out JT type) {
            var test = EmitCondition(c);
            bool pureAfterTest = _pure;
            JT tt, ft;
            MSA.Expression te;
            int guard = PushTruthGuard(c);
            try {
                te = EmitRegion(t, out tt);
            } finally {
                PopTruthGuard(guard);
            }
            bool pureAfterThen = _pure;
            _pure = pureAfterTest;
            var fe = EmitRegion(f, out ft);
            _pure &= pureAfterThen;    // as EmitIf
            return Join(test, te, tt, fe, ft, out type);
        }

        private MSA.Expression/*!*/ Join(MSA.Expression/*!*/ test, MSA.Expression/*!*/ a, JT at, MSA.Expression/*!*/ b, JT bt, out JT type) {
            type = Merge(at, bt);
            return Ast.Condition(test, Coerce(a, at, type), Coerce(b, bt, type), ClrType(type));
        }

        /// <summary>
        /// The type of a value that comes from one of two control-flow paths - the arms of an
        /// `if', the fall-off value and the explicit returns. Int and Lng are two
        /// representations of one Ruby class, so they merge to Lng. An Integer and a Float do
        /// NOT merge to Float: converting the Integer would change its class (`c ? 1.5 : 2' is
        /// 2, not 2.0), so a mixed pair is boxed.
        /// </summary>
        private static JT Merge(JT a, JT b) {
            if (a == b) { return a; }
            // A path that jumped away contributes no value, so the other one decides.
            if (a == JT.Never) { return b; }
            if (b == JT.Never) { return a; }
            if (IsInteger(a) && IsInteger(b)) { return JT.Lng; }
            return JT.Obj;
        }

        /// <summary>
        /// The operand type of a binary numeric operation: Ruby promotes an Integer operand of
        /// Float arithmetic to Float, so here - and only here - Int op Dbl is Dbl.
        /// </summary>
        private static JT Unify(JT a, JT b) {
            if (a == b) { return a; }
            if (IsNumeric(a) && IsNumeric(b)) {
                // int < long < double: the wider of the two wins.
                return (a == JT.Dbl || b == JT.Dbl) ? JT.Dbl : JT.Lng;
            }
            return JT.Obj;
        }

        private MSA.Expression/*!*/ Coerce(MSA.Expression/*!*/ e, JT from, JT to) {
            if (from == to) { return e; }
            if (from == JT.Never) {
                // e always jumps away, so what follows it is unreachable and only there to give
                // the expression the static type its context wants.
                return Ast.Block(ClrType(to), e, Ast.Default(ClrType(to)));
            }
            if (to == JT.Dbl && (from == JT.Int || from == JT.Lng)) { return Ast.Convert(e, typeof(double)); }
            if (to == JT.Lng && from == JT.Int) { return Ast.Convert(e, typeof(long)); }
            if (to == JT.Int && from == JT.Lng) {
                // Only where the value has to be an int again: an int local, an int parameter of
                // the direct self-call. Out of range means the Ruby value really did outgrow an
                // Int32, and the generic body takes it from there.
                _canDeopt = true;
                return FuseIntOp(e) ?? Ast.Call(JitRuntime.M("DemoteToInt"), e);
            }
            if (to == JT.Obj) {
                // A long leaves through the representation funnel: a value that fits in an Int32
                // IS an Int32, which is what #hash, #eql? and Hash keys are defined on.
                if (from == JT.Lng) { return Ast.Call(JitRuntime.M("NarrowLong"), e); }
                // Fixnum is a boxed int, Float a boxed double, true/false boxed bools. A literal
                // is boxed once, here, as the generic body does, rather than on every evaluation
                // (`@count = 0' in an initializer would otherwise allocate per object).
                var constant = e as MSA.ConstantExpression;
                if (constant != null) { return Ast.Constant(constant.Value, typeof(object)); }
                if (from == JT.Bool) { return Ast.Call(typeof(Microsoft.Scripting.Runtime.ScriptingRuntimeHelpers).GetMethod("BooleanToObject"), e); }
                return Ast.Convert(e, typeof(object));
            }
            throw JitBailout.Instance;
        }

        /// <summary>
        /// Ruby truthiness. A comparison already produces a CLR bool; an int/float value is
        /// always truthy.
        /// </summary>
        private MSA.Expression/*!*/ EmitCondition(RExpr/*!*/ node) {
            JT t;
            return EmitCondition(node, out t);
        }

        private MSA.Expression/*!*/ EmitCondition(RExpr/*!*/ node, out JT t) {
            // `a && b' / `a || b' tested for truth: only the truth of the operands matters
            var and = node as AndExpression;
            var or = node as OrExpression;
            if (and != null || or != null) {
                if (++_nodes > MaxNodes) { throw Bail("too many nodes"); }
                t = JT.Bool;
                var l = EmitCondition((and != null) ? and.Left : or.Left);
                PushScope();
                int guard = (and != null) ? PushTruthGuard(and.Left) : -1;
                MSA.Expression r;
                try {
                    r = EmitCondition((and != null) ? and.Right : or.Right);
                } finally {
                    if (guard >= 0) { PopTruthGuard(guard); }
                }
                if (PopScope().Count > 0) { throw JitBailout.Instance; }
                return (and != null) ? Ast.AndAlso(l, r) : Ast.OrElse(l, r);
            }

            var e = Emit(node, out t);
            switch (t) {
                case JT.Bool: return e;
                case JT.Int:
                case JT.Lng:
                case JT.Dbl: _conditionValueLeaves++; return Ast.Block(e, Ast.Constant(true));
                // anything but nil and false is true, and that is not a method call
                case JT.Obj: _conditionValueLeaves++; return Ast.Call(IsTrueMethod, e);
                default: throw Bail("condition of type " + t);
            }
        }

        /// <summary>
        /// `a && b' / `a || b' as a value: a when it decides the outcome - a falsy a for &&, a
        /// truthy one for || - and b otherwise. `n && n + 1' is n + 1, not true, and `n || 7' is n.
        /// The left operand has to be Bool or numeric, as a condition's has (a number is always
        /// truthy); the value's type is what the two outcomes merge to.
        /// </summary>
        private MSA.Expression/*!*/ EmitAndOr(RExpr/*!*/ left, RExpr/*!*/ right, bool isAnd, out JT type) {
            return EmitAndOr(left, right, null, isAnd, out type);
        }

        /// <summary>`left || right', or with `store' (EmitOrAssignment) `left || (left = store's value)'.</summary>
        private MSA.Expression/*!*/ EmitAndOr(RExpr/*!*/ left, SimpleAssignmentExpression/*!*/ store, bool isAnd, out JT type) {
            return EmitAndOr(left, null, store, isAnd, out type);
        }

        private MSA.Expression/*!*/ EmitAndOr(RExpr/*!*/ left, RExpr right, SimpleAssignmentExpression store, bool isAnd, out JT type) {
            JT lt;
            var l = Emit(left, out lt);
            var value = Ast.Variable(ClrType(lt), "#andor");
            MSA.Expression truth;
            switch (lt) {
                case JT.Bool: truth = value; break;
                case JT.Int:
                case JT.Lng:
                case JT.Dbl: truth = Ast.Constant(true); break;
                case JT.Obj: truth = Ast.Call(IsTrueMethod, value); break;
                default: throw Bail("&& / || operand of type " + lt);
            }

            PushScope();
            JT rt;
            MSA.Expression r;
            int guard = isAnd ? PushTruthGuard(left) : -1;
            try {
                r = (store != null) ? EmitAssignment(store, null, true, out rt) : Emit(right, out rt);
            } finally {
                if (guard >= 0) { PopTruthGuard(guard); }
            }
            if (PopScope().Count > 0) { throw JitBailout.Instance; }

            var result = isAnd ? Join(truth, r, rt, value, lt, out type) : Join(truth, value, lt, r, rt, out type);
            return Ast.Block(ClrType(type), new[] { value }, Ast.Assign(value, l), result);
        }

        /// <summary>
        /// `!x', `not x' - a call of x's #!, which the program may have redefined. An object - an
        /// instance variable, say - is speculated to be true, false or nil, whose #! is
        /// BasicObject's while RequireBuiltin says so (a redefinition later invalidates the body):
        /// anything else deopts, as its class may have a #! of its own. The value is read once, so
        /// another thread changing the variable in between cannot slip past the test.
        /// </summary>
        private MSA.Expression/*!*/ EmitNot(RExpr/*!*/ operandNode, out JT type) {
            JT operandType;
            int valueLeaves = _conditionValueLeaves;
            var operand = EmitCondition(operandNode, out operandType);
            // `!(a && b)' calls #! on the value of `a && b', which is b's when a is truthy: only the truth of
            // booleans stands for that value. With an object or a number among the operands the receiver of #!
            // is not known to be true, false or nil, and its class may have a #! of its own.
            if (operandType == JT.Bool && _conditionValueLeaves != valueLeaves) { throw Bail("! of && or || over values that are not booleans"); }
            type = JT.Bool;
            if (operandType == JT.Obj) {
                var value = (operand is MSA.MethodCallExpression truth && truth.Method == IsTrueMethod) ? truth.Arguments[0] : null;
                if (value == null) { throw Bail("! of an object"); }
                RequireBuiltin(JT.Bool, "!");
                if (!IsPublicBuiltin(_context.NilClass, "!")) { throw Bail("operator ! on nil is not the built-in one"); }
                _canDeopt = true;
                return Ast.Call(JitRuntime.M("NotBoolean"), value);
            }
            RequireBuiltin(operandType, "!");
            return Ast.Not(operand);
        }

        // ---- calls -------------------------------------------------------------------------

        /// <summary>
        /// `raise', `raise "m"', `fail Foo, msg' and the like: a call of Kernel#raise (or of whatever self's
        /// class answers to the name) with no receiver and no block. The arguments may be anything at all,
        /// since EmitRaise never emits them.
        /// </summary>
        internal static bool IsRaiseCall(MethodCall/*!*/ node) {
            return node.Target == null && node.Block == null && (node.MethodName == "raise" || node.MethodName == "fail");
        }

        /// <summary>
        /// A `raise' is a deopt: the typed body throws its deopt there, and the generic body runs again from the
        /// top, reaches the same `raise' and makes the call - evaluates its arguments once, builds the exception,
        /// gives it the backtrace of its own frame, sets $!, fires TracePoint's raise event, runs the callers'
        /// ensure and rescue clauses. The typed body constructs nothing and evaluates none of the arguments
        /// (they may have effects: `raise Foo, build_message').
        ///
        /// That is sound whatever `raise' turns out to be when the call is made: the generic body does what the
        /// source does, and a `raise' of self's own that returns is run there and the method goes on. It only
        /// costs: a method whose `raise' is somebody's ordinary method would enter its typed body only to deopt
        /// on that path, until the entry retires the body (JitStub). So the body is declined when the receiver
        /// the profile saw does not answer `raise' with Kernel's, or a refinement of it is active where the
        /// method is written; anything that changes that later moves the method version, which recompiles.
        /// Like every deopt, it is allowed only before anything observable but held-back writes of self's
        /// instance variables (_canDeopt, _deoptAfterEffect): those the generic body makes, and then raises.
        /// </summary>
        private MSA.Expression/*!*/ EmitRaise(MethodCall/*!*/ node, out JT type) {
            if (_loop != null) { throw Bail("raise in an outlined loop"); }
            object sample = (_hints != null) ? _hints.SelfSample : null;
            if (sample != null && !IsBuiltinMethod(JitRuntime.ClassOf(_context, sample), node.MethodName)) {
                throw Bail(node.MethodName + " is not Kernel#" + node.MethodName);
            }
            _canDeopt = true;
            _raises = true;
            type = JT.Never;
            return Ast.Call(JitRuntime.M("RaiseDeopt"));
        }

        /// <summary>
        /// A call whose block is `&amp;nil', or `&amp;k' of the method's block parameter k when the method only passes it on and
        /// the body is compiled for a call without a block (_passedBlock): it passes no block, and is the call without one.
        /// </summary>
        private bool PassesNoBlock(MethodCall/*!*/ node) {
            return _refinements.IsEmpty && JitScreen.PassesNoBlock(node, _passedBlock);
        }

        // `block_given?' in a method (not an outlined loop, which runs in a frame that may have a block)
        private bool IsBlockGiven(MethodCall/*!*/ node) {
            return node.Target == null && node.MethodName == "block_given?" && (node.Arguments == null || node.Arguments.IsEmpty) &&
                node.Block == null && _ast != null && _loop == null;
        }

        /// <summary>
        /// `block_given?' as false, for a body the entry takes only when the call has no block (JitCode.AssumesNoBlock) - and
        /// which a body that inlines it or calls it directly calls without one. Kernel#block_given? is what self's class
        /// resolves the name to now; the entry's method version guard covers a later definition.
        /// </summary>
        private MSA.Expression/*!*/ EmitBlockGiven(out JT type) {
            object sample = (_hints != null) ? _hints.SelfSample : null;
            // (only BuildEntry tests the block: not a keyword method's direct entry, nor a per-count body's)
            if (_keywords != null || _supplied >= 0) { throw Bail("block_given? with optional parameters"); }
            if (sample == null || !_refinements.IsEmpty || !IsBuiltinMethod(JitRuntime.ClassOf(_context, sample), "block_given?")) {
                throw Bail("block_given? is not Kernel's");
            }
            _blockGivenFalse = true;
            type = JT.Bool;
            return Ast.Constant(false);
        }

        private MSA.Expression/*!*/ EmitCall(MethodCall/*!*/ node, out JT type) {
            if (node.Block != null && !PassesNoBlock(node)) {
                // an iterator with a literal block inside a core iterator's outlined loop: run in place (JitCompiler.Nest.cs)
                if (_loop != null && WritesReceivers) { return EmitNestedBlockLoop(node, out type); }
                throw Bail("call with a block: " + node.MethodName);
            }
            var args = node.Arguments;
            int argc = (args == null) ? 0 : args.Expressions.Length;

            // `!x' written as a call (prism's form of `!@x')
            if (node.Target != null && argc == 0 && node.MethodName == "!") {
                return EmitNot(node.Target, out type);
            }

            if (IsBlockGiven(node)) { return EmitBlockGiven(out type); }

            // `raise ...' or `fail ...' (EmitRaise): a deopt, and the generic body raises.
            if (IsRaiseCall(node)) { return EmitRaise(node, out type); }

            // Self-recursion: `fib(n - 1)' inside `def fib'. Sound because the entry stub pins
            // the receiver class and the global method version, so no override can slip in.
            if (_ast != null && _keywords == null && node.Target == null && !node.IsVariableCall && node.MethodName == _ast.Name && argc == _paramNames.Length) {
                var callArgs = new MSA.Expression[argc + 1];
                callArgs[0] = _self;
                for (int i = 0; i < argc; i++) {
                    JT at;
                    var a = Emit(args.Expressions[i], out at);
                    callArgs[i + 1] = Coerce(a, at, _paramTypes[i]);
                }
                type = _selfReturn;
                _selfCall = true;
                var selfCall = Ast.Invoke(
                    Ast.Convert(Ast.Field(Ast.Constant(_selfCell), "Value"), _selfDelegateType),
                    callArgs
                );
                _selfCalls.Add(selfCall);
                return selfCall;
            }

            // `m(a, b)', `self.m(a)', `o.m(a)', m a small pure specialized method (JitCompiler.Inline.cs).
            if (node.Target == null || node.Target is SelfReference || ((node.Target is LocalVariable || node.Target is InstanceVariable) &&
                !JitScreen.IsBinaryOperator(node.MethodName) && node.MethodName != "[]")) {
                var inlined = TryInlineCall(node, out type);
                if (inlined != null) { return inlined; }
            }

            // -X:JITCalls: `norm(x, y)' or `self.norm(x, y)', norm another method of the receiver that has
            // a typed body which could not be made part of this one.
            if (_callees != null && (node.Target == null || node.Target is SelfReference)) {
                var callee = _callees(node.MethodName, argc);
                if (callee != null) { return EmitDirectCall(node, callee, argc, out type); }
            }

            // `x' or `self.x' in an outlined loop, where x is an attr_reader on the loop's self: the
            // instance variable read the reader does, and no call. JitRuntime.ReadSelfAttr checks
            // self's class and the global method version on each read and deopts when either has
            // moved; a read is not observable, so the iteration stays restartable. It finds the
            // reader as a call with no refinements active does, so only a loop written where none
            // are (the copy's prologue checks the loop is still entered so) reads this way.
            if (argc == 0 && _attrProfile != null && _refinements.IsEmpty && (node.Target == null || node.Target is SelfReference)) {
                string variableName = _attrProfile(node.MethodName);
                if (variableName != null) {
                    _canDeopt = true;
                    if (_attrReads == null) { _attrReads = new Dictionary<MethodCall, string>(); }
                    _attrReads[node] = variableName;
                    type = JT.Obj;
                    return Ast.Call(JitRuntime.M("ReadSelfAttr"), Ast.Constant(_context), _self,
                        Ast.Constant(new JitRuntime.SelfAttrSite(node.MethodName, variableName, false)));
                }
            }

            // `o.x' in an outlined loop, o a local holding an object whose x is an attr_reader: the
            // same read, of o as it is in its tuple slot (the loop may not write o), typed by what
            // the variable holds in o right now - a guarded unboxing for an Integer or a Float.
            // A local the loop writes (`b = bodies[j]; b.x') is read from its object slot instead,
            // and what its tuple slot holds now - the value of an iteration run before the loop was
            // compiled - is only a profile: ReadSelfAttr checks each receiver's class anyway. Only in
            // a core iterator's loop (WritesReceivers): a `while' that walks a linked structure
            // (`y = y.parent' in CD's Node#successor) runs a few iterations per entry, which do not
            // pay for the entry of a copy.
            var receiverLocal = node.Target as LocalVariable;
            if (argc == 0 && receiverLocal != null && _peekLocal != null && _refinements.IsEmpty) {
                MSA.ParameterExpression bound;
                JT boundType;
                bool written = TryLookup(Key(receiverLocal), out bound, out boundType);
                if (!written || boundType == JT.Obj && WritesReceivers) {
                    var peek = _peekLocal(receiverLocal);
                    object live = peek.HasValue ? peek.Value.Value : null;
                    if (live == null && written) { live = LoopSample(receiverLocal); }
                    string variableName = (live != null)
                        ? JitRuntime.SelfAttrSite.ReaderVariable(JitRuntime.ClassOf(_context, live), node.MethodName, true) : null;
                    int member = (variableName == null && live is RubyStruct)
                        ? JitRuntime.StructMemberSite.Member(JitRuntime.ClassOf(_context, live), node.MethodName, true, false) : -1;
                    if (member >= 0) {
                        // a Struct member reader: the member's read (JitRuntime.ReadStructMember)
                        _canDeopt = true;
                        if (!written) {
                            if (_peeked == null) { _peeked = new List<LocalVariable>(); }
                            _peeked.Add(receiverLocal);
                        }
                        type = JT.Obj;
                        return Ast.Call(JitRuntime.M("ReadStructMember"), written ? (MSA.Expression)bound : peek.Value.Key,
                            Ast.Constant(new JitRuntime.StructMemberSite(node.MethodName, member, true, false)));
                    }
                    if (variableName != null) {
                        _canDeopt = true;
                        if (!written) {
                            if (_peeked == null) { _peeked = new List<LocalVariable>(); }
                            _peeked.Add(receiverLocal);
                        }
                        MSA.Expression read = Ast.Call(JitRuntime.M("ReadSelfAttr"), Ast.Constant(_context),
                            written ? (MSA.Expression)bound : peek.Value.Key,
                            Ast.Constant(new JitRuntime.SelfAttrSite(node.MethodName, variableName, true)));
                        object value;
                        _context.TryGetInstanceVariable(live, variableName, out value);
                        string guard = (value is int) ? "GuardInt" : (value is long) ? "GuardLong" : (value is double) ? "GuardDouble" : null;
                        if (guard == null) { type = JT.Obj; return read; }
                        type = (value is int) ? JT.Int : (value is long) ? JT.Lng : JT.Dbl;
                        return GuardIVarRead(guard, read);
                    }
                }
            }

            // `o.x(v)', o a local holding an object whose x writes v to an instance variable and does
            // nothing else (an attr_writer called by name, or a method that is one): the store, as
            // for `o.x = v' (EmitAttributeWrite).
            if (argc == 1 && receiverLocal != null && _peekLocal != null && _refinements.IsEmpty && !(args.Expressions[0] is SplattedArgument)) {
                MSA.ParameterExpression bound;
                JT boundType;
                if (!TryLookup(Key(receiverLocal), out bound, out boundType)) {
                    var peek = _peekLocal(receiverLocal);
                    object live = peek.HasValue ? peek.Value.Value : null;
                    if (live is RubyObject && JitRuntime.SelfAttrSite.WriterVariable(JitRuntime.ClassOf(_context, live), node.MethodName, true) != null) {
                        return EmitAttributeWrite(receiverLocal, node.MethodName, args.Expressions[0], out type);
                    }
                }
            }

            // `a[i]', `a.size', `a.length', a a local holding a plain Array that the loop (or a parameter
            // that the method) does not write: loaded once by the entry (JitCompiler.ArrayLocal).
            if (node.Target is LocalVariable) {
                var loaded = ArrayLocalRead(node);
                if (loaded != null) { return EmitArrayRead(loaded.Value, args.Expressions[0], true, out type); }
                if (argc == 0 && (node.MethodName == "size" || node.MethodName == "length") && ArrayMethodIsPlain(false) &&
                    IsPublicBuiltin(ArrayClass, node.MethodName)) {
                    loaded = ArrayLocal(node.Target);
                    if (loaded != null) {
                        type = JT.Int;
                        return Ast.Property(loaded.Value.Key, "Count");
                    }
                }
            }

            // Hash#key? and #fetch, Array/Hash #size on an object (JitCompiler.Objects.cs).
            if (node.Target != null && IsHashQuery(node.MethodName, argc)) {
                return EmitHashQuery(node, out type);
            }

            // `f.to_i' of a Float, and `n.to_i' of an Integer, which is n.
            if (argc == 0 && node.Target != null && node.MethodName == "to_i") {
                JT tt;
                var t = Emit(node.Target, out tt);
                t = NumericIVar(node.Target, t, ref tt);
                if (IsNumeric(tt)) {
                    RequireBuiltin(tt, "to_i");
                    if (tt != JT.Dbl) { type = tt; return t; }
                    _canDeopt = true;
                    type = JT.Lng;
                    return Ast.Call(JitRuntime.M("FloatToLong"), t);
                }
                throw Bail("to_i on " + tt);
            }

            // `n.abs' of an Integer or a Float (Bounce's `@x_vel.abs'): Integer#abs of an Int32 is an
            // Int64 (-2**31 has none in an Int32), of an Int64 the same but for -2**63, whose absolute value is a
            // Bignum the generic code makes (JitRuntime.AbsLong deopts); Float#abs clears the sign bit, so -0.0
            // is 0.0 and a NaN stays one.
            // A receiver the profile saw hold something else (an object with an attr_reader :abs) is left
            // to the calls below, which compile the attribute read.
            if (argc == 0 && node.Target != null && node.MethodName == "abs" && MayBeNumeric(ObservedType(node.Target))) {
                JT tt;
                var t = Emit(node.Target, out tt);
                t = NumericIVar(node.Target, t, ref tt);
                if (IsNumeric(tt)) {
                    RequireBuiltin(tt, "abs");
                    switch (tt) {
                        case JT.Int:
                            type = JT.Lng;
                            return Ast.Call(typeof(Math).GetMethod("Abs", new[] { typeof(long) }), Ast.Convert(t, typeof(long)));
                        case JT.Lng:
                            _canDeopt = true;
                            type = JT.Lng;
                            return Ast.Call(JitRuntime.M("AbsLong"), t);
                        default:
                            type = JT.Dbl;
                            return Ast.Call(typeof(Math).GetMethod("Abs", new[] { typeof(double) }), t);
                    }
                }
                throw Bail("abs on " + tt);
            }

            // `Array.new(n, v)' anywhere in a method (JitCompiler.Objects.cs); before the construction that ends
            // one, which it would otherwise be and which Array is not (no plain construction).
            if (_ast != null && _loop == null && IsArrayConstruction(node)) {
                return EmitArrayConstruction(node, out type);
            }

            // `C.new(a, b)' ending the method (JitCompiler.Objects.cs).
            if (_ast != null && _loop == null && _keywords == null && node == TailConstruction(_ast)) {
                return EmitConstruction(node, out type);
            }

            // `s == "lit"', `s != "lit"' (JitCompiler.Objects.cs).
            if (node.Target != null && argc == 1 && IsStringLiteralComparison(node)) {
                return EmitStringLiteralComparison(node, out type);
            }

            // `s + "lit"', `s << "lit"' (JitCompiler.Objects.cs).
            if (argc == 1 && IsStringLiteralConcat(node)) {
                return EmitStringLiteralConcat(node, out type);
            }

            // `v.nil?' on any object (JitRuntime.IsNil).
            if (node.Target != null && argc == 0 && node.MethodName == "nil?") {
                JT tt;
                var t = Emit(node.Target, out tt);
                return EmitIdentityCall(node.MethodName, Coerce(t, tt, JT.Obj), null, out type);
            }

            // `x', `self.x', `@o.x', `o.x' in a method, x an attr_reader of the receiver (JitCompiler.Objects.cs).
            if (argc == 0 && _loop == null) {
                var read = EmitMethodAttrRead(node, out type);
                if (read != null) { return read; }
            }

            // `Math.sqrt(x)' on a number (JitCompiler.Objects.cs).
            if (argc == 1 && IsMathSqrt(node)) {
                return EmitMathSqrt(node, out type);
            }

            // Binary operators on Fixnum/Float, and Array#[] / Hash#[] on an object.
            if (node.Target != null && argc == 1 && !(args.Expressions[0] is SplattedArgument)) {
                JT lt, rt;
                var l = Emit(node.Target, out lt);
                var r = Emit(args.Expressions[0], out rt);
                // an instance variable the profile saw hold a number is one (NumericIVar), before == is taken for identity
                l = NumericIVar(node.Target, l, ref lt);
                r = NumericIVar(args.Expressions[0], r, ref rt);
                if (node.MethodName == "equal?" || lt == JT.Obj && !IsNumeric(rt) && (node.MethodName == "==" || node.MethodName == "!=") &&
                    !(HoldsIntegerElements(node.Target) && HoldsIntegerElements(args.Expressions[0]))) {
                    // `v == w', `v != w', `v.equal?(w)' on an object (JitRuntime.IsIdentical), which the
                    // profile does not see to be one that has == of its own (a String, a number)
                    if (lt != JT.Obj) { throw Bail(node.MethodName + " on " + lt); }
                    // An expression's value is unknown here, and one of a class with an == of its own (a
                    // number, an Array) would deopt on every call. An instance variable is typed by the receiver
                    // that turned the method hot (ObservedType).
                    Type observed = ObservedType(node.Target);
                    if (observed == typeof(void) && node.Target is ConstantVariable && node.MethodName != "equal?" && _refinements.IsEmpty) {
                        // `NO_WORK == x' (Richards), NO_WORK a constant whose value is nil, which the constant
                        // version guard keeps: NilClass#== (and BasicObject#!=, which calls it) being the public
                        // library methods, checked here and kept by the method version, it is `x is nil', and
                        // cannot deopt
                        var nilClass = KindClass(JitRuntime.KindNil);
                        if (IsPublicBuiltin(nilClass, "==") && (node.MethodName == "==" || IsPublicBuiltin(nilClass, "!="))) {
                            type = JT.Bool;
                            var isNil = Ast.Equal(Coerce(r, rt, JT.Obj), Ast.Constant(null, typeof(object)));
                            return (node.MethodName == "!=") ? Ast.Not(isNil) : isNil;
                        }
                    }
                    if (observed == typeof(MutableString) && node.MethodName != "equal?") {
                        // `@current != ch' (Json's `read_char'): String#== between two Strings whose class is String
                        // itself compares their contents (JitRuntime.StringEqualsString); anything else deopts
                        var stringClass = KindClass(JitRuntime.KindString);
                        if (!IsPlainLibraryMethod(stringClass, "==") || !IsPublic(stringClass, "==") ||
                            node.MethodName == "!=" && (!IsPlainLibraryMethod(stringClass, "!=") || !IsPublic(stringClass, "!="))) {
                            throw Bail("String#" + node.MethodName + " is not the built-in one");
                        }
                        _canDeopt = true;
                        type = JT.Bool;
                        MSA.Expression equal = Ast.Call(JitRuntime.M("StringEqualsString"), l, Coerce(r, rt, JT.Obj));
                        return (node.MethodName == "!=") ? Ast.Not(equal) : equal;
                    }
                    if ((observed == typeof(RubyArray) || observed == typeof(Hash)) && node.MethodName != "equal?" &&
                        args.Expressions[0] is Literal && ((Literal)args.Expressions[0]).Value == null) {
                        return EmitCollectionEqualsNil(node.MethodName, l, out type);
                    }
                    if (observed == null || observed != typeof(RubyObject) && observed != typeof(void) &&
                        observed != typeof(bool) && observed != typeof(RubySymbol)) {
                        throw Bail(node.MethodName + " on a " + ((observed == null) ? "receiver of unknown type" : observed.Name));
                    }
                    return EmitIdentityCall(node.MethodName, l, Coerce(r, rt, JT.Obj), out type);
                }
                if (IsNumeric(lt) && IsNumeric(rt)) {
                    return EmitBinary(node.MethodName, l, lt, r, rt, out type);
                }
                if (node.MethodName == "[]") {
                    return EmitIndexRead(node.Target, l, lt, r, rt, out type);
                }
                if ((lt == JT.Obj && IsNumeric(rt)) || (IsNumeric(lt) && rt == JT.Obj)) {
                    return EmitSpeculatedBinary(node.MethodName, l, lt, node.Target, r, rt, args.Expressions[0], out type);
                }
                if (lt == JT.Obj && rt == JT.Obj && JitScreen.IsBinaryOperator(node.MethodName) &&
                    HoldsIntegerElements(node.Target) && HoldsIntegerElements(args.Expressions[0])) {
                    // `b[i] + a[i]' in an outlined loop, both Arrays of Integers now: the receiver is speculated
                    // to be an Integer, and then the argument is speculated next to it as above.
                    _canDeopt = true;
                    return EmitSpeculatedBinary(node.MethodName, Ast.Call(JitRuntime.M("SpeculateLong"), l), JT.Lng, null,
                        r, rt, args.Expressions[0], out type);
                }
            }

            throw Bail("call: " + node.MethodName + "/" + argc);
        }

        /// <summary>
        /// A call of another method of the receiver, as a call of that method's typed body: no call
        /// site, no entry, and a typed result. JitStub.BindCallee resolved the name from the receiver
        /// class the entry of this body pins, under the method version that entry guards on, so the
        /// call is the one the generic body would make - as long as nothing redefines the method
        /// while this body runs, which a typed body itself never does (it calls nothing but typed
        /// bodies and helpers), but a trap handler run at a loop's safe point or another thread can.
        /// What the callee's entry would have done falls to this body: its arguments arrive as the
        /// types the callee was compiled for, and its deopts and effects become this body's.
        /// </summary>
        private MSA.Expression/*!*/ EmitDirectCall(MethodCall/*!*/ node, JitCallee/*!*/ callee, int argc, out JT type) {
            for (int i = 0; i < argc; i++) {
                if (node.Arguments.Expressions[i] is SplattedArgument) { throw Bail("call with a splat: " + node.MethodName); }
            }
            var code = callee.Code;
            if (code.ReturnType == JT.None || code.ReturnType == JT.Never) { throw Bail("call of a body without a value: " + node.MethodName); }

            var callArgs = new MSA.Expression[argc + 1];
            callArgs[0] = _self;
            for (int i = 0; i < argc; i++) {
                JT at;
                var a = Emit(node.Arguments.Expressions[i], out at);
                callArgs[i + 1] = CoerceArgument(a, at, code.ParameterTypes[i], node.MethodName);
            }

            // A deopt of the callee leaves through this body to its entry, which runs the generic
            // body of this method from the top, and the callee's effects are observable part way
            // through this one: both are recorded here, where the call is, in the order the callee
            // runs them - its deopts come before its effects, or TryCompile would not have accepted
            // it (_deoptAfterEffect).
            if (code.Deopts) { _canDeopt = true; }
            if (code.IvarWrite) {
                // The callee's self is this body's, so its first instance variable write is still
                // the first one that could meet a frozen object - unless this body has already done
                // something elsewhere, as for a write of its own (EmitAssignment).
                if (_effectNotOnSelf) { _canDeopt = true; }
                _ivarWrite = true;
            }
            if (!code.Pure) { _pure = false; }
            if (code.EffectNotOnSelf) { _effectNotOnSelf = true; }
            if (code.UsesConstants) { _usesConstants = true; TakeFoldedConstants(code); }
            if (code.HasLoop) { _calleeLoop = true; }
            // after the arguments, which may read a global themselves
            TakeGlobals(code, node.MethodName);
            _directCalls = true;

            type = code.ReturnType;
            return Ast.Invoke(callee.Target, callArgs);
        }

        /// <summary>
        /// An argument of a direct call, as the type the callee's body was compiled for - which is
        /// its entry's type check moved to the caller. An Integer is one in either representation
        /// and anything can be passed boxed; an object where the callee takes a number is unboxed
        /// behind a guard. No other pair converts: an Integer passed where the profile saw a Float
        /// is not that Float.
        /// </summary>
        private MSA.Expression/*!*/ CoerceArgument(MSA.Expression/*!*/ e, JT from, JT to, string/*!*/ name) {
            if (from == to || from == JT.Never || to == JT.Obj || (IsIntegral(from) && IsIntegral(to))) {
                return Coerce(e, from, to);
            }
            if (from == JT.Obj) {
                switch (to) {
                    case JT.Int: _canDeopt = true; return Ast.Call(JitRuntime.M("GuardInt"), e);
                    case JT.Lng: _canDeopt = true; return Ast.Call(JitRuntime.M("GuardLong"), e);
                    case JT.Dbl: _canDeopt = true; return GuardIVarRead("GuardDouble", e);
                }
            }
            throw Bail("call: " + name + " takes " + to + ", passed " + from);
        }

        private static bool IsIntegral(JT t) {
            return t == JT.Int || t == JT.Lng;
        }

        /// <summary>What <paramref name="self"/>'s instance variables hold now, as types (NumericIVar).</summary>
        internal static Func<string, JT>/*!*/ IVarProfile(RubyContext/*!*/ context, object self) {
            return name => {
                object value;
                if (!context.TryGetInstanceVariable(self, name, out value)) { return JT.Obj; }
                return (value is int) ? JT.Int : (value is long) ? JT.Lng : (value is double) ? JT.Dbl : JT.Obj;
            };
        }

        /// <summary>
        /// `v.nil?', `v == w', `v != w', `v.equal?(w)' where v is any object: the library method's answer,
        /// once the site finds v's class to resolve the name to the library method named here
        /// (JitRuntime.IdentityCallSite), and a deopt otherwise. A refinement of the method active at
        /// the call would be what the call reaches instead, so none may be.
        /// </summary>
        private MSA.Expression/*!*/ EmitIdentityCall(string/*!*/ name, MSA.Expression/*!*/ value, MSA.Expression other, out JT type) {
            if (!_refinements.IsEmpty) { throw Bail(name + " with refinements active"); }
            JitRuntime.IdentityCallSite site;
            if (name == "nil?") {
                site = new JitRuntime.IdentityCallSite(name, LibraryMethods(name, _context.ObjectClass, _context.NilClass), null);
            } else {
                var equals = new JitRuntime.IdentityCallSite("==", LibraryMethods("==", _context.BasicObjectClass, KindClass(JitRuntime.KindNil),
                    KindClass(JitRuntime.KindTrue), KindClass(JitRuntime.KindFalse), KindClass(JitRuntime.KindSymbol)), null);
                site = (name == "==") ? equals : new JitRuntime.IdentityCallSite(name, LibraryMethods(name, _context.BasicObjectClass),
                    (name == "!=") ? equals : null);
            }
            _canDeopt = true;
            type = JT.Bool;
            if (other == null) {
                return Ast.Call(JitRuntime.M("IsNil"), Ast.Constant(_context), value, Ast.Constant(site));
            }
            MSA.Expression test = Ast.Call(JitRuntime.M("IsIdentical"), Ast.Constant(_context), value, other, Ast.Constant(site));
            return (name == "!=") ? Ast.Not(test) : test;
        }

        /// <summary>
        /// `v == nil', `v != nil' with a v the profile saw hold an Array or a Hash (som.rb's `@storage == nil'):
        /// Array#== and Hash#== of an object that is no Array (Hash) are false unless it responds to to_ary
        /// (to_hash), which nil does not while NilClass has neither (rb_ary_equal, rb_hash_equal); a v that is
        /// neither is compared as EmitIdentityCall's IsIdentical would, nil included
        /// (JitRuntime.CollectionEqualsNil).
        /// </summary>
        private MSA.Expression/*!*/ EmitCollectionEqualsNil(string/*!*/ name, MSA.Expression/*!*/ value, out JT type) {
            if (!_refinements.IsEmpty) { throw Bail(name + " with refinements active"); }
            var nilClass = KindClass(JitRuntime.KindNil);
            bool arrays = IsPlainLibraryMethod(ArrayClass, "==") && IsPublic(ArrayClass, "==") && NoConversion(nilClass, "to_ary") &&
                (name == "==" || IsPlainLibraryMethod(ArrayClass, "!=") && IsPublic(ArrayClass, "!="));
            bool hashes = IsPlainLibraryMethod(HashClass, "==") && IsPublic(HashClass, "==") && NoConversion(nilClass, "to_hash") &&
                (name == "==" || IsPlainLibraryMethod(HashClass, "!=") && IsPublic(HashClass, "!="));
            if (!arrays && !hashes) { throw Bail(name + " of nil: neither Array's nor Hash's is the built-in one"); }
            var equals = new JitRuntime.IdentityCallSite("==", LibraryMethods("==", _context.BasicObjectClass, KindClass(JitRuntime.KindNil),
                KindClass(JitRuntime.KindTrue), KindClass(JitRuntime.KindFalse), KindClass(JitRuntime.KindSymbol)), null);
            var site = (name == "==") ? equals : new JitRuntime.IdentityCallSite(name, LibraryMethods(name, _context.BasicObjectClass), equals);
            _canDeopt = true;
            type = JT.Bool;
            MSA.Expression test = Ast.Call(JitRuntime.M("CollectionEqualsNil"), Ast.Constant(_context), value, Ast.Constant(site),
                Ast.Constant(arrays), Ast.Constant(hashes));
            return (name == "!=") ? Ast.Not(test) : test;
        }

        // The library methods `name' resolves to on the classes: the first must be one, the others are
        // left out when they are not.
        private RubyMemberInfo[]/*!*/ LibraryMethods(string/*!*/ name, RubyClass/*!*/ first, params RubyClass[]/*!*/ others) {
            if (!IsBuiltinMethod(first, name)) { throw Bail(first.Name + "#" + name + " is not the built-in one"); }
            var result = new List<RubyMemberInfo> { first.ResolveMethod(name, VisibilityContext.AllVisible).Info };
            foreach (var cls in others) {
                if (IsBuiltinMethod(cls, name)) { result.Add(cls.ResolveMethod(name, VisibilityContext.AllVisible).Info); }
            }
            return result.ToArray();
        }

        private static bool MayBeNumeric(Type observed) {
            return observed == null || observed == typeof(int) || observed == typeof(double) || observed == typeof(System.Numerics.BigInteger);
        }

        private static bool IsNumeric(JT t) {
            return t == JT.Int || t == JT.Lng || t == JT.Dbl;
        }

        /// <summary>
        /// An instance variable read used as a numeric operand - `sum += @x', `@n * 2'. An
        /// outlined loop is compiled with its self at hand, so what the variable holds right now
        /// is a type profile: an Integer or a Float there is read through a guard that unboxes it
        /// while it still is one and deopts when it is not, and the generic loop then does
        /// whatever the operator does with whatever the variable holds. Reads are not
        /// observable, so this keeps a body restartable. Anything else stays an object.
        /// </summary>
        private MSA.Expression/*!*/ NumericIVar(RExpr/*!*/ node, MSA.Expression/*!*/ e, ref JT type) {
            var ivar = node as InstanceVariable;
            string name = (ivar != null) ? ivar.Name : null;
            var call = node as MethodCall;
            if (call != null && _attrReads != null) { _attrReads.TryGetValue(call, out name); }
            if (type != JT.Obj || name == null || _ivarProfile == null) { return e; }
            string guard;
            switch (_ivarProfile(name)) {
                case JT.Int: type = JT.Int; guard = "GuardInt"; break;
                case JT.Lng: type = JT.Lng; guard = "GuardLong"; break;
                case JT.Dbl: type = JT.Dbl; guard = "GuardDouble"; break;
                default: return e;
            }
            _canDeopt = true;
            return GuardIVarRead(guard, e);
        }

        /// <summary>
        /// The guarded unboxing of an instance variable read: GuardDouble (SpeculateDouble) of GetIVar or
        /// ReadSelfAttr becomes their Double (SpeculateDouble) version, which reads a Float the typed code stored raw without boxing it
        /// (RubyInstanceData, "Float locations"). Both deopt on the same values.
        /// </summary>
        internal static MSA.Expression/*!*/ GuardIVarRead(string/*!*/ guard, MSA.Expression/*!*/ read) {
            var call = read as MSA.MethodCallExpression;
            if ((guard == "GuardDouble" || guard == "SpeculateDouble") && call != null && call.Object == null) {
                string suffix = (guard == "GuardDouble") ? "Double" : "SpeculateDouble";
                if (call.Method == JitRuntime.M("GetIVar")) { return Ast.Call(JitRuntime.M("GetIVar" + suffix), call.Arguments); }
                if (call.Method == JitRuntime.M("ReadSelfAttr")) { return Ast.Call(JitRuntime.M("ReadSelfAttr" + suffix), call.Arguments); }
            }
            return Ast.Call(JitRuntime.M(guard), read);
        }

        /// <summary>The two representations of a Ruby Integer the specialized code carries.</summary>
        private static bool IsInteger(JT t) {
            return t == JT.Int || t == JT.Lng;
        }

        /// <summary>
        /// `x = a op b' where a, b and x are all ints. Computing in long and narrowing back is
        /// correct but is a widening, an operation and a range check where one int operation with
        /// one check will do - and that shape is most of every int loop, so it is worth spotting:
        /// an int-typed operand pair under a narrowing back to int fuses into the int helper it
        /// came from. The two deopt on exactly the same values.
        /// </summary>
        private static MSA.Expression FuseIntOp(MSA.Expression/*!*/ e) {
            var bin = e as MSA.BinaryExpression;
            if (bin != null) {
                string name;
                switch (bin.NodeType) {
                    case MSA.ExpressionType.Add: name = "AddInt"; break;
                    case MSA.ExpressionType.Subtract: name = "SubInt"; break;
                    case MSA.ExpressionType.Multiply: name = "MulInt"; break;
                    default: return null;
                }
                var l = Narrowed(bin.Left);
                var r = Narrowed(bin.Right);
                return (l != null && r != null) ? Ast.Call(JitRuntime.M(name), l, r) : null;
            }
            var call = e as MSA.MethodCallExpression;
            if (call != null && call.Object == null && call.Arguments.Count == 2) {
                string name = (call.Method.Name == "DivLong") ? "DivInt"
                    : (call.Method.Name == "ModLong") ? "ModInt" : null;
                if (name == null) { return null; }
                var l = Narrowed(call.Arguments[0]);
                var r = Narrowed(call.Arguments[1]);
                return (l != null && r != null) ? Ast.Call(JitRuntime.M(name), l, r) : null;
            }
            return null;
        }

        /// <summary>The int this long operand was widened from, if it was one.</summary>
        private static MSA.Expression Narrowed(MSA.Expression/*!*/ e) {
            var unary = e as MSA.UnaryExpression;
            if (unary != null && unary.NodeType == MSA.ExpressionType.Convert
                && unary.Type == typeof(long) && unary.Operand.Type == typeof(int)) {
                return unary.Operand;
            }
            var constant = e as MSA.ConstantExpression;
            if (constant != null && constant.Type == typeof(long)) {
                long v = (long)constant.Value;
                if (v >= Int32.MinValue && v <= Int32.MaxValue) { return Ast.Constant((int)v, typeof(int)); }
            }
            return null;
        }

        // ---- the operators the body inlines must be the built-in ones ----------------------
        // Inlining `a + b' as a CLR add is only right while Integer#+ is the library method.
        // GlobalMethodVersion covers a redefinition AFTER specialization - the entry guard then
        // fails - but not one that happened before: a method that first turns hot after
        // `class Integer; def +(o) ... end; end' would otherwise inline the operator the program
        // replaced. So every operator is resolved, at compile time, on the class of its receiver,
        // and must be a library method registered under that very name (which also rejects
        // `alias_method :-, :+', a library method under someone else's name).

        private RubyClass/*!*/ ClassOfType(JT t, bool value) {
            object sample = (t == JT.Dbl) ? (object)0.0 : (t == JT.Bool) ? (object)value : (object)0;
            return JitRuntime.ClassOf(_context, sample);
        }

        private bool IsBuiltinMethod(RubyClass/*!*/ cls, string/*!*/ name) {
            // A refinement of the operator active where the code was written is what its call site would
            // call: that is not the built-in one either.
            var resolved = cls.ResolveMethod(name, VisibilityContext.AllVisible, _refinements);
            var info = resolved.Found ? resolved.Info as RubyLibraryMethodInfo : null;
            if (info == null) { return false; }
            foreach (var member in info.GetMembers()) {
                bool named = false;
                foreach (RubyMethodAttribute a in member.GetCustomAttributes(typeof(RubyMethodAttribute), false)) {
                    if (a.Name == name) { named = true; break; }
                }
                if (!named) { return false; }
            }
            return true;
        }

        /// <summary>The library method, and public: what a call with an explicit receiver reaches.</summary>
        private bool IsPublicBuiltin(RubyClass/*!*/ cls, string/*!*/ name) {
            return IsBuiltinMethod(cls, name) && IsPublic(cls, name);
        }

        // The visibility a call sees: `private :!=' in Array, where the method is BasicObject's, stores a
        // forwarder that ResolveMethod follows to BasicObject#!=, whose own visibility is still public;
        // the lookup reads the forwarder's (RubyModule.GetMethodLookup).
        private static bool IsPublic(RubyClass/*!*/ cls, string/*!*/ name) {
            var lookup = cls.GetMethodLookup(name);
            return lookup.Result.Found && lookup.Visibility == RubyMethodVisibility.Public;
        }

        // RequireBuiltin's answers so far in this compile. IsBuiltinMethod resolves the method and
        // reads custom attributes, and a long arithmetic body asks the same few questions hundreds
        // of times (ruby-bench's 30k_variables: most of the time it took to specialize). A body
        // asks about a handful of (type, operator) pairs, so a list searched from the front does;
        // a Dictionary keyed by a (JT, string) tuple cost the first compile 2-3 ms of instantiating
        // and JIT-compiling the generic code for that key.
        private sealed class BuiltinAnswer {
            internal readonly JT Type;
            internal readonly string/*!*/ Operator;
            internal readonly bool Ok;
            internal readonly BuiltinAnswer Next;
            internal BuiltinAnswer(JT type, string/*!*/ op, bool ok, BuiltinAnswer next) {
                Type = type; Operator = op; Ok = ok; Next = next;
            }
        }
        private BuiltinAnswer _builtinAnswers;

        /// <summary>`op' called on a receiver of type t is the built-in method; bail out if not.</summary>
        private void RequireBuiltin(JT t, string/*!*/ op) {
            BuiltinAnswer answer = _builtinAnswers;
            while (answer != null && (answer.Type != t || !String.Equals(answer.Operator, op))) {
                answer = answer.Next;
            }
            bool ok;
            if (answer != null) {
                ok = answer.Ok;
            } else {
                // public as well: the operator is called with an explicit receiver, so a private
                // one (`class Integer; private :+; end') raises NoMethodError
                ok = (t == JT.Bool)
                    ? IsPublicBuiltin(ClassOfType(t, true), op) && IsPublicBuiltin(ClassOfType(t, false), op)
                    : IsPublicBuiltin(ClassOfType(t, false), op);
                // BasicObject#!= answers by calling ==.
                if (ok && op == "!=") { RequireBuiltin(t, "=="); }
                _builtinAnswers = new BuiltinAnswer(t, op, ok, _builtinAnswers);
            }
            if (!ok) { throw Bail("operator " + op + " on " + t + " is not the built-in one"); }
        }

        private MSA.Expression/*!*/ EmitBinary(string/*!*/ op, MSA.Expression/*!*/ l, JT lt, MSA.Expression/*!*/ r, JT rt, out JT type) {
            JT operand = Unify(lt, rt);
            if (operand == JT.Obj) { throw Bail("operator " + op + " on " + lt + ", " + rt); }
            // Ruby dispatches on the receiver: `1 + 2.0' is Integer#+.
            RequireBuiltin(lt, op);

            if (operand == JT.Dbl) {
                if (IsComparison(op) && (lt == JT.Lng || rt == JT.Lng)) {
                    return EmitIntegerFloatCompare(op, l, lt, r, rt, out type);
                }
                l = Coerce(l, lt, JT.Dbl);
                r = Coerce(r, rt, JT.Dbl);
                switch (op) {
                    case "+": type = JT.Dbl; return Ast.Add(l, r);
                    case "-": type = JT.Dbl; return Ast.Subtract(l, r);
                    case "*": type = JT.Dbl; return Ast.Multiply(l, r);
                    case "/": type = JT.Dbl; return Ast.Call(JitRuntime.M("DivDouble"), l, r);
                    case "%": type = JT.Dbl; return Ast.Call(JitRuntime.M("ModDouble"), l, r);
                    default: return EmitCompare(op, l, r, out type);
                }
            }

            if (op == "<<" || op == ">>") {
                return EmitShift(op, l, lt, r, rt, out type);
            }

            // Integers. `operand' is Int exactly when both sides are, and that is the one case
            // in which the CLR operation cannot overflow a long and so needs no check at all:
            // two Int32s added, subtracted or multiplied in 64 bits always fit. So `int op int'
            // that overflows produces a long rather than deopting, which is what the runtime
            // does too - it is only the step out of Int64 that has nowhere typed left to go.
            bool bothInt = (operand == JT.Int);
            switch (op) {
                case "<": case ">": case "<=": case ">=": case "==": case "!=":
                    // A comparison never overflows, so two ints are compared as ints and a mixed
                    // pair widens to long. Both are exact.
                    return EmitCompare(op, Coerce(l, lt, operand), Coerce(r, rt, operand), out type);

                case "&": case "|": case "^": {
                    // Bitwise operations on integers stay within the range of their operands.
                    type = operand;
                    var bl = Coerce(l, lt, operand);
                    var br = Coerce(r, rt, operand);
                    if (op == "&") { return Ast.And(bl, br); }
                    if (op == "|") { return Ast.Or(bl, br); }
                    return Ast.ExclusiveOr(bl, br);
                }
            }

            l = Coerce(l, lt, JT.Lng);
            r = Coerce(r, rt, JT.Lng);
            type = JT.Lng;
            switch (op) {
                case "+": if (bothInt) { return Ast.Add(l, r); } _canDeopt = true; return Ast.Call(JitRuntime.M("AddLong"), l, r);
                case "-": if (bothInt) { return Ast.Subtract(l, r); } _canDeopt = true; return Ast.Call(JitRuntime.M("SubLong"), l, r);
                case "*": if (bothInt) { return Ast.Multiply(l, r); } _canDeopt = true; return Ast.Call(JitRuntime.M("MulLong"), l, r);
                case "/": _canDeopt = true; return Ast.Call(JitRuntime.M("DivLong"), l, r);
                case "%": _canDeopt = true; return Ast.Call(JitRuntime.M("ModLong"), l, r);
                default: throw Bail("integer operator " + op);
            }
        }

        /// <summary>
        /// Integer#&lt;&lt; and #&gt;&gt; on an integer shift width. A right shift of an int stays an int
        /// (it only loses bits) unless the width is negative; every other case is a long, and a
        /// result past a long deopts, as Integer#&lt;&lt; would hand out a Bignum.
        /// </summary>
        private MSA.Expression/*!*/ EmitShift(string/*!*/ op, MSA.Expression/*!*/ l, JT lt, MSA.Expression/*!*/ r, JT rt, out JT type) {
            if ((lt != JT.Int && lt != JT.Lng) || (rt != JT.Int && rt != JT.Lng)) {
                throw Bail("shift " + op + " on " + lt + ", " + rt);
            }
            _canDeopt = true;
            var width = Coerce(r, rt, JT.Lng);
            if (op == ">>" && lt == JT.Int) {
                type = JT.Int;
                return Ast.Call(JitRuntime.M("ShrInt"), l, width);
            }
            type = JT.Lng;
            return Ast.Call(JitRuntime.M(op == "<<" ? "ShlLong" : "ShrLong"), Coerce(l, lt, JT.Lng), width);
        }

        private MSA.Expression/*!*/ EmitCompare(string/*!*/ op, MSA.Expression/*!*/ l, MSA.Expression/*!*/ r, out JT type) {
            type = JT.Bool;
            switch (op) {
                case "<": return Ast.LessThan(l, r);
                case ">": return Ast.GreaterThan(l, r);
                case "<=": return Ast.LessThanOrEqual(l, r);
                case ">=": return Ast.GreaterThanOrEqual(l, r);
                case "==": return Ast.Equal(l, r);
                case "!=": return Ast.NotEqual(l, r);
                default: throw Bail("float operator " + op);
            }
        }

        private static bool IsComparison(string/*!*/ op) {
            switch (op) {
                case "<": case ">": case "<=": case ">=": case "==": case "!=": return true;
                default: return false;
            }
        }

        /// <summary>`a op b' is `b Mirror(op) a'.</summary>
        private static string/*!*/ Mirror(string/*!*/ op) {
            switch (op) {
                case "<": return ">";
                case ">": return "<";
                case "<=": return ">=";
                case ">=": return "<=";
                default: return op;
            }
        }

        /// <summary>
        /// `x op f' or `f op x', x an Integer carried as a long and f a Float. Integer#==, #&lt; and
        /// the rest, and Float's with an Integer argument, compare the two exactly (MRI's
        /// rb_integer_float_cmp and rb_integer_float_eq) - and a long past 2**53 does not convert to
        /// a double exactly: `2**53 + 1 == 2.0**53' is false, though (double)(2**53 + 1) is 2.0**53.
        /// A long that is a widened int, or a constant within 2**53, does convert exactly, and is
        /// compared as a double. Any other is converted all the same: the nearest double to x orders
        /// against f as x does unless the two are equal (f is a double, so it is at least the next
        /// one up when it is above x's double, which x is short of). Only when they are equal does
        /// JitRuntime.CompareWideLongDouble work out the exact order, which costs the common case
        /// no more than a second test of the flags the first comparison set.
        /// </summary>
        private MSA.Expression/*!*/ EmitIntegerFloatCompare(string/*!*/ op, MSA.Expression/*!*/ l, JT lt, MSA.Expression/*!*/ r, JT rt,
            out JT type) {
            bool integerLeft = (lt == JT.Lng);
            MSA.Expression exact = ExactDouble(integerLeft ? l : r);
            if (exact != null) {
                return integerLeft
                    ? EmitCompare(op, exact, Coerce(r, rt, JT.Dbl), out type)
                    : EmitCompare(op, Coerce(l, lt, JT.Dbl), exact, out type);
            }
            var integer = Ast.Variable(typeof(long), "#integer");
            var number = Ast.Variable(typeof(double), "#float");
            var asDouble = Ast.Variable(typeof(double), "#integerAsDouble");
            var integerValue = Ast.Assign(integer, integerLeft ? l : r);
            var floatValue = Ast.Assign(number, Coerce(integerLeft ? r : l, integerLeft ? rt : lt, JT.Dbl));
            // x's relation to f; on a tie x's double is f, so f is no NaN and the order is -1, 0 or 1
            string relation = integerLeft ? op : Mirror(op);
            MSA.Expression tie = Ast.Equal(asDouble, number);
            MSA.Expression ordered = OrderTest(relation, Ast.Call(JitRuntime.M("CompareWideLongDouble"), integer, number), out type);
            MSA.Expression test;
            switch (relation) {
                case "<": case "<=": test = Ast.OrElse(Ast.LessThan(asDouble, number), Ast.AndAlso(tie, ordered)); break;
                case ">": case ">=": test = Ast.OrElse(Ast.GreaterThan(asDouble, number), Ast.AndAlso(tie, ordered)); break;
                case "==": test = Ast.AndAlso(tie, ordered); break;
                case "!=": test = Ast.OrElse(Ast.NotEqual(asDouble, number), ordered); break;
                default: throw JitBailout.Instance;
            }
            type = JT.Bool;
            return Ast.Block(typeof(bool), new[] { integer, number, asDouble },
                integerLeft ? integerValue : floatValue,
                integerLeft ? floatValue : integerValue,
                Ast.Assign(asDouble, Ast.Convert(integer, typeof(double))),
                test
            );
        }

        /// <summary>A long expression as a double, if the conversion is known to be exact; else null.</summary>
        private static MSA.Expression ExactDouble(MSA.Expression/*!*/ e) {
            var narrowed = Narrowed(e);
            if (narrowed != null) {
                var c = narrowed as MSA.ConstantExpression;
                return (c != null) ? (MSA.Expression)Ast.Constant((double)(int)c.Value, typeof(double)) : Ast.Convert(narrowed, typeof(double));
            }
            var constant = e as MSA.ConstantExpression;
            if (constant != null && constant.Type == typeof(long)) {
                long v = (long)constant.Value;
                if (v >= -(1L << 53) && v <= (1L << 53)) { return Ast.Constant((double)v, typeof(double)); }
            }
            return null;
        }

        /// <summary>
        /// `a op b' from the ordering of a against b (JitRuntime.CompareLongDouble and the like): -1, 0 or
        /// 1, or 2 when the two are unordered (a NaN), which every relation but != is false for.
        /// </summary>
        private static MSA.Expression/*!*/ OrderTest(string/*!*/ op, MSA.Expression/*!*/ order, out JT type) {
            type = JT.Bool;
            switch (op) {
                case "<": return Ast.Equal(order, Ast.Constant(-1));
                case "<=": return Ast.LessThanOrEqual(order, Ast.Constant(0));
                case ">": return Ast.Equal(order, Ast.Constant(1));
                // 0 or 1: -1 and 2 both have a bit other than the lowest set
                case ">=": return Ast.Equal(Ast.And(order, Ast.Constant(~1)), Ast.Constant(0));
                case "==": return Ast.Equal(order, Ast.Constant(0));
                case "!=": return Ast.NotEqual(order, Ast.Constant(0));
                default: throw JitBailout.Instance;
            }
        }

        /// <summary>
        /// Makes the body end with something after its value, so no self-call is the last thing the method does
        /// (#165). RyuJIT turns a call followed by `ret' into a jump when it can (an implicit tail call), and a
        /// self-call made so reuses the frame: `def d(n) = d(n + 1)' became a loop that never ran out of stack and
        /// never raised SystemStackError. The decision is made when the call's IL is read, by looking at the next
        /// instruction, so a GC.KeepAlive in between rules it out on every runtime; the JIT emits no code for it.
        /// </summary>
        private static MSA.Expression/*!*/ KeepOutOfTailPosition(MSA.Expression/*!*/ body, MSA.ParameterExpression/*!*/ self) {
            var keepAlive = Ast.Call(typeof(GC).GetMethod("KeepAlive"), self);
            if (body.Type == typeof(void)) {
                return Ast.Block(body, keepAlive);
            }
            var result = Ast.Variable(body.Type, "#result");
            return Ast.Block(body.Type, new[] { result }, Ast.Assign(result, body), keepAlive, result);
        }

        /// <summary>
        /// Puts the stack check a method's scope would make (#128) before the self-calls of a JIT-compiled body. A JIT
        /// body enters Ruby code that makes no scope only by calling itself - any other call goes through a scope or
        /// another JIT body's check - and a frame needs the check once, before its first such call: one made on every
        /// path to a self-call covers it. So fib's leaves (n < 2) make no check, and the rest one, where a check at
        /// entry cost fib(30) a fifth of its time. Where paths join, what all of them checked counts; a loop, a label,
        /// a try and a nested lambda start from what was checked before them.
        /// </summary>
        private sealed class StackCheckPlacer : MSA.ExpressionVisitor {
            private readonly HashSet<MSA.Expression>/*!*/ _selfCalls;
            private bool _checked;

            internal StackCheckPlacer(HashSet<MSA.Expression>/*!*/ selfCalls) {
                _selfCalls = selfCalls;
            }

            protected override MSA.Expression VisitInvocation(MSA.InvocationExpression node) {
                var result = base.VisitInvocation(node);
                if (_checked || !_selfCalls.Contains(node)) {
                    return result;
                }
                _checked = true;
                return Ast.Block(result.Type, Ast.Call(JitRuntime.M("CheckStack")), result);
            }

            protected override MSA.Expression VisitConditional(MSA.ConditionalExpression node) {
                var test = Visit(node.Test);
                bool before = _checked;
                var ifTrue = Visit(node.IfTrue);
                bool afterTrue = _checked;
                _checked = before;
                var ifFalse = Visit(node.IfFalse);
                _checked &= afterTrue;
                return node.Update(test, ifTrue, ifFalse);
            }

            protected override MSA.Expression VisitBinary(MSA.BinaryExpression node) {
                if (node.NodeType != MSA.ExpressionType.AndAlso && node.NodeType != MSA.ExpressionType.OrElse &&
                    node.NodeType != MSA.ExpressionType.Coalesce) {
                    return base.VisitBinary(node);
                }
                // the right operand may not run
                var left = Visit(node.Left);
                bool before = _checked;
                var result = node.Update(left, VisitAndConvert(node.Conversion, "VisitBinary"), Visit(node.Right));
                _checked = before;
                return result;
            }

            // a goto can reach a label, and a loop's back edge its start, from where nothing was checked
            protected override MSA.Expression VisitLabel(MSA.LabelExpression node) {
                var result = base.VisitLabel(node);
                _checked = false;
                return result;
            }

            protected override MSA.Expression VisitLoop(MSA.LoopExpression node) {
                _checked = false;
                var result = base.VisitLoop(node);
                _checked = false;
                return result;
            }

            protected override MSA.Expression VisitTry(MSA.TryExpression node) {
                bool before = _checked;
                var result = base.VisitTry(node);
                _checked = before;
                return result;
            }

            protected override MSA.Expression VisitSwitch(MSA.SwitchExpression node) {
                bool before = _checked;
                var result = base.VisitSwitch(node);
                _checked = before;
                return result;
            }

            protected override MSA.Expression VisitGoto(MSA.GotoExpression node) {
                bool before = _checked;
                var result = base.VisitGoto(node);
                _checked = before;
                return result;
            }

            protected override MSA.Expression VisitExtension(MSA.Expression node) {
                bool before = _checked;
                var result = base.VisitExtension(node);
                _checked = before;
                return result;
            }

            protected override MSA.Expression VisitLambda<T>(MSA.Expression<T> node) {
                bool before = _checked;
                _checked = false;
                var result = base.VisitLambda(node);
                _checked = before;
                return result;
            }
        }
    }

    /// <summary>
    /// The static pre-screen, asked once per method before it is wrapped: *could* this body ever
    /// compile?
    ///
    /// Why it exists. Without it the only answer comes from TryCompile, which cannot run until
    /// the profiler has seen Threshold calls - and by then the trampoline is the delegate the
    /// runtime cached in RubyMethodBody._delegate and baked into call site rules, so even a
    /// perfect give-up leaves one permanent extra indirect call on a method the JIT never helps.
    /// Most rejections are not about types at all: `def fib(n) ... return n ... end' is declined
    /// only because Emit has no case for ReturnStatement, and no amount of profiling will change
    /// that. Those methods are better off never being wrapped.
    ///
    /// The contract. This walk rejects only what JitCompiler.Emit refuses for reasons that are
    /// visible in the syntax tree: a node kind it does not dispatch on, an operator EmitBinary
    /// does not know, a rescue/else/ensure, a `do' block, a post-test while, a local from an
    /// enclosing scope. Wherever Emit's answer depends on the *types* the profiler will observe -
    /// "is this operand numeric", "is this condition a bool", "did the self-call's return type
    /// settle" - the screen says yes. It is a pre-screen, not a decision: a false yes costs what
    /// the old code always cost, a false no costs a specialization.
    ///
    /// KEEPING IT HONEST. It is directly below Emit because the two have to move together, and
    /// each direction of drift is a statistic rather than silent slowness (IR_JIT_VERBOSE=1):
    ///   - too permissive (the screen passes a body Emit then refuses): the method is wrapped,
    ///     profiled and declined, which is JitRuntime.Rejected, and each one is named on stderr.
    ///     With the screen in place that count should be near zero.
    ///   - too strict (Emit grew a case the screen was not told about): the node kind appears in
    ///     the "screen rejected" histogram printed on the way out, so a newly supported node kind
    ///     sitting at the top of that histogram is the drift, visible without reading any code.
    /// </summary>
    /// <summary>
    /// A value the typed body has already computed, as the right side of an assignment JitCompiler emits itself
    /// (EmitParallelAssignment). Never in a parsed tree, never transformed by the generic compiler.
    /// </summary>
    internal sealed class EmittedValue : RExpr {
        internal readonly MSA.Expression/*!*/ Value;
        internal readonly JT Type;

        internal EmittedValue(MSA.Expression/*!*/ value, JT type)
            : base(Microsoft.Scripting.SourceSpan.None) {
            Value = value;
            Type = type;
        }

        internal override MSA.Expression/*!*/ TransformReadCore(AstGenerator/*!*/ gen) {
            throw new InvalidOperationException("EmittedValue is the method JIT's own");
        }

        protected internal override void Walk(Walker/*!*/ walker) {
        }
    }

    internal sealed class JitScreen {
        private readonly int _scopeDepth;
        private readonly string/*!*/ _methodName;
        private readonly int _arity;
        private int _nodes;

        /// <summary>The node kind that said no, for the histogram.</summary>
        private string _rejectedBy;
        // ... and with the name, when asked for (LastRejectionDetail)
        private string _rejectedDetail;

        // IR_JIT_VERBOSE and the census (JitCensus): what the last rejection was, with the method or variable name.
        // A screen sets it to its own first rejection when it ends, after any screen of a callee it made on the way
        // (whose refusal is that callee's, not this method's).
        [ThreadStatic] internal static string LastRejectionDetail;

        // The census (JitCensus) asks for more than the first refusal: with a list here, the screen of the next
        // method goes on after each one and names them all in it, each once, so that the census can tell a method
        // one thing keeps out from a method that five do. The answer and the first refusal stay what they are
        // without it. A screen of a callee, made while the list is being filled, does not see it.
        [ThreadStatic] internal static List<string> CollectAll;
        private List<string> _all;

        // Locals read anywhere other than straight into an instance variable (`@a = a') or an
        // element (`a[i] = v'), or assigned. A parameter not in here is only ever stored, so it is
        // best carried boxed: unboxing it on entry would only mean boxing it again for each store.
        private readonly HashSet<string>/*!*/ _otherUses = new HashSet<string>();

        // walking a keyword default, which may not assign a local (JitCompiler.EmitKeywordDefaults)
        private bool _inKeywordDefault;

        // the `while'/`until' loops around the node being walked, whose break and next Emit takes
        private int _loops;

        // a define_method body's def, which may read the locals it captured (JitBlockMethod); null for a def
        private readonly JitBlockMethod.BlockMethodDefinition _blockMethod;

        private JitScreen(MethodDeclaration/*!*/ ast, JitKeywords keywords) {
            _scopeDepth = ast.DefinedScope.Depth;
            _blockMethod = ast as JitBlockMethod.BlockMethodDefinition;
            _methodName = ast.Name;
            // a method with keywords is never called directly from its typed body (EmitCall)
            _arity = (keywords != null) ? -1 : ast.Parameters.Mandatory.Length + ast.Parameters.Optional.Length;
            _tailConstruction = (keywords != null) ? null : JitCompiler.TailConstruction(ast);
            if (JitStub.BlockParameterOnlyPassed(ast)) { _passedBlock = ast.Parameters.Block; }
        }

        // the method's block parameter, when it only passes it on (JitCompiler._passedBlock)
        private readonly LocalVariable _passedBlock;

        /// <summary>
        /// `&amp;nil', or `&amp;k' of <paramref name="passedBlock"/>, the block parameter of a method that only passes it on and
        /// whose body is entered only without a block: no block is passed (JitCompiler.PassesNoBlock).
        /// </summary>
        internal static bool PassesNoBlock(MethodCall/*!*/ node, LocalVariable passedBlock) {
            var reference = node.Block as BlockReference;
            if (reference == null) { return false; }
            var literal = reference.Expression as Literal;
            if (literal != null && literal.GetType() == typeof(Literal) && literal.Value == null) { return true; }
            var local = reference.Expression as LocalVariable;
            return local != null && passedBlock != null && local.Name == passedBlock.Name &&
                local.DefinitionLexicalDepth == passedBlock.DefinitionLexicalDepth;
        }

        // the `C.new(a, b)' the method ends with, if it does (JitCompiler.TailConstruction)
        private readonly MethodCall _tailConstruction;

        // Whether a call on self of a name is of a method the typed body may inline (JitStub.IsLeafMethod); null
        // when screening a callee, which may call nothing itself.
        private Func<string, bool, bool> _isLeafCallee;

        // What a constant guarding a statement is now (CanEverCompile's constantTruth); null: not known.
        private Func<ConstantVariable, bool?> _constantTruth;

        /// <summary>
        /// False when no assignment of parameter types could ever make TryCompile succeed for
        /// this body. <paramref name="rejectedBy"/> names the node kind that decided it.
        /// </summary>
        internal static bool CanEverCompile(MethodDeclaration/*!*/ ast, out string rejectedBy) {
            HashSet<string> otherUses;
            return CanEverCompile(ast, null, null, out rejectedBy, out otherUses);
        }

        /// <summary>
        /// <paramref name="otherUses"/>: the locals the body reads other than as the value of an
        /// instance variable or element assignment, or assigns.
        /// <paramref name="constantTruth"/>: what a constant guarding a statement is now (JitCompiler.ConstantGuard): true
        /// or false, or null if it could not be folded; null when the values are not known (the census, the precompiled
        /// library), in which case a guard of one arm is taken for a switch that is off.
        /// </summary>
        internal static bool CanEverCompile(MethodDeclaration/*!*/ ast, JitKeywords keywords, Func<string, bool, bool> isLeafCallee,
            out string rejectedBy, out HashSet<string> otherUses, Func<ConstantVariable, bool?> constantTruth = null) {
            var s = new JitScreen(ast, keywords);
            s._isLeafCallee = isLeafCallee;
            s._constantTruth = constantTruth;
            otherUses = s._otherUses;
            s._all = CollectAll;
            CollectAll = null;
            bool ok;
            try {
                ok = (keywords != null) ? s.WalkMethodBody(ast.Body, keywords.PrologueLength) : s.WalkBody(ast.Body);
                if (ok && keywords == null) {
                    // the defaults a per-count body evaluates (JitCompiler.EmitOmittedDefaults)
                    foreach (var optional in ast.Parameters.Optional) {
                        if (!s.Walk(optional.Right)) { ok = false; break; }
                    }
                } else if (ok && keywords.DefaultExprs != null) {
                    // the keyword defaults the typed body evaluates (JitCompiler.EmitKeywordDefaults)
                    s._inKeywordDefault = true;
                    foreach (var expr in keywords.DefaultExprs) {
                        if (expr != null && !s.Walk(expr)) { ok = false; break; }
                    }
                    s._inKeywordDefault = false;
                }
            } catch (Exception) {
                // A screen that throws must not take the process down with it; it just means the
                // method is not wrapped. (One that went on after a refusal keeps that refusal: what it
                // walked into afterwards, it would not have reached.)
                ok = false;
                if (s._all == null || s._rejectedBy == null) { s._rejectedBy = "screen-error"; }
            }
            if (s._all != null) {
                // going on after a refusal answered yes to it: the answer is the first one's
                if (s._rejectedBy != null) { ok = false; }
                CollectAll = s._all;
            }
            rejectedBy = s._rejectedBy;
            LastRejectionDetail = s._rejectedDetail;
            return ok;
        }

        private bool No(object/*!*/ node) {
            string detail = null;
            if (_rejectedBy == null || _all != null) {
                string kind = node.GetType().Name;
                if (JitRuntime.Verbose || JitCensus.Current != null) {
                    var call = node as MethodCall;
                    var ivar = node as InstanceVariable;
                    var assign = node as SimpleAssignmentExpression;
                    // "MethodCall each (self)", "InstanceVariable @x", "SimpleAssignmentExpression ||= InstanceVariable"
                    detail = (call != null) ? kind + " " + call.MethodName + (call.Target == null ? " (self)" : "")
                        : (ivar != null) ? kind + " " + ivar.Name
                        : (assign != null) ? kind + " " + assign.Operation + "= " + assign.Left.GetType().Name : kind;
                }
                if (_rejectedBy == null) {
                    _rejectedBy = kind;
                    _rejectedDetail = detail;
                }
            }
            if (_all != null) {
                // the census's screen: note it and go on as if it had been taken (CollectAll)
                if (detail != null && !_all.Contains(detail)) { _all.Add(detail); }
                CensusChildren.Screen(this, node as RExpr);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The census's screen goes on into the operands of a node it has refused: a refusal that returns without walking
        /// them (`@x ||= compute', `{k => f(v)}', `"#{f(x)}"', `a..f(b)', the final refusal of a kind the screen does not
        /// know) would otherwise leave their own refusals uncounted, and the node would be reported as the only thing in
        /// the way of a method that has a call behind it. Each child expression is screened as a statement would be. Not
        /// the children of a call (its arguments and block are the call's own question: a call with a block is counted as
        /// one refusal), of a block, lambda or definition (another scope), or of a Body (WalkClausesForCensus). Only with
        /// CollectAll: the real screen has stopped at its first refusal and never gets here.
        /// </summary>
        private sealed class CensusChildren : Walker {
            private readonly JitScreen/*!*/ _screen;
            private readonly RExpr/*!*/ _root;

            private CensusChildren(JitScreen/*!*/ screen, RExpr/*!*/ root) {
                _screen = screen;
                _root = root;
            }

            internal static void Screen(JitScreen/*!*/ screen, RExpr node) {
                if (node == null || node is MethodCall || node is SuperCall || node is YieldCall || node is Body ||
                    node is LambdaDefinition || node is DefinitionExpression) { return; }
                node.Walk(new CensusChildren(screen, node));
            }

            private bool Visit(object/*!*/ node) {
                if (ReferenceEquals(node, _root)) { return true; }
                // An assignment's target is not read: only the expressions inside it are (`a[i] = v', `o.x = v').
                var simple = _root as SimpleAssignmentExpression;
                var parallel = _root as ParallelAssignmentExpression;
                if ((simple != null && ReferenceEquals(node, simple.Left)) || (parallel != null && ReferenceEquals(node, parallel.Left))) {
                    ScreenTarget((LeftValue)node);
                    return false;
                }
                if (node is BlockDefinition || node is LambdaDefinition || node is DefinitionExpression || node is SourceUnitTree) { return false; }
                var expr = node as RExpr;
                if (expr == null) { return true; }      // an argument list, a maplet, a clause: go on to the expressions in it
                _screen.Walk(expr);
                return false;
            }

            private void ScreenTarget(RExpr/*!*/ target) {
                var compound = target as CompoundLeftValue;
                if (compound != null) {
                    foreach (var item in compound.LeftValues) { ScreenTarget(item); }
                    return;
                }
                var element = target as ArrayItemAccess;
                if (element != null) {
                    _screen.Walk(element.Array);
                    foreach (var arg in element.Arguments.Expressions) { _screen.Walk(arg); }
                    return;
                }
                var attribute = target as AttributeAccess;
                if (attribute != null) { _screen.Walk(attribute.Qualifier); }
                // a variable, or a splat of one: nothing is read
            }

            public override bool Enter(SourceUnitTree/*!*/ node) { return Visit(node); }
            public override bool Enter(BlockDefinition/*!*/ node) { return Visit(node); }
            public override bool Enter(BlockReference/*!*/ node) { return Visit(node); }
            public override bool Enter(Body/*!*/ node) { return Visit(node); }
            public override bool Enter(Maplet/*!*/ node) { return Visit(node); }
            public override bool Enter(Parameters/*!*/ node) { return Visit(node); }
            public override bool Enter(Arguments/*!*/ node) { return Visit(node); }
            public override bool Enter(SplattedArgument/*!*/ node) { return Visit(node); }
            public override bool Enter(KeywordArgumentsTest/*!*/ node) { return Visit(node); }
            public override bool Enter(KeywordParameterRead/*!*/ node) { return Visit(node); }
            public override bool Enter(SplatParameterRead/*!*/ node) { return Visit(node); }
            public override bool Enter(KeywordSplatMerge/*!*/ node) { return Visit(node); }
            public override bool Enter(ClassDefinition/*!*/ node) { return Visit(node); }
            public override bool Enter(ModuleDefinition/*!*/ node) { return Visit(node); }
            public override bool Enter(SingletonDefinition/*!*/ node) { return Visit(node); }
            public override bool Enter(MethodDefinition/*!*/ node) { return Visit(node); }
            public override bool Enter(LambdaDefinition/*!*/ node) { return Visit(node); }
            public override bool Enter(AndExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(ArrayConstructor/*!*/ node) { return Visit(node); }
            public override bool Enter(AssignmentExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(IsDefinedExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(BlockExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(CaseExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(ConditionalExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(ForLoopExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(HashConstructor/*!*/ node) { return Visit(node); }
            public override bool Enter(IfExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(Literal/*!*/ node) { return Visit(node); }
            public override bool Enter(StringLiteral/*!*/ node) { return Visit(node); }
            public override bool Enter(SymbolLiteral/*!*/ node) { return Visit(node); }
            public override bool Enter(EncodingExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(MethodCall/*!*/ node) { return Visit(node); }
            public override bool Enter(MatchExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(NotExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(OrExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(RangeExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(RangeCondition/*!*/ node) { return Visit(node); }
            public override bool Enter(RegularExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(RegularExpressionCondition/*!*/ node) { return Visit(node); }
            public override bool Enter(RescueExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(SelfReference/*!*/ node) { return Visit(node); }
            public override bool Enter(StringConstructor/*!*/ node) { return Visit(node); }
            public override bool Enter(SuperCall/*!*/ node) { return Visit(node); }
            public override bool Enter(UnlessExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(WhileLoopExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(YieldCall/*!*/ node) { return Visit(node); }
            public override bool Enter(ArrayItemAccess/*!*/ node) { return Visit(node); }
            public override bool Enter(AttributeAccess/*!*/ node) { return Visit(node); }
            public override bool Enter(ClassVariable/*!*/ node) { return Visit(node); }
            public override bool Enter(CompoundLeftValue/*!*/ node) { return Visit(node); }
            public override bool Enter(ConstantVariable/*!*/ node) { return Visit(node); }
            public override bool Enter(AstGlobalVariable/*!*/ node) { return Visit(node); }
            public override bool Enter(InstanceVariable/*!*/ node) { return Visit(node); }
            public override bool Enter(LocalVariable/*!*/ node) { return Visit(node); }
            public override bool Enter(Placeholder/*!*/ node) { return Visit(node); }
            public override bool Enter(RegexMatchReference/*!*/ node) { return Visit(node); }
            public override bool Enter(MemberAssignmentExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(ParallelAssignmentExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(SimpleAssignmentExpression/*!*/ node) { return Visit(node); }
            public override bool Enter(AliasStatement/*!*/ node) { return Visit(node); }
            public override bool Enter(ShutdownHandlerStatement/*!*/ node) { return Visit(node); }
            public override bool Enter(UndefineStatement/*!*/ node) { return Visit(node); }
            public override bool Enter(BreakStatement/*!*/ node) { return Visit(node); }
            public override bool Enter(NextStatement/*!*/ node) { return Visit(node); }
            public override bool Enter(RedoStatement/*!*/ node) { return Visit(node); }
            public override bool Enter(RetryStatement/*!*/ node) { return Visit(node); }
            public override bool Enter(ReturnStatement/*!*/ node) { return Visit(node); }
            public override bool Enter(RescueClause/*!*/ node) { return Visit(node); }
            public override bool Enter(WhenClause/*!*/ node) { return Visit(node); }
            public override bool Enter(ElseIfClause/*!*/ node) { return Visit(node); }
        }

        /// <summary>Mirrors JitCompiler.EmitBody.</summary>
        private bool WalkBody(Body/*!*/ body) {
            if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                return No(body) && WalkClausesForCensus(body, 0);
            }
            return WalkStatements(body.Statements);
        }

        /// <summary>
        /// The census's screen (CollectAll) goes on into a body it has refused for its clauses: what the protected part, the
        /// rescue clauses (their class lists too), else and ensure hold would be refused as well, and a refusal of the body
        /// alone must mean that taking the clauses lets the method through. The real screen stops at No and never gets here.
        /// </summary>
        private bool WalkClausesForCensus(Body/*!*/ body, int prologueLength) {
            if (_all == null) { return false; }
            for (int i = prologueLength; i < body.Statements.Count; i++) { Walk(body.Statements[i]); }
            if (body.RescueClauses != null) {
                foreach (var clause in body.RescueClauses) {
                    foreach (var type in clause.Types) { Walk(type); }
                    WalkStatements(clause.Statements);
                }
            }
            WalkStatements(body.ElseStatements);
            WalkStatements(body.EnsureStatements);
            return true;
        }

        /// <summary>Mirrors JitCompiler.EmitBody of a method with keywords: the prologue is not emitted.</summary>
        private bool WalkMethodBody(Body/*!*/ body, int prologueLength) {
            if (body.RescueClauses != null || body.ElseStatements != null || body.EnsureStatements != null) {
                return No(body) && WalkClausesForCensus(body, prologueLength);
            }
            for (int i = prologueLength; i < body.Statements.Count; i++) {
                if (!Walk(body.Statements[i])) { return false; }
            }
            return true;
        }

        private bool WalkStatements(Statements statements) {
            if (statements == null) { return true; }
            foreach (var s in statements) {
                if (!Walk(s)) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Mirrors JitCompiler.EmitCondition, which takes an operand of any type: an object is tested
        /// for Ruby truthiness, which is not a method call.
        /// </summary>
        private bool WalkCondition(RExpr/*!*/ node) {
            return Walk(node);
        }

        /// <summary>
        /// An operand of a binary operator, or the right side of `x op= v': EmitCall and
        /// EmitAssignment only go on when both sides are numeric, and self or a nil/true/false
        /// literal never is. An instance variable may be: `@count + 1' is typed by what the
        /// receiver's variable held when the method turned hot (JitCompiler.NumericIVar), and an
        /// unprofiled one is speculated to be a number next to one that is.
        /// </summary>
        private bool WalkOperand(RExpr/*!*/ node) {
            if (node is AstGlobalVariable || node is SelfReference || node is StringLiteral) { return No(node); }
            var literal = node as Literal;
            if (literal != null && !(literal.Value is int || literal.Value is long || literal.Value is double)) { return No(node); }
            // a Hash query is a Bool, never a number (an element read is speculated to be one)
            var call = node as MethodCall;
            if (call != null && call.Target != null && IsBoolQueryName(call.MethodName)) { return No(node); }
            // nor is `s + "lit"' or `s << "lit"' (JitCompiler.EmitStringLiteralConcat): a String (`@user + ':' + @password')
            if (call != null && JitCompiler.IsStringLiteralConcat(call)) { return No(node); }
            // nor is anything holding a String literal (`suffixes - [".rb"]'): such an operator only deopts
            _inOperand++;
            try {
                return Walk(node);
            } finally {
                _inOperand--;
            }
        }

        // walking an operand (WalkOperand), where a String literal is refused
        private int _inOperand;

        /// <summary>`a[i]', `h.fetch(k)': an object, whatever the types the profile sees.</summary>
        private static bool IsElementRead(RExpr/*!*/ node) {
            var call = node as MethodCall;
            return call != null && call.Target != null && (call.MethodName == "[]" || call.MethodName == "fetch");
        }

        private static bool IsBoolQueryName(string/*!*/ name) {
            return name == "key?" || name == "has_key?" || name == "empty?";
        }

        /// <summary>
        /// Mirrors JitCompiler.Emit, case for case and in the same order. Read the two side by
        /// side; that is the point of them being in one file.
        /// </summary>
        private bool Walk(RExpr/*!*/ node) {
            // Emit gives up past MaxNodes. The screen's count is not exactly Emit's, so it only
            // rules out a body that is too big by any counting - the walk stays bounded either way.
            if (++_nodes > 2 * JitCompiler.MaxNodes) { return No(node); }
            // Nor one nested deeper than this thread's stack goes: interpreting it is fine (see StackGuard).
            if (!StackGuard.IsSufficient) { return No(node); }

            var literal = node as Literal;
            if (literal != null) {
                object v = literal.Value;
                return (v is int || v is long || v is double || v is bool || v == null) || No(node);
            }
            if (node is SymbolLiteral) { return true; }
            // a String literal is any other object (JitCompiler.EmitStringLiteral), never an operand (WalkOperand)
            if (node is StringLiteral) { return _inOperand == 0 || No(node); }
            // and so is an interpolated String (JitCompiler.EmitInterpolation), whose parts are any values
            var interpolation = node as StringConstructor;
            if (interpolation != null) {
                if (interpolation.Kind != StringKind.Mutable || _inOperand != 0) { return No(node); }
                foreach (var part in interpolation.Parts) {
                    if (part is StringLiteral) { continue; }
                    // Not a call (`#{self.class}', `#{name}'), unless a binary operator: a call on self is compiled only as an
                    // attribute read, and the object of any other is one whose to_s the conversion leaves to the generic body.
                    var partCall = part as MethodCall;
                    if (partCall != null && !(partCall.Target != null && partCall.Block == null && IsBinaryOperator(partCall.MethodName))) { return No(part); }
                    if (!Walk(part)) { return false; }
                    // The part's conversion can deopt, after whatever was written before it, the part included: such a method
                    // would be wrapped to be declined (ERB#set_eoutvar's `compiler.put_cmd = "#{eoutvar}.<<"', twice).
                    if (_effectWalked) { return No(node); }
                }
                return true;
            }

            var local = node as LocalVariable;
            if (local != null) {
                // A local from an enclosing scope is never read from a typed body, but by a define_method
                // body that captured it (JitBlockMethod). (Emit also bails on a read before any assignment,
                // which is a flow property: say yes.)
                _otherUses.Add(local.Name);
                return local.DefinitionLexicalDepth == _scopeDepth || _blockMethod != null && _blockMethod.GetCaptured(local) != null || No(node);
            }

            var assign = node as SimpleAssignmentExpression;
            if (assign != null) {
                bool ok = WalkAssignment(assign);
                // a write anywhere but to a local of the method is an effect, after which an interpolation is refused
                if (!(assign.Left is LocalVariable)) { _effectWalked = true; }
                return ok;
            }

            var call = node as MethodCall;
            if (call != null) { return WalkCall(call); }

            var cond = node as ConditionalExpression;
            if (cond != null) {
                return WalkCondition(cond.Condition) && Walk(cond.TrueExpression) && Walk(cond.FalseExpression);
            }

            // `x if C', `x unless C', `if C then x else y end' (JitCompiler.TryEmitConstantGuarded): only the arm the
            // constant's value leaves alive is emitted. Not knowing the value, a guard of one arm is a switch that is
            // off (`trace(x) if TRACING'), as it is where the census meets it; a guard of two arms has both walked.
            ConstantVariable guard;
            Statements whenTrue, whenFalse;
            if (JitRuntime.DeadConstants && JitCompiler.ConstantGuard(node, out guard, out whenTrue, out whenFalse)) {
                if (_constantTruth != null) {
                    bool? truth = _constantTruth(guard);
                    if (truth.HasValue) { return WalkStatements(truth.Value ? whenTrue : whenFalse); }
                } else if (whenTrue == null || whenFalse == null) {
                    return true;
                }
            }

            var ifExpr = node as IfExpression;
            if (ifExpr != null) {
                var clauses = ifExpr.ElseIfClauses;
                if (clauses != null) {
                    for (int i = 0; i < clauses.Count; i++) {
                        var c = clauses[i];
                        if (c.Condition == null) {
                            // Only the last clause may be the `else'.
                            if (i != clauses.Count - 1) { return No(c); }
                        } else if (!WalkCondition(c.Condition)) {
                            return false;
                        }
                        if (!WalkStatements(c.Statements)) { return false; }
                    }
                }
                return WalkCondition(ifExpr.Condition) && WalkStatements(ifExpr.Body);
            }

            var unless = node as UnlessExpression;
            if (unless != null) {
                var els = unless.ElseClause;
                if (els != null) {
                    if (els.Condition != null) { return No(els); }
                    if (!WalkStatements(els.Statements)) { return false; }
                }
                return WalkCondition(unless.Condition) && WalkStatements(unless.Statements);
            }

            var not = node as NotExpression;
            if (not != null) { return WalkCondition(not.Expression); }

            var and = node as AndExpression;
            if (and != null) { return WalkCondition(and.Left) && WalkCondition(and.Right); }

            var or = node as OrExpression;
            if (or != null) { return WalkCondition(or.Left) && WalkCondition(or.Right); }

            if (node is InstanceVariable || node is AstGlobalVariable || node is SelfReference) { return true; }

            var loop = node as WhileLoopExpression;
            if (loop != null) {
                if (loop.IsPostTest) { return No(node); }
                _loops++;
                try {
                    return WalkCondition(loop.Condition) && WalkStatements(loop.Statements);
                } finally {
                    _loops--;
                }
            }

            // A jump of the loop around it (JitCompiler.EmitBreak); one anywhere else is a block's, whose body the
            // census's screen may walk on into after refusing the call.
            var brk = node as BreakStatement;
            if (brk != null) {
                if (_loops == 0) { return No(node); }
                var brkArgs = brk.Arguments;
                if (brkArgs == null || brkArgs.Expressions.Length == 0) { return true; }
                if (brkArgs.Expressions.Length != 1 || brkArgs.Expressions[0] is SplattedArgument) { return No(node); }
                return Walk(brkArgs.Expressions[0]);
            }
            var nxt = node as NextStatement;
            if (nxt != null) { return (_loops != 0 && nxt.Arguments == null) || No(node); }

            // `return' / `return v'. Emit's ReturnStatement case lands in a parallel change; the
            // screen already says yes so that the two merge into a working whole. Until then a
            // method with a `return' is wrapped and declined, which is what it always was. A
            // multi-value return builds an Array, which no typed body produces.
            var ret = node as ReturnStatement;
            if (ret != null) {
                var retArgs = ret.Arguments;
                if (retArgs == null || retArgs.Expressions.Length == 0) { return true; }
                if (retArgs.Expressions.Length != 1) { return No(node); }
                return Walk(retArgs.Expressions[0]);
            }

            // `a, b = x, y' into locals and instance variables (JitCompiler.EmitParallelAssignment)
            var parallel = node as ParallelAssignmentExpression;
            if (parallel != null) {
                if (!JitCompiler.IsListAssignment(parallel, _scopeDepth) || _inKeywordDefault) { return No(node); }
                foreach (var value in parallel.Right) {
                    if (!Walk(value)) { return false; }
                }
                foreach (var target in parallel.Left.LeftValues) {
                    var targetLocal = target as LocalVariable;
                    if (targetLocal != null) { _otherUses.Add(targetLocal.Name); }
                }
                return true;
            }

            // `{k => v}' with plain literal keys (JitCompiler.EmitHashLiteral): any other object, never an operand
            var hashLiteral = node as HashConstructor;
            if (hashLiteral != null) {
                if (hashLiteral.IsKeywordArguments || hashLiteral.Maplets.Length > JitCompiler.MaxArrayLiteral || _inOperand != 0 ||
                    !JitCompiler.IsPlainLiteralKeyed(hashLiteral)) { return No(node); }
                foreach (var maplet in hashLiteral.Maplets) {
                    if (!Walk(maplet.Key) || !Walk(maplet.Value)) { return false; }
                }
                return true;
            }

            var arrayLiteral = node as ArrayConstructor;
            if (arrayLiteral != null) {
                var items = arrayLiteral.Arguments.Expressions;
                if (items.Length > JitCompiler.MaxArrayLiteral) { return No(node); }
                foreach (var item in items) {
                    if (!Walk(item)) { return false; }
                }
                return true;
            }

            // break/next are only emitted in loop mode, which a method body never is, so they
            // fall through to the rejection below along with every other unknown node.

            var body = node as Body;
            if (body != null) { return WalkBody(body); }

            var constant = node as ConstantVariable;
            if (constant != null) { return WalkConstant(constant) || No(node); }

            var caseExpr = node as CaseExpression;
            if (caseExpr != null) { return WalkCase(caseExpr); }

            var sequence = node as BlockExpression;
            if (sequence != null) { return WalkStatements(sequence.Statements); }

            return No(node);
        }

        /// <summary>Mirrors JitCompiler.EmitAssignment and the writes it leads to.</summary>
        private bool WalkAssignment(SimpleAssignmentExpression/*!*/ assign) {
            RExpr node = assign;
            string op = assign.Operation;
            if (op == "&&" || op == "||") {
                // `@x ||= v', `x ||= v' (and `&&=') are `@x || (@x = v)' (JitCompiler.EmitOrAssignment);
                // every other target stays out. So does a value that is a call (`@x ||= compute_x',
                // `@x ||= @o.find'): a memo's work is the callee's, which in the census and in traced runs is
                // rarely a body the JIT has compiled, and this one is then declined (it pays for the stub on every
                // call). An operator (`a &&= a + 1') is not such a call. Nor is a memo of nil or false worth one:
                // `@x ||= nil' stores only to turn false into nil or to define the variable, an effect that keeps
                // what follows it from deopting. The memos it takes are of other literals, `[]', constants,
                // operators and the method's own variables.
                var valueCall = assign.Right as MethodCall;
                if (valueCall != null && !(valueCall.Target != null && valueCall.Block == null && IsBinaryOperator(valueCall.MethodName))) { return No(node); }
                var valueLiteral = assign.Right as Literal;
                if (valueLiteral != null && (valueLiteral.Value == null || false.Equals(valueLiteral.Value))) { return No(node); }
                var orTarget = assign.Left;
                if (orTarget is InstanceVariable) { return Walk(assign.Right); }
                var orLocal = orTarget as LocalVariable;
                if (orLocal == null || orLocal.DefinitionLexicalDepth != _scopeDepth || _inKeywordDefault) { return No(node); }
                _otherUses.Add(orLocal.Name);
                return Walk(assign.Right);
            }
            // `x op= v' becomes `x = x op v', so an operator EmitBinary does not know is out.
            if (op != null && !IsBinaryOperator(op)) { return No(node); }

            // `a[i] = v', `a[i] op= v' (JitCompiler.EmitIndexWrite)
            var element = assign.Left as ArrayItemAccess;
            if (element != null) {
                var indexArgs = element.Arguments.Expressions;
                if (element.Block != null || indexArgs.Length != 1 || indexArgs[0] is SplattedArgument) { return No(node); }
                if (!Walk(element.Array) || !Walk(indexArgs[0])) { return false; }
                if (op != null) { return WalkOperand(assign.Right); }
                // a local stored as the element is only stored, as it is into an instance variable
                var storedElement = assign.Right as LocalVariable;
                if (storedElement != null && storedElement.DefinitionLexicalDepth == _scopeDepth) { ++_nodes; return true; }
                return Walk(assign.Right);
            }

            var ivarTarget = assign.Left as InstanceVariable;
            if (ivarTarget != null) {
                if (op != null) { return WalkOperand(assign.Right); }
                var stored = assign.Right as LocalVariable;
                if (stored != null && stored.DefinitionLexicalDepth == _scopeDepth) { ++_nodes; return true; }
                return Walk(assign.Right);
            }
            // `@o.x = v', `o.x = v', `self.x = v' in a method (JitCompiler.EmitAttributeWrite)
            var attribute = assign.Left as AttributeAccess;
            if (attribute != null) {
                if (op != null || attribute.IsSafeNavigation || _inKeywordDefault) { return No(node); }
                var q = attribute.Qualifier;
                if (!(q is SelfReference || q is InstanceVariable || q is LocalVariable) || !Walk(q)) { return No(node); }
                return Walk(assign.Right);
            }
            // `$x = v' (JitCompiler.EmitGlobalWrite)
            if (assign.Left is AstGlobalVariable) {
                if (op != null || _inKeywordDefault) { return No(node); }
                return Walk(assign.Right);
            }
            var target = assign.Left as LocalVariable;
            if (target == null || target.DefinitionLexicalDepth != _scopeDepth || _inKeywordDefault) { return No(node); }
            _otherUses.Add(target.Name);
            return (op != null) ? WalkOperand(assign.Right) : Walk(assign.Right);
        }

        // An assignment of something other than a local of the method was walked (Walk): an effect, which the walk's
        // order puts before what is walked after it.
        private bool _effectWalked;

        /// <summary>A constant JitCompiler.ResolveConstant may fold (WalkConstant).</summary>
        internal static bool IsFoldableConstant(ConstantVariable/*!*/ node) {
            return WalkConstant(node);
        }

        /// <summary>Mirrors JitCompiler.ResolveConstant: `C', `::C', and `A::B' qualified by constants.</summary>
        private static bool WalkConstant(ConstantVariable/*!*/ node) {
            for (RExpr q = node.Qualifier; q != null; q = ((ConstantVariable)q).Qualifier) {
                if (!(q is ConstantVariable)) { return false; }
            }
            return true;
        }

        /// <summary>Mirrors JitCompiler.EmitCase and EmitWhenTest.</summary>
        private bool WalkCase(CaseExpression/*!*/ node) {
            bool hasSubject = node.Value != null;
            if (hasSubject && !Walk(node.Value)) { return false; }
            foreach (var when in node.WhenClauses) {
                if (when.Comparisons.Length == 0) { return No(when); }
                foreach (var comparison in when.Comparisons) {
                    if (++_nodes > 2 * JitCompiler.MaxNodes) { return No(node); }
                    if (hasSubject ? !IsWhenTest(comparison) : !WalkCondition(comparison)) { return No(comparison); }
                }
                if (!WalkStatements(when.Statements)) { return false; }
            }
            return WalkStatements(node.ElseStatements);
        }

        /// <summary>The comparisons EmitWhenTest turns into tests.</summary>
        private static bool IsWhenTest(RExpr/*!*/ node) {
            var constant = node as ConstantVariable;
            if (constant != null) { return WalkConstant(constant); }
            var literal = node as Literal;
            if (literal != null) { return literal.Value is int || literal.Value == null || literal.Value is bool; }
            if (node is SymbolLiteral || node is StringLiteral) { return true; }
            var range = node as RangeExpression;
            if (range != null) { return IsRangeBound(range.Begin) && IsRangeBound(range.End); }
            return false;
        }

        private static bool IsRangeBound(RExpr/*!*/ node) {
            var literal = node as Literal;
            if (literal != null) { return literal.Value is int; }
            var constant = node as ConstantVariable;
            return constant != null && WalkConstant(constant);
        }

        /// <summary>Mirrors JitCompiler.EmitCall: a self-recursive call, a binary operator, an element read or a Hash query.</summary>
        private bool WalkCall(MethodCall/*!*/ node) {
            if (node.Block != null && !PassesNoBlock(node, _passedBlock)) { return No(node); }
            // `raise ...': a deopt, whose arguments are never emitted (JitCompiler.EmitRaise)
            if (JitCompiler.IsRaiseCall(node)) { return true; }
            var args = node.Arguments;
            int argc = (args == null) ? 0 : args.Expressions.Length;

            if (node.Target != null && ((node.MethodName == "[]" && argc == 1) || JitCompiler.IsHashQuery(node.MethodName, argc))) {
                if (!Walk(node.Target)) { return false; }
                if (args != null) {
                    foreach (var arg in args.Expressions) {
                        if (arg is SplattedArgument || !Walk(arg)) { return No(node); }
                    }
                }
                return true;
            }

            if (node == _tailConstruction || JitCompiler.IsArrayConstruction(node)) {
                // `C.new(a, b)' ending the method (JitCompiler.EmitConstruction), or `Array.new(n, v)'
                // anywhere in it (JitCompiler.EmitArrayConstruction)
                if (args != null) {
                    foreach (var arg in args.Expressions) {
                        if (!Walk(arg)) { return false; }
                    }
                }
                return true;
            }

            if (node.Target != null && argc == 0 && node.MethodName == "!") {
                // `!x' (JitCompiler.EmitNot)
                return WalkCondition(node.Target);
            }

            // `v.nil?', `v == w', `v != w', `v.equal?(w)' on any object (JitCompiler.EmitIdentityCall); `=='
            // and `!=' between numbers stay EmitBinary's, and `s == "lit"' is EmitStringLiteralComparison's,
            // which this must not pre-empt (a String literal is no operand Walk takes).
            if (node.Target != null && ((argc == 0 && node.MethodName == "nil?") ||
                argc == 1 && !JitCompiler.IsStringLiteralComparison(node) && (node.MethodName == "equal?" || node.MethodName == "==" || node.MethodName == "!=") &&
                !(args.Expressions[0] is SplattedArgument) && !IsElementRead(node.Target) && !IsElementRead(args.Expressions[0]))) {
                return Walk(node.Target) && (argc == 0 || Walk(args.Expressions[0]));
            }

            if (argc == 0 && (node.Target == null || node.Target is SelfReference || node.Target is InstanceVariable ||
                node.Target is LocalVariable) && !(node.Target == null && node.MethodName == _methodName && _arity == 0)) {
                // `x', `self.x', `@o.x', `o.x': an attr_reader read if the profile finds one (JitCompiler.EmitMethodAttrRead)
                return node.Target == null || Walk(node.Target) || No(node);
            }

            if (argc == 1 && JitCompiler.IsMathSqrt(node)) {
                // `Math.sqrt(x)' (JitCompiler.EmitMathSqrt): x is a number, or an object speculated to be one
                return WalkOperand(args.Expressions[0]);
            }

            if ((node.Target == null && !node.IsVariableCall || node.Target is SelfReference) && node.MethodName != _methodName ||
                (node.Target is LocalVariable || node.Target is InstanceVariable) && !IsBinaryOperator(node.MethodName) && node.MethodName != "[]") {
                // `m(a)', `self.m(a)', `o.m(a)': a call the emitter inlines if m is a small pure specialized
                // method when it compiles (JitCompiler.TryInlineCall), which only the profile can say for sure.
                // Taken only of a method the declaring module resolves the name to and whose body JitCode.Inlinable can
                // accept as far as the syntax tells (JitStub.IsLeafMethod): taken of anything, many methods that are declined
                // in the end would be wrapped, which costs them (Richards' add_input_and_check_priority calling `append').
                // A call on an instance variable has no class to resolve on here; admitting it whatever the callee bought
                // DeltaBlue's choose_method, and cost seven methods wrapped and declined and no measurable time.
                if (_isLeafCallee == null || !_isLeafCallee(node.MethodName, node.Target == null || node.Target is SelfReference)) {
                    return No(node);
                }
                if (argc > JitStub.MaxArity || node.Target != null && !Walk(node.Target)) { return No(node); }
                for (int i = 0; i < argc; i++) {
                    var arg = args.Expressions[i];
                    // Nor a String literal argument (`read_char('.')'): a callee that takes one is rarely a body the JIT has
                    // compiled, and with it taken AWFY's Json binds `read_char' and declines read_number, read_null and four
                    // more, each then paying for its stub on every call.
                    if (arg is StringLiteral && !(arg is SymbolLiteral) || arg is StringConstructor) { return No(arg); }
                    if (arg is SplattedArgument || !Walk(arg)) { return No(node); }
                }
                return true;
            }

            if (node.Target == null) {
                if (node.IsVariableCall || node.MethodName != _methodName || argc != _arity) { return No(node); }
            } else if (argc == 1 && JitCompiler.IsStringLiteralComparison(node)) {
                // `s == "lit"' or `"lit" == s' (JitCompiler.EmitStringLiteralComparison): s is any object
                return Walk(JitCompiler.IsReversedStringLiteralComparison(node) ? args.Expressions[0] : node.Target);
            } else if (argc == 1 && JitCompiler.IsStringLiteralConcat(node)) {
                // `s + "lit"', `s << "lit"' (JitCompiler.EmitStringLiteralConcat): s is any object; a String, so never
                // inside an operand of a binary operator, which only deopts on one (as a String literal there)
                return (_inOperand == 0) ? Walk(node.Target) : No(node);
            } else if (argc != 1 || !IsBinaryOperator(node.MethodName)) {
                // Emit only reaches EmitBinary for a one-argument call on a target, and only
                // EmitBinary's own operators survive there whatever the operand types are.
                return No(node);
            } else {
                // an object operand is speculated to be a number, but only next to one that is
                if (IsElementRead(node.Target) && IsElementRead(args.Expressions[0])) { return No(node); }
                return WalkOperand(node.Target) && WalkOperand(args.Expressions[0]);
            }

            // The self-call: each argument is coerced to its parameter's profiled type, which is
            // a question about types, so any argument Emit can emit at all will do.
            for (int i = 0; i < argc; i++) {
                if (args.Expressions[i] is SplattedArgument) { return No(node); }
                if (!Walk(args.Expressions[i])) { return false; }
            }
            return true;
        }

        /// <summary>The operators JitCompiler.EmitBinary and EmitCompare between them handle.</summary>
        internal static bool IsBinaryOperator(string/*!*/ op) {
            switch (op) {
                case "+": case "-": case "*": case "/": case "%":
                case "<": case ">": case "<=": case ">=": case "==": case "!=":
                case "&": case "|": case "^": case "<<": case ">>":
                    return true;
                default:
                    return false;
            }
        }
    }
}
