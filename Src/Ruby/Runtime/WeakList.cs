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
using System.Collections;

namespace IronRuby.Runtime {
    internal sealed class WeakList<T> : IEnumerable<T>
        where T : class {
        private readonly List<WeakReference/*!*/> _list = new List<WeakReference>();

        // The same references, for Contains: a module that is extended into many objects gets a new singleton
        // class in its list per object, and a linear search per extend made extending quadratic (gap 19).
        // WeakReference does not override Equals, so this is a set of references by identity; it works because
        // a module hands out one WeakReference for itself (RubyModule.WeakSelf).
        private readonly HashSet<WeakReference/*!*/> _set = new HashSet<WeakReference>();

        // Add prunes when the list has doubled since the last prune, so entries whose targets died are dropped
        // even if nothing ever enumerates the list, at amortized constant cost per Add.
        private int _pruneAt = 16;

        public WeakList() {
        }

        // Enumerations of this list in progress (they nest: a walk of the dependents may reach the same
        // module again). An enumeration goes by index, so entries may be added under it, but nothing may be
        // removed: Prune moves entries down, and the walk would skip some - classes whose method version or
        // mixins it was there to update.
        private int _enumerations;

        public IEnumerator<T> GetEnumerator() {
            int deadCount = 0;
            _enumerations++;
            try {
                for (int i = 0; i < _list.Count; i++) {
                    object item = _list[i].Target;
                    if (item != null) {
                        yield return (T)item;
                    } else {
                        deadCount++;
                    }
                }
            } finally {
                _enumerations--;
            }

            // deadCount is greated than 1/5 of total => remove dead (the threshold is arbitrary, might need tuning):
            if (_enumerations == 0 && deadCount > 5 && deadCount > _list.Count / 5) {
                Prune();
            }
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return GetEnumerator();
        }


        public void Add(WeakReference item) {
            if (_enumerations == 0 && _list.Count >= _pruneAt) {
                Prune();
                _pruneAt = Math.Max(16, _list.Count * 2);
            }
            _list.Add(item);
            _set.Add(item);
        }

        /// <summary>
        /// True if this exact reference was added and has not been pruned; a reference whose target died may
        /// still be found, which is harmless since a dead target cannot be added again.
        /// </summary>
        public bool Contains(WeakReference item) {
            return _set.Contains(item);
        }

        private void Prune() {
            int i = 0, j = 0;
            while (i < _list.Count) {
                if (_list[i].IsAlive) {
                    if (j != i) {
                        _list[j] = _list[i];
                    }
                    j++;
                } else {
                    _set.Remove(_list[i]);
                }
                i++;
            }

            if (j < i) {
                _list.RemoveRange(j, i - j);
                _list.TrimExcess();
            }
        }
    }
}
