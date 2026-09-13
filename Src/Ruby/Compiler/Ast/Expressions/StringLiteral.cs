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
#if FEATURE_CORE_DLR
using MSA = System.Linq.Expressions;
#else
using MSA = Microsoft.Scripting.Ast;
#endif

using System.Diagnostics;
using Microsoft.Scripting;
using IronRuby.Builtins;
using System.Runtime.CompilerServices;

namespace IronRuby.Compiler.Ast {
    using AstUtils = Microsoft.Scripting.Ast.Utils;

    /// <summary>
    /// Represents a string literal.
    /// </summary>
    public partial class StringLiteral : Expression {
        // string or byte[]
        private readonly object/*!*/ _value;

        // TODO: we can save memory if we subclass StringLiteral (EncodedStringLiteral, EncodedSymbolLiteral) for _encoding != __ENCODING__
        private readonly RubyEncoding/*!*/ _encoding;

        // Whether the literal is under `# frozen_string_literal: true` (or -\-enable=frozen-string-literal).
        private readonly bool _isFrozen;

        internal StringLiteral(object/*!*/ value, RubyEncoding/*!*/ encoding, SourceSpan location)
            : this(value, encoding, false, location) {
        }

        internal StringLiteral(object/*!*/ value, RubyEncoding/*!*/ encoding, bool isFrozen, SourceSpan location) 
            : base(location) {
            Debug.Assert(value is string || value is byte[]);
            Debug.Assert(encoding != null);
            _value = value;
            _encoding = encoding;
            _isFrozen = isFrozen;
        }

        public bool IsFrozen {
            get { return _isFrozen; }
        }

        public object/*!*/ Value {
            get { return _value; }
        }

        public RubyEncoding/*!*/ Encoding {
            get { return _encoding; }
        }

        public MutableString/*!*/ GetMutableString() {
            string str = _value as string;
            if (str != null) {
                return MutableString.Create(str, _encoding);
            } else {
                return MutableString.CreateBinary((byte[])_value, _encoding);
            }
        }

        internal override MSA.Expression/*!*/ TransformRead(AstGenerator/*!*/ gen) {
            return _isFrozen ? TransformFrozen(_value, _encoding) : Transform(_value, _encoding);
        }

        /// <summary>
        /// A frozen literal is created once and then handed out again on every evaluation, so the
        /// StrongBox belongs to this one literal in the program - the same shape the regexp
        /// literals use for their cache.
        /// </summary>
        internal static MSA.Expression/*!*/ TransformFrozen(object/*!*/ value, RubyEncoding/*!*/ encoding) {
            var cache = AstUtils.Constant(new StrongBox<MutableString>(null));
            if (value is string) {
                return Methods.CreateFrozenMutableStringL.OpCall(AstUtils.Constant(value), encoding.Expression, cache);
            } else {
                return Methods.CreateFrozenMutableStringB.OpCall(AstUtils.Constant(value), encoding.Expression, cache);
            }
        }

        internal static MSA.Expression/*!*/ Transform(object/*!*/ value, RubyEncoding/*!*/ encoding) {
            if (value is string) {
                return Methods.CreateMutableStringL.OpCall(AstUtils.Constant(value), encoding.Expression);
            } else {
                return Methods.CreateMutableStringB.OpCall(AstUtils.Constant(value), encoding.Expression);
            }
        }
    }
}
