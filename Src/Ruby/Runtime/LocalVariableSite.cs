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

namespace IronRuby.Runtime {
    /// <summary>
    /// A read or write of a local that eval'd code knows by name only - the binding's, or one an earlier eval defined
    /// (LocalVariable with no lexical depth, RubyOps.GetLocalVariable / SetLocalVariable). It remembers where the name
    /// resolved from the last scope it ran in: a slot of the locals of the scope that declares it, or the cell of one
    /// an eval or Binding defined (RubyScope.TryResolveLocal). The compiled code of a text is shared by every eval of
    /// it (EvalCodeCache) and so by every binding and thread, so the memory is one immutable object, replaced whole,
    /// and good only while no scope of the runtime has gained a dynamic local since (RubyContext.LocalVariableSitesVersion)
    /// and for the same scope, or one that reaches the same holder through scopes of the same bodies (the next run of a
    /// block): a new dynamic local is the one thing that can make a name resolve elsewhere - a scope's static locals are
    /// its body's compiled table, its parent never changes, and a cell stays the local's for the scope's life.
    /// </summary>
    public sealed class LocalVariableSite {
        internal readonly string/*!*/ Name;
        private Resolution _last;

        internal LocalVariableSite(string/*!*/ name) {
            Name = name;
        }

        internal sealed class Resolution {
            internal readonly RubyScope/*!*/ Start;
            internal readonly int Version;
            private readonly RubyScope/*!*/ _holder;
            private readonly int _index;
            private readonly StrongBox<object> _cell;
            // the locals tables of the scopes the lookup passed on its way from Start to the holder, nearest first
            private readonly string[]/*!*/[]/*!*/ _passed;

            internal Resolution(RubyScope/*!*/ start, int version, RubyScope/*!*/ holder, int index, StrongBox<object> cell,
                string[]/*!*/[]/*!*/ passed) {
                Start = start;
                Version = version;
                _holder = holder;
                _index = index;
                _cell = cell;
                _passed = passed;
            }

            // Another scope that reaches the same holder through scopes of the same bodies: the next run of a block.
            internal bool HoldsFor(RubyScope/*!*/ scope) {
                return scope.ResolvesThroughSameChain(_passed, _holder);
            }

            internal object Read() {
                return (_cell != null) ? _cell.Value : _holder.GetLocalSlotValueForSite(_index);
            }

            internal void Write(object value) {
                if (_cell != null) {
                    _cell.Value = value;
                } else {
                    _holder.SetLocalSlotValueForSite(_index, value);
                }
            }
        }

        // The resolution for this scope, if the last one still holds; else a new one, remembered, or null if the name does
        // not resolve (then nothing is remembered: the call is made by name, as before).
        internal Resolution Resolve(RubyScope/*!*/ scope) {
            var last = _last;
            var context = scope.RubyContext;
            int version = context.LocalVariableSitesVersion;
            if (last != null && last.Version == version && (last.Start == scope || last.HoldsFor(scope))) {
                return last;
            }
            var resolution = scope.TryResolveLocal(Name, version);
            if (resolution != null) {
                _last = resolution;
            }
            return resolution;
        }
    }
}
