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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    internal interface IEnumerator {
        object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block);
    }

    /// <summary>
    /// A wrapper that provides "each" method for an arbitrary object. 
    /// A call to "each" on the instance of Enumerator is forwarded to a call to a method of a given name on the wrapped object.
    /// </summary>
    [RubyClass("Enumerator"), Includes(typeof(Enumerable))]
    public class Enumerator : IRubyInstanceDataOwner {
        // Its external iteration (#next, #peek, #rewind in ruby4.rb) keeps its state in instance
        // variables, so they live here rather than in the runtime's table of instance data.
        private OwnedInstanceData _instanceData;

        // An instance of a Ruby subclass (Enumerator::Lazy, or a user's) is an IRubyObject with instance
        // data of its own, which every other path reads: that is the one to hand out then.
        bool IRubyInstanceDataOwner.TryGetInstanceData(RubyContext/*!*/ context, out RubyInstanceData data) {
            var rubyObject = this as IRubyObject;
            if (rubyObject != null) {
                data = rubyObject.TryGetInstanceData();
                return true;
            }
            return _instanceData.TryGet(context, out data);
        }

        RubyInstanceData IRubyInstanceDataOwner.GetInstanceData(RubyContext/*!*/ context) {
            var rubyObject = this as IRubyObject;
            return (rubyObject != null) ? rubyObject.GetInstanceData() : _instanceData.GetOrCreate(context);
        }

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
                // The arguments were recorded when the enumerator was made. A hash among them
                // that a ruby2_keywords method was carrying has to go back to being keyword
                // arguments now, exactly as it would at a splatted call site - `enum_for(:find,
                // *paths, ignore_error: true)' has to reach #find as keywords.
                return KernelOps.SendMessageOpt(scope, block, _targetObject, _targetName,
                    RubyOps.RestoreRuby2Keywords(_targetArguments)
                );
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
        [ReceivesKeywords]
        public static Enumerator/*!*/ Create(RubyClass/*!*/ self, object targetObject, [DefaultProtocol, Optional]string targetName,
            params object[]/*!*/ targetArguments) {

            return Reinitialize(new Enumerator(), targetObject, targetName, targetArguments);
        }

        /// <summary>
        /// #dup and #clone: the copy iterates the same target with the same arguments (the
        /// instance variables a prelude-built enumerator keeps its generator in are copied by
        /// the runtime). Without this a copy had no target and failed with "undefined method
        /// `each' for nil".
        /// </summary>
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static Enumerator/*!*/ InitializeCopy(Enumerator/*!*/ self, [NotNull]Enumerator/*!*/ other) {
            self._impl = other._impl;
            self.SizeSource = other.SizeSource;
            self.SizeOp = other.SizeOp;
            self.SizeArg = other.SizeArg;
            return self;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        [ReceivesKeywords]
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

        [RubyMethod("__each_without_generator__")]
        public static object Each(RubyScope/*!*/ scope, BlockParam/*!*/ block, Enumerator/*!*/ self) {
            return self._impl.Each(scope, block);
        }

        /// <summary>
        /// Enumerator#each. Iterating a method-based enumerator
        /// calls the method with the frame that called #each, as MRI's C #each does, so a Regexp
        /// match the method makes (String#scan) lands in the $~ the block reads. Anything else -
        /// arguments, a generator, no block - is the prelude's __ir_each_impl__.
        /// </summary>
        [RubyMethod("each")]
        [ReceivesKeywords]
        public static object EachFromCaller(CallSiteStorage<Func<CallSite, object, Proc, System.Collections.IList, object>>/*!*/ implStorage,
            RubyScope/*!*/ scope, BlockParam block, Enumerator/*!*/ self, params object[]/*!*/ args) {

            object generator;
            if (args.Length == 0 && block != null && self._impl != null &&
                !(scope.RubyContext.TryGetInstanceVariable(self, RubyUtils.GetCoreStateInstanceVariableName("@__generator__"), out generator) && generator != null)) {
                return self._impl.Each(scope, block);
            }

            var site = implStorage.GetCallSite("__ir_each_impl__",
                new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock | RubyCallFlags.HasSplattedArgument));
            return site.Target(site, self, (block != null) ? block.Proc : null, new RubyArray(args));
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

        #region external iteration: #next, #peek, #next_values, #peek_values, #feed

        // MRI's enumerator_next and friends suspend the #each they run in a fiber, which is a thread
        // of its own here: a resume / Fiber.yield round trip is a thread switch each way. Most
        // enumerators are over a built-in method that runs no Ruby code between two yields - Array#each,
        // Hash#each_pair, Integer#times... - and for those an ExternalCursor does what the method does,
        // one element per #next, with no fiber at all. Everything else (a generator, a lazy chain, a
        // method that has been redefined) goes on the prelude's fiber (__iter_slow__ in ruby4.rb).
        //
        // The cursor is chosen when the iteration starts, at the first #next or #peek, because that is
        // when MRI calls the method (next_i): a method redefined after the enumerator was made but
        // before then is the one that runs, and one redefined later does not stop the iteration that
        // is under way.

        // The iteration under way without a fiber, or null.
        private ExternalCursor _cursor;
        // The iteration under way, or the one that ended, runs on the prelude's fiber.
        private bool _onFiber;
        // Enumerator#feed: what the suspended yield answers once the iteration goes on.
        private object _feedValue;
        private bool _hasFeed;

        // __iter_slow__'s operations.
        private const int SlowAdvance = 0, SlowPeek = 1, SlowAcrossThreads = 2;

        /// <summary>
        /// Where the iteration of a built-in method has got to. A cursor belongs to the Ruby thread
        /// that started it, as the fiber would; its state changes under a lock, since another thread
        /// may take a value it peeked (MRI hands that one out without looking at the thread).
        /// </summary>
        internal abstract class ExternalCursor {
            internal readonly Thread/*!*/ Thread;
            // What the method answers (StopIteration#result): its receiver, for all of them.
            internal readonly object Receiver;
            internal bool Started, Peeked, Done;
            // The values of the step #peek has taken: the value, or an Array of them (Multiple).
            internal object PeekValue;
            internal StopIteration StopException;

            protected ExternalCursor(object receiver) {
                Receiver = receiver;
                Thread = RubyUtils.CurrentRubyThread;
            }

            /// <summary>Yields more than one value per step (each_with_index): the value is then an Array of them.</summary>
            internal virtual bool Multiple {
                get { return false; }
            }

            /// <summary>The next step of the iteration, false at its end.</summary>
            internal abstract bool MoveNext(out object value);

            /// <summary>The method has returned: what it does on its way out.</summary>
            internal virtual void Finish() {
            }

            /// <summary>
            /// The yield of the last step has returned, with the value fed to it (Enumerator#feed) or nil: what the method's
            /// block would have answered.
            /// </summary>
            internal virtual void Resume(object fed) {
            }

            /// <summary>What the method answers (StopIteration#result), once it has returned.</summary>
            internal virtual object Result {
                get { return Receiver; }
            }
        }

        // What a collecting method does with what its block answers, the block being the suspended yield of #next: #map
        // keeps the answers, #select (#filter, #find_all) the elements it answered true for, #reject the others.
        private const int CollectMap = 0, CollectSelect = 1, CollectReject = 2;

        // Array#map, #collect, #select, #filter and #reject (IListOps): the length is read at each step, as Array#each's
        // is; #select keeps the element as it is once the block has run (MRI's rb_ary_elt), #reject the one it yielded.
        private sealed class ArrayCollectCursor : ExternalCursor {
            private readonly RubyArray/*!*/ _array;
            private readonly int _kind;
            private readonly RubyArray/*!*/ _result = new RubyArray();
            private int _index;
            private object _current;

            internal ArrayCollectCursor(RubyArray/*!*/ array, int kind) : base(array) {
                _array = array;
                _kind = kind;
            }

            internal override bool MoveNext(out object value) {
                int index = _index;
                if (index < _array.Count) {
                    _index = index + 1;
                    value = _current = _array[index];
                    return true;
                }
                value = _current = null;
                return false;
            }

            internal override void Resume(object fed) {
                if (_kind == CollectMap) {
                    _result.Add(fed);
                } else if (_kind == CollectSelect) {
                    if (Protocols.IsTrue(fed)) {
                        int index = _index - 1;
                        _result.Add(index < _array.Count ? _array[index] : null);
                    }
                } else if (!Protocols.IsTrue(fed)) {
                    _result.Add(_current);
                }
            }

            internal override object Result {
                get { return _result; }
            }
        }

        // Enumerable#map, #collect, #select, #filter, #find_all and #reject over a cursor of the receiver's #each (a Range,
        // a Hash's #map and #find_all, a Struct): the element kept is the one yielded, packed before the block runs (FilterImpl).
        private sealed class EnumerableCollectCursor : ExternalCursor {
            private readonly ExternalCursor/*!*/ _inner;
            private readonly int _kind;
            private readonly RubyArray/*!*/ _result = new RubyArray();
            private object _current;

            internal EnumerableCollectCursor(object receiver, ExternalCursor/*!*/ inner, int kind) : base(receiver) {
                _inner = inner;
                _kind = kind;
            }

            internal override bool Multiple {
                get { return _inner.Multiple; }
            }

            internal override bool MoveNext(out object value) {
                if (_inner.MoveNext(out value)) {
                    _current = value;
                    return true;
                }
                _current = null;
                return false;
            }

            internal override void Resume(object fed) {
                if (_kind == CollectMap) {
                    _result.Add(fed);
                } else if (Protocols.IsTrue(fed) == (_kind == CollectSelect)) {
                    _result.Add(_current);
                }
            }

            internal override void Finish() {
                _inner.Finish();
            }

            internal override object Result {
                get { return _result; }
            }
        }

        // Enumerable#each_slice and #each_cons (EachSlice) over a cursor of the receiver's #each: a new
        // Array per slice; each_slice ends with the incomplete slice, each_cons drops it.
        private sealed class SliceCursor : ExternalCursor {
            private readonly ExternalCursor/*!*/ _inner;
            private readonly int _size;
            private readonly bool _includeIncomplete;
            private RubyArray _slice;
            private bool _done;

            internal SliceCursor(object receiver, ExternalCursor/*!*/ inner, int size, bool includeIncomplete) : base(receiver) {
                _inner = inner;
                _size = size;
                _includeIncomplete = includeIncomplete;
            }

            internal override bool MoveNext(out object value) {
                while (!_done) {
                    object item;
                    if (!_inner.MoveNext(out item)) {
                        _done = true;
                        _inner.Finish();
                        if (_slice != null && _includeIncomplete) {
                            value = _slice;
                            _slice = null;
                            return true;
                        }
                        break;
                    }
                    if (_slice == null) {
                        _slice = new RubyArray(_size);
                    }
                    _slice.Add(item);
                    if (_slice.Count == _size) {
                        RubyArray complete = _slice;
                        if (_includeIncomplete) {
                            _slice = null;
                        } else {
                            _slice = new RubyArray(_size);
                            for (int i = 1; i < complete.Count; i++) {
                                _slice.Add(complete[i]);
                            }
                        }
                        value = complete;
                        return true;
                    }
                }
                value = null;
                return false;
            }
        }

        // Array#each, #each_index: the length and the element are read afresh for every step, as
        // rb_ary_each does, so an Array that grows or shrinks half way through is walked the same.
        private sealed class ArrayCursor : ExternalCursor {
            private readonly RubyArray/*!*/ _array;
            private readonly bool _indices;
            private int _index;

            internal ArrayCursor(RubyArray/*!*/ array, bool indices) : base(array) {
                _array = array;
                _indices = indices;
            }

            internal override bool MoveNext(out object value) {
                int index = _index;
                if (index < _array.Count) {
                    _index = index + 1;
                    value = _indices ? ScriptingRuntimeHelpers.Int32ToObject(index) : _array[index];
                    return true;
                }
                value = null;
                return false;
            }
        }

        // Enumerable#each_with_index over Array#each: [element, index], two values per step.
        private sealed class ArrayWithIndexCursor : ExternalCursor {
            private readonly RubyArray/*!*/ _array;
            private int _index;

            internal ArrayWithIndexCursor(RubyArray/*!*/ array) : base(array) {
                _array = array;
            }

            internal override bool Multiple {
                get { return true; }
            }

            internal override bool MoveNext(out object value) {
                int index = _index;
                if (index < _array.Count) {
                    _index = index + 1;
                    var values = new RubyArray(2);
                    values.Add(_array[index]);
                    values.Add(ScriptingRuntimeHelpers.Int32ToObject(index));
                    value = values;
                    return true;
                }
                value = null;
                return false;
            }
        }

        // Enumerator#with_index, #each_with_index and #with_object made without a block (the prelude's generators, marked by
        // __iter_index_source__), and Enumerable#each_with_index over a receiver whose #each a cursor walks: the source's step
        // with its index, or the memo, beside it - [value, index], the values of a step of several packed into an Array as the
        // generator's `a.size <= 1 ? a.first : a` packs them. What the block answers goes on to the source's suspended yield
        // (with_index_i and with_object_i answer it), so `map.with_index` collects it.
        private sealed class IndexCursor : ExternalCursor {
            private readonly ExternalCursor/*!*/ _inner;
            private readonly bool _withObject;
            private readonly object _memo;
            // the generator answers what the source's #each answers; Enumerable#each_with_index answers its receiver
            private readonly bool _answersReceiver;
            private long _index;

            internal IndexCursor(object receiver, ExternalCursor/*!*/ inner, long offset, bool answersReceiver) : base(receiver) {
                _inner = inner;
                _index = offset;
                _answersReceiver = answersReceiver;
            }

            internal IndexCursor(object receiver, ExternalCursor/*!*/ inner, object memo) : base(receiver) {
                _inner = inner;
                _withObject = true;
                _memo = memo;
            }

            internal override bool Multiple {
                get { return true; }
            }

            internal override bool MoveNext(out object value) {
                object step;
                if (!_inner.MoveNext(out step)) {
                    value = null;
                    return false;
                }
                if (_inner.Multiple) {
                    var stepValues = (RubyArray)step;
                    step = (stepValues.Count == 0) ? null : (stepValues.Count == 1) ? stepValues[0] : new RubyArray(stepValues);
                }
                var values = new RubyArray(2);
                values.Add(step);
                if (_withObject) {
                    values.Add(_memo);
                } else {
                    long index = _index++;
                    values.Add((index >= Int32.MinValue && index <= Int32.MaxValue) ?
                        ScriptingRuntimeHelpers.Int32ToObject((int)index) : (object)(BigInteger)index);
                }
                value = values;
                return true;
            }

            internal override void Resume(object fed) {
                _inner.Resume(fed);
            }

            internal override void Finish() {
                _inner.Finish();
            }

            internal override object Result {
                get { return _withObject ? _memo : _answersReceiver ? Receiver : _inner.Result; }
            }
        }

        // Array#reverse_each (rb_ary_reverse_each): from the length the iteration started with, down,
        // moving the position back when the Array has shrunk below it in the meantime.
        private sealed class ArrayReverseCursor : ExternalCursor {
            private readonly RubyArray/*!*/ _array;
            private int _index;
            private bool _yielded;

            internal ArrayReverseCursor(RubyArray/*!*/ array) : base(array) {
                _array = array;
                _index = array.Count;
            }

            internal override bool MoveNext(out object value) {
                if (_yielded) {
                    int count = _array.Count;
                    if (count < _index) {
                        _index = count;
                    }
                }
                if (_index > 0) {
                    _index--;
                    _yielded = true;
                    value = _array[_index];
                    return true;
                }
                value = null;
                return false;
            }
        }

        // Hash#each/#each_pair, #each_key, #each_value, walked in place (see Hash.TryGetNextEntry): an
        // entry deleted ahead of the walk is skipped and a value overwritten is seen overwritten. The
        // hash is being iterated - no new key, no #rehash - from the first step to the end of the walk,
        // as in MRI, where it stays so for good if the walk never gets to its end (#rewind, or a dropped
        // enumerator): MRI's fiber never runs hash_foreach_ensure then.
        private sealed class HashCursor : ExternalCursor {
            internal const int Pairs = 0, Keys = 1, Values = 2;
            private readonly Hash/*!*/ _hash;
            private readonly int _kind;
            private int _index;
            private bool _begun, _iterating;

            internal HashCursor(Hash/*!*/ hash, int kind) : base(hash) {
                _hash = hash;
                _kind = kind;
            }

            internal override bool MoveNext(out object value) {
                if (!_begun) {
                    _begun = true;
                    // rb_hash_foreach does not iterate an empty hash at all
                    if (_hash.Count == 0) {
                        value = null;
                        return false;
                    }
                    _hash.BeginIteration();
                    _iterating = true;
                }
                object key, entryValue;
                if (_hash.TryGetNextEntry(ref _index, out key, out entryValue)) {
                    switch (_kind) {
                        case Keys: value = CustomStringDictionary.ObjToNull(key); break;
                        case Values: value = entryValue; break;
                        default: value = IDictionaryOps.MakeArray(key, entryValue); break;
                    }
                    return true;
                }
                value = null;
                return false;
            }

            internal override void Finish() {
                if (_iterating) {
                    _iterating = false;
                    _hash.EndIteration();
                }
            }
        }

        // Range#each over Integers, Integer#times, #upto and #downto: the ends are fixed when the
        // iteration starts. Last is inclusive; an endless range runs to the end of long, which nobody
        // reaches with #next.
        private sealed class IntegerCursor : ExternalCursor {
            private long _next;
            private readonly long _last;
            private readonly bool _down;
            private bool _done;

            internal IntegerCursor(object receiver, long first, long last, bool down) : base(receiver) {
                _next = first;
                _last = last;
                _down = down;
                _done = down ? first < last : first > last;
            }

            internal override bool MoveNext(out object value) {
                if (_done) {
                    value = null;
                    return false;
                }
                long current = _next;
                value = Protocols.Normalize(current);
                if (current == _last) {
                    _done = true;
                } else {
                    _next = _down ? current - 1 : current + 1;
                }
                return true;
            }
        }

        // String#each_byte (rb_str_each_byte) reads the length and the byte afresh for every step.
        private sealed class ByteCursor : ExternalCursor {
            private readonly MutableString/*!*/ _string;
            private int _index;

            internal ByteCursor(MutableString/*!*/ str) : base(str) {
                _string = str;
            }

            internal override bool MoveNext(out object value) {
                int index = _index;
                if (index < _string.GetByteCount()) {
                    _index = index + 1;
                    value = ScriptingRuntimeHelpers.Int32ToObject((int)_string.GetByte(index));
                    return true;
                }
                value = null;
                return false;
            }
        }

        // String#each_char (rb_str_each_char) walks a frozen copy of the string, made as it starts:
        // what the string becomes later is not seen.
        private sealed class CharCursor : ExternalCursor {
            private readonly MutableString/*!*/ _copy;
            private readonly MutableString.CharacterEnumerator/*!*/ _chars;

            internal CharCursor(MutableString/*!*/ str) : base(str) {
                _copy = MutableString.Create(str);
                _chars = _copy.GetCharacters();
            }

            internal override bool MoveNext(out object value) {
                if (_chars.MoveNext()) {
                    value = _chars.Current.ToMutableString(_copy.Encoding);
                    return true;
                }
                value = null;
                return false;
            }
        }

        // Struct#each (rb_struct_each): every member read when its turn comes.
        private sealed class StructCursor : ExternalCursor {
            private readonly RubyStruct/*!*/ _struct;
            private int _index;

            internal StructCursor(RubyStruct/*!*/ str) : base(str) {
                _struct = str;
            }

            internal override bool MoveNext(out object value) {
                int index = _index;
                if (index < _struct.ItemCount) {
                    _index = index + 1;
                    value = _struct[index];
                    return true;
                }
                value = null;
                return false;
            }
        }

        // The prelude's own definitions of methods a cursor stands in for (registered by
        // __iter_cursor_method__), per runtime: a method that is still one of these has not been
        // replaced.
        private sealed class PreludeMethods {
            internal RubyMemberInfo EachWithIndex;
            internal RubyMemberInfo StructEach;
            internal RubyMemberInfo EachSlice;
            internal RubyMemberInfo EachCons;
            internal RubyMemberInfo StopIterationResult;
            internal RubyMemberInfo LazyEach;
        }

        private static readonly object _preludeMethodsKey = new object();

        private static PreludeMethods/*!*/ GetPreludeMethods(RubyContext/*!*/ context) {
            return (PreludeMethods)context.GetOrCreateLibraryData(_preludeMethodsKey, () => new PreludeMethods());
        }

        /// <summary>
        /// Called by the prelude right after it defines a method that wraps a built-in one a cursor
        /// can stand in for (Enumerable#each_with_index, Struct#each).
        /// </summary>
        [RubyMethod("__iter_cursor_method__", RubyMethodAttributes.PublicSingleton)]
        public static object RegisterCursorMethod(RubyContext/*!*/ context, object self, [NotNull]RubyModule/*!*/ module, [DefaultProtocol, NotNull]string/*!*/ name) {
            var method = module.ResolveMethod(name, VisibilityContext.AllVisible);
            if (method.Found) {
                var methods = GetPreludeMethods(context);
                if (name == "each_with_index") {
                    methods.EachWithIndex = method.Info;
                } else if (name == "each" && module.Name == "Enumerator::Lazy") {
                    methods.LazyEach = method.Info;
                } else if (name == "each") {
                    methods.StructEach = method.Info;
                } else if (name == "each_slice") {
                    methods.EachSlice = method.Info;
                } else if (name == "each_cons") {
                    methods.EachCons = method.Info;
                } else if (name == "result") {
                    methods.StopIterationResult = method.Info;
                }
            }
            return null;
        }

        private static bool IsPreludeMethod(RubyContext/*!*/ context, object target, string/*!*/ name, RubyMemberInfo expected) {
            if (expected == null) {
                return false;
            }
            var method = context.ResolveMethod(target, name, VisibilityContext.AllVisible);
            return method.Found && ReferenceEquals(method.Info, expected);
        }

        /// <summary>
        /// A cursor for this enumerator's iteration, or null when it has to run on a fiber: it is not a
        /// built-in method over one of its own receivers, or one of the methods involved - the
        /// enumerator's #each, the target method, and what that one calls - has been replaced
        /// (redefined, aliased over, overridden in a subclass or a singleton class, prepended to).
        /// </summary>
        private static ExternalCursor TryCreateCursor(RubyContext/*!*/ context, Enumerator/*!*/ self) {
            var wrapper = self._impl as Wrapper;
            if (wrapper == null) {
                return null;
            }

            object generator;
            if (context.TryGetInstanceVariable(self, RubyUtils.GetCoreStateInstanceVariableName("@__generator__"), out generator) && generator != null) {
                return TryCreateIndexCursor(context, self, generator);
            }

            if (!IsBuiltinMethod(context, self, "each", typeof(Enumerator), "EachFromCaller")) {
                return null;
            }

            object target = wrapper.TargetObject;
            string name = wrapper.TargetName;
            object[] args = wrapper.TargetArguments;

            if (args.Length == 1 && target is int && args[0] is int) {
                int from = (int)target, to = (int)args[0];
                if (name == "upto" && IsBuiltinMethod(context, target, name, typeof(Integer), "UpTo")) {
                    return new IntegerCursor(target, from, to, false);
                }
                if (name == "downto" && IsBuiltinMethod(context, target, name, typeof(Integer), "DownTo")) {
                    return new IntegerCursor(target, from, to, true);
                }
                return null;
            }
            if (args.Length == 1 && args[0] is int && (name == "each_slice" || name == "each_cons")) {
                return TryCreateSliceCursor(context, target, name, (int)args[0]);
            }
            if (args.Length != 0) {
                return null;
            }

            RubyArray array;
            Hash hash;
            Range range;
            MutableString str;
            RubyStruct rubyStruct;
            switch (name) {
                case "each":
                    return TryCreateEachCursor(context, target);

                case "map":
                case "collect":
                    return TryCreateCollectCursor(context, target, name, CollectMap);

                case "select":
                case "filter":
                case "find_all":
                    return TryCreateCollectCursor(context, target, name, CollectSelect);

                case "reject":
                    return TryCreateCollectCursor(context, target, name, CollectReject);

                case "each_pair":
                    return (hash = target as Hash) != null && IsBuiltinMethod(context, target, name, typeof(IDictionaryOps), "Each", "EachPair") ?
                        new HashCursor(hash, HashCursor.Pairs) : null;

                case "each_key":
                    return (hash = target as Hash) != null && IsBuiltinMethod(context, target, name, typeof(IDictionaryOps), "EachKey") ?
                        new HashCursor(hash, HashCursor.Keys) : null;

                case "each_value":
                    return (hash = target as Hash) != null && IsBuiltinMethod(context, target, name, typeof(IDictionaryOps), "EachValue") ?
                        new HashCursor(hash, HashCursor.Values) : null;

                case "each_index":
                    return (array = target as RubyArray) != null && IsBuiltinMethod(context, target, name, typeof(IListOps), "EachIndex") ?
                        new ArrayCursor(array, true) : null;

                case "reverse_each":
                    return (array = target as RubyArray) != null && IsBuiltinMethod(context, target, name, typeof(IListOps), "ReverseEach") ?
                        new ArrayReverseCursor(array) : null;

                case "each_with_index":
                    // the prelude's #each_with_index, the built-in one it hands a block to, and the #each that one calls
                    if (!IsPreludeMethod(context, target, name, GetPreludeMethods(context).EachWithIndex) ||
                        !IsBuiltinMethod(context, target, "__each_with_index_without_args__", typeof(Enumerable), "GetEachWithIndexEnumerator", "EachWithIndex")) {
                        return null;
                    }
                    if ((array = target as RubyArray) != null) {
                        return IsBuiltinMethod(context, target, "each", typeof(IListOps), "Each") ? new ArrayWithIndexCursor(array) : null;
                    }
                    // a Hash, a Range, a Struct: the steps of its #each with their index, and the receiver for the result
                    if (!_fastNext) {
                        return null;
                    }
                    ExternalCursor eachCursor = TryCreateEachCursor(context, target);
                    return eachCursor != null ? new IndexCursor(target, eachCursor, 0, true) : null;

                case "times":
                    if (target is int && IsBuiltinMethod(context, target, name, typeof(Integer), "Times")) {
                        int count = (int)target;
                        return new IntegerCursor(target, 0, (long)count - 1, false);
                    }
                    return null;

                case "each_byte":
                    return (str = target as MutableString) != null && IsBuiltinMethod(context, target, name, typeof(MutableStringOps), "EachByte") ?
                        new ByteCursor(str) : null;

                case "each_char":
                    return (str = target as MutableString) != null && IsBuiltinMethod(context, target, name, typeof(MutableStringOps), "EachChar") ?
                        new CharCursor(str) : null;
            }
            return null;
        }

        // A collecting method (#map, #select...) that is Array's own over an Array, or Enumerable's over a receiver whose #each
        // a cursor can do. IR_FAST_NEXT=0 leaves them to the fiber.
        private static ExternalCursor TryCreateCollectCursor(RubyContext/*!*/ context, object target, string/*!*/ name, int kind) {
            if (!_fastNext) {
                return null;
            }
            // the C# methods of the name: the blockless one that made the enumerator, and the one with a block it stands for
            string impl = (kind == CollectMap) ? (name == "collect" ? "Collect" : "Map") :
                (kind == CollectReject) ? "Reject" : (name == "filter" ? "Filter" : name == "find_all" ? "FindAll" : "Select");
            RubyArray array = target as RubyArray;
            if (array != null && name != "find_all") {
                return IsBuiltinMethod(context, target, name, typeof(IListOps),
                    kind == CollectMap ? "GetMapEnumerator" : kind == CollectSelect ? "GetSelectEnumerator" : "Reject", impl) ?
                    new ArrayCollectCursor(array, kind) : null;
            }
            if (!IsBuiltinMethod(context, target, name, typeof(Enumerable), kind == CollectMap ? "GetMapEnumerator" : impl, impl)) {
                return null;
            }
            ExternalCursor inner = TryCreateEachCursor(context, target);
            return inner != null ? new EnumerableCollectCursor(target, inner, kind) : null;
        }

        // What the prelude's #with_index (#each_with_index) and #with_object made without a block are over, while their generator
        // is the one they made (an #initialize again replaces it; a copy has no mark): see __iter_index_source__.
        private Enumerator _indexSource;
        private object _indexArgument;
        private bool _indexWithObject;
        private object _indexGenerator;

        /// <summary>
        /// Marks the generator enumerator the prelude's #with_index or #with_object has just made as one over <paramref name="source"/>,
        /// so that #next walks it with a cursor over the source's when the source has one, rather than on a fiber.
        /// </summary>
        [RubyMethod("__iter_index_source__", RubyMethodAttributes.PrivateInstance)]
        public static Enumerator/*!*/ MarkIndexSource(RubyContext/*!*/ context, Enumerator/*!*/ self, object source, object argument, bool withObject) {
            var enumerator = source as Enumerator;
            object generator;
            if (enumerator != null && self._indexGenerator == null &&
                context.TryGetInstanceVariable(self, RubyUtils.GetCoreStateInstanceVariableName("@__generator__"), out generator) && generator != null) {
                self._indexSource = enumerator;
                self._indexArgument = argument;
                self._indexWithObject = withObject;
                self._indexGenerator = generator;
            }
            return self;
        }

        // A marked generator enumerator whose #each is the built-in one, over a source that has a cursor of its own; an index
        // that is not a Fixnum leaves it to the fiber. IR_FAST_NEXT=0 leaves them all to it.
        private static ExternalCursor TryCreateIndexCursor(RubyContext/*!*/ context, Enumerator/*!*/ self, object generator) {
            if (!_fastNext || self._indexGenerator == null || !ReferenceEquals(generator, self._indexGenerator) ||
                (!self._indexWithObject && !(self._indexArgument is int)) ||
                !IsBuiltinMethod(context, self, "each", typeof(Enumerator), "EachFromCaller")) {
                return null;
            }
            ExternalCursor inner = TryCreateCursor(context, self._indexSource);
            if (inner == null) {
                return null;
            }
            return self._indexWithObject ? new IndexCursor(self, inner, self._indexArgument) :
                new IndexCursor(self, inner, (int)self._indexArgument, false);
        }

        // The receiver's own #each, when it is a built-in one a cursor can do.
        private static ExternalCursor TryCreateEachCursor(RubyContext/*!*/ context, object target) {
            RubyArray array;
            Hash hash;
            Range range;
            RubyStruct rubyStruct;
            if ((array = target as RubyArray) != null) {
                return IsBuiltinMethod(context, target, "each", typeof(IListOps), "Each") ? new ArrayCursor(array, false) : null;
            }
            if ((hash = target as Hash) != null) {
                return IsBuiltinMethod(context, target, "each", typeof(IDictionaryOps), "Each", "EachPair") ? new HashCursor(hash, HashCursor.Pairs) : null;
            }
            if ((range = target as Range) != null) {
                return TryCreateRangeCursor(context, range);
            }
            if ((rubyStruct = target as RubyStruct) != null) {
                return IsPreludeMethod(context, target, "each", GetPreludeMethods(context).StructEach) &&
                    IsBuiltinMethod(context, target, "__struct_each__", typeof(RubyStructOps), "Each") ? new StructCursor(rubyStruct) : null;
            }
            return null;
        }

        // Enumerable#each_slice / #each_cons (the prelude's, over the built-in ones) of a receiver whose
        // #each a cursor can do: the slices are cut from that cursor's steps.
        private static ExternalCursor TryCreateSliceCursor(RubyContext/*!*/ context, object target, string/*!*/ name, int size) {
            if (size <= 0) {
                return null;
            }
            var prelude = GetPreludeMethods(context);
            bool slice = name == "each_slice";
            if (!IsPreludeMethod(context, target, name, slice ? prelude.EachSlice : prelude.EachCons) ||
                !IsBuiltinMethod(context, target, slice ? "__each_slice_without_self__" : "__each_cons_without_self__", typeof(Enumerable),
                    slice ? "GetEachSliceEnumerator" : "GetEachConsEnumerator", slice ? "EachSlice" : "EachCons")) {
                return null;
            }
            ExternalCursor inner = TryCreateEachCursor(context, target);
            return inner != null ? new SliceCursor(target, inner, size, slice) : null;
        }

        // An Integer range whose end is an Integer that fits a long, or none: range_each's fixnum loops.
        private static ExternalCursor TryCreateRangeCursor(RubyContext/*!*/ context, Range/*!*/ range) {
            if (!(range.Begin is int) || !IsBuiltinMethod(context, range, "each", typeof(RangeOps), "GetEachEnumerator", "Each")) {
                return null;
            }
            long first = (int)range.Begin;
            long last;
            object end = range.End;
            if (end == null) {
                last = Int64.MaxValue;
            } else if (end is int) {
                last = (int)end;
            } else if (end is BigInteger && (BigInteger)end >= Int64.MinValue && (BigInteger)end <= Int64.MaxValue) {
                last = (long)(BigInteger)end;
            } else {
                return null;
            }
            if (end != null && range.ExcludeEnd) {
                if (last == Int64.MinValue) {
                    return null;
                }
                last--;
            }
            return new IntegerCursor(range, first, last, false);
        }

        // External iteration needs the target/generator, not the public Enumerator that owns
        // the fiber handle. Preserve custom #each receivers, where detaching could change Ruby
        // behavior; those can still retain themselves through their suspended user frames.
        [RubyMethod("__enum_iteration_source__", RubyMethodAttributes.PrivateInstance)]
        public static Enumerator GetIterationSource(RubyContext context, Enumerator self) {
            if (context.GetImmediateClassOf(self) != context.GetClass(typeof(Enumerator)) ||
                !IsBuiltinMethod(context, self, "each", typeof(Enumerator), "EachFromCaller")) {
                return self;
            }
            var source = new Enumerator(self._impl);
            object generator;
            if (context.TryGetInstanceVariable(self, RubyUtils.GetCoreStateInstanceVariableName("@__generator__"), out generator)) {
                context.SetInstanceVariable(source, RubyUtils.GetCoreStateInstanceVariableName("@__generator__"), generator);
            }
            return source;
        }

        /// <summary>
        /// Whether a lazy enumerator's #next may run its #each on a copy of it (the prelude's Lazy#__enum_iteration_source__):
        /// it is an Enumerator::Lazy and nothing more (no singleton class, no subclass), and its #each is the prelude's, which
        /// reads only the instance variables the copy is given. The fiber then holds no reference to the lazy the program
        /// has, and an abandoned one is reclaimed (its handle collected) as an Enumerator's is. IR_FAST_NEXT=0 keeps the lazy.
        /// </summary>
        [RubyMethod("__iter_detachable_lazy__", RubyMethodAttributes.PublicSingleton)]
        public static bool IsDetachableLazy(RubyContext/*!*/ context, object self, object lazy) {
            RubyMemberInfo expected = GetPreludeMethods(context).LazyEach;
            return _fastNext && expected != null && context.GetImmediateClassOf(lazy) == expected.DeclaringModule &&
                IsPreludeMethod(context, lazy, "each", expected);
        }

        private static bool IsBuiltinMethod(RubyContext/*!*/ context, object target, string/*!*/ name, Type/*!*/ declaringType, params string[]/*!*/ clrNames) {
            var method = context.ResolveMethod(target, name, VisibilityContext.AllVisible);
            if (!method.Found || !(method.Info is RubyLibraryMethodInfo)) {
                return false;
            }
            var members = method.Info.GetMembers();
            if (members.Length == 0) {
                return false;
            }
            foreach (var member in members) {
                if (member.DeclaringType != declaringType || Array.IndexOf(clrNames, member.Name) < 0) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The cursor of the iteration under way or about to start, or null when it is the fiber's.
        /// Raises FiberError (through the prelude) for a cursor another thread started, unless it
        /// has a peeked value to hand out or has ended, which MRI looks at first.
        /// </summary>
        private static ExternalCursor GetCursor(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            RubyContext context = slowStorage.Context;
            if (context.IsObjectFrozen(self)) {
                throw RubyExceptions.CreateObjectFrozenError(context, self);
            }

            ExternalCursor cursor = self._cursor;
            if (cursor == null) {
                if (self._onFiber) {
                    return null;
                }
                ExternalCursor created = TryCreateCursor(context, self);
                if (created == null) {
                    self._onFiber = true;
                    return null;
                }
                // Two threads that start the iteration together: one of them gets the cursor, and the
                // other one is "across threads" (nothing has been done to the receiver yet).
                cursor = Interlocked.CompareExchange(ref self._cursor, created, null) ?? created;
            }

            if (cursor.Thread != RubyUtils.CurrentRubyThread && !cursor.Peeked && !cursor.Done) {
                var site = slowStorage.GetCallSite("__iter_slow__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
                site.Target(site, self, ScriptingRuntimeHelpers.Int32ToObject(SlowAcrossThreads));
            }
            return cursor;
        }

        /// <summary>
        /// Makes sure the cursor holds the values of the next step (in PeekValue) and answers them,
        /// or raises StopIteration at the end; <paramref name="consume"/> takes them out as well.
        /// Ends with the cursor's lock released: everything that can raise happens outside it.
        /// </summary>
        private static object CursorStep(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage,
            Enumerator/*!*/ self, ExternalCursor/*!*/ cursor, bool consume) {

            RubyContext context = slowStorage.Context;
            StopIteration stop = null;
            bool first = false, acrossThreads = false;
            object value;
            using (new RubyUtils.UninterruptedLock(cursor)) {
                if (!cursor.Peeked) {
                    if (cursor.Done) {
                        stop = cursor.StopException;
                    } else if (cursor.Thread != RubyUtils.CurrentRubyThread) {
                        // GetCursor let this thread in because a value was peeked, and the owner has taken
                        // it since: producing the next one here would run the iteration on a thread that
                        // does not own it. Decided under the lock that the owner consumes under.
                        acrossThreads = true;
                    } else {
                        // the suspended yield returns (next_ii), taking the fed value with it
                        if (cursor.Started) {
                            cursor.Resume(self._hasFeed ? self._feedValue : null);
                            self._feedValue = null;
                            self._hasFeed = false;
                        }
                        cursor.Started = true;
                        if (cursor.MoveNext(out value)) {
                            cursor.PeekValue = value;
                            cursor.Peeked = true;
                        } else {
                            cursor.Done = true;
                            cursor.Finish();
                            self._feedValue = null;
                            self._hasFeed = false;
                            stop = cursor.StopException = CreateStopIteration(context, cursor.Result, null);
                            first = true;
                        }
                    }
                }

                if (stop == null && !acrossThreads) {
                    value = cursor.PeekValue;
                    if (consume) {
                        cursor.Peeked = false;
                        cursor.PeekValue = null;
                    }
                    return value;
                }
            }

            if (acrossThreads) {
                // raises FiberError
                SlowStep(slowStorage, self, SlowAcrossThreads);
            }

            // get_next_values: the end is raised once as such, and from then on as a new StopIteration
            // whose cause is the first one
            if (first) {
                throw ThrowsUnkept() ? RubyExceptionData.MarkUnkept(stop) : stop;
            }
            // the first one is handed to Ruby code now (Exception#cause): if it was rescued and dropped, the walk for its
            // backtrace is still to be taken, and is taken here, as close to where it was rescued as there is
            RubyExceptionData.ExposeCatchSite(stop);
            var again = CreateStopIteration(context, cursor.Result, stop);
            throw ThrowsUnkept() ? RubyExceptionData.MarkUnkept(again) : again;
        }

        // IR_FAST_NEXT=0 throws the StopIteration of a cursor as before, the stack walked as soon as a frame rescues it.
        private static readonly bool _fastNext = Environment.GetEnvironmentVariable("IR_FAST_NEXT") != "0";

        /// <summary>
        /// The StopIteration a cursor throws is one nothing can have kept, as one Kernel#raise makes (RaisedUnkept): it is made
        /// here, of the built-in class, with no Ruby code run, and thrown at once. So the frame that rescues it may leave the walk
        /// of the stack below it - most of what a raise costs here, and an iteration ends with one - until the exception is
        /// exposed, and a `loop { e.next }` or a `rescue StopIteration` that drops it never takes the walk. The first one the cursor
        /// does keep, to be the cause of the ones after it, and takes the walk when it hands it out as one (CursorStep). Not while a
        /// TracePoint is enabled, which may see it go by and keep it.
        /// </summary>
        private static bool ThrowsUnkept() {
            return _fastNext && TracePoint.ActiveEvents == 0;
        }

        private static StopIteration/*!*/ CreateStopIteration(RubyContext/*!*/ context, object result, StopIteration cause) {
            var stop = new StopIteration("iteration reached an end");
            context.SetInstanceVariable(stop, RubyUtils.GetCoreStateInstanceVariableName("@__result__"), result);
            if (cause != null) {
                RubyExceptionData.GetInstance(stop).SetCause(cause);
            }
            return stop;
        }

        private static RubyArray/*!*/ SlowStep(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self, int op) {
            var site = slowStorage.GetCallSite("__iter_slow__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
            return (RubyArray)site.Target(site, self, ScriptingRuntimeHelpers.Int32ToObject(op));
        }

        // ary2sv: no values are nil, one is that value, more are the Array of them (a copy for #peek)
        private static object ValuesToValue(RubyArray/*!*/ values, bool copy) {
            switch (values.Count) {
                case 0: return null;
                case 1: return values[0];
                default: return copy ? new RubyArray(values) : values;
            }
        }

        private static RubyArray/*!*/ CursorValues(ExternalCursor/*!*/ cursor, object value, bool copy) {
            if (cursor.Multiple) {
                return copy ? new RubyArray((RubyArray)value) : (RubyArray)value;
            }
            var values = new RubyArray(1);
            values.Add(value);
            return values;
        }

        [RubyMethod("next")]
        public static object Next(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            var cursor = GetCursor(slowStorage, self);
            if (cursor == null) {
                return ValuesToValue(SlowStep(slowStorage, self, SlowAdvance), false);
            }
            return CursorStep(slowStorage, self, cursor, true);
        }

        [RubyMethod("peek")]
        public static object Peek(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            var cursor = GetCursor(slowStorage, self);
            if (cursor == null) {
                return ValuesToValue(SlowStep(slowStorage, self, SlowPeek), true);
            }
            object value = CursorStep(slowStorage, self, cursor, false);
            return cursor.Multiple ? new RubyArray((RubyArray)value) : value;
        }

        [RubyMethod("next_values")]
        public static RubyArray/*!*/ NextValues(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            var cursor = GetCursor(slowStorage, self);
            if (cursor == null) {
                return SlowStep(slowStorage, self, SlowAdvance);
            }
            return CursorValues(cursor, CursorStep(slowStorage, self, cursor, true), false);
        }

        [RubyMethod("peek_values")]
        public static RubyArray/*!*/ PeekValues(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ slowStorage, Enumerator/*!*/ self) {
            var cursor = GetCursor(slowStorage, self);
            if (cursor == null) {
                return new RubyArray(SlowStep(slowStorage, self, SlowPeek));
            }
            return CursorValues(cursor, CursorStep(slowStorage, self, cursor, false), true);
        }

        /// <summary>
        /// The value the suspended yield inside the method will answer (the next #next lets it go
        /// on); one at a time.
        /// </summary>
        [RubyMethod("feed")]
        public static object Feed(RubyContext/*!*/ context, Enumerator/*!*/ self, object value) {
            if (context.IsObjectFrozen(self)) {
                throw RubyExceptions.CreateObjectFrozenError(context, self);
            }
            if (self._hasFeed) {
                throw RubyExceptions.CreateTypeError("feed value already set");
            }
            self._feedValue = value;
            self._hasFeed = true;
            return null;
        }

        /// <summary>
        /// Kernel#loop's `rescue StopIteration => stop; stop.result`, without the `=> stop`: that hands the exception to Ruby code,
        /// which takes the walk of the stack for its backtrace (RubyExceptionData.TryDeferCatchSite), and the walk is most of what
        /// ending a `loop { e.next }` costs. The result is read here from the exception being rescued ($!), which nothing sees;
        /// unless #result is not the prelude's own (redefined, or a subclass's or a singleton's), which is then called with the
        /// exception, exposed first as `=> stop` would have.
        /// </summary>
        [RubyMethod("__rescued_stop_result__", RubyMethodAttributes.PublicSingleton)]
        public static object GetRescuedStopResult(CallSiteStorage<Func<CallSite, object, object>>/*!*/ resultStorage, RubyClass/*!*/ self) {
            RubyContext context = resultStorage.Context;
            Exception stop = context.CurrentExceptionNoExpose;
            object result;
            if (_fastNext && stop is StopIteration &&
                IsPreludeMethod(context, stop, "result", GetPreludeMethods(context).StopIterationResult)) {
                return context.TryGetInstanceVariable(stop, RubyUtils.GetCoreStateInstanceVariableName("@__result__"), out result) ? result : null;
            }
            var site = resultStorage.GetCallSite("result", 0);
            return site.Target(site, context.CurrentException);
        }

        /// <summary>
        /// For the fiber: takes the fed value, if any, as the suspended yield returns.
        /// </summary>
        [RubyMethod("__iter_take_feed__", RubyMethodAttributes.PrivateInstance)]
        public static object TakeFeed(Enumerator/*!*/ self) {
            object value = self._feedValue;
            self._feedValue = null;
            self._hasFeed = false;
            return value;
        }

        /// <summary>
        /// Forgets the external iteration, whichever way it ran (#rewind, #initialize_copy). A hash a
        /// cursor was walking stays iterated, as MRI's stays for a fiber nobody resumes.
        /// </summary>
        [RubyMethod("__iter_reset__", RubyMethodAttributes.PrivateInstance)]
        public static object ResetExternalIteration(Enumerator/*!*/ self) {
            self._cursor = null;
            self._onFiber = false;
            self._feedValue = null;
            self._hasFeed = false;
            return null;
        }

        /// <summary>
        /// Whether a cursor is in the middle of an iteration: started, and not at its end. Nil when
        /// the iteration is the fiber's to tell.
        /// </summary>
        [RubyMethod("__iter_cursor_in_progress__", RubyMethodAttributes.PrivateInstance)]
        public static object IsCursorInProgress(Enumerator/*!*/ self) {
            var cursor = self._cursor;
            if (cursor == null) {
                return null;
            }
            return ScriptingRuntimeHelpers.BooleanToObject((cursor.Started || cursor.Peeked) && !cursor.Done);
        }

        #endregion
    }
}
