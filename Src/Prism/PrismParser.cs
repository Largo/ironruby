using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IronRuby.Prism {
    /// <summary>
    /// Managed front end over libprism. The primary path is Parse: prism's compact
    /// binary serialization decoded by the generated PrismLoader (same scheme as
    /// JRuby's generated Java loader). ParseToJson remains for debugging.
    /// </summary>
    public static class PrismParser {
        public static PrismParseResult/*!*/ Parse(string/*!*/ source, string path, int startLine, IList<string> outerLocals) {
            return Parse(source, path, startLine, outerLocals, 0);
        }

        public static PrismParseResult/*!*/ Parse(string/*!*/ source, string path, int startLine, IList<string> outerLocals,
            int frozenStringLiteral) {
            return Parse(source, path, startLine, outerLocals, frozenStringLiteral, Encoding.UTF8);
        }

        /// <summary>
        /// prism works on the bytes of the file, so it has to be given the bytes the file
        /// actually held. The source arrives here already decoded - with the encoding its magic
        /// comment asked for - and encoding it back with that same encoding reproduces them.
        /// Encoding it as UTF-8 instead would hand prism a doubled copy of every byte above
        /// 0x7F, which is a different file: "\xE3" in a binary source would reach it as
        /// "\xC3\xA3".
        /// </summary>
        public static PrismParseResult/*!*/ Parse(string/*!*/ source, string path, int startLine, IList<string> outerLocals,
            int frozenStringLiteral, Encoding/*!*/ sourceEncoding) {

            byte[] sourceBytes = sourceEncoding.GetBytes(source);
            PrismParseResult result = PrismLoader.LoadParse(
                ParseSerializedBytes(sourceBytes, BuildOptionsData(path, startLine, outerLocals, frozenStringLiteral)));
            result.DataOffset = ComputeDataOffset(result.DataLocation, sourceBytes);
            return result;
        }

        /// <summary>
        /// prism reports the __END__ token's own start offset; DATA is positioned after
        /// the token and its line terminator (CRuby: `offset = data_loc-&gt;start + 7` then
        /// skip \r\n). These are UTF-8 byte offsets, so the skip must index the byte
        /// array rather than the UTF-16 source string.
        /// </summary>
        private static int ComputeDataOffset(IronRuby.Prism.Ast.PmLocation? dataLocation, byte[]/*!*/ sourceBytes) {
            if (dataLocation == null) {
                return -1;
            }

            int offset = dataLocation.Value.Start + 7; // "__END__".Length
            if (offset < 0 || offset > sourceBytes.Length) {
                return -1;
            }
            if (offset < sourceBytes.Length && sourceBytes[offset] == (byte)'\r') offset++;
            if (offset < sourceBytes.Length && sourceBytes[offset] == (byte)'\n') offset++;
            return offset;
        }

        public static byte[]/*!*/ ParseSerialized(string/*!*/ source) {
            return ParseSerialized(source, null);
        }

        public static byte[]/*!*/ ParseSerialized(string/*!*/ source, byte[] optionsData) {
            return ParseSerializedBytes(Encoding.UTF8.GetBytes(source), optionsData);
        }

        private static byte[]/*!*/ ParseSerializedBytes(byte[]/*!*/ bytes, byte[] optionsData) {
            IntPtr buffer = IntPtr.Zero;
            GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            GCHandle options = optionsData != null ? GCHandle.Alloc(optionsData, GCHandleType.Pinned) : default;
            try {
                buffer = PrismNative.pm_buffer_new();
                PrismNative.pm_serialize_parse(buffer, pinned.AddrOfPinnedObject(), (nuint)bytes.Length,
                    optionsData != null ? options.AddrOfPinnedObject() : IntPtr.Zero);
                int length = checked((int)PrismNative.pm_buffer_length(buffer));
                byte[] result = new byte[length];
                Marshal.Copy(PrismNative.pm_buffer_value(buffer), result, 0, length);
                return result;
            } finally {
                if (buffer != IntPtr.Zero) PrismNative.pm_buffer_free(buffer);
                pinned.Free();
                if (optionsData != null) options.Free();
            }
        }

        /// <summary>
        /// Serializes pm_options_t as expected by pm_serialize_parse's data argument
        /// (see prism docs/serialization.md "APIs"). Passing outer locals as a scope
        /// makes prism resolve eval-context locals itself.
        /// </summary>
        internal static byte[]/*!*/ BuildOptionsData(string path, int startLine, IList<string> outerLocals) {
            return BuildOptionsData(path, startLine, outerLocals, 0);
        }

        internal static byte[]/*!*/ BuildOptionsData(string path, int startLine, IList<string> outerLocals, int frozenStringLiteral) {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream)) {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path ?? "");
                writer.Write(pathBytes.Length);
                writer.Write(pathBytes);
                writer.Write(startLine);
                writer.Write(0);            // encoding name length (default)
                // 1 enabled, -1 (0xff) disabled, 0 unset - a magic comment in the file wins
                // over either, which prism does itself.
                writer.Write((sbyte)frozenStringLiteral);
                writer.Write((byte)0);      // command line flags
                writer.Write((byte)0);      // syntax version (latest)
                writer.Write((byte)0);      // encoding locked
                writer.Write((byte)0);      // main script
                writer.Write((byte)0);      // partial script
                writer.Write((byte)0);      // freeze
                if (outerLocals != null) {
                    writer.Write(1);        // one scope
                    writer.Write(outerLocals.Count);
                    writer.Write((byte)0);  // forwarding flags
                    foreach (string local in outerLocals) {
                        byte[] localBytes = Encoding.UTF8.GetBytes(local);
                        writer.Write(localBytes.Length);
                        writer.Write(localBytes);
                    }
                } else {
                    writer.Write(0);        // no scopes
                }
                return stream.ToArray();
            }
        }

        public static string/*!*/ ParseToJson(string/*!*/ source) {
            byte[] bytes = Encoding.UTF8.GetBytes(source);
            IntPtr arena = IntPtr.Zero, parser = IntPtr.Zero, buffer = IntPtr.Zero;
            GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try {
                arena = PrismNative.pm_arena_new();
                parser = PrismNative.pm_parser_new(arena, pinned.AddrOfPinnedObject(), (nuint)bytes.Length, IntPtr.Zero);
                IntPtr node = PrismNative.pm_parse(parser);
                buffer = PrismNative.pm_buffer_new();
                PrismNative.pm_dump_json(buffer, parser, node);
                int length = checked((int)PrismNative.pm_buffer_length(buffer));
                unsafe {
                    return Encoding.UTF8.GetString((byte*)PrismNative.pm_buffer_value(buffer), length);
                }
            } finally {
                if (buffer != IntPtr.Zero) PrismNative.pm_buffer_free(buffer);
                if (parser != IntPtr.Zero) PrismNative.pm_parser_free(parser);
                if (arena != IntPtr.Zero) PrismNative.pm_arena_free(arena);
                pinned.Free();
            }
        }
    }
}
