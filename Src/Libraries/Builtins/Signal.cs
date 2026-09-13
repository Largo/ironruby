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
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    [RubyModule("Signal")]
    public static class Signal {
        #region Private Instance & Singleton Methods

        [RubyMethod("list", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ List(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            Hash result = new Hash(context);
            result.Add(MutableString.CreateAscii("EXIT"), ScriptingRuntimeHelpers.Int32ToObject(0));
            foreach (var entry in PosixSignals.Numbers) {
                result.Add(MutableString.CreateAscii(entry.Key), ScriptingRuntimeHelpers.Int32ToObject(entry.Value));
            }
            return result;
        }

        [RubyMethod("signame", RubyMethodAttributes.PublicSingleton)]
        public static MutableString Signame(RubyModule/*!*/ self, [DefaultProtocol]int number) {
            string name = PosixSignals.ToName(number);
            return (name != null) ? MutableString.CreateAscii(name) : null;
        }

        /// <summary>
        /// Installs a handler for a signal and returns the one it replaced. The handler cancels the
        /// platform's default disposition, so trapping SIGTERM really does stop SIGTERM from ending
        /// the process.
        /// </summary>
        [RubyMethod("trap", RubyMethodAttributes.PublicSingleton)]
        public static object Trap(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage,
            RubyContext/*!*/ context,
            object self,
            object signalId,
            object command) {

            int number = PosixSignals.ToNumber(signalId);
            if (number == SignalInterrupt) {
                var proc = command as Proc;
                context.InterruptSignalHandler = (proc != null) ? new Action(() => proc.Call(null)) : null;
            }

            // MRI takes a block, a Proc, a Method - anything that answers #call - so dispatch
            // dynamically rather than insisting on a Proc.
            var site = callStorage.GetCallSite("call", 1);
            return PosixSignals.Trap(number, command,
                signalNumber => site.Target(site, command, ScriptingRuntimeHelpers.Int32ToObject(signalNumber)));
        }

        [RubyMethod("trap", RubyMethodAttributes.PublicSingleton)]
        public static object Trap(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage,
            RubyContext/*!*/ context,
            BlockParam block,
            object self,
            object signalId) {

            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }
            return Trap(callStorage, context, self, signalId, block.Proc);
        }

        private const int SignalInterrupt = 2;

        #endregion
    }
}
