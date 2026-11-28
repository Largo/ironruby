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

using Microsoft.Scripting;
using System.Diagnostics;

namespace IronRuby.Compiler.Ast {

    public abstract class CallExpression : Expression {
        /// <summary>
        /// The line of the method's name, where it is not the call's first line (`a\n  .b', `a.\n  b'): the line MRI
        /// reports for the call (prism's message_loc). 0: the call's first line.
        /// </summary>
        internal int MessageLine;

        internal int CallLine {
            get { return MessageLine > 0 ? MessageLine : Location.Start.Line; }
        }

        // null means no parameters, not even empty parenthesis 
        private readonly Arguments _args;
        private Block _block;

        public Arguments Arguments {
            get { return _args; }
        }

        public Block Block {
            get { return _block; }
            internal set {
                Debug.Assert(_block == null);
                _block = value; 
            }
        }

        protected CallExpression(Arguments args, Block block, SourceSpan location)
            : base(location) {
            _args = args;
            _block = block;
        }
    }
}
