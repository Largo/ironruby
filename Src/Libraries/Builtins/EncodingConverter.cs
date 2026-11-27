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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using IronRuby.Runtime;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    public static partial class RubyEncodingOps {

        /// <summary>
        /// Encoding::Converter
        /// </summary>
        [RubyClass("Converter", Inherits = typeof(Object))]
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
            internal const int LfNewlineFlag = 0x4000;
            internal const int NewlineMask = UniversalNewlineFlag | CrlfNewlineFlag | CrNewlineFlag | LfNewlineFlag;
            internal const int XmlTextFlag = 0x8000;
            internal const int XmlAttrContentFlag = 0x10000;
            internal const int XmlAttrQuoteFlag = 0x100000;
            internal const int PartialInputFlag = 0x20000;
            internal const int AfterOutputFlag = 0x40000;

            // amp_escape has no flag in MRI - it is only ever asked for by name, as
            // Encoding::Converter.new("", "amp_escape") - so this one is IronRuby's own
            internal const int AmpEscapeFlag = 0x10000000;

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

            [RubyConstant("LF_NEWLINE_DECORATOR")]
            public static readonly int LfNewlineDecorator = LfNewlineFlag;

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
            private readonly bool _noConversion;
            private int _flags;
            private int _sourceUnitSize;

            private Decoder/*!*/ _decoder;

            // UTF8-MAC's composition (MRI's from_utf8_mac), which holds the last character decoded
            // back for the one after it; null for any other source.
            private readonly Utf8MacComposer _composer;

            // Characters the composer let go of that a full destination kept from being converted;
            // they go first in the next call, ahead of any new input.
            private string _composedPending = String.Empty;

            // Decoded characters a newline that filled the destination kept from being converted;
            // they go through EncodeChars first in the next call (the XML escapes and the newline
            // decorators still apply to them), ahead of any new input.
            private string _pendingChars = String.Empty;

            // How much of this call's output there was when the composer last wrote to it (-1: it
            // has not), which tells whether the composer is what filled the destination.
            private int _composerOutputEnd = -1;
            private Encoder/*!*/ _encoder;

            private readonly List<byte>/*!*/ _incomplete = new List<byte>();
            private readonly List<byte>/*!*/ _putback = new List<byte>();
            private readonly List<byte>/*!*/ _pendingOutput = new List<byte>();

            // how many bytes at the front of _pendingOutput #insert_output put there: the next
            // insertion goes after them but still before converted bytes parked behind them
            private int _insertedCount;

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

            // set once the :xml => :attr decorator has written its opening quote, and once it has
            // written the closing one
            private bool _xmlQuoteOpened, _decoratorsFinished;

            // what #convpath and #inspect say of a converter built from a conversion path
            // (Encoding::Converter.new([...])) or from a decorator's name, which is not what the
            // flags alone would say; null for any other
            private RubyArray _explicitConvPath;
            private string _explicitDescription;

            #endregion

            #region Construction

            /// <summary>
            /// A null path is a converter that changes no encoding and only runs its decorators:
            /// MRI's Encoding::Converter.new("", "", ...). It works on bytes, which it hands back
            /// as ASCII-8BIT.
            /// </summary>
            internal RubyConverter(RubyEncoding/*!*/[] path, int flags) {
                _noConversion = (path == null);
                path = path ?? new[] { RubyEncoding.Binary, RubyEncoding.Binary };
                _path = path;
                _flags = flags;
                _sourceUnitSize = GetUnitSize(path[0]);
                _decoder = DecodeStageSource.TranscodingEncoding.GetDecoder();
                _encoder = EncodeStageDestination.TranscodingEncoding.GetEncoder();
                _composer = _noConversion ? null : DecodeStageSource.CreateTranscodingComposer();
                _replacement = DefaultReplacement(path);
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

            /// <summary>
            /// MRI's default replacement is U+FFFD where the last converter inserts output in UTF-8
            /// (RubyExceptions.ReplacesWithUnicode) and "?" otherwise, and it is spelled in UTF-8 or
            /// US-ASCII respectively rather than in the destination encoding - #replacement is a
            /// description of the replacement, not the bytes the conversion will actually insert.
            /// </summary>
            private static MutableString/*!*/ DefaultReplacement(RubyEncoding/*!*/[]/*!*/ path) {
                return RubyExceptions.ReplacesWithUnicode(path[0], path[path.Length - 1]) ?
                    MutableString.CreateMutable("�", RubyEncoding.UTF8) :
                    MutableString.CreateAscii("?");
            }

            /// <summary>
            /// The encoding #replacement answers in once one has been set: the destination if it
            /// can hold ASCII, and UTF-8 if it cannot.
            /// </summary>
            private RubyEncoding/*!*/ ReplacementEncoding {
                get { return EncodeStageDestination.IsAsciiIdentity ? EncodeStageDestination : RubyEncoding.UTF8; }
            }

            /// <summary>The encodings the decoder half works between: source -&gt; (UTF-8 | destination).</summary>
            internal RubyEncoding/*!*/ DecodeStageSource { get { return _path[0]; } }
            internal RubyEncoding/*!*/ DecodeStageDestination { get { return _path[1]; } }

            /// <summary>The encodings the encoder half works between: (source | UTF-8) -&gt; destination.</summary>
            internal RubyEncoding/*!*/ EncodeStageSource { get { return _path[_path.Length - 2]; } }
            internal RubyEncoding/*!*/ EncodeStageDestination { get { return _path[_path.Length - 1]; } }

            internal RubyEncoding/*!*/ SourceEncoding { get { return _path[0]; } }
            internal bool IsNoConversion { get { return _noConversion; } }
            internal RubyEncoding/*!*/ DestinationEncoding { get { return _path[_path.Length - 1]; } }

            #endregion

            #region Conversion path

            /// <summary>
            /// The decorators a flag word asks for, in the order MRI lists them when it complains
            /// that it cannot build the converter.
            /// </summary>
            private static string/*!*/ DecoratorNames(int flags) {
                var names = new List<string>();
                if ((flags & UniversalNewlineFlag) != 0) { names.Add("universal_newline"); }
                if ((flags & CrlfNewlineFlag) != 0) { names.Add("crlf_newline"); }
                if ((flags & CrNewlineFlag) != 0) { names.Add("cr_newline"); }
                if ((flags & LfNewlineFlag) != 0) { names.Add("lf_newline"); }
                if ((flags & XmlTextFlag) != 0) { names.Add("xml_text"); }
                if ((flags & XmlAttrContentFlag) != 0) { names.Add("xml_attr_content"); }
                if ((flags & XmlAttrQuoteFlag) != 0) { names.Add("xml_attr_quote"); }
                return String.Join(",", names);
            }

            /// <summary>
            /// How MRI describes a converter, in #inspect and when it cannot build one: "UTF-8 to
            /// EUC-JP with crlf_newline", just the encoding named when the other is "", and just
            /// the decorators - or "no-conversion" - when both are.
            /// </summary>
            private static string/*!*/ Describe(string/*!*/ source, string/*!*/ destination, int flags) {
                string decorators = DecoratorNames(flags);
                string encodings = (source.Length == 0) ? destination :
                    (destination.Length == 0) ? source : source + " to " + destination;
                if (encodings.Length == 0) {
                    return decorators.Length == 0 ? "no-conversion" : decorators;
                }
                return decorators.Length == 0 ? encodings : encodings + " with " + decorators;
            }

            /// <summary>
            /// MRI has one answer for "that pair has no converter" and for "that is not an
            /// encoding at all", because both mean the same thing to it: there is no such code
            /// converter.  So the names are reported as they were given, unresolved.
            /// </summary>
            private static Exception/*!*/ ConverterNotFound(string/*!*/ source, string/*!*/ destination, int flags) {
                return new ConverterNotFoundError("code converter not found (" + Describe(source, destination, flags) + ")");
            }

            internal static RubyEncoding/*!*/[]/*!*/ SearchPath(RubyEncoding/*!*/ source, RubyEncoding/*!*/ destination) {
                if (source == destination || source.HasNoTranscoder || destination.HasNoTranscoder) {
                    throw ConverterNotFound(source.Name, destination.Name, 0);
                }

                return RubyExceptions.ConversionPath(source, destination);
            }

            /// <summary>
            /// Resolves both ends of a conversion together. Neither end can be resolved on its own
            /// because the error for an unresolvable one names them both.
            /// </summary>
            /// <remarks>
            /// Two empty names ask for no conversion at all, only the decorators: a converter can
            /// be made of that (null here), but there is no path to search.
            /// </remarks>
            private static RubyEncoding[] ResolvePath(ConversionStorage<MutableString>/*!*/ toStr,
                object source, object destination, int flags, bool allowNoConversion) {

                string sourceName = EncodingName(toStr, source);
                string destinationName = EncodingName(toStr, destination);
                if (sourceName.Length == 0 && destinationName.Length == 0 && allowNoConversion) {
                    return null;
                }
                if (sourceName.Length == 0 || destinationName.Length == 0) {
                    throw ConverterNotFound(sourceName, destinationName, flags);
                }
                RubyEncoding from = FindEncoding(toStr.Context, source, sourceName);
                RubyEncoding to = FindEncoding(toStr.Context, destination, destinationName);

                if (from == null || to == null || from == to) {
                    throw ConverterNotFound(sourceName, destinationName, flags);
                }
                if (from.HasNoTranscoder || to.HasNoTranscoder) {
                    // Ruby ships no converter for these, so there is no path to walk either.
                    throw ConverterNotFound(sourceName, destinationName, flags);
                }
                if (flags != 0 && (from == to)) {
                    throw ConverterNotFound(sourceName, destinationName, flags);
                }
                return RubyExceptions.ConversionPath(from, to);
            }

            /// <summary>
            /// The newline decorators exclude one another: MRI finds no converter that is both
            /// universal_newline and crlf_newline.
            /// </summary>
            private static void RequireOneNewlineDecorator(ConversionStorage<MutableString>/*!*/ toStr,
                object source, object destination, int flags) {
                int newline = flags & NewlineMask;
                if ((newline & (newline - 1)) != 0) {
                    throw ConverterNotFound(EncodingName(toStr, source), EncodingName(toStr, destination), flags);
                }
            }

            private static string/*!*/ EncodingName(ConversionStorage<MutableString>/*!*/ toStr, object obj) {
                var encoding = obj as RubyEncoding;
                if (encoding != null) {
                    return encoding.Name;
                }
                if (obj == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion of nil into String");
                }
                return Protocols.CastToString(toStr, obj).ToString();
            }

            private static RubyEncoding FindEncoding(RubyContext/*!*/ context, object obj, string/*!*/ name) {
                var encoding = obj as RubyEncoding;
                if (encoding != null) {
                    return encoding;
                }
                try {
                    return context.GetRubyEncoding(MutableString.CreateAscii(name));
                } catch (ArgumentException) {
                    return null;
                }
            }

            internal static RubyArray/*!*/ MakeConvPath(RubyEncoding/*!*/[] path, int flags) {
                var result = new RubyArray();
                for (int i = 0; path != null && i < path.Length - 1; i++) {
                    var pair = new RubyArray(2);
                    pair.Add(path[i]);
                    pair.Add(path[i + 1]);
                    result.Add(pair);
                }

                // The escapers run before the newline decorators, and #convpath lists them in the
                // order they run rather than in the order the options were given. MRI's
                // rb_econv_decorate_at_last puts them on the UTF-8 side of a last leg into an
                // encoding that is not ASCII compatible: [[UTF-8, UTF-16BE]] with
                // universal_newline is ["universal_newline", [UTF-8, UTF-16BE]].
                int at = (path != null && !path[path.Length - 1].IsAsciiIdentity) ? result.Count - 1 : result.Count;
                foreach (string name in DecoratorPathNames(flags)) {
                    result.Insert(at++, MutableString.CreateBinary(Encoding.ASCII.GetBytes(name)).Freeze());
                }
                return result;
            }

            private static List<string>/*!*/ DecoratorPathNames(int flags) {
                var names = new List<string>();
                if ((flags & XmlTextFlag) != 0) { names.Add("xml_text_escape"); }
                if ((flags & XmlAttrContentFlag) != 0) { names.Add("xml_attr_content_escape"); }
                if ((flags & XmlAttrQuoteFlag) != 0) { names.Add("xml_attr_quote"); }
                if ((flags & UniversalNewlineFlag) != 0) { names.Add("universal_newline"); }
                if ((flags & CrlfNewlineFlag) != 0) { names.Add("crlf_newline"); }
                if ((flags & CrNewlineFlag) != 0) { names.Add("cr_newline"); }
                if ((flags & LfNewlineFlag) != 0) { names.Add("lf_newline"); }
                return names;
            }

            internal RubyArray/*!*/ ConvPath() {
                if (_explicitConvPath != null) {
                    return new RubyArray(_explicitConvPath);
                }
                return MakeConvPath(_noConversion ? null : _path, _flags);
            }

            #endregion

            #region Replacement

            internal MutableString/*!*/ Replacement {
                get { return _replacement; }
            }

            private byte[]/*!*/ EncodeReplacement(MutableString/*!*/ value) {
                string chars = value.ToString();
                try {
                    return EncodeStageDestination.TranscodingEncoding.GetBytes(chars);
                } catch (EncoderFallbackException) {
                    throw new UndefinedConversionError("replacement character setup failed");
                }
            }

            /// <summary>
            /// Sets the replacement string. Raises UndefinedConversionError, leaving the current
            /// replacement in place, if it cannot be represented in the destination encoding.
            /// </summary>
            internal void SetReplacement(MutableString/*!*/ value) {
                byte[] bytes = EncodeReplacement(value);
                _replacement = MutableString.CreateMutable(value.ConvertToString(), ReplacementEncoding);
                _replacementBytes = bytes;
            }

            #endregion

            #region Error bookkeeping

            private string/*!*/ UndefinedConversionMessage(int codepoint) {
                string u = "U+" + codepoint.ToString(codepoint > 0xffff ? "X" : "X4", CultureInfo.InvariantCulture);
                return RubyExceptions.UndefinedConversionMessage(u, _path, _path.Length - 2);
            }

            private string/*!*/ InvalidByteSequenceMessage(byte[]/*!*/ errorBytes, byte[]/*!*/ readAgain, bool incomplete) {
                return RubyExceptions.InvalidByteSequenceMessage(DecodeStageSource, errorBytes, readAgain, incomplete);
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

                // a leg out of EUC-JP (on the way to stateless-ISO-2022-JP) names the character by its bytes
                var error = new UndefinedConversionError(EncodeStageSource == RubyEncoding.EUCJP
                    ? RubyExceptions.UndefinedConversionMessage(RubyExceptions.InspectTranscodingBytes(errorBytes), _path, _path.Length - 2)
                    : UndefinedConversionMessage(codepoint));
                error.SourceEncoding = EncodeStageSource;
                error.DestinationEncoding = EncodeStageDestination;
                error.ErrorCharBytes = errorBytes;
                _lastError = error;
            }

            /// <summary>An undefined conversion on the first leg, which MRI names by its bytes.</summary>
            private void RecordUndefinedSourceCharacter(byte[]/*!*/ errorBytes) {
                _status = "undefined_conversion";
                _errorBytes = errorBytes;
                _readAgainBytes = EmptyBytes;
                _errorAtEncodeStage = false;

                var error = new UndefinedConversionError(
                    RubyExceptions.UndefinedConversionMessage(RubyExceptions.InspectTranscodingBytes(errorBytes), _path, 0));
                error.SourceEncoding = DecodeStageSource;
                error.DestinationEncoding = DecodeStageDestination;
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
                    result.Add(MutableString.CreateBinary(Encoding.ASCII.GetBytes(RubyExceptions.TranscoderName(from))));
                    result.Add(MutableString.CreateBinary(Encoding.ASCII.GetBytes(RubyExceptions.TranscoderName(to))));
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
                if (EncodeStageDestination.IsDummy && str.Encoding != DestinationEncoding) {
                    // MRI hands the text to a stateful destination's encoder (in its source
                    // encoding), so it shares the encoder's shift state with the converted text
                    // around it rather than opening and closing one of its own.
                    string text = str.ToString();
                    var buffer = new byte[16 + EncodeStageDestination.MaxBytesPerChar * (text.Length + 4)];
                    int charsUsed, bytesUsed;
                    bool completed;
                    try {
                        // checked on a fresh encoder first, so that a refusal leaves the shift state alone
                        EncodeStageDestination.TranscodingEncoding.GetBytes(text);
                    } catch (EncoderFallbackException e) {
                        int codepoint = e.CharUnknownHigh != '\0' ?
                            Char.ConvertToUtf32(e.CharUnknownHigh, e.CharUnknownLow) : e.CharUnknown;
                        throw new UndefinedConversionError(UndefinedConversionMessage(codepoint));
                    }
                    _encoder.Convert(text.ToCharArray(), 0, text.Length, buffer, 0, buffer.Length, false,
                        out charsUsed, out bytesUsed, out completed);
                    AddInserted(Slice(buffer, 0, bytesUsed));
                    return;
                }
                if (str.Encoding == DestinationEncoding) {
                    bytes = str.ToByteArray();
                } else {
                    try {
                        bytes = DestinationEncoding.TranscodingEncoding.GetBytes(str.ToString());
                    } catch (EncoderFallbackException e) {
                        int codepoint = e.CharUnknownHigh != '\0' ?
                            Char.ConvertToUtf32(e.CharUnknownHigh, e.CharUnknownLow) : e.CharUnknown;
                        throw new UndefinedConversionError(UndefinedConversionMessage(codepoint));
                    }
                }
                // the inserted bytes start in the initial shift state (MRI's
                // rb_econv_insert_output finishes the last transcoder first)
                byte[] reset = TakeEncoderReset();
                AddInserted(Concat(reset, bytes));
            }

            /// <summary>
            /// Inserted text comes out in the order it was inserted, ahead of converted bytes still
            /// waiting for room in the destination (MRI's rb_econv_insert_output appends to the
            /// last transcoder's output buffer, in front of what the transcoders have yet to write).
            /// </summary>
            private void AddInserted(byte[]/*!*/ bytes) {
                _pendingOutput.InsertRange(_insertedCount, bytes);
                _insertedCount += bytes.Length;
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
                // MRI's econv_primitive_convert checks its arguments before it converts anything,
                // so a finished converter still refuses a negative size
                if (byteLimit.HasValue && byteLimit.Value < 0) {
                    throw RubyExceptions.CreateArgumentError("negative output_bytesize");
                }
                if (_finished) {
                    return "finished";
                }

                int limit = byteLimit.HasValue ? byteLimit.Value : Int32.MaxValue;
                var written = new List<byte>();

                byte[] sourceBytes = source != null ? source.ToByteArray() : EmptyBytes;
                int prefix = 0;
                byte[] input = sourceBytes;

                int consumed = 0;
                var chars = new char[16];
                string status = null;

                // The :xml => :attr decorator's opening quote goes in front of text inserted
                // with #insert_output as well.
                if (_pendingOutput.Count != 0 && NeedsOpeningQuote) {
                    var quote = AsciiInDestination("\"");
                    _xmlQuoteOpened = true;
                    _pendingOutput.InsertRange(0, quote);
                    _insertedCount += quote.Length;
                }

                // whatever didn't fit last time goes out first
                if (!Drain(written, limit)) {
                    status = "destination_buffer_full";
                } else {
                    // The read-again bytes join the input only now: a call that cannot even drain
                    // what it owes from the last one reads nothing, and they wait for the next.
                    prefix = _putback.Count;
                    if (prefix != 0) {
                        input = new byte[prefix + sourceBytes.Length];
                        _putback.CopyTo(input, 0);
                        Array.Copy(sourceBytes, 0, input, prefix, sourceBytes.Length);
                        _putback.Clear();
                    }

                    _composerOutputEnd = -1;

                    if (_pendingChars.Length != 0) {
                        char[] pending = _pendingChars.ToCharArray();
                        _pendingChars = String.Empty;
                        status = EncodeChars(pending, pending.Length, written, limit);
                    }

                    // characters the composer let go of last time come before any new input
                    if (status == null && _composedPending.Length != 0) {
                        status = EncodeComposed(String.Empty, written, limit);
                    }

                    while (status == null) {
                        if (consumed >= input.Length) {
                            if (_incomplete.Count != 0 && (flags & PartialInputFlag) == 0 &&
                                (_flags & InvalidMaskFlag) == InvalidReplaceFlag) {
                                // A character cut short by the end of the input is an invalid byte
                                // sequence like any other, so :invalid => :replace covers it too
                                // and the conversion finishes rather than stopping on it.
                                ResetDecoder();
                                // a full destination parks what the composer held, and the
                                // replacement after it, rather than leaving the replacement out
                                string flushStatus = FlushComposer(written, limit);
                                if (flushStatus != null && flushStatus != "destination_buffer_full") {
                                    status = flushStatus;
                                    break;
                                }
                                if (!EmitInserted(_replacementBytes, written, limit) || flushStatus != null || !EmitFinish(written, limit)) {
                                    status = "destination_buffer_full";
                                    break;
                                }
                                status = "finished";
                            } else if (_incomplete.Count != 0 && (flags & PartialInputFlag) == 0) {
                                var errorBytes = _incomplete.ToArray();
                                ResetDecoder();
                                RecordInvalidByteSequence(errorBytes, EmptyBytes, true);
                                status = "incomplete_input";
                            } else if ((flags & PartialInputFlag) != 0) {
                                status = "source_buffer_empty";
                            } else if (_composer != null && _path.Length == 2 && written.Count >= limit &&
                                _composerOutputEnd == written.Count) {
                                // MRI's from_utf8_mac has a finish to run, which a destination its
                                // own output filled leaves for the next call
                                status = "destination_buffer_full";
                            } else if ((status = FlushComposer(written, limit)) != null) {
                                // the character the composer held could not be written
                            } else if (!EmitFinish(written, limit)) {
                                status = "destination_buffer_full";
                            } else {
                                status = "finished";
                            }
                            break;
                        }

                        // MRI's no-conversion converter copies straight from the source buffer
                        // into the destination, so a full destination leaves the rest unread
                        // rather than parked in the converter.
                        if (_noConversion && written.Count >= limit) {
                            status = "destination_buffer_full";
                            break;
                        }

                        if (IsWideSource) {
                            string wideStatus = ConvertWideByte(input[consumed++], chars, written, limit, flags);
                            if (wideStatus != null) {
                                status = wideStatus;
                                break;
                            }
                            continue;
                        }

                        int unit = Math.Min(_sourceUnitSize, input.Length - consumed);
                        int bytesUsed, charsUsed;
                        bool completed;
                        try {
                            _decoder.Convert(input, consumed, unit, chars, 0, chars.Length, false,
                                out bytesUsed, out charsUsed, out completed);
                        } catch (DecoderFallbackException) {
                            // A character of the right shape that the table leaves out (0x81 in
                            // Windows-1252, 0xA2AF in EUC-JP) is an undefined conversion on the
                            // first leg, not an invalid byte sequence.
                            var character = new byte[_incomplete.Count + unit];
                            _incomplete.CopyTo(character, 0);
                            Array.Copy(input, consumed, character, _incomplete.Count, unit);
                            if (!MutableString.CreateBinary(character, DecodeStageSource).ContainsInvalidCharacters()) {
                                consumed += unit;
                                ResetDecoder();
                                if ((_flags & UndefMaskFlag) == UndefReplaceFlag) {
                                    if (!Emit(_replacementBytes, written, limit)) {
                                        status = "destination_buffer_full";
                                        break;
                                    }
                                    continue;
                                }
                                RecordUndefinedSourceCharacter(character);
                                status = "undefined_conversion";
                                break;
                            }

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
                                // The read-again bytes were never part of the error, only proof
                                // that it was one; they go back into the input rather than into
                                // the putback buffer, because the conversion carries straight on
                                // and they may well start a character of their own.
                                consumed -= readAgain.Length;
                                // (as above: the replacement is parked after the held character)
                                string flushStatus = FlushComposer(written, limit);
                                if (flushStatus != null && flushStatus != "destination_buffer_full") {
                                    status = flushStatus;
                                    break;
                                }
                                if (!Emit(_replacementBytes, written, limit) || flushStatus != null) {
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

                        // ASCII-8BIT's bytes above 0x7F are characters that mean nothing in any
                        // other encoding: MRI's first leg out of it refuses them as undefined.
                        if (DecodeStageSource == RubyEncoding.Binary && !_noConversion && chars[0] > 0x7f) {
                            if ((_flags & UndefMaskFlag) == UndefReplaceFlag) {
                                if (!Emit(_replacementBytes, written, limit)) {
                                    status = "destination_buffer_full";
                                    break;
                                }
                                continue;
                            }
                            RecordUndefinedSourceCharacter(new[] { (byte)chars[0] });
                            status = "undefined_conversion";
                            break;
                        }

                        string encodeStatus;
                        if (_composer != null) {
                            encodeStatus = EncodeComposed(_composer.Push(chars, charsUsed), written, limit);
                        } else {
                            encodeStatus = EncodeChars(chars, charsUsed, written, limit);
                        }
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
                // a conversion that has seen the end of its input is over: #convert refuses to go
                // on, and #finish has nothing left to give
                if (status == "finished") {
                    _finished = true;
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
                var converted = written.ToArray();
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

                    if (NewlinesInCharacters) {
                        bool consumed;
                        string newline = TranslateNewline(codepoint, out consumed);
                        if (newline.Length != 0 && !Emit(AsciiInDestination(newline), written, limit)) {
                            if (consumed) {
                                i += size;
                            }
                            // the rest of the characters are still to be converted, by the next call
                            _pendingChars = new string(chars, i, count - i) + _pendingChars;
                            return "destination_buffer_full";
                        }
                        if (consumed) {
                            i += size;
                            continue;
                        }
                    }

                    string escape = XmlEscape(codepoint);
                    if (escape != null) {
                        if (!EmitInserted(AsciiInDestination(escape), written, limit)) {
                            return "destination_buffer_full";
                        }
                        i += size;
                        continue;
                    }

                    byte[] bytes;
                    try {
                        bytes = EncodeCodepoint(chars, i, size);
                    } catch (EncoderFallbackException) {
                        // the shift state the characters before this one left behind still has
                        // to be closed before anything inserted in its place
                        byte[] reset = TakeEncoderReset();
                        ResetEncoder();

                        int undefMode = _flags & UndefMaskFlag;
                        if (undefMode == UndefReplaceFlag) {
                            if (!Emit(Concat(reset, _replacementBytes), written, limit)) {
                                return "destination_buffer_full";
                            }
                            i += size;
                            continue;
                        }
                        if (undefMode == UndefHexCharRefFlag) {
                            string reference = "&#x" + codepoint.ToString("X", CultureInfo.InvariantCulture) + ";";
                            if (!Emit(Concat(reset, Encoding.ASCII.GetBytes(reference)), written, limit)) {
                                return "destination_buffer_full";
                            }
                            i += size;
                            continue;
                        }

                        // MRI stops with the shift sequence still owed rather than lost: the next
                        // call writes it before whatever it converts.
                        _pendingOutput.AddRange(reset);
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

            /// <summary>
            /// MRI runs the newline decorators on the UTF-8 side of a conversion into an
            /// encoding that is not ASCII compatible (UTF-16, UTF-32): there they see the
            /// characters, which is where they are applied here too, rather than to the bytes.
            /// </summary>
            private bool NewlinesInCharacters {
                get { return (_flags & NewlineMask) != 0 && !DestinationEncoding.IsAsciiIdentity && !_noConversion; }
            }

            /// <summary>
            /// What the newline decorator makes of a character: the ASCII text to write for it,
            /// and whether that text replaces it (<paramref name="consumed"/>) or merely goes in
            /// front of it - the LF a CR held back by universal_newline turns into, once the
            /// character after it shows that it was no CRLF.
            /// </summary>
            private string/*!*/ TranslateNewline(int codepoint, out bool consumed) {
                if ((_flags & (UniversalNewlineFlag | LfNewlineFlag)) != 0) {
                    string held = "";
                    if (_pendingCarriageReturn) {
                        _pendingCarriageReturn = false;
                        held = "\n";
                        if (codepoint == '\n') {
                            consumed = true;
                            return held;
                        }
                    }
                    if (codepoint == '\r') {
                        _pendingCarriageReturn = true;
                        consumed = true;
                        return held;
                    }
                    consumed = false;
                    return held;
                }
                if (codepoint == '\n') {
                    consumed = true;
                    return ((_flags & CrlfNewlineFlag) != 0) ? "\r\n" : "\r";
                }
                consumed = false;
                return "";
            }

            private bool IsWideSource {
                get {
                    switch (DecodeStageSource.CodePage) {
                        case RubyEncoding.CodePageUTF16LE:
                        case RubyEncoding.CodePageUTF16BE:
                        case RubyEncoding.CodePageUTF32LE:
                        case RubyEncoding.CodePageUTF32BE:
                            return !_noConversion;
                        default:
                            return false;
                    }
                }
            }

            /// <summary>
            /// MRI's from_UTF_16BE/LE and from_UTF_32BE/LE transcoders, a byte at a time: answers
            /// 0 while a character is still being read, the number of chars written to
            /// <paramref name="chars"/> for a complete one, and -1 for an invalid sequence, with the
            /// bytes in error and those to be read again. A lone low surrogate is one invalid
            /// unit; a high surrogate followed by something other than a low one fails
            /// on the first byte that rules it out (UTF-16BE) or on the unit (UTF-16LE), and the
            /// bytes after the high surrogate are read again.
            /// </summary>
            private int DecodeWideByte(byte b, char[]/*!*/ chars, out byte[] errorBytes, out byte[] readAgain) {
                errorBytes = readAgain = null;
                _incomplete.Add(b);
                var p = _incomplete;
                int n = p.Count;
                int cp = DecodeStageSource.CodePage;
                if (cp == RubyEncoding.CodePageUTF32LE || cp == RubyEncoding.CodePageUTF32BE) {
                    if (n < 4) {
                        return 0;
                    }
                    long value = (cp == RubyEncoding.CodePageUTF32BE)
                        ? ((long)p[0] << 24) | ((long)p[1] << 16) | ((long)p[2] << 8) | p[3]
                        : ((long)p[3] << 24) | ((long)p[2] << 16) | ((long)p[1] << 8) | p[0];
                    if (value > 0x10ffff || value >= 0xd800 && value <= 0xdfff) {
                        errorBytes = p.ToArray();
                        readAgain = EmptyBytes;
                        p.Clear();
                        return -1;
                    }
                    p.Clear();
                    string s = Char.ConvertFromUtf32((int)value);
                    s.CopyTo(0, chars, 0, s.Length);
                    return s.Length;
                }

                bool big = cp == RubyEncoding.CodePageUTF16BE;
                Func<int, int> unitAt = i => big ? (p[i] << 8) | p[i + 1] : (p[i + 1] << 8) | p[i];
                switch (n) {
                    case 1:
                        return 0;

                    case 2: {
                            int u = unitAt(0);
                            if (u >= 0xdc00 && u <= 0xdfff) {
                                errorBytes = p.ToArray();
                                readAgain = EmptyBytes;
                                p.Clear();
                                return -1;
                            }
                            if (u >= 0xd800 && u <= 0xdbff) {
                                return 0;
                            }
                            p.Clear();
                            chars[0] = (char)u;
                            return 1;
                        }

                    case 3:
                        if (big && (b < 0xdc || b > 0xdf)) {
                            errorBytes = new[] { p[0], p[1] };
                            readAgain = new[] { p[2] };
                            p.Clear();
                            return -1;
                        }
                        return 0;

                    default: {
                            int u2 = unitAt(2);
                            if (u2 < 0xdc00 || u2 > 0xdfff) {
                                errorBytes = new[] { p[0], p[1] };
                                readAgain = new[] { p[2], p[3] };
                                p.Clear();
                                return -1;
                            }
                            chars[0] = (char)unitAt(0);
                            chars[1] = (char)u2;
                            p.Clear();
                            return 2;
                        }
                }
            }

            /// <summary>
            /// One byte of a UTF-16/32 source through the conversion. Returns null to go on, or the
            /// status that ends the call.
            /// </summary>
            private string ConvertWideByte(byte b, char[]/*!*/ chars, List<byte>/*!*/ written, int limit, int flags) {
                byte[] errorBytes, readAgain;
                int produced = DecodeWideByte(b, chars, out errorBytes, out readAgain);
                if (produced > 0) {
                    string encodeStatus = EncodeChars(chars, produced, written, limit);
                    if (encodeStatus != null) {
                        return encodeStatus;
                    }
                    if ((flags & AfterOutputFlag) != 0 && written.Count > 0) {
                        return "after_output";
                    }
                    return null;
                }
                if (produced == 0) {
                    return null;
                }

                if ((_flags & InvalidMaskFlag) == InvalidReplaceFlag) {
                    // The bytes after the error start over. Those this call does not get to are
                    // already gone from the input, so they wait in the read-again buffer, which
                    // the next call reads first (MRI keeps them in the transcoder's readagain).
                    if (!Emit(_replacementBytes, written, limit)) {
                        _putback.AddRange(readAgain);
                        return "destination_buffer_full";
                    }
                    for (int i = 0; i < readAgain.Length; i++) {
                        string againStatus = ConvertWideByte(readAgain[i], chars, written, limit, flags);
                        if (againStatus != null) {
                            _putback.AddRange(Slice(readAgain, i + 1, readAgain.Length - i - 1));
                            return againStatus;
                        }
                    }
                    return null;
                }

                _putback.Clear();
                _putback.AddRange(readAgain);
                RecordInvalidByteSequence(errorBytes, readAgain, false);
                return "invalid_byte_sequence";
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
                    return EncodeStageSource.TranscodingEncoding.GetBytes(chars, index, size);
                } catch (EncoderFallbackException) {
                    return Encoding.UTF8.GetBytes(new String(chars, index, size));
                }
            }

            /// <summary>
            /// Writes what the encoder owes to return a stateful (dummy) destination such as
            /// ISO-2022-JP to its initial state - the ESC ( B that has to come before ASCII the
            /// converter inserts itself (a replacement, a character reference, an XML escape) and
            /// at the end of the conversion. MRI's transcoders do the same (iso2022jp_encoder's
            /// finish, and the reset sequence rb_econv_insert_output asks for).
            /// </summary>
            private bool EmitEncoderReset(List<byte>/*!*/ written, int limit) {
                byte[] reset = TakeEncoderReset();
                return reset.Length == 0 || Emit(reset, written, limit);
            }

            /// <summary>Flushes the encoder, returning the bytes that bring it back to its initial state.</summary>
            private byte[]/*!*/ TakeEncoderReset() {
                if (!EncodeStageDestination.IsDummy) {
                    return EmptyBytes;
                }
                var buffer = new byte[64];
                int charsUsed, bytesUsed = 0;
                bool completed;
                try {
                    _encoder.Convert(EmptyChars, 0, 0, buffer, 0, buffer.Length, true, out charsUsed, out bytesUsed, out completed);
                } catch (ArgumentException) {
                    // the Encoder an Encoding gets by default throws "Conversion buffer overflow"
                    // when asked to convert nothing, having nothing to flush (an
                    // EncoderFallbackException is an ArgumentException too)
                    bytesUsed = 0;
                }
                return Slice(buffer, 0, bytesUsed);
            }

            /// <summary>Emits bytes the converter inserts itself, in the initial shift state.</summary>
            private bool EmitInserted(byte[]/*!*/ bytes, List<byte>/*!*/ written, int limit) {
                return Emit(Concat(TakeEncoderReset(), bytes), written, limit);
            }

            /// <summary>
            /// Converted bytes through the decorators and into the output. The decorated bytes
            /// count against the destination limit, as MRI's decorators are transcoders of their
            /// own with output buffers like any other.
            /// </summary>
            private bool Emit(byte[]/*!*/ bytes, List<byte>/*!*/ written, int limit) {
                return Put(ApplyOutputDecorators(bytes), written, limit);
            }

            /// <summary>
            /// Moves <paramref name="bytes"/> into the output as they are, parking the tail in
            /// _pendingOutput if the destination limit is reached. Returns false if the buffer
            /// filled up.
            /// </summary>
            private bool Put(byte[]/*!*/ bytes, List<byte>/*!*/ written, int limit) {
                int i = 0;
                while (i < bytes.Length && written.Count < limit) {
                    written.Add(bytes[i++]);
                }
                if (i < bytes.Length) {
                    _pendingOutput.AddRange(Slice(bytes, i, bytes.Length - i));
                    return false;
                }
                return true;
            }

            private bool Drain(List<byte>/*!*/ written, int limit) {
                int i = 0;
                while (i < _pendingOutput.Count && written.Count < limit) {
                    written.Add(_pendingOutput[i++]);
                }
                if (i > 0) {
                    _pendingOutput.RemoveRange(0, i);
                    _insertedCount = Math.Max(_insertedCount - i, 0);
                }
                return _pendingOutput.Count == 0;
            }

            /// <summary>
            /// What a conversion owes the output once its input is exhausted: the newline a held CR
            /// turns into, the sequence that returns a stateful destination to its initial state,
            /// and the :xml => :attr decorator's closing quote (both quotes, if nothing came before
            /// it). Each counts against the destination limit; what does not fit is parked and
            /// the next call goes on from there.
            /// </summary>
            private bool EmitFinish(List<byte>/*!*/ written, int limit) {
                if (_pendingCarriageReturn) {
                    // a CR at the very end has nothing left to pair with: it is a newline
                    _pendingCarriageReturn = false;
                    if (!Put(AsciiInDestination("\n"), written, limit)) {
                        return false;
                    }
                }
                if (!EmitEncoderReset(written, limit)) {
                    return false;
                }
                if ((_flags & XmlAttrQuoteFlag) != 0 && !_decoratorsFinished) {
                    _decoratorsFinished = true;
                    byte[] quotes = AsciiInDestination(_xmlQuoteOpened ? "\"" : "\"\"");
                    _xmlQuoteOpened = true;
                    if (!Put(quotes, written, limit)) {
                        return false;
                    }
                }
                return true;
            }

            private static byte[]/*!*/ Concat(byte[]/*!*/ first, byte[]/*!*/ second) {
                if (first.Length == 0) {
                    return second;
                }
                var result = new byte[first.Length + second.Length];
                first.CopyTo(result, 0);
                second.CopyTo(result, first.Length);
                return result;
            }

            private static byte[]/*!*/ Slice(byte[]/*!*/ array, int start, int count) {
                if (count <= 0) {
                    return EmptyBytes;
                }
                var result = new byte[count];
                Array.Copy(array, start, result, 0, count);
                return result;
            }

            // Writes out the character the UTF8-MAC composer holds, if there is one: at the end of
            // the input, and before a replacement, which comes after it in MRI's single converter.
            private string FlushComposer(List<byte>/*!*/ written, int limit) {
                return (_composer != null) ? EncodeComposed(_composer.Flush(), written, limit) : null;
            }

            // The composer can let go of more than one character at a time, whose source bytes are
            // all read by then. They are converted one by one: one that does not fit is parked
            // (Put), and those after it wait in _composedPending, so that a full destination is
            // reported before anything a later character would raise, as MRI does; a character
            // that cannot be converted is reported even when the destination is just full.
            private string EncodeComposed(string/*!*/ text, List<byte>/*!*/ written, int limit) {
                text = _composedPending + text;
                _composedPending = String.Empty;
                int i = 0;
                while (i < text.Length) {
                    int size = (Char.IsHighSurrogate(text[i]) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1])) ? 2 : 1;
                    var converted = new List<byte>();
                    string status = EncodeChars(text.ToCharArray(i, size), size, converted, Int32.MaxValue);
                    bool fits = Put(converted.ToArray(), written, limit);
                    _composerOutputEnd = written.Count;
                    i += size;
                    if (status != null) {
                        _composedPending = text.Substring(i);
                        return status;
                    }
                    if (!fits) {
                        _composedPending = text.Substring(i);
                        return "destination_buffer_full";
                    }
                }
                return null;
            }

            private void ResetDecoder() {
                _incomplete.Clear();
                _decoder = DecodeStageSource.TranscodingEncoding.GetDecoder();
            }

            private void ResetEncoder() {
                _encoder = EncodeStageDestination.TranscodingEncoding.GetEncoder();
            }

            /// <summary>
            /// The decorators all work on the converted bytes rather than on characters. They are
            /// only meaningful for ASCII-compatible destinations; anything else passes through
            /// unchanged.
            /// </summary>
            private byte[]/*!*/ ApplyOutputDecorators(byte[]/*!*/ bytes) {
                return ApplyNewlineDecorators(ApplyXmlDecorators(bytes));
            }

            /// <summary>
            /// The :xml => :attr decorator supplies the quotes around the attribute value. The
            /// opening one goes in front of the first byte the converter ever produces, so a
            /// converter finished without ever having converted anything writes both at once.
            /// </summary>
            private byte[]/*!*/ ApplyXmlDecorators(byte[]/*!*/ bytes) {
                if (bytes.Length == 0 || !NeedsOpeningQuote) {
                    return bytes;
                }

                _xmlQuoteOpened = true;
                return Concat(AsciiInDestination("\""), bytes);
            }

            private bool NeedsOpeningQuote {
                get { return (_flags & XmlAttrQuoteFlag) != 0 && !_xmlQuoteOpened; }
            }

            /// <summary>
            /// ASCII text the decorators write, in the destination encoding: MRI's escapers run on
            /// the UTF-8 side of the conversion, so a UTF-16 destination gets "\0\"" and a
            /// stateful one the ASCII bytes (in its initial state).
            /// </summary>
            private byte[]/*!*/ AsciiInDestination(string/*!*/ text) {
                if (DestinationEncoding.IsAsciiIdentity || DestinationEncoding.IsDummy) {
                    return Encoding.ASCII.GetBytes(text);
                }
                return DestinationEncoding.TranscodingEncoding.GetBytes(text);
            }

            /// <summary>
            /// The escape a character needs before it can go into a text node or an attribute
            /// value, or null if it can be converted as it stands. This runs on the characters
            /// rather than on the converted bytes so that the ampersand of a numeric character
            /// reference the converter itself produced is not escaped a second time.
            /// </summary>
            private string XmlEscape(int codepoint) {
                if ((_flags & (XmlTextFlag | XmlAttrContentFlag)) == 0) {
                    return ((_flags & AmpEscapeFlag) != 0 && codepoint == '&') ? "&amp;" : null;
                }
                bool attribute = (_flags & XmlAttrContentFlag) != 0;
                switch (codepoint) {
                    case '&': return "&amp;";
                    case '<': return "&lt;";
                    case '>': return "&gt;";
                    case '"': return attribute ? "&quot;" : null;
                    case '\'': return attribute ? "&apos;" : null;
                    default: return null;
                }
            }

            private byte[]/*!*/ ApplyNewlineDecorators(byte[]/*!*/ bytes) {
                if ((_flags & NewlineMask) == 0 || !DestinationEncoding.IsAsciiIdentity) {
                    return bytes;
                }

                var result = new List<byte>(bytes.Length + 8);
                foreach (byte b in bytes) {
                    if ((_flags & (UniversalNewlineFlag | LfNewlineFlag)) != 0) {
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

                _finished = true;
                return destination;
            }

            #endregion

            #region Argument coercion

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
            private static int ParseOptions(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
                object options, out object replacement) {
                replacement = null;
                if (options == null || options == Missing.Value) {
                    return 0;
                }

                if (options is int) {
                    return (int)options;
                }

                var hash = options as IDictionary<object, object>;
                if (hash == null) {
                    // CRuby's ** applies #to_hash at the call site; do it here so that objects
                    // that only respond to #to_hash are accepted.
                    var site = toHash.GetSite(TryConvertToHashAction.Make(toHash.Context));
                    hash = site.Target(site, options);
                }
                if (hash == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion into Hash");
                }

                int flags = 0;
                int newlineOption = -1;
                foreach (var entry in hash) {
                    switch (KeyName(entry.Key)) {
                        case "invalid":
                            if (SymbolName(entry.Value) == "replace") {
                                flags |= InvalidReplaceFlag;
                            } else if (entry.Value != null) {
                                throw RubyExceptions.CreateArgumentError("unknown value for invalid character option");
                            }
                            break;

                        case "undef":
                            if (SymbolName(entry.Value) == "replace") {
                                flags |= UndefReplaceFlag;
                            } else if (entry.Value != null) {
                                throw RubyExceptions.CreateArgumentError("unknown value for undefined character option");
                            }
                            break;

                        case "replace":
                            replacement = entry.Value ?? DefaultReplacementMarker;
                            break;

                        case "xml":
                            // :xml also decides what happens to a character the destination
                            // cannot hold: it becomes a numeric character reference, which is
                            // always representable, so :xml never raises on undefined input.
                            if (SymbolName(entry.Value) == "text") {
                                flags |= XmlTextFlag | UndefHexCharRefFlag;
                            } else if (SymbolName(entry.Value) == "attr") {
                                flags |= XmlAttrContentFlag | XmlAttrQuoteFlag | UndefHexCharRefFlag;
                            } else {
                                throw RubyExceptions.CreateArgumentError("unexpected value for xml option: {0}",
                                    (object)SymbolName(entry.Value) ?? toHash.Context.Inspect(entry.Value));
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

                        case "lf_newline":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= LfNewlineFlag;
                            }
                            break;

                        case "newline":
                            switch (SymbolName(entry.Value)) {
                                case "universal": newlineOption = UniversalNewlineFlag; break;
                                case "crlf": newlineOption = CrlfNewlineFlag; break;
                                case "cr": newlineOption = CrNewlineFlag; break;
                                case "lf": newlineOption = LfNewlineFlag; break;
                                default:
                                    throw RubyExceptions.CreateArgumentError("unexpected value for newline option: {0}",
                                        (object)SymbolName(entry.Value) ?? toHash.Context.Inspect(entry.Value));
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

                // MRI's econv_opts: newline: wins over the *_newline: flags, with a warning
                if (newlineOption >= 0) {
                    const int newlineMask = NewlineMask;
                    if ((flags & newlineMask) != 0) {
                        toHash.Context.ReportWarning(":newline option precedes other newline options", true);
                    }
                    flags = (flags & ~newlineMask) | newlineOption;
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
            public static RubyConverter/*!*/ Create(ConversionStorage<MutableString>/*!*/ toStr,
                ConversionStorage<IDictionary<object, object>>/*!*/ toHash, RubyClass/*!*/ self,
                object source, [Optional]object destination, [Optional]object options) {

                if (destination == Missing.Value) {
                    var convpath = source as IList<object>;
                    if (convpath == null) {
                        throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 1, expected 2..3)");
                    }
                    return CreateFromConvPath(toStr, convpath);
                }

                object replacement;
                int flags = ParseOptions(toHash, options, out replacement);

                // Encoding::Converter.new("", "amp_escape"): a decorator asked for by name, which
                // MRI finds as a transcoder from "" to it
                int decorator;
                var sourceString = source as MutableString;
                var destinationString = destination as MutableString;
                if (sourceString != null && sourceString.IsEmpty && destinationString != null &&
                    TryGetDecoratorFlag(destinationString.ToString(), out decorator)) {
                    var named = new RubyConverter(null, flags | decorator);
                    string name = destinationString.ToString();
                    named._explicitConvPath = new RubyArray { MutableString.CreateBinary(Encoding.ASCII.GetBytes(name)).Freeze() };
                    named._explicitDescription = name;
                    return named;
                }

                RequireOneNewlineDecorator(toStr, source, destination, flags);
                var path = ResolvePath(toStr, source, destination, flags, true);

                var result = new RubyConverter(path, flags);
                if (replacement != null && replacement != DefaultReplacementMarker) {
                    result.SetReplacement(ToReplacementString(toStr, replacement));
                }
                return result;
            }

            private static bool TryGetDecoratorFlag(string/*!*/ name, out int flag) {
                switch (name) {
                    case "amp_escape": flag = AmpEscapeFlag; return true;
                    case "xml_text_escape": flag = XmlTextFlag; return true;
                    case "xml_attr_content_escape": flag = XmlAttrContentFlag; return true;
                    case "xml_attr_quote": flag = XmlAttrQuoteFlag; return true;
                    case "universal_newline": flag = UniversalNewlineFlag; return true;
                    case "crlf_newline": flag = CrlfNewlineFlag; return true;
                    case "cr_newline": flag = CrNewlineFlag; return true;
                    case "lf_newline": flag = LfNewlineFlag; return true;
                    default: flag = 0; return false;
                }
            }

            /// <summary>
            /// Encoding::Converter.new(convpath): the conversion spelled out as MRI's #convpath
            /// answers it, a list of [source, destination] pairs and decorator names. The pairs
            /// are chained (each one searched for as Encoding::Converter.new would) and the
            /// decorators added. #convpath gives the path back as it was asked for; the
            /// decorators run where the options would put them, wherever they were listed.
            /// </summary>
            private static RubyConverter/*!*/ CreateFromConvPath(ConversionStorage<MutableString>/*!*/ toStr, IList<object>/*!*/ convpath) {
                var path = new List<RubyEncoding>();
                var reported = new RubyArray();
                int flags = 0;
                foreach (object item in convpath) {
                    var pair = item as IList<object>;
                    if (pair != null) {
                        if (pair.Count != 2) {
                            throw RubyExceptions.CreateArgumentError("not a 2-element array in convpath");
                        }
                        string sourceName = EncodingName(toStr, pair[0]);
                        string destinationName = EncodingName(toStr, pair[1]);
                        RubyEncoding from = FindEncoding(toStr.Context, pair[0], sourceName);
                        RubyEncoding to = FindEncoding(toStr.Context, pair[1], destinationName);
                        if (from == null || to == null || from == to || from.HasNoTranscoder || to.HasNoTranscoder) {
                            throw RubyExceptions.CreateArgumentError("adding conversion failed: {0} to {1}", sourceName, destinationName);
                        }
                        RubyEncoding[] leg = RubyExceptions.ConversionPath(from, to);
                        if (path.Count != 0 && path[path.Count - 1] != leg[0]) {
                            throw RubyExceptions.CreateArgumentError("adding conversion failed: {0} to {1}", sourceName, destinationName);
                        }
                        for (int i = (path.Count == 0) ? 0 : 1; i < leg.Length; i++) {
                            path.Add(leg[i]);
                        }
                        for (int i = 0; i < leg.Length - 1; i++) {
                            reported.Add(new RubyArray { leg[i], leg[i + 1] });
                        }
                        continue;
                    }

                    string name = Protocols.CastToString(toStr, item).ToString();
                    int decorator;
                    if (!TryGetDecoratorFlag(name, out decorator)) {
                        throw RubyExceptions.CreateArgumentError("decoration failed: {0}", name);
                    }
                    flags |= decorator;
                    reported.Add(MutableString.CreateBinary(Encoding.ASCII.GetBytes(name)).Freeze());
                }

                var result = new RubyConverter(path.Count != 0 ? path.ToArray() : null, flags);
                result._explicitConvPath = reported;
                // MRI's #inspect names the encodings of such a converter but none of its decorators
                result._explicitDescription = (path.Count != 0) ? Describe(path[0].Name, path[path.Count - 1].Name, 0) : "no-conversion";
                return result;
            }

            [RubyMethod("search_convpath", RubyMethodAttributes.PublicSingleton)]
            public static RubyArray/*!*/ SearchConvPath(ConversionStorage<MutableString>/*!*/ toStr,
                ConversionStorage<IDictionary<object, object>>/*!*/ toHash, RubyClass/*!*/ self,
                object source, object destination, [Optional]object options) {

                object replacement;
                int flags = ParseOptions(toHash, options, out replacement);
                RequireOneNewlineDecorator(toStr, source, destination, flags);
                var path = ResolvePath(toStr, source, destination, flags, false);
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

                // MRI answers from its transcoder tables: the encoding a converter out of an
                // ASCII incompatible one produces as its first step.
                switch (enc.CodePage) {
                    case RubyEncoding.CodePageUTF16BE:
                    case RubyEncoding.CodePageUTF16LE:
                    case RubyEncoding.CodePageUTF32BE:
                    case RubyEncoding.CodePageUTF32LE:
                    case RubyEncoding.CodePageUTF16:
                    case RubyEncoding.CodePageUTF32:
                    case RubyEncoding.CodePageCESU8:
                        return RubyEncoding.UTF8;

                    case 37: // IBM037
                        return RubyEncoding.GetRubyEncoding(28591);

                    case 50220: // ISO-2022-JP
                        return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageStatelessISO2022JP);

                    case RubyEncoding.CodePageISO2022JPKDDI:
                        return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageStatelessISO2022JPKDDI);

                    case RubyEncoding.CodePageCP50220:
                    case 50221: // CP50221
                        return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageCP51932);

                    default:
                        return null;
                }
            }

            [RubyMethod("source_encoding")]
            public static RubyEncoding GetSourceEncoding(RubyConverter/*!*/ self) {
                return self.IsNoConversion ? null : self.SourceEncoding;
            }

            [RubyMethod("destination_encoding")]
            public static RubyEncoding GetDestinationEncoding(RubyConverter/*!*/ self) {
                return self.IsNoConversion ? null : self.DestinationEncoding;
            }

            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(RubyConverter/*!*/ self) {
                string description = self._explicitDescription ?? (self.IsNoConversion ? Describe("", "", self._flags) :
                    Describe(self.SourceEncoding.Name, self.DestinationEncoding.Name, self._flags));
                return MutableString.CreateBinary(Encoding.ASCII.GetBytes("#<Encoding::Converter: " + description + ">"));
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
                ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
                RubyContext/*!*/ context,
                RubyConverter/*!*/ self,
                [DefaultProtocol]MutableString source,
                [DefaultProtocol, NotNull]MutableString/*!*/ destination,
                [Optional]object byteOffset,
                [Optional]object byteLimit,
                [Optional]object options) {

                // MRI's econv_primitive_convert reads the source and writes the destination in
                // place, so the two cannot be one string
                if (ReferenceEquals(source, destination)) {
                    throw RubyExceptions.CreateArgumentError("cannot convert into the source string");
                }

                int? offset = ToOptionalInt(toInt, byteOffset);
                int? limit = ToOptionalInt(toInt, byteLimit);
                object replacement;
                int flags = ParseOptions(toHash, options, out replacement);

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
