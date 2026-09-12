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
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using System.Runtime.InteropServices;


namespace IronRuby.Builtins {
    [Serializable]
    public class LocalJumpError : SystemException {
        [NonSerialized]
        private readonly RuntimeFlowControl _skipFrame;

        /// <summary>
        /// The exception cannot be rescued in this frame if set.
        /// </summary>
        internal RuntimeFlowControl SkipFrame {
            get { return _skipFrame; }
        }

        internal LocalJumpError(string/*!*/ message, RuntimeFlowControl/*!*/ skipFrame)
            : this(message, (Exception)null) {
            Assert.NotNull(message, skipFrame);
            _skipFrame = skipFrame;
        }

        public LocalJumpError() : this(null, (Exception)null) { }
        public LocalJumpError(string message) : this(message, (Exception)null) { }
        public LocalJumpError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected LocalJumpError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class SystemExit : Exception {
        private readonly int _status;

        public int Status {
            get { return _status; }
        }

        public SystemExit(int status, string message)
            : this(message) {
            _status = status;
        }

        public SystemExit(int status) 
            : this() {
            _status = status;
        }

        public SystemExit() : this(null, null) { }
        public SystemExit(string message) : this(message, null) { }
        public SystemExit(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected SystemExit(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class ScriptError : Exception {
        public ScriptError() : this(null, null) { }
        public ScriptError(string message) : this(message, null) { }
        public ScriptError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected ScriptError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class NotImplementedError : ScriptError {
        public NotImplementedError() : this(null, null) { }
        public NotImplementedError(string message) : this(message, null) { }
        public NotImplementedError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected NotImplementedError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class LoadError : ScriptError {
        public LoadError() : this(null, null) { }
        public LoadError(string message) : this(message, null) { }
        public LoadError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected LoadError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class SystemStackError : SystemException {
        public SystemStackError() : this(null, null) { }
        public SystemStackError(string message) : this(message, null) { }
        public SystemStackError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected SystemStackError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class RegexpError : SystemException {
        public RegexpError() : this(null, null) { }
        public RegexpError(string message) : this(message, null) { }
        public RegexpError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected RegexpError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class EncodingError : SystemException {
        public EncodingError() : this(null, null) { }
        public EncodingError(string message) : this(message, null) { }
        public EncodingError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected EncodingError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class EncodingCompatibilityError : EncodingError {
        public EncodingCompatibilityError() : this(null, null) { }
        public EncodingCompatibilityError(string message) : this(message, null) { }
        public EncodingCompatibilityError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected EncodingCompatibilityError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class UndefinedConversionError : EncodingError {
        public UndefinedConversionError() : this(null, null) { }
        public UndefinedConversionError(string message) : this(message, null) { }
        public UndefinedConversionError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected UndefinedConversionError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif

        /// <summary>The encoding the failing conversion stage read from. May be null.</summary>
        [NonSerialized]
        private RubyEncoding _sourceEncoding, _destinationEncoding;

        [NonSerialized]
        private byte[] _errorCharBytes;

        public RubyEncoding SourceEncoding { get { return _sourceEncoding; } set { _sourceEncoding = value; } }
        public RubyEncoding DestinationEncoding { get { return _destinationEncoding; } set { _destinationEncoding = value; } }

        /// <summary>The character that could not be represented, in the bytes of the stage's source encoding.</summary>
        public byte[] ErrorCharBytes { get { return _errorCharBytes; } set { _errorCharBytes = value; } }
    }

    [Serializable]
    public class InvalidByteSequenceError : EncodingError {
        public InvalidByteSequenceError() : this(null, null) { }
        public InvalidByteSequenceError(string message) : this(message, null) { }
        public InvalidByteSequenceError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected InvalidByteSequenceError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif

        [NonSerialized]
        private RubyEncoding _sourceEncoding, _destinationEncoding;

        [NonSerialized]
        private byte[] _errorBytes, _readAgainBytes;

        [NonSerialized]
        private bool _incompleteInput;

        public RubyEncoding SourceEncoding { get { return _sourceEncoding; } set { _sourceEncoding = value; } }
        public RubyEncoding DestinationEncoding { get { return _destinationEncoding; } set { _destinationEncoding = value; } }

        /// <summary>The bytes that were rejected.</summary>
        public byte[] ErrorBytes { get { return _errorBytes; } set { _errorBytes = value; } }

        /// <summary>The bytes that should be read again after the error.</summary>
        public byte[] ReadAgainBytes { get { return _readAgainBytes; } set { _readAgainBytes = value; } }

        /// <summary>True if the input ended in the middle of a character rather than being invalid.</summary>
        public bool IncompleteInput { get { return _incompleteInput; } set { _incompleteInput = value; } }
    }

    [Serializable]
    public class ConverterNotFoundError : EncodingError {
        public ConverterNotFoundError() : this(null, null) { }
        public ConverterNotFoundError(string message) : this(message, null) { }
        public ConverterNotFoundError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected ConverterNotFoundError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class RuntimeError : SystemException {
        public RuntimeError() : this(null, null) { }
        public RuntimeError(string message) : this(message, null) { }
        public RuntimeError(string message, Exception inner) : base(message, inner) { }

#if FEATURE_SERIALIZATION
        protected RuntimeError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    /// <summary>
    /// Ruby's FrozenError (a subclass of RuntimeError since Ruby 2.5). Declared here in the core
    /// assembly, not in Src/Libraries, so that RubyExceptions can throw it -- the core assembly
    /// cannot reference the library one. Registration lives in Src/Libraries/Builtins/Exceptions.cs.
    /// </summary>
    [Serializable]
    public class FrozenError : RuntimeError {
        private object _receiver;
        private bool _hasReceiver;

        public FrozenError() : this(null, null) { }
        public FrozenError(string message) : this(message, null) { }
        public FrozenError(string message, Exception inner) : base(message, inner) { }

        /// <summary>
        /// The object that was being modified, exposed as FrozenError#receiver.
        /// </summary>
        public object Receiver {
            get { return _receiver; }
        }

        public bool HasReceiver {
            get { return _hasReceiver; }
        }

        public FrozenError SetReceiver(object receiver) {
            _receiver = receiver;
            _hasReceiver = true;
            return this;
        }

#if FEATURE_SERIALIZATION
        protected FrozenError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class SyntaxError : ScriptError {
        private readonly string _file;
        private readonly string _lineSourceCode;
        private readonly int _line;
        private readonly int _column;
        private readonly bool _hasLineInfo;

        public SyntaxError() : this(null, null) { }
        public SyntaxError(string message) : this(message, null) { }
        public SyntaxError(string message, Exception inner) : base(message, inner) { }

        internal string File {
            get { return _file; }
        }

        internal int Line {
            get { return _line; }
        }

        internal int Column {
            get { return _column; }
        }

        internal string LineSourceCode {
            get { return _lineSourceCode; }
        }

        internal bool HasLineInfo {
            get { return _hasLineInfo; }
        }

        internal SyntaxError(string/*!*/ message, string file, int line, int column, string lineSourceCode) 
            : base(message) {
            _file = file;
            _line = line;
            _column = column;
            _lineSourceCode = lineSourceCode;
            _hasLineInfo = true;
        }

#if FEATURE_SERIALIZATION
        protected SyntaxError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class ExistError : ExternalException {
        private const string/*!*/ M = "File exists";

        public ExistError() : this(null, null) { }
        public ExistError(string message) : this(message, null) { }
        public ExistError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public ExistError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
        protected ExistError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class BadFileDescriptorError : ExternalException {
        private const string/*!*/ M = "Bad file descriptor";

        public BadFileDescriptorError() : this(null, null) { }
        public BadFileDescriptorError(string message) : this(message, null) { }
        public BadFileDescriptorError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public BadFileDescriptorError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
        protected BadFileDescriptorError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    /// <summary>
    /// Backs Errno::EISDIR. .NET has no "is a directory" exception -- opening a
    /// directory throws UnauthorizedAccessException, which maps to EACCES -- so the
    /// runtime needs a type of its own to raise. Registered in Errno.cs.
    /// </summary>
    [Serializable]
    public class DirectoryIsError : ExternalException {
        private const string/*!*/ M = "Is a directory";

        public DirectoryIsError() : this(null, null) { }
        public DirectoryIsError(string message) : this(message, null) { }
        public DirectoryIsError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public DirectoryIsError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
        protected DirectoryIsError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class ExecFormatError : ExternalException {
        private const string/*!*/ M = "Exec format error";

        public ExecFormatError() : this(null, null) { }
        public ExecFormatError(string message) : this(message, null) { }
        public ExecFormatError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public ExecFormatError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
        protected ExecFormatError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }

    [Serializable]
    public class InvalidError : ExternalException {
        private const string/*!*/ M = "Invalid argument";

        public InvalidError() : this(null, null) { }
        public InvalidError(string message) : this(message, null) { }
        public InvalidError(string message, Exception inner) : base(RubyExceptions.MakeMessage(message, M), inner) { }
        public InvalidError(MutableString message) : base(RubyExceptions.MakeMessage(ref message, M)) { RubyExceptionData.InitializeException(this, message); }

#if FEATURE_SERIALIZATION
        protected InvalidError(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
#endif
    }
}

