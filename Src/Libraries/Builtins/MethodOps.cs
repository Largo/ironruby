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
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {

    [RubyClass("Method", Extends = typeof(RubyMethod))]
    public static class MethodOps {
        [RubyMethod("==")]
        [RubyMethod("eql?")]
        public static bool Equal(RubyMethod/*!*/ self, [NotNull]RubyMethod/*!*/ other) {
            return ReferenceEquals(self.Target, other.Target) && self.Info.IsEquivalentTo(other.Info);
        }

        [RubyMethod("==")]
        public static bool Equal(RubyMethod/*!*/ self, object other) {
            return false;
        }

        [RubyMethod("arity")]
        public static int GetArity(RubyMethod/*!*/ self) {
            return self.Info.GetArity();            
        }

        [RubyMethod("name")]
        public static RubySymbol/*!*/ GetName(RubyContext/*!*/ context, RubyMethod/*!*/ self) {
            return context.EncodeIdentifier(self.Name);
        }

        /// <summary>
        /// The module the method is defined in, which for an alias is the module the alias was
        /// created in rather than the one holding the original definition. With Module#prepend
        /// this is the prepended module rather than the class the method was looked up on.
        /// </summary>
        [RubyMethod("owner")]
        public static RubyModule/*!*/ GetOwner(RubyMethod/*!*/ self) {
            return self.Info.DeclaringModule ?? self.GetTargetClass();
        }

        [RubyMethod("receiver")]
        public static object GetReceiver(RubyMethod/*!*/ self) {
            return self.Target;
        }

        [RubyMethod("clone")]
        public static RubyMethod/*!*/ Clone(RubyMethod/*!*/ self) {
            return new RubyMethod(self.Target, self.Info, self.Name);
        }

        [RubyMethod("[]")]
        [RubyMethod("call")]
        public static RuleGenerator/*!*/ Call() {
            return new RuleGenerator(RuleGenerators.MethodCall);
        }

        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(RubyContext/*!*/ context, RubyMethod/*!*/ self) {
            return UnboundMethod.ToS(context, self.Name, self.Info.DeclaringModule, self.GetTargetClass(), "Method");
        }

        [RubyMethod("to_proc")]
        public static Proc/*!*/ ToProc(RubyScope/*!*/ scope, RubyMethod/*!*/ self) {
            return self.ToProc(scope);
        }

        [RubyMethod("unbind")]
        public static UnboundMethod/*!*/ Unbind(RubyMethod/*!*/ self) {
            return new UnboundMethod(self.GetTargetClass(), self.Name, self.Info);
        }

        internal static RubyMemberInfo/*!*/ BindGenericParameters(RubyContext/*!*/ context, RubyMemberInfo/*!*/ info, string/*!*/ name, object[]/*!*/ typeArgs) {
            RubyMemberInfo result = info.TryBindGenericParameters(Protocols.ToTypes(context, typeArgs));
            if (result == null) {
                throw RubyExceptions.CreateArgumentError("wrong number of generic arguments for `{0}'", name);
            }
            return result;
        }

        internal static RubyMemberInfo/*!*/ SelectOverload(RubyContext/*!*/ context, RubyMemberInfo/*!*/ info, string/*!*/ name, object[]/*!*/ typeArgs) {
            RubyMemberInfo result = info.TrySelectOverload(Protocols.ToTypes(context, typeArgs));
            if (result == null) {
                throw RubyExceptions.CreateArgumentError("no overload of `{0}' matches given parameter types", name);
            }
            return result;
        }

        [RubyMethod("of")]
        public static RubyMethod/*!*/ BindGenericParameters(RubyContext/*!*/ context, RubyMethod/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ typeArgs) {
            return new RubyMethod(self.Target, BindGenericParameters(context, self.Info, self.Name, typeArgs), self.Name);
        }

        [RubyMethod("overloads")]
        public static RubyMethod/*!*/ SelectOverload_old(RubyContext/*!*/ context, RubyMethod/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ parameterTypes) {
            throw RubyExceptions.CreateNameError("Method#overloads is an obsolete name, use Method#overload.");
        }

        [RubyMethod("overload")]
        public static RubyMethod/*!*/ SelectOverload(RubyContext/*!*/ context, RubyMethod/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ parameterTypes) {
            return new RubyMethod(self.Target, SelectOverload(context, self.Info, self.Name, parameterTypes), self.Name);
        }

        [RubyMethod("clr_members")]
        public static RubyArray/*!*/ GetClrMembers(RubyMethod/*!*/ self) {
            return new RubyArray(self.Info.GetMembers());
        }

        [RubyMethod("source_location")]
        public static RubyArray GetSourceLocation(RubyMethod/*!*/ self) {
            return UnboundMethod.GetSourceLocation(self.Info);
        }

        [RubyMethod("parameters")]
        public static RubyArray/*!*/ GetParameters(RubyMethod/*!*/ self) {
            return self.Info.GetRubyParameterArray();
        }
    }
}
