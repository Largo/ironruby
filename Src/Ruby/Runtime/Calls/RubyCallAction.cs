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

using System.Linq.Expressions;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Reflection;
using System.Numerics;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Generation;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Calls {
    using AstFactory = IronRuby.Compiler.Ast.AstFactory;
    using Ast = Expression;

    public class RubyCallAction : RubyMetaBinder {
        private readonly RubyCallSignature _signature;
        private readonly string/*!*/ _methodName;

        public override RubyCallSignature Signature {
            get { return _signature; }
        }

        public string/*!*/ MethodName {
            get { return _methodName; }
        }

        // `defined?(obj.m)' answers a bool; one in a light frame (HasLightScope) answers it boxed, or RubyOps.LightScopeRequired
        // when the rule would read the scope (MethodDefinition.LightFrameRewriter.LightIsDefined).
        public override Type/*!*/ ReturnType {
            get { return _signature.ResolveOnly && !_signature.HasLightScope ? typeof(bool) : typeof(object); }
        }

        internal protected RubyCallAction(RubyContext context, string/*!*/ methodName, RubyCallSignature signature) 
            : base(context) {
            Assert.NotNull(methodName);
            
            // a virtual call cannot be a super call nor interop call:
            Debug.Assert(!signature.IsVirtualCall || !signature.IsSuperCall && !signature.IsInteropCall);

            // a super call must have implicit self:
            Debug.Assert(!signature.IsSuperCall || signature.HasImplicitSelf);

            _methodName = methodName;
            _signature = signature;
        }

        /// <summary>
        /// Creates a runtime-bound call site binder.
        /// </summary>
        public static RubyCallAction/*!*/ Make(RubyContext/*!*/ context, string/*!*/ methodName, int argumentCount) {
            return Make(context, methodName, RubyCallSignature.Simple(argumentCount));
        }

        /// <summary>
        /// Creates a runtime-bound call site binder.
        /// </summary>
        public static RubyCallAction/*!*/ Make(RubyContext/*!*/ context, string/*!*/ methodName, RubyCallSignature signature) {
            ContractUtils.RequiresNotNull(context, "context");
            ContractUtils.RequiresNotNull(methodName, "methodName");
            return context.MetaBinderFactory.Call(methodName, signature);
        }

        /// <summary>
        /// Creates a call site binder that can be used from multiple runtimes. The site it binds for can be called from multiple runtimes.
        /// </summary>
        [Emitted]
        public static RubyCallAction/*!*/ MakeShared(string/*!*/ methodName, RubyCallSignature signature) {
            // TODO: reduce usage of these sites to minimum
            return RubyMetaBinderFactory.Shared.Call(methodName, signature);
        }

        public override string/*!*/ ToString() {
            return _methodName + _signature.ToString() + (Context != null ? " @" + Context.RuntimeId.ToString() : null);
        }

        public override Expression/*!*/ CreateExpression() {
            return Expression.Call(
                Methods.GetMethod(typeof(RubyCallAction), "MakeShared", typeof(string), typeof(RubyCallSignature)),
                AstUtils.Constant(_methodName),
                _signature.CreateExpression()
            );
        }

        #region Precompiled Rules

        /// <summary>
        /// The dispatchers this binder made that have spent their budget: the calls they would take are hot (see LibraryDispatcher.IsHot).
        /// </summary>
        internal LibraryDispatcher[] SpentDispatchers;

        internal override int GetRuleCompilationThreshold(object[]/*!*/ args, RubyContext/*!*/ context) {
            int targetIndex = Signature.HasScope ? 1 : 0;
            return (Context != null && targetIndex < args.Length) ?
                LibraryDispatcher.GetRuleCompilationThreshold(this, args[targetIndex], context) : context.CompilationThreshold;
        }

        protected override object BindPrecompiled(Type/*!*/ delegateType, object[]/*!*/ args) {
            // IsVariableCall only decides which error a call that finds no method raises; a dispatcher
            // is only made for a method that was found, and hands every other case back to the
            // binder. Leaving it out of the mask sent every bare `foo` to a rule of its own.
            if (Context == null ||
                Signature.ResolveOnly ||
                (Signature.Flags & ~(RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasScope | RubyCallFlags.HasBlock | RubyCallFlags.HasRhsArgument | RubyCallFlags.HasSourceBlock | RubyCallFlags.IsVariableCall | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasFreshSplat | RubyCallFlags.HasLightScope)) != 0 ||
                Signature.HasSplattedArgument && Signature.HasRhsArgument) {
                return null;
            }

            // A site with a light scope (a call in a method that runs without its scope, see MethodDefinition.LightScopeCall) is
            // handed the declaring scope, not the caller's. The dispatcher of a Ruby method or an attribute accessor reads no scope:
            // it calls the method with the receiver, the block and the arguments, which is all the rule does once the method is
            // resolved as the rule resolves it - an explicit receiver's public method, which every caller sees, and no refinement.
            // Everything else (library methods, which may take the scope, a splat, a refined name) is left to the rule, which
            // answers RubyOps.LightScopeRequired where it would read the scope. Without this a light site whose receivers are of
            // many classes - a visitor's `node.accept(self)' - built and compiled a rule for every class, and once there were more
            // than the DLR's rule caches hold, kept building them again.
            bool light = Signature.HasLightScope;
            if (light && Signature.HasSplattedArgument) {
                return null;
            }

            // Nor can it hold RubyMethodInfo.BuildCallNoFlow's unmarking of a keyword-arguments
            // hash handed to a method that declares no keywords. A call site's syntax decides
            // whether it passes keywords, so seeing one here means every call at this site
            // passes one: unless the method takes keywords (checked below), hand the site to the
            // rule builder instead of precompiling it.
            bool passesKeywords = false;
            for (int i = 0; i < args.Length; i++) {
                if (RubyOps.IsKeywordArgumentsHash(args[i])) {
                    passesKeywords = true;
                    break;
                }
            }

            RubyScope scope;
            object target;
            if (Signature.HasScope) {
                scope = (RubyScope)args[0];
                target = args[1];
            } else {
                scope = Context.EmptyScope;
                target = args[0];
            }

            int version;
            MethodResolutionResult method;
            ClrReceiverTest clrTest;
            RefinementActivation refinements = null;
            RecordScope recordScope = RecordScope.None;
            RubyClass targetClass = Context.GetImmediateClassOf(target);
            using (targetClass.Context.ClassHierarchyLocker()) {
                // initialization changes the version (see MetaObjectBuilder.AddTargetTypeTest):
                targetClass.InitializeMethodsNoLock();
                // A call a refinement could reach is resolved under the caller's refinements, and its dispatcher
                // has to test them as the rule would. It can where the rule reads them from a method's record (see
                // MakeRefinementGuard); any other call of the kind is left to rule building. Refining the name
                // later re-versions the class, which retires a dispatcher made without the test.
                if (Signature.HasScope && targetClass.Context.IsRefinementSensitive(targetClass, _methodName)) {
                    if (light) {
                        return null;
                    }
                    refinements = scope.GetActiveRefinements();
                    recordScope = GetRecordScope(scope, refinements);
                    if (recordScope == RecordScope.None) {
                        return null;
                    }
                    version = targetClass.Version.Method;
                    method = targetClass.ResolveMethodNoLock(_methodName, GetVisibilityContext(Signature, scope), MethodLookup.Default,
                        refinements).InvalidateSitesOnOverride();
                } else {
                    version = targetClass.Version.Method;
                    method = targetClass.ResolveMethodForSiteNoLock(_methodName, (light && !Signature.HasImplicitSelf) ?
                        new VisibilityContext(RubyMethodAttributes.Public) : GetVisibilityContext(Signature, scope));
                }
                clrTest = (target != null && !(target is IRubyObject)) ? GetClrReceiverTest(targetClass, _methodName) : ClrReceiverTest.None;
            }

            if (!method.Found || method.Visibility == RubyMethodVisibility.Protected && !Signature.HasImplicitSelf) {
                return null;
            }

            // A singleton class is one object's: a dispatcher made for it serves that object only, and a light site that sees a new
            // one every few calls is better off with the shared rule (RubyMetaBinder tries it first). Not so a method define_method
            // made of a block (LambdaMethodCall, which reads no scope), the kind OpenStruct defines on each object's singleton class:
            // no rule is shared for it, so the first calls of each go through a LibraryDispatcher (below) rather than a rule each.
            // A module's singleton class is that module's for good, though, and the next module a site meets finds the shared rule as
            // well: a method called on a module or a class (File.join, Gem.dir) has a dispatcher of its own, as it has at a site with
            // a scope.
            if (light && targetClass.IsSingletonClass && method.Info.GetType() != typeof(RubyLambdaMethodInfo) && !(target is RubyModule)) {
                return null;
            }

            // The first calls go through a LibraryDispatcher, rather than a rule, until the call is hot:
            bool mayDispatch = LibraryDispatcher.MayDispatch(this, target);

            // the rule has to check for an ignored block (see BuildCall)
            if (Signature.HasSourceBlock && RequiresUnusedBlockCheck(method.Info)) {
                return null;
            }

            // a call through an alias has to tell the method its name (see RubyMethodInfo.BuildCallNoFlow): the precompiled dispatchers
            // of Ruby methods don't, a RubyMethodCallPlan does
            var rubyMethod = method.Info as RubyMethodInfo;
            if (rubyMethod != null && rubyMethod.DefinitionName != _methodName) {
                if (light || !mayDispatch || refinements != null || passesKeywords && !rubyMethod.TakesOrRefusesKeywords) {
                    return null;
                }
                return LibraryDispatcher.TryCreateRubyCall(this, delegateType, Signature, rubyMethod, targetClass, target, clrTest, version, args, null, _methodName);
            }

            // a method that takes or refuses keywords reads the hash as it is - nothing to unmark
            if (passesKeywords && (rubyMethod == null || !rubyMethod.TakesOrRefusesKeywords)) {
                return null;
            }

            MemberDispatcher dispatcher;
            if (refinements != null) {
                // A call a refinement could reach: a dispatcher that tests the refinements as the rule would, or the rule.
                // (The dispatchers of first calls below test no refinements.)
                dispatcher = (rubyMethod != null && !Signature.HasSplattedArgument) ?
                    rubyMethod.GetRefinedDispatcher(delegateType, Signature, target, version, refinements, recordScope == RecordScope.Block) :
                    null;
                if (dispatcher == null) {
                    return null;
                }
            } else {
                // A splatted call: of a Ruby or a library method, through the call its rule makes of the list's items (see TryCreateRubyCall,
                // LibraryDispatcher.TryCreate). The other dispatchers take the site's arguments as they are.
                if (Signature.HasSplattedArgument) {
                    if (!mayDispatch) {
                        return null;
                    }
                    if (rubyMethod != null) {
                        return LibraryDispatcher.TryCreateRubyCall(this, delegateType, Signature, rubyMethod, targetClass, target, clrTest, version, args);
                    }
                    var splattedMethodCall = method.Info as RubyCustomMethodInfo;
                    if (splattedMethodCall != null && target is RubyMethod && splattedMethodCall.IsMethodCall) {
                        return LibraryDispatcher.TryCreateMethodObjectCall(this, delegateType, Signature, (RubyMethod)target, targetClass, clrTest, version, args);
                    }
                    var splattedLibraryMethod = method.Info as RubyLibraryMethodInfo;
                    return (splattedLibraryMethod != null && !TracePoint.CCallTracing && !DispatchIntrinsics.IsSend(splattedLibraryMethod)) ?
                        LibraryDispatcher.TryCreate(this, delegateType, Signature, splattedLibraryMethod, targetClass, target, clrTest, version, args) :
                        null;
                }

                dispatcher = (!light || rubyMethod != null || method.Info is RubyAttributeAccessorInfo ||
                    method.Info is RubyCustomMethodInfo customReader && customReader.IsStructReader) ?
                    method.Info.GetDispatcher(delegateType, Signature, target, version) : null;

                // a Ruby method of a builtin class (much of the core library is written in Ruby), called on nil, true, false or a CLR object:
                if (dispatcher == null && rubyMethod != null && !(target is IRubyObject) &&
                    (target == null || target is bool || clrTest == ClrReceiverTest.Plain && !targetClass.IsSingletonClass) &&
                    !IsForeignObject(target)) {
                    dispatcher = rubyMethod.GetClrDispatcher(delegateType, Signature, target, targetClass, version);
                }
            }
            if (dispatcher != null) {
                object result = dispatcher.CreateDelegate(MethodDispatcher.UntypedFuncs.Contains(delegateType));
                RubyBinder.DumpPrecompiledRule(this, dispatcher);
                return result;
            }

            if (!mayDispatch) {
                return null;
            }

            // At a light site the first calls of a method define_method made of a block go on (see above), and so do those of a
            // library method whose overload takes no scope (LibraryDispatcher.TryCreate refuses one that does): the rule of such a
            // call reads nothing the declaring scope does not give it either. Every other method the dispatchers above do not
            // call is left to the rule. A light frame calls the core library's methods (Integer#&, String#[], File.join...) often
            // from sites that run a few times at start-up, each of which built and interpreted a rule (~150 us) before.
            if (light) {
                if (method.Info is RubyLambdaMethodInfo) {
                    // (by the name it is called by: an alias's is __callee__'s)
                    return LibraryDispatcher.TryCreateRubyCall(this, delegateType, Signature, method.Info, targetClass, target, clrTest, version, args,
                        name: _methodName);
                }
                var lightLibraryMethod = method.Info as RubyLibraryMethodInfo;
                return (lightLibraryMethod != null && !TracePoint.CCallTracing && !DispatchIntrinsics.IsSend(lightLibraryMethod)) ?
                    LibraryDispatcher.TryCreate(this, delegateType, Signature, lightLibraryMethod, targetClass, target, clrTest, version, args) :
                    null;
            }

            // The first calls of a Ruby method that those dispatchers do not call (one with optional parameters or a rest parameter,
            // say) go without a rule too, as library methods' do (below).
            if (rubyMethod != null) {
                return LibraryDispatcher.TryCreateRubyCall(this, delegateType, Signature, rubyMethod, targetClass, target, clrTest, version, args);
            }

            // and so do those of a method define_method made of a block
            if (method.Info is RubyLambdaMethodInfo) {
                return LibraryDispatcher.TryCreateRubyCall(this, delegateType, Signature, method.Info, targetClass, target, clrTest, version, args,
                    name: _methodName);
            }

            // The first calls of a library method from a site go without a rule. A rule built for a call is used for one
            // call more often than not - most of the prelude runs once - and building it costs more than many such calls.
            // Calls while c_call is traced get rules (the rule reports the call). Kernel#send of a symbol makes the call it makes
            // (whose rule is that call, see DispatchIntrinsics.TryBuildSend) through a site of its own. The same goes for Class#new
            // of a plain Ruby class.
            var custom = method.Info as RubyCustomMethodInfo;
            if (custom != null && target is RubyMethod && !passesKeywords && custom.IsMethodCall) {
                return LibraryDispatcher.TryCreateMethodObjectCall(this, delegateType, Signature, (RubyMethod)target, targetClass, clrTest, version, args);
            }

            if (custom != null && custom.IsInstanceConstructor && target is RubyClass && !passesKeywords) {
                return LibraryDispatcher.TryCreateConstruction(this, delegateType, Signature, (RubyClass)target, targetClass, version, args);
            }

            var libraryMethod = method.Info as RubyLibraryMethodInfo;
            if (libraryMethod != null && !TracePoint.CCallTracing && DispatchIntrinsics.IsSend(libraryMethod) && !passesKeywords) {
                return LibraryDispatcher.TryCreateSend(this, delegateType, Signature, targetClass, target, clrTest, version, args);
            }

            if (libraryMethod != null && !TracePoint.CCallTracing && !DispatchIntrinsics.IsSend(libraryMethod)) {
                return LibraryDispatcher.TryCreate(this, delegateType, Signature, libraryMethod, targetClass, target, clrTest, version, args);
            }

            return null;
        }

        #endregion

        #region Shared Rules

        // A rule that serves receivers of several classes, or of one class in several states, that resolve the call to the same method.
        // The first rule bound for a method is an ordinary one (tests a constant version); the second class (version) to resolve the call
        // to that method gets a rule that tests the class version against a set (see RuleVersionSet), and every further one is added to
        // the set once the call has been resolved for it again - with no rule built or compiled.
        private sealed class SharedRule {
            internal readonly RubyMemberInfo/*!*/ Member;
            // the call finds no method of its name, and Member is the method_missing it calls
            internal readonly bool IsMissing;
            internal readonly Type/*!*/ TargetType;
            internal readonly Type/*!*/ DelegateType;
            internal readonly int FirstVersion;

            // null until the shared rule is built:
            internal RuleVersionSet Versions;
            internal volatile object Rule;

            internal SharedRule(RubyMemberInfo/*!*/ member, bool isMissing, Type/*!*/ targetType, Type/*!*/ delegateType, int firstVersion) {
                Member = member;
                IsMissing = isMissing;
                TargetType = targetType;
                DelegateType = delegateType;
                FirstVersion = firstVersion;
            }
        }

        private const int MaxSharedRules = 16;

        private const RubyCallFlags SharableCallFlags = RubyCallFlags.HasScope | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasRhsArgument |
            RubyCallFlags.HasBlock | RubyCallFlags.HasImplicitSelf | RubyCallFlags.IsVariableCall | RubyCallFlags.HasSourceBlock |
            RubyCallFlags.HasLightScope | RubyCallFlags.HasFreshSplat;

        private static readonly object _SharedRulesLock = new object();
        private SharedRule[] _sharedRules;

        // Handed from BindSharedRule to Build of the same binder:
        [ThreadStatic]
        private static RuleVersionSet _PendingVersionSet;
        [ThreadStatic]
        private static RubyCallAction _PendingVersionSetOwner;

        private bool IsSharable {
            get {
                return !Signature.ResolveOnly && (Signature.Flags & ~SharableCallFlags) == 0 && !TracePoint.CCallTracing;
            }
        }

        // Resolves the call for the target as BindShared does: the method a rule shared with receivers of other classes would call
        // (or the method_missing a call of an undefined name makes), and the version of the target's class it is resolved for.
        // publicOnly: resolve for a caller that sees public methods only (see TryShareVersion).
        private bool ResolveShared(RubyContext/*!*/ context, RubyScope/*!*/ scope, object target, bool publicOnly, out int version,
            out RubyMemberInfo member, out bool isMissing) {
            version = 0;
            member = null;
            isMissing = false;

            // an object of another runtime:
            RubyClass targetClass = context.GetImmediateClassOf(target);
            if (targetClass.Context != context) {
                return false;
            }

            MethodResolutionResult method;
            RubyMemberInfo methodMissing = null;
            using (context.ClassHierarchyLocker()) {
                targetClass.InitializeMethodsNoLock();
                if (target != null && !(target is IRubyObject) && !HasPlainClrTypeTest(targetClass, _methodName)) {
                    return false;
                }
                // a rule shared by receivers of several classes cannot hold the lexical refinement guard either
                if (Signature.HasScope && context.IsRefinementSensitive(targetClass, _methodName)) {
                    return false;
                }
                version = targetClass.Version.Method;
                // As Resolve does: a site with a light scope has not got the caller's scope, so an explicit-receiver call there
                // looks for a public method; when there is none, nothing is shared and the ordinary bind reads the scope.
                var visibility = (publicOnly || Signature.HasLightScope && !Signature.HasImplicitSelf) ?
                    new VisibilityContext(RubyMethodAttributes.Public) : GetVisibilityContext(Signature, scope);
                method = targetClass.ResolveMethodForSiteNoLock(_methodName, visibility, MethodLookup.Default);

                // A call of a name a Ruby object's class has no method of, at any visibility, is a call of the method_missing it
                // resolves: OpenStruct's first `o.total = x' is, on a new singleton class each time. Resolve registers the lookup
                // of the missing name the same way, so that defining it re-versions the class.
                if (!method.Found && method.Info == null && method.IncompatibleVisibility == RubyMethodVisibility.None &&
                    target is IRubyObject && _methodName != Symbols.MethodMissing) {
                    methodMissing = targetClass.ResolveMethodMissingForSite(_methodName, RubyMethodVisibility.None);
                }
            }

            // Rules calling these methods depend on nothing but the method, the call signature and the arguments.
            // A protected method's rule also tests the caller's class.
            if (method.Found) {
                member = method.Info;
                isMissing = false;
            } else if (methodMissing != null && methodMissing.GetType() == typeof(RubyMethodInfo)) {
                member = methodMissing;
                isMissing = true;
            } else {
                return false;
            }
            if ((isMissing ? member.Visibility : method.Visibility) == RubyMethodVisibility.Protected ||
                member.GetType() != typeof(RubyLibraryMethodInfo) && member.GetType() != typeof(RubyMethodInfo)) {
                return false;
            }

            // respond_to?(:sym) and send(:sym) rules are specialized on the receiver's class (DispatchIntrinsics):
            if (!isMissing && DispatchIntrinsics.MaySpecialize(member, Signature, targetClass)) {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Called by the test of a shared rule (see RuleVersionSet.IsValid) for a Ruby object whose class version the rule does not
        /// hold yet: if the call resolves to the rule's method for the object's class, adds the version and returns true. This is
        /// what BindShared would do, but without going through the call site's update and the binder: a call in a method that
        /// runs for objects that each get a singleton class of their own (OpenStruct's update_to_values!, new_ostruct_member!...)
        /// sees a new class version on every call, and bound each of them (~2-10 us apiece, twice once the rule was compiled).
        /// The caller's scope is not at hand: a call with an explicit receiver whose visibility the bind reads from the scope is
        /// resolved for public methods, which every caller sees, so it takes in the version only if the rule's method is public there.
        /// </summary>
        internal bool TryShareVersion(object/*!*/ target, RuleVersionSet/*!*/ versionSet) {
            RubyContext context = Context;
            if (context == null || !IsSharable || versionSet.Member == null) {
                return false;
            }
            bool publicOnly = Signature.HasScope && !Signature.HasImplicitSelf && !Signature.HasLightScope && !Signature.IsInteropCall;

            int version;
            RubyMemberInfo member;
            bool isMissing;
            if (!ResolveShared(context, context.EmptyScope, target, publicOnly, out version, out member, out isMissing) ||
                member != versionSet.Member || isMissing != versionSet.IsMissing) {
                return false;
            }

            versionSet.TryAdd(version);
            return true;
        }

        internal override T BindShared<T>(object[]/*!*/ args, bool beforeDispatcher, out bool deferred) {
            deferred = false;
            RubyContext context = Context;
            if (context == null || !IsSharable) {
                return null;
            }

            RubyScope scope;
            object target;
            if (Signature.HasScope) {
                scope = (RubyScope)args[0];
                target = args[1];
            } else {
                scope = context.EmptyScope;
                target = args[0];
            }

            // The type test of a Ruby object is only a version check (see MetaObjectBuilder.AddTargetTypeTest), and so is that of nil
            // and of a CLR object whose class has neither singletons nor instances of a Ruby subclass. Not true/false: they are 
            // told apart by value.
            Type targetType;
            if (target == null) {
                targetType = typeof(DynamicNull);
            } else if (target is bool) {
                return null;
            } else {
                targetType = target.GetType();
            }

            int version;
            RubyMemberInfo member;
            bool isMissing;
            if (!ResolveShared(context, scope, target, false, out version, out member, out isMissing)) {
                return null;
            }

            SharedRule shared = null;
            var sharedRules = _sharedRules;
            if (sharedRules != null) {
                foreach (var rule in sharedRules) {
                    if (rule.Member == member && rule.IsMissing == isMissing && rule.TargetType == targetType && rule.DelegateType == typeof(T)) {
                        shared = rule;
                        break;
                    }
                }
            }

            if (shared == null) {
                // (see RubyMetaBinder.BindDelegateNoTiming)
                if (beforeDispatcher && member.DeclaringModule is RubyClass declaring && declaring.IsSingletonClass &&
                    declaring == context.GetImmediateClassOf(target)) {
                    deferred = true;
                    return null;
                }
                lock (_SharedRulesLock) {
                    sharedRules = _sharedRules;
                    if (sharedRules == null || sharedRules.Length < MaxSharedRules) {
                        _sharedRules = ArrayUtils.Append(sharedRules ?? new SharedRule[0], new SharedRule(member, isMissing, targetType, typeof(T), version));
                    }
                }
                return null;
            }

            object sharedRule = shared.Rule;
            if (sharedRule == null) {
                // the first rule bound for the method didn't match for some other reason than its class version:
                if (version == shared.FirstVersion) {
                    return null;
                }

                return BindSharedRule<T>(shared, args);
            }

            // The shared rule is handed out for a version it does not hold yet, and only then: a call that comes here although
            // the rule holds its version didn't match the rule (its arguments are of other types, say) and gets a rule of its own.
            if (!shared.Versions.TryAdd(version)) {
                // Or the rule is interpreted and has been compiled meanwhile: it then fails its test once, so that the site binds again
                // and picks up the compiled one (DynamicUtils.LightBind), which the rule's binder hands out without building a rule.
                // The shared rule is that one from now on; handing out the interpreted one again would fail and bind every call.
                // It goes to the binder's cache too: the interpreted one stays there and fails for every site that has not run the
                // call yet, so without the compiled one next to it each new site bound (a light site's first call of `a + 1' did).
                RuleBuilt = false;
                T compiled = this.LightBind<T>(args, Context.CompilationThreshold);
                if (!RuleBuilt) {
                    if (PhaseTimer.BindTrace) PhaseTimer.BindKind = "shared-compiled";
                    bool replaced = false;
                    lock (_SharedRulesLock) {
                        if (shared.Rule == sharedRule && sharedRule != (object)compiled) {
                            shared.Rule = compiled;
                            replaced = true;
                        }
                    }
                    if (replaced) {
                        CacheTarget(compiled);
                    }
                } else {
                    CacheTarget(compiled);
                }
                return compiled;
            }

            return (T)sharedRule;
        }

        // RubyMetaBinder.IsForeignMetaObject of the target: a dynamic object of another language is bound by its own meta-object
        internal static bool IsForeignObject(object target) {
            return target is IDynamicMetaObjectProvider && !(target is IRubyDynamicMetaObjectProvider) ||
                target != null && TypeUtils.IsComObjectType(target.GetType());
        }

        /// <summary>
        /// The test of a CLR object receiver (not nil, true or false) that MetaObjectBuilder.AddTargetTypeTest makes in a rule calling
        /// the method of that name.
        /// </summary>
        internal enum ClrReceiverTest {
            // another test (an adopted object)
            None,

            // its exact type and the version of its class - its singleton class if it has one (and no other object of the type has a
            // singleton method of that name or of method_missing): nothing else (HasPlainClrTypeTest, but for the singleton)
            Plain,

            // its exact type, and that it has no singleton class (nor a Ruby class) and its class's version (IsClrNonSingletonRuleValid):
            // some object of its type has a singleton method of that name, or of method_missing, or a Ruby class
            NonSingleton,

            // its exact type, and that it has a singleton class of the version (IsClrSingletonRuleValid)
            Singleton,
        }

        internal static ClrReceiverTest GetClrReceiverTest(RubyClass/*!*/ targetClass, string/*!*/ methodName) {
            if (targetClass.GetNonSingletonClass().IsRubyClass) {
                return ClrReceiverTest.None;
            }
            var nominalClass = targetClass.NominalClass;
            var singletonMethods = nominalClass.ClrSingletonMethods;
            bool singletonCheck = nominalClass.HasAdoptedInstances ||
                singletonMethods != null && (singletonMethods.ContainsKey(methodName) || singletonMethods.ContainsKey(Symbols.MethodMissing));

            return !singletonCheck ? ClrReceiverTest.Plain : targetClass.IsSingletonClass ? ClrReceiverTest.Singleton : ClrReceiverTest.NonSingleton;
        }

        // MetaObjectBuilder.AddTargetTypeTest tests a CLR object's type and its class version - and nothing else - in this case:
        internal static bool HasPlainClrTypeTest(RubyClass/*!*/ targetClass, string/*!*/ methodName) {
            if (targetClass.IsSingletonClass || targetClass.GetNonSingletonClass().IsRubyClass) {
                return false;
            }
            var nominalClass = targetClass.NominalClass;
            var singletonMethods = nominalClass.ClrSingletonMethods;
            return !nominalClass.HasAdoptedInstances && (singletonMethods == null ||
                !singletonMethods.ContainsKey(methodName) && !singletonMethods.ContainsKey(Symbols.MethodMissing));
        }

        private T/*!*/ BindSharedRule<T>(SharedRule/*!*/ shared, object[]/*!*/ args) where T : class {
            var versionSet = new RuleVersionSet(this);
            T result;

            _PendingVersionSet = versionSet;
            _PendingVersionSetOwner = this;
            try {
                result = this.LightBind<T>(args, Context.CompilationThreshold);
            } finally {
                _PendingVersionSet = null;
                _PendingVersionSetOwner = null;
            }
            CacheTarget(result);

            // The rule tests the version set iff Build has taken it and bound it to a version the method was resolved for:
            if (versionSet.Member == shared.Member && versionSet.IsMissing == shared.IsMissing) {
                lock (_SharedRulesLock) {
                    if (shared.Rule == null) {
                        shared.Versions = versionSet;
                        shared.Rule = result;
                    }
                }
            }
            return result;
        }

        #endregion

        private static bool RequiresUnusedBlockCheck(RubyMemberInfo/*!*/ method) {
            var rubyMethod = method as RubyMethodInfo;
            return rubyMethod != null && !rubyMethod.UsesBlock;
        }

        protected override bool Build(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, bool defaultFallback) {
            if (_PendingVersionSetOwner == this) {
                metaBuilder.TargetVersionSet = _PendingVersionSet;
                _PendingVersionSet = null;
                _PendingVersionSetOwner = null;
            }
            return BuildCall(metaBuilder, _methodName, args, defaultFallback, true, true);
        }

        // Returns true if the call was bound (with success or failure), false if fallback should be performed.
        internal static bool BuildCall(MetaObjectBuilder/*!*/ metaBuilder, string/*!*/ methodName, CallArguments/*!*/ args, 
            bool defaultFallback, bool callClrMethods) {
            return BuildCall(metaBuilder, methodName, args, defaultFallback, callClrMethods, false);
        }

        // siteNamesMethod: the call is the one the site's source makes, of the name it is written with - not one made for it
        // (`send(:name)', an interop binder's), whose site does not know what a rule for that name may answer
        // (DispatchIntrinsics.TryBuildBlockGiven).
        private static bool BuildCall(MetaObjectBuilder/*!*/ metaBuilder, string/*!*/ methodName, CallArguments/*!*/ args, 
            bool defaultFallback, bool callClrMethods, bool siteNamesMethod) {

            RubyMemberInfo methodMissing;
            var method = Resolve(metaBuilder, methodName, args, out methodMissing);

            if (method.Found) {
                if (!callClrMethods && !method.Info.IsRubyMember) {
                    return false;
                }

                if (args.Signature.ResolveOnly) {
                    metaBuilder.Result = AstFactory.True;
                    return true;    
                }
                
                if (args.Signature.IsVirtualCall && (!method.Info.IsRubyMember || IsOverriddenOverloadMismatch(args, methodName, method.Info))) {
                    metaBuilder.Result = Ast.Field(null, Fields.ForwardToBase);
                    return true;
                }

                if (DispatchIntrinsics.TryBuildRespondTo(metaBuilder, args, method.Info)) {
                    return true;
                }

                if (siteNamesMethod && DispatchIntrinsics.TryBuildBlockGiven(metaBuilder, args, method.Info, methodName)) {
                    return true;
                }

                if (callClrMethods && DispatchIntrinsics.TryBuildNotEquals(metaBuilder, args, method.Info)) {
                    return true;
                }

                string sentName;
                if (defaultFallback && callClrMethods && DispatchIntrinsics.TryBuildSend(metaBuilder, args, method.Info, out sentName)) {
                    return BuildCall(metaBuilder, sentName, args, defaultFallback, callClrMethods, false);
                }

                method.Info.BuildCall(metaBuilder, args, methodName);

                // MRI 3.4+: a block passed to a method that never uses it may be ignored
                if (args.Signature.HasSourceBlock && RequiresUnusedBlockCheck(method.Info) && !metaBuilder.Error && metaBuilder.Result != null) {
                    metaBuilder.Result = Ast.Block(
                        Methods.WarnUnusedBlock.OpCall(
                            AstUtils.Convert(args.GetBlockExpression(), typeof(Proc)),
                            AstUtils.Constant(method.Info, typeof(RubyMethodInfo))
                        ),
                        metaBuilder.Result
                    );
                }
                return true;
            } else if (args.Signature.ResolveOnly) {
                if (!args.Signature.HasImplicitSelf && method.IncompatibleVisibility == RubyMethodVisibility.None) {
                    // defined?(obj.m) for an obj with no m at all: MRI asks respond_to_missing?(:m, false)
                    metaBuilder.Result = Methods.IsTrue.OpCall(AstUtils.LightDynamic(
                        RubyCallAction.Make(args.RubyContext, Symbols.RespondToMissing, RubyCallSignature.WithImplicitSelf(2)),
                        args.TargetExpression,
                        Ast.Constant(args.RubyContext.CreateSymbol(methodName, RubyEncoding.Binary)),
                        AstUtils.Constant(false, typeof(object))
                    ));
                } else {
                    metaBuilder.Result = AstFactory.False;
                }
                return true;
            } else {
                return BuildMethodMissingCall(metaBuilder, args, methodName, methodMissing, method.IncompatibleVisibility, false, defaultFallback);
            }
        }

        /// <summary>
        /// A Ruby method overriding a CLR virtual method takes the name of every overload, but overloads call each other
        /// (Sleep() => Sleep(8) => Sleep(8, "bed")) and the Ruby method usually takes the arguments of just one of them.
        /// A virtual call with an argument count the Ruby method cannot take goes to the CLR implementation of that
        /// overload instead, as if the Ruby method overrode only the overloads it can take. An overload without an
        /// implementation (abstract) has nothing to go to, so it still gets the ArgumentError.
        /// </summary>
        private static bool IsOverriddenOverloadMismatch(CallArguments/*!*/ args, string/*!*/ methodName, RubyMemberInfo/*!*/ method) {
            int min, max;
            int count = args.Signature.ArgumentCount;
            if (!TryGetPositionalArgumentRange(method, out min, out max) || count >= min && (max < 0 || count <= max)) {
                return false;
            }

            return args.Target is IRubyType && HasClrImplementation(args.Target.GetType(), methodName, count);
        }

        // The number of positional arguments a Ruby method accepts; max is -1 if unbounded.
        private static bool TryGetPositionalArgumentRange(RubyMemberInfo/*!*/ method, out int min, out int max) {
            var signature = method.GetParameterSignature();
            if (signature != null) {
                min = signature.MinPositionalCount;
                max = signature.MaxPositionalCount;
                return true;
            }

            RubyMethodInfo rubyMethod;
            RubyLambdaMethodInfo lambdaMethod;
            if ((rubyMethod = method as RubyMethodInfo) != null) {
                var parameters = rubyMethod.Parameters;
                min = parameters.Mandatory.Length;
                max = (parameters.Unsplat != null) ? -1 : min + parameters.Optional.Length;
            } else if ((lambdaMethod = method as RubyLambdaMethodInfo) != null) {
                int arity = lambdaMethod.GetArity();
                min = (arity < 0) ? -arity - 1 : arity;
                max = (arity < 0) ? -1 : arity;
            } else if (method is RubyAttributeReaderInfo) {
                min = max = 0;
            } else if (method is RubyAttributeWriterInfo) {
                min = max = 1;
            } else {
                min = max = 0;
                return false;
            }
            return true;
        }

        // Whether the base class or an interface (a default method) of the type emitted for a Ruby class implements a
        // virtual method of the given override-site name taking the given number of arguments.
        private static bool HasClrImplementation(Type/*!*/ emittedType, string/*!*/ methodName, int argumentCount) {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            for (Type type = emittedType.BaseType; type != null; type = type.BaseType) {
                foreach (MethodInfo method in type.GetMethods(flags | BindingFlags.DeclaredOnly)) {
                    if (IsClrImplementation(method, methodName, argumentCount)) {
                        return true;
                    }
                }
            }

            foreach (Type interfaceType in emittedType.GetInterfaces()) {
                foreach (MethodInfo method in interfaceType.GetMethods(flags)) {
                    if (IsClrImplementation(method, methodName, argumentCount)) {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsClrImplementation(MethodInfo/*!*/ method, string/*!*/ methodName, int argumentCount) {
            if (!method.IsVirtual || method.IsAbstract || method.IsFinal || method.IsPrivate || method.IsAssembly) {
                return false;
            }

            var parameters = method.GetParameters();
            int count = (parameters.Length > 0 && parameters[0].ParameterType == typeof(RubyContext)) ? parameters.Length - 1 : parameters.Length;
            return count == argumentCount && GetVirtualSiteName(method) == methodName;
        }

        // The name ClsTypeEmitter gives the dynamic site of an override of the method.
        private static string/*!*/ GetVirtualSiteName(MethodInfo/*!*/ method) {
            string name = method.Name;
            if (method.IsSpecialName) {
                var defaultMember = (DefaultMemberAttribute)Attribute.GetCustomAttribute(method.DeclaringType, typeof(DefaultMemberAttribute), false);
                if (defaultMember != null && name == "get_" + defaultMember.MemberName) {
                    return "[]";
                } else if (defaultMember != null && name == "set_" + defaultMember.MemberName) {
                    return "[]=";
                } else if (name.StartsWith("get_", StringComparison.Ordinal)) {
                    return name.Substring(4);
                } else if (name.StartsWith("set_", StringComparison.Ordinal)) {
                    return name.Substring(4) + "=";
                }
            }
            return name;
        }

        // Returns true if the call was bound (with success or failure), false if fallback should be performed.
        internal static bool BuildAccess(MetaObjectBuilder/*!*/ metaBuilder, string/*!*/ methodName, CallArguments/*!*/ args, 
            bool defaultFallback, bool callClrMethods) {

            RubyMemberInfo methodMissing;
            var method = Resolve(metaBuilder, methodName, args, out methodMissing);

            if (method.Found) {
                if (!callClrMethods && !method.Info.IsRubyMember) {
                    return false;
                }

                if (method.Info.IsDataMember) {
                    method.Info.BuildCall(metaBuilder, args, methodName);
                } else {
                    metaBuilder.Result = Methods.CreateBoundMember.OpCall(
                        AstUtils.Convert(args.TargetExpression, typeof(object)),
                        Ast.Constant(method.Info, typeof(RubyMemberInfo)),
                        Ast.Constant(methodName)
                    );
                }
                return true;
            } else {
                // Ruby doesn't have "attribute_missing" so we will always use method_missing and return a bound method object:
                return BuildMethodMissingAccess(metaBuilder, args, methodName, methodMissing, method.IncompatibleVisibility, false, defaultFallback);
            }
        }

        private static VisibilityContext GetVisibilityContext(RubyCallSignature callSignature, RubyScope scope) {
            // A call that can only see public members says so whatever else it carries. It may
            // still need the scope - Kernel#public_send honours the refinements active where it
            // was called - and asking the scope who self is would make a protected method visible
            // to a receiver of the same class, which is exactly what public_send must not do.
            if (callSignature.IsInteropCall) {
                return new VisibilityContext(RubyMethodAttributes.Public);
            }

            return callSignature.HasImplicitSelf || !callSignature.HasScope ? 
                new VisibilityContext(RubyMethodAttributes.VisibilityMask) :
                new VisibilityContext(scope.SelfImmediateClass);
        }

        /// <summary>
        /// Integer's CLR members are those of Int32, its CLR type. An Int64 or BigInteger receiver is an Integer too, and a
        /// member only its own type has - (2**70).IsEven, .Sign, (2**40).is_even - is looked up there before the call goes to
        /// method_missing. The rule already tests the receiver's CLR type, so an Int32 never gets here; the test of the class
        /// version covers extension methods activated for the type later.
        /// </summary>
        private static MethodResolutionResult ResolveOwnClrMember(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args,
            RubyClass/*!*/ targetClass, string/*!*/ methodName, MethodResolutionResult notFound) {

            object target = args.Target;
            var context = targetClass.Context;
            if (!(target is long || target is BigInteger) || targetClass != context.IntegerClass ||
                !(context.GetClrTypeModule(target.GetType()) is RubyClass ownClass) || ownClass == targetClass) {
                return notFound;
            }

            metaBuilder.AddVersionTest(ownClass);
            RubyMemberInfo member;
            if (ownClass.TryGetClrMember(methodName, null, out member) && member.Visibility == RubyMethodVisibility.Public) {
                return new MethodResolutionResult(member, ownClass, true);
            }
            return notFound;
        }

        internal static MethodResolutionResult Resolve(MetaObjectBuilder/*!*/ metaBuilder, string/*!*/ methodName, CallArguments/*!*/ args,
            out RubyMemberInfo methodMissing) {

            MethodResolutionResult method;
            var targetClass = args.TargetClass;
            // Only a call with an explicit receiver asks the scope who self is, and only for a protected method. A site with a
            // light scope does not have the caller's scope to ask, so it looks for a public method, which every caller sees; if
            // there is none, the rule reads the scope after all and the call is made from a real one (CallArguments.ScopeRead).
            var signature = args.Signature;
            bool publicOnly = signature.HasLightScope && !signature.HasImplicitSelf && !signature.IsInteropCall;
            var visibilityContext = publicOnly ? new VisibilityContext(RubyMethodAttributes.Public) :
                GetVisibilityContext(signature, (signature.HasImplicitSelf || !signature.HasScope || signature.IsInteropCall) ? null : args.Scope);
            RefinementActivation refinements = null;
            using (targetClass.Context.ClassHierarchyLocker()) {
                var versionSet = metaBuilder.TargetVersionSet;
                metaBuilder.TargetVersionSet = null;

                metaBuilder.AddTargetTypeTest(args.Target, targetClass, args.TargetExpression, args.MetaContext, 
                    new[] { methodName, Symbols.MethodMissing }, versionSet
                );

                // Refinements are lexically scoped, so resolution has to consult the scope the call site was
                // compiled in - which IronRuby hands to every source-level call site as argument 0.  Only a
                // call of a name some refinement of the target class (or of an ancestor) has is: every other
                // site is built exactly as before and pays nothing - CRuby's REFINED method entry.
                if (args.Signature.HasScope && !args.Signature.IsSuperCall
                    && targetClass.Context.IsRefinementSensitive(targetClass, methodName)) {
                    refinements = args.Scope.GetActiveRefinements();
                }

                if (args.Signature.IsSuperCall) {
                    Debug.Assert(!args.Signature.IsVirtualCall && args.Signature.HasImplicitSelf);
                    method = targetClass.ResolveSuperMethodNoLock(methodName, targetClass).InvalidateSitesOnOverride();
                } else {
                    var options = args.Signature.IsVirtualCall ? MethodLookup.Virtual : MethodLookup.Default;
                    method = targetClass.ResolveMethodNoLock(methodName, visibilityContext, options, refinements).InvalidateSitesOnOverride();
                }

                if (!method.Found && method.Info == null && !args.Signature.IsVirtualCall) {
                    method = ResolveOwnClrMember(metaBuilder, args, targetClass, methodName, method);
                }

                if (!method.Found) {
                    if (publicOnly) {
                        args.ScopeRead = true;
                    }
                    methodMissing = targetClass.ResolveMethodMissingForSite(methodName, method.IncompatibleVisibility);
                } else {
                    methodMissing = null;
                }

                // the version the test was bound against resolves to this method:
                if (versionSet != null && versionSet.Count > 0) {
                    if (method.Found) {
                        versionSet.Member = method.Info;
                    } else if (method.Info == null && method.IncompatibleVisibility == RubyMethodVisibility.None) {
                        // the method_missing a call of an undefined name makes (see BindShared)
                        versionSet.Member = methodMissing;
                        versionSet.IsMissing = true;
                    } else {
                        versionSet.Member = null;
                    }
                }
            }

            // The rule is only valid while the caller's lexical activation is the one it was bound against.
            // RefinementActivation instances are interned by contents, so reference equality is enough, and
            // a `using' elsewhere - after which every scope recomputes its table - leaves the condition true.
            // A refinement that gains or loses a method re-versions the classes below the refined module.
            if (refinements != null) {
                metaBuilder.AddCondition(MakeRefinementGuard(args, refinements));
            }

            // Whenever the current self's class changes we need to invalidate the rule, if a protected method is being called.
            if (method.Info != null && method.Visibility == RubyMethodVisibility.Protected && visibilityContext.Class != null) {
                // We don't need to compare versions, just the class objects (super-class relationship cannot be changed).
                // Since we don't want to hold on a class object (to make it collectible) we compare references to the version handlers.
                metaBuilder.AddCondition(Ast.Equal(
                    Methods.GetSelfClassVersionHandle.OpCall(AstUtils.Convert(args.MetaScope.Expression, typeof(RubyScope))),
                    Ast.Constant(visibilityContext.Class.Version)
                ));
            }

            return method;
        }

        // The scopes whose refinements can be read from a method's record (see GetRecordScope).
        private enum RecordScope {
            None,
            Method,
            Block,
        }

        /// <summary>
        /// A method body's scope has the refinements its `def' recorded (RubyMethodBody.SetDefinitionRefinements) and nothing
        /// can `using' anything inside a method, so the scope of a method, and that of a block written directly in one, has
        /// the refinements the method's record holds - the block unless a refine block is running it, which
        /// RubyOps.RefineBlocksRunning says none is. Which of the two <paramref name="scope"/> is, if its refinements are
        /// <paramref name="refinements"/>; None for any other scope, whose refinements have to be walked to.
        /// </summary>
        private static RecordScope GetRecordScope(RubyScope/*!*/ scope, RefinementActivation/*!*/ refinements) {
            var methodScope = scope as RubyMethodScope;
            if (methodScope != null) {
                return ReferenceEquals(methodScope.DefinitionRefinementsValue, refinements) ? RecordScope.Method : RecordScope.None;
            }

            var blockScope = scope as RubyBlockScope;
            if (blockScope != null && RubyOps.RefineBlocksRunning == 0) {
                methodScope = blockScope.Parent as RubyMethodScope;
                if (methodScope != null && ReferenceEquals(methodScope.DefinitionRefinementsValue, refinements)) {
                    return RecordScope.Block;
                }
            }
            return RecordScope.None;
        }

        /// <summary>
        /// The test that the caller's lexical refinements are <paramref name="refinements"/>, the ones the call was
        /// resolved under. For the scope of a method or of a block in one it reads the method's record (GetRecordScope);
        /// any other scope walks.
        /// </summary>
        internal static Expression/*!*/ MakeRefinementGuard(CallArguments/*!*/ args, RefinementActivation/*!*/ refinements) {
            var scope = AstUtils.Convert(args.MetaScope.Expression, typeof(RubyScope));
            var expected = Ast.Constant(refinements, typeof(RefinementActivation));

            switch (GetRecordScope(args.Scope, refinements)) {
                case RecordScope.Method:
                    return Ast.AndAlso(
                        Ast.TypeIs(scope, typeof(RubyMethodScope)),
                        Ast.Equal(Ast.Call(Ast.Convert(scope, typeof(RubyMethodScope)), Methods.RubyMethodScope_get_DefinitionRefinementsValue), expected)
                    );

                case RecordScope.Block:
                    var parent = Ast.Call(scope, Methods.RubyScope_get_Parent);
                    return Ast.AndAlso(
                        Ast.AndAlso(
                            Ast.TypeIs(scope, typeof(RubyBlockScope)),
                            Ast.Equal(Ast.Field(null, Fields.RefineBlocksRunning), AstUtils.Constant(0))
                        ),
                        Ast.AndAlso(
                            Ast.TypeIs(parent, typeof(RubyMethodScope)),
                            Ast.Equal(Ast.Call(Ast.Convert(parent, typeof(RubyMethodScope)), Methods.RubyMethodScope_get_DefinitionRefinementsValue), expected)
                        )
                    );

                default:
                    return Ast.Equal(Methods.GetLexicalRefinements.OpCall(scope), expected);
            }
        }

        // Returns true if the call was bound (with success or failure), false if fallback should be performed.
        internal static bool BuildMethodMissingCall(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ methodName,
            RubyMemberInfo methodMissing, RubyMethodVisibility incompatibleVisibility, bool isSuperCall, bool defaultFallback) {

            switch (BindToKernelMethodMissing(metaBuilder, args, methodName, methodMissing, incompatibleVisibility, isSuperCall)) {
                case MethodMissingBinding.Custom:
                    Debug.Assert(!metaBuilder.Error);
                    // a user-defined method_missing may call super: record how the call was made, and whether it found a
                    // private or protected method
                    if (incompatibleVisibility == RubyMethodVisibility.Private || incompatibleVisibility == RubyMethodVisibility.Protected) {
                        metaBuilder.AddInitialization(Ast.Call(RubyOps.SetMethodMissingReasonVisibilityMethod,
                            AstUtils.Constant(args.Signature.HasImplicitSelf), AstUtils.Constant(defaultFallback && args.Signature.IsVariableCall),
                            AstUtils.Constant(incompatibleVisibility)
                        ));
                    } else {
                        metaBuilder.AddInitialization(Methods.SetMethodMissingReason.OpCall(
                            AstUtils.Constant(args.Signature.HasImplicitSelf), AstUtils.Constant(defaultFallback && args.Signature.IsVariableCall)
                        ));
                    }
                    methodMissing.BuildMethodMissingCall(metaBuilder, args, methodName);
                    return true;

                case MethodMissingBinding.Error:
                    // method_missing is defined in Kernel, error has been reported:
                    return true;

                case MethodMissingBinding.Fallback:
                    // A call of method_missing itself where it has been undefined: MRI's method_missing() goes to
                    // raise_method_missing with the call's own arguments, which is what BasicObject#method_missing
                    // does with them (`no method name given' for none, a name that is not a Symbol refused).
                    if (defaultFallback && methodName == Symbols.MethodMissing && incompatibleVisibility == RubyMethodVisibility.None) {
                        var basicMethodMissing = args.RubyContext.BasicObjectClass.GetMethod(Symbols.MethodMissing) as RubyLibraryMethodInfo;
                        if (basicMethodMissing != null) {
                            // MRI sets the reason afresh for this call (vm_call_method_missing): what an earlier
                            // call left for a custom method_missing that never reached super is not this one's
                            if (!isSuperCall) {
                                metaBuilder.AddInitialization(Methods.SetMethodMissingReason.OpCall(
                                    AstUtils.Constant(args.Signature.HasImplicitSelf), AstUtils.Constant(false)
                                ));
                            }
                            basicMethodMissing.BuildCall(metaBuilder, args, Symbols.MethodMissing);
                            return true;
                        }
                    }

                    // method_missing is defined in Kernel:
                    if (defaultFallback && args.Signature.IsVariableCall) {
                        metaBuilder.SetError(Methods.MakeUndefinedLocalOrMethodError.OpCall(
                            args.MetaContext.Expression,
                            AstUtils.Convert(args.MetaScope.Expression, typeof(RubyScope)),
                            AstUtils.Convert(args.TargetExpression, typeof(object)),
                            Ast.Constant(methodName)
                        ));
                        return true;
                    }
                    if (defaultFallback) {
                        metaBuilder.SetError(WithCallArguments(Methods.MakeMissingMethodError.OpCall(
                            args.MetaContext.Expression,
                            AstUtils.Convert(args.TargetExpression, typeof(object)),
                            Ast.Constant(methodName)
                        ), args));
                        return true;
                    }
                    return false;
            }
            throw Assert.Unreachable;
        }

        // Returns true if the call was bound (with success or failure), false if fallback should be performed.
        internal static bool BuildMethodMissingAccess(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ methodName,
            RubyMemberInfo methodMissing, RubyMethodVisibility incompatibleVisibility, bool isSuperCall, bool defaultFallback) {

            switch (BindToKernelMethodMissing(metaBuilder, args, methodName, methodMissing, incompatibleVisibility, isSuperCall)) {
                case MethodMissingBinding.Custom:
                    // we pretend we found the member and return a method that calls method_missing:
                    Debug.Assert(!metaBuilder.Error);
                    metaBuilder.Result = Methods.CreateBoundMissingMember.OpCall(
                        AstUtils.Convert(args.TargetExpression, typeof(object)),
                        Ast.Constant(methodMissing, typeof(RubyMemberInfo)),
                        Ast.Constant(methodName)
                    );
                    return true;

                case MethodMissingBinding.Error:
                    // method_missing is defined in Kernel, error has been reported:
                    return true;

                case MethodMissingBinding.Fallback:
                    // method_missing is defined in Kernel:
                    if (defaultFallback) {
                        metaBuilder.SetError(Methods.MakeMissingMemberError.OpCall(Ast.Constant(methodName)));
                        return true;
                    }
                    return false;
            }
            throw Assert.Unreachable;
        }

        // NoMethodError#args: MRI records the arguments of the call that found no (callable) method.
        private static Expression/*!*/ WithCallArguments(Expression/*!*/ error, CallArguments/*!*/ args) {
            var simple = new List<Expression>();
            foreach (var arg in args.GetSimpleArgumentExpressions()) {
                simple.Add(AstUtils.Box(arg));
            }

            var splat = args.GetSplattedArgumentExpression();
            var rhs = args.GetRhsArgumentExpression();

            return Methods.SetMissingMethodArguments.OpCall(
                error,
                Ast.NewArrayInit(typeof(object), simple),
                splat != null ? AstUtils.Convert(splat, typeof(IList)) : (Expression)AstUtils.Constant(null, typeof(IList)),
                rhs != null ? AstUtils.Box(rhs) : AstUtils.Constant(null),
                AstUtils.Constant(rhs != null),
                AstUtils.Constant(args.Signature.HasImplicitSelf)
            );
        }

        private enum MethodMissingBinding {
            Error,
            Fallback,
            Custom
        }

        private static MethodMissingBinding BindToKernelMethodMissing(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ methodName,
            RubyMemberInfo methodMissing, RubyMethodVisibility incompatibleVisibility, bool isSuperCall) {

            // TODO: better specialization of method_missing methods
            if (methodMissing == null ||
                methodMissing.DeclaringModule == methodMissing.Context.BasicObjectClass && methodMissing is RubyLibraryMethodInfo) {

                if (isSuperCall) {
                    metaBuilder.SetError(Methods.MakeMissingSuperException.OpCall(
                        AstUtils.Convert(args.MetaContext.Expression, typeof(RubyContext)),
                        AstUtils.Convert(args.TargetExpression, typeof(object)),
                        AstUtils.Constant(methodName)
                    ));
                } else if (incompatibleVisibility == RubyMethodVisibility.Private) {
                    metaBuilder.SetError(WithCallArguments(Methods.MakePrivateMethodCalledError.OpCall(
                        AstUtils.Convert(args.MetaContext.Expression, typeof(RubyContext)), args.TargetExpression, AstUtils.Constant(methodName)), args)
                    );
                } else if (incompatibleVisibility == RubyMethodVisibility.Protected) {
                    metaBuilder.SetError(WithCallArguments(Methods.MakeProtectedMethodCalledError.OpCall(
                        AstUtils.Convert(args.MetaContext.Expression, typeof(RubyContext)), args.TargetExpression, AstUtils.Constant(methodName)), args)
                    );
                } else {
                    return MethodMissingBinding.Fallback;
                }

                return MethodMissingBinding.Error;
            }

            return MethodMissingBinding.Custom;
        }

        protected override DynamicMetaObjectBinder GetInteropBinder(RubyContext/*!*/ context, IList<DynamicMetaObject/*!*/>/*!*/ args,
            out MethodInfo postConverter) {

            postConverter = null;
                    
            ExpressionType op;
            int opArity = RubyUtils.TryMapOperator(_methodName, out op);
            if (opArity == 1 + args.Count) {
                switch (opArity) {
                    case 1: return context.MetaBinderFactory.InteropUnaryOperation(op);
                    case 2: return context.MetaBinderFactory.InteropBinaryOperation(op);
                }
            }
       
            switch (_methodName) {
                case "new":
                    return context.MetaBinderFactory.InteropCreateInstance(new CallInfo(args.Count));

                case "call":
                    return context.MetaBinderFactory.InteropInvoke(new CallInfo(args.Count));

                case "to_s":
                    if (args.Count == 0) {
                        postConverter = Methods.ObjectToMutableString;
                        return context.MetaBinderFactory.InteropInvokeMember("ToString", new CallInfo(0));
                    }
                    goto default;

                case "to_str":
                    if (args.Count == 0) {
                        postConverter = Methods.StringToMutableString;
                        return context.MetaBinderFactory.InteropConvert(typeof(string), false);
                    }
                    goto default;

                case "[]":
                    // TODO: or invoke?
                    return context.MetaBinderFactory.InteropGetIndex(new CallInfo(args.Count));

                case "[]=":
                    return context.MetaBinderFactory.InteropSetIndex(new CallInfo(args.Count));

                default:
                    if (_methodName.LastCharacter() == '=') {
                        var baseName = _methodName.Substring(0, _methodName.Length - 1);
                        if (args.Count == 1) {
                            return context.MetaBinderFactory.InteropSetMember(baseName);
                        } else {
                            return context.MetaBinderFactory.InteropSetIndexedProperty(baseName, new CallInfo(args.Count));
                        }
                    } else {
                        return context.MetaBinderFactory.InteropInvokeMember(_methodName, new CallInfo(args.Count));
                    }
            }
        }
    }
}
