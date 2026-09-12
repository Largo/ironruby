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
#if FEATURE_CRYPTOGRAPHY
using System.Security.Cryptography;
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Compiler;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    [RubyModule("Kernel", Extends = typeof(Kernel))]
    public static class KernelOps {
        #region initialize_copy

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static object InitializeCopy(RubyContext/*!*/ context, object self, object source) {
            RubyClass selfClass = context.GetClassOf(self);
            RubyClass sourceClass = context.GetClassOf(source);
            if (sourceClass != selfClass) {
                throw RubyExceptions.CreateTypeError("initialize_copy should take same class object");
            }

            if (context.IsObjectFrozen(self)) {
                throw RubyExceptions.CreateTypeError("can't modify frozen {0}", selfClass.Name);
            }

            return self;
        }

        #endregion

        #region Array, Float, Integer, String, Complex, Rational

        [RubyMethod("Array", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("Array", RubyMethodAttributes.PublicSingleton)]
        public static IList/*!*/ ToArray(ConversionStorage<IList>/*!*/ tryToAry, ConversionStorage<IList>/*!*/ tryToA, object self, object obj) {
            IList result = Protocols.TryCastToArray(tryToAry, obj);
            if (result != null) {
                return result;
            }

            // MRI 1.9 calls to_a (MRI 1.8 doesn't):
            //if (context.RubyOptions.Compatibility > RubyCompatibility.Ruby18) {
            result = Protocols.TryConvertToArray(tryToA, obj);
            if (result != null) {
                return result;
            }
            //}

            result = new RubyArray();
            if (obj != null) {
                result.Add(obj);
            }
            return result;
        }

        [RubyMethod("Float", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("Float", RubyMethodAttributes.PublicSingleton)]
        public static double ToFloat(object self, [DefaultProtocol]double value) {
            return value;
        }

        [RubyMethod("Integer", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("Integer", RubyMethodAttributes.PublicSingleton)]
        public static object/*!*/ ToInteger(object self, [NotNull]MutableString/*!*/ value) {
            var str = value.ConvertToString();
            int i = 0;
            object result = Tokenizer.ParseInteger(str, 0, ref i).ToObject();

            while (i < str.Length && Tokenizer.IsWhiteSpace(str[i])) {
                i++;
            }

            if (i < str.Length) {
                // MRI: Integer("zz") => ArgumentError: invalid value for Integer(): "zz"
                throw RubyExceptions.CreateArgumentError("invalid value for Integer(): \"{0}\"", str);
            }

            return result;
        }

        [RubyMethod("Integer", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("Integer", RubyMethodAttributes.PublicSingleton)]
        public static object/*!*/ ToInteger(ConversionStorage<IntegerValue>/*!*/ integerConversion, object self, object value) {
            var integer = Protocols.ConvertToInteger(integerConversion, value);
            return integer.IsFixnum ? ScriptingRuntimeHelpers.Int32ToObject(integer.Fixnum) : integer.Bignum;
        }

        [RubyMethod("String", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("String", RubyMethodAttributes.PublicSingleton)]
        public static object/*!*/ ToString(ConversionStorage<MutableString>/*!*/ tosConversion, object self, object obj) {
            return Protocols.ConvertToString(tosConversion, obj);
        }

        [RubyMethod("Complex", RubyMethodAttributes.PrivateInstance, Compatibility = RubyCompatibility.Ruby19)]
        [RubyMethod("Complex", RubyMethodAttributes.PublicSingleton, Compatibility = RubyCompatibility.Ruby19)]
        public static object ToComplex(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toComplex, 
            RubyScope/*!*/ scope, object self, object real, [DefaultParameterValue(null)]object imaginary) {
            
            // TODO: hack: redefines this method
            scope.RubyContext.Loader.LoadFile(scope.GlobalScope.Scope, self, MutableString.CreateAscii("complex18.rb"), LoadFlags.Require);
            var site = toComplex.GetCallSite("Complex", 2);
            return site.Target(site, self, real, imaginary);
        }

        [RubyMethod("Rational", RubyMethodAttributes.PrivateInstance, Compatibility = RubyCompatibility.Ruby19)]
        [RubyMethod("Rational", RubyMethodAttributes.PublicSingleton, Compatibility = RubyCompatibility.Ruby19)]
        public static object/*!*/ ToRational(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ toRational, 
            RubyScope/*!*/ scope, object self, object numerator, [DefaultParameterValue(null)]object denominator) {

            // TODO: hack: redefines this method
            scope.RubyContext.Loader.LoadFile(scope.GlobalScope.Scope, self, MutableString.CreateAscii("rational18.rb"), LoadFlags.Require);
            var site = toRational.GetCallSite("Rational", 2);
            return site.Target(site, self, numerator, denominator);
        }

        #endregion

        #region binding, block_given?, local_variables, caller, callcc, 1.9: __callee__, __method__

        [RubyMethod("binding", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("binding", RubyMethodAttributes.PublicSingleton)]
        public static Binding/*!*/ GetLocalScope(RubyScope/*!*/ scope, object self) {
            if (scope.RubyContext.RubyOptions.Compatibility < RubyCompatibility.Ruby19) {
                return new Binding(scope, self);
            } else {
                return new Binding(scope);
            }
        }

        [RubyMethod("block_given?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("block_given?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("iterator?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("iterator?", RubyMethodAttributes.PublicSingleton)]
        public static bool HasBlock(RubyScope/*!*/ scope, object self) {
            var methodScope = scope.GetInnerMostMethodScope();
            return methodScope != null && methodScope.BlockParameter != null;
        }

        [RubyMethod("local_variables", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("local_variables", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetLocalVariableNames(RubyScope/*!*/ scope, object self) {
            var names = scope.GetVisibleLocalNames();
            return new RubyArray(names.Count).AddRange(scope.RubyContext.StringifyIdentifiers(names));
        }

        [RubyMethod("caller", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("caller", RubyMethodAttributes.PublicSingleton)]
        [RubyStackTraceHidden]
        public static RubyArray/*!*/ GetStackTrace(RubyContext/*!*/ context, object self, [DefaultParameterValue(1)]int skipFrames) {
            if (skipFrames < 0) {
                return new RubyArray();
            }

            return RubyExceptionData.CreateBacktrace(context, skipFrames);
        }

        //callcc
        // 1.9 private instance/singleton __callee__
        // 1.9 private instance/singleton __method__

        #endregion

        #region throw, catch, loop, proc, lambda

        private sealed class ThrowCatchUnwinder : StackUnwinder {
            public readonly object Label;

            internal ThrowCatchUnwinder(object label, object returnValue)
                : base(returnValue) {
                Label = label;
            }
        }

        [ThreadStatic]
        private static Stack<object> _catchSymbols;

        [RubyMethod("catch", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("catch", RubyMethodAttributes.PublicSingleton)]
        public static object Catch(BlockParam/*!*/ block, object self, object label) {
            if (block == null) {
                throw RubyExceptions.NoBlockGiven();
            }

            try {
                if (_catchSymbols == null) {
                    _catchSymbols = new Stack<object>();
                }
                _catchSymbols.Push(label);

                try {
                    object result;
                    block.Yield(label, out result);
                    return result;
                } catch (ThrowCatchUnwinder unwinder) {
                    if (ReferenceEquals(unwinder.Label, label)) {
                        return unwinder.ReturnValue;
                    }

                    throw;
                }
            } finally {
                _catchSymbols.Pop();
            }
        }

        [RubyMethod("throw", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("throw", RubyMethodAttributes.PublicSingleton)]
        public static void Throw(RubyContext/*!*/ context, object self, object label, [DefaultParameterValue(null)]object returnValue) {
            if (_catchSymbols == null || !_catchSymbols.Contains(label, ReferenceEqualityComparer<object>.Instance)) {
                throw RubyExceptions.CreateNameError("uncaught throw `{0}'", context.Inspect(label).ToAsciiString());
            }

            throw new ThrowCatchUnwinder(label, returnValue);
        }

        [RubyMethod("loop", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("loop", RubyMethodAttributes.PublicSingleton)]
        public static object Loop(BlockParam/*!*/ block, object self) {
            if (block == null) {
                throw RubyExceptions.NoBlockGiven();
            }

            while (true) {
                object result;
                if (block.Yield(out result)) {
                    return result;
                }
            }
        }

        [RubyMethod("lambda", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("lambda", RubyMethodAttributes.PublicSingleton)]
        public static Proc/*!*/ CreateLambda(BlockParam/*!*/ block, object self) {
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }

            return block.Proc.ToLambda(null);
        }

        [RubyMethod("proc", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("proc", RubyMethodAttributes.PublicSingleton)]
        public static Proc/*!*/ CreateProc(BlockParam/*!*/ block, object self) {
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }

            return block.Proc;
        }

        #endregion

        #region raise, fail

        [RubyMethod("raise", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("raise", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("fail", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("fail", RubyMethodAttributes.PublicSingleton)]
        [RubyStackTraceHidden]
        public static void RaiseException(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, object self, params object[]/*!*/ args) {

            Exception exception = CreateExceptionToRaise(respondToStorage, storage0, storage1, setBackTraceStorage, context, args);
#if DEBUG && FEATURE_THREAD && FEATURE_EXCEPTION_STATE
            if (RubyOptions.UseThreadAbortForSyncRaise) {
                RubyUtils.RaiseAsyncException(Thread.CurrentThread, exception);
            }
#endif
            // rethrow semantics, preserves the backtrace associated with the exception:
            throw exception;
        }

        /// <summary>
        /// Builds the exception `raise` would throw, without throwing it. Fiber#raise is written
        /// in Ruby (Src/StdLib/ironruby/ruby4.rb) and has to hand the exception to another fiber,
        /// so it needs the argument handling - including `cause:`, which MRI resolves in the
        /// *calling* context - without the throw. Private, and not part of MRI's Kernel.
        /// </summary>
        [RubyMethod("__build_exception__", RubyMethodAttributes.PrivateInstance)]
        public static Exception/*!*/ BuildException(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, object self, params object[]/*!*/ args) {

            return CreateExceptionToRaise(respondToStorage, storage0, storage1, setBackTraceStorage, context, args);
        }

        /// <summary>
        /// The whole of `raise [exception [, message [, backtrace]]] [, cause: c]`, shared by
        /// Kernel#raise, Thread#raise and (via __build_exception__) Fiber#raise so that all
        /// three agree.
        /// </summary>
        internal static Exception/*!*/ CreateExceptionToRaise(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, object[]/*!*/ args) {

            return CreateExceptionToRaise(respondToStorage, storage0, storage1, setBackTraceStorage, context, args, true);
        }

        internal static Exception/*!*/ CreateExceptionToRaise(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            CallSiteStorage<Action<CallSite, Exception, object>>/*!*/ setBackTraceStorage,
            RubyContext/*!*/ context, object[]/*!*/ args, bool bareRaiseReRaisesCurrentException) {

            object cause;
            bool hasCause = TryTakeCauseKeyword(ref args, out cause);

            if (args.Length > 3) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..3)", args.Length);
            }

            if (args.Length == 0 && hasCause) {
                throw RubyExceptions.CreateArgumentError("only cause is given with no arguments");
            }

            Exception exception;
            if (args.Length == 0) {
                // bare `raise` re-raises $!, or a fresh RuntimeError with an empty message.
                exception = bareRaiseReRaisesCurrentException ? context.CurrentException : null;
                if (exception == null) {
                    exception = RubyExceptionData.InitializeException(new RuntimeError(""), MutableString.CreateEmpty());
                }
            } else {
                exception = MakeException(respondToStorage, storage0, storage1, args);

                if (args.Length >= 3 && args[2] != null) {
                    var site = setBackTraceStorage.GetCallSite("set_backtrace", 1);
                    site.Target(site, exception, args[2]);
                }
            }

            SetCause(context, exception, hasCause, cause);
            return exception;
        }

        private static Exception/*!*/ MakeException(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ storage0, BinaryOpStorage/*!*/ storage1,
            object[]/*!*/ args) {

            object obj = args[0];

            // `raise "boom"` is RuntimeError. A String is only accepted on its own: MRI answers
            // "exception class/object expected" for `raise "boom", "more"`, because there the
            // first argument has to be something that responds to #exception.
            var message = obj as MutableString;
            if (message != null && args.Length == 1) {
                return RubyExceptionData.InitializeException(new RuntimeError(message.ToString()), message);
            }

            if (!Protocols.RespondTo(respondToStorage, obj, "exception")) {
                throw RubyExceptions.CreateTypeError("exception class/object expected");
            }

            object result;
            if (args.Length >= 2) {
                var site = storage1.GetCallSite("exception");
                result = site.Target(site, obj, args[1]);
            } else {
                var site = storage0.GetCallSite("exception");
                result = site.Target(site, obj);
            }

            var exception = result as Exception;
            if (exception == null) {
                // MRI distinguishes "you passed something that isn't raisable at all" from
                // "#exception answered something that isn't an exception".
                throw RubyExceptions.CreateTypeError("exception object expected");
            }
            return exception;
        }

        /// <summary>
        /// Splits a trailing `cause:` keyword off the argument list.
        ///
        /// IronRuby has no real keyword arguments - they arrive as a trailing Hash - so this can
        /// only go by shape: a trailing Hash whose single key is :cause. That is deliberate,
        /// because MRI passes any *other* trailing hash on to the exception constructor
        /// (`raise MyError, data: 42`). The one case it gets wrong is an explicitly braced
        /// `raise "msg", {cause: e}`, which MRI treats as positional.
        /// </summary>
        private static bool TryTakeCauseKeyword(ref object[]/*!*/ args, out object cause) {
            cause = null;
            if (args.Length == 0) {
                return false;
            }

            var hash = args[args.Length - 1] as IDictionary<object, object>;
            if (hash == null || hash.Count != 1) {
                return false;
            }

            foreach (var entry in hash) {
                var key = entry.Key as RubySymbol;
                if (key == null || key.ToString() != "cause") {
                    return false;
                }
                cause = entry.Value;
            }

            var rest = new object[args.Length - 1];
            Array.Copy(args, rest, rest.Length);
            args = rest;
            return true;
        }

        private static void SetCause(RubyContext/*!*/ context, Exception/*!*/ exception, bool hasCause, object cause) {
            Exception causeException;
            if (hasCause) {
                if (cause == null) {
                    causeException = null;
                } else {
                    causeException = cause as Exception;
                    if (causeException == null) {
                        throw RubyExceptions.CreateTypeError("exception object expected");
                    }
                }
            } else {
                // no explicit cause: chain to whatever is currently being handled ($!)
                causeException = context.CurrentException;
            }

            // `raise e, cause: e` is not circular, it just leaves the cause unset.
            if (causeException != exception) {
                for (Exception c = causeException; c != null; c = RubyExceptionData.GetInstance(c).Cause) {
                    if (c == exception) {
                        throw RubyExceptions.CreateArgumentError("circular causes");
                    }
                }
            }

            RubyExceptionData.GetInstance(exception).TrySetCause(causeException);
        }

        #endregion


        #region =~, !~, ===, <=>, eql?, hash, to_s, inspect, to_a

        [RubyMethod("=~")]
        public static object Match(object self, object other) {
            // Default implementation of match that is overridden in descendents (notably String and Regexp)
            return null;
        }

        [RubyMethod("!~")]
        public static bool NotMatch(BinaryOpStorage/*!*/ match, object self, object other) {
            var site = match.GetCallSite("=~", 1);
            return RubyOps.IsFalse(site.Target(site, self, other));
        }

        // calls == by default
        [RubyMethod("===")]
        public static bool CaseEquals(BinaryOpStorage/*!*/ equals, object self, object other) {
            return Protocols.IsEqual(equals, self, other);
        }

        // calls == by default
        [RubyMethod("<=>")]
        public static object Compare(BinaryOpStorage/*!*/ equals, object self, object other) {
            return Protocols.IsEqual(equals, self, other) ? ScriptingRuntimeHelpers.Int32ToObject(0) : null;
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("eql?")]
        public static bool ValueEquals([NotNull]IRubyObject/*!*/ self, object other) {
            return self.BaseEquals(other);
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("eql?")]
        public static bool ValueEquals(object self, object other) {
            return Object.Equals(self, other);
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("hash")]
        public static int Hash([NotNull]IRubyObject/*!*/ self) {
            return self.BaseGetHashCode();
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("hash")]
        public static int Hash(object self) {
            return self == null ? RubyUtils.NilObjectId : self.GetHashCode();
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS([NotNull]IRubyObject/*!*/ self) {
            return RubyUtils.ObjectBaseToMutableString(self);
        }

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(object self) {
            return self == null ? MutableString.CreateEmpty() : MutableString.Create(self.ToString(), RubyEncoding.UTF8);
        }

        /// <summary>
        /// Returns a string containing a human-readable representation of obj.
        /// If not overridden, uses the to_s method to generate the string. 
        /// </summary>
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(UnaryOpStorage/*!*/ inspectStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            object self) {

            RubyClass cls;
            var context = tosConversion.Context;
            if (context.HasInstanceVariables(self) && ((cls = context.GetClassOf(self)).IsRubyClass || cls.IsObjectClass)) {
                return RubyUtils.InspectObject(inspectStorage, tosConversion, self);
            } else {
                var site = tosConversion.GetSite(ConvertToSAction.Make(context));
                return site.Target(site, self);
            }
        }

        #endregion

        #region nil?, __id__, id, object_id

        // thread-safe:
        [RubyMethod("nil?")]
        public static bool IsNil(object self) {
            return self == null;
        }
        
        [RubyMethod("id")]
        public static object GetId(RubyContext/*!*/ context, object self) {
            context.ReportWarning("Object#id will be deprecated; use Object#object_id");
            return GetObjectId(context, self);
        }

        [RubyMethod("__id__")]
        [RubyMethod("object_id")]
        public static object GetObjectId(RubyContext/*!*/ context, object self) {
            return ClrInteger.Narrow(RubyUtils.GetObjectId(context, self));
        }

        #endregion

        #region clone, dup

        [RubyMethod("clone")]
        public static object/*!*/ Clone(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            object self) {

            return Clone(initializeCopyStorage, allocateStorage, true, self);
        }

        [RubyMethod("dup")]
        public static object/*!*/ Duplicate(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            object self) {

            return Clone(initializeCopyStorage, allocateStorage, false, self);
        }

        private static object/*!*/ Clone(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            bool isClone, object self) {

            var context = allocateStorage.Context;

            object result;
            if (!RubyUtils.TryDuplicateObject(initializeCopyStorage, allocateStorage, self, isClone, out result)) {
                throw RubyExceptions.CreateTypeError("can't {0} {1}", isClone ? "clone" : "dup", context.GetClassDisplayName(self));
            }
            return context.TaintObjectBy(result, self);
        }

        #endregion

        #region class, type, extend, instance_of?, is_a?, kind_of? (thread-safe)

        [RubyMethod("class")]
        public static RubyClass/*!*/ GetClass(RubyContext/*!*/ context, object self) {
            return context.GetClassOf(self);
        }

        [RubyMethod("type")]
        public static RubyClass/*!*/ GetClassObsolete(RubyContext/*!*/ context, object self) {
            context.ReportWarning("Object#type will be deprecated; use Object#class");
            return context.GetClassOf(self);
        }

        // thread-safe:
        [RubyMethod("is_a?")]
        [RubyMethod("kind_of?")]
        public static bool IsKindOf(object self, RubyModule/*!*/ other) {
            ContractUtils.RequiresNotNull(other, "other");
            return other.Context.IsKindOf(self, other);
        }

        // thread-safe:
        [RubyMethod("instance_of?")]
        public static bool IsOfClass(object self, RubyModule/*!*/ other) {
            ContractUtils.RequiresNotNull(other, "other");
            return other.Context.GetClassOf(self) == other;
        }

        [RubyMethod("extend")]
        public static object Extend(
            CallSiteStorage<Func<CallSite, RubyModule, object, object>>/*!*/ extendObjectStorage,
            CallSiteStorage<Func<CallSite, RubyModule, object, object>>/*!*/ extendedStorage,
            object self, [NotNull]RubyModule/*!*/ module, [NotNullItems]params RubyModule/*!*/[]/*!*/ modules) {

            Assert.NotNull(modules);

            // TODO: this is strange:
            RubyUtils.RequireMixins(module.GetOrCreateSingletonClass(), modules);

            var extendObject = extendObjectStorage.GetCallSite("extend_object", 1);
            var extended = extendedStorage.GetCallSite("extended", 1);

            // Kernel#extend_object inserts the module at the beginning of the object's singleton ancestors list;
            // ancestors after extend: [modules[0], modules[1], ..., modules[N-1], self-singleton, ...]
            for (int i = modules.Length - 1; i >= 0; i--) {
                extendObject.Target(extendObject, modules[i], self);
                extended.Target(extended, modules[i], self);
            }

            extendObject.Target(extendObject, module, self);
            extended.Target(extended, module, self);

            return self;
        }

        #endregion

        #region frozen?, freeze, tainted?, taint, untaint, trust, untrust, untrusted?

        [RubyMethod("frozen?")]
        public static bool Frozen([NotNull]MutableString/*!*/ self) {
            return self.IsFrozen;
        }

        [RubyMethod("frozen?")]
        public static bool Frozen(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self)) {
                return false; // can't freeze value types
            }
            return context.IsObjectFrozen(self);
        }

        [RubyMethod("freeze")]
        public static object Freeze(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self)) {
                return self; // can't freeze value types
            }
            context.FreezeObject(self);
            return self;
        }

        [RubyMethod("tainted?")]
        public static bool Tainted(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self)) {
                return false; // can't taint value types
            }
            return context.IsObjectTainted(self);
        }

        [RubyMethod("taint")]
        public static object Taint(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self)) {
                return self;
            }
            context.SetObjectTaint(self, true);
            return self;
        }

        [RubyMethod("untaint")]
        public static object Untaint(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self)) {
                return self;
            }
            context.SetObjectTaint(self, false);
            return self;
        }

        [RubyMethod("untrusted?")]
        public static bool Untrusted(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self)) {
                return false; // can't untrust value types
            }
            return context.IsObjectUntrusted(self);
        }

        [RubyMethod("trust")]
        public static object Trust(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self)) {
                return self;
            }
            context.SetObjectTrustiness(self, false);
            return self;
        }

        [RubyMethod("untrust")]
        public static object Untrust(RubyContext/*!*/ context, object self) {
            if (!RubyUtils.HasObjectState(self)) {
                return self;
            }
            context.SetObjectTrustiness(self, true);
            return self;
        }

        #endregion

        #region eval

        [RubyMethod("eval", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("eval", RubyMethodAttributes.PublicSingleton)]
        public static object Evaluate(RubyScope/*!*/ scope, object self, [NotNull]MutableString/*!*/ code,
            [Optional]Binding binding, [Optional, NotNull]MutableString file, [DefaultParameterValue(1)]int line) {

            RubyScope targetScope;
            object targetSelf;
            if (binding != null) {
                targetScope = binding.LocalScope;
                targetSelf = binding.SelfObject;
            } else {
                targetScope = scope;
                targetSelf = self;
            }
            return RubyUtils.Evaluate(code, targetScope, targetSelf, null, file, line);
        }

        [RubyMethod("eval", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("eval", RubyMethodAttributes.PublicSingleton)]
        public static object Evaluate(RubyScope/*!*/ scope, object self, [NotNull]MutableString/*!*/ code,
            [NotNull]Proc/*!*/ procBinding, [Optional, NotNull]MutableString file, [DefaultParameterValue(1)]int line) {

            return RubyUtils.Evaluate(code, procBinding.LocalScope, procBinding.LocalScope.SelfObject, null, file, line);
        }

        #endregion

        #region instance_variables, instance_variable_defined?, instance_variable_get, instance_variable_set, remove_instance_variable

        [RubyMethod("instance_variables")]
        public static RubyArray/*!*/ GetInstanceVariableNames(RubyContext/*!*/ context, object self) {
            return context.StringifyIdentifiers(context.GetInstanceVariableNames(self));
        }

        [RubyMethod("instance_variable_get")]
        public static object InstanceVariableGet(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]string/*!*/ name) {
            object value;
            if (!context.TryGetInstanceVariable(self, name, out value)) {
                // We didn't find it, check if the name is valid
                RubyUtils.CheckInstanceVariableName(name);
                return null;
            }
            return value;
        }

        [RubyMethod("instance_variable_set")]
        public static object InstanceVariableSet(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]string/*!*/ name, object value) {
            RubyUtils.CheckInstanceVariableName(name);
            context.SetInstanceVariable(self, name, value);
            return value;
        }

        [RubyMethod("instance_variable_defined?")]
        public static bool InstanceVariableDefined(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]string/*!*/ name) {
            object value;
            if (!context.TryGetInstanceVariable(self, name, out value)) {
                // We didn't find it, check if the name is valid
                RubyUtils.CheckInstanceVariableName(name);
                return false;
            }

            return true;
        }

        [RubyMethod("remove_instance_variable", RubyMethodAttributes.PrivateInstance)]
        public static object RemoveInstanceVariable(RubyContext/*!*/ context, object/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ name) {
            object value;
            if (!context.TryRemoveInstanceVariable(self, name, out value)) {
                // We didn't find it, check if the name is valid
                RubyUtils.CheckInstanceVariableName(name);

                throw RubyExceptions.CreateNameError("instance variable `{0}' not defined", name);
            }

            return value;
        }

        #endregion

        #region global_variables

        [RubyMethod("global_variables", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("global_variables", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetGlobalVariableNames(RubyContext/*!*/ context, object self) {
            RubyArray result = new RubyArray();
            lock (context.GlobalVariablesLock) {
                foreach (KeyValuePair<string, GlobalVariable> global in context.GlobalVariables) {
                    if (global.Value.IsEnumerated) {
                        result.Add(context.StringifyIdentifier(global.Key));
                    }
                }
            }
            return result;
        }

        #endregion

        #region autoload, autoloaded?

        [RubyMethod("autoload", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("autoload", RubyMethodAttributes.PublicSingleton)]
        public static void SetAutoloadedConstant(RubyScope/*!*/ scope, object self,
            [DefaultProtocol, NotNull]string/*!*/ constantName, [DefaultProtocol, NotNull]MutableString/*!*/ path) {
            ModuleOps.SetAutoloadedConstant(scope.GetInnerMostModuleForConstantLookup(), constantName, path);
        }

        [RubyMethod("autoload?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("autoload?", RubyMethodAttributes.PublicSingleton)]
        public static MutableString GetAutoloadedConstantPath(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ constantName) {
            return ModuleOps.GetAutoloadedConstantPath(scope.GetInnerMostModuleForConstantLookup(), constantName);
        }

        #endregion

        #region respond_to?

        // This method is a binder intrinsic and the behavior of the binder needs to be adjusted appropriately if changed.
        [RubyMethod("respond_to?")]
        public static bool RespondTo(RubyContext/*!*/ context, object self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [Optional]bool includePrivate) {

            return context.ResolveMethod(self, methodName, includePrivate).Found;
        }

        #endregion

        #region send, public_send

        [RubyMethod("send")]
        [RubyMethod("public_send")]
        public static object SendMessage(RubyScope/*!*/ scope, object self) {
            throw RubyExceptions.CreateArgumentError("no method name given");
        }

        // public_send differs from send only in visibility: the call site does not
        // pretend self is implicit, so private and protected methods are not found.
        [RubyMethod("public_send")]
        public static object PublicSendMessage(RubyScope/*!*/ scope, BlockParam block, object self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, params object[]/*!*/ args) {

            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, Proc, RubyArray, object>>(
                methodName, new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, RubyOps.MakeArrayN(args));
        }

        // ARGS: 0
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName) {
            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, object>>(
                methodName, new RubyCallSignature(0, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );
            return site.Target(site, scope, self);
        }

        // ARGS: 0&
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName) {
            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, Proc, object>>(
                methodName, new RubyCallSignature(0, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null);
        }

        // ARGS: 1
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1) {

            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, object, object>>(
                methodName, new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );

            return site.Target(site, scope, self, arg1);
        }

        // ARGS: 1&
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1) {

            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, Proc, object, object>>(
                methodName, new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, arg1);
        }

        // ARGS: 2
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2) {

            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, object, object, object>>(
                methodName, new RubyCallSignature(2, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );

            return site.Target(site, scope, self, arg1, arg2);
        }

        // ARGS: 2&
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2) {

            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, Proc, object, object, object>>(
                methodName, new RubyCallSignature(2, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, arg1, arg2);
        }

        // ARGS: 3
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2, object arg3) {

            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, object, object, object, object>>(
                methodName, new RubyCallSignature(3, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf)
            );

            return site.Target(site, scope, self, arg1, arg2, arg3);
        }

        // ARGS: 3&
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            object arg1, object arg2, object arg3) {

            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, Proc, object, object, object, object>>(
                methodName, new RubyCallSignature(3, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, arg1, arg2, arg3);
        }

        // ARGS: N
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            params object[]/*!*/ args) {
            
            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, RubyArray, object>>(
                methodName, new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasSplattedArgument)
            );

            return site.Target(site, scope, self, RubyOps.MakeArrayN(args));
        }

        // ARGS: N&
        [RubyMethod("send")]
        public static object SendMessage(RubyScope/*!*/ scope, BlockParam block, object self, [DefaultProtocol, NotNull]string/*!*/ methodName,
            params object[]/*!*/ args) {

            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, Proc, RubyArray, object>>(
                methodName, new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasSplattedArgument |
                    RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, self, block != null ? block.Proc : null, RubyOps.MakeArrayN(args));
        }

        internal static object SendMessageOpt(RubyScope/*!*/ scope, BlockParam block, object self, string/*!*/ methodName, object[] args) {
            switch ((args != null ? args.Length : 0)) {
                case 0: return SendMessage(scope, block, self, methodName);
                case 1: return SendMessage(scope, block, self, methodName, args[0]);
                case 2: return SendMessage(scope, block, self, methodName, args[0], args[1]);
                case 3: return SendMessage(scope, block, self, methodName, args[0], args[1], args[2]);
                default: return SendMessage(scope, block, self, methodName, args);
            }
        }

        // 1.9: public_send

        [RubyMethod("tap")]
        public static object Tap(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block, object/*!*/ self) {

            object blockResult;
            if (block.Yield(self, out blockResult)) {
                return blockResult;
            }
            return self;
        }

        #endregion

        #region clr_member, method, public_method

        // thread-safe:
        /// <summary>
        /// Returns a RubyMethod instance that represents one or more CLR members of given name.
        /// An exception is thrown if the member is not found.
        /// Name could be of Ruby form (foo_bar) or CLR form (FooBar). Operator names are translated 
        /// (e.g. "+" to op_Addition, "[]"/"[]=" to a default index getter/setter).
        /// The resulting RubyMethod might represent multiple CLR members (overloads).
        /// Inherited members are included.
        /// Includes all CLR members that match the name even if they are not callable from Ruby - 
        /// they are hidden by a Ruby member or their declaring type is not included in the ancestors list of the class.
        /// Includes members of any Ruby visibility.
        /// Includes CLR protected members.
        /// Includes CLR private members if PrivateBinding is on.
        /// </summary>
        [RubyMethod("clr_member")]
        public static RubyMethod/*!*/ GetClrMember(RubyContext/*!*/ context, object self, [DefaultParameterValue(null), NotNull]object asType, 
            [DefaultProtocol, NotNull]string/*!*/ name) {
            RubyMemberInfo info;

            RubyClass cls = context.GetClassOf(self);
            Type type = (asType != null) ? Protocols.ToType(context, asType) : null;
            if (!cls.TryGetClrMember(name, type, out info)) {
                throw RubyExceptions.CreateNameError("undefined CLR method `{0}' for class `{1}'", name, cls.Name);
            }

            return new RubyMethod(self, info, name);
        }

        // thread-safe:
        [RubyMethod("method")]
        public static RubyMethod/*!*/ GetMethod(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]string/*!*/ name) {
            RubyMemberInfo info = context.ResolveMethod(self, name, VisibilityContext.AllVisible).Info;
            if (info == null) {
                throw RubyExceptions.CreateUndefinedMethodError(context.GetClassOf(self), name);
            }
            return new RubyMethod(self, info, name);
        }

        // 1.9: public: public_method

        #endregion

        #region define_singleton_method (thread-safe)

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubyMethod/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]RubyMethod/*!*/ method) {

            // TODO:
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, method);
        }

        // thread-safe:
        // Defines method using mangled CLR name and aliases that method with the actual CLR name.
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static RubyMethod/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [NotNull]ClrName/*!*/ methodName, [NotNull]RubyMethod/*!*/ method) {

            // TODO:
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, method);
        }

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static UnboundMethod/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]UnboundMethod/*!*/ method) {

            // TODO:
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, method);
        }

        // thread-safe:
        // Defines method using mangled CLR name and aliases that method with the actual CLR name.
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static UnboundMethod/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [NotNull]ClrName/*!*/ methodName, [NotNull]UnboundMethod/*!*/ method) {

            // TODO:
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, method);
        }

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static Proc/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block,
            object self, [DefaultProtocol, NotNull]string/*!*/ methodName) {

            // TODO:
            return ModuleOps.DefineMethod(scope, block, scope.RubyContext.GetOrCreateSingletonClass(self), methodName);
        }

        // thread-safe:
        // Defines method using mangled CLR name and aliases that method with the actual CLR name.
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static Proc/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, [NotNull]BlockParam/*!*/ block,
            object self, [NotNull]ClrName/*!*/ methodName) {

            // TODO:
            return ModuleOps.DefineMethod(scope, block, scope.RubyContext.GetOrCreateSingletonClass(self), methodName);
        }

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static Proc/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [DefaultProtocol, NotNull]string/*!*/ methodName, [NotNull]Proc/*!*/ block) {

            // TODO:
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, block);
        }

        // thread-safe:
        [RubyMethod("define_singleton_method", RubyMethodAttributes.PublicInstance)]
        public static Proc/*!*/ DefineSingletonMethod(RubyScope/*!*/ scope, object self,
            [NotNull]ClrName/*!*/ methodName, [NotNull]Proc/*!*/ block) {

            // TODO:
            return ModuleOps.DefineMethod(scope, scope.RubyContext.GetOrCreateSingletonClass(self), methodName, block);
        }

        #endregion


        #region methods, (private|protected|public|singleton)_methods (thread-safe)

        // thread-safe:
        [RubyMethod("methods")]
        public static RubyArray/*!*/ GetMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            var foreignMembers = context.GetForeignDynamicMemberNames(self);

            RubyClass immediateClass = context.GetImmediateClassOf(self);
            if (!inherited && !immediateClass.IsSingletonClass) {
                var result = new RubyArray();
                if (foreignMembers.Count > 0) {
                    foreach (var name in foreignMembers) {
	                    if (Tokenizer.IsMethodName(name) || Tokenizer.IsOperatorName(name)) {
                            result.Add(new ClrName(name));
                        }
                    }
                }
                return result;
            }

            return ModuleOps.GetMethods(immediateClass, inherited, RubyMethodAttributes.Public | RubyMethodAttributes.Protected, foreignMembers);
        }

        // thread-safe:
        [RubyMethod("singleton_methods")]
        public static RubyArray/*!*/ GetSingletonMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            RubyClass immediateClass = context.GetImmediateClassOf(self);
            return ModuleOps.GetMethods(immediateClass, inherited, RubyMethodAttributes.Singleton | RubyMethodAttributes.Public | RubyMethodAttributes.Protected);
        }

        // thread-safe:
        [RubyMethod("private_methods")]
        public static RubyArray/*!*/ GetPrivateMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            return GetMethods(context, self, inherited, RubyMethodAttributes.PrivateInstance);
        }

        // thread-safe:
        [RubyMethod("protected_methods")]
        public static RubyArray/*!*/ GetProtectedMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            return GetMethods(context, self, inherited, RubyMethodAttributes.ProtectedInstance);
        }

        // thread-safe:
        [RubyMethod("public_methods")]
        public static RubyArray/*!*/ GetPublicMethods(RubyContext/*!*/ context, object self, [DefaultParameterValue(true)]bool inherited) {
            return GetMethods(context, self, inherited, RubyMethodAttributes.PublicInstance);
        }

        private static RubyArray/*!*/ GetMethods(RubyContext/*!*/ context, object self, bool inherited, RubyMethodAttributes attributes) {
            RubyClass immediateClass = context.GetImmediateClassOf(self);
            return ModuleOps.GetMethods(immediateClass, inherited, attributes);
        }

        #endregion


        #region `, exec, system, fork, 1.9: spawn

#if FEATURE_PROCESS
        [RubyMethod("`", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_PROCESS")]
        [RubyMethod("`", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static MutableString/*!*/ ExecuteCommand(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]MutableString/*!*/ command) {
            Process p = RubyProcess.CreateProcess(context, command, true);

            string output = p.StandardOutput.ReadToEnd();
            if (Environment.NewLine != "\n") {
                output = output.Replace(Environment.NewLine, "\n");
            }
            MutableString result = MutableString.Create(output, RubyEncoding.GetRubyEncoding(p.StandardOutput.CurrentEncoding));
            return result;
        }

        // Overloads of exec and system will always execute using the Windows shell if there is only the command parameter
        // If args parameter is passed, it will execute the command directly without going to the shell.

        [RubyMethod("exec", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_PROCESS")]
        [RubyMethod("exec", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static void Execute(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]MutableString/*!*/ command) {
            Process p = RubyProcess.CreateProcess(context, command, false);
            p.WaitForExit();
            Exit(self, p.ExitCode);
        }

        [RubyMethod("exec", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_PROCESS")]
        [RubyMethod("exec", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static void Execute(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]MutableString/*!*/ command,
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ args) {
            Process p = RubyProcess.CreateProcess(context, command, args);
            Exit(self, p.ExitCode);
        }

        [RubyMethod("system", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_PROCESS")]
        [RubyMethod("system", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static bool System(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]MutableString/*!*/ command) {
            try {
                Process p = RubyProcess.CreateProcess(context, command, false);
                p.WaitForExit();
                return p.ExitCode == 0;
            } catch (FileNotFoundException) {
                return false;
            }
        }

        [RubyMethod("system", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_PROCESS")]
        [RubyMethod("system", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static bool System(RubyContext/*!*/ context, object self, [DefaultProtocol, NotNull]MutableString/*!*/ command,
            [DefaultProtocol, NotNullItems]params MutableString/*!*/[]/*!*/ args) {
            try {
                Process p = RubyProcess.CreateProcess(context, command, args);
                return p.ExitCode == 0;
            } catch (FileNotFoundException) {
                return false;
            }
        }

        //fork
#endif
        #endregion

        #region select, sleep

        [RubyMethod("select", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("select", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Select(RubyContext/*!*/ context, object self, RubyArray read, [Optional]RubyArray write, [Optional]RubyArray error) {
            return RubyIOOps.Select(context, null, read, write, error);
        }

        [RubyMethod("select", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("select", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Select(RubyContext/*!*/ context, object self, RubyArray read, [Optional]RubyArray write, [Optional]RubyArray error, int timeoutInSeconds) {
            return RubyIOOps.Select(context, null, read, write, error, timeoutInSeconds);
        }

        [RubyMethod("select", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("select", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Select(RubyContext/*!*/ context, object self, RubyArray read, [Optional]RubyArray write, [Optional]RubyArray error, double timeoutInSeconds) {
            return RubyIOOps.Select(context, null, read, write, error, timeoutInSeconds);
        }

#if FEATURE_THREAD
        [RubyMethod("sleep", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_THREAD")]
        [RubyMethod("sleep", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_THREAD")]
        public static void Sleep(object self) {
            ThreadOps.DoSleep();
        }

        [RubyMethod("sleep", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_THREAD")]
        [RubyMethod("sleep", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_THREAD")]
        public static int Sleep(object self, int seconds) {
            if (seconds < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must be positive");
            }

            long ms = seconds * 1000;
            return ThreadOps.DoSleep(ms > Int32.MaxValue ? Timeout.Infinite : (int)ms);
        }

        [RubyMethod("sleep", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_THREAD")]
        [RubyMethod("sleep", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_THREAD")]
        public static int Sleep(object self, double seconds) {
            if (seconds < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must be positive");
            }

            double ms = seconds * 1000;
            return ThreadOps.DoSleep(ms > Int32.MaxValue ? Timeout.Infinite : (int)ms);
        }
#endif

        #endregion

        #region test
#if FEATURE_FILESYSTEM
        [RubyMethod("test", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_FILESYSTEM")]
        [RubyMethod("test", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object Test(ConversionStorage<MutableString>/*!*/ toPath, object self, [NotNull]MutableString/*!*/ cmd, object path) {
            if (cmd.IsEmpty) {
                throw RubyExceptions.CreateTypeConversionError("String", "Integer");
            }
            return Test(toPath, self, cmd.GetChar(0), path);
        }

        [RubyMethod("test", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_FILESYSTEM")]
        [RubyMethod("test", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object Test(ConversionStorage<MutableString>/*!*/ toPath, object self, [DefaultProtocol]int cmd, object path) {
            RubyContext context = toPath.Context;
            MutableString pathStr = Protocols.CastToPath(toPath, path);
            cmd &= 0xFF;
            switch (cmd) {
                case 'A':
                    return RubyFileOps.RubyStatOps.AccessTime(RubyFileOps.RubyStatOps.Create(context, pathStr));

                case 'b':
                    return RubyFileOps.RubyStatOps.IsBlockDevice(RubyFileOps.RubyStatOps.Create(context, pathStr));

                case 'C':
                    return RubyFileOps.RubyStatOps.CreateTime(RubyFileOps.RubyStatOps.Create(context, pathStr));

                case 'c':
                    return RubyFileOps.RubyStatOps.IsCharDevice(RubyFileOps.RubyStatOps.Create(context, pathStr));

                case 'd':
                    return RubyFileOps.DirectoryExists(context, pathStr);

                case 'e':
                case 'f':
                    return RubyFileOps.FileExists(context, pathStr);

                case 'g':
                    return RubyFileOps.RubyStatOps.IsSetGid(RubyFileOps.RubyStatOps.Create(context, pathStr));

                case 'G':
                    return RubyFileOps.RubyStatOps.IsGroupOwned(RubyFileOps.RubyStatOps.Create(context, pathStr));

                case 'k':
                    return RubyFileOps.RubyStatOps.IsSticky(RubyFileOps.RubyStatOps.Create(context, pathStr));

                case 'l':
                    return RubyFileOps.RubyStatOps.IsSymLink(RubyFileOps.RubyStatOps.Create(context, pathStr));

                case 'M': throw new NotImplementedException();
                case 'O': throw new NotImplementedException();
                case 'o': throw new NotImplementedException();
                case 'p': throw new NotImplementedException();
                case 'r': throw new NotImplementedException();
                case 'R': throw new NotImplementedException();
                case 's': throw new NotImplementedException();
                case 'S': throw new NotImplementedException();
                case 'u': throw new NotImplementedException();
                case 'w': throw new NotImplementedException();
                case 'W': throw new NotImplementedException();
                case 'x': throw new NotImplementedException();
                case 'X': throw new NotImplementedException();
                case 'z': throw new NotImplementedException();
                default:
                    throw RubyExceptions.CreateArgumentError("unknown command ?{0}", (char)cmd);
            }
        }

        [RubyMethod("test", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("test", RubyMethodAttributes.PublicSingleton)]
        public static object Test(
            RubyContext/*!*/ context,
            object self,
            int cmd,
            [DefaultProtocol, NotNull]MutableString/*!*/ file1,
            [DefaultProtocol, NotNull]MutableString/*!*/ file2) {
            cmd &= 0xFF;
            switch (cmd) {
                case '-': throw new NotImplementedException();
                case '=': throw new NotImplementedException();
                case '<': throw new NotImplementedException();
                case '>': throw new NotImplementedException();
                default:
                    throw RubyExceptions.CreateArgumentError("unknown command ?{0}", (char)cmd);
            }
        }
#endif
        #endregion

        #region syscall, trap
        
        //syscall

#if FEATURE_PROCESS
        [RubyMethod("trap", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_PROCESS")]
        [RubyMethod("trap", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static object Trap(RubyContext/*!*/ context, object self, object signalId, Proc proc) {
            return Signal.Trap(context, self, signalId, proc);
        }

        [RubyMethod("trap", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_PROCESS")]
        [RubyMethod("trap", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static object Trap(RubyContext/*!*/ context, [NotNull]BlockParam/*!*/ block, object self, object signalId) {
            return Signal.Trap(context, block, self, signalId);
        }

#endif
        #endregion

        #region abort, exit, exit!, at_exit

        [RubyMethod("abort", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("abort", RubyMethodAttributes.PublicSingleton)]
        public static void Abort(object/*!*/ self) {
            Exit(self, 1);
        }

        [RubyMethod("abort", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("abort", RubyMethodAttributes.PublicSingleton)]
        public static void Abort(BinaryOpStorage/*!*/ writeStorage, object/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ message) {
            var site = writeStorage.GetCallSite("write", 1);
            site.Target(site, writeStorage.Context.StandardErrorOutput, message);

            Exit(self, 1);
        }

        [RubyMethod("exit", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit", RubyMethodAttributes.PublicSingleton)]
        public static void Exit(object self) {
            Exit(self, 0);
        }

        [RubyMethod("exit", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit", RubyMethodAttributes.PublicSingleton)]
        public static void Exit(object self, [NotNull]bool isSuccessful) {
            Exit(self, isSuccessful ? 0 : 1);
        }

        [RubyMethod("exit", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit", RubyMethodAttributes.PublicSingleton)]
        public static void Exit(object self, [DefaultProtocol]int exitCode) {
            throw new SystemExit(exitCode, "exit");
        }

        [RubyMethod("exit!", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit!", RubyMethodAttributes.PublicSingleton)]
        public static void TerminateExecution(RubyContext/*!*/ context, object self) {
            TerminateExecution(context, self, 1);
        }

        [RubyMethod("exit!", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit!", RubyMethodAttributes.PublicSingleton)]
        public static void TerminateExecution(RubyContext/*!*/ context, object self, bool isSuccessful) {
            TerminateExecution(context, self, isSuccessful ? 0 : 1);
        }

        [RubyMethod("exit!", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exit!", RubyMethodAttributes.PublicSingleton)]
        public static void TerminateExecution(RubyContext/*!*/ context, object self, int exitCode) {
            context.DomainManager.Platform.TerminateScriptExecution(exitCode);
        }

        [RubyMethod("at_exit", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("at_exit", RubyMethodAttributes.PublicSingleton)]
        public static Proc/*!*/ AtExit(BlockParam/*!*/ block, object self) {
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("called without a block");
            }

            block.RubyContext.RegisterShutdownHandler(block.Proc);
            return block.Proc;
        }

        #endregion

        #region load, load_assembly, require, using_clr_extensions

        [RubyMethod("load", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        public static bool Load(ConversionStorage<MutableString>/*!*/ toPath, RubyScope/*!*/ scope, object self, object libraryName, [Optional]bool wrap) {
            return scope.RubyContext.Loader.LoadFile(
                scope.GlobalScope.Scope, 
                self, 
                Protocols.CastToPath(toPath, libraryName), 
                wrap ? LoadFlags.LoadIsolated : LoadFlags.None
            );
        }

        [RubyMethod("require", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("require", RubyMethodAttributes.PublicSingleton)]
        public static bool Require(ConversionStorage<MutableString>/*!*/ toPath, RubyScope/*!*/ scope, object self, object libraryName) {
            return scope.RubyContext.Loader.LoadFile(
                scope.GlobalScope.Scope, 
                self, 
                Protocols.CastToPath(toPath, libraryName), 
                LoadFlags.Require
            );
        }

        [RubyMethod("load_assembly", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("load_assembly", RubyMethodAttributes.PublicSingleton)]
        public static bool LoadAssembly(RubyContext/*!*/ context, object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ assemblyName, [DefaultProtocol, Optional, NotNull]MutableString libraryNamespace) {

            string initializer = libraryNamespace != null ? LibraryInitializer.GetFullTypeName(libraryNamespace.ConvertToString()) : null;
            return context.Loader.LoadAssembly(assemblyName.ConvertToString(), initializer, true, true) != null;
        }

        [RubyMethod("using_clr_extensions", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("using_clr_extensions", RubyMethodAttributes.PublicSingleton)]
        public static void UsingClrExtensions(RubyContext/*!*/ context, object self, RubyModule namespaceModule) {
            string ns;
            if (namespaceModule == null) {
                ns = "";
            } else if (namespaceModule.NamespaceTracker == null) {
                throw RubyExceptions.CreateNotClrNamespaceError(namespaceModule);
            } else if (context != namespaceModule.Context) {
                throw RubyExceptions.CreateTypeError("Cannot use namespace `{0}' defined in a foreign runtime #{1}",
                    namespaceModule.NamespaceTracker.Name, namespaceModule.Context.RuntimeId);
            } else {
                ns = namespaceModule.NamespaceTracker.Name;
            }

            context.ActivateExtensions(ns);
        }

        #endregion

        #region open

        // TODO: should call File#initialize

        private static object OpenWithBlock(BlockParam/*!*/ block, RubyIO file) {
            try {
                object result;
                block.Yield(file, out result);
                return result;
            } finally {
                file.Close();
            }
        }

        private static void SetPermission(RubyContext/*!*/ context, string/*!*/ fileName, int/*!*/ permission) {
            bool existingFile = context.DomainManager.Platform.FileExists(fileName);

            if (!existingFile) {
                RubyFileOps.Chmod(fileName, permission);
            }
        }
        private static RubyIO CheckOpenPipe(RubyContext/*!*/ context, MutableString path, IOMode mode) {
            string fileName = path.ConvertToString();
            if (fileName.Length > 0 && fileName[0] == '|') {
#if FEATURE_PROCESS
                if (fileName.Length > 1 && fileName[1] == '-') {
                    throw new NotImplementedError("forking a process is not supported");
                }
                return RubyIOOps.OpenPipe(context, path.GetSlice(1), (IOMode)mode);
#else
                throw new NotSupportedException("open cannot create a subprocess");
#endif
            }
            return null;
        }

        [RubyMethod("open", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RubyIO/*!*/ Open(
            RubyContext/*!*/ context,
            object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ path,
            [DefaultProtocol, Optional]MutableString modeString,
            [DefaultProtocol, DefaultParameterValue(RubyFileOps.ReadWriteMode)]int permission) {

            IOMode mode = IOModeEnum.Parse(modeString);
            
            RubyIO pipe = CheckOpenPipe(context, path, mode);
            if (pipe != null) {
                return pipe;
            }

            string fileName = path.ConvertToString();
            RubyIO file = new RubyFile(context, fileName, mode);

            SetPermission(context, fileName, permission);

            return file;
        }

        [RubyMethod("open", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static object Open(
            RubyContext/*!*/ context,
            [NotNull]BlockParam/*!*/ block,
            object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ path,
            [DefaultProtocol, Optional]MutableString mode,
            [DefaultProtocol, DefaultParameterValue(RubyFileOps.ReadWriteMode)]int permission) {

            RubyIO file = Open(context, self, path, mode, permission);
            return OpenWithBlock(block, file);
        }

        [RubyMethod("open", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RubyIO/*!*/ Open(
            RubyContext/*!*/ context,
            object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ path,
            int mode,
            [DefaultProtocol, DefaultParameterValue(RubyFileOps.ReadWriteMode)]int permission) {

            RubyIO pipe = CheckOpenPipe(context, path, (IOMode)mode);
            if (pipe != null) {
                return pipe;
            }

            string fileName = path.ConvertToString();
            RubyIO file = new RubyFile(context, fileName, (IOMode)mode);

            SetPermission(context, fileName, permission);

            return file;
        }

        [RubyMethod("open", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static object Open(
            RubyContext/*!*/ context,
            [NotNull]BlockParam/*!*/ block,
            object self,
            [DefaultProtocol, NotNull]MutableString/*!*/ path,
            int mode,
            [DefaultProtocol, DefaultParameterValue(RubyFileOps.ReadWriteMode)]int permission) {

            RubyIO file = Open(context, self, path, mode, permission);
            return OpenWithBlock(block, file);
        }

        #endregion

        #region p, print, printf, putc, puts, display, warn, gets, getc

        [RubyMethod("p", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("p", RubyMethodAttributes.PublicSingleton)]
        public static object PrintInspect(BinaryOpStorage/*!*/ writeStorage, UnaryOpStorage/*!*/ inspectStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            object self, params object[]/*!*/ args) {

            var inspect = inspectStorage.GetCallSite("inspect");
            var inspectedArgs = new MutableString[args.Length];
            for (int i = 0; i < args.Length; i++) {
                inspectedArgs[i] = Protocols.ConvertToString(tosConversion, inspect.Target(inspect, args[i]));
            }

            // no dynamic dispatch to "puts":
            foreach (var arg in inspectedArgs) {
                PrintOps.Puts(writeStorage, writeStorage.Context.StandardOutput, arg);
            }

            if (args.Length == 0) {
                return null;
            } else if (args.Length == 1) {
                return args[0];
            } else {
                return new RubyArray(args);
            }
        }

        [RubyMethod("print", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("print", RubyMethodAttributes.PublicSingleton)]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, RubyScope/*!*/ scope, object self) {
            // no dynamic dispatch to "print":
            PrintOps.Print(writeStorage, scope, scope.RubyContext.StandardOutput);
        }

        [RubyMethod("print", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("print", RubyMethodAttributes.PublicSingleton)]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object self, object val) {
            // no dynamic dispatch to "print":
            PrintOps.Print(writeStorage, writeStorage.Context.StandardOutput, val);
        }

        [RubyMethod("print", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("print", RubyMethodAttributes.PublicSingleton)]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object self, params object[]/*!*/ args) {
            // no dynamic dispatch to "print":
            PrintOps.Print(writeStorage, writeStorage.Context.StandardOutput, args);
        }

        // this overload is called only if the first parameter is string:
        [RubyMethod("printf", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("printf", RubyMethodAttributes.PublicSingleton)]
        public static void PrintFormatted(
            StringFormatterSiteStorage/*!*/ storage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            BinaryOpStorage/*!*/ writeStorage,
            object self, [NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {

            PrintFormatted(storage, stringCast, writeStorage, self, storage.Context.StandardOutput, format, args);
        }

        [RubyMethod("printf", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("printf", RubyMethodAttributes.PublicSingleton)]
        public static void PrintFormatted(
            StringFormatterSiteStorage/*!*/ storage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            BinaryOpStorage/*!*/ writeStorage,
            object self, object io, [NotNull]object/*!*/ format, params object[]/*!*/ args) {

            Debug.Assert(!(io is MutableString));

            // TODO: BindAsObject attribute on format?
            // format cannot be strongly typed to MutableString due to ambiguity between signatures (MS, object) vs (object, MS)
            Protocols.Write(writeStorage, io,
                Sprintf(storage, self, Protocols.CastToString(stringCast, format), args)
            );
        }

        [RubyMethod("putc", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("putc", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Putc(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ arg) {
            // no dynamic dispatch:
            return PrintOps.Putc(writeStorage, writeStorage.Context.StandardOutput, arg);
        }

        [RubyMethod("putc", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("putc", RubyMethodAttributes.PublicSingleton)]
        public static int Putc(BinaryOpStorage/*!*/ writeStorage, object self, [DefaultProtocol]int arg) {
            // no dynamic dispatch:
            return PrintOps.Putc(writeStorage, writeStorage.Context.StandardOutput, arg);
        }

        [RubyMethod("puts", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("puts", RubyMethodAttributes.PublicSingleton)]
        public static void PutsEmptyLine(BinaryOpStorage/*!*/ writeStorage, object self) {
            // call directly, no dynamic dispatch to "self":
            PrintOps.PutsEmptyLine(writeStorage, writeStorage.Context.StandardOutput);
        }

        [RubyMethod("puts", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("puts", RubyMethodAttributes.PublicSingleton)]
        public static void PutString(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            ConversionStorage<IList>/*!*/ tryToAry, object self, object arg) {

            // call directly, no dynamic dispatch to "self":
            PrintOps.Puts(writeStorage, tosConversion, tryToAry, writeStorage.Context.StandardOutput, arg);
        }

        [RubyMethod("puts", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("puts", RubyMethodAttributes.PublicSingleton)]
        public static void PutString(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ arg) {
            // call directly, no dynamic dispatch to "self":
            PrintOps.Puts(writeStorage, writeStorage.Context.StandardOutput, arg);
        }

        [RubyMethod("puts", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("puts", RubyMethodAttributes.PublicSingleton)]
        public static void PutString(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            ConversionStorage<IList>/*!*/ tryToAry, object self, params object[]/*!*/ args) {

            // call directly, no dynamic dispatch to "self":
            PrintOps.Puts(writeStorage, tosConversion, tryToAry, writeStorage.Context.StandardOutput, args);
        }

        [RubyMethod("display")]
        public static void Display(BinaryOpStorage/*!*/ writeStorage, object self) {
            Protocols.Write(writeStorage, writeStorage.Context.StandardOutput, self);
        }

        [RubyMethod("warn", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("warn", RubyMethodAttributes.PublicSingleton)]
        public static void ReportWarning(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            object self, object message) {

            PrintOps.ReportWarning(writeStorage, tosConversion, message);
        }

        [RubyMethod("gets", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("gets", RubyMethodAttributes.PublicSingleton)]
        public static object ReadInputLine(CallSiteStorage<Func<CallSite, object, object>>/*!*/ storage, object self) {
            var site = storage.GetCallSite("gets", 0);
            return site.Target(site, storage.Context.StandardInput);
        }

        [RubyMethod("gets", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("gets", RubyMethodAttributes.PublicSingleton)]
        public static object ReadInputLine(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ storage, object self, 
            [NotNull]MutableString/*!*/ separator) {

            var site = storage.GetCallSite("gets", 1);
            return site.Target(site, storage.Context.StandardInput, separator);
        }

        [RubyMethod("gets", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("gets", RubyMethodAttributes.PublicSingleton)]
        public static object ReadInputLine(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ storage, object self,
            [NotNull]MutableString/*!*/ separator, [DefaultProtocol]int limit) {

            var site = storage.GetCallSite("gets", 2);
            return site.Target(site, storage.Context.StandardInput, separator, limit);
        }

        #endregion

        #region split, chomp, chop, gsub, sub, format, sprintf

        //split
        //chomp
        //chomp!
        //chop
        //chop!
        //gsub
        //gsub!
        //sub
        //sub!

        [RubyMethod("format", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("format", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("sprintf", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("sprintf", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Sprintf(StringFormatterSiteStorage/*!*/ storage,
            object self, [DefaultProtocol, NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {

            return new StringFormatter(storage, format.ConvertToString(), format.Encoding, args).Format();
        }

        #endregion

        #region rand, srand
#if FEATURE_CRYPTOGRAPHY
        private static RNGCryptoServiceProvider _RNGCryptoServiceProvider;

        [RubyMethod("srand", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_CRYPTOGRAPHY")]
        [RubyMethod("srand", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_CRYPTOGRAPHY")]
        public static object SeedRandomNumberGenerator(RubyContext/*!*/ context, object self) {
            // This should use a combination of the time, the process id, and a sequence number.

            if (_RNGCryptoServiceProvider == null) {
                _RNGCryptoServiceProvider = new RNGCryptoServiceProvider();
            }

            int secureRandomNumber = 0;
            do {
                byte[] b = new byte[4];
                _RNGCryptoServiceProvider.GetBytes(b);
                secureRandomNumber = ((int)b[0] << 24) | ((int)b[1] << 16) | ((int)b[2] << 8) | b[3];
            } while (secureRandomNumber == 0);
            return SeedRandomNumberGenerator(context, self, secureRandomNumber);
        }

        [RubyMethod("srand", RubyMethodAttributes.PrivateInstance, BuildConfig = "FEATURE_CRYPTOGRAPHY")]
        [RubyMethod("srand", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_CRYPTOGRAPHY")]
        public static object SeedRandomNumberGenerator(RubyContext/*!*/ context, object self, [DefaultProtocol]IntegerValue seed) {
            object result = context.RandomNumberGeneratorSeed;
            context.SeedRandomNumberGenerator(seed);
            return result;
        }
#endif
        [RubyMethod("rand", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("rand", RubyMethodAttributes.PublicSingleton)]
        public static double Random(RubyContext/*!*/ context, object self) {
            return context.RandomNumberGenerator.NextDouble();
        }

        [RubyMethod("rand", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("rand", RubyMethodAttributes.PublicSingleton)]
        public static object Random(RubyContext/*!*/ context, object self, int limit) {
            Random generator = context.RandomNumberGenerator;
            if (limit == Int32.MinValue) {
                return generator.Random(-(BigInteger)limit);
            } else {
                return ScriptingRuntimeHelpers.Int32ToObject(generator.Next(limit));
            }
        }

        [RubyMethod("rand", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("rand", RubyMethodAttributes.PublicSingleton)]
        public static object Random(ConversionStorage<IntegerValue>/*!*/ conversion, RubyContext/*!*/ context, object self, object limit) {
            IntegerValue intLimit = Protocols.ConvertToInteger(conversion, limit);
            Random generator = context.RandomNumberGenerator;

            bool isFixnum;
            int fixnum = 0;
            BigInteger bignum = BigInteger.Zero;
            if (intLimit.IsFixnum) {
                if (intLimit.Fixnum == Int32.MinValue) {
                    bignum = -(BigInteger)intLimit.Fixnum;
                    isFixnum = false;
                } else {
                    fixnum = Math.Abs(intLimit.Fixnum);
                    isFixnum = true;
                }
            } else {
                bignum = intLimit.Bignum.Abs();
                isFixnum = intLimit.Bignum.AsInt32(out fixnum);
            }

            if (isFixnum) {
                if (fixnum == 0) {
                    return generator.NextDouble();
                } else {
                    return ScriptingRuntimeHelpers.Int32ToObject(generator.Next(fixnum));
                }
            } else {
                return generator.Random(bignum);
            }
        }

        #endregion

        #region set_trace_func, trace_var, untrace_var

        [RubyMethod("set_trace_func", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("set_trace_func", RubyMethodAttributes.PublicSingleton)]
        public static Proc SetTraceListener(RubyContext/*!*/ context, object self, Proc listener) {
            if (listener != null && !context.RubyOptions.EnableTracing) {
                throw new NotSupportedException("Tracing is not supported unless -trace option is specified.");
            }
            return context.TraceListener = listener;
        }

        //trace_var
        //untrace_var

        #endregion

        #region to_enum

        [RubyMethod("to_enum")]
        [RubyMethod("enum_for")]
        public static Enumerator/*!*/ Create(object self, [DefaultProtocol, NotNull]string/*!*/ enumeratorName, params object[]/*!*/ targetParameters) {
            return new Enumerator(self, enumeratorName, targetParameters);
        }

        #endregion
    }
}
