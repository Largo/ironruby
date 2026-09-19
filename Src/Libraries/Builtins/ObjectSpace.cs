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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using System.Runtime.CompilerServices;

namespace IronRuby.Builtins {
    [RubyModule("ObjectSpace")]
    public static class ObjectSpace {
        #region define_finalizer, undefine_finalizer

        /// <summary>
        /// The finalizers of one object. It hangs off the object, so the CLR finalizes it once the object
        /// is gone; the context runs whatever is left at exit. Each finalizer is called with the object's id.
        /// </summary>
        private sealed class FinalizerInvoker : IExitFinalizer, IPerObjectState {
            public const string InstanceVariableName = "<FINALIZER>";

            private readonly RubyContext/*!*/ _context;
            private readonly CallSite<Func<CallSite, object, object, object>>/*!*/ _callSite;
            private readonly List<object>/*!*/ _finalizers = new List<object>();
            private readonly object _objectId;
            private int _ran;

            public FinalizerInvoker(RubyContext/*!*/ context, CallSite<Func<CallSite, object, object, object>>/*!*/ callSite, object objectId) {
                Assert.NotNull(context, callSite);
                _context = context;
                _callSite = callSite;
                _objectId = objectId;
            }

            public List<object>/*!*/ Finalizers {
                get { return _finalizers; }
            }

            // dup and clone copy the finalizers: they run once for each object, with its own id
            public object CopyFor(object copy) {
                var result = new FinalizerInvoker(_context, _callSite, ClrInteger.Narrow(RubyUtils.GetObjectId(_context, copy)));
                lock (_finalizers) {
                    result._finalizers.AddRange(_finalizers);
                }
                _context.RegisterExitFinalizer(result);
                return result;
            }

            ~FinalizerInvoker() {
                Run();
            }

            public void RunAtExit() {
                Run();
                GC.SuppressFinalize(this);
            }

            public void Cancel() {
                Interlocked.Exchange(ref _ran, 1);
                GC.SuppressFinalize(this);
            }

            private void Run() {
                if (Interlocked.Exchange(ref _ran, 1) != 0) {
                    return;
                }

                object[] finalizers;
                lock (_finalizers) {
                    finalizers = _finalizers.ToArray();
                }
                foreach (var finalizer in finalizers) {
                    try {
                        _callSite.Target(_callSite, finalizer, _objectId);
                    } catch (SystemExit) {
                        // `exit' in a finalizer ends that finalizer, not the others
                    } catch (Exception e) {
                        _context.ReportFinalizerException(finalizer, e);
                    }
                }
            }
        }

        /// <summary>
        /// The finalizer may be given as a block instead of an argument.
        /// </summary>
        [RubyMethod("define_finalizer", RubyMethodAttributes.PublicSingleton)]
        public static object DefineFinalizer(RespondToStorage/*!*/ respondTo, BinaryOpStorage/*!*/ call, BinaryOpStorage/*!*/ equals,
            [NotNull]BlockParam/*!*/ block, RubyModule/*!*/ self, object obj) {

            return DefineFinalizer(respondTo, call, equals, self, obj, block.Proc);
        }

        [RubyMethod("define_finalizer", RubyMethodAttributes.PublicSingleton)]
        public static object DefineFinalizer(RespondToStorage/*!*/ respondTo, BinaryOpStorage/*!*/ call, BinaryOpStorage/*!*/ equals,
            RubyModule/*!*/ self, object obj, object finalizer) {

            var context = respondTo.Context;
            if (!Protocols.RespondTo(respondTo, finalizer, "call")) {
                throw RubyExceptions.CreateArgumentError("finalizer should be callable (respond to :call)");
            }
            // an immediate - nil, true, a Symbol, a small Integer - is never collected
            if (obj == null || obj is bool || obj is RubySymbol || !RubyUtils.HasObjectState(obj)) {
                throw RubyExceptions.CreateArgumentError("cannot define finalizer for {0}", context.GetClassDisplayName(obj));
            }
            if (context.IsObjectFrozen(obj)) {
                throw RubyExceptions.CreateObjectFrozenError(context, obj);
            }

            // A finalizer that holds on to the object keeps it alive for good, so it only runs at exit.
            if (ReferencesObject(finalizer, obj)) {
                context.ReportWarning("finalizer references object to be finalized");
            }

            FinalizerInvoker invoker;
            object existing;
            if (context.TryGetInstanceVariable(obj, FinalizerInvoker.InstanceVariableName, out existing) && existing is FinalizerInvoker) {
                invoker = (FinalizerInvoker)existing;
            } else {
                invoker = new FinalizerInvoker(context, call.GetCallSite("call"),
                    // the id as #object_id answers it - an Integer, which a Hash keyed by object_id can find
                    ClrInteger.Narrow(RubyUtils.GetObjectId(context, obj)));
                context.SetInstanceVariable(obj, FinalizerInvoker.InstanceVariableName, invoker);
                context.RegisterExitFinalizer(invoker);
            }

            // the same finalizer - by #== - is defined once, and answering for it is the one defined first
            lock (invoker.Finalizers) {
                foreach (var defined in invoker.Finalizers) {
                    if (Protocols.IsEqual(equals, defined, finalizer)) {
                        finalizer = defined;
                        goto done;
                    }
                }
                invoker.Finalizers.Add(finalizer);
            }

          done:
            RubyArray result = new RubyArray(2);
            result.Add(0);
            result.Add(finalizer);
            return result;
        }

        private static bool ReferencesObject(object finalizer, object obj) {
            if (ReferenceEquals(finalizer, obj)) {
                return true;
            }
            var proc = finalizer as Proc;
            if (proc != null) {
                return ReferenceEquals(proc.Self, obj);
            }
            var method = finalizer as RubyMethod;
            if (method != null) {
                return ReferenceEquals(method.Target, obj);
            }
            return false;
        }

        [RubyMethod("undefine_finalizer", RubyMethodAttributes.PublicSingleton)]
        public static object UndefineFinalizer(RubyContext/*!*/ context, RubyModule/*!*/ self, object obj) {
            if (context.IsObjectFrozen(obj)) {
                throw RubyExceptions.CreateObjectFrozenError(context, obj);
            }

            object invokerObj;
            if (context.TryRemoveInstanceVariable(obj, FinalizerInvoker.InstanceVariableName, out invokerObj)) {
                var invoker = invokerObj as FinalizerInvoker;
                if (invoker != null) {
                    invoker.Cancel();
                }
            }
            return obj;
        }

        #endregion

        // MRI walks its heap. The CLR cannot, so IronRuby records what each_object can find as it is
        // created (RubyContext.ObjectSpaceModules/Objects): modules and classes, and - with
        // -X:ObjectSpace - the instances of Ruby classes deriving from Object or BasicObject. What is
        // not recorded - instances of the builtin types (String, Array, Exception, ...), or any object
        // without -X:ObjectSpace - is asked for with an error rather than an empty walk, as in JRuby.
        [RubyMethod("each_object", RubyMethodAttributes.PublicSingleton)]
        public static Enumerator/*!*/ GetEachObjectEnumerator(RubyModule/*!*/ self, [Optional]RubyModule theModule) {
            return new Enumerator((_, block) => EachObject(block, self, theModule));
        }

        [RubyMethod("each_object", RubyMethodAttributes.PublicSingleton)]
        public static object EachObject([NotNull]BlockParam/*!*/ block, RubyModule/*!*/ self, [Optional]RubyModule theModule) {
            var context = self.Context;
            var objects = context.GetObjectSpaceObjects();
            if (objects == null) {
                if (theModule == null || !OnlyModulesAreInstances(theModule)) {
                    throw RubyExceptions.CreateRuntimeError(
                        "each_object only supported for modules and classes unless ObjectSpace is enabled (-X:ObjectSpace)"
                    );
                }
            } else if (theModule != null && !CanFindInstances(theModule)) {
                throw RubyExceptions.CreateRuntimeError(
                    "each_object only supported for modules, classes and objects of classes derived from Object or BasicObject"
                );
            }

            int matches = 0;
            foreach (var list in new[] { context.GetObjectSpaceModules(), objects }) {
                if (list == null) {
                    continue;
                }
                foreach (object obj in list) {
                    var module = obj as RubyModule;
                    if (module != null && IsHidden(module)) {
                        continue;
                    }

                    if (theModule == null || context.IsKindOf(obj, theModule)) {
                        matches++;

                        object result;
                        if (block.Yield(obj, out result)) {
                            return result;
                        }
                    }
                }
            }
            return matches;
        }

        // The dummy singleton class that ends a chain of singleton classes has no MRI counterpart. And
        // MRI hides the singleton class of a class until it has a singleton class of its own
        // (rb_singleton_class_internal_p) - it gets one when Kernel#singleton_class exposes it; IronRuby
        // creates the singleton class of every class eagerly, with a dummy one after it.
        private static bool IsHidden(RubyModule/*!*/ module) {
            if (module.IsDummySingletonClass) {
                return true;
            }
            var cls = module as RubyClass;
            return cls != null && cls.IsSingletonClass && cls.SingletonClassOf is RubyClass && cls.ImmediateClass.IsDummySingletonClass;
        }

        // Module, Class, singleton classes of modules - and modules, which may extend a module.
        private static bool OnlyModulesAreInstances(RubyModule/*!*/ module) {
            var cls = module as RubyClass;
            return cls == null || cls.HasAncestor(module.Context.ModuleClass);
        }

        private static bool CanFindInstances(RubyModule/*!*/ module) {
            if (OnlyModulesAreInstances(module)) {
                return true;
            }
            var cls = (RubyClass)module;
            while (cls.IsSingletonClass) {
                cls = cls.SuperClass;
            }
            Type type = cls.GetUnderlyingSystemType();
            return type == typeof(object) || typeof(RubyObject).IsAssignableFrom(type);
        }

        // Deprecated in 4.0 but still working. Only an id MRI computes (nil, true, false, an Integer)
        // or one of the objects each_object can find (with -X:ObjectSpace for anything but modules)
        // is turned back into its object: IronRuby keeps no table from ids to the other objects.
        [RubyMethod("_id2ref", RubyMethodAttributes.PublicSingleton)]
        public static object IdToReference(RubyModule/*!*/ self, [DefaultProtocol]IntegerValue id) {
            var context = self.Context;
            context.ReportDeprecationWarning("ObjectSpace._id2ref is deprecated");

            if (id.IsFixnum) {
                int value = id.Fixnum;
                if (value == RubyUtils.NilObjectId) {
                    return null;
                } else if (value == RubyUtils.TrueObjectId) {
                    return ScriptingRuntimeHelpers.True;
                } else if (value == RubyUtils.FalseObjectId) {
                    return ScriptingRuntimeHelpers.False;
                }

                foreach (var list in new[] { context.GetObjectSpaceModules(), context.GetObjectSpaceObjects() }) {
                    if (list == null) {
                        continue;
                    }
                    foreach (object obj in list) {
                        // an object whose id has not been asked for yet has no instance data, and it is not created here
                        var rubyObject = obj as IRubyObject;
                        if (rubyObject != null && rubyObject.TryGetInstanceData() != null && RubyUtils.GetObjectId(context, obj) == value) {
                            return obj;
                        }
                    }
                }

                // The ids IronRuby hands out to objects are consecutive, so an odd one is not
                // necessarily an Integer's (2n + 1) as in MRI: the objects were looked at first.
                if ((value & 1) != 0) {
                    return ScriptingRuntimeHelpers.Int32ToObject(value >> 1);
                }
            }

            throw RubyExceptions.CreateRangeError(String.Format("\"{0}\" is not an id value", id.IsFixnum ? id.Fixnum.ToString() : id.Bignum.ToString()));
        }

        // GC.start's keywords (full_mark:, immediate_sweep:) have no CLR counterpart and are ignored
        [RubyMethod("garbage_collect", RubyMethodAttributes.PublicSingleton)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods")]
        public static void GarbageCollect(RubyModule/*!*/ self, [Optional]IDictionary<object, object> options) {
            GC.Collect();
        }
    }
}
