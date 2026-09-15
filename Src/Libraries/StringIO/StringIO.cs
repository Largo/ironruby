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
using System.Collections.Generic;
using System.Reflection;

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

        /// <summary>
        /// A limit of zero would read an empty line for ever, so MRI refuses it where a reader
        /// loops rather than looping - the same message IO#each_line gives.
        /// </summary>
        private static void CheckLineLimit(int limit) {
            if (limit == 0) {
                throw RubyExceptions.CreateArgumentError("invalid limit: 0");
            }
        }

        /// <summary>
        /// The argument shapes the four line readers share: (), (separator), (limit),
        /// (separator, limit), with a chomp: keyword on any of them. A lone Integer is the limit
        /// rather than the separator - the one shape that arity cannot tell apart - and a nil
        /// separator means "read everything".
        /// </summary>
        private static void ParseLineArguments(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt, RubyContext/*!*/ context,
            object first, object second, IDictionary<object, object> options,
            out MutableString separator, out int limit, out bool chomp) {

            Protocols.TryConvertToOptions(toHash, ref options, ref first, ref second);

            separator = context.InputSeparator;
            limit = -1;
            chomp = false;

            object value;
            if (options != null && options.TryGetValue(context.CreateAsciiSymbol("chomp"), out value)) {
                chomp = RubyOps.IsTrue(value);
            }

            if (second != Missing.Value) {
                separator = (first == null) ? null : Protocols.CastToString(toStr, first);
                limit = (second == null) ? -1 : Protocols.CastToFixnum(toInt, second);
            } else if (first != Missing.Value) {
                if (first == null) {
                    separator = null;
                } else if (first is int) {
                    limit = (int)first;
                } else if (first is MutableString) {
                    separator = (MutableString)first;
                } else {
                    // to_str is tried before to_int, which is the order MRI tries them in
                    MutableString asString = Protocols.TryCastToString(toStr, first);
                    if (asString != null) {
                        separator = asString;
                    } else {
                        limit = Protocols.CastToFixnum(toInt, first);
                    }
                }
            }
        }

        /// <summary>
        /// chomp takes off the separator that was actually found: a nil separator read to the end
        /// and has nothing to take off, and an empty one read a paragraph, whose separator is the
        /// run of blank lines that ended it.
        /// </summary>
        private static MutableString Chomp(MutableString line, MutableString separator, bool chomp) {
            if (!chomp || line == null || separator == null) {
                return line;
            }

            int length = line.GetByteCount();
            if (separator.IsEmpty) {
                int end = length;
                while (end > 0 && line.GetByte(end - 1) == '\n') {
                    end--;
                }
                return (end == length) ? line : line.GetSlice(0, end);
            }

            int separatorLength = separator.GetByteCount();
            if (separatorLength == 0 || length < separatorLength || !line.EndsWith(separator)) {
                return line;
            }
            return line.GetSlice(0, length - separatorLength);
        }

        [RubyMethod("gets")]
        public static MutableString Gets(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyScope/*!*/ scope, StringIO/*!*/ self,
            [Optional]object separatorOrLimit, [Optional]object limitOrOptions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            MutableString separator;
            int limit;
            bool chomp;
            ParseLineArguments(toHash, toStr, toInt, scope.RubyContext, separatorOrLimit, limitOrOptions, options,
                out separator, out limit, out chomp);

            var content = self.GetReadableContent();

            int position = self._position;
            MutableString result = Chomp(ReadLine(content, separator, limit, ref position), separator, chomp);
            self._position = position;

            // $_ is frame local, and the frame it belongs in is the caller's - which is why this
            // is not wrapped in Ruby the way the chomp: option once was.
            scope.GetInnerMostClosureScope().LastInputLine = result;
            self._lineNumber++;

            return result;
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyScope/*!*/ scope, StringIO/*!*/ self,
            [Optional]object separatorOrLimit, [Optional]object limitOrOptions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            // no dynamic call, modifies $_ scope variable:
            MutableString result = Gets(toHash, toStr, toInt, scope, self, separatorOrLimit, limitOrOptions, options);
            if (result == null) {
                throw new EOFError("end of file reached");
            }

            return result;
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyContext/*!*/ context, StringIO/*!*/ self,
            [Optional]object separatorOrLimit, [Optional]object limitOrOptions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            MutableString separator;
            int limit;
            bool chomp;
            ParseLineArguments(toHash, toStr, toInt, context, separatorOrLimit, limitOrOptions, options,
                out separator, out limit, out chomp);

            var content = self.GetReadableContent();
            CheckLineLimit(limit);
            RubyArray result = new RubyArray();

            // no dynamic call, doesn't modify $_ scope variable:
            MutableString line;
            int position = self._position;
            while ((line = ReadLine(content, separator, limit, ref position)) != null) {
                result.Add(Chomp(line, separator, chomp));
                self._lineNumber++;
            }
            self._position = position;
            return result;
        }

        private static readonly byte[] ParagraphSeparator = new byte[] { (byte)'\n', (byte)'\n' };

        /// <summary>
        /// One line: up to and including the separator, or <paramref name="limit"/> bytes,
        /// whichever ends first - a negative limit means there is none. A nil separator reads to
        /// the end of the string and an empty one reads a paragraph, skipping the blank lines in
        /// front of it and ending at the blank line after it.
        /// </summary>
        private static MutableString ReadLine(MutableString/*!*/ content, MutableString separator, int limit, ref int position) {
            int length = content.GetByteCount();
            if (position >= length) {
                return null;
            }

            int oldPosition = position;

            if (limit == 0) {
                // A limit of nothing answers an empty string and reads nothing, rather than nil.
                return content.GetSlice(oldPosition, 0);
            }

            if (separator == null) {
                position = length;
            } else if (separator.IsEmpty) {
                // skip initial ends of line:
                while (oldPosition < length && content.GetByte(oldPosition) == '\n') {
                    oldPosition++;
                }

                int terminator = content.IndexOf(ParagraphSeparator, oldPosition);
                if (terminator == -1) {
                    position = length;
                } else {
                    // A paragraph ends with the whole run of blank lines that closed it, not
                    // with the first newline of that run: "a\n\n\nb" reads as "a\n\n\n".
                    position = terminator + 1;
                    while (position < length && content.GetByte(position) == '\n') {
                        position++;
                    }
                }
            } else {
                position = content.IndexOf(separator, oldPosition);
                position = (position != -1) ? position + separator.Length : length;
            }

            // The limit counts bytes from where the line started and wins when it is the shorter
            // of the two: gets(">", 2) answers "th", not "this>".
            if (limit > 0 && position - oldPosition > limit) {
                position = oldPosition + limit;
            }

            return content.GetSlice(oldPosition, position - oldPosition);
        }

        #endregion

        #region each, each_line, each_byte, each_char (1.9)

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object EachLine(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyContext/*!*/ context, BlockParam block, StringIO/*!*/ self,
            [Optional]object separatorOrLimit, [Optional]object limitOrOptions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            MutableString separator;
            int limit;
            bool chomp;
            ParseLineArguments(toHash, toStr, toInt, context, separatorOrLimit, limitOrOptions, options,
                out separator, out limit, out chomp);

            var content = self.GetReadableContent();
            CheckLineLimit(limit);
            if (block == null) {
                throw RubyExceptions.NoBlockGiven();
            }

            // Reading through the stream rather than over the string, so that the position moves
            // as the lines are yielded and the limit means the same thing it does to #gets.
            MutableString line;
            while ((line = ReadLine(content, separator, limit, ref self._position)) != null) {
                self._lineNumber++;

                object result;
                if (block.Yield(Chomp(line, separator, chomp), out result)) {
                    return result;
                }
            }
            return self;
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
            return self;
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
