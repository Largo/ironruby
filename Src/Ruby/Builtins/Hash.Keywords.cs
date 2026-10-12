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

namespace IronRuby.Builtins {

    /// <summary>
    /// What binding keyword parameters needs from the hash a keyword call passed (see
    /// RubyOps.KeywordArgumentsMatch). The keys looked for are Symbols, and a Symbol is equal to
    /// nothing but itself, so an entry holds one exactly when its key is that very object. A
    /// keyword hash is small, and scanning a handful of entries for a reference is cheaper than
    /// hashing the key and probing for it; a larger hash is probed as usual.
    /// </summary>
    public partial class Hash {
        private const int IdentityScanLimit = 8;

        /// <summary>
        /// The value stored under <paramref name="key"/>, which must be a key that is equal only to
        /// itself (a Symbol). Entry <paramref name="hint"/> is tried first: callers mostly pass
        /// keywords in the order they are declared in.
        /// </summary>
        internal bool TryGetValueByIdentity(object/*!*/ key, int hint, out object value) {
            Entry[] entries = _entries;
            if (entries != null && _liveCount <= IdentityScanLimit) {
                int count = _count;
                if ((uint)hint < (uint)count && entries[hint].Key == key && entries[hint].Next != TombstoneNext) {
                    value = entries[hint].Value;
                    return true;
                }
                for (int i = _start; i < count; i++) {
                    if (entries[i].Key == key && entries[i].Next != TombstoneNext) {
                        value = entries[i].Value;
                        return true;
                    }
                }
                value = null;
                return false;
            }
            return TryGetValue(key, out value);
        }

        /// <summary>
        /// True when the first <paramref name="requiredCount"/> of <paramref name="names"/> are all keys
        /// of this hash and, unless <paramref name="acceptsOthers"/>, it has no key but those names.
        /// The names are compared by identity, see <see cref="TryGetValueByIdentity"/>.
        /// </summary>
        internal bool HasKeywords(object/*!*/[]/*!*/ names, int requiredCount, bool acceptsOthers) {
            int live = _liveCount;
            if (live == 0) {
                return requiredCount == 0;
            }

            if (live <= IdentityScanLimit && names.Length <= 64) {
                Entry[] entries = _entries;
                int count = _count;
                ulong seen = 0;
                for (int i = _start; i < count; i++) {
                    if (entries[i].Next == TombstoneNext) {
                        continue;
                    }
                    object key = entries[i].Key;
                    int j = 0;
                    while (j < names.Length && names[j] != key) {
                        j++;
                    }
                    if (j < names.Length) {
                        seen |= 1UL << j;
                    } else if (!acceptsOthers) {
                        return false;
                    }
                }
                ulong required = (requiredCount >= 64) ? ulong.MaxValue : (1UL << requiredCount) - 1;
                return (seen & required) == required;
            }

            int found = 0;
            for (int i = 0; i < names.Length; i++) {
                if (ContainsKey(names[i])) {
                    found++;
                } else if (i < requiredCount) {
                    return false;
                }
            }
            return acceptsOthers || found == live;
        }
    }
}
