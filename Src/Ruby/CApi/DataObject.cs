/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using IronRuby.Builtins;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// A T_DATA object: an instance a C extension made with Data_Wrap_Struct or
    /// TypedData_Wrap_Struct. Its fields - dmark/dfree or the rb_data_type_t, and the data
    /// pointer - live in its handle's slot (CApi.Values.cs), where the C macros read them.
    /// </summary>
    public sealed class DataObject : RubyObject {
        internal readonly bool Typed;

        internal DataObject(RubyClass/*!*/ cls, bool typed)
            : base(cls) {
            Typed = typed;
        }

        /// <summary>
        /// dup and clone allocate through the class's allocator, as MRI's do; the extension's
        /// initialize_copy then copies the data.
        /// </summary>
        protected override RubyObject/*!*/ CreateInstance() {
            RubyClass cls = ImmediateClass.NominalClass;
            var allocator = cls.CAllocator;
            return allocator != null ? (RubyObject)allocator(cls) : new DataObject(cls, Typed);
        }
    }
}
