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

using System;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {

    [RubyClass("UnboundMethod")]
    public class UnboundMethod : IDuplicable {
        private readonly string/*!*/ _name;
        private readonly RubyMemberInfo/*!*/ _info;
        private readonly RubyModule/*!*/ _targetConstraint;

        internal RubyMemberInfo/*!*/ Info {
            get { return _info; }
        }

        internal string/*!*/ Name {
            get { return _name; }
        }

        internal RubyModule/*!*/ TargetConstraint {
            get { return _targetConstraint; }
        }

        internal UnboundMethod(RubyModule/*!*/ targetConstraint, string/*!*/ name, RubyMemberInfo/*!*/ info) {
            Assert.NotNull(targetConstraint, name, info);

            _name = name;
            _info = info;
            _targetConstraint = targetConstraint;
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = new UnboundMethod(_targetConstraint, _name, _info);
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        #region Public Instance Methods

        [RubyMethod("==")]
        [RubyMethod("eql?")]
        public static bool Equal(UnboundMethod/*!*/ self, [NotNull]UnboundMethod/*!*/ other) {
            return self.Info.IsEquivalentTo(other.Info);
        }

        // both names need both overloads, or the two are not the same method and the specs that
        // check `eql?' is an alias of `==' by comparing the two UnboundMethods fail
        [RubyMethod("==")]
        [RubyMethod("eql?")]
        public static bool Equal(UnboundMethod/*!*/ self, object other) {
            return false;
        }

        [RubyMethod("hash")]
        public static int GetHash(UnboundMethod/*!*/ self) {
            return self.Info.GetEquivalenceHashCode();
        }

        [RubyMethod("arity")]
        public static int GetArity(UnboundMethod/*!*/ self) {
            return self.Info.GetArity();
        }

        [RubyMethod("bind")]
        public static RubyMethod/*!*/ Bind(UnboundMethod/*!*/ self, object target) {
            RubyContext context = self._targetConstraint.Context;

            // Since Ruby 3.0 (Feature #15608) an unbound method whose owner is a module rather than a class
            // may be bound to any receiver:
            if (self._targetConstraint.IsClass && !context.IsKindOf(target, self._targetConstraint)) {
                throw RubyExceptions.CreateTypeError(
                    "bind argument must be an instance of {0}", self._targetConstraint.GetName(context)
                );
            }
            
            return new RubyMethod(target, self._info, self._name);
        }

        /// <summary>
        /// Binds and calls in one step. mspec's own pretty_inspect uses this, so its absence
        /// turned unrelated failures into "undefined method `bind_call'" noise.
        /// </summary>
        [RubyMethod("bind_call")]
        public static object BindCall(RubyScope/*!*/ scope, BlockParam block, UnboundMethod/*!*/ self, object target,
            params object[]/*!*/ args) {

            var bound = Bind(self, target);
            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, Proc, RubyArray, object>>(
                "call", new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, bound, block != null ? block.Proc : null, RubyOps.MakeArrayN(args));
        }

        [RubyMethod("name")]
        public static RubySymbol/*!*/ GetName(RubyContext/*!*/ context, UnboundMethod/*!*/ self) {
            // EncodeIdentifier rather than StringifyIdentifier: both return a Symbol, but the latter
            // hardcodes UTF-8 where this needs the context's identifier encoding.
            return context.EncodeIdentifier(self._name);
        }

        // The module the method is defined in. With Module#prepend this is the prepended module rather than
        // the class the method was looked up on.
        [RubyMethod("owner")]
        public static RubyModule/*!*/ GetOwner(UnboundMethod/*!*/ self) {
            return self._info.AliasOwner ?? self._info.DeclaringModule ?? self._targetConstraint;
        }

        /// <summary>
        /// The name the body was written with, which differs from #name once `alias' or
        /// define_method has given the same body a second name.
        /// </summary>
        [RubyMethod("original_name")]
        public static RubySymbol/*!*/ GetOriginalName(RubyContext/*!*/ context, UnboundMethod/*!*/ self) {
            return context.EncodeIdentifier(self._info.OriginalName ?? self._name);
        }

        [RubyMethod("to_s"), RubyMethod("inspect")]
        public static MutableString/*!*/ ToS(RubyContext/*!*/ context, UnboundMethod/*!*/ self) {
            return ToS(context, self.Name, self._info, null, "UnboundMethod");
        }

        /// <summary>
        /// MRI's description is
        ///
        ///   #&lt;Method: Origin(Owner)#name(original_name)(parameters) file:line&gt;
        ///
        /// of which this used to print only the first half.  The origin is the module the method
        /// was looked up on and is dropped when it is the owner itself; an UnboundMethod has no
        /// receiver to have looked it up on, so it prints the owner alone.  A method that lives in
        /// a singleton class is spelled "object.name" instead, and a singleton class that a method
        /// merely passed through on its way to an ancestor is replaced by the object's real class,
        /// unless the object is a class or a module - which is why String.method(:include) still
        /// says #&lt;Class:String&gt;.
        /// </summary>
        internal static MutableString/*!*/ ToS(RubyContext/*!*/ context, string/*!*/ methodName, RubyMemberInfo/*!*/ info,
            RubyModule targetModule, string/*!*/ classDisplayName) {

            RubyModule declaringModule = info.AliasOwner ?? info.DeclaringModule ?? targetModule;

            MutableString result = MutableString.CreateMutable(context.GetIdentifierEncoding());

            result.Append("#<");
            result.Append(classDisplayName);
            result.Append(": ");

            RubyClass declaringSingleton = declaringModule as RubyClass;
            if (targetModule != null && declaringSingleton != null && declaringSingleton.IsSingletonClass) {
                var attached = declaringSingleton.SingletonClassOf;
                var attachedModule = attached as RubyModule;
                result.Append(attachedModule != null
                    ? attachedModule.GetDisplayName(context, false)
                    : context.Inspect(attached));
                result.Append('.');
            } else {
                RubyModule origin = targetModule ?? declaringModule;
                var originSingleton = origin as RubyClass;
                if (originSingleton != null && originSingleton.IsSingletonClass && !(originSingleton.SingletonClassOf is RubyModule)) {
                    origin = context.GetClassOf(originSingleton.SingletonClassOf);
                }

                result.Append(origin.GetDisplayName(context, false));
                if (!ReferenceEquals(origin, declaringModule)) {
                    result.Append('(');
                    result.Append(declaringModule.GetDisplayName(context, false));
                    result.Append(')');
                }
                result.Append('#');
            }

            result.Append(methodName);

            if (info.OriginalName != null && info.OriginalName != methodName) {
                result.Append('(');
                result.Append(info.OriginalName);
                result.Append(')');
            }

            var signature = info.GetParameterSignature();
            if (signature != null) {
                result.Append(signature.ToParameterListString());
            }

            var location = GetSourceLocation(info);
            if (location != null) {
                result.Append(' ');
                result.Append(location[0] as MutableString);
                result.Append(':');
                result.Append(location[1].ToString());
            }

            result.Append('>');
            return result; 
        }

        [RubyMethod("of")]
        public static UnboundMethod/*!*/ BingGenericParameters(RubyContext/*!*/ context, UnboundMethod/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ typeArgs) {
            return new UnboundMethod(self.TargetConstraint, self.Name, MethodOps.BindGenericParameters(context, self.Info, self.Name, typeArgs));
        }

        [RubyMethod("overloads")]
        public static RubyMethod/*!*/ SelectOverload_old(RubyContext/*!*/ context, RubyMethod/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ parameterTypes) {
            throw RubyExceptions.CreateNameError("UnboundMethod#overloads is an obsolete name, use UnboundMethod#overload.");
        }
        
        [RubyMethod("overload")]
        public static UnboundMethod/*!*/ SelectOverload(RubyContext/*!*/ context, UnboundMethod/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ parameterTypes) {
            return new UnboundMethod(self.TargetConstraint, self.Name, MethodOps.SelectOverload(context, self.Info, self.Name, parameterTypes));
        }

        [RubyMethod("clr_members")]
        public static RubyArray/*!*/ GetClrMembers(UnboundMethod/*!*/ self) {
            return new RubyArray(self.Info.GetMembers());
        }

        [RubyMethod("source_location")]
        public static RubyArray GetSourceLocation(UnboundMethod/*!*/ self) {
            return GetSourceLocation(self.Info);
        }

        [RubyMethod("parameters")]
        public static RubyArray/*!*/ GetParameters(UnboundMethod/*!*/ self) {
            return self.Info.GetRubyParameterArray();
        }

        internal static RubyArray GetSourceLocation(RubyMemberInfo/*!*/ info) {
            RubyMethodInfo rubyInfo = info as RubyMethodInfo;
            return (rubyInfo == null) ? null : new RubyArray(2) {
                rubyInfo.DeclaringModule.Context.EncodePath(rubyInfo.Document.FileName),
                rubyInfo.SourceSpan.Start.Line
            };
        }

        #endregion
    }
}
