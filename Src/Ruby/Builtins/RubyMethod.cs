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
using System.Linq.Expressions;
#else
using Microsoft.Scripting.Ast;
#endif

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Compiler;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Runtime;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Builtins {
    using Ast = Expression;
    using BlockCallTargetUnsplatN = Func<BlockParam, object, object[], RubyArray, object>;

    [DebuggerDisplay("{GetDebugView(), nq}")]
    public partial class RubyMethod : IDuplicable {
        private readonly object _target;
        private readonly string/*!*/ _name;
        private readonly RubyMemberInfo/*!*/ _info;
        private BlockDispatcherUnsplatN _procDispatcher;

        /// <summary>
        /// Set for a method that stands in for one the object only answers through
        /// #method_missing: it reports the name that was asked for, and calls #method_missing
        /// with that name pushed in front of the arguments. This used to be a subclass, which
        /// gave it a Ruby class of its own - MRI hands back a plain Method, and ruby/spec checks
        /// it with #instance_of?.
        /// </summary>
        private readonly string _methodMissingName;

        public bool IsMethodMissing {
            get { return _methodMissingName != null; }
        }

        public object Target {
            get { return _target; }
        }

        public RubyMemberInfo/*!*/ Info {
            get { return _info; }
        }

        public string/*!*/ Name {
            get { return _name; } 
        }

        public RubyMethod(object target, RubyMemberInfo/*!*/ info, string/*!*/ name) {
            ContractUtils.RequiresNotNull(info, "info");
            ContractUtils.RequiresNotNull(name, "name");
                        
            _target = target;
            _info = info;
            _name = name;
        }

        private RubyMethod(object target, RubyMemberInfo/*!*/ info, string/*!*/ name, string methodMissingName)
            : this(target, info, name) {
            _methodMissingName = methodMissingName;
        }

        /// <summary>
        /// A Method standing in for a name the object only answers through #method_missing.
        /// </summary>
        public static RubyMethod/*!*/ CreateMethodMissing(object target, RubyMemberInfo/*!*/ info, string/*!*/ name) {
            return new RubyMethod(target, info, name, name);
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = new RubyMethod(_target, _info, _name, _methodMissingName);
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        public RubyClass/*!*/ GetTargetClass() {
            return _info.Context.GetClassOf(_target);
        }

        public virtual Proc/*!*/ ToProc(RubyScope/*!*/ scope) {
            ContractUtils.RequiresNotNull(scope, "scope");

            // TODO: 
            // This should pass a proc parameter (use BlockDispatcherUnsplatProcN).
            // MRI 1.9.2 doesn't do so though (see http://redmine.ruby-lang.org/issues/show/3792).

            if (_procDispatcher == null) {
                var site = CallSite<Func<CallSite, object, object, object>>.Create(
                    // TODO: use InvokeBinder
                    RubyCallAction.Make(
                        scope.RubyContext, "call",
                        new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasSplattedArgument)
                    )
                );

                var block = new BlockCallTargetUnsplatN((blockParam, self, args, unsplat) => {
                    // block takes no parameters but unsplat => all actual arguments are added to unsplat:
                    Debug.Assert(args.Length == 0);

                    return site.Target(site, this, unsplat);
                });

                // MRI's proc reports the location of the method it wraps, not of the to_proc call
                string sourcePath = null;
                int sourceLine = 0;
                var rubyInfo = _info as RubyMethodInfo;
                if (rubyInfo != null) {
                    sourcePath = rubyInfo.Document.FileName;
                    sourceLine = rubyInfo.SourceSpan.Start.Line;
                } else {
                    var lambdaInfo = _info as RubyLambdaMethodInfo;
                    if (lambdaInfo != null) {
                        sourcePath = lambdaInfo.Lambda.Dispatcher.SourcePath;
                        sourceLine = lambdaInfo.Lambda.Dispatcher.SourceLine;
                    }
                }

                _procDispatcher = new BlockDispatcherUnsplatN(0, 
                    BlockDispatcher.MakeAttributes(BlockSignatureAttributes.HasUnsplatParameter, _info.GetArity()),
                    sourcePath, sourceLine
                );

                _procDispatcher.SetMethod(block);
                _procDispatcher.ParameterSignature = _info.GetParameterSignature();
            }

            // A method binds its arguments strictly and `return` from it returns from the
            // method, both of which are lambda behaviour, so MRI's Method#to_proc answers
            // #lambda? with true.
            // TODO: 
            // MRI: source file/line are that of the to_proc method call:
            return new Proc(ProcKind.Lambda, scope.SelfObject, scope, _procDispatcher);
        }

        #region Dynamic Operations

        internal void BuildInvoke(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            Assert.NotNull(metaBuilder, args);
            Debug.Assert(args.Target == this);

            // first argument must be this method:
            metaBuilder.AddRestriction(Ast.Equal(args.TargetExpression, AstUtils.Constant(this)));

            // set the target (becomes self in the called method):
            args.SetTarget(AstUtils.Constant(_target, CompilerHelpers.GetVisibleType(_target)), _target);

            if (_methodMissingName != null) {
                args.InsertMethodName(_methodMissingName);
                _info.BuildCall(metaBuilder, args, Symbols.MethodMissing);
            } else {
                _info.BuildCall(metaBuilder, args, _name);
            }
        }

        #endregion

        #region Debug View

        private string/*!*/ GetDebugView() {
            var result = new StringBuilder();
            result.Append(_info.Visibility.ToString().ToLowerInvariant());
            result.Append(' ');

            result.Append(GetTargetClass().Name);
            result.Append('#');
            result.Append(_name);

            // TODO: parameter names?
            result.Append((_methodMissingName != null) ? "(?) via method_missing" : "()");
            return result.ToString();            
        }

        #endregion
    }
}
