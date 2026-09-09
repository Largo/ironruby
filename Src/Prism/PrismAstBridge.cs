using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime;
using Microsoft.Scripting;

namespace IronRuby.Prism {
    /// <summary>
    /// Maps prism's AST (via the JSON dump for now) onto IronRuby.Compiler.Ast so the
    /// existing AstGenerator/DLR pipeline compiles it unchanged. Covers a growing subset
    /// of node types; anything unmapped raises SyntaxError with the prism node name.
    /// </summary>
    public sealed class PrismAstBridge {
        private readonly string/*!*/ _source;
        private readonly string _path;
        private readonly List<int>/*!*/ _lineStarts;
        private readonly RubyEncoding/*!*/ _encoding;
        private readonly Stack<LexicalScope>/*!*/ _scopes = new Stack<LexicalScope>();

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
            return ParseText(sourceUnit.GetCode(), sourceUnit.Path, options.LocalNames);
        }

        public static SourceUnitTree ParseText(string/*!*/ code, string path) {
            return ParseText(code, path, null);
        }

        public static SourceUnitTree ParseText(string/*!*/ code, string path, List<string> outerLocalNames) {
            var bridge = new PrismAstBridge(code, path, RubyEncoding.UTF8);
            using (var doc = JsonDocument.Parse(PrismParser.ParseToJson(code))) {
                return bridge.Program(doc.RootElement, outerLocalNames);
            }
        }

        // ---- helpers ----

        private SourceSpan Span(JsonElement node) {
            if (!node.TryGetProperty("location", out var loc)) return SourceSpan.None;
            int start = loc.GetProperty("start").GetInt32();
            int length = loc.GetProperty("length").GetInt32();
            return new SourceSpan(Location(start), Location(start + length));
        }

        private SourceLocation Location(int index) {
            int line = _lineStarts.BinarySearch(index);
            if (line < 0) line = ~line - 1;
            if (index > _source.Length) index = _source.Length;
            return new SourceLocation(index, line + 1, index - _lineStarts[line] + 1);
        }

        private static JsonElement? Opt(JsonElement node, string name) {
            if (node.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null) return value;
            return null;
        }

        private static string Type(JsonElement node) {
            return node.GetProperty("type").GetString();
        }

        private static bool HasFlag(JsonElement node, string flag) {
            if (!node.TryGetProperty("flags", out var flags)) return false;
            foreach (var f in flags.EnumerateArray()) {
                if (f.GetString() == flag) return true;
            }
            return false;
        }

        private Exception Unsupported(JsonElement node) {
            var span = Span(node);
            return new NotSupportedException(
                $"Prism bridge: node type '{Type(node)}' is not supported yet (line {span.Start.Line})");
        }

        private LexicalScope/*!*/ CurrentScope {
            get { return _scopes.Peek(); }
        }

        // ---- program / statements ----

        private SourceUnitTree/*!*/ Program(JsonElement node, List<string> outerLocalNames) {
            // eval: locals defined outside this compilation unit live in a runtime outer scope
            var scope = new TopStaticLexicalScope(
                outerLocalNames != null ? new RuntimeLexicalScope(outerLocalNames) : null);
            _scopes.Push(scope);
            var statements = Statements(Opt(node, "statements"));
            _scopes.Pop();
            return new SourceUnitTree(scope, statements, null, _encoding, -1);
        }

        private Statements/*!*/ Statements(JsonElement? stmtsNode) {
            var result = new Statements();
            if (stmtsNode.HasValue) {
                foreach (var stmt in stmtsNode.Value.GetProperty("body").EnumerateArray()) {
                    result.Add(Expr(stmt));
                }
            }
            return result;
        }

        private Expression/*!*/ StatementsAsExpression(JsonElement? stmtsNode, SourceSpan span) {
            var statements = Statements(stmtsNode);
            if (statements.Count == 1) {
                foreach (var s in statements) return s;
            }
            if (statements.Count == 0) {
                return Literal.Nil(span);
            }
            return new BlockExpression(statements, span);
        }

        // ---- expression dispatch ----

        private Expression/*!*/ Expr(JsonElement node) {
            var span = Span(node);
            switch (Type(node)) {
                case "IntegerNode": {
                    var value = node.GetProperty("value");
                    if (value.TryGetInt32(out int i32)) return Literal.Integer(i32, span);
                    return Literal.BigInteger(BigInteger.Parse(value.GetRawText()), span);
                }
                case "FloatNode":
                    return Literal.Double(node.GetProperty("value").GetDouble(), span);
                case "StringNode":
                    return new StringLiteral(node.GetProperty("unescaped").GetString(), _encoding, span);
                case "SymbolNode":
                    return new SymbolLiteral(node.GetProperty("unescaped").GetString(), _encoding, span);
                case "TrueNode": return Literal.True(span);
                case "FalseNode": return Literal.False(span);
                case "NilNode": return Literal.Nil(span);
                case "SelfNode": return new SelfReference(span);

                case "ParenthesesNode":
                    return StatementsAsExpression(Opt(node, "body"), span);
                case "StatementsNode":
                    return StatementsAsExpression(node, span);

                case "ArrayNode": {
                    var items = new List<Expression>();
                    foreach (var el in node.GetProperty("elements").EnumerateArray()) items.Add(Argument(el));
                    return new ArrayConstructor(new Arguments(items.ToArray()), span);
                }
                case "HashNode": case "KeywordHashNode": {
                    var maplets = new List<Maplet>();
                    foreach (var el in node.GetProperty("elements").EnumerateArray()) {
                        if (Type(el) != "AssocNode") throw Unsupported(el);
                        maplets.Add(new Maplet(Expr(el.GetProperty("key")), Expr(el.GetProperty("value")), Span(el)));
                    }
                    return new HashConstructor(maplets.ToArray(), span);
                }
                case "RangeNode": {
                    var left = Opt(node, "left"); var right = Opt(node, "right");
                    return new RangeExpression(
                        left.HasValue ? Expr(left.Value) : Literal.Nil(span),
                        right.HasValue ? Expr(right.Value) : Literal.Nil(span),
                        HasFlag(node, "EXCLUDE_END"), span);
                }

                case "InterpolatedStringNode": {
                    var parts = new List<Expression>();
                    foreach (var part in node.GetProperty("parts").EnumerateArray()) parts.Add(StringPart(part));
                    return new StringConstructor(parts, StringKind.Mutable, span);
                }
                case "EmbeddedStatementsNode":
                    return StatementsAsExpression(Opt(node, "statements"), span);

                case "CallNode": return Call(node, span);

                case "LocalVariableReadNode":
                    return CurrentScope.ResolveOrAddVariable(node.GetProperty("name").GetString(), span);
                case "LocalVariableWriteNode": {
                    var lhs = CurrentScope.ResolveOrAddVariable(node.GetProperty("name").GetString(), span);
                    return new SimpleAssignmentExpression(lhs, Expr(node.GetProperty("value")), null, span);
                }
                case "LocalVariableOperatorWriteNode": {
                    var lhs = CurrentScope.ResolveOrAddVariable(node.GetProperty("name").GetString(), span);
                    return new SimpleAssignmentExpression(lhs, Expr(node.GetProperty("value")),
                        node.GetProperty("binary_operator").GetString(), span);
                }
                case "LocalVariableOrWriteNode": {
                    var lhs = CurrentScope.ResolveOrAddVariable(node.GetProperty("name").GetString(), span);
                    return new SimpleAssignmentExpression(lhs, Expr(node.GetProperty("value")), "||", span);
                }
                case "LocalVariableAndWriteNode": {
                    var lhs = CurrentScope.ResolveOrAddVariable(node.GetProperty("name").GetString(), span);
                    return new SimpleAssignmentExpression(lhs, Expr(node.GetProperty("value")), "&&", span);
                }

                case "InstanceVariableReadNode":
                    return new InstanceVariable(node.GetProperty("name").GetString(), span);
                case "InstanceVariableWriteNode":
                    return new SimpleAssignmentExpression(
                        new InstanceVariable(node.GetProperty("name").GetString(), span),
                        Expr(node.GetProperty("value")), null, span);
                case "GlobalVariableReadNode":
                    return new IronRuby.Compiler.Ast.GlobalVariable(node.GetProperty("name").GetString().TrimStart('$'), span);
                case "GlobalVariableWriteNode":
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(node.GetProperty("name").GetString().TrimStart('$'), span),
                        Expr(node.GetProperty("value")), null, span);
                case "ClassVariableReadNode":
                    return new ClassVariable(node.GetProperty("name").GetString(), span);
                case "ClassVariableWriteNode":
                    return new SimpleAssignmentExpression(
                        new ClassVariable(node.GetProperty("name").GetString(), span),
                        Expr(node.GetProperty("value")), null, span);

                case "ConstantReadNode":
                    return new ConstantVariable(node.GetProperty("name").GetString(), span);
                case "ConstantPathNode":
                    return ConstantPath(node, span);
                case "ConstantWriteNode":
                    return new SimpleAssignmentExpression(
                        new ConstantVariable(node.GetProperty("name").GetString(), span),
                        Expr(node.GetProperty("value")), null, span);

                case "IfNode": return If(node, span);
                case "UnlessNode": {
                    var elseNode = Opt(node, "else_clause");
                    ElseIfClause elseClause = null;
                    if (elseNode.HasValue) {
                        elseClause = new ElseIfClause(null, Statements(Opt(elseNode.Value, "statements")), Span(elseNode.Value));
                    }
                    return new UnlessExpression(Expr(node.GetProperty("predicate")),
                        Statements(Opt(node, "statements")), elseClause, span);
                }
                case "WhileNode":
                    return new WhileLoopExpression(Expr(node.GetProperty("predicate")), true,
                        HasFlag(node, "BEGIN_MODIFIER"), Statements(Opt(node, "statements")), span);
                case "UntilNode":
                    return new WhileLoopExpression(Expr(node.GetProperty("predicate")), false,
                        HasFlag(node, "BEGIN_MODIFIER"), Statements(Opt(node, "statements")), span);

                case "AndNode":
                    return new AndExpression(Expr(node.GetProperty("left")), Expr(node.GetProperty("right")), span);
                case "OrNode":
                    return new OrExpression(Expr(node.GetProperty("left")), Expr(node.GetProperty("right")), span);

                case "DefNode": return Def(node, span);
                case "ClassNode": return Class(node, span);
                case "ModuleNode": return Module(node, span);

                case "ReturnNode": return new ReturnStatement(JumpArguments(node), span);
                case "BreakNode": return new BreakStatement(JumpArguments(node), span);
                case "NextNode": return new NextStatement(JumpArguments(node), span);
                case "SuperNode": {
                    var argsNode = Opt(node, "arguments");
                    Block superBlock = null;
                    var blockNode = Opt(node, "block");
                    if (blockNode.HasValue) {
                        if (Type(blockNode.Value) == "BlockNode") {
                            superBlock = BlockDef(blockNode.Value);
                        } else {
                            superBlock = new BlockReference(Expr(blockNode.Value.GetProperty("expression")), Span(blockNode.Value));
                        }
                    }
                    // explicit super(...): empty Arguments when no args (distinct from zsuper)
                    return new SuperCall(argsNode.HasValue ? BuildArguments(argsNode.Value) : new Arguments(), superBlock, span);
                }
                case "ForwardingSuperNode": {
                    Block superBlock = null;
                    var blockNode = Opt(node, "block");
                    if (blockNode.HasValue) superBlock = BlockDef(blockNode.Value);
                    return new SuperCall(null, superBlock, span); // zsuper: forwards current params
                }
                case "YieldNode": {
                    var args = Opt(node, "arguments");
                    return new YieldCall(args.HasValue ? BuildArguments(args.Value) : null, span);
                }

                case "BeginNode": return BuildBeginBody(node, span);
                case "RescueModifierNode":
                    return new RescueExpression(Expr(node.GetProperty("expression")),
                        Expr(node.GetProperty("rescue_expression")),
                        Span(node.GetProperty("rescue_expression")), span);

                case "CaseNode": {
                    var whens = new List<WhenClause>();
                    foreach (var w in node.GetProperty("conditions").EnumerateArray()) {
                        var comparisons = new List<Expression>();
                        foreach (var c in w.GetProperty("conditions").EnumerateArray()) comparisons.Add(Argument(c));
                        whens.Add(new WhenClause(comparisons.ToArray(), Statements(Opt(w, "statements")), Span(w)));
                    }
                    var caseElse = Opt(node, "else_clause");
                    var predicate = Opt(node, "predicate");
                    return new CaseExpression(predicate.HasValue ? Expr(predicate.Value) : null, whens.ToArray(),
                        caseElse.HasValue ? Statements(Opt(caseElse.Value, "statements")) : null, span);
                }

                case "DefinedNode":
                    return new IsDefinedExpression(Expr(node.GetProperty("value")), span);

                case "RegularExpressionNode": case "MatchLastLineNode":
                    return new RegularExpression(
                        new List<Expression> { new StringLiteral(node.GetProperty("unescaped").GetString(), _encoding, span) },
                        RegexOptions(node), Type(node) == "MatchLastLineNode", span);
                case "InterpolatedRegularExpressionNode": {
                    var parts = new List<Expression>();
                    foreach (var part in node.GetProperty("parts").EnumerateArray()) parts.Add(StringPart(part));
                    return new RegularExpression(parts, RegexOptions(node), span);
                }
                case "MatchRequiredNode": case "MatchPredicateNode":
                    throw Unsupported(node); // pattern matching

                case "MatchWriteNode": {
                    // /(?<x>...)/ =~ str — writes named captures into locals
                    var call = node.GetProperty("call");
                    if (!(Expr(call.GetProperty("receiver")) is RegularExpression regex)) throw Unsupported(node);
                    foreach (var t in node.GetProperty("targets").EnumerateArray()) {
                        CurrentScope.ResolveOrAddVariable(t.GetProperty("name").GetString(), Span(t));
                    }
                    var matchArg = call.GetProperty("arguments").GetProperty("arguments")[0];
                    return new MatchExpression(regex, Expr(matchArg), span);
                }

                case "BackReferenceReadNode": {
                    string refName = node.GetProperty("name").GetString().TrimStart('$');
                    int index;
                    switch (refName) {
                        case "&": index = 0; break;
                        case "~": index = -1; break;
                        case "+": index = -2; break;
                        case "`": index = -3; break;
                        case "'": index = -4; break;
                        default: throw Unsupported(node);
                    }
                    return new RegexMatchReference(index, span);
                }
                case "NumberedReferenceReadNode":
                    return new RegexMatchReference(node.GetProperty("number").GetInt32(), span);

                case "MultiWriteNode":
                    return new ParallelAssignmentExpression(CompoundTarget(node), RhsFromValue(node.GetProperty("value")), span);

                case "CallOperatorWriteNode":
                    return new MemberAssignmentExpression(Expr(node.GetProperty("receiver")),
                        node.GetProperty("read_name").GetString(),
                        node.GetProperty("binary_operator").GetString(), Expr(node.GetProperty("value")), span);
                case "CallOrWriteNode":
                    return new MemberAssignmentExpression(Expr(node.GetProperty("receiver")),
                        node.GetProperty("read_name").GetString(), "||", Expr(node.GetProperty("value")), span);
                case "CallAndWriteNode":
                    return new MemberAssignmentExpression(Expr(node.GetProperty("receiver")),
                        node.GetProperty("read_name").GetString(), "&&", Expr(node.GetProperty("value")), span);

                case "IndexOperatorWriteNode": case "IndexOrWriteNode": case "IndexAndWriteNode": {
                    string op = Type(node) == "IndexOperatorWriteNode"
                        ? node.GetProperty("binary_operator").GetString()
                        : (Type(node) == "IndexOrWriteNode" ? "||" : "&&");
                    var argsNode = Opt(node, "arguments");
                    var access = new ArrayItemAccess(Expr(node.GetProperty("receiver")),
                        argsNode.HasValue ? BuildArguments(argsNode.Value) : new Arguments(), null, span);
                    return new SimpleAssignmentExpression(access, Expr(node.GetProperty("value")), op, span);
                }

                case "InstanceVariableOperatorWriteNode": case "InstanceVariableOrWriteNode": case "InstanceVariableAndWriteNode":
                case "GlobalVariableOperatorWriteNode": case "GlobalVariableOrWriteNode": case "GlobalVariableAndWriteNode":
                case "ClassVariableOperatorWriteNode": case "ClassVariableOrWriteNode": case "ClassVariableAndWriteNode":
                case "ConstantOperatorWriteNode": case "ConstantOrWriteNode": case "ConstantAndWriteNode": {
                    string type = Type(node);
                    string name = node.GetProperty("name").GetString();
                    LeftValue lhs =
                        type.StartsWith("InstanceVariable") ? new InstanceVariable(name, span) :
                        type.StartsWith("GlobalVariable") ? (LeftValue)new IronRuby.Compiler.Ast.GlobalVariable(name.TrimStart('$'), span) :
                        type.StartsWith("ClassVariable") ? new ClassVariable(name, span) :
                        new ConstantVariable(name, span);
                    string op = type.EndsWith("OperatorWriteNode")
                        ? node.GetProperty("binary_operator").GetString()
                        : (type.EndsWith("OrWriteNode") ? "||" : "&&");
                    return new SimpleAssignmentExpression(lhs, Expr(node.GetProperty("value")), op, span);
                }

                case "SingletonClassNode": {
                    var scScope = new TopLocalDefinitionLexicalScope(CurrentScope);
                    _scopes.Push(scScope);
                    try {
                        return new SingletonDefinition(scScope, Expr(node.GetProperty("expression")),
                            DefinitionBody(node, span), span);
                    } finally {
                        _scopes.Pop();
                    }
                }

                case "AliasMethodNode":
                    return new AliasStatement(true, Symbol(node.GetProperty("new_name")), Symbol(node.GetProperty("old_name")), span);
                case "AliasGlobalVariableNode":
                    return new AliasStatement(false,
                        new ConstructedSymbol(node.GetProperty("new_name").GetProperty("name").GetString().TrimStart('$')),
                        new ConstructedSymbol(node.GetProperty("old_name").GetProperty("name").GetString().TrimStart('$')), span);
                case "UndefNode": {
                    var names = new List<ConstructedSymbol>();
                    foreach (var n in node.GetProperty("names").EnumerateArray()) names.Add(Symbol(n));
                    return new UndefineStatement(names, span);
                }

                case "RetryNode": return new RetryStatement(span);
                case "RedoNode": return new RedoStatement(span);

                case "ForNode": {
                    var forScope = new PaddingLexicalScope(CurrentScope);
                    _scopes.Push(forScope);
                    try {
                        var target = Target(node.GetProperty("index"));
                        var clv = target as CompoundLeftValue ?? new CompoundLeftValue(new[] { target });
                        Parameters parameters = clv.HasUnsplattedValue
                            ? new Parameters(RemoveAt(clv.LeftValues, clv.UnsplattedValueIndex), clv.UnsplattedValueIndex,
                                null, clv.UnsplattedValue, null, SourceSpan.None)
                            : new Parameters(clv.LeftValues, clv.LeftValues.Length, null, null, null, SourceSpan.None);
                        return new ForLoopExpression(forScope, parameters, Expr(node.GetProperty("collection")),
                            Statements(Opt(node, "statements")), span);
                    } finally {
                        _scopes.Pop();
                    }
                }

                case "InterpolatedSymbolNode": {
                    var parts = new List<Expression>();
                    foreach (var part in node.GetProperty("parts").EnumerateArray()) parts.Add(StringPart(part));
                    return new StringConstructor(parts, StringKind.Symbol, span);
                }
                case "XStringNode":
                    return new StringConstructor(
                        new List<Expression> { new StringLiteral(node.GetProperty("unescaped").GetString(), _encoding, span) },
                        StringKind.Command, span);
                case "InterpolatedXStringNode": {
                    var parts = new List<Expression>();
                    foreach (var part in node.GetProperty("parts").EnumerateArray()) parts.Add(StringPart(part));
                    return new StringConstructor(parts, StringKind.Command, span);
                }

                case "ConstantPathWriteNode":
                    return new SimpleAssignmentExpression(ConstantPath(node.GetProperty("target"), Span(node.GetProperty("target"))),
                        Expr(node.GetProperty("value")), null, span);

                case "PostExecutionNode": {
                    var endScope = new PaddingLexicalScope(CurrentScope);
                    _scopes.Push(endScope);
                    try {
                        return new ShutdownHandlerStatement(endScope, Statements(Opt(node, "statements")), span);
                    } finally {
                        _scopes.Pop();
                    }
                }

                case "SourceFileNode":
                    // prism never sees the file name; the DLR SourceUnit does
                    return new StringLiteral(_path ?? node.GetProperty("filepath").GetString(), _encoding, span);
                case "SourceLineNode":
                    return Literal.Integer(span.Start.Line, span);
                case "SourceEncodingNode":
                    return new EncodingExpression(span);

                default:
                    throw Unsupported(node);
            }
        }

        private static LeftValue/*!*/[]/*!*/ RemoveAt(LeftValue/*!*/[]/*!*/ values, int index) {
            var result = new LeftValue[values.Length - 1];
            Array.Copy(values, 0, result, 0, index);
            Array.Copy(values, index + 1, result, index, values.Length - index - 1);
            return result;
        }

        private ConstructedSymbol Symbol(JsonElement node) {
            if (Type(node) != "SymbolNode") throw Unsupported(node);
            return new ConstructedSymbol(node.GetProperty("unescaped").GetString());
        }

        private RubyRegexOptions RegexOptions(JsonElement node) {
            var options = RubyRegexOptions.NONE;
            if (HasFlag(node, "IGNORE_CASE")) options |= RubyRegexOptions.IgnoreCase;
            if (HasFlag(node, "EXTENDED")) options |= RubyRegexOptions.Extended;
            if (HasFlag(node, "MULTI_LINE")) options |= RubyRegexOptions.Multiline;
            if (HasFlag(node, "ONCE")) options |= RubyRegexOptions.Once;
            if (HasFlag(node, "EUC_JP")) options |= RubyRegexOptions.EUC;
            if (HasFlag(node, "WINDOWS_31J")) options |= RubyRegexOptions.SJIS;
            if (HasFlag(node, "UTF_8")) options |= RubyRegexOptions.UTF8;
            if (HasFlag(node, "ASCII_8BIT")) options |= RubyRegexOptions.FIXED;
            return options;
        }

        private Body/*!*/ BuildBeginBody(JsonElement node, SourceSpan span) {
            var statements = Statements(Opt(node, "statements"));
            List<RescueClause> rescues = null;
            var rescueNode = Opt(node, "rescue_clause");
            while (rescueNode.HasValue) {
                if (rescues == null) rescues = new List<RescueClause>();
                rescues.Add(Rescue(rescueNode.Value));
                rescueNode = Opt(rescueNode.Value, "subsequent");
            }
            var elseNode = Opt(node, "else_clause");
            var ensureNode = Opt(node, "ensure_clause");
            return new Body(statements, rescues,
                elseNode.HasValue ? Statements(Opt(elseNode.Value, "statements")) : null,
                ensureNode.HasValue ? Statements(Opt(ensureNode.Value, "statements")) : null, span);
        }

        private RescueClause/*!*/ Rescue(JsonElement node) {
            var types = new List<Expression>();
            foreach (var ex in node.GetProperty("exceptions").EnumerateArray()) types.Add(Argument(ex));
            var reference = Opt(node, "reference");
            return new RescueClause(types.ToArray(),
                reference.HasValue ? Target(reference.Value) : null,
                Statements(Opt(node, "statements")), Span(node));
        }

        private LeftValue/*!*/ Target(JsonElement node) {
            var span = Span(node);
            switch (Type(node)) {
                case "LocalVariableTargetNode": case "RequiredParameterNode":
                    return CurrentScope.ResolveOrAddVariable(node.GetProperty("name").GetString(), span);
                case "InstanceVariableTargetNode":
                    return new InstanceVariable(node.GetProperty("name").GetString(), span);
                case "GlobalVariableTargetNode":
                    return new IronRuby.Compiler.Ast.GlobalVariable(node.GetProperty("name").GetString().TrimStart('$'), span);
                case "ClassVariableTargetNode":
                    return new ClassVariable(node.GetProperty("name").GetString(), span);
                case "ConstantTargetNode":
                    return new ConstantVariable(node.GetProperty("name").GetString(), span);
                case "IndexTargetNode": {
                    var argsNode = Opt(node, "arguments");
                    return new ArrayItemAccess(Expr(node.GetProperty("receiver")),
                        argsNode.HasValue ? BuildArguments(argsNode.Value) : new Arguments(), null, span);
                }
                case "CallTargetNode":
                    return new AttributeAccess(Expr(node.GetProperty("receiver")),
                        node.GetProperty("name").GetString().TrimEnd('='), span);
                case "MultiTargetNode":
                    return CompoundTarget(node);
                default:
                    throw Unsupported(node);
            }
        }

        private CompoundLeftValue/*!*/ CompoundTarget(JsonElement node) {
            var lvs = new List<LeftValue>();
            foreach (var l in node.GetProperty("lefts").EnumerateArray()) lvs.Add(Target(l));
            int unsplatIndex = int.MaxValue;
            var rest = Opt(node, "rest");
            if (rest.HasValue) {
                unsplatIndex = lvs.Count;
                if (Type(rest.Value) == "SplatNode") {
                    var target = Opt(rest.Value, "expression");
                    lvs.Add(target.HasValue ? Target(target.Value) : Placeholder.Singleton);
                } else { // ImplicitRestNode: `a, = value`
                    lvs.Add(Placeholder.Singleton);
                }
            }
            foreach (var r in node.GetProperty("rights").EnumerateArray()) lvs.Add(Target(r));
            return unsplatIndex == int.MaxValue
                ? new CompoundLeftValue(lvs.ToArray())
                : new CompoundLeftValue(lvs.ToArray(), unsplatIndex);
        }

        private Expression/*!*/[]/*!*/ RhsFromValue(JsonElement value) {
            // prism wraps `a, b = 1, 2` into a synthesized ArrayNode (no opening bracket)
            if (Type(value) == "ArrayNode" && !Opt(value, "opening_loc").HasValue) {
                var rhs = new List<Expression>();
                foreach (var el in value.GetProperty("elements").EnumerateArray()) rhs.Add(Argument(el));
                return rhs.ToArray();
            }
            return new[] { Expr(value) };
        }

        private Expression/*!*/ StringPart(JsonElement part) {
            switch (Type(part)) {
                case "StringNode":
                    return new StringLiteral(part.GetProperty("unescaped").GetString(), _encoding, Span(part));
                case "EmbeddedStatementsNode":
                    return StatementsAsExpression(Opt(part, "statements"), Span(part));
                case "EmbeddedVariableNode":
                    return Expr(part.GetProperty("variable"));
                default:
                    return Expr(part); // nested interpolated strings (adjacent literals, heredocs)
            }
        }

        private ConstantVariable/*!*/ ConstantPath(JsonElement node, SourceSpan span) {
            string name = node.GetProperty("name").GetString();
            var parent = Opt(node, "parent");
            return parent.HasValue
                ? new ConstantVariable(Expr(parent.Value), name, span)
                : new ConstantVariable(name, span);
        }

        private Expression/*!*/ If(JsonElement node, SourceSpan span) {
            // flatten prism's nested subsequent (elsif = IfNode, else = ElseNode) into IfExpression clauses
            var elseIfClauses = new List<ElseIfClause>();
            var subsequent = Opt(node, "subsequent");
            while (subsequent.HasValue) {
                var sub = subsequent.Value;
                if (Type(sub) == "IfNode") {
                    elseIfClauses.Add(new ElseIfClause(Expr(sub.GetProperty("predicate")),
                        Statements(Opt(sub, "statements")), Span(sub)));
                    subsequent = Opt(sub, "subsequent");
                } else { // ElseNode
                    elseIfClauses.Add(new ElseIfClause(null, Statements(Opt(sub, "statements")), Span(sub)));
                    break;
                }
            }
            return new IfExpression(Expr(node.GetProperty("predicate")),
                Statements(Opt(node, "statements")), elseIfClauses, span);
        }

        private Arguments JumpArguments(JsonElement node) {
            var args = Opt(node, "arguments");
            return args.HasValue ? BuildArguments(args.Value) : null;
        }

        // ---- calls ----

        private Expression/*!*/ Call(JsonElement node, SourceSpan span) {
            if (HasFlag(node, "SAFE_NAVIGATION")) throw Unsupported(node);

            string name = node.GetProperty("name").GetString();

            // prism can't see locals defined outside an eval'd unit; it marks bare-word
            // reads as VARIABLE_CALL — resolve them against the scope chain first
            if (HasFlag(node, "VARIABLE_CALL")) {
                var local = CurrentScope.ResolveVariable(name);
                if (local != null) return local;
            }

            if (HasFlag(node, "ATTRIBUTE_WRITE")) {
                // a.foo = v / a[i] = v: expression value is the RHS, so use assignment nodes
                var writeArgs = new List<JsonElement>();
                foreach (var a in node.GetProperty("arguments").GetProperty("arguments").EnumerateArray()) writeArgs.Add(a);
                var rhs = Expr(writeArgs[writeArgs.Count - 1]);
                var target = Expr(node.GetProperty("receiver"));
                LeftValue lhs;
                if (name == "[]=") {
                    var indexArgs = new List<Expression>();
                    for (int i = 0; i < writeArgs.Count - 1; i++) indexArgs.Add(Argument(writeArgs[i]));
                    lhs = new ArrayItemAccess(target, new Arguments(indexArgs.ToArray()), null, span);
                } else {
                    lhs = new AttributeAccess(target, name.TrimEnd('='), span);
                }
                return new SimpleAssignmentExpression(lhs, rhs, null, span);
            }
            var receiverNode = Opt(node, "receiver");
            Expression receiver = receiverNode.HasValue ? Expr(receiverNode.Value) : null;

            var argsNode = Opt(node, "arguments");
            Arguments args = argsNode.HasValue ? BuildArguments(argsNode.Value) : null;

            Block block = null;
            var blockNode = Opt(node, "block");
            if (blockNode.HasValue) {
                if (Type(blockNode.Value) == "BlockNode") {
                    block = BlockDef(blockNode.Value);
                } else { // BlockArgumentNode (&proc)
                    var blockExpr = Opt(blockNode.Value, "expression");
                    if (!blockExpr.HasValue) throw Unsupported(blockNode.Value);
                    block = new BlockReference(Expr(blockExpr.Value), Span(blockNode.Value));
                }
            }

            return new MethodCall(receiver, name, args, block, span);
        }

        private Expression/*!*/ Argument(JsonElement node) {
            if (Type(node) == "SplatNode") {
                var expr = Opt(node, "expression");
                if (!expr.HasValue) throw Unsupported(node);
                return new SplattedArgument(Expr(expr.Value));
            }
            return Expr(node);
        }

        private Arguments/*!*/ BuildArguments(JsonElement argumentsNode) {
            var exprs = new List<Expression>();
            foreach (var arg in argumentsNode.GetProperty("arguments").EnumerateArray()) {
                exprs.Add(Argument(arg));
            }
            return new Arguments(exprs.ToArray());
        }

        // ---- definitions ----

        private BlockDefinition/*!*/ BlockDef(JsonElement node) {
            var span = Span(node);
            var scope = new BlockLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                Parameters parameters = null;
                var paramsNode = Opt(node, "parameters");
                if (paramsNode.HasValue) {
                    if (Type(paramsNode.Value) != "BlockParametersNode") throw Unsupported(paramsNode.Value);
                    var inner = Opt(paramsNode.Value, "parameters");
                    if (inner.HasValue) parameters = BuildParameters(inner.Value);
                }
                var body = Statements(Opt(node, "body"));
                return new BlockDefinition(scope, parameters, body, span);
            } finally {
                _scopes.Pop();
            }
        }

        private Expression/*!*/ Def(JsonElement node, SourceSpan span) {
            var receiverNode = Opt(node, "receiver");
            Expression target = receiverNode.HasValue ? Expr(receiverNode.Value) : null;
            var scope = new MethodLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                Parameters parameters = Parameters.Empty;
                var paramsNode = Opt(node, "parameters");
                if (paramsNode.HasValue) parameters = BuildParameters(paramsNode.Value);
                var body = DefinitionBody(node, span);
                return new MethodDefinition(scope, target, node.GetProperty("name").GetString(), parameters, body, span);
            } finally {
                _scopes.Pop();
            }
        }

        private Body/*!*/ DefinitionBody(JsonElement node, SourceSpan span) {
            var bodyNode = Opt(node, "body");
            Statements statements;
            if (!bodyNode.HasValue) {
                statements = new Statements();
            } else if (Type(bodyNode.Value) == "StatementsNode") {
                statements = Statements(bodyNode.Value);
            } else if (Type(bodyNode.Value) == "BeginNode") {
                return BuildBeginBody(bodyNode.Value, span);
            } else {
                throw Unsupported(bodyNode.Value);
            }
            return new Body(statements, null, null, null, span);
        }

        private Expression/*!*/ Class(JsonElement node, SourceSpan span) {
            var name = ClassName(node.GetProperty("constant_path"));
            var superNode = Opt(node, "superclass");
            Expression superClass = superNode.HasValue ? Expr(superNode.Value) : null;
            var scope = new ClassLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                return new ClassDefinition(scope, name, superClass, DefinitionBody(node, span), span);
            } finally {
                _scopes.Pop();
            }
        }

        private Expression/*!*/ Module(JsonElement node, SourceSpan span) {
            var name = ClassName(node.GetProperty("constant_path"));
            var scope = new TopLocalDefinitionLexicalScope(CurrentScope);
            _scopes.Push(scope);
            try {
                return new ModuleDefinition(scope, name, DefinitionBody(node, span), span);
            } finally {
                _scopes.Pop();
            }
        }

        private ConstantVariable/*!*/ ClassName(JsonElement constantPath) {
            var span = Span(constantPath);
            if (Type(constantPath) == "ConstantReadNode") {
                return new ConstantVariable(constantPath.GetProperty("name").GetString(), span);
            }
            return ConstantPath(constantPath, span);
        }

        private Parameters/*!*/ BuildParameters(JsonElement node) {
            var span = Span(node);
            if (Opt(node, "keywords").HasValue && node.GetProperty("keywords").GetArrayLength() > 0) throw Unsupported(node);
            if (Opt(node, "keyword_rest").HasValue) throw Unsupported(node);

            var mandatory = new List<LeftValue>();
            foreach (var req in node.GetProperty("requireds").EnumerateArray()) {
                if (Type(req) == "RequiredParameterNode") {
                    mandatory.Add(CurrentScope.ResolveOrAddVariable(req.GetProperty("name").GetString(), Span(req)));
                } else if (Type(req) == "MultiTargetNode") {
                    mandatory.Add(CompoundTarget(req)); // destructured param |a, (b, c)|
                } else {
                    throw Unsupported(req);
                }
            }
            int leadingMandatoryCount = mandatory.Count;

            var optional = new List<SimpleAssignmentExpression>();
            foreach (var opt in node.GetProperty("optionals").EnumerateArray()) {
                var lhs = CurrentScope.ResolveOrAddVariable(opt.GetProperty("name").GetString(), Span(opt));
                optional.Add(new SimpleAssignmentExpression(lhs, Expr(opt.GetProperty("value")), null, Span(opt)));
            }

            LeftValue unsplat = null;
            var rest = Opt(node, "rest");
            if (rest.HasValue) {
                var restSpan = Span(rest.Value);
                if (Type(rest.Value) == "ImplicitRestNode") {
                    // |a,| trailing comma: hidden local, same as the legacy parser
                    unsplat = CurrentScope.ResolveOrAddVariable(Symbols.RestArgsLocal, restSpan);
                } else {
                    var restName = Opt(rest.Value, "name");
                    unsplat = CurrentScope.ResolveOrAddVariable(
                        restName.HasValue ? restName.Value.GetString() : Symbols.RestArgsLocal, restSpan);
                }
            }

            foreach (var post in node.GetProperty("posts").EnumerateArray()) {
                if (Type(post) != "RequiredParameterNode") throw Unsupported(post);
                mandatory.Add(CurrentScope.AddVariable(post.GetProperty("name").GetString(), Span(post)));
            }

            LocalVariable blockParam = null;
            var block = Opt(node, "block");
            if (block.HasValue) {
                var blockName = Opt(block.Value, "name");
                if (!blockName.HasValue) throw Unsupported(block.Value);
                blockParam = CurrentScope.ResolveOrAddVariable(blockName.Value.GetString(), Span(block.Value));
            }

            return new Parameters(mandatory.ToArray(), leadingMandatoryCount,
                optional.Count > 0 ? optional.ToArray() : null, unsplat, blockParam, span);
        }
    }
}
