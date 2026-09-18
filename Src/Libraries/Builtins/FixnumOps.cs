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
using IronRuby.Runtime;
using Microsoft.Scripting;

namespace IronRuby.Builtins {
    // Was the Fixnum class.  Fixnum and Bignum were unified into Integer in Ruby 2.4 and
    // removed in 3.2, so these are int-typed overloads on Integer itself now.
    public partial class Integer {
        #region to_sym

        /// <summary>
        /// Returns the Symbol whose integer value is self
        /// <seealso cref="FixnumOps.id2name"/>
        /// </summary>
        /// <returns>Symbol or nil if there is no symbol with id of self</returns>
        /// <example>
        /// fred = :fred.to_i
        /// fred.id2name   #=> "fred"
        /// fred.to_sym    #=> :fred
        /// </example>
        [RubyMethod("to_sym")]
        public static object ToSymbol(RubyContext/*!*/ context, int self) {
            return context.FindSymbol(self);
        }

        #endregion

        #region id2name

        /// <summary>
        /// Returns the name of the object whose symbol id is self.
        /// </summary>
        /// <returns>MutableString or nil if there is no symbol with id of self.</returns>
        /// <example>
        /// symbol = :@inst_var    #=> :@inst_var
        /// id     = symbol.to_i   #=> 9818
        /// id.id2name             #=> "@inst_var"
        /// </example>
        [RubyMethod("id2name")]
        public static object Id2Name(RubyContext/*!*/ context, int self) {
            var symbol = context.FindSymbol(self);
            return symbol != null ? symbol.String.Clone() : null;
        }

        #endregion

        [RubyMethod("size")]
        public static int Size(int self) {
            // the size of MRI's fixnum on a 64-bit machine: a C long
            return sizeof(long);
        }
    }
}
