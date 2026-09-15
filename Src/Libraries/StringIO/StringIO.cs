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

using System.Runtime.InteropServices;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Actions;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Utils;
using System;
using System.IO;
using System.Numerics;
using System.Diagnostics;
using System.Collections;

namespace IronRuby.StandardLibrary.StringIO {
    [RubyClass("StringIO", Inherits = typeof(object)), Includes(typeof(Enumerable))]
    public class StringIO {
        private MutableString/*!*/ _content;
        private int _position;
        private IOMode _mode;
        private int _lineNumber;

        // What #set_encoding was told, when it was told anything. Normally the encoding is the
        // string's own, but the string may be frozen - and MRI still remembers the answer then
        // rather than refusing, so it cannot live only on the string.
        private RubyEncoding _externalEncoding;

        public StringIO()
            : this(MutableString.CreateEmpty(), IOMode.ReadWrite) {
        }

        public StringIO(RubyContext/*!*/ context)
            : this(MutableString.CreateEmpty(context.DefaultExternalEncoding), IOMode.ReadWrite) {
        }

        public StringIO(MutableString/*!*/ content, IOMode mode) {
            ContractUtils.RequiresNotNull(content, "content");
            _content = content;
            _mode = mode;
        }

        private void SetPosition(long value) {
            if (value < 0 || value > Int32.MaxValue) {
                throw RubyExceptions.CreateEINVAL();
            }
            _position = (int)value; 
        }

        private void SetContent(MutableString/*!*/ content) {
            _content = content;
            _position = 0;
            _lineNumber = 0;
            _externalEncoding = null;
        }

        private MutableString/*!*/ GetContent() {
            if (_mode.IsClosed()) {
                throw RubyExceptions.CreateIOError("closed stream");
            }
            return _content;
        }

        private MutableString/*!*/ GetReadableContent() {
            if (!_mode.CanRead()) {
                throw RubyExceptions.CreateIOError("not opened for reading");
            }
            return _content;
        }

        private MutableString/*!*/ GetWritableContent() {
            if (!_mode.CanWrite()) {
                throw RubyExceptions.CreateIOError("not opened for writing");
            }
            return _content;
        }

        private void Close() {
            _mode = _mode.Close();
        }

        private static MutableString/*!*/ CheckContent(MutableString/*!*/ content, IOMode mode) {
            if (content.IsFrozen && mode.CanWrite()) {
                throw Errno.CreateEACCES("Permission denied");
            }

            if ((mode & IOMode.Truncate) != 0) {
                content.Clear();
            }
            return content;
        }

        #region Construction

        [RubyConstructor]
        public static StringIO/*!*/ Create(RubyClass/*!*/ self) {
            return new StringIO(self.Context);
        }

        [RubyConstructor]
        public static StringIO/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ initialString,
            [DefaultProtocol, Optional, NotNull]MutableString mode) {

            IOMode ioMode = IOModeEnum.Parse(mode, initialString.IsFrozen ? IOMode.ReadOnly : IOMode.ReadWrite) | IOMode.PreserveEndOfLines;
            return new StringIO(CheckContent(initialString, ioMode), ioMode);
        }

        [RubyConstructor]
        public static StringIO/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString initialString, int mode) {
            IOMode ioMode = (IOMode)mode | IOMode.PreserveEndOfLines;
            return new StringIO(CheckContent(initialString, ioMode), ioMode);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static StringIO/*!*/ Reinitialize(RubyContext/*!*/ context, StringIO/*!*/ self) {
            self.SetContent(MutableString.CreateEmpty(context.DefaultExternalEncoding));
            self._mode = IOMode.ReadWrite;
            return self;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static StringIO/*!*/ Reinitialize(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ content,
            [DefaultProtocol, Optional, NotNull]MutableString mode) {
            IOMode ioMode = IOModeEnum.Parse(mode, content.IsFrozen ? IOMode.ReadOnly : IOMode.ReadWrite) | IOMode.PreserveEndOfLines;
            self.SetContent(CheckContent(content, ioMode));
            self._mode = ioMode;
            return self;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static StringIO/*!*/ Reinitialize(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ content, int mode) {
            IOMode ioMode = (IOMode)mode | IOMode.PreserveEndOfLines;
            self.SetContent(CheckContent(content, ioMode));
            self._mode = ioMode;
            return self;
        }

        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ Open() {
            return RubyIOOps.Open();
        }

        #endregion

        #region reopen

        [RubyMethod("reopen")]
        public static StringIO/*!*/ Reopen(StringIO/*!*/ self) {
            self.SetContent(MutableString.CreateBinary());
            self._mode = IOMode.ReadWrite;
            return self;
        }

        [RubyMethod("reopen")]
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static StringIO/*!*/ Reopen(RespondToStorage/*!*/ respondToStorage, UnaryOpStorage/*!*/ toStringIoStorage,
            StringIO/*!*/ self, [NotNull]object/*!*/ other) {

            if (!Protocols.RespondTo(respondToStorage, other, "to_strio")) {
                throw RubyExceptions.CreateImplicitConversionError(respondToStorage.Context.GetClassName(other), "StringIO");
            }

            var site = toStringIoStorage.GetCallSite("to_strio", 0);
            var strio = site.Target(site, other) as StringIO;
            if (strio == null) {
                throw RubyExceptions.CreateTypeError("C#to_strio should return StringIO");
            }

            return Reopen(respondToStorage.Context, self, strio);
        }

        [RubyMethod("reopen")]
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static StringIO/*!*/ Reopen(RubyContext/*!*/ context, [NotNull]StringIO/*!*/ self, [NotNull]StringIO/*!*/ other) {
            self.SetContent(other._content);
            self._mode = other._mode;
            self._lineNumber = other._lineNumber;
            self._position = other._position;

            // TODO: this seems to be MRI bug
            // Shouldn't StringIO's taint be always same as the underlying string's taint?
            context.TaintObjectBy(self, other);
            return self;
        }

        [RubyMethod("reopen")]
        public static StringIO/*!*/ Reopen(StringIO/*!*/ self, [NotNull]MutableString/*!*/ content) {
            return Reopen(self, content, null);
        }

        [RubyMethod("reopen")]
        public static StringIO/*!*/ Reopen(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ content,
            [DefaultProtocol, NotNull]MutableString mode) {
            IOMode ioMode = IOModeEnum.Parse(mode, content.IsFrozen ? IOMode.ReadOnly : IOMode.ReadWrite) | IOMode.PreserveEndOfLines;
            self.SetContent(CheckContent(content, ioMode));
            self._mode = ioMode;
            return self;
        }

        [RubyMethod("reopen")]
        public static StringIO/*!*/ Reopen(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ content, int mode) {
            IOMode ioMode = (IOMode)mode | IOMode.PreserveEndOfLines;
            self.SetContent(CheckContent(content, ioMode));
            self._mode = ioMode;
            return self;
        }

        #endregion

        #region close(_read|_write), closed(_read|_write)?

        [RubyMethod("close")]
        public static void Close(StringIO/*!*/ self) {
            self.GetContent();
            self.Close();
        }

        [RubyMethod("close_read")]
        public static void CloseRead(StringIO/*!*/ self) {
            self.GetReadableContent();
            self._mode = self._mode.CloseRead();
        }

        [RubyMethod("close_write")]
        public static void CloseWrite(StringIO/*!*/ self) {
            self.GetWritableContent();
            self._mode = self._mode.CloseWrite();
        }

        [RubyMethod("closed?")]
        public static bool IsClosed(StringIO/*!*/ self) {
            return self._mode.IsClosed();
        }

        [RubyMethod("closed_read?")]
        public static bool IsClosedRead(StringIO/*!*/ self) {
            return !self._mode.CanRead();
        }

        [RubyMethod("closed_write?")]
        public static bool IsClosedWrite(StringIO/*!*/ self) {
            return !self._mode.CanWrite();
        }

        #endregion

        #region length, size, pos, tell, truncate, eof, eof?, rewind, seek

        [RubyMethod("length")]
        [RubyMethod("size")]
        public static int GetLength(StringIO/*!*/ self) {
            return self.GetContent().GetByteCount();
        }

        [RubyMethod("pos")]
        [RubyMethod("tell")]
        public static int GetPosition(StringIO/*!*/ self) {
            return self._position;
        }

        [RubyMethod("pos=")]
        public static void Pos(StringIO/*!*/ self, [DefaultProtocol]int pos) {
            self.SetPosition(pos);
        }

        [RubyMethod("truncate")]
        public static object SetLength(ConversionStorage<int>/*!*/ fixnumCast, StringIO/*!*/ self, object lengthObj) {
            int length = Protocols.CastToFixnum(fixnumCast, lengthObj);
            if (length < 0) {
                throw RubyExceptions.CreateEINVAL("negative length");
            }
            self.GetWritableContent().SetByteCount(length);
            return lengthObj;
        }

        [RubyMethod("rewind")]
        public static int Rewind(StringIO/*!*/ self) {
            self.GetContent();
            self._position = 0;
            self._lineNumber = 0;
            return 0;
        }

        [RubyMethod("seek")]
        public static int Seek(StringIO/*!*/ self, [DefaultProtocol]int pos, [DefaultProtocol, DefaultParameterValue(RubyIO.SEEK_SET)]int seekOrigin) {
            self.SetPosition(RubyIO.GetSeekPosition(
                self._content.GetByteCount(), self._position, pos, RubyIO.ToSeekOrigin(seekOrigin)
            ));
            return 0;
        }

        [RubyMethod("eof")]
        [RubyMethod("eof?")]
        public static bool Eof(StringIO/*!*/ self) {
            var context = self.GetReadableContent();
            return self._position >= context.GetByteCount();
        }

        #endregion

        #region string, string=

        [RubyMethod("string")]
        public static MutableString/*!*/ GetString(StringIO/*!*/ self) {
            return self._content;
        }

        [RubyMethod("string=")]
        public static MutableString/*!*/ SetString(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            self.SetContent(str);
            return str;
        }

        #endregion

        #region <<, print, putc, puts

        [RubyMethod("<<")]
        public static object/*!*/ Output(BinaryOpStorage/*!*/ writeStorage, object/*!*/ self, object value) {
            return PrintOps.Output(writeStorage, self, value);
        }

        [RubyMethod("print")]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, RubyScope/*!*/ scope, object self) {
            Print(writeStorage, self, scope.GetInnerMostClosureScope().LastInputLine);
        }

        [RubyMethod("print")]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object self, params object[]/*!*/ args) {
            // MRI: StringIO#print is different from PrintOps.Print - it doesn't output delimiter after each arg.
            MutableString delimiter = writeStorage.Context.OutputSeparator;

            foreach (object arg in args) {
                Protocols.Write(writeStorage, self, arg ?? MutableString.CreateAscii("nil"));
            }

            if (delimiter != null) {
                Protocols.Write(writeStorage, self, delimiter);
            }
        }

        [RubyMethod("print")]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object/*!*/ self, object value) {
            Protocols.Write(writeStorage, self, value ?? MutableString.CreateAscii("nil"));

            MutableString delimiter = writeStorage.Context.OutputSeparator;
            if (delimiter != null) {
                Protocols.Write(writeStorage, self, delimiter);
            }
        }

        [RubyMethod("putc")]
        public static MutableString/*!*/ Putc(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ val) {
            return PrintOps.Putc(writeStorage, self, val);
        }

        [RubyMethod("putc")]
        public static object Putc(ConversionStorage<int>/*!*/ fixnumCast, BinaryOpStorage/*!*/ writeStorage, object self, object c) {
            return PrintOps.Putc(fixnumCast, writeStorage, self, c);
        }

        [RubyMethod("puts")]
        public static void PutsEmptyLine(BinaryOpStorage/*!*/ writeStorage, object self) {
            PrintOps.PutsEmptyLine(writeStorage, self);
        }

        [RubyMethod("puts")]
        public static void Puts(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ str) {
            PrintOps.Puts(writeStorage, self, str);
        }

        [RubyMethod("puts")]
        public static void Puts(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion, 
            ConversionStorage<IList>/*!*/ tryToAry, object self, [NotNull]object/*!*/ val) {

            PrintOps.Puts(writeStorage, tosConversion, tryToAry, self, val);
        }

        [RubyMethod("puts")]
        public static void Puts(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            ConversionStorage<IList>/*!*/ tryToAry, object self, params object[]/*!*/ vals) {

            PrintOps.Puts(writeStorage, tosConversion, tryToAry, self, vals);
        }

        [RubyMethod("printf")]
        public static void PrintFormatted(
            StringFormatterSiteStorage/*!*/ storage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            BinaryOpStorage/*!*/ writeStorage,
            StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {

            PrintOps.PrintFormatted(storage, stringCast, writeStorage, self, format, args);
        }

        #endregion

        #region write, syswrite

        [RubyMethod("write")]
        [RubyMethod("syswrite")]
        public static int Write(StringIO/*!*/ self, [NotNull]MutableString/*!*/ value) {
            var content = self.GetWritableContent();
            int length = content.GetByteCount();
            var bytesWritten = value.GetByteCount();
            int pos;

            if ((self._mode & IOMode.WriteAppends) != 0) {
                pos = length;
            } else {
                pos = self._position;
            }

            try {
                content.WriteBytes(pos, value, 0, bytesWritten);
            } catch (InvalidOperationException) {
                throw RubyExceptions.CreateIOError("not modifiable string");
            }

            content.TaintBy(value);
            self._position = pos + bytesWritten;
            return bytesWritten;
        }

        [RubyMethod("write")]
        [RubyMethod("syswrite")]
        public static int Write(ConversionStorage<MutableString>/*!*/ tosConversion, StringIO/*!*/ self, object obj) {
            return Write(self, Protocols.ConvertToString(tosConversion, obj));
        }

        #endregion

        #region read, sysread

        [RubyMethod("read")]
        public static MutableString/*!*/ Read(StringIO/*!*/ self, [Optional]DynamicNull bytes) {
            return Read(self, null, false);
        }

        [RubyMethod("read")]
        public static MutableString/*!*/ Read(StringIO/*!*/ self, DynamicNull bytes, [DefaultProtocol, NotNull]MutableString buffer) {
            return Read(self, buffer, false);
        }

        public static MutableString/*!*/ Read(StringIO/*!*/ self, MutableString buffer, bool eofError) {
            var content = self.GetReadableContent();
            int start = self._position;
            int length = content.GetByteCount();

            if (buffer != null) {
                buffer.Clear();
            } else {
                buffer = MutableString.CreateBinary();
            }

            if (start < length) {
                self._position = length;
                buffer.Append(content, start, length - start).TaintBy(content);
            } else if (eofError) {
                throw new EOFError("end of file reached");
            }

            return buffer;
        }

        [RubyMethod("read")]
        public static MutableString Read(StringIO/*!*/ self, [DefaultProtocol]int count, [DefaultProtocol, Optional, NotNull]MutableString buffer) {
            var content = self.GetReadableContent();
            if (count < 0) {
                throw RubyExceptions.CreateArgumentError("negative length -1 given");
            }

            if (buffer != null) {
                buffer.Clear();
            }

            int length = content.GetByteCount();
            if (self._position >= length) {
                return null;
            }

            if (buffer == null) {
                buffer = MutableString.CreateBinary();
            }

            int bytesRead = Math.Min(count, length - self._position);
            buffer.Append(content, self._position, bytesRead).TaintBy(content);
            self._position += bytesRead;
            return buffer;
        }

        [RubyMethod("sysread")]
        public static MutableString/*!*/ SystemRead(StringIO/*!*/ self, [Optional]DynamicNull bytes) {
            return Read(self, null, true);
        }

        [RubyMethod("sysread")]
        public static MutableString/*!*/ SystemRead(StringIO/*!*/ self, DynamicNull bytes, [DefaultProtocol, NotNull]MutableString buffer) {
            return Read(self, buffer, true);
        }

        [RubyMethod("sysread")]
        public static MutableString/*!*/ SystemRead(StringIO/*!*/ self, [DefaultProtocol]int bytes, [DefaultProtocol, Optional, NotNull]MutableString buffer) {
            var result = Read(self, bytes, buffer);
            if (result == null) {
                throw new EOFError("end of file reached");
            }
            return result;
        }

        #endregion

        #region getc, getbyte (1.9), ungetc, ungetbyte (1.9), readchar, readbyte (1.9)

        [RubyMethod("getc")]
        public static object GetByte(StringIO/*!*/ self) {
            var content = self.GetReadableContent();

            if (self._position >= content.GetByteCount()) {
                return null;
            }

            return ScriptingRuntimeHelpers.Int32ToObject(content.GetByte(self._position++));
        }

        [RubyMethod("ungetc")]
        public static void SetPreviousByte(StringIO/*!*/ self, [DefaultProtocol]int b) {
            // MRI: this checks if the IO is readable although it actually modifies the string:
            MutableString content = self.GetReadableContent();

            int pos = self._position - 1;
            if (pos >= 0) {
                int length = content.GetByteCount();
                try {
                    if (pos >= length) {
                        content.Append(0, pos - length);
                        content.Append(unchecked((byte)b));
                    } else {
                        content.SetByte(pos, unchecked((byte)b));
                    }
                    self._position = pos;
                } catch (InvalidOperationException) {
                    throw RubyExceptions.CreateIOError("not modifiable string");
                }
            }
        }

        // returns a string in 1.9
        [RubyMethod("readchar")]
        public static int ReadChar(StringIO/*!*/ self) {
            var content = self.GetReadableContent();
            int length = content.GetByteCount();

            if (self._position >= length) {
                throw new EOFError("end of file reached");
            }

            return content.GetByte(self._position++);
        }

        #endregion

        #region gets, readline, readlines

        //
        // Note
        //
        // MRI: the behavior of IO#readline and StringIO#readline is different.
        // StringIO doesn't normalize EOLNs.
        // Also, gets/readlines increment _lineNumber field instead of global $. variable.
        // 

        [RubyMethod("lineno")]
        public static int GetLineNo(StringIO/*!*/ self) {
            return self._lineNumber;
        }

        [RubyMethod("lineno=")]
        public static void SetLineNo(StringIO/*!*/ self, [DefaultProtocol]int value) {
            self._lineNumber = value;
        }

        [RubyMethod("gets")]
        public static MutableString Gets(RubyScope/*!*/ scope, StringIO/*!*/ self) {
            return Gets(scope, self, scope.RubyContext.InputSeparator, -1);
        }

        [RubyMethod("gets")]
        public static MutableString Gets(RubyScope/*!*/ scope, StringIO/*!*/ self, DynamicNull separator) {
            return Gets(scope, self, null, -1);
        }

        [RubyMethod("gets")]
        public static MutableString Gets(RubyScope/*!*/ scope, StringIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            if (separatorOrLimit.IsFixnum()) {
                return Gets(scope, self, scope.RubyContext.InputSeparator, separatorOrLimit.Fixnum());
            } else {
                return Gets(scope, self, separatorOrLimit.String(), -1);
            }
        }

        [RubyMethod("gets")]
        public static MutableString Gets(RubyScope/*!*/ scope, StringIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {
            var content = self.GetReadableContent();

            // TODO: limit

            int position = self._position;
            MutableString result = ReadLine(content, separator, ref position);
            self._position = position;

            scope.GetInnerMostClosureScope().LastInputLine = result;
            self._lineNumber++;

            return result;
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(RubyScope/*!*/ scope, StringIO/*!*/ self) {
            return ReadLine(scope, self, scope.RubyContext.InputSeparator, -1);
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(RubyScope/*!*/ scope, StringIO/*!*/ self, DynamicNull separator) {
            return ReadLine(scope, self, null, -1);
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(RubyScope/*!*/ scope, StringIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            if (separatorOrLimit.IsFixnum()) {
                return ReadLine(scope, self, scope.RubyContext.InputSeparator, separatorOrLimit.Fixnum());
            } else {
                return ReadLine(scope, self, separatorOrLimit.String(), -1);
            }
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(RubyScope/*!*/ scope, StringIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {

            // no dynamic call, modifies $_ scope variable:
            MutableString result = Gets(scope, self, separator, limit);
            if (result == null) {
                throw new EOFError("end of file reached");
            }

            return result;
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(RubyContext/*!*/ context, StringIO/*!*/ self) {
            return ReadLines(self, context.InputSeparator, -1);
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(RubyContext/*!*/ context, StringIO/*!*/ self, DynamicNull separator) {
            return ReadLines(self, null, -1);
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(RubyContext/*!*/ context, StringIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            if (separatorOrLimit.IsFixnum()) {
                return ReadLines(self, context.InputSeparator, separatorOrLimit.Fixnum());
            } else {
                return ReadLines(self, separatorOrLimit.String(), -1);
            }
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(StringIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {
            var content = self.GetReadableContent();
            RubyArray result = new RubyArray();

            // TODO: limit

            // no dynamic call, doesn't modify $_ scope variable:
            MutableString line;
            int position = self._position;
            while ((line = ReadLine(content, separator, ref position)) != null) {
                result.Add(line);
                self._lineNumber++;
            }
            self._position = position;
            return result;
        }

        private static readonly byte[] ParagraphSeparator = new byte[] { (byte)'\n', (byte)'\n' };

        private static MutableString ReadLine(MutableString/*!*/ content, MutableString separator, ref int position) {
            int length = content.GetByteCount();
            if (position >= length) {
                return null;
            }

            int oldPosition = position;

            if (separator == null) {
                position = length;
            } else if (separator.IsEmpty) {
                // skip initial ends of line:
                while (oldPosition < length && content.GetByte(oldPosition) == '\n') {
                    oldPosition++;
                }

                position = content.IndexOf(ParagraphSeparator, oldPosition);
                position = (position != -1) ? position + 1 : length;
            } else {
                position = content.IndexOf(separator, oldPosition);
                position = (position != -1) ? position + separator.Length : length;
            }

            return content.GetSlice(oldPosition, position - oldPosition);
        }

        #endregion

        #region each, each_line, each_byte, each_char (1.9)

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object EachLine(RubyContext/*!*/ context, BlockParam block, StringIO/*!*/ self) {
            return EachLine(block, self, context.InputSeparator, -1);
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object EachLine(RubyContext/*!*/ context, BlockParam block, StringIO/*!*/ self, DynamicNull separator) {
            return EachLine(block, self, null, -1);
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object EachLine(RubyContext/*!*/ context, BlockParam block, StringIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            if (separatorOrLimit.IsFixnum()) {
                return EachLine(block, self, context.InputSeparator, separatorOrLimit.Fixnum());
            } else {
                return EachLine(block, self, separatorOrLimit.String(), -1);
            }
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object EachLine(BlockParam block, StringIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {
            // TODO: improve MSOps.EachLine
            // TODO: limit
            var content = self.GetReadableContent();
            var result = MutableStringOps.EachLine(block, content, separator, self._position);
            return ReferenceEquals(result, content) ? self : result;
        }

        [RubyMethod("each_byte")]
        public static object EachByte(BlockParam block, StringIO/*!*/ self) {
            MutableString content;
            int pos;
            while ((pos = self._position) < (content = self.GetReadableContent()).GetByteCount()) {
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }

                self._position++;

                object result;
                if (block.Yield(ScriptingRuntimeHelpers.Int32ToObject(content.GetByte(pos)), out result)) {
                    return result;
                }
            }
            return null;
        }

        #endregion
        
        #region ungetc, ungetbyte primitive

        /// <summary>
        /// What #ungetc and #ungetbyte are built on. Both write backwards over the string - the
        /// position moves back by as much as is pushed and those bytes replace what was there -
        /// which is MRI's behaviour rather than a pushback buffer of its own. Pushing back past
        /// the start moves the rest of the string along instead of dropping what does not fit.
        /// </summary>
        [RubyMethod("__ir_unget_bytes__", RubyMethodAttributes.PrivateInstance)]
        public static void UngetBytes(StringIO/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ pushed) {
            // MRI asks for a readable stream even though this writes to the string:
            MutableString content = self.GetReadableContent();
            int count = pushed.GetByteCount();
            if (count == 0) {
                return;
            }

            try {
                int position = self._position;
                int length = content.GetByteCount();
                if (position < count) {
                    MutableString tail = content.GetSlice(position, length - position);
                    content.SetByteCount(0);
                    content.WriteBytes(0, pushed, 0, count);
                    if (tail != null && tail.GetByteCount() > 0) {
                        content.WriteBytes(count, tail, 0, tail.GetByteCount());
                    }
                    self._position = 0;
                } else {
                    if (position > length) {
                        content.Append(0, position - length);
                    }
                    content.WriteBytes(position - count, pushed, 0, count);
                    self._position = position - count;
                }
            } catch (InvalidOperationException) {
                throw RubyExceptions.CreateIOError("not modifiable string");
            }
        }

        #endregion

        #region external_encoding, internal_encoding, set_encoding (1.9)

        /// <summary>
        /// A StringIO's external encoding is its string's - there is no descriptor to carry one
        /// of its own - and it never transcodes, so the internal encoding is always nil.
        /// </summary>
        [RubyMethod("external_encoding")]
        public static RubyEncoding/*!*/ GetExternalEncoding(StringIO/*!*/ self) {
            return self._externalEncoding ?? self.GetContent().Encoding;
        }

        [RubyMethod("internal_encoding")]
        public static RubyEncoding GetInternalEncoding(StringIO/*!*/ self) {
            return null;
        }

        // Separate arities rather than [Optional]: an omitted [Optional]object arrives as
        // Missing.Value, which is neither nil nor null and has caught this codebase out before.
        [RubyMethod("set_encoding")]
        public static StringIO/*!*/ SetEncoding(ConversionStorage<MutableString>/*!*/ toStr, StringIO/*!*/ self, object external) {
            return SetExternalEncoding(toStr, self, external);
        }

        [RubyMethod("set_encoding")]
        public static StringIO/*!*/ SetEncoding(ConversionStorage<MutableString>/*!*/ toStr, StringIO/*!*/ self,
            object external, object @internal) {
            return SetExternalEncoding(toStr, self, external);
        }

        [RubyMethod("set_encoding")]
        public static StringIO/*!*/ SetEncoding(ConversionStorage<MutableString>/*!*/ toStr, StringIO/*!*/ self,
            object external, object @internal, object options) {
            return SetExternalEncoding(toStr, self, external);
        }

        /// <summary>
        /// The encoding belongs to the string, so this is String#force_encoding on it. There is
        /// nothing here to transcode between, which is why the internal encoding and the
        /// conversion options are accepted and then ignored, as MRI's stringio does.
        /// </summary>
        private static StringIO/*!*/ SetExternalEncoding(ConversionStorage<MutableString>/*!*/ toStr, StringIO/*!*/ self, object external) {
            MutableString content = self.GetContent();
            if (external == null) {
                return self;
            }

            RubyEncoding encoding = Protocols.ConvertToEncoding(toStr, external);
            self._externalEncoding = encoding;
            // The encoding belongs to the string, so tell the string too - unless it is frozen,
            // which MRI does not treat as a failure: the StringIO keeps the answer either way.
            if (!content.IsFrozen) {
                content.ForceEncoding(encoding);
            }
            return self;
        }

        #endregion

        #region Stubs: binmode, fcntl, fileno, pid, fsync, sync, sync=, isatty, tty?, flush

        /// <summary>
        /// MRI's StringIO#binmode declares the string to be bytes - it sets the encoding to
        /// BINARY - rather than doing nothing at all, and #set_encoding_by_bom will not read a
        /// mark until that is true.
        /// </summary>
        [RubyMethod("binmode")]
        public static StringIO/*!*/ SetBinaryMode(StringIO/*!*/ self) {
            MutableString content = self.GetContent();
            self._externalEncoding = RubyEncoding.Binary;
            if (!content.IsFrozen) {
                content.ForceEncoding(RubyEncoding.Binary);
            }
            return self;
        }

        [RubyMethod("binmode?")]
        public static bool IsBinmode(StringIO/*!*/ self) {
            return ReferenceEquals(GetExternalEncoding(self), RubyEncoding.Binary);
        }

        [RubyMethod("__readable_stream__?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsReadableStream(StringIO/*!*/ self) {
            return self._mode.CanRead();
        }

        [RubyMethod("fcntl")]
        public static void FileControl(StringIO/*!*/ self) {
            throw new NotImplementedError();
        }

        [RubyMethod("fileno")]
        [RubyMethod("pid")]
        public static object GetDescriptor(StringIO/*!*/ self) {
            // nop
            return null;
        }

        [RubyMethod("fsync")]
        public static int FSync(StringIO/*!*/ self) {
            // nop
            return 0;
        }

        [RubyMethod("sync")]
        public static bool Sync(StringIO/*!*/ self) {
            // nop
            return true;
        }

        [RubyMethod("sync=")]
        public static bool SetSync(StringIO/*!*/ self, bool value) {
            // nop
            return value;
        }

        [RubyMethod("isatty")]
        [RubyMethod("tty?")]
        public static bool IsConsole(StringIO/*!*/ self) {
            // nop
            return false;
        }

        [RubyMethod("flush")]
        public static StringIO/*!*/ Flush(StringIO/*!*/ self) {
            // nop
            return self;
        }

        #endregion
    }
}
