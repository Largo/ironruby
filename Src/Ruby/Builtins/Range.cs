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
using System.Runtime.Serialization;
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;
using Microsoft.Scripting.Generation;
using System.Runtime.CompilerServices;
using IronRuby.Runtime.Calls;
using System.Text;

namespace IronRuby.Builtins {

    public partial class Range : IDuplicable, ISerializable {
        private object _begin;
        private object _end;
        private bool _excludeEnd;
        private bool _initialized;

        public object Begin { get { return _begin; } }
        public object End { get { return _end; } }
        public bool ExcludeEnd { get { return _excludeEnd; } }

        protected Range(SerializationInfo info, StreamingContext context) {
            _begin = info.GetValue("begin", typeof(object));
            _end = info.GetValue("end", typeof(object));
            _excludeEnd = info.GetBoolean("excl");
            _initialized = true;
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context) {
            info.AddValue("begin", _begin);
            info.AddValue("end", _end);
            info.AddValue("excl", _excludeEnd);
        }

        protected Range(Range/*!*/ range) {
            _begin = range._begin;
            _end = range._end;
            _excludeEnd = range._excludeEnd;
        }

        public Range() {
        }

        public Range(int begin, int end, bool excludeEnd) {
            _begin = begin;
            _end = end;
            _excludeEnd = excludeEnd;
            _initialized = true;
        }

        public Range(MutableString/*!*/ begin, MutableString/*!*/ end, bool excludeEnd) {
            _begin = begin;
            _end = end;
            _excludeEnd = excludeEnd;
            _initialized = true;
        }
        
        // Convience function for constructing from C#, calls initialize
        public Range(BinaryOpStorage/*!*/ comparisonStorage, 
            RubyContext/*!*/ context, object begin, object end, bool excludeEnd) {
            Initialize(comparisonStorage, context, begin, end, excludeEnd);
        }

        public void Initialize(BinaryOpStorage/*!*/ comparisonStorage,
            RubyContext/*!*/ context, object begin, object end, bool excludeEnd) {

            if (_initialized) {
                throw RubyExceptions.CreateNameError("`initialize' called twice");
            }

            // Range tests whether the items can be compared, and uses that to determine if the range is valid
            // Only a non-existent <=> method or a result of nil seems to trigger the exception.
            // A nil end (endless range, 2.6) or nil begin (beginless range, 2.7) skips the test.
            if (begin != null && end != null) {
                object compareResult;

                var site = comparisonStorage.GetCallSite("<=>");
                try {
                    compareResult = site.Target(site, begin, end);
                } catch (Exception) {
                    compareResult = null;
                }

                if (compareResult == null) {
                    throw RubyExceptions.CreateArgumentError("bad value for range");
                }
            }

            _begin = begin;
            _end = end;
            _excludeEnd = excludeEnd;
            _initialized = true;
        }

        protected virtual Range/*!*/ Copy() {
            return new Range(this);
        }

        // Range doesn't have "initialize_copy", it's entirely initialized in dup:
        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = Copy();
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        private string/*!*/ Separator {
            get { return _excludeEnd ? "..." : ".."; }
        }

        public override string/*!*/ ToString() {
            var result = new StringBuilder();
            result.Append(_begin.ToString());
            result.Append(Separator);
            result.Append(_end.ToString());
            return result.ToString();
        }

        public MutableString/*!*/ Inspect(RubyContext/*!*/ context) {
            var result = MutableString.CreateMutable(RubyEncoding.Binary);
            result.Append(context.Inspect(_begin));
            result.Append(Separator);
            result.Append(context.Inspect(_end));
            return result;
        }

        public MutableString/*!*/ ToMutableString(ConversionStorage<MutableString>/*!*/ tosConversion) {
            // String#to_s answers the string itself, so appending to what the conversion
            // returns rewrites the range's own endpoint: ("ab".."zz").to_s used to leave
            // the begin string reading "ab..zz".
            MutableString begin = Protocols.ConvertToString(tosConversion, _begin);
            MutableString end = Protocols.ConvertToString(tosConversion, _end);

            var result = MutableString.CreateMutable(begin.Encoding);
            result.Append(begin);
            result.Append(Separator);
            result.Append(end);
            return result;
        }
    }
}
