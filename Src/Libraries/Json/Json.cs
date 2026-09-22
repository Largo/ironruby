/* ****************************************************************************
 *
 * JSON for IronRuby.
 *
 * MRI implements json as a C extension, so this is a C# reimplementation in
 * the same spirit as the Digest, Zlib and StringIO libraries here — the
 * approach JRuby also takes for the C-backed parts of the standard library.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Json {

    [RubyModule("JSON")]
    public static class JsonModule {

        #region errors

        [RubyClass("JSONError", Extends = typeof(JsonError), Inherits = typeof(SystemException))]
        public static class JsonErrorOps {
        }

        [RubyClass("ParserError", Extends = typeof(JsonParserError), Inherits = typeof(JsonError))]
        public static class JsonParserErrorOps {
        }

        [RubyClass("GeneratorError", Extends = typeof(JsonGeneratorError), Inherits = typeof(JsonError))]
        public static class JsonGeneratorErrorOps {
        }

        #endregion

        #region generate

        /// <summary>
        /// The layout half of the generator's options - what the json gem calls indent,
        /// space, space_before, object_nl and array_nl.  A null Layout is the compact
        /// output JSON.generate produces when it is given no options.
        /// </summary>
        private sealed class Layout {
            public string Indent = "";
            public string Space = "";
            public string SpaceBefore = "";
            public string ObjectNewLine = "";
            public string ArrayNewLine = "";

            public static Layout FromOptions(Hash options) {
                if (options == null || options.Count == 0) {
                    return null;
                }
                var layout = new Layout {
                    Indent = StringOption(options, "indent"),
                    Space = StringOption(options, "space"),
                    SpaceBefore = StringOption(options, "space_before"),
                    ObjectNewLine = StringOption(options, "object_nl"),
                    ArrayNewLine = StringOption(options, "array_nl"),
                };
                return layout.IsCompact ? null : layout;
            }

            public bool IsCompact {
                get {
                    return Indent.Length == 0 && Space.Length == 0 && SpaceBefore.Length == 0
                        && ObjectNewLine.Length == 0 && ArrayNewLine.Length == 0;
                }
            }

            public static readonly Layout Pretty = new Layout {
                Indent = "  ", Space = " ", ObjectNewLine = "\n", ArrayNewLine = "\n"
            };
        }

        private static string/*!*/ StringOption(Hash/*!*/ options, string/*!*/ name) {
            foreach (var entry in options) {
                var key = entry.Key;
                var keyString = key as MutableString;
                var keySymbol = key as RubySymbol;
                string spelled = keyString != null ? keyString.ConvertToString() :
                    keySymbol != null ? keySymbol.ToString() : null;
                if (spelled != name) {
                    continue;
                }
                var value = entry.Value as MutableString;
                return value != null ? value.ConvertToString() : "";
            }
            return "";
        }

        // JSON.generate(obj, opts) - opts being the json gem's generator options.  A
        // JSON::State arrives here as its own to_h, from JSON::State#generate, so what
        // this sees is always a Hash.  Only the layout options change the output; the
        // rest (max_nesting, allow_nan, ...) are accepted and ignored, the way the gem
        // ignores an option it does not know.
        [RubyMethod("generate", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("dump", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Generate(RubyContext/*!*/ context, object self, object obj,
            [DefaultParameterValue(null)]Hash options) {
            var builder = new StringBuilder();
            WriteValue(context, builder, obj, Layout.FromOptions(options), 0);
            return MutableString.Create(builder.ToString(), RubyEncoding.UTF8);
        }

        [RubyMethod("pretty_generate", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ PrettyGenerate(RubyContext/*!*/ context, object self, object obj,
            [DefaultParameterValue(null)]Hash options) {
            var builder = new StringBuilder();
            WriteValue(context, builder, obj, Layout.FromOptions(options) ?? Layout.Pretty, 0);
            return MutableString.Create(builder.ToString(), RubyEncoding.UTF8);
        }

        private static void WriteValue(RubyContext/*!*/ context, StringBuilder/*!*/ builder, object obj, Layout layout, int depth) {
            if (depth > 100) {
                throw new JsonGeneratorError("nesting of 100 is too deep");
            }

            if (obj == null) {
                builder.Append("null");
                return;
            }

            if (obj is bool) {
                builder.Append((bool)obj ? "true" : "false");
                return;
            }

            if (obj is int || obj is BigInteger || obj is long) {
                builder.Append(obj.ToString());
                return;
            }

            if (obj is double) {
                double value = (double)obj;
                if (Double.IsNaN(value) || Double.IsInfinity(value)) {
                    throw new JsonGeneratorError(String.Format("{0} not allowed in JSON", value));
                }
                builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var str = obj as MutableString;
            if (str != null) {
                WriteString(builder, str.ConvertToString());
                return;
            }

            var symbol = obj as RubySymbol;
            if (symbol != null) {
                WriteString(builder, symbol.ToString());
                return;
            }

            var hash = obj as Hash;
            if (hash != null) {
                WriteHash(context, builder, hash, layout, depth);
                return;
            }

            var list = obj as IList<object>;
            if (list != null) {
                WriteArray(context, builder, list, layout, depth);
                return;
            }

            // anything else is rendered through to_s, as the json gem does
            WriteString(builder, context.Inspect(obj).ConvertToString());
        }

        private static void WriteHash(RubyContext/*!*/ context, StringBuilder/*!*/ builder, Hash/*!*/ hash, Layout layout, int depth) {
            builder.Append('{');
            bool first = true;
            foreach (var entry in hash) {
                if (!first) {
                    builder.Append(',');
                }
                first = false;
                NewLine(builder, layout, layout == null ? null : layout.ObjectNewLine, depth + 1);

                // JSON object keys are always strings
                var key = entry.Key;
                var keyString = key as MutableString;
                var keySymbol = key as RubySymbol;
                if (keyString != null) {
                    WriteString(builder, keyString.ConvertToString());
                } else if (keySymbol != null) {
                    WriteString(builder, keySymbol.ToString());
                } else if (key == null) {
                    WriteString(builder, "");
                } else {
                    WriteString(builder, key.ToString());
                }

                if (layout != null) {
                    builder.Append(layout.SpaceBefore);
                }
                builder.Append(':');
                if (layout != null) {
                    builder.Append(layout.Space);
                }
                WriteValue(context, builder, entry.Value, layout, depth + 1);
            }
            if (!first) {
                CloseLine(builder, layout, layout == null ? null : layout.ObjectNewLine, depth);
            }
            builder.Append('}');
        }

        private static void WriteArray(RubyContext/*!*/ context, StringBuilder/*!*/ builder, IList<object>/*!*/ list, Layout layout, int depth) {
            builder.Append('[');
            for (int i = 0; i < list.Count; i++) {
                if (i > 0) {
                    builder.Append(',');
                }
                NewLine(builder, layout, layout == null ? null : layout.ArrayNewLine, depth + 1);
                WriteValue(context, builder, list[i], layout, depth + 1);
            }
            if (list.Count > 0) {
                CloseLine(builder, layout, layout == null ? null : layout.ArrayNewLine, depth);
            }
            builder.Append(']');
        }

        private static void NewLine(StringBuilder/*!*/ builder, Layout layout, string newLine, int depth) {
            if (layout == null) {
                return;
            }
            builder.Append(newLine);
            for (int i = 0; i < depth; i++) {
                builder.Append(layout.Indent);
            }
        }

        // Before the closing bracket the json gem writes nothing at all when the
        // collection's newline option is empty - unlike between elements, where it still
        // indents.  `JSON.generate(x, indent: "\t")` with no array_nl is the case that
        // tells them apart.
        private static void CloseLine(StringBuilder/*!*/ builder, Layout layout, string newLine, int depth) {
            if (layout == null || String.IsNullOrEmpty(newLine)) {
                return;
            }
            NewLine(builder, layout, newLine, depth);
        }

        private static void WriteString(StringBuilder/*!*/ builder, string/*!*/ value) {
            builder.Append('"');
            foreach (char c in value) {
                switch (c) {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20) {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        } else {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
        }

        #endregion

        #region parse

        [RubyMethod("parse", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        public static object Parse(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]MutableString/*!*/ source,
            [DefaultParameterValue(null)]Hash options) {

            var parser = new JsonParser(source.ConvertToString(), context);
            parser.SymbolizeNames = TruthyOption(context, options, "symbolize_names");
            object result = parser.ParseValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd) {
                throw new JsonParserError("unexpected token at '" + parser.Rest + "'");
            }
            return result;
        }

        /// <summary>
        /// One keyword of the options Hash JSON.parse takes.  Only the options this parser
        /// can honour are read: symbolize_names, and allow_comments, which needs no flag
        /// because this parser has always skipped comments.  An option it does not know is
        /// ignored rather than rejected, the way the json gem ignores unknown keys.
        /// </summary>
        private static bool TruthyOption(RubyContext/*!*/ context, Hash options, string/*!*/ name) {
            if (options == null) {
                return false;
            }
            object value;
            if (!options.TryGetValue(context.CreateSymbol(name, RubyEncoding.UTF8), out value)) {
                return false;
            }
            return RubyOps.IsTrue(value);
        }

        #endregion
    }

    #region exception types

    public class JsonError : SystemException {
        public JsonError(string message) : base(message) { }
        public JsonError() : this("JSON error") { }
    }

    public class JsonParserError : JsonError {
        public JsonParserError(string message) : base(message) { }
        public JsonParserError() : this("parser error") { }
    }

    public class JsonGeneratorError : JsonError {
        public JsonGeneratorError(string message) : base(message) { }
        public JsonGeneratorError() : this("generator error") { }
    }

    #endregion

    /// <summary>
    /// A straightforward recursive-descent JSON reader. Produces IronRuby values
    /// directly (Hash, RubyArray, MutableString, int/BigInteger, double, bool, null).
    /// </summary>
    internal sealed class JsonParser {
        private readonly string/*!*/ _source;
        private readonly RubyContext/*!*/ _context;
        private int _position;

        /// <summary>
        /// JSON.parse(source, symbolize_names: true) answers Symbol keys.
        /// </summary>
        internal bool SymbolizeNames { get; set; }

        internal JsonParser(string/*!*/ source, RubyContext/*!*/ context) {
            _source = source;
            _context = context;
        }

        internal bool AtEnd {
            get { return _position >= _source.Length; }
        }

        internal string Rest {
            get { return _position < _source.Length ? _source.Substring(_position, Math.Min(10, _source.Length - _position)) : ""; }
        }

        internal void SkipWhitespace() {
            while (_position < _source.Length) {
                char c = _source[_position];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
                    _position++;
                } else if (c == '/' && _position + 1 < _source.Length && _source[_position + 1] == '/') {
                    while (_position < _source.Length && _source[_position] != '\n') _position++;
                } else if (c == '/' && _position + 1 < _source.Length && _source[_position + 1] == '*') {
                    _position += 2;
                    while (_position + 1 < _source.Length &&
                           !(_source[_position] == '*' && _source[_position + 1] == '/')) {
                        _position++;
                    }
                    _position = Math.Min(_position + 2, _source.Length);
                } else {
                    break;
                }
            }
        }

        internal object ParseValue(int depth) {
            if (depth > 100) {
                throw new JsonParserError("nesting of 100 is too deep");
            }

            SkipWhitespace();
            if (AtEnd) {
                throw new JsonParserError("unexpected end of input");
            }

            char c = _source[_position];
            switch (c) {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"': return MutableString.Create(ParseString(), RubyEncoding.UTF8);
                case 't': Expect("true"); return ScriptingRuntimeHelpers.True;
                case 'f': Expect("false"); return ScriptingRuntimeHelpers.False;
                case 'n': Expect("null"); return null;
                default: return ParseNumber();
            }
        }

        private void Expect(string/*!*/ word) {
            if (_position + word.Length > _source.Length || String.CompareOrdinal(_source, _position, word, 0, word.Length) != 0) {
                throw new JsonParserError("unexpected token at '" + Rest + "'");
            }
            _position += word.Length;
        }

        private object ParseObject(int depth) {
            _position++; // '{'
            var hash = new Hash(_context);
            SkipWhitespace();
            if (!AtEnd && _source[_position] == '}') {
                _position++;
                return hash;
            }

            while (true) {
                SkipWhitespace();
                if (AtEnd || _source[_position] != '"') {
                    throw new JsonParserError("expected object key at '" + Rest + "'");
                }
                string name = ParseString();
                object key = SymbolizeNames
                    ? (object)_context.CreateSymbol(name, RubyEncoding.UTF8)
                    : MutableString.Create(name, RubyEncoding.UTF8);
                SkipWhitespace();
                if (AtEnd || _source[_position] != ':') {
                    throw new JsonParserError("expected ':' at '" + Rest + "'");
                }
                _position++;
                hash[key] = ParseValue(depth + 1);

                SkipWhitespace();
                if (AtEnd) {
                    throw new JsonParserError("unexpected end of input");
                }
                if (_source[_position] == ',') {
                    _position++;
                    continue;
                }
                if (_source[_position] == '}') {
                    _position++;
                    return hash;
                }
                throw new JsonParserError("expected ',' or '}' at '" + Rest + "'");
            }
        }

        private object ParseArray(int depth) {
            _position++; // '['
            var array = new RubyArray();
            SkipWhitespace();
            if (!AtEnd && _source[_position] == ']') {
                _position++;
                return array;
            }

            while (true) {
                array.Add(ParseValue(depth + 1));
                SkipWhitespace();
                if (AtEnd) {
                    throw new JsonParserError("unexpected end of input");
                }
                if (_source[_position] == ',') {
                    _position++;
                    continue;
                }
                if (_source[_position] == ']') {
                    _position++;
                    return array;
                }
                throw new JsonParserError("expected ',' or ']' at '" + Rest + "'");
            }
        }

        private string/*!*/ ParseString() {
            _position++; // opening quote
            var builder = new StringBuilder();
            while (true) {
                if (AtEnd) {
                    throw new JsonParserError("unterminated string");
                }
                char c = _source[_position++];
                if (c == '"') {
                    return builder.ToString();
                }
                if (c != '\\') {
                    builder.Append(c);
                    continue;
                }

                if (AtEnd) {
                    throw new JsonParserError("unterminated escape");
                }
                char escape = _source[_position++];
                switch (escape) {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (_position + 4 > _source.Length) {
                            throw new JsonParserError("invalid \\u escape");
                        }
                        builder.Append((char)Int32.Parse(_source.Substring(_position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _position += 4;
                        break;
                    default:
                        throw new JsonParserError("invalid escape '\\" + escape + "'");
                }
            }
        }

        private object ParseNumber() {
            int start = _position;
            if (!AtEnd && (_source[_position] == '-' || _source[_position] == '+')) {
                _position++;
            }
            bool isFloat = false;
            while (!AtEnd) {
                char c = _source[_position];
                if (c >= '0' && c <= '9') {
                    _position++;
                } else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') {
                    isFloat = true;
                    _position++;
                } else {
                    break;
                }
            }

            string text = _source.Substring(start, _position - start);
            if (text.Length == 0) {
                throw new JsonParserError("unexpected token at '" + Rest + "'");
            }

            if (isFloat) {
                double result;
                if (!Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) {
                    throw new JsonParserError("invalid number '" + text + "'");
                }
                return result;
            }

            int intResult;
            if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intResult)) {
                return ScriptingRuntimeHelpers.Int32ToObject(intResult);
            }

            BigInteger bigResult;
            if (BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out bigResult)) {
                return bigResult;
            }

            throw new JsonParserError("invalid number '" + text + "'");
        }
    }
}
