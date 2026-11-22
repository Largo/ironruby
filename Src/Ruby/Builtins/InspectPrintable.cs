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

namespace IronRuby.Builtins {

    /// <summary>
    /// MRI's rb_enc_isprint for a character of an ASCII compatible encoding without Unicode's
    /// tables, as String#inspect asks it of a string in the encoding it answers in: a character
    /// of more than one byte always is; one byte above 0x7F is as Onigmo's ctype table for the
    /// encoding says (InspectPrintable.Generated.cs, read off CRuby).
    /// </summary>
    internal static partial class InspectPrintable {
        private static Dictionary<string, ulong[]> _masks;

        private static Dictionary<string, ulong[]>/*!*/ Masks {
            get {
                var masks = _masks;
                if (masks == null) {
                    masks = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
                    for (int i = 0; i < _tables.Length; i += 3) {
                        masks[(string)_tables[i]] = new[] { (ulong)_tables[i + 1], (ulong)_tables[i + 2] };
                    }
                    _masks = masks;
                }
                return masks;
            }
        }

        internal static bool IsPrintable(RubyEncoding/*!*/ encoding, byte[]/*!*/ character) {
            if (character.Length != 1) {
                return character.Length > 1;
            }
            int b = character[0];
            if (b < 0x80) {
                return b >= 0x20 && b < 0x7f;
            }
            ulong[] mask;
            if (!Masks.TryGetValue(encoding.Name, out mask)) {
                return false;
            }
            b -= 0x80;
            return (mask[b / 64] & (1UL << (b % 64))) != 0;
        }
    }
}
