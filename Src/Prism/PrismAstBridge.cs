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
        private readonly List<int>/*!*/ _lineStarts;
        private readonly RubyEncoding/*!*/ _encoding;
        private readonly Stack<LexicalScope>/*!*/ _scopes = new Stack<LexicalScope>();

        private PrismAstBridge(string/*!*/ source, RubyEncoding/*!*/ encoding) {
            _source = source;
            _encoding = encoding;
            _lineStarts = new List<int> { 0 };
            for (int i = 0; i < source.Length; i++) {
                if (source[i] == '\n') _lineStarts.Add(i + 1);
            }
        }

        public static SourceUnitTree Parse(SourceUnit/*!*/ sourceUnit, RubyCompilerOptions/*!*/ options, ErrorSink/*!*/ errorSink) {
            string code = sourceUnit.GetCode();
            var bridge = new PrismAstBridge(code, RubyEncoding.UTF8);
            using (var doc = JsonDocument.Parse(PrismParser.ParseToJson(code))) {
                return bridge.Program(doc.RootElement);
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

        private SourceUnitTree/*!*/ Program(JsonElement node) {
            var scope = new TopStaticLexicalScope(null);
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
                    return new IronRuby.Compiler.Ast.GlobalVariable(node.GetProperty("name").GetString(), span);
                case "GlobalVariableWriteNode":
                    return new SimpleAssignmentExpression(
                        new IronRuby.Compiler.Ast.GlobalVariable(node.GetProperty("name").GetString(), span),
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

                default:
                    throw Unsupported(node);
            }
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
                    throw Unsupported(part);
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
            } else {
                throw Unsupported(bodyNode.Value); // BeginNode (rescue/ensure) not mapped yet
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
                if (Type(req) != "RequiredParameterNode") throw Unsupported(req);
                mandatory.Add(CurrentScope.AddVariable(req.GetProperty("name").GetString(), Span(req)));
            }
            int leadingMandatoryCount = mandatory.Count;

            var optional = new List<SimpleAssignmentExpression>();
            foreach (var opt in node.GetProperty("optionals").EnumerateArray()) {
                var lhs = CurrentScope.AddVariable(opt.GetProperty("name").GetString(), Span(opt));
                optional.Add(new SimpleAssignmentExpression(lhs, Expr(opt.GetProperty("value")), null, Span(opt)));
            }

            LeftValue unsplat = null;
            var rest = Opt(node, "rest");
            if (rest.HasValue) {
                var restName = Opt(rest.Value, "name");
                if (!restName.HasValue) throw Unsupported(rest.Value);
                unsplat = CurrentScope.AddVariable(restName.Value.GetString(), Span(rest.Value));
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
                blockParam = CurrentScope.AddVariable(blockName.Value.GetString(), Span(block.Value));
            }

            return new Parameters(mandatory.ToArray(), leadingMandatoryCount,
                optional.Count > 0 ? optional.ToArray() : null, unsplat, blockParam, span);
        }
    }
}
