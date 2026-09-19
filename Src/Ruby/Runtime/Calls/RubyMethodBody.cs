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

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Compiler.Generation;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

namespace IronRuby.Runtime.Calls {
    /// <summary>
    /// Represents a Ruby method body AST. Multiple RubyMethodInfos can share the same instance.
    /// </summary>
    public sealed class RubyMethodBody {
        private readonly MethodDeclaration/*!*/ _ast;
        private readonly MSA.SymbolDocumentInfo _document;
        private readonly RubyEncoding/*!*/ _encoding;

        private Delegate _delegate;
        private RubyModule _delegateModule;
        private ConditionalWeakTable<RubyModule, Delegate> _moduleVariants;

        internal RubyMethodBody(MethodDeclaration/*!*/ ast, MSA.SymbolDocumentInfo document, RubyEncoding/*!*/ encoding) {
            Assert.NotNull(ast, encoding);

            _ast = ast;
            _document = document;
            _encoding = encoding;
        }

        /// <summary>
        /// Set by Module#ruby2_keywords. It lives on the body rather than on the method info so
        /// that it is shared with every alias of the method, which is what MRI does - marking a
        /// method marks the aliases made of it, before or after.
        /// </summary>
        private bool _ruby2Keywords;

        public bool Ruby2Keywords {
            get { return _ruby2Keywords; }
            set { _ruby2Keywords = value; }
        }

        public MethodDeclaration Ast { get { return _ast; } }

        // Set once the call of this method with a block it ignores has been warned about (or would
        // have been, had warnings been on): MRI warns once per method definition.
        internal bool UnusedBlockReported { get; set; }
        public MSA.SymbolDocumentInfo Document { get { return _document; } }
        public bool HasTarget { get { return _ast.Target != null; } }
        public string/*!*/ Name { get { return _ast.Name; } }

        // The lexical modules the first compilation was made for, when they run through the
        // singleton class of a plain object; null otherwise. See GetDelegate.
        private RubyModule[] _singletonLexicalModules;
        private List<KeyValuePair<RubyModule[], Delegate>> _lexicalVariants;

        internal Delegate GetDelegate(RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule) {
            if (_delegate == null) {
                lock (this) {
                    if (_delegate == null) {
                        _singletonLexicalModules = GetObjectSingletonLexicalModules(declaringScope);
                        _delegateModule = declaringModule;
                        _delegate = Compile(declaringScope, declaringModule);
                    }
                }
            }

            // The declaring module is baked in as well, and `super' looks up from it. A `def' run
            // for more than one module - `Class.new { def initialize; super; end }' made twice,
            // class_eval in a loop - gets a compilation per module.
            if (declaringModule != _delegateModule && _singletonLexicalModules == null) {
                lock (this) {
                    if (_moduleVariants == null) {
                        _moduleVariants = new ConditionalWeakTable<RubyModule, Delegate>();
                    }
                    Delegate result;
                    if (!_moduleVariants.TryGetValue(declaringModule, out result)) {
                        result = Compile(declaringScope, declaringModule);
                        _moduleVariants.Add(declaringModule, result);
                    }
                    return result;
                }
            }

            // The declaring scope is baked into the compiled body, which is shared by every
            // execution of the `def'. For `class << obj' run once per obj (in a loop, a method)
            // each run has its own singleton class, and constants inside the method have to be
            // looked up there, so such a body is compiled once per lexical module chain.
            if (_singletonLexicalModules != null) {
                var modules = GetLexicalModules(declaringScope);
                if (!SameModules(modules, _singletonLexicalModules)) {
                    lock (this) {
                        if (_lexicalVariants == null) {
                            _lexicalVariants = new List<KeyValuePair<RubyModule[], Delegate>>();
                        }
                        foreach (var variant in _lexicalVariants) {
                            if (SameModules(modules, variant.Key)) {
                                return variant.Value;
                            }
                        }
                        var result = Compile(declaringScope, declaringModule);
                        _lexicalVariants.Add(new KeyValuePair<RubyModule[], Delegate>(modules, result));
                        return result;
                    }
                }
            }

            return _delegate;
        }

        private Delegate/*!*/ Compile(RubyScope/*!*/ declaringScope, RubyModule/*!*/ declaringModule) {
            // TODO: remove options
            AstGenerator gen = new AstGenerator(declaringScope.RubyContext, new RubyCompilerOptions(), _document, _encoding, false);
            MSA.LambdaExpression lambda = _ast.TransformBody(gen, declaringScope, declaringModule);
            return RubyScriptCode.CompileLambda(lambda, declaringScope.RubyContext);
        }

        private static RubyModule/*!*/[]/*!*/ GetLexicalModules(RubyScope/*!*/ scope) {
            var result = new List<RubyModule>();
            for (RubyScope s = scope; s != null; s = s.Parent) {
                if (s.Module != null) {
                    result.Add(s.Module);
                }
            }
            return result.ToArray();
        }

        private static RubyModule[] GetObjectSingletonLexicalModules(RubyScope/*!*/ scope) {
            var modules = GetLexicalModules(scope);
            foreach (var module in modules) {
                var cls = module as RubyClass;
                if (cls != null && cls.IsSingletonClass && !(cls.SingletonClassOf is RubyModule)) {
                    return modules;
                }
            }
            return null;
        }

        private static bool SameModules(RubyModule/*!*/[]/*!*/ a, RubyModule/*!*/[]/*!*/ b) {
            if (a.Length != b.Length) {
                return false;
            }
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) {
                    return false;
                }
            }
            return true;
        }
    }
}
