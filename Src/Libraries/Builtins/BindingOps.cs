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
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    [RubyClass("Binding", Extends = typeof(Binding))]
    [UndefineMethod("new", IsStatic = true)]
    [UndefineMethod("LocalScope")]
    public static class BindingOps {

        [RubyMethod("source_location")]
        public static RubyArray GetSourceLocation(RubyContext/*!*/ context, Binding/*!*/ self) {
            if (self.SourcePath == null) {
                return null;
            }
            return new RubyArray(2) { context.EncodePath(self.SourcePath), self.SourceLine };
        }

        /// <summary>
        /// `it` and `_1`.. are parameters the block body conjured up by mentioning them. MRI keeps
        /// them out of #local_variables and answers for them here instead, and only for the scope
        /// the binding was taken in: one in an enclosing or a nested block does not count.
        /// </summary>
        [RubyMethod("implicit_parameters")]
        public static RubyArray/*!*/ GetImplicitParameters(RubyContext/*!*/ context, Binding/*!*/ self) {
            var names = self.FrameScope.OwnImplicitParameterNames;
            var result = new RubyArray(names != null ? names.Length : 0);
            if (names != null) {
                foreach (var name in names) {
                    result.Add(context.EncodeIdentifier(name));
                }
            }
            return result;
        }

        [RubyMethod("implicit_parameter_defined?")]
        public static bool IsImplicitParameterDefined(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name) {
            return IsImplicitParameter(self, CheckImplicitParameterName(toStr, name));
        }

        [RubyMethod("implicit_parameter_get")]
        public static object GetImplicitParameter(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name) {
            var context = toStr.Context;
            string parameterName = CheckImplicitParameterName(toStr, name);
            if (!IsImplicitParameter(self, parameterName)) {
                throw RubyExceptions.CreateNameError(
                    String.Format("implicit parameter '{0}' is not defined for {1}", parameterName, context.Inspect(self))
                );
            }
            return self.FrameScope.GetImplicitParameterValue(parameterName);
        }

        private static bool IsImplicitParameter(Binding/*!*/ self, string/*!*/ name) {
            var names = self.FrameScope.OwnImplicitParameterNames;
            return names != null && Array.IndexOf(names, name) >= 0;
        }

        /// <summary>
        /// Only `it` and `_1`..`_9` are implicit parameter names at all; anything else is a NameError
        /// before the scope is even consulted, and anything that is not a String or a Symbol is a
        /// TypeError.
        /// </summary>
        private static string/*!*/ CheckImplicitParameterName(ConversionStorage<MutableString>/*!*/ toStr, object name) {
            string text = ToIdentifier(toStr, name);
            if (text != "it" && !(text.Length == 2 && text[0] == '_' && text[1] >= '1' && text[1] <= '9')) {
                throw RubyExceptions.CreateNameError(
                    String.Format("'{0}' is not an implicit parameter", text)
                );
            }
            return text;
        }

        /// <summary>
        /// A local variable name may be given as a Symbol, a String or anything with #to_str.
        /// </summary>
        [RubyMethod("__ir_variable_name__", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ VariableName(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name) {
            return MutableString.Create(ToIdentifier(toStr, name), toStr.Context.GetIdentifierEncoding());
        }

        /// <summary>
        /// The locals the binding can see, its own (an eval's or a #local_variable_set's) first, newest
        /// first, then the frame's and the enclosing blocks' - what Kernel#local_variables evaluated
        /// through the binding says, without compiling anything.
        /// </summary>
        [RubyMethod("local_variables")]
        public static RubyArray/*!*/ GetLocalVariables(Binding/*!*/ self) {
            return self.LocalScope.GetLocalVariableSymbols();
        }

        // The slot access of Binding#local_variable_defined?, _get and _set (Src/StdLib/ironruby/ruby4.rb),
        // which have checked the name already. They read and write the scope rather than evaluating
        // source text: a name no source text can say - a keyword argument called `alias:` or `if:` is a
        // local named alias or if, which fluentd's ConfigureProxy reads this way - is a name like any other.

        [RubyMethod("__ir_local_defined__", RubyMethodAttributes.PrivateInstance)]
        public static bool IsLocalDefined(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name) {
            object value;
            return self.LocalScope.TryResolveBindingLocal(ToIdentifier(toStr, name), out value);
        }

        [RubyMethod("__ir_local_get__", RubyMethodAttributes.PrivateInstance)]
        public static object GetLocal(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name) {
            string identifier = ToIdentifier(toStr, name);
            object value;
            if (!self.LocalScope.TryResolveBindingLocal(identifier, out value)) {
                throw RubyExceptions.CreateNameError(
                    String.Format("local variable '{0}' is not defined for {1}", identifier, toStr.Context.Inspect(self))
                );
            }
            return value;
        }

        /// <summary>
        /// A variable the binding can see is assigned where it lives; any other becomes the binding's own,
        /// seen by later calls and evals through this binding but neither by the frame's compiled code nor
        /// by another binding of the frame (RubyScope.SetBindingLocal). A second thread writing through the
        /// same binding meets the scope's lock on its dynamic locals, as an eval through it does.
        /// </summary>
        [RubyMethod("__ir_local_set__", RubyMethodAttributes.PrivateInstance)]
        public static object SetLocal(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name, object value) {
            self.LocalScope.SetBindingLocal(ToIdentifier(toStr, name), value);
            return value;
        }

        internal static string/*!*/ ToIdentifier(ConversionStorage<MutableString>/*!*/ toStr, object name) {
            var symbol = name as RubySymbol;
            if (symbol != null) {
                return symbol.ToString();
            }
            var str = name as MutableString;
            if (str != null) {
                return str.ToString();
            }
            // anything with #to_str is a name too
            var converted = Protocols.TryCastToString(toStr, name);
            if (converted != null) {
                return converted.ToString();
            }
            throw RubyExceptions.CreateTypeError("{0} is not a symbol nor a string",
                toStr.Context.Inspect(name).ToString());
        }

        [RubyMethod("to_s"), RubyMethod("inspect")]
        public static MutableString/*!*/ ToS(RubyContext/*!*/ context, Binding/*!*/ self) {
            return RubyUtils.ObjectToMutableStringPrefix(context, self).Append('>');
        }
    }
}
