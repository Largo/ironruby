using System;
using System.Collections.Generic;
using System.Numerics;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Pm = IronRuby.Prism.Ast;

namespace IronRuby.Prism {
    /// <summary>
    /// Maps prism's AST (typed nodes decoded from the binary serialization by the
    /// generated PrismLoader) onto IronRuby.Compiler.Ast, so the existing
    /// AstGenerator/DLR pipeline compiles it unchanged. Modern syntax that the
    /// 1.9-era AST has no nodes for (safe navigation, keyword arguments, `it`)
    /// is lowered to equivalent constructs; unmapped nodes raise
    /// NotSupportedException naming the prism node type.
    /// </summary>
    public sealed class PrismAstBridge {
        private readonly string/*!*/ _source;
        private readonly string _path;
        private readonly List<int>/*!*/ _lineStarts;
        private readonly RubyEncoding/*!*/ _encoding;
        private readonly Stack<LexicalScope>/*!*/ _scopes = new Stack<LexicalScope>();
        private int _tempCounter;

        private PrismAstBridge(string/*!*/ source, string path, RubyEncoding/*!*/ encoding) {
            _source = source;
            _path = path;
            _encoding = encoding;
            _lineStarts = new List<int> { 0 };
            for (int i = 0; i < source.Length; i++) {
                if (source[i] == '\n') _lineStarts.Add(i + 1);
            }
        }

        public static SourceUnitTree Parse(SourceUnit/*!*/ sourceUnit, RubyCompilerOptions/*!*/ options, ErrorSink/*!*/ errorSink) {
            return ParseText(sourceUnit.GetCode(), sourceUnit.Path, options.LocalNames, sourceUnit, errorSink);
        }

        public static SourceUnitTree ParseText(string/*!*/ code, string path) {
            return ParseText(code, path, null, null, null);
        }

        public static SourceUnitTree ParseText(string/*!*/ code, string path, List<string> outerLocalNames,
            SourceUnit sourceUnit, ErrorSink errorSink) {

            var bridge = new PrismAstBridge(code, path, RubyEncoding.UTF8);
            PrismParseResult result = PrismParser.Parse(code, path, 1, outerLocalNames);

            if (result.Errors.Count > 0) {
                if (errorSink != null && sourceUnit != null) {
                    foreach (var error in result.Errors) {
                        errorSink.Add(sourceUnit, error.Message, bridge.Span(error.Location), 0, Severity.FatalError);
                    }
                    return null;
                }
                var first = result.Errors[0];
                throw new NotSupportedException(
                    $"syntax error: {first.Message} (line {bridge.Location(first.Location.Start).Line})");
            }

            return bridge.Program((Pm.ProgramNode)result.Root, outerLocalNames);
        }

        // ---- helpers ----

        private SourceSpan Span(Pm.PmNode node) {
            return new SourceSpan(Location(node.StartOffset), Location(node.StartOffset + node.Length));
        }

        private SourceSpan Span(Pm.PmLocation location) {
            return new SourceSpan(Location(location.Start), Location(location.Start + location.Length));
        }

        private SourceLocation Location(int index) {
            int line = _lineStarts.BinarySearch(index);
            if (line < 0) line = ~line - 1;
            if (index > _source.Length) index = _source.Length;
            return new SourceLocation(index, line + 1, index - _lineStarts[line] + 1);
        }

        private Exception Unsupported(Pm.PmNode node) {
            var span = Span(node);
            return new NotSupportedException(
                $"Prism bridge: node type '{node.GetType().Name}' is not supported yet (line {span.Start.Line})");
        }

        private LexicalScope/*!*/ CurrentScope {
            get { return _scopes.Peek(); }
        }

        private static bool HasFlag(Pm.PmNode node, uint flag) {
            return (node.Flags & flag) != 0;
        }

        // regex literals become $_ matches, ranges become flip-flops, etc.
        private Expression/*!*/ Condition(Pm.PmNode/*!*/ predicate) {
            return Expr(predicate).ToCondition(CurrentScope);
        }

        // ---- program / statements ----

        private SourceUnitTree/*!*/ Program(Pm.ProgramNode/*!*/ node, List<string> outerLocalNames) {
            var scope = new TopStaticLexicalScope(
                outerLocalNames != null ? new RuntimeLexicalScope(outerLocalNames) : null);
            _scopes.Push(scope);
            var statements = BuildStatements(node.Statements);
            _scopes.Pop();
            return new SourceUnitTree(scope, statements, null, _encoding, -1);
        }

        private Statements/*!*/ BuildStatements(Pm.PmNode statementsNode) {
            var result = new Statements();
            if (statementsNode is Pm.StatementsNode statements) {
                foreach (var statement in statements.Body) {
                    result.Add(Expr(statement));
                }
            } else if (statementsNode != null) {
                result.Add(Expr(statementsNode));
            }
            return result;
        }

        private Expression/*!*/ StatementsAsExpression(Pm.PmNode statementsNode, SourceSpan span) {
            var statements = BuildStatements(statementsNode);
            if (statements.Count == 1) {
                foreach (var s in statements) return s;
            }
            if (statements.Count == 0) {
                return Literal.Nil(span);
            }
            return new BlockExpression(statements, span);
        }

        // ---- expression dispatch ----

        private Expression/*!*/ Expr(Pm.PmNode/*!*/ node) {
            var span = Span(node);
            switch (node) {
                case Pm.IntegerNode integer:
                    return BigIntegerLiteral(integer.Value, span);
                case Pm.FloatNode floatNode:
                    return Literal.Double(floatNode.Value, span);
                case Pm.RationalNode rational:
                    return new MethodCall(null, "Rational", new Arguments(new Expression[] {
                        BigIntegerLiteral(rational.Numerator, span), BigIntegerLiteral(rational.Denominator, span)
                    }), span);
                case Pm.ImaginaryNode imaginary:
                    return new MethodCall(null, "Complex", new Arguments(new Expression[] {
                        Literal.Integer(0, span), Expr(imaginary.Numeric)
                    }), span);
                case Pm.StringNode str:
                    return new StringLiteral(str.Unescaped, _encoding, span);
                case Pm.SymbolNode symbol:
                    return new SymbolLiteral(symbol.Unescaped, _encoding, span);
                case Pm.TrueNode _: return Literal.True(span);
                case Pm.FalseNode _: return Literal.False(span);
                case Pm.NilNode _: return Literal.Nil(span);
                case Pm.SelfNode _: return new SelfReference(span);

                case Pm.ParenthesesNode parens:
                    return StatementsAsExpression(parens.Body, span);
                case Pm.StatementsNode statements:
                    return StatementsAsExpression(statements, span);
                case Pm.ImplicitNode implicitNode:
                    return Expr(implicitNode.Value); // hash shorthand {x:} / omitted values
                case Pm.ShareableConstantNode shareable:
                    return Expr(shareable.Write);

                case Pm.ArrayNode array: {
                    var items = new List<Expression>();
                    foreach (var element in array.Elements) items.Add(Argument(element));
                    return new ArrayConstructor(new Arguments(items.ToArray()), span);
                }
                case Pm.HashNode hash:
                    return HashExpression(hash.Elements, span);
                case Pm.KeywordHashNode keywordHash:
                    return HashExpression(keywordHash.Elements, span);
                case Pm.FlipFlopNode flipFlop: {
                    var rangeExpr = new RangeExpression(
                        flipFlop.Left != null ? Expr(flipFlop.Left) : Literal.Nil(span),
                        flipFlop.Right != null ? Expr(flipFlop.Right) : Literal.Nil(span),
                        HasFlag(flipFlop, Pm.RangeFlags.ExcludeEnd), span);
                    return rangeExpr.ToCondition(CurrentScope);
                }
                case Pm.RangeNode range:
                    return new RangeExpression(
                        range.Left != null ? Expr(range.Left) : Literal.Nil(span),
                        range.Right != null ? Expr(range.Right) : Literal.Nil(span),
                        HasFlag(range, Pm.RangeFlags.ExcludeEnd), span);

                case Pm.InterpolatedStringNode interp:
                    return new StringConstructor(StringParts(interp.Parts), StringKind.Mutable, span);
                case Pm.InterpolatedSymbolNode interpSymbol:
                    return new StringConstructor(StringParts(interpSymbol.Parts), StringKind.Symbol, span);
                case Pm.XStringNode xstr:
                    return new StringConstructor(
                        new List<Expression> { new StringLiteral(xstr.Unescaped, _encoding, span) },
                        StringKind.Command, span);
                case Pm.InterpolatedXStringNode interpX:
                    return new StringConstructor(StringParts(interpX.Parts), StringKind.Command, span);
                case Pm.EmbeddedStatementsNode embedded:
                    return StatementsAsExpression(embedded.Statements, span);

                case Pm.RegularExpressionNode regex:
                    return new RegularExpression(
                        new List<Expression> { new StringLiteral(regex.Unescaped, _encoding, span) },
                        RegexOptions(regex), false, span);
                case Pm.MatchLastLineNode matchLast:
                    return new RegularExpression(
                        new List<Expression> { new StringLiteral(matchLast.Unescaped, _encoding, span) },
                        RegexOptions(matchLast), true, span);
                case Pm.InterpolatedRegularExpressionNode interpRegex:
                    return new RegularExpression(StringParts(interpRegex.Parts), RegexOptions(interpRegex), span);
                case Pm.InterpolatedMatchLastLineNode interpMatchLast:
                    return new RegularExpression(StringParts(interpMatchLast.Parts), RegexOptions(interpMatchLast), true, span);

                case Pm.CallNode call: return Call(call, span);

                case Pm.LocalVariableReadNode read:
                    return CurrentScope.ResolveOrAddVariable(read.Name, span);
                case Pm.ItLocalVariableReadNode _:
                    return CurrentScope.ResolveOrAddVariable("it", span);
                case Pm.LocalVariableWriteNode write:
                    return new SimpleAssignmentExpression(
                        CurrentScope.ResolveOrAddVariable(write.Name, span), Expr(write.Value), null, span);
                case Pm.LocalVariableOperatorWriteNode opWrite:
                    return new SimpleAssignmentExpression(
                        CurrentScope.ResolveOrAddVariable(opWrite.Name, span), Expr(opWrite.Value), opWrite.BinaryOperator, span);
                case Pm.LocalVariableOrWriteNode orWrite:
                    return new SimpleAssignmentExpression(
                        CurrentScope.ResolveOrAddVariable(orWrite.Name, span), Expr(orWrite.Value), "||", span);
                case Pm.LocalVariableAndWriteNode andWrite:
                    return new SimpleAssignmentExpression(
                        CurrentScope.ResolveOrAddVariable(andWrite.Name, span), Expr(andWrite.Value), "&&", span);

                case Pm.InstanceVariableReadNode ivarRead:
                    return new InstanceVariable(ivarRead.Name, span);
                case Pm.InstanceVariableWriteNode ivarWrite:
                    return new SimpleAssignmentExpression(
                        new InstanceVariable(ivarWrite.Name, span), Expr(ivarWrite.Value), null, span);
                case Pm.InstanceVariableOperatorWriteNode ivarOp:
                    return new SimpleAssignmentExpression(
                        new InstanceVariable(ivarOp.Name, span), Expr(ivarOp.Value), ivarOp.BinaryOperator, span);
                case Pm.InstanceVariableOrWriteNode ivarOr:
                    return new SimpleAssignmentExpression(
                        new InstanceVariable(ivarOr.Name, span), Expr(ivarOr.Value), "||", span);
                case Pm.InstanceVariableAndWriteNode ivarAnd:
                    return new SimpleAssignmentExpression(
                        new InstanceVariable(ivarAnd.Name, span), Expr(ivarAnd.Value), "&&", span);

                case Pm.GlobalVariableReadNode gvarRead:
                    return new IronRuby.Compiler.Ast.GlobalVariable(gvarRead.Name.TrimStart('$'), span);
                case Pm.GlobalVariableWriteNode gvarWrite:
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(gvarWrite.Name.TrimStart('$'), span), Expr(gvarWrite.Value), null, span);
                case Pm.GlobalVariableOperatorWriteNode gvarOp:
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(gvarOp.Name.TrimStart('$'), span), Expr(gvarOp.Value), gvarOp.BinaryOperator, span);
                case Pm.GlobalVariableOrWriteNode gvarOr:
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(gvarOr.Name.TrimStart('$'), span), Expr(gvarOr.Value), "||", span);
                case Pm.GlobalVariableAndWriteNode gvarAnd:
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(gvarAnd.Name.TrimStart('$'), span), Expr(gvarAnd.Value), "&&", span);

                case Pm.ClassVariableReadNode cvarRead:
                    return new ClassVariable(cvarRead.Name, span);
                case Pm.ClassVariableWriteNode cvarWrite:
                    return new SimpleAssignmentExpression(
                        new ClassVariable(cvarWrite.Name, span), Expr(cvarWrite.Value), null, span);
                case Pm.ClassVariableOperatorWriteNode cvarOp:
                    return new SimpleAssignmentExpression(
                        new ClassVariable(cvarOp.Name, span), Expr(cvarOp.Value), cvarOp.BinaryOperator, span);
                case Pm.ClassVariableOrWriteNode cvarOr:
                    return new SimpleAssignmentExpression(
                        new ClassVariable(cvarOr.Name, span), Expr(cvarOr.Value), "||", span);
                case Pm.ClassVariableAndWriteNode cvarAnd:
                    return new SimpleAssignmentExpression(
                        new ClassVariable(cvarAnd.Name, span), Expr(cvarAnd.Value), "&&", span);

                case Pm.ConstantReadNode constRead:
                    return new ConstantVariable(constRead.Name, span);
                case Pm.ConstantPathNode constPath:
                    return ConstantPath(constPath, span);
                case Pm.ConstantWriteNode constWrite:
                    return new SimpleAssignmentExpression(
                        new ConstantVariable(constWrite.Name, span), Expr(constWrite.Value), null, span);
                case Pm.ConstantOperatorWriteNode constOp:
                    return new SimpleAssignmentExpression(
                        new ConstantVariable(constOp.Name, span), Expr(constOp.Value), constOp.BinaryOperator, span);
                case Pm.ConstantOrWriteNode constOr:
                    return new SimpleAssignmentExpression(
                        new ConstantVariable(constOr.Name, span), Expr(constOr.Value), "||", span);
                case Pm.ConstantAndWriteNode constAnd:
                    return new SimpleAssignmentExpression(
                        new ConstantVariable(constAnd.Name, span), Expr(constAnd.Value), "&&", span);
                case Pm.ConstantPathWriteNode constPathWrite:
                    return new SimpleAssignmentExpression(
                        ConstantPath(constPathWrite.Target, Span(constPathWrite.Target)), Expr(constPathWrite.Value), null, span);
                case Pm.ConstantPathOrWriteNode constPathOr:
                    return new SimpleAssignmentExpression(
                        ConstantPath(constPathOr.Target, Span(constPathOr.Target)), Expr(constPathOr.Value), "||", span);
                case Pm.ConstantPathAndWriteNode constPathAnd:
                    return new SimpleAssignmentExpression(
                        ConstantPath(constPathAnd.Target, Span(constPathAnd.Target)), Expr(constPathAnd.Value), "&&", span);
                case Pm.ConstantPathOperatorWriteNode constPathOp:
                    return new SimpleAssignmentExpression(
                        ConstantPath(constPathOp.Target, Span(constPathOp.Target)), Expr(constPathOp.Value), constPathOp.BinaryOperator, span);

                case Pm.IfNode ifNode: return If(ifNode, span);
                case Pm.UnlessNode unless: {
                    ElseIfClause elseClause = null;
                    if (unless.ElseClause is Pm.ElseNode elseNode) {
                        elseClause = new ElseIfClause(null, BuildStatements(elseNode.Statements), Span(elseNode));
                    }
                    return new UnlessExpression(Condition(unless.Predicate), BuildStatements(unless.Statements), elseClause, span);
                }
                case Pm.WhileNode whileNode:
                    return new WhileLoopExpression(Condition(whileNode.Predicate), true,
                        HasFlag(whileNode, Pm.LoopFlags.BeginModifier), BuildStatements(whileNode.Statements), span);
                case Pm.UntilNode untilNode:
                    return new WhileLoopExpression(Condition(untilNode.Predicate), false,
                        HasFlag(untilNode, Pm.LoopFlags.BeginModifier), BuildStatements(untilNode.Statements), span);

                case Pm.AndNode and:
                    return new AndExpression(Expr(and.Left), Expr(and.Right), span);
                case Pm.OrNode or:
                    return new OrExpression(Expr(or.Left), Expr(or.Right), span);

                case Pm.CaseNode caseNode: {
                    var whens = new List<WhenClause>();
                    foreach (var condition in caseNode.Conditions) {
                        var when = (Pm.WhenNode)condition;
                        var comparisons = new List<Expression>();
                        foreach (var comparison in when.Conditions) comparisons.Add(Argument(comparison));
                        whens.Add(new WhenClause(comparisons.ToArray(), BuildStatements(when.Statements), Span(when)));
                    }
                    Statements elseStatements = null;
                    if (caseNode.ElseClause is Pm.ElseNode caseElse) {
                        elseStatements = BuildStatements(caseElse.Statements);
                    }
                    return new CaseExpression(
                        caseNode.Predicate != null ? Expr(caseNode.Predicate) : null,
                        whens.ToArray(), elseStatements, span);
                }

                case Pm.BeginNode begin: return BuildBeginBody(begin, span, null);
                case Pm.RescueModifierNode rescueMod:
                    return new RescueExpression(Expr(rescueMod.Expression), Expr(rescueMod.RescueExpression),
                        Span(rescueMod.RescueExpression), span);

                case Pm.DefinedNode defined:
                    return new IsDefinedExpression(Expr(defined.Value), span);

                case Pm.CaseMatchNode caseMatch: return CaseMatch(caseMatch, span);
                case Pm.MatchPredicateNode matchPredicate: {
                    // value in pattern  =>  true/false
                    Expression assign;
                    var temp = NewTemp(Expr(matchPredicate.Value), span, out assign);
                    return new BlockExpression(MakeStatements(new Expression[] {
                        assign,
                        new ConditionalExpression(PatternTest(matchPredicate.Pattern, temp, span),
                            Literal.True(span), Literal.False(span), span)
                    }), span);
                }
                case Pm.MatchRequiredNode matchRequired: {
                    // value => pattern  =>  nil, raises NoMatchingPatternError on mismatch
                    Expression assign;
                    var temp = NewTemp(Expr(matchRequired.Value), span, out assign);
                    return new BlockExpression(MakeStatements(new Expression[] {
                        assign,
                        new UnlessExpression(PatternTest(matchRequired.Pattern, temp, span),
                            new Statements(RaiseNoMatchingPattern(temp, span)), null, span)
                    }), span);
                }

                case Pm.BackReferenceReadNode backRef: {
                    switch (backRef.Name.TrimStart('$')) {
                        case "&": return new RegexMatchReference(0, span);
                        case "~": return new RegexMatchReference(-1, span);
                        case "+": return new RegexMatchReference(-2, span);
                        case "`": return new RegexMatchReference(-3, span);
                        case "'": return new RegexMatchReference(-4, span);
                        default: throw Unsupported(node);
                    }
                }
                case Pm.NumberedReferenceReadNode numberedRef:
                    return new RegexMatchReference((int)numberedRef.Number, span);
                case Pm.MatchWriteNode matchWrite: {
                    var call = (Pm.CallNode)matchWrite.Call;
                    if (!(Expr(call.Receiver) is RegularExpression regex)) throw Unsupported(node);
                    foreach (var target in matchWrite.Targets) {
                        CurrentScope.ResolveOrAddVariable(((Pm.LocalVariableTargetNode)target).Name, Span(target));
                    }
                    var arguments = (Pm.ArgumentsNode)call.Arguments;
                    return new MatchExpression(regex, Expr(arguments.Arguments[0]), span);
                }

                case Pm.MultiWriteNode multiWrite:
                    return new ParallelAssignmentExpression(
                        CompoundTarget(multiWrite.Lefts, multiWrite.Rest, multiWrite.Rights), RhsFromValue(multiWrite.Value), span);

                case Pm.CallOperatorWriteNode callOp:
                    return new MemberAssignmentExpression(Expr(callOp.Receiver), callOp.ReadName, callOp.BinaryOperator,
                        Expr(callOp.Value), span);
                case Pm.CallOrWriteNode callOr:
                    return new MemberAssignmentExpression(Expr(callOr.Receiver), callOr.ReadName, "||", Expr(callOr.Value), span);
                case Pm.CallAndWriteNode callAnd:
                    return new MemberAssignmentExpression(Expr(callAnd.Receiver), callAnd.ReadName, "&&", Expr(callAnd.Value), span);

                case Pm.IndexOperatorWriteNode indexOp:
                    return new SimpleAssignmentExpression(
                        new ArrayItemAccess(Expr(indexOp.Receiver), BuildArguments(indexOp.Arguments), null, span),
                        Expr(indexOp.Value), indexOp.BinaryOperator, span);
                case Pm.IndexOrWriteNode indexOr:
                    return new SimpleAssignmentExpression(
                        new ArrayItemAccess(Expr(indexOr.Receiver), BuildArguments(indexOr.Arguments), null, span),
                        Expr(indexOr.Value), "||", span);
                case Pm.IndexAndWriteNode indexAnd:
                    return new SimpleAssignmentExpression(
                        new ArrayItemAccess(Expr(indexAnd.Receiver), BuildArguments(indexAnd.Arguments), null, span),
                        Expr(indexAnd.Value), "&&", span);

                case Pm.DefNode def: return Def(def, span);
                case Pm.ClassNode classNode: return Class(classNode, span);
                case Pm.ModuleNode module: return Module(module, span);
                case Pm.SingletonClassNode singleton: {
                    // the singleton expression (class << expr) is evaluated in the outer scope
                    var singletonExpr = Expr(singleton.Expression);
                    var scope = new TopLocalDefinitionLexicalScope(CurrentScope);
                    _scopes.Push(scope);
                    try {
                        return new SingletonDefinition(scope, singletonExpr, DefinitionBody(singleton.Body, span, null), span);
                    } finally {
                        _scopes.Pop();
                    }
                }
                case Pm.LambdaNode lambda: {
                    var scope = new BlockLexicalScope(CurrentScope);
                    _scopes.Push(scope);
                    try {
                        Statements prologue;
                        var parameters = BlockParameters(lambda.Parameters, false, out prologue);
                        var body = BlockBody(lambda.Body, span, prologue);
                        return new LambdaDefinition(new BlockDefinition(scope, parameters, body, span));
                    } finally {
                        _scopes.Pop();
                    }
                }

                case Pm.ReturnNode ret:
                    return new ReturnStatement(OptionalArguments(ret.Arguments), span);
                case Pm.BreakNode brk:
                    return new BreakStatement(OptionalArguments(brk.Arguments), span);
                case Pm.NextNode next:
                    return new NextStatement(OptionalArguments(next.Arguments), span);
                case Pm.RetryNode _: return new RetryStatement(span);
                case Pm.RedoNode _: return new RedoStatement(span);
                case Pm.YieldNode yield:
                    return new YieldCall(yield.Arguments != null ? BuildArguments(yield.Arguments) : null, span);

                case Pm.SuperNode super: {
                    Block superBlock = OptionalBlock(super.Block);
                    var superArgs = super.Arguments != null
                        ? BuildArguments(super.Arguments, ref superBlock)
                        : new Arguments();
                    return new SuperCall(superArgs, superBlock, span);
                }
                case Pm.ForwardingSuperNode forwardingSuper:
                    return new SuperCall(null,
                        forwardingSuper.Block != null ? BlockDef((Pm.BlockNode)forwardingSuper.Block) : null, span);

                case Pm.ForNode forNode: {
                    // index and collection belong to the outer scope; only the body is
                    // parsed inside the padding scope (matches the legacy grammar)
                    var target = Target(forNode.Index);
                    var clv = target as CompoundLeftValue ?? new CompoundLeftValue(new[] { target });
                    Parameters parameters = clv.HasUnsplattedValue
                        ? new Parameters(RemoveAt(clv.LeftValues, clv.UnsplattedValueIndex), clv.UnsplattedValueIndex,
                            null, clv.UnsplattedValue, null, SourceSpan.None)
                        : new Parameters(clv.LeftValues, clv.LeftValues.Length, null, null, null, SourceSpan.None);
                    var collection = Expr(forNode.Collection);
                    var forScope = new PaddingLexicalScope(CurrentScope);
                    _scopes.Push(forScope);
                    try {
                        return new ForLoopExpression(forScope, parameters, collection,
                            BuildStatements(forNode.Statements), span);
                    } finally {
                        _scopes.Pop();
                    }
                }

                case Pm.AliasMethodNode aliasMethod:
                    return new AliasStatement(true, Symbol(aliasMethod.NewName), Symbol(aliasMethod.OldName), span);
                case Pm.AliasGlobalVariableNode aliasGlobal:
                    return new AliasStatement(false,
                        new ConstructedSymbol(GlobalAliasName(aliasGlobal.NewName)),
                        new ConstructedSymbol(GlobalAliasName(aliasGlobal.OldName)), span);
                case Pm.UndefNode undef: {
                    var names = new List<ConstructedSymbol>();
                    foreach (var name in undef.Names) names.Add(Symbol(name));
                    return new UndefineStatement(names, span);
                }

                case Pm.PostExecutionNode postExec: {
                    var scope = new PaddingLexicalScope(CurrentScope);
                    _scopes.Push(scope);
                    try {
                        return new ShutdownHandlerStatement(scope, BuildStatements(postExec.Statements), span);
                    } finally {
                        _scopes.Pop();
                    }
                }
                case Pm.PreExecutionNode preExec:
                    // BEGIN{}: run inline (correct unless code precedes it textually, which is rare)
                    return StatementsAsExpression(preExec.Statements, span);

                case Pm.SourceFileNode sourceFile:
                    return new StringLiteral(_path ?? sourceFile.Filepath, _encoding, span);
                case Pm.SourceLineNode _:
                    return Literal.Integer(span.Start.Line, span);
                case Pm.SourceEncodingNode _:
                    return new EncodingExpression(span);

                default:
                    throw Unsupported(node);
            }
        }

        private Expression/*!*/ BigIntegerLiteral(BigInteger value, SourceSpan span) {
            if (value >= int.MinValue && value <= int.MaxValue) return Literal.Integer((int)value, span);
            return Literal.BigInteger(value, span);
        }

        private string/*!*/ GlobalAliasName(Pm.PmNode/*!*/ node) {
            switch (node) {
                case Pm.GlobalVariableReadNode gvar: return gvar.Name.TrimStart('$');
                case Pm.BackReferenceReadNode backRef: return backRef.Name.TrimStart('$');
                case Pm.NumberedReferenceReadNode numbered: return numbered.Number.ToString();
                default: throw Unsupported(node);
            }
        }

        private List<Expression>/*!*/ StringParts(Pm.PmNode[]/*!*/ parts) {
            var result = new List<Expression>();
            foreach (var part in parts) {
                switch (part) {
                    case Pm.StringNode str:
                        result.Add(new StringLiteral(str.Unescaped, _encoding, Span(str)));
                        break;
                    case Pm.EmbeddedStatementsNode embedded:
                        result.Add(StatementsAsExpression(embedded.Statements, Span(embedded)));
                        break;
                    case Pm.EmbeddedVariableNode embeddedVar:
                        result.Add(Expr(embeddedVar.Variable));
                        break;
                    default:
                        result.Add(Expr(part)); // nested interpolations, heredocs
                        break;
                }
            }
            return result;
        }

        private ConstructedSymbol Symbol(Pm.PmNode/*!*/ node) {
            switch (node) {
                case Pm.SymbolNode symbol: return new ConstructedSymbol(symbol.Unescaped);
                case Pm.InterpolatedSymbolNode interp:
                    return new ConstructedSymbol((StringConstructor)Expr(interp));
                default: throw Unsupported(node);
            }
        }

        private RubyRegexOptions RegexOptions(Pm.PmNode/*!*/ node) {
            var options = RubyRegexOptions.NONE;
            if (HasFlag(node, Pm.RegularExpressionFlags.IgnoreCase)) options |= RubyRegexOptions.IgnoreCase;
            if (HasFlag(node, Pm.RegularExpressionFlags.Extended)) options |= RubyRegexOptions.Extended;
            if (HasFlag(node, Pm.RegularExpressionFlags.MultiLine)) options |= RubyRegexOptions.Multiline;
            if (HasFlag(node, Pm.RegularExpressionFlags.Once)) options |= RubyRegexOptions.Once;
            if (HasFlag(node, Pm.RegularExpressionFlags.EucJp)) options |= RubyRegexOptions.EUC;
            if (HasFlag(node, Pm.RegularExpressionFlags.Windows31j)) options |= RubyRegexOptions.SJIS;
            if (HasFlag(node, Pm.RegularExpressionFlags.Utf8)) options |= RubyRegexOptions.UTF8;
            if (HasFlag(node, Pm.RegularExpressionFlags.Ascii8bit)) options |= RubyRegexOptions.FIXED;
            return options;
        }

        private ConstantVariable/*!*/ ConstantPath(Pm.PmNode/*!*/ node, SourceSpan span) {
            switch (node) {
                case Pm.ConstantReadNode read:
                    return new ConstantVariable(read.Name, span);
                case Pm.ConstantPathNode path when path.Name != null:
                    // null parent = ::Foo, explicitly bound to Object
                    return new ConstantVariable(path.Parent != null ? Expr(path.Parent) : null, path.Name, span);
                case Pm.ConstantPathTargetNode target when target.Name != null:
                    return new ConstantVariable(target.Parent != null ? Expr(target.Parent) : null, target.Name, span);
                default:
                    throw Unsupported(node);
            }
        }

        private Expression/*!*/ If(Pm.IfNode/*!*/ node, SourceSpan span) {
            var elseIfClauses = new List<ElseIfClause>();
            var subsequent = node.Subsequent;
            while (subsequent != null) {
                if (subsequent is Pm.IfNode elseIf) {
                    elseIfClauses.Add(new ElseIfClause(Condition(elseIf.Predicate), BuildStatements(elseIf.Statements), Span(elseIf)));
                    subsequent = elseIf.Subsequent;
                } else {
                    var elseNode = (Pm.ElseNode)subsequent;
                    elseIfClauses.Add(new ElseIfClause(null, BuildStatements(elseNode.Statements), Span(elseNode)));
                    break;
                }
            }
            return new IfExpression(Condition(node.Predicate), BuildStatements(node.Statements), elseIfClauses, span);
        }

        private Expression/*!*/ HashExpression(Pm.PmNode[]/*!*/ elements, SourceSpan span) {
            // fold AssocNode runs and ** splats into successive Hash#merge calls
            Expression result = null;
            var maplets = new List<Maplet>();

            foreach (var element in elements) {
                switch (element) {
                    case Pm.AssocNode assoc:
                        maplets.Add(new Maplet(Expr(assoc.Key), Expr(assoc.Value), Span(assoc)));
                        break;
                    case Pm.AssocSplatNode splat when splat.Value != null:
                        result = MergeHash(result, maplets, span);
                        result = result == null
                            ? Expr(splat.Value)
                            : new MethodCall(result, "merge", new Arguments(Expr(splat.Value)), span);
                        break;
                    default:
                        throw Unsupported(element);
                }
            }
            result = MergeHash(result, maplets, span);
            return result ?? new HashConstructor(new Maplet[0], span);
        }

        private Expression MergeHash(Expression result, List<Maplet>/*!*/ maplets, SourceSpan span) {
            if (maplets.Count == 0) return result;
            var ctor = new HashConstructor(maplets.ToArray(), span);
            maplets.Clear();
            return result == null ? (Expression)ctor : new MethodCall(result, "merge", new Arguments(ctor), span);
        }

        // ---- calls ----

        private Expression/*!*/ Call(Pm.CallNode/*!*/ node, SourceSpan span) {
            string name = node.Name;

            // prism can't see locals defined outside an eval'd unit unless scopes are
            // passed; resolve VARIABLE_CALL bare words against the scope chain first
            if (HasFlag(node, Pm.CallNodeFlags.VariableCall)) {
                var local = CurrentScope.ResolveVariable(name);
                if (local != null) return local;
            }

            if (HasFlag(node, Pm.CallNodeFlags.SafeNavigation)) {
                return SafeNavigation(node, span);
            }

            if (HasFlag(node, Pm.CallNodeFlags.AttributeWrite)) {
                var writeArgs = ((Pm.ArgumentsNode)node.Arguments).Arguments;
                var rhs = Expr(writeArgs[writeArgs.Length - 1]);
                var target = Expr(node.Receiver);
                LeftValue lhs;
                if (name == "[]=") {
                    var indexArgs = new List<Expression>();
                    for (int i = 0; i < writeArgs.Length - 1; i++) indexArgs.Add(Argument(writeArgs[i]));
                    lhs = new ArrayItemAccess(target, new Arguments(indexArgs.ToArray()), null, span);
                } else {
                    lhs = new AttributeAccess(target, name.TrimEnd('='), span);
                }
                return new SimpleAssignmentExpression(lhs, rhs, null, span);
            }

            Expression receiver = node.Receiver != null ? Expr(node.Receiver) : null;
            Block block = OptionalBlock(node.Block);
            Arguments args = node.Arguments != null ? BuildArguments(node.Arguments, ref block) : null;
            return new MethodCall(receiver, name, args, block, span);
        }

        private Expression/*!*/ SafeNavigation(Pm.CallNode/*!*/ node, SourceSpan span) {
            // recv&.m(args)  =>  (?safeN? = recv).nil? ? nil : ?safeN?.m(args)
            var temp = CurrentScope.ResolveOrAddVariable("?safe" + _tempCounter++ + "?", span);
            var assign = new SimpleAssignmentExpression(temp, Expr(node.Receiver), null, span);
            var test = new MethodCall(assign, "nil?", null, span);
            var invoke = new MethodCall(temp, node.Name,
                node.Arguments != null ? BuildArguments(node.Arguments) : null, OptionalBlock(node.Block), span);
            return new ConditionalExpression(test, Literal.Nil(span), invoke, span);
        }

        private Block OptionalBlock(Pm.PmNode block) {
            switch (block) {
                case null: return null;
                case Pm.BlockNode blockNode: return BlockDef(blockNode);
                case Pm.BlockArgumentNode blockArg when blockArg.Expression != null:
                    return new BlockReference(Expr(blockArg.Expression), Span(blockArg));
                case Pm.BlockArgumentNode anonymous: {
                    // `f(&)` forwards the anonymous block parameter of the enclosing `def f(&)`
                    var blockLocal = CurrentScope.ResolveVariable("?block?");
                    if (blockLocal == null) throw Unsupported(anonymous);
                    return new BlockReference(blockLocal, Span(anonymous));
                }
                default: throw Unsupported(block);
            }
        }

        private Expression/*!*/ Argument(Pm.PmNode/*!*/ node) {
            if (node is Pm.SplatNode splat) {
                if (splat.Expression == null) throw Unsupported(node);
                return new SplattedArgument(Expr(splat.Expression));
            }
            return Expr(node);
        }

        private Arguments/*!*/ BuildArguments(Pm.PmNode argumentsNode) {
            Block ignored = null;
            return BuildArguments(argumentsNode, ref ignored);
        }

        /// <summary>
        /// Builds the argument list, expanding `...` forwarding into the hidden rest local
        /// (and, when the caller passes no block of its own, the hidden block local).
        /// </summary>
        private Arguments/*!*/ BuildArguments(Pm.PmNode argumentsNode, ref Block block) {
            if (argumentsNode == null) return new Arguments();
            var exprs = new List<Expression>();
            foreach (var arg in ((Pm.ArgumentsNode)argumentsNode).Arguments) {
                if (arg is Pm.ForwardingArgumentsNode) {
                    var rest = CurrentScope.ResolveVariable(ForwardingRestName);
                    var fwdBlock = CurrentScope.ResolveVariable(ForwardingBlockName);
                    if (rest == null) throw Unsupported(arg);
                    exprs.Add(new SplattedArgument(rest));
                    if (block == null && fwdBlock != null) {
                        block = new BlockReference(fwdBlock, Span(arg));
                    }
                } else {
                    exprs.Add(Argument(arg));
                }
            }
            return new Arguments(exprs.ToArray());
        }

        private Arguments OptionalArguments(Pm.PmNode argumentsNode) {
            return argumentsNode != null ? BuildArguments(argumentsNode) : null;
        }

        // ---- targets / multiple assignment ----

        private LeftValue/*!*/ Target(Pm.PmNode/*!*/ node) {
            var span = Span(node);
            switch (node) {
                case Pm.LocalVariableTargetNode local:
                    return CurrentScope.ResolveOrAddVariable(local.Name, span);
                case Pm.RequiredParameterNode requiredParam:
                    return CurrentScope.ResolveOrAddVariable(requiredParam.Name, span);
                case Pm.InstanceVariableTargetNode ivar:
                    return new InstanceVariable(ivar.Name, span);
                case Pm.GlobalVariableTargetNode gvar:
                    return new IronRuby.Compiler.Ast.GlobalVariable(gvar.Name.TrimStart('$'), span);
                case Pm.ClassVariableTargetNode cvar:
                    return new ClassVariable(cvar.Name, span);
                case Pm.ConstantTargetNode constant:
                    return new ConstantVariable(constant.Name, span);
                case Pm.ConstantPathTargetNode constantPath:
                    return ConstantPath(constantPath, span);
                case Pm.IndexTargetNode index:
                    return new ArrayItemAccess(Expr(index.Receiver), BuildArguments(index.Arguments), null, span);
                case Pm.CallTargetNode callTarget:
                    return new AttributeAccess(Expr(callTarget.Receiver), callTarget.Name.TrimEnd('='), span);
                case Pm.MultiTargetNode multi:
                    return CompoundTarget(multi.Lefts, multi.Rest, multi.Rights);
                default:
                    throw Unsupported(node);
            }
        }

        private CompoundLeftValue/*!*/ CompoundTarget(Pm.PmNode[]/*!*/ lefts, Pm.PmNode rest, Pm.PmNode[]/*!*/ rights) {
            var lvs = new List<LeftValue>();
            foreach (var left in lefts) lvs.Add(Target(left));
            int unsplatIndex = int.MaxValue;
            if (rest != null) {
                unsplatIndex = lvs.Count;
                if (rest is Pm.SplatNode splat && splat.Expression != null) {
                    lvs.Add(Target(splat.Expression));
                } else {
                    lvs.Add(Placeholder.Singleton); // `a, * = x` / `a, = x`
                }
            }
            foreach (var right in rights) lvs.Add(Target(right));
            return unsplatIndex == int.MaxValue
                ? new CompoundLeftValue(lvs.ToArray())
                : new CompoundLeftValue(lvs.ToArray(), unsplatIndex);
        }

        private Expression/*!*/[]/*!*/ RhsFromValue(Pm.PmNode/*!*/ value) {
            // prism wraps `a, b = 1, 2` in a synthesized ArrayNode (no bracket)
            if (value is Pm.ArrayNode array && array.OpeningLoc == null) {
                var rhs = new List<Expression>();
                foreach (var element in array.Elements) rhs.Add(Argument(element));
                return rhs.ToArray();
            }
            return new[] { Expr(value) };
        }

        // ---- bodies ----

        private Body/*!*/ BuildBeginBody(Pm.BeginNode/*!*/ node, SourceSpan span, Statements prologue) {
            var statements = BuildStatements(node.Statements);
            if (prologue != null) statements = Prepend(prologue, statements);

            List<RescueClause> rescues = null;
            var rescueNode = node.RescueClause as Pm.RescueNode;
            while (rescueNode != null) {
                if (rescues == null) rescues = new List<RescueClause>();
                rescues.Add(Rescue(rescueNode));
                rescueNode = rescueNode.Subsequent as Pm.RescueNode;
            }

            Statements elseStatements = null;
            if (node.ElseClause is Pm.ElseNode elseNode) elseStatements = BuildStatements(elseNode.Statements);
            Statements ensureStatements = null;
            if (node.EnsureClause is Pm.EnsureNode ensureNode) ensureStatements = BuildStatements(ensureNode.Statements);

            return new Body(statements, rescues, elseStatements, ensureStatements, span);
        }

        private static Statements/*!*/ Prepend(Statements/*!*/ prologue, Statements/*!*/ body) {
            var result = new Statements();
            foreach (var statement in prologue) result.Add(statement);
            foreach (var statement in body) result.Add(statement);
            return result;
        }

        private RescueClause/*!*/ Rescue(Pm.RescueNode/*!*/ node) {
            var types = new List<Expression>();
            foreach (var exception in node.Exceptions) types.Add(Argument(exception));
            return new RescueClause(types.ToArray(),
                node.Reference != null ? Target(node.Reference) : null,
                BuildStatements(node.Statements), Span(node));
        }

        private Body/*!*/ DefinitionBody(Pm.PmNode body, SourceSpan span, Statements prologue) {
            if (body is Pm.BeginNode begin) {
                return BuildBeginBody(begin, span, prologue);
            }
            var statements = BuildStatements(body);
            if (prologue != null) statements = Prepend(prologue, statements);
            return new Body(statements, null, null, null, span);
        }

        private Statements/*!*/ BlockBody(Pm.PmNode body, SourceSpan span, Statements prologue) {
            Statements statements;
            if (body is Pm.BeginNode begin) {
                statements = new Statements(BuildBeginBody(begin, span, null));
            } else {
                statements = BuildStatements(body);
            }
            return prologue != null ? Prepend(prologue, statements) : statements;
        }

        // ---- definitions ----

        private BlockDefinition/*!*/ BlockDef(Pm.BlockNode/*!*/ node) {
            var span = Span(node);
            var scope = new BlockLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                Statements prologue;
                var parameters = BlockParameters(node.Parameters, true, out prologue);
                return new BlockDefinition(scope, parameters, BlockBody(node.Body, span, prologue), span);
            } finally {
                _scopes.Pop();
            }
        }

        private Parameters BlockParameters(Pm.PmNode parametersNode, bool autoSplat, out Statements prologue) {
            prologue = null;
            switch (parametersNode) {
                case null:
                    return null;
                case Pm.BlockParametersNode blockParams: {
                    foreach (var blockLocal in blockParams.Locals) {
                        CurrentScope.ResolveOrAddVariable(((Pm.BlockLocalVariableNode)blockLocal).Name, Span(blockLocal));
                    }
                    return blockParams.Parameters != null
                        ? BuildParameters((Pm.ParametersNode)blockParams.Parameters, autoSplat, out prologue)
                        : null;
                }
                case Pm.NumberedParametersNode numbered: {
                    var span = Span(numbered);
                    var mandatory = new LeftValue[numbered.Maximum];
                    for (int i = 0; i < numbered.Maximum; i++) {
                        mandatory[i] = DefineParameter("_" + (i + 1), span);
                    }
                    return new Parameters(mandatory, mandatory.Length, null, null, null, span);
                }
                case Pm.ItParametersNode it: {
                    var span = Span(it);
                    var mandatory = new LeftValue[] { DefineParameter("it", span) };
                    return new Parameters(mandatory, 1, null, null, null, span);
                }
                default:
                    throw Unsupported(parametersNode);
            }
        }

        private Expression/*!*/ Def(Pm.DefNode/*!*/ node, SourceSpan span) {
            Expression target = node.Receiver != null ? Expr(node.Receiver) : null;
            var scope = new MethodLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                Statements prologue = null;
                Parameters parameters = Parameters.Empty;
                if (node.Parameters != null) {
                    parameters = BuildParameters((Pm.ParametersNode)node.Parameters, false, out prologue);
                }
                var body = DefinitionBody(node.Body, span, prologue);
                return new MethodDefinition(scope, target, node.Name, parameters, body, span);
            } finally {
                _scopes.Pop();
            }
        }

        private Expression/*!*/ Class(Pm.ClassNode/*!*/ node, SourceSpan span) {
            var name = ConstantPath(node.ConstantPath, Span(node.ConstantPath));
            Expression superClass = node.Superclass != null ? Expr(node.Superclass) : null;
            var scope = new ClassLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                return new ClassDefinition(scope, name, superClass, DefinitionBody(node.Body, span, null), span);
            } finally {
                _scopes.Pop();
            }
        }

        private Expression/*!*/ Module(Pm.ModuleNode/*!*/ node, SourceSpan span) {
            var name = ConstantPath(node.ConstantPath, Span(node.ConstantPath));
            var scope = new TopLocalDefinitionLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                return new ModuleDefinition(scope, name, DefinitionBody(node.Body, span, null), span);
            } finally {
                _scopes.Pop();
            }
        }

        // ---- parameters (including keyword-argument lowering) ----

        private Parameters/*!*/ BuildParameters(Pm.ParametersNode/*!*/ node, bool autoSplat, out Statements prologue) {
            var span = Span(node);
            prologue = null;

            var mandatory = new List<LeftValue>();
            foreach (var required in node.Requireds) {
                switch (required) {
                    case Pm.RequiredParameterNode requiredParam:
                        mandatory.Add(DefineParameter(requiredParam.Name, Span(required)));
                        break;
                    case Pm.MultiTargetNode multi:
                        mandatory.Add(CompoundTarget(multi.Lefts, multi.Rest, multi.Rights));
                        break;
                    default:
                        throw Unsupported(required);
                }
            }
            int leadingMandatoryCount = mandatory.Count;

            var optional = new List<SimpleAssignmentExpression>();
            foreach (var opt in node.Optionals) {
                var optParam = (Pm.OptionalParameterNode)opt;
                var lhs = DefineParameter(optParam.Name, Span(opt));
                optional.Add(new SimpleAssignmentExpression(lhs, Expr(optParam.Value), null, Span(opt)));
            }

            LeftValue unsplat = null;
            if (node.Rest != null) {
                var restSpan = Span(node.Rest);
                if (node.Rest is Pm.ImplicitRestNode) {
                    // |a,| occurs only in blocks/lambdas (invalid in def); a placeholder
                    // keeps MRI arity semantics
                    unsplat = Placeholder.Singleton;
                } else if (node.Rest is Pm.RestParameterNode restParam) {
                    unsplat = DefineParameter(restParam.Name ?? Symbols.RestArgsLocal, restSpan);
                } else {
                    throw Unsupported(node.Rest);
                }
            }

            foreach (var post in node.Posts) {
                if (!(post is Pm.RequiredParameterNode postParam)) throw Unsupported(post);
                mandatory.Add(DefineParameter(postParam.Name, Span(post)));
            }

            // keyword arguments: with only mandatory positionals a trailing optional hash
            // is equivalent, so use that (it keeps arity and Method#parameters sane).
            // Anything richer needs positional binding done by hand, because keywords do
            // not have their own calling-convention slot here.
            if (node.Keywords.Length > 0 || node.KeywordRest is Pm.KeywordRestParameterNode) {
                if (optional.Count > 0 || unsplat != null || node.Posts.Length > 0) {
                    return LowerGeneralParameters(node, autoSplat, span, out prologue);
                }
                prologue = LowerKeywords(node, optional, span);
            } else if (node.KeywordRest is Pm.ForwardingParameterNode) {
                // def f(...) => def f(*?fwd?, &?fwdblk?); calls with `...` splat them back
                if (unsplat != null) throw Unsupported(node.KeywordRest);
                unsplat = DefineParameter(ForwardingRestName, Span(node.KeywordRest));
            } else if (node.KeywordRest != null && !(node.KeywordRest is Pm.NoKeywordsParameterNode)) {
                throw Unsupported(node.KeywordRest);
            }

            LocalVariable blockParam = null;
            if (node.Block is Pm.BlockParameterNode block) {
                blockParam = DefineParameter(block.Name ?? "?block?", Span(node.Block));
            } else if (node.KeywordRest is Pm.ForwardingParameterNode) {
                blockParam = DefineParameter(ForwardingBlockName, Span(node.KeywordRest));
            }

            return new Parameters(mandatory.ToArray(), leadingMandatoryCount,
                optional.Count > 0 ? optional.ToArray() : null, unsplat, blockParam, span);
        }

        /// <summary>
        /// Signatures mixing keywords with optional/rest/post positionals are lowered to a
        /// single splat whose contents the prologue binds by hand:
        ///
        ///   def m(a, b = 5, *c, d, e: 2, **k)
        ///     =&gt; def m(*?args?)
        ///        ?args? = ?args?[0] if <auto-splat, blocks only>
        ///        ?kw?   = Hash === ?args?.last ? ?args?.pop : {}
        ///        a = ?args?.shift
        ///        b = ?args?.size &gt; 1 ? ?args?.shift : 5     # 1 = number of posts
        ///        c = ?args?.shift(?args?.size - 1)
        ///        d = ?args?.shift
        ///        e, k from ?kw?
        ///
        /// Execution semantics match MRI; arity and Method#parameters do not.
        /// </summary>
        private Parameters/*!*/ LowerGeneralParameters(Pm.ParametersNode/*!*/ node, bool autoSplat, SourceSpan span, out Statements prologue) {
            var args = DefineParameter("?args?", span);
            var statements = new Statements();
            int postCount = node.Posts.Length;

            var kwVar = CurrentScope.AddVariable("?kw?", span);

            // A trailing Hash is how a keyword call arrives here, since keywords have no
            // calling-convention slot of their own. But when a block auto-splats a single
            // Array argument no keywords were passed, so the Hash stays positional
            // (Ruby 3 keyword separation).
            var popKeywords = new SimpleAssignmentExpression(kwVar,
                new ConditionalExpression(
                    CaseEqual(new ConstantVariable("Hash", span),
                        new MethodCall(args, "last", null, span), span),
                    new MethodCall(args, "pop", null, span),
                    new HashConstructor(new Maplet[0], span), span),
                null, span);

            int formalCount = node.Requireds.Length + node.Optionals.Length + postCount + (node.Rest != null ? 1 : 0);
            if (autoSplat && formalCount > 1) {
                statements.Add(new IfExpression(
                    new AndExpression(
                        new MethodCall(new MethodCall(args, "size", null, span), "==",
                            new Arguments(Literal.Integer(1, span)), span),
                        CaseEqual(new ConstantVariable("Array", span),
                            new MethodCall(args, "[]", new Arguments(Literal.Integer(0, span)), span), span),
                        span),
                    MakeStatements(
                        new SimpleAssignmentExpression(args,
                            new MethodCall(args, "[]", new Arguments(Literal.Integer(0, span)), span), null, span),
                        new SimpleAssignmentExpression(kwVar, new HashConstructor(new Maplet[0], span), null, span)),
                    new List<ElseIfClause> { new ElseIfClause(null, MakeStatements(popKeywords), span) },
                    span));
            } else {
                statements.Add(popKeywords);
            }

            foreach (var required in node.Requireds) {
                statements.Add(new SimpleAssignmentExpression(Target(required),
                    new MethodCall(args, "shift", null, span), null, span));
            }

            foreach (var opt in node.Optionals) {
                var optParam = (Pm.OptionalParameterNode)opt;
                var local = CurrentScope.ResolveOrAddVariable(optParam.Name, Span(opt));
                statements.Add(new SimpleAssignmentExpression(local,
                    new ConditionalExpression(
                        new MethodCall(new MethodCall(args, "size", null, span), ">",
                            new Arguments(Literal.Integer(postCount, span)), span),
                        new MethodCall(args, "shift", null, span),
                        Expr(optParam.Value), span),
                    null, span));
            }

            if (node.Rest is Pm.RestParameterNode rest && rest.Name != null) {
                var local = CurrentScope.ResolveOrAddVariable(rest.Name, Span(node.Rest));
                statements.Add(new SimpleAssignmentExpression(local,
                    new MethodCall(args, "shift", new Arguments(
                        new MethodCall(new MethodCall(args, "size", null, span), "-",
                            new Arguments(Literal.Integer(postCount, span)), span)), span),
                    null, span));
            } else if (node.Rest != null && postCount > 0) {
                // anonymous rest still has to consume what the posts do not take
                statements.Add(new MethodCall(args, "shift", new Arguments(
                    new MethodCall(new MethodCall(args, "size", null, span), "-",
                        new Arguments(Literal.Integer(postCount, span)), span)), span));
            }

            foreach (var post in node.Posts) {
                statements.Add(new SimpleAssignmentExpression(Target(post),
                    new MethodCall(args, "shift", null, span), null, span));
            }

            foreach (var statement in BindKeywordsFrom(node, kwVar)) {
                statements.Add(statement);
            }

            prologue = statements;

            LocalVariable blockParam = null;
            if (node.Block is Pm.BlockParameterNode block) {
                blockParam = DefineParameter(block.Name ?? "?block?", Span(node.Block));
            }
            return new Parameters(LeftValue.EmptyArray, 0, null, args, blockParam, span);
        }

        /// <summary>
        /// def m(a, k: 1, j:, **rest) becomes def m(a, ?kw? = {}) plus a prologue:
        ///   raise ArgumentError, "missing keyword: :j" unless ?kw?.key?(:j)
        ///   j = ?kw?[:j]
        ///   k = ?kw?.key?(:k) ? ?kw?[:k] : 1
        ///   rest = ?kw?.dup ; rest.delete(:j) ; rest.delete(:k)
        /// </summary>
        private Statements/*!*/ LowerKeywords(Pm.ParametersNode/*!*/ node, List<SimpleAssignmentExpression>/*!*/ optional, SourceSpan span) {
            var kwVar = CurrentScope.AddVariable("?kw?", span);
            optional.Add(new SimpleAssignmentExpression(kwVar, new HashConstructor(new Maplet[0], span), null, span));

            var prologue = new Statements();
            // blocks and lambdas pass nil for parameters the caller omitted, so the
            // optional-parameter default above does not fire for them
            prologue.Add(new SimpleAssignmentExpression(kwVar,
                new HashConstructor(new Maplet[0], span), "||", span));

            foreach (var statement in BindKeywordsFrom(node, kwVar)) {
                prologue.Add(statement);
            }
            return prologue;
        }

        /// <summary>
        /// Binds each declared keyword parameter (and **rest) out of the hash the caller
        /// passed, raising ArgumentError for missing required keywords like MRI does.
        /// </summary>
        private List<Expression>/*!*/ BindKeywordsFrom(Pm.ParametersNode/*!*/ node, LocalVariable/*!*/ kwVar) {
            var span = Span(node);
            var prologue = new List<Expression>();
            var names = new List<string>();

            foreach (var keyword in node.Keywords) {
                var kwSpan = Span(keyword);
                switch (keyword) {
                    case Pm.RequiredKeywordParameterNode required: {
                        names.Add(required.Name);
                        var local = CurrentScope.ResolveOrAddVariable(required.Name, kwSpan);
                        prologue.Add(new UnlessExpression(
                            new MethodCall(kwVar, "key?", new Arguments(new SymbolLiteral(required.Name, _encoding, kwSpan)), kwSpan),
                            new Statements(new MethodCall(null, "raise", new Arguments(new Expression[] {
                                new ConstantVariable("ArgumentError", kwSpan),
                                new StringLiteral("missing keyword: :" + required.Name, _encoding, kwSpan)
                            }), kwSpan)),
                            null, kwSpan));
                        prologue.Add(new SimpleAssignmentExpression(local,
                            new MethodCall(kwVar, "[]", new Arguments(new SymbolLiteral(required.Name, _encoding, kwSpan)), kwSpan),
                            null, kwSpan));
                        break;
                    }
                    case Pm.OptionalKeywordParameterNode optKeyword: {
                        names.Add(optKeyword.Name);
                        var local = CurrentScope.ResolveOrAddVariable(optKeyword.Name, kwSpan);
                        prologue.Add(new SimpleAssignmentExpression(local,
                            new ConditionalExpression(
                                new MethodCall(kwVar, "key?", new Arguments(new SymbolLiteral(optKeyword.Name, _encoding, kwSpan)), kwSpan),
                                new MethodCall(kwVar, "[]", new Arguments(new SymbolLiteral(optKeyword.Name, _encoding, kwSpan)), kwSpan),
                                Expr(optKeyword.Value), kwSpan),
                            null, kwSpan));
                        break;
                    }
                    default:
                        throw Unsupported(keyword);
                }
            }

            if (node.KeywordRest is Pm.KeywordRestParameterNode keywordRest && keywordRest.Name != null) {
                var kwSpan = Span(node.KeywordRest);
                var restLocal = CurrentScope.ResolveOrAddVariable(keywordRest.Name, kwSpan);
                prologue.Add(new SimpleAssignmentExpression(restLocal,
                    new MethodCall(kwVar, "dup", null, kwSpan), null, kwSpan));
                foreach (var name in names) {
                    prologue.Add(new MethodCall(restLocal, "delete",
                        new Arguments(new SymbolLiteral(name, _encoding, kwSpan)), kwSpan));
                }
            }

            return prologue;
        }


        // ---- pattern matching (case/in), lowered to tests + bindings ----

        private const string ForwardingRestName = "?fwd?";
        private const string ForwardingBlockName = "?fwdblk?";

        private static Statements/*!*/ MakeStatements(params Expression[]/*!*/ statements) {
            var result = new Statements();
            foreach (var statement in statements) result.Add(statement);
            return result;
        }

        private LocalVariable/*!*/ NewTemp(Expression/*!*/ value, SourceSpan span, out Expression/*!*/ assignment) {
            var temp = CurrentScope.ResolveOrAddVariable("?pm" + _tempCounter++ + "?", span);
            assignment = new SimpleAssignmentExpression(temp, value, null, span);
            return temp;
        }

        private Expression/*!*/ RaiseNoMatchingPattern(Expression/*!*/ subject, SourceSpan span) {
            return new MethodCall(null, "raise", new Arguments(new Expression[] {
                new ConstantVariable("NoMatchingPatternError", span),
                new MethodCall(subject, "inspect", null, span)
            }), span);
        }

        private Expression/*!*/ CaseMatch(Pm.CaseMatchNode/*!*/ node, SourceSpan span) {
            Expression assign;
            var temp = NewTemp(Expr(node.Predicate), span, out assign);

            var clauses = new List<ElseIfClause>();
            Expression firstTest = null;
            Statements firstBody = null;
            foreach (var condition in node.Conditions) {
                var inNode = (Pm.InNode)condition;
                var test = PatternTest(inNode.Pattern, temp, Span(inNode));
                var body = BuildStatements(inNode.Statements);
                if (firstTest == null) {
                    firstTest = test;
                    firstBody = body;
                } else {
                    clauses.Add(new ElseIfClause(test, body, Span(inNode)));
                }
            }
            if (node.ElseClause is Pm.ElseNode elseNode) {
                clauses.Add(new ElseIfClause(null, BuildStatements(elseNode.Statements), Span(elseNode)));
            } else {
                clauses.Add(new ElseIfClause(null, new Statements(RaiseNoMatchingPattern(temp, span)), span));
            }

            var ifExpr = new IfExpression(firstTest, firstBody, clauses, span);
            return new BlockExpression(MakeStatements(new Expression[] { assign, ifExpr }), span);
        }

        /// <summary>
        /// Builds an expression that is truthy iff <paramref name="pattern"/> matches
        /// <paramref name="subject"/> (a temp local), binding capture variables as a
        /// side effect. Mirrors MRI semantics: === for value patterns,
        /// deconstruct/deconstruct_keys for array/hash patterns.
        /// </summary>
        private Expression/*!*/ PatternTest(Pm.PmNode/*!*/ pattern, Expression/*!*/ subject, SourceSpan span) {
            switch (pattern) {
                case Pm.LocalVariableTargetNode target: {
                    var local = CurrentScope.ResolveOrAddVariable(target.Name, Span(target));
                    return BindTrue(local, subject, Span(target));
                }
                case Pm.CapturePatternNode capture: {
                    var target = (Pm.LocalVariableTargetNode)capture.Target;
                    var local = CurrentScope.ResolveOrAddVariable(target.Name, Span(target));
                    return new AndExpression(PatternTest(capture.Value, subject, span),
                        BindTrue(local, subject, Span(capture)), span);
                }
                case Pm.AlternationPatternNode alternation:
                    return new OrExpression(PatternTest(alternation.Left, subject, span),
                        PatternTest(alternation.Right, subject, span), span);
                case Pm.PinnedVariableNode pinned:
                    return CaseEqual(Expr(pinned.Variable), subject, Span(pinned));
                case Pm.PinnedExpressionNode pinnedExpr:
                    return CaseEqual(Expr(pinnedExpr.Expression), subject, Span(pinnedExpr));
                case Pm.IfNode guard: {
                    // `in pat if cond`: prism nests the pattern in the guard's statements
                    var inner = ((Pm.StatementsNode)guard.Statements).Body[0];
                    return new AndExpression(PatternTest(inner, subject, span), Condition(guard.Predicate), Span(guard));
                }
                case Pm.UnlessNode guard: {
                    var inner = ((Pm.StatementsNode)guard.Statements).Body[0];
                    return new AndExpression(PatternTest(inner, subject, span),
                        new NotExpression(Expr(guard.Predicate), Span(guard)), Span(guard));
                }
                case Pm.ArrayPatternNode arrayPattern:
                    return ArrayPattern(arrayPattern, subject, span);
                case Pm.HashPatternNode hashPattern:
                    return HashPattern(hashPattern, subject, span);
                case Pm.FindPatternNode findPattern:
                    return FindPattern(findPattern, subject, span);
                default:
                    return CaseEqual(Expr(pattern), subject, span); // value pattern
            }
        }

        private Expression/*!*/ BindTrue(LocalVariable/*!*/ local, Expression/*!*/ value, SourceSpan span) {
            return new BlockExpression(MakeStatements(new Expression[] {
                new SimpleAssignmentExpression(local, value, null, span),
                Literal.True(span)
            }), span);
        }

        private Expression/*!*/ CaseEqual(Expression/*!*/ pattern, Expression/*!*/ subject, SourceSpan span) {
            return new MethodCall(pattern, "===", new Arguments(subject), span);
        }

        private static Expression/*!*/ AndAll(List<Expression>/*!*/ tests, SourceSpan span) {
            Expression result = null;
            foreach (var test in tests) {
                result = result == null ? test : new AndExpression(result, test, span);
            }
            return result;
        }

        private Expression/*!*/ ArrayPattern(Pm.ArrayPatternNode/*!*/ node, Expression/*!*/ subject, SourceSpan span) {
            var tests = new List<Expression>();
            if (node.Constant != null) {
                tests.Add(CaseEqual(Expr(node.Constant), subject, span));
            }

            // arr = Array === subj ? subj : (subj.respond_to?(:deconstruct) ? subj.deconstruct : nil)
            Expression deconstructed = new ConditionalExpression(
                CaseEqual(new ConstantVariable("Array", span), subject, span),
                subject,
                new ConditionalExpression(
                    new MethodCall(subject, "respond_to?", new Arguments(new SymbolLiteral("deconstruct", _encoding, span)), span),
                    new MethodCall(subject, "deconstruct", null, span),
                    Literal.Nil(span), span),
                span);
            Expression arrAssign;
            var arr = NewTemp(deconstructed, span, out arrAssign);
            tests.Add(new BlockExpression(MakeStatements(new Expression[] { arrAssign, arr }), span));

            int required = node.Requireds.Length + node.Posts.Length;
            Expression lenAssign;
            var len = NewTemp(new MethodCall(arr, "length", null, span), span, out lenAssign);
            tests.Add(new BlockExpression(MakeStatements(new Expression[] { lenAssign, Literal.True(span) }), span));
            tests.Add(new MethodCall(len, node.Rest != null ? ">=" : "==",
                new Arguments(Literal.Integer(required, span)), span));

            for (int i = 0; i < node.Requireds.Length; i++) {
                tests.Add(ElementPattern(node.Requireds[i], arr, Literal.Integer(i, span), span));
            }

            if (node.Rest is Pm.SplatNode splat && splat.Expression != null) {
                var restLocal = (LocalVariable)Target(splat.Expression);
                // rest = arr[requireds, len - required]
                var restValue = new MethodCall(arr, "[]", new Arguments(new Expression[] {
                    Literal.Integer(node.Requireds.Length, span),
                    new MethodCall(len, "-", new Arguments(Literal.Integer(required, span)), span)
                }), span);
                tests.Add(BindTrue(restLocal, restValue, span));
            }

            for (int i = 0; i < node.Posts.Length; i++) {
                // index = len - postCount + i
                var index = new MethodCall(len, "-", new Arguments(Literal.Integer(node.Posts.Length - i, span)), span);
                tests.Add(ElementPattern(node.Posts[i], arr, index, span));
            }

            return AndAll(tests, span);
        }

        private Expression/*!*/ ElementPattern(Pm.PmNode/*!*/ pattern, LocalVariable/*!*/ arr, Expression/*!*/ index, SourceSpan span) {
            Expression elemAssign;
            var elem = NewTemp(new MethodCall(arr, "[]", new Arguments(index), span), span, out elemAssign);
            return new BlockExpression(MakeStatements(new Expression[] {
                elemAssign,
                PatternTest(pattern, elem, span)
            }), span);
        }

        private Expression/*!*/ HashPattern(Pm.HashPatternNode/*!*/ node, Expression/*!*/ subject, SourceSpan span) {
            var tests = new List<Expression>();
            if (node.Constant != null) {
                tests.Add(CaseEqual(Expr(node.Constant), subject, span));
            }

            // h = Hash === subj ? subj : (subj.respond_to?(:deconstruct_keys) ? subj.deconstruct_keys(nil) : nil)
            Expression deconstructed = new ConditionalExpression(
                CaseEqual(new ConstantVariable("Hash", span), subject, span),
                subject,
                new ConditionalExpression(
                    new MethodCall(subject, "respond_to?", new Arguments(new SymbolLiteral("deconstruct_keys", _encoding, span)), span),
                    new MethodCall(subject, "deconstruct_keys", new Arguments(Literal.Nil(span)), span),
                    Literal.Nil(span), span),
                span);
            Expression hashAssign;
            var hash = NewTemp(deconstructed, span, out hashAssign);
            tests.Add(new BlockExpression(MakeStatements(new Expression[] { hashAssign, hash }), span));

            var knownKeys = new List<string>();
            foreach (var element in node.Elements) {
                var assoc = (Pm.AssocNode)element;
                var key = (Pm.SymbolNode)assoc.Key;
                knownKeys.Add(key.Unescaped);
                var keySymbol = new SymbolLiteral(key.Unescaped, _encoding, Span(key));
                tests.Add(new MethodCall(hash, "key?", new Arguments(keySymbol), span));

                Pm.PmNode valuePattern = assoc.Value is Pm.ImplicitNode implicitValue ? implicitValue.Value : assoc.Value;
                Expression valueAssign;
                var value = NewTemp(new MethodCall(hash, "[]", new Arguments(new SymbolLiteral(key.Unescaped, _encoding, span)), span),
                    span, out valueAssign);
                tests.Add(new BlockExpression(MakeStatements(new Expression[] {
                    valueAssign,
                    PatternTest(valuePattern, value, span)
                }), span));
            }

            if (node.Rest is Pm.AssocSplatNode restSplat && restSplat.Value != null) {
                var restLocal = (LocalVariable)Target(restSplat.Value);
                var restStatements = new List<Expression> {
                    new SimpleAssignmentExpression(restLocal, new MethodCall(hash, "dup", null, span), null, span)
                };
                foreach (var key in knownKeys) {
                    restStatements.Add(new MethodCall(restLocal, "delete",
                        new Arguments(new SymbolLiteral(key, _encoding, span)), span));
                }
                restStatements.Add(Literal.True(span));
                tests.Add(new BlockExpression(MakeStatements(restStatements.ToArray()), span));
            } else if (node.Rest is Pm.NoKeywordsParameterNode) {
                // {**nil}: no keys beyond the listed ones — (h.keys - [...]).empty?
                var listed = new List<Expression>();
                foreach (var key in knownKeys) listed.Add(new SymbolLiteral(key, _encoding, span));
                tests.Add(new MethodCall(
                    new MethodCall(new MethodCall(hash, "keys", null, span), "-",
                        new Arguments(new ArrayConstructor(new Arguments(listed.ToArray()), span)), span),
                    "empty?", null, span));
            }

            return AndAll(tests, span);
        }

        /// <summary>
        /// [*pre, a, b, *post]: scans for the first index where the middle patterns
        /// match via the __pm_find_index__ prelude helper, then binds the splats.
        /// </summary>
        private Expression/*!*/ FindPattern(Pm.FindPatternNode/*!*/ node, Expression/*!*/ subject, SourceSpan span) {
            var tests = new List<Expression>();
            if (node.Constant != null) {
                tests.Add(CaseEqual(Expr(node.Constant), subject, span));
            }

            Expression deconstructed = new ConditionalExpression(
                CaseEqual(new ConstantVariable("Array", span), subject, span),
                subject,
                new ConditionalExpression(
                    new MethodCall(subject, "respond_to?", new Arguments(new SymbolLiteral("deconstruct", _encoding, span)), span),
                    new MethodCall(subject, "deconstruct", null, span),
                    Literal.Nil(span), span),
                span);
            Expression arrAssign;
            var arr = NewTemp(deconstructed, span, out arrAssign);
            tests.Add(new BlockExpression(MakeStatements(arrAssign, arr), span));

            int count = node.Requireds.Length;
            Expression lenAssign;
            var len = NewTemp(new MethodCall(arr, "length", null, span), span, out lenAssign);
            tests.Add(new BlockExpression(MakeStatements(lenAssign, Literal.True(span)), span));
            tests.Add(new MethodCall(len, ">=", new Arguments(Literal.Integer(count, span)), span));

            // captures inside the block must live in the enclosing scope
            PreDeclareCaptures(node.Requireds);

            var blockScope = new BlockLexicalScope(CurrentScope);
            _scopes.Push(blockScope);
            BlockDefinition probe;
            try {
                var index = CurrentScope.AddVariable("?pmi" + _tempCounter++ + "?", span);
                var elementTests = new List<Expression>();
                for (int i = 0; i < count; i++) {
                    var position = new MethodCall(index, "+", new Arguments(Literal.Integer(i, span)), span);
                    elementTests.Add(ElementPattern(node.Requireds[i], arr, position, span));
                }
                probe = new BlockDefinition(blockScope,
                    new Parameters(new LeftValue[] { index }, 1, null, null, null, span),
                    MakeStatements(AndAll(elementTests, span)), span);
            } finally {
                _scopes.Pop();
            }

            Expression idxAssign;
            var idx = NewTemp(new MethodCall(null, "__pm_find_index__",
                new Arguments(new Expression[] { arr, Literal.Integer(count, span) }), probe, span), span, out idxAssign);
            tests.Add(new BlockExpression(MakeStatements(idxAssign, idx), span));

            if (node.Left is Pm.SplatNode leftSplat && leftSplat.Expression != null) {
                var pre = (LocalVariable)Target(leftSplat.Expression);
                tests.Add(BindTrue(pre, new MethodCall(arr, "[]", new Arguments(new Expression[] {
                    Literal.Integer(0, span), idx }), span), span));
            }
            if (node.Right is Pm.SplatNode rightSplat && rightSplat.Expression != null) {
                var post = (LocalVariable)Target(rightSplat.Expression);
                var start = new MethodCall(idx, "+", new Arguments(Literal.Integer(count, span)), span);
                var length = new MethodCall(new MethodCall(len, "-", new Arguments(idx), span), "-",
                    new Arguments(Literal.Integer(count, span)), span);
                tests.Add(BindTrue(post, new MethodCall(arr, "[]",
                    new Arguments(new Expression[] { start, length }), span), span));
            }

            return AndAll(tests, span);
        }

        private void PreDeclareCaptures(Pm.PmNode[]/*!*/ patterns) {
            foreach (var pattern in patterns) PreDeclareCaptures(pattern);
        }

        private void PreDeclareCaptures(Pm.PmNode pattern) {
            switch (pattern) {
                case null: return;
                case Pm.LocalVariableTargetNode target:
                    CurrentScope.ResolveOrAddVariable(target.Name, Span(target));
                    return;
                case Pm.CapturePatternNode capture:
                    PreDeclareCaptures(capture.Value);
                    PreDeclareCaptures(capture.Target);
                    return;
                case Pm.ArrayPatternNode array:
                    PreDeclareCaptures(array.Requireds);
                    PreDeclareCaptures(array.Rest);
                    PreDeclareCaptures(array.Posts);
                    return;
                case Pm.SplatNode splat:
                    PreDeclareCaptures(splat.Expression);
                    return;
                case Pm.HashPatternNode hash:
                    foreach (var element in hash.Elements) {
                        if (element is Pm.AssocNode assoc) {
                            PreDeclareCaptures(assoc.Value is Pm.ImplicitNode imp ? imp.Value : assoc.Value);
                        }
                    }
                    return;
                default:
                    return;
            }
        }

        /// <summary>
        /// Defines a parameter in the current scope. Unlike ResolveOrAddVariable this never
        /// reaches into an enclosing scope, which would make one LocalVariable a formal
        /// parameter of two different scopes (mirrors Parser.DefineParameter).
        /// </summary>
        private LocalVariable/*!*/ DefineParameter(string/*!*/ name, SourceSpan span) {
            LocalVariable existing;
            if (CurrentScope.TryGetValue(name, out existing)) {
                // Duplicate parameter names are legal for `_`. Each occurrence still needs
                // its own slot (one LocalVariable cannot hold two closure indices), so give
                // the repeats hidden names; reads of `_` see the first one, as in MRI.
                return CurrentScope.AddVariable("?dup" + _tempCounter++ + "?", span);
            }
            return CurrentScope.AddVariable(name, span);
        }

        private static LeftValue/*!*/[]/*!*/ RemoveAt(LeftValue/*!*/[]/*!*/ values, int index) {
            var result = new LeftValue[values.Length - 1];
            Array.Copy(values, 0, result, 0, index);
            Array.Copy(values, index + 1, result, index, values.Length - index - 1);
            return result;
        }
    }
}
