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
using System.Threading;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {

    /// <summary>
    /// What an all-visible method lookup of one name on one class found: the method (forwarders
    /// already followed to the body they forward to, as <see cref="RubyModule.ResolveMethod"/> answers)
    /// and the visibility the name has *at its entry point* - the forwarder's, when `public :m' in a
    /// subclass re-exported an inherited private method, which is what #respond_to? and
    /// #public_method_defined? ask about.
    /// </summary>
    public sealed class CachedMethodLookup {
        public static readonly CachedMethodLookup/*!*/ NotFound = new CachedMethodLookup(MethodResolutionResult.NotFound);

        public readonly MethodResolutionResult Result;

        internal CachedMethodLookup(MethodResolutionResult result) {
            Result = result;
        }

        public RubyMethodVisibility Visibility {
            get { return Result.Visibility; }
        }

        public bool Found {
            get { return Result.Found; }
        }

        public RubyMemberInfo Info {
            get { return Result.Info; }
        }
    }

    // Per-class method-lookup cache: name -> CachedMethodLookup, lock-free to read.
    //
    // Invalidation keys on the class's own VersionHandle.Method, the version the call-site rules
    // check. It is bumped (under the class-hierarchy lock) for a class and every class below it
    // whenever a method change there could alter what a *cached* lookup would find - but only for
    // lookups that registered themselves the way a call site does, since IronRuby bumps lazily:
    //  - a found method is marked InvalidateSitesOnOverride, so overriding, undefining, removing,
    //    hiding or re-visibility-ing it (anywhere below its owner, including via include/extend)
    //    bumps the version;
    //  - an absent name goes into RubyContext.MissingMethodsCachedInSites, so a later definition
    //    of that name in any class, or in any module mixed into one, bumps it.
    // Prepend, refine, extension methods and TracePoint bump unconditionally. Registering exactly
    // as call sites do means the cache is sound for precisely the reason cached rules are.
    //
    // A fill takes the class-hierarchy lock, reads the version, resolves and publishes under that
    // one lock hold; versions only change under the same lock, so an entry can never be filed under
    // a version it was not computed at. A reader that races a concurrent definition sees either the
    // state before it or falls through to the lock - the same guarantee a call site's version check
    // gives. Only classes are cached: a module's VersionHandle is never bumped (only the classes
    // that depend on it are), so a module's lookups keep taking the lock.
    public partial class RubyModule {
        private sealed class MethodLookupTable {
            internal readonly int Version;
            internal readonly Dictionary<string, CachedMethodLookup>/*!*/ Entries;

            internal MethodLookupTable(int version, Dictionary<string, CachedMethodLookup>/*!*/ entries) {
                Version = version;
                Entries = entries;
            }
        }

        // An upper bound on the names cached per class, for programs that ask with generated names.
        private const int MaxCachedMethodLookups = 256;

        // Copy-on-write: a published table is never mutated, so readers need no lock.
        private MethodLookupTable _methodLookups;

        /// <summary>
        /// All-visible lookup of <paramref name="name"/>, ignoring refinements. Lock-free once warm for a class.
        /// </summary>
        public CachedMethodLookup/*!*/ GetMethodLookup(string/*!*/ name) {
            int version;
            return GetMethodLookup(name, out version);
        }

        /// <summary>
        /// <see cref="GetMethodLookup(string)"/>, and the method version the answer belongs to, for a caller
        /// that keeps the answer itself (a call site's storage): it stays right while <see cref="Version"/>.Method
        /// equals <paramref name="version"/>. -1 when the answer must not be kept.
        /// </summary>
        public CachedMethodLookup/*!*/ GetMethodLookup(string/*!*/ name, out int version) {
            var table = Volatile.Read(ref _methodLookups);
            CachedMethodLookup entry;
            if (table != null && table.Version == Version.Method && table.Entries.TryGetValue(name, out entry)) {
                version = table.Version;
                return entry;
            }

            // A singleton class that finds what its superclass finds for every name it has no entry for (see InheritedLookups):
            var inherited = Volatile.Read(ref _inheritedLookups);
            if (inherited != null && inherited.Version == Version.Method && !inherited.HasOwn(name)) {
                int superVersion;
                entry = ((RubyClass)this).SuperClass.GetMethodLookup(name, out superVersion);

                // Nothing in this class changed meanwhile: a change of its own table or modules drops the snapshot under the
                // class hierarchy lock, which a fill of the superclass's answer takes as well (see InheritedLookups).
                if (superVersion >= 0 && Volatile.Read(ref _inheritedLookups) == inherited && inherited.Version == Version.Method) {
                    version = inherited.Version;
                    return entry;
                }
            }
            return FillMethodLookup(name, out version);
        }

        private CachedMethodLookup/*!*/ FillMethodLookup(string/*!*/ name, out int publishedVersion) {
            publishedVersion = -1;
            using (Context.ClassHierarchyLocker()) {
                int version = Version.Method;

                // Not while a library method table is being populated: its methods go in without the
                // version bump a later definition would make.
                if (Context.MethodTableInitializationDepth == 0 && InheritsMethodLookupsNoLock()) {
                    var inherited = _inheritedLookups;
                    if (inherited == null || inherited.Version != version) {
                        Volatile.Write(ref _inheritedLookups, inherited = new InheritedLookups(version, _methods));
                    }
                    if (!inherited.HasOwn(name)) {
                        int superVersion;
                        var inheritedEntry = ((RubyClass)this).SuperClass.GetMethodLookup(name, out superVersion);
                        if (superVersion >= 0 && Version.Method == version) {
                            publishedVersion = version;
                        }
                        return inheritedEntry;
                    }
                }

                var entry = ResolveMethodLookupNoLock(name);

                // Not while a library method table is being populated: its methods go in without the
                // version bump a later definition would make.
                if (!IsClass || Version.Method != version || Context.MethodTableInitializationDepth != 0) {
                    return entry;
                }

                var table = _methodLookups;
                Dictionary<string, CachedMethodLookup> entries;
                if (table == null || table.Version != version) {
                    entries = new Dictionary<string, CachedMethodLookup>(StringComparer.Ordinal);
                } else if (table.Entries.Count >= MaxCachedMethodLookups) {
                    // Not filed. A method found is registered for invalidation all the same (InvalidateSitesOnOverride),
                    // so a caller may keep it as long as a filed answer; an absent name would need registering first.
                    if (entry.Found) {
                        publishedVersion = version;
                    }
                    return entry;
                } else {
                    entries = new Dictionary<string, CachedMethodLookup>(table.Entries, StringComparer.Ordinal);
                }

                if (!entry.Found) {
                    // A definition of this name anywhere now bumps the versions of the classes it affects:
                    entry.Result.InvalidateSitesOnMissingMethodAddition(name, Context);
                }

                entries[name] = entry;
                Volatile.Write(ref _methodLookups, new MethodLookupTable(version, entries));
                publishedVersion = version;
                return entry;
            }
        }

        // IR_SINGLETON_LOOKUP=0: a singleton class's lookups are filed in its own cache as any class's (see InheritedLookups).
        private static readonly bool _InheritLookups = Environment.GetEnvironmentVariable("IR_SINGLETON_LOOKUP") != "0";

        /// <summary>
        /// The names in the method table of a singleton class that answers every other lookup from its superclass's cache
        /// (see <see cref="InheritsMethodLookupsNoLock"/>), as of a method version of the class.
        ///
        /// Each definition on an object that gets methods of its own (OpenStruct's define_singleton_method! of every member)
        /// re-versions its singleton class and so empties the class's lookup cache, and each name asked again then walked the
        /// ancestors and copied the cache's table to file one more entry - some thirty lookups (the hooks, respond_to?, the
        /// shared rules' tests) per OpenStruct. The superclass's cache stays warm across all such objects, so a lookup of a name
        /// the singleton class has no entry for is answered there, without the class hierarchy lock, and nothing is filed.
        ///
        /// The answer is kept by its callers under this class's version, as a filed one is, and is right for as long: the
        /// superclass's fill registered it for invalidation (InvalidateSitesOnOverride, MissingMethodsCachedInSites), so a
        /// definition of the name here re-versions this class, as anything that changes the superclass's answer does. A
        /// definition of a name nobody has asked for does not re-version it, though, so the snapshot of the names is dropped
        /// (under the class hierarchy lock) by every change of the method table or of the modules, and the reader checks it is
        /// still the current one after taking the superclass's answer: a superclass fill that registered the name took the
        /// lock after any definition that had not seen the registration.
        /// </summary>
        private sealed class InheritedLookups {
            internal readonly int Version;
            private readonly string/*!*/[]/*!*/ _ownNames;

            internal InheritedLookups(int version, Dictionary<string, RubyMemberInfo> methods) {
                Version = version;
                if (methods == null || methods.Count == 0) {
                    _ownNames = Array.Empty<string>();
                } else {
                    _ownNames = new string[methods.Count];
                    methods.Keys.CopyTo(_ownNames, 0);
                }
            }

            // any entry, a hidden or undefined one too
            internal bool HasOwn(string/*!*/ name) {
                foreach (string own in _ownNames) {
                    if (String.Equals(own, name)) {
                        return true;
                    }
                }
                return false;
            }
        }

        // Dropped by every change of this module's method table or modules (under the class hierarchy lock):
        private InheritedLookups _inheritedLookups;

        // Past this many own methods a singleton class files its lookups (the names are searched one by one).
        private const int MaxInheritedLookupOwnNames = 16;

        /// <summary>
        /// True if this is the singleton class of a plain Ruby object (not a module) that has no modules included in it or
        /// prepended to it, nor many methods of its own: a walk from it for a name it has no entry for is its superclass's walk
        /// (FindMethodInAncestorsNoLock), and finds the same method and owner. See <see cref="InheritedLookups"/>.
        /// </summary>
        private bool InheritsMethodLookupsNoLock() {
            var cls = this as RubyClass;
            if (!_InheritLookups || cls == null || !cls.IsSingletonClass || cls.SuperClass == null ||
                !(cls.SingletonClassOf is IRubyObject) || cls.SingletonClassOf is RubyModule || TypeTracker != null ||
                _prepends.Length != 0 || _mixins.Length != 0) {
                return false;
            }
            InitializeMethodsNoLock();
            return _methods == null || _methods.Count <= MaxInheritedLookupOwnNames;
        }

        /// <summary>
        /// The definition of <paramref name="name"/> in this module's own method table only (method_defined?'s
        /// inherit = false: MRI looks up from the origin and requires the entry's owner to be this module, so
        /// neither prepended modules nor ancestors count). Not cached; it is rare.
        /// </summary>
        public CachedMethodLookup/*!*/ GetOwnMethodLookup(string/*!*/ name) {
            using (Context.ClassHierarchyLocker()) {
                InitializeMethodsNoLock();

                RubyMemberInfo info;
                bool skipHidden = false;
                if (!TryGetMethod(name, ref skipHidden, out info) || info == null || info.IsUndefined) {
                    return CachedMethodLookup.NotFound;
                }

                if (info.IsSuperForwarder) {
                    var target = ResolveSuperMethodNoLock(((SuperForwarderInfo)info).SuperName, this);
                    return target.Found ? new CachedMethodLookup(target.WithVisibility(info.Visibility)) : CachedMethodLookup.NotFound;
                }

                return new CachedMethodLookup(new MethodResolutionResult(info, this, true));
            }
        }

        private CachedMethodLookup/*!*/ ResolveMethodLookupNoLock(string/*!*/ name) {
            Context.RequiresClassHierarchyLock();

            // The same walk as ResolveMethodNoLock(name, AllVisible), except that a super-forwarder is
            // returned so its visibility can be kept, then followed the way that walk follows it.
            var result = ResolveMethodNoLock(name, VisibilityContext.AllVisible, MethodLookup.ReturnForwarder).InvalidateSitesOnOverride();
            if (!result.Found) {
                return CachedMethodLookup.NotFound;
            }

            var visibility = result.Visibility;
            if (result.Info.IsSuperForwarder) {
                var forwarder = (SuperForwarderInfo)result.Info;
                result = result.Owner.ResolveSuperMethodNoLock(forwarder.SuperName, result.Owner).InvalidateSitesOnOverride();
                if (!result.Found) {
                    return CachedMethodLookup.NotFound;
                }
            }

            return new CachedMethodLookup(result.WithVisibility(visibility));
        }
    }
}
