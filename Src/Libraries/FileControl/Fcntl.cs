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

using IronRuby.Runtime;
using IronRuby.Builtins;

namespace IronRuby.StandardLibrary.FileControl {

    [RubyModule("Fcntl")]
    public class Fcntl {

        // The commands are the Linux ones: IO#fcntl hands them to fcntl(2) as they are.
        [RubyConstant]
        public const int F_DUPFD = 0;

        [RubyConstant]
        public const int F_GETFD = 1;

        [RubyConstant]
        public const int F_SETFD = 2;

        [RubyConstant]
        public const int F_GETFL = 3;

        [RubyConstant]
        public const int F_SETFL = 4;

        [RubyConstant]
        public const int F_GETLK = 5;

        [RubyConstant]
        public const int F_SETLK = 6;

        [RubyConstant]
        public const int F_SETLKW = 7;

        [RubyConstant]
        public const int FD_CLOEXEC = 1;

        [RubyConstant]
        public const int F_RDLCK = 0;

        [RubyConstant]
        public const int F_WRLCK = 1;

        [RubyConstant]
        public const int F_UNLCK = 2;

        [RubyConstant]
        public const int O_CREAT = 0x0100;

        [RubyConstant]
        public const int O_EXCL = 0x0400;

        [RubyConstant]
        public const int O_TRUNC = 0x0200;

        [RubyConstant]
        public const int O_APPEND = 0x0008;

        [RubyConstant]
        public const int O_NONBLOCK = 0x01;

        [RubyConstant]
        public const int O_RDONLY = 0x0000;

        [RubyConstant]
        public const int O_RDWR = 0x0002;

        [RubyConstant]
        public const int O_WRONLY = 0x0001;

        [RubyConstant]
        public const int O_ACCMODE = O_RDONLY | O_WRONLY | O_RDWR;
    }
}
