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
                var result = new FinalizerInvoker(_context, _callSite, RubyUtils.GetObjectId(_context, copy));
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
                invoker = new FinalizerInvoker(context, call.GetCallSite("call"), RubyUtils.GetObjectId(context, obj));
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

        [RubyMethod("each_object", RubyMethodAttributes.PublicSingleton)]
        public static Enumerator/*!*/ GetEachObjectEnumerator(RubyModule/*!*/ self, [NotNull]RubyClass/*!*/ theClass) {
            return new Enumerator((_, block) => EachObject(block, self, theClass));
        }

        [RubyMethod("each_object", RubyMethodAttributes.PublicSingleton)]
        public static object EachObject([NotNull]BlockParam/*!*/ block, RubyModule/*!*/ self, [NotNull]RubyClass/*!*/ theClass) {
            if (!theClass.HasAncestor(self.Context.ModuleClass)) {
                throw RubyExceptions.CreateRuntimeError("each_object only supported for objects of type Class or Module");
            }

            int matches = 0;
            List<RubyModule> visitedModules = new List<RubyModule>();
            Stack<RubyModule> pendingModules = new Stack<RubyModule>();
            pendingModules.Push(theClass.Context.ObjectClass);

            while (pendingModules.Count > 0) {
                RubyModule next = pendingModules.Pop();
                visitedModules.Add(next);

                if (theClass.Context.IsKindOf(next, theClass)) {
                    matches++;

                    object result;
                    if (block.Yield(next, out result)) {
                        return result;
                    }
                }

                using (theClass.Context.ClassHierarchyLocker()) {
                    next.EnumerateConstants(delegate(RubyModule module, string name, object value) {
                        RubyModule constAsModule = value as RubyModule;
                        if (constAsModule != null && !visitedModules.Contains(constAsModule)) {
                            pendingModules.Push(constAsModule);
                        }
                        return false;
                    });
                }
            }
            return matches;
        }

        // GC.start's keywords (full_mark:, immediate_sweep:) have no CLR counterpart and are ignored
        [RubyMethod("garbage_collect", RubyMethodAttributes.PublicSingleton)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods")]
        public static void GarbageCollect(RubyModule/*!*/ self, [Optional]IDictionary<object, object> options) {
            GC.Collect();
        }
    }
}
