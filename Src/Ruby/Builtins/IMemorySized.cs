/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

namespace IronRuby.Builtins {
    /// <summary>
    /// The bytes an object holds outside its slot, as a C extension's rb_data_type_t.dsize function
    /// reports them. ObjectSpace.memsize_of adds it to the slot, as MRI does.
    /// </summary>
    public interface IMemorySized {
        long ExtraMemorySize { get; }
    }
}
