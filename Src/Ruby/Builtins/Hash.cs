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
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// TODO: ordered dictionary
    /// TODO: all operations should check frozen state!
    /// 
    /// Dictionary inherits from Object, mixes in Enumerable.
    /// Ruby hash is a Dictionary{object, object}, but it adds default value/proc
    /// </summary>
    public partial class Hash : Dictionary<object, object>, IRubyObjectState, IDuplicable {

        // The default value can be a Proc that we should *return*, and that is different
        // from the default value being a Proc that we should *call*, hence two variables
        private Proc _defaultProc;
        private object _defaultValue;

        private uint _flags;
        private const uint IsFrozenFlag = 1;
        private const uint IsTaintedFlag = 2;
        private const uint IsUntrustedFlag = 4;
        private const uint IsKeywordArgumentsFlag = 8;
        private const uint IsRuby2KeywordsHashFlag = 16;

        // Hash#compare_by_identity has to swap the comparer of an *existing* dictionary, which
        // Dictionary<,> offers no API for; the field is patched directly and the entries rehashed.
        private static readonly FieldInfo _DictionaryComparerField =
            typeof(Dictionary<object, object>).GetField("_comparer", BindingFlags.NonPublic | BindingFlags.Instance);

        private bool _comparesByIdentity;

        /// <summary>
        /// True if Hash#compare_by_identity has been called on this hash.
        /// </summary>
        public bool ComparesByIdentity {
            get { return _comparesByIdentity; }
        }

        /// <summary>
        /// Switches the hash over to identity comparison and rehashes the existing entries.
        /// </summary>
        public Hash/*!*/ CompareByIdentity() {
            return SetComparesByIdentity(true, null);
        }

        /// <summary>
        /// Turns identity comparison on or off. <paramref name="defaultComparer"/> is only needed when
        /// turning it off (Hash#replace with a non-identity argument is the only caller that does).
        /// </summary>
        public Hash/*!*/ SetComparesByIdentity(bool value, IEqualityComparer<object> defaultComparer) {
            RequireNotFrozen();
            if (_comparesByIdentity == value) {
                return this;
            }
            IEqualityComparer<object> comparer = value ? IdentityComparer.Instance : defaultComparer;
            if (comparer == null || _DictionaryComparerField == null) {
                throw new NotSupportedException("Hash#compare_by_identity is not available: Dictionary<,> layout changed");
            }

            var entries = new KeyValuePair<object, object>[Count];
            ((ICollection<KeyValuePair<object, object>>)this).CopyTo(entries, 0);
            Clear();
            _DictionaryComparerField.SetValue(this, comparer);
            _comparesByIdentity = value;
            foreach (var entry in entries) {
                this[entry.Key] = entry.Value;
            }
            return this;
        }

        /// <summary>
        /// #equal? semantics without ever dispatching to Ruby. Immediate values still compare by
        /// value, matching CRuby, where they have no separate identity.
        /// </summary>
        internal sealed class IdentityComparer : IEqualityComparer<object> {
            internal static readonly IdentityComparer/*!*/ Instance = new IdentityComparer();

            private IdentityComparer() {
            }

            bool IEqualityComparer<object>.Equals(object x, object y) {
                if (ReferenceEquals(x, y)) {
                    return true;
                }
                if (x is int) {
                    return y is int && (int)x == (int)y;
                }
                if (x is bool) {
                    return y is bool && (bool)x == (bool)y;
                }
                return false;
            }

            int IEqualityComparer<object>.GetHashCode(object obj) {
                if (obj is int) {
                    return (int)obj;
                }
                if (obj is bool) {
                    return (bool)obj ? 1 : 0;
                }
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        public Proc DefaultProc { 
            get { return _defaultProc; } 
            set {
                Mutate();
                _defaultProc = value;
            }
        }

        public object DefaultValue { 
            get { return _defaultValue; } 
            set {
                Mutate();
                _defaultValue = value;
            }
        }

        #region Construction

        // Dictionary has no constructor of its own to chain through, so every constructor here
        // records the new hash; off unless the objspace library asked for it.
        private void Created() {
            if (ObjectTracking.Enabled) {
                ObjectTracking.Track(this);
            }
        }

        public Hash(RubyContext/*!*/ context)
            : base(context.EqualityComparer) {
            Created();
        }

        public Hash(IEqualityComparer<object>/*!*/ comparer)
            : base(comparer) {
            Created();
        }

        public Hash(EqualityComparer/*!*/ comparer, Proc defaultProc, object defaultValue)
            : base(comparer) {
            _defaultValue = defaultValue;
            _defaultProc = defaultProc;
            Created();
        }

        public Hash(EqualityComparer/*!*/ comparer, int capacity)
            : base(capacity, comparer) {
            Created();
        }

        public Hash(IDictionary<object, object>/*!*/ dictionary)
            : base(dictionary) {
            Created();
        }

        public Hash(IDictionary<object, object>/*!*/ dictionary, EqualityComparer/*!*/ comparer)
            : base(dictionary, comparer) {
            Created();
        }

        public Hash(Hash/*!*/ hash)
            : base(hash, hash.Comparer) {
            _defaultProc = hash._defaultProc;
            _defaultValue = hash.DefaultValue;
            _comparesByIdentity = hash._comparesByIdentity;
            Created();
        }

        /// <summary>
        /// Creates a blank instance of a RubyArray or its subclass given the Ruby class object.
        /// </summary>
        public static Hash/*!*/ CreateInstance(RubyClass/*!*/ rubyClass) {
            return (rubyClass.GetUnderlyingSystemType() == typeof(Hash)) ? new Hash(rubyClass.Context) : new Hash.Subclass(rubyClass);
        }

        /// <summary>
        /// Creates an empty instance.
        /// Doesn't copy instance data.
        /// Preserves the class of the Hash.
        /// </summary>
        protected virtual Hash/*!*/ CreateInstance() {
            return new Hash(Comparer) { _comparesByIdentity = _comparesByIdentity };
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = CreateInstance();
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        #endregion

        #region Flags

        public void RequireNotFrozen() {
            if ((_flags & IsFrozenFlag) != 0) {
                throw RubyExceptions.CreateObjectFrozenError("Hash");
            }
        }

        private void Mutate() {
            RequireNotFrozen();
        }

        public bool IsTainted {
            get {
                return (_flags & IsTaintedFlag) != 0;
            }
            set {
                Mutate();
                _flags = (_flags & ~IsTaintedFlag) | (value ? IsTaintedFlag : 0);
            }
        }

        public bool IsUntrusted {
            get {
                return (_flags & IsUntrustedFlag) != 0;
            }
            set {
                Mutate();
                _flags = (_flags & ~IsUntrustedFlag) | (value ? IsUntrustedFlag : 0);
            }
        }

        public bool IsFrozen {
            get {
                return (_flags & IsFrozenFlag) != 0;
            }
        }

        /// <summary>
        /// True for a hash that a call site built out of keyword-argument syntax - `f(a: 1)`,
        /// `f("a" =&gt; 1, b: 2)` or `f(**h)`. Keyword arguments have no slot of their own in this
        /// calling convention: they travel as a trailing positional Hash, and this is what tells
        /// the callee whether the caller wrote keywords or passed a Hash (Ruby 3 separates the
        /// two). Such a hash is always freshly made by the call site, and the flag is not copied
        /// by #dup, so it never shows up on a hash user code holds on to.
        /// </summary>
        public bool IsKeywordArguments {
            get { return (_flags & IsKeywordArgumentsFlag) != 0; }
            set { _flags = (_flags & ~IsKeywordArgumentsFlag) | (value ? IsKeywordArgumentsFlag : 0); }
        }

        /// <summary>
        /// True for the hash a ruby2_keywords method's rest parameter is holding: it arrived as
        /// keyword arguments, and splatting the array it sits in has to send it on as keyword
        /// arguments again. Unlike <see cref="IsKeywordArguments"/>, which is a property of one
        /// call in progress, this one belongs to the object and outlives the call - which is why
        /// Hash.ruby2_keywords_hash? can report it and why the hash is copied before it is set.
        /// </summary>
        public bool IsRuby2KeywordsHash {
            get { return (_flags & IsRuby2KeywordsHashFlag) != 0; }
            set { _flags = (_flags & ~IsRuby2KeywordsHashFlag) | (value ? IsRuby2KeywordsHashFlag : 0); }
        }

        void IRubyObjectState.Freeze() {
            Freeze();
        }

        public Hash/*!*/ Freeze() {
            _flags |= IsFrozenFlag;
            return this;
        }

        #endregion
    }
}
