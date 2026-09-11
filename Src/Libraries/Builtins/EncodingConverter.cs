/* ****************************************************************************
 *
 * Encoding::Converter
 *
 * Ruby's transcoding engine implemented on top of System.Text.Encoder/Decoder.
 * A converter owns a conversion path - either a direct pair or
 * "source -> UTF-8 -> destination", mirroring CRuby's transcoder graph for the
 * pairs .NET can express - plus the stream state the incremental
 * #primitive_convert protocol needs:
 *
 *   _incomplete    bytes of a character the decoder has seen but not finished
 *   _putback       "read again" bytes rewound after an invalid byte sequence
 *   _pendingOutput converted bytes that did not fit in the destination buffer
 *
 * Errors are attributed to the stage of the path they occur in: a byte the
 * decoder rejects is reported against (path[0], path[1]), a character the
 * encoder cannot represent against (path[n-2], path[n-1]). That is what makes
 * Encoding::Converter.new("EUC-JP", "ISO-8859-1") report EUC-JP -> UTF-8 for a
 * bad byte, exactly as CRuby does.
 *
 * ***************************************************************************/
#if FEATURE_ENCODING

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    public static partial class RubyEncodingOps {

        /// <summary>
        /// Encoding::Converter
        /// </summary>
        [RubyClass("Converter", Inherits = typeof(Object), BuildConfig = "FEATURE_ENCODING")]
        public class RubyConverter {
            #region Flags and constants

            internal const int InvalidMaskFlag = 0x0f;
            internal const int InvalidReplaceFlag = 0x02;
            internal const int UndefMaskFlag = 0xf0;
            internal const int UndefReplaceFlag = 0x20;
            internal const int UndefHexCharRefFlag = 0x30;
            internal const int UniversalNewlineFlag = 0x100;
            internal const int CrlfNewlineFlag = 0x1000;
            internal const int CrNewlineFlag = 0x2000;
            internal const int XmlTextFlag = 0x8000;
            internal const int XmlAttrContentFlag = 0x10000;
            internal const int XmlAttrQuoteFlag = 0x100000;
            internal const int PartialInputFlag = 0x20000;
            internal const int AfterOutputFlag = 0x40000;

            [RubyConstant("INVALID_MASK")]
            public static readonly int InvalidMask = InvalidMaskFlag;

            [RubyConstant("INVALID_REPLACE")]
            public static readonly int InvalidReplace = InvalidReplaceFlag;

            [RubyConstant("UNDEF_MASK")]
            public static readonly int UndefMask = UndefMaskFlag;

            [RubyConstant("UNDEF_REPLACE")]
            public static readonly int UndefReplace = UndefReplaceFlag;

            [RubyConstant("UNDEF_HEX_CHARREF")]
            public static readonly int UndefHexCharRef = UndefHexCharRefFlag;

            [RubyConstant("PARTIAL_INPUT")]
            public static readonly int PartialInput = PartialInputFlag;

            [RubyConstant("AFTER_OUTPUT")]
            public static readonly int AfterOutput = AfterOutputFlag;

            [RubyConstant("UNIVERSAL_NEWLINE_DECORATOR")]
            public static readonly int UniversalNewlineDecorator = UniversalNewlineFlag;

            [RubyConstant("CRLF_NEWLINE_DECORATOR")]
            public static readonly int CrlfNewlineDecorator = CrlfNewlineFlag;

            [RubyConstant("CR_NEWLINE_DECORATOR")]
            public static readonly int CrNewlineDecorator = CrNewlineFlag;

            [RubyConstant("XML_TEXT_DECORATOR")]
            public static readonly int XmlTextDecorator = XmlTextFlag;

            [RubyConstant("XML_ATTR_CONTENT_DECORATOR")]
            public static readonly int XmlAttrContentDecorator = XmlAttrContentFlag;

            [RubyConstant("XML_ATTR_QUOTE_DECORATOR")]
            public static readonly int XmlAttrQuoteDecorator = XmlAttrQuoteFlag;

            private static readonly byte[]/*!*/ EmptyBytes = new byte[0];
            private static readonly char[]/*!*/ EmptyChars = new char[0];

            /// <summary>Marks "replace: nil", i.e. keep the encoding's default replacement.</summary>
            private static readonly object/*!*/ DefaultReplacementMarker = new object();

            #endregion

            #region State

            private RubyEncoding/*!*/[]/*!*/ _path;
            private int _flags;
            private int _sourceUnitSize;

            private Decoder/*!*/ _decoder;
            private Encoder/*!*/ _encoder;

            private readonly List<byte>/*!*/ _incomplete = new List<byte>();
            private readonly List<byte>/*!*/ _putback = new List<byte>();
            private readonly List<byte>/*!*/ _pendingOutput = new List<byte>();

            private MutableString/*!*/ _replacement;
            private byte[]/*!*/ _replacementBytes;

            private string/*!*/ _status = "source_buffer_empty";
            private byte[] _errorBytes, _readAgainBytes;
            private bool _errorAtEncodeStage;
            private Exception _lastError;
            private bool _finished;

            // set when the previous output byte was CR, so a CRLF split across two
            // calls is still collapsed by the universal-newline decorator
            private bool _pendingCarriageReturn;

            #endregion

            #region Construction

            internal RubyConverter(RubyEncoding/*!*/[]/*!*/ path, int flags) {
                _path = path;
                _flags = flags;
                _sourceUnitSize = GetUnitSize(path[0]);
                _decoder = DecodeStageSource.StrictEncoding.GetDecoder();
                _encoder = EncodeStageDestination.StrictEncoding.GetEncoder();
                _replacement = DefaultReplacement(EncodeStageDestination);
                _replacementBytes = EncodeReplacement(_replacement);
            }

            /// <summary>
            /// Number of bytes in the smallest code unit of the source encoding. The decoder is fed
            /// one unit at a time so that the boundary of a broken character is known exactly.
            /// </summary>
            private static int GetUnitSize(RubyEncoding/*!*/ encoding) {
                switch (encoding.CodePage) {
                    case RubyEncoding.CodePageUTF16LE:
                    case RubyEncoding.CodePageUTF16BE:
                        return 2;

                    case RubyEncoding.CodePageUTF32LE:
                    case RubyEncoding.CodePageUTF32BE:
                        return 4;

                    default:
                        return 1;
                }
            }

            private static MutableString/*!*/ DefaultReplacement(RubyEncoding/*!*/ destination) {
                return destination == RubyEncoding.UTF8 ?
                    MutableString.CreateMutable("�", RubyEncoding.UTF8) :
                    MutableString.CreateAscii("?");
            }

            /// <summary>The encodings the decoder half works between: source -&gt; (UTF-8 | destination).</summary>
            internal RubyEncoding/*!*/ DecodeStageSource { get { return _path[0]; } }
            internal RubyEncoding/*!*/ DecodeStageDestination { get { return _path[1]; } }

            /// <summary>The encodings the encoder half works between: (source | UTF-8) -&gt; destination.</summary>
            internal RubyEncoding/*!*/ EncodeStageSource { get { return _path[_path.Length - 2]; } }
            internal RubyEncoding/*!*/ EncodeStageDestination { get { return _path[_path.Length - 1]; } }

            internal RubyEncoding/*!*/ SourceEncoding { get { return _path[0]; } }
            internal RubyEncoding/*!*/ DestinationEncoding { get { return _path[_path.Length - 1]; } }

            #endregion

            #region Conversion path

            /// <summary>
            /// CRuby's transcoder table has a direct converter between UTF-8 and (almost) every
            /// other encoding and routes everything else through UTF-8. .NET can only convert
            /// through UTF-16, so the only pairs we can honestly call "direct" are those with
            /// UTF-8 at one end.
            /// </summary>
            private static bool HasDirectConverter(RubyEncoding/*!*/ source, RubyEncoding/*!*/ destination) {
                return source == RubyEncoding.UTF8 || destination == RubyEncoding.UTF8;
            }

            internal static RubyEncoding/*!*/[]/*!*/ SearchPath(RubyEncoding/*!*/ source, RubyEncoding/*!*/ destination) {
                if (source == destination) {
                    throw new ConverterNotFoundError(String.Format(CultureInfo.InvariantCulture,
                        "code converter not found ({0} to {1})", source.Name, destination.Name));
                }

                if (HasDirectConverter(source, destination)) {
                    return new[] { source, destination };
                }

                return new[] { source, RubyEncoding.UTF8, destination };
            }

            internal static RubyArray/*!*/ MakeConvPath(RubyEncoding/*!*/[]/*!*/ path, int flags) {
                var result = new RubyArray(path.Length);
                for (int i = 0; i < path.Length - 1; i++) {
                    var pair = new RubyArray(2);
                    pair.Add(path[i]);
                    pair.Add(path[i + 1]);
                    result.Add(pair);
                }

                if ((flags & UniversalNewlineFlag) != 0) {
                    result.Add(MutableString.CreateAscii("universal_newline").Freeze());
                }
                if ((flags & CrlfNewlineFlag) != 0) {
                    result.Add(MutableString.CreateAscii("crlf_newline").Freeze());
                }
                if ((flags & CrNewlineFlag) != 0) {
                    result.Add(MutableString.CreateAscii("cr_newline").Freeze());
                }
                return result;
            }

            internal RubyArray/*!*/ ConvPath() {
                return MakeConvPath(_path, _flags);
            }

            #endregion

            #region Replacement

            internal MutableString/*!*/ Replacement {
                get { return _replacement; }
            }

            private byte[]/*!*/ EncodeReplacement(MutableString/*!*/ value) {
                string chars = value.ToString();
                try {
                    return EncodeStageDestination.StrictEncoding.GetBytes(chars);
                } catch (EncoderFallbackException e) {
                    int codepoint = e.CharUnknownHigh != '\0' ?
                        Char.ConvertToUtf32(e.CharUnknownHigh, e.CharUnknownLow) : e.CharUnknown;
                    throw new UndefinedConversionError(UndefinedConversionMessage(codepoint));
                }
            }

            /// <summary>
            /// Sets the replacement string. Raises UndefinedConversionError, leaving the current
            /// replacement in place, if it cannot be represented in the destination encoding.
            /// </summary>
            internal void SetReplacement(MutableString/*!*/ value) {
                byte[] bytes = EncodeReplacement(value);
                _replacement = MutableString.Create(value);
                _replacementBytes = bytes;
            }

            #endregion

            #region Error bookkeeping

            private static string/*!*/ InspectBytes(byte[]/*!*/ bytes) {
                var result = new StringBuilder(bytes.Length + 2);
                result.Append('"');
                foreach (byte b in bytes) {
                    if (b >= 0x20 && b < 0x7f && b != (byte)'"' && b != (byte)'\\') {
                        result.Append((char)b);
                    } else {
                        result.Append("\\x").Append(b.ToString("X2", CultureInfo.InvariantCulture));
                    }
                }
                result.Append('"');
                return result.ToString();
            }

            private string/*!*/ UndefinedConversionMessage(int codepoint) {
                string u = "U+" + codepoint.ToString(codepoint > 0xffff ? "X" : "X4", CultureInfo.InvariantCulture);
                if (_path.Length == 2) {
                    return String.Format(CultureInfo.InvariantCulture, "{0} from {1} to {2}", u, _path[0].Name, _path[1].Name);
                }

                var names = new string[_path.Length];
                for (int i = 0; i < _path.Length; i++) {
                    names[i] = _path[i].Name;
                }
                return String.Format(CultureInfo.InvariantCulture, "{0} to {1} in conversion from {2}",
                    u, DestinationEncoding.Name, String.Join(" to ", names));
            }

            private string/*!*/ InvalidByteSequenceMessage(byte[]/*!*/ errorBytes, byte[]/*!*/ readAgain, bool incomplete) {
                if (incomplete) {
                    return String.Format(CultureInfo.InvariantCulture, "incomplete {0} on {1}",
                        InspectBytes(errorBytes), DecodeStageSource.Name);
                }
                if (readAgain.Length == 0) {
                    return String.Format(CultureInfo.InvariantCulture, "{0} on {1}",
                        InspectBytes(errorBytes), DecodeStageSource.Name);
                }
                return String.Format(CultureInfo.InvariantCulture, "{0} followed by {1} on {2}",
                    InspectBytes(errorBytes), InspectBytes(readAgain), DecodeStageSource.Name);
            }

            private void RecordSuccess(string/*!*/ status) {
                _status = status;
                _errorBytes = null;
                _readAgainBytes = null;
                _lastError = null;
            }

            private void RecordInvalidByteSequence(byte[]/*!*/ errorBytes, byte[]/*!*/ readAgain, bool incomplete) {
                _status = incomplete ? "incomplete_input" : "invalid_byte_sequence";
                _errorBytes = errorBytes;
                _readAgainBytes = readAgain;
                _errorAtEncodeStage = false;

                var error = new InvalidByteSequenceError(InvalidByteSequenceMessage(errorBytes, readAgain, incomplete));
                error.SourceEncoding = DecodeStageSource;
                error.DestinationEncoding = DecodeStageDestination;
                error.ErrorBytes = errorBytes;
                error.ReadAgainBytes = readAgain;
                error.IncompleteInput = incomplete;
                _lastError = error;
            }

            private void RecordUndefinedConversion(int codepoint, byte[]/*!*/ errorBytes) {
                _status = "undefined_conversion";
                _errorBytes = errorBytes;
                _readAgainBytes = EmptyBytes;
                _errorAtEncodeStage = true;

                var error = new UndefinedConversionError(UndefinedConversionMessage(codepoint));
                error.SourceEncoding = EncodeStageSource;
                error.DestinationEncoding = EncodeStageDestination;
                error.ErrorCharBytes = errorBytes;
                _lastError = error;
            }

            internal Exception LastError {
                get { return _lastError; }
            }

            internal RubyArray/*!*/ GetErrorInfo(RubyContext/*!*/ context) {
                var result = new RubyArray(5);
                result.Add(context.CreateAsciiSymbol(_status));
                if (_errorBytes == null) {
                    result.Add(null);
                    result.Add(null);
                    result.Add(null);
                    result.Add(null);
                } else {
                    RubyEncoding from = _errorAtEncodeStage ? EncodeStageSource : DecodeStageSource;
                    RubyEncoding to = _errorAtEncodeStage ? EncodeStageDestination : DecodeStageDestination;
                    result.Add(MutableString.CreateBinary(Encoding.ASCII.GetBytes(from.Name)));
                    result.Add(MutableString.CreateBinary(Encoding.ASCII.GetBytes(to.Name)));
                    result.Add(MutableString.CreateBinary(_errorBytes));
                    result.Add(MutableString.CreateBinary(_readAgainBytes ?? EmptyBytes));
                }
                return result;
            }

            #endregion

            #region putback, insert_output

            internal MutableString/*!*/ PutBack(int? count) {
                int n = count.HasValue ? Math.Min(Math.Max(count.Value, 0), _putback.Count) : _putback.Count;
                if (n == 0) {
                    return MutableString.CreateBinary(EmptyBytes, SourceEncoding);
                }

                // CRuby hands back the tail of the read-again buffer
                var bytes = _putback.GetRange(_putback.Count - n, n).ToArray();
                _putback.RemoveRange(_putback.Count - n, n);
                return MutableString.CreateBinary(bytes, SourceEncoding);
            }

            internal void InsertOutput(MutableString/*!*/ str) {
                byte[] bytes;
                if (str.Encoding == DestinationEncoding) {
                    bytes = str.ToByteArray();
                } else {
                    try {
                        bytes = DestinationEncoding.StrictEncoding.GetBytes(str.ToString());
                    } catch (EncoderFallbackException) {
                        throw new UndefinedConversionError("cannot insert output in " + DestinationEncoding.Name);
                    }
                }
                _pendingOutput.InsertRange(0, bytes);
            }

            #endregion

            #region The conversion loop

            internal bool IsFinished {
                get { return _finished; }
            }

            /// <summary>
            /// The core of #primitive_convert. Consumes bytes from <paramref name="source"/> (which
            /// is mutated in place), appends converted bytes to <paramref name="destination"/> and
            /// returns CRuby's status name.
            /// </summary>
            internal string/*!*/ PrimitiveConvert(MutableString source, MutableString/*!*/ destination,
                int? byteOffset, int? byteLimit, int flags) {

                destination.RequireNotFrozen();

                byte[] existing = destination.ToByteArray();
                int offset = byteOffset ?? existing.Length;
                if (offset > existing.Length) {
                    throw RubyExceptions.CreateArgumentError("output_byteoffset too big");
                }
                if (offset < 0) {
                    throw RubyExceptions.CreateArgumentError("negative output_byteoffset");
                }

                int limit = byteLimit.HasValue ? Math.Max(byteLimit.Value, 0) : Int32.MaxValue;
                var written = new List<byte>();

                byte[] sourceBytes = source != null ? source.ToByteArray() : EmptyBytes;
                int prefix = _putback.Count;
                byte[] input;
                if (prefix == 0) {
                    input = sourceBytes;
                } else {
                    input = new byte[prefix + sourceBytes.Length];
                    _putback.CopyTo(input, 0);
                    Array.Copy(sourceBytes, 0, input, prefix, sourceBytes.Length);
                    _putback.Clear();
                }

                int consumed = 0;
                var chars = new char[16];
                string status = null;

                // whatever didn't fit last time goes out first
                if (!Drain(_pendingOutput, written, limit)) {
                    status = "destination_buffer_full";
                } else {
                    while (true) {
                        if (consumed >= input.Length) {
                            if (_incomplete.Count != 0 && (flags & PartialInputFlag) == 0) {
                                var errorBytes = _incomplete.ToArray();
                                ResetDecoder();
                                RecordInvalidByteSequence(errorBytes, EmptyBytes, true);
                                status = "incomplete_input";
                            } else if ((flags & PartialInputFlag) != 0) {
                                status = "source_buffer_empty";
                            } else {
                                status = "finished";
                            }
                            break;
                        }

                        int unit = Math.Min(_sourceUnitSize, input.Length - consumed);
                        int bytesUsed, charsUsed;
                        bool completed;
                        try {
                            _decoder.Convert(input, consumed, unit, chars, 0, chars.Length, false,
                                out bytesUsed, out charsUsed, out completed);
                        } catch (DecoderFallbackException) {
                            byte[] errorBytes, readAgain;
                            if (_incomplete.Count != 0) {
                                errorBytes = _incomplete.ToArray();
                                readAgain = Slice(input, consumed, unit);
                            } else {
                                errorBytes = Slice(input, consumed, unit);
                                readAgain = EmptyBytes;
                            }
                            consumed += unit;
                            ResetDecoder();

                            if ((_flags & InvalidMaskFlag) == InvalidReplaceFlag) {
                                _putback.Clear();
                                _putback.AddRange(readAgain);
                                if (!Emit(_replacementBytes, written, limit)) {
                                    status = "destination_buffer_full";
                                    break;
                                }
                                continue;
                            }

                            _putback.Clear();
                            _putback.AddRange(readAgain);
                            RecordInvalidByteSequence(errorBytes, readAgain, false);
                            status = "invalid_byte_sequence";
                            break;
                        }

                        int chunkStart = consumed;
                        consumed += bytesUsed;
                        if (charsUsed == 0) {
                            for (int i = 0; i < bytesUsed; i++) {
                                _incomplete.Add(input[chunkStart + i]);
                            }
                            continue;
                        }

                        _incomplete.Clear();

                        string encodeStatus = EncodeChars(chars, charsUsed, written, limit);
                        if (encodeStatus != null) {
                            status = encodeStatus;
                            break;
                        }

                        if ((flags & AfterOutputFlag) != 0 && written.Count > 0) {
                            status = "after_output";
                            break;
                        }
                    }
                }

                if (status != "invalid_byte_sequence" && status != "incomplete_input" && status != "undefined_conversion") {
                    RecordSuccess(status);
                }

                // consume from the caller's source buffer
                if (source != null) {
                    int sourceConsumed = Math.Min(Math.Max(consumed - prefix, 0), sourceBytes.Length);
                    if (sourceConsumed > 0 || sourceBytes.Length == 0) {
                        var rest = Slice(sourceBytes, sourceConsumed, sourceBytes.Length - sourceConsumed);
                        var encoding = source.Encoding;
                        source.Clear();
                        if (rest.Length > 0) {
                            source.Append(rest);
                        }
                        source.ForceEncoding(encoding);
                    }
                }

                // rewrite the destination buffer: content up to the offset plus what we produced
                var converted = ApplyNewlineDecorators(written.ToArray());
                destination.Clear();
                if (offset > 0) {
                    destination.Append(existing, 0, offset);
                }
                if (converted.Length > 0) {
                    destination.Append(converted);
                }
                destination.ForceEncoding(DestinationEncoding);

                return status;
            }

            /// <summary>
            /// Encodes the characters the decoder produced. Returns null on success, or the status
            /// that terminates the conversion.
            /// </summary>
            private string EncodeChars(char[]/*!*/ chars, int count, List<byte>/*!*/ written, int limit) {
                int i = 0;
                while (i < count) {
                    int size = (Char.IsHighSurrogate(chars[i]) && i + 1 < count && Char.IsLowSurrogate(chars[i + 1])) ? 2 : 1;
                    int codepoint = size == 2 ? Char.ConvertToUtf32(chars[i], chars[i + 1]) : chars[i];

                    byte[] bytes;
                    try {
                        bytes = EncodeCodepoint(chars, i, size);
                    } catch (EncoderFallbackException) {
                        ResetEncoder();

                        int undefMode = _flags & UndefMaskFlag;
                        if (undefMode == UndefReplaceFlag) {
                            if (!Emit(_replacementBytes, written, limit)) {
                                return "destination_buffer_full";
                            }
                            i += size;
                            continue;
                        }
                        if (undefMode == UndefHexCharRefFlag) {
                            string reference = "&#x" + codepoint.ToString("X", CultureInfo.InvariantCulture) + ";";
                            if (!Emit(Encoding.ASCII.GetBytes(reference), written, limit)) {
                                return "destination_buffer_full";
                            }
                            i += size;
                            continue;
                        }

                        RecordUndefinedConversion(codepoint, ErrorCharBytes(chars, i, size));
                        return "undefined_conversion";
                    }

                    if (!Emit(bytes, written, limit)) {
                        return "destination_buffer_full";
                    }
                    i += size;
                }
                return null;
            }

            private byte[]/*!*/ EncodeCodepoint(char[]/*!*/ chars, int index, int size) {
                var buffer = new byte[16 + EncodeStageDestination.MaxBytesPerChar * (size + 4)];
                int charsUsed, bytesUsed;
                bool completed;
                _encoder.Convert(chars, index, size, buffer, 0, buffer.Length, false, out charsUsed, out bytesUsed, out completed);
                return Slice(buffer, 0, bytesUsed);
            }

            /// <summary>The offending character, expressed in the source encoding of the failing stage.</summary>
            private byte[]/*!*/ ErrorCharBytes(char[]/*!*/ chars, int index, int size) {
                try {
                    return EncodeStageSource.StrictEncoding.GetBytes(chars, index, size);
                } catch (EncoderFallbackException) {
                    return Encoding.UTF8.GetBytes(new String(chars, index, size));
                }
            }

            /// <summary>
            /// Moves <paramref name="bytes"/> into the output, parking the tail in _pendingOutput
            /// if the destination limit is reached. Returns false if the buffer filled up.
            /// </summary>
            private bool Emit(byte[]/*!*/ bytes, List<byte>/*!*/ written, int limit) {
                int i = 0;
                while (i < bytes.Length && written.Count < limit) {
                    written.Add(bytes[i++]);
                }
                if (i < bytes.Length) {
                    for (int j = bytes.Length - 1; j >= i; j--) {
                        _pendingOutput.Insert(0, bytes[j]);
                    }
                    return false;
                }
                return true;
            }

            private static bool Drain(List<byte>/*!*/ pending, List<byte>/*!*/ written, int limit) {
                int i = 0;
                while (i < pending.Count && written.Count < limit) {
                    written.Add(pending[i++]);
                }
                if (i > 0) {
                    pending.RemoveRange(0, i);
                }
                return pending.Count == 0;
            }

            private static byte[]/*!*/ Slice(byte[]/*!*/ array, int start, int count) {
                if (count <= 0) {
                    return EmptyBytes;
                }
                var result = new byte[count];
                Array.Copy(array, start, result, 0, count);
                return result;
            }

            private void ResetDecoder() {
                _incomplete.Clear();
                _decoder = DecodeStageSource.StrictEncoding.GetDecoder();
            }

            private void ResetEncoder() {
                _encoder = EncodeStageDestination.StrictEncoding.GetEncoder();
            }

            /// <summary>
            /// Newline decorators operate on the converted bytes. They are only meaningful for
            /// ASCII-compatible destinations; anything else passes through unchanged.
            /// </summary>
            private byte[]/*!*/ ApplyNewlineDecorators(byte[]/*!*/ bytes) {
                if ((_flags & (UniversalNewlineFlag | CrlfNewlineFlag | CrNewlineFlag)) == 0 ||
                    !DestinationEncoding.IsAsciiIdentity) {
                    return bytes;
                }

                var result = new List<byte>(bytes.Length + 8);
                foreach (byte b in bytes) {
                    if ((_flags & UniversalNewlineFlag) != 0) {
                        if (_pendingCarriageReturn) {
                            _pendingCarriageReturn = false;
                            result.Add((byte)'\n');
                            if (b == (byte)'\n') {
                                continue;
                            }
                        }
                        if (b == (byte)'\r') {
                            _pendingCarriageReturn = true;
                            continue;
                        }
                        result.Add(b);
                        continue;
                    }

                    if (b == (byte)'\n') {
                        if ((_flags & CrlfNewlineFlag) != 0) {
                            result.Add((byte)'\r');
                            result.Add((byte)'\n');
                            continue;
                        }
                        if ((_flags & CrNewlineFlag) != 0) {
                            result.Add((byte)'\r');
                            continue;
                        }
                    }
                    result.Add(b);
                }
                return result.ToArray();
            }

            #endregion

            #region convert, finish

            internal MutableString/*!*/ Convert(MutableString/*!*/ str) {
                if (_finished) {
                    throw RubyExceptions.CreateArgumentError("converter already finished");
                }

                var source = MutableString.CreateBinary(str.ToByteArray());
                var destination = MutableString.CreateBinary(DestinationEncoding);
                while (true) {
                    string status = PrimitiveConvert(source, destination, null, null, PartialInputFlag);
                    if (status == "source_buffer_empty" || status == "finished") {
                        return destination;
                    }
                    if (status == "destination_buffer_full" || status == "after_output") {
                        continue;
                    }
                    throw _lastError;
                }
            }

            internal MutableString/*!*/ FinishConversion() {
                var destination = MutableString.CreateBinary(DestinationEncoding);
                if (_finished) {
                    return destination;
                }

                string status = PrimitiveConvert(null, destination, null, null, 0);
                if (status != "finished") {
                    _finished = true;
                    throw _lastError ?? RubyExceptions.CreateArgumentError("conversion failed");
                }

                // flush the encoder's shift state (e.g. the closing ESC ( B of ISO-2022-JP)
                var buffer = new byte[64];
                int charsUsed, bytesUsed;
                bool completed;
                _encoder.Convert(EmptyChars, 0, 0, buffer, 0, buffer.Length, true, out charsUsed, out bytesUsed, out completed);
                if (bytesUsed > 0) {
                    destination.Append(buffer, 0, bytesUsed);
                    destination.ForceEncoding(DestinationEncoding);
                }

                _finished = true;
                return destination;
            }

            #endregion

            #region Argument coercion

            private static RubyEncoding/*!*/ ToEncoding(ConversionStorage<MutableString>/*!*/ toStr, object obj) {
                if (obj == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion of nil into String");
                }
                return Protocols.ConvertToEncoding(toStr, obj);
            }

            private static MutableString/*!*/ ToReplacementString(ConversionStorage<MutableString>/*!*/ toStr, object obj) {
                var str = obj as MutableString;
                if (str != null) {
                    return str;
                }
                if (obj == null || obj is bool || obj is int) {
                    throw RubyExceptions.CreateTypeError(String.Format(CultureInfo.InvariantCulture,
                        "no implicit conversion of {0} into String",
                        obj == null ? "nil" : toStr.Context.GetClassDisplayName(obj)));
                }
                return Protocols.CastToString(toStr, obj);
            }

            private static string KeyName(object key) {
                var symbol = key as RubySymbol;
                if (symbol != null) {
                    return symbol.ToString();
                }
                var str = key as MutableString;
                return str != null ? str.ToString() : null;
            }

            private static string SymbolName(object value) {
                var symbol = value as RubySymbol;
                if (symbol != null) {
                    return symbol.ToString();
                }
                var str = value as MutableString;
                return str != null ? str.ToString() : null;
            }

            /// <summary>
            /// Turns the trailing argument (an Integer flag word or an options Hash) into CRuby's
            /// flags. The replacement, if the Hash specifies one, comes back separately.
            /// </summary>
            private static int ParseOptions(object options, out object replacement) {
                replacement = null;
                if (options == null || options == Missing.Value) {
                    return 0;
                }

                if (options is int) {
                    return (int)options;
                }

                var hash = options as IDictionary<object, object>;
                if (hash == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion into Hash");
                }

                int flags = 0;
                foreach (var entry in hash) {
                    switch (KeyName(entry.Key)) {
                        case "invalid":
                            if (SymbolName(entry.Value) == "replace") {
                                flags |= InvalidReplaceFlag;
                            }
                            break;

                        case "undef":
                            if (SymbolName(entry.Value) == "replace") {
                                flags |= UndefReplaceFlag;
                            }
                            break;

                        case "replace":
                            replacement = entry.Value ?? DefaultReplacementMarker;
                            break;

                        case "xml":
                            if (SymbolName(entry.Value) == "text") {
                                flags |= XmlTextFlag;
                            } else if (SymbolName(entry.Value) == "attr") {
                                flags |= XmlAttrContentFlag | XmlAttrQuoteFlag;
                            }
                            break;

                        case "universal_newline":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= UniversalNewlineFlag;
                            }
                            break;

                        case "crlf_newline":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= CrlfNewlineFlag;
                            }
                            break;

                        case "cr_newline":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= CrNewlineFlag;
                            }
                            break;

                        case "newline":
                            switch (SymbolName(entry.Value)) {
                                case "universal": flags |= UniversalNewlineFlag; break;
                                case "crlf": flags |= CrlfNewlineFlag; break;
                                case "cr": flags |= CrNewlineFlag; break;
                            }
                            break;

                        case "partial_input":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= PartialInputFlag;
                            }
                            break;

                        case "after_output":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= AfterOutputFlag;
                            }
                            break;
                    }
                }
                return flags;
            }

            private static int? ToOptionalInt(ConversionStorage<int>/*!*/ toInt, object value) {
                if (value == null || value == Missing.Value) {
                    return null;
                }
                if (value is int) {
                    return (int)value;
                }
                return Protocols.CastToFixnum(toInt, value);
            }

            #endregion

            #region Ruby methods

            [RubyConstructor]
            public static RubyConverter/*!*/ Create(ConversionStorage<MutableString>/*!*/ toStr, RubyClass/*!*/ self,
                object source, object destination, [Optional]object options) {

                var path = SearchPath(ToEncoding(toStr, source), ToEncoding(toStr, destination));

                object replacement;
                int flags = ParseOptions(options, out replacement);

                var result = new RubyConverter(path, flags);
                if (replacement != null && replacement != DefaultReplacementMarker) {
                    result.SetReplacement(ToReplacementString(toStr, replacement));
                }
                return result;
            }

            [RubyMethod("search_convpath", RubyMethodAttributes.PublicSingleton)]
            public static RubyArray/*!*/ SearchConvPath(ConversionStorage<MutableString>/*!*/ toStr, RubyClass/*!*/ self,
                object source, object destination, [Optional]object options) {

                var path = SearchPath(ToEncoding(toStr, source), ToEncoding(toStr, destination));
                object replacement;
                int flags = ParseOptions(options, out replacement);
                return MakeConvPath(path, flags);
            }

            [RubyMethod("asciicompat_encoding", RubyMethodAttributes.PublicSingleton)]
            public static RubyEncoding AsciiCompatibleEncoding(ConversionStorage<MutableString>/*!*/ toStr, RubyClass/*!*/ self,
                object encoding) {

                RubyEncoding enc = encoding as RubyEncoding;
                if (enc == null) {
                    var name = Protocols.CastToString(toStr, encoding);
                    try {
                        enc = self.Context.GetRubyEncoding(name);
                    } catch (Exception) {
                        // names that resolve to no encoding (e.g. "internal" with no default_internal)
                        return null;
                    }
                    if (enc == null) {
                        return null;
                    }
                }

                return enc.IsAsciiIdentity ? null : RubyEncoding.UTF8;
            }

            [RubyMethod("source_encoding")]
            public static RubyEncoding/*!*/ GetSourceEncoding(RubyConverter/*!*/ self) {
                return self.SourceEncoding;
            }

            [RubyMethod("destination_encoding")]
            public static RubyEncoding/*!*/ GetDestinationEncoding(RubyConverter/*!*/ self) {
                return self.DestinationEncoding;
            }

            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(RubyConverter/*!*/ self) {
                return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                    "#<Encoding::Converter: {0} to {1}>", self.SourceEncoding.Name, self.DestinationEncoding.Name));
            }

            [RubyMethod("convpath")]
            public static RubyArray/*!*/ GetConvPath(RubyConverter/*!*/ self) {
                return self.ConvPath();
            }

            [RubyMethod("convert")]
            public static MutableString/*!*/ ConvertString(RubyConverter/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
                return self.Convert(str);
            }

            [RubyMethod("finish")]
            public static MutableString/*!*/ Finish(RubyConverter/*!*/ self) {
                return self.FinishConversion();
            }

            [RubyMethod("primitive_convert")]
            public static object PrimitiveConvert(
                ConversionStorage<int>/*!*/ toInt,
                RubyContext/*!*/ context,
                RubyConverter/*!*/ self,
                MutableString source,
                [NotNull]MutableString/*!*/ destination,
                [Optional]object byteOffset,
                [Optional]object byteLimit,
                [Optional]object options) {

                int? offset = ToOptionalInt(toInt, byteOffset);
                int? limit = ToOptionalInt(toInt, byteLimit);
                object replacement;
                int flags = ParseOptions(options, out replacement);

                return context.CreateAsciiSymbol(self.PrimitiveConvert(source, destination, offset, limit, flags));
            }

            [RubyMethod("primitive_errinfo")]
            public static RubyArray/*!*/ GetPrimitiveErrorInfo(RubyContext/*!*/ context, RubyConverter/*!*/ self) {
                return self.GetErrorInfo(context);
            }

            [RubyMethod("last_error")]
            public static object GetLastError(RubyConverter/*!*/ self) {
                return self.LastError;
            }

            [RubyMethod("putback")]
            public static MutableString/*!*/ PutBackAll(RubyConverter/*!*/ self) {
                return self.PutBack(null);
            }

            [RubyMethod("putback")]
            public static MutableString/*!*/ PutBackCount(RubyConverter/*!*/ self, [DefaultProtocol]int count) {
                return self.PutBack(count);
            }

            [RubyMethod("insert_output")]
            public static object InsertOutput(RubyConverter/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
                self.InsertOutput(str);
                return null;
            }

            [RubyMethod("replacement")]
            public static MutableString/*!*/ GetReplacement(RubyConverter/*!*/ self) {
                return self.Replacement;
            }

            [RubyMethod("replacement=")]
            public static object SetReplacement(ConversionStorage<MutableString>/*!*/ toStr, RubyConverter/*!*/ self, object value) {
                self.SetReplacement(ToReplacementString(toStr, value));
                return value;
            }

            #endregion
        }
    }
}
#endif
