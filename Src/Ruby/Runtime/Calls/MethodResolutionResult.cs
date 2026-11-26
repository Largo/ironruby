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

using System.Collections.Generic;
using IronRuby.Builtins;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime.Calls {
    // Method doesn't exist -> info == null, Found == false
    // Method exists but is not visible -> info != null, visible == false, Found == false
    // Method exists and is visible -> info != null, visible == true, Found == true
    public struct MethodResolutionResult {
        public static readonly MethodResolutionResult NotFound = new MethodResolutionResult();

        private readonly RubyMemberInfo _info;
        private readonly RubyModule _owner;
        private readonly bool _visible;
        private readonly RubyMethodVisibility _visibility;

        public RubyMemberInfo Info { get { return _info; } }

        /// <summary>
        /// The visibility the lookup found, which is not always Info's: `protected :m' or `private :m' in a subclass
        /// leaves an entry there that only forwards to the inherited m, and the resolution follows it to a method whose
        /// own visibility is still public.
        /// </summary>
        public RubyMethodVisibility Visibility { get { return _visibility; } }
        public RubyModule Owner { get { return _owner; } }

        public bool Found {
            get { return _info != null && _visible; }
        }

        public RubyMethodVisibility IncompatibleVisibility {
            get { return (_info == null || _visible) ? RubyMethodVisibility.None : _visibility; }
        }

        public MethodResolutionResult(RubyMemberInfo/*!*/ info, RubyModule/*!*/ owner, bool visible)
            : this(info, owner, visible, info.Visibility) {
        }

        private MethodResolutionResult(RubyMemberInfo/*!*/ info, RubyModule/*!*/ owner, bool visible, RubyMethodVisibility visibility) {
            Assert.NotNull(info, owner);
            _info = info;
            _owner = owner;
            _visible = visible;
            _visibility = visibility;
        }

        /// <summary>This result, as reached through a forwarder of the given visibility.</summary>
        internal MethodResolutionResult WithVisibility(RubyMethodVisibility visibility) {
            return (_info == null) ? this : new MethodResolutionResult(_info, _owner, _visible, visibility);
        }

        internal MethodResolutionResult InvalidateSitesOnOverride() {
            // Mark method as used in DS regardless of visibility. Even if the method cannot be invoked due to its visibility
            // we need to invalidate the cached rule whenever the method is overridden or removed.
            if (_info != null) {
                _info.SetInvalidateSitesOnOverride();
                _owner.OwnedMethodCachedInSite();
            }
            return this;
        }

        // Only call on method missing:
        internal MethodResolutionResult InvalidateSitesOnMissingMethodAddition(string/*!*/ methodName, RubyContext/*!*/ context) {
            // mark that methodName is used in method_missing dynamic site:
            if (context.MissingMethodsCachedInSites == null) {
                context.MissingMethodsCachedInSites = new HashSet<string>();
            }
            context.MissingMethodsCachedInSites.Add(methodName);
            return this;
        }
    }
}
