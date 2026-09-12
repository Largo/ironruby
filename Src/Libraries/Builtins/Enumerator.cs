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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    using EachSite = Func<CallSite, object, Proc, object>;

    internal interface IEnumerator {
        object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block);
    }

    /// <summary>
    /// A wrapper that provides "each" method for an arbitrary object. 
    /// A call to "each" on the instance of Enumerator is forwarded to a call to a method of a given name on the wrapped object.
    /// </summary>
    [RubyClass("Enumerator"), Includes(typeof(Enumerable))]
    public class Enumerator {
        internal sealed class Wrapper : IEnumerator {
            private readonly object _targetObject;
            private readonly string/*!*/ _targetName;
            private readonly object[]/*!*/ _targetArguments;

            public Wrapper(object targetObject, string targetName, object[] targetArguments) {
                _targetObject = targetObject;
                _targetName = targetName ?? "each";
                _targetArguments = targetArguments ?? ArrayUtils.EmptyObjects;
            }

            public object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block) {
                return KernelOps.SendMessageOpt(scope, block, _targetObject, _targetName, _targetArguments);
            }

            internal object TargetObject { get { return _targetObject; } }
            internal string/*!*/ TargetName { get { return _targetName; } }
            internal object[]/*!*/ TargetArguments { get { return _targetArguments; } }
        }

        internal sealed class DelegateWrapper : IEnumerator {
            private readonly Func<RubyScope, BlockParam, object>/*!*/ _each;

            public DelegateWrapper(Func<RubyScope, BlockParam, object>/*!*/ each) {
                _each = each;
            }

            public object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block) {
                return _each(scope, block);
            }
        }

        private IEnumerator/*!*/ _impl;

        /// <summary>
        /// What `Enumerator#size` is computed from. MRI attaches a size function to every
        /// enumerator a builtin returns; there is no equivalent slot here, so instead the receiver
        /// and a tag naming the arithmetic are recorded and the prelude does the sum. Null
        /// <see cref="SizeOp"/> means "unknown", which is the nil MRI returns for a generator.
        /// </summary>
        internal object SizeSource;
        internal string SizeOp;
        internal object SizeArg;

        public Enumerator()
            : this(null, null, null) {
        }

        internal Enumerator(IEnumerator/*!*/ impl) {
            Assert.NotNull(impl);
            _impl = impl;
        }

        internal Enumerator(Func<RubyScope, BlockParam, object>/*!*/ impl) {
            Assert.NotNull(impl);
            _impl = new DelegateWrapper(impl);
        }

        /// <summary>Same as the delegate constructor, but records what #size is computed from.</summary>
        internal static Enumerator/*!*/ Sized(Func<RubyScope, BlockParam, object>/*!*/ impl, object sizeSource, string/*!*/ sizeOp) {
            return Sized(impl, sizeSource, sizeOp, null);
        }

        internal static Enumerator/*!*/ Sized(Func<RubyScope, BlockParam, object>/*!*/ impl, object sizeSource, string/*!*/ sizeOp, object sizeArg) {
            return new Enumerator(impl) { SizeSource = sizeSource, SizeOp = sizeOp, SizeArg = sizeArg };
        }

        public Enumerator(object targetObject, string targetName, params object[] targetArguments) {
            Reinitialize(this, targetObject, targetName, targetArguments);
        }

        [RubyConstructor]
        public static Enumerator/*!*/ Create(RubyClass/*!*/ self, object targetObject, [DefaultProtocol, Optional]string targetName,
            params object[]/*!*/ targetArguments) {

            return Reinitialize(new Enumerator(), targetObject, targetName, targetArguments);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static Enumerator/*!*/ Reinitialize(Enumerator/*!*/ self, object targetObject, [DefaultProtocol, Optional]string targetName,
            params object[] targetArguments) {

            self._impl = new Wrapper(targetObject, targetName, targetArguments);
            return self;
        }

        /// <summary>
        /// What the prelude's #initialize calls. #initialize itself mixes an optional parameter
        /// with a params array, and that combination does not survive being called from Ruby -
        /// every argument lands in the params array and the method name defaults to "each". Three
        /// mandatory parameters bind unambiguously.
        /// </summary>
        [RubyMethod("__enum_init__", RubyMethodAttributes.PrivateInstance)]
        public static Enumerator/*!*/ InitializeFrom(Enumerator/*!*/ self, object targetObject,
            [DefaultProtocol, NotNull]string/*!*/ targetName, [NotNull]RubyArray/*!*/ targetArguments) {

            self._impl = new Wrapper(targetObject, targetName, targetArguments.ToArray());
            return self;
        }

        [RubyMethod("each")]
        public static object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block, Enumerator/*!*/ self) {
            return self._impl.Each(scope, block);
        }

        /// <summary>
        /// [receiver, :method, [args]] for an enumerator built from a method name, nil otherwise.
        /// The prelude needs it for #inspect and for the object #rewind forwards to.
        /// </summary>
        [RubyMethod("__enum_target__")]
        public static object GetTarget(RubyContext/*!*/ context, Enumerator/*!*/ self) {
            var wrapper = self._impl as Wrapper;
            if (wrapper == null) {
                return null;
            }
            var result = new RubyArray(3);
            result.Add(wrapper.TargetObject);
            result.Add(context.CreateSymbol(wrapper.TargetName, RubyEncoding.UTF8));
            result.Add(new RubyArray(wrapper.TargetArguments));
            return result;
        }

        /// <summary>
        /// [receiver, "op", arg] describing how to compute #size, or nil when it is unknown.
        /// </summary>
        [RubyMethod("__enum_size_info__")]
        public static object GetSizeInfo(RubyContext/*!*/ context, Enumerator/*!*/ self) {
            if (self.SizeOp == null) {
                return null;
            }
            var result = new RubyArray(3);
            result.Add(self.SizeSource);
            result.Add(context.CreateSymbol(self.SizeOp, RubyEncoding.UTF8));
            result.Add(self.SizeArg);
            return result;
        }

        // TODO: 1.9:
        // :each_with_index, :each_with_object, :with_index, :with_object, :next, :rewind
    }
}
