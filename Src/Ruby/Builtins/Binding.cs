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

using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    public sealed class Binding : IDuplicable {
        private readonly RubyScope/*!*/ _localScope;
        private readonly object _self;

        /// <summary>
        /// Local scope captured by the binding.
        /// </summary>
        public RubyScope/*!*/ LocalScope {
            get { return _localScope; }
        }

        /// <summary>
        /// Self object captured by the binding. Can be different from LocalScope.SelfObject in MRI 1.8.
        /// </summary>
        public object SelfObject {
            get { return _self; }
        }

        public Binding(RubyScope/*!*/ localScope) 
            : this(localScope, localScope.SelfObject) {
        }

        public Binding(RubyScope/*!*/ localScope, object self) {
            Assert.NotNull(localScope);
            _localScope = localScope;
            _self = self;
        }

        /// <summary>
        /// MRI's Binding#dup keeps every variable that already exists shared with the original and
        /// lets the two diverge only over variables defined afterwards, so the copy gets a scope
        /// nested inside this one rather than this very scope or a snapshot of it.
        /// </summary>
        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = new Binding(new RubyBindingCopyScope(_localScope, _self), _self);
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }
    }
}
